using System.Buffers.Binary;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class ContentReaderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void ScriptReaderWriterPreservePaddingNamesAndTail()
    {
        byte[] bytes = ContentFixture.Scripts(); var doc = FormatRegistry.Default.OpenBytes("interp.zbd", bytes, token: Token);
        Assert.Empty(doc.Diagnostics); Assert.Equal(bytes, PreparedScriptWriter.Write(doc.Scripts!, Token));
        Assert.Equal("strange command", doc.Scripts!.Entries[0].Instructions[0].Tokens[0]);
        Assert.Equal(new[] { "strange command", "", "é\n\\\"" }, doc.Scripts.Entries[0].Instructions[0].Tokens);
    }
    [Theory]
    [InlineData(0, "count")]
    [InlineData(1, "size")]
    [InlineData(1, "string")]
    [InlineData(2, "terminator")]
    [InlineData(0, "index")]
    [InlineData(0, "outside")]
    public void DamagedScriptKeepsValidRecordsAndScopedDiagnostics(int index, string damage)
    {
        byte[] bytes = ContentFixture.DamagedScripts(index, damage), original = bytes.ToArray();
        var doc = FormatRegistry.Default.OpenBytes("damaged.zbd", bytes, token: Token);
        Assert.Equal(Enumerable.Range(0, 3).Where(i => i != index), doc.Assets.Select(a => a.Index));
        foreach (var asset in doc.Assets)
        {
            Assert.Equal(AssetKind.Script, asset.Kind); Assert.Equal($"script-{asset.Index}.zrd", asset.Name);
            Assert.Equal(new[] { "strange command", "", "é\n\\\"" }, Assert.IsType<ScriptContent>(asset.Content).Instructions.Single());
        }
        var diagnostic = Assert.Single(doc.Diagnostics);
        Assert.Equal("Error", diagnostic.Severity); Assert.Equal(index, diagnostic.AssetIndex);
        Assert.Equal((long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12 + index * 128 + 124)), diagnostic.Offset);
        Assert.Contains($"script-{index}.zrd", diagnostic.Message); Assert.DoesNotContain("Parsing stopped", diagnostic.Message);
        Assert.Null(doc.Scripts); Assert.Equal(original, doc.Bytes.ToArray());
    }
    [Fact]
    public void EmbeddedPaletteUsesStorageFlagRatherThanRuntimeOwnership()
    {
        byte[] bytes = ContentFixture.Texture(2, 1, false); var doc = FormatRegistry.Default.OpenBytes("a.zbd", bytes, token: Token);
        Assert.Empty(doc.Diagnostics); Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, TextureDecoder.Decode(doc, doc.Assets[0], Token).Rgba);
    }
    /// <summary>
    /// Must-have: a cut-out PNG (alpha only 0 or 255) is packed colour-keyed, and the engine shows those texels through three
    /// conversions; each must show the PNG's colours within one 5-bit step, its holes as the key and its black as opaque.
    /// A 3D pack and a world's texture directory refuse two textures the engine would chain as numbered mipmaps, which
    /// leaves one untextured or crashes the game as the mission loads.
    /// </summary>
    [Fact]
    public void PackedTexturesShowTheirColoursAndStaySeparateTextures()
    {
        // Odd RGB565 greens over blue below 128, such as (200, 100, 40), gained 132 blue on upload; (0, 3, 0) and black stored
        // 0x0020, which a 555 display reads as the key.
        (byte R, byte G, byte B)[] colours = [(200, 100, 40), (0, 0, 0), (255, 255, 255), (10, 250, 5), (90, 33, 120), (7, 130, 3), (0, 3, 0)];
        byte[] rgba = new byte[8 * 8 * 4];
        for (int i = 0; i < 64; i++)
        {
            var (r, g, b) = colours[i % colours.Length];
            rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = i % 5 == 4 ? (byte)0 : (byte)255;
        }
        static int Expand(int value, int bits) => bits == 5 ? value << 3 | value >> 2 : value << 2 | value >> 4;
        foreach (string pack in new[] { "rtexture16.zbd", "texture8.zbd", "image.zbd" })
        {
            byte[] bytes = TexturePackBuilder.Build([new("cut", "cut", new(8, 8, rgba), Direct: true)], TexturePackVariant.FromFileName(pack)!, Token).Bytes;
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24 + 32));
            Assert.Equal(0x03, bytes[offset]);
            for (int i = 0; i < 64; i++)
            {
                int s = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 16 + i * 2));
                // Direct3D on a 565 display uploads 1555 (ConvertImagePixelsForTexture, retail 0x4aa70d).
                int upload = (s & 0x1F) | ((s >> 1) & 0x7FF0) | (s != 0 ? 0x8000 : 0);
                // A 555 display converts each texel as it is read (zVid_Image::ReadData, retail 0x46ef05).
                int read555 = ((s >> 1) & 0x7FE0) | (s & 0x1F);
                bool opaque = rgba[i * 4 + 3] == 255;
                // The software renderer draws every nonzero texel as stored (retail 0x49b7e0).
                Assert.Equal(opaque, (upload & 0x8000) != 0); Assert.Equal(opaque, read555 != 0); Assert.Equal(opaque, s != 0);
                if (!opaque) continue;
                (int R, int G, int B)[] shown =
                [
                    (Expand(upload >> 10 & 31, 5), Expand(upload >> 5 & 31, 5), Expand(upload & 31, 5)),
                    (Expand(read555 >> 10 & 31, 5), Expand(read555 >> 5 & 31, 5), Expand(read555 & 31, 5)),
                    (Expand(s >> 11, 5), Expand(s >> 5 & 63, 6), Expand(s & 31, 5)),
                ];
                foreach (var (r, g, b) in shown)
                    Assert.True(Math.Abs(r - rgba[i * 4]) <= 8 && Math.Abs(g - rgba[i * 4 + 1]) <= 8 && Math.Abs(b - rgba[i * 4 + 2]) <= 8,
                        $"{pack} texel {i}: ({rgba[i * 4]}, {rgba[i * 4 + 1]}, {rgba[i * 4 + 2]}) is shown as ({r}, {g}, {b}).");
            }
        }

        // The engine loads rock_2 as a smaller copy of rock_1, whose first "_1" ends its name (BuildMipChain, retail
        // 0x46e3e0), whichever comes first and ignoring case in packs; within one texture directory it needs no pack record
        // (rock_2 loaded first is chained with an uninitialized image). A chain stops at its first missing number, a first
        // "_1" that does not end the name starts none, and image.zbd's interface images (the retail camp_1 and camp_2) are
        // not texture-directory entries.
        PackTexture Named(string name, string key) => new(name, key, new(8, 8, rgba));
        var hardware = TexturePackVariant.FromFileName("rtexture16.zbd")!;
        foreach (var (first, second) in new[] { ("rock_1", "Rock_2"), ("rock_2", "rock_1") })
        {
            string refused = Assert.Throws<InvalidDataException>(() => TexturePackBuilder.Build([Named(first, "a"), Named(second, "b")], hardware, Token)).Message;
            Assert.Contains("rock_1", refused, StringComparison.OrdinalIgnoreCase); Assert.Contains("rock_2", refused, StringComparison.OrdinalIgnoreCase);
            GameZWorld world = new(); WorldGltf.Texture(world, first);
            Assert.Throws<InvalidDataException>(() => WorldGltf.Texture(world, second)); Assert.Single(world.Textures);
        }
        Assert.Equal(4, TexturePackBuilder.Build([Named("x_1", "a"), Named("x_3", "b"), Named("y_1_1", "c"), Named("y_1_2", "d")], hardware, Token).Sizes.Count);
        Assert.Equal(2, TexturePackBuilder.Build([Named("camp_1", "a"), Named("camp_2", "b")], TexturePackVariant.FromFileName("image.zbd")!, Token).Sizes.Count);
    }
    [Fact]
    public async Task ReadOnlyScriptCorpusRoundTrips()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (string.IsNullOrEmpty(root)) return;
        string path = Path.Combine(root, "interp.zbd"); if (!File.Exists(path)) return;
        var doc = await FormatRegistry.Default.OpenAsync(path, Token); Assert.Equal(doc.Bytes.ToArray(), PreparedScriptWriter.Write(doc.Scripts!, Token));
    }
}
