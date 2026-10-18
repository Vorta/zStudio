using System.Globalization;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;

namespace zStudio.Core.Worlds;

public static partial class WorldGltf
{
    internal static bool HasEmbeddedZones(GltfDocument document) =>
        document.SceneExtras?[Key] is JsonObject scene && scene["sourceFormat"] != null
        || document.AllNodes().Any(node => node.Mesh?.Primitives.Any(p => (p.Extras?[Key] as JsonObject)?["polygonZones"] != null) == true);
    /// <summary><paramref name="Holders"/> names, per override, the file and reference node that authored it.</summary>
    internal sealed record EmbeddedZoneScope(string OwnerPath, IReadOnlyList<int> References, IReadOnlyList<JsonObject> Overrides,
        IReadOnlyList<(string File, int Node)> Holders);
    private sealed class EmbeddedZoneLease(ImportContext context, bool pushed) : IDisposable
    {
        public void Dispose() { if (pushed) context.EmbeddedZoneScopes.Pop(); }
    }
    private static IDisposable EnterEmbeddedZones(ImportContext context, string path)
    {
        bool push = context.EmbeddedZoneScopes.Count == 0;
        if (push) context.EmbeddedZoneScopes.Push(new(path, [], [], []));
        return new EmbeddedZoneLease(context, push);
    }
    private static IDisposable PushZoneOverrides(ImportContext context, GltfNode source, GltfDocument referenced,
        string referencedPath, string ownerPath)
    {
        var parent = context.EmbeddedZoneScopes.Peek();
        List<JsonObject> scopes = [];
        List<(string File, int Node)> holders = [];
        for (int i = 0; i < parent.Overrides.Count; i++)
        {
            var scope = parent.Overrides[i];
            if (scope["references"] is { } refs && refs is not JsonObject)
                throw new InvalidDataException($"{ownerPath}: nested reference zone overrides must be an object.");
            var inherited = scope["references"]?[source.Index.ToString(CultureInfo.InvariantCulture)];
            if (inherited is JsonObject value) { scopes.Add(value); holders.Add(parent.Holders[i]); }
            else if (inherited != null) throw new InvalidDataException($"{ownerPath}: a reference zone override must be an object.");
        }
        var authored = source.Extras?[Key]?["zoneOverrides"];
        if (authored is JsonObject own) { scopes.Add(own); holders.Add((ownerPath, source.Index)); }
        else if (authored != null) throw new InvalidDataException($"{ownerPath}: a reference zone override must be an object.");
        context.EmbeddedZoneScopes.Push(new(parent.OwnerPath, [.. parent.References, source.Index], scopes, holders));
        return new EmbeddedZoneLease(context, true);
    }

    /// <summary>Writes attributes beside the nodes and polygon boundaries they describe, preserving actual reference URIs.</summary>
    public static void EmbedZones(GltfDocument document, WorldZoneProfile profile,
        IReadOnlyDictionary<int, string>? references = null, CancellationToken token = default)
    {
        AdmitEmbeddedProfile(profile, token);
        var layout = ZoneLayout.Read(document, token);
        ValidateZoneProfile(layout, profile, "The source model", token);
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var node = layout.Nodes[i];
            node.Extras ??= [];
            var values = EmbeddedValues(node.Extras);
            PutNodeZone(values, profile.Nodes[i]);
            if (references?.TryGetValue(i, out var uri) == true)
            { values.Remove(ZoneReference); values["ref"] = uri; }
        }
        for (int i = 0; i < layout.Meshes.Count; i++)
        {
            int offset = 0;
            foreach (var primitive in layout.Meshes[i].Primitives)
            {
                token.ThrowIfCancellationRequested();
                primitive.Extras ??= [];
                uint[] words = new uint[layout.Polygons[primitive].Count];
                for (int j = 0; j < words.Length; j++) words[j] = profile.MeshPolygons[i][offset++];
                WritePolygonZoneValues(EmbeddedValues(primitive.Extras), words, token);
            }
        }
        document.SceneExtras ??= [];
        var scene = EmbeddedValues(document.SceneExtras);
        scene["sourceFormat"] = 4;
        PutLoadRootZone(scene, profile.LoadRoot);
    }

    /// <summary>Updates JSON attributes only; buffer URIs, accessors, vertex bytes and unrelated extras remain intact.</summary>
    public static void PatchEmbeddedZones(JsonObject root, GltfDocument document, WorldZoneProfile profile,
        CancellationToken token = default)
    {
        AdmitEmbeddedProfile(profile, token);
        var layout = ZoneLayout.Read(document, token);
        ValidateZoneProfile(layout, profile, "The source model", token);
        var nodes = root["nodes"] as JsonArray ?? throw new InvalidDataException("The source model has no node array.");
        var meshes = root["meshes"] as JsonArray;
        Dictionary<GltfMesh, int> meshIndices = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var node = layout.Nodes[i];
            if (node.Index < 0 || node.Index >= nodes.Count || nodes[node.Index] is not JsonObject stored)
                throw new InvalidDataException("The source node identity no longer matches its JSON document.");
            PutNodeZone(EmbeddedValues(EnsureObject(stored, "extras")), profile.Nodes[i]);
            if (node.Mesh is { } mesh) meshIndices.TryAdd(mesh, checked((int)Integer(stored["mesh"], "mesh", "the source model", 0, int.MaxValue)));
        }
        for (int i = 0; i < layout.Meshes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            int meshIndex = meshIndices[layout.Meshes[i]], offset = 0;
            if (meshes == null || meshIndex >= meshes.Count || meshes[meshIndex]?["primitives"] is not JsonArray primitives || primitives.Count != layout.Meshes[i].Primitives.Count)
                throw new InvalidDataException("The source mesh identity no longer matches its JSON document.");
            for (int j = 0; j < primitives.Count; j++)
            {
                var stored = primitives[j] as JsonObject ?? throw new InvalidDataException("The source primitive is invalid.");
                uint[] words = new uint[layout.Polygons[layout.Meshes[i].Primitives[j]].Count];
                for (int k = 0; k < words.Length; k++) words[k] = profile.MeshPolygons[i][offset++];
                WritePolygonZoneValues(EmbeddedValues(EnsureObject(stored, "extras")), words, token);
            }
        }
        var scenes = root["scenes"] as JsonArray;
        if (scenes == null || scenes.Count == 0)
        {
            JsonArray roots = [];
            foreach (var node in document.Roots) roots.Add(node.Index);
            scenes = [new JsonObject { ["nodes"] = roots }];
            root["scenes"] = scenes; root["scene"] = 0;
        }
        int sceneIndex = root["scene"] is { } selected ? checked((int)Integer(selected, "scene", "the source model", 0, int.MaxValue)) : 0;
        if (sceneIndex >= scenes.Count || scenes[sceneIndex] is not JsonObject scene) throw new InvalidDataException("The source scene is invalid.");
        var values = EmbeddedValues(EnsureObject(scene, "extras"));
        values["sourceFormat"] = 4;
        PutLoadRootZone(values, profile.LoadRoot);
    }

    /// <summary>A placement override belongs to its reference node, and is checked against the referenced topology.</summary>
    public static JsonObject WriteZoneOverride(WorldZoneProfile profile, CancellationToken token = default)
    {
        AdmitEmbeddedProfile(profile, token);
        JsonArray nodes = [], meshes = [];
        foreach (var node in profile.Nodes) { token.ThrowIfCancellationRequested(); nodes.Add(WriteEmbeddedNode(node)); }
        foreach (var mesh in profile.MeshPolygons)
        {
            JsonObject values = [];
            WritePolygonZoneValues(values, mesh, token); meshes.Add(values);
        }
        JsonObject result = new() { ["fingerprint"] = profile.Fingerprint, ["nodes"] = nodes, ["meshes"] = meshes };
        if (profile.LoadRoot is { } root) result["loadRoot"] = WriteEmbeddedNode(root);
        return result;
    }

    public static WorldZoneProfile ReadZoneOverride(JsonObject value, CancellationToken token = default)
    {
        if (value["fingerprint"] is not JsonValue fingerprint || !fingerprint.TryGetValue<string>(out var hash) || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("A reference zone override needs its structural fingerprint.");
        if (value["nodes"] is not JsonArray nodes || nodes.Count > GltfDocument.MaximumNodes || value["meshes"] is not JsonArray meshes || meshes.Count > GltfDocument.MaximumNodes)
            throw new InvalidDataException("The reference zone override has invalid node or mesh assignments.");
        List<WorldNodeZone> nodeValues = [];
        foreach (var node in nodes) { token.ThrowIfCancellationRequested(); nodeValues.Add(ReadEmbeddedNode(node)); }
        List<IReadOnlyList<uint>> meshValues = []; long count = nodes.Count;
        foreach (var mesh in meshes)
        {
            token.ThrowIfCancellationRequested();
            if (mesh is not JsonObject fields || fields["polygonZones"] is not JsonArray words)
                throw new InvalidDataException("Reference polygon zones require current embedded assignments. Reconstruct older projects from the original ZBD files.");
            if ((count += words.Count) > GltfDocument.MaximumDecodedElements)
                throw new InvalidDataException("The reference zone override exceeds its polygon allowance.");
            uint[] values = ReadPolygonZoneValues(fields, words.Count, "the reference", token);
            meshValues.Add(values);
        }
        return new(hash, nodeValues, meshValues, value["loadRoot"] is { } root ? ReadEmbeddedLoadRoot(root) : null);
    }

    internal static void ValidateZoneOverrideScope(GltfDocument document, JsonObject scope, CancellationToken token)
    {
        foreach (var property in scope)
        {
            token.ThrowIfCancellationRequested();
            if (property.Key is not ("fingerprint" or "nodes" or "meshes" or "loadRoot" or "references"))
                throw new InvalidDataException("A reference zone override contains an unknown attribute.");
        }
        if (scope["fingerprint"] == null && (scope["nodes"] != null || scope["meshes"] != null || scope["loadRoot"] != null))
            throw new InvalidDataException("Reference assignments require their source layout fingerprint.");
        if (scope["references"] == null) return;
        if (scope["references"] is not JsonObject references || references.Count > GltfDocument.MaximumNodes)
            throw new InvalidDataException("Nested reference zone overrides must be a bounded object.");
        var nodes = document.AllNodes().Where(n => (n.Extras?[Key] as JsonObject)?["ref"] != null).Select(n => n.Index).ToHashSet();
        foreach (var property in references)
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(property.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || property.Key != index.ToString(CultureInfo.InvariantCulture) || !nodes.Contains(index) || property.Value is not JsonObject)
                throw new InvalidDataException("A nested zone override no longer identifies a source reference. Rebuild or restore the source hierarchy.");
        }
    }

    public static WorldNodeZone? EmbeddedLoadRoot(GltfDocument document) =>
        SourceSceneValues(document, "the scene")?["loadRoot"] is { } value ? ReadEmbeddedLoadRoot(value) : null;

    private static WorldNodeZone ReadEmbeddedLoadRoot(JsonNode value)
    {
        // A load root may carry flags alone; its missing zone is the engine's unconstrained root zone.
        if (value is JsonObject fields && !fields.ContainsKey("zone"))
            return new(ReadNodeZoneValue(fields, DefaultZone, "the source load root"), (StoredNodeFlags(fields) & ZoneGate) != 0);
        var root = ReadEmbeddedNode(value);
        if (root.Inherit) throw new InvalidDataException("A source load-root zone cannot inherit from a model parent.");
        return root;
    }

    private static void AdmitEmbeddedProfile(WorldZoneProfile profile, CancellationToken token)
    {
        if (profile.Nodes.Count > GltfDocument.MaximumNodes || profile.MeshPolygons.Count > GltfDocument.MaximumNodes
            || profile.Fingerprint.Length != 64 || profile.Fingerprint.Any(c => !Uri.IsHexDigit(c)) || profile.LoadRoot is { Inherit: true })
            throw new InvalidDataException("The source zone profile has invalid layout metadata.");
        long bytes = 256L + 128L * profile.Nodes.Count;
        foreach (var mesh in profile.MeshPolygons)
        {
            token.ThrowIfCancellationRequested(); bytes += 64;
            foreach (uint word in mesh)
            {
                token.ThrowIfCancellationRequested(); bytes += PolygonZoneMetadataBytes(word);
                if (bytes > GltfDocument.MaximumMetadataBytes) throw new InvalidDataException("The source zone attributes exceed their metadata allowance.");
            }
        }
        if (bytes > GltfDocument.MaximumMetadataBytes) throw new InvalidDataException("The source zone attributes exceed their metadata allowance.");
    }

    private static JsonObject EmbeddedValues(JsonObject extras) => EnsureObject(extras, Key);
    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject value) return value;
        if (parent[key] != null) throw new InvalidDataException($"The source attribute {key} must be an object.");
        JsonObject created = []; parent[key] = created; return created;
    }
    private static JsonObject WriteEmbeddedNode(WorldNodeZone node)
    {
        JsonObject result = [];
        if (node.Gate) result["altitudeZoneCheck"] = true;
        WriteNodeZoneValue(result, node.Word, node.Inherit); return result;
    }
    private static WorldNodeZone ReadEmbeddedNode(JsonNode? node)
    {
        if (node is not JsonObject value) throw new InvalidDataException("A source node zone must be an object.");
        bool inherited = NodeZoneIsInherited(value, "the source zone");
        if (value["zone"] == null)
            throw new InvalidDataException("A source node zone needs a numeric zone ID, \"any\" or \"inherit\".");
        return new(ReadNodeZoneValue(value, DefaultZone, "the source zone"), (StoredNodeFlags(value) & ZoneGate) != 0,
            inherited);
    }
    private static void PutNodeZone(JsonObject values, WorldNodeZone zone)
    {
        WriteNodeZoneValue(values, zone.Word, zone.Inherit);
        uint flags = StoredNodeFlags(values);
        WriteNodeFlags(values, zone.Gate ? flags | ZoneGate : flags & ~ZoneGate);
    }

    private static void PutLoadRootZone(JsonObject scene, WorldNodeZone? zone)
    {
        if (zone is { } value) PutNodeZone(EnsureObject(scene, "loadRoot"), value);
        else if (scene["loadRoot"] is JsonObject root)
        {
            // Zone-only edits must retain the independently authored load-root flags.
            foreach (string key in new[] { "zone", "zonePadding" }) root.Remove(key);
            if (root.Count == 0) scene.Remove("loadRoot");
        }
    }
}
