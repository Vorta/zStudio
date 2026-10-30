using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private ViewPose? overlayPose;
    private double overlayWidth, overlayHeight;
    /// <summary>
    /// The camera's pose or the view's size changed this frame: screen-space overlays drawn over the view (guide time dots, range
    /// rings, the player pin) project their world points again.
    /// </summary>
    public event Action? ViewChanged;

    private void ReportOverlayView()
    {
        if (ViewChanged == null || view.Camera is not RenderCamera) return;
        var pose = CaptureView();
        if (pose == overlayPose && overlayWidth == viewport.ActualWidth && overlayHeight == viewport.ActualHeight) return;
        overlayPose = pose; overlayWidth = viewport.ActualWidth; overlayHeight = viewport.ActualHeight;
        ViewChanged.Invoke();
    }

    /// <summary>
    /// Projects a world point into <see cref="RenderSurface"/> coordinates with the current camera (as picking does).
    /// <paramref name="inFront"/> is false for a point behind the eye: <paramref name="screen"/> then lies on the side the point
    /// is on (mirrored through the view's centre), so an edge arrow can point towards it. Returns false without a camera.
    /// </summary>
    public bool ProjectToView(Vector3 position, out Point screen, out bool inFront)
    {
        screen = default; inFront = false;
        if (view.Camera is not RenderCamera camera || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0 || !Finite(position)) return false;
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var offset = new Point3D(position.X, position.Y, position.Z) - camera.Position;
        double depth = Vector3D.DotProduct(offset, forward);
        bool orthographic = camera is OrthographicRenderCamera;
        inFront = orthographic || depth > 1e-6;
        var projection = camera.CreateProjectionMatrix(Aspect);
        double divisor = orthographic ? 1 : Math.Max(1e-6, Math.Abs(depth));
        double x = Vector3D.DotProduct(offset, right) * projection.M11 / divisor;
        double y = Vector3D.DotProduct(offset, up) * projection.M22 / divisor;
        // Behind the eye (divided by the distance's magnitude) the point keeps its side: pushed far out, it points the way the
        // camera would turn to see it; straight behind reads as below.
        if (!inFront) { x *= 1e3; y *= 1e3; if (Math.Abs(x) < 1e-6 && Math.Abs(y) < 1e-6) y = -1e3; }
        screen = new((x + 1) * viewport.ActualWidth / 2, (1 - y) * viewport.ActualHeight / 2);
        return double.IsFinite(screen.X) && double.IsFinite(screen.Y);
    }
}
