using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>
/// The Export ZBD files window's mission × file grid: one row per mission, one column per kind of file under the map files,
/// rtexture, texture and rgbatexture sections, with a tri-state box for each section, column, mission and All missions. The header rows
/// and the mission column stay while the body scrolls. One tab stop; the arrows move inside, skipping cells without a file
/// (design/export-zbd-files.md §4, §8). After the last section a tail column, empty in every row and never a stop, gives
/// that section's header box room for its label (design/export-zbd-files-rgba.md §3).
/// </summary>
internal sealed class SourceExportGrid : Grid
{
    internal const double RowHeight = 28, Gap = 12;
    /// <summary>Room under the last mission row and after the last column while the body scrolls sideways, for the overlay scroll bars at their expanded size.</summary>
    private const double ScrollBarRoom = 16;
    private static readonly (double Min, double Max) HeaderWidth = (112, 152), MapWidth = (48, 64), PackWidth = (34, 48);
    private readonly SourceExportSelection model;
    /// <summary>The grid's columns left to right: a kind, or null for the gap between two sections.</summary>
    private readonly List<SourceExportKind?> columns = [];
    private readonly Grid corner = new(), header = new(), rowHeaders = new() { Background = Brushes.Transparent }, cells = new() { Background = Brushes.Transparent };
    private readonly ScrollViewer headerScroll = Scroller(ScrollBarVisibility.Hidden, ScrollBarVisibility.Disabled), rowScroll = Scroller(ScrollBarVisibility.Disabled, ScrollBarVisibility.Hidden),
        bodyScroll = Scroller(ScrollBarVisibility.Auto, ScrollBarVisibility.Auto);
    private readonly List<(SourceExportSection Section, CheckBox Box, int First)> sectionBoxes = [];
    private readonly List<Border> rowTints = [], rowHeaderTints = [];
    private readonly Dictionary<int, Border> columnTints = [];
    /// <summary>The boxes as the arrows see them: the section row, the column-box row, then one row per mission; column 0 is the row headers.</summary>
    private readonly CheckBox?[][] map;
    private int lastColumn = -1;
    private (int Row, int Column) hover = (-1, -1);
    private (double RowHeader, double Map, double Pack, double Tail) fitted;
    /// <summary>The grid's width with every column at its maximum, the tail included (known once the grid is loaded).</summary>
    internal double MaximumWidth { get; private set; }
    private double minimumWidth;
    /// <summary>The widths without the tail column, at the columns' minimum and maximum.</summary>
    private readonly double columnsMinimum, columnsMaximum;
    /// <summary>The tail column's index in the header and body grids: one past the last section's columns.</summary>
    private readonly int tail;
    /// <summary>Called with the row header's width whenever the columns are sized again.</summary>
    internal Action<double>? WidthsChanged { get; set; }
    internal CheckBox? FirstCell { get; }

    /// <summary>
    /// What a "–" cell means: the mission builds no file of the column; in a 32-bit tier's column, that its textures all fit a
    /// smaller full-size pack, that the tier is its full-size pack (in the full column), or that a smaller tier was left out.
    /// </summary>
    private static string AbsentTip(SourceExportSet mission, SourceExportKind kind)
    {
        if (kind.Section == SourceExportSelection.RgbaSection && !kind.Automatic
            && mission.Files.FirstOrDefault(f => f.Kind == SourceExportSelection.FullSizeKind) is { BudgetMegabytes: long full } fullPack
            && Core.Sources.BuildProfiles.RgbaTextureMegabytes(kind.Kind) is long tier)
        {
            string file = fullPack.Path[(fullPack.Path.LastIndexOf('/') + 1)..];
            return tier > full ? $"{mission.Name} needs no {kind.Kind}: all its textures fit its full-size pack, {file}"
                : tier == full ? $"{mission.Name}'s {kind.Kind} is its full-size pack, in the full column"
                : $"{mission.Name}'s {kind.Kind} would equal a larger tier, so the export leaves it out";
        }
        return $"The project builds no {(!kind.Automatic ? kind.Kind : kind.Section == SourceExportSelection.RgbaSection ? "full-size 32-bit pack" : "automatic pack")} for {mission.Name}";
    }
    /// <summary>A column label's tooltip: what the column's packs are.</summary>
    private static string ColumnTip(SourceExportKind kind)
    {
        if (kind.Section == SourceExportSelection.RgbaSection)
            return kind.Automatic ? "Each mission's full-size 32-bit pack: rgbatexture<N>.zbd, N the smallest tier (MB) that holds every texture at its authored size"
                : $"{kind.Kind}: the 32-bit pack reRECOIL v2 opens with {Core.Sources.BuildProfiles.RgbaTextureMegabytes(kind.Kind)} MB of texture memory, textures reduced to fit";
        return kind.Automatic ? "The automatic 16-bit Direct3D pack: rtexture<N>.zbd, N the MB its textures need" : kind.Kind;
    }

    internal SourceExportGrid(SourceExportSelection model)
    {
        this.model = model;
        Focusable = false;
        AutomationProperties.SetName(this, "Mission files"); AutomationProperties.SetAutomationId(this, "MissionFiles");
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Contained);
        // The body extends into the card's right padding so its overlay scroll bar stays clear of the last column's boxes.
        Margin = new(0, 0, -8, 0);
        foreach (var section in model.Sections)
        {
            if (columns.Count > 0) columns.Add(null);
            columns.AddRange(section.Kinds);
        }
        int mapColumns = columns.Count(c => c?.Section == SourceExportSelection.MapSection), packColumns = columns.Count(c => c != null) - mapColumns, gaps = columns.Count(c => c == null);
        minimumWidth = columnsMinimum = HeaderWidth.Min + mapColumns * MapWidth.Min + packColumns * PackWidth.Min + gaps * Gap;
        MaximumWidth = columnsMaximum = HeaderWidth.Max + mapColumns * MapWidth.Max + packColumns * PackWidth.Max + gaps * Gap;
        tail = columns.Count;

        ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        headerScroll.Content = header; rowScroll.Content = rowHeaders; bodyScroll.Content = cells;
        SetColumn(headerScroll, 1); SetRow(rowScroll, 1); SetRow(bodyScroll, 1); SetColumn(bodyScroll, 1);
        Children.Add(corner); Children.Add(headerScroll); Children.Add(rowScroll); Children.Add(bodyScroll);
        foreach (var part in new[] { corner, header }) foreach (double height in new double[] { 30, 18, 30, 1 }) part.RowDefinitions.Add(new() { Height = new(height) });
        corner.ColumnDefinitions.Add(new() { Width = new(HeaderWidth.Min) }); rowHeaders.ColumnDefinitions.Add(new() { Width = new(HeaderWidth.Min) });
        foreach (var column in columns) { header.ColumnDefinitions.Add(new() { Width = new(column == null ? Gap : 34) }); cells.ColumnDefinitions.Add(new() { Width = new(column == null ? Gap : 34) }); }
        header.ColumnDefinitions.Add(new() { Width = new(0) }); cells.ColumnDefinitions.Add(new() { Width = new(0) });
        foreach (var _ in model.Missions) { rowHeaders.RowDefinitions.Add(new() { Height = new(RowHeight) }); cells.RowDefinitions.Add(new() { Height = new(RowHeight) }); }

        map = new CheckBox?[2 + model.Missions.Count][];
        for (int r = 0; r < map.Length; r++) map[r] = new CheckBox?[columns.Count + 1];

        // Row tints (hover) under everything else, then the dividers, then the boxes.
        for (int r = 0; r < model.Missions.Count; r++)
        {
            Border tint = new(), headerTint = new();
            SetRow(tint, r); SetColumnSpan(tint, columns.Count + 1); SetRow(headerTint, r);
            cells.Children.Add(tint); rowHeaders.Children.Add(headerTint); rowTints.Add(tint); rowHeaderTints.Add(headerTint);
        }
        for (int c = 0; c < columns.Count; c++)
        {
            if (columns[c] == null)
            {
                header.Children.Add(VerticalLine(c, 2, 1)); cells.Children.Add(VerticalLine(c, 0, Math.Max(1, model.Missions.Count)));
                continue;
            }
            Border tint = new(); SetColumn(tint, c); SetRow(tint, 1); SetRowSpan(tint, 2); header.Children.Add(tint); columnTints[c] = tint;
        }
        foreach (var part in new[] { corner, header })
        {
            var line = SourceExportSelectionDialog.Divider(); line.Margin = new(0); SetRow(line, 3); SetColumnSpan(line, Math.Max(1, part.ColumnDefinitions.Count)); part.Children.Add(line);
        }

        // Section row: each section's box over its first column's box.
        int first = 0;
        foreach (var section in model.Sections)
        {
            first = columns.IndexOf(section.Kinds[0]);
            string packs = section.Id switch
            {
                SourceExportSelection.HardwareSection => "16-bit Direct3D texture pack (rtexture)",
                SourceExportSelection.RgbaSection => "32-bit texture pack for reRECOIL v2 (rgbatexture)",
                _ => "16-bit software-renderer texture pack (texture)",
            };
            var box = Box(section, section.Detail.Length > 0 ? $"{section.Label} ({section.Detail}): every {packs}" : $"{section.Label}: gamez, anim and zrdr of every mission",
                section.Id == SourceExportSelection.MapSection ? "Select or clear the world, animations and resources of every mission" : $"Select or clear every {packs}");
            TextBlock label = new() { FontSize = 13 };
            label.Inlines.Add(new System.Windows.Documents.Run(section.Label));
            if (section.Kinds.Count >= 3 && section.Detail.Length > 0)
            {
                var detail = new System.Windows.Documents.Run(" " + section.Detail) { FontSize = 12 };
                detail.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
                label.Inlines.Add(detail);
            }
            box.Content = label; SourceExportSelectionDialog.Labeled(box);
            box.HorizontalAlignment = HorizontalAlignment.Left; box.VerticalAlignment = VerticalAlignment.Center;
            // Over the gap after it, or the last section's over the tail column.
            int span = section.Kinds.Count + 1;
            SetColumn(box, first); SetColumnSpan(box, span); header.Children.Add(box);
            sectionBoxes.Add((section, box, first));
            for (int c = 0; c < section.Kinds.Count; c++) map[0][first + c + 1] = box;
        }

        // Column labels and boxes, with All missions in the row header.
        var allMissions = Box(model.AllMissions, $"All missions: {model.AllMissions.Files.Count:N0} files", "Select or clear every mission file");
        allMissions.Content = "All missions"; SourceExportSelectionDialog.Labeled(allMissions); allMissions.HorizontalAlignment = HorizontalAlignment.Left;
        Place(corner, allMissions, 2, 0, left: 6); map[1][0] = allMissions;
        for (int c = 0; c < columns.Count; c++)
        {
            if (columns[c] is not { } kind) continue;
            bool fullSize = kind.Section == SourceExportSelection.RgbaSection;
            string name = !kind.Automatic ? kind.Kind : fullSize ? "The full-size rgbatexture pack" : "The automatic rtexture pack";
            TextBlock label = new() { Text = kind.Label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, LineHeight = 14,
                ToolTip = ColumnTip(kind), Background = Brushes.Transparent };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            SetColumn(label, c); SetRow(label, 1); header.Children.Add(label);
            // A 32-bit tier's own column holds its file only where the mission's full-size tier is larger: name and help say so.
            bool belowFull = kind.Section == SourceExportSelection.RgbaSection && !kind.Automatic;
            var box = Box(kind, belowFull ? $"{name} below each mission's full-size tier" : $"{name} in every mission",
                belowFull ? $"{name} in every mission that builds it below its full-size tier" : $"{name} in every mission that builds it");
            Place(header, box, 2, c); map[1][c + 1] = box;
        }

        // Missions: the row header (box, name, "N of M" while mixed), then a box per file or "–".
        for (int r = 0; r < model.Missions.Count; r++)
        {
            var mission = model.Missions[r];
            var box = Box(mission, $"{mission.Name}: all {mission.Files.Count:N0} files", $"Select or clear every file of {mission.Name}");
            TextBlock count = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0) };
            count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            Typography.SetNumeralAlignment(count, FontNumeralAlignment.Tabular);
            count.SetBinding(TextBlock.TextProperty, new Binding(nameof(SourceExportSet.Count)) { Source = mission });
            DockPanel content = new(); DockPanel.SetDock(count, Dock.Right);
            content.Children.Add(count); content.Children.Add(new TextBlock { Text = mission.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            box.Content = content; SourceExportSelectionDialog.Labeled(box);
            box.HorizontalContentAlignment = HorizontalAlignment.Stretch; box.HorizontalAlignment = HorizontalAlignment.Stretch;
            Place(rowHeaders, box, r, 0, left: 6, right: 6); map[r + 2][0] = box;
            for (int c = 0; c < columns.Count; c++)
            {
                if (columns[c] is not { } kind) continue;
                if (model.Cell(mission, kind) is { } file)
                {
                    var cell = Box(file, file.Path, file.Description);
                    cell.ToolTip = $"{file.Path}\n{file.Description}";
                    Place(cells, cell, r, c); map[r + 2][c + 1] = cell;
                    FirstCell ??= cell;
                }
                else
                {
                    TextBlock none = new() { Text = "–", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = AbsentTip(mission, kind) };
                    none.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorDisabledBrush");
                    SetColumn(none, c); SetRow(none, r); cells.Children.Add(none);
                }
            }
        }
        // One tab stop: the box focused last (m1's first file at first); Tab returns to it.
        SourceExportRoving.Attach(this, map.SelectMany(r => r).OfType<CheckBox>().Distinct().ToArray(), FirstCell ?? map.SelectMany(r => r).OfType<CheckBox>().FirstOrDefault());

        bodyScroll.ScrollChanged += (_, e) =>
        {
            // When the columns reach into the 8 DIP the body keeps for the vertical scroll bar (see the constructor), or scroll sideways,
            // the overlay scroll bars would cover the last column's boxes and the last row's: room after the columns and under the rows
            // lets them scroll clear. The row headers and the column header get the same room so they scroll as far; the test leaves the
            // room out, so it never keeps itself.
            double room = bodyScroll.ExtentWidth - cells.Margin.Right > bodyScroll.ViewportWidth - 8 + 0.5 ? ScrollBarRoom : 0;
            if (cells.Margin.Bottom != room) { cells.Margin = new(0, 0, room, room); rowHeaders.Margin = new(0, 0, 0, room); header.Margin = new(0, 0, room, 0); }
            Sync(headerScroll, e.HorizontalOffset, horizontal: true); Sync(rowScroll, e.VerticalOffset, horizontal: false);
        };
        rowScroll.ScrollChanged += (_, e) => Sync(bodyScroll, e.VerticalOffset, horizontal: false);
        headerScroll.ScrollChanged += (_, e) => Sync(bodyScroll, e.HorizontalOffset, horizontal: true);
        // The wheel scrolls the body wherever the pointer is over the grid.
        PreviewMouseWheel += (_, e) =>
        {
            if (IsWithin(e.OriginalSource as DependencyObject, bodyScroll)) return;
            e.Handled = true; bodyScroll.ScrollToVerticalOffset(bodyScroll.VerticalOffset - e.Delta * 48.0 / 120);
        };
        cells.MouseMove += (_, e) => { var p = e.GetPosition(cells); Hover((int)(p.Y / RowHeight), ColumnAt(p.X)); };
        rowHeaders.MouseMove += (_, e) => Hover((int)(e.GetPosition(rowHeaders).Y / RowHeight), -1);
        cells.MouseLeave += (_, _) => Hover(-1, -1); rowHeaders.MouseLeave += (_, _) => Hover(-1, -1);
        PreviewKeyDown += MoveFocus;
        SizeChanged += (_, _) => Fit();
        Loaded += (_, _) => Fit();
    }

    private static ScrollViewer Scroller(ScrollBarVisibility horizontal, ScrollBarVisibility vertical) => new()
    { HorizontalScrollBarVisibility = horizontal, VerticalScrollBarVisibility = vertical, Focusable = false, IsTabStop = false, CanContentScroll = false, PanningMode = PanningMode.Both };
    private static void Sync(ScrollViewer target, double offset, bool horizontal)
    {
        if (horizontal ? Math.Abs(target.HorizontalOffset - offset) > 0.5 : Math.Abs(target.VerticalOffset - offset) > 0.5)
            if (horizontal) target.ScrollToHorizontalOffset(offset); else target.ScrollToVerticalOffset(offset);
    }
    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var node = element; node != null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node == ancestor) return true;
        return false;
    }
    private Border VerticalLine(int column, int row, int span)
    {
        Border line = new() { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, SnapsToDevicePixels = true };
        line.SetResourceReference(Border.BackgroundProperty, SystemParameters.HighContrast ? SystemColors.GrayTextBrushKey : "DividerStrokeColorDefaultBrush");
        SetColumn(line, column); SetRow(line, row); SetRowSpan(line, span);
        return line;
    }
    private static SourceExportCheckBox Box(object source, string name, string help)
    {
        SourceExportCheckBox box = new() { MinWidth = 0, MinHeight = 0, Padding = new(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ToolTip = help };
        box.SetResourceReference(StyleProperty, "DefaultCheckBoxStyle");
        box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("IsChecked") { Source = source, Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(box, name); AutomationProperties.SetHelpText(box, help);
        return box;
    }
    private static void Place(Grid grid, FrameworkElement element, int row, int column, double left = 0, double right = 0)
    {
        if (left > 0 || right > 0) element.Margin = new(left, 0, right, 0);
        SetRow(element, row); SetColumn(element, column); grid.Children.Add(element);
    }

    /// <summary>
    /// Every column grows from its minimum to its maximum with the card; past the maximum the window centres the block. The tail
    /// column is as wide as the last section's header box reaches past that section's columns (its label's width plus the
    /// box's indent, less the columns), so a one-column section's label is never clipped; the grid's minimum and maximum
    /// widths include it.
    /// </summary>
    private void Fit()
    {
        if (sectionBoxes.Count == 0) return;
        var (lastSection, lastBox, lastFirst) = sectionBoxes[^1];
        bool lastIsMap = lastSection.Id == SourceExportSelection.MapSection;
        // The box's own width, without the indent Fit gives it.
        var indent = lastBox.Margin; lastBox.Margin = new(0); lastBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); lastBox.Margin = indent;
        double label = Math.Ceiling(lastBox.DesiredSize.Width);
        double TailAt(double column) => Math.Max(0, label + Math.Floor((column - 20) / 2) - lastSection.Kinds.Count * column);
        minimumWidth = columnsMinimum + TailAt(lastIsMap ? MapWidth.Min : PackWidth.Min);
        double maximum = columnsMaximum + TailAt(lastIsMap ? MapWidth.Max : PackWidth.Max);
        bool grew = maximum != MaximumWidth; MaximumWidth = maximum;
        // The grid reaches 8 DIP into the card's padding (see the constructor); the columns share the card's own width.
        double available = ActualWidth - 8;
        double t = MaximumWidth > minimumWidth ? Math.Clamp((available - minimumWidth) / (MaximumWidth - minimumWidth), 0, 1) : 1;
        double rowHeader = Math.Floor(HeaderWidth.Min + t * (HeaderWidth.Max - HeaderWidth.Min)), mapWidth = Math.Floor(MapWidth.Min + t * (MapWidth.Max - MapWidth.Min)),
            packWidth = Math.Floor(PackWidth.Min + t * (PackWidth.Max - PackWidth.Min));
        double tailWidth = TailAt(lastIsMap ? mapWidth : packWidth);
        if (fitted == (rowHeader, mapWidth, packWidth, tailWidth) && !grew) return;
        fitted = (rowHeader, mapWidth, packWidth, tailWidth);
        corner.ColumnDefinitions[0].Width = rowHeaders.ColumnDefinitions[0].Width = new(rowHeader);
        for (int c = 0; c < columns.Count; c++) header.ColumnDefinitions[c].Width = cells.ColumnDefinitions[c].Width = new(Width(columns[c]));
        header.ColumnDefinitions[tail].Width = cells.ColumnDefinitions[tail].Width = new(tailWidth);
        foreach (var (section, box, first) in sectionBoxes) box.Margin = new(Math.Floor((Width(columns[first]) - 20) / 2), 0, 0, 0);
        WidthsChanged?.Invoke(rowHeader);
        double Width(SourceExportKind? kind) => kind == null ? Gap : kind.Section == SourceExportSelection.MapSection ? mapWidth : packWidth;
    }

    private int ColumnAt(double x)
    {
        double left = 0;
        for (int c = 0; c < columns.Count; c++) { double width = cells.ColumnDefinitions[c].ActualWidth; if (x < left + width) return columns[c] == null ? -1 : c; left += width; }
        return -1;
    }
    /// <summary>The row under the pointer and its column's label and box: a crosshair to read a cell.</summary>
    private void Hover(int row, int column)
    {
        if (row >= model.Missions.Count) row = -1;
        if (hover == (row, column)) return;
        Tint(hover.Row, hover.Column, false); hover = (row, column); Tint(row, column, true);
    }
    private void Tint(int row, int column, bool on)
    {
        object key = SystemParameters.HighContrast ? SystemColors.HighlightBrushKey : "SubtleFillColorSecondaryBrush";
        void Set(Border border) { if (on) border.SetResourceReference(Border.BackgroundProperty, key); else border.ClearValue(Border.BackgroundProperty); }
        if (row >= 0 && row < rowTints.Count) { Set(rowTints[row]); Set(rowHeaderTints[row]); }
        if (columnTints.TryGetValue(column, out var tint)) Set(tint);
    }

    // ---------- keyboard: arrows, Home/End, Page Up/Down inside the grid ----------

    private void MoveFocus(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            e.Handled = Navigate(e.Key, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }
    /// <summary>Moves the focus from the focused box as the key does; false when the focus is not on one of the grid's boxes.</summary>
    internal bool Navigate(Key key, bool control)
    {
        if (Keyboard.FocusedElement is not CheckBox focused) return false;
        int row = Array.FindIndex(map, r => r.Contains(focused));
        if (row < 0) return false;
        int column = Array.IndexOf(map[row], focused);
        if (lastColumn >= 0 && lastColumn < map[row].Length && map[row][lastColumn] == focused) column = lastColumn;
        int width = map[row].Length, last = map.Length - 1;
        (int, int)? target = key switch
        {
            Key.Left => Step(0, -1), Key.Right => Step(0, 1), Key.Up => Step(-1, 0), Key.Down => Step(1, 0),
            Key.Home => control ? FirstIn(2, from: 1) : FirstIn(row, from: 0),
            Key.End => control ? LastIn(last) : LastIn(row),
            Key.PageUp => Vertical(2, 1), Key.PageDown => Vertical(last, -1),
            _ => ((int, int)?)null,
        };
        if (target is not { } t || map[t.Item1][t.Item2] is not { } next) return true;
        lastColumn = t.Item2;
        next.Focus();
        next.BringIntoView();
        // BringIntoView stops at the box's edge: the first and last rows and columns scroll on to the body's ends (the last into
        // the room kept for the scroll bars), so a column's label and a row's tint show whole.
        if (t.Item1 == last && last >= 2) bodyScroll.ScrollToBottom();
        else if (t.Item1 == 2) bodyScroll.ScrollToTop();
        if (t.Item2 == width - 1) bodyScroll.ScrollToRightEnd();
        else if (t.Item2 == 1) bodyScroll.ScrollToLeftEnd();
        return true;

        (int, int)? Step(int dr, int dc)
        {
            for (int r = row + dr, c = column + dc; r >= 0 && r <= last && c >= 0 && c < width; r += dr, c += dc)
                if (map[r][c] is { } box && box != focused) return (r, c);
            return null;
        }
        (int, int)? FirstIn(int r, int from)
        {
            if (r > last) return null;
            for (int c = from; c < width; c++) if (map[r][c] != null) return (r, c);
            return null;
        }
        (int, int)? LastIn(int r)
        {
            for (int c = width - 1; c >= 0; c--) if (map[r][c] != null) return (r, c);
            return null;
        }
        // The same column in the first or last mission (the nearest mission that builds it).
        (int, int)? Vertical(int start, int direction)
        {
            for (int r = start; r >= 2 && r <= last; r += direction) if (map[r][column] != null) return (r, column);
            return null;
        }
    }
}

/// <summary>
/// The Shared files row: its box in the grid's row-header column (at least the box and the files' own spacing after it),
/// then the files every mission uses on one line. The header column narrows (down to that least width) before the files
/// take more lines, so the window's default size shows every file. One tab stop; Left/Right/Home/End move inside.
/// </summary>
internal sealed class SourceExportSharedRow : Grid
{
    private readonly List<CheckBox> boxes = [];
    private readonly ColumnDefinition headerColumn = new() { Width = new(112) };
    private readonly SourceExportCheckBox shared;
    private readonly SourceExportFlow items = new();
    private double preferred = 112;

    internal SourceExportSharedRow(SourceExportSelection model)
    {
        Focusable = false;
        AutomationProperties.SetName(this, "Shared files"); AutomationProperties.SetAutomationId(this, "SharedFiles");
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Contained);
        ColumnDefinitions.Add(headerColumn); ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        string[] words = ["no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
        int count = model.SharedFiles.Count;
        shared = Box(model.Shared!, "Shared files", $"Select or clear the {(count < words.Length ? words[count] : count.ToString("N0"))} files every mission uses");
        shared.Content = "Shared files"; shared.Margin = new(6, 0, 0, 0); shared.HorizontalAlignment = HorizontalAlignment.Left; shared.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(shared); boxes.Add(shared);
        foreach (var file in model.SharedFiles)
        {
            var box = Box(file, file.Path, file.Description);
            box.ToolTip = $"{file.Path}\n{file.Description}";
            box.Content = file.Path.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase) ? file.Path[..^4] : file.Path;
            items.Children.Add(box); boxes.Add(box);
        }
        SetColumn(items, 1); Children.Add(items);
        SourceExportRoving.Attach(this, boxes, shared);
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is not CheckBox focused || boxes.IndexOf(focused) is not (int i and >= 0)) return;
            int? target = e.Key switch { Key.Left => i - 1, Key.Right => i + 1, Key.Home => 0, Key.End => boxes.Count - 1, Key.Up or Key.Down => i, _ => null };
            if (target is not { } t) return;
            e.Handled = true;
            boxes[Math.Clamp(t, 0, boxes.Count - 1)].Focus();
        };
    }
    private static SourceExportCheckBox Box(object source, string name, string help)
    {
        SourceExportCheckBox box = new() { ToolTip = help };
        box.SetResourceReference(StyleProperty, "DefaultCheckBoxStyle");
        SourceExportSelectionDialog.Labeled(box);
        box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("IsChecked") { Source = source, Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(box, name); AutomationProperties.SetHelpText(box, help);
        return box;
    }
    internal void SetHeaderWidth(double width) { if (preferred == width) return; preferred = width; InvalidateMeasure(); }
    protected override Size MeasureOverride(Size available)
    {
        // The grid's row-header width, but never closer to the first file than the files are to each other (the grid's
        // narrowest row header is a few DIP wider than the box alone).
        shared.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double least = shared.DesiredSize.Width + SourceExportFlow.Spacing, header = Math.Max(preferred, least);
        if (!double.IsInfinity(available.Width))
        {
            double line = items.LineWidth();
            if (available.Width - header < line) header = Math.Max(least, Math.Min(header, available.Width - line));
        }
        if (headerColumn.Width.Value != header) headerColumn.Width = new(header);
        return base.MeasureOverride(available);
    }
}

/// <summary>Items on one line 36 DIP tall, or in the fewest lines of equal columns that fit (two lines of three for six).</summary>
internal sealed class SourceExportFlow : Panel
{
    private const double LineHeight = 36;
    /// <summary>The space between two files, and after the Shared files box when the row narrows.</summary>
    internal const double Spacing = 14;
    private int perLine = 1;
    private double[] widths = [];
    /// <summary>The width the items take on one line.</summary>
    internal double LineWidth()
    {
        double total = 0;
        foreach (UIElement child in InternalChildren) { child.Measure(new Size(double.PositiveInfinity, LineHeight)); total += child.DesiredSize.Width; }
        return total + Spacing * Math.Max(0, InternalChildren.Count - 1);
    }
    protected override Size MeasureOverride(Size available)
    {
        int n = InternalChildren.Count;
        if (n == 0) return default;
        foreach (UIElement child in InternalChildren) child.Measure(new Size(double.PositiveInfinity, LineHeight));
        for (int lines = 1; lines <= n; lines++)
        {
            perLine = (n + lines - 1) / lines;
            widths = new double[perLine];
            for (int i = 0; i < n; i++) widths[i % perLine] = Math.Max(widths[i % perLine], InternalChildren[i].DesiredSize.Width);
            double total = widths.Sum() + Spacing * (perLine - 1);
            if (total <= available.Width || perLine == 1) return new Size(Math.Min(total, double.IsInfinity(available.Width) ? total : available.Width), LineHeight * lines);
        }
        return default;
    }
    protected override Size ArrangeOverride(Size final)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int line = i / perLine, column = i % perLine;
            double x = widths.Take(column).Sum() + Spacing * column;
            var child = InternalChildren[i];
            child.Arrange(new Rect(x, line * LineHeight + (LineHeight - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
        }
        return final;
    }
}

/// <summary>
/// Roving focus: a group of boxes is one tab stop, the box focused last (WPF keeps TabOnceActiveElement internal), so Tab
/// leaves the group and returns to where it was.
/// </summary>
internal static class SourceExportRoving
{
    internal static void Attach(UIElement group, IReadOnlyList<CheckBox> boxes, CheckBox? first)
    {
        foreach (var box in boxes) box.IsTabStop = box == first;
        group.GotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is not CheckBox focused || !boxes.Contains(focused) || focused.IsTabStop) return;
            foreach (var box in boxes) box.IsTabStop = box == focused;
        };
    }
}
