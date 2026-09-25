using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    private Vector3D flyMovement;
    private TimeSpan? flyFrameTime;
    private bool flySpeedInitialized;
    private bool savedFlyInertia, savedFlyMove, savedFlyPan, savedFlyRotate, savedFlyZoom;
    public bool IsFlyActive { get; private set; }
    public double FlySpeed { get; private set; } = 100;
    public event Action? FlyStateChanged;

    public void SetFly(bool enabled)
    {
        if (IsFlyActive == enabled) return;
        CancelPickupDrag(); SetPickupHover(false);
        StopNavigationMotion();
        rotationVelocity = default; rotationPoint = null; flyMovement = default; flyFrameTime = null;
        if (enabled)
        {
            if (!flySpeedInitialized)
            {
                FlySpeed = FlyCameraMotion.InitialSpeed((sceneMax - sceneMin).Length());
                flySpeedInitialized = true;
            }
            savedFlyInertia = viewport.IsInertiaEnabled; savedFlyMove = viewport.IsMoveEnabled;
            savedFlyPan = viewport.IsPanEnabled; savedFlyRotate = viewport.IsRotationEnabled; savedFlyZoom = viewport.IsZoomEnabled;
            viewport.IsInertiaEnabled = viewport.IsMoveEnabled = viewport.IsPanEnabled = viewport.IsRotationEnabled = viewport.IsZoomEnabled = false;
        }
        else
        {
            viewport.IsInertiaEnabled = savedFlyInertia; viewport.IsMoveEnabled = savedFlyMove;
            viewport.IsPanEnabled = savedFlyPan; viewport.IsRotationEnabled = savedFlyRotate; viewport.IsZoomEnabled = savedFlyZoom;
        }
        IsFlyActive = enabled;
        viewport.CameraMode = enabled ? CameraMode.WalkAround : CameraMode.Inspect;
        cameraPoseDirty = true; viewport.InvalidateRender(); FlyStateChanged?.Invoke();
    }

    /// <summary>Signed right/up/forward input; speed is normalized for combined axes.</summary>
    public void SetFlyMovement(int right, int up, int forward)
    {
        if (!IsFlyActive) return;
        var next = new Vector3D(Math.Sign(right), Math.Sign(up), Math.Sign(forward));
        if (flyMovement == next) return;
        // The first held key must not inherit time spent idle.
        if (flyMovement.LengthSquared == 0) flyFrameTime = null;
        flyMovement = next; viewport.InvalidateRender();
    }

    public void LookFlyBy(double horizontal, double vertical)
    {
        if (!IsFlyActive || viewport.Camera is not HCamera camera) return;
        camera.LookDirection = FlyCameraMotion.Look(camera.LookDirection, horizontal, vertical);
        cameraPoseDirty = true; viewport.InvalidateRender();
    }

    public void AdjustFlySpeed(int wheelDelta)
    {
        if (!IsFlyActive || wheelDelta == 0) return;
        FlySpeed = FlyCameraMotion.AdjustSpeed(FlySpeed, wheelDelta); FlyStateChanged?.Invoke();
    }

    private void PrepareFlyFrame(TimeSpan timestamp)
    {
        if (!IsFlyActive || viewport.Camera is not HCamera camera) return;
        double seconds = flyFrameTime is { } previous ? (timestamp - previous).TotalSeconds : 0;
        flyFrameTime = timestamp;
        if (flyMovement.LengthSquared == 0) return;
        camera.Position += FlyCameraMotion.Displacement(camera.LookDirection, flyMovement, FlySpeed, seconds);
        cameraPoseDirty = true; viewport.InvalidateRender();
    }

    private void StopNavigationMotion()
    {
        // Helix exposes StopSpin but not its full controller. One update with zero
        // inertia drains pan/zoom/move/rotate forces; restore the exact pose afterward.
        var pose = CaptureView();
        bool inertia = viewport.IsInertiaEnabled, rotation = viewport.IsRotationEnabled;
        try
        {
            viewport.IsInertiaEnabled = false; viewport.IsRotationEnabled = true;
            viewport.ChangeDirection(pose.LookDirection, pose.UpDirection, 0);
            ((FrameViewport)viewport).DrainNavigation(previousFrameTime + TimeSpan.FromMilliseconds(1));
            RestoreView(pose);
        }
        finally { viewport.IsInertiaEnabled = inertia; viewport.IsRotationEnabled = rotation; }
    }
}
