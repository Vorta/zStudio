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
    public sealed record ViewPose(Point3D Position, Vector3D LookDirection, Vector3D UpDirection, double FieldOfView);
    public ViewPose CaptureView() => viewport.Camera is HCamera camera ? new(camera.Position, camera.LookDirection, camera.UpDirection, camera.FieldOfView) : throw new InvalidOperationException("No perspective camera.");
    public void RestoreView(ViewPose pose)
    {
        if (viewport.Camera is not HCamera camera) return;
        viewport.StopSpin(); rotationVelocity = default; rotationPoint = null;
        camera.Position = pose.Position; camera.LookDirection = pose.LookDirection; camera.UpDirection = pose.UpDirection; camera.FieldOfView = pose.FieldOfView;
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
    private sealed class FrameViewport(Action<TimeSpan> prepare) : Viewport3DX, IViewport3DX
    {
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
            UpdatePickupGizmoSize(); UpdateClipPlanes();
        }
        finally { preparingCamera = false; }
    }
    private void KeepCameraUpright()
    {
        if (viewport.Camera is not HCamera camera || camera.LookDirection.LengthSquared < 1e-12) return;
        var look = camera.LookDirection; double length = look.Length; look.Normalize();
        double yaw = Math.Atan2(look.X, -look.Z), pitch = Math.Asin(Math.Clamp(look.Y, -1, 1));
        if (look.X * look.X + look.Z * look.Z < 1e-12 || camera.UpDirection.Y < 0 && Math.Abs(lastForward.Y) > .98)
            yaw = Math.Atan2(lastForward.X, -lastForward.Z);
        var forward = UprightDirection(yaw, pitch);
        var target = camera.Position + camera.LookDirection;
        if ((forward - look).LengthSquared > 1e-20)
        {
            if (viewport.CameraMode == CameraMode.Inspect && !authoredCameraPose) camera.Position = target - forward * length;
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
    /// <summary>Rotate in screen pixels with an upright camera and a bounded pitch.</summary>
    public void RotateBy(double horizontal, double vertical)
    {
        if (IsFlyActive) { LookFlyBy(horizontal, vertical); return; }
        if (viewport.Camera is not HCamera camera || !viewport.IsRotationEnabled || camera.LookDirection.LengthSquared < 1e-12) return;
        var look = camera.LookDirection; double length = look.Length; look.Normalize();
        double speed = (viewport.CameraMode == CameraMode.Inspect ? -.5 : .1) * Math.PI / 180 * viewport.RotationSensitivity;
        var direction = UprightDirection(Math.Atan2(look.X, -look.Z) - horizontal * speed, Math.Asin(Math.Clamp(look.Y, -1, 1)) + vertical * speed);
        var target = camera.Position + camera.LookDirection;
        if (viewport.CameraMode == CameraMode.Inspect) camera.Position = target - direction * length;
        camera.LookDirection = direction * length;
        var right = Vector3D.CrossProduct(direction, new(0, 1, 0)); right.Normalize();
        camera.UpDirection = Vector3D.CrossProduct(right, direction);
    }
    private void ConfigureUprightRotation()
    {
        viewport.PreviewMouseDown += (_, e) =>
        {
            if (IsPickupDragging) { e.Handled = true; return; }
            rotationVelocity = default;
            if (e.ChangedButton != MouseButton.Right || Keyboard.Modifiers != ModifierKeys.None || !viewport.IsRotationEnabled) return;
            viewport.StopSpin();
            rotationInputTick = Stopwatch.GetTimestamp(); rotationPoint = e.GetPosition(viewport); viewport.CaptureMouse(); viewport.Focus(); e.Handled = true;
        };
        viewport.PreviewMouseMove += (_, e) =>
        {
            if (rotationPoint is not { } previous) return;
            var current = e.GetPosition(viewport); var delta = current - previous;
            double seconds = Math.Max(.008, Stopwatch.GetElapsedTime(rotationInputTick).TotalSeconds); rotationInputTick = Stopwatch.GetTimestamp();
            rotationVelocity = delta / seconds;
            RotateBy(delta.X, delta.Y); rotationPoint = current; e.Handled = true;
        };
        viewport.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Right || rotationPoint == null) return;
            if (Stopwatch.GetElapsedTime(rotationInputTick).TotalSeconds > .15 || !viewport.IsInertiaEnabled) rotationVelocity = default;
            rotationPoint = null; viewport.ReleaseMouseCapture(); viewport.InvalidateRender(); e.Handled = true;
        };
        viewport.LostMouseCapture += (_, _) => { if (rotationPoint != null) rotationVelocity = default; rotationPoint = null; };
    }
}
