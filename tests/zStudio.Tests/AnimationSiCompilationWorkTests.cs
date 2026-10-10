using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationSiCompilationWorkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Folder = "data/m1/zrdr/", Root = Folder + "anim.zad";

    [Fact]
    public void StationaryEventsShareWorkEvenThoughEachEmitsOnlyEndpoints()
    {
        var files = Fixture(Event() + Event());
        int visits = 0;
        var error = Assert.Throws<InvalidDataException>(() => Compile(files, 45, Token, () => visits++));
        Assert.Contains("aggregate work limit", error.Message);
        Assert.Equal(45, visits);
        Assert.Single(files.Reads, p => p == Folder + "move.zan");
        var result = Compile(files, 46, Token);
        Assert.Equal(AnimationCompiler.Compile(files, Root, ["gate", "door"], Token).Bytes, result.Bytes);
        Assert.All(result.Package.Entries[1].Sequences[0].Events, e =>
        {
            Assert.Equal(224, e.Bytes.Length);
            Assert.Equal(2, e.Keyframes(Token).Count);
            Assert.Equal(0x3D088889, BitConverter.SingleToInt32Bits(e.Keyframes(Token)[0].End)); //1/30 second
            Assert.Equal(0x3E4CCCCE, BitConverter.SingleToInt32Bits(e.Keyframes(Token)[1].Start)); //rounded6×float(1/30)
        });
        // Indexing eight frame/pose pairs costs16; every compile visits8 poses and7 segments.
        Assert.Equal(3, files.Reads.Count(p => p == Folder + "move.zan"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkDoesNotResetAtWildcardRootsOrCleanup(bool cleanup)
    {
        var files = Fixture(Event(), rootName: cleanup ? "gate" : "gate*", cleanup: cleanup ? Event() : "");
        string[] names = cleanup ? ["gate", "door"] : ["gate1", "gate2", "door"];
        Assert.Throws<InvalidDataException>(() => Compile(files, 45, Token, names: names));
        var result = Compile(files, 46, Token, names: names);
        Assert.Null(result.EngineRejection);
        Assert.Equal(cleanup ? 2 : 3, result.Package.Entries.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AliasesAndDifferentNamingFilesStillShareTheOperationBudget(bool differentFrom)
    {
        var files = Fixture(Event() + Event(file: "./move.zan"));
        if (differentFrom)
        {
            files.Data[Root] = Bytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( one.zad ) ANIMATION_DEFINITION_FILE ( two.zad ) ) )");
            files.Data[Folder + "one.zad"] = Definition(Event());
            files.Data[Folder + "two.zad"] = Definition(Event());
        }
        Assert.Throws<InvalidDataException>(() => Compile(files, 61, Token));
        Assert.Null(Compile(files, 62, Token).EngineRejection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseFallbackCountsEmptyKeysAndLookahead(bool named)
    {
        var files = Fixture(Event() + Event());
        files.Data[Folder + "move.zan"] = Bytes((named ? "OBJECT door\n" : "") +
            "FRAME 0 POSITION 1 2 3\nFRAME 1\nFRAME 2\nFRAME 3\n");
        Assert.Throws<InvalidDataException>(() => Compile(files, 11, Token));
        var result = Compile(files, 12, Token);
        Assert.All(result.Package.Entries[1].Sequences[0].Events, e => Assert.Single(e.Keyframes(Token)));
        Assert.Equal(AnimationCompiler.Compile(files, Root, ["gate", "door"], Token).Bytes, result.Bytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(33)]
    public void CancellationDuringIndexingOrRepeatedConversionLeavesRetryIndependent(int stopAt)
    {
        var files = Fixture(Event() + Event());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int visits = 0;
        Assert.Throws<OperationCanceledException>(() => Compile(files, 100, stop.Token, () =>
        {
            if (++visits == stopAt) stop.Cancel();
        }));
        Assert.Equal(stopAt, visits);
        Assert.Equal(2, Compile(files, 46, Token).Package.Entries[1].Sequences[0].Events.Count);
    }

    [Fact]
    public void CancellationAndRefusalCannotPublishAPartialTrackIndex()
    {
        var script = SiAnimationScript.Parse(Si(), "move.zan", Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int visits = 0;
        var work = new AnimationCompileWork(stop.Token, 100, () => { if (++visits == 3) stop.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => SiAnimationScript.Compile(script, "door", 30, "move.zan", stop.Token, work));
        Assert.Throws<InvalidDataException>(() => SiAnimationScript.Compile(script, "door", 30, "move.zan", Token, new(Token, 15)));
        Assert.Equal(2, SiAnimationScript.Compile(script, "door", 30, "move.zan", Token, new(Token, 31)).Count);
    }

    [Fact]
    public void DifferentRatesRetainTheirBytesAndEventsNeverShareMutableKeys()
    {
        var files = Fixture(Event(rate: 10) + Event(rate: 20) + Event(rate: 10));
        var result = Compile(files, 61, Token);
        var events = result.Package.Entries[1].Sequences[0].Events;
        Assert.Equal(events[0].Bytes, events[2].Bytes);
        Assert.NotEqual(events[0].Keyframes(Token)[0].End, events[1].Keyframes(Token)[0].End);
        byte[] second = events[1].Bytes.ToArray(), third = events[2].Bytes.ToArray();
        // The first v28 key starts after the32-byte event header; edit its actual stored start.
        events[0].SetFloat(36, 123);
        Assert.Equal(123f, events[0].Keyframes(Token)[0].Start);
        Assert.Equal(second, events[1].Bytes);
        Assert.Equal(third, events[2].Bytes);
        Assert.Equal(third, Compile(files, 61, Token).Package.Entries[1].Sequences[0].Events[0].Bytes);
    }

    [Fact]
    public void RawRepeatedLabelsAreChargedBeforeTheyAreSkipped()
    {
        byte[] bytes = Bytes("SI Animation Script\n" + Pose(1) + Pose(1) + Pose(2));
        var script = SiAnimationScript.Parse(bytes, "duplicate.zan", Token);
        // Six index visits, three raw poses (including the duplicate), then one segment.
        Assert.Throws<InvalidDataException>(() => SiAnimationScript.Compile(script, "door", 30, "duplicate.zan", Token, new(Token, 8)));
        var frames = SiAnimationScript.Compile(script, "door", 30, "duplicate.zan", Token, new(Token, 4));
        Assert.Single(frames);
        Assert.Equal(0f, frames[0].Start);
    }

    [Fact]
    public void DistinctObjectsRetainTheirAuthoredChannelsUnderOneIndexBudget()
    {
        var files = Fixture(Event() + Event(target: "lamp"));
        files.Data[Folder + "move.zan"] = Bytes("SI Animation Script\n" + string.Concat(Enumerable.Range(1, 8).Select(i =>
            Pose(i) + "Object: lamp\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 3 4 5\n")));
        string[] names = ["gate", "door", "lamp"];
        Assert.Throws<InvalidDataException>(() => Compile(files, 53, Token, names: names));
        var events = Compile(files, 54, Token, names: names).Package.Entries[1].Sequences[0].Events;
        var door = events[0].Keyframes(Token)[0]; var lamp = events[1].Keyframes(Token)[0];
        Assert.Equal(0f, door.F32(door.ChannelOffset(0)));
        Assert.Equal(3f, lamp.F32(lamp.ChannelOffset(0)));
    }

    [Fact]
    public void FallbackLookaheadObservesCancellationBeforeScanningTheNextKey()
    {
        var keys = AnimationScript.Parse(Bytes("FRAME 0 POSITION 1 2 3\nFRAME 1\nFRAME 2\nFRAME 3\n"), "sparse.zan", Token)[0].Keys;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int visits = 0;
        var work = new AnimationCompileWork(stop.Token, 100, () => { if (++visits == 2) stop.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => AnimationScript.Compile(keys, 30, "sparse.zan", stop.Token, work));
        Assert.Equal(2, visits);
        Assert.Single(AnimationScript.Compile(keys, 30, "sparse.zan", Token, new(Token, 6)));
    }

    [Fact]
    public void PreCanceledOperationReadsNoSource()
    {
        var files = Fixture(Event());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => Compile(files, 31, stop.Token));
        Assert.Empty(files.Reads);
    }

    private static AnimationCompiler.Result Compile(Files files, long maximum, CancellationToken token,
        Action? admitted = null, string[]? names = null) => AnimationCompiler.Compile(files, Root,
            names ?? ["gate", "door"], null, FormatRegistry.MaximumDocumentBytes, token,
            maximumKeyframeWork: maximum, keyframeWorkAdmitted: admitted);

    private static string Event(string file = "move.zan", int rate = 30, string target = "door") =>
        $"OBJECT_MOTION_SI_SCRIPT ( NAME ( {target} ) SCRIPT_FILENAME ( \"{file}\" ) SCRIPT_FRAME_RATE ( {rate} ) ) ";
    private static byte[] Definition(string events, string rootName = "gate", string cleanup = "") => Bytes(
        $"ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( \"{rootName}\" ) " +
        $"RESET_STATE ( {cleanup} ) SEQUENCE_DEFINITION ( NAME ( go ) {events} ) ) ) )");
    private static Files Fixture(string events, string rootName = "gate", string cleanup = "") => new(new()
    {
        [Root] = Definition(events, rootName, cleanup), [Folder + "move.zan"] = Si(),
    });
    private static byte[] Si() => Bytes("SI Animation Script\nFRAMES: 8\nOBJECTS: 1\n" + string.Concat(Enumerable.Range(1, 8).Select(Pose)));
    private static string Pose(int frame) => $"Frame: {frame}\nObject: door\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0\n";
    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    private sealed class Files(Dictionary<string, byte[]> data) : IProjectFiles
    {
        public Dictionary<string, byte[]> Data { get; } = data;
        public List<string> Reads { get; } = [];
        public bool Exists(string relative) => Data.ContainsKey(Canonical(relative));
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested();
            Reads.Add(Canonical(relative));
            byte[] bytes = Data[Canonical(relative)]; limits.Validate(bytes); return bytes;
        }
        private static string Canonical(string path) => path.Replace("/./", "/", StringComparison.Ordinal);
    }
}
