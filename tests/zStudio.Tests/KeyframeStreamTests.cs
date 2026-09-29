using System.Buffers.Binary;
using Recoil.Zbd.Core.Animation;
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
    [Fact]
    public void InvalidTailAndChangedCountCannotReuseValidatedIndex()
    {
        byte[] bytes = new byte[48]; bytes[0] = 12;
        var ev = new AnimationEvent(bytes) { Version = 39 }; ev.SetInt(16, 1);
        Assert.Single(ev.Keyframes(TestContext.Current.CancellationToken)); ev.SetInt(16, 0);
        Assert.Throws<InvalidDataException>(() => ev.Keyframes(TestContext.Current.CancellationToken));
    }
}
