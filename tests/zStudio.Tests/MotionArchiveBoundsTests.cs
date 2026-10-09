using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class MotionArchiveBoundsTests
{
    [Fact]
    public void LargeMotionTracksUseDenseStorageWithoutPerSampleObjects()
    {
        using MemoryStream stream = new(); using BinaryWriter w = new(stream);
        w.Write(4); w.Write(1f); w.Write(100_000); w.Write(1); w.Write(-1f); w.Write(1f);
        w.Write(4); w.Write("body"u8); w.Write(12);
        for (int i = 0; i < 100_001 * 3; i++) w.Write(0f);
        for (int i = 0; i < 100_001; i++) { w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f); }
        byte[] source = stream.ToArray(); long before = GC.GetAllocatedBytesForCurrentThread();
        var clip = MotionClip.Read(source, TestContext.Current.CancellationToken);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4_100_000);
        Assert.Equal(100_001, clip.Parts[0].Frames.Count); Assert.Equal(source, clip.Write(TestContext.Current.CancellationToken));
    }
    [Fact]
    public void ClipsBeyondTheArchiveSampleBudgetAreRejectedFromTheHeader()
    {
        byte[] header = new byte[24]; BinaryPrimitives.WriteInt32LittleEndian(header, 4); BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 100_000); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 21);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(16), -1); BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(20), 1);
        var error = Assert.Throws<InvalidDataException>(() => MotionClip.Read(header, TestContext.Current.CancellationToken));
        Assert.Contains("Motion sample count 2,100,021 exceeds the supported 2,097,152", error.Message);
    }
    [Fact]
    public async Task ArchiveMotionSamplesShareOneBudgetAndEditsCannotDemoteAClip()
    {
        var token = TestContext.Current.CancellationToken;
        // Complete, structurally valid clips: together the first two use exactly the archive budget.
        byte[] large = Clip(63, 32_767), small = Clip(1, 32_767), extra = Clip(1, 1);
        var doc = FormatRegistry.Default.OpenBytes("motion.zbd", ResourceEditingTests.Archive(("large", large), ("small", small), ("extra", extra)), token: token);
        Assert.Equal([AssetKind.Motion, AssetKind.Motion, AssetKind.Raw], doc.Assets.Select(a => a.Kind));
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Warning" && d.Message.Contains("Archive member 2 (extra)", StringComparison.Ordinal) &&
            d.Message.Contains("2,097,152 decoded motion samples per archive", StringComparison.Ordinal));
        Assert.Equal(extra, doc.Slice(doc.Assets[2].Offset, doc.Assets[2].Length).ToArray());
        // Growing a clip past the shared budget is rejected instead of silently leaving a raw member.
        var edits = new ResourceEditSession(doc);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareMotionAsync(edits.Current.Members[1].Id, "insert", frame: 0, token: token));
        Assert.Contains("would exceed the supported 2,097,152 decoded motion samples", error.Message);
        Assert.False(edits.IsDirty);
    }
    /// <summary>A complete version 4 clip with identity rotations.</summary>
    private static byte[] Clip(int parts, int frames)
    {
        using MemoryStream stream = new(); using BinaryWriter w = new(stream);
        w.Write(4); w.Write(1f); w.Write(frames); w.Write(parts); w.Write(-1f); w.Write(1f);
        byte[] translations = new byte[(frames + 1) * 12], rotations = new byte[(frames + 1) * 16];
        for (int f = 0; f <= frames; f++) BinaryPrimitives.WriteSingleLittleEndian(rotations.AsSpan(f * 16), 1);
        for (int p = 0; p < parts; p++) { w.Write(4); w.Write("body"u8); w.Write(12); w.Write(translations); w.Write(rotations); }
        return stream.ToArray();
    }
    [Fact]
    public void AliasedMotionMembersShareOneDecodedPayloadAndKeepDistinctRecords()
    {
        var token = TestContext.Current.CancellationToken;
        byte[] payload = Clip(33, 1);
        // Warm the shared reader before measuring only the alias expansion, not first-use initialization.
        _ = FormatRegistry.Default.OpenBytes("warm.zbd", ResourceEditingTests.Archive(("motion", payload)), token: token);
        using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
        writer.Write(payload);
        int size = (int)stream.Length;
        const int aliases = 1024;
        for (int i = 0; i < aliases; i++)
        {
            writer.Write(0); writer.Write(size);
            byte[] metadata = new byte[140]; Encoding.Latin1.GetBytes("motion_" + i).CopyTo(metadata, 0); writer.Write(metadata);
        }
        writer.Write(1); writer.Write(aliases);
        byte[] source = stream.ToArray(); long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("aliases.zbd", source, token: token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // One small shared payload must not materialize a 32-part JSON tree for every directory alias.
        Assert.True(allocated < 16_000_000, $"Opening motion aliases allocated {allocated:N0} bytes.");
        Assert.Empty(doc.Diagnostics); Assert.Equal(aliases, doc.Assets.Count);
        var clip = Assert.IsType<MotionClip>(doc.Assets[0].Content);
        Assert.All(doc.Assets, a => { Assert.Equal(AssetKind.Motion, a.Kind); Assert.Same(clip, a.Content); });
        Assert.Equal(aliases, doc.Assets.Select(a => a.Id).Distinct().Count());
        Assert.Equal(32, doc.Assets[0].Metadata["motion"]!["parts"]!.AsArray().Count);
        Assert.InRange(doc.Assets.Sum(a => a.Metadata["motion"]!["parts"]!.AsArray().Count), 32, 4096);
        var last = doc.Assets[^1];
        Assert.Empty(last.Metadata["motion"]!["parts"]!.AsArray());
        Assert.True(last.Metadata["motion"]!["parts_truncated"]!.GetValue<bool>());
        Assert.Equal(33, last.Metadata["motion"]!["part_count"]!.GetValue<int>());
        // An omitted ordinary preview still has full authored content and complete explicit JSON/raw export.
        var complete = ExportService.AssetJson(doc, last, token)["properties"]!["motion"]!;
        Assert.Equal(33, complete["parts"]!.AsArray().Count);
        Assert.False(complete["parts_truncated"]!.GetValue<bool>());
        Assert.Equal(payload, doc.Slice(last.Offset, last.Length).ToArray());
        Assert.Equal(payload, clip.Write(token));
    }
}
