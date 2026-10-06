using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Walks over gamegen script graphs and the Blender checkout folder follow everything a build supports, or refuse: the
/// missions running a shared script past 512 sourced files, FindNode lookups sourced more than eight levels deep, other
/// missions' builds that the build refuses, texture-effect previews, and checkout listings of many folders and files.
/// </summary>
public sealed class ScriptScanRound6Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Message(Action action) => Assert.Throws<InvalidDataException>(action).Message;

    /// <summary>Writes <paramref name="count"/> scripts that source each other in a line, the last sourcing <paramref name="end"/>; returns the first.</summary>
    private static string Chain(SourceWorldFixture fixture, string stem, int count, string end, string? last = null)
    {
        for (int i = 0; i < count; i++)
            fixture.Write($"gamegen/{stem}{i}.gw", i + 1 < count ? $"source {stem}{i + 1}.gw\r\n" : (last ?? "") + $"source {end}\r\n");
        return $"{stem}0.gw";
    }
    private static string Replace(SourceWorldFixture fixture, string relative, string text, string with)
    {
        string old = Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path(relative)));
        Assert.Contains(text, old, StringComparison.Ordinal);
        return old.Replace(text, with, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissionReachingASharedScriptPastFiveHundredTwelveFilesRunsIt()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/shared.gw", "NewObject3D marker\r\n");
        // m2 reaches shared.gw only through 600 scripts, a graph the build follows (each sources the next once).
        string first = Chain(fixture, "c", 600, "shared.gw");
        fixture.Write("gamegen/m2.gs", Replace(fixture, "gamegen/m2.gs", "GameZWriteZBDFile", $"source {first}\r\nGameZWriteZBDFile"));
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Equal(["m2"], SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token));
        // The mission being edited is never one of the others, and a script nobody else reaches runs in none.
        Assert.Empty(SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m2", Token));
        fixture.Write("gamegen/alone.gw", "NewObject3D alone\r\n");
        Assert.Empty(SourceObjectEdits.MissionsRunning(workspace, "gamegen/alone.gw", "m1", Token));
    }

    [Fact]
    public async Task AnEditOfAScriptAnotherMissionReachesFarAwayIsNotTakenAsThisMissionsAlone()
    {
        using SourceWorldFixture fixture = new();
        // Both missions make their world in world.gw; m2 sources it through 600 other scripts.
        fixture.Write("gamegen/m1.gs", Replace(fixture, "gamegen/m1.gs", "NewWorld %worldName%", "source world.gw"));
        fixture.Write("gamegen/m2.gs", Replace(fixture, "gamegen/m2.gs", "NewWorld %worldName%", "source " + Chain(fixture, "far", 600, "world.gw")));
        fixture.Write("gamegen/world.gw", "NewWorld %worldName%\r\n");
        SourceWorkspace workspace = new(fixture.Project);
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "p-" + Guid.NewGuid().ToString("N")), workspace.Overlay(), token: Token);
        Assert.Null(build.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        var origin = build.Provenance[GameZWriter.NodeSlots(world)[world.Nodes.Single(n => n.Class == WorldNodeClass.World)]];
        Assert.Equal("gamegen/world.gw", origin.Created!.Script);
        // Fog inserted after NewWorld in world.gw would reach m2 too: refused, as for a mission sourcing it directly.
        Assert.Contains("also runs in m2", Message(() => SourceObjectEdits.PlanCommand(workspace, "world", origin, build.Executions, "WorldSetFogDensity", ["0.25"], Token, "m1")));
    }

    [Fact]
    public void ScriptGraphsBeyondTheSharingBudgetAreRefusedNotTakenAsMissionLocal()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/shared.gw", "NewObject3D marker\r\n");
        fixture.Write("gamegen/m2.gs", Replace(fixture, "gamegen/m2.gs", "GameZWriteZBDFile", $"source {Chain(fixture, "c", 20, "shared.gw")}\r\nGameZWriteZBDFile"));
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Equal(["m2"], SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", 64, 64, Token));
        // More scripts, or more source lines, than the walk follows: refused, whether or not they would reach the script.
        Assert.Contains("more than 16 different scripts", Message(() => SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", 16, 64, Token)));
        Assert.Contains("more than 8 source lines", Message(() => SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", 64, 8, Token)));
        // Every script is read once however many lines source it: repeated lines cost source lines, not scripts.
        fixture.Write("gamegen/c19.gw", string.Concat(Enumerable.Repeat("source shared.gw\r\n", 40)));
        Assert.Equal(["m2"], SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", 22, 64, Token));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", canceled.Token));
    }

    [Fact]
    public void AnotherMissionSourcingAScriptAMacroNamesMayRunTheEditedOne()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/shared.gw", "NewObject3D marker\r\n");
        fixture.Write("gamegen/m2.gs", Replace(fixture, "gamegen/m2.gs", "GameZWriteZBDFile", "set part shared\r\nsource %part%.gw\r\nGameZWriteZBDFile"));
        SourceWorkspace workspace = new(fixture.Project);
        // Only m2's build decides what %part% names: the edit cannot be taken as m1's alone.
        string message = Message(() => SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token));
        Assert.Contains("may also run in m2", message);
        Assert.Contains("gamegen/m2.gs line", message);
        // The edited mission's own macro sources do not matter, and a single % names no macro.
        Assert.Empty(SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m2", Token));
        fixture.Write("gamegen/m2.gs", Replace(fixture, "gamegen/m2.gs", "source %part%.gw", "source 100%.gw"));
        Assert.Empty(SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token));
        // A mission that reaches the script anyway runs it, whatever else its macros name.
        fixture.Write("gamegen/m2.gs", Replace(fixture, "gamegen/m2.gs", "source 100%.gw", "source %part%.gw\r\nsource shared.gw"));
        Assert.Equal(["m2"], SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token));
    }

    [Fact]
    public void FindNodesFollowsSourcesAsDeepAsTheBuildAndRefusesDeeper()
    {
        Dictionary<string, string> scripts = new(StringComparer.OrdinalIgnoreCase);
        void Nest(int depth, string tail)
        {
            scripts.Clear();
            for (int i = 0; i < depth; i++) scripts[i == 0 ? WorldLookups.LoadScript("m1") : $"gamegen/d{i}.gw"] = $"source d{i + 1}.gw\r\n";
            scripts[$"gamegen/d{depth}.gw"] = tail;
        }
        IReadOnlyList<(string Source, string Name)> Find() => WorldLookups.FindNodes(p => scripts.TryGetValue(p, out var text) ? Encoding.Latin1.GetBytes(text) : null, "m1", Token);
        // Twelve levels down, and as deep as the build follows (32): the lookup is found.
        Nest(12, "FindNode deep\r\n");
        Assert.Equal([("gamegen/d12.gw", "deep")], Find());
        Nest(WorldAssembler.MaximumScriptDepth, "FindNode deepest\r\n");
        Assert.Equal([("gamegen/d32.gw", "deepest")], Find());
        // One level more is refused, never left out.
        Nest(WorldAssembler.MaximumScriptDepth + 1, "FindNode beyond\r\n");
        Assert.Contains("more than 32 levels deep", Message(() => Find()));
        // A script sourced again, or sourcing itself, is read once.
        Nest(3, "FindNode loop\r\nsource d1.gw\r\nsource d3.gw\r\n");
        Assert.Equal([("gamegen/d3.gw", "loop")], Find());
        // A script only the game's run names, or one outside gamegen, is refused rather than skipped.
        Nest(2, "set part tex\r\nsource %part%.gw\r\n");
        Assert.Contains("sources a script its macros name (%part%.gw)", Message(() => Find()));
        Nest(2, "source ..\\data\\tex.gw\r\n");
        Assert.Contains("outside the project's gamegen folder", Message(() => Find()));
        // Instructions beyond what a build runs, across scripts.
        string many = string.Concat(Enumerable.Repeat("a\n", WorldAssembler.MaximumInstructions / 2 + 1));
        scripts.Clear();
        scripts[WorldLookups.LoadScript("m1")] = "source half.gw\r\nsource other.gw\r\n";
        scripts["gamegen/half.gw"] = many; scripts["gamegen/other.gw"] = many;
        Assert.Contains("instructions", Message(() => Find()));
    }

    [Fact]
    public async Task LookupsSourcedDeepAreReportedAndThoseNotFollowedAreSaidToBeUnchecked()
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePartDatabase();
        // The part is copied twice, so FindNode crate has two candidates; the script finding it is ten sources down.
        fixture.Write("gamegen/m1_zbd.gs", $"source {Chain(fixture, "fx", 10, "support\\tex_fxm1.gw")}\r\nQuit\r\n");
        fixture.Write("gamegen/support/tex_fxm1.gw", "FindNode crate\r\nQuit\r\n");
        var check = await SourceBuilder.CheckAsync(fixture.Project, ["m1/gamez.zbd", "m1/anim.zbd"], token: Token);
        var crate = Assert.Single(check.Lookups, l => l.Name == "crate");
        Assert.Equal((SourceLookup.TextureEffect, "gamegen/support/tex_fxm1.gw", 2), (crate.Kind, crate.Source, crate.Candidates));
        Assert.DoesNotContain(check.Notes, n => n.Contains("were not checked", StringComparison.Ordinal));

        // Deeper than a build follows: the export and the source world both say the lookups were not checked.
        fixture.Write("gamegen/m1_zbd.gs", $"source {Chain(fixture, "deep", WorldAssembler.MaximumScriptDepth + 1, "support\\tex_fxm1.gw")}\r\nQuit\r\n");
        check = await SourceBuilder.CheckAsync(fixture.Project, ["m1/gamez.zbd", "m1/anim.zbd"], token: Token);
        Assert.Empty(check.Lookups);
        Assert.Contains(check.Notes, n => n.StartsWith("The lookups by name the game makes as it loads m1 were not checked", StringComparison.Ordinal) && n.Contains("32 levels deep", StringComparison.Ordinal));
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "p-" + Guid.NewGuid().ToString("N")), token: Token);
        Assert.Empty(build.Lookups);
        Assert.Contains(build.Outputs.Single(o => o.Family == "world").Warnings, w => w.StartsWith("The lookups by name the game makes as it loads m1 were not checked", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherMissionsBuildTheBuildRefusesReachesNoNode()
    {
        using SourceWorldFixture fixture = new();
        // m2 loads the tank and turns its hull; its scripts first source each other deeper than the build follows.
        fixture.Write("gamegen/m2.gs", Replace(fixture, "gamegen/m2.gs", "LoadGameGen tank.flt tank", "LoadGameGen tank.flt tank\r\nFindSubNode hull\r\nObject3DRotate 0.0 1.0 0.0"));
        SourceWorkspace workspace = new(fixture.Project);
        WorldNodeProvenance origin = new() { ModelFile = fixture.Tank, ModelNode = 0, ModelNodeName = "hull" };
        var hit = SourceObjectEdits.TransformElsewhere(workspace, "m1", origin, "hull", Token);
        Assert.Equal(("m2", "Object3DRotate"), (hit?.Mission, hit?.Instruction.Command));
        fixture.Write("gamegen/m2.gs", "source " + Chain(fixture, "deep", WorldAssembler.MaximumScriptDepth + 1, "end.gw") + "\r\n" + Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m2.gs"))));
        fixture.Write("gamegen/end.gw", "# nothing\r\n");
        // The build stops there, so m2 builds nothing: its later lines turn no node.
        Assert.Null(SourceObjectEdits.TransformElsewhere(workspace, "m1", origin, "hull", Token));
    }

    private static AnimationPreviewContext PreviewContext()
    {
        GameScene scene = new();
        scene.Models.Add(new(0, [Vector3.Zero], [], [], [], []));
        var world = new ZbdDocument("gamez.zbd", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        return new() { Package = new AnimationPackage { Prefix = [], Tail = [] }, World = world };
    }

    [Fact]
    public void TextureEffectPreviewsStopAtTheBuildsLimitsAndSaySo()
    {
        // As deep as a build follows: read without a note.
        Dictionary<string, ScriptContent> scripts = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < WorldAssembler.MaximumScriptDepth; i++) scripts[$"s{i}"] = new([["source", $"s{i + 1}"]], "");
        scripts[$"s{WorldAssembler.MaximumScriptDepth}"] = new([["FindNode", "x"]], "");
        var context = PreviewContext();
        context.ReadTextureScript("s0", scripts, Token);
        Assert.Empty(context.Diagnostics);
        // One level more: the preview says it stopped.
        scripts[$"s{WorldAssembler.MaximumScriptDepth}"] = new([["source", "beyond"]], "");
        context = PreviewContext();
        context.ReadTextureScript("s0", scripts, Token);
        Assert.Contains(context.Diagnostics, d => d.Contains("sourced more than 32 levels deep", StringComparison.Ordinal));
        // A script sourced repeatedly runs each time, up to the instructions a build runs: 2^24 runs are not attempted.
        scripts.Clear();
        for (int i = 0; i < 24; i++) scripts[$"r{i}"] = new([["source", $"r{i + 1}"], ["source", $"r{i + 1}"]], "");
        scripts["r24"] = new([["FindNode", "x"]], "");
        context = PreviewContext();
        context.ReadTextureScript("r0", scripts, Token);
        Assert.Contains(context.Diagnostics, d => d.Contains("more than 1,000,000 instructions", StringComparison.Ordinal));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PreviewContext().ReadTextureScript("r0", scripts, canceled.Token));
    }

    /// <summary>A checkout folder as Edit in Blender writes it, with <paramref name="files"/> listed in its manifest and <paramref name="exports"/> in its outbox.</summary>
    private static string FakeCheckout(string project, string id, int exports, JsonArray? files = null)
    {
        string folder = Path.Combine(project, "zstudio", "export", id);
        Directory.CreateDirectory(Path.Combine(folder, "outbox", "nested"));
        JsonObject manifest = new() { ["format"] = "zstudio-blender-checkout", ["version"] = 1, ["id"] = id, ["model"] = "data/m1/models/m1.gltf", ["created"] = "2026-01-01T00:00:00.0000000Z", ["files"] = files ?? [] };
        File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest.ToJsonString());
        for (int i = 0; i < exports; i++) File.WriteAllText(Path.Combine(folder, "outbox", i % 2 == 0 ? "" : "nested", $"e{i}.gltf"), "{}");
        File.WriteAllText(Path.Combine(folder, "outbox", "notes.txt"), "");
        return folder;
    }

    [Fact]
    public void CheckoutListingsAreBoundedAndCancellable()
    {
        using SourceWorldFixture fixture = new();
        for (int i = 0; i < 3; i++) FakeCheckout(fixture.Project, $"c{i}", 4);
        var listed = SourceBlender.CheckoutExports(fixture.Project, Token);
        Assert.Equal(3, listed.Count);
        Assert.All(listed, c => Assert.Equal(["e0.gltf", "e2.gltf", "nested/e1.gltf", "nested/e3.gltf"], c.Exports.Select(e => e.Relative).Order(StringComparer.Ordinal)));
        // Three checkout folders, and in each outbox a nested folder, four exports and a note: 3 + 3 × 6 entries in all.
        Assert.Equal(3, SourceBlender.CheckoutExports(fixture.Project, 21, Token).Count);
        var error = Assert.Throws<IOException>(() => SourceBlender.CheckoutExports(fixture.Project, 20, Token));
        Assert.Contains("zstudio/export holds more than 20 files and folders", error.Message, StringComparison.Ordinal);
        // The files a manifest lists count before they are read.
        FakeCheckout(fixture.Project, "listed", 0, new JsonArray([.. Enumerable.Range(0, 40).Select(_ => (JsonNode?)0)]));
        Assert.Throws<IOException>(() => SourceBlender.CheckoutExports(fixture.Project, 60, Token));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SourceBlender.CheckoutExports(fixture.Project, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SourceBlender.Checkouts(fixture.Project, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SourceBlender.Exports(listed[0].Checkout, canceled.Token));
    }
}
