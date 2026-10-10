using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationScriptLexicalBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Fallback = "FRAME 0 POSITION 0 0 0\nFRAME 1\n";
    private const string Si = "SI Animation Script\nFrame: 1\nObject: door\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0\nFrame: 2\nObject: door\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0\n";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CommentHeavyAndSingleWideCommentSourcesDoNotMaterializeDiscardedLines(bool si, bool oneLine)
    {
        Assert.Equal(2, Parse(Bytes(si ? Si : Fallback), si));
        string comments = oneLine ? "#" + new string('x', 1 << 20) + "\n" : string.Concat(Enumerable.Repeat("#\n", 1 << 19));
        byte[] bytes = Bytes(comments + (si ? Si : Fallback));
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = Parse(bytes, si);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2, count);
        Assert.InRange(allocated, 0, 128 * 1024); // The1MiB many-line controls previously allocated about40/19MiB.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualAnimationCompilationRetainsTheSameFramesWithCommentHeavySources(bool si)
    {
        const string root = "data/m1/zrdr/anim.zad", script = "data/m1/zrdr/move.zan";
        var files = new Files(new()
        {
            [root] = Bytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( gate ) SEQUENCE_DEFINITION ( NAME ( go ) OBJECT_MOTION_SI_SCRIPT ( NAME ( door ) SCRIPT_FILENAME ( move.zan ) SCRIPT_FRAME_RATE ( 1 ) ) ) ) ) )"),
            [script] = Bytes(si ? Si : Fallback)
        });
        var expected = AnimationCompiler.Compile(files, root, ["gate", "door"], Token);
        files.Data[script] = Bytes(string.Concat(Enumerable.Repeat("#\n", 1 << 19)) + (si ? Si : Fallback));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var actual = AnimationCompiler.Compile(files, root, ["gate", "door"], Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Null(actual.EngineRejection);
        Assert.Equal(expected.Bytes, actual.Bytes);
        Assert.InRange(allocated, 0, 1024 * 1024);
    }

    [Fact]
    public void WideFallbackFrameStreamsRepeatedChannelsWithoutTokenArrays()
    {
        _ = AnimationScript.Parse(Bytes(Fallback), "warm.zan", Token);
        byte[] source = Bytes("FRAME 0 " + string.Concat(Enumerable.Repeat("POSITION 1 2 3 ", 100_000)) + "POSITION 4 5 6\nFRAME 1");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var keys = Assert.Single(AnimationScript.Parse(source, "wide.zan", Token)).Keys;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(new Vector3(4, 5, 6), keys[0].Position);
        Assert.Equal(2, keys.Count);
        Assert.InRange(allocated, 0, 128 * 1024);
    }

    [Theory]
    [InlineData("fallback-token")]
    [InlineData("si-colon")]
    [InlineData("si-token")]
    [InlineData("si-vector")]
    public void WideMalformedTokensAndVectorLinesRefuseBeforeExpandingDiagnostics(string kind)
    {
        string huge = new('0', 1 << 20);
        string source = kind switch
        {
            "fallback-token" => "FRAME " + huge,
            "si-colon" => huge + ": 1",
            "si-token" => "Frame: " + huge,
            _ => "Frame: 1\nObject: door\nScaling: " + string.Concat(Enumerable.Repeat("0 ", 1 << 19))
        };
        byte[] bytes = Bytes(source);
        string path = new('p', 1 << 20);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => Parse(bytes, kind != "fallback-token", path));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(error.Message.Length, 1, 256);
        Assert.Contains(kind == "si-vector" ? "three finite numbers" : "token exceeds", error.Message);
        Assert.InRange(allocated, 0, 128 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationInterruptsTheFirstWideLineScanDeterministically(bool si)
    {
        byte[] bytes = Bytes("#" + new string('x', 1 << 20) + "\n" + (si ? Si : Fallback));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int scanned = 0;
        void Progress(int offset) { scanned = offset; if (offset >= 8192) cancel.Cancel(); }
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            if (si) SiAnimationScript.Parse(bytes, "cancel.zan", cancel.Token, Progress);
            else AnimationScript.Parse(bytes, "cancel.zan", cancel.Token, null, Progress);
        });
        Assert.Equal(8192, scanned);
        Assert.Equal(2, Parse(Bytes(si ? Si : Fallback), si));
    }

    [Fact]
    public void RecognitionSkipsCommentsWithoutDecodingAndSupportsCancellation()
    {
        _ = SiAnimationScript.Recognize(Bytes(Si), Token);
        byte[] bytes = Bytes(string.Concat(Enumerable.Repeat("#\n", 1 << 19)) + Si);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(SiAnimationScript.Recognize(bytes, Token));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 16 * 1024);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        byte[] wide = Bytes("#" + new string('x', 1 << 20));
        int scanned = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => SiAnimationScript.Recognize(wide, cancel.Token, offset =>
        { scanned = offset; if (offset >= 4096) cancel.Cancel(); }));
        Assert.Equal(4096, scanned);
        Assert.False(SiAnimationScript.Recognize(Bytes("# comment\nOBJECT door"), Token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LineEndingsFinalLinesAndDialectWhitespaceRetainTheirMeaning(bool si, bool crlf)
    {
        string text = si ? Si.Replace("door", "caf\u00E9", StringComparison.Ordinal) : "oBjEcT caf\u00E9\nFrAmE 0\tPoSiTiOn 0 0 0 # inline\nFRAME 1";
        text = text.TrimEnd('\n');
        if (crlf) text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Equal(2, Parse(Bytes(text), si));
        Assert.Equal(2, Parse(Bytes(text + (crlf ? "\r\n" : "\n")), si));
        if (si)
        {
            byte[] marked = [0xEF, 0xBB, 0xBF, .. Bytes("\u00A0\r\n# comment\n" + text)];
            Assert.True(SiAnimationScript.Recognize(marked, Token));
            Assert.Equal("caf\u00E9", Assert.Single(SiAnimationScript.Parse(marked, "si.zan", Token).Objects));
        }
        else
        {
            Assert.Equal("caf\u00E9", Assert.Single(AnimationScript.Parse(Bytes(text), "fallback.zan", Token)).Object);
            Assert.Throws<InvalidDataException>(() => AnimationScript.Parse([0xEF, 0xBB, 0xBF, .. Bytes(text)], "bom.zan", Token));
            // NBSP is not one of the fallback dialect's separators; it must not silently become whitespace.
            Assert.Throws<InvalidDataException>(() => AnimationScript.Parse(Bytes("FRAME\u00A00 POSITION 0 0 0\nFRAME 1"), "space.zan", Token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BareCarriageReturnDoesNotBecomeALineTerminator(bool si)
    {
        byte[] bytes = Bytes(si ? "SI Animation Script\rFrame: 1" : "FRAME 0 POSITION 0 0 0\rFRAME 1");
        Assert.Contains("line 1", Assert.Throws<InvalidDataException>(() => Parse(bytes, si)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjectIdentityAtTheTokenLimitIsPreservedAndWritersRefuseLargerNames(bool si)
    {
        string name = new('n', AnimationScriptLexing.MaximumTokenBytes);
        string text = si ? Si.Replace("door", name, StringComparison.Ordinal) : "OBJECT " + name + "\n" + Fallback;
        if (si) Assert.Equal(name, Assert.Single(SiAnimationScript.Parse(Bytes(text), "name.zan", Token).Objects));
        else Assert.Equal(name, Assert.Single(AnimationScript.Parse(Bytes(text), "name.zan", Token)).Object);
        Assert.Throws<InvalidDataException>(() => Parse(Bytes(text.Replace(name, name + "x", StringComparison.Ordinal)), si));
        Assert.True(AnimationScript.IsObjectName(name));
        Assert.False(AnimationScript.IsObjectName(name + "x"));
        Assert.Throws<InvalidDataException>(() => AnimationScript.Write([(name + "x", Fallback)], Token));
        Assert.Throws<InvalidDataException>(() => SiScriptWriter.Text([(name + "x", 0, Array.Empty<long[]?>())], [], new(null), Token));
    }

    [Theory]
    [InlineData(AnimationFieldKind.Vector)]
    [InlineData(AnimationFieldKind.Float)]
    [InlineData(AnimationFieldKind.Integer)]
    public void OversizedNumericEditorInputsRefuseBeforeParsingWithoutChangingTheRecord(AnimationFieldKind kind)
    {
        AnimationField field = new("value", 0, kind);
        AnimationRecord record = new(Enumerable.Repeat((byte)0x5A, 12).ToArray());
        byte[] original = record.Bytes.ToArray();
        string text = kind == AnimationFieldKind.Vector ? string.Concat(Enumerable.Repeat("1,", 1 << 19)) : new string('0', 1 << 20);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => field.Write(record, text));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        Assert.Equal(original, record.Bytes);
    }

    [Fact]
    public void VectorEditorSeparatorsAndRejectedValuesPreserveRecordSemantics()
    {
        AnimationField field = new("value", 0, AnimationFieldKind.Vector);
        AnimationRecord record = new(new byte[12]);
        field.Write(record, " , 1, ,\t2 , 3, ");
        Assert.Equal(new Vector3(1, 2, 3), record.Vector(0));
        byte[] original = record.Bytes.ToArray();
        foreach (string bad in new[] { "4,5,6,7", "4, NaN, 6", "4,5,Infinity" })
        {
            Assert.Throws<InvalidDataException>(() => field.Write(record, bad));
            Assert.Equal(original, record.Bytes);
        }
        Assert.Throws<FormatException>(() => field.Write(record, "4\t5\t6"));
        Assert.Equal(original, record.Bytes);
        AnimationField textField = new("name", 0, AnimationFieldKind.Text, 6000);
        AnimationRecord textRecord = new(new byte[6000]);
        string name = new('n', 5000);
        textField.Write(textRecord, name);
        Assert.Equal(name, textRecord.Text(0, 6000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceLimitIsCheckedBeforeAnyLexicalMaterialization(bool si)
    {
        byte[] bytes = new byte[SourceProject.MaximumSourceTextBytes + 1];
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("source limit", Assert.Throws<InvalidDataException>(() => Parse(bytes, si)).Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumericTokensAtTheLimitRetainInvariantParsing(bool si)
    {
        string zeros = new('0', AnimationScriptLexing.MaximumTokenBytes - 1);
        string text = si ? Si.Replace("Frame: 1", "Frame: " + zeros + "1", StringComparison.Ordinal)
            : Fallback.Replace("FRAME 0", "FRAME " + zeros + "0", StringComparison.Ordinal);
        Assert.Equal(2, Parse(Bytes(text), si));
    }

    private static int Parse(byte[] bytes, bool si, string path = "test.zan") => si
        ? SiAnimationScript.Parse(bytes, path, Token).Frames.Count
        : Assert.Single(AnimationScript.Parse(bytes, path, Token)).Keys.Count;
    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);
    private sealed class Files(Dictionary<string, byte[]> data) : IProjectFiles
    {
        internal Dictionary<string, byte[]> Data { get; } = data;
        public bool Exists(string path) => Data.ContainsKey(path);
        public byte[] Read(string path, CancellationToken token) => Read(path, token, ProjectReadLimits.Document);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] bytes = Data[path]; limits.Validate(bytes); return bytes; }
    }
}
