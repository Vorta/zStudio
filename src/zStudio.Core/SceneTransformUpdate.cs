using System.Text.Json.Nodes;
using zStudio.Core.Worlds;

namespace zStudio.Core;

/// <summary>A prepared replacement of instance transforms with unchanged geometry, appearance and hierarchy.</summary>
public sealed record SceneTransformUpdate(IReadOnlyDictionary<ScenePlacement, ScenePlacement> Placements)
{
    /// <summary>Models whose polygons carry other zone sets (a polygon zone edit); their geometry and appearance are the same.</summary>
    public IReadOnlySet<int> ZoneModels { get; init; } = new HashSet<int>();

    public static SceneTransformUpdate? Prepare(GameScene before, GameScene after, AssetRecord asset, int lod,
        CancellationToken token = default, Func<int, bool?>? visibility = null, Action<string>? refused = null)
    {
        // Reasons are fixed categories plus bounded numeric indices; never expand authored metadata for tracing.
        SceneTransformUpdate? Refuse(string reason) { refused?.Invoke(reason); return null; }
        if (before.Nodes.Count != after.Nodes.Count || before.Models.Count != after.Models.Count || before.Materials.Count != after.Materials.Count || before.Textures.Count != after.Textures.Count) return Refuse("scene collection counts changed");
        for (int i = 0; i < before.Textures.Count; i++) if (!JsonNode.DeepEquals(before.Textures[i], after.Textures[i])) return Refuse($"texture {i} changed");
        for (int i = 0; i < before.Materials.Count; i++) if (!JsonNode.DeepEquals(before.Materials[i], after.Materials[i])) return Refuse($"material {i} changed");
        HashSet<int> zoneModels = [];
        for (int i = 0; i < before.Models.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var a = before.Models[i]; var b = after.Models[i];
            // Incremental source snapshots share immutable geometry; compare rebuilt models in full.
            if (ReferenceEquals(a, b)) continue;
            if (!a.Vertices.AsSpan().SequenceEqual(b.Vertices) || !a.Normals.AsSpan().SequenceEqual(b.Normals) || !a.Morphs.AsSpan().SequenceEqual(b.Morphs)
                || !JsonNode.DeepEquals(a.Metadata, b.Metadata) || a.Polygons.Length != b.Polygons.Length) return Refuse($"model {i} geometry changed");
            for (int p = 0; p < a.Polygons.Length; p++)
            {
                token.ThrowIfCancellationRequested(); var x = a.Polygons[p]; var y = b.Polygons[p];
                if (x.MaterialIndex != y.MaterialIndex || x.Flags != y.Flags || !x.Vertices.AsSpan().SequenceEqual(y.Vertices)
                    || !x.Normals.AsSpan().SequenceEqual(y.Normals) || !x.Uvs.AsSpan().SequenceEqual(y.Uvs) || !x.Colors.AsSpan().SequenceEqual(y.Colors)
                    || !SameExceptZones(x.Metadata, y.Metadata)) return Refuse($"model {i} polygon {p} changed");
                if (!JsonNode.DeepEquals(x.Metadata["zone_set"], y.Metadata["zone_set"])) zoneModels.Add(i);
            }
        }
        LookupWorkBudget hierarchyWork = new(token: token);
        for (int i = 0; i < before.Nodes.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var a = before.Nodes[i]; var b = after.Nodes[i];
            if (a.Index != b.Index || a.Name != b.Name || a.Class != b.Class || a.ModelIndex != b.ModelIndex
                || !a.Parents.AsSpan().SequenceEqual(b.Parents) || !SameTransformChildren(a, b, hierarchyWork)) return Refuse($"node {i} topology changed");
            foreach (string field in new[] { "flags", "field040", "zone_id", "action_priority" })
                if (!JsonNode.DeepEquals(a.Metadata[field], b.Metadata[field])) return Refuse($"node {i} gameplay attributes changed");
            if (a.Data.Count != b.Data.Count) return Refuse($"node {i} data layout changed");
            foreach (var (field, value) in a.Data)
            {
                if (a.Class == "object3d" && field is "rotate" or "scale" or "transform") continue;
                // WorldGltf/WorldUpdate encode identity, authored matrix and TRS cache state in these bits.
                // SceneBuilder evaluates them into the compared placement matrices; appearance bits remain strict.
                if (a.Class == "object3d" && field == "flags"
                    && (a.Data.UInt(field) & ~WorldGltf.ObjectTransformFlags) == (b.Data.UInt(field) & ~WorldGltf.ObjectTransformFlags)) continue;
                if (a.Class == "world" && field == "partitions") continue;
                if (!JsonNode.DeepEquals(value, b.Data[field])) return Refuse($"node {i} non-transform data changed");
            }
        }
        var oldView = SceneBuilder.ForAsset(before, asset, lod, token, visibility);
        var newView = SceneBuilder.ForAsset(after, asset, lod, token, visibility);
        if (oldView.Placements.Count != newView.Placements.Count || !oldView.VisibleNodes.SetEquals(newView.VisibleNodes)) return Refuse("visible scene membership changed");
        // Partition traversal order is derived from position. Pair unique authored occurrences by node/model
        // identity, retaining each mesh's existing instance order (and its picking target) across cell crossings.
        var next = newView.Placements.GroupBy(p => (p.NodeIndex, p.ModelIndex, p.Name)).ToDictionary(g => g.Key, g => g.ToArray());
        Dictionary<ScenePlacement, ScenePlacement> replacements = [];
        foreach (var group in oldView.Placements.GroupBy(p => (p.NodeIndex, p.ModelIndex, p.Name)))
        {
            token.ThrowIfCancellationRequested();
            if (!next.TryGetValue(group.Key, out var rows)) return Refuse("placement identity changed");
            var original = group.ToArray();
            if (original.Length != rows.Length) return Refuse("placement occurrence count changed");
            if (original.Length == 1) { replacements[original[0]] = rows[0]; continue; }
            // A node reached along several paths has no persistent occurrence key here. Only reuse those rows
            // when their complete placement multiset is unchanged; never infer which parent occurrence moved.
            var remaining = rows.GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
            foreach (var row in original)
            {
                token.ThrowIfCancellationRequested();
                if (!remaining.TryGetValue(row, out int count) || count == 0) return Refuse("ambiguous repeated placement moved");
                remaining[row] = count - 1; replacements[row] = row;
            }
        }
        return new(replacements) { ZoneModels = zoneModels };
    }

    private static bool SameExceptZones(JsonObject a, JsonObject b) =>
        a.Count == b.Count && a.All(pair => pair.Key == "zone_set" ? b.ContainsKey(pair.Key) : b.TryGetPropertyValue(pair.Key, out var value) && JsonNode.DeepEquals(pair.Value, value));

    /// <summary>World cells and overflow are spatial storage for the same children, not authored parent changes.</summary>
    internal static bool SameTransformChildren(GameNode before, GameNode after, LookupWorkBudget work)
    {
        if (before.Class != after.Class) return false;
        if (before.Class != "world") return before.Children.AsSpan().SequenceEqual(after.Children);
        // Use the renderer's complete traversal semantics: partition entries are deduplicated against overflow,
        // while an unpartitioned world's repeated children remain repeated. Cell traversal order may change
        // when an object crosses the grid; mission initialization separately verifies order-sensitive lookups.
        Dictionary<int, int> children = [];
        foreach (int child in SceneBuilder.Children(before, work)) children[child] = children.GetValueOrDefault(child) + 1;
        foreach (int child in SceneBuilder.Children(after, work))
        {
            if (!children.TryGetValue(child, out int count)) return false;
            if (count == 1) children.Remove(child); else children[child] = count - 1;
        }
        return children.Count == 0;
    }
}
