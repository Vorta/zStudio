using System.Windows.Media.Media3D;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private Vector3D flyMovement;
    private TimeSpan? flyFrameTime;
    private bool flySpeedInitialized;
    private bool savedFlyInertia, savedFlyPan, savedFlyRotate, savedFlyZoom;
    public bool IsFlyActive { get; private set; }
    public double FlySpeed { get; private set; } = 100;
    public event Action? FlyStateChanged;

    /// <summary>Checks editing conflicts without changing focus, input capture or scene state.</summary>
    public bool CanStartFly(out string? error)
    {
        error = AiPathEditingNetwork != null || HasParachutePreview || IsParachutePointerMoving
            ? "Finish AI path or drop-window editing before starting Fly camera." : null;
        return error == null;
    }

    public void SetFly(bool enabled)
    {
        if (IsFlyActive == enabled) return;
        if (enabled && !CanStartFly(out string? error))
            throw new InvalidOperationException(error);
        CancelPickupDrag(); SetPickupHover(false);
        StopNavigationMotion();
        rotationVelocity = default; rotationPoint = null; flyMovement = default; flyFrameTime = null;
        if (enabled)
        {
            DisengageWorldAnimationCamera();
            ManualNavigationStarting?.Invoke();
            ZonePaintActive = false;
            RestoreView(UprightPose(CaptureView() with { Projection = "perspective", OrthographicWidth = null }));
            if (!flySpeedInitialized)
            {
                FlySpeed = FlyCameraMotion.InitialSpeed((sceneMax - sceneMin).Length());
                flySpeedInitialized = true;
            }
            savedFlyInertia = navigationInertia; savedFlyPan = panEnabled; savedFlyRotate = rotationEnabled; savedFlyZoom = zoomEnabled;
            navigationInertia = panEnabled = rotationEnabled = zoomEnabled = false;
        }
        else
        {
            navigationInertia = savedFlyInertia; panEnabled = savedFlyPan; rotationEnabled = savedFlyRotate; zoomEnabled = savedFlyZoom;
            orbitPivot = null; navigationReferenceDistance = null;
        }
        IsFlyActive = enabled;
        if (!enabled) RefreshTerrainBrushCursor();
        walkAroundCamera = enabled;
        cameraPoseDirty = true; view.InvalidateRender(); FlyStateChanged?.Invoke();
    }

    /// <summary>Signed right/up/forward input; speed is normalized for combined axes.</summary>
    public void SetFlyMovement(int right, int up, int forward)
    {
        if (!IsFlyActive) return;
        var next = new Vector3D(Math.Sign(right), Math.Sign(up), Math.Sign(forward));
        if (flyMovement == next) return;
        // The first held key must not inherit time spent idle.
        if (flyMovement.LengthSquared == 0) flyFrameTime = null;
        flyMovement = next; view.InvalidateRender();
    }

    public void LookFlyBy(double horizontal, double vertical)
    {
        if (!IsFlyActive || view.Camera is not PerspectiveRenderCamera camera) return;
        camera.LookDirection = FlyCameraMotion.Look(camera.LookDirection, horizontal, vertical);
        cameraPoseDirty = true; view.InvalidateRender();
    }

    public void AdjustFlySpeed(int wheelDelta)
    {
        if (!IsFlyActive || wheelDelta == 0) return;
        FlySpeed = FlyCameraMotion.AdjustSpeed(FlySpeed, wheelDelta); FlyStateChanged?.Invoke();
    }

    private void PrepareFlyFrame(TimeSpan timestamp)
    {
        if (!IsFlyActive || view.Camera is not PerspectiveRenderCamera camera) return;
        double seconds = flyFrameTime is { } previous ? (timestamp - previous).TotalSeconds : 0;
        flyFrameTime = timestamp;
        if (flyMovement.LengthSquared == 0) return;
        camera.Position += FlyCameraMotion.Displacement(camera.LookDirection, flyMovement, FlySpeed, seconds);
        cameraPoseDirty = true; view.InvalidateRender();
    }

    // Restoring the exact current pose clears the inertia and pending orbit state.
    private void StopNavigationMotion() => RestoreView(CaptureView());
}
