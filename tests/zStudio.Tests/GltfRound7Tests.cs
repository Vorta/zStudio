using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// glTF data laid out where the specification places it (strides of 4 to 252 bytes in steps of 4 that hold an element and
/// fit the view, offsets at multiples of the component size, vertex attribute elements on 4-byte boundaries, no stride for
/// indices) and texture wrap modes RECOIL can store: repeat or clamp, never mirrored repeat or a mode glTF does not define.
/// </summary>
public sealed class GltfRound7Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A file of one triangle (float positions in accessor 0) declaring KHR_mesh_quantization, its other views added in turn to one buffer.</summary>
    private sealed class Model
    {
        private readonly List<byte> buffer = [];
        private readonly JsonArray views = [], accessors = [];
        public JsonObject Attributes { get; } = [];
        public JsonObject Primitive { get; }
        public JsonObject Root { get; } = new() { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["extensionsUsed"] = new JsonArray("KHR_mesh_quantization") };

        public Model()
        {
            Attributes["POSITION"] = Accessor(Floats(0, 0, 0, 1, 0, 0, 0, 1, 0), 5126, "VEC3", 3);
            Primitive = new() { ["attributes"] = Attributes };
        }

        /// <summary>A view of <paramref name="hex"/> starting <paramref name="misalign"/> bytes past a 4-byte boundary of the buffer.</summary>
        public int View(string hex, int? stride = null, int misalign = 0)
        {
            while (buffer.Count % 4 != 0) buffer.Add(0);
            for (int i = 0; i < misalign; i++) buffer.Add(0);
            byte[] data = Convert.FromHexString(hex);
            JsonObject view = new() { ["buffer"] = 0, ["byteOffset"] = buffer.Count, ["byteLength"] = data.Length };
            if (stride is { } s) view["byteStride"] = s;
            views.Add(view); buffer.AddRange(data);
            return views.Count - 1;
        }
        /// <summary>An accessor reading <paramref name="hex"/> from <paramref name="offset"/> of its own view.</summary>
        public int Accessor(string hex, int componentType, string type, int count, bool normalized = false, int? stride = null, int offset = 0, int misalign = 0)
        {
            JsonObject accessor = new() { ["bufferView"] = View(hex, stride, misalign), ["componentType"] = componentType, ["count"] = count, ["type"] = type };
            if (offset != 0) accessor["byteOffset"] = offset;
            if (normalized) accessor["normalized"] = true;
            accessors.Add(accessor);
            return accessors.Count - 1;
        }
        /// <summary>Three zero float vectors of which the sparse elements at <paramref name="indices"/> are replaced by <paramref name="values"/>.</summary>
        public int Sparse(int indexType, string indices, string values, int replaced, int indexOffset = 0, int valueOffset = 0, int indexMisalign = 0)
        {
            JsonObject indexInfo = new() { ["bufferView"] = View(indices, misalign: indexMisalign), ["componentType"] = indexType }, valueInfo = new() { ["bufferView"] = View(values) };
            if (indexOffset != 0) indexInfo["byteOffset"] = indexOffset;
            if (valueOffset != 0) valueInfo["byteOffset"] = valueOffset;
            accessors.Add(new JsonObject { ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3", ["sparse"] = new JsonObject { ["count"] = replaced, ["indices"] = indexInfo, ["values"] = valueInfo } });
            return accessors.Count - 1;
        }
        /// <summary>Material 0 (rock), showing rock.png through texture 0 with <paramref name="sampler"/> (none when null).</summary>
        public void Textured(JsonNode? sampler)
        {
            Root["materials"] = new JsonArray(new JsonObject { ["name"] = "rock", ["pbrMetallicRoughness"] = new JsonObject { ["metallicFactor"] = 0, ["baseColorTexture"] = new JsonObject { ["index"] = 0 } } });
            JsonObject texture = new() { ["source"] = 0 };
            if (sampler != null) { texture["sampler"] = 0; Root["samplers"] = new JsonArray(sampler); }
            Root["textures"] = new JsonArray(texture);
            Root["images"] = new JsonArray(new JsonObject { ["uri"] = "rock.png" });
            Primitive["material"] = 0;
        }

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
    }

    private static string Floats(params float[] values)
    {
        byte[] data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), values[i]);
        return Convert.ToHexString(data);
    }

    /// <summary>Puts accessor <paramref name="accessor"/> to <paramref name="use"/> in the model's triangle.</summary>
    private static void Use(Model model, string use, int accessor)
    {
        if (use == "indices") model.Primitive["indices"] = accessor;
        else if (use == "target") model.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = accessor });
        else model.Attributes[use] = accessor;
    }

    [Theory]
    // glTF has a stated stride be a multiple of 4 from 4 to 252 bytes, no longer than its view: these were read from offsets
    // that other readers refuse (a byte normal every 3 bytes, a short texture coordinate pair every 5).
    [InlineData("NORMAL", 5120, "VEC3", true, 3, "007F00" + "007F00" + "007F00", "byte stride of 3")]
    [InlineData("TEXCOORD_0", 5122, "VEC2", false, 5, "0000040000" + "0000040000" + "04000400", "byte stride of 5")]
    [InlineData("POSITION", 5126, "VEC3", false, 14, "000000000000000000000000" + "0000" + "0000803F00000000000000000000" + "000000000000803F00000000", "byte stride of 14")]
    // A stride of 16 for one 12-byte position, in a 12-byte view: no element lies past the view, but the stride does.
    [InlineData("POSITION", 5126, "VEC3", false, 16, "000000000000000000000000", "byte stride of 16")]
    public void AStatedStrideMustBeAMultipleOfFourThatFitsTheView(string use, int componentType, string type, bool normalized, int stride, string hex, string message)
    {
        Model model = new();
        int count = use == "POSITION" && stride == 16 ? 1 : 3;
        Use(model, use, model.Accessor(hex, componentType, type, count, normalized, stride));
        if (count == 1) model.Primitive["indices"] = model.Accessor("000000", 5121, "SCALAR", 3);
        var refused = model.Refused();
        Assert.Contains(message, refused.Message);
        Assert.Contains("glTF needs a multiple of 4 from 4 to 252", refused.Message);
    }

    [Fact]
    public void EachElementIsReadAtItsStride()
    {
        // The bytes between elements (AA) are another attribute's, never this one's.
        Model model = new();
        model.Attributes["NORMAL"] = model.Accessor("007F00AA" + "007F00AA" + "007F00AA", 5120, "VEC3", 3, normalized: true, stride: 4);
        model.Attributes["TEXCOORD_0"] = model.Accessor("FF00AAAA" + "00FFAAAA" + "FFFF", 5121, "VEC2", 3, normalized: true, stride: 4);
        var read = model.Read();
        Assert.All(read.Normals, n => Assert.Equal(Vector3.UnitY, n));
        Assert.Equal([new(1, 0), new(0, 1), new(1, 1)], read.TexCoords);

        Model positions = new();
        positions.Attributes["POSITION"] = positions.Accessor("000000000000AAAA" + "020000000000AAAA" + "000002000000", 5122, "VEC3", 3, stride: 8);
        Assert.Equal([Vector3.Zero, new(2, 0, 0), new(0, 2, 0)], positions.Read().Positions);
    }

    [Fact]
    public void IndicesAreNeverReadWithAStride()
    {
        // glTF has indices tightly packed: a reader honouring the stride took every other short (0, 1, 2 here), one ignoring it 0, AAAA, 1.
        Model model = new();
        model.Primitive["indices"] = model.Accessor("0000AAAA" + "0100AAAA" + "0200", 5123, "SCALAR", 3, stride: 4);
        var refused = model.Refused();
        Assert.Contains("glTF buffer view 1 states a byte stride, but accessor 1 reads indices from it", refused.Message);
        Assert.Contains("remove the view's byteStride", refused.Message);
    }

    [Theory]
    // glTF has an accessor start at a multiple of its component size in its view, and the view at one in its buffer.
    [InlineData("POSITION", 5126, "VEC3", 2, 0, "move them to a multiple of 4")]
    [InlineData("POSITION", 5126, "VEC3", 0, 2, "starts at byte 38 of buffer 0")]
    [InlineData("indices", 5123, "SCALAR", 1, 0, "them to start at a multiple of 2, the size of their components")]
    [InlineData("indices", 5123, "SCALAR", 0, 1, "starts at byte 37 of buffer 0")]
    // A vertex attribute's elements start on 4-byte boundaries of their view, whatever their component size.
    [InlineData("TEXCOORD_0", 5122, "VEC2", 2, 0, "a vertex attribute's elements to start on 4-byte boundaries of their view")]
    public void DataStartsWhereItsComponentsAlign(string use, int componentType, string type, int offset, int misalign, string message)
    {
        Model model = new();
        string data = componentType switch
        {
            5126 => Floats(0, 0, 0, 1, 0, 0, 0, 1, 0),
            5123 => "000001000200",
            _ => "00000400" + "00000000" + "04000400",
        };
        string hex = new string('0', offset * 2) + data;
        Use(model, use, model.Accessor(hex, componentType, type, 3, stride: use == "TEXCOORD_0" ? 4 : null, offset: offset, misalign: misalign));
        Assert.Contains(message, model.Refused().Message);
    }

    [Fact]
    public void IndicesAlignToTheirComponentsOnly()
    {
        // Indices are not vertex attributes: shorts at byte 2 of their view (and bytes at byte 1) are where glTF allows them.
        Model shorts = new();
        shorts.Primitive["indices"] = shorts.Accessor("AAAA" + "020001000000", 5123, "SCALAR", 3, offset: 2);
        Assert.Equal([2, 1, 0], shorts.Read().Indices);
        Model bytes = new();
        bytes.Primitive["indices"] = bytes.Accessor("AA" + "000102", 5121, "SCALAR", 3, offset: 1);
        Assert.Equal([0, 1, 2], bytes.Read().Indices);
    }

    [Theory]
    // Without a stated stride, elements of a size that is not a multiple of 4 would not start on 4-byte boundaries, which
    // glTF requires of vertex attributes (KHR_mesh_quantization: a byte normal takes 4 bytes, not 3).
    [InlineData("NORMAL", 5120, "VEC3", true, "007F00" + "007F00" + "007F00", 3, 4)]
    [InlineData("TEXCOORD_0", 5121, "VEC2", true, "FF00" + "00FF" + "FFFF", 2, 4)]
    [InlineData("POSITION", 5122, "VEC3", false, "000000000000" + "020000000000" + "000002000000", 6, 8)]
    [InlineData("target", 5122, "VEC3", false, "FFFF00000000" + "000000000000" + "000000000100", 6, 8)]
    public void VertexAttributesOfOtherSizesNeedAStride(string use, int componentType, string type, bool normalized, string hex, int element, int padded)
    {
        Model model = new();
        Use(model, use, model.Accessor(hex, componentType, type, 3, normalized));
        var refused = model.Refused();
        Assert.Contains($"states no byte stride, so glTF accessor 1's", refused.Message);
        Assert.Contains($"whose elements take {element} bytes, would lie {element} bytes apart", refused.Message);
        Assert.Contains($"pad each element to {padded} bytes and state a byteStride of {padded}", refused.Message);
    }

    [Theory]
    // Sparse indices start at a multiple of their size, sparse values at one of their components' size, in the view and the buffer.
    [InlineData(5123, "00000200", 1, 0, 0, "sparse indices of glTF accessor 1 start at byte 1 of glTF buffer view")]
    [InlineData(5123, "00000200", 0, 0, 1, "starts at byte 37 of buffer 0, but it holds the sparse indices of glTF accessor 1")]
    [InlineData(5121, "0002", 0, 2, 0, "sparse values of glTF accessor 1 start at byte 2 of glTF buffer view")]
    public void SparseDataStartsWhereItsComponentsAlign(int indexType, string indices, int indexOffset, int valueOffset, int indexMisalign, string message)
    {
        Model model = new();
        string values = new string('0', valueOffset * 2) + Floats(0, 1, 0, 0, 2, 0);
        if (indexOffset > 0) indices = new string('0', indexOffset * 2) + indices;
        model.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = model.Sparse(indexType, indices, values, 2, indexOffset, valueOffset, indexMisalign) });
        Assert.Contains(message, model.Refused().Message);
    }

    [Fact]
    public void AlignedSparseDataIsRead()
    {
        Model model = new();
        model.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = model.Sparse(5123, "AAAA" + "00000200", Floats(0, 1, 0, 0, 2, 0), 2, indexOffset: 2) });
        Assert.Equal([new(0, 1, 0), Vector3.Zero, new(0, 2, 0)], model.Read().Targets.Single());
    }

    [Fact]
    public void WrittenModelsKeepTheirLayoutAndWrapModes()
    {
        // What zStudio writes (reconstructed models: float attributes, short indices after which the next view is padded)
        // reads back with its edge modes.
        GltfMaterial clamped = new() { Name = "rock", ImageUri = "rock.png", ClampS = true }, repeated = new() { Name = "sand", ImageUri = "sand.png", ClampT = true };
        GltfMesh mesh = new();
        foreach (var material in (GltfMaterial[])[clamped, repeated])
        {
            GltfPrimitive primitive = new() { Material = material };
            primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitY]);
            primitive.Normals.AddRange([Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ]);
            primitive.TexCoords.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]);
            primitive.Indices.AddRange([0, 1, 2]);
            mesh.Primitives.Add(primitive);
        }
        GltfDocument document = new(); document.Roots.Add(new GltfNode { Name = "n", Mesh = mesh });
        var (json, binary) = document.Write("n.bin");
        var read = GltfDocument.Read(json, uri => uri == "n.bin" ? binary : throw new FileNotFoundException(uri), Token).Roots[0].Mesh!.Primitives;
        Assert.Equal([(true, false), (false, true)], read.Select(p => (p.Material!.ClampS, p.Material.ClampT)));
        Assert.All(read, p => Assert.Equal([0, 1, 2], p.Indices));
    }

    [Theory]
    [InlineData("wrapS", "horizontally")]
    [InlineData("wrapT", "vertically")]
    public void MirroredRepeatIsRefusedWithWhatToChangeInBlender(string property, string axis)
    {
        // A RECOIL texture repeats or clamps on each axis (the Direct3D texture record's WRAP or CLAMP): mirrored repeat,
        // which flips every other tile, was built as a plain repeat. Blender writes it for an image's Mirror extension.
        Model model = new();
        model.Attributes["TEXCOORD_0"] = model.Accessor(Floats(0, 0, 2, 0, 0, 2), 5126, "VEC2", 3);
        model.Textured(new JsonObject { [property] = 33648 });
        var refused = model.Refused();
        Assert.Contains($"glTF material 0 (rock)'s base-colour texture uses sampler 0, which mirrors every other tile {axis} ({property} MIRRORED_REPEAT)", refused.Message);
        Assert.Contains("In Blender, set the Image Texture node's extension to Repeat (or Extend, to clamp) instead of Mirror", refused.Message);
    }

    [Theory]
    // glTF defines three wrap modes; any other value was built as a repeat.
    [InlineData("12345", "12345")]
    [InlineData("0", "0")]
    [InlineData("\"33071\"", "33071")]
    [InlineData("33071.5", "33071.5")]
    [InlineData("[33071]", "a list")]
    public void AWrapModeGltfDoesNotDefineIsRefused(string value, string shown)
    {
        Model model = new();
        model.Textured(new JsonObject { ["wrapT"] = JsonNode.Parse(value) });
        var refused = model.Refused();
        Assert.Contains($"uses sampler 0, whose wrapT is {shown}; glTF wraps a texture with 10497 (REPEAT), 33071 (CLAMP_TO_EDGE) or 33648 (MIRRORED_REPEAT)", refused.Message);
    }

    [Fact]
    public void RepeatAndClampAreKeptAndFiltersAreNotRead()
    {
        // The engine filters every texture alike, so a sampler's filters (Blender's Closest writes NEAREST) neither refuse
        // the file nor change what is built, even ones glTF does not define.
        (bool, bool) Wraps(JsonNode? sampler)
        {
            Model model = new(); model.Textured(sampler);
            var material = model.Read().Material!;
            return (material.ClampS, material.ClampT);
        }
        Assert.Equal((true, false), Wraps(new JsonObject { ["magFilter"] = 9728, ["minFilter"] = 9984, ["wrapS"] = 33071, ["wrapT"] = 10497 }));
        Assert.Equal((false, true), Wraps(new JsonObject { ["magFilter"] = 1234, ["minFilter"] = 5678, ["wrapT"] = 33071 }));
        Assert.Equal((false, false), Wraps(new JsonObject()));
        Assert.Equal((false, false), Wraps(null));
    }

    [Fact]
    public void ASamplerThatIsNotAnObjectIsRefused()
    {
        Model model = new();
        model.Textured(JsonValue.Create(7));
        Assert.Contains("glTF sampler 0 is not an object", model.Refused().Message);
    }
}
