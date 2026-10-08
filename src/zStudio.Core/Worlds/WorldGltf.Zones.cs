using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Recoil.Zbd.Core.Gltf;

namespace Recoil.Zbd.Core.Worlds;

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
        if (context.Zones != null || context.Meshes.Count != 0 || context.Materials.Count != 0)
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
    /// Captures legacy inline assignments without rewriting geometry. The caller supplies the actual load's inherited
    /// zone, and must not collapse uses whose inherited assignments differ into one logical profile.
    /// </summary>
    public static WorldZoneProfile CaptureZoneProfile(GltfDocument document, uint parentZone = DefaultZone,
        WorldNodeZone? loadRoot = null, CancellationToken token = default)
    {
        var layout = ZoneLayout.Read(document, token);
        Dictionary<GltfNode, WorldNodeZone> assigned = new(ReferenceEqualityComparer.Instance);
        Dictionary<long, WorldNodeZone> instances = [];
        Stack<(GltfNode Node, uint Parent)> pending = [];
        for (int i = document.Roots.Count - 1; i >= 0; i--) pending.Push((document.Roots[i], parentZone & 0xFF));
        while (pending.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();
            if (assigned.ContainsKey(entry.Node)) continue;
            var extras = entry.Node.Extras?[Key];
            uint? word = extras?["zoneWord"] is { } w ? Hex(w, "zoneWord", "the model") : null;
            uint low = extras?["zone"] is { } z ? (uint)Integer(z, "zone", "the model") & 0xFF : word is { } full ? full & 0xFF : entry.Parent;
            uint flags = extras?["flags"] is { } f ? Hex(f, "flags", "the model") : DefaultCarried;
            WorldNodeZone zone = new(word is { } whole ? whole & ~0xFFu | low : low, (flags & ZoneGate) != 0, extras?["zone"] == null && word == null);
            if (extras?["instance"] is { } instance)
            {
                long number = Integer(instance, "instance", "the model", 1, int.MaxValue);
                if (!instances.TryAdd(number, zone)) zone = instances[number];
            }
            assigned.Add(entry.Node, zone);
            for (int i = entry.Node.Children.Count - 1; i >= 0; i--) pending.Push((entry.Node.Children[i], zone.Word & 0xFF));
        }
        List<IReadOnlyList<uint>> meshes = [];
        foreach (var mesh in layout.Meshes)
        {
            List<uint> words = [];
            foreach (var primitive in mesh.Primitives)
            {
                token.ThrowIfCancellationRequested();
                uint word = primitive.Material?.Extras?[Key]?["zone"] is { } z ? Hex(z, "zone", "the model") : DefaultPolygonZone;
                foreach (var polygon in layout.Polygons[primitive]) words.Add(word);
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
            throw new InvalidDataException($"{path}: geometry topology no longer matches its map zone assignments; rebind the edited geometry before importing.");
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
        internal Dictionary<GltfNode, WorldNodeZone> Nodes { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<GltfMesh, uint[]> Meshes { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<GltfPrimitive, List<int[]>> Polygons { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<GltfNode, string> References { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private static BoundZones? BindZones(GltfDocument document, string path, ImportContext context)
    {
        if (context.ZoneBindings.TryGetValue((path, document), out var known)) return known;
        if (context.ZoneProfile?.Invoke(path, document) is not { } profile)
        {
            // Legacy documents keep their inline assignments. Neutral reference holders cannot lose their bindings.
            foreach (var node in document.AllNodes())
            {
                context.Token.ThrowIfCancellationRequested();
                if ((node.Extras?[Key] as System.Text.Json.Nodes.JsonObject)?[ZoneReference] != null)
                    throw new InvalidDataException($"{path}: this model needs its map zone manifest before it can be imported.");
            }
            context.ZoneBindings.Add((path, document), null);
            return null;
        }
        if (!context.ZoneLayouts.TryGetValue(document, out var layout))
        {
            layout = ZoneLayout.Read(document, context.Token, context.PolygonWork);
            context.ZoneLayouts.Add(document, layout);
        }
        ValidateZoneProfile(layout, profile, path, context.Token);
        context.PolygonWork.Charge(layout.Nodes.Count + profile.MeshPolygons.Sum(m => (long)m.Count));
        Dictionary<GltfNode, string> references = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            context.Token.ThrowIfCancellationRequested();
            var node = layout.Nodes[i];
            if (node.Extras?[Key]?[ZoneReference] is not { } marker) continue;
            if (!Flag(marker, ZoneReference, path) || node.Extras?[Key]?["ref"] != null)
                throw new InvalidDataException($"{path}: neutral reference {i} has conflicting or invalid reference fields.");
            string uri = context.AssetReference?.Invoke(path, i)
                ?? throw new InvalidDataException($"{path}: map reference {i} has no authored URI in its zone manifest.");
            if (uri.Length == 0 || uri.Length > 4096)
                throw new InvalidDataException($"{path}: map reference {i} has an empty or excessive URI.");
            references.Add(node, uri);
        }
        using ZoneHash identity = new(context.Token);
        identity.Text(layout.Fingerprint);
        BoundZones bound = new() { Identity = "" };
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            var assigned = profile.Nodes[i] ?? throw new InvalidDataException($"{path}: a node zone assignment is absent.");
            identity.Number(assigned.Word); identity.Number(assigned.Gate ? 1u : 0u); identity.Number(assigned.Inherit ? 1u : 0u);
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
        context.ZoneBindings.Add((path, document), bound);
        return bound;
    }

    internal sealed class ZoneLayout
    {
        internal List<GltfNode> Nodes { get; } = [];
        internal List<GltfMesh> Meshes { get; } = [];
        internal Dictionary<GltfPrimitive, List<int[]>> Polygons { get; } = new(ReferenceEqualityComparer.Instance);
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
                var engine = node.Extras?[Key];
                hash.Text(engine?["class"]?.ToString() ?? "object3d");
                hash.Text(engine?["instance"]?.ToString() ?? "");
                hash.Number(engine?[ZoneReference] == null ? 0u : 1u);
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
                    bool recorded = primitive.Extras?[Key]?["polygons"] != null;
                    var polygons = WorldGltf.Polygons(primitive, primitive.Material?.ImageUri != null || primitive.Material?.Extras?[Key]?["texture"] != null, work, recorded);
                    result.Polygons.Add(primitive, polygons);
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
