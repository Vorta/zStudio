using System.Numerics;
using zStudio.Core.Worlds;

namespace zStudio.Core.Animation;

/// <summary>Immutable preview-start standable geometry. Independent of viewport visibility and camera LOD.</summary>
public sealed class AnimationTerrain
{
    private readonly record struct Triangle(int Source, Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, Vector3 Min, Vector3 Max);
    private readonly record struct Branch(Vector3 Min, Vector3 Max, int Start, int Count, int Left, int Right);
    private readonly Triangle[] triangles;
    private readonly List<Branch> branches = [];
    private const int MaximumTriangles = 1_000_000;

    public AnimationTerrain(GameScene scene, CancellationToken token = default)
    {
        var work = new LookupWorkBudget(64 * 1024 * 1024, token);
        List<Triangle> collected = [];
        // Fixed nearest LOD and authored active flags; no renderer visibility overrides.
        var view = SceneBuilder.Assemble(scene, token: token);
        foreach (var placement in view.Placements)
        {
            work.Reserve(1);
            var node = scene.Nodes[placement.NodeIndex];
            if ((node.Metadata.UInt("flags") & 12) != 12) continue;
            var model = scene.Models[placement.ModelIndex];
            float morph = model.Metadata.Float("morph_factor");
            foreach (var polygon in model.Polygons)
            {
                work.Reserve(1L + polygon.Vertices.Length);
                if (polygon.Vertices.Length < 3) continue;
                if (polygon.Vertices.Length > 8192 || collected.Count + (long)polygon.Vertices.Length - 2 > MaximumTriangles)
                    throw new InvalidDataException("Standable preview geometry exceeds its triangle allowance.");
                if (polygon.Vertices.Any(v => v < 0 || v >= model.Vertices.Length))
                    throw new InvalidDataException($"Standable node #{node.Index} has invalid polygon indices.");
                var vertices = new Vector3[polygon.Vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    int v = polygon.Vertices[i];
                    vertices[i] = Vector3.Transform(model.Vertices[v] + (model.Morphs.Length == model.Vertices.Length ? model.Morphs[v] * morph : Vector3.Zero), placement.Transform);
                    if (!Finite(vertices[i])) throw new InvalidDataException($"Standable node #{node.Index} has nonfinite geometry.");
                }
                // Triangulation is quadratic for nonconvex polygons: admit its work before allocating.
                work.Reserve((long)vertices.Length * vertices.Length);
                var indices = (polygon.Flags & 1024) != 0 ? GeometryBuilder.TriangleStrip(vertices.Length) : GeometryBuilder.Triangulate(vertices);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    var a = vertices[indices[i]]; var b = vertices[indices[i + 1]]; var c = vertices[indices[i + 2]];
                    var normal = Vector3.Cross(b - a, c - a);
                    if (!Finite(normal)) throw new InvalidDataException("Standable preview geometry has an invalid normal.");
                    if (normal.Y <= 1e-8f) continue;
                    collected.Add(new(node.Index, a, b, c, normal, Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c))));
                }
            }
        }
        triangles = collected.ToArray();
        if (triangles.Length > 0) Build(0, triangles.Length);

        int Build(int start, int count)
        {
            work.Reserve(count);
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            for (int i = start; i < start + count; i++) { min = Vector3.Min(min, triangles[i].Min); max = Vector3.Max(max, triangles[i].Max); }
            int index = branches.Count; branches.Add(default);
            if (count <= 8) branches[index] = new(min, max, start, count, -1, -1);
            else
            {
                var extent = max - min; int axis = extent.X >= extent.Z ? 0 : 2;
                Array.Sort(triangles, start, count, Comparer<Triangle>.Create((a, b) =>
                    ((double)a.Min[axis] + a.Max[axis]).CompareTo((double)b.Min[axis] + b.Max[axis])));
                int left = Build(start, count / 2), right = Build(start + count / 2, count - count / 2);
                branches[index] = new(min, max, 0, 0, left, right);
            }
            return index;
        }
    }

    // A one-sided swept support point. A roof above the previous point cannot catch a fall below it.
    internal float? Contact(Vector3 from, Vector3 to, IReadOnlySet<int> excluded, Action<long> reserve)
    {
        if (branches.Count == 0) return null;
        var min = Vector3.Min(from, to) - new Vector3(.0001f);
        var max = Vector3.Max(from, to) + new Vector3(.0001f);
        float? first = null;
        Visit(0);
        return first;
        void Visit(int index)
        {
            reserve(1);
            var branch = branches[index];
            if (min.X > branch.Max.X || max.X < branch.Min.X || min.Y > branch.Max.Y + .0001f || max.Y < branch.Min.Y - .0001f || min.Z > branch.Max.Z || max.Z < branch.Min.Z) return;
            if (branch.Count == 0) { Visit(branch.Left); Visit(branch.Right); return; }
            for (int i = branch.Start; i < branch.Start + branch.Count; i++)
            {
                reserve(1); var t = triangles[i];
                if (excluded.Contains(t.Source)) continue;
                float a = Vector3.Dot(from - t.A, t.Normal), b = Vector3.Dot(to - t.A, t.Normal);
                float tolerance = .0001f * t.Normal.Y;
                if (a < -tolerance || b > tolerance || b >= a) continue;
                float fraction = Math.Clamp(a / (a - b), 0, 1);
                if (first is float old && fraction >= old) continue;
                Vector3 hit = Vector3.Lerp(from, to, fraction);
                if (Inside(t.A, t.B) && Inside(t.B, t.C) && Inside(t.C, t.A)) first = fraction;
                bool Inside(Vector3 x, Vector3 y) => Vector3.Dot(Vector3.Cross(y - x, hit - x), t.Normal) >= -1e-5f * t.Normal.LengthSquared();
            }
        }
    }

    internal float? Height(Vector3 position, float bottom, IReadOnlySet<int> excluded, Action<long> reserve)
    {
        var end = new Vector3(position.X, bottom - .001f, position.Z);
        return Contact(position, end, excluded, reserve) is float t ? Vector3.Lerp(position, end, t).Y : null;
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
