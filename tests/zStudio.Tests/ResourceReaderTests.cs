using System.Buffers.Binary;
using System.Text;
using zStudio.Core;
using zStudio.Core.Formats;
using Xunit;

namespace zStudio.Tests;

public sealed class ResourceReaderTests
{

    [Fact]
    public void SupportedZrdDepthFitsBoundedInspectionAndCompleteJsonExport()
    {
        var token = TestContext.Current.CancellationToken;
        var root = ZrdNode.Create(ZrdKind.Int, "42");
        for (int i = 0; i < 128; i++) root = ZrdNode.Create(ZrdKind.Array) with { Children = [root] };
        var doc = FormatRegistry.Default.OpenBytes("deep.zrd", ZrdWriter.Write(root, token), token: token);
        Assert.Empty(doc.Diagnostics);
        var tree = Assert.IsType<ZrdNode>(doc.Assets[0].Content);
        string full = tree.ToJson(token).ToJsonString(JsonData.Options);
        Assert.Contains("42", full); Assert.DoesNotContain("children_truncated", full);
        Assert.Contains("children_truncated", tree.ToPreviewJson(token).ToJsonString());
    }
    [Fact]
    public void StandaloneZrdMustDecodeCompletely()
    {
        var token = TestContext.Current.CancellationToken;
        foreach (byte[] bytes in new byte[][] { [1,0,0,0], [2,0,0,0], [3,0,0,0,2,0,0,0,65], [4,0,0,0,0,0,0,0], [4,0,0,0,2,0,0,0,99,0,0,0], [1,0,0,0,0,0,0,0,99] })
        {
            var doc = FormatRegistry.Default.OpenBytes("malformed.zrd", bytes, token: token);
            Assert.Contains(doc.Diagnostics, d => d.Severity == "Error" && d.Message.StartsWith("Parsing stopped:"));
            Assert.Equal(AssetKind.Raw, Assert.Single(doc.Assets).Kind);
        }
        var valid = FormatRegistry.Default.OpenBytes("valid.zrd", ZrdWriter.Write(ZrdNode.Create(ZrdKind.Int, "0"), token), token: token);
        Assert.Empty(valid.Diagnostics);
        Assert.IsType<ZrdNode>(Assert.Single(valid.Assets).Content);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => FormatRegistry.Default.OpenBytes("valid.zrd", valid.Bytes.ToArray(), token: cancellation.Token));
    }
    [Fact]
    public void MalformedEmbeddedZrdRemainsRaw()
    {
        var token = TestContext.Current.CancellationToken;
        var doc = FormatRegistry.Default.OpenBytes("resources.zbd", Archive(("bad.zrd", [1,0,0,0])), token: token);
        Assert.Equal(AssetKind.Raw, Assert.Single(doc.Assets).Kind);
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Warning" && d.AssetIndex == 0 && d.Offset == 0);
    }
    [Fact]
    public void AliasedArchivePayloadIsDecodedOnce()
    {
        var token = TestContext.Current.CancellationToken;
        byte[] data = ZrdWriter.Write(ZrdNode.Create(ZrdKind.String) with { Text = new string('x', 256 * 1024) }, token);
        const int count = 128; byte[] bytes = new byte[data.Length + count * 148 + 8]; data.CopyTo(bytes, 0);
        for (int i = 0; i < count; i++)
        {
            int record = data.Length + i * 148;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 4), data.Length);
            Encoding.Latin1.GetBytes("alias.zrd").CopyTo(bytes, record + 8);
        }
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), count);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("aliases.zbd", bytes, token: token);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(allocated < 4 * 1024 * 1024, $"Aliased payload allocated {allocated:N0} bytes.");
        Assert.Equal(count, doc.Assets.Count);
    }
    [Fact]
    public void APlausibleFirstWordDoesNotMakeAMemberZrd()
    {
        var token = TestContext.Current.CancellationToken;
        var opaque = FormatRegistry.Default.OpenBytes("opaque.zbd", Archive(("not-zrd", new byte[] {1,0,0,0,0,0,0,0,99})), token: token);
        Assert.Equal(AssetKind.Raw, opaque.Assets.Single().Kind); // A plausible first word is insufficient.
    }
    [Fact]
    public void ZrdInspectionBoundsTreeAndStringsWithoutTruncatingExports()
    {
        var token = TestContext.Current.CancellationToken;
        var text = ZrdNode.Create(ZrdKind.String) with { Text = new string('\0', 1024 * 1024) };
        var root = ZrdNode.Create(ZrdKind.Array) with { Children = Enumerable.Range(0, 2048).Select(_ => text with { Id = Guid.NewGuid() }).ToArray() };
        var preview = root.ToPreviewJson(token); Assert.True(preview["children_truncated"]!.GetValue<bool>());
        Assert.Equal(1023, preview["children"]!.AsArray().Count);
        Assert.True(preview["children"]![0]!["value_truncated"]!.GetValue<bool>());
        Assert.True(preview["children"]!.AsArray().Sum(n => n!["value"]!.GetValue<string>().Length) <= 65536);
        Assert.True(preview.ToJsonString().Length < 512 * 1024);
        Assert.Equal(text.Text, text.ToJson(token)["value"]!.GetValue<string>());
    }
    [Fact]
    public void ZrdPreviewBoundsFormattingBeforeEscapingAndPreservesExactPrefixes()
    {
        foreach (string text in new[] { "", "plain", new string('x', 4094), new string('x', 4095), string.Concat(Enumerable.Range(0, 256).Select(i => (char)i)) })
        {
            var node = ZrdNode.Create(ZrdKind.String) with { Text = text };
            string full = System.Text.Json.JsonSerializer.Serialize(text);
            foreach (int limit in new[] { 1, 2, 199, 200, 4096 })
            {
                var preview = node.PreviewValue(limit);
                Assert.Equal(full[..Math.Min(limit, full.Length)], preview.Value);
                Assert.Equal(full.Length > limit, preview.Truncated);
            }
        }
        foreach (var node in new[] { ZrdNode.Create(ZrdKind.Int, "-2147483648"), ZrdNode.Create(ZrdKind.Float, "0x7FA12345"), ZrdNode.Create(ZrdKind.Array) })
            Assert.Equal((node.Value, false), node.PreviewValue(4096));
        var large = ZrdNode.Create(ZrdKind.String) with { Text = new string('\0', 8 * 1024 * 1024) };
        _ = large.PreviewValue(4096);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var bounded = large.PreviewValue(4096);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(4096, bounded.Value.Length); Assert.True(bounded.Truncated);
        // Concurrent corpus tests can trim the serializer's pooled buffers between warmup and measurement.
        // Include cold buffer rentals while remaining far below the 8 MiB stored string (before escaping).
        Assert.True(allocated < 256 * 1024, $"Formatting allocated {allocated:N0} bytes for a bounded prefix.");
        Assert.Throws<ArgumentOutOfRangeException>(() => large.PreviewValue(0));
    }
    [Fact]
    public async Task OptionalCorpusZrdRoundTripsAreByteIdentical()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (root == null) return;
        var token = TestContext.Current.CancellationToken;
        int archives = 0, resources = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*.zbd", SearchOption.AllDirectories).Where(p => FormatRegistry.Probe(p).Family == FormatFamily.Archive))
        {
            var doc = await FormatRegistry.Default.OpenAsync(path, token); archives++;
            foreach (var asset in doc.Assets.Where(a => a.Kind == AssetKind.Zrd))
            {
                var bytes = doc.Slice(asset.Offset, asset.Length); Assert.Equal(bytes.ToArray(), ZrdWriter.Write(ZrdDecoder.Read(bytes, token), token)); resources++;
            }
        }
        Assert.True(archives > 0 && resources > 0, "Corpus must contain both ZAR archives and ZRD members.");
    }
    [Fact]
    public void BoundedZrdReaderAndWriterRejectMalformedStructure()
    {
        var token = TestContext.Current.CancellationToken;
        foreach (byte[] bytes in new byte[][] { [], [1,0,0,0], [7,0,0,0], [3,0,0,0,255,255,255,127], [4,0,0,0,255,255,255,127], [1,0,0,0,0,0,0,0,9] })
            Assert.Throws<InvalidDataException>(() => ZrdDecoder.Read(bytes, token));
        var child = ZrdNode.Create(ZrdKind.Int); var duplicate = ZrdNode.Create(ZrdKind.Array) with { Children = [child, child] }; Assert.Throws<InvalidDataException>(() => ZrdWriter.Write(duplicate, token));
        var nested = child; for (int i = 0; i < 130; i++) nested = ZrdNode.Create(ZrdKind.Array) with { Children = [nested] };
        Assert.Throws<InvalidDataException>(() => ZrdWriter.Write(nested, token));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); Assert.Throws<OperationCanceledException>(() => ZrdWriter.Write(child, cancellation.Token));
    }
    internal static byte[] Archive(params (string Name, byte[] Bytes)[] entries)
    {
        int size = entries.Sum(e => e.Bytes.Length); byte[] bytes = new byte[size + entries.Length * 148 + 8]; int offset = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i]; e.Bytes.CopyTo(bytes, offset); int record = size + i * 148;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record), offset); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 4), e.Bytes.Length);
            Encoding.Latin1.GetBytes(e.Name).CopyTo(bytes, record + 8); bytes[record + 72] = 19; bytes[record + 144] = 27; offset += e.Bytes.Length;
        }
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), entries.Length); return bytes;
    }
    [Fact]
    public void ZrdFloatBitsAndEscapedStringsRoundTrip()
    {
        var token = TestContext.Current.CancellationToken;
        var root = ZrdNode.Create(ZrdKind.Array) with { Children = [ZrdNode.Create(ZrdKind.Float, "0x7FA12345"), ZrdNode.Create(ZrdKind.String, "\"old\\u0000\\u00ff\""), ZrdNode.Create(ZrdKind.Array)] };
        byte[] bytes = ZrdWriter.Write(root, token); Assert.Equal(bytes, ZrdWriter.Write(ZrdDecoder.Read(bytes, token), token));
    }
    [Fact]
    public void MalformedZrdTextAndStructureAreRejected()
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Throws<InvalidDataException>(() => ZrdNode.Create(ZrdKind.String, "\"漢\""));
        Assert.Throws<InvalidDataException>(() => ZrdDecoder.Read(new byte[] {4,0,0,0,0,0,0,0}, token));
    }
}
