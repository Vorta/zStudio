using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound22TerrainTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TerrainGrid Grid = new(0, 10, 10, -10, 10, -10, 1, 1);
    private static TerrainSurface Surface(string name) => new(name, "surface.gltf", name, TerrainAttributes.None);
    private static TerrainRecipe Recipe(int surfaces = 1) => new(1, Enumerable.Range(0, surfaces).Select(i => Surface($"s{i}")).ToArray(), TerrainAttributes.None, []);
    private static GltfDocument Document(int surfaces = 1, int triangles = 1)
    {
        GltfPrimitive primitive = new() { Material = new() };
        primitive.Positions.AddRange([new(1, 0, 1), new(2, 0, 1), new(1, 0, 2)]);
        for (int i = 0; i < triangles; i++) primitive.Indices.AddRange([0, 2, 1]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument doc = new();
        for (int i = 0; i < surfaces; i++) doc.Roots.Add(new() { Name = $"s{i}", Mesh = mesh });
        return doc;
    }
    private static WorldGltf.ImportContext Context(GltfDocument doc, TerrainRecipe recipe)
    {
        byte[] bytes = recipe.Write();
        return new()
        {
            World = new(), Reference = (_, _) => (doc, "surface.gltf"), TextureName = (_, _, _) => "texture",
            ReadFile = (_, _) => (bytes, "surface.terrain.json"), Grid = () => Grid, Token = Token
        };
    }
    private static List<WorldNode> Import(WorldGltf.ImportContext context, long limit = TerrainCompiler.MaximumCreatedCorners) =>
        WorldGltf.ImportTerrain("surface.terrain.json", "database.gltf", context, limit);

    [Fact]
    public void SurfaceCopiesShareOneInputAllowance()
    {
        var doc = Document(2);
        var context = Context(doc, Recipe(2));
        Assert.Contains("terrain input", Assert.Throws<InvalidDataException>(() => Import(context, 3)).Message);
        Assert.Empty(context.World.Models);
        // Both distinct surfaces are retained at the exact allowance, even though their mesh object is shared.
        var pieces = Import(Context(doc, Recipe(2)), 6);
        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, p => Assert.Single(p.Model!.Polygons));
    }

    [Fact]
    public void InputRefusalPrecedesPolygonListAndCornerAllocation()
    {
        var small = Context(Document(triangles: 1), Recipe());
        Assert.Throws<InvalidDataException>(() => Import(small, 0)); // warm the refusal path
        var large = Context(Document(triangles: 100_000), Recipe());
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("terrain input", Assert.Throws<InvalidDataException>(() => Import(large, 0)).Message);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 256_000);
        Assert.Empty(large.World.Models);
    }

    [Fact]
    public void RecordedPolygonCornersCountBeforeTheirArraysAreBuilt()
    {
        var doc = Document(); var primitive = doc.Roots[0].Mesh!.Primitives[0];
        primitive.Positions.Add(new(2, 0, 2));
        primitive.Extras = new() { [WorldGltf.Key] = new JsonObject { ["polygons"] = new JsonArray(
            new JsonObject { ["triangles"] = 1, ["corners"] = new JsonArray(0, 2, 3, 1) }) } };
        Assert.Contains("terrain input", Assert.Throws<InvalidDataException>(() => Import(Context(doc, Recipe()), 3)).Message);
        Assert.Single(Import(Context(doc, Recipe()), 4));
    }

    [Fact]
    public void FinitePositionsOverflowingAnInvertibleTransformAreRefused()
    {
        var doc = Document(); var node = doc.Roots[0];
        node.Matrix = Matrix4x4.CreateTranslation(float.MaxValue, 0, 0);
        var positions = node.Mesh!.Primitives[0].Positions;
        positions[0] = positions[0] with { X = float.MaxValue };
        Assert.True(Matrix4x4.Invert(node.Matrix.Value, out _));
        var context = Context(doc, Recipe());
        Assert.Throws<InvalidDataException>(() => Import(context));
        Assert.Empty(context.World.Models);
    }

    [Fact]
    public void FiniteTransformOverflowRefusesInsteadOfDroppingTheSurface()
    {
        var doc = Document();
        doc.Roots[0].Matrix = Matrix4x4.CreateScale(1e38f);
        var context = Context(doc, Recipe());
        Assert.Throws<InvalidDataException>(() => Import(context));
        Assert.Empty(context.World.Models);
        doc.Roots[0].Matrix = Matrix4x4.Identity;
        Assert.Single(Import(Context(doc, Recipe())));
    }

    [Fact]
    public void NestedFiniteTransformsCannotHideAnOverflow()
    {
        var doc = Document(); var leaf = doc.Roots[0];
        leaf.Matrix = Matrix4x4.CreateScale(1e20f);
        GltfNode parent = new() { Name = "parent", Matrix = Matrix4x4.CreateScale(1e20f) };
        parent.Children.Add(leaf); doc.Roots.Clear(); doc.Roots.Add(parent);
        Assert.Throws<InvalidDataException>(() => Import(Context(doc, Recipe())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegionsThatDoNotCutStillSpendTheAggregateWorkAllowance(bool outlined)
    {
        var surface = Surface("s0");
        TerrainShape? shape = outlined ? new([new([new(100, 100), new(101, 100), new(100, 101)], [])]) : null;
        var regions = Enumerable.Range(0, 100).Select(i => new TerrainRegion($"r{i}", [], shape, new() { Soil = (uint)(i % 6) })).ToArray();
        TerrainRecipe recipe = new(1, [surface], TerrainAttributes.None, regions);
        var geometry = Geometry(surface, 20);
        Assert.Contains("work limit", Assert.Throws<InvalidDataException>(() =>
            TerrainCompiler.Compile("test", recipe, [geometry], [new(0)], Grid, Token, 100_000, 1_000)).Message);
        var compiled = TerrainCompiler.Compile("test", recipe, [geometry], [new(0)], Grid, Token, 100_000, 100_000);
        Assert.Equal(20, compiled.Pieces.Sum(p => p.Polygons.Count));
        if (!outlined) Assert.All(compiled.Pieces.SelectMany(p => p.Polygons), p => Assert.Equal(3u, p.Soil));
    }

    [Fact]
    public void DirectCompilerRejectsNonfiniteCornersAndRemainsUsable()
    {
        var surface = Surface("s0"); var recipe = new TerrainRecipe(1, [surface], TerrainAttributes.None, []);
        TerrainCorner bad = new(new(float.PositiveInfinity, 0, 0), Vector3.UnitY, Vector2.Zero);
        var good = Geometry(surface, 1);
        var broken = new TerrainSurfaceGeometry(surface, [new(0, bad, good.Faces[0].Corners[1], good.Faces[0].Corners[2])]);
        Assert.Throws<InvalidDataException>(() => TerrainCompiler.Compile("test", recipe, [broken], [new(0)], Grid, Token));
        Assert.Single(TerrainCompiler.Compile("test", recipe, [good], [new(0)], Grid, Token).Pieces);
    }

    private static TerrainSurfaceGeometry Geometry(TerrainSurface surface, int faces)
    {
        TerrainCorner a = new(new(1, 0, 1), Vector3.UnitY, Vector2.Zero);
        TerrainCorner b = new(new(1, 0, 2), Vector3.UnitY, Vector2.Zero);
        TerrainCorner c = new(new(2, 0, 1), Vector3.UnitY, Vector2.Zero);
        return new(surface, Enumerable.Repeat(new TerrainFace(0, a, b, c), faces).ToArray());
    }
}
