using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// Writes a GameZ v15 world as the engine does (CZZbd::WriteZBDFile, retail 0x454A50): the texture directory, the
/// material pool, the display-instance pool with its data, and the full node table with class data, all references as
/// slot indices. Stored pointer words are runtime values the loader replaces, so they are written as 0, except the
/// non-zero markers zStudio's reader uses for present UV and normal arrays.
/// </summary>
public static class GameZWriter
{
    public const uint Magic = 0x02971222, Version = 15;
    private const int NodeSlotSize = 196, ModelSlotSize = 88, MaterialSlotSize = 44, TextureEntrySize = 36;

    public static byte[] Write(GameZWorld world, CancellationToken token = default)
    {
        Validate(world);
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
            w.Write(name); w.Write(t.State); w.Write(Texture(t.NextVariant));
        }
        // Material pool (zModel_MatlBuffer::WriteGameZ, retail 0x480600): the active list runs from the newest slot down
        // to 0 and the free list up from the first unused slot, both doubly linked.
        int materialOffset = (int)stream.Position, materials = world.Materials.Count;
        w.Write(world.MaterialCapacity); w.Write(materials); w.Write(materials); w.Write(materials - 1);
        for (int i = 0; i < world.MaterialCapacity; i++)
        {
            if (i < materials)
            {
                var m = world.Materials[i];
                w.Write((ushort)(m.Texture != null ? m.Flags | 0x100 : m.Flags & ~0x100 & ~0x400)); w.Write(m.PackedColor);
                // Untextured slots keep a zero texture word (the loader converts it only for textured materials).
                w.Write(m.Color.X); w.Write(m.Color.Y); w.Write(m.Color.Z); w.Write(m.Texture == null ? 0 : Texture(m.Texture));
                w.Write(m.Field14); w.Write(m.Field18); w.Write(m.Field1C); w.Write(m.Soil); w.Write(0);
                w.Write((short)(i == materials - 1 ? -1 : i + 1)); w.Write((short)(i - 1));
            }
            else { w.Write(new byte[40]); w.Write((short)(i == materials ? -1 : i - 1)); w.Write((short)(i == world.MaterialCapacity - 1 ? -1 : i + 1)); }
        }
        // Display instances (zModel_DiPool::WriteToStream, retail 0x4815C0): table, then each live model's data at the
        // absolute offset its slot records (ReadEntryByIndexFromStream seeks it when craters reload terrain).
        int modelOffset = (int)stream.Position, models = world.Models.Count;
        var refCounts = RefCounts(world);
        w.Write(world.ModelCapacity); w.Write(models); w.Write(models);
        long table = stream.Position; stream.Position += (long)world.ModelCapacity * ModelSlotSize;
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
                w.Write(p.Material == null ? -1 : materialIndex.TryGetValue(p.Material, out int mi) ? mi : throw new InvalidDataException("A polygon's material is not in the world."));
                w.Write(p.Zone);
            }
            foreach (var p in m.Polygons)
            {
                foreach (int v in p.Vertices) w.Write(v);
                foreach (int n in p.Normals) w.Write(n);
                foreach (var uv in p.Uvs) { w.Write(uv.X); w.Write(uv.Y); }
            }
        }
        int nodeOffset = checked((int)stream.Position);
        long end = stream.Position; stream.Position = table;
        for (int i = 0; i < world.ModelCapacity; i++)
        {
            if (i < models)
            {
                var m = world.Models[i];
                w.Write(m.Mode); w.Write(m.Flags); w.Write(refCounts.GetValueOrDefault(m)); w.Write(m.Polygons.Count); w.Write(m.Vertices.Count);
                w.Write(m.Normals.Count); w.Write(m.Morphs.Count); w.Write(m.Points.Count); w.Write(m.MorphFactor); w.Write(m.ScrollU); w.Write(m.ScrollV); w.Write(m.ScrollFrame);
                w.Write(new byte[20]); Vector(w, m.BoundsCentre); w.Write(m.BoundsRadius); w.Write(checked((int)dataOffsets[i]));
            }
            else { w.Write(new byte[84]); w.Write(i == world.ModelCapacity - 1 ? -1 : i + 1); }
        }
        stream.Position = end;
        // Node table (CZZbd::WriteNodeTable, retail 0x454890): every slot, then each slot's class data and lists in slot order.
        long nodeTable = stream.Position; stream.Position += (long)world.NodeCapacity * NodeSlotSize;
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
                        if ((long)Math.Max(0, columns) * Math.Max(0, rows) != node.Areas.Count) throw new InvalidDataException($"World {node.Name} has {node.Areas.Count} areas for a {columns} × {rows} grid.");
                        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x90), node.WorldLights.Count); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x9C), node.WorldSounds.Count);
                        foreach (int offset in new[] { 0x0C, 0x80, 0x94, 0x98, 0xA0, 0xA4 }) BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), 0);
                        w.Write(payload);
                        foreach (var light in node.WorldLights) w.Write(Slot(light));
                        foreach (var sound in node.WorldSounds) w.Write(Slot(sound));
                        foreach (var area in node.Areas)
                        {
                            if (area.Nodes.Count > 0x7FFF) throw new InvalidDataException("An area holds at most 32,767 nodes.");
                            byte[] record = (byte[])area.Record.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x3A), (ushort)area.Nodes.Count);
                            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x3C), 0); w.Write(record);
                            foreach (var child in area.Nodes) w.Write(Slot(child));
                        }
                        break;
                    }
                case WorldNodeClass.Light or WorldNodeClass.Sound:
                    {
                        int count = node.Class == WorldNodeClass.Light ? 0xDC : 0x8C;
                        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(count), node.AttachedWorlds.Count); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(count + 4), 0);
                        w.Write(payload); foreach (var attached in node.AttachedWorlds) w.Write(Slot(attached));
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
                // freeTag: the low 24 bits record where the class data starts (informational; the loader reads sequentially).
                w.Write((uint)(classOffsets[s] & 0x00FFFFFF));
            }
            else if (s < slots.Count && slots[s] is byte[] freed) w.Write(freed);
            else
            {
                // A never-used slot: zero apart from "no model" and the next free slot.
                w.Write(new byte[60]); w.Write(-1); w.Write(new byte[128]); w.Write(s == world.NodeCapacity - 1 ? 0x00FFFFFF : s + 1);
            }
        }
        stream.Position = 0;
        w.Write(Magic); w.Write(Version); w.Write(world.Textures.Count); w.Write(textureOffset); w.Write(materialOffset); w.Write(modelOffset);
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
    /// <summary>Each model's reference count is the number of nodes that use it.</summary>
    public static Dictionary<WorldModel, int> RefCounts(GameZWorld world)
    {
        Dictionary<WorldModel, int> counts = new(ReferenceEqualityComparer.Instance);
        foreach (var node in world.Nodes) if (node.Model != null) counts[node.Model] = counts.GetValueOrDefault(node.Model) + 1;
        return counts;
    }

    /// <summary>Slot contents in order: live nodes fill the slots freed slots do not occupy.</summary>
    internal static List<object> SlotSequence(GameZWorld world)
    {
        List<object> slots = []; int live = 0;
        while (live < world.Nodes.Count || world.FreedSlots.Keys.Any(k => k >= slots.Count))
        {
            if (world.FreedSlots.TryGetValue(slots.Count, out var freed)) slots.Add(freed);
            else if (live < world.Nodes.Count) slots.Add(world.Nodes[live++]);
            else break;
        }
        return slots;
    }
    /// <summary>Each live node's slot in the world file, which is the node's index in a scene read from the file.</summary>
    public static IReadOnlyDictionary<WorldNode, int> NodeSlots(GameZWorld world) => SlotIndices(world);
    internal static Dictionary<WorldNode, int> SlotIndices(GameZWorld world)
    {
        Dictionary<WorldNode, int> result = new(ReferenceEqualityComparer.Instance); var slots = SlotSequence(world);
        for (int i = 0; i < slots.Count; i++) if (slots[i] is WorldNode node) result[node] = i;
        return result;
    }

    private static void Validate(GameZWorld world)
    {
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
