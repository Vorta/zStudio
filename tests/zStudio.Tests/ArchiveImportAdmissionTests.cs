using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ArchiveImportAdmissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("resource.zrd")]
    [InlineData("definition.zad")]
    public async Task OversizedTextReadsOnlyTheRegistryProbe(string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes("( VALUE ( 42 ) )" + new string(' ', 1024));
        using CountingStream stream = new(bytes);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ResourceEditSession.ReadArchiveImportAsync(stream, name, 64, Token));
        Assert.Contains("source text", error.Message);
        Assert.Equal(44, stream.BytesRead);
        Assert.Equal(bytes, stream.ToArray());
    }

    [Theory]
    [InlineData(false, ".zrd")]
    [InlineData(true, ".zrd")]
    [InlineData(false, ".zad")]
    [InlineData(true, ".zad")]
    public async Task ActualAddAndReplaceUseTheEffectiveMemberNameAndPreserveHistory(bool replace, string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), "zstudio-archive-admission-" + Guid.NewGuid().ToString("N") + ".bin");
        byte[] text = Encoding.Latin1.GetBytes("( VALUE ( 42 ) )" + new string(' ', 80));
        byte[] compiled = ZrdWriter.Write(ZrdText.Parse("( VALUE ( 7 ) )", Token), Token);
        var edits = new ResourceEditSession(FormatRegistry.Default.OpenBytes("library.zbd",
            ResourceEditingTests.Archive(("target" + extension, compiled), ("sibling.bin", new byte[] { 99, 98 })), token: Token));
        var before = edits.Current;
        try
        {
            await File.WriteAllBytesAsync(path, text, Token);
            string action = replace ? "replace" : "add", name = replace ? "ignored.bin" : "added" + extension;
            Guid id = before.Members[0].Id;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareArchiveAsync(action, id, name, path, -1, text.Length - 1, Token));
            Assert.Contains("source text", error.Message);
            Assert.Same(before, edits.Current); Assert.False(edits.HasHistory); Assert.False(edits.IsDirty);
            var prepared = await edits.PrepareArchiveAsync(action, id, name, path, -1, text.Length, Token);
            Assert.Same(before, edits.Current);
            edits.Accept(prepared);
            var member = replace ? edits.Current.Members[0] : edits.Current.Members[^1];
            if (replace) Assert.Equal(id, member.Id); else Assert.DoesNotContain(before.Members, m => m.Id == member.Id);
            Assert.Equal(replace ? "target" + extension : name, member.Name);
            var tree = ZrdDecoder.Read(member.Data, Token);
            Assert.Equal(ZrdWriter.Write(ZrdText.Parse(text, Token), Token), ZrdWriter.Write(tree, Token));
            Assert.Equal(new byte[] { 99, 98 }, edits.Current.Members[1].Data.ToArray());
            Assert.Equal(text, await File.ReadAllBytesAsync(path, Token));
            edits.UndoRedo(false); Assert.Same(before, edits.Current); Assert.False(edits.IsDirty);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReplacingRawMemberDoesNotUseTheSelectedFileExtensionOrIgnoredName()
    {
        string path = Path.Combine(Path.GetTempPath(), "zstudio-raw-admission-" + Guid.NewGuid().ToString("N") + ".zrd");
        byte[] text = Encoding.Latin1.GetBytes("( RAW ( value ) )" + new string(' ', 80));
        var edits = new ResourceEditSession(FormatRegistry.Default.OpenBytes("library.zbd", ResourceEditingTests.Archive(("raw.bin", new byte[] { 99 })), token: Token));
        try
        {
            await File.WriteAllBytesAsync(path, text, Token);
            edits.Accept(await edits.PrepareArchiveAsync("replace", edits.Current.Members[0].Id, "ignored.zrd", path, -1, 1, Token));
            Assert.Equal("raw.bin", edits.Current.Members[0].Name);
            Assert.Equal(text, edits.Current.Members[0].Data.ToArray());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CompleteRegistryPrecedenceRetainsTheBinaryAndRawAllowance()
    {
        byte[] magic = new byte[40]; BinaryPrimitives.WriteUInt32LittleEndian(magic, 0x02971222); BinaryPrimitives.WriteUInt32LittleEndian(magic.AsSpan(4), 15);
        byte[] texture = new byte[24]; BinaryPrimitives.WriteUInt32LittleEndian(texture.AsSpan(4), 1);
        byte[] wave = new byte[40]; "RIFF"u8.CopyTo(wave); "WAVE"u8.CopyTo(wave.AsSpan(8));
        // A printable prefix looks like source text, but the archive trailer has higher registry precedence.
        byte[] archive = Enumerable.Repeat((byte)' ', 64).ToArray(); "( VALUE ( 1 ) )"u8.CopyTo(archive);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(56), 1); archive.AsSpan(60).Clear();
        byte[] compiled = ZrdWriter.Write(ZrdNode.Create(ZrdKind.String, "\"a compiled string\""), Token);
        foreach (var (bytes, name) in new[] { (magic, "magic.zrd"), (texture, "texture.zrd"), (wave, "wave.zad"), (archive, "archive.zrd"), (compiled, "compiled.zrd"), ("( raw text )"u8.ToArray(), "raw.bin") })
        {
            var expected = FormatRegistry.Probe(bytes.AsSpan(0, Math.Min(36, bytes.Length)), bytes.AsSpan(Math.Max(0, bytes.Length - 8)), bytes.Length, Path.GetExtension(name));
            Assert.NotEqual(FormatRegistry.SourceZrdDescription, expected.Description);
            using CountingStream stream = new(bytes);
            Assert.Equal(bytes, await ResourceEditSession.ReadArchiveImportAsync(stream, name, 1, Token));
            Assert.Equal(bytes.Length + Math.Min(36, bytes.Length) + Math.Min(8, bytes.Length), stream.BytesRead);
        }
    }

    [Theory]
    [InlineData("compiled.zrd")]
    [InlineData("compiled.zad")]
    public async Task ActualCompiledImportKeepsItsBytesAboveTheTextAllowance(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "zstudio-compiled-admission-" + Guid.NewGuid().ToString("N"));
        byte[] compiled = ZrdWriter.Write(ZrdNode.Create(ZrdKind.String, "\"complete compiled identity\""), Token);
        var edits = new ResourceEditSession(FormatRegistry.Default.OpenBytes("library.zbd", ResourceEditingTests.Archive(), token: Token));
        try
        {
            await File.WriteAllBytesAsync(path, compiled, Token);
            edits.Accept(await edits.PrepareArchiveAsync("add", Guid.Empty, name, path, -1, 1, Token));
            Assert.Equal(compiled, Assert.Single(edits.Current.Members).Data.ToArray());
            Assert.Equal("complete compiled identity", edits.Tree(edits.Current.Members[0], Token).Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancellationBeforeAdmissionReadsNothing()
    {
        using CountingStream stream = new("( value )"u8.ToArray());
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResourceEditSession.ReadArchiveImportAsync(stream, "a.zrd", 32, cancelled.Token));
        Assert.Equal(0, stream.BytesRead);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }
}
