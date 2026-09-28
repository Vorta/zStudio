using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Rendering;

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
    public bool InspectionSelectionEnabled => !worldInspection || !pickupLocked;
    public string TransformMode { get; private set; } = "move";
    public bool HasTransformDraft => transformSource != null;
    public bool TransformHandlesVisible => pickupManipulator?.Visibility == Visibility.Visible;
    public bool SelectionBoundsVisible => selectionBox?.Visibility == Visibility.Visible;
    public event Action<PlacementTransform>? TransformDraftChanged;
    private bool CanManipulate => InspectionSelectionEnabled && HasTransformDraft && transformValid &&
        SelectedInspection is { Active: true } selected && selected.Target == transformTarget;

    public void PreviewTransformDraft(MissionPickupSource source, PlacementTransform value, PlacementRotationKind rotation, string mode, bool valid)
    {
        if (mode is not ("move" or "rotate") || mode == "rotate" && rotation == PlacementRotationKind.None)
            throw new ArgumentException("Unsupported transform mode.");
        if (!InspectionSelectionEnabled) throw new InvalidOperationException("Unlock editing first.");
        var pending = valid ? placementEdits?.PreviewTransform(source, value) : transformOverrides;
        bool changedMode = TransformMode != mode || transformRotation != rotation;
        transformSource = source; transformTarget = SelectedInspection?.Target;
        transformRotation = rotation; TransformMode = mode; transformValid = valid;
        if (valid) { transformDraft = value; transformOverrides = pending; UpdateMissionTransformPreview(); }
        if (changedMode && !IsPickupDragging) CreatePickupManipulator();
        RefreshPickupSelection();
    }

    public void EndTransformDraft()
    {
        CancelPickupDrag();
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
            AiNetworks = changed; RebuildAiOverlay();
            if (selected != null) SelectedInspection = InspectAi(selected);
        }
        RefreshPickupSelection();
        if (SelectedInspection is { } selectedInspection) SelectedInspection = InspectTarget(selectedInspection.Target) ?? selectedInspection;
        InspectionChanged?.Invoke();
    }

    private Vector3 rotationAxis, rotationPrevious;
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
        rotationAngle = 0; rotationScreenStart = point;
        rotationPlane = RotationPoint(point, rotationAxis, transformDraft.Position, out rotationPrevious);
        var radial = hit - transformDraft.Position;
        if (!Finite(radial) || radial.LengthSquared() < 1e-10f) radial = Vector3.Cross(axis, axis.Y == 0 ? Vector3.UnitY : Vector3.UnitX);
        var tangent = Vector3.Normalize(Vector3.Cross(rotationAxis, Vector3.Normalize(radial)));
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
        else rotationAngle = (float)(System.Windows.Vector.Multiply(point - rotationScreenStart, rotationScreenTangent) / rotationScreenRadius);
        PublishTransformDrag(transformDragStart.RotateWorld(transformRotation, rotationAxis, rotationAngle));
    }

    private void PublishTransformDrag(PlacementTransform value)
    {
        if (!Finite(value.Position) || !Finite(value.Rotation)) { CancelPickupDrag(); return; }
        transformDraft = value; TransformDraftChanged?.Invoke(value);
        PickupMovePreviewed?.Invoke(value.Position);
    }
}
