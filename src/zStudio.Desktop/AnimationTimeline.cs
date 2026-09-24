using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

/// <summary>Actual dispatch times from the deterministic simulation, not guessed absolute thresholds.</summary>
public sealed class AnimationTimeline : FrameworkElement
{
    private static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register("Surface",typeof(Brush),typeof(AnimationTimeline),new FrameworkPropertyMetadata(SystemColors.WindowBrush,FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty InkProperty = DependencyProperty.Register("Ink",typeof(Brush),typeof(AnimationTimeline),new FrameworkPropertyMetadata(SystemColors.WindowTextBrush,FrameworkPropertyMetadataOptions.AffectsRender));
    public AnimationFrame? Frame { get; set; }
    public Guid SelectedSequence { get; set; }
    public IReadOnlyDictionary<Guid,string> SequenceLabels { get; set; } = new Dictionary<Guid,string>();
    private double LabelWidth => Math.Min(156, ActualWidth * .3);
    public double Duration { get; set; } = 1;
    public event Action<double>? SeekRequested;
    public AnimationTimeline()
    {
        MinHeight = 132; Focusable = true; ClipToBounds = true;
        BindingOperations.SetBinding(this,SurfaceProperty,new Binding(nameof(Window.Background)) { RelativeSource = new(RelativeSourceMode.FindAncestor,typeof(Window),1) });
        BindingOperations.SetBinding(this,InkProperty,new Binding(nameof(Window.Foreground)) { RelativeSource = new(RelativeSourceMode.FindAncestor,typeof(Window),1) });
        AutomationProperties.SetName(this,"Dispatched events. Left and Right seek one frame; Home and End seek the visible range. Event log provides accessible event details.");
        ToolTip = "Actual event trace. Drag to scrub; Ctrl+wheel changes the visible time range.";
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Seek(e); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured) Seek(e); };
        MouseLeftButtonUp += (_, e) => { ReleaseMouseCapture(); e.Handled = true; };
        MouseWheel += (_, e) => { if (Keyboard.Modifiers != ModifierKeys.Control) return; Duration = Math.Clamp(Duration * (e.Delta > 0 ? .8 : 1.25), AnimationPlayer.StepSeconds, 3600); InvalidateVisual(); e.Handled = true; };
        KeyDown += (_,e) =>
        {
            double? target = e.Key switch { Key.Left => (Frame?.Time ?? 0) - AnimationPlayer.StepSeconds, Key.Right => (Frame?.Time ?? 0) + AnimationPlayer.StepSeconds, Key.Home => 0, Key.End => Duration, _ => null };
            if (target is double time) { SeekRequested?.Invoke(Math.Clamp(time,0,Duration)); e.Handled = true; }
        };
    }
    private void Seek(MouseEventArgs e) => SeekRequested?.Invoke(Math.Clamp((e.GetPosition(this).X - LabelWidth) / Math.Max(1, ActualWidth - LabelWidth) * Duration, 0, Duration));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        Brush surface = SystemParameters.HighContrast ? SystemColors.WindowBrush : (Brush)GetValue(SurfaceProperty);
        Brush ink = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : (Brush)GetValue(InkProperty);
        dc.DrawRectangle(surface,null,bounds);
        double left = LabelWidth, width = Math.Max(1, ActualWidth - left); double duration = Math.Max(.01, Duration);
        int ticks = Math.Clamp((int)(width / 80), 2, 10);
        for (int i = 0; i < ticks; i++)
        {
            double x = left + i * width / ticks; dc.DrawLine(new Pen(ink, .3), new(x, 19), new(x, ActualHeight));
            Text(dc, (i * duration / ticks).ToString("0.##", CultureInfo.InvariantCulture) + "s", x + 3, 1, ink);
        }
        if (Frame == null) return;
        var sequences = Frame.Sequences.Where(s => s.Instance == 1).Select(s => s.Sequence).Distinct().ToArray();
        int lanes = Math.Max(1, Math.Min(6, sequences.Length)); double row = Math.Max(8, (ActualHeight - 24) / lanes);
        dc.PushClip(new RectangleGeometry(new Rect(0, 20, Math.Max(0,left - 5), Math.Max(0,ActualHeight - 20))));
        for (int lane = 0; lane < Math.Min(lanes,sequences.Length); lane++)
        {
            string label = SequenceLabels.GetValueOrDefault(sequences[lane]) ?? Frame.Sequences.First(s => s.Instance == 1 && s.Sequence == sequences[lane]).Name;
            var text = new FormattedText(label,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,ink,VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1,left - 10), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(text,new(3,22 + row * lane));
        }
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(left,20,width,Math.Max(0,ActualHeight - 20))));
        foreach (var t in Frame.Trace.Where(t => t.Instance == 1).TakeLast(1200))
        {
            int lane = Array.IndexOf(sequences, t.Sequence); if (lane < 0 || lane >= lanes || t.Start > duration) continue;
            double start = left + Math.Max(0, t.Start / duration * width), end = left + Math.Min(width, (t.End ?? Frame.Time) / duration * width);
            var brush = t.Sequence == SelectedSequence ? Brushes.DeepSkyBlue : t.Status == "Engine-based" ? Brushes.SteelBlue : Brushes.DarkGoldenrod;
            bool supported = t.Status == "Engine-based";
            var rect = new Rect(start,22 + row * lane,Math.Max(3,end - start),Math.Max(4,row - 3));
            var outline = t.Sequence == SelectedSequence ? new Pen(SystemColors.HighlightBrush,2) : !supported ? new Pen(ink,1) { DashStyle = DashStyles.Dot } : null;
            dc.DrawRoundedRectangle(SystemParameters.HighContrast ? supported ? SystemColors.HighlightBrush : surface : brush,outline,rect,2,2);
            if (!supported && rect.Width > 14 && rect.Height > 12) Text(dc,"△",start + 2,rect.Top,ink);
        }
        dc.Pop();
        double cursor = left + Math.Clamp(Frame.Time / duration * width, 0, width);
        dc.DrawLine(new Pen(ink, 1.5), new(cursor, 0), new(cursor, ActualHeight));
        if (Frame.Trace.Count == 0) Text(dc, "Play or seek to inspect dispatched events", left + 8, 30, ink);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    private void Text(DrawingContext dc, string value, double x, double y, Brush brush) => dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
}
