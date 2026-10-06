using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// A reconstruction reads the game files the checks before it decided on, or stops; a world's canonical content is hashed
/// without a joined copy of its output; and the names terrain conversion keeps come from text sources bounded before they
/// are decoded and from compiled resources' strings, never from their bytes read as text.
/// </summary>
// Measures allocations of large inputs; kept apart from tests that run in parallel.
[Collection("Allocation-sensitive")]
public sealed class ReconstructionTerrainRound3Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Runs on the reconstruction's own thread as each file is about to be read.</summary>
    private sealed class BeforeReading(string item, Action action) : IProgress<SourceProgress>
    {
        public bool Ran { get; private set; }
        public void Report(SourceProgress value) { if (!Ran && value.Stage == SourceStage.Items && value.Item == item) { Ran = true; action(); } }
    }

    private static bool Empty(string folder) => !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();

    // ---- Reconstruction reads the files it checked ----

    [Fact]
    public async Task AnOriginalArchiveReplacedAfterTheCheckIsRefused()
    {
        using var fixture = new SourceFixture();
        string archive = Path.Combine(fixture.Corpus, "zrdr.zbd");
        byte[] original = File.ReadAllBytes(archive);
        // An archive without the definitions (anim.zrd renamed: the same size), given the original's time.
        byte[] exported = (byte[])original.Clone();
        int name = exported.AsSpan().IndexOf("anim.zrd\0"u8);
        Assert.True(name >= 0);
        exported[name + 3] = (byte)'x';
        DateTime time = File.GetLastWriteTimeUtc(archive);
        BeforeReading replace = new("zrdr.zbd", () => { File.WriteAllBytes(archive, exported); File.SetLastWriteTimeUtc(archive, time); });

        var error = await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, replace, Token));
        Assert.True(replace.Ran);
        Assert.StartsWith("zrdr.zbd changed after the game data folder was checked", error.Message, StringComparison.Ordinal);
        // What the scripts and mission resources read before it wrote is removed.
        Assert.True(Empty(fixture.Project));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AGameFileChangedAfterTheFolderWasListedIsRefused(bool otherSize, bool otherTime)
    {
        using var fixture = new SourceFixture();
        string bank = Path.Combine(fixture.Corpus, "soundsh.zbd");
        DateTime time = File.GetLastWriteTimeUtc(bank);
        byte[] waveA = (byte[])fixture.WaveA.Clone();
        // Another recording: one sample differs, and with otherSize the second sound is gone.
        waveA[^1] ^= 0x55;
        byte[] changed = otherSize
            ? SourceFixture.Archive(("a.wav", waveA, new byte[64]))
            : SourceFixture.Archive(("a.wav", waveA, Field("a.wav")), ("b.wav", fixture.WaveB, Field("b.wav")));
        if (!otherSize) Assert.Equal(new FileInfo(bank).Length, changed.Length);
        BeforeReading replace = new("soundsh.zbd", () => { File.WriteAllBytes(bank, changed); File.SetLastWriteTimeUtc(bank, otherTime ? time.AddSeconds(2) : time); });

        var error = await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, replace, Token));
        Assert.True(replace.Ran);
        Assert.StartsWith("soundsh.zbd changed after the game data folder was checked", error.Message, StringComparison.Ordinal);
        Assert.True(Empty(fixture.Project));
        // Unchanged, the same folder is reconstructed.
        File.WriteAllBytes(bank, SourceFixture.Archive(("a.wav", fixture.WaveA, Field("a.wav")), ("b.wav", fixture.WaveB, Field("b.wav"))));
        File.SetLastWriteTimeUtc(bank, time);
        var report = await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        Assert.True(report.SourceFiles > 0);
    }

    private static byte[] Field(string text) { byte[] b = new byte[64]; Encoding.Latin1.GetBytes(text).CopyTo(b, 0); return b; }

    // ---- A world's canonical content is hashed as written ----

    [Fact]
    public void CanonicalContentIsHashedWithoutJoiningItsBuffers()
    {
        static GltfDocument Document(int vertices)
        {
            GltfPrimitive primitive = new();
            for (int i = 0; i < vertices; i++) { primitive.Positions.Add(new Vector3(i, i % 7, -i)); primitive.Indices.Add(i); }
            GltfDocument doc = new();
            doc.Roots.Add(new GltfNode { Name = "big", Mesh = new GltfMesh { Name = "big", Primitives = { primitive } } });
            return doc;
        }
        _ = WorldSources.ContentHash(Document(3)); _ = Document(3).Write("content.bin");
        var doc = Document(600_000);
        var (json, bin) = doc.Write("content.bin");
        // The hash of the JSON followed by the binary buffer, as the reconstruction has always told files apart by.
        using (MemoryStream joined = new()) { joined.Write(json); joined.Write(bin); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(joined.ToArray()))[..16], WorldSources.ContentHash(doc)); }
        long written = Allocated(() => doc.Write("content.bin"));
        long hashed = Allocated(() => WorldSources.ContentHash(doc));
        // Writing it is all it costs: no copy of the ~9 MB output.
        Assert.True(hashed <= written + 64 * 1024, $"hashing allocated {hashed:N0} bytes; writing alone {written:N0}");
    }

    // ---- Terrain conversion's names ----

    private sealed class Project : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-round3-" + Guid.NewGuid().ToString("N"));
        public Project() { Directory.CreateDirectory(Path.Combine(Root, "data", "m1", "zrdr")); Directory.CreateDirectory(Path.Combine(Root, "gamegen")); }
        public void Write(string relative, byte[] bytes) => File.WriteAllBytes(Path.Combine(Root, relative), bytes);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static ZrdNode A(params ZrdNode[] children) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", children);
    private static ZrdNode S(string text) => new(Guid.NewGuid(), ZrdKind.String, 0, text, []);

    [Fact]
    public void ACompiledResourceNamesItsStringsWithoutBeingReadAsText()
    {
        using Project project = new();
        // A large compiled resource: a few names, and 500,000 integers whose bytes spell "AAAA".
        ZrdNode[] numbers = [.. Enumerable.Range(0, 500_000).Select(_ => new ZrdNode(Guid.NewGuid(), ZrdKind.Int, 0x41414141, "", []))];
        byte[] compiled = ZrdWriter.Write(A(S("MODEL"), A(S("gate one")), S("pu***"), A(numbers)), Token);
        const string file = "data/m1/zrdr/big.zrd";
        project.Write(file, compiled);
        SourceWorkspace workspace = new(project.Root);
        _ = SourceTerrainConversion.References(workspace, [file], Token);

        long read = Allocated(() => workspace.Read(file, Token));
        long decoded = Allocated(() => ZrdDecoder.Read(compiled, Token));
        (HashSet<string> Names, IReadOnlyList<Regex> Patterns) found = ([], []);
        long allocated = Allocated(() => found = SourceTerrainConversion.References(workspace, [file], Token));

        // The strings, whole and word by word; an integer is no name.
        Assert.Superset(new HashSet<string>(["MODEL", "gate one", "gate", "one"]), found.Names);
        Assert.DoesNotContain("AAAA", found.Names);
        Assert.Contains(found.Patterns, p => p.IsMatch("pu123"));
        // Reading and decoding the file is what it costs, not its 4 MB as an 8 MB string and a token per integer.
        Assert.True(allocated <= read + decoded + (1 << 20), $"{allocated:N0} bytes; reading {read:N0}, decoding {decoded:N0}");
    }

    [Theory]
    [InlineData("data/m1/zrdr/big.zrd")]
    [InlineData("data/m1/zrdr/big.zad")]
    [InlineData("gamegen/big.gs")]
    [InlineData("data/m1/big.zan")]
    public void ATextSourceAboveTheLimitIsRefusedBeforeItIsDecoded(string file)
    {
        using Project project = new();
        // Text a little over the limit: a list of names.
        byte[] text = new byte[SourceProject.MaximumSourceTextBytes + 4096];
        "(\n"u8.CopyTo(text);
        for (int i = 2; i < text.Length - 2; i++) text[i] = i % 8 == 7 ? (byte)'\n' : (byte)('a' + i % 7);
        ")\n"u8.CopyTo(text.AsSpan(text.Length - 2));
        project.Write(file, text);
        SourceWorkspace workspace = new(project.Root);
        long read = Allocated(() => workspace.Read(file, Token));

        InvalidDataException? error = null;
        long allocated = Allocated(() => error = Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.References(workspace, [file], Token)));
        Assert.Contains(file, error!.Message, StringComparison.Ordinal);
        Assert.Contains("16 MiB", error.Message, StringComparison.Ordinal);
        // Only the file's bytes: not their 32 MB as a string.
        Assert.True(allocated <= read + (1 << 20), $"{allocated:N0} bytes; reading {read:N0}");
    }

    [Fact]
    public void WildcardPatternsAreKeptOnceAndBounded()
    {
        using Project project = new();
        const string file = "data/m1/zrdr/patterns.zrd";
        // Each piece is matched against every pattern: a repeated one counts once, and more distinct ones than planning
        // matches are refused.
        string Patterns(int count) => "(\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"  ( w{i}* w0* )\n")) + ")\n";
        project.Write(file, Encoding.ASCII.GetBytes(Patterns(SourceTerrainConversion.MaximumPatterns)));
        SourceWorkspace workspace = new(project.Root);
        var found = SourceTerrainConversion.References(workspace, [file], Token);
        Assert.Equal(SourceTerrainConversion.MaximumPatterns, found.Patterns.Count);
        Assert.Contains(found.Patterns, p => p.IsMatch("w123"));
        project.Write(file, Encoding.ASCII.GetBytes(Patterns(SourceTerrainConversion.MaximumPatterns + 1)));
        var error = Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.References(workspace, [file], Token));
        Assert.Contains("4,096 wildcard patterns", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoadScriptAboveTheLimitIsRefusedBeforeItIsDecoded()
    {
        byte[] script = new byte[SourceProject.MaximumSourceTextBytes + 4096];
        script.AsSpan().Fill((byte)'x');
        "FindNode a\n"u8.CopyTo(script);
        string load = WorldLookups.LoadScript("m1");
        InvalidDataException? error = null;
        long allocated = Allocated(() => error = Assert.Throws<InvalidDataException>(() => WorldLookups.FindNodes(path => path == load ? script : null, "m1")));
        Assert.StartsWith(load + ":", error!.Message, StringComparison.Ordinal);
        Assert.True(allocated <= 1 << 20, $"{allocated:N0} bytes");
    }
}
