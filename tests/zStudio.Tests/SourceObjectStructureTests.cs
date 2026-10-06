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

/// <summary>Deleting, copying and re-parenting world objects of a source world through the sources that made them.</summary>
public sealed class SourceObjectStructureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(SourceWorldBuild Build, GameZWorld World)> BuildAsync(SourceWorldFixture fixture, SourceWorkspace workspace, string mission)
    {
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, mission, Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "p-" + Guid.NewGuid().ToString("N")), workspace.Overlay(), token: Token);
        Assert.Null(build.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        return (build, world);
    }
    private static SourceObjectTarget Target(SourceWorkspace workspace, string mission, SourceWorldBuild build, GameZWorld world, string name) =>
        Target(workspace, mission, build, world, world.Nodes.Single(n => n.Name == name));
    private static SourceObjectTarget Target(SourceWorkspace workspace, string mission, SourceWorldBuild build, GameZWorld world, WorldNode picked)
    {
        var slots = GameZWriter.NodeSlots(world);
        Dictionary<WorldNode, WorldNodeProvenance> provenance = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, slot) in slots) if (build.Provenance.TryGetValue(slot, out var origin)) provenance[node] = origin;
        return new(workspace, mission, world, SourceObjectEdits.ObjectOf(picked, provenance), provenance, build.Executions);
    }
    private static void Apply(SourceWorkspace workspace, SourceEditPlan plan) => Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
    private static string Text(SourceWorkspace workspace, string path) => Encoding.Latin1.GetString(workspace.Read(path, Token)!);
    private static WorldNode WorldNode(GameZWorld world) => world.Nodes.Single(n => n.Class == WorldNodeClass.World);

    [Fact]
    public async Task NodesSeveralParentsShareAreNotEditedThroughOneCopy()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteSharedDatabase();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var gate = world.Nodes.Single(n => n.Name == "gate");
        var shared = Assert.Single(gate.Parents);
        Assert.Equal(2, shared.Parents.Count);
        // The file holds the shared node once per gate and the build reads the first: moving gate out of one copy would keep
        // one place (other edits change every copy alike, see SourceObjectPlanTests).
        var target = Target(workspace, "m1", build, world, gate);
        Assert.Contains("several parents share", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(target, null, Token)).Message);
        // Moving another object into it would place that object under each gate.
        Assert.Contains("placed under each", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "ground"), gate, Token)).Message);
        // The gates themselves move freely; deleting the first leaves the shared node, unchanged, under the second.
        var first = Target(workspace, "m1", build, world, "sgate1");
        Apply(workspace, SourceObjectEdits.PlanTransform(workspace, "sgate1", first.Origin, build.Executions, new(new(10, 0, 0), Vector3.Zero, Vector3.One), Token));
        Apply(workspace, SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, "sgate1"), Token));
        (_, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal("sgate2", Assert.Single(Assert.Single(world.Nodes.Single(n => n.Name == "gate").Parents).Parents).Name);
    }

    [Fact]
    public async Task ChildrenAScriptMovedElsewhereStillCountForTheirFilesParent()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteNestedDatabase();
        // As gamegen's morfUtil.gw does with morph LODs, a script takes the lid out of the crate and attaches it to the ground.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs"))).Replace("# no vehicles", "FindNode crate\r\nDeleteChild lid\r\nFindNode ground\r\nAddChild lid", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal("ground", Assert.Single(world.Nodes.Single(n => n.Name == "lid").Parents).Name);
        // Copying the crate copies its lid in the file too; the script's AddChild lid would then find the copy's.
        Assert.Contains("acts on lid", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(Target(workspace, "m1", build, world, "crate"), "crate2", null, Token)).Message);
    }

    [Fact]
    public async Task NodesAScriptPlacesElsewhereAreNotCopiedOrUsedAsParents()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteNestedDatabase();
        // The script attaches the lid to the ground: a copy of it would stand in the crate, where the file puts it.
        string script = Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs")));
        fixture.Write("gamegen/m1.gs", script.Replace("# no vehicles", "FindNode crate\r\nDeleteChild lid\r\nFindNode ground\r\nAddChild lid", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Contains("places lid elsewhere", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(Target(workspace, "m1", build, world, "lid"), "lid2", null, Token)).Message);
        // Taken out of the world instead, the lid is no parent to move the ground under: the ground would leave the world.
        fixture.Write("gamegen/m1.gs", script.Replace("# no vehicles", "FindNode crate\r\nDeleteChild lid", StringComparison.Ordinal));
        workspace = new(fixture.Project);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Empty(world.Nodes.Single(n => n.Name == "lid").Parents);
        Assert.Contains("not placed in the world", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "ground"), world.Nodes.Single(n => n.Name == "lid"), Token)).Message);
    }

    [Fact]
    public async Task ScriptLoadedObjectsWhosePartsScriptsChangeAreNotCopied()
    {
        using SourceWorldFixture fixture = new();
        // The tank is placed in the world and a script scales its hull: a copy would repeat only the tank's own lines.
        string script = Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m2.gs")));
        fixture.Write("gamegen/m2.gs", script.Replace("LoadGameGen tank.flt tank", "LoadGameGen tank.flt tank\r\nFindNode world\r\nAddChild tank\r\nFindNode hull\r\nObject3DScale 1.0 1.0 1.0", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m2");
        Assert.Contains("acts on hull, which is below tank", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(Target(workspace, "m2", build, world, "tank"), "tank2", null, Token)).Message);
    }

    [Fact]
    public async Task ABuildShowsInstructionsThatFindANodeOnlyAfterAMove()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteNestedDatabase();
        // FindSubNode lid finds nothing under the ground until the lid moves there.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs"))).Replace("# no vehicles", "FindNode ground\r\nFindSubNode lid\r\nSetLandmark on", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "lid"), world.Nodes.Single(n => n.Name == "ground"), Token));
        var (after, _) = await BuildAsync(fixture, workspace, "m1");
        Assert.Contains("instead of no node", SourceObjectEdits.TargetChange(build, after));
    }

    [Fact]
    public async Task ABuildTellsCopiesOfAReferencedFileApart()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteReferencingDatabase(twice: true);
        // Both crates reference lidm.gltf; the script marks the newest lid. Moving the first crate under the second makes
        // its lid the newest: another copy of the same glTF node.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs"))).Replace("# no vehicles", "FindNode lid\r\nSetLandmark on", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal(2, world.Nodes.Count(n => n.Name == "lid"));
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "crate"), world.Nodes.Single(n => n.Name == "crate_b"), Token));
        var (after, _) = await BuildAsync(fixture, workspace, "m1");
        Assert.Contains("would act on", SourceObjectEdits.TargetChange(build, after));
    }

    [Fact]
    public async Task ABuildShowsWhenAMoveMakesAScriptActOnAnotherNode()
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePartDatabase(secondGround: true);
        // The script marks the newest ground as a landmark; moving the other ground under it makes that one the newest.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs"))).Replace("# no vehicles", "FindNode ground\r\nSetLandmark on", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Null(SourceObjectEdits.TargetChange(build, build));
        var slots = GameZWriter.NodeSlots(world);
        var grounds = world.Nodes.Where(n => n.Name == "ground").ToList();
        var flagged = grounds.Single(g => build.Provenance[slots[g]].Applied.Count > 0);
        var plain = grounds.Single(g => !ReferenceEquals(g, flagged));
        // The plan cannot know; the rebuilt world shows it, and the editor takes such a change back.
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, plain), flagged, Token));
        var (after, _) = await BuildAsync(fixture, workspace, "m1");
        Assert.Contains("would act on", SourceObjectEdits.TargetChange(build, after));
    }

    [Fact]
    public async Task CopiesAreRefusedWhileAScriptTakesAPartOutOfTheNode()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteReferencingDatabase();
        // The lid comes from the file the crate references, so neither the crate's built subtree nor its file holds it once
        // the script moves it; copying the crate would still copy it, and AddChild lid would find the copy's.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs"))).Replace("# no vehicles", "FindNode crate\r\nDeleteChild lid\r\nFindNode ground\r\nAddChild lid", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal("ground", Assert.Single(world.Nodes.Single(n => n.Name == "lid").Parents).Name);
        Assert.Contains("(DeleteChild) takes a part out of crate", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(Target(workspace, "m1", build, world, "crate"), "crate2", null, Token)).Message);
    }

    [Fact]
    public async Task PartEditsRespectScriptsActingOnAnyCopy()
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePartDatabase();
        // A script scales one copy's lid (FindNode finds the newest). Deleting or copying either crate edits the part, so both copies: the
        // script would lose its lid, or find the other one, whichever copy was chosen.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs"))).Replace("# no vehicles", "FindNode lid\r\nObject3DScale 1.0 1.0 1.0", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var crates = world.Nodes.Where(n => n.Name == "crate").ToList();
        Assert.Equal(2, crates.Count);
        foreach (var crate in crates)
        {
            var target = Target(workspace, "m1", build, world, crate);
            Assert.Contains("(Object3DScale) acts on lid", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDelete(target, Token)).Message);
            Assert.Contains("(Object3DScale) acts on lid", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(target, "crate2", null, Token)).Message);
        }
        // One copy's lid is scaled by the script and the other's is not, so the refusal for that crate comes from the other copy.
        var slots = GameZWriter.NodeSlots(world);
        Assert.Equal([0, 1], world.Nodes.Where(n => n.Name == "lid").Select(lid => build.Provenance[slots[lid]].Applied.Count).Order());
        // Moving the unscaled copy's lid edits the part's node too, so the other copy's script scale would stop applying.
        var plain = world.Nodes.Where(n => n.Name == "lid").Select(lid => build.Provenance[slots[lid]]).Single(p => p.Applied.Count == 0);
        // Copying the unscaled lid would give every copy's part a lid built without the other copy's script scale.
        var unscaled = world.Nodes.Where(n => n.Name == "lid").Single(lid => build.Provenance[slots[lid]].Applied.Count == 0);
        Assert.Contains("in another copy of it", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(Target(workspace, "m1", build, world, unscaled), "lid2", null, Token)).Message);
        // Copying the scaled one would give the unscaled copy's part a scaled lid.
        var scaled = world.Nodes.Where(n => n.Name == "lid").Single(lid => build.Provenance[slots[lid]].Applied.Count > 0);
        Assert.Contains("one copy of lid", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(Target(workspace, "m1", build, world, scaled), "lid2", null, Token)).Message);
        Assert.Contains("another copy of lid", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "lid", plain, build.Executions,
            new(new(1, 2, 3), Vector3.Zero, Vector3.One), Token, "m1", null, SourceObjectEdits.CopiesOf(plain, build.Provenance.Values))).Message);
    }

    [Fact]
    public async Task PartsOfTheDatabaseAreEditedInTheirOwnFileForEveryCopy()
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePartDatabase();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        const string Part = "data/m1/models/m1_01.gltf";
        List<WorldNode> Named(string name) => [.. world.Nodes.Where(n => n.Name == name)];
        // The database copies the part twice; each copy's objects join the world and come from the part's file.
        Assert.Equal(2, Named("crate").Count);
        Assert.All(Named("crate").Concat(Named("post")), n => Assert.Equal([WorldNode(world)], n.Parents));
        var crate = Target(workspace, "m1", build, world, Named("crate")[1]);
        Assert.Equal(Part, crate.Origin.ModelFile); Assert.True(crate.Origin.Database); Assert.True(crate.Origin.Part);
        Assert.False(Target(workspace, "m1", build, world, "ground").Origin.Part);

        // A move edits the part's node, so both copies move.
        var move = SourceObjectEdits.PlanTransform(workspace, "crate", crate.Origin, build.Executions, new(new(20, 0, -40), Vector3.Zero, Vector3.One), Token);
        Assert.Equal(Part, move.Changes.Single().Relative);
        Assert.Contains(move.Notes, n => n.Contains("part of the mission database", StringComparison.Ordinal));
        Apply(workspace, move);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.All(Named("crate"), c => Assert.Equal(new Vector3(20, 0, -40), WorldUpdate.LocalMatrix(c)!.Value.Translation));
        // A copy in the part appears in every copy of it.
        var copy = SourceObjectEdits.PlanDuplicate(Target(workspace, "m1", build, world, Named("post")[0]), "post2", null, Token);
        Assert.Equal(Part, copy.Changes.Single().Relative);
        Assert.Contains(copy.Notes, n => n.Contains("2 nodes named post2", StringComparison.Ordinal));
        Apply(workspace, copy);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal(2, Named("post2").Count);
        // It moves under another node of the part, never under a node of another file; the lid moves to the part's top.
        Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, Named("post2")[0]), world.Nodes.Single(n => n.Name == "ground"), Token));
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, Named("post2")[0]), Named("crate")[0], Token));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.All(Named("post2"), p => Assert.Equal("crate", Assert.Single(p.Parents).Name));
        var top = SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, Named("lid")[0]), null, Token);
        Assert.Contains(top.Notes, n => n.Contains("root of " + Part, StringComparison.Ordinal));
        Assert.Equal($"Move lid to the top of {Path.GetFileName(Part)}", top.Label);
        Apply(workspace, top);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.All(Named("lid"), l => Assert.Equal([WorldNode(world)], l.Parents));
        // Moving it there again changes nothing, so it is refused rather than reported as a move.
        Assert.Contains("already a root of the world", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, Named("lid")[0]), null, Token)).Message);
        // Deleting it removes it from the part, so from both copies; the database file never changed.
        Apply(workspace, SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, Named("post2")[0]), Token));
        (_, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Empty(Named("post2"));
        Assert.Equal(File.ReadAllBytes(fixture.Path("data/m1/models/m1.gltf")), workspace.Read("data/m1/models/m1.gltf", Token));
        for (int i = 0; i < 5; i++) workspace.Undo();
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task DatabaseObjectsAreCopiedMovedAndDeletedInTheirGltfFile()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        // A copy beside the original, at its own place, sharing the mesh.
        var copy = SourceObjectEdits.PlanDuplicate(Target(workspace, "m1", build, world, "ground"), "ground2", new(new(0, 0, 100), Vector3.Zero, Vector3.One), Token);
        var gltf = JsonNode.Parse(copy.Changes.Single().Content)!;
        Assert.Equal(["ground", "ground2"], gltf["nodes"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()));
        Assert.Equal(gltf["nodes"]![0]!["mesh"]!.GetValue<int>(), gltf["nodes"]![1]!["mesh"]!.GetValue<int>());
        Assert.Equal([0, 1], gltf["scenes"]![0]!["nodes"]!.AsArray().Select(n => n!.GetValue<int>()));
        Apply(workspace, copy);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var copied = world.Nodes.Single(n => n.Name == "ground2");
        Assert.Contains(WorldNode(world), copied.Parents);
        Assert.Equal(new Vector3(0, 0, 100), ObjectTransform.FromMatrix(WorldUpdate.LocalMatrix(copied)!.Value).Position);
        // Under the original, it keeps its place in the world.
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "ground2"), world.Nodes.Single(n => n.Name == "ground"), Token));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var moved = world.Nodes.Single(n => n.Name == "ground2");
        Assert.Equal("ground", Assert.Single(moved.Parents).Name);
        Assert.Equal([0], JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf", Token)!)!["scenes"]![0]!["nodes"]!.AsArray().Select(n => n!.GetValue<int>()));
        // Deleting it removes the node and renumbers the file.
        Apply(workspace, SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, "ground2"), Token));
        var after = JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf", Token)!)!;
        Assert.Equal("ground", Assert.Single(after["nodes"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Null(after["nodes"]![0]!["children"]);
        (_, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.DoesNotContain(world.Nodes, n => n.Name == "ground2");
        // Undoing the three changes restores the original file.
        for (int i = 0; i < 3; i++) workspace.Undo();
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void TransformsSurviveTinyScalesAndNearlyVerticalPitches()
    {
        static Vector3 Row(Matrix4x4 m, int row) => new(m[row, 0], m[row, 1], m[row, 2]);
        // Decompose replaces axes shorter than its epsilon (here with a quaternion 147° off): such a matrix is written whole.
        var tiny = Matrix4x4.CreateScale(1e-5f) * Matrix4x4.CreateFromYawPitchRoll(0.5f, 0.3f, 0.7f) * Matrix4x4.CreateTranslation(10, 0, 0);
        JsonObject node = [];
        GltfNodeEdits.SetLocal(node, tiny);
        var back = GltfNodeEdits.Local(node);
        for (int row = 0; row < 3; row++) Assert.True((Row(back, row) - Row(tiny, row)).Length() <= 1e-3f * Row(tiny, row).Length(), $"row {row}");
        // Near ±90° pitch, a rotation stored as glTF's quaternion reads back as angles giving the same orientation.
        foreach (var angles in new Vector3[] { new(89.9999f, 10, 70), new(89.999f, 37, 12), new(-89.999f, -20, 5), new(-90, 0, 33), new(45, 30, 15) })
        {
            var m = new ObjectTransform(Vector3.Zero, angles, Vector3.One).Matrix();
            JsonObject stored = [];
            GltfNodeEdits.SetLocal(stored, m);
            var shown = ObjectTransform.FromMatrix(GltfNodeEdits.Local(stored));
            var again = new ObjectTransform(Vector3.Zero, shown.RotationDegrees, Vector3.One).Matrix();
            for (int row = 0; row < 3; row++) Assert.True((Row(again, row) - Row(m, row)).Length() < 1e-3f, $"{angles} row {row}: {shown.RotationDegrees}");
        }
        // Edits read the angles back each time: near ±90° their float noise must not build up into a turn.
        var start = new ObjectTransform(Vector3.Zero, new(89.9825f, -156.37941f, 109.36977f), Vector3.One).Matrix();
        JsonObject chain = [];
        GltfNodeEdits.SetLocal(chain, start);
        for (int edit = 0; edit < 30; edit++)
        {
            var shown = ObjectTransform.FromMatrix(GltfNodeEdits.Local(chain));
            GltfNodeEdits.SetLocal(chain, new ObjectTransform(shown.Position, ObjectTransform.Snap(shown.RotationDegrees, angles: true), new Vector3(edit % 2 == 0 ? 1.5f : 1)).Matrix());
        }
        var end = GltfNodeEdits.Local(chain);
        for (int row = 0; row < 3; row++) Assert.True((Row(end, row) - Row(start, row)).Length() < 3e-4f, $"after 30 edits, row {row}");
    }

    [Fact]
    public void MovesUnderFarTurnedParentsAreKept()
    {
        // A node at the origin moved under a plainly turned parent far away (1999 m9's horizon under bar_37: a 10° yaw about
        // 1,700 units out): the inverse rounds with the parent's distance, not the node's, and the move is accepted.
        int refused = 0;
        for (int yaw = 1; yaw < 90; yaw += 4)
            for (float far = 500; far <= 8000; far *= 1.37f)
            {
                var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * MathF.PI / 180);
                var root = JsonNode.Parse($$"""
                    {"scene":0,"scenes":[{"nodes":[0,1]}],"nodes":[
                      {"name":"bar","rotation":[{{q.X:R}},{{q.Y:R}},{{q.Z:R}},{{q.W:R}}],"translation":[{{far:R}},40,{{far * 0.7f:R}}]},
                      {"name":"horizon"}]}
                    """)!.AsObject();
                try { GltfNodeEdits.Reparent(root, 1, 0); }
                catch (InvalidDataException) { refused++; continue; }
                var placed = GltfNodeEdits.World(root, 1);
                Assert.True(placed.Translation.Length() < 0.05f, $"{yaw}° at {far}: {placed.Translation}");
            }
        Assert.Equal(0, refused);
    }

    [Fact]
    public void MovesUnderBadlyConditionedParentsAreRefused()
    {
        // b, under a, is sheared and badly conditioned: its inverse is too imprecise to keep c in place under it.
        var root = JsonNode.Parse("""
            {"scene":0,"scenes":[{"nodes":[0,2]}],"nodes":[
              {"name":"a","children":[1],"matrix":[-0.13071162,0.049208876,0.4451546,0,0.0044525927,-0.00023183179,0.0013330502,0,15.219602,194.42451,-17.023403,0,0,0,0,1]},
              {"name":"b","matrix":[138.63388,21.302254,-73.82902,0,0.00066238473,-0.0006774366,0.0010483419,0,-3.7498612,-26.3116,-14.633194,0,0,0,0,1]},
              {"name":"c","translation":[100,20,30]}]}
            """)!.AsObject();
        Assert.Contains("badly conditioned", Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Reparent(root, 2, 1)).Message);
        // A half turn reads as 180°, not atan2's −180°.
        Assert.Equal(new Vector3(0, 180, 180), ObjectTransform.Snap(new(0, -180, -180), angles: true));
    }

    [Fact]
    public async Task GltfEditsKeepWhatTheFileCanHold()
    {
        foreach (bool far in new[] { false, true })
        {
            using SourceWorldFixture fixture = new();
            fixture.WriteNestedDatabase();
            var json = JsonNode.Parse(File.ReadAllText(fixture.Path("data/m1/models/m1.gltf")))!;
            JsonObject Named(string name) => json["nodes"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == name)!.AsObject();
            // Under a crate 20000 times larger the ground's scale would flatten; under a crate 1000 times smaller the ground,
            // 2000 units away, would lie 2,000,000 units from it.
            Named("crate")["scale"] = far ? new JsonArray(0.001f, 0.001f, 0.001f) : new JsonArray(20000f, 20000f, 20000f);
            Named("ground")["scale"] = new JsonArray(0.01f, 0.01f, 0.01f);
            if (far) Named("ground")["translation"] = new JsonArray(2000f, 0f, 0f);
            // The lid holds a sheared matrix: its axes are not perpendicular, so no rotation and scale can show it.
            var lid = Named("lid");
            foreach (var key in new[] { "translation", "rotation", "scale" }) lid.Remove(key);
            lid["matrix"] = new JsonArray(1f, 0f, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f);
            fixture.Write("data/m1/models/m1.gltf", json.ToJsonString());
            SourceWorkspace workspace = new(fixture.Project);
            var (build, world) = await BuildAsync(fixture, workspace, "m1");
            Assert.Contains(far ? "beyond" : "flattens", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "ground"), world.Nodes.Single(n => n.Name == "crate"), Token)).Message);
            if (far) continue;
            var target = Target(workspace, "m1", build, world, "lid");
            var shown = ObjectTransform.Of(world.Nodes.Single(n => n.Name == "lid"));
            Assert.Contains("shear", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "lid", target.Origin, build.Executions, shown with { RotationDegrees = new(0, 45, 0) }, Token, "m1", shown)).Message);
            Assert.Contains("shear", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(target, "lid2", shown with { RotationDegrees = new(0, 45, 0) }, Token)).Message);
            // Moving it, or copying it with a new position only, keeps the matrix.
            Assert.NotEmpty(SourceObjectEdits.PlanTransform(workspace, "lid", target.Origin, build.Executions, shown with { Position = new(3, 0, 0) }, Token, "m1", shown).Changes);
            Assert.NotEmpty(SourceObjectEdits.PlanDuplicate(target, "lid2", shown with { Position = new(3, 0, 0) }, Token, keepBasis: true).Changes);
        }
    }

    [Fact]
    public async Task NearlyVerticalTurnsAtLargeScalesStayEditable()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        // Pitched 89.99° and scaled 20: Euler angles read back imprecisely there, but the axes stay perpendicular (no shear).
        Apply(workspace, SourceObjectEdits.PlanTransform(workspace, "ground", Target(workspace, "m1", build, world, "ground").Origin, build.Executions, new(Vector3.Zero, new(89.99f, 30, 0), new(20, 20, 20)), Token, "m1"));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var origin = Target(workspace, "m1", build, world, "ground").Origin;
        Assert.NotEmpty(SourceObjectEdits.PlanTransform(workspace, "ground", origin, build.Executions, new(Vector3.Zero, new(45, 0, 0), new(20, 20, 20)), Token, "m1").Changes);
        // A scale of 0 would leave no rotation to read back: it is refused.
        Assert.Contains("scale of 0", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "ground", origin, build.Executions, new(Vector3.Zero, Vector3.Zero, new(0, 1, 1)), Token, "m1")).Message);
        // So is one so small that after the build it could read back as flattened.
        Assert.Contains("0.00001", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "ground", origin, build.Executions, new(Vector3.Zero, new(-10, 37, 0), new(1e-6f, 1e-6f, 1e-6f)), Token, "m1")).Message);
    }

    [Fact]
    public async Task FlattenedNodesKeepTheRotationTheyHide()
    {
        using SourceWorldFixture fixture = new();
        // Blender hides an object by scaling an axis to 0; its rotation stays in the file, but no matrix shows it.
        var json = JsonNode.Parse(File.ReadAllText(fixture.Path("data/m1/models/m1.gltf")))!;
        var ground = json["nodes"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "ground")!.AsObject();
        ground["rotation"] = new JsonArray(0f, 0.38268343f, 0f, 0.9238795f); ground["scale"] = new JsonArray(0f, 1f, 1f);
        fixture.Write("data/m1/models/m1.gltf", json.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var origin = Target(workspace, "m1", build, world, "ground").Origin;
        var shown = ObjectTransform.Of(world.Nodes.Single(n => n.Name == "ground"));
        // Setting the scale would rebuild the matrix from the rotation shown (0), losing the authored one.
        Assert.Contains("zero scale", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "ground", origin, build.Executions, shown with { Scale = Vector3.One }, Token, "m1", shown)).Message);
        // A value that is not a number is checked even beside unchanged ones.
        Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "ground", origin, build.Executions, shown with { Scale = new(float.NaN, 1, 1) }, Token, "m1", shown));
        // A move keeps the matrix, and a copy in place or at a new position keeps it too.
        Assert.NotEmpty(SourceObjectEdits.PlanTransform(workspace, "ground", origin, build.Executions, shown with { Position = new(5, 0, 0) }, Token, "m1", shown).Changes);
        Assert.NotEmpty(SourceObjectEdits.PlanDuplicate(Target(workspace, "m1", build, world, "ground"), "ground2", shown with { Position = new(5, 0, 0) }, Token, keepBasis: true).Changes);
    }

    [Fact]
    public async Task ScriptObjectsKeepTheirPlaceUnderAFarParent()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_a", new(500, 0, 400)), []), Token);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_b", new(0, 0, 30)), []), Token);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        // Under tank_a, tank_b stands at (−500, 0, −370): a position, not an angle, so no half turn is added to it.
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "tank_b"), world.Nodes.Single(n => n.Name == "tank_a"), Token));
        Assert.Contains("Object3DTranslate -500.0", Text(workspace, "gamegen/m1.gs"));
        (_, world) = await BuildAsync(fixture, workspace, "m1");
        var tank = world.Nodes.Single(n => n.Name == "tank_b");
        Assert.Equal("tank_a", Assert.Single(tank.Parents).Name);
        var placed = WorldUpdate.LocalMatrix(tank)!.Value * WorldUpdate.LocalMatrix(tank.Parents[0])!.Value;
        Assert.True(Vector3.Distance(new(0, 0, 30), placed.Translation) < 1e-3f, placed.Translation.ToString());
    }

    [Fact]
    public async Task ScriptObjectsDoNotMoveWhereTheirTransformWouldShear()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_a", new(100, 0, -50)), []), Token);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_b", new(0, 0, 30)), []), Token);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Apply(workspace, SourceObjectEdits.PlanTransform(workspace, "tank_a", Target(workspace, "m1", build, world, "tank_a").Origin, build.Executions, new(new(100, 0, -50), new(0, 37, 0), new(2, 1, 0.5f)), Token, "m1"));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        // Under the turned, unevenly scaled tank_a, tank_b would need a shear, which script transforms cannot hold.
        Assert.Contains("sheared", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "tank_b"), world.Nodes.Single(n => n.Name == "tank_a"), Token)).Message);
    }

    [Fact]
    public async Task ScriptObjectsAreCopiedMovedAndDeletedThroughTheirInstructions()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Apply(workspace, SourceObjectEdits.PlanFlag(workspace, "tank_at", Target(workspace, "m1", build, world, "tank_at").Origin, build.Executions, 0x10, true, Token));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        // A part of the loaded model stands for the whole load.
        var hull = Target(workspace, "m1", build, world, "hull");
        Assert.Equal("tank_at", hull.Node.Name);
        // The copy repeats the load and its flag, at a new place, before the world is written.
        var copy = SourceObjectEdits.PlanDuplicate(hull, "tank_b", new(new(0, 0, 30), new(0, 90, 0), Vector3.One), Token);
        string script = Encoding.Latin1.GetString(copy.Changes.Single().Content);
        Assert.Contains("SetModelDirectory ..\\data\\m2\\models\\bft\r\nLoadGameGen tank.gltf tank_b\r\nSetIntersectSurface on\r\nObject3DTranslate 0.0 0.0 30.0\r\nObject3DRotate 0.0 90.0 0.0\r\nFindNode world\r\nAddChild tank_b\r\nGameZWriteZBDFile", script);
        Assert.Equal("tank_b", Assert.Single(copy.Additions).Name);
        Apply(workspace, copy);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var b = world.Nodes.Single(n => n.Name == "tank_b");
        Assert.Same(WorldNode(world), Assert.Single(b.Parents));
        Assert.NotEqual(0u, b.Flags & 0x10);
        // Moving tank_b under the ground: its AddChild becomes a comment and new lines attach it.
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "tank_b"), world.Nodes.Single(n => n.Name == "ground"), Token));
        Assert.Contains("# AddChild tank_b\r\n", Text(workspace, "gamegen/m1.gs"));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal("ground", Assert.Single(world.Nodes.Single(n => n.Name == "tank_b").Parents).Name);
        // Deleting the first tank turns its instructions into comments; the rest of the script runs as before.
        string before = Text(workspace, "gamegen/m1.gs");
        Apply(workspace, SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, "tank_at"), Token));
        string deleted = Text(workspace, "gamegen/m1.gs");
        foreach (string line in new[] { "LoadGameGen tank.gltf tank_at", "Object3DTranslate 100.0 0.0 -50.0", "SetIntersectSurface on", "AddChild tank_at" })
            Assert.Contains("# " + line, deleted);
        Assert.Equal(before.Length + 8, deleted.Length);
        (_, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.DoesNotContain(world.Nodes, n => n.Name == "tank_at");
        Assert.Contains(world.Nodes, n => n.Name == "tank_b");
    }

    [Fact]
    public async Task EditsThatCannotBeExpressedSafelyAreRefused()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m2");
        // The tank template is not placed in the world, so a copy has no parent to join.
        var template = Target(workspace, "m2", build, world, "hull");
        Assert.Equal("tank", template.Node.Name);
        Assert.Contains("not placed", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(template, "tank2", null, Token)).Message);
        // Names must be new and writable as one token.
        Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(Target(workspace, "m2", build, world, "ground"), "ground", null, Token));
        Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(Target(workspace, "m2", build, world, "ground"), "two words", null, Token));
        // Only objects: the world node cannot be deleted.
        var worldTarget = Target(workspace, "m2", build, world, WorldNode(world).Name);
        Assert.Contains("only objects", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDelete(worldTarget, Token)).Message);
        // A node cannot move under one of its own parts.
        var ground = Target(workspace, "m2", build, world, "ground");
        Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(ground, ground.Node, Token));
        // Nothing was changed.
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task EditsCheckThatTheSourcesStillHoldWhatTheBuildRan()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var tank = Target(workspace, "m1", build, world, "tank_at");
        // A line added above the instructions after the build: their line numbers now name other lines.
        workspace.Apply("Note", [("gamegen/m1.gs", Encoding.Latin1.GetBytes("# note\r\n" + Text(workspace, "gamegen/m1.gs")))], Token);
        Assert.Contains("changed since the world was built", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanFlag(workspace, "tank_at", tank.Origin, build.Executions, 0x10, true, Token, "m1")).Message);
        workspace.Undo();
        // A script another mission runs too is not changed for one of them.
        fixture.Write("gamegen/m3.gs", "source m1.gs\r\n");
        Assert.Contains("also runs in m3", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanFlag(workspace, "tank_at", tank.Origin, build.Executions, 0x10, true, Token, "m1")).Message);
        File.Delete(Path.Combine(fixture.Project, "gamegen", "m3.gs"));
        Assert.NotEmpty(SourceObjectEdits.PlanFlag(workspace, "tank_at", tank.Origin, build.Executions, 0x10, true, Token, "m1").Changes);
        // A node of the database renamed (or replaced) since the build is not the node to edit.
        var ground = Target(workspace, "m1", build, world, "ground");
        var gltf = JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf", Token)!)!;
        gltf["nodes"]![0]!["name"] = "renamed";
        workspace.Apply("Rename", [("data/m1/models/m1.gltf", Encoding.UTF8.GetBytes(gltf.ToJsonString()))], Token);
        Assert.Contains("no longer ground", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "ground", ground.Origin, build.Executions, new(new(1, 0, 0), Vector3.Zero, Vector3.One), Token)).Message);
        workspace.Undo();
        // Copies cannot take a name model imports would shorten to the original's.
        Assert.Contains("suffix", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDuplicate(ground, "ground.001", null, Token)).Message);
        // Only the added tank remains changed.
        Assert.Equal(["gamegen/m1.gs"], workspace.DirtyFiles);
    }

    [Fact]
    public async Task AMoveKeepsTheAuthoredBasisAndDeletingRespectsScriptUsers()
    {
        using SourceWorldFixture fixture = new();
        // The ground's matrix mirrors it, which a rotation and scale cannot express.
        var gltf = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Project, "data", "m1", "models", "m1.gltf")))!;
        gltf["nodes"]![0]!["matrix"] = new JsonArray(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 0, 0, 1);
        fixture.Write("data/m1/models/m1.gltf", gltf.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var ground = Target(workspace, "m1", build, world, "ground");
        var shown = ObjectTransform.Of(ground.Node);
        var moved = SourceObjectEdits.PlanTransform(workspace, "ground", ground.Origin, build.Executions, shown with { Position = new(5, 0, 7) }, Token, "m1", shown);
        var local = GltfNodeEdits.Local((JsonObject)JsonNode.Parse(moved.Changes.Single().Content)!["nodes"]![0]!);
        var expected = new Matrix4x4(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 0, 7, 1);
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) Assert.True(MathF.Abs(local[i, j] - expected[i, j]) < 1e-5f, $"{local} is not {expected}");
        // An unchanged transform changes nothing.
        Assert.Empty(SourceObjectEdits.PlanTransform(workspace, "ground", ground.Origin, build.Executions, shown, Token, "m1", shown).Changes);
        // A script instruction acting on the ground keeps it from being deleted.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(workspace.Read("gamegen/m1.gs", Token)!).Replace("# no vehicles", "FindNode ground\r\nSetIntersectSurface on"));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Contains("SetIntersectSurface", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, "ground"), Token)).Message);
    }

    [Fact]
    public async Task AuthoredGltfNodesAreEditedWhereTheBuildTakesEachPart()
    {
        using SourceWorldFixture fixture = new();
        // The ground has its own rotation and translation; a script translates it (the build applies that), rotates it
        // (ignored for an authored matrix) and turns collision on.
        var gltf = JsonNode.Parse(File.ReadAllBytes(fixture.Path("data/m1/models/m1.gltf")))!;
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        gltf["nodes"]![0]!["rotation"] = new JsonArray(quarter.X, quarter.Y, quarter.Z, quarter.W);
        gltf["nodes"]![0]!["translation"] = new JsonArray(5, 0, 0);
        fixture.Write("data/m1/models/m1.gltf", gltf.ToJsonString());
        string script = File.ReadAllText(fixture.Path("gamegen/m1.gs"));
        fixture.Write("gamegen/m1.gs", script.Replace("# no vehicles", "FindNode ground\r\nObject3DTranslate 20.0 0.0 0.0\r\nObject3DRotate 0.0 45.0 0.0\r\nSetIntersectSurface off"));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var ground = Target(workspace, "m1", build, world, "ground");
        var built = WorldUpdate.LocalMatrix(ground.Node)!.Value;
        Assert.Equal(20f, built.Translation.X, 3);
        Assert.Equal(0f, built.M11, 3);
        var shown = ObjectTransform.FromMatrix(built);
        // A move changes the script's Object3DTranslate, which the build applies; the glTF stays.
        var moved = SourceObjectEdits.PlanTransform(workspace, "ground", ground.Origin, build.Executions, shown with { Position = new(30, 0, 0) }, Token, "m1", shown);
        Assert.Equal("gamegen/m1.gs", Assert.Single(moved.Changes).Relative);
        Apply(workspace, moved);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal(30f, WorldUpdate.LocalMatrix(world.Nodes.Single(n => n.Name == "ground"))!.Value.Translation.X, 3);
        // A rotation changes the glTF's basis and keeps the file's own translation.
        ground = Target(workspace, "m1", build, world, "ground");
        shown = ObjectTransform.Of(ground.Node);
        var turned = SourceObjectEdits.PlanTransform(workspace, "ground", ground.Origin, build.Executions, shown with { RotationDegrees = Vector3.Zero }, Token, "m1", shown);
        var node = (JsonObject)JsonNode.Parse(Assert.Single(turned.Changes).Content)!["nodes"]![0]!;
        Assert.Equal(new Vector3(5, 0, 0), GltfNodeEdits.Local(node).Translation);
        Apply(workspace, turned);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var after = WorldUpdate.LocalMatrix(world.Nodes.Single(n => n.Name == "ground"))!.Value;
        Assert.Equal(1f, after.M11, 3); Assert.Equal(30f, after.Translation.X, 3);
        // A copy in the glTF sits where the original was built and keeps the flags the script gave it (collision off, which
        // the glTF's default would turn on).
        Apply(workspace, SourceObjectEdits.PlanDuplicate(Target(workspace, "m1", build, world, "ground"), "ground2", null, Token));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var copy = world.Nodes.Single(n => n.Name == "ground2");
        Assert.Equal(30f, WorldUpdate.LocalMatrix(copy)!.Value.Translation.X, 3);
        Assert.Equal(0u, copy.Flags & 0x10);
        Assert.Equal(world.Nodes.Single(n => n.Name == "ground").Flags & WorldGltf.CarriedFlags, copy.Flags & WorldGltf.CarriedFlags);
        // Re-parenting under the ground would place the copy by the glTF's translation, not the script's: refused.
        Assert.Contains("A script sets ground's transform", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "ground2"), world.Nodes.Single(n => n.Name == "ground"), Token)).Message);
        // Without the script's translation, an identity glTF transform would let its Object3DRotate apply again: refused.
        workspace.Apply("No translate", [("gamegen/m1.gs", Encoding.Latin1.GetBytes(Text(workspace, "gamegen/m1.gs").Replace("Object3DTranslate 30.0 0.0 0.0\r\n", "")))], Token);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        ground = Target(workspace, "m1", build, world, "ground");
        shown = ObjectTransform.Of(ground.Node);
        Assert.Contains("Object3DRotate", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "ground", ground.Origin, build.Executions, new(Vector3.Zero, Vector3.Zero, Vector3.One), Token, "m1", shown)).Message);
    }

    [Fact]
    public void MirroredTransformsDecomposeToANegativeScaleAndBack()
    {
        foreach (var scale in new Vector3[] { new(-2, 1, 3), new(1, -1, 1), new(-1, -1, -1), new(2, 3, 4) })
        {
            var m = new ObjectTransform(new(1, 2, 3), new(10, 30, 50), scale).Matrix();
            var back = ObjectTransform.FromMatrix(m).Matrix();
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) Assert.True(MathF.Abs(back[i, j] - m[i, j]) < 1e-4f, $"{scale}: {back} is not {m}");
        }
    }

    [Fact]
    public async Task OnlyTheChangedComponentsOfAScriptValueChange()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        // The tank's X comes from a macro: moving it in Y leaves the macro alone.
        string script = Text(workspace, "gamegen/m1.gs");
        workspace.Apply("Macro", [("gamegen/m1.gs", Encoding.Latin1.GetBytes(script.Replace("Object3DTranslate 100.0 0.0 -50.0", "set tx 100.0\r\nObject3DTranslate %tx% 0.0 -50.0")))], Token);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var tank = Target(workspace, "m1", build, world, "tank_at");
        var shown = ObjectTransform.Of(tank.Node);
        Assert.Equal(100f, shown.Position.X, 3);
        var plan = SourceObjectEdits.PlanTransform(workspace, "tank_at", tank.Origin, build.Executions, shown with { Position = shown.Position with { Y = 5 } }, Token, "m1", shown);
        Assert.Contains("Object3DTranslate %tx% 5.0 -50.0", Encoding.Latin1.GetString(Assert.Single(plan.Changes).Content));
        // Changing the macro's own component is still refused.
        Assert.Contains("macro", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "tank_at", tank.Origin, build.Executions, shown with { Position = shown.Position with { X = 7 } }, Token, "m1", shown)).Message);
    }

    [Fact]
    public async Task ScriptMirrorsAndSmallMovesAreWrittenAsShown()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(2500, 0, -50)), []), Token);
        // The script mirrors the tank in Y; it shows as the script stores it.
        string script = Text(workspace, "gamegen/m1.gs");
        workspace.Apply("Mirror", [("gamegen/m1.gs", Encoding.Latin1.GetBytes(script.Replace("Object3DTranslate 2500.0 0.0 -50.0", "Object3DTranslate 2500.0 0.0 -50.0\r\nObject3DScale 1.0 -1.0 1.0")))], Token);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var tank = Target(workspace, "m1", build, world, "tank_at");
        var shown = ObjectTransform.Of(tank.Node);
        Assert.Equal(new Vector3(1, -1, 1), shown.Scale); Assert.Equal(Vector3.Zero, shown.RotationDegrees);
        // Turning it adds the rotation and keeps the script's scale (the Euler angles and scale animations start from).
        var turned = shown with { RotationDegrees = shown.RotationDegrees with { Y = 45 } };
        var turning = SourceObjectEdits.PlanTransform(workspace, "tank_at", tank.Origin, build.Executions, turned, Token, "m1", shown);
        string turnedScript = Encoding.Latin1.GetString(Assert.Single(turning.Changes).Content);
        Assert.Contains("Object3DScale 1.0 -1.0 1.0\r\nObject3DRotate 0.0 45.0 0.0\r\n", turnedScript);
        Apply(workspace, turning);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var built = WorldUpdate.LocalMatrix(world.Nodes.Single(n => n.Name == "tank_at"))!.Value;
        var expected = turned.Matrix();
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) Assert.True(MathF.Abs(built[i, j] - expected[i, j]) < 1e-4f, $"{built} is not {expected}");
        // A small move far from the origin is written, not lost in a relative tolerance.
        tank = Target(workspace, "m1", build, world, "tank_at");
        shown = ObjectTransform.Of(tank.Node);
        var nudged = SourceObjectEdits.PlanTransform(workspace, "tank_at", tank.Origin, build.Executions, shown with { Position = shown.Position with { X = 2500.02f } }, Token, "m1", shown);
        Assert.Contains("Object3DTranslate 2500.02 0.0 -50.0", Encoding.Latin1.GetString(Assert.Single(nudged.Changes).Content));
        // Even a thousandth there (two float steps) is a change.
        var tiny = SourceObjectEdits.PlanTransform(workspace, "tank_at", tank.Origin, build.Executions, shown with { Position = shown.Position with { X = 2500.001f } }, Token, "m1", shown);
        Assert.Contains("Object3DTranslate 2500.001 0.0 -50.0", Encoding.Latin1.GetString(Assert.Single(tiny.Changes).Content));
    }

    [Fact]
    public async Task ReparentingKeepsTheStoredRotationAndScale()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        // A rotation outside the decomposition's range and a Y mirror, as an animation would start from them.
        string script = Text(workspace, "gamegen/m1.gs");
        workspace.Apply("Pose", [("gamegen/m1.gs", Encoding.Latin1.GetBytes(script.Replace("Object3DTranslate 100.0 0.0 -50.0", "Object3DTranslate 100.0 0.0 -50.0\r\nObject3DRotate 120.0 0.0 0.0\r\nObject3DScale 1.0 -1.0 1.0")))], Token);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var before = WorldUpdate.LocalMatrix(world.Nodes.Single(n => n.Name == "tank_at"))!.Value;
        // Under the ground (which only sits in the world), the place is kept and the rotation and scale lines stay as written.
        var plan = SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "tank_at"), world.Nodes.Single(n => n.Name == "ground"), Token);
        string moved = Encoding.Latin1.GetString(Assert.Single(plan.Changes).Content);
        Assert.Contains("Object3DRotate 120.0 0.0 0.0\r\nObject3DScale 1.0 -1.0 1.0\r\n", moved);
        Apply(workspace, plan);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var tank = world.Nodes.Single(n => n.Name == "tank_at");
        Assert.Equal("ground", Assert.Single(tank.Parents).Name);
        var after = WorldUpdate.LocalMatrix(tank)!.Value;
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) Assert.True(MathF.Abs(after[i, j] - before[i, j]) < 1e-3f, $"{after} is not {before}");
        // A nudge of two thousandths at x = 100 is a change too.
        var shown = ObjectTransform.Of(Target(workspace, "m1", build, world, "tank_at").Node);
        var nudged = SourceObjectEdits.PlanTransform(workspace, "tank_at", Target(workspace, "m1", build, world, "tank_at").Origin, build.Executions, shown with { Position = shown.Position with { X = 100.002f } }, Token, "m1", shown);
        Assert.Contains("Object3DTranslate 100.002 0.0 -50.0", Encoding.Latin1.GetString(Assert.Single(nudged.Changes).Content));
    }

    [Fact]
    public async Task ReparentingUnderATurnedParentKeepsTheScaleAndTheWorldPlace()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        // A pivot turned a quarter about Y and moved; the tank is mirrored in Y and turned about X.
        string script = Text(workspace, "gamegen/m1.gs");
        workspace.Apply("Pose", [("gamegen/m1.gs", Encoding.Latin1.GetBytes(script.Replace("Object3DTranslate 100.0 0.0 -50.0",
            "Object3DTranslate 100.0 0.0 -50.0\r\nObject3DRotate 120.0 0.0 0.0\r\nObject3DScale 1.0 -1.0 1.0\r\nNewObject3D pivot\r\nObject3DTranslate 10.0 0.0 5.0\r\nObject3DRotate 0.0 90.0 0.0\r\nFindNode world\r\nAddChild pivot")))], Token);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var before = WorldUpdate.LocalMatrix(world.Nodes.Single(n => n.Name == "tank_at"))!.Value;
        var plan = SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "tank_at"), world.Nodes.Single(n => n.Name == "pivot"), Token);
        string moved = Encoding.Latin1.GetString(Assert.Single(plan.Changes).Content);
        // The scale stays as written; the rotation takes the pivot's turn.
        Assert.Contains("Object3DScale 1.0 -1.0 1.0\r\n", moved);
        Assert.DoesNotContain("Object3DRotate 120.0 0.0 0.0\r\n", moved);
        Apply(workspace, plan);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var tank = world.Nodes.Single(n => n.Name == "tank_at");
        Assert.Equal("pivot", Assert.Single(tank.Parents).Name);
        var after = WorldUpdate.LocalMatrix(tank)!.Value * WorldUpdate.LocalMatrix(world.Nodes.Single(n => n.Name == "pivot"))!.Value;
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) Assert.True(MathF.Abs(after[i, j] - before[i, j]) < 1e-3f, $"{after} is not {before}");
    }

    [Fact]
    public void GltfNodeEditsRenumberEveryReference()
    {
        var root = JsonNode.Parse("""
            { "scene": 0, "scenes": [ { "nodes": [0, 3] } ],
              "nodes": [ { "name": "a", "children": [1, 2] }, { "name": "b", "translation": [1, 0, 0] }, { "name": "c", "extras": { "recoil": { "instance": 4 } } }, { "name": "d" } ],
              "animations": [ { "channels": [ { "target": { "node": 3, "path": "translation" } } ] } ] }
            """)!.AsObject();
        // A copy of a with its children: new instance numbers for shared parts, placed beside a among the scene roots.
        int copy = GltfNodeEdits.Duplicate(root, 0, "a2");
        Assert.Equal(4, copy);
        Assert.Equal([0, 4, 3], root["scenes"]![0]!["nodes"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal([5, 6], root["nodes"]![4]!["children"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal(5, root["nodes"]![6]!["extras"]!["recoil"]!["instance"]!.GetValue<long>());
        // Removing a renumbers the rest, including the animation target.
        GltfNodeEdits.Remove(root, 0);
        Assert.Equal(["d", "a2", "b", "c"], root["nodes"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()));
        Assert.Equal([1, 0], root["scenes"]![0]!["nodes"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal(0, root["animations"]![0]!["channels"]![0]!["target"]!["node"]!.GetValue<int>());
        Assert.Equal([2, 3], root["nodes"]![1]!["children"]!.AsArray().Select(n => n!.GetValue<int>()));
        // An animated node cannot be removed; re-parenting keeps the world place.
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Remove(root, 0));
        root["nodes"]![1]!["translation"] = new JsonArray(10f, 0f, 0f);
        GltfNodeEdits.Reparent(root, 2, 0);
        Assert.Equal(new Vector3(11, 0, 0), GltfNodeEdits.World(root, 2).Translation);
        Assert.Equal(new Vector3(11, 0, 0), GltfNodeEdits.Local(root["nodes"]![2]!.AsObject()).Translation);
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Reparent(root, 1, 3));
    }

    [Fact]
    public async Task AMovedObjectKeepsTheZoneItWasBuiltWith()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteTerrainDatabase();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        // sky has no zone of its own (the database's 0xFF reaches it); under ground (zone 3) it would take 3.
        Assert.Equal(0xFFu, world.Nodes.Single(n => n.Name == "sky").Zone & 0xFF);
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "sky"), world.Nodes.Single(n => n.Name == "ground"), Token));
        (_, world) = await BuildAsync(fixture, workspace, "m1");
        var sky = world.Nodes.Single(n => n.Name == "sky");
        Assert.Equal("ground", Assert.Single(sky.Parents).Name);
        Assert.Equal(0xFFu, sky.Zone & 0xFF);
    }

    [Fact]
    public async Task MembersOfDeletedGroupsAreAlreadyUnderTheWorld()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteTerrainDatabase(grouped: true);
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        // The build deletes g1 and its pieces join the world: moving one there changes nothing in the world.
        Assert.Equal([WorldNode(world)], world.Nodes.Single(n => n.Name == "flat_a").Parents);
        Assert.Contains("already a root of the world", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "flat_a"), null, Token)).Message);
    }

    [Fact]
    public void ReparentingKeepsTheZoneANodeInherited()
    {
        // g sets zone 0, which o (no zone of its own) and o's child take; r is a root with zone 3; w has no zone either.
        var root = JsonNode.Parse("""
            { "scene": 0, "scenes": [ { "nodes": [0, 2, 4] } ], "nodes": [
              { "name": "g", "children": [1], "extras": { "recoil": { "zone": 0 } } },
              { "name": "o", "children": [3] },
              { "name": "r", "extras": { "recoil": { "zone": 3 } } },
              { "name": "c" },
              { "name": "w" },
              { "name": "v" } ] }
            """)!.AsObject();
        root["scenes"]![0]!["nodes"]!.AsArray().Add(5);
        int? Zone(int node) => root["nodes"]![node]!["extras"]?["recoil"]?["zone"]?.GetValue<int>();
        // To the top it would take the database's 0xFF: it keeps zone 0, and so does its child.
        GltfNodeEdits.Reparent(root, 1, null);
        Assert.Equal(0, Zone(1)); Assert.Null(Zone(3));
        // A root that took its zone from outside the file keeps the built one under a node with another zone.
        GltfNodeEdits.Reparent(root, 4, 2, 0xFF);
        Assert.Equal(0xFF, Zone(4));
        // Without the built zone (a part whose copies take different ones), a move that would replace it is refused.
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Reparent(root, 5, 2));
        // A node with a zone of its own keeps it unchanged.
        GltfNodeEdits.Reparent(root, 2, 0);
        Assert.Equal(3, Zone(2));
    }
}
