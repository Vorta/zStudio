using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core.Worlds;

public sealed partial class WorldAssembler
{
    private SourceMissionModels? missionModels;
    private string? zoneManifest;
    private readonly Dictionary<GltfDocument, Dictionary<GltfNode, (int Node, int Mesh)>> zoneLayouts = new(ReferenceEqualityComparer.Instance);

    private void ReadMapZones(string script)
    {
        string mission = Path.GetFileNameWithoutExtension(script.Replace('\\', '/')).ToLowerInvariant();
        if (!SourceProject.MissionName().IsMatch(mission)) return;
        string path = SourceMapZones.PathForMission(mission);
        // The missing-file probe participates in the same source snapshot as every other build input.
        if (!Exists(path)) return;
        missionModels = new(SourceMapZones.Parse(files.Read(path, token, ProjectReadLimits.Bytes(SourceMapZones.MaximumBytes)), token), token);
        zoneManifest = path;
        ModelFiles.Add(path);
    }

    private SourceMapZoneAsset? ZoneAsset(string logical)
        => missionModels?.Asset(logical);

    private string GeometryPath(string logical) => missionModels?.Geometry(logical) ?? logical;

    private bool ModelExists(string logical)
    {
        return missionModels?.Exists(logical, Exists) ?? Exists(logical);
    }

    private string? ZoneReference(string logical, int node)
        => missionModels?.Reference(logical, node);

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
