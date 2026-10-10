using System.Collections;
using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationDecompileBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Root = "data/m1/zrdr/anim.zad", Path = "data/m1/zrdr/move.zan";
    private const string Definition = "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( gate ) SEQUENCE_DEFINITION ( NAME ( go ) OBJECT_MOTION_SI_SCRIPT ( NAME ( door ) SCRIPT_FILENAME ( move.zan ) SCRIPT_FRAME_RATE ( 1 ) ) ) ) ) ) )";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidLargeStreamsRefuseBeforeBuildingUnparseableTrackText(bool continuous)
    {
        var frames = Frames(60_000, continuous);
        _ = AnimationScript.Decompile([frames[0]], 1, Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => AnimationScript.Decompile(frames, 1, Token));
        Assert.Contains(continuous ? "source limit" : "track exceeds", error.Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1L << 20);
    }

    [Fact]
    public void ExactFormattingPreservesRatesSignedZeroGapsAndReversedSegments()
    {
        const string expected = "FRAME 0 POSITION -0 1E-30 -2 VELOCITY 0 1 -1 ROTATION 1 0 0 0 SPIN 0 0 0 SCALE 1 2 3 GROWTH 0 0 0\nFRAME 2\nFRAME 4 POSITION 5 6 7 VELOCITY 0 0 0\nFRAME 3\n";
        var parsed = AnimationScript.Parse(Encoding.Latin1.GetBytes(expected), "expected.zan", Token);
        var frames = AnimationScript.Compile(parsed.Single().Keys, 1, "expected.zan", Token);
        Assert.Equal(expected, AnimationScript.Decompile(frames, 1, Token));
        Assert.Equal(expected, AnimationScript.Decompile(frames, 1, Token, expected.Length, null));
        Assert.Contains("source limit", Assert.Throws<InvalidDataException>(() => AnimationScript.Decompile(frames, 1, Token, expected.Length - 1, null)).Message);
    }

    [Fact]
    public void CancellationDuringMeasurementStopsBeforeAnotherFrameAndFreshCallStillWorks()
    {
        using CancellationTokenSource stop = new();
        var frames = new ObservedFrames(Frames(100, true), () => stop.Cancel());
        var error = Assert.Throws<OperationCanceledException>(() => AnimationScript.Decompile(frames, 1, stop.Token));
        Assert.Equal(stop.Token, error.CancellationToken);
        Assert.Equal(1, frames.Reads);
        Assert.NotNull(AnimationScript.Decompile(Frames(2, true), 1, Token));
        Assert.Throws<OperationCanceledException>(() => AnimationScript.Decompile([], 1, new CancellationToken(true)));
    }

    [Fact]
    public void SiPrepassesAndLazyTrackIndexObserveCancellationWithoutPublishingPartialCache()
    {
        using CancellationTokenSource writerStop = new();
        var observed = new ObservedList<AnimationKeyframe>(Frames(100, true), () => writerStop.Cancel());
        var writerError = Assert.Throws<OperationCanceledException>(() => SiScriptWriter.Write([new("door", observed, 1)], new(null), writerStop.Token));
        Assert.Equal(writerStop.Token, writerError.CancellationToken);
        Assert.Equal(1, observed.Reads);

        using CancellationTokenSource indexStop = new();
        var labels = new ObservedList<int>([0, 1, 2], () => indexStop.Cancel());
        Assert.Throws<OperationCanceledException>(() => new SiScriptWriter.FrameIndex(labels, indexStop.Token));
        Assert.Equal(1, labels.Reads);
        using CancellationTokenSource rangeStop = new();
        var index = new SiScriptWriter.FrameIndex([0, 1, 2], rangeStop.Token);
        var range = new ObservedList<int>([0, 1, 2], () => rangeStop.Cancel());
        Assert.Throws<OperationCanceledException>(() => index.Range(range, "door"));
        Assert.Equal(1, range.Reads);

        using CancellationTokenSource cacheStop = new();
        SiAnimationScript.Pose pose = new([1, 1, 1], [0, 0, 0], [0, 0, 0]);
        var poses = new ObservedList<(string Object, SiAnimationScript.Pose Pose)>([("door", pose), ("other", pose)], () => cacheStop.Cancel());
        SiAnimationScript.Script script = new([new(1, poses), new(2, [("door", pose)])], ["door", "other"]);
        var cacheError = Assert.Throws<OperationCanceledException>(() => SiAnimationScript.Compile(script, "door", 1, "cancel.zan", cacheStop.Token));
        Assert.Equal(cacheStop.Token, cacheError.CancellationToken);
        Assert.Equal(1, poses.Reads);
        Assert.Single(SiAnimationScript.Compile(script, "door", 1, "retry.zan", Token));
        Assert.True(script.Has("other", Token));
    }

    [Fact]
    public void WritersReserveTheirBuilderAndStringBeforeAllocationAndShareTheOperationAllowance()
    {
        var frames = Frames(2, true);
        string expected = AnimationScript.Decompile(frames, 1, Token)!;
        long remaining = 64 + 4L * expected.Length;
        void Reserve(long bytes)
        {
            if (bytes > remaining) throw new IOException("shared text allowance");
            remaining -= bytes;
        }
        Assert.Equal(expected, AnimationScript.Decompile(frames, 1, Token, SourceProject.MaximumSourceTextBytes, Reserve));
        Assert.Equal(0, remaining);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<IOException>(() => AnimationScript.Decompile(frames, 1, Token, SourceProject.MaximumSourceTextBytes, Reserve));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 20_000);
        Assert.Throws<IOException>(() => AnimationScript.Write([("door", expected)], Token, Reserve));
    }

    [Fact]
    public void SiTextUsesTheSamePreappendLimitWithoutChangingItsLayout()
    {
        List<(string Object, int Start, long[]?[] Values)> objects = [("door", 0, [[1_000_000, 1_000_000, 1_000_000], [0, 0, 0], [0, 0, 0]])];
        var layout = new SiScriptWriter.Layout("3.7");
        string expected = SiScriptWriter.Text(objects, [0], layout, Token);
        Assert.Equal(expected, SiScriptWriter.Text(objects, [0], layout, Token, expected.Length));
        Assert.Contains("source limit", Assert.Throws<InvalidDataException>(() => SiScriptWriter.Text(objects, [0], layout, Token, expected.Length - 1)).Message);
        string hugeVersion = new('v', 1_000_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => SiScriptWriter.Text(objects, [0], new(hugeVersion), Token, 100));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 20_000);
    }

    [Fact]
    public void ReconstructionPropagatesWriterCapacityBeforeRetainingOversizedText()
    {
        var (files, package) = Mission(60_000);
        // The writer/readback fixture carries a valid 5.76 MB event with enough full channels to exceed text capacity.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => AnimationSources.Reconstruct([new(1, package, [], ["gate", "door"])], files, [], Token));
        Assert.Contains("source limit", error.Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 100L << 20);
        Assert.False(files.Exists(Path));
    }

    [Fact]
    public void ReconstructionUsesItsRemainingAllowanceWhileMeasuringAndDoesNotPoisonRetry()
    {
        var (files, package) = Mission(1000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<IOException>(() => AnimationSources.Reconstruct([new(1, package, [], ["gate", "door"])], files, [], Token,
            maximumRetainedBytes: 256_000 + 4096));
        Assert.Contains("memory limit", error.Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1L << 20);
        Assert.False(files.Exists(Path));
        var output = Assert.Single(AnimationSources.Reconstruct([new(1, package, [], ["gate", "door"])], files, [], Token));
        Assert.Equal(Path, output.Path);
        files.Data[Path] = output.Bytes;
        var rebuilt = AnimationCompiler.Compile(files, Root, ["gate", "door"], Token).Package;
        Assert.Null(AnimationComparer.Difference(package.Entries[1], rebuilt.Entries[1], Token));
    }

    private static AnimationKeyframe[] Frames(int count, bool continuous)
    {
        AnimationKeyframe[] frames = new AnimationKeyframe[count];
        for (int i = 0; i < count; i++)
        {
            var frame = AnimationKeyframe.Create(7);
            frame.Start = continuous ? i : 0; frame.End = continuous ? i + 1 : 1;
            for (int channel = 0; channel < 3; channel++)
            {
                int offset = frame.ChannelOffset(channel);
                for (int value = 0; value < (channel == 1 ? 4 : 3); value++) frame.SetFloat(offset + value * 4, -1.2345678E-30f);
                for (int value = 0; value < 3; value++) frame.SetFloat(offset + 16 + value * 4, -1.2345678E-30f);
            }
            // Keep the quaternion valid for the source parser as well as the binary reader. Other components/rates
            // retain long round-trip text, so the large fixture still crosses the source-text capacity.
            frame.SetFloat(frame.ChannelOffset(1), 1);
            frame.Validate(); frames[i] = frame;
        }
        return frames;
    }

    private static (Files Files, AnimationPackage Package) Mission(int frames)
    {
        Files files = new(new() { [Root] = Encoding.Latin1.GetBytes(Definition), [Path] = Encoding.Latin1.GetBytes("OBJECT door\nFRAME 0 POSITION 0 0 0\nFRAME 1\n") });
        var package = AnimationCompiler.Compile(files, Root, ["gate", "door"], Token).Package;
        var sequence = package.Entries[1].Sequences.Single();
        var ev = sequence.Events.Single(e => e.Type == 12);
        sequence.Events[sequence.Events.IndexOf(ev)] = ev.WithKeyframes(Frames(frames, true));
        package = AnimationPackage.Read(AnimationWriter.Write(package, Token), Token);
        files.Data.Remove(Path);
        return (files, package);
    }

    private sealed class ObservedFrames(AnimationKeyframe[] frames, Action read) : IReadOnlyList<AnimationKeyframe>
    {
        internal int Reads;
        public int Count => frames.Length;
        public AnimationKeyframe this[int index] { get { Reads++; read(); return frames[index]; } }
        public IEnumerator<AnimationKeyframe> GetEnumerator() => ((IEnumerable<AnimationKeyframe>)frames).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class ObservedList<T>(IReadOnlyList<T> values, Action read) : IReadOnlyList<T>
    {
        internal int Reads;
        public int Count => values.Count;
        public T this[int index] { get { Reads++; read(); return values[index]; } }
        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Files(Dictionary<string, byte[]> data) : IProjectFiles
    {
        internal Dictionary<string, byte[]> Data => data;
        public bool Exists(string path) => data.ContainsKey(path);
        public byte[] Read(string path, CancellationToken token) => Read(path, token, ProjectReadLimits.Document);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] bytes = data[path]; limits.Validate(bytes); return bytes; }
    }
}
