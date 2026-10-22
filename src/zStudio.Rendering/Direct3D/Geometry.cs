using System.Numerics;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// Triangle-list geometry shared by every mesh node and instance that draws it. Positions, normals, texture coordinates and
/// indices are fixed for its lifetime (callers never mutate the arrays); the vertex colours can be replaced on their own,
/// keeping this object, its positions and every node that draws it. Colours null and colours empty both mean none.
/// <para>
/// No managed copy is kept: the arrays are uploaded to the device on first draw (the colour stream again for each new colour
/// array, the last two kept), and a picking hierarchy over the positions is built on the first ray query.
/// </para>
/// </summary>
internal sealed class MeshData
{
    private Color4[]? colors;
    private DeviceCache<MeshBuffers>? buffers;
    private MeshBvh? bvh;
    private (Vector3 Min, Vector3 Max)? bounds;

    public MeshData(Vector3[] positions, int[] indices, Vector3[]? normals = null, Vector2[]? textureCoordinates = null, Color4[]? colors = null)
    {
        Positions = positions; Indices = indices; Normals = normals; TextureCoordinates = textureCoordinates; this.colors = colors;
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
            RenderView.InvalidateAll();
        }
    }

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
    private DeviceCache<LineBuffers>? buffers;
    private (Vector3 Min, Vector3 Max)? bounds;
    public LineData(Vector3[] positions, int[] indices) { Positions = positions; Indices = indices; }
    public Vector3[] Positions { get; }
    public int[] Indices { get; }
    internal LineBuffers? Buffers(D3DGraphics graphics) => Positions.Length == 0 || Indices.Length < 2 ? null
        : (buffers ??= new()).Get(graphics, g => new LineBuffers(g, this));
    internal (Vector3 Min, Vector3 Max) Bounds => bounds ??= MeshBvh.Measure(Positions);
    internal void ReleaseDeviceResources() => buffers?.Release();
}
