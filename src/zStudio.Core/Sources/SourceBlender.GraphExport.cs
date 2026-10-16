using System.Globalization;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceBlender
{
    internal sealed record GraphMapping(Dictionary<int, int> Nodes, Dictionary<int, (int Mesh, int[] Polygons)> Meshes)
    {
        internal Dictionary<int, string> EditorNodes { get; init; } = [];
        internal Dictionary<int, string> EditorMeshes { get; init; } = [];
        internal Dictionary<int, int[][]> Faces { get; init; } = [];
        internal Dictionary<int, int[][]> PolygonSources { get; init; } = [];
    }

    public static JsonObject PrepareBlenderExport(string edits, string destination, CancellationToken token = default)
    {
        edits = AbsoluteGraphPath(edits); destination = AbsoluteGraphPath(destination);
        var request = Parse(SourceRead.All(edits, MaximumCheckoutManifestBytes, token), edits, token);
        if (request["protocol"]?.GetValue<int>() != GraphProtocol) throw new InvalidDataException("Incompatible Blender export protocol.");
        string session = AbsoluteGraphPath(request["session"]!.GetValue<string>());
        var manifest = Parse(SourceRead.All(GraphPath(session, "session.json"), MaximumCheckoutManifestBytes, token), "session", token);
        if (manifest["protocol"]?.GetValue<int>() != GraphProtocol) throw new InvalidDataException("Incompatible Blender session.");
        string sourceRoot = Path.GetFullPath(manifest["sourceRoot"]!.GetValue<string>()).TrimEnd(Path.DirectorySeparatorChar);
        string outputRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        if (outputRoot.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase) || outputRoot.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an export folder outside the imported sources; a checkout's outbox is beside its input folder.");
        string root = manifest["root"]!.GetValue<string>();
        Graph original = new((path, maximum) => SourceRead.All(GraphPath(session, "baseline/" + path), maximum, token), token);
        original.Load(root);
        if (manifest["files"]!.AsArray().Count != original.Files.Count) throw new InvalidDataException("The Blender baseline inventory changed; import the source again.");
        foreach (var file in manifest["files"]!.AsArray())
            if (SourceProject.Sha256(original.Read(file!["path"]!.GetValue<string>())) != file["sha256"]!.GetValue<string>())
                throw new InvalidDataException("The Blender baseline changed; import the source again.");
        string exportRoot = Path.GetDirectoryName(edits)!;
        var output = original.Files.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GraphMapping> mappings = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JsonObject> roots = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Dictionary<int, int[]>> faceIds = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, byte[]> writtenTextures = new(StringComparer.OrdinalIgnoreCase);
        long total = original.Files.Sum(p => (long)p.Value.Length);
        var rows = request["models"] as JsonArray ?? throw new InvalidDataException("The export has no model inventory.");
        if (rows.Count != original.Models.Count) throw new InvalidDataException("The export does not contain the complete source graph.");
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            string path = row!["path"]!.GetValue<string>();
            if (!original.Models.TryGetValue(path, out var before) || roots.ContainsKey(path)) throw new InvalidDataException("The export model identity is missing or repeated.");
            byte[] Read(string relative, long maximum)
            {
                var bytes = SourceRead.All(GraphPath(exportRoot, relative), Math.Min(maximum, MaximumExportBytes - total), token);
                total += bytes.Length; return bytes;
            }
            byte[] raw = Read(path, GltfDocument.MaximumJsonBytes);
            var json = Parse(raw, path, token);
            // A material may be reused in another physical file. Its source image belongs
            // to that original material, not to a similarly named texture in this file.
            Dictionary<JsonObject, bool> importedImages = new(ReferenceEqualityComparer.Instance);
            foreach (var material in (json["materials"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var source = material["extras"]?["zstudio_material_source"];
                string sourcePath = source?["path"]?.GetValue<string>() ?? path;
                int origin = source?["index"]?.GetValue<int>() ?? material["extras"]?[BridgeKey]?.GetValue<int>() ?? -1;
                bool hadImage = false;
                if (origin >= 0)
                {
                    if (!original.Models.TryGetValue(sourcePath, out var model) || model.Json["materials"] is not JsonArray materials || origin >= materials.Count)
                        throw new InvalidDataException("Invalid source material identity.");
                    hadImage = materials[origin]?["pbrMetallicRoughness"]?["baseColorTexture"]?["index"] != null;
                }
                else if (source != null) throw new InvalidDataException("Invalid source material identity.");
                importedImages.Add(material, hadImage);
                (material["extras"] as JsonObject)?.Remove("zstudio_material_source");
            }
            // Check the submitted material even when the geometry fast path would retain
            // baseline buffers. Missing-source checker images also count as connections.
            FollowImages(json, (material, _) => importedImages[material], path, followNames: false);
            var exported = GltfDocument.Read(raw, (uri, maximum) => Read(WorldAssembler.Relative(path, uri), maximum), token);
            var mapping = ReadMapping(before, exported, json, token);
            faceIds[path] = (json["meshes"] as JsonArray ?? []).Select((mesh, index) => (index, Faces: mesh!["primitives"]!.AsArray()
                .SelectMany(p => p!["extras"]![BridgeKey]!["faces"]!.AsArray().Select(v => v!.GetValue<int>())).ToArray())).ToDictionary(p => p.index, p => p.Faces);
            bool geometry = row["geometryChanged"]?.GetValue<bool>() == true || StructuralChange(before.Json, json, mapping);
            geometry |= ChangedPolygonZones(before, exported, json, mapping, token);
            var result = geometry ? json : MergeMetadata(before, json, mapping, token);
            if (geometry)
            {
                PreserveGraphOrder(before, result, mapping);
                var indexPatches = WriteExportPolygons(before, exported, result, mapping, faceIds[path], token);
                Dictionary<string, byte[]> geometryBuffers = new(StringComparer.OrdinalIgnoreCase);
                // Buffers and textures belong to this physical file. Preserve established texture paths by identity/name.
                foreach (var buffer in result["buffers"] as JsonArray ?? [])
                {
                    string uri = buffer!["uri"]!.GetValue<string>();
                    if (!uri.StartsWith("data:", StringComparison.Ordinal))
                    {
                        string relative = WorldAssembler.Relative(path, Uri.UnescapeDataString(uri));
                        geometryBuffers[relative] = SourceRead.All(GraphPath(exportRoot, relative), MaximumExportBytes - geometryBuffers.Sum(p => (long)p.Value.Length), token);
                    }
                }
                foreach (var image in result["images"] as JsonArray ?? [])
                {
                    string uri = image!["uri"]!.GetValue<string>();
                    string relative = WorldAssembler.Relative(path, Uri.UnescapeDataString(uri));
                    byte[] png = SourceRead.All(GraphPath(exportRoot, relative), MaximumExportBytes - output.Sum(p => (long)p.Value.Length), token);
                    string name = Path.GetFileName(relative);
                    var candidates = (before.Json["images"] as JsonArray ?? []).Select(i => i?["uri"]?.GetValue<string>()).OfType<string>()
                        .Select(u => WorldAssembler.Relative(path, Uri.UnescapeDataString(u))).Where(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (candidates.Length > 1) throw new InvalidDataException("The exported texture name has several source identities.");
                    string? identity = image["extras"]?[BridgeKey]?.GetValue<string>();
                    if (identity != null && ((!original.Files.ContainsKey(identity) && !original.MissingImages.Contains(identity)) || !identity.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Invalid source texture identity.");
                    string target = identity ?? candidates.SingleOrDefault() ?? relative;
                    (image["extras"] as JsonObject)?.Remove(BridgeKey);
                    bool missing = image["extras"]?["zstudio_missing_image"]?.GetValue<bool>() == true;
                    (image["extras"] as JsonObject)?.Remove("zstudio_missing_image");
                    if (missing)
                    {
                        if (identity == null || !original.MissingImages.Contains(identity)) throw new InvalidDataException("An exported image has invalid missing-source provenance.");
                        image["uri"] = Path.GetRelativePath(Path.GetDirectoryName(GraphPath(exportRoot, path))!, GraphPath(exportRoot, identity)).Replace('\\', '/');
                        continue;
                    }
                    if (writtenTextures.TryGetValue(target, out var prior) && !prior.AsSpan().SequenceEqual(png))
                        throw new InvalidDataException("Two exported images would replace the same source texture with different pictures; give them distinct source names.");
                    writtenTextures[target] = png;
                    output[target] = png;
                    image["uri"] = Path.GetRelativePath(Path.GetDirectoryName(GraphPath(exportRoot, path))!, GraphPath(exportRoot, target)).Replace('\\', '/');
                }
                // Image provenance can restore a different physical filename than Blender's
                // temporary export name; the compiled texture name follows that final image.
                FollowImages(result, (_, _) => false, path);
                WriteGraphIndices(path, result, indexPatches, geometryBuffers);
                foreach (var buffer in result["buffers"] as JsonArray ?? [])
                {
                    string uri = buffer!["uri"]!.GetValue<string>();
                    if (uri.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string relative = WorldAssembler.Relative(path, Uri.UnescapeDataString(uri));
                    byte[] content = geometryBuffers[relative];
                    // An untouched file may still use the old buffer. Publish edited
                    // buffers by content identity instead of overwriting that dependency.
                    string name = Path.GetFileNameWithoutExtension(path) + "." + SourceProject.Sha256(content).ToLowerInvariant() + ".bin";
                    string target = WorldAssembler.Relative(path, name);
                    if (output.TryGetValue(target, out var existing) && !existing.AsSpan().SequenceEqual(content)) throw new InvalidDataException("An exported buffer conflicts with another source dependency.");
                    output[target] = content; buffer["uri"] = name;
                }
            }
            else
            {
                var identity = IdentityMapping(before, token);
                foreach (var (index, origin) in mapping.Nodes) identity.EditorNodes[origin] = mapping.EditorNodes[index];
                foreach (var (index, origin) in mapping.Meshes) identity.EditorMeshes[origin.Mesh] = mapping.EditorMeshes[index];
                var layout = WorldGltf.ZoneLayout.Read(before.Document, token);
                var indices = GraphMeshIndices(layout, before.Json);
                var rawFaces = new Dictionary<int, int[]>(faceIds[path]);
                foreach (var mesh in layout.Meshes)
                {
                    int originalIndex = indices[mesh];
                    int exportedIndex = mapping.Meshes.Single(m => m.Value.Mesh == originalIndex).Key;
                    int offset = 0;
                    identity.Faces[originalIndex] = mesh.Primitives.SelectMany(p => layout.Triangles[p]).Select(count =>
                    { var faces = rawFaces[exportedIndex].Skip(offset).Take(count).Distinct().ToArray(); offset += count; return faces; }).ToArray();
                    faceIds[path][originalIndex] = identity.Faces[originalIndex].Select(f => f[0]).ToArray();
                }
                mapping = identity;
            }
            CanonicalMetadata(result);
            byte[] resultBytes = GltfJson.Write(result, true, token);
            var transformDocument = GltfDocument.Read(resultBytes, uri => output[WorldAssembler.Relative(path, uri)], token);
            UngroupTransforms(transformDocument, result, path);
            roots.Add(path, result); mappings.Add(path, mapping);
            output[path] = GltfJson.Write(result, true, token);
        }
        // Reindex placement profiles only after every physical file has its final layout.
        Graph updated = new((path, maximum) => !output.TryGetValue(path, out var bytes) ? throw new FileNotFoundException("Missing exported dependency.")
            : bytes.Length <= maximum ? bytes : throw new InvalidDataException("Oversized exported dependency."), token);
        // Existing fingerprints describe the baseline; validate/remap those before normal graph validation.
        Dictionary<string, GltfDocument> documents = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, json) in roots)
            documents[path] = GltfDocument.Read(output[path], uri => output[WorldAssembler.Relative(path, uri)], token);
        ScopeRemapBudget scopeBudget = new();
        foreach (var (path, json) in roots)
        {
            scopeBudget.BeginOwner(path);
            foreach (var node in json["nodes"] as JsonArray ?? [])
                if (node?["extras"]?[WorldGltf.Key] is JsonObject values && values["ref"] is JsonValue reference && values["zoneOverrides"] is JsonObject scope)
                    RemapScope(scope, WorldAssembler.Relative(path, reference.GetValue<string>()), original, documents, roots, mappings, token, scopeBudget);
            output[path] = GltfJson.Write(json, true, token);
        }
        foreach (var (path, json) in roots)
        {
            scopeBudget.BeginOwner(path);
            foreach (var node in json["nodes"] as JsonArray ?? [])
                if (node?["extras"] is JsonObject extras && extras["zstudio_placements"] is JsonArray placements)
                {
                    ApplyPlacementEdits(path, node!.AsObject(), placements, roots, documents, mappings, faceIds, token, scopeBudget);
                    extras.Remove("zstudio_placements");
                }
            output[path] = GltfJson.Write(json, true, token);
        }
        updated.Load(root);
        foreach (string path in roots.Keys) updated.Load(path);
        string generation = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..12];
        string folder = GraphPath(destination, generation);
        using CopyFiles written = new(); written.Root(folder);
        foreach (var (path, bytes) in updated.Files) written.Write(GraphPath(folder, path), bytes);
        JsonObject complete = new()
        {
            ["format"] = "zstudio-recoil-export", ["version"] = GraphProtocol, ["root"] = root,
            ["files"] = new JsonArray(updated.Files.Select(p => (JsonNode)new JsonObject { ["path"] = p.Key, ["sha256"] = SourceProject.Sha256(p.Value) }).ToArray()),
            ["models"] = new JsonArray(mappings.Select(p => (JsonNode)new JsonObject
            {
                ["path"] = p.Key,
                ["fingerprint"] = WorldGltf.LayoutFingerprint(documents[p.Key], token),
                ["editorNodes"] = new JsonObject(p.Value.EditorNodes.Select(n => KeyValuePair.Create<string, JsonNode?>(n.Key.ToString(CultureInfo.InvariantCulture), JsonValue.Create(n.Value)))),
                ["editorMeshes"] = new JsonObject(p.Value.EditorMeshes.Select(n => KeyValuePair.Create<string, JsonNode?>(n.Key.ToString(CultureInfo.InvariantCulture), JsonValue.Create(n.Value)))),
                ["nodes"] = new JsonObject(p.Value.Nodes.Select(n => KeyValuePair.Create<string, JsonNode?>(n.Key.ToString(CultureInfo.InvariantCulture), JsonValue.Create(n.Value)))),
                ["meshes"] = new JsonObject(p.Value.Meshes.Select(m => KeyValuePair.Create<string, JsonNode?>(m.Key.ToString(CultureInfo.InvariantCulture), new JsonObject
                { ["mesh"] = m.Value.Mesh, ["polygons"] = new JsonArray(m.Value.Polygons.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                    ["faces"] = new JsonArray(p.Value.Faces[m.Key].Select(f => (JsonNode)new JsonArray(f.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray())).ToArray()) }))),
            }).ToArray()),
        };
        // The completion manifest is last. Listings never offer a partially written generation.
        written.Write(Path.Combine(folder, GraphManifest), GltfJson.Write(complete, true, token)); written.Keep();
        return new() { ["protocol"] = GraphProtocol, ["root"] = Path.Combine(folder, root), ["manifest"] = Path.Combine(folder, GraphManifest) };
    }

    private static GraphMapping IdentityMapping(GraphModel model, CancellationToken token)
    {
        var layout = WorldGltf.ZoneLayout.Read(model.Document, token);
        var indices = GraphMeshIndices(layout, model.Json);
        Dictionary<int, (int, int[])> meshes = [];
        for (int i = 0; i < layout.Meshes.Count; i++)
        {
            int index = indices[layout.Meshes[i]];
            meshes.Add(index, (index, Enumerable.Range(0, layout.Meshes[i].Primitives.Sum(p => layout.Polygons[p].Count)).ToArray()));
        }
        return new(model.Document.AllNodes().ToDictionary(n => n.Index, n => n.Index), meshes);
    }

    private static GraphMapping ReadMapping(GraphModel before, GltfDocument after, JsonObject json, CancellationToken token)
    {
        Dictionary<int, int> nodes = []; Dictionary<int, (int, int[])> meshes = [];
        Dictionary<int, string> editorNodes = [], editorMeshes = [];
        string Identity(JsonNode? values, int index, string prefix)
        {
            string id = values?["zstudio_editor_id"]?.GetValue<string>() ?? prefix + index.ToString(CultureInfo.InvariantCulture);
            if (id.Length is < 1 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new InvalidDataException("Invalid Blender editor identity.");
            return id;
        }
        int oldCount = (before.Json["nodes"] as JsonArray)?.Count ?? 0;
        int oldMeshes = (before.Json["meshes"] as JsonArray)?.Count ?? 0;
        foreach (var node in after.AllNodes())
        {
            int origin = json["nodes"]![node.Index]?["extras"]?[BridgeKey]?.GetValue<int>() ?? -1;
            if (origin < -1 || origin >= oldCount) throw new InvalidDataException("Invalid exported node provenance.");
            nodes.Add(node.Index, origin);
            editorNodes.Add(node.Index, Identity(json["nodes"]![node.Index]?["extras"], node.Index, "n"));
        }
        var array = json["meshes"] as JsonArray ?? [];
        for (int i = 0; i < array.Count; i++)
        {
            token.ThrowIfCancellationRequested(); int origin = array[i]?["extras"]?[BridgeKey]?.GetValue<int>() ?? -1;
            if (origin < -1 || origin >= oldMeshes) throw new InvalidDataException("Invalid exported mesh provenance.");
            List<int> polygons = [];
            foreach (var primitive in array[i]!["primitives"]!.AsArray())
            {
                if (primitive?["extras"]?[BridgeKey]?["origins"] is not JsonArray faces) throw new InvalidDataException("The export lost its polygon provenance.");
                polygons.AddRange(faces.Select(f => f!.GetValue<int>()));
            }
            meshes.Add(i, (origin, polygons.ToArray()));
            editorMeshes.Add(i, Identity(array[i]?["extras"], i, "m"));
        }
        if (editorNodes.Values.Distinct().Count() != editorNodes.Count || editorMeshes.Values.Distinct().Count() != editorMeshes.Count)
            throw new InvalidDataException("Copied objects or meshes have ambiguous editor identities. Reopen their placement zones or make the edited mesh single-user before exporting.");
        return new(nodes, meshes) { EditorNodes = editorNodes, EditorMeshes = editorMeshes };
    }

    private static bool StructuralChange(JsonObject before, JsonObject after, GraphMapping mapping)
    {
        var oldNodes = before["nodes"] as JsonArray ?? [];
        if ((before["materials"] as JsonArray)?.Count != (after["materials"] as JsonArray)?.Count
            || mapping.Meshes.Count != (before["meshes"] as JsonArray ?? []).Count
            || mapping.Meshes.Values.Select(m => m.Mesh).Distinct().Count() != mapping.Meshes.Count) return true;
        if (mapping.Nodes.Count != oldNodes.Count || mapping.Nodes.Values.Distinct().Count() != oldNodes.Count || mapping.Nodes.Values.Any(v => v < 0)) return true;
        foreach (var (current, original) in mapping.Nodes)
        {
            var oldChildren = (oldNodes[original]?["children"] as JsonArray ?? []).Select(n => n!.GetValue<int>()).Order().ToArray();
            int? oldMesh = oldNodes[original]?["mesh"]?.GetValue<int>();
            int? newMesh = after["nodes"]![current]?["mesh"]?.GetValue<int>();
            if (oldMesh != (newMesh is { } mesh ? mapping.Meshes[mesh].Mesh : (int?)null)) return true;
            var newChildren = (after["nodes"]![current]?["children"] as JsonArray ?? []).Select(n => mapping.Nodes[n!.GetValue<int>()]).Order().ToArray();
            if (!oldChildren.SequenceEqual(newChildren)) return true;
        }
        return false;
    }

    private static JsonObject MergeMetadata(GraphModel before, JsonObject after, GraphMapping mapping, CancellationToken token)
    {
        var result = before.Json.DeepClone().AsObject();
        foreach (var (current, original) in mapping.Nodes)
        {
            var source = after["nodes"]![current]!.AsObject(); var target = result["nodes"]![original]!.AsObject();
            SetRecoil(target, source["extras"]?[WorldGltf.Key]);
            if (source["extras"]?["zstudio_placements"] is { } placements) target["extras"]!["zstudio_placements"] = placements.DeepClone();
            if (source["extras"]?["zstudio_transform_changed"]?.GetValue<bool>() == true)
            {
                foreach (string key in new[] { "matrix", "translation", "rotation", "scale" })
                { target.Remove(key); if (source[key] is { } value) target[key] = value.DeepClone(); }
            }
        }
        foreach (var (current, source) in mapping.Meshes)
            if (source.Mesh >= 0) SetRecoil(result["meshes"]![source.Mesh]!.AsObject(), after["meshes"]![current]?["extras"]?[WorldGltf.Key]);
        foreach (var material in after["materials"] as JsonArray ?? [])
        {
            int original = material?["extras"]?[BridgeKey]?.GetValue<int>() ?? -1;
            if (original >= 0 && result["materials"] is JsonArray materials && original < materials.Count) SetRecoil(materials[original]!.AsObject(), material?["extras"]?[WorldGltf.Key]);
        }
        int scene = result["scene"]?.GetValue<int>() ?? 0, next = after["scene"]?.GetValue<int>() ?? 0;
        if (result["scenes"] is JsonArray scenes && after["scenes"] is JsonArray exports) SetRecoil(scenes[scene]!.AsObject(), exports[next]?["extras"]?[WorldGltf.Key]);
        return result;
    }

    private static void SetRecoil(JsonObject target, JsonNode? value)
    {
        if (value is not JsonObject) throw new InvalidDataException("The export lost required RECOIL properties.");
        if (target["extras"] == null) target["extras"] = new JsonObject();
        target["extras"]![WorldGltf.Key] = value.DeepClone();
    }

    private static bool ChangedPolygonZones(GraphModel before, GltfDocument after, JsonObject json, GraphMapping mapping, CancellationToken token)
    {
        var layout = WorldGltf.ZoneLayout.Read(before.Document, token);
        var profile = WorldGltf.WriteZoneOverride(WorldGltf.CaptureZoneProfile(before.Document, token: token), token);
        var indices = GraphMeshIndices(layout, before.Json);
        var ordinals = layout.Meshes.Select((m, i) => (Index: indices[m], Ordinal: i)).ToDictionary(p => p.Index, p => p.Ordinal);
        foreach (var (current, mappingMesh) in mapping.Meshes)
        {
            if (!ordinals.TryGetValue(mappingMesh.Mesh, out int ordinal)) return true;
            var oldZones = profile["meshes"]![ordinal]!["polygonZones"]!.AsArray();
            foreach (var primitive in json["meshes"]![current]!["primitives"]!.AsArray())
            {
                var values = primitive!["extras"]![BridgeKey]!;
                var origins = values["origins"]!.AsArray(); var zones = values["zones"]!.AsArray();
                if (origins.Count != zones.Count) throw new InvalidDataException("Polygon zone counts disagree.");
                for (int i = 0; i < origins.Count; i++)
                {
                    int origin = origins[i]!.GetValue<int>();
                    if (origin < 0 || origin >= oldZones.Count || !JsonNode.DeepEquals(zones[i], oldZones[origin] ?? new JsonArray())) return true;
                }
            }
        }
        return false;
    }

    private static List<(JsonObject Primitive, int[] Indices)> WriteExportPolygons(GraphModel before, GltfDocument after, JsonObject json, GraphMapping mapping, Dictionary<int, int[]> faceIds, CancellationToken token)
    {
        List<(JsonObject, int[])> patches = [];
        PolygonWorkBudget work = new(token);
        var oldLayout = WorldGltf.ZoneLayout.Read(before.Document, token);
        var oldProfile = WorldGltf.CaptureZoneProfile(before.Document, token: token);
        var oldIndices = GraphMeshIndices(oldLayout, before.Json);
        var oldAssignments = oldLayout.Meshes.Select((m, i) => (Index: oldIndices[m], Values: oldProfile.MeshPolygons[i])).ToDictionary(p => p.Index, p => p.Values);
        var oldMeshes = oldLayout.Meshes.ToDictionary(m => oldIndices[m]);
        var parsedMeshes = after.AllNodes().Where(n => n.Mesh != null).GroupBy(n => json["nodes"]![n.Index]!["mesh"]!.GetValue<int>()).ToDictionary(g => g.Key, g => g.First().Mesh!);
        foreach (var (index, mapped) in mapping.Meshes.ToArray())
        {
            var parsed = parsedMeshes[index]; var primitives = json["meshes"]![index]!["primitives"]!.AsArray();
            int oldFaces = mapped.Mesh < 0 ? 0 : oldMeshes[mapped.Mesh].Primitives.Sum(p => oldLayout.Polygons[p].Count);
            var oldCounts = mapped.Mesh < 0 ? [] : oldMeshes[mapped.Mesh].Primitives.SelectMany(p => oldLayout.Triangles[p]).ToArray();
            List<int> remapped = [], retainedFaces = []; List<int[]> faceGroups = [];
            for (int j = 0; j < primitives.Count; j++)
            {
                var primitive = primitives[j]!.AsObject(); var metadata = primitive["extras"]![BridgeKey]!;
                var origins = metadata["origins"]!.AsArray(); var zones = metadata["zones"]!.AsArray();
                var identities = metadata["faces"]!.AsArray(); var partitions = metadata["partitions"] as JsonArray;
                if (origins.Count != parsed.Primitives[j].Indices.Count / 3 || zones.Count != origins.Count) throw new InvalidDataException("Exported polygon identities do not match the geometry.");
                JsonArray polygons = []; List<uint> words = [];
                bool patch = false;
                for (int k = 0; k < origins.Count; k++)
                {
                    token.ThrowIfCancellationRequested(); int origin = origins[k]!.GetValue<int>();
                    if (origin < -1 || origin >= oldFaces) throw new InvalidDataException("An exported face has invalid source provenance.");
                    int count = origin < 0 ? 1 : oldCounts[origin];
                    bool complete = count > 1 && k + count <= origins.Count;
                    for (int c = 1; complete && c < count; c++)
                        complete = origins[k + c]!.GetValue<int>() == origin && JsonNode.DeepEquals(zones[k], zones[k + c])
                            && (partitions == null || JsonNode.DeepEquals(partitions[k], partitions[k + c]));
                    var corners = complete ? ExportBoundary(parsed.Primitives[j], k, count, work) : null;
                    polygons.Add(corners == null ? JsonValue.Create(1) : new JsonObject
                    { ["triangles"] = count, ["corners"] = new JsonArray(corners.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()) });
                    uint word = WorldGltf.ReadPolygonZoneValue(new JsonObject { ["zones"] = zones[k]!.DeepClone() }, "Blender polygon");
                    if (origin >= 0)
                    {
                        uint old = oldAssignments[mapped.Mesh][origin]; JsonObject previous = new(); WorldGltf.WritePolygonZoneValue(previous, old);
                        if (JsonNode.DeepEquals(previous["zones"] ?? new JsonArray(), zones[k])) word = old;
                    }
                    words.Add(word); remapped.Add(origin); retainedFaces.Add(identities[k]!.GetValue<int>());
                    faceGroups.Add(identities.Skip(k).Take(corners == null ? 1 : count).Select(f => f!.GetValue<int>()).Distinct().ToArray());
                    if (corners != null) { k += count - 1; patch = true; }
                }
                JsonObject properties = new() { ["polygons"] = polygons };
                WorldGltf.WritePolygonZoneValues(properties, words, token);
                primitive["extras"]![WorldGltf.Key] = properties;
                primitive["extras"]!.AsObject().Remove(BridgeKey);
                if (patch) patches.Add((primitive, parsed.Primitives[j].Indices.ToArray()));
            }
            mapping.Meshes[index] = (mapped.Mesh, remapped.ToArray());
            faceIds[index] = retainedFaces.ToArray();
            mapping.Faces[index] = faceGroups.ToArray();
        }
        return patches;
    }

    private static void CanonicalMetadata(JsonObject json)
    {
        foreach (var node in json["nodes"] as JsonArray ?? [])
        {
            if (node?["extras"] is not JsonObject extras || extras[WorldGltf.Key] is not JsonObject values) throw new InvalidDataException("An exported node lost its RECOIL attributes.");
            WorldGltf.WriteNodeFlags(values, WorldGltf.StoredNodeFlags(values));
            extras.Remove(BridgeKey); extras.Remove("zstudio_transform_changed");
            extras.Remove("zstudio_editor_id");
        }
        foreach (string kind in new[] { "meshes", "materials" })
            foreach (var item in json[kind] as JsonArray ?? [])
            { (item?["extras"] as JsonObject)?.Remove(BridgeKey); (item?["extras"] as JsonObject)?.Remove("zstudio_editor_id"); }
        foreach (var scene in json["scenes"] as JsonArray ?? [])
            if (scene?["extras"]?[WorldGltf.Key]?["loadRoot"] is JsonObject root) WorldGltf.WriteNodeFlags(root, WorldGltf.StoredNodeFlags(root));
    }

    // Admission is shared by every scope in an owner and every owner in an operation.
    // Measure the expansion before cloning: a small edited reference file may multiply
    // large assignments stored in a mission outside the checkout's input graph.
    internal sealed class ScopeRemapBudget(long ownerMaximum = GltfDocument.MaximumMetadataBytes, long totalMaximum = MaximumExportBytes)
    {
        private readonly Dictionary<string, long> owners = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<JsonNode, bool> extendedRoots = new(ReferenceEqualityComparer.Instance);
        private string owner = "";
        private long totalBytes;
        // The remap and placement-edit phases revisit each physical owner. Selecting
        // it again must retain its earlier usage instead of starting a fresh allowance.
        internal void BeginOwner(string path) => owner = path;
        internal void Admit(long bytes)
        {
            long ownerBytes = owners.GetValueOrDefault(owner);
            if (bytes < 0 || bytes > ownerMaximum - ownerBytes || bytes > totalMaximum - totalBytes)
                throw new InvalidDataException("Copying these reference assignments exceeds the source metadata allowance.");
            owners[owner] = ownerBytes + bytes; totalBytes += bytes;
        }
        internal long ProfileBytes(GraphMapping mapping, JsonObject root, JsonObject? inherited, CancellationToken token)
        {
            if (!extendedRoots.TryGetValue(root, out bool extended))
                extendedRoots.Add(root, extended = HasExtendedZones(root, token));
            long polygonBytes = extended || inherited != null && HasExtendedZones(inherited, token) ? 64 : 16;
            long bytes = 256L + 128L * mapping.Nodes.Count + 64L * mapping.Meshes.Count;
            foreach (var mesh in mapping.Meshes.Values)
            {
                token.ThrowIfCancellationRequested();
                bytes += polygonBytes * mesh.Polygons.LongLength;
                if (bytes > ownerMaximum) throw new InvalidDataException("Copying these reference assignments exceeds the source metadata allowance.");
            }
            return bytes;
        }
    }

    private static bool HasExtendedZones(JsonNode value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value is JsonObject fields)
        {
            if (fields.ContainsKey("polygonZoneRaw") || fields.ContainsKey("polygonZonePadding") || fields.ContainsKey("zoneRaw") || fields.ContainsKey("zonePadding")) return true;
            foreach (var item in fields) if (item.Value != null && HasExtendedZones(item.Value, token)) return true;
        }
        else if (value is JsonArray array)
            foreach (var item in array) if (item != null && HasExtendedZones(item, token)) return true;
        return false;
    }

    internal static void RemapScope(JsonObject scope, string target, Graph original, Dictionary<string, GltfDocument> documents, Dictionary<string, JsonObject> roots,
        Dictionary<string, GraphMapping> mappings, CancellationToken token, ScopeRemapBudget budget)
    {
        Dictionary<(JsonObject Scope, string Target), long> sizes = [];
        budget.Admit(Measure(scope, target, 0));
        RemapScopeCore(scope, target, original, documents, roots, mappings, token);

        long Measure(JsonObject values, string path, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > GltfDocument.MaximumDepth) throw new InvalidDataException("A placement override exceeds the hierarchy allowance.");
            if (sizes.TryGetValue((values, path), out long known)) return known;
            // Include the source copy as well as the new profile: both are alive while
            // remapping. The bounded writer measures escaped authored values safely.
            long size = GltfJson.Write(values, false, token).LongLength;
            var mapping = mappings[path];
            if (values["fingerprint"] != null)
                size += budget.ProfileBytes(mapping, roots[path], values, token);
            if (values["references"] is JsonObject nested)
            {
                foreach (var (current, previous) in mapping.Nodes)
                {
                    token.ThrowIfCancellationRequested();
                    if (previous < 0 || nested[previous.ToString(CultureInfo.InvariantCulture)] is not JsonObject child) continue;
                    string reference = original.Models[path].Json["nodes"]![previous]!["extras"]![WorldGltf.Key]!["ref"]!.GetValue<string>();
                    size += 32 + Measure(child, WorldAssembler.Relative(path, reference), depth + 1);
                    Check();
                }
            }
            Check(); sizes.Add((values, path), size); return size;
            void Check()
            {
                if (size > GltfDocument.MaximumMetadataBytes)
                    throw new InvalidDataException("Copying these reference assignments exceeds the source metadata allowance.");
            }
        }
    }

    private static void RemapScopeCore(JsonObject scope, string target, Graph original, Dictionary<string, GltfDocument> documents, Dictionary<string, JsonObject> roots,
        Dictionary<string, GraphMapping> mappings, CancellationToken token)
    {
        var before = original.Models[target]; var after = documents[target]; var mapping = mappings[target];
        if (scope["fingerprint"] != null)
        {
            var profile = WorldGltf.ReadZoneOverride(scope, token); WorldGltf.ValidateZoneProfile(before.Document, profile, token);
            var oldLayout = WorldGltf.ZoneLayout.Read(before.Document, token); var layout = WorldGltf.ZoneLayout.Read(after, token);
            var defaults = WorldGltf.CaptureZoneProfile(after, token: token);
            var oldOrdinals = oldLayout.Nodes.Select((n, i) => (n.Index, Ordinal: i)).ToDictionary(p => p.Index, p => p.Ordinal);
            var oldIndices = GraphMeshIndices(oldLayout, before.Json); var newIndices = GraphMeshIndices(layout, roots[target]);
            var oldMeshes = oldLayout.Meshes.Select((m, i) => (Index: oldIndices[m], Ordinal: i)).ToDictionary(p => p.Index, p => p.Ordinal);
            List<WorldNodeZone> nodes = []; List<IReadOnlyList<uint>> meshes = [];
            foreach (var node in layout.Nodes)
            {
                int origin = mapping.Nodes[node.Index];
                nodes.Add(origin >= 0 ? profile.Nodes[oldOrdinals[origin]] : defaults.Nodes[nodes.Count]);
            }
            foreach (var mesh in layout.Meshes)
            {
                int index = newIndices[mesh];
                var mapped = mapping.Meshes[index];
                var fallback = defaults.MeshPolygons[meshes.Count];
                var old = mapped.Mesh < 0 ? null : profile.MeshPolygons[oldMeshes[mapped.Mesh]];
                meshes.Add(mapped.Polygons.Select((p, i) =>
                {
                    if (old == null || p < 0) return fallback[i];
                    var candidates = mapping.PolygonSources.TryGetValue(index, out var sources) ? sources[i] : [p];
                    if (candidates.Any(c => old[c] != old[p])) throw new InvalidDataException("A geometry merge combines different placement zones. Keep these faces separate or create a fresh checkout after resolving their assignments.");
                    return old[p];
                }).ToArray());
            }
            var remapped = WorldGltf.WriteZoneOverride(new(layout.Fingerprint, nodes, meshes, profile.LoadRoot), token);
            foreach (var (key, value) in remapped) scope[key] = value?.DeepClone();
        }
        if (scope["references"] is JsonObject nested)
        {
            JsonObject next = [];
            foreach (var (current, previous) in mapping.Nodes)
            {
                if (previous < 0 || nested[previous.ToString(CultureInfo.InvariantCulture)] is not JsonObject child) continue;
                string reference = before.Json["nodes"]![previous]!["extras"]![WorldGltf.Key]!["ref"]!.GetValue<string>();
                token.ThrowIfCancellationRequested();
                var copy = child.DeepClone().AsObject(); RemapScopeCore(copy, WorldAssembler.Relative(target, reference), original, documents, roots, mappings, token);
                next[current.ToString(CultureInfo.InvariantCulture)] = copy;
            }
            scope["references"] = next;
        }
    }
}
