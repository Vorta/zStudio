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
    public void SelectFramingNode(int? node) { if (node is int index && InspectionNodes != null && !InspectionNodes.Contains(index)) return; FramingSelection = node; if (node != null && SelectedAiNode != null) SelectAiNode(null); }
    internal enum NavigationGesture { None, Orbit, Pan, Zoom, Dolly }
    private NavigationGesture navigationGesture;
    private Vector navigationVelocity;
    private Point? zoomScreenPoint;
    private Point? pendingOrbitPoint;
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
        RestoreView(pose with { Position = target - look, LookDirection = look, UpDirection = up, AxisView = view, AutoPerspective = true,
            OrbitPivot = target });
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
        RestoreView(UprightPose(pose with { Position = pose.Position + pose.LookDirection * 2, LookDirection = -pose.LookDirection,
            OrbitPivot = pose.Position + pose.LookDirection }));
    }
    public void PanBy(double horizontal, double vertical)
    {
        if (!BeginManualNavigation() || !viewport.IsPanEnabled || viewport.Camera is not ProjectionCamera camera) return;
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        TranslateNavigation(camera, (-right * horizontal + up * vertical) * UnitsPerPixel);
    }
    private Point ClampNavigationPoint(Point point) => new(
        double.IsFinite(point.X) ? Math.Clamp(point.X, 0, Math.Max(0, Math.BitDecrement(viewport.ActualWidth))) : viewport.ActualWidth / 2,
        double.IsFinite(point.Y) ? Math.Clamp(point.Y, 0, Math.Max(0, Math.BitDecrement(viewport.ActualHeight))) : viewport.ActualHeight / 2);

    /// <summary>Zoom towards the pointer at pointed-surface speed, or scale orthographic views about it.</summary>
    public void ZoomBy(double steps, Point? screenPoint = null)
    {
        if (!BeginManualNavigation() || !viewport.IsZoomEnabled || viewport.Camera is not ProjectionCamera camera) return;
        steps = Math.Clamp(steps, -100, 100);
        if (steps == 0 || !double.IsFinite(steps)) return;
        zoomScreenPoint = ClampNavigationPoint(screenPoint ?? new Point(viewport.ActualWidth / 2, viewport.ActualHeight / 2));
        bool hasRay = TryNavigationRay(zoomScreenPoint.Value, out var rayOrigin, out var rayDirection);
        double factor = Math.Exp(-steps * .12);
        if (camera is OrthographicCamera orthographic)
        {
            double previousWidth = orthographic.Width;
            double width = Math.Clamp(previousWidth * factor, .001, 1e12);
            if (hasRay) TranslateNavigation(camera, (rayOrigin - camera.Position) * (1 - width / previousWidth));
            orthographic.Width = width;
        }
        else
        {
            double distance = camera.LookDirection.Length;
            if (!double.IsFinite(distance) || distance < 1e-12) return;
            var forward = camera.LookDirection / distance;
            var direction = hasRay ? rayDirection : forward;
            navigationReferenceDistance ??= distance;
            orbitPivot ??= camera.Position + camera.LookDirection;
            // A short explicit look vector must not make the eye jump backwards.
            if (distance < .01) orbitPivot += forward * (.01 - distance);
            distance = Math.Max(distance, .01);
            // Refresh at most one wheel step at a time, including immediately
            // after crossing the old view target. Target length never sets speed.
            while (steps != 0)
            {
                double step = Math.Clamp(steps, -1, 1);
                if (TryNavigationSurface(zoomScreenPoint.Value, out var hit)) navigationReferenceDistance = (hit - camera.Position).Length;
                double travel = .12 * Math.Max(navigationReferenceDistance.Value, .01) * step;
                double forwardTravel = Vector3D.DotProduct(direction, forward) * travel;
                double next = Math.Clamp(distance - forwardTravel, .01, 1e12);
                orbitPivot += forward * (forwardTravel + next - distance);
                camera.Position += direction * travel;
                camera.LookDirection = forward * next;
                distance = next; steps -= step;
            }
        }
    }
    /// <summary>Move eye and orbit target together along the view direction, in game units.</summary>
    public void DollyBy(double distance)
    {
        if (!BeginManualNavigation() || viewport.Camera is not ProjectionCamera camera) return;
        var direction = camera.LookDirection; direction.Normalize(); TranslateNavigation(camera, direction * distance);
    }
    private void TranslateNavigation(ProjectionCamera camera, Vector3D delta)
    {
        orbitPivot = (orbitPivot ?? camera.Position + camera.LookDirection) + delta;
        camera.Position += delta;
    }
    internal void ApplyNavigationDelta(NavigationGesture gesture, Vector delta)
    {
        switch (gesture)
        {
            case NavigationGesture.Orbit: RotateBy(delta.X, delta.Y); break;
            case NavigationGesture.Pan: PanBy(delta.X, delta.Y); break;
            case NavigationGesture.Zoom: ZoomBy(delta.Y / 40, zoomScreenPoint); break;
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
        pendingOrbitPoint = gesture == NavigationGesture.Orbit ? position : null;
        if (gesture == NavigationGesture.Zoom) zoomScreenPoint = position;
        rotationPoint = position; rotationInputTick = Stopwatch.GetTimestamp();
    }
    internal void UpdateNavigationModifiers(ModifierKeys modifiers, Point position)
    {
        if (rotationPoint == null) return;
        var gesture = Gesture(MouseButton.Middle, modifiers);
        if (gesture == navigationGesture) return;
        navigationGesture = gesture;
        rotationVelocity = default; navigationVelocity = default;
        rotationPoint = position; rotationInputTick = Stopwatch.GetTimestamp();
        pendingOrbitPoint = gesture == NavigationGesture.Orbit ? position : null;
        zoomScreenPoint = gesture == NavigationGesture.Zoom ? ClampNavigationPoint(position) : null;
    }
    internal bool NavigationModifierKey(Key key, ModifierKeys modifiers, Point position)
    {
        if (rotationPoint == null || key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)) return false;
        UpdateNavigationModifiers(modifiers, position);
        return true;
    }
    internal void MoveNavigationDrag(Point current, ModifierKeys? modifiers = null)
    {
        if (modifiers is { } held) UpdateNavigationModifiers(held, current);
        if (rotationPoint is not { } previous) return;
        var delta = current - previous;
        if (delta.LengthSquared == 0) return;
        double seconds = Math.Max(.008, Stopwatch.GetElapsedTime(rotationInputTick).TotalSeconds);
        rotationInputTick = Stopwatch.GetTimestamp();
        if (navigationGesture == NavigationGesture.Orbit && pendingOrbitPoint is { } pivotPoint)
        { pendingOrbitPoint = null; PickOrbitPivot(pivotPoint); }
        if (navigationGesture == NavigationGesture.Zoom) zoomScreenPoint = ClampNavigationPoint(current);
        ApplyNavigationDelta(navigationGesture, delta);
        rotationPoint = current;
        if (navigationGesture == NavigationGesture.Orbit) rotationVelocity = delta / seconds;
        else if (navigationGesture != NavigationGesture.None) navigationVelocity = delta / seconds;
    }
    internal void EndNavigationDrag()
    {
        if (Stopwatch.GetElapsedTime(rotationInputTick).TotalSeconds > .15 || !viewport.IsInertiaEnabled)
        { rotationVelocity = default; navigationVelocity = default; }
        rotationPoint = null; pendingOrbitPoint = null; viewport.InvalidateRender();
    }
    public void CancelNavigation()
    {
        rotationVelocity = default; navigationVelocity = default; rotationPoint = null;
        zoomScreenPoint = null; pendingOrbitPoint = null;
        if (viewport.IsMouseCaptured && !IsPickupDragging && !IsFlyActive) viewport.ReleaseMouseCapture();
    }
    private void ConfigureNavigation()
    {
        ConfigureNavigationCube();
        // Parent tunneling precedes Helix's class-level hit testing and view-cube handler.
        PreviewMouseDown += (_, e) =>
        {
            if (e.Handled || IsFlyActive || IsPickupDragging ||
                InspectionContent is DependencyObject panel && IsInspectionInput(e.OriginalSource as DependencyObject, panel)) return;
            var gesture = Gesture(e.ChangedButton, Keyboard.Modifiers);
            if (gesture == NavigationGesture.None) return;
            BeginNavigationDrag(gesture, e.GetPosition(viewport));
            viewport.CaptureMouse(); viewport.Focus(); e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (rotationPoint == null) return;
            MoveNavigationDrag(e.GetPosition(viewport), Keyboard.Modifiers); e.Handled = true;
        };
        PreviewKeyDown += NavigationModifiersChanged;
        PreviewKeyUp += NavigationModifiersChanged;
        PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle || rotationPoint == null) return;
            UpdateNavigationModifiers(Keyboard.Modifiers, e.GetPosition(viewport));
            EndNavigationDrag(); viewport.ReleaseMouseCapture(); e.Handled = true;
        };
        PreviewMouseWheel += (_, e) =>
        {
            // The card is a sibling of the render surface. Let its native controls
            // process the wheel before any camera motion or inertia is started.
            if (e.Handled || IsFlyActive ||
                InspectionContent is DependencyObject panel && IsInspectionInput(e.OriginalSource as DependencyObject, panel)) return;
            if (!IsPickupDragging)
            {
                StopCameraMotion(); ZoomAt(e.GetPosition(viewport), e.Delta);
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
    private void NavigationModifiersChanged(object sender, KeyEventArgs e)
    {
        if (rotationPoint == null) return;
        if (NavigationModifierKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, Mouse.GetPosition(viewport)))
            e.Handled = true;
    }
    private void AttachNavigationWindow(Window? window)
    {
        if (navigationWindow != null) navigationWindow.Deactivated -= NavigationDeactivated;
        navigationWindow = window;
        if (navigationWindow != null) navigationWindow.Deactivated += NavigationDeactivated;
    }
    private void NavigationDeactivated(object? sender, EventArgs e) => CancelNavigation();
}
