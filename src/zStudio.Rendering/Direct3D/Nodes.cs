using System.Collections;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using HelixDiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;
using GeometryModel3D = HelixToolkit.Wpf.SharpDX.GeometryModel3D;
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
    private static readonly ConditionalWeakTable<Element3D, Node> nodes = new();
    private Visibility visibility;
    private bool rendering = true, guide, hitTestVisible = true;
    private Matrix4x4? transform;
    private bool settingTransform;

    /// <param name="helix">The Helix adapter's element (Helix backend only; null in the Direct3D backend).</param>
    private protected Node(Element3D? helix)
    {
        HelixElement = helix;
        if (helix == null) return;
        nodes.AddOrUpdate(helix, this);
        visibility = helix.Visibility;
        if (helix.Transform is { } existing && !existing.Value.IsIdentity) transform = ToNumerics(existing.Value);
    }
    /// <summary>The Helix adapter's element, null in the Direct3D backend.</summary>
    internal Element3D? HelixElement { get; }
    internal static Node? FromHelix(object? element) => element is Element3D key && nodes.TryGetValue(key, out var node) ? node : null;
    internal INodeOwner? Owner { get; set; }
    /// <summary>The view this node is shown in (through its groups), or null while it is in none.</summary>
    internal RenderView? View => Owner?.View;
    /// <summary>The Direct3D backend redraws after a change (the Helix adapter's elements invalidate themselves).</summary>
    private protected void Changed() { if (HelixElement == null) View?.InvalidateRender(); }

    public Visibility Visibility { get => visibility; set { visibility = value; if (HelixElement != null) ApplyVisibility(); VisibilityChanged(); Changed(); } }
    public bool IsRendering
    {
        get => HelixElement != null && !guide ? HelixElement.IsRendering : rendering;
        set { rendering = value; if (HelixElement != null) ApplyRendering(); Changed(); }
    }
    public bool IsHitTestVisible
    {
        get => HelixElement?.IsHitTestVisible ?? hitTestVisible;
        set { hitTestVisible = value; if (HelixElement != null) HelixElement.IsHitTestVisible = value; }
    }
    /// <summary>Presentation chrome: hidden while the view's guides are hidden (Hide UI), whatever its own flags say.</summary>
    public bool IsGuide
    {
        get => guide;
        set
        {
            if (guide == value) return;
            if (value && HelixElement != null) rendering = HelixElement.IsRendering;
            guide = value;
            if (HelixElement != null) ApplyRendering();
            Changed();
        }
    }
    public Matrix4x4? Transform
    {
        get => transform;
        set
        {
            transform = value;
            if (HelixElement == null) { TransformSet(); Changed(); return; }
            settingTransform = true;
            try { HelixElement.Transform = value is { } matrix ? new MatrixTransform3D(ToWpf(matrix)) : Transform3D.Identity; }
            finally { settingTransform = false; }
        }
    }
    /// <summary>Direct3D backend: the node's transform was set (groups raise <see cref="GroupNode.TransformChanged"/>).</summary>
    private protected virtual void TransformSet() { }
    /// <summary>Helix adapter: the transform the backend itself gave the node (the gizmo moving its target).</summary>
    private protected void ReadBackTransform()
    {
        if (settingTransform || HelixElement == null) return;
        transform = HelixElement.Transform is { } current ? ToNumerics(current.Value) : null;
    }

    private protected virtual bool Shown => visibility == Visibility.Visible;
    /// <summary>The node's own visibility (its visibility, instances and followed mesh; not its flags or ancestors).</summary>
    internal bool IsShown => Shown;
    /// <summary>The node's own rendering flag (guides: before the view's guide state).</summary>
    internal bool RendersItself => rendering && (HelixElement == null || guide || HelixElement.IsRendering);
    private protected virtual void VisibilityChanged() { }
    internal void ApplyVisibility()
    {
        if (HelixElement != null) HelixElement.Visibility = Shown ? visibility : visibility == Visibility.Visible ? Visibility.Collapsed : visibility;
    }
    private void ApplyRendering() { if (HelixElement != null) HelixElement.IsRendering = rendering && (!guide || View?.GuidesVisible != false); }
    /// <summary>Guides follow their view's guide state again (attached, or the state changed).</summary>
    internal virtual void RefreshGuides() { if (guide) ApplyRendering(); }

    /// <summary>Makes a transform just set visible to ray picking before the next frame (the Helix adapter picks with the last frame's).</summary>
    public void UpdateHitTestTransform() => HelixElement?.SceneNode.UpdateAllTransformMatrix();
    public virtual void Dispose() => HelixElement?.Dispose();

    internal static Matrix3D ToWpf(Matrix4x4 m) => new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    internal static Matrix4x4 ToNumerics(Matrix3D m) => new((float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14, (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34, (float)m.OffsetX, (float)m.OffsetY, (float)m.OffsetZ, (float)m.M44);
}

/// <summary>An ordered node list: submission order, which also orders equal render orders and unsorted transparent nodes.</summary>
internal sealed class NodeCollection : IReadOnlyList<Node>
{
    private readonly List<Node> items = [];
    private readonly IList<Element3D>? backend;
    private readonly INodeOwner owner;
    internal NodeCollection(INodeOwner owner, IList<Element3D>? backend) { this.owner = owner; this.backend = backend; }
    public int Count => items.Count;
    public Node this[int index] => items[index];
    public void Add(Node node)
    {
        if (node.Owner != null) throw new InvalidOperationException("The node already belongs to a collection.");
        items.Add(node); node.Owner = owner;
        if (backend != null && node.HelixElement != null) backend.Add(node.HelixElement);
        node.RefreshGuides(); owner.View?.InvalidateRender();
    }
    public bool Remove(Node node)
    {
        if (!items.Remove(node)) return false;
        if (backend != null && node.HelixElement != null) backend.Remove(node.HelixElement);
        node.Owner = null; owner.View?.InvalidateRender(); return true;
    }
    public void Clear()
    {
        foreach (var node in items) node.Owner = null;
        items.Clear(); backend?.Clear(); owner.View?.InvalidateRender();
    }
    public bool Contains(Node node) => items.Contains(node);
    public IEnumerator<Node> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Draws its children in order, under its own transform and flags.</summary>
internal class GroupNode : Node, INodeOwner
{
    private EventHandler? backendTransform;
    private Action? transformChanged;
    public GroupNode() : this(RenderBackend.UseHelix ? new GroupModel3D() : null) { }
    private protected GroupNode(GroupElement3D? element) : base(element) => Children = new(this, element?.Children);
    public NodeCollection Children { get; }
    RenderView? INodeOwner.View => View;
    internal override void RefreshGuides() { base.RefreshGuides(); foreach (var child in Children) child.RefreshGuides(); }
    /// <summary>Raised after every change of <see cref="Node.Transform"/>, the node's own and the transform gizmo's on its target.</summary>
    public event Action? TransformChanged
    {
        add
        {
            if (transformChanged == null && HelixElement != null)
            {
                backendTransform = (_, _) => { ReadBackTransform(); transformChanged?.Invoke(); };
                DependencyPropertyDescriptor.FromProperty(Element3D.TransformProperty, HelixElement.GetType()).AddValueChanged(HelixElement, backendTransform);
            }
            transformChanged += value;
        }
        remove
        {
            transformChanged -= value;
            if (transformChanged == null) DetachTransform();
        }
    }
    private protected override void TransformSet() => transformChanged?.Invoke();
    private void DetachTransform()
    {
        if (backendTransform != null && HelixElement != null)
            DependencyPropertyDescriptor.FromProperty(Element3D.TransformProperty, HelixElement.GetType()).RemoveValueChanged(HelixElement, backendTransform);
        backendTransform = null; transformChanged = null;
    }
    public override void Dispose() { DetachTransform(); base.Dispose(); }
}

/// <summary>
/// Draws its opaque children first, then its transparent ones back to front (squared eye distance to each node's world bounds,
/// instances included), re-sorted every frame. With sorting off, children keep their order.
/// </summary>
internal sealed class SortingGroupNode : GroupNode
{
    private bool sorting = true;
    public SortingGroupNode() : base(RenderBackend.UseHelix ? new SortingGroupModel3D { SortTransparentOnly = true, SortingInterval = 0 } : null) { }
    public bool SortingEnabled
    {
        get => HelixElement is SortingGroupModel3D helix ? helix.EnableSorting : sorting;
        set { sorting = value; if (HelixElement is SortingGroupModel3D helix) helix.EnableSorting = value; else Changed(); }
    }
}

/// <summary>Drawn after everything else with the depth buffer cleared: its children depth-test only among themselves.</summary>
internal sealed class TopMostGroupNode() : GroupNode(RenderBackend.UseHelix ? new TopMostGroup3D() : null);

/// <summary>
/// Geometry drawn once per instance: world position is p · instance · <see cref="Node.Transform"/> (instances are placed in the
/// node's frame, as every CPU bounds, framing and inspection path composes them; the Helix adapter applies the transform first,
/// which differs whenever a node has both a non-identity transform and instances that do not commute with it, and zStudio builds
/// no node with both). Null
/// <see cref="Instances"/> draws once; an empty list draws (and hits) nothing. Opaque meshes and lines are drawn in ascending
/// <see cref="RenderOrder"/> (default 0; equal orders in submission order). <see cref="IsDepthClipEnabled"/> false lets the near
/// and far planes not clip it; <see cref="DepthBias"/> is the rasterizer's integer bias on 32-bit float depth.
/// </summary>
internal abstract class GeometryNode : Node
{
    private IReadOnlyList<Matrix4x4>? instances;
    private int renderOrder, depthBias;
    private bool depthClip = true;
    private InstanceBuffer? instanceBuffer;
    private protected GeometryNode(GeometryModel3D? element) : base(element) => instances = element?.Instances as IReadOnlyList<Matrix4x4>;
    private GeometryModel3D? Model => (GeometryModel3D?)HelixElement;
    public IReadOnlyList<Matrix4x4>? Instances
    {
        get => instances;
        set
        {
            instances = value;
            if (Model is { } model) { model.Instances = value == null ? null! : value as IList<Matrix4x4> ?? [.. value]; ApplyVisibility(); }
            InstancesChanged(); Changed();
        }
    }
    private protected virtual void InstancesChanged() { }
    private protected override bool Shown => base.Shown && instances is not { Count: 0 };
    public int RenderOrder { get => Model?.RenderOrder ?? renderOrder; set { renderOrder = value; if (Model is { } model) model.RenderOrder = value; else Changed(); } }
    public bool IsDepthClipEnabled { get => Model?.IsDepthClipEnabled ?? depthClip; set { depthClip = value; if (Model is { } model) model.IsDepthClipEnabled = value; else Changed(); } }
    public int DepthBias { get => Model?.DepthBias ?? depthBias; set { depthBias = value; if (Model is { } model) model.DepthBias = value; else Changed(); } }

    /// <summary>Direct3D backend: the instance stream (null: draw once at identity).</summary>
    internal InstanceBuffer? InstanceStream(D3DGraphics graphics)
    {
        if (instances == null) { instanceBuffer?.Dispose(); instanceBuffer = null; return null; }
        if (instanceBuffer is { IsReleased: false } current && current.Generation == graphics.Generation && ReferenceEquals(current.Source, instances) && current.Count == instances.Count) return current;
        instanceBuffer?.Dispose(); instanceBuffer = null;
        return instanceBuffer = new InstanceBuffer(graphics, instances);
    }
    public override void Dispose() { instanceBuffer?.Dispose(); instanceBuffer = null; base.Dispose(); }

    // Direct3D backend: the world bounds of every instance, kept while the instances, the world matrix and the geometry stay.
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
    public MeshNode() : base(RenderBackend.UseHelix ? new MeshGeometryModel3D { IsThrowingShadow = false } : null) { }
    /// <summary>The facade of a mesh the Helix adapter's gizmo built itself (its handles).</summary>
    internal MeshNode(MeshGeometryModel3D existing) : base(existing) { }
    private MeshGeometryModel3D? Mesh => (MeshGeometryModel3D?)HelixElement;
    /// <summary>The Direct3D backend releases a replaced geometry's device copy at once (rebuilt geometry such as camera-facing
    /// links would otherwise hold device memory until collected; another node still drawing it uploads it again).</summary>
    public MeshData? Geometry
    {
        get => geometry;
        set
        {
            var previous = geometry; geometry = value;
            if (Mesh is { } mesh) { mesh.Geometry = value?.HelixGeometry; return; }
            if (previous != null && !ReferenceEquals(previous, value)) previous.ReleaseDeviceResources();
            Changed();
        }
    }
    public PreviewMaterial? Material
    {
        get => Mesh is { } mesh ? material ??= mesh.Material is HelixDiffuseMaterial backend ? PreviewMaterial.Wrap(backend) : null : material;
        set { material = value; if (Mesh is { } mesh) mesh.Material = value?.HelixMaterial; else Changed(); }
    }
    public bool IsTransparent { get => Mesh?.IsTransparent ?? transparent; set { transparent = value; if (Mesh is { } mesh) mesh.IsTransparent = value; else Changed(); } }
    public CullMode CullMode
    {
        get => Mesh is { } mesh ? mesh.CullMode switch { SharpDX.Direct3D11.CullMode.Front => CullMode.Front, SharpDX.Direct3D11.CullMode.Back => CullMode.Back, _ => CullMode.None } : cullMode;
        set
        {
            cullMode = value;
            if (Mesh is { } mesh) mesh.CullMode = value switch { CullMode.Front => SharpDX.Direct3D11.CullMode.Front, CullMode.Back => SharpDX.Direct3D11.CullMode.Back, _ => SharpDX.Direct3D11.CullMode.None };
            else Changed();
        }
    }
    /// <summary>Whether triangles are filled (always: wireframe is drawn as polygon outline lines, never as a fill mode).</summary>
    internal bool FillsTriangles => Mesh is not { } mesh || mesh.FillMode == SharpDX.Direct3D11.FillMode.Solid;
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
    public LineNode() : base(RenderBackend.UseHelix ? new LineGeometryModel3D() : null) { }
    private LineGeometryModel3D? Line => (LineGeometryModel3D?)HelixElement;
    public LineData? Geometry
    {
        get => geometry;
        set
        {
            var previous = geometry; geometry = value;
            if (Line is { } line) { line.Geometry = value?.HelixGeometry; return; }
            if (previous != null && !ReferenceEquals(previous, value)) previous.ReleaseDeviceResources();
            Changed();
        }
    }
    public Color Color { get => Line?.Color ?? color; set { color = value; if (Line is { } line) line.Color = value; else Changed(); } }
    public double Thickness { get => Line?.Thickness ?? thickness; set { thickness = value; if (Line is { } line) line.Thickness = value; else Changed(); } }
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
    internal void FollowedChanged() { if (followed != null) Instances = followed.Instances; ApplyVisibility(); Changed(); }
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
    private double offset, rate = 5, spacing = 10, thickness = .05, fading = .2;
    private bool autoSpacing = true;
    private Color plane = Colors.Gray, grid = Colors.DarkGray;
    public GroundGridNode() : base(RenderBackend.UseHelix ? new AxisPlaneGridModel3D { UpAxis = Axis.Y, GridPattern = GridPattern.Grid, RenderShadowMap = false } : null) { }
    private AxisPlaneGridModel3D? Grid => (AxisPlaneGridModel3D?)HelixElement;
    public double Offset { get => Grid?.Offset ?? offset; set { offset = value; if (Grid is { } g) g.Offset = value; else Changed(); } }
    public bool AutoSpacing { get => Grid?.AutoSpacing ?? autoSpacing; set { autoSpacing = value; if (Grid is { } g) g.AutoSpacing = value; else { if (!value) ActualSpacing = (float)spacing; Changed(); } } }
    public double AutoSpacingRate { get => Grid?.AutoSpacingRate ?? rate; set { rate = value; if (Grid is { } g) g.AutoSpacingRate = value; else Changed(); } }
    public double GridSpacing { get => Grid?.GridSpacing ?? spacing; set { spacing = value; ActualSpacing = (float)value; if (Grid is { } g) g.GridSpacing = value; else Changed(); } }
    public double GridThickness { get => Grid?.GridThickness ?? thickness; set { thickness = value; if (Grid is { } g) g.GridThickness = value; } }
    public double FadingFactor { get => Grid?.FadingFactor ?? fading; set { fading = value; if (Grid is { } g) g.FadingFactor = value; } }
    public Color PlaneColor { get => Grid?.PlaneColor ?? plane; set { plane = value; if (Grid is { } g) g.PlaneColor = value; } }
    public Color GridColor { get => Grid?.GridColor ?? grid; set { grid = value; if (Grid is { } g) g.GridColor = value; else Changed(); } }
    public bool DrawsGridLines => Grid is not { } g || g.GridPattern == GridPattern.Grid;

    /// <summary>Direct3D backend: the spacing drawn (auto spacing keeps the last one while the look is more than 60° from vertical).</summary>
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
