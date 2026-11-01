using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class SharedTextureSourceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Frames = ["prop02", "prop03", "prop04", "caust02", "caust03", "caust04", "caust05"];

    // Shared models must keep map overrides and script-only frames through preview, planning and both pack families.
    [Fact]
    public async Task SharedModelsAndScriptFramesUseTheSameMapSourcesEverywhere()
    {
        using SourceWorldFixture f = new();
        var builder = new ModelBuilder();
        foreach (string name in new[] { "prop01", "caust01" })
            builder.Add(new([new(0,0,0),new(1,0,0),new(0,0,1)], [new(0,0),new(1,0),new(0,1)], [], [], new() { Texture = new(name), Flags = 0x1FF }));
        var (json, bin) = WorldGltf.Export([new("shared", WorldNodeClass.Object3D) { Model = builder.Finish() }], 0xFF,
            new() { Texture = t => ($"../../textures/bft/{t.Name}.png", 0) }).Write("bft.bin", Token);
        f.Write("data/common/models/bft/bft.gltf", json); f.Write("data/common/models/bft/bft.bin", bin);
        foreach (string name in new[] { "prop01", "caust01" }.Concat(Frames))
        {
            f.Write($"data/common/textures/bft/{name}.png", Png(Frames.Contains(name) ? 1024 : 8, 0, 255, 0));
            // MissionTextures inventories this default first; the configured search order selects another source.
            f.Write($"data/common/textures/{name}.png", Png(16, 255, 0, 0));
        }
        f.Write("data/m1/textures/bft/prop01.png", Png(1024, 0, 0, 255, 128));
        f.Write("data/m1/textures/bft/prop02.png", Png(1024, 0, 0, 255));
        f.Write("data/common/textures/bft/pre.anim.png", Png(8, 0, 255, 0));
        f.Write("data/common/textures/bft/fire.anim.png", Png(8, 0, 255, 0));
        foreach (string mission in new[] { "m1", "m2" })
            f.Write($"gamegen/{mission}.gs", $"""
                SetTextureDirectory ../data/common/textures
                SetTextureDirectory ../data/common/textures/bft
                SetTextureDirectory ../data/{mission}/textures/bft
                SetTextureDirectory ../data/common/textures
                SetModelDirectory ../data/common/models/bft
                NewWorld world
                GameGenSetWorld
                LoadGameGen bft.gltf bft
                TextureAdd caust01.png
                LensFlareTexture 0 prop01.png
                TextureAdd pre.anim.png
                GameZWriteZBDFile gamez.zbd
                FindNode shared
                CycleTextureSetOn 9
                CycleTextureSetMap prop01.png
                {string.Join('\n', Frames.Select(n => $"CycleTextureSetMap {n}.png"))}
                CycleTextureSetMap caust01.png
                WriteTextureSetMap caust02.png
                TextureAdd prop03.png
                LensFlareTexture 0 prop04.png
                CycleTextureSetOn 1
                CycleTextureSetMap fire.anim.png
                """);

        var m1 = await SourceWorlds.LoadSceneAsync(f.Project, "m1", token: Token);
        var m2 = await SourceWorlds.LoadSceneAsync(f.Project, "m2", token: Token);
        foreach (string name in new[] { "prop01", "caust01" }.Concat(Frames))
        {
            string common = $"data/common/textures/bft/{name}.png";
            Assert.Equal(common, m2.TextureFiles[name]);
            Assert.Equal(name is "prop01" or "prop02" ? $"data/m1/textures/bft/{name}.png" : common, m1.TextureFiles[name]);
            Assert.Contains(m1.TextureFiles[name], m1.Dependencies);
            Assert.Equal(await File.ReadAllBytesAsync(f.Path(m1.TextureFiles[name]), Token), m1.Textures[name].Bytes);
        }
        Assert.Contains("data/m2/textures/bft/prop02.png", m2.MissingInputs);
        Assert.Equal(["prop01", "caust01", "pre"], m1.World.Textures.Select(t => t.Name));
        Assert.Equal("data/common/textures/bft/pre.anim.png", m1.TextureFiles["pre"]);
        Assert.Equal("data/common/textures/bft/fire.anim.png", m1.TextureFiles["fire"]);
        var plan = SourceBuilder.Plan(f.Project, token: Token);
        Assert.Contains(plan, p => p.Path == "m1/rtexture32.zbd" && p.Automatic);
        Assert.DoesNotContain(plan, p => p.Path.StartsWith("m2/", StringComparison.Ordinal) && p.Automatic);
        string[] outputs = ["m1/rtexture32.zbd", "m1/texturemax.zbd", "m2/rtexture16.zbd", "m2/texturemax.zbd"];
        var check = await SourceBuilder.CheckAsync(f.Project, outputs, token: Token);
        Assert.Equal(0, check.Failed);
        string destination = Path.Combine(f.Root, "packs");
        var report = await SourceBuilder.ExportAsync(f.Project, destination, outputs, token: Token);
        Assert.Equal(4, report.Built);
        foreach (string output in outputs)
        {
            var doc = FormatRegistry.Default.OpenBytes(output, await File.ReadAllBytesAsync(Path.Combine(destination, output), Token), token: Token);
            Assert.Contains(doc.Assets, a => a.Name == "pre"); Assert.Contains(doc.Assets, a => a.Name == "fire");
            foreach (string name in new[] { "prop01", "caust01" }.Concat(Frames))
            {
                var asset = Assert.Single(doc.Assets, a => a.Name == name);
                var info = (TextureInfo)asset.Content!;
                bool local = output.StartsWith("m1/", StringComparison.Ordinal) && name is "prop01" or "prop02";
                Assert.Equal(local || Frames.Contains(name) ? 1024 : 8, info.Width);
                var rgba = TextureDecoder.Decode(doc, asset, Token).Rgba;
                Assert.Equal(local ? (byte)255 : (byte)0, rgba[2]);
                Assert.Equal(local ? (byte)0 : (byte)255, rgba[1]);
            }
        }
        SourceBuilder.Snapshot accepted1 = new(f.Project), accepted2 = new(f.Project);
        _ = accepted1.World("m1", Token); _ = accepted2.World("m2", Token);
        File.Delete(f.Path("data/m1/textures/bft/prop01.png"));
        File.Delete(f.Path("data/m1/textures/bft/prop02.png"));
        Assert.Throws<InvalidDataException>(() => accepted1.CheckUnchanged(Token));
        var fallback = await SourceWorlds.LoadSceneAsync(f.Project, "m1", token: Token);
        Assert.Equal("data/common/textures/bft/prop01.png", fallback.TextureFiles["prop01"]);
        Assert.Equal("data/common/textures/bft/prop02.png", fallback.TextureFiles["prop02"]);
        Assert.DoesNotContain(SourceBuilder.Plan(f.Project, token: Token), p => p.Path.StartsWith("m1/", StringComparison.Ordinal) && p.Automatic);
        f.Write("data/m2/textures/bft/prop02.png", Png(32, 0, 0, 255));
        Assert.Throws<InvalidDataException>(() => accepted2.CheckUnchanged(Token));
        // No predefined inventory at all: a world using only configured subfolders still has packs to build.
        foreach (string name in new[] { "prop01", "caust01" }.Concat(Frames)) File.Delete(f.Path($"data/common/textures/{name}.png"));
        File.Delete(f.Path("data/m1/textures/rock.png"));
        Assert.Contains(SourceBuilder.Plan(f.Project, token: Token), p => p.Path == "m1/rtexture16.zbd");
        var subfoldersOnly = await SourceBuilder.CheckAsync(f.Project, ["m1/rtexture16.zbd"], token: Token);
        Assert.Equal(1, subfoldersOnly.Built);
        // A script reusing a model's URI-only source must not claim the valid texture is missing.
        f.Write("gamegen/m1.gs", (await File.ReadAllTextAsync(f.Path("gamegen/m1.gs"), Token))
            .Replace("SetTextureDirectory ../data/common/textures/bft", "", StringComparison.Ordinal));
        var uriOnly = await SourceWorlds.LoadSceneAsync(f.Project, "m1", token: Token);
        Assert.Equal("data/common/textures/bft/prop01.png", uriOnly.TextureFiles["prop01"]);
        Assert.DoesNotContain(uriOnly.Notes, n => n.Contains("texture prop01 has no PNG", StringComparison.Ordinal));
    }

    private static byte[] Png(int side, byte r, byte g, byte b, byte alpha = 255)
    {
        byte[] bytes = new byte[side * side * 4];
        for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = r; bytes[i+1] = g; bytes[i+2] = b; bytes[i+3] = alpha; }
        return PngEncoder.Encode(new(side, side, bytes), Token);
    }
}
