using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound8Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task AsynchronousOpenedReadChecksItsLimitBeforeAllocating()
    {
        string path = Path.Combine(Path.GetTempPath(), "zstudio-read-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = File.Create(path)) file.SetLength(16 * 1024 * 1024);
            long before = GC.GetAllocatedBytesForCurrentThread();
            // The refusal happens before the first asynchronous read or result-buffer allocation.
            Task<byte[]> refused = SourceRead.AllAsync(path, 1024, Token);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 200_000);
            await Assert.ThrowsAsync<InvalidDataException>(() => refused);
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Equal(new byte[] { 1, 2, 3 }, await SourceRead.AllAsync(path, 3, Token));
        }
        finally { File.Delete(path); }
    }
    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
    private static GltfDocument Read(string json) => GltfDocument.Read(Text(json), _ => throw new FileNotFoundException(), Token);
    private static GltfDocument Triangle(int vertices = 3, int indices = 3, int targets = 0, bool tinted = false)
    {
        GltfPrimitive p = new() { Material = tinted ? new() { ImageUri = "rock.png", BaseColor = new(.5f, 1, 1, 1) } : null };
        for (int i = 0; i < vertices; i++) { p.Positions.Add(new(i % 2, i / 2, 0)); p.TexCoords.Add(Vector2.Zero); }
        p.Indices.AddRange(Enumerable.Range(0, indices).Select(i => i % vertices));
        for (int i = 0; i < targets; i++) p.Targets.Add(Enumerable.Repeat(Vector3.UnitZ, vertices).ToList());
        GltfMesh mesh = new() { Name = "shape" }; mesh.Primitives.Add(p);
        GltfDocument doc = new(); doc.Roots.Add(new() { Name = "one", Mesh = mesh }); return doc;
    }
    private static List<WorldNode> Import(GltfDocument doc) => WorldGltf.Import(doc, "data/model.gltf", 255,
        new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, _, _) => "rock" });

    [Theory]
    [InlineData("NORMAL")]
    [InlineData("TANGENT")]
    public void UnsupportedMorphChannelsCannotSilentlyBecomeZeroPositionDeltas(string channel)
    {
        var source = Triangle(); source.Roots[0].Mesh!.Primitives[0].Normals.AddRange(Enumerable.Repeat(Vector3.UnitY, 3));
        var (json, bin) = source.Write("mesh.bin"); var root = JsonNode.Parse(json)!;
        var primitive = root["meshes"]![0]!["primitives"]![0]!;
        primitive["targets"] = new JsonArray(new JsonObject { [channel] = primitive["attributes"]!["NORMAL"]!.DeepClone() });
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Text(root.ToJsonString()), _ => bin, Token));
    }
    [Fact]
    public void VertexColoursCannotSilentlyDisappearFromAnImportedModel()
    {
        var (json, bin) = Triangle().Write("mesh.bin"); var root = JsonNode.Parse(json)!;
        // The triangle's positions (0,0,0), (1,0,0), (0,1,0) are also legal black/red/green VEC3 float colours.
        var attributes = root["meshes"]![0]!["primitives"]![0]!["attributes"]!;
        attributes["COLOR_0"] = attributes["POSITION"]!.DeepClone();
        Assert.Contains("vertex colours", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Text(root.ToJsonString()), _ => bin, Token)).Message);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GltfAnimationAndSkinningCannotSilentlyBecomeStaticGeometry(bool skin)
    {
        var (json, bin) = Triangle().Write("mesh.bin"); var root = JsonNode.Parse(json)!;
        var accessors = root["accessors"]!.AsArray(); var views = root["bufferViews"]!.AsArray();
        var attributes = root["meshes"]![0]!["primitives"]![0]!["attributes"]!;
        int first = accessors.Count;
        byte[] payload = new byte[skin ? 60 : 12];
        int AddAccessor(int offset, int length, string type, int component)
        {
            int view = views.Count; views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length + offset, ["byteLength"] = length });
            accessors.Add(new JsonObject { ["bufferView"] = view, ["componentType"] = component, ["count"] = 3, ["type"] = type });
            return accessors.Count - 1;
        }
        if (skin)
        {
            attributes["JOINTS_0"] = AddAccessor(0, 12, "VEC4", 5121);
            attributes["WEIGHTS_0"] = AddAccessor(12, 48, "VEC4", 5126);
            for (int i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(12 + 16 * i), 1);
            root["nodes"]![0]!["skin"] = 0;
            root["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "joint", ["translation"] = new JsonArray(1, 0, 0) });
            root["scenes"]![0]!["nodes"]!.AsArray().Add(1);
            root["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(1) });
        }
        else
        {
            AddAccessor(0, 12, "SCALAR", 5126);
            for (int i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4 * i), i);
            accessors[first]!["min"] = new JsonArray(0); accessors[first]!["max"] = new JsonArray(2);
            root["animations"] = new JsonArray(new JsonObject
            {
                ["samplers"] = new JsonArray(new JsonObject { ["input"] = first, ["output"] = attributes["POSITION"]!.DeepClone() }),
                ["channels"] = new JsonArray(new JsonObject { ["sampler"] = 0, ["target"] = new JsonObject { ["node"] = 0, ["path"] = "translation" } })
            });
        }
        byte[] data = [.. bin, .. payload]; root["buffers"]![0]!["byteLength"] = data.Length;
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Text(root.ToJsonString()), _ => data, Token));
    }

    [Fact]
    public void ModelImportAccountsForGeometryAlreadyInTheWorld()
    {
        GameZWorld world = new(); WorldModel existing = new(); existing.Vertices.AddRange(Enumerable.Repeat(Vector3.Zero, 1024));
        for (int i = 0; i < 1024; i++) world.Models.Add(existing);
        WorldGltf.ImportContext context = new() { World = world, Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, _, _) => "rock", Token = Token };
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(Triangle(), "data/model.gltf", 255, context));
        Assert.Equal(1024, world.Models.Count);
    }

    [Theory]
    [InlineData("3.0", 3)] [InlineData("3e0", 3)] [InlineData("300e-2", 3)]
    [InlineData("-0e99999999", 0)] [InlineData("2147483647.000", int.MaxValue)]
    public void IntegersAcceptExactDecimalAndExponentForms(string value, int expected) => Assert.Equal(expected, GltfInteger.Int32(JsonNode.Parse(value)));

    [Fact]
    public void SharedCopiesCannotDisagreeInNodeMorphWeights()
    {
        var doc = Triangle(targets: 1); var first = doc.Roots[0];
        first.Extras = new JsonObject { ["recoil"] = new JsonObject { ["instance"] = 1 } }; first.Weights.Add(0);
        GltfNode second = new() { Name = first.Name, Mesh = first.Mesh, Extras = first.Extras.DeepClone().AsObject() }; second.Weights.Add(1); doc.Roots.Add(second);
        Assert.Contains("morph weights", Assert.Throws<InvalidDataException>(() => WorldGltf.CheckInstances(doc, "shared.gltf")).Message);
        second.Weights[0] = 0; WorldGltf.CheckInstances(doc, "shared.gltf");
    }

    [Fact]
    public void EngineIdentityAndZoneNeverRoundFractionalNumbers()
    {
        var doc = Triangle();
        doc.Roots[0].Extras = JsonNode.Parse("""{"recoil":{"instance":1.0000000000000000000001}}""")!.AsObject();
        Assert.Throws<InvalidDataException>(() => Import(doc));
        Assert.Null(WorldGltf.StatedZone(JsonNode.Parse("""{"zone":3.000000000000000000001}""")));
        var root = JsonNode.Parse("""{"nodes":[{"extras":{"recoil":{"instance":1}}},{"extras":{"recoil":{"instance":1.0000000000000000000001}}}]}""")!.AsObject();
        Assert.Equal([0], GltfNodeEdits.InstanceCopies(root, 0));
        root["nodes"]![1]!["extras"]!["recoil"]!["instance"] = JsonNode.Parse("1e0");
        Assert.Equal([0, 1], GltfNodeEdits.InstanceCopies(root, 0));
    }

    [Theory]
    [InlineData("3.1")] [InlineData("3.0000000000000000000000000000000001")]
    [InlineData("1e-99999")] [InlineData("1e99999")] [InlineData("2147483648")]
    [InlineData("\"3\"")] [InlineData("true")] [InlineData("null")]
    public void IntegersNeverRoundOrCoerce(string value) => Assert.Throws<InvalidDataException>(() => GltfInteger.Int32(JsonNode.Parse(value)));

    [Fact]
    public void EveryIntegerInAWrittenModelCanUseExponentNotationAndRemainEditable()
    {
        var (json, bin) = Triangle().Write("mesh.bin"); var root = JsonNode.Parse(json)!.AsObject();
        void Rewrite(JsonNode node)
        {
            if (node is JsonObject obj) foreach (var (key, child) in obj.ToArray())
                if (child is JsonValue v && v.TryGetValue(out int n)) obj[key] = JsonNode.Parse(n + "e0"); else if (child != null) Rewrite(child);
            if (node is JsonArray arr) for (int i = 0; i < arr.Count; i++)
                if (arr[i] is JsonValue v && v.TryGetValue(out int n)) arr[i] = JsonNode.Parse(n + ".0"); else if (arr[i] != null) Rewrite(arr[i]!);
        }
        Rewrite(root);
        var doc = GltfDocument.Read(Text(root.ToJsonString()), _ => bin, Token);
        Assert.Equal(3, doc.Roots.Single().Mesh!.Primitives.Single().Indices.Count);
        int copy = GltfNodeEdits.Duplicate(root, 0, "two"); Assert.Equal(1, copy);
        GltfNodeEdits.Remove(root, 0);
        Assert.Equal("two", GltfDocument.Read(Text(root.ToJsonString()), _ => bin, Token).Roots.Single().Name);
    }

    private static byte[] Glb(params (uint Type, byte[] Bytes)[] chunks)
    {
        byte[] file = new byte[12 + chunks.Sum(c => 8 + c.Bytes.Length)]; "glTF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 2); BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)file.Length);
        int offset = 12;
        foreach (var chunk in chunks) { BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(offset), chunk.Bytes.Length); BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(offset + 4), chunk.Type); chunk.Bytes.CopyTo(file, offset + 8); offset += 8 + chunk.Bytes.Length; }
        return file;
    }
    private static byte[] JsonChunk(string json = "{\"asset\":{\"version\":\"2.0\"}}") => Text(json.PadRight((json.Length + 3) / 4 * 4));
    [Fact]
    public void GlbValidatesLengthFramingOrderAndDuplicateChunks()
    {
        byte[] json = JsonChunk(), valid = Glb((0x4E4F534A, json));
        GltfDocument.Read(valid, _ => [], Token);
        GltfDocument.Read(Glb((0x4E4F534A, json), (0x12345678, new byte[4])), _ => [], Token);
        List<byte[]> bad = [Glb((0x4E4F534A, json), (0x4E4F534A, json)), Glb((0x004E4942, new byte[4]), (0x4E4F534A, json)), Glb((0x4E4F534A, json), (0x004E4942, new byte[4]), (0x004E4942, new byte[4])), Glb((0x4E4F534A, json), (0x12345678, new byte[3]))];
        for (int n = 1; n <= 7; n++) { byte[] trailing = [.. valid, .. new byte[n]]; BinaryPrimitives.WriteUInt32LittleEndian(trailing.AsSpan(8), (uint)trailing.Length); bad.Add(trailing); }
        byte[] length = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(length.AsSpan(8), (uint)(length.Length + 4)); bad.Add(length);
        foreach (byte[] bytes in bad) Assert.Throws<InvalidDataException>(() => GltfDocument.Read(bytes, _ => [], Token));
    }

    [Fact]
    public void GlbBinaryIsDecodedInPlaceAndUnusedBinaryIsNotCopied()
    {
        var (json, bin) = Triangle().Write("mesh.bin"); var root = JsonNode.Parse(json)!.AsObject(); root["buffers"]![0]!.AsObject().Remove("uri");
        var glb = Glb((0x4E4F534A, JsonChunk(root.ToJsonString())), (0x004E4942, [.. bin, .. new byte[(4 - bin.Length % 4) % 4]]));
        Assert.Equal(3, GltfDocument.Read(glb, _ => [], Token).Roots[0].Mesh!.Primitives[0].Positions.Count);
        byte[] unused = Glb((0x4E4F534A, JsonChunk()), (0x004E4942, new byte[4 * 1024 * 1024]));
        GltfDocument.Read(unused, _ => [], Token);
        long before = GC.GetAllocatedBytesForCurrentThread(); GltfDocument.Read(unused, _ => [], Token);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 512 * 1024);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(5)]
    public void TriangleListsNeverDiscardIndices(int count)
    {
        var (json, bin) = Triangle(indices: count).Write("mesh.bin");
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, _ => bin, Token));
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void ImplicitTriangleListsMustBeComplete(int count)
    {
        var (json, bin) = Triangle(vertices: count).Write("mesh.bin"); var root = JsonNode.Parse(json)!;
        root["meshes"]![0]!["primitives"]![0]!.AsObject().Remove("indices");
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Text(root.ToJsonString()), _ => bin, Token));
    }

    [Theory]
    [InlineData("[1,1,1,1]")] [InlineData("[0,0,0,0]")] [InlineData("[0,0,0,1.001]")]
    public void NonUnitQuaternionsAreRefused(string q) => Assert.Throws<InvalidDataException>(() => Read("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"rotation\":" + q + "}]}"));
    [Fact]
    public void RoundedUnitQuaternionsRemainPureRotations()
    {
        var doc = Read("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"rotation\":[0,0.707107,0,0.707107]}]}");
        Assert.InRange(doc.Roots[0].Matrix!.Value.GetDeterminant(), .99999f, 1.00001f);
    }

    [Fact]
    public void UnsupportedMorphsAndTextureTintsFailBothPreflightAndImport()
    {
        foreach (var doc in new[] { Triangle(targets: 2), Triangle(tinted: true) })
        {
            Assert.Throws<InvalidDataException>(() => WorldGltf.ValidateSupported(doc, "mesh.gltf"));
            Assert.Throws<InvalidDataException>(() => Import(doc));
        }
        Assert.Single(Import(Triangle(targets: 1)));
    }
    [Fact]
    public void NodeWeightsOverrideMeshDefaultsWithoutChangingOtherInstances()
    {
        var original = Triangle(targets: 1); var mesh = original.Roots[0].Mesh!; mesh.Weights.Add(0);
        original.Roots.Add(new() { Name = "two", Mesh = mesh }); original.Roots[0].Weights.Add(1);
        var (json, bin) = original.Write("mesh.bin");
        var nodes = Import(GltfDocument.Read(json, _ => bin, Token));
        Assert.Equal(1, nodes[0].Model!.MorphFactor); Assert.Equal(0, nodes[1].Model!.MorphFactor); Assert.NotSame(nodes[0].Model, nodes[1].Model);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IendMustBeEmptyAndFinal(bool data)
    {
        byte[] png = PngEncoder.Encode(new(1, 1, [1, 2, 3, 255]), Token);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, PngDecoder.Decode(png, token: Token).Rgba);
        byte[] bad;
        if (!data) bad = [.. png, 0];
        else
        {
            bad = new byte[png.Length + 1]; png.AsSpan(0, png.Length - 12).CopyTo(bad);
            int at = png.Length - 12; BinaryPrimitives.WriteInt32BigEndian(bad.AsSpan(at), 1); "IEND"u8.CopyTo(bad.AsSpan(at + 4));
            uint crc = uint.MaxValue; foreach (byte b in bad.AsSpan(at + 4, 5)) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
            BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(at + 9), ~crc);
        }
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(bad, token: Token));
    }

    [Fact]
    public void BoundedReadsUseTheOpenedLengthAndNeverGrowTheirAllocation()
    {
        using var stream = new MemoryStream(new byte[65]);
        Assert.Throws<InvalidDataException>(() => SourceRead.All(stream, 64, "replaced source", Token)); Assert.Equal(0, stream.Position);
        using var growing = new ChangedLengthStream(1, new byte[65]);
        Assert.Throws<IOException>(() => SourceRead.All(growing, 64, "growing source", Token)); Assert.Equal(2, growing.Position);
        using var shrinking = new ChangedLengthStream(65, new byte[1]);
        Assert.Throws<IOException>(() => SourceRead.All(shrinking, 128, "shrinking source", Token));
    }
    private sealed class ChangedLengthStream(long length, byte[] bytes) : MemoryStream(bytes) { public override long Length => length; }
}
