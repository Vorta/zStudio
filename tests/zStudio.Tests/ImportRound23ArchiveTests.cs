using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23ArchiveTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DistinctOverlappingZrdRangesHaveOneAllocationAllowanceBeforeDecoding()
    {
        byte[] bytes = Overlapping(64, 4096);
        var document = Document(bytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        new ArchiveReader(32 * 1024).Read(document, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 500_000);
        Assert.Equal(64, document.Assets.Count);
        Assert.InRange(document.Assets.Count(a => a.Kind == AssetKind.Zrd), 1, 3);
        Assert.Contains(document.Diagnostics, d => d.Severity == "Warning" && d.Message.Contains("allocation budget", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Diagnostics, d => d.Severity == "Error");
        foreach (var asset in document.Assets)
            Assert.Equal(bytes.AsSpan((int)asset.Offset, (int)asset.Length).ToArray(), document.Slice(asset.Offset, asset.Length).ToArray());
        ResourceEditSession edits = new(document);
        foreach (var member in edits.Current.Members.Where(m => m.TypedDecodeLimited))
            Assert.Contains("budget", Assert.Throws<InvalidDataException>(() => edits.Tree(member, Token)).Message);
        Assert.Equal(bytes, ArchiveWriter.Write(document, edits.Current.Members, Token));
    }

    [Fact]
    public void ExactAliasesShareTheirTreeAndDoNotSpendTheAllowanceAgain()
    {
        byte[] bytes = Overlapping(64, 1024, aliases: true);
        var document = Document(bytes);
        new ArchiveReader(3000).Read(document, Token);
        var tree = Assert.IsType<ZrdNode>(document.Assets[0].Content);
        Assert.All(document.Assets, a => { Assert.Equal(AssetKind.Zrd, a.Kind); Assert.Same(tree, a.Content); });
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void MalformedMemberDiagnosticsAreCappedAndRetainErrorSeverity()
    {
        const int records = 2500;
        byte[] bytes = new byte[records * 148 + 8];
        for (int i = 0; i < records; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 148 + 4), 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), records);
        var document = Document(bytes); new ArchiveReader().Read(document, Token);
        Assert.Empty(document.Assets);
        Assert.Equal(2001, document.Diagnostics.Count);
        Assert.Equal("Error", document.Diagnostics[^1].Severity);
        Assert.Contains("500 further", document.Diagnostics[^1].Message);
    }

    [Fact]
    public async Task LimitedMembersStillExportTheirExactOriginalBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-archive-budget-r23-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] bytes = Overlapping(2, 1024);
            string input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
            var document = Document(bytes, Path.Combine(input, "archive.zbd"));
            new ArchiveReader(3000).Read(document, Token);
            var raw = Assert.Single(document.Assets, a => a.Kind == AssetKind.Raw);
            using AssetResolver resolver = new(input);
            var result = await new ExportService(resolver).ExportAsync(document, [raw], Path.Combine(root, "export"), false, token: Token);
            Assert.Empty(result.Errors);
            var exported = Assert.Single(Directory.GetFiles(result.Directory, "*.zrd", SearchOption.AllDirectories));
            Assert.Equal(document.Slice(raw.Offset, raw.Length).ToArray(), await File.ReadAllBytesAsync(exported, Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReplacementCanRestoreTypedAccessWithoutRetainingTheOldLimitFlag()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-archive-replace-r23-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var document = Document(Overlapping(2, 1024), Path.Combine(root, "archive.zbd"));
            new ArchiveReader(3000).Read(document, Token);
            ResourceEditSession edits = new(document);
            var limited = edits.Current.Members.Last(m => m.TypedDecodeLimited);
            string input = Path.Combine(root, "replacement.zrd");
            byte[] replacement = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(replacement, 1); BinaryPrimitives.WriteInt32LittleEndian(replacement.AsSpan(4), 123);
            await File.WriteAllBytesAsync(input, replacement, Token);
            edits.Accept(await edits.PrepareArchiveAsync("replace", limited.Id, path: input, token: Token));
            var member = edits.Member(limited.Id);
            Assert.False(member.TypedDecodeLimited);
            Assert.Equal(123u, edits.Tree(member, Token).Bits);
            edits.UndoRedo(false);
            Assert.True(edits.Member(limited.Id).TypedDecodeLimited);
            Assert.Throws<InvalidDataException>(() => edits.Tree(edits.Member(limited.Id), Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LargeStringResourcesRemainTypedWhenTheirDecodedShapeFits()
    {
        const int characters = 1024 * 1024;
        byte[] resource = new byte[8 + 2 * (8 + characters)];
        BinaryPrimitives.WriteInt32LittleEndian(resource, 4); BinaryPrimitives.WriteInt32LittleEndian(resource.AsSpan(4), 3);
        for (int i = 0; i < 2; i++)
        {
            int offset = 8 + i * (8 + characters);
            BinaryPrimitives.WriteInt32LittleEndian(resource.AsSpan(offset), 3);
            BinaryPrimitives.WriteInt32LittleEndian(resource.AsSpan(offset + 4), characters);
            resource.AsSpan(offset + 8, characters).Fill((byte)'x');
        }
        var document = FormatRegistry.Default.OpenBytes("names.zbd", ResourceEditingTests.Archive(("names.zrd", resource)), token: Token);
        var tree = Assert.IsType<ZrdNode>(Assert.Single(document.Assets).Content);
        Assert.Equal(2, tree.Children.Count);
        Assert.All(tree.Children, child => Assert.Equal(characters, child.Text.Length));
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void FailedProbesStillSpendTheirDecodedAllocationAllowance()
    {
        byte[] malformed = new byte[256], scalar = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(malformed, 3); BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(4), 240);
        BinaryPrimitives.WriteInt32LittleEndian(scalar, 1);
        var document = Document(ResourceEditingTests.Archive(("trailing.bin", malformed), ("scalar.zrd", scalar)));
        new ArchiveReader(700).Read(document, Token);
        Assert.All(document.Assets, a => Assert.Equal(AssetKind.Raw, a.Kind));
        Assert.True(document.Assets[1].Metadata["typed_decode_limited"]!.GetValue<bool>());
    }

    private static ZbdDocument Document(byte[] bytes, string path = "archive.zbd") => new(path, new(bytes.Length, DateTime.MinValue),
        new(FormatFamily.Archive, 1, Recognition.Supported, "fixture"), bytes);

    private static byte[] Overlapping(int records, int payload, bool aliases = false)
    {
        byte[] bytes = new byte[payload + records * 148 + 8];
        for (int i = 0; i < records; i++)
        {
            int offset = aliases ? 0 : i * 16;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), 4);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 8), 3);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 12), payload - offset - 16);
            int entry = payload + i * 148;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry), offset);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 4), payload - offset);
            Encoding.Latin1.GetBytes($"entry{i}.zrd").CopyTo(bytes, entry + 8);
        }
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), records);
        return bytes;
    }
}
