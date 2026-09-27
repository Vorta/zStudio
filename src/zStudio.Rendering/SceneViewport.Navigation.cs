using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    public event Action? ManualNavigationStarting;
    public int? FramingSelection { get; private set; }
    public void SelectFramingNode(int? node) { FramingSelection = node; if (node != null && SelectedAiNode != null) SelectAiNode(null); }
    internal enum NavigationGesture { None, Orbit, Pan, Zoom, Dolly }
    private NavigationGesture navigationGesture;
    private Vector navigationVelocity;
    private Window? navigationWindow;
    private double Aspect => Math.Max(1, viewport.ActualWidth) / Math.Max(1, viewport.ActualHeight);
    private double ViewWidth(ViewPose pose) => pose.Projection == "orthographic" && pose.OrthographicWidth is { } width
        ? width : 2 * pose.LookDirection.Length * Math.Tan(pose.FieldOfView * Math.PI / 360) * Aspect;
    private double UnitsPerPixel => ViewWidth(CaptureView()) / Math.Max(1, viewport.ActualWidth);

    internal static NavigationGesture Gesture(MouseButton button, ModifierKeys modifiers) => button != MouseButton.Middle ? NavigationGesture.None : modifiers switch
    {
        ModifierKeys.None => NavigationGesture.Orbit, ModifierKeys.Shift => NavigationGesture.Pan,
        ModifierKeys.Control => NavigationGesture.Zoom, ModifierKeys.Control | ModifierKeys.Shift => NavigationGesture.Dolly,
        _ => NavigationGesture.None
    };

    public bool BeginManualNavigation()
    {
        if (IsFlyActive || IsPickupDragging) return false;
        ManualNavigationStarting?.Invoke();
        return true;
    }
    public void StopCameraMotion() { CancelNavigation(); StopNavigationMotion(); }
    public void PrepareAuthoredCamera()
    {
        StopCameraMotion(); ChangeProjection("perspective"); axisView = null; autoPerspective = false;
    }
    public void OrbitStep(double horizontalDegrees, double verticalDegrees)
    {
        StopCameraMotion();
        double sensitivity = Math.Max(.0001, viewport.RotationSensitivity) * .5;
        RotateBy(horizontalDegrees / sensitivity, verticalDegrees / sensitivity);
    }

    public void SetProjection(string projection)
    {
        if (projection is not ("perspective" or "orthographic")) throw new ArgumentException("Use perspective or orthographic.", nameof(projection));
        if (!BeginManualNavigation()) return;
        StopCameraMotion(); ChangeProjection(projection); autoPerspective = false;
    }
    private void ChangeProjection(string projection)
    {
        var pose = CaptureView();
        if (pose.Projection == projection) return;
        double width = ViewWidth(pose);
        if (projection == "orthographic") RestoreView(pose with { Projection = projection, OrthographicWidth = width });
        else
        {
            var direction = pose.LookDirection; direction.Normalize();
            double distance = width / (2 * Math.Tan(pose.FieldOfView * Math.PI / 360) * Aspect);
            var target = pose.Position + pose.LookDirection;
            RestoreView(pose with { Projection = projection, OrthographicWidth = null, Position = target - direction * distance, LookDirection = direction * distance });
        }
    }

    public void SetAxisView(string view)
    {
        (Vector3D Look, Vector3D Up) basis = view switch
        {
            "front" => (new(0, 0, 1), new(0, 1, 0)), "back" => (new(0, 0, -1), new(0, 1, 0)),
            "right" => (new(-1, 0, 0), new(0, 1, 0)), "left" => (new(1, 0, 0), new(0, 1, 0)),
            "top" => (new(0, -1, 0), new(0, 0, -1)), "bottom" => (new(0, 1, 0), new(0, 0, 1)),
            _ => throw new ArgumentException("Unknown axis view.", nameof(view))
        };
        var (look, up) = basis;
        if (!BeginManualNavigation()) return;
        StopCameraMotion(); ChangeProjection("orthographic");
        var pose = CaptureView(); var target = pose.Position + pose.LookDirection;
        look *= pose.LookDirection.Length;
        RestoreView(pose with { Position = target - look, LookDirection = look, UpDirection = up, AxisView = view, AutoPerspective = true });
    }
    public void OppositeView()
    {
        if (!BeginManualNavigation()) return;
        if (axisView is { } axis)
        {
            SetAxisView(axis switch { "front" => "back", "back" => "front", "left" => "right", "right" => "left", "top" => "bottom", _ => "top" });
            return;
        }
        StopCameraMotion(); var pose = CaptureView();
        RestoreView(UprightPose(pose with { Position = pose.Position + pose.LookDirection * 2, LookDirection = -pose.LookDirection }));
    }
    public void PanBy(double horizontal, double vertical)
    {
        if (!BeginManualNavigation() || !viewport.IsPanEnabled || viewport.Camera is not ProjectionCamera camera) return;
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        camera.Position += (-right * horizontal + up * vertical) * UnitsPerPixel;
    }
    /// <summary>Signed wheel-equivalent steps; positive zooms in, around the current target.</summary>
    public void ZoomBy(double steps)
    {
        if (!BeginManualNavigation() || !viewport.IsZoomEnabled || viewport.Camera is not ProjectionCamera camera) return;
        double factor = Math.Exp(-Math.Clamp(steps, -100, 100) * .12);
        if (camera is OrthographicCamera orthographic) orthographic.Width = Math.Clamp(orthographic.Width * factor, .001, 1e12);
        else
        {
            var target = camera.Position + camera.LookDirection;
            double distance = camera.LookDirection.Length;
            double next = Math.Clamp(distance * factor, minimumClipDistance * 4, 1e12);
            camera.LookDirection *= next / distance; camera.Position = target - camera.LookDirection;
        }
    }
    /// <summary>Move eye and orbit target together along the view direction, in game units.</summary>
    public void DollyBy(double distance)
    {
        if (!BeginManualNavigation() || viewport.Camera is not ProjectionCamera camera) return;
        var direction = camera.LookDirection; direction.Normalize(); camera.Position += direction * distance;
    }
    internal void ApplyNavigationDelta(NavigationGesture gesture, Vector delta)
    {
        switch (gesture)
        {
            case NavigationGesture.Orbit: RotateBy(delta.X, delta.Y); break;
            case NavigationGesture.Pan: PanBy(delta.X, delta.Y); break;
            case NavigationGesture.Zoom: ZoomBy(delta.Y / 40); break;
            case NavigationGesture.Dolly: DollyBy(delta.Y * UnitsPerPixel); break;
        }
    }
    private void AdvanceNavigationInertia(double seconds)
    {
        if (rotationPoint != null || !viewport.IsInertiaEnabled || navigationVelocity.LengthSquared <= 1) return;
        ApplyNavigationDelta(navigationGesture, navigationVelocity * seconds);
        // CameraChanged is deliberately suppressed during frame preparation.
        // Refresh camera-dependent horizons/billboards for inertial motion too.
        cameraPoseDirty = true;
        navigationVelocity *= Math.Pow(Math.Clamp(viewport.CameraInertiaFactor, .01, .99), seconds / .02);
        viewport.InvalidateRender();
    }
    internal void BeginNavigationDrag(NavigationGesture gesture, Point position)
    {
        if (gesture == NavigationGesture.None || !BeginManualNavigation()) return;
        StopNavigationMotion(); navigationGesture = gesture;
        rotationPoint = position; rotationInputTick = Stopwatch.GetTimestamp();
    }
    internal void MoveNavigationDrag(Point current)
    {
        if (rotationPoint is not { } previous) return;
        var delta = current - previous;
        double seconds = Math.Max(.008, Stopwatch.GetElapsedTime(rotationInputTick).TotalSeconds);
        rotationInputTick = Stopwatch.GetTimestamp();
        ApplyNavigationDelta(navigationGesture, delta);
        rotationPoint = current;
        if (navigationGesture == NavigationGesture.Orbit) rotationVelocity = delta / seconds;
        else navigationVelocity = delta / seconds;
    }
    internal void EndNavigationDrag()
    {
        if (Stopwatch.GetElapsedTime(rotationInputTick).TotalSeconds > .15 || !viewport.IsInertiaEnabled)
        { rotationVelocity = default; navigationVelocity = default; }
        rotationPoint = null; viewport.InvalidateRender();
    }
    public void CancelNavigation()
    {
        rotationVelocity = default; navigationVelocity = default; rotationPoint = null;
        if (viewport.IsMouseCaptured && !IsPickupDragging && !IsFlyActive) viewport.ReleaseMouseCapture();
    }
    private void ConfigureNavigation()
    {
        ConfigureNavigationCube();
        // Parent tunneling precedes Helix's class-level hit testing and view-cube handler.
        PreviewMouseDown += (_, e) =>
        {
            if (e.Handled || IsFlyActive || IsPickupDragging) return;
            if (e.ChangedButton == MouseButton.Left && NavigateCubeAt(e.GetPosition(viewport))) { e.Handled = true; return; }
            var gesture = Gesture(e.ChangedButton, Keyboard.Modifiers);
            if (gesture == NavigationGesture.None) return;
            BeginNavigationDrag(gesture, e.GetPosition(viewport));
            viewport.CaptureMouse(); viewport.Focus(); e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (rotationPoint == null) return;
            MoveNavigationDrag(e.GetPosition(viewport)); e.Handled = true;
        };
        PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle || rotationPoint == null) return;
            EndNavigationDrag(); viewport.ReleaseMouseCapture(); e.Handled = true;
        };
        PreviewMouseWheel += (_, e) =>
        {
            if (IsFlyActive) return;
            if (!IsPickupDragging)
            {
                StopCameraMotion(); ZoomBy(e.Delta / 120.0);
                navigationGesture = NavigationGesture.Zoom;
                if (viewport.IsInertiaEnabled) navigationVelocity = new(0, e.Delta / 120.0 * 80);
            }
            e.Handled = true;
        };
        viewport.LostMouseCapture += (_, _) => { if (rotationPoint != null) CancelNavigation(); };
        viewport.LostKeyboardFocus += (_, _) => { if (rotationPoint != null) CancelNavigation(); };
        Unloaded += (_, _) => { CancelNavigation(); AttachNavigationWindow(null); };
        Loaded += (_, _) => AttachNavigationWindow(Window.GetWindow(this));
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelNavigation(); };
    }
    private void AttachNavigationWindow(Window? window)
    {
        if (navigationWindow != null) navigationWindow.Deactivated -= NavigationDeactivated;
        navigationWindow = window;
        if (navigationWindow != null) navigationWindow.Deactivated += NavigationDeactivated;
    }
    private void NavigationDeactivated(object? sender, EventArgs e) => CancelNavigation();
}
