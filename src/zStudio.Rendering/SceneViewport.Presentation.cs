using System.Numerics;
using Vector = System.Windows.Vector;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private bool showAxes = true;
    /// <summary>The coordinate axes in the lower left corner (a docked editor shows the selection there instead).</summary>
    public bool ShowAxes
    {
        get => showAxes;
        set { showAxes = value; viewport.ShowCoordinateSystem = value; }
    }
    private readonly Grid presentationOverlays = new();

    private bool aiPresentationRebase, parachutePresentationRebase, pickupPresentationRebase, navigationPresentationRebase;
    private bool brushPresentationSuspended, zonePresentationSuspended, aiBoxPresentationRebase;
    private Vector aiPointerDelta;
    private Vector parachutePointerOffset;
    private bool pickupPresentationConstraint;
    private Vector3 pickupPresentationAxis, pickupPresentationNormal, pickupPresentationHit, pickupPresentationPosition;

    private void RebasePresentationPointers()
    {
        aiPresentationRebase = IsAiPathPointerTransform;
        parachutePresentationRebase = IsParachutePointerMoving;
        pickupPresentationRebase = IsPickupDragging;
        navigationPresentationRebase = rotationPoint != null;
        brushPresentationSuspended = IsTerrainStroking;
        zonePresentationSuspended = IsZonePainting;
        aiBoxPresentationRebase = aiPathBoxStart != null;
    }

    private bool RebasePickupPointer(Point point)
    {
        if (!pickupPresentationRebase || !IsPickupDragging || activePickupHandle?.ModelHit is not MeshGeometryModel3D arrow) return false;
        pickupPresentationRebase = false;
        if (TransformMode == "rotate")
        {
            // Keep the original gesture rollback, but start measuring the remaining angle here.
            bool usePlane = rotationPlane;
            if (usePlane && !RotationPoint(point, rotationAxis, transformDraft.Position, out _))
            { pickupPresentationRebase = true; return true; }
            float angle = rotationAngle;
            var radial = Vector3.Transform(rotationStartRadial, Matrix4x4.CreateFromAxisAngle(rotationAxis, angle));
            var originalRadial = rotationStartRadial;
            BeginRotationPointer(point, rotationAxis, transformDraft.Position + radial * (float)pickupGizmoSize);
            rotationStartRadial = originalRadial;
            rotationPlane = usePlane;
            rotationAngle = rotationPresentationAngle = angle;
            return true;
        }
        var direction = (arrow.Transform?.Value ?? Matrix3D.Identity).Transform(new Vector3D(1, 0, 0));
        pickupPresentationAxis = Vector3.Normalize(new((float)direction.X, (float)direction.Y, (float)direction.Z));
        var look = CaptureView().LookDirection;
        var forward = Vector3.Normalize(new((float)look.X, (float)look.Y, (float)look.Z));
        // The axis-containing plane facing the camera is the ordinary translation constraint.
        pickupPresentationNormal = forward - pickupPresentationAxis * Vector3.Dot(forward, pickupPresentationAxis);
        pickupPresentationPosition = transformDraft.Position;
        pickupPresentationConstraint = PickupPresentationPoint(point, out pickupPresentationHit);
        if (!pickupPresentationConstraint) pickupPresentationRebase = true;
        return true;
    }

    private bool PickupPresentationPoint(Point point, out Vector3 result)
    {
        result = default;
        if (!TryNavigationRay(point, out var origin, out var direction)) return false;
        var start = new Vector3((float)origin.X, (float)origin.Y, (float)origin.Z);
        var ray = new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z);
        float denominator = Vector3.Dot(ray, pickupPresentationNormal);
        if (Math.Abs(denominator) < 1e-7f) return false;
        float distance = Vector3.Dot(pickupPresentationPosition - start, pickupPresentationNormal) / denominator;
        result = start + ray * distance;
        return float.IsFinite(distance) && Finite(result);
    }

    private void MovePresentationPickup(Point point)
    {
        if (!PickupPresentationPoint(point, out var hit)) return;
        PublishTransformDrag(transformDraft with { Position = pickupPresentationPosition + pickupPresentationAxis * Vector3.Dot(hit - pickupPresentationHit, pickupPresentationAxis) });
        // This drag moves the draft itself, not Helix's handle target: the handles follow it now rather than on release.
        if (IsPickupDragging) PlacePickupHandles();
    }
    private float rotationPresentationAngle;
}
