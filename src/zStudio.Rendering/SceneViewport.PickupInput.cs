using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using HitTestResult = HelixToolkit.SharpDX.HitTestResult;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    // WPF logical pixels: the entire shaft and tip get a 24-DIP-wide target at every zoom/DPI.
    private const double PickupHandleHitRadius = 12;
    private readonly List<MeshGeometryModel3D> pickupArrows = [];
    private HitTestResult? activePickupHandle;
    private bool pickupHover;
    private Cursor? pickupPreviousCursor;

    private bool IsInsidePickupViewport(Point point) => point.X >= 0 && point.Y >= 0 &&
        point.X <= viewport.ActualWidth && point.Y <= viewport.ActualHeight;

    private HitTestResult? PickPickupHandle(Point point)
    {
        if (pickupLocked || !pickupEditable || selectedPickup is not int root ||
            pickupManipulator?.Visibility != Visibility.Visible || viewport.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera ||
            !IsInsidePickupViewport(point)) return null;
        var origin = pickupPositions[root] + pickupManipulator.CenterOffset;
        var start = viewport.Project(new Point3D(origin.X, origin.Y, origin.Z));
        double nearest = PickupHandleHitRadius * PickupHandleHitRadius;
        MeshGeometryModel3D? chosen = null;
        foreach (var arrow in pickupArrows)
        {
            var axis = (arrow.Transform?.Value ?? Matrix3D.Identity).Transform(new Vector3D(1, 0, 0));
            var tip = new Point3D(origin.X, origin.Y, origin.Z) + axis * (pickupManipulator.SizeScale * 1.8);
            // Do not select the mirrored projection of an endpoint behind the camera.
            if (Vector3D.DotProduct(tip - camera.Position, camera.LookDirection) <= 0) continue;
            var end = viewport.Project(tip); var segment = end - start;
            if (!double.IsFinite(segment.LengthSquared) || segment.LengthSquared < 1) continue;
            double along = Math.Clamp(System.Windows.Vector.Multiply(point - start, segment) / segment.LengthSquared, 0, 1);
            double distance = (point - (start + segment * along)).LengthSquared;
            if (distance > nearest) continue;
            nearest = distance; chosen = arrow;
        }
        return chosen == null ? null : new HitTestResult { ModelHit = chosen, PointHit = origin, IsValid = true, Distance = 0 };
    }

    internal bool HandlePickupPointerDown(Point point, MouseButtonEventArgs e)
    {
        if (IsFlyActive) return true;
        if (IsPickupDragging) return true;
        SetPickupHover(false);
        if (e.ChangedButton != MouseButton.Left) return false;
        if (PickPickupHandle(point) is not { ModelHit: MeshGeometryModel3D arrow } hit) return false;
        // A rejected handle click must not fall through to Helix and start a drag.
        if (CanStartPickupEdit?.Invoke() == false) return true;
        PickupInteractionStarting?.Invoke();
        // Use the native axis constraint, but own routing/capture for the whole drag. Do not let
        // scene depth or another triangle choose a different target after this screen-space pick.
        arrow.RaiseEvent(new MouseDown3DEventArgs(arrow, hit, point, viewport, e));
        if (IsPickupDragging) activePickupHandle = hit;
        return true;
    }

    internal bool HandlePickupPointerMove(Point point)
    {
        if (IsFlyActive) return true;
        if (IsPickupDragging && activePickupHandle?.ModelHit is MeshGeometryModel3D arrow)
        {
            // Captured input can arrive outside without MouseLeave. Check after the handle
            // starts; CaptureMouse can synchronously resend an earlier pointer position.
            if (!IsInsidePickupViewport(point)) { CancelPickupDrag(); return true; }
            arrow.RaiseEvent(new MouseMove3DEventArgs(arrow, activePickupHandle, point, viewport));
            return true;
        }
        SetPickupHover(Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released &&
            Mouse.MiddleButton == MouseButtonState.Released && PickPickupHandle(point) != null);
        return false;
    }

    internal bool HandlePickupPointerUp(Point point, MouseButtonEventArgs e)
    {
        if (IsFlyActive) return true;
        if (!IsPickupDragging || e.ChangedButton != MouseButton.Left || selectedPickup is not int root) return false;
        if (!IsInsidePickupViewport(point)) { CancelPickupDrag(); return true; }
        if (activePickupHandle?.ModelHit is MeshGeometryModel3D arrow)
            arrow.RaiseEvent(new MouseUp3DEventArgs(arrow, activePickupHandle, point, viewport, e));
        Vector3 position = pickupPositions[root]; activePickupHandle = null; IsPickupDragging = false;
        ResumePickupCamera(); viewport.ReleaseMouseCapture();
        PickupMoveCommitted?.Invoke(root, position);
        SetPickupHover(PickPickupHandle(point) != null);
        return true;
    }

    private void SetPickupHover(bool value)
    {
        if (pickupHover == value) return;
        pickupHover = value;
        if (value) { pickupPreviousCursor = viewport.Cursor; viewport.Cursor = Cursors.Hand; }
        else { viewport.Cursor = pickupPreviousCursor; pickupPreviousCursor = null; }
    }
}
