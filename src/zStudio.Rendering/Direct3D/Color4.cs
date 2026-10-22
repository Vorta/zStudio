using System.Numerics;

namespace zStudio.Rendering.Direct3D;

/// <summary>A linear RGBA colour as the shaders receive it (components are not clamped).</summary>
internal readonly record struct Color4(float Red, float Green, float Blue, float Alpha)
{
    public static Color4 FromColor(System.Windows.Media.Color color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    public Vector4 ToVector4() => new(Red, Green, Blue, Alpha);
}

/// <summary>Which triangle faces a mesh drops (front faces wind counterclockwise on screen).</summary>
internal enum CullMode { None, Front, Back }
