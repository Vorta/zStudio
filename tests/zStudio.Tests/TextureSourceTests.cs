using System.Buffers.Binary;
using System.IO.Compression;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class TextureSourceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ReconstructionKeepsTheShapeOfThePackWithTheMostRoom()
    {
        (long, int) texture4 = (4L << 20, 1), rtexture2 = (2L << 20, 0), rtexture4 = (4L << 20, 0);
        // 1999 sonic06: the 128 x 16 strip, not rtexture2's 64 x 64 square that only stretches it (twice its texels).
        Assert.Equal(1, SourceExtractor.BestTextureVariant([(64, 64, rtexture2, false), (128, 16, texture4, true)]));
        // m5 swall3: equal areas; the wider shape of the packs with more room, then direct colour within that shape.
        Assert.Equal(2, SourceExtractor.BestTextureVariant([(128, 128, rtexture2, false), (256, 64, texture4, true), (256, 64, rtexture4, false)]));
    }

    private static DecodedImage Image(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) { var (r, g, b, a) = pixel(x, y); int o = (y * width + x) * 4; rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a; }
        return new(width, height, rgba);
    }

    /// <summary>A PNG with the given colour type, depth and interlacing, from raw samples per pixel.</summary>
    private static byte[] Png(int width, int height, int colorType, int depth, bool interlaced, Func<int, int, int[]> samples, byte[]? palette = null, byte[]? transparency = null)
    {
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 }, bits = channels * depth;
        (int X, int Y, int Dx, int Dy)[] passes = interlaced ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)] : [(0, 0, 1, 1)];
        using MemoryStream raw = new();
        foreach (var (px, py, dx, dy) in passes)
        {
            int w = width > px ? (width - px + dx - 1) / dx : 0, h = height > py ? (height - py + dy - 1) / dy : 0;
            if (w == 0 || h == 0) continue;
            for (int row = 0; row < h; row++)
            {
                raw.WriteByte(0); byte[] line = new byte[(w * bits + 7) / 8];
                for (int col = 0; col < w; col++)
                {
                    int[] s = samples(px + col * dx, py + row * dy);
                    for (int c = 0; c < channels; c++)
                    {
                        if (depth == 16) BinaryPrimitives.WriteUInt16BigEndian(line.AsSpan((col * channels + c) * 2), (ushort)s[c]);
                        else if (depth == 8) line[col * channels + c] = (byte)s[c];
                        else { int bit = (col * channels + c) * depth; line[bit >> 3] |= (byte)(s[c] << (8 - depth - (bit & 7))); }
                    }
                }
                raw.Write(line);
            }
        }
        using MemoryStream png = new(); png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)depth; header[9] = (byte)colorType; header[12] = (byte)(interlaced ? 1 : 0);
        Chunk("IHDR", header);
        if (palette != null) Chunk("PLTE", palette);
        if (transparency != null) Chunk("tRNS", transparency);
        Chunk("gAMA", [0, 0, 177, 143]);
        using MemoryStream compressed = new();
        using (ZLibStream z = new(compressed, CompressionLevel.Fastest, true)) z.Write(raw.ToArray());
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        return png.ToArray();
        void Chunk(string type, byte[] data)
        {
            byte[] word = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(word, data.Length); png.Write(word);
            byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data]; png.Write(typed);
            BinaryPrimitives.WriteUInt32BigEndian(word, PngDecoder.Crc(typed)); png.Write(word);
        }
    }

    [Fact]
    public void PngDecoderReadsEveryFormatEditorsSave()
    {
        // 16-bit RGBA, interlaced: every channel keeps its high byte.
        var rgba16 = PngDecoder.Decode(Png(5, 3, 6, 16, true, (x, y) => [x * 4000 + 255, y * 20000, 0xFF00, x == 1 ? 0 : 0xFFFF]), token: Token);
        Assert.Equal((5, 3), (rgba16.Width, rgba16.Height));
        Assert.Equal([0, 0, 255, 255], rgba16.Rgba[..4]); Assert.Equal(0, rgba16.Rgba[7]);
        Assert.Equal(156, rgba16.Rgba[(2 * 5 + 0) * 4 + 1]);
        // 2-bit palette with tRNS: entry 1 is half transparent, entries past tRNS are opaque.
        var paletted = PngDecoder.Decode(Png(9, 2, 3, 2, false, (x, y) => [(x + y) % 3], [10, 20, 30, 40, 50, 60, 70, 80, 90], [255, 128]), token: Token);
        Assert.Equal([10, 20, 30, 255], paletted.Rgba[..4]); Assert.Equal([40, 50, 60, 128], paletted.Rgba[4..8]); Assert.Equal([70, 80, 90, 255], paletted.Rgba[8..12]);
        // 1-bit greyscale scales to 0/255; 4-bit greyscale with a transparent grey value.
        var mono = PngDecoder.Decode(Png(10, 1, 0, 1, false, (x, _) => [x & 1]), token: Token);
        Assert.Equal([0, 0, 0, 255, 255, 255, 255, 255], mono.Rgba[..8]);
        var grey = PngDecoder.Decode(Png(3, 1, 0, 4, true, (x, _) => [x * 5], transparency: [0, 5]), token: Token);
        Assert.Equal([0, 0, 0, 255, 85, 85, 85, 0, 170, 170, 170, 255], grey.Rgba);
        // Grey+alpha and RGB with a transparent colour.
        Assert.Equal([9, 9, 9, 7], PngDecoder.Decode(Png(1, 1, 4, 8, false, (_, _) => [9, 7]), token: Token).Rgba);
        Assert.Equal([1, 2, 3, 0], PngDecoder.Decode(Png(1, 1, 2, 8, false, (_, _) => [1, 2, 3], transparency: [0, 1, 0, 2, 0, 3]), token: Token).Rgba);
        // The encoder's output reads back exactly; corruption and oversize are refused.
        var image = Image(7, 5, (x, y) => ((byte)(x * 30), (byte)(y * 40), 99, (byte)(x * y)));
        Assert.Equal(image.Rgba, PngDecoder.Decode(PngEncoder.Encode(image, Token), token: Token).Rgba);
        byte[] broken = PngEncoder.Encode(image, Token); broken[^20] ^= 1;
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(broken, token: Token));
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(PngEncoder.Encode(image, Token), 4, Token));
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Png(2, 2, 3, 8, false, (_, _) => [0]), token: Token));
    }

    private static AssetDocument Open(byte[] bytes) => FormatRegistry.Default.OpenBytes("pack.zbd", bytes, token: Token);

    [Fact]
    public void PacksFollowTheEngineRulesForEachRenderer()
    {
        var sky = Image(300, 60, (x, y) => ((byte)x, (byte)y, 200, 255));
        var keyed = Image(16, 16, (x, y) => x < 8 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)0, (byte)255));
        var glass = Image(32, 32, (x, y) => (10, 200, 30, (byte)(x * 8)));
        var tiny = Image(4, 2, (_, _) => (255, 0, 0, 255));
        PackTexture[] textures =
        [
            new("sky", "data\\m1\\textures\\sky.png", sky, Addressing: 2, Direct: true),
            new("keyed", "data\\common\\textures\\keyed.png", keyed),
            new("glass", "data\\common\\effects\\textures\\glass.png", glass, Addressing: 3),
            new("tiny", "data\\m1\\textures\\tiny.png", tiny),
        ];
        // Hardware: direct colour, powers of two, aspect ≤ 8, records in source-path order.
        var hardware = TexturePackBuilder.Build(textures, TexturePackVariant.FromFileName("rtexture16.zbd")!, Token);
        var doc = Open(hardware.Bytes);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(["glass", "keyed", "sky", "tiny"], doc.Assets.Select(a => a.Name));
        var info = doc.Assets.Select(a => (TextureInfo)a.Content!).ToArray();
        Assert.All(info, t => Assert.Equal(0, t.PaletteCount));
        Assert.Equal((256, 64), (info[2].Width, info[2].Height)); // nearest powers of two
        Assert.Equal((8, 8), (info[3].Width, info[3].Height));
        Assert.Equal([0x0B, 0x03, 0x05, 0x05], info.Select(t => (int)t.Flags));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(doc.Bytes.Span[(int)(doc.Assets[0].Offset + 14)..]));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(doc.Bytes.Span[(int)(doc.Assets[2].Offset + 14)..]));
        // Keyed: transparent texels are the key and opaque black is nudged off it.
        var decoded = TextureDecoder.Decode(doc, doc.Assets[1], Token);
        Assert.Equal(0, decoded.Rgba[3]); Assert.Equal(255, decoded.Rgba[8 * 4 + 3]);
        // Alpha planes keep their graded alpha.
        Assert.Equal(glass.Rgba.Where((_, i) => i % 4 == 3), TextureDecoder.Decode(doc, doc.Assets[0], Token).Rgba.Where((_, i) => i % 4 == 3));

        // Software: shared palette pages with a reserved black entry; the direct texture stays RGB565.
        var software = TexturePackBuilder.Build(textures, TexturePackVariant.FromFileName("texture8.zbd")!, Token);
        doc = Open(software.Bytes); info = doc.Assets.Select(a => (TextureInfo)a.Content!).ToArray();
        Assert.Empty(doc.Diagnostics);
        Assert.True(software.Pages >= 1);
        Assert.Equal([0x1B, 0x13, 0x05, 0x15], info.Select(t => (int)t.Flags));
        Assert.All(new[] { 0, 1, 3 }, i => Assert.Equal(256, info[i].PaletteCount));
        Assert.Equal(0, doc.Bytes.Span[info[1].PixelsOffset]); // keyed transparent → index 0
        Assert.NotEqual(0, doc.Bytes.Span[info[1].PixelsOffset + 8]); // keyed opaque black never uses index 0
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, TextureDecoder.Decode(doc, doc.Assets[3], Token).Rgba[..4]);

        // Budgets shrink textures evenly by halving, never below 8.
        var budgeted = TexturePackBuilder.Build(textures, new("rtexture1.zbd", TexturePackKind.Hardware, 4096, 256), Token);
        long used = Open(budgeted.Bytes).Assets.Select(a => (TextureInfo)a.Content!).Sum(t => (long)t.PixelsLength + (t.AlphaOffset >= 0 ? t.Width * t.Height : 0));
        Assert.True(used <= 4096, $"{used} bytes exceed the budget.");
        Assert.All(budgeted.Sizes, s => Assert.True(s.Width >= 8 && s.Height >= 8));
        var impossible = TexturePackBuilder.Build(textures, new("rtexture1.zbd", TexturePackKind.Hardware, 100, 256), Token);
        Assert.Contains(impossible.Warnings, w => w.Contains("budget"));

        // Interface images keep their authored size.
        var ui = Open(TexturePackBuilder.Build([new("sky", "a", sky), new("sky", "b", tiny)], TexturePackVariant.FromFileName("image.zbd")!, Token).Bytes);
        Assert.Equal((300, 60), (((TextureInfo)ui.Assets[0].Content!).Width, ((TextureInfo)ui.Assets[0].Content!).Height));
        Assert.Equal(2, ui.Assets.Count);
        Assert.Null(TexturePackVariant.FromFileName("texture0.zbd"));
        Assert.Equal(TexturePackKind.Software, TexturePackVariant.FromFileName("TEXTUREMAX.ZBD")!.Kind);
    }

    [Fact]
    public void DitheringKeepsExactColoursAndKeepsTheMeanOfOthers()
    {
        // Colours RGB565 holds exactly (the engine's expansion of its levels), as in a texture extracted from the game.
        static int Five(int level) => (level << 3) | (level >> 2);
        static int Six(int level) => (level << 2) | (level >> 4);
        var exact = Image(16, 16, (x, y) => ((byte)Five((x * 7 + y) % 32), (byte)Six((x * 5 + y * 3) % 64), (byte)Five((y * 11 + x) % 32), 255));
        // Red and blue 100 fall between the 5-bit levels 99 and 107: rounding alone shows 99 everywhere.
        var flat = Image(16, 16, (_, _) => (100, 100, 100, 255));
        // A keyed grey texture with a white panel.
        var sign = Image(64, 64, (x, y) => x == 0 && y == 0 ? ((byte)0, (byte)0, (byte)0, (byte)0) : x is >= 8 and < 56 && y is >= 16 and < 40 ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)60, (byte)60, (byte)64, (byte)255));
        // A colour-keyed texture stores green at five bits; it shows as that six-bit level, (q << 3) | (q >> 3).
        var keyed = Image(16, 16, (x, y) => ((byte)Five((x * 7 + y) % 31 + 1), (byte)((((x * 5 + y * 3) % 32) << 3) | (((x * 5 + y * 3) % 32) >> 3)), (byte)Five((y * 11 + x) % 32), (byte)((x + y) % 5 == 0 ? 0 : 255)));
        TexturePackVariant hardware = TexturePackVariant.FromFileName("rtexture16.zbd")!;
        byte[] Pack(DecodedImage image, TextureDithering dithering) => TexturePackBuilder.Build([new("t", "t", image)], hardware with { Dithering = dithering }, Token).Bytes;
        double MeanRed(byte[] pack) { var doc = Open(pack); return TextureDecoder.Decode(doc, doc.Assets[0], Token).Rgba.Where((_, i) => i % 4 == 0).Average(v => (double)v); }
        Assert.Equal(99, MeanRed(Pack(flat, TextureDithering.None)));
        foreach (var dithering in new[] { TextureDithering.Ordered, TextureDithering.Diffusion })
        {
            Assert.Equal(Pack(exact, TextureDithering.None), Pack(exact, dithering));
            Assert.Equal(Pack(keyed, TextureDithering.None), Pack(keyed, dithering));
            // A keyed green shows at most 251: white's error must not pile up and spill green below it.
            var doc = Open(Pack(sign, dithering));
            byte[] shown = TextureDecoder.Decode(doc, doc.Assets[0], Token).Rgba;
            Assert.InRange(Enumerable.Range(40 * 64, 64).Average(i => (double)shown[i * 4 + 1]), 55, 66);
            Assert.InRange(MeanRed(Pack(flat, dithering)), 99.5, 100.5);
        }
        // A software (paletted) pack stores black as 0x0020: diffusion must not pile error up across black and smear the colours
        // after it. More colours than a palette holds below the black, so the pack must dither them.
        var below = Image(64, 128, (x, y) => y < 64 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)(x * 5 % 96), (byte)((x * 3 + y * 7) % 96), (byte)((y * 11 + x) % 96), (byte)255));
        TexturePackVariant software = TexturePackVariant.FromFileName("texturemax.zbd")!;
        byte[] Software(TextureDithering dithering, DecodedImage image, params PackTexture[] others)
        {
            var doc = Open(TexturePackBuilder.Build([new("t", "t", image), .. others], software with { Dithering = dithering }, Token).Bytes);
            return TextureDecoder.Decode(doc, doc.Assets.First(a => a.Name == "t"), Token).Rgba;
        }
        double Green(byte[] rgba, int row) => Enumerable.Range(row * 64, 4 * 64).Average(i => (double)rgba[i * 4 + 1]);
        Assert.InRange(Green(Software(TextureDithering.Diffusion, below), 64), Green(Software(TextureDithering.None, below), 64) - 3, Green(Software(TextureDithering.None, below), 64) + 3);
        // Error reaching black from colours above it, on a page shared with other textures (dark entries near black), dies out
        // within a few rows instead of carrying on through the black.
        var random = new Random(7);
        var band = Image(64, 64, (x, y) => y >= 20 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)(60 + x * 2 + random.Next(20)), (byte)(50 + y * 4 + random.Next(20)), (byte)(40 + x + random.Next(20)), (byte)255));
        PackTexture[] noise = [.. Enumerable.Range(0, 6).Select(k =>
        {
            var r = new Random(k);
            return new PackTexture($"n{k}", $"n{k}", Image(64, 64, (_, _) => ((byte)Math.Clamp(40 + k * 15 + r.Next(-60, 60), 0, 255), (byte)Math.Clamp(60 + k * 10 + r.Next(-60, 60), 0, 255), (byte)Math.Clamp(50 + k * 12 + r.Next(-60, 60), 0, 255), (byte)255)));
        })];
        byte[] diffused = Software(TextureDithering.Diffusion, band, noise);
        Assert.Equal(0, Enumerable.Range(26 * 64, 38 * 64).Count(i => diffused[i * 4] + diffused[i * 4 + 1] + diffused[i * 4 + 2] > 4));
        // That entry is also the exact colour (0,4,0), common in the game's textures: dithering keeps it as it keeps every exact colour.
        var dark = Image(16, 16, (x, _) => x < 8 ? ((byte)0, (byte)4, (byte)0, (byte)255) : ((byte)16, (byte)16, (byte)16, (byte)255));
        Assert.Equal(Software(TextureDithering.None, dark), Software(TextureDithering.Diffusion, dark));
    }

    [Fact]
    public void MissionTexturesArePlacedBySortedRuns()
    {
        string[] names = ["fire1", "wsmok3", "ammolock", "rock", "a_door", "bf_sign", "tankwing", "wheel", "cliff", "zzz"];
        List<string> notes = [];
        var campaign = TextureSources.PlaceMission("m1", names, false, notes);
        Assert.Equal(TextureSources.EffectsTextures, campaign["wsmok3"]);
        Assert.Equal(TextureSources.CommonTextures, campaign["rock"]);
        Assert.Equal("data/m1/textures", campaign["a_door"]); // sorts before bft\
        Assert.Equal("data/m1/textures/bft", campaign["tankwing"]);
        Assert.Equal("data/m1/textures", campaign["cliff"]);
        var multiplayer = TextureSources.PlaceMission("m7", names, true, notes);
        Assert.Equal(TextureSources.MultiBftTextures, multiplayer["rock"]);
        Assert.Equal(TextureSources.CommonTextures, multiplayer["tankwing"]);
        Assert.Empty(notes);
        var odd = TextureSources.PlaceMission("m2", ["b", "a"], false, notes);
        Assert.Equal("data/m2/textures", odd["a"]); Assert.Single(notes);
        Assert.Equal(["data/common/multi_bft/textures", "data/common/effects/textures", "data/common/textures", "data/m9/textures"], TextureSources.MissionFolders("m9", true));
        Assert.Equal("data\\common\\textures\\rock.png", TextureSources.SortKey("data/common/Textures/Rock.png"));
    }

    [Fact]
    public void InterfaceImagesArePlacedByRecordedFolders()
    {
        static ZrdNode A(params ZrdNode[] c) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", c);
        static ZrdNode S(string t) => new(Guid.NewGuid(), ZrdKind.String, 0, t, []);
        (string, string, ZrdNode)[] resources =
        [
            ("zrdr.zbd", "dialog.zrd", A(A(S("IMAGE_PATH"), A(S("..\\data\\common\\images\\dialog\\Options")), S("BACKGROUND"), A(S("opback"), S("dup"))))),
            ("zrdr.zbd", "fonts.zrd", A(S("FONTS"), A(S("font8")))),
            ("zrdr.zbd", "hud.zrd", A(S("icon"))),
            ("m2/zrdr.zbd", "objectives.zrd", A(S("OBJECTIVE1"), A(S("obj1")))),
            ("m7/zrdr.zbd", "objectives.zrd", A(S("OBJECTIVE1"), A(S("obj1")))),
        ];
        // Records in pack order: font run, dialog run (with an unnamed image), then a second copy of "dup" in the HUD run.
        string[] names = ["font8", "fontx", "dup", "opback", "unnamed", "dup", "icon", "other", "obj1", "donut"];
        var folders = TextureSources.PlaceImages(names, resources);
        Assert.Equal(TextureSources.Fonts, folders[0]); Assert.Equal(TextureSources.Fonts, folders[1]);
        Assert.Equal("data/common/images/dialog/Options", folders[2]); Assert.Equal("data/common/images/dialog/Options", folders[4]);
        Assert.Equal(TextureSources.HudImages, folders[5]); Assert.Equal(TextureSources.HudImages, folders[7]);
        Assert.Equal("data/m2/images", folders[8]);
        Assert.Equal(TextureSources.Images, folders[9]);
        Assert.Null(TextureSources.RecordedFolder("..\\data\\..\\evil"));
        Assert.Null(TextureSources.RecordedFolder("C:\\data\\x"));
    }
}
