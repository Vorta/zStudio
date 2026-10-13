using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23NodeRemovalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void BulkDeletionKeepsNodeObjectsAndRenumbersEveryReferenceInOnePass()
    {
        var root = JsonNode.Parse("""
            {"nodes":[{"name":"a","children":[1,2],"extras":{"recoil":{"inlineSource":{"id":"part","count":1}}}},{"name":"remove","children":[3]},
              {"name":"keep"},{"name":"descendant"},{"name":"last"}],
             "scenes":[{"nodes":[0,4]},{"nodes":[1,2,4]}],
             "animations":[{"channels":[{"target":{"node":4,"path":"translation"}}]}],
             "skins":[{"joints":[0,2,4],"skeleton":0}]}
            """)!.AsObject();
        var nodes = root["nodes"]!.AsArray(); var keep = nodes[2]; var last = nodes[4];
        GltfNodeEdits.Remove(root, new[] { 1, 3 }, Token);
        Assert.Same(nodes, root["nodes"]); Assert.Same(keep, nodes[1]); Assert.Same(last, nodes[2]);
        Assert.Equal("[1]", nodes[0]!["children"]!.ToJsonString());
        Assert.Equal(0, nodes[0]!["extras"]!["recoil"]!["inlineSource"]!["count"]!.GetValue<int>());
        Assert.Equal("[0,2]", root["scenes"]![0]!["nodes"]!.ToJsonString());
        Assert.Equal("[1,2]", root["scenes"]![1]!["nodes"]!.ToJsonString());
        Assert.Equal(2, (int)root["animations"]![0]!["channels"]![0]!["target"]!["node"]!);
        Assert.Equal("[0,1,2]", root["skins"]![0]!["joints"]!.ToJsonString());
    }

    [Theory]
    [InlineData("copy-prefix")]
    [InlineData("copy-suffix")]
    [InlineData("move")]
    public void InlineSourceMembershipFollowsTheEditedPrefix(string edit)
    {
        var root = JsonNode.Parse("""
            {"nodes":[{"name":"holder","children":[1,2,3],"extras":{"recoil":{"inlineSource":{"id":"part","count":2}}}},
              {"name":"a"},{"name":"b"},{"name":"own"},
              {"name":"destination","children":[5],"extras":{"recoil":{"inlineSource":{"id":"other","count":1}}}},
              {"name":"c"}],"scenes":[{"nodes":[0,4]}],"scene":0}
            """)!.AsObject();
        if (edit == "move") GltfNodeEdits.Reparent(root, 1, 4, token: Token);
        else GltfNodeEdits.Duplicate(root, edit == "copy-prefix" ? 1 : 3, "copy");
        string[] Prefix(int index)
        {
            var owner = root["nodes"]![index]!;
            int count = owner["extras"]!["recoil"]!["inlineSource"]!["count"]!.GetValue<int>();
            return owner["children"]!.AsArray().Take(count).Select(n => root["nodes"]![n!.GetValue<int>()]!["name"]!.GetValue<string>()).ToArray();
        }
        Assert.Equal(edit == "move" ? ["b"] : edit == "copy-prefix" ? ["a", "copy", "b"] : new[] { "a", "b" }, Prefix(0));
        Assert.Equal(new[] { "c" }, Prefix(4));
        if (edit == "move") Assert.Equal(1, root["nodes"]![4]!["children"]![1]!.GetValue<int>());
    }

    [Fact]
    public void ManyAlternatingRootsDoNotAllocatePerDeletedNodeGraphScans()
    {
        JsonArray nodes = [], roots = [];
        for (int i = 0; i < 4000; i++) { nodes.Add(new JsonObject { ["name"] = $"n{i}" }); roots.Add(i); }
        JsonObject root = new() { ["nodes"] = nodes, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = roots }) };
        int[] selected = Enumerable.Range(0, 2000).Select(i => 2 * i).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        GltfNodeEdits.Remove(root, selected, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 8L * 1024 * 1024);
        Assert.Equal(2000, nodes.Count);
        Assert.Equal("n1", (string)nodes[0]!["name"]!); Assert.Equal("n3999", (string)nodes[^1]!["name"]!);
        Assert.Equal(1999, (int)root["scenes"]![0]!["nodes"]![1999]!);
    }

    [Fact]
    public void CanceledAndSharedDescendantRefusalsLeaveTheInputUntouched()
    {
        var root = JsonNode.Parse("""{"nodes":[{"children":[2]},{"children":[2]},{}],"scenes":[{"nodes":[0,1]}]}""")!.AsObject();
        string original = root.ToJsonString();
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Remove(root, new[] { 0 }, Token));
        Assert.Equal(original, root.ToJsonString());
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GltfNodeEdits.Remove(root, new[] { 0, 1 }, canceled.Token));
        Assert.Equal(original, root.ToJsonString());
        GltfNodeEdits.Remove(root, new[] { 0, 1 }, Token);
        Assert.Empty(root["nodes"]!.AsArray());
    }
}
