using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound32TerrainTests
{
    [Theory]
    [InlineData("uncut", false)]
    [InlineData("grid", false)]
    [InlineData("region", false)]
    [InlineData("uncut", true)]
    [InlineData("grid", true)]
    [InlineData("region", true)]
    public void RecordedFanKeepsItsOriginalTrianglesWhenLeadingCornersAreCollinear(string cutting, bool warped)
    {
        Vector3[] points = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 0, 1), new(0, 0, 1), new(1, warped ? .5f : 0, .5f)];
        var (recipe, grid) = Inputs(cutting);
        GltfPrimitive primitive = new()
        {
            Material = new(),
            Extras = new() { [WorldGltf.Key] = new JsonObject { ["polygons"] = new JsonArray(points.Length - 2) } },
        };
        primitive.Positions.AddRange(points);
        for (int i = 1; i + 1 < points.Length; i++) primitive.Indices.AddRange([0, i, i + 1]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument document = new(); document.Roots.Add(new() { Name = "surface", Mesh = mesh });
        WorldGltf.ImportContext context = new()
        {
            World = new(), Reference = (_, _) => (document, "surface.gltf"),
            ReadFile = (_, _) => (recipe.Write(), "surface.terrain.json"), TextureName = (_, _, _) => "texture",
            Grid = () => grid, Token = TestContext.Current.CancellationToken,
        };
        var imported = WorldGltf.ImportTerrain("surface.terrain.json", "database.gltf", context, 10000);
        var actual = imported.SelectMany(n =>
        {
            var model = n.Model!;
            return model.Polygons.Select(p => p.Vertices.Select(i => model.Vertices[i]).ToArray());
        }).ToArray();
        AssertMoments(Moments([points]), Moments(actual));
        if (cutting == "uncut") Assert.Equal(points, Assert.Single(actual));
    }

    [Theory]
    [InlineData("uncut")]
    [InlineData("grid")]
    [InlineData("region")]
    public void ConvexAuthoredFanDoesNotSpreadAnInvisibleCornersTextureCoordinate(string cutting)
    {
        // Corner1 occurs only in the zero-area first triangle. Another fan origin or an unfanned grid cut
        // incorrectly spreads its U=100 across real geometry, even though the outline is convex.
        Vector3[] points = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(2, 0, 1), new(0, 0, 1)];
        TerrainCorner[] corners = points.Select((p, i) => new TerrainCorner(p,
            Vector3.Normalize(new Vector3(i + 1, 2, 3)), new Vector2(i == 1 ? 100 : 0, 0))).ToArray();
        var (recipe, grid) = Inputs(cutting);
        var result = TerrainCompiler.Compile("t", recipe,
            [new(recipe.Surfaces[0], [new TerrainFace(0, corners)])], [new(0xFFFFFF00)], grid, TestContext.Current.CancellationToken);
        var polygons = result.Pieces.SelectMany(p => p.Polygons).ToArray();
        double area = 0, textureIntegral = 0;
        foreach (var polygon in polygons)
            for (int i = 1; i + 1 < polygon.Corners.Length; i++)
            {
                var a = polygon.Corners[0]; var b = polygon.Corners[i]; var c = polygon.Corners[i + 1];
                double triangle = Math.Abs(SignedArea(a.Position, b.Position, c.Position));
                area += triangle;
                textureIntegral += triangle * (a.Uv.X + b.Uv.X + c.Uv.X) / 3;
            }
        Assert.InRange(Math.Abs(area - 2), 0, 1e-6);
        Assert.InRange(Math.Abs(textureIntegral), 0, 1e-6);
        if (cutting == "uncut") Assert.Equal(corners, Assert.Single(polygons).Corners);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedModelPathIsRefusedBeforeDocumentDecodingOrPathSplitting(bool escaped)
    {
        byte[] input = RecipeJson(escaped ? string.Concat(Enumerable.Repeat("\\u002f", 1_000_000)) : new string('/', 1_000_000));
        _ = TerrainRecipe.Parse(RecipeJson("surface.gltf"), "warm");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var failure = Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(input, "recipe"));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("260", failure.Message);
        Assert.InRange(allocated, 0, 1_000_000);
    }

    [Fact]
    public void ModelPathChecksDecodedLengthAndKeepsValidEscapedRelativePaths()
    {
        string valid = "../" + new string('a', 252) + ".gltf";
        Assert.Equal(260, valid.Length);
        string escaped = string.Concat(valid.Select(c => $"\\u{(int)c:x4}"));
        var recipe = TerrainRecipe.Parse(RecipeJson(escaped), "valid");
        Assert.Equal(valid, recipe.Surfaces[0].Model);
        Assert.Equal(recipe.Write(), TerrainRecipe.Parse(recipe.Write(), "roundtrip").Write());
        Assert.Contains("260", Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(RecipeJson(valid + "x"), "long")).Message);
        Assert.Equal("../surface.gltf", TerrainRecipe.Parse(RecipeJson("..\\\\surface.gltf"), "backslash").Surfaces[0].Model);
    }

    private static byte[] RecipeJson(string model) => Encoding.UTF8.GetBytes("{\"format\":\"recoil-terrain\",\"version\":1,\"compiler\":1,\"surfaces\":[{\"id\":\"surface\",\"model\":\"" + model + "\",\"node\":\"surface\"}]}");

    private static (TerrainRecipe Recipe, TerrainGrid Grid) Inputs(string cutting)
    {
        TerrainRegion[] regions = cutting == "region"
            ? [new("strip", [], new([new([new(.25f, -1), new(1.75f, -1), new(1.75f, 2), new(.25f, 2)], [])]), new() { Soil = 2 })]
            : [];
        return (new(1, [new("surface", "surface.gltf", "surface", TerrainAttributes.None)], TerrainAttributes.None, regions),
            cutting == "grid" ? new(-1, 2, 5, -3, 1.25f, -1, 4, 3) : new(-1, 2, 5, -3, 5, -3, 1, 1));
    }

    private static double SignedArea(Vector3 a, Vector3 b, Vector3 c) =>
        ((b.X - (double)a.X) * (c.Z - (double)a.Z) - (b.Z - (double)a.Z) * (c.X - (double)a.X)) / 2;

    private static double[] Moments(IEnumerable<Vector3[]> polygons)
    {
        double[] result = new double[4];
        foreach (var p in polygons)
            for (int i = 1; i + 1 < p.Length; i++)
            {
                double signed = SignedArea(p[0], p[i], p[i + 1]);
                int side = signed >= 0 ? 0 : 1;
                result[side] += Math.Abs(signed);
                result[side + 2] += Math.Abs(signed) * (p[0].Y + p[i].Y + p[i + 1].Y) / 3;
            }
        return result;
    }

    private static void AssertMoments(double[] expected, double[] actual)
    {
        for (int i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, 1e-5);
    }
}
