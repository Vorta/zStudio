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
    [InlineData(15)]
    [InlineData(27)]
    public void PointLightVerticesShareTheGeometryBudgetBeforeTheyAreExpanded(int version)
    {
        // One light record passes the header total; its complete vertex list is what would become JSON metadata.
        byte[] world = World(version, 1, 0, lightVertices: 300_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", world, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Error" && d.Message.Contains("polygon/light record count 300,001 exceeds the supported 262,144", StringComparison.Ordinal));
        Assert.True(allocated < 32_000_000, $"Allocated {allocated:N0} bytes");
        var retail = FormatRegistry.Default.OpenBytes("gamez.zbd", World(version, 1, 0, lightVertices: 10), token: Token);
        Assert.DoesNotContain(retail.Diagnostics, d => d.Severity == "Error");
        Assert.Equal(10, retail.Assets.Single(a => a.Kind == AssetKind.Model).Metadata["lights"]![0]!["vertices"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(27)]
    public void ModelVectorsAndPolygonCornersAreBoundedBeforeTheirArraysAreAllocated(int version)
    {
        // Complete vertex bytes are present: only the model-vector total stops the dense array.
        byte[] world = World(version, 1, 0, vertices: 1_100_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", world, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Error" && d.Message.Contains("model vector count 1,100,000 exceeds the supported 1,048,576", StringComparison.Ordinal));
        Assert.True(allocated < 8_000_000, $"Allocated {allocated:N0} bytes");
        // Complete 255-corner index arrays for every polygon: the corner total stops them before any is allocated.
        doc = FormatRegistry.Default.OpenBytes("gamez.zbd", World(version, 1, 16_500, polygonCorners: 255), token: Token);
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Error" && d.Message.Contains("polygon corner count 4,207,500 exceeds the supported 4,194,304", StringComparison.Ordinal));
        var retail = FormatRegistry.Default.OpenBytes("gamez.zbd", World(version, 1, 2, vertices: 3, polygonCorners: 3), token: Token);
        Assert.DoesNotContain(retail.Diagnostics, d => d.Severity is "Error" or "Warning");
        var model = retail.Scene!.Models[0]; Assert.Equal(3, model.Vertices.Length); Assert.All(model.Polygons, p => Assert.Equal(3, p.Vertices.Length));
    }

    [Fact]
    public void MaterialCycleIndicesShareABudgetBeforeTheyAreExpanded()
    {
        byte[] world = World(15, 1, 0, cycleIndices: 300_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", world, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Error" && d.Message.Contains("material cycle texture index count 300,000 exceeds the supported 262,144", StringComparison.Ordinal));
        Assert.True(allocated < 32_000_000, $"Allocated {allocated:N0} bytes");
        var retail = FormatRegistry.Default.OpenBytes("gamez.zbd", World(15, 1, 0, cycleIndices: 8), token: Token);
        Assert.DoesNotContain(retail.Diagnostics, d => d.Severity == "Error");
        Assert.Equal(8, retail.Assets.Single(a => a.Kind == AssetKind.Material).Metadata["cycle"]!["texture_indices"]!.AsArray().Count);
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
    public void OversizedWorldPartitionsAreRejectedBeforeCellsAreMaterialized()
    {
        // Complete empty cells are present, so only the supported cell limit stops per-cell metadata.
        byte[] cells = WorldNode(300, 300, _ => 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => GameZReader.ReadNodeData(new BinaryCursor(cells), "world", GameZLayouts.For(15)));
        Assert.Contains("partition cell count 90,000 exceeds the supported 65,536", error.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4_000_000);
        // Complete index arrays are present too: the running node-reference total stops before the last array is built.
        byte[] references = WorldNode(5, 1, _ => ushort.MaxValue);
        error = Assert.Throws<InvalidDataException>(() => GameZReader.ReadNodeData(new BinaryCursor(references), "world", GameZLayouts.For(15)));
        Assert.Contains("node reference count 327,675 exceeds the supported 262,144", error.Message);
        // Other node index lists share the same per-document budget (here, world light indices with complete bytes).
        error = Assert.Throws<InvalidDataException>(() => GameZReader.ReadNodeData(new BinaryCursor(WorldNode(0, 0, _ => 0, lights: 300_000)), "world", GameZLayouts.For(15)));
        Assert.Contains("node reference count 300,000 exceeds the supported 262,144", error.Message);
        var retail = GameZReader.ReadNodeData(new BinaryCursor(WorldNode(20, 30, i => i % 3)), "world", GameZLayouts.For(15));
        Assert.Equal(30, ((System.Text.Json.Nodes.JsonArray)retail["partitions"]!).Count);
    }
    /// <summary>A version-15 world node payload followed by its interleaved partition cells and node indices.</summary>
    private static byte[] WorldNode(int x, int z, Func<int, int> references, int lights = 0)
    {
        using MemoryStream stream = new(); using BinaryWriter w = new(stream);
        byte[] world = new byte[172]; BinaryPrimitives.WriteInt32LittleEndian(world.AsSpan(120), x); BinaryPrimitives.WriteInt32LittleEndian(world.AsSpan(124), z);
        BinaryPrimitives.WriteInt32LittleEndian(world.AsSpan(144), lights); w.Write(world); w.Write(new byte[lights * 4]);
        for (int i = 0; i < x * z; i++)
        {
            byte[] cell = new byte[64]; int count = references(i); BinaryPrimitives.WriteUInt16LittleEndian(cell.AsSpan(58), (ushort)count); w.Write(cell);
            w.Write(new byte[count * 4]);
        }
        return stream.ToArray();
    }

    [Fact]
    public void WorldsWithinTheLimitsStillOpen()
    {
        var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", World(27, 2_000, 2), token: Token);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "Error");
        Assert.Equal(2_000, doc.Scene!.Models.Count); Assert.Equal(2, doc.Scene.Models[0].Polygons.Length);
    }

    /// <summary>A minimal world: no textures/materials/nodes, <paramref name="models"/> zero-vertex models, the first with empty polygons
    /// and, when <paramref name="lightVertices"/> is nonnegative, one point light with that many complete vertices.
    /// A nonnegative <paramref name="cycleIndices"/> adds one cycling material with that many complete texture indices.
    /// The first model may also have <paramref name="vertices"/> complete vertices and <paramref name="polygonCorners"/> vertex indices per polygon.</summary>
    private static byte[] World(int version, int models, int polygons, int lightVertices = -1, int cycleIndices = -1, int vertices = 0, int polygonCorners = 0)
    {
        var layout = GameZLayouts.For((uint)version); int header = layout.ModelSize, polygonSize = layout.PolygonSize, shift = version == 27 ? 4 : 0;
        const int materials = 36; int modelTable = materials + 16 + (cycleIndices < 0 ? 0 : 44 + 28 + cycleIndices * 4);
        int data = modelTable + 12 + models * (header + 4), light = lightVertices < 0 ? 0 : 76 + lightVertices * 12;
        int polygonInfo = data + vertices * 12 + light, nodes = polygonInfo + polygons * (polygonSize + polygonCorners * 4);
        byte[] b = new byte[nodes];
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        I(0, 0x02971222); I(4, version); I(12, 36); I(16, materials); I(20, modelTable); I(24, 0); I(28, -1); I(32, nodes);
        I(modelTable, models); I(modelTable + 4, models); I(modelTable + 8, -1);
        // polygon_count (+12, shifted for version 27) on the first model only; polygon flags 0 carry no index arrays.
        I(modelTable + 12 + 12 + shift, polygons);
        // light_count (+28); the light record's vertex_count (+12) precedes its vertices and the polygons.
        if (lightVertices >= 0) { I(modelTable + 12 + 28 + shift, 1); I(data + vertices * 12 + 12, lightVertices); }
        // vertex_count (+16); polygon flags (+0) carry the corner count, and zero pointers add no normal/UV/color arrays.
        I(modelTable + 12 + 16 + shift, vertices);
        for (int i = 0; i < polygons && polygonCorners > 0; i++) I(polygonInfo + i * polygonSize, polygonCorners);
        // Material flag 4 (+1) reads the cycle record after the table; its tex_map_count (+16) precedes the indices.
        if (cycleIndices >= 0) { I(materials, 1); I(materials + 4, 1); b[materials + 16 + 1] = 4; I(materials + 16 + 44 + 16, cycleIndices); }
        return b;
    }
}
