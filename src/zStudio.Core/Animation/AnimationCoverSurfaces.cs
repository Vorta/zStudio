using System.Numerics;
using zStudio.Core.Worlds;

namespace zStudio.Core.Animation;

/// <summary>
/// The intersection surfaces NODE_UNDERCOVER tests (<c>TraceUpwardHitFromNodeOrPos</c> 0x45c530, segment picks
/// <c>RaycastFindClosest</c> 0x444e90 and <c>BuildPickCandidateForSegmentVsPolygon</c> 0x4857f0 [B]): polygons of nodes
/// that are active (0x04) and intersection surfaces (0x10) at every level from the world down, skipping any node flagged
/// 0x40000, at the nearest LOD band. A polygon is hit when the segment crosses it from its front side, or from either
/// side when it shows its back face (polygon flag 0x100). Immutable preview-start geometry; runtime-owned nodes are tested
/// from their current pose by the player instead.
/// </summary>
internal sealed class AnimationCoverSurfaces
{
    internal const uint ActiveFlag = 0x04, SurfaceFlag = 0x10, SkipFlag = 0x40000, BackFace = 0x100;
    internal const string NoSurfaces = "the scene has no intersection surfaces";
    private const int MaximumTriangles = 1_000_000;
    private readonly record struct Triangle(int Source, Vector3 A, Vector3 B, Vector3 C, bool TwoSided, Vector3 Min, Vector3 Max);
    private readonly record struct Branch(Vector3 Min, Vector3 Max, int Start, int Count, int Left, int Right);
    private readonly Triangle[] triangles;
    private readonly List<Branch> branches = [];
    internal int TriangleCount => triangles.Length;

    internal AnimationCoverSurfaces(GameScene scene, CancellationToken token = default)
    {
        var work = new LookupWorkBudget(64 * 1024 * 1024, token);
        List<Triangle> collected = [];
        var view = SceneBuilder.Assemble(scene, 0, false, token, null, work, visibility: index => Candidate(scene.Nodes[index]));
        foreach (var placement in view.Placements)
        {
            work.Reserve(1);
            var model = scene.Models[placement.ModelIndex];
            AddModel(model, placement.NodeIndex, placement.Transform, model.Metadata.Float("morph_factor"), (_, _, _, _, _) => true, collected, work.Reserve);
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

    /// <summary>Whether a node is an intersection surface the traversal does not skip (0x10 without 0x40000), whatever its active flag: a run can show it.</summary>
    internal static bool Surface(GameNode node)
    {
        if (node.Class == "world") return true;
        uint flags = node.Metadata.UInt("flags");
        return (flags & SurfaceFlag) != 0 && (flags & SkipFlag) == 0;
    }

    /// <summary>Whether the pick traversal enters <paramref name="node"/>: the world always, any other node by its flags.</summary>
    internal static bool Candidate(GameNode node)
    {
        if (node.Class == "world") return true;
        uint flags = node.Metadata.UInt("flags");
        return (flags & (ActiveFlag | SurfaceFlag)) == (ActiveFlag | SurfaceFlag) && (flags & SkipFlag) == 0;
    }

    /// <summary>The height of the first surface the segment from <paramref name="start"/> up by <paramref name="height"/> crosses, or null.</summary>
    internal float? Hit(Vector3 start, float height, Func<int, bool> excluded, Action<long> reserve)
    {
        if (branches.Count == 0 || !(height > 0)) return null;
        Vector3 end = start + new Vector3(0, height, 0);
        float? first = null;
        Visit(0);
        return first;
        void Visit(int index)
        {
            reserve(1);
            var branch = branches[index];
            if (start.X < branch.Min.X || start.X > branch.Max.X || start.Z < branch.Min.Z || start.Z > branch.Max.Z
                || end.Y < branch.Min.Y || start.Y > branch.Max.Y) return;
            if (branch.Count == 0) { Visit(branch.Left); Visit(branch.Right); return; }
            for (int i = branch.Start; i < branch.Start + branch.Count; i++)
            {
                reserve(1); var t = triangles[i];
                if (Cross(start, end, t.A, t.B, t.C, t.TwoSided) is float y && (first == null || y < first) && !excluded(t.Source)) first = y;
            }
        }
    }

    /// <summary>The first crossing height of one model at a pose, for nodes whose runtime state replaces the static surfaces.</summary>
    internal static float? Hit(GameModel model, Matrix4x4 transform, float morph, Vector3 start, float height, Action<long> reserve)
    {
        if (!(height > 0)) return null;
        Vector3 end = start + new Vector3(0, height, 0);
        float? first = null;
        AddModel(model, -1, transform, morph, (a, b, c, twoSided, _) =>
        {
            if (Cross(start, end, a, b, c, twoSided) is float y && (first == null || y < first)) first = y;
            return false;
        }, null, reserve);
        return first;
    }

    private static void AddModel(GameModel model, int source, Matrix4x4 transform, float morph,
        Func<Vector3, Vector3, Vector3, bool, int, bool> accept, List<Triangle>? collected, Action<long> reserve)
    {
        bool morphs = model.Morphs.Length == model.Vertices.Length;
        foreach (var polygon in model.Polygons)
        {
            reserve(1L + polygon.Vertices.Length);
            if (polygon.Vertices.Length < 3 || polygon.Vertices.Length > 8192) continue;
            if (polygon.Vertices.Any(v => v < 0 || v >= model.Vertices.Length)) continue;
            var vertices = new Vector3[polygon.Vertices.Length];
            bool finite = true;
            for (int i = 0; i < vertices.Length; i++)
            {
                int v = polygon.Vertices[i];
                vertices[i] = Vector3.Transform(model.Vertices[v] + (morphs ? model.Morphs[v] * morph : Vector3.Zero), transform);
                finite &= float.IsFinite(vertices[i].X) && float.IsFinite(vertices[i].Y) && float.IsFinite(vertices[i].Z);
            }
            if (!finite) continue;
            reserve((long)vertices.Length * vertices.Length);
            var indices = (polygon.Flags & 1024) != 0 ? GeometryBuilder.TriangleStrip(vertices.Length) : GeometryBuilder.Triangulate(vertices);
            bool twoSided = (polygon.Flags & BackFace) != 0;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                var a = vertices[indices[i]]; var b = vertices[indices[i + 1]]; var c = vertices[indices[i + 2]];
                if (!accept(a, b, c, twoSided, source) || collected == null) continue;
                if (collected.Count >= MaximumTriangles) throw new InvalidDataException("Intersection surfaces exceed their triangle allowance.");
                collected.Add(new(source, a, b, c, twoSided, Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c))));
            }
        }
    }

    /// <summary>
    /// The segment's crossing height with one triangle: the end must lie behind a one-sided face and the ends on opposite
    /// sides of its plane (the retail sign-bit test), and the crossing inside the triangle.
    /// </summary>
    private static float? Cross(Vector3 start, Vector3 end, Vector3 a, Vector3 b, Vector3 c, bool twoSided)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a);
        if (normal.LengthSquared() < 1e-20f) return null;
        float endSide = Vector3.Dot(end - a, normal);
        if (!twoSided && endSide >= 0) return null;
        float startSide = Vector3.Dot(start - a, normal);
        if (float.IsNegative(startSide) == float.IsNegative(endSide)) return null;
        float t = startSide / (startSide - endSide);
        Vector3 hit = Vector3.Lerp(start, end, t);
        float tolerance = -1e-5f * normal.LengthSquared();
        if (Vector3.Dot(Vector3.Cross(b - a, hit - a), normal) < tolerance || Vector3.Dot(Vector3.Cross(c - b, hit - b), normal) < tolerance
            || Vector3.Dot(Vector3.Cross(a - c, hit - c), normal) < tolerance) return null;
        return hit.Y;
    }
}
