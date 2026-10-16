using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public sealed record SourceMapZoneReference(int Node, string Asset, string Spelling);
public sealed record SourceMapZoneAsset(string LogicalPath, string GeometryPath, WorldZoneProfile Profile, IReadOnlyList<SourceMapZoneReference> References)
{
    public IReadOnlyList<int> OwnerReferenceChain { get; init; } = [];
}
public sealed record SourceMapZoneLabel(byte Id, string Label);

/// <summary>One bounded, workspace-backed view of embedded assignments and their reference occurrences.</summary>
internal sealed class SourceEmbeddedZones(SourceWorkspace workspace, CancellationToken token)
{
    internal const int MaximumAssets = 16_384, MaximumTargets = 2_000_000, MaximumLabelCharacters = 256;

    internal sealed record Model(string Path, byte[] Bytes, JsonObject Root, GltfDocument Document);
    internal sealed record Entry(string OwnerPath, IReadOnlyList<int> ReferenceChain, string ModelPath, WorldZoneProfile Profile, bool HasOverrides = false, IReadOnlyList<string>? AssignmentSites = null)
    {
        internal string Key => Identity(OwnerPath, ReferenceChain);
    }
    internal sealed record Mission(string MasterPath, IReadOnlyList<Entry> Entries, IReadOnlyList<SourceMapZoneLabel> Labels);
    private readonly Dictionary<string, Model> models = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SourceScriptInspection inspection = new(workspace, token);
    private readonly ScriptTraceBudget traceBudget = new();
    private readonly Dictionary<string, IReadOnlyList<IReadOnlyList<string>>?> scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<Entry>> closures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorldZoneProfile> defaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<JsonObject, WorldZoneProfile> overrides = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(GltfDocument Document, WorldZoneProfile Profile)> validatedProfiles = [];
    private readonly HashSet<(GltfDocument Document, JsonObject Scope)> validatedScopes = [];
    private long profileTargets, siteCharacters, siteCount;
    private long readBytes, occurrences, occurrenceTargets;
    private const long MaximumBytes = 512L << 20;

    internal static string Identity(string owner, IReadOnlyList<int> chain) => SourceWorkspace.Normalize(owner).ToUpperInvariant() + "|" + string.Join('/', chain);

    internal Model File(string path)
    {
        token.ThrowIfCancellationRequested(); path = SourceWorkspace.Normalize(path);
        if (models.TryGetValue(path, out var known)) return known;
        if (models.Count >= SourceEmbeddedZones.MaximumAssets) throw Bad("Too many referenced model files.");
        var bytes = Read(path, ProjectReadLimits.Model());
        var document = WorldAssembler.ReadModel(bytes, path, (buffer, remaining) => Read(buffer, ProjectReadLimits.Bytes(remaining)), token);
        var json = GltfDocument.ContainerJson(bytes, token, out _);
        var root = JsonNode.Parse(json, documentOptions: new() { MaxDepth = 64 }) as JsonObject ?? throw Bad("The source model is not a JSON object.");
        var model = new Model(path, bytes, root, document);
        models.Add(path, model); return model;
    }

    private byte[] Read(string path, ProjectReadLimits limits)
    {
        var bytes = workspace.Read(path, token, limits.WithMaximum(MaximumBytes - readBytes)) ?? throw Bad($"Source file {JsonData.ShownText(path)} is missing.");
        readBytes += bytes.LongLength; return bytes;
    }

    internal Entry Resolve(string owner, IReadOnlyList<int> chain, string expectedModel)
    {
        if (chain.Count > GltfDocument.MaximumDepth) throw Bad("The reference hierarchy is too deep.");
        string path = SourceWorkspace.Normalize(owner);
        List<JsonObject> scopes = [];
        HashSet<string> active = new(StringComparer.OrdinalIgnoreCase) { path };
        foreach (int index in chain)
        {
            token.ThrowIfCancellationRequested(); var file = File(path);
            var node = ReferenceNode(file, index);
            scopes = ChildScopes(scopes, node, index);
            path = WorldAssembler.Relative(path, Reference(node));
            if (!active.Add(path)) throw Bad("The model references contain a cycle.");
        }
        if (!path.Equals(SourceWorkspace.Normalize(expectedModel), StringComparison.OrdinalIgnoreCase))
            throw Bad("The reference occurrence no longer names the selected model. Rebuild the world.");
        return EntryFor(owner, chain, path, scopes);
    }

    internal IReadOnlyList<Entry> Closure(string owner)
    {
        owner = SourceWorkspace.Normalize(owner);
        if (closures.TryGetValue(owner, out var cached)) return cached;
        List<Entry> entries = [];
        HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
        Visit(owner, [], []);
        return closures[owner] = entries;
        void Visit(string path, int[] chain, List<JsonObject> scopes)
        {
            token.ThrowIfCancellationRequested();
            if (++occurrences > SourceEmbeddedZones.MaximumTargets || chain.Length > GltfDocument.MaximumDepth) throw Bad("The reference hierarchy exceeds its inspection allowance.");
            if (!active.Add(path)) throw Bad("The model references contain a cycle.");
            var file = File(path);
            var entry = EntryFor(owner, chain, path, scopes);
            long targets = entry.Profile.Nodes.Count + entry.Profile.MeshPolygons.Sum(m => (long)m.Count) + 1;
            if (targets > GltfDocument.MaximumDecodedElements - occurrenceTargets)
                throw Bad("Repeated model placements exceed the zone inspection work allowance.");
            occurrenceTargets += targets;
            entries.Add(entry);
            foreach (var node in file.Document.AllNodes())
            {
                token.ThrowIfCancellationRequested();
                if (node.Extras?[WorldGltf.Key]?["ref"] == null) continue;
                var json = ReferenceNode(file, node.Index);
                Visit(WorldAssembler.Relative(path, Reference(json)), [.. chain, node.Index], ChildScopes(scopes, json, node.Index));
            }
            active.Remove(path);
        }
    }

    private Entry EntryFor(string owner, IReadOnlyList<int> chain, string path, List<JsonObject> scopes)
    {
        var file = File(path);
        foreach (var scope in scopes)
            if (validatedScopes.Add((file.Document, scope))) WorldGltf.ValidateZoneOverrideScope(file.Document, scope, token);
        var assigned = scopes.FirstOrDefault(s => s["fingerprint"] != null);
        WorldZoneProfile profile;
        if (assigned == null)
        {
            if (!defaults.TryGetValue(path, out profile!))
            {
                var layout = WorldGltf.ZoneLayout.Read(file.Document, token);
                ReserveProfile(layout.Nodes.Count + layout.Polygons.Values.Sum(p => (long)p.Count));
                defaults.Add(path, profile = WorldGltf.CaptureZoneProfile(file.Document, token: token));
            }
        }
        else if (!overrides.TryGetValue(assigned, out profile!))
        {
            long targets = (assigned["nodes"] as JsonArray)?.Count ?? 0;
            if (assigned["meshes"] is JsonArray meshes)
                foreach (var mesh in meshes)
                {
                    token.ThrowIfCancellationRequested();
                    targets += (mesh as JsonObject)?["polygonZones"] is JsonArray polygons ? polygons.Count : 0;
                }
            ReserveProfile(targets);
            overrides.Add(assigned, profile = WorldGltf.ReadZoneOverride(assigned, token));
        }
        if (validatedProfiles.Add((file.Document, profile))) WorldGltf.ValidateZoneProfile(file.Document, profile, token);
        return new(SourceWorkspace.Normalize(owner), chain.ToArray(), path, profile, scopes.Count > 0, AssignmentSites(owner, chain));
    }
    // Precedence is outer occurrence, nested file occurrence, then the model defaults. A write at
    // any site before the first authored profile changes this occurrence; later sites are shadowed.
    private IReadOnlyList<string> AssignmentSites(string owner, IReadOnlyList<int> chain)
    {
        List<string> result = []; string path = SourceWorkspace.Normalize(owner);
        for (int start = 0; start <= chain.Count; start++)
        {
            token.ThrowIfCancellationRequested();
            if (++siteCount > SourceEmbeddedZones.MaximumTargets || path.Length + 12L * (chain.Count - start) > (16L << 20) - siteCharacters)
                throw Bad("The reference assignment identities exceed their inspection allowance.");
            siteCharacters += path.Length + 12L * (chain.Count - start);
            result.Add(Identity(path, chain.Skip(start).ToArray()));
            if (start == chain.Count) break;
            var node = ReferenceNode(File(path), chain[start]);
            JsonObject? scope = node["extras"]?[WorldGltf.Key]?["zoneOverrides"] as JsonObject;
            for (int index = start + 1; scope != null && index < chain.Count; index++)
                scope = scope["references"]?[chain[index].ToString(CultureInfo.InvariantCulture)] as JsonObject;
            if (scope?["fingerprint"] != null) break;
            path = WorldAssembler.Relative(path, Reference(node));
        }
        return result;
    }

    private void ReserveProfile(long count)
    {
        if (count > SourceEmbeddedZones.MaximumTargets - profileTargets) throw Bad("Embedded zone profiles exceed their aggregate target allowance.");
        profileTargets += count;
    }

    private static List<JsonObject> ChildScopes(List<JsonObject> parents, JsonObject node, int index)
    {
        List<JsonObject> scopes = [];
        foreach (var parent in parents)
        {
            if (parent["references"] is { } references && references is not JsonObject) throw Bad("Nested reference overrides must be an object.");
            if (parent["references"]?[index.ToString(CultureInfo.InvariantCulture)] is JsonObject nested) scopes.Add(nested);
            else if (parent["references"]?[index.ToString(CultureInfo.InvariantCulture)] != null) throw Bad("A reference override must be an object.");
        }
        if (node["extras"]?[WorldGltf.Key]?["zoneOverrides"] is JsonObject local) scopes.Add(local);
        else if (node["extras"]?[WorldGltf.Key]?["zoneOverrides"] != null) throw Bad("A reference override must be an object.");
        return scopes;
    }
    private static JsonObject ReferenceNode(Model model, int index) => model.Root["nodes"] is JsonArray nodes && index >= 0 && index < nodes.Count && nodes[index] is JsonObject node
        && node["extras"]?[WorldGltf.Key]?["ref"] != null ? node : throw Bad("A source reference no longer exists. Rebuild the world.");
    private static string Reference(JsonObject node) => node["extras"]?[WorldGltf.Key]?["ref"] is JsonValue value && value.TryGetValue<string>(out var uri)
        ? uri : throw Bad("A model reference must contain a relative path.");

    internal Mission ReadMission(string mission, bool includeEntries = true)
    {
        if (!SourceProject.MissionName().IsMatch(mission)) throw Bad("The mission name is invalid.");
        List<string> roots = []; HashSet<string> seenRoots = new(StringComparer.OrdinalIgnoreCase);
        string? master = null; bool database = false;
        ScriptTrace.Visit(Script, mission + ".gs", step =>
        {
            token.ThrowIfCancellationRequested();
            if (step.Command == "GameGenSetWorld") database = true;
            if (step is not { Command: "LoadGameGen", Args: [var name, _, ..] }) return;
            string path = WorldAssembler.ResolveModel(name, step.ModelDirectories, p => workspace.Exists(p, token))
                ?? throw Bad($"The mission's model {JsonData.ShownText(name)} is missing.");
            if (database) { master ??= path; database = false; }
            if (seenRoots.Add(path)) roots.Add(path);
        }, token, budget: traceBudget);
        if (master == null) throw Bad("The mission script does not identify a master world model.");
        List<Entry> entries = [];
        if (includeEntries) foreach (var root in roots) entries.AddRange(Closure(root));
        return new(master, entries, Labels(File(master).Root));
        IReadOnlyList<IReadOnlyList<string>>? Script(string name)
        {
            string path = "gamegen/" + name.Replace('\\', '/');
            if (scripts.TryGetValue(path, out var known)) return known;
            var parsed = inspection.Read(path);
            return scripts[path] = parsed == null ? throw Bad($"Mission script {JsonData.ShownText(path)} is missing.") : [.. parsed.Lines.Where(l => l.IsInstruction).Select(l => l.Tokens)];
        }
    }

    internal static JsonObject SceneEngine(JsonObject root, GltfDocument document)
    {
        if (root["scenes"] == null || root["scenes"] is JsonArray { Count: 0 })
        {
            // The shared parser has already validated and selected the implicit roots. Merely
            // naming a zone must retain those roots, rather than introduce an empty scene.
            root["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(document.Roots.Select(n => (JsonNode?)JsonValue.Create(n.Index)).ToArray()) });
            root["scene"] = 0;
        }
        int selected = root["scene"] is JsonValue value && value.TryGetValue<int>(out int index) ? index : 0;
        if (root["scenes"] is not JsonArray scenes || selected < 0 || selected >= scenes.Count || scenes[selected] is not JsonObject scene)
            throw Bad("The master model has no selected scene.");
        return Object(Object(scene, "extras"), WorldGltf.Key);
    }
    private static IReadOnlyList<SourceMapZoneLabel> Labels(JsonObject root)
    {
        // Reading a catalog is not an attribute initialization. Scene-less glTF files are valid.
        int selected = root["scene"] is JsonValue chosen && chosen.TryGetValue<int>(out int index) ? index : 0;
        if (root["scenes"] is not JsonArray { Count: > 0 } scenes) return [new(255, "Any")];
        if (selected < 0 || selected >= scenes.Count) throw Bad("The master model has no selected scene.");
        if (scenes[selected]?["extras"]?[WorldGltf.Key]?["zoneLabels"] is not { } value) return [new(255, "Any")];
        if (value is not JsonArray array || array.Count > 256) throw Bad("Invalid zone catalog.");
        List<SourceMapZoneLabel> labels = []; HashSet<byte> seen = [];
        foreach (var entry in array)
        {
            if (entry?["id"] is not JsonValue id || !id.TryGetValue<byte>(out var number) || !seen.Add(number)
                || entry?["label"] is not JsonValue label || !label.TryGetValue<string>(out var text)
                || text.Length is < 1 or > SourceEmbeddedZones.MaximumLabelCharacters || number == 255 && text != "Any") throw Bad("Invalid zone catalog label.");
            labels.Add(new(number, text));
        }
        return labels;
    }
    internal void SetLabels(string master, IEnumerable<SourceMapZoneLabel> labels)
    {
        JsonArray values = [];
        foreach (var label in labels.OrderBy(l => l.Id)) { token.ThrowIfCancellationRequested(); values.Add(new JsonObject { ["id"] = label.Id, ["label"] = label.Label }); }
        var file = File(master);
        SceneEngine(file.Root, file.Document)["zoneLabels"] = values; changed.Add(master);
    }

    /// <summary>The override physically authored by this owner, without inherited scopes from referenced files.</summary>
    internal JsonObject? OwnedOverride(Entry entry)
    {
        if (entry.ReferenceChain.Count == 0) return null;
        JsonNode? scope = ReferenceNode(File(entry.OwnerPath), entry.ReferenceChain[0])["extras"]?[WorldGltf.Key]?["zoneOverrides"];
        for (int i = 1; scope != null && i < entry.ReferenceChain.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            scope = scope["references"]?[entry.ReferenceChain[i].ToString(System.Globalization.CultureInfo.InvariantCulture)];
        }
        return scope as JsonObject;
    }

    internal void Set(Entry entry, WorldZoneProfile profile, GltfDocument? expectedDocument = null)
    {
        var owner = File(entry.OwnerPath);
        WorldGltf.ValidateZoneProfile(expectedDocument ?? File(entry.ModelPath).Document, profile, token);
        if (entry.ReferenceChain.Count == 0) WorldGltf.PatchEmbeddedZones(owner.Root, owner.Document, profile, token);
        else
        {
            var at = Object(Object(Object(ReferenceNode(owner, entry.ReferenceChain[0]), "extras"), WorldGltf.Key), "zoneOverrides");
            for (int i = 1; i < entry.ReferenceChain.Count; i++) at = Object(Object(at, "references"), entry.ReferenceChain[i].ToString(CultureInfo.InvariantCulture));
            var updated = WorldGltf.WriteZoneOverride(profile, token);
            foreach (var property in updated) at[property.Key] = property.Value?.DeepClone();
        }
        changed.Add(entry.OwnerPath);
    }
    internal void ReplaceFile(string path, JsonObject root, GltfDocument document)
    {
        var original = File(path); models[path] = original with { Root = root, Document = document };
    }
    internal void MarkChanged(string path) => changed.Add(path);
    internal IReadOnlyList<(string Relative, byte[] Content)> Changes()
    {
        List<(string Relative, byte[] Content)> result = [];
        foreach (string path in changed.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested(); var model = File(path);
            byte[] output = Repack(model.Bytes, model.Root, token);
            if (!output.AsSpan().SequenceEqual(model.Bytes)) result.Add((path, output));
        }
        workspace.TrackMapZoneSources(result.Select(c => c.Relative).ToArray(), result, token);
        return result;
    }
    /// <summary>Replace only the JSON of a validated source container, retaining every GLB payload chunk.</summary>
    internal static byte[] Repack(ReadOnlySpan<byte> original, JsonObject root, CancellationToken token)
    {
        _ = GltfDocument.ContainerJson(original, token, out _);
        byte[] json = GltfJson.Write(root, original[..Math.Min(original.Length, 4096)].Contains((byte)'\n'), token);
        if (!original.StartsWith("glTF"u8)) return json;
        int originalJson = BinaryPrimitives.ReadInt32LittleEndian(original[12..]);
        int padded = checked((json.Length + 3) & ~3), tail = 20 + originalJson;
        byte[] output = new byte[checked(20 + padded + original.Length - tail)];
        original[..20].CopyTo(output); BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), padded); output.AsSpan(20, padded).Fill((byte)' ');
        json.CopyTo(output.AsSpan(20)); original[tail..].CopyTo(output.AsSpan(20 + padded));
        return output;
    }

    private static JsonObject Object(JsonObject parent, string name)
    {
        if (parent[name] == null) parent[name] = new JsonObject();
        return parent[name] as JsonObject ?? throw Bad($"Invalid embedded {name} object.");
    }
    private static InvalidDataException Bad(string message) => new("Map zones: " + message);
}
