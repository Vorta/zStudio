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
        // Reversed and negative times are authored semantics, not malformed layouts; see ReversedAndNegativeTimesRemainReadableAndEditable.
        var mutations = new (int Offset, int Bits)[] { (0, 8), (0, -1), (8, 0x7f800000), (4, 0x7fc00000), (12, 0x7fc00000), (12 + stride, 0x7f800000), (12 + 2 * stride + 24, unchecked((int)0xff800000)) };
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
        ev.SetInt((version == 39 ? 36 : 32) + 4, 0x7fc00000); // A NaN start cannot be sampled or ordered.
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
    [Theory]
    [InlineData(28)]
    [InlineData(39)]
    public void ReversedAndNegativeTimesRemainReadableAndEditable(uint version)
    {
        var token = TestContext.Current.CancellationToken;
        var reversed = AnimationKeyframe.Create(1); reversed.Start = 4.5f; reversed.End = 4; reversed.SetFloat(12, 7);
        var negative = AnimationKeyframe.Create(3); negative.Start = -1; negative.End = 2;
        var ev = AnimationCatalog.Create(12, version).WithKeyframes([AnimationKeyframe.Create(1), reversed.ForVersion(version), negative.ForVersion(version)]);
        byte[] original = ev.Bytes.ToArray();
        var frames = ev.Keyframes(token);
        Assert.Equal(3, frames.Count); Assert.Equal(4.5f, frames[1].Start); Assert.Equal(4, frames[1].End); Assert.Equal(-1, frames[2].Start);
        Assert.Equal(3, Assert.IsType<System.Text.Json.Nodes.JsonArray>(ev.ToJson(token)["keyframes"]).Count);
        // Editing an unrelated segment never requires repairing authored reversed/negative spans.
        var list = new KeyframeEditList(ev.Keyframes(token)); list[0].End = 3;
        var edited = ev.WithKeyframes(list);
        Assert.Equal(3, edited.Keyframes(token)[0].End);
        int first = (version == 39 ? 36 : 32) + 12 + (version == 39 ? 76 : 28);
        Assert.Equal(original.AsSpan(first).ToArray(), edited.Bytes.AsSpan(first).ToArray());
        Assert.Equal(original, ev.WithKeyframes(ev.Keyframes(token)).Bytes);
        if (version == 28)
        {
            Assert.Equal(3, ev.PlaybackKeyframes().Count);
            Assert.Null(ev.ToPreviewJson(token)["keyframe_diagnostic"]);
        }
        else
        {
            // MW3 runtime meaning for these spans is unverified: inspection stays available, preview does not.
            Assert.Contains("Keyframe 1", Assert.Throws<InvalidDataException>(() => ev.PlaybackKeyframes()).Message);
            Assert.Contains("unverified runtime meaning", ev.ToPreviewJson(token)["keyframe_diagnostic"]!.GetValue<string>());
            Assert.Contains("unverified runtime meaning", ev.ToJson(token)["keyframe_diagnostic"]!.GetValue<string>());
        }
        Assert.Equal(original, ev.Bytes);
    }
    [Theory]
    [InlineData(28)]
    [InlineData(39)]
    public void ReversedSpanOpeningDiagnosticIsLimitedToUnverifiedMw3Preview(uint version)
    {
        var token = TestContext.Current.CancellationToken;
        byte[] prefix = new byte[version == 39 ? 80 : 72];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), version);
        var package = new AnimationPackage { Prefix = prefix, Tail = [] };
        var entry = new AnimationEntry(new byte[version == 39 ? 316 : 308], 0, prefix.Length);
        package.Entries.Add(entry);
        var reversed = AnimationKeyframe.Create(1); reversed.Start = 2;
        entry.Primary.Events.Add(AnimationCatalog.Create(12, version).WithKeyframes([AnimationKeyframe.Create(1), reversed.ForVersion(version)]));
        var bytes = AnimationWriter.Write(package, token);
        var doc = FormatRegistry.Default.OpenBytes("animation.zbd", bytes, token: token);
        var read = Assert.Single(doc.Animations!.Entries[0].Primary.Events);
        Assert.Equal(2, read.Keyframes(token).Count);
        if (version == 28) Assert.DoesNotContain(doc.Diagnostics, d => d.Message.Contains("Keyframe"));
        else Assert.Contains(doc.Diagnostics, d => d.Severity == "Warning" && d.Message.Contains("Keyframe 1") && d.Message.Contains("transform preview is unavailable"));
        Assert.Equal(bytes, AnimationWriter.Write(doc.Animations, token));
    }
    [Fact]
    public async Task RetailRecoilReversedSpansOpenWithoutKeyframeWarnings()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (string.IsNullOrEmpty(root)) return;
        string path = Path.Combine(root, "m3", "anim.zbd"); if (!File.Exists(path)) return;
        var token = TestContext.Current.CancellationToken;
        var doc = await FormatRegistry.Default.OpenAsync(path, token);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Message.Contains("Keyframe"));
        var events = doc.Animations!.Entries.Where(e => e.Name == "m3pickup").SelectMany(e => e.AllSequences).SelectMany(s => s.Events).Where(e => e.Type == 12)
            .Where(e => e.Keyframes(token).Any(f => f.End < f.Start)).ToArray();
        Assert.Equal(3, events.Length);
        Assert.All(events, e => { Assert.Equal(15, e.Keyframes(token).Count); Assert.Equal(15, e.PlaybackKeyframes().Count); Assert.Null(e.ToPreviewJson(token)["keyframe_diagnostic"]); });
        Assert.Equal(doc.Bytes.ToArray(), AnimationWriter.Write(doc.Animations, token));
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
