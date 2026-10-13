using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

public static partial class SourceTerrainConversion
{
    private sealed class ConversionZones
    {
        private readonly WorldZoneProfile originalProfile;
        private readonly Dictionary<JsonNode, int> originalNodes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<GltfMesh, IReadOnlyList<uint>> meshWords = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, IReadOnlyList<uint>> rawMeshWords = [];
        private readonly Dictionary<GltfNode, WorldNodeZone> nodeWords = new(ReferenceEqualityComparer.Instance);

        private ConversionZones(WorldZoneProfile profile,
            JsonObject root, GltfDocument doc, CancellationToken token)
        {
            originalProfile = profile;
            WorldGltf.ValidateZoneProfile(doc, originalProfile, token);
            var jsonNodes = (JsonArray)root["nodes"]!;
            foreach (var node in doc.AllNodes())
            {
                token.ThrowIfCancellationRequested();
                if (nodeWords.ContainsKey(node)) continue;
                int ordinal = nodeWords.Count;
                nodeWords.Add(node, originalProfile.Nodes[ordinal]);
                originalNodes.Add(jsonNodes[node.Index]!, ordinal);
                if (node.Mesh is not { } mesh || meshWords.ContainsKey(mesh)) continue;
                var words = originalProfile.MeshPolygons[meshWords.Count];
                meshWords.Add(mesh, words);
                rawMeshWords.Add(GltfInteger.Int32(jsonNodes[node.Index]!["mesh"]), words);
            }
        }

        internal static ConversionZones? Read(SourceWorkspace workspace, string database, JsonObject root,
            GltfDocument doc, CancellationToken token)
        {
            // Conversion changes topology. Refuse when other placements own overrides that it cannot preserve.
            var owners = SourceMapZoneBindings.Read(workspace, database, doc, token);
            owners.RequireSingleTerrainOwner(database);
            return new(WorldGltf.CaptureZoneProfile(doc, token: token), root, doc, token);
        }

        // Only the parsed planning view gains effective inherited node fields.
        internal void ApplyPlanningValues(GltfDocument doc, CancellationToken token)
        {
            foreach (var node in doc.Roots) Apply(node, originalProfile.LoadRoot?.Word & 255u ?? 255u);
            void Apply(GltfNode node, uint inherited)
            {
                token.ThrowIfCancellationRequested();
                var zone = nodeWords[node];
                uint effective = zone.Inherit ? zone.Word & ~255u | inherited : zone.Word;
                node.Extras ??= new();
                if (node.Extras[WorldGltf.Key] is not JsonObject extras)
                    node.Extras[WorldGltf.Key] = extras = new();
                uint flags = Flags(extras);
                extras["flags"] = $"0x{(zone.Gate ? flags | WorldGltf.ZoneGate : flags & ~WorldGltf.ZoneGate):X8}";
                WorldGltf.WriteNodeZoneValue(extras, effective);
                foreach (var child in node.Children) Apply(child, effective & 255u);
            }
        }

        internal void Update(JsonObject root, GltfDocument database,
            GltfDocument surfaces, IReadOnlyList<TerrainConversionSurface> groups,
            IReadOnlyDictionary<int, GltfNode> originals, CancellationToken token)
        {
            List<IReadOnlyList<uint>> surfaceWords = []; long count = 0;
            foreach (var group in groups)
            {
                List<uint> words = [];
                foreach (int index in group.Nodes)
                {
                    token.ThrowIfCancellationRequested();
                    var source = meshWords[originals[index].Mesh!];
                    if (source.Count > SourceMapZones.MaximumTargets - count)
                        throw new InvalidDataException("Converted terrain exceeds the map's polygon zone assignment limit.");
                    count += source.Count; words.AddRange(source);
                }
                surfaceWords.Add(words);
            }
            WorldZoneProfile surfaceProfile = new(WorldGltf.LayoutFingerprint(surfaces, token),
                surfaces.Roots.Select(_ => new WorldNodeZone(255, false, true)).ToArray(), surfaceWords);
            WorldGltf.ValidateZoneProfile(surfaces, surfaceProfile, token);

            var jsonNodes = (JsonArray)root["nodes"]!;
            List<WorldNodeZone> nodes = []; List<IReadOnlyList<uint>> meshes = [];
            HashSet<GltfNode> seen = new(ReferenceEqualityComparer.Instance);
            HashSet<GltfMesh> seenMeshes = new(ReferenceEqualityComparer.Instance);
            foreach (var node in database.AllNodes())
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(node)) continue;
                if (originalNodes.TryGetValue(jsonNodes[node.Index]!, out int old))
                {
                    nodes.Add(originalProfile.Nodes[old]);
                }
                else nodes.Add(new(255, false, true)); // The new terrain marker has no stored object zone.
                if (node.Mesh is { } mesh && seenMeshes.Add(mesh))
                    meshes.Add(rawMeshWords[GltfInteger.Int32(jsonNodes[node.Index]!["mesh"])]);
            }
            var profile = new WorldZoneProfile(WorldGltf.LayoutFingerprint(database, token), nodes, meshes, originalProfile.LoadRoot);
            WorldGltf.ValidateZoneProfile(database, profile, token);
            WorldGltf.PatchEmbeddedZones(root, database, profile, token);
            WorldGltf.EmbedZones(surfaces, surfaceProfile, token: token);
        }
    }
}
