using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Export;

namespace Recoil.Zbd.Core.Formats;

/// <summary>Edits versioned world ranges recorded by GameZReader. Never serializes unknown records or stored pointers.</summary>
public static partial class ModelReplacementWriter
{
    public static byte[] Replace(ZbdDocument source, IReadOnlyDictionary<int, ImportedMesh> replacements, string textureName, CancellationToken token = default)
    {
        if (replacements.Count == 0) return source.Bytes.ToArray();
        var layout = source.GameZLayout ?? throw new InvalidDataException("Model replacement requires GameZ v15 or v27.");
        var scene = source.Scene!;
        if (source.Probe.Version is not (15 or 27) || source.Diagnostics.Any(d => d.Severity == "Error")) throw new InvalidDataException("An intact GameZ v15 or v27 document is required.");
        var dialect = GameZLayouts.For(source.Probe.Version);
        int modelStride = dialect.ModelSize + 4, nodeStride = dialect.NodeSize + 4;
        var sourceModels = source.Assets.Where(a => a.Kind == AssetKind.Model).ToDictionary(a => a.Index);
        long outputLength = source.Bytes.Length + (long)dialect.TextureSize;
        foreach (var (index, mesh) in replacements)
        {
            token.ThrowIfCancellationRequested();
            if (!sourceModels.TryGetValue(index, out var original)) throw new InvalidDataException("Missing model index.");
            mesh.Validate();
            outputLength += mesh.Positions.Length * 24L + mesh.Triangles.Length / 3L * (dialect.HasVertexColors ? 120 : 76) - original.Length;
        }
        FormatRegistry.ValidateDocumentSize(outputLength);
        ValidateName(textureName);
        if (scene.Textures.Any(t => t.Text("name").Equals(textureName, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Choose a new, unique texture name.");
        if (scene.Textures.Count >= 4096) throw new InvalidDataException("The engine texture directory is full.");
        int material = scene.Materials.Count;
        byte[] materials = source.Slice(layout.MaterialOffset, layout.ModelOffset - layout.MaterialOffset).ToArray();
        if (material >= layout.MaterialCapacity || material >= short.MaxValue || I32(materials, 8) != material)
            throw new InvalidDataException("The material pool must have a contiguous free slot; fragmented or full pools are not supported.");
        int slot = 16 + material * 44, nextFree = BinaryPrimitives.ReadInt16LittleEndian(materials.AsSpan(slot + 42)), activeHead = I32(materials, 12);
        Put(materials, 4, material + 1); Put(materials, 8, nextFree); Put(materials, 12, material);
        if (activeHead < -1 || activeHead >= material || nextFree < -1 || nextFree >= layout.MaterialCapacity) throw new InvalidDataException("Invalid material pool links.");
        ValidatePool(source.Slice(layout.MaterialOffset, layout.ModelOffset - layout.MaterialOffset).ToArray(), material, layout.MaterialCapacity);
        if (nextFree >= 0) Put16(materials, 16 + nextFree * 44 + 40, -1);
        if (activeHead >= 0) Put16(materials, 16 + activeHead * 44 + 40, material);
        materials.AsSpan(slot, 44).Clear(); materials[slot] = 255; materials[slot + 1] = source.Probe.Version == 27 ? AuthoredTexturedFlags(scene) : (byte)1;
        Put16(materials, slot + 2, 32767);
        for (int i = 4; i <= 12; i += 4) Float(materials, slot + i, 255);
        Put(materials, slot + 16, scene.Textures.Count); Float(materials, slot + 24, .5f); Float(materials, slot + 28, .5f);
        Put16(materials, slot + 40, -1); Put16(materials, slot + 42, activeHead);
        ValidatePool(materials, material + 1, layout.MaterialCapacity);

        byte[] table = source.Slice(layout.ModelOffset, 12L + layout.ModelCapacity * (long)modelStride).ToArray();
        int newModelOffset = layout.ModelOffset + dialect.TextureSize;
        using MemoryStream dynamics = new();
        foreach (var model in scene.Models)
        {
            token.ThrowIfCancellationRequested();
            int header = 12 + model.Index * modelStride;
            var originalAsset = sourceModels[model.Index];
            if (I32(table, header + dialect.ModelSize) != originalAsset.Offset) throw new InvalidDataException($"Model {model.Index} has a noncanonical data offset; refusing relocation.");
            Put(table, header + dialect.ModelSize, checked(newModelOffset + table.Length + (int)dynamics.Length));
            if (!replacements.TryGetValue(model.Index, out var mesh))
            {
                dynamics.Write(source.Slice(originalAsset.Offset, originalAsset.Length).Span); continue;
            }
            if (model.Morphs.Length != 0 || model.Metadata.Int("light_count") != 0) throw new InvalidDataException($"Model {model.Index} has morphs or lights; replacement is not supported.");
            byte[] modelHeader = table.AsSpan(header, dialect.ModelSize).ToArray();
            var convention = dialect.HasVertexColors ? PolygonConvention.From(source, model, originalAsset.Offset) : default;
            dynamics.Write(EncodeMesh(modelHeader, source.Probe.Version, mesh, material, convention, token));
            modelHeader.CopyTo(table, header);
        }
        if (replacements.Keys.Any(i => i < 0 || i >= scene.Models.Count)) throw new InvalidDataException("Missing model index.");
        // Preserve padding between the last dynamic model and the node table.
        long oldEnd = source.Assets.Where(a => a.Kind == AssetKind.Model).Max(a => a.Offset + a.Length);
        dynamics.Write(source.Slice(oldEnd, layout.NodeOffset - oldEnd).Span);
        int newNodeOffset = checked(newModelOffset + table.Length + (int)dynamics.Length);
        byte[] nodes = source.Bytes.Span[layout.NodeOffset..].ToArray();
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            var node = scene.Nodes[i]; int header = i * nodeStride;
            // Class-none slots use this word for a parent index, not an offset.
            if (node.Class != "none")
            {
                if (I32(nodes, header + dialect.NodeSize) != layout.NodeDataOffsets[i]) throw new InvalidDataException($"Node {i} has a noncanonical data offset; refusing relocation.");
                Put(nodes, header + dialect.NodeSize, checked((int)layout.NodeDataOffsets[i] + newNodeOffset - layout.NodeOffset));
            }
        }
        UpdateNodeBounds(scene, nodes, replacements, nodeStride, token);
        using MemoryStream output = new();
        byte[] prefix = source.Bytes.Span[..layout.MaterialOffset].ToArray();
        Put(prefix, 8, scene.Textures.Count + 1); Put(prefix, 16, layout.MaterialOffset + dialect.TextureSize); Put(prefix, 20, newModelOffset); Put(prefix, 32, newNodeOffset);
        int insertion = layout.TextureOffset + scene.Textures.Count * dialect.TextureSize;
        output.Write(prefix.AsSpan(0, insertion));
        byte[] texture = new byte[dialect.TextureSize]; Encoding.ASCII.GetBytes(textureName).CopyTo(texture, 8); Put(texture, 28, 2); Put(texture, dialect.TextureSize - 4, -1);
        output.Write(texture); output.Write(prefix.AsSpan(insertion)); output.Write(materials); output.Write(table); dynamics.Position = 0; dynamics.CopyTo(output); output.Write(nodes);
        if (output.Length > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException("Replacement exceeds the document size limit.");
        byte[] bytes = output.ToArray();
        var check = FormatRegistry.Default.OpenBytes(source.Path, bytes, token: token);
        if (check.Diagnostics.Any(d => d.Severity == "Error") || check.Scene?.Models.Count != scene.Models.Count) throw new InvalidDataException("Replacement failed shared-reader verification.");
        foreach (var (index, mesh) in replacements)
        {
            var m = check.Scene.Models[index];
            if (m.Metadata.Int("model_type") != 0 || !m.Vertices.SequenceEqual(mesh.Positions) || m.Polygons.Length * 3 != mesh.Triangles.Length)
                throw new InvalidDataException($"Replacement model {index} failed readback.");
        }
        return bytes;
    }
    // Version-27 materials copy the flags of the world's most common authored opaque, textured, non-cycling
    // material (all six retail MW3 worlds: 0x11; every MW3 material carries 0x10, which version 15 never uses).
    // This follows authored corpus conventions; MW3 engine semantics remain unverified.
    private static byte AuthoredTexturedFlags(GameScene scene) => scene.Materials
        .Where(m => (m.UInt("flags") & 5) == 1 && m.Int("alpha") == 255 && m.Int("texture_index", -1) >= 0 && m.Text("cycle_ptr") == "0x00000000")
        .GroupBy(m => (byte)m.UInt("flags")).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (byte?)g.Key).FirstOrDefault() ?? 0x11;
    public static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 19 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new InvalidDataException("Texture name must be 1–19 ASCII letters, digits or underscores.");
    }
    // Retail zDi::RebuildBounds 0x483AD0: centre = min + half extent; the radius uses the engine's
    // square-root bit approximation, which reproduces stored corpus radii (1.41 for a 2.2 x 1 x 1.2 box).
    internal static (Vector3 Center, float Radius) DisplayInstanceSphere(Vector3 min, Vector3 max)
    {
        float hx = (max.X - min.X) * .5f, hy = (max.Y - min.Y) * .5f, hz = (max.Z - min.Z) * .5f;
        float squared = hx * hx + hy * hy + hz * hz;
        return (new(hx + min.X, hy + min.Y, hz + min.Z), BitConverter.Int32BitsToSingle((BitConverter.SingleToInt32Bits(squared) >> 1) + 0x1FC00000));
    }
    // Node-local bounds (zClass.h CZNode): +0x74 cached node box, +0x8C model box, +0xA4 child box.
    // gwNodeRecalcBBox 0x448E90: cached = model (flag 0x200) ∪ child (flag 0x400); gwNodeComputeChildBBox
    // 0x4491B0: child = ∪ children's cached boxes × their local matrices. CZZbd::ReadNodeTable 0x455350
    // loads them verbatim, so replaced nodes receive solid-model boxes here and ancestors expand only
    // when a new box no longer fits, keeping stored envelopes valid supersets. +0x64/+0x70 is a
    // render-time world-sphere cache and stays unchanged. World partitions are never re-gridded.
    internal static void UpdateNodeBounds(GameScene scene, byte[] nodes, IReadOnlyDictionary<int, ImportedMesh> replacements, int nodeStride, CancellationToken token)
    {
        Queue<int> grown = [];
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            if (scene.Nodes[i].ModelIndex is not int model || !replacements.TryGetValue(model, out var mesh)) continue;
            int header = i * nodeStride; uint flags = U32(nodes, header + 36);
            if ((flags & 0x300) != 0x300) throw new InvalidDataException($"Node {i} has no valid cached/model bounds; replacement is not supported.");
            var box = mesh.Bounds; WriteBox(nodes, header + 140, box);
            if ((flags & 0x400) != 0) box = Union(box, ReadBox(nodes, header + 164));
            var old = ReadBox(nodes, header + 116);
            if (scene.Nodes[i].Parents.Any(p => IsWorld(scene, p)))
            {
                if (!Contains(old, box)) throw new InvalidDataException($"Replacement model {model} exceeds node {i}'s world-partition bounds; spatial re-partitioning is not supported.");
                continue; // Keep the stored grid envelope.
            }
            WriteBox(nodes, header + 116, box);
            if (!Contains(old, box)) grown.Enqueue(i);
        }
        for (int steps = 0; grown.TryDequeue(out int i); steps++)
        {
            token.ThrowIfCancellationRequested();
            if (steps > scene.Nodes.Count * 8) throw new InvalidDataException("Cyclic node bounds hierarchy.");
            var node = scene.Nodes[i]; var moved = Transform(ReadBox(nodes, i * nodeStride + 116), SceneBuilder.LocalTransform(node));
            foreach (int parent in node.Parents)
            {
                if (parent < 0 || parent >= scene.Nodes.Count) throw new InvalidDataException($"Node {i} has a missing parent.");
                int header = parent * nodeStride; var child = ReadBox(nodes, header + 164);
                if (Contains(child, moved)) continue;
                if (IsWorld(scene, parent)) throw new InvalidDataException($"Node {i} would exceed its world-partition bounds; spatial re-partitioning is not supported.");
                if ((U32(nodes, header + 36) & 0x500) != 0x500) throw new InvalidDataException($"Node {parent} has no valid child bounds to expand.");
                child = Union(child, moved); WriteBox(nodes, header + 164, child);
                var own = ReadBox(nodes, header + 116);
                if (!Contains(own, child)) { WriteBox(nodes, header + 116, Union(own, child)); grown.Enqueue(parent); }
            }
        }
    }
    private static bool IsWorld(GameScene scene, int index) => index >= 0 && index < scene.Nodes.Count && scene.Nodes[index].Class == "world";
    private static (Vector3 Min, Vector3 Max) ReadBox(byte[] bytes, int offset) => (ReadVector(bytes, offset), ReadVector(bytes, offset + 12));
    private static void WriteBox(byte[] bytes, int offset, (Vector3 Min, Vector3 Max) box) { Vector(bytes, offset, box.Min); Vector(bytes, offset + 12, box.Max); }
    private static (Vector3 Min, Vector3 Max) Union((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b) => (Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
    private static bool Contains((Vector3 Min, Vector3 Max) outer, (Vector3 Min, Vector3 Max) inner) =>
        outer.Min.X <= inner.Min.X && outer.Min.Y <= inner.Min.Y && outer.Min.Z <= inner.Min.Z && outer.Max.X >= inner.Max.X && outer.Max.Y >= inner.Max.Y && outer.Max.Z >= inner.Max.Z;
    private static (Vector3 Min, Vector3 Max) Transform((Vector3 Min, Vector3 Max) box, Matrix4x4 matrix)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            var p = Vector3.Transform(new((corner & 1) == 0 ? box.Min.X : box.Max.X, (corner & 2) == 0 ? box.Min.Y : box.Max.Y, (corner & 4) == 0 ? box.Min.Z : box.Max.Z), matrix);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        return (min, max);
    }
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void ValidatePool(byte[] pool, int count, int capacity)
    {
        HashSet<int> visited = [];
        Walk(I32(pool, 12), true, count); Walk(I32(pool, 8), false, capacity - count);
        void Walk(int index, bool active, int expected)
        {
            int previous = -1, length = 0;
            while (index != -1)
            {
                if (index < 0 || index >= capacity || !visited.Add(index) || (index < count) != active || BinaryPrimitives.ReadInt16LittleEndian(pool.AsSpan(16 + index * 44 + 40)) != previous)
                    throw new InvalidDataException("Material pool links are not a complete, contiguous active/free partition.");
                previous = index; index = BinaryPrimitives.ReadInt16LittleEndian(pool.AsSpan(16 + index * 44 + 42)); length++;
            }
            if (length != expected) throw new InvalidDataException("Material pool chain length does not match its count.");
        }
    }
    internal static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    internal static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset), checked((short)value));
    private static void Float(byte[] bytes, int offset, float value) => Put(bytes, offset, BitConverter.SingleToInt32Bits(value));
    private static void Vector(byte[] bytes, int offset, Vector3 v) { Float(bytes, offset, v.X); Float(bytes, offset + 4, v.Y); Float(bytes, offset + 8, v.Z); }
    private static Vector3 ReadVector(byte[] bytes, int offset) => new(BitConverter.ToSingle(bytes, offset), BitConverter.ToSingle(bytes, offset + 4), BitConverter.ToSingle(bytes, offset + 8));
}

public static partial class TexturePackWriter
{
    /// <summary>
    /// Retail zImage ReadHeader 0x46ED70 stores 16-bit sizes and allocates by pixel count; the
    /// software spans (texVShift cases 10..17) cover 512. CreateTextureRecord 0x4AA0F0 swaps a texture
    /// larger than the device's reported maximum (forced to 256 only when a driver reports 0) for the
    /// default texture, so 512 needs a device/wrapper that reports at least 512.
    /// </summary>
    public const int MaximumDimension = 512;
    public static byte[] Append(ZbdDocument source, string name, DecodedImage image, CancellationToken token = default)
    {
        ModelReplacementWriter.ValidateName(name);
        if (source.Probe.Family != FormatFamily.TexturePack || source.Probe.Version != 1 || source.Diagnostics.Any(d => d.Severity == "Error")) throw new InvalidDataException("An intact v1 texture pack is required.");
        if (source.Assets.Any(a => Path.GetFileNameWithoutExtension(a.Name).Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException($"Texture {name} already exists in {source.Path}.");
        if (image.Width < 1 || image.Width > MaximumDimension || image.Height < 1 || image.Height > MaximumDimension || !System.Numerics.BitOperations.IsPow2(image.Width) || !System.Numerics.BitOperations.IsPow2(image.Height) || image.Rgba.Length != image.Width * image.Height * 4)
            throw new InvalidDataException($"Game textures must be power-of-two RGB/RGBA images up to {MaximumDimension} × {MaximumDimension}.");
        if (Enumerable.Range(0, image.Width * image.Height).Any(i => image.Rgba[i * 4 + 3] != 255)) throw new InvalidDataException("Replacement solid meshes require an opaque diffuse texture.");
        int count = source.Assets.Count;
        var encoded = Encode(name, image, token: token);
        byte[] bytes = Write(new(source, new Dictionary<int, TexturePayload>(), [encoded.Payload]), token);
        var parsed = FormatRegistry.Default.OpenBytes(source.Path, bytes, token: token);
        if (parsed.Diagnostics.Any(d => d.Severity == "Error") || parsed.Assets.Count != count + 1) throw new InvalidDataException("Texture pack failed readback.");
        return bytes;
    }
}
