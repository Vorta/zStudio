using System.Text;
using Recoil.Zbd.Core;
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
    public void AliasedMotionMembersShareOneDecodedPayloadAndKeepDistinctRecords()
    {
        using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
        writer.Write(4); writer.Write(1f); writer.Write(1000); writer.Write(1); writer.Write(-1f); writer.Write(1f);
        writer.Write(4); writer.Write("body"u8); writer.Write(12);
        for (int i = 0; i < 1001 * 3; i++) writer.Write(0f);
        for (int i = 0; i < 1001; i++) { writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f); }
        int size = (int)stream.Length;
        for (int i = 0; i < 64; i++)
        {
            writer.Write(0); writer.Write(size);
            byte[] metadata = new byte[140]; Encoding.Latin1.GetBytes("motion_" + i).CopyTo(metadata, 0); writer.Write(metadata);
        }
        writer.Write(1); writer.Write(64);
        var doc = FormatRegistry.Default.OpenBytes("aliases.zbd", stream.ToArray(), token: TestContext.Current.CancellationToken);
        Assert.Empty(doc.Diagnostics); Assert.Equal(64, doc.Assets.Count);
        var clip = Assert.IsType<MotionClip>(doc.Assets[0].Content);
        Assert.All(doc.Assets, a => { Assert.Equal(AssetKind.Motion, a.Kind); Assert.Same(clip, a.Content); });
        Assert.Equal(64, doc.Assets.Select(a => a.Id).Distinct().Count());
    }
}
