using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>Prepared companion edits for every map that owns assignments on a physical geometry file.</summary>
internal sealed class SourceMapZoneBindings
{
    private sealed record Owner(string Path, SourceMapZones Map, IReadOnlyList<SourceMapZoneAsset> Assets);
    private readonly List<Owner> owners = [];
    internal bool Any => owners.Count != 0;
    internal void RequireSingleTerrainOwner(string database)
    {
        if (owners.Count > 1 || owners.Any(o => o.Assets.Count != 1 || !o.Assets[0].LogicalPath.Equals(database, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("This database geometry is shared by several map bindings. Give the mission an independently owned database before changing its terrain recipes.");
    }

    internal static SourceMapZoneBindings Read(SourceWorkspace workspace, string geometry, GltfDocument? original, CancellationToken token)
    {
        SourceMapZoneBindings result = new(); long remaining = 128L << 20;
        WorldGltf.ZoneLayout? layout = null;
        geometry = SourceWorkspace.Normalize(geometry);
        foreach (string mission in SourceProject.MissionFolders(workspace.Root, token))
        {
            token.ThrowIfCancellationRequested();
            string path = SourceMapZones.PathForMission(mission.ToLowerInvariant());
            var bytes = workspace.Read(path, token, Math.Min(remaining, SourceMapZones.MaximumBytes));
            if (bytes == null) continue;
            remaining -= bytes.Length;
            var map = SourceMapZones.Parse(bytes, token);
            var matches = map.Assets.Where(a => a.GeometryPath.Equals(geometry, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) continue;
            if (original != null)
            {
                layout ??= WorldGltf.ZoneLayout.Read(original, token);
                foreach (var asset in matches) WorldGltf.ValidateZoneProfile(layout, asset.Profile, geometry, token);
            }
            result.owners.Add(new(path, map, matches));
        }
        return result;
    }

    internal void ValidateReplacement(GltfDocument replacement, CancellationToken token)
    {
        // External edits provide no trusted old-to-new polygon map. Shape-preserving deformation is
        // accepted; changed connectivity/hierarchy refuses even when a checkout conflict is forced.
        if (!Any) return;
        var layout = WorldGltf.ZoneLayout.Read(replacement, token);
        foreach (var owner in owners)
            foreach (var asset in owner.Assets) WorldGltf.ValidateZoneProfile(layout, asset.Profile, asset.GeometryPath, token);
    }

    internal IReadOnlyList<(string Relative, byte[] Content)> Remap(GltfDocument before, GltfDocument after,
        JsonObject root, IReadOnlyDictionary<JsonNode, int> originalObjects, IReadOnlyDictionary<int, int> copies,
        CancellationToken token, IReadOnlySet<int>? newTerrainMarkers = null)
    {
        if (!Any) return [];
        var layout = WorldGltf.ZoneLayout.Read(after, token);
        var oldNodes = before.AllNodes().Distinct().ToArray();
        var oldOrdinals = oldNodes.Select((node, i) => (node.Index, Ordinal: i)).ToDictionary(x => x.Index, x => x.Ordinal);
        var oldParents = Parents(before); var newParents = Parents(after);
        Dictionary<long, int> oldInstances = [];
        foreach (var node in oldNodes) if (Mark(node) is { } mark) oldInstances.TryAdd(mark, node.Index);
        Dictionary<GltfMesh, int> oldMeshes = new(ReferenceEqualityComparer.Instance);
        foreach (var node in oldNodes) if (node.Mesh is { } mesh) oldMeshes.TryAdd(mesh, oldMeshes.Count);
        var newNodes = after.AllNodes().Distinct().ToArray();
        var jsonNodes = (JsonArray)root["nodes"]!;
        Dictionary<int, int> origins = [];
        foreach (var node in newNodes)
        {
            token.ThrowIfCancellationRequested();
            if (originalObjects.TryGetValue(jsonNodes[node.Index]!, out int original) || copies.TryGetValue(node.Index, out original))
                origins.Add(node.Index, original);
            else if (newTerrainMarkers?.Contains(node.Index) == true && node.Mesh == null && node.Children.Count == 0 && node.Extras?[WorldGltf.Key]?["terrain"] != null)
                origins.Add(node.Index, -1);
            else throw new InvalidDataException("The geometry edit introduced a node without an exact zone identity.");
        }
        List<(string, byte[])> changes = [];
        Dictionary<long, int> changedInstances = [];
        HashSet<long> firstInstances = [];
        foreach (var node in newNodes)
        {
            if (Mark(node) is not { } mark || !firstInstances.Add(mark) || origins[node.Index] is not (>= 0 and var original)) continue;
            if (Mark(oldNodes[oldOrdinals[original]]) is not { } previousMark) continue;
            int previousFirst = oldInstances[previousMark];
            int parent = newParents.GetValueOrDefault(node.Index, -1);
            int parentOrigin = parent < 0 ? -1 : origins[parent];
            if (previousFirst != original || oldParents.GetValueOrDefault(previousFirst, -1) != parentOrigin)
                changedInstances.Add(mark, previousFirst);
        }
        // The importer ignores the complete subtree of later instance holders, not just
        // their root fields. A map edit can therefore leave inactive child assignments
        // different from the first definition. Carry that definition's entire exact
        // subtree when a deletion/reorder promotes another holder.
        Dictionary<int, int> promotedOrigins = [];
        LookupWorkBudget promotionWork = new(LookupWorkBudget.MaximumUnits, token);
        foreach (var node in newNodes)
        {
            if (Mark(node) is not { } mark || !changedInstances.TryGetValue(mark, out int first)) continue;
            Stack<(GltfNode Current, GltfNode Previous)> pending = new();
            pending.Push((node, oldNodes[oldOrdinals[first]]));
            while (pending.TryPop(out var pair))
            {
                promotionWork.Reserve(1L + pair.Current.Children.Count);
                if (promotedOrigins.TryGetValue(pair.Current.Index, out int known))
                {
                    if (known != pair.Previous.Index) throw new InvalidDataException("The promoted instance has ambiguous child zone identities.");
                    continue;
                }
                int original = origins[pair.Current.Index];
                var priorCopy = original < 0 ? null : oldNodes[oldOrdinals[original]];
                if (priorCopy == null || pair.Current.Children.Count != pair.Previous.Children.Count ||
                    !ReferenceEquals(priorCopy.Mesh, pair.Previous.Mesh) ||
                    !string.Equals(WorldGltf.EngineName(priorCopy), WorldGltf.EngineName(pair.Previous), StringComparison.Ordinal))
                    throw new InvalidDataException("The promoted instance differs from its first definition; its child zone assignments cannot be preserved safely.");
                promotedOrigins.Add(pair.Current.Index, pair.Previous.Index);
                for (int i = pair.Current.Children.Count - 1; i >= 0; i--)
                    pending.Push((pair.Current.Children[i], pair.Previous.Children[i]));
            }
        }
        foreach (var owner in owners)
        {
            Dictionary<string, SourceMapZoneAsset> replacements = new(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in owner.Assets)
            {
                token.ThrowIfCancellationRequested();
                List<WorldNodeZone> nodes = []; List<IReadOnlyList<uint>> meshes = []; List<SourceMapZoneReference> references = [];
                var oldReferences = asset.References.ToDictionary(r => r.Node);
                HashSet<GltfMesh> seenMeshes = new(ReferenceEqualityComparer.Instance);
                foreach (var node in newNodes)
                {
                    token.ThrowIfCancellationRequested();
                    int original = promotedOrigins.GetValueOrDefault(node.Index, origins[node.Index]);
                    if (original < 0) { nodes.Add(new(255, false, true)); continue; }
                    int ordinal = oldOrdinals[original];
                    var zone = asset.Profile.Nodes[ordinal];
                    int oldParent = oldParents.GetValueOrDefault(original, -1);
                    int newParent = newParents.GetValueOrDefault(node.Index, -1);
                    int parentOrigin = newParent < 0 ? -1 : origins[newParent];
                    if (Mark(node) is { } mark && changedInstances.TryGetValue(mark, out int first))
                    {
                        // All copies of an instance use its first definition. Removing/moving that
                        // holder must not make the next copy inherit a different parent's zone.
                        zone = asset.Profile.Nodes[oldOrdinals[first]];
                        if (zone.Inherit) zone = zone with { Word = zone.Word & ~255u | Effective(first), Inherit = false };
                    }
                    else if (zone.Inherit && parentOrigin != oldParent)
                        zone = zone with { Word = zone.Word & ~255u | Effective(original), Inherit = false };
                    if (oldReferences.TryGetValue(ordinal, out var reference)) references.Add(reference with { Node = nodes.Count });
                    nodes.Add(zone);
                    if (node.Mesh is { } mesh && seenMeshes.Add(mesh))
                    {
                        var oldMesh = oldNodes[ordinal].Mesh ?? throw new InvalidDataException("A new mesh has no source zone mapping.");
                        meshes.Add(asset.Profile.MeshPolygons[oldMeshes[oldMesh]]);
                    }
                }
                var profile = new WorldZoneProfile(layout.Fingerprint, nodes, meshes, asset.Profile.LoadRoot);
                WorldGltf.ValidateZoneProfile(layout, profile, asset.GeometryPath, token);
                replacements.Add(asset.LogicalPath, asset with { Profile = profile, References = references });

                uint Effective(int index)
                {
                    for (int depth = 0; index >= 0 && depth <= GltfDocument.MaximumDepth; depth++, index = oldParents.GetValueOrDefault(index, -1))
                    {
                        token.ThrowIfCancellationRequested();
                        if (Mark(oldNodes[oldOrdinals[index]]) is { } mark) index = oldInstances[mark];
                        var value = asset.Profile.Nodes[oldOrdinals[index]];
                        if (!value.Inherit) return value.Word & 255;
                    }
                    return asset.Profile.LoadRoot?.Word & 255 ?? throw new InvalidDataException("This moved object inherits its zone from each load. Give it an explicit object zone before moving it between parents.");
                }
            }
            changes.Add((owner.Path, new SourceMapZones(owner.Map.Assets.Select(a => replacements.GetValueOrDefault(a.LogicalPath) ?? a).ToArray(), owner.Map.Labels).Write(token)));
        }
        return changes;

        static long? Mark(GltfNode node) => (node.Extras?[WorldGltf.Key] as JsonObject)?["instance"] is { } value &&
            GltfInteger.TryInt64(value, out long mark) && mark > 0 ? mark : null;

        Dictionary<int, int> Parents(GltfDocument document)
        {
            Dictionary<int, int> result = [];
            foreach (var node in document.AllNodes())
            {
                token.ThrowIfCancellationRequested();
                foreach (var child in node.Children) result.TryAdd(child.Index, node.Index);
            }
            return result;
        }
    }
}
