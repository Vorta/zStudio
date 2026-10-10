using System.Collections;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound22InferenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class CountedOrder(int[] entries) : IReadOnlyList<int>
    {
        public int Reads;
        public int Count => entries.Length;
        public int this[int index] { get { Reads++; return entries[index]; } }
        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)entries).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static (DatabaseRecords.Parts.Retained Retained, CountedOrder Order,
        Dictionary<int, WorldNode> Live, HashSet<int> Deleted) Suffixes(int count)
    {
        // The freed cache is a star. All but its last child lost their names when their slots were reused.
        // Every suffix matches those unknown children, then the last known name fails against a deleted slot.
        List<int> free = [0, .. Enumerable.Range(1, count).Reverse()];
        var retained = DatabaseRecords.Parts.Retained.Read(free,
            new Dictionary<int, string> { [0] = "root", [count] = "exists" }, [], Token)!;
        return (retained, new(Enumerable.Range(0, count + 1).ToArray()),
            new() { [count + 2] = new("exists", WorldNodeClass.Object3D) }, [.. Enumerable.Range(1, count)]);
    }

    [Fact]
    public void RetainedSuffixFailuresAreChargedBeforeTheyReadSlots()
    {
        var fixture = Suffixes(100);
        var live = fixture.Live.Values.Single();
        WorldNode a = new("a", WorldNodeClass.Object3D), b = new("b", WorldNodeClass.Object3D);
        live.Children.AddRange([b, a]);
        var error = Assert.Throws<InvalidDataException>(() => fixture.Retained.Continue(fixture.Order,
            fixture.Live, fixture.Deleted, new(64), Token));
        Assert.Contains("work limit", error.Message);
        Assert.InRange(fixture.Order.Reads, 1, 64);
        Assert.Equal([b, a], live.Children);
    }

    [Fact]
    public void RetainedBoundariesShareOneBudgetAndFailedMatchingDoesNotPoisonTheNextRun()
    {
        var fixture = Suffixes(4);
        DatabaseRecords.RetainedMatchBudget budget = new(25);
        Assert.Equal(0, fixture.Retained.Continue(fixture.Order, fixture.Live, fixture.Deleted, budget, Token)!.Value.Passed);
        int firstReads = fixture.Order.Reads;
        Assert.Throws<InvalidDataException>(() => fixture.Retained.Continue(fixture.Order, fixture.Live, fixture.Deleted, budget, Token));
        Assert.InRange(fixture.Order.Reads - firstReads, 0, 6);
        var retried = fixture.Retained.Continue(fixture.Order, fixture.Live, fixture.Deleted, new(25), Token);
        Assert.NotNull(retried);
        Assert.Equal(0, retried.Value.Passed);
        Assert.Equal(4, retried.Value.Reference.Children.Count);
    }

    [Fact]
    public void CanceledRetainedMatchingDoesNotReadOrCloneItsCandidates()
    {
        var fixture = Suffixes(100);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Retained.Continue(fixture.Order,
            fixture.Live, fixture.Deleted, new(), canceled.Token));
        Assert.Equal(0, fixture.Order.Reads);
    }

    private sealed class CountedIdentity(WorldNode[] nodes) : IEqualityComparer<WorldNode>
    {
        private readonly Dictionary<WorldNode, int> indices = nodes.Select((node, index) => (node, index)).ToDictionary(p => p.node, p => p.index);
        public int Hashes, Comparisons;
        public int GetHashCode(WorldNode value) { Hashes++; return indices[value]; }
        public bool Equals(WorldNode? a, WorldNode? b) { Comparisons++; return ReferenceEquals(a, b); }
    }

    [Fact]
    public void SharedWorldCellsFlattenInFirstOccurrenceOrderWithLinearMembershipWork()
    {
        WorldNode world = new("world", WorldNodeClass.World);
        WorldNode[] nodes = [.. Enumerable.Range(0, 4000).Select(i => new WorldNode("n" + i, WorldNodeClass.Object3D))];
        world.Children.AddRange([nodes[2], nodes[0], nodes[2]]);
        for (int i = 0; i < 40; i++)
        {
            WorldArea cell = new(); cell.Nodes.AddRange(nodes.Reverse()); world.Areas.Add(cell);
        }
        CountedIdentity identity = new(nodes);
        WorldDecomposer.FlattenAreas(world, Token, identity);
        Assert.Equal(new[] { nodes[2], nodes[0], nodes[2] }.Concat(nodes.Reverse().Where(n => n != nodes[2] && n != nodes[0])), world.Children);
        Assert.All(world.Areas, area => Assert.Empty(area.Nodes));
        // One membership lookup per original edge, not a search through the growing child list per cell edge.
        int edges = 40 * nodes.Length + 3;
        Assert.InRange(identity.Hashes, edges, 2 * edges);
        Assert.InRange(identity.Comparisons, 1, 2 * edges);
    }

    [Fact]
    public void DecompositionUsesTheSameAreaFlatteningAndHonorsCancellation()
    {
        GameZWorld world = new(); WorldNode root = new("world", WorldNodeClass.World);
        WorldNode a = new("same", WorldNodeClass.Object3D), b = new("same", WorldNodeClass.Object3D);
        world.Nodes.AddRange([root, a, b]); root.Children.Add(b);
        WorldArea cell = new(); cell.Nodes.AddRange([a, b, a]); root.Areas.Add(cell);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorldDecomposer.DecomposeAll(world, [], [], canceled.Token));
        Assert.Equal([b], root.Children); Assert.Equal([a, b, a], cell.Nodes);
        WorldDecomposer.DecomposeAll(world, [], [], Token);
        Assert.Equal([b, a], root.Children); Assert.Empty(cell.Nodes);
    }
}
