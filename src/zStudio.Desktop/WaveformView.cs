using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Recoil.Zbd.Desktop;

public sealed class WaveformView : FrameworkElement
{
    private float[] peaks = [];
    private double duration;
    public event Action<double>? SeekRequested;
    public void Set(float[] values, double seconds) { peaks = values; duration = seconds; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 32, 42)), null, new Rect(RenderSize));
        Pen pen = new(new SolidColorBrush(Color.FromRgb(66, 164, 217)), Math.Max(1, ActualWidth / Math.Max(1, peaks.Length)));
        for (int i = 0; i < peaks.Length; i++) { double x = (i + 0.5) * ActualWidth / peaks.Length, h = peaks[i] * ActualHeight * 0.45; dc.DrawLine(pen, new(x, ActualHeight / 2 - h), new(x, ActualHeight / 2 + h)); }
        if (peaks.Length == 0) dc.DrawText(new FormattedText("Waveform unavailable for this encoding", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(15, 20));
    }
    protected override void OnMouseDown(MouseButtonEventArgs e) { base.OnMouseDown(e); if (ActualWidth > 0) SeekRequested?.Invoke(Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1) * duration); }
}
