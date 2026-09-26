using System.Windows.Media.Media3D;
using Recoil.Zbd.Rendering;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class McpCameraTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void VerticalPoseIsImmediatelySafeForMovement(double vertical)
    {
        var eye = new Point3D(10, 20, 30);
        var pose = SceneViewport.UprightPose(new(eye, new(0, vertical * 5, 0), new(0, 1, 0), 60));
        Assert.Equal(eye, pose.Position);
        Assert.Equal(5, pose.LookDirection.Length, 10);
        var forward = pose.LookDirection; forward.Normalize();
        Assert.InRange(Math.Abs(Math.Asin(forward.Y) * 180 / Math.PI), 88.999, 89.001);
        var right = Vector3D.CrossProduct(forward, new(0, 1, 0)); right.Normalize();
        var moved = pose.Position + right * 10 + forward * 10;
        Assert.True(double.IsFinite(moved.X) && double.IsFinite(moved.Y) && double.IsFinite(moved.Z));
        Assert.True(pose.UpDirection.Y > 0);
        Assert.Equal(0, Vector3D.DotProduct(forward, pose.UpDirection), 10);
    }
}
