using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

/// <summary>Serialized layouts for the two supported base-game world formats.</summary>
internal sealed class GameZLayouts(bool mw3)
{
    private static readonly GameZLayouts recoil = new(false), mw = new(true);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonArray> definitions = new();
    internal static GameZLayouts For(uint? version) => version switch
    {
        15 => recoil, 27 => mw, _ => throw new InvalidDataException("Unsupported world version.")
    };
    internal bool HasVertexColors => mw3;
    internal int TextureSize => mw3 ? 40 : 36;
    internal int ModelSize => mw3 ? 92 : 84;
    internal int PolygonSize => mw3 ? 36 : 28;
    internal int NodeSize => mw3 ? 208 : 192;
    internal int WorldSize => mw3 ? 188 : 172;
    internal int PartitionSize => mw3 ? 72 : 64;
    internal int LightSize => mw3 ? 208 : 228;
    internal JsonObject Read(BinaryCursor c, int size, string name) => Decode(c.Take(size), name);
    internal JsonObject Decode(ReadOnlyMemory<byte> bytes, string name)
    {
        return FieldLayouts.Decode(bytes, definitions.GetOrAdd(name, Build));
    }
    private JsonArray Build(string name)
    {
        var fields = (JsonArray)FieldLayouts.Definitions["layouts"]![name]!.DeepClone();
        if (mw3)
        {
            switch (name)
            {
                case "GAMEZ_TEXDIR_LAYOUT":
                    Shift(fields, 32, 4);
                    Add(fields, "category", 32, 4, "i32");
                    break;
                case "GAMEZ_MODEL_INFO_LAYOUT":
                    Shift(fields, 4, 4);
                    Add(fields, "facade_mode", 4, 4, "u32"); Add(fields, "active_polygon_index", 88, 4, "u32");
                    break;
                case "GAMEZ_POLYGON_INFO_LAYOUT":
                    Shift(fields, 20, 8);
                    Add(fields, "colors_ptr", 20, 4, "ptr"); Add(fields, "field24_ptr", 24, 4, "ptr");
                    break;
                case "GAMEZ_NODE_BASE_LAYOUT":
                    for (int offset = 192; offset < 208; offset += 4) Add(fields, $"field{offset}", offset, 4, "i32");
                    break;
                case "GAMEZ_WORLD_LAYOUT":
                    Shift(fields, 84, 16);
                    for (int offset = 84; offset < 100; offset += 4) Add(fields, $"virtual_bound_{offset}", offset, 4, "i32");
                    break;
                case "GAMEZ_WORLD_PARTITION_LAYOUT":
                    Add(fields, "field64", 64, 4, "i32"); Add(fields, "field68", 68, 4, "i32");
                    break;
                case "GAMEZ_LIGHT_LAYOUT":
                    // Version 27 packs the light switches into one flags word.
                    fields = new JsonArray(fields.OfType<JsonArray>().Where(f => f[1]!.GetValue<int>() < 176).Select(f => f.DeepClone()).ToArray());
                    Add(fields, "flags", 176, 4, "u32");
                    string[] scalarNames = ["range_near", "range_far", "range_near_squared", "range_far_squared", "range_inverse"];
                    for (int i = 0; i < scalarNames.Length; i++) Add(fields, scalarNames[i], 180 + i * 4, 4, "f32");
                    Add(fields, "attached_count", 200, 4, "u32"); Add(fields, "attached_ptr", 204, 4, "ptr");
                    break;
            }
        }
        return fields;
    }
    private static void Shift(JsonArray fields, int first, int delta)
    {
        foreach (var f in fields.OfType<JsonArray>()) if (f[1]!.GetValue<int>() >= first) f[1] = f[1]!.GetValue<int>() + delta;
    }
    private static void Add(JsonArray fields, string name, int offset, int size, string type) => fields.Add(new JsonArray(name, offset, size, type));
}
