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
