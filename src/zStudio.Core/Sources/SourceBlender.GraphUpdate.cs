using System.Globalization;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceBlender
{
    private static BlenderUpdatePlan PlanGraphUpdate(SourceWorkspace workspace, BlenderCheckout checkout, string? export, bool force,
        Func<BlenderConflictException, CancellationToken, bool>? confirm, CancellationToken token)
    {
        RequireCurrentCheckout(checkout);
        var prepared = workspace.IsPrepared ? null : workspace.BeginPreparedEdit();
        var view = prepared?.Workspace ?? workspace;
        var exports = Exports(checkout, token);
        var selected = export == null ? exports.FirstOrDefault() : exports.FirstOrDefault(e => e.Relative.Equals(SourceWorkspace.Normalize(export), StringComparison.OrdinalIgnoreCase));
        if (selected == null) throw new InvalidDataException("No completed RECOIL graph export was found in this checkout's outbox.");
        string generation = Path.GetDirectoryName(selected.Gltf)!;
        var receiptBytes = SourceRead.All(selected.Gltf, GltfDocument.MaximumJsonBytes, token);
        var receipt = Parse(receiptBytes, selected.Relative, token);
        if (receipt["format"]?.GetValue<string>() != "zstudio-recoil-export" || receipt["version"]?.GetValue<int>() != GraphProtocol || receipt["root"]?.GetValue<string>() != checkout.Model)
            throw new InvalidDataException("The Blender export belongs to another source graph or extension version.");
        string? import = receipt["import"]?.GetValue<string>(), exportGeneration = receipt["generation"]?.GetValue<string>();
        // The generations the exporting Blender file made before this one; null from extensions that do not record them.
        HashSet<string>? lineage = receipt["lineage"] is JsonArray earlier ? earlier.Select(g => g!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) : null;
        Graph exported = new((path, maximum) => SourceRead.All(GraphPath(generation, path), maximum, token), token);
        exported.Load(checkout.Model);
        var exportedModels = receipt["models"] as JsonArray ?? throw new InvalidDataException("The export has no physical source inventory.");
        if (exportedModels.Count > 4096) throw new InvalidDataException("The export has too many physical source files.");
        foreach (var model in exportedModels) exported.Load(model!["path"]!.GetValue<string>());
        var inventory = receipt["files"] as JsonArray ?? throw new InvalidDataException("The export has no completed file inventory.");
        if (inventory.Count != exported.Files.Count) throw new InvalidDataException("The completed export inventory no longer matches its graph.");
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inventory)
        {
            string path = item!["path"]!.GetValue<string>();
            if (!seen.Add(path) || !exported.Files.TryGetValue(path, out var content) || SourceProject.Sha256(content) != item["sha256"]!.GetValue<string>())
                throw new InvalidDataException("An exported file changed after graph validation; export again from Blender.");
        }
        exported.Verify((path, bytes) => SourceRead.Matches(GraphPath(generation, path), bytes.Length, SourceProject.Sha256(bytes), token));
        if (!SourceRead.Matches(selected.Gltf, receiptBytes.Length, SourceProject.Sha256(receiptBytes), token)) throw new IOException("The export manifest changed while it was read.");
        Graph original = new((path, maximum) => SourceRead.All(GraphPath(checkout.Folder, "input/" + path), maximum, token), token);
        original.Load(checkout.Model);
        foreach (var file in checkout.Files)
            if (!original.Files.TryGetValue(file.Project, out var bytes) || SourceProject.Sha256(bytes) != file.Sha256)
                throw new InvalidDataException("The checkout input changed; create a new checkout.");
        var mappings = ReadReceiptMappings(receipt, original, exported, token);
        (Graph Before, Dictionary<string, GraphMapping> Mappings)? history = null;
        foreach (var (path, model) in exported.Models)
            if (model.Document.AllNodes().Any(n => n.Mesh is { } mesh && mesh.Primitives.Sum(p => (long)p.Positions.Count) > ModelBuilder.MaximumVertices)
                && !TerrainSurfaces(view, path, token)) WorldGltf.CheckVertexLimits(model.Document, path, token);
        var roots = exported.Models.ToDictionary(p => p.Key, p => p.Value.Json, StringComparer.OrdinalIgnoreCase);
        var documents = exported.Models.ToDictionary(p => p.Key, p => p.Value.Document, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, byte[]> changes = new(exported.Files, StringComparer.OrdinalIgnoreCase);
        List<string> notes = [];
        // Source files outside this checkout may own placement profiles on an edited shared model.
        long inspected = 0;
        ScopeRemapBudget scopeBudget = new();
        static bool ModelPath(string path) => path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
        foreach (string folder in new[] { SourceProject.DataFolder, SourceProject.GameGenFolder })
        foreach (string owner in SourceProject.Files(view.Root, folder, ModelPath, view.Overlay().Keys.ToArray(), token))
        {
            if (roots.ContainsKey(owner) || !view.Exists(owner, token)) continue;
            scopeBudget.BeginOwner(owner);
            byte[] bytes = view.Read(owner, token, ProjectReadLimits.Model().WithMaximum(MaximumExportBytes - inspected))!;
            inspected += bytes.Length;
            var json = JsonNode.Parse(GltfDocument.ContainerJson(bytes, token, out _), documentOptions: new() { MaxDepth = 64 }) as JsonObject
                ?? throw new InvalidDataException("A source model is not a JSON object.");
            bool changed = false;
            foreach (var node in json["nodes"] as JsonArray ?? [])
            {
                if (node?["extras"]?[WorldGltf.Key] is not JsonObject values || values["ref"] is not JsonValue reference || values["zoneOverrides"] is not JsonObject scope) continue;
                var beforeScope = scope.DeepClone();
                RemapExternal(scope, WorldAssembler.Relative(owner, reference.GetValue<string>()), 0);
                changed |= !JsonNode.DeepEquals(beforeScope, scope);
            }
            if (changed) { changes[owner] = SourceEmbeddedZones.Repack(bytes, json, token); notes.Add("Preserved placement assignments in " + JsonData.ShownText(owner, 512)); }
        }
        void RemapExternal(JsonObject scope, string target, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > GltfDocument.MaximumDepth) throw new InvalidDataException("A placement override exceeds the hierarchy allowance.");
            if (mappings.ContainsKey(target))
            {
                history ??= GraphHistory(view, checkout, original, exported, mappings, import, lineage, token);
                RemapScope(scope, target, history.Value.Before, documents, roots, history.Value.Mappings, token, scopeBudget); return;
            }
            if (scope["references"] is not JsonObject nested) return;
            var bytes = view.Read(target, token, ProjectReadLimits.Model().WithMaximum(MaximumExportBytes - inspected)) ?? throw new InvalidDataException("A placement reference is missing.");
            inspected += bytes.Length;
            var json = JsonNode.Parse(GltfDocument.ContainerJson(bytes, token, out _), documentOptions: new() { MaxDepth = 64 }) as JsonObject
                ?? throw new InvalidDataException("A placement model is not a JSON object.");
            foreach (var (key, value) in nested)
            {
                if (!int.TryParse(key, CultureInfo.InvariantCulture, out int index) || json["nodes"] is not JsonArray nodes || index < 0 || index >= nodes.Count || value is not JsonObject child)
                    throw new InvalidDataException("Invalid nested placement identity.");
                string uri = nodes[index]?["extras"]?[WorldGltf.Key]?["ref"]?.GetValue<string>() ?? throw new InvalidDataException("A placement reference no longer exists.");
                RemapExternal(child, WorldAssembler.Relative(target, uri), depth + 1);
            }
        }
        // States an earlier update applied count as the checkout's own only for exports that descend from that update: the
        // same Blender import, and (when the extension records it) an export this Blender file made before. Another import of
        // the checkout, or an older save of this one, starts from before that update; replacing what it applied would undo it.
        var accepted = checkout.Files.Concat(checkout.Applied.Where(f => f.Import == import && (lineage == null || f.Generation != null && lineage.Contains(f.Generation)))).GroupBy(f => f.Project, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var hashes = view.ReadEditHashes(changes.Keys, token);
        List<string> conflicts = []; Dictionary<string, string?> expected = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in changes.Keys.ToArray())
        {
            view.CheckEditable(path); string hash = SourceProject.Sha256(changes[path]); string? current = hashes[path];
            if (current == hash) { changes.Remove(path); continue; }
            expected[path] = current;
            if (exported.Files.ContainsKey(path))
            {
                bool known = accepted.TryGetValue(path, out var states);
                // Undo before Save removes newly created buffers from the overlay. Their original
                // state was absence, so restoring this exact previously accepted immutable payload
                // is safe. Missing original sources and changed existing content still conflict.
                string suffix = "." + hash + ".bin";
                bool restoredBuffer = current == null && !original.Files.ContainsKey(path) && known && states!.Contains(hash)
                    && Path.GetFileName(path).Length > suffix.Length && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
                bool conflict = current == null ? known && !restoredBuffer : !known || !states!.Contains(current);
                if (conflict) conflicts.Add(path);
            }
        }
        List<TextureShadow> shadows = [];
        foreach (string model in original.Models.Keys)
        {
            IEnumerable<string> Images(JsonObject json) => (json["images"] as JsonArray ?? []).Select(i => WorldAssembler.Relative(model, Uri.UnescapeDataString(i!["uri"]!.GetValue<string>())));
            var previous = Images(original.Models[model].Json).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var texturePaths = Images(exported.Models[model].Json).Where(p => !previous.Contains(p) || changes.ContainsKey(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (texturePaths.Length == 0) continue;
            var bindings = SourceMapZoneBindings.Read(view, model, null, token);
            shadows.AddRange(TextureShadows(view, model, texturePaths, hashes, new HashSet<string>(bindings.Missions, StringComparer.OrdinalIgnoreCase), token));
        }
        if (!force && (conflicts.Count > 0 || shadows.Count > 0))
        {
            // States an update applied from another import, or from a later save of this Blender file, are named as such.
            bool applied = conflicts.Any(path => checkout.Applied.Any(f => f.Project.Equals(path, StringComparison.OrdinalIgnoreCase) && f.Sha256.Equals(hashes[path], StringComparison.OrdinalIgnoreCase)));
            string reason = "The Blender graph would replace project changes or shared textures. " +
                (applied ? "An update from another Blender import or a later save of this Blender file changed them since this file's last export; export from that file, or replace them knowingly. " : "") +
                string.Join("; ", conflicts.Take(8)) + " " + string.Join("; ", shadows.Take(8).Select(s => s.Reason));
            var conflict = new BlenderConflictException(conflicts, reason);
            if (confirm == null) throw conflict;
            if (!confirm(conflict, token)) throw new OperationCanceledException("The Blender update was declined.", token);
        }
        foreach (string model in original.Models.Keys.Where(changes.ContainsKey)) notes.Add("Shared source updated: " + JsonData.ShownText(model, 512));
        // Scripts and animations find objects by name, and a Blender rename or delete does not change them: say which names
        // went (a zStudio rename refuses while a script or animation of the mission needs the name).
        foreach (string model in exported.Models.Keys.Where(changes.ContainsKey))
        {
            if (view.Read(model, token) is not { } before) continue;
            // Engine names (extras.recoil.name first), as the built world and its lookups see them; a name another node still
            // has is not gone.
            static HashSet<string> Names(JsonNode? json) => (json?["nodes"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(WorldGltf.EngineName).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
            var left = Names(exported.Models[model].Json);
            var gone = Names(JsonNode.Parse(before)).Where(name => !left.Contains(name)).Order(StringComparer.Ordinal).ToArray();
            if (gone.Length > 0)
                notes.Add($"{JsonData.ShownText(model, 512)}: no node is named {string.Join(", ", gone.Take(8).Select(g => JsonData.ShownText(g, 64)))}"
                    + (gone.Length > 8 ? $" and {gone.Length - 8} more" : "") + " any more. Scripts and animations that name them are not changed; check the missions that use this file.");
        }
        var proposed = changes.Select(p => (Relative: p.Key, Content: p.Value)).ToArray();
        var changedModels = changes.Keys.Where(ModelPath).ToArray();
        if (changedModels.Length > 0)
            SourceDeclientEdits.ValidateModels(view, changedModels, new("Update Blender graph", proposed, checkout.Model, notes), new(view, token), "Blender graph update", token);
        using CopyFiles written = new();
        string sealedFolder = GraphPath(checkout.Folder, "sealed/" + Guid.NewGuid().ToString("N")); written.Root(sealedFolder);
        foreach (var (path, bytes) in exported.Files) written.Write(GraphPath(sealedFolder, path), bytes);
        written.Write(Path.Combine(sealedFolder, GraphManifest), receiptBytes);
        var planned = proposed;
        _ = AppliedManifest(checkout, planned, import, exportGeneration);
        view.TrackMapZoneOwners(changedModels, token);
        view.TrackMapZoneSources(changedModels, proposed, token);
        if (prepared != null) workspace.ValidatePreparedEdit(prepared, token);
        // Save must rediscover placement owners added after planning, also after Undo/Redo.
        // Prepared callers transfer this tracking on acceptance; standalone callers retain it here.
        foreach (string model in changedModels)
        {
            view.TrackMapZoneGeometry(model);
            if (prepared != null) workspace.TrackMapZoneGeometry(model);
        }
        if (prepared != null) workspace.CopyMapZoneOwnersFrom(view, changedModels);
        written.Keep();
        return new("Update RECOIL graph from Blender", planned, notes, sealedFolder) { Expected = expected, Import = import, Generation = exportGeneration };
    }

    private static Dictionary<string, GraphMapping> ReadReceiptMappings(JsonObject receipt, Graph before, Graph after, CancellationToken token)
    {
        var result = new Dictionary<string, GraphMapping>(StringComparer.OrdinalIgnoreCase);
        var models = receipt["models"] as JsonArray ?? throw new InvalidDataException("The export has no provenance inventory.");
        if (models.Count != before.Models.Count || models.Count != after.Models.Count) throw new InvalidDataException("The export lost a physical source identity.");
        foreach (var item in models)
        {
            token.ThrowIfCancellationRequested(); string path = item!["path"]!.GetValue<string>();
            if (!before.Models.TryGetValue(path, out var old) || !after.Models.TryGetValue(path, out var current) || result.ContainsKey(path)) throw new InvalidDataException("Invalid source graph identity.");
            var oldNodes = old.Document.AllNodes().Select(n => n.Index).ToHashSet();
            Dictionary<int, int> nodes = [];
            foreach (var (key, value) in item["nodes"]!.AsObject())
            {
                int index = int.Parse(key, CultureInfo.InvariantCulture), origin = value!.GetValue<int>();
                if (origin != -1 && !oldNodes.Contains(origin)) throw new InvalidDataException("Invalid source node identity.");
                nodes.Add(index, origin);
            }
            if (!nodes.Keys.ToHashSet().SetEquals(current.Document.AllNodes().Select(n => n.Index))) throw new InvalidDataException("Incomplete source node provenance.");
            Dictionary<int, (int, int[])> meshes = [];
            var oldLayout = WorldGltf.ZoneLayout.Read(old.Document, token); var layout = WorldGltf.ZoneLayout.Read(current.Document, token);
            var oldIndices = GraphMeshIndices(oldLayout, old.Json); var newIndices = GraphMeshIndices(layout, current.Json);
            var oldSizes = oldLayout.Meshes.ToDictionary(m => oldIndices[m], m => m.Primitives.Sum(p => oldLayout.Polygons[p].Count));
            foreach (var (key, value) in item["meshes"]!.AsObject())
            {
                int index = int.Parse(key, CultureInfo.InvariantCulture), origin = value!["mesh"]!.GetValue<int>();
                int[] polygons = value["polygons"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
                if (origin != -1 && !oldSizes.ContainsKey(origin) || polygons.Any(p => p < -1 || p >= (origin == -1 ? 0 : oldSizes[origin]))) throw new InvalidDataException("Invalid source polygon provenance.");
                meshes.Add(index, (origin, polygons));
            }
            foreach (var mesh in layout.Meshes)
            {
                int index = newIndices[mesh];
                if (!meshes.TryGetValue(index, out var polygonMapping) || polygonMapping.Item2.Length != mesh.Primitives.Sum(p => layout.Polygons[p].Count)) throw new InvalidDataException("Incomplete polygon provenance.");
            }
            var mapping = new GraphMapping(nodes, meshes);
            void Identities(string field, Dictionary<int, string> identities, IEnumerable<int> keys)
            {
                foreach (var (key, value) in item[field]!.AsObject())
                {
                    string identity = value!.GetValue<string>();
                    if (identity.Length is < 1 or > 64 || identity.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new InvalidDataException("Invalid editor identity in the receipt.");
                    identities.Add(int.Parse(key, CultureInfo.InvariantCulture), identity);
                }
                if (!identities.Keys.ToHashSet().SetEquals(keys) || identities.Values.Distinct().Count() != identities.Count) throw new InvalidDataException("Incomplete or ambiguous editor identities in the receipt.");
            }
            Identities("editorNodes", mapping.EditorNodes, nodes.Keys);
            Identities("editorMeshes", mapping.EditorMeshes, meshes.Keys);
            if (item["copiedNodes"] is JsonObject copied)
                foreach (var (key, value) in copied)
                {
                    string from = value!.GetValue<string>(); int node = int.Parse(key, CultureInfo.InvariantCulture);
                    if (from.Length is < 1 or > 64 || from.Any(c => !char.IsAsciiLetterOrDigit(c)) || !nodes.ContainsKey(node)) throw new InvalidDataException("Invalid copy identity in the receipt.");
                    mapping.CopiedNodes.Add(node, from);
                }
            foreach (var (key, value) in item["meshes"]!.AsObject())
            {
                int index = int.Parse(key, CultureInfo.InvariantCulture);
                var faces = value!["faces"]!.AsArray().Select(f => f!.AsArray().Select(v => v!.GetValue<int>()).ToArray()).ToArray();
                if (faces.Length != meshes[index].Item2.Length || faces.Any(f => f.Length == 0 || f.Any(v => v <= 0))) throw new InvalidDataException("Invalid face identities in the receipt.");
                mapping.Faces[index] = faces;
                if (value["parents"] is JsonArray parents)
                {
                    var split = parents.Select(f => f!.AsArray().Select(v => v!.GetValue<int>()).ToArray()).ToArray();
                    if (split.Length != faces.Length || split.Any(f => f.Any(v => v <= 0))) throw new InvalidDataException("Invalid face parents in the receipt.");
                    mapping.FaceParents[index] = split;
                }
            }
            result.Add(path, mapping);
        }
        return result;
    }
}
