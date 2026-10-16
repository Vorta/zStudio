using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceBlender
{
    private static void PreserveGraphOrder(GraphModel before, JsonObject after, GraphMapping mapping)
    {
        var original = before.Json["nodes"] as JsonArray ?? [];
        var nodes = after["nodes"] as JsonArray ?? [];
        void Order(JsonArray list, JsonArray? previous)
        {
            var rank = (previous ?? []).Select((v, i) => (Index: v!.GetValue<int>(), Rank: i)).ToDictionary(p => p.Index, p => p.Rank);
            var sorted = list.Select(v => v!.GetValue<int>()).OrderBy(i => rank.GetValueOrDefault(mapping.Nodes[i], int.MaxValue)).ToArray();
            list.Clear(); foreach (int i in sorted) list.Add(i);
        }
        foreach (var (index, origin) in mapping.Nodes)
        {
            var current = nodes[index]!.AsObject();
            if (origin < 0) continue;
            var old = original[origin]!.AsObject();
            // Blender's alphabetical display/export ordering must not change engine lookup order.
            if (current["children"] is JsonArray children) Order(children, old["children"] as JsonArray);
            if (current["extras"]?["zstudio_transform_changed"]?.GetValue<bool>() != true)
                foreach (string key in new[] { "matrix", "translation", "rotation", "scale" })
                { current.Remove(key); if (old[key] is { } value) current[key] = value.DeepClone(); }
            if (old["extras"] is JsonObject extras)
                foreach (var (key, value) in extras)
                    if (key != WorldGltf.Key && current["extras"]?[key] == null) current["extras"]![key] = value?.DeepClone();
            if (old["extras"]?[WorldGltf.Key]?[WorldGltf.InlineSourceField] is JsonObject inline)
            {
                int count = inline["count"]!.GetValue<int>();
                var members = old["children"]!.AsArray().Take(count).Select(v => v!.GetValue<int>()).ToHashSet();
                current["extras"]![WorldGltf.Key]![WorldGltf.InlineSourceField]!["count"] =
                    (current["children"] as JsonArray ?? []).Count(v => members.Contains(mapping.Nodes[v!.GetValue<int>()]));
            }
        }
        int oldScene = before.Json["scene"]?.GetValue<int>() ?? 0, newScene = after["scene"]?.GetValue<int>() ?? 0;
        if (after["scenes"]?[newScene]?["nodes"] is JsonArray roots) Order(roots, before.Json["scenes"]?[oldScene]?["nodes"] as JsonArray);
    }

    // Recover a boundary only when it retains the exported surface, normals, UVs and morph surfaces.
    // The existing shared equivalence check also tests zStudio's preview triangulation for nonplanar faces.
    private static int[]? ExportBoundary(GltfPrimitive primitive, int first, int count, PolygonWorkBudget work)
    {
        if (count < 2 || count + 2 > ModelBuilder.MaximumCorners) return null;
        Dictionary<int, int> aliases = []; Dictionary<int, int> changes = [];
        bool Same(int a, int b) => primitive.Positions[a] == primitive.Positions[b]
            && (primitive.Normals.Count == 0 || WorldGltf.StoresNormals(primitive.Material, "Blender export") == false || primitive.Normals[a] == primitive.Normals[b])
            && (primitive.TexCoords.Count == 0 || primitive.TexCoords[a] == primitive.TexCoords[b])
            && primitive.Targets.All(t => t[a] == t[b]);
        for (int i = first * 3; i < (first + count) * 3; i++)
        {
            int vertex = primitive.Indices[i];
            if (!aliases.TryGetValue(vertex, out int alias))
            {
                alias = aliases.Values.Distinct().FirstOrDefault(v => Same(vertex, v), -1);
                aliases.Add(vertex, alias < 0 ? vertex : alias);
            }
            changes[i] = aliases[vertex];
        }
        Dictionary<(int, int), int> edges = [];
        for (int t = first; t < first + count; t++)
            for (int c = 0; c < 3; c++)
            {
                var edge = (changes[t * 3 + c], changes[t * 3 + (c + 1) % 3]);
                if (edge.Item1 == edge.Item2) return null;
                edges[edge] = edges.GetValueOrDefault(edge) + 1;
            }
        var boundary = edges.Where(p => p.Value - edges.GetValueOrDefault((p.Key.Item2, p.Key.Item1)) > 0).ToArray();
        if (boundary.Length != count + 2 || boundary.Any(p => p.Value != 1) || boundary.Select(p => p.Key.Item1).Distinct().Count() != boundary.Length) return null;
        var next = boundary.ToDictionary(p => p.Key.Item1, p => p.Key.Item2);
        List<int> corners = []; int cursor = boundary[0].Key.Item1;
        while (corners.Count < boundary.Length && !corners.Contains(cursor))
        { corners.Add(cursor); if (!next.TryGetValue(cursor, out cursor)) return null; }
        if (corners.Count != boundary.Length || cursor != corners[0]) return null;
        // Prefer the original fan's first corner. Other rotations are useful after a mirrored edit.
        for (int rotation = 0; rotation < corners.Count; rotation++)
        {
            int[] candidate = corners.Skip(rotation).Concat(corners.Take(rotation)).ToArray();
            if (!WorldGltf.MorphPreservesSurface(primitive, candidate, work)) continue;
            if (primitive.Material?.ImageUri != null && primitive.TexCoords.Count == primitive.Positions.Count
                && !ModelBuilder.Affine(candidate.Select(c => primitive.Positions[c]).ToArray(), candidate.Select(c => primitive.TexCoords[c]).ToArray(), ModelBuilder.AffineTolerance / 4, work)) continue;
            if (!KeepsTriangles(primitive, first, count, candidate, changes, work)) continue;
            bool morphs = true;
            foreach (var target in primitive.Targets)
            {
                var local = candidate.Select((v, i) => (v, i)).ToDictionary(p => p.v, p => p.i);
                GltfPrimitive shape = new();
                shape.Indices.AddRange(Enumerable.Range(first * 3, count * 3).Select(i => local[changes[i]]));
                shape.Positions.AddRange(candidate.Select(i => primitive.Positions[i] + target[i]));
                if (!KeepsTriangles(shape, 0, count, Enumerable.Range(0, candidate.Length).ToArray(), [], work)) { morphs = false; break; }
            }
            if (morphs)
            {
                foreach (var (at, vertex) in changes) primitive.Indices[at] = vertex;
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Whether recovering an edited boundary preserves its surface in both polygon consumers.</summary>
    private static bool KeepsTriangles(GltfPrimitive primitive, int first, int count, int[] corners, Dictionary<int, int> changes, PolygonWorkBudget work)
    {
        // Keep explicit polygon boundaries within the supported corner limit.
        if (corners.Length > ModelBuilder.MaximumCorners) return false;
        // Ear clipping and its inner scans are charged before allocation.
        work.Charge(2L * corners.Length * corners.Length * corners.Length + count);
        var points = corners.Select(c => primitive.Positions[c]).ToArray();
        static (int, int, int) Key(int a, int b, int c) => a <= b && a <= c ? (a, b, c) : b <= c ? (b, c, a) : (c, a, b);
        var recovered = WorldGltf.Triangulate(points)
            .Where(t => !ModelBuilder.Straight(points[t.Item1], points[t.Item2], points[t.Item3]))
            .Select(t => Key(corners[t.Item1], corners[t.Item2], corners[t.Item3])).Order().ToArray();
        int At(int at) => changes.GetValueOrDefault(at, primitive.Indices[at]);
        var authored = Enumerable.Range(first, count).Select(t => Key(At(t * 3), At(t * 3 + 1), At(t * 3 + 2))).Order().ToArray();
        if (!recovered.SequenceEqual(authored)) return false;
        // Coplanar polygons retain their established interpolation and boundary semantics. Only a nonplanar
        // boundary can change the geometric surface merely by choosing a different preview diagonal.
        if (ModelBuilder.Coplanar(points)) return true;
        var rendered = GeometryBuilder.Triangulate(points);
        var preview = Enumerable.Range(0, rendered.Length / 3).Select(t => Key(corners[rendered[t * 3]], corners[rendered[t * 3 + 1]], corners[rendered[t * 3 + 2]])).Order().ToArray();
        return preview.SequenceEqual(authored);
    }

    private static void WriteGraphIndices(string path, JsonObject root, List<(JsonObject Primitive, int[] Indices)> patches, Dictionary<string, byte[]> output)
    {
        if (patches.Count == 0) return;
        var buffer = root["buffers"]![0]!.AsObject();
        string target = WorldAssembler.Relative(path, Uri.UnescapeDataString(buffer["uri"]!.GetValue<string>()));
        var previous = output[target];
        long added = patches.Sum(p => p.Indices.LongLength * 4) + 3;
        if (added > MaximumExportBytes - output.Sum(p => (long)p.Value.Length)) throw new InvalidDataException("Exported polygon preservation exceeds 512 MiB.");
        using MemoryStream bytes = new(checked(previous.Length + (int)added));
        bytes.Write(previous); while (bytes.Length % 4 != 0) bytes.WriteByte(0);
        var views = root["bufferViews"]!.AsArray(); var accessors = root["accessors"]!.AsArray();
        Span<byte> word = stackalloc byte[4];
        foreach (var (primitive, indices) in patches)
        {
            int view = views.Count;
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = bytes.Length, ["byteLength"] = indices.LongLength * 4 });
            primitive["indices"] = accessors.Count;
            accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = 5125, ["count"] = indices.Length, ["type"] = "SCALAR" });
            foreach (int index in indices) { System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)index); bytes.Write(word); }
        }
        buffer["byteLength"] = bytes.Length; output[target] = bytes.ToArray();
    }
}
