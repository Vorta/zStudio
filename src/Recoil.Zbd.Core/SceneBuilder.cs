using System.Numerics;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core;

public sealed record ScenePlacement(int NodeIndex, int ModelIndex, string Name, Matrix4x4 Transform);
public sealed record SceneView(IReadOnlyList<ScenePlacement> Placements, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record MeshPart(int MaterialIndex, Vector3[] Positions, Vector3[] Normals, Vector2[] TextureCoordinates, int[] Indices);

public static class SceneBuilder
{
    public static IEnumerable<int> Children(GameNode node)
    {
        IEnumerable<int> children = node.Children;
        if (node.Class == "world" && node.Data["partitions"] is JsonArray rows)
            children = children.Concat(rows.OfType<JsonArray>().SelectMany(row => row.OfType<JsonObject>()).SelectMany(cell => cell["node_indices"]?.AsArray().Select(v => checked((int)JsonData.Integer(v))) ?? [])).Distinct();
        return children;
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
        if (asset.Kind == AssetKind.World) return Assemble(scene, lodLevel, token: token);
        if (SceneLods.PreviewRoot(scene, asset) is int root) return Assemble(scene, lodLevel, token: token, rootIndex: root);
        return new([new(-1, asset.Index, asset.Name, Matrix4x4.Identity)], []);
    }
    public static SceneView Assemble(GameScene scene, int lodLevel = 0, bool includeHidden = false, CancellationToken token = default, int? rootIndex = null)
    {
        List<ScenePlacement> placements = []; List<Diagnostic> diagnostics = []; HashSet<int> activePath = [];
        SceneLods lods = new(scene);
        var roots = scene.Nodes.Where(n => rootIndex is int root ? n.Index == root : n.Class == "world").ToArray();
        foreach (var root in roots) Visit(root.Index, Matrix4x4.Identity, 0);
        return new(placements, diagnostics);
        void Visit(int index, Matrix4x4 parent, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (index < 0 || index >= scene.Nodes.Count) { diagnostics.Add(new("Warning", $"Missing scene node {index}.")); return; }
            if (depth > 256 || !activePath.Add(index)) { diagnostics.Add(new("Warning", $"Cyclic or excessive scene hierarchy at node {index}.")); return; }
            if (placements.Count >= 200_000) throw new InvalidDataException("Scene instance limit exceeded.");
            GameNode node = scene.Nodes[index];
            try
            {
                // 0x4 is the engine's visible/traversable node flag.
                if (!includeHidden && index != rootIndex && node.Class is ("object3d" or "lod") && (node.Metadata.UInt("flags") & 4) == 0) return;
                if (!includeHidden && index != rootIndex && node.Metadata["preview_pending_placement"]?.GetValue<bool>() == true) return;
                Matrix4x4 transform = LocalTransform(node) * parent;
                if (node.ModelIndex is int model)
                {
                    if (model >= 0 && model < scene.Models.Count) placements.Add(new(index, model, node.Name, transform));
                    else diagnostics.Add(new("Warning", $"Node {index} references missing model {model}."));
                }
                // The world grid owns terrain/static roots outside the ordinary
                // child list. A root can occur in multiple adjacent cells.
                foreach (int child in Children(node))
                {
                    if (lods.Includes(index, child, lodLevel)) Visit(child, transform, depth + 1);
                }
            }
            catch (InvalidDataException ex) { diagnostics.Add(new("Warning", $"Node {index}: {ex.Message}")); }
            finally { activePath.Remove(index); }
        }
    }
}

public static class GeometryBuilder
{
    public static IReadOnlyList<MeshPart> Build(GameModel model, IList<Diagnostic>? diagnostics = null, CancellationToken token = default)
    {
        Dictionary<int, (List<Vector3> Positions, List<Vector3> Normals, List<Vector2> Uvs, List<int> Indices)> groups = [];
        for (int p = 0; p < model.Polygons.Length; p++)
        {
            token.ThrowIfCancellationRequested(); Polygon polygon = model.Polygons[p];
            if (polygon.Vertices.Length < 3) continue;
            if (polygon.Vertices.Any(i => i < 0 || i >= model.Vertices.Length)) { diagnostics?.Add(new("Warning", $"Model {model.Index}, polygon {p}: invalid vertex indices.")); continue; }
            Vector3[] vertices = polygon.Vertices.Select(i => model.Vertices[i]).ToArray();
            if (vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))) { diagnostics?.Add(new("Warning", $"Model {model.Index}, polygon {p}: nonfinite vertices.")); continue; }
            int[] triangles = Triangulate(vertices);
            if (triangles.Length == 0) { diagnostics?.Add(new("Warning", $"Model {model.Index}, polygon {p}: degenerate polygon.")); continue; }
            if (!groups.TryGetValue(polygon.MaterialIndex, out var group)) { group = ([], [], [], []); groups.Add(polygon.MaterialIndex, group); }
            int offset = group.Positions.Count;
            Vector3 normal = Vector3.Cross(vertices[triangles[1]] - vertices[triangles[0]], vertices[triangles[2]] - vertices[triangles[0]]);
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
            for (int i = 0; i < vertices.Length; i++)
            {
                group.Positions.Add(vertices[i]);
                Vector3 n = polygon.Normals.Length == vertices.Length && polygon.Normals[i] >= 0 && polygon.Normals[i] < model.Normals.Length ? model.Normals[polygon.Normals[i]] : normal;
                group.Normals.Add(n.LengthSquared() > 1e-12f && float.IsFinite(n.LengthSquared()) ? Vector3.Normalize(n) : normal);
                group.Uvs.Add(polygon.Uvs.Length == vertices.Length ? polygon.Uvs[i] : Vector2.Zero);
            }
            foreach (int i in triangles) group.Indices.Add(offset + i);
        }
        return groups.Select(g => new MeshPart(g.Key, g.Value.Positions.ToArray(), g.Value.Normals.ToArray(), g.Value.Uvs.ToArray(), g.Value.Indices.ToArray())).ToArray();
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
