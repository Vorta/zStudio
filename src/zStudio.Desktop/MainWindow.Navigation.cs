using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private readonly MenuItem cameraNavigationMenu = new() { Header = "3D Navigation" };
    private SceneViewport? ActiveNavigationViewport => EmptyPreview.Visibility == Visibility.Visible ? null
        : animation?.Viewport ?? (SceneHost.Visibility == Visibility.Visible ? scene : null);
    private void InitializeCameraNavigation()
    {
        var viewMenu = (MenuItem)PropertiesMenu.Parent;
        viewMenu.Items.Insert(1, cameraNavigationMenu);
        foreach (var (title, action, shortcut) in new (string, string, string)[]
        {
            ("Front", "front", "Numpad 1"), ("Back", "back", "Ctrl+Numpad 1"),
            ("Right", "right", "Numpad 3"), ("Left", "left", "Ctrl+Numpad 3"),
            ("Top", "top", "Numpad 7"), ("Bottom", "bottom", "Ctrl+Numpad 7"),
            ("Opposite", "opposite", "Numpad 9"), ("Perspective", "perspective", "Numpad 5"),
            ("Orthographic", "orthographic", "Numpad 5"), ("Frame all", "frameAll", "Home"),
            ("Frame selected", "frameSelected", "Numpad ."), ("Follow animation camera", "follow", "Numpad 0")
        })
        {
            var item = new MenuItem { Header = title, Tag = action, InputGestureText = shortcut,
                IsCheckable = action is "perspective" or "orthographic" or "follow" };
            item.Click += CameraNavigationClick; cameraNavigationMenu.Items.Add(item);
        }
        viewMenu.SubmenuOpened += (_, e) =>
        {
            if (e.OriginalSource != viewMenu && e.OriginalSource != cameraNavigationMenu) return;
            cameraNavigationMenu.Visibility = ViewModel.HasRoot ? Visibility.Visible : Visibility.Collapsed;
            var active = ActiveNavigationViewport;
            cameraNavigationMenu.IsEnabled = active != null && !active.IsPickupDragging && !active.IsFlyActive;
            foreach (MenuItem item in cameraNavigationMenu.Items)
            {
                string action = (string)item.Tag;
                item.IsChecked = action == active?.CaptureView().Projection || action == "follow" && animation?.Options.FollowCamera == true;
                item.IsEnabled = action != "follow" || animation != null;
            }
        };
    }
    private void CameraNavigationClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string action }) RunCameraNavigation(action);
        e.Handled = true;
    }
    internal bool RunCameraNavigation(string action, Point? zoomPoint = null)
    {
        var active = ActiveNavigationViewport;
        if (active == null || active.IsPickupDragging || active.IsFlyActive) return false;
        switch (action)
        {
            case "front": case "back": case "left": case "right": case "top": case "bottom": active.SetAxisView(action); break;
            case "perspective": case "orthographic": active.SetProjection(action); break;
            case "projection": active.SetProjection(active.CaptureView().Projection == "perspective" ? "orthographic" : "perspective"); break;
            case "opposite": active.OppositeView(); break;
            case "frameAll": case "frameSelected": case "frameAsset":
                if (!active.TryFrame(action == "frameAll" ? "all" : action == "frameAsset" ? "asset" : "selected"))
                    ViewModel.Status = "No visible geometry is available to frame.";
                break;
            case "follow": if (animation == null) return false; animation.ToggleCameraFollow(); break;
            case "orbitLeft": active.OrbitStep(-15, 0); break;
            case "orbitRight": active.OrbitStep(15, 0); break;
            case "orbitUp": active.OrbitStep(0, 15); break;
            case "orbitDown": active.OrbitStep(0, -15); break;
            case "panLeft": case "panRight": case "panUp": case "panDown":
                active.StopCameraMotion();
                var surface = (FrameworkElement)active.Content;
                active.PanBy(action == "panLeft" ? surface.ActualWidth * .1 : action == "panRight" ? -surface.ActualWidth * .1 : 0,
                    action == "panUp" ? surface.ActualHeight * .1 : action == "panDown" ? -surface.ActualHeight * .1 : 0);
                break;
            case "zoomIn": case "zoomOut": active.StopCameraMotion(); active.ZoomBy(action == "zoomIn" ? 1 : -1, zoomPoint); break;
            default: return false;
        }
        return true;
    }
    internal static string? CameraKeyAction(Key key, ModifierKeys modifiers) => (key, modifiers) switch
    {
        (Key.NumPad1, ModifierKeys.None) => "front", (Key.NumPad1, ModifierKeys.Control) => "back",
        (Key.NumPad3, ModifierKeys.None) => "right", (Key.NumPad3, ModifierKeys.Control) => "left",
        (Key.NumPad7, ModifierKeys.None) => "top", (Key.NumPad7, ModifierKeys.Control) => "bottom",
        (Key.NumPad2, ModifierKeys.None) => "orbitDown", (Key.NumPad4, ModifierKeys.None) => "orbitLeft",
        (Key.NumPad6, ModifierKeys.None) => "orbitRight", (Key.NumPad8, ModifierKeys.None) => "orbitUp",
        (Key.NumPad2, ModifierKeys.Control) => "panDown", (Key.NumPad4, ModifierKeys.Control) => "panLeft",
        (Key.NumPad6, ModifierKeys.Control) => "panRight", (Key.NumPad8, ModifierKeys.Control) => "panUp",
        (Key.NumPad9, ModifierKeys.None) => "opposite", (Key.NumPad5, ModifierKeys.None) => "projection",
        (Key.NumPad0, ModifierKeys.None) => "follow", (Key.Home, ModifierKeys.None) => "frameAll",
        (Key.Decimal, ModifierKeys.None) => "frameSelected", (Key.Add, ModifierKeys.None) => "zoomIn",
        (Key.Subtract, ModifierKeys.None) => "zoomOut", _ => null
    };
    internal static bool CameraInputIsNative(DependencyObject? source)
    {
        for (var current = source; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is TextBoxBase or PasswordBox or ComboBox or ButtonBase or MenuItem or Slider or Thumb or Selector or TreeView or AnimationTimeline)
                return true;
        return false;
    }
    private bool CameraKeyboard(KeyEventArgs e)
    {
        var active = ActiveNavigationViewport;
        if (active == null || CameraInputIsNative(e.OriginalSource as DependencyObject) || CameraInputIsNative(System.Windows.Input.Keyboard.FocusedElement as DependencyObject)
            || !active.IsKeyboardFocusWithin && !active.IsMouseOver) return false;
        string? action = CameraKeyAction(e.Key, System.Windows.Input.Keyboard.Modifiers);
        if (action == null) return false;
        if (e.IsRepeat && !(action.StartsWith("orbit", StringComparison.Ordinal) || action.StartsWith("pan", StringComparison.Ordinal) || action.StartsWith("zoom", StringComparison.Ordinal)))
        { e.Handled = true; return true; }
        Point? zoomPoint = action is "zoomIn" or "zoomOut" && active.IsMouseOver ? Mouse.GetPosition((FrameworkElement)active.Content) : null;
        return e.Handled = RunCameraNavigation(action, zoomPoint);
    }
}
