using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Resampling for texture packs works a few rows at a time with exactly the arithmetic of whole-image passes, and malformed
/// glTF engine values and other authored values are shown in messages only as bounded previews.
/// </summary>
public sealed class PackGltfRound5Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DecodedImage Noise(int width, int height, int seed)
    {
        Random random = new(seed); byte[] rgba = new byte[width * height * 4]; random.NextBytes(rgba);
        // Every kind of alpha: transparent, opaque and partial.
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = (rgba[i] % 3) switch { 0 => 0, 1 => 255, _ => rgba[i] };
        return new(width, height, rgba);
    }

    /// <summary>The resampling as whole-image passes (zStudio 0.8 before row-wise resampling), which packs must keep matching.</summary>
    private static DecodedImage WholeImageResample(DecodedImage image, int width, int height)
    {
        if (width == image.Width && height == image.Height) return image;
        float[] source = new float[image.Width * image.Height * 4];
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            float a = image.Rgba[i * 4 + 3] / 255f;
            for (int c = 0; c < 3; c++) source[i * 4 + c] = image.Rgba[i * 4 + c] * a;
            source[i * 4 + 3] = image.Rgba[i * 4 + 3];
        }
        var horizontal = Weights(image.Width, width); var vertical = Weights(image.Height, height);
        float[] rows = new float[width * image.Height * 4];
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < width; x++)
                foreach (var (index, weight) in horizontal[x])
                    for (int c = 0; c < 4; c++) rows[(y * width + x) * 4 + c] += source[(y * image.Width + index) * 4 + c] * weight;
        byte[] rgba = new byte[width * height * 4]; Span<float> sum = stackalloc float[4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                sum.Clear();
                foreach (var (index, weight) in vertical[y]) for (int c = 0; c < 4; c++) sum[c] += rows[(index * width + x) * 4 + c] * weight;
                int o = (y * width + x) * 4; float alpha = Math.Clamp(sum[3], 0, 255);
                rgba[o + 3] = (byte)MathF.Round(alpha);
                for (int c = 0; c < 3; c++) rgba[o + c] = alpha <= 0 ? (byte)0 : (byte)Math.Clamp(MathF.Round(sum[c] / (alpha / 255f)), 0, 255);
            }
        return new(width, height, rgba);

        static (int Index, float Weight)[][] Weights(int from, int to)
        {
            var result = new (int, float)[to][];
            for (int t = 0; t < to; t++)
            {
                if (to <= from)
                {
                    double start = (double)t * from / to, end = (double)(t + 1) * from / to; List<(int, float)> list = [];
                    for (int s = (int)Math.Floor(start); s < Math.Min(from, (int)Math.Ceiling(end)); s++)
                    {
                        double cover = Math.Min(end, s + 1) - Math.Max(start, s);
                        if (cover > 0) list.Add((s, (float)(cover * to / from)));
                    }
                    result[t] = list.ToArray();
                }
                else
                {
                    double centre = Math.Clamp((t + 0.5) * from / to - 0.5, 0, from - 1); int s0 = (int)centre, s1 = Math.Min(s0 + 1, from - 1); float f = (float)(centre - s0);
                    result[t] = s1 == s0 ? [(s0, 1f)] : [(s0, 1 - f), (s1, f)];
                }
            }
            return result;
        }
    }

    [Theory]
    // Down, odd ratios, up, each axis alone, one axis up and the other down, a single texel or row, and extreme aspects.
    [InlineData(64, 64, 8, 8)]
    [InlineData(37, 23, 16, 8)]
    [InlineData(1000, 300, 256, 64)]
    [InlineData(300, 1000, 64, 256)]
    [InlineData(100, 60, 128, 64)]
    [InlineData(5, 3, 1024, 8)]
    [InlineData(48, 48, 48, 16)]
    [InlineData(48, 48, 16, 48)]
    [InlineData(200, 12, 64, 128)]
    [InlineData(12, 200, 128, 64)]
    [InlineData(1, 1, 8, 8)]
    [InlineData(2, 1, 16, 16)]
    [InlineData(1, 777, 8, 64)]
    [InlineData(4096, 9, 512, 8)]
    [InlineData(9, 4096, 8, 512)]
    [InlineData(513, 257, 1024, 512)]
    [InlineData(1024, 768, 24, 40)]
    [InlineData(777, 1023, 100, 9)]
    public void ResamplingRowByRowMatchesTheWholeImagePasses(int sourceWidth, int sourceHeight, int width, int height)
    {
        var image = Noise(sourceWidth, sourceHeight, sourceWidth * 7919 + sourceHeight);
        var expected = WholeImageResample(image, width, height);
        var actual = TexturePackBuilder.Resample(image, width, height, Token);
        Assert.Equal((width, height), (actual.Width, actual.Height));
        Assert.Equal(expected.Rgba, actual.Rgba);
    }

    [Theory]
    [InlineData(2048, 2048, 256, 256)]
    [InlineData(2048, 2048, 1024, 1024)]
    [InlineData(1536, 2560, 512, 1024)]
    [InlineData(300, 200, 1024, 512)]
    public void ResamplingHoldsAFewRowsBesidesTheResult(int sourceWidth, int sourceHeight, int width, int height)
    {
        var image = Noise(sourceWidth, sourceHeight, 5);
        TexturePackBuilder.Resample(Noise(17, 13, 1), 8, 8, Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var resampled = TexturePackBuilder.Resample(image, width, height, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The result, plus rows of workspace and the weights (whole-image passes took 16 bytes a source texel and 16 a
        // texel of the image resampled across: 64 MiB and more for a 2048 × 2048 master).
        long result = resampled.Rgba.LongLength, workspace = 64L * (sourceWidth + width) + 64L * (sourceHeight + height) + (256 << 10);
        Assert.True(allocated <= result + workspace, $"Resampling {sourceWidth} × {sourceHeight} to {width} × {height} allocated {allocated:N0} bytes for a {result:N0}-byte result.");
    }

    [Fact]
    public void AHardwarePackOfLargeMastersAllocatesNoFullSizeWorkspace()
    {
        // Four 2048 × 2048 masters (one image, decoded without allocating) stored at 1024 × 1024 in rtexture16.
        const int side = 2048, count = 4;
        var master = Noise(side, side, 9);
        for (int i = 3; i < master.Rgba.Length; i += 4) master.Rgba[i] = 255;
        var variant = TexturePackVariant.FromFileName("rtexture16.zbd")!;
        PackSource[] sources = [.. Enumerable.Range(0, count).Select(i => new PackSource($"t{i}", $"t{i}", side, side, _ => master))];
        TexturePackBuilder.BuildFromSources([new PackSource("w", "w", 64, 64, _ => Noise(64, 64, 3))], variant, Token);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var built = TexturePackBuilder.BuildFromSources(sources, variant, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.All(built.Sizes, s => Assert.Equal((1024, 1024), (s.Width, s.Height)));
        // Each texture at its stored size (4 MiB as RGBA) and the pack file (2 MiB a texture), with room to spare; resampling
        // each master as floats took 96 MiB more a texture.
        long stored = count * 4L * 1024 * 1024;
        Assert.True(allocated < stored + 2 * built.Bytes.LongLength + (16L << 20), $"Building the pack allocated {allocated:N0} bytes for {stored:N0} bytes of stored textures and a {built.Bytes.Length:N0}-byte pack.");
    }

    [Fact]
    public void AnAlphaPlaneIsWrittenWithoutACopyOfThePlane()
    {
        // A 2048 × 2048 texture with an alpha plane, stored at full size: the pack file is the only large allocation.
        const int side = 2048;
        var master = Noise(side, side, 11);
        TexturePackVariant variant = new("rtexture64.zbd", TexturePackKind.Hardware, null, 4096);
        TexturePackBuilder.BuildFromSources([new PackSource("w", "w", 64, 64, _ => Noise(64, 64, 3))], variant, Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var built = TexturePackBuilder.BuildFromSources([new PackSource("glass", "glass", side, side, _ => master)], variant, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(56 + 24 + 3L * side * side, built.Bytes.LongLength);
        Assert.Equal(master.Rgba.Where((_, i) => i % 4 == 3), built.Bytes.AsSpan(built.Bytes.Length - side * side).ToArray());
        Assert.True(allocated < built.Bytes.LongLength + (1 << 20), $"Building a {built.Bytes.Length:N0}-byte pack allocated {allocated:N0} bytes.");
    }

    /// <summary>A model of one node whose engine values give <paramref name="flags"/> (JSON text) as its flags.</summary>
    private static GltfDocument Model(string flags) => GltfDocument.Read(Encoding.UTF8.GetBytes(
        "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"name\":\"crate\",\"extras\":{\"" + WorldGltf.Key + "\":{\"flags\":" + flags + "}}}]}"),
        _ => throw new InvalidOperationException(), Token);

    private static InvalidDataException Import(GltfDocument doc) =>
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(doc, "crate.gltf", 0xFF,
            new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (u, n, _) => n ?? u }));

    private static long Allocated(Action action)
    {
        action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void AMalformedEngineValueIsShownAsABoundedPreview()
    {
        // An object where a hexadecimal word belongs, within the reader's metadata limit: written whole, its text would
        // escape six-fold.
        var doc = Model("{\"text\":\"" + new string('<', 1 << 20) + "\",\"more\":[1,2,3]}");
        var refused = Import(doc);
        Assert.StartsWith("crate.gltf: the engine value 'flags' is invalid ({\"text\":\"", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("…).", refused.Message, StringComparison.Ordinal);
        Assert.True(refused.Message.Length < 200, refused.Message);
        var small = Model("{\"text\":\"<\"}");
        long large = Allocated(() => Import(doc)), baseline = Allocated(() => Import(small));
        Assert.True(large - baseline < 64 << 10, $"Refusing the large value allocated {large:N0} bytes, a small one {baseline:N0}.");

        // A string that is not hexadecimal and a number with a million digits: as written, cut short.
        Assert.Matches("^crate\\.gltf: the engine value 'flags' is invalid \\(\"z{63}…\\)\\.$", Import(Model("\"" + new string('z', 1 << 20) + "\"")).Message);
        Assert.Matches("^crate\\.gltf: the engine value 'flags' is invalid \\(7{64}…\\)\\.$", Import(Model(new string('7', 1 << 20))).Message);
        // Small values read as before.
        Assert.Equal("crate.gltf: the engine value 'flags' is invalid (\"0xZZ\").", Import(Model("\"0xZZ\"")).Message);
        Assert.Equal("crate.gltf: the engine value 'flags' is invalid ({\"a\":[1,true,null,\"x\"]}).", Import(Model("{ \"a\": [1, true, null, \"x\"] }")).Message);
    }

    [Fact]
    public void JsonPreviewsReadOnlyWhatTheyShow()
    {
        Assert.Equal("null", JsonData.Shown(null));
        Assert.Equal("5", JsonData.Shown(JsonValue.Create(5)));
        Assert.Equal("\"a\\\"b\"", JsonData.Shown(JsonValue.Create("a\"b")));
        Assert.Equal("a\"b", JsonData.Shown(JsonValue.Create("a\"b"), asText: true));
        Assert.Equal("[]", JsonData.Shown(new JsonArray()));
        Assert.Equal("{\"x\":{\"y\":[1.5,false]}}", JsonData.Shown(JsonNode.Parse("{ \"x\" : { \"y\" : [ 1.5, false ] } }")));
        Assert.Equal("tile.terrain.json", JsonData.Shown(JsonNode.Parse("\"tile.terrain.json\""), asText: true));
        Assert.Equal(new string('a', 64) + "…", JsonData.Shown(JsonNode.Parse($"\"{new string('a', 5000)}\""), asText: true));
        Assert.Equal(new string('a', 64) + "…", JsonData.Shown(JsonValue.Create(new string('a', 5000)), asText: true));

        // A parsed object of a hundred thousand long strings, previewed after its first characters.
        // Parsed text is shown as written, with the escapes the writer chose.
        JsonObject large = []; for (int i = 0; i < 100_000; i++) large[$"key{i}"] = new string('<', 64);
        var parsed = JsonNode.Parse(large.ToJsonString())!;
        string first = JsonData.Shown(parsed);
        long before = GC.GetAllocatedBytesForCurrentThread();
        string shown = JsonData.Shown(parsed);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(first, shown);
        Assert.Equal(JsonData.ShownCharacters + 1, shown.Length);
        Assert.StartsWith("{\"key0\":\"\\u003C\\u003C", shown, StringComparison.Ordinal);
        Assert.EndsWith("…", shown, StringComparison.Ordinal);
        Assert.True(allocated < 16 << 10, $"The preview allocated {allocated:N0} bytes.");

        // A long string as text and a surrogate pair at the cut, which stays whole or is left out.
        string pairs = string.Concat(Enumerable.Repeat("😀", 100));
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 32)) + "…", JsonData.Shown(JsonNode.Parse($"\"{pairs}\""), asText: true));
        Assert.Equal("x" + string.Concat(Enumerable.Repeat("😀", 31)) + "…", JsonData.ShownText("x" + pairs));
        Assert.Equal("name", JsonData.ShownText("name"));
    }

    [Fact]
    public void RecipeAndReferenceRefusalsShowBoundedValues()
    {
        string id = new('<', 1 << 20);
        string recipe = $"{{\"format\":\"recoil-terrain\",\"version\":1,\"compiler\":1,\"surfaces\":[{{\"id\":\"{id}\",\"model\":\"a.gltf\",\"node\":\"n\"}}]}}";
        var refused = Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Encoding.UTF8.GetBytes(recipe), "m1.terrain.json"));
        Assert.Equal($"m1.terrain.json surface id \"{new string('<', 64)}…\" must be 1–32 letters, digits, _ or -.", refused.Message);

        string unknown = "{\"format\":\"recoil-terrain\",\"version\":1,\"compiler\":1,\"surfaces\":[{\"id\":\"s\",\"model\":\"a.gltf\",\"node\":\"n\",\"defaults\":{\"" + new string('k', 1 << 20) + "\":1}}]}";
        Assert.True(Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Encoding.UTF8.GetBytes(unknown), "m1.terrain.json")).Message.Length < 400);

        string uri = "C:" + new string('x', 1 << 20);
        Assert.Equal($"'{uri[..64]}…' is not a relative reference.", Assert.Throws<InvalidDataException>(() => WorldAssembler.Relative("data/m1/a.gltf", uri)).Message);
    }
}
