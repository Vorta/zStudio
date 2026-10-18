using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>Current physical glTF references shared by builds and source dependency inspection.</summary>
internal static class SourceMissionModels
{
    internal static string? Reference(JsonObject? extras)
    {
        var engine = extras?[WorldGltf.Key] as JsonObject;
        if (engine?.ContainsKey(WorldGltf.ZoneReference) == true)
            throw new InvalidDataException("A source model needs embedded reference URIs. Reconstruct older projects from the original ZBD files.");
        return engine?["ref"] is JsonValue reference && reference.TryGetValue(out string? value) ? value : null;
    }

    internal static IEnumerable<string> Dependencies(string path, GltfDocument hierarchy, Action<long> reserve)
    {
        foreach (var node in hierarchy.AllNodes())
        {
            reserve(1);
            string? uri = Reference(node.Extras);
            if (uri == null) continue;
            reserve(4L * (path.Length + uri.Length + 1));
            yield return WorldAssembler.Relative(path, uri);
        }
    }
}
