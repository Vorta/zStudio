using System.IO;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// A later pass that adds objects to the map: aiv (units the mission's AIV resource places for the selected difficulty),
/// puppies (its pickups) and anim (objects the startup animations place). Its rows are top-level siblings of the world.
/// </summary>
internal sealed record SourceMapPass(string Kind, string Label, string Description, IReadOnlyList<int> Roots);

/// <summary>Immutable editor projection. Negative row IDs denote source-only groups, never scene slots.</summary>
internal sealed class SourceMapSelection
{
    /// <summary>The later passes, in the order the mission adds them; empty passes are omitted.</summary>
    internal IReadOnlyList<SourceMapPass> Passes { get; }
    private readonly Dictionary<int, SourceMapPass> passOf = [];
    /// <summary>The pass whose top-level row this is, or null for the world's rows.</summary>
    internal SourceMapPass? PassOf(int row) => passOf.GetValueOrDefault(row);
    /// <summary>The mission animations bound to each object (the game binds them by name), at most eight per object.</summary>
    internal IReadOnlyDictionary<int, IReadOnlyList<string>> Animations { get; }
    internal GameScene Scene { get; }
    internal IReadOnlyDictionary<int, int> Owners { get; }
    internal IReadOnlyDictionary<int, SourceSelectionBoundary> Boundaries { get; }
    internal IReadOnlyDictionary<int, string> Identities { get; }
    internal IReadOnlyDictionary<int, string> PartIdentities { get; }
    internal IReadOnlyDictionary<int, int[]> MemberSlots { get; }
    internal IReadOnlyDictionary<int, SourceSelectionNode> AuthoredNodes { get; }
    internal IReadOnlyList<int> Roots { get; }
    private readonly Dictionary<int, SceneHierarchyEdge[]> children;
    private readonly Dictionary<int, int> predecessor;
    private readonly Dictionary<int, int> parentCounts;
    internal int Owner(int node) => Owners.GetValueOrDefault(node, node);
    internal bool IsBoundary(int node) => Boundaries.ContainsKey(node);
    internal bool IsSourceGroup(int node) => node < 0 && AuthoredNodes.ContainsKey(node) && !IsBoundary(node);
    internal int? Parent(int node) => predecessor.TryGetValue(Owner(node), out int parent) && parent != Owner(node) ? parent : null;
    internal int ParentCount(int node) => parentCounts.GetValueOrDefault(node);
    internal string DisplayName(int node) => AuthoredNodes.GetValueOrDefault(node)?.Name ?? Boundaries.GetValueOrDefault(node)?.Name
        ?? (node >= 0 && node < Scene.Nodes.Count ? Scene.Nodes[node].Class == "world" ? "world" : Scene.Nodes[node].Name : "Source group");
    internal IReadOnlyList<SceneHierarchyEdge> Children(int node) => children.GetValueOrDefault(node) ?? [];
    internal IReadOnlyList<int> Path(int node)
    {
        node = Owner(node);
        if (!predecessor.ContainsKey(node)) return [];
        List<int> path = [];
        for (int current = node, depth = 0; depth++ <= SceneHierarchy.MaximumDepth;)
        {
            path.Add(current);
            if (!predecessor.TryGetValue(current, out int parent) || parent == current) break;
            current = parent;
        }
        path.Reverse(); return path;
    }

    internal SourceMapSelection(GameScene scene, MissionSceneContext? mission, SourceSelectionIndex source,
        Func<int, SceneTreeIdentity> identity, CancellationToken token, Func<MissionActor, string?>? actorIdentity = null)
    {
        Scene = scene;
        if (scene.Nodes.Count > 200_000) throw new InvalidDataException("The source map has too many nodes to prepare selection.");
        var actors = mission?.PrepareActorIndex(scene, token);
        Dictionary<int, int> owners = [];
        Dictionary<int, string> identities = [];
        Dictionary<int, SourceSelectionBoundary> boundaries = new(source.Boundaries);
        Dictionary<int, string> parts = [];
        Dictionary<string, List<int>> partUses = new(StringComparer.Ordinal);
        Dictionary<int, List<int>> members = [];
        Dictionary<int, int> sourceRows = [];
        Dictionary<MissionActor, string> unretainedActors = new(ReferenceEqualityComparer.Instance);
        for (int node = 0; node < scene.Nodes.Count; node++)
        {
            token.ThrowIfCancellationRequested();
            int sourceNode = mission != null && node < mission.SourceNodes.Count ? mission.SourceNodes[node] : node;
            var actor = actors?.At(node);
            // Ambiguous actors must never fall through to a shared template's source edit path.
            if (actors?.IsAmbiguous(node) == true) throw new InvalidDataException("Several mission placements share a node; source-map selection is ambiguous.");
            int owner = actor?.Root ?? source.Owners.GetValueOrDefault(sourceNode, node);
            owners[node] = owner;
            if (actor == null) sourceRows.TryAdd(source.Owners.GetValueOrDefault(sourceNode, sourceNode), owner);
            if (!members.TryGetValue(owner, out var list)) members[owner] = list = [];
            list.Add(node);
            string? physical = source.Identities.GetValueOrDefault(sourceNode);
            bool retainPart = physical != null;
            physical ??= identity(node).Key;
            if (actor != null)
            {
                // Source-map builds live in different temporary folders and may reuse
                // compiled slots. Only source-relative placement + template provenance
                // may carry editor state between those builds.
                string? stableActor = actorIdentity?.Invoke(actor);
                retainPart &= stableActor != null;
                if (stableActor == null && !unretainedActors.ContainsKey(actor)) unretainedActors[actor] = "unretained:" + Guid.NewGuid().ToString("N");
                string actorKey = stableActor ?? unretainedActors[actor];
                boundaries[owner] = new(owner, actorKey, actor.Name, actor.PlacementSource, false,
                    actor.Pickup == null && actor.CoordinateSource == null ? "This object's placement is controlled by startup animation. Editing that placement is unavailable." : null);
                identities[owner] = actorKey;
                physical = actorKey + ":" + physical;
            }
            else identities[owner] = source.Identities.GetValueOrDefault(owner)
                ?? (owner >= 0 ? identity(owner).Key : throw new InvalidDataException("A source-only reference group has no verified identity."));
            if (actor == null || retainPart)
            {
                if (!partUses.TryGetValue(physical, out var uses)) partUses[physical] = uses = [];
                uses.Add(node);
            }
        }
        foreach (var (key, nodes) in partUses)
            if (nodes.Count == 1) parts[nodes[0]] = key;
        Owners = owners; Boundaries = boundaries; Identities = identities; PartIdentities = parts;
        MemberSlots = members.ToDictionary(p => p.Key, p => p.Value.ToArray());

        Dictionary<int, SourceSelectionNode> authoredNodes = [];
        foreach (var (sourceRow, node) in source.AuthoredNodes)
        {
            token.ThrowIfCancellationRequested();
            int row;
            if (sourceRow < 0) row = sourceRow;
            else if (!sourceRows.TryGetValue(sourceRow, out row)) continue;
            authoredNodes[row] = node with { Id = row };
            identities.TryAdd(row, node.Key);
        }
        AuthoredNodes = authoredNodes;

        var hierarchy = new SceneHierarchy(scene, token);
        children = [];
        foreach (var node in scene.Nodes)
        {
            token.ThrowIfCancellationRequested();
            if (Owner(node.Index) != node.Index || boundaries.ContainsKey(node.Index)) continue;
            HashSet<int> seen = [];
            children[node.Index] = hierarchy.Children(node.Index).Select(e => new SceneHierarchyEdge(Owner(e.Node), e.Kind))
                .Where(e => e.Node != node.Index && seen.Add(e.Node)).ToArray();
        }
        // Replace compiler-flattened attachments with the frozen authored graph. The remaining edges
        // still describe script-created groups and runtime/resource placements, using their existing identities.
        Dictionary<int, SceneHierarchyEdge[]> authoredChildren = [];
        HashSet<int> authoredAttached = [];
        foreach (var (sourceParent, sourceChildren) in source.AuthoredChildren)
        {
            token.ThrowIfCancellationRequested();
            int parent;
            if (sourceParent < 0) parent = sourceParent;
            else if (!sourceRows.TryGetValue(sourceParent, out parent)) continue;
            if (boundaries.ContainsKey(parent)) continue;
            List<SceneHierarchyEdge> edges = [];
            foreach (int sourceChild in sourceChildren)
            {
                int child;
                if (sourceChild < 0) child = sourceChild;
                else if (!sourceRows.TryGetValue(sourceChild, out child)) continue;
                if (!authoredNodes.ContainsKey(child) || child == parent) continue;
                edges.Add(new(child, "authored child")); authoredAttached.Add(child);
            }
            authoredChildren[parent] = edges.ToArray();
        }
        HashSet<(int Parent, int Child)> flattened = [];
        foreach (var edge in source.FlattenedEdges)
        {
            token.ThrowIfCancellationRequested();
            if (sourceRows.TryGetValue(edge.Parent, out int parent) && sourceRows.TryGetValue(edge.Child, out int child)) flattened.Add((parent, child));
        }
        foreach (int parent in children.Keys.ToArray())
        {
            token.ThrowIfCancellationRequested();
            var authoredAtParent = (authoredChildren.GetValueOrDefault(parent) ?? []).Select(e => e.Node).ToHashSet();
            children[parent] = children[parent].Where(e => !authoredAtParent.Contains(e.Node)
                && (!authoredAttached.Contains(e.Node) || !flattened.Contains((parent, e.Node)))).ToArray();
        }
        foreach (var (parent, edges) in authoredChildren) children[parent] = [.. edges, .. children.GetValueOrDefault(parent) ?? []];
        // Objects later passes add or place leave the world's rows for their pass: the world shows what the map itself holds.
        List<int> aiv = [], puppies = [], animated = [];
        HashSet<int> assigned = [];
        foreach (var actor in mission?.Actors ?? [])
        {
            token.ThrowIfCancellationRequested();
            int row = Owner(actor.Root);
            var pass = actor.Pickup != null ? puppies : actor.CoordinateSource != null ? aiv : actor.PlacementSource == "Animation initialization" ? animated : null;
            if (pass != null && assigned.Add(row)) pass.Add(row);
        }
        foreach (int root in mission?.DormantRoots.Order() ?? Enumerable.Empty<int>())
            if (assigned.Add(Owner(root))) animated.Add(Owner(root));
        List<SourceMapPass> passes = [];
        string Short(string resource) => System.IO.Path.GetFileNameWithoutExtension(resource);
        if (aiv.Count > 0) passes.Add(new("aiv", mission!.Layout.AivResource is { } aivResource ? Short(aivResource) : "aiv",
            $"Units {mission.Layout.AivResource} places for {mission.Layout.Difficulty}: the mission adds them after the world is loaded.", aiv));
        if (puppies.Count > 0) passes.Add(new("puppies", Short(mission!.Layout.PickupResource),
            $"Pickups {mission.Layout.PickupResource} places for {mission.Layout.Difficulty}.", puppies));
        if (animated.Count > 0) passes.Add(new("anim", "anim",
            "Objects the mission's startup animations place, or hide until an animation places them. Their placement comes from the animations.", animated));
        Passes = passes;
        HashSet<int> passRows = [];
        foreach (var pass in passes) foreach (int row in pass.Roots) { passOf[row] = pass; passRows.Add(row); }
        foreach (int parent in children.Keys.ToArray())
            if (children[parent].Any(e => passRows.Contains(e.Node))) children[parent] = children[parent].Where(e => !passRows.Contains(e.Node)).ToArray();
        // An object's rows merge the animations of every node it owns (eight names, then "…").
        Dictionary<int, List<string>> animations = [];
        foreach (var (root, names) in mission?.AnimationRoots ?? new Dictionary<int, IReadOnlyList<string>>())
        {
            if (root < 0 || root >= scene.Nodes.Count) continue;
            if (!animations.TryGetValue(Owner(root), out var merged)) animations[Owner(root)] = merged = [];
            foreach (string name in names)
                if (merged.Count < 8 && name != "…" && !merged.Contains(name)) merged.Add(name);
                else if (merged.Count == 8 && (name == "…" || !merged.Contains(name))) { merged.Add("…"); break; }
        }
        Animations = animations.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value);
        parentCounts = [];
        foreach (var edges in children.Values)
            foreach (var edge in edges) parentCounts[edge.Node] = parentCounts.GetValueOrDefault(edge.Node) + 1;
        // The actual world root remains the root. Source-only reference groups replace their constituent siblings.
        Roots = hierarchy.Roots.Select(Owner).Distinct().ToArray();
        predecessor = [];
        Queue<int> pending = new();
        foreach (int root in Roots.Concat(passRows)) if (predecessor.TryAdd(root, root)) pending.Enqueue(root);
        while (pending.TryDequeue(out int node))
        {
            token.ThrowIfCancellationRequested();
            foreach (var child in Children(node)) if (predecessor.TryAdd(child.Node, node)) pending.Enqueue(child.Node);
        }
    }
}

public partial class MainWindow
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentModel, System.Runtime.CompilerServices.ConditionalWeakTable<GameScene, SourceMapSelection>> sourceSelections = new();
    private async Task PrepareSourceMapSelectionAsync(DocumentModel doc, GameScene data, MissionSceneContext? mission, CancellationToken token)
    {
        if (doc.SourceWorld == null) return;
        var cache = sourceSelections.GetOrCreateValue(doc);
        if (cache.TryGetValue(data, out _)) return;
        var source = SourceWorldModel(doc).Selection;
        var edits = doc.PickupEdits;
        string buildFolder = doc.SourceBuild!.Root;
        var projection = await Task.Run(() => new SourceMapSelection(data, mission, source, TreeIdentities(data, mission, edits), token,
            SourceMapActorKeys.Prepare(buildFolder, source, edits, token, doc.SourceBuild.ReadHashes)), token);
        token.ThrowIfCancellationRequested();
        if (!cache.TryGetValue(data, out _)) cache.Add(data, projection);
    }
    private SourceMapSelection? SourceSelection(DocumentModel? doc, GameScene? data)
        => doc != null && data != null && sourceSelections.TryGetValue(doc, out var cache) && cache.TryGetValue(data, out var found) ? found : null;
    private IReadOnlyDictionary<int, int> SourceSelectionOwners(DocumentModel doc, GameScene data, MissionSceneContext? mission)
        => SourceSelection(doc, data)?.Owners ?? throw new StudioCommandException("not_ready", "The source map's selection projection is not prepared.");
    private IReadOnlyDictionary<int, string> SourceSelectionPartIdentities(DocumentModel doc, GameScene data, MissionSceneContext? mission)
        => SourceSelection(doc, data)?.PartIdentities ?? throw new StudioCommandException("not_ready", "The source map's part identities are not prepared.");
    private int SourceSelectionOwner(DocumentModel doc, int node)
    {
        if (doc.SourceWorld == null) return node;
        var projection = SourceSelection(doc, doc == shownDocument ? scene?.PreviewScene : null);
        return projection?.Owner(node) ?? SourceWorldModel(doc).Selection.Owners.GetValueOrDefault(node, node);
    }
    private int EditableSourceSelectionNode(DocumentModel doc, int node)
    {
        node = SourceSelectionOwner(doc, node);
        var projection = SourceSelection(doc, doc == shownDocument ? scene?.PreviewScene : null);
        if (projection?.Boundaries.GetValueOrDefault(node)?.ReadOnlyReason is { } reason)
            throw new StudioCommandException("read_only", reason);
        if (node < 0) throw new StudioCommandException("read_only", "This reference group has no compiled node. Its parts cannot be edited independently.");
        if (doc == shownDocument && scene?.Mission?.ActorAt(node) is { } actor)
            throw new StudioCommandException("read_only", actor.Pickup != null || actor.CoordinateSource != null
                ? "This is a resource-placed instance. Edit its mission placement instead of its shared model source."
                : "This object's placement is controlled by startup animation. Editing that placement is unavailable.");
        return node;
    }
}
