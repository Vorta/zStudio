using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core.Worlds;

public sealed partial class WorldAssembler
{
    private readonly Dictionary<GltfDocument, Dictionary<GltfNode, (int Node, int Mesh)>> zoneLayouts = new(ReferenceEqualityComparer.Instance);

    private void ReadMapZones(string script)
    {
        string mission = Path.GetFileNameWithoutExtension(script.Replace('\\', '/')).ToLowerInvariant();
        if (!SourceProject.MissionName().IsMatch(mission)) return;
        string path = SourceMapZones.PathForMission(mission);
        if (Exists(path)) throw new InvalidDataException("This source project uses external zone metadata. Re-extract the original ZBD files with zStudio 0.8.5 before opening or building it.");
    }

    private static string GeometryPath(string logical) => logical;

    private bool ModelExists(string logical)
    {
        return Exists(logical);
    }

    private static string? ZoneReference(string logical, int node) => null;

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
