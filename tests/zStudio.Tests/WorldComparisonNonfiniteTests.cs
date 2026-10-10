using System.Buffers.Binary;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldComparisonNonfiniteTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ReaderAcceptedNonfiniteLocalMatrixMustDifferFromFiniteIdentity(float nonfinite)
    {
        GameZWorld world = new() { NodeCapacity = 2, MaterialCapacity = 1, ModelCapacity = 1 };
        WorldNode root = new("world", WorldNodeClass.World), child = new("object", WorldNodeClass.Object3D);
        child.SetPayloadInt(0, 0x10);
        child.SetPayloadFloat(0x30, 1); child.SetPayloadFloat(0x40, 1); child.SetPayloadFloat(0x50, 1);
        root.Children.Add(child); child.Parents.Add(root); world.Nodes.AddRange([root, child]);
        byte[] bytes = GameZWriter.Write(world, Token);
        var original = FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token);
        var expected = GameZWorldReader.FromDocument(original, Token);
        byte[] modified = (byte[])bytes.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(modified.AsSpan(checked((int)original.GameZLayout!.NodeDataOffsets[1]) + 0x30), nonfinite);
        var document = FormatRegistry.Default.OpenBytes("gamez.zbd", modified, token: Token);
        var actual = GameZWorldReader.FromDocument(document, Token);
        Assert.Equal(nonfinite, actual.Nodes.Single(n => n.Name == "object").PayloadFloat(0x30));
        Assert.Contains(WorldComparer.CompareTree(expected, actual, token: Token).Differences, d => d.Field == "matrix");
        Assert.Contains(WorldComparer.CompareTree(actual, expected, token: Token).Differences, d => d.Field == "matrix");
        Assert.Empty(WorldComparer.CompareTree(expected, GameZWorldReader.FromDocument(original, Token), token: Token).Differences);
        Assert.Empty(WorldComparer.CompareTree(actual, GameZWorldReader.FromDocument(document, Token), token: Token).Differences);
    }
}
