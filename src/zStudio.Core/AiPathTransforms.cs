using System.Numerics;

namespace zStudio.Core;

/// <summary>World-axis transforms of authored AI positions about the selection centre.</summary>
public static class AiPathTransforms
{
    public const int MaximumNodes = 16_384;
    public const double MaximumCoordinate = 1e12;

    public static Vector3 Center(IReadOnlyDictionary<MissionPickupSource, Vector3> positions)
    {
        var (x, y, z) = CenterComponents(positions);
        return new((float)x, (float)y, (float)z);
    }

    public static IReadOnlyDictionary<MissionPickupSource, Vector3> Translate(
        IReadOnlyDictionary<MissionPickupSource, Vector3> positions, Vector3 delta)
    {
        _ = CenterComponents(positions);
        RequireFinite(delta);
        return Map(positions, p => ((double)p.X + delta.X, (double)p.Y + delta.Y, (double)p.Z + delta.Z));
    }

    /// <param name="axis">World X, Y or Z, respectively 0, 1 or 2.</param>
    /// <param name="degrees">Right-handed rotation in degrees.</param>
    public static IReadOnlyDictionary<MissionPickupSource, Vector3> Rotate(
        IReadOnlyDictionary<MissionPickupSource, Vector3> positions, int axis, double degrees)
    {
        if (axis is < 0 or > 2) throw new InvalidDataException("Choose a world X, Y or Z rotation axis.");
        if (!double.IsFinite(degrees)) throw new InvalidDataException("Rotation must be finite degrees.");
        var (cx, cy, cz) = CenterComponents(positions);
        double turn = Math.IEEERemainder(degrees, 360);
        if (turn == 0) return Map(positions, p => (p.X, p.Y, p.Z));
        double radians = turn * Math.PI / 180;
        double sin = Math.Sin(radians), cos = Math.Cos(radians);
        return Map(positions, p =>
        {
            double x = p.X - cx, y = p.Y - cy, z = p.Z - cz;
            return axis switch
            {
                0 => ((double)p.X, cy + cos * y - sin * z, cz + sin * y + cos * z),
                1 => (cx + cos * x + sin * z, (double)p.Y, cz - sin * x + cos * z),
                _ => (cx + cos * x - sin * y, cy + sin * x + cos * y, (double)p.Z)
            };
        });
    }

    /// <summary>Component factors apply in world XYZ; use equal factors for uniform scaling.</summary>
    public static IReadOnlyDictionary<MissionPickupSource, Vector3> Scale(
        IReadOnlyDictionary<MissionPickupSource, Vector3> positions, Vector3 factors)
    {
        RequireFinite(factors);
        var (cx, cy, cz) = CenterComponents(positions);
        return Map(positions, p => (factors.X == 1 ? p.X : cx + (p.X - cx) * factors.X,
            factors.Y == 1 ? p.Y : cy + (p.Y - cy) * factors.Y,
            factors.Z == 1 ? p.Z : cz + (p.Z - cz) * factors.Z));
    }

    internal static void RequirePosition(Vector3 position)
    {
        RequireFinite(position);
        if (Math.Abs((double)position.X) > MaximumCoordinate || Math.Abs((double)position.Y) > MaximumCoordinate ||
            Math.Abs((double)position.Z) > MaximumCoordinate)
            throw new InvalidDataException("AI coordinates must stay within the supported ±1e12 preview range.");
    }

    private static void RequireFinite(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new InvalidDataException("Transform components must be finite game floats.");
    }

    private static (double X, double Y, double Z) CenterComponents(IReadOnlyDictionary<MissionPickupSource, Vector3> positions)
    {
        if (positions.Count is < 1 or > MaximumNodes)
            throw new InvalidDataException($"Select between 1 and {MaximumNodes} AI nodes.");
        double x = 0, y = 0, z = 0;
        foreach (var position in positions.Values)
        {
            RequirePosition(position);
            x += position.X; y += position.Y; z += position.Z;
        }
        return (x / positions.Count, y / positions.Count, z / positions.Count);
    }

    private static IReadOnlyDictionary<MissionPickupSource, Vector3> Map(
        IReadOnlyDictionary<MissionPickupSource, Vector3> positions, Func<Vector3, (double X, double Y, double Z)> transform)
    {
        Dictionary<MissionPickupSource, Vector3> result = new(positions.Count, new MissionSourceComparer());
        foreach (var (source, position) in positions)
        {
            var (x, y, z) = transform(position);
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
                Math.Abs(x) > MaximumCoordinate || Math.Abs(y) > MaximumCoordinate || Math.Abs(z) > MaximumCoordinate)
                throw new InvalidDataException("The transform would put AI coordinates outside the supported ±1e12 preview range.");
            result.Add(source, new((float)x, (float)y, (float)z));
        }
        return result;
    }
}
