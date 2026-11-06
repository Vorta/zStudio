using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

/// <summary>Lossless, named fixed records in MW3 source recipes. Unspecified bytes are zero; reserved bytes are explicit.
/// Unlike an inspection JSON export, these records have a versioned, bidirectional source contract.</summary>
internal static class Mw3Record
{
    internal static JsonArray Layout(params (string Name, int Offset, int Size, string Type)[] fields)
        => new(fields.Select(f => (JsonNode)new JsonArray(f.Name, f.Offset, f.Size, f.Type)).ToArray());

    internal static JsonObject Decode(ReadOnlySpan<byte> bytes, JsonArray layout)
    {
        bool[] covered = new bool[bytes.Length]; JsonObject fields = [], reserved = [];
        foreach (var item in layout.OfType<JsonArray>())
        {
            string name = item[0]!.GetValue<string>(), type = item[3]!.GetValue<string>();
            int offset = item[1]!.GetValue<int>(), size = item[2]!.GetValue<int>();
            BinaryCursor.CheckRange(bytes.Length, offset, size);
            covered.AsSpan(offset, size).Fill(true);
            fields[name] = Value(bytes.Slice(offset, size), type);
        }
        for (int i = 0; i < bytes.Length; i++)
            if (!covered[i] && bytes[i] != 0) reserved[i.ToString(CultureInfo.InvariantCulture)] = (int)bytes[i];
        if (reserved.Count > 0) fields["_reserved"] = reserved;
        return fields;
    }

    internal static byte[] Encode(JsonObject fields, int size, JsonArray layout)
    {
        if (size < 0 || size > 4096) throw new InvalidDataException("MW3 fixed record exceeds 4096 bytes.");
        byte[] bytes = new byte[size]; bool[] covered = new bool[size]; HashSet<string> known = ["_reserved"];
        foreach (var item in layout.OfType<JsonArray>())
        {
            string name = item[0]!.GetValue<string>(), type = item[3]!.GetValue<string>(); known.Add(name);
            int offset = item[1]!.GetValue<int>(), length = item[2]!.GetValue<int>();
            BinaryCursor.CheckRange(size, offset, length); covered.AsSpan(offset, length).Fill(true);
            Write(bytes.AsSpan(offset, length), type, fields[name] ?? throw new InvalidDataException($"Missing MW3 field {name}."));
        }
        if (fields.Any(f => !known.Contains(f.Key))) throw new InvalidDataException("Unknown MW3 record field.");
        if (fields["_reserved"] is JsonObject reserved)
            foreach (var (key, value) in reserved)
            {
                int offset = int.Parse(key, CultureInfo.InvariantCulture);
                if (offset < 0 || offset >= size || covered[offset]) throw new InvalidDataException("Reserved MW3 byte overlaps a named field.");
                bytes[offset] = checked((byte)value!.GetValue<int>());
            }
        return bytes;
    }

    private static JsonNode Value(ReadOnlySpan<byte> b, string type)
    {
        if (type.Contains(':'))
        {
            string[] p = type.Split(':'); JsonArray values = []; int count = int.Parse(p[1], CultureInfo.InvariantCulture);
            for (int i = 0; i < count; i++) values.Add(Value(b.Slice(i * 4, 4), p[0][..^1]));
            return values;
        }
        if (type == "str")
        {
            int end = b.IndexOf((byte)0); if (end < 0) end = b.Length;
            return new JsonObject { ["text"] = Encoding.Latin1.GetString(b[..end]), ["suffix"] = Convert.ToHexStringLower(b[end..]) };
        }
        if (type is "vec3" or "color" or "bbox" or "range" or "partition2")
        {
            JsonArray values = []; for (int i = 0; i < b.Length; i += 4) values.Add(Value(b.Slice(i, 4), type == "partition2" ? "i32" : "f32"));
            return values;
        }
        return type switch
        {
            "u8" => JsonValue.Create((int)b[0])!, "u16" => JsonValue.Create((int)BinaryPrimitives.ReadUInt16LittleEndian(b))!,
            "i32" or "opt_index" => JsonValue.Create(BinaryPrimitives.ReadInt32LittleEndian(b))!,
            "f32" => Float(b), "hex" => JsonValue.Create(Convert.ToHexStringLower(b))!,
            _ => JsonValue.Create(BinaryPrimitives.ReadUInt32LittleEndian(b))!
        };
    }
    internal static JsonNode Float(ReadOnlySpan<byte> b)
    {
        float f = BinaryPrimitives.ReadSingleLittleEndian(b);
        return float.IsFinite(f) && BitConverter.SingleToInt32Bits(f) != int.MinValue ? JsonValue.Create(f)!
            : JsonValue.Create("bits:" + Convert.ToHexStringLower(b))!;
    }
    internal static float ReadFloat(JsonNode value) => value is JsonValue v && v.TryGetValue<string>(out string? text)
        ? BinaryPrimitives.ReadSingleLittleEndian(Convert.FromHexString(text.StartsWith("bits:", StringComparison.Ordinal) ? text[5..] : throw new InvalidDataException("Invalid float bits.")))
        : value.GetValue<float>();

    private static void Write(Span<byte> b, string type, JsonNode value)
    {
        if (type.Contains(':'))
        {
            string[] p = type.Split(':'); var a = value.AsArray(); int count = int.Parse(p[1], CultureInfo.InvariantCulture);
            if (a.Count != count) throw new InvalidDataException("MW3 fixed-array length changed.");
            for (int i = 0; i < count; i++) Write(b.Slice(i * 4, 4), p[0][..^1], a[i]!);
            return;
        }
        if (type == "str")
        {
            string text = value["text"]!.GetValue<string>(); byte[] suffix = Convert.FromHexString(value["suffix"]!.GetValue<string>());
            if (text.Any(c => c > 255 || c == 0) || text.Length > b.Length) throw new InvalidDataException("MW3 name does not fit its field.");
            Encoding.Latin1.GetBytes(text, b);
            if (suffix.Length == b.Length - text.Length) suffix.CopyTo(b[text.Length..]);
            else if (text.Length == b.Length) throw new InvalidDataException("MW3 name requires a terminator.");
            return;
        }
        if (type is "vec3" or "color" or "bbox" or "range" or "partition2")
        {
            var a = value.AsArray(); if (a.Count * 4 != b.Length) throw new InvalidDataException("MW3 vector length changed.");
            for (int i = 0; i < a.Count; i++) Write(b.Slice(i * 4, 4), type == "partition2" ? "i32" : "f32", a[i]!);
            return;
        }
        switch (type)
        {
            case "u8": b[0] = checked((byte)value.GetValue<int>()); break;
            case "u16": BinaryPrimitives.WriteUInt16LittleEndian(b, checked((ushort)value.GetValue<int>())); break;
            case "i32": case "opt_index": BinaryPrimitives.WriteInt32LittleEndian(b, value.GetValue<int>()); break;
            case "f32": BinaryPrimitives.WriteSingleLittleEndian(b, ReadFloat(value)); break;
            case "hex": var raw = Convert.FromHexString(value.GetValue<string>()); if (raw.Length != b.Length) throw new InvalidDataException("MW3 hex field length changed."); raw.CopyTo(b); break;
            default: BinaryPrimitives.WriteUInt32LittleEndian(b, value.GetValue<uint>()); break;
        }
    }
}
