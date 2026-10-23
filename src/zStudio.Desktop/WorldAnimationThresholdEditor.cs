using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace zStudio.Desktop;

/// <summary>A source-clock scalar editor. Loading a value never creates a proposal or quantizes it.</summary>
public sealed class WorldAnimationThresholdEditor : UserControl
{
    private readonly TextBlock modeLabel = new() { FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 4) };
    private readonly ValueTextBox number = new() { MinWidth = 70 };
    private readonly ThresholdRuler ruler;
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button previous = new() { Content = "−1 tick", Padding = new(5, 2, 5, 2), Margin = new(0, 2, 3, 2) };
    private readonly Button next = new() { Content = "+1 tick", Padding = new(5, 2, 5, 2), Margin = new(0, 2, 3, 2) };
    private double value;
    private string mode = "ANIMATION_OFFSET";
    private bool editable;
    private bool updating;
    public event Action<double>? ValueProposed;
    /// <summary>Every user text edit, including incomplete or invalid input, participates in the shared draft.</summary>
    public event Action<string>? TextEdited;
    public double Value => value;
    public string ClockMode => mode;
    public WorldAnimationThresholdEditor()
    {
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush"); SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        var panel = new StackPanel(); Content = panel; panel.Children.Add(modeLabel); panel.Children.Add(number);
        ruler = new(this) { Height = 44, Margin = new(0, 5, 0, 3) }; panel.Children.Add(ruler);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(previous); buttons.Children.Add(next); panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "Eligibility threshold in this clock; not an observed dispatch time. Drag snaps to 1/60 s.", FontSize = 11, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(error);
        AutomationProperties.SetName(number, "Source timing threshold in seconds");
        previous.Click += (_, _) => Propose(value - WorldAnimationTimeline.Step);
        next.Click += (_, _) => Propose(value + WorldAnimationTimeline.Step);
        number.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ProposeText(); e.Handled = true; } else if (e.Key == Key.Escape) { number.Text = Format(value); error.Text = ""; e.Handled = true; } };
        number.LostKeyboardFocus += (_, _) => ProposeText();
        number.TextChanged += (_, _) =>
        {
            if (updating || !editable) return;
            error.Text = double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
                ? "" : "Enter a finite source time in seconds.";
            TextEdited?.Invoke(number.Text);
        };
        SetValue(mode, 0, false);
    }
    public void SetValue(string clockMode, double threshold, bool canEdit = true)
    {
        if (!double.IsFinite(threshold)) throw new ArgumentOutOfRangeException(nameof(threshold));
        mode = clockMode; value = threshold; editable = canEdit;
        modeLabel.Text = WorldAnimationTimeline.Shown(clockMode + " · seconds");
        updating = true;
        try { if (!number.IsKeyboardFocusWithin) number.Text = Format(value); }
        finally { updating = false; }
        number.IsReadOnly = !editable; previous.IsEnabled = next.IsEnabled = ruler.IsEnabled = editable;
        error.Text = ""; ruler.InvalidateVisual();
    }
    private static string Format(double n) => n.ToString("0.####################################################################################################################################################################################################################################################################################################################################", CultureInfo.InvariantCulture);
    private void ProposeText()
    {
        if (!editable) return;
        if (!double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed)) { error.Text = "Enter a finite source time in seconds."; return; }
        error.Text = ""; Propose(parsed);
    }
    private void Propose(double proposed)
    {
        if (!editable || !double.IsFinite(proposed) || proposed == value) return;
        ValueProposed?.Invoke(proposed);
    }
    private sealed class ThresholdRuler : FrameworkElement
    {
        private readonly WorldAnimationThresholdEditor owner;
        private double? proposed;
        private double dragStartValue, dragStartX, dragStart, dragEnd;
        public ThresholdRuler(WorldAnimationThresholdEditor owner)
        { this.owner = owner; Focusable = true; ClipToBounds = true; AutomationProperties.SetName(this, "Native source-clock threshold ruler"); }
        private (double Start, double End) Bounds
        {
            get
            {
                // Include a negative or far-off-grid authored value without constraining source semantics.
                double span = Math.Max(1, Math.Abs(owner.value) * 1.5);
                if (!double.IsFinite(span)) span = Math.Abs(owner.value);
                return owner.value < 0 ? (-span, span * .1) : (-span * .1, span);
            }
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); var (start, end) = IsMouseCaptured ? (dragStart, dragEnd) : Bounds;
            Brush ink = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : owner.Foreground;
            dc.DrawRectangle(SystemParameters.HighContrast ? SystemColors.WindowBrush : owner.Background, null, new(0, 0, ActualWidth, ActualHeight));
            WorldAnimationTimelineDrawing.Grid(dc, start, end, ActualWidth, ActualHeight, ink, false, true, this);
            double x = ((proposed ?? owner.value) - start) / (end - start) * ActualWidth;
            dc.DrawLine(new(ink, 1), new(x, 18), new(x, ActualHeight)); WorldAnimationTimelineDrawing.Diamond(dc, new(x, 27), ink, new(ink, 1));
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
            double candidate = Math.Round((dragStartValue + delta) * 60) / 60;
            if (double.IsFinite(candidate)) proposed = candidate; InvalidateVisual();
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
            else if (e.Key is Key.Left or Key.Right) { owner.Propose(owner.value + (e.Key == Key.Left ? -1 : 1) * WorldAnimationTimeline.Step); e.Handled = true; }
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
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw new ArgumentException("A source threshold must be finite.");
                ruler.Dispatcher.Invoke(() => ruler.owner.Propose(number));
            }
            public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
            protected override string GetClassNameCore() => nameof(ThresholdRuler);
            protected override string GetHelpTextCore() => "Native source clock, including negative times. No source value is changed by inspecting the ruler.";
        }
    }
}
