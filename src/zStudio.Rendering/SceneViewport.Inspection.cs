using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using zStudio.Core;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed record SceneInspection(string Target, int Node, int Model, int Material, long? RuntimeInstance,
    string? AiNode, Vector3? Surface, Vector3? Normal, Vector3? Origin, bool Active, double? Time)
{
    /// <summary>Original polygon in the inspected model, resolved from the actual hit triangle.</summary>
    public int? Polygon { get; init; }
    /// <summary>Selectable instance owning the physical hit. Node/Model/Polygon retain the actual surface.</summary>
    public int? OwnerNode { get; init; }
    /// <summary>An explicit tree node, whose selection must not be promoted to the picked object's owner.</summary>
    public bool ExactSelection { get; init; }
    public int SelectionNode => OwnerNode ?? Node;
}

public sealed partial class SceneViewport
{
    private sealed record InspectionMesh(string Id, int Material, long? Runtime, int Node, int Model);
    private readonly Dictionary<MeshNode, InspectionMesh> inspectionMeshes = [];
    private readonly Dictionary<MeshNode, int[]> inspectionPolygons = [];
    private readonly Dictionary<int, Vector3> tankPositions = [];
    private readonly Grid inspectionHost = new();
    private long inspectionSerial;
    private sealed record InspectionStamp(Point? Pointer, ViewPose Pose, long Serial, string? Selected, bool InputBusy, double Width, double Height);
    private InspectionStamp? inspectionStamp;
    public string? InspectionSourcePath { get; private set; }
    /// <summary>The rendered 3D surface alone (without overlays), also the blurred backdrop behind the inspection panel.</summary>
    public FrameworkElement RenderSurface => viewport;
    private Point? inspectionPointer;
    private DateTime inspectionTick;
    public SceneInspection? HoverInspection { get; private set; }
    public SceneInspection? SelectedInspection { get; private set; }
    public event Action? InspectionChanged;
    /// <summary>Explicit card/source deselection, distinct from renderer replacement.</summary>
    public event Action? InspectionSelectionCleared;
    public Func<bool>? CanChangeInspection { get; set; }
    /// <summary>Source editor's prepared instance ownership; identity for other preview modes.</summary>
    public Func<int, int>? SelectionOwner { get; set; }
    /// <summary>Immutable source-editor presentation overrides. Null uses initialized mission visibility.</summary>
    public Func<int, bool?>? NodeVisibility { get; set; }
    public IReadOnlySet<int> VisibleSceneNodes { get; private set; } = new HashSet<int>();
    public int SelectionNode(int node) => node < 0 ? node : SelectionOwner?.Invoke(node) ?? node;
    private UIElement? inspectionContent;
    public UIElement? InspectionContent
    {
        get => inspectionContent;
        set
        {
            if (inspectionContent != null) presentationOverlays.Children.Remove(inspectionContent);
            inspectionContent = value;
            ReserveInspectionPanel(Rect.Empty);
            if (value != null) presentationOverlays.Children.Add(value);
        }
    }

    private void ConfigureInspection()
    {
        inspectionHost.Children.Add(viewport); inspectionHost.Children.Add(presentationOverlays); Content = inspectionHost;
        viewport.MouseMove += (_, e) => { inspectionPointer = e.GetPosition(viewport); view.InvalidateRender(); };
        viewport.MouseLeave += (_, _) => { inspectionPointer = null; HoverInspection = null; InspectionChanged?.Invoke(); };
    }
    private static bool IsInspectionInput(DependencyObject? child, DependencyObject root)
    {
        while (child != null)
        {
            if (ReferenceEquals(child, root)) return true;
            // An open drop-down's list is in its own window: continue from the popup that owns it.
            child = LogicalTreeHelper.GetParent(child) is System.Windows.Controls.Primitives.Popup popup ? popup
                : child is System.Windows.Media.Visual or Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        }
        return false;
    }
    private void RegisterInspectionMesh(MeshNode mesh, int material, long? runtime = null, int node = -1, int model = -1)
        => inspectionMeshes[mesh] = new(Guid.NewGuid().ToString("N"), material, runtime, node, model);

    public SceneInspection? ProbeInspection(Point point)
    {
        if (!IsNavigationPointInside(point) || IsFlyActive || IsPickupDragging) return null;
        if (PickAiNode(point) is { } ai)
        {
            var marker = InspectAi(ai);
            return marker != null && TrySceneSurface(point, out var under) ? marker with { Surface = under!.Point, Normal = under.Normal } : marker;
        }
        return TrySceneSurface(point, out var hit) ? InspectHit(hit!) : null;
    }
    private SceneInspection? InspectAi(string id)
    {
        if (!AiVisible || AiNetworks.Find(id) is not { } item || AiNetworkFilter != null && item.Network.Id != AiNetworkFilter) return null;
        return new("ai:" + AiNetworks.Id + ":" + id, -1, -1, -1, null, id, null, null, item.Node.Position, true, null);
    }
    private SceneInspection? InspectHit(RenderHit hit)
    {
        if (hit.Node is not MeshNode mesh || !inspectionMeshes.TryGetValue(mesh, out var meta)) return null;
        int instance = hit.Tag ?? 0;
        if (meta.Runtime == null && visiblePlacements.TryGetValue(mesh, out var visible) && instance >= 0 && instance < visible.Length)
            instance = Array.IndexOf(placements[mesh], visible[instance]);
        var result = InspectMesh(mesh, meta, instance);
        // A hit keeps its triangle's vertex tuple. GeometryBuilder duplicates vertices at polygon
        // boundaries, so three agreeing vertex identities resolve the exact face in constant time.
        int? polygon = hit.Triangle is { } triangle && inspectionPolygons.TryGetValue(mesh, out var polygons) &&
            triangle.A >= 0 && triangle.A < polygons.Length && triangle.B >= 0 && triangle.B < polygons.Length &&
            triangle.C >= 0 && triangle.C < polygons.Length && polygons[triangle.A] == polygons[triangle.B] &&
            polygons[triangle.A] == polygons[triangle.C] ? polygons[triangle.A] : null;
        return result == null ? null : result with { Surface = hit.Point, Normal = hit.Normal, Polygon = polygon };
    }
    private SceneInspection? InspectMesh(MeshNode mesh, InspectionMesh meta, int instance)
    {
        int node = meta.Node, model = meta.Model; Vector3 origin;
        if (meta.Runtime == null)
        {
            if (!placements.TryGetValue(mesh, out var rows) || instance < 0 || instance >= rows.Length) return null;
            node = rows[instance].NodeIndex; model = rows[instance].ModelIndex; origin = rows[instance].Transform.Translation;
            var transformed = ToWpf(mesh.Transform ?? Matrix4x4.Identity).Transform(new Point3D(origin.X, origin.Y, origin.Z));
            origin = new((float)transformed.X, (float)transformed.Y, (float)transformed.Z);
        }
        else
        {
            if (animationFrame?.Nodes.FirstOrDefault(n => n.Id == meta.Runtime) is not { } pose) return null;
            origin = pose.Transform.Translation;
        }
        bool active = mesh.IsRendering && mesh.Visibility == Visibility.Visible && mesh.IsDepthClipEnabled;
        if (meta.Runtime == null) active &= visiblePlacements.TryGetValue(mesh, out var visible) && visible.Contains(placements[mesh][instance]);
        return new(meta.Id + ":" + instance, node, model, meta.Material, meta.Runtime, null, null, null, active ? origin : null, active, animationFrame?.Time)
        { OwnerNode = SelectionNode(node) };
    }
    public SceneInspection? InspectTarget(string target)
    {
        if (target.StartsWith("ai:" + AiNetworks.Id + ":", StringComparison.Ordinal)) return InspectAi(target[(AiNetworks.Id.Length + 4)..]);
        int colon = target.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(target.AsSpan(colon + 1), out int instance)) return null;
        string id = target[..colon];
        foreach (var (mesh, meta) in inspectionMeshes)
            if (meta.Id == id) return InspectMesh(mesh, meta, instance);
        return null;
    }
    public bool SelectInspection(string? target, bool guard = true, int? exactNode = null)
    {
        var next = target == null ? null : InspectTarget(target);
        if (target != null && next == null || IsFlyActive || IsPickupDragging || guard && CanChangeInspection?.Invoke() == false) return false;
        if (next != null && exactNode is int exact) next = next with { OwnerNode = exact, ExactSelection = true };
        SelectedInspection = next;
        if (next == null) { SelectAiNode(null); SelectPickup(null); FramingSelection = null; InspectionSelectionCleared?.Invoke(); }
        else if (next.AiNode is { } ai) SelectAiNode(ai);
        else if (next is { Node: >= 0 } node) { SelectFramingNode(PickupAt(node.SelectionNode)?.Root ?? node.SelectionNode); NodeSelected?.Invoke(node.SelectionNode); }
        RefreshPickupSelection(); InspectionChanged?.Invoke(); return true;
    }
    public bool SelectInspectionNode(int node, long? runtime = null, bool exact = false)
    {
        if (!CanInspectNode(node)) return false;
        var members = exact ? Subtrees([node]) : null;
        bool Matches(int candidate) => members?.Contains(candidate) ?? SelectionNode(candidate) == SelectionNode(node);
        foreach (var (mesh, meta) in inspectionMeshes)
        {
            if (runtime != null && meta.Runtime != runtime) continue;
            if (meta.Runtime != null && Matches(meta.Node) && InspectMesh(mesh, meta, 0)?.Active == true) return SelectInspection(meta.Id + ":0", exactNode: exact ? node : null);
            if (meta.Runtime == null && placements.TryGetValue(mesh, out var rows))
                for (int i = 0; i < rows.Length; i++) if (Matches(rows[i].NodeIndex) && visiblePlacements[mesh].Contains(rows[i])) return SelectInspection(meta.Id + ":" + i, exactNode: exact ? node : null);
        }
        return false;
    }
    private bool HandleInspectionClick(Point point, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsFlyActive || IsPickupDragging) return false;
        // Consume the scene click even when a draft vetoes selection, before the view's own pointer handling.
        var hit = ProbeInspection(point);
        if (hit == null) return false;
        viewport.Focus();
        if (SelectInspection(hit.Target) && SelectedInspection != null)
        { SelectedInspection = SelectedInspection with { Polygon = hit.Polygon, Surface = hit.Surface, Normal = hit.Normal }; InspectionChanged?.Invoke(); }
        return true;
    }
    private void RefreshInspection()
    {
        // Bounded to 20 Hz, including moving animation/camera under a stationary pointer.
        var now = DateTime.UtcNow;
        if ((now - inspectionTick).TotalMilliseconds < 50) return;
        inspectionTick = now;
        bool busy = PresentationUiHidden || IsFlyActive || IsPickupDragging || Mouse.LeftButton != MouseButtonState.Released || Mouse.MiddleButton != MouseButtonState.Released || Mouse.RightButton != MouseButtonState.Released;
        var stamp = new InspectionStamp(inspectionPointer, CaptureView(), inspectionSerial, SelectedInspection?.Target, busy, viewport.ActualWidth, viewport.ActualHeight);
        if (stamp == inspectionStamp) return;
        inspectionStamp = stamp;
        if (inspectionPointer is { } p && !busy)
            HoverInspection = ProbeInspection(p);
        else HoverInspection = null;
        if (SelectedInspection is { } selected)
        {
            SelectedInspection = InspectTarget(selected.Target) is { } refreshed
                ? refreshed with { Polygon = selected.Polygon, Surface = selected.Surface, Normal = selected.Normal,
                    OwnerNode = selected.ExactSelection ? selected.OwnerNode : refreshed.OwnerNode, ExactSelection = selected.ExactSelection }
                : selected with { Active = false, Origin = null, Surface = null, Normal = null };
            if (selected.AiNode != null) RefreshPickupSelection();
        }
        if (inspectionPointer != null || SelectedInspection != null) InspectionChanged?.Invoke();
    }
    public Point InspectionAnchor(SceneInspection inspection)
    {
        if (inspection.Origin is not { } p || view.Camera is not RenderCamera camera ||
            Vector3D.DotProduct(new Point3D(p.X, p.Y, p.Z) - camera.Position, camera.LookDirection) <= 0) return new(double.NaN, double.NaN);
        return view.Project(new Point3D(p.X, p.Y, p.Z));
    }
    public MissionActor? ActorAt(int node) => Mission?.ActorAt(node);
    public void SetMissionCoordinates(PickupPlacementEditSession edits)
    {
        placementEdits = edits; UpdateMissionTransformPreview();
    }
    private void ClearInspection()
    {
        inspectionMeshes.Clear(); inspectionPolygons.Clear(); ClearZonePainting(); tankPositions.Clear(); ++inspectionSerial;
        inspectionStamp = null; InspectionSourcePath = null;
        HoverInspection = SelectedInspection = null; inspectionPointer = null; InspectionChanged?.Invoke();
    }
}
