using System.IO;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// A later pass that adds objects to the map: aiv (units the mission's AIV resources place), puppies (its pickups) and anim
/// (objects the startup animations place). Its rows are top-level siblings of the world. <see cref="Roots"/> are the objects
/// in the view; <see cref="Entries"/> lists them with the units or pickups only other difficulties place.
/// </summary>
internal sealed record SourceMapPass(string Kind, string Label, string Description, IReadOnlyList<int> Roots, IReadOnlyList<SourceMapPassEntry> Entries);

/// <summary>
/// One unit or pickup of a pass: <paramref name="Row"/> is its object in the view, or null when the shown difficulty does not
/// place it. <paramref name="Difficulties"/> are those whose resources place it (matched across them like placement edits).
/// </summary>
internal sealed record SourceMapPassEntry(int? Row, string Key, string Name, IReadOnlyList<MissionDifficulty> Difficulties, string Detail, string? PickupType = null,
    MissionPickup? Pickup = null, System.Numerics.Vector3? Position = null)
{
    /// <summary>"All" when every difficulty places it, else "E/M", "M/H", "H"…; empty when unknown.</summary>
    internal string Tag => Difficulties.Count > 0 && Difficulties.Distinct().Count() == AllDifficulties ? "All" : string.Join("/", Difficulties.Select(d => d.ToString()[..1]));
    private static readonly int AllDifficulties = Enum.GetValues<MissionDifficulty>().Length;
    /// <summary>A pickup's number among the section's pickups of its catalog type (1, 2…), in list order over every difficulty; 0 for units.</summary>
    internal int Number { get; init; }
    /// <summary>How often its resource lists this placement ("listed twice in aiv_easy.zrd") when more than once, else null; <see cref="Detail"/> ends with it.</summary>
    internal string? Listed { get; init; }
}

/// <summary>Immutable editor projection. Negative row IDs denote source-only groups, never scene slots.</summary>
internal sealed class SourceMapSelection
{
    /// <summary>The player's start under the mission's opening (owner decision 2026-10-23, docs/agents/sources.md): it moves the tank there.</summary>
    internal const string OpeningSetsStart = "The mission's opening animation sets where the player starts and when the controls are released, so its placement can't be edited.";
    /// <summary>The player's start under the mission's opening, which holds the controls without moving the tank (m1).</summary>
    internal const string OpeningHoldsStart = "The mission's opening holds the controls and releases them from this pose, so its placement can't be edited.";
    internal const string HiddenAtStart = "Hidden at the mission's start (a startup animation shows it later), so its placement can't be edited.";
    internal const string MissingTankTemplate = "The tank template is missing or ambiguous, so its placement can't be edited.";
    internal const string HiddenInTree = "Show it in the World tree to edit its placement.";
    internal const string HiddenByIsolation = "Hidden by an isolation; end it (middle-click the isolated row's eye, or Show all) to edit its placement.";
    /// <summary>The later passes, in the order the mission adds them; a source map always has all three (an empty one says so in the tree).</summary>
    internal IReadOnlyList<SourceMapPass> Passes { get; }
    /// <summary>The difficulty the view shows (the aiv and puppies trees list the others' entries dimmed), or null.</summary>
    internal MissionDifficulty? ShownDifficulty { get; }
    private readonly Dictionary<int, SourceMapPass> passOf = [];
    /// <summary>The pass whose top-level row this is, or null for the world's rows.</summary>
    internal SourceMapPass? PassOf(int row) => passOf.GetValueOrDefault(row);
    /// <summary>Resolved initial and indirect associations projected to each authored object, at most eight names.</summary>
    internal IReadOnlyDictionary<int, IReadOnlyList<string>> Animations { get; }
    internal IReadOnlyDictionary<int, MissionAnimationUses> AnimationUses { get; }
    private readonly IReadOnlyList<int>? sourceNodes;
    /// <summary>A template copy the mission makes at run time, which has no source node of its own (a defensive rule for copies outside the aiv and puppies passes).</summary>
    internal bool IsRuntimeCopy(int node) => node >= 0 && sourceNodes != null && node < sourceNodes.Count && sourceNodes[node] != node;
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
        ?? (node >= 0 && node < Scene.Nodes.Count ? Scene.Nodes[node].Class == "world" ? "World" : Scene.Nodes[node].Name : "Source group");
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
        Func<int, SceneTreeIdentity> identity, CancellationToken token, Func<MissionActor, string?>? actorIdentity = null,
        PickupPlacementEditSession? placements = null)
    {
        Scene = scene; sourceNodes = mission?.SourceNodes;
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
            bool repeatedRoot = false;
            // Ambiguous actors must never fall through to a shared template's source edit path. Records that place one node
            // themselves (retail m2 Easy lists bft_00 twice): the first of them owns the node and everything below it, read
            // only, since an edit could not tell which record to change.
            if (actors?.IsAmbiguous(node) == true)
            {
                actor = mission!.RepeatedPlacementAt(scene, node) ?? throw new InvalidDataException("Several mission placements share a node; source-map selection is ambiguous.");
                repeatedRoot = true;
            }
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
                boundaries[owner] = new(owner, actorKey, actor.Name, actor.PlacementSource, false, PlacementReadOnlyReason(actor, repeatedRoot));
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

        // Why a placed object's placement can't be edited (the card, scene_tree readOnlyReason, scene_card begin), or null.
        string? PlacementReadOnlyReason(MissionActor actor, bool repeatedRoot)
        {
            if (repeatedRoot)
                return $"{mission!.Actors.Count(a => a.Root == actor.Root)} records of the shown difficulty place this object, so editing its placement is unavailable.";
            if (actor.Pickup == null && actor.CoordinateSource == null) return "This object's placement is controlled by startup animation. Editing that placement is unavailable.";
            // The opening's hold on the player's start comes before its hiding the tank, which it also causes (m2, m4, m5).
            if (ReferenceEquals(actor, mission!.LocalPlayer) && mission.PlayerStartControl is { } opening) return opening.SetsPose ? OpeningSetsStart : OpeningHoldsStart;
            if (actor.Root >= 0 && actor.Root < scene.Nodes.Count && (scene.Nodes[actor.Root].Metadata.UInt("flags") & 4) == 0)
                return HiddenAtStart;
            if (actor.CoordinateSource is { } record && placements?.Coordinate(record) is { Kind: "tank", MissionSpecific: false } vehicle && (vehicle.TemplateSourceNode == null || vehicle.Difficulties.Count == 0))
                return MissingTankTemplate;
            return null;
        }
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
        Dictionary<int, MissionPickupSource> placedBy = [];
        Dictionary<int, string> pickupTypes = [];
        Dictionary<int, MissionPickup> pickupsAt = [];
        foreach (var actor in mission?.Actors ?? [])
        {
            token.ThrowIfCancellationRequested();
            int row = Owner(actor.Root);
            var pass = actor.Pickup != null ? puppies : actor.CoordinateSource != null ? aiv : actor.PlacementSource == "Animation initialization" ? animated : null;
            if (pass == null || !assigned.Add(row)) continue;
            pass.Add(row);
            if (actor.Pickup is { } pickup) { pickupTypes[row] = pickup.LogicalName; pickupsAt[row] = pickup; }
            if ((actor.Pickup?.Source ?? actor.CoordinateSource) is { } record) placedBy[row] = record;
        }
        foreach (int root in mission?.DormantRoots.Order() ?? Enumerable.Empty<int>())
            if (assigned.Add(Owner(root))) animated.Add(Owner(root));
        List<SourceMapPass> passes = [];
        // The aiv and puppies trees list every difficulty's units and pickups, whichever difficulty the view shows.
        var shown = mission?.Layout is { DifficultyApplies: true } layout ? layout.Difficulty : (MissionDifficulty?)null;
        ShownDifficulty = shown;
        var aivEntries = PassEntries(aiv, false); var pickupEntries = Numbered(PassEntries(puppies, true));
        string shownDifficulty = mission?.Layout.Difficulty.ToString() ?? "the shown difficulty";
        passes.Add(new("aiv", OutlinerKinds.SectionLabel("aiv"),
            $"Units the mission's AIV resources (aiv_easy, aiv, aiv_hard) place: the mission adds them after the world is loaded. The view shows {shownDifficulty}'s; the others are dimmed.", aiv, aivEntries));
        passes.Add(new("puppies", OutlinerKinds.SectionLabel("puppies"),
            $"Pickups the mission's puppies resources (puppies_easy, puppies, puppies_hard) place. The view shows {shownDifficulty}'s; the others are dimmed.", puppies, pickupEntries));
        passes.Add(new("anim", OutlinerKinds.SectionLabel("anim"),
            "Objects the mission's startup animations place, or hide until an animation places them. Their placement comes from the animations.", animated,
            animated.Select(row => new SourceMapPassEntry(row, "", DisplayName(row), [], "")).ToArray()));
        Passes = passes;
        HashSet<int> passRows = [];
        foreach (var pass in passes) foreach (int row in pass.Roots) { passOf[row] = pass; passRows.Add(row); }
        foreach (int parent in children.Keys.ToArray())
            if (children[parent].Any(e => passRows.Contains(e.Node))) children[parent] = children[parent].Where(e => !passRows.Contains(e.Node)).ToArray();
        // Merge by authored occurrence ownership, never by object name. Retain origin before capping details.
        Dictionary<int, MissionAnimationUses> animationUses = [];
        foreach (var (root, uses) in mission?.AnimationUses ?? new Dictionary<int, MissionAnimationUses>())
        {
            if (root < 0 || root >= scene.Nodes.Count) continue;
            token.ThrowIfCancellationRequested();
            int owner = Owner(root);
            animationUses[owner] = animationUses.GetValueOrDefault(owner, MissionAnimationUses.Empty).Merge(uses);
        }
        AnimationUses = animationUses;
        Animations = animationUses.ToDictionary(p => p.Key, p => p.Value.Names);
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

        // A pass's units or pickups with the difficulties placing each, matched across the difficulty resources like placement
        // edits (type or template, original position and rotation). Those the shown difficulty does not place follow as entries
        // without a row. The order is stable across difficulties: by the first difficulty placing each, then its record there.
        // Pickups are numbered per catalog type in list order, over every difficulty, so a number does not change with the
        // shown difficulty. The number is presentation only: rows keep their placement identity.
        List<SourceMapPassEntry> Numbered(List<SourceMapPassEntry> entries)
        {
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            return entries.Select(e => e.PickupType is { } type ? e with { Number = counts[type] = counts.GetValueOrDefault(type) + 1 } : e).ToList();
        }
        SourceMapPassEntry Placed(int row, string key, IReadOnlyList<MissionDifficulty> difficulties, string detail)
        {
            var pickup = pickupsAt.GetValueOrDefault(row);
            return new(row, key, DisplayName(row), difficulties, detail, pickupTypes.GetValueOrDefault(row), pickup, pickup?.Position);
        }
        List<SourceMapPassEntry> PassEntries(List<int> rows, bool pickups)
        {
            if (placements == null || shown is not MissionDifficulty current)
                return rows.Select(row => Placed(row, "", [], "")).ToList();
            List<PassGroup> groups = [];
            Dictionary<MissionPickupSource, PassGroup> groupOf = new(placements.SourceComparer);
            var records = pickups
                ? placements.Records.Select(r => new PassRecord(r.Source, r.Difficulties,
                    MissionPickupType.Catalog.FirstOrDefault(t => t.Name == r.Type)?.TemplateName ?? r.Type, $"{r.Type} pickup · {Position(r.OriginalPosition)}", true, r.Type, r.OriginalPosition))
                : placements.OtherCoordinates.Where(c => c.Kind == "tank").Select(c => new PassRecord(c.Source, c.Difficulties, c.Name,
                    $"{c.Template} · {Position(c.OriginalPosition)} · heading {c.Rotation.Y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}°", c.TemplateSourceNode != null));
            // A resource that lists one placement more than once says so on each of its entries: retail m2 aiv_easy.zrd lists
            // bft_00 twice, which places one unit (two equal pickup records spawn two copies).
            records = records.ToList();
            var listings = records.GroupBy(r => (r.Source.ResourceName, r.Name, r.Detail)).Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.Count());
            records = records.Select(r => listings.TryGetValue((r.Source.ResourceName, r.Name, r.Detail), out int listed)
                && $"listed {(listed == 2 ? "twice" : listed.ToString(System.Globalization.CultureInfo.InvariantCulture) + " times")} in {r.Source.ResourceName.ToLowerInvariant()}" is var note
                ? r with { Detail = r.Detail + " · " + note, Listed = note } : r).ToList();
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                // A resource no difficulty uses (a variant shadows it) places nothing.
                if (record.Difficulties.Count == 0 || groupOf.ContainsKey(record.Source)) continue;
                var scope = placements.Scope(record.Source).Sources;
                var group = scope.Select(s => groupOf.GetValueOrDefault(s)).FirstOrDefault(g => g != null);
                if (group == null) groups.Add(group = new());
                group.Records.Add(record); groupOf[record.Source] = group;
            }
            List<(SourceMapPassEntry Entry, (int, int, int) Order)> entries = [];
            int ordinal = 0;
            foreach (int row in rows)
            {
                var group = placedBy.TryGetValue(row, out var placed) ? groupOf.GetValueOrDefault(placed) : null;
                if (group is { Row: null }) { group.Row = row; continue; }
                entries.Add((Placed(row, "", [current], ""), ((int)current, placed?.RecordIndex ?? int.MaxValue, ordinal++)));
            }
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                var difficulties = group.Records.SelectMany(r => r.Difficulties).Distinct().Order().ToArray();
                var first = group.Records.MinBy(r => ((int)r.Difficulties.Min(), r.Source.RecordIndex))!;
                var order = ((int)first.Difficulties.Min(), first.Source.RecordIndex, ordinal++);
                // The presentation key is unique within its resource only: the resource name tells difficulty files apart.
                string key = first.Source.ResourceName + ":" + (placements.SourcePresentationKey(first.Source) ?? "#" + first.Source.RecordIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                // A record the shown difficulty places that the view could not show (its notices say why) stays out, as does
                // one whose template the world lacks: the game skips both.
                if (group.Row is int row) entries.Add((Placed(row, key, difficulties, first.Detail) with { Listed = first.Listed }, order));
                else if (!difficulties.Contains(current) && group.Records.Any(r => r.Placeable))
                    entries.Add((new(null, key, first.Name, difficulties, first.Detail, first.PickupType, Position: first.Position) { Listed = first.Listed }, order));
            }
            return entries.OrderBy(e => e.Order).Select(e => e.Entry).ToList();
        }
        static string Position(System.Numerics.Vector3 p) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"X {p.X:0.#}, Y {p.Y:0.#}, Z {p.Z:0.#}");
    }

    private sealed record PassRecord(MissionPickupSource Source, IReadOnlyList<MissionDifficulty> Difficulties, string Name, string Detail, bool Placeable, string? PickupType = null,
        System.Numerics.Vector3? Position = null, string? Listed = null);
    private sealed class PassGroup
    {
        internal List<PassRecord> Records { get; } = [];
        internal int? Row { get; set; }
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
            SourceMapActorKeys.Prepare(buildFolder, source, edits, token, doc.SourceBuild.ReadHashes), edits), token);
        token.ThrowIfCancellationRequested();
        if (!cache.TryGetValue(data, out _)) cache.Add(data, projection);
    }
    private SourceMapSelection? SourceSelection(DocumentModel? doc, GameScene? data)
        => doc != null && data != null && sourceSelections.TryGetValue(doc, out var cache) && cache.TryGetValue(data, out var found) ? found : null;
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
        // A placed unit's source refusal names what it is, and why its placement can't be edited either when it is locked.
        if (doc == shownDocument && scene?.Mission?.InspectedActorAt(node) is { } actor)
            throw new StudioCommandException("read_only", actor.Pickup == null && actor.CoordinateSource == null
                ? "This object's placement is controlled by startup animation. Editing that placement is unavailable."
                : projection?.Boundaries.GetValueOrDefault(node)?.ReadOnlyReason is { } locked
                ? "This is a resource-placed instance, so its shared model source isn't edited here. " + locked
                : "This is a resource-placed instance. Edit its mission placement instead of its shared model source.");
        if (projection?.Boundaries.GetValueOrDefault(node)?.ReadOnlyReason is { } reason)
            throw new StudioCommandException("read_only", reason);
        if (node < 0) throw new StudioCommandException("read_only", "This reference group has no compiled node. Its parts cannot be edited independently.");
        return node;
    }
}
