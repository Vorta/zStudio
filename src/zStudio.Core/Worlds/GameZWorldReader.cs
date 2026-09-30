using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// Builds a <see cref="GameZWorld"/> from a document the shared GameZ reader has opened: its decoded scene supplies
/// structure and references, and the reader's recorded offsets locate each record's stored fields.
/// </summary>
public static class GameZWorldReader
{
    public static GameZWorld FromDocument(ZbdDocument doc, CancellationToken token = default)
    {
        if (doc.Probe is not { Family: FormatFamily.GameZ, Version: 15 } || doc.Scene is not { } scene || doc.GameZLayout is not { } layout)
            throw new InvalidDataException("A RECOIL (version 15) GameZ world is required.");
        if (doc.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException(error.Message);
        var bytes = doc.Bytes.Span;
        GameZWorld world = new() { MaterialCapacity = layout.MaterialCapacity, ModelCapacity = layout.ModelCapacity, NodeCapacity = layout.NodeCapacity };
        world.FreeHead = BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]);

        for (int i = 0; i < scene.Textures.Count; i++)
        {
            var entry = bytes.Slice(layout.TextureOffset + i * 36, 36);
            world.Textures.Add(new(scene.Textures[i].Text("name")) { NameField = entry.Slice(8, 20).ToArray(), State = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]) });
        }
        for (int i = 0; i < scene.Textures.Count; i++)
        {
            int next = BinaryPrimitives.ReadInt32LittleEndian(bytes[(layout.TextureOffset + i * 36 + 32)..]);
            if (next >= 0 && next < world.Textures.Count) world.Textures[i].NextVariant = world.Textures[next];
        }
        for (int i = 0; i < scene.Materials.Count; i++)
        {
            var m = bytes.Slice(layout.MaterialOffset + 16 + i * 44, 44);
            int texture = BinaryPrimitives.ReadInt32LittleEndian(m[16..]); ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(m);
            world.Materials.Add(new()
            {
                Flags = flags, PackedColor = BinaryPrimitives.ReadUInt16LittleEndian(m[2..]), Color = Vec(m[4..]),
                Texture = (flags & 0x100) != 0 && texture >= 0 && texture < world.Textures.Count ? world.Textures[texture] : null,
                Field14 = F(m[20..]), Field18 = F(m[24..]), Field1C = F(m[28..]), Soil = BinaryPrimitives.ReadUInt32LittleEndian(m[32..]),
            });
        }
        var modelAssets = doc.Assets.Where(a => a.Kind == AssetKind.Model).ToDictionary(a => a.Index);
        foreach (var model in scene.Models)
        {
            token.ThrowIfCancellationRequested();
            var info = bytes.Slice(layout.ModelOffset + 12 + model.Index * 88, 88);
            WorldModel m = new()
            {
                Mode = U(info), Flags = U(info[4..]), MorphFactor = F(info[32..]), ScrollU = F(info[36..]), ScrollV = F(info[40..]), ScrollFrame = U(info[44..]),
                BoundsCentre = Vec(info[68..]), BoundsRadius = F(info[80..]),
            };
            m.Vertices.AddRange(model.Vertices); m.Normals.AddRange(model.Normals); m.Morphs.AddRange(model.Morphs);
            long data = modelAssets[model.Index].Offset + 12L * (model.Vertices.Length + model.Normals.Length + model.Morphs.Length);
            int points = (int)U(info[28..]); long pointVertices = data + 76L * points;
            for (int p = 0; p < points; p++)
            {
                byte[] record = bytes.Slice((int)(data + 76L * p), 76).ToArray(); int count = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(12));
                Vector3[] vertices = new Vector3[count];
                for (int v = 0; v < count; v++) vertices[v] = Vec(bytes[(int)(pointVertices + 12L * v)..]);
                pointVertices += 12L * count; m.Points.Add(new() { Record = record, Vertices = vertices });
            }
            long polygons = pointVertices;
            for (int p = 0; p < model.Polygons.Length; p++)
            {
                var source = model.Polygons[p]; var record = bytes.Slice((int)(polygons + 28L * p), 28);
                int material = source.MaterialIndex;
                m.Polygons.Add(new()
                {
                    Flags = source.Flags & ~0xFFu, Priority = BinaryPrimitives.ReadInt32LittleEndian(record[4..]), Zone = U(record[24..]),
                    Material = material >= 0 && material < world.Materials.Count ? world.Materials[material] : null,
                    Vertices = source.Vertices, Normals = source.Normals, Uvs = source.Uvs,
                });
            }
            world.Models.Add(m);
        }
        // Slots up to the first never-used one: live nodes and, in m6, slots freed during the build.
        var nodeAssets = doc.Assets.Where(a => a.Kind == AssetKind.Node).ToDictionary(a => a.Index);
        var live = new WorldNode?[scene.Nodes.Count];
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var source = scene.Nodes[i]; var slot = bytes.Slice((int)nodeAssets[i].Offset, 196);
            if (source.Class == "none") { world.FreedSlots[i] = slot.ToArray(); continue; }
            var kind = source.Class switch
            {
                "camera" => WorldNodeClass.Camera, "world" => WorldNodeClass.World, "window" => WorldNodeClass.Window, "display" => WorldNodeClass.Display,
                "object3d" => WorldNodeClass.Object3D, "lod" => WorldNodeClass.Lod, "light" => WorldNodeClass.Light,
                _ => throw new InvalidDataException($"Node {i} has unsupported class {source.Class}.")
            };
            WorldNode node = new("", kind)
            {
                NameField = slot[..36].ToArray(), Flags = U(slot[36..]), AuxFlags = U(slot[40..]), BoundsFlags = U(slot[44..]), Zone = U(slot[48..]),
                Priority = U(slot[68..]), GridColumn = BinaryPrimitives.ReadInt32LittleEndian(slot[76..]), GridRow = BinaryPrimitives.ReadInt32LittleEndian(slot[80..]),
                SphereCache = slot.Slice(100, 16).ToArray(), CachedBounds = Box(slot[116..]), PrimaryBounds = Box(slot[140..]), SecondaryBounds = Box(slot[164..]),
                Model = source.ModelIndex is int model && model >= 0 && model < world.Models.Count ? world.Models[model] : null,
            };
            node.Payload = bytes.Slice((int)layout.NodeDataOffsets[i], node.Payload.Length).ToArray();
            live[i] = node; world.Nodes.Add(node);
        }
        WorldNode Node(int index) => index >= 0 && index < live.Length && live[index] is { } n ? n : throw new InvalidDataException($"A reference names node slot {index}, which is not live.");
        WorldNode? Optional(int index) => index < 0 ? null : Node(index);
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            if (live[i] is not { } node) continue;
            var source = scene.Nodes[i];
            foreach (int p in source.Parents) node.Parents.Add(Node(p));
            foreach (int c in source.Children) node.Children.Add(Node(c));
            switch (node.Class)
            {
                case WorldNodeClass.Camera:
                    node.CameraWorld = Optional(node.PayloadInt(0)); node.CameraWindow = Optional(node.PayloadInt(4));
                    node.CameraHorizon = Optional(node.PayloadInt(8)); node.CameraHorizonXZ = Optional(node.PayloadInt(12));
                    break;
                case WorldNodeClass.Light:
                    foreach (var index in source.Data["attached_indices"] as JsonArray ?? []) node.AttachedWorlds.Add(Node((int)JsonData.Integer(index)));
                    break;
                case WorldNodeClass.World:
                    {
                        foreach (var index in source.Data["light_indices"] as JsonArray ?? []) node.WorldLights.Add(Node((int)JsonData.Integer(index)));
                        foreach (var index in source.Data["sound_indices"] as JsonArray ?? []) node.WorldSounds.Add(Node((int)JsonData.Integer(index)));
                        long area = layout.NodeDataOffsets[i] + node.Payload.Length + 4L * (node.WorldLights.Count + node.WorldSounds.Count);
                        foreach (var row in source.Data["partitions"] as JsonArray ?? [])
                            foreach (var cell in row as JsonArray ?? [])
                            {
                                WorldArea a = new() { Record = bytes.Slice((int)area, 64).ToArray() };
                                foreach (var index in cell!["node_indices"] as JsonArray ?? []) a.Nodes.Add(Node((int)JsonData.Integer(index)));
                                node.Areas.Add(a); area += 64 + 4L * a.Nodes.Count;
                            }
                        break;
                    }
            }
        }
        // The file's links can form any graph; everything that walks a world follows them recursively.
        WorldUpdate.CheckHierarchy(world.Nodes);
        return world;
    }

    private static uint U(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);
    private static float F(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadSingleLittleEndian(b);
    private static Vector3 Vec(ReadOnlySpan<byte> b) => new(F(b), F(b[4..]), F(b[8..]));
    private static WorldBox Box(ReadOnlySpan<byte> b) => new(Vec(b), Vec(b[12..]));
}
