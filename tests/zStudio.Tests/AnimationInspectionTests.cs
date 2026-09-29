using System.Buffers.Binary;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationInspectionTests
{
    [Theory]
    [InlineData(28)]
    [InlineData(39)]
    public void ReferenceSequenceAndStampPreviewsKeepCountsAndCompleteExports(int version)
    {
        var token = TestContext.Current.CancellationToken;
        byte[] seed = Fixture(version, 12, false), stamped = new byte[seed.Length + 20 * 84];
        seed.AsSpan(0, 12).CopyTo(stamped); seed.AsSpan(12).CopyTo(stamped.AsSpan(12 + 20 * 84));
        BinaryPrimitives.WriteInt32LittleEndian(stamped.AsSpan(8), 20);
        var package = AnimationPackage.Read(stamped, token); var entry = package.Entries[0];
        int[] sizes = [96, 40, 44, 44, 36, 36, 48, 72];
        for (int table = 0; table < sizes.Length; table++)
            for (int i = 0; i < 255; i++) entry.References[table].Add(new(new byte[sizes[table]]));
        for (int i = 0; i < 255; i++) entry.Sequences.Add(new(new byte[64]));
        if (version == 39) for (int i = 0; i < 255; i++) entry.Puffers.Add(new(new byte[44]));
        byte[] bytes = AnimationWriter.Write(package, token);
        var doc = FormatRegistry.Default.OpenBytes("references.zbd", bytes, token: token);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "Error");
        Assert.Equal(20, doc.Metadata["stamp_count"]!.GetValue<int>()); Assert.Equal(16, doc.Metadata["stamps"]!.AsArray().Count);
        Assert.True(doc.Metadata["stamps_truncated"]!.GetValue<bool>());
        var preview = doc.Assets[0].Metadata;
        Assert.All(preview["reference_counts"]!.AsArray(), n => Assert.Equal(255, n!.GetValue<int>()));
        Assert.All(preview["references"]!.AsArray(), n => Assert.Equal(4, n!.AsArray().Count));
        Assert.Equal(256, preview["sequence_count"]!.GetValue<int>()); Assert.Equal(16, preview["sequences"]!.AsArray().Count);
        Assert.True(preview["references_truncated"]!.GetValue<bool>()); Assert.True(preview["sequences_truncated"]!.GetValue<bool>());
        var full = ExportService.AssetJson(doc, doc.Assets[0], token)["properties"]!;
        Assert.All(full["references"]!.AsArray(), n => Assert.Equal(255, n!.AsArray().Count));
        Assert.Equal(256, full["sequences"]!.AsArray().Count);
        Assert.Equal(version == 39 ? 255 : 0, full["puffer_references"]!.AsArray().Count);
        Assert.Equal(bytes, AnimationWriter.Write(doc.Animations!, token));
    }
    [Theory]
    [InlineData(28, false)]
    [InlineData(39, false)]
    [InlineData(28, true)]
    [InlineData(39, true)]
    public void OpeningAndInspectionDoNotExpandLargePayloads(int version, bool opaque)
    {
        var token = TestContext.Current.CancellationToken;
        // Warm layout/catalog initialization before measuring only ordinary open allocations.
        FormatRegistry.Default.OpenBytes("warm.zbd", Fixture(version, 12, false), token: token);
        byte[] bytes = Fixture(version, 2 * 1024 * 1024, opaque);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("animation.zbd", bytes, token: token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var asset = Assert.Single(doc.Assets);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "Error");
        Assert.True(allocated < bytes.Length * 3L, $"Ordinary opening allocated {allocated} bytes for {bytes.Length} source bytes.");
        Assert.True(asset.Metadata.ToJsonString().Length < 32768);
        before = GC.GetAllocatedBytesForCurrentThread();
        var preview = ExportService.AssetJson(doc, asset, token, boundedZrd: true);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256 * 1024);
        Assert.True(preview.ToJsonString().Length < 32768);
        var full = ExportService.AssetJson(doc, asset, token);
        var sequence = full["properties"]!["sequences"]![0]!;
        string hex = (opaque ? sequence["opaque_tail_hex"] : sequence["events"]![0]!["raw_hex"])!.GetValue<string>();
        Assert.Equal(Convert.ToHexStringLower(bytes.AsSpan(bytes.Length - 2 * 1024 * 1024)), hex);
        Assert.Equal(bytes, Recoil.Zbd.Core.Animation.AnimationWriter.Write(doc.Animations!, token));
    }

    private static byte[] Fixture(int version, int payloadLength, bool opaque)
    {
        int prefix = version == 39 ? 80 : 72, header = version == 39 ? 316 : 308;
        byte[] bytes = new byte[prefix + header + 64 + payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x08170616);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), 1 << 16);
        int sequence = prefix + header;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sequence + 60), payloadLength);
        bytes[sequence + 64] = 254; bytes[sequence + 65] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sequence + 68), opaque ? 0 : payloadLength);
        bytes[^1] = 0x7b;
        return bytes;
    }
}
