using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationSearchPathTests
{
    private const string Root = "data/m1/zrdr/anim.zad";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ManyDistinctDirectoriesRetainTheirAuthoredOrderAndCompile()
    {
        string[] paths = Enumerable.Range(0, 32_000).Select(i => $"../data/dir{i:D6}").ToArray();
        var files = new Files(Wrap($"ANIMATION_PATH ( \"{string.Join(';', paths)}\" ) ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( root ) ) )"));
        var set = AnimationDefinitionSet.Load(files, Root, Token);
        Assert.Equal(paths.Select(p => p[3..]), set.SearchPath);
        var compiled = AnimationCompiler.Compile(files, Root, ["root"], Token);
        Assert.Equal(["", "root"], compiled.Package.Entries.Select(e => e.Name));
    }

    [Fact]
    public void PathsKeepFirstSpellingAndOrderAcrossDeclarationsAndCaseDuplicates()
    {
        var files = new Files(Wrap("""
            ANIMATION_PATH ( " ; ../data/First ;../data/second; ../DATA/FIRST; ../outside/x; " )
            ANIMATION_PATH ( "../data/SECOND;../data/third;" )
            """));
        var set = AnimationDefinitionSet.Load(files, Root, Token);
        Assert.Equal(["data/First", "data/second", "data/third"], set.SearchPath);
        Assert.Single(set.Warnings);
        files.Existing.UnionWith(["data/First/gate.zad", "data/second/gate.zad"]);
        Assert.Equal("data/First/gate.zad", set.Resolve("gate.zad", Root));
        files.Existing.Add("data/m1/zrdr/gate.zad");
        Assert.Equal("data/m1/zrdr/gate.zad", set.Resolve("gate.zad", Root));
        set.SearchPath.Reverse(); // The public list has historically remained editable after Load.
        files.Existing.Remove("data/m1/zrdr/gate.zad");
        Assert.Equal("data/second/gate.zad", set.Resolve("gate.zad", Root));
    }

    [Fact]
    public void DirectoryAndLookupWorkShareOneBudgetAndAFreshLoadStillSucceeds()
    {
        var files = new Files(Wrap("ANIMATION_PATH ( \"../data/one;../data/two\" )"));
        Assert.Throws<InvalidDataException>(() => Load(files, 1));
        var set = Load(files, 4096);
        for (int i = 0; i < 1000; i++) set.SearchPath.Add($"data/extra{i}");
        Assert.Contains("lookup work limit", Assert.Throws<InvalidDataException>(() => set.Resolve("missing.zad", Root)).Message);
        Assert.InRange(files.Probes, 1, 64);
        Assert.Equal(["data/one", "data/two"], AnimationDefinitionSet.Load(files, Root, Token).SearchPath);
    }

    [Fact]
    public void RepeatedMissingIncludesCannotMultiplyAllDirectoryProbesWithoutBound()
    {
        string paths = string.Join(';', Enumerable.Range(0, 20).Select(i => $"../data/d{i}"));
        var files = new Files(Wrap($"ANIMATION_PATH ( \"{paths}\" ) ANIMATION_LIST ( " +
            string.Concat(Enumerable.Repeat("ANIMATION_DEFINITION_FILE ( absent.zad ) ", 200)) + " )"));
        Assert.Throws<InvalidDataException>(() => Load(files, 16_384));
        Assert.InRange(files.Probes, 21, 200);
        Assert.Equal(1, files.Reads); // Missing includes never consumed the older successful-file-read limit.
    }

    [Fact]
    public void OversizedLookupIsRefusedBeforeCandidateOrNormalizationAllocation()
    {
        var files = new Files(Wrap(""));
        var set = Load(files, 4096);
        string huge = new('x', 1_000_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => set.Resolve(huge, Root));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 20_000);
        Assert.Equal(0, files.Probes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringAProbeStopsBeforeAnotherCandidateOrSuccessfulReturn(bool found)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var files = new Files(Wrap("ANIMATION_PATH ( \"../data/one;../data/two\" )"));
        var set = AnimationDefinitionSet.Load(files, Root, cancel.Token);
        files.OnProbe = candidate => { if (files.Probes == 2) cancel.Cancel(); return found && candidate == "data/one/gate.zad"; };
        Assert.ThrowsAny<OperationCanceledException>(() => set.Resolve("gate.zad", Root));
        Assert.Equal(2, files.Probes);
        Assert.ThrowsAny<OperationCanceledException>(() => set.Resolve("another.zad", Root));
        Assert.Equal(2, files.Probes);
    }

    private static AnimationDefinitionSet Load(Files files, long work) => AnimationDefinitionSet.Load(files, Root,
        AnimationDefinitionSet.MaximumSourceBytes, AnimationDefinitionSet.MaximumSourceNodes, Token, work);
    private static byte[] Wrap(string body) => Encoding.ASCII.GetBytes($"ANIMATION_DEFINITIONS ( {body} )");
    private sealed class Files(byte[] root) : IProjectFiles
    {
        internal HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Func<string, bool>? OnProbe { get; set; }
        internal int Probes { get; private set; }
        internal int Reads { get; private set; }
        public bool Exists(string relative) { Probes++; return OnProbe?.Invoke(relative) ?? Existing.Contains(relative); }
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { Reads++; token.ThrowIfCancellationRequested(); byte[] bytes = root; limits.Validate(bytes); return bytes; }
    }
}
