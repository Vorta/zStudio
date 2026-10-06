using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Accessors that reach past their buffer views, PNG transparency and chunk order other decoders read differently, and
/// Blender checkouts bounded and read as one state of the project.
/// </summary>
public sealed class GltfPngCheckoutRound3Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Model = "data/m1/models/m1.gltf";

    /// <summary>
    /// A triangle with normals and a sparse morph target, its data in one 100-byte buffer: positions (view 0, bytes 0–35),
    /// normals (view 1, 36–71), the target's two sparse indices (view 2, 72–73) and their values (view 3, 76–99).
    /// </summary>
    private static JsonObject Triangle()
    {
        byte[] data = new byte[100];
        float[] floats = [0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 1, 0, 0, 1, 0, 0, 1, 0];
        for (int i = 0; i < floats.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), floats[i]);
        data[72] = 0; data[73] = 2;
        float[] moved = [0, 2, 0, 0, 3, 0];
        for (int i = 0; i < moved.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(76 + i * 4), moved[i]);
        static JsonObject View(int offset, int length) => new() { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length };
        return new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["name"] = "tri", ["mesh"] = 0 }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = 0, ["NORMAL"] = 1 },
                    ["targets"] = new JsonArray(new JsonObject { ["POSITION"] = 2 }),
                }),
                ["weights"] = new JsonArray(0.5f),
            }),
            ["accessors"] = new JsonArray(
                new JsonObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3" },
                new JsonObject { ["bufferView"] = 1, ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3" },
                new JsonObject
                {
                    ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3",
                    ["sparse"] = new JsonObject { ["count"] = 2, ["indices"] = new JsonObject { ["bufferView"] = 2, ["componentType"] = 5121 }, ["values"] = new JsonObject { ["bufferView"] = 3 } },
                }),
            ["bufferViews"] = new JsonArray(View(0, 36), View(36, 36), View(72, 2), View(76, 24)),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = 100, ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(data) }),
        };
    }
    private static GltfDocument Read(JsonObject json) => GltfDocument.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), _ => throw new FileNotFoundException(), Token);

    [Fact]
    public void AccessorsWithinTheirViewsRead()
    {
        var primitive = Read(Triangle()).Roots.Single().Mesh!.Primitives.Single();
        Assert.Equal([new(0, 0, 0), new(1, 0, 0), new(0, 0, -1)], primitive.Positions);
        Assert.All(primitive.Normals, n => Assert.Equal(Vector3.UnitY, n));
        Assert.Equal([new(0, 2, 0), Vector3.Zero, new(0, 3, 0)], primitive.Targets.Single());
    }

    [Theory]
    // The normals start a corner late in the positions' view: their last corner is the normals view's first bytes.
    [InlineData("attribute past its view", "of glTF buffer view 0, which holds 36")]
    // A stride the positions' view cannot hold for three corners, though the buffer holds the bytes.
    [InlineData("stride past its view", "of glTF buffer view 0, which holds 36")]
    // The buffer states 36 bytes; its data holds 100, but the normals' view lies past what it states.
    [InlineData("view past its buffer", "does not lie within buffer 0, which holds 36")]
    // Two sparse indices of one byte each in a one-byte view, the next byte of the buffer padding.
    [InlineData("sparse indices past their view", "sparse indices of glTF accessor 2 need 2 bytes")]
    // Two moved corners in a view of one.
    [InlineData("sparse values past their view", "sparse values of glTF accessor 2 need 24 bytes")]
    // Sparse data is tightly packed: a stride there would be ignored, reading other values than the file states.
    [InlineData("sparse view with a stride", "states a byte stride")]
    [InlineData("view without a length", "has no byteLength")]
    public void AccessorsReadingPastTheirViewsAreRefused(string change, string message)
    {
        var json = Triangle();
        var views = json["bufferViews"]!.AsArray();
        var accessors = json["accessors"]!.AsArray();
        switch (change)
        {
            case "attribute past its view": accessors[1]!["bufferView"] = 0; accessors[1]!["byteOffset"] = 12; break;
            case "stride past its view": views[0]!["byteStride"] = 24; break;
            case "view past its buffer": json["buffers"]![0]!["byteLength"] = 36; break;
            case "sparse indices past their view": views[2]!["byteLength"] = 1; break;
            case "sparse values past their view": views[3]!["byteLength"] = 12; break;
            case "sparse view with a stride": views[2]!["byteStride"] = 4; break;
            default: views[0]!.AsObject().Remove("byteLength"); break;
        }
        var refused = Assert.Throws<InvalidDataException>(() => Read(json));
        Assert.Contains(message, refused.Message);
    }

    /// <summary>A PNG of the given header fields (bit depth, colour type) and chunks, its rows compressed into one IDAT unless the chunks hold their own.</summary>
    private static byte[] Png(int width, int height, int depth, int colorType, byte[] rows, params (string Type, byte[] Data)[] chunks)
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
        foreach (var (type, data) in chunks) Chunk(type, type == "IDAT" && data.Length == 0 ? Compressed(rows) : data);
        if (!chunks.Any(c => c.Type == "IDAT")) Chunk("IDAT", Compressed(rows));
        Chunk("IEND", []);
        return png.ToArray();
    }
    private static byte[] Compressed(byte[] rows)
    {
        using MemoryStream compressed = new();
        using (ZLibStream z = new(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(rows);
        return compressed.ToArray();
    }
    /// <summary>The image data in one IDAT chunk at this place.</summary>
    private static readonly (string, byte[]) Data = ("IDAT", []);
    private static readonly byte[] Palette = [255, 0, 0, 0, 0, 255];

    [Fact]
    public void WellFormedTransparencyReads()
    {
        // Greyscale: the 2-byte value 20 is transparent.
        Assert.Equal([10, 10, 10, 255, 20, 20, 20, 0], PngDecoder.Decode(Png(2, 1, 8, 0, [0, 10, 20], ("tRNS", [0, 20])), token: Token).Rgba);
        // RGB: the 6-byte value 1, 2, 3 is transparent.
        Assert.Equal([1, 2, 3, 0], PngDecoder.Decode(Png(1, 1, 8, 2, [0, 1, 2, 3], ("tRNS", [0, 1, 0, 2, 0, 3])), token: Token).Rgba);
        // A palette's first entries have alpha values; the rest stay opaque.
        Assert.Equal([255, 0, 0, 128, 0, 0, 255, 255], PngDecoder.Decode(Png(2, 1, 8, 3, [0, 0, 1], ("PLTE", Palette), ("tRNS", [128])), token: Token).Rgba);
        // Image data in several consecutive chunks is one stream.
        byte[] compressed = Compressed([0, 10, 20]);
        Assert.Equal([10, 10, 10, 255, 20, 20, 20, 255], PngDecoder.Decode(Png(2, 1, 8, 0, [], ("IDAT", compressed[..3]), ("IDAT", compressed[3..])), token: Token).Rgba);
    }

    [Fact]
    public void TransparentValuesUseOnlyTheSampleBits()
    {
        // Below 16 bits the PNG specification has decoders mask the value's other bits, which an encoder may leave set:
        // 0x0101 in a 1-bit image is the value 1 (white), 0x0114 in an 8-bit image the value 20.
        Assert.Equal([255, 255, 255, 0, 0, 0, 0, 255], PngDecoder.Decode(Png(2, 1, 1, 0, [0, 0x80], ("tRNS", [1, 1])), token: Token).Rgba);
        Assert.Equal([10, 10, 10, 255, 20, 20, 20, 0], PngDecoder.Decode(Png(2, 1, 8, 0, [0, 10, 20], ("tRNS", [1, 20])), token: Token).Rgba);
        Assert.Equal([1, 2, 3, 0], PngDecoder.Decode(Png(1, 1, 8, 2, [0, 1, 2, 3], ("tRNS", [0xFF, 1, 0xFF, 2, 0xFF, 3])), token: Token).Rgba);
    }

    [Theory]
    // A transparency chunk of the wrong length for the colour type, which read as opaque dropped what the file meant.
    [InlineData("greyscale tRNS of 1 byte", "holds 2 bytes, not 1")]
    [InlineData("greyscale tRNS of 3 bytes", "holds 2 bytes, not 3")]
    [InlineData("RGB tRNS of 4 bytes", "holds 6 bytes, not 4")]
    [InlineData("palette tRNS longer than the palette", "3 alpha values for a palette of 2 entries")]
    [InlineData("RGBA with tRNS", "alpha channel")]
    // Order and repetition, on which decoders differ.
    [InlineData("tRNS before PLTE", "comes before its palette")]
    [InlineData("PLTE after IDAT", "follows its image data")]
    [InlineData("tRNS after IDAT", "follows its image data")]
    [InlineData("two PLTE", "two palettes")]
    [InlineData("two tRNS", "two transparency")]
    [InlineData("IDAT split by another chunk", "split by other chunks")]
    public void MalformedTransparencyAndChunkOrderAreRefused(string change, string message)
    {
        byte[] png = change switch
        {
            "greyscale tRNS of 1 byte" => Png(2, 1, 8, 0, [0, 10, 20], ("tRNS", [20])),
            "greyscale tRNS of 3 bytes" => Png(2, 1, 8, 0, [0, 10, 20], ("tRNS", [0, 20, 0])),
            "RGB tRNS of 4 bytes" => Png(1, 1, 8, 2, [0, 1, 2, 3], ("tRNS", [0, 1, 0, 2])),
            "palette tRNS longer than the palette" => Png(2, 1, 8, 3, [0, 0, 1], ("PLTE", Palette), ("tRNS", [128, 64, 32])),
            "RGBA with tRNS" => Png(1, 1, 8, 6, [0, 1, 2, 3, 4], ("tRNS", [0, 1, 0, 2, 0, 3])),
            "tRNS before PLTE" => Png(2, 1, 8, 3, [0, 0, 1], ("tRNS", [128]), ("PLTE", Palette)),
            "PLTE after IDAT" => Png(2, 1, 8, 3, [0, 0, 1], Data, ("PLTE", Palette)),
            "tRNS after IDAT" => Png(2, 1, 8, 3, [0, 0, 1], ("PLTE", Palette), Data, ("tRNS", [128])),
            "two PLTE" => Png(2, 1, 8, 3, [0, 0, 1], ("PLTE", Palette), ("PLTE", [0, 0, 255, 255, 0, 0])),
            "two tRNS" => Png(2, 1, 8, 0, [0, 10, 20], ("tRNS", [0, 20]), ("tRNS", [0, 10])),
            _ => Png(2, 1, 8, 0, [], ("IDAT", Compressed([0, 10, 20])[..3]), ("tEXt", "a\0b"u8.ToArray()), ("IDAT", Compressed([0, 10, 20])[3..])),
        };
        var refused = Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png, token: Token));
        Assert.Contains(message, refused.Message);
    }

    private static void AssertNoCheckout(SourceWorldFixture fixture)
    {
        Assert.Empty(SourceBlender.Checkouts(fixture.Project));
        string export = Path.Combine(fixture.Project, "zstudio", "export");
        Assert.True(!Directory.Exists(export) || !Directory.EnumerateFileSystemEntries(export).Any());
    }

    [Fact]
    public void CheckoutsCopyOnlyWhatAModelMayHold()
    {
        using SourceWorldFixture fixture = new();
        var gltf = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Model)))!.AsObject();
        // 4,097 distinct project files, each a valid buffer or texture by itself: more buffers than a model may use, and
        // more textures than a pack holds. Neither is copied, and the checkout leaves nothing behind.
        const int Files = 4097;
        for (int i = 0; i < Files; i++) fixture.Write($"data/m1/models/many/b{i}.bin", [1, 2, 3, 4]);
        var buffers = gltf.DeepClone().AsObject();
        foreach (int i in Enumerable.Range(0, Files)) buffers["buffers"]!.AsArray().Add(new JsonObject { ["uri"] = $"many/b{i}.bin", ["byteLength"] = 4 });
        fixture.Write(Model, Encoding.UTF8.GetBytes(buffers.ToJsonString()));
        var refused = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(new SourceWorkspace(fixture.Project), Model, Token));
        Assert.Contains("at most 4,096", refused.Message);
        AssertNoCheckout(fixture);

        var images = gltf.DeepClone().AsObject();
        foreach (int i in Enumerable.Range(0, Files)) images["images"]!.AsArray().Add(new JsonObject { ["uri"] = $"many/b{i}.bin" });
        fixture.Write(Model, Encoding.UTF8.GetBytes(images.ToJsonString()));
        refused = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(new SourceWorkspace(fixture.Project), Model, Token));
        Assert.Contains("more than 4,096 textures", refused.Message);
        AssertNoCheckout(fixture);

        // A model the build could not read (its positions reach past their buffer view) is refused before anything is copied.
        var malformed = gltf.DeepClone().AsObject();
        malformed["bufferViews"]![0]!["byteLength"] = 1;
        fixture.Write(Model, Encoding.UTF8.GetBytes(malformed.ToJsonString()));
        refused = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(new SourceWorkspace(fixture.Project), Model, Token));
        Assert.StartsWith(Model + ":", refused.Message);
        AssertNoCheckout(fixture);

        // As it was, the model checks out.
        fixture.Write(Model, Encoding.UTF8.GetBytes(gltf.ToJsonString()));
        Assert.Single(SourceBlender.Checkout(new SourceWorkspace(fixture.Project), Model, Token).Files, f => f.Project == "data/m1/models/m1.bin");
    }

    [Theory]
    // Another program saves the model (its glTF and buffer) after the checkout read the glTF, before or after the buffer.
    [InlineData(Model, "data/m1/models/m1.gltf")]
    [InlineData("data/m1/models/m1.bin", "data/m1/models/m1.gltf")]
    // ... or saves a texture the checkout already copied.
    [InlineData("data/m1/textures/rock.png", "data/m1/textures/rock.png")]
    public void CheckoutsRefuseFilesChangedWhileTheyCopy(string after, string changed)
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        byte[] gltf = File.ReadAllBytes(fixture.Path(Model)), buffer = File.ReadAllBytes(fixture.Path("data/m1/models/m1.bin"));
        void Touch(string relative, byte[] content)
        {
            fixture.Write(relative, content);
            File.SetLastWriteTimeUtc(fixture.Path(relative), File.GetLastWriteTimeUtc(fixture.Path(relative)).AddMinutes(1));
        }
        List<string> reads = [];
        void Read(string relative)
        {
            reads.Add(relative);
            if (relative != after) return;
            if (after.EndsWith(".png", StringComparison.Ordinal)) { Touch(after, PngEncoder.Encode(new DecodedImage(1, 1, [1, 2, 3, 255]), Token)); return; }
            var json = JsonNode.Parse(gltf)!.AsObject(); json["nodes"]![0]!["name"] = "renamed by another program";
            Touch(Model, Encoding.UTF8.GetBytes(json.ToJsonString())); Touch("data/m1/models/m1.bin", [.. buffer, 0, 0, 0, 0]);
        }
        var refused = Assert.Throws<SourceFileChangedException>(() => SourceBlender.Checkout(workspace, Model, Token, Read));
        Assert.Contains(after, reads);
        Assert.Equal([changed], refused.Files);
        Assert.Contains("check it out again", refused.Message);
        AssertNoCheckout(fixture);

        // Unchanged since, it checks out with what the project now holds.
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        Assert.Equal(File.ReadAllBytes(fixture.Path("data/m1/models/m1.bin")), File.ReadAllBytes(Path.Combine(checkout.Folder, "input", "m1.bin")));
    }

    [Fact]
    public void UpdatesTakeAsManyTexturesAsAPackHolds()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        // Blender exported the model with 4,097 textures (the rock and 4,096 new ones): more than a pack holds, so the
        // update is refused rather than decoding them all and failing only when the world rebuilds.
        string outbox = Path.Combine(checkout.Outbox, "edit");
        Directory.CreateDirectory(outbox);
        var json = JsonNode.Parse(File.ReadAllText(checkout.Input))!.AsObject();
        byte[] png = PngEncoder.Encode(new DecodedImage(1, 1, [1, 2, 3, 255]), Token);
        for (int i = 0; i < 4096; i++)
        {
            File.WriteAllBytes(Path.Combine(outbox, $"t{i}.png"), png);
            json["images"]!.AsArray().Add(new JsonObject { ["uri"] = $"t{i}.png" });
        }
        Directory.CreateDirectory(Path.Combine(outbox, "textures"));
        File.Copy(Path.Combine(Path.GetDirectoryName(checkout.Input)!, "textures", "rock.png"), Path.Combine(outbox, "textures", "rock.png"));
        File.Copy(Path.Combine(Path.GetDirectoryName(checkout.Input)!, "m1.bin"), Path.Combine(outbox, "m1.bin"));
        File.WriteAllText(Path.Combine(outbox, "m1.gltf"), json.ToJsonString());
        var refused = Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, "edit/m1.gltf", force: true, token: Token));
        Assert.Contains("more than 4,096 textures", refused.Message);

        // One texture fewer is an update.
        json["images"]!.AsArray().RemoveAt(json["images"]!.AsArray().Count - 1);
        File.WriteAllText(Path.Combine(outbox, "m1.gltf"), json.ToJsonString());
        var plan = SourceBlender.PlanUpdate(workspace, checkout, "edit/m1.gltf", force: true, token: Token);
        Assert.Equal(4095, plan.Changes.Count(c => c.Relative.StartsWith("data/m1/textures/t", StringComparison.Ordinal)));
    }

    [Fact]
    public void CheckoutsReadTheWorkspacesUnsavedEdits()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        // An unsaved edit of the model's texture is what the checkout copies; the disk's file may change meanwhile.
        byte[] edited = PngEncoder.Encode(new DecodedImage(1, 1, [9, 8, 7, 255]), Token);
        workspace.Apply("paint", [("data/m1/textures/rock.png", edited)], Token);
        var checkout = SourceBlender.Checkout(workspace, Model, Token, relative =>
        {
            if (relative == "data/m1/textures/rock.png") fixture.Write(relative, PngEncoder.Encode(new DecodedImage(1, 1, [1, 1, 1, 255]), Token));
        });
        Assert.Equal(edited, File.ReadAllBytes(Path.Combine(checkout.Folder, "input", "textures", "rock.png")));
    }
}
