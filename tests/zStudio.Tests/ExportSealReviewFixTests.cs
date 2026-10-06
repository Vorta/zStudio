using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// What a publication installs is what it built and checked: a staged output is held against other programs from its check
/// against the validated bytes until it is in place (exports, source saves and document saves), and the same sources
/// export to the same archives at any time and in any time zone.
/// </summary>
public sealed class ExportSealReviewFixTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress> { public void Report(SourceProgress value) => action(value); }

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-seal-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Root);
        public string Write(string relative, string text)
        {
            string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
            return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }
    private static JournalDigest Digest(string text) => JournalDigest.OfContent(Encoding.UTF8.GetBytes(text));
    /// <summary>Writes over a file as a program that shares it with every other: only a holder that denies writing stops it.</summary>
    private static void Overwrite(string path, string text)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.SetLength(0); stream.Write(Encoding.UTF8.GetBytes(text));
    }
    /// <summary>Reads a file as other programs can while it is held: sharing its deletion.</summary>
    private static string Shared(string path)
    {
        using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
        return reader.ReadToEnd();
    }

    [Fact]
    public void ASealedFileCannotBeWrittenRenamedOrDeletedUntilItIsInPlace()
    {
        using var folder = new Folder();
        string staged = folder.Write("staging/out.zbd", "BUILT"), target = Path.Combine(folder.Root, "game", "sub", "out.zbd");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var seal = SealedFile.Open(staged, Digest("BUILT")))
        {
            Assert.ThrowsAny<IOException>(() => Overwrite(staged, "OTHER"));
            Assert.ThrowsAny<IOException>(() => File.Move(staged, staged + ".moved"));
            Assert.ThrowsAny<Exception>(() => File.Delete(staged));
            // Reading stays possible (sharing its deletion, which the seal holds).
            Assert.Equal("BUILT", Shared(staged));
            seal.MoveTo(target);
            Assert.False(File.Exists(staged));
            // Still held where it was put, until it is released.
            Assert.ThrowsAny<IOException>(() => Overwrite(target, "OTHER"));
        }
        Assert.Equal("BUILT", File.ReadAllText(target));

        // A file that no longer has the checked content, or that another program is writing, is not held.
        folder.Write("staging/changed.zbd", "CHANGED");
        Assert.Contains("changed after it was checked", Assert.ThrowsAny<IOException>(() => SealedFile.Open(Path.Combine(folder.Root, "staging", "changed.zbd"), Digest("BUILT"))).Message);
        string writing = folder.Write("staging/writing.zbd", "BUILT");
        using (FileStream writer = new(writing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            Assert.Contains("Another program has", Assert.ThrowsAny<IOException>(() => SealedFile.Open(writing, Digest("BUILT"))).Message);

        // Replacing keeps the replaced file as the backup when one is asked for.
        string next = folder.Write("staging/next.zbd", "NEXT"), backup = target + ".bak";
        using (var seal = SealedFile.Open(next, Digest("NEXT"))) seal.Replace(target, backup);
        Assert.Equal("NEXT", File.ReadAllText(target));
        Assert.Equal("BUILT", File.ReadAllText(backup));
    }

    /// <summary>Staged outputs with the digests of the bytes they were built with.</summary>
    private static (string, JournalDigest)[] Built(params (string Relative, string Text)[] outputs) => [.. outputs.Select(o => (o.Relative, Digest(o.Text)))];

    [Fact]
    public void PublicationRefusesAStagedOutputChangedAfterItWasBuiltBeforeReplacingAnything()
    {
        using var folder = new Folder();
        string destination = Path.Combine(folder.Root, "game"), staging = Path.Combine(destination, ".zstudio-staging-test");
        folder.Write("game/.zstudio-staging-test/zrdr.zbd", "NEW ARCHIVE");
        folder.Write("game/.zstudio-staging-test/m1/zrdr.zbd", "NEW MISSION ARCHIVE");
        folder.Write("game/.zstudio-staging-test/m1/anim.zbd", "NEW ANIMATIONS");
        folder.Write("game/zrdr.zbd", "ORIGINAL ARCHIVE");
        folder.Write("game/m1/anim.zbd", "ORIGINAL ANIMATIONS");
        var outputs = Built(("zrdr.zbd", "NEW ARCHIVE"), ("m1/zrdr.zbd", "NEW MISSION ARCHIVE"), ("m1/anim.zbd", "NEW ANIMATIONS"));
        // Another program rewrites the last staged output after the export built and reopened it.
        var error = Assert.Throws<IOException>(() => SourceBuilder.Publish(staging, destination, outputs, overwrite: true, Token, (step, index) =>
        {
            if (step == "check" && index == 2) File.WriteAllText(Path.Combine(staging, "m1", "anim.zbd"), "ANOTHER PROGRAM");
        }));
        Assert.Contains("m1/anim.zbd was not installed", error.Message);
        Assert.Contains("changed after it was checked", error.Message);
        // Nothing was replaced or installed.
        Assert.Equal("ORIGINAL ARCHIVE", File.ReadAllText(Path.Combine(destination, "zrdr.zbd")));
        Assert.Equal("ORIGINAL ANIMATIONS", File.ReadAllText(Path.Combine(destination, "m1", "anim.zbd")));
        Assert.False(File.Exists(Path.Combine(destination, "m1", "zrdr.zbd")));
        Assert.Empty(Directory.GetDirectories(destination, ".zstudio-backup-*"));
    }

    [Fact]
    public void PublicationHoldsEveryStagedOutputUntilItIsInPlace()
    {
        using var folder = new Folder();
        string destination = Path.Combine(folder.Root, "game"), staging = Path.Combine(destination, ".zstudio-staging-test");
        folder.Write("game/.zstudio-staging-test/zrdr.zbd", "NEW ARCHIVE");
        folder.Write("game/.zstudio-staging-test/m1/zrdr.zbd", "NEW MISSION ARCHIVE");
        folder.Write("game/zrdr.zbd", "ORIGINAL ARCHIVE");
        List<string> refused = [];
        // Once the first output is in place, another program tries to change the second's staged file, which was checked
        // before anything was replaced, by writing it and by putting another file at its name.
        SourceBuilder.Publish(staging, destination, Built(("zrdr.zbd", "NEW ARCHIVE"), ("m1/zrdr.zbd", "NEW MISSION ARCHIVE")), overwrite: true, Token, (step, index) =>
        {
            if (step != "install" || index != 1) return;
            string second = Path.Combine(staging, "m1", "zrdr.zbd");
            try { Overwrite(second, "ANOTHER PROGRAM"); } catch (IOException ex) { refused.Add(ex.Message); }
            try { File.Move(second, second + ".aside"); File.WriteAllText(second, "ANOTHER PROGRAM"); } catch (IOException ex) { refused.Add(ex.Message); }
        });
        Assert.Equal(2, refused.Count);
        Assert.Equal("NEW ARCHIVE", File.ReadAllText(Path.Combine(destination, "zrdr.zbd")));
        Assert.Equal("NEW MISSION ARCHIVE", File.ReadAllText(Path.Combine(destination, "m1", "zrdr.zbd")));
    }

    [Fact]
    public async Task AnExportWhoseStagedOutputChangesAfterItWasBuiltIsRefusedAndUndone()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string destination = Path.Combine(fixture.Root, "export");
        await SourceBuilder.ExportAsync(fixture.Project, destination, token: Token);
        var before = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        // After every output was built and reopened, another program rewrites the staged mission archive.
        var progress = new OnReport(p =>
        {
            if (p.Item != "Publishing") return;
            string staging = Assert.Single(Directory.GetDirectories(destination, ".zstudio-staging-*"));
            File.WriteAllText(Path.Combine(staging, "m1", "zrdr.zbd"), "ANOTHER PROGRAM");
        });
        var error = await Assert.ThrowsAnyAsync<IOException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, overwrite: true, progress: progress, token: Token));
        Assert.Contains("m1/zrdr.zbd was not installed", error.Message);
        // The destination has the files of the first export, and nothing of the refused one.
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Order());
        Assert.All(before, file => Assert.Equal(file.Value, File.ReadAllBytes(file.Key)));
        Assert.Empty(Directory.GetDirectories(destination, ".zstudio-*"));
    }

    [Fact]
    public async Task ExportingTheSameSourcesAgainGivesTheSameArchivesAtAFixedTime()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string first = Path.Combine(fixture.Root, "first"), second = Path.Combine(fixture.Root, "second");
        await SourceBuilder.ExportAsync(fixture.Project, first, token: Token);
        await Task.Delay(2100, Token);
        await SourceBuilder.ExportAsync(fixture.Project, second, token: Token);
        string[] archives = ["zrdr.zbd", "m1/zrdr.zbd", "soundsh.zbd", "soundsm.zbd", "soundsl.zbd"];
        foreach (string archive in archives)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(first, archive));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(second, archive)));
            // Every member records the DOS epoch, in UTC: 00:00:00 as a DOS time (with bit 1, as the original compiler set it)
            // and as a FILETIME; never the time of the export or the machine's time zone.
            Assert.All(ArchiveSources.Read(bytes), member =>
            {
                Assert.Equal(2u, member.Aux);
                Assert.Equal((ulong)new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), member.FileTime);
            });
        }
        // Every other output is the same too.
        Assert.All(Directory.GetFiles(first, "*", SearchOption.AllDirectories), file => Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(second, Path.GetRelativePath(first, file)))));
    }

    [Fact]
    public void ASourceSaveWhoseStagedCopyChangesIsUndone()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-seal-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data", "m1")); Directory.CreateDirectory(Path.Combine(root, "gamegen"));
        try
        {
            string ai = Path.Combine(root, "data", "m1", "ai.zrd"), added = Path.Combine(root, "data", "m1", "added.zrd");
            File.WriteAllText(ai, "GRAVITY ( -9.8 )\n");
            byte[] original = File.ReadAllBytes(ai);
            SourceFileWrite[] writes = [new("data/m1/ai.zrd", original, Encoding.ASCII.GetBytes("GRAVITY ( -4.9 )\n")), new("data/m1/added.zrd", null, Encoding.ASCII.GetBytes("ADDED ( 1 )\n"))];
            // After the first file was installed, another program rewrites the second's staged copy before it is checked.
            SourcePublisher publisher = new(root)
            {
                Fault = (step, index) =>
                {
                    if (step != "intent" || index != 1) return;
                    File.WriteAllText(Assert.Single(Directory.GetFiles(Path.Combine(root, "zstudio", "staging"), "1.tmp", SearchOption.AllDirectories)), "ANOTHER PROGRAM");
                },
            };
            var error = Assert.ThrowsAny<IOException>(() => publisher.Publish(writes, "seal", Token));
            Assert.Contains("data/m1/added.zrd was not installed", error.Message);
            // The save was undone: the first file has its original content and the second was never created.
            Assert.Equal(original, File.ReadAllBytes(ai));
            Assert.False(File.Exists(added));
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, "zstudio", "recovery")));

            // While a staged copy is being installed, no other program can change it.
            List<string> refused = [];
            publisher.Fault = (step, index) =>
            {
                if (step != "install" || index != 1) return;
                try { Overwrite(Assert.Single(Directory.GetFiles(Path.Combine(root, "zstudio", "staging"), "1.tmp", SearchOption.AllDirectories)), "ANOTHER PROGRAM"); }
                catch (IOException ex) { refused.Add(ex.Message); }
            };
            publisher.Publish(writes, "seal", Token);
            Assert.Single(refused);
            Assert.Equal("GRAVITY ( -4.9 )\n", File.ReadAllText(ai));
            Assert.Equal("ADDED ( 1 )\n", File.ReadAllText(added));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task ADocumentSaveIsHeldFromItsVerificationUntilItIsInPlace()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-seal-document-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string pack = Path.Combine(root, "texture1.zbd"), png = Path.Combine(root, "image.png");
            await File.WriteAllBytesAsync(pack, ContentFixture.Texture(2, 1, true), Token);
            await File.WriteAllBytesAsync(png, PngEncoder.Encode(new(2, 1, [0, 0, 255, 255, 0, 0, 255, 255]), Token), Token);
            using AssetResolver resolver = new(root); var edits = new TextureEditSession(await resolver.OpenCachedAsync(pack, Token));
            edits.Accept(await edits.PrepareAsync(png, 0, "", [new(pack, 0)], resolver, Token));
            var publish = edits.PublishFile; List<string> refused = [];
            // The verified temporary file of the in-place save cannot be changed while it is put in place.
            edits.PublishFile = (file, target, createNew) =>
            {
                string temporary = Assert.Single(Directory.GetFiles(root, "*.tmp"));
                try { Overwrite(temporary, "ANOTHER PROGRAM"); } catch (IOException ex) { refused.Add(ex.Message); }
                publish(file, target, createNew);
            };
            var result = await edits.SaveAsync(token: Token);
            Assert.Empty(result.Errors);
            Assert.Single(refused);
            Assert.Equal(edits.Current.Documents[pack].Bytes.ToArray(), await File.ReadAllBytesAsync(pack, Token));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>
/// A texture pack keeps at most a bounded amount of its textures decoded (four bytes a texel) while it builds its palettes
/// and writes them; the others are decoded again for each pass, with the same bytes.
/// </summary>
// Collects garbage to count the images a build keeps; kept apart from tests that run in parallel.
[Collection("Allocation-sensitive")]
public sealed class TexturePackRetentionReviewFixTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DecodedImage Image(int seed, int side)
    {
        byte[] rgba = new byte[side * side * 4];
        uint state = (uint)seed * 2654435761u + 1;
        for (int p = 0; p < side * side; p++)
        {
            state = state * 1664525u + 1013904223u;
            rgba[p * 4] = (byte)(state >> 24); rgba[p * 4 + 1] = (byte)(p * 255 / (side * side)); rgba[p * 4 + 2] = (byte)(seed * 40 + (p & 31)); rgba[p * 4 + 3] = 255;
        }
        return new(side, side, rgba);
    }

    [Theory]
    // A software pack decodes each texture for its mean colour, its palette page's colours and its texels; a Direct3D pack
    // for its size and its texels. Two of twelve are kept, so the other ten are decoded again for each later pass.
    [InlineData("texturemax.zbd", 12 + 2 * 10)]
    [InlineData("rtexture64.zbd", 12 + 10)]
    public void PacksKeepNoMoreDecodedTexturesThanTheyMayRetain(string file, int expectedDecodes)
    {
        const int count = 12, side = 64;
        TexturePackVariant variant = file == "texturemax.zbd" ? TexturePackVariant.FromFileName(file)! : new(file, TexturePackKind.Hardware, null, 1024);
        // Each texture is stored at its own size, so the image a pack keeps is the decoded one.
        List<WeakReference> images = []; int decodes = 0, alive = 0;
        DecodedImage Decode(int i)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            alive = Math.Max(alive, images.Count(m => m.IsAlive));
            decodes++;
            var image = Image(i, side);
            images.Add(new(image));
            return image;
        }
        PackSource[] sources = [.. Enumerable.Range(0, count).Select(i => new PackSource($"t{i:00}", $"t{i:00}", side, side, _ => Decode(i)) { Transparency = TextureTransparency.Opaque })];
        var built = TexturePackBuilder.BuildFromSources(sources, variant, 2L * 4 * side * side, Token);
        Assert.Equal(expectedDecodes, decodes);
        // The two kept and the last decoded, which a local may still hold; keeping every texture would leave eleven.
        Assert.InRange(alive, 0, 4);
        // The same pack as one that keeps every texture, and as one built from every image decoded first.
        Assert.Equal(TexturePackBuilder.BuildFromSources([.. Enumerable.Range(0, count).Select(i => new PackSource($"t{i:00}", $"t{i:00}", side, side, _ => Image(i, side)))], variant, Token).Bytes, built.Bytes);
        Assert.Equal(TexturePackBuilder.Build([.. Enumerable.Range(0, count).Select(i => new PackTexture($"t{i:00}", $"t{i:00}", Image(i, side)))], variant, Token).Bytes, built.Bytes);
    }
}
