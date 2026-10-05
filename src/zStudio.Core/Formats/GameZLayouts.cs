using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

/// <summary>
/// Serialized layouts for the supported world formats: RECOIL version 15 (the releases), RECOIL version 13 (the July and
/// August 1998 demos, read-only) and MechWarrior 3 version 27.
/// </summary>
internal sealed class GameZLayouts(uint version)
{
    private static readonly GameZLayouts recoil = new(15), demo = new(13), mw = new(27);
    private readonly bool mw3 = version == 27;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonArray> definitions = new();
    internal static GameZLayouts For(uint? version) => version switch
    {
        15 => recoil, 13 => demo, 27 => mw, _ => throw new InvalidDataException("Unsupported world version.")
    };
    internal uint Version => version;
    /// <summary>
    /// Version 13 stores a node's cached box as its eight corners in the parent's space (the box transformed by the node's
    /// matrix) where version 15 stores the box itself, and an Object3D's translation beside its matrix (corpus bytes of the
    /// 1998 demos: every corner set is the node's model-and-child box under its own matrix).
    /// </summary>
    internal bool Demo => version == 13;
    internal bool HasVertexColors => mw3;
    // Supported limits for per-record metadata, far above retail pools (at most 20,000 models/nodes and about
    // 35,000 polygons per world). They bound allocation before any record is materialized.
    internal const int MaximumTableEntries = FormatRegistry.MaximumDirectoryEntries, MaximumGeometryRecords = 262_144;
    // Dense decoded geometry per file; retail worlds use at most 77,559 model vectors and 455,526 polygon corner elements.
    internal const int MaximumModelVectors = 1_048_576, MaximumPolygonCorners = 4_194_304;
    internal static void CheckEntries(string table, long count, long maximum = MaximumTableEntries) => FormatRegistry.CheckEntries("GameZ " + table, count, maximum);
    internal int TextureSize => mw3 ? 40 : 36;
    internal int ModelSize => mw3 ? 92 : 84;
    internal int PolygonSize => mw3 ? 36 : 28;
    internal int NodeSize => mw3 ? 208 : Demo ? 264 : 192;
    internal int Object3DSize => Demo ? 156 : 144;
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
        if (Demo)
        {
            switch (name)
            {
                case "GAMEZ_NODE_BASE_LAYOUT":
                    // The cached box becomes its eight corners in the parent's space; the model and child boxes follow.
                    fields = new JsonArray(fields.OfType<JsonArray>().Where(f => f[1]!.GetValue<int>() < 116).Select(f => f.DeepClone()).ToArray());
                    Add(fields, "node_corners", 116, 96, "f32a:24"); Add(fields, "model_bbox", 212, 24, "bbox"); Add(fields, "child_bbox", 236, 24, "bbox");
                    Add(fields, "activation_ptr", 260, 4, "ptr");
                    break;
                case "GAMEZ_OBJECT3D_LAYOUT":
                    // Rotation, scale, then the translation the matrix also carries.
                    Shift(fields, 48, 12);
                    Add(fields, "translate", 48, 12, "vec3");
                    break;
            }
        }
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
