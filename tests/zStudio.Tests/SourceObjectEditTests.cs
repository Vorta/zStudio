using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// World object edits in a source world: a built node knows where it came from, and an edit changes that source — the
/// glTF node of the mission database, or the script instruction that placed it — then the rebuilt world shows it.
/// </summary>
public sealed class SourceObjectEditTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(SourceWorldBuild Build, GameZWorld World)> BuildAsync(SourceWorldFixture fixture, SourceWorkspace workspace, string mission = "m2")
    {
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, mission, Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "p-" + Guid.NewGuid().ToString("N")), workspace.Overlay(), token: Token);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        return (build, world);
    }
    private static int Slot(GameZWorld world, string name) => world.Nodes.IndexOf(world.Nodes.Single(n => n.Name == name));

    [Fact]
    public async Task EveryBuiltNodeKnowsItsSource()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        // The ground is a node of the mission database; the tank's root is the node LoadGameGen created in m2.gs.
        var ground = build.Provenance[Slot(world, "ground")];
        Assert.Equal("data/m2/models/m2.gltf", ground.ModelFile); Assert.Equal(0, ground.ModelNode); Assert.True(ground.Database);
        Assert.Equal("LoadGameGen", ground.Load!.Command);
        var tank = build.Provenance[Slot(world, "tank")];
        Assert.Null(tank.ModelFile);
        Assert.Equal(("gamegen/m2.gs", "LoadGameGen"), (tank.Created!.Script, tank.Created.Command));
        Assert.Equal(1, build.Executions[(tank.Created.Script, tank.Created.Line)]);
        // The hull inside the tank model comes from the model file, not the database.
        var hull = build.Provenance[Slot(world, "hull")];
        Assert.Equal(fixture.Tank, hull.ModelFile); Assert.False(hull.Database);
        // The world node records the commands that set it up.
        var worldNode = build.Provenance[world.Nodes.FindIndex(n => n.Class == WorldNodeClass.World)];
        Assert.Equal("NewWorld", worldNode.Created!.Command);
        Assert.Equal("WorldPartition", worldNode.Writers["WorldPartition"].Command);
    }

    [Fact]
    public async Task MovingADatabaseObjectEditsItsGltfNode()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        var ground = build.Provenance[Slot(world, "ground")];
        ObjectTransform requested = new(new(10, 2, -30), new(0, 90, 0), Vector3.One);
        var plan = SourceObjectEdits.PlanTransform(workspace, "ground", ground, build.Executions, requested, Token);
        Assert.Equal("data/m2/models/m2.gltf", plan.Changes.Single().Relative);
        var node = JsonNode.Parse(plan.Changes.Single().Content)!["nodes"]![0]!;
        Assert.Null(node["matrix"]);
        Assert.Equal([10f, 2f, -30f], node["translation"]!.AsArray().Select(v => v!.GetValue<float>()));
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var (_, moved) = await BuildAsync(fixture, workspace);
        var local = WorldUpdate.LocalMatrix(moved.Nodes.Single(n => n.Name == "ground"))!.Value;
        var back = ObjectTransform.FromMatrix(local);
        Assert.Equal(requested.Position, back.Position);
        Assert.Equal(90, back.RotationDegrees.Y, 3); Assert.Equal(0, back.RotationDegrees.X, 3); Assert.Equal(0, back.RotationDegrees.Z, 3);
    }

    [Fact]
    public async Task MovingAScriptPlacedObjectEditsOrAddsItsInstructions()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        // A placed copy of the tank: LoadGameGen, Object3DTranslate, FindNode and AddChild.
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var origin = build.Provenance[Slot(world, "tank_at")];
        Assert.Equal("Object3DTranslate", origin.Writers["Object3DTranslate"].Command);
        string before = Encoding.Latin1.GetString(workspace.Read("gamegen/m1.gs", Token)!);
        // A new position changes the translation tokens; a rotation adds an Object3DRotate after it.
        var plan = SourceObjectEdits.PlanTransform(workspace, "tank_at", origin, build.Executions, new(new(110, 5, -50), new(0, 45, 0), Vector3.One), Token);
        string after = Encoding.Latin1.GetString(plan.Changes.Single().Content);
        Assert.Equal(before.Replace("Object3DTranslate 100.0 0.0 -50.0\r\n", "Object3DTranslate 110.0 5.0 -50.0\r\nObject3DRotate 0.0 45.0 0.0\r\n"), after);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var (rebuilt, moved) = await BuildAsync(fixture, workspace, "m1");
        var transform = ObjectTransform.FromMatrix(WorldUpdate.LocalMatrix(moved.Nodes.Single(n => n.Name == "tank_at"))!.Value);
        Assert.Equal(new Vector3(110, 5, -50), transform.Position); Assert.Equal(45, transform.RotationDegrees.Y, 3);
        // The rotation instruction now exists, so the next edit changes it in place.
        var again = SourceObjectEdits.PlanTransform(workspace, "tank_at", rebuilt.Provenance[Slot(moved, "tank_at")], rebuilt.Executions, new(new(110, 5, -50), new(0, 90, 0), Vector3.One), Token);
        Assert.Equal(after.Replace("Object3DRotate 0.0 45.0 0.0", "Object3DRotate 0.0 90.0 0.0"), Encoding.Latin1.GetString(again.Changes.Single().Content));
    }

    [Fact]
    public async Task FlagsChangeInTheGltfExtrasOrTheScriptCommandThatSetThem()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        var ground = build.Provenance[Slot(world, "ground")];
        // CanModify on the database ground: the glTF node's carried flags.
        var plan = SourceObjectEdits.PlanFlag(workspace, world.Nodes.Single(n => n.Name == "ground"), ground, build.Executions, 0x10000, true, Token);
        var recoil = JsonNode.Parse(plan.Changes.Single().Content)!["nodes"]![0]!["extras"]!["recoil"]!;
        Assert.Equal($"0x{WorldGltf.DefaultCarried | 0x10000:X8}", recoil["flags"]!.GetValue<string>());
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var (_, flagged) = await BuildAsync(fixture, workspace);
        Assert.NotEqual(0u, flagged.Nodes.Single(n => n.Name == "ground").Flags & 0x10000);
        // Clearing it again restores the default, so the extras entry goes away.
        var (b2, w2) = await BuildAsync(fixture, workspace);
        var clear = SourceObjectEdits.PlanFlag(workspace, w2.Nodes.Single(n => n.Name == "ground"), b2.Provenance[Slot(w2, "ground")], b2.Executions, 0x10000, false, Token);
        Assert.Null(JsonNode.Parse(clear.Changes.Single().Content)!["nodes"]![0]!["extras"]!["recoil"]!["flags"]);
        // A flag of a script-created node is set by a new instruction after the one that created it.
        var tank = build.Provenance[Slot(world, "tank")];
        var scripted = SourceObjectEdits.PlanFlag(workspace, world.Nodes.Single(n => n.Name == "tank"), tank, build.Executions, 0x10, false, Token);
        Assert.Contains("LoadGameGen tank.flt tank\r\nSetIntersectSurface off\r\n", Encoding.Latin1.GetString(scripted.Changes.Single().Content));
    }

    [Fact]
    public void InstructionsThatRunMoreThanOnceOrUseMacrosAreRefused()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceInstruction translate = new("gamegen/m1.gs", 3, "Object3DTranslate", ["Object3DTranslate", "1.0", "2.0", "3.0"], ["1.0", "2.0", "3.0"]);
        WorldNodeProvenance origin = new();
        origin.Writers["Object3DTranslate"] = translate;
        var twice = new Dictionary<(string, int), int> { [("gamegen/m1.gs", 3)] = 2 };
        Assert.Contains("runs 2 times", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "x", origin, twice, new(new(5, 5, 5), Vector3.Zero, Vector3.One), Token)).Message);
        origin.Writers["Object3DTranslate"] = translate with { Tokens = ["Object3DTranslate", "%x%", "2.0", "3.0"] };
        var once = new Dictionary<(string, int), int> { [("gamegen/m1.gs", 3)] = 1 };
        Assert.Contains("macro", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "x", origin, once, new(new(5, 5, 5), Vector3.Zero, Vector3.One), Token)).Message);
        Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "x", origin, once, new(new(float.NaN, 0, 0), Vector3.Zero, Vector3.One), Token));
    }

    [Fact]
    public void TransformsRoundTripThroughTheEngineMatrix()
    {
        foreach (var rotation in new Vector3[] { new(10, 20, 30), new(-45, 170, 5), new(0, -90, 0), new(30, 0, -60) })
        {
            ObjectTransform t = new(new(1, 2, 3), rotation, new(1, 2, 0.5f));
            var back = ObjectTransform.FromMatrix(t.Matrix());
            Assert.Equal(t.Position, back.Position);
            for (int i = 0; i < 3; i++) { Assert.Equal(t.RotationDegrees[i], back.RotationDegrees[i], 2); Assert.Equal(t.Scale[i], back.Scale[i], 4); }
        }
    }

    [Fact]
    public void ScriptTokensAndLinesChangeLosslessly()
    {
        string text = "# header\r\nset a 1\r\n  LoadGameGen x.flt x   # trailing\r\nObject3DTranslate 1.0,2.0 3.0\r\nQuit\r\n";
        var syntax = GameGenScriptSyntax.Parse(text, TestContext.Current.CancellationToken);
        Assert.Equal("\r\n", syntax.Newline);
        Assert.Equal(["Object3DTranslate", "1.0", "2.0", "3.0"], syntax.Line(4).Tokens);
        string replaced = syntax.ReplaceTokens(4, new Dictionary<int, string> { [1] = "7.5", [3] = "-1.0" }, TestContext.Current.CancellationToken);
        Assert.Equal(text.Replace("1.0,2.0 3.0", "7.5,2.0 -1.0"), replaced);
        string inserted = GameGenScriptSyntax.Parse(replaced, TestContext.Current.CancellationToken).InsertLines(4, [["Object3DRotate", "0.0", "90.0", "0.0"]], TestContext.Current.CancellationToken);
        Assert.Contains("# trailing\r\nObject3DRotate 0.0 90.0 0.0\r\nObject3DTranslate", inserted);
        Assert.StartsWith("#   LoadGameGen", GameGenScriptSyntax.Parse(text, TestContext.Current.CancellationToken).CommentOut(3, TestContext.Current.CancellationToken)[(text.IndexOf("  LoadGameGen", StringComparison.Ordinal))..]);
        Assert.Throws<InvalidDataException>(() => syntax.ReplaceTokens(4, new Dictionary<int, string> { [1] = "two words" }, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => syntax.ReplaceTokens(1, new Dictionary<int, string> { [0] = "x" }, TestContext.Current.CancellationToken));
        // A file without a final newline gets one before an appended line.
        Assert.Equal("Quit\nNewWorld w\n", GameGenScriptSyntax.Parse("Quit", TestContext.Current.CancellationToken).InsertLines(2, [["NewWorld", "w"]], TestContext.Current.CancellationToken).Replace("\r\n", "\n"));
    }
}
