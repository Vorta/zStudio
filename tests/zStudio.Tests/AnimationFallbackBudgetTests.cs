using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationFallbackBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ReconstructingTwoTracksFromOneLooseScriptKeepsBothAndCompilesBackExactly()
    {
        const string root = "data/m1/zrdr/anim.zad", path = "data/m1/zrdr/move.zan";
        const string definition = "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( gate ) " +
            "SEQUENCE_DEFINITION ( NAME ( go ) OBJECT_MOTION_SI_SCRIPT ( NAME ( door ) SCRIPT_FILENAME ( move.zan ) SCRIPT_FRAME_RATE ( 1 ) ) " +
            "OBJECT_MOTION_SI_SCRIPT ( NAME ( lamp ) SCRIPT_FILENAME ( move.zan ) SCRIPT_FRAME_RATE ( 1 ) ) ) ) ) ) )";
        var files = new Files(new() { [root] = Encoding.Latin1.GetBytes(definition), [path] = Encoding.Latin1.GetBytes(Track(33_001)) });
        string[] nodes = ["gate", "door", "lamp"];
        var original = AnimationCompiler.Compile(files, root, nodes, Token);
        files.Data.Remove(path);
        List<string> notes = [];
        var output = Assert.Single(AnimationSources.Reconstruct([new(1, original.Package, [], nodes)], files, notes, Token));
        Assert.Equal(path, output.Path);
        Assert.Contains(notes, note => note.EndsWith("written in zStudio's keyframe format.", StringComparison.Ordinal));
        Assert.DoesNotContain(notes, note => note.Contains("do not compile", StringComparison.Ordinal));
        var parsed = AnimationScript.Parse(output.Bytes, path, Token);
        Assert.Equal(["door", "lamp"], parsed.Select(track => track.Object));
        Assert.All(parsed, track => Assert.Equal(33_001, track.Keys.Count));
        files.Data[path] = output.Bytes;
        var rebuilt = AnimationCompiler.Compile(files, root, nodes, Token);
        Assert.Null(AnimationComparer.Difference(original.Package.Entries[1], rebuilt.Package.Entries[1], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SingleTrackAndAggregateLimitsAreCheckedBeforeRetainingExtraKeys()
    {
        string oversized = Track(AnimationScript.MaximumKeys + 1);
        Assert.Contains("one track", Assert.Throws<InvalidDataException>(() => AnimationScript.Parse(Encoding.Latin1.GetBytes(oversized), "large.zan", Token)).Message);
        Assert.Contains("track exceeds", Assert.Throws<InvalidDataException>(() => AnimationScript.Write([("door", oversized)], Token)).Message);
        string maximum = Track(AnimationScript.MaximumKeys);
        var tracks = Enumerable.Range(0, 5).Select(i => ($"node{i}", maximum)).ToArray();
        Assert.Contains("across its tracks", Assert.Throws<InvalidDataException>(() => AnimationScript.Write(tracks, Token)).Message);
        byte[] bytes = Encoding.Latin1.GetBytes(string.Concat(tracks.Select(track => $"OBJECT {track.Item1}\n{track.Item2}")));
        Assert.Contains("across the script's tracks", Assert.Throws<InvalidDataException>(() => AnimationScript.Parse(bytes, "many.zan", Token)).Message);
    }

    [Fact]
    public void WriterRefusesOversizedTextBeforeAppendingAndParserHonorsSharedReservationAndCancellation()
    {
        string huge = new(' ', SourceProject.MaximumSourceTextBytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("source limit", Assert.Throws<InvalidDataException>(() => AnimationScript.Write([("door", huge)], Token)).Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 100_000);
        byte[] keys = Encoding.Latin1.GetBytes(Track(100));
        int reserved = 0;
        Assert.Throws<InvalidOperationException>(() => AnimationScript.Parse(keys, "shared.zan", Token, () =>
        {
            if (reserved == 3) throw new InvalidOperationException("shared budget");
            reserved++;
        }));
        Assert.Equal(3, reserved);
        Assert.Throws<OperationCanceledException>(() => AnimationScript.Parse(keys, "cancel.zan", new CancellationToken(true)));
    }

    private static string Track(int count)
    {
        StringBuilder text = new();
        for (int i = 0; i < count - 1; i++) text.Append("FRAME ").Append(i).Append(" POSITION ").Append(i).Append(" 0 0\n");
        return text.Append("FRAME ").Append(count - 1).Append('\n').ToString();
    }

    private sealed class Files(Dictionary<string, byte[]> data) : IProjectFiles
    {
        public Dictionary<string, byte[]> Data { get; } = data;
        public bool Exists(string path) => Data.ContainsKey(path);
        public byte[] Read(string path, CancellationToken token) => Read(path, token, ProjectReadLimits.Document);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] bytes = Data[path]; limits.Validate(bytes); return bytes; }
    }
}
