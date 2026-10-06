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
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 5_000_000);
        Assert.Throws<InvalidDataException>(() => TerrainShapes.Stroke(star.Take(1000).ToArray(), 100));
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
    public async Task CreatingATerrainAddsItsRecipeAndMarkerAsOneUndoableChange()
    {
        using SourceWorldFixture fixture = new();
        // The surfaces: a 100 × 100 plane across the grid's cell lines, in their own file.
        ModelBuilder builder = new();
        builder.Add(new([new(200, 0, 300), new(300, 0, 300), new(300, 0, 200), new(200, 0, 200)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], new() { Texture = new("rock"), Flags = 0x1FF }));
        WorldNode land = new("land", WorldNodeClass.Object3D) { Model = builder.Finish(), Flags = WorldGltf.DefaultCarried };
        land.SetPayloadInt(0, 0x28);
        var (json, bin) = WorldGltf.Export([land], 0xFF, new() { Texture = t => ($"../../textures/{t.Name}.png", 0) }).Write("hills.bin");
        fixture.Write("data/m1/models/terrain/hills.gltf", json); fixture.Write("data/m1/models/terrain/hills.bin", bin);
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, "data/m1/models/m1.gltf", "data/m1/models/m1.gltf", ["ground"], token: Token));
        var created = SourceTerrain.Create(workspace, "data/m1/models/m1.gltf", "data/m1/models/terrain/hills.gltf", ["land"], token: Token);
        Assert.Equal(["data/m1/models/m1.gltf", "data/m1/models/terrain/hills.terrain.json"], workspace.DirtyFiles);
        Assert.Equal(["data/m1/models/terrain/hills.terrain.json"], SourceTerrain.Recipes(workspace));
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
        Assert.Empty(SourceTerrain.Recipes(workspace));
    }
}
