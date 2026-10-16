using zStudio.Core.Gltf;
using zStudio.Core.Sources;

namespace zStudio.Core.Worlds;

public sealed partial class WorldAssembler
{
    private readonly Dictionary<GltfDocument, Dictionary<GltfNode, (int Node, int Mesh)>> zoneLayouts = new(ReferenceEqualityComparer.Instance);

    private void RejectExternalZoneMetadata(string script)
    {
        string mission = Path.GetFileNameWithoutExtension(script.Replace('\\', '/')).ToLowerInvariant();
        if (!SourceProject.MissionName().IsMatch(mission)) return;
        string path = $"data/{mission}/meta/zones.json";
        if (Exists(path)) throw new InvalidDataException("This source project uses external zone metadata. Re-extract the original ZBD files with zStudio before opening or building it.");
    }

    private Dictionary<GltfNode, (int Node, int Mesh)> ZoneLayout(GltfDocument document)
    {
        if (zoneLayouts.TryGetValue(document, out var known)) return known;
        Dictionary<GltfNode, (int Node, int Mesh)> nodes = new(ReferenceEqualityComparer.Instance);
        Dictionary<GltfMesh, int> meshes = new(ReferenceEqualityComparer.Instance);
        foreach (var node in document.AllNodes())
        {
            token.ThrowIfCancellationRequested();
            if (nodes.ContainsKey(node)) continue;
            int mesh = -1;
            if (node.Mesh is { } geometry && !meshes.TryGetValue(geometry, out mesh))
                meshes.Add(geometry, mesh = meshes.Count);
            nodes.Add(node, (nodes.Count, mesh));
        }
        zoneLayouts.Add(document, nodes);
        return nodes;
    }
}
