using System.Numerics;
using zStudio.Core;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1.5707963f, .3f, -.4f)]
    [InlineData(-1.5707963f, -.8f, .7f)]
    [InlineData(1.5707f, 1.2f, -.6f)]
    [InlineData(.2f, -.7f, 1.1f)]
    public void RotationHandlesPreserveOrientationAtPolesAndKeepScaledChildrenAtPlacementPivot(float x, float y, float z)
    {
        var before = new PlacementTransform(new(100, -20, 73), new(x, y, z));
        const PlacementRotationKind kind = PlacementRotationKind.EulerRadians;
        var basis = Matrix4x4.CreateFromQuaternion(PlacementTransform.Orientation(kind, before.Rotation));
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var after = before.RotateWorld(kind, axis, .35f);
            var expected = basis * Matrix4x4.CreateFromAxisAngle(axis, .35f);
            var actual = Matrix4x4.CreateFromQuaternion(PlacementTransform.Orientation(kind, after.Rotation));
            foreach (var v in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                Assert.True(Vector3.Distance(Vector3.TransformNormal(v, expected), Vector3.TransformNormal(v, actual)) < .0001f);
            var authored = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(7, 8, 9) * basis * Matrix4x4.CreateTranslation(before.Position);
            var transformed = authored * after.DeltaFrom(before, kind);
            var desired = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(7, 8, 9) * expected * Matrix4x4.CreateTranslation(before.Position);
            Assert.True(Vector3.Distance(Vector3.Transform(new(3, 4, 5), transformed), Vector3.Transform(new(3, 4, 5), desired)) < .0001f);
            Assert.Equal(Matrix4x4.Identity, after.DeltaFrom(after, kind)); // Refreshed baselines do not rotate twice.
        }
        Assert.Equal(Matrix4x4.CreateTranslation(1, 2, 3), (before with { Position = before.Position + new Vector3(1, 2, 3) }).DeltaFrom(before, kind));
    }
}
