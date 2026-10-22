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
/// <para>
/// The Direct3D backend keeps no managed copy: it uploads these arrays to the device on first draw (the colour stream again
/// for each new colour array, the last two kept) and builds a picking hierarchy over the positions on the first ray query.
/// </para>
/// </summary>
internal sealed class MeshData
{
    private Color4[]? colors;
    private HelixGeometry? helix;
    private DeviceCache<MeshBuffers>? buffers;
    private MeshBvh? bvh;
    private (Vector3 Min, Vector3 Max)? bounds;

    public MeshData(Vector3[] positions, int[] indices, Vector3[]? normals = null, Vector2[]? textureCoordinates = null, Color4[]? colors = null)
    {
        Positions = positions; Indices = indices; Normals = normals; TextureCoordinates = textureCoordinates; this.colors = colors;
        if (RenderBackend.UseHelix) helix = BuildHelix();
    }

    public Vector3[] Positions { get; }
    public int[] Indices { get; }
    public Vector3[]? Normals { get; }
    public Vector2[]? TextureCoordinates { get; }
    /// <summary>Per-vertex colours; assigning another array changes only the colour stream (the same array keeps its upload).</summary>
    public Color4[]? Colors
    {
        get => colors;
        set
        {
            if (ReferenceEquals(colors, value)) return;
            colors = value;
            if (helix != null) helix.Colors = HelixCollections.Colors(value);
            else RenderView.InvalidateAll();
        }
    }

    // Helix adapter: positions and colours come from the per-array caches below; normals, texture coordinates and indices are copied per geometry.
    private HelixGeometry BuildHelix() => new()
    {
        Positions = HelixCollections.Positions(Positions), Indices = new IntCollection(Indices),
        Normals = Normals == null ? null : new Vector3Collection(Normals),
        TextureCoordinates = TextureCoordinates == null ? null : new Vector2Collection(TextureCoordinates),
        Colors = HelixCollections.Colors(colors)
    };
    internal HelixGeometry HelixGeometry => helix ??= BuildHelix();

    /// <summary>The device buffers (null when there is nothing to draw).</summary>
    internal MeshBuffers? Buffers(D3DGraphics graphics) => Positions.Length == 0 || Indices.Length < 3 ? null
        : (buffers ??= new()).Get(graphics, g => new MeshBuffers(g, this));
    /// <summary>Releases the device copies now (a later draw uploads them again); shared aids keep theirs until their device goes.</summary>
    internal void ReleaseDeviceResources() { if (!IsShared) buffers?.Release(); }
    /// <summary>A static aid every view draws (gizmo handles, view cube, axes): nodes never release its device copies.</summary>
    internal bool IsShared { get; init; }
    /// <summary>The picking hierarchy, built on first use.</summary>
    internal MeshBvh Bvh => bvh ??= new MeshBvh(Positions, Indices);
    /// <summary>Model-space bounds of the positions (empty geometry: min +∞, max −∞).</summary>
    internal (Vector3 Min, Vector3 Max) Bounds => bounds ??= MeshBvh.Measure(Positions);
}

/// <summary>Line-list geometry (index pairs); it may share its positions array with a <see cref="MeshData"/>.</summary>
internal sealed class LineData
{
    private HelixLineGeometry? helix;
    private DeviceCache<LineBuffers>? buffers;
    private (Vector3 Min, Vector3 Max)? bounds;
    public LineData(Vector3[] positions, int[] indices)
    {
        Positions = positions; Indices = indices;
        if (RenderBackend.UseHelix) helix = BuildHelix();
    }
    public Vector3[] Positions { get; }
    public int[] Indices { get; }
    private HelixLineGeometry BuildHelix() => new() { Positions = HelixCollections.Positions(Positions), Indices = new IntCollection(Indices) };
    internal HelixLineGeometry HelixGeometry => helix ??= BuildHelix();
    internal LineBuffers? Buffers(D3DGraphics graphics) => Positions.Length == 0 || Indices.Length < 2 ? null
        : (buffers ??= new()).Get(graphics, g => new LineBuffers(g, this));
    internal (Vector3 Min, Vector3 Max) Bounds => bounds ??= MeshBvh.Measure(Positions);
    internal void ReleaseDeviceResources() => buffers?.Release();
}

/// <summary>Helix adapter: one backend collection per positions or colours array, so geometries sharing positions share their copy and a colour array swapped back is not copied again.</summary>
internal static class HelixCollections
{
    private static readonly ConditionalWeakTable<Vector3[], Vector3Collection> positions = new();
    private static readonly ConditionalWeakTable<Color4[], Color4Collection> colors = new();
    internal static Vector3Collection Positions(Vector3[] source) => positions.GetValue(source, static p => new Vector3Collection(p));
    internal static Color4Collection? Colors(Color4[]? source) => source == null ? null
        : colors.GetValue(source, static c => new Color4Collection(c.Select(v => new HelixColor4(v.Red, v.Green, v.Blue, v.Alpha))));
}
