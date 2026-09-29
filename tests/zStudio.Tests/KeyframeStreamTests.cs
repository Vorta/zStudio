using System.Buffers.Binary;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class KeyframeStreamTests
{
    [Theory]
    [InlineData(28)]
    [InlineData(39)]
    public void MillionRecordInspectionUsesSparseIndexAndIndependentEditableViews(uint version)
    {
        int prefix = version == 39 ? 36 : 32;
        byte[] bytes = new byte[prefix + 1_000_000 * 12]; bytes[0] = 12;
        if (version == 39) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 1_000_000);
        var ev = new AnimationEvent(bytes) { Version = version };
        long before = GC.GetAllocatedBytesForCurrentThread();
        var frames = ev.Keyframes(TestContext.Current.CancellationToken);
        Assert.Equal(1_000_000, frames.Count); Assert.Equal(0, frames[999_999].Flags);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
        frames[500_000].Start = 12;
        Assert.Equal(12, frames[500_000].Start);
        Assert.Equal(0, ev.Keyframes(TestContext.Current.CancellationToken)[500_000].Start);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(prefix + 500_000 * 12 + 4)));
    }
    [Theory]
    [InlineData(28)]
    [InlineData(39)]
    public void SparseValidationRejectsMalformedContentsWithoutExecutingThem(uint version)
    {
        var token = TestContext.Current.CancellationToken;
        var valid = AnimationCatalog.Create(12, version).WithKeyframes([AnimationKeyframe.Create(7)]);
        int prefix = version == 39 ? 36 : 32, stride = version == 39 ? 76 : 28;
        var mutations = new (int Offset, int Bits)[] { (0, 8), (0, -1), (4, BitConverter.SingleToInt32Bits(-1)), (4, BitConverter.SingleToInt32Bits(2)), (8, 0x7f800000), (4, 0x7fc00000), (12, 0x7fc00000), (12 + stride, 0x7f800000), (12 + 2 * stride + 24, unchecked((int)0xff800000)) };
        foreach (var (offset, bits) in mutations)
        {
            var ev = valid.Clone(token); ev.SetInt(prefix + offset, bits);
            Assert.Throws<InvalidDataException>(() => ev.Keyframes(token));
            Assert.Throws<InvalidDataException>(() => ev.PlaybackKeyframes());
        }
    }
    [Theory]
    [InlineData(28)]
    [InlineData(39)]
    public void InactiveVectorSlotsAndExtendedBytesStayOpaque(uint version)
    {
        var frame = AnimationKeyframe.Create(5).ForVersion(version);
        frame.SetInt(24, unchecked((int)0xffffffff)); frame.SetInt(24 + frame.ChannelStride, 0x7fc00000);
        if (version == 39) frame.SetInt(12 + 40, 0x7f800000);
        var ev = AnimationCatalog.Create(12, version).WithKeyframes([frame]);
        Assert.Equal(frame.Bytes, Assert.Single(ev.Keyframes(TestContext.Current.CancellationToken)).Bytes);
        Assert.Equal(ev.Bytes, ev.WithKeyframes(ev.Keyframes(TestContext.Current.CancellationToken)).Bytes);
    }
    [Theory]
    [InlineData(28)]
    [InlineData(39)]
    public void InvalidStreamHasAnOpeningDiagnosticAndRoundTripsWithoutRepair(uint version)
    {
        var token = TestContext.Current.CancellationToken;
        byte[] prefix = new byte[version == 39 ? 80 : 72];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), version);
        var package = new AnimationPackage { Prefix = prefix, Tail = [] };
        var entry = new AnimationEntry(new byte[version == 39 ? 316 : 308], 0, prefix.Length);
        package.Entries.Add(entry);
        var ev = AnimationCatalog.Create(12, version).WithKeyframes([AnimationKeyframe.Create(1)]);
        ev.SetFloat((version == 39 ? 36 : 32) + 4, 2); // End remains 1: an unsupported reversed interval.
        entry.Primary.Events.Add(ev);
        var bytes = AnimationWriter.Write(package, token);
        var doc = FormatRegistry.Default.OpenBytes("animation.zbd", bytes, token: token);
        Assert.NotNull(doc.Animations);
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Warning" && d.Message.Contains("Keyframe times"));
        var read = Assert.Single(doc.Animations.Entries[0].Primary.Events);
        Assert.Contains("Keyframe times", read.ToPreviewJson(token)["keyframe_diagnostic"]!.GetValue<string>());
        Assert.Throws<InvalidDataException>(() => read.PlaybackKeyframes());
        Assert.Equal(bytes, AnimationWriter.Write(doc.Animations, token));
        read.SetFloat((version == 39 ? 36 : 32) + 4, 0);
        Assert.Single(read.Keyframes(token));
        Assert.Null(read.ToPreviewJson(token)["keyframe_diagnostic"]);
    }
    [Fact]
    public void InvalidTailAndChangedCountCannotReuseValidatedIndex()
    {
        byte[] bytes = new byte[48]; bytes[0] = 12;
        var ev = new AnimationEvent(bytes) { Version = 39 }; ev.SetInt(16, 1);
        Assert.Single(ev.Keyframes(TestContext.Current.CancellationToken)); ev.SetInt(16, 0);
        Assert.Throws<InvalidDataException>(() => ev.Keyframes(TestContext.Current.CancellationToken));
    }
}
