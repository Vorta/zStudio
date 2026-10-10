using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>One mission's logical model bindings, shared by builds and source editing.</summary>
internal sealed class SourceMissionModels
{
    internal SourceMapZones? Map { get; }
    private readonly Dictionary<string, Dictionary<int, string>> references = new(StringComparer.OrdinalIgnoreCase);

    internal SourceMissionModels(SourceMapZones? map, CancellationToken token)
    {
        Map = map;
        if (map == null) return;
        foreach (var asset in map.Assets)
        {
            token.ThrowIfCancellationRequested();
            Dictionary<int, string> nodes = [];
            foreach (var reference in asset.References)
            { token.ThrowIfCancellationRequested(); nodes.Add(reference.Node, reference.Spelling); }
            references.Add(asset.LogicalPath, nodes);
        }
    }

    internal SourceMapZoneAsset? Asset(string logical) => Map?.TryGetAsset(logical, out var asset) == true ? asset : null;
    internal string Geometry(string logical) => Asset(logical)?.GeometryPath ?? logical;
    internal string? Reference(string logical, int node) => references.TryGetValue(logical, out var nodes) ? nodes.GetValueOrDefault(node) : null;

    internal static string? EffectiveReference(JsonObject? extras, string? binding)
    {
        var engine = extras?[WorldGltf.Key] as JsonObject;
        // WorldGltf imports map URIs only on neutral holders. A legacy ref remains authoritative.
        if (engine?[WorldGltf.ZoneReference] != null)
            return binding ?? throw new InvalidDataException("The model needs its map reference bindings before its dependencies can be checked.");
        return engine?["ref"] is JsonValue reference && reference.TryGetValue(out string? value) ? value : null;
    }

    internal IEnumerable<string> Dependencies(string logical, GltfDocument hierarchy, Action<long> reserve)
    {
        int ordinal = 0;
        foreach (var node in hierarchy.AllNodes())
        {
            reserve(1);
            string? uri = EffectiveReference(node.Extras, Reference(logical, ordinal++));
            if (uri == null) continue;
            reserve(4L * (logical.Length + uri.Length + 1));
            yield return WorldAssembler.Relative(logical, uri);
        }
    }

    internal bool Exists(string logical, Func<string, bool> exists)
    {
        if (Asset(logical) is not { } asset) return exists(logical);
        // A missing backing file is a broken binding, not permission to search another directory.
        if (!logical.Equals(asset.GeometryPath, StringComparison.OrdinalIgnoreCase) && exists(logical))
            throw new InvalidDataException($"{JsonData.ShownText(logical)} exists both as a map zone alias and as a physical model. Remove the conflicting alias or rename the model before building.");
        return true;
    }
}
