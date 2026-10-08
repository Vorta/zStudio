using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

public static partial class SourceWorlds
{
    // A model brought from another map carries its reference bindings with it. Copy the dependency
    // closure into the receiving map's source transaction, never make builds consult another map.
    private static (string Path, byte[]? Bytes)? AdditionZones(SourceWorkspace workspace, string mission,
        string model, CancellationToken token)
    {
        model = SourceWorkspace.Normalize(model);
        string destination = SourceMapZones.PathForMission(mission);
        byte[]? existing = workspace.Read(destination, token, SourceMapZones.MaximumBytes);
        SourceMapZones own = existing == null ? new([]) : SourceMapZones.Parse(existing, token);
        if (own.TryGetAsset(model, out _)) return null;

        long remaining = 128L << 20;
        SourceMapZones? donor = null;
        string[] components = model.Split('/');
        // Mission-local geometry has an explicit owner. A common file may have several map profiles;
        // accepting an arbitrary one would silently choose that map's object references and zones.
        IEnumerable<string> candidates = components.Length > 2 && MissionName().IsMatch(components[1])
            ? [components[1].ToLowerInvariant()]
            : SourceProject.MissionFolders(workspace.Root, token).Select(m => m.ToLowerInvariant());
        byte[]? selected = null;
        foreach (string candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            string path = SourceMapZones.PathForMission(candidate);
            byte[]? bytes = workspace.Read(path, token, Math.Min(remaining, SourceMapZones.MaximumBytes));
            if (bytes == null) continue;
            remaining -= bytes.Length;
            var map = SourceMapZones.Parse(bytes, token);
            if (!map.TryGetAsset(model, out _)) continue;
            var closure = Closure(map);
            byte[] identity = new SourceMapZones(closure).Write(token);
            if (selected != null && !selected.AsSpan().SequenceEqual(identity))
                throw new InvalidDataException("This shared model has different bindings in several maps. Choose a mission-local model with an explicit zone profile before adding it.");
            selected = identity; donor = map;
        }
        if (donor == null) return null; // An ordinary authored glTF still uses its inline data.
        List<SourceMapZoneAsset> assets = [.. own.Assets];
        foreach (var asset in Closure(donor))
        {
            token.ThrowIfCancellationRequested();
            if (own.TryGetAsset(asset.LogicalPath, out _)) continue;
            if (assets.Count >= SourceMapZones.MaximumAssets)
                throw new InvalidDataException("Adding this model exceeds the map's zone asset limit.");
            assets.Add(asset);
        }
        return (destination, new SourceMapZones(assets, own.Labels).Write(token));

        List<SourceMapZoneAsset> Closure(SourceMapZones source)
        {
            List<SourceMapZoneAsset> result = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            Queue<string> pending = new(); pending.Enqueue(model);
            while (pending.TryDequeue(out string? path))
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(path) || !source.TryGetAsset(path, out var asset)) continue;
                result.Add(asset);
                foreach (var reference in asset.References)
                    if (!seen.Contains(reference.Asset)) pending.Enqueue(reference.Asset);
            }
            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.LogicalPath, b.LogicalPath));
            return result;
        }
    }
}
