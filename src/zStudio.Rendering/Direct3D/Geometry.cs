using System.Numerics;
using System.Runtime.CompilerServices;
using HelixToolkit;
using HelixGeometry = HelixToolkit.SharpDX.MeshGeometry3D;
using HelixLineGeometry = HelixToolkit.SharpDX.LineGeometry3D;
using HelixColor4 = HelixToolkit.Maths.Color4;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// Triangle-list geometry shared by every mesh node and instance that draws it. Positions, normals, texture coordinates and
/// indices are fixed for its lifetime (callers never mutate the arrays); the vertex colours can be replaced on their own,
/// keeping this object, its positions and every node that draws it. Colours null and colours empty both mean none.
/// </summary>
internal sealed class MeshData
{
    private Color4[]? colors;

    public MeshData(Vector3[] positions, int[] indices, Vector3[]? normals = null, Vector2[]? textureCoordinates = null, Color4[]? colors = null)
    {
        Positions = positions; Indices = indices; Normals = normals; TextureCoordinates = textureCoordinates; this.colors = colors;
        // Backend copies: one Helix collection per source array (positions shared across poses and outlines; colours per array).
        HelixGeometry = new HelixGeometry
        {
            Positions = HelixCollections.Positions(positions), Indices = new IntCollection(indices),
            Normals = normals == null ? null : new Vector3Collection(normals),
            TextureCoordinates = textureCoordinates == null ? null : new Vector2Collection(textureCoordinates),
            Colors = HelixCollections.Colors(colors)
        };
    }

    public Vector3[] Positions { get; }
    public int[] Indices { get; }
    public Vector3[]? Normals { get; }
    public Vector2[]? TextureCoordinates { get; }
    /// <summary>Per-vertex colours; assigning another array changes only the colour stream (the same array keeps its upload).</summary>
    public Color4[]? Colors
    {
        get => colors;
        set { if (ReferenceEquals(colors, value)) return; colors = value; HelixGeometry.Colors = HelixCollections.Colors(value); }
    }

    internal HelixGeometry HelixGeometry { get; }
}

/// <summary>Line-list geometry (index pairs); it may share its positions array with a <see cref="MeshData"/>.</summary>
internal sealed class LineData
{
    public LineData(Vector3[] positions, int[] indices)
    {
        Positions = positions; Indices = indices;
        HelixGeometry = new HelixLineGeometry { Positions = HelixCollections.Positions(positions), Indices = new IntCollection(indices) };
    }
    public Vector3[] Positions { get; }
    public int[] Indices { get; }
    internal HelixLineGeometry HelixGeometry { get; }
}

/// <summary>One backend collection per source array, so geometries sharing an array share its copy and a colour array swapped back is not copied again.</summary>
internal static class HelixCollections
{
    private static readonly ConditionalWeakTable<Vector3[], Vector3Collection> positions = new();
    private static readonly ConditionalWeakTable<Color4[], Color4Collection> colors = new();
    internal static Vector3Collection Positions(Vector3[] source) => positions.GetValue(source, static p => new Vector3Collection(p));
    internal static Color4Collection? Colors(Color4[]? source) => source == null ? null
        : colors.GetValue(source, static c => new Color4Collection(c.Select(v => new HelixColor4(v.Red, v.Green, v.Blue, v.Alpha))));
}
