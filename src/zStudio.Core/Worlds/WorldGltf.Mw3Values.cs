using System.Text.Json.Nodes;
using System.Text.Json;

namespace zStudio.Core.Worlds;

public static partial class WorldGltf
{
    internal readonly record struct SourceIdentity(string? Game, string? LibraryMember, bool InvalidLibraryMember);
    /// <summary>Inspect the selected scene's source identity through the shared JSON/GLB framing, without reading buffers.</summary>
    internal static SourceIdentity ReadSourceIdentity(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        var json = Gltf.GltfDocument.ContainerJson(bytes, token, out _);
        using var document = JsonDocument.Parse(json.ToArray(), new() { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array) return default;
        int selected = 0;
        if (root.TryGetProperty("scene", out var index)) { if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out selected)) return default; }
        else if (scenes.GetArrayLength() != 1) return default;
        if (selected < 0 || selected >= scenes.GetArrayLength()) return default;
        var scene = scenes[selected];
        if (scene.ValueKind != JsonValueKind.Object || !scene.TryGetProperty("extras", out var extras) || extras.ValueKind != JsonValueKind.Object
            || !extras.TryGetProperty(Key, out var values) || values.ValueKind != JsonValueKind.Object) return default;
        string? game = values.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
        if (!values.TryGetProperty("mechLibraryMember", out var member)) return new(game, null, false);
        return member.ValueKind == JsonValueKind.String ? new(game, member.GetString(), false) : new(game, null, true);
    }
    private static WorldMaterial Mw3OwnedMaterial(ImportContext context, WorldModel model, WorldMaterial source)
    {
        if (context.Mw3ModelMaterials.TryGetValue((model, source), out var material)) return material;
        WorldMaterialCycle? cycle = null;
        if (source.Mw3Cycle is { } value)
        {
            cycle = new() { Loop = value.Loop, Frame = value.Frame, Time = value.Time, Speed = value.Speed, Index = value.Index };
            cycle.Textures.AddRange(value.Textures);
        }
        material = new() { Mw3Flags = source.Mw3Flags, Mw3Cycle = cycle, Flags = source.Flags, PackedColor = source.PackedColor,
            Color = source.Color, Texture = source.Texture, Field14 = source.Field14, Field18 = source.Field18, Field1C = source.Field1C, Soil = source.Soil };
        if (context.World.Materials.Count >= context.World.MaterialCapacity) throw new InvalidDataException("The MW3 material pool is full; increase its capacity in the initialization script.");
        context.World.Materials.Add(material); context.Mw3ModelMaterials.Add((model, source), material); return material;
    }
    internal static void ApplyMw3LoadRoot(WorldNode node, Gltf.GltfDocument document, string path)
        => ReadMw3Node(node, SourceSceneValues(document, path)?["loadRoot"] as JsonObject, path);

    internal static IReadOnlyDictionary<string, int> Mw3TextureDefaults(Gltf.GltfDocument document, string path)
    {
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        if (SourceSceneValues(document, path)?["mw3TextureAddressing"] is not { } value) return result;
        if (value is not JsonObject modes || modes.Count > 65_536) throw new InvalidDataException($"{path}: native texture addressing must be an object with at most 65,536 texture names.");
        foreach (var pair in modes)
        {
            if (pair.Key.Length is < 1 or > 31 || pair.Key.Any(c => c < 32 || c > 255 || c is '/' or '\\')) throw new InvalidDataException($"{path}: invalid native texture addressing name.");
            if (!result.TryAdd(pair.Key, checked((int)Integer(pair.Value, "mw3TextureAddressing", path, 0, 3)))) throw new InvalidDataException($"{path}: duplicate native texture addressing name.");
        }
        return result;
    }
    private static void WriteMw3Node(WorldNode node, JsonObject extras)
    {
        if (node.Mw3Words.Length == 0) return;
        extras["mw3NodeWords"] = new JsonArray(node.Mw3Words.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
        extras["mw3Flags"] = $"0x{node.Flags:X8}";
    }

    private static void ReadMw3Node(WorldNode node, JsonObject? extras, string path)
    {
        if (extras?["mw3NodeWords"] is not { } native) return;
        if (native is not JsonArray { Count: 4 } values) throw new InvalidDataException($"{path}: MW3 node words need four integers.");
        node.Mw3Words = values.Select(v => checked((int)Integer(v, "mw3NodeWords", path, int.MinValue, int.MaxValue))).ToArray();
        if (extras["mw3Flags"] is { } flags) node.Flags = (node.Flags & CarriedFlags) | (Hex(flags, "mw3Flags", path) & ~CarriedFlags);
    }

    private static void WriteMw3Material(WorldMaterial material, JsonObject extras)
    {
        if (material.Mw3Flags is { } flags) extras["mw3Flags"] = $"0x{flags:X4}";
        if (material.Mw3Cycle is not { } cycle) return;
        extras["mw3Cycle"] = new JsonObject
        {
            ["loop"] = cycle.Loop, ["speed"] = cycle.Speed,
            ["textures"] = new JsonArray(cycle.Textures.Select(t => (JsonNode)JsonValue.Create(t.Name)!).ToArray()),
        };
    }

    private static void ReadMw3Material(WorldMaterial material, JsonObject? extras, string path, ImportContext context, string? imageUri)
    {
        if (extras?["mw3Flags"] is { } flags)
        {
            uint word = Hex(flags, "mw3Flags", path);
            if (word > byte.MaxValue) throw new InvalidDataException($"{path}: MW3 material flags must fit one byte.");
            material.Mw3Flags = (ushort)word;
        }
        if (extras?["mw3Cycle"] is not JsonObject value) return;
        if (value["textures"] is not JsonArray textures || textures.Count > 4096) throw new InvalidDataException($"{path}: an MW3 material cycle needs at most 4,096 textures.");
        WorldMaterialCycle cycle = new()
        {
            Loop = value["loop"]?.GetValue<bool>() ?? false, Speed = Real(value["speed"]!, "cycle speed", path),
            Index = checked((uint)textures.Count),
        };
        int addressing = material.Texture != null ? context.TextureAddressing.GetValueOrDefault(material.Texture.Name) : 0;
        foreach (var texture in textures)
        {
            string frame = Text(texture!, "cycle texture", path);
            string uri = imageUri == null ? "" : imageUri[..(imageUri.LastIndexOf('/') + 1)] + frame + ".png";
            string name = context.TextureName(uri, frame, path);
            if (!context.TextureAddressing.TryAdd(name, addressing) && context.TextureAddressing[name] != addressing)
                throw new InvalidDataException($"{path}: cycle frame {name} is sampled with different edge modes.");
            cycle.Textures.Add(Texture(context, name));
        }
        material.Mw3Cycle = cycle;
    }
}
