using System.Collections;
using System.Text;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceParserCancellationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData(" # only a comment")]
    [InlineData("\r\n\t ")]
    [InlineData("name")]
    [InlineData("\"quoted\\xE9\"")]
    public void ResourceEntryAndDecodeObserveTheActualCancelledToken(string text)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() => ZrdText.Parse(text, cancellation.Token)).CancellationToken);
        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() => ZrdText.Parse(bytes, cancellation.Token)).CancellationToken);
        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() => ZrdTextSyntax.Parse(bytes, cancellation.Token)).CancellationToken);
        Assert.NotNull(ZrdText.Parse(bytes, Token));
    }

    [Theory]
    [InlineData("comment")]
    [InlineData("space")]
    [InlineData("word")]
    [InlineData("quoted")]
    [InlineData("escape")]
    public void ResourceSingleLexemeChecksDuringScanningWithoutAClock(string kind)
    {
        string text = kind switch
        {
            "comment" => "#" + new string('x', 2048),
            "space" => new string(' ', 2048),
            "word" => new string('x', 2048),
            "quoted" => "\"" + new string('x', 2048) + "\"",
            _ => "\"" + string.Concat(Enumerable.Repeat("\\x41", 2048)) + "\""
        };
        using var cancellation = new CancellationTokenSource();
        int checkpoints = 0, published = 0;
        var exception = Assert.Throws<OperationCanceledException>(() => ZrdText.Parse(text, cancellation.Token,
            (_, _) => published++, () => { checkpoints++; cancellation.Cancel(); }));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, checkpoints);
        Assert.Equal(0, published);
        Assert.NotNull(ZrdText.Parse(text, Token));
    }

    [Fact]
    public void CancellationFromFinalRootObservationCannotReturnSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() =>
            ZrdText.Parse("# empty", cancellation.Token, (_, _) => cancellation.Cancel())).CancellationToken);
        Assert.Empty(ZrdText.Parse("# empty", Token).Children);
    }

    [Fact]
    public void ResourceLexingKeepsExactValuesSpansAndLineErrors()
    {
        const string text = "# keep\r\nNAME ( -2 -0.0 f32:7FC01234 \"x\\xE9\\\"\\\\\" )\r\n";
        var syntax = ZrdTextSyntax.Parse(text, Token);
        Assert.Equal("NAME", syntax.Root.Children[0].Text);
        var values = syntax.Root.Children[1].Children;
        Assert.Equal(unchecked((uint)-2), values[0].Bits);
        Assert.Equal(0x80000000u, values[1].Bits);
        Assert.Equal(0x7fc01234u, values[2].Bits);
        Assert.Equal("xé\"\\", values[3].Text);
        Assert.Equal(new TextSpan(8, 4), syntax.SpanOf(syntax.Root.Children[0].Id));
        Assert.Equal(text, syntax.Text);
        Assert.Contains("Line 2", Assert.Throws<InvalidDataException>(() => ZrdText.Parse("# first\n\"bad\n", Token)).Message);
    }

    [Fact]
    public void ScriptSingleTokenScanStopsBeforeAllocatingTokenOrSpan()
    {
        string text = new('x', SourceTextScan.Chunk + 1);
        List<string> tokens = []; List<TextSpan> spans = [];
        using var cancellation = new CancellationTokenSource();
        int checkpoints = 0;
        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() =>
            GameGenScriptText.TokenizeLine(text, 0, text.Length, tokens, spans, cancellation.Token,
                () => { if (++checkpoints == 2) cancellation.Cancel(); })).CancellationToken);
        Assert.Equal(2, checkpoints); Assert.Empty(tokens); Assert.Empty(spans);
        Assert.Equal(text, Assert.Single(GameGenScriptText.TokenizeLine(text, Token)));
    }

    [Fact]
    public void ScriptDialectAndPhysicalSpansStayExact()
    {
        const string text = "# keep\r\n  echo,a,,b # ignored\r\nlast\r";
        var syntax = GameGenScriptSyntax.Parse(text, Token);
        Assert.Equal("\r\n", syntax.Newline);
        Assert.Equal(3, syntax.Lines.Count);
        Assert.Empty(syntax.Line(1).Tokens);
        Assert.Equal(new[] { "echo", "a", "", "b" }, syntax.Line(2).Tokens);
        Assert.Equal(new[] { new TextSpan(10, 4), new TextSpan(15, 1), new TextSpan(17, 0), new TextSpan(18, 1) }, syntax.Line(2).Spans);
        Assert.Equal("last\r", Assert.Single(syntax.Line(3).Tokens));
        Assert.Equal(text, Encoding.Latin1.GetString(syntax.Encode()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# comment\r\n\n")]
    [InlineData("echo,x,,y\n")]
    public void ScriptEntryAndEditNoOpsObserveCancellation(string text)
    {
        var syntax = GameGenScriptSyntax.Parse("echo x\n", Token);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => GameGenScriptSyntax.Parse(Encoding.Latin1.GetBytes(text), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => GameGenScriptText.TokenizeCancellable(text, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => syntax.ReplaceTokens(1, new Dictionary<int, string>(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => syntax.InsertLines(1, [], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => syntax.CommentOut(new HashSet<int>(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => SourceWorlds.InsertIntoScript(Encoding.Latin1.GetBytes(text), [], "script", cancellation.Token));
    }

    [Fact]
    public void EditCancellationDuringEnumerationPreservesOriginalAndAllowsRetry()
    {
        var syntax = GameGenScriptSyntax.Parse("echo x\r\n", Token);
        using var cancellation = new CancellationTokenSource();
        var instructions = new CancelledInstructions(cancellation);
        Assert.Throws<OperationCanceledException>(() => syntax.InsertLines(1, instructions, cancellation.Token));
        Assert.Equal("echo x\r\n", syntax.Text);
        Assert.Equal("echo y\r\necho x\r\n", syntax.InsertLines(1, [["echo", "y"]], Token));
    }

    [Fact]
    public void WorldAssemblyReadCancellationStopsBeforeAnyWorldMutationAndRetryWorks()
    {
        byte[] source = Encoding.ASCII.GetBytes(new string('\n', 512) + "NewWorld world\nGameZWriteZBDFile ../m1/gamez.zbd\n");
        using var cancellation = new CancellationTokenSource();
        var files = new CancelOnRead(source, cancellation);
        // Warm only the same small refusal path so the allocation oracle excludes first-use initialization.
        Assert.Throws<OperationCanceledException>(() => new WorldAssembler(files, cancellation.Token).Assemble("m1.gs"));
        using var measuredCancellation = new CancellationTokenSource();
        var assembler = new WorldAssembler(new CancelOnRead(source, measuredCancellation), measuredCancellation.Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var exception = Assert.Throws<OperationCanceledException>(() => assembler.Assemble("m1.gs"));
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(measuredCancellation.Token, exception.CancellationToken);
        Assert.InRange(allocated, 0, 16 * 1024); // No512 physical-line records may be built after read cancellation.
        Assert.Empty(assembler.World.Nodes);
        Assert.Equal(source, files.Content);
        Assert.Equal("world", Assert.Single(new WorldAssembler(new CancelOnRead(source, null), Token).Assemble("m1.gs").Nodes).Name);
    }

    [Fact]
    public void LookupReadCancellationPreservesFullIdentityOnFreshRetry()
    {
        byte[] source = Encoding.ASCII.GetBytes("FindNode exact.name\n");
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => WorldLookups.FindNodes(_ => { cancellation.Cancel(); return source; }, "m1", cancellation.Token));
        Assert.Equal("exact.name", Assert.Single(WorldLookups.FindNodes(_ => source, "m1", Token)).Name);
    }

    [Theory]
    [InlineData("resource.zrd")]
    [InlineData("script.gs")]
    public void GenericSourceOpeningDoesNotConvertCancelledParsingIntoSuccess(string path)
    {
        byte[] bytes = "# only a comment"u8.ToArray();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var registry = new FormatRegistry();
        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() => registry.OpenBytes(path, bytes, token: cancellation.Token)).CancellationToken);
        Assert.Single(registry.OpenBytes(path, bytes, token: Token).Assets);
    }

    private sealed class CancelOnRead(byte[] source, CancellationTokenSource? cancellation) : IProjectFiles
    {
        internal byte[] Content => source;
        public bool Exists(string relative) => relative == "gamegen/m1.gs";
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested(); limits.Validate(source); cancellation?.Cancel(); return source;
        }
    }

    private sealed class CancelledInstructions(CancellationTokenSource cancellation) : IReadOnlyList<IReadOnlyList<string>>
    {
        public int Count => 1;
        public IReadOnlyList<string> this[int index] => ["echo", "y"];
        public IEnumerator<IReadOnlyList<string>> GetEnumerator() { cancellation.Cancel(); yield return this[0]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
