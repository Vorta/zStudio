using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

/// <summary>A model hierarchy owned by one archive directory record.</summary>
public sealed record MechAssembly(int MemberIndex, int RootNode, int NodeCount, int FirstModel, int ModelCount);

internal static class MechLibraryReader
{
    internal static void Read(ZbdDocument doc, CancellationToken token)
    {
        // Metadata records identify the library dialect, independently of its filename.
        var versions = doc.Assets.Where(a => a.Name == "version" && a.Length == 4).ToArray();
        var formats = doc.Assets.Where(a => a.Name == "format" && a.Length == 4).ToArray();
        var materials = doc.Assets.Where(a => a.Name == "materials").ToArray();
        if (versions.Length != 1 || formats.Length != 1 || materials.Length != 1 ||
            new BinaryCursor(doc.Slice(versions[0].Offset, 4)).U32() != 27 ||
            new BinaryCursor(doc.Slice(formats[0].Offset, 4)).U32() != 1) return;
        GameScene scene = new(); var layout = GameZLayouts.For(27);
        var materialCursor = new BinaryCursor(doc.Slice(materials[0].Offset, materials[0].Length), materials[0].Offset);
        int count = materialCursor.Count(materialCursor.U32(), 40);
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var material = FieldLayouts.Read(materialCursor, 40, "GAMEZ_MATERIAL_LAYOUT");
            if ((material.UInt("flags") & 1) != 0)
            {
                string texture = SizedString(materialCursor);
                material["stored_texture_pointer"] = material["texture_index"]?.DeepClone();
                material["texture_index"] = scene.Textures.Count;
                scene.Textures.Add(new JsonObject { ["name"] = texture });
            }
            else material["texture_index"] = -1;
            scene.Materials.Add(material);
        }
        if (materialCursor.Remaining != 0) throw new InvalidDataException("Mech material table has trailing bytes.");
        foreach (var asset in doc.Assets.ToArray())
        {
            if (asset == versions[0] || asset == formats[0] || asset == materials[0]) continue;
            token.ThrowIfCancellationRequested();
            var c = new BinaryCursor(doc.Slice(asset.Offset, asset.Length), asset.Offset);
            int root = scene.Nodes.Count, firstModel = scene.Models.Count;
            ReadNode(-1, 0);
            if (c.Remaining != 0) throw new InvalidDataException($"Mech member {asset.Index} has trailing bytes.");
            var assembly = new MechAssembly(asset.Index, root, scene.Nodes.Count - root, firstModel, scene.Models.Count - firstModel);
            var replacement = new AssetRecord { Id = new(doc.Path, AssetKind.Model, asset.Index), Name = asset.Name,
                Offset = asset.Offset, Length = asset.Length, Metadata = asset.Metadata, Content = assembly,
                Summary = $"Mech assembly · {assembly.NodeCount} nodes · {assembly.ModelCount} models" };
            replacement.Metadata["root_node"] = root;
            replacement.Metadata["member_index"] = asset.Index;
            doc.Assets[doc.Assets.IndexOf(asset)] = replacement;

            int ReadNode(int parent, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 256 || scene.Nodes.Count >= 200_000) throw new InvalidDataException("Mech hierarchy limit exceeded.");
                long start = c.AbsolutePosition;
                var info = layout.Read(c, layout.NodeSize, "GAMEZ_NODE_BASE_LAYOUT");
                if (info.Int("node_class") != 5 || info.Int("parent_count") != (parent < 0 ? 0 : 1))
                    throw new InvalidDataException($"Invalid mech hierarchy at {start}.");
                int children = c.Count(info.UInt("child_count"), layout.NodeSize + 144);
                var data = GameZReader.ReadNodeData(c, "object3d", layout);
                int? modelIndex = null;
                uint storedModel = unchecked((uint)info.Int("model_index"));
                if (storedModel != 0)
                {
                    modelIndex = scene.Models.Count;
                    long header = c.AbsolutePosition;
                    var modelInfo = layout.Read(c, layout.ModelSize, "GAMEZ_MODEL_INFO_LAYOUT");
                    modelInfo["source_header_offset"] = header; modelInfo["member_index"] = asset.Index;
                    long modelStart = c.AbsolutePosition;
                    var model = GameZReader.ReadModelData(c, modelInfo, modelIndex.Value, layout, doc.Diagnostics, token);
                    modelInfo["source_data_offset"] = modelStart; modelInfo["source_data_length"] = c.AbsolutePosition - modelStart;
                    scene.Models.Add(model);
                }
                int index = scene.Nodes.Count;
                info["stored_model_pointer"] = $"0x{storedModel:X8}"; info["model_index"] = modelIndex;
                info["node_class"] = "object3d"; info["member_index"] = asset.Index;
                info["local_node_index"] = index - root; info["source_header_offset"] = start;
                int[] parents = parent < 0 ? [] : [parent];
                GameNode node = new(index, info.Text("name"), "object3d", modelIndex, parents, new int[children], info, data);
                scene.Nodes.Add(node);
                for (int i = 0; i < children; i++) node.Children[i] = ReadNode(index, depth + 1);
                info["parent_indices"] = JsonData.Integers(parents); info["child_indices"] = JsonData.Integers(node.Children); info["data"] = data;
                return index;
            }
        }
        doc.Scene = scene;
        doc.Game = GameVariant.MechWarrior3;
        doc.Metadata["game"] = "MechWarrior 3";
        doc.Metadata["content"] = "Mech library";
    }
    internal static string SizedString(BinaryCursor c)
    {
        int length = c.Count(c.U32(), 1);
        if (length > 4096) throw new InvalidDataException("Stored name exceeds 4096 bytes.");
        return System.Text.Encoding.Latin1.GetString(c.Take(length).Span);
    }
}
