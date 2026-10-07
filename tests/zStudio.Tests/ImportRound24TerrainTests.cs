using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound24TerrainTests
{
    [Theory]
    [InlineData("uncut")]
    [InlineData("grid")]
    [InlineData("region")]
    public void RecordedOpposingFanTrianglesRetainAreaWindingAndVertices(string cutting)
    {
        // Both authored triangles are real geometry: opposite winding must not cancel their area.
        Vector3[] authored = [new(0, 0, 0), new(1, 0, 1), new(0, 0, 1), new(1, 0, 0)];
        var pieces = Import(authored, cutting);
        Assert.NotEmpty(pieces);
        double positive = 0, negative = 0, region = 0;
        HashSet<Vector3> vertices = [];
        foreach (var node in pieces)
        {
            var model = node.Model!;
            foreach (var vertex in model.Vertices) vertices.Add(vertex);
            foreach (var polygon in model.Polygons)
                for (int i = 1; i + 1 < polygon.Vertices.Length; i++)
                {
                    var a = model.Vertices[polygon.Vertices[0]];
                    var b = model.Vertices[polygon.Vertices[i]];
                    var c = model.Vertices[polygon.Vertices[i + 1]];
                    double signed = ((b.X - a.X) * (double)(c.Z - a.Z) - (b.Z - a.Z) * (double)(c.X - a.X)) / 2;
                    if (signed > 0) positive += signed; else negative -= signed;
                    if (polygon.Material!.Soil == 2) region += Math.Abs(signed);
                }
        }
        Assert.InRange(Math.Abs(positive - .5), 0, 1e-6);
        Assert.InRange(Math.Abs(negative - .5), 0, 1e-6);
        Assert.All(authored, vertex => Assert.Contains(vertex, vertices));
        if (cutting == "region") Assert.InRange(Math.Abs(region - .5), 0, 1e-6);
        else Assert.Equal(0, region);
    }

    [Fact]
    public void TrulyCollinearRecordedFanStillProducesNoGeometry()
    {
        Assert.Empty(Import([new(0, 0, 0), new(.25f, 0, 0), new(.5f, 0, 0), new(1, 0, 0)], "uncut"));
    }

    private static IReadOnlyList<WorldNode> Import(Vector3[] points, string cutting)
    {
        GltfPrimitive primitive = new()
        {
            Material = new(),
            Extras = new() { [WorldGltf.Key] = new JsonObject { ["polygons"] = new JsonArray(2) } },
        };
        primitive.Positions.AddRange(points);
        primitive.Indices.AddRange([0, 1, 2, 0, 2, 3]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument document = new(); document.Roots.Add(new() { Name = "surface", Mesh = mesh });
        TerrainRegion[] regions = cutting == "region"
            ? [new("strip", [], new([new([new(.25f, 0), new(.75f, 0), new(.75f, 1), new(.25f, 1)], [])]), new() { Soil = 2 })]
            : [];
        TerrainRecipe recipe = new(1, [new("surface", "surface.gltf", "surface", TerrainAttributes.None)], TerrainAttributes.None, regions);
        WorldGltf.ImportContext context = new()
        {
            World = new(), Reference = (_, _) => (document, "surface.gltf"),
            ReadFile = (_, _) => (recipe.Write(), "surface.terrain.json"), TextureName = (_, _, _) => "texture",
            Grid = () => cutting == "grid" ? new(0, 1, 1, -1, .5f, -.5f, 2, 2) : new(-1, 2, 3, -3, 3, -3, 1, 1),
            Token = TestContext.Current.CancellationToken,
        };
        return WorldGltf.ImportTerrain("surface.terrain.json", "database.gltf", context, 10000);
    }
}
