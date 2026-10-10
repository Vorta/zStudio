using System.Buffers.Binary;
using System.Numerics;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class DemoBoundsMagnitudeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1f, 1e20f)]
    [InlineData(1e20f, 2e20f)]
    [InlineData(1e20f, 1e20f)]
    public void StoredDemoCornersWinUnlessComponentBoxActuallyMatches(float stored, float component)
    {
        var sample = GameZVersion13Tests.SampleWorld();
        var node = sample.Nodes.Single(n => n.Name == "crate"); // Identity matrix: stored parent-space corners are local corners.
        int slot = GameZWriter.NodeSlots(sample, TestContext.Current.CancellationToken)[node];
        byte[] bytes = DemoWorldFixture.FromVersion15(GameZWriter.Write(sample, Token));
        int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32)) + slot * 268;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 36), WorldUpdate.CachedBoundsFlag | WorldUpdate.ModelBoundsFlag);
        for (int i = 0; i < 8; i++)
        {
            Write(offset + 116 + i * 12, (i & 1) == 0 ? stored : 2 * stored);
            Write(offset + 120 + i * 12, (i & 2) == 0 ? stored : 2 * stored);
            Write(offset + 124 + i * 12, (i & 4) == 0 ? stored : 2 * stored);
        }
        for (int i = 0; i < 3; i++) { Write(offset + 212 + i * 4, component); Write(offset + 224 + i * 4, 2 * component); }
        var document = FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token);
        Assert.Empty(document.Diagnostics);
        var decoded = GameZWorldReader.FromDocument(document, Token).Nodes.Single(n => n.Name == "crate");
        Assert.Equal(new WorldBox(new(stored), new(2 * stored)), decoded.CachedBounds);
        Assert.Equal(new WorldBox(new(component), new(2 * component)), decoded.PrimaryBounds);
        Assert.Equal(new Vector3(stored), WorldBox.Of(WorldUpdate.ParentCorners(decoded)).Min);
        void Write(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
    }
}
