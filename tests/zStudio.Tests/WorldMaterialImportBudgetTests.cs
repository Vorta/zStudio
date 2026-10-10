using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldMaterialImportBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("flags", true)]
    [InlineData("packedColor", true)]
    [InlineData("zone", true)]
    [InlineData("priority", false)]
    [InlineData("opacity", false)]
    [InlineData("soil", false)]
    [InlineData("backface", false)]
    [InlineData("normals", false)]
    [InlineData("fields", false)]
    [InlineData("color", false)]
    public void ColdLongNumericRepresentationsAreRefusedBeforeImportAndPermitValidReuse(string field, bool hex)
    {
        string operand = hex ? "\"0x" + new string('0', 4096) + "200\"" : "0." + new string('0', 4096);
        JsonNode value = JsonNode.Parse(operand)!;
        JsonObject extras = new() { [field] = field is "fields" or "color" ? new JsonArray(value, 0, 0) : value };
        var input = Read(Model(2, extras));
        var context = Context();
        var error = Assert.Throws<InvalidDataException>(() => WorldGltf.Import(input, "model.gltf", 255, context));
        Assert.Contains("representation limit", error.Message);
        Assert.True(error.Message.Length < 300);
        Assert.Empty(context.World.Materials); Assert.Empty(context.World.Models); Assert.Equal(0, context.Created);

        var valid = Read(Model(2, new() { ["flags"] = "0x0200", ["priority"] = JsonNode.Parse("3.0e0") }));
        var node = Assert.Single(WorldGltf.Import(valid, "valid.gltf", 255, context));
        Assert.Equal(2, node.Model!.Polygons.Count);
        Assert.All(node.Model.Polygons, p => Assert.Equal(3, p.Priority));
        Assert.Equal((ushort)0x2FF, Assert.Single(context.World.Materials).Flags);
    }

    [Theory]
    [InlineData("flags", true)]
    [InlineData("priority", false)]
    [InlineData("fields", false)]
    public void TerrainUsesTheSameColdNumericAdmission(string field, bool hex)
    {
        var value = JsonNode.Parse(hex ? "\"0x" + new string('0', 4096) + "200\"" : "0." + new string('0', 4096))!;
        var input = Read(Model(2, new() { [field] = field == "fields" ? new JsonArray(value, 0, 0) : value }));
        TerrainRecipe recipe = new(1, [new("surface", "model.gltf", "surface", TerrainAttributes.None)], TerrainAttributes.None, []);
        var context = Context(input, recipe);
        var error = Assert.Throws<InvalidDataException>(() => WorldGltf.ImportTerrain("surface.terrain.json", "database.gltf", context, 100));
        Assert.Contains("representation limit", error.Message);
        Assert.Empty(context.World.Materials); Assert.Empty(context.World.Models);
    }

    [Fact]
    public void SharedMaterialMetadataDoesNotAllocatePerPrimitive()
    {
        const int count = 3000;
        var shortInput = Read(Model(count, new() { ["flags"] = "0x200", ["priority"] = 0 }));
        var paddedInput = Read(Model(count, new() { ["flags"] = "0x" + new string('0', 180) + "200", ["priority"] = JsonNode.Parse("0." + new string('0', 180)) }));
        _ = Measure(shortInput); _ = Measure(paddedInput); // Warm both scalar forms and nonempty geometry.
        long baseline = Measure(shortInput), padded = Measure(paddedInput);
        // Geometry is identical. One padded shared value may cost a small constant, not thousands of copies.
        Assert.True(padded - baseline < 128 * 1024, $"Padding one shared material added {padded - baseline:N0} bytes for {count} primitives.");

        static long Measure(GltfDocument input)
        {
            var context = Context();
            long start = GC.GetAllocatedBytesForCurrentThread();
            var node = Assert.Single(WorldGltf.Import(input, "model.gltf", 255, context));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(count, node.Model!.Polygons.Count);
            Assert.Equal((ushort)0x2FF, Assert.Single(context.World.Materials).Flags);
            return allocated;
        }
    }

    [Fact]
    public void TextureResolutionRetainsFullNamesAndSourcePathIdentity()
    {
        string name = "texture_" + new string('x', 2048), uri = "../images/" + new string('y', 2048) + ".png";
        var document = Model(50, new() { ["texture"] = name });
        var source = document.Roots[0].Mesh!.Primitives[0].Material!;
        source.ImageUri = uri;
        foreach (var p in document.Roots[0].Mesh!.Primitives) p.TexCoords.AddRange([Vector2.Zero, Vector2.UnitY, Vector2.UnitX]);
        var input = Read(document);
        List<string> resolvedFrom = [];
        GameZWorld world = new();
        WorldGltf.ImportContext context = new()
        {
            World = world, Reference = (_, _) => throw new InvalidOperationException(), Token = Token,
            TextureName = (image, identity, path) =>
            {
                Assert.Equal(uri, image); Assert.Equal(name, identity); resolvedFrom.Add(path);
                return path == "a/model.gltf" ? "first" : "second";
            },
        };
        var first = Assert.Single(WorldGltf.Import(input, "a/model.gltf", 255, context));
        var second = Assert.Single(WorldGltf.Import(input, "b/model.gltf", 255, context));
        Assert.Equal(new[] { "a/model.gltf", "b/model.gltf" }, resolvedFrom);
        Assert.All(first.Model!.Polygons, p => Assert.Equal("first", p.Material!.Texture!.Name));
        Assert.All(second.Model!.Polygons, p => Assert.Equal("second", p.Material!.Texture!.Name));
    }

    [Fact]
    public void CanceledMaterialImportDoesNotPublishIntoAnotherLoad()
    {
        var input = Read(Model(50, new() { ["flags"] = "0x200" }));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token); canceled.Cancel();
        var stopped = new WorldGltf.ImportContext
        {
            World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, _, _) => "unused", Token = canceled.Token,
        };
        Assert.Throws<OperationCanceledException>(() => WorldGltf.Import(input, "model.gltf", 255, stopped));
        Assert.Empty(stopped.World.Models); Assert.Empty(stopped.World.Materials); Assert.Empty(stopped.ImportedMaterials);
        var next = Context();
        Assert.Equal(50, Assert.Single(WorldGltf.Import(input, "model.gltf", 255, next)).Model!.Polygons.Count);
    }

    private static GltfDocument Model(int primitives, JsonObject engine)
    {
        GltfMaterial material = new() { MetallicFactor = 0, Extras = new() { [WorldGltf.Key] = engine } };
        GltfMesh mesh = new();
        for (int i = 0; i < primitives; i++)
        {
            GltfPrimitive p = new() { Material = material };
            p.Positions.AddRange([Vector3.Zero, Vector3.UnitZ, Vector3.UnitX]); p.Indices.AddRange([0, 1, 2]); mesh.Primitives.Add(p);
        }
        GltfDocument doc = new(); doc.Roots.Add(new() { Name = "surface", Mesh = mesh }); return doc;
    }

    private static GltfDocument Read(GltfDocument input)
    {
        var (json, binary) = input.Write("model.bin", Token);
        return GltfDocument.Read(json, _ => binary, Token);
    }

    private static WorldGltf.ImportContext Context(GltfDocument? document = null, TerrainRecipe? recipe = null) => new()
    {
        World = new(), Reference = (_, _) => (document!, "model.gltf"), TextureName = (_, _, _) => "unused", Token = Token,
        ReadFile = (_, _) => (recipe!.Write(), "surface.terrain.json"), Grid = () => new(0, 4, 4, -4, 4, -4, 1, 1),
    };
}
