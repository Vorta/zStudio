using System.Collections;
using System.Numerics;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class MotionPreviewTests
{
    [Fact]
    public void MaximumPartAndNodeCountsBindWithoutRepeatedAssemblyScans()
    {
        var token = TestContext.Current.CancellationToken;
        GameScene scene = new(); scene.Models.Add(new(0, [], [], [], [], []));
        MotionPart[] parts = Enumerable.Range(0, 4096).Select(i => Part($"part_{i}")).ToArray();
        for (int i = 0; i < 200_000; i++)
            scene.Nodes.Add(new(i, $"part_{i}", "object3d", i < parts.Length ? 0 : null, i == 0 ? [] : [0],
                i == 0 ? Enumerable.Range(1, parts.Length - 1).ToArray() : [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
        var preview = new MotionPreview(Clip(parts), Library(scene), new(0, 0, scene.Nodes.Count, 0, 1), token);
        Assert.Single(preview.Diagnostics);
        var frame = preview.At(.5, token: token);
        Assert.Equal(parts.Length, frame.Nodes.Count);
        Assert.Equal(new Vector3(1, 0, 0), frame.Nodes.Single(n => n.SourceNode == 0).Transform.Translation);
        Assert.All(frame.Nodes.Where(n => n.SourceNode != 0), n => Assert.Equal(new Vector3(2, 0, 0), n.Transform.Translation));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++) Assert.Equal(parts.Length, preview.At(.125 * i, token: token).Nodes.Count);
        long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocation < 8_000_000, $"Warm frame sampling allocated {allocation:N0} bytes; hierarchy construction must stay out of the frame loop.");
    }

    [Fact]
    public void BindingIsOrdinalScopedToTheMemberAndRetainsAmbiguousStoredPoses()
    {
        GameScene scene = new(); scene.Models.Add(new(0, [], [], [], [], []));
        string[] names = ["body", "root", "body", "arm", "arm", "BODY", "body"];
        for (int i = 0; i < names.Length; i++)
            scene.Nodes.Add(new(i, names[i], "object3d", i is >= 2 and <= 5 ? 0 : null, i is >= 2 and <= 5 ? [1] : [],
                i == 1 ? [2, 3, 4, 5] : [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
        var preview = new MotionPreview(Clip([Part("body"), Part("arm"), Part("missing")]), Library(scene), new(0, 1, 5, 0, 1));
        Assert.Equal(3, preview.Diagnostics.Count);
        var frame = preview.At(.5, token: TestContext.Current.CancellationToken);
        Assert.Equal(new Vector3(1, 0, 0), frame.Nodes.Single(n => n.SourceNode == 2).Transform.Translation);
        Assert.All(frame.Nodes.Where(n => n.SourceNode != 2), n => Assert.Equal(Matrix4x4.Identity, n.Transform));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("counting")]
    [InlineData("binding")]
    public void BindingObservesCancellationBeforeAndDuringTrackPasses(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        GameScene scene = new(); scene.Nodes.Add(new(0, "body", "object3d", null, [], [], [], []));
        var parts = new ObservedParts(Part("body"), enumerate => { if (phase == (enumerate ? "counting" : "binding")) cancellation.Cancel(); });
        if (phase == "before") cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => new MotionPreview(Clip(parts), Library(scene), new(0, 0, 1, 0, 0), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private static MotionPart Part(string name) => new(name, 0, [new(Vector3.Zero, Quaternion.Identity), new(new(2, 0, 0), Quaternion.Identity)], ReadOnlyMemory<byte>.Empty);
    private static MotionClip Clip(IReadOnlyList<MotionPart> parts) => new() { Header = ReadOnlyMemory<byte>.Empty, LoopTime = 1, FrameCount = 1, Parts = parts };
    private static ZbdDocument Library(GameScene scene) => new("library.zbd", new(0, DateTime.MinValue), new(FormatFamily.Archive, 1, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
    private sealed class ObservedParts(MotionPart part, Action<bool> read) : IReadOnlyList<MotionPart>
    {
        public int Count => 1;
        public MotionPart this[int index] { get { read(false); return part; } }
        public IEnumerator<MotionPart> GetEnumerator() { read(true); yield return part; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
