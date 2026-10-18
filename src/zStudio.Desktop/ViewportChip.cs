using System.Windows;
using System.Windows.Controls;

namespace zStudio.Desktop;

/// <summary>A small rounded panel over the 3D view (view menu, selection, surface under the cursor, Flycam): see PreviewStyles.</summary>
public sealed class ViewportChip : ContentControl
{
}

/// <summary>Marks a plain command button as switched on (the command bar's Parachute path), drawn like a checked toggle.</summary>
public static class CommandState
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached("IsOn", typeof(bool), typeof(CommandState), new FrameworkPropertyMetadata(false));
    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);
    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);
}

/// <summary>Not: keeps the outliner's legend button from reopening the legend its own click just closed.</summary>
public sealed class InverseBool : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;
}

/// <summary>
/// Stretches a source outliner row to the tree's visible width (the tree item's own template sizes its header to the content),
/// so the row's eye and Frame buttons line up at the pane's right edge at every depth, clear of the overlaid scroll bar.
/// </summary>
public static class OutlinerRowStretch
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(OutlinerRowStretch), new PropertyMetadata(false, EnabledChanged));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    private static void EnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement row || e.NewValue is not true) return;
        EventHandler update = (_, _) => Update(row);
        // A row realized by the last layout pass (an expand, a scroll) is sized at once, not at the next pass.
        row.Loaded += (_, _) => { row.LayoutUpdated -= update; row.LayoutUpdated += update; Update(row); };
        row.Unloaded += (_, _) => row.LayoutUpdated -= update;
    }
    private static void Update(FrameworkElement row)
    {
        if (!row.IsVisible) return;
        ScrollViewer? viewer = null;
        for (DependencyObject? parent = System.Windows.Media.VisualTreeHelper.GetParent(row); parent != null && viewer == null; parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
            viewer = parent as ScrollViewer;
        if (viewer == null || viewer.ViewportWidth <= 0) return;
        double left = row.TranslatePoint(new Point(0, 0), viewer).X;
        double width = Math.Floor(viewer.ViewportWidth - left - 16);
        // Too deep for the pane: keep the row's own width rather than squeezing its name away.
        if (width < 80) { if (!double.IsNaN(row.Width)) row.ClearValue(FrameworkElement.WidthProperty); return; }
        if (Math.Abs(row.Width - width) > 0.5 || double.IsNaN(row.Width)) row.Width = width;
    }
}
