using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Worlds;

public static partial class WorldGltf
{
    /// <summary>
    /// The node holding a pickup's collision volume. The game registers an object as a pickup only when it has one, and
    /// switches it off at once, so it is never drawn (retail Pickup::AssignBvolGroupAndId 0x41DB60).
    /// </summary>
    public const string CollisionVolume = "bvol";
    private const string HiddenSuffix = "~hidden";

    /// <summary>
    /// Shows a model in glTF viewers (Blender) as the game draws it, changing nothing a build reads:
    /// <list type="bullet">
    /// <item>a material whose texture is transparent is marked so: alpha only 0 or 255 as MASK at the default 0.5 cutoff
    /// (the packs key texels below 128), any other alpha as BLEND. The game takes transparency from the texture; import
    /// reads opacity only from a BLEND material whose base alpha is below 1, which these are not.</item>
    /// <item>meshes that only collision volumes (<see cref="CollisionVolume"/>) show get fully transparent materials of
    /// their own (MASK with base alpha 0, which Blender writes back unchanged), recording their engine opacity so the look
    /// never becomes it. A material other meshes also use is copied first.</item>
    /// </list>
    /// <paramref name="texture"/> gives the transparency of an image by its unescaped URI, or null when unknown. Indices
    /// and values of another shape (a file another tool wrote) are left alone. Returns whether the file changed; applying
    /// it again changes nothing.
    /// </summary>
    public static bool ApplyPresentation(JsonObject root, Func<string, TextureTransparency?> texture)
    {
        if (root["materials"] is not JsonArray materials) return false;
        bool changed = false;
        var nodes = root["nodes"] as JsonArray ?? []; var meshes = root["meshes"] as JsonArray ?? [];
        var textures = root["textures"] as JsonArray ?? []; var images = root["images"] as JsonArray ?? [];

        foreach (var material in materials.OfType<JsonObject>())
        {
            var pbr = material["pbrMetallicRoughness"] as JsonObject;
            if (Text(material["alphaMode"]) is not (null or "OPAQUE") || BaseAlpha(pbr) is not 1) continue;
            if (Index(pbr?["baseColorTexture"]?["index"], textures.Count) is not { } t || Index(textures[t]?["source"], images.Count) is not { } i
                || Text(images[i]?["uri"]) is not { } uri || uri.StartsWith("data:", StringComparison.Ordinal)) continue;
            string? mode = texture(Uri.UnescapeDataString(uri)) switch { TextureTransparency.Keyed => "MASK", TextureTransparency.Alpha => "BLEND", _ => null };
            if (mode == null) continue;
            material["alphaMode"] = mode; changed = true;
        }

        // A mesh is hidden when every node that shows it is a collision volume.
        Dictionary<int, bool> hidden = [];
        foreach (var node in nodes.OfType<JsonObject>())
            if (Index(node["mesh"], meshes.Count) is { } m)
                hidden[m] = hidden.GetValueOrDefault(m, true) && EngineName(node) == CollisionVolume;
        HashSet<int> visible = [];
        for (int m = 0; m < meshes.Count; m++)
            if (!hidden.GetValueOrDefault(m))
                foreach (var primitive in Primitives(meshes[m]))
                    if (Index(primitive["material"], materials.Count) is { } used) visible.Add(used);
        Dictionary<int, int> copies = [];
        foreach (int m in hidden.Where(h => h.Value).Select(h => h.Key).Order())
            foreach (var primitive in Primitives(meshes[m]))
            {
                if (Index(primitive["material"], materials.Count) is not { } used || materials[used] is not JsonObject material) continue;
                if (visible.Contains(used))
                {
                    if (!copies.TryGetValue(used, out int copy)) { copies[used] = copy = materials.Count; materials.Add(material.DeepClone()); }
                    primitive["material"] = copy; material = (JsonObject)materials[copy]!; changed = true;
                }
                changed |= Hide(material);
            }
        return changed;

        static IEnumerable<JsonObject> Primitives(JsonNode? mesh) => (mesh?["primitives"] as JsonArray ?? []).OfType<JsonObject>();
    }

    /// <summary>
    /// Makes a material fully transparent, keeping its colour and recording the opacity import read from it; false when it
    /// already is, or when it has no engine attributes to record an opacity below 255 in (adding some would also change
    /// how import reads its normals).
    /// </summary>
    private static bool Hide(JsonObject material)
    {
        var pbr = material["pbrMetallicRoughness"] as JsonObject;
        string? mode = Text(material["alphaMode"]); double? alpha = BaseAlpha(pbr);
        if (mode == "MASK" && material["alphaCutoff"] == null && alpha is 0) return false;
        var engine = (material["extras"] as JsonObject)?[Key] as JsonObject;
        // Without a recorded opacity, import reads a BLEND material's base alpha (WorldGltf.ImportMaterial).
        int opacity = mode == "BLEND" && alpha is < 1 ? (int)Math.Clamp(MathF.Round((float)alpha.Value * 255), 0, 255) : 255;
        if (engine == null && opacity != 255) return false;
        if (pbr == null) material["pbrMetallicRoughness"] = pbr = [];
        if (pbr["baseColorFactor"] is JsonArray { Count: 4 } factor && alpha != null) factor[3] = 0.0;
        else pbr["baseColorFactor"] = new JsonArray(1.0, 1.0, 1.0, 0.0);
        material["alphaMode"] = "MASK"; material.Remove("alphaCutoff");
        if (Text(material["name"]) is { } name && !name.EndsWith(HiddenSuffix, StringComparison.Ordinal)) material["name"] = name + HiddenSuffix;
        if (engine != null && engine["opacity"] == null) engine["opacity"] = opacity;
        return true;
    }

    /// <summary>The alpha of a material's base colour factor (1 when it has none), or null when the factor is malformed.</summary>
    private static double? BaseAlpha(JsonObject? pbr) => pbr?["baseColorFactor"] switch
    {
        null => 1,
        JsonArray { Count: 4 } factor when factor.All(c => Finite(c) != null) => Finite(factor[3]),
        _ => null,
    };
    /// <summary>A finite number, read or built in memory, or null.</summary>
    private static double? Finite(JsonNode? node) => node is not JsonValue value ? null
        : value.TryGetValue(out double d) ? (double.IsFinite(d) ? d : null) : value.TryGetValue(out float f) ? (float.IsFinite(f) ? f : null)
        : value.TryGetValue(out long l) ? l : value.TryGetValue(out int i) ? i : null;
    /// <summary>A node's engine name in glTF JSON, as <see cref="EngineName(GltfNode)"/> gives it.</summary>
    private static string EngineName(JsonObject node) =>
        Text(((node["extras"] as JsonObject)?[Key] as JsonObject)?["name"]) ?? BlenderSuffix().Replace(Text(node["name"]) ?? "", "");
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    private static int? Index(JsonNode? node, int count) => node is JsonValue value && value.TryGetValue(out int index) && index >= 0 && index < count ? index : null;
}
