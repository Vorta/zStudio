using System.Diagnostics;
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

internal sealed partial class AnimationTimingView
{
    /// <summary>The lanes, drawn: only the rows in view are drawn and hit-tested (virtualised by drawing).</summary>
    internal sealed class TimingSurface : FrameworkElement
    {
        private const double MarkHit = 5, ClusterWidth = 4, HandleSize = 10;
        private readonly AnimationTimingView owner;
        private readonly List<(Rect Bounds, AnimationTimingLane Lane, AnimationTimingMark[] Marks)> hits = [];
        private readonly List<(Rect Bounds, AnimationTimingLane Lane, AnimationTimingBand Band)> bandHits = [];
        private readonly List<(Rect Bounds, AnimationTimingStart[] Starts)> startHits = [];
        /// <summary>The start a cluster's list brought to the top; a press on a cluster remembers it, so a click without a drag opens its list.</summary>
        private Guid? chosenStart; private AnimationTimingStart[]? pressedCluster;
        private readonly List<(Rect Bounds, AnimationTimingLane Lane, Rect Chevron)> rowHits = [];
        private (Rect Bounds, AnimationTimingMark Mark)? handle;
        private AnimationTimingStart? dragging; private double? dragTime; private Point pressed;
        private AnimationTimingMark? thresholdDrag; private double? thresholdValue;
        private object? hovered;
        private readonly List<(Rect Bounds, AnimationTimingDropBar Bar)> dropHits = [];
        private AnimationTimingDropBar? dropDrag; private double? dropStart;
        internal int DrawnRows { get; private set; }
        internal int DrawnMarks { get; private set; }

        internal TimingSurface(AnimationTimingView owner)
        {
            this.owner = owner; Focusable = true; ClipToBounds = true;
            FocusVisualStyle = null;
            AutomationProperties.SetName(this, "Timing lanes");
            ToolTipService.SetInitialShowDelay(this, 250); ToolTipService.SetShowDuration(this, 20000);
        }
        internal static double RowHeight(AnimationTimingLane lane) => lane.Kind == AnimationTimingLaneKind.Sequence ? 24 : 28;
        private double ClockLeft => owner.LabelWidth;
        internal double ClockWidth => Math.Max(1, ActualWidth - owner.LabelWidth);
        internal double X(double seconds) => ClockLeft + (seconds - owner.VisibleStart) / Math.Max(1e-9, owner.VisibleEnd - owner.VisibleStart) * ClockWidth;
        internal double At(double x) => owner.VisibleStart + (x - ClockLeft) / ClockWidth * (owner.VisibleEnd - owner.VisibleStart);
        internal void Changed() { InvalidateVisual(); (AutomationPeer as SurfacePeer)?.Refresh(); }
        private SurfacePeer? AutomationPeer => UIElementAutomationPeer.FromElement(this) as SurfacePeer;

        // ------------------------------------------------------------ drawing

        protected override void OnRender(DrawingContext dc)
        {
            hits.Clear(); bandHits.Clear(); startHits.Clear(); rowHits.Clear(); dropHits.Clear(); handle = null; DrawnRows = DrawnMarks = 0;
            var ink = owner.Palette; var g = ink.Graph;
            dc.DrawRectangle(g.Pane, null, new Rect(0, 0, ActualWidth, ActualHeight));
            var timing = owner.Timing;
            if (timing == null || ActualWidth <= owner.LabelWidth) return;
            var state = owner.State;
            double top = -owner.ScrollOffset;
            Dictionary<string, double> centers = new(StringComparer.Ordinal);
            List<(AnimationTimingLane Lane, double Top)> shown = [];
            foreach (var lane in owner.Rows)
            {
                double height = RowHeight(lane);
                centers[lane.Id] = top + height / 2;
                if (top + height >= 0 && top <= ActualHeight) shown.Add((lane, top));
                top += height;
            }
            double clockRight = ActualWidth;
            // Lane backgrounds, the playback range outside shaded, then each row's content.
            foreach (var (lane, y) in shown)
            {
                double height = RowHeight(lane);
                var row = new Rect(0, y, ActualWidth, height);
                bool selected = IsSelected(lane);
                if (lane.Kind == AnimationTimingLaneKind.Group) dc.DrawRectangle(ink.Section, null, row);
                if (selected) dc.DrawRectangle(ink.LaneSelected, null, new Rect(ClockLeft, y, ActualWidth - ClockLeft, height));
                dc.DrawLine(ink.Divider, new Point(ClockLeft, y + height - .5), new Point(clockRight, y + height - .5));
            }
            double rangeA = X(state.RangeStart), rangeB = X(state.RangeEnd);
            if (rangeA > ClockLeft) dc.DrawRectangle(ink.Range, null, new Rect(ClockLeft, 0, Math.Min(rangeA, clockRight) - ClockLeft, ActualHeight));
            if (rangeB < clockRight) dc.DrawRectangle(ink.Range, null, new Rect(Math.Max(ClockLeft, rangeB), 0, clockRight - Math.Max(ClockLeft, rangeB), ActualHeight));
            dc.PushClip(new RectangleGeometry(new Rect(ClockLeft, 0, ClockWidth, ActualHeight)));
            if (state.Stale) dc.PushOpacity(.5);
            foreach (var (lane, y) in shown) DrawLane(dc, lane, y, centers);
            if (state.Stale) dc.Pop();
            // The playhead across the lanes, and a dragged start's ghost.
            double playhead = X(state.Time);
            if (state.Evaluated || state.Time > 0) dc.DrawLine(ink.Playhead, new Point(playhead, 0), new Point(playhead, ActualHeight));
            dc.Pop();
            // The label column over everything to its left.
            dc.DrawRectangle(g.Pane, null, new Rect(0, 0, ClockLeft, ActualHeight));
            foreach (var (lane, y) in shown) DrawLabel(dc, lane, y);
            dc.DrawLine(new Pen(g.Divider, 1), new Point(ClockLeft - .5, 0), new Point(ClockLeft - .5, ActualHeight));
            DrawnRows = shown.Count;
        }

        private bool IsSelected(AnimationTimingLane lane) =>
            lane.Id == owner.SelectedLane || owner.SelectedEvent is Guid ev && lane.Kind == AnimationTimingLaneKind.Sequence && lane.Marks.Any(m => m.Event == ev);

        private void DrawLabel(DrawingContext dc, AnimationTimingLane lane, double y)
        {
            var ink = owner.Palette; var g = ink.Graph;
            double height = RowHeight(lane), right = ClockLeft - 8;
            if (lane.Id == owner.SelectedLane) dc.DrawRectangle(ink.LabelSelected, null, new Rect(0, y, ClockLeft, height));
            // The lane's notices: a ⚠ count at the label's right (the item's Inspector card lists them).
            if (owner.Notices.TryGetValue(lane.Id, out int notices) && notices > 0)
                right -= AnimationGraphNodeView.DrawNoticeBadge(dc, g, right, y + height / 2, notices, VisualTreeHelper.GetDpi(this).PixelsPerDip) + 6;
            double indent = 8 + Math.Min(lane.Depth, 6) * 12;
            Rect chevron = Rect.Empty;
            switch (lane.Kind)
            {
                case AnimationTimingLaneKind.Starts:
                    {
                        var count = Text(lane.State, 10.5, g.T3, g.Text);
                        dc.DrawText(count, new Point(right - count.Width, y + (height - count.Height) / 2));
                        var name = Text("Starts", 12, g.T1, g.Strong, right - count.Width - 8 - indent);
                        dc.DrawText(name, new Point(indent, y + (height - name.Height) / 2));
                        break;
                    }
                case AnimationTimingLaneKind.Group:
                    {
                        chevron = new Rect(indent - 2, y + (height - 16) / 2, 16, 16);
                        AnimationGraphGlyphs.Draw(dc, lane.Expanded ? "chevron" : "chevronRight", new Point(indent, y + (height - 12) / 2), 12, g.T3);
                        double x = indent + 16;
                        var status = Text(lane.State, 10.5, g.T3, g.Text, Math.Max(20, (right - x) * .45));
                        dc.DrawText(status, new Point(right - status.Width, y + (height - status.Height) / 2));
                        var name = Text(lane.Label, 12, g.T1, g.Strong, Math.Max(10, right - status.Width - 6 - x));
                        dc.DrawText(name, new Point(x, y + (height - name.Height) / 2));
                        break;
                    }
                default:
                    {
                        double x = indent + 4;
                        var (glyph, glyphInk) = Glyph(lane.Run, g);
                        double glyphWidth = 0;
                        if (lane.Run == AnimationSequenceRunState.WaitingForRelease)
                        {
                            AnimationGraphGlyphs.Draw(dc, "pause", new Point(right - 11, y + (height - 11) / 2), 11, glyphInk);
                            glyphWidth = 11;
                        }
                        else if (glyph.Length > 0)
                        {
                            var shown = Text(glyph, 11, glyphInk, g.Text);
                            glyphWidth = shown.Width;
                            dc.DrawText(shown, new Point(right - shown.Width, y + (height - shown.Height) / 2));
                        }
                        var name = Text(lane.Label, 12, lane.Cleanup ? g.T2 : g.T1, lane.Cleanup ? g.Italic : g.Text, Math.Max(10, right - glyphWidth - 6 - x));
                        dc.DrawText(name, new Point(x, y + (height - name.Height) / 2));
                        break;
                    }
            }
            rowHits.Add((new Rect(0, y, ActualWidth, height), lane, chevron));
        }
        internal static (string Glyph, Brush Ink) Glyph(AnimationSequenceRunState? run, AnimationGraphPalette g) => run switch
        {
            AnimationSequenceRunState.Running => ("▶", g.Run),
            AnimationSequenceRunState.Complete => ("✓", g.T3),
            AnimationSequenceRunState.WaitingForRelease => ("⏸", g.Release),
            AnimationSequenceRunState.WaitingForThreshold => ("◷", g.T3),
            AnimationSequenceRunState.Blocked => ("⊘", g.Bad),
            AnimationSequenceRunState.Paused => ("‖", g.T3),
            AnimationSequenceRunState.Stopped => ("■", g.T3),
            AnimationSequenceRunState.Unavailable => ("!", g.Bad),
            _ => ("", g.T3),
        };

        private void DrawLane(DrawingContext dc, AnimationTimingLane lane, double y, Dictionary<string, double> centers)
        {
            var ink = owner.Palette; var g = ink.Graph;
            double height = RowHeight(lane), mid = y + height / 2;
            if (lane.Kind == AnimationTimingLaneKind.Starts) { DrawStarts(dc, lane, y, height); return; }
            if (lane.Id == DropLaneId) { DrawDrops(dc, y, height); return; }
            // Bands: the lane's state over time, labelled where they fit; a completion's ✓ waits for the marks (it sits beside them).
            List<(double X, AnimationTimingBand Band)> checks = [];
            foreach (var band in lane.Bands)
            {
                double a = Math.Max(X(band.Start), ClockLeft - 2), b = Math.Min(X(band.End), ActualWidth + 2);
                if (band.Kind == AnimationTimingBandKind.Complete)
                {
                    double at = X(band.Start);
                    if (at >= ClockLeft - 8 && at <= ActualWidth) checks.Add((at, band));
                    continue;
                }
                if (b <= a) continue;
                Rect rect = lane.Kind == AnimationTimingLaneKind.Group ? new Rect(a, mid - 3, b - a, 6) : new Rect(a, y + 3, b - a, height - 6);
                Brush? fill = band.Kind switch
                {
                    AnimationTimingBandKind.WaitingForRelease => ink.Release, AnimationTimingBandKind.WaitingForThreshold => ink.Threshold,
                    AnimationTimingBandKind.Paused => ink.Pause, AnimationTimingBandKind.Blocked => ink.Blocked,
                    AnimationTimingBandKind.Binding => g.Divider, _ => null,
                };
                if (fill != null) dc.DrawRoundedRectangle(fill, null, rect, 2, 2);
                bandHits.Add((new Rect(a, y, b - a, height), lane, band));
                Brush text = band.Kind is AnimationTimingBandKind.Blocked or AnimationTimingBandKind.Unavailable ? g.Bad : band.Kind == AnimationTimingBandKind.WaitingForRelease && band.Label.Contains("no releaser", StringComparison.Ordinal) ? g.Caution : g.T2;
                if (lane.Kind == AnimationTimingLaneKind.Group) continue;
                // The full label where it fits, else its first clause ("waiting for release"), else none (the tooltip has it).
                var label = Text(band.Label, 10.5, text, g.Text);
                double left = Math.Max(a, ClockLeft) + 6;
                if (b - left < label.Width + 10 && band.Label.IndexOf(" · ", StringComparison.Ordinal) is int clause and > 0)
                    label = Text(band.Label[..clause], 10.5, text, g.Text);
                if (b - left >= label.Width + 10)
                {
                    dc.DrawRoundedRectangle(ink.BandBackground, null, new Rect(left - 4, mid - label.Height / 2, label.Width + 8, label.Height), 3, 3);
                    dc.DrawText(label, new Point(left, mid - label.Height / 2));
                }
            }
            if (lane.Kind == AnimationTimingLaneKind.Group)
            {
                // A closed group shows its lanes' marks instead of its start label (the tooltip has it).
                if (lane.Started is double started && (lane.Expanded || lane.Marks.Count == 0 && !(owner.Timing?.AllLanes ?? []).Any(l => l.Group == lane.Id && l.Marks.Count > 0)))
                {
                    string text = "started " + AnimationSeconds.Format(started);
                    double x = X(started);
                    if (x < ClockLeft) { var off = Text("◂ " + text, 10, g.T3, g.Text); dc.DrawText(off, new Point(ClockLeft + 3, y + 2)); }
                    else if (x < ActualWidth) { var label = Text(text, 10, g.T3, g.Text); dc.DrawText(label, new Point(x + 4, y + 2)); }
                }
                // A closed group shows its lanes' marks; refused starts sit on the row itself.
                var marks = lane.Expanded ? lane.Marks : lane.Marks.Concat((owner.Timing?.AllLanes ?? []).Where(l => l.Group == lane.Id && l.Kind == AnimationTimingLaneKind.Sequence).SelectMany(l => l.Marks)).ToArray();
                if (!lane.Expanded) dc.PushOpacity(.75);
                DrawMarks(dc, lane, marks, y, height, centers);
                if (!lane.Expanded) dc.Pop();
                return;
            }
            if (owner.Thresholds) DrawBrackets(dc, lane, y, height);
            int before = hits.Count;
            DrawMarks(dc, lane, lane.Marks, y, height, centers);
            foreach (var (at, band) in checks)
            {
                // Beside a mark at the same time, else just after the completion.
                bool crowded = hits.Skip(before).Any(h => h.Bounds.Left <= at + 6 && h.Bounds.Right >= at - 6 && h.Bounds.Width < 30);
                var check = Text("✓", 10, g.T3, g.Text);
                double x = at + (crowded ? 12 : 4);
                dc.DrawText(check, new Point(x, mid - check.Height / 2));
                bandHits.Add((new Rect(x - 2, y, 14, height), lane, band));
            }
        }

        private void DrawStarts(DrawingContext dc, AnimationTimingLane lane, double y, double height)
        {
            var g = owner.Palette.Graph;
            List<string> off = [];
            double lastRight = double.NegativeInfinity;
            double SecondsOf(AnimationTimingStart start) => dragging?.Id == start.Id && dragTime is double moved ? moved : start.Seconds;
            List<(AnimationTimingStart Start, double Seconds, double X)> shown = [];
            foreach (var start in lane.Starts)
            {
                double seconds = SecondsOf(start), x = X(seconds);
                if (x < ClockLeft - 1) { off.Add(Text(start.Label, 10, g.T3, g.Text).Text + " " + AnimationSeconds.Format(seconds)); continue; }
                if (x > ActualWidth + 1) continue;
                shown.Add((start, seconds, x));
            }
            // Starts within 4 DIP of each other (several starts at one time, the usual case after opening another definition) are
            // one cluster: one marker with a count, every start listed on hover and in its click list. The cluster's top start (the
            // one its list chose, else the primary, else the last) is drawn last, takes the pointer and is what a drag moves.
            foreach (var cluster in Clusters(shown))
            {
                var members = cluster.OrderBy(m => m.Start.Id == chosenStart ? 2 : m.Start.Primary ? 1 : 0).ToArray();
                var (top, seconds, x) = members[^1];
                foreach (var (start, _, at) in members)
                {
                    Brush fill = start.Primary ? g.Accent : g.T2;
                    if (start.Refused != null) fill = g.Bad;
                    var triangle = new StreamGeometry();
                    using (var c = triangle.Open()) { c.BeginFigure(new Point(at - 5.5, y + height - 11), true, true); c.LineTo(new Point(at + 5.5, y + height - 11), true, false); c.LineTo(new Point(at, y + height - 4), true, false); }
                    triangle.Freeze();
                    dc.DrawGeometry(fill, members.Length > 1 ? new Pen(g.Pane, 1) : null, triangle);
                    if (start.PrerequisitesIgnored) dc.DrawEllipse(g.Caution, null, new Point(at + 7.5, y + height - 9), 2.5, 2.5);
                }
                string more = members.Length > 1 ? " +" + (members.Length - 1).ToString(CultureInfo.InvariantCulture) : "";
                var label = Text(top.Label + " " + AnimationSeconds.Format(seconds) + more, 10.5, top.Refused != null ? g.Bad : g.T1, g.Text);
                double left = Math.Max(ClockLeft + 2, x - label.Width / 2);
                bool labelled = left > lastRight + 4;
                if (labelled)
                {
                    dc.DrawRoundedRectangle(g.Pane, null, new Rect(left - 3, y + 1, label.Width + 6, label.Height), 3, 3);
                    dc.DrawText(label, new Point(left, y + 1));
                    lastRight = left + label.Width;
                }
                double minimum = members.Min(m => m.X), maximum = members.Max(m => m.X);
                Rect marker = new(minimum - 8, y, maximum - minimum + 16, height);
                var starts = members.Select(m => m.Start).ToArray();
                // Hits are tested last first: each cluster's top start takes the pointer, members before it lie under it.
                startHits.Add((marker, starts));
                if (labelled) startHits.Add((new Rect(left, y, label.Width, 14), starts));
            }
            if (off.Count > 0) { var text = Text("◂ " + string.Join(", ", off), 10, g.T3, g.Text, Math.Max(40, ClockWidth * .4)); dc.DrawText(text, new Point(ClockLeft + 3, y + height - text.Height - 2)); }
        }
        private static IEnumerable<List<(AnimationTimingStart Start, double Seconds, double X)>> Clusters(List<(AnimationTimingStart Start, double Seconds, double X)> shown)
        {
            List<(AnimationTimingStart Start, double Seconds, double X)>? cluster = null;
            foreach (var item in shown.OrderBy(s => s.X))
            {
                if (cluster != null && item.X - cluster[^1].X <= ClusterWidth) { cluster.Add(item); continue; }
                if (cluster != null) yield return cluster;
                cluster = [item];
            }
            if (cluster != null) yield return cluster;
        }
        /// <summary>The cluster of starts under <paramref name="p"/> (its top start last), topmost hit first.</summary>
        private AnimationTimingStart[]? StartsAt(Point p) => p.X < ClockLeft ? null : startHits.LastOrDefault(s => s.Bounds.Contains(p)).Starts;
        /// <summary>A cluster's list: each of its starts, to bring one to the top (so a drag moves it) and show the Scenario tab.</summary>
        private void ShowStartList(AnimationTimingStart[] starts)
        {
            ContextMenu menu = new() { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            foreach (var start in starts.Reverse())
            {
                MenuItem item = new()
                {
                    Header = new TextBlock { Text = $"{start.Label} · {AnimationSeconds.Format(start.Seconds)}" + (start.Primary ? " · primary" : "") + (start.Refused != null ? " · refused" : "") },
                    IsChecked = ReferenceEquals(start, starts[^1]),
                    ToolTip = "Bring this start to the top of the cluster: a drag on the marker then moves it",
                };
                item.Click += (_, _) => { chosenStart = start.Id; InvalidateVisual(); owner.ShowScenario(); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        /// <summary>
        /// The Drop windows lane: each window of each flight cycle as a bar on the flight sequence's clock (cyan, like its start
        /// marker in the view), numbered; the selected window outlined; a dragged one as a ghost at its proposed start.
        /// </summary>
        private void DrawDrops(DrawingContext dc, double y, double height)
        {
            var g = owner.Palette.Graph;
            if (owner.State.Drops is not { } drops) return;
            // A stale analysis keeps its bars where it found the windows, muted with a dashed edge, and says so at the lane's start.
            var fill = new SolidColorBrush(Color.FromArgb(drops.Stale ? (byte)36 : g.Dark ? (byte)110 : (byte)90, 0x22, 0xC6, 0xE6)); fill.Freeze();
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(drops.Stale ? (byte)150 : (byte)255, 0x22, 0xC6, 0xE6)), 1.25); if (drops.Stale) edge.DashStyle = new DashStyle([3, 2], 0); edge.Freeze();
            var ghost = new Pen(g.Accent, 1.5) { DashStyle = new DashStyle([2, 2], 0) }; ghost.Freeze();
            if (drops.Bars.Count == 0 && drops.Note.Length > 0)
            {
                var note = Text(drops.Note, 10.5, g.T3, g.Italic, Math.Max(40, ClockWidth - 8));
                dc.DrawText(note, new Point(ClockLeft + 6, y + (height - note.Height) / 2));
                return;
            }
            foreach (var bar in drops.Bars)
            {
                double a = X(bar.Cycle + bar.Start), b = X(bar.End);
                if (b < ClockLeft - 2 || a > ActualWidth + 2) continue;
                var rect = new Rect(Math.Max(a, ClockLeft - 2), y + 4, Math.Max(2, Math.Min(b, ActualWidth + 2) - Math.Max(a, ClockLeft - 2)), height - 8);
                dc.DrawRoundedRectangle(fill, edge, rect, 3, 3);
                if (drops.Selected == bar.Window) dc.DrawRoundedRectangle(null, g.AccentOutline, new Rect(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4), 4, 4);
                var number = Text(bar.Number.ToString(CultureInfo.InvariantCulture), 10, drops.Stale ? g.T3 : g.T1, g.Strong);
                if (rect.Width >= number.Width + 6) dc.DrawText(number, new Point(rect.X + 4, y + (height - number.Height) / 2));
                dropHits.Add((new Rect(rect.X - 3, y, rect.Width + 6, height), bar));
                if (dropDrag is { } dragged && dropStart is double proposed && dragged.Window == bar.Window && dragged.Cycle == bar.Cycle)
                {
                    double ga = X(bar.Cycle + proposed), gb = X(bar.Cycle + proposed + bar.Duration);
                    dc.DrawRoundedRectangle(null, ghost, new Rect(ga, y + 3, Math.Max(2, gb - ga), height - 6), 3, 3);
                    var label = Text("starts " + AnimationSeconds.Format(proposed), 10, g.Accent, g.Text);
                    dc.DrawRoundedRectangle(g.Pane, null, new Rect(gb + 4, y + (height - label.Height) / 2 - 1, label.Width + 6, label.Height + 2), 3, 3);
                    dc.DrawText(label, new Point(gb + 7, y + (height - label.Height) / 2));
                }
            }
            if (drops.Stale && drops.Note.Length > 0)
            {
                var stale = Text(drops.Note, 10.5, g.Caution, g.Text, Math.Max(40, ClockWidth - 16));
                var pill = new Rect(ClockLeft + 4, y + (height - stale.Height) / 2 - 1, stale.Width + 10, stale.Height + 2);
                dc.DrawRoundedRectangle(owner.Palette.BandBackground, new Pen(g.Caution, 1) { DashStyle = new DashStyle([3, 2], 0) }, pill, 3, 3);
                dc.DrawText(stale, new Point(pill.X + 5, pill.Y + 1));
            }
        }

        /// <summary>Threshold brackets for events whose anchor occurred: anchor → anchor + threshold, labelled; the selected event's end drags.</summary>
        private void DrawBrackets(DrawingContext dc, AnimationTimingLane lane, double y, double height)
        {
            var ink = owner.Palette; var g = ink.Graph;
            AnimationTimingMark? editable = null;
            if (owner.SelectedEvent is Guid selected && owner.CanEditThreshold(selected))
                editable = lane.Marks.FirstOrDefault(m => m.Id == owner.SelectedMark && m.Bracket != null) ?? lane.Marks.LastOrDefault(m => m.Event == selected && m.Bracket != null && X(m.Bracket.End) >= ClockLeft && X(m.Bracket.Anchor) <= ActualWidth);
            foreach (var mark in lane.Marks)
            {
                if (mark.Bracket is not { } bracket) continue;
                double end = thresholdDrag?.Id == mark.Id && thresholdValue is double proposed ? bracket.Anchor + proposed : bracket.End;
                double a = X(bracket.Anchor), b = X(end);
                if (b < ClockLeft || a > ActualWidth || b - a < 1) continue;
                double top = y + 4, bottom = y + height - 4;
                var figure = new StreamGeometry();
                using (var c = figure.Open()) { c.BeginFigure(new Point(a, top), false, false); c.LineTo(new Point(a, bottom), true, true); c.LineTo(new Point(b, bottom), true, true); c.LineTo(new Point(b, top), true, true); }
                figure.Freeze();
                bool active = mark == editable;
                dc.DrawGeometry(null, active ? ink.BracketHandle : ink.Bracket, figure);
                string text = thresholdDrag?.Id == mark.Id && thresholdValue is double value ? "≥ " + AnimationSeconds.Short(value) + " → " + AnimationSeconds.Format(end) : bracket.Label;
                var label = Text(text, 9.5, active ? g.Accent : g.T3, g.Text);
                // Inside the bracket where it fits; the active one, else beside its handle.
                if (b - a >= label.Width + 8) dc.DrawText(label, new Point(a + 4, bottom - label.Height));
                else if (active) { dc.DrawRoundedRectangle(ink.BandBackground, null, new Rect(b + 7, bottom - label.Height - 1, label.Width + 6, label.Height), 3, 3); dc.DrawText(label, new Point(b + 10, bottom - label.Height - 1)); }
                if (bracket.Gap is { } gap && X(mark.Start) - b > 30)
                {
                    var why = Text(gap, 9.5, g.T3, g.Italic);
                    double x = (b + X(mark.Start)) / 2 - why.Width / 2;
                    if (X(mark.Start) - b >= why.Width + 6) dc.DrawText(why, new Point(x, y + 1));
                    dc.DrawLine(new Pen(g.T4, 1) { DashStyle = DashStyles.Dot }, new Point(b, bottom), new Point(X(mark.Start), bottom));
                }
                if (active)
                {
                    var box = new Rect(b - HandleSize / 2, bottom - HandleSize + 2, HandleSize, HandleSize);
                    dc.DrawRoundedRectangle(g.Accent, new Pen(g.Pane, 1.5), box, 2, 2);
                    handle = (box, mark);
                }
            }
        }

        private void DrawMarks(DrawingContext dc, AnimationTimingLane lane, IReadOnlyList<AnimationTimingMark> marks, double y, double height, Dictionary<string, double> centers)
        {
            var ink = owner.Palette; var g = ink.Graph;
            double mid = y + height / 2;
            double begin = owner.VisibleStart, end = owner.VisibleEnd;
            // Bars first: executed intervals, running events (fading out), blocked waits.
            foreach (var mark in marks)
            {
                if (mark.Kind is not (AnimationTimingMarkKind.Interval or AnimationTimingMarkKind.Running or AnimationTimingMarkKind.Blocked) || mark.End is not double stop) continue;
                if (stop < begin || mark.Start > end) continue;
                double a = Math.Max(X(mark.Start), ClockLeft - 4), b = Math.Max(a + 1.5, Math.Min(X(stop), ActualWidth + 4));
                var rect = lane.Kind == AnimationTimingLaneKind.Group ? new Rect(a, mid - 2, b - a, 4) : new Rect(a, mid - 4, b - a, 8);
                if (mark.Kind == AnimationTimingMarkKind.Blocked)
                {
                    dc.DrawRectangle(ink.Blocked, null, new Rect(a, y + 3, b - a, height - 6));
                    DrawPoint(dc, mark, X(mark.Start), mid, g, 1);
                }
                else
                {
                    Brush fill = g.Family(mark.Family);
                    if (mark.Kind == AnimationTimingMarkKind.Running)
                    {
                        var color = ((SolidColorBrush)fill).Color;
                        var fade = new LinearGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B), 0) { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                        fade.GradientStops.Insert(1, new GradientStop(color, .7)); fade.Freeze();
                        dc.DrawRoundedRectangle(fade, null, rect, 2, 2);
                        var arrow = Text("▸", 10, g.T2, g.Text); dc.DrawText(arrow, new Point(b + 2, mid - arrow.Height / 2));
                    }
                    else dc.DrawRoundedRectangle(fill, null, rect, 2, 2);
                    if (IsMarkSelected(mark)) dc.DrawRoundedRectangle(null, g.AccentOutline, new Rect(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4), 3, 3);
                }
                hits.Add((new Rect(a, y, b - a, height), lane, [mark]));
                DrawnMarks++;
            }
            // Points, clustered within 4 DIP: the first is drawn, a badge counts them, hover lists them.
            List<(double X, AnimationTimingMark Mark)> points = [];
            foreach (var mark in marks)
            {
                if (mark.Kind is AnimationTimingMarkKind.Interval or AnimationTimingMarkKind.Running) continue;
                if (mark.Start < begin - 1e-9 || mark.Start > end + 1e-9) continue;
                points.Add((X(mark.Start), mark));
            }
            points.Sort((a, b) => a.X.CompareTo(b.X));
            int earlier = marks.Count(m => m.Kind is not (AnimationTimingMarkKind.Interval or AnimationTimingMarkKind.Running) && m.Start < begin - 1e-9);
            if (earlier > 0)
            {
                var first = marks.First(m => m.Start < begin - 1e-9);
                var off = Text($"◂ {earlier.ToString(CultureInfo.InvariantCulture)} from {AnimationSeconds.Format(first.Start)}", 10, g.T3, g.Text);
                dc.DrawText(off, new Point(ClockLeft + 3, mid - off.Height / 2));
            }
            for (int i = 0; i < points.Count;)
            {
                // Marks each within 4 DIP of the previous form one cluster; a long run of them (a polling loop) is drawn as a rail
                // from its first to its last mark, its count after it.
                int j = i + 1;
                while (j < points.Count && points[j].X - points[j - 1].X <= ClusterWidth) j++;
                var cluster = points.Skip(i).Take(j - i).Select(p => p.Mark).ToArray();
                var lead = cluster.FirstOrDefault(IsMarkSelected) ?? cluster.OrderBy(Priority).First();
                double x = points[i].X, last = points[j - 1].X;
                bool rail = last - x > 6;
                if (rail)
                {
                    dc.DrawLine(new Pen(g.Family(lead.Family) == g.T2 ? g.T3 : g.Family(lead.Family), 2), new Point(x, mid), new Point(last, mid));
                    dc.DrawEllipse(g.T2, null, new Point(last, mid), 2, 2);
                }
                if (lead.Kind != AnimationTimingMarkKind.Blocked) DrawPoint(dc, lead, x, mid, g, 1);
                if (cluster.Length > 1)
                {
                    // After the rail, or after a condition's T or F, else at the mark's upper right.
                    var badge = Text(cluster.Length.ToString(CultureInfo.InvariantCulture), 9.5, g.T2, g.Text);
                    double at = rail ? last + 5 : x + (lead.Kind == AnimationTimingMarkKind.Condition && lead.Value != null ? 15 : 4);
                    dc.DrawText(badge, new Point(at, rail ? mid - badge.Height / 2 : y + 1));
                }
                if (lead.Kind == AnimationTimingMarkKind.Release && lead.TargetLane is { } target && centers.TryGetValue(target, out double to))
                {
                    var pen = new Pen(g.Release, 1.25);
                    double from = mid - 5;
                    double reach = Math.Abs(to - from) < 200 ? to : from + Math.Sign(to - from) * 8;
                    dc.DrawLine(pen, new Point(x, from), new Point(x, reach));
                    dc.DrawEllipse(g.Release, null, new Point(x, reach), 1.8, 1.8);
                }
                if (cluster.Any(IsMarkSelected)) dc.DrawRectangle(null, g.AccentOutline, new Rect(x - 7.5, mid - 7.5, 15, 15));
                hits.Add((new Rect(x - MarkHit, y, last - x + MarkHit * 2 + (cluster.Length > 1 ? 10 : 0), height), lane, cluster));
                DrawnMarks += cluster.Length;
                i = j;
            }
        }
        private static int Priority(AnimationTimingMark mark) => mark.Kind switch
        {
            AnimationTimingMarkKind.Refused => 0, AnimationTimingMarkKind.Blocked => 1, AnimationTimingMarkKind.Condition => 2, AnimationTimingMarkKind.Release => 3,
            AnimationTimingMarkKind.Executed => 4, AnimationTimingMarkKind.Excluded => 5, AnimationTimingMarkKind.Duplicate => 6, AnimationTimingMarkKind.Loop => 7, _ => 8,
        };
        private bool IsMarkSelected(AnimationTimingMark mark) => mark.Id == owner.SelectedMark;

        /// <summary>One point mark: shape carries the kind, colour the family (design §9, "Marks").</summary>
        private void DrawPoint(DrawingContext dc, AnimationTimingMark mark, double x, double y, AnimationGraphPalette g, double opacity)
        {
            Brush family = g.Family(mark.Family);
            switch (mark.Kind)
            {
                case AnimationTimingMarkKind.Condition:
                    {
                        Brush color = g.T2;
                        dc.DrawGeometry(g.Pane, new Pen(mark.Forced ? g.Accent : color, mark.Forced ? 2 : 1.5), Diamond(x, y, 5));
                        if (mark.Value is bool value) { var t = Text(value ? "T" : "F", 9.5, color, g.Strong); dc.DrawText(t, new Point(x + 6, y - t.Height / 2)); }
                        break;
                    }
                case AnimationTimingMarkKind.Skipped:
                    dc.PushOpacity(.55);
                    dc.DrawGeometry(null, new Pen(g.T3, 1.5), Diamond(x, y, 4.5));
                    dc.DrawLine(new Pen(g.T2, 1.25), new Point(x - 3.5, y + 3.5), new Point(x + 3.5, y - 3.5));
                    dc.Pop();
                    break;
                case AnimationTimingMarkKind.Excluded:
                    dc.DrawRectangle(g.Pane, new Pen(g.Call, 1.5), new Rect(x - 4, y - 4, 8, 8));
                    break;
                case AnimationTimingMarkKind.Duplicate:
                    dc.DrawRectangle(g.Pane, new Pen(g.Call, 1.5) { DashStyle = new DashStyle([1.5, 1.5], 0) }, new Rect(x - 4, y - 4, 8, 8));
                    dc.DrawEllipse(g.Call, null, new Point(x, y), 1.3, 1.3);
                    break;
                case AnimationTimingMarkKind.Refused:
                    dc.DrawEllipse(g.Pane, new Pen(g.Stop, 1.5), new Point(x, y), 5, 5);
                    dc.DrawLine(new Pen(g.Stop, 1.5), new Point(x - 3.4, y + 3.4), new Point(x + 3.4, y - 3.4));
                    break;
                case AnimationTimingMarkKind.Blocked:
                    dc.DrawEllipse(g.Pane, new Pen(g.Bad, 1.5), new Point(x, y), 5, 5);
                    dc.DrawLine(new Pen(g.Bad, 1.5), new Point(x - 3.4, y - 3.4), new Point(x + 3.4, y + 3.4));
                    break;
                case AnimationTimingMarkKind.Release:
                    dc.DrawGeometry(g.Release, null, Diamond(x, y, 4.5));
                    break;
                case AnimationTimingMarkKind.Loop:
                    AnimationGraphGlyphs.Draw(dc, "loop", new Point(x - 5.5, y - 5.5), 11, g.T2);
                    break;
                case AnimationTimingMarkKind.Unresolved:
                    dc.DrawGeometry(null, new Pen(g.T3, 1.25) { DashStyle = new DashStyle([1.5, 1.5], 0) }, Diamond(x, y, 4.5));
                    break;
                default:
                    dc.DrawGeometry(family, null, Diamond(x, y, 4.5));
                    break;
            }
        }
        private static Geometry Diamond(double x, double y, double half)
        {
            var geometry = new StreamGeometry();
            using (var c = geometry.Open()) { c.BeginFigure(new Point(x, y - half), true, true); c.LineTo(new Point(x + half, y), true, true); c.LineTo(new Point(x, y + half), true, true); c.LineTo(new Point(x - half, y), true, true); }
            geometry.Freeze(); return geometry;
        }
        private FormattedText Text(string text, double size, Brush brush, Typeface face, double? width = null)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            if (width is double maximum) { formatted.MaxTextWidth = Math.Max(1, maximum); formatted.MaxLineCount = 1; formatted.Trimming = TextTrimming.CharacterEllipsis; }
            return formatted;
        }

        // ------------------------------------------------------------ pointer and keys

        private (AnimationTimingLane Lane, Rect Chevron)? RowAt(Point p) => rowHits.FirstOrDefault(r => r.Bounds.Contains(p)) is { Lane: not null } row ? (row.Lane, row.Chevron) : null;
        private (AnimationTimingLane Lane, AnimationTimingMark[] Marks)? MarksAt(Point p)
        {
            (AnimationTimingLane, AnimationTimingMark[])? best = null; double distance = double.MaxValue;
            foreach (var (bounds, lane, marks) in hits)
            {
                if (!bounds.Contains(p)) continue;
                bool point = marks.Length > 1 || marks[0].Kind is not (AnimationTimingMarkKind.Interval or AnimationTimingMarkKind.Running);
                double d = point ? Math.Abs(bounds.X + MarkHit - p.X) : 3;
                if (d < distance) { distance = d; best = (lane, marks); }
            }
            return best;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus(); var p = e.GetPosition(this); pressed = p;
            if (handle is { } h && Inflate(h.Bounds, 3).Contains(p) && CaptureMouse()) { thresholdDrag = h.Mark; thresholdValue = null; e.Handled = true; return; }
            if (p.X >= ClockLeft && dropHits.LastOrDefault(d => d.Bounds.Contains(p)) is { Bar: not null } drop)
            {
                // A click selects the window; with an editable lane a drag (past 3 DIP) moves its start, never its length.
                dropStart = null;
                if (owner.State.Drops?.Editable == true && CaptureMouse()) dropDrag = drop.Bar; else owner.SelectDropWindow(drop.Bar.Window);
                e.Handled = true; return;
            }
            if (StartsAt(p) is { Length: > 0 } starts)
            {
                // The top start of the marker moves with a drag; a click on a cluster without a drag lists its starts.
                owner.ShowScenario();
                dragging = starts[^1]; dragTime = null; pressedCluster = starts.Length > 1 ? starts : null; CaptureMouse();
                e.Handled = true; return;
            }
            if (RowAt(p) is not { } row) return;
            if (p.X < ClockLeft)
            {
                if (row.Lane.Kind == AnimationTimingLaneKind.Group && (row.Chevron.Contains(p) || e.ClickCount == 2)) owner.Toggle(row.Lane);
                else if (row.Lane.Kind == AnimationTimingLaneKind.Starts) owner.ShowScenario();
                else owner.ChooseLane(row.Lane);
                e.Handled = true; return;
            }
            if (MarksAt(p) is { } found)
            {
                var mark = found.Marks.FirstOrDefault(m => m.Id != owner.SelectedMark) is { } next && found.Marks.Length > 1 && found.Marks.Any(m => m.Id == owner.SelectedMark)
                    ? found.Marks[(Array.FindIndex(found.Marks, m => m.Id == owner.SelectedMark) + 1) % found.Marks.Length] : found.Marks.OrderBy(Priority).First();
                var lane = owner.Timing?.AllLanes.FirstOrDefault(l => l.Kind == AnimationTimingLaneKind.Sequence && l.Marks.Contains(mark)) ?? found.Lane;
                owner.ChooseMark(lane, mark);
                e.Handled = true; return;
            }
            if (row.Lane.Kind == AnimationTimingLaneKind.Group && e.ClickCount == 2) owner.Toggle(row.Lane);
            else if (row.Lane.Kind != AnimationTimingLaneKind.Starts) owner.ChooseLane(row.Lane);
            e.Handled = true;
        }
        private static Rect Inflate(Rect rect, double by) { rect.Inflate(by, by); return rect; }
        private static AnimationSnap Snap() => Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? AnimationSnap.Free : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? AnimationSnap.Coarse : AnimationSnap.Grid;
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            if (IsMouseCaptured && dropDrag is { } dropping)
            {
                if (Math.Abs(p.X - pressed.X) < 3 && dropStart == null) return;
                double proposed = AnimationSeconds.Snap(dropping.Start + At(p.X) - At(pressed.X), Snap());
                dropStart = Math.Clamp(proposed, Math.Max(0, dropping.Minimum), dropping.Maximum);
                ToolTip = $"Drop window {dropping.Number} starts at {AnimationSeconds.Format(dropStart.Value)} on the flight sequence's clock (0.025 s; Shift 0.1 s; Ctrl free)";
                InvalidateVisual(); return;
            }
            if (IsMouseCaptured && dragging != null)
            {
                if (Math.Abs(p.X - pressed.X) < 3 && dragTime == null) return;
                dragTime = AnimationSeconds.Snap(At(p.X), Snap()); InvalidateVisual();
                ToolTip = dragging.Label + " starts at " + AnimationSeconds.Format(dragTime.Value) + " (0.025 s; Shift 0.1 s; Ctrl free)";
                return;
            }
            if (IsMouseCaptured && thresholdDrag?.Bracket is { } bracket)
            {
                // The same 3 DIP dead zone as a start drag: a click on the handle proposes nothing.
                if (Math.Abs(p.X - pressed.X) < 3 && thresholdValue == null) return;
                thresholdValue = Math.Max(0, AnimationSeconds.Snap(Math.Max(0, At(p.X) - bracket.Anchor), Snap()));
                ToolTip = "Start threshold ≥ " + AnimationSeconds.Short(thresholdValue.Value) + (bracket.Clock switch { 2 => " after the sequence starts", 3 => " after previous", _ => " after the animation starts" })
                    + " (0.025 s; Shift 0.1 s; Ctrl free)";
                InvalidateVisual(); return;
            }
            Hover(p);
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!IsMouseCaptured) return;
            var start = dragging; double? moved = dragTime; var drag = thresholdDrag; double? proposed = thresholdValue;
            var window = dropDrag; double? windowStart = dropStart; dropDrag = null; dropStart = null;
            var cluster = pressedCluster; pressedCluster = null;
            dragging = null; dragTime = null; thresholdDrag = null; thresholdValue = null;
            ReleaseMouseCapture(); InvalidateVisual();
            if (start != null && moved is double seconds && Math.Abs(seconds - start.Seconds) > 1e-9) owner.MoveStart(start.Id, seconds);
            else if (start != null && moved == null && cluster != null) ShowStartList(cluster);
            if (window != null) { if (windowStart is double local && Math.Abs(local - window.Start) > 1e-9) owner.MoveDropWindow(window.Window, local); else owner.SelectDropWindow(window.Window); }
            if (drag != null && proposed is double threshold && drag.Bracket is { } b && Math.Abs(threshold - b.Threshold) > 1e-9) owner.ProposeThreshold(drag, threshold);
            e.Handled = true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); if (dragging != null || thresholdDrag != null || dropDrag != null) { dragging = null; dragTime = null; pressedCluster = null; thresholdDrag = null; thresholdValue = null; dropDrag = null; dropStart = null; InvalidateVisual(); } }
        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            var p = e.GetPosition(this);
            if (StartsAt(p) is not { Length: > 0 } starts) return;
            ContextMenu menu = new() { PlacementTarget = this };
            // One Remove per start of the marker (a cluster lists each, its top start first).
            foreach (var start in starts.Reverse())
            {
                string text = starts.Length > 1 ? $"Remove {start.Label} · {AnimationSeconds.Format(start.Seconds)}" : "Remove start";
                MenuItem remove = new() { Header = new TextBlock { Text = text }, IsEnabled = !start.Primary, ToolTip = start.Primary ? "The primary start is the opened binding; it cannot be removed" : null };
                ToolTipService.SetShowOnDisabled(remove, true);
                remove.Click += (_, _) => owner.RemoveStart(start.Id);
                menu.Items.Add(remove);
            }
            MenuItem scenario = new() { Header = new TextBlock { Text = "Show in Scenario" } };
            scenario.Click += (_, _) => owner.ShowScenario();
            menu.Items.Add(scenario);
            menu.IsOpen = true; e.Handled = true;
        }
        protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); hovered = null; ToolTip = null; Cursor = null; }
        private void Hover(Point p)
        {
            object? found = null; string? tip = null; Cursor? cursor = null;
            if (p.X >= ClockLeft && dropHits.LastOrDefault(d => d.Bounds.Contains(p)) is { Bar: not null } window)
            {
                var bar = window.Bar; found = bar; cursor = owner.State.Drops?.Editable == true ? Cursors.SizeWE : null;
                tip = $"Drop window {bar.Number} · starts {AnimationSeconds.Format(bar.Start)} on the flight sequence's clock ({AnimationSeconds.Format(bar.Cycle + bar.Start)} on the shared clock) · lasts {AnimationSeconds.Format(bar.Duration)}"
                    + (owner.State.Drops?.Editable == true ? "\nClick to select it; drag to move its start (0.025 s; Shift 0.1 s; Ctrl free); Apply in the Inspector keeps it" : "");
            }
            else if (handle is { } h && Inflate(h.Bounds, 3).Contains(p)) { found = h.Mark; tip = "Drag to propose a new start threshold in this event's clock (0.025 s; Shift 0.1 s; Ctrl free)"; cursor = Cursors.SizeWE; }
            else if (StartsAt(p) is { Length: > 0 } starts)
            {
                var start = starts[^1];
                found = start; cursor = Cursors.SizeWE;
                static string About(AnimationTimingStart s) => (s.PrerequisitesIgnored ? " · prerequisites ignored by the scenario's rule" : "") + (s.Refused is { } reason ? " · refused: " + reason : "");
                tip = starts.Length == 1
                    ? $"{start.Label} starts at {AnimationSeconds.Format(start.Seconds)} · drag to move (0.025 s; Shift 0.1 s; Ctrl free) · right-click to remove"
                      + (start.PrerequisitesIgnored ? "\nIts unmet prerequisites are ignored by the scenario's rule (Scenario → Rules → Prerequisites)." : "")
                      + (start.Refused is { } reason ? "\nRefused: " + reason : "")
                    : $"{starts.Length.ToString(CultureInfo.InvariantCulture)} starts here:\n" + string.Join("\n", starts.Reverse().Take(32).Select(s => $"{(s.Primary ? "★ " : "▼ ")}{s.Label} · {AnimationSeconds.Format(s.Seconds)}{About(s)}"))
                      + $"\nDrag to move {start.Label} (0.025 s; Shift 0.1 s; Ctrl free) · click to choose another · right-click to remove one";
            }
            else if (p.X >= ClockLeft && MarksAt(p) is { } marks)
            {
                found = marks.Marks[0];
                tip = string.Join("\n", marks.Marks.Take(20).Select(MarkTip)) + (marks.Marks.Length > 20 ? $"\nand {(marks.Marks.Length - 20).ToString(CultureInfo.InvariantCulture)} more" : "");
            }
            else if (p.X >= ClockLeft && bandHits.FirstOrDefault(b => b.Bounds.Contains(p)) is { Band: not null } band)
            {
                found = band.Band;
                tip = band.Band.Label + " · " + AnimationSeconds.Format(band.Band.Start) + (band.Band.Open ? " → now" : " – " + AnimationSeconds.Format(band.Band.End));
            }
            else if (RowAt(p) is { } row)
            {
                found = row.Lane; tip = row.Lane.Tooltip.Length > 0 ? row.Lane.Tooltip : row.Lane.Label;
                if (row.Lane.Kind == AnimationTimingLaneKind.Group && row.Chevron.Contains(p)) tip = (row.Lane.Expanded ? "Collapse " : "Expand ") + row.Lane.Label;
            }
            Cursor = cursor;
            if (ReferenceEquals(found, hovered)) return;
            hovered = found; ToolTip = tip;
        }
        internal static string MarkTip(AnimationTimingMark mark)
        {
            string time = AnimationSeconds.Format(mark.Start) + (mark.End is double end && end > mark.Start + 1e-9 ? "–" + AnimationSeconds.Format(end) : "");
            string text = $"{mark.Label} · {time} · {mark.Result}";
            if (mark.Kind == AnimationTimingMarkKind.Skipped && mark.Count > 1) text += $" ({mark.Count.ToString(CultureInfo.InvariantCulture)} events)";
            if (mark.Bracket is { } bracket) text += $"\n  threshold {bracket.Label} (from {AnimationSeconds.Format(bracket.Anchor)})" + (bracket.Gap is { } gap ? " · later: " + gap : "");
            return text;
        }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var p = e.GetPosition(this);
            if (owner.Wheel(e.Delta, Keyboard.Modifiers, (p.X - ClockLeft) / ClockWidth)) { e.Handled = true; return; }
            if (Keyboard.Modifiers == ModifierKeys.None) { owner.ScrollLanes(-e.Delta / 120.0 * 48); e.Handled = true; return; }
            base.OnMouseWheel(e);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!HandleKey(e.Key, Keyboard.Modifiers)) { base.OnKeyDown(e); return; }
            e.Handled = true;
        }
        /// <summary>↑ ↓ choose a lane; ← → step one tick (Shift 1 s), Ctrl+← → the previous or next occurrence on the lane; Home/End the range; Enter opens a group; F2 the threshold.</summary>
        internal bool HandleKey(Key key, ModifierKeys modifiers)
        {
            var timing = owner.Timing; var state = owner.State;
            var lanes = owner.Rows;
            int index = -1;
            for (int i = 0; i < lanes.Count; i++) if (lanes[i].Id == owner.SelectedLane) { index = i; break; }
            switch (key)
            {
                case System.Windows.Input.Key.Up or System.Windows.Input.Key.Down when modifiers == ModifierKeys.None && lanes.Count > 0:
                    {
                        int next = Math.Clamp(index < 0 ? 1 : index + (key == System.Windows.Input.Key.Down ? 1 : -1), 0, lanes.Count - 1);
                        if (lanes[next].Kind == AnimationTimingLaneKind.Starts) next = Math.Min(lanes.Count - 1, next + (key == System.Windows.Input.Key.Down ? 1 : 1));
                        owner.ChooseLane(lanes[next]); return true;
                    }
                case System.Windows.Input.Key.Left or System.Windows.Input.Key.Right when modifiers == ModifierKeys.Control:
                    {
                        if (index < 0) return true;
                        // Step through the lane's marks in time order from the selected one; a seek lands at the end of a mark's
                        // tick, so the playhead alone would keep finding the same mark behind it.
                        var marks = lanes[index].Marks.OrderBy(m => m.Start).ToList();
                        bool forward = key == System.Windows.Input.Key.Right;
                        int at = owner.SelectedMark is { } selected ? marks.FindIndex(m => m.Id == selected) : -1;
                        double now = state.Time;
                        var mark = at >= 0 ? marks.ElementAtOrDefault(at + (forward ? 1 : -1))
                            : forward ? marks.FirstOrDefault(m => m.Start > now + 1e-6)
                            : marks.LastOrDefault(m => AnimationSeconds.OccurrenceTime(m.Start) < now - 1e-6);
                        if (mark != null) { owner.RevealTime(mark.Start); owner.ChooseMark(lanes[index], mark); }
                        return true;
                    }
                case System.Windows.Input.Key.Left or System.Windows.Input.Key.Right when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                    {
                        int direction = key == System.Windows.Input.Key.Right ? 1 : -1;
                        double target = modifiers == ModifierKeys.Shift ? state.Time + direction : AnimationSeconds.Stepped(state.Time, direction);
                        owner.RequestSeek(Math.Clamp(target, 0, AnimationSeconds.Maximum), true); return true;
                    }
                case System.Windows.Input.Key.Home when modifiers == ModifierKeys.None: owner.RequestSeek(state.RangeStart, true); return true;
                case System.Windows.Input.Key.End when modifiers == ModifierKeys.None: owner.RequestSeek(state.RangeEnd, true); return true;
                case System.Windows.Input.Key.Enter when index >= 0 && lanes[index].Kind == AnimationTimingLaneKind.Group: owner.Toggle(lanes[index]); return true;
                case System.Windows.Input.Key.F2: owner.EditTiming(); return true;
                default: return false;
            }
        }

        // ------------------------------------------------------------ UI Automation: lanes as list items, marks and starts below them

        protected override AutomationPeer OnCreateAutomationPeer() => new SurfacePeer(this);
        internal sealed class SurfacePeer(TimingSurface surface) : FrameworkElementAutomationPeer(surface)
        {
            private readonly Dictionary<string, RowPeer> rows = new(StringComparer.Ordinal);
            internal TimingSurface Surface => surface;
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
            protected override string GetClassNameCore() => "AnimationTimingLanes";
            protected override string GetNameCore() => "Timing lanes";
            protected override string GetHelpTextCore() => "One row per sequence, grouped by instance; ↑ ↓ choose a lane, ← → step one tick, Ctrl+← → the previous or next occurrence, Enter opens a group, F2 focuses the start threshold.";
            protected override List<AutomationPeer>? GetChildrenCore()
            {
                List<AutomationPeer> children = [];
                var shown = surface.owner.Rows;
                // Rows of lanes no longer shown go; a kept row lists its marks again (they change with every frame).
                HashSet<string> ids = new(shown.Select(l => l.Id), StringComparer.Ordinal);
                foreach (string gone in rows.Keys.Where(k => !ids.Contains(k)).ToArray()) rows.Remove(gone);
                foreach (var lane in shown)
                {
                    if (!rows.TryGetValue(lane.Id, out var peer)) rows[lane.Id] = peer = new(this, lane.Id);
                    children.Add(peer);
                }
                return children;
            }
            internal void Refresh()
            {
                try { ResetChildrenCache(); foreach (var row in rows.Values) row.ResetChildrenCache(); }
                catch (InvalidOperationException) { }
            }
            internal Rect RowBounds(string lane)
            {
                if (PresentationSource.FromVisual(surface) == null || surface.owner.Timing is not { } timing) return Rect.Empty;
                double top = -surface.owner.ScrollOffset;
                foreach (var row in surface.owner.Rows)
                {
                    double height = RowHeight(row);
                    if (row.Id == lane)
                    {
                        var a = surface.PointToScreen(new Point(0, Math.Max(0, top))); var b = surface.PointToScreen(new Point(surface.ActualWidth, Math.Min(surface.ActualHeight, top + height)));
                        return top + height < 0 || top > surface.ActualHeight ? Rect.Empty : new Rect(a, b);
                    }
                    top += height;
                }
                return Rect.Empty;
            }
            internal Rect PointBounds(string lane, double seconds)
            {
                var row = RowBounds(lane);
                if (row.IsEmpty) return Rect.Empty;
                double x = surface.X(seconds);
                if (x < surface.ClockLeft || x > surface.ActualWidth) return Rect.Empty;
                var at = surface.PointToScreen(new Point(x, 0));
                return new Rect(at.X - 5, row.Y + row.Height / 2 - 5, 10, 10);
            }
        }

        /// <summary>A lane: its label and state; a group expands; its marks (at most 200) and the Starts lane's starts are its children.</summary>
        internal sealed class RowPeer(SurfacePeer parent, string lane) : TimingPeer(parent), ISelectionItemProvider, IExpandCollapseProvider
        {
            private readonly Dictionary<string, AutomationPeer> marks = new(StringComparer.Ordinal);
            internal AnimationTimingLane? Lane => Owner.Surface.owner.RowLane(lane);
            protected override string GetNameCore() => Lane is not { } row ? lane : row.Kind switch
            {
                AnimationTimingLaneKind.Starts => $"Starts, {row.State}",
                AnimationTimingLaneKind.Group => $"{row.Label}, {(row.Started is double s ? "started " + AnimationSeconds.Format(s) : "not started")}" + (row.State.Length > 0 ? ", " + row.State : "") + ", " + AnimationNotice.Plural(row.LaneCount, "1 lane", "lanes"),
                _ => $"{row.Label}, {row.State}, {AnimationNotice.Plural(row.Marks.Count, "1 mark", "marks")}",
            };
            protected override string GetHelpTextCore() => Lane?.Tooltip ?? "";
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
            protected override Rect GetBoundingRectangleCore() => Owner.RowBounds(lane);
            protected override bool IsOffscreenCore() => Owner.RowBounds(lane).IsEmpty;
            public override object? GetPattern(PatternInterface pattern) => pattern switch
            {
                PatternInterface.SelectionItem => this,
                PatternInterface.ExpandCollapse when Lane?.Kind == AnimationTimingLaneKind.Group => this,
                _ => null,
            };
            protected override List<AutomationPeer>? GetChildrenCore()
            {
                if (Lane is not { } row) return null;
                List<AutomationPeer> children = [];
                if (row.Kind == AnimationTimingLaneKind.Starts)
                    foreach (var start in row.Starts)
                    {
                        string id = "start:" + start.Id.ToString("N");
                        if (!marks.TryGetValue(id, out var peer)) marks[id] = peer = new StartPeer(Owner, lane, start.Id);
                        children.Add(peer);
                    }
                else
                    foreach (var mark in row.Marks.Take(200))
                    {
                        if (!marks.TryGetValue(mark.Id, out var peer)) marks[mark.Id] = peer = new MarkPeer(Owner, lane, mark.Id);
                        children.Add(peer);
                    }
                return children;
            }
            public bool IsSelected => Owner.Surface.owner.SelectedLane == lane;
            public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(Owner);
            public void Select() => Owner.Surface.Dispatcher.Invoke(() => { if (Lane is { } row) Owner.Surface.owner.ChooseLane(row); });
            public void AddToSelection() => Select();
            public void RemoveFromSelection() { }
            public ExpandCollapseState ExpandCollapseState => Lane is { Kind: AnimationTimingLaneKind.Group } row ? row.Expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed : ExpandCollapseState.LeafNode;
            public void Expand() => Owner.Surface.Dispatcher.Invoke(() => { if (Lane is { Kind: AnimationTimingLaneKind.Group, Expanded: false } row) Owner.Surface.owner.Toggle(row); });
            public void Collapse() => Owner.Surface.Dispatcher.Invoke(() => { if (Lane is { Kind: AnimationTimingLaneKind.Group, Expanded: true } row) Owner.Surface.owner.Toggle(row); });
        }

        /// <summary>A mark: "Release mainsplash, 22.217 s, released mainsplash"; Invoke selects it and seeks to the occurrence.</summary>
        internal sealed class MarkPeer(SurfacePeer parent, string lane, string mark) : TimingPeer(parent), IInvokeProvider
        {
            private AnimationTimingMark? Mark => Owner.Surface.owner.Timing?.Lane(lane)?.Marks.FirstOrDefault(m => m.Id == mark);
            protected override string GetNameCore() => Mark is { } m ? $"{m.Label}, {AnimationSeconds.Format(m.Start)}, {m.Result}" : mark;
            protected override string GetHelpTextCore() => Mark is { } m ? MarkTip(m) : "";
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
            protected override Rect GetBoundingRectangleCore() => Mark is { } m ? Owner.PointBounds(lane, m.Start) : Rect.Empty;
            protected override bool IsOffscreenCore() => GetBoundingRectangleCore().IsEmpty;
            public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : null;
            public void Invoke() => Owner.Surface.Dispatcher.Invoke(() =>
            {
                if (Owner.Surface.owner.Timing?.Lane(lane) is { } row && Mark is { } m) Owner.Surface.owner.ChooseMark(row, m);
            });
        }

        /// <summary>A scenario start: its value is its time in seconds; setting it moves the start (as a drag does).</summary>
        internal sealed class StartPeer(SurfacePeer parent, string lane, Guid start) : TimingPeer(parent), IRangeValueProvider
        {
            private AnimationTimingStart? Start => Owner.Surface.owner.Timing?.Lane(lane)?.Starts.FirstOrDefault(s => s.Id == start);
            protected override string GetNameCore() => Start is { } s ? $"{s.Label} starts at {AnimationSeconds.Format(s.Seconds)}" + (s.PrerequisitesIgnored ? ", prerequisites ignored" : "") + (s.Refused is { } r ? ", refused: " + r : "") : "start";
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
            protected override Rect GetBoundingRectangleCore() => Start is { } s ? Owner.PointBounds(lane, s.Seconds) : Rect.Empty;
            protected override bool IsOffscreenCore() => GetBoundingRectangleCore().IsEmpty;
            public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : null;
            public double Value => Start?.Seconds ?? 0;
            public bool IsReadOnly => false;
            public double Maximum => AnimationSeconds.Maximum;
            public double Minimum => 0;
            public double LargeChange => 1;
            public double SmallChange => AnimationSeconds.GridStep;
            public void SetValue(double value)
            {
                if (!double.IsFinite(value) || value is < 0 or > AnimationSeconds.Maximum) throw new ArgumentOutOfRangeException(nameof(value));
                Owner.Surface.Dispatcher.Invoke(() => Owner.Surface.owner.MoveStart(start, value));
            }
        }

        /// <summary>The members every timing peer shares: no keyboard focus of its own, enabled, a control element.</summary>
        internal abstract class TimingPeer(SurfacePeer parent) : AutomationPeer
        {
            protected SurfacePeer Owner => parent;
            protected override string GetAcceleratorKeyCore() => "";
            protected override string GetAccessKeyCore() => "";
            protected override string GetAutomationIdCore() => "";
            protected override string GetClassNameCore() => GetType().Name;
            protected override Point GetClickablePointCore() { var r = GetBoundingRectangleCore(); return r.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }
            protected override string GetHelpTextCore() => "";
            protected override string GetItemStatusCore() => "";
            protected override string GetItemTypeCore() => "";
            protected override AutomationPeer? GetLabeledByCore() => null;
            protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
            protected override bool HasKeyboardFocusCore() => false;
            protected override bool IsContentElementCore() => true;
            protected override bool IsControlElementCore() => true;
            protected override bool IsEnabledCore() => true;
            protected override bool IsKeyboardFocusableCore() => false;
            protected override bool IsPasswordCore() => false;
            protected override bool IsRequiredForFormCore() => false;
            protected override void SetFocusCore() { }
            protected override List<AutomationPeer>? GetChildrenCore() => null;
        }
    }

    /// <summary>The header: "Shared clock · seconds" over the labels, and the ruler (ticks at 1, 0.5 or 0.1 s by zoom, the playback range shaded, the playhead); click or drag seeks.</summary>
    internal sealed class TimingHeader : FrameworkElement
    {
        private readonly AnimationTimingView owner;
        private readonly Stopwatch throttle = Stopwatch.StartNew();
        private double? requested;
        internal TimingHeader(AnimationTimingView owner) { this.owner = owner; Focusable = true; ClipToBounds = true; FocusVisualStyle = null; AutomationProperties.SetName(this, "Shared clock in seconds"); ToolTip = "The shared clock in seconds: click or drag to seek (the preview shows the 1/60 s tick at or before it); Ctrl+wheel zooms, Shift+wheel pans"; }
        private double Left => owner.LabelWidth;
        private double ClockWidth => owner.surface.ClockWidth;
        private double X(double seconds) => Left + (seconds - owner.VisibleStart) / Math.Max(1e-9, owner.VisibleEnd - owner.VisibleStart) * ClockWidth;
        private double At(double x) => owner.VisibleStart + (x - Left) / ClockWidth * (owner.VisibleEnd - owner.VisibleStart);
        protected override void OnRender(DrawingContext dc)
        {
            var ink = owner.Palette; var g = ink.Graph; var state = owner.State;
            dc.DrawRectangle(g.Pane, null, new Rect(0, 0, ActualWidth, ActualHeight));
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText T(string text, double size, Brush brush, Typeface face) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dpi);
            string caption = state.Stale ? state.StaleByEdit ? "Stale · from before your edit" : "Stale · from before Stop" : "Shared clock · seconds";
            var head = T(caption, 11.5, state.Stale ? g.Caution : g.T3, g.Text); head.MaxTextWidth = Math.Max(1, Left - 16); head.MaxLineCount = 1; head.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(head, new Point(10, (ActualHeight - head.Height) / 2));
            dc.DrawLine(new Pen(g.Divider, 1), new Point(Left - .5, 0), new Point(Left - .5, ActualHeight));
            dc.DrawLine(new Pen(g.Divider, 1), new Point(0, ActualHeight - .5), new Point(ActualWidth, ActualHeight - .5));
            dc.PushClip(new RectangleGeometry(new Rect(Left, 0, Math.Max(0, ActualWidth - Left), ActualHeight)));
            double right = Left + ClockWidth;
            double a = X(state.RangeStart), b = X(state.RangeEnd);
            if (a > Left) dc.DrawRectangle(ink.Range, null, new Rect(Left, 0, Math.Min(a, right) - Left, ActualHeight));
            if (b < right) dc.DrawRectangle(ink.Range, null, new Rect(Math.Max(Left, b), 0, right - Math.Max(Left, b), ActualHeight));
            double span = owner.VisibleEnd - owner.VisibleStart, perSecond = ClockWidth / Math.Max(1e-9, span);
            double minor = new[] { .1, .5, 1, 5, 10, 30, 60, 300, 600 }.FirstOrDefault(s => s * perSecond >= 8, 600);
            double major = new[] { .1, .5, 1, 2, 5, 10, 30, 60, 300, 600 }.FirstOrDefault(s => s * perSecond >= 90 && Math.Abs(s / minor - Math.Round(s / minor)) < 1e-6, 600);
            long first = (long)Math.Ceiling(owner.VisibleStart / minor - 1e-9);
            for (long i = first; i * minor <= owner.VisibleEnd + 1e-9 && i - first < 2000; i++)
            {
                double t = i * minor, x = Math.Round(X(t)) + .5;
                bool labelled = Math.Abs(t / major - Math.Round(t / major)) < 1e-6;
                dc.DrawLine(ink.Tick, new Point(x, ActualHeight - (labelled ? 8 : 4)), new Point(x, ActualHeight));
                if (!labelled) continue;
                var label = T(t.ToString(major < 1 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s", 10.5, g.T3, g.Text);
                double at = Math.Max(Left + 2, x - label.Width / 2);
                if (at + label.Width <= right) dc.DrawText(label, new Point(at, 3));
            }
            double time = requested ?? state.Time;
            if (state.Evaluated || state.Time > 0 || requested != null)
            {
                double x = X(time);
                if (x >= Left - 30 && x <= right + 30)
                {
                    var text = T(AnimationSeconds.Value(time), 10.5, g.OnAccent, g.Strong);
                    var pill = new Rect(x - text.Width / 2 - 5, ActualHeight - 15, text.Width + 10, 15);
                    dc.DrawRoundedRectangle(g.Accent, null, pill, 3, 3);
                    dc.DrawRectangle(g.Accent, null, new Rect(pill.X, pill.Bottom - 3, pill.Width, 3));
                    dc.DrawText(text, new Point(pill.X + 5, pill.Y + (15 - text.Height) / 2));
                    var handle = new StreamGeometry();
                    using (var c = handle.Open()) { c.BeginFigure(new Point(x - 4, ActualHeight - 15), true, true); c.LineTo(new Point(x + 4, ActualHeight - 15), true, true); c.LineTo(new Point(x, ActualHeight - 19), true, true); }
                    handle.Freeze(); dc.DrawGeometry(g.Accent, null, handle);
                }
            }
            dc.Pop();
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.GetPosition(this).X < Left || !owner.State.CanSeek) return;
            Focus(); CaptureMouse(); Seek(e, false); e.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (IsMouseCaptured) Seek(e, false); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!IsMouseCaptured) return;
            Seek(e, true); ReleaseMouseCapture(); requested = null; e.Handled = true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); requested = null; InvalidateVisual(); }
        private void Seek(MouseEventArgs e, bool final)
        {
            double seconds = Math.Clamp(At(e.GetPosition(this).X), 0, AnimationSeconds.Maximum);
            requested = seconds; InvalidateVisual();
            // A drag seeks at most every 60 ms; the release always seeks.
            if (final || throttle.ElapsedMilliseconds >= 60) { throttle.Restart(); owner.RequestSeek(seconds, final); }
        }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (owner.Wheel(e.Delta, Keyboard.Modifiers, (e.GetPosition(this).X - Left) / ClockWidth)) e.Handled = true; else base.OnMouseWheel(e);
        }
        protected override void OnKeyDown(KeyEventArgs e) { if (owner.surface.HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true; else base.OnKeyDown(e); }
        protected override AutomationPeer OnCreateAutomationPeer() => new HeaderPeer(this);
        private sealed class HeaderPeer(TimingHeader header) : FrameworkElementAutomationPeer(header), IRangeValueProvider
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
            protected override string GetClassNameCore() => "AnimationTimingRuler";
            protected override string GetNameCore() => "Shared clock in seconds";
            public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPattern(pattern);
            public bool IsReadOnly => !header.owner.State.CanSeek;
            public double Value => header.owner.State.Time;
            public double Minimum => 0;
            public double Maximum => AnimationSeconds.Maximum;
            public double SmallChange => AnimationSeconds.Tick;
            public double LargeChange => 1;
            public void SetValue(double value)
            {
                if (IsReadOnly) throw new ElementNotEnabledException();
                if (!double.IsFinite(value) || value is < 0 or > AnimationSeconds.Maximum) throw new ArgumentOutOfRangeException(nameof(value));
                header.Dispatcher.Invoke(() => header.owner.RequestSeek(value, true));
            }
        }
    }
}
