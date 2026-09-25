using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

/// <summary>Owns input only while the user explicitly activates the visible static 3D viewer.</summary>
internal sealed class FlyCameraSession : IDisposable
{
    private readonly Window window;
    private readonly SceneViewport scene;
    private readonly HashSet<Key> held = [];
    private FlyMouseCapture? mouse;
    private Cursor? previousCursor;
    private bool previousForceCursor, previousFocusable, ending, disposed, preparingInput;
    private FlyMouseCapture.PointI pointerBefore;
    private bool savedPointer;
    internal bool IsActive { get; private set; }
    internal event Action? Changed;

    internal FlyCameraSession(Window window, SceneViewport scene)
    {
        this.window = window; this.scene = scene;
        scene.FlyStateChanged += SceneChanged;
        scene.Unloaded += Unavailable;
        scene.IsVisibleChanged += AvailabilityChanged;
        scene.IsEnabledChanged += AvailabilityChanged;
        scene.LostMouseCapture += CaptureLost;
        scene.LostKeyboardFocus += FocusLost;
        scene.SizeChanged += SizeChanged;
        window.Deactivated += Unavailable;
        window.Closing += Closing;
        window.PreviewMouseDown += BlockPointer;
        window.PreviewMouseUp += BlockPointer;
        window.PreviewMouseMove += BlockPointer;
        window.PreviewMouseWheel += Wheel;
    }

    internal bool TryStart(out string? error)
    {
        error = null;
        if (IsActive) return true;
        if (disposed || !window.IsActive || scene.PreviewScene == null || !scene.IsVisible || !scene.IsEnabled || scene.ActualWidth <= 0 || scene.ActualHeight <= 0)
        { error = "Open a model or Whole world preview before enabling Fly camera."; return false; }
        try
        {
            scene.CancelPickupDrag();
            previousCursor = scene.Cursor; previousForceCursor = scene.ForceCursor; previousFocusable = scene.Focusable;
            preparingInput = true;
            savedPointer = FlyMouseCapture.GetCursorPos(out pointerBefore);
            scene.Focusable = true;
            if (!scene.Focus() || !Mouse.Capture(scene, CaptureMode.Element)) throw new InvalidOperationException("The viewer could not capture input.");
            mouse = new(window, scene);
            mouse.Look += Look; mouse.Invalidated += End;
            scene.Cursor = Cursors.None; scene.ForceCursor = true;
            IsActive = true;
            scene.SetFly(true);
            Changed?.Invoke(); return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            End(); error = "Fly camera could not start: " + ex.Message; return false;
        }
    }

    internal bool HandleKey(KeyEventArgs e, bool down)
    {
        if (!IsActive) return false;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Release before operating-system switching/closing; do not swallow those shortcuts.
        if (key is Key.LWin or Key.RWin || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        { End(); return false; }
        if (down && key == Key.Escape)
        {
            End();
            if (savedPointer && window.IsActive) FlyMouseCapture.SetCursorPos(pointerBefore.X, pointerBefore.Y);
        }
        else
        {
            if (down) held.Add(key); else held.Remove(key);
            scene.SetFlyMovement(Axis(Key.D, Key.A), Axis(Key.Space, Key.C), Axis(Key.W, Key.S));
        }
        e.Handled = true; return true;
    }

    private int Axis(Key positive, Key negative) => (held.Contains(positive) ? 1 : 0) - (held.Contains(negative) ? 1 : 0);
    private void Look(double x, double y) { if (IsActive) scene.LookFlyBy(x, y); }
    private void Wheel(object sender, MouseWheelEventArgs e) { if (IsActive) { scene.AdjustFlySpeed(e.Delta); e.Handled = true; } }
    private void BlockPointer(object sender, MouseEventArgs e)
    {
        if (!IsActive) return;
        if (e.RoutedEvent == Mouse.PreviewMouseMoveEvent) mouse?.QueueFallbackLook();
        e.Handled = true;
    }
    private void SceneChanged() { if (IsActive && !scene.IsFlyActive) End(); else Changed?.Invoke(); }
    private void Unavailable(object? sender, EventArgs e) => End();
    private void Closing(object? sender, CancelEventArgs e) => End();
    private void AvailabilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) End(); }
    private void CaptureLost(object sender, MouseEventArgs e) { if (Mouse.Captured != scene) End(); }
    private void FocusLost(object sender, KeyboardFocusChangedEventArgs e) { if (!scene.IsKeyboardFocusWithin) End(); }
    private void SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsActive) return;
        try { mouse?.UpdateBounds(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { End(); }
    }

    internal void End()
    {
        if (ending || !IsActive && !preparingInput && mouse == null) return;
        ending = true;
        try
        {
            bool hadInput = preparingInput || IsActive || mouse != null || Mouse.Captured == scene;
            IsActive = false; held.Clear();
            try { mouse?.Dispose(); }
            finally
            {
                mouse = null;
                if (Mouse.Captured == scene) Mouse.Capture(null);
                if (hadInput)
                {
                    scene.Cursor = previousCursor; scene.ForceCursor = previousForceCursor; scene.Focusable = previousFocusable;
                }
                preparingInput = false;
                try { scene.SetFly(false); }
                finally { Changed?.Invoke(); }
            }
        }
        finally { ending = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        End(); disposed = true;
        scene.FlyStateChanged -= SceneChanged; scene.Unloaded -= Unavailable;
        scene.IsVisibleChanged -= AvailabilityChanged; scene.IsEnabledChanged -= AvailabilityChanged;
        scene.LostMouseCapture -= CaptureLost; scene.LostKeyboardFocus -= FocusLost; scene.SizeChanged -= SizeChanged;
        window.Deactivated -= Unavailable; window.Closing -= Closing;
        window.PreviewMouseDown -= BlockPointer; window.PreviewMouseUp -= BlockPointer;
        window.PreviewMouseMove -= BlockPointer; window.PreviewMouseWheel -= Wheel;
    }
}
