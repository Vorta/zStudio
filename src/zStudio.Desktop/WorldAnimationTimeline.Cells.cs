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

/// <summary>Viewport-sized drawing and hit testing; never allocates an element per frame.</summary>
internal sealed class WorldAnimationRowPlot(WorldAnimationTimelineRowView container) : FrameworkElement
{
    private Point pressed;
    private WorldAnimationTimelineMark? pressedMark;
    private string? pressedRow;
    private double? proposed;
    internal WorldAnimationTimeline? Timeline => container.Owner;
    internal WorldAnimationTimelineRow? Row => container.Row;
    private double Begin => Timeline?.VisibleStart ?? 0;
    private double End => Timeline?.VisibleEnd ?? 1;
    private double CellWidth => ActualWidth / Math.Max(AnimationTimelineFrames.Step, End - Begin) / 40;
    private int Group => CellWidth >= 6 ? 1 : (int)Math.Clamp(Math.Ceiling(6 / Math.Max(.000001, CellWidth)), 1, 144000);
    private double X(double seconds) => (seconds - Begin) / Math.Max(AnimationTimelineFrames.Step, End - Begin) * ActualWidth;
    private double At(double x) => Begin + Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * (End - Begin);
    private double Bucket(double seconds) => Math.Floor((AnimationTimelineFrames.Cell(seconds) - 1) / Group) * Group;
    internal double CellStart(double x) => Bucket(At(x)) / 40;
    internal IEnumerable<WorldAnimationTimelineMark> VisibleMarks => (Row?.Marks ?? []).Where(m => double.IsFinite(m.Start)
        && (Bucket(m.Start) / 40 <= End && (Bucket(m.Start) + Group) / 40 > Begin
            || m.End is double end && Math.Min(m.Start, end) <= End && Math.Max(m.Start, end) >= Begin));

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e); Focusable = true; ClipToBounds = true;
        AutomationProperties.SetName(this, "40 fps frame cells; select to inspect and seek available execution");
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (Timeline is not { } owner || Row is not { } row || ActualWidth <= 0) return;
        var ink = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : owner.Foreground;
        var accent = SystemColors.HighlightBrush;
        dc.DrawRectangle(Brushes.Transparent, null, new(0, 0, ActualWidth, ActualHeight));
        if (row.IsGroup) return;
        double first = Math.Floor(Begin * 40 / Group) * Group, width = CellWidth * Group;
        double size = Math.Min(24, Math.Max(2, width - 1)), top = (ActualHeight - size) / 2;
        var marks = VisibleMarks.ToArray();
        var occupied = marks.GroupBy(m => Bucket(m.Start)).ToDictionary(g => g.Key, g => g.ToArray());
        int count = Math.Min(8192, (int)Math.Ceiling(ActualWidth / Math.Max(1, width)) + 2);
        for (int i = 0; i < count; i++)
        {
            double cell = first + i * Group, x = X(cell / 40);
            if (x > ActualWidth) break;
            var rect = new Rect(x + Math.Max(0, (width - size) / 2), top, size, size);
            dc.PushOpacity(.07); dc.DrawRectangle(ink, null, rect); dc.Pop();
            dc.PushOpacity(.25); dc.DrawRectangle(null, new Pen(ink, .5), rect); dc.Pop();
            if (Group == 1 && cell % 5 == 0) { dc.PushOpacity(.4); dc.DrawLine(new Pen(ink, 1), new(x, top), new(x, top + size)); dc.Pop(); }
            if (occupied.TryGetValue(cell, out var entries))
            {
                dc.PushOpacity(.65); dc.DrawRectangle(ink, null, rect); dc.Pop();
                if (entries.Any(m => owner.SelectedRowId == row.Id && owner.SelectedMarkId == m.Id))
                    dc.DrawRectangle(null, new Pen(accent, 2), rect);
                if (entries.Length > 1 && size >= 12) WorldAnimationTimelineDrawing.Text(dc, entries.Length.ToString(), rect.X + 2, top + 1, owner.Background, this);
                foreach (var mark in entries.Take(3))
                {
                    double exact = X(mark.Start);
                    if (Group == 1 && Math.Abs(exact - x) > .25)
                        dc.DrawLine(new Pen(accent, 1.5), new(exact, top), new(exact, top + size));
                }
            }
        }
        foreach (var mark in marks)
        {
            if (mark.End is double end && end != mark.Start)
            {
                double left = Math.Clamp(X(Math.Min(mark.Start, end)), 0, ActualWidth), right = Math.Clamp(X(Math.Max(mark.Start, end)), 0, ActualWidth);
                dc.PushOpacity(.3); dc.DrawLine(new Pen(ink, 2), new(left, ActualHeight - 2), new(right, ActualHeight - 2)); dc.Pop();
            }
        }
        if (row.Marks.Any(m => m.Start < Begin)) WorldAnimationTimelineDrawing.Text(dc, "‹", 0, 1, ink, this);
        if (row.Marks.Any(m => m.Start > End)) WorldAnimationTimelineDrawing.Text(dc, "›", Math.Max(0, ActualWidth - 9), 1, ink, this);
        if (!row.IsSourceClock && !row.IsWindowClock)
        {
            double playhead = owner.RequestedTime ?? owner.State.Time;
            if (playhead >= Begin && playhead <= End) dc.DrawLine(new Pen(accent, 1.5), new(X(playhead), 0), new(X(playhead), ActualHeight));
        }
        if (proposed is double time) dc.DrawRectangle(null, new Pen(accent, 2) { DashStyle = DashStyles.Dash }, new(Math.Clamp(X(time), 0, ActualWidth), top, size, size));
    }

    internal IReadOnlyList<WorldAnimationTimelineMark> Hit(double x)
    {
        double time = At(x), bucket = Bucket(time);
        var hits = VisibleMarks.Where(m => Bucket(m.Start) == bucket).ToArray();
        return hits.Length > 0 ? hits : VisibleMarks.Where(m => m.End is double end && time >= Math.Min(m.Start, end) && time < Math.Max(m.Start, end)).ToArray();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); if (Timeline == null || Row == null) return;
        Focus(); pressed = e.GetPosition(this); pressedRow = Row.Id;
        var hits = Hit(pressed.X); pressedMark = hits.Count == 1 ? hits[0] : null;
        CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || Row is not { } row || row.Id != pressedRow || pressedMark == null) return;
        if (Math.Abs(e.GetPosition(this).X - pressed.X) < 4) return;
        if (row.CanEditTiming && pressedMark.Kind == WorldAnimationTimelineMarkKind.Authored)
            proposed = AnimationTimelineFrames.Snap(pressedMark.Start + At(e.GetPosition(this).X) - At(pressed.X));
        else if (row.CanMoveWindow && pressedMark.Kind == WorldAnimationTimelineMarkKind.Window)
            proposed = AnimationTimelineFrames.Snap((row.WindowStartThreshold ?? pressedMark.Start) + At(e.GetPosition(this).X) - At(pressed.X));
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e); if (!IsMouseCaptured) return;
        double? value = proposed; var mark = pressedMark; string? id = pressedRow; ReleaseMouseCapture();
        if (Timeline is not { } owner || Row is not { } row || row.Id != id) return;
        if (value is double seconds && mark != null && row.Marks.Any(m => m.Id == mark.Id))
        {
            if (row.CanEditTiming) owner.MoveTiming(row, seconds); else owner.MoveWindow(row, seconds);
        }
        else
        {
            var hits = Hit(e.GetPosition(this).X);
            if (hits.Count == 1) owner.Select(row, hits[0]);
            else if (hits.Count > 1) ShowHits(hits, 0, row.Id, owner.State.EvidenceIdentity, owner.ViewMode);
            else { owner.Select(row); if (!row.IsSourceClock && !row.IsWindowClock) owner.RequestSeek(CellStart(e.GetPosition(this).X)); }
        }
        e.Handled = true;
    }
    private void ShowHits(IReadOnlyList<WorldAnimationTimelineMark> hits, int offset, string row, string evidence, string view)
    {
        var menu = new ContextMenu();
        foreach (var mark in hits.Skip(offset).Take(100))
        {
            var item = new MenuItem { Header = WorldAnimationTimeline.Shown($"Frame {AnimationTimelineFrames.Frame(mark.Start):0.###} · {mark.Label}") };
            item.Click += (_, _) => Timeline?.SelectCurrentMark(row, mark.Id, evidence, view); menu.Items.Add(item);
        }
        if (offset + 100 < hits.Count) { var more = new MenuItem { Header = "Next 100 items" }; more.Click += (_, _) => Dispatcher.BeginInvoke(() => ShowHits(hits, offset + 100, row, evidence, view)); menu.Items.Add(more); }
        menu.PlacementTarget = this; menu.Placement = PlacementMode.MousePoint; menu.IsOpen = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); proposed = null; pressedMark = null; pressedRow = null; InvalidateVisual(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsMouseCaptured) { ReleaseMouseCapture(); e.Handled = true; return; }
        if (Timeline?.HandleCanvasKey(e) != true) base.OnKeyDown(e);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    { if (Timeline?.HandleWheel(e.Delta, Keyboard.Modifiers, e.GetPosition(this).X / Math.Max(1, ActualWidth)) == true) e.Handled = true; else base.OnMouseWheel(e); }
    protected override AutomationPeer OnCreateAutomationPeer() => new CellPeer(this);
    private sealed class CellPeer(WorldAnimationRowPlot plot) : FrameworkElementAutomationPeer(plot), IInvokeProvider
    {
        protected override string GetNameCore() => WorldAnimationTimeline.Shown((plot.Row?.Label ?? "Frame cells") + ". " + plot.Row?.TimingLabel);
        protected override List<AutomationPeer>? GetChildrenCore() => plot.VisibleMarks.Take(100).Select(m => (AutomationPeer)new MarkPeer(plot, m)).ToList();
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
        public void Invoke() => plot.Dispatcher.Invoke(() => { if (plot.Row is { } row && plot.Timeline is { } timeline) timeline.Select(row); });
    }
    private sealed class MarkPeer(WorldAnimationRowPlot plot, WorldAnimationTimelineMark mark) : AutomationPeer, IInvokeProvider
    {
        public void Invoke() => plot.Dispatcher.Invoke(() => { if (plot.Row is { } row && row.Marks.Any(m => m.Id == mark.Id)) plot.Timeline?.Select(row, mark); });
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : null;
        protected override string GetNameCore() => WorldAnimationTimeline.Shown($"{mark.Kind} at {mark.Start:R} seconds. {mark.Label}");
        protected override string GetAutomationIdCore() => mark.Id;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => "AnimationOccurrence";
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override bool IsEnabledCore() => plot.IsEnabled;
        protected override bool IsKeyboardFocusableCore() => true;
        protected override bool HasKeyboardFocusCore() => plot.IsKeyboardFocused && plot.Timeline?.SelectedMarkId == mark.Id;
        protected override bool IsOffscreenCore() => !plot.IsVisible;
        protected override Rect GetBoundingRectangleCore() { if (!plot.IsVisible || PresentationSource.FromVisual(plot) == null) return Rect.Empty; var origin = plot.PointToScreen(new(Math.Clamp(plot.X(mark.Start), 0, plot.ActualWidth), 0)); return new(origin, new Size(8, plot.ActualHeight)); }
        protected override Point GetClickablePointCore() => GetBoundingRectangleCore().TopLeft;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override void SetFocusCore() { plot.Focus(); Invoke(); }
        protected override string GetAcceleratorKeyCore() => "";
        protected override string GetAccessKeyCore() => "";
        protected override string GetHelpTextCore() => plot.Row?.IsSourceClock == true ? "Native-clock mark. Select to inspect its source values." : "Select to inspect and seek an available current occurrence.";
        protected override string GetItemStatusCore() => mark.Kind.ToString();
        protected override string GetItemTypeCore() => "Animation occurrence";
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.Horizontal;
        protected override bool IsPasswordCore() => false;
        protected override bool IsRequiredForFormCore() => false;
    }
}
