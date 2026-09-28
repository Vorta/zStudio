using System.Numerics;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Core;

public enum PlacementRotationKind { None, EulerRadians, HeadingDegrees }

/// <summary>Authored position and angles in the source format's units, never a runtime pose.</summary>
public readonly record struct PlacementTransform(Vector3 Position, Vector3 Rotation)
{
    public static Quaternion Orientation(PlacementRotationKind kind, Vector3 rotation) => kind switch
    {
        PlacementRotationKind.EulerRadians => AnimationMath.FromEuler(rotation),
        PlacementRotationKind.HeadingDegrees => Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(rotation.Y * (Math.PI / 180))),
        _ => Quaternion.Identity
    };

    public Matrix4x4 DeltaFrom(PlacementTransform baseline, PlacementRotationKind kind)
    {
        // Avoid multiplying inverse rotations for position-only changes: untouched
        // instance bases (including nonuniform scale) retain their exact components.
        if (Rotation == baseline.Rotation) return Matrix4x4.CreateTranslation(Position - baseline.Position);
        return Matrix4x4.CreateTranslation(-baseline.Position) *
            Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(Orientation(kind, baseline.Rotation))) *
            Matrix4x4.CreateFromQuaternion(Orientation(kind, Rotation)) * Matrix4x4.CreateTranslation(Position);
    }

    public PlacementTransform RotateWorld(PlacementRotationKind kind, Vector3 axis, float radians)
    {
        if (!float.IsFinite(radians) || !float.IsFinite(axis.LengthSquared()) || axis.LengthSquared() < .5f)
            throw new ArgumentException("A finite rotation and axis are required.");
        if (kind == PlacementRotationKind.None) throw new InvalidOperationException("This placement has no authored rotation.");
        if (radians == 0) return this;
        if (kind == PlacementRotationKind.HeadingDegrees)
        {
            if (axis != Vector3.UnitY) throw new InvalidOperationException("Vehicles support heading around Y only.");
            return this with { Rotation = Rotation with { Y = Rotation.Y + (float)(radians * (180 / Math.PI)) } };
        }
        var q = Quaternion.Concatenate(Orientation(kind, Rotation), Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians));
        // Double intermediates avoid losing yaw/roll to float cancellation near
        // a pitch pole. Normalize here so the matrix remains orthonormal.
        double inverseLength = 1 / Math.Sqrt((double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W);
        double x = q.X * inverseLength, y = q.Y * inverseLength, z = q.Z * inverseLength, w = q.W * inverseLength;
        double m31 = 2 * (x * z + y * w), m32 = 2 * (y * z - x * w), m33 = 1 - 2 * (x * x + y * y);
        double horizontal = Math.Sqrt(m31 * m31 + m33 * m33);
        float pitch = (float)Math.Atan2(-m32, horizontal);
        // At a pitch pole yaw and roll are not independently identifiable. Choose
        // zero roll and retain their combined orientation instead of atan2(0,0).
        var euler = horizontal > 1e-7
            ? new Vector3(pitch, (float)Math.Atan2(m31, m33), (float)Math.Atan2(2 * (x * y + z * w), 1 - 2 * (x * x + z * z)))
            : new Vector3(MathF.CopySign(MathF.PI / 2, (float)-m32), (float)Math.Atan2(2 * (y * w - x * z), 1 - 2 * (y * y + z * z)), 0);
        return this with { Rotation = euler };
    }
}
