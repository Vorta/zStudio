using System.Text.Json.Nodes;
using zStudio.Core.Animation;
using zStudio.Core.Worlds;

namespace zStudio.Core;

/// <summary>Dependencies observed during the actual initialization frontier, bounded by scene-node identity.</summary>
internal sealed class MissionInitializationTransforms
{
    internal HashSet<int> Reads { get; } = [];
    internal HashSet<int> Writes { get; } = [];
    internal HashSet<(int Root, string Name, int Limit, int Result)> OrderedLookups { get; } = [];
}

public sealed partial class MissionSceneContext
{
    internal MissionInitializationTransforms? InitializationTransforms { get; init; }

    /// <summary>
    /// Rebase an initialized mission onto a source-only transform change without replaying initialization.
    /// Returns null if a changed transform influenced initialization, is initialization-owned, or has ambiguous occurrences.
    /// The source snapshots and this published baseline remain untouched; immutable geometry and unchanged nodes are shared.
    /// </summary>
    public MissionSceneContext? WithSourceTransforms(GameScene beforeSource, GameScene afterSource, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (InitializationTransforms is not { } dependencies || beforeSource.Nodes.Count != originalNodeCount
            || afterSource.Nodes.Count != originalNodeCount || Scene.Nodes.Count < originalNodeCount
            || SourceNodes.Count != Scene.Nodes.Count || !SameGeometry(beforeSource, afterSource)) return null;
        HashSet<int> changed = []; LookupWorkBudget hierarchyWork = new(token: token);
        for (int i = 0; i < originalNodeCount; i++)
        {
            token.ThrowIfCancellationRequested();
            var before = beforeSource.Nodes[i]; var after = afterSource.Nodes[i];
            if (before.Index != after.Index || before.Name != after.Name || before.Class != after.Class || before.ModelIndex != after.ModelIndex
                || !before.Parents.AsSpan().SequenceEqual(after.Parents) || !SceneTransformUpdate.SameTransformChildren(before, after, hierarchyWork)
                || !SameFields(before.Data, after.Data, field => TransformField(before, field) || before.Class == "world" && field == "partitions")
                || !SameFields(before.Metadata, after.Metadata, field => DerivedMetadata(field) || field == "data" || before.Class == "world" && field is "child_count" or "child_indices")) return null;
            if (SceneBuilder.LocalTransform(before) != SceneBuilder.LocalTransform(after))
            {
                var initialized = Scene.Nodes[i];
                if (before.Class != "object3d" || initialized.Name != before.Name || initialized.Class != before.Class
                    || !initialized.Parents.AsSpan().SequenceEqual(before.Parents) || !initialized.Children.AsSpan().SequenceEqual(before.Children)
                    || SourceNodes[i] != i || dependencies.Reads.Contains(i) || dependencies.Writes.Contains(i)
                    || DormantRoots.Contains(i) || ActorAt(i) is { Root: var actorRoot } actor && actorRoot == i && (actor.PlacementPosition.HasValue || actor.CoordinateSource != null || actor.Pickup != null)
                    || SceneBuilder.LocalTransform(initialized) != SceneBuilder.LocalTransform(before)) return null;
                changed.Add(i);
            }
        }
        // A template edit would also change descendants copied into mission actors. The source-transform path deliberately
        // does not guess whether each clone's local component survived placement/initialization.
        var affected = new HashSet<int>(); Stack<int> pending = new(changed);
        while (pending.TryPop(out int index))
        {
            token.ThrowIfCancellationRequested();
            if (index < 0 || index >= originalNodeCount || !affected.Add(index)) continue;
            if (beforeSource.Nodes[index].Parents.Length > 1) return null;
            foreach (int child in SceneBuilder.Children(beforeSource.Nodes[index])) pending.Push(child);
        }
        for (int i = originalNodeCount; i < SourceNodes.Count; i++)
        { token.ThrowIfCancellationRequested(); if (affected.Contains(SourceNodes[i])) return null; }

        GameScene scene = new(); scene.Models.AddRange(Scene.Models); scene.Materials.AddRange(Scene.Materials); scene.Textures.AddRange(Scene.Textures);
        for (int i = 0; i < Scene.Nodes.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var current = Scene.Nodes[i];
            if (i >= originalNodeCount) { scene.Nodes.Add(current); continue; }
            var before = beforeSource.Nodes[i]; var after = afterSource.Nodes[i];
            JsonObject? data = null, metadata = null; int[]? children = null;
            if (changed.Contains(i))
            {
                data = (JsonObject)JsonData.Clone(current.Data, token)!;
                foreach (string field in new[] { "transform", "rotate", "scale", "translate" }) Copy(data, after.Data, field, token);
                data["flags"] = (current.Data.UInt("flags") & ~WorldGltf.ObjectTransformFlags) | (after.Data.UInt("flags") & WorldGltf.ObjectTransformFlags);
            }
            if (before.Class == "world")
            {
                // Cells and overflow are two storage locations for the same authored world edges. Mission placement
                // appends actor roots to explicit children; preserve those additions when source overflow membership changes.
                if (!JsonNode.DeepEquals(before.Data["partitions"], current.Data["partitions"])) return null;
                if (!before.Children.AsSpan().SequenceEqual(after.Children))
                {
                    hierarchyWork.Reserve((long)before.Children.Length + after.Children.Length + current.Children.Length);
                    var authored = before.Children.ToHashSet();
                    if (!authored.IsSubsetOf(current.Children)) return null;
                    var retained = current.Children.Where(child => !authored.Contains(child)).ToArray();
                    // A resource can attach an already-overflowing authored root without leaving a second explicit edge.
                    // If that same root enters a cell, rerun placement rather than guessing its attachment order.
                    var nextAuthored = after.Children.ToHashSet();
                    hierarchyWork.Reserve(Actors.Count);
                    if (Actors.Any(actor => authored.Contains(actor.Root) && !nextAuthored.Contains(actor.Root)
                        && (actor.PlacementPosition.HasValue || actor.Pickup != null))) return null;
                    children = [.. after.Children, .. retained];
                    metadata ??= (JsonObject)JsonData.Clone(current.Metadata, token)!;
                    Copy(metadata, after.Metadata, "child_count", token); Copy(metadata, after.Metadata, "child_indices", token);
                }
                if (!JsonNode.DeepEquals(before.Data["partitions"], after.Data["partitions"]))
                { data ??= (JsonObject)JsonData.Clone(current.Data, token)!; Copy(data, after.Data, "partitions", token); }
            }
            foreach (string field in BoundsFields)
                if (!JsonNode.DeepEquals(before.Metadata[field], after.Metadata[field]))
                { metadata ??= (JsonObject)JsonData.Clone(current.Metadata, token)!; Copy(metadata, after.Metadata, field, token); }
            if (data != null && current.Metadata.ContainsKey("data"))
            { metadata ??= (JsonObject)JsonData.Clone(current.Metadata, token)!; metadata["data"] = JsonData.Clone(data, token); }
            scene.Nodes.Add(data == null && metadata == null && children == null ? current : current with { Data = data ?? current.Data, Metadata = metadata ?? current.Metadata, Children = children ?? current.Children });
        }
        if (dependencies.OrderedLookups.Count > 0)
        {
            AnimationPreviewContext lookupContext = new() { World = new SceneDocument("", scene), Package = new() { Prefix = [], Tail = [] } };
            AnimationBindingOperation lookup = new(lookupContext, token);
            foreach (var query in dependencies.OrderedLookups)
                if (lookup.First(query.Root, query.Name, query.Limit) != query.Result) return null;
        }
        return new(scene, SourceNodes.ToList(), Actors.ToList(), new(DormantRoots), Diagnostics, Layout, originalNodeCount, token)
        { AiNetworks = AiNetworks, InitializationTransforms = dependencies, AnimationRoots = AnimationRoots, AnimationEntryRoots = AnimationEntryRoots, AnimationUses = AnimationUses };
    }

    /// <summary>
    /// Rebase an initialized mission onto a source update that replaced only built models' polygon zones (see
    /// SourceWorlds.TryUpdatePolygonZones): nodes, materials and textures are the same objects, so initialization, which
    /// reads no polygon zone, stands. Null when the update replaced anything else or the mission does not show those models.
    /// </summary>
    public MissionSceneContext? WithSourcePolygonZones(GameScene beforeSource, GameScene afterSource, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (beforeSource.Nodes.Count != afterSource.Nodes.Count || beforeSource.Models.Count != afterSource.Models.Count
            || Scene.Models.Count < beforeSource.Models.Count || beforeSource.Materials.Count != afterSource.Materials.Count || beforeSource.Textures.Count != afterSource.Textures.Count
            || !beforeSource.Nodes.Zip(afterSource.Nodes).All(pair => ReferenceEquals(pair.First, pair.Second))
            || !beforeSource.Materials.Zip(afterSource.Materials).All(pair => ReferenceEquals(pair.First, pair.Second))
            || !beforeSource.Textures.Zip(afterSource.Textures).All(pair => ReferenceEquals(pair.First, pair.Second))) return null;
        GameScene scene = new(); scene.Models.AddRange(Scene.Models); scene.Materials.AddRange(Scene.Materials); scene.Textures.AddRange(Scene.Textures); scene.Nodes.AddRange(Scene.Nodes);
        for (int i = 0; i < beforeSource.Models.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(beforeSource.Models[i], afterSource.Models[i])) continue;
            var (a, b) = (beforeSource.Models[i], afterSource.Models[i]);
            if (!ReferenceEquals(Scene.Models[i], a) || a.Polygons.Length != b.Polygons.Length || !ReferenceEquals(a.Vertices, b.Vertices)) return null;
            scene.Models[i] = b;
        }
        return new(scene, SourceNodes.ToList(), Actors.ToList(), new(DormantRoots), Diagnostics, Layout, originalNodeCount, token)
        { AiNetworks = AiNetworks, InitializationTransforms = InitializationTransforms, AnimationRoots = AnimationRoots, AnimationEntryRoots = AnimationEntryRoots, AnimationUses = AnimationUses };
    }

    private static readonly string[] BoundsFields =["node_bbox", "model_bbox", "child_bbox", "area_partition", "update_flags"];
    private static bool DerivedMetadata(string field) => field is "node_bbox" or "model_bbox" or "child_bbox" or "area_partition" or "update_flags";
    private static bool TransformField(GameNode node, string field) => node.Class == "object3d" && field is "transform" or "rotate" or "scale" or "translate" or "flags";
    private static bool SameFields(JsonObject before, JsonObject after, Func<string, bool> ignored)
    {
        foreach (var (field, value) in before)
            if (!ignored(field) && (!after.ContainsKey(field) || !JsonNode.DeepEquals(value, after[field]))) return false;
        foreach (string field in after.Select(pair => pair.Key)) if (!ignored(field) && !before.ContainsKey(field)) return false;
        // Appearance bits are not transform cache flags.
        if (ignored("flags") && (before.UInt("flags") & ~WorldGltf.ObjectTransformFlags) != (after.UInt("flags") & ~WorldGltf.ObjectTransformFlags)) return false;
        return true;
    }
    private static bool SameGeometry(GameScene before, GameScene after)
        => before.Models.Count == after.Models.Count && before.Materials.Count == after.Materials.Count && before.Textures.Count == after.Textures.Count
        && before.Models.Zip(after.Models).All(pair => ReferenceEquals(pair.First, pair.Second))
        && before.Materials.Zip(after.Materials).All(pair => ReferenceEquals(pair.First, pair.Second))
        && before.Textures.Zip(after.Textures).All(pair => ReferenceEquals(pair.First, pair.Second));
    private static void Copy(JsonObject target, JsonObject source, string field, CancellationToken token)
    { if (source.TryGetPropertyValue(field, out var value)) target[field] = JsonData.Clone(value, token); else target.Remove(field); }
}
