using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core;

namespace zStudio.Desktop;

/// <summary>A zone the picker lists: how many polygons of the map carry it (as placed in the world) and its label.</summary>
internal sealed record ZonePickerEntry(byte Zone, string? Label, long Polygons);

/// <summary>
/// The polygon zone tool's zone picker: a text box that takes 0–254, Any or a label, the zones the map uses (with their
/// labels and polygon counts) and a zone it does not use yet. Zones are named in the World tree (a zone's card).
/// </summary>
internal sealed class ZonePicker : Border
{
    private readonly TextBox query = new() { Height = 30, FontSize = 13, MaxLength = 64 };
    private readonly TextBlock help = new() { FontSize = 11.5, Margin = new(2, 4, 0, 6) };
    private readonly StackPanel rows = new();
    private readonly Action<byte> choose;
    private readonly Action close;
    private IReadOnlyList<ZonePickerEntry> used = [];
    private IReadOnlyDictionary<byte, string> labels = new Dictionary<byte, string>();
    private byte current;
    private int highlighted = -1;
    private readonly List<(Border Row, byte Zone)> shown = [];

    internal ZonePicker(Action<byte> choose, Action close)
    {
        this.choose = choose; this.close = close;
        Width = 340; CornerRadius = new(8); BorderThickness = new(1); Padding = new(8);
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        SetResourceReference(BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = .35 };
        System.Windows.Automation.AutomationProperties.SetName(query, "Zone number, Any or label");
        query.TextChanged += (_, _) => { highlighted = -1; Rebuild(); };
        query.PreviewKeyDown += QueryKey;
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        StackPanel content = new();
        content.Children.Add(WithHint(query, "Type a zone number, Any or a label")); content.Children.Add(help);
        content.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        TextBlock footer = new() { Text = "Name zones in the World tree: select one under Zones.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(2, 8, 2, 0) };
        footer.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        content.Children.Add(footer);
        Child = content;
    }

    /// <summary>Shows the map's zones with <paramref name="zone"/> chosen and the text box empty and focused.</summary>
    internal void Open(IReadOnlyList<ZonePickerEntry> zones, IReadOnlyDictionary<byte, string> names, byte zone)
    {
        used = zones; labels = names; current = zone;
        query.Text = ""; highlighted = -1; Rebuild();
        Dispatcher.BeginInvoke(() => { query.Focus(); Keyboard.Focus(query); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>A text box with its hint shown while it is empty.</summary>
    private static Grid WithHint(TextBox box, string text)
    {
        TextBlock hint = new() { Text = text, IsHitTestVisible = false, Margin = new(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = box.FontSize, TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        void Update() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Update(); Update();
        Grid grid = new(); grid.Children.Add(box); grid.Children.Add(hint);
        return grid;
    }
    private static string ZoneName(byte zone) => zone == 255 ? "Any" : "Zone " + zone.ToString(CultureInfo.InvariantCulture);
    internal static Brush Swatch(byte zone)
    {
        var (r, g, b) = WorldSurfaceHighlights.ZoneColor(zone);
        var brush = new SolidColorBrush(Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255))); brush.Freeze(); return brush;
    }
    /// <summary>The zone the text names: a number 0–254, Any, or one label; null when it names none (or several).</summary>
    private byte? Typed(out bool valid)
    {
        string text = query.Text.Trim(); valid = true;
        if (text.Length == 0) return null;
        if (text.Equals("any", StringComparison.OrdinalIgnoreCase)) return 255;
        if (text.StartsWith("zone ", StringComparison.OrdinalIgnoreCase)) text = text[5..].Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number)) { valid = number <= 254; return valid ? (byte)number : null; }
        var matches = labels.Where(l => l.Key != 255 && l.Value.Contains(text, StringComparison.CurrentCultureIgnoreCase)).Select(l => l.Key).Take(2).ToArray();
        valid = matches.Length > 0;
        return matches.Length == 1 ? matches[0] : null;
    }
    private bool Matches(byte zone)
    {
        string text = query.Text.Trim();
        if (text.Length == 0) return true;
        if (text.StartsWith("zone ", StringComparison.OrdinalIgnoreCase) && int.TryParse(text[5..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)) text = text[5..].Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return zone != 255 && zone.ToString(CultureInfo.InvariantCulture).StartsWith(text, StringComparison.Ordinal);
        return ZoneName(zone).Contains(text, StringComparison.OrdinalIgnoreCase) || labels.TryGetValue(zone, out var label) && label.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || zone == 255 && "any".StartsWith(text, StringComparison.OrdinalIgnoreCase);
    }

    private void Rebuild()
    {
        rows.Children.Clear(); shown.Clear();
        byte? typed = Typed(out bool valid);
        help.Text = valid ? "Enter picks the typed zone" : "Enter 0–254 or Any";
        help.SetResourceReference(TextBlock.ForegroundProperty, valid ? "TextFillColorTertiaryBrush" : "SystemFillColorCriticalBrush");
        var listed = used.Where(z => Matches(z.Zone)).OrderBy(z => z.Zone == 255 ? 256 : z.Zone).ToList();
        Header("Used in this map", "Polygons");
        if (listed.Count == 0) rows.Children.Add(Muted(used.Count == 0 ? "No polygon of the map has a zone yet." : "No used zone matches."));
        foreach (var entry in listed) Row(entry.Zone, entry.Polygons);
        List<byte> unused = [];
        if (typed is { } candidate && used.All(z => z.Zone != candidate)) unused.Add(candidate);
        if (query.Text.Trim().Length == 0)
        {
            if (used.All(z => z.Zone != current)) unused.Add(current);
            if (used.All(z => z.Zone != 255) && !unused.Contains(255)) unused.Add(255);
        }
        if (unused.Count > 0)
        {
            rows.Children.Add(new Border { Height = 1, Margin = new(0, 6, 0, 2), Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)) });
            Header("Not used yet", null);
            foreach (byte zone in unused.Distinct()) Row(zone, null);
        }
        if (highlighted >= shown.Count) highlighted = shown.Count - 1;
        Highlight();
    }
    private void Header(string text, string? right)
    {
        Grid header = new() { Margin = new(2, 4, 4, 2) };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        TextBlock left = new() { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        left.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        header.Children.Add(left);
        if (right != null)
        {
            TextBlock count = new() { Text = right, FontSize = 11.5 }; count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            Grid.SetColumn(count, 1); header.Children.Add(count);
        }
        rows.Children.Add(header);
    }
    private static TextBlock Muted(string text)
    {
        TextBlock block = new() { Text = text, FontSize = 12, Margin = new(10, 4, 4, 4) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return block;
    }
    private void Row(byte zone, long? polygons)
    {
        Grid grid = new();
        foreach (var width in new[] { GridLength.Auto, new GridLength(66), new GridLength(1, GridUnitType.Star), GridLength.Auto }) grid.ColumnDefinitions.Add(new() { Width = width });
        Border pill = new() { Width = 3, Height = 16, CornerRadius = new(1.5), Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = zone == current ? Visibility.Visible : Visibility.Hidden };
        pill.SetResourceReference(BackgroundProperty, "AccentFillColorDefaultBrush");
        StackPanel lead = new() { Orientation = Orientation.Horizontal };
        lead.Children.Add(pill);
        lead.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new(3), Background = Swatch(zone), Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) });
        grid.Children.Add(lead);
        TextBlock name = new() { Text = ZoneName(zone), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        string? label = zone == 255 ? "No zone limit" : labels.TryGetValue(zone, out var named) ? named : null;
        TextBlock labelText = new() { Text = label ?? (polygons == null ? "Not used in this map" : "No label"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 8, 0) };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, label != null && zone != 255 ? "TextFillColorSecondaryBrush" : "TextFillColorTertiaryBrush");
        labelText.ToolTip = label;
        Grid.SetColumn(labelText, 2); grid.Children.Add(labelText);
        if (polygons is { } count)
        {
            TextBlock text = new() { Text = count == 1 ? "1 polygon" : $"{count:N0} polygons", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            Grid.SetColumn(text, 3); grid.Children.Add(text);
        }
        Border row = new() { Height = 32, CornerRadius = new(4), Padding = new(4, 0, 2, 0), Child = grid, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => { if (shown.FindIndex(s => s.Row == row) is int at and >= 0) { highlighted = at; Highlight(); } };
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Choose(zone); };
        rows.Children.Add(row); shown.Add((row, zone));
    }
    private void Highlight()
    {
        for (int i = 0; i < shown.Count; i++)
        {
            var (row, zone) = shown[i];
            if (i == highlighted || zone == current) row.SetResourceReference(BackgroundProperty, i == highlighted ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush");
            else row.Background = Brushes.Transparent;
        }
    }
    private void Choose(byte zone) => choose(zone);
    private void QueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            if (shown.Count == 0) return;
            highlighted = e.Key == Key.Down ? Math.Min(shown.Count - 1, highlighted + 1) : Math.Max(0, highlighted - 1);
            Highlight(); shown[highlighted].Row.BringIntoView(); e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (highlighted >= 0 && highlighted < shown.Count) { Choose(shown[highlighted].Zone); return; }
            if (Typed(out _) is { } zone) Choose(zone);
        }
        else if (e.Key == Key.Escape) { e.Handled = true; close(); }
    }
}
