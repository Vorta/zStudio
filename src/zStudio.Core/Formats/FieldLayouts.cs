using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

internal static class FieldLayouts
{
    internal static readonly JsonObject Definitions = Load();
    private static JsonObject Load()
    {
        using Stream stream = typeof(FieldLayouts).Assembly.GetManifestResourceStream("Recoil.Zbd.Core.Formats.field-layouts.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    }
    public static JsonObject Read(BinaryCursor cursor, int size, string layout) => Decode(cursor.Take(size), Definitions["layouts"]![layout]!.AsArray());
    public static JsonObject Decode(ReadOnlyMemory<byte> bytes, JsonArray layout)
    {
        JsonObject result = [];
        foreach (JsonNode? definition in layout)
        {
            JsonArray field = definition!.AsArray();
            string name = field[0]!.GetValue<string>(), type = field[3]!.GetValue<string>();
            int offset = field[1]!.GetValue<int>(), size = field[2]!.GetValue<int>();
            BinaryCursor.CheckRange(bytes.Length, offset, size);
            BinaryCursor c = new(bytes.Slice(offset, size));
            result[name] = ReadValue(c, type, size);
        }
        return result;
    }
    private static JsonNode? ReadValue(BinaryCursor c, string type, int size)
    {
        if (type.Contains(':'))
        {
            JsonArray a = []; string[] parts = type.Split(':'); int count = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            c.Count((uint)count, 4);
            for (int i = 0; i < count; i++) a.Add(ReadValue(c, parts[0][..^1], 4));
            return a;
        }
        switch (type)
        {
            case "str":
                var raw = c.Take(size); string text = BinaryCursor.FixedString(raw.Span);
                int nul = raw.Span.IndexOf((byte)0);
                return nul >= 0 && raw.Span[(nul + 1)..].ContainsAnyExcept((byte)0)
                    ? new JsonObject { ["text"] = text, ["trailing_hex"] = Convert.ToHexStringLower(raw.Span[(nul + 1)..]) } : JsonValue.Create(text);
            case "u8": return JsonValue.Create((long)c.U8());
            case "u16": return JsonValue.Create((long)c.U16());
            case "i32": return JsonValue.Create((long)c.I32());
            case "u32": return JsonValue.Create((long)c.U32());
            case "f32": return JsonData.Number(c.F32());
            case "ptr": return JsonValue.Create($"0x{c.U32():X8}");
            case "bool32": return JsonValue.Create(c.U32() != 0);
            case "opt_index": int n = c.I32(); return n == -1 ? null : JsonValue.Create((long)n);
            case "vec3": return new JsonObject { ["x"] = JsonData.Number(c.F32()), ["y"] = JsonData.Number(c.F32()), ["z"] = JsonData.Number(c.F32()) };
            case "color": return new JsonObject { ["r"] = JsonData.Number(c.F32()), ["g"] = JsonData.Number(c.F32()), ["b"] = JsonData.Number(c.F32()) };
            case "range": return new JsonObject { ["min"] = JsonData.Number(c.F32()), ["max"] = JsonData.Number(c.F32()) };
            case "bbox": return new JsonObject { ["a"] = ReadValue(c, "vec3", 12), ["b"] = ReadValue(c, "vec3", 12) };
            case "partition2": return new JsonObject { ["x"] = (long)c.I32(), ["z"] = (long)c.I32() };
            case "zone_set": uint packed = c.U32(); JsonArray zones = []; for (int i = 0; i < Math.Min(packed & 255, 3); i++) zones.Add((long)(sbyte)(packed >> (8 + i * 8))); return zones;
            case "hex": return JsonValue.Create(Convert.ToHexStringLower(c.Take(size).Span));
            default: throw new InvalidDataException($"Unknown field type {type}.");
        }
    }
}
