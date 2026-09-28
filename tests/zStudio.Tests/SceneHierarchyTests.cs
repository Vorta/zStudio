using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SceneHierarchyTests
{
    [Fact]
    public void HierarchyPreservesEdgesSharedReferencesAndAllDisconnectedComponents()
    {
        GameScene scene = new();
        Add("world", [1, 2]); Add("object3d", [3, 99]); Add("object3d", [3]); Add("object3d", []);
        Add("object3d", [5]); Add("object3d", [4]); Add("object3d", []); Add("object3d", [6]);
        scene.Nodes[0].Data["partitions"] = new JsonArray(new JsonArray(new JsonObject { ["node_indices"] = new JsonArray(2, 3) }));
        var hierarchy = new SceneHierarchy(scene);
        Assert.Equal(new[] { 0 }, hierarchy.Roots);
        Assert.Equal(new[] { 7, 4 }, hierarchy.UnlinkedRoots);
        Assert.Equal(new[] { 1, 2, 3 }, hierarchy.Children(0).Select(e => e.Node));
        Assert.Equal("world partition", hierarchy.Children(0)[2].Kind);
        Assert.Equal(new[] { 0, 1, 2 }, hierarchy.Parents(3));
        Assert.Equal(99, hierarchy.Children(1)[1].Node);
        Assert.Equal(new[] { 7, 6 }, hierarchy.PathTo(6));
        Assert.Equal(new[] { 4, 5 }, hierarchy.PathTo(5));
        Assert.Equal(new[] { 4 }, hierarchy.Children(5).Select(e => e.Node));
        Assert.Empty(hierarchy.PathTo(-1)); Assert.Empty(hierarchy.PathTo(99));
        void Add(string kind, int[] children) => scene.Nodes.Add(new(scene.Nodes.Count, "same name", kind, null, [], children, new(), new()));
    }
    [Fact]
    public void RootlessDeepGraphsAreIndexedWithoutRecursiveTraversal()
    {
        GameScene scene = new();
        for (int i = 0; i < 12000; i++) scene.Nodes.Add(new(i, "node", "object3d", null, [], i == 11999 ? [] : [i + 1], new(), new()));
        var hierarchy = new SceneHierarchy(scene);
        Assert.Equal(new[] { 0 }, hierarchy.Roots); Assert.Empty(hierarchy.UnlinkedRoots);
        Assert.Equal(12000, hierarchy.PathTo(11999).Count);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SceneHierarchy(scene, cancellation.Token));
    }
}
