using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Gltf;

/// <summary>A glTF 2.0 node: a name, an optional mesh, children, a local transform and application extras.</summary>
public sealed class GltfNode
{
    public string Name { get; set; } = "";
    /// <summary>The node's index in the file it was read from (-1 for a node built in memory).</summary>
    public int Index { get; init; } = -1;
    public GltfMesh? Mesh { get; set; }
    public List<GltfNode> Children { get; } = [];
    /// <summary>Local transform in System.Numerics' row-vector convention (the order of glTF's column-major array); null is identity.</summary>
    public Matrix4x4? Matrix { get; set; }
    public JsonObject? Extras { get; set; }
    public override string ToString() => Name;
}

/// <summary>Triangles sharing one material. Vertex attributes are per vertex; morph targets hold position deltas.</summary>
public sealed class GltfPrimitive
{
    public GltfMaterial? Material { get; set; }
    public List<Vector3> Positions { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<Vector2> TexCoords { get; } = [];
    public List<int> Indices { get; } = [];
    public List<List<Vector3>> Targets { get; } = [];
    public JsonObject? Extras { get; set; }
}

public sealed class GltfMesh
{
    public string Name { get; set; } = "";
    public List<GltfPrimitive> Primitives { get; } = [];
    public List<float> Weights { get; } = [];
    public JsonObject? Extras { get; set; }
}

/// <summary>A material: base colour (with alpha), an optional base-colour texture with its wrap modes, and extras.</summary>
public sealed class GltfMaterial
{
    public string Name { get; set; } = "";
    public Vector4 BaseColor { get; set; } = Vector4.One;
    /// <summary>The texture image, as a URI relative to the glTF file.</summary>
    public string? ImageUri { get; set; }
    /// <summary>The base-colour texture's image is stored in the file (a buffer view or data URI) rather than as a file.</summary>
    public bool EmbeddedImage { get; set; }
    public bool ClampS { get; set; }
    public bool ClampT { get; set; }
    public bool DoubleSided { get; set; }
    public string AlphaMode { get; set; } = "OPAQUE";
    public JsonObject? Extras { get; set; }
}

/// <summary>
/// A glTF 2.0 asset reduced to what RECOIL models need: one scene of nodes with meshes, materials that reference PNG
/// images by URI, morph targets and extras. <see cref="Write"/> produces a .gltf JSON document and its .bin buffer;
/// <see cref="Read"/> accepts external buffers, data URIs and GLB, with the accessor types exporters use.
/// </summary>
public sealed class GltfDocument
{
    public const int MaximumNodes = 200_000, MaximumElements = 16_000_000;
    /// <summary>Components one read may decode in all (accessors can be shared, so each use counts).</summary>
    public const long MaximumDecodedElements = 4L * MaximumElements;
    /// <summary>The deepest node hierarchy a file may hold; the model loaders follow hierarchies recursively.</summary>
    public const int MaximumDepth = 256;
    /// <summary>The buffers a file may list; each external one is a file the read opens.</summary>
    public const int MaximumBuffers = 4096;
    /// <summary>
    /// The bytes a file's buffers may hold together (external files, data URIs and the GLB chunk), the limit one project file
    /// has: every buffer is held until the views are read, so many files that each fit would otherwise add up without bound.
    /// </summary>
    public const long MaximumBufferBytes = Formats.FormatRegistry.MaximumDocumentBytes;
    public List<GltfNode> Roots { get; } = [];
    public JsonObject? SceneExtras { get; set; }
    public string Generator { get; set; } = "zStudio";

    public IEnumerable<GltfNode> AllNodes() { foreach (var root in Roots) foreach (var n in Walk(root)) yield return n; }
    private static IEnumerable<GltfNode> Walk(GltfNode node) { yield return node; foreach (var c in node.Children) foreach (var n in Walk(c)) yield return n; }

    /// <summary>Serializes to glTF JSON and a binary buffer; <paramref name="binaryUri"/> is the buffer's file name next to the .gltf.</summary>
    public (byte[] Json, byte[] Binary) Write(string binaryUri)
    {
        using MemoryStream bin = new();
        JsonArray nodes = [], meshes = [], materials = [], textures = [], images = [], samplers = [], accessors = [], views = [];
        Dictionary<GltfNode, int> nodeIndex = new(ReferenceEqualityComparer.Instance);
        Dictionary<GltfMesh, int> meshIndex = new(ReferenceEqualityComparer.Instance);
        Dictionary<GltfMaterial, int> materialIndex = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, int> imageIndex = new(StringComparer.Ordinal);
        Dictionary<(bool, bool), int> samplerIndex = [];
        List<GltfNode> ordered = [];
        foreach (var n in AllNodes()) if (nodeIndex.TryAdd(n, ordered.Count)) ordered.Add(n);
        if (ordered.Count > MaximumNodes) throw new InvalidDataException($"A glTF file holds at most {MaximumNodes:N0} nodes.");

        int View(ReadOnlySpan<byte> data, int? target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            JsonObject view = new() { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length };
            if (target is { } t) view["target"] = t;
            bin.Write(data); views.Add(view); return views.Count - 1;
        }
        int Floats(IReadOnlyList<float> values, int components, string type, bool bounds)
        {
            byte[] data = new byte[values.Count * 4];
            for (int i = 0; i < values.Count; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), values[i]);
            JsonObject accessor = new() { ["bufferView"] = View(data, 34962), ["componentType"] = 5126, ["count"] = values.Count / components, ["type"] = type };
            if (bounds && values.Count > 0)
            {
                JsonArray min = [], max = [];
                for (int c = 0; c < components; c++)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int i = c; i < values.Count; i += components) { lo = Math.Min(lo, values[i]); hi = Math.Max(hi, values[i]); }
                    min.Add(lo); max.Add(hi);
                }
                accessor["min"] = min; accessor["max"] = max;
            }
            accessors.Add(accessor); return accessors.Count - 1;
        }
        int Vectors(IEnumerable<Vector3> values, bool bounds) => Floats(values.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(), 3, "VEC3", bounds);
        int Material(GltfMaterial m)
        {
            if (materialIndex.TryGetValue(m, out int index)) return index;
            JsonObject pbr = new() { ["metallicFactor"] = 0.0, ["roughnessFactor"] = 1.0 };
            if (m.BaseColor != Vector4.One) pbr["baseColorFactor"] = new JsonArray(m.BaseColor.X, m.BaseColor.Y, m.BaseColor.Z, m.BaseColor.W);
            if (m.ImageUri != null)
            {
                if (!imageIndex.TryGetValue(m.ImageUri, out int image)) { images.Add(new JsonObject { ["uri"] = Uri.EscapeDataString(m.ImageUri).Replace("%2F", "/") }); imageIndex[m.ImageUri] = image = images.Count - 1; }
                if (!samplerIndex.TryGetValue((m.ClampS, m.ClampT), out int sampler))
                {
                    samplers.Add(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = m.ClampS ? 33071 : 10497, ["wrapT"] = m.ClampT ? 33071 : 10497 });
                    samplerIndex[(m.ClampS, m.ClampT)] = sampler = samplers.Count - 1;
                }
                textures.Add(new JsonObject { ["source"] = image, ["sampler"] = sampler });
                pbr["baseColorTexture"] = new JsonObject { ["index"] = textures.Count - 1 };
            }
            JsonObject material = new() { ["name"] = m.Name, ["pbrMetallicRoughness"] = pbr };
            if (m.DoubleSided) material["doubleSided"] = true;
            if (m.AlphaMode != "OPAQUE") material["alphaMode"] = m.AlphaMode;
            if (m.Extras != null) material["extras"] = m.Extras.DeepClone();
            materials.Add(material); materialIndex[m] = materials.Count - 1; return materials.Count - 1;
        }
        int Mesh(GltfMesh mesh)
        {
            if (meshIndex.TryGetValue(mesh, out int index)) return index;
            JsonArray primitives = [];
            foreach (var p in mesh.Primitives)
            {
                if (p.Positions.Count == 0 || p.Indices.Count == 0) continue;
                JsonObject attributes = new() { ["POSITION"] = Vectors(p.Positions, true) };
                if (p.Normals.Count == p.Positions.Count) attributes["NORMAL"] = Vectors(p.Normals, false);
                if (p.TexCoords.Count == p.Positions.Count) attributes["TEXCOORD_0"] = Floats(p.TexCoords.SelectMany(t => new[] { t.X, t.Y }).ToArray(), 2, "VEC2", false);
                bool wide = p.Positions.Count > 65535; byte[] data = new byte[p.Indices.Count * (wide ? 4 : 2)];
                for (int i = 0; i < p.Indices.Count; i++) if (wide) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), (uint)p.Indices[i]); else BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), (ushort)p.Indices[i]);
                accessors.Add(new JsonObject { ["bufferView"] = View(data, 34963), ["componentType"] = wide ? 5125 : 5123, ["count"] = p.Indices.Count, ["type"] = "SCALAR" });
                JsonObject primitive = new() { ["attributes"] = attributes, ["indices"] = accessors.Count - 1, ["mode"] = 4 };
                if (p.Material != null) primitive["material"] = Material(p.Material);
                if (p.Targets.Count > 0) primitive["targets"] = new JsonArray(p.Targets.Select(t => (JsonNode)new JsonObject { ["POSITION"] = Vectors(t, true) }).ToArray());
                if (p.Extras != null) primitive["extras"] = p.Extras.DeepClone();
                primitives.Add(primitive);
            }
            JsonObject json = new() { ["name"] = mesh.Name, ["primitives"] = primitives };
            if (mesh.Weights.Count > 0) json["weights"] = new JsonArray(mesh.Weights.Select(w => (JsonNode)w).ToArray());
            if (mesh.Extras != null) json["extras"] = mesh.Extras.DeepClone();
            meshes.Add(json); meshIndex[mesh] = meshes.Count - 1; return meshes.Count - 1;
        }
        foreach (var n in ordered)
        {
            JsonObject json = new() { ["name"] = n.Name };
            if (n.Mesh != null) json["mesh"] = Mesh(n.Mesh);
            if (n.Children.Count > 0) json["children"] = new JsonArray(n.Children.Select(c => (JsonNode)nodeIndex[c]).ToArray());
            if (n.Matrix is { } m && !m.IsIdentity)
                json["matrix"] = new JsonArray(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
            if (n.Extras != null) json["extras"] = n.Extras.DeepClone();
            nodes.Add(json);
        }
        JsonObject scene = new() { ["nodes"] = new JsonArray(Roots.Select(r => (JsonNode)nodeIndex[r]).ToArray()) };
        if (SceneExtras != null) scene["extras"] = SceneExtras.DeepClone();
        JsonObject root = new()
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = Generator },
            ["scene"] = 0, ["scenes"] = new JsonArray(scene), ["nodes"] = nodes,
        };
        if (meshes.Count > 0) root["meshes"] = meshes;
        if (materials.Count > 0) root["materials"] = materials;
        if (textures.Count > 0) { root["textures"] = textures; root["images"] = images; root["samplers"] = samplers; }
        if (accessors.Count > 0)
        {
            root["accessors"] = accessors; root["bufferViews"] = views;
            root["buffers"] = new JsonArray(new JsonObject { ["uri"] = Uri.EscapeDataString(binaryUri), ["byteLength"] = bin.Length });
        }
        byte[] text = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (text, bin.ToArray());
    }

    /// <summary>
    /// Reads a .gltf (JSON) or .glb document. <paramref name="resolve"/> returns the bytes of an external buffer URI
    /// (relative to the file). Triangle lists, strips and fans are accepted; points and lines are ignored. A malformed
    /// file is reported as <see cref="InvalidDataException"/>, whatever part of it is wrong.
    /// </summary>
    public static GltfDocument Read(ReadOnlySpan<byte> bytes, Func<string, byte[]> resolve, CancellationToken token = default) => Read(bytes, resolve, MaximumBufferBytes, token);
    /// <summary>Reads as <see cref="Read(ReadOnlySpan{byte}, Func{string, byte[]}, CancellationToken)"/>, with <paramref name="bufferBytes"/> in place of <see cref="MaximumBufferBytes"/>.</summary>
    internal static GltfDocument Read(ReadOnlySpan<byte> bytes, Func<string, byte[]> resolve, long bufferBytes, CancellationToken token)
    {
        try { return ReadDocument(bytes, resolve, bufferBytes, token); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException
            or NullReferenceException or IndexOutOfRangeException or KeyNotFoundException or InvalidCastException)
        { throw new InvalidDataException($"The glTF file is malformed: {ex.Message}", ex); }
    }

    private static GltfDocument ReadDocument(ReadOnlySpan<byte> bytes, Func<string, byte[]> resolve, long bufferBytes, CancellationToken token)
    {
        byte[]? glbBinary = null; ReadOnlySpan<byte> jsonBytes = bytes;
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("glTF"u8))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2) throw new InvalidDataException("Only glTF 2.0 is supported.");
            int offset = 12; jsonBytes = default;
            while (offset + 8 <= bytes.Length)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]); uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
                if (length < 0 || offset + 8 + (long)length > bytes.Length) throw new InvalidDataException("Truncated GLB chunk.");
                if (type == 0x4E4F534A) jsonBytes = bytes.Slice(offset + 8, length); else if (type == 0x004E4942) glbBinary = bytes.Slice(offset + 8, length).ToArray();
                offset += 8 + length;
            }
            if (jsonBytes.IsEmpty) throw new InvalidDataException("GLB without JSON.");
        }
        JsonObject root = JsonNode.Parse(jsonBytes, documentOptions: new() { MaxDepth = 64 }) as JsonObject ?? throw new InvalidDataException("glTF JSON must be an object.");
        if (root["asset"]?["version"]?.GetValue<string>() is not { } version || !version.StartsWith('2')) throw new InvalidDataException("Only glTF 2.0 is supported.");
        foreach (var required in root["extensionsRequired"] as JsonArray ?? [])
            throw new InvalidDataException($"The glTF file requires extension {required}, which zStudio does not support.");
        // Every buffer is held until the views are read, so together they are bounded like one file: a URI listed again is the
        // bytes already read, a buffer's declared length is checked against what is left before its file is read, and what
        // each read returns counts once.
        var jsonBuffers = root["buffers"] as JsonArray ?? [];
        if (jsonBuffers.Count > MaximumBuffers) throw new InvalidDataException($"The glTF file lists {jsonBuffers.Count:N0} buffers; a model may use at most {MaximumBuffers:N0}.");
        List<byte[]> buffers = [];
        Dictionary<string, byte[]> external = new(StringComparer.Ordinal);
        HashSet<byte[]> held = new(ReferenceEqualityComparer.Instance); long heldBytes = 0;
        InvalidDataException TooLarge() => new($"The glTF file's buffers hold more than {(bufferBytes % (1024 * 1024) == 0 ? $"{bufferBytes / (1024 * 1024):N0} MiB" : $"{bufferBytes:N0} bytes")} together, more than a model may; remove buffers it does not use, or split it into several files.");
        foreach (var buffer in jsonBuffers)
        {
            token.ThrowIfCancellationRequested();
            if (buffer is not JsonObject entry) throw new InvalidDataException("A glTF buffer is not an object.");
            string? uri = entry["uri"]?.GetValue<string>();
            long? declared = entry["byteLength"]?.GetValue<long>();
            if (declared < 0) throw new InvalidDataException("A glTF buffer has a negative length.");
            byte[] data;
            if (uri == null) data = glbBinary ?? throw new InvalidDataException("A buffer has no data.");
            else if (uri.StartsWith("data:", StringComparison.Ordinal))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0) throw new InvalidDataException("A glTF data URI has no data.");
                if ((long)(uri.Length - comma - 1) / 4 * 3 > bufferBytes - heldBytes) throw TooLarge();
                data = Convert.FromBase64String(uri[(comma + 1)..]);
            }
            else
            {
                string name = Uri.UnescapeDataString(uri);
                if (!external.TryGetValue(name, out data!))
                {
                    if (declared > bufferBytes - heldBytes) throw TooLarge();
                    external[name] = data = resolve(name);
                }
            }
            if (held.Add(data) && (heldBytes += data.Length) > bufferBytes) throw TooLarge();
            if (data.Length < (declared ?? 0)) throw new InvalidDataException("A glTF buffer is shorter than declared.");
            buffers.Add(data);
        }
        JsonArray views = root["bufferViews"] as JsonArray ?? [], accessors = root["accessors"] as JsonArray ?? [];
        long decoded = 0;
        static int Size(int componentType) => componentType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException($"Unsupported component type {componentType}.") };
        static float Component(ReadOnlySpan<byte> span, int componentType, bool normalized) => componentType switch
        {
            5126 => BinaryPrimitives.ReadSingleLittleEndian(span),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(span),
            5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(span) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(span),
            5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(span) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(span),
            5121 => normalized ? span[0] / 255f : span[0],
            _ => normalized ? Math.Max((sbyte)span[0] / 127f, -1) : (sbyte)span[0],
        };
        // A buffer view's bytes from an extra offset: its buffer and where the data starts, checked to hold byteCount bytes.
        (byte[] Data, long Start) View(JsonNode? reference, long offset, long byteCount)
        {
            var view = views[reference!.GetValue<int>()]!; var data = buffers[view["buffer"]!.GetValue<int>()];
            long start = (view["byteOffset"]?.GetValue<long>() ?? 0) + offset;
            if (start < 0 || offset < 0 || start + byteCount > data.Length) throw new InvalidDataException("A glTF accessor exceeds its buffer.");
            return (data, start);
        }
        float[] Accessor(int index, out int components)
        {
            var a = accessors[index] ?? throw new InvalidDataException("Missing accessor.");
            components = a["type"]!.GetValue<string>() switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, var t => throw new InvalidDataException($"Unsupported accessor type {t}.") };
            int count = a["count"]!.GetValue<int>(), componentType = a["componentType"]!.GetValue<int>(); bool normalized = a["normalized"]?.GetValue<bool>() ?? false;
            if (count < 0 || (long)count * components > MaximumElements) throw new InvalidDataException("A glTF accessor is too large.");
            // Accessors may be shared by any number of primitives; every use is decoded, so the file's total is bounded.
            if ((decoded += (long)count * components) > MaximumDecodedElements) throw new InvalidDataException("The glTF file decodes to more data than a model can hold.");
            int size = Size(componentType), element = size * components;
            float[] values = new float[count * components];
            if (a["bufferView"] is { } reference && count > 0)
            {
                // Elements are tightly packed unless the view has a stride, which must hold an element (glTF: 4 to 252 bytes).
                int stride = views[reference.GetValue<int>()]!["byteStride"]?.GetValue<int>() ?? element;
                if (stride < element || stride > 252 && stride != element) throw new InvalidDataException($"A glTF buffer view has an invalid byte stride of {stride}.");
                var (data, start) = View(reference, a["byteOffset"]?.GetValue<long>() ?? 0, (long)(count - 1) * stride + element);
                for (int i = 0; i < count; i++)
                    for (int c = 0; c < components; c++) values[i * components + c] = Component(data.AsSpan((int)(start + (long)i * stride + c * size)), componentType, normalized);
            }
            // Sparse accessors replace some elements (Blender writes morph targets this way, often without a buffer view).
            if (a["sparse"] is { } sparse)
            {
                int n = sparse["count"]!.GetValue<int>();
                if (n < 1 || n > count) throw new InvalidDataException("A sparse glTF accessor has an invalid count.");
                var indices = sparse["indices"]!; int indexType = indices["componentType"]!.GetValue<int>();
                if (indexType is not (5121 or 5123 or 5125)) throw new InvalidDataException($"Sparse glTF indices cannot use component type {indexType}.");
                int indexSize = Size(indexType);
                var (indexData, indexStart) = View(indices["bufferView"], indices["byteOffset"]?.GetValue<long>() ?? 0, (long)n * indexSize);
                var (valueData, valueStart) = View(sparse["values"]!["bufferView"], sparse["values"]!["byteOffset"]?.GetValue<long>() ?? 0, (long)n * element);
                for (int k = 0; k < n; k++)
                {
                    var at = indexData.AsSpan((int)(indexStart + (long)k * indexSize));
                    long target = indexType switch { 5121 => at[0], 5123 => BinaryPrimitives.ReadUInt16LittleEndian(at), _ => BinaryPrimitives.ReadUInt32LittleEndian(at) };
                    if (target >= count) throw new InvalidDataException("A sparse glTF index is out of range.");
                    for (int c = 0; c < components; c++) values[target * components + c] = Component(valueData.AsSpan((int)(valueStart + ((long)k * components + c) * size)), componentType, normalized);
                }
            }
            return values;
        }
        List<Vector3> Vec3(int index) { var v = Accessor(index, out int c); if (c != 3) throw new InvalidDataException("Expected a VEC3 accessor."); return Enumerable.Range(0, v.Length / 3).Select(i => new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2])).ToList(); }

        JsonArray jsonImages = root["images"] as JsonArray ?? [], jsonTextures = root["textures"] as JsonArray ?? [], jsonSamplers = root["samplers"] as JsonArray ?? [];
        List<GltfMaterial> materials = [];
        foreach (var m in root["materials"] as JsonArray ?? [])
        {
            GltfMaterial material = new() { Name = m?["name"]?.GetValue<string>() ?? "", DoubleSided = m?["doubleSided"]?.GetValue<bool>() ?? false, AlphaMode = m?["alphaMode"]?.GetValue<string>() ?? "OPAQUE", Extras = m?["extras"]?.DeepClone() as JsonObject };
            var pbr = m?["pbrMetallicRoughness"];
            if (pbr?["baseColorFactor"] is { } factor)
            {
                if (!TryNumbers(factor, 4, out var c)) throw new InvalidDataException($"glTF material {materials.Count} has a base colour that is not 4 finite numbers.");
                material.BaseColor = new(c[0], c[1], c[2], c[3]);
            }
            if (pbr?["baseColorTexture"]?["index"] is { } textureIndex)
            {
                var texture = jsonTextures[Reference(textureIndex, jsonTextures.Count, "texture")]!;
                if (texture["source"] is { } source)
                {
                    int image = Reference(source, jsonImages.Count, "image");
                    if (jsonImages[image]?["uri"]?.GetValue<string>() is { } uri && !uri.StartsWith("data:", StringComparison.Ordinal)) material.ImageUri = Uri.UnescapeDataString(uri);
                    else material.EmbeddedImage = true;
                }
                if (texture["sampler"] is { } sampler)
                {
                    int s = Reference(sampler, jsonSamplers.Count, "sampler");
                    material.ClampS = jsonSamplers[s]?["wrapS"]?.GetValue<int>() == 33071; material.ClampT = jsonSamplers[s]?["wrapT"]?.GetValue<int>() == 33071;
                }
            }
            materials.Add(material);
        }
        List<GltfMesh> meshes = [];
        foreach (var m in root["meshes"] as JsonArray ?? [])
        {
            token.ThrowIfCancellationRequested();
            GltfMesh mesh = new() { Name = m?["name"]?.GetValue<string>() ?? "", Extras = m?["extras"]?.DeepClone() as JsonObject };
            foreach (var w in m?["weights"] as JsonArray ?? []) mesh.Weights.Add(w!.GetValue<float>());
            foreach (var p in m?["primitives"] as JsonArray ?? [])
            {
                int mode = p?["mode"]?.GetValue<int>() ?? 4;
                if (mode is not (4 or 5 or 6) || p?["attributes"]?["POSITION"] is null) continue;
                GltfPrimitive primitive = new() { Extras = p["extras"]?.DeepClone() as JsonObject };
                if (p["material"] is { } material) primitive.Material = materials[Reference(material, materials.Count, "material")];
                var attributes = p["attributes"]!;
                primitive.Positions.AddRange(Vec3(attributes["POSITION"]!.GetValue<int>()));
                if (attributes["NORMAL"] is { } normal) primitive.Normals.AddRange(Vec3(normal.GetValue<int>()));
                if (attributes["TEXCOORD_0"] is { } uv)
                {
                    var values = Accessor(uv.GetValue<int>(), out int c); if (c != 2) throw new InvalidDataException("Expected VEC2 texture coordinates.");
                    for (int i = 0; i < values.Length / 2; i++) primitive.TexCoords.Add(new(values[i * 2], values[i * 2 + 1]));
                }
                foreach (var target in p["targets"] as JsonArray ?? [])
                    primitive.Targets.Add(target?["POSITION"] is { } delta ? Vec3(delta.GetValue<int>()) : Enumerable.Repeat(Vector3.Zero, primitive.Positions.Count).ToList());
                int[] indices = p["indices"] is { } ix ? Accessor(ix.GetValue<int>(), out _).Select(v => (int)v).ToArray() : Enumerable.Range(0, primitive.Positions.Count).ToArray();
                if (indices.Any(i => i < 0 || i >= primitive.Positions.Count)) throw new InvalidDataException("A glTF index is out of range.");
                if (mode == 4) primitive.Indices.AddRange(indices.Take(indices.Length / 3 * 3));
                else for (int i = 2; i < indices.Length; i++)
                    {
                        if (mode == 6) primitive.Indices.AddRange([indices[0], indices[i - 1], indices[i]]);
                        else primitive.Indices.AddRange(i % 2 == 0 ? [indices[i - 2], indices[i - 1], indices[i]] : [indices[i - 1], indices[i - 2], indices[i]]);
                    }
                mesh.Primitives.Add(primitive);
            }
            meshes.Add(mesh);
        }
        JsonArray jsonNodes = root["nodes"] as JsonArray ?? [];
        if (jsonNodes.Count > MaximumNodes) throw new InvalidDataException($"A glTF file holds at most {MaximumNodes:N0} nodes.");
        var nodes = jsonNodes.Select((n, i) => new GltfNode { Name = n?["name"]?.GetValue<string>() ?? "", Index = i, Extras = n?["extras"]?.DeepClone() as JsonObject }).ToArray();
        for (int i = 0; i < jsonNodes.Count; i++)
        {
            var n = jsonNodes[i] as JsonObject ?? throw new InvalidDataException($"glTF node {i} is not an object."); var node = nodes[i];
            if (n["mesh"] is { } mesh) node.Mesh = meshes[Reference(mesh, meshes.Count, "mesh")];
            foreach (var c in n["children"] as JsonArray ?? [])
            {
                int child = c!.GetValue<int>(); if (child < 0 || child >= nodes.Length || child == i) throw new InvalidDataException("A glTF node has an invalid child.");
                node.Children.Add(nodes[child]);
            }
            node.Matrix = LocalTransform(n, i);
        }
        GltfDocument doc = new() { Generator = root["asset"]?["generator"]?.GetValue<string>() ?? "" };
        var scenes = root["scenes"] as JsonArray ?? [];
        int sceneIndex = root["scene"] is { } chosen ? Reference(chosen, scenes.Count, "scene") : 0;
        if (scenes.Count > 0)
        {
            var scene = scenes[sceneIndex]!;
            doc.SceneExtras = scene["extras"]?.DeepClone() as JsonObject;
            foreach (var r in scene["nodes"] as JsonArray ?? []) doc.Roots.Add(nodes[Reference(r, nodes.Length, "node")]);
        }
        else
        {
            // Without scenes, every node that is nobody's child is a root.
            var children = nodes.SelectMany(n => n.Children).ToHashSet(ReferenceEqualityComparer.Instance);
            doc.Roots.AddRange(nodes.Where(n => !children.Contains(n)));
        }
        // Reject cycles and hierarchies too deep to follow before anyone walks them; the walk itself keeps its own stack.
        HashSet<GltfNode> visiting = new(ReferenceEqualityComparer.Instance), done = new(ReferenceEqualityComparer.Instance);
        Stack<(GltfNode Node, int Next)> path = new();
        foreach (var r in doc.Roots)
        {
            if (done.Contains(r)) continue;
            path.Push((r, 0)); visiting.Add(r);
            while (path.Count > 0)
            {
                var (node, next) = path.Pop();
                if (next == node.Children.Count) { visiting.Remove(node); done.Add(node); continue; }
                path.Push((node, next + 1));
                var child = node.Children[next];
                if (done.Contains(child)) continue;
                if (!visiting.Add(child)) throw new InvalidDataException("The glTF node hierarchy has a cycle.");
                if (path.Count >= MaximumDepth) throw new InvalidDataException($"The glTF node hierarchy is deeper than {MaximumDepth} levels.");
                path.Push((child, 0));
            }
        }
        return doc;
    }

    /// <summary>
    /// A node's local transform as glTF states it, in System.Numerics' row-vector convention: its <c>matrix</c> (16 numbers,
    /// column-major), or its <c>translation</c> (3 numbers), <c>rotation</c> (a quaternion, 4) and <c>scale</c> (3) as
    /// scale · rotation · translation; null when it states none (identity). A property of another shape, a value that is not
    /// a finite number, or a matrix beside translation, rotation or scale (glTF allows one or the other) is refused rather
    /// than read as identity. <paramref name="index"/> (the node's index, when known) names it in the message.
    /// </summary>
    public static Matrix4x4? LocalTransform(JsonObject node, int index = -1)
    {
        JsonNode? matrix = node["matrix"], translation = node["translation"], rotation = node["rotation"], scale = node["scale"];
        if (matrix != null)
        {
            if (translation != null || rotation != null || scale != null) throw Malformed("has both a matrix and translation, rotation or scale; glTF allows one or the other");
            float[] m = Numbers(matrix, 16, "matrix");
            return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }
        if (translation == null && rotation == null && scale == null) return null;
        Vector3 t = translation == null ? Vector3.Zero : new(Numbers(translation, 3, "translation"));
        Quaternion r = Quaternion.Identity;
        if (rotation != null) { float[] q = Numbers(rotation, 4, "rotation"); r = new(q[0], q[1], q[2], q[3]); }
        Vector3 s = scale == null ? Vector3.One : new(Numbers(scale, 3, "scale"));
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);

        float[] Numbers(JsonNode value, int count, string property)
        {
            if (value is not JsonArray array || array.Count != count)
                throw Malformed($"has a {property} that is {(value is JsonArray other ? $"a list of {other.Count:N0} values" : "not a list")}; glTF needs {count} numbers");
            float[] numbers = new float[count];
            for (int k = 0; k < count; k++)
                if (!TryNumber(array[k], out numbers[k]) || !float.IsFinite(numbers[k])) throw Malformed($"has a {property} whose value {k + 1} is not a finite number");
            return numbers;
        }
        InvalidDataException Malformed(string problem)
        {
            // The name is authored text of any length: the message shows a bounded part of it.
            string name = node["name"] is JsonValue v && v.TryGetValue(out string? text) ? text : "";
            if (name.Length > 64) name = name[..64] + "…";
            return new($"glTF node {(index >= 0 ? $"{index} " : "")}{(name.Length > 0 ? $"({name}) " : "")}{problem}.");
        }
    }
    /// <summary>
    /// A JSON number as a float: read as written (as a reader parses it) or, for a value built in memory, converted from the
    /// type it holds; false for anything else.
    /// </summary>
    private static bool TryNumber(JsonNode? node, out float value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue(out float f)) value = f;
        else if (v.TryGetValue(out double d)) value = (float)d;
        else if (v.TryGetValue(out int i)) value = i;
        else if (v.TryGetValue(out long l)) value = l;
        else if (v.TryGetValue(out decimal m)) value = (float)m;
        else return false;
        return true;
    }
    /// <summary>A list of exactly <paramref name="count"/> finite numbers.</summary>
    private static bool TryNumbers(JsonNode value, int count, out float[] numbers)
    {
        numbers = new float[count];
        if (value is not JsonArray array || array.Count != count) return false;
        for (int k = 0; k < count; k++) if (!TryNumber(array[k], out numbers[k]) || !float.IsFinite(numbers[k])) return false;
        return true;
    }
    /// <summary>An index into one of the file's lists (meshes, materials, scenes…); one that names no entry is refused rather than dropped.</summary>
    private static int Reference(JsonNode? value, int count, string list)
    {
        int index = value is JsonValue v && v.TryGetValue(out int i) ? i : throw new InvalidDataException($"A glTF {list} reference is not a whole number.");
        if (index < 0 || index >= count) throw new InvalidDataException($"A glTF {list} reference names {list} {index}, but the file has {count:N0}.");
        return index;
    }
}
