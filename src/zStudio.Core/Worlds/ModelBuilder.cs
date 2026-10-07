using System.Numerics;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>A polygon to add: corners in order, with UVs, normals and morph target positions when present.</summary>
public sealed record PolygonInput(Vector3[] Points, Vector2[] Uvs, Vector3[] Normals, Vector3[] Targets, WorldMaterial Material, int Priority = 0, bool ShowBackFace = false, uint Zone = 0xFFFFFF00);

/// <summary>
/// Builds a display instance polygon by polygon as the original build did, so compiled models match the shipped ones:
/// large polygons split, vertices merge within 0.001 (at most 921 per model), normals within 0.0001, and textured UVs
/// are shifted to their tile and quantized to 1/256. Polygons are otherwise kept as authored. The runtime's
/// zDi::AddPolygonEx (retail 0x483650) also removes colinear corners, fans non-planar polygons and extrapolates UVs
/// past the first triangle, but the shipped models repeat corners (a vertex listed twice in a row), hold non-planar
/// polygons and keep a non-affine mapping, so the build did none of that; only a polygon without area is discarded.
/// </summary>
public sealed class ModelBuilder(WorldModel model)
{
    public const float VertexMergeEpsilon = 0.001f;
    public const double NormalMergeEpsilon = 0.0001, CoplanarTolerance = 0.001;
    /// <summary>AddOrMergeVertex fails once a model holds more than 0.9 × 1024 vertices.</summary>
    public const int MaximumVertices = 921, SplitCorners = 48, MaximumCorners = 57;
    /// <summary>How far a corner's UV may lie from the first triangle's affine map and still count as on it.</summary>
    public const float AffineTolerance = 1f / 32;
    public WorldModel Model { get; } = model;
    public List<string> Warnings { get; } = [];
    public ModelBuilder() : this(new WorldModel()) { }

    public bool Add(PolygonInput polygon)
    {
        int n = polygon.Points.Length;
        if (n < 3) { Warnings.Add($"A polygon with {n} corners was discarded."); return false; }
        bool textured = polygon.Material.Texture != null;
        if (textured && polygon.Uvs.Length != n) throw new InvalidDataException("A textured polygon needs a UV for every corner.");
        // A non-finite coordinate would reach the world's bounds and grid cells.
        if (!polygon.Points.All(Finite) || !polygon.Targets.All(Finite) || !polygon.Normals.All(Finite) || !polygon.Uvs.All(uv => float.IsFinite(uv.X) && float.IsFinite(uv.Y)))
            throw new InvalidDataException("A polygon has a non-finite coordinate; no geometry was accepted.");
        if (polygon.Targets.Length == n && Enumerable.Range(0, n).Any(i => !Finite(polygon.Targets[i] - polygon.Points[i])))
            throw new InvalidDataException("A polygon's morph delta exceeds the finite coordinate range.");
        // Validate derived UVs before adding any vertices or fanning the polygon: finite authored values can overflow
        // the tile shift or integer quantization. Rejection must leave the builder usable for the next polygon.
        Vector2[] uvs = textured ? Uvs(polygon.Uvs) : [];
        if (n > MaximumCorners) { Warnings.Add($"A polygon with {n} corners exceeds the engine limit and was fanned."); return Fan(polygon); }
        if (!HasArea(polygon.Points)) { Warnings.Add("A polygon without area (all corners on one line) was discarded."); return false; }
        if (polygon.Points.Length > SplitCorners) return Split(polygon);

        int count = polygon.Points.Length; bool morphs = polygon.Targets.Length == count;
        int[] vertices = new int[count];
        // A polygon is added whole or not at all: when a corner finds no room, the vertices and morph deltas its earlier
        // corners added go again, so they take no room from the polygons after it.
        int vertexCount = Model.Vertices.Count, morphCount = Model.Morphs.Count;
        for (int i = 0; i < count; i++)
        {
            vertices[i] = morphs ? AddVertexAndMorph(polygon.Points[i], polygon.Targets[i]) : AddVertex(polygon.Points[i]);
            if (vertices[i] < 0)
            {
                Model.Vertices.RemoveRange(vertexCount, Model.Vertices.Count - vertexCount);
                Model.Morphs.RemoveRange(morphCount, Model.Morphs.Count - morphCount);
                Warnings.Add($"The model exceeds {MaximumVertices} vertices; a polygon was discarded."); return false;
            }
        }
        int[] normals = polygon.Normals.Length == count ? AddNormals(polygon.Normals) : [];
        Model.Polygons.Add(new() { Material = polygon.Material, Priority = polygon.Priority, Flags = polygon.ShowBackFace ? 0x100u : 0, Zone = polygon.Zone, Vertices = vertices, Normals = normals, Uvs = uvs });
        return true;
    }

    private static PolygonInput Select(PolygonInput p, List<int> keep) => p with
    {
        Points = keep.Select(i => p.Points[i]).ToArray(), Uvs = p.Uvs.Length == p.Points.Length ? keep.Select(i => p.Uvs[i]).ToArray() : p.Uvs,
        Normals = p.Normals.Length == p.Points.Length ? keep.Select(i => p.Normals[i]).ToArray() : p.Normals,
        Targets = p.Targets.Length == p.Points.Length ? keep.Select(i => p.Targets[i]).ToArray() : p.Targets,
    };

    /// <summary>Fan triangles from the first corner, for polygons past the engine's corner limit.</summary>
    private bool Fan(PolygonInput p)
    {
        bool any = false;
        for (int i = 1; i + 1 < p.Points.Length; i++) any |= Add(Select(p, [0, i, i + 1]));
        return any;
    }
    /// <summary>Planar polygons above 48 corners split into fans of at most 48 corners sharing the first corner.</summary>
    private bool Split(PolygonInput p)
    {
        bool any = false; int start = 1;
        while (start + 1 < p.Points.Length)
        {
            int end = Math.Min(p.Points.Length - 1, start + SplitCorners - 2);
            List<int> chunk = [0, .. Enumerable.Range(start, end - start + 1)];
            any |= Add(Select(p, chunk)); start = end;
        }
        return any;
    }

    /// <summary>Whether the corners span a plane: some corner lies off the line through the first two distinct ones.</summary>
    private static bool HasArea(Vector3[] points)
    {
        for (int i = 1; i < points.Length; i++)
            if (points[i] != points[0])
            {
                for (int j = i + 1; j < points.Length; j++) if (!Straight(points[0], points[i], points[j])) return true;
                return false;
            }
        return false;
    }

    /// <summary>
    /// Whether corner <paramref name="b"/> lies on the line through its neighbours: every component of the cross product
    /// of its edges is zero in float arithmetic. No shipped polygon has such a corner past its first except where a
    /// corner repeats, so fans are never merged across one.
    /// </summary>
    public static bool Straight(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 cross = Vector3.Cross(b - a, c - b);
        return cross.X == 0 && cross.Y == 0 && cross.Z == 0;
    }

    /// <summary>
    /// Whether every corner's UV lies on one affine map, fitted on the corners spanning the largest triangle so that
    /// quantized UVs of close corners do not skew it.
    /// </summary>
    public static bool Affine(IReadOnlyList<Vector3> points, IReadOnlyList<Vector2> uvs, float tolerance, CancellationToken token = default)
        => Affine(points, uvs, tolerance, new PolygonWorkBudget(token));

    internal static bool Affine(IReadOnlyList<Vector3> points, IReadOnlyList<Vector2> uvs, float tolerance, PolygonWorkBudget work)
    {
        work.CheckCancellation();
        if (points.Count <= 3) return true;
        // Preserve the largest-triangle fit and its deterministic tie order, but account for the entire cubic search
        // before allocating projected corners or trying any triple. Large arbitrary callers also fail without overflow.
        long triples = points.Count > 10_000 ? long.MaxValue : (long)points.Count * (points.Count - 1) * (points.Count - 2) / 6;
        work.Charge(triples);
        work.Charge(3L * points.Count);
        Vector3 normal = Vector3.Zero;
        for (int i = 0; i < points.Count; i++) normal += Vector3.Cross(points[i], points[(i + 1) % points.Count]);
        float ax = Math.Abs(normal.X), ay = Math.Abs(normal.Y), az = Math.Abs(normal.Z);
        Func<Vector3, Vector2> project = ax >= ay && ax >= az ? p => new(p.Y, p.Z) : ay >= ax && ay >= az ? p => new(p.Z, p.X) : p => new(p.X, p.Y);
        var q = points.Select(project).ToArray();
        (int, int, int) best = (0, 1, 2); float area = -1;
        for (int i = 0; i < q.Length; i++)
        {
            work.CheckCancellation();
            for (int j = i + 1; j < q.Length; j++) for (int k = j + 1; k < q.Length; k++)
                {
                    float a = Math.Abs((q[j].X - q[i].X) * (q[k].Y - q[i].Y) - (q[k].X - q[i].X) * (q[j].Y - q[i].Y));
                    if (a > area) { area = a; best = (i, j, k); }
                }
        }
        var (i0, i1, i2) = best;
        if (Gradient(q[i0], q[i1], q[i2], uvs[i0].X, uvs[i1].X, uvs[i2].X) is not { } gu || Gradient(q[i0], q[i1], q[i2], uvs[i0].Y, uvs[i1].Y, uvs[i2].Y) is not { } gv) return false;
        for (int i = 0; i < q.Length; i++)
        {
            Vector2 d = q[i] - q[i0];
            if (Vector2.Distance(new(uvs[i0].X + d.X * gu.X + d.Y * gu.Y, uvs[i0].Y + d.X * gv.X + d.Y * gv.Y), uvs[i]) > tolerance) return false;
        }
        return true;
    }

    /// <summary>IsPolygonCoplanar: every corner within 0.001 of the polygon's plane (Newell normal through the centroid).</summary>
    public static bool Coplanar(IReadOnlyList<Vector3> points)
    {
        double nx = 0, ny = 0, nz = 0, cx = 0, cy = 0, cz = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Count];
            nx += ((double)a.Y - b.Y) * ((double)a.Z + b.Z); ny += ((double)a.Z - b.Z) * ((double)a.X + b.X); nz += ((double)a.X - b.X) * ((double)a.Y + b.Y);
            cx += a.X; cy += a.Y; cz += a.Z;
        }
        double length = Math.Sqrt(nx * nx + ny * ny + nz * nz); if (length < 1e-12) return false;
        nx /= length; ny /= length; nz /= length; cx /= points.Count; cy /= points.Count; cz /= points.Count;
        double d = -(nx * cx + ny * cy + nz * cz);
        return points.All(p => Math.Abs(p.X * nx + p.Y * ny + p.Z * nz + d) <= CoplanarTolerance);
    }

    private int AddVertex(Vector3 point)
    {
        for (int i = 0; i < Model.Vertices.Count; i++)
        {
            var v = Model.Vertices[i];
            if (Math.Abs(v.X - point.X) <= VertexMergeEpsilon && Math.Abs(v.Y - point.Y) <= VertexMergeEpsilon && Math.Abs(v.Z - point.Z) <= VertexMergeEpsilon) return i;
        }
        if (Model.Vertices.Count >= MaximumVertices) return -1;
        Model.Vertices.Add(point); return Model.Vertices.Count - 1;
    }
    /// <summary>AddOrMergeVertexAndNormal: morphing models merge only identical positions with identical deltas.</summary>
    private int AddVertexAndMorph(Vector3 point, Vector3 target)
    {
        Vector3 delta = target - point;
        while (Model.Morphs.Count < Model.Vertices.Count) Model.Morphs.Add(Vector3.Zero);
        for (int i = 0; i < Model.Vertices.Count; i++) if (Model.Vertices[i] == point && Model.Morphs[i] == delta) return i;
        if (Model.Vertices.Count >= MaximumVertices) return -1;
        Model.Vertices.Add(point); Model.Morphs.Add(delta); return Model.Vertices.Count - 1;
    }
    /// <summary>
    /// FindOrAppendNormalIndex for each corner. The game transforms a model's normals into a 1,024-entry buffer
    /// (g_zModel_TransformedNormals, retail PrepareTransformedNormals) and the build stopped at 921, so a polygon whose
    /// normals would pass that is stored without normals (drawn flat) instead of overrunning the buffer in the game.
    /// </summary>
    private int[] AddNormals(Vector3[] source)
    {
        int[] indices = new int[source.Length]; int existing = Model.Normals.Count; List<Vector3> added = [];
        for (int i = 0; i < source.Length; i++)
        {
            int found = Find(Model.Normals, source[i]);
            if (found < 0 && Find(added, source[i]) is var fresh and >= 0) found = existing + fresh;
            if (found < 0) { found = existing + added.Count; added.Add(source[i]); }
            indices[i] = found;
        }
        if (existing + added.Count > MaximumVertices) { Warnings.Add($"The model exceeds {MaximumVertices} normals; polygons past them are stored without normals."); return []; }
        Model.Normals.AddRange(added);
        return indices;
        static int Find(List<Vector3> normals, Vector3 normal)
        {
            for (int i = 0; i < normals.Count; i++)
            {
                var n = normals[i];
                if (Math.Abs(n.X - normal.X) < NormalMergeEpsilon && Math.Abs(n.Y - normal.Y) < NormalMergeEpsilon && Math.Abs(n.Z - normal.Z) < NormalMergeEpsilon) return i;
            }
            return -1;
        }
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>The engine's UV pipeline for a textured entry: tile shift, affine extrapolation past the first triangle, 1/256 quantization, tile shift.</summary>
    private static Vector2[] Uvs(Vector2[] source)
    {
        Vector2[] uv = (Vector2[])source.Clone();
        // Corners keep their own UVs: the one shipped polygon whose mapping is not affine stores them unchanged, so the
        // original build did not extrapolate corners past the first triangle as the runtime's AddPolygonEx does.
        Shift(uv);
        for (int i = 0; i < uv.Length; i++) uv[i] = new(Quantize(uv[i].X), Quantize(uv[i].Y));
        Shift(uv);
        return uv;
        static float Quantize(float value)
        {
            float scaled = (value + 0.001953125f) * 256f;
            if (!float.IsFinite(scaled) || (double)scaled < int.MinValue || (double)scaled > int.MaxValue)
                throw new InvalidDataException("A polygon's texture coordinates exceed the game's integer quantization range after tile shifting.");
            return (int)scaled * (1f / 256f);
        }
        static void Shift(Vector2[] uv)
        {
            float minU = uv.Min(u => u.X), minV = uv.Min(u => u.Y), baseU = MathF.Floor(minU), baseV = MathF.Floor(minV);
            for (int i = 0; i < uv.Length; i++) uv[i] -= new Vector2(baseU, baseV);
        }
    }
    /// <summary>The gradient (∂s/∂a, ∂s/∂b) of a scalar linear over a triangle in 2D, or null for a degenerate triangle.</summary>
    internal static Vector2? Gradient(Vector2 p0, Vector2 p1, Vector2 p2, float s0, float s1, float s2)
    {
        float a1 = p1.X - p0.X, b1 = p1.Y - p0.Y, a2 = p2.X - p0.X, b2 = p2.Y - p0.Y, det = a1 * b2 - a2 * b1;
        if (MathF.Abs(det) < 1e-12f) return null;
        float d1 = s1 - s0, d2 = s2 - s0;
        return new((d1 * b2 - d2 * b1) / det, (a1 * d2 - a2 * d1) / det);
    }

    /// <summary>After all polygons: pad morph deltas for vertices added without them, and compute the model bounds.</summary>
    public WorldModel Finish()
    {
        if (Model.Morphs.Count > 0) while (Model.Morphs.Count < Model.Vertices.Count) Model.Morphs.Add(Vector3.Zero);
        WorldUpdate.RebuildModel(Model);
        return Model;
    }
}
