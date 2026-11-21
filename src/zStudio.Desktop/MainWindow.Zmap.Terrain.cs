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

    private FrameworkElement zmapTerrainSection = null!;
    private StackPanel zmapTerrainRows = null!;
    private ScrollViewer zmapTerrainScroll = null!;
    private TextBlock zmapTerrainSummary = null!;
    private Button zmapShowAllTerrain = null!;
    private TextBox zmapTerrainHeight = null!;
    private Button zmapApplyTerrainHeight = null!, zmapClearTerrainHeight = null!;
    private float? displayedTerrainHeight;
    private readonly Dictionary<byte, ZmapTerrainRow> zmapTerrainChoices = [];
    private bool syncingZmapTerrain;

    private bool CanChangeZmapTerrainVisibility => zmap != null && !zmapBusy && zmapDrag == null;

    /// <summary>
    /// The Terrain zones section at the foot of the Outlines pane: one row per object zone that terrain or fixed scenery uses,
    /// each with an eye as the World tree's rows have, and Show all in its header.
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
        TextBlock title = new() { Text = "Terrain zones", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Show or hide the ground, water and fixed map scenery by object zone. Hidden pieces are no snap targets. Only the view changes; nothing is saved." };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        header.Children.Add(title);
        StackPanel height = new() { Margin = new(14, 4, 8, 4) };
        var heightLabel = new TextBlock { Text = "Hide terrain above", FontSize = 12, Margin = new(0, 0, 0, 4) };
        heightLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        height.Children.Add(heightLabel);
        Grid heightInput = new();
        heightInput.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heightInput.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heightInput.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        height.Children.Add(heightInput);
        zmapTerrainHeight = new() { MaxLength = 40, Padding = new(5, 2, 5, 2), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Maximum world Y. Enter an empty value to clear the cutoff." };
        AutomationProperties.SetLabeledBy(zmapTerrainHeight, heightLabel);
        heightInput.Children.Add(zmapTerrainHeight);
        zmapClearTerrainHeight = ZmapIconButton("Close", "CommandButton", "Clear terrain cutoff", "Clear the Y cutoff",
            () => EditZmap(() => ApplyZmapTerrainHeight(null)));
        zmapApplyTerrainHeight = ZmapIconButton("Apply", "CommandButton", "Apply terrain cutoff", "Apply the Y cutoff (Enter)",
            () => EditZmap(ApplyZmapTerrainHeightText));
        Grid.SetColumn(zmapApplyTerrainHeight, 1);
        Grid.SetColumn(zmapClearTerrainHeight, 2);
        foreach (var button in new[] { zmapApplyTerrainHeight, zmapClearTerrainHeight })
        {
            button.Width = button.Height = 26;
            button.Margin = new(4, 0, 0, 0);
            heightInput.Children.Add(button);
        }
        zmapTerrainRows = new();
        // At most seven rows show (fewer in a short pane, see BuildZmapOutlinesPane); a map with more zones scrolls them.
        ScrollViewer scroll = new() { Content = zmapTerrainRows, MaxHeight = 196, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(6, 0, 6, 8), Focusable = false };
        scroll.SetResourceReference(StyleProperty, "OutlinerScrollStyle");
        zmapTerrainScroll = scroll;
        Border line = new() { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        StackPanel section = new() { Visibility = Visibility.Collapsed };
        section.Children.Add(line); section.Children.Add(header); section.Children.Add(height); section.Children.Add(scroll);
        AutomationProperties.SetName(section, "Terrain zones");
        zmapTerrainSection = section;
        return section;
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
            DockPanel row = new() { Height = 28, Margin = new(4, 0, 8, 0), Background = System.Windows.Media.Brushes.Transparent, ToolTip = label == null ? name : $"{name} · {label}" };
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
        zmapTerrainSection.Visibility = Visibility.Collapsed;
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
        zmapTerrainSection.Visibility = zmapTerrainChoices.Count != 0 ? Visibility.Visible : Visibility.Collapsed;
        zmapShowAllTerrain.IsEnabled = enabled && hidden != 0;
        zmapTerrainSummary.Text = hidden == 0 ? "" : $"{hidden.ToString(CultureInfo.CurrentCulture)} hidden";
        zmapTerrainHeight.IsEnabled = zmapApplyTerrainHeight.IsEnabled = zmapClearTerrainHeight.IsEnabled = enabled;
        if (displayedTerrainHeight != viewport.ZmapTerrainMaxY) DisplayZmapTerrainHeight(viewport.ZmapTerrainMaxY);
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

    private void ApplyZmapTerrainHeightText()
    {
        string text = zmapTerrainHeight.Text.Trim();
        if (text.Length == 0) { ApplyZmapTerrainHeight(null); return; }
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) || !float.IsFinite(y))
            throw new StudioCommandException("invalid_argument", "Enter a finite Y coordinate, or clear the height field.");
        ApplyZmapTerrainHeight(y);
    }

    private void ApplyZmapTerrainHeight(float? y)
    {
        RequireZmapTerrainControls();
        if (y is float cutoff && !float.IsFinite(cutoff))
            throw new StudioCommandException("invalid_argument", "Enter a finite Y coordinate.");
        if (ZmapLayer.Viewport!.SetZmapTerrainHeight(y)) zmap!.Touch();
        DisplayZmapTerrainHeight(y);
    }

    private void DisplayZmapTerrainHeight(float? y)
    {
        displayedTerrainHeight = y;
        zmapTerrainHeight.Text = y?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    }
}
