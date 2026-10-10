using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceDuplicateBatchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("holders")]
    [InlineData("siblings")]
    [InlineData("roots")]
    public void WideCopiesUseLinearWorkAndKeepEveryAdjacencyOrder(string layout)
    {
        long small = Copy(256), large = Copy(1024);
        Assert.InRange(large, 1, small * 4 + 16);

        long Copy(int count)
        {
            var (root, selected) = Wide(count, layout);
            var nodes = root["nodes"]!.AsArray();
            int before = nodes.Count;
            var original = nodes.Select(n => n!.ToJsonString()).ToArray();
            var oldChildren = nodes.Select(n => n!["children"]?.AsArray().Select(v => v!.GetValue<int>()).ToArray()).ToArray();
            var oldScenes = root["scenes"]!.AsArray().Select(s => s!["nodes"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray()).ToArray();
            Dictionary<long, long> markers = [];
            long visits = 0;
            var copies = GltfNodeEdits.Duplicate(root, selected, "copy", markers, Token, () => visits++);
            Assert.Equal(before + count, nodes.Count);
            Assert.Equal(Enumerable.Range(before, count), copies);
            Assert.Equal(2L, markers[1]);
            var beside = selected.Zip(copies).ToDictionary(p => p.First, p => p.Second);
            for (int i = 0; i < before; i++)
            {
                if (oldChildren[i] is { } children)
                    Assert.Equal(Expand(children), nodes[i]!["children"]!.AsArray().Select(v => v!.GetValue<int>()));
                else Assert.Equal(original[i], nodes[i]!.ToJsonString());
            }
            for (int i = 0; i < oldScenes.Length; i++)
                Assert.Equal(Expand(oldScenes[i]), root["scenes"]![i]!["nodes"]!.AsArray().Select(v => v!.GetValue<int>()));
            foreach (int copy in copies)
            {
                Assert.Equal("copy", nodes[copy]!["name"]!.GetValue<string>());
                Assert.Equal("copy", nodes[copy]!["extras"]!["recoil"]!["name"]!.GetValue<string>());
                Assert.Equal(2L, nodes[copy]!["extras"]!["recoil"]!["instance"]!.GetValue<long>());
            }
            _ = Read(root);
            // Linear in original nodes, selected leaves and authored edges; no timing/CPU-speed dependency.
            Assert.InRange(visits, 1, 30L * (before + selected.Length));
            return visits;

            IEnumerable<int> Expand(IEnumerable<int> values)
            {
                foreach (int value in values)
                {
                    yield return value;
                    if (beside.TryGetValue(value, out int copy)) yield return copy;
                }
            }
        }
    }

    [Fact]
    public void NestedMarkerHolesAndAllScenesHaveIndependentExpectedOrder()
    {
        var root = JsonNode.Parse("""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0,3,6,7]},{"nodes":[3,0]}],"nodes":[
              {"name":"holderA","children":[1,2]},
              {"name":"same","extras":{"recoil":{"instance":2147483647}}},
              {"name":"same","extras":{"recoil":{"instance":2147483646}}},
              {"name":"holderB","children":[4,5]},
              {"name":"same","extras":{"recoil":{"instance":2147483647}}},
              {"name":"same","extras":{"recoil":{"instance":2147483646}}},
              {"name":"occupied","extras":{"recoil":{"instance":1}}},
              {"name":"occupied","extras":{"recoil":{"instance":3}}}]}
            """)!.AsObject();
        Dictionary<long, long> markers = [];
        var copies = GltfNodeEdits.Duplicate(root, new[] { 3, 0 }, "copy", markers, Token);
        Assert.Equal(new[] { 8, 11 }, copies);
        Assert.Equal(new[] { 0, 11, 3, 8, 6, 7 }, Indices(root["scenes"]![0]!["nodes"]!));
        Assert.Equal(new[] { 3, 8, 0, 11 }, Indices(root["scenes"]![1]!["nodes"]!));
        Assert.Equal(new[] { 9, 10 }, Indices(root["nodes"]![8]!["children"]!));
        Assert.Equal(new[] { 12, 13 }, Indices(root["nodes"]![11]!["children"]!));
        Assert.Equal(2L, markers[int.MaxValue]); Assert.Equal(4L, markers[int.MaxValue - 1L]);
        var imported = WorldGltf.Import(Read(root), "model.gltf", 255,
            new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, name, _) => name!, Token = Token });
        var first = imported[1]; var second = imported[3];
        Assert.Equal("copy", first.Name); Assert.Equal("copy", second.Name);
        Assert.Same(first.Children[0], second.Children[0]); Assert.Same(first.Children[1], second.Children[1]);
        Assert.NotSame(first.Children[0], first.Children[1]);
        Assert.NotSame(imported[0].Children[0], first.Children[0]);
    }

    [Fact]
    public void CancellationDuringPreparationLeavesJsonAndSharedMarkerMapUntouched()
    {
        var (baseline, selection) = Wide(64, "siblings");
        int total = 0;
        GltfNodeEdits.Duplicate(baseline, selection, "copy", new() { [123] = 9 }, Token, () => total++);
        foreach (int stop in new[] { 1, total / 2, total - 1, total })
        {
            var (root, selected) = Wide(64, "siblings");
            string before = root.ToJsonString();
            Dictionary<long, long> map = new() { [123] = 9 };
            using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
            int visits = 0;
            Assert.ThrowsAny<OperationCanceledException>(() =>
                GltfNodeEdits.Duplicate(root, selected, "copy", map, cancel.Token, () => { if (++visits == stop) cancel.Cancel(); }));
            Assert.Equal(before, root.ToJsonString());
            Assert.Equal(new KeyValuePair<long, long>(123, 9), Assert.Single(map));
        }
    }

    [Fact]
    public void CapacityAndOverlappingSelectionsRefuseBeforeCloningOrChangingTheMap()
    {
        var (root, _) = Wide(2, "holders");
        string before = root.ToJsonString();
        Dictionary<long, long> map = [];
        Assert.Contains("exceed", Assert.Throws<InvalidDataException>(() =>
            GltfNodeEdits.Duplicate(root, new[] { 1 }, "copy", map, Token, null, maximumNodes: 4)).Message);
        Assert.Equal(before, root.ToJsonString()); Assert.Empty(map);
        Assert.Single(GltfNodeEdits.Duplicate(root, new[] { 1 }, "copy", map, Token, null, maximumNodes: 5));
        Assert.Equal(5, root["nodes"]!.AsArray().Count);
        map.Clear();

        var (small, _) = Wide(2, "holders");
        before = small.ToJsonString();
        Assert.Contains("disjoint", Assert.Throws<InvalidDataException>(() =>
            GltfNodeEdits.Duplicate(small, new[] { 0, 1 }, "copy", map, Token)).Message);
        Assert.Equal(before, small.ToJsonString()); Assert.Empty(map);
        Assert.Single(GltfNodeEdits.Duplicate(small, new[] { 1 }, "copy", map, Token));
        Assert.Equal(2L, map[1]);
    }

    [Fact]
    public void ImplicitSceneKeepsExistingRootOrderAndAppendsCopiesInSelectionOrder()
    {
        var (root, _) = Wide(3, "roots");
        root.Remove("scene"); root.Remove("scenes");
        var copies = GltfNodeEdits.Duplicate(root, new[] { 2, 0 }, "copy", token: Token);
        Assert.Equal(new[] { 3, 4 }, copies);
        Assert.False(root.ContainsKey("scenes"));
        Assert.Equal(new[] { "leaf", "leaf", "leaf", "copy", "copy" }, Read(root).Roots.Select(n => n.Name));
        Assert.Equal(2L, root["nodes"]![3]!["extras"]!["recoil"]!["instance"]!.GetValue<long>());
        Assert.Equal(2L, root["nodes"]![4]!["extras"]!["recoil"]!["instance"]!.GetValue<long>());
    }

    [Fact]
    public void LateSceneRefusalAndInvalidMapLeaveNoPartlyPublishedCopies()
    {
        var (root, selected) = Wide(3, "roots");
        root["scenes"]!.AsArray().Add(new JsonObject { ["nodes"] = new JsonArray(100) });
        string before = root.ToJsonString();
        Dictionary<long, long> map = [];
        Assert.Contains("no node 100", Assert.Throws<InvalidDataException>(() =>
            GltfNodeEdits.Duplicate(root, selected, "copy", map, Token)).Message);
        Assert.Equal(before, root.ToJsonString()); Assert.Empty(map);
        root["scenes"]!.AsArray().RemoveAt(1);
        before = root.ToJsonString(); map[1] = 0;
        Assert.Contains("positive Int32", Assert.Throws<InvalidDataException>(() =>
            GltfNodeEdits.Duplicate(root, selected, "copy", map, Token)).Message);
        Assert.Equal(before, root.ToJsonString()); Assert.Equal(0L, map[1]);
        map.Clear();
        Assert.Equal(3, GltfNodeEdits.Duplicate(root, selected, "copy", map, Token).Count);
    }

    [Fact]
    public void PublicPlanPreservesSharedShearedBasisAndUndoRestoresExactSource()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteSharedDatabase();
        const string path = "data/m1/models/m1.gltf";
        var source = JsonNode.Parse(File.ReadAllBytes(fixture.Path(path)))!.AsObject();
        Matrix4x4 basis = Matrix4x4.Identity; basis.M12 = .25f; basis.M41 = 2;
        foreach (var node in source["nodes"]!.AsArray().OfType<JsonObject>().Where(n => n["name"]?.GetValue<string>() == "gate"))
            GltfNodeEdits.SetLocal(node, basis);
        fixture.Write(path, source.ToJsonString());
        byte[] original = File.ReadAllBytes(fixture.Path(path));
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var world = assembler.Assemble("m1.gs");
        var gate = world.Nodes.Single(n => n.Name == "gate");
        var requested = ObjectTransform.Of(gate) with { Position = new(3, 4, 5) };
        var plan = SourceObjectEdits.PlanDuplicate(new(workspace, "m1", world, gate, assembler.Provenance, assembler.Executions),
            "gate2", requested, Token, keepBasis: true);
        Assert.False(workspace.IsDirty); Assert.Equal(original, workspace.Read(path, Token));
        Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
        WorldAssembler rebuilt = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
        var after = rebuilt.Assemble("m1.gs");
        var copied = after.Nodes.Single(n => n.Name == "gate2");
        Assert.Equal(basis with { Translation = requested.Position }, WorldUpdate.LocalMatrix(copied)!.Value);
        Assert.Same(after.Nodes.Single(n => n.Name == "gate").Parents[0], Assert.Single(copied.Parents));
        Assert.NotEmpty(GameZWriter.Write(after, Token));
        workspace.Undo();
        Assert.False(workspace.IsDirty); Assert.Equal(original, workspace.Read(path, Token));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path(path)));
        workspace.Redo();
        Assert.True(workspace.IsDirty);
        Assert.Single(new WorldAssembler(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token).Assemble("m1.gs").Nodes, n => n.Name == "gate2");
    }

    private static int[] Indices(JsonNode node) => node.AsArray().Select(n => n!.GetValue<int>()).ToArray();
    private static GltfDocument Read(JsonObject root) =>
        GltfDocument.Read(Encoding.UTF8.GetBytes(root.ToJsonString()), _ => throw new InvalidOperationException(), Token);

    private static (JsonObject Root, int[] Selected) Wide(int count, string layout)
    {
        JsonArray nodes = [], roots = []; List<int> selected = [];
        if (layout == "siblings") { nodes.Add(new JsonObject { ["name"] = "holder", ["children"] = new JsonArray() }); roots.Add(0); }
        for (int i = 0; i < count; i++)
        {
            int index = nodes.Count;
            if (layout == "holders")
            {
                roots.Add(index);
                nodes.Add(new JsonObject { ["name"] = "holder" + i, ["children"] = new JsonArray(index + 1) });
                index++;
            }
            else if (layout == "siblings") nodes[0]!["children"]!.AsArray().Add(index);
            else roots.Add(index);
            selected.Add(index);
            nodes.Add(new JsonObject { ["name"] = "leaf", ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["instance"] = 1 } } });
        }
        return (new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["nodes"] = nodes,
            ["scene"] = 0, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = roots }) }, selected.ToArray());
    }
}
