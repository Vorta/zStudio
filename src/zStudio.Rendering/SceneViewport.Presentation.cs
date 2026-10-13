using System.Numerics;
using Vector = System.Windows.Vector;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    private static readonly DependencyProperty PresentationGuidesVisibleProperty = DependencyProperty.Register(
        nameof(PresentationGuidesVisible), typeof(bool), typeof(SceneViewport), new PropertyMetadata(true));
    public bool PresentationGuidesVisible => (bool)GetValue(PresentationGuidesVisibleProperty);
    public bool PresentationUiHidden { get; private set; }
    private readonly Grid presentationOverlays = new();

    /// <summary>Transient editor chrome only. Guide preferences, geometry and editing sessions stay intact.</summary>
    public void SetPresentationUiHidden(bool hidden)
    {
        if (PresentationUiHidden == hidden) return;
        PresentationUiHidden = hidden;
        SetValue(PresentationGuidesVisibleProperty, !hidden);
        viewport.ShowViewCube = viewport.ShowCoordinateSystem = !hidden;
        presentationOverlays.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        SetPickupHover(false);
        RebasePresentationPointers();
        ++inspectionSerial;
        viewport.InvalidateRender();
    }

    // IsRendering is independent of each guide's normal Visibility. Refreshes may change that
    // preference while hidden, and newly created guides inherit the override before publication.
    private void PresentationGuide(Element3D guide) => BindingOperations.SetBinding(guide, Element3D.IsRenderingProperty,
        new Binding(nameof(PresentationGuidesVisible)) { Source = this, Mode = BindingMode.OneWay });

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
        if (PickupPresentationPoint(point, out var hit))
            PublishTransformDrag(transformDraft with { Position = pickupPresentationPosition + pickupPresentationAxis * Vector3.Dot(hit - pickupPresentationHit, pickupPresentationAxis) });
    }
    private float rotationPresentationAngle;
}
