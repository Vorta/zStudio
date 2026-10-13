using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

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
        var remaining = new HashSet<string>(baseline.Models.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, model) in baseline.Models)
        {
            if (!current.Models.TryGetValue(path, out var live)) throw new InvalidDataException("The source graph changed its physical files; create a new checkout.");
            if (WorldGltf.LayoutFingerprint(model.Document, token) == WorldGltf.LayoutFingerprint(live.Document, token)
                && model.Document.AllNodes().Select(n => n.Index).SequenceEqual(live.Document.AllNodes().Select(n => n.Index))
                && model.Document.AllNodes().Select(n => model.Json["nodes"]![n.Index]?["mesh"]?.ToString())
                    .SequenceEqual(live.Document.AllNodes().Select(n => live.Json["nodes"]![n.Index]?["mesh"]?.ToString())))
            { result[path] = incoming[path]; remaining.Remove(path); }
        }
        if (remaining.Count == 0) return (current, result);
        string sealedRoot = GraphPath(checkout.Folder, "sealed");
        if (Directory.Exists(sealedRoot))
        {
            long inspected = 0;
            var folders = Directory.EnumerateDirectories(sealedRoot).Take(257).ToArray();
            if (folders.Length > 256) throw new InvalidDataException("The checkout has too many saved generations; create a fresh checkout.");
            var fingerprints = remaining.ToDictionary(path => path, path => WorldGltf.LayoutFingerprint(current.Models[path].Document, token), StringComparer.OrdinalIgnoreCase);
            foreach (string folder in folders.OrderByDescending(p => p, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                string file = GraphPath(folder, GraphManifest);
                if (!File.Exists(file)) continue;
                byte[] bytes = SourceRead.All(file, Math.Min(MaximumCheckoutManifestBytes, MaximumExportBytes - inspected), token); inspected += bytes.Length;
                var receipt = Parse(bytes, file, token);
                var hashes = receipt["files"]!.AsArray().ToDictionary(f => f!["path"]!.GetValue<string>(), f => f!["sha256"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
                var recorded = receipt["models"]!.AsArray().ToDictionary(m => m!["path"]!.GetValue<string>(), m => m!["fingerprint"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
                var matches = remaining.Where(path => hashes.TryGetValue(path, out var hash) && (hash == SourceProject.Sha256(current.Files[path])
                    || recorded.GetValueOrDefault(path) == fingerprints[path])).ToArray();
                if (matches.Length == 0) continue;
                Graph previous = new((path, maximum) =>
                {
                    byte[] content = SourceRead.All(GraphPath(folder, path), Math.Min(maximum, MaximumExportBytes - inspected), token);
                    inspected += content.Length; return content;
                }, token);
                previous.Load(checkout.Model);
                foreach (string path in baseline.Models.Keys) previous.Load(path);
                foreach (var (path, content) in previous.Files)
                    if (!hashes.TryGetValue(path, out var hash) || hash != SourceProject.Sha256(content)) throw new InvalidDataException("A sealed Blender generation changed; create a new checkout.");
                var earlier = ReadReceiptMappings(receipt, baseline, previous, token);
                foreach (string path in matches)
                {
                    var oldModel = previous.Models[path]; var currentModel = current.Models[path];
                    if (WorldGltf.LayoutFingerprint(oldModel.Document, token) != fingerprints[path]
                        || !oldModel.Document.AllNodes().Select(n => n.Index).SequenceEqual(currentModel.Document.AllNodes().Select(n => n.Index))) continue;
                    result[path] = ComposeGraphMapping(earlier[path], incoming[path], oldModel.Json, exported.Models[path].Json); remaining.Remove(path);
                }
                if (remaining.Count == 0) return (current, result);
            }
        }
        throw new InvalidDataException("Placement assignments refer to geometry changed outside this checkout. Create a fresh checkout before updating it.");
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
