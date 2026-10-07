using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Gltf;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// glTF extensions the reader implements (KHR_mesh_quantization required by a file, KHR_texture_transform on the base-colour
/// texture) and the vertex attributes only a file declaring KHR_mesh_quantization may use, sparse indices that must
/// increase, the texture coordinate set a material's texture names, and PNG image data inflated in place.
/// </summary>
public sealed class GltfPngRound4Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A file of one triangle (positions in accessor 0), its other accessors and views added in turn to one buffer.</summary>
    private sealed class Triangle
    {
        private readonly List<byte> buffer = [];
        private readonly JsonArray views = [], accessors = [];
        public JsonObject Attributes { get; } = [];
        public JsonObject Primitive { get; }
        public JsonObject Root { get; } = new() { ["asset"] = new JsonObject { ["version"] = "2.0" } };

        public Triangle(int componentType = 5126, bool normalized = false, string positions = "000000000000000000000000" + "0000803F0000000000000000" + "000000000000803F00000000")
        {
            Attributes["POSITION"] = Accessor(positions, componentType, "VEC3", 3, normalized);
            Primitive = new() { ["attributes"] = Attributes };
        }

        /// <summary>A view of <paramref name="hex"/>, tightly packed unless it states a <paramref name="stride"/>.</summary>
        public int View(string hex, int? stride = null)
        {
            while (buffer.Count % 4 != 0) buffer.Add(0);
            byte[] data = Convert.FromHexString(hex);
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = buffer.Count, ["byteLength"] = data.Length });
            if (stride is { } s) ((JsonObject)views[^1]!)["byteStride"] = s;
            buffer.AddRange(data);
            return views.Count - 1;
        }
        /// <summary>
        /// An accessor of <paramref name="count"/> elements given tightly packed in <paramref name="hex"/>. glTF starts each
        /// element of a vertex attribute on a 4-byte boundary, so vector elements of another size (KHR_mesh_quantization's
        /// byte and short vectors) are padded to the next multiple of 4, which their view states as its stride.
        /// </summary>
        public int Accessor(string hex, int componentType, string type, int count, bool normalized = false)
        {
            int element = (componentType is 5120 or 5121 ? 1 : componentType is 5122 or 5123 ? 2 : 4) * (type switch { "VEC2" => 2, "VEC3" => 3, _ => 1 });
            int stride = (element + 3) / 4 * 4;
            bool padded = type != "SCALAR" && stride != element && hex.Length == element * count * 2;
            if (padded) hex = string.Concat(Enumerable.Range(0, count).Select(i => hex.Substring(i * element * 2, element * 2) + new string('0', (stride - element) * 2)));
            JsonObject accessor = new() { ["bufferView"] = View(hex, padded ? stride : null), ["componentType"] = componentType, ["count"] = count, ["type"] = type };
            if (normalized) accessor["normalized"] = true;
            accessors.Add(accessor);
            return accessors.Count - 1;
        }
        /// <summary>An accessor of <paramref name="count"/> zero vectors whose sparse elements replace some.</summary>
        public int Sparse(int count, int indexType, string indices, string values, int replaced)
        {
            accessors.Add(new JsonObject
            {
                ["componentType"] = 5126, ["count"] = count, ["type"] = "VEC3",
                ["sparse"] = new JsonObject
                {
                    ["count"] = replaced,
                    ["indices"] = new JsonObject { ["bufferView"] = View(indices), ["componentType"] = indexType },
                    ["values"] = new JsonObject { ["bufferView"] = View(values) },
                },
            });
            return accessors.Count - 1;
        }
        public int TexCoords(params float[] uv) => Accessor(Floats(uv), 5126, "VEC2", uv.Length / 2);

        public GltfPrimitive Read() => GltfDocument.Read(Bytes(), _ => throw new FileNotFoundException(), Token).Roots[0].Mesh!.Primitives[0];
        public InvalidDataException Refused() => Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Bytes(), _ => throw new FileNotFoundException(), Token));
        private byte[] Bytes()
        {
            Root["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 });
            Root["meshes"] = new JsonArray(new JsonObject { ["primitives"] = new JsonArray(Primitive.DeepClone()) });
            Root["accessors"] = accessors.DeepClone(); Root["bufferViews"] = views.DeepClone();
            Root["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = buffer.Count, ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String([.. buffer]) });
            return Encoding.UTF8.GetBytes(Root.ToJsonString());
        }

        /// <summary>Material 0, showing image rock.png through its base-colour texture info <paramref name="info"/> (index 0 is added).</summary>
        public void Textured(JsonObject info)
        {
            info["index"] = 0;
            Root["materials"] = new JsonArray(new JsonObject { ["name"] = "rock", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = info } });
            Root["textures"] = new JsonArray(new JsonObject { ["source"] = 0 });
            Root["images"] = new JsonArray(new JsonObject { ["uri"] = "rock.png" });
            Primitive["material"] = 0;
        }
        public void Declare(string list, params string[] extensions) => Root[list] = new JsonArray([.. extensions.Select(e => (JsonNode)e)]);
    }

    private static string Floats(params float[] values)
    {
        byte[] data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), values[i]);
        return Convert.ToHexString(data);
    }

    // Unsigned 16-bit corners (0,0,0), (2,0,0), (0,2,0), as KHR_mesh_quantization stores positions.
    private const string ShortPositions = "000000000000" + "020000000000" + "000002000000";

    [Fact]
    public void AFileRequiringMeshQuantizationIsRead()
    {
        // KHR_mesh_quantization is required by files that store integer attributes (gltfpack lists it so): the reader
        // implements it, so such a file was refused only because it said so.
        Triangle file = new(5123, positions: ShortPositions);
        file.Declare("extensionsUsed", "KHR_mesh_quantization");
        file.Declare("extensionsRequired", "KHR_mesh_quantization");
        file.Attributes["NORMAL"] = file.Accessor("007F00" + "007F00" + "007F00", 5120, "VEC3", 3, normalized: true);
        file.Attributes["TEXCOORD_0"] = file.Accessor("0000" + "0400" + "0000" + "0000" + "0400" + "0400", 5122, "VEC2", 3);
        var read = file.Read();
        Assert.Equal([new(0, 0, 0), new(2, 0, 0), new(0, 2, 0)], read.Positions);
        Assert.All(read.Normals, n => Assert.Equal(Vector3.UnitY, n));
        Assert.Equal([new(0, 4), new(0, 0), new(4, 4)], read.TexCoords);
    }

    [Theory]
    [InlineData("""["KHR_draco_mesh_compression"]""", "requires extension KHR_draco_mesh_compression")]
    [InlineData("""["KHR_mesh_quantization","EXT_meshopt_compression"]""", "requires extension EXT_meshopt_compression")]
    [InlineData("""["KHR_mesh_quantization",7]""", "requires extension 7")]
    public void ARequiredExtensionTheReaderDoesNotImplementIsRefused(string required, string message)
    {
        Triangle file = new();
        file.Root["extensionsRequired"] = JsonNode.Parse(required);
        Assert.Contains(message, file.Refused().Message);
    }

    [Theory]
    // Integer positions, normals and texture coordinates other than core glTF's are KHR_mesh_quantization's, which a file
    // that uses them declares; without it they are not glTF, and a reader that implements only glTF refuses them.
    [InlineData("POSITION", 5123, false, "unsigned short values, but it is used for positions, which need floats that are not normalized (bytes or shorts need the KHR_mesh_quantization extension, which the file does not declare)")]
    [InlineData("NORMAL", 5120, true, "normalized signed byte values, but it is used for normals, which need floats that are not normalized (normalized signed bytes or shorts need the KHR_mesh_quantization extension")]
    [InlineData("TEXCOORD_0", 5123, false, "unsigned short values, but it is used for texture coordinates, which need floats that are not normalized, or normalized unsigned bytes or shorts (other bytes or shorts need the KHR_mesh_quantization extension")]
    [InlineData("target", 5122, false, "signed short values, but it is used for morph target positions, which need floats that are not normalized (signed bytes or shorts need the KHR_mesh_quantization extension")]
    public void QuantizedAttributesNeedTheExtensionDeclared(string use, int componentType, bool normalized, string message)
    {
        // Three zero values of the accessor's size.
        string hex = new('0', (componentType is 5120 or 5121 ? 1 : 2) * (use == "TEXCOORD_0" ? 2 : 3) * 3 * 2);
        Triangle undeclared = Quantized(use, componentType, normalized, hex), declared = Quantized(use, componentType, normalized, hex);
        Assert.Contains(message, undeclared.Refused().Message);
        declared.Declare("extensionsUsed", "KHR_mesh_quantization");
        Assert.NotNull(declared.Read());

        static Triangle Quantized(string use, int componentType, bool normalized, string hex)
        {
            if (use == "POSITION") return new Triangle(componentType, normalized, hex);
            Triangle file = new();
            int accessor = file.Accessor(hex, componentType, use == "TEXCOORD_0" ? "VEC2" : "VEC3", 3, normalized);
            if (use == "target") file.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = accessor });
            else file.Attributes[use] = accessor;
            return file;
        }
    }

    [Fact]
    public void CoreAttributeTypesNeedNoExtension()
    {
        // Normalized unsigned bytes and shorts are core glTF texture coordinates.
        Triangle file = new();
        file.Attributes["TEXCOORD_0"] = file.Accessor("FF00" + "00FF" + "FFFF", 5121, "VEC2", 3, normalized: true);
        Assert.Equal([new(1, 0), new(0, 1), new(1, 1)], file.Read().TexCoords);
    }

    [Fact]
    public void MorphTargetPositionsAreFloatsOrSignedIntegers()
    {
        // KHR_mesh_quantization stores morph target deltas as signed integers only: unsigned ones are not among its types.
        Triangle unsigned = new();
        unsigned.Declare("extensionsUsed", "KHR_mesh_quantization");
        unsigned.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = unsigned.Accessor("000000000000000000000000000000000000", 5123, "VEC3", 3) });
        Assert.Contains("unsigned short values, but it is used for morph target positions, which need floats that are not normalized, or signed bytes or shorts", unsigned.Refused().Message);

        Triangle signed = new();
        signed.Declare("extensionsUsed", "KHR_mesh_quantization");
        signed.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = signed.Accessor("FFFF00000000" + "000000000000" + "000000000100", 5122, "VEC3", 3) });
        Assert.Equal([new(-1, 0, 0), Vector3.Zero, new(0, 0, 1)], signed.Read().Targets.Single());
    }

    [Theory]
    // glTF has sparse indices strictly increase: a repeated or earlier index is replaced by whichever value a reader
    // applies last, which readers do not agree on.
    [InlineData(5121, "0202", "element 2 after element 2")]
    [InlineData(5121, "0201", "element 1 after element 2")]
    [InlineData(5123, "02000100", "element 1 after element 2")]
    [InlineData(5125, "0100000001000000", "element 1 after element 1")]
    public void SparseIndicesMustIncrease(int indexType, string indices, string message)
    {
        Triangle file = new();
        file.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = file.Sparse(3, indexType, indices, Floats(0, 1, 0, 0, 2, 0), 2) });
        var refused = file.Refused();
        Assert.Contains("sparse indices of glTF accessor 1 do not increase", refused.Message);
        Assert.Contains(message, refused.Message);
    }

    [Fact]
    public void IncreasingSparseIndicesReplaceTheirElements()
    {
        Triangle file = new();
        file.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = file.Sparse(3, 5121, "0002", Floats(0, 1, 0, 0, 2, 0), 2) });
        Assert.Equal([new(0, 1, 0), Vector3.Zero, new(0, 2, 0)], file.Read().Targets.Single());
    }

    [Fact]
    public void TheBaseColourTextureIsSampledWithTheSetItNames()
    {
        // The texture names set 1: its coordinates are those, not TEXCOORD_0's, which were read before (the texture placed
        // where the file does not place it).
        Triangle file = new();
        file.Attributes["TEXCOORD_0"] = file.TexCoords(0, 0, 0, 0, 0, 0);
        file.Attributes["TEXCOORD_1"] = file.TexCoords(0.25f, 0.5f, 0.75f, 0.5f, 0.25f, 1);
        file.Textured(new JsonObject { ["texCoord"] = 1 });
        Assert.Equal([new(0.25f, 0.5f), new(0.75f, 0.5f), new(0.25f, 1)], file.Read().TexCoords);

        // A primitive holding only the set its texture names reads it (before: no coordinates, the texture set to zero).
        Triangle only = new();
        only.Attributes["TEXCOORD_1"] = only.TexCoords(0.25f, 0.5f, 0.75f, 0.5f, 0.25f, 1);
        only.Textured(new JsonObject { ["texCoord"] = 1 });
        Assert.Equal([new(0.25f, 0.5f), new(0.75f, 0.5f), new(0.25f, 1)], only.Read().TexCoords);

        // A texture naming no set uses set 0, as does a primitive without a material.
        Triangle first = new();
        first.Attributes["TEXCOORD_0"] = first.TexCoords(1, 0, 0, 1, 1, 1);
        first.Attributes["TEXCOORD_1"] = first.TexCoords(0, 0, 0, 0, 0, 0);
        Assert.Equal([new(1, 0), new(0, 1), new(1, 1)], first.Read().TexCoords);
        first.Textured(new JsonObject());
        Assert.Equal([new(1, 0), new(0, 1), new(1, 1)], first.Read().TexCoords);

        // The set it names holds one pair for each position, as every attribute does.
        Triangle few = new();
        few.Attributes["TEXCOORD_1"] = few.TexCoords(0, 0, 1, 1);
        few.Textured(new JsonObject { ["texCoord"] = 1 });
        Assert.Contains("2 texture coordinates (TEXCOORD_1, which its material's texture uses) for 3 positions", few.Refused().Message);

        // An optional extension the reader lacks is ignored (a required one refuses the whole file), and a set written
        // with a zero fraction is the same whole number.
        Triangle optional = new();
        optional.Attributes["TEXCOORD_0"] = optional.TexCoords(0, 0, 0, 0, 0, 0);
        optional.Attributes["TEXCOORD_1"] = optional.TexCoords(0.25f, 0.5f, 0.75f, 0.5f, 0.25f, 1);
        optional.Textured(JsonNode.Parse("""{"texCoord":1.0,"extensions":{"EXT_vendor_mapping":{}}}""")!.AsObject());
        Assert.Equal([new(0.25f, 0.5f), new(0.75f, 0.5f), new(0.25f, 1)], optional.Read().TexCoords);
    }

    [Fact]
    public void TheBaseColourTextureTransformIsApplied()
    {
        // KHR_texture_transform (gltfpack dequantizes texture coordinates with it; Blender writes a Mapping node as it): the
        // Khronos' column-major GLSL example maps (1,0) to (0,1) for +pi/2. After scale and offset:
        // u' = 0.5 - 3v, v' = 0.25 + 2u. Its texCoord replaces the texture info's.
        Triangle file = new();
        file.Declare("extensionsUsed", "KHR_texture_transform");
        file.Attributes["TEXCOORD_0"] = file.TexCoords(9, 9, 9, 9, 9, 9);
        file.Attributes["TEXCOORD_1"] = file.TexCoords(1, 0, 0, 1, 1, 1);
        file.Textured(new JsonObject
        {
            ["extensions"] = new JsonObject
            {
                ["KHR_texture_transform"] = new JsonObject { ["offset"] = new JsonArray(0.5, 0.25), ["rotation"] = Math.PI / 2, ["scale"] = new JsonArray(2, 3), ["texCoord"] = 1 },
            },
        });
        Vector2[] expected = [new(0.5f, 2.25f), new(-2.5f, 0.25f), new(-2.5f, 2.25f)];
        var read = file.Read().TexCoords;
        Assert.Equal(3, read.Count);
        for (int i = 0; i < 3; i++) Assert.True(Vector2.Distance(expected[i], read[i]) < 1e-5f, $"Corner {i}: {read[i]}, expected {expected[i]}.");

        // An offset alone moves the coordinates; a file may require the extension, which the reader implements.
        Triangle moved = new();
        moved.Declare("extensionsRequired", "KHR_texture_transform");
        moved.Attributes["TEXCOORD_0"] = moved.TexCoords(1, 0, 0, 1, 1, 1);
        moved.Textured(new JsonObject { ["extensions"] = new JsonObject { ["KHR_texture_transform"] = new JsonObject { ["offset"] = new JsonArray(0.5, -1) } } });
        Assert.Equal([new(1.5f, -1), new(0.5f, 0), new(1.5f, 0)], moved.Read().TexCoords);
    }

    [Theory]
    [InlineData("""{"texCoord":-1}""", "texture coordinate set (texCoord) that is not a whole number")]
    [InlineData("""{"texCoord":1.5}""", "texture coordinate set (texCoord) that is not a whole number")]
    [InlineData("""{"extensions":[]}""", "extensions that are not an object")]
    [InlineData("""{"extensions":{"KHR_texture_transform":{"scale":[1]}}}""", "scale that is not 2 finite numbers")]
    [InlineData("""{"extensions":{"KHR_texture_transform":{"offset":[0,"a"]}}}""", "offset that is not 2 finite numbers")]
    [InlineData("""{"extensions":{"KHR_texture_transform":{"rotation":"a"}}}""", "rotation that is not a finite number")]
    [InlineData("""{"extensions":{"KHR_texture_transform":{"texCoord":-2}}}""", "texture coordinate set (texCoord) that is not a whole number")]
    [InlineData("""{"extensions":{"KHR_texture_transform":{"scale":[3e38,1]}}}""", "not finite numbers once its material's texture transform")]
    public void ATextureSamplingTheReaderCannotFollowIsRefused(string info, string message)
    {
        // Read without what they say, the texture would lie elsewhere than the file places it.
        Triangle file = new();
        file.Attributes["TEXCOORD_0"] = file.TexCoords(2, 0, 0, 1, 1, 1);
        file.Textured(JsonNode.Parse(info)!.AsObject());
        Assert.Contains(message, file.Refused().Message);
    }

    /// <summary>A PNG of the given size, bit depth and colour type whose image data is the given IDAT chunks, in turn.</summary>
    private static byte[] Png(int width, int height, int depth, int colorType, params byte[][] idats)
    {
        using MemoryStream png = new();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string type, byte[] data)
        {
            byte[] number = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); png.Write(number);
            byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data]; png.Write(typed);
            BinaryPrimitives.WriteUInt32BigEndian(number, PngDecoder.Crc(typed)); png.Write(number);
        }
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)depth; header[9] = (byte)colorType;
        Chunk("IHDR", header);
        foreach (var idat in idats) Chunk("IDAT", idat);
        Chunk("IEND", []);
        return png.ToArray();
    }
    private static byte[] Compressed(byte[] rows)
    {
        using MemoryStream compressed = new();
        using (ZLibStream z = new(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(rows);
        return compressed.ToArray();
    }

    [Fact]
    public void ImageDataIsInflatedWithoutCopyingIt()
    {
        // A 1 × 1 image whose deflate data holds 32 MiB of empty stored blocks before the block with its row: valid, and
        // copied whole into a growing buffer (beside the file) before the decoder found how little it decodes to.
        const int emptyBlocks = 32 * 1024 * 1024 / 5;
        byte[] row = [0, 10, 20, 30, 255];
        byte[] data = new byte[2 + emptyBlocks * 5 + 5 + row.Length + 4];
        data[0] = 0x78; data[1] = 0x01;
        for (int i = 0; i < emptyBlocks; i++) { data[2 + i * 5 + 3] = 0xFF; data[2 + i * 5 + 4] = 0xFF; }
        int last = 2 + emptyBlocks * 5;
        data[last] = 1; data[last + 1] = (byte)row.Length; data[last + 3] = (byte)~row.Length; data[last + 4] = 0xFF;
        row.CopyTo(data, last + 5);
        uint a = 1, b = 0;
        foreach (byte value in row) { a = (a + value) % 65521; b = (b + a) % 65521; }
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(last + 5 + row.Length), b << 16 | a);
        byte[] png = Png(1, 1, 8, 6, data);
        Assert.Equal([10, 20, 30, 255], PngDecoder.Decode(Png(1, 1, 8, 6, Compressed(row)), token: Token).Rgba);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var image = PngDecoder.Decode(png, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal([10, 20, 30, 255], image.Rgba);
        Assert.True(allocated < 1024 * 1024, $"Decoding a 1 × 1 image allocated {allocated:N0} bytes for its {data.Length:N0} bytes of image data.");
    }

    [Fact]
    public void ImageDataSplitAcrossChunksIsOneStream()
    {
        // Rows of a 3 × 2 RGB image, their deflate data cut at every length, with empty chunks between.
        byte[] rows = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];
        byte[] data = Compressed(rows);
        List<byte[]> chunks = [[]];
        for (int at = 0, size = 1; at < data.Length; at += size, size++) { chunks.Add(data[at..Math.Min(data.Length, at + size)]); chunks.Add([]); }
        var image = PngDecoder.Decode(Png(3, 2, 8, 2, [.. chunks]), token: Token);
        Assert.Equal([1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 1, 1, 1, 255, 2, 2, 2, 255, 3, 3, 3, 255], image.Rgba);

        // Cut short or followed by more pixels, it is refused as it was in one chunk.
        Assert.Contains("Truncated", Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Png(3, 2, 8, 2, data[..4], [], data[4..8]), token: Token)).Message);
        byte[] more = Compressed([.. rows, 0]);
        Assert.Contains("Excess", Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Png(3, 2, 8, 2, more[..3], more[3..]), token: Token)).Message);
    }
}
