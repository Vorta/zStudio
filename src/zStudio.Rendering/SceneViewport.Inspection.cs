using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using HelixToolkit;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Rendering;

public sealed record SceneInspection(string Target, int Node, int Model, int Material, long? RuntimeInstance,
    string? AiNode, Vector3? Surface, Vector3? Normal, Vector3? Origin, bool Active, double? Time);

public sealed partial class SceneViewport
{
    private sealed record InspectionMesh(string Id, int Material, long? Runtime, int Node, int Model);
    private readonly Dictionary<MeshGeometryModel3D, InspectionMesh> inspectionMeshes = [];
    private readonly Dictionary<int, MissionActor?> inspectionActors = [];
    private bool inspectionActorsIndexed;
    private readonly Dictionary<int, Vector3> tankPositions = [];
    private readonly Grid inspectionHost = new();
    private long inspectionSerial;
    private sealed record InspectionStamp(Point? Pointer, ViewPose Pose, long Serial, string? Selected, bool InputBusy, double Width, double Height);
    private InspectionStamp? inspectionStamp;
    public string? InspectionSourcePath { get; private set; }
    internal FrameworkElement RenderSurface => viewport;
    private Point? inspectionPointer;
    private DateTime inspectionTick;
    public SceneInspection? HoverInspection { get; private set; }
    public SceneInspection? SelectedInspection { get; private set; }
    public event Action? InspectionChanged;
    /// <summary>Explicit card/source deselection, distinct from renderer replacement.</summary>
    public event Action? InspectionSelectionCleared;
    public Func<bool>? CanChangeInspection { get; set; }
    public UIElement? InspectionContent
    {
        get => inspectionHost.Children.Count > 1 ? inspectionHost.Children[1] : null;
        set { while (inspectionHost.Children.Count > 1) inspectionHost.Children.RemoveAt(1); if (value != null) inspectionHost.Children.Add(value); }
    }

    private void ConfigureInspection()
    {
        inspectionHost.Children.Add(viewport); Content = inspectionHost;
        viewport.MouseMove += (_, e) => { inspectionPointer = e.GetPosition(viewport); viewport.InvalidateRender(); };
        viewport.MouseLeave += (_, _) => { inspectionPointer = null; HoverInspection = null; InspectionChanged?.Invoke(); };
    }
    private static bool IsInspectionInput(DependencyObject? child, DependencyObject root)
    {
        while (child != null)
        {
            if (ReferenceEquals(child, root)) return true;
            child = child is System.Windows.Media.Visual or Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        }
        return false;
    }
    private void RegisterInspectionMesh(MeshGeometryModel3D mesh, int material, long? runtime = null, int node = -1, int model = -1)
        => inspectionMeshes[mesh] = new(Guid.NewGuid().ToString("N"), material, runtime, node, model);

    public SceneInspection? ProbeInspection(Point point)
    {
        if (!IsNavigationPointInside(point) || IsFlyActive || IsPickupDragging) return null;
        if (PickAiNode(point) is { } ai)
        {
            var marker = InspectAi(ai);
            return marker != null && TrySceneSurface(point, out var under) ? marker with { Surface = under!.PointHit, Normal = under.NormalAtHit } : marker;
        }
        return TrySceneSurface(point, out var hit) ? InspectHit(hit!) : null;
    }
    private SceneInspection? InspectAi(string id)
    {
        if (!AiVisible || AiNetworks.Find(id) is not { } item || AiNetworkFilter != null && item.Network.Id != AiNetworkFilter) return null;
        return new("ai:" + AiNetworks.Id + ":" + id, -1, -1, -1, null, id, null, null, item.Node.Position, true, null);
    }
    private SceneInspection? InspectHit(HitTestResult hit)
    {
        if (hit.ModelHit is not MeshGeometryModel3D mesh || !inspectionMeshes.TryGetValue(mesh, out var meta)) return null;
        int instance = hit.Tag is int i ? i : 0;
        if (meta.Runtime == null && visiblePlacements.TryGetValue(mesh, out var visible) && instance >= 0 && instance < visible.Length)
            instance = Array.IndexOf(placements[mesh], visible[instance]);
        var result = InspectMesh(mesh, meta, instance);
        return result == null ? null : result with { Surface = hit.PointHit, Normal = hit.NormalAtHit };
    }
    private SceneInspection? InspectMesh(MeshGeometryModel3D mesh, InspectionMesh meta, int instance)
    {
        int node = meta.Node, model = meta.Model; Vector3 origin;
        if (meta.Runtime == null)
        {
            if (!placements.TryGetValue(mesh, out var rows) || instance < 0 || instance >= rows.Length) return null;
            node = rows[instance].NodeIndex; model = rows[instance].ModelIndex; origin = rows[instance].Transform.Translation;
            var transformed = mesh.Transform.Transform(new Point3D(origin.X, origin.Y, origin.Z));
            origin = new((float)transformed.X, (float)transformed.Y, (float)transformed.Z);
        }
        else
        {
            if (animationFrame?.Nodes.FirstOrDefault(n => n.Id == meta.Runtime) is not { } pose) return null;
            origin = pose.Transform.Translation;
        }
        bool active = mesh.IsRendering && mesh.Visibility == Visibility.Visible && mesh.IsDepthClipEnabled;
        if (meta.Runtime == null) active &= visiblePlacements.TryGetValue(mesh, out var visible) && visible.Contains(placements[mesh][instance]);
        return new(meta.Id + ":" + instance, node, model, meta.Material, meta.Runtime, null, null, null, active ? origin : null, active, animationFrame?.Time);
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
    public bool SelectInspection(string? target, bool guard = true)
    {
        var next = target == null ? null : InspectTarget(target);
        if (target != null && (!InspectionSelectionEnabled || next == null) || IsFlyActive || IsPickupDragging || guard && CanChangeInspection?.Invoke() == false) return false;
        SelectedInspection = next;
        if (next == null) { SelectAiNode(null); SelectPickup(null, false, true); FramingSelection = null; InspectionSelectionCleared?.Invoke(); }
        else if (next.AiNode is { } ai) SelectAiNode(ai);
        else if (next is { Node: >= 0 } node) { SelectFramingNode(PickupAt(node.Node)?.Root ?? node.Node); NodeSelected?.Invoke(node.Node); }
        RefreshPickupSelection(); InspectionChanged?.Invoke(); return true;
    }
    public bool SelectInspectionNode(int node, long? runtime = null)
    {
        foreach (var (mesh, meta) in inspectionMeshes)
        {
            if (runtime != null && meta.Runtime != runtime) continue;
            if (meta.Runtime != null && meta.Node == node && InspectMesh(mesh, meta, 0)?.Active == true) return SelectInspection(meta.Id + ":0");
            if (meta.Runtime == null && placements.TryGetValue(mesh, out var rows))
                for (int i = 0; i < rows.Length; i++) if (rows[i].NodeIndex == node && visiblePlacements[mesh].Contains(rows[i])) return SelectInspection(meta.Id + ":" + i);
        }
        return false;
    }
    private bool HandleInspectionClick(Point point, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsFlyActive || IsPickupDragging) return false;
        if (!InspectionSelectionEnabled) return true;
        // Consume the scene click even when a draft vetoes selection, before Helix's callbacks.
        var hit = ProbeInspection(point);
        if (hit == null) return false;
        viewport.Focus(); SelectInspection(hit.Target); return true;
    }
    private void RefreshInspection()
    {
        // Bounded to 20 Hz, including moving animation/camera under a stationary pointer.
        var now = DateTime.UtcNow;
        if ((now - inspectionTick).TotalMilliseconds < 50) return;
        inspectionTick = now;
        bool busy = IsFlyActive || IsPickupDragging || Mouse.LeftButton != MouseButtonState.Released || Mouse.MiddleButton != MouseButtonState.Released || Mouse.RightButton != MouseButtonState.Released;
        var stamp = new InspectionStamp(inspectionPointer, CaptureView(), inspectionSerial, SelectedInspection?.Target, busy, viewport.ActualWidth, viewport.ActualHeight);
        if (stamp == inspectionStamp) return;
        inspectionStamp = stamp;
        if (inspectionPointer is { } p && !busy)
            HoverInspection = ProbeInspection(p);
        else HoverInspection = null;
        if (SelectedInspection is { } selected)
        {
            SelectedInspection = InspectTarget(selected.Target) ?? selected with { Active = false, Origin = null, Surface = null, Normal = null };
            if (selected.AiNode != null) RefreshPickupSelection();
        }
        if (inspectionPointer != null || SelectedInspection != null) InspectionChanged?.Invoke();
    }
    public Point InspectionAnchor(SceneInspection inspection)
    {
        if (inspection.Origin is not { } p || viewport.Camera is not ProjectionCamera camera ||
            Vector3D.DotProduct(new Point3D(p.X, p.Y, p.Z) - camera.Position, camera.LookDirection) <= 0) return new(double.NaN, double.NaN);
        return viewport.Project(new Point3D(p.X, p.Y, p.Z));
    }
    public MissionActor? ActorAt(int node)
    {
        if (Mission == null) return null;
        if (!inspectionActorsIndexed)
        {
            foreach (var actor in Mission.Actors)
            {
                Stack<int> pending = new([actor.Root]); HashSet<int> seen = [];
                while (pending.TryPop(out int child))
                {
                    if (child < 0 || child >= Mission.Scene.Nodes.Count || !seen.Add(child)) continue;
                    // A shared descendant or duplicated placement root cannot
                    // identify one editable instance. Never pick the first actor.
                    if (!inspectionActors.TryAdd(child, actor)) inspectionActors[child] = null;
                    foreach (int nested in SceneBuilder.Children(Mission.Scene.Nodes[child])) pending.Push(nested);
                }
            }
            inspectionActorsIndexed = true;
        }
        return inspectionActors.GetValueOrDefault(node);
    }
    public void SetMissionCoordinates(PickupPlacementEditSession edits)
    {
        placementEdits = edits; UpdateMissionTransformPreview();
    }
    private void ClearInspection()
    {
        inspectionMeshes.Clear(); inspectionActors.Clear(); inspectionActorsIndexed = false; tankPositions.Clear(); ++inspectionSerial;
        inspectionStamp = null; InspectionSourcePath = null;
        HoverInspection = SelectedInspection = null; inspectionPointer = null; InspectionChanged?.Invoke();
    }
}
