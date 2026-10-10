using System.Numerics;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core;

public sealed record ScenePlacement(int NodeIndex, int ModelIndex, string Name, Matrix4x4 Transform);
public sealed record SceneView(IReadOnlyList<ScenePlacement> Placements, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record MeshPart(int MaterialIndex, Vector3[] Positions, Vector3[] Normals, Vector2[] TextureCoordinates, int[] Indices)
{
    public Vector4[] Colors { get; init; } = [];
    /// <summary>
    /// The zero-based source GameModel.Polygons index for each triangle (three consecutive Indices).
    /// Empty when the producer supplied no source mapping; it never means polygon zero.
    /// </summary>
    public int[] TrianglePolygons { get; init; } = [];
    /// <summary>Polygon identity for each emitted vertex; vertices are duplicated at polygon boundaries.</summary>
    public int[] VertexPolygons { get; init; } = [];
}

public static class SceneBuilder
{
    public static IEnumerable<int> Children(GameNode node)
        => Children(node, node.Children);

    // Mission placement accumulates its selected world's edges before publishing one final array. Traversal
    // during construction must still see those current edges, with the same partition/deduplication semantics.
    internal static IEnumerable<int> Children(GameNode node, IEnumerable<int> children)
    {
        if (node.Class == "world" && node.Data["partitions"] is JsonArray rows)
            children = children.Concat(rows.OfType<JsonArray>().SelectMany(row => row.OfType<JsonObject>()).SelectMany(cell => cell["node_indices"]?.AsArray().Select(v => checked((int)JsonData.Integer(v))) ?? [])).Distinct();
        return children;
    }
    /// <summary>Operation-bounded equivalent of Children, charging raw partition entries even when duplicates collapse.</summary>
    internal static IEnumerable<int> Children(GameNode node, Worlds.LookupWorkBudget work)
    {
        if (node.Class != "world" || node.Data["partitions"] is not JsonArray rows)
        {
            foreach (int child in node.Children) { work.Reserve(1); yield return child; }
            yield break;
        }
        HashSet<int> seen = [];
        foreach (int child in node.Children) { work.Reserve(1); if (seen.Add(child)) yield return child; }
        foreach (var row in rows)
        {
            work.Reserve(1);
            if (row is not JsonArray cells) continue;
            foreach (var cell in cells)
            {
                work.Reserve(1);
                if (cell is not JsonObject value || value["node_indices"] == null) continue;
                foreach (var index in value["node_indices"]!.AsArray())
                {
                    work.Reserve(1);
                    int child = checked((int)JsonData.Integer(index));
                    if (seen.Add(child)) yield return child;
                }
            }
        }
    }
    // zMat4x3: three basis vectors followed by translation. System.Numerics
    // uses row-vector composition, so local * parent implements MatMultiply.
    // Engine evidence: Object3d.c gwObject3DGet/SetPosition; zmth_main.c MatMultiply.
    public static Matrix4x4 LocalTransform(GameNode node)
    {
        if (node.Class == "camera")
        {
            // Retail Class.c 0x4496AE: +0x20 is Euler rotation, +0x14
            // translation, despite the historical SetPosition/SetTarget names.
            if ((node.Data.UInt("flags") & 2) != 0 && node.Data["translate_matrix"] is JsonArray cached && cached.Count == 12)
                return Matrix(cached);
            var p = Vector(node.Data["translate"]); var r = Vector(node.Data["rotate"]);
            return Matrix4x4.CreateFromYawPitchRoll(r.Y, r.X, r.Z) * Matrix4x4.CreateTranslation(p);
        }
        if (node.Class != "object3d" || (node.Data.UInt("flags") & 8) != 0) return Matrix4x4.Identity;
        if (node.Data["transform"] is not JsonArray a || a.Count != 12) throw new InvalidDataException("Object transform must have 12 elements.");
        return Matrix(a);
    }
    private static Vector3 Vector(JsonNode? value)
    {
        var result = new Vector3(value.Float("x"), value.Float("y"), value.Float("z"));
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z)) throw new InvalidDataException("Nonfinite camera transform.");
        return result;
    }
    private static Matrix4x4 Matrix(JsonArray a)
    {
        float[] m = a.Select(v => JsonData.Scalar(v, float.NaN)).ToArray();
        if (m.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Nonfinite scene transform.");
        return new(m[0], m[1], m[2], 0, m[3], m[4], m[5], 0, m[6], m[7], m[8], 0, m[9], m[10], m[11], 1);
    }
    public static SceneView ForAsset(GameScene scene, AssetRecord asset, int lodLevel = 0, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (asset.Kind == AssetKind.World) return Assemble(scene, lodLevel, token: token);
        if (SceneLods.PreviewRoot(scene, asset) is int root) return Assemble(scene, lodLevel, token: token, rootIndex: root);
        return new([new(-1, asset.Index, asset.Name, Matrix4x4.Identity)], []);
    }
    public static SceneView Assemble(GameScene scene, int lodLevel = 0, bool includeHidden = false, CancellationToken token = default, int? rootIndex = null)
        => Assemble(scene, lodLevel, includeHidden, token, rootIndex, new Worlds.LookupWorkBudget(MaximumTraversalWork, token));

    internal const long MaximumTraversalWork = 4_000_000;
    internal const int MaximumPlacements = 200_000;

    internal static SceneView Assemble(GameScene scene, int lodLevel, bool includeHidden, CancellationToken token, int? rootIndex,
        Worlds.LookupWorkBudget work, int maximumPlacements = MaximumPlacements)
    {
        if (maximumPlacements is < 0 or > MaximumPlacements) throw new ArgumentOutOfRangeException(nameof(maximumPlacements));
        List<ScenePlacement> placements = []; BoundedDiagnostics diagnostics = new(); HashSet<int> activePath = [];
        bool refusing = false;
        SceneLods lods;
        try
        {
            // LOD band discovery scans stored edges once, rather than expanded occurrences. Admit that
            // prepass too, before its arrays/dictionaries, and account for roots that are not selected.
            foreach (var node in scene.Nodes) { token.ThrowIfCancellationRequested(); work.Reserve(1L + node.Children.Length); }
            lods = new(scene);
            foreach (var root in scene.Nodes)
            {
                token.ThrowIfCancellationRequested();
                if (rootIndex is int selected ? root.Index == selected : root.Class == "world") Visit(root.Index, Matrix4x4.Identity, 0);
            }
        }
        catch (InvalidDataException ex) when (work.Exhausted)
        { throw new InvalidDataException("Scene traversal exceeds its work budget. Simplify repeated instances or preview a smaller subtree.", ex); }
        return new(placements, diagnostics.Messages.Select(message => new Diagnostic("Warning", message)).ToArray());
        void Visit(int index, Matrix4x4 parent, int depth)
        {
            token.ThrowIfCancellationRequested();
            work.Reserve(1);
            if (index < 0 || index >= scene.Nodes.Count) { diagnostics.Add($"Missing scene node {index}."); return; }
            if (depth > 256 || !activePath.Add(index)) { diagnostics.Add($"Cyclic or excessive scene hierarchy at node {index}."); return; }
            GameNode node = scene.Nodes[index];
            try
            {
                // 0x4 is the engine's visible/traversable node flag.
                if (!includeHidden && index != rootIndex && node.Class is ("object3d" or "lod") && (node.Metadata.UInt("flags") & 4) == 0) return;
                if (!includeHidden && index != rootIndex && node.Metadata["preview_pending_placement"]?.GetValue<bool>() == true) return;
                Matrix4x4 transform = LocalTransform(node) * parent;
                if (node.ModelIndex is int model)
                {
                    if (model >= 0 && model < scene.Models.Count)
                    {
                        if (placements.Count >= maximumPlacements) { refusing = true; throw new InvalidDataException("Scene instance limit exceeded."); }
                        placements.Add(new(index, model, node.Name, transform));
                    }
                    else diagnostics.Add($"Node {index} references missing model {model}.");
                }
                // The world grid owns terrain/static roots outside the ordinary
                // child list. A root can occur in multiple adjacent cells.
                foreach (int child in Children(node, work))
                {
                    if (lods.Includes(index, child, lodLevel)) Visit(child, transform, depth + 1);
                }
            }
            // Local malformed data remains inspectable. A descendant's operation-wide refusal
            // must escape every ancestor instead of becoming a warning followed by more traversal.
            catch (InvalidDataException ex) when (!work.Exhausted && !refusing) { diagnostics.Add($"Node {index}: {ex.Message}"); }
            finally { activePath.Remove(index); }
        }
    }
}

public static class GeometryBuilder
{
    public static IReadOnlyList<MeshPart> Build(GameModel model, IList<Diagnostic>? diagnostics = null, CancellationToken token = default)
    {
        bool colored = model.Polygons.Any(p => p.Colors.Length != 0);
        Dictionary<int, (List<Vector3> Positions, List<Vector3> Normals, List<Vector2> Uvs, List<int> Indices, List<Vector4> Colors, List<int> TrianglePolygons, List<int> VertexPolygons)> groups = [];
        for (int p = 0; p < model.Polygons.Length; p++)
        {
            token.ThrowIfCancellationRequested(); Polygon polygon = model.Polygons[p];
            if (polygon.Vertices.Length < 3) continue;
            if (polygon.Vertices.Any(i => i < 0 || i >= model.Vertices.Length)) { diagnostics?.Add(new("Warning", $"Model {model.Index}, polygon {p}: invalid vertex indices.")); continue; }
            Vector3[] vertices = polygon.Vertices.Select(i => model.Vertices[i]).ToArray();
            if (vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))) { diagnostics?.Add(new("Warning", $"Model {model.Index}, polygon {p}: nonfinite vertices.")); continue; }
            int[] triangles = (polygon.Flags & 1024) != 0 ? TriangleStrip(vertices.Length) : Triangulate(vertices);
            if (triangles.Length == 0) { diagnostics?.Add(new("Warning", $"Model {model.Index}, polygon {p}: degenerate polygon.")); continue; }
            if (!groups.TryGetValue(polygon.MaterialIndex, out var group)) { group = ([], [], [], [], [], [], []); groups.Add(polygon.MaterialIndex, group); }
            int offset = group.Positions.Count;
            Vector3 normal = Vector3.Cross(vertices[triangles[1]] - vertices[triangles[0]], vertices[triangles[2]] - vertices[triangles[0]]);
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
            for (int i = 0; i < vertices.Length; i++)
            {
                group.Positions.Add(vertices[i]);
                group.VertexPolygons.Add(p);
                if (colored)
                {
                    var color = polygon.Colors.Length == vertices.Length ? polygon.Colors[i] / 255f : Vector3.One;
                    group.Colors.Add(new Vector4(float.IsFinite(color.LengthSquared()) ? Vector3.Clamp(color, Vector3.Zero, Vector3.One) : Vector3.One, 1));
                }
                Vector3 n = polygon.Normals.Length == vertices.Length && polygon.Normals[i] >= 0 && polygon.Normals[i] < model.Normals.Length ? model.Normals[polygon.Normals[i]] : normal;
                group.Normals.Add(n.LengthSquared() > 1e-12f && float.IsFinite(n.LengthSquared()) ? Vector3.Normalize(n) : normal);
                group.Uvs.Add(polygon.Uvs.Length == vertices.Length ? polygon.Uvs[i] : Vector2.Zero);
            }
            foreach (int i in triangles) group.Indices.Add(offset + i);
            for (int i = 0; i < triangles.Length; i += 3) group.TrianglePolygons.Add(p);
        }
        return groups.Select(g => new MeshPart(g.Key, g.Value.Positions.ToArray(), g.Value.Normals.ToArray(), g.Value.Uvs.ToArray(), g.Value.Indices.ToArray())
        { Colors = colored ? g.Value.Colors.ToArray() : [], TrianglePolygons = g.Value.TrianglePolygons.ToArray(), VertexPolygons = g.Value.VertexPolygons.ToArray() }).ToArray();
    }
    public static int[] TriangleStrip(int count)
    {
        List<int> indices = [];
        for (int i = 2; i < count; i++) indices.AddRange(i % 2 == 0 ? [i - 2, i - 1, i] : [i - 1, i - 2, i]);
        return indices.ToArray();
    }
    public static int[] Triangulate(IReadOnlyList<Vector3> points)
    {
        if (points.Count < 3) return [];
        Vector3 normal = Vector3.Zero;
        for (int i = 0; i < points.Count; i++)
        { Vector3 a = points[i], b = points[(i + 1) % points.Count]; normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y)); }
        if (normal.LengthSquared() < 1e-18f) return [];
        Vector3 abs = Vector3.Abs(normal); int drop = abs.X >= abs.Y && abs.X >= abs.Z ? 0 : abs.Y >= abs.Z ? 1 : 2;
        Vector2[] projected = points.Select(v => drop == 0 ? new Vector2(v.Y, v.Z) : drop == 1 ? new Vector2(v.X, v.Z) : new Vector2(v.X, v.Y)).ToArray();
        float area = 0; for (int i = 0; i < points.Count; i++) area += Cross(projected[i], projected[(i + 1) % points.Count]);
        float sign = area >= 0 ? 1 : -1; List<int> ring = Enumerable.Range(0, points.Count).ToList(); List<int> result = [];
        while (ring.Count > 3)
        {
            bool found = false;
            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[(i + ring.Count - 1) % ring.Count], b = ring[i], c = ring[(i + 1) % ring.Count];
                float cross = Cross(projected[b] - projected[a], projected[c] - projected[b]) * sign;
                if (Math.Abs(cross) < 1e-10f) { ring.RemoveAt(i); found = true; break; }
                if (cross < 0) continue;
                bool inside = ring.Any(j => j != a && j != b && j != c && Cross(projected[b] - projected[a], projected[j] - projected[a]) * sign > 1e-8f && Cross(projected[c] - projected[b], projected[j] - projected[b]) * sign > 1e-8f && Cross(projected[a] - projected[c], projected[j] - projected[c]) * sign > 1e-8f);
                if (inside) continue;
                result.AddRange([a, b, c]); ring.RemoveAt(i); found = true; break;
            }
            if (!found) return [];
        }
        if (ring.Count == 3) result.AddRange(ring); return result.ToArray();
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
