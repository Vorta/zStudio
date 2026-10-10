using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound22ScriptTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Pack(byte[] strings, int count, int repeat = 1)
    {
        byte[] bytes = new byte[144 + (strings.Length + 8) * repeat];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x08971119);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 7);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        Encoding.ASCII.GetBytes("sample.gs").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(136), 140);
        for (int i = 0; i < repeat; i++)
        {
            int offset = 140 + i * (strings.Length + 8);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), strings.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), count);
            strings.CopyTo(bytes, offset + 8);
        }
        return bytes;
    }

    [Fact]
    public void ExcessiveStoredTokenCountIsRefusedBeforeTokenArraysAreAllocated()
    {
        _ = FormatRegistry.Default.OpenBytes("warm.zbd", Pack([0], 1), token: Token);
        byte[] bytes = Pack(new byte[2_100_000], 2_100_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("large.zbd", bytes, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("128 MiB"));
        Assert.Null(doc.Scripts); Assert.Empty(doc.Assets);
        Assert.InRange(allocated, 0, 128 * 1024);
        Assert.Equal(bytes, doc.Bytes.ToArray());
    }

    [Fact]
    public void OrdinaryOpeningDoesNotQuoteAndJoinEveryEmptyStoredToken()
    {
        _ = FormatRegistry.Default.OpenBytes("warm.zbd", Pack([0], 1), token: Token);
        byte[] bytes = Pack(new byte[200_000], 200_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("many.zbd", bytes, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(doc.Diagnostics);
        var content = Assert.IsType<ScriptContent>(Assert.Single(doc.Assets).Content);
        Assert.Equal(200_000, Assert.Single(content.Instructions).Length);
        Assert.True(content.TextTruncated); Assert.Equal(65_536, content.PreviewText.Length);
        Assert.InRange(allocated, 0, 8 * 1024 * 1024);
        Assert.Equal(bytes, PreparedScriptWriter.Write(doc.Scripts!, Token));
        Assert.Equal(600_000, content.GetText(Token).Length);
    }

    [Fact]
    public async Task EscapedPreviewIsBoundedBeforeExpansionAndExplicitExportRetainsAllTokens()
    {
        byte[] strings = new byte[256 * 1024 + 1]; strings.AsSpan(0, strings.Length - 1).Fill((byte)'"');
        byte[] bytes = Pack(strings, 1);
        _ = FormatRegistry.Default.OpenBytes("warm.zbd", Pack([0], 1), token: Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("quoted.zbd", bytes, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var asset = Assert.Single(doc.Assets); var content = Assert.IsType<ScriptContent>(asset.Content);
        Assert.True(content.TextTruncated); Assert.Equal(65_536, content.PreviewText.Length);
        Assert.InRange(allocated, 0, 2 * 1024 * 1024);
        string expected = "\"" + string.Concat(Enumerable.Repeat("\\\"", strings.Length - 1)) + "\"\n";
        Assert.Equal(expected, content.Text);
        string root = Path.Combine(Path.GetTempPath(), "zstudio-script22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using AssetResolver resolver = new(Path.Combine(root, "input"));
            Directory.CreateDirectory(resolver.Root);
            var exported = await new ExportService(resolver).ExportAsync(doc, [asset], Path.Combine(root, "export"), false, token: Token);
            Assert.Empty(exported.Errors);
            string path = Assert.Single(Directory.GetFiles(exported.Directory, "*.gs", SearchOption.AllDirectories));
            Assert.Equal(expected, await File.ReadAllTextAsync(path, Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MalformedLateTokenIsRefusedWithoutDecodingItsValidPrefix()
    {
        byte[] strings = new byte[200_000]; strings[^1] = 1;
        byte[] bytes = Pack(strings, strings.Length);
        _ = FormatRegistry.Default.OpenBytes("warm.zbd", Pack([0], 1), token: Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("broken.zbd", bytes, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("Unterminated"));
        Assert.Null(doc.Scripts); Assert.InRange(allocated, 0, 128 * 1024);
    }

    [Fact]
    public void TinyInstructionsHaveAPackageWideCountLimit()
    {
        byte[] bytes = Pack([0], 1, ScriptReader.MaximumInstructions + 1);
        var doc = FormatRegistry.Default.OpenBytes("many-lines.zbd", bytes, token: Token);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("65,536")); Assert.Null(doc.Scripts);
    }

    [Fact]
    public void DecodeAndExplicitTextExportHonorCancellation()
    {
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FormatRegistry.Default.OpenBytes("cancel.zbd", Pack([0], 1), token: canceled.Token));
        var doc = FormatRegistry.Default.OpenBytes("valid.zbd", Pack([0], 1), token: Token);
        var content = Assert.IsType<ScriptContent>(Assert.Single(doc.Assets).Content);
        Assert.ThrowsAny<OperationCanceledException>(() => content.GetText(canceled.Token));
    }

    [Fact]
    public void SmallTextKeepsEmptyWhitespaceAndBackslashQuoting()
    {
        var doc = FormatRegistry.Default.OpenBytes("small.zbd", ContentFixture.Scripts(), token: Token);
        var content = Assert.IsType<ScriptContent>(doc.Assets[0].Content);
        Assert.False(content.TextTruncated);
        Assert.Equal("\"strange command\" \"\" \"é\n\\\\\\\"\"\n", content.GetText(Token));
        Assert.Equal(content.Text, content.PreviewText);
    }

    [Fact]
    public void ReconstructionReservesDecodedReferencesRatherThanOnlyRawBytes()
    {
        long bytes = 200_152;
        Assert.True(SourceExtractor.ScriptDecodeReservation(bytes) > 64 * bytes);
        Assert.Throws<IOException>(() => SourceExtractor.RequireReconstructionCapacity("interp.zbd", 0,
            SourceExtractor.ScriptDecodeReservation(bytes), 8 * bytes));
    }

    [Fact]
    public void DecodeReservesEscapedPreviewBeforeCreatingStringsButCapsItsReservation()
    {
        byte[] small = new byte[1001]; small.AsSpan(0, 1000).Fill((byte)'"');
        byte[] shortRecord = Pack(small, 1)[140..];
        // Decoded UTF-16 alone fits; the simultaneous escaped preview does not.
        Assert.Throws<InvalidDataException>(() => ScriptReader.Decode(shortRecord, Token, 16_000));
        Assert.False(ScriptReader.Decode(shortRecord, Token, 20_000).TextTruncated);

        byte[] large = new byte[128 * 1024 + 1]; large.AsSpan(0, large.Length - 1).Fill((byte)'"');
        var content = ScriptReader.Decode(Pack(large, 1).AsMemory(140), Token, 900_000);
        Assert.True(content.TextTruncated); Assert.Equal(65_536, content.PreviewText.Length);
        Assert.Equal(large.Length - 1, Assert.Single(content.Instructions)[0].Length);
    }
}
