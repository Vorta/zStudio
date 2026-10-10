using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainSharedMaterialNormalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly Vector3 AuthoredNormal = Vector3.Normalize(new(1, 2, 1));

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, true, null)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ReaderBackedTerrainKeepsEachPrimitivesNormalPolicy(bool smoothFirst, bool separateSurfaces, bool? explicitNormals)
    {
        // Explicit nonmetallic material: omitting a material in a disk glTF means metallicFactor 1 and is refused.
        GltfMaterial material = new() { MetallicFactor = 0 };
        if (explicitNormals is { } policy) material.Extras = new() { [WorldGltf.Key] = new JsonObject { ["normals"] = policy } };
        GltfPrimitive flat = Triangle(material, 0, false), smooth = Triangle(material, 2, true);
        GltfDocument document = new();
        if (separateSurfaces)
        {
            GltfMesh flatMesh = new(), smoothMesh = new();
            flatMesh.Primitives.Add(flat); smoothMesh.Primitives.Add(smooth);
            document.Roots.Add(new() { Name = "flat", Mesh = flatMesh });
            document.Roots.Add(new() { Name = "smooth", Mesh = smoothMesh });
        }
        else
        {
            GltfMesh mesh = new(); mesh.Primitives.AddRange(smoothFirst ? [smooth, flat] : [flat, smooth]);
            document.Roots.Add(new() { Name = "both", Mesh = mesh });
        }
        var (json, binary) = document.Write("surface.bin", Token);
        var parsed = GltfDocument.Read(json, _ => binary, Token);
        var parsedPrimitives = parsed.AllNodes().SelectMany(n => n.Mesh!.Primitives).ToArray();
        Assert.Same(parsedPrimitives[0].Material, parsedPrimitives[1].Material);
        Assert.Equal(0, parsedPrimitives[0].Material!.MetallicFactor);
        string[] names = separateSurfaces ? smoothFirst ? ["smooth", "flat"] : ["flat", "smooth"] : ["both"];
        TerrainRecipe recipe = new(1, [.. names.Select(n => new TerrainSurface(n, "surface.gltf", n, TerrainAttributes.None))], TerrainAttributes.None, []);
        GameZWorld world = new();
        WorldGltf.ImportContext context = new()
        {
            World = world, Reference = (_, _) => (parsed, "surface.gltf"),
            ReadFile = (_, _) => (recipe.Write(), "surface.terrain.json"), TextureName = (_, _, _) => "unused",
            Grid = () => new(0, 4, 8, -4, 8, -4, 1, 1), Token = Token,
        };
        world.Nodes.AddRange(WorldGltf.ImportTerrain("surface.terrain.json", "database.gltf", context, 100));
        var compiled = FormatRegistry.Default.OpenBytes("world.zbd", GameZWriter.Write(world, Token), token: Token);
        Assert.Empty(compiled.Diagnostics);
        var read = GameZWorldReader.FromDocument(compiled, Token);
        var polygons = read.Models.SelectMany(m => m.Polygons.Select(p => (Model: m, Polygon: p))).ToArray();
        Assert.Equal(2, polygons.Length);
        Assert.Single(read.Materials); // Different normal storage is a polygon property, not a new engine material.
        foreach (var (model, polygon) in polygons)
        {
            bool authored = polygon.Vertices.All(v => model.Vertices[v].X >= 2);
            bool expectedNormals = explicitNormals ?? authored;
            Assert.Equal(expectedNormals ? 3 : 0, polygon.Normals.Length);
            foreach (int index in polygon.Normals)
                Assert.InRange(Vector3.Distance(authored ? AuthoredNormal : Vector3.UnitY, model.Normals[index]), 0, 2e-6f);
        }
    }

    private static GltfPrimitive Triangle(GltfMaterial material, float x, bool smooth)
    {
        GltfPrimitive primitive = new() { Material = material };
        primitive.Positions.AddRange([new(x, 0, 0), new(x, 0, 1), new(x + 1, 0, 0)]);
        primitive.Indices.AddRange([0, 1, 2]);
        if (smooth) primitive.Normals.AddRange([AuthoredNormal, AuthoredNormal, AuthoredNormal]);
        return primitive;
    }
}
