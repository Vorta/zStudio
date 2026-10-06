using System.Buffers.Binary;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Exports and reconstructions never overwrite or remove files another program wrote while they ran, and texture packs
/// are refused before their images are decoded when a pack file could not hold them (see also <see cref="PackDecodingTests"/>).
/// </summary>
public sealed class PublicationSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress> { public void Report(SourceProgress value) => action(value); }

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-publication-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Root);
        public string Write(string relative, string text) => Write(relative, System.Text.Encoding.ASCII.GetBytes(text));
        public string Write(string relative, byte[] bytes)
        {
            string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
            return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UndoingAPublicationKeepsAFileAnotherProgramWroteOverAnInstalledOutput(bool replacedOriginal)
    {
        using var folder = new Folder();
        string destination = Path.Combine(folder.Root, "game"), staging = Path.Combine(destination, ".zstudio-staging-test");
        folder.Write("game/.zstudio-staging-test/zrdr.zbd", "NEW ARCHIVE");
        folder.Write("game/.zstudio-staging-test/m1/zrdr.zbd", "NEW MISSION ARCHIVE");
        if (replacedOriginal) folder.Write("game/zrdr.zbd", "ORIGINAL ARCHIVE");
        string installed = Path.Combine(destination, "zrdr.zbd");
        // Once the first output is in place another program replaces it; then the second output fails.
        var error = Assert.Throws<IOException>(() => SourceBuilder.Publish(staging, destination, ["zrdr.zbd", "m1/zrdr.zbd"], overwrite: true, Token, (step, index) =>
        {
            if (step != "install" || index != 1) return;
            File.WriteAllText(installed, "ANOTHER PROGRAM");
            throw new IOException("forced failure");
        }));
        Assert.Contains("forced failure", error.Message);
        Assert.Equal("ANOTHER PROGRAM", File.ReadAllText(installed));
        Assert.False(File.Exists(Path.Combine(destination, "m1", "zrdr.zbd")));
        Assert.Contains(installed, error.Message);
        var backups = Directory.GetDirectories(destination, ".zstudio-backup-*");
        if (replacedOriginal)
        {
            // The original stays where the error says, instead of replacing the other program's file.
            string backup = Assert.Single(backups);
            Assert.Contains(backup, error.Message);
            Assert.Equal("ORIGINAL ARCHIVE", File.ReadAllText(Path.Combine(backup, "zrdr.zbd")));
        }
        else Assert.Empty(backups);
    }

    [Fact]
    public void UndoingAPublicationRestoresUntouchedOutputsExactly()
    {
        using var folder = new Folder();
        string destination = Path.Combine(folder.Root, "game"), staging = Path.Combine(destination, ".zstudio-staging-test");
        folder.Write("game/.zstudio-staging-test/zrdr.zbd", "NEW ARCHIVE");
        folder.Write("game/.zstudio-staging-test/m1/zrdr.zbd", "NEW MISSION ARCHIVE");
        folder.Write("game/.zstudio-staging-test/m1/anim.zbd", "NEW ANIMATIONS");
        folder.Write("game/zrdr.zbd", "ORIGINAL ARCHIVE");
        folder.Write("game/m1/anim.zbd", "ORIGINAL ANIMATIONS");
        var error = Assert.Throws<IOException>(() => SourceBuilder.Publish(staging, destination, ["zrdr.zbd", "m1/zrdr.zbd", "m1/anim.zbd"], overwrite: true, Token,
            (step, index) => { if (step == "install" && index == 2) throw new IOException("forced failure"); }));
        Assert.Equal("forced failure", error.Message);
        Assert.Equal("ORIGINAL ARCHIVE", File.ReadAllText(Path.Combine(destination, "zrdr.zbd")));
        Assert.Equal("ORIGINAL ANIMATIONS", File.ReadAllText(Path.Combine(destination, "m1", "anim.zbd")));
        Assert.False(File.Exists(Path.Combine(destination, "m1", "zrdr.zbd")));
        Assert.Empty(Directory.GetDirectories(destination, ".zstudio-backup-*"));
    }

    [Fact]
    public async Task ReconstructionNeverReplacesAFileAnotherProgramCreatesAtOneOfItsPaths()
    {
        using var fixture = new SourceFixture();
        // Created after the folder was found empty, before the m1 archive's resources are written.
        string theirs = Path.Combine(fixture.Project, "data", "m1", "zrdr", "ai.zrd");
        var progress = new OnReport(p =>
        {
            if (p.Completed != 0 || File.Exists(theirs)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(theirs)!); File.WriteAllText(theirs, "ANOTHER PROGRAM");
        });
        var error = await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, progress, Token));
        Assert.Contains("data/m1/zrdr/ai.zrd", error.Message);
        Assert.Contains("another program", error.Message);
        Assert.Equal("ANOTHER PROGRAM", File.ReadAllText(theirs));
        // What the reconstruction wrote before it stopped (the scripts) is gone; the other program's file stays.
        Assert.Equal([theirs], Directory.GetFiles(fixture.Project, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StoppingAReconstructionKeepsAFileAnotherProgramChanged()
    {
        using var fixture = new SourceFixture();
        string changed = Path.Combine(fixture.Project, "data", "m1", "zrdr", "ai.zrd");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        // After the m1 archive's resources were written, another program rewrites one; then the reconstruction is canceled.
        var progress = new OnReport(p =>
        {
            if (p.Completed != 2 || cancel.IsCancellationRequested) return;
            Assert.True(File.Exists(changed));
            File.WriteAllText(changed, "ANOTHER PROGRAM"); cancel.Cancel();
        });
        var error = await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, progress, cancel.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Contains("data/m1/zrdr/ai.zrd", error.Message);
        Assert.Equal("ANOTHER PROGRAM", File.ReadAllText(changed));
        Assert.Equal([changed], Directory.GetFiles(fixture.Project, "*", SearchOption.AllDirectories));
        // Once the changed file is moved away, the folder can be used again.
        Directory.Delete(fixture.Project, true);
        Assert.Equal(6, (await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token)).Families.Values.Sum());
    }

    /// <summary>The 33 bytes of a PNG's signature and header chunk: enough for its size, not for its pixels.</summary>
    internal static byte[] PngHeader(int width, int height)
    {
        byte[] png = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), 13); "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width); BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        png[24] = 8; png[25] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), PngDecoder.Crc(png.AsSpan(12, 17)));
        return png;
    }

    [Fact]
    public void PacksWhoseStoredTexturesExceedAPackFileAreRefusedBeforeAnyImageIsDecoded()
    {
        using var folder = new Folder();
        Directory.CreateDirectory(Path.Combine(folder.Root, "data", "m1")); Directory.CreateDirectory(Path.Combine(folder.Root, "gamegen"));
        // 4096 × 4096 PNGs of which only the headers exist: decoding any would fail. texturemax keeps every texture at full
        // size up to 1024 × 1024, a byte a texel, so 513 of them take more than the 512 MiB a pack file holds.
        string[] inputs = [.. Enumerable.Range(0, 513).Select(i => $"data/m1/textures/big{i:000}.png")];
        foreach (string input in inputs) folder.Write(input, PngHeader(4096, 4096));
        var pack = new SourceOutputPlan("m1/texturemax.zbd", "textures", inputs) { Pack = TexturePackVariant.FromFileName("texturemax.zbd") };
        var error = Assert.Throws<InvalidDataException>(() => SourceBuilder.Build(folder.Root, pack, new(folder.Root), DateTime.UtcNow, Token));
        Assert.StartsWith("texturemax.zbd would take at least 514 MiB with its textures at the sizes it stores, more than the 512 MiB a pack file can hold.", error.Message);
        Assert.Contains("Give the pack a budget or a smaller largest side", error.Message);
        Assert.Contains("big000 (1024 × 1024)", error.Message);
        // Interface images are stored at their authored size, two bytes a pixel: seventeen take 544 MiB.
        var images = new SourceOutputPlan("image.zbd", "images", inputs[..17]);
        error = Assert.Throws<InvalidDataException>(() => SourceBuilder.Build(folder.Root, images, new(folder.Root), DateTime.UtcNow, Token));
        Assert.StartsWith("image.zbd would take at least 545 MiB", error.Message);
        Assert.Contains("Use fewer or smaller images", error.Message);
        Assert.Contains("big000 (4096 × 4096)", error.Message);
        // What a pack file holds is decoded (and these headers fail there).
        var fits = new SourceOutputPlan("m1/texturemax.zbd", "textures", inputs[..511]) { Pack = pack.Pack };
        error = Assert.Throws<InvalidDataException>(() => SourceBuilder.Build(folder.Root, fits, new(folder.Root), DateTime.UtcNow, Token));
        Assert.StartsWith("data/m1/textures/big000.png:", error.Message);
        Assert.DoesNotContain("MiB", error.Message);
    }

    [Fact]
    public void TexturePacksWithMoreTexturesThanAPackHoldsAreRefusedBeforeAnyImageIsDecoded()
    {
        using var folder = new Folder();
        Directory.CreateDirectory(Path.Combine(folder.Root, "data", "m1")); Directory.CreateDirectory(Path.Combine(folder.Root, "gamegen"));
        // Pending files of a workspace: nothing is on disk, and none of them decodes.
        int count = TexturePackBuilder.MaximumRecords + 1;
        Dictionary<string, byte[]> pending = Enumerable.Range(0, count).ToDictionary(i => $"data/m1/textures/t{i:0000}.png", _ => PngHeader(8, 8), StringComparer.OrdinalIgnoreCase);
        var pack = new SourceOutputPlan("m1/rtexture16.zbd", "textures", [.. pending.Keys]) { Pack = TexturePackVariant.FromFileName("rtexture16.zbd") };
        var error = Assert.Throws<InvalidDataException>(() => SourceBuilder.Build(folder.Root, pack, new(folder.Root, pending), DateTime.UtcNow, Token));
        Assert.Contains($"{count:N0} textures", error.Message);
        Assert.Contains($"at most {TexturePackBuilder.MaximumRecords:N0}", error.Message);
    }
}
