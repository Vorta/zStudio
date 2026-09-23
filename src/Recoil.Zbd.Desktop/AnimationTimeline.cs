using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

/// <summary>Actual dispatch times from the deterministic simulation, not guessed absolute thresholds.</summary>
public sealed class AnimationTimeline : FrameworkElement
{
    public AnimationFrame? Frame { get; set; }
    public Guid SelectedSequence { get; set; }
    public double Duration { get; set; } = 1;
    public event Action<double>? SeekRequested;
    public AnimationTimeline()
    {
        MinHeight = 74; Focusable = true; ClipToBounds = true;
        ToolTip = "Actual event trace. Drag to scrub; Ctrl+wheel changes the visible time range.";
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Seek(e); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured) Seek(e); };
        MouseLeftButtonUp += (_, e) => { ReleaseMouseCapture(); e.Handled = true; };
        MouseWheel += (_, e) => { if (Keyboard.Modifiers != ModifierKeys.Control) return; Duration = Math.Clamp(Duration * (e.Delta > 0 ? .8 : 1.25), AnimationPlayer.StepSeconds, 3600); InvalidateVisual(); e.Handled = true; };
    }
    private void Seek(MouseEventArgs e) => SeekRequested?.Invoke(Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth) * Duration, 0, Duration));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var bounds = new Rect(0, 0, ActualWidth, ActualHeight); dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 33, 40)), null, bounds);
        double width = Math.Max(1, ActualWidth); double duration = Math.Max(.01, Duration);
        int ticks = Math.Clamp((int)(width / 80), 2, 10);
        for (int i = 0; i < ticks; i++)
        {
            double x = i * width / ticks; dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(65, 73, 83)), 1), new(x, 19), new(x, ActualHeight));
            Text(dc, (i * duration / ticks).ToString("0.##", CultureInfo.InvariantCulture) + "s", x + 3, 1, Brushes.LightGray);
        }
        if (Frame == null) return;
        var sequences = Frame.Sequences.Where(s => s.Instance == 1).Select(s => s.Sequence).Distinct().ToArray();
        int lanes = Math.Max(1, Math.Min(6, sequences.Length)); double row = Math.Max(8, (ActualHeight - 24) / lanes);
        foreach (var t in Frame.Trace.Where(t => t.Instance == 1).TakeLast(1200))
        {
            int lane = Array.IndexOf(sequences, t.Sequence); if (lane < 0 || lane >= lanes || t.Start > duration) continue;
            double start = Math.Max(0, t.Start / duration * width), end = Math.Min(width, (t.End ?? Frame.Time) / duration * width);
            var brush = t.Sequence == SelectedSequence ? Brushes.DeepSkyBlue : t.Status == "Engine-based" ? Brushes.SteelBlue : Brushes.DarkGoldenrod;
            dc.DrawRoundedRectangle(brush, null, new(start, 22 + row * lane, Math.Max(3, end - start), Math.Max(4, row - 3)), 2, 2);
        }
        double cursor = Math.Clamp(Frame.Time / duration * width, 0, width);
        dc.DrawLine(new Pen(Brushes.White, 1.5), new(cursor, 0), new(cursor, ActualHeight));
        if (Frame.Trace.Count == 0) Text(dc, "Play or seek to inspect dispatched events", 8, 30, Brushes.Gray);
    }
    private void Text(DrawingContext dc, string value, double x, double y, Brush brush) => dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
}
