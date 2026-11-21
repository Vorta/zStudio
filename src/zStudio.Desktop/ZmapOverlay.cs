using System.Numerics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using zStudio.Core.Formats;
using zStudio.Rendering;
using Vector = System.Windows.Vector;

namespace zStudio.Desktop;

public sealed class ZmapOverlay : FrameworkElement
{
    internal SceneViewport? Viewport { get; set; }
    internal ZmapEditorSession? Editor { get; set; }
    internal IReadOnlyList<Vector3>? Pending { get; set; }
    internal Vector3? Pointer { get; set; }
    internal (Guid Vertex, Vector3 Position)? Drag { get; set; }
    internal Vector3? SnapTarget { get; set; }
    internal (Vector3? A, Vector3? B, Vector3? Hover, bool ShowChord)? SplitPreview { get; set; }
    private static readonly Pen Light = Frozen(Brushes.WhiteSmoke, 5);
    private static readonly Pen Dark = Frozen(Brushes.Black, 3.5);
    private static readonly Pen Selected = Frozen(Brushes.Gold, 7);
    private static readonly Pen DraftPen = Frozen(Brushes.Gold, 2);
    private static readonly Pen HandleBorder = Frozen(Brushes.Black, 1.5);
    private static readonly Pen PointBorder = Frozen(Brushes.WhiteSmoke, 1);
    private static readonly Pen SnapStroke = Frozen(Brushes.Aqua, 2);
    private static readonly Pen SplitStroke = SplitPen();

    private static Pen SplitPen()
    {
        Pen pen = new(Brushes.Gold, 2) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }

    private static Pen Frozen(Brush brush, double width)
    {
        Pen pen = new(brush, width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    internal static Brush Swatch(ZmapColor color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
        brush.Freeze();
        return brush;
    }

    internal readonly record struct Hit(Guid Outline, Guid? Vertex, Guid? EdgeStart, double Fraction);

    internal Hit? Pick(Point point, bool edge)
    {
        if (Editor?.Map is not { } map || Viewport == null) return null;
        if (!edge && Editor.Outline is Guid active && PickSelectedVertex(point) is Guid target)
            return new(active, target, null, 0);
        double best = 64;
        Hit? hit = null;
        foreach (var outline in map.Outlines)
        {
            if (edge && outline.Id != Editor.Outline) continue;
            var vertices = outline.Vertices;
            int segments = vertices.Length == 2 ? 1 : vertices.Length;
            for (int i = 0; i < segments; i++)
            {
                if (!TryProject(vertices[i].Position, out var a) || !TryProject(vertices[(i + 1) % vertices.Length].Position, out var b)) continue;
                var ab = b - a;
                double t = ab.LengthSquared > 0 ? Math.Clamp(Vector.Multiply(point - a, ab) / ab.LengthSquared, 0, 1) : 0;
                if (edge && !(t > 0 && t < 1)) continue;
                double distance = (point - (a + ab * t)).LengthSquared;
                if (distance < best)
                {
                    best = distance;
                    hit = new(outline.Id, vertices.Length == 1 ? vertices[i].Id : null, vertices[i].Id, t);
                }
            }
        }
        return hit;
    }

    internal Guid? PickSelectedVertex(Point point)
    {
        if (Editor?.Selected is not { } outline) return null;
        double distance = 49;
        Guid? target = null;
        foreach (var vertex in outline.Vertices)
            if (TryProject(vertex.Position, out var projected))
            {
                double candidate = (point - projected).LengthSquared;
                if (candidate < distance || candidate == distance && (target == null || vertex.Id == Editor.Vertex))
                { target = vertex.Id; distance = candidate; }
            }
        return target;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Viewport == null || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        if (Editor?.Map is { } map)
        {
            for (int i = map.Outlines.Length - 1; i >= 0; i--)
                if (map.Outlines[i].Id != Editor.Outline) DrawOutline(map.Outlines[i], false);
            if (Editor.Selected is { } selected) DrawOutline(selected, true);
        }
        if (Pending is { Count: > 0 } pending)
        {
            var points = pending.Select(Project).ToList();
            if (Pointer is { } next) points.Add(Project(next));
            DrawPath(points, false, [Dark, DraftPen]);
            foreach (var p in points)
                if (p is Point value && new Rect(RenderSize).Contains(value))
                    dc.DrawEllipse(Brushes.Gold, HandleBorder, value, 3, 3);
        }
        if (SplitPreview is { } split)
        {
            Point? a = split.A is Vector3 start ? Project(start) : null;
            Point? b = split.B is Vector3 end ? Project(end) : null;
            Point? next = split.Hover is Vector3 hovered ? Project(hovered) : null;
            if (split.ShowChord && (a ?? next) is Point left && (b ?? next) is Point right && ClipSegment(new Rect(RenderSize), ref left, ref right))
            {
                dc.DrawLine(Dark, left, right);
                dc.DrawLine(SplitStroke, left, right);
            }
            if (a is Point first) DrawEndpoint(first, "A");
            if (b is Point second) DrawEndpoint(second, "B");
        }
        if (SnapTarget is Vector3 snap && Project(snap) is Point center && new Rect(RenderSize).Contains(center))
        {
            DrawPath([new Point(center.X, center.Y - 8), new Point(center.X + 8, center.Y),
                new Point(center.X, center.Y + 8), new Point(center.X - 8, center.Y)], true, [Dark, SnapStroke]);
        }
        dc.Pop();

        void DrawEndpoint(Point point, string label)
        {
            if (!new Rect(RenderSize).Contains(point)) return;
            dc.DrawEllipse(Brushes.Gold, HandleBorder, point, 9, 9);
            FormattedText text = new(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new(point.X - text.Width / 2, point.Y - text.Height / 2));
        }

        void DrawOutline(ZmapOutline outline, bool selected)
        {
            Point?[] points = outline.Vertices.Select(v => Project(Drag is { } moving && moving.Vertex == v.Id ? moving.Position : v.Position)).ToArray();
            Brush fill = Swatch(outline.Color);
            if (points.Length == 1)
            {
                if (points[0] is Point point && new Rect(RenderSize).Contains(point))
                {
                    dc.DrawEllipse(Brushes.Black, selected ? DraftPen : HandleBorder, point, 5, 5);
                    dc.DrawEllipse(fill, PointBorder, point, 2.5, 2.5);
                }
            }
            else DrawPath(points, points.Length > 2, [selected ? Selected : Light, Dark, Frozen(fill, 2)]);
            if (!selected) return;
            Point? current = null;
            for (int i = 0; i < points.Length; i++)
                if (points[i] is Point point && new Rect(RenderSize).Contains(point))
                {
                    if (Editor!.Vertex == outline.Vertices[i].Id) current = point;
                    else dc.DrawEllipse(Brushes.WhiteSmoke, HandleBorder, point, 4.5, 4.5);
                }
            if (current is Point marked) dc.DrawEllipse(Brushes.Gold, HandleBorder, marked, 4.5, 4.5);
        }

        void DrawPath(IReadOnlyList<Point?> points, bool closed, Pen[] pens)
        {
            StreamGeometry geometry = new();
            var clip = new Rect(RenderSize);
            clip.Inflate(8, 8);
            using (var path = geometry.Open())
            {
                int segments = closed ? points.Count : points.Count - 1;
                Point? last = null;
                for (int i = 0; i < segments; i++)
                {
                    if (points[i] is not Point a || points[(i + 1) % points.Count] is not Point b || !ClipSegment(clip, ref a, ref b))
                    { last = null; continue; }
                    if (a != last) path.BeginFigure(a, false, false);
                    path.LineTo(b, true, true);
                    last = b;
                }
            }
            geometry.Freeze();
            foreach (var pen in pens) dc.DrawGeometry(null, pen, geometry);
        }
    }

    private Point? Project(Vector3 position) => TryProject(position, out var point) ? point : null;
    private bool TryProject(Vector3 position, out Point point)
    {
        point = default;
        if (Viewport == null || !Viewport.ProjectToView(position, out var screen, out _)) return false;
        point = Viewport.RenderSurface.TranslatePoint(screen, this);
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    private static bool ClipSegment(Rect rect, ref Point a, ref Point b)
    {
        var d = b - a;
        double first = 0, last = 1;
        if (!Bound(-d.X, a.X - rect.Left) || !Bound(d.X, rect.Right - a.X) ||
            !Bound(-d.Y, a.Y - rect.Top) || !Bound(d.Y, rect.Bottom - a.Y)) return false;
        b = a + last * d;
        a += first * d;
        return true;

        bool Bound(double delta, double distance)
        {
            if (delta == 0) return distance >= 0;
            double t = distance / delta;
            if (delta < 0) first = Math.Max(first, t); else last = Math.Min(last, t);
            return first <= last;
        }
    }
}
