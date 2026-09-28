using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;

namespace Recoil.Zbd.Desktop;

internal sealed partial class SceneInspectionCard
{
    private const double HoverHeight = 90;
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
        effectiveHeight = PanelExpanded ? ExpandedPanelHeight : Math.Min(HoverHeight + 2, MaximumPanelHeight),
        minimumHeight = MinimumPanelHeight, maximumHeight = MaximumPanelHeight, expanded = PanelExpanded };
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
        StackPanel readout = new(); readout.Children.Add(hoverName);
        AddVector("Surface", "Surface world XYZ", hoverSurface); AddVector("Origin", "Object origin XYZ", hoverOrigin);
        var hoverBox = Box(readout); hoverBox.Name = "InspectionHover"; hoverBox.Height = HoverHeight;
        Grid contents = new(); contents.RowDefinitions.Add(new() { Height = GridLength.Auto });
        contents.RowDefinitions.Add(new() { Height = GridLength.Auto });
        contents.RowDefinitions.Add(new() { Height = GridLength.Auto });
        contents.Children.Add(hoverBox); SetRow(card, 1); contents.Children.Add(card);
        ConfigureResizeGrip(); SetRow(resizeGrip, 2); contents.Children.Add(resizeGrip);
        Border result = new() { Name = "InspectionPanel", Child = contents, CornerRadius = new(6),
            BorderBrush = card.BorderBrush, BorderThickness = new(1), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new(10), ClipToBounds = true };
        result.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        // Let native fields/buttons/scrolling run first; unused panel space and
        // scroll limits still consume input instead of acting on the scene behind.
        result.MouseWheel += (_, e) => e.Handled = true;
        result.MouseDown += (_, e) => e.Handled = true;
        return result;

        void AddVector(string label, string tooltip, TextBlock[] components)
        {
            Grid row = new() { Height = 24, ToolTip = tooltip };
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
        SetVector(hoverSurface, hovered?.Surface); SetVector(hoverOrigin, hovered?.Origin);

        static void SetVector(TextBlock[] components, Vector3? value)
        {
            for (int i = 0; i < components.Length; i++)
            {
                string text = "XYZ"[i] + " " + (value?[i].ToString("R", CultureInfo.InvariantCulture) ?? "—");
                components[i].Text = text; components[i].ToolTip = text;
            }
        }
    }

    private void ArrangePanel()
    {
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
        resizeGrip.Visibility = PanelExpanded ? Visibility.Visible : Visibility.Collapsed;
        card.Height = Math.Max(0, ExpandedPanelHeight - HoverHeight - 2 - ResizeGripHeight);
        viewport.ReserveInspectionPanel(new Rect(Math.Max(0, ActualWidth - 10 - panel.Width), 10, panel.Width, height));
    }
}
