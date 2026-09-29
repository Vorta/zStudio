using System.Buffers.Binary;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class GameZBoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(15)]
    [InlineData(27)]
    public void OversizedModelTablesAreRejectedBeforeHeadersAreMaterialized(int version)
    {
        // Complete zero-geometry headers are present, so only the supported limit stops materialization.
        byte[] world = World(version, 70_000, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", world, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Error" && d.Message.Contains("model count 70,000 exceeds the supported 65,536", StringComparison.Ordinal));
        Assert.True(allocated < 32_000_000, $"Allocated {allocated:N0} bytes");
        Assert.Equal(world, doc.Bytes.ToArray());
    }

    [Theory]
    [InlineData(15)]
    [InlineData(27)]
    public void OversizedPolygonTotalsAreRejectedBeforeModelDataIsRead(int version)
    {
        byte[] world = World(version, 1, 300_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", world, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Error" && d.Message.Contains("polygon/light record count 300,000 exceeds the supported 262,144", StringComparison.Ordinal));
        Assert.True(allocated < 32_000_000, $"Allocated {allocated:N0} bytes");
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("texture")]
    [InlineData("script")]
    public void OversizedDirectoriesAreRejectedBeforeEntriesAreMaterialized(string kind)
    {
        const int count = 70_000; byte[] bytes;
        if (kind == "archive")
        {
            // Complete zero-length member records: every entry would otherwise become metadata with a hex record copy.
            bytes = new byte[count * 148 + 8]; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), count);
        }
        else if (kind == "texture") { bytes = new byte[24 + count * 40]; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), count); }
        else { bytes = new byte[12 + count * 128]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x08971119); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 7); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), count); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes(kind + ".zbd", bytes, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Error" && d.Message.Contains("count 70,000 exceeds the supported 65,536", StringComparison.Ordinal));
        Assert.True(allocated < 32_000_000, $"Allocated {allocated:N0} bytes");
    }

    [Fact]
    public void WorldsWithinTheLimitsStillOpen()
    {
        var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", World(27, 2_000, 2), token: Token);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "Error");
        Assert.Equal(2_000, doc.Scene!.Models.Count); Assert.Equal(2, doc.Scene.Models[0].Polygons.Length);
    }

    /// <summary>A minimal world: no textures/materials/nodes, <paramref name="models"/> zero-vertex models, the first with empty polygons.</summary>
    private static byte[] World(int version, int models, int polygons)
    {
        var layout = GameZLayouts.For((uint)version); int header = layout.ModelSize, polygonSize = layout.PolygonSize, shift = version == 27 ? 4 : 0;
        const int materials = 36, modelTable = materials + 16;
        int data = modelTable + 12 + models * (header + 4), nodes = data + polygons * polygonSize;
        byte[] b = new byte[nodes];
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        I(0, 0x02971222); I(4, version); I(12, 36); I(16, materials); I(20, modelTable); I(24, 0); I(28, -1); I(32, nodes);
        I(modelTable, models); I(modelTable + 4, models); I(modelTable + 8, -1);
        // polygon_count (+12, shifted for version 27) on the first model only; polygon flags 0 carry no index arrays.
        I(modelTable + 12 + 12 + shift, polygons);
        return b;
    }
}
