using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class SceneTreeModelTests
{
    [Fact]
    public void SharedRowsCycleLeavesInvalidLinksAndDepthAreExplicit()
    {
        GameScene scene = new();
        Add([1, 2]); Add([3, 99]); Add([3]); Add([1]);
        var tree = new SceneTreeModel(scene, "fixture", true, new());
        var root = Assert.Single(tree.Roots);
        var first = root.Children[0].Children[0]; var second = root.Children[1].Children[0];
        Assert.True(first.Shared); Assert.NotEqual(first.Id, second.Id); Assert.Equal(first.Index, second.Index);
        Assert.Equal("cycle reference", first.Children[0].Problem); Assert.Empty(first.Children[0].Children);
        Assert.Equal("invalid reference", root.Children[0].Children[1].Problem);
        Assert.Null(root.Children[0].Children[1].Node);
        scene = new();
        for (int i = 0; i < 260; i++) Add(i == 259 ? [] : [i + 1]);
        tree = new(scene, "fixture", true, new()); var current = tree.Roots[0];
        for (int i = 0; i < SceneHierarchy.MaximumDepth; i++) current = Assert.Single(current.Children);
        Assert.Equal("hierarchy depth limit", current.Problem); Assert.Empty(current.Children);
        void Add(int[] children) => scene.Nodes.Add(new(scene.Nodes.Count, "same", scene.Nodes.Count == 0 ? "world" : "object3d", null, [], children, new(), new()));
    }
    [Fact]
    public void ExpansionAndSelectionFollowProvenanceRatherThanReusedCloneIndices()
    {
        var state = new SceneTreeState();
        var first = new SceneTreeModel(Scene(false), "fixture", true, state, i => new(i == 0 ? "world" : i == 1 ? "actor-A" : "actor-B", i, null));
        var selected = first.Reveal(1)!; first.Select(selected); selected.IsExpanded = true;
        var second = new SceneTreeModel(Scene(true), "fixture", true, state, i => new(i == 0 ? "world" : i == 2 ? "actor-A" : "actor-B", i, null));
        Assert.Equal(2, second.Selected?.Index); Assert.True(second.Selected?.IsExpanded);
        Assert.NotEqual(first.Context, second.Context); Assert.Null(second.Find(selected.Id));
        var other = new SceneTreeModel(Scene(false), "fixture", true, state, i => new("different:" + i, i, null));
        Assert.Null(other.Selected);
        static GameScene Scene(bool reverse)
        {
            GameScene data = new(); data.Nodes.Add(new(0, "world", "world", null, [], reverse ? [2, 1] : [1, 2], new(), new()));
            data.Nodes.Add(new(1, "same", "object3d", null, [0], [], new(), new())); data.Nodes.Add(new(2, "same", "object3d", null, [0], [], new(), new())); return data;
        }
    }
}
