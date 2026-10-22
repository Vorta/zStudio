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
    private bool rendering = true, guide;
    private Matrix4x4? transform;
    private bool settingTransform;

    private protected Node(Element3D element)
    {
        HelixElement = element; nodes.AddOrUpdate(element, this);
        visibility = element.Visibility;
        if (element.Transform is { } existing && !existing.Value.IsIdentity) transform = ToNumerics(existing.Value);
    }
    internal Element3D HelixElement { get; }
    internal static Node? FromHelix(object? element) => element is Element3D key && nodes.TryGetValue(key, out var node) ? node : null;
    internal INodeOwner? Owner { get; set; }
    /// <summary>The view this node is shown in (through its groups), or null while it is in none.</summary>
    internal RenderView? View => Owner?.View;

    public Visibility Visibility { get => visibility; set { visibility = value; ApplyVisibility(); VisibilityChanged(); } }
    public bool IsRendering
    {
        get => guide ? rendering : HelixElement.IsRendering;
        set { rendering = value; ApplyRendering(); }
    }
    public bool IsHitTestVisible { get => HelixElement.IsHitTestVisible; set => HelixElement.IsHitTestVisible = value; }
    /// <summary>Presentation chrome: hidden while the view's guides are hidden (Hide UI), whatever its own flags say.</summary>
    public bool IsGuide
    {
        get => guide;
        set { if (guide == value) return; if (value) rendering = HelixElement.IsRendering; guide = value; ApplyRendering(); }
    }
    public Matrix4x4? Transform
    {
        get => transform;
        set
        {
            transform = value; settingTransform = true;
            try { HelixElement.Transform = value is { } matrix ? new MatrixTransform3D(ToWpf(matrix)) : Transform3D.Identity; }
            finally { settingTransform = false; }
        }
    }
    /// <summary>The transform the backend itself gave the node (the gizmo moving its target).</summary>
    private protected void ReadBackTransform()
    {
        if (settingTransform) return;
        transform = HelixElement.Transform is { } current && !current.Value.IsIdentity ? ToNumerics(current.Value) : null;
    }

    private protected virtual bool Shown => visibility == Visibility.Visible;
    private protected virtual void VisibilityChanged() { }
    internal void ApplyVisibility() => HelixElement.Visibility = Shown ? visibility : visibility == Visibility.Visible ? Visibility.Collapsed : visibility;
    private void ApplyRendering() => HelixElement.IsRendering = rendering && (!guide || View?.GuidesVisible != false);
    /// <summary>Guides follow their view's guide state again (attached, or the state changed).</summary>
    internal virtual void RefreshGuides() { if (guide) ApplyRendering(); }

    public virtual void Dispose() => HelixElement.Dispose();

    internal static Matrix3D ToWpf(Matrix4x4 m) => new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    internal static Matrix4x4 ToNumerics(Matrix3D m) => new((float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14, (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34, (float)m.OffsetX, (float)m.OffsetY, (float)m.OffsetZ, (float)m.M44);
}

/// <summary>An ordered node list: submission order, which also orders equal render orders and unsorted transparent nodes.</summary>
internal sealed class NodeCollection : IReadOnlyList<Node>
{
    private readonly List<Node> items = [];
    private readonly IList<Element3D> backend;
    private readonly INodeOwner owner;
    internal NodeCollection(INodeOwner owner, IList<Element3D> backend) { this.owner = owner; this.backend = backend; }
    public int Count => items.Count;
    public Node this[int index] => items[index];
    public void Add(Node node)
    {
        if (node.Owner != null) throw new InvalidOperationException("The node already belongs to a collection.");
        items.Add(node); node.Owner = owner; backend.Add(node.HelixElement); node.RefreshGuides();
    }
    public bool Remove(Node node)
    {
        if (!items.Remove(node)) return false;
        backend.Remove(node.HelixElement); node.Owner = null; return true;
    }
    public void Clear() { foreach (var node in items) node.Owner = null; items.Clear(); backend.Clear(); }
    public bool Contains(Node node) => items.Contains(node);
    public IEnumerator<Node> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Draws its children in order, under its own transform and flags.</summary>
internal class GroupNode : Node, INodeOwner
{
    private EventHandler? backendTransform;
    private Action? transformChanged;
    public GroupNode() : this(new GroupModel3D()) { }
    private protected GroupNode(GroupElement3D element) : base(element) => Children = new(this, element.Children);
    public NodeCollection Children { get; }
    RenderView? INodeOwner.View => View;
    internal override void RefreshGuides() { base.RefreshGuides(); foreach (var child in Children) child.RefreshGuides(); }
    /// <summary>Raised after every change of <see cref="Node.Transform"/>, the node's own and the transform gizmo's on its target.</summary>
    public event Action? TransformChanged
    {
        add
        {
            if (transformChanged == null)
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
    private void DetachTransform()
    {
        if (backendTransform == null) return;
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
    public SortingGroupNode() : base(new SortingGroupModel3D { SortTransparentOnly = true, SortingInterval = 0 }) { }
    public bool SortingEnabled { get => ((SortingGroupModel3D)HelixElement).EnableSorting; set => ((SortingGroupModel3D)HelixElement).EnableSorting = value; }
}

/// <summary>Drawn after everything else with the depth buffer cleared: its children depth-test only among themselves.</summary>
internal sealed class TopMostGroupNode() : GroupNode(new TopMostGroup3D());

/// <summary>
/// Geometry drawn once per instance: world position is p · <see cref="Node.Transform"/> · instance. Null
/// <see cref="Instances"/> draws once; an empty list draws (and hits) nothing. Opaque meshes and lines are drawn in ascending
/// <see cref="RenderOrder"/> (default 0). <see cref="IsDepthClipEnabled"/> false lets the near and far planes not clip it;
/// <see cref="DepthBias"/> is the rasterizer's integer bias on 32-bit float depth.
/// </summary>
internal abstract class GeometryNode : Node
{
    private IReadOnlyList<Matrix4x4>? instances;
    private protected GeometryNode(GeometryModel3D element) : base(element) => instances = element.Instances as IReadOnlyList<Matrix4x4>;
    private GeometryModel3D Model => (GeometryModel3D)HelixElement;
    public IReadOnlyList<Matrix4x4>? Instances
    {
        get => instances;
        set
        {
            instances = value;
            Model.Instances = value == null ? null! : value as IList<Matrix4x4> ?? [.. value];
            ApplyVisibility(); InstancesChanged();
        }
    }
    private protected virtual void InstancesChanged() { }
    private protected override bool Shown => base.Shown && instances is not { Count: 0 };
    internal bool IsShown => Shown;
    public int RenderOrder { get => Model.RenderOrder; set => Model.RenderOrder = value; }
    public bool IsDepthClipEnabled { get => Model.IsDepthClipEnabled; set => Model.IsDepthClipEnabled = value; }
    public int DepthBias { get => Model.DepthBias; set => Model.DepthBias = value; }
}

/// <summary>A triangle mesh with a <see cref="PreviewMaterial"/>; transparent meshes are drawn after the opaque ones without depth writes.</summary>
internal sealed class MeshNode : GeometryNode
{
    private MeshData? geometry;
    private PreviewMaterial? material;
    private readonly List<LineNode> followers = [];
    public MeshNode() : base(new MeshGeometryModel3D { IsThrowingShadow = false }) { }
    /// <summary>The facade of a mesh the backend built itself (the transform gizmo's handles).</summary>
    internal MeshNode(MeshGeometryModel3D existing) : base(existing) { }
    private MeshGeometryModel3D Mesh => (MeshGeometryModel3D)HelixElement;
    public MeshData? Geometry { get => geometry; set { geometry = value; Mesh.Geometry = value?.HelixGeometry; } }
    public PreviewMaterial? Material
    {
        get => material ??= Mesh.Material is HelixDiffuseMaterial backend ? PreviewMaterial.Wrap(backend) : null;
        set { material = value; Mesh.Material = value?.HelixMaterial; }
    }
    public bool IsTransparent { get => Mesh.IsTransparent; set => Mesh.IsTransparent = value; }
    public CullMode CullMode
    {
        get => Mesh.CullMode switch { SharpDX.Direct3D11.CullMode.Front => CullMode.Front, SharpDX.Direct3D11.CullMode.Back => CullMode.Back, _ => CullMode.None };
        set => Mesh.CullMode = value switch { CullMode.Front => SharpDX.Direct3D11.CullMode.Front, CullMode.Back => SharpDX.Direct3D11.CullMode.Back, _ => SharpDX.Direct3D11.CullMode.None };
    }
    /// <summary>Whether triangles are filled (always: wireframe is drawn as polygon outline lines, never as a fill mode).</summary>
    internal bool FillsTriangles => Mesh.FillMode == SharpDX.Direct3D11.FillMode.Solid;
    internal void Follow(LineNode line, bool follow) { if (follow) followers.Add(line); else followers.Remove(line); }
    private protected override void InstancesChanged() { foreach (var line in followers) line.FollowedChanged(); }
    private protected override void VisibilityChanged() { foreach (var line in followers) line.FollowedChanged(); }
}

/// <summary>
/// A screen-space ribbon per index pair: <see cref="Thickness"/> DIPs to each side (2 × Thickness DIPs wide), solid, alpha-blended,
/// depth-tested (LessEqual) with depth writes. A line following a mesh (<see cref="FollowInstancesOf"/>) uses that mesh's
/// instances and shows only where the mesh shows.
/// </summary>
internal sealed class LineNode : GeometryNode
{
    private LineData? geometry;
    private MeshNode? followed;
    public LineNode() : base(new LineGeometryModel3D()) { }
    private LineGeometryModel3D Line => (LineGeometryModel3D)HelixElement;
    public LineData? Geometry { get => geometry; set { geometry = value; Line.Geometry = value?.HelixGeometry; } }
    public Color Color { get => Line.Color; set => Line.Color = value; }
    public double Thickness { get => Line.Thickness; set => Line.Thickness = value; }
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
    internal void FollowedChanged() { if (followed != null) Instances = followed.Instances; ApplyVisibility(); }
    private protected override bool Shown => base.Shown && followed is not { IsShown: false };
    public override void Dispose() { FollowInstancesOf = null; base.Dispose(); }
}

/// <summary>
/// The animation preview's reference plane at world Y = <see cref="Offset"/> (lines along X and Z). Drawn after opaque geometry and
/// before transparent; depth-tested (LessEqual) without writes, not clipped by the near/far planes, fading towards grazing
/// angles. Auto spacing: the largest power of ten not above (distance along the look ray to the plane) / <see cref="AutoSpacingRate"/>
/// (at least 1), kept unchanged while the look is more than 60° from vertical.
/// </summary>
internal sealed class GroundGridNode : Node
{
    public GroundGridNode() : base(new AxisPlaneGridModel3D { UpAxis = Axis.Y, GridPattern = GridPattern.Grid, RenderShadowMap = false }) { }
    private AxisPlaneGridModel3D Grid => (AxisPlaneGridModel3D)HelixElement;
    public double Offset { get => Grid.Offset; set => Grid.Offset = value; }
    public bool AutoSpacing { get => Grid.AutoSpacing; set => Grid.AutoSpacing = value; }
    public double AutoSpacingRate { get => Grid.AutoSpacingRate; set => Grid.AutoSpacingRate = value; }
    public double GridSpacing { get => Grid.GridSpacing; set => Grid.GridSpacing = value; }
    public double GridThickness { get => Grid.GridThickness; set => Grid.GridThickness = value; }
    public double FadingFactor { get => Grid.FadingFactor; set => Grid.FadingFactor = value; }
    public Color PlaneColor { get => Grid.PlaneColor; set => Grid.PlaneColor = value; }
    public Color GridColor { get => Grid.GridColor; set => Grid.GridColor = value; }
    public bool DrawsGridLines => Grid.GridPattern == GridPattern.Grid;
}
