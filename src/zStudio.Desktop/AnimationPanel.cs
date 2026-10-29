using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>What the transport shows: one snapshot of the opened binding's run, in seconds.</summary>
internal sealed record AnimationTransportState
{
    public bool Playing { get; init; }
    public bool CanPlay { get; init; }
    public bool CanSeek { get; init; }
    public bool CanStop { get; init; }
    public bool Busy { get; init; }
    public double Time { get; init; }
    public double RangeStart { get; init; }
    public double RangeEnd { get; init; } = 30;
    public double Speed { get; init; } = 1;
    public double Volume { get; init; } = .5;
    public bool Muted { get; init; }
    public bool Replay { get; init; }
    public bool FollowCamera { get; init; }
    /// <summary>"Not evaluated", "Evaluated 0–24.0 s · 136 occurrences", "Stale · from before your edit" or a failure.</summary>
    public string Status { get; init; } = "Not evaluated";
    public bool Failed { get; init; }
    /// <summary>Why Play is off (an open draft), shown in its tooltip.</summary>
    public string? PlayBlocked { get; init; }
    /// <summary>The range follows Auto (the measured run) rather than a Custom end.</summary>
    public bool RangeAuto { get; init; } = true;
    /// <summary>The range flyout's line: "Auto · 5 s after the last timed event (6.100 s) · pieces fall until they hit the ground".</summary>
    public string? RangeText { get; init; }
    /// <summary>Auto would change something (the range is Custom, or Auto's measurement is missing or timed out).</summary>
    public bool CanAuto { get; init; }
    /// <summary>Extend +5 s can lengthen the range (an opened binding, below 3,600 s, its Auto end measured).</summary>
    public bool CanExtend { get; init; }
    /// <summary>Auto's measurement has not arrived yet: the range shows no end ("of 0–… s").</summary>
    public bool RangePending { get; init; }
    /// <summary>Playback stopped at an end the 5 s rule (or Extend) set while the run goes on: "Ended 5 s after the last timed event · motion may continue".</summary>
    public string? Hint { get; init; }
}

/// <summary>The Auto range's words, shared by Animation mode and the compiled preview (design §13 Range).</summary>
internal static class AnimationRangeText
{
    /// <summary>What Extend +5 s adds to the range's end.</summary>
    internal const double ExtendSeconds = 5;
    /// <summary>The flyout's line: Auto with why it ends there, or Custom with where Auto would end.</summary>
    internal static string Describe(bool auto, double start, double end, AnimationDuration? measured, bool measuring, string? unmeasured = null)
    {
        string why = measured is { } d ? Lower(d.Explanation.TrimEnd('.')) : measuring ? "measuring…" : unmeasured ?? "not measured yet";
        if (auto) return "Auto · " + why;
        return $"Custom · {AnimationSeconds.Short(start).Replace(" s", "")}–{AnimationSeconds.Short(end)}" + (measured != null ? $" · Auto would end at {AnimationSeconds.Short(measured.Seconds)}: {why}" : measuring ? " · Auto is measuring…" : "");
    }
    /// <summary>The transport's hint when playback stops at an end the 5 s rule set (Auto) or Extend lengthened (Custom).</summary>
    internal static string? Hint(bool auto, bool extended, AnimationDuration? measured) =>
        auto ? measured?.AfterLastTimed == true ? "Ended 5 s after the last timed event · motion may continue" : null
             : extended ? "Ended at the extended range · motion may continue" : null;
    /// <summary>Whether Auto would change anything: a Custom range, or an Auto measurement missing, timed out or cut by its limit.</summary>
    internal static bool CanAuto(bool auto, AnimationDuration? measured, bool measuring) =>
        !auto || !measuring && measured is null or { TimedOut: true };
    private static string Lower(string text) => text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}

/// <summary>
/// Animation mode's panel under the 3D view (design/animation-redesign.md §7, §13): the one transport (40 DIP), the view bar
/// (34 DIP: Structure | Timing | Split and the shown view's tools) and the views. It shows snapshots and raises intents; the
/// window owns the run, the scenario and the source.
/// </summary>
internal sealed class AnimationPanel : Border
{
    internal const double MinimumPanelHeight = 240, SplitWidth = 1300, CompactWidth = 1040;
    private readonly Button play, stop, toStart, stepBack, stepForward, rangeButton, overflow, statusLink, extendLink;
    private readonly PreviewIcon playIcon = new() { Kind = "Play", Width = 16, Height = 16 };
    private readonly ValueTextBox readout = new() { Width = 92, Height = 30, MinHeight = 30, Padding = new(8, 0, 8, 0), TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock rangeText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox speed = new() { Width = 74, Height = 30, MinHeight = 30, ItemsSource = new[] { "0.25×", "0.5×", "1×", "2×" }, SelectedIndex = 2, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The Speed choices (MCP animation_options speed accepts exactly these).</summary>
    internal static readonly double[] SpeedChoices = [.25, .5, 1, 2];
    private static double[] Speeds => SpeedChoices;
    private readonly ToggleButton loop, mute, follow;
    private readonly Slider volume = new() { Minimum = 0, Maximum = 1, Value = .5, Width = 76, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 8, 0) };
    private readonly StackPanel secondary = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel transportLeft = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel overflowItems = new() { Margin = new(10) };
    private readonly Popup overflowPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly Popup rangePopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly ValueTextBox rangeStart = new() { Width = 80, Height = 30, MinHeight = 30, TextAlignment = TextAlignment.Right };
    private readonly ValueTextBox rangeEnd = new() { Width = 80, Height = 30, MinHeight = 30, TextAlignment = TextAlignment.Right };
    private readonly Button fitRange = new() { Content = "Fit range to starts", Margin = new(0, 6, 6, 0), ToolTip = "The range from 0 s to the latest start plus 30 s" };
    private readonly Button autoRange = new() { Content = "Auto", Margin = new(0, 6, 6, 0), ToolTip = "Auto: the range of the run (it ends where the run ends, where every loop has gone round once, or 5 s after the last timed event when something goes on without a timed end); measures again when needed" };
    private readonly Button extendRange = new() { Content = "Extend +5 s", Margin = new(0, 6, 6, 0), ToolTip = "Lengthen the range by 5 s (it becomes Custom until Auto)" };
    private readonly TextBlock rangeReason = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 296, Margin = new(0, 8, 0, 0) };
    private readonly TextBlock status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(8, 0, 4, 0) };
    private readonly ToggleButton structureView, timingView, splitView;
    private readonly ContentControl structureTools = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
    private readonly ContentControl tools = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
    private readonly Grid views = new();
    private readonly ColumnDefinition structureColumn = new(), splitterColumn = new() { Width = new(0) }, timingColumn = new();
    private readonly Border structureSlot = new(), timingSlot = new();
    private readonly GridSplitter viewSplitter = new() { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, Focusable = true };
    private readonly Border tip = new() { CornerRadius = new(6), BorderThickness = new(1), Padding = new(10, 6, 4, 6), Margin = new(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    private readonly Button noticesChip, legendButton;
    private readonly TextBlock noticesText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly AnimationGraphGlyph noticesGlyph;
    private bool approximationsOpen;
    private readonly Popup noticesPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly Popup legendPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private IReadOnlyList<AnimationNotice> notices = [];
    private readonly DockPanel transportRow;
    private readonly DockPanel viewBar;
    private readonly FrameworkElement segmented, chips;
    private readonly Button toolsMore;
    private readonly WrapPanel toolsOverflow = new() { Orientation = Orientation.Horizontal, MaxWidth = 420, Margin = new(8) };
    private readonly Popup toolsPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    /// <summary>The view tools moved to ⋯: each with the toolbar and position it came from.</summary>
    private readonly List<(Panel From, int At, UIElement Item)> overflowed = [];
    private bool overflowQueued;
    private bool changing, compact;
    private AnimationTransportState state = new();
    private string preferredView = "structure";
    private FrameworkElement? timingTools;

    /// <summary>play, pause, stop, start (range start), previous, next.</summary>
    internal event Action<string>? TransportRequested;
    internal event Action<double>? SeekRequested;
    /// <summary>speed, replay, mute, volume, followCamera, range (start, end).</summary>
    internal event Action<string, object>? OptionRequested;
    internal event Action<string>? ViewRequested;
    /// <summary>The range flyout's Fit range to starts, Auto and Extend +5 s (also the hint's Extend).</summary>
    internal event Action? FitRangeRequested, AutoRangeRequested, ExtendRequested, TipDismissed, DiagnosticsRequested;
    internal event Action<double>? SplitChanged;
    /// <summary>A notice's Go to: select its item in the graph (or its lane in Timing).</summary>
    internal event Action<AnimationNotice>? NoticeRequested;
    /// <summary>The notices shown in the view bar's chip (every notice of the opened animation).</summary>
    internal IReadOnlyList<AnimationNotice> Notices => notices;

    internal AnimationTransportState State => state;
    /// <summary>The chosen view (structure, timing or split); <see cref="ShownView"/> is what the width allows.</summary>
    internal string View => preferredView;
    internal string ShownView => preferredView == "split" && !SplitAvailable ? "timing" : preferredView;
    internal bool SplitAvailable => ActualWidth <= 0 || ActualWidth >= SplitWidth;
    internal bool Compact => compact;
    /// <summary>The transport row (layout checks).</summary>
    internal FrameworkElement TransportRow => transportRow;

    internal AnimationPanel()
    {
        SetResourceReference(StyleProperty, "SourcePane");
        AutomationProperties.SetName(this, "Animation panel");
        // The transport row: Play/Pause, Stop, range start, step back and forward a tick, the time, the range; then speed,
        // loop range, sound and Follow camera (in ⋯ when narrow); the run's status at the right.
        play = IconButton("Play", "Play (Space)", () => TransportRequested?.Invoke(state.Playing ? "pause" : "play")); play.Content = playIcon;
        stop = IconButton("Stop", "Stop: the static map at 0 s; the scenario stays", () => TransportRequested?.Invoke("stop"));
        toStart = IconButton("ToStart", "Go to the range start (Home)", () => TransportRequested?.Invoke("start"));
        stepBack = IconButton("StepBack", "Back one tick, 1/60 s (,)", () => TransportRequested?.Invoke("previous"));
        stepForward = IconButton("StepForward", "Forward one tick, 1/60 s (.)", () => TransportRequested?.Invoke("next"));
        AutomationProperties.SetName(stop, "Stop"); AutomationProperties.SetName(toStart, "Go to range start");
        AutomationProperties.SetName(stepBack, "Step back one tick"); AutomationProperties.SetName(stepForward, "Step forward one tick");
                System.Windows.Documents.Typography.SetNumeralAlignment(readout, FontNumeralAlignment.Tabular);
        readout.ToolTip = "The playhead in seconds. Type a time and press Enter to seek; the preview evaluates in 1/60 s ticks.";
        AutomationProperties.SetName(readout, "Playhead seconds");
        readout.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitReadout(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ShowReadout(); Keyboard.ClearFocus(); e.Handled = true; }
        };
        readout.GotKeyboardFocus += (_, _) => { readout.Text = AnimationSeconds.Value(state.Time).Replace(",", ""); readout.SelectAll(); };
        readout.LostKeyboardFocus += (_, _) => ShowReadout();
        rangeText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        rangeButton = new() { Content = rangeText, Height = 30, Padding = new(6, 0, 6, 0), Margin = new(2, 0, 4, 0), ToolTip = "The playback range: why Auto ends where it does; Extend +5 s, Auto, or edit its start and end" };
        rangeButton.SetResourceReference(StyleProperty, "CommandButton"); rangeButton.Width = double.NaN;
        AutomationProperties.SetName(rangeButton, "Playback range");
        rangeButton.Click += (_, _) => { rangeStart.Text = Number(state.RangeStart); rangeEnd.Text = RangeEndText(state); rangePopup.IsOpen = true; };
        BuildRangeFlyout();
        foreach (var element in new UIElement[] { play, stop, toStart, stepBack, stepForward }) transportLeft.Children.Add(element);
        transportLeft.Children.Add(Gap(6)); transportLeft.Children.Add(readout); transportLeft.Children.Add(rangeButton);

        var speedLabel = Label("Speed");
        AutomationProperties.SetName(speed, "Speed"); speed.ToolTip = "Playback speed";
        speed.SelectionChanged += (_, _) => { if (!changing && speed.SelectedIndex >= 0) OptionRequested?.Invoke("speed", Speeds[speed.SelectedIndex]); };
        loop = TextToggle("Loop", "Loop range", "Loop range: replay the playback range (authored loops are separate)");
        // The toggles apply through their checked state, so UI Automation's Toggle works as a click does.
        Toggled(loop, "replay");
        mute = new() { Content = new PreviewIcon { Kind = "Volume", Width = 16, Height = 16 }, ToolTip = "Mute" };
        mute.SetResourceReference(StyleProperty, "CommandButton"); AutomationProperties.SetName(mute, "Mute");
        Toggled(mute, "mute");
        AutomationProperties.SetName(volume, "Volume"); volume.ToolTip = "Volume";
        volume.ValueChanged += (_, _) => { if (!changing) OptionRequested?.Invoke("volume", volume.Value); };
        follow = TextToggle("Camera", "Follow camera", "Follow camera: the view follows the animation's camera; navigating turns it off");
        Toggled(follow, "followCamera");
        foreach (var element in new UIElement[] { Divider(), speedLabel, speed, Divider(), loop, Divider(), mute, volume, Divider(), follow }) secondary.Children.Add(element);
        overflow = IconButton("More", "More transport controls", () => overflowPopup.IsOpen = true);
        AutomationProperties.SetName(overflow, "More transport controls"); overflow.Visibility = Visibility.Collapsed;
        overflowPopup.PlacementTarget = overflow;
        overflowPopup.Child = Flyout(overflowItems);
        KeyboardFlyout(overflowPopup, overflow);
        transportLeft.Children.Add(secondary); transportLeft.Children.Add(overflow);
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        statusLink = new() { Content = new TextBlock { Text = "Show in Diagnostics", FontSize = 12 }, Height = 26, Padding = new(6, 0, 6, 0), Visibility = Visibility.Collapsed, ToolTip = "Show the Diagnostics strip under the panel" };
        statusLink.SetResourceReference(StyleProperty, "CommandButton"); statusLink.Width = double.NaN;
        statusLink.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        statusLink.Click += (_, _) => DiagnosticsRequested?.Invoke();
        // The hint when playback reaches an end the 5 s rule set: "Ended 5 s after the last timed event · motion may continue · Extend +5 s".
        extendLink = new() { Content = new TextBlock { Text = "Extend +5 s", FontSize = 12 }, Height = 26, Padding = new(6, 0, 6, 0), Visibility = Visibility.Collapsed, ToolTip = "Lengthen the range by 5 s to see what the run does next (it becomes Custom until Auto)" };
        extendLink.SetResourceReference(StyleProperty, "CommandButton"); extendLink.Width = double.NaN;
        extendLink.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        AutomationProperties.SetName(extendLink, "Extend the range by 5 s");
        extendLink.Click += (_, _) => ExtendRequested?.Invoke();
        DockPanel transport = new() { Height = 40, Margin = new(8, 0, 8, 0), LastChildFill = true };
        transportRow = transport;
        // The status takes what the controls leave: right-aligned, trimmed at its end, the whole text in its tooltip.
        Grid right = new() { VerticalAlignment = VerticalAlignment.Center };
        right.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); right.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); right.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        status.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(status); Grid.SetColumn(extendLink, 1); right.Children.Add(extendLink); Grid.SetColumn(statusLink, 2); right.Children.Add(statusLink);
        DockPanel.SetDock(transportLeft, Dock.Left); transport.Children.Add(transportLeft);
        transport.Children.Add(right);

        // The view bar: Structure | Timing | Split, then the shown view's tools.
        structureView = ViewToggle("Structure", "Structure", "Structure: what can happen and what causes what (Ctrl+1)");
        timingView = ViewToggle("Timing", "Timing", "Timing: what happened when, on the shared clock (Ctrl+2)");
        splitView = ViewToggle("Split", "Split", "Structure and Timing side by side (Ctrl+3; needs 1,300 DIP)");
        Border segmented = new() { CornerRadius = new(5), BorderThickness = new(1), Padding = new(2), VerticalAlignment = VerticalAlignment.Center };
        segmented.SetResourceReference(BackgroundProperty, "ControlFillColorTertiaryBrush");
        segmented.SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        StackPanel switcher = new() { Orientation = Orientation.Horizontal };
        switcher.Children.Add(structureView); switcher.Children.Add(timingView); switcher.Children.Add(splitView);
        segmented.Child = switcher;
        AutomationProperties.SetName(segmented, "Animation view");
        DockPanel viewBar = new() { Height = 34, Margin = new(8, 0, 8, 2) };
        DockPanel.SetDock(segmented, Dock.Left); viewBar.Children.Add(segmented);
        // At the right: the notices chip (a count, its flyout with Go to and Show in Diagnostics) and the legend.
        StackPanel chips = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        StackPanel noticeContent = new() { Orientation = Orientation.Horizontal };
        noticesGlyph = new AnimationGraphGlyph { Glyph = "warning", Size = 13, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        noticesGlyph.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "SystemFillColorCautionBrush");
        noticeContent.Children.Add(noticesGlyph); noticeContent.Children.Add(noticesText);
        noticesChip = new() { Content = noticeContent, Height = 24, Padding = new(9, 0, 10, 0), Margin = new(0, 0, 6, 0), Visibility = Visibility.Collapsed };
        noticesChip.SetResourceReference(StyleProperty, "ScenarioChip"); noticesChip.Height = 24;
        noticesChip.SetResourceReference(BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
        noticesChip.Click += (_, _) => ShowNotices();
        noticesPopup.PlacementTarget = noticesChip;
        legendButton = new() { Content = new AnimationGraphGlyph { Glyph = "legend", Size = 15 }, ToolTip = "Legend: edges, marks, states and families" };
        legendButton.SetResourceReference(StyleProperty, "CommandButton"); legendButton.Width = 30; legendButton.Height = 28;
        legendButton.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetName(legendButton, "Legend");
        legendButton.Click += (_, _) => { legendPopup.Child = AnimationLegend.Create(AnimationGraphPalette.IsDark(this)); legendPopup.IsOpen = true; };
        legendPopup.PlacementTarget = legendButton; legendPopup.HorizontalOffset = -300;
        chips.Children.Add(noticesChip); chips.Children.Add(legendButton);
        DockPanel.SetDock(chips, Dock.Right); viewBar.Children.Add(chips);
        // The view's tools take what is left: the tools that do not fit go, in order, to ⋯ beside the notices chip.
        toolsMore = IconButton("More", "More view tools", () => toolsPopup.IsOpen = true);
        AutomationProperties.SetName(toolsMore, "More view tools"); toolsMore.Visibility = Visibility.Collapsed;
        toolsPopup.PlacementTarget = toolsMore; toolsPopup.Child = Flyout(toolsOverflow);
        KeyboardFlyout(toolsPopup, toolsMore);
        DockPanel.SetDock(toolsMore, Dock.Right); viewBar.Children.Add(toolsMore);
        StackPanel viewTools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        viewTools.Children.Add(structureTools); viewTools.Children.Add(tools);
        viewBar.Children.Add(viewTools);
        this.viewBar = viewBar; this.segmented = segmented; this.chips = chips;
        viewBar.SizeChanged += (_, _) => QueueToolsOverflow();
        chips.SizeChanged += (_, _) => QueueToolsOverflow();

        // The views: Structure (left, 58 % in Split) and Timing.
        structureColumn.Width = new(58, GridUnitType.Star); timingColumn.Width = new(42, GridUnitType.Star);
        views.ColumnDefinitions.Add(structureColumn); views.ColumnDefinitions.Add(splitterColumn); views.ColumnDefinitions.Add(timingColumn);
        structureSlot.Background = Brushes.Transparent;
        views.Children.Add(structureSlot);
        Grid.SetColumn(viewSplitter, 1); views.Children.Add(viewSplitter);
        AutomationProperties.SetName(viewSplitter, "Resize Structure and Timing");
        viewSplitter.DragCompleted += (_, _) => { double total = structureColumn.ActualWidth + timingColumn.ActualWidth; if (total > 0) SplitChanged?.Invoke(structureColumn.ActualWidth / total); };
        Grid.SetColumn(timingSlot, 2); views.Children.Add(timingSlot);
        tip.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        tip.SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        Grid.SetColumnSpan(tip, 3); views.Children.Add(tip);
        Panel.SetZIndex(tip, 5);
        BuildTip();

        Grid layout = new();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        layout.Children.Add(transport);
        Border line = new() { Height = 1, Margin = new(0) }; line.SetResourceReference(BackgroundProperty, "DividerStrokeColorDefaultBrush");
        Grid.SetRow(line, 1); layout.Children.Add(line);
        Grid.SetRow(viewBar, 2); layout.Children.Add(viewBar);
        Border clip = new() { Child = views, ClipToBounds = true, Margin = new(1, 0, 1, 1), CornerRadius = new(0, 0, 8, 8) };
        Grid.SetRow(clip, 3); layout.Children.Add(clip);
        Child = layout;
        SizeChanged += (_, _) => ApplyWidth();
        ApplyView(); SetState(state);
    }

    // ---------------------------------------------------------------- state

    internal void SetState(AnimationTransportState value)
    {
        ArgumentNullException.ThrowIfNull(value);
        state = value; changing = true;
        try
        {
            playIcon.Kind = value.Playing ? "Pause" : "Play";
            string playTip = value.Playing ? "Pause (Space)" : value.PlayBlocked ?? "Play (Space)";
            play.ToolTip = playTip; AutomationProperties.SetName(play, value.Playing ? "Pause" : "Play");
            play.IsEnabled = value.CanPlay || value.Playing; stop.IsEnabled = value.CanStop;
            toStart.IsEnabled = stepBack.IsEnabled = stepForward.IsEnabled = readout.IsEnabled = value.CanSeek;
            if (!readout.IsKeyboardFocusWithin) ShowReadout();
            speed.SelectedIndex = Array.IndexOf(Speeds, value.Speed) is int index and >= 0 ? index : -1;
            loop.IsChecked = value.Replay; mute.IsChecked = value.Muted; follow.IsChecked = value.FollowCamera;
            ((PreviewIcon)mute.Content).Kind = value.Muted ? "Mute" : "Volume";
            mute.ToolTip = value.Muted ? "Unmute" : "Mute";
            if (Math.Abs(volume.Value - value.Volume) > 1e-9) volume.Value = Math.Clamp(value.Volume, 0, 1);
            // A failure outranks the hint; the hint outranks the run's coverage.
            bool hint = value.Hint != null && !value.Failed && !value.Busy;
            string shown = value.Busy ? "Preparing…" : hint ? value.Hint! : value.Status;
            status.Text = shown; status.ToolTip = hint ? value.Hint + " · Extend +5 s lengthens the range" : value.Status;
            if (value.Failed) status.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            else if (hint) status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            else status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            statusLink.Visibility = value.Failed ? Visibility.Visible : Visibility.Collapsed;
            extendLink.Visibility = hint && value.CanExtend ? Visibility.Visible : Visibility.Collapsed;
            // While Auto measures, the range shows no end (never a provisional or earlier one).
            rangeText.Text = $"of {Short(value.RangeStart)}–{RangeEndText(value)} s";
            rangeButton.ToolTip = (value.RangeText ?? (value.RangeAuto ? "Auto" : "Custom")) + "\nThe playback range: Extend +5 s, Auto, or edit its start and end";
            AutomationProperties.SetHelpText(rangeButton, value.RangeText ?? "");
            rangeReason.Text = value.RangeText ?? (value.RangeAuto ? "Auto" : "Custom");
            // An open flyout follows a range that changes under it (Extend, Auto, a measurement), never a box being typed in.
            if (rangePopup.IsOpen)
            {
                if (!rangeStart.IsKeyboardFocusWithin) rangeStart.Text = Number(value.RangeStart);
                if (!rangeEnd.IsKeyboardFocusWithin) rangeEnd.Text = RangeEndText(value);
            }
            autoRange.IsEnabled = value.CanAuto; extendRange.IsEnabled = value.CanExtend;
        }
        finally { changing = false; }
    }
    /// <summary>The flyout's Fit range to starts: a map's scenario has several starts; a compiled preview plays one from 0 s.</summary>
    internal bool FitRangeShown { get => fitRange.Visibility == Visibility.Visible; set => fitRange.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    private void ShowReadout() => readout.Text = AnimationSeconds.Format(state.Time);
    private void CommitReadout()
    {
        string text = readout.Text.Trim().TrimEnd('s', 'S').Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds))
            SeekRequested?.Invoke(Math.Clamp(seconds, 0, AnimationSeconds.Maximum));
        else ShowReadout();
    }
    private static string Short(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Number(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
    /// <summary>The range's end, or "…" while Auto measures it.</summary>
    private static string RangeEndText(AnimationTransportState value) => value.RangePending ? "…" : Number(value.RangeEnd);

    /// <summary>Hosts the Structure view (the graph) and its tools.</summary>
    internal void SetStructure(FrameworkElement structure, FrameworkElement? structureToolbar)
    {
        RestoreToolsOverflow();
        if (structure.Parent is Decorator old) old.Child = null;
        if (structureToolbar?.Parent is ContentControl host) host.Content = null;
        structureSlot.Child = structure; structureTools.Content = structureToolbar;
        ApplyView();
    }

    /// <summary>Hosts the Timing view (the timeline) and its tools.</summary>
    internal void SetTiming(FrameworkElement timing, FrameworkElement? timingToolbar)
    {
        RestoreToolsOverflow();
        if (timing.Parent is Decorator old) old.Child = null;
        timingSlot.Child = timing; timingTools = timingToolbar;
        ApplyView();
    }

    internal void SetView(string view)
    {
        if (view is not ("structure" or "timing" or "split")) throw new ArgumentException("Choose structure, timing or split.");
        preferredView = view; ApplyView();
    }
    internal void SetSplit(double structureShare)
    {
        double share = double.IsFinite(structureShare) ? Math.Clamp(structureShare, .2, .8) : .58;
        structureColumn.Width = new(share, GridUnitType.Star); timingColumn.Width = new(1 - share, GridUnitType.Star);
    }
    internal double SplitShare => structureColumn.Width.Value / Math.Max(1e-9, structureColumn.Width.Value + timingColumn.Width.Value);

    private void ApplyView()
    {
        string shown = ShownView;
        changing = true;
        try
        {
            structureView.IsChecked = shown == "structure"; timingView.IsChecked = shown == "timing"; splitView.IsChecked = shown == "split";
        }
        finally { changing = false; }
        splitView.Visibility = SplitAvailable ? Visibility.Visible : Visibility.Collapsed;
        bool structure = shown is "structure" or "split", timing = shown is "timing" or "split";
        structureSlot.Visibility = structure ? Visibility.Visible : Visibility.Collapsed;
        timingSlot.Visibility = timing ? Visibility.Visible : Visibility.Collapsed;
        viewSplitter.Visibility = shown == "split" ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(structureSlot, 0); Grid.SetColumnSpan(structureSlot, shown == "split" ? 1 : 3);
        Grid.SetColumn(timingSlot, shown == "split" ? 2 : 0); Grid.SetColumnSpan(timingSlot, shown == "split" ? 1 : 3);
        splitterColumn.Width = new(shown == "split" ? 6 : 0);
        if (shown != "split") { structureColumn.MinWidth = timingColumn.MinWidth = 0; }
        else { structureColumn.MinWidth = 320; timingColumn.MinWidth = 320; }
        var shownTools = timing ? timingTools : null;
        var structureShown = structure ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(tools.Content, shownTools) || structureTools.Visibility != structureShown)
        {
            RestoreToolsOverflow();
            tools.Content = shownTools; structureTools.Visibility = structureShown;
        }
        QueueToolsOverflow();
    }

    /// <summary>The view tools are fitted after the next layout pass (a width, the shown view or the notices chip changed).</summary>
    private void QueueToolsOverflow()
    {
        if (overflowQueued) return;
        overflowQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { overflowQueued = false; ApplyToolsOverflow(); });
    }
    private bool overflowOnClose;
    private void QueueToolsOverflowOnClose()
    {
        if (overflowOnClose) return;
        overflowOnClose = true;
        toolsPopup.Closed += Closed;
        void Closed(object? sender, EventArgs e) { toolsPopup.Closed -= Closed; overflowOnClose = false; QueueToolsOverflow(); }
    }
    /// <summary>The tools moved to ⋯ go back to their toolbars, in their places.</summary>
    private void RestoreToolsOverflow()
    {
        if (overflowed.Count == 0) return;
        toolsPopup.IsOpen = false;
        foreach (var (from, at, item) in overflowed.OrderBy(o => o.At))
        {
            toolsOverflow.Children.Remove(item);
            from.Children.Insert(Math.Min(at, from.Children.Count), item);
        }
        overflowed.Clear(); toolsMore.Visibility = Visibility.Collapsed;
    }
    /// <summary>
    /// Fits the shown view's tools between the view switch and the notices chip: those that do not fit go, in order, to ⋯
    /// (a narrow window, a compiled preview's command groups), so no tool is ever cut off.
    /// </summary>
    private void ApplyToolsOverflow()
    {
        // An open ⋯ flyout keeps its tools until it closes.
        if (toolsPopup.IsOpen) { QueueToolsOverflowOnClose(); return; }
        RestoreToolsOverflow();
        if (viewBar.ActualWidth <= 0) return;
        double available = viewBar.ActualWidth - segmented.ActualWidth - chips.ActualWidth - 16;
        var bars = new[] { structureTools.Visibility == Visibility.Visible ? structureTools.Content : null, tools.Content }
            .OfType<Panel>().ToArray();
        var items = bars.SelectMany(bar => bar.Children.Cast<UIElement>().Select((item, at) => (Bar: bar, At: at, Item: item)))
            .Where(i => i.Item.Visibility != Visibility.Collapsed).ToArray();
        double Width(UIElement item) { item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return item.DesiredSize.Width; }
        double total = items.Sum(i => Width(i.Item)) + bars.Length * 8;
        if (total <= available) return;
        // Keep what fits beside ⋯; the rest (and a divider left at the end) moves.
        double room = available - 36, used = 0;
        int keep = 0;
        for (Panel? bar = null; keep < items.Length; keep++)
        {
            double width = Width(items[keep].Item) + (items[keep].Bar != bar ? 8 : 0);
            if (used + width > room) break;
            used += width; bar = items[keep].Bar;
        }
        while (keep > 0 && items[keep - 1].Item is Border { Width: <= 2 }) keep--;
        foreach (var (bar, at, item) in items.Skip(keep).Reverse())
        {
            bar.Children.Remove(item);
            overflowed.Add((bar, at, item));
        }
        foreach (var (_, _, item) in overflowed.AsEnumerable().Reverse()) toolsOverflow.Children.Add(item);
        toolsMore.Visibility = overflowed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyWidth()
    {
        bool narrow = ActualWidth > 0 && ActualWidth < CompactWidth;
        if (narrow != compact)
        {
            compact = narrow;
            // Narrow: speed, loop range, sound and Follow camera go to ⋯ (design §7 "Narrow window").
            if (compact) { transportLeft.Children.Remove(secondary); overflowItems.Children.Add(secondary); secondary.Orientation = Orientation.Horizontal; overflow.Visibility = Visibility.Visible; }
            else { overflowPopup.IsOpen = false; overflowItems.Children.Remove(secondary); transportLeft.Children.Insert(transportLeft.Children.IndexOf(overflow), secondary); overflow.Visibility = Visibility.Collapsed; }
        }
        ApplyView();
    }

    // ---------------------------------------------------------------- focus (F6)

    /// <summary>Where the keyboard focus is in the panel: "transport", "view" or null.</summary>
    internal string? FocusRegion => transportRow.IsKeyboardFocusWithin ? "transport" : IsKeyboardFocusWithin ? "view" : null;
    internal bool FocusTransport() => transportRow.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    /// <summary>The shown view: Structure (its graph) or Timing (its lanes).</summary>
    internal bool FocusView()
    {
        var slot = ShownView == "timing" ? timingSlot : structureSlot;
        if (slot.Child is UIElement { Focusable: true } view && view.Focus()) return true;
        return slot.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    // ---------------------------------------------------------------- notices

    /// <summary>
    /// The opened animation's notices: the chip counts those about its items (as their badges and cards do); the preview's
    /// generic approximations are one collapsed line in the flyout (and the chip's count when there is nothing else: ⓘ n).
    /// Hidden when there are none.
    /// </summary>
    internal void SetNotices(IReadOnlyList<AnimationNotice> shown)
    {
        // The run refreshes this many times a second: an open flyout is rebuilt only when what it lists changed (its scroll
        // position, an open approximations line and a hovered Go to stay otherwise).
        if (ReferenceEquals(shown, notices) || shown.SequenceEqual(notices)) return;
        notices = shown;
        int specific = shown.Count(n => !n.Generic), generic = GenericNotices(shown).Count;
        noticesChip.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        noticesText.Text = (specific > 0 ? specific : generic).ToString(CultureInfo.CurrentCulture);
        noticesGlyph.Glyph = specific > 0 ? "warning" : "legend"; noticesGlyph.InvalidateVisual();
        noticesGlyph.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, specific > 0 ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        noticesChip.SetResourceReference(BackgroundProperty, specific > 0 ? "SystemFillColorCautionBackgroundBrush" : "ControlFillColorSecondaryBrush");
        string summary = string.Join("; ", shown.Where(n => !n.Generic).Select(n => n.Kind switch
        {
            "ignoredPrerequisites" => "ignored prerequisites", "refused" => "refused starts", "approximation" => "approximations", "unavailable" => "unavailable calls",
            "limit" => "preview limits", "support" => "support notes", _ => "preview notes",
        }).Distinct().Append(generic > 0 ? (generic == 1 ? "1 preview approximation" : generic.ToString(CultureInfo.CurrentCulture) + " preview approximations") : null).OfType<string>());
        noticesChip.ToolTip = "Notices of this animation: " + summary;
        AutomationProperties.SetName(noticesChip, specific > 0 ? "Notices of this animation, " + specific.ToString(CultureInfo.CurrentCulture) : "Preview approximations of this animation, " + generic.ToString(CultureInfo.CurrentCulture));
        if (noticesPopup.IsOpen) ShowNotices();
    }
    /// <summary>The generic approximations, one per text (however many items they came from).</summary>
    private static List<AnimationNotice> GenericNotices(IReadOnlyList<AnimationNotice> shown) =>
        [.. shown.Where(n => n.Generic).GroupBy(n => n.Text).Select(g => g.First() with { Count = g.Sum(n => n.Count) })];
    private void ShowNotices()
    {
        var specific = notices.Where(n => !n.Generic).ToArray();
        var generic = GenericNotices(notices);
        StackPanel body = new() { Margin = new(12, 10, 12, 10), Width = 380 };
        body.Children.Add(new TextBlock { Text = specific.Length == 1 ? "1 notice of this animation" : specific.Length.ToString(CultureInfo.CurrentCulture) + " notices of this animation", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) });
        StackPanel list = new();
        foreach (var notice in specific.Take(64))
        {
            DockPanel row = new() { Margin = new(0, 2, 0, 6) };
            StackPanel actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            if (notice.Row != null || notice.Node != null || notice.Lane != null)
            {
                Button go = new() { Content = new TextBlock { Text = "Go to", FontSize = 12 }, Height = 24, Padding = new(6, 0, 6, 0), ToolTip = "Select its item in the graph (or its lane in Timing)" };
                go.SetResourceReference(StyleProperty, "CommandButton"); go.Width = double.NaN; go.Height = 24;
                go.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                AutomationProperties.SetName(go, "Go to: " + notice.Text);
                go.Click += (_, _) => { noticesPopup.IsOpen = false; NoticeRequested?.Invoke(notice); };
                actions.Children.Add(go);
            }
            DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            var glyph = new AnimationGraphGlyph { Glyph = notice.Severity == "Information" ? "legend" : "warning", Size = 13, Margin = new(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            glyph.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, notice.Severity == "Information" ? "TextFillColorTertiaryBrush" : "SystemFillColorCautionBrush");
            row.Children.Add(glyph);
            TextBlock text = new() { Text = notice.Text + (notice.Count > 1 ? " (×" + notice.Count.ToString("N0", CultureInfo.CurrentCulture) + ")" : "") + notice.Where, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            row.Children.Add(text);
            list.Children.Add(row);
        }
        if (specific.Length > 64) list.Children.Add(new TextBlock { Text = "and " + (specific.Length - 64).ToString("N0", CultureInfo.CurrentCulture) + " more (animation_runtime notices lists them all)", FontSize = 11.5, Opacity = .7 });
        if (generic.Count > 0)
        {
            // The preview's generic approximations: one collapsed line, its lines on demand.
            StackPanel lines = new() { Visibility = approximationsOpen ? Visibility.Visible : Visibility.Collapsed, Margin = new(20, 2, 0, 4) };
            foreach (var notice in generic.Take(32))
                lines.Children.Add(new TextBlock { Text = notice.Text + (notice.Count > 1 ? " (×" + notice.Count.ToString("N0", CultureInfo.CurrentCulture) + ")" : ""), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 1, 0, 3), Opacity = .8 });
            string label = generic.Count == 1 ? "1 preview approximation" : generic.Count.ToString(CultureInfo.CurrentCulture) + " preview approximations";
            TextBlock caption = new() { Text = label + (approximationsOpen ? " ▾" : " ▸"), FontSize = 12 };
            Button toggle = new() { Content = caption, HorizontalAlignment = HorizontalAlignment.Left, Padding = new(6, 0, 8, 0), Margin = new(-6, 2, 0, 0),
                ToolTip = "How the preview approximates the game for this animation's kinds of events (not about one item)" };
            toggle.SetResourceReference(StyleProperty, "RowLinkButton"); toggle.Width = double.NaN; toggle.Height = 26; toggle.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetName(toggle, label);
            toggle.Click += (_, _) => { approximationsOpen = !approximationsOpen; lines.Visibility = approximationsOpen ? Visibility.Visible : Visibility.Collapsed; caption.Text = label + (approximationsOpen ? " ▾" : " ▸"); };
            list.Children.Add(toggle); list.Children.Add(lines);
        }
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Button diagnostics = new() { Content = "Show in Diagnostics", Margin = new(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Show the Diagnostics strip under the panel" };
        AutomationProperties.SetName(diagnostics, "Show in Diagnostics");
        diagnostics.Click += (_, _) => { noticesPopup.IsOpen = false; DiagnosticsRequested?.Invoke(); };
        body.Children.Add(diagnostics);
        var frame = Flyout(body);
        AutomationProperties.SetName(frame, "Notices of this animation");
        noticesPopup.Child = frame; noticesPopup.IsOpen = true;
    }

    // ---------------------------------------------------------------- the tip

    /// <summary>The first-entry tip ("Animation mode keeps the map where it is…"), until it is dismissed.</summary>
    internal bool TipShown { get => tip.Visibility == Visibility.Visible; set => tip.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    private void BuildTip()
    {
        DockPanel content = new();
        Button close = new() { Content = new PreviewIcon { Kind = "Close", Width = 12, Height = 12 }, ToolTip = "Dismiss", Width = 26, Height = 26, Margin = new(8, 0, 0, 0) };
        close.SetResourceReference(StyleProperty, "CardIconButton"); AutomationProperties.SetName(close, "Dismiss the Animation mode tip");
        close.Click += (_, _) => { TipShown = false; TipDismissed?.Invoke(); };
        DockPanel.SetDock(close, Dock.Right); content.Children.Add(close);
        var info = new PreviewIcon { Kind = "Info", Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        info.SetResourceReference(PreviewIcon.ForegroundProperty, "TextFillColorSecondaryBrush");
        content.Children.Add(info);
        TextBlock text = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        text.Inlines.Add(new System.Windows.Documents.Run("Animation mode") { FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new System.Windows.Documents.Run(" keeps the map where it is. Exit returns to it; the scenario is kept."));
        content.Children.Add(text);
        tip.Child = content;
        AutomationProperties.SetName(tip, "Animation mode tip");
    }

    // ---------------------------------------------------------------- building

    private void BuildRangeFlyout()
    {
        StackPanel body = new() { Margin = new(12), Width = 296 };
        body.Children.Add(new TextBlock { Text = "Playback range", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 4) });
        // Why the range ends where it does (Auto), or where Auto would end (Custom).
        rangeReason.Margin = new(0, 0, 0, 10);
        rangeReason.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetName(rangeReason, "Why the range ends there");
        body.Children.Add(rangeReason);
        Grid fields = new(); fields.ColumnDefinitions.Add(new()); fields.ColumnDefinitions.Add(new() { Width = new(12) }); fields.ColumnDefinitions.Add(new());
        fields.Children.Add(Field("Start", rangeStart, "s"));
        var end = Field("End", rangeEnd, "s"); Grid.SetColumn(end, 2); fields.Children.Add(end);
        AutomationProperties.SetName(rangeStart, "Playback range start seconds"); AutomationProperties.SetName(rangeEnd, "Playback range end seconds");
        body.Children.Add(fields);
        void Commit()
        {
            if (double.TryParse(rangeStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && double.TryParse(rangeEnd.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
                && double.IsFinite(a) && double.IsFinite(b) && a >= 0 && b <= AnimationSeconds.Maximum && b > a && (a != state.RangeStart || b != state.RangeEnd))
                OptionRequested?.Invoke("playbackRange", new[] { a, b });
        }
        foreach (var box in new[] { rangeStart, rangeEnd })
        {
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { rangePopup.IsOpen = false; e.Handled = true; } };
            box.LostKeyboardFocus += (_, _) => Commit();
        }
        WrapPanel actions = new() { Margin = new(0, 4, 0, 0) };
        extendRange.Click += (_, _) => ExtendRequested?.Invoke();
        autoRange.Click += (_, _) => AutoRangeRequested?.Invoke();
        fitRange.Click += (_, _) => FitRangeRequested?.Invoke();
        AutomationProperties.SetName(extendRange, "Extend +5 s"); AutomationProperties.SetName(autoRange, "Auto range"); AutomationProperties.SetName(fitRange, "Fit range to starts");
        actions.Children.Add(extendRange); actions.Children.Add(autoRange); actions.Children.Add(fitRange);
        body.Children.Add(actions);
        rangePopup.PlacementTarget = rangeButton;
        rangePopup.Child = Flyout(body);
    }

    /// <summary>A transport toggle requests its option from its checked state (a click, Space or UI Automation's Toggle), never while the state is shown.</summary>
    private void Toggled(ToggleButton toggle, string option)
    {
        toggle.Checked += (_, _) => { if (!changing) OptionRequested?.Invoke(option, true); };
        toggle.Unchecked += (_, _) => { if (!changing) OptionRequested?.Invoke(option, false); };
    }
    /// <summary>
    /// A ⋯ flyout works from the keyboard: opening it focuses its first control, Tab cycles inside it, and Esc closes it and
    /// returns the focus to <paramref name="opener"/> (a list or menu open inside it closes first).
    /// </summary>
    private void KeyboardFlyout(Popup popup, Button opener)
    {
        if (popup.Child is not FrameworkElement content) return;
        KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(content, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(content, KeyboardNavigationMode.Cycle);
        popup.Opened += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (popup.IsOpen && !content.IsKeyboardFocusWithin) content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
        content.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != Key.Escape || !popup.IsOpen) return;
            popup.IsOpen = false; opener.Focus(); e.Handled = true;
        }));
    }
    private static Border Flyout(UIElement content)
    {
        Border border = new() { CornerRadius = new(8), BorderThickness = new(1), Child = content };
        border.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        border.SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        return border;
    }
    private static StackPanel Field(string label, Control box, string suffix)
    {
        StackPanel field = new();
        TextBlock caption = new() { Text = label, FontSize = 12, Margin = new(0, 0, 0, 4) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        field.Children.Add(caption);
        StackPanel row = new() { Orientation = Orientation.Horizontal }; row.Children.Add(box);
        TextBlock unit = new() { Text = suffix, Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        row.Children.Add(unit); field.Children.Add(row);
        return field;
    }
    private static Button IconButton(string icon, string tip, Action click)
    {
        Button button = new() { Content = new PreviewIcon { Kind = icon, Width = 16, Height = 16 }, ToolTip = tip };
        button.SetResourceReference(StyleProperty, "CommandButton");
        button.Width = 32; button.Height = 32;
        button.Click += (_, _) => click();
        return button;
    }
    private static ToggleButton TextToggle(string icon, string text, string tip)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        content.Children.Add(new PreviewIcon { Kind = icon, Width = 15, Height = 15, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        ToggleButton toggle = new() { Content = content, ToolTip = tip, Padding = new(8, 0, 10, 0) };
        toggle.SetResourceReference(StyleProperty, "CommandButton");
        toggle.Width = double.NaN; toggle.Height = 30;
        AutomationProperties.SetName(toggle, text);
        return toggle;
    }
    private ToggleButton ViewToggle(string icon, string text, string tip)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        content.Children.Add(new PreviewIcon { Kind = icon, Width = 14, Height = 14, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        ToggleButton toggle = new() { Content = content, ToolTip = tip, Padding = new(10, 0, 12, 0) };
        toggle.SetResourceReference(StyleProperty, "SegmentOption");
        AutomationProperties.SetName(toggle, text + " view");
        string view = text.ToLowerInvariant();
        toggle.Click += (_, _) =>
        {
            if (changing) return;
            // A view button is a radio: clicking the shown one keeps it.
            preferredView = view; ApplyView(); ViewRequested?.Invoke(view);
        };
        return toggle;
    }
    private static TextBlock Label(string text)
    {
        TextBlock label = new() { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 6, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return label;
    }
    private static Border Divider()
    {
        Border line = new() { Width = 1, Height = 20, Margin = new(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        line.SetResourceReference(BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }
    private static FrameworkElement Gap(double width) => new Border { Width = width };
}
