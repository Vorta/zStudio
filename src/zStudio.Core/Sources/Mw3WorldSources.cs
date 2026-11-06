using System.Buffers.Binary;
using System.Text.Json.Nodes;
using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

internal static class Mw3WorldSources
{
    internal static JsonObject Reconstruct(AssetDocument doc, string folder, Action<string, byte[]> write, CancellationToken token)
    {
        if (doc.Probe is not { Family: FormatFamily.GameZ, Version: 27 } || doc.GameZLayout is not { } layout || doc.Scene is not { } scene)
            throw new InvalidDataException("An MW3 version-27 world is required.");
        Mw3SourceRecipe.Tape tape = new(doc.Bytes, folder, write, token);
        int I(int offset) => BinaryPrimitives.ReadInt32LittleEndian(doc.Bytes.Span[offset..]);
        tape.Fixed(0, 36, "world_header", "world");
        tape.Relocate(12, layout.TextureOffset); tape.Relocate(16, layout.MaterialOffset); tape.Relocate(20, layout.ModelOffset); tape.Relocate(32, layout.NodeOffset);
        for (int i = 0; i < scene.Textures.Count; i++) tape.Fixed(layout.TextureOffset + i * 40, 40, "GAMEZ_TEXDIR_LAYOUT", $"texture {i}");
        tape.Fixed(layout.MaterialOffset, 16, "pool_header", "material pool");
        for (int i = 0; i < layout.MaterialCapacity; i++) tape.Fixed(layout.MaterialOffset + 16 + i * 44, 44, "GAMEZ_MATERIAL_LAYOUT", $"material slot {i}");
        // MW3 stores cycles in the material used-list's order, not material-index order.
        int cycle = tape.Position;
        foreach (var material in scene.Materials.AsEnumerable().Reverse())
            if ((material.UInt("flags") & 4) != 0)
            {
                tape.Fixed(cycle, 28, "GAMEZ_MATERIAL_CYCLE_LAYOUT", "material cycle");
                int count = I(cycle + 16);
                tape.Array(cycle + 28, count, "u32", "cycle textures"); cycle = tape.Position;
            }
        tape.Fixed(layout.ModelOffset, 12, "model_pool_header", "model pool");
        for (int i = 0; i < layout.ModelCapacity; i++)
        {
            int offset = layout.ModelOffset + 12 + i * 96;
            tape.Fixed(offset, 96, "GAMEZ_MODEL_INFO_LAYOUT", $"model slot {i}");
            if (i < scene.Models.Count) tape.Relocate(offset + 92, I(offset + 92));
        }
        var assets = doc.Assets.Where(a => a.Kind == AssetKind.Model).ToDictionary(a => a.Index);
        foreach (var model in scene.Models) Model(tape, model, checked((int)assets[model.Index].Offset), token);
        for (int i = 0; i < layout.NodeCapacity; i++) tape.Fixed(layout.NodeOffset + i * 212, 212, "GAMEZ_NODE_BASE_LAYOUT", $"node slot {i}");
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            var node = scene.Nodes[i]; int offset = checked((int)layout.NodeDataOffsets[i]);
            if (node.Class != "none") tape.Relocate(layout.NodeOffset + i * 212 + 208, offset);
            int size = node.Class switch { "none" => 0, "camera" => 488, "world" => 188, "window" => 248, "display" => 28, "object3d" => 144, "lod" => 80, "light" => 208, _ => throw new InvalidDataException("Unknown MW3 node class.") };
            if (size > 0)
            {
                if (node.Class == "window") tape.Array(offset, size / 4, "u32", $"window {i}");
                else tape.Fixed(offset, size, "GAMEZ_" + node.Class.ToUpperInvariant() + "_LAYOUT", $"{node.Class} {i}");
            }
            if (node.Class == "light") tape.Array(tape.Position, node.Data.Int("attached_count"), "u32", "attached nodes");
            if (node.Class == "world")
            {
                tape.Array(tape.Position, node.Data.Int("light_count"), "u32", "lights");
                tape.Array(tape.Position, node.Data.Int("sound_count"), "u32", "sounds");
                foreach (var row in node.Data["partitions"]!.AsArray().OfType<JsonArray>())
                    foreach (var cell in row.OfType<JsonObject>())
                    {
                        tape.Fixed(tape.Position, 72, "GAMEZ_WORLD_PARTITION_LAYOUT", "partition");
                        tape.Array(tape.Position, cell.Int("node_count"), "u32", "partition nodes");
                    }
            }
            if (node.Class != "none") tape.Array(tape.Position, node.Parents.Length, "u32", "parents");
            tape.Array(tape.Position, node.Children.Length, "u32", "children");
        }
        return tape.Finish();
    }

    internal static void Model(Mw3SourceRecipe.Tape tape, GameModel model, int start, CancellationToken token)
    {
        if (tape.Position != start) throw new InvalidDataException("MW3 model geometry is not contiguous with its pool.");
        tape.Positions(model);
        tape.Array(tape.Position, model.Normals.Length * 3, "f32", "normals");
        tape.Array(tape.Position, model.Morphs.Length * 3, "f32", "morph targets");
        var lights = model.Metadata["lights"]!.AsArray().OfType<JsonObject>().ToArray();
        foreach (var light in lights) tape.Fixed(tape.Position, 76, "GAMEZ_POINT_LIGHT_LAYOUT", "point light");
        foreach (var light in lights) tape.Array(tape.Position, light.Int("vertex_count") * 3, "f32", "light vertices");
        foreach (var polygon in model.Polygons) { token.ThrowIfCancellationRequested(); tape.Fixed(tape.Position, 36, "GAMEZ_POLYGON_INFO_LAYOUT", "polygon"); }
        foreach (var polygon in model.Polygons)
        {
            tape.Array(tape.Position, polygon.Vertices.Length, "u32", "vertex indices");
            tape.Array(tape.Position, polygon.Normals.Length, "u32", "normal indices");
            tape.Array(tape.Position, polygon.Uvs.Length * 2, "f32", "texture coordinates");
            tape.Array(tape.Position, polygon.Colors.Length * 3, "f32", "corner colors");
        }
    }
}
