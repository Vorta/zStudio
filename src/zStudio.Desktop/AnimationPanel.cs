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
    public string? Duration { get; init; }
}

/// <summary>
/// Animation mode's panel under the 3D view (design/animation-redesign.md §7, §13): the one transport (40 DIP), the view bar
/// (34 DIP: Structure | Timing | Split and the shown view's tools) and the views. It shows snapshots and raises intents; the
/// window owns the run, the scenario and the source.
/// </summary>
internal sealed class AnimationPanel : Border
{
    internal const double MinimumPanelHeight = 240, SplitWidth = 1300, CompactWidth = 1040;
    private readonly Button play, stop, toStart, stepBack, stepForward, rangeButton, overflow, statusLink;
    private readonly PreviewIcon playIcon = new() { Kind = "Play", Width = 16, Height = 16 };
    private readonly ValueTextBox readout = new() { Width = 92, Height = 30, MinHeight = 30, Padding = new(8, 0, 8, 0), TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock rangeText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox speed = new() { Width = 74, Height = 30, MinHeight = 30, ItemsSource = new[] { "0.25×", "0.5×", "1×", "2×" }, SelectedIndex = 2, VerticalAlignment = VerticalAlignment.Center };
    private static readonly double[] Speeds = [.25, .5, 1, 2];
    private readonly ToggleButton loop, mute, follow;
    private readonly Slider volume = new() { Minimum = 0, Maximum = 1, Value = .5, Width = 76, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 8, 0) };
    private readonly StackPanel secondary = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel transportLeft = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel overflowItems = new() { Margin = new(10) };
    private readonly Popup overflowPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly Popup rangePopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly ValueTextBox rangeStart = new() { Width = 80, Height = 30, MinHeight = 30, TextAlignment = TextAlignment.Right };
    private readonly ValueTextBox rangeEnd = new() { Width = 80, Height = 30, MinHeight = 30, TextAlignment = TextAlignment.Right };
    private readonly TextBlock durationText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 280, Margin = new(0, 6, 0, 0) };
    private readonly TextBlock status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(8, 0, 4, 0) };
    private readonly ToggleButton structureView, timingView, splitView;
    private readonly ContentControl structureTools = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
    private readonly ContentControl tools = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
    private readonly Grid views = new();
    private readonly ColumnDefinition structureColumn = new(), splitterColumn = new() { Width = new(0) }, timingColumn = new();
    private readonly Border structureSlot = new(), timingSlot = new();
    private readonly GridSplitter viewSplitter = new() { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, Focusable = true };
    private readonly Border tip = new() { CornerRadius = new(6), BorderThickness = new(1), Padding = new(10, 6, 4, 6), Margin = new(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
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
    internal event Action? FitRangeRequested, DurationRequested, TipDismissed, DiagnosticsRequested;
    internal event Action<double>? SplitChanged;

    internal AnimationTransportState State => state;
    /// <summary>The chosen view (structure, timing or split); <see cref="ShownView"/> is what the width allows.</summary>
    internal string View => preferredView;
    internal string ShownView => preferredView == "split" && !SplitAvailable ? "timing" : preferredView;
    internal bool SplitAvailable => ActualWidth <= 0 || ActualWidth >= SplitWidth;
    internal bool Compact => compact;

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
        rangeButton = new() { Content = rangeText, Height = 30, Padding = new(6, 0, 6, 0), Margin = new(2, 0, 4, 0), ToolTip = "The playback range: edit its start and end, fit it to the starts or estimate the duration" };
        rangeButton.SetResourceReference(StyleProperty, "CommandButton"); rangeButton.Width = double.NaN;
        AutomationProperties.SetName(rangeButton, "Playback range");
        rangeButton.Click += (_, _) => { rangeStart.Text = Number(state.RangeStart); rangeEnd.Text = Number(state.RangeEnd); rangePopup.IsOpen = true; };
        BuildRangeFlyout();
        foreach (var element in new UIElement[] { play, stop, toStart, stepBack, stepForward }) transportLeft.Children.Add(element);
        transportLeft.Children.Add(Gap(6)); transportLeft.Children.Add(readout); transportLeft.Children.Add(rangeButton);

        var speedLabel = Label("Speed");
        AutomationProperties.SetName(speed, "Speed"); speed.ToolTip = "Playback speed";
        speed.SelectionChanged += (_, _) => { if (!changing && speed.SelectedIndex >= 0) OptionRequested?.Invoke("speed", Speeds[speed.SelectedIndex]); };
        loop = TextToggle("Loop", "Loop range", "Loop range: replay the playback range (authored loops are separate)");
        loop.Click += (_, _) => { if (!changing) OptionRequested?.Invoke("replay", loop.IsChecked == true); };
        mute = new() { Content = new PreviewIcon { Kind = "Volume", Width = 16, Height = 16 }, ToolTip = "Mute" };
        mute.SetResourceReference(StyleProperty, "CommandButton"); AutomationProperties.SetName(mute, "Mute");
        mute.Click += (_, _) => { if (!changing) OptionRequested?.Invoke("mute", mute.IsChecked == true); };
        AutomationProperties.SetName(volume, "Volume"); volume.ToolTip = "Volume";
        volume.ValueChanged += (_, _) => { if (!changing) OptionRequested?.Invoke("volume", volume.Value); };
        follow = TextToggle("Camera", "Follow camera", "Follow camera: the view follows the animation's camera; navigating turns it off");
        follow.Click += (_, _) => { if (!changing) OptionRequested?.Invoke("followCamera", follow.IsChecked == true); };
        foreach (var element in new UIElement[] { Divider(), speedLabel, speed, Divider(), loop, Divider(), mute, volume, Divider(), follow }) secondary.Children.Add(element);
        overflow = IconButton("More", "More transport controls", () => overflowPopup.IsOpen = true);
        AutomationProperties.SetName(overflow, "More transport controls"); overflow.Visibility = Visibility.Collapsed;
        overflowPopup.PlacementTarget = overflow;
        overflowPopup.Child = Flyout(overflowItems);
        transportLeft.Children.Add(secondary); transportLeft.Children.Add(overflow);
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        statusLink = new() { Content = new TextBlock { Text = "Show in Diagnostics", FontSize = 12 }, Height = 26, Padding = new(6, 0, 6, 0), Visibility = Visibility.Collapsed, ToolTip = "Show the Diagnostics strip under the panel" };
        statusLink.SetResourceReference(StyleProperty, "CommandButton"); statusLink.Width = double.NaN;
        statusLink.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        statusLink.Click += (_, _) => DiagnosticsRequested?.Invoke();
        DockPanel transport = new() { Height = 40, Margin = new(8, 0, 8, 0), LastChildFill = true };
        StackPanel right = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(status); right.Children.Add(statusLink);
        DockPanel.SetDock(transportLeft, Dock.Left); transport.Children.Add(transportLeft);
        right.HorizontalAlignment = HorizontalAlignment.Right; transport.Children.Add(right);

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
        StackPanel viewTools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        viewTools.Children.Add(structureTools); viewTools.Children.Add(tools);
        viewBar.Children.Add(viewTools);

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
            rangeText.Text = $"of {Short(value.RangeStart)}–{Short(value.RangeEnd)} s";
            speed.SelectedIndex = Array.IndexOf(Speeds, value.Speed) is int index and >= 0 ? index : -1;
            loop.IsChecked = value.Replay; mute.IsChecked = value.Muted; follow.IsChecked = value.FollowCamera;
            ((PreviewIcon)mute.Content).Kind = value.Muted ? "Mute" : "Volume";
            mute.ToolTip = value.Muted ? "Unmute" : "Mute";
            if (Math.Abs(volume.Value - value.Volume) > 1e-9) volume.Value = Math.Clamp(value.Volume, 0, 1);
            status.Text = value.Busy ? "Preparing…" : value.Status; status.ToolTip = value.Status;
            if (value.Failed) status.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            else status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            statusLink.Visibility = value.Failed ? Visibility.Visible : Visibility.Collapsed;
            durationText.Text = value.Duration ?? "Estimate duration measures how long the scenario runs, without moving the playhead.";
        }
        finally { changing = false; }
    }
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

    /// <summary>Hosts the Structure view (the graph) and its tools.</summary>
    internal void SetStructure(FrameworkElement structure, FrameworkElement? structureToolbar)
    {
        if (structure.Parent is Decorator old) old.Child = null;
        if (structureToolbar?.Parent is ContentControl host) host.Content = null;
        structureSlot.Child = structure; structureTools.Content = structureToolbar;
        ApplyView();
    }

    /// <summary>Hosts the Timing view (the timeline) and its tools.</summary>
    internal void SetTiming(FrameworkElement timing, FrameworkElement? timingToolbar)
    {
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
        tools.Content = timing ? timingTools : null;
        structureTools.Visibility = structure ? Visibility.Visible : Visibility.Collapsed;
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
        StackPanel body = new() { Margin = new(12), Width = 280 };
        body.Children.Add(new TextBlock { Text = "Playback range", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) });
        Grid fields = new(); fields.ColumnDefinitions.Add(new()); fields.ColumnDefinitions.Add(new() { Width = new(12) }); fields.ColumnDefinitions.Add(new());
        fields.Children.Add(Field("Start", rangeStart, "s"));
        var end = Field("End", rangeEnd, "s"); Grid.SetColumn(end, 2); fields.Children.Add(end);
        AutomationProperties.SetName(rangeStart, "Playback range start seconds"); AutomationProperties.SetName(rangeEnd, "Playback range end seconds");
        body.Children.Add(fields);
        void Commit()
        {
            if (double.TryParse(rangeStart.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && double.TryParse(rangeEnd.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
                && double.IsFinite(a) && double.IsFinite(b) && a >= 0 && b <= AnimationSeconds.Maximum && b > a && (a != state.RangeStart || b != state.RangeEnd))
                OptionRequested?.Invoke("range", new[] { a, b });
        }
        foreach (var box in new[] { rangeStart, rangeEnd })
        {
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { rangePopup.IsOpen = false; e.Handled = true; } };
            box.LostKeyboardFocus += (_, _) => Commit();
        }
        WrapPanel actions = new() { Margin = new(0, 10, 0, 0) };
        Button fit = new() { Content = "Fit range to starts", Margin = new(0, 0, 6, 0), ToolTip = "The range from 0 s to the latest start plus 30 s" };
        fit.Click += (_, _) => FitRangeRequested?.Invoke();
        Button duration = new() { Content = "Estimate duration", ToolTip = "Measure how long the scenario runs, without moving the playhead" };
        duration.Click += (_, _) => DurationRequested?.Invoke();
        AutomationProperties.SetName(fit, "Fit range to starts"); AutomationProperties.SetName(duration, "Estimate duration");
        actions.Children.Add(fit); actions.Children.Add(duration);
        body.Children.Add(actions);
        durationText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        body.Children.Add(durationText);
        rangePopup.PlacementTarget = rangeButton;
        rangePopup.Child = Flyout(body);
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
