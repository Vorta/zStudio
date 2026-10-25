using zStudio.Core.Animation;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;

namespace zStudio.Desktop;

internal sealed class WorldAnimationTimeRuler : FrameworkElement
{
    private readonly WorldAnimationTimeline owner;
    public WorldAnimationTimeRuler(WorldAnimationTimeline owner)
    {
        this.owner = owner; Focusable = true; ClipToBounds = true;
        AutomationProperties.SetName(this, "Animation playhead in seconds");
        ToolTip = "40 fps · Frame 1 is 0 seconds · Drag to seek silently · Ctrl+wheel zooms · Shift+wheel pans · arrows step one frame";
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); Brush ink = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : owner.Foreground;
        dc.DrawRectangle(SystemParameters.HighContrast ? SystemColors.WindowBrush : owner.Background, null, new(0, 0, ActualWidth, ActualHeight));
        WorldAnimationTimelineDrawing.Grid(dc, owner.VisibleStart, owner.VisibleEnd, ActualWidth, ActualHeight, ink, true, true, this);
        if ((owner.RequestedTime ?? owner.State.Time) >= owner.VisibleStart && (owner.RequestedTime ?? owner.State.Time) <= owner.VisibleEnd)
        {
            double x = ((owner.RequestedTime ?? owner.State.Time) - owner.VisibleStart) / (owner.VisibleEnd - owner.VisibleStart) * ActualWidth;
            WorldAnimationTimelineDrawing.Diamond(dc, new(x, ActualHeight - 5), ink, new(ink, 1));
        }
    }
    private void Seek(MouseEventArgs e) => owner.RequestSeek(owner.VisibleStart + Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1) * (owner.VisibleEnd - owner.VisibleStart));
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { base.OnMouseLeftButtonDown(e); Focus(); if (CaptureMouse()) Seek(e); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (IsMouseCaptured) Seek(e); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); if (IsMouseCaptured) { Seek(e); ReleaseMouseCapture(); } e.Handled = true; }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { if (owner.HandleWheel(e.Delta, Keyboard.Modifiers, e.GetPosition(this).X / Math.Max(1, ActualWidth))) e.Handled = true; else base.OnMouseWheel(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (!owner.HandleCanvasKey(e)) base.OnKeyDown(e); }
    protected override AutomationPeer OnCreateAutomationPeer() => new RulerPeer(this);
    private sealed class RulerPeer(WorldAnimationTimeRuler ruler) : FrameworkElementAutomationPeer(ruler), IRangeValueProvider
    {
        public bool IsReadOnly => !ruler.owner.State.CanSeek;
        public double LargeChange => 1;
        public double SmallChange => WorldAnimationTimeline.Step;
        public double Maximum => 3600;
        public double Minimum => 0;
        public double Value => ruler.owner.State.Time;
        public void SetValue(double value) { if (IsReadOnly) throw new ElementNotEnabledException(); if (!double.IsFinite(value) || value is < 0 or > 3600) throw new ArgumentOutOfRangeException(nameof(value)); ruler.Dispatcher.Invoke(() => ruler.owner.RequestSeek(value)); }
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(WorldAnimationTimeRuler);
    }
}

internal static class WorldAnimationTimelineDrawing
{
    public static void Grid(DrawingContext dc, double start, double end, double width, double height, Brush ink, bool ticks, bool labels, Visual visual)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start || width <= 0) return;
        if (ticks)
        {
            double cellWidth = width / ((end - start) * AnimationTimelineFrames.Rate);
            double stride = 5 * Math.Max(1, Math.Ceiling(70 / Math.Max(.000001, cellWidth * 5)));
            double firstFrame = Math.Ceiling(start * AnimationTimelineFrames.Rate / stride) * stride;
            for (int i = 0; i < 200; i++)
            {
                double frame = firstFrame + i * stride, seconds = frame / AnimationTimelineFrames.Rate;
                if (seconds > end) break;
                double x = (seconds - start) / (end - start) * width;
                dc.DrawLine(new Pen(ink, .8), new(x, 28), new(x, height));
                if (labels)
                {
                    Text(dc, (frame + 1).ToString("0", CultureInfo.InvariantCulture) + "f", x + 3, 0, ink, visual);
                    dc.PushOpacity(.65); Text(dc, seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s", x + 3, 12, ink, visual); dc.Pop();
                }
            }
            return;
        }
        double desired = (end - start) / Math.Max(1, width / 80), power = Math.Pow(10, Math.Floor(Math.Log10(desired)));
        double fraction = desired / power, step = (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * power;
        if (ticks) step = Math.Max(WorldAnimationTimeline.Step, Math.Ceiling(step * 40) / 40);
        double first = Math.Ceiling(start / step) * step;
        for (int i = 0; i < 100; i++)
        {
            double value = first + i * step; if (value > end + step * .001) break;
            double x = (value - start) / (end - start) * width;
            dc.DrawLine(new Pen(ink, labels ? .5 : .15), new(x, labels ? 17 : 0), new(x, height));
            if (labels) Text(dc, ticks ? AnimationTimelineFrames.Frame(value).ToString("0", CultureInfo.InvariantCulture) + "f" : value.ToString("0.###", CultureInfo.InvariantCulture) + "s", x + 3, 1, ink, visual);
        }
    }
    public static void Text(DrawingContext dc, string value, double x, double y, Brush ink, Visual visual, double? maximumWidth = null)
    {
        var text = new FormattedText(WorldAnimationTimeline.Shown(value), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, ink, VisualTreeHelper.GetDpi(visual).PixelsPerDip);
        if (maximumWidth is double width) { text.MaxTextWidth = Math.Max(1, width); text.MaxLineCount = 1; text.Trimming = TextTrimming.CharacterEllipsis; }
        dc.DrawText(text, new(x, y));
    }
    public static void Diamond(DrawingContext dc, Point center, Brush? fill, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open()) { context.BeginFigure(new(center.X, center.Y - 4), fill != null, true); context.LineTo(new(center.X + 4, center.Y), true, false); context.LineTo(new(center.X, center.Y + 4), true, false); context.LineTo(new(center.X - 4, center.Y), true, false); }
        geometry.Freeze(); dc.DrawGeometry(fill, pen, geometry);
    }
}
