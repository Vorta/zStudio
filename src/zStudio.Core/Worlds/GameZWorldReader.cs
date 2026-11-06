using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Formats;

namespace zStudio.Core.Worlds;

/// <summary>
/// Builds a <see cref="GameZWorld"/> from a document the shared GameZ reader has opened: its decoded scene supplies
/// structure and references, and the reader's recorded offsets locate each record's stored fields. Version 13 (the 1998
/// demos) differs only in its node slots and Object3D records, which are read into the same version-15 model. Version 200
/// (reRECOIL v2) differs in the widths of its material links, node free tags, grid cell counts and crater limit: its freed
/// node slots and grid cells become version-15 records, and a world's crater limit is the 32-bit word the model always keeps.
/// </summary>
public static class GameZWorldReader
{
    /// <summary>Version 13 node slots: the version-15 fields up to the sphere, the cached box as eight corners, then the rest.</summary>
    private const int DemoSlotSize = 268, DemoCorners = 116, DemoModelBox = 212, DemoChildBox = 236, DemoActivation = 260;
    /// <summary>A version-13 Object3D record: the version-15 fields with a translation (12 bytes, a script's Object3DTranslate) after the scale.</summary>
    private const int DemoObject3DSize = 156, DemoTranslation = 48;

    public static GameZWorld FromDocument(AssetDocument doc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        bool mech = doc.Probe.Family == FormatFamily.Archive && doc.Assets.Any(a => a.Content is MechAssembly);
        if ((!mech && doc.Probe is not { Family: FormatFamily.GameZ, Version: 13 or 15 or 27 or GameZWriter.WideVersion }) || doc.Scene is not { } scene)
            throw new InvalidDataException("A supported GameZ world is required.");
        var layout = doc.GameZLayout;
        if (doc.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException(error.Message);
        var bytes = doc.Bytes.Span;
        bool demo = !mech && doc.Probe.Version == 13, mw3 = mech || doc.Probe.Version == 27, wide = doc.Probe.Version == GameZWriter.WideVersion;
        int slotSize = mech ? 208 : demo ? DemoSlotSize : mw3 ? 212 : wide ? GameZWriter.WideNodeSlotSize : GameZWriter.NodeSlotSize;
        int materialSlotSize = wide ? GameZWriter.WideMaterialSlotSize : GameZWriter.MaterialSlotSize;
        int textureStride = mw3 ? 40 : 36, modelStride = mw3 ? 96 : 88, modelShift = mw3 ? 4 : 0;
        GameZWorld world = new() { SourceVersion = mw3 ? 27u : doc.Probe.Version!.Value, MaterialCapacity = mech ? scene.Materials.Count : layout!.MaterialCapacity, ModelCapacity = mech ? scene.Models.Count : layout!.ModelCapacity, NodeCapacity = mech ? scene.Nodes.Count : layout!.NodeCapacity };
        if (!mech) world.FreeHead = BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]);

        for (int i = 0; i < scene.Textures.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (mech) { world.Textures.Add(new(scene.Textures[i].Text("name"))); continue; }
            var entry = bytes.Slice(layout!.TextureOffset + i * textureStride, textureStride);
            world.Textures.Add(new(scene.Textures[i].Text("name")) { NameField = entry.Slice(8, 20).ToArray(), State = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]) });
        }
        for (int i = 0; i < scene.Textures.Count; i++)
        {
            if (mech) break;
            token.ThrowIfCancellationRequested();
            int next = BinaryPrimitives.ReadInt32LittleEndian(bytes[(layout!.TextureOffset + i * textureStride + (mw3 ? 36 : 32))..]);
            if (next < -1 || next >= world.Textures.Count)
                throw new InvalidDataException($"Texture {i} has next-variant index {next} outside its directory; it cannot be reconstructed or compared as no link.");
            if (next >= 0) world.Textures[i].NextVariant = world.Textures[next];
        }
        for (int i = 0; i < scene.Materials.Count; i++)
        {
            var m = bytes.Slice(mech ? checked((int)scene.Materials[i]["source_header_offset"]!.GetValue<long>()) : layout!.MaterialOffset + 16 + i * materialSlotSize, mech ? 40 : materialSlotSize);
            int texture = mech ? scene.Materials[i].Int("texture_index", -1) : BinaryPrimitives.ReadInt32LittleEndian(m[16..]); ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(m);
            ushort? nativeFlags = mw3 ? m[1] : null;
            if (mw3) flags = (ushort)(m[0] | ((m[1] & 1) != 0 ? 0x100 : 0) | ((m[1] & 4) != 0 ? 0x400 : 0));
            token.ThrowIfCancellationRequested();
            if ((flags & 0x100) != 0 && (texture < 0 || texture >= world.Textures.Count))
                throw new InvalidDataException($"Material {i} has texture index {texture} outside its directory; it cannot be reconstructed or compared as untextured.");
            world.Materials.Add(new()
            {
                Flags = flags, Mw3Flags = nativeFlags, PackedColor = BinaryPrimitives.ReadUInt16LittleEndian(m[2..]), Color = Vec(m[4..]),
                Texture = (flags & 0x100) != 0 ? world.Textures[texture] : null,
                Field14 = F(m[20..]), Field18 = F(m[24..]), Field1C = F(m[28..]), Soil = BinaryPrimitives.ReadUInt32LittleEndian(m[32..]),
            });
        }
        if (mw3 && !mech)
        {
            int at = checked(layout!.MaterialOffset + 16 + layout.MaterialCapacity * 44);
            for (int i = world.Materials.Count - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested(); var material = world.Materials[i];
                if ((material.Mw3Flags!.Value & 4) == 0) continue;
                var record = bytes.Slice(at, 28); int count = BinaryPrimitives.ReadInt32LittleEndian(record[16..]);
                BinaryCursor.CheckRange(bytes.Length, at + 28L, count * 4L);
                WorldMaterialCycle cycle = new() { Loop = U(record) != 0, Frame = BinaryPrimitives.ReadInt32LittleEndian(record[4..]), Time = F(record[8..]), Speed = F(record[12..]), Index = U(record[20..]) };
                for (int t = 0; t < count; t++)
                {
                    int index = BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 28 + t * 4)..]);
                    if (index < 0 || index >= world.Textures.Count) throw new InvalidDataException("An MW3 material cycle references a missing texture.");
                    cycle.Textures.Add(world.Textures[index]);
                }
                material.Mw3Cycle = cycle; at = checked(at + 28 + count * 4);
            }
        }
        var modelAssets = doc.Assets.Where(a => a.Kind == AssetKind.Model).ToDictionary(a => a.Index);
        foreach (var model in scene.Models)
        {
            token.ThrowIfCancellationRequested();
            var info = bytes.Slice(mech ? checked((int)model.Metadata["source_header_offset"]!.GetValue<long>()) : layout!.ModelOffset + 12 + model.Index * modelStride, mech ? 92 : modelStride);
            WorldModel m = new()
            {
                Mode = U(info), Mw3FacadeMode = mw3 ? U(info[4..]) : null, Flags = U(info[(4 + modelShift)..]), MorphFactor = F(info[(32 + modelShift)..]), ScrollU = F(info[(36 + modelShift)..]), ScrollV = F(info[(40 + modelShift)..]), ScrollFrame = U(info[(44 + modelShift)..]),
                BoundsCentre = Vec(info[(68 + modelShift)..]), BoundsRadius = F(info[(80 + modelShift)..]),
            };
            m.Vertices.AddRange(model.Vertices); m.Normals.AddRange(model.Normals); m.Morphs.AddRange(model.Morphs);
            long data = (mech ? model.Metadata["source_data_offset"]!.GetValue<long>() : modelAssets[model.Index].Offset) + 12L * (model.Vertices.Length + model.Normals.Length + model.Morphs.Length);
            int points = (int)U(info[(28 + modelShift)..]); long pointVertices = data + 76L * points;
            for (int p = 0; p < points; p++)
            {
                token.ThrowIfCancellationRequested();
                byte[] record = bytes.Slice((int)(data + 76L * p), 76).ToArray(); int count = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(12));
                Vector3[] vertices = new Vector3[count];
                for (int v = 0; v < count; v++)
                {
                    if ((v & 1023) == 0) token.ThrowIfCancellationRequested();
                    vertices[v] = Vec(bytes[(int)(pointVertices + 12L * v)..]);
                }
                pointVertices += 12L * count; m.Points.Add(new() { Record = record, Vertices = vertices });
            }
            long polygons = pointVertices;
            for (int p = 0; p < model.Polygons.Length; p++)
            {
                token.ThrowIfCancellationRequested();
                var source = model.Polygons[p]; var record = bytes.Slice((int)(polygons + (mw3 ? 36L : 28L) * p), mw3 ? 36 : 28);
                int material = source.MaterialIndex;
                if (material < -1 || material >= world.Materials.Count)
                    throw new InvalidDataException($"Model {model.Index} has polygon {p} with material index {material} outside its directory.");
                // The reader only warns about these; everything that builds on the world indexes the model's lists.
                if (source.Vertices.Any(v => v < 0 || v >= m.Vertices.Count) || source.Normals.Any(n => n < 0 || n >= m.Normals.Count))
                    throw new InvalidDataException($"Model {model.Index} has a polygon that references a missing vertex or normal.");
                m.Polygons.Add(new()
                {
                    Flags = source.Flags & ~0xFFu, Priority = BinaryPrimitives.ReadInt32LittleEndian(record[4..]), Zone = U(record[(mw3 ? 32 : 24)..]),
                    Material = material >= 0 ? world.Materials[material] : null,
                    Vertices = source.Vertices, Normals = source.Normals, Uvs = source.Uvs, Colors = source.Colors,
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
            var source = scene.Nodes[i]; int sourceOffset = mech ? checked((int)source.Metadata["source_header_offset"]!.GetValue<long>()) : (int)nodeAssets[i].Offset;
            var slot = bytes.Slice(sourceOffset, slotSize);
            if (source.Class == "none")
            {
                if (mw3) { byte[] normalized = new byte[196]; slot[..192].CopyTo(normalized); slot.Slice(208, 4).CopyTo(normalized.AsSpan(192)); world.FreedSlots[i] = normalized; }
                else world.FreedSlots[i] = demo ? Version15Slot(slot) : wide ? Version15FreedSlot(slot, i, world.NodeCapacity) : slot.ToArray();
                continue;
            }
            var kind = source.Class switch
            {
                "camera" => WorldNodeClass.Camera, "world" => WorldNodeClass.World, "window" => WorldNodeClass.Window, "display" => WorldNodeClass.Display,
                "object3d" => WorldNodeClass.Object3D, "lod" => WorldNodeClass.Lod, "light" => WorldNodeClass.Light,
                _ => throw new InvalidDataException($"Node {i} has unsupported class {source.Class}.")
            };
            if (source.ModelIndex is int modelIndex && (modelIndex < 0 || modelIndex >= world.Models.Count))
                throw new InvalidDataException($"Node {i} has model index {modelIndex} outside its directory.");
            WorldNode node = new("", kind)
            {
                NameField = slot[..36].ToArray(), Flags = U(slot[36..]), AuxFlags = U(slot[40..]), BoundsFlags = U(slot[44..]), Zone = U(slot[48..]),
                Priority = U(slot[68..]), GridColumn = BinaryPrimitives.ReadInt32LittleEndian(slot[76..]), GridRow = BinaryPrimitives.ReadInt32LittleEndian(slot[80..]),
                SphereCache = slot.Slice(100, 16).ToArray(),
                CachedBounds = demo ? WorldBox.Empty : Box(slot[116..]), PrimaryBounds = Box(slot[(demo ? DemoModelBox : 140)..]), SecondaryBounds = Box(slot[(demo ? DemoChildBox : 164)..]),
                Model = source.ModelIndex is int model ? world.Models[model] : null,
            };
            if (mw3) { node.Mw3Words = new int[4]; for (int j = 0; j < 4; j++) node.Mw3Words[j] = BinaryPrimitives.ReadInt32LittleEndian(slot[(192 + j * 4)..]); }
            var data = bytes[(mech ? sourceOffset + 208 : (int)layout!.NodeDataOffsets[i])..];
            if (demo && kind == WorldNodeClass.Object3D)
            {
                // The matrix carries the translation a script set (and a model file's, which leaves the field zero), so the
                // version-15 record is the version-13 one without it.
                byte[] payload = new byte[node.Payload.Length];
                data[..DemoTranslation].CopyTo(payload); data[(DemoTranslation + 12)..DemoObject3DSize].CopyTo(payload.AsSpan(DemoTranslation));
                node.Payload = payload;
            }
            else if (mw3 && kind == WorldNodeClass.World)
            {
                node.Payload = new byte[172]; data[..84].CopyTo(node.Payload); data.Slice(100, 88).CopyTo(node.Payload.AsSpan(84));
                node.Mw3VirtualBounds = new int[4]; for (int j = 0; j < 4; j++) node.Mw3VirtualBounds[j] = BinaryPrimitives.ReadInt32LittleEndian(data[(84 + j * 4)..]);
            }
            else if (mw3 && kind == WorldNodeClass.Light) { node.Payload = Mw3LightPayload(data); node.Mw3LightFlags = U(data[176..]); }
            else node.Payload = data[..node.Payload.Length].ToArray();
            if (demo) node.CachedBounds = DemoCachedBounds(node, slot.Slice(DemoCorners, 96));
            live[i] = node; world.Nodes.Add(node);
        }
        WorldNode Node(int index)
        {
            token.ThrowIfCancellationRequested();
            return index >= 0 && index < live.Length && live[index] is { } n ? n : throw new InvalidDataException($"A reference names node slot {index}, which is not live.");
        }
        WorldNode? Optional(int index) => index < 0 ? null : Node(index);
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
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
                        long area = layout!.NodeDataOffsets[i] + (mw3 ? 188 : node.Payload.Length) + 4L * (node.WorldLights.Count + node.WorldSounds.Count);
                        foreach (var row in source.Data["partitions"] as JsonArray ?? [])
                            foreach (var cell in row as JsonArray ?? [])
                            {
                                token.ThrowIfCancellationRequested();
                                var record = bytes.Slice((int)area, 64);
                                WorldArea a = new() { Record = wide ? Version15Area(record, node.Areas.Count) : record.ToArray() };
                                foreach (var index in cell!["node_indices"] as JsonArray ?? []) a.Nodes.Add(Node((int)JsonData.Integer(index)));
                                node.Areas.Add(a); area += (mw3 ? 72 : 64) + 4L * a.Nodes.Count;
                            }
                        break;
                    }
            }
        }
        // The file's links can form any graph; everything that walks a world follows them recursively.
        WorldUpdate.CheckHierarchy(world.Nodes, token);
        token.ThrowIfCancellationRequested();
        return world;
    }

    private static byte[] Mw3LightPayload(ReadOnlySpan<byte> data)
    {
        byte[] result = new byte[228]; data[..176].CopyTo(result.AsSpan(8));
        uint flags = U(data[176..]);
        BinaryPrimitives.WriteInt32LittleEndian(result, (flags & 1) != 0 ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), (flags & 2) != 0 ? 1 : 0);
        uint[] bits = [4, 8, 16, 32, 128];
        for (int i = 0; i < bits.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(184 + i * 4), (flags & bits[i]) != 0 ? 1 : 0);
        data.Slice(180, 8).CopyTo(result.AsSpan(204)); data.Slice(192, 8).CopyTo(result.AsSpan(212));
        data.Slice(200, 8).CopyTo(result.AsSpan(220)); return result;
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
        static bool Close(Vector3 a, Vector3 b)
        {
            // Finite large boxes must not compare equal because both float norms overflow to infinity.
            if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(a.Z)
                || !float.IsFinite(b.X) || !float.IsFinite(b.Y) || !float.IsFinite(b.Z)) return false;
            static double Length(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);
            return Length((double)a.X - b.X, (double)a.Y - b.Y, (double)a.Z - b.Z)
                <= 1e-3 * (1 + Math.Max(Length(a.X, a.Y, a.Z), Length(b.X, b.Y, b.Z)));
        }
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
    /// <summary>
    /// A freed version-200 slot as the version-15 one the model keeps: the same node and boxes, and its next free slot and
    /// flags in one tag (flags in the top byte, the link in the low 24 bits). The table holds at most 65,536 slots, so every
    /// link fits; a link outside the table or flags in the low 24 bits (which the game also refuses) cannot be kept.
    /// </summary>
    private static byte[] Version15FreedSlot(ReadOnlySpan<byte> slot, int index, int capacity)
    {
        int next = BinaryPrimitives.ReadInt32LittleEndian(slot[(GameZWriter.WideNodeSlotSize - 8)..]);
        uint flags = U(slot[(GameZWriter.WideNodeSlotSize - 4)..]);
        if (next < -1 || next >= capacity) throw new InvalidDataException($"Node slot {index} names free slot {next}, which is outside the table of {capacity:N0}.");
        if ((flags & 0x00FFFFFF) != 0) throw new InvalidDataException($"Node slot {index} has slot flags 0x{flags:X8}; bits 0 to 23 hold no flag.");
        byte[] result = new byte[GameZWriter.NodeSlotSize];
        slot[..(GameZWriter.NodeSlotSize - 4)].CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(GameZWriter.NodeSlotSize - 4), flags | ((uint)next & 0x00FFFFFF));
        return result;
    }
    /// <summary>
    /// A version-200 grid cell as the version-15 record the model keeps: the crater count in the byte at +0x39, the unknown
    /// byte before it 0, and no counts or list after it (the writer stores those from the cell's nodes). The model holds 0 to
    /// 255 craters a cell, which covers every world zStudio writes and the shipped ones (all store 0).
    /// </summary>
    private static byte[] Version15Area(ReadOnlySpan<byte> record, int cell)
    {
        int features = BinaryPrimitives.ReadInt32LittleEndian(record[0x38..]);
        if (features is < 0 or > 255) throw new InvalidDataException($"World grid cell {cell} stores {features} craters; zStudio's world model holds 0 to 255 a cell.");
        byte[] result = record.ToArray();
        result.AsSpan(0x38, 8).Clear(); result[0x39] = (byte)features;
        return result;
    }

    private static uint U(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);
    private static float F(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadSingleLittleEndian(b);
    private static Vector3 Vec(ReadOnlySpan<byte> b) => new(F(b), F(b[4..]), F(b[8..]));
    private static WorldBox Box(ReadOnlySpan<byte> b) => new(Vec(b), Vec(b[12..]));
}
