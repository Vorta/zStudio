using System.Numerics;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23WorldBoundsTests
{
    [Fact]
    public void SharedPointAndMorphGeometryIsMeasuredOnceWithIndependentBounds()
    {
        GameZWorld world = new();
        WorldModel model = new();
        model.Vertices.AddRange([new(-2, 1, 3), new(4, -1, 0)]);
        model.Morphs.AddRange([new(-8, -5, -11), new(8, 10, 6)]);
        model.Points.Add(new() { Vertices = Enumerable.Repeat(new Vector3(1, 11, -4), 4096).ToArray() });
        world.Models.Add(model);
        for (int i = 0; i < 1024; i++)
        {
            WorldNode node = new($"copy{i}", WorldNodeClass.Object3D) { Model = model };
            node.SetPayloadInt(0, 8);
            world.Nodes.Add(node);
        }
        var expected = new WorldBox(new(-10, -4, -8), new(12, 11, 6));

        Assert.Equal(1, WorldUpdate.RebuildBoundsCore(world, TestContext.Current.CancellationToken));
        Assert.All(world.Nodes, node =>
        {
            Assert.Equal(expected, node.PrimaryBounds);
            Assert.Equal(expected, node.CachedBounds);
            Assert.NotEqual(0u, node.Flags & WorldUpdate.ModelBoundsFlag);
        });
        Assert.Equal(new Vector3(1, 3.5f, -1), model.BoundsCentre);
        // Retail exponent approximation, independently evaluated from the expected half extents 11, 7.5, 7.
        int radiusBits = (BitConverter.SingleToInt32Bits(226.25f) >> 1) + 0x1FC00000;
        Assert.Equal(radiusBits, BitConverter.SingleToInt32Bits(model.BoundsRadius));

        // The cache lasts only for this pass: authored changes must be picked up by the next rebuild.
        model.Points[0].Vertices[0] = new(20, 11, -4);
        Assert.Equal(1, WorldUpdate.RebuildBoundsCore(world, TestContext.Current.CancellationToken));
        Assert.All(world.Nodes, node => Assert.Equal(new Vector3(20, 11, 6), node.PrimaryBounds.Max));
    }

    [Fact]
    public void DistinctAndUnreferencedModelsKeepSeparateBounds()
    {
        GameZWorld world = new();
        WorldModel first = new(), second = new(), unused = new();
        first.Vertices.Add(new(1, 2, 3)); second.Vertices.Add(new(4, 5, 6)); unused.Vertices.Add(new(7, 8, 9));
        world.Models.AddRange([first, second, unused]);
        world.Nodes.Add(new("first", WorldNodeClass.Object3D) { Model = first });
        world.Nodes.Add(new("second", WorldNodeClass.Object3D) { Model = second });

        Assert.Equal(3, WorldUpdate.RebuildBoundsCore(world, TestContext.Current.CancellationToken));
        Assert.Equal(new Vector3(1, 2, 3), world.Nodes[0].PrimaryBounds.Min);
        Assert.Equal(new Vector3(4, 5, 6), world.Nodes[1].PrimaryBounds.Min);
        Assert.Equal(new Vector3(7, 8, 9), unused.BoundsCentre);
    }

    [Fact]
    public void CanceledBoundsPassDoesNotStartPublishingDerivedValues()
    {
        GameZWorld world = new();
        WorldModel model = new() { BoundsCentre = new(99) };
        model.Vertices.Add(new(1, 2, 3)); world.Models.Add(model);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorldUpdate.RebuildBounds(world, cancellation.Token));
        Assert.Equal(new Vector3(99), model.BoundsCentre);
        Assert.Throws<OperationCanceledException>(() => WorldUpdate.ModelBounds(model, cancellation.Token));
    }
}
