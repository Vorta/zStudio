using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// An exact source-world node, optionally one polygon of its source mesh: the polygon's ordinal in the mesh's map zone profile,
/// as <see cref="SourceZoneTarget.Polygon"/> returns it. It is not a built model's polygon index, which differs once a build
/// splits or discards source polygons; <see cref="SourceZoneEdits.FromCompiled"/> maps a picked built polygon to it.
/// </summary>
public sealed record SourceZoneSelection(int SceneNode, int? Polygon = null);
public enum SourceZoneTargetKind { Node, LoadRoot, Polygon }
/// <summary>Index is the source node or mesh ordinal; a load root has index -1.</summary>
public sealed record SourceZoneTarget(string Asset, SourceZoneTargetKind Kind, int Index, int Polygon = -1);
public sealed record SourceZoneTargetState(SourceZoneTarget Target, uint Word, bool? Gate, int AffectedCount);
/// <summary>Null leaves that property alone. An empty polygon list explicitly authors no zones; 255 means Any.</summary>
public sealed record SourceZoneAssignment(byte? NodeZone = null, bool? Gate = null, IReadOnlyList<byte>? PolygonZones = null);
public sealed record SourceZoneInspection(string Manifest, bool InitializesManifest, IReadOnlyList<SourceZoneTargetState> Targets,
    int TargetCount, int Offset, int? NextOffset, IReadOnlyList<int> AffectedNodes, int AffectedCount, bool RequiresSharedScope);
public sealed record SourceZoneCatalogEntry(byte Id, string Label, int NodeUses, int PolygonUses, bool Authored);
public sealed record SourceZoneCatalog(string Manifest, bool Exists, IReadOnlyList<SourceZoneCatalogEntry> Labels);
/// <summary>Apply Changes once through the prepared SourceWorkspace transaction; planning never accepts or saves an edit.</summary>
public sealed record SourceZoneEditPlan(string Label, IReadOnlyList<(string Relative, byte[] Content)> Changes,
    IReadOnlyList<int> AffectedNodes, int AffectedCount, bool SharedScope);

/// <summary>One map's authoritative zone catalog and exact logical-asset assignments, shared by GUI and automation.</summary>
public static class SourceZoneEdits
{
    public const int MaximumSelections = 4096, MaximumSelectedAssets = 64, MaximumPage = 32, MaximumAffectedShown = 256;
    private const long MaximumGeometryBytes = 512L << 20;

    public static SourceZoneCatalog Catalog(SourceWorkspace workspace, string mission, CancellationToken token = default)
    {
        var (path, map, exists) = Read(workspace, mission, token);
        int[] nodes = new int[256], polygons = new int[256];
        CountUses(map, nodes, polygons, token);
        var names = map.Labels.ToDictionary(x => x.Id);
        List<SourceZoneCatalogEntry> entries = [];
        for (int i = 0; i < 256; i++)
            if (names.TryGetValue((byte)i, out var label) || nodes[i] != 0 || polygons[i] != 0 || i == 255)
                entries.Add(new((byte)i, map.Label((byte)i), nodes[i], polygons[i], label != null));
        return new(path, exists, entries);
    }

    /// <summary>
    /// The selection a viewport pick makes: a polygon of the node's built model becomes the source polygon it was built from
    /// (the pieces a build splits one source polygon into select it together).
    /// </summary>
    public static SourceZoneSelection FromCompiled(SourceWorldBuild build, int sceneNode, int? compiledPolygon)
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
    public static void RequireBuiltPolygons(SourceWorldBuild build, IReadOnlyList<SourceZoneSelection> selections, CancellationToken token = default)
    {
        BuiltPolygons built = new();
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested();
            if (selection.Polygon is { } polygon && build.Provenance.TryGetValue(selection.SceneNode, out var origin)) built.Require(origin, polygon, token);
        }
    }

    public static SourceZoneInspection Inspect(SourceWorkspace workspace, SourceWorldBuild build,
        IReadOnlyList<SourceZoneSelection> selections, int offset = 0, CancellationToken token = default)
    {
        if (offset < 0) throw Bad("Use a nonnegative inspection offset.");
        var context = Prepare(workspace, build, selections, token);
        var states = context.Targets.Skip(offset).Take(MaximumPage).Select(target =>
        {
            var profile = context.Assets[target.Asset].Profile;
            var node = Node(profile, target);
            return new SourceZoneTargetState(target, node?.Word ?? profile.MeshPolygons[target.Index][target.Polygon], node?.Gate,
                context.Scope(target).Count);
        }).ToArray();
        return new(context.Path, !context.Exists, states, context.Targets.Count, offset,
            (long)offset + states.Length < context.Targets.Count ? offset + states.Length : null,
            context.Affected.Take(MaximumAffectedShown).ToArray(), context.Affected.Count, context.Shared);
    }

    public static SourceZoneEditPlan PlanAssignments(SourceWorkspace workspace, SourceWorldBuild build,
        IReadOnlyList<SourceZoneSelection> selections, SourceZoneAssignment assignment, bool sharedScope,
        CancellationToken token = default)
    {
        if (assignment.NodeZone == null && assignment.Gate == null && assignment.PolygonZones == null)
            throw Bad("Choose an object zone, gate or polygon zone set.");
        if (assignment.PolygonZones?.Count > 3) throw Bad("A polygon accepts zero to three ordered zone IDs.");
        var context = Prepare(workspace, build, selections, token);
        // The GUI shows no scope page, so the refusal itself states what the edit would reach.
        if (context.Shared && !sharedScope)
            throw Bad($"These assignments also change other uses of the same logical asset or mesh in this map: {context.Affected.Count:N0} world node{(context.Affected.Count == 1 ? "" : "s")} in all{(context.SharedInstance ? ", including a node placed under several parents" : "")}. Choose shared scope to change them all.");
        Dictionary<string, WorldZoneProfile> changed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var group in context.Targets.GroupBy(t => t.Asset, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var profile = context.Assets[group.Key].Profile;
            WorldNodeZone[]? nodes = null;
            IReadOnlyList<uint>[]? meshes = null;
            Dictionary<int, uint[]> editedMeshes = [];
            var root = profile.LoadRoot;
            foreach (var target in group)
            {
                token.ThrowIfCancellationRequested();
                if (target.Kind == SourceZoneTargetKind.Polygon)
                {
                    if (assignment.PolygonZones == null || assignment.NodeZone != null || assignment.Gate != null)
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
                    var next = new WorldNodeZone(assignment.NodeZone is { } id ? previous.Word & ~255u | id : previous.Word,
                        assignment.Gate ?? previous.Gate, assignment.NodeZone == null && previous.Inherit);
                    if (target.Kind == SourceZoneTargetKind.LoadRoot) root = next;
                    else { nodes ??= profile.Nodes.ToArray(); nodes[target.Index] = next; }
                }
            }
            changed.Add(group.Key, profile with { Nodes = nodes ?? profile.Nodes, MeshPolygons = meshes ?? profile.MeshPolygons, LoadRoot = root });
        }
        var assets = context.Map.Assets.Select(a => changed.TryGetValue(a.LogicalPath, out var profile) ? a with { Profile = profile } : a).ToList();
        foreach (var asset in context.Assets.Values)
            if (!context.Map.TryGetAsset(asset.LogicalPath, out _)) assets.Add(asset with { Profile = changed[asset.LogicalPath] });
        var after = new SourceMapZones(assets, context.Map.Labels).Write(token);
        return new("Edit map zones", [(context.Path, after)], context.Affected.Take(MaximumAffectedShown).ToArray(), context.Affected.Count, context.Shared);
    }

    public static SourceZoneEditPlan PlanLabel(SourceWorkspace workspace, string mission, byte id, string label, CancellationToken token = default)
    {
        var (path, map, _) = Read(workspace, mission, token);
        if (label.Length is < 1 or > SourceMapZones.MaximumLabelCharacters || id == 255 && label != "Any")
            throw Bad("Use a nonempty label of at most 256 characters; zone 255 is always Any.");
        var labels = map.Labels.Where(x => x.Id != id).Append(new SourceMapZoneLabel(id, label)).ToArray();
        return new("Name map zone", [(path, new SourceMapZones(map.Assets, labels).Write(token))], [], 0, false);
    }

    public static SourceZoneEditPlan PlanRemoveLabel(SourceWorkspace workspace, string mission, byte id, byte? replacement = null, CancellationToken token = default)
    {
        var (path, map, _) = Read(workspace, mission, token);
        if (id == 255) throw Bad("The Any zone cannot be removed.");
        if (replacement == id) throw Bad("A removed zone needs a different replacement ID.");
        if (!map.Labels.Any(x => x.Id == id)) throw Bad("That zone has no authored catalog label.");
        int[] nodes = new int[256], polygons = new int[256]; CountUses(map, nodes, polygons, token);
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
        List<SourceMapZoneAsset> assets = [];
        foreach (var asset in map.Assets)
        {
            token.ThrowIfCancellationRequested(); var profile = asset.Profile;
            var changedNodes = profile.Nodes.Select(n => { token.ThrowIfCancellationRequested(); return ReplaceNode(n); }).ToArray();
            var changedMeshes = profile.MeshPolygons.Select(mesh => (IReadOnlyList<uint>)mesh.Select(w => { token.ThrowIfCancellationRequested(); return ReplaceWord(w); }).ToArray()).ToArray();
            assets.Add(asset with { Profile = profile with { Nodes = changedNodes, MeshPolygons = changedMeshes, LoadRoot = profile.LoadRoot is { } root ? ReplaceNode(root) : null } });
        }
        return new("Remove map zone", [(path, new SourceMapZones(assets, map.Labels.Where(x => x.Id != id).ToArray()).Write(token))], [], 0, replacement != null);
    }

    private static uint ZoneWord(IReadOnlyList<byte> ids)
    {
        uint word = 0xFFFFFF00u | (uint)ids.Count;
        for (int i = 0; i < ids.Count; i++) { int shift = (i + 1) * 8; word = word & ~(255u << shift) | (uint)ids[i] << shift; }
        return word;
    }
    private static WorldNodeZone? Node(WorldZoneProfile profile, SourceZoneTarget target) => target.Kind switch
    { SourceZoneTargetKind.Node => profile.Nodes[target.Index], SourceZoneTargetKind.LoadRoot => profile.LoadRoot, _ => null };

    private static (string Path, SourceMapZones Map, bool Exists) Read(SourceWorkspace workspace, string mission, CancellationToken token)
    {
        string path = SourceMapZones.PathForMission(mission);
        var bytes = workspace.Read(path, token, SourceMapZones.MaximumBytes);
        return (path, bytes == null ? new SourceMapZones([]) : SourceMapZones.Parse(bytes, token), bytes != null);
    }
    private static void CountUses(SourceMapZones map, int[] nodes, int[] polygons, CancellationToken token)
    {
        foreach (var asset in map.Assets)
        {
            foreach (var node in asset.Profile.Nodes) { token.ThrowIfCancellationRequested(); if (!node.Inherit) nodes[(byte)node.Word]++; }
            if (asset.Profile.LoadRoot is { } root) nodes[(byte)root.Word]++;
            foreach (var mesh in asset.Profile.MeshPolygons)
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

    private sealed class Context(string path, SourceMapZones map, bool exists)
    {
        internal string Path { get; } = path;
        internal SourceMapZones Map { get; } = map;
        internal bool Exists { get; } = exists;
        internal Dictionary<string, SourceMapZoneAsset> Assets { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<SourceZoneTarget> Targets { get; } = [];
        internal Dictionary<string, Dictionary<(SourceZoneTargetKind Kind, int Index), List<int>>> Scopes { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal SortedSet<int> Affected { get; } = [];
        internal bool SharedInstance { get; set; }
        internal bool Shared => SharedInstance || Targets.Any(t => Scope(t).Count > 1);
        internal List<int> Scope(SourceZoneTarget target) => Scopes[target.Asset][(target.Kind, target.Index)];
    }

    private static Context Prepare(SourceWorkspace workspace, SourceWorldBuild build, IReadOnlyList<SourceZoneSelection> selections, CancellationToken token)
    {
        if (selections.Count is < 1 or > MaximumSelections) throw Bad("Select between one and 4,096 exact zone targets.");
        if (build.Provenance.Count > SourceMapZones.MaximumTargets || build.Freed.Count > SourceMapZones.MaximumTargets)
            throw Bad("The source world has too many zone occurrences.");
        var (path, map, exists) = Read(workspace, build.Mission, token);
        Context context = new(path, map, exists);
        HashSet<SourceZoneTarget> targets = [];
        BuiltPolygons built = new();
        long geometryBytes = 0;
        byte[] ReadGeometry(string file, ProjectReadLimits limits)
        {
            var bytes = workspace.Read(file, token, limits.WithMaximum(MaximumGeometryBytes - geometryBytes)) ?? throw Bad("A zone target's geometry or buffer is missing.");
            geometryBytes += bytes.LongLength; return bytes;
        }
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested();
            if (!build.Provenance.TryGetValue(selection.SceneNode, out var origin)) throw Bad("The selected scene node has no source provenance.");
            if (origin.Terrain != null) throw Bad("Terrain zones belong to the source surface recipe; use terrain surface editing.");
            string logical = origin.LogicalModelFile ?? origin.LogicalLoadedFile ?? throw Bad("The selected object has no logical model asset.");
            bool loadedRoot = origin.LogicalModelFile == null;
            if (origin.ZoneManifest != null && !origin.ZoneManifest.Equals(path, StringComparison.OrdinalIgnoreCase)) throw Bad("The selected node belongs to another map's zone assignments.");
            if (!context.Assets.TryGetValue(logical, out var asset))
            {
                if (context.Assets.Count >= MaximumSelectedAssets) throw Bad("Select at most 64 logical assets per zone edit.");
                bool bound = map.TryGetAsset(logical, out asset!);
                if (!bound && origin.ZoneManifest != null)
                    throw Bad("The map no longer contains the selected logical asset. Rebuild the world before editing its zones.");
                string geometry = bound ? asset.GeometryPath : origin.ModelFile ?? origin.LoadedFile ?? logical;
                var document = WorldAssembler.ReadModel(ReadGeometry(geometry, ProjectReadLimits.Model()), geometry,
                    (buffer, remaining) => ReadGeometry(buffer, ProjectReadLimits.Bytes(remaining)), token);
                if (bound) WorldGltf.ValidateZoneProfile(document, asset.Profile, token);
                else asset = CaptureLegacy(build, logical, geometry, document, token);
                context.Assets.Add(logical, asset);
            }
            SourceZoneTarget target;
            if (selection.Polygon is { } polygon)
            {
                // The selection already names the source polygon (inspection's index); a built face must still come from it.
                built.Require(origin, polygon, token);
                if (loadedRoot || origin.ZoneMesh < 0 || origin.ZoneMesh >= asset.Profile.MeshPolygons.Count || polygon < 0 || polygon >= asset.Profile.MeshPolygons[origin.ZoneMesh].Count)
                    throw Bad("The selected polygon is outside its exact source mesh profile.");
                target = new(asset.LogicalPath, SourceZoneTargetKind.Polygon, origin.ZoneMesh, polygon);
            }
            else if (loadedRoot)
            {
                if (asset.Profile.LoadRoot == null) throw Bad("The logical asset has no authored load-root zone profile.");
                target = new(asset.LogicalPath, SourceZoneTargetKind.LoadRoot, -1);
            }
            else
            {
                if (origin.ZoneNode < 0 || origin.ZoneNode >= asset.Profile.Nodes.Count) throw Bad("The selected object has no exact source node profile.");
                target = new(asset.LogicalPath, SourceZoneTargetKind.Node, origin.ZoneNode);
            }
            if (targets.Add(target)) context.Targets.Add(target);
        }
        foreach (var target in context.Targets)
        {
            if (!context.Scopes.TryGetValue(target.Asset, out var scopes)) context.Scopes.Add(target.Asset, scopes = []);
            scopes.TryAdd((target.Kind, target.Index), []);
        }
        foreach (var (scene, origin) in build.Provenance)
        {
            token.ThrowIfCancellationRequested();
            if (origin.Terrain != null) continue;
            string? logical = origin.LogicalModelFile ?? origin.LogicalLoadedFile;
            if (logical == null || !context.Scopes.TryGetValue(logical, out var scopes)) continue;
            void Add(SourceZoneTargetKind kind, int index)
            {
                if (scopes.TryGetValue((kind, index), out var uses))
                {
                    uses.Add(scene); context.Affected.Add(scene);
                    // One engine node can be placed below several parents. Slot count alone
                    // does not disclose that an edit reaches every placement of that instance.
                    context.SharedInstance |= origin.Instance != null;
                }
            }
            if (origin.LogicalModelFile == null) Add(SourceZoneTargetKind.LoadRoot, -1);
            else { Add(SourceZoneTargetKind.Node, origin.ZoneNode); Add(SourceZoneTargetKind.Polygon, origin.ZoneMesh); }
        }
        token.ThrowIfCancellationRequested(); return context;
    }

    private static SourceMapZoneAsset CaptureLegacy(SourceWorldBuild build, string logical, string geometry, GltfDocument document, CancellationToken token)
    {
        // The assembler records effective import assignments so inherited and repeated readings cannot silently collapse.
        var profile = WorldGltf.CaptureZoneProfile(document, token: token);
        var nodes = profile.Nodes.ToArray();
        bool[] observed = new bool[nodes.Length];
        WorldNodeZone? loadRoot = null;
        foreach (var origin in build.Provenance.Values.Concat(build.Freed))
        {
            token.ThrowIfCancellationRequested();
            bool root = origin.LogicalModelFile == null && string.Equals(origin.LogicalLoadedFile, logical, StringComparison.OrdinalIgnoreCase);
            if (!root && !string.Equals(origin.LogicalModelFile, logical, StringComparison.OrdinalIgnoreCase)) continue;
            if (origin.ImportedZoneWord is not { } word || origin.ImportedZoneGate is not { } gate)
                throw Bad("The selected legacy model lacks its imported zone values. Rebuild the world before editing zones.");
            bool inherited = !root && origin.ZoneNode >= 0 && origin.ZoneNode < nodes.Length && nodes[origin.ZoneNode].Inherit;
            WorldNodeZone value = new(inherited ? nodes[origin.ZoneNode].Word : word, gate, inherited);
            if (root)
            {
                if (loadRoot != null && loadRoot != value) throw InheritanceConflict();
                loadRoot = value;
            }
            else
            {
                if (origin.ZoneNode < 0 || origin.ZoneNode >= nodes.Length)
                    throw Bad("The legacy model's source layout changed. Rebuild the world before editing zones.");
                if (observed[origin.ZoneNode] && nodes[origin.ZoneNode] != value) throw InheritanceConflict();
                nodes[origin.ZoneNode] = value; observed[origin.ZoneNode] = true;
            }
        }
        return new(logical, geometry, profile with { Nodes = nodes, LoadRoot = loadRoot }, []);
        static InvalidDataException InheritanceConflict() => Bad("This legacy model inherits different object zones at its placements. Separate those logical uses before assigning one map profile; no files were changed.");
    }
    private static InvalidDataException Bad(string message) => new("Map zones: " + message);
}
