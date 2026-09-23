using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Recoil.Zbd.Desktop;

public partial class AnimationEditor
{
    private double sidebarWidth = 360;
    private bool compactSidebar, layingOutSidebar, resizingSidebar, wideSidebarOpen = true;
    private StudioSettings? layoutSettings;
    private readonly Dictionary<FrameworkElement, double> defaultSectionHeights = [];

    private IEnumerable<Expander> SidebarSections => [PreviewOptionsSection, SequencesSection, EventsSection, StatusSection, TraceSection];
    private void InitializeLayoutPreferences()
    {
        if (layoutSettings != null || Window.GetWindow(this) is not MainWindow owner) return;
        layoutSettings = owner.ViewModel.Settings;
        sidebarWidth = Math.Clamp(layoutSettings.AnimationSidebarWidth, 320, 460);
        layoutSettings.AnimationSections ??= [];
        layoutSettings.AnimationSectionHeights ??= [];
        foreach (var section in SidebarSections)
        {
            var body = (FrameworkElement)section.Content;
            defaultSectionHeights[body] = body.Height;
            if (layoutSettings.AnimationSectionHeights.TryGetValue(section.Name, out double height) && double.IsFinite(height))
                body.Height = Math.Clamp(height, body.MinHeight, body.MaxHeight);
            if (layoutSettings.AnimationSections.TryGetValue(section.Name, out bool expanded)) section.IsExpanded = expanded;
        }
        foreach (var section in SidebarSections)
        {
            section.Expanded += (_, _) => SaveLayoutPreferences();
            section.Collapsed += (_, _) => SaveLayoutPreferences();
        }
        ArrangeSidebar();
    }
    private void SaveLayoutPreferences()
    {
        if (layoutSettings == null) return;
        layoutSettings.AnimationSidebarWidth = sidebarWidth;
        foreach (var section in SidebarSections)
        {
            layoutSettings.AnimationSections[section.Name] = section.IsExpanded;
            layoutSettings.AnimationSectionHeights[section.Name] = ((FrameworkElement)section.Content).Height;
        }
    }
    internal void ResetLayout()
    {
        sidebarWidth = 360; wideSidebarOpen = true;
        foreach (var (body, height) in defaultSectionHeights) body.Height = height;
        foreach (var section in SidebarSections) section.IsExpanded = section == SequencesSection || section == EventsSection;
        SidebarToggle.IsChecked = !compactSidebar;
        ArrangeSidebar(); SaveLayoutPreferences();
    }

    private void SectionResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is Thumb { Parent: FrameworkElement body } && double.IsFinite(e.VerticalChange))
            body.Height = Math.Clamp(body.Height + e.VerticalChange, body.MinHeight, body.MaxHeight);
        e.Handled = true;
    }
    private void SectionResizeCompleted(object sender, DragCompletedEventArgs e) => SaveLayoutPreferences();
    private void SectionResizeKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Thumb { Parent: FrameworkElement body } || e.Key is not (Key.Up or Key.Down) ||
            (Keyboard.Modifiers & ~ModifierKeys.Shift) != 0) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 32 : 8;
        body.Height = Math.Clamp(body.Height + (e.Key == Key.Down ? step : -step), body.MinHeight, body.MaxHeight);
        SaveLayoutPreferences(); e.Handled = true;
    }

    private void EditorSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender != PlayerTransport || e.HeightChanged) ArrangeSidebar();
    }
    private void ToolbarFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is FrameworkElement element) element.BringIntoView();
    }
    private void SidebarToggled(object sender, RoutedEventArgs e)
    {
        if (!layingOutSidebar && !compactSidebar) wideSidebarOpen = SidebarToggle.IsChecked == true;
        ArrangeSidebar();
    }
    private void SidebarResizeStarted(object sender, DragStartedEventArgs e) => resizingSidebar = true;
    private void SidebarResized(object sender, DragCompletedEventArgs e)
    {
        resizingSidebar = false;
        sidebarWidth = Math.Clamp(SidebarColumn.ActualWidth, 320, 460);
        ArrangeSidebar(); SaveLayoutPreferences();
    }
    private void ArrangeSidebar()
    {
        if (layingOutSidebar || resizingSidebar || SidebarHost == null || EditorBody.ActualWidth <= 0) return;
        layingOutSidebar = true;
        try
        {
            double width = EditorBody.ActualWidth;
            bool shortPlayer = EditorBody.ActualHeight < 400;
            TraceGraphicRow.Height = new(shortPlayer ? 80 : 100);
            TraceHint.Visibility = shortPlayer ? Visibility.Collapsed : Visibility.Visible;
            bool compact = width < (compactSidebar ? 740 : 700);
            if (compact != compactSidebar)
            {
                compactSidebar = compact;
                // Preserve useful preview space when neighbouring panes leave no
                // room for two columns. Details uses the preview region temporarily,
                // with the same player and transport still attached underneath.
                SidebarToggle.IsChecked = !compact && wideSidebarOpen;
            }
            bool open = SidebarToggle.IsChecked == true;
            SidebarHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            SidebarSplitter.Visibility = open && !compact ? Visibility.Visible : Visibility.Collapsed;
            SidebarSplitterColumn.Width = new(open && !compact ? 6 : 0);
            SidebarColumn.MinWidth = 0;
            SidebarColumn.MaxWidth = double.PositiveInfinity;
            SidebarColumn.Width = new(open && !compact ? Math.Min(sidebarWidth, width - 366) : 0);
            if (open && !compact)
            {
                SidebarColumn.MinWidth = 320;
                SidebarColumn.MaxWidth = Math.Clamp(width - 366, 320, 460);
            }
            Grid.SetColumn(SidebarHost, compact ? 0 : 2);
            Grid.SetColumnSpan(SidebarHost, compact ? 3 : 1);
            SidebarHost.Width = compact ? Math.Min(360, width) : double.NaN;
            SidebarHost.Height = compact ? ViewportArea.ActualHeight : double.NaN;
            SidebarHost.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            SidebarHost.VerticalAlignment = compact ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            ViewportArea.Visibility = compact && open ? Visibility.Hidden : Visibility.Visible;
            System.Windows.Controls.Panel.SetZIndex(SidebarHost, compact ? 2 : 0);
            SidebarToggle.Content = compact && open ? "Back to preview" : "Details";
            SidebarToggle.ToolTip = compact ? "Switch between animation details and preview; widen the workspace for side-by-side editing" : "Show or hide animation details";
        }
        finally { layingOutSidebar = false; }
    }
    private void SidebarWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        // Let bounded lists/read-only text scroll themselves. At either end,
        // pass the gesture to the enclosing section scroller instead of trapping it.
        ScrollViewer? inner = null;
        for (var node = e.OriginalSource as DependencyObject; node != null && node != SidebarScroll; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ComboBox or Slider) return;
            inner ??= node as ScrollViewer;
        }
        if (inner == null) return;
        bool edge = e.Delta > 0 ? inner.VerticalOffset <= .5 : inner.VerticalOffset >= inner.ScrollableHeight - .5;
        if (edge) { SidebarScroll.ScrollToVerticalOffset(SidebarScroll.VerticalOffset - e.Delta / 3.0); e.Handled = true; }
    }
}
