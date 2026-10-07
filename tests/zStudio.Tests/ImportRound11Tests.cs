using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound11Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress>
    { public void Report(SourceProgress value) => action(value); }

    [Fact]
    public async Task MissingDefinitionAppearingBeforePublicationRefusesTheExport()
    {
        using SourceWorldFixture fixture = new();
        const string definition = "data/m1/zrdr/gates.zad";
        byte[] original = File.ReadAllBytes(fixture.Path(definition)); File.Delete(fixture.Path(definition));
        string destination = Path.Combine(fixture.Root, "export"); bool appeared = false;
        var progress = new OnReport(p => { if (p.Item == "Publishing") { fixture.Write(definition, original); appeared = true; } });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/anim.zbd"], progress: progress, token: Token));
        Assert.True(appeared); Assert.Contains("gates.zad", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(destination));
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/anim.zbd"], token: Token);
        Assert.True(File.Exists(Path.Combine(destination, "m1/anim.zbd")));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void CyclesOutsideSelectedRootsAreRefused(bool scene)
    {
        string json = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"children\":[1]},{\"children\":[0]},{}]" +
            (scene ? ",\"scenes\":[{\"nodes\":[2]}],\"scene\":0" : "") + "}";
        Assert.Contains("cycle", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => [], Token)).Message);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void UnselectedDepthIsCheckedRegardlessOfNodeNumbering(bool reversed)
    {
        int count = GltfDocument.MaximumDepth + 1;
        JsonArray nodes = [];
        for (int i = 0; i < count; i++) nodes.Add(i == (reversed ? 0 : count - 1) ? new JsonObject() : new JsonObject { ["children"] = new JsonArray(reversed ? i - 1 : i + 1) });
        nodes.Add(new JsonObject());
        var json = new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["nodes"] = nodes, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(count) }) };
        Assert.Contains("deeper", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), _ => [], Token)).Message);
    }

    [Fact]
    public void SnapshotProbesCheckAbsencePresenceAndPendingContent()
    {
        using SourceWorldFixture fixture = new();
        const string missing = "data/m1/zrdr/new.zad", present = "data/m1/zrdr/gates.zad";
        SourceBuilder.Snapshot snapshot = new(fixture.Project);
        Assert.False(snapshot.Files().Exists(missing)); Assert.True(snapshot.Files().Exists(present));
        snapshot.CheckUnchanged(Token);
        fixture.Write(missing, "new"); Assert.Throws<InvalidDataException>(() => snapshot.Files().Exists(missing));
        Assert.Throws<InvalidDataException>(() => snapshot.Read(missing, Token));
        Assert.Throws<InvalidDataException>(() => snapshot.CheckUnchanged(Token));
        File.Delete(fixture.Path(missing)); File.Delete(fixture.Path(present));
        Assert.Throws<InvalidDataException>(() => snapshot.CheckUnchanged(Token));
        SourceBuilder.Snapshot pending = new(fixture.Project, new Dictionary<string, byte[]> { [missing] = [1, 2] });
        Assert.True(pending.Files().Exists(missing)); Assert.Equal([1, 2], pending.Read(missing, Token));
        Assert.Empty(pending.Missing()); pending.CheckUnchanged(Token);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void SoftwarePacksKeepWideAndTallTextureDetail(bool tall)
    {
        int width = tall ? 8 : 1024, height = tall ? 1024 : 8;
        var image = Image(width, height, 255);
        var software = TexturePackBuilder.Build([new("stripe", "a", image)], TexturePackVariant.FromFileName("texturemax.zbd")!, Token);
        var info = (TextureInfo)Assert.Single(FormatRegistry.Default.OpenBytes("texturemax.zbd", software.Bytes, token: Token).Assets).Content!;
        Assert.Equal((width, height), (info.Width, info.Height));
        var hardware = TexturePackBuilder.Build([new("stripe", "a", image)], TexturePackVariant.FromFileName("rtexture16.zbd")!, Token);
        Assert.Equal(tall ? (8, 64) : (64, 8), (hardware.Sizes[0].Width, hardware.Sizes[0].Height));
    }

    [Fact]
    public async Task DifferingDuplicatesWithinOnePackAreNotRankedAsVariants()
    {
        using SourceFixture fixture = new();
        byte[] pack = TexturePackBuilder.Build([new("rock", "a", Image(8, 8, 255)), new("ROCK", "b", Image(16, 16, 0))],
            TexturePackVariant.FromFileName("rtexture16.zbd")!, Token).Bytes;
        File.WriteAllBytes(Path.Combine(fixture.Corpus, "m1/rtexture16.zbd"), pack);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token));
        Assert.Contains("duplicate texture", error.Message);
        Assert.False(Directory.Exists(fixture.Project));
    }

    [Fact]
    public void DuplicateComparisonIncludesExternalPalettesAndHasABudget()
    {
        byte[] bytes = TexturePackBuilder.Build([new("rock", "a", Image(8, 8, 255)), new("ROCK", "b", Image(8, 8, 255))],
            TexturePackVariant.FromFileName("texturemax.zbd")!, Token).Bytes;
        var doc = FormatRegistry.Default.OpenBytes("texturemax.zbd", bytes, token: Token);
        SourceExtractor.ValidateTextureNames("texturemax.zbd", doc, Token);
        Assert.Throws<InvalidDataException>(() => SourceExtractor.ValidateTextureNames("texturemax.zbd", doc, Token, maximumComparedBytes: 1));
        // Alias the image body but give the second record a different external palette page.
        int firstOffset = (int)doc.Assets[0].Offset;
        var info = (TextureInfo)doc.Assets[0].Content!;
        Assert.True((info.Flags & 16) != 0);
        int pages = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        int tableEnd = 24 + 2 * 40 + pages * 512;
        byte[] other = new byte[bytes.Length + 512];
        bytes.AsSpan(0, tableEnd).CopyTo(other);
        bytes.AsSpan(info.PaletteOffset, 512).CopyTo(other.AsSpan(tableEnd));
        bytes.AsSpan(tableEnd).CopyTo(other.AsSpan(tableEnd + 512));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(other.AsSpan(8), pages + 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(other.AsSpan(24 + 32), firstOffset + 512);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(other.AsSpan(24 + 40 + 32), firstOffset + 512);
        SourceExtractor.ValidateTextureNames("texturemax.zbd", FormatRegistry.Default.OpenBytes("texturemax.zbd", other, token: Token), Token, maximumComparedBytes: 0); // Exact aliases need no scan.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(other.AsSpan(24 + 40 + 36), pages);
        Assert.Throws<InvalidDataException>(() => SourceExtractor.ValidateTextureNames("texturemax.zbd", FormatRegistry.Default.OpenBytes("texturemax.zbd", other, token: Token), Token, maximumComparedBytes: 1));
        other[tableEnd + bytes[info.PixelsOffset] * 2] ^= 1;
        Assert.Throws<InvalidDataException>(() => SourceExtractor.ValidateTextureNames("texturemax.zbd", FormatRegistry.Default.OpenBytes("texturemax.zbd", other, token: Token), Token));
        var differingDoc = FormatRegistry.Default.OpenBytes("image.zbd", other, token: Token);
        SourceExtractor.ValidateTextureNames("image.zbd", differingDoc, Token, identity: a => $"data/m{a.Index + 1}/images/{a.Name}");
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void EmbeddedImagesCannotBecomePlainSurfaces(bool bufferView)
    {
        var model = Model(new GltfMaterial { ImageUri = "rock.png" });
        var (json, bin) = model.Write("mesh.bin", TestContext.Current.CancellationToken); var root = JsonNode.Parse(json)!;
        root["images"]![0] = bufferView ? new JsonObject { ["bufferView"] = 0, ["mimeType"] = "image/png" }
            : new JsonObject { ["uri"] = "data:image/png;base64,AA==" };
        var doc = GltfDocument.Read(Encoding.UTF8.GetBytes(root.ToJsonString()), _ => bin, Token);
        Assert.Contains("embedded image", Assert.Throws<InvalidDataException>(() => Import(doc)).Message);
    }

    [Fact]
    public void ConflictingSamplersForOneTextureRefuseTheModel()
    {
        var model = Model(new() { ImageUri = "rock.png", ClampS = true }, new() { ImageUri = "ROCK.png" });
        Assert.Contains("edge modes", Assert.Throws<InvalidDataException>(() => Import(model)).Message);
        Assert.Contains("edge modes", Assert.Throws<InvalidDataException>(() => WorldGltf.ValidateSupported(model, "model.gltf")).Message);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void BlenderCheckoutUsesTheSameMaterialPreflight(bool embedded)
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/models/m1.gltf";
        var doc = embedded ? Model(new GltfMaterial { ImageUri = "../textures/rock.png" })
            : Model(new() { ImageUri = "../textures/rock.png", ClampS = true }, new() { ImageUri = "../textures/rock.png" });
        var (json, bin) = doc.Write("m1.bin", TestContext.Current.CancellationToken); var root = JsonNode.Parse(json)!;
        if (embedded) root["images"]![0]!["uri"] = "data:image/png;base64,AA==";
        fixture.Write(path, root.ToJsonString()); fixture.Write("data/m1/models/m1.bin", bin);
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Contains(embedded ? "embedded image" : "edge modes", Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(workspace, path, Token)).Message);
        Assert.False(workspace.IsDirty); Assert.Empty(SourceBlender.Checkouts(fixture.Project, Token));
    }

    [Fact]
    public async Task DifferentLoadsCannotAssignDifferentModesToTheSameTexture()
    {
        using SourceWorldFixture fixture = new();
        const string model = "data/m2/models/bft/tank.gltf";
        var root = JsonNode.Parse(File.ReadAllBytes(fixture.Path(model)))!;
        root["images"]![0]!["uri"] = "../../textures/rock.png";
        root["materials"]![0]!["extras"]![WorldGltf.Key]!["texture"] = "rock";
        root["samplers"] = new JsonArray(new JsonObject { ["wrapS"] = 33071, ["wrapT"] = 10497 }); root["textures"]![0]!["sampler"] = 0;
        fixture.Write(model, root.ToJsonString());
        var report = await SourceBuilder.CheckAsync(fixture.Project, ["m2/rtexture16.zbd"], token: Token);
        Assert.Contains(report.Outputs, o => o.Error?.Contains("edge modes", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ATextureOnlyExportFailsWhenItsWorldCannotAssemble()
    {
        using SourceWorldFixture fixture = new(); fixture.Write("data/m1/models/m1.gltf", "invalid JSON");
        string destination = Path.Combine(fixture.Root, "export");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture16.zbd"], token: Token));
        Assert.Contains("world does not assemble", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    private static List<WorldNode> Import(GltfDocument doc) => WorldGltf.Import(doc, "model.gltf", 255,
        new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (uri, name, _) => name ?? Path.GetFileNameWithoutExtension(uri) });
    private static GltfDocument Model(params GltfMaterial[] materials)
    {
        GltfMesh mesh = new();
        foreach (var material in materials)
        {
            GltfPrimitive primitive = new() { Material = material };
            primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitY]); primitive.Indices.AddRange([0, 1, 2]);
            primitive.TexCoords.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]); mesh.Primitives.Add(primitive);
        }
        GltfDocument doc = new(); doc.Roots.Add(new() { Name = "surface", Mesh = mesh }); return doc;
    }
    private static DecodedImage Image(int width, int height, byte red)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = red; rgba[i + 3] = 255; }
        return new(width, height, rgba);
    }

    [Fact]
    public void SharedSoftwarePalettesKeepOpaqueBlackBesideBrightColours()
    {
        byte[] bytes = TexturePackBuilder.Build([new("black", "a", Image(8, 8, 0)), new("red", "b", Image(8, 8, 255))],
            TexturePackVariant.FromFileName("texturemax.zbd")!, Token).Bytes;
        var doc = FormatRegistry.Default.OpenBytes("texturemax.zbd", bytes, token: Token);
        var black = TextureDecoder.Decode(doc, doc.Assets[0], Token);
        Assert.All(black.Rgba.Chunk(4), p => { Assert.InRange(p[0], (byte)0, (byte)8); Assert.InRange(p[1], (byte)0, (byte)8); Assert.InRange(p[2], (byte)0, (byte)8); Assert.Equal(255, p[3]); });
        Assert.Equal(255, TextureDecoder.Decode(doc, doc.Assets[1], Token).Rgba[0]);
    }
}
