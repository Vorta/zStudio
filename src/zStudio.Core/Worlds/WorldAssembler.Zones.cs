using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core.Worlds;

public sealed partial class WorldAssembler
{
    private SourceMapZones? mapZones;
    private string? zoneManifest;
    private readonly Dictionary<string, Dictionary<int, string>> zoneReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GltfDocument, Dictionary<GltfNode, (int Node, int Mesh)>> zoneLayouts = new(ReferenceEqualityComparer.Instance);

    private void ReadMapZones(string script)
    {
        string mission = Path.GetFileNameWithoutExtension(script.Replace('\\', '/')).ToLowerInvariant();
        if (!SourceProject.MissionName().IsMatch(mission)) return;
        string path = SourceMapZones.PathForMission(mission);
        // The missing-file probe participates in the same source snapshot as every other build input.
        if (!files.Exists(path)) return;
        mapZones = SourceMapZones.Parse(files.Read(path, token, ProjectReadLimits.Bytes(SourceMapZones.MaximumBytes)), token);
        zoneManifest = path;
        ModelFiles.Add(path);
        foreach (var asset in mapZones.Assets)
        {
            token.ThrowIfCancellationRequested();
            Dictionary<int, string> references = [];
            foreach (var reference in asset.References)
            {
                token.ThrowIfCancellationRequested();
                references.Add(reference.Node, reference.Spelling);
            }
            zoneReferences.Add(asset.LogicalPath, references);
        }
    }

    private SourceMapZoneAsset? ZoneAsset(string logical)
        => mapZones?.TryGetAsset(logical, out var asset) == true ? asset : null;

    private string GeometryPath(string logical) => ZoneAsset(logical)?.GeometryPath ?? logical;

    private bool ModelExists(string logical)
    {
        if (ZoneAsset(logical) is not { } asset) return files.Exists(logical);
        // A stale alias must not silently hide a newly authored physical model at the same path.
        if (!string.Equals(logical, asset.GeometryPath, StringComparison.OrdinalIgnoreCase) && files.Exists(logical))
            throw new InvalidDataException($"{JsonData.ShownText(logical)} exists both as a map zone alias and as a physical model. Remove the conflicting alias or rename the model before building.");
        return true; // A missing backing geometry is an error in this binding, never a search-directory fallback.
    }

    private string? ZoneReference(string logical, int node)
        => zoneReferences.TryGetValue(logical, out var references) ? references.GetValueOrDefault(node) : null;

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
