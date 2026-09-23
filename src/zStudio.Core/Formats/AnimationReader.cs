using System.Globalization;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Core.Formats;

internal sealed class AnimationReader : IZbdFormatReader
{
    public FormatFamily Family => FormatFamily.Animation;
    public void Read(ZbdDocument doc, CancellationToken token)
    {
        doc.Animations = AnimationPackage.Read(doc.Bytes, token);
        doc.Diagnostics.AddRange(doc.Animations.Diagnostics);
        BinaryCursor c = new(doc.Bytes); c.Skip(8); int stamps = c.Count(c.U32(), 84); JsonArray stampList = [];
        for (int i = 0; i < stamps; i++) stampList.Add(FieldLayouts.Read(c, 84, "ANIM_STAMP_LAYOUT"));
        doc.Metadata["stamps"] = stampList;
        JsonObject globals = FieldLayouts.Read(c, 60, "ANIM_GLOBALS_LAYOUT"); doc.Metadata["globals"] = globals;
        int count = (int)(globals.UInt("counts_packed") >> 16); c.Count((uint)count, 308);
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested(); int start = c.Position;
            JsonObject entry = FieldLayouts.Read(c, 308, "ANIM_ENTRY_LAYOUT");
            uint a = entry.UInt("countsPacked_104"), b = entry.UInt("countsPacked_108"), d = entry.UInt("countsPacked_10c");
            string[] lanes = ["surface_count", "tracked_node_count", "node_ref_count", "light_ref_count", "sound_ref_count", "sample_ref_count", "effect_template_ref_count", "activation_prereq_count", "activation_prereq_min_count", "runtime_ref_count", "_count_10e", "_count_10f"];
            uint[] words = [a, b, d]; for (int i = 0; i < lanes.Length; i++) entry[lanes[i]] = (long)((words[i / 4] >> (i % 4 * 8)) & 255);
            foreach (JsonNode? array in FieldLayouts.Definitions["subarrays"]!.AsArray())
            {
                string name = array!.Text("name"), layout = array.Text("layout"); int size = array!["size"]!.GetValue<int>(), n = entry.Int(array.Text("count")); c.Count((uint)n, size);
                JsonArray records = []; for (int i = 0; i < n; i++) records.Add(FieldLayouts.Read(c, size, layout)); entry[name] = records;
            }
            entry["surface_primary"] = Surface(c, doc, index, token);
            JsonArray surfaces = []; for (int i = 0; i < entry.Int("surface_count"); i++) surfaces.Add(Surface(c, doc, index, token)); entry["surface_runtimes"] = surfaces;
            var asset = doc.Add(AssetKind.Animation, index, entry.Text("name"), start, c.Position - start, entry);
            asset.Content = doc.Animations.Entries[index];
            if (string.IsNullOrWhiteSpace(asset.Name)) asset.Name = entry["surface_primary"].Text("sequence_name");
            if (string.IsNullOrWhiteSpace(asset.Name)) asset.Name = $"Animation {index}";
            asset.Summary = $"{entry.Int("surface_count") + 1} sequences · {entry.Int("node_ref_count")} node references";
        }
        if (c.Remaining != 0) doc.Diagnostics.Add(new("Warning", $"{c.Remaining} trailing animation bytes preserved.", Offset: c.Position));
    }
    private static JsonObject Surface(BinaryCursor c, ZbdDocument doc, int index, CancellationToken token)
    {
        JsonObject surface = FieldLayouts.Read(c, 64, "ANIM_SURFACE_LAYOUT"); int size = surface.Int("payload_size");
        long start = c.AbsolutePosition; BinaryCursor events = new(c.Take(size), start); JsonArray result = [];
        while (events.Remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                int pos = events.Position; uint packed = events.U32(), length = events.U32();
                if (length < 12 || length > int.MaxValue) throw new InvalidDataException("Invalid event record size.");
                events.Seek(pos); var bytes = events.Take((int)length); BinaryCursor e = new(bytes); e.Skip(8); float threshold = e.F32();
                int type = (int)(packed & 255), mode = (int)((packed >> 8) & 255);
                var spec = FieldLayouts.Definitions["events"]![type.ToString(CultureInfo.InvariantCulture)];
                JsonObject record = spec?["layout"] is JsonArray layout ? FieldLayouts.Decode(bytes, layout) : [];
                record["type_id"] = (long)type; record["type"] = AnimationCatalog.Find(type)?.Name ?? $"unknown_{type:X2}";
                record["start_mode"] = AnimationCatalog.ModeName(mode);
                record["start_threshold"] = JsonData.Number(threshold); record["record_size"] = (long)length; record["offset"] = $"0x{start + pos:X}";
                // Raw event bytes remain inspectable even where only part of a handler is understood.
                record["raw_hex"] = Convert.ToHexStringLower(bytes.Span);
                if (spec?.Text("custom") == "keyframes")
                {
                    record["node_ref_index"] = (long)e.I32(); record["u32_10"] = (long)e.U32(); record["segment_time_cursor"] = JsonData.Number(e.F32()); record["segment_data_offset"] = (long)e.I32(); record["segment_index"] = (long)e.I32();
                    JsonArray segments = [];
                    while (e.Remaining > 0)
                    {
                        uint flags = e.U32(); JsonObject seg = new() { ["flags"] = (long)flags, ["start_time"] = JsonData.Number(e.F32()), ["end_time"] = JsonData.Number(e.F32()) };
                        string[] channels = ["position_channel", "rotation_channel", "scale_channel"];
                        for (int channel = 0; channel < 3; channel++)
                            if ((flags & (1 << channel)) != 0) { JsonArray values = []; for (int j = 0; j < 7; j++) values.Add(JsonData.Number(e.F32())); seg[channels[channel]] = values; }
                        segments.Add(seg);
                    }
                    record["segments"] = segments;
                }
                result.Add(record);
            }
            catch (InvalidDataException ex) { doc.Diagnostics.Add(new("Warning", $"Sequence {surface.Text("sequence_name")}: {ex.Message}", index, events.AbsolutePosition)); break; }
        }
        surface["events"] = result; return surface;
    }
}
