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

/// <summary>
/// A World tree search result's name line: the row's name, its secondary text and a definition's or binding's target, with
/// every word of the search underlined in the accent colour (1.5 DIP) where it occurs in the name or the target.
/// </summary>
public static class MatchHighlight
{
    public static readonly DependencyProperty HitProperty = DependencyProperty.RegisterAttached("Hit", typeof(object), typeof(MatchHighlight), new PropertyMetadata(null, HitChanged));
    public static object? GetHit(DependencyObject element) => element.GetValue(HitProperty);
    public static void SetHit(DependencyObject element, object? value) => element.SetValue(HitProperty, value);
    private static void HitChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock text) return;
        text.Inlines.Clear();
        if (e.NewValue is not OutlinerHit { Row: { } row } hit) return;
        var words = hit.Words ?? [];
        Add(row.OutlinerLabel, null, 13, words);
        Add(row.SecondaryRunText, "TextFillColorSecondaryBrush", 12, []);
        Add(row.TargetRunText, "TextFillColorTertiaryBrush", 12, words);
        Add(row.TargetQuietRunText, "TextFillColorDisabledBrush", 12, words);

        void Add(string value, string? brush, double size, IReadOnlyList<string> marked)
        {
            if (value.Length == 0) return;
            // The matched spans, merged where words overlap.
            bool[] hits = new bool[value.Length];
            foreach (string word in marked)
                for (int at = value.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0 && word.Length > 0; at = value.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
                    for (int i = at; i < at + word.Length; i++) hits[i] = true;
            for (int start = 0; start < value.Length;)
            {
                int end = start; while (end < value.Length && hits[end] == hits[start]) end++;
                System.Windows.Documents.Run run = new(value[start..end]) { FontSize = size };
                if (brush != null) run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, brush);
                if (hits[start] && Application.Current?.TryFindResource("AccentFillColorDefaultBrush") is System.Windows.Media.Brush accent)
                    run.TextDecorations = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Underline, new System.Windows.Media.Pen(accent, 1.5), 1, TextDecorationUnit.Pixel, TextDecorationUnit.Pixel) };
                text.Inlines.Add(run);
                start = end;
            }
        }
    }
}

/// <summary>Not: keeps the outliner's legend button from reopening the legend its own click just closed.</summary>
public sealed class InverseBool : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;
}

/// <summary>
/// Stretches a source outliner row to the tree's visible width (the tree item's own template sizes its header to the content),
/// so the row's eye and Frame buttons line up at the pane's right edge. A row never gets narrower than its icon, counts and
/// buttons plus room for its name (a world object's 96 DIP, a section row's 24: the Animations rows keep their buttons inside
/// a 200 DIP pane, design/world-tree-animations.md §10); deep rows keep that room and scroll horizontally.
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
        if (viewer?.Template.FindName("PART_ScrollContentPresenter", viewer) is not ScrollContentPresenter content || content.ActualWidth <= 0) return;
        // Measure indentation in content coordinates so scrolling does not change the row's width or extent.
        double left = row.TranslatePoint(new Point(0, 0), content).X + viewer.HorizontalOffset;
        double width = Math.Max(Minimum(row), Math.Floor(content.ActualWidth - left - 4));
        if (Math.Abs(row.Width - width) > 0.5 || double.IsNaN(row.Width)) row.Width = width;
    }
    /// <summary>The row's fixed parts (every child of its panel but the name, which fills) and its name's room.</summary>
    private static double Minimum(FrameworkElement row)
    {
        double fixedParts = 0;
        if (row is Panel panel)
            for (int i = 0; i < panel.Children.Count - 1; i++)
                if (panel.Children[i].Visibility != Visibility.Collapsed) fixedParts += panel.Children[i].DesiredSize.Width;
        double name = row.DataContext is SceneTreeItem { Section: null, Pass: null } ? 96 : 24;
        return Math.Ceiling(fixedParts + name);
    }
}
