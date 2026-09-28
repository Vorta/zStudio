using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Diagnostics;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    public sealed record ViewPose(Point3D Position, Vector3D LookDirection, Vector3D UpDirection, double FieldOfView,
        string Projection = "perspective", double? OrthographicWidth = null, string? AxisView = null, bool AutoPerspective = false,
        Point3D? OrbitPivot = null, double? NavigationReferenceDistance = null);
    private double perspectiveFieldOfView = 45;
    private string? axisView;
    private bool autoPerspective;
    private Point3D? orbitPivot;
    private double? navigationReferenceDistance;
    public ViewPose CaptureView() => viewport.Camera is ProjectionCamera camera
        ? new(camera.Position, camera.LookDirection, camera.UpDirection, camera is HCamera p ? p.FieldOfView : perspectiveFieldOfView,
            camera is OrthographicCamera ? "orthographic" : "perspective", (camera as OrthographicCamera)?.Width, axisView, autoPerspective,
            orbitPivot ?? camera.Position + camera.LookDirection, navigationReferenceDistance ?? camera.LookDirection.Length)
        : throw new InvalidOperationException("No projection camera.");
    public void RestoreView(ViewPose pose)
    {
        if (viewport.Camera is not ProjectionCamera previous) return;
        ProjectionCamera camera = pose.Projection == "orthographic"
            ? previous as NavigationOrthographicCamera ?? new NavigationOrthographicCamera()
            : previous as NavigationPerspectiveCamera ?? new NavigationPerspectiveCamera();
        viewport.StopSpin(); rotationVelocity = default; rotationPoint = null; pendingOrbitPoint = null;
        navigationVelocity = default;
        camera.NearPlaneDistance = previous.NearPlaneDistance; camera.FarPlaneDistance = previous.FarPlaneDistance;
        camera.Position = pose.Position; camera.LookDirection = pose.LookDirection; camera.UpDirection = pose.UpDirection;
        perspectiveFieldOfView = pose.FieldOfView;
        if (camera is HCamera perspective) perspective.FieldOfView = pose.FieldOfView;
        if (camera is OrthographicCamera orthographic) orthographic.Width = pose.OrthographicWidth ?? ViewWidth(pose);
        axisView = pose.AxisView; autoPerspective = pose.AutoPerspective;
        orbitPivot = pose.OrbitPivot ?? pose.Position + pose.LookDirection;
        navigationReferenceDistance = pose.NavigationReferenceDistance ?? pose.LookDirection.Length;
        if (!ReferenceEquals(camera, previous)) viewport.Camera = camera;
        cameraPoseDirty = true; viewport.InvalidateRender();
    }
    private bool preparingCamera, cameraPoseDirty = true, authoredCameraPose;
    private Point? rotationPoint;
    private Vector rotationVelocity;
    private long rotationInputTick;
    private TimeSpan previousFrameTime;
    private Vector3D lastForward = new(0, 0, -1);

    // Helix updates inertia here, immediately before copying the camera to the
    // render context. Preparing in CameraChanged instead observes partial poses
    // and recursively changes projection while another property is still updating.
    private sealed class FrameViewport : Viewport3DX, IViewport3DX
    {
        private readonly Action<TimeSpan> prepare;
        private readonly Action resize;
        public FrameViewport(Action<TimeSpan> prepare, Action resize)
        {
            this.prepare = prepare; this.resize = resize;
            Loaded += (_, _) => resize();
        }
        public override void OnApplyTemplate() { base.OnApplyTemplate(); resize(); }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); resize(); }
        void IViewport3DX.Update(TimeSpan timeStamp) { base.Update(timeStamp); prepare(timeStamp); }
        public void DrainNavigation(TimeSpan timestamp) => base.Update(timestamp);
    }
    private void CameraChanged()
    {
        if (preparingCamera || updatingClipping) return;
        cameraPoseDirty = true;
        viewport.InvalidateRender();
    }
    private void PrepareCameraFrame(TimeSpan timeStamp)
    {
        if (preparingCamera) return;
        preparingCamera = true;
        try
        {
            PrepareFlyFrame(timeStamp);
            if (IsPickupDragging && pickupCameraPose != null) RestoreView(pickupCameraPose);
            double seconds = Math.Clamp((timeStamp - previousFrameTime).TotalSeconds, 0, .05); previousFrameTime = timeStamp;
            AdvanceNavigationInertia(seconds);
            if (rotationPoint == null && rotationVelocity.LengthSquared > 1 && viewport.IsInertiaEnabled)
            {
                RotateBy(rotationVelocity.X * seconds, rotationVelocity.Y * seconds); cameraPoseDirty = true;
                rotationVelocity *= Math.Pow(Math.Clamp(viewport.CameraInertiaFactor, .01, .99), seconds / .02);
                viewport.InvalidateRender();
            }
            if (cameraPoseDirty)
            {
                KeepCameraUpright();
                RefreshAnimationCamera();
                RefreshHorizon();
                cameraPoseDirty = false; authoredCameraPose = false;
            }
            UpdatePickupGizmoSize(); UpdateClipPlanes(); UpdateAiMarkers(); RefreshInspection();
        }
        finally { preparingCamera = false; }
    }
    private void KeepCameraUpright()
    {
        if (viewport.Camera is not ProjectionCamera camera || camera.LookDirection.LengthSquared < 1e-12) return;
        if (axisView is "top" or "bottom" && !authoredCameraPose) return;
        var look = camera.LookDirection; double length = look.Length; look.Normalize();
        double yaw = Math.Atan2(look.X, -look.Z), pitch = Math.Asin(Math.Clamp(look.Y, -1, 1));
        if (look.X * look.X + look.Z * look.Z < 1e-12 || camera.UpDirection.Y < 0 && Math.Abs(lastForward.Y) > .98)
            yaw = Math.Atan2(lastForward.X, -lastForward.Z);
        var forward = UprightDirection(yaw, pitch);
        if ((forward - look).LengthSquared > 1e-20)
        {
            if (viewport.CameraMode == CameraMode.Inspect && !authoredCameraPose) RotateEyeAboutPivot(camera, forward);
            camera.LookDirection = forward * length;
        }
        var right = Vector3D.CrossProduct(forward, new(0, 1, 0)); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        if ((camera.UpDirection - up).LengthSquared > 1e-20) camera.UpDirection = up;
        lastForward = forward;
    }
    private static Vector3D UprightDirection(double yaw, double pitch)
    {
        pitch = Math.Clamp(pitch, -89 * Math.PI / 180, 89 * Math.PI / 180);
        return new(Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
    }
    /// <summary>Clamp an explicit camera pose before immediate navigation, preserving its eye and look length.</summary>
    public static ViewPose UprightPose(ViewPose pose)
    {
        var look = pose.LookDirection;
        double length = look.Length;
        if (!double.IsFinite(length) || length < 1e-6) throw new ArgumentException("A finite nonzero look direction is required.", nameof(pose));
        look /= length;
        var forward = UprightDirection(Math.Atan2(look.X, -look.Z), Math.Asin(Math.Clamp(look.Y, -1, 1)));
        var right = Vector3D.CrossProduct(forward, new(0, 1, 0)); right.Normalize();
        return pose with { LookDirection = forward * length, UpDirection = Vector3D.CrossProduct(right, forward), AxisView = null, AutoPerspective = false };
    }
    /// <summary>Rotate in screen pixels with an upright camera and a bounded pitch.</summary>
    public void RotateBy(double horizontal, double vertical)
    {
        if (IsFlyActive) { LookFlyBy(horizontal, vertical); return; }
        if (IsPickupDragging || !viewport.IsRotationEnabled || horizontal == 0 && vertical == 0) return;
        ManualNavigationStarting?.Invoke();
        if (autoPerspective) ChangeProjection("perspective");
        string? previousAxis = axisView; axisView = null; autoPerspective = false;
        if (viewport.Camera is not ProjectionCamera camera || camera.LookDirection.LengthSquared < 1e-12) return;
        var look = camera.LookDirection; double length = look.Length; look.Normalize();
        double speed = (viewport.CameraMode == CameraMode.Inspect ? -.5 : .1) * Math.PI / 180 * viewport.RotationSensitivity;
        double yaw = previousAxis is "top" or "bottom" ? 0 : Math.Atan2(look.X, -look.Z);
        var direction = UprightDirection(yaw - horizontal * speed, Math.Asin(Math.Clamp(look.Y, -1, 1)) + vertical * speed);
        if (viewport.CameraMode == CameraMode.Inspect) RotateEyeAboutPivot(camera, direction);
        camera.LookDirection = direction * length;
        var right = Vector3D.CrossProduct(direction, new(0, 1, 0)); right.Normalize();
        camera.UpDirection = Vector3D.CrossProduct(right, direction);
    }

    // Rotate the eye offset and viewing basis together. An off-center picked
    // surface keeps its screen position, including when orbit leaves an axis view.
    private void RotateEyeAboutPivot(ProjectionCamera camera, Vector3D direction)
    {
        var pivot = orbitPivot ?? camera.Position + camera.LookDirection;
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var nextRight = Vector3D.CrossProduct(direction, new(0, 1, 0)); nextRight.Normalize();
        var nextUp = Vector3D.CrossProduct(nextRight, direction);
        var offset = camera.Position - pivot;
        camera.Position = pivot + nextRight * Vector3D.DotProduct(offset, right)
            + nextUp * Vector3D.DotProduct(offset, up) + direction * Vector3D.DotProduct(offset, forward);
        orbitPivot = pivot;
    }
}
