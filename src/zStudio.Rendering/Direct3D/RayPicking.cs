using System.Numerics;
using System.Windows;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// A bounding volume hierarchy over one triangle list in model space (leaves of up to four triangles, split at the median
/// centroid of the longest axis). Triangles with an index outside the positions are left out. Immutable once built.
/// </summary>
internal sealed class MeshBvh
{
    private const int LeafSize = 4;
    private readonly Vector3[] positions;
    private readonly int[] indices;
    // Triangle ordinals, grouped by leaf.
    private readonly int[] triangles;
    // Per node: bounds; Count > 0 is a leaf over triangles[Start .. Start + Count), otherwise children Start and Start + 1.
    private readonly Node[] nodes;
    private struct Node { public Vector3 Min, Max; public int Start, Count; }

    public MeshBvh(Vector3[] positions, int[] indices)
    {
        this.positions = positions; this.indices = indices;
        List<int> valid = [];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int a = indices[t], b = indices[t + 1], c = indices[t + 2];
            if ((uint)a < (uint)positions.Length && (uint)b < (uint)positions.Length && (uint)c < (uint)positions.Length) valid.Add(t / 3);
        }
        triangles = [.. valid];
        if (triangles.Length == 0) { nodes = []; return; }
        var centroids = new Vector3[indices.Length / 3];
        var boxMin = new Vector3[centroids.Length];
        var boxMax = new Vector3[centroids.Length];
        foreach (int t in triangles)
        {
            var (p0, p1, p2) = (positions[indices[3 * t]], positions[indices[3 * t + 1]], positions[indices[3 * t + 2]]);
            boxMin[t] = Vector3.Min(p0, Vector3.Min(p1, p2)); boxMax[t] = Vector3.Max(p0, Vector3.Max(p1, p2));
            centroids[t] = (p0 + p1 + p2) / 3;
        }
        var keys = new float[triangles.Length];
        List<Node> built = [new()];
        Stack<(int Node, int Start, int Count)> pending = new();
        pending.Push((0, 0, triangles.Length));
        while (pending.TryPop(out var item))
        {
            Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity), cmin = min, cmax = max;
            for (int i = item.Start; i < item.Start + item.Count; i++)
            {
                int t = triangles[i];
                min = Vector3.Min(min, boxMin[t]); max = Vector3.Max(max, boxMax[t]);
                cmin = Vector3.Min(cmin, centroids[t]); cmax = Vector3.Max(cmax, centroids[t]);
            }
            var extent = cmax - cmin;
            if (item.Count <= LeafSize || !(extent.X > 0 || extent.Y > 0 || extent.Z > 0))
            {
                built[item.Node] = new Node { Min = min, Max = max, Start = item.Start, Count = item.Count };
                continue;
            }
            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            int half = item.Count / 2;
            var span = triangles.AsSpan(item.Start, item.Count);
            var keySpan = keys.AsSpan(item.Start, item.Count);
            for (int i = 0; i < span.Length; i++) keySpan[i] = Axis(centroids[span[i]], axis);
            // Median split: the triangles ordered by centroid along the axis.
            keySpan.Sort(span);
            int left = built.Count;
            built.Add(new()); built.Add(new());
            built[item.Node] = new Node { Min = min, Max = max, Start = left, Count = 0 };
            pending.Push((left + 1, item.Start + half, item.Count - half));
            pending.Push((left, item.Start, half));
        }
        nodes = [.. built];
    }
    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>
    /// Managed size of a hierarchy over <paramref name="triangles"/> triangles: the triangle order (4 bytes each) and about 0.8
    /// nodes per triangle (32 bytes each: median splits leave leaves of 2–4 triangles).
    /// </summary>
    public static long RetainedBytes(long triangles) => checked(64 + triangles * 4 + (triangles * 4 / 5 + 2) * 32);
    /// <summary>The transient memory of building one (centroids and bounds per triangle, sort keys, the growing node list).</summary>
    public static long BuildBytes(long triangles) => checked(triangles * (12 + 24 + 4 + 8) + (triangles * 4 / 5 + 2) * 32 * 2);

    internal static (Vector3 Min, Vector3 Max) Measure(Vector3[] positions)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return (min, max);
    }

    /// <summary>
    /// The nearest triangle the ray origin + t·direction (model space, any direction length) crosses at t ≥ 0, both windings;
    /// equal distances keep the lowest triangle ordinal.
    /// </summary>
    public bool Nearest(Vector3 origin, Vector3 direction, out float distance, out int triangle)
    {
        distance = float.PositiveInfinity; triangle = -1;
        if (nodes.Length == 0) return false;
        var inverse = new Vector3(1 / direction.X, 1 / direction.Y, 1 / direction.Z);
        Span<int> stack = stackalloc int[64];
        int depth = 0;
        stack[depth++] = 0;
        while (depth > 0)
        {
            ref readonly var node = ref nodes[stack[--depth]];
            if (!Slab(origin, inverse, node.Min, node.Max, distance, out _)) continue;
            if (node.Count > 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    int t = triangles[i];
                    if (Intersect(origin, direction, positions[indices[3 * t]], positions[indices[3 * t + 1]], positions[indices[3 * t + 2]], out float hit)
                        && (hit < distance || hit == distance && t < triangle))
                    { distance = hit; triangle = t; }
                }
                continue;
            }
            bool nearA = Slab(origin, inverse, nodes[node.Start].Min, nodes[node.Start].Max, distance, out float a);
            bool nearB = Slab(origin, inverse, nodes[node.Start + 1].Min, nodes[node.Start + 1].Max, distance, out float b);
            if (depth + 2 > stack.Length) { var grown = new int[stack.Length * 2]; stack.CopyTo(grown); stack = grown; }
            // Visit the nearer child first (pushed last).
            if (nearA && nearB)
            {
                if (a <= b) { stack[depth++] = node.Start + 1; stack[depth++] = node.Start; }
                else { stack[depth++] = node.Start; stack[depth++] = node.Start + 1; }
            }
            else if (nearA) stack[depth++] = node.Start;
            else if (nearB) stack[depth++] = node.Start + 1;
        }
        return triangle >= 0;
    }

    /// <summary>Whether the ray meets the box at a parameter within [0, limit] (inclusive at equal distances); its entry parameter.</summary>
    internal static bool Slab(Vector3 origin, Vector3 inverse, Vector3 min, Vector3 max, float limit, out float entry)
    {
        var t1 = (min - origin) * inverse;
        var t2 = (max - origin) * inverse;
        // NaN (a zero direction component with the origin on a slab face) counts as inside.
        var near = Vector3.Min(t1, t2); var far = Vector3.Max(t1, t2);
        float enter = MathF.Max(MathF.Max(Fix(near.X, float.NegativeInfinity), Fix(near.Y, float.NegativeInfinity)), MathF.Max(Fix(near.Z, float.NegativeInfinity), 0));
        float exit = MathF.Min(MathF.Min(Fix(far.X, float.PositiveInfinity), Fix(far.Y, float.PositiveInfinity)), MathF.Min(Fix(far.Z, float.PositiveInfinity), limit));
        entry = enter;
        return enter <= exit * 1.000001f + 1e-6f;
    }
    private static float Fix(float value, float nan) => float.IsNaN(value) ? nan : value;

    /// <summary>Möller–Trumbore, two-sided: the ray parameter of the crossing (t ≥ 0).</summary>
    internal static bool Intersect(Vector3 origin, Vector3 direction, Vector3 p0, Vector3 p1, Vector3 p2, out float t)
    {
        t = 0;
        var e1 = p1 - p0; var e2 = p2 - p0;
        var p = Vector3.Cross(direction, e2);
        float det = Vector3.Dot(e1, p);
        if (det == 0 || !float.IsFinite(det)) return false;
        float inv = 1 / det;
        var s = origin - p0;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(direction, q) * inv;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t >= 0 && float.IsFinite(t);
    }
}

/// <summary>Ray picking over a view's nodes (the Direct3D backend): mesh nodes only, with the current transforms and instances.</summary>
internal static class RayPicking
{
    /// <summary>Every hit in node submission order: one per hit instance (instanced nodes, <see cref="RenderHit.Tag"/> the instance) or one per node (Tag the triangle ordinal).</summary>
    public static IReadOnlyList<RenderHit> HitTest(RenderView view, Vector3 origin, Vector3 direction)
    {
        List<RenderHit> hits = [];
        if (!float.IsFinite(origin.X + origin.Y + origin.Z) || !float.IsFinite(direction.X + direction.Y + direction.Z) || direction.LengthSquared() == 0) return hits;
        Walk(view, view.Items, Matrix4x4.Identity, origin, direction, hits);
        return hits;
    }

    private static void Walk(RenderView view, IReadOnlyList<Node> nodes, Matrix4x4 parent, Vector3 origin, Vector3 direction, List<RenderHit> hits)
    {
        foreach (var node in nodes)
        {
            if (!node.IsHitTestVisible || !SceneRenderer.IsDrawn(view, node)) continue;
            var world = node.Transform is { } local ? local * parent : parent;
            switch (node)
            {
                case GroupNode group: Walk(view, group.Children, world, origin, direction, hits); break;
                case MeshNode { Geometry: { } geometry } mesh: Mesh(mesh, geometry, world, origin, direction, hits); break;
            }
        }
    }

    private static void Mesh(MeshNode node, MeshData geometry, Matrix4x4 world, Vector3 origin, Vector3 direction, List<RenderHit> hits)
    {
        if (geometry.Positions.Length == 0 || geometry.Indices.Length < 3) return;
        var (min, max) = geometry.Bounds;
        // Skip the whole node unless the ray meets the bounds of all its instances.
        var (nodeMin, nodeMax) = node.WorldBounds(world, geometry, (min, max));
        var inverseDirection = new Vector3(1 / direction.X, 1 / direction.Y, 1 / direction.Z);
        if (!MeshBvh.Slab(origin, inverseDirection, nodeMin, nodeMax, float.PositiveInfinity, out _)) return;
        if (node.Instances is { } instances)
        {
            for (int i = 0; i < instances.Count; i++)
                if (Instance(node, geometry, instances[i] * world, min, max, origin, direction) is { } hit) hits.Add(hit with { Tag = i });
        }
        else if (Instance(node, geometry, world, min, max, origin, direction) is { } hit) hits.Add(hit);
    }

    private static RenderHit? Instance(MeshNode node, MeshData geometry, Matrix4x4 matrix, Vector3 min, Vector3 max, Vector3 origin, Vector3 direction)
    {
        var (worldMin, worldMax) = TransformBounds(min, max, matrix);
        var inverseDirection = new Vector3(1 / direction.X, 1 / direction.Y, 1 / direction.Z);
        if (!MeshBvh.Slab(origin, inverseDirection, worldMin, worldMax, float.PositiveInfinity, out _)) return null;
        if (!Matrix4x4.Invert(matrix, out var inverse)) return null;
        var localOrigin = Vector3.Transform(origin, inverse);
        var localDirection = Vector3.TransformNormal(direction, inverse);
        if (!geometry.Bvh.Nearest(localOrigin, localDirection, out float t, out int triangle)) return null;
        int a = geometry.Indices[3 * triangle], b = geometry.Indices[3 * triangle + 1], c = geometry.Indices[3 * triangle + 2];
        var p0 = Vector3.Transform(geometry.Positions[a], matrix);
        var p1 = Vector3.Transform(geometry.Positions[b], matrix);
        var p2 = Vector3.Transform(geometry.Positions[c], matrix);
        var point = Vector3.Transform(localOrigin + localDirection * t, matrix);
        return new RenderHit(node, point, Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0)), (point - origin).Length(), triangle, (a, b, c));
    }

    /// <summary>The axis-aligned bounds of a transformed box.</summary>
    internal static (Vector3 Min, Vector3 Max) TransformBounds(Vector3 min, Vector3 max, Matrix4x4 m)
    {
        var center = (min + max) / 2; var extent = (max - min) / 2;
        var c = Vector3.Transform(center, m);
        var e = new Vector3(
            MathF.Abs(m.M11) * extent.X + MathF.Abs(m.M21) * extent.Y + MathF.Abs(m.M31) * extent.Z,
            MathF.Abs(m.M12) * extent.X + MathF.Abs(m.M22) * extent.Y + MathF.Abs(m.M32) * extent.Z,
            MathF.Abs(m.M13) * extent.X + MathF.Abs(m.M23) * extent.Y + MathF.Abs(m.M33) * extent.Z);
        return (c - e, c + e);
    }

    /// <summary>The eye ray through a surface point (DIPs) of a view <paramref name="size"/> DIPs large: from the eye (perspective) or the view plane (orthographic).</summary>
    public static (Vector3 Origin, Vector3 Direction) EyeRay(RenderCamera camera, Point point, Size size)
    {
        double width = Math.Max(1e-9, size.Width), height = Math.Max(1e-9, size.Height);
        var projection = camera.CreateProjectionMatrix(width / height);
        var forward = camera.LookDirection; forward.Normalize();
        var right = System.Windows.Media.Media3D.Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = System.Windows.Media.Media3D.Vector3D.CrossProduct(right, forward);
        double x = (2 * point.X / width - 1) / projection.M11;
        double y = (1 - 2 * point.Y / height) / projection.M22;
        var origin = camera.Position;
        var direction = forward;
        if (camera is OrthographicRenderCamera) origin += right * x + up * y;
        else { direction += right * x + up * y; direction.Normalize(); }
        return (new((float)origin.X, (float)origin.Y, (float)origin.Z), new((float)direction.X, (float)direction.Y, (float)direction.Z));
    }
}
