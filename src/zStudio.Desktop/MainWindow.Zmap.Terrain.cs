using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using zStudio.Automation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    /// <summary>A terrain zone's row under the Outlines list: its label, its eye (checked while the zone shows) and the row it dims.</summary>
    private sealed record ZmapTerrainRow(string? Label, string Name, ToggleButton Eye, PreviewIcon Icon, FrameworkElement Row);

    private FrameworkElement zmapCutoffSection = null!, zmapTerrainHeader = null!;
    private StackPanel zmapTerrainRows = null!;
    private ScrollViewer zmapTerrainScroll = null!;
    private TextBlock zmapTerrainSummary = null!, zmapCutoffSummary = null!;
    private Button zmapShowAllTerrain = null!;
    private ValueTextBox zmapTerrainHeight = null!;
    private Button zmapLowerTerrainHeight = null!, zmapRaiseTerrainHeight = null!;
    private float? displayedTerrainHeight;
    private string appliedTerrainHeightText = "";
    private bool syncingTerrainHeightText;
    private readonly Dictionary<byte, ZmapTerrainRow> zmapTerrainChoices = [];
    private bool syncingZmapTerrain;

    private bool CanChangeZmapTerrainVisibility => zmap != null && !zmapBusy && zmapDrag == null;

    /// <summary>
    /// The Height cutoff section of the Outlines pane, between the list and Terrain zones: the world Y above which the map
    /// is not drawn, typed into a box that carries the Y axis letter or stepped by 0.1, with the cutoff in effect in its
    /// header (the box may hold text that is not a height yet).
    /// </summary>
    private FrameworkElement BuildZmapCutoffSection()
    {
        DockPanel header = new() { Margin = new(14, 4, 8, 0), MinHeight = 30 };
        var title = ZmapSectionTitle("Height cutoff", "Hide map geometry above a world Y: terrain, structures, vehicles and pickups. "
            + "What is hidden is no snap target. Only the view changes; nothing is saved.");
        DockPanel.SetDock(title, Dock.Left); header.Children.Add(title);
        // No button ends this header: the readout ends where the icons under it do.
        zmapCutoffSummary = new() { FontSize = 11, Margin = new(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
        zmapCutoffSummary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        header.Children.Add(zmapCutoffSummary);

        Grid row = new() { Margin = new(14, 2, 8, 10) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        // A world coordinate's box, as the card's: the axis letter inside it, and the clear button while it holds text.
        zmapTerrainHeight = new() { ShowClearWhenNonEmpty = true, MaxLength = 40, Height = 32, Padding = new(20, 0, 5, 0), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "The world Y above which the map is hidden. It applies as you type; clear the box to show every height. Esc restores the last height it accepted." };
        AutomationProperties.SetName(zmapTerrainHeight, "Height cutoff (world Y)");
        Grid field = ZmapWithHint(zmapTerrainHeight, "No cutoff", 22);
        field.Margin = new(0, 0, 5, 0);
        field.Children.Add(new TextBlock { Text = "Y", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = FieldEditor.AxisBrushes[1], Margin = new(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        row.Children.Add(field);
        zmapLowerTerrainHeight = Step("Subtract", "Lower cutoff by 0.1", "Lower the cutoff by 0.1", -.1m, 1);
        zmapRaiseTerrainHeight = Step("Add", "Raise cutoff by 0.1", "Raise the cutoff by 0.1", .1m, 2);
        zmapTerrainHeight.TextChanged += (_, _) => ZmapTerrainHeightChanged();

        StackPanel section = new() { Visibility = Visibility.Collapsed };
        section.Children.Add(ZmapSectionLine()); section.Children.Add(header); section.Children.Add(row);
        zmapCutoffSection = section;
        return section;

        Button Step(string icon, string name, string tip, decimal step, int column)
        {
            var button = ZmapIconButton(icon, "CommandButton", name, tip, () => EditZmap(() =>
            {
                if (TryZmapHeightStep(step, out decimal y))
                    ApplyZmapTerrainHeight((float)y, y.ToString("G29", CultureInfo.InvariantCulture));
            }));
            button.Width = button.Height = 30;
            button.IsEnabled = false;
            Grid.SetColumn(button, column);
            row.Children.Add(button);
            return button;
        }
    }

    /// <summary>
    /// The Terrain zones section at the foot of the Outlines pane: its header with Show all, returned, and one row per
    /// object zone that terrain or fixed scenery uses, each with an eye as the World tree's rows have, in
    /// <see cref="zmapTerrainScroll"/>. The pane's layout gives the rows their height.
    /// </summary>
    private FrameworkElement BuildZmapTerrainSection()
    {
        DockPanel header = new() { Margin = new(14, 4, 8, 0), MinHeight = 30 };
        zmapShowAllTerrain = ZmapIconButton("Eye", "CommandButton", "Show all terrain zones", "Show all · show every hidden terrain zone",
            () => EditZmap(() => SetZmapTerrainVisibility(null, true)));
        zmapShowAllTerrain.Width = zmapShowAllTerrain.Height = 30;
        DockPanel.SetDock(zmapShowAllTerrain, Dock.Right); header.Children.Add(zmapShowAllTerrain);
        zmapTerrainSummary = new() { FontSize = 11, Margin = new(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        zmapTerrainSummary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        DockPanel.SetDock(zmapTerrainSummary, Dock.Right); header.Children.Add(zmapTerrainSummary);
        header.Children.Add(ZmapSectionTitle("Terrain zones",
            "Show or hide the ground, water and fixed map scenery by object zone. Hidden pieces are no snap targets. Only the view changes; nothing is saved."));
        zmapTerrainRows = new();
        // It scrolls by rows (the rows' panel does the scrolling), so the rows that show stay whole after a wheel turn.
        zmapTerrainScroll = new() { Content = zmapTerrainRows, CanContentScroll = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(6, 0, 6, 8), Focusable = false, Visibility = Visibility.Collapsed };
        zmapTerrainScroll.SetResourceReference(StyleProperty, "OutlinerScrollStyle");
        StackPanel top = new() { Visibility = Visibility.Collapsed };
        top.Children.Add(ZmapSectionLine()); top.Children.Add(header);
        zmapTerrainHeader = top;
        return top;
    }

    /// <summary>A section's title in the Outlines pane, under the pane's own.</summary>
    private static TextBlock ZmapSectionTitle(string text, string tip)
    {
        TextBlock title = new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return title;
    }

    private static Border ZmapSectionLine()
    {
        Border line = new() { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    private void InitializeZmapTerrainZones()
    {
        var viewport = ZmapLayer.Viewport!;
        if (viewport.ZmapTerrainZones.Count == 0) return;
        var labels = ZoneLabels(zmap!.Document.SourceWorld!);
        foreach (byte zone in viewport.ZmapTerrainZones)
        {
            string? label = labels.TryGetValue(zone, out var authored) ? Bounded(authored, 128) : null;
            string name = zone == 255 ? "Any" : $"Zone {zone.ToString(CultureInfo.InvariantCulture)}";
            PreviewIcon icon = new() { Kind = "Eye", Width = 14, Height = 14 };
            ToggleButton eye = new() { Content = icon, IsChecked = true };
            eye.SetResourceReference(StyleProperty, "OutlinerEyeToggle");
            // From its checked state, so UI Automation's Toggle works as a click.
            eye.Checked += SetFromEye;
            eye.Unchecked += SetFromEye;
            DockPanel row = new() { Height = ZmapZoneRowHeight, Margin = new(4, 0, 8, 0), Background = System.Windows.Media.Brushes.Transparent, ToolTip = label == null ? name : $"{name} · {label}" };
            DockPanel.SetDock(eye, Dock.Right); row.Children.Add(eye);
            Border swatch = new() { Width = 12, Height = 12, CornerRadius = new(3), Margin = new(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF)), Background = ZonePicker.Swatch(zone) };
            DockPanel.SetDock(swatch, Dock.Left); row.Children.Add(swatch);
            TextBlock title = new() { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(title, Dock.Left); row.Children.Add(title);
            TextBlock caption = new() { Text = label ?? "", FontSize = 11, Margin = new(8, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            row.Children.Add(caption);
            zmapTerrainRows.Children.Add(row);
            zmapTerrainChoices.Add(zone, new(label, name, eye, icon, row));

            void SetFromEye(object sender, RoutedEventArgs args)
            {
                if (!syncingZmapTerrain) EditZmap(() => SetZmapTerrainVisibility(zone, eye.IsChecked == true));
            }
        }
    }

    private void ClearZmapTerrainControl()
    {
        zmapTerrainRows.Children.Clear();
        zmapTerrainChoices.Clear();
        DisplayZmapTerrainHeight(null);
        zmapCutoffSection.Visibility = zmapTerrainHeader.Visibility = zmapTerrainScroll.Visibility = Visibility.Collapsed;
    }

    private void RefreshZmapTerrainControl()
    {
        var viewport = ZmapLayer.Viewport!;
        int hidden = 0;
        bool enabled = CanChangeZmapTerrainVisibility;
        syncingZmapTerrain = true;
        try
        {
            foreach (var (zone, row) in zmapTerrainChoices)
            {
                bool visible = viewport.IsZmapTerrainZoneVisible(zone);
                if (!visible) hidden++;
                row.Eye.IsChecked = visible;
                row.Eye.IsEnabled = enabled;
                row.Icon.Kind = visible ? "Eye" : "EyeOff";
                // A hidden zone's row dims, as a hidden object's row does in the World tree.
                row.Row.Opacity = visible ? 1 : .55;
                string tip = (visible ? "Hide " : "Show ") + (zone == 255 ? "Any" : row.Name.ToLowerInvariant()) + " terrain";
                row.Eye.ToolTip = tip;
                AutomationProperties.SetName(row.Eye, tip);
            }
        }
        finally { syncingZmapTerrain = false; }
        zmapCutoffSection.Visibility = Visibility.Visible;
        zmapTerrainHeader.Visibility = zmapTerrainScroll.Visibility = zmapTerrainChoices.Count != 0 ? Visibility.Visible : Visibility.Collapsed;
        zmapShowAllTerrain.IsEnabled = enabled && hidden != 0;
        zmapTerrainSummary.Text = hidden == 0 ? "" : $"{hidden.ToString(CultureInfo.CurrentCulture)} hidden";
        zmapTerrainHeight.IsEnabled = enabled;
        if (displayedTerrainHeight != viewport.ZmapTerrainMaxY) DisplayZmapTerrainHeight(viewport.ZmapTerrainMaxY);
        RefreshZmapHeightSteps();
    }

    private void RefreshZmapHeightSteps()
    {
        bool enabled = CanChangeZmapTerrainVisibility;
        zmapLowerTerrainHeight.IsEnabled = enabled && TryZmapHeightStep(-.1m, out _);
        zmapRaiseTerrainHeight.IsEnabled = enabled && TryZmapHeightStep(.1m, out _);
    }

    private bool TryZmapHeightStep(decimal step, out decimal next)
    {
        next = default;
        if (!decimal.TryParse(zmapTerrainHeight.Text.AsSpan().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal current)
            || current is decimal.MinValue or decimal.MaxValue) return false;
        next = current + step;
        return (float)next != (float)current;
    }

    private void RequireZmapTerrainControls()
    {
        if (!CanChangeZmapTerrainVisibility)
            throw new StudioCommandException("busy", "Finish the ZMAP file operation or vertex drag first.");
    }

    private void SetZmapTerrainVisibility(byte? zone, bool visible)
    {
        RequireZmapTerrainControls();
        if (zone is byte id && !zmapTerrainChoices.ContainsKey(id))
            throw new StudioCommandException("invalid_argument", "Choose an object zone from terrainZones.");
        if (zone == null && !visible)
            throw new StudioCommandException("invalid_argument", "Choose a terrain zone to hide.");
        if (ZmapLayer.Viewport!.SetZmapTerrainVisibility(zone, visible)) zmap!.Touch();
    }

    private void ZmapTerrainHeightChanged()
    {
        RefreshZmapHeightSteps();
        if (syncingTerrainHeightText || !CanChangeZmapTerrainVisibility) return;
        string text = zmapTerrainHeight.Text;
        var value = text.AsSpan().Trim();
        if (value.IsEmpty)
            EditZmap(() => ApplyZmapTerrainHeight(null, text));
        else if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && float.IsFinite(y))
            EditZmap(() => ApplyZmapTerrainHeight(y, text));
    }

    private void ApplyZmapTerrainHeight(float? y, string? text = null)
    {
        RequireZmapTerrainControls();
        if (y is float cutoff && !float.IsFinite(cutoff))
            throw new StudioCommandException("invalid_argument", "Enter a finite Y coordinate.");
        // The viewport may refresh snap controls synchronously during this change.
        DisplayZmapTerrainHeight(y, text);
        if (ZmapLayer.Viewport!.SetZmapTerrainHeight(y)) zmap!.Touch();
    }

    private void DisplayZmapTerrainHeight(float? y, string? text = null)
    {
        displayedTerrainHeight = y;
        // What is in effect, which the box does not say while its text is not a height.
        zmapCutoffSummary.Text = y is float cutoff ? $"above {cutoff.ToString("R", CultureInfo.InvariantCulture)} hidden" : "";
        zmapCutoffSummary.ToolTip = y == null ? null : zmapCutoffSummary.Text;
        appliedTerrainHeightText = text ?? y?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        RestoreZmapTerrainHeightText();
    }

    private void RestoreZmapTerrainHeightText()
    {
        if (zmapTerrainHeight.Text == appliedTerrainHeightText) return;
        syncingTerrainHeightText = true;
        try { zmapTerrainHeight.Text = appliedTerrainHeightText; }
        finally { syncingTerrainHeightText = false; }
    }
}
