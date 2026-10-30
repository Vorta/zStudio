using System.Globalization;
using System.Numerics;
using Vector = System.Windows.Vector;
using System.Windows;
using System.Windows.Media;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>A range test the 3D view draws (design §10, "Drawing range tests"): a ring of its radius around its attach node.</summary>
internal sealed record AnimationRangeTest(string Id, Vector3 Center, double Radius, string Attach, bool Gate)
{
    /// <summary>Selected or hovered: its dashed line to the player and its label are drawn.</summary>
    public bool Emphasized { get; init; }
    public string Label { get; init; } = "";
}

/// <summary>The player reference the range tests measure from, at the playhead.</summary>
internal sealed record AnimationPlayerMark(Vector3 Position, string Source, bool Frozen);

/// <summary>A guide the view shows, with whether a selected motion event emphasises it (alpha only).</summary>
internal sealed record AnimationGuideTrack(AnimationPath Path, bool Emphasized);

/// <summary>
/// Animation mode's screen-space layer over the 3D view: a dot on every shown guide at the playhead time, the contacts (⊥) and
/// impact sounds (◎) of the evaluated paths, the range rings around attach nodes with the player pin, the dashed line and its
/// label ("2,010 · outside 64 → false"), and an edge arrow with the distance when the player is off screen. It draws what the
/// window gives it and projects world points through <see cref="Project"/> each time the camera moves; it never takes input.
/// </summary>
public sealed class AnimationViewOverlay : FrameworkElement
{
    /// <summary>A world point in this element's coordinates, and whether it is in front of the eye (null without a camera).</summary>
    internal Func<Vector3, (Point Screen, bool InFront)?>? Project { get; set; }
    private IReadOnlyList<AnimationGuideTrack> guides = [];
    private IReadOnlyList<AnimationPathMarker> markers = [];
    private IReadOnlyList<AnimationRangeTest> ranges = [];
    private AnimationPlayerMark? player;
    private double time;

    public AnimationViewOverlay() { IsHitTestVisible = false; Focusable = false; SnapsToDevicePixels = false; }

    internal int DrawnDots { get; private set; }
    internal int DrawnRings { get; private set; }
    internal bool PlayerOffScreen { get; private set; }
    internal IReadOnlyList<AnimationRangeTest> Ranges => ranges;

    internal void SetGuides(IReadOnlyList<AnimationGuideTrack> shown, double seconds)
    {
        if (ReferenceEquals(shown, guides) && seconds == time) return;
        guides = shown; time = seconds; InvalidateVisual();
    }
    internal void SetTime(double seconds) { if (seconds == time) return; time = seconds; if (guides.Count > 0) InvalidateVisual(); }
    internal void SetMarkers(IReadOnlyList<AnimationPathMarker> shown) { markers = shown; InvalidateVisual(); }
    internal void SetRanges(IReadOnlyList<AnimationRangeTest> shown, AnimationPlayerMark? reference)
    {
        ranges = shown; player = reference; InvalidateVisual();
    }
    internal bool IsEmpty => guides.Count == 0 && markers.Count == 0 && ranges.Count == 0;

    protected override void OnRender(DrawingContext dc)
    {
        DrawnDots = DrawnRings = 0; PlayerOffScreen = false;
        if (Project == null || ActualWidth <= 0 || ActualHeight <= 0 || IsEmpty) return;
        var g = AnimationGraphPalette.For(AnimationGraphPalette.IsDark(this));
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        DrawMarkers(dc, g);
        DrawGuides(dc, g);
        DrawRanges(dc, g);
        dc.Pop();
    }

    // ------------------------------------------------------------ guides with time

    private void DrawGuides(DrawingContext dc, AnimationGraphPalette g)
    {
        var outline = new Pen(g.Pane, 1.5); outline.Freeze();
        foreach (var track in guides)
        {
            if (PositionAt(track.Path.Points, time) is not Vector3 at || Project!(at) is not { InFront: true } p || !Inside(p.Screen, 6)) continue;
            Color color = track.Path.Kind == "camera" ? Color.FromRgb(0xF5, 0xA7, 0x42) : Color.FromRgb(0x6F, 0xC8, 0xD6);
            if (!track.Emphasized) color.A = 120;
            var fill = new SolidColorBrush(color); fill.Freeze();
            dc.DrawEllipse(fill, outline, p.Screen, track.Emphasized ? 4.5 : 3.5, track.Emphasized ? 4.5 : 3.5);
            DrawnDots++;
        }
    }
    /// <summary>The guide's position at <paramref name="seconds"/>: before its first sample none; after its last, where it rests.</summary>
    internal static Vector3? PositionAt(IReadOnlyList<AnimationPathPoint> points, double seconds)
    {
        if (points.Count == 0 || seconds < points[0].Time - 1e-9) return null;
        if (seconds >= points[^1].Time) return points[^1].Position;
        int lower = 0, upper = points.Count - 1;
        while (lower + 1 < upper) { int middle = (lower + upper) / 2; if (points[middle].Time <= seconds) lower = middle; else upper = middle; }
        var a = points[lower]; var b = points[upper];
        // A discontinuity jumps at its own sample: until then the target is where the previous sample left it.
        if (b.Break || b.Time <= a.Time) return a.Position;
        return Vector3.Lerp(a.Position, b.Position, (float)Math.Clamp((seconds - a.Time) / (b.Time - a.Time), 0, 1));
    }

    // ------------------------------------------------------------ contacts and impacts

    private void DrawMarkers(DrawingContext dc, AnimationGraphPalette g)
    {
        if (markers.Count == 0) return;
        var contact = new Pen(new SolidColorBrush(Color.FromRgb(0x6F, 0xC8, 0xD6)), 1.6); contact.Freeze();
        var impact = new Pen(g.Sound, 1.4); impact.Freeze();
        var halo = new Pen(g.Pane, 3.5); halo.Freeze();
        int drawn = 0;
        foreach (var marker in markers)
        {
            if (drawn >= 2048) break;
            if (Project!(marker.Position) is not { InFront: true } p || !Inside(p.Screen, 8)) continue;
            double x = p.Screen.X, y = p.Screen.Y;
            if (marker.Kind == "impact")
            {
                dc.DrawEllipse(null, halo, p.Screen, 6, 6);
                dc.DrawEllipse(null, impact, p.Screen, 6, 6); dc.DrawEllipse(g.Sound, null, p.Screen, 2, 2);
            }
            else
            {
                // ⊥: where the motion touched the ground.
                foreach (var pen in new[] { halo, contact })
                {
                    dc.DrawLine(pen, new Point(x, y - 8), new Point(x, y));
                    dc.DrawLine(pen, new Point(x - 5.5, y), new Point(x + 5.5, y));
                }
            }
            drawn++;
        }
    }

    // ------------------------------------------------------------ range tests and the player

    private void DrawRanges(DrawingContext dc, AnimationGraphPalette g)
    {
        if (ranges.Count == 0) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Color release = ((SolidColorBrush)g.Release).Color;
        var ring = new Pen(new SolidColorBrush(Color.FromArgb(179, release.R, release.G, release.B)), 1.5); ring.Freeze();
        var ringStrong = new Pen(new SolidColorBrush(Color.FromArgb(230, release.R, release.G, release.B)), 2); ringStrong.Freeze();
        var disc = new SolidColorBrush(Color.FromArgb(20, release.R, release.G, release.B)); disc.Freeze();
        var dashed = new Pen(g.T1, 1.5) { DashStyle = new DashStyle([3.3, 2.7], 0) }; dashed.Freeze();
        (Point Screen, bool InFront)? playerAt = player is { } reference ? Project!(reference.Position) : null;
        bool playerShown = playerAt is { InFront: true } shown && Inside(shown.Screen, 2);
        foreach (var test in ranges.OrderBy(r => r.Emphasized))
        {
            // The ring lies in the horizontal plane through the attach node (the game measures the 3D distance).
            List<(Point Point, bool Front)> points = [];
            for (int i = 0; i <= 72; i++)
            {
                double angle = i * Math.PI * 2 / 72;
                var at = test.Center + new Vector3((float)(Math.Cos(angle) * test.Radius), 0, (float)(Math.Sin(angle) * test.Radius));
                points.Add(Project!(at) is { } p ? (p.Screen, p.InFront) : (default, false));
            }
            var outline = new StreamGeometry();
            bool closed = points.All(p => p.Front);
            using (var c = outline.Open())
            {
                bool open = false;
                foreach (var (point, front) in points)
                {
                    if (!front || !double.IsFinite(point.X) || Math.Abs(point.X) > 1e6 || Math.Abs(point.Y) > 1e6) { open = false; continue; }
                    if (!open) { c.BeginFigure(point, closed, false); open = true; } else c.LineTo(point, true, true);
                }
            }
            outline.Freeze();
            if (closed) dc.DrawGeometry(disc, null, outline);
            dc.DrawGeometry(null, test.Emphasized ? ringStrong : ring, outline);
            DrawnRings++;
            // The radius at the ring's right.
            if (points[0].Front && Inside(points[0].Point, 0))
            {
                var radius = Text(AnimationPhrases.Number(test.Radius), 12, g.Release, dpi);
                dc.DrawText(radius, new Point(points[0].Point.X + 5, points[0].Point.Y - radius.Height / 2));
            }
            if (!test.Emphasized || player == null || Project!(test.Center) is not { InFront: true } center) continue;
            // The dashed line to the player (or to the edge arrow), labelled with the distance and the outcome.
            Point end;
            if (playerShown) end = playerAt!.Value.Screen;
            else
            {
                end = EdgePoint(center.Screen, playerAt?.Screen ?? new Point(center.Screen.X, ActualHeight * 2), 26);
                PlayerOffScreen = true;
            }
            dc.DrawLine(dashed, center.Screen, end);
            if (test.Label.Length > 0)
            {
                var label = Text(test.Label, 13, g.T1, dpi, FontWeights.SemiBold);
                var mid = new Point((center.Screen.X + end.X) / 2, (center.Screen.Y + end.Y) / 2);
                var box = new Rect(mid.X - label.Width / 2 - 6, mid.Y - label.Height - 6, label.Width + 12, label.Height + 4);
                box.X = Math.Clamp(box.X, 4, Math.Max(4, ActualWidth - box.Width - 4)); box.Y = Math.Clamp(box.Y, 4, Math.Max(4, ActualHeight - box.Height - 4));
                dc.DrawRoundedRectangle(g.Pane, new Pen(g.Divider, 1), box, 4, 4);
                dc.DrawText(label, new Point(box.X + 6, box.Y + 2));
            }
            if (!playerShown) DrawEdgeArrow(dc, g, center.Screen, end, Vector3.Distance(test.Center, player.Position), dpi);
        }
        if (player != null && playerShown) DrawPin(dc, g, playerAt!.Value.Screen, player, dpi);
        else if (player != null && !ranges.Any(r => r.Emphasized) && Project!(ranges[0].Center) is { InFront: true } first)
        {
            // Show range tests without a selection: the player's direction from the first ring, with its distance.
            var end = EdgePoint(first.Screen, playerAt?.Screen ?? new Point(first.Screen.X, ActualHeight * 2), 26);
            PlayerOffScreen = true;
            DrawEdgeArrow(dc, g, first.Screen, end, Vector3.Distance(ranges[0].Center, player.Position), dpi);
        }
    }
    private void DrawPin(DrawingContext dc, AnimationGraphPalette g, Point at, AnimationPlayerMark mark, double dpi)
    {
        var stroke = new Pen(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)), 1.5); stroke.Freeze();
        var pin = new StreamGeometry();
        using (var c = pin.Open())
        {
            c.BeginFigure(at, true, true);
            c.LineTo(new Point(at.X - 5.2, at.Y - 11), true, true);
            c.ArcTo(new Point(at.X + 5.2, at.Y - 11), new Size(6.5, 6.5), 0, true, SweepDirection.Clockwise, true, true);
        }
        pin.Freeze();
        dc.DrawGeometry(g.Accent, stroke, pin);
        dc.DrawEllipse(g.Pane, null, new Point(at.X, at.Y - 13.5), 2.2, 2.2);
        var label = Text("Player" + (mark.Frozen ? " · frozen" : ""), 11.5, g.T1, dpi);
        dc.DrawRoundedRectangle(g.Pane, null, new Rect(at.X + 9, at.Y - 22, label.Width + 8, label.Height + 2), 3, 3);
        dc.DrawText(label, new Point(at.X + 13, at.Y - 21));
    }
    private void DrawEdgeArrow(DrawingContext dc, AnimationGraphPalette g, Point from, Point at, double distance, double dpi)
    {
        Vector direction = at - from;
        if (direction.LengthSquared < 1e-6) direction = new Vector(0, 1);
        direction.Normalize();
        Vector side = new(-direction.Y, direction.X);
        var arrow = new StreamGeometry();
        using (var c = arrow.Open())
        {
            c.BeginFigure(at + direction * 9, true, true);
            c.LineTo(at - direction * 5 + side * 7, true, true);
            c.LineTo(at - direction * 5 - side * 7, true, true);
        }
        arrow.Freeze();
        dc.DrawGeometry(g.Accent, new Pen(g.Pane, 1.5), arrow);
        var label = Text("Player · " + distance.ToString("N0", CultureInfo.InvariantCulture), 12, g.T1, dpi);
        Point text = at - direction * 16 - new Vector(label.Width / 2, label.Height / 2);
        text.X = Math.Clamp(text.X, 4, Math.Max(4, ActualWidth - label.Width - 4)); text.Y = Math.Clamp(text.Y, 4, Math.Max(4, ActualHeight - label.Height - 4));
        dc.DrawRoundedRectangle(g.Pane, null, new Rect(text.X - 4, text.Y - 1, label.Width + 8, label.Height + 2), 3, 3);
        dc.DrawText(label, text);
    }
    /// <summary>Where the line from <paramref name="from"/> towards <paramref name="to"/> leaves the view, <paramref name="inset"/> inside its edge.</summary>
    private Point EdgePoint(Point from, Point to, double inset)
    {
        double left = inset, top = inset, right = ActualWidth - inset, bottom = ActualHeight - inset;
        from = new Point(Math.Clamp(from.X, left, right), Math.Clamp(from.Y, top, bottom));
        Vector d = to - from;
        if (d.LengthSquared < 1e-9) return from;
        double t = double.MaxValue;
        if (d.X > 0) t = Math.Min(t, (right - from.X) / d.X); else if (d.X < 0) t = Math.Min(t, (left - from.X) / d.X);
        if (d.Y > 0) t = Math.Min(t, (bottom - from.Y) / d.Y); else if (d.Y < 0) t = Math.Min(t, (top - from.Y) / d.Y);
        if (!double.IsFinite(t) || t < 0) return from;
        return from + d * t;
    }
    private bool Inside(Point p, double margin) => p.X >= -margin && p.Y >= -margin && p.X <= ActualWidth + margin && p.Y <= ActualHeight + margin;
    private static FormattedText Text(string text, double size, Brush brush, double dpi, FontWeight? weight = null) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush, dpi);
}
