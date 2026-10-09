using System.IO;
using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
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
    public void TheOutlineIndexAgreesWithRayCastingOverEveryRing()
    {
        // Two polygons (one with two holes), points on bucket lines, edges and the outline's maximum.
        TerrainShape shape = new([
            new([new(0, 0), new(100, 0), new(100, 80), new(60, 80), new(60, 40), new(0, 40)], [[new(10, 10), new(20, 10), new(20, 20), new(10, 20)], [new(70, 50), new(90, 50), new(80, 70)]]),
            new([new(120, 0), new(150, 30), new(120, 60)], [])], MinY: -5, MaxY: 50);
        var outline = new TerrainCompiler.Outline(shape);
        bool Brute(Vector3 p)
        {
            if (p.Y < -5 || p.Y > 50) return false;
            bool In(IReadOnlyList<Vector2> ring)
            {
                bool inside = false;
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                    if (ring[i].Y > p.Z != ring[j].Y > p.Z && p.X < (ring[j].X - ring[i].X) * (p.Z - (double)ring[i].Y) / (ring[j].Y - (double)ring[i].Y) + ring[i].X) inside = !inside;
                return inside;
            }
            return shape.Polygons.Any(poly => In(poly.Outer) && !poly.Holes.Any(In));
        }
        var random = new Random(7);
        List<Vector3> points = [new(150, 0, 30), new(100, 0, 80), new(0, 0, 0), new(60, 0, 40), new(15, 0, 15), new(80, 0, 60), new(130, 0, 30), new(75, 60, 20)];
        for (int i = 0; i < 5000; i++) points.Add(new(random.NextSingle() * 170 - 10, random.NextSingle() * 60 - 8, random.NextSingle() * 100 - 10));
        for (int x = -10; x <= 160; x += 5) for (int z = -10; z <= 90; z += 5) points.Add(new(x, 0, z));
        foreach (var p in points) Assert.True(Brute(p) == outline.Inside(p), $"{p}: index {outline.Inside(p)}, rings {Brute(p)}");
    }

    [Fact]
    public void ShortenedPieceNamesKeepSurfacesApart()
    {
        // Two surfaces whose ids begin alike, in one cell, under a long label: their pieces' names still differ.
        string a = "landscape_terrain_tile_section_001", b = "landscape_terrain_tile_section_002";
        var recipe = new TerrainRecipe(1, [new(a, "surfaces.gltf", a, TerrainAttributes.None), new(b, "surfaces.gltf", b, TerrainAttributes.None)], TerrainAttributes.None, []);
        var result = TerrainCompiler.Compile("m1_world_terrain", recipe, [Sheet(a, 10, 300, 20, 310) with { Surface = recipe.Surfaces[0] }, Sheet(b, 30, 300, 40, 310, y: 5) with { Surface = recipe.Surfaces[1] }], Materials, Grid, Token);
        Assert.Equal(2, result.Pieces.Count);
        Assert.Equal(result.Pieces.Count, result.Pieces.Select(p => p.Name).Distinct().Count());
        Assert.All(result.Pieces, p => Assert.True(p.Name.Length <= 34, p.Name));
        Assert.Equal(["~00_00x00", "~01_00x00"], result.Pieces.Select(p => p.Name[^9..]).Order());
        // A shortened name never equals another surface's full name: northern_cliff_rock_01 fits whole, while its longer
        // sibling, shortened, would otherwise end the same way.
        string fits = "northern_cliff_rock_01", longer = "northern_cliff_rock_large";
        var pair = new TerrainRecipe(1, [new(fits, "surfaces.gltf", fits, TerrainAttributes.None), new(longer, "surfaces.gltf", longer, TerrainAttributes.None)], TerrainAttributes.None, []);
        var named = TerrainCompiler.Compile("coast", pair, [Sheet(fits, 10, 300, 20, 310) with { Surface = pair.Surfaces[0] }, Sheet(longer, 30, 300, 40, 310, y: 5) with { Surface = pair.Surfaces[1] }], Materials, Grid, Token);
        Assert.Contains("coast_northern_cliff_rock_01_00x00", named.Pieces.Select(p => p.Name));
        Assert.Equal(2, named.Pieces.Select(p => p.Name).Distinct().Count());
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

        // Finite FLOAT coordinates beyond the old scaled Int64 range must split too. Each triangle has area
        // in YZ and three distinct vertices; the huge X values are exactly representable, with no cut required.
        // Descending authored X order must survive the split instead of being replaced by spatial sorting.
        foreach (bool huge in new[] { false, true })
        {
            GltfPrimitive primitive = new() { Material = new() };
            for (int i = 0; i < 308; i++)
            {
                int position = 307 - i;
                float x = huge ? MathF.ScaleB(1, 54) + position * MathF.ScaleB(1, 32) : 1024 + position * 4;
                primitive.Positions.AddRange([new(x, 0, 0), new(x, 1, 0), new(x, 0, 1)]);
                primitive.Normals.AddRange([Vector3.UnitX, Vector3.UnitX, Vector3.UnitX]);
                primitive.Indices.AddRange([3 * i, 3 * i + 1, 3 * i + 2]);
            }
            GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
            GltfDocument document = new(); document.Roots.Add(new() { Name = "land", Mesh = mesh });
            var (json, binary) = document.Write("surfaces.bin", Token);
            var parsed = GltfDocument.Read(json, _ => binary, Token);
            byte[] recipe = Recipe(TerrainAttributes.None).Write();
            WorldGltf.ImportContext context = new()
            {
                World = new(), Reference = (_, _) => (parsed, "surfaces.gltf"),
                ReadFile = (_, _) => (recipe, "surface.terrain.json"),
                TextureName = (_, _, _) => "texture", Grid = () => Grid, Token = Token,
            };
            var nodes = WorldGltf.ImportTerrain("surface.terrain.json", "database.gltf", context, 10000);
            Assert.Equal(2, nodes.Count);
            Assert.Equal(308, nodes.Sum(n => n.Model!.Polygons.Count));
            Assert.All(nodes, n =>
            {
                var model = Assert.IsType<WorldModel>(n.Model);
                Assert.InRange(model.Vertices.Count, 1, ModelBuilder.MaximumVertices);
                Assert.Equal(Vector3.UnitX, Assert.Single(model.Normals));
                Assert.All(model.Polygons, p => Assert.Equal(p.Vertices.Length, p.Normals.Length));
            });
            Assert.Equal(primitive.Positions,
                nodes.SelectMany(n => n.Model!.Polygons.SelectMany(p => p.Vertices.Select(v => n.Model!.Vertices[v]))));
        }
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
