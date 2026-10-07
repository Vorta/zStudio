using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound12Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress>
    { public void Report(SourceProgress value) => action(value); }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"pbrMetallicRoughness\":{}}")]
    public void ImplicitMetalnessIsRefused(string material)
    {
        var error = Assert.Throws<InvalidDataException>(() => ReadMaterial(material));
        Assert.Contains("metallicFactor", error.Message);
    }

    [Theory]
    [InlineData("[2,-1,0,1]")][InlineData("[0,0,0,1.01]")][InlineData("[-0.01,0,0,1]")]
    public void BaseColourMustBeInUnitRange(string colour)
    {
        Assert.Throws<InvalidDataException>(() => ReadMaterial("{\"pbrMetallicRoughness\":{\"metallicFactor\":0,\"baseColorFactor\":" + colour + "}}"));
    }

    private static GltfDocument ReadMaterial(string material) => GltfDocument.Read(
        Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"materials\":[" + material + "]}"), _ => [], Token);

    [Fact]
    public void UnassignedGltfMaterialCannotBypassMetalnessPreflight()
    {
        var doc = GltfDocument.Read("""
            {"asset":{"version":"2.0"},"nodes":[{"mesh":0}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],
            "accessors":[{"componentType":5126,"type":"VEC3","count":3}]}
            """u8, _ => [], Token);
        Assert.Contains("metallicFactor", Assert.Throws<InvalidDataException>(() => WorldGltf.ValidateSupported(doc, "bare.gltf")).Message);
        // Writing a parsed default does not silently turn it nonmetallic either.
        var json = System.Text.Json.Nodes.JsonNode.Parse(doc.Write("bare.bin").Json)!;
        Assert.Equal(1, json["materials"]![0]!["pbrMetallicRoughness"]!["metallicFactor"]!.GetValue<float>());
    }

    [Fact]
    public async Task SameStampSourceRewritePreventsPublication()
    {
        using SourceWorldFixture fixture = new();
        const string source = "data/m1/zrdr/gates.zad";
        string path = fixture.Path(source), destination = Path.Combine(fixture.Root, "export");
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        string original = File.ReadAllText(path); bool changed = false;
        var progress = new OnReport(p =>
        {
            if (p.Item != "Publishing") return;
            fixture.Write(source, original.Replace("sink", "rise", StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(path, stamp); changed = true;
        });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination,
            ["m1/anim.zbd"], progress: progress, token: Token));
        Assert.True(changed); Assert.Contains("gates.zad", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(destination));
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/anim.zbd"], token: Token);
        Assert.True(File.Exists(Path.Combine(destination, "m1/anim.zbd")));
    }

    [Fact]
    public async Task UnselectedProfilePackIsReportedAndLeftUntouched()
    {
        using SourceWorldFixture fixture = new();
        string destination = Path.Combine(fixture.Root, "export"), stale = Path.Combine(destination, "m1/rtexture16.zbd");
        fixture.Write("gamegen/build-profiles/pair.json", """
            { "format":"recoil-build-profile", "version":1, "texturePacks":[{"file":"rtexture2.zbd"},{"file":"rtexture16.zbd"}] }
            """);
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!); File.WriteAllBytes(stale, [1, 2, 3]);
        var report = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture2.zbd"], token: Token, profile: "pair");
        Assert.Contains(report.Notes, n => n.StartsWith("m1/rtexture16.zbd", StringComparison.Ordinal));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(stale));
    }

    [Theory]
    [InlineData(0, 8, 2)][InlineData(4, 8, 2)]
    [InlineData(3, 1, 3)][InlineData(3, 2, 5)][InlineData(3, 4, 17)]
    public void ForbiddenPngPalettesAreRefused(int type, int depth, int colours)
        => Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Png(type, depth, colours), token: Token));

    [Theory]
    [InlineData(3, 1, 2)][InlineData(3, 2, 4)][InlineData(3, 4, 16)][InlineData(3, 8, 256)]
    [InlineData(2, 8, 256)][InlineData(6, 8, 256)]
    public void LegalPngPalettesRemainAccepted(int type, int depth, int colours)
        => Assert.Equal(1, PngDecoder.Decode(Png(type, depth, colours), token: Token).Width);

    private static byte[] Png(int type, int depth, int colours)
    {
        using MemoryStream png = new(); png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string name, byte[] data)
        {
            byte[] size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, data.Length); png.Write(size);
            byte[] body = [.. Encoding.ASCII.GetBytes(name), .. data]; png.Write(body);
            uint crc = uint.MaxValue;
            foreach (byte b in body) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
            BinaryPrimitives.WriteUInt32BigEndian(size, ~crc); png.Write(size);
        }
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, (byte)depth, (byte)type, 0, 0, 0]);
        Chunk("PLTE", new byte[colours * 3]);
        int channels = type switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };
        using MemoryStream compressed = new();
        using (ZLibStream z = new(compressed, CompressionLevel.SmallestSize, true)) z.Write(new byte[1 + (channels * depth + 7) / 8]);
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return png.ToArray();
    }
}
