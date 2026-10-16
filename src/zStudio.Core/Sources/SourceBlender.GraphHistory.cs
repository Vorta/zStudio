using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceBlender
{
    // A checkout baseline stays immutable. Later exports are mapped through the
    // sealed generation currently present in the workspace, including after Undo.
    private static (Graph Before, Dictionary<string, GraphMapping> Mappings) GraphHistory(SourceWorkspace workspace,
        BlenderCheckout checkout, Graph baseline, Graph exported, Dictionary<string, GraphMapping> incoming, CancellationToken token)
    {
        Graph current = new((path, maximum) => workspace.Read(path, token, ProjectReadLimits.Bytes(maximum))
            ?? throw new FileNotFoundException("A source dependency is missing."), token);
        current.Load(checkout.Model);
        foreach (string path in baseline.Models.Keys) current.Load(path);
        Dictionary<string, GraphMapping> result = new(StringComparer.OrdinalIgnoreCase);
        var identities = baseline.Models.Keys.ToDictionary(path => path,
            path => HistoryIdentity(current, path, token), StringComparer.OrdinalIgnoreCase);
        foreach (string path in baseline.Models.Keys)
            if (JsonNode.DeepEquals(HistoryIdentity(baseline, path, token), identities[path]))
                Consider(path, incoming[path]);
        string sealedRoot = GraphPath(checkout.Folder, "sealed");
        if (Directory.Exists(sealedRoot))
        {
            long inspected = 0;
            var folders = Directory.EnumerateDirectories(sealedRoot).Take(257).ToArray();
            if (folders.Length > 256) throw new InvalidDataException("The checkout has too many saved generations; create a fresh checkout.");
            // Directory names are random, not chronology. Every content-matching
            // generation must agree on provenance, even if one matches exact bytes.
            foreach (string folder in folders)
            {
                token.ThrowIfCancellationRequested();
                string file = GraphPath(folder, GraphManifest);
                if (!File.Exists(file)) continue;
                byte[] bytes = SourceRead.All(file, Math.Min(MaximumCheckoutManifestBytes, MaximumExportBytes - inspected), token); inspected += bytes.Length;
                var receipt = Parse(bytes, file, token);
                var hashes = receipt["files"]!.AsArray().ToDictionary(f => f!["path"]!.GetValue<string>(), f => f!["sha256"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (string path in baseline.Models.Keys)
                {
                    token.ThrowIfCancellationRequested();
                    byte[] content = SourceRead.All(GraphPath(folder, path), Math.Min(GltfDocument.MaximumJsonBytes, MaximumExportBytes - inspected), token);
                    inspected += content.Length;
                    if (!hashes.TryGetValue(path, out var hash) || hash != SourceProject.Sha256(content))
                        throw new InvalidDataException("A sealed Blender generation changed; create a new checkout.");
                    var json = Parse(content, path, token);
                    var identity = HistoryIdentity(json, uri => hashes.GetValueOrDefault(WorldAssembler.Relative(path, Uri.UnescapeDataString(uri)))
                        ?? throw new InvalidDataException("A sealed Blender dependency is missing."), token);
                    if (!JsonNode.DeepEquals(identity, identities[path])) continue;
                    // This is a file identity, not a whole-graph snapshot. Avoid
                    // rereading every referenced mesh for an unchanged holder.
                    Graph previous = new((relative, maximum) =>
                    {
                        byte[] bytes = SourceRead.All(GraphPath(folder, relative), Math.Min(maximum, MaximumExportBytes - inspected), token);
                        inspected += bytes.Length;
                        if (!hashes.TryGetValue(relative, out var digest) || digest != SourceProject.Sha256(bytes))
                            throw new InvalidDataException("A sealed Blender generation changed; create a new checkout.");
                        return bytes;
                    }, token);
                    var document = GltfDocument.Read(content, (uri, maximum) => previous.Read(WorldAssembler.Relative(path, uri), maximum), token);
                    WorldGltf.ValidateSupported(document, path);
                    WorldGltf.CheckInstances(document, path);
                    previous.Models[path] = new(path, json, document);
                    Graph original = new((_, _) => throw new InvalidOperationException(), token);
                    original.Models[path] = baseline.Models[path];
                    var row = receipt["models"]!.AsArray().Single(m => m!["path"]!.GetValue<string>().Equals(path, StringComparison.OrdinalIgnoreCase));
                    var partial = new JsonObject { ["models"] = new JsonArray(row!.DeepClone()) };
                    var earlier = ReadReceiptMappings(partial, original, previous, token)[path];
                    Consider(path, ComposeGraphMapping(earlier, incoming[path], json, exported.Models[path].Json));
                }
            }
        }
        if (result.Count != baseline.Models.Count)
            throw new InvalidDataException("Placement assignments refer to geometry changed outside this checkout. Create a fresh checkout before updating it.");
        return (current, result);

        void Consider(string path, GraphMapping candidate)
        {
            if (result.TryGetValue(path, out var prior) && !SameHistoryMapping(prior, candidate))
                throw new InvalidDataException("Several Blender generations have identical source content but different placement identities. Create a fresh checkout before updating it.");
            result[path] = candidate;
        }
    }

    // A layout fingerprint checks cardinalities/connectivity, not face or node
    // identity: equally sized faces can trade places without changing it. Match
    // geometry, hierarchy and buffer bytes instead. Typed display/surface values
    // and zones can change independently; replacing them still requires the
    // normal update conflict consent, not a new geometry checkout.
    private static JsonObject HistoryIdentity(Graph graph, string path, CancellationToken token)
    {
        return HistoryIdentity(graph.Models[path].Json, uri => SourceProject.Sha256(graph.Files[WorldAssembler.Relative(path, Uri.UnescapeDataString(uri))]), token);
    }

    internal static JsonObject HistoryIdentity(JsonObject source, Func<string, string> bufferHash, CancellationToken token)
    {
        var json = source.DeepClone().AsObject();
        static void Remove(JsonObject? values, params string[] fields)
        {
            if (values != null) foreach (string field in fields) values.Remove(field);
        }
        static void Appearance(JsonObject values)
        {
            if (values["appearance"] is not JsonObject appearance) return;
            Remove(appearance, "flags", "alphaScale", "color", "colorAlpha");
            if (appearance.Count == 0) values.Remove("appearance");
        }
        static void NodeValues(JsonObject values)
        {
            Remove(values, "zone", "zonePadding", "zoneOverrides");
            Remove(values, WorldGltf.NodeFlagFields);
            Appearance(values);
        }
        void Owner(JsonNode? owner, Action<JsonObject> strip)
        {
            token.ThrowIfCancellationRequested();
            if (owner?["extras"] is not JsonObject extras || extras[WorldGltf.Key] is not JsonObject values) return;
            strip(values);
            // Typed metadata may be the only reason these wrappers existed.
            if (values.Count == 0) extras.Remove(WorldGltf.Key);
            if (extras.Count == 0) owner.AsObject().Remove("extras");
        }
        foreach (var node in json["nodes"] as JsonArray ?? [])
            Owner(node, values =>
            {
                NodeValues(values);
                if (values["model"] is JsonObject model) Remove(model, "mode", "flags", "scroll");
            });
        foreach (var scene in json["scenes"] as JsonArray ?? [])
            Owner(scene, values =>
            {
                if (values["loadRoot"] is not JsonObject root) return;
                NodeValues(root);
                if (root.Count == 0) values.Remove("loadRoot");
            });
        foreach (var mesh in json["meshes"] as JsonArray ?? [])
        {
            Owner(mesh, values => Remove(values, "mode", "flags", "scroll"));
            foreach (var primitive in mesh?["primitives"] as JsonArray ?? [])
                Owner(primitive, values => Remove(values, "polygonZones", "polygonZonePadding", "polygonZoneRaw"));
        }
        foreach (var material in json["materials"] as JsonArray ?? [])
        {
            Owner(material, values => Remove(values, "zone", "zones", "zonePadding", "zoneRaw", "flags", "opacity", "color", "packedColor", "fields", "soil", "priority", "backface", "normals"));
            Remove(material as JsonObject, "doubleSided", "alphaMode", "alphaCutoff", "emissiveFactor");
            if (material?["pbrMetallicRoughness"] is JsonObject pbr)
            {
                Remove(pbr, "baseColorFactor", "metallicFactor", "roughnessFactor");
                if (pbr.Count == 0) material.AsObject().Remove("pbrMetallicRoughness");
            }
        }
        foreach (var buffer in json["buffers"] as JsonArray ?? [])
        {
            // A GLB has one URI-less embedded BIN; workspace dependency tracking
            // supplies its content digest through the empty-URI callback.
            string uri = buffer!["uri"]?.GetValue<string>() ?? "";
            if (!uri.StartsWith("data:", StringComparison.Ordinal))
                buffer["uri"] = bufferHash(uri);
        }
        return json;
    }

    private static bool SameHistoryMapping(GraphMapping left, GraphMapping right)
    {
        if (left.Nodes.Count != right.Nodes.Count || left.Meshes.Count != right.Meshes.Count) return false;
        foreach (var (index, origin) in left.Nodes)
            if (!right.Nodes.TryGetValue(index, out int other) || origin != other) return false;
        foreach (var (index, source) in left.Meshes)
        {
            if (!right.Meshes.TryGetValue(index, out var other) || source.Mesh != other.Mesh || source.Polygons.Length != other.Polygons.Length) return false;
            for (int polygon = 0; polygon < source.Polygons.Length; polygon++)
            {
                var first = left.PolygonSources.TryGetValue(index, out var a) ? a[polygon] : [source.Polygons[polygon]];
                var second = right.PolygonSources.TryGetValue(index, out var b) ? b[polygon] : [other.Polygons[polygon]];
                if (!first.Order().SequenceEqual(second.Order())) return false;
            }
        }
        return true;
    }

    private static GraphMapping ComposeGraphMapping(GraphMapping previous, GraphMapping next, JsonObject oldJson, JsonObject newJson)
    {
        var nodeIds = previous.EditorNodes.ToDictionary(n => n.Value, n => n.Key);
        var meshIds = previous.EditorMeshes.ToDictionary(m => m.Value, m => m.Key);
        GraphMapping result = new([], []);
        foreach (var (index, origin) in next.Nodes)
            result.Nodes[index] = nodeIds.GetValueOrDefault(next.EditorNodes[index], -1);
        foreach (var (index, origin) in next.Meshes)
        {
            if (!meshIds.TryGetValue(next.EditorMeshes[index], out int oldIndex))
            {
                // Make Single User and newly applied modifiers change the editor mesh
                // identity while the source node and original face provenance survive.
                var owners = next.Nodes.Keys.Where(n => newJson["nodes"]![n]?["mesh"]?.GetValue<int>() == index)
                    .Select(n => nodeIds.GetValueOrDefault(next.EditorNodes[n], -1)).Where(n => n >= 0)
                    .Select(n => oldJson["nodes"]![n]?["mesh"]?.GetValue<int>()).OfType<int>().Distinct().ToArray();
                if (origin.Mesh >= 0 && owners.Length == 1 && previous.Meshes[owners[0]].Mesh == origin.Mesh) oldIndex = owners[0];
                else { result.Meshes[index] = (-1, Enumerable.Repeat(-1, origin.Polygons.Length).ToArray()); continue; }
            }
            var oldFaces = previous.Faces[oldIndex];
            Dictionary<int, List<int>> byFace = [];
            for (int i = 0; i < oldFaces.Length; i++)
                foreach (int face in oldFaces[i])
                { if (!byFace.TryGetValue(face, out var values)) byFace[face] = values = []; values.Add(i); }
            var sources = next.Faces[index].Select(f => f.SelectMany(id => byFace.GetValueOrDefault(id) ?? []).Distinct().ToArray()).ToArray();
            result.Meshes[index] = (oldIndex, sources.Select(s => s.Length == 0 ? -1 : s[0]).ToArray());
            result.PolygonSources[index] = sources;
        }
        return result;
    }
}
