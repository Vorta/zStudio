using System.Numerics;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

internal sealed class GameZReader : IZbdFormatReader
{
    public FormatFamily Family => FormatFamily.GameZ;
    public void Read(ZbdDocument doc, CancellationToken token)
    {
        BinaryCursor h = new(doc.Bytes); h.Skip(8);
        uint textureCount = h.U32(), textureOffset = h.U32(), materialOffset = h.U32(), modelOffset = h.U32(), nodeCapacity = h.U32(), lastFree = h.U32(), nodeOffset = h.U32();
        if (textureOffset < 36 || materialOffset < textureOffset || modelOffset < materialOffset || nodeOffset < modelOffset) throw new InvalidDataException("GameZ section offsets are not ordered.");
        GameScene scene = new(); doc.Scene = scene;
        doc.Metadata["header_raw"] = Convert.ToHexStringLower(doc.Bytes.Span[..36]);
        BinaryCursor t = new(doc.Slice(textureOffset, materialOffset - textureOffset), textureOffset); t.Count(textureCount, 36);
        for (int i = 0; i < textureCount; i++)
        {
            var meta = FieldLayouts.Read(t, 36, "GAMEZ_TEXDIR_LAYOUT"); scene.Textures.Add(meta);
            doc.Add(AssetKind.TextureReference, i, meta.Text("name"), textureOffset + i * 36L, 36, meta);
        }
        ReadMaterials(doc, scene, new(doc.Slice(materialOffset, modelOffset - materialOffset), materialOffset), token);
        ReadModels(doc, scene, new(doc.Slice(modelOffset, nodeOffset - modelOffset), modelOffset), token);
        ReadNodes(doc, scene, new(doc.Slice(nodeOffset, doc.Bytes.Length - nodeOffset), nodeOffset), nodeCapacity, token);
        foreach (GameModel model in scene.Models)
        {
            var node = scene.Nodes.FirstOrDefault(n => n.ModelIndex == model.Index);
            if (node != null) doc.Assets.First(a => a.Kind == AssetKind.Model && a.Index == model.Index).Name = $"{node.Name} [model {model.Index}]";
        }
        if (scene.Nodes.Any(n => n.Class == "world"))
            doc.Add(AssetKind.World, 0, "Whole world", 0, doc.Bytes.Length, new JsonObject { ["nodes"] = scene.Nodes.Count, ["models"] = scene.Models.Count, ["preview"] = "Static scene; no gameplay or effect simulation" }, scene);
    }
    private static void ReadMaterials(ZbdDocument doc, GameScene scene, BinaryCursor c, CancellationToken token)
    {
        uint capacity = c.U32(), count = c.U32(); c.Skip(8); if (count > capacity) throw new InvalidDataException("Material count exceeds capacity."); c.Count(capacity, 44);
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested(); long offset = c.AbsolutePosition;
            var material = FieldLayouts.Read(c, 40, "GAMEZ_MATERIAL_LAYOUT"); material["next_index"] = (long)c.I16(); material["prev_index"] = (long)c.I16();
            scene.Materials.Add(material); doc.Add(AssetKind.Material, i, $"Material {i}", offset, 44, material).Summary = $"Texture {material.Int("texture_index", -1)}";
        }
        c.Skip(checked((int)(capacity - count) * 44));
        foreach (var material in scene.Materials)
            if ((material.UInt("flags") & 4) != 0 || material.Text("cycle_ptr") != "0x00000000")
            {
                var cycle = FieldLayouts.Read(c, 28, "GAMEZ_MATERIAL_CYCLE_LAYOUT"); cycle["texture_indices"] = JsonData.Integers(c.Indices(cycle.Int("tex_map_count"))); material["cycle"] = cycle;
            }
    }
    private static void ReadModels(ZbdDocument doc, GameScene scene, BinaryCursor c, CancellationToken token)
    {
        uint capacity = c.U32(), count = c.U32(); c.Skip(4); if (count > capacity) throw new InvalidDataException("Model count exceeds capacity."); c.Count(capacity, 88);
        List<JsonObject> infos = [];
        for (int i = 0; i < count; i++) { var info = FieldLayouts.Read(c, 84, "GAMEZ_MODEL_INFO_LAYOUT"); info["data_offset"] = (long)c.U32(); infos.Add(info); }
        c.Skip(checked((int)(capacity - count) * 88));
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested(); long start = c.AbsolutePosition; var info = infos[index];
            Vector3[] vertices = c.Vectors(info.Int("vertex_count")), normals = c.Vectors(info.Int("normal_count")), morphs = c.Vectors(info.Int("morph_count"));
            int lightCount = c.Count(info.UInt("light_count"), 76); JsonArray lights = [];
            for (int i = 0; i < lightCount; i++) lights.Add(FieldLayouts.Read(c, 76, "GAMEZ_POINT_LIGHT_LAYOUT"));
            foreach (var light in lights) light!["vertices"] = JsonData.Vectors(c.Vectors(light.Int("vertex_count")));
            info["lights"] = lights;
            int polygonCount = c.Count(info.UInt("polygon_count"), 28); List<JsonObject> polygonInfos = [];
            for (int i = 0; i < polygonCount; i++) polygonInfos.Add(FieldLayouts.Read(c, 28, "GAMEZ_POLYGON_INFO_LAYOUT"));
            List<Polygon> polygons = [];
            foreach (var polygon in polygonInfos)
            {
                uint flags = polygon.UInt("flags"); int n = (int)(flags & 255); int[] vi = c.Indices(n), ni = (flags & 512) != 0 ? c.Indices(n) : [];
                Vector2[] uvs = polygon.Text("uvs_ptr") != "0x00000000" ? c.Uvs(n) : [];
                polygons.Add(new(polygon.Int("material_index", -1), flags, vi, ni, uvs, polygon));
                if (vi.Any(v => v < 0 || v >= vertices.Length)) doc.Diagnostics.Add(new("Warning", $"Model {index} contains an out-of-range vertex reference.", index, start));
            }
            GameModel model = new(index, vertices, normals, morphs, polygons.ToArray(), info); scene.Models.Add(model);
            var a = doc.Add(AssetKind.Model, index, $"Model {index}", start, c.AbsolutePosition - start, info, model); a.Summary = $"{vertices.Length:N0} vertices · {polygons.Count:N0} polygons";
        }
    }
    private static void ReadNodes(ZbdDocument doc, GameScene scene, BinaryCursor c, uint capacity, CancellationToken token)
    {
        int count = c.Count(capacity, 196); List<(JsonObject Info, uint Offset, long Header)> entries = []; bool free = false;
        for (int i = 0; i < count; i++)
        {
            long header = c.AbsolutePosition; var raw = c.Take(192); uint offset = c.U32();
            if (!raw.Span[..36].ContainsAnyExcept((byte)0)) free = true;
            if (!free) entries.Add((FieldLayouts.Decode(raw, FieldLayouts.Definitions["layouts"]!["GAMEZ_NODE_BASE_LAYOUT"]!.AsArray()), offset, header));
        }
        string[] names = ["none", "camera", "world", "window", "display", "object3d", "lod", "unknown_7", "unknown_8", "light"];
        for (int i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var (info, offset, header) = entries[i]; int classId = info.Int("node_class"); string kind = classId >= 0 && classId < names.Length ? names[classId] : $"unknown_{classId}";
            info["node_class"] = kind; info["data_offset_word"] = $"0x{offset:X8}";
            JsonObject data = ReadNodeData(c, kind);
            int[] parents = kind == "none" ? offset == uint.MaxValue ? [] : [unchecked((int)offset)] : c.Indices(info.Int("parent_count"));
            int[] children = c.Indices(info.Int("child_count"));
            info["parent_indices"] = JsonData.Integers(parents); info["child_indices"] = JsonData.Integers(children); info["data"] = data;
            GameNode node = new(i, info.Text("name"), kind, info["model_index"] == null ? null : info.Int("model_index"), parents, children, info, data);
            scene.Nodes.Add(node); doc.Add(AssetKind.Node, i, node.Name, header, 196, info, node).Summary = $"{kind} · {children.Length} children";
        }
        if (c.Remaining > 0) doc.Diagnostics.Add(new("Warning", $"{c.Remaining} trailing node bytes preserved.", Offset: c.AbsolutePosition));
    }
    private static JsonObject ReadNodeData(BinaryCursor c, string kind)
    {
        (int size, string? layout) = kind switch
        {
            "none" => (0, null),
            "camera" => (488, "GAMEZ_CAMERA_LAYOUT"),
            "display" => (28, "GAMEZ_DISPLAY_LAYOUT"),
            "object3d" => (144, "GAMEZ_OBJECT3D_LAYOUT"),
            "lod" => (80, "GAMEZ_LOD_LAYOUT"),
            "light" => (228, "GAMEZ_LIGHT_LAYOUT"),
            "world" => (172, "GAMEZ_WORLD_LAYOUT"),
            "window" => (248, null),
            _ => throw new InvalidDataException($"Unknown node class {kind}; remaining bytes preserved.")
        };
        if (kind == "window")
        {
            BinaryCursor w = new(c.Take(size)); JsonObject value = new() { ["origin_x"] = (long)w.I32(), ["origin_y"] = (long)w.I32(), ["resolution_x"] = (long)w.I32(), ["resolution_y"] = (long)w.I32() };
            JsonArray polys = []; for (int i = 0; i < 4; i++) polys.Add(FieldLayouts.Read(w, 52, "GAMEZ_WINDOW_CLEAR_POLYGON_LAYOUT")); value["clear_polygons"] = polys;
            value["clear_polygon_count"] = (long)w.I32(); value["buffer_index"] = (long)w.I32(); value["buffer_surface_ptr"] = $"0x{w.U32():X8}"; value["buffer_width"] = (long)w.U32(); value["buffer_height"] = (long)w.U32(); value["buffer_bit_depth"] = (long)w.U32(); return value;
        }
        JsonObject result = layout == null ? [] : FieldLayouts.Read(c, size, layout);
        if (kind == "light") result["attached_indices"] = JsonData.Integers(c.Indices(result.Int("attached_count")));
        if (kind == "world")
        {
            result["light_indices"] = JsonData.Integers(c.Indices(result.Int("light_count"))); result["sound_indices"] = JsonData.Integers(c.Indices(result.Int("sound_count")));
            int x = Math.Max(0, result.Int("virt_partition_x_count")), z = Math.Max(0, result.Int("virt_partition_z_count")); c.Count(checked((uint)((long)x * z)), 64);
            JsonArray rows = []; for (int row = 0; row < z; row++)
            {
                JsonArray cells = []; for (int col = 0; col < x; col++) { var cell = FieldLayouts.Read(c, 64, "GAMEZ_WORLD_PARTITION_LAYOUT"); cell["node_indices"] = JsonData.Integers(c.Indices(cell.Int("node_count"))); cells.Add(cell); }
                rows.Add(cells);
            }
            result["partitions"] = rows;
        }
        return result;
    }
}
