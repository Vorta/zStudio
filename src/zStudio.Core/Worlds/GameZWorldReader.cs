using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// Builds a <see cref="GameZWorld"/> from a document the shared GameZ reader has opened: its decoded scene supplies
/// structure and references, and the reader's recorded offsets locate each record's stored fields. Version 13 (the 1998
/// demos) differs only in its node slots and Object3D records, which are read into the same version-15 model.
/// </summary>
public static class GameZWorldReader
{
    /// <summary>Version 13 node slots: the version-15 fields up to the sphere, the cached box as eight corners, then the rest.</summary>
    private const int DemoSlotSize = 268, DemoCorners = 116, DemoModelBox = 212, DemoChildBox = 236, DemoActivation = 260;
    /// <summary>A version-13 Object3D record: the version-15 fields with a translation (12 bytes, a script's Object3DTranslate) after the scale.</summary>
    private const int DemoObject3DSize = 156, DemoTranslation = 48;

    public static GameZWorld FromDocument(ZbdDocument doc, CancellationToken token = default)
    {
        if (doc.Probe is not { Family: FormatFamily.GameZ, Version: 13 or 15 } || doc.Scene is not { } scene || doc.GameZLayout is not { } layout)
            throw new InvalidDataException("A RECOIL (version 13 or 15) GameZ world is required.");
        if (doc.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException(error.Message);
        var bytes = doc.Bytes.Span;
        bool demo = doc.Probe.Version == 13; int slotSize = demo ? DemoSlotSize : GameZWriter.NodeSlotSize;
        GameZWorld world = new() { SourceVersion = doc.Probe.Version!.Value, MaterialCapacity = layout.MaterialCapacity, ModelCapacity = layout.ModelCapacity, NodeCapacity = layout.NodeCapacity };
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
                // The reader only warns about these; everything that builds on the world indexes the model's lists.
                if (source.Vertices.Any(v => v < 0 || v >= m.Vertices.Count) || source.Normals.Any(n => n < 0 || n >= m.Normals.Count))
                    throw new InvalidDataException($"Model {model.Index} has a polygon that references a missing vertex or normal.");
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
            var source = scene.Nodes[i]; var slot = bytes.Slice((int)nodeAssets[i].Offset, slotSize);
            if (source.Class == "none") { world.FreedSlots[i] = demo ? Version15Slot(slot) : slot.ToArray(); continue; }
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
                SphereCache = slot.Slice(100, 16).ToArray(),
                CachedBounds = demo ? WorldBox.Empty : Box(slot[116..]), PrimaryBounds = Box(slot[(demo ? DemoModelBox : 140)..]), SecondaryBounds = Box(slot[(demo ? DemoChildBox : 164)..]),
                Model = source.ModelIndex is int model && model >= 0 && model < world.Models.Count ? world.Models[model] : null,
            };
            var data = bytes[(int)layout.NodeDataOffsets[i]..];
            if (demo && kind == WorldNodeClass.Object3D)
            {
                // The matrix carries the translation a script set (and a model file's, which leaves the field zero), so the
                // version-15 record is the version-13 one without it.
                byte[] payload = new byte[node.Payload.Length];
                data[..DemoTranslation].CopyTo(payload); data[(DemoTranslation + 12)..DemoObject3DSize].CopyTo(payload.AsSpan(DemoTranslation));
                node.Payload = payload;
            }
            else node.Payload = data[..node.Payload.Length].ToArray();
            if (demo) node.CachedBounds = DemoCachedBounds(node, slot.Slice(DemoCorners, 96));
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

    /// <summary>
    /// A version-13 node's cached box, which the file stores as its eight corners in the parent's space (the box under the
    /// node's own matrix): the model and child boxes its flags name where they give those corners, which every node of the
    /// 1998 demos does, otherwise the corners mapped back through the matrix.
    /// </summary>
    private static WorldBox DemoCachedBounds(WorldNode node, ReadOnlySpan<byte> stored)
    {
        Vector3[] corners = new Vector3[8];
        for (int k = 0; k < 8; k++) corners[k] = Vec(stored[(k * 12)..]);
        if (corners.All(c => c == Vector3.Zero)) return WorldBox.Empty;
        var matrix = node.Class == WorldNodeClass.Object3D ? WorldUpdate.LocalMatrix(node) : null;
        WorldBox? box = null;
        if ((node.Flags & WorldUpdate.ModelBoundsFlag) != 0) box = node.PrimaryBounds;
        if ((node.Flags & WorldUpdate.ChildBoundsFlag) != 0) box = box is { } b ? b.Union(node.SecondaryBounds) : node.SecondaryBounds;
        if (box is { } candidate)
        {
            // The same eight points both ways, so a smaller or flat box that touches one stored corner is not taken for it.
            var placed = candidate.Corners().Select(c => matrix is { } m ? Vector3.Transform(c, m) : c).ToArray();
            if (placed.All(c => corners.Any(s => Close(c, s))) && corners.All(s => placed.Any(c => Close(c, s)))) return candidate;
        }
        return matrix is { } local && Matrix4x4.Invert(local, out var inverse) ? WorldBox.Of(corners.Select(c => Vector3.Transform(c, inverse))) : WorldBox.Of(corners);
        static bool Close(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= 1e-3f * (1 + Math.Max(a.Length(), b.Length()));
    }
    /// <summary>A freed version-13 slot as a version-15 one: its name and links, its corners' extent as the box.</summary>
    private static byte[] Version15Slot(ReadOnlySpan<byte> slot)
    {
        byte[] result = new byte[GameZWriter.NodeSlotSize];
        slot[..DemoCorners].CopyTo(result);
        Vector3[] corners = new Vector3[8];
        for (int k = 0; k < 8; k++) corners[k] = Vec(slot[(DemoCorners + k * 12)..]);
        var box = WorldBox.Of(corners);
        float[] extent = [box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z];
        for (int k = 0; k < 6; k++) BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(116 + k * 4), extent[k]);
        slot[DemoModelBox..(DemoActivation + 4)].CopyTo(result.AsSpan(140));
        slot[(DemoActivation + 4)..DemoSlotSize].CopyTo(result.AsSpan(GameZWriter.NodeSlotSize - 4));
        return result;
    }

    private static uint U(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);
    private static float F(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadSingleLittleEndian(b);
    private static Vector3 Vec(ReadOnlySpan<byte> b) => new(F(b), F(b[4..]), F(b[8..]));
    private static WorldBox Box(ReadOnlySpan<byte> b) => new(Vec(b), Vec(b[12..]));
}
