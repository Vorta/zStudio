using System.Text.Json.Nodes;
using zStudio.Core.Gltf;

namespace zStudio.Core.Worlds;

public static partial class WorldGltf
{
    private static readonly string[] PolygonZoneFields = ["polygonZones", "polygonZonePadding", "polygonZoneRaw"];

    /// <summary>
    /// Whether two versions of a source glTF differ at most in polygon zones (a primitive's defaults or a reference
    /// placement's override) and the map's zone labels: everything a world build reads apart from those is the same.
    /// </summary>
    public static bool PolygonZonesOnlyChanged(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, CancellationToken token = default)
    {
        var a = Comparable(before, token, out var aBinary);
        var b = Comparable(after, token, out var bBinary);
        return aBinary.SequenceEqual(bBinary) && JsonNode.DeepEquals(a, b);
    }

    private static JsonObject Comparable(ReadOnlySpan<byte> bytes, CancellationToken token, out byte[] binary)
    {
        var json = GltfDocument.ContainerJson(bytes, token, out var glbBinary);
        binary = glbBinary.ToArray();
        var root = JsonNode.Parse(json, documentOptions: new() { MaxDepth = 64 }) as JsonObject ?? throw new InvalidDataException("The source model is not a JSON object.");
        if (root["meshes"] is JsonArray meshes)
            foreach (var mesh in meshes)
                if (mesh?["primitives"] is JsonArray primitives)
                    foreach (var primitive in primitives)
                    { token.ThrowIfCancellationRequested(); RemovePolygonZones(primitive?["extras"]?[Key] as JsonObject); PruneExtras(primitive as JsonObject); }
        if (root["nodes"] is JsonArray nodes)
            foreach (var node in nodes)
            { token.ThrowIfCancellationRequested(); RemoveOverrideZones(node?["extras"]?[Key]?["zoneOverrides"] as JsonObject, 0, token); }
        if (root["scenes"] is JsonArray scenes)
            foreach (var scene in scenes) { (scene?["extras"]?[Key] as JsonObject)?.Remove("zoneLabels"); PruneExtras(scene as JsonObject); }
        return root;
    }
    /// <summary>A first assignment creates its extras: emptied ones compare as absent.</summary>
    private static void PruneExtras(JsonObject? owner)
    {
        if (owner?["extras"] is not JsonObject extras) return;
        if (extras[Key] is JsonObject { Count: 0 }) extras.Remove(Key);
        if (extras.Count == 0) owner.Remove("extras");
    }
    private static void RemovePolygonZones(JsonObject? values)
    {
        if (values == null) return;
        foreach (string field in PolygonZoneFields) values.Remove(field);
    }
    private static void RemoveOverrideZones(JsonObject? scope, int depth, CancellationToken token)
    {
        if (scope == null || depth > GltfDocument.MaximumDepth) return;
        if (scope["meshes"] is JsonArray meshes) foreach (var mesh in meshes) RemovePolygonZones(mesh as JsonObject);
        if (scope["references"] is JsonObject references)
            foreach (var (_, nested) in references) { token.ThrowIfCancellationRequested(); RemoveOverrideZones(nested as JsonObject, depth + 1, token); }
    }
}
