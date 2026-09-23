using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class CoreTests
{
    private static byte[] Words(params uint[] words) { byte[] b = new byte[words.Length * 4]; for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), words[i]); return b; }
    [Theory]
    [InlineData(0x08971119u, 7u, FormatFamily.Scripts)]
    [InlineData(0x08170616u, 28u, FormatFamily.Animation)]
    [InlineData(0x02971222u, 15u, FormatFamily.GameZ)]
    public void DetectionUsesStructureAndReportsUnsupportedVersion(uint magic, uint version, FormatFamily family)
    {
        byte[] bytes = Words(magic, version, 0, 0, 0, 0, 0, 0, 0); var result = FormatRegistry.Probe(bytes, [], bytes.Length, ".renamed"); Assert.Equal(family, result.Family); Assert.Equal(Recognition.Supported, result.Recognition);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 999); var doc = FormatRegistry.Default.OpenBytes("renamed", bytes, token: TestContext.Current.CancellationToken); Assert.Equal(Recognition.UnsupportedVersion, doc.Probe.Recognition); Assert.Single(doc.Assets); Assert.Equal(AssetKind.Raw, doc.Assets[0].Kind);
    }
    [Fact]
    public void MalformedCountsCannotAllocateBeyondInput()
    {
        foreach (var b in new[] { Words(0, 1, uint.MaxValue, uint.MaxValue, 0, 0), Words(0x08971119, 7, uint.MaxValue), Words(1, uint.MaxValue) })
        { var doc = FormatRegistry.Default.OpenBytes("bad.zbd", b, token: TestContext.Current.CancellationToken); Assert.NotEmpty(doc.Diagnostics); }
    }
    [Fact]
    public void EveryTruncatedTextureIsDiagnosedWithoutCrashing()
    {
        byte[] source = TextureFixture(0); for (int size = 24; size < source.Length; size++) { var doc = FormatRegistry.Default.OpenBytes("bad.zbd", source[..size], token: TestContext.Current.CancellationToken); Assert.NotEmpty(doc.Diagnostics); }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DirectSharedAndEmbeddedPalettesDecodeWithAlpha(int encoding)
    {
        var doc = FormatRegistry.Default.OpenBytes("colors.zbd", TextureFixture(encoding), token: TestContext.Current.CancellationToken); Assert.Empty(doc.Diagnostics); var image = TextureDecoder.Decode(doc, Assert.Single(doc.Assets), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 255, 0, 0, 64, 0, 255, 0, 255 }, image.Rgba);
    }
    private static byte[] TextureFixture(int encoding)
    {
        int offset = encoding == 1 ? 576 : 64, pixelBytes = encoding == 0 ? 4 : 2; byte[] bytes = new byte[offset + 16 + pixelBytes + 2 + (encoding == 2 ? 4 : 0)];
        Words(0, 1, encoding == 1 ? 1u : 0u, 1, 0, 0).CopyTo(bytes, 0); "colors"u8.CopyTo(bytes.AsSpan(24)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), offset); bytes[offset] = (byte)(encoding == 2 ? 136 : encoding == 0 ? 9 : 8); bytes[offset + 4] = 2; bytes[offset + 6] = 1; bytes[offset + 12] = encoding == 0 ? (byte)0 : (byte)2;
        int palette = encoding == 0 ? offset + 16 : encoding == 1 ? 64 : offset + 20; new byte[] { 0, 248, 224, 7 }.CopyTo(bytes, palette);
        if (encoding != 0) { bytes[offset + 16] = 0; bytes[offset + 17] = 1; }
        bytes[offset + 16 + pixelBytes] = 64; bytes[offset + 17 + pixelBytes] = 255; return bytes;
    }
    [Fact]
    public void PngHasIndependentDecompressibleRgbaRowsAndCorrectCrc()
    {
        byte[] rgba = [255, 20, 0, 128, 0, 33, 70, 255]; byte[] png = PngEncoder.Encode(new(2, 1, rgba), TestContext.Current.CancellationToken); Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        using MemoryStream compressed = new(); int p = 8;
        while (p < png.Length) { int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p)); string kind = System.Text.Encoding.ASCII.GetString(png, p + 4, 4); uint crc = 0xFFFFFFFF; foreach (byte b in png.AsSpan(p + 4, length + 4)) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); } Assert.Equal(~crc, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p + 8 + length))); if (kind == "IDAT") compressed.Write(png, p + 8, length); p += length + 12; }
        compressed.Position = 0; using ZLibStream zlib = new(compressed, CompressionMode.Decompress); using MemoryStream raw = new(); zlib.CopyTo(raw); Assert.Equal((byte[])[0, .. rgba], raw.ToArray());
    }
    [Fact]
    public void ZrdCountsIncludeArraySentinelAndRejectTrailingBytes()
    {
        var tree = ZrdDecoder.Decode(Words(4, 3, 1, 42, 2, 0x3f800000), TestContext.Current.CancellationToken); Assert.Equal(2, tree["children"]!.AsArray().Count); Assert.Equal(42, tree["children"]![0]!["value"]!.GetValue<long>());
        Assert.Throws<InvalidDataException>(() => ZrdDecoder.Decode(Words(4, 0), TestContext.Current.CancellationToken)); Assert.Throws<InvalidDataException>(() => ZrdDecoder.Decode(Words(1, 42, 99), TestContext.Current.CancellationToken));
    }
    [Fact]
    public void ScalarZrdDoesNotCollideWithEmptyArchiveTrailer()
    {
        var doc = FormatRegistry.Default.OpenBytes("scalar.zrd", Words(1, 0), token: TestContext.Current.CancellationToken); Assert.Equal(FormatFamily.Zrd, doc.Probe.Family);
    }
    [Fact]
    public void SoundArchiveTakesPrecedenceOverItsFirstRiffMember()
    {
        byte[] prefix = new byte[36]; "RIFF"u8.CopyTo(prefix); "WAVE"u8.CopyTo(prefix.AsSpan(8));
        Assert.Equal(FormatFamily.Archive, FormatRegistry.Probe(prefix, Words(1, 2), 1000, ".zbd").Family);
        Assert.Equal(FormatFamily.Wave, FormatRegistry.Probe(prefix, Words(0, 0), 1000, ".wav").Family);
    }
    [Fact]
    public void NonfiniteTransformIsDiagnosed()
    {
        var node = Node(0, "object3d", null, [], [1, 0, 0, 0, 1, 0, 0, 0, 1, float.NaN, 0, 0]); Assert.Throws<InvalidDataException>(() => SceneBuilder.LocalTransform(node)); Assert.True(float.IsNaN(JsonData.Scalar(JsonValue.Create("0x7FC00000"), float.NaN)));
    }
    [Fact]
    public void EngineBasisThenTranslationComposesLocalBeforeParent()
    {
        GameScene scene = new(); scene.Models.Add(new(0, [], [], [], [], [])); scene.Nodes.Add(Node(0, "world", null, [1])); scene.Nodes.Add(Node(1, "object3d", null, [2], [0, 1, 0, -1, 0, 0, 0, 0, 1, 10, 0, 0])); scene.Nodes.Add(Node(2, "object3d", 0, [], [1, 0, 0, 0, 1, 0, 0, 0, 1, 2, 0, 0]));
        var view = SceneBuilder.Assemble(scene, token: TestContext.Current.CancellationToken); Assert.Empty(view.Diagnostics); Vector3 result = Vector3.Transform(Vector3.Zero, Assert.Single(view.Placements).Transform); Assert.Equal(new Vector3(10, 2, 0), result);
    }
    [Fact]
    public void SharedNodesProduceInstancesButCyclesStop()
    {
        GameScene scene = new(); scene.Models.Add(new(0, [], [], [], [], [])); scene.Nodes.Add(Node(0, "world", null, [1, 2])); scene.Nodes.Add(Node(1, "object3d", null, [3])); scene.Nodes.Add(Node(2, "object3d", null, [3])); scene.Nodes.Add(Node(3, "object3d", 0, [1])); var view = SceneBuilder.Assemble(scene, token: TestContext.Current.CancellationToken); Assert.Equal(2, view.Placements.Count); Assert.NotEmpty(view.Diagnostics);
    }
    [Fact]
    public void LodSelectsOneDistanceBand()
    {
        GameScene scene = new(); scene.Models.Add(new(0, [], [], [], [], [])); scene.Nodes.Add(Node(0, "world", null, [1, 2])); scene.Nodes.Add(new(1, "near", "lod", null, [], [3], new() { ["flags"] = 4 }, new() { ["range_near_sq"] = 0, ["range_far_sq"] = 100 })); scene.Nodes.Add(new(2, "far", "lod", null, [], [4], new() { ["flags"] = 4 }, new() { ["range_near_sq"] = 100, ["range_far_sq"] = 1000 })); scene.Nodes.Add(Node(3, "object3d", 0, [])); scene.Nodes.Add(Node(4, "object3d", 0, [])); Assert.Equal(3, Assert.Single(SceneBuilder.Assemble(scene, 0, token: TestContext.Current.CancellationToken).Placements).NodeIndex); Assert.Equal(4, Assert.Single(SceneBuilder.Assemble(scene, 1, token: TestContext.Current.CancellationToken).Placements).NodeIndex);
    }
    [Fact]
    public void PartitionRootsAreIncludedOnceAcrossOverlappingCells()
    {
        GameScene scene = new(); scene.Models.Add(new(0, [], [], [], [], []));
        var world = Node(0, "world", null, [1]); world.Data["partitions"] = new JsonArray(new JsonArray(new JsonObject { ["node_indices"] = new JsonArray(1, 2) }, new JsonObject { ["node_indices"] = new JsonArray(2) }));
        scene.Nodes.Add(world); scene.Nodes.Add(Node(1, "object3d", 0, [])); scene.Nodes.Add(Node(2, "object3d", 0, []));
        Assert.Equal(2, SceneBuilder.Assemble(scene, token: TestContext.Current.CancellationToken).Placements.Count);
    }
    private static GameNode Node(int index, string cls, int? model, int[] children, float[]? matrix = null) => new(index, "node" + index, cls, model, [], children, new() { ["flags"] = 4 }, new() { ["flags"] = matrix == null ? 8 : 0, ["transform"] = matrix == null ? null : new JsonArray(matrix.Select(v => (JsonNode?)JsonData.Number(v)).ToArray()) });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcaveTriangulationPreservesAreaAndWinding(bool reverse)
    {
        Vector3[] polygon = [new(0, 0, 0), new(3, 0, 0), new(3, 3, 0), new(1.5f, 1, 0), new(0, 3, 0)]; if (reverse) Array.Reverse(polygon); int[] indices = GeometryBuilder.Triangulate(polygon); Assert.Equal(9, indices.Length); float area = 0; for (int i = 0; i < indices.Length; i += 3) { float cross = Vector3.Cross(polygon[indices[i + 1]] - polygon[indices[i]], polygon[indices[i + 2]] - polygon[indices[i]]).Z; Assert.True(reverse ? cross < 0 : cross > 0); area += cross / 2; }
        Assert.Equal(reverse ? -6f : 6f, area);
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\outside")]
    public void ExportRejectsPathsOutsideDestination(string path) => Assert.Throws<IOException>(() => ExportService.DestinationPath(Path.GetTempPath(), path));
    [Fact]
    public async Task ExportNeverOverwritesAndHandlesUnicodePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "zbd-tests-ž-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { await ExportService.WriteAtomicAsync(root, "音/ž.bin", new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken); await Assert.ThrowsAsync<IOException>(() => ExportService.WriteAtomicAsync(root, "音/ž.bin", new byte[] { 4 }, TestContext.Current.CancellationToken)); Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "音/ž.bin"), TestContext.Current.CancellationToken)); }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ExportRejectsSourceTree()
    {
        using var resolver = new AssetResolver(Path.GetTempPath()); var doc = FormatRegistry.Default.OpenBytes("test", [0], token: TestContext.Current.CancellationToken); await Assert.ThrowsAsync<IOException>(() => new ExportService(resolver).ExportAsync(doc, doc.Assets, Path.GetTempPath(), false, token: TestContext.Current.CancellationToken));
    }
    [Fact] public void NamesCannotEscapeOrBecomeWindowsDevices() { Assert.Equal("_CON.txt", ExportService.SafeName("CON.txt")); Assert.DoesNotContain('/', ExportService.SafeName("../../evil")); Assert.DoesNotContain('\\', ExportService.SafeName("..\\evil")); }
}
