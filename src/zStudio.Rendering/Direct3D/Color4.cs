using System.Numerics;

namespace zStudio.Rendering.Direct3D;

/// <summary>An RGBA colour as the shaders receive it: byte / 255 with no gamma conversion (the back buffer is UNorm, not sRGB);
/// components are not clamped.</summary>
internal readonly record struct Color4(float Red, float Green, float Blue, float Alpha)
{
    public Vector4 ToVector4() => new(Red, Green, Blue, Alpha);
}

/// <summary>Which triangle faces a mesh drops (front faces wind counterclockwise on screen).</summary>
internal enum CullMode { None, Front, Back }
