using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Recoil.Zbd.Desktop;

internal static class PreviewToolbarCheck
{
    public static async Task Run(MainWindow window, AnimationEditor editor, Func<string, Task> capture)
    {
        var bar = (ToolBar)editor.FindName("ViewOptionsRow");
        var lod = (ComboBox)editor.FindName("Lod");
        var height = (TextBox)editor.FindName("PreviewHeight");
        var ground = (StackPanel)editor.FindName("GroundTools");
        var grid = (ToggleButton)editor.FindName("ShowGrid");
        var collision = (ToggleButton)editor.FindName("GroundCollision");
        Require(bar.Items[0] == lod && bar.Items[1] == editor.FindName("Difficulty") && bar.Items[2] == editor.FindName("FramePose"), "Toolbar prefix is not LOD / Difficulty / Frame");
        Require(ground.Children[0] == grid && ground.Children[1] == height && ground.Children[2] == collision, "Grid, Height and collision are not one ordered overflow group");
        ((TabControl)window.FindName("InspectorTabs")).SelectedItem = window.FindName("PreviewSetupTab");
        await Idle();
        string[] controls = ["Lod", "Difficulty", "ShowLevel", "ShowGrid", "PreviewHeight", "GroundCollision", "ShowHorizon", "Lighting", "FollowCamera", "Mute", "Volume"];
        Require(!Descendants(editor.PreviewSetupView).OfType<FrameworkElement>().Any(e => controls.Contains(e.Name)), "Settings contains a duplicate toolbar/playback control");
        Require(!Descendants(bar).OfType<Button>().Any(b => Equals(b.Content, "Settings")), "Toolbar Settings button remains");
        Require(Descendants(lod).OfType<TextBlock>().Any(t => t.Text == "LOD 0"), "Collapsed LOD did not use its short label");
        lod.IsDropDownOpen = true; await Idle();
        var item = (ComboBoxItem)lod.ItemContainerGenerator.ContainerFromIndex(0);
        Require(Descendants(item).OfType<TextBlock>().Any(t => t.Text.Contains("Highest detail")), "LOD dropdown lost its description");
        lod.IsDropDownOpen = false;

        var frame = editor.CurrentFrame;
        var peer = new ToggleButtonAutomationPeer(grid);
        var toggle = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle);
        Require(peer.GetName() == "Grid" && toggle.ToggleState == ToggleState.On, "Grid has no native accessible toggle state");
        var on = Descendants(grid).OfType<Border>().First().Background;
        toggle.Toggle(); await Idle();
        Require(toggle.ToggleState == ToggleState.Off && grid.ToolTip.ToString()!.Contains("Off") && collision.IsChecked == true, "Grid toggle or tooltip changed collision");
        Require(on.ToString() != Descendants(grid).OfType<Border>().First().Background.ToString(), "On/off backgrounds do not differ");
        Require(!((PreviewIcon)grid.Content).IsChecked && ReferenceEquals(frame, editor.CurrentFrame), "Grid state indicator or presentation-only behavior is wrong");
        toggle.Toggle(); await Idle();
        Require(((PreviewIcon)grid.Content).IsChecked, "Checked marker did not track the control");
        await capture("toolbar-dark");

        Require(height.Focus(), "Could not focus Height for overflow check");
        height.Text = "-"; height.CaretIndex = 1;
        window.Width = 1080; window.Height = 650; await Idle();
        Require(ToolBar.GetIsOverflowItem(ground), "Narrow check did not exercise grouped overflow");
        Require(height.Text == "-" && height.CaretIndex == 1 && ReferenceEquals(frame, editor.CurrentFrame), "Overflow discarded a partial height, caret or frame");
        bar.IsOverflowOpen = true; await Idle();
        Require(grid.IsVisible && height.IsVisible && collision.IsVisible, "Overflow hid part of the ground group");
        await capture("toolbar-overflow");
        bar.IsOverflowOpen = false;
        window.Width = 1600; window.Height = 900; await Idle();
        Require(height.Text == "-" && height.CaretIndex == 1, "Returning from overflow normalized input");
        height.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(height), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
        Require(height.Text == "0", "Escape did not restore the last valid height");

#pragma warning disable WPF0001
        Application.Current.ThemeMode = ThemeMode.Light; await Idle(); await capture("toolbar-light");
        Application.Current.ThemeMode = ThemeMode.Dark; await Idle();
#pragma warning restore WPF0001
        var view = ((Menu)window.FindName("AppMenu")).Items.OfType<MenuItem>().Single(m => Equals(m.Header,"_View"));
        var density = view.Items.OfType<MenuItem>().Single(m => Equals(m.Header,"Density"));
        density.Items.OfType<MenuItem>().Single(m => Equals(m.Header,"Comfortable")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Idle(); Require(grid.ActualWidth == 40 && ((PreviewIcon)grid.Content).ActualWidth == 20, "Comfortable icon hit targets did not scale");
        await capture("toolbar-comfortable");
        density.Items.OfType<MenuItem>().Single(m => Equals(m.Header,"Compact")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Idle(); Require(grid.ActualWidth == 32 && ((PreviewIcon)grid.Content).ActualWidth == 16, "Compact icon hit targets did not scale");
        Console.WriteLine("PASS: one home per setting, compact/full LOD labels, native accessible toggle states/tooltips/checked marker, theme backgrounds, independent grid/collision, grouped overflow with focused partial Height/caret, Escape restoration, both densities and unchanged preview.");
    }
    private static async Task Idle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(150); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
