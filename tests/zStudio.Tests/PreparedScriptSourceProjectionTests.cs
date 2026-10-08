using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class PreparedScriptSourceProjectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Expected = "set dbName m1.gltf\nLoadGameGen %dbName% %dbName%\nLoadGameGen crate.gltf crate.flt\nTextureAdd ROCK.png\nFindNode crate.flt\n";

    [Fact]
    public void ReaderAdmittedZeroTokenRecordRefusesSourceProjectionWithoutChangingItsRawBytes()
    {
        byte[] bytes = Package();
        var document = FormatRegistry.Default.OpenBytes("interp.zbd", bytes, token: Token);
        Assert.Empty(document.Diagnostics);
        var package = Assert.IsType<PreparedScriptPackage>(document.Scripts);
        var malformed = Assert.Single(package.Entries[0].Instructions);
        Assert.Empty(malformed.Tokens);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(malformed.Raw.Span));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(malformed.Raw.Span[4..]));
        Assert.Equal(new byte[] { 0xA5 }, malformed.Padding.ToArray());
        var lines = Lines(package.Entries[0]);
        var macros = GameGenScriptText.ModelMacros(package.Entries.Select(Lines));
        Assert.Contains("dbName", macros);
        var error = Assert.Throws<InvalidDataException>(() => GameGenScriptText.ProjectFileNames(lines, macros));
        Assert.Contains("without a command", error.Message);
        Assert.Throws<InvalidDataException>(() => GameGenScriptText.Write(lines));
        Assert.Empty(malformed.Tokens);
        Assert.Equal(bytes, document.Bytes.ToArray());
        Assert.Equal(bytes, PreparedScriptWriter.Write(package, Token));

        // The failure is per script. Its companion still normalizes only filenames, and a retry has no stale state.
        var valid = Lines(package.Entries[1]);
        var normalized = GameGenScriptText.ProjectFileNames(valid, macros);
        Assert.Equal(Expected, GameGenScriptText.Write(normalized));
        Assert.Equal(Expected, GameGenScriptText.Write(GameGenScriptText.ProjectFileNames(valid, macros)));
        Assert.Equal("crate.FLT", valid[2][1]);
        Assert.Equal("ROCK.TIF", valid[3][1]);
        var parsed = GameGenScriptText.Tokenize(Expected);
        Assert.Equal(normalized.Count, parsed.Count);
        for (int i = 0; i < parsed.Count; i++) Assert.Equal(normalized[i], parsed[i]);
    }

    [Fact]
    public async Task ReconstructionSkipsOnlyTheUnrepresentableScriptAndARepairedRetryWritesIt()
    {
        using SourceFixture fixture = new();
        string path = Path.Combine(fixture.Corpus, "interp.zbd");
        byte[] original = Package();
        await File.WriteAllBytesAsync(path, original, Token);
        var report = await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        Assert.Contains(report.Notes, n => n.Contains("bad.gw", StringComparison.Ordinal)
            && n.Contains("cannot be written as text and was skipped", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(fixture.Project, "gamegen", "bad.gw")));
        Assert.Equal(Expected, await File.ReadAllTextAsync(Path.Combine(fixture.Project, "gamegen", "good.gw"), Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));

        var package = FormatRegistry.Default.OpenBytes("interp.zbd", original, token: Token).Scripts!;
        var repaired = package.Entries[0] with { Instructions = [Instruction("Quit")] };
        await File.WriteAllBytesAsync(path, PreparedScriptWriter.Write(package with { Entries = [repaired, package.Entries[1]] }, Token), Token);
        string retry = Path.Combine(fixture.Root, "retry");
        var retried = await SourceExtractor.ExtractAsync(fixture.Corpus, retry, token: Token);
        Assert.DoesNotContain(retried.Notes, n => n.Contains("bad.gw", StringComparison.Ordinal));
        Assert.Equal("Quit\n", await File.ReadAllTextAsync(Path.Combine(retry, "gamegen", "bad.gw"), Token));
        Assert.Equal(Expected, await File.ReadAllTextAsync(Path.Combine(retry, "gamegen", "good.gw"), Token));
    }

    [Fact]
    public void AnEmptyPreparedScriptRemainsAnEmptySourceFile()
    {
        var package = FormatRegistry.Default.OpenBytes("interp.zbd", Package(), token: Token).Scripts!;
        var empty = package.Entries[0] with { Instructions = [] };
        byte[] bytes = PreparedScriptWriter.Write(package with { Entries = [empty] }, Token);
        var reread = FormatRegistry.Default.OpenBytes("interp.zbd", bytes, token: Token);
        Assert.Empty(reread.Diagnostics);
        var emptyPackage = Assert.IsType<PreparedScriptPackage>(reread.Scripts);
        var lines = Lines(Assert.Single(emptyPackage.Entries));
        Assert.Empty(lines);
        Assert.Equal("", GameGenScriptText.Write(GameGenScriptText.ProjectFileNames(lines, new HashSet<string>())));
        Assert.Equal(bytes, PreparedScriptWriter.Write(emptyPackage, Token));
    }

    private static IReadOnlyList<IReadOnlyList<string>> Lines(PreparedScriptEntry entry) =>
        entry.Instructions.Select(i => i.Tokens).ToArray();

    private static ScriptInstruction Instruction(params string[] tokens) => new(Guid.NewGuid(), tokens, ReadOnlyMemory<byte>.Empty, null);

    private static byte[] Package()
    {
        byte[] header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x08971119);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 7);
        // A positive-size, zero-token raw record, not a terminator. The reader retains its unused byte as padding.
        byte[] raw = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, 1); raw[8] = 0xA5;
        PreparedScriptEntry Entry(string name, params ScriptInstruction[] instructions)
        {
            byte[] directory = new byte[128]; Encoding.Latin1.GetBytes(name).CopyTo(directory, 0);
            return new(Guid.NewGuid(), null, name, 912_000_000, directory, instructions, ReadOnlyMemory<byte>.Empty);
        }
        PreparedScriptPackage package = new(header, ReadOnlyMemory<byte>.Empty,
        [
            Entry("bad.gw", new ScriptInstruction(Guid.NewGuid(), [], raw, null)),
            Entry("good.gw", Instruction("set", "dbName", "m1.flt"), Instruction("LoadGameGen", "%dbName%", "%dbName%"),
                Instruction("LoadGameGen", "crate.FLT", "crate.flt"), Instruction("TextureAdd", "ROCK.TIF"), Instruction("FindNode", "crate.flt")),
        ], ReadOnlyMemory<byte>.Empty);
        return PreparedScriptWriter.Write(package, Token);
    }
}
