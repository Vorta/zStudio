using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class DatabaseModelSharingBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RepeatedEmptyReferencesDoNotAllocateSubtreeWalksForEveryPair()
    {
        WorldNode[] nodes = [.. Enumerable.Range(0, 1000).Select(_ => new WorldNode("empty.flt", WorldNodeClass.Object3D))];
        int walks = 0;
        IEnumerable<WorldNode> Below(WorldNode node) { walks++; return WorldAssembler.Subtree(node); }
        DatabaseRecords.ModelSharing comparison = new(Below, new(), Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i < nodes.Length; i++)
            for (int j = 0; j < i; j++) Assert.False(comparison.Shares(nodes[j], nodes[i]));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(walks, 1, nodes.Length);
        Assert.InRange(allocated, 0, 8L << 20); // Former per-pair walks allocated about 280 MB for these 499,500 pairs.
    }

    [Fact]
    public void IdentityMatchingRetainsSharedModelsAndBudgetsUnrelatedRepeatedNames()
    {
        WorldModel shared = new(), different = new();
        WorldNode first = new("same.flt", WorldNodeClass.Object3D), second = new("same.flt", WorldNodeClass.Object3D), third = new("same.flt", WorldNodeClass.Object3D);
        first.Children.Add(new("mesh", WorldNodeClass.Object3D) { Model = shared });
        second.Children.Add(new("mesh", WorldNodeClass.Object3D) { Model = shared });
        third.Children.Add(new("mesh", WorldNodeClass.Object3D) { Model = different });
        DatabaseRecords.ModelSharing comparison = new(WorldAssembler.Subtree, new(), Token);
        Assert.True(comparison.Shares(first, second));
        Assert.False(comparison.Shares(first, third));
        DatabaseRecords.ModelSharing refused = new(WorldAssembler.Subtree, new(1), Token);
        Assert.Contains("work limit", Assert.Throws<InvalidDataException>(() => refused.Shares(first, second)).Message);
        Assert.Equal(shared, Assert.Single(first.Children).Model);
    }

    [Fact]
    public void ExhaustedBudgetPreventsAnotherSubtreeEnumeration()
    {
        int visits = 0;
        IEnumerable<WorldNode> Below(WorldNode node) { visits++; return [node]; }
        WorldNode empty = new("empty", WorldNodeClass.Object3D);
        DatabaseRecords.ModelSharing comparison = new(Below, new(3), Token);
        Assert.False(comparison.Shares(empty, empty));
        Assert.Equal(1, visits);
        Assert.Throws<InvalidDataException>(() => comparison.Shares(new("second", WorldNodeClass.Object3D), empty));
        Assert.Equal(1, visits);
    }
}
