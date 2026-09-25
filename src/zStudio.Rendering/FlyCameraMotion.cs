using System.Windows.Media.Media3D;

namespace Recoil.Zbd.Rendering;

/// <summary>Time-based navigation, independent of geometry hit tests and keyboard repeat.</summary>
internal static class FlyCameraMotion
{
    internal static double InitialSpeed(double diagonal) => double.IsFinite(diagonal) && diagonal > 0 ? Math.Clamp(diagonal * .1, 1, 1000) : 100;
    internal static double AdjustSpeed(double speed, int wheelDelta) => Math.Clamp(speed * Math.Pow(1.25, Math.Clamp(wheelDelta / 120d, -100, 100)), .1, 100000);

    internal static Vector3D Displacement(Vector3D look, Vector3D input, double speed, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || look.LengthSquared < 1e-12 || input.LengthSquared < 1e-12) return default;
        look.Normalize();
        var right = Vector3D.CrossProduct(look, new(0, 1, 0));
        if (right.LengthSquared < 1e-12) return default;
        right.Normalize();
        var movement = right * input.X + new Vector3D(0, 1, 0) * input.Y + look * input.Z;
        if (movement.LengthSquared < 1e-12) return default;
        movement.Normalize();
        return movement * (speed * Math.Min(seconds, .1));
    }

    internal static Vector3D Look(Vector3D look, double horizontal, double vertical)
    {
        double length = look.Length;
        if (length < 1e-6 || !double.IsFinite(horizontal) || !double.IsFinite(vertical)) return look;
        look /= length;
        const double sensitivity = .1 * Math.PI / 180;
        double yaw = Math.Atan2(look.X, -look.Z) + horizontal * sensitivity;
        double pitch = Math.Clamp(Math.Asin(Math.Clamp(look.Y, -1, 1)) - vertical * sensitivity, -89 * Math.PI / 180, 89 * Math.PI / 180);
        return new Vector3D(Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch)) * length;
    }
}
