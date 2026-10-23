using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>One recycled native row: accessible controls on the left, bounded time-window drawing on the right.</summary>
public sealed class WorldAnimationTimelineRowView : Grid
{
    public static readonly DependencyProperty OwnerProperty = DependencyProperty.Register(nameof(Owner), typeof(WorldAnimationTimeline), typeof(WorldAnimationTimelineRowView), new PropertyMetadata(null, OwnerChanged));
    private readonly ColumnDefinition labels = new();
    private readonly Button expand = SmallButton("▸"), eye = SmallButton("◉"), occurrences = SmallButton("⋯");
    private readonly CheckBox include = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(1, 0, 3, 0), MinWidth = 16 };
    private readonly TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock timing = new() { FontSize = 10, Opacity = .72, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel controls = new() { Orientation = Orientation.Horizontal };
    private readonly WorldAnimationRowPlot plot;
    private WorldAnimationTimelineRow? row;
    private bool changing;
    public WorldAnimationTimeline? Owner { get => (WorldAnimationTimeline?)GetValue(OwnerProperty); set => SetValue(OwnerProperty, value); }

    public WorldAnimationTimelineRowView()
    {
        ClipToBounds = true; Height = 28; ColumnDefinitions.Add(labels); ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var left = new DockPanel(); controls.Children.Add(expand); controls.Children.Add(include); controls.Children.Add(eye); DockPanel.SetDock(controls, Dock.Left); left.Children.Add(controls);
        DockPanel.SetDock(occurrences, Dock.Right); left.Children.Add(occurrences);
        var text = new Grid(); text.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); text.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MaxWidth = 85 });
        text.Children.Add(label); Grid.SetColumn(timing, 1); text.Children.Add(timing); left.Children.Add(text); Children.Add(left);
        plot = new(this); Grid.SetColumn(plot, 1); Children.Add(plot);
        DataContextChanged += (_, _) => { row = DataContext as WorldAnimationTimelineRow; Refresh(); };
        Loaded += (_, _) => { if (Owner != null) { Owner.ViewChanged -= Refresh; Owner.ViewChanged += Refresh; } Refresh(); };
        Unloaded += (_, _) => { if (Owner != null) Owner.ViewChanged -= Refresh; };
        expand.Click += (_, _) => { if (row != null) Owner?.Expand(row, !row.Expanded); };
        include.Click += (_, _) => { if (!changing && row != null) Owner?.Include(row, include.IsChecked == true); };
        eye.Click += (_, _) => { if (row != null) Owner?.ShowGuides(row, row.GuidesShown != true); };
        occurrences.Click += (_, _) => ShowOccurrences(0);
    }
    private static Button SmallButton(string value) => new() { Content = value, Padding = new(1, 0, 1, 0), MinWidth = 20, MinHeight = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new(0) };
    private static void OwnerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (WorldAnimationTimelineRowView)d;
        if (e.OldValue is WorldAnimationTimeline old) old.ViewChanged -= control.Refresh;
        if (control.IsLoaded && e.NewValue is WorldAnimationTimeline next) next.ViewChanged += control.Refresh;
        control.Refresh();
    }
    private void Refresh()
    {
        if (row == null || Owner == null) return;
        changing = true;
        try
        {
            labels.Width = new(Owner.LabelWidth); Height = row.IsGroup ? 32 : 28;
            controls.Margin = new(Math.Min(48, Math.Max(0, row.Depth) * 12), 0, 0, 0);
            expand.Content = row.Expanded ? "▾" : "▸"; expand.Visibility = row.Expandable ? Visibility.Visible : Visibility.Hidden;
            include.Visibility = row.Included.HasValue ? Visibility.Visible : Visibility.Collapsed; include.IsChecked = row.Included; include.IsEnabled = row.InclusionEnabled;
            include.ToolTip = row.InclusionEnabled ? "Include this call only; descendants keep their own settings" : row.InclusionReason;
            ToolTipService.SetShowOnDisabled(include, true);
            eye.Visibility = row.GuidesShown.HasValue ? Visibility.Visible : Visibility.Collapsed; eye.Opacity = row.GuidesShown == true ? 1 : .45; eye.ToolTip = "Show guides; this does not evaluate or include a child";
            occurrences.Visibility = row.Marks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            label.Text = WorldAnimationTimeline.Shown(row.Label); label.FontWeight = row.IsGroup ? FontWeights.SemiBold : FontWeights.Normal;
            label.ToolTip = WorldAnimationTimeline.Shown(row.Label) + (row.Detail.Length > 0 ? "\n" + WorldAnimationTimeline.Shown(row.Detail) : "");
            timing.Text = WorldAnimationTimeline.Shown(row.TimingLabel); timing.ToolTip = row.TimingLabel;
            // Source thresholds describe eligibility, not a position on the global ruler. Give
            // them prose space in the empty canvas and keep the label column for event identity.
            timing.Visibility = Owner.ViewMode == "source" && row.Marks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(this, WorldAnimationTimeline.Shown(row.Label + ". " + row.TimingLabel + ". " + row.Detail));
            AutomationProperties.SetName(expand, (row.Expanded ? "Collapse " : "Expand ") + row.Label);
            AutomationProperties.SetName(include, "Include child call " + row.Label); AutomationProperties.SetName(eye, "Show guides for " + row.Label);
            AutomationProperties.SetName(occurrences, "Occurrences for " + row.Label);
            plot.ToolTip = row.IsWindowClock ? row.CanMoveWindow ? "Flight sequence clock. Drag the start grip to propose a verified window move." : "Flight sequence clock. " + row.WindowMoveReason : row.TimingLabel + "\n" + row.Detail;
            plot.InvalidateVisual();
        }
        finally { changing = false; }
    }
    internal WorldAnimationTimelineRow? Row => row;
    private void ShowOccurrences(int offset, string? rowId = null, string? evidence = null, string? projection = null)
    {
        if (row == null || Owner == null) return;
        var owner = Owner; rowId ??= row.Id; evidence ??= owner.State.EvidenceIdentity; projection ??= owner.ViewMode;
        if (owner.State.EvidenceIdentity != evidence || owner.ViewMode != projection || owner.CurrentRow(rowId) is not { } captured) return;
        var menu = new ContextMenu();
        if (offset > 0) AddPage("Previous 100 occurrences", Math.Max(0, offset - 100));
        foreach (var mark in captured.Marks.Skip(offset).Take(100))
        {
            var item = new MenuItem { Header = WorldAnimationTimeline.Shown($"{mark.Start:0.######} s · {mark.Kind} · {mark.Label}"), ToolTip = mark.Label };
            item.Click += (_, _) => owner.SelectCurrentMark(captured.Id, mark.Id, evidence, projection); menu.Items.Add(item);
        }
        if (offset + 100 < captured.Marks.Count) AddPage("Next 100 occurrences", offset + 100);
        menu.PlacementTarget = occurrences; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
        void AddPage(string title, int next) { var item = new MenuItem { Header = title }; item.Click += (_, _) => Dispatcher.BeginInvoke(() => ShowOccurrences(next, captured.Id, evidence, projection)); menu.Items.Add(item); }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}

internal sealed class WorldAnimationRowPlot(WorldAnimationTimelineRowView container) : FrameworkElement
{
    private Point pressed;
    private bool draggingPlayhead, draggingWindow;
    private double? proposedWindow;
    private WorldAnimationTimelineMark? windowMark;
    private string? gestureRowId;
    private double windowThreshold;
    internal WorldAnimationTimeline? Timeline => container.Owner;
    internal WorldAnimationTimelineRow? Row => container.Row;
    private bool IsNativeClock => Row?.IsWindowClock == true || Row?.NativeClockEnd.HasValue == true;
    private double Begin => IsNativeClock ? Row?.NativeClockStart ?? 0 : Timeline?.VisibleStart ?? 0;
    private double End => IsNativeClock ? Math.Max(Begin + WorldAnimationTimeline.Step, Row?.NativeClockEnd ?? Row?.WindowClockEnd ?? 30) : Timeline?.VisibleEnd ?? 30;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e); Focusable = true; ClipToBounds = true;
        AutomationProperties.SetName(this, "Event observations; click selects, ruler or playhead drag seeks");
    }
    private double X(double seconds) => (seconds - Begin) / Math.Max(WorldAnimationTimeline.Step, End - Begin) * ActualWidth;
    private double At(double x) => Begin + Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * (End - Begin);
    internal IEnumerable<WorldAnimationTimelineMark> VisibleMarks => (Row?.Marks ?? []).Where(m => double.IsFinite(m.Start) && m.Start <= End && (m.End ?? Math.Max(m.Start, Timeline?.State.Time ?? 0)) >= Begin);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (Timeline is not { } owner || Row is not { } row) return;
        Brush ink = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : owner.Foreground;
        Brush accent = SystemParameters.HighContrast ? SystemColors.HighlightBrush : (TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.SteelBlue);
        dc.DrawRectangle(Brushes.Transparent, null, new(0, 0, ActualWidth, ActualHeight));
        dc.DrawLine(new Pen(ink, .2), new(0, ActualHeight - 1), new(ActualWidth, ActualHeight - 1));
        WorldAnimationTimelineDrawing.Grid(dc, Begin, End, ActualWidth, ActualHeight, ink, !IsNativeClock && owner.ShowTicks, IsNativeClock, this);
        if (owner.ViewMode == "source" && row.Marks.Count == 0 && row.TimingLabel.Length > 0 && ActualWidth > 14)
            WorldAnimationTimelineDrawing.Text(dc, row.TimingLabel, 7, (ActualHeight - 14) / 2, ink, this, ActualWidth - 14);
        var buckets = new Dictionary<int, int>();
        foreach (var mark in VisibleMarks)
        {
            double x = Math.Clamp(X(mark.Start), -8, ActualWidth + 8);
            int bucket = (int)Math.Floor(Math.Clamp(x, 0, Math.Max(0, ActualWidth)) / 7);
            int lane = buckets.GetValueOrDefault(bucket); buckets[bucket] = lane + 1;
            if (lane >= 3) continue;
            double y = (IsNativeClock ? ActualHeight - 6 : ActualHeight / 2) + (lane - 1) * 5;
            double finish = Math.Clamp(X(mark.End ?? owner.State.Time), 0, ActualWidth);
            bool selected = owner.SelectedRowId == row.Id && owner.SelectedMarkId == mark.Id;
            var pen = new Pen(selected ? SystemColors.HighlightBrush : ink, selected ? 2 : 1);
            switch (mark.Kind)
            {
                case WorldAnimationTimelineMarkKind.Interval:
                case WorldAnimationTimelineMarkKind.Window:
                case WorldAnimationTimelineMarkKind.Running:
                    double left = Math.Max(0, x), width = Math.Max(2, finish - left);
                    if (mark.Kind == WorldAnimationTimelineMarkKind.Running)
                    {
                        dc.DrawRectangle(null, pen, new(left, y - 3, width, 6));
                        for (double hatch = left; hatch < left + width; hatch += 8) dc.DrawLine(new Pen(ink, .6), new(hatch, y + 3), new(Math.Min(hatch + 5, left + width), y - 3));
                    }
                    else dc.DrawRoundedRectangle(accent, selected ? pen : null, new(left, y - 3, width, 6), 1, 1);
                    if (mark.Kind == WorldAnimationTimelineMarkKind.Window && row.IsWindowClock && row.CanMoveWindow) dc.DrawRectangle(ink, null, new(Math.Max(0, x - 3), 3, 6, Math.Max(1, ActualHeight - 6)));
                    break;
                case WorldAnimationTimelineMarkKind.Call:
                    dc.DrawLine(pen, new(x - 4, y), new(x + 5, y)); dc.DrawLine(pen, new(x + 1, y - 4), new(x + 5, y)); dc.DrawLine(pen, new(x + 1, y + 4), new(x + 5, y)); break;
                case WorldAnimationTimelineMarkKind.Blocked:
                    dc.DrawLine(pen, new(x - 4, y - 4), new(x + 4, y + 4)); dc.DrawLine(pen, new(x - 4, y + 4), new(x + 4, y - 4));
                    if (ActualWidth - x > 60) WorldAnimationTimelineDrawing.Text(dc, "blocked", x + 7, y - 7, ink, this); break;
                case WorldAnimationTimelineMarkKind.Loop: WorldAnimationTimelineDrawing.Text(dc, "∞", x - 5, y - 8, ink, this); break;
                case WorldAnimationTimelineMarkKind.Truncation:
                    dc.DrawLine(new Pen(ink, 1) { DashStyle = DashStyles.Dash }, new(x, 0), new(x, ActualHeight)); break;
                case WorldAnimationTimelineMarkKind.Keyframe: dc.DrawLine(pen, new(x, y - 4), new(x, y + 4)); break;
                default: WorldAnimationTimelineDrawing.Diamond(dc, new(x, y), mark.Kind == WorldAnimationTimelineMarkKind.Authored ? null : accent, pen); break;
            }
        }
        foreach (var bucket in buckets.Where(b => b.Value > 3)) WorldAnimationTimelineDrawing.Text(dc, "+" + (bucket.Value - 3), bucket.Key * 7 + 3, 0, ink, this);
        if (!IsNativeClock && owner.State.Time >= Begin && owner.State.Time <= End) dc.DrawLine(new Pen(ink, 1.2), new(X(owner.State.Time), 0), new(X(owner.State.Time), ActualHeight));
        if (proposedWindow is double proposed && windowMark != null)
        {
            // This is only a proposed start grip. The adapter, not this control, supplies validated endpoints.
            double x = X(windowMark.Start + proposed - windowThreshold);
            dc.DrawLine(new Pen(ink, 1.5) { DashStyle = DashStyles.Dash }, new(x, 0), new(x, ActualHeight));
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); if (Timeline is not { } owner || Row is not { } row) return;
        Focus(); pressed = e.GetPosition(this);
        var mark = Hit(pressed.X);
        owner.Select(row, mark);
        if (mark != null)
        {
            if (row.IsWindowClock && row.CanMoveWindow && mark.Kind == WorldAnimationTimelineMarkKind.Window && Math.Abs(pressed.X - X(mark.Start)) <= 6)
            { windowMark = mark; windowThreshold = row.WindowStartThreshold ?? mark.Start; gestureRowId = row.Id; draggingWindow = CaptureMouse(); }
            else if (e.ClickCount == 2 && mark.Kind != WorldAnimationTimelineMarkKind.Authored && !IsNativeClock)
                owner.RequestSeek(Math.Ceiling(mark.Start * 60 - 1e-7) / 60);
        }
        else if (!IsNativeClock && owner.State.CanSeek && Math.Abs(pressed.X - X(owner.State.Time)) <= 4)
            draggingPlayhead = CaptureMouse();
        e.Handled = true;
    }
    private WorldAnimationTimelineMark? Hit(double x)
        => VisibleMarks.Reverse().FirstOrDefault(m => Math.Abs(x - X(m.Start)) <= 6 || m.End is double end && x >= X(m.Start) && x <= X(end));
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (draggingPlayhead) Timeline?.RequestSeek(At(e.GetPosition(this).X));
        if (draggingWindow && windowMark != null && Math.Abs(e.GetPosition(this).X - pressed.X) >= 3)
        { proposedWindow = Math.Round((windowThreshold + At(e.GetPosition(this).X) - At(pressed.X)) * 60) / 60; InvalidateVisual(); }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e); if (!IsMouseCaptured) return;
        double? proposed = proposedWindow; var mark = windowMark; string? rowId = gestureRowId; double threshold = windowThreshold; bool move = draggingWindow;
        ReleaseMouseCapture(); ResetGesture();
        if (move && Row is { } row && row.Id == rowId && proposed is double value && mark != null && row.Marks.Any(m => m.Id == mark.Id) && value != threshold) Timeline?.MoveWindow(row, value);
        e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); ResetGesture(); }
    private void ResetGesture() { draggingPlayhead = draggingWindow = false; proposedWindow = null; windowMark = null; gestureRowId = null; InvalidateVisual(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsMouseCaptured) { ReleaseMouseCapture(); ResetGesture(); e.Handled = true; return; }
        if (Timeline?.HandleCanvasKey(e) != true) base.OnKeyDown(e);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    { if (Timeline?.HandleWheel(e.Delta, Keyboard.Modifiers, e.GetPosition(this).X / Math.Max(1, ActualWidth)) == true) e.Handled = true; else base.OnMouseWheel(e); }
    protected override AutomationPeer OnCreateAutomationPeer() => new PlotPeer(this);
    private sealed class PlotPeer(WorldAnimationRowPlot plot) : FrameworkElementAutomationPeer(plot)
    {
        protected override string GetClassNameCore() => nameof(WorldAnimationRowPlot);
        protected override string GetNameCore() => WorldAnimationTimeline.Shown((plot.Row?.Label ?? "Timeline row") + ": " + plot.Row?.TimingLabel);
        protected override List<AutomationPeer>? GetChildrenCore() => plot.VisibleMarks.Take(100).Select(m => (AutomationPeer)new MarkPeer(plot, m)).ToList();
        protected override string GetHelpTextCore() => "Observed marks are read-only. The row's Occurrences button pages every retained occurrence.";
    }
    private sealed class MarkPeer(WorldAnimationRowPlot plot, WorldAnimationTimelineMark mark) : AutomationPeer, IInvokeProvider
    {
        public void Invoke() => plot.Dispatcher.Invoke(() => { if (plot.Row is { } row && row.Marks.Any(m => m.Id == mark.Id)) plot.Timeline?.Select(row, mark); });
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : null;
        protected override string GetNameCore() => WorldAnimationTimeline.Shown($"{mark.Kind} at {mark.Start:R} seconds. {mark.Label}");
        protected override string GetAutomationIdCore() => mark.Id;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => "AnimationOccurrence";
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override bool IsEnabledCore() => plot.IsEnabled;
        protected override bool IsKeyboardFocusableCore() => true;
        protected override bool HasKeyboardFocusCore() => plot.IsKeyboardFocused && plot.Timeline?.SelectedMarkId == mark.Id;
        protected override bool IsOffscreenCore() => !plot.IsVisible;
        protected override Rect GetBoundingRectangleCore() { if (!plot.IsVisible || PresentationSource.FromVisual(plot) == null) return Rect.Empty; var origin = plot.PointToScreen(new(Math.Clamp(plot.X(mark.Start), 0, plot.ActualWidth), 0)); return new(origin, new Size(8, plot.ActualHeight)); }
        protected override Point GetClickablePointCore() => GetBoundingRectangleCore().TopLeft;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override void SetFocusCore() { plot.Focus(); Invoke(); }
        protected override string GetAcceleratorKeyCore() => "";
        protected override string GetAccessKeyCore() => "";
        protected override string GetHelpTextCore() => plot.IsNativeClock ? "Native-clock mark. Select to inspect its source values." : "Select this occurrence without seeking. Double-click in the timeline to seek.";
        protected override string GetItemStatusCore() => mark.Kind.ToString();
        protected override string GetItemTypeCore() => "Animation occurrence";
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.Horizontal;
        protected override bool IsPasswordCore() => false;
        protected override bool IsRequiredForFormCore() => false;
    }
}
