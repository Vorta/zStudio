using System.ComponentModel;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    private const double PickupGizmoPixelsPerUnit = 72;
    private readonly Dictionary<int, MissionActor> pickupActors = [];
    private readonly Dictionary<int, int> pickupRoots = [];
    private readonly Dictionary<MeshGeometryModel3D, ScenePlacement[]> pickupBasePlacements = [];
    private readonly Dictionary<LineGeometryModel3D, ScenePlacement[]> boundsPlacements = [];
    private readonly Dictionary<int, Vector3> pickupPositions = [];
    private TopMostGroup3D? pickupOverlay;
    private LineGeometryModel3D? selectionBox;
    private PickupManipulator? pickupManipulator;
    private GroupModel3D? pickupTarget;
    private DependencyPropertyDescriptor? pickupTransformDescriptor;
    private int? selectedPickup;
    private bool pickupLocked = true, pickupEditable, changingPickupTarget;
    private Vector3 pickupDragStart;
    private double pickupGizmoSize;
    private bool pickupCameraInertia;
    private ViewPose? pickupCameraPose;
    public bool IsPickupDragging { get; private set; }
    public int? SelectedPickupRoot => selectedPickup;
    public event Action<int, Vector3>? PickupMoveCommitted;
    public event Action<Vector3>? PickupMovePreviewed;
    public event Action? PickupInteractionStarting;
    public Func<bool>? CanStartPickupEdit { get; set; }
    public MissionActor? PickupAt(int node) => pickupRoots.TryGetValue(node, out int root) ? pickupActors[root] : null;

    private sealed class PickupManipulator(Func<MouseDown3DEventArgs?, bool> begin) : TransformManipulator3D
    {
        protected override bool CanBeginTransform(MouseDown3DEventArgs? e) => begin(e);
    }
    private void ConfigurePickupInput()
    {
        // Pick overlay handles on the parent, before Helix's viewport class handler picks the scene.
        PreviewMouseDown += (_, e) =>
        {
            if (HandlePickupPointerDown(e.GetPosition(viewport), e)) e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (HandlePickupPointerMove(e.GetPosition(viewport))) e.Handled = true;
        };
        PreviewMouseUp += (_, e) =>
        {
            if (HandlePickupPointerUp(e.GetPosition(viewport), e)) e.Handled = true;
        };
        MouseLeave += (_, _) => { CancelPickupDrag(); SetPickupHover(false); };
        viewport.LostMouseCapture += (_, _) => { if (IsPickupDragging) CancelPickupDrag(); };
        viewport.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && CancelPickupDrag()) e.Handled = true; };
    }
    private void ConfigurePickups()
    {
        if (Mission == null) return;
        foreach (var actor in Mission.Actors.Where(a => a.Pickup != null))
        {
            pickupActors[actor.Root] = actor;
            pickupPositions[actor.Root] = WorldTransform(Mission.Scene, actor.Root).Translation;
            Stack<int> pending = new(); HashSet<int> seen = []; pending.Push(actor.Root);
            while (pending.TryPop(out int node))
            {
                if (node < 0 || node >= Mission.Scene.Nodes.Count || !seen.Add(node)) continue;
                pickupRoots.TryAdd(node, actor.Root);
                foreach (int child in SceneBuilder.Children(Mission.Scene.Nodes[node])) pending.Push(child);
            }
        }
        foreach (var pair in placements) pickupBasePlacements.Add(pair.Key, pair.Value);
        pickupTarget = new GroupModel3D(); viewport.Items.Add(pickupTarget);
        pickupTransformDescriptor = DependencyPropertyDescriptor.FromProperty(Element3D.TransformProperty, typeof(GroupModel3D));
        pickupTransformDescriptor.AddValueChanged(pickupTarget, PickupTargetChanged);
        pickupOverlay = new TopMostGroup3D(); viewport.Items.Add(pickupOverlay);
        selectionBox = new() { Color = Colors.Gold, Thickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        pickupOverlay.Children.Add(selectionBox);
        CreatePickupManipulator();
    }
    private void CreatePickupManipulator()
    {
        if (pickupOverlay == null) return;
        SetPickupHover(false); pickupArrows.Clear();
        if (pickupManipulator != null) { pickupOverlay.Children.Remove(pickupManipulator); pickupManipulator.Dispose(); }
        pickupManipulator = new(BeginPickupDrag) { EnableTranslation = true, EnableRotation = false, EnableScaling = false,
            EnableXRayGrid = false, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        foreach (var mesh in ManipulatorMeshes(pickupManipulator))
        {
            if (mesh.Material is DiffuseMaterial material) mesh.Material = new DiffuseMaterial { DiffuseColor = material.DiffuseColor, EnableUnLit = true };
            if (mesh.IsRendering)
            {
                // Widen the active translation arrows in their local Y/Z plane, before axis rotation.
                // Pointer picking uses a forgiving screen-space band around each visible arrow.
                var transform = Matrix3D.Identity;
                transform.Scale(new Vector3D(1, 2, 2));
                transform.Append(mesh.Transform?.Value ?? Matrix3D.Identity);
                mesh.Transform = new MatrixTransform3D(transform);
                pickupArrows.Add(mesh);
            }
        }
        pickupOverlay.Children.Add(pickupManipulator); pickupGizmoSize = 0;
    }
    private static IEnumerable<MeshGeometryModel3D> ManipulatorMeshes(Element3D element)
    {
        if (element is MeshGeometryModel3D mesh) yield return mesh;
        else if (element is GroupElement3D group)
            foreach (var child in group.Children) foreach (var nested in ManipulatorMeshes(child)) yield return nested;
    }
    public void SelectPickup(int? root, bool editable, bool locked)
    {
        CancelPickupDrag();
        selectedPickup = root is int r && pickupActors.ContainsKey(r) ? r : null;
        pickupEditable = editable; pickupLocked = locked; RefreshPickupSelection();
    }
    public void SetPickupLocked(bool locked)
    {
        if (locked) CancelPickupDrag(); pickupLocked = locked; RefreshPickupSelection();
    }
    public void SetPickupPositions(IReadOnlyDictionary<int, Vector3> positions)
    {
        foreach (var (root, position) in positions)
            if (pickupActors.ContainsKey(root) && Finite(position)) pickupPositions[root] = position;
        UpdatePickupInstances(); RefreshPickupSelection();
    }
    public Vector3 PickupPosition(int root) => pickupPositions[root];
    private void UpdatePickupInstances()
    {
        if (Mission == null) return;
        ScenePlacement Adjust(ScenePlacement p)
        {
            if (!pickupRoots.TryGetValue(p.NodeIndex, out int root)) return p;
            var matrix = p.Transform; matrix.Translation += pickupPositions[root] - WorldTransform(Mission.Scene, root).Translation;
            return p with { Transform = matrix };
        }
        foreach (var (mesh, original) in pickupBasePlacements)
        {
            if (!original.Any(p => pickupRoots.ContainsKey(p.NodeIndex))) continue;
            var visible = visiblePlacements[mesh].Select(p => p.NodeIndex).ToHashSet();
            var changed = original.Select(Adjust).ToArray(); placements[mesh] = changed;
            var shown = changed.Where(p => visible.Contains(p.NodeIndex)).ToArray(); visiblePlacements[mesh] = shown;
            mesh.Instances = shown.Select(p => p.Transform).ToArray();
        }
        foreach (var (box, original) in boundsPlacements) box.Instances = original.Select(p => Adjust(p).Transform).ToArray();
        RecalculateSceneBounds(); cameraPoseDirty = true; viewport.InvalidateRender();
    }
    private void RefreshPickupSelection()
    {
        if (selectionBox == null || pickupManipulator == null || pickupTarget == null) return;
        bool selected = selectedPickup is int root && pickupActors.ContainsKey(root);
        selectionBox.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        pickupManipulator.Visibility = selected && pickupEditable && !pickupLocked ? Visibility.Visible : Visibility.Collapsed;
        if (pickupManipulator.Visibility != Visibility.Visible) SetPickupHover(false);
        if (!selected || selectedPickup is not int id) { pickupManipulator.Target = null; return; }
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var (mesh, items) in visiblePlacements)
        {
            if (mesh.Geometry?.Positions is not { Count: > 0 } vertices) continue;
            var matching = items.Where(p => pickupRoots.GetValueOrDefault(p.NodeIndex, -1) == id).ToArray();
            if (matching.Length == 0) continue;
            Vector3 localMin = vertices.Aggregate(Vector3.Min), localMax = vertices.Aggregate(Vector3.Max);
            foreach (var p in matching) for (int c = 0; c < 8; c++)
            {
                Vector3 v = Vector3.Transform(new((c & 1) == 0 ? localMin.X : localMax.X, (c & 2) == 0 ? localMin.Y : localMax.Y, (c & 4) == 0 ? localMin.Z : localMax.Z), p.Transform);
                min = Vector3.Min(min, v); max = Vector3.Max(max, v);
            }
        }
        if (!Finite(min) || !Finite(max)) { selectionBox.Visibility = pickupManipulator.Visibility = Visibility.Collapsed; return; }
        Vector3[] corners = [new(min.X,min.Y,min.Z), new(max.X,min.Y,min.Z), new(max.X,max.Y,min.Z), new(min.X,max.Y,min.Z), new(min.X,min.Y,max.Z), new(max.X,min.Y,max.Z), new(max.X,max.Y,max.Z), new(min.X,max.Y,max.Z)];
        selectionBox.Geometry = new LineGeometry3D { Positions = new Vector3Collection(corners), Indices = new IntCollection([0,1,1,2,2,3,3,0,4,5,5,6,6,7,7,4,0,4,1,5,2,6,3,7]) };
        if (!IsPickupDragging)
        {
            changingPickupTarget = true;
            try
            {
                var p = pickupPositions[id]; pickupTarget.Transform = new TranslateTransform3D(p.X, p.Y, p.Z);
                pickupManipulator.CenterOffset = (min + max) * .5f - p;
                pickupManipulator.Target = null; pickupManipulator.Target = pickupTarget;
                UpdatePickupGizmoSize(true);
            }
            finally { changingPickupTarget = false; }
        }
    }
    private bool BeginPickupDrag(MouseDown3DEventArgs? e)
    {
        if (IsFlyActive || pickupLocked || !pickupEditable || selectedPickup is not int root || IsPickupDragging ||
            e?.OriginalInputEventArgs is not MouseButtonEventArgs { ChangedButton: MouseButton.Left } || viewport.Camera is not HCamera camera) return false;
        if (e.HitTestResult?.ModelHit is MeshGeometryModel3D axisMesh)
        {
            var axis = (axisMesh.Transform?.Value ?? Matrix3D.Identity).Transform(new Vector3D(1, 0, 0));
            var look = camera.LookDirection; look.Normalize();
            if (Vector3D.CrossProduct(axis, look).LengthSquared < .0076)
            { Information?.Invoke("Rotate the view to drag this axis, or enter its coordinate in Pickup placement."); return false; }
        }
        viewport.StopSpin(); rotationVelocity = default;
        pickupCameraPose = CaptureView(); pickupCameraInertia = viewport.IsInertiaEnabled; viewport.IsInertiaEnabled = false;
        pickupDragStart = pickupPositions[root]; IsPickupDragging = true;
        viewport.CaptureMouse(); viewport.Focus(); return true;
    }
    private void PickupTargetChanged(object? sender, EventArgs e)
    {
        if (changingPickupTarget || !IsPickupDragging || selectedPickup is not int root || pickupTarget?.Transform == null) return;
        var m = pickupTarget.Transform.Value; var position = new Vector3((float)m.OffsetX, (float)m.OffsetY, (float)m.OffsetZ);
        if (!Finite(position)) { CancelPickupDrag(); return; }
        pickupPositions[root] = position; UpdatePickupInstances(); RefreshPickupSelection(); PickupMovePreviewed?.Invoke(position);
    }
    public bool CancelPickupDrag()
    {
        if (!IsPickupDragging) return false;
        IsPickupDragging = false; activePickupHandle = null; ResumePickupCamera();
        if (selectedPickup is int root) pickupPositions[root] = pickupDragStart;
        viewport.ReleaseMouseCapture(); CreatePickupManipulator(); UpdatePickupInstances(); RefreshPickupSelection(); PickupMovePreviewed?.Invoke(pickupDragStart); return true;
    }
    private void UpdatePickupGizmoSize(bool force = false)
    {
        if (IsPickupDragging || selectedPickup is not int root || pickupManipulator == null || pickupTarget == null || viewport.Camera is not HCamera camera) return;
        var p = pickupPositions[root] + pickupManipulator.CenterOffset; var look = camera.LookDirection; look.Normalize();
        double depth = Vector3D.DotProduct(new Point3D(p.X, p.Y, p.Z) - camera.Position, look);
        if (depth <= 0) { pickupManipulator.Visibility = Visibility.Collapsed; return; }
        pickupManipulator.Visibility = pickupEditable && !pickupLocked && selectionBox?.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        double size = Math.Max(.001, depth * 2 * Math.Tan(camera.FieldOfView * Math.PI / 360) * PickupGizmoPixelsPerUnit / Math.Max(1, viewport.ActualWidth));
        if (!force && Math.Abs(size - pickupGizmoSize) < size * .001) return;
        pickupGizmoSize = size; pickupManipulator.SizeScale = size;
        pickupManipulator.Target = null; pickupManipulator.Target = pickupTarget;
        pickupManipulator.Visibility = pickupEditable && !pickupLocked && selectionBox?.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ClearPickupEditing()
    {
        IsPickupDragging = false; activePickupHandle = null; SetPickupHover(false); pickupArrows.Clear(); ResumePickupCamera(); selectedPickup = null;
        if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
        if (pickupTarget != null) pickupTransformDescriptor?.RemoveValueChanged(pickupTarget, PickupTargetChanged);
        pickupTransformDescriptor = null; pickupManipulator?.Dispose(); selectionBox?.Dispose(); pickupOverlay?.Dispose(); pickupTarget?.Dispose();
        pickupManipulator = null; selectionBox = null; pickupOverlay = null; pickupTarget = null;
        pickupActors.Clear(); pickupRoots.Clear(); pickupPositions.Clear(); pickupBasePlacements.Clear(); boundsPlacements.Clear();
    }
    private void ResumePickupCamera()
    {
        if (pickupCameraPose == null) return;
        viewport.IsInertiaEnabled = pickupCameraInertia; RestoreView(pickupCameraPose); pickupCameraPose = null;
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
