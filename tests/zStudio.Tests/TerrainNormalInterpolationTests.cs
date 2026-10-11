using System.Numerics;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainNormalInterpolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrittenNormalsKeepTheAuthoredTriangleDirectionAcrossGridSteps(bool smooth)
    {
        var one = Import(1, [], smooth);
        var two = Import(2, [], smooth);
        var unchanged = Import(4, [], smooth);
        foreach (var corners in new[] { one, two, unchanged }) AssertDirections(corners, smooth);
        Vector3 point = new(2, 0, 2);
        Assert.Contains(one, c => c.Position == point);
        Assert.Contains(two, c => c.Position == point);
        foreach (var a in one.Where(c => c.Position == point))
            foreach (var b in two.Where(c => c.Position == point))
                Assert.InRange(Vector3.Distance(a.Normal, b.Normal), 0, 2e-6f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegionCutsDoNotRenormalizeIntermediateNormalFields(bool reverse)
    {
        TerrainRegion[] regions =
        [
            new("vertical", [], new([new([new(1, -1), new(3, -1), new(3, 5), new(1, 5)], [])]), new() { Soil = 2 }),
            new("horizontal", [], new([new([new(-1, 1), new(5, 1), new(5, 3), new(-1, 3)], [])]), new() { Priority = 3 }),
        ];
        if (reverse) Array.Reverse(regions);
        AssertDirections(Import(4, regions, smooth: true), smooth: true);
    }

    [Fact]
    public void OpposedNormalsAtAnInsertedCornerStayFiniteAndZero()
    {
        var corners = Import(2, [], smooth: false, [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY]);
        Assert.Contains(corners, c => c.Position == new Vector3(2, 0, 0) && c.Normal == Vector3.Zero);
        Assert.All(corners, c => Assert.True(float.IsFinite(c.Normal.X) && float.IsFinite(c.Normal.Y) && float.IsFinite(c.Normal.Z)));
    }

    [Fact]
    public void UncutAuthoredNormalsRetainTheirExactStoredMagnitude()
    {
        TerrainCorner[] corners = [new(Vector3.Zero, 2 * Vector3.UnitX, Vector2.Zero),
            new(new(4, 0, 0), 3 * Vector3.UnitY, Vector2.UnitX), new(new(0, 0, 4), 4 * Vector3.UnitZ, Vector2.UnitY)];
        TerrainSurface surface = new("surface", "surface.gltf", "surface", TerrainAttributes.None);
        TerrainRecipe recipe = new(1, [surface], TerrainAttributes.None, []);
        var result = TerrainCompiler.Compile("t", recipe, [new(surface, [new TerrainFace(0, corners)])], [new(0)],
            new(0, 4, 4, -4, 4, -4, 1, 1), TestContext.Current.CancellationToken);
        Assert.Equal(corners, Assert.Single(Assert.Single(result.Pieces).Polygons).Corners);
    }

    private readonly record struct Corner(Vector3 Position, Vector3 Normal);

    private static Vector3[] Normals(bool smooth) => smooth
        ? [Vector3.UnitY, Vector3.Normalize(new(1, 2, 0)), Vector3.Normalize(new(0, 2, 1))]
        : [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

    private static void AssertDirections(Corner[] corners, bool smooth)
    {
        Assert.NotEmpty(corners);
        var normals = Normals(smooth);
        foreach (var corner in corners)
        {
            float b = corner.Position.X / 4, c = corner.Position.Z / 4, a = 1 - b - c;
            // Independent original-triangle barycentric field, normalized only at the final vertex.
            var expected = Vector3.Normalize(a * normals[0] + b * normals[1] + c * normals[2]);
            Assert.InRange(Vector3.Distance(expected, corner.Normal), 0, 2e-6f);
        }
    }

    private static Corner[] Import(float step, IReadOnlyList<TerrainRegion> regions, bool smooth, Vector3[]? authoredNormals = null)
    {
        GltfPrimitive primitive = new() { Material = new() };
        primitive.Positions.AddRange([Vector3.Zero, new(4, 0, 0), new(0, 0, 4)]);
        primitive.Normals.AddRange(authoredNormals ?? Normals(smooth));
        primitive.Indices.AddRange([0, 1, 2]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument source = new(); source.Roots.Add(new() { Name = "surface", Mesh = mesh });
        var token = TestContext.Current.CancellationToken;
        var (json, binary) = source.Write("surface.bin", token);
        var parsed = GltfDocument.Read(json, _ => binary, token);
        TerrainRecipe recipe = new(1, [new("surface", "surface.gltf", "surface", TerrainAttributes.None)], TerrainAttributes.None, regions);
        GameZWorld world = new();
        WorldGltf.ImportContext context = new()
        {
            World = world, Reference = (_, _) => (parsed, "surface.gltf"),
            ReadFile = (_, _) => (recipe.Write(), "surface.terrain.json"), TextureName = (_, _, _) => "texture",
            Grid = () => new(0, 4, 4, -4, step, -step, (int)(4 / step), (int)(4 / step)), Token = token,
        };
        world.Nodes.AddRange(WorldGltf.ImportTerrain("surface.terrain.json", "database.gltf", context, 10000));
        var document = FormatRegistry.Default.OpenBytes("world.zbd", GameZWriter.Write(world, token), token: token);
        Assert.Empty(document.Diagnostics);
        var reread = GameZWorldReader.FromDocument(document, token);
        return reread.Nodes.Where(n => n.Model != null).SelectMany(n =>
        {
            var model = n.Model!;
            return model.Polygons.SelectMany(p => p.Vertices.Select((v, i) => new Corner(model.Vertices[v], model.Normals[p.Normals[i]])));
        }).ToArray();
    }
}
