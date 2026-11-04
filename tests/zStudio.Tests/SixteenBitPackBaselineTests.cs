using System.Security.Cryptography;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>
/// The 16-bit packs keep their bytes: reRECOIL v2 draws them until its renderer moves to the 32-bit packs and compares them
/// with its reference, so the 32-bit packs are written beside them without changing them.
/// </summary>
public sealed class SixteenBitPackBaselineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Must-have (data integrity): every 16-bit pack and image.zbd of a project with opaque, colour-keyed and alpha textures,
    /// at sizes the packs resample, exports to the bytes zStudio 0.9.13 (main 036ff35) wrote for the same sources, with no
    /// dithering and with error diffusion. A deliberate change to the 16-bit packs updates these digests.
    /// </summary>
    [Fact]
    public async Task SixteenBitPacksKeepTheBytesOfZStudio0913()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/textures/gradient.png", Png(100, 60, (x, y) => ((byte)(x * 2), (byte)(y * 4), (byte)(x + y), 255)));
        fixture.Write("data/m1/textures/keyed.png", Png(32, 32, (x, y) => ((byte)(x * 8), 90, (byte)(y * 8), (byte)((x / 4 + y / 4) % 3 == 0 ? 0 : 255))));
        fixture.Write("data/m1/textures/glass.png", Png(64, 16, (x, y) => (30, (byte)(x * 4), 200, (byte)(x * 4 + y))));
        fixture.Write("data/common/textures/shared.png", Png(48, 48, (x, y) => ((byte)(x * y), (byte)(x * 5), (byte)(255 - y * 5), 255)));
        fixture.Write("data/common/images/flare.png", Png(40, 24, (x, y) => (250, (byte)(x * 6), (byte)(y * 10), (byte)(x * 6 + y))));
        // Large enough that the 256-texel packs and the budgets of rtexture8, texture2 and texture4 reduce them.
        fixture.Write("data/m1/textures/wide.png", Png(1024, 1024, (x, y) => ((byte)(x ^ y), (byte)(x >> 2), (byte)(y >> 1), 255)));
        fixture.Write("data/m1/textures/veil.png", Png(1024, 1024, (x, y) => ((byte)(x >> 1), (byte)(x + y), 128, (byte)(y >> 2))));
        fixture.Write("data/m1/textures/haze.png", Png(1024, 1024, (x, y) => (200, (byte)(y >> 2), (byte)(x * 3), (byte)((x + y) >> 3))));
        string[] packs = ["rtexture2.zbd", "rtexture4.zbd", "rtexture8.zbd", "rtexture16.zbd", "texture2.zbd", "texture4.zbd", "texture6.zbd", "texture8.zbd", "texturemax.zbd"];
        string[] outputs = ["image.zbd", .. packs.Select(p => $"m1/{p}"), .. packs.Select(p => $"m2/{p}")];
        List<string> digests = [];
        foreach (var dithering in new[] { TextureDithering.None, TextureDithering.Diffusion })
        {
            string destination = Path.Combine(fixture.Root, dithering.ToString());
            var report = await SourceBuilder.ExportAsync(fixture.Project, destination, outputs, token: Token, dithering: dithering);
            Assert.Equal(0, report.Failed);
            foreach (string output in outputs)
                digests.Add($"{dithering} {output} {Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(destination, output), Token)))[..16]}");
        }
        Assert.Equal(Expected, string.Join("\n", digests));
    }

    /// <summary>The digests zStudio 0.9.13 (main 036ff35) gives: the first 16 hex digits of each file's SHA-256.</summary>
    private static readonly string Expected = string.Join("\n",
    [
        "None image.zbd 6501a8e7c7408ef2",
        "None m1/rtexture2.zbd 2a1ded6bbff3561e",
        "None m1/rtexture4.zbd 2a1ded6bbff3561e",
        "None m1/rtexture8.zbd 303242659d3a6669",
        "None m1/rtexture16.zbd 465aea1c204d3b7b",
        "None m1/texture2.zbd 7125a947f9c71798",
        "None m1/texture4.zbd 4e6575e39543338f",
        "None m1/texture6.zbd 03066333f1640422",
        "None m1/texture8.zbd 03066333f1640422",
        "None m1/texturemax.zbd 03066333f1640422",
        "None m2/rtexture2.zbd a071f2294938f082",
        "None m2/rtexture4.zbd a071f2294938f082",
        "None m2/rtexture8.zbd a071f2294938f082",
        "None m2/rtexture16.zbd a071f2294938f082",
        "None m2/texture2.zbd f39df4cd02e0d1a9",
        "None m2/texture4.zbd f39df4cd02e0d1a9",
        "None m2/texture6.zbd f39df4cd02e0d1a9",
        "None m2/texture8.zbd f39df4cd02e0d1a9",
        "None m2/texturemax.zbd f39df4cd02e0d1a9",
        "Diffusion image.zbd 1ce5f9124fe46a9c",
        "Diffusion m1/rtexture2.zbd 3a2a8d8617c6e109",
        "Diffusion m1/rtexture4.zbd 3a2a8d8617c6e109",
        "Diffusion m1/rtexture8.zbd e61dad7c0eb37f0d",
        "Diffusion m1/rtexture16.zbd 9be84d5d6895566a",
        "Diffusion m1/texture2.zbd 895cad9dc7e63d47",
        "Diffusion m1/texture4.zbd c943d04e8c9aff44",
        "Diffusion m1/texture6.zbd 8dd8892ea323dcb5",
        "Diffusion m1/texture8.zbd 8dd8892ea323dcb5",
        "Diffusion m1/texturemax.zbd 8dd8892ea323dcb5",
        "Diffusion m2/rtexture2.zbd 5e0ad3876859bde5",
        "Diffusion m2/rtexture4.zbd 5e0ad3876859bde5",
        "Diffusion m2/rtexture8.zbd 5e0ad3876859bde5",
        "Diffusion m2/rtexture16.zbd 5e0ad3876859bde5",
        "Diffusion m2/texture2.zbd 400d16741f341e68",
        "Diffusion m2/texture4.zbd 400d16741f341e68",
        "Diffusion m2/texture6.zbd 400d16741f341e68",
        "Diffusion m2/texture8.zbd 400d16741f341e68",
        "Diffusion m2/texturemax.zbd 400d16741f341e68"
    ]);

    private static byte[] Png(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) { var (r, g, b, a) = pixel(x, y); int o = (y * width + x) * 4; rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a; }
        return PngEncoder.Encode(new DecodedImage(width, height, rgba), TestContext.Current.CancellationToken);
    }
}
