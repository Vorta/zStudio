using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;

namespace zStudio.Desktop;

internal sealed partial class SceneInspectionCard
{
    /// <summary>The hover readout's height: source worlds show no origin row.</summary>
    /// <summary>The probe strip's height: one line, and an origin row below it outside source worlds.</summary>
    private double HoverHeight => IsSourceWorld ? 37 : 61;
    private const double BackdropBlur = 24;
    private Border hoverBox = null!;
    /// <summary>The 3D view behind the panel, blurred, under an 80% tint: an acrylic look over the live scene.</summary>
    private readonly VisualBrush backdrop = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    private const double ResizeGripHeight = 8;
    private readonly Thumb resizeGrip = new() { Name = "InspectionResize", Height = ResizeGripHeight,
        Cursor = Cursors.SizeNS, Focusable = true, ToolTip = "Resize inspection panel · drag or use Up/Down" };
    private double preferredPanelHeight = WorkspaceLayout.DefaultInspectionPanelHeight, resizeStartHeight;
    private bool resizingPanel;
    internal event Action<double>? PanelHeightChanged;
    private bool PanelExpanded => card.Visibility == Visibility.Visible;
    private double MaximumPanelHeight => Math.Max(0, Math.Min(ActualHeight * .8, ActualHeight - 20));
    private double MinimumPanelHeight => Math.Min(WorkspaceLayout.MinimumInspectionPanelHeight, MaximumPanelHeight);
    private double ExpandedPanelHeight => Math.Clamp(preferredPanelHeight, MinimumPanelHeight, MaximumPanelHeight);
    internal object DescribePanel() => new { preferredHeight = preferredPanelHeight,
        effectiveHeight = Docked ? panel.ActualHeight : PanelExpanded ? ExpandedPanelHeight : Math.Min(HoverHeight + 2, MaximumPanelHeight),
        minimumHeight = MinimumPanelHeight, maximumHeight = MaximumPanelHeight, expanded = PanelExpanded, docked = Docked };
    internal void SetPanelHeight(double height)
    {
        // Explicit layout requests supersede a gesture, including a request for
        // the current effective height. Its later canceled event must not undo
        // the newer preference. Automatic viewport clamping never calls here.
        resizingPanel = false;
        if (resizeGrip.IsDragging) resizeGrip.CancelDrag();
        ApplyPanelHeight(height);
    }
    private void ApplyPanelHeight(double height)
    {
        preferredPanelHeight = WorkspaceLayout.NormalizeInspectionPanelHeight(height);
        ArrangePanel();
    }
    private void ResizePanel(double height)
    {
        ApplyPanelHeight(Math.Clamp(height, MinimumPanelHeight, MaximumPanelHeight));
        PanelHeightChanged?.Invoke(preferredPanelHeight);
    }
    private readonly TextBlock hoverName = new() { Name = "InspectionHoverName", FontWeight = FontWeights.SemiBold,
        FontSize = 12, Height = 20, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock[] hoverSurface = HoverComponents("InspectionSurface"), hoverOrigin = HoverComponents("InspectionOrigin");

    private static TextBlock[] HoverComponents(string name) => Enumerable.Range(0, 3).Select(i => new TextBlock
    { Name = name + "XYZ"[i], FontSize = 12, Height = 18, Margin = new(0, 0, i == 2 ? 0 : 10, 0) }).ToArray();

    private Border CreatePanel()
    {
        StackPanel readout = new();
        // One line: the probe icon, the pointed object and the surface coordinates at the right.
        DockPanel probe = new() { Height = 20, ToolTip = "Surface world XYZ" };
        var probeIcon = new PreviewIcon { Kind = "Probe", Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        probeIcon.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel surface = new() { Orientation = Orientation.Horizontal };
        foreach (var component in hoverSurface) surface.Children.Add(component);
        Viewbox surfaceFit = new() { Child = surface, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = 210, Margin = new(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center };
        hoverName.Height = double.NaN; hoverName.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(probeIcon, Dock.Left); DockPanel.SetDock(surfaceFit, Dock.Right);
        probe.Children.Add(probeIcon); probe.Children.Add(surfaceFit); probe.Children.Add(hoverName);
        readout.Children.Add(probe);
        AddVector("Origin", "Object origin XYZ", hoverOrigin);
        // The probe strip is the theme's base background (a shade darker than the card in dark themes), also at 80%.
        System.Windows.Shapes.Rectangle stripTint = new() { Opacity = .8, IsHitTestVisible = false };
        stripTint.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SolidBackgroundFillColorBaseBrush");
        readout.Margin = new(14, 8, 14, 8);
        Grid strip = new(); strip.Children.Add(stripTint); strip.Children.Add(readout);
        hoverBox = Box(strip); hoverBox.Name = "InspectionHover"; hoverBox.Height = HoverHeight; hoverBox.Padding = new(0);
        hoverBox.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush"); hoverBox.BorderThickness = new(0, 0, 0, 1);
        contents = new(); contents.RowDefinitions.Add(new() { Height = GridLength.Auto });
        contents.RowDefinitions.Add(new() { Height = GridLength.Auto });
        contents.RowDefinitions.Add(new() { Height = GridLength.Auto });
        contents.Children.Add(hoverBox); SetRow(card, 1); contents.Children.Add(card);
        ConfigureResizeGrip(); SetRow(resizeGrip, 2); contents.Children.Add(resizeGrip);
        // Acrylic: the blurred scene behind the panel under the theme background at 80% opacity, clipped to the rounded frame.
        blur = new() { Fill = backdrop, Margin = new(-BackdropBlur), IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = BackdropBlur, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance } };
        tint = new() { Opacity = .8, IsHitTestVisible = false };
        tint.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SolidBackgroundFillColorQuarternaryBrush");
        Grid shell = new(); shell.Children.Add(blur); shell.Children.Add(tint); shell.Children.Add(contents);
        Border result = new() { Name = "InspectionPanel", Child = shell, CornerRadius = new(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x75, 0x75, 0x75)), BorderThickness = new(1), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new(10), ClipToBounds = true };
        shell.SizeChanged += (_, e) => shell.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        backdrop.Visual = viewport.RenderSurface;
        result.LayoutUpdated += (_, _) => AlignBackdrop(result);
        // Let native fields/buttons/scrolling run first; unused panel space and
        // scroll limits still consume input instead of acting on the scene behind.
        result.MouseWheel += (_, e) => e.Handled = true;
        result.MouseDown += (_, e) => e.Handled = true;
        return result;

        void AddVector(string label, string tooltip, TextBlock[] components)
        {
            Grid row = new() { Height = 24, ToolTip = tooltip, Margin = new(22, 0, 0, 0) };
            row.ColumnDefinitions.Add(new() { Width = new(46) });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, Opacity = .75, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            StackPanel coordinates = new() { Orientation = Orientation.Horizontal };
            foreach (var component in components) coordinates.Children.Add(component);
            // Keep full round-trip numbers visible even in a constrained panel;
            // ordinary widths retain the normal font size without stretching.
            Viewbox fit = new() { Child = coordinates, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(fit, 1); row.Children.Add(fit);
            readout.Children.Add(row);
        }
    }

    /// <summary>Shows the part of the 3D view under the panel (and a blur margin around it) in the backdrop.</summary>
    private void AlignBackdrop(Border frame)
    {
        if (!frame.IsVisible || PresentationSource.FromVisual(frame) == null || PresentationSource.FromVisual(viewport.RenderSurface) == null) return;
        Rect area;
        try { area = new Rect(frame.TranslatePoint(new Point(-BackdropBlur, -BackdropBlur), viewport.RenderSurface), new Size(frame.ActualWidth + 2 * BackdropBlur, frame.ActualHeight + 2 * BackdropBlur)); }
        catch (InvalidOperationException) { return; }
        if (backdrop.Viewbox != area) backdrop.Viewbox = area;
    }

    private void ConfigureResizeGrip()
    {
        AutomationProperties.SetName(resizeGrip, "Resize inspection panel");
        FrameworkElementFactory hit = new(typeof(Border)); hit.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        FrameworkElementFactory mark = new(typeof(Border));
        mark.SetValue(WidthProperty, 32d); mark.SetValue(HeightProperty, 2d); mark.SetValue(Border.CornerRadiusProperty, new CornerRadius(1));
        mark.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); mark.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        mark.SetValue(OpacityProperty, .5d);
        mark.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new(RelativeSourceMode.TemplatedParent) });
        hit.AppendChild(mark); resizeGrip.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = hit };
        resizeGrip.DragStarted += (_, e) => { resizeStartHeight = preferredPanelHeight; resizingPanel = true; e.Handled = true; };
        resizeGrip.DragDelta += (_, e) => { if (resizingPanel) ResizePanel(ExpandedPanelHeight + e.VerticalChange); e.Handled = true; };
        resizeGrip.DragCompleted += (_, e) =>
        {
            bool restore = resizingPanel && e.Canceled;
            resizingPanel = false; e.Handled = true;
            if (restore) { ApplyPanelHeight(resizeStartHeight); PanelHeightChanged?.Invoke(preferredPanelHeight); }
        };
        resizeGrip.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && resizeGrip.IsDragging) { resizeGrip.CancelDrag(); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down or Key.Home or Key.End)
            {
                ResizePanel(e.Key switch { Key.Home => MinimumPanelHeight, Key.End => MaximumPanelHeight,
                    Key.Up => ExpandedPanelHeight - 10, _ => ExpandedPanelHeight + 10 });
                e.Handled = true;
            }
        };
        Unloaded += (_, _) => { if (resizeGrip.IsDragging) resizeGrip.CancelDrag(); };
    }

    private void RefreshHover()
    {
        var hovered = viewport.HoverInspection;
        hoverName.Text = hovered == null ? "Point at geometry or an AI node" : Display(describe(hovered)["Node"]);
        hoverName.ToolTip = hoverName.Text;
        if (hoverOrigin[0].Parent is System.Windows.Controls.Panel coordinates && coordinates.Parent is Viewbox box && box.Parent is Grid originRow) originRow.Visibility = IsSourceWorld ? Visibility.Collapsed : Visibility.Visible;
        SetVector(hoverSurface, hovered?.Surface); SetVector(hoverOrigin, hovered?.Origin);
        if (Docked)
        {
            BuildChips();
            surfaceName.Text = hovered == null ? "—" : hoverName.Text.Split(" · ")[0]; surfaceName.ToolTip = hoverName.Text;
            for (int i = 0; i < 3; i++)
            {
                string number = hovered?.Surface is { } at ? at[i].ToString("0.00", CultureInfo.InvariantCulture) : "—";
                if (surfaceAxes[i].Tag as string == number) continue;
                surfaceAxes[i].Tag = number; surfaceAxes[i].Inlines.Clear();
                surfaceAxes[i].Inlines.Add(new System.Windows.Documents.Run("XYZ"[i] + " ") { Foreground = FieldEditor.AxisBrushes[i], FontWeight = FontWeights.SemiBold });
                surfaceAxes[i].Inlines.Add(new System.Windows.Documents.Run(number));
            }
        }

        static void SetVector(TextBlock[] components, Vector3? value)
        {
            for (int i = 0; i < components.Length; i++)
            {
                string number = value?[i].ToString("R", CultureInfo.InvariantCulture) ?? "—", text = "XYZ"[i] + " " + number;
                if (components[i].Text == text) continue;
                // The axis letter is coloured like the 3D handles.
                components[i].Inlines.Clear();
                components[i].Inlines.Add(new System.Windows.Documents.Run("XYZ"[i].ToString()) { Foreground = FieldEditor.AxisBrushes[i], FontWeight = FontWeights.SemiBold });
                components[i].Inlines.Add(new System.Windows.Documents.Run(" " + number));
                components[i].ToolTip = text;
            }
        }
    }

    private Grid contents = null!;
    private System.Windows.Shapes.Rectangle blur = null!, tint = null!;
    private readonly TextBlock surfaceName = new() { FontWeight = FontWeights.SemiBold, Margin = new(4, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock[] surfaceAxes = Enumerable.Range(0, 3).Select(i => new TextBlock { Margin = new(0, 0, i == 2 ? 0 : 10, 0), VerticalAlignment = VerticalAlignment.Center }).ToArray();
    private readonly PreviewIcon selectionGlyph = new() { Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock selectionName = new() { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock selectionMode = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 0, 0) };
    private bool chipsBuilt;

    /// <summary>
    /// Docks the panel (a source map's Properties pane, hosted by the window) or floats it over the view again. Docked, the
    /// panel stretches, keeps no acrylic, hover strip or resize grip, and the selection and surface readouts become chips.
    /// </summary>
    internal void SetDocked(bool docked)
    {
        if (Docked == docked) return;
        Docked = docked;
        if (docked) Children.Remove(panel);
        else if (panel.Parent == null) Children.Insert(0, panel);
        blur.Visibility = tint.Visibility = docked ? Visibility.Collapsed : Visibility.Visible;
        panel.CornerRadius = new(docked ? 0 : 8); panel.BorderThickness = new(docked ? 0 : 1); panel.Margin = new(docked ? 0 : 10);
        panel.HorizontalAlignment = docked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        panel.VerticalAlignment = docked ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        contents.RowDefinitions[1].Height = docked ? new(1, GridUnitType.Star) : GridLength.Auto;
        card.Padding = docked ? new(12, 12, 8, 10) : new(12, 12, 6, 10);
        viewport.NavigationCubeTopRight = docked; viewport.ShowAxes = !docked;
        Refresh();
    }
    private void BuildChips()
    {
        if (chipsBuilt) return;
        chipsBuilt = true;
        StackPanel surface = new() { Orientation = Orientation.Horizontal };
        var probe = new PreviewIcon { Kind = "Probe", Width = 13, Height = 13, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        probe.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
        TextBlock label = new() { Text = "Surface", VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        surface.Children.Add(probe); surface.Children.Add(label); surface.Children.Add(surfaceName);
        foreach (var axis in surfaceAxes) surface.Children.Add(axis);
        surfaceChip.Content = surface;
        StackPanel selection = new() { Orientation = Orientation.Horizontal };
        selectionMode.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        selection.Children.Add(selectionGlyph); selection.Children.Add(selectionName); selection.Children.Add(selectionMode);
        selectionChip.Content = selection;
    }
    /// <summary>Docked: the selected object (and the shown gizmo) at the view's lower left, the surface under the cursor at its lower right.</summary>
    private void RefreshChips(zStudio.Rendering.SceneInspection? selected)
    {
        surfaceChip.Visibility = Docked ? Visibility.Visible : Visibility.Collapsed;
        selectionChip.Visibility = Docked && selected != null ? Visibility.Visible : Visibility.Collapsed;
        if (!Docked) return;
        BuildChips();
        if (selected == null) return;
        string category = CategoryOf?.Invoke(selected) ?? "object";
        selectionGlyph.Kind = OutlinerKinds.Icon(category); selectionGlyph.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, OutlinerKinds.Brush(category));
        selectionMode.Text = handlesShown ? $"· {(transformMode == "rotate" ? "Rotate" : "Move")} gizmo" : "";
    }
    /// <summary>The empty docked pane: a hint, the recently selected objects and a summary of the world.</summary>
    private string? emptySignature;
    private void RefreshEmptyView()
    {
        var recentNow = RecentSelections?.Invoke() ?? [];
        var summaryNow = WorldSummary?.Invoke() ?? [];
        string signature = string.Join("\u0001", recentNow.Select(r => r.Name + "\u0002" + r.Parent + "\u0002" + r.Category + "\u0002" + string.Join("/", r.Path)))
            + "\u0003" + string.Join("\u0001", summaryNow.Select(s => s.Label + "\u0002" + s.Value));
        if (signature == emptySignature && emptyBody.Children.Count > 0) return;
        emptySignature = signature;
        emptyBody.Children.Clear();
        StackPanel hero = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new(20, 22, 20, 18) };
        Grid art = new() { Width = 56, Height = 56, Margin = new(0, 0, 0, 14) };
        System.Windows.Shapes.Rectangle outline = new() { RadiusX = 12, RadiusY = 12, StrokeThickness = 1.5, StrokeDashArray = [4, 3] };
        outline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextFillColorDisabledBrush");
        var pointer = new PreviewIcon { Kind = "Pointer", Width = 24, Height = 24 };
        pointer.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
        art.Children.Add(outline); art.Children.Add(pointer);
        hero.Children.Add(art);
        hero.Children.Add(new TextBlock { Text = "Nothing selected", FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        TextBlock hint = new() { Text = "Click an object in the viewport or a node in the World tree to edit its properties.", FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, MaxWidth = 250, Margin = new(0, 6, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        hero.Children.Add(hint);
        emptyBody.Children.Add(hero);
        var recent = recentNow;
        if (recent.Count > 0)
        {
            var (section, rows) = EmptySection("Recently selected");
            foreach (var entry in recent)
            {
                DockPanel line = new() { Height = 34 };
                TextBlock parent = new() { Text = entry.Parent, FontSize = 11, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
                parent.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                DockPanel.SetDock(parent, Dock.Right); line.Children.Add(parent);
                var icon = new PreviewIcon { Kind = OutlinerKinds.Icon(entry.Category), Width = 15, Height = 15, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                icon.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, OutlinerKinds.Brush(entry.Category));
                line.Children.Add(icon);
                line.Children.Add(new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                Button pick = new() { Content = line, Width = double.NaN, Height = 34, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(8, 0, 8, 0), Margin = new(0) };
                pick.SetResourceReference(StyleProperty, "CommandButton");
                pick.Width = double.NaN; pick.HorizontalAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(pick, "Select " + entry.Name + " again");
                pick.Click += (_, _) => PickRecent?.Invoke(entry);
                rows.Children.Add(pick);
            }
            emptyBody.Children.Add(section);
        }
        if (summaryNow is { Count: > 0 } summary)
        {
            var (section, rows) = EmptySection("World");
            Grid table = new() { Margin = new(8, 0, 8, 6) };
            table.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); table.ColumnDefinitions.Add(new());
            for (int i = 0; i < summary.Count; i++)
            {
                table.RowDefinitions.Add(new() { Height = GridLength.Auto });
                TextBlock key = new() { Text = summary[i].Label, FontSize = 12.5, Margin = new(0, 3, 12, 3) };
                key.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                TextBlock value = new() { Text = summary[i].Value, FontSize = 12.5, Margin = new(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
                SetRow(key, i); SetRow(value, i); SetColumn(value, 1);
                table.Children.Add(key); table.Children.Add(value);
            }
            rows.Children.Add(table);
            emptyBody.Children.Add(section);
        }
        static (Border Section, StackPanel Rows) EmptySection(string title)
        {
            StackPanel rows = new() { Margin = new(4, 0, 4, 6) };
            rows.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new(8, 10, 8, 4) });
            Border section = new() { Child = rows, CornerRadius = new(6), BorderThickness = new(1), Margin = new(0, 0, 0, 6) };
            section.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            section.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            return (section, rows);
        }
    }

    private void ArrangePanel()
    {
        if (Docked)
        {
            panel.Width = double.NaN; panel.MaxHeight = double.PositiveInfinity;
            hoverBox.Visibility = Visibility.Collapsed; resizeGrip.Visibility = Visibility.Collapsed;
            card.Height = double.NaN;
            viewport.ReserveInspectionPanel(Rect.Empty);
            return;
        }
        hoverBox.Visibility = Visibility.Visible;
        panel.Width = Math.Max(0, Math.Min(350, ActualWidth - 20));
        double height = PanelExpanded ? ExpandedPanelHeight : Math.Min(HoverHeight + 2, MaximumPanelHeight);
        var cube = viewport.NavigationCubeHomeBounds;
        // Reserve enough side space for the cube whenever it could overlap the
        // expanded panel. Selection/collapse does not change the panel width.
        if (!cube.IsEmpty && cube.Right > ActualWidth - 10 - panel.Width && cube.Top < 20 + MaximumPanelHeight)
        {
            panel.Width = Math.Max(0, Math.Min(panel.Width, ActualWidth - 30 - Math.Min(cube.Width, 60)));
        }
        panel.MaxHeight = MaximumPanelHeight;
        hoverBox.Height = HoverHeight; // Source worlds learn they are one after construction.
        resizeGrip.Visibility = PanelExpanded ? Visibility.Visible : Visibility.Collapsed;
        card.Height = Math.Max(0, ExpandedPanelHeight - HoverHeight - 2 - ResizeGripHeight);
        viewport.ReserveInspectionPanel(new Rect(Math.Max(0, ActualWidth - 10 - panel.Width), 10, panel.Width, height));
    }
}
