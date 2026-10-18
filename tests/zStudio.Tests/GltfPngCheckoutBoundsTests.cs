using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core.Export;
using zStudio.Core.Gltf;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>
/// Malformed or oversized model inputs: glTF buffers bounded together, node transforms refused rather than read as
/// identity, PNG pixel data longer than its image, and Blender checkouts and updates that keep files of one name apart.
/// </summary>
public sealed class GltfPngCheckoutBoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Model = "data/m1/models/m1.gltf";

    private static byte[] Gltf(params JsonObject[] buffers) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["asset"] = new JsonObject { ["version"] = "2.0" },
        ["buffers"] = new JsonArray([.. buffers]),
    }.ToJsonString());
    private static JsonObject Buffer(string uri, long length) => new() { ["uri"] = uri, ["byteLength"] = length };

    [Fact]
    public void BuffersAreBoundedTogetherAndAFileListedAgainIsReadOnce()
    {
        // A file listed by a hundred buffers is read once and held once.
        int reads = 0;
        GltfDocument.Read(Gltf([.. Enumerable.Range(0, 100).Select(_ => Buffer("big.bin", 600))]), _ => { reads++; return new byte[600]; }, 1024, Token);
        Assert.Equal(1, reads);

        // Different files add up, whatever they declare: the second 600-byte file exceeds 1,024 bytes and nothing after it is read.
        reads = 0;
        var together = Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Gltf([.. Enumerable.Range(0, 100).Select(i => Buffer($"b{i}.bin", 1))]),
            _ => { reads++; return new byte[600]; }, 1024, Token));
        Assert.Contains("together", together.Message);
        Assert.Equal(2, reads);

        // A declared length beyond what is left is refused before its file is read.
        reads = 0;
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Gltf(Buffer("a.bin", 600), Buffer("b.bin", 600)), _ => { reads++; return new byte[600]; }, 1024, Token));
        Assert.Equal(1, reads);

        // Bytes a resolver hands back again (its own cache, under another spelling) count once.
        byte[] cached = new byte[600];
        GltfDocument.Read(Gltf(Buffer("a.bin", 600), Buffer("./a.bin", 1)), _ => cached, 1024, Token);

        // Data URIs count too, checked before they are decoded.
        string data = "data:application/octet-stream;base64," + Convert.ToBase64String(new byte[600]);
        Assert.Contains("together", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Gltf(Buffer(data, 600), Buffer(data, 600)), _ => [], 1024, Token)).Message);

        // The real bound: a buffer declaring more than 512 MiB is refused without reading a byte, and so is a file listing
        // more buffers than a model may use.
        static byte[] Never(string uri) => throw new Xunit.Sdk.XunitException($"{uri} was read.");
        Assert.Contains("512 MiB", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Gltf(Buffer("huge.bin", GltfDocument.MaximumBufferBytes + 1)), Never, Token)).Message);
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Gltf([.. Enumerable.Range(0, GltfDocument.MaximumBuffers + 1).Select(_ => Buffer("x.bin", 1))]), Never, Token));
    }

    private static string Node(string transform) =>
        $$"""{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"n"{{(transform.Length > 0 ? "," + transform : "")}}}]}""";

    [Theory]
    [InlineData("\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,5,6,7]")]
    [InlineData("\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,5,6,7,1,0]")]
    [InlineData("\"matrix\":{}")]
    [InlineData("\"matrix\":5")]
    [InlineData("\"matrix\":null")]
    [InlineData("\"translation\":null")]
    [InlineData("\"rotation\":null")]
    [InlineData("\"scale\":null")]
    [InlineData("\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],\"translation\":null")]
    [InlineData("\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,5,6,7,\"1\"]")]
    [InlineData("\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,5,6,7,1],\"translation\":[1,2,3]")]
    [InlineData("\"translation\":[1,2]")]
    [InlineData("\"translation\":[1,2,3,4]")]
    [InlineData("\"translation\":\"1 2 3\"")]
    [InlineData("\"translation\":[null,0,0]")]
    [InlineData("\"translation\":[1e39,0,0]")]
    [InlineData("\"rotation\":[0,0,0]")]
    [InlineData("\"scale\":[1,1,1,1]")]
    [InlineData("\"scale\":[1,1,\"x\"]")]
    public void MalformedNodeTransformsAreRefusedNotReadAsIdentity(string transform)
    {
        string json = Node(transform);
        var read = Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => [], Token));
        Assert.Contains("glTF node 0 (n)", read.Message);
        // Edits read node transforms from the JSON the same way.
        var node = JsonNode.Parse(json)!["nodes"]![0]!.AsObject();
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Local(node));
    }

    [Fact]
    public void WellFormedNodeTransformsReadAsBefore()
    {
        GltfNode Read(string transform) => GltfDocument.Read(Encoding.UTF8.GetBytes(Node(transform)), _ => [], Token).Roots.Single();
        Assert.Null(Read("").Matrix);
        Assert.Equal(new Vector3(5, 6, 7), Read("\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,5,6,7,1]").Matrix!.Value.Translation);
        Assert.Equal(Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(1, 2, 3), Read("\"translation\":[1,2,3],\"scale\":[2,2,2]").Matrix);
        // Values an edit wrote in memory (floats, doubles, integers) read as numbers.
        Assert.Equal(new Vector3(1, 2, 3), GltfNodeEdits.Local(new JsonObject { ["translation"] = new JsonArray(1f, 2.0, 3) }).Translation);
        Assert.True(GltfNodeEdits.Local(new JsonObject { ["name"] = "plain" }).IsIdentity);
    }

    /// <summary>A one-triangle textured model as zStudio writes it, its JSON and buffer.</summary>
    private static (JsonObject Json, byte[] Binary) Triangle()
    {
        GltfPrimitive primitive = new() { Material = new() { Name = "rock", ImageUri = "rock.png" } };
        primitive.Positions.AddRange([new(0, 0, 0), new(1, 0, 0), new(0, 0, -1)]); primitive.Indices.AddRange([0, 1, 2]);
        GltfMesh mesh = new() { Name = "tri" }; mesh.Primitives.Add(primitive);
        GltfDocument document = new(); document.Roots.Add(new GltfNode { Name = "n", Mesh = mesh });
        var (json, binary) = document.Write("m.bin");
        return (JsonNode.Parse(json)!.AsObject(), binary);
    }

    [Theory]
    [InlineData("node mesh")]
    [InlineData("primitive material")]
    [InlineData("material texture")]
    [InlineData("texture image")]
    [InlineData("texture sampler")]
    [InlineData("scene")]
    [InlineData("scene root")]
    [InlineData("base colour of 3")]
    [InlineData("base colour text")]
    public void ReferencesToNothingAndMalformedColoursAreRefusedNotDropped(string change)
    {
        var (json, binary) = Triangle();
        GltfDocument Read() => GltfDocument.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), _ => binary, Token);
        // As written, the model reads with its mesh, material, texture and sampler.
        var material = Read().Roots.Single().Mesh!.Primitives.Single().Material!;
        Assert.Equal("rock.png", material.ImageUri);
        var pbr = json["materials"]![0]!["pbrMetallicRoughness"]!.AsObject();
        switch (change)
        {
            case "node mesh": json["nodes"]![0]!["mesh"] = 99; break;
            case "primitive material": json["meshes"]![0]!["primitives"]![0]!["material"] = 99; break;
            case "material texture": pbr["baseColorTexture"]!["index"] = 99; break;
            case "texture image": json["textures"]![0]!["source"] = 99; break;
            case "texture sampler": json["textures"]![0]!["sampler"] = 99; break;
            case "scene": json["scene"] = 5; break;
            case "scene root": json["scenes"]![0]!["nodes"] = new JsonArray(99); break;
            case "base colour of 3": pbr["baseColorFactor"] = new JsonArray(1, 1, 1); break;
            default: pbr["baseColorFactor"] = new JsonArray(1, 1, 1, "x"); break;
        }
        Assert.Throws<InvalidDataException>(() => Read());
    }

    [Fact]
    public void AnUpdateWarningReadsAMalformedTransformItReplaces()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var after = JsonNode.Parse(workspace.Read(Model, Token)!)!.AsObject();
        var before = after.DeepClone().AsObject(); before["nodes"]![0]!["matrix"] = new JsonArray(1, 2, 3);
        // The project's file no build reads does not stop the Blender update that replaces it.
        Assert.Empty(SourceObjectEdits.ScriptTransformsReached(workspace, "m1", Model, Encoding.UTF8.GetBytes(before.ToJsonString()), Encoding.UTF8.GetBytes(after.ToJsonString()), [], Token));
    }

    /// <summary>A 1 × 1 RGBA PNG whose image data inflates to <paramref name="scanlines"/>.</summary>
    private static byte[] Png(byte[] scanlines)
    {
        using MemoryStream png = new();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string type, byte[] data)
        {
            byte[] number = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); png.Write(number);
            byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data]; png.Write(typed);
            BinaryPrimitives.WriteUInt32BigEndian(number, PngDecoder.Crc(typed)); png.Write(number);
        }
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        using MemoryStream compressed = new();
        using (ZLibStream z = new(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(scanlines);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return png.ToArray();
    }

    [Fact]
    public void PixelDataLongerThanTheImageIsRefused()
    {
        Assert.Equal([10, 20, 30, 255], PngDecoder.Decode(Png([0, 10, 20, 30, 255]), token: Token).Rgba);
        var excess = Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Png([0, 10, 20, 30, 255, 0, 1, 2, 3, 4]), token: Token));
        Assert.Contains("Excess", excess.Message);
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Png([0, 10, 20, 30]), token: Token));
    }
}
