using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceHierarchyWorkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string File = "data/m1/models/m1.gltf";

    [Fact]
    public void SupportedSourceReparentPreservesPoseUnusedNumbersAndUndo()
    {
        using SourceWorldFixture fixture = new();
        var (root, leaf, target) = Document(12, 8);
        fixture.Write(File, root.ToJsonString());
        byte[] original = System.IO.File.ReadAllBytes(fixture.Path(File));
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var world = assembler.Assemble("m1.gs");
        Assert.NotEmpty(GameZWriter.Write(world, Token));
        var moving = world.Nodes.Single(n => n.Name == "moving");
        uint zone = moving.Zone;
        SourceObjectTarget selected = new(workspace, "m1", world, moving, assembler.Provenance, assembler.Executions);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SourceObjectEdits.PlanReparent(selected, world.Nodes.Single(n => n.Name == "target"), canceled.Token));
        Assert.False(workspace.IsDirty);
        var plan = SourceObjectEdits.PlanReparent(selected, world.Nodes.Single(n => n.Name == "target"), Token);
        byte[] changed = Assert.Single(plan.Changes).Content;
        var after = JsonNode.Parse(changed)!.AsObject();
        Assert.Equal(target, GltfNodeEdits.Parent(after, leaf, Token));
        Assert.Equal(new Vector3(3, 0, 0), GltfNodeEdits.World(after, leaf, Token).Translation);
        for (int i = 0; i < 24; i++) Assert.Equal(root["nodes"]![i]!.ToJsonString(), after["nodes"]![i]!.ToJsonString());
        Assert.NotNull(workspace.Apply(plan.Label, [(File, (byte[]?)changed)], Token));
        WorldAssembler rebuild = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
        var rebuilt = rebuild.Assemble("m1.gs");
        Assert.NotEmpty(GameZWriter.Write(rebuilt, Token));
        var moved = rebuilt.Nodes.Single(n => n.Name == "moving");
        Assert.Equal("target", Assert.Single(moved.Parents).Name);
        Assert.Equal(zone, moved.Zone);
        workspace.Undo();
        Assert.False(workspace.IsDirty);
        Assert.Equal(original, workspace.Read(File, Token));
        workspace.Redo();
        Assert.Equal(changed, workspace.Read(File, Token));
        Assert.Equal(original, System.IO.File.ReadAllBytes(fixture.Path(File)));
    }

    [Fact]
    public void AncestorWalksDoNotReparseUnrelatedNumericEdges()
    {
        var (root, leaf, target) = Document(12, 8);
        _ = GltfDocument.Read(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()),
            _ => throw new InvalidDataException("This fixture has no external buffers."), Token);
        int visits = 0;
        var hierarchy = new GltfNodeEdits.Hierarchy(root, Token, () => visits++);
        Assert.Equal(new Vector3(3, 0, 0), hierarchy.World(leaf).Translation);
        Assert.Equal(new Vector3(5, 0, 0), hierarchy.World(target).Translation);
        long charged = hierarchy.NumericBytes;
        int before = visits;
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(new Vector3(3, 0, 0), hierarchy.World(leaf).Translation);
            Assert.Null(hierarchy.FileZone(leaf));
        }
        Assert.Equal(charged, hierarchy.NumericBytes);
        Assert.InRange(visits - before, 1, 5 * 8 * 8); // Walk depth, not unrelated node/edge count.
        var larger = Document(24, 8);
        var twice = new GltfNodeEdits.Hierarchy(larger.Root, Token);
        twice.World(larger.Leaf);
        Assert.InRange(twice.NumericBytes, charged, charged * 2);
    }

    [Fact]
    public void ExactNumericAllowanceSucceedsAndOneLessRefusesWithoutChangingDom()
    {
        var (sample, leaf, target) = Document(3, 3);
        var measured = new GltfNodeEdits.Hierarchy(sample, Token);
        GltfNodeEdits.Reparent(measured, leaf, target);
        long needed = measured.NumericBytes;
        var (exact, _, _) = Document(3, 3);
        GltfNodeEdits.Reparent(new GltfNodeEdits.Hierarchy(exact, Token, maximumNumericWork: needed), leaf, target);
        Assert.True(JsonNode.DeepEquals(sample, exact));
        var (shortByOne, _, _) = Document(3, 3);
        string original = shortByOne.ToJsonString();
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Reparent(
            new GltfNodeEdits.Hierarchy(shortByOne, Token, maximumNumericWork: needed - 1), leaf, target));
        Assert.Equal(original, shortByOne.ToJsonString());
        GltfNodeEdits.Reparent(shortByOne, leaf, target, token: Token);
        Assert.True(JsonNode.DeepEquals(exact, shortByOne));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringIndexOrAncestorPreparationLeavesDomReusable(bool duringWalk)
    {
        var (root, leaf, target) = Document(4, 5);
        string original = root.ToJsonString();
        using var cancel = new CancellationTokenSource();
        bool armed = !duringWalk; int visited = 0;
        void Visit() { if (armed && ++visited == 4) cancel.Cancel(); }
        if (duringWalk)
        {
            var hierarchy = new GltfNodeEdits.Hierarchy(root, cancel.Token, Visit);
            armed = true;
            Assert.ThrowsAny<OperationCanceledException>(() => hierarchy.World(leaf));
            Assert.ThrowsAny<OperationCanceledException>(() => GltfNodeEdits.Reparent(hierarchy, leaf, target));
        }
        else Assert.ThrowsAny<OperationCanceledException>(() => new GltfNodeEdits.Hierarchy(root, cancel.Token, Visit));
        Assert.Equal(original, root.ToJsonString());
        GltfNodeEdits.Reparent(root, leaf, target, token: Token);
        Assert.Equal(target, GltfNodeEdits.Parent(root, leaf, Token));
    }

    [Fact]
    public void MutatedHierarchyCannotBeReusedAndConsecutiveMovesSeeCurrentParents()
    {
        var (root, leaf, target) = Document(2, 3);
        var hierarchy = new GltfNodeEdits.Hierarchy(root, Token);
        int first = hierarchy.Parent(leaf)!.Value;
        GltfNodeEdits.Reparent(hierarchy, leaf, target);
        Assert.Throws<InvalidOperationException>(() => hierarchy.Parent(leaf));
        Assert.Equal(target, GltfNodeEdits.Parent(root, leaf, Token));
        GltfNodeEdits.Reparent(root, leaf, first, token: Token);
        Assert.Equal(first, GltfNodeEdits.Parent(root, leaf, Token));
        Assert.Equal(new Vector3(3, 0, 0), GltfNodeEdits.World(root, leaf, Token).Translation);
        GltfNodeEdits.Remove(root, [0, 1], Token);
        Assert.Equal(first - 2, GltfNodeEdits.Parent(root, leaf - 2, Token));
        int copy = GltfNodeEdits.Duplicate(root, leaf - 2, "copy");
        Assert.Equal(first - 2, GltfNodeEdits.Parent(root, copy, Token));
    }

    [Fact]
    public void FirstParentAndInheritedZoneStayIndependentOfDuplicateNames()
    {
        var (root, leaf, target) = Document(0, 3);
        root["nodes"]![0]!["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["zone"] = 7 } };
        root["nodes"]![target]!["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["zone"] = 9 } };
        root["nodes"]![target]!["name"] = root["nodes"]![0]!["name"]!.DeepClone();
        GltfNodeEdits.Reparent(root, leaf, target, token: Token);
        Assert.Equal(7, root["nodes"]![leaf]!["extras"]!["recoil"]!["zone"]!.GetValue<int>());
        Assert.Equal(target, GltfNodeEdits.Parent(root, leaf, Token));
        // Direct DOM helper compatibility: first parent is node-index order, never name or last writer.
        root["nodes"]![0]!["children"]!.AsArray().Add(leaf);
        Assert.Equal(0, GltfNodeEdits.Parent(root, leaf, Token));
    }

    [Fact]
    public void InvalidDestinationAndLongOperandRefuseBeforeAnyMutation()
    {
        var (root, leaf, _) = Document(1, 3);
        root["scene"] = 99;
        string original = root.ToJsonString();
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Reparent(root, leaf, null, token: Token));
        Assert.Equal(original, root.ToJsonString());
        root["scene"] = 0;
        root["nodes"]![0]!["children"]![0] = JsonNode.Parse("1." + new string('0', 4095));
        original = root.ToJsonString();
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Reparent(root, leaf, null, token: Token));
        Assert.Equal(original, root.ToJsonString());
        root["nodes"]![0]!["children"]![0] = 1;
        GltfNodeEdits.Reparent(root, leaf, null, token: Token);
        Assert.Null(GltfNodeEdits.Parent(root, leaf, Token));
    }

    [Fact]
    public void SourceGroupRefusalUsesTheParentsAfterTheMove()
    {
        using SourceWorldFixture fixture = new();
        var (root, leaf, _) = Document(3, 4);
        root["nodes"]![leaf]!["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["group"] = true } };
        // A group below an ordinary object is retained. At the database top its nonidentity pose is forbidden.
        root["nodes"]![leaf]!.AsObject().Remove("translation");
        fixture.Write(File, root.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var world = assembler.Assemble("m1.gs");
        var moving = world.Nodes.Single(n => n.Name == "moving");
        SourceObjectTarget selected = new(workspace, "m1", world, moving, assembler.Provenance, assembler.Executions);
        Assert.Contains("group of the mission database", Assert.Throws<InvalidDataException>(() =>
            SourceObjectEdits.PlanReparent(selected, null, Token)).Message);
        Assert.False(workspace.IsDirty);
        Assert.Equal(root.ToJsonString(), System.Text.Encoding.UTF8.GetString(workspace.Read(File, Token)!));
        // A regular parent keeps the group below an object, so the same source remains editable after refusal.
        Assert.NotEmpty(SourceObjectEdits.PlanReparent(selected, world.Nodes.Single(n => n.Name == "target"), Token).Changes);
    }

    [Fact]
    public void SourceLodChecksBothParentPosesBeforeReparenting()
    {
        using SourceWorldFixture fixture = new();
        var (root, leaf, target) = Document(3, 4);
        root["nodes"]![leaf]!.AsObject().Remove("translation");
        root["nodes"]![leaf]!["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["class"] = "lod" } };
        var nodes = root["nodes"]!.AsArray();
        int bad = nodes.Count;
        var badNode = nodes[target]!.DeepClone(); badNode["name"] = "bad"; nodes.Add(badNode);
        root["scenes"]![0]!["nodes"]!.AsArray().Add(bad);
        nodes[target]!["translation"] = new JsonArray(2, 0, 0);
        fixture.Write(File, root.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var world = assembler.Assemble("m1.gs");
        var moving = world.Nodes.Single(n => n.Name == "moving");
        Assert.Equal(WorldNodeClass.Lod, moving.Class);
        SourceObjectTarget selected = new(workspace, "m1", world, moving, assembler.Provenance, assembler.Executions);
        Assert.Contains("level-of-detail", Assert.Throws<InvalidDataException>(() =>
            SourceObjectEdits.PlanReparent(selected, world.Nodes.Single(n => n.Name == "bad"), Token)).Message);
        Assert.False(workspace.IsDirty);
        var plan = SourceObjectEdits.PlanReparent(selected, world.Nodes.Single(n => n.Name == "target"), Token);
        var after = JsonNode.Parse(Assert.Single(plan.Changes).Content)!.AsObject();
        Assert.Equal(target, GltfNodeEdits.Parent(after, leaf, Token));
        Assert.Equal(new Vector3(2, 0, 0), GltfNodeEdits.World(after, leaf, Token).Translation);
    }

    private static (JsonObject Root, int Leaf, int Target) Document(int pairs, int depth)
    {
        JsonArray nodes = [];
        for (int i = 0; i < pairs; i++)
        {
            nodes.Add(new JsonObject { ["name"] = "unused", ["children"] = new JsonArray(JsonNode.Parse($"{nodes.Count + 1}." + new string('0', 125))) });
            nodes.Add(new JsonObject { ["name"] = "unused" });
        }
        int first = nodes.Count;
        for (int i = 0; i < depth; i++)
        {
            JsonObject node = new() { ["name"] = i == depth - 1 ? "moving" : $"chain{i}" };
            if (i < depth - 1) node["children"] = new JsonArray(first + i + 1);
            if (i == 0 || i == depth - 1) node["translation"] = new JsonArray(i == 0 ? 2 : 1, 0, 0);
            nodes.Add(node);
        }
        int target = nodes.Count;
        nodes.Add(new JsonObject { ["name"] = "target", ["translation"] = new JsonArray(5, 0, 0) });
        return (new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["nodes"] = nodes,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(first, target) }), ["scene"] = 0 }, first + depth - 1, target);
    }
}
