using System.Numerics;
using Recoil.Zbd.Core;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ScenePolygonIdentityTests
{
    [Fact]
    public void MaterialBatchesKeepOriginalPolygonIndicesAcrossInterleavedMaterials()
    {
        var model = Model(
            [new(0, 0, 0), new(2, 0, 0), new(2, 2, 0), new(0, 2, 0),
             new(10, 0, 0), new(11, 0, 0), new(10, 1, 0),
             new(20, 0, 0), new(21, 0, 0), new(20, 1, 0)],
            Face(7, 0, 1, 2, 3), Face(9, 4, 5, 6), Face(7, 7, 8, 9));

        var parts = GeometryBuilder.Build(model, token: TestContext.Current.CancellationToken);
        Assert.Equal(2, parts.Count);
        var shared = Assert.Single(parts, p => p.MaterialIndex == 7);
        Assert.Equal([0, 0, 2], shared.TrianglePolygons);
        Assert.Equal([1], Assert.Single(parts, p => p.MaterialIndex == 9).TrianglePolygons);
        AssertTriangleOwnership(model, parts);
    }

    [Fact]
    public void TriangleStripWindingAndSourceIdentitySurviveEarlierBatchVertices()
    {
        var model = Model(
            [new(-3, 0, 0), new(-2, 0, 0), new(-3, 1, 0),
             new(0, 0, 0), new(0, 1, 0), new(1, 0, 0), new(1, 1, 0), new(2, 0, 0)],
            Face(4, 0, 1, 2), Face(4, 3, 4, 5, 6, 7) with { Flags = 1024 });

        var part = Assert.Single(GeometryBuilder.Build(model, token: TestContext.Current.CancellationToken));
        Assert.Equal([0, 1, 1, 1], part.TrianglePolygons);
        Assert.Equal([0, 1, 2, 3, 4, 5, 5, 4, 6, 5, 6, 7], part.Indices);
        AssertTriangleOwnership(model, [part]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcaveTriangulationKeepsEveryTriangleOnItsAuthoredPolygon(bool reverse)
    {
        Vector3[] vertices = [new(-2, 0, 0), new(-1, 0, 0), new(-2, 1, 0),
            new(0, 0, 0), new(3, 0, 0), new(3, 3, 0), new(1.5f, 1, 0), new(0, 3, 0)];
        int[] corners = [3, 4, 5, 6, 7];
        if (reverse) Array.Reverse(corners);
        var model = Model(vertices, Face(2, 0, 1, 2), Face(2, corners));

        var part = Assert.Single(GeometryBuilder.Build(model, token: TestContext.Current.CancellationToken));
        Assert.Equal([0, 1, 1, 1], part.TrianglePolygons);
        float area = 0;
        for (int triangle = 1; triangle < part.TrianglePolygons.Length; triangle++)
        {
            int index = triangle * 3;
            var a = part.Positions[part.Indices[index]];
            var b = part.Positions[part.Indices[index + 1]];
            var c = part.Positions[part.Indices[index + 2]];
            float cross = Vector3.Cross(b - a, c - a).Z;
            Assert.True(reverse ? cross < 0 : cross > 0);
            area += cross / 2;
        }
        Assert.Equal(reverse ? -6f : 6f, area);
        AssertTriangleOwnership(model, [part]);
    }

    [Fact]
    public void SkippedPolygonsLeaveIdentityGapsWithoutPhantomTrianglesOrParts()
    {
        var model = Model(
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(float.NaN, 0, 0),
             new(2, 0, 0), new(10, 0, 0), new(11, 0, 0), new(10, 1, 0)],
            Face(3, 0, 1, 2), Face(99, 0, 1), Face(99, 0, 1, 99),
            Face(99, 0, 1, 3), Face(99, 0, 1, 4), Face(3, 5, 6, 7));
        List<Diagnostic> diagnostics = [];

        var part = Assert.Single(GeometryBuilder.Build(model, diagnostics, TestContext.Current.CancellationToken));
        Assert.Equal(3, part.MaterialIndex);
        Assert.Equal([0, 5], part.TrianglePolygons);
        Assert.Equal(6, part.Positions.Length);
        Assert.Equal(6, part.Indices.Length);
        Assert.Equal(3, diagnostics.Count);
        AssertTriangleOwnership(model, [part]);
    }

    [Fact]
    public void ManuallyProducedMeshHasNoImpliedPolygonZeroIdentity()
    {
        MeshPart part = new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [0, 1, 2]);
        Assert.Empty(part.TrianglePolygons);
        Assert.Empty(part.VertexPolygons);
    }

    private static Polygon Face(int material, params int[] vertices) => new(material, 0, vertices, [], [], new());
    private static GameModel Model(Vector3[] vertices, params Polygon[] polygons) => new(0, vertices, [], [], polygons, new());

    private static void AssertTriangleOwnership(GameModel source, IReadOnlyList<MeshPart> parts)
    {
        foreach (var part in parts)
        {
            Assert.Equal(part.Indices.Length / 3, part.TrianglePolygons.Length);
            Assert.Equal(part.Positions.Length, part.VertexPolygons.Length);
            for (int triangle = 0; triangle < part.TrianglePolygons.Length; triangle++)
            {
                var polygon = source.Polygons[part.TrianglePolygons[triangle]];
                Assert.Equal(part.MaterialIndex, polygon.MaterialIndex);
                var corners = polygon.Vertices.Select(i => source.Vertices[i]).ToHashSet();
                for (int corner = 0; corner < 3; corner++)
                {
                    Assert.Contains(part.Positions[part.Indices[triangle * 3 + corner]], corners);
                    Assert.Equal(part.TrianglePolygons[triangle], part.VertexPolygons[part.Indices[triangle * 3 + corner]]);
                }
            }
        }
    }
}
