using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>
/// An event's start threshold in seconds, in its own clock (design §11, Start threshold): the clock as a segmented control
/// (choosing another is an explicit edit), the value with − / + (0.025 s; Shift 0.1 s), the native-clock ruler whose diamond
/// drags on the 0.025 s grid (Shift 0.1 s, Ctrl free), the "May start …" sentence and, only with evidence, what was observed.
/// Loading a value never proposes or rounds it; every text edit joins the card's shared draft.
/// </summary>
public sealed class AnimationThresholdEditor : UserControl
{
    private readonly ValueTextBox number = new() { Height = 30, MinHeight = 30, TextAlignment = TextAlignment.Right, Padding = new(8, 0, 22, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ThresholdRuler ruler;
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Visibility = Visibility.Collapsed };
    private readonly TextBlock sentence = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 0) };
    private readonly TextBlock observed = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock note = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button previous, next;
    private readonly ToggleButton[] clocks;
    private static readonly (string Mode, string Text, string Tip)[] Clocks =
    [
        ("ANIMATION_OFFSET", "Animation", "Animation clock: may start once its animation has run this long (ANIMATION_OFFSET)"),
        ("SEQUENCE_OFFSET", "Sequence", "Sequence clock: may start once its sequence has run this long (SEQUENCE_OFFSET)"),
        ("EVENT_OFFSET", "After previous", "After previous: may start this long after the previous event completes (EVENT_OFFSET)"),
    ];
    private double value;
    private string mode = "ANIMATION_OFFSET";
    private bool editable;
    private bool updating;
    public event Action<double>? ValueProposed;
    /// <summary>Another clock was chosen: an explicit edit of the threshold's clock (its value stays).</summary>
    public event Action<string>? ClockProposed;
    /// <summary>Every user text edit, including incomplete or invalid input, participates in the shared draft.</summary>
    public event Action<string>? TextEdited;
    public double Value => value;
    public string ClockMode => mode;

    public AnimationThresholdEditor()
    {
        var panel = new StackPanel(); Content = panel;
        // Clock: a segmented control, 30 high.
        Border segmented = new() { CornerRadius = new(5), BorderThickness = new(1), Padding = new(2), HorizontalAlignment = HorizontalAlignment.Left };
        segmented.SetResourceReference(BackgroundProperty, "ControlFillColorTertiaryBrush");
        segmented.SetResourceReference(BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        StackPanel options = new() { Orientation = Orientation.Horizontal };
        clocks = Clocks.Select(c =>
        {
            ToggleButton option = new() { Content = new TextBlock { Text = c.Text, FontSize = 12.5 }, ToolTip = c.Tip, Padding = new(10, 0, 10, 0), Height = 26, Tag = c.Mode };
            option.SetResourceReference(StyleProperty, "SegmentOption");
            AutomationProperties.SetName(option, c.Text + " clock");
            option.Click += (_, _) => { if (!updating) { RefreshClock(); if (editable && c.Mode != mode) ClockProposed?.Invoke(c.Mode); } };
            options.Children.Add(option);
            return option;
        }).ToArray();
        segmented.Child = options;
        AutomationProperties.SetName(segmented, "Start threshold clock");
        panel.Children.Add(Caption("Clock")); panel.Children.Add(segmented);
        // Value: the seconds box with its unit, then − and +.
        panel.Children.Add(Caption("Value"));
        Grid row = new();
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid box = new(); box.Children.Add(number);
        TextBlock unit = new() { Text = "s", Margin = new(0, 0, 9, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        box.Children.Add(unit);
        row.Children.Add(box);
        previous = StepButton("−", "Earlier by 0.025 s (Shift: 0.1 s)", "Earlier by 0.025 seconds"); Grid.SetColumn(previous, 1); row.Children.Add(previous);
        next = StepButton("+", "Later by 0.025 s (Shift: 0.1 s)", "Later by 0.025 seconds"); Grid.SetColumn(next, 2); row.Children.Add(next);
        panel.Children.Add(row);
        ruler = new(this) { Height = 36, Margin = new(0, 8, 0, 2) }; panel.Children.Add(ruler);
        foreach (var text in new[] { sentence, observed, note }) text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        observed.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        panel.Children.Add(error); panel.Children.Add(sentence); panel.Children.Add(observed); panel.Children.Add(note);
        AutomationProperties.SetName(number, "Start threshold in seconds");
        number.ToolTip = "The threshold in its own clock, in seconds. Enter keeps it in the draft; Escape restores the field.";
        previous.Click += (_, _) => Propose(Nudge(-1));
        next.Click += (_, _) => Propose(Nudge(1));
        number.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ProposeText(); e.Handled = true; }
            else if (e.Key == Key.Escape) { updating = true; try { number.Text = Format(value); } finally { updating = false; } ShowError(null); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down) { Propose(Nudge(e.Key == Key.Up ? 1 : -1)); e.Handled = true; }
        };
        number.LostKeyboardFocus += (_, _) => ProposeText();
        number.TextChanged += (_, _) =>
        {
            if (updating || !editable) return;
            ShowError(double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) ? null : "Enter a finite time in seconds.");
            TextEdited?.Invoke(number.Text);
        };
        SetValue(mode, 0, false);
    }
    private static TextBlock Caption(string text)
    {
        TextBlock caption = new() { Text = text, FontSize = 12, Margin = new(0, 6, 0, 4) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return caption;
    }
    private static Button StepButton(string text, string tip, string name)
    {
        Button button = new() { Content = new TextBlock { Text = text, FontSize = 14 }, Width = 32, Height = 30, Padding = new(0), Margin = new(6, 0, 0, 0), ToolTip = tip };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetShowOnDisabled(button, true);
        return button;
    }
    private void ShowError(string? message) { error.Text = message ?? ""; error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible; }
    private void RefreshClock()
    {
        updating = true;
        try { foreach (var option in clocks) option.IsChecked = (string)option.Tag == mode; }
        finally { updating = false; }
    }

    /// <summary>One step from the stored value: 0.025 s, or 0.1 s with Shift, landing on that grid.</summary>
    private double Nudge(int direction)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? AnimationSeconds.CoarseStep : AnimationSeconds.GridStep;
        decimal grid = (decimal)step, current = (decimal)Math.Clamp(value, -AnimationSeconds.Maximum, AnimationSeconds.Maximum);
        // The next grid line strictly beyond the stored value in that direction.
        decimal result = direction > 0 ? (Math.Floor(current / grid) + 1) * grid : (Math.Ceiling(current / grid) - 1) * grid;
        return Math.Clamp((double)result, -AnimationSeconds.Maximum, AnimationSeconds.Maximum);
    }

    public void SetValue(string clockMode, double threshold, bool canEdit = true)
    {
        if (!double.IsFinite(threshold)) throw new ArgumentOutOfRangeException(nameof(threshold));
        mode = clockMode; value = threshold; editable = canEdit;
        updating = true;
        try { if (!number.IsKeyboardFocusWithin) number.Text = Format(value); }
        finally { updating = false; }
        RefreshClock();
        number.IsReadOnly = !editable; previous.IsEnabled = next.IsEnabled = ruler.IsEnabled = editable;
        foreach (var option in clocks) option.IsEnabled = editable;
        ShowError(null); ruler.InvalidateVisual();
        AutomationProperties.SetHelpText(ruler, clockMode switch
        {
            "SEQUENCE_OFFSET" => "Its sequence's clock", "EVENT_OFFSET" => "Time after the previous event completes", _ => "Its animation's clock",
        });
    }
    /// <summary>The sentence ("May start 0.1 s after the previous event completes…"), what was observed (only with evidence) and a note.</summary>
    public void SetText(string text, string? seen, string? remark = null)
    {
        sentence.Text = text;
        observed.Text = seen ?? ""; observed.Visibility = seen == null ? Visibility.Collapsed : Visibility.Visible;
        note.Text = remark ?? ""; note.Visibility = remark == null ? Visibility.Collapsed : Visibility.Visible;
    }
    /// <summary>What the run observed (only with evidence); the sentence stays as the draft has it.</summary>
    public void SetObserved(string? seen) { observed.Text = seen ?? ""; observed.Visibility = seen == null ? Visibility.Collapsed : Visibility.Visible; }
    /// <summary>The stored value exactly (never rounded by viewing).</summary>
    private static string Format(double n) => n.ToString("R", CultureInfo.InvariantCulture);
    private void ProposeText()
    {
        if (!editable) return;
        if (!double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed)) { ShowError("Enter a finite time in seconds."); return; }
        ShowError(null); Propose(parsed);
    }
    private void Propose(double proposed)
    {
        if (!editable || !double.IsFinite(proposed) || proposed == value) return;
        ValueProposed?.Invoke(proposed);
    }
    /// <summary>F2 and Alt+Enter land here.</summary>
    internal void FocusValue() { number.Focus(); number.SelectAll(); }

    /// <summary>The threshold on its own clock: a diamond that drags (0.025 s grid; Shift 0.1 s; Ctrl free), ← → nudge it.</summary>
    private sealed class ThresholdRuler : FrameworkElement
    {
        private readonly AnimationThresholdEditor owner;
        private double? proposed;
        private double dragStartValue, dragStartX, dragStart, dragEnd;
        public ThresholdRuler(AnimationThresholdEditor owner)
        {
            this.owner = owner; Focusable = true; ClipToBounds = true; AutomationProperties.SetName(this, "Start threshold ruler in seconds");
            ToolTip = "This event's own clock. Drag the diamond (0.025 s; Shift 0.1 s; Ctrl free); ← → nudge it.";
        }
        private (double Start, double End) Bounds
        {
            get
            {
                // Include a negative or far stored value without changing what it means; 0 … 0.5 s for small thresholds.
                double span = Math.Max(.5, Math.Abs(owner.value) * 1.5);
                return owner.value < 0 ? (-span, span * .1) : (-span * .02, span);
            }
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var palette = AnimationGraphPalette.For(AnimationGraphPalette.IsDark(this));
            var (start, end) = IsMouseCaptured ? (dragStart, dragEnd) : Bounds;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            double width = Math.Max(1, ActualWidth - 12);
            double X(double t) => 6 + (t - start) / (end - start) * width;
            double span = end - start, step = new[] { .025, .1, .5, 1, 5, 10, 60, 600 }.FirstOrDefault(s => s / span * width >= 6, 600);
            double major = new[] { .1, .5, 1, 2, 5, 10, 60, 600 }.FirstOrDefault(s => s / span * width >= 44, 600);
            var tick = new Pen(palette.T4, 1);
            double baseline = ActualHeight - 14;
            dc.DrawLine(tick, new Point(6, baseline + .5), new Point(6 + width, baseline + .5));
            for (long i = (long)Math.Ceiling(start / step); i * step <= end && i - (long)Math.Ceiling(start / step) < 1000; i++)
            {
                double t = i * step, x = Math.Round(X(t)) + .5; bool labelled = Math.Abs(t / major - Math.Round(t / major)) < 1e-6;
                dc.DrawLine(tick, new Point(x, baseline - (labelled ? 8 : 5)), new Point(x, baseline));
                if (labelled)
                {
                    var text = new FormattedText(t.ToString("0.###", CultureInfo.InvariantCulture) + " s", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, palette.Text, 10, palette.T3, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width)), baseline + 2));
                }
            }
            double at = X(proposed ?? owner.value), y = baseline - 8;
            var diamond = new StreamGeometry();
            using (var c = diamond.Open()) { c.BeginFigure(new Point(at, y - 6), true, true); c.LineTo(new Point(at + 6, y), true, true); c.LineTo(new Point(at, y + 6), true, true); c.LineTo(new Point(at - 6, y), true, true); }
            diamond.Freeze();
            dc.DrawGeometry(IsEnabled ? palette.Accent : palette.T4, null, diamond);
            if (IsKeyboardFocused) dc.DrawRectangle(null, new Pen(palette.Accent, 1), new Rect(.5, .5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)));
        }
        private double Width2 => Math.Max(1, ActualWidth - 12);
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e); if (!owner.editable) return; Focus();
            var bounds = Bounds; dragStart = bounds.Start; dragEnd = bounds.End; dragStartValue = owner.value; dragStartX = e.GetPosition(this).X;
            double handle = 6 + (owner.value - dragStart) / (dragEnd - dragStart) * Width2;
            if (Math.Abs(dragStartX - handle) <= 10) CaptureMouse();
            e.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if (!IsMouseCaptured || Math.Abs(e.GetPosition(this).X - dragStartX) < 3) return;
            double delta = (e.GetPosition(this).X - dragStartX) / Width2 * (dragEnd - dragStart);
            var snap = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? AnimationSnap.Free : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? AnimationSnap.Coarse : AnimationSnap.Grid;
            proposed = AnimationSeconds.SnapSigned(dragStartValue + delta, snap); InvalidateVisual();
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e); if (!IsMouseCaptured) return; double? result = proposed; ReleaseMouseCapture(); proposed = null; InvalidateVisual();
            if (result is double number) owner.Propose(number); e.Handled = true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); proposed = null; InvalidateVisual(); }
        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && IsMouseCaptured) { ReleaseMouseCapture(); proposed = null; e.Handled = true; }
            else if (e.Key is Key.Left or Key.Right && owner.editable) { owner.Propose(owner.Nudge(e.Key == Key.Left ? -1 : 1)); e.Handled = true; }
            else base.OnKeyDown(e);
        }
        protected override AutomationPeer OnCreateAutomationPeer() => new ThresholdPeer(this);
        private sealed class ThresholdPeer(ThresholdRuler ruler) : FrameworkElementAutomationPeer(ruler), IValueProvider
        {
            public bool IsReadOnly => !ruler.owner.editable;
            public string Value => ruler.owner.value.ToString("R", CultureInfo.InvariantCulture);
            public void SetValue(string value)
            {
                if (IsReadOnly) throw new ElementNotEnabledException();
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw new ArgumentException("A start threshold is a finite number of seconds.");
                ruler.Dispatcher.Invoke(() => ruler.owner.Propose(number));
            }
            public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
            protected override string GetClassNameCore() => nameof(ThresholdRuler);
            protected override string GetHelpTextCore() => "The threshold in its own clock, in seconds, including negative values. Inspecting the ruler changes nothing.";
        }
    }
}
