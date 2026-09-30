using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceProjectTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ZrdNode A(params ZrdNode[] children) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", children);
    private static ZrdNode S(string text) => new(Guid.NewGuid(), ZrdKind.String, 0, text, []);
    private static ZrdNode I(int value) => new(Guid.NewGuid(), ZrdKind.Int, unchecked((uint)value), "", []);
    private static ZrdNode F(uint bits) => new(Guid.NewGuid(), ZrdKind.Float, bits, "", []);
    private static ZrdNode F(float value) => F(BitConverter.SingleToUInt32Bits(value));

    [Fact]
    public void ZrdTextRoundTripsEveryCompiledValueExactly()
    {
        string allBytes = new(Enumerable.Range(0, 256).Select(i => (char)i).ToArray());
        var root = A(A(S("ANIMATION_DEFINITIONS"), A(S("GRAVITY"), A(F(-9.8f)), S("DANGLING_KEY"))),
            A(I(int.MinValue), I(int.MaxValue), I(0), I(-1)),
            A(F(-0f), F(1u), F(0x7FC00001u), F(float.NegativeInfinity), F(3.4028235e38f), F(0.1f), F(1e-45f), F(123f)),
            A(S(""), S("key"), S("with space"), S(allBytes), S("\"quoted\" \\ back"), S("#hash"), S("f32:00000000"), S("123"), S("-5"), S("1e5"), S(".5"), S("_x"), S("a(b)c")),
            A(), A(A(A(A()))), S("TRAILING"));
        byte[] compiled = ZrdWriter.Write(root, Token);
        string text = ZrdText.Write(root, Token);
        Assert.All(text, c => Assert.InRange(c, (char)9, (char)126));
        var parsed = ZrdText.Parse(Encoding.Latin1.GetBytes(text), Token);
        Assert.Equal(compiled, ZrdWriter.Write(parsed, Token));
        // Keys sit beside their values, and whitespace or comments never change the data.
        Assert.Contains("GRAVITY ( -9.8 )", text);
        string commented = "# header\n" + text.Replace("\n", "   # note\n", StringComparison.Ordinal).Replace("  ", "\t", StringComparison.Ordinal);
        Assert.Equal(compiled, ZrdWriter.Write(ZrdText.Parse(commented, Token), Token));
        // Deep nesting within the reader's limit.
        var deep = A(); for (int i = 0; i < 100; i++) deep = A(deep);
        Assert.Equal(ZrdWriter.Write(A(deep), Token), ZrdWriter.Write(ZrdText.Parse(ZrdText.Write(A(deep), Token), Token), Token));
    }

    [Theory]
    [InlineData("( a b", "Line 1: Missing ')'.")]
    [InlineData("a\n)", "Line 2: Unexpected ')'.")]
    [InlineData("\"open", "Line 1: Unterminated string.")]
    [InlineData("\"bad \\q\"", "Line 1: Unknown escape '\\q'.")]
    [InlineData("1.2.3", "Line 1: Invalid float '1.2.3'.")]
    [InlineData("99999999999", "Line 1: Invalid integer '99999999999'.")]
    [InlineData("a:b", "Line 1: 'a:b' must be quoted.")]
    [InlineData("f32:123", "Line 1: Raw float bits need exactly eight hex digits after f32:.")]
    [InlineData("\n\n( 1e99 )", "Line 3: Invalid float '1e99'.")]
    public void ZrdTextErrorsNameTheLine(string text, string message)
        => Assert.Equal(message, Assert.Throws<InvalidDataException>(() => ZrdText.Parse(text, Token)).Message);

    [Theory]
    [InlineData("SetModelDirectory ..\\data\\m1\\models", new[] { "SetModelDirectory", "..\\data\\m1\\models" })]
    [InlineData("  LoadGameGen   erfpgammo.flt,pu000   # a comment", new[] { "LoadGameGen", "erfpgammo.flt", "pu000" })]
    [InlineData("a,,b", new[] { "a", "", "b" })]
    [InlineData("a\tb, c", new[] { "a", "b", "c" })]
    [InlineData("a,", new[] { "a" })]
    [InlineData("a,,", new[] { "a", "" })]
    [InlineData("# only a comment", new string[0])]
    [InlineData("set X %MISSION_DIR%\\zrdr\r", new[] { "set", "X", "%MISSION_DIR%\\zrdr\r" })]
    public void ScriptTextTokenizesLikeTheEngine(string line, string[] tokens) => Assert.Equal(tokens, GameGenScriptText.TokenizeLine(line));

    [Fact]
    public void ScriptTextEncodesEmptyTokensAndRejectsUnrepresentableOnes()
    {
        foreach (string[] tokens in new[] { new[] { "a", "", "b" }, new[] { "a", "" }, new[] { "", "a" }, new[] { "" }, new[] { "a", "", "" }, new[] { "x", "y" } })
            Assert.Equal(tokens, GameGenScriptText.TokenizeLine(GameGenScriptText.WriteLine(tokens)!));
        foreach (string[] tokens in new[] { new[] { "a b" }, new[] { "a#b" }, new[] { "a,b" }, new[] { "\u0100" } })
            Assert.Null(GameGenScriptText.WriteLine(tokens));
        Assert.Equal(2, GameGenScriptText.Tokenize("a b\r\n\r\n# c\r\nd\r\n").Count);
    }

    [Fact]
    public async Task SyntheticDataReconstructsAndPacksExactlyAndReportsEdits()
    {
        using var fixture = new SourceFixture();
        var manifest = await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        Assert.Equal(new Dictionary<string, string> { ["interp.zbd"] = "scripts", ["m1/zrdr.zbd"] = "archive", ["other.bin"] = "passthrough", ["soundsh.zbd"] = "archive", ["soundsm.zbd"] = "archive" },
            manifest.Outputs.ToDictionary(o => o.Path, o => o.Family));
        Assert.Empty(manifest.Notes);
        string Source(string relative) => Path.Combine(fixture.Project, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal("(\n  GRAVITY ( -9.8 )\n)\n", File.ReadAllText(Source("data/m1/zrdr/ai.zrd")));
        Assert.True(File.Exists(Source("data/m1/zrdr/envmodels/frcgate.zrd")));
        Assert.Equal(fixture.Blob, File.ReadAllBytes(Source("data/_unplaced/zrdr/blob.bin")));
        Assert.Equal(fixture.WaveB, File.ReadAllBytes(Source("data/common/sounds/b.wav")));
        Assert.Equal("set ZBD_DIR zbd\nmkdir %ZBD_DIR%,,\n", File.ReadAllText(Source("gamegen/support/common.gw")));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(912_000_000), File.GetLastWriteTimeUtc(Source("gamegen/support/common.gw")));

        var report = await SourcePacker.VerifyAsync(fixture.Project, token: Token);
        Assert.Equal(5, report.Identical);
        string packed = Path.Combine(fixture.Root, "packed");
        report = await SourcePacker.PackAsync(fixture.Project, packed, token: Token);
        foreach (var output in manifest.Outputs)
            Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Corpus, output.Path)), File.ReadAllBytes(Path.Combine(packed, output.Path)));
        Assert.True(File.Exists(Path.Combine(packed, SourcePacker.MarkerFileName)));

        // Edited sources are compiled into their outputs and reported as changes.
        File.WriteAllText(Source("data/m1/zrdr/ai.zrd"), "# edited\n( GRAVITY ( -4.5 ) )\n");
        File.AppendAllText(Source("gamegen/m1.gs"), "Quit\n");
        report = await SourcePacker.PackAsync(fixture.Project, packed, token: Token);
        var archive = report.Outputs.Single(o => o.Path == "m1/zrdr.zbd");
        Assert.Equal("changed", archive.Status); Assert.Equal(["data/m1/zrdr/ai.zrd"], archive.ChangedSources);
        var reopened = FormatRegistry.Default.OpenBytes("zrdr.zbd", File.ReadAllBytes(Path.Combine(packed, "m1/zrdr.zbd")), token: Token);
        Assert.Equal(BitConverter.SingleToUInt32Bits(-4.5f), ((ZrdNode)reopened.Assets.Single(a => a.Name == "ai.zrd").Content!).Children[0].Children[1].Children[0].Bits);
        var scripts = FormatRegistry.Default.OpenBytes("interp.zbd", File.ReadAllBytes(Path.Combine(packed, "interp.zbd")), token: Token);
        Assert.Equal(["Quit"], scripts.Scripts!.Entries.Single(e => e.Name == "m1.gs").Instructions[^1].Tokens);
        Assert.Equal("changed", report.Outputs.Single(o => o.Path == "interp.zbd").Status);
        Assert.Equal("identical", report.Outputs.Single(o => o.Path == "soundsm.zbd").Status);

        // A derived variant cannot be regenerated yet: nothing is written, and the error names the source.
        File.WriteAllBytes(Source("data/common/sounds/b.wav"), [.. fixture.WaveB, 0]);
        byte[] before = File.ReadAllBytes(Path.Combine(packed, "m1/zrdr.zbd"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourcePacker.PackAsync(fixture.Project, packed, token: Token));
        Assert.Contains("soundsm.zbd", error.Message); Assert.Contains("data/common/sounds/b.wav", error.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(packed, "m1/zrdr.zbd")));
        Assert.Empty(Directory.GetDirectories(packed, ".zstudio-staging-*"));

        // Syntax errors fail the owning output with the source line.
        File.WriteAllText(Source("data/m1/zrdr/ai.zrd"), "( GRAVITY");
        var failed = (await SourcePacker.VerifyAsync(fixture.Project, token: Token)).Outputs.Single(o => o.Path == "m1/zrdr.zbd");
        Assert.Equal("failed", failed.Status); Assert.Contains("data/m1/zrdr/ai.zrd: Line 1: Missing ')'.", failed.Error);
    }

    [Fact]
    public async Task SourceTextOpensEditsAndSavesAsTextAndCompilesOnArchiveImport()
    {
        using var fixture = new SourceFixture();
        string zrd = Path.Combine(fixture.Root, "gate.zrd"), script = Path.Combine(fixture.Root, "loadm1.gw"), broken = Path.Combine(fixture.Root, "broken.zrd");
        File.WriteAllText(zrd, "# gate\n( MODEL ( frcgate ) SPEED ( 2.5 ) )\n"); File.WriteAllText(script, "# load\nset dbName m1.flt\nLoadGameGen %dbName%\n"); File.WriteAllText(broken, "(\n  MODEL (");
        var doc = await FormatRegistry.Default.OpenAsync(zrd, Token);
        Assert.Equal("zrd-text", doc.SourceSyntax); Assert.Empty(doc.Diagnostics);
        var tree = Assert.IsType<ZrdNode>(Assert.Single(doc.Assets).Content);
        Assert.Equal("frcgate", tree.Children[0].Children[1].Children[0].Text);
        var scripts = await FormatRegistry.Default.OpenAsync(script, Token);
        Assert.Equal("gamegen-script", scripts.SourceSyntax);
        var content = Assert.IsType<ScriptContent>(Assert.Single(scripts.Assets).Content);
        Assert.Equal([["set", "dbName", "m1.flt"], ["LoadGameGen", "%dbName%"]], content.Instructions);
        Assert.StartsWith("# load", content.Text);
        var invalid = await FormatRegistry.Default.OpenAsync(broken, Token);
        Assert.Contains(invalid.Diagnostics, d => d.Severity == "Error" && d.Message.Contains("Line 2: Missing ')'.", StringComparison.Ordinal));

        // The shared ZRD editor edits source text and saves text, never compiled bytes.
        var edits = new ResourceEditSession(doc); var member = Assert.Single(edits.Current.Members);
        var speed = edits.Tree(member, Token).Children[0].Children[3].Children[0];
        edits.Accept(await edits.PrepareZrdAsync(member.Id, speed.Id, "set", value: "7.25", token: Token));
        string saved = Path.Combine(fixture.Root, "gate-edited.zrd");
        await edits.SaveAsync(saved, Token);
        string text = File.ReadAllText(saved);
        Assert.Contains("SPEED ( 7.25 )", text); Assert.True(ZrdText.LooksLikeText(File.ReadAllBytes(saved)));
        Assert.Equal(BitConverter.SingleToUInt32Bits(7.25f), ((ZrdNode)(await FormatRegistry.Default.OpenAsync(saved, Token)).Assets[0].Content!).Children[0].Children[3].Children[0].Bits);

        // Importing a source .zrd into an archive compiles it.
        var archive = new ResourceEditSession(await FormatRegistry.Default.OpenAsync(Path.Combine(fixture.Corpus, "m1", "zrdr.zbd"), Token));
        archive.Accept(await archive.PrepareArchiveAsync("add", Guid.Empty, "gate.zrd", zrd, token: Token));
        var added = archive.Current.Members[^1];
        Assert.Equal(ZrdWriter.Write(tree, Token), added.Data.ToArray());
    }

    [Fact]
    public async Task ProjectsAndPacksStaySeparateFromTheirInputs()
    {
        using var fixture = new SourceFixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, Path.Combine(fixture.Corpus, "project"), token: Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, Path.Combine(fixture.Root, "zbd_1999", "project"), token: Token));
        string mw3 = Path.Combine(fixture.Root, "mw3"); Directory.CreateDirectory(mw3);
        byte[] world = new byte[36]; BinaryPrimitives.WriteUInt32LittleEndian(world, 0x02971222); BinaryPrimitives.WriteUInt32LittleEndian(world.AsSpan(4), 27);
        File.WriteAllBytes(Path.Combine(mw3, "gamez.zbd"), world);
        var unsupported = await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(mw3, Path.Combine(fixture.Root, "mw3-project"), token: Token));
        Assert.Contains("MechWarrior 3", unsupported.Message); Assert.False(Directory.Exists(Path.Combine(fixture.Root, "mw3-project")));
        Directory.CreateDirectory(fixture.Project); File.WriteAllText(Path.Combine(fixture.Project, "keep.txt"), "user file");
        await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token));
        File.Delete(Path.Combine(fixture.Project, "keep.txt"));
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => SourcePacker.PackAsync(fixture.Project, Path.Combine(fixture.Project, "out"), token: Token));
        string foreign = Path.Combine(fixture.Root, "foreign"); Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "x.txt"), "x");
        await Assert.ThrowsAsync<IOException>(() => SourcePacker.PackAsync(fixture.Project, foreign, token: Token));
        Assert.Equal(["x.txt"], Directory.GetFileSystemEntries(foreign).Select(Path.GetFileName));
        // Re-packing into a previous pack folder is allowed; files it did not produce are left alone.
        string packed = Path.Combine(fixture.Root, "packed");
        await SourcePacker.PackAsync(fixture.Project, packed, token: Token);
        File.WriteAllBytes(Path.Combine(packed, "leftover.zbd"), [1]);
        await SourcePacker.PackAsync(fixture.Project, packed, token: Token);
        Assert.True(File.Exists(Path.Combine(packed, "leftover.zbd"))); // not a previous output: untouched

        // Hand-edited manifests and layouts cannot escape the project, the pack folder or the cache.
        string manifestPath = SourceProject.ManifestPath(fixture.Project), original = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, original.Replace("\"path\": \"other.bin\"", "\"path\": \"../escape.bin\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => SourcePacker.PackAsync(fixture.Project, Path.Combine(fixture.Root, "escape"), token: Token));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.bin")));
        File.WriteAllText(manifestPath, original);
        string layout = Path.Combine(fixture.Project, ".zstudio", "layouts", "soundsm.zbd.json");
        File.WriteAllText(layout, File.ReadAllText(layout).Replace("\"cache\": \"", "\"cache\": \"x", StringComparison.Ordinal));
        var damaged = (await SourcePacker.VerifyAsync(fixture.Project, token: Token)).Outputs.Single(o => o.Path == "soundsm.zbd");
        Assert.Equal("failed", damaged.Status); Assert.Contains("not a SHA-256 cache identity", damaged.Error);
    }
}
