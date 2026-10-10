using System.Text;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class DefinitionDiscoveryBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllMissionsShareTheInputAllowanceBeforeReadingOrDecoding(bool pending)
    {
        using var fixture = new Fixture();
        byte[] small = Definition("first"), large = Definition(new string('x', 2_000_000));
        fixture.Write("data/m2/zrdr/anim.zad", small);
        fixture.Write("data/m3/zrdr/anim.zad", large);
        IReadOnlyDictionary<string, byte[]>? overlay = pending
            ? new Dictionary<string, byte[]> { ["data/m3/zrdr/anim.zad"] = large } : null;
        // Warm the shared parser and discovery path outside the allocation measurement.
        Assert.Single(SourceWorlds.DefinitionsFor(fixture.Root, "m3", "tank", token: Token));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Record.Exception(() => SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", overlay, small.Length + 128, 1 << 20, Token));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsType<IOException>(error);
        Assert.Contains("aggregate source allowance", error.Message);
        Assert.True(allocated < 1_000_000, $"Oversized later input was materialized: {allocated:N0} bytes.");
        // The refusal neither edits sources nor poisons a subsequent complete discovery.
        var result = SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", overlay, token: Token);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Animations.Single().Length == 2_000_000);
        Assert.Equal(large, File.ReadAllBytes(fixture.Path("data/m3/zrdr/anim.zad")));
    }

    [Fact]
    public void ExistingPrefixesAreChargedBeforeEachFilesystemProbe()
    {
        using var fixture = new Fixture();
        string folder = "data/" + string.Join('/', Enumerable.Repeat("d", 32));
        Directory.CreateDirectory(fixture.Path(folder));
        fixture.Write("data/m2/zrdr/anim.zad", Encoding.ASCII.GetBytes($"ANIMATION_DEFINITIONS ( ANIMATION_PATH ( \"../{folder}\" ) ANIMATION_LIST ( " +
            string.Concat(Enumerable.Repeat("ANIMATION_DEFINITION_FILE ( \"absent.zad\" ) ", 20)) + ") )"));
        Assert.Empty(SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", token: Token));
        var error = Assert.Throws<IOException>(() => SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", null, 1 << 20, 1 << 20, Token, maximumProbeWork: 50_000));
        Assert.Contains("directory lookup work allowance", error.Message);
        Assert.True(Directory.Exists(fixture.Path(folder)));
    }

    [Fact]
    public void DeepMissingSearchPathDoesNotExpandEveryAbsentPrefix()
    {
        using var fixture = new Fixture();
        string folder = "../data/" + string.Join('/', Enumerable.Repeat("d", 4000));
        fixture.Write("data/m2/zrdr/anim.zad", Encoding.ASCII.GetBytes($"ANIMATION_DEFINITIONS ( ANIMATION_PATH ( \"{folder}\" ) ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( absent.zad ) ) )"));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(result);
        Assert.True(allocated < 2_000_000, $"Absent prefixes amplified a small input: {allocated:N0} bytes.");
        // Reject the complete spelling before accepting a missing prefix; it cannot hide later redirection.
        Assert.Throws<InvalidDataException>(() => SourceProject.RejectNestedLinks(fixture.Root, "data/missing/../present/file.zad"));
        Assert.Throws<InvalidDataException>(() => SourceProject.RejectNestedLinks(fixture.Root, "data/missing/./file.zad"));
        string outside = System.IO.Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        string link = fixture.Path("data/linked");
        Directory.CreateSymbolicLink(link, outside);
        try { Assert.Throws<IOException>(() => SourceProject.RejectNestedLinks(fixture.Root, "data/linked/missing/file.zad")); }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void MissingCandidatesShareProbeWorkAcrossMissionLoaders()
    {
        using var fixture = new Fixture();
        string folders = string.Join(';', Enumerable.Range(0, 8).Select(i => "../data/" + new string('d', 80) + i));
        byte[] definition = Encoding.ASCII.GetBytes($"ANIMATION_DEFINITIONS ( ANIMATION_PATH ( \"{folders}\" ) ANIMATION_LIST ( " +
            string.Concat(Enumerable.Repeat("ANIMATION_DEFINITION_FILE ( \"absent.zad\" ) ", 20)) + ") )");
        fixture.Write("data/m2/zrdr/anim.zad", definition);
        Assert.Empty(SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", token: Token));
        // Enough for either mission independently, but not the two together; source bytes are far below their cap.
        const long probes = 600_000;
        Assert.Empty(SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", null, 1 << 20, 1 << 20, Token, probes));
        fixture.Write("data/m3/zrdr/anim.zad", definition);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Record.Exception(() => SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", null, 1 << 20, 1 << 20, Token, probes));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsType<IOException>(error);
        Assert.Contains("directory lookup work allowance", error.Message);
        Assert.True(allocated < 5_000_000, $"Missing candidates escaped the shared bound: {allocated:N0} bytes.");
    }

    [Fact]
    public void PerFileLimitPrecedesAllocationEvenWithAggregateCapacityRemaining()
    {
        using var fixture = new Fixture();
        const string file = "data/m2/zrdr/anim.zad";
        fixture.Write(file, []);
        using (var stream = File.OpenWrite(fixture.Path(file))) stream.SetLength(17L << 20);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Record.Exception(() => SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", token: Token));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsType<IOException>(error);
        Assert.Contains("size limit", error.Message);
        Assert.True(allocated < 1_000_000, $"Oversized definition was read before refusal: {allocated:N0} bytes.");
    }

    [Fact]
    public void RepeatedEmptyFilesShareTheReadCountAcrossMissions()
    {
        using var fixture = new Fixture();
        fixture.Write("data/common/zrdr/empty.zad", []);
        byte[] list = Encoding.ASCII.GetBytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( " +
            string.Concat(Enumerable.Repeat("ANIMATION_DEFINITION_FILE ( \"../data/common/zrdr/empty.zad\" ) ", 6000)) + ") )");
        fixture.Write("data/m2/zrdr/anim.zad", list);
        Assert.Empty(SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", token: Token));
        fixture.Write("data/m3/zrdr/anim.zad", list);
        Assert.Contains("aggregate source allowance", Assert.Throws<IOException>(() =>
            SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", token: Token)).Message);
    }

    [Fact]
    public void ResultCapacityRefusesInsteadOfReturningTheEarlierMission()
    {
        using var fixture = new Fixture();
        fixture.Write("data/m2/zrdr/anim.zad", Definition("first"));
        string name = new('z', 4096);
        fixture.Write("data/m3/zrdr/anim.zad", Definition(name));
        var error = Assert.Throws<IOException>(() => SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", null, 1 << 20, 4096, Token));
        Assert.Contains("retained-result allowance", error.Message);
        var complete = SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", token: Token);
        Assert.Equal(2, complete.Count);
        Assert.Equal(name, complete.Single(item => item.Missions.Contains("m3")).Animations.Single());
    }

    [Fact]
    public void SharedDefinitionsRetainOneFullNameAndAllMissionIdentities()
    {
        using var fixture = new Fixture();
        const string common = "data/common/zrdr/shared.zad";
        string name = new('q', 1000);
        fixture.Write(common, Definition(name));
        byte[] list = Encoding.ASCII.GetBytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( \"../data/common/zrdr/shared.zad\" ) ) )");
        fixture.Write("data/m2/zrdr/anim.zad", list);
        fixture.Write("data/m3/zrdr/anim.zad", list);
        var result = Assert.Single(SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", null, 1 << 20, 2800, Token));
        Assert.Equal(common, result.Path);
        Assert.Equal(name, Assert.Single(result.Animations));
        Assert.Equal(["m2", "m3"], result.Missions);
        // Own mission inspection participates in the same input allowance and still excludes its files.
        fixture.Write("data/m1/zrdr/anim.zad", list);
        Assert.Empty(SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", token: Token));
        Assert.Throws<IOException>(() => SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", null, list.Length, 2800, Token));
    }

    [Fact]
    public void CancellationPropagatesWithoutReturningPartialChoices()
    {
        using var fixture = new Fixture();
        fixture.Write("data/m2/zrdr/anim.zad", Definition("first"));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.Cancel();
        var error = Assert.ThrowsAny<OperationCanceledException>(() => SourceWorlds.DefinitionsFor(fixture.Root, "m1", "tank", token: cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);
    }

    private static byte[] Definition(string name) => Encoding.ASCII.GetBytes($"ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( tank ) ANIMATION_NAME ( \"{name}\" ) ) ) )");

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zstudio-definition-discovery-" + Guid.NewGuid().ToString("N"));
        public Fixture() { Directory.CreateDirectory(System.IO.Path.Combine(Root, "data")); Directory.CreateDirectory(System.IO.Path.Combine(Root, "gamegen")); }
        public string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void Write(string relative, byte[] bytes) { string path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
