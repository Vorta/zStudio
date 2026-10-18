using System.Globalization;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceBlender
{
    internal static void ApplyPlacementEdits(string owner, JsonObject holder, JsonArray edits,
        Dictionary<string, JsonObject> roots, Dictionary<string, GltfDocument> documents,
        Dictionary<string, GraphMapping> mappings, Dictionary<string, Dictionary<int, int[]>> faceIds, CancellationToken token, ScopeRemapBudget budget)
    {
        if (edits.Count > GltfDocument.MaximumNodes) throw new InvalidDataException("Too many edited placement scopes.");
        var values = holder["extras"]![WorldGltf.Key]!.AsObject();
        string start = values["ref"]?.GetValue<string>() ?? throw new InvalidDataException("The edited placement no longer references a model.");
        foreach (var edit in edits)
        {
            token.ThrowIfCancellationRequested(); string target = WorldAssembler.Relative(owner, start);
            values["zoneOverrides"] ??= new JsonObject();
            var write = values["zoneOverrides"]!.AsObject(); List<JsonObject> scopes = [write];
            var chain = edit!["chain"]!.AsArray();
            if (chain.Count > GltfDocument.MaximumDepth) throw new InvalidDataException("The edited placement chain is too deep.");
            foreach (var part in chain)
            {
                string previous = part!.GetValue<string>();
                var matches = mappings[target].EditorNodes.Where(n => n.Value == previous).Take(2).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("A placement reference was deleted or duplicated ambiguously; check the placement in the World outliner and export again.");
                int index = matches[0].Key; string key = index.ToString(CultureInfo.InvariantCulture);
                var nodeValues = roots[target]["nodes"]![index]!["extras"]![WorldGltf.Key]!.AsObject();
                write["references"] ??= new JsonObject(); write["references"]![key] ??= new JsonObject();
                write = write["references"]![key]!.AsObject();
                scopes = scopes.Select(s => s["references"]?[key]).OfType<JsonObject>().ToList();
                if (nodeValues["zoneOverrides"] is JsonObject local) scopes.Add(local);
                target = WorldAssembler.Relative(target, nodeValues["ref"]!.GetValue<string>());
            }
            var inherited = scopes.FirstOrDefault(s => s["fingerprint"] != null);
            // Accepted placement edits may predate a geometry expansion. Charge the
            // complete current profile, not the small edit payload, before creating it.
            budget.Admit(budget.ProfileBytes(mappings[target], roots[target], inherited, token) + 64L * (chain.Count + 1));
            var document = documents[target]; var layout = WorldGltf.ZoneLayout.Read(document, token);
            var profile = inherited == null ? WorldGltf.CaptureZoneProfile(document, token: token) : WorldGltf.ReadZoneOverride(inherited, token);
            WorldGltf.ValidateZoneProfile(document, profile, token);
            var result = WorldGltf.WriteZoneOverride(profile, token);
            var nodeEdits = edit["nodes"]!.AsObject(); var meshEdits = edit["meshes"]!.AsObject();
            var meshIndices = GraphMeshIndices(layout, roots[target]);
            for (int i = 0; i < layout.Nodes.Count; i++)
            {
                string previous = mappings[target].EditorNodes[layout.Nodes[i].Index];
                if (nodeEdits[previous] is JsonObject assignment)
                {
                    _ = WorldGltf.ReadNodeZoneValue(assignment, 0, "Blender placement");
                    uint flags = WorldGltf.StoredNodeFlags(assignment);
                    var nodeValues = result["nodes"]![i]!.AsObject();
                    nodeValues["zone"] = assignment["zone"]?.DeepClone() ?? JsonValue.Create("inherit");
                    WorldGltf.WriteNodeFlags(nodeValues, flags & WorldGltf.ZoneGate);
                }
            }
            for (int i = 0; i < layout.Meshes.Count; i++)
            {
                var mesh = layout.Meshes[i]; int index = meshIndices[mesh];
                string previous = mappings[target].EditorMeshes[index];
                var assignments = meshEdits[previous] as JsonObject ?? (previous.Length == 64 ? meshEdits[previous[..32]] as JsonObject : null);
                if (assignments == null) continue;
                var words = profile.MeshPolygons[i].ToArray();
                var identities = faceIds[target][index];
                if (identities.Length != words.Length) throw new InvalidDataException("Placement face identities no longer match the exported surface.");
                // A face split in Blender takes the assignment of the nearest face it was split from that has one.
                var ancestry = mappings[target].FaceParents.GetValueOrDefault(index);
                JsonNode? Assigned(int j)
                {
                    if (assignments[identities[j].ToString(CultureInfo.InvariantCulture)] is { } own) return own;
                    if (ancestry != null && j < ancestry.Length)
                        foreach (int ancestor in ancestry[j])
                            if (assignments[ancestor.ToString(CultureInfo.InvariantCulture)] is { } inherited) return inherited;
                    return null;
                }
                for (int j = 0; j < words.Length; j++)
                    if (Assigned(j) is { } word)
                    {
                        JsonObject oldWord = new(); WorldGltf.WritePolygonZoneValue(oldWord, words[j]);
                        if (!JsonNode.DeepEquals(oldWord["zones"] ?? new JsonArray(), word))
                            words[j] = WorldGltf.ReadPolygonZone(word, null, "Blender placement");
                    }
                WorldGltf.WritePolygonZoneValues(result["meshes"]![i]!.AsObject(), words, token);
            }
            var canonical = WorldGltf.WriteZoneOverride(WorldGltf.ReadZoneOverride(result, token), token);
            foreach (var (key, value) in canonical) write[key] = value?.DeepClone();
        }
    }
}
