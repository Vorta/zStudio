using System.Numerics;
using System.Windows.Media.Media3D;
using HelixToolkit;
using Recoil.Zbd.Rendering;
using Xunit;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class VisibleDepthRangeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2041)]
    [InlineData(100000)]
    public void WorldTranslationPreservesDepthPrecision(double origin)
    {
        var range = Range(new(origin, 32, 200)); var matrix = Matrix3D.Identity; matrix.Translate(new(origin, 32, 0));
        range.Include(Triangle(new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)), matrix);
        Assert.Equal(200, range.Near, 8); Assert.Equal(200, range.Far, 8);
    }
    [Fact]
    public void OffscreenAndBehindCameraGeometryDoesNotReduceNearPlane()
    {
        var range = Range(new(0, 0, 10));
        range.Include(Triangle(new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)), Matrix3D.Identity);
        range.Include(Triangle(new(100, -1, 9), new(101, -1, 9), new(100, 1, 9)), Matrix3D.Identity);
        range.Include(Triangle(new(-1, -1, 20), new(1, -1, 20), new(0, 1, 20)), Matrix3D.Identity);
        Assert.Equal(10, range.Near); Assert.Equal(10, range.Far);
    }
    [Fact]
    public void TerrainBoundsCrossingCameraAreRefinedAgainstTriangles()
    {
        var range = Range(new(0, 10, 0));
        range.Include(Triangle(new(-1000, 0, -1000), new(1000, 0, -1000), new(0, 0, 1000)), Matrix3D.Identity);
        // Horizontal camera ten units above a large ground plane: the nearest
        // visible ground is about 23 units forward, not at the navigation floor.
        Assert.InRange(range.Near, 22, 24); Assert.Equal(1000, range.Far, 6);
    }
    [Fact]
    public void SurfaceThroughEyeUsesNavigationFloorWithoutInvalidProjection()
    {
        var range = Range(new(0, 0, 0));
        range.Include(Triangle(new(0, -10, -10), new(0, 10, -10), new(0, 0, 10)), Matrix3D.Identity);
        Assert.InRange(range.Near, .000999, .001001); Assert.InRange(range.Far, .001, 10);
    }
    [Fact]
    public void TransformedBoundsDoNotDiscardRotatedSurface()
    {
        var range = Range(new(0, 0, 10)); var matrix = Matrix3D.Identity;
        matrix.Rotate(new System.Windows.Media.Media3D.Quaternion(new Vector3D(0, 1, 0), 90));
        range.Include(Triangle(new(0, -1, -1), new(0, -1, 1), new(0, 1, 0)), matrix);
        Assert.Equal(10, range.Near, 6); Assert.Equal(10, range.Far, 6);
    }
    private static VisibleDepthRange Range(Point3D eye) => new(eye, new(0, 0, -1), new(0, 1, 0), 45, 1.5, .001);
    private static MeshGeometry3D Triangle(params Vector3[] vertices) => new() { Positions = new Vector3Collection(vertices), Indices = new IntCollection([0, 1, 2]) };
}
