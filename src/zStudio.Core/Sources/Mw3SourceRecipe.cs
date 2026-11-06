using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

/// <summary>Versioned MW3 build recipes. Every source reference is project-relative and goes through the run's snapshot.</summary>
internal static class Mw3SourceRecipe
{
    internal const string Manifest = "gamegen/mw3.project.json";
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static JsonObject Parse(byte[] bytes)
    {
        CheckJson(bytes);
        return JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 32 })?.AsObject() ?? throw new InvalidDataException("Missing MW3 recipe.");
    }
    internal static byte[] Text(JsonNode node) => JsonSerializer.SerializeToUtf8Bytes(node, JsonOptions);
    internal static JsonArray Fields(string id) => id.StartsWith("event_", StringComparison.Ordinal) ? EventLayout(byte.Parse(id[6..], System.Globalization.CultureInfo.InvariantCulture))
        : id.StartsWith("name_", StringComparison.Ordinal) ? Mw3Record.Layout(("name", 0, int.Parse(id[5..], System.Globalization.CultureInfo.InvariantCulture), "str"))
        : id == "mw3_animation_entry" ? EntryLayout()
        : id.StartsWith("GAMEZ_", StringComparison.Ordinal)
        ? GameZLayouts.For(27).Definition(id)
        : FieldLayouts.Definitions["layouts"]?[id] as JsonArray ?? id switch
        {
            "world_header" => Mw3Record.Layout(("signature",0,4,"u32"),("version",4,4,"u32"),("textures",8,4,"u32"),("textures_offset",12,4,"u32"),
                ("materials_offset",16,4,"u32"),("models_offset",20,4,"u32"),("node_capacity",24,4,"u32"),("free_node",28,4,"i32"),("nodes_offset",32,4,"u32")),
            "pool_header" => Mw3Record.Layout(("capacity",0,4,"u32"),("count",4,4,"u32"),("free",8,4,"i32"),("used",12,4,"i32")),
            "model_pool_header" => Mw3Record.Layout(("capacity",0,4,"u32"),("count",4,4,"u32"),("free",8,4,"i32")),
            "archive_entry" => Mw3Record.Layout(("offset",0,4,"u32"),("length",4,4,"u32"),("name",8,64,"str"),("flags",72,4,"u32"),("comment",76,64,"str"),("time_low",140,4,"u32"),("time_high",144,4,"u32")),
            "sequence" => Mw3Record.Layout(("name",0,32,"str"),("state",32,4,"u32"),("runtime",36,24,"f32a:6"),("payload_bytes",60,4,"u32")),
            "motion_header" => Mw3Record.Layout(("version",0,4,"u32"),("loop_seconds",4,4,"f32"),("frames",8,4,"u32"),("parts",12,4,"u32"),("minimum",16,4,"f32"),("maximum",20,4,"f32")),
            _ => throw new InvalidDataException($"Unknown MW3 source layout {JsonData.ShownText(id)}.")
        };

    private static JsonArray EntryLayout()
    {
        var layout = (JsonArray)FieldLayouts.Definitions["layouts"]!["ANIM_ENTRY_LAYOUT"]!.DeepClone();
        foreach (var field in layout.OfType<JsonArray>())
            if (field[1]!.GetValue<int>() >= 196) field[1] = field[1]!.GetValue<int>() + 4;
        return layout;
    }
    private static JsonArray EventLayout(byte type)
    {
        var layout = Mw3Record.Layout(("event",0,1,"u8"),("start_mode",1,1,"u8"),("size",4,4,"u32"),("threshold",8,4,"f32"));
        foreach (var field in Animation.AnimationCatalog.Find(type, 39)!.Fields)
            layout.Add(new JsonArray(field.Name, field.Offset, field.Kind == Animation.AnimationFieldKind.Vector ? 12 : field.Size,
                field.Kind switch { Animation.AnimationFieldKind.Text => "str", Animation.AnimationFieldKind.Vector => "vec3", Animation.AnimationFieldKind.Float => "f32", Animation.AnimationFieldKind.Short => "u16", _ => field.Size == 1 ? "u8" : "u32" }));
        return layout;
    }

    internal sealed class Tape(ReadOnlyMemory<byte> bytes, string folder, Action<string, byte[]> write, CancellationToken token)
    {
        private readonly JsonArray parts = [], pending = [];
        private readonly JsonArray fixups = [], inputs = [];
        private int part, end;
        internal JsonObject Recipe { get; } = new() { ["format"] = 1, ["kind"] = "stream" };
        internal int Position => end;
        internal void Positions(GameModel model)
        {
            if (model.Vertices.Length == 0) return;
            if (!model.Polygons.Any(p => p.Vertices.Length >= 3)) { Array(end, model.Vertices.Length * 3, "f32", "positions"); return; }
            string source = $"{folder}/model-{model.Index:D5}.gltf";
            var data = Worlds.WorldGltf.ExportMw3Model(model, "positions.bin", token);
            write(source, data.Json); inputs.Add(source);
            var min = model.Vertices.Aggregate(System.Numerics.Vector3.Min); var max = model.Vertices.Aggregate(System.Numerics.Vector3.Max);
            Add(new JsonObject { ["offset"] = end, ["source"] = source, ["count"] = model.Vertices.Length,
                ["envelope"] = new JsonArray(min.X, min.Y, min.Z, max.X, max.Y, max.Z),
                ["triangles"] = new JsonArray(data.Triangles.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) }, model.Vertices.Length * 12);
        }
        internal void Fixed(int offset, int size, string layout, string name)
        {
            if (offset < 0 || size < 0 || offset + (long)size > bytes.Length) throw new InvalidDataException($"MW3 {name}: record {layout} at {offset} with {size} bytes exceeds {bytes.Length}.");
            Gap(offset);
            Add(new JsonObject { ["name"] = name, ["offset"] = offset, ["layout"] = layout, ["size"] = size,
                ["preserved"] = layout.StartsWith("GAMEZ_", StringComparison.Ordinal) || layout is "world_header" or "pool_header" or "model_pool_header" ? SourceProject.Sha256(bytes.Span.Slice(offset, size)) : null,
                ["fields"] = Mw3Record.Decode(bytes.Span.Slice(offset, size), Fields(layout)) }, size);
        }
        internal void Array(int offset, int count, string type, string name)
        {
            if (count == 0) return;
            Gap(offset); int stride = type == "u8" ? 1 : type == "u16" ? 2 : 4;
            BinaryCursor.CheckRange(bytes.Length, offset, (long)count * stride);
            JsonArray values = [];
            for (int i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                var b = bytes.Span[(offset + i * stride)..];
                values.Add(type == "f32" ? Mw3Record.Float(b[..4]) : type == "u8" ? JsonValue.Create((uint)b[0])
                    : type == "u16" ? JsonValue.Create((uint)BinaryPrimitives.ReadUInt16LittleEndian(b)) : JsonValue.Create(BinaryPrimitives.ReadUInt32LittleEndian(b)));
            }
            bool editable = type == "f32" && name is "normals" or "texture coordinates" or "corner colors" or "translations" or "rotations WXYZ";
            Add(new JsonObject { ["name"] = name, ["offset"] = offset, ["type"] = type, ["count"] = count, ["values"] = values,
                ["preserved"] = editable ? null : SourceProject.Sha256(bytes.Span.Slice(offset, checked(count * stride))) }, checked(count * stride));
        }
        internal void Relocate(int offset, int target) => fixups.Add(new JsonArray(offset, target));
        private void Gap(int next)
        {
            if (next < end) throw new InvalidDataException($"Overlapping MW3 source records at {next} after {end}.");
            if (next > end) Array(end, next - end, "u8", "reserved");
        }
        private void Add(JsonObject record, int length)
        {
            token.ThrowIfCancellationRequested(); pending.Add(record); end = checked(end + length);
            if (pending.Count == 512) Flush();
        }
        private void Flush()
        {
            if (pending.Count == 0) return;
            string path = $"{folder}/records-{part++:D5}.json";
            byte[] text = Text(pending);
            if (text.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException("MW3 source part exceeds 16 MiB.");
            write(path, text); parts.Add(path); pending.Clear();
        }
        internal JsonObject Finish()
        {
            Gap(bytes.Length); Flush(); Recipe["parts"] = parts; Recipe["relocations"] = fixups; Recipe["inputs"] = inputs; return Recipe;
        }
    }

    internal static byte[] Build(JsonObject recipe, Func<string, byte[]> read, CancellationToken token)
    {
        RequireFormat(recipe);
        if (recipe["kind"]!.GetValue<string>() != "stream") throw new InvalidDataException("Expected an MW3 record stream.");
        using MemoryStream output = new(); Dictionary<int, int> positions = []; List<(int Old, int New, int Length)> ranges = [];
        foreach (string path in Strings(recipe["parts"]!))
        {
            token.ThrowIfCancellationRequested(); var records = ParseArray(read(path));
            foreach (var item in records.OfType<JsonObject>())
            {
                token.ThrowIfCancellationRequested(); int old = item["offset"]!.GetValue<int>(), start = checked((int)output.Position);
                if (!positions.TryAdd(old, start)) throw new InvalidDataException("Duplicate MW3 source record identity.");
                if (item["source"] is { } source)
                {
                    var vertices = Worlds.WorldGltf.ReadMw3Model(read(source.GetValue<string>()), name => throw new InvalidDataException("MW3 position sources keep their buffer embedded."),
                        item["count"]!.GetValue<int>(), item["triangles"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray(), token);
                    var limits = item["envelope"]!.AsArray().Select(n => n!.GetValue<float>()).ToArray();
                    if (limits.Length != 6 || vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || v.X < limits[0] || v.Y < limits[1] || v.Z < limits[2] || v.X > limits[3] || v.Y > limits[4] || v.Z > limits[5]))
                        throw new InvalidDataException("MW3 mesh edit exceeds its original bounds. Growing or moving a mesh requires a spatial rebuild, which this source pipeline does not yet support.");
                    using BinaryWriter writer = new(output, Encoding.Latin1, true);
                    foreach (var v in vertices) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
                }
                else if (item["layout"] is { } layout)
                {
                    byte[] fixedBytes = Mw3Record.Encode(item["fields"]!.AsObject(), item["size"]!.GetValue<int>(), Fields(layout.GetValue<string>()));
                    if (item["preserved"] is { } expected && expected.GetValue<string>() != SourceProject.Sha256(fixedBytes))
                        throw new InvalidDataException("MW3 structure and cached spatial records are preserved. This edit needs the later MW3 authoring compiler; no files were exported.");
                    output.Write(fixedBytes);
                }
                else
                {
                    string type = item["type"]!.GetValue<string>(); var values = item["values"]!.AsArray();
                    if (values.Count != item["count"]!.GetValue<int>()) throw new InvalidDataException("MW3 typed-array editing preserves the recorded element count; resizing needs the later authoring compiler.");
                    int stride = type == "u8" ? 1 : type == "u16" ? 2 : type is "u32" or "f32" ? 4 : throw new InvalidDataException("Unknown MW3 scalar type.");
                    FormatRegistry.ValidateDocumentSize(output.Length + (long)stride * values.Count);
                    using BinaryWriter writer = new(output, Encoding.Latin1, true);
                    foreach (var value in values)
                        if (type == "f32") writer.Write(Mw3Record.ReadFloat(value!));
                        else if (stride == 1) writer.Write(checked((byte)value!.GetValue<uint>()));
                        else if (stride == 2) writer.Write(checked((ushort)value!.GetValue<uint>()));
                        else writer.Write(value!.GetValue<uint>());
                }
                if (item["layout"] == null && item["preserved"] is { } preserved)
                {
                    if (!output.TryGetBuffer(out var buffer) || SourceProject.Sha256(buffer.AsSpan(start, checked((int)output.Position - start))) != preserved.GetValue<string>())
                        throw new InvalidDataException("MW3 indexed structure and opaque tracks are preserved in this pipeline; that edit needs the later authoring compiler.");
                }
                FormatRegistry.ValidateDocumentSize(output.Length); ranges.Add((old, start, checked((int)output.Position - start)));
            }
        }
        byte[] result = output.ToArray();
        if (ranges.Count > 0) positions.TryAdd(ranges[^1].Old + ranges[^1].Length, result.Length);
        foreach (var fixup in recipe["relocations"]!.AsArray().OfType<JsonArray>())
        {
            int old = fixup[0]!.GetValue<int>(), target = fixup[1]!.GetValue<int>();
            int lo = 0, hi = ranges.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (ranges[mid].Old <= old) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            var range = found < 0 ? default : ranges[found];
            if (range.Length == 0 || old + 4L > range.Old + (long)range.Length || !positions.TryGetValue(target, out int to)) throw new InvalidDataException("MW3 relocation has no source record.");
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(range.New + old - range.Old), to);
        }
        return result;
    }
    private static JsonArray ParseArray(byte[] bytes)
    {
        CheckJson(bytes);
        return JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 32 })!.AsArray();
    }
    private static void CheckJson(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException("MW3 source JSON exceeds 16 MiB; split its record parts.");
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions { MaxDepth = 32 }); int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > 1_000_000) throw new InvalidDataException("MW3 source JSON exceeds one million tokens; split its record parts.");
            if (reader.HasValueSequence ? reader.ValueSequence.Length > 65_536 : reader.ValueSpan.Length > 65_536)
                throw new InvalidDataException("MW3 source JSON scalar exceeds 64 KiB.");
        }
    }
    internal static void RequireFormat(JsonObject recipe)
    { if (recipe["format"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported MW3 source format; reconstruct the project."); }
    internal static IEnumerable<string> Strings(JsonNode node) => node.AsArray().Select(n => n!.GetValue<string>());
}
