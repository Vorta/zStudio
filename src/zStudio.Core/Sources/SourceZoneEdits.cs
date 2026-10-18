using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>
/// An exact source-world node, optionally one polygon of its source mesh: the polygon's ordinal in the mesh's embedded zone profile,
/// as <see cref="SourceZoneTarget.Polygon"/> returns it. It is not a built model's polygon index, which differs once a build
/// splits or discards source polygons; <see cref="SourceZoneEdits.FromCompiled"/> maps a picked built polygon to it.
/// </summary>
public sealed record SourceZoneSelection(int SceneNode, int? Polygon = null);
public enum SourceZoneTargetKind { Node, LoadRoot, Polygon }
/// <summary>Index is the source node or mesh ordinal; a load root has index -1.</summary>
public sealed record SourceZoneTarget(string Asset, SourceZoneTargetKind Kind, int Index, int Polygon = -1,
    string? OwnerPath = null, IReadOnlyList<int>? OwnerReferences = null);
public sealed record SourceZoneTargetState(SourceZoneTarget Target, uint Word, bool? Gate, int AffectedCount);
/// <summary>
/// Null leaves that property alone. An empty polygon list explicitly authors no zones; 255 means Any. <paramref name="Inherit"/>
/// makes an object node take its parent's zone again (keeping its opaque upper bytes); a load root has no parent to inherit from.
/// </summary>
public sealed record SourceZoneAssignment(byte? NodeZone = null, bool? Gate = null, IReadOnlyList<byte>? PolygonZones = null, bool Inherit = false);
public sealed record SourceZoneInspection(string MasterPath, IReadOnlyList<string> SourcePaths, IReadOnlyList<SourceZoneTargetState> Targets,
    int TargetCount, int Offset, int? NextOffset, IReadOnlyList<int> AffectedNodes, int AffectedCount, bool RequiresSharedScope,
    IReadOnlyList<string> AffectedMissions, int AffectedMissionCount, IReadOnlyList<string> AffectedSources, int AffectedSourceCount);
public sealed record SourceZoneCatalogEntry(byte Id, string Label, int NodeUses, int PolygonUses, bool Authored);
public sealed record SourceZoneCatalog(string MasterPath, IReadOnlyList<SourceZoneCatalogEntry> Labels);
/// <summary>Apply Changes once through the prepared SourceWorkspace transaction; planning never accepts or saves an edit.</summary>
public sealed record SourceZoneEditPlan(string Label, IReadOnlyList<(string Relative, byte[] Content)> Changes,
    IReadOnlyList<int> AffectedNodes, int AffectedCount, bool SharedScope, IReadOnlyList<string>? AffectedMissions = null,
    int AffectedMissionCount = 0, IReadOnlyList<string>? AffectedSources = null, int AffectedSourceCount = 0);

/// <summary>One map's embedded zone catalog and exact source-occurrence assignments, shared by GUI and automation.</summary>
public static partial class SourceZoneEdits
{
    public const int MaximumSelections = 4096, MaximumSelectedAssets = 64, MaximumPage = 32, MaximumAffectedShown = 256, MaximumAffectedPathsShown = 32;

    public static SourceZoneCatalog Catalog(SourceWorkspace workspace, string mission, CancellationToken token = default)
    {
        SourceEmbeddedZones sources = new(workspace, token);
        var missionSources = sources.ReadMission(mission);
        int[] nodes = new int[256], polygons = new int[256];
        CountUses(missionSources.Entries.Select(e => e.Profile), nodes, polygons, token);
        var names = missionSources.Labels.ToDictionary(x => x.Id);
        List<SourceZoneCatalogEntry> entries = [];
        for (int i = 0; i < 256; i++)
            if (names.TryGetValue((byte)i, out var label) || nodes[i] != 0 || polygons[i] != 0 || i == 255)
                entries.Add(new((byte)i, i == 255 ? "Any" : label?.Label ?? $"Zone {i}", nodes[i], polygons[i], label != null));
        return new(missionSources.MasterPath, entries);
    }

    /// <summary>
    /// The selection a viewport pick makes: a polygon of the node's built model becomes the source polygon it was built from
    /// (the pieces a build splits one source polygon into select it together).
    /// </summary>
    public static SourceZoneSelection FromCompiled(SourceSceneState build, int sceneNode, int? compiledPolygon)
    {
        if (compiledPolygon is not { } polygon) return new(sceneNode);
        if (!build.Provenance.TryGetValue(sceneNode, out var origin) || origin.ZonePolygons is not { } mapping || polygon < 0 || polygon >= mapping.Count)
            throw Bad("The selected face has no source polygon identity. Rebuild the world before editing zones.");
        return new(sceneNode, mapping[polygon]);
    }

    /// <summary>
    /// Refuses a source polygon ordinal the node's built model has no face for (a polygon the build discarded, or an index
    /// read from another layout), so that a stale index is never applied to another face.
    /// </summary>
    public static void RequireBuiltPolygons(SourceSceneState build, IReadOnlyList<SourceZoneSelection> selections, CancellationToken token = default)
    {
        BuiltPolygons built = new();
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested();
            if (selection.Polygon is { } polygon && build.Provenance.TryGetValue(selection.SceneNode, out var origin)) built.Require(origin, polygon, token);
        }
    }

    public static SourceZoneInspection Inspect(SourceWorkspace workspace, SourceSceneState build,
        IReadOnlyList<SourceZoneSelection> selections, int offset = 0, CancellationToken token = default)
    {
        if (offset < 0) throw Bad("Use a nonnegative inspection offset.");
        var context = Prepare(workspace, build, selections, token);
        var states = context.Targets.Skip(offset).Take(MaximumPage).Select(target =>
        {
            var profile = context.Assets[Key(target)].Profile;
            var node = Node(profile, target);
            uint word = node?.Word ?? profile.MeshPolygons[target.Index][target.Polygon];
            if (node is { Inherit: true } && target.Kind == SourceZoneTargetKind.Node)
            {
                if (!context.InheritedZones.TryGetValue((Key(target), target.Index), out var effective) || effective == null)
                    throw Bad("The selected inherited assignment has no single imported zone baseline. Rebuild the world and inspect its placements separately.");
                // A file profile cannot resolve an inherited ID supplied by an outer placement.
                // Use the selected occurrence's immutable import baseline, not a runtime node or
                // the referenced file's default parent, while retaining its own opaque bytes.
                word = word & ~255u | effective.Value;
            }
            return new SourceZoneTargetState(target, word, node?.Gate,
                context.Scope(target).Count);
        }).ToArray();
        return new(context.Path, context.Assets.Values.Select(e => e.OwnerPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), states, context.Targets.Count, offset,
            (long)offset + states.Length < context.Targets.Count ? offset + states.Length : null,
            context.Affected.Take(MaximumAffectedShown).ToArray(), context.Affected.Count, context.Shared,
            context.Missions.Take(MaximumAffectedPathsShown).ToArray(), context.Missions.Count,
            context.AffectedSources.Take(MaximumAffectedPathsShown).ToArray(), context.AffectedSources.Count);
    }

    public static SourceZoneEditPlan PlanAssignments(SourceWorkspace workspace, SourceSceneState build,
        IReadOnlyList<SourceZoneSelection> selections, SourceZoneAssignment assignment, bool sharedScope,
        CancellationToken token = default)
    {
        if (assignment.NodeZone == null && assignment.Gate == null && assignment.PolygonZones == null && !assignment.Inherit)
            throw Bad("Choose an object zone, gate or polygon zone set.");
        if (assignment.Inherit && (assignment.NodeZone != null || assignment.PolygonZones != null)) throw Bad("An inheriting object has no zone of its own.");
        if (assignment.PolygonZones?.Count > 3) throw Bad("A polygon accepts zero to three ordered zone IDs.");
        var context = Prepare(workspace, build, selections, token);
        // The GUI shows no scope page, so the refusal itself states what the edit would reach.
        if (context.Shared && !sharedScope)
            throw Bad($"These assignments also change shared source uses: {context.Affected.Count:N0} node{(context.Affected.Count == 1 ? "" : "s")} in this world, across {context.Missions.Count:N0} mission{(context.Missions.Count == 1 ? "" : "s")} ({string.Join(", ", context.Missions.Take(8))}{(context.Missions.Count > 8 ? ", …" : "")}){(context.SharedInstance ? ", including a node placed under several parents" : "")}. " +
                $"Affected sources ({context.AffectedSources.Count:N0}): {string.Join(", ", context.AffectedSources.Take(4).Select(p => JsonData.ShownText(p, 160)))}{(context.AffectedSources.Count > 4 ? ", …" : "")}. Choose shared scope to change them all.");

        foreach (var group in context.Targets.GroupBy(Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var profile = context.Assets[group.Key].Profile;
            WorldNodeZone[]? nodes = null;
            IReadOnlyList<uint>[]? meshes = null;
            Dictionary<int, uint[]> editedMeshes = [];
            var root = profile.LoadRoot;
            // When one transaction changes a file default and also authors an override that
            // previously inherited it, carry both selected changes into the new full profile.
            var dependencies = (context.Assets[group.Key].AssignmentSites ?? [group.Key]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var target in context.Targets.Where(t => dependencies.Contains(Key(t))))
            {
                token.ThrowIfCancellationRequested();
                if (target.Kind == SourceZoneTargetKind.Polygon)
                {
                    if (assignment.PolygonZones == null || assignment.NodeZone != null || assignment.Gate != null || assignment.Inherit)
                        throw Bad("Polygon selections require only a polygon zone set; object zone and gate are separate edits.");
                    if (!editedMeshes.TryGetValue(target.Index, out var words))
                    {
                        meshes ??= profile.MeshPolygons.ToArray();
                        words = profile.MeshPolygons[target.Index].ToArray();
                        editedMeshes.Add(target.Index, words); meshes[target.Index] = words;
                    }
                    words[target.Polygon] = ZoneWord(assignment.PolygonZones);
                }
                else
                {
                    if (assignment.PolygonZones != null) throw Bad("Object selections do not identify a polygon; select exact faces for a polygon edit.");
                    var previous = Node(profile, target)!;
                    if (assignment.Inherit && target.Kind == SourceZoneTargetKind.LoadRoot) throw Bad("A load root has no parent to inherit a zone from; choose a zone or Any.");
                    var next = new WorldNodeZone(assignment.NodeZone is { } id ? previous.Word & ~255u | id : previous.Word,
                        assignment.Gate ?? previous.Gate, assignment.Inherit || assignment.NodeZone == null && previous.Inherit);
                    if (target.Kind == SourceZoneTargetKind.LoadRoot) root = next;
                    else { nodes ??= profile.Nodes.ToArray(); nodes[target.Index] = next; }
                }
            }
            context.Sources.Set(context.Assets[group.Key], profile with { Nodes = nodes ?? profile.Nodes, MeshPolygons = meshes ?? profile.MeshPolygons, LoadRoot = root });
        }
        return new("Edit map zones", context.Sources.Changes(), context.Affected.Take(MaximumAffectedShown).ToArray(), context.Affected.Count, context.Shared,
            context.Missions.Take(MaximumAffectedPathsShown).ToArray(), context.Missions.Count,
            context.AffectedSources.Take(MaximumAffectedPathsShown).ToArray(), context.AffectedSources.Count);
    }

    /// <summary>Names zone <paramref name="id"/> in the map's catalog; an empty label removes its name (its assignments stay).</summary>
    public static SourceZoneEditPlan PlanLabel(SourceWorkspace workspace, string mission, byte id, string label, CancellationToken token = default)
    {
        SourceEmbeddedZones sources = new(workspace, token);
        var map = sources.ReadMission(mission, includeEntries: false);
        if (label.Length > SourceEmbeddedZones.MaximumLabelCharacters || id == 255)
            throw Bad("Use a label of at most 256 characters; zone 255 is always Any.");
        if (label.Length == 0)
        {
            if (!map.Labels.Any(x => x.Id == id)) throw Bad($"Zone {id} has no label to remove.");
            sources.SetLabels(map.MasterPath, map.Labels.Where(x => x.Id != id));
            return new($"Remove the label of zone {id}", sources.Changes(), [], 0, false);
        }
        sources.SetLabels(map.MasterPath, map.Labels.Where(x => x.Id != id).Append(new SourceMapZoneLabel(id, label)));
        return new($"Label zone {id} “{JsonData.ShownText(label, 64)}”", sources.Changes(), [], 0, false);
    }

    public static SourceZoneEditPlan PlanRemoveLabel(SourceWorkspace workspace, string mission, byte id, byte? replacement = null, CancellationToken token = default)
    {
        SourceEmbeddedZones sources = new(workspace, token);
        var map = sources.ReadMission(mission);
        if (id == 255) throw Bad("The Any zone cannot be removed.");
        if (replacement == id) throw Bad("A removed zone needs a different replacement ID.");
        if (!map.Labels.Any(x => x.Id == id)) throw Bad("That zone has no authored catalog label.");
        int[] nodes = new int[256], polygons = new int[256]; CountUses(map.Entries.Select(e => e.Profile), nodes, polygons, token);
        if (replacement == null && (nodes[id] != 0 || polygons[id] != 0))
            throw Bad("The zone is still assigned. Choose an explicit replacement before removing it.");
        WorldNodeZone ReplaceNode(WorldNodeZone node) => replacement is { } next && !node.Inherit && (byte)node.Word == id ? node with { Word = node.Word & ~255u | next } : node;
        uint ReplaceWord(uint word)
        {
            if (replacement is not { } next) return word;
            for (int slot = 0; slot < Math.Min(3, (int)(byte)word); slot++)
            { int shift = 8 * (slot + 1); if ((byte)(word >> shift) == id) word = word & ~(255u << shift) | (uint)next << shift; }
            return word;
        }
        HashSet<string> changedSites = new(StringComparer.OrdinalIgnoreCase);
        if (replacement != null)
            foreach (var entry in map.Entries)
            {
                token.ThrowIfCancellationRequested(); var profile = entry.Profile;
                var changedNodes = profile.Nodes.Select(n => { token.ThrowIfCancellationRequested(); return ReplaceNode(n); }).ToArray();
                var changedMeshes = profile.MeshPolygons.Select(mesh => (IReadOnlyList<uint>)mesh.Select(w => { token.ThrowIfCancellationRequested(); return ReplaceWord(w); }).ToArray()).ToArray();
                if (changedNodes.SequenceEqual(profile.Nodes) && changedMeshes.Zip(profile.MeshPolygons).All(pair => pair.First.SequenceEqual(pair.Second))
                    && (profile.LoadRoot == null || ReplaceNode(profile.LoadRoot) == profile.LoadRoot)) continue;
                sources.Set(entry, profile with { Nodes = changedNodes, MeshPolygons = changedMeshes, LoadRoot = profile.LoadRoot is { } root ? ReplaceNode(root) : null });
                changedSites.Add(entry.Key);
            }
        if (changedSites.Count > 0)
            foreach (string other in Missions(workspace, token))
            {
                if (other.Equals(mission, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var occurrence in sources.ReadMission(other).Entries)
                    if ((occurrence.AssignmentSites ?? [occurrence.Key]).Any(changedSites.Contains))
                        throw Bad($"Replacing this zone also changes source assignments used by {other}. Change those assignments with the explicit shared-scope zone editor first, then remove the unused label.");
            }
        sources.SetLabels(map.MasterPath, map.Labels.Where(x => x.Id != id));
        return new("Remove map zone", sources.Changes(), [], 0, replacement != null);
    }

    private static uint ZoneWord(IReadOnlyList<byte> ids)
    {
        uint word = 0xFFFFFF00u | (uint)ids.Count;
        for (int i = 0; i < ids.Count; i++) { int shift = (i + 1) * 8; word = word & ~(255u << shift) | (uint)ids[i] << shift; }
        return word;
    }
    private static WorldNodeZone? Node(WorldZoneProfile profile, SourceZoneTarget target) => target.Kind switch
    { SourceZoneTargetKind.Node => profile.Nodes[target.Index], SourceZoneTargetKind.LoadRoot => profile.LoadRoot, _ => null };

    private static void CountUses(IEnumerable<WorldZoneProfile> profiles, int[] nodes, int[] polygons, CancellationToken token)
    {
        foreach (var profile in profiles)
        {
            foreach (var node in profile.Nodes) { token.ThrowIfCancellationRequested(); if (!node.Inherit) nodes[(byte)node.Word]++; }
            if (profile.LoadRoot is { } root) nodes[(byte)root.Word]++;
            foreach (var mesh in profile.MeshPolygons)
                foreach (uint word in mesh)
                { token.ThrowIfCancellationRequested(); for (int slot = 0; slot < Math.Min(3, (int)(byte)word); slot++) polygons[(byte)(word >> (8 * (slot + 1)))]++; }
        }
    }

    /// <summary>The source polygons each built model has faces from; one set per model's mapping, shared by its nodes.</summary>
    private sealed class BuiltPolygons
    {
        private readonly Dictionary<IReadOnlyList<int>, HashSet<int>> sources = new(ReferenceEqualityComparer.Instance);
        internal void Require(WorldNodeProvenance origin, int polygon, CancellationToken token)
        {
            if (origin.ZonePolygons is not { } mapping)
                throw Bad("The selected node has no compiled-to-source polygon identity. Rebuild the world before editing zones.");
            if (!sources.TryGetValue(mapping, out var found))
            {
                found = [];
                for (int i = 0; i < mapping.Count; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    found.Add(mapping[i]);
                }
                sources.Add(mapping, found);
            }
            if (!found.Contains(polygon))
                throw Bad($"Source polygon {polygon} has no face in the selected node's built model: the build discarded it, or the index comes from an older layout. Select the face again from the current world.");
        }
    }

    private static string Key(SourceZoneTarget target) => SourceEmbeddedZones.Identity(target.OwnerPath ?? target.Asset, target.OwnerReferences ?? []);
    private static string Key(WorldNodeProvenance origin, string model) => SourceEmbeddedZones.Identity(origin.ZoneOwnerPath ?? model, origin.ZoneOwnerReferences);

    private sealed class Context(string path, SourceEmbeddedZones sources)
    {
        internal string Path { get; } = path;
        internal SourceEmbeddedZones Sources { get; } = sources;
        internal Dictionary<string, SourceEmbeddedZones.Entry> Assets { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<SourceZoneTarget> Targets { get; } = [];
        /// <summary>For each of <see cref="Targets"/>, the first selection that named it.</summary>
        internal List<SourceZoneSelection> Selections { get; } = [];
        internal Dictionary<(string Key, int Index), uint?> InheritedZones { get; } = [];
        internal Dictionary<string, Dictionary<(SourceZoneTargetKind Kind, int Index), List<int>>> Scopes { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal SortedSet<int> Affected { get; } = [];
        internal bool SharedInstance { get; set; }
        internal SortedSet<string> Missions { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal SortedSet<string> AffectedSources { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal bool SharedSource { get; set; }
        internal bool Shared => SharedInstance || SharedSource || Targets.Any(t => Scope(t).Count > 1);
        internal List<int> Scope(SourceZoneTarget target) => Scopes[Key(target)][(target.Kind, target.Index)];
    }

    private static Context Prepare(SourceWorkspace workspace, SourceSceneState build, IReadOnlyList<SourceZoneSelection> selections, CancellationToken token,
        int maximumAssets = MaximumSelectedAssets)
    {
        if (selections.Count is < 1 or > MaximumSelections) throw Bad("Select between one and 4,096 exact zone targets.");
        if (build.Provenance.Count > SourceEmbeddedZones.MaximumTargets || build.Freed.Count > SourceEmbeddedZones.MaximumTargets)
            throw Bad("The source world has too many zone occurrences.");
        SourceEmbeddedZones sources = new(workspace, token);
        var mission = sources.ReadMission(build.Mission, includeEntries: false);
        Context context = new(mission.MasterPath, sources);
        HashSet<(string Key, SourceZoneTargetKind Kind, int Index, int Polygon)> targets = [];
        BuiltPolygons built = new();
        var loadRoots = ImportedLoadRoots(build, selections, token);
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested();
            if (!build.Provenance.TryGetValue(selection.SceneNode, out var origin)) throw Bad("The selected scene node has no source provenance.");
            if (origin.Terrain != null) throw Bad("Terrain zones belong to the source surface recipe; use terrain surface editing.");
            string model = origin.ModelFile ?? origin.LoadedFile ?? throw Bad("The selected object has no source model.");
            bool loadedRoot = origin.ModelFile == null;
            string key = Key(origin, model);
            if (!context.Assets.TryGetValue(key, out var asset))
            {
                if (context.Assets.Count >= maximumAssets) throw Bad($"Select at most {maximumAssets:N0} source occurrences per zone edit.");
                asset = sources.Resolve(origin.ZoneOwnerPath ?? model, origin.ZoneOwnerReferences, model);
                context.Assets.Add(key, asset);
            }
            if (!asset.ModelPath.Equals(model, StringComparison.OrdinalIgnoreCase)) throw Bad("A selected reference has inconsistent source identities. Rebuild the world.");
            SourceZoneTarget target;
            if (selection.Polygon is { } polygon)
            {
                built.Require(origin, polygon, token);
                if (loadedRoot || origin.ZoneMesh < 0 || origin.ZoneMesh >= asset.Profile.MeshPolygons.Count || polygon < 0 || polygon >= asset.Profile.MeshPolygons[origin.ZoneMesh].Count)
                    throw Bad("The selected polygon is outside its exact source mesh profile.");
                target = new(model, SourceZoneTargetKind.Polygon, origin.ZoneMesh, polygon, asset.OwnerPath, asset.ReferenceChain);
            }
            else if (loadedRoot)
            {
                if (asset.Profile.LoadRoot == null)
                {
                    if (!loadRoots.TryGetValue(key, out var imported) || imported == null)
                        throw Bad("The selected load root lacks a consistent imported zone baseline. Rebuild the world before editing its zones.");
                    // Plain imported models and files reconstructed only as references have no
                    // authored load root. Inspect the exact import baseline without writing it;
                    // only accepting an assignment authors it in the owning source file.
                    asset = asset with { Profile = asset.Profile with { LoadRoot = imported } };
                    context.Assets[key] = asset;
                }
                target = new(model, SourceZoneTargetKind.LoadRoot, -1, OwnerPath: asset.OwnerPath, OwnerReferences: asset.ReferenceChain);
            }
            else
            {
                if (origin.ZoneNode < 0 || origin.ZoneNode >= asset.Profile.Nodes.Count) throw Bad("The selected object has no exact source node profile.");
                target = new(model, SourceZoneTargetKind.Node, origin.ZoneNode, OwnerPath: asset.OwnerPath, OwnerReferences: asset.ReferenceChain);
                if (asset.Profile.Nodes[origin.ZoneNode].Inherit)
                {
                    uint? effective = origin.ImportedZoneWord is { } imported ? imported & 255u : null;
                    var identity = (key, origin.ZoneNode);
                    if (!context.InheritedZones.TryAdd(identity, effective) && context.InheritedZones[identity] != effective)
                        context.InheritedZones[identity] = null;
                }
            }
            if (targets.Add((key, target.Kind, target.Index, target.Polygon))) { context.Targets.Add(target); context.Selections.Add(selection); }
        }
        foreach (var target in context.Targets)
        {
            string key = Key(target);
            if (!context.Scopes.TryGetValue(key, out var scopes)) context.Scopes.Add(key, scopes = []);
            scopes.TryAdd((target.Kind, target.Index), []);
        }
        context.Missions.Add(build.Mission);
        foreach (var entry in context.Assets.Values) context.AffectedSources.Add(entry.OwnerPath);
        var selectedModels = context.Assets.Values.Select(e => e.ModelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SourceEmbeddedZones.Entry> occurrences = new(context.Assets, StringComparer.OrdinalIgnoreCase);
        foreach (var (scene, origin) in build.Provenance)
        {
            token.ThrowIfCancellationRequested();
            if (origin.Terrain != null) continue;
            string? model = origin.ModelFile ?? origin.LoadedFile;
            if (model == null || !selectedModels.Contains(model)) continue;
            string ownKey = Key(origin, model);
            if (!occurrences.TryGetValue(ownKey, out var occurrence))
            {
                occurrence = sources.Resolve(origin.ZoneOwnerPath ?? model, origin.ZoneOwnerReferences, model);
                occurrences.Add(ownKey, occurrence);
            }
            foreach (var site in occurrence.AssignmentSites ?? [ownKey])
            {
                if (!context.Scopes.TryGetValue(site, out var scopes)) continue;
                void Add(SourceZoneTargetKind kind, int index)
                {
                    if (scopes.TryGetValue((kind, index), out var uses))
                    {
                        uses.Add(scene); context.Affected.Add(scene);
                        context.SharedInstance |= origin.SharedPlacement;
                    }
                }
                if (origin.ModelFile == null) Add(SourceZoneTargetKind.LoadRoot, -1);
                else { Add(SourceZoneTargetKind.Node, origin.ZoneNode); Add(SourceZoneTargetKind.Polygon, origin.ZoneMesh); }
            }
        }
        // The storage file may itself be loaded or referenced by another mission. An outer full
        // override shields that placement; missing overrides intentionally depend on these defaults.
        foreach (string missionName in Missions(workspace, token))
        {
            token.ThrowIfCancellationRequested();
            var missionSources = sources.ReadMission(missionName);
            foreach (var occurrence in missionSources.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (!selectedModels.Contains(occurrence.ModelPath)) continue;
                foreach (string site in occurrence.AssignmentSites ?? [occurrence.Key])
                    if (context.Assets.ContainsKey(site))
                    {
                        context.Missions.Add(missionName); context.AffectedSources.Add(occurrence.OwnerPath);
                        context.SharedSource |= !missionName.Equals(build.Mission, StringComparison.OrdinalIgnoreCase)
                            || !site.Equals(occurrence.Key, StringComparison.OrdinalIgnoreCase);
                    }
            }
        }
        token.ThrowIfCancellationRequested(); return context;
    }

    private static Dictionary<string, WorldNodeZone?> ImportedLoadRoots(SourceSceneState build,
        IReadOnlyList<SourceZoneSelection> selections, CancellationToken token)
    {
        HashSet<string> wanted = new(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested();
            if (build.Provenance.TryGetValue(selection.SceneNode, out var origin) && origin.ModelFile == null && origin.LoadedFile is { } model)
                wanted.Add(Key(origin, model));
        }
        if (wanted.Count > MaximumSelectedAssets) throw Bad("Select at most 64 source occurrences per zone edit.");
        Dictionary<string, WorldNodeZone?> result = new(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;
        foreach (var origin in build.Provenance.Values.Concat(build.Freed))
        {
            token.ThrowIfCancellationRequested();
            if (origin.ModelFile != null || origin.LoadedFile is not { } model) continue;
            string key = Key(origin, model);
            if (!wanted.Contains(key)) continue;
            WorldNodeZone? value = origin.ImportedZoneWord is { } word && origin.ImportedZoneGate is { } gate ? new(word, gate) : null;
            if (!result.TryAdd(key, value) && result[key] != value) result[key] = null;
        }
        return result;
    }
    private static IEnumerable<string> Missions(SourceWorkspace workspace, CancellationToken token)
    {
        foreach (string script in SourceProject.Files(workspace.Root, SourceProject.GameGenFolder,
            name => name.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) && SourceProject.MissionName().IsMatch(System.IO.Path.GetFileNameWithoutExtension(name)),
            workspace.Overlay().Keys.ToArray(), token))
        {
            token.ThrowIfCancellationRequested();
            if (script.Count(c => c == '/') == 1 && workspace.Exists(script, token)) yield return System.IO.Path.GetFileNameWithoutExtension(script);
        }
    }
    private static InvalidDataException Bad(string message) => new("Map zones: " + message);
}
