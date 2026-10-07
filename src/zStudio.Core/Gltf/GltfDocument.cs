using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
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
    /// <summary>Per-node morph weights overriding the mesh's defaults; empty means inherit.</summary>
    public List<float> Weights { get; } = [];
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
    /// <summary>The source UV set selected by the material, retained for missing-coordinate diagnostics.</summary>
    internal int TextureCoordinateSet { get; set; }
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
    /// <summary>RECOIL-authored materials are nonmetallic; a parsed implicit glTF material has factor 1.</summary>
    public float MetallicFactor { get; set; }
    /// <summary>The texture image, as a URI relative to the glTF file.</summary>
    public string? ImageUri { get; set; }
    /// <summary>The base-colour texture's image is stored in the file (a buffer view or data URI) rather than as a file.</summary>
    public bool EmbeddedImage { get; set; }
    public bool ClampS { get; set; }
    public bool ClampT { get; set; }
    public bool DoubleSided { get; set; }
    public string AlphaMode { get; set; } = "OPAQUE";
    public float AlphaCutoff { get; set; } = 0.5f;
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
    /// <summary>
    /// The entries a file's accessors and buffer views may hold (its primitives, morph targets and weights: see
    /// <see cref="MaximumPrimitives"/>). Its meshes, materials, images, textures, samplers and scenes may
    /// each hold as many as <see cref="MaximumNodes"/>. The reader makes an object of every entry of a list it reads or
    /// indexes, used or not, so the lists are bounded before any entry is read.
    /// </summary>
    public const int MaximumEntries = 1_000_000;
    /// <summary>
    /// The primitives (all meshes together), morph targets (all primitives together) and weights (all meshes together) a file
    /// may hold: each primitive becomes an object with its attributes, so they are bounded like nodes (the 1999 release's
    /// files hold about 20,000 primitives in all).
    /// </summary>
    public const int MaximumPrimitives = 200_000;
    /// <summary>
    /// The bytes (as the JSON writes them) of the names, extras and image paths a file may hold together: the reader keeps a
    /// copy of each, also of entries nothing uses, and its callers read the extras again.
    /// </summary>
    public const long MaximumMetadataBytes = 32L * 1024 * 1024;
    /// <summary>Complete JSON, including unknown properties and inline data, bounded before either DOM is allocated.</summary>
    public const int MaximumJsonBytes = 32 * 1024 * 1024;
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
        GltfMaterial neutralMaterial = new(); // An in-memory RECOIL primitive without a material is neutral, not glTF's default metal.
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
            JsonObject pbr = new() { ["metallicFactor"] = m.MetallicFactor, ["roughnessFactor"] = 1.0 };
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
            if (m.AlphaMode == "MASK" && m.AlphaCutoff != 0.5f) material["alphaCutoff"] = m.AlphaCutoff;
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
                primitive["material"] = Material(p.Material ?? neutralMaterial);
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
            if (n.Weights.Count > 0) json["weights"] = new JsonArray(n.Weights.Select(w => (JsonNode)w).ToArray());
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
    public static GltfDocument Read(ReadOnlySpan<byte> bytes, Func<string, byte[]> resolve, CancellationToken token = default) => Read(bytes, resolve, ReadLimits.Default, token);
    /// <summary>Reads as <see cref="Read(ReadOnlySpan{byte}, Func{string, byte[]}, CancellationToken)"/>, with <paramref name="bufferBytes"/> in place of <see cref="MaximumBufferBytes"/>.</summary>
    internal static GltfDocument Read(ReadOnlySpan<byte> bytes, Func<string, byte[]> resolve, long bufferBytes, CancellationToken token) =>
        Read(bytes, resolve, ReadLimits.Default with { BufferBytes = bufferBytes }, token);
    /// <summary>Reads as <see cref="Read(ReadOnlySpan{byte}, Func{string, byte[]}, CancellationToken)"/>, with other limits in place of the maximums.</summary>
    internal static GltfDocument Read(ReadOnlySpan<byte> bytes, Func<string, byte[]> resolve, ReadLimits limits, CancellationToken token)
    {
        try { return ReadDocument(bytes, resolve, limits, token); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException
            or NullReferenceException or IndexOutOfRangeException or KeyNotFoundException or InvalidCastException)
        { throw new InvalidDataException($"The glTF file is malformed: {ex.Message}", ex); }
    }

    /// <summary>What one read may hold: <see cref="MaximumBufferBytes"/>, <see cref="MaximumMetadataBytes"/> and <see cref="MaximumDecodedElements"/>.</summary>
    internal readonly record struct ReadLimits(long BufferBytes, long MetadataBytes, long DecodedElements)
    {
        public static ReadLimits Default => new(MaximumBufferBytes, MaximumMetadataBytes, MaximumDecodedElements);
    }

    /// <summary>What each use of an accessor reads.</summary>
    private enum AccessorUse { Positions, Normals, TextureCoordinates, Indices, TargetPositions }

    private const string MeshQuantization = "KHR_mesh_quantization", TextureTransform = "KHR_texture_transform";

    /// <summary>Whether the file's list of extensions (extensionsUsed or extensionsRequired) names KHR_mesh_quantization.</summary>
    private static bool Declares(JsonElement root, string list)
    {
        if (!root.TryGetProperty(list, out var names) || names.ValueKind != JsonValueKind.Array) return false;
        foreach (var name in names.EnumerateArray()) if (name.ValueKind == JsonValueKind.String && name.ValueEquals(MeshQuantization)) return true;
        return false;
    }

    private static GltfDocument ReadDocument(ReadOnlySpan<byte> bytes, Func<string, byte[]> resolve, ReadLimits limits, CancellationToken token)
    {
        long bufferBytes = limits.BufferBytes;
        ReadOnlySpan<byte> glbBinary = default, jsonBytes = bytes;
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("glTF"u8))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2) throw new InvalidDataException("Only glTF 2.0 is supported.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length) throw new InvalidDataException("GLB total length does not match the file.");
            int offset = 12, chunk = 0; bool hasJson = false, hasBin = false; jsonBytes = default;
            while (offset < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                if (bytes.Length - offset < 8) throw new InvalidDataException("Truncated GLB chunk header.");
                int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]); uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
                if (length < 0 || offset + 8 + (long)length > bytes.Length) throw new InvalidDataException("Truncated GLB chunk.");
                if (length % 4 != 0) throw new InvalidDataException("GLB chunks must be aligned to four bytes.");
                if (type == 0x4E4F534A)
                {
                    if (chunk != 0 || hasJson) throw new InvalidDataException("GLB must have exactly one JSON chunk, first.");
                    hasJson = true; jsonBytes = bytes.Slice(offset + 8, length);
                }
                else if (type == 0x004E4942)
                {
                    if (chunk != 1 || hasBin) throw new InvalidDataException("GLB may have one BIN chunk, second.");
                    hasBin = true; glbBinary = bytes.Slice(offset + 8, length);
                }
                else if (chunk == 0) throw new InvalidDataException("GLB must start with its JSON chunk.");
                chunk++;
                offset += 8 + length;
            }
            if (jsonBytes.IsEmpty) throw new InvalidDataException("GLB without JSON.");
        }
        ValidateJsonText(jsonBytes, token);
        JsonElement parsed = JsonElement.Parse(jsonBytes, new JsonDocumentOptions { MaxDepth = 64 });
        if (parsed.ValueKind != JsonValueKind.Object) throw new InvalidDataException("glTF JSON must be an object.");
        Bound(parsed, limits.MetadataBytes);
        JsonObject root = JsonObject.Create(parsed)!;
        if (!parsed.TryGetProperty("asset", out var asset) || asset.ValueKind != JsonValueKind.Object
            || !asset.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || !version.ValueEquals("2.0"))
            throw new InvalidDataException("The glTF asset.version must be '2.0'; other or malformed versions are not supported.");
        if (asset.TryGetProperty("minVersion", out var minimum) && !SupportedMinimum(minimum))
            throw new InvalidDataException("The glTF asset.minVersion must be canonical major.minor, no greater than the supported version 2.0.");
        static bool SupportedMinimum(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 19 } text) return false;
            var parts = text.Split('.');
            if (parts.Length != 2 || parts.Any(p => p.Length is < 1 or > 9 || p.Length > 1 && p[0] == '0' || !p.All(char.IsAsciiDigit))) return false;
            int major = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), minor = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            return major < 2 || major == 2 && minor == 0;
        }
        // A required extension changes what the file means, so one the reader does not implement refuses the file (glTF has
        // readers do so). It implements KHR_mesh_quantization (integer vertex attributes, below) and KHR_texture_transform
        // on the base-colour texture, the only texture it reads.
        foreach (string declaration in (ReadOnlySpan<string>)["extensionsRequired", "extensionsUsed"])
            if (parsed.TryGetProperty(declaration, out var extensions))
            {
                if (extensions.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"glTF {declaration} must be an array of extension names.");
                foreach (var extension in extensions.EnumerateArray())
                {
                    if (declaration == "extensionsRequired" && (extension.ValueKind != JsonValueKind.String || !extension.ValueEquals(MeshQuantization) && !extension.ValueEquals(TextureTransform)))
                        throw new InvalidDataException($"The glTF file requires extension {Shown(extension)}, which zStudio does not support.");
                    if (extension.ValueKind != JsonValueKind.String) throw new InvalidDataException($"glTF {declaration} must contain only extension names.");
                }
            }
        // The vertex attributes KHR_mesh_quantization adds (8- and 16-bit integer positions, normals and texture
        // coordinates) are glTF only in a file that declares it, as glTF has a file declare every extension it uses.
        bool quantized = Declares(parsed, "extensionsUsed") || Declares(parsed, "extensionsRequired");
        if (root["animations"] is JsonArray { Count: > 0 })
            throw new InvalidDataException("glTF animation channels cannot be preserved in a RECOIL model. Export the static model and author animations in the project's .zad/.zan sources.");
        if ((root["nodes"] as JsonArray ?? []).Any(n => n?["skin"] != null))
            throw new InvalidDataException("glTF skinning cannot be preserved in a RECOIL model. Bake the armature's intended pose into the mesh before exporting.");
        if ((root["nodes"] as JsonArray ?? []).Any(n => n?["camera"] != null))
            throw new InvalidDataException("Core glTF camera nodes cannot be preserved in a RECOIL model. Remove cameras from the export; reconstructed RECOIL cameras use their recorded engine attributes.");
        // Every buffer is held until the views are read, so together they are bounded like one file: a URI listed again is the
        // bytes already read, a buffer's declared length is checked against what is left before its file is read, and what
        // each read returns counts once.
        var jsonBuffers = root["buffers"] as JsonArray ?? [];
        // Null denotes the input's BIN slice. Pass that span to decoders rather than capturing or copying it.
        List<byte[]?> buffers = [];
        // The bytes each buffer holds for its views: its stated length (a GLB chunk or a file may hold more).
        List<long> bufferLengths = [];
        Dictionary<string, byte[]> external = new(StringComparer.Ordinal);
        HashSet<byte[]> held = new(ReferenceEqualityComparer.Instance); long heldBytes = glbBinary.Length;
        InvalidDataException TooLarge() => new($"The glTF file's buffers hold more than {(bufferBytes % (1024 * 1024) == 0 ? $"{bufferBytes / (1024 * 1024):N0} MiB" : $"{bufferBytes:N0} bytes")} together, more than a model may; remove buffers it does not use, or split it into several files.");
        if (heldBytes > bufferBytes) throw TooLarge();
        foreach (var buffer in jsonBuffers)
        {
            token.ThrowIfCancellationRequested();
            if (buffer is not JsonObject entry) throw new InvalidDataException("A glTF buffer is not an object.");
            string? uri = entry["uri"]?.GetValue<string>();
            long? declared = GltfInteger.OptionalInt64(entry["byteLength"], "byteLength");
            if (declared < 0) throw new InvalidDataException("A glTF buffer has a negative length.");
            byte[]? data;
            if (uri == null)
            {
                if (buffers.Count != 0 || glbBinary.IsEmpty) throw new InvalidDataException("Only the first GLB buffer may use the BIN chunk.");
                data = null;
            }
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
            if (data != null && held.Add(data) && (heldBytes += data.Length) > bufferBytes) throw TooLarge();
            int dataLength = data?.Length ?? glbBinary.Length;
            if (dataLength < (declared ?? 0)) throw new InvalidDataException("A glTF buffer is shorter than declared.");
            if (data == null && declared is { } statedLength && dataLength - statedLength > 3) throw new InvalidDataException("GLB BIN padding exceeds three bytes.");
            buffers.Add(data); bufferLengths.Add(declared ?? dataLength);
        }
        JsonArray views = root["bufferViews"] as JsonArray ?? [], accessors = root["accessors"] as JsonArray ?? [];
        // Accessors may be shared by any number of primitives; every use is decoded, so the file's total is bounded, with what
        // is made in place of an absent accessor (a morph target that moves nothing).
        long decoded = 0;
        void Charge(long elements) { if ((decoded += elements) > limits.DecodedElements) throw new InvalidDataException("The glTF file decodes to more data than a model can hold."); }
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
        (int Index, JsonObject View) ViewEntry(JsonNode? reference)
        {
            int index = Reference(reference, views.Count, "buffer view");
            return (index, views[index] as JsonObject ?? throw new InvalidDataException($"glTF buffer view {index} is not an object."));
        }
        // A buffer view's bytes from an extra offset: its buffer and where the data starts. glTF places a view within its
        // buffer's stated length and what an accessor reads within its view, so byteCount bytes must lie in the view, not
        // only in the buffer: bytes past the view's end belong to another view (or to none), which the read would take for
        // this accessor's. A view holding tightly packed data (sparse indices and values) may not state a stride. glTF has
        // the view start at a multiple of the data's component size in its buffer, and the data at such a multiple in the
        // view (at a multiple of 4 for a vertex attribute, whose elements start on 4-byte boundaries): data placed
        // otherwise is refused, as other readers refuse it, rather than read from where they would not read it.
        (byte[]? Data, long Start) View(JsonNode? reference, long offset, long byteCount, string what, int componentSize, bool packed = false, bool attribute = false)
        {
            var (index, view) = ViewEntry(reference);
            int buffer = Reference(view["buffer"], buffers.Count, "buffer");
            long start = GltfInteger.OptionalInt64(view["byteOffset"], "byteOffset") ?? 0, length = GltfInteger.OptionalInt64(view["byteLength"], "byteLength") ?? throw new InvalidDataException($"glTF buffer view {index} has no byteLength.");
            if (start < 0 || length < 1 || start > bufferLengths[buffer] - length)
                throw new InvalidDataException($"glTF buffer view {index} ({length:N0} bytes from byte {start:N0}) does not lie within buffer {buffer}, which holds {bufferLengths[buffer]:N0} bytes.");
            if (packed && view["byteStride"] != null) throw new InvalidDataException($"glTF buffer view {index} holds the {what}, which are tightly packed, but it states a byte stride (glTF allows none there).");
            if (offset < 0 || offset > length || byteCount > length - offset)
                throw new InvalidDataException($"The {what} need {byteCount:N0} bytes from byte {offset:N0} of glTF buffer view {index}, which holds {length:N0}; an accessor reads only its own view.");
            if (start % componentSize != 0)
                throw new InvalidDataException($"glTF buffer view {index} starts at byte {start:N0} of buffer {buffer}, but it holds the {what}, whose components take {componentSize} bytes; glTF needs the view to start at a multiple of {componentSize}, so align it in its buffer.");
            int alignment = attribute ? 4 : componentSize;
            if (offset % alignment != 0)
                throw new InvalidDataException($"The {what} start at byte {offset:N0} of glTF buffer view {index}; glTF needs {(attribute ? "a vertex attribute's elements to start on 4-byte boundaries of their view" : $"them to start at a multiple of {componentSize}, the size of their components")}, so move them to a multiple of {alignment}.");
            return (buffers[buffer], start + offset);
        }
        // An accessor holds what its use reads (glTF 2.0): positions, normals and morph target positions are vectors of floats,
        // texture coordinates of floats or normalized unsigned 8- or 16-bit integers, and indices are single unsigned integers
        // that are not normalized. A file that declares KHR_mesh_quantization may also store positions and texture coordinates
        // as any 8- or 16-bit integers, normals as normalized signed ones and morph target positions as signed ones. Anything
        // else would be read as values other than the file states (fractions truncated, normalized indices scaled, vector
        // components taken for indices), so it is refused before anything is decoded.
        float[] Accessor(JsonNode which, AccessorUse use, ReadOnlySpan<byte> binary)
        {
            int index = Reference(which, accessors.Count, "accessor");
            var a = accessors[index] ?? throw new InvalidDataException("Missing accessor.");
            string type = a["type"]!.GetValue<string>();
            int componentType = GltfInteger.Int32(a["componentType"], "componentType"); bool normalized = a["normalized"]?.GetValue<bool>() ?? false;
            var (shape, components, what) = use switch
            {
                AccessorUse.Positions => ("VEC3", 3, "positions"), AccessorUse.Normals => ("VEC3", 3, "normals"),
                AccessorUse.TargetPositions => ("VEC3", 3, "morph target positions"),
                AccessorUse.TextureCoordinates => ("VEC2", 2, "texture coordinates"), _ => ("SCALAR", 1, "indices"),
            };
            if (type != shape) throw new InvalidDataException($"glTF accessor {index} holds {(type.Length > 16 ? type[..16] + "…" : type)} values, but it is used for {what}, which need {shape}.");
            bool small = componentType is 5120 or 5121 or 5122 or 5123, floats = componentType == 5126 && !normalized;
            // What a file needs to declare KHR_mesh_quantization for, said when it does not.
            string Quantized(string core, string extension) => quantized ? $"{core}, or {extension}" : $"{core} ({extension} need the {MeshQuantization} extension, which the file does not declare)";
            var (valid, needed) = use switch
            {
                AccessorUse.Indices => (componentType is 5121 or 5123 or 5125 && !normalized, "unsigned bytes, shorts or integers that are not normalized"),
                AccessorUse.Normals => (floats || quantized && componentType is 5120 or 5122 && normalized, Quantized("floats that are not normalized", "normalized signed bytes or shorts")),
                AccessorUse.TargetPositions => (floats || quantized && componentType is 5120 or 5122, Quantized("floats that are not normalized", "signed bytes or shorts")),
                AccessorUse.TextureCoordinates => (floats || componentType is 5121 or 5123 && normalized || quantized && small,
                    Quantized("floats that are not normalized, or normalized unsigned bytes or shorts", "other bytes or shorts")),
                _ => (floats || quantized && small, Quantized("floats that are not normalized", "bytes or shorts")),
            };
            if (!valid) throw new InvalidDataException($"glTF accessor {index} holds {(normalized ? "normalized " : "")}{ComponentName(componentType)} values, but it is used for {what}, which need {needed}.");
            int count = GltfInteger.Int32(a["count"], "count");
            if (count < 0 || (long)count * components > MaximumElements) throw new InvalidDataException("A glTF accessor is too large.");
            Charge((long)count * components);
            int size = Size(componentType), element = size * components;
            float[] values = new float[count * components];
            if (a["bufferView"] is { } reference && count > 0)
            {
                // glTF places element i at offset + i × stride, the stride being the view's or, when it states none, an
                // element's size. Only vertex attributes are read with a stated stride: a multiple of 4 from 4 to 252 bytes that
                // holds an element and is no longer than the view. A vertex attribute's elements start on 4-byte boundaries, so
                // elements of another size need a stride (KHR_mesh_quantization's byte normals take 4 bytes, not 3). Other
                // readers refuse anything else, so it is refused rather than read from offsets they would not read.
                var (viewIndex, view) = ViewEntry(reference);
                bool attribute = use != AccessorUse.Indices;
                int stride = element, padded = (element + 3) / 4 * 4;
                string elements = $"glTF accessor {index}'s {what}, whose elements take {element} bytes";
                if (view["byteStride"] is { } stated)
                {
                    if (!attribute)
                        throw new InvalidDataException($"glTF buffer view {viewIndex} states a byte stride, but accessor {index} reads indices from it, which glTF has tightly packed (only vertex attributes have a stride); remove the view's byteStride.");
                    stride = GltfInteger.Int32(stated, "byte stride");
                    long? length = GltfInteger.OptionalInt64(view["byteLength"], "byteLength");
                    if (stride < 4 || stride > 252 || stride % 4 != 0 || stride < element || stride > length)
                        throw new InvalidDataException($"glTF buffer view {viewIndex} states a byte stride of {stride} for {elements}; glTF needs a multiple of 4 from 4 to 252 that holds an element and is no longer than the view{(length is { } bytes ? $" ({bytes:N0} bytes)" : "")}, such as {padded}.");
                }
                else if (attribute && element % 4 != 0)
                    throw new InvalidDataException($"glTF buffer view {viewIndex} states no byte stride, so {elements}, would lie {element} bytes apart; glTF starts each element of a vertex attribute on a 4-byte boundary, so pad each element to {padded} bytes and state a byteStride of {padded}.");
                var (data, start) = View(reference, GltfInteger.OptionalInt64(a["byteOffset"], "byteOffset") ?? 0, (long)(count - 1) * stride + element, $"{count:N0} {what} of glTF accessor {index}", size, attribute: attribute);
                for (int i = 0; i < count; i++)
                    for (int c = 0; c < components; c++) values[i * components + c] = ReadComponent((data == null ? binary : data.AsSpan())[(int)(start + (long)i * stride + c * size)..], componentType, normalized);
            }
            // Sparse accessors replace some elements (Blender writes morph targets this way, often without a buffer view).
            if (a["sparse"] is { } sparse)
            {
                int n = GltfInteger.Int32(sparse["count"], "count");
                if (n < 1 || n > count) throw new InvalidDataException("A sparse glTF accessor has an invalid count.");
                var indices = sparse["indices"]!; int indexType = GltfInteger.Int32(indices["componentType"], "componentType");
                if (indexType is not (5121 or 5123 or 5125)) throw new InvalidDataException($"Sparse glTF indices cannot use component type {indexType}.");
                int indexSize = Size(indexType);
                var (indexData, indexStart) = View(indices["bufferView"], GltfInteger.OptionalInt64(indices["byteOffset"], "byteOffset") ?? 0, (long)n * indexSize, $"sparse indices of glTF accessor {index}", indexSize, packed: true);
                var (valueData, valueStart) = View(sparse["values"]!["bufferView"], GltfInteger.OptionalInt64(sparse["values"]!["byteOffset"], "byteOffset") ?? 0, (long)n * element, $"sparse values of glTF accessor {index}", size, packed: true);
                // glTF has the indices strictly increase, so each element is replaced once: a repeated one would be replaced
                // by whichever value a reader applies last, which readers do not agree on.
                long previous = -1;
                for (int k = 0; k < n; k++)
                {
                    var at = (indexData == null ? binary : indexData.AsSpan())[(int)(indexStart + (long)k * indexSize)..];
                    long target = indexType switch { 5121 => at[0], 5123 => BinaryPrimitives.ReadUInt16LittleEndian(at), _ => BinaryPrimitives.ReadUInt32LittleEndian(at) };
                    if (target >= count) throw new InvalidDataException("A sparse glTF index is out of range.");
                    if (target <= previous)
                        throw new InvalidDataException($"The sparse indices of glTF accessor {index} do not increase: index {k:N0} names element {target:N0} after element {previous:N0}; glTF needs each greater than the one before.");
                    previous = target;
                    for (int c = 0; c < components; c++) values[target * components + c] = ReadComponent((valueData == null ? binary : valueData.AsSpan())[(int)(valueStart + ((long)k * components + c) * size)..], componentType, normalized);
                }
            }
            return values;
            float ReadComponent(ReadOnlySpan<byte> span, int type, bool normalize)
            {
                float value = Component(span, type, normalize);
                if (!float.IsFinite(value)) throw new InvalidDataException($"glTF accessor {index} contains a non-finite component.");
                return value;
            }
        }
        List<Vector3> Vectors(JsonNode reference, AccessorUse use, ReadOnlySpan<byte> binary)
        {
            var v = Accessor(reference, use, binary); List<Vector3> vectors = new(v.Length / 3);
            for (int i = 0; i < v.Length / 3; i++) vectors.Add(new(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]));
            return vectors;
        }

        JsonArray jsonImages = root["images"] as JsonArray ?? [], jsonTextures = root["textures"] as JsonArray ?? [], jsonSamplers = root["samplers"] as JsonArray ?? [];
        // An image's path is read once and shared by every material that shows it, however long it is and however many do.
        var imagePaths = new (string? Uri, bool Embedded)?[jsonImages.Count];
        List<GltfMaterial> materials = [];
        // Where each material's base-colour texture is sampled: the texture coordinate set it names and the transform
        // KHR_texture_transform applies to those coordinates (null for none). A primitive showing the material reads that set
        // and keeps the coordinates transformed, so the texture lies where the file places it.
        List<(int Set, Matrix3x2? Transform)> sampling = [];
        foreach (var m in root["materials"] as JsonArray ?? [])
        {
            GltfMaterial material = new() { Name = m?["name"]?.GetValue<string>() ?? "", DoubleSided = m?["doubleSided"]?.GetValue<bool>() ?? false, AlphaMode = m?["alphaMode"]?.GetValue<string>() ?? "OPAQUE", Extras = Extras(m) };
            if (material.AlphaMode is not ("OPAQUE" or "BLEND" or "MASK")) throw new InvalidDataException($"glTF material {materials.Count} has an unsupported alpha mode.");
            if (m?["alphaCutoff"] is { } cutoff) material.AlphaCutoff = cutoff.GetValue<float>();
            if (!float.IsFinite(material.AlphaCutoff) || material.AlphaCutoff < 0) throw new InvalidDataException($"glTF material {materials.Count} has an invalid alpha cutoff.");
            var pbr = m?["pbrMetallicRoughness"];
            RefuseMaterialChannels(m!, pbr, materials.Count);
            if (pbr?["baseColorFactor"] is { } factor)
            {
                if (!TryNumbers(factor, 4, out var c) || c.Any(v => v < 0 || v > 1)) throw new InvalidDataException($"glTF material {materials.Count} has a base colour that is not 4 finite numbers between 0 and 1.");
                material.BaseColor = new(c[0], c[1], c[2], c[3]);
            }
            (int Set, Matrix3x2? Transform) sampled = (0, null);
            if (pbr?["baseColorTexture"] is { } textureInfo)
            {
                var textureIndex = textureInfo["index"] ?? throw new InvalidDataException($"glTF material {materials.Count}'s baseColorTexture needs a texture index.");
                sampled = Sampling(textureInfo, materials.Count);
                int textureNumber = Reference(textureIndex, jsonTextures.Count, "texture");
                var texture = jsonTextures[textureNumber] as JsonObject ?? throw new InvalidDataException($"glTF texture {textureNumber} is not an object.");
                var source = texture["source"] ?? throw new InvalidDataException($"glTF texture {textureNumber} needs an image source; export the base-colour image as an external PNG.");
                int image = Reference(source, jsonImages.Count, "image");
                (material.ImageUri, material.EmbeddedImage) = imagePaths[image] ??=
                    jsonImages[image]?["uri"]?.GetValue<string>() is { } uri && !uri.StartsWith("data:", StringComparison.Ordinal) ? (Uri.UnescapeDataString(uri), false) : ((string?)null, true);
                // Without a sampler the texture repeats (glTF's default); its filters are not kept (see Clamps).
                if (texture["sampler"] is { } sampler)
                {
                    int s = Reference(sampler, jsonSamplers.Count, "sampler");
                    var entry = jsonSamplers[s] as JsonObject ?? throw new InvalidDataException($"glTF sampler {s} is not an object.");
                    material.ClampS = Clamps(entry["wrapS"], "wrapS", s, materials.Count, material.Name);
                    material.ClampT = Clamps(entry["wrapT"], "wrapT", s, materials.Count, material.Name);
                }
            }
            materials.Add(material); sampling.Add(sampled);
        }
        GltfMaterial implicitMaterial = new() { MetallicFactor = 1 };
        List<GltfMesh> meshes = [];
        foreach (var m in root["meshes"] as JsonArray ?? [])
        {
            token.ThrowIfCancellationRequested();
            GltfMesh mesh = new() { Name = m?["name"]?.GetValue<string>() ?? "", Extras = Extras(m) };
            foreach (var w in m?["weights"] as JsonArray ?? []) mesh.Weights.Add(w!.GetValue<float>());
            // Older reconstructed point-only models used an empty mesh. Recognize that engine record by validated
            // content; an ordinary glTF mesh still needs primitives, including when its extras are empty or malformed.
            if (m?["primitives"] is not JsonArray meshPrimitives || meshPrimitives.Count == 0 && !Worlds.WorldGltf.IsLegacyPointMesh(mesh))
                throw new InvalidDataException($"glTF mesh {meshes.Count} must contain a nonempty primitives array.");
            int number = -1;
            foreach (var p in meshPrimitives)
            {
                number++;
                int mode = GltfInteger.OptionalInt32(p?["mode"], "mode") ?? 4;
                if (mode is not (4 or 5 or 6)) throw new InvalidDataException($"glTF mesh {meshes.Count} primitive {number} uses mode {mode}, which RECOIL cannot store. Convert it to triangles before exporting.");
                if (p?["attributes"]?["POSITION"] is null) throw new InvalidDataException($"glTF mesh {meshes.Count} primitive {number} has no POSITION attribute.");
                GltfPrimitive primitive = new() { Extras = Extras(p) };
                (int Set, Matrix3x2? Transform) sampled = (0, null);
                if (p["material"] is { } material)
                {
                    int shown = Reference(material, materials.Count, "material");
                    primitive.Material = materials[shown]; sampled = sampling[shown];
                }
                else primitive.Material = implicitMaterial;
                var attributes = p["attributes"]!;
                if (attributes["COLOR_0"] != null)
                    throw new InvalidDataException("RECOIL models cannot preserve glTF vertex colours. Bake vertex colours into the PNG texture before exporting.");
                primitive.Positions.AddRange(Vectors(attributes["POSITION"]!, AccessorUse.Positions, glbBinary));
                int vertices = primitive.Positions.Count;
                if (attributes["NORMAL"] is { } normal)
                {
                    primitive.Normals.AddRange(Vectors(normal, AccessorUse.Normals, glbBinary));
                    OnePerPosition(primitive.Normals.Count, "normals", vertices, meshes.Count, number);
                }
                // The set the material's texture is sampled with (TEXCOORD_0 when it names none).
                if (attributes[$"TEXCOORD_{sampled.Set}"] is { } uv)
                {
                    var values = Accessor(uv, AccessorUse.TextureCoordinates, glbBinary);
                    OnePerPosition(values.Length / 2, sampled.Set == 0 ? "texture coordinates" : $"texture coordinates (TEXCOORD_{sampled.Set}, which its material's texture uses)", vertices, meshes.Count, number);
                    for (int i = 0; i < values.Length / 2; i++)
                    {
                        Vector2 coordinates = new(values[i * 2], values[i * 2 + 1]);
                        if (sampled.Transform is { } transform && (coordinates = Vector2.Transform(coordinates, transform)) is { X: var u, Y: var v } && !(float.IsFinite(u) && float.IsFinite(v)))
                            throw new InvalidDataException($"Primitive {number} of glTF mesh {meshes.Count} has texture coordinates that are not finite numbers once its material's texture transform (KHR_texture_transform) is applied.");
                        primitive.TexCoords.Add(coordinates);
                    }
                }
                primitive.TextureCoordinateSet = sampled.Set;
                foreach (var target in p["targets"] as JsonArray ?? [])
                {
                    if (target?["NORMAL"] != null || target?["TANGENT"] != null)
                        throw new InvalidDataException("RECOIL morph targets store position deltas only. Export shape keys with morph normals and tangents disabled.");
                    if (target?["POSITION"] is { } delta)
                    {
                        var moved = Vectors(delta, AccessorUse.TargetPositions, glbBinary);
                        OnePerPosition(moved.Count, "morph target positions", vertices, meshes.Count, number);
                        primitive.Targets.Add(moved);
                    }
                    else
                    {
                        // A target that moves nothing is made in full, one delta for each position, so it counts as decoded.
                        Charge(3L * vertices);
                        primitive.Targets.Add(Enumerable.Repeat(Vector3.Zero, vertices).ToList());
                    }
                }
                int[] indices;
                if (p["indices"] is { } ix)
                {
                    float[] values = Accessor(ix, AccessorUse.Indices, glbBinary);
                    indices = new int[values.Length];
                    // Unsigned integers, exact as floats below 2^24, which no position count reaches.
                    for (int i = 0; i < values.Length; i++) indices[i] = values[i] < vertices ? (int)values[i] : throw new InvalidDataException("A glTF index is out of range.");
                }
                else indices = Enumerable.Range(0, vertices).ToArray();
                if (indices.Length < 3 || mode == 4 && indices.Length % 3 != 0)
                    throw new InvalidDataException($"Primitive {number} of glTF mesh {meshes.Count} has {indices.Length} indices, invalid for its triangle topology.");
                if (mode == 4) primitive.Indices.AddRange(indices);
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
        var nodes = jsonNodes.Select((n, i) => new GltfNode { Name = n?["name"]?.GetValue<string>() ?? "", Index = i, Extras = Extras(n) }).ToArray();
        // Node hierarchies are trees (glTF requires it): a node under two parents, or listed twice by one, would be walked
        // once for each path to it, which doubles with every level, so each node has one parent at most.
        bool[] parented = new bool[nodes.Length];
        for (int i = 0; i < jsonNodes.Count; i++)
        {
            var n = jsonNodes[i] as JsonObject ?? throw new InvalidDataException($"glTF node {i} is not an object."); var node = nodes[i];
            if (n["mesh"] is { } mesh) node.Mesh = meshes[Reference(mesh, meshes.Count, "mesh")];
            if (n["weights"] is { } weights)
            {
                int targets = node.Mesh?.Primitives.FirstOrDefault()?.Targets.Count ?? 0;
                if (targets == 0 || !TryNumbers(weights, targets, out var values))
                    throw new InvalidDataException($"glTF node {i} needs one finite weight per morph target of its mesh.");
                node.Weights.AddRange(values);
            }
            foreach (var c in n["children"] as JsonArray ?? [])
            {
                int child = GltfInteger.Int32(c, "child"); if (child < 0 || child >= nodes.Length || child == i) throw new InvalidDataException("A glTF node has an invalid child.");
                if (parented[child]) throw new InvalidDataException($"glTF node {child} is listed as a child more than once; a node may have only one parent.");
                parented[child] = true;
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
            doc.SceneExtras = Extras(scene);
            bool[] rooted = new bool[nodes.Length];
            foreach (var r in scene["nodes"] as JsonArray ?? [])
            {
                int k = Reference(r, nodes.Length, "node");
                if (parented[k]) throw new InvalidDataException($"The glTF scene lists node {k} as a root, but it is another node's child.");
                if (rooted[k]) throw new InvalidDataException($"The glTF scene lists node {k} more than once.");
                rooted[k] = true; doc.Roots.Add(nodes[k]);
            }
        }
        // Without scenes, every node that is nobody's child is a root.
        else doc.Roots.AddRange(nodes.Where((n, i) => !parented[i]));
        // Reject cycles and hierarchies too deep to follow before anyone walks them; the walk itself keeps its own stack.
        HashSet<GltfNode> visiting = new(ReferenceEqualityComparer.Instance);
        Dictionary<GltfNode, int> heights = new(ReferenceEqualityComparer.Instance);
        Stack<(GltfNode Node, int Next)> path = new();
        foreach (var r in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (heights.ContainsKey(r)) continue;
            path.Push((r, 0)); visiting.Add(r);
            while (path.Count > 0)
            {
                var (node, next) = path.Pop();
                if (next == node.Children.Count)
                {
                    int height = 1 + (node.Children.Count == 0 ? 0 : node.Children.Max(c => heights[c]));
                    if (height > MaximumDepth) throw new InvalidDataException($"The glTF node hierarchy is deeper than {MaximumDepth} levels.");
                    visiting.Remove(node); heights.Add(node, height); continue;
                }
                path.Push((node, next + 1));
                var child = node.Children[next];
                if (heights.ContainsKey(child)) continue;
                if (!visiting.Add(child)) throw new InvalidDataException("The glTF node hierarchy has a cycle.");
                if (path.Count >= MaximumDepth) throw new InvalidDataException($"The glTF node hierarchy is deeper than {MaximumDepth} levels.");
                path.Push((child, 0));
            }
        }
        return doc;
    }

    /// <summary>
    /// Refuses, on the parsed text and before any entry is read, a file whose lists or metadata pass what a model may hold.
    /// The reader makes an object of every entry of a list it reads or indexes and keeps a copy of names, extras and image
    /// paths, unused ones too, so a file would otherwise cost far more than its accessor and buffer limits allow. Counting
    /// reads only the parsed text: no list is made into objects to be counted.
    /// </summary>
    private static void Bound(JsonElement root, long metadataBytes)
    {
        int nodes = Entries("nodes", MaximumNodes);
        foreach (string list in (ReadOnlySpan<string>)["meshes", "materials", "images", "textures", "samplers", "scenes"]) Entries(list, MaximumNodes);
        Entries("accessors", MaximumEntries); Entries("bufferViews", MaximumEntries);
        Entries("buffers", MaximumBuffers);
        Length(root, "animations"); Length(root, "skins");
        long metadata = root.TryGetProperty("asset", out var asset) && asset.ValueKind == JsonValueKind.Object ? Bytes(asset, "generator", JsonValueKind.String) : 0;
        long children = 0, primitives = 0, targets = 0, weights = 0;
        foreach (var node in Objects(root, "nodes"))
        {
            metadata += Named(node); children += Length(node, "children");
            if ((weights += Length(node, "weights")) > MaximumPrimitives) throw new InvalidDataException("The glTF nodes hold too many morph weights.");
        }
        // Each node has one parent at most, so more children than nodes means some node has several.
        if (children > nodes) throw new InvalidDataException($"The glTF file's {nodes:N0} nodes list {children:N0} children; a node may have only one parent.");
        foreach (var scene in Objects(root, "scenes"))
        {
            metadata += Bytes(scene, "extras", JsonValueKind.Object);
            if (Length(scene, "nodes") > nodes) throw new InvalidDataException($"A glTF scene lists {Length(scene, "nodes"):N0} root nodes but the file holds {nodes:N0} nodes; a scene lists each root once.");
        }
        foreach (var material in Objects(root, "materials")) metadata += Named(material);
        foreach (var mesh in Objects(root, "meshes"))
        {
            metadata += Named(mesh);
            if ((weights += Length(mesh, "weights")) > MaximumPrimitives) throw new InvalidDataException($"The glTF file's meshes list more than {MaximumPrimitives:N0} morph target weights; a model may hold at most {MaximumPrimitives:N0}.");
            if ((primitives += Length(mesh, "primitives")) > MaximumPrimitives) throw new InvalidDataException($"The glTF file's meshes hold more than {MaximumPrimitives:N0} primitives; a model may hold at most {MaximumPrimitives:N0}.");
            foreach (var primitive in Objects(mesh, "primitives"))
            {
                metadata += Bytes(primitive, "extras", JsonValueKind.Object);
                if ((targets += Length(primitive, "targets")) > MaximumPrimitives) throw new InvalidDataException($"The glTF file's primitives hold more than {MaximumPrimitives:N0} morph targets; a model may hold at most {MaximumPrimitives:N0}.");
            }
        }
        // An embedded image (a data URI) is only recognized, never kept.
        foreach (var image in Objects(root, "images"))
            if (image.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String && !JsonMarshal.GetRawUtf8Value(uri).StartsWith("\"data:"u8)) metadata += JsonMarshal.GetRawUtf8Value(uri).Length;
        if (metadata > metadataBytes)
            throw new InvalidDataException($"The glTF file's names, extras and image paths hold more than {(metadataBytes % (1024 * 1024) == 0 ? $"{metadataBytes / (1024 * 1024):N0} MiB" : $"{metadataBytes:N0} bytes")} together, more than a model may; remove custom properties (extras) and materials, meshes or nodes it does not need.");

        int Entries(string list, int maximum)
        {
            int count = Length(root, list);
            if (!root.TryGetProperty(list, out var array)) return 0;
            if (count > maximum) throw new InvalidDataException($"The glTF file lists {count:N0} {(list == "bufferViews" ? "buffer views" : list)}; a model may hold at most {maximum:N0}, so remove those it does not use or split it into several files.");
            int index = 0;
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"glTF {(list == "meshes" ? "mesh" : list == "bufferViews" ? "buffer view" : list[..^1])} {index} is not an object.");
                index++;
            }
            return count;
        }
        static IEnumerable<JsonElement> Objects(JsonElement holder, string list)
        {
            Length(holder, list);
            if (!holder.TryGetProperty(list, out var array)) yield break;
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"glTF {list} must contain only objects.");
                yield return entry;
            }
        }
        static int Length(JsonElement holder, string list)
        {
            if (!holder.TryGetProperty(list, out var array)) return 0;
            if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"glTF {list} must be an array.");
            return array.GetArrayLength();
        }
        // The bytes of a value of the kind the reader keeps (a string name, an object of extras), as written.
        static long Bytes(JsonElement holder, string property, JsonValueKind kind) =>
            holder.TryGetProperty(property, out var value) && value.ValueKind == kind ? JsonMarshal.GetRawUtf8Value(value).Length : 0;
        static long Named(JsonElement entry) => Bytes(entry, "name", JsonValueKind.String) + Bytes(entry, "extras", JsonValueKind.Object);
    }

    /// <summary>An entry's extras when they are an object (anything else is not extras), copied so the parsed text is not held.</summary>
    private static JsonObject? Extras(JsonNode? entry) => entry?["extras"] is JsonObject extras ? (JsonObject)extras.DeepClone() : null;

    /// <summary>Shared pre-DOM guard for the importer and source JSON editors, including unknown properties.</summary>
    internal static void ValidateJsonText(ReadOnlySpan<byte> json, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (json.Length > MaximumJsonBytes)
            throw new InvalidDataException("glTF JSON exceeds 32 MiB. Remove unused metadata/extras and put large inline buffers in external .bin files or a GLB BIN chunk.");
        Utf8JsonReader scan = new(json, new JsonReaderOptions { MaxDepth = 64 });
        int tokens = 0;
        while (scan.Read())
        {
            if ((++tokens & 1023) == 0) token.ThrowIfCancellationRequested();
            if (tokens > 4_000_000) throw new InvalidDataException("glTF JSON holds more than 4,000,000 tokens. Split the model or remove unused metadata.");
        }
    }

    private static void RefuseMaterialChannels(JsonNode material, JsonNode? pbr, int index)
    {
        void Unsupported(string field) => throw new InvalidDataException($"glTF material {index} uses {field}, which RECOIL cannot preserve. Bake the appearance into the base-colour texture and remove that channel.");
        foreach (string field in new[] { "normalTexture", "occlusionTexture", "emissiveTexture" })
            if (material[field] != null) Unsupported(field);
        if (pbr?["metallicRoughnessTexture"] != null) Unsupported("metallicRoughnessTexture");
        // glTF defaults metalness to 1, including when the entire PBR object is absent.
        if (pbr?["metallicFactor"] is not { } metallic || !TryNumber(metallic, out float m) || m != 0) Unsupported("metallicFactor (defaults to 1; explicitly set 0 for RECOIL)");
        if (pbr?["roughnessFactor"] is { } roughness && (!TryNumber(roughness, out float r) || r != 1)) Unsupported("roughnessFactor (only 1 is representable)");
        if (material["emissiveFactor"] is { } emissive && (!TryNumbers(emissive, 3, out var e) || e.Any(v => v != 0))) Unsupported("emissiveFactor");
    }

    /// <summary>
    /// Where a material's base-colour texture (its texture info) is sampled: the texture coordinate set it names (texCoord,
    /// 0 when absent) and the transform its KHR_texture_transform extension applies, as glTF states it: the coordinates are
    /// scaled, rotated by <c>rotation</c> radians (u loses v·sin, v gains u·sin) and offset, and the extension's own texCoord
    /// replaces the set. Another extension on it may change where the texture lies as well, so it is refused rather than read
    /// without it, as is a set or transform that is not a valid number.
    /// </summary>
    private static (int Set, Matrix3x2? Transform) Sampling(JsonNode info, int material)
    {
        int set = info["texCoord"] is { } named ? Set(named) : 0;
        Matrix3x2? transform = null;
        if (info["extensions"] is { } extensions)
        {
            if (extensions is not JsonObject list) throw Malformed("extensions that are not an object");
            foreach (var (name, value) in list)
            {
                // Another extension here is optional (a required one the reader lacks refuses the whole file), so it is ignored,
                // as glTF lets readers ignore optional extensions.
                if (name != TextureTransform) continue;
                if (value is not JsonObject t) throw Malformed($"a {TextureTransform} that is not an object");
                float[] offset = [0, 0], scale = [1, 1];
                if (t["offset"] is { } o && !TryNumbers(o, 2, out offset)) throw Malformed($"a {TextureTransform} offset that is not 2 finite numbers");
                if (t["scale"] is { } s && !TryNumbers(s, 2, out scale)) throw Malformed($"a {TextureTransform} scale that is not 2 finite numbers");
                float rotation = 0;
                if (t["rotation"] is { } r && !(TryNumber(r, out rotation) && float.IsFinite(rotation))) throw Malformed($"a {TextureTransform} rotation that is not a finite number");
                if (t["texCoord"] is { } replaced) set = Set(replaced);
                // u' = offset.u + cos·scale.u·u - sin·scale.v·v, v' = offset.v + sin·scale.u·u + cos·scale.v·v (translation ·
                // rotation · scale), as Vector2.Transform applies a Matrix3x2.
                float cos = (float)Math.Cos(rotation), sin = (float)Math.Sin(rotation);
                transform = new(cos * scale[0], sin * scale[0], -sin * scale[1], cos * scale[1], offset[0], offset[1]);
            }
        }
        return (set, transform);

        // A whole number, also when written with a zero fraction (1.0).
        int Set(JsonNode value) => GltfInteger.TryInt64(value, out long d) && d >= 0 && d <= 255 ? (int)d : throw Malformed("a texture coordinate set (texCoord) that is not a whole number of zero or more");
        InvalidDataException Malformed(string problem) => new($"glTF material {material}'s base-colour texture has {problem}.");
    }

    /// <summary>
    /// Whether a sampler's wrap mode (<paramref name="property"/>, wrapS or wrapT; glTF's REPEAT when absent) clamps the
    /// texture at its edges. A RECOIL texture stores one bit for each axis, clamp or repeat (bits 0 and 1 of its header word at
    /// +0x0E, which retail 0x46DE50 passes to the Direct3D texture record as D3DTADDRESS_CLAMP or D3DTADDRESS_WRAP), so a
    /// mirrored repeat, which flips every other tile, cannot be stored: it is refused rather than built as a repeat, which
    /// would show the texture differently, as is a value glTF does not define. The sampler's filters are not read: the engine
    /// filters every texture alike, whatever its file says.
    /// </summary>
    private static bool Clamps(JsonNode? mode, string property, int sampler, int material, string name)
    {
        if (mode == null) return false;
        long value = GltfInteger.TryInt64(mode, out long number) ? number : -1;
        if (value is 10497 or 33071) return value == 33071;
        string texture = $"glTF material {material}{(name.Length > 0 ? $" ({JsonData.ShownText(name)})" : "")}'s base-colour texture uses sampler {sampler}";
        if (value == 33648)
            throw new InvalidDataException($"{texture}, which mirrors every other tile {(property == "wrapS" ? "horizontally" : "vertically")} ({property} MIRRORED_REPEAT); RECOIL textures repeat or clamp at their edges and are never mirrored, so the game would show it differently. In Blender, set the Image Texture node's extension to Repeat (or Extend, to clamp) instead of Mirror, and replace a ping-pong UV wrap with a plain one.");
        string shown = mode is JsonValue raw && raw.TryGetValue(out JsonElement element) ? Shown(element) : mode is JsonValue ? JsonData.ShownText(mode.ToJsonString()) : mode is JsonArray ? "a list" : "an object";
        throw new InvalidDataException($"{texture}, whose {property} is {shown}; glTF wraps a texture with 10497 (REPEAT), 33071 (CLAMP_TO_EDGE) or 33648 (MIRRORED_REPEAT).");
    }

    /// <summary>Refuses an attribute that does not hold one value for each position, as glTF requires: the rest would pair values with the wrong corners.</summary>
    private static void OnePerPosition(int count, string what, int positions, int mesh, int primitive)
    {
        if (count != positions)
            throw new InvalidDataException($"Primitive {primitive} of glTF mesh {mesh} has {count:N0} {what} for {positions:N0} positions; glTF needs one for each position.");
    }

    private static string ComponentName(int componentType) => componentType switch
    {
        5120 => "signed byte", 5121 => "unsigned byte", 5122 => "signed short", 5123 => "unsigned short", 5125 => "unsigned integer", 5126 => "float",
        _ => $"component type {componentType}",
    };

    /// <summary>A JSON value for a message: a string as text, anything else as written, at most 64 characters of either.</summary>
    private static string Shown(JsonElement value)
    {
        var raw = JsonMarshal.GetRawUtf8Value(value);
        string text = value.ValueKind == JsonValueKind.String && raw.Length <= 1024 ? value.GetString()! : Encoding.UTF8.GetString(raw[..Math.Min(raw.Length, 256)]);
        return text.Length > 64 ? text[..64] + "…" : text;
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
            if (m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1)
                throw Malformed("has a non-affine matrix; its homogeneous terms must be [0, 0, 0, 1]");
            return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }
        if (translation == null && rotation == null && scale == null) return null;
        Vector3 t = translation == null ? Vector3.Zero : new(Numbers(translation, 3, "translation"));
        Quaternion r = Quaternion.Identity;
        if (rotation != null)
        {
            float[] q = Numbers(rotation, 4, "rotation");
            double squared = q.Sum(v => (double)v * v);
            if (Math.Abs(squared - 1) > 1e-5) throw Malformed("has a rotation that is not a unit quaternion");
            r = Quaternion.Normalize(new(q[0], q[1], q[2], q[3]));
        }
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
        int index = GltfInteger.Int32(value, $"{list} reference");
        if (index < 0 || index >= count) throw new InvalidDataException($"A glTF {list} reference names {list} {index}, but the file has {count:N0}.");
        return index;
    }
}
