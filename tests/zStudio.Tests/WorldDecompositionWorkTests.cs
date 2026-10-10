using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldDecompositionWorkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ReaderAdmittedRemovedChildrenReturnInOriginalOrderWithReciprocalParents()
    {
        var world = ReadWorld();
        var trace = Trace();
        var work = new LookupWorkBudget(20_000, Token);
        var result = WorldDecomposer.DecomposeAll(world, trace, [], Token, new BoundedDiagnostics([]), work);
        var load = Assert.Single(result.Loads);
        Assert.Equal(["part0", "part1", "part2", "part3"], load.Content.Select(n => n.Name));
        Assert.All(load.Content, node => Assert.Same(load.Root, Assert.Single(node.Parents)));
        Assert.Same(world.Nodes[1], load.Root);
        Assert.InRange(work.UsedUnits, 1, 4000);
    }

    [Fact]
    public void AddedReversalRemovesFirstOccurrencesIndependentlyWithoutRepairingAsymmetry()
    {
        GameZWorld world = new();
        WorldNode root = new("world", WorldNodeClass.World), load = new("loaded", WorldNodeClass.Object3D), child = new("part", WorldNodeClass.Object3D), other = new("other", WorldNodeClass.Object3D);
        world.Nodes.AddRange([root, load, child, other]);
        load.Children.AddRange([child, other, child]);
        child.Parents.Add(load); // Deliberately asymmetric inspection graph: do not invent a second parent.
        other.Parents.Add(load);
        var result = WorldDecomposer.DecomposeAll(world,
            [Step("LoadGameGen", "loaded.flt", "loaded"), Step("AddChild", "part"), Step("AddChild", "part")], [], Token);
        Assert.Equal([other], Assert.Single(result.Loads).Content);
        Assert.Empty(child.Parents);
        Assert.Equal([load], other.Parents);
    }

    [Fact]
    public void ReverseRenameAndDuplicateNameLoadSlotsRemainDistinct()
    {
        GameZWorld world = new();
        WorldNode root = new("world", WorldNodeClass.World), first = new("loaded", WorldNodeClass.Object3D), second = new("loaded", WorldNodeClass.Object3D), part = new("after", WorldNodeClass.Object3D);
        world.Nodes.AddRange([root, first, second, part]);
        first.Children.Add(part); part.Parents.Add(first);
        var result = WorldDecomposer.DecomposeAll(world,
            [Step("LoadGameGen", "first.flt", "loaded"), Step("FindSubNode", "before"), Step("NodeSetDescription", "after"), Step("LoadGameGen", "second.flt", "loaded")], [], Token);
        Assert.Equal(2, result.Loads.Count);
        Assert.Same(first, result.Loads[0].Root); Assert.Same(second, result.Loads[1].Root);
        Assert.Equal("before", Assert.Single(result.Loads[0].Content).Name);
        Assert.Empty(result.Loads[1].Content);
    }

    [Fact]
    public void LowSharedAllowanceRefusesAndFreshRetryPreservesSourceBytes()
    {
        var world = ReadWorld();
        byte[] before = GameZWriter.Write(world, Token);
        Assert.Throws<InvalidDataException>(() => WorldDecomposer.DecomposeAll(world, Trace(), [], Token,
            new BoundedDiagnostics([]), new LookupWorkBudget(1, Token)));
        Assert.Equal(before, GameZWriter.Write(world, Token));
        Assert.Equal(4, Assert.Single(WorldDecomposer.DecomposeAll(ReadWorld(), Trace(), [], Token).Loads).Content.Count);
    }

    [Fact]
    public void AnInferredAncestorLinkIsRefusedWithoutChangingNamesLinksOrSlots()
    {
        var authored = ReadWorld();
        var load = authored.Nodes[1]; var parent = authored.Nodes[2]; var child = authored.Nodes[3];
        parent.Name = "after";
        load.Children.Add(parent); parent.Parents.Add(load);
        parent.Children.Add(child); child.Parents.Add(parent);
        // Start from an admitted acyclic binary graph; the archived removal cannot establish that an absent
        // ancestor edge ever existed. Reversal must refuse it without leaving a partial rename or a cycle.
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(authored, Token), token: Token), Token);
        byte[] before = GameZWriter.Write(world, Token);
        var error = Assert.Throws<InvalidDataException>(() => WorldDecomposer.DecomposeAll(world,
            [Step("LoadGameGen", "loaded.flt", "loaded"), Step("FindSubNode", "before"), Step("NodeSetDescription", "after"),
             Step("FindNode", "part1"), Step("DeleteChild", "loaded")], [], Token));
        Assert.Contains("own ancestor", error.Message);
        WorldUpdate.CheckHierarchy(world.Nodes, Token);
        Assert.Equal(before, GameZWriter.Write(world, Token));
        Assert.Equal(["after"], Assert.Single(WorldDecomposer.DecomposeAll(world,
            [Step("LoadGameGen", "loaded.flt", "loaded")], [], Token).Loads).Content.Select(n => n.Name));
    }

    [Fact]
    public void IndexedOperationsMatchListOrderAndFirstDuplicateRemoval()
    {
        WorldNode a = new("same", WorldNodeClass.Object3D), b = new("same", WorldNodeClass.Object3D), c = new("c", WorldNodeClass.Object3D);
        List<WorldNode> original = [a, b, a, c, b], expected = [.. original];
        var work = new LookupWorkBudget(10_000, Token);
        var actual = new OrderedWorldLinks(original, work, Token);
        expected.Remove(a); actual.RemoveFirst(a);
        expected.Remove(c); actual.RemoveFirst(c);
        expected.Insert(0, c); Assert.True(actual.PrependIfAbsent(c));
        Assert.False(actual.PrependIfAbsent(b));
        expected.Add(a); actual.Append(a);
        expected.Remove(b); actual.RemoveFirst(b);
        actual.CopyTo(original);
        Assert.Equal(expected, original);
        Assert.Same(b, original[2]);
    }

    [Fact]
    public void CancellationDuringIndexedReversalDoesNotChangeOriginalLists()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        WorldNode node = new("node", WorldNodeClass.Object3D);
        List<WorldNode> list = [node, node];
        var indexed = new OrderedWorldLinks(list, new(token: cancellation.Token), cancellation.Token);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => indexed.RemoveFirst(node));
        Assert.Throws<OperationCanceledException>(() => indexed.CopyTo(list));
        Assert.Equal([node, node], list);
    }

    [Fact]
    public void RepeatedRemovalsAndPrependsHaveLinearChargedWork()
    {
        WorldNode[] nodes = Enumerable.Range(0, 32).Select(i => new WorldNode("part" + i, WorldNodeClass.Object3D)).ToArray();
        var work = new LookupWorkBudget(10_000, Token);
        var indexed = new OrderedWorldLinks(nodes, work, Token);
        foreach (var node in nodes) indexed.RemoveFirst(node);
        for (int i = nodes.Length - 1; i >= 0; i--) Assert.True(indexed.PrependIfAbsent(nodes[i]));
        List<WorldNode> actual = []; indexed.CopyTo(actual);
        Assert.Equal(nodes, actual);
        Assert.Equal(32 * (128 + 1 + 1 + 128 + 8) + 1, work.UsedUnits);
    }

    private static GameZWorld ReadWorld()
    {
        GameZWorld world = new();
        world.Nodes.Add(new("world", WorldNodeClass.World));
        world.Nodes.Add(new("loaded", WorldNodeClass.Object3D));
        for (int i = 0; i < 4; i++) world.Nodes.Add(new("part" + i, WorldNodeClass.Object3D));
        return GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(world, Token), token: Token), Token);
    }

    private static TracedInstruction[] Trace() =>
    [
        Step("LoadGameGen", "loaded.flt", "loaded"),
        Step("DeleteChild", "part0"), Step("DeleteChild", "part1"),
        Step("DeleteChild", "part2"), Step("DeleteChild", "part3"),
        // The forward interpreter's DeleteChild of itself is a no-op. It must not manufacture a self-edge
        // while undoing the four genuine removals, or change their original order and reciprocal parents.
        Step("FindNode", "loaded"), Step("DeleteChild", "loaded"),
    ];
    private static TracedInstruction Step(string command, params string[] args) => new("m1.gs", command, args, [], null, []);
}
