using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>Region shapes, recipe edits and terrain recipes created in a source project's workspace.</summary>
public sealed class TerrainEditTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TerrainOutline Square(float x0, float z0, float size) => new([new(x0, z0), new(x0 + size, z0), new(x0 + size, z0 + size), new(x0, z0 + size)], []);
    private static TerrainRecipe Basic() => new(1, [new("land", "land.gltf", "land", TerrainAttributes.None), new("rock", "land.gltf", "rock", TerrainAttributes.None)], TerrainAttributes.None, []);

    [Fact]
    public void ClippingRefusesDenseCrossingsBeforeExpansionAndRetainsOrdinaryShapes()
    {
        Vector2[] star = Enumerable.Range(0, 3001).Select(i =>
        {
            double angle = (i * 1500 % 3001) * Math.Tau / 3001;
            return new Vector2((float)Math.Cos(angle) * 1000, (float)Math.Sin(angle) * 1000);
        }).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("complexity", Assert.Throws<InvalidDataException>(() => TerrainShapes.Normalize([new(star, [])])).Message);
        // A stroke of dense crossings is refused before its pieces are offset.
        Assert.Throws<InvalidDataException>(() => TerrainShapes.Stroke(star, 100));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 5_000_000);
        var circle = Enumerable.Range(0, 20_000).Select(i => new Vector2(
            (float)Math.Cos(i * Math.Tau / 20_000) * 1000, (float)Math.Sin(i * Math.Tau / 20_000) * 1000)).ToArray();
        Assert.InRange(TerrainShapes.SquareUnits(TerrainShapes.Normalize([new(circle, [])])), 3_140_000, 3_143_000);
        Assert.Equal(100, TerrainShapes.SquareUnits(TerrainShapes.Normalize([Square(0, 0, 10)])));
    }

    [Fact]
    public void ShapesPaintAndEraseAsCleanAreas()
    {
        // Two overlapping squares merge into one polygon of their union.
        var union = TerrainShapes.Paint([Square(0, 0, 10)], [Square(5, 5, 10)], add: true);
        Assert.Single(union);
        Assert.Equal(175, TerrainShapes.SquareUnits(union), 3);
        // Erasing a square in the middle leaves a hole.
        var holed = TerrainShapes.Paint([Square(0, 0, 30)], [Square(10, 10, 10)], add: false);
        Assert.Single(Assert.Single(holed).Holes);
        Assert.Equal(800, TerrainShapes.SquareUnits(holed), 3);
        // Painting into the hole adds an island of its own.
        var island = TerrainShapes.Paint(holed, [Square(12, 12, 4)], add: true);
        Assert.Equal(2, island.Count);
        Assert.Equal(816, TerrainShapes.SquareUnits(island), 3);
        // A brush stroke covers a capsule along its path.
        var stroke = TerrainShapes.Stroke([new(0, 0), new(100, 0)], 5);
        // Arcs are polygons, a little inside the true circle.
        Assert.InRange(TerrainShapes.SquareUnits(stroke), (100 * 10 + Math.PI * 25) * 0.995, 100 * 10 + Math.PI * 25);
        Assert.InRange(TerrainShapes.SquareUnits(TerrainShapes.Stroke([new(3, 3)], 2)), Math.PI * 4 * 0.98, Math.PI * 4 * 1.0001);
        Assert.Throws<InvalidDataException>(() => TerrainShapes.Stroke([new(0, 0)], 0));
        // Long drags sampled as the viewport does (every quarter radius) paint: a half circle 157 radii long, and a drag
        // out 100 radii and back 50, whose reversal must not be cut short.
        const float r = 8;
        var arc = TerrainShapes.Stroke([.. Enumerable.Range(0, 629).Select(i => new Vector2(MathF.Cos(i / 200f) * 50 * r, MathF.Sin(i / 200f) * 50 * r))], r);
        Assert.Empty(Assert.Single(arc).Holes);
        Assert.InRange(TerrainShapes.SquareUnits(arc), Math.PI * 101 * r * r * 0.995, Math.PI * 101 * r * r);
        var back = TerrainShapes.Stroke([.. Enumerable.Range(0, 601).Select(i => new Vector2((i <= 400 ? i : 800 - i) * r / 4, 0))], r);
        Assert.InRange(TerrainShapes.SquareUnits(back), (100 * 2 + Math.PI) * r * r * 0.995, (100 * 2 + Math.PI) * r * r);
    }

    [Fact]
    public void RecipeEditsKeepRegionsValidAndOrdered()
    {
        var recipe = TerrainEdits.AddRegion(Basic(), new("road", ["land"], new([Square(0, 0, 10), Square(5, 5, 10)]), new() { Craters = TerrainCraters.Blocked }));
        Assert.Single(recipe.Regions[0].Shape!.Polygons);
        recipe = TerrainEdits.AddRegion(recipe, new("cave", [], null, new() { Zones = new([4]) }), 0);
        Assert.Equal(["cave", "road"], recipe.Regions.Select(r => r.Name));
        recipe = TerrainEdits.MoveRegion(recipe, "cave", 1);
        Assert.Equal(["road", "cave"], recipe.Regions.Select(r => r.Name));
        Assert.Throws<InvalidDataException>(() => TerrainEdits.AddRegion(recipe, new("road", [], null, TerrainAttributes.None)));
        Assert.Throws<InvalidDataException>(() => TerrainEdits.UpdateRegion(recipe, "cave", r => r with { Name = "road" }));
        // Clearing one attribute leaves the others (a JSON null removes the override).
        recipe = TerrainEdits.UpdateRegion(recipe, "road", r => r with { Set = TerrainAttributes.FromJson(JsonNode.Parse("""{ "craters": null, "soil": "lava" }"""), "patch", r.Set) });
        Assert.Null(recipe.Regions[0].Set.Craters); Assert.Equal(4u, recipe.Regions[0].Set.Soil);
        // Painting a region that covers everything adds nothing, so it is refused (for the GUI brush and MCP alike).
        Assert.Contains("covers its whole surfaces", Assert.Throws<InvalidDataException>(() => TerrainEdits.Paint(recipe, "cave", [Square(0, 0, 10)], add: true)).Message);
        // Erasing from a region that covers everything leaves everywhere but the stroke; erasing all of it leaves nothing.
        recipe = TerrainEdits.Paint(recipe, "cave", [Square(0, 0, 10)], add: false);
        Assert.Single(Assert.Single(recipe.Regions[1].Shape!.Polygons).Holes);
        recipe = TerrainEdits.Paint(recipe, "road", [Square(-100, -100, 300)], add: false);
        Assert.Empty(recipe.Regions[0].Shape!.Polygons);
        Assert.Equal(recipe.Write(), TerrainRecipe.Parse(recipe.Write(), "x").Write());
        // A surface that is the only one a region names cannot go; others can, leaving the region's list.
        recipe = TerrainEdits.UpdateRegion(recipe, "road", r => r with { Surfaces = ["rock"] });
        Assert.Throws<InvalidDataException>(() => TerrainEdits.RemoveSurface(recipe, "rock"));
        Assert.Single(TerrainEdits.RemoveSurface(recipe, "land").Surfaces);
    }

    [Fact]
    public void HundredsOfStrokesPaintAndEraseWithinBoundedWork()
    {
        // A 64 × 64-quad heightfield (8,192 triangles) over a 16 × 16-cell grid, and 512 hand-drawn drags of a radius-8 brush
        // (sampled every quarter radius, as the viewport does), painted in batches.
        TerrainGrid grid = new(0, 4096, 4096, -4096, 256, -256, 16, 16);
        List<TerrainFace> faces = [];
        TerrainCorner C(int i, int j) { float x = i * 64f, z = j * 64f; return new(new(x, 30 * MathF.Sin(x / 350) + 20 * MathF.Cos(z / 270), z), Vector3.UnitY, new(i / 64f, j / 64f)); }
        for (int i = 0; i < 64; i++)
            for (int j = 0; j < 64; j++) { faces.Add(new(0, C(i, j), C(i + 1, j), C(i + 1, j + 1))); faces.Add(new(0, C(i, j), C(i + 1, j + 1), C(i, j + 1))); }
        TerrainSurfaceGeometry land = new(new("land", "land.gltf", "land", TerrainAttributes.None), faces);
        TerrainRecipe Recipe(TerrainShape? shape) => new(1, [land.Surface], TerrainAttributes.None, [new("r", [], shape, new() { Soil = 3 })]);
        TerrainRecipe erased = Recipe(null), painted = Recipe(new([]));
        var random = new Random(1234);
        for (int batch = 0; batch < 8; batch++)
        {
            List<TerrainOutline> strokes = [];
            for (int s = 0; s < 64; s++)
            {
                Vector2 start = new(300 + random.NextSingle() * 3400, 300 + random.NextSingle() * 3400);
                float angle = random.NextSingle() * MathF.Tau;
                Vector2 along = new(MathF.Cos(angle), MathF.Sin(angle)), across = new(-along.Y, along.X);
                strokes.AddRange(TerrainShapes.Stroke([.. Enumerable.Range(0, 100).Select(i => start + along * 2 * i + across * 12 * MathF.Sin(i / 6f))], 8));
            }
            // The shapes' clipping check once ran out of its sweep work near 500 strokes, refusing the next stroke.
            erased = TerrainEdits.Paint(erased, "r", strokes, add: false);
            painted = TerrainEdits.Paint(painted, "r", strokes, add: true);
        }
        int points = TerrainShapes.PointCount(painted.Regions[0].Shape!.Polygons);
        Assert.True(points > 80_000, $"{points} outline points");
        // Erasing leaves everywhere (±1,000,000 units) with the strokes as holes; cutting it stays local to the strokes, as
        // painting them does. Each piece is cut only by the outline edges that cross it, not by their lines' extensions,
        // and its centre's side is found from its bucket of the outline: once, painting stopped building after about 140
        // such strokes (over 100 million steps) and its pieces numbered several times the outline's points.
        const long budget = 24_000_000;
        var paint = TerrainCompiler.Compile("t", painted, [land], [new(0xFFFFFF00)], grid, Token, TerrainCompiler.MaximumCreatedCorners, budget);
        var erase = TerrainCompiler.Compile("t", erased, [land], [new(0xFFFFFF00)], grid, Token, TerrainCompiler.MaximumCreatedCorners, budget);
        foreach (var compiled in new[] { paint, erase })
            Assert.InRange(compiled.Pieces.Sum(p => p.Polygons.Count), faces.Count, faces.Count * 2 + points * 3 / 2);
        // The erased region covers exactly what the painted one leaves out.
        static double PlanArea(IEnumerable<TerrainPolygonOutput> polygons) => polygons.Sum(p =>
            Math.Abs(Enumerable.Range(0, p.Corners.Length).Sum(i => (double)p.Corners[i].Position.X * p.Corners[(i + 1) % p.Corners.Length].Position.Z - (double)p.Corners[(i + 1) % p.Corners.Length].Position.X * p.Corners[i].Position.Z)) / 2);
        double painting = PlanArea(paint.Pieces.SelectMany(p => p.Polygons).Where(p => p.Soil == 3));
        Assert.Equal(TerrainShapes.SquareUnits(painted.Regions[0].Shape!.Polygons), painting, 1.0);
        Assert.Equal(painting, PlanArea(erase.Pieces.SelectMany(p => p.Polygons).Where(p => p.Soil != 3)), 1.0);
        Assert.Equal(4096.0 * 4096 - painting, PlanArea(erase.Pieces.SelectMany(p => p.Polygons).Where(p => p.Soil == 3)), 1.0);
        // The engine takes a polygon's plane from its first three corners (ZoneProbe). Painting puts corners on the sloped
        // polygons' edges, first edges included, and cuts pieces whose corners can start on a line: each polygon must still
        // start where that plane is its own.
        foreach (var polygon in paint.Pieces.Concat(erase.Pieces).SelectMany(p => p.Polygons))
        {
            var c = polygon.Corners;
            var normal = ZoneProbe.PlaneNormal(c[0].Position, c[1].Position, c[2].Position);
            foreach (var corner in c)
                if (!(Math.Abs(ZoneProbe.PlaneHeight(c[0].Position, normal, corner.Position.X, corner.Position.Z) - corner.Position.Y) <= 0.01f))
                    Assert.Fail($"The plane of {c[0].Position}, {c[1].Position}, {c[2].Position} misses the corner {corner.Position}.");
        }
    }

    [Fact]
    public async Task CreatingATerrainAddsItsRecipeAndMarkerAsOneUndoableChange()
    {
        using SourceWorldFixture fixture = new();
        // The surfaces: a 100 × 100 plane across the grid's cell lines, in their own file.
        ModelBuilder builder = new();
        builder.Add(new([new(200, 0, 300), new(300, 0, 300), new(300, 0, 200), new(200, 0, 200)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], new() { Texture = new("rock"), Flags = 0x1FF }));
        WorldNode land = new("land", WorldNodeClass.Object3D) { Model = builder.Finish(), Flags = WorldGltf.DefaultCarried };
        land.SetPayloadInt(0, 0x28);
        var (json, bin) = WorldGltf.Export([land], 0xFF, new() { Texture = t => ($"../../textures/{t.Name}.png", 0) }).Write("hills.bin", TestContext.Current.CancellationToken);
        fixture.Write("data/m1/models/terrain/hills.gltf", json); fixture.Write("data/m1/models/terrain/hills.bin", bin);
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, "data/m1/models/m1.gltf", "data/m1/models/m1.gltf", ["ground"], token: Token));
        var created = SourceTerrain.Create(workspace, "data/m1/models/m1.gltf", "data/m1/models/terrain/hills.gltf", ["land"], token: Token);
        Assert.Equal(["data/m1/models/m1.gltf", "data/m1/models/terrain/hills.terrain.json"], workspace.DirtyFiles);
        Assert.Equal(["data/m1/models/terrain/hills.terrain.json"], SourceTerrain.Recipes(workspace, Token));
        var marker = JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf", Token)!)!["nodes"]!.AsArray().Last()!;
        Assert.Equal("terrain/hills.terrain.json", marker["extras"]!["recoil"]!["terrain"]!.GetValue<string>());
        Assert.Equal("hills.gltf", SourceTerrain.Read(workspace, "data/m1/models/terrain/hills.terrain.json", Token).Surfaces.Single().Model);
        // Painting a region rebuilds the pieces: four cells, plus the painted road's own pieces.
        SourceTerrain.Edit(workspace, "data/m1/models/terrain/hills.terrain.json", "Add road", r => TerrainEdits.AddRegion(r, new("road", [], new([]), new() { Craters = TerrainCraters.Blocked })), Token);
        SourceTerrain.Edit(workspace, "data/m1/models/terrain/hills.terrain.json", "Paint road", r => TerrainEdits.Paint(r, "road", TerrainShapes.Stroke([new(220, 250), new(280, 250)], 4), add: true), Token);
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "preview"), workspace.Overlay(), token: Token);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        var pieces = world.Nodes.Where(n => n.Name.StartsWith("hills_land_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, pieces.Count(p => (p.Flags & 0x20000) == 0));
        Assert.Equal(2, pieces.Count(p => (p.Flags & 0x20000) != 0));
        // An edit that changes nothing is not a change; undo takes the three changes back to the files on disk.
        Assert.Null(SourceTerrain.Edit(workspace, "data/m1/models/terrain/hills.terrain.json", "Nothing", r => r, Token));
        workspace.Undo(); workspace.Undo(); workspace.Undo();
        Assert.False(workspace.IsDirty);
        Assert.Empty(SourceTerrain.Recipes(workspace, Token));
        // A recipe name too long for the marker's engine name (hills_…_terrain) gives the marker the fallback name, so the
        // world it was accepted for still builds.
        SourceTerrain.Create(workspace, "data/m1/models/m1.gltf", "data/m1/models/terrain/hills.gltf", ["land"], "data/m1/models/terrain/hills_with_a_long_recipe_name.terrain.json", Token);
        Assert.Equal("terrain", JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf", Token)!)!["nodes"]!.AsArray().Last()!["name"]!.GetValue<string>());
        build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "long"), workspace.Overlay(), token: Token);
        Assert.True(File.Exists(build.WorldPath));
    }
}
