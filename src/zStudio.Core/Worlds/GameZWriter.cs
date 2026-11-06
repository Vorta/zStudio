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
/// <para>
/// It also writes version 200 for reRECOIL v2 (docs/zbd-v200.md): version 15 with the fields that capped content widened
/// in their places, so that a world within version 15's limits gives two files with the same sections, records, pools and
/// free slots in the same order. Only version 200 holds what version 15's widths cannot: more than 32,767 materials, more
/// than 32,767 nodes in a grid cell, a crater limit above 255.
/// </para>
/// </summary>
public static class GameZWriter
{
    /// <summary>The world file's magic; the version RECOIL reads; the version reRECOIL v2 reads beside it.</summary>
    public const uint Magic = 0x02971222, Version = 15, WideVersion = 200;
    /// <summary>
    /// The node slot of each version (the node, its boxes and the free tag; in version 200 the next free slot and the slot's
    /// flags) and the material slot (the material and its two list links, 16-bit in version 15, 32-bit in version 200).
    /// The world model keeps freed slots as version-15 records.
    /// </summary>
    internal const int NodeSlotSize = 196, WideNodeSlotSize = 200, MaterialSlotSize = 44, WideMaterialSlotSize = 48;
    private const int ModelSlotSize = 88, TextureEntrySize = 36;
    /// <summary>The most material slots a version-15 file stores: its 16-bit links name slots up to 32,767.</summary>
    internal const int MaximumVersion15Materials = 32767;
    /// <summary>The highest crater limit a version-15 world stores: one byte (partitionMaxDecFeatureCount).</summary>
    internal const int MaximumVersion15Craters = 255;
    /// <summary>The world class data's crater limit (partitionMaxDecFeatureCount), a 32-bit word in the world model and in version 200.</summary>
    internal const int CraterLimitOffset = 0x4C;

    /// <summary>Writes the world as version 15.</summary>
    public static byte[] Write(GameZWorld world, CancellationToken token = default) => Write(world, Version, token);

    /// <summary>
    /// Writes the world as <paramref name="version"/>: <see cref="Version"/> (15) or <see cref="WideVersion"/> (200).
    /// Version 15 stores at most <see cref="MaximumVersion15Materials"/> material slots and a crater limit of at most
    /// 255, as the original build tool clamped them (the world model keeps the values the build script set).
    /// </summary>
    public static byte[] Write(GameZWorld world, uint version, CancellationToken token = default)
    {
        if (version is not (Version or WideVersion)) throw new ArgumentOutOfRangeException(nameof(version), version, "zStudio writes GameZ worlds of version 15 or 200.");
        bool wide = version == WideVersion;
        Validate(world, version, token);
        if (wide) CheckWide(world, token);
        int materialCapacity = MaterialCapacity(world, version);
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
        // A full pool stores -1 as its free head, as the engine's own chain does: AllocFromFreeList refuses only a negative head.
        w.Write(materialCapacity); w.Write(materials); w.Write(materials < materialCapacity ? materials : -1); w.Write(materials - 1);
        // A slot's previous and next links: 16-bit in version 15, 32-bit in version 200.
        void Links(int previous, int next)
        {
            if (wide) { w.Write(previous); w.Write(next); }
            else { w.Write((short)previous); w.Write((short)next); }
        }
        for (int i = 0; i < materialCapacity; i++)
        {
            if (i < materials)
            {
                var m = world.Materials[i];
                w.Write((ushort)(m.Texture != null ? m.Flags | 0x100 : m.Flags & ~0x100 & ~0x400)); w.Write(m.PackedColor);
                // Untextured slots keep a zero texture word (the loader converts it only for textured materials).
                w.Write(m.Color.X); w.Write(m.Color.Y); w.Write(m.Color.Z); w.Write(m.Texture == null ? 0 : Texture(m.Texture));
                w.Write(m.Field14); w.Write(m.Field18); w.Write(m.Field1C); w.Write(m.Soil); w.Write(0);
                Links(i == materials - 1 ? -1 : i + 1, i - 1);
            }
            else { w.Write(new byte[40]); Links(i == materials ? -1 : i - 1, i == materialCapacity - 1 ? -1 : i + 1); }
        }
        // Display instances (zModel_DiPool::WriteToStream, retail 0x4815C0): table, then each live model's data at the
        // absolute offset its slot records (ReadEntryByIndexFromStream seeks it when craters reload terrain).
        int modelOffset = (int)stream.Position, models = world.Models.Count;
        var refCounts = RefCounts(world);
        w.Write(world.ModelCapacity); w.Write(models); w.Write(models < world.ModelCapacity ? models : -1);
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
                // Version 15 keeps the corner count in the flags' low byte and a marker in the stale vertex index pointer;
                // version 200 leaves the low byte 0 and stores the count in that pointer's word.
                uint flags = (p.Flags & ~0x2FFu) | (wide ? 0 : (uint)p.Vertices.Length) | (p.Normals.Length > 0 ? 0x200u : 0);
                w.Write(flags); w.Write(p.Priority); w.Write(wide ? p.Vertices.Length : 1); w.Write(p.Normals.Length > 0 ? 1 : 0); w.Write(p.Uvs.Length > 0 ? 1 : 0);
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
        long nodeTable = stream.Position; stream.Position += (long)world.NodeCapacity * (wide ? WideNodeSlotSize : NodeSlotSize);
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
                        // The crater limit is one byte and three zero bytes in version 15, as the build tool clamped it.
                        if (!wide) BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(CraterLimitOffset), Math.Clamp(node.PayloadInt(CraterLimitOffset), 0, MaximumVersion15Craters));
                        w.Write(payload);
                        foreach (var light in node.WorldLights) w.Write(Slot(light));
                        foreach (var sound in node.WorldSounds) w.Write(Slot(sound));
                        foreach (var area in node.Areas)
                        {
                            byte[] record = (byte[])area.Record.Clone();
                            if (wide)
                            {
                                // The crater count (the model's byte at +0x39) and the node count, each 32-bit, in the places of
                                // the unknown byte, the byte count and the 16-bit node count, and of the stale node list.
                                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x38), record[0x39]);
                                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x3C), area.Nodes.Count);
                            }
                            else
                            {
                                if (area.Nodes.Count > 0x7FFF) throw new InvalidDataException("An area holds at most 32,767 nodes; version 200 for reRECOIL v2 (export target v2) holds more.");
                                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x3A), (ushort)area.Nodes.Count);
                                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x3C), 0);
                            }
                            w.Write(record);
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
        int freeHead = world.FreeHead ?? lastUsed;
        // Version 200 stores a slot's next free slot only for the slots on the free list, -1 for every other slot.
        bool[]? listed = wide ? FreeListed(world, slots, freeHead, token) : null;
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
                // Version 200: no next free slot and no flags.
                if (wide) { w.Write(-1); w.Write(0u); }
                else w.Write((uint)(classOffsets[s] & 0x00FFFFFF));
            }
            else if (s < slots.Count && slots[s] is byte[] freed)
            {
                if (!wide) w.Write(freed);
                else
                {
                    w.Write(freed.AsSpan(0, NodeSlotSize - 4));
                    w.Write(listed![s] ? Version15Link(world, slots, s) : -1);
                    w.Write(BinaryPrimitives.ReadUInt32LittleEndian(freed.AsSpan(NodeSlotSize - 4)) & 0xFF000000u);
                }
            }
            else
            {
                // A never-used slot: zero apart from "no model" and the next free slot.
                w.Write(new byte[60]); w.Write(-1); w.Write(new byte[128]);
                if (wide) { w.Write(listed![s] ? Version15Link(world, slots, s) : -1); w.Write(0u); }
                else w.Write(s == world.NodeCapacity - 1 ? 0x00FFFFFF : s + 1);
            }
        }
        stream.Position = 0;
        w.Write(Magic); w.Write(version); w.Write(world.Textures.Count); w.Write(textureOffset); w.Write(materialOffset); w.Write(modelOffset);
        w.Write(world.NodeCapacity); w.Write(freeHead); w.Write(nodeOffset);
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

    /// <summary>
    /// The material pool a world file of <paramref name="version"/> stores: the world's, at most
    /// <see cref="MaximumVersion15Materials"/> in version 15.
    /// </summary>
    private static int MaterialCapacity(GameZWorld world, uint version) => version == WideVersion ? world.MaterialCapacity : Math.Min(world.MaterialCapacity, MaximumVersion15Materials);

    /// <summary>
    /// The next free slot that version 15 stores in slot <paramref name="s"/>'s free tag: a freed slot's low 24 bits,
    /// sign-extended, or a never-used slot's following slot (-1 after the last). <paramref name="slots"/> is the
    /// <see cref="SlotSequence(GameZWorld)"/>.
    /// </summary>
    private static int Version15Link(GameZWorld world, List<object> slots, int s) => s < slots.Count && slots[s] is byte[] freed
        ? BinaryPrimitives.ReadInt32LittleEndian(freed.AsSpan(NodeSlotSize - 4)) << 8 >> 8
        : s == world.NodeCapacity - 1 ? -1 : s + 1;

    /// <summary>
    /// The slots on the node free list (from <paramref name="head"/> along each slot's version-15 link to -1), which
    /// version 200 gives their links while every other slot stores -1. Version 200 needs the list the game walks: one that
    /// ends at -1, names only the table's slots, holds no live node and passes no slot twice; a world whose list does not
    /// is refused (its version-15 file would fail to load as well).
    /// </summary>
    private static bool[] FreeListed(GameZWorld world, List<object> slots, int head, CancellationToken token)
    {
        bool[] listed = new bool[world.NodeCapacity];
        for (int s = head, steps = 0; s != -1; s = Version15Link(world, slots, s))
        {
            if ((++steps & 4095) == 0) token.ThrowIfCancellationRequested();
            if (s < 0 || s >= world.NodeCapacity) throw new InvalidDataException($"The node free list names slot {s}, which is outside the table of {world.NodeCapacity:N0} slots.");
            if (listed[s]) throw new InvalidDataException($"The node free list comes back to slot {s}.");
            if (s < slots.Count && slots[s] is WorldNode node) throw new InvalidDataException($"The node free list holds slot {s}, which node {JsonData.ShownText(node.Name)} uses.");
            listed[s] = true;
        }
        return listed;
    }

    /// <summary>
    /// Checks that every value version 15 keeps in the bytes version 200 widens over fits the wider field: a grid cell's
    /// unknown byte before its crater count must be 0 (it is in every world zStudio builds and every shipped world), and a
    /// world's crater limit must not be negative (the game refuses it).
    /// </summary>
    private static void CheckWide(GameZWorld world, CancellationToken token)
    {
        foreach (var node in world.Nodes.Where(n => n.Class == WorldNodeClass.World))
        {
            token.ThrowIfCancellationRequested();
            if (node.PayloadInt(CraterLimitOffset) < 0)
                throw new InvalidDataException($"World {JsonData.ShownText(node.Name)} has a negative crater limit ({node.PayloadInt(CraterLimitOffset)}).");
            for (int i = 0; i < node.Areas.Count; i++)
                if (node.Areas[i].Record[0x38] != 0)
                    throw new InvalidDataException($"World {JsonData.ShownText(node.Name)} grid cell {i} has the unknown byte 0x{node.Areas[i].Record[0x38]:X2} before its crater count; a version-200 world stores the crater count in its place.");
        }
    }

    /// <summary>
    /// Checks that <paramref name="world"/> is whole and that its tables fit a file of <paramref name="version"/> (version
    /// 200 holds the larger material pool), before anything is written.
    /// </summary>
    internal static void Validate(GameZWorld world, uint version, CancellationToken token)
    {
        bool wide = version == WideVersion;
        GameZLayouts.CheckEntries("node capacity", world.NodeCapacity);
        GameZLayouts.CheckEntries("model capacity", world.ModelCapacity);
        GameZLayouts.CheckEntries("material capacity", world.MaterialCapacity);
        int materialCapacity = MaterialCapacity(world, version);
        foreach (var (slot, record) in world.FreedSlots)
            if (slot < 0 || slot >= world.NodeCapacity || record.Length != NodeSlotSize) throw new InvalidDataException("A freed node slot is outside the table or has an invalid record.");
        WorldGeometryBudget geometry = new();
        long bytes = 36L + TextureEntrySize * (long)world.Textures.Count + 16 + (wide ? WideMaterialSlotSize : MaterialSlotSize) * (long)materialCapacity
            + 12 + ModelSlotSize * (long)world.ModelCapacity + (wide ? WideNodeSlotSize : NodeSlotSize) * (long)world.NodeCapacity;
        foreach (var model in world.Models)
        {
            token.ThrowIfCancellationRequested(); geometry.Add(model); WorldNumbers.Model(model);
            bytes += 12L * ((long)model.Vertices.Count + model.Normals.Count + model.Morphs.Count) + 76L * model.Points.Count + 28L * model.Polygons.Count;
            foreach (var point in model.Points) bytes += 12L * point.Vertices.Length;
            foreach (var polygon in model.Polygons) bytes += 4L * ((long)polygon.Vertices.Length + polygon.Normals.Length) + 8L * polygon.Uvs.Length;
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
            bytes += node.Payload.Length + 64L * node.Areas.Count + 4L * links;
            FormatRegistry.ValidateDocumentSize(bytes);
        }
        FormatRegistry.ValidateDocumentSize(bytes);
        if (world.Textures.Count > 0x1000) throw new InvalidDataException("The texture directory holds at most 4,096 entries.");
        if (world.Materials.Count > materialCapacity)
            throw new InvalidDataException(materialCapacity < world.MaterialCapacity && world.Materials.Count <= world.MaterialCapacity
                ? $"The world uses {world.Materials.Count:N0} materials, more than the 32,767 material slots a version-15 world stores; version 200 for reRECOIL v2 (export target v2) stores its pool of {world.MaterialCapacity:N0}."
                : $"The world uses {world.Materials.Count:N0} of {materialCapacity:N0} material slots.");
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
