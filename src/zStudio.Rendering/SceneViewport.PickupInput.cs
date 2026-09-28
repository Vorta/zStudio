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
        if (!CanManipulate ||
            pickupManipulator?.Visibility != Visibility.Visible || viewport.Camera is not HelixToolkit.Wpf.SharpDX.ProjectionCamera camera ||
            !IsInsidePickupViewport(point)) return null;
        var origin = transformDraft.Position;
        var start = viewport.Project(new Point3D(origin.X, origin.Y, origin.Z));
        double nearest = PickupHandleHitRadius * PickupHandleHitRadius;
        MeshGeometryModel3D? chosen = null;
        Vector3 hitPoint = origin;
        foreach (var arrow in pickupArrows)
        {
            if (TransformMode == "rotate")
            {
                var basis = arrow.Transform?.Value ?? Matrix3D.Identity;
                for (int i = 0; i < 64; i++)
                {
                    Vector3 Ring(int index)
                    {
                        double angle = index * Math.PI / 32;
                        var p = basis.Transform(new Point3D(0, Math.Cos(angle), Math.Sin(angle)));
                        return origin + new Vector3((float)p.X, (float)p.Y, (float)p.Z) * (float)pickupGizmoSize;
                    }
                    var a = Ring(i); var b = Ring(i + 1);
                    var pa = new Point3D(a.X, a.Y, a.Z); var pb = new Point3D(b.X, b.Y, b.Z);
                    if (Vector3D.DotProduct(pa - camera.Position, camera.LookDirection) <= 0 || Vector3D.DotProduct(pb - camera.Position, camera.LookDirection) <= 0) continue;
                    var screenA = viewport.Project(pa); var line = viewport.Project(pb) - screenA;
                    if (!double.IsFinite(line.LengthSquared) || line.LengthSquared < 1e-8) continue;
                    double ringAlong = Math.Clamp(System.Windows.Vector.Multiply(point - screenA, line) / line.LengthSquared, 0, 1);
                    double ringDistance = (point - (screenA + line * ringAlong)).LengthSquared;
                    if (ringDistance > nearest) continue;
                    nearest = ringDistance; chosen = arrow; hitPoint = Vector3.Lerp(a, b, (float)ringAlong);
                }
                continue;
            }
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
        return chosen == null ? null : new HitTestResult { ModelHit = chosen, PointHit = hitPoint, IsValid = true, Distance = 0 };
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
        var down = new MouseDown3DEventArgs(arrow, hit, point, viewport, e);
        if (TransformMode == "rotate")
        {
            var axis = (arrow.Transform?.Value ?? Matrix3D.Identity).Transform(new Vector3D(1, 0, 0));
            if (BeginPickupDrag(down)) BeginRotationPointer(point, new((float)axis.X, (float)axis.Y, (float)axis.Z), hit.PointHit);
        }
        else arrow.RaiseEvent(down);
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
            if (TransformMode == "rotate") MoveRotationPointer(point);
            else arrow.RaiseEvent(new MouseMove3DEventArgs(arrow, activePickupHandle, point, viewport));
            return true;
        }
        SetPickupHover(Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released &&
            Mouse.MiddleButton == MouseButtonState.Released && PickPickupHandle(point) != null);
        return false;
    }

    internal bool HandlePickupPointerUp(Point point, MouseButtonEventArgs e)
    {
        if (IsFlyActive) return true;
        if (!IsPickupDragging || e.ChangedButton != MouseButton.Left) return false;
        if (!IsInsidePickupViewport(point)) { CancelPickupDrag(); return true; }
        if (TransformMode == "move" && activePickupHandle?.ModelHit is MeshGeometryModel3D arrow)
            arrow.RaiseEvent(new MouseUp3DEventArgs(arrow, activePickupHandle, point, viewport, e));
        activePickupHandle = null; IsPickupDragging = false;
        ResumePickupCamera(); viewport.ReleaseMouseCapture();
        // Completing a gesture updates the card draft only. The document service
        // accepts one combined transform when the card is explicitly confirmed.
        if (selectedPickup is int root) PickupMoveCommitted?.Invoke(root, transformDraft.Position);
        RefreshPickupSelection();
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
