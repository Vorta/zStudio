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
    /// retains these recorded engine materials while generic MASK imports are handled or refused explicitly.</item>
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
    /// Takes back what <see cref="ApplyPresentation"/> wrote where a build could not tell it from an authored value, in a file
    /// an editor wrote (a Blender export of a checkout), so the build reads the engine's material, also when the editor
    /// dropped the engine attributes (Blender's Custom Properties off):
    /// <list type="bullet">
    /// <item>Alpha Clip (MASK) at the default 0.5 cutoff with an opaque base colour, in a material that records no texture
    /// name, over a texture whose alpha is only 0 or 255 (or that has none): the packs draw that texture so (they key out the
    /// texels below 128), so the material is opaque again. A clip at another cutoff, or over graded alpha (which the game
    /// blends), stays for the build to refuse.</item>
    /// <item>a hidden collision volume's material (<c>~hidden</c>, MASK with base alpha 0) that lost its recorded opacity:
    /// it becomes the opaque material it was hidden from.</item>
    /// </list>
    /// <paramref name="texture"/> gives the transparency of an image by its unescaped URI (null when unknown); it is asked only
    /// for the images of such clipped materials. Values of another shape are left alone. Returns whether the file changed.
    /// </summary>
    public static bool RemovePresentation(JsonObject root, Func<string, TextureTransparency?> texture)
    {
        if (root["materials"] is not JsonArray materials) return false;
        var textures = root["textures"] as JsonArray ?? []; var images = root["images"] as JsonArray ?? [];
        bool changed = false;
        foreach (var material in materials.OfType<JsonObject>())
        {
            if (Text(material["alphaMode"]) != "MASK" || material["alphaCutoff"] is { } cutoff && Finite(cutoff) is not 0.5
                || material["pbrMetallicRoughness"] is { } values && values is not JsonObject) continue;
            var pbr = material["pbrMetallicRoughness"] as JsonObject;
            var engine = (material["extras"] as JsonObject)?[Key] as JsonObject;
            double? alpha = BaseAlpha(pbr);
            if (alpha is 1 && engine?["texture"] == null)
            {
                // Read before the glTF reader has checked the file: entries of another shape are no image.
                if (Index((pbr?["baseColorTexture"] as JsonObject)?["index"], textures.Count) is not { } t || Index((textures[t] as JsonObject)?["source"], images.Count) is not { } i
                    || Text((images[i] as JsonObject)?["uri"]) is not { } uri || uri.StartsWith("data:", StringComparison.Ordinal)
                    || texture(Uri.UnescapeDataString(uri)) is not (TextureTransparency.Keyed or TextureTransparency.Opaque)) continue;
            }
            else if (alpha is 0 && engine?["opacity"] == null && Text(material["name"]) is { } name && BlenderSuffix().Replace(name, "").EndsWith(HiddenSuffix, StringComparison.Ordinal))
                ((JsonArray)pbr!["baseColorFactor"]!)[3] = 1.0;
            else continue;
            material.Remove("alphaMode"); material.Remove("alphaCutoff"); changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Makes the engine values a material records follow the standard glTF values an editor shows them by, in a file it wrote
    /// (a Blender export of a checkout), wherever the two no longer agree: the editor changed them, and import would otherwise
    /// keep the recorded value. Run after <see cref="RemovePresentation"/>.
    /// <list type="bullet">
    /// <item><c>color</c> of a material without a texture, by component, from the base colour (rounded to 0–255 as import
    /// rounds it); a recorded component outside 0–255, which a glTF base colour cannot show, keeps its value while the
    /// colour shows as the nearest it can.</item>
    /// <item><c>opacity</c> from the alpha mode and base alpha: Alpha Blend gives its alpha, Alpha Clip 0 below the cutoff and
    /// 255 otherwise, opaque 255. A hidden collision volume's material (<c>~hidden</c>, Alpha Clip with base alpha 0) shows
    /// presentation, not its opacity, so its record stays.</item>
    /// <item><c>backface</c> from <c>doubleSided</c> (Blender's Backface Culling off).</item>
    /// </list>
    /// Values of another shape are left alone (import refuses what it cannot read). Returns whether the file changed.
    /// </summary>
    public static bool FollowShownMaterials(JsonObject root)
    {
        if (root["materials"] is not JsonArray materials) return false;
        bool changed = false;
        foreach (var material in materials.OfType<JsonObject>())
        {
            if ((material["extras"] as JsonObject)?[Key] is not JsonObject engine || material["pbrMetallicRoughness"] is { } values && values is not JsonObject) continue;
            var pbr = material["pbrMetallicRoughness"] as JsonObject;
            var factor = BaseColor(pbr);
            if (engine["texture"] == null && pbr?["baseColorTexture"] == null && engine["color"] is JsonArray { Count: 3 } color && factor != null)
                for (int c = 0; c < 3; c++)
                {
                    if (Finite(color[c]) is not { } recorded) break;
                    // Blender writes the float it read back; anything further from the shown value is the artist's colour.
                    float shown = (float)factor[c] * 255;
                    if (Math.Abs(shown - Math.Clamp(recorded, 0, 255)) <= 0.01) continue;
                    color[c] = MathF.Round(shown); changed = true;
                }
            double? cutoff = material["alphaCutoff"] is { } given ? Finite(given) : 0.5;
            if (Finite(engine["opacity"]) is { } opacity && factor != null && cutoff != null)
            {
                string mode = Text(material["alphaMode"]) ?? "OPAQUE";
                double alpha = factor[3];
                bool hidden = mode == "MASK" && alpha == 0 && Text(material["name"]) is { } name && BlenderSuffix().Replace(name, "").EndsWith(HiddenSuffix, StringComparison.Ordinal);
                int shown = mode switch
                {
                    "MASK" => alpha < cutoff ? 0 : 255,
                    "BLEND" when alpha < 1 => (int)Math.Clamp(MathF.Round((float)alpha * 255), 0, 255),
                    _ => 255,
                };
                if (!hidden && shown != opacity) { engine["opacity"] = shown; changed = true; }
            }
            if (Switch(engine["backface"]) is { } backface)
            {
                bool shown = material["doubleSided"] is JsonValue sided && sided.TryGetValue(out bool doubleSided) && doubleSided;
                if (shown != backface) { engine["backface"] = shown; changed = true; }
            }
        }
        return changed;

        // As import reads a switch: true or false, or an editor's integer 1 or 0.
        static bool? Switch(JsonNode? node) => node is not JsonValue value ? null : value.TryGetValue(out bool flag) ? flag
            : GltfInteger.TryInt64(value, out long whole) && whole is 0 or 1 ? whole == 1 : null;
    }

    /// <summary>A material's base colour factor (white and opaque when it has none), or null when it is malformed.</summary>
    private static double[]? BaseColor(JsonObject? pbr) => pbr?["baseColorFactor"] switch
    {
        null => [1, 1, 1, 1],
        JsonArray { Count: 4 } factor when factor.All(c => Finite(c) != null) => [.. factor.Select(c => Finite(c)!.Value)],
        _ => null,
    };

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
