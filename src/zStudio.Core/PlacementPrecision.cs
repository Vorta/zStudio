using System.Numerics;

namespace zStudio.Core;

/// <summary>
/// The precision mission placements are stored with. The shipped <c>net_NN.zrd</c>, <c>aiv*.zrd</c> and <c>puppies*.zrd</c>
/// files write positions and AIV headings (degrees) with one decimal; pickup rotations are radians (all 0.0 in the shipped
/// files) and keep four decimals (about 0.006°). An edit stores the values it changes at this precision, so the preview
/// shows what is saved; values it does not change keep their stored text.
/// </summary>
public static class PlacementPrecision
{
    public const int PositionDecimals = 1, HeadingDecimals = 1, RadianDecimals = 4;

    public static float Round(float value, int decimals)
    {
        float rounded = (float)Math.Round((double)value, decimals, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    /// <summary>A whole new position (every component is written).</summary>
    public static Vector3 Position(Vector3 value) => new(Round(value.X, PositionDecimals), Round(value.Y, PositionDecimals), Round(value.Z, PositionDecimals));

    /// <summary>An edited position: the components that differ from <paramref name="current"/>, rounded.</summary>
    public static Vector3 Position(Vector3 value, Vector3 current) => Changed(value, current, PositionDecimals);

    /// <summary>The components of <paramref name="value"/> that differ from <paramref name="current"/>, rounded.</summary>
    public static Vector3 Changed(Vector3 value, Vector3 current, int decimals) => new(
        value.X == current.X ? value.X : Round(value.X, decimals),
        value.Y == current.Y ? value.Y : Round(value.Y, decimals),
        value.Z == current.Z ? value.Z : Round(value.Z, decimals));
}
