using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;

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
    /// <item>in a <paramref name="pickup"/>, the mesh that only its collision volume shows gets fully transparent materials
    /// of its own (MASK with base alpha 0), recording their engine opacity so neither the look nor how an editor writes it
    /// back becomes it. A material other meshes also use is copied first. The game switches off the one node named
    /// <see cref="CollisionVolume"/> that FindSubNodeByName finds from the pickup (the file's roots and then each node's
    /// children from last to first); any other is drawn.</item>
    /// </list>
    /// <paramref name="texture"/> gives the transparency of an image by its unescaped URI, or null when unknown;
    /// <paramref name="pickup"/> is whether the file is loaded as a pickup (<see cref="IsPickupName"/>). Indices and values
    /// of another shape (a file another tool wrote) are left alone. Returns whether the file changed; applying it again
    /// changes nothing.
    /// </summary>
    public static bool ApplyPresentation(JsonObject root, Func<string, TextureTransparency?> texture, bool pickup)
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

        // A mesh is hidden when the collision volume the game switches off is the only node that shows it.
        Dictionary<int, bool> hidden = [];
        var volume = pickup ? SwitchedOff(root, nodes) : null;
        foreach (var node in nodes.OfType<JsonObject>())
            if (Index(node["mesh"], meshes.Count) is { } m)
                hidden[m] = hidden.GetValueOrDefault(m, true) && ReferenceEquals(node, volume);
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
                if (!visible.Contains(used)) { changed |= Hide(material); continue; }
                if (!copies.TryGetValue(used, out int copy))
                {
                    // A material that cannot be hidden (already hidden, or malformed) is not copied either.
                    var hiddenCopy = (JsonObject)material.DeepClone();
                    if (!Hide(hiddenCopy)) continue;
                    copies[used] = copy = materials.Count; materials.Add(hiddenCopy);
                }
                primitive["material"] = copy; changed = true;
            }
        return changed;

        static IEnumerable<JsonObject> Primitives(JsonNode? mesh) => (mesh?["primitives"] as JsonArray ?? []).OfType<JsonObject>();
    }

    /// <summary>
    /// Whether a node of this name is a pickup the game sets up and switches the collision volume of: a copy of a pickup
    /// template (pu000–pu039, Pickup::Init 0x41CCF0, Pickup::CreateObjectInstance 0x41DAB0), or a pickup placed in the
    /// world, named pu and a number whose hundreds are a pickup type (InitAndLoadPuppySpawns 0x41DE70, AssignBvolGroupAndId).
    /// </summary>
    public static bool IsPickupName(string name)
    {
        if (!name.StartsWith("pu", StringComparison.Ordinal) || name.Length < 5 || !char.IsAsciiDigit(name[2])) return false;
        if (name.Length == 5) return MissionPickupType.Catalog.Any(t => t.TemplateName == name);
        // atol: the leading digits; the type is the number's hundreds, refused above 40 (more digits only make it larger).
        long value = 0;
        for (int i = 2; i < name.Length && char.IsAsciiDigit(name[i]) && value <= 4100; i++) value = value * 10 + (name[i] - '0');
        return value / 100 <= 40;
    }

    /// <summary>
    /// The node a pickup's AssignBvolGroupAndId switches off: FindSubNodeByName(pickup, "bvol"), which compares names exactly
    /// and visits a node, then its children from last to first; the pickup's children are the file's roots.
    /// </summary>
    private static JsonObject? SwitchedOff(JsonObject root, JsonArray nodes)
    {
        var scenes = root["scenes"] as JsonArray ?? [];
        int scene = Index(root["scene"], scenes.Count) ?? 0;
        // The scene's roots, or without scenes every node that is no node's child (as GltfDocument reads them).
        var held = nodes.OfType<JsonObject>().SelectMany(n => n["children"] as JsonArray ?? []).Select(c => Index(c, nodes.Count)).OfType<int>().ToHashSet();
        var roots = scenes.Count == 0 ? Enumerable.Range(0, nodes.Count).Where(i => !held.Contains(i)).ToList()
            : (scenes[Math.Min(scene, scenes.Count - 1)] as JsonObject)?["nodes"] is JsonArray list ? list.Select(n => Index(n, nodes.Count)).OfType<int>().ToList() : [];
        HashSet<int> visited = [];
        for (int i = roots.Count - 1; i >= 0; i--) if (Find(roots[i], 0) is { } found) return found;
        return null;

        JsonObject? Find(int index, int depth)
        {
            if (depth > Gltf.GltfDocument.MaximumDepth || !visited.Add(index) || nodes[index] is not JsonObject node) return null;
            if (EngineName(node) == CollisionVolume) return node;
            var children = (node["children"] as JsonArray ?? []).Select(c => Index(c, nodes.Count)).OfType<int>().ToList();
            for (int k = children.Count - 1; k >= 0; k--) if (Find(children[k], depth + 1) is { } found) return found;
            return null;
        }
    }

    /// <summary>
    /// Makes a material fully transparent, keeping its colour and recording the opacity import reads from it, also in a
    /// material without engine attributes (with <c>normals</c>, which keeps how import reads them); false when it already
    /// is, or when its base colour is malformed (left alone).
    /// </summary>
    private static bool Hide(JsonObject material)
    {
        if (material["pbrMetallicRoughness"] is { } values && values is not JsonObject) return false;
        var pbr = material["pbrMetallicRoughness"] as JsonObject;
        string? mode = Text(material["alphaMode"]); double? alpha = BaseAlpha(pbr);
        if (alpha == null || mode == "MASK" && material["alphaCutoff"] == null && alpha is 0) return false;
        if (material["extras"] is { } other && other is not JsonObject) return false;
        var engine = (material["extras"] as JsonObject)?[Key];
        if (engine != null && engine is not JsonObject) return false;
        // Without a recorded opacity, import reads a BLEND material's base alpha (WorldGltf.ImportMaterial).
        int opacity = mode == "BLEND" && alpha is < 1 ? (int)Math.Clamp(MathF.Round((float)alpha.Value * 255), 0, 255) : 255;
        if (engine == null)
        {
            // A material without engine attributes keeps the primitive's normals; one with them only when it says so.
            if (material["extras"] is not JsonObject extras) material["extras"] = extras = [];
            extras[Key] = engine = new JsonObject { ["normals"] = true };
        }
        if (pbr == null) material["pbrMetallicRoughness"] = pbr = [];
        if (pbr["baseColorFactor"] is JsonArray factor) factor[3] = 0.0;
        else pbr["baseColorFactor"] = new JsonArray(1.0, 1.0, 1.0, 0.0);
        material["alphaMode"] = "MASK"; material.Remove("alphaCutoff");
        if (Text(material["name"]) is { } name && !name.EndsWith(HiddenSuffix, StringComparison.Ordinal)) material["name"] = name + HiddenSuffix;
        if (engine["opacity"] == null) engine["opacity"] = opacity;
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
    internal static string EngineName(JsonObject node) =>
        Text(((node["extras"] as JsonObject)?[Key] as JsonObject)?["name"]) ?? BlenderSuffix().Replace(Text(node["name"]) ?? "", "");
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    private static int? Index(JsonNode? node, int count) => GltfInteger.TryInt64(node, out long index) && index >= 0 && index < count ? (int)index : null;
}
