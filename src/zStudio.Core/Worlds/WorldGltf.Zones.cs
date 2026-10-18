using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using zStudio.Core.Gltf;

namespace zStudio.Core.Worlds;

/// <summary>A node's complete stored zone word and its independent altitude-probe gate.</summary>
public sealed record WorldNodeZone(uint Word, bool Gate, bool Inherit = false);

/// <summary>Map-owned assignments in neutral glTF node/mesh serialization order; polygon words retain their exact bytes.</summary>
public sealed record WorldZoneProfile(string Fingerprint, IReadOnlyList<WorldNodeZone> Nodes,
    IReadOnlyList<IReadOnlyList<uint>> MeshPolygons, WorldNodeZone? LoadRoot = null);

/// <summary>Reusable geometry, its map-owned assignments, and the authored URIs of its reference holders.</summary>
public sealed record WorldZoneExport(GltfDocument Geometry, WorldZoneProfile Profile, IReadOnlyDictionary<int, string> References);

public static partial class WorldGltf
{
    public const uint ZoneGate = 0x01000000;
    public const string ZoneReference = "zoneReference";

    internal sealed class ZoneExportState
    {
        internal Dictionary<GltfNode, WorldNodeZone> Nodes { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<GltfMesh, List<uint>> Meshes { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<GltfNode, string> References { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// Exports geometry without map zones, gates or reference URIs. Captures assignments before grouping surfaces,
    /// recording every emitted polygon boundary so zones cannot change how polygons are recovered on import.
    /// A context belongs to one export; mixing ordinary and neutral exports in its caches is refused.
    /// </summary>
    public static WorldZoneExport ExportZoned(IReadOnlyList<WorldNode> roots, uint parentZone, ExportContext context, WorldNode? loadRoot = null)
    {
        if (context.Zones != null || context.Meshes.Count != 0 || context.TurnedMeshes.Count != 0 || context.Materials.Count != 0)
            throw new InvalidOperationException("A neutral geometry export needs a fresh export context.");
        ZoneExportState state = new(); context.Zones = state;
        var document = Export(roots, parentZone, context, loadRoot);
        var layout = ZoneLayout.Read(document, context.Token);
        var nodes = layout.Nodes.Select(n => state.Nodes[n]).ToArray();
        IReadOnlyList<uint>[] meshes = [.. layout.Meshes.Select(m => (IReadOnlyList<uint>)state.Meshes[m].ToArray())];
        Dictionary<int, string> references = [];
        for (int i = 0; i < layout.Nodes.Count; i++)
            if (state.References.TryGetValue(layout.Nodes[i], out var uri)) references.Add(i, uri);
        return new(document, new(layout.Fingerprint, nodes, meshes,
            loadRoot == null ? null : new(loadRoot.Zone, (loadRoot.Flags & ZoneGate) != 0)), references);
    }

    /// <summary>
    /// Checks the ordered source layout: hierarchy, names/classes/shared-instance marks, mesh sharing, connectivity,
    /// attribute cardinalities and polygon boundaries. Coordinates, transforms and material values are not identities.
    /// Missing polygon records may use the shared polygon recovery; a profile accepts it only if this digest agrees.
    /// </summary>
    public static string LayoutFingerprint(GltfDocument document, CancellationToken token = default) => ZoneLayout.Read(document, token).Fingerprint;

    /// <summary>
    /// Captures embedded assignments without rewriting geometry. The caller supplies the actual load's inherited
    /// zone, and must not collapse uses whose inherited assignments differ into one logical profile.
    /// </summary>
    public static WorldZoneProfile CaptureZoneProfile(GltfDocument document, uint parentZone = DefaultZone,
        WorldNodeZone? loadRoot = null, CancellationToken token = default)
        => CaptureZoneProfile(document, ZoneLayout.Read(document, token), parentZone, loadRoot, token);

    private static WorldZoneProfile CaptureZoneProfile(GltfDocument document, ZoneLayout layout, uint parentZone,
        WorldNodeZone? loadRoot, CancellationToken token)
    {
        loadRoot ??= EmbeddedLoadRoot(document);
        Dictionary<GltfNode, WorldNodeZone> assigned = new(ReferenceEqualityComparer.Instance);
        Dictionary<(GltfNode? Scope, long Number), WorldNodeZone> instances = [];
        Stack<(GltfNode Node, uint Parent, GltfNode? Scope)> pending = [];
        for (int i = document.Roots.Count - 1; i >= 0; i--) pending.Push((document.Roots[i], (loadRoot?.Word ?? parentZone) & 0xFF, null));
        while (pending.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();
            if (assigned.ContainsKey(entry.Node)) continue;
            // Terrain may select one mesh from a document containing unrelated editor extras.
            // Ordinary model import validates every node's engine object before reaching this path.
            var extras = entry.Node.Extras?[Key] as System.Text.Json.Nodes.JsonObject;
            uint word = ReadNodeZoneValue(extras, entry.Parent, "the model");
            uint flags = StoredNodeFlags(extras);
            WorldNodeZone zone = new(word, (flags & ZoneGate) != 0, NodeZoneIsInherited(extras, "the model"));
            if (extras?["instance"] is { } instance)
            {
                long number = Integer(instance, "instance", "the model", 1, int.MaxValue);
                if (!instances.TryAdd((entry.Scope, number), zone)) zone = instances[(entry.Scope, number)];
            }
            assigned.Add(entry.Node, zone);
            var inline = extras == null ? null : InlineSource(entry.Node, "the model");
            for (int i = entry.Node.Children.Count - 1; i >= 0; i--) pending.Push((entry.Node.Children[i], zone.Word & 0xFF, inline is { } section && i < section.Count ? entry.Node : entry.Scope));
        }
        List<IReadOnlyList<uint>> meshes = [];
        foreach (var mesh in layout.Meshes)
        {
            List<uint> words = [];
            foreach (var primitive in mesh.Primitives)
            {
                token.ThrowIfCancellationRequested();
                uint word = ReadPolygonZoneValue(primitive.Material?.Extras?[Key], "the model");
                if (primitive.Extras?[Key]?["polygonZones"] is { } stored)
                {
                    var values = (System.Text.Json.Nodes.JsonObject)primitive.Extras[Key]!;
                    words.AddRange(ReadPolygonZoneValues(values, layout.Polygons[primitive].Count, "the model", token));
                }
                else
                {
                    if (primitive.Extras?[Key]?["polygonZonePadding"] != null || primitive.Extras?[Key]?["polygonZoneRaw"] != null)
                        throw new InvalidDataException("Polygon zone padding requires its polygonZones assignments.");
                    foreach (var polygon in layout.Polygons[primitive]) words.Add(word);
                }
            }
            meshes.Add(words.ToArray());
        }
        return new(layout.Fingerprint, layout.Nodes.Select(n => assigned[n]).ToArray(), meshes, loadRoot);
    }

    /// <summary>Validates topology and complete assignment counts without creating or changing a world.</summary>
    public static void ValidateZoneProfile(GltfDocument document, WorldZoneProfile profile, CancellationToken token = default) =>
        ValidateZoneProfile(ZoneLayout.Read(document, token), profile, "The model", token);

    internal static void ValidateZoneProfile(ZoneLayout layout, WorldZoneProfile profile, string path, CancellationToken token)
    {
        if (!string.Equals(profile.Fingerprint, layout.Fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException($"{path}: geometry topology no longer matches its map zone assignments. Restore the source hierarchy or re-extract the original ZBD files; a reference-aware Blender plugin is required for external geometry edits.");
        if (profile.Nodes.Count != layout.Nodes.Count || profile.MeshPolygons.Count != layout.Meshes.Count)
            throw new InvalidDataException($"{path}: map zone assignments do not cover the model's nodes and meshes.");
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (profile.Nodes[i] == null) throw new InvalidDataException($"{path}: a node zone assignment is absent.");
        }
        for (int i = 0; i < layout.Meshes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            long count = 0;
            foreach (var primitive in layout.Meshes[i].Primitives) count += layout.Polygons[primitive].Count;
            if (profile.MeshPolygons[i].Count != count)
                throw new InvalidDataException($"{path}: map zone assignments for mesh {i} do not cover its source polygons.");
        }
    }

    internal sealed class BoundZones
    {
        internal required string Identity { get; set; }
        /// <summary>For a reference placement's overrides (rather than the file's own assignments), the file and reference node holding them.</summary>
        internal (string File, int Node)? Placement { get; init; }
        internal Dictionary<GltfNode, WorldNodeZone> Nodes { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<GltfMesh, uint[]> Meshes { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<GltfPrimitive, List<int[]>> Polygons { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<GltfNode, string> References { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private static BoundZones? BindZones(GltfDocument document, string path, ImportContext context)
    {
        _ = SourceSceneValues(document, path);
        if (context.EmbeddedZoneScopes.Count > 0)
            foreach (var scope in context.EmbeddedZoneScopes.Peek().Overrides)
                if (!context.ValidatedZoneScopes.Contains((document, scope)))
                {
                    ValidateZoneOverrideScope(document, scope, context.Token);
                    context.ValidatedZoneScopes.Add((document, scope));
                }
        var scopes = context.EmbeddedZoneScopes.Count > 0 ? context.EmbeddedZoneScopes.Peek() : null;
        int chosen = -1;
        for (int i = 0; scopes != null && chosen < 0 && i < scopes.Overrides.Count; i++)
            if (scopes.Overrides[i]["fingerprint"] != null) chosen = i;
        var placement = chosen < 0 ? null : scopes!.Overrides[chosen];
        bool cached = placement == null;
        if (cached && context.ZoneBindings.TryGetValue((path, document), out var known)) return known;
        if (placement != null && context.EmbeddedZoneBindings.TryGetValue((document, placement), out var placed)) return placed;
        var supplied = context.ZoneProfile?.Invoke(path, document);
        bool embedded = HasEmbeddedZones(document);
        if (placement == null && supplied == null && !embedded)
        {
            context.ZoneBindings.Add((path, document), null);
            return null;
        }
        if (!context.ZoneLayouts.TryGetValue(document, out var layout))
        {
            layout = ZoneLayout.Read(document, context.Token, context.PolygonWork);
            context.ZoneLayouts.Add(document, layout);
        }
        var profile = placement != null ? ReadZoneOverride(placement, context.Token)
            : supplied ?? CaptureZoneProfile(document, layout, DefaultZone, null, context.Token);
        ValidateZoneProfile(layout, profile, path, context.Token);
        context.PolygonWork.Charge(layout.Nodes.Count + profile.MeshPolygons.Sum(m => (long)m.Count));
        Dictionary<GltfNode, string> references = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            context.Token.ThrowIfCancellationRequested();
            var node = layout.Nodes[i];
            if ((node.Extras?[Key] as System.Text.Json.Nodes.JsonObject)?[ZoneReference] is not { } marker) continue;
            if (!Flag(marker, ZoneReference, path) || node.Extras?[Key]?["ref"] != null)
                throw new InvalidDataException($"{path}: neutral reference {i} has conflicting or invalid reference fields.");
            string uri = context.AssetReference?.Invoke(path, i)
                ?? throw new InvalidDataException($"{path}: map reference {i} has no supplied authored URI.");
            if (uri.Length == 0 || uri.Length > 4096)
                throw new InvalidDataException($"{path}: map reference {i} has an empty or excessive URI.");
            references.Add(node, uri);
        }
        using ZoneHash identity = new(context.Token);
        identity.Text(layout.Fingerprint);
        BoundZones bound = new() { Identity = "", Placement = chosen < 0 ? null : scopes!.Holders[chosen] };
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            var assigned = profile.Nodes[i] ?? throw new InvalidDataException($"{path}: a node zone assignment is absent.");
            identity.Number(assigned.Inherit ? assigned.Word & ~255u : assigned.Word);
            identity.Number(assigned.Gate ? 1u : 0u); identity.Number(assigned.Inherit ? 1u : 0u);
            bound.Nodes.Add(layout.Nodes[i], assigned);
        }
        for (int i = 0; i < layout.Meshes.Count; i++)
        {
            var values = profile.MeshPolygons[i]; uint[] words = new uint[values.Count];
            for (int j = 0; j < words.Length; j++) { words[j] = values[j]; identity.Number(words[j]); }
            bound.Meshes.Add(layout.Meshes[i], words);
        }
        foreach (var (primitive, polygons) in layout.Polygons) bound.Polygons.Add(primitive, polygons);
        foreach (var (node, uri) in references) { bound.References.Add(node, uri); identity.Text(uri); }
        bound.Identity = identity.Finish();
        if (cached) context.ZoneBindings.Add((path, document), bound);
        else context.EmbeddedZoneBindings.Add((document, placement!), bound);
        return bound;
    }

    internal sealed class ZoneLayout
    {
        internal List<GltfNode> Nodes { get; } = [];
        internal List<GltfMesh> Meshes { get; } = [];
        internal Dictionary<GltfPrimitive, List<int[]>> Polygons { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>Each polygon's number of consecutive triangles in its primitive, in the order of <see cref="Polygons"/>.</summary>
        internal Dictionary<GltfPrimitive, List<int>> Triangles { get; } = new(ReferenceEqualityComparer.Instance);
        internal string Fingerprint { get; private set; } = "";

        internal static ZoneLayout Read(GltfDocument document, CancellationToken token, PolygonWorkBudget? supplied = null)
        {
            PolygonWorkBudget work = supplied ?? new(token);
            ZoneLayout result = new();
            Dictionary<GltfNode, int> nodes = new(ReferenceEqualityComparer.Instance);
            Dictionary<GltfMesh, int> meshes = new(ReferenceEqualityComparer.Instance);
            HashSet<GltfNode> active = new(ReferenceEqualityComparer.Instance);
            Stack<(GltfNode Node, int Depth, bool End)> pending = [];
            long edges = document.Roots.Count;
            if (edges > GltfDocument.MaximumNodes) throw new InvalidDataException("The map zone layout has too many roots.");
            for (int i = document.Roots.Count - 1; i >= 0; i--) pending.Push((document.Roots[i], 1, false));
            while (pending.TryPop(out var entry))
            {
                work.Charge(1);
                if (entry.End) { active.Remove(entry.Node); continue; }
                if (active.Contains(entry.Node)) throw new InvalidDataException("The map zone layout contains a cyclic hierarchy.");
                if (nodes.ContainsKey(entry.Node)) continue;
                if (entry.Depth > GltfDocument.MaximumDepth || result.Nodes.Count >= GltfDocument.MaximumNodes)
                    throw new InvalidDataException("The map zone layout exceeds the hierarchy allowance.");
                if ((edges += entry.Node.Children.Count) > GltfDocument.MaximumEntries)
                    throw new InvalidDataException("The map zone layout has too many child references.");
                nodes.Add(entry.Node, result.Nodes.Count); result.Nodes.Add(entry.Node);
                if (entry.Node.Mesh is { } mesh && meshes.TryAdd(mesh, result.Meshes.Count)) result.Meshes.Add(mesh);
                active.Add(entry.Node); pending.Push((entry.Node, entry.Depth, true));
                for (int i = entry.Node.Children.Count - 1; i >= 0; i--) pending.Push((entry.Node.Children[i], entry.Depth + 1, false));
            }
            using ZoneHash hash = new(token);
            hash.Text("recoil-map-zone-layout-1");
            hash.Number((uint)document.Roots.Count);
            foreach (var root in document.Roots) hash.Number((uint)nodes[root]);
            hash.Number((uint)result.Nodes.Count);
            foreach (var node in result.Nodes)
            {
                work.Charge(1L + node.Children.Count);
                hash.Text(EngineName(node));
                var engine = node.Extras?[Key] as System.Text.Json.Nodes.JsonObject;
                hash.Text(engine?["class"]?.ToString() ?? "object3d");
                hash.Text(engine?["instance"]?.ToString() ?? "");
                hash.Number(engine?[ZoneReference] == null && engine?["ref"] == null ? 0u : 1u);
                hash.Number(node.Mesh == null ? uint.MaxValue : (uint)meshes[node.Mesh]);
                hash.Number((uint)node.Children.Count);
                foreach (var child in node.Children) hash.Number((uint)nodes[child]);
            }
            hash.Number((uint)result.Meshes.Count);
            long primitives = 0, indices = 0;
            foreach (var mesh in result.Meshes)
            {
                if ((primitives += mesh.Primitives.Count) > GltfDocument.MaximumPrimitives)
                    throw new InvalidDataException("The map zone layout has too many primitives.");
                hash.Number((uint)mesh.Primitives.Count);
                foreach (var primitive in mesh.Primitives)
                {
                    if ((indices += primitive.Indices.Count) > GltfDocument.MaximumDecodedElements)
                        throw new InvalidDataException("The map zone layout has too many polygon indices.");
                    work.Charge(1L + primitive.Indices.Count + primitive.Targets.Count);
                    hash.Number((uint)primitive.Positions.Count); hash.Number((uint)primitive.Normals.Count);
                    hash.Number((uint)primitive.TexCoords.Count); hash.Number((uint)primitive.Targets.Count);
                    foreach (var target in primitive.Targets) hash.Number((uint)target.Count);
                    hash.Number((uint)primitive.Indices.Count);
                    foreach (int index in primitive.Indices)
                    {
                        if (index < 0 || index >= primitive.Positions.Count) throw new InvalidDataException("The map zone layout contains an invalid vertex index.");
                        hash.Number((uint)index);
                    }
                    bool recorded = (primitive.Extras?[Key] as System.Text.Json.Nodes.JsonObject)?["polygons"] != null;
                    List<int> counts = [];
                    var polygons = WorldGltf.Polygons(primitive, Textured(primitive), work, recorded, counts);
                    result.Polygons.TryAdd(primitive, polygons); result.Triangles.TryAdd(primitive, counts);
                    hash.Number((uint)polygons.Count);
                    foreach (var polygon in polygons)
                    {
                        work.Charge(1L + polygon.Length);
                        hash.Number((uint)polygon.Length);
                        foreach (int corner in polygon) hash.Number((uint)corner);
                    }
                }
            }
            result.Fingerprint = hash.Finish();
            return result;
        }

        /// <summary>Whether the layout reads a primitive's polygons as textured (fan merging then keeps texture mapping affine).</summary>
        internal static bool Textured(GltfPrimitive primitive) => primitive.Material?.ImageUri != null || primitive.Material?.Extras?[Key]?["texture"] != null;
    }

    private sealed class ZoneHash(CancellationToken token) : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long textBytes;
        internal void Number(uint value)
        {
            token.ThrowIfCancellationRequested();
            Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); hash.AppendData(bytes);
        }
        internal void Text(string value)
        {
            if (value.Length > GltfDocument.MaximumMetadataBytes - textBytes)
                throw new InvalidDataException("The map zone layout exceeds its metadata allowance.");
            int bytes = Encoding.UTF8.GetByteCount(value);
            if ((textBytes += bytes) > GltfDocument.MaximumMetadataBytes)
                throw new InvalidDataException("The map zone layout exceeds its metadata allowance.");
            Number((uint)bytes); hash.AppendData(Encoding.UTF8.GetBytes(value));
        }
        internal string Finish() => Convert.ToHexString(hash.GetHashAndReset());
        public void Dispose() => hash.Dispose();
    }
}
