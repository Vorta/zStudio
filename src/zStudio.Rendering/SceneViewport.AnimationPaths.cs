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
    public void SetAnimationPaths(string definition, IReadOnlyList<AnimationPath> paths, bool selected = true)
    {
        ClearAnimationPaths(definition);
        if (paths.Count > 128 || paths.Sum(p => (long)p.Points.Count) > 216001) throw new ArgumentException("Animation paths exceed the guide allowance.");
        if (paths.Count == 0) return;
        var group = new TopMostGroupNode { IsGuide = true };
        foreach (var path in paths)
        {
            var points = path.Points;
            if (points.Any(p => !Finite(p.Position) || !double.IsFinite(p.Time))) throw new ArgumentException("Animation guide samples must be finite.");
            var indices = Enumerable.Range(1, Math.Max(0, points.Count - 1)).Where(i => !points[i].Break).SelectMany(i => new[] { i - 1, i }).ToArray();
            group.Children.Add(new LineNode { Geometry = new LineData(points.Select(p => p.Position).ToArray(), indices),
                Color = selected ? Color.FromRgb(0x6f, 0xc8, 0xd6) : Color.FromArgb(110, 0x6f, 0xc8, 0xd6), Thickness = selected ? 1.8 : 1.1,
                IsHitTestVisible = false, IsDepthClipEnabled = false });
            if (points.Count == 0) continue;
            Vector3 min = points[0].Position, max = min;
            foreach (var point in points) { min = Vector3.Min(min, point.Position); max = Vector3.Max(max, point.Position); }
            float size = Math.Clamp(Vector3.Distance(min, max) / 250, .5f, 8);
            List<Vector3> marks = []; List<int> edges = [];
            void Edge(Vector3 a, Vector3 b) { int first = marks.Count; marks.Add(a); marks.Add(b); edges.Add(first); edges.Add(first + 1); }
            // Small crosses retain stationary targets; arrows communicate direction without editable curve handles.
            foreach (var point in new[] { points[Math.Min(1, points.Count - 1)], points[^1] })
            { Edge(point.Position - Vector3.UnitX * size, point.Position + Vector3.UnitX * size); Edge(point.Position - Vector3.UnitZ * size, point.Position + Vector3.UnitZ * size); }
            int stride = Math.Max(60, points.Count / 24);
            for (int i = stride; i < points.Count; i += stride)
            {
                if (points[i].Break || Vector3.DistanceSquared(points[i].Position, points[i - 1].Position) < .0001f) continue;
                Vector3 direction = Vector3.Normalize(points[i].Position - points[i - 1].Position);
                Vector3 side = Vector3.Cross(direction, Vector3.UnitY);
                if (side.LengthSquared() < .001f) side = Vector3.UnitX; else side = Vector3.Normalize(side);
                Edge(points[i].Position, points[i].Position - direction * size * 2 + side * size);
                Edge(points[i].Position, points[i].Position - direction * size * 2 - side * size);
            }
            group.Children.Add(new LineNode { Geometry = new LineData(marks.ToArray(), edges.ToArray()), Color = Color.FromRgb(0x6f, 0xc8, 0xd6), Thickness = 2, IsHitTestVisible = false, IsDepthClipEnabled = false });
        }
        animationPathGuides[definition] = group; animationGuidePaths[definition] = paths.ToArray();
        view.Items.Add(group); view.InvalidateRender();
    }
    public void EmphasizeAnimationPaths(string definition)
    {
        foreach (var (key, group) in animationPathGuides)
            foreach (var line in group.Children.OfType<LineNode>()) line.Color = key == definition ? Color.FromRgb(0x6f, 0xc8, 0xd6) : Color.FromArgb(95, 0x6f, 0xc8, 0xd6);
        view.InvalidateRender();
    }
    public void ClearAnimationPaths(string? definition = null)
    {
        foreach (string key in animationPathGuides.Keys.Where(k => definition == null || definition == k).ToArray())
        {
            view.Items.Remove(animationPathGuides[key]); animationPathGuides[key].Dispose(); animationPathGuides.Remove(key); animationGuidePaths.Remove(key);
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
}
