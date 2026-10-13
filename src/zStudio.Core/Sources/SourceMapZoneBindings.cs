using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>Prepared companion edits for every map that owns assignments on a physical geometry file.</summary>
internal sealed class SourceMapZoneBindings
{
    private sealed record Owner(string Path, IReadOnlyList<SourceMapZoneAsset> Assets, SourceEmbeddedZones.Entry Entry, bool OwnsProfile);
    private SourceEmbeddedZones embedded = null!;
    private string geometry = "";
    private bool referenced;
    private readonly List<Owner> owners = [];
    internal bool Any => owners.Count != 0;
    internal static void ValidateOwned(SourceWorkspace workspace, string ownerPath, CancellationToken token)
    {
        _ = new SourceEmbeddedZones(workspace, token).Closure(ownerPath);
    }

    internal sealed record GeometrySource(string? Identity, IReadOnlyDictionary<string, string?> Buffers);

    internal static bool AssignmentChanges(byte[]? before, byte[]? after, CancellationToken token)
    {
        if (before == null || after == null) return before != after;
        JsonObject Read(byte[] bytes) => JsonNode.Parse(GltfDocument.ContainerJson(bytes, token, out _), documentOptions: new() { MaxDepth = 64 })!.AsObject();
        var a = Read(before); var b = Read(after);
        static JsonNode? Engine(JsonNode? node) => node?["extras"]?[WorldGltf.Key];
        static bool Fields(JsonNode? a, JsonNode? b, params string[] names) => names.Any(n => !JsonNode.DeepEquals(a?[n], b?[n]));
        bool Rows(JsonNode? a, JsonNode? b, Func<JsonNode?, JsonNode?, bool> changed)
        {
            var left = a as JsonArray; var right = b as JsonArray;
            for (int i = 0; i < Math.Max(left?.Count ?? 0, right?.Count ?? 0); i++)
            {
                token.ThrowIfCancellationRequested();
                if (changed(i < (left?.Count ?? 0) ? left![i] : null, i < (right?.Count ?? 0) ? right![i] : null)) return true;
            }
            return false;
        }
        bool Node(JsonNode? a, JsonNode? b) => Fields(a, b, "zone", "zonePadding", "altitudeZoneCheck", "zoneOverrides", "ref", "instance");
        return Fields(a, b, "scene", "buffers", "bufferViews", "accessors")
            || Rows(a["scenes"], b["scenes"], (x, y) => Fields(x, y, "nodes") || Node(Engine(x)?["loadRoot"], Engine(y)?["loadRoot"]))
            || Rows(a["nodes"], b["nodes"], (x, y) => Fields(x, y, "mesh", "children", "matrix", "translation", "rotation", "scale") || Node(Engine(x), Engine(y)))
            || Rows(a["meshes"], b["meshes"], (x, y) => Rows(x?["primitives"], y?["primitives"],
                (p, q) => Fields(p, q, "attributes", "indices", "mode", "targets", "material") || Fields(Engine(p), Engine(q), "polygons", "polygonZones", "polygonZonePadding", "polygonZoneRaw")))
            || Rows(a["materials"], b["materials"], (x, y) => Fields(Engine(x), Engine(y), "zone", "zones", "zonePadding", "zoneRaw"));
    }

    internal static Dictionary<string, IReadOnlySet<string>> SourceClosures(SourceWorkspace workspace, IReadOnlyCollection<string> owners,
        IReadOnlyList<(string Relative, byte[] Content)> proposed, CancellationToken token)
    {
        var replacements = proposed.ToDictionary(p => p.Relative, p => p.Content, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string[]> references = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlySet<string>> result = new(StringComparer.OrdinalIgnoreCase);
        long bytesRead = 0, identities = 0, work = 0;
        foreach (string owner in owners.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            HashSet<string> closure = new(StringComparer.OrdinalIgnoreCase), active = new(StringComparer.OrdinalIgnoreCase);
            Visit(owner, 0); result.Add(owner, closure);
            void Visit(string path, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > GltfDocument.MaximumDepth || ++work > SourceMapZones.MaximumTargets)
                    throw new InvalidDataException("Source geometry dependencies exceed their inspection allowance.");
                if (!active.Add(path)) throw new InvalidDataException("Source geometry dependencies contain a reference cycle.");
                if (!closure.Add(path)) { active.Remove(path); return; }
                if (path.Length + 64L > GltfDocument.MaximumMetadataBytes - identities)
                    throw new InvalidDataException("Source geometry dependency identities exceed their metadata allowance.");
                identities += path.Length + 64L;
                if (!references.TryGetValue(path, out var children))
                {
                    if (references.Count >= SourceMapZones.MaximumAssets) throw new InvalidDataException("Too many source geometry dependencies.");
                    byte[] bytes;
                    var limits = ProjectReadLimits.Model().WithMaximum((512L << 20) - bytesRead);
                    if (replacements.TryGetValue(path, out var changed)) { limits.Validate(changed); bytes = changed; }
                    else bytes = workspace.Read(path, token, limits) ?? throw new InvalidDataException("A source geometry dependency is missing.");
                    bytesRead += bytes.LongLength;
                    var hierarchy = GltfDocument.ReadHierarchy(bytes, token);
                    children = hierarchy.AllNodes().Distinct().Where(n => n.Extras?[WorldGltf.Key]?["ref"] != null)
                        .Select(n => WorldAssembler.Relative(path, n.Extras![WorldGltf.Key]!["ref"]!.GetValue<string>())).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    references.Add(path, children);
                }
                foreach (string child in children) Visit(child, depth + 1);
                active.Remove(path);
            }
        }
        return result;
    }

    internal static Dictionary<string, GeometrySource> GeometryIdentities(SourceWorkspace workspace, IEnumerable<string> paths, bool disk, CancellationToken token,
        IReadOnlyDictionary<string, byte[]>? prospective = null)
    {
        Dictionary<string, GeometrySource> result = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> buffers = new(StringComparer.OrdinalIgnoreCase);
        long read = 0, written = 0, retained = 0;
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (result.Count >= SourceMapZones.MaximumAssets) throw new InvalidDataException("Too many source geometry dependencies.");
            byte[]? bytes = workspace.ReadMapZoneSource(path, disk, (512L << 20) - read, token);
            Dictionary<string, string?> inputs = new(StringComparer.OrdinalIgnoreCase);
            string? semantic = null;
            if (bytes != null)
            {
                read += bytes.LongLength;
                var jsonBytes = GltfDocument.ContainerJson(bytes, token, out var binary);
                var root = JsonNode.Parse(jsonBytes, documentOptions: new() { MaxDepth = 64 })!.AsObject();
                string? embedded = bytes.AsSpan().StartsWith("glTF"u8) ? SourceProject.Sha256(binary) : null;
                var identity = SourceBlender.HistoryIdentity(root, uri =>
                {
                    if (uri.Length == 0) return embedded ?? throw new InvalidDataException("An embedded geometry buffer is missing.");
                    string relative = Buffer(uri);
                    if (inputs[relative] == null) throw new InvalidDataException("A source geometry buffer is missing.");
                    return relative.ToUpperInvariant();
                }, token);
                if (written >= 512L << 20) throw new InvalidDataException("Source geometry identities exceed their metadata allowance.");
                byte[] canonical = GltfJson.Write(identity, false, token, (int)Math.Min(GltfDocument.MaximumJsonBytes, (512L << 20) - written));
                written += canonical.LongLength; semantic = SourceProject.Sha256(canonical);
            }
            if (prospective != null)
            {
                var limits = ProjectReadLimits.Model().WithMaximum((512L << 20) - read);
                byte[]? future;
                if (prospective.TryGetValue(path, out var replacement)) { limits.Validate(replacement); future = replacement; }
                else future = workspace.Read(path, token, limits);
                if (future != null)
                {
                    read += future.LongLength;
                    var json = JsonNode.Parse(GltfDocument.ContainerJson(future, token, out _), documentOptions: new() { MaxDepth = 64 })!;
                    foreach (var buffer in json["buffers"] as JsonArray ?? [])
                        if (buffer?["uri"] is JsonValue value && value.TryGetValue<string>(out var uri) && !uri.StartsWith("data:", StringComparison.Ordinal)) Buffer(uri);
                }
            }
            result.Add(path, new(semantic, inputs));
            string Buffer(string uri)
            {
                string relative = WorldAssembler.Relative(path, Uri.UnescapeDataString(uri));
                if (!inputs.ContainsKey(relative))
                {
                    long metadata = 96L + relative.Length * 2L + 128;
                    if (metadata > GltfDocument.MaximumMetadataBytes - retained)
                        throw new InvalidDataException("Source buffer dependencies exceed their metadata allowance.");
                    retained += metadata;
                }
                if (!buffers.TryGetValue(relative, out var hash))
                {
                    byte[]? content = workspace.ReadMapZoneBuffer(relative, disk, (512L << 20) - read, token);
                    read += content?.LongLength ?? 0; hash = content == null ? null : SourceProject.Sha256(content); buffers.Add(relative, hash);
                }
                inputs[relative] = hash; return relative;
            }
        }
        return result;
    }

    // Topology fingerprints cannot identify faces after a same-layout reorder. Remember the
    // physically authored incoming scopes as semantic dependencies until geometry is saved.
    internal static Dictionary<string, IReadOnlyDictionary<string, string>> IncomingOwners(SourceWorkspace workspace,
        IReadOnlyCollection<string> geometries, bool disk, CancellationToken token)
    {
        var result = geometries.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p,
            _ => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        if (result.Count == 0) return result;
        Dictionary<string, Dictionary<int, (string Target, JsonObject? Scope)>> references = new(StringComparer.OrdinalIgnoreCase);
        long bytesRead = 0, edges = 0, visited = 0, encoded = 0, identityCharacters = 0;
        var overlay = disk ? [] : workspace.Overlay().Keys.ToArray();
        foreach (string folder in new[] { SourceProject.DataFolder, SourceProject.GameGenFolder })
        foreach (string path in SourceProject.Files(workspace.Root, folder,
            p => p.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".glb", StringComparison.OrdinalIgnoreCase), overlay, token))
        {
            if (references.Count >= SourceMapZones.MaximumAssets) throw new InvalidDataException("Too many source models for placement dependency inspection.");
            byte[]? bytes = workspace.ReadMapZoneSource(path, disk, (512L << 20) - bytesRead, token);
            if (bytes == null) continue;
            bytesRead += bytes.LongLength;
            var hierarchy = GltfDocument.ReadHierarchy(bytes, token);
            Dictionary<int, (string Target, JsonObject? Scope)> nodes = [];
            foreach (var node in hierarchy.AllNodes().Distinct())
            {
                if (node.Extras?[WorldGltf.Key] is not JsonObject values || values["ref"] is not { } value) continue;
                if (++edges > SourceMapZones.MaximumTargets) throw new InvalidDataException("Too many source references for placement dependency inspection.");
                if (value is not JsonValue text || !text.TryGetValue<string>(out var uri)) throw new InvalidDataException("A model reference must contain a relative path.");
                nodes.Add(node.Index, (WorldAssembler.Relative(path, uri), values["zoneOverrides"] as JsonObject));
            }
            references.Add(path, nodes);
        }
        Dictionary<JsonObject, string> digests = new(ReferenceEqualityComparer.Instance);
        foreach (var (owner, nodes) in references)
        foreach (var (index, reference) in nodes)
            if (reference.Scope != null) Visit(owner, [index], reference.Target, reference.Scope);
        return result;

        void Visit(string owner, int[] chain, string target, JsonObject scope)
        {
            token.ThrowIfCancellationRequested();
            if (chain.Length > GltfDocument.MaximumDepth || ++visited > SourceMapZones.MaximumTargets)
                throw new InvalidDataException("Nested placement dependencies exceed their inspection allowance.");
            if (result.TryGetValue(target, out var owners))
            {
                long characters = owner.Length + 1L + chain.Length * 11L;
                if (characters > GltfDocument.MaximumMetadataBytes - identityCharacters)
                    throw new InvalidDataException("Placement dependency identities exceed their metadata allowance.");
                identityCharacters += characters;
                if (!digests.TryGetValue(scope, out var digest))
                {
                    if (encoded >= 512L << 20) throw new InvalidDataException("Placement dependency values exceed their metadata allowance.");
                    byte[] bytes = GltfJson.Write(scope, false, token, (int)Math.Min(GltfDocument.MaximumMetadataBytes, (512L << 20) - encoded));
                    encoded += bytes.LongLength; digests.Add(scope, digest = SourceProject.Sha256(bytes));
                }
                ((Dictionary<string, string>)owners).Add(SourceEmbeddedZones.Identity(owner, chain), digest);
            }
            if (scope["references"] is not JsonObject nested) return;
            foreach (var (key, child) in nested)
            {
                if (!int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
                    || child is not JsonObject values || !references.TryGetValue(target, out var nodes) || !nodes.TryGetValue(index, out var reference))
                    throw new InvalidDataException("A nested placement dependency no longer names a reference.");
                Visit(owner, [.. chain, index], reference.Target, values);
            }
        }
    }

    private void RemapNestedReferences(SourceEmbeddedZones.Entry entry, IReadOnlyDictionary<int, int> origins, CancellationToken token)
    {
        if (entry.ReferenceChain.Count == 0) return;
        if (embedded.OwnedOverride(entry) is not { } scope || scope["references"] is not JsonObject references) return;
        JsonObject remapped = [];
        Dictionary<JsonNode, int> sizes = new(ReferenceEqualityComparer.Instance);
        long retained = 0;
        foreach (var (current, original) in origins)
        {
            token.ThrowIfCancellationRequested();
            if (original >= 0 && references[original.ToString(System.Globalization.CultureInfo.InvariantCulture)] is { } assignment)
            {
                if (!sizes.TryGetValue(assignment, out int size)) sizes.Add(assignment, size = GltfJson.Write(assignment, false, token).Length);
                if (size > GltfDocument.MaximumMetadataBytes - retained)
                    throw new InvalidDataException("Copying these reference assignments exceeds the source metadata allowance.");
                retained += size;
                remapped[current.ToString(System.Globalization.CultureInfo.InvariantCulture)] = assignment.DeepClone();
            }
        }
        scope["references"] = remapped;
        embedded.MarkChanged(entry.OwnerPath);
    }
    internal void RequireSingleTerrainOwner(string database)
    {
        if (referenced || owners.Count > 1 || owners.Any(o => o.Assets.Count != 1 || !o.Assets[0].LogicalPath.Equals(database, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("This database geometry is shared by several map bindings. Give the mission an independently owned database before changing its terrain recipes.");
    }

    internal static SourceMapZoneBindings Read(SourceWorkspace workspace, string geometry, GltfDocument? original, CancellationToken token)
    {
        geometry = SourceWorkspace.Normalize(geometry);
        SourceMapZoneBindings result = new() { embedded = new(workspace, token), geometry = geometry };
        WorldGltf.ZoneLayout? layout = null;
        var pending = workspace.Overlay();
        Dictionary<string, List<(string Owner, int Node)>> incoming = new(StringComparer.OrdinalIgnoreCase);
        long metadataBytes = 0, edges = 0;
        foreach (string folder in new[] { SourceProject.DataFolder, SourceProject.GameGenFolder })
        foreach (string path in SourceProject.Files(workspace.Root, folder,
            n => n.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".glb", StringComparison.OrdinalIgnoreCase), pending.Keys.ToArray(), token))
        {
            if (!workspace.Exists(path, token)) continue;
            var bytes = workspace.Read(path, token, ProjectReadLimits.Model().WithMaximum((512L << 20) - metadataBytes))
                ?? throw new InvalidDataException("A source model disappeared while its references were inspected.");
            metadataBytes += bytes.LongLength;
            // Discover only hierarchy/metadata here. Unrelated models need not have decodable geometry or buffers.
            var hierarchy = GltfDocument.ReadHierarchy(bytes, token);
            foreach (var node in hierarchy.AllNodes())
            {
                token.ThrowIfCancellationRequested();
                if ((node.Extras?[WorldGltf.Key] as JsonObject)?["ref"] is not { } value) continue;
                if (value is not JsonValue text || !text.TryGetValue<string>(out var uri))
                    throw new InvalidDataException($"{path}: a model reference must contain a relative path.");
                if (++edges > SourceMapZones.MaximumTargets) throw new InvalidDataException("The source reference graph exceeds its inspection allowance.");
                string referenced = WorldAssembler.Relative(path, uri);
                if (!incoming.TryGetValue(referenced, out var parents)) incoming.Add(referenced, parents = []);
                parents.Add((path, node.Index));
            }
        }
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase), active = new(StringComparer.OrdinalIgnoreCase);
        result.referenced = incoming.ContainsKey(geometry);
        long occurrences = 0;
        Visit(geometry, []);
        void Visit(string owner, int[] chain)
        {
            token.ThrowIfCancellationRequested();
            if (chain.Length > GltfDocument.MaximumDepth || ++occurrences > SourceMapZones.MaximumTargets)
                throw new InvalidDataException("The source reference occurrences exceed their inspection allowance.");
            if (!active.Add(owner)) throw new InvalidDataException("The edited model participates in a cyclic reference hierarchy.");
            var entry = result.embedded.Resolve(owner, chain, geometry);
            var owned = result.embedded.OwnedOverride(entry);
            if ((chain.Length == 0 || owned != null) && seen.Add(entry.Key))
            {
                if (original != null)
                {
                    layout ??= WorldGltf.ZoneLayout.Read(original, token);
                    WorldGltf.ValidateZoneProfile(layout, entry.Profile, geometry, token);
                }
                var source = result.embedded.File(geometry).Document;
                List<SourceMapZoneReference> refs = []; int ordinal = 0;
                foreach (var node in source.AllNodes())
                {
                    if ((node.Extras?[WorldGltf.Key] as JsonObject)?["ref"] is JsonValue value && value.TryGetValue<string>(out var uri))
                        refs.Add(new(ordinal, WorldAssembler.Relative(geometry, uri), uri));
                    ordinal++;
                }
                SourceMapZoneAsset asset = new(geometry, geometry, entry.Profile, refs) { OwnerReferenceChain = entry.ReferenceChain };
                result.owners.Add(new(entry.OwnerPath, [asset], entry, chain.Length == 0 || owned?["fingerprint"] != null));
            }
            if (incoming.TryGetValue(owner, out var parents))
                foreach (var parent in parents) Visit(parent.Owner, [parent.Node, .. chain]);
            active.Remove(owner);
        }
        workspace.TrackMapZoneGeometry(geometry);
        return result;
    }

    /// <summary>Checks that every binding still describes <paramref name="original"/>, the geometry as the project holds it.</summary>
    internal void Validate(GltfDocument original, CancellationToken token)
    {
        if (!Any) return;
        var layout = WorldGltf.ZoneLayout.Read(original, token);
        foreach (var owner in owners) foreach (var asset in owner.Assets) WorldGltf.ValidateZoneProfile(layout, asset.Profile, asset.GeometryPath, token);
    }

    /// <summary>The logical assets of every map that bind this geometry, with the map file that holds each.</summary>
    internal IEnumerable<(string Map, SourceMapZoneAsset Asset)> Assets => owners.SelectMany(o => o.Assets.Select(a => (o.Path, a)));
    /// <summary>The mission folders containing physical sources that own assignments on this geometry.</summary>
    internal IEnumerable<string> Missions => owners.Select(o => o.Path.Split('/')[1]);

    /// <param name="loadZone">
    /// The zone the shown build gave a node (map file, logical model, glTF node index) whose zone came from what loads the
    /// file, when every load of it gave the same; null when unknown or different, which refuses a change that would replace it.
    /// </param>
    internal IReadOnlyList<(string Relative, byte[] Content)> Remap(GltfDocument before, GltfDocument after,
        JsonObject root, IReadOnlyDictionary<JsonNode, int> originalObjects, IReadOnlyDictionary<int, int> copies,
        CancellationToken token, IReadOnlySet<int>? newTerrainMarkers = null, Func<string, string, IReadOnlyList<int>, int, uint?>? loadZone = null)
    {
        if (!Any) return [];
        var jsonNodes = (JsonArray)root["nodes"]!;
        Dictionary<int, int> origins = [];
        foreach (var node in after.AllNodes().Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (originalObjects.TryGetValue(jsonNodes[node.Index]!, out int original) || copies.TryGetValue(node.Index, out original))
                origins.Add(node.Index, original);
            else if (newTerrainMarkers?.Contains(node.Index) == true && node.Mesh == null && node.Children.Count == 0 && node.Extras?[WorldGltf.Key]?["terrain"] != null)
                origins.Add(node.Index, -1);
            else throw new InvalidDataException("The geometry edit introduced a node without an exact zone identity.");
        }
        embedded.ReplaceFile(geometry, root, after);
        return Remap(before, after, origins, token, loadZone: loadZone,
            preservePromoted: promoted => GltfNodeEdits.PreservePromotedReferenceOverrides(before, after, root, promoted, token))
            .Where(c => !c.Relative.Equals(geometry, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>
    /// Rewrites every binding of the geometry for <paramref name="after"/>: <paramref name="origins"/> gives each node of
    /// <paramref name="after"/> (by file index) the node of <paramref name="before"/> it is, or -1 for a new node, which takes
    /// <paramref name="added"/>'s assignment (by default its parent's zone). <paramref name="meshWords"/> gives a mesh's
    /// polygon words for an asset; without it a mesh keeps the words of the mesh its first node had. <paramref name="loadZone"/>
    /// is as for the other overload.
    /// </summary>
    internal IReadOnlyList<(string Relative, byte[] Content)> Remap(GltfDocument before, GltfDocument after, IReadOnlyDictionary<int, int> origins,
        CancellationToken token, Func<GltfNode, WorldNodeZone>? added = null, Func<SourceMapZoneAsset, GltfMesh, IReadOnlyList<uint>>? meshWords = null,
        Func<string, string, IReadOnlyList<int>, int, uint?>? loadZone = null,
        Action<IReadOnlyDictionary<int, int>>? preservePromoted = null)
    {
        if (!Any) return [];
        var layout = WorldGltf.ZoneLayout.Read(after, token);
        var oldNodes = before.AllNodes().Distinct().ToArray();
        var oldOrdinals = oldNodes.Select((node, i) => (node.Index, Ordinal: i)).ToDictionary(x => x.Index, x => x.Ordinal);
        var oldParents = Parents(before); var newParents = Parents(after);
        Dictionary<long, int> oldInstances = [];
        foreach (var node in oldNodes) if (Mark(node) is { } mark) oldInstances.TryAdd(mark, node.Index);
        Dictionary<GltfMesh, int> oldMeshes = new(ReferenceEqualityComparer.Instance);
        foreach (var node in oldNodes) if (node.Mesh is { } mesh) oldMeshes.TryAdd(mesh, oldMeshes.Count);
        var newNodes = after.AllNodes().Distinct().ToArray();
        // Each new node's place in a profile (the order of newNodes), and the first copy of each shared node, which the build reads.
        Dictionary<int, int> newOrdinals = [];
        Dictionary<long, int> newInstances = [];
        for (int i = 0; i < newNodes.Length; i++)
        {
            newOrdinals.TryAdd(newNodes[i].Index, i);
            if (Mark(newNodes[i]) is { } mark) newInstances.TryAdd(mark, newNodes[i].Index);
        }
        Dictionary<long, int> changedInstances = [];
        HashSet<long> firstInstances = [];
        foreach (var node in newNodes)
        {
            if (Mark(node) is not { } mark || !firstInstances.Add(mark) || origins[node.Index] is not (>= 0 and var original)) continue;
            if (Mark(oldNodes[oldOrdinals[original]]) is not { } previousMark) continue;
            int previousFirst = oldInstances[previousMark];
            int parent = newParents.GetValueOrDefault(node.Index, -1);
            int parentOrigin = parent < 0 ? -1 : origins[parent];
            if (previousFirst != original || oldParents.GetValueOrDefault(previousFirst, -1) != parentOrigin)
                changedInstances.Add(mark, previousFirst);
        }
        // The importer ignores the complete subtree of later instance holders, not just
        // their root fields. A map edit can therefore leave inactive child assignments
        // different from the first definition. Carry that definition's entire exact
        // subtree when a deletion/reorder promotes another holder.
        Dictionary<int, int> promotedOrigins = [];
        LookupWorkBudget promotionWork = new(LookupWorkBudget.MaximumUnits, token);
        foreach (var node in newNodes)
        {
            if (Mark(node) is not { } mark || !changedInstances.TryGetValue(mark, out int first)) continue;
            Stack<(GltfNode Current, GltfNode Previous)> pending = new();
            pending.Push((node, oldNodes[oldOrdinals[first]]));
            while (pending.TryPop(out var pair))
            {
                promotionWork.Reserve(1L + pair.Current.Children.Count);
                if (promotedOrigins.TryGetValue(pair.Current.Index, out int known))
                {
                    if (known != pair.Previous.Index) throw new InvalidDataException("The promoted instance has ambiguous child zone identities.");
                    continue;
                }
                int original = origins[pair.Current.Index];
                var priorCopy = original < 0 ? null : oldNodes[oldOrdinals[original]];
                if (priorCopy == null || pair.Current.Children.Count != pair.Previous.Children.Count ||
                    !ReferenceEquals(priorCopy.Mesh, pair.Previous.Mesh) ||
                    !string.Equals(WorldGltf.EngineName(priorCopy), WorldGltf.EngineName(pair.Previous), StringComparison.Ordinal))
                    throw new InvalidDataException("The promoted instance differs from its first definition; its child zone assignments cannot be preserved safely.");
                promotedOrigins.Add(pair.Current.Index, pair.Previous.Index);
                // A child's place is its name and how many earlier siblings have it, as the importer finds it inside an
                // instance; an editor (Blender) may list the children in another order.
                Dictionary<GltfNode, (string Name, int Occurrence)> priorPlaces = new(ReferenceEqualityComparer.Instance);
                foreach (var (child, place) in Places(priorCopy.Children)) priorPlaces.Add(child, place);
                var previousChildren = Places(pair.Previous.Children).ToDictionary(p => p.Place, p => p.Node);
                for (int i = pair.Current.Children.Count - 1; i >= 0; i--)
                {
                    var child = pair.Current.Children[i];
                    int childOrigin = origins[child.Index];
                    GltfNode? previous = null;
                    if (childOrigin >= 0 && priorPlaces.TryGetValue(oldNodes[oldOrdinals[childOrigin]], out var place)) previousChildren.TryGetValue(place, out previous);
                    pending.Push((child, previous ?? throw new InvalidDataException("The promoted instance differs from its first definition; its child zone assignments cannot be preserved safely.")));
                }
            }
        }
        static IEnumerable<(GltfNode Node, (string Name, int Occurrence) Place)> Places(List<GltfNode> children)
        {
            Dictionary<string, int> seen = new(StringComparer.Ordinal);
            foreach (var child in children)
            {
                string name = WorldGltf.EngineName(child); int occurrence = seen.GetValueOrDefault(name); seen[name] = occurrence + 1;
                yield return (child, (name, occurrence));
            }
        }
        preservePromoted?.Invoke(promotedOrigins);
        IReadOnlyDictionary<int, int> referenceOrigins = promotedOrigins.Count == 0 ? origins
            : origins.ToDictionary(pair => pair.Key, pair => promotedOrigins.GetValueOrDefault(pair.Key, pair.Value));
        foreach (var owner in owners)
        {
            // A references-only container owns the nested assignments, not the effective profile
            // inherited from the referenced file. Preserve that inheritance while reindexing its keys.
            if (!owner.OwnsProfile)
            {
                RemapNestedReferences(owner.Entry, referenceOrigins, token);
                continue;
            }
            Dictionary<string, SourceMapZoneAsset> replacements = new(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in owner.Assets)
            {
                token.ThrowIfCancellationRequested();
                List<WorldNodeZone> nodes = []; List<IReadOnlyList<uint>> meshes = []; List<SourceMapZoneReference> references = [];
                var oldReferences = asset.References.ToDictionary(r => r.Node);
                HashSet<GltfMesh> seenMeshes = new(ReferenceEqualityComparer.Instance);
                foreach (var node in newNodes)
                {
                    token.ThrowIfCancellationRequested();
                    int original = promotedOrigins.GetValueOrDefault(node.Index, origins[node.Index]);
                    int ordinal = original < 0 ? -1 : oldOrdinals[original];
                    if (original < 0) nodes.Add(added?.Invoke(node) ?? new(255, false, true));
                    else
                    {
                        var zone = asset.Profile.Nodes[ordinal];
                        int oldParent = oldParents.GetValueOrDefault(original, -1);
                        int newParent = newParents.GetValueOrDefault(node.Index, -1);
                        int parentOrigin = newParent < 0 ? -1 : origins[newParent];
                        if (Mark(node) is { } mark && changedInstances.TryGetValue(mark, out int first))
                        {
                            // All copies of an instance use its first definition. Removing/moving that
                            // holder must not make the next copy inherit a different parent's zone.
                            zone = asset.Profile.Nodes[oldOrdinals[first]];
                            if (zone.Inherit) zone = Moved(zone, first, node);
                        }
                        else if (zone.Inherit && parentOrigin != oldParent)
                            zone = Moved(zone, original, node);
                        // A reference stays with a node that still marks one (an editor can drop the mark).
                        if (oldReferences.TryGetValue(ordinal, out var reference) && node.Extras?[WorldGltf.Key]?[WorldGltf.ZoneReference] != null)
                            references.Add(reference with { Node = nodes.Count });
                        nodes.Add(zone);
                    }
                    if (node.Extras?[WorldGltf.Key]?[WorldGltf.ZoneReference] != null && (references.Count == 0 || references[^1].Node != nodes.Count - 1))
                        throw new InvalidDataException($"{asset.GeometryPath}: node {NodePath(node)} (glTF node {node.Index}) is marked {WorldGltf.ZoneReference} but is no reference holder of {owner.Path}; give it ref (the referenced model's path) instead, or remove the mark.");
                    if (node.Mesh is { } mesh && seenMeshes.Add(mesh))
                    {
                        if (meshWords != null) { meshes.Add(meshWords(asset, mesh)); continue; }
                        var oldMesh = (ordinal < 0 ? null : oldNodes[ordinal].Mesh) ?? throw new InvalidDataException("A new mesh has no source zone mapping.");
                        meshes.Add(asset.Profile.MeshPolygons[oldMeshes[oldMesh]]);
                    }
                }
                var profile = new WorldZoneProfile(layout.Fingerprint, nodes, meshes, asset.Profile.LoadRoot);
                WorldGltf.ValidateZoneProfile(layout, profile, asset.GeometryPath, token);
                replacements.Add(asset.LogicalPath, asset with { Profile = profile, References = references });

                // A node that inherits its zone and now has other parents (or a shared node whose first copy, the one the build
                // reads, changed; start is that copy as it was): where both its old and its new parents take their zone from
                // the load, it is still the load's, in every load, so it keeps inheriting. Otherwise it keeps the zone it had,
                // as its own: its old parents' zone, or the load's as known (the map's load root; the zone every load in the
                // shown build gave it), or the zone the edited file gives the node itself; without one the change is refused.
                WorldNodeZone Moved(WorldNodeZone zone, int start, GltfNode node)
                {
                    uint? had = Given(start), gets = Gets(node);
                    if (had == null && gets == null) return zone;
                    uint kept = had ?? asset.Profile.LoadRoot?.Word & 255 ?? loadZone?.Invoke(owner.Path, asset.LogicalPath, asset.OwnerReferenceChain, start)
                        ?? WorldGltf.StatedZone(node.Extras?[WorldGltf.Key])
                        ?? throw new InvalidDataException($"{asset.GeometryPath}: {JsonData.ShownText(WorldGltf.EngineName(node))} takes its zone from each load of {Path.GetFileName(asset.LogicalPath)} in {owner.Path}, where its new parents would give it zone {gets} instead. Give it a zone of its own first (in the source glTF's extras.recoil.zone, using \"any\" for any zone; or an object zone with Tools → Edit map zones in that map), or leave it under its old parents.");
                    return zone with { Word = zone.Word & ~255u | kept, Inherit = false };
                }
                // The zone the node's old parents gave it (start is the node), or null when it came from the load.
                uint? Given(int start)
                {
                    int index = start;
                    for (int depth = 0; index >= 0 && depth <= GltfDocument.MaximumDepth; depth++, index = oldParents.GetValueOrDefault(index, -1))
                    {
                        token.ThrowIfCancellationRequested();
                        if (Mark(oldNodes[oldOrdinals[index]]) is { } mark) index = oldInstances[mark];
                        var value = asset.Profile.Nodes[oldOrdinals[index]];
                        if (!value.Inherit) return value.Word & 255;
                    }
                    return null;
                }
                // The zone the node's new parents give it in this profile (written before their children), or null for the load's.
                uint? Gets(GltfNode node)
                {
                    int index = Mark(node) is { } own ? newInstances[own] : node.Index;
                    index = newParents.GetValueOrDefault(index, -1);
                    for (int depth = 0; index >= 0 && depth <= GltfDocument.MaximumDepth; depth++, index = newParents.GetValueOrDefault(index, -1))
                    {
                        token.ThrowIfCancellationRequested();
                        if (Mark(newNodes[newOrdinals[index]]) is { } mark) index = newInstances[mark];
                        var value = nodes[newOrdinals[index]];
                        if (!value.Inherit) return value.Word & 255;
                    }
                    return null;
                }
            }
            var updated = replacements[owner.Assets[0].LogicalPath];
            embedded.Set(owner.Entry, updated.Profile, after);
            RemapNestedReferences(owner.Entry, referenceOrigins, token);
        }
        return embedded.Changes();

        static long? Mark(GltfNode node) => (node.Extras?[WorldGltf.Key] as JsonObject)?["instance"] is { } value &&
            GltfInteger.TryInt64(value, out long mark) && mark > 0 ? mark : null;

        // A node of the edited file by its path of names (an editor's own names where it gave them), its last eight steps.
        string NodePath(GltfNode node)
        {
            List<string> steps = [];
            for (int index = node.Index; index >= 0 && steps.Count <= 8; index = newParents.GetValueOrDefault(index, -1))
            {
                var at = newNodes[newOrdinals[index]];
                steps.Add(at.Name.Length > 0 ? JsonData.ShownText(at.Name, 40) : $"#{at.Index}");
            }
            if (steps.Count > 8) steps[8] = "…";
            steps.Reverse();
            return JsonData.ShownText(string.Join("/", steps), 400);
        }

        Dictionary<int, int> Parents(GltfDocument document)
        {
            Dictionary<int, int> result = [];
            foreach (var node in document.AllNodes())
            {
                token.ThrowIfCancellationRequested();
                foreach (var child in node.Children) result.TryAdd(child.Index, node.Index);
            }
            return result;
        }
    }
}
