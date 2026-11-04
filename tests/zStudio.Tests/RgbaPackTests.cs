using System.Buffers.Binary;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>
/// The 32-bit texture and image packs for reRECOIL v2 (docs/rgba-packs.md): reRECOIL's data dumps rely on a pack holding its
/// masters' decoded pixels exactly, on the tiers' budgets and on deterministic exports, and its decoder on the readers refusing
/// what the format forbids.
/// </summary>
public sealed class RgbaPackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const long MiB = 1 << 20;

    [Fact]
    public async Task FullSizePacksHoldTheMastersDecodedPixelsExactly()
    {
        // Must-have (data integrity): every PNG encoding a project may hold reaches the pack as zStudio decodes it, at its
        // authored size, with the RGB of transparent pixels; in the 16-bit packs' names, order and edge modes.
        using SourceWorldFixture fixture = new();
        Dictionary<string, byte[]> masters = new()
        {
            // 16-bit RGBA, interlaced: channels reduced to their high byte.
            ["data/m1/textures/deep.png"] = TextureSourceTests.Png(5, 3, 6, 16, true, (x, y) => [x * 4000 + 255, y * 20000, 0xFF00, x == 1 ? 0 : 0xFFFF]),
            // 2-bit palette with tRNS; entries past tRNS opaque.
            ["data/m1/textures/indexed.png"] = TextureSourceTests.Png(9, 2, 3, 2, false, (x, y) => [(x + y) % 3], [10, 20, 30, 40, 50, 60, 70, 80, 90], [255, 128]),
            // 8-bit RGB with a tRNS key: keyed pixels are transparent and keep their RGB.
            ["data/m1/textures/keyed.png"] = TextureSourceTests.Png(7, 5, 2, 8, false, (x, y) => (x + y) % 2 == 0 ? [200, 10, 30] : [x * 30, y * 40, 7], transparency: [0, 200, 0, 10, 0, 30]),
            // 4-bit grey (scaled by 17) and 16-bit grey + alpha.
            ["data/m1/textures/grey.png"] = TextureSourceTests.Png(6, 2, 0, 4, false, (x, y) => [x + y * 6]),
            ["data/common/images/hud_grey.png"] = TextureSourceTests.Png(3, 3, 4, 16, false, (x, y) => [x * 0x5555, y * 0x7FFF]),
        };
        // RGBA8 at a size no 16-bit pack stores, with colour under alpha 0.
        byte[] rgba = new byte[300 * 20 * 4];
        for (int p = 0; p < 300 * 20; p++) { rgba[p * 4] = (byte)p; rgba[p * 4 + 1] = (byte)(p >> 8); rgba[p * 4 + 2] = 77; rgba[p * 4 + 3] = (byte)(p % 3 == 0 ? 0 : p % 251); }
        masters["data/common/images/strip.png"] = PngEncoder.Encode(new(300, 20, rgba), Token);
        foreach (var (path, png) in masters) fixture.Write(path, png);
        // The decode rules the specification states.
        var deep = PngDecoder.Decode(masters["data/m1/textures/deep.png"], token: Token);
        Assert.Equal([0, 0, 255, 255], deep.Rgba[..4]); Assert.Equal(156, deep.Rgba[(2 * 5) * 4 + 1]);
        var keyed = PngDecoder.Decode(masters["data/m1/textures/keyed.png"], token: Token);
        Assert.Equal([200, 10, 30, 0], keyed.Rgba[..4]);
        Assert.Equal([0, 17, 34, 51], PngDecoder.Decode(masters["data/m1/textures/grey.png"], token: Token).Rgba.Where((_, i) => i % 4 == 0).Take(4));

        var plan = SourceBuilder.Plan(fixture.Project, token: Token);
        var tiers = plan.Where(p => p.Family == "rgbatextures").ToArray();
        Assert.Equal(["m1/rgbatexture64.zbd", "m2/rgbatexture64.zbd"], tiers.Select(p => p.Path));
        Assert.All(tiers, p => Assert.True(p.FullSize));
        string destination = Path.Combine(fixture.Root, "game");
        var report = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rgbatexture64.zbd", "m1/rtexture16.zbd", "image.zbd", "rgbaimage.zbd"], token: Token);
        Assert.Equal(0, report.Failed);
        Assert.All(report.Outputs, o => Assert.Empty(o.Reduced));

        foreach (var (pack, sixteen, kind) in new[] { ("m1/rgbatexture64.zbd", "m1/rtexture16.zbd", 1u), ("rgbaimage.zbd", "image.zbd", 2u) })
        {
            var doc = Open(destination, pack);
            Assert.Empty(doc.Diagnostics);
            Assert.Equal((kind, kind == 1 ? 64u : 0u), (BinaryPrimitives.ReadUInt32LittleEndian(doc.Bytes.Span[12..]), BinaryPrimitives.ReadUInt32LittleEndian(doc.Bytes.Span[16..])));
            var packed = Open(destination, sixteen);
            // The same names in the same order, each with the 16-bit record's edge mode.
            Assert.Equal(packed.Assets.Select(a => a.Name), doc.Assets.Select(a => a.Name));
            foreach (var (asset, other) in doc.Assets.Zip(packed.Assets))
                Assert.Equal(BinaryPrimitives.ReadUInt16LittleEndian(packed.Bytes.Span[(int)(other.Offset + 14)..]) & 3, asset.Metadata["addressing"]!.GetValue<long>());
            foreach (var asset in doc.Assets)
            {
                string? source = masters.Keys.FirstOrDefault(k => Path.GetFileNameWithoutExtension(k) == asset.Name);
                byte[] png = source != null ? masters[source] : await File.ReadAllBytesAsync(fixture.Path($"data/m1/textures/{asset.Name}.png"), Token);
                var master = PngDecoder.Decode(png, token: Token);
                var stored = TextureDecoder.Decode(doc, asset, Token);
                Assert.Equal((master.Width, master.Height), (stored.Width, stored.Height));
                Assert.Equal(master.Rgba, stored.Rgba);
            }
        }
    }

    [Fact]
    public async Task SmallerTiersFitTheirBudgetAndExportsAreDeterministic()
    {
        // Must-have (core workflow): a mission whose textures outgrow 64 MB gets the full-size tier that holds them and a
        // 64 MB tier that reduces them to fit, reported per texture; the same sources export to the same bytes.
        using SourceWorldFixture fixture = new();
        for (int i = 0; i < 17; i++)
        {
            byte[] rgba = new byte[1024 * 1024 * 4];
            for (int p = 0; p < 1024 * 1024; p++) { rgba[p * 4] = (byte)(p + i); rgba[p * 4 + 1] = (byte)(p >> 10); rgba[p * 4 + 2] = (byte)i; rgba[p * 4 + 3] = (byte)(255 - (p & 63)); }
            fixture.Write($"data/m1/textures/large{i:D2}.png", PngEncoder.Encode(new(1024, 1024, rgba), Token));
        }
        // 17 textures of 4 MiB need 68 MiB at their authored size: the 128 MB tier holds them, the 64 MB tier reduces them.
        var tiers = SourceBuilder.Plan(fixture.Project, token: Token).Where(p => p.Family == "rgbatextures" && p.Path.StartsWith("m1/", StringComparison.Ordinal)).ToArray();
        Assert.Equal([("m1/rgbatexture64.zbd", false, 64 * MiB), ("m1/rgbatexture128.zbd", true, 128 * MiB)], tiers.Select(p => (p.Path, p.FullSize, p.Pack!.BudgetBytes!.Value)));
        string[] outputs = ["m1/rgbatexture64.zbd", "m1/rgbatexture128.zbd", "m1/rtexture16.zbd"];
        string first = Path.Combine(fixture.Root, "first"), second = Path.Combine(fixture.Root, "second");
        var report = await SourceBuilder.ExportAsync(fixture.Project, first, outputs, token: Token);
        Assert.Equal(0, report.Failed);
        await SourceBuilder.ExportAsync(fixture.Project, second, outputs, token: Token);
        foreach (string output in outputs) Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(first, output), Token), await File.ReadAllBytesAsync(Path.Combine(second, output), Token));

        var full = Open(first, "m1/rgbatexture128.zbd");
        var reduced = Open(first, "m1/rgbatexture64.zbd");
        Assert.Empty(full.Diagnostics); Assert.Empty(reduced.Diagnostics);
        Assert.Empty(report.Outputs.Single(o => o.Path == "m1/rgbatexture128.zbd").Reduced);
        Assert.InRange(reduced.Assets.Sum(a => a.Length), 1, 64 * MiB);
        var listed = report.Outputs.Single(o => o.Path == "m1/rgbatexture64.zbd").Reduced;
        Assert.NotEmpty(listed);
        // The full-size tier stores each texture as authored; the report names each texture the 64 MB tier stored smaller,
        // with both sizes, and that tier stores the others as authored.
        Assert.Equal(full.Assets.Select(a => a.Name), reduced.Assets.Select(a => a.Name));
        foreach (var (whole, fitted) in full.Assets.Zip(reduced.Assets))
        {
            var (authored, stored) = ((TextureInfo)whole.Content!, (TextureInfo)fitted.Content!);
            var entry = listed.SingleOrDefault(r => r.Name == fitted.Name);
            Assert.Equal(entry == null ? (authored.Width, authored.Height) : (entry.StoredWidth, entry.StoredHeight), (stored.Width, stored.Height));
            if (entry != null) Assert.Equal((authored.Width, authored.Height), (entry.Width, entry.Height));
        }
        Assert.Equal(17, full.Assets.Count(a => a.Content is TextureInfo { Width: 1024, Height: 1024, Rgba: true }));
        Assert.Equal(PngDecoder.Decode(await File.ReadAllBytesAsync(fixture.Path("data/m1/textures/large07.png"), Token), token: Token).Rgba,
            TextureDecoder.Decode(full, full.Assets.Single(a => a.Name == "large07"), Token).Rgba);
    }

    [Fact]
    public void ReadersRefuseWhatTheFormatForbids()
    {
        // Must-have (data integrity): the shared reader, which every export's output must reopen through, never reads past a
        // pack's end and reports each break of the format; reRECOIL's decoder implements the same rules.
        var build = RgbaPackBuilder.Build([Source("b", 2, 1), Source("a", 1, 2)], RgbaPackKind.MissionTextures, 64, "rgbatexture64.zbd", Token);
        byte[] valid = build.Bytes;
        var doc = FormatRegistry.Default.OpenBytes("rgbatexture64.zbd", valid, token: Token);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(["a", "b"], doc.Assets.Select(a => a.Name));
        Assert.Equal(24 + 2 * 31 + 16, valid.Length);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], TextureDecoder.Decode(doc, doc.Assets[0], Token).Rgba);

        Assert.Equal(Recognition.Malformed, Probe(valid[..20]));
        byte[] version = (byte[])valid.Clone(); version[8] = 2;
        Assert.Equal(Recognition.UnsupportedVersion, Probe(version));
        byte[] count = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(count.AsSpan(20), 1000);
        Assert.Equal(Recognition.Malformed, Probe(count));
        // Pixels that do not follow the previous record's, a byte past the last pixel, a duplicate name, an interface image
        // with an edge mode.
        byte[] gap = (byte[])valid.Clone(); gap[24 + 31 + 3 + 12]++;
        Errors(gap, "not at 0x");
        Errors([.. valid, 0], "not at its last pixel");
        byte[] duplicate = (byte[])valid.Clone(); duplicate[24 + 2] = (byte)'b';
        Errors(duplicate, "another record has its name");
        byte[] images = RgbaPackBuilder.Build([Source("a", 1, 1)], RgbaPackKind.InterfaceImages, 0, "rgbaimage.zbd", Token).Bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(images.AsSpan(24 + 3 + 8), 1);
        Errors(images, "an interface image stores addressing 0");
        // A name length of zero ends the record table: nothing past it is read.
        byte[] table = (byte[])valid.Clone(); table[24] = 0;
        Errors(table, "the record table ends there");

        static Recognition Probe(byte[] bytes) => FormatRegistry.Probe(bytes.AsSpan(0, Math.Min(36, bytes.Length)), bytes.AsSpan(Math.Max(0, bytes.Length - 8)), bytes.Length).Recognition;
        void Errors(byte[] bytes, string expected)
        {
            var broken = FormatRegistry.Default.OpenBytes("rgbatexture64.zbd", bytes, token: Token);
            Assert.Contains(broken.Diagnostics, d => d.Severity == "Error" && d.Message.Contains(expected, StringComparison.Ordinal));
        }
        PackSource Source(string name, int width, int height)
        {
            byte first = (byte)(name == "a" ? 1 : 11);
            byte[] pixels = [.. Enumerable.Range(0, width * height * 4).Select(i => (byte)(first + i))];
            return new(name, name, width, height, _ => new DecodedImage(width, height, pixels));
        }
    }

    private static AssetDocument Open(string folder, string output)
    {
        string path = Path.Combine(folder, output);
        return FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: TestContext.Current.CancellationToken);
    }
}
