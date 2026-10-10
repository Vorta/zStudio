using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

public static partial class SourceTerrainConversion
{
    private sealed class ConversionZones
    {
        private readonly string path;
        private readonly SourceMapZones map;
        private readonly SourceMapZoneAsset asset;
        private readonly Dictionary<JsonNode, int> originalNodes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<GltfMesh, IReadOnlyList<uint>> meshWords = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, IReadOnlyList<uint>> rawMeshWords = [];
        private readonly Dictionary<GltfNode, WorldNodeZone> nodeWords = new(ReferenceEqualityComparer.Instance);

        private ConversionZones(string path, SourceMapZones map, SourceMapZoneAsset asset,
            JsonObject root, GltfDocument doc, CancellationToken token)
        {
            this.path = path; this.map = map; this.asset = asset;
            WorldGltf.ValidateZoneProfile(doc, asset.Profile, token);
            var jsonNodes = (JsonArray)root["nodes"]!;
            foreach (var node in doc.AllNodes())
            {
                token.ThrowIfCancellationRequested();
                if (nodeWords.ContainsKey(node)) continue;
                int ordinal = nodeWords.Count;
                nodeWords.Add(node, asset.Profile.Nodes[ordinal]);
                originalNodes.Add(jsonNodes[node.Index]!, ordinal);
                if (node.Mesh is not { } mesh || meshWords.ContainsKey(mesh)) continue;
                var words = asset.Profile.MeshPolygons[meshWords.Count];
                meshWords.Add(mesh, words);
                rawMeshWords.Add(GltfInteger.Int32(jsonNodes[node.Index]!["mesh"]), words);
            }
        }

        internal static ConversionZones? Read(SourceWorkspace workspace, string database, JsonObject root,
            GltfDocument doc, CancellationToken token)
        {
            string[] parts = SourceWorkspace.Normalize(database).Split('/');
            if (parts.Length < 3 || !SourceProject.MissionName().IsMatch(parts[1])) return null;
            string path = SourceMapZones.PathForMission(parts[1]);
            if (workspace.Read(path, token, SourceMapZones.MaximumBytes) is not { } bytes) return null;
            var map = SourceMapZones.Parse(bytes, token);
            var bindings = map.Assets.Where(a => a.GeometryPath.Equals(database, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (bindings.Length == 0) return null;
            if (bindings.Length > 1 || !bindings[0].LogicalPath.Equals(database, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This database geometry has multiple logical zone bindings. Convert an independently owned mission database.");
            SourceMapZoneBindings.Read(workspace, database, doc, token).RequireSingleTerrainOwner(database);
            return new(path, map, bindings[0], root, doc, token);
        }

        // Only the parsed planning view gains effective node fields. The authored glTF stays neutral.
        internal void ApplyPlanningValues(GltfDocument doc, CancellationToken token)
        {
            foreach (var node in doc.Roots) Apply(node, asset.Profile.LoadRoot?.Word & 255u ?? 255u);
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
                extras.Remove("zone"); extras.Remove("zoneWord");
                extras["zone"] = (int)(effective & 255);
                if ((effective & ~255u) != 0) extras["zoneWord"] = $"0x{effective:X8}";
                foreach (var child in node.Children) Apply(child, effective & 255u);
            }
        }

        internal (string, byte[]?) Update(JsonObject root, GltfDocument database, string surfacePath,
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
            Dictionary<int, int> renumbered = [];
            foreach (var node in database.AllNodes())
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(node)) continue;
                if (originalNodes.TryGetValue(jsonNodes[node.Index]!, out int old))
                {
                    renumbered.Add(old, nodes.Count); nodes.Add(asset.Profile.Nodes[old]);
                }
                else nodes.Add(new(255, false, true)); // The new terrain marker has no stored object zone.
                if (node.Mesh is { } mesh && seenMeshes.Add(mesh))
                    meshes.Add(rawMeshWords[GltfInteger.Int32(jsonNodes[node.Index]!["mesh"])]);
            }
            var profile = new WorldZoneProfile(WorldGltf.LayoutFingerprint(database, token), nodes, meshes, asset.Profile.LoadRoot);
            WorldGltf.ValidateZoneProfile(database, profile, token);
            var references = asset.References.Where(r => renumbered.ContainsKey(r.Node)).Select(r => r with { Node = renumbered[r.Node] }).ToArray();
            List<SourceMapZoneAsset> assets = [];
            foreach (var entry in map.Assets) assets.Add(ReferenceEquals(entry, asset) ? entry with { Profile = profile, References = references } : entry);
            assets.Add(new(surfacePath, surfacePath, surfaceProfile, []));
            return (path, new SourceMapZones(assets, map.Labels).Write(token));
        }
    }
}
