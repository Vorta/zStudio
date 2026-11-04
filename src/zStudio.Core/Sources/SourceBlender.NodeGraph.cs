using System.Text.Json.Nodes;
using zStudio.Core.Gltf;

namespace zStudio.Core.Sources;

public static partial class SourceBlender
{
    private static bool SameMeshInventory(JsonObject before, JsonObject after, GraphMapping mapping)
    {
        if (mapping.Nodes.Values.Where(i => i >= 0).Distinct().Count() != mapping.Nodes.Values.Count(i => i >= 0) ||
            mapping.Meshes.Count != (before["meshes"] as JsonArray ?? []).Count ||
            mapping.Meshes.Values.Any(m => m.Mesh < 0) || mapping.Meshes.Values.Select(m => m.Mesh).Distinct().Count() != mapping.Meshes.Count ||
            (before["materials"] as JsonArray)?.Count != (after["materials"] as JsonArray)?.Count) return false;
        // A new mesh user or a changed assignment may have inherited a different
        // effective morph pose. Export that pose through the geometry path.
        foreach (var (current, original) in mapping.Nodes)
        {
            int? mesh = GltfInteger.OptionalInt32(after["nodes"]![current]?["mesh"], "mesh");
            int? previous = original < 0 ? null : GltfInteger.OptionalInt32(before["nodes"]![original]?["mesh"], "mesh");
            if (mesh is { } index ? original < 0 || previous != mapping.Meshes[index].Mesh : previous != null) return false;
            if (mesh is { } currentMesh && previous is { } oldMesh)
            {
                var beforeMesh = before["meshes"]![oldMesh]!; var afterMesh = after["meshes"]![currentMesh]!;
                var oldWeights = (before["nodes"]![original]?["weights"] ?? beforeMesh["weights"]) as JsonArray;
                var newWeights = (after["nodes"]![current]?["weights"] ?? afterMesh["weights"]) as JsonArray;
                int count = Math.Max((beforeMesh["primitives"]![0]?["targets"] as JsonArray)?.Count ?? 0,
                    (afterMesh["primitives"]![0]?["targets"] as JsonArray)?.Count ?? 0);
                // The importer can split one source mesh by node morph override.
                // Compare the actual float pose as well as its source identity.
                for (int weight = 0; weight < count; weight++)
                    if ((oldWeights != null && weight < oldWeights.Count ? oldWeights[weight]!.GetValue<float>() : 0) !=
                        (newWeights != null && weight < newWeights.Count ? newWeights[weight]!.GetValue<float>() : 0)) return false;
            }
        }
        return true;
    }

    private static JsonObject MergeNodeGraph(GraphModel before, JsonObject after, ref GraphMapping mapping)
    {
        PreserveGraphOrder(before, after, mapping);
        var nodes = after["nodes"] as JsonArray ?? new JsonArray();
        var old = before.Json["nodes"]!.AsArray();
        int scene = GltfInteger.OptionalInt32(after["scene"], "scene") ?? 0;
        var roots = after["scenes"]![scene]!["nodes"] as JsonArray ?? new JsonArray();
        // Existing source record order is retained. New subtrees are spliced at
        // their authored hierarchy position rather than alphabetically appended.
        var origins = mapping.Nodes;
        var ordered = origins.Where(p => p.Value >= 0).OrderBy(p => p.Value).Select(p => p.Key).ToList();
        var traversal = new List<int>(); var visited = new HashSet<int>();
        void Visit(int index)
        {
            if (!visited.Add(index)) return;
            traversal.Add(index);
            foreach (var child in nodes[index]?["children"] as JsonArray ?? []) Visit(GltfInteger.Int32(child, "child"));
        }
        foreach (var root in roots) Visit(GltfInteger.Int32(root, "root"));
        for (int i = traversal.Count - 1; i >= 0; i--)
            if (origins[traversal[i]] < 0)
            {
                int next = i + 1 < traversal.Count ? ordered.IndexOf(traversal[i + 1]) : ordered.Count;
                ordered.Insert(next < 0 ? ordered.Count : next, traversal[i]);
            }
        var remap = ordered.Select((previous, current) => (previous, current)).ToDictionary(p => p.previous, p => p.current);
        JsonArray References(JsonArray values) => new(values.Select(v => (JsonNode?)JsonValue.Create(remap[GltfInteger.Int32(v, "node")])).ToArray());
        var result = before.Json.DeepClone().AsObject();
        foreach (var (current, source) in mapping.Meshes)
            SetRecoil(result["meshes"]![source.Mesh]!.AsObject(), after["meshes"]![current]?["extras"]?["recoil"]);
        foreach (var material in after["materials"] as JsonArray ?? [])
        {
            int origin = material?["extras"]?[BridgeKey]?.GetValue<int>() ?? -1;
            if (origin >= 0) SetRecoil(result["materials"]![origin]!.AsObject(), material?["extras"]?["recoil"]);
        }
        JsonArray output = [];
        foreach (int index in ordered)
        {
            var current = nodes[index]!.AsObject(); int origin = origins[index];
            var merged = origin < 0 ? current.DeepClone().AsObject() : old[origin]!.DeepClone().AsObject();
            // Blender moves node morph overrides to mesh weights. With unchanged
            // geometry keep the original node/mesh split, including overrides.
            foreach (string field in new[] { "name", "matrix", "translation", "rotation", "scale", "extras" })
            { merged.Remove(field); if (current[field] is { } value) merged[field] = value.DeepClone(); }
            merged.Remove("children"); if (current["children"] is JsonArray children) merged["children"] = References(children);
            merged.Remove("mesh"); if (current["mesh"] is { } mesh) merged["mesh"] = mapping.Meshes[GltfInteger.Int32(mesh, "mesh")].Mesh;
            output.Add(merged);
        }
        result["nodes"] = output;
        int oldScene = GltfInteger.OptionalInt32(result["scene"], "scene") ?? 0;
        result["scenes"]![oldScene]!["nodes"] = References(roots);
        result["scenes"]![oldScene]!["extras"] = after["scenes"]![scene]!["extras"]?.DeepClone();
        mapping = mapping with
        {
            Nodes = mapping.Nodes.ToDictionary(p => remap[p.Key], p => p.Value),
            EditorNodes = mapping.EditorNodes.ToDictionary(p => remap[p.Key], p => p.Value),
            CopiedNodes = mapping.CopiedNodes.ToDictionary(p => remap[p.Key], p => p.Value)
        };
        return result;
    }
}
