using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class WorldWriterCancellationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void CancellableSlotProjectionPreservesFreedSlotAndNodeIdentities()
    {
        GameZWorld world = new();
        WorldNode first = new("same", WorldNodeClass.Object3D), second = new("same", WorldNodeClass.Object3D);
        world.Nodes.Add(first); world.Nodes.Add(second); world.FreedSlots.Add(1, new byte[196]);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GameZWriter.NodeSlots(world, canceled.Token));
        var slots = GameZWriter.NodeSlots(world, Token);
        Assert.Equal(0, slots[first]); Assert.Equal(2, slots[second]); Assert.Equal(2, slots.Count);
    }

    [Fact]
    public void HierarchyCancellationDuringEnumerationLeavesGraphUnchangedAndRetryStillChecksCycles()
    {
        WorldNode first = new("first", WorldNodeClass.Object3D), second = new("second", WorldNodeClass.Object3D);
        first.Children.Add(second); second.Parents.Add(first);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        IEnumerable<WorldNode> Nodes()
        {
            yield return first;
            canceled.Cancel();
            yield return second;
        }
        Assert.Throws<OperationCanceledException>(() => WorldUpdate.CheckHierarchy(Nodes(), canceled.Token));
        Assert.Same(second, Assert.Single(first.Children)); Assert.Same(first, Assert.Single(second.Parents));
        WorldUpdate.CheckHierarchy([first, second], Token);
        second.Children.Add(first);
        Assert.Throws<InvalidDataException>(() => WorldUpdate.CheckHierarchy([first, second], Token));
    }
}
