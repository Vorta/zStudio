using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using zStudio.Core.Animation;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private readonly Dictionary<string, TopMostGroupNode> animationPathGuides = [];
    private readonly Dictionary<string, AnimationPath[]> animationGuidePaths = [];
    /// <summary>Each guide's lines (the path, its end crosses and arrows, its second ticks) and the node it follows.</summary>
    private readonly Dictionary<string, List<(int Node, LineNode Line, LineNode Marks, LineNode? Ticks, Color Base)>> animationGuideLines = [];
    private const byte GuideFull = 255, GuideDimmed = 95, TicksFull = 120, TicksDimmed = 45;
    public void SetAnimationPaths(string definition, IReadOnlyList<AnimationPath> paths, bool selected = true)
    {
        ClearAnimationPaths(definition);
        if (paths.Count > 128 || paths.Sum(p => (long)p.Points.Count) > 216001) throw new ArgumentException("Animation paths exceed the guide allowance.");
        if (paths.Count == 0) return;
        var group = new TopMostGroupNode { IsGuide = true };
        List<(int, LineNode, LineNode, LineNode?, Color)> lines = [];
        foreach (var path in paths)
        {
            var points = path.Points;
            Color color = path.Kind == "camera" ? Color.FromRgb(0xf5, 0xa7, 0x42) : Color.FromRgb(0x6f, 0xc8, 0xd6);
            if (points.Any(p => !Finite(p.Position) || !double.IsFinite(p.Time))) throw new ArgumentException("Animation guide samples must be finite.");
            var indices = Enumerable.Range(1, Math.Max(0, points.Count - 1)).Where(i => !points[i].Break).SelectMany(i => new[] { i - 1, i }).ToArray();
            var line = new LineNode { Geometry = new LineData(points.Select(p => p.Position).ToArray(), indices),
                Color = WithAlpha(color, selected ? GuideFull : GuideDimmed), Thickness = selected ? 1.8 : 1.1,
                IsHitTestVisible = false, IsDepthClipEnabled = false };
            group.Children.Add(line);
            if (points.Count == 0) continue;
            Vector3 min = points[0].Position, max = min;
            foreach (var point in points) { min = Vector3.Min(min, point.Position); max = Vector3.Max(max, point.Position); }
            float size = Math.Clamp(Vector3.Distance(min, max) / 250, .5f, 8);
            List<Vector3> marks = []; List<int> edges = [];
            void Edge(List<Vector3> into, List<int> pairs, Vector3 a, Vector3 b) { int first = into.Count; into.Add(a); into.Add(b); pairs.Add(first); pairs.Add(first + 1); }
            // Small crosses retain stationary targets; arrows communicate direction without editable curve handles.
            foreach (var point in new[] { points[Math.Min(1, points.Count - 1)], points[^1] })
            { Edge(marks, edges, point.Position - Vector3.UnitX * size, point.Position + Vector3.UnitX * size); Edge(marks, edges, point.Position - Vector3.UnitZ * size, point.Position + Vector3.UnitZ * size); }
            int stride = Math.Max(60, points.Count / 24);
            for (int i = stride; i < points.Count; i += stride)
            {
                if (points[i].Break || Vector3.DistanceSquared(points[i].Position, points[i - 1].Position) < .0001f) continue;
                Vector3 direction = Vector3.Normalize(points[i].Position - points[i - 1].Position);
                Vector3 side = Vector3.Cross(direction, Vector3.UnitY);
                if (side.LengthSquared() < .001f) side = Vector3.UnitX; else side = Vector3.Normalize(side);
                Edge(marks, edges, points[i].Position, points[i].Position - direction * size * 2 + side * size);
                Edge(marks, edges, points[i].Position, points[i].Position - direction * size * 2 - side * size);
            }
            var markNode = new LineNode { Geometry = new LineData(marks.ToArray(), edges.ToArray()), Color = WithAlpha(color, selected ? GuideFull : GuideDimmed), Thickness = 2, IsHitTestVisible = false, IsDepthClipEnabled = false };
            group.Children.Add(markNode);
            // Faint ticks across the guide at every whole second of the shared clock while the target moves (design §14).
            List<Vector3> ticks = []; List<int> tickEdges = [];
            for (int i = 1; i < points.Count && ticks.Count < 4096; i++)
            {
                var a = points[i - 1]; var b = points[i];
                if (b.Break || b.Time <= a.Time) continue;
                for (double second = Math.Floor(a.Time) + 1; second <= b.Time + 1e-9; second++)
                {
                    if (second <= a.Time + 1e-9) continue;
                    Vector3 delta = b.Position - a.Position;
                    if (delta.LengthSquared() < 1e-8f) continue;
                    Vector3 at = Vector3.Lerp(a.Position, b.Position, (float)((second - a.Time) / (b.Time - a.Time)));
                    Vector3 direction = Vector3.Normalize(delta);
                    Vector3 side = Vector3.Cross(direction, Vector3.UnitY);
                    if (side.LengthSquared() < .001f) side = Vector3.UnitX; else side = Vector3.Normalize(side);
                    Edge(ticks, tickEdges, at - side * size * .9f, at + side * size * .9f);
                }
            }
            LineNode? tickNode = null;
            if (ticks.Count > 0)
            {
                tickNode = new LineNode { Geometry = new LineData(ticks.ToArray(), tickEdges.ToArray()), Color = WithAlpha(color, selected ? TicksFull : TicksDimmed), Thickness = 1.2, IsHitTestVisible = false, IsDepthClipEnabled = false };
                group.Children.Add(tickNode);
            }
            lines.Add((path.Node, line, markNode, tickNode, color));
        }
        animationPathGuides[definition] = group; animationGuidePaths[definition] = paths.ToArray(); animationGuideLines[definition] = lines;
        view.Items.Add(group); view.InvalidateRender();
    }
    private static Color WithAlpha(Color color, byte alpha) { color.A = alpha; return color; }
    /// <summary>
    /// Emphasis is alpha only: the guides of <paramref name="definition"/> are drawn in full and every other definition's dimmed;
    /// with <paramref name="nodes"/> (the nodes a selected motion event names) only the guides of those nodes stay full.
    /// </summary>
    public void EmphasizeAnimationPaths(string definition, IReadOnlySet<int>? nodes = null)
    {
        foreach (var (key, lines) in animationGuideLines)
            foreach (var (node, line, marks, ticks, color) in lines)
            {
                bool full = key == definition && (nodes == null || nodes.Count == 0 || !lines.Any(l => nodes.Contains(l.Node)) || nodes.Contains(node));
                line.Color = WithAlpha(color, full ? GuideFull : GuideDimmed);
                marks.Color = WithAlpha(color, full ? GuideFull : GuideDimmed);
                if (ticks != null) ticks.Color = WithAlpha(color, full ? TicksFull : TicksDimmed);
            }
        view.InvalidateRender();
    }
    public void ClearAnimationPaths(string? definition = null)
    {
        foreach (string key in animationPathGuides.Keys.Where(k => definition == null || definition == k).ToArray())
        {
            view.Items.Remove(animationPathGuides[key]); animationPathGuides[key].Dispose(); animationPathGuides.Remove(key); animationGuidePaths.Remove(key); animationGuideLines.Remove(key);
        }
        view.InvalidateRender();
    }
    public bool FrameAnimationPaths(string definition)
    {
        if (IsFlyActive || IsPickupDragging || IsParachutePointerMoving || !animationGuidePaths.TryGetValue(definition, out var paths)) return false;
        Rect3D bounds = Rect3D.Empty;
        foreach (var path in paths) foreach (var p in path.Points) bounds.Union(new Point3D(p.Position.X, p.Position.Y, p.Position.Z));
        if (bounds.IsEmpty) return false;
        bounds.Union(new Rect3D(bounds.X - 1, bounds.Y - 1, bounds.Z - 1, 2, 2, 2));
        return FrameBounds(bounds, true);
    }
    /// <summary>Frames the VTOL flight path the drop-window analysis drew.</summary>
    public bool FrameParachutePath()
    {
        if (IsFlyActive || IsPickupDragging || IsParachutePointerMoving || parachutePath.Length < 2) return false;
        Rect3D bounds = Rect3D.Empty;
        foreach (var p in parachutePath.Skip(1)) bounds.Union(new Point3D(p.Position.X, p.Position.Y, p.Position.Z));
        if (bounds.IsEmpty) return false;
        bounds.Union(new Rect3D(bounds.X - 1, bounds.Y - 1, bounds.Z - 1, 2, 2, 2));
        return FrameBounds(bounds, true);
    }
}
