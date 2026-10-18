using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using zStudio.Core;

namespace zStudio.Desktop;

/// <summary>
/// The Zone section's read-only polygon zones (design/zone-section-ui.md): under a divider and its own sub-header, one row
/// per zone set of the object's polygons (its own and those below it), each a swatch in the overlay's colours, the zones'
/// name, their labels and the polygon count. Text blocks only: nothing in it takes input; right-click copies it.
/// </summary>
internal sealed partial class SourceObjectCardEditor
{
    private const string PolygonZonesHelp = "The zones each polygon makes current while a vehicle or the camera is on it. Counted over this object and everything below it. Paint them with Edit zones in the Zones group.";

    /// <summary>
    /// Shows the polygon zones of <c>scene_inspect</c>'s <c>Object polygon zones</c> (rows of <c>zones</c>, or
    /// <c>moreSets</c> or <c>unknown</c>, each with <c>polygons</c>; <c>total</c>; <c>truncated</c>); null shows nothing.
    /// </summary>
    internal void ShowPolygonZones(JsonObject? data, Func<byte, string?>? label, string objectName)
    {
        polygonZones.Children.Clear(); polygonZones.ContextMenu = null;
        if (data?["rows"] is not JsonArray rows) return;
        long total = Number(data["total"]);
        string? truncated = data["truncated"] is JsonValue tv && tv.TryGetValue<string>(out var kind) ? kind : null;

        Border divider = new() { Height = 1, Margin = new(0, 8, 0, 6) };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        polygonZones.Children.Add(divider);

        Grid header = new() { MinHeight = 20, Background = Brushes.Transparent, ToolTip = PolygonZonesHelp };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        StackPanel title = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "Polygon zones", FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = .7, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(Glyph("", new(4, 0, 0, 0)));
        header.Children.Add(title);
        if (rows.Count >= 2)
        {
            TextBlock sum = new() { Text = truncated != null ? Count(total) + "+ polygons" : Polygons(total), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            sum.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            System.Windows.Documents.Typography.SetNumeralAlignment(sum, FontNumeralAlignment.Tabular);
            Grid.SetColumn(sum, 1); header.Children.Add(sum);
        }
        polygonZones.Children.Add(header);

        if (rows.Count == 0)
        {
            TextBlock none = new() { Text = "No polygons here or below.", FontSize = 12, Margin = new(0, 2, 0, 2) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            polygonZones.Children.Add(none);
            return;
        }
        StackPanel list = new(); Grid.SetIsSharedSizeScope(list, true);
        AutomationProperties.SetName(list, "Polygon zones");
        AutomationProperties.SetHelpText(list, Polygons(total) + (truncated != null ? " counted" : ""));
        StringBuilder copy = new($"Polygon zones of {objectName} ({(truncated != null ? Count(total) + "+ polygons" : Polygons(total))})");
        foreach (var row in rows.OfType<JsonObject>())
        {
            long count = Number(row["polygons"]);
            list.Children.Add(Row(row, count, label, copy));
        }
        polygonZones.Children.Add(list);
        if (truncated != null)
        {
            StackPanel note = new() { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 0) };
            note.Children.Add(Glyph("", new(0, 0, 6, 0)));
            TextBlock text = new() { Text = truncated == "objects" ? "Counted the first 4,096 objects only." : "Counted the first 200,000 polygons only.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            note.Children.Add(text); polygonZones.Children.Add(note);
        }
        // Copying replaces the selectable text box the list used to be.
        string clipboard = copy.ToString();
        MenuItem item = new() { Header = "Copy polygon zones" };
        item.Click += (_, _) => { try { Clipboard.SetText(clipboard); } catch (System.Runtime.InteropServices.ExternalException) { } };
        polygonZones.ContextMenu = new ContextMenu { Items = { item } };
    }

    private static FrameworkElement Row(JsonObject row, long count, Func<byte, string?>? label, StringBuilder copy)
    {
        Grid grid = new() { Height = 24, Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new() { Width = new(20) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto, SharedSizeGroup = "ZoneName", MinWidth = 58, MaxWidth = 132 });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        string name, accessible, labels = "", tip = "";
        bool secondaryName = false, spans = false;
        FrameworkElement? swatch;
        if (row["moreSets"] is JsonValue && Number(row["moreSets"]) is long sets)
        {
            name = sets == 1 ? "1 more zone set" : $"{Count(sets)} more zone sets"; accessible = name;
            swatch = null; secondaryName = true; spans = true;
        }
        else if (row["unknown"] is JsonValue)
        {
            name = "Unknown zones"; accessible = name; tip = "The zones of these polygons could not be read.";
            swatch = Glyph("", new(0)); secondaryName = true; spans = true;
        }
        else
        {
            byte[] zones = row["zones"] is JsonArray ids ? [.. ids.Select(id => id is JsonValue v && v.TryGetValue<int>(out int z) ? (byte)z : (byte)0)] : [];
            if (zones.Length == 0)
            {
                name = "No zones"; accessible = name; secondaryName = true;
                swatch = new Rectangle { Width = 12, Height = 12, StrokeThickness = 1, StrokeDashArray = [2, 2], VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
                swatch.SetResourceReference(Shape.StrokeProperty, "TextFillColorTertiaryBrush");
            }
            else if (zones is [255])
            {
                name = "Any"; labels = "No zone limit"; accessible = "Any zone, no zone limit";
                swatch = Swatch(zones);
            }
            else
            {
                static string Id(byte z) => z == 255 ? "Any" : z.ToString(CultureInfo.InvariantCulture);
                name = (zones.Length == 1 ? "Zone " : "Zones ") + string.Join(", ", zones.Select(Id));
                var named = zones.Where(z => z != 255).Select(z => (Zone: z, Label: label?.Invoke(z))).ToArray();
                labels = string.Join(" · ", named.Where(n => n.Label != null).Select(n => n.Label));
                tip = zones.Length > 1 ? string.Join("\n", named.Select(n => $"Zone {n.Zone}" + (n.Label != null ? " · " + n.Label : ""))) : labels;
                accessible = zones.Length == 1 ? name + (labels.Length > 0 ? ", " + labels : "") : "Zones " + JoinAnd(zones.Select(Id).ToArray());
                swatch = Swatch(zones);
            }
        }
        if (swatch != null) { swatch.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(swatch); }
        TextBlock shownName = new() { Text = name, FontSize = row["moreSets"] != null ? 12 : 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        shownName.SetResourceReference(TextBlock.ForegroundProperty, row["moreSets"] != null ? "TextFillColorTertiaryBrush" : secondaryName ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        System.Windows.Documents.Typography.SetNumeralAlignment(shownName, FontNumeralAlignment.Tabular);
        Grid.SetColumn(shownName, 1); if (spans) Grid.SetColumnSpan(shownName, 2);
        grid.Children.Add(shownName);
        if (!spans && labels.Length > 0)
        {
            TextBlock shownLabels = new() { Text = labels, FontSize = 12, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = labels };
            shownLabels.SetResourceReference(TextBlock.ForegroundProperty, name == "Any" ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush");
            Grid.SetColumn(shownLabels, 2); grid.Children.Add(shownLabels);
        }
        TextBlock shownCount = new() { Text = Polygons(count), FontSize = 12, Margin = new(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        shownCount.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        System.Windows.Documents.Typography.SetNumeralAlignment(shownCount, FontNumeralAlignment.Tabular);
        Grid.SetColumn(shownCount, 3); grid.Children.Add(shownCount);
        if (tip.Length > 0) grid.ToolTip = tip;
        AutomationProperties.SetName(grid, $"{accessible}: {Polygons(count)}");
        copy.Append('\n').Append(name).Append(labels.Length > 0 && name != "Any" ? $" ({labels})" : "").Append(": ").Append(Polygons(count));
        return grid;
    }

    /// <summary>A zone set's swatch in the overlay's colours: one colour, or diagonal stripes in the set's stored order.</summary>
    private static Border Swatch(byte[] zones)
    {
        Brush fill;
        if (zones.Length == 1) fill = ZonePicker.Swatch(zones[0]);
        else
        {
            const double Stripe = 3;
            GradientStopCollection stops = [];
            for (int i = 0; i < zones.Length; i++)
            {
                Color color = ((SolidColorBrush)ZonePicker.Swatch(zones[i])).Color;
                stops.Add(new(color, (double)i / zones.Length)); stops.Add(new(color, (double)(i + 1) / zones.Length));
            }
            double length = Stripe * zones.Length;
            var gradient = new LinearGradientBrush(stops, new Point(0, 0), new Point(length, length)) { MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat };
            gradient.Freeze(); fill = gradient;
        }
        return new Border { Width = 12, Height = 12, CornerRadius = new(3), Background = fill, BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Left };
    }
    private static TextBlock Glyph(string glyph, Thickness margin)
    {
        TextBlock icon = new() { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return icon;
    }
    /// <summary>A whole number however the JSON value holds it (the producer stores ints).</summary>
    private static long Number(JsonNode? node) => node is JsonValue value ? value.TryGetValue<long>(out long wide) ? wide : value.TryGetValue<int>(out int narrow) ? narrow : 0 : 0;
    private static string Count(long count) => count.ToString("N0", CultureInfo.CurrentCulture);
    private static string Polygons(long count) => count == 1 ? "1 polygon" : $"{Count(count)} polygons";
    private static string JoinAnd(string[] parts) => parts.Length <= 1 ? string.Join("", parts) : string.Join(", ", parts[..^1]) + " and " + parts[^1];
}
