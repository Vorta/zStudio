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
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, mission, Path.Combine(fixture.Root, "p-" + Guid.NewGuid().ToString("N")), workspace.Overlay(), token: Token);
        Assert.Null(build.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        return (build, world);
    }
    private static SourceObjectTarget Target(SourceWorkspace workspace, string mission, SourceWorldBuild build, GameZWorld world, string name)
    {
        var slots = GameZWriter.NodeSlots(world);
        Dictionary<WorldNode, WorldNodeProvenance> provenance = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, slot) in slots) if (build.Provenance.TryGetValue(slot, out var origin)) provenance[node] = origin;
        var picked = world.Nodes.Single(n => n.Name == name);
        return new(workspace, mission, world, SourceObjectEdits.ObjectOf(picked, provenance), provenance, build.Executions);
    }
    private static void Apply(SourceWorkspace workspace, SourceEditPlan plan) => Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
    private static string Text(SourceWorkspace workspace, string path) => Encoding.Latin1.GetString(workspace.Read(path, Token)!);
    private static WorldNode WorldNode(GameZWorld world) => world.Nodes.Single(n => n.Class == WorldNodeClass.World);

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
        Assert.Contains("SetModelDirectory ..\\data\\m2\\models\\bft\r\nLoadGameGen tank.flt tank_b\r\nSetIntersectSurface on\r\nObject3DTranslate 0.0 0.0 30.0\r\nObject3DRotate 0.0 90.0 0.0\r\nFindNode world\r\nAddChild tank_b\r\nGameZWriteZBDFile", script);
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
        foreach (string line in new[] { "LoadGameGen tank.flt tank_at", "Object3DTranslate 100.0 0.0 -50.0", "SetIntersectSurface on", "AddChild tank_at" })
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
        var shown = ObjectTransform.FromMatrix(WorldUpdate.LocalMatrix(ground.Node)!.Value);
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
        shown = ObjectTransform.FromMatrix(WorldUpdate.LocalMatrix(ground.Node)!.Value);
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
        shown = ObjectTransform.FromMatrix(WorldUpdate.LocalMatrix(ground.Node)!.Value);
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
        var shown = ObjectTransform.FromMatrix(WorldUpdate.LocalMatrix(tank.Node)!.Value);
        Assert.Equal(100f, shown.Position.X, 3);
        var plan = SourceObjectEdits.PlanTransform(workspace, "tank_at", tank.Origin, build.Executions, shown with { Position = shown.Position with { Y = 5 } }, Token, "m1", shown);
        Assert.Contains("Object3DTranslate %tx% 5.0 -50.0", Encoding.Latin1.GetString(Assert.Single(plan.Changes).Content));
        // Changing the macro's own component is still refused.
        Assert.Contains("macro", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "tank_at", tank.Origin, build.Executions, shown with { Position = shown.Position with { X = 7 } }, Token, "m1", shown)).Message);
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
}
