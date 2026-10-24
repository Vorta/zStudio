using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>The source world's sole timeline/transport. It presents snapshots and emits intent, never edits source.</summary>
public sealed class WorldAnimationTimeline : UserControl
{
    internal const double Step = 1.0 / 60;
    private const int MaximumRows = 65536, MaximumMarks = 40000;
    private readonly ObservableCollection<WorldAnimationTimelineRow> displayed = [];
    private IReadOnlyList<WorldAnimationTimelineRow> sourceRows = [], runRows = [];
    private readonly Grid root = new();
    private readonly ListBox rows = new();
    private readonly TextBlock target = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock status = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private readonly TextBlock time = new() { FontFamily = new("Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 6, 0) };
    private readonly TextBlock coverage = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Margin = new(6, 2, 6, 3) };
    private readonly ToolBar transport = new() { Band = 0, Margin = new(0), Padding = new(2, 0, 2, 0) };
    private readonly ToolBar presentation = new() { Band = 0, Margin = new(0), Padding = new(2, 0, 2, 0) };
    private readonly Button play, stop, previous, next, exit, useSelection;
    private readonly ComboBox speed = new() { Width = 68, ItemsSource = new[] { "0.25×", "0.5×", "1×", "2×", "4×" }, SelectedIndex = 2 };
    private readonly CheckBox mute = new() { Content = "Mute", VerticalAlignment = VerticalAlignment.Center, Margin = new(5, 0, 5, 0) };
    private readonly Slider volume = new() { Minimum = 0, Maximum = 1, Value = .5, Width = 64, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox camera = new() { Content = "Follow camera", VerticalAlignment = VerticalAlignment.Center, Margin = new(5, 0, 5, 0) };
    private readonly CheckBox replay = new() { Content = "Replay range", VerticalAlignment = VerticalAlignment.Center, Margin = new(5, 0, 5, 0) };
    private readonly ValueTextBox rangeStart = new() { Text = "0", Width = 48, Padding = new(4, 0, 4, 0) };
    private readonly ValueTextBox rangeEnd = new() { Text = "30", Width = 48, Padding = new(4, 0, 4, 0) };
    private readonly ComboBox view = new() { ItemsSource = new[] { "Source", "Run" }, SelectedIndex = 0, Width = 96 };
    private readonly CheckBox ticks = new() { Content = "Ticks", VerticalAlignment = VerticalAlignment.Center, Margin = new(5, 0, 5, 0) };
    private readonly CheckBox follow = new() { Content = "Follow playhead", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new(5, 0, 5, 0) };
    private readonly WorldAnimationTimeRuler ruler;
    private readonly ColumnDefinition rulerLabels = new() { Width = new(220) };
    private readonly Grid rulerGrid = new() { Height = 28 };
    private bool changing, changingRows;
    private string? selectedId, selectedMarkId;
    private double visibleStart, visibleEnd = 30;
    private double preferredLabelWidth = 220;
    private WorldAnimationTimelineState state = new();

    public event Action<string>? TransportRequested;
    public event Action<double>? SeekRequested;
    public event Action<WorldAnimationTimelineRow>? RowSelected;
    public event Action<WorldAnimationTimelineRow, WorldAnimationTimelineMark>? OccurrenceSelected;
    public event Action<WorldAnimationTimelineRow, bool>? ExpansionRequested;
    public event Action<WorldAnimationTimelineRow, bool>? InclusionRequested;
    public event Action<WorldAnimationTimelineRow, bool>? GuideVisibilityRequested;
    public event Action<WorldAnimationTimelineRow>? EditTimingRequested;
    public event Action<WorldAnimationTimelineRow, double>? WindowMoveRequested;
    public event Action<string, object>? OptionRequested;
    internal event Action? ViewChanged;

    public WorldAnimationTimelineState State => state;
    public string ViewMode => view.SelectedIndex == 1 ? "run" : "source";
    public double VisibleStart => visibleStart;
    public double VisibleEnd => visibleEnd;
    public double LabelWidth => Math.Min(preferredLabelWidth, Math.Max(0, ActualWidth - SystemParameters.VerticalScrollBarWidth) * .42);
    public string? SelectedRowId => selectedId;
    public string? SelectedMarkId => selectedMarkId;
    public WorldAnimationTimelinePresentation PresentationState => new(ViewMode, visibleStart, visibleEnd, FollowPlayhead, ShowTicks, selectedId, selectedMarkId, preferredLabelWidth);
    public bool FollowPlayhead { get => follow.IsChecked == true; set { follow.IsChecked = value; RefreshView(); } }
    public bool ShowTicks { get => ticks.IsChecked == true; set { ticks.IsChecked = value; RefreshView(); } }

    public WorldAnimationTimeline()
    {
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        root.SetResourceReference(Panel.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        Content = root;
        var heading = new DockPanel { Margin = new(6, 3, 4, 1) };
        useSelection = Command("Use selection", "useSelection", "Use the selected binding; stop the old run without starting another");
        DockPanel.SetDock(useSelection, Dock.Right); heading.Children.Add(useSelection);
        var title = new StackPanel(); title.Children.Add(target); title.Children.Add(status); heading.Children.Add(title);
        root.Children.Add(heading);
        play = Command("Play", "play", "Play or pause (Space on the timeline)");
        stop = Command("Stop", "stop", "Stop and restore the initialized static world");
        previous = Command("−1f", "previous", "Previous runtime tick");
        next = Command("+1f", "next", "Next runtime tick");
        exit = Command("Exit", "exit", "Stop and release this timeline's pinned target");
        foreach (var element in new UIElement[] { play, stop, previous, next, time, exit }) transport.Items.Add(element);
        foreach (var element in new UIElement[] { play, stop, time, exit }) ToolBar.SetOverflowMode(element, OverflowMode.Never);
        transport.Items.Add(speed); transport.Items.Add(replay); transport.Items.Add(mute); transport.Items.Add(volume); transport.Items.Add(camera);
        var range = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        range.Children.Add(new TextBlock { Text = "Range ", VerticalAlignment = VerticalAlignment.Center });
        range.Children.Add(rangeStart); range.Children.Add(new TextBlock { Text = "–", VerticalAlignment = VerticalAlignment.Center }); range.Children.Add(rangeEnd);
        range.Children.Add(new TextBlock { Text = " s", VerticalAlignment = VerticalAlignment.Center }); transport.Items.Add(range);
        AutomationProperties.SetName(rangeStart, "Preview range start in seconds"); AutomationProperties.SetName(rangeEnd, "Preview range end in seconds");
        AutomationProperties.SetName(speed, "Playback speed"); AutomationProperties.SetName(volume, "Playback volume");
        ToolBarTray.SetIsLocked(transport, true); transport.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        Grid.SetRow(transport, 1); root.Children.Add(transport);

        var main = new Grid(); main.RowDefinitions.Add(new() { Height = GridLength.Auto }); main.RowDefinitions.Add(new() { Height = GridLength.Auto }); main.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); main.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(main, 2); root.Children.Add(main);
        presentation.Items.Add(view); presentation.Items.Add(ticks); presentation.Items.Add(follow);
        var fit = new Button { Content = "Fit", Padding = new(7, 2, 7, 2), ToolTip = "Fit selection, evaluated events, or preview range" };
        var fitMenu = new ContextMenu();
        foreach (var pair in new[] { ("Selection", "selection"), ("Evaluated events", "evaluated"), ("Preview range", "range") })
        { var item = new MenuItem { Header = pair.Item1 }; item.Click += (_, _) => { Fit(pair.Item2); OptionRequested?.Invoke("fit", pair.Item2); }; fitMenu.Items.Add(item); }
        fit.Click += (_, _) => { fitMenu.PlacementTarget = fit; fitMenu.IsOpen = true; }; presentation.Items.Add(fit);
        presentation.Items.Add(Command("Evaluate paths", "evaluatePaths", "Silently evaluate paths for this target and included child calls"));
        presentation.Items.Add(Command("Estimate duration", "duration", "Explicit bounded duration analysis; does not change the current run"));
        presentation.Items.Add(Command("Scenario", "scenario", "Focus preview configuration in Properties"));
        ToolBarTray.SetIsLocked(presentation, true); presentation.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        main.Children.Add(presentation);
        rulerGrid.ColumnDefinitions.Add(rulerLabels); rulerGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var label = new TextBlock { Text = "Source / inclusion / clock", FontSize = 11, Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; rulerGrid.Children.Add(label);
        var labelGrip = new Thumb { Width = 5, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.SizeWE, Background = Brushes.Transparent, Focusable = true, ToolTip = "Resize timeline labels" };
        AutomationProperties.SetName(labelGrip, "Resize timeline labels");
        labelGrip.DragDelta += (_, e) => { SetLabelWidth(Math.Clamp(LabelWidth + e.HorizontalChange, 80, 720)); OptionRequested?.Invoke("labelWidth", preferredLabelWidth); };
        labelGrip.KeyDown += (_, e) => { if (e.Key is Key.Left or Key.Right) { SetLabelWidth(Math.Clamp(preferredLabelWidth + (e.Key == Key.Left ? -10 : 10), 80, 720)); OptionRequested?.Invoke("labelWidth", preferredLabelWidth); e.Handled = true; } };
        rulerGrid.Children.Add(labelGrip);
        ruler = new(this); Grid.SetColumn(ruler, 1); rulerGrid.Children.Add(ruler); Grid.SetRow(rulerGrid, 1); main.Children.Add(rulerGrid);
        rows.ItemsSource = displayed; rows.BorderThickness = new(0); rows.Padding = new(0); rows.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        rows.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        rows.SetValue(ScrollViewer.CanContentScrollProperty, true); rows.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Visible); rows.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(rows, true); VirtualizingPanel.SetVirtualizationMode(rows, VirtualizationMode.Recycling);
        var rowStyle = new Style(typeof(ListBoxItem)); rowStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); rowStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0))); rowStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
        rowStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(WorldAnimationTimelineRow.Label))));
        rowStyle.Setters.Add(new Setter(AutomationProperties.HelpTextProperty, new Binding(nameof(WorldAnimationTimelineRow.Detail)))); rows.ItemContainerStyle = rowStyle;
        var factory = new FrameworkElementFactory(typeof(WorldAnimationTimelineRowView));
        factory.SetBinding(WorldAnimationTimelineRowView.OwnerProperty, new Binding { RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(WorldAnimationTimeline), 1) });
        rows.ItemTemplate = new DataTemplate { VisualTree = factory }; Grid.SetRow(rows, 2); main.Children.Add(rows);
        Grid.SetRow(coverage, 3); main.Children.Add(coverage);
        AutomationProperties.SetName(rows, "Animation timeline rows"); AutomationProperties.SetName(view, "Timeline projection");
        rows.SelectionChanged += (_, _) => { if (changingRows) return; if (rows.SelectedItem is WorldAnimationTimelineRow row) { selectedId = row.Id; selectedMarkId = null; RowSelected?.Invoke(row); RefreshView(); } };
        rows.PreviewKeyDown += RowsKeyDown;
        rows.PreviewMouseWheel += (_, e) =>
        {
            // Let the time canvas anchor Ctrl+wheel at its actual pointer, rather than the label-column center.
            for (DependencyObject? node = e.OriginalSource as DependencyObject; node != null && node != rows;
                node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node is WorldAnimationRowPlot) return;
            if (HandleWheel(e.Delta, Keyboard.Modifiers, .5)) e.Handled = true;
        };
        view.SelectionChanged += (_, _) => { if (changing) return; UpdateDisplayedRows(); OptionRequested?.Invoke("view", ViewMode); };
        ticks.Click += (_, _) => { RefreshView(); OptionRequested?.Invoke("ticks", ShowTicks); };
        follow.Click += (_, _) => { RefreshView(); OptionRequested?.Invoke("followPlayhead", FollowPlayhead); };
        speed.SelectionChanged += (_, _) => { if (!changing) OptionRequested?.Invoke("speed", new[] { .25, .5, 1, 2, 4 }[Math.Clamp(speed.SelectedIndex, 0, 4)]); };
        mute.Click += (_, _) => { if (!changing) OptionRequested?.Invoke("mute", mute.IsChecked == true); };
        camera.Click += (_, _) => { if (!changing) OptionRequested?.Invoke("followCamera", camera.IsChecked == true); };
        replay.Click += (_, _) => { if (!changing) OptionRequested?.Invoke("replay", replay.IsChecked == true); };
        volume.ValueChanged += (_, _) => { if (!changing) OptionRequested?.Invoke("volume", volume.Value); };
        rangeStart.KeyDown += RangeKeyDown; rangeEnd.KeyDown += RangeKeyDown;
        rangeStart.LostKeyboardFocus += (_, _) => ProposeRange(); rangeEnd.LostKeyboardFocus += (_, _) => ProposeRange();
        SizeChanged += (_, _) => { if (displayed.Count == 0) rulerLabels.Width = new(LabelWidth); bool narrow = ActualWidth < 640; foreach (var element in new UIElement[] { previous, next, speed, replay, mute, volume, camera, range }) ToolBar.SetOverflowMode(element, narrow ? OverflowMode.Always : OverflowMode.AsNeeded); RefreshView(); };
        SetState(state);
    }

    private Button Command(string text, string action, string tip)
    {
        var button = new Button { Content = text, ToolTip = tip, MinHeight = 28, MinWidth = 30, Padding = new(6, 2, 6, 2), Margin = new(1, 0, 1, 0) };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => TransportRequested?.Invoke(action == "play" && state.Playing ? "pause" : action);
        return button;
    }

    public void SetState(WorldAnimationTimelineState value)
    {
        ArgumentNullException.ThrowIfNull(value); state = value; changing = true;
        try
        {
            target.Text = Shown(value.Target); target.ToolTip = value.Target;
            status.Text = Shown(value.Status + (value.SelectedTarget.Length > 0 ? " · Selected: " + value.SelectedTarget : ""));
            status.ToolTip = status.Text;
            play.Content = value.Playing ? "Pause" : "Play"; AutomationProperties.SetName(play, value.Playing ? "Pause" : "Play");
            play.IsEnabled = value.CanPlay; stop.IsEnabled = value.CanStop; exit.IsEnabled = value.CanExit;
            previous.IsEnabled = next.IsEnabled = ruler.IsEnabled = value.CanSeek;
            useSelection.Visibility = value.CanUseSelection ? Visibility.Visible : Visibility.Collapsed;
            time.Text = $"{value.Time:0.000} s · {Math.Floor(value.Time * 60 + 1e-7):0}f";
            coverage.Text = Shown(value.Coverage); coverage.ToolTip = value.Coverage;
            speed.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { .25, .5, 1, 2, 4 }, value.Speed));
            mute.IsChecked = value.Muted; volume.Value = Math.Clamp(value.Volume, 0, 1); camera.IsChecked = value.FollowCamera; replay.IsChecked = value.Replay;
            if (!rangeStart.IsKeyboardFocusWithin) rangeStart.Text = value.RangeStart.ToString("0.#########", CultureInfo.InvariantCulture);
            if (!rangeEnd.IsKeyboardFocusWithin) rangeEnd.Text = value.RangeEnd.ToString("0.#########", CultureInfo.InvariantCulture);
        }
        finally { changing = false; }
        if (FollowPlayhead && value.Playing && (value.Time < visibleStart || value.Time > visibleEnd))
        { double span = visibleEnd - visibleStart; SetVisibleRange(Math.Clamp(value.Time - span * .2, 0, 3600 - span), Math.Clamp(value.Time - span * .2, 0, 3600 - span) + span); }
        RefreshView();
    }

    public void SetRows(IReadOnlyList<WorldAnimationTimelineRow> source, IReadOnlyList<WorldAnimationTimelineRow> run)
    {
        ValidateRows(source); ValidateRows(run); sourceRows = source; runRows = run; UpdateDisplayedRows();
    }
    private static void ValidateRows(IReadOnlyList<WorldAnimationTimelineRow> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Count > MaximumRows) throw new ArgumentException("Timeline rows exceed the presentation allowance.");
        long marks = 0; foreach (var row in value) { marks += row.Marks.Count; if (marks > MaximumMarks) throw new ArgumentException("Timeline observations exceed the presentation allowance."); }
    }
    private void UpdateDisplayedRows()
    {
        var value = ViewMode == "run" ? runRows : sourceRows; changingRows = true;
        try
        {
            while (displayed.Count > value.Count) displayed.RemoveAt(displayed.Count - 1);
            for (int i = 0; i < value.Count; i++) { if (i == displayed.Count) displayed.Add(value[i]); else if (!ReferenceEquals(displayed[i], value[i])) displayed[i] = value[i]; }
            rows.SelectedItem = displayed.FirstOrDefault(r => r.Id == selectedId);
        }
        finally { changingRows = false; }
        RefreshView();
    }
    public void SetView(string value)
    {
        if (value is not ("source" or "run")) throw new ArgumentException("Timeline view must be source or run.");
        changing = true; try { view.SelectedIndex = value == "run" ? 1 : 0; } finally { changing = false; }
        UpdateDisplayedRows();
    }
    public void SelectRow(string? id, string? markId = null)
    { selectedId = id; selectedMarkId = markId; changingRows = true; try { rows.SelectedItem = displayed.FirstOrDefault(r => r.Id == id); } finally { changingRows = false; } RefreshView(); }
    internal WorldAnimationTimelineRow? CurrentRow(string id) => displayed.FirstOrDefault(row => row.Id == id);
    internal bool SelectCurrentMark(string rowId, string markId, string evidence, string projection)
    {
        if (state.EvidenceIdentity != evidence || ViewMode != projection || CurrentRow(rowId) is not { } row || row.Marks.FirstOrDefault(mark => mark.Id == markId) is not { } mark) return false;
        Select(row, mark); return true;
    }
    public void SetBodyVisible(bool visible) { root.RowDefinitions[2].Height = visible ? new(1, GridUnitType.Star) : new(0); root.Children[2].Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
    public void SetLabelWidth(double width)
    { if (!double.IsFinite(width) || width is < 80 or > 720) throw new ArgumentOutOfRangeException(nameof(width)); preferredLabelWidth = width; if (displayed.Count == 0) rulerLabels.Width = new(LabelWidth); RefreshView(); }
    public void SetVisibleRange(double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > 3600 || end - start < Step - 1e-9) throw new ArgumentException("Visible range must span at least one tick within 0–3600 seconds.");
        visibleStart = start; visibleEnd = end; RefreshView();
    }
    public void Fit(string target)
    {
        if (target == "range") { SetVisibleRange(state.RangeStart, Math.Max(state.RangeStart + Step, state.RangeEnd)); return; }
        if (target is not ("selection" or "evaluated")) throw new ArgumentException("Fit must target selection, evaluated, or range.");
        // Local keyframe/flight clocks are not positions on the global preview ruler.
        var candidates = displayed.Where(r => !r.IsWindowClock && r.NativeClockStart == null && r.NativeClockEnd == null && (target != "selection" || r.Id == selectedId))
            .SelectMany(r => r.Marks).Where(m => double.IsFinite(m.Start) && m.Kind != WorldAnimationTimelineMarkKind.Authored).ToArray();
        if (candidates.Length == 0) return;
        double begin = Math.Clamp(candidates.Min(m => m.Start), 0, 3600 - Step);
        double end = Math.Clamp(candidates.Max(m => m.End is double e && double.IsFinite(e) ? e : Math.Max(m.Start, state.Time)), begin + Step, 3600);
        double margin = Math.Max(Step, (end - begin) * .05); SetVisibleRange(Math.Max(0, begin - margin), Math.Min(3600, end + margin));
    }
    internal void RefreshView() { ruler.InvalidateVisual(); ViewChanged?.Invoke(); }
    internal void AlignRuler(FrameworkElement plot)
    {
        if (plot.ActualWidth <= 0 || ActualWidth <= 0) return;
        // Fluent uses an overlay scrollbar. Use the realized content's bounds rather than
        // assuming a reserved system scrollbar gutter or a particular item border thickness.
        double left = plot.TranslatePoint(new(), this).X;
        double right = Math.Max(0, ActualWidth - left - plot.ActualWidth);
        if (!double.IsFinite(left) || left < 0 || left >= ActualWidth) return;
        if (Math.Abs(rulerLabels.Width.Value - left) > .01) rulerLabels.Width = new(left);
        if (Math.Abs(rulerGrid.Margin.Right - right) > .01) rulerGrid.Margin = new(0, 0, right, 0);
    }
    internal void RequestSeek(double seconds)
    { if (state.CanSeek && double.IsFinite(seconds)) SeekRequested?.Invoke(Math.Clamp(Math.Round(seconds * 60) / 60, 0, 3600)); }
    internal void Select(WorldAnimationTimelineRow row, WorldAnimationTimelineMark? mark = null)
    {
        // An occurrence owns the exact source selection; do not also dispatch the row's sequence selection.
        changingRows = mark != null;
        try { rows.SelectedItem = row; selectedId = row.Id; }
        finally { changingRows = false; }
        if (mark != null) { selectedMarkId = mark.Id; OccurrenceSelected?.Invoke(row, mark); }
        RefreshView();
    }
    internal void Expand(WorldAnimationTimelineRow row, bool expanded) => ExpansionRequested?.Invoke(row, expanded);
    internal void Include(WorldAnimationTimelineRow row, bool included) { if (row.InclusionEnabled) InclusionRequested?.Invoke(row, included); }
    internal void ShowGuides(WorldAnimationTimelineRow row, bool shown) => GuideVisibilityRequested?.Invoke(row, shown);
    internal void EditTiming(WorldAnimationTimelineRow row) { if (row.CanEditTiming) EditTimingRequested?.Invoke(row); }
    internal void MoveWindow(WorldAnimationTimelineRow row, double value) { if (row.IsWindowClock && row.CanMoveWindow && double.IsFinite(value)) WindowMoveRequested?.Invoke(row, value); }
    internal bool HandleWheel(int delta, ModifierKeys modifiers, double fraction)
    {
        double span = visibleEnd - visibleStart;
        if (modifiers == ModifierKeys.Control)
        {
            double at = visibleStart + span * Math.Clamp(fraction, 0, 1), nextSpan = Math.Clamp(span * (delta > 0 ? .8 : 1.25), Step, 3600);
            double begin = Math.Clamp(at - nextSpan * fraction, 0, 3600 - nextSpan);
            FollowPlayhead = false; SetVisibleRange(begin, begin + nextSpan); OptionRequested?.Invoke("visibleRange", new[] { visibleStart, visibleEnd }); return true;
        }
        if (modifiers == ModifierKeys.Shift)
        {
            double begin = Math.Clamp(visibleStart - Math.Sign(delta) * span * .1, 0, 3600 - span);
            FollowPlayhead = false; SetVisibleRange(begin, begin + span); OptionRequested?.Invoke("visibleRange", new[] { visibleStart, visibleEnd }); return true;
        }
        return false;
    }
    internal bool HandleCanvasKey(KeyEventArgs e)
    {
        if (e.IsRepeat && e.Key == Key.Space) { e.Handled = true; return true; }
        double amount = Keyboard.Modifiers == ModifierKeys.Shift ? 10 * Step : Step;
        switch (e.Key)
        {
            case Key.Left: RequestSeek(state.Time - amount); break;
            case Key.Right: RequestSeek(state.Time + amount); break;
            case Key.Home: RequestSeek(Keyboard.Modifiers == ModifierKeys.Control ? 0 : state.RangeStart); break;
            case Key.End: RequestSeek(state.RangeEnd); break;
            case Key.Space when Keyboard.Modifiers == ModifierKeys.None: if (state.CanPlay) TransportRequested?.Invoke(state.Playing ? "pause" : "play"); break;
            case Key.F2: if (rows.SelectedItem is WorldAnimationTimelineRow row) EditTiming(row); break;
            default: return false;
        }
        e.Handled = true; return true;
    }
    private void RowsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is WorldAnimationRowPlot) return;
        if (e.OriginalSource is CheckBox or Button or TextBox) return;
        if (rows.SelectedItem is not WorldAnimationTimelineRow row) return;
        if (e.Key is Key.Left or Key.Right && row.Expandable) { Expand(row, e.Key == Key.Right); e.Handled = true; }
        else if (e.Key == Key.F2) { EditTiming(row); e.Handled = true; }
    }
    private void RangeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ProposeRange(); e.Handled = true; }
        else if (e.Key == Key.Escape) { rangeStart.Text = state.RangeStart.ToString("R", CultureInfo.InvariantCulture); rangeEnd.Text = state.RangeEnd.ToString("R", CultureInfo.InvariantCulture); e.Handled = true; }
    }
    private void ProposeRange()
    {
        if (changing || !double.TryParse(rangeStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) || !double.TryParse(rangeEnd.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) || !double.IsFinite(a) || !double.IsFinite(b) || a < 0 || b > 3600 || b <= a) return;
        a = Math.Floor(a * 60) / 60; b = Math.Ceiling(b * 60) / 60;
        if (a != state.RangeStart || b != state.RangeEnd) OptionRequested?.Invoke("range", new[] { a, b });
    }
    internal static string Shown(string value) => value.Length > 1024 ? value[..1024] + "…" : value;
}
