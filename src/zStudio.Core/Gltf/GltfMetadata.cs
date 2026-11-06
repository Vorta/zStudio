using System.Text.Json;
using System.Text.Json.Nodes;

namespace zStudio.Core.Gltf;

/// <summary>The source metadata namespace. Legacy input is accepted; output has one canonical tree.</summary>
internal static class GltfMetadata
{
    internal const string Key = "zstudio", LegacyKey = "recoil";
    internal static JsonNode? Values(JsonNode? extras) => extras is JsonObject values
        ? values.ContainsKey(Key) ? values[Key] : values[LegacyKey] : null;
    internal static JsonObject CanonicalValues(JsonObject extras)
    {
        Canonicalize(extras);
        if (extras[Key] is JsonObject value) return value;
        if (extras[Key] != null) throw new InvalidDataException("The source attribute zstudio must be an object.");
        JsonObject created = []; extras[Key] = created; return created;
    }
    private static bool Canonicalize(JsonObject extras)
    {
        if (!extras.ContainsKey(LegacyKey)) return false;
        bool canonical = extras.ContainsKey(Key);
        if (extras.Remove(LegacyKey, out var legacy) && !canonical) extras[Key] = legacy;
        return true;
    }
    internal static void Normalize(GltfDocument document, CancellationToken token)
    {
        if (document.SceneExtras is { } scene) Canonicalize(scene);
        HashSet<GltfMesh> meshes = [];
        foreach (var node in document.AllNodes())
        {
            token.ThrowIfCancellationRequested();
            if (node.Extras is { } extras) Canonicalize(extras);
            if (node.Mesh is not { } mesh || !meshes.Add(mesh)) continue;
            if (mesh.Extras is { } fields) Canonicalize(fields);
            foreach (var primitive in mesh.Primitives)
            {
                if (primitive.Extras is { } values) Canonicalize(values);
                if (primitive.Material?.Extras is { } material) Canonicalize(material);
            }
        }
    }

    internal static bool TryRead(JsonElement extras, out JsonElement value)
    {
        value = default;
        return extras.ValueKind == JsonValueKind.Object
            && (extras.TryGetProperty(Key, out value) || extras.TryGetProperty(LegacyKey, out value));
    }

    internal static JsonObject? Parse(ReadOnlySpan<byte> bytes, CancellationToken token = default, int maximumDepth = 64)
    {
        GltfDocument.ValidateJsonText(bytes, token);
        var root = JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = maximumDepth }) as JsonObject;
        if (root?["asset"] is JsonObject) Normalize(root, token);
        return root;
    }

    internal static bool Normalize(JsonNode root, CancellationToken token = default)
    {
        bool changed = false;
        Owner(root); Owner(root["asset"]);
        foreach (string name in (ReadOnlySpan<string>)["scenes", "nodes", "meshes", "materials", "textures", "images", "samplers", "accessors", "bufferViews", "buffers", "animations"])
            if (root[name] is JsonArray entries)
                foreach (var entry in entries)
                {
                    Owner(entry);
                    if (name == "meshes" && entry?["primitives"] is JsonArray primitives) foreach (var primitive in primitives) Owner(primitive);
                    if (name == "animations")
                        foreach (string field in (ReadOnlySpan<string>)["channels", "samplers"])
                            if (entry?[field] is JsonArray children) foreach (var child in children) Owner(child);
                }
        return changed;
        void Owner(JsonNode? node)
        {
            token.ThrowIfCancellationRequested();
            if (node is JsonObject owner && owner["extras"] is JsonObject extras) changed |= Canonicalize(extras);
        }
    }
}
