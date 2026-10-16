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
using zStudio.Core;
using HCamera = HelixToolkit.Wpf.SharpDX.ProjectionCamera;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

namespace zStudio.Rendering;

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
    private bool editingLocked = true, changingPickupTarget;
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
            if (HandleScenePointerDown(e.GetPosition(viewport), e)) e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (PresentationUiHidden)
            {
                // Do not let Helix's class handler advance an already active native handle.
                e.Handled = IsPickupDragging || IsAiPathPointerTransform || IsParachutePointerMoving ||
                    IsTerrainStroking || IsZonePainting || aiPathBoxStart != null;
                return;
            }
            if (InspectionContent is DependencyObject panel && IsInspectionInput(e.OriginalSource as DependencyObject, panel)) return;
            if (MoveParachutePointer(e.GetPosition(viewport)) || HandleAiPathMove(e.GetPosition(viewport))) { e.Handled = true; return; }
            if (HandleZoneMove(e.GetPosition(viewport)) || HandleTerrainBrushMove(e.GetPosition(viewport))) { e.Handled = true; return; }
            if (HandlePickupPointerMove(e.GetPosition(viewport))) e.Handled = true;
            else HandleAiPointerMove(e.GetPosition(viewport));
        };
        PreviewMouseUp += (_, e) =>
        {
            if (PresentationUiHidden)
            {
                // Releasing a hidden marquee ends only that transient selection gesture.
                // Do not select against its old rectangle after the viewport has resized.
                if (aiPathBoxStart != null && e.ChangedButton == MouseButton.Left)
                { CancelAiPathBox(); e.Handled = true; return; }
                if (HandleZoneUp(e) || HandleTerrainBrushUp(e) || HandlePickupPointerUp(e.GetPosition(viewport), e)) e.Handled = true;
                return;
            }
            if (HandleAiPathUp(e.GetPosition(viewport), e) || HandleZoneUp(e) || HandleTerrainBrushUp(e) || HandlePickupPointerUp(e.GetPosition(viewport), e)) e.Handled = true;
        };
        MouseLeave += (_, _) => { if (!PresentationUiHidden && !pickupPresentationRebase) CancelPickupDrag(); SetPickupHover(false); hoveredAiNode = null; PublishAiLabel(); };
        viewport.LostMouseCapture += (_, _) => { if (IsPickupDragging) CancelPickupDrag(); CancelTerrainStroke(); CancelZoneGesture(); CancelAiPathBox(); };
        viewport.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && (CancelAiPathBox() || CancelPickupDrag() || CancelTerrainStroke() || CancelZoneGesture())) e.Handled = true; };
    }
    internal bool HandleScenePointerDown(Point point, MouseButtonEventArgs e)
    {
        if (PresentationUiHidden) return e.ChangedButton is MouseButton.Left or MouseButton.Right;
        if (e.Handled ||
            InspectionContent is DependencyObject panel && IsInspectionInput(e.OriginalSource as DependencyObject, panel)) return false;
        if (IsFlyActive || IsPickupDragging) return true;
        // The screen-space cube owns its clicks, even over scene geometry or
        // handles. Resolve it before the editing lock can consume a scene click.
        if (!IsAiPathPointerTransform && !IsParachutePointerMoving && aiPathBoxStart == null && e.ChangedButton == MouseButton.Left && NavigateCubeAt(point)) return true;
        return HandleParachuteDown(point, e) || HandleAiPathDown(point, e) || HandleZoneDown(point, e) || HandleTerrainBrushDown(point, e) || HandlePickupPointerDown(point, e) || HandleInspectionClick(point, e) || HandleAiPointerDown(point, e);
    }
    private void ConfigurePickups()
    {
        worldInspection = true;
        foreach (var actor in Mission?.Actors.Where(a => a.Pickup != null) ?? [])
        {
            pickupActors[actor.Root] = actor;
            pickupPositions[actor.Root] = WorldTransform(Mission!.Scene, actor.Root).Translation;
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
        pickupOverlay = new TopMostGroup3D(); PresentationGuide(pickupOverlay); viewport.Items.Add(pickupOverlay);
        selectionBox = new() { Color = Colors.Gold, Thickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        pickupOverlay.Children.Add(selectionBox);
        CreatePickupManipulator();
    }
    private void CreatePickupManipulator()
    {
        if (pickupOverlay == null) return;
        SetPickupHover(false); pickupArrows.Clear();
        if (pickupManipulator != null) { pickupOverlay.Children.Remove(pickupManipulator); pickupManipulator.Dispose(); }
        pickupManipulator = new(BeginPickupDrag) { EnableTranslation = TransformMode == "move", EnableRotation = TransformMode == "rotate",
            EnableRotationX = transformRotation == PlacementRotationKind.EulerRadians, EnableRotationZ = transformRotation == PlacementRotationKind.EulerRadians, EnableScaling = false,
            EnableXRayGrid = false, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        // A source object's move arrows point along its own axes; placements and rotation rings keep the world's.
        Matrix3D? orient = null;
        if (objectDraftNode != null && TransformMode == "move" && objectHandleOrientation != System.Numerics.Quaternion.Identity)
        {
            var m = Matrix4x4.CreateFromQuaternion(objectHandleOrientation);
            orient = new Matrix3D(m.M11, m.M12, m.M13, 0, m.M21, m.M22, m.M23, 0, m.M31, m.M32, m.M33, 0, 0, 0, 0, 1);
        }
        foreach (var mesh in ManipulatorMeshes(pickupManipulator))
        {
            if (mesh.Material is DiffuseMaterial material) mesh.Material = new DiffuseMaterial { DiffuseColor = material.DiffuseColor, EnableUnLit = true };
            if (mesh.IsRendering)
            {
                // Widen the active translation arrows in their local Y/Z plane, before axis rotation.
                // Pointer picking uses a forgiving screen-space band around each visible arrow.
                if (TransformMode == "move")
                {
                    var transform = Matrix3D.Identity; transform.Scale(new Vector3D(1, 2, 2));
                    transform.Append(mesh.Transform?.Value ?? Matrix3D.Identity);
                    if (orient is { } turn) transform.Append(turn);
                    mesh.Transform = new MatrixTransform3D(transform);
                }
                mesh.CullMode = SharpDX.Direct3D11.CullMode.None;
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
        if (root != selectedPickup) CancelPickupDrag();
        selectedPickup = root is int r && pickupActors.ContainsKey(r) ? r : null;
        RefreshPickupSelection();
    }
    public void SetEditingLocked(bool locked)
    {
        worldInspection = true;
        if (locked) { ResetParachuteSelection(); SetAiPathEditing(null); CancelPickupDrag(); EndTransformDraft(); }
        editingLocked = locked;
        if (locked && !AllowLockedInspection)
        {
            // Locking must also close the card during Fly; ordinary selection
            // changes intentionally reject that interaction state.
            SelectedInspection = null; SelectAiNode(null); SelectPickup(null, false, true);
            FramingSelection = null; InspectionSelectionCleared?.Invoke();
        }
        RefreshPickupSelection(); RefreshAiSelection();
        ++inspectionSerial; InspectionChanged?.Invoke(); viewport.InvalidateRender();
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
        ++inspectionSerial;
        if (Mission == null) return;
        foreach (var (mesh, original) in pickupBasePlacements)
        {
            if (!original.Any(p => pickupRoots.ContainsKey(p.NodeIndex) || ActorAt(p.NodeIndex)?.CoordinateSource != null)) continue;
            var visible = visiblePlacements[mesh].Select(p => p.NodeIndex).ToHashSet();
            var changed = original.Select(AdjustPickupPlacement).ToArray(); placements[mesh] = changed;
            var shown = changed.Where(p => visible.Contains(p.NodeIndex)).ToArray(); visiblePlacements[mesh] = shown;
            mesh.Instances = shown.Select(p => p.Transform).ToArray();
        }
        RefreshBoundsBoxes();
        RecalculateSceneBounds(); cameraPoseDirty = true; viewport.InvalidateRender();
    }
    private ScenePlacement AdjustPickupPlacement(ScenePlacement p)
    {
        if (Mission == null) return p;
        if (ActorAt(p.NodeIndex) is { } placed && placementDeltas.TryGetValue(placed.Root, out var delta))
            return p with { Transform = p.Transform * delta };
        if (!pickupRoots.TryGetValue(p.NodeIndex, out int root))
        {
            if (ActorAt(p.NodeIndex) is { } actor && tankPositions.TryGetValue(actor.Root, out var position))
            { var tank = p.Transform; tank.Translation += position - (actor.PlacementPosition ?? WorldTransform(Mission.Scene, actor.Root).Translation); return p with { Transform = tank }; }
            return p;
        }
        var matrix = p.Transform; matrix.Translation += pickupPositions[root] - WorldTransform(Mission.Scene, root).Translation;
        return p with { Transform = matrix };
    }
    private void RefreshPickupSelection()
    {
        if (selectionBox == null || pickupManipulator == null || pickupTarget == null) return;
        bool selected = worldInspection && InspectionSelectionEnabled && SelectedInspection is { Active: true };
        selectionBox.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        pickupManipulator.Visibility = CanManipulate ? Visibility.Visible : Visibility.Collapsed;
        if (pickupManipulator.Visibility != Visibility.Visible) SetPickupHover(false);
        if (!selected) { pickupManipulator.Target = null; return; }
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        HashSet<int> selectedNodes = [];
        if (SelectedInspection is { Node: >= 0 } inspected && PreviewScene != null)
        {
            int owner = inspected.SelectionNode;
            Stack<int> pending = new(owner < 0 ? PreviewScene.Nodes.Where(n => SelectionNode(n.Index) == owner).Select(n => n.Index)
                : [ActorAt(owner)?.Root ?? owner]);
            while (pending.TryPop(out int index))
            {
                if (index < 0 || index >= PreviewScene.Nodes.Count || !selectedNodes.Add(index)) continue;
                foreach (int child in SceneBuilder.Children(PreviewScene.Nodes[index])) pending.Push(child);
            }
        }
        var selectionFrame = SelectionBoundsFrame(selectedNodes);
        var inverseFrame = selectionFrame; inverseFrame.Invert();
        if (SelectedInspection?.AiNode is { } ai && AiNetworks.Find(ai) is { } node)
        { float radius = AiScale(node.Node.Position, 5.5); min = node.Node.Position - new Vector3(radius); max = node.Node.Position + new Vector3(radius); }
        foreach (var (mesh, items) in visiblePlacements)
        {
            if (!mesh.IsRendering || mesh.Visibility != Visibility.Visible || !mesh.IsDepthClipEnabled ||
                mesh.Geometry is not HelixToolkit.SharpDX.MeshGeometry3D geometry ||
                !staticMeshBounds.TryGetValue(geometry, out var local) || !Finite(local.Min) || !Finite(local.Max)) continue;
            var (localMin, localMax) = local;
            for (int i = 0; i < items.Length; i++)
            {
                if (!selectedNodes.Contains(items[i].NodeIndex)) continue;
                var transform = SelectionPlacementTransform(mesh, items, i);
                transform.Append(inverseFrame);
                for (int c = 0; c < 8; c++)
                {
                    var point = transform.Transform(new Point3D((c & 1) == 0 ? localMin.X : localMax.X,
                        (c & 2) == 0 ? localMin.Y : localMax.Y, (c & 4) == 0 ? localMin.Z : localMax.Z));
                    Vector3 v = new((float)point.X, (float)point.Y, (float)point.Z);
                    min = Vector3.Min(min, v); max = Vector3.Max(max, v);
                }
            }
        }
        if (!Finite(min) || !Finite(max)) { selectionBox.Visibility = pickupManipulator.Visibility = Visibility.Collapsed; return; }
        Vector3[] corners = [new(min.X,min.Y,min.Z), new(max.X,min.Y,min.Z), new(max.X,max.Y,min.Z), new(min.X,max.Y,min.Z), new(min.X,min.Y,max.Z), new(max.X,min.Y,max.Z), new(max.X,max.Y,max.Z), new(min.X,max.Y,max.Z)];
        for (int i = 0; i < corners.Length; i++)
        {
            var point = selectionFrame.Transform(new Point3D(corners[i].X, corners[i].Y, corners[i].Z));
            corners[i] = new((float)point.X, (float)point.Y, (float)point.Z);
        }
        if (corners.Any(p => !Finite(p))) { selectionBox.Visibility = pickupManipulator.Visibility = Visibility.Collapsed; return; }
        selectionBox.Geometry = new LineGeometry3D { Positions = new Vector3Collection(corners), Indices = new IntCollection([0,1,1,2,2,3,3,0,4,5,5,6,6,7,7,4,0,4,1,5,2,6,3,7]) };
        if (!IsPickupDragging && CanManipulate) PlacePickupHandles();
    }
    /// <summary>Puts the handles at the draft position.</summary>
    private void PlacePickupHandles()
    {
        if (pickupTarget == null) return;
        changingPickupTarget = true;
        try
        {
            var p = transformDraft.Position; pickupTarget.Transform = new TranslateTransform3D(p.X, p.Y, p.Z);
            if (pickupManipulator == null) return;
            pickupManipulator.CenterOffset = Vector3.Zero;
            pickupManipulator.Target = null; pickupManipulator.Target = pickupTarget;
            UpdatePickupGizmoSize(true);
        }
        finally { changingPickupTarget = false; }
    }
    private bool BeginPickupDrag(MouseDown3DEventArgs? e)
    {
        if (IsFlyActive || !CanManipulate || IsPickupDragging ||
            e?.OriginalInputEventArgs is not MouseButtonEventArgs { ChangedButton: MouseButton.Left } || viewport.Camera is not HCamera camera) return false;
        if (e.HitTestResult?.ModelHit is MeshGeometryModel3D axisMesh && TransformMode == "move")
        {
            var axis = (axisMesh.Transform?.Value ?? Matrix3D.Identity).Transform(new Vector3D(1, 0, 0));
            var look = camera.LookDirection; look.Normalize();
            if (Vector3D.CrossProduct(axis, look).LengthSquared < .0076)
            { Information?.Invoke("Rotate the view to drag this axis, or enter its coordinate in Pickup placement."); return false; }
        }
        StopNavigationMotion();
        pickupCameraPose = CaptureView(); pickupCameraInertia = viewport.IsInertiaEnabled; viewport.IsInertiaEnabled = false;
        transformDragStart = transformDraft; ++objectGesture; pickupPresentationConstraint = pickupPresentationRebase = false; IsPickupDragging = true;
        viewport.CaptureMouse(); viewport.Focus(); return true;
    }
    private void PickupTargetChanged(object? sender, EventArgs e)
    {
        if (changingPickupTarget || !IsPickupDragging || TransformMode != "move" || pickupTarget?.Transform == null) return;
        var m = pickupTarget.Transform.Value; var position = new Vector3((float)m.OffsetX, (float)m.OffsetY, (float)m.OffsetZ);
        if (!Finite(position)) { CancelPickupDrag(); return; }
        PublishTransformDrag(transformDraft with { Position = position });
    }
    public bool CancelPickupDrag()
    {
        if (!IsPickupDragging) return false;
        IsPickupDragging = false; activePickupHandle = null; ResumePickupCamera();
        viewport.ReleaseMouseCapture(); PublishTransformDrag(transformDragStart);
        if (objectDraftNode != null) ObjectGestureEnded?.Invoke();
        CreatePickupManipulator(); RefreshPickupSelection(); return true;
    }
    private void UpdatePickupGizmoSize(bool force = false)
    {
        if (IsPickupDragging || pickupManipulator == null || pickupTarget == null || viewport.Camera is not HCamera camera) return;
        var p = transformDraft.Position; var look = camera.LookDirection; look.Normalize();
        double depth = Vector3D.DotProduct(new Point3D(p.X, p.Y, p.Z) - camera.Position, look);
        if (depth <= 0) { pickupManipulator.Visibility = Visibility.Collapsed; return; }
        pickupManipulator.Visibility = CanManipulate && selectionBox?.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        double worldWidth = camera is OrthographicCamera orthographic ? orthographic.Width
            : depth * 2 * Math.Tan(CaptureView().FieldOfView * Math.PI / 360) * Aspect;
        double size = Math.Max(.001, worldWidth * PickupGizmoPixelsPerUnit / Math.Max(1, viewport.ActualWidth));
        if (!force && Math.Abs(size - pickupGizmoSize) < size * .001) return;
        pickupGizmoSize = size; pickupManipulator.SizeScale = size;
        pickupManipulator.Target = null; pickupManipulator.Target = pickupTarget;
        pickupManipulator.Visibility = CanManipulate && selectionBox?.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ClearPickupEditing()
    {
        IsPickupDragging = false; activePickupHandle = null; SetPickupHover(false); pickupArrows.Clear(); ResumePickupCamera(); selectedPickup = null;
        if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
        if (pickupTarget != null) pickupTransformDescriptor?.RemoveValueChanged(pickupTarget, PickupTargetChanged);
        pickupTransformDescriptor = null; pickupManipulator?.Dispose(); selectionBox?.Dispose(); pickupOverlay?.Dispose(); pickupTarget?.Dispose();
        pickupManipulator = null; selectionBox = null; pickupOverlay = null; pickupTarget = null;
        pickupActors.Clear(); pickupRoots.Clear(); pickupPositions.Clear(); pickupBasePlacements.Clear(); boundsPlacements.Clear();
        worldInspection = false; editingLocked = true; placementEdits = null; transformSource = null; transformOverrides = null; placementDeltas.Clear(); transformValid = false;
        transformTarget = null; TransformMode = "move"; transformRotation = PlacementRotationKind.None;
        objectDraftNode = null; objectDraftPreview = 0;
    }
    private void ResumePickupCamera()
    {
        if (pickupCameraPose == null) return;
        viewport.IsInertiaEnabled = pickupCameraInertia; RestoreView(pickupCameraPose); pickupCameraPose = null;
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
