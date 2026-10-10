using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23TerrainTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegionSelectionSpendsWorkEvenWhenSurfacesHaveNoGeometry(bool selectAll)
    {
        var (recipe, geometry) = EmptySurfaces(128, 256, selectAll);
        var grid = new TerrainGrid(0, 10, 10, -10, 10, -10, 1, 1);
        Assert.Contains("work limit", Assert.Throws<InvalidDataException>(() =>
            TerrainCompiler.Compile("empty", recipe, geometry, [], grid, Token, 1000, 10_000)).Message);
        Assert.Empty(TerrainCompiler.Compile("empty", recipe, geometry, [], grid, Token, 1000, 100_000).Pieces);
    }

    [Fact]
    public void RegionSelectionDoesNotAllocateASurfaceListForEveryRequestedId()
    {
        var grid = new TerrainGrid(0, 10, 10, -10, 10, -10, 1, 1);
        var (warmRecipe, warmGeometry) = EmptySurfaces(2, 2, false);
        TerrainCompiler.Compile("warm", warmRecipe, warmGeometry, [], grid, Token);
        var (recipe, geometry) = EmptySurfaces(128, 256, false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var compiled = TerrainCompiler.Compile("empty", recipe, geometry, [], grid, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(compiled.Pieces);
        // This bounded fixture used to allocate at least 32 MiB of copied surface references alone.
        Assert.InRange(allocated, 0, 4 * 1024 * 1024);
    }

    private static (TerrainRecipe Recipe, TerrainSurfaceGeometry[] Geometry) EmptySurfaces(int surfaceCount, int regionCount, bool selectAll)
    {
        var surfaces = Enumerable.Range(0, surfaceCount).Select(i =>
            new TerrainSurface($"s{i}", "surface.gltf", $"s{i}", TerrainAttributes.None)).ToArray();
        string[] ids = selectAll ? [] : surfaces.Select(s => s.Id).ToArray();
        var regions = Enumerable.Range(0, regionCount).Select(i =>
            new TerrainRegion($"r{i}", ids, null, new() { Soil = (uint)(i % 6) })).ToArray();
        return (new(1, surfaces, TerrainAttributes.None, regions),
            surfaces.Select(s => new TerrainSurfaceGeometry(s, [])).ToArray());
    }

    [Theory]
    [InlineData(57)]
    [InlineData(58)]
    [InlineData(60)]
    public void RecordedFansBeyondOneEnginePolygonKeepTheirTerrainGeometry(int corners)
    {
        GltfPrimitive primitive = new() { Material = new(), Extras = new()
        {
            [WorldGltf.Key] = new JsonObject { ["polygons"] = new JsonArray(corners - 2) }
        } };
        for (int i = 0; i < corners; i++)
        {
            double angle = i * 2 * Math.PI / corners;
            primitive.Positions.Add(new(5 + (float)Math.Cos(angle), 0, 5 + (float)Math.Sin(angle)));
        }
        for (int i = 1; i + 1 < corners; i++) primitive.Indices.AddRange([0, i, i + 1]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument doc = new(); doc.Roots.Add(new() { Name = "surface", Mesh = mesh });
        TerrainRecipe recipe = new(1, [new("surface", "surface.gltf", "surface", TerrainAttributes.None)], TerrainAttributes.None, []);
        WorldGltf.ImportContext context = new()
        {
            World = new(), Reference = (_, _) => (doc, "surface.gltf"), ReadFile = (_, _) => (recipe.Write(), "surface.terrain.json"),
            TextureName = (_, _, _) => "texture", Grid = () => new(0, 10, 10, -10, 10, -10, 1, 1), Token = Token
        };
        var pieces = WorldGltf.ImportTerrain("surface.terrain.json", "database.gltf", context, 10000);
        Assert.NotEmpty(pieces);
        // Independent area check: all fan triangles together must retain the authored regular polygon's area.
        double expected = corners * Math.Sin(2 * Math.PI / corners) / 2;
        double actual = 0;
        foreach (var piece in pieces)
        {
            var model = piece.Model!;
            foreach (var polygon in model.Polygons)
                for (int i = 1; i + 1 < polygon.Vertices.Length; i++)
                {
                    var a = model.Vertices[polygon.Vertices[0]];
                    var b = model.Vertices[polygon.Vertices[i]];
                    var c = model.Vertices[polygon.Vertices[i + 1]];
                    actual += Math.Abs((b.X - a.X) * (double)(c.Z - a.Z) - (b.Z - a.Z) * (double)(c.X - a.X)) / 2;
                }
        }
        Assert.InRange(Math.Abs(actual - expected), 0, 1e-4);
    }
}
