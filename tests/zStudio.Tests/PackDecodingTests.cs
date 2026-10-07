using System.Security.Cryptography;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Texture and image packs decode one source image at a time and keep their textures only at the sizes they store, so
/// what a build holds depends on what the pack keeps, not on how large the source images are; they are refused only when
/// the pack file could not hold them. Undoing a publication never removes an output whose original the backup lost, and a
/// canceled publication stays a cancellation.
/// </summary>
// Forces garbage collections; kept apart from tests that run in parallel.
[Collection("Allocation-sensitive")]
public sealed class PackDecodingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    /// <summary>Staged outputs with the digests of their staged files, as built.</summary>
    private static (string, JournalDigest)[] Staged(string staging, string[] outputs) => [.. outputs.Select(o => (o, JournalDigest.Of(File.ReadAllBytes(Path.Combine(staging, o)))!))];

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-pack-decoding-" + Guid.NewGuid().ToString("N"));
        public Folder() { Directory.CreateDirectory(Path.Combine(Root, "data", "m1")); Directory.CreateDirectory(Path.Combine(Root, "gamegen")); }
        public string Write(string relative, byte[] bytes)
        {
            string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
            return path;
        }
        public string Write(string relative, string text) => Write(relative, System.Text.Encoding.ASCII.GetBytes(text));
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    /// <summary>A deterministic image: gradients with noise, opaque, colour-keyed (a checkerboard of holes) or with an alpha ramp.</summary>
    private static DecodedImage Image(int seed, int width, int height, TextureTransparency kind)
    {
        byte[] rgba = new byte[width * height * 4];
        uint state = (uint)seed * 2654435761u + 1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                state = state * 1664525u + 1013904223u;
                int o = (y * width + x) * 4;
                rgba[o] = (byte)(x * 255 / width ^ (int)(state >> 28));
                rgba[o + 1] = (byte)(y * 255 / height);
                rgba[o + 2] = (byte)((x + y + seed * 37) & 255);
                rgba[o + 3] = kind switch { TextureTransparency.Opaque => 255, TextureTransparency.Keyed => ((x / 16 + y / 16) & 1) == 0 ? (byte)255 : (byte)0, _ => (byte)((x * 7 + y * 3) & 255) };
            }
        return new(width, height, rgba);
    }

    /// <summary>
    /// Opaque, keyed and alpha textures, one unpaletted, at sizes that are not powers of two, larger than the packs store,
    /// and below their smallest side.
    /// </summary>
    private static PackTexture[] Textures() =>
    [
        new("sky", @"data\common\textures\sky.png", Image(1, 640, 480, TextureTransparency.Opaque)),
        new("glass", @"data\common\textures\glass.png", Image(2, 300, 200, TextureTransparency.Alpha), 3),
        new("fence", @"data\m1\textures\fence.png", Image(3, 256, 64, TextureTransparency.Keyed), 1),
        new("rock", @"data\m1\textures\rock.png", Image(4, 1024, 1024, TextureTransparency.Opaque)),
        new("pock1", @"data\common\effects\textures\pock1.png", Image(5, 128, 128, TextureTransparency.Alpha), 0, true),
        new("wall", @"data\m1\textures\wall.png", Image(6, 2048, 512, TextureTransparency.Opaque), 2),
        new("tiny", @"data\m1\textures\tiny.png", Image(7, 5, 3, TextureTransparency.Keyed)),
    ];

    /// <summary>
    /// SHA-256 of each pack built from <see cref="Textures"/> by the builder that decoded every master before laying the pack
    /// out (before packs were decoded one texture at a time): Direct3D packs with and without a budget, software packs,
    /// interface images, and budgets small enough that alpha planes decide how far textures shrink.
    /// </summary>
    private static readonly (TexturePackVariant Variant, string Sha256)[] Previous =
    [
        (TexturePackVariant.FromFileName("rtexture2.zbd")!, "3d0831c92dbd56fa0d16b4584759ac0ae2ad7344326a7a153d7aa2f6e830d43a"),
        (TexturePackVariant.FromFileName("rtexture16.zbd")!, "7676bcf9dbf0b6b5c8903e830212ac59ba385d15101674771a5116f403d2f5af"),
        (new("rtexture64.zbd", TexturePackKind.Hardware, null, 4096), "15a5b119f849acfc40771d064faf40cc157dbb3bdd4189559c8d8d6fbdeb7db0"),
        (TexturePackVariant.FromFileName("texturemax.zbd")!, "009ced3f0564671a04e03abded4f3ead53ce1dff7fc2286621331e15da71c9b6"),
        (TexturePackVariant.FromFileName("image.zbd")!, "7b9add7ca2d9e9317cc3635d74fa42ee381c145c1b93a5530b1d001d0770ca5f"),
        (new("rtexture1.zbd", TexturePackKind.Hardware, 96 * 1024, 512), "6af06ec664d44f8f0e0bd4ca0a6251104553029260bff1ce1f17f10a57b7d25f"),
        (new("texture1.zbd", TexturePackKind.Software, 40 * 1024, 1024), "2afd634d72b46d94ef0bb39575b18b4d93c3460358fe5704e6520e6e753f78df"),
    ];

    [Fact]
    public void PacksBuiltOneTextureAtATimeHaveTheBytesOfPacksBuiltFromEveryMaster()
    {
        var textures = Textures();
        foreach (var (variant, sha) in Previous)
        {
            Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(TexturePackBuilder.Build(textures, variant, Token).Bytes)));
            // Decoded when needed, with the transparency of some textures already known (a run classifies each image once).
            int decodes = 0;
            PackSource[] sources = [.. textures.Select((t, i) => new PackSource(t.Name, t.SortKey, t.Master.Width, t.Master.Height, _ => { decodes++; return t.Master; }, t.Addressing, t.Direct)
                { Transparency = i % 2 == 0 ? TexturePackBuilder.Classify(t.Master) : null })];
            var built = TexturePackBuilder.BuildFromSources(sources, variant, Token);
            Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(built.Bytes)));
            // A texture whose transparency is unknown is decoded to classify it and kept at the size the pack stores without
            // a budget; only one the budget then reduces is decoded again.
            int unknown = textures.Length / 2;
            bool reduced = variant.BudgetBytes != null && !TexturePackBuilder.Build(textures, variant with { BudgetBytes = null }, Token).Sizes.SequenceEqual(built.Sizes);
            if (reduced) Assert.InRange(decodes, textures.Length, textures.Length + unknown);
            else Assert.Equal(textures.Length, decodes);
        }
    }

    [Theory]
    [InlineData(null, false, 8)]
    [InlineData(null, true, 8)]
    [InlineData(1024, false, 8)]
    [InlineData(1024, true, 8)]
    [InlineData(64, false, 16)]
    [InlineData(64, true, 8)]
    public void PacksHoldOneSourceImageAtATime(int? budgetKiB, bool known, int expectedDecodes)
    {
        const int count = 8, side = 512;
        // The packs store at most 128 × 128, so a source image is never what a pack keeps. These eight take 304 KiB:
        // a 1 MiB budget holds them, and 64 KiB reduces every one, which is then decoded again (unless its transparency was
        // known, so that it was decoded only once its final size was known).
        TexturePackVariant variant = new("rtexture1.zbd", TexturePackKind.Hardware, budgetKiB * 1024L, 128);
        static TextureTransparency Kind(int i) => i % 3 == 0 ? TextureTransparency.Alpha : TextureTransparency.Opaque;
        List<WeakReference> masters = []; int decodes = 0, alive = 0;
        DecodedImage Decode(int i)
        {
            // Every source image decoded before this one can be collected: none is kept while the next is decoded.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            alive = Math.Max(alive, masters.Count(m => m.IsAlive));
            decodes++;
            var image = Image(i, side, side, Kind(i));
            masters.Add(new(image));
            return image;
        }
        PackSource[] sources = [.. Enumerable.Range(0, count).Select(i => new PackSource($"t{i}", $"t{i}", side, side, _ => Decode(i)) { Transparency = known ? Kind(i) : null })];
        var built = TexturePackBuilder.BuildFromSources(sources, variant, Token);
        Assert.Equal(expectedDecodes, decodes);
        // The local that held the previous image may still be reported live; holding every image would leave count − 1.
        Assert.InRange(alive, 0, 2);
        Assert.All(built.Sizes, s => Assert.True(s.Width <= 128 && s.Height <= 128));
        // The same pack as from images decoded all at once.
        Assert.Equal(TexturePackBuilder.Build([.. Enumerable.Range(0, count).Select(i => new PackTexture($"t{i}", $"t{i}", Image(i, side, side, Kind(i))))], variant, Token).Bytes, built.Bytes);
    }

    [Fact]
    public void PacksKeepNoMoreThanTheirBudgetWhileClassifying()
    {
        // Sixteen opaque 128 × 128 images take 32 KiB each in a pack that stores them as they are, so the image a texture
        // is kept as is its source. A 64 KiB budget holds two of them: while the others are classified, at most those two
        // (and the local that held the last one) are kept; then every texture is reduced and decoded again.
        const int count = 16;
        TexturePackVariant variant = new("rtexture1.zbd", TexturePackKind.Hardware, 64 * 1024, 128);
        List<WeakReference> images = []; int decodes = 0, alive = 0;
        DecodedImage Decode(int i)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            alive = Math.Max(alive, images.Count(m => m.IsAlive));
            decodes++;
            var image = Image(i, 128, 128, TextureTransparency.Opaque);
            images.Add(new(image));
            return image;
        }
        var built = TexturePackBuilder.BuildFromSources([.. Enumerable.Range(0, count).Select(i => new PackSource($"t{i:00}", $"t{i:00}", 128, 128, _ => Decode(i)))], variant, Token);
        Assert.InRange(alive, 0, 4);
        Assert.Equal(2 * count, decodes);
        Assert.All(built.Sizes, s => Assert.True(s.Width * s.Height < 128 * 128));
    }

    [Fact]
    public void PacksWhoseFileWouldExceedWhatAPackFileHoldsAreRefusedBeforeTheirTexturesAreKept()
    {
        // A profile's pack may have a budget up to 1024 MiB, but a pack file holds at most 512 MiB: 1024 × 1024 Direct3D
        // textures take 2 MiB each, so 257 cannot be written. Their transparency is known, so nothing needs decoding first.
        TexturePackVariant variant = new("rtexture1024.zbd", TexturePackKind.Hardware, 1024L << 20, 1024);
        int decodes = 0;
        PackSource Source(int i, TextureTransparency transparency) => new($"t{i:000}", $"t{i:000}", 1024, 1024, _ => { decodes++; throw new InvalidDataException("decoded"); }) { Transparency = transparency };
        var error = Assert.Throws<InvalidDataException>(() => TexturePackBuilder.BuildFromSources([.. Enumerable.Range(0, 257).Select(i => Source(i, TextureTransparency.Opaque))], variant, Token));
        Assert.StartsWith("rtexture1024.zbd would take 515 MiB with its textures at the sizes it stores, more than the 512 MiB a pack file can hold. Give the pack a budget below 512 MiB (it has 1024 MiB)", error.Message);
        Assert.Contains("t000 (1024 × 1024)", error.Message);
        Assert.Equal(0, decodes);
        // Alpha planes count: 171 such textures take 3 MiB each.
        error = Assert.Throws<InvalidDataException>(() => TexturePackBuilder.BuildFromSources([.. Enumerable.Range(0, 171).Select(i => Source(i, TextureTransparency.Alpha))], variant, Token));
        Assert.StartsWith("rtexture1024.zbd would take 514 MiB", error.Message);
        Assert.Equal(0, decodes);
        // What a pack file holds is built: its textures are decoded.
        error = Assert.Throws<InvalidDataException>(() => TexturePackBuilder.BuildFromSources([.. Enumerable.Range(0, 255).Select(i => Source(i, TextureTransparency.Opaque))], variant, Token));
        Assert.Equal("decoded", error.Message);
        Assert.Equal(1, decodes);
    }

    [Theory]
    [InlineData("m1/rtexture2.zbd", null)]
    [InlineData("m1/rtexture16.zbd", null)]
    [InlineData("m1/texturemax.zbd", null)]
    [InlineData("m1/rtexture32.zbd", 4096)]
    [InlineData("image.zbd", null)]
    public void PacksAreNotRefusedForTheSizeOfTheirSourceImages(string output, int? side)
    {
        using var folder = new Folder();
        // Seventeen 4096 × 4096 sources take 1,088 MiB decoded at their authored sizes, but each mission pack keeps far
        // less: 256 × 256 (rtexture2), 1024 × 1024 fitted to 16 MiB (rtexture16), 1024 × 1024 a byte a texel (texturemax),
        // 4096 × 4096 fitted to 32 MiB (a profile's rtexture32). Interface images are kept at their authored size, two bytes
        // a pixel, so fifteen (480 MiB) fit in a pack file. Only the sources' headers exist, so the build decodes them and
        // fails there instead of refusing the pack.
        bool images = output == "image.zbd";
        string[] inputs = [.. Enumerable.Range(0, images ? 15 : 17).Select(i => $"data/m1/{(images ? "images" : "textures")}/big{i:00}.png")];
        foreach (string input in inputs) folder.Write(input, PublicationSafetyTests.PngHeader(4096, 4096));
        var variant = TexturePackVariant.FromFileName(Path.GetFileName(output))!;
        SourceOutputPlan plan = images ? new(output, "images", inputs) : new(output, "textures", inputs) { Pack = side is int largest ? variant with { MaximumDimension = largest } : variant };
        var error = Assert.Throws<InvalidDataException>(() => SourceBuilder.Build(folder.Root, plan, new(folder.Root), Token));
        Assert.StartsWith($"{inputs[0]}:", error.Message);
        Assert.DoesNotContain("MiB", error.Message);
    }

    [Fact]
    public void PacksOfOneRunClassifyEachImageOnceAndKeepTheirBytes()
    {
        using var folder = new Folder();
        (string Input, DecodedImage Image)[] sources =
        [
            ("data/m1/textures/sky.png", Image(1, 320, 240, TextureTransparency.Opaque)),
            ("data/m1/textures/glass.png", Image(2, 150, 100, TextureTransparency.Alpha)),
            ("data/m1/textures/fence.png", Image(3, 64, 32, TextureTransparency.Keyed)),
            ("data/m1/textures/rock.png", Image(4, 512, 512, TextureTransparency.Opaque)),
            ("data/m1/textures/bft/skin.png", Image(5, 256, 256, TextureTransparency.Alpha)),
        ];
        foreach (var (input, image) in sources) folder.Write(input, PngEncoder.Encode(image, Token));
        string[] inputs = [.. sources.Select(s => s.Input)];
        SourceBuilder.Snapshot snapshot = new(folder.Root);
        foreach (string output in (string[])["m1/rtexture2.zbd", "m1/texturemax.zbd", "m1/rtexture16.zbd", "image.zbd"])
        {
            var variant = TexturePackVariant.FromFileName(Path.GetFileName(output))!;
            SourceOutputPlan plan = output == "image.zbd" ? new(output, "images", inputs) : new(output, "textures", inputs) { Pack = variant };
            var built = SourceBuilder.Build(folder.Root, plan, snapshot, Token);
            // The pack from every image decoded first, as builds made it before (the bft skin stays unpaletted).
            var expected = TexturePackBuilder.Build([.. sources.Select(s => new PackTexture(SourceBuilder.TextureName(s.Input), TextureSources.SortKey(s.Input),
                PngDecoder.Decode(File.ReadAllBytes(Path.Combine(folder.Root, s.Input))), 0, plan.Family == "textures" && s.Input.Contains("/bft/", StringComparison.Ordinal)))], variant, Token);
            Assert.Equal(expected.Bytes, built.Bytes);
            // From the first pack on, every image's size and transparency are known for the run.
            Assert.All(sources, s => Assert.Equal((s.Image.Width, s.Image.Height, TexturePackBuilder.Classify(s.Image)), snapshot.Texture(s.Input)));
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UndoingAPublicationNeverRemovesAnOutputWhoseOriginalTheBackupLost(bool installed, bool deleted)
    {
        using var folder = new Folder();
        string destination = Path.Combine(folder.Root, "game"), staging = Path.Combine(destination, ".zstudio-staging-test"), target = Path.Combine(destination, "zrdr.zbd");
        folder.Write("game/.zstudio-staging-test/zrdr.zbd", "NEW ARCHIVE");
        folder.Write("game/.zstudio-staging-test/m1/zrdr.zbd", "NEW MISSION ARCHIVE");
        folder.Write("game/zrdr.zbd", "ORIGINAL ARCHIVE");
        // Once the original is in the backup (and, when installed, the new archive in its place), another program deletes
        // or changes that original; then publication fails. Publication now holds the backup directory itself in place,
        // but its unsealed contents remain externally writable, so remove the file to exercise the missing-original case.
        var error = Assert.Throws<IOException>(() => SourceBuilder.Publish(staging, destination, Staged(staging, ["zrdr.zbd", "m1/zrdr.zbd"]), overwrite: true, Token, (step, index) =>
        {
            if (step != "install" || index != (installed ? 1 : 0)) return;
            string backup = Assert.Single(Directory.GetDirectories(destination, ".zstudio-backup-*"));
            string original = Path.Combine(backup, "zrdr.zbd");
            if (deleted) { File.Delete(original); Assert.False(File.Exists(original)); }
            else File.WriteAllText(original, "ANOTHER PROGRAM");
            throw new IOException("forced failure");
        }));
        Assert.Contains("forced failure", error.Message);
        Assert.Contains(target, error.Message);
        Assert.Contains("no longer in", error.Message);
        Assert.DoesNotContain("remain in", error.Message);
        // The exported archive stays rather than leaving no file; nothing else was installed.
        if (installed) Assert.Equal("NEW ARCHIVE", File.ReadAllText(target));
        else Assert.False(File.Exists(target));
        Assert.False(File.Exists(Path.Combine(destination, "m1", "zrdr.zbd")));
        if (!deleted) Assert.Equal("ANOTHER PROGRAM", File.ReadAllText(Path.Combine(Assert.Single(Directory.GetDirectories(destination, ".zstudio-backup-*")), "zrdr.zbd")));
    }

    [Fact]
    public void ACanceledPublicationThatMeetsAnotherProgramsFileStaysACancellation()
    {
        using var folder = new Folder();
        string destination = Path.Combine(folder.Root, "game"), staging = Path.Combine(destination, ".zstudio-staging-test"), first = Path.Combine(destination, "zrdr.zbd");
        folder.Write("game/.zstudio-staging-test/zrdr.zbd", "NEW ARCHIVE");
        folder.Write("game/.zstudio-staging-test/m1/zrdr.zbd", "NEW MISSION ARCHIVE");
        folder.Write("game/.zstudio-staging-test/m1/anim.zbd", "NEW ANIMATIONS");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        // After the first output is in place, another program writes over it and the export is canceled.
        var error = Assert.ThrowsAny<OperationCanceledException>(() => SourceBuilder.Publish(staging, destination, Staged(staging, ["zrdr.zbd", "m1/zrdr.zbd", "m1/anim.zbd"]), overwrite: false, cancel.Token, (step, index) =>
        {
            if (step != "replace" || index != 1) return;
            File.WriteAllText(first, "ANOTHER PROGRAM"); cancel.Cancel();
        }));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.StartsWith("The export was canceled.", error.Message);
        Assert.Contains(first, error.Message);
        Assert.Equal("ANOTHER PROGRAM", File.ReadAllText(first));
        Assert.False(File.Exists(Path.Combine(destination, "m1", "zrdr.zbd")));
        Assert.False(File.Exists(Path.Combine(destination, "m1", "anim.zbd")));
    }
}
