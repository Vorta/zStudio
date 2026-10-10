using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldTextureCapacityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScriptTextureRegistrationStopsAtCapacityButCanReuseAnExistingName(bool overflow)
    {
        string repeated = string.Concat(Enumerable.Repeat("TextureAdd T4095\nCycleTextureSetMap T4095\nWriteTextureSetMap T4095\nLensFlareTexture T4095\n", overflow ? 1 : 25000));
        string script = string.Concat(Enumerable.Range(0, 4096).Select(i => $"TextureAdd t{i:0000}\n"))
            + repeated + (overflow ? "TextureAdd extra\n" : "") + "GameZWriteZBDFile gamez.zbd\nTextureAdd late_only\n";
        var assembler = new WorldAssembler(new Files(script), Token);
        if (overflow)
            Assert.Contains("4,096", Assert.Throws<InvalidDataException>(() => assembler.Assemble("m1.gs")).Message);
        else
        {
            Assert.Same(assembler.World, assembler.Assemble("m1.gs"));
            VerifyWritten(assembler.World);
        }
        Assert.Equal(4096, assembler.World.Textures.Count);
        Assert.Single(assembler.World.Textures, t => t.Name == "t0000");
        Assert.Single(assembler.World.Textures, t => t.Name == "t4095");
        Assert.DoesNotContain(assembler.World.Textures, t => t.Name == "extra");
        Assert.DoesNotContain("extra", assembler.ScriptTextures);
        Assert.Equal(!overflow, assembler.ScriptTextures.Contains("late_only"));
        Assert.DoesNotContain(assembler.World.Textures, t => t.Name == "late_only");
    }

    [Fact]
    public void ScriptCanRegisterAndRepeatedlyReuseATextureImportedAtCapacity()
    {
        GltfPrimitive primitive = new() { Material = new() { ImageUri = "imported.png" } };
        primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]);
        primitive.TexCoords.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]);
        primitive.Indices.AddRange([0, 1, 2]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument document = new(); document.Roots.Add(new() { Name = "surface", Mesh = mesh });
        var bytes = document.Write("surface.bin", Token);
        string script = "SetModelDirectory ../data/models\n"
            + string.Concat(Enumerable.Range(0, 4095).Select(i => $"TextureAdd t{i:0000}\n"))
            + "LoadGameGen surface.gltf root\n"
            + string.Concat(Enumerable.Repeat("TextureAdd IMPORTED\n", 1024))
            + "GameZWriteZBDFile gamez.zbd\nCycleTextureSetMap late_only\n";
        var assembler = new WorldAssembler(new Files(script, new()
        {
            ["data/models/surface.gltf"] = bytes.Json,
            ["data/models/surface.bin"] = bytes.Binary,
        }), Token);
        var world = assembler.Assemble("m1.gs");
        Assert.Equal(4096, world.Textures.Count);
        var imported = Assert.Single(world.Textures, t => t.Name == "imported");
        Assert.Same(imported, Assert.Single(Assert.Single(world.Models).Polygons).Material!.Texture);
        Assert.Contains("imported", assembler.ScriptTextures);
        Assert.Contains("late_only", assembler.ScriptTextures);
        Assert.DoesNotContain(world.Textures, t => t.Name == "late_only");
        VerifyWritten(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsedMaterialsStopBeforeAddingAnExcessTextureAndReuseNamesAtCapacity(bool overflow)
    {
        GltfDocument source = new();
        GltfMesh mesh = new();
        for (int i = 0; i <= 4096; i++)
        {
            string name = i == 4096 ? overflow ? "extra" : "T0000" : $"t{i:0000}";
            GltfPrimitive primitive = new() { Material = new() { ImageUri = name + ".png" } };
            primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]);
            primitive.TexCoords.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]);
            primitive.Indices.AddRange([0, 1, 2]);
            mesh.Primitives.Add(primitive);
        }
        source.Roots.Add(new() { Name = "surface", Mesh = mesh });
        var bytes = source.Write("surface.bin", Token);
        var document = GltfDocument.Read(bytes.Json, _ => bytes.Binary, Token);
        int textureRequests = 0;
        WorldGltf.ImportContext context = new()
        {
            World = new(), Reference = (_, _) => throw new InvalidOperationException(), Token = Token,
            TextureName = (uri, _, _) => { textureRequests++; return Path.GetFileNameWithoutExtension(uri); },
        };
        if (overflow)
        {
            Assert.Contains("4,096", Assert.Throws<InvalidDataException>(() => WorldGltf.Import(document, "surface.gltf", 0, context)).Message);
            Assert.Empty(context.World.Models);
        }
        else
        {
            context.World.Nodes.AddRange(WorldGltf.Import(document, "surface.gltf", 0, context));
            var model = Assert.Single(context.World.Models);
            Assert.Same(model.Polygons[0].Material!.Texture, model.Polygons[^1].Material!.Texture);
            VerifyWritten(context.World);
        }
        Assert.Equal(4097, textureRequests);
        Assert.Equal(4096, context.World.Textures.Count);
        Assert.Equal(4096, context.World.Materials.Count);
        Assert.DoesNotContain(context.World.Textures, t => t.Name == "extra");
    }

    private static void VerifyWritten(GameZWorld world)
    {
        world.NodeCapacity = world.Nodes.Count;
        world.FreeHead = null;
        world.ModelCapacity = world.Models.Count;
        world.MaterialCapacity = world.Materials.Count;
        var document = FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(world, Token), token: Token);
        Assert.Empty(document.Diagnostics);
        Assert.Equal(4096, GameZWorldReader.FromDocument(document, Token).Textures.Count);
    }

    private sealed class Files(string script, Dictionary<string, byte[]>? assets = null) : IProjectFiles
    {
        private readonly byte[] bytes = Encoding.ASCII.GetBytes(script);
        public bool Exists(string relative) => relative == "gamegen/m1.gs" || assets?.ContainsKey(relative) == true;
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = relative == "gamegen/m1.gs" ? bytes : assets![relative]; limits.Validate(result); return result; }
    }
}
