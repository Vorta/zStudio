using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceDuplicateInstanceRangeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void MaximumMarkerDuplicatesThroughPublicPlanAndRebuildIntoAnUnusedHole()
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/models/m1.gltf";
        var root = JsonNode.Parse(File.ReadAllBytes(fixture.Path(path)))!.AsObject();
        var nodes = root["nodes"]!.AsArray();
        Mark(nodes[0]!.AsObject(), int.MaxValue);
        foreach (int marker in new[] { 1, 3 })
        {
            JsonObject other = new() { ["name"] = "same" }; Mark(other, marker);
            root["scenes"]![0]!["nodes"]!.AsArray().Add(nodes.Count); nodes.Add(other);
        }
        fixture.Write(path, root.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var before = assembler.Assemble("m1.gs");
        var ground = before.Nodes.Single(n => n.Name == "ground");
        var plan = SourceObjectEdits.PlanDuplicate(new(workspace, "m1", before, ground, assembler.Provenance, assembler.Executions), "ground_copy", null, Token);
        var updated = JsonNode.Parse(Assert.Single(plan.Changes).Content)!;
        var clone = updated["nodes"]!.AsArray().Single(n => n?["name"]?.GetValue<string>() == "ground_copy")!;
        Assert.Equal(2L, clone["extras"]!["recoil"]!["instance"]!.GetValue<long>());
        Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
        WorldAssembler rebuilt = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
        var after = rebuilt.Assemble("m1.gs");
        Assert.Single(after.Nodes, n => n.Name == "ground"); Assert.Single(after.Nodes, n => n.Name == "ground_copy");
        Assert.Equal(2, after.Nodes.Count(n => n.Name == "same"));
        Assert.NotEmpty(GameZWriter.Write(after, Token));
    }

    [Fact]
    public void SharedMapPreservesGroupsAcrossCopiesWithoutConfusingEqualNames()
    {
        var root = JsonNode.Parse("""
          {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0,4,5]}],"nodes":[
            {"name":"holder","children":[1,2,3]},
            {"name":"same","extras":{"recoil":{"instance":2147483647}}},
            {"name":"same","extras":{"recoil":{"instance":2147483647}}},
            {"name":"same","extras":{"recoil":{"instance":2147483646}}},
            {"name":"occupied","extras":{"recoil":{"instance":1}}},
            {"name":"occupied","extras":{"recoil":{"instance":3}}}]}
          """)!.AsObject();
        Dictionary<long, long> map = [];
        int copy1 = GltfNodeEdits.Duplicate(root, 0, "copy1", map);
        int copy2 = GltfNodeEdits.Duplicate(root, 0, "copy2", map);
        Assert.Equal(2, map.Count); Assert.Equal(2L, map[int.MaxValue]); Assert.Equal(4L, map[int.MaxValue - 1L]);
        var doc = GltfDocument.Read(Encoding.UTF8.GetBytes(root.ToJsonString()), _ => throw new InvalidOperationException(), Token);
        var imported = WorldGltf.Import(doc, "model.gltf", 255, new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, name, _) => name!, Token = Token });
        var first = imported.Single(n => n.Name == "copy1"); var second = imported.Single(n => n.Name == "copy2");
        // Import coalesces repeated edges to the same instance under one parent. The three source records
        // therefore become two children, while each of those children remains shared across the two copied holders.
        var records = root["nodes"]!.AsArray();
        var children = records[copy1]!["children"]!.AsArray().Select(i => records[i!.GetValue<int>()]!).ToArray();
        Assert.Equal(3, children.Length);
        Assert.Equal(2L, children[0]["extras"]!["recoil"]!["instance"]!.GetValue<long>());
        Assert.Equal(2L, children[1]["extras"]!["recoil"]!["instance"]!.GetValue<long>());
        Assert.Equal(4L, children[2]["extras"]!["recoil"]!["instance"]!.GetValue<long>());
        Assert.Equal(2, first.Children.Count); Assert.Equal(2, second.Children.Count);
        Assert.NotSame(first.Children[0], first.Children[1]);
        Assert.Same(first.Children[0], second.Children[0]);
        Assert.Same(first.Children[1], second.Children[1]);
        Assert.NotSame(imported.Single(n => n.Name == "holder").Children[0], first.Children[0]);
        Assert.NotSame(imported.Single(n => n.Name == "holder").Children[1], first.Children[1]);
        Assert.True(copy2 > copy1);
    }

    private static void Mark(JsonObject node, int marker)
    {
        if (node["extras"] is not JsonObject extras) node["extras"] = extras = new();
        if (extras["recoil"] is not JsonObject recoil) extras["recoil"] = recoil = new();
        recoil["instance"] = marker;
    }
}
