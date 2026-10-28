using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>
/// An event's start threshold in seconds, in its own clock (the Inspector's Start Time until the card's redesign): a seconds
/// box, − / + 0.025 s (Shift 0.1 s) and a ruler whose diamond drags on the 0.025 s grid (Shift 0.1 s, Ctrl free). Loading a
/// value never proposes or rounds it; every text edit joins the card's shared draft.
/// </summary>
public sealed class AnimationThresholdEditor : UserControl
{
    private readonly TextBlock modeLabel = new() { FontSize = 12, Margin = new(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
    private readonly ValueTextBox number = new() { MinWidth = 90, Height = 30, MinHeight = 30, TextAlignment = TextAlignment.Right };
    private readonly ThresholdRuler ruler;
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button previous = new() { Content = "−0.025 s", Padding = new(8, 2, 8, 2), Margin = new(6, 0, 3, 0), Height = 30, ToolTip = "Earlier by 0.025 s (Shift: 0.1 s)" };
    private readonly Button next = new() { Content = "+0.025 s", Padding = new(8, 2, 8, 2), Height = 30, ToolTip = "Later by 0.025 s (Shift: 0.1 s)" };
    private double value;
    private string mode = "ANIMATION_OFFSET";
    private bool editable;
    private bool updating;
    public event Action<double>? ValueProposed;
    /// <summary>Every user text edit, including incomplete or invalid input, participates in the shared draft.</summary>
    public event Action<string>? TextEdited;
    public double Value => value;
    public string ClockMode => mode;

    public AnimationThresholdEditor()
    {
        modeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        var panel = new StackPanel(); Content = panel;
        panel.Children.Add(modeLabel);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(number);
        var unit = new TextBlock { Text = "s", Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        row.Children.Add(unit); row.Children.Add(previous); row.Children.Add(next);
        panel.Children.Add(row);
        ruler = new(this) { Height = 40, Margin = new(0, 6, 0, 3) }; panel.Children.Add(ruler);
        var help = new TextBlock { Text = "A threshold in this clock, not a time on the shared clock: earlier events, waits and conditions can delay the event. Drag the diamond on the 0.025 s grid (Shift 0.1 s, Ctrl free).", FontSize = 11, TextWrapping = TextWrapping.Wrap };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        panel.Children.Add(help); panel.Children.Add(error);
        AutomationProperties.SetName(number, "Start threshold in seconds");
        AutomationProperties.SetName(previous, "Earlier by 0.025 seconds"); AutomationProperties.SetName(next, "Later by 0.025 seconds");
        previous.Click += (_, _) => Propose(Nudge(-1));
        next.Click += (_, _) => Propose(Nudge(1));
        number.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ProposeText(); e.Handled = true; }
            else if (e.Key == Key.Escape) { number.Text = Format(value); error.Text = ""; e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down) { Propose(Nudge(e.Key == Key.Up ? 1 : -1)); e.Handled = true; }
        };
        number.LostKeyboardFocus += (_, _) => ProposeText();
        number.TextChanged += (_, _) =>
        {
            if (updating || !editable) return;
            error.Text = double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
                ? "" : "Enter a finite time in seconds.";
            TextEdited?.Invoke(number.Text);
        };
        SetValue(mode, 0, false);
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
        modeLabel.Text = clockMode switch
        {
            "SEQUENCE_OFFSET" => "Sequence clock: may start once its sequence has run this long",
            "EVENT_OFFSET" => "After previous: may start this long after the previous event completes",
            _ => "Animation clock: may start once its animation has run this long",
        };
        updating = true;
        try { if (!number.IsKeyboardFocusWithin) number.Text = Format(value); }
        finally { updating = false; }
        number.IsReadOnly = !editable; previous.IsEnabled = next.IsEnabled = ruler.IsEnabled = editable;
        error.Text = ""; ruler.InvalidateVisual();
    }
    /// <summary>The stored value exactly (never rounded by viewing).</summary>
    private static string Format(double n) => n.ToString("R", CultureInfo.InvariantCulture);
    private void ProposeText()
    {
        if (!editable) return;
        if (!double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed)) { error.Text = "Enter a finite time in seconds."; return; }
        error.Text = ""; Propose(parsed);
    }
    private void Propose(double proposed)
    {
        if (!editable || !double.IsFinite(proposed) || proposed == value) return;
        ValueProposed?.Invoke(proposed);
    }

    /// <summary>The threshold on its own clock: a diamond that drags (0.025 s grid; Shift 0.1 s; Ctrl free), ← → nudge it.</summary>
    private sealed class ThresholdRuler : FrameworkElement
    {
        private readonly AnimationThresholdEditor owner;
        private double? proposed;
        private double dragStartValue, dragStartX, dragStart, dragEnd;
        public ThresholdRuler(AnimationThresholdEditor owner)
        { this.owner = owner; Focusable = true; ClipToBounds = true; AutomationProperties.SetName(this, "Start threshold ruler in seconds"); }
        private (double Start, double End) Bounds
        {
            get
            {
                // Include a negative or far stored value without changing what it means.
                double span = Math.Max(1, Math.Abs(owner.value) * 1.5);
                return owner.value < 0 ? (-span, span * .1) : (-span * .05, span);
            }
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var palette = AnimationGraphPalette.For(AnimationGraphPalette.IsDark(this));
            var (start, end) = IsMouseCaptured ? (dragStart, dragEnd) : Bounds;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            double X(double t) => (t - start) / (end - start) * ActualWidth;
            double span = end - start, step = new[] { .025, .1, .5, 1, 5, 10, 60, 600 }.FirstOrDefault(s => s / span * ActualWidth >= 6, 600);
            double major = new[] { .1, .5, 1, 2, 5, 10, 60, 600 }.FirstOrDefault(s => s / span * ActualWidth >= 48, 600);
            var tick = new Pen(palette.T4, 1);
            for (long i = (long)Math.Ceiling(start / step); i * step <= end && i - (long)Math.Ceiling(start / step) < 1000; i++)
            {
                double t = i * step, x = Math.Round(X(t)) + .5; bool labelled = Math.Abs(t / major - Math.Round(t / major)) < 1e-6;
                dc.DrawLine(tick, new Point(x, ActualHeight - (labelled ? 9 : 5)), new Point(x, ActualHeight));
                if (labelled)
                {
                    var text = new FormattedText(t.ToString("0.###", CultureInfo.InvariantCulture) + " s", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, palette.Text, 10, palette.T3, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(text, new Point(x - text.Width / 2, 0));
                }
            }
            double at = X(proposed ?? owner.value), y = ActualHeight - 13;
            var diamond = new StreamGeometry();
            using (var c = diamond.Open()) { c.BeginFigure(new Point(at, y - 5), true, true); c.LineTo(new Point(at + 5, y), true, true); c.LineTo(new Point(at, y + 5), true, true); c.LineTo(new Point(at - 5, y), true, true); }
            diamond.Freeze();
            dc.DrawGeometry(IsEnabled ? palette.Accent : palette.T4, null, diamond);
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e); if (!owner.editable) return; Focus();
            var bounds = Bounds; dragStart = bounds.Start; dragEnd = bounds.End; dragStartValue = owner.value; dragStartX = e.GetPosition(this).X;
            double handle = (owner.value - dragStart) / (dragEnd - dragStart) * ActualWidth;
            if (Math.Abs(dragStartX - handle) <= 10) CaptureMouse();
            e.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if (!IsMouseCaptured || Math.Abs(e.GetPosition(this).X - dragStartX) < 3) return;
            double delta = (e.GetPosition(this).X - dragStartX) / Math.Max(1, ActualWidth) * (dragEnd - dragStart);
            var snap = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? AnimationSnap.Free : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? AnimationSnap.Coarse : AnimationSnap.Grid;
            proposed = AnimationSeconds.SnapSigned(dragStartValue + delta, snap); InvalidateVisual();
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e); if (!IsMouseCaptured) return; double? result = proposed; ReleaseMouseCapture(); proposed = null; InvalidateVisual();
            if (result is double number) owner.Propose(number); e.Handled = true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); proposed = null; InvalidateVisual(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && IsMouseCaptured) { ReleaseMouseCapture(); proposed = null; e.Handled = true; }
            else if (e.Key is Key.Left or Key.Right) { owner.Propose(owner.Nudge(e.Key == Key.Left ? -1 : 1)); e.Handled = true; }
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
