using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>What the Timing view shows: the run's projection at the playhead, the transport's time and range, and whether it is stale.</summary>
internal sealed record AnimationTimingState
{
    public static AnimationTimingState Empty { get; } = new();
    public AnimationTiming? Timing { get; init; }
    public double Time { get; init; }
    public double RangeStart { get; init; }
    public double RangeEnd { get; init; } = 30;
    public bool CanSeek { get; init; }
    public bool Playing { get; init; }
    /// <summary>The run has evidence (a frame); without it the empty state shows over the lanes.</summary>
    public bool Evaluated { get; init; }
    public bool Stale { get; init; }
    public bool StaleByEdit { get; init; } = true;
    /// <summary>Shown instead of the lanes ("Not bound on Hard: …").</summary>
    public string? Message { get; init; }
    /// <summary>Whether an event's start threshold can be proposed (a bracket end drags).</summary>
    public Func<Guid, bool>? CanEditThreshold { get; init; }
    /// <summary>The Starts lane's starts neither move nor go (a compiled preview's one start, the shown animation at 0 s).</summary>
    public bool StartsReadOnly { get; init; }
    /// <summary>The opened flight's drop windows: a "Drop windows" lane under its flight sequence lane (design §14).</summary>
    public AnimationTimingDrops? Drops { get; init; }
    /// <summary>The ruler's "+5 s" at the range's end lengthens the range (Extend +5 s).</summary>
    public bool CanExtend { get; init; }
    /// <summary>Auto's measurement has not arrived yet: the ruler and lanes shade no range end and the ruler offers no "+5 s".</summary>
    public bool RangePending { get; init; }
}

/// <summary>
/// The Drop windows lane: under <see cref="AnchorLane"/> (the flight sequence), each window of each flight cycle the run
/// reached as a bar on the flight's sequence clock. <see cref="Editable"/> bars drag (their start only, never their length).
/// </summary>
internal sealed record AnimationTimingDrops(string AnchorLane, IReadOnlyList<AnimationTimingDropBar> Bars, bool Editable)
{
    public Guid? Selected { get; init; }
    public string Note { get; init; } = "";
    /// <summary>The analysis is stale (an event draft or edit, a scenario change): the bars show where it found the windows, muted.</summary>
    public bool Stale { get; init; }
}
/// <summary>One window in one flight cycle: shared-clock start and end, the cycle's start, and its start's bounds on the sequence clock.</summary>
internal sealed record AnimationTimingDropBar(Guid Window, int Number, double Cycle, double Start, double Duration, double Minimum, double Maximum)
{
    public bool Moved { get; init; }
    public double End => Cycle + Start + Duration;
}

/// <summary>
/// Animation mode's Timing view (design/animation-redesign.md §9): the run on the shared clock in seconds. A label column
/// (220 DIP, grip 160–420), the ruler (playback range shaded, the playhead, click or drag seeks), the Starts lane (drag a
/// start, right-click to remove it), instance groups with their binding state and one lane per sequence with its state bands
/// and marks; thresholds on demand with a draggable bracket end for the selected event; and the Log. It draws a snapshot
/// (<see cref="AnimationTiming"/>) and raises intents; the window owns the run, the scenario, the selection's other views
/// and the source draft.
/// </summary>
internal sealed partial class AnimationTimingView : Grid
{
    internal const double DefaultLabelWidth = 220, MinimumLabelWidth = 160, MaximumLabelWidth = 420;
    private readonly TimingHeader header;
    private readonly TimingSurface surface;
    private readonly ScrollBar vertical = new() { Orientation = Orientation.Vertical, Minimum = 0, SmallChange = 24, Width = 12 };
    private readonly ScrollBar horizontal = new() { Orientation = Orientation.Horizontal, Minimum = 0, Height = 12 };
    private readonly Thumb grip = new() { Width = 7, Cursor = Cursors.SizeWE, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, Focusable = true };
    private readonly Grid lanesView = new();
    private readonly DockPanel logView = new() { Visibility = Visibility.Collapsed };
    private readonly ListView log = new() { BorderThickness = new(0) };
    private readonly TextBlock logFooter = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 10, 0) };
    private readonly CheckBox followLatest = new() { Content = "Follow latest", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 6, 0) };
    private readonly Border message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new(14, 10, 14, 10), CornerRadius = new(6), BorderThickness = new(1), IsHitTestVisible = false, MaxWidth = 520 };
    private readonly TextBlock messageText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, TextAlignment = TextAlignment.Center };
    private readonly ToggleButton lanesToggle, logToggle, thresholdsToggle;
    private bool changing;
    private AnimationTimingLogItem[] logItems = [];
    private AnimationTiming? logSource;

    internal AnimationTimingState State { get; private set; } = AnimationTimingState.Empty;
    internal AnimationTiming? Timing => State.Timing;
    internal double VisibleStart { get; private set; }
    internal double VisibleEnd { get; private set; } = 30;
    internal double LabelWidth { get; private set; } = DefaultLabelWidth;
    internal string View { get; private set; } = "lanes";
    internal bool Thresholds { get; private set; }
    internal string? SelectedLane { get; private set; }
    internal string? SelectedMark { get; private set; }
    /// <summary>The event the selection names (a mark's event, or the graph's selected row): its marks are outlined and its bracket end drags.</summary>
    internal Guid? SelectedEvent { get; private set; }
    internal double ScrollOffset => vertical.Value;
    internal FrameworkElement Tools { get; }
    /// <summary>Whether the user moved the visible range since the view was given its key (the first projection fits the playback range).</summary>
    private bool rangeChosen;
    private string key = "";

    /// <summary>A ruler click or drag (final at the release), ← → and Home/End: the window seeks.</summary>
    internal event Action<double, bool>? SeekRequested;
    internal event Action<AnimationTimingLane>? LaneSelected;
    internal event Action<AnimationTimingLane, AnimationTimingMark>? MarkSelected;
    internal event Action<string, bool>? GroupToggled;
    internal event Action<Guid, double>? StartMoved;
    internal event Action<Guid>? StartRemoved;
    internal event Action<AnimationTimingMark, double>? ThresholdProposed;
    /// <summary>A drop window chosen on its lane (a click), or moved there (a drag: its new start on the flight sequence's clock).</summary>
    internal event Action<Guid>? DropWindowSelected;
    internal event Action<Guid, double>? DropWindowMoved;
    internal const string DropLaneId = "drops";
    private AnimationTimingLane? dropLane;
    private IReadOnlyList<AnimationTimingLane> rows = [];
    /// <summary>The rows shown: the projection's lanes, with the Drop windows lane under its flight sequence lane.</summary>
    internal IReadOnlyList<AnimationTimingLane> Rows => rows;
    internal AnimationTimingLane? RowLane(string id) => id == DropLaneId ? dropLane : Timing?.Lane(id);
    private void BuildRows()
    {
        var lanes = Timing?.Lanes ?? [];
        dropLane = null;
        if (State.Drops is not { } drops || lanes.FirstOrDefault(l => l.Id == drops.AnchorLane) is not { } anchor) { rows = lanes; return; }
        dropLane = new(DropLaneId, AnimationTimingLaneKind.Sequence, "Drop windows", anchor.Depth)
        {
            Group = anchor.Group, State = AnimationNotice.Plural(drops.Bars.Select(b => b.Window).Distinct().Count(), "1 window", "windows"),
            Tooltip = "Drop windows on the flight sequence's clock" + (drops.Editable ? ": click one to select it, drag it to move its start (0.025 s; Shift 0.1 s; Ctrl free); its length stays" : "") + (drops.Note.Length > 0 ? ". " + drops.Note : ""),
        };
        List<AnimationTimingLane> shown = new(lanes.Count + 1);
        foreach (var lane in lanes) { shown.Add(lane); if (lane == anchor) shown.Add(dropLane); }
        rows = shown;
    }
    internal void SelectDropWindow(Guid id) => DropWindowSelected?.Invoke(id);
    internal void MoveDropWindow(Guid id, double start) => DropWindowMoved?.Invoke(id, start);
    /// <summary>EditTiming (F2), the Starts lane's Show in Scenario, and the ruler's "+5 s" at the range's end (Extend +5 s).</summary>
    internal event Action? EditTimingRequested, ScenarioRequested, ExtendRequested;
    /// <summary>The ruler's "+5 s" at the playback range's end (design §13: Extend +5 s).</summary>
    private readonly Button extend = new() { Content = new TextBlock { Text = "+5 s", FontSize = 10.5 }, Height = 17, MinHeight = 17, Padding = new(5, 0, 5, 0), Visibility = Visibility.Collapsed,
        ToolTip = "Extend +5 s: lengthen the playback range by 5 s (it becomes Custom until Auto)" };
    private readonly Canvas rulerOverlay = new() { ClipToBounds = true, Background = null };
    /// <summary>
    /// Places the ruler's "+5 s" just after the range's end (before it at the right edge); hidden while the end is out of view or
    /// Auto still measures it. It never covers the playhead's time tag: at rest it moves beside the tag (after it when there is
    /// room, else before it); while playing it hides as the tag passes.
    /// </summary>
    internal void PlaceExtend()
    {
        double clock = surface.ClockWidth, span = Math.Max(1e-9, VisibleEnd - VisibleStart), right = LabelWidth + clock;
        double x = LabelWidth + (State.RangeEnd - VisibleStart) / span * clock;
        bool shown = State.CanExtend && !State.RangePending && View == "lanes" && clock > 0 && x >= LabelWidth - 1 && x <= right + 1;
        double left = 0;
        if (shown)
        {
            extend.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = extend.DesiredSize.Width;
            left = x + 3 + width <= right ? x + 3 : x - 3 - width;
            if (header.PlayheadTag() is { } tag && left < tag.Right + ExtendGap && left + width > tag.Left - ExtendGap)
            {
                double after = tag.Right + ExtendGap, before = tag.Left - ExtendGap - width;
                if (State.Playing) shown = false;
                else if (after + width <= right) left = after;
                else if (before >= LabelWidth) left = before;
                else shown = false;
            }
        }
        extend.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown) { Canvas.SetLeft(extend, Math.Max(LabelWidth, left)); Canvas.SetTop(extend, 1); }
    }
    /// <summary>The least room between the ruler's "+5 s" and the playhead's time tag.</summary>
    private const double ExtendGap = 3;

    internal AnimationTimingView()
    {
        AutomationProperties.SetName(this, "Timing");
        Background = Brushes.Transparent; ClipToBounds = true;
        header = new(this); surface = new(this);
        // Lanes: the header (32), then the lanes with their scroll bars; the label grip spans both.
        lanesView.RowDefinitions.Add(new() { Height = new(32) });
        lanesView.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        lanesView.RowDefinitions.Add(new() { Height = GridLength.Auto });
        lanesView.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        lanesView.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        lanesView.Children.Add(header); Grid.SetColumnSpan(header, 2);
        // Over the ruler: "+5 s" at the playback range's end, a small outlined pill (visible on the ruler in both themes).
        extend.Template = RulerPill();
        extend.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        extend.Cursor = Cursors.Hand; extend.Focusable = true;
        AutomationProperties.SetName(extend, "Extend the range by 5 s");
        extend.Click += (_, _) => ExtendRequested?.Invoke();
        rulerOverlay.Children.Add(extend); Grid.SetColumnSpan(rulerOverlay, 2); lanesView.Children.Add(rulerOverlay); Panel.SetZIndex(rulerOverlay, 5);
        Grid.SetRow(surface, 1); lanesView.Children.Add(surface);
        Grid.SetRow(vertical, 1); Grid.SetColumn(vertical, 1); lanesView.Children.Add(vertical);
        Grid.SetRow(horizontal, 2); lanesView.Children.Add(horizontal);
        AutomationProperties.SetName(vertical, "Scroll the lanes"); AutomationProperties.SetName(horizontal, "Scroll the shared clock");
        vertical.ValueChanged += (_, _) => surface.InvalidateVisual();
        horizontal.ValueChanged += (_, e) =>
        {
            if (changing) return;
            double span = VisibleEnd - VisibleStart;
            SetVisibleRange(e.NewValue, e.NewValue + span, user: true);
        };
        Grid.SetRowSpan(grip, 2); lanesView.Children.Add(grip); Panel.SetZIndex(grip, 4);
        AutomationProperties.SetName(grip, "Resize the label column");
        grip.ToolTip = "Drag to resize the label column (160–420 DIP); ← → with the grip focused";
        grip.DragDelta += (_, e) => SetLabelWidth(LabelWidth + e.HorizontalChange);
        grip.KeyDown += (_, e) => { if (e.Key is Key.Left or Key.Right) { SetLabelWidth(LabelWidth + (e.Key == Key.Left ? -10 : 10)); e.Handled = true; } };
        message.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        message.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        messageText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        message.Child = messageText; Grid.SetRow(message, 1); lanesView.Children.Add(message); Panel.SetZIndex(message, 3);
        AutomationProperties.SetName(message, "Timing status");
        Children.Add(lanesView);
        BuildLog(); Children.Add(logView);
        // The view bar's tools: Lanes | Log, Thresholds, Fit range ▾.
        lanesToggle = Segment("Lanes", "Lanes: one row per sequence, grouped by instance");
        logToggle = Segment("Log", "Log: the same evidence as a table, newest last");
        lanesToggle.Click += (_, _) => SetView("lanes"); logToggle.Click += (_, _) => SetView("log");
        Border segmented = new() { CornerRadius = new(5), BorderThickness = new(1), Padding = new(2), VerticalAlignment = VerticalAlignment.Center };
        segmented.SetResourceReference(Border.BackgroundProperty, "ControlFillColorTertiaryBrush");
        segmented.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        StackPanel pair = new() { Orientation = Orientation.Horizontal }; pair.Children.Add(lanesToggle); pair.Children.Add(logToggle); segmented.Child = pair;
        AutomationProperties.SetName(segmented, "Timing view");
        thresholdsToggle = new() { Content = new TextBlock { Text = "Thresholds", FontSize = 12.5 }, Padding = new(10, 0, 10, 0), Margin = new(8, 0, 0, 0),
            ToolTip = "Thresholds: brackets from each event's observed anchor to anchor + its start threshold; with an event selected, drag its bracket end to propose a new threshold" };
        thresholdsToggle.SetResourceReference(StyleProperty, "CommandButton"); thresholdsToggle.Width = double.NaN; thresholdsToggle.Height = 28;
        AutomationProperties.SetName(thresholdsToggle, "Thresholds");
        thresholdsToggle.Click += (_, _) => { if (!changing) SetThresholds(thresholdsToggle.IsChecked == true); };
        Button fit = new() { Content = new TextBlock { Text = "Fit range", FontSize = 12.5 }, Padding = new(10, 0, 6, 0), Height = 28, Margin = new(4, 0, 0, 0), ToolTip = "Fit the visible range to the playback range" };
        fit.SetResourceReference(StyleProperty, "CommandButton"); fit.Width = double.NaN;
        AutomationProperties.SetName(fit, "Fit range");
        fit.Click += (_, _) => Fit("range");
        Button more = new() { Content = new PreviewIcon { Kind = "Chevron", Width = 12, Height = 12 }, Width = 24, Height = 28, ToolTip = "Fit range, Fit run or Fit selection" };
        more.SetResourceReference(StyleProperty, "CommandButton"); more.Width = 24;
        AutomationProperties.SetName(more, "More fits");
        ContextMenu fits = new();
        foreach (var (text, target, tip) in new[] { ("Fit range", "range", "The playback range"), ("Fit run", "run", "Everything the run did, up to the playhead"), ("Fit selection", "selection", "The selected event's or lane's marks") })
        {
            MenuItem item = new() { Header = new TextBlock { Text = text }, ToolTip = tip };
            item.Click += (_, _) => Fit(target);
            fits.Items.Add(item);
        }
        more.Click += (_, _) => { fits.PlacementTarget = more; fits.IsOpen = true; };
        StackPanel tools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(segmented); tools.Children.Add(thresholdsToggle); tools.Children.Add(fit); tools.Children.Add(more);
        Tools = tools;
        ApplyView();
        SizeChanged += (_, _) => { ApplyScroll(); surface.InvalidateVisual(); header.InvalidateVisual(); PlaceExtend(); };
        surface.SizeChanged += (_, _) => PlaceExtend();
    }

    /// <summary>The ruler's "+5 s": a rounded, outlined pill that fills on hover and press, its outline the text colour with the keyboard focus.</summary>
    private static ControlTemplate RulerPill()
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        chrome.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        chrome.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorSecondaryBrush");
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        chrome.AppendChild(content);
        ControlTemplate template = new(typeof(Button)) { VisualTree = chrome };
        Trigger hover = new() { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ControlFillColorSecondaryBrush"), "Chrome"));
        Trigger pressed = new() { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ControlFillColorTertiaryBrush"), "Chrome"));
        Trigger focused = new() { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush"), "Chrome"));
        template.Triggers.Add(hover); template.Triggers.Add(pressed); template.Triggers.Add(focused);
        return template;
    }

    private static ToggleButton Segment(string text, string tip)
    {
        ToggleButton toggle = new() { Content = new TextBlock { Text = text, FontSize = 12.5 }, ToolTip = tip, Padding = new(10, 0, 10, 0) };
        toggle.SetResourceReference(StyleProperty, "SegmentOption");
        AutomationProperties.SetName(toggle, text);
        return toggle;
    }

    // ---------------------------------------------------------------- state and presentation

    internal AnimationTimingPalette Palette => AnimationTimingPalette.For(AnimationGraphPalette.IsDark(this));

    /// <summary>The ⚠ notice counts per lane (design §15).</summary>
    internal IReadOnlyDictionary<string, int> Notices { get; private set; } = new Dictionary<string, int>();
    /// <summary>
    /// Where an excluded call would stop, reset or invalidate a binding of the run in the game (design §15): a thin dashed marker
    /// on the affected binding's group at the estimated time, its words in the tooltip ("would be stopped by excluded …").
    /// </summary>
    internal sealed record ExcludedMarker(string Lane, double Seconds, string Label);
    internal IReadOnlyList<ExcludedMarker> Markers { get; private set; } = [];
    internal void SetNotices(IReadOnlyDictionary<string, int> notices, IReadOnlyList<ExcludedMarker>? markers = null)
    {
        markers ??= [];
        if (Notices.Count == notices.Count && notices.All(n => Notices.TryGetValue(n.Key, out int had) && had == n.Value) && Markers.SequenceEqual(markers)) return;
        Notices = notices; Markers = markers;
        surface.InvalidateVisual();
    }
    /// <summary>Shows a projection. <paramref name="viewKey"/> (the opened binding) resets the range to the playback range when it changes.</summary>
    internal void SetState(AnimationTimingState state, string viewKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool newKey = viewKey != key; key = viewKey;
        var previous = State;
        State = state;
        BuildRows();
        if (newKey) { rangeChosen = false; vertical.Value = 0; SelectedLane = SelectedMark = null; SelectedEvent = null; }
        if (!rangeChosen) SetVisibleRange(state.RangeStart, Math.Max(state.RangeStart + AnimationSeconds.Tick, state.RangeEnd), user: false);
        // While playing the range pages with the playhead; a seek elsewhere brings it into view. Without a chosen range the
        // view follows the playback range, paged to a playhead outside it (a seek past the range's end).
        if ((!rangeChosen || state.Playing || Math.Abs(state.Time - previous.Time) > 1e-9) && (state.Time < VisibleStart || state.Time > VisibleEnd))
        {
            double span = VisibleEnd - VisibleStart, begin = Math.Clamp(state.Time - span * .2, 0, AnimationSeconds.Maximum - span);
            SetVisibleRange(begin, begin + span, user: false);
        }
        if (SelectedLane != null && RowLane(SelectedLane) == null) { SelectedLane = null; SelectedMark = null; }
        string? text = state.Message ?? (state.Timing == null ? "Open an animation in Animation mode." : !state.Evaluated
            ? "Nothing has run yet. Play, or click the ruler to seek; the preview runs the scenario up to that time." : null);
        messageText.Text = text ?? ""; message.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        if (View == "log") RefreshLog();
        ApplyScroll();
        surface.Changed(); header.InvalidateVisual(); PlaceExtend();
    }
    /// <summary>The playhead alone moved (between projections while playing).</summary>
    internal void SetTime(double time)
    {
        State = State with { Time = time };
        if (State.Playing && (time < VisibleStart || time > VisibleEnd))
        {
            double span = VisibleEnd - VisibleStart, begin = Math.Clamp(time - span * .2, 0, AnimationSeconds.Maximum - span);
            SetVisibleRange(begin, begin + span, user: false);
        }
        surface.InvalidateVisual(); header.InvalidateVisual(); PlaceExtend();
    }

    internal void SetVisibleRange(double start, double end, bool user = true)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end)) throw new ArgumentException("Use finite seconds.");
        double span = Math.Clamp(end - start, AnimationSeconds.Tick * 6, AnimationSeconds.Maximum);
        start = Math.Clamp(start, 0, AnimationSeconds.Maximum - span);
        VisibleStart = start; VisibleEnd = start + span;
        if (user) rangeChosen = true;
        ApplyScroll();
        surface.InvalidateVisual(); header.InvalidateVisual(); PlaceExtend();
    }
    internal void SetLabelWidth(double width)
    {
        if (!double.IsFinite(width)) throw new ArgumentException("Use a finite width.");
        LabelWidth = Math.Clamp(width, MinimumLabelWidth, MaximumLabelWidth);
        grip.Margin = new(LabelWidth - 3, 0, 0, 0);
        surface.InvalidateVisual(); header.InvalidateVisual(); PlaceExtend();
    }
    internal void SetThresholds(bool value)
    {
        Thresholds = value;
        changing = true; try { thresholdsToggle.IsChecked = value; } finally { changing = false; }
        surface.InvalidateVisual();
    }
    internal void SetView(string value)
    {
        if (value is not ("lanes" or "log")) throw new ArgumentException("Choose lanes or log.");
        View = value; ApplyView();
    }
    private void ApplyView()
    {
        changing = true;
        try { lanesToggle.IsChecked = View == "lanes"; logToggle.IsChecked = View == "log"; }
        finally { changing = false; }
        lanesView.Visibility = View == "lanes" ? Visibility.Visible : Visibility.Collapsed;
        logView.Visibility = View == "log" ? Visibility.Visible : Visibility.Collapsed;
        thresholdsToggle.IsEnabled = View == "lanes";
        if (View == "log") RefreshLog();
        grip.Margin = new(LabelWidth - 3, 0, 0, 0);
        PlaceExtend();
    }

    /// <summary>Whether <see cref="Fit"/> has something to fit (a run for Fit run, marks of the selection for Fit selection).</summary>
    internal bool CanFit(string target) => target switch
    {
        "range" => true,
        "run" => Timing?.RunStart is double,
        "selection" => SelectionMarks().Any(),
        _ => throw new ArgumentException("Fit range, run or selection."),
    };
    /// <summary>Fit range (the playback range), Fit run (all evidence up to the playhead) or Fit selection.</summary>
    internal bool Fit(string target)
    {
        double begin, end;
        switch (target)
        {
            case "range": begin = State.RangeStart; end = State.RangeEnd; break;
            case "run":
                if (Timing?.RunStart is not double first) return false;
                begin = first; end = Math.Max(Timing.RunEnd ?? first, State.Time); break;
            case "selection":
                {
                    var marks = SelectionMarks().ToArray();
                    if (marks.Length == 0) return false;
                    begin = marks.Min(m => m.Start); end = marks.Max(m => m.End ?? m.Start); break;
                }
            default: throw new ArgumentException("Fit range, run or selection.");
        }
        double margin = Math.Max(.25, (end - begin) * .06);
        SetVisibleRange(Math.Max(0, begin - margin), Math.Min(AnimationSeconds.Maximum, end + margin), user: true);
        return true;
    }
    private IEnumerable<AnimationTimingMark> SelectionMarks()
    {
        if (Timing == null) yield break;
        foreach (var lane in Timing.Lanes)
            foreach (var mark in lane.Marks)
                if (SelectedEvent is Guid ev ? mark.Event == ev : lane.Id == SelectedLane) yield return mark;
    }

    /// <summary>Selects a lane (and the mark, or the event), scrolls it into view; raises nothing.</summary>
    internal void Select(string? lane, string? mark = null, Guid? ev = null)
    {
        SelectedLane = lane; SelectedMark = mark;
        SelectedEvent = ev ?? (mark != null && lane != null ? Timing?.Lane(lane)?.Marks.FirstOrDefault(m => m.Id == mark)?.Event : null);
        if (lane != null) RevealLane(lane);
        surface.Changed();
        if (View == "log" && mark != null && logItems.FirstOrDefault(i => i.Row.Mark == mark) is { } item) { changing = true; try { log.SelectedItem = item; log.ScrollIntoView(item); } finally { changing = false; } }
    }
    internal void RevealLane(string lane)
    {
        if (Timing == null) return;
        double top = 0;
        foreach (var row in Rows)
        {
            double height = TimingSurface.RowHeight(row);
            if (row.Id == lane)
            {
                double viewport = Math.Max(0, surface.ActualHeight);
                if (top < vertical.Value) vertical.Value = top;
                else if (top + height > vertical.Value + viewport) vertical.Value = Math.Max(0, top + height - viewport);
                return;
            }
            top += height;
        }
    }
    internal void RevealTime(double seconds)
    {
        if (seconds >= VisibleStart && seconds <= VisibleEnd) return;
        double span = VisibleEnd - VisibleStart, begin = Math.Clamp(seconds - span * .3, 0, AnimationSeconds.Maximum - span);
        SetVisibleRange(begin, begin + span, user: false);
    }

    private void ApplyScroll()
    {
        changing = true;
        try
        {
            double total = Rows.Sum(TimingSurface.RowHeight), viewport = Math.Max(0, surface.ActualHeight);
            vertical.Maximum = Math.Max(0, total - viewport); vertical.ViewportSize = viewport; vertical.LargeChange = Math.Max(24, viewport - 24);
            vertical.Visibility = total > viewport + .5 ? Visibility.Visible : Visibility.Collapsed;
            if (vertical.Value > vertical.Maximum) vertical.Value = vertical.Maximum;
            double span = VisibleEnd - VisibleStart;
            double extent = Math.Min(AnimationSeconds.Maximum, Math.Max(Math.Max(State.RangeEnd, Timing?.RunEnd ?? 0), Math.Max(VisibleEnd, State.Time)) + span * .5);
            horizontal.Maximum = Math.Max(0, extent - span); horizontal.ViewportSize = span; horizontal.LargeChange = span; horizontal.SmallChange = span / 10;
            horizontal.Value = Math.Clamp(VisibleStart, 0, horizontal.Maximum);
            horizontal.Margin = new(LabelWidth, 0, 0, 0);
        }
        finally { changing = false; }
    }
    internal void ScrollLanes(double delta) => vertical.Value = Math.Clamp(vertical.Value + delta, 0, vertical.Maximum);

    // ---------------------------------------------------------------- intents (from the surface, header and log)

    internal void RequestSeek(double seconds, bool final)
    {
        if (!State.CanSeek || !double.IsFinite(seconds)) return;
        SeekRequested?.Invoke(Math.Clamp(seconds, 0, AnimationSeconds.Maximum), final);
    }
    internal void ChooseLane(AnimationTimingLane lane) { if (lane.Id == DropLaneId) return; Select(lane.Id); LaneSelected?.Invoke(lane); }
    internal void ChooseMark(AnimationTimingLane lane, AnimationTimingMark mark) { Select(lane.Id, mark.Id); MarkSelected?.Invoke(lane, mark); }
    internal void Toggle(AnimationTimingLane group) { if (group.Kind == AnimationTimingLaneKind.Group) GroupToggled?.Invoke(group.Id, !group.Expanded); }
    internal void MoveStart(Guid id, double seconds) => StartMoved?.Invoke(id, seconds);
    internal void RemoveStart(Guid id) => StartRemoved?.Invoke(id);
    internal void ShowScenario() => ScenarioRequested?.Invoke();
    internal void ProposeThreshold(AnimationTimingMark mark, double seconds) => ThresholdProposed?.Invoke(mark, seconds);
    internal void EditTiming() => EditTimingRequested?.Invoke();
    internal bool CanEditThreshold(Guid ev) => State.CanEditThreshold?.Invoke(ev) == true;

    /// <summary>Ctrl+wheel zooms at <paramref name="fraction"/> of the clock's width; Shift+wheel pans; else the lanes scroll.</summary>
    internal bool Wheel(int delta, ModifierKeys modifiers, double fraction)
    {
        double span = VisibleEnd - VisibleStart;
        if (modifiers == ModifierKeys.Control)
        {
            double at = VisibleStart + span * Math.Clamp(fraction, 0, 1), next = Math.Clamp(span * (delta > 0 ? .8 : 1.25), AnimationSeconds.Tick * 6, AnimationSeconds.Maximum);
            double begin = at - next * Math.Clamp(fraction, 0, 1);
            SetVisibleRange(begin, begin + next); return true;
        }
        if (modifiers == ModifierKeys.Shift)
        {
            double begin = VisibleStart - Math.Sign(delta) * span * .1;
            SetVisibleRange(begin, begin + span); return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- the Log

    private void BuildLog()
    {
        GridView columns = new();
        void Column(string title, string path, double width, bool right = false)
        {
            GridViewColumn column = new() { Header = title, Width = width };
            if (right)
            {
                var factory = new FrameworkElementFactory(typeof(TextBlock));
                factory.SetBinding(TextBlock.TextProperty, new Binding(path));
                factory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
                factory.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
                factory.SetValue(FrameworkElement.MinWidthProperty, width - 20);
                column.CellTemplate = new DataTemplate { VisualTree = factory };
            }
            else column.DisplayMemberBinding = new Binding(path);
            columns.Columns.Add(column);
        }
        Column("Time", nameof(AnimationTimingLogItem.Time), 92, true);
        Column("Instance", nameof(AnimationTimingLogItem.Instance), 190);
        Column("Sequence", nameof(AnimationTimingLogItem.Sequence), 170);
        Column("Event", nameof(AnimationTimingLogItem.Event), 300);
        Column("Result", nameof(AnimationTimingLogItem.Result), 200);
        log.View = columns;
        log.FontSize = 12;
        // Compact rows (the Fluent default is taller than a Log needs).
        log.Loaded += (_, _) =>
        {
            if (log.ItemContainerStyle != null) return;
            var style = new Style(typeof(ListViewItem), log.TryFindResource(typeof(ListViewItem)) as Style);
            style.Setters.Add(new Setter(MinHeightProperty, 26d)); style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
            log.ItemContainerStyle = style;
        };
        VirtualizingPanel.SetIsVirtualizing(log, true); VirtualizingPanel.SetVirtualizationMode(log, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(log, true);
        log.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        AutomationProperties.SetName(log, "Timing log");
        log.SelectionChanged += (_, _) =>
        {
            if (changing || log.SelectedItem is not AnimationTimingLogItem item || Timing?.Lane(item.Row.Lane) is not { } lane) return;
            if (lane.Marks.FirstOrDefault(m => m.Id == item.Row.Mark) is { } mark) ChooseMark(lane, mark);
        };
        log.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F2) { EditTiming(); e.Handled = true; }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { CopyLog(); e.Handled = true; }
        };
        DockPanel footer = new() { Height = 34 };
        logFooter.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Button copy = new() { Content = new TextBlock { Text = "Copy", FontSize = 12.5 }, Height = 28, Padding = new(10, 0, 10, 0), Margin = new(6, 0, 0, 0), ToolTip = "Copy the selected rows (all rows when none is selected) as tab-separated text (Ctrl+C)" };
        copy.SetResourceReference(StyleProperty, "CommandButton"); copy.Width = double.NaN;
        AutomationProperties.SetName(copy, "Copy the log");
        copy.Click += (_, _) => CopyLog();
        followLatest.ToolTip = "Keep the newest row in view as the run advances";
        AutomationProperties.SetName(followLatest, "Follow latest");
        followLatest.Click += (_, _) => { if (followLatest.IsChecked == true && logItems.Length > 0) log.ScrollIntoView(logItems[^1]); };
        DockPanel.SetDock(copy, Dock.Left); footer.Children.Add(copy);
        DockPanel.SetDock(followLatest, Dock.Left); footer.Children.Add(followLatest);
        footer.Children.Add(logFooter);
        DockPanel.SetDock(footer, Dock.Bottom); logView.Children.Add(footer);
        logView.Children.Add(log);
    }
    private void RefreshLog()
    {
        var timing = Timing;
        if (ReferenceEquals(timing, logSource)) return;
        logSource = timing;
        var rows = timing?.Log ?? [];
        logItems = new AnimationTimingLogItem[rows.Count];
        for (int i = 0; i < rows.Count; i++) logItems[i] = new(rows[i]);
        changing = true;
        try
        {
            log.ItemsSource = logItems;
            if (SelectedMark != null && logItems.FirstOrDefault(i => i.Row.Mark == SelectedMark) is { } selected) log.SelectedItem = selected;
        }
        finally { changing = false; }
        logFooter.Text = timing == null ? "" : $"{rows.Count.ToString("N0", CultureInfo.InvariantCulture)} rows · {timing.Retained.ToString("N0", CultureInfo.InvariantCulture)} retained · {timing.Discarded.ToString("N0", CultureInfo.InvariantCulture)} discarded"
            + (timing.PhasesDropped > 0 ? $" · {timing.PhasesDropped.ToString("N0", CultureInfo.InvariantCulture)} older state changes discarded" : "")
            + (State.Stale ? " · stale" : "");
        logFooter.ToolTip = timing == null ? null : "Occurrences the run retained and discarded (the oldest go first)"
            + (timing.PhasesDropped > 0 ? "; older sequence state changes were discarded too, so bands before the oldest kept one may be missing" : "");
        if (followLatest.IsChecked == true && logItems.Length > 0) log.ScrollIntoView(logItems[^1]);
    }
    private void CopyLog()
    {
        var rows = log.SelectedItems.Count > 0 ? log.SelectedItems.OfType<AnimationTimingLogItem>().ToArray() : logItems;
        var text = new System.Text.StringBuilder("Time\tInstance\tSequence\tEvent\tResult\n");
        foreach (var row in rows.Take(100_000)) text.Append(row.Time).Append('\t').Append(row.Instance).Append('\t').Append(row.Sequence).Append('\t').Append(row.Event).Append('\t').Append(row.Result).Append('\n');
        try { Clipboard.SetText(text.ToString()); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }
}

/// <summary>A Log row as the list shows it.</summary>
internal sealed class AnimationTimingLogItem(AnimationTimingLogRow row)
{
    public AnimationTimingLogRow Row { get; } = row;
    public string Time => AnimationSeconds.Format(Row.Time);
    public string Instance => Row.Instance;
    public string Sequence => Row.Sequence;
    public string Event => Row.Event;
    public string Result => Row.Result;
    public override string ToString() => $"{Time}, {Instance}, {Sequence}, {Event}, {Result}";
}

/// <summary>The Timing view's colours (the mockup's band tokens beside the graph's palette), one frozen set per theme.</summary>
internal sealed class AnimationTimingPalette
{
    private static AnimationTimingPalette? dark, light;
    internal static AnimationTimingPalette For(bool isDark) => isDark ? dark ??= new(true) : light ??= new(false);
    public AnimationGraphPalette Graph { get; }
    public Brush Section, LaneSelected, LabelSelected, Range, BandBackground;
    public Pen Divider, Tick, Playhead, Bracket, BracketHandle, Excluded;
    public DrawingBrush Release, Threshold, Pause, Blocked;
    private AnimationTimingPalette(bool isDark)
    {
        Graph = AnimationGraphPalette.For(isDark);
        Brush B(uint argb) { var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)); brush.Freeze(); return brush; }
        Pen P(Brush brush, double thickness) { var pen = new Pen(brush, thickness); pen.Freeze(); return pen; }
        Section = isDark ? B(0x0AFFFFFF) : B(0xBFFFFFFF);
        LaneSelected = isDark ? B(0x0DFF9A4D) : B(0x0FB85406);
        LabelSelected = Graph.Selected;
        Range = B(0x1F7F7F7F);
        BandBackground = Graph.Pane;
        Divider = P(Graph.Divider, 1); Tick = P(Graph.T4, 1); Playhead = P(Graph.Accent, 1.5); Bracket = P(Graph.T3, 1.5); BracketHandle = P(Graph.Accent, 2);
        Excluded = new Pen(Graph.Caution, 1.25) { DashStyle = new DashStyle([3, 2], 0) }; Excluded.Freeze();
        Brush release = isDark ? B(0x427BD88F) : B(0x382F8A47), threshold = isDark ? B(0x38FFFFFF) : B(0x38000000), pause = isDark ? B(0x1FFFFFFF) : B(0x1A000000);
        Brush blocked = isDark ? B(0x55FF99A4) : B(0x45C42B1C);
        Release = Hatch(release, 2, 6, false); Blocked = Hatch(blocked, 2, 6, false); Pause = Hatch(pause, 2, 7, true);
        var dot = new GeometryDrawing(threshold, null, new EllipseGeometry(new Point(2.5, 2.5), 1, 1));
        Threshold = new DrawingBrush(dot) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 5, 5), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 5, 5), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        Threshold.Freeze();
    }
    /// <summary>Diagonal stripes: <paramref name="width"/> wide every <paramref name="period"/> DIP, "/" or "\".</summary>
    private static DrawingBrush Hatch(Brush brush, double width, double period, bool back)
    {
        var pen = new Pen(brush, width / Math.Sqrt(2) * 1.4) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square }; pen.Freeze();
        var line = back ? Geometry.Parse($"M0,0 L{period},{period} M-1,{period - 1} L1,{period + 1} M{period - 1},-1 L{period + 1},1")
            : Geometry.Parse($"M0,{period} L{period},0 M-1,1 L1,-1 M{period - 1},{period + 1} L{period + 1},{period - 1}");
        var drawing = new GeometryDrawing(null, pen, line);
        var hatch = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, period, period), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, period, period), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        hatch.Freeze(); return hatch;
    }
}
