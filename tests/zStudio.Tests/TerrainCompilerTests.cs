using System.IO;
using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Terrain;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>The terrain splitter: cuts at cell lines and region outlines, attributes by layer, no T-junctions, model limits, determinism.</summary>
public sealed class TerrainCompilerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    /// <summary>A 512 × 512 world of 256-unit cells, origin (0, 512), rows toward −z.</summary>
    private static readonly TerrainGrid Grid = new(0, 512, 512, -512, 256, -256, 2, 2);
    private static readonly TerrainMaterialInfo[] Materials = [new(0xFFFFFF00)];

    /// <summary>A flat grid of <paramref name="n"/> × <paramref name="n"/> quads over [x0, x1] × [z0, z1] at height 0, two triangles each.</summary>
    private static TerrainSurfaceGeometry Sheet(string id, float x0, float z0, float x1, float z1, int n = 1, float y = 0)
    {
        List<TerrainFace> triangles = [];
        TerrainCorner C(int i, int j) { float u = i / (float)n, v = j / (float)n; return new(new(x0 + (x1 - x0) * u, y, z0 + (z1 - z0) * v), Vector3.UnitY, new(u, v)); }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                triangles.Add(new(0, C(i, j), C(i + 1, j), C(i + 1, j + 1)));
                triangles.Add(new(0, C(i, j), C(i + 1, j + 1), C(i, j + 1)));
            }
        return new(new(id, "surfaces.gltf", id, TerrainAttributes.None), triangles);
    }
    private static TerrainRecipe Recipe(TerrainAttributes defaults, params TerrainRegion[] regions) =>
        new(1, [new("land", "surfaces.gltf", "land", TerrainAttributes.None)], defaults, regions);
    private static TerrainShape Square(float x0, float z0, float x1, float z1) => new([new([new(x0, z0), new(x1, z0), new(x1, z1), new(x0, z1)], [])]);
    private static float Area(TerrainPolygonOutput p)
    {
        Vector3 sum = Vector3.Zero;
        for (int i = 1; i + 1 < p.Corners.Length; i++) sum += Vector3.Cross(p.Corners[i].Position - p.Corners[0].Position, p.Corners[i + 1].Position - p.Corners[0].Position);
        return sum.Length() / 2;
    }
    /// <summary>No corner of any polygon lies inside another polygon's edge (a T-junction).</summary>
    private static void NoJunctions(IReadOnlyList<TerrainPiece> pieces)
    {
        var corners = pieces.SelectMany(p => p.Polygons).SelectMany(p => p.Corners).Select(c => c.Position).Distinct().ToArray();
        foreach (var polygon in pieces.SelectMany(p => p.Polygons))
            for (int i = 0; i < polygon.Corners.Length; i++)
            {
                Vector3 a = polygon.Corners[i].Position, b = polygon.Corners[(i + 1) % polygon.Corners.Length].Position, ab = b - a;
                foreach (var c in corners)
                {
                    if (Vector3.DistanceSquared(c, a) < 1e-6f || Vector3.DistanceSquared(c, b) < 1e-6f) continue;
                    float t = Vector3.Dot(c - a, ab) / ab.LengthSquared();
                    if (t <= 0 || t >= 1) continue;
                    Assert.False(Vector3.Distance(a + ab * t, c) < 1e-3f, $"{c} lies inside the edge {a}–{b}.");
                }
            }
    }

    [Fact]
    public void SurfacesAreCutExactlyAtCellLines()
    {
        var result = TerrainCompiler.Compile("t", Recipe(TerrainAttributes.None), [Sheet("land", 200, 200, 300, 300)], Materials, Grid, Token);
        Assert.Equal(["t_land_00x00", "t_land_01x00", "t_land_00x01", "t_land_01x01"], result.Pieces.Select(p => p.Name));
        foreach (var piece in result.Pieces)
        {
            // Every corner lies inside the piece's cell, the cell lines included.
            float minX = piece.Column * 256f, maxX = minX + 256, maxZ = 512 - piece.Row * 256f, minZ = maxZ - 256;
            Assert.All(piece.Polygons.SelectMany(p => p.Corners), c => Assert.True(c.Position.X >= minX && c.Position.X <= maxX && c.Position.Z >= minZ && c.Position.Z <= maxZ, $"{c.Position} is outside cell {piece.Column},{piece.Row}"));
        }
        Assert.Equal(100f * 100f, result.Pieces.SelectMany(p => p.Polygons).Sum(Area), 0.01f);
        NoJunctions(result.Pieces);
    }

    [Fact]
    public void RegionsPaintTheirShapeAndSeparateNodesByNodeAttributes()
    {
        var recipe = Recipe(new() { Zones = new([1]), Craters = TerrainCraters.Allowed },
            new TerrainRegion("road", [], Square(240, 240, 260, 260), new() { Craters = TerrainCraters.Blocked, Zones = new([1, 2]) }),
            new TerrainRegion("pit", [], Square(210, 210, 220, 220), new() { Soil = 3 }));
        var result = TerrainCompiler.Compile("t", recipe, [Sheet("land", 200, 200, 300, 300)], Materials, Grid, Token);
        var road = result.Pieces.Where(p => (p.CarriedFlags & 0x20000) != 0).ToArray();
        var land = result.Pieces.Where(p => (p.CarriedFlags & 0x20000) == 0).ToArray();
        // The road straddles both cell lines: four ClipTo pieces of a transition zone (any, gate on), 400 units in all.
        Assert.Equal(4, road.Length);
        Assert.All(road, p => { Assert.Equal(0xFF, p.Zone); Assert.NotEqual(0u, p.CarriedFlags & 0x01000000); Assert.Equal(0u, p.CarriedFlags & 0x10000); });
        Assert.All(road.SelectMany(p => p.Polygons), p => Assert.Equal(0xFF020102u, p.ZoneWord));
        Assert.Equal(400f, road.SelectMany(p => p.Polygons).Sum(Area), 0.01f);
        // The rest is craters-allowed land in zone 1, and the pit's polygons have quicksand soil without a node of their own.
        Assert.All(land, p => { Assert.Equal(1, p.Zone); Assert.NotEqual(0u, p.CarriedFlags & 0x10000); });
        Assert.Equal(100f, land.SelectMany(p => p.Polygons).Where(p => p.Soil == 3).Sum(Area), 0.01f);
        Assert.Equal(10000f - 400f, land.SelectMany(p => p.Polygons).Sum(Area), 0.01f);
        NoJunctions(result.Pieces);
    }

    [Fact]
    public void LargePiecesAreDividedBelowTheEngineLimits()
    {
        // 40 × 40 quads in one cell: 1,681 vertices, twice what one model may hold.
        var result = TerrainCompiler.Compile("t", Recipe(TerrainAttributes.None), [Sheet("land", 10, 500, 240, 270, 40)], Materials, Grid, Token);
        Assert.True(result.Pieces.Count >= 2);
        Assert.All(result.Pieces, p =>
        {
            Assert.Equal((0, 0), (p.Column, p.Row));
            Assert.True(p.Polygons.SelectMany(q => q.Corners).Select(c => c.Position).Distinct().Count() <= TerrainCompiler.VertexBudget);
        });
        Assert.Equal(3200, result.Pieces.Sum(p => p.Polygons.Count));
    }

    [Fact]
    public void TheSameInputsGiveTheSamePieces()
    {
        var recipe = Recipe(TerrainAttributes.None, new TerrainRegion("blob", [], new([new([new(230, 230), new(280, 240), new(270, 290), new(235, 270)], [[new(250, 250), new(260, 250), new(255, 260)]])]), new() { Zones = new([3]) }));
        string Fingerprint() => string.Join("|", TerrainCompiler.Compile("t", recipe, [Sheet("land", 200, 200, 300, 300, 8)], Materials, Grid, Token).Pieces
            .Select(p => p.Name + ":" + string.Join(";", p.Polygons.Select(q => q.ZoneWord + "/" + string.Join(",", q.Corners.Select(c => c.Position))))));
        Assert.Equal(Fingerprint(), Fingerprint());
        var result = TerrainCompiler.Compile("t", recipe, [Sheet("land", 200, 200, 300, 300, 8)], Materials, Grid, Token);
        NoJunctions(result.Pieces);
        Assert.Equal(10000f, result.Pieces.SelectMany(p => p.Polygons).Sum(Area), 0.05f);
    }

    [Fact]
    public void StackedSurfacesStaySeparateAndHeightRangesSelectTheirSheet()
    {
        // A floor at y 0 and a ceiling at y 20 over the same ground: a region limited to y ≥ 10 paints only the ceiling.
        var recipe = new TerrainRecipe(1, [new("floor", "s.gltf", "floor", TerrainAttributes.None), new("ceiling", "s.gltf", "ceiling", TerrainAttributes.None)], TerrainAttributes.None,
            [new("roof", [], Square(0, 300, 100, 400) with { MinY = 10 }, new() { Standable = false })]);
        var result = TerrainCompiler.Compile("t", recipe, [Sheet("floor", 0, 300, 100, 400), Sheet("ceiling", 0, 300, 100, 400, y: 20) with { Surface = recipe.Surfaces[1] }], Materials, Grid, Token);
        Assert.Equal(2, result.Pieces.Count);
        Assert.NotEqual(0u, result.Pieces.Single(p => p.Surface == 0).CarriedFlags & 0x08);
        Assert.Equal(0u, result.Pieces.Single(p => p.Surface == 1).CarriedFlags & 0x08);
    }

    [Fact]
    public void RecipesRoundTripAndRejectMistakes()
    {
        const string text = """
            { "format": "recoil-terrain", "version": 1, "compiler": 1,
              "surfaces": [ { "id": "ground", "model": "coast.gltf", "node": "ground", "defaults": { "craters": "allowed" } },
                            { "id": "sea_floor", "model": "../shared/sea.gltf", "node": "floor", "defaults": { "soil": "seafloor" } } ],
              "defaults": { "zones": [0], "nodeZone": "auto", "collision": true },
              "regions": [ { "name": "tunnel mouth", "surfaces": ["ground"], "shape": { "plane": "xz", "minY": -5, "polygons": [ { "outer": [[1200, 2050], [1260, 2050], [1260, 2110]], "holes": [] } ] }, "set": { "zones": [0, 4], "flags": "0x01000018" } },
                           { "name": "everywhere", "set": { "nodeZone": "any", "priority": 2 } } ] }
            """;
        var recipe = TerrainRecipe.Parse(Encoding.UTF8.GetBytes(text), "coast.terrain.json");
        Assert.Equal(2, recipe.Surfaces.Count); Assert.Equal(2u, recipe.Surfaces[1].Defaults.Soil);
        Assert.Equal(0xFF040002u, recipe.Regions[0].Set.Zones!.Word);
        Assert.Equal(TerrainAttributes.AnyZone, recipe.Regions[1].Set.NodeZone);
        var written = recipe.Write();
        var again = TerrainRecipe.Parse(written, "again");
        Assert.Equal(written, again.Write());
        Assert.Equal(-5f, again.Regions[0].Shape!.MinY);
        foreach (string bad in new[]
        {
            text.Replace("\"compiler\": 1", "\"compiler\": 2"), text.Replace("\"zones\": [0, 4]", "\"zones\": [0, 4, 5, 6]"), text.Replace("seafloor", "mud"),
            text.Replace("\"surfaces\": [\"ground\"]", "\"surfaces\": [\"rock\"]"), text.Replace("[1260, 2110]", "[1260, \"x\"]"), text.Replace("coast.gltf", "C:/coast.gltf"),
            text.Replace("\"craters\": \"allowed\"", "\"craters\": \"Allowed\""), text.Replace("\"priority\": 2", "\"colour\": 2"), text.Replace("\"id\": \"sea_floor\"", "\"id\": \"ground\""),
        })
            Assert.Contains("coast.terrain.json", Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Encoding.UTF8.GetBytes(bad), "coast.terrain.json")).Message);
    }
}
