using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using zStudio.Core.Formats;

namespace zStudio.Core.Worlds;

/// <summary>
/// Writes a GameZ v15 world as the engine does (CZZbd::WriteZBDFile, retail 0x454A50): the texture directory, the
/// material pool, the display-instance pool with its data, and the full node table with class data, all references as
/// slot indices. Stored pointer words are runtime values the loader replaces, so they are written as 0, except the
/// non-zero markers zStudio's reader uses for present UV and normal arrays.
/// </summary>
public static class GameZWriter
{
    public const uint Magic = 0x02971222, Version = 15;
    internal const int NodeSlotSize = 196; private const int ModelSlotSize = 88, MaterialSlotSize = 44, TextureEntrySize = 36;

    public static byte[] Write(GameZWorld world, CancellationToken token = default)
    {
        Validate(world, token);
        bool mw3 = world.SourceVersion == 27;
        int nodeSlotSize = mw3 ? 212 : NodeSlotSize, modelSlotSize = mw3 ? 96 : ModelSlotSize;
        var slotOf = SlotIndices(world);
        var materialIndex = Indices(world.Materials); var modelIndex = Indices(world.Models); var textureIndex = Indices(world.Textures);
        int Texture(WorldTexture? t) => t == null ? -1 : textureIndex.TryGetValue(t, out int i) ? i : throw new InvalidDataException($"Texture {t.Name} is not in the texture directory.");
        int Slot(WorldNode? n) => n == null ? -1 : slotOf.TryGetValue(n, out int i) ? i : throw new InvalidDataException($"Node {n.Name} is not in the world.");

        using MemoryStream stream = new(); using BinaryWriter w = new(stream, Encoding.Latin1);
        w.Write(new byte[36]);
        // Texture directory (zImage::WriteTextureDirectory, retail 0x46D360).
        int textureOffset = (int)stream.Position;
        foreach (var t in world.Textures)
        {
            w.Write(0); w.Write(0);
            byte[] name = t.NameField is { Length: 20 } field ? field : Name(t.Name, 20);
            w.Write(name); w.Write(t.State); if (mw3) w.Write(0); w.Write(Texture(t.NextVariant));
        }
        // Material pool (zModel_MatlBuffer::WriteGameZ, retail 0x480600): the active list runs from the newest slot down
        // to 0 and the free list up from the first unused slot, both doubly linked.
        int materialOffset = (int)stream.Position, materials = world.Materials.Count;
        // A full pool stores -1 as its free head, as the engine's own chain does: AllocFromFreeList refuses only a negative head.
        w.Write(world.MaterialCapacity); w.Write(materials); w.Write(materials < world.MaterialCapacity ? materials : -1); w.Write(materials - 1);
        for (int i = 0; i < world.MaterialCapacity; i++)
        {
            if (i < materials)
            {
                var m = world.Materials[i];
                ushort flags = (ushort)(m.Texture != null ? m.Flags | 0x100 : m.Flags & ~0x100 & ~0x400);
                if (mw3)
                {
                    int native = (m.Mw3Flags ?? 0x10) & ~5;
                    if (m.Texture != null) native |= 1;
                    if (m.Mw3Cycle != null) native |= 4;
                    flags = (ushort)((native << 8) | (m.Flags & 255));
                }
                w.Write(flags); w.Write(m.PackedColor);
                // Untextured slots keep a zero texture word (the loader converts it only for textured materials).
                w.Write(m.Color.X); w.Write(m.Color.Y); w.Write(m.Color.Z); w.Write(m.Texture == null ? 0 : Texture(m.Texture));
                w.Write(m.Field14); w.Write(m.Field18); w.Write(m.Field1C); w.Write(m.Soil); w.Write(0);
                w.Write((short)(i == materials - 1 ? -1 : i + 1)); w.Write((short)(i - 1));
            }
            else { if (mw3) { w.Write((ushort)0x2000); w.Write(new byte[38]); } else w.Write(new byte[40]); w.Write((short)(i == materials ? -1 : i - 1)); w.Write((short)(i == world.MaterialCapacity - 1 ? -1 : i + 1)); }
        }
        if (mw3)
            foreach (var material in world.Materials.AsEnumerable().Reverse())
                if (material.Mw3Cycle is { } cycle)
                {
                    token.ThrowIfCancellationRequested();
                    w.Write(cycle.Loop ? 1 : 0); w.Write(cycle.Frame); w.Write(cycle.Time); w.Write(cycle.Speed);
                    w.Write(cycle.Textures.Count); w.Write(cycle.Index); w.Write(0);
                    foreach (var texture in cycle.Textures) w.Write(Texture(texture));
                }
        // Display instances (zModel_DiPool::WriteToStream, retail 0x4815C0): table, then each live model's data at the
        // absolute offset its slot records (ReadEntryByIndexFromStream seeks it when craters reload terrain).
        int modelOffset = (int)stream.Position, models = world.Models.Count;
        var refCounts = RefCounts(world);
        w.Write(world.ModelCapacity); w.Write(models); w.Write(models < world.ModelCapacity ? models : -1);
        long table = stream.Position; stream.Position += (long)world.ModelCapacity * modelSlotSize;
        long[] dataOffsets = new long[models];
        for (int i = 0; i < models; i++)
        {
            token.ThrowIfCancellationRequested(); var m = world.Models[i]; dataOffsets[i] = stream.Position;
            foreach (var v in m.Vertices) Vector(w, v);
            foreach (var v in m.Normals) Vector(w, v);
            foreach (var v in m.Morphs) Vector(w, v);
            foreach (var p in m.Points)
            {
                if (p.Record.Length != 76) throw new InvalidDataException("Point entries are 76 bytes.");
                byte[] record = (byte[])p.Record.Clone(); BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12), p.Vertices.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), 0); w.Write(record);
            }
            foreach (var p in m.Points) foreach (var v in p.Vertices) Vector(w, v);
            foreach (var p in m.Polygons)
            {
                uint flags = (p.Flags & ~0x2FFu) | (uint)p.Vertices.Length | (p.Normals.Length > 0 ? 0x200u : 0);
                w.Write(flags); w.Write(p.Priority); w.Write(1); w.Write(p.Normals.Length > 0 ? 1 : 0); w.Write(p.Uvs.Length > 0 ? 1 : 0);
                if (mw3) { w.Write(1); w.Write(p.Mw3Field24); }
                w.Write(p.Material == null ? -1 : materialIndex.TryGetValue(p.Material, out int mi) ? mi : throw new InvalidDataException("A polygon's material is not in the world."));
                w.Write(p.Zone);
            }
            foreach (var p in m.Polygons)
            {
                foreach (int v in p.Vertices) w.Write(v);
                foreach (int n in p.Normals) w.Write(n);
                foreach (var uv in p.Uvs) { w.Write(uv.X); w.Write(uv.Y); }
                if (mw3) for (int c = 0; c < p.Vertices.Length; c++) Vector(w, p.Colors.Length > 0 ? p.Colors[c] : new Vector3(255));
            }
        }
        int nodeOffset = checked((int)stream.Position);
        long end = stream.Position; stream.Position = table;
        for (int i = 0; i < world.ModelCapacity; i++)
        {
            if (i < models)
            {
                var m = world.Models[i];
                w.Write(m.Mode); if (mw3) w.Write(m.Mw3FacadeMode ?? 0); w.Write(m.Flags); w.Write(refCounts.GetValueOrDefault(m)); w.Write(m.Polygons.Count); w.Write(m.Vertices.Count);
                w.Write(m.Normals.Count); w.Write(m.Morphs.Count); w.Write(m.Points.Count); w.Write(m.MorphFactor); w.Write(m.ScrollU); w.Write(m.ScrollV); w.Write(m.ScrollFrame);
                w.Write(new byte[20]); Vector(w, m.BoundsCentre); w.Write(m.BoundsRadius); if (mw3) w.Write(0); w.Write(checked((int)dataOffsets[i]));
            }
            else { w.Write(new byte[modelSlotSize - 4]); w.Write(i == world.ModelCapacity - 1 ? -1 : i + 1); }
        }
        stream.Position = end;
        // Node table (CZZbd::WriteNodeTable, retail 0x454890): every slot, then each slot's class data and lists in slot order.
        long nodeTable = stream.Position; stream.Position += (long)world.NodeCapacity * nodeSlotSize;
        var slots = SlotSequence(world);
        int lastUsed = slots.Count;
        long[] classOffsets = new long[slots.Count];
        for (int s = 0; s < slots.Count; s++)
        {
            token.ThrowIfCancellationRequested();
            if (slots[s] is not WorldNode node) { classOffsets[s] = -1; continue; }
            classOffsets[s] = stream.Position;
            byte[] payload = (byte[])node.Payload.Clone();
            switch (node.Class)
            {
                case WorldNodeClass.Camera:
                    BinaryPrimitives.WriteInt32LittleEndian(payload, Slot(node.CameraWorld)); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), Slot(node.CameraWindow));
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), Slot(node.CameraHorizon)); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), Slot(node.CameraHorizonXZ));
                    w.Write(payload); break;
                case WorldNodeClass.World:
                    {
                        int columns = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0x78)), rows = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0x7C));
                        if (columns < 0 || rows < 0 || (columns == 0) != (rows == 0) || (long)columns * rows != node.Areas.Count) throw new InvalidDataException($"World {node.Name} has {node.Areas.Count} areas for a {columns} × {rows} grid.");
                        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x90), node.WorldLights.Count); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x9C), node.WorldSounds.Count);
                        foreach (int offset in new[] { 0x0C, 0x80, 0x94, 0x98, 0xA0, 0xA4 }) BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), 0);
                        if (mw3)
                        {
                            w.Write(payload.AsSpan(0, 84));
                            int[] bounds = node.Mw3VirtualBounds.Length == 4 ? node.Mw3VirtualBounds : [1, 1, Math.Max(0, columns - 1), Math.Max(0, rows - 1)];
                            foreach (int value in bounds) w.Write(value);
                            w.Write(payload.AsSpan(84));
                        }
                        else w.Write(payload);
                        foreach (var light in node.WorldLights) w.Write(Slot(light));
                        foreach (var sound in node.WorldSounds) w.Write(Slot(sound));
                        foreach (var area in node.Areas)
                        {
                            if (area.Nodes.Count > 0x7FFF) throw new InvalidDataException("An area holds at most 32,767 nodes.");
                            byte[] record = (byte[])area.Record.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x3A), (ushort)area.Nodes.Count);
                            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x3C), 0); w.Write(record); if (mw3) { w.Write(0); w.Write(0); }
                            foreach (var child in area.Nodes) w.Write(Slot(child));
                        }
                        break;
                    }
                case WorldNodeClass.Light or WorldNodeClass.Sound:
                    {
                        int count = node.Class == WorldNodeClass.Light ? 0xDC : 0x8C;
                        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(count), node.AttachedWorlds.Count); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(count + 4), 0);
                        if (mw3 && node.Class == WorldNodeClass.Light) WriteMw3Light(w, node, payload); else w.Write(payload);
                        foreach (var attached in node.AttachedWorlds) w.Write(Slot(attached));
                        break;
                    }
                default: w.Write(payload); break;
            }
            foreach (var parent in node.Parents) w.Write(Slot(parent));
            foreach (var child in node.Children) w.Write(Slot(child));
        }
        long fileEnd = stream.Position; stream.Position = nodeTable;
        for (int s = 0; s < world.NodeCapacity; s++)
        {
            if (s < slots.Count && slots[s] is WorldNode node)
            {
                w.Write(node.NameField); w.Write(node.Flags); w.Write(node.AuxFlags); w.Write(node.BoundsFlags); w.Write(node.Zone); w.Write((int)node.Class);
                w.Write(0); w.Write(node.Model == null ? -1 : modelIndex.TryGetValue(node.Model, out int mi) ? mi : throw new InvalidDataException($"Node {node.Name} uses a model that is not in the world."));
                w.Write(0); w.Write(node.Priority); w.Write(0); w.Write(node.GridColumn); w.Write(node.GridRow);
                w.Write(node.Parents.Count); w.Write(0); w.Write(node.Children.Count); w.Write(0);
                w.Write(node.SphereCache); Box(w, node.CachedBounds); Box(w, node.PrimaryBounds); Box(w, node.SecondaryBounds); w.Write(0);
                if (mw3) for (int n = 0; n < 4; n++) w.Write(node.Mw3Words.Length == 4 ? node.Mw3Words[n] : 0);
                // freeTag: the low 24 bits record where the class data starts (informational; the loader reads sequentially).
                w.Write((uint)(classOffsets[s] & 0x00FFFFFF));
            }
            else if (s < slots.Count && slots[s] is byte[] freed)
            {
                if (mw3) { w.Write(freed.AsSpan(0, 192)); w.Write(new byte[16]); w.Write(freed.AsSpan(192, 4)); }
                else w.Write(freed);
            }
            else
            {
                // A never-used slot: zero apart from "no model" and the next free slot.
                w.Write(new byte[60]); w.Write(-1); w.Write(new byte[mw3 ? 144 : 128]); w.Write(s == world.NodeCapacity - 1 ? 0x00FFFFFF : s + 1);
            }
        }
        stream.Position = 0;
        w.Write(Magic); w.Write(mw3 ? 27u : Version); w.Write(world.Textures.Count); w.Write(textureOffset); w.Write(materialOffset); w.Write(modelOffset);
        w.Write(world.NodeCapacity); w.Write(world.FreeHead ?? lastUsed); w.Write(nodeOffset);
        FormatRegistry.ValidateDocumentSize(fileEnd);
        return stream.ToArray();
    }

    private static Dictionary<T, int> Indices<T>(IReadOnlyList<T> items) where T : class
    {
        Dictionary<T, int> result = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < items.Count; i++) result.TryAdd(items[i], i);
        return result;
    }

    private static void WriteMw3Light(BinaryWriter writer, WorldNode node, byte[] payload)
    {
        writer.Write(payload.AsSpan(8, 176));
        uint flags = node.Mw3LightFlags ?? 0x233u;
        uint[] bits = [1, 2, 4, 8, 16, 32, 128]; int[] offsets = [0, 4, 184, 188, 192, 196, 200];
        for (int i = 0; i < bits.Length; i++) flags = node.PayloadInt(offsets[i]) != 0 ? flags | bits[i] : flags & ~bits[i];
        bool white = node.PayloadFloat(172) == 1 && node.PayloadFloat(176) == 1 && node.PayloadFloat(180) == 1;
        flags = (flags & 128) != 0 && (flags & 96) == 0 && white ? flags | 256 : flags & ~256u;
        writer.Write(flags); float near = node.PayloadFloat(204);
        writer.Write(near); writer.Write(node.PayloadFloat(208)); writer.Write(WorldNumbers.Finite((float)(near * (double)near)));
        writer.Write(node.PayloadFloat(212)); writer.Write(node.PayloadFloat(216)); writer.Write(payload.AsSpan(220, 8));
    }
    /// <summary>Each model's reference count is the number of nodes that use it.</summary>
    public static Dictionary<WorldModel, int> RefCounts(GameZWorld world)
    {
        Dictionary<WorldModel, int> counts = new(ReferenceEqualityComparer.Instance);
        foreach (var node in world.Nodes) if (node.Model != null) counts[node.Model] = counts.GetValueOrDefault(node.Model) + 1;
        return counts;
    }

    /// <summary>Slot contents in order: live nodes fill the slots freed slots do not occupy.</summary>
    internal static List<object> SlotSequence(GameZWorld world) => SlotSequence(world, CancellationToken.None);
    private static List<object> SlotSequence(GameZWorld world, CancellationToken token)
    {
        List<object> slots = []; int live = 0;
        // The last freed slot, found once: freed slots above every live node are written up to it.
        int lastFreed = world.FreedSlots.Count > 0 ? world.FreedSlots.Keys.Max() : -1;
        while (live < world.Nodes.Count || slots.Count <= lastFreed)
        {
            token.ThrowIfCancellationRequested();
            if (world.FreedSlots.TryGetValue(slots.Count, out var freed)) slots.Add(freed);
            else if (live < world.Nodes.Count) slots.Add(world.Nodes[live++]);
            else break;
        }
        return slots;
    }
    /// <summary>Each live node's slot in the world file, which is the node's index in a scene read from the file.</summary>
    public static IReadOnlyDictionary<WorldNode, int> NodeSlots(GameZWorld world) => SlotIndices(world);
    public static IReadOnlyDictionary<WorldNode, int> NodeSlots(GameZWorld world, CancellationToken token) => SlotIndices(world, token);
    internal static Dictionary<WorldNode, int> SlotIndices(GameZWorld world) => SlotIndices(world, CancellationToken.None);
    private static Dictionary<WorldNode, int> SlotIndices(GameZWorld world, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Dictionary<WorldNode, int> result = new(ReferenceEqualityComparer.Instance); var slots = SlotSequence(world, token);
        for (int i = 0; i < slots.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (slots[i] is WorldNode node) result[node] = i;
        }
        return result;
    }

    internal static void Validate(GameZWorld world, CancellationToken token)
    {
        bool mw3 = world.SourceVersion == 27;
        GameZLayouts.CheckEntries("node capacity", world.NodeCapacity);
        GameZLayouts.CheckEntries("model capacity", world.ModelCapacity);
        GameZLayouts.CheckEntries("material capacity", world.MaterialCapacity, 32767);
        foreach (var (slot, record) in world.FreedSlots)
            if (slot < 0 || slot >= world.NodeCapacity || record.Length != NodeSlotSize) throw new InvalidDataException("A freed node slot is outside the table or has an invalid record.");
        WorldGeometryBudget geometry = new();
        long bytes = 36L + (mw3 ? 40 : TextureEntrySize) * (long)world.Textures.Count + 16 + MaterialSlotSize * (long)world.MaterialCapacity
            + 12 + (mw3 ? 96 : ModelSlotSize) * (long)world.ModelCapacity + (mw3 ? 212 : NodeSlotSize) * (long)world.NodeCapacity;
        foreach (var material in world.Materials)
            if (material.Mw3Cycle is { } cycle)
            {
                if (!mw3 || cycle.Textures.Count > 4096) throw new InvalidDataException("Material cycles require an MW3 world and at most 4,096 texture frames.");
                bytes += 28L + cycle.Textures.Count * 4L; FormatRegistry.ValidateDocumentSize(bytes);
            }
        foreach (var model in world.Models)
        {
            token.ThrowIfCancellationRequested(); geometry.Add(model); WorldNumbers.Model(model);
            bytes += 12L * ((long)model.Vertices.Count + model.Normals.Count + model.Morphs.Count) + 76L * model.Points.Count + (mw3 ? 36L : 28L) * model.Polygons.Count;
            foreach (var point in model.Points) bytes += 12L * point.Vertices.Length;
            foreach (var polygon in model.Polygons) bytes += 4L * ((long)polygon.Vertices.Length + polygon.Normals.Length) + 8L * polygon.Uvs.Length + (mw3 ? 12L * polygon.Vertices.Length : 0);
            FormatRegistry.ValidateDocumentSize(bytes);
        }
        GameZReader.RecordBudget references = new("node reference"), partitionCells = new("world partition cell", FormatRegistry.MaximumDirectoryEntries);
        foreach (var node in world.Nodes)
        {
            token.ThrowIfCancellationRequested(); WorldNumbers.Node(node);
            long links = (long)node.Parents.Count + node.Children.Count + node.WorldLights.Count + node.WorldSounds.Count + node.AttachedWorlds.Count;
            partitionCells.Add(node.Areas.Count);
            foreach (var area in node.Areas) links += area.Nodes.Count;
            references.Add(links);
            bytes += node.Payload.Length + (mw3 && node.Class == WorldNodeClass.World ? 16 : 0) + (mw3 ? 72L : 64L) * node.Areas.Count + 4L * links;
            FormatRegistry.ValidateDocumentSize(bytes);
        }
        FormatRegistry.ValidateDocumentSize(bytes);
        if (world.Textures.Count > 0x1000) throw new InvalidDataException("The texture directory holds at most 4,096 entries.");
        if (world.Materials.Count > world.MaterialCapacity || world.MaterialCapacity > 32767) throw new InvalidDataException($"The world uses {world.Materials.Count:N0} of {world.MaterialCapacity:N0} material slots.");
        if (world.Models.Count > world.ModelCapacity) throw new InvalidDataException($"The world uses {world.Models.Count:N0} of {world.ModelCapacity:N0} model slots.");
        if (SlotSequence(world).Count > world.NodeCapacity) throw new InvalidDataException($"The world uses more than {world.NodeCapacity:N0} node slots.");
        foreach (var t in world.Textures) if (t.NameField == null && (t.Name.Length is < 1 or > 19)) throw new InvalidDataException($"Texture name '{t.Name}' must have 1–19 characters.");
        foreach (var m in world.Models)
            foreach (var p in m.Polygons)
            {
                if (p.Vertices.Length is < 3 or > 255) throw new InvalidDataException("Polygons have 3–255 corners.");
                if (p.Normals.Length is not 0 && p.Normals.Length != p.Vertices.Length || p.Uvs.Length is not 0 && p.Uvs.Length != p.Vertices.Length) throw new InvalidDataException("Polygon normals and UVs must match its corners.");
                if (p.Colors.Length != 0 && (!mw3 || p.Colors.Length != p.Vertices.Length)) throw new InvalidDataException("Only MW3 polygons carry a color per corner.");
                if (p.Colors.Any(c => !float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z))) throw new InvalidDataException("A polygon has a non-finite corner color.");
                if (p.Vertices.Any(v => v < 0 || v >= m.Vertices.Count) || p.Normals.Any(n => n < 0 || n >= m.Normals.Count)) throw new InvalidDataException("A polygon references a missing vertex or normal.");
                // The engine reads UVs when the material is textured.
                if ((p.Material?.Texture != null) != (p.Uvs.Length > 0)) throw new InvalidDataException("Polygons carry UVs exactly when their material is textured.");
            }
        foreach (var node in world.Nodes) if (node.Payload.Length != WorldNode.PayloadSize(node.Class)) throw new InvalidDataException($"Node {node.Name} has {node.Payload.Length} bytes of class data.");
    }

    private static byte[] Name(string name, int size)
    {
        byte[] field = new byte[size]; var bytes = Encoding.Latin1.GetBytes(name);
        if (bytes.Length >= size) throw new InvalidDataException($"'{name}' is longer than {size - 1} characters.");
        bytes.CopyTo(field, 0); return field;
    }
    private static void Vector(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    private static void Box(BinaryWriter w, WorldBox b) { Vector(w, b.Min); Vector(w, b.Max); }
}
