using System.Collections;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace zStudio.Rendering.Direct3D;

/// <summary>Something that holds nodes: the view itself, or a group.</summary>
internal interface INodeOwner { RenderView? View { get; } }

/// <summary>
/// A scene node. A node draws (and is picked) only while <see cref="Visibility"/> is Visible and <see cref="IsRendering"/> is
/// set; a guide (<see cref="IsGuide"/>) also needs <see cref="RenderView.GuidesVisible"/>, which never changes its own flags. A
/// group's flags apply to everything in it. <see cref="Transform"/> (null is identity) places the node in its parent's frame.
/// A node belongs to at most one collection at a time.
/// </summary>
internal abstract class Node : IDisposable
{
    private Visibility visibility;
    private bool rendering = true, guide, hitTestVisible = true;
    private Matrix4x4? transform;

    internal INodeOwner? Owner { get; set; }
    /// <summary>The view this node is shown in (through its groups), or null while it is in none.</summary>
    internal RenderView? View => Owner?.View;
    /// <summary>Redraws the view after a change.</summary>
    private protected void Changed() => View?.InvalidateRender();
    /// <summary>Stores <paramref name="value"/> and redraws when it differs (setting the same value, as per-frame refreshes do,
    /// renders nothing).</summary>
    private protected void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; Changed();
    }

    public Visibility Visibility
    {
        get => visibility;
        set
        {
            if (visibility == value) return;
            visibility = value; VisibilityChanged(); Changed();
        }
    }
    public bool IsRendering { get => rendering; set => Set(ref rendering, value); }
    public bool IsHitTestVisible { get => hitTestVisible; set => hitTestVisible = value; }
    /// <summary>Presentation chrome: hidden while the view's guides are hidden (Hide UI), whatever its own flags say.</summary>
    public bool IsGuide { get => guide; set => Set(ref guide, value); }
    public Matrix4x4? Transform
    {
        get => transform;
        set
        {
            bool changed = transform != value;
            transform = value; TransformSet();
            if (changed) Changed();
        }
    }
    /// <summary>The node's transform was set (groups raise <see cref="GroupNode.TransformChanged"/>).</summary>
    private protected virtual void TransformSet() { }

    private protected virtual bool Shown => visibility == Visibility.Visible;
    /// <summary>The node's own visibility (its visibility, instances and followed mesh; not its flags or ancestors).</summary>
    internal bool IsShown => Shown;
    /// <summary>The node's own rendering flag (guides: before the view's guide state).</summary>
    internal bool RendersItself => rendering;
    private protected virtual void VisibilityChanged() { }

    public virtual void Dispose() { }
}

/// <summary>An ordered node list: submission order, which also orders equal render orders and unsorted transparent nodes.</summary>
internal sealed class NodeCollection : IReadOnlyList<Node>
{
    private readonly List<Node> items = [];
    private readonly INodeOwner owner;
    internal NodeCollection(INodeOwner owner) => this.owner = owner;
    public int Count => items.Count;
    public Node this[int index] => items[index];
    public void Add(Node node)
    {
        if (node.Owner != null) throw new InvalidOperationException("The node already belongs to a collection.");
        items.Add(node); node.Owner = owner;
        owner.View?.InvalidateRender();
    }
    public bool Remove(Node node)
    {
        if (!items.Remove(node)) return false;
        node.Owner = null; owner.View?.InvalidateRender(); return true;
    }
    public void Clear()
    {
        foreach (var node in items) node.Owner = null;
        items.Clear(); owner.View?.InvalidateRender();
    }
    public bool Contains(Node node) => items.Contains(node);
    public IEnumerator<Node> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Draws its children in order, under its own transform and flags.</summary>
internal class GroupNode : Node, INodeOwner
{
    public GroupNode() => Children = new(this);
    public NodeCollection Children { get; }
    RenderView? INodeOwner.View => View;
    /// <summary>Raised after every change of <see cref="Node.Transform"/>, the node's own and the transform gizmo's on its target.</summary>
    public event Action? TransformChanged;
    private protected override void TransformSet() => TransformChanged?.Invoke();
    public override void Dispose() { TransformChanged = null; base.Dispose(); }
}

/// <summary>
/// Draws its opaque children first, then its transparent ones back to front (squared eye distance to each node's world bounds,
/// instances included), re-sorted every frame. With sorting off, children keep their order.
/// </summary>
internal sealed class SortingGroupNode : GroupNode
{
    private bool sorting = true;
    public bool SortingEnabled { get => sorting; set => Set(ref sorting, value); }
}

/// <summary>Drawn after everything else with the depth buffer cleared: its children depth-test only among themselves.</summary>
internal sealed class TopMostGroupNode : GroupNode;

/// <summary>
/// Geometry drawn once per instance: world position is p · instance · <see cref="Node.Transform"/> (instances are placed in the
/// node's frame, as every CPU bounds, framing and inspection path composes them). Null <see cref="Instances"/> draws once; an
/// empty list draws (and hits) nothing. Opaque meshes and lines are drawn in ascending <see cref="RenderOrder"/> (default 0;
/// among equal orders horizon materials first, then submission order). <see cref="IsDepthClipEnabled"/> false lets the near
/// and far planes not clip it; <see cref="DepthBias"/> is the rasterizer's integer bias on 32-bit float depth.
/// </summary>
internal abstract class GeometryNode : Node
{
    private IReadOnlyList<Matrix4x4>? instances;
    private int renderOrder, depthBias;
    private bool depthClip = true;
    private InstanceBuffer? instanceBuffer;
    public IReadOnlyList<Matrix4x4>? Instances
    {
        get => instances;
        set
        {
            if (ReferenceEquals(instances, value)) return;
            instances = value; InstancesChanged(); Changed();
        }
    }
    private protected virtual void InstancesChanged() { }
    private protected override bool Shown => base.Shown && instances is not { Count: 0 };
    public int RenderOrder { get => renderOrder; set => Set(ref renderOrder, value); }
    public bool IsDepthClipEnabled { get => depthClip; set => Set(ref depthClip, value); }
    public int DepthBias { get => depthBias; set => Set(ref depthBias, value); }

    /// <summary>The instance stream (null: draw once at identity).</summary>
    internal InstanceBuffer? InstanceStream(D3DGraphics graphics)
    {
        if (instances == null) { instanceBuffer?.Dispose(); instanceBuffer = null; return null; }
        if (instanceBuffer is { } current && current.IsValidFor(graphics) && current.IsFor(instances)) return current;
        instanceBuffer?.Dispose(); instanceBuffer = null;
        return instanceBuffer = new InstanceBuffer(graphics, instances);
    }
    public override void Dispose() { instanceBuffer?.Dispose(); instanceBuffer = null; base.Dispose(); }

    // The world bounds of every instance, kept while the instances, the world matrix and the geometry stay.
    private (IReadOnlyList<Matrix4x4>? Instances, Matrix4x4 World, object? Geometry, Vector3 Min, Vector3 Max)? worldBounds;
    /// <summary>The axis-aligned world bounds of the node's geometry over all its instances (empty geometry: min +∞).</summary>
    internal (Vector3 Min, Vector3 Max) WorldBounds(Matrix4x4 world, object? geometry, (Vector3 Min, Vector3 Max) local)
    {
        if (worldBounds is { } cached && ReferenceEquals(cached.Instances, instances) && cached.World == world && ReferenceEquals(cached.Geometry, geometry))
            return (cached.Min, cached.Max);
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        if (float.IsFinite(local.Min.X))
        {
            if (instances is { } list)
                foreach (var instance in list)
                {
                    var (a, b) = RayPicking.TransformBounds(local.Min, local.Max, instance * world);
                    min = Vector3.Min(min, a); max = Vector3.Max(max, b);
                }
            else (min, max) = RayPicking.TransformBounds(local.Min, local.Max, world);
        }
        worldBounds = (instances, world, geometry, min, max);
        return (min, max);
    }
}

/// <summary>A triangle mesh with a <see cref="PreviewMaterial"/>; transparent meshes are drawn after the opaque ones, without depth
/// writes unless the material is plain.</summary>
internal sealed class MeshNode : GeometryNode
{
    private MeshData? geometry;
    private PreviewMaterial? material;
    private bool transparent;
    private CullMode cullMode;
    private readonly List<LineNode> followers = [];
    /// <summary>Replacing the geometry releases the replaced one's device copy at once (rebuilt geometry such as camera-facing
    /// links would otherwise hold device memory until collected; another node still drawing it uploads it again).</summary>
    public MeshData? Geometry
    {
        get => geometry;
        set
        {
            var previous = geometry; geometry = value;
            if (ReferenceEquals(previous, value)) return;
            previous?.ReleaseDeviceResources();
            Changed();
        }
    }
    public PreviewMaterial? Material { get => material; set => Set(ref material, value); }
    public bool IsTransparent { get => transparent; set => Set(ref transparent, value); }
    public CullMode CullMode { get => cullMode; set => Set(ref cullMode, value); }
    internal void Follow(LineNode line, bool follow) { if (follow) followers.Add(line); else followers.Remove(line); }
    private protected override void InstancesChanged() { foreach (var line in followers) line.FollowedChanged(); }
    private protected override void VisibilityChanged() { foreach (var line in followers) line.FollowedChanged(); }
    /// <summary>Also releases the device copies of its geometry and texture (shared ones upload again for their other users).</summary>
    public override void Dispose()
    {
        geometry?.ReleaseDeviceResources(); material?.DiffuseMap?.ReleaseDeviceResources();
        base.Dispose();
    }
}

/// <summary>
/// A screen-space ribbon per index pair: <see cref="Thickness"/> DIPs to each side (2 × Thickness DIPs wide), solid, alpha-blended,
/// depth-tested (LessEqual) with depth writes. A line following a mesh (<see cref="FollowInstancesOf"/>) uses that mesh's
/// instances and shows only where the mesh shows. Lines are never ray-picked.
/// </summary>
internal sealed class LineNode : GeometryNode
{
    private LineData? geometry;
    private MeshNode? followed;
    private Color color = Colors.Black;
    private double thickness = 1;
    public LineData? Geometry
    {
        get => geometry;
        set
        {
            var previous = geometry; geometry = value;
            if (ReferenceEquals(previous, value)) return;
            previous?.ReleaseDeviceResources();
            Changed();
        }
    }
    public Color Color { get => color; set => Set(ref color, value); }
    public double Thickness { get => thickness; set => Set(ref thickness, value); }
    public MeshNode? FollowInstancesOf
    {
        get => followed;
        set
        {
            followed?.Follow(this, false);
            followed = value;
            value?.Follow(this, true);
            FollowedChanged();
        }
    }
    internal void FollowedChanged() { if (followed != null) Instances = followed.Instances; Changed(); }
    private protected override bool Shown => base.Shown && followed is not { IsShown: false };
    public override void Dispose() { FollowInstancesOf = null; geometry?.ReleaseDeviceResources(); base.Dispose(); }
}

/// <summary>
/// The animation preview's reference plane at world Y = <see cref="Offset"/> (lines along X and Z). Drawn after opaque geometry and
/// before transparent; depth-tested (LessEqual) without writes, not clipped by the near/far planes, fading towards grazing
/// angles. Auto spacing: the largest power of ten not above (distance along the look ray to the plane) / <see cref="AutoSpacingRate"/>
/// (at least 1), kept unchanged while the look is more than 60° from vertical.
/// </summary>
internal sealed class GroundGridNode : Node
{
    private double offset, rate = 5, spacing = 10;
    private bool autoSpacing = true;
    private Color grid = Colors.DarkGray;
    public double Offset { get => offset; set => Set(ref offset, value); }
    public bool AutoSpacing { get => autoSpacing; set { autoSpacing = value; if (!value) ActualSpacing = (float)spacing; Changed(); } }
    public double AutoSpacingRate { get => rate; set => Set(ref rate, value); }
    public double GridSpacing { get => spacing; set { spacing = value; ActualSpacing = (float)value; Changed(); } }
    public Color GridColor { get => grid; set => Set(ref grid, value); }

    /// <summary>The spacing drawn (auto spacing keeps the last one while the look is more than 60° from vertical).</summary>
    internal float ActualSpacing { get; private set; } = 10;
    internal void UpdateSpacing(Vector3 eye, Vector3 look)
    {
        if (!autoSpacing) return;
        look = Vector3.Normalize(look);
        if (Math.Acos(Math.Abs(Vector3.Dot(Vector3.UnitY, look))) > Math.PI / 3) return;
        if (look.Y == 0) return;
        float distance = ((float)offset - eye.Y) / look.Y;
        if (!(distance >= 0) || !float.IsFinite(distance)) return;
        distance /= (float)rate;
        int n = 1;
        while (n < 1e6) { if (n > distance) { n /= 10; break; } n *= 10; }
        ActualSpacing = n == 0 ? 1 : n;
    }
}
