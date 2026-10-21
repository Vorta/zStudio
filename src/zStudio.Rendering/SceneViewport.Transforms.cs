using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using zStudio.Core;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private bool worldInspection;
    private PickupPlacementEditSession? placementEdits;
    private MissionPickupSource? transformSource;
    private string? transformTarget;
    private PlacementTransform transformDraft, transformDragStart;
    private PlacementRotationKind transformRotation;
    private IReadOnlyDictionary<MissionPickupSource, PlacementTransform>? transformOverrides;
    private readonly Dictionary<int, Matrix4x4> placementDeltas = [];
    private bool transformValid;
    public bool InspectionSelectionEnabled => AllowLockedInspection || !worldInspection || !editingLocked;
    private bool PlacementEditingEnabled => !worldInspection || !editingLocked;
    public string TransformMode { get; private set; } = "move";
    public bool HasTransformDraft => transformSource != null || objectDraftNode != null;
    public bool TransformHandlesVisible => !PresentationUiHidden && pickupManipulator?.Visibility == Visibility.Visible;
    public bool SelectionBoundsVisible => !PresentationUiHidden && selectionBox?.Visibility == Visibility.Visible;
    public event Action<PlacementTransform>? TransformDraftChanged;
    /// <summary>
    /// A source object's handle gesture, relative to where it started: <see cref="ObjectDrag.Translation"/> is the change of its
    /// local position and <see cref="ObjectDrag.Rotation"/> the turn of its local basis, both in its parent's frame. A
    /// canceled gesture asks for the values it started from; the receiver restores them exactly.
    /// </summary>
    public event Action<ObjectDrag>? ObjectDraftDragged;
    public readonly record struct ObjectDrag(long Gesture, Vector3 Translation, Matrix4x4 Rotation, bool Canceled);
    /// <summary>A source object's handle gesture ended: released or canceled.</summary>
    public event Action? ObjectGestureEnded;
    private long objectGesture;
    private bool CanManipulate => PlacementEditingEnabled && HasTransformDraft && transformValid && TransformHandlesEnabled &&
        SelectedInspection is { Active: true } selected && selected.Target == transformTarget;
    private bool transformHandlesEnabled = true;
    /// <summary>Whether a draft's handles are shown: the card's Move and Rotate toggles can hide them without ending the draft.</summary>
    public bool TransformHandlesEnabled
    {
        get => transformHandlesEnabled;
        set { if (transformHandlesEnabled == value) return; if (!value) CancelPickupDrag(); transformHandlesEnabled = value; RefreshPickupSelection(); }
    }
    /// <summary>A source object's world orientation: its move arrows point along its own axes.</summary>
    private System.Numerics.Quaternion objectHandleOrientation = System.Numerics.Quaternion.Identity;
    // A source object's draft: the node, its parent's world matrix, its draft local transform and the instance preview showing it.
    private int? objectDraftNode;
    private Matrix4x4 objectDraftParent = Matrix4x4.Identity, objectDraftLocal;
    private long objectDraftPreview;

    public void PreviewTransformDraft(MissionPickupSource source, PlacementTransform value, PlacementRotationKind rotation, string mode, bool valid)
    {
        if (mode is not ("move" or "rotate") || mode == "rotate" && rotation == PlacementRotationKind.None)
            throw new ArgumentException("Unsupported transform mode.");
        if (HasParachutePreview || IsParachutePointerMoving) throw new InvalidOperationException("Finish drop-window editing first.");
        if (!PlacementEditingEnabled) throw new InvalidOperationException("Unlock editing first.");
        var pending = valid ? placementEdits?.PreviewTransform(source, value) : transformOverrides;
        bool changedMode = TransformMode != mode || transformRotation != rotation;
        transformSource = source; transformTarget = SelectedInspection?.Target;
        transformRotation = rotation; TransformMode = mode; transformValid = valid;
        if (valid) { transformDraft = value; transformOverrides = pending; UpdateMissionTransformPreview(); }
        if (changedMode && !IsPickupDragging) CreatePickupManipulator();
        RefreshPickupSelection();
    }

    /// <summary>
    /// Shows move or rotate handles for source object <paramref name="node"/> at the draft local transform <paramref name="local"/>
    /// (<paramref name="expected"/> is its accepted one), and previews the object there when the scene can draw it moved
    /// (<see cref="PreviewObjectTransform"/>). Dragging a handle raises <see cref="ObjectDraftDragged"/>; nothing is accepted here.
    /// An invalid draft keeps the last valid handles. Returns false when the object has no handles: it is not drawn, or it or an
    /// ancestor has several parents.
    /// </summary>
    public bool PreviewObjectDraft(int node, Matrix4x4 expected, Matrix4x4 local, string mode, bool valid)
    {
        if (mode is not ("move" or "rotate")) throw new ArgumentException("Unsupported transform mode.");
        if (HasParachutePreview || IsParachutePointerMoving) throw new InvalidOperationException("Finish drop-window editing first.");
        if (!PlacementEditingEnabled) throw new InvalidOperationException("Editing is locked.");
        var data = Mission?.Scene ?? PreviewScene;
        if (data == null || node < 0 || node >= data.Nodes.Count) return false;
        HashSet<int> chain = [];
        for (int at = node; at >= 0 && at < data.Nodes.Count && chain.Add(at); at = data.Nodes[at].Parents.FirstOrDefault(-1))
            if (data.Nodes[at].Parents.Length > 1) return false;
        int parent = data.Nodes[node].Parents.FirstOrDefault(-1);
        Matrix4x4 parentWorld;
        try { parentWorld = parent >= 0 ? WorldTransform(data, parent) : Matrix4x4.Identity; } catch (System.IO.InvalidDataException) { return false; }
        bool changedMode = TransformMode != mode || transformRotation != PlacementRotationKind.EulerRadians || objectDraftNode != node;
        if (transformSource != null) EndTransformDraft();
        objectDraftNode = node; objectDraftParent = parentWorld;
        transformTarget = SelectedInspection?.Target; transformRotation = PlacementRotationKind.EulerRadians; TransformMode = mode;
        if (valid && IsFinite(local))
        {
            objectDraftLocal = local; transformValid = true;
            transformDraft = new((local * parentWorld).Translation, Vector3.Zero);
            // The move arrows follow the object's world orientation (without its scale).
            var orientation = Matrix4x4.Decompose(local * parentWorld, out _, out var turned, out _) && float.IsFinite(turned.X + turned.Y + turned.Z + turned.W) ? System.Numerics.Quaternion.Normalize(turned) : System.Numerics.Quaternion.Identity;
            if (orientation != objectHandleOrientation) { objectHandleOrientation = orientation; changedMode |= mode == "move"; }
            // An unchanged draft shows the accepted object; any change is previewed.
            if (local != expected) objectDraftPreview = PreviewObjectTransform(node, expected, local, false);
            else if (objectDraftPreview != 0) { ClearObjectTransformPreview(objectDraftPreview); objectDraftPreview = 0; }
        }
        // Invalid input hides the handles until it is valid again, as for placements.
        else transformValid = false;
        if (changedMode && !IsPickupDragging) CreatePickupManipulator();
        RefreshPickupSelection();
        return true;
    }

    public void EndTransformDraft()
    {
        CancelPickupDrag();
        // Only its own preview: 0 would clear any, such as the one an accepted edit shows while it applies.
        if (objectDraftNode != null) { if (objectDraftPreview != 0) ClearObjectTransformPreview(objectDraftPreview); objectDraftNode = null; objectDraftPreview = 0; }
        transformSource = null; transformTarget = null; transformOverrides = null; transformValid = false;
        TransformMode = "move"; transformRotation = PlacementRotationKind.None;
        UpdateMissionTransformPreview(); CreatePickupManipulator(); RefreshPickupSelection();
    }

    private void UpdateMissionTransformPreview()
    {
        if (Mission == null || placementEdits == null) return;
        placementDeltas.Clear();
        foreach (var actor in Mission.Actors.GroupBy(a => a.Root).Where(g => g.Count() == 1).Select(g => g.Single()))
        {
            var source = actor.Pickup?.Source ?? actor.CoordinateSource;
            if (source == null || placementEdits.Find(source) == null && placementEdits.Coordinate(source) == null) continue;
            var value = transformOverrides?.GetValueOrDefault(source) ?? placementEdits.Transform(source);
            var baseline = actor.Pickup is { } pickup ? new PlacementTransform(pickup.Position, pickup.Rotation)
                : new PlacementTransform(actor.PlacementPosition ?? WorldTransform(Mission.Scene, actor.Root).Translation, actor.PlacementRotation ?? Vector3.Zero);
            placementDeltas[actor.Root] = value.DeltaFrom(baseline, placementEdits.RotationKind(source));
            if (actor.Pickup != null) pickupPositions[actor.Root] = value.Position;
        }
        UpdatePickupInstances();
        var accepted = placementEdits.ApplyAiPositions(Mission.AiNetworks);
        var changed = transformOverrides == null ? accepted : placementEdits.ApplyAiPositions(accepted, transformOverrides);
        if (changed.Id != AiNetworks.Id || !changed.Networks.SelectMany(n => n.Nodes).Select(n => n.Position).SequenceEqual(AiNetworks.Networks.SelectMany(n => n.Nodes).Select(n => n.Position)))
        {
            var selected = SelectedInspection?.AiNode;
            if (changed.Id != AiNetworks.Id) SetAiPathEditing(null);
            AiNetworks = changed; RebuildAiOverlay();
            if (selected != null) SelectedInspection = InspectAi(selected);
        }
        RefreshPickupSelection();
        if (SelectedInspection is { } selectedInspection) SelectedInspection = InspectTarget(selectedInspection.Target) ?? selectedInspection;
        InspectionChanged?.Invoke();
    }

    private Vector3 rotationAxis, rotationPrevious, rotationStartRadial;
    private Point rotationScreenStart;
    private System.Windows.Vector rotationScreenTangent;
    private double rotationScreenRadius;
    private bool rotationPlane;
    private float rotationAngle;

    private bool RotationPoint(Point point, Vector3 axis, Vector3 origin, out Vector3 unit)
    {
        var ray = viewport.UnProject(point);
        float denominator = Vector3.Dot(ray.Direction, axis);
        unit = default;
        if (Math.Abs(denominator) < .02f) return false;
        float distance = Vector3.Dot(origin - ray.Position, axis) / denominator;
        var radial = ray.Position + ray.Direction * distance - origin;
        if (distance < 0 || !Finite(radial) || radial.LengthSquared() < 1e-10f) return false;
        unit = Vector3.Normalize(radial); return true;
    }

    private void BeginRotationPointer(Point point, Vector3 axis, Vector3 hit)
    {
        // Native ring transforms contain trig roundoff; the editor's axes are
        // exactly world X/Y/Z, including the vehicle's Y-only constraint.
        rotationAxis = Math.Abs(axis.X) > .5f ? Vector3.UnitX : Math.Abs(axis.Y) > .5f ? Vector3.UnitY : Vector3.UnitZ;
        rotationAngle = rotationPresentationAngle = 0; rotationScreenStart = point;
        rotationPlane = RotationPoint(point, rotationAxis, transformDraft.Position, out rotationPrevious);
        var radial = hit - transformDraft.Position;
        if (!Finite(radial) || radial.LengthSquared() < 1e-10f) radial = Vector3.Cross(axis, axis.Y == 0 ? Vector3.UnitY : Vector3.UnitX);
        rotationStartRadial = Vector3.Normalize(radial);
        var tangent = Vector3.Normalize(Vector3.Cross(rotationAxis, rotationStartRadial));
        Point Project(Vector3 p) => viewport.Project(new Point3D(p.X, p.Y, p.Z));
        var segment = Project(hit + tangent * (float)pickupGizmoSize) - Project(hit);
        rotationScreenRadius = Math.Max(8, segment.Length); rotationScreenTangent = segment;
        if (segment.Length > .001) rotationScreenTangent.Normalize(); else rotationScreenTangent = new(1, 0);
    }

    private void MoveRotationPointer(Point point)
    {
        if (rotationPlane)
        {
            if (!RotationPoint(point, rotationAxis, transformDragStart.Position, out var current)) return;
            float step = MathF.Atan2(Vector3.Dot(rotationAxis, Vector3.Cross(rotationPrevious, current)), Vector3.Dot(rotationPrevious, current));
            if (!float.IsFinite(step)) return;
            rotationAngle += step; rotationPrevious = current;
        }
        else rotationAngle = rotationPresentationAngle + (float)(System.Windows.Vector.Multiply(point - rotationScreenStart, rotationScreenTangent) / rotationScreenRadius);
        PublishTransformDrag(transformDragStart.RotateWorld(transformRotation, rotationAxis, rotationAngle));
    }

    private void PublishTransformDrag(PlacementTransform value)
    {
        if (!Finite(value.Position) || !Finite(value.Rotation)) { CancelPickupDrag(); return; }
        if (objectDraftNode != null)
        {
            transformDraft = value;
            // Only the parent's linear part converts a world-axis change: no absolute world round trip adds float noise.
            var linear = objectDraftParent; linear.Translation = Vector3.Zero;
            if (!IsPickupDragging) { ObjectDraftDragged?.Invoke(new(objectGesture, Vector3.Zero, Matrix4x4.Identity, true)); return; }
            if (!Matrix4x4.Invert(linear, out var inverse)) { CancelPickupDrag(); return; }
            ObjectDrag drag = TransformMode == "rotate"
                ? new(objectGesture, Vector3.Zero, rotationAngle == 0 ? Matrix4x4.Identity : linear * Matrix4x4.CreateFromAxisAngle(rotationAxis, rotationAngle) * inverse, false)
                : new(objectGesture, Vector3.TransformNormal(value.Position - transformDragStart.Position, inverse), Matrix4x4.Identity, false);
            if (!Finite(drag.Translation) || !IsFinite(drag.Rotation)) { CancelPickupDrag(); return; }
            ObjectDraftDragged?.Invoke(drag);
            return;
        }
        // A placement drag snaps to the precision an edit stores, so the card and preview show what is saved.
        if (transformSource is { } source && placementEdits != null) value = placementEdits.Stored(source, value);
        transformDraft = value; TransformDraftChanged?.Invoke(value);
        PickupMovePreviewed?.Invoke(value.Position);
    }
}
