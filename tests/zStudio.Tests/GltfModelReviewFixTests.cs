using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// glTF reading and model building review fixes: a file's lists and metadata are bounded before any entry is read or
/// copied, an accessor is refused when its use would read other values than it holds, node hierarchies stay trees, and a
/// polygon discarded at the vertex limit leaves no vertices behind.
/// </summary>
public sealed class GltfModelReviewFixTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Text(string json) => Encoding.UTF8.GetBytes(json);
    private static string Asset(string rest) => $$"""{"asset":{"version":"2.0"}{{(rest.Length > 0 ? "," + rest : "")}}}""";
    private static string Repeat(string entry, int count) => string.Join(",", Enumerable.Repeat(entry, count));
    private static GltfDocument Read(string json, GltfDocument.ReadLimits? limits = null) =>
        GltfDocument.Read(Text(json), _ => throw new FileNotFoundException(), limits ?? GltfDocument.ReadLimits.Default, Token);
    private static InvalidDataException Refused(string json, GltfDocument.ReadLimits? limits = null) => Assert.Throws<InvalidDataException>(() => Read(json, limits));

    [Fact]
    public void ListsAreBoundedBeforeAnyOfTheirEntriesIsRead()
    {
        // Unused entries cost as much as used ones: each would be made into an object (a material, a mesh, a primitive, a
        // morph target), however cheap it is to write. The counts are refused on the parsed text.
        int named = GltfDocument.MaximumNodes + 1, entries = GltfDocument.MaximumEntries + 1;
        Assert.Contains("200,001 materials", Refused(Asset($"\"materials\":[{Repeat("{}", named)}]")).Message);
        Assert.Contains("200,001 meshes", Refused(Asset($"\"meshes\":[{Repeat("{}", named)}]")).Message);
        Assert.Contains("200,001 images", Refused(Asset($"\"images\":[{Repeat("{}", named)}]")).Message);
        Assert.Contains("200,001 nodes", Refused(Asset($"\"nodes\":[{Repeat("{}", named)}]")).Message);
        Assert.Contains("1,000,001 accessors", Refused(Asset($"\"accessors\":[{Repeat("{}", entries)}]")).Message);
        // Primitives and morph targets count across meshes and primitives, weights across meshes.
        Assert.Contains("primitives", Refused(Asset($"\"meshes\":[{{\"primitives\":[{Repeat("{}", entries / 2)}]}},{{\"primitives\":[{Repeat("{}", entries / 2 + 1)}]}}]")).Message);
        // Each primitive becomes an object with its attributes: they are bounded like nodes.
        int primitives = GltfDocument.MaximumPrimitives / 2;
        Assert.Contains("more than 200,000 primitives", Refused(Asset($"\"meshes\":[{{\"primitives\":[{Repeat("{}", primitives)}]}},{{\"primitives\":[{Repeat("{}", primitives + 1)}]}}]")).Message);
        string empty = "\"accessors\":[{\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}]";
        Assert.Contains("morph targets", Refused(Asset($"{empty},\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":0}},\"targets\":[{Repeat("{}", entries)}]}}]}}]")).Message);
        Assert.Contains("weights", Refused(Asset($"\"meshes\":[{{\"weights\":[{Repeat("0", entries)}]}}]")).Message);

        // At the limits the same lists read.
        Assert.Empty(Read(Asset($"\"materials\":[{Repeat("{}", GltfDocument.MaximumNodes)}]")).Roots);
    }

    [Fact]
    public void NamesExtrasAndImagePathsAreBoundedTogetherBeforeTheyAreCopied()
    {
        GltfDocument.ReadLimits small = GltfDocument.ReadLimits.Default with { MetadataBytes = 1024 };
        string Extras(int bytes) => $"{{\"recoil\":\"{new string('x', bytes - 13)}\"}}";
        Assert.Equal(600, Encoding.UTF8.GetByteCount(Extras(600)));

        // Extras of materials nothing uses count with those of the rest; refused before any material is read: material 0's
        // malformed base colour would otherwise be found first.
        string unused = $"{{\"extras\":{Extras(600)}}}";
        var refused = Refused(Asset($"\"materials\":[{{\"pbrMetallicRoughness\":{{\"baseColorFactor\":[1]}}}},{unused},{unused}]"), small);
        Assert.Contains("1,024 bytes", refused.Message); Assert.Contains("extras", refused.Message);
        // So do node and mesh names and extras, primitives' and the scene's extras, and image paths.
        Refused(Asset($"\"nodes\":[{{\"name\":\"{new string('n', 600)}\"}},{{\"extras\":{Extras(600)}}}]"), small);
        Refused(Asset($"\"meshes\":[{{\"name\":\"{new string('m', 600)}\",\"primitives\":[{{\"extras\":{Extras(600)}}}]}}]"), small);
        Refused(Asset($"\"scenes\":[{{\"extras\":{Extras(600)}}}],\"images\":[{{\"uri\":\"{new string('i', 600)}.png\"}}]"), small);

        // Within the budget everything is kept; an embedded image's data is only recognized, never kept, so it does not count.
        var doc = Read(Asset($$$"""
            "scene":0,"scenes":[{"nodes":[0],"extras":{{{Extras(300)}}}}],"nodes":[{"name":"crate","mesh":0}],
            "meshes":[{"primitives":[{"attributes":{"POSITION":0},"material":0}]}],"accessors":[{"componentType":5126,"count":3,"type":"VEC3"}],
            "materials":[{"pbrMetallicRoughness":{"baseColorTexture":{"index":0}},"extras":{{{Extras(600)}}}}],
            "textures":[{"source":0}],"images":[{"uri":"data:image/png;base64,{{{new string('A', 4000)}}}"}]
            """), small);
        Assert.Equal(new string('x', 587), doc.Roots[0].Mesh!.Primitives[0].Material!.Extras!["recoil"]!.GetValue<string>());
        Assert.True(doc.Roots[0].Mesh!.Primitives[0].Material!.EmbeddedImage);
        Assert.NotNull(doc.SceneExtras);

        // The real budget is what the reader states.
        Assert.Equal(GltfDocument.MaximumMetadataBytes, GltfDocument.ReadLimits.Default.MetadataBytes);
    }

    [Fact]
    public void AnImagePathIsReadOnceForEveryMaterialThatShowsIt()
    {
        // A long path shown by many materials is one string, not one copy per material.
        string path = "textures/" + new string('a', 5000) + "%20b.png";
        var doc = Read(Asset($"\"textures\":[{{\"source\":0}}],\"images\":[{{\"uri\":\"{path}\"}}],\"materials\":[{Repeat("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}", 50)}]," +
            $"\"meshes\":[{{\"primitives\":[{string.Join(",", Enumerable.Range(0, 50).Select(i => $"{{\"attributes\":{{\"POSITION\":0}},\"material\":{i}}}"))}]}}]," +
            "\"accessors\":[{\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}],\"nodes\":[{\"mesh\":0}]"));
        var materials = doc.Roots[0].Mesh!.Primitives.Select(p => p.Material!).ToList();
        Assert.Equal(50, materials.Distinct().Count());
        Assert.Equal("textures/" + new string('a', 5000) + " b.png", materials[0].ImageUri);
        Assert.All(materials, m => Assert.Same(materials[0].ImageUri, m.ImageUri));
    }

    [Fact]
    public void MorphTargetsThatMoveNothingCountAsDecoded()
    {
        // Each target without positions is made in full, one delta per position, so it takes from the decoding budget like
        // an accessor: nine positions (27 components) and three empty targets (81) pass a budget of 100.
        GltfDocument.ReadLimits small = GltfDocument.ReadLimits.Default with { DecodedElements = 100 };
        string File(int targets) => Asset($"\"nodes\":[{{\"mesh\":0}}],\"accessors\":[{{\"componentType\":5126,\"count\":9,\"type\":\"VEC3\"}}]," +
            $"\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":0}},\"targets\":[{Repeat("{}", targets)}]}}]}}]");
        var read = Read(File(2), small).Roots[0].Mesh!.Primitives[0];
        Assert.Equal(2, read.Targets.Count); Assert.All(read.Targets, t => Assert.Equal(Enumerable.Repeat(Vector3.Zero, 9), t));
        Assert.Contains("decodes to more data", Refused(File(3), small).Message);
    }

    /// <summary>
    /// A triangle (positions in accessor 0) whose <paramref name="use"/> reads accessor 1, holding <paramref name="payload"/>
    /// (tightly packed). glTF starts each element of a vertex attribute on a 4-byte boundary, so vector elements of another
    /// size are padded to the next multiple of 4, which their view states as its stride.
    /// </summary>
    private static GltfPrimitive Primitive(string use, int componentType, string type, int count, bool normalized, string payload)
    {
        byte[] positions = new byte[36];
        float[] corners = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        for (int i = 0; i < corners.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(positions.AsSpan(i * 4), corners[i]);
        int element = (componentType is 5120 or 5121 ? 1 : componentType is 5122 or 5123 ? 2 : 4) * (type switch { "VEC2" => 2, "VEC3" => 3, _ => 1 });
        int stride = (element + 3) / 4 * 4;
        bool padded = type != "SCALAR" && stride != element && payload.Length == element * count * 2;
        if (padded) payload = string.Concat(Enumerable.Range(0, count).Select(i => payload.Substring(i * element * 2, element * 2) + new string('0', (stride - element) * 2)));
        byte[] data = Convert.FromHexString(payload), buffer = [.. positions, .. data];
        JsonObject view = new() { ["buffer"] = 0, ["byteOffset"] = 36, ["byteLength"] = data.Length };
        if (padded) view["byteStride"] = stride;
        JsonObject accessor = new() { ["bufferView"] = 1, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
        if (normalized) accessor["normalized"] = true;
        JsonObject attributes = new() { ["POSITION"] = use == "POSITION" ? 1 : 0 }, primitive = new() { ["attributes"] = attributes };
        if (use == "indices") primitive["indices"] = 1;
        else if (use == "target") primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = 1 });
        else if (use != "POSITION") attributes[use] = 1;
        JsonObject json = new()
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            // The integer attributes read here are KHR_mesh_quantization's, which a file declares.
            ["extensionsUsed"] = new JsonArray("KHR_mesh_quantization"),
            ["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 }),
            ["meshes"] = new JsonArray(new JsonObject { ["primitives"] = new JsonArray(primitive) }),
            ["accessors"] = new JsonArray(new JsonObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3" }, accessor),
            ["bufferViews"] = new JsonArray(new JsonObject { ["buffer"] = 0, ["byteLength"] = 36 }, view),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = buffer.Length, ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(buffer) }),
        };
        return Read(json.ToJsonString()).Roots[0].Mesh!.Primitives[0];
    }

    [Theory]
    // Indices are single unsigned integers that are not normalized: a float index was truncated (1.5 read as 1), a
    // normalized one scaled to a fraction (every index 0), a signed one read below zero, and a vector's components taken
    // for separate indices.
    [InlineData("indices", 5126, "SCALAR", 3, false, "000000000000C03F00000040", "float values, but it is used for indices")]
    [InlineData("indices", 5121, "SCALAR", 3, true, "000102", "normalized unsigned byte values, but it is used for indices")]
    [InlineData("indices", 5120, "SCALAR", 3, false, "000102", "signed byte values, but it is used for indices")]
    [InlineData("indices", 5122, "SCALAR", 3, false, "000001000200", "signed short values, but it is used for indices")]
    [InlineData("indices", 5121, "VEC3", 1, false, "000102", "holds VEC3 values, but it is used for indices, which need SCALAR")]
    // Vertex attributes: unsigned integers, normalized floats and integer normals that are not normalized have no meaning.
    [InlineData("POSITION", 5125, "VEC3", 3, false, "000000000000000000000000000000000000000000000000000000000000000000000000", "unsigned integer values, but it is used for positions")]
    [InlineData("POSITION", 5126, "VEC3", 3, true, "000000000000000000000000000000000000000000000000000000000000000000000000", "normalized float values, but it is used for positions")]
    [InlineData("POSITION", 5126, "SCALAR", 9, false, "000000000000000000000000000000000000000000000000000000000000000000000000", "holds SCALAR values, but it is used for positions")]
    [InlineData("NORMAL", 5122, "VEC3", 3, false, "000000000000000000000000000000000000", "signed short values, but it is used for normals")]
    [InlineData("TEXCOORD_0", 5126, "VEC3", 3, false, "000000000000000000000000000000000000000000000000000000000000000000000000", "used for texture coordinates, which need VEC2")]
    [InlineData("target", 5125, "VEC3", 3, false, "000000000000000000000000000000000000000000000000000000000000000000000000", "unsigned integer values, but it is used for morph target positions")]
    // Every attribute holds one value per position: two would pair the third corner with nothing (or the wrong value).
    [InlineData("NORMAL", 5126, "VEC3", 2, false, "000000000000000000000000000000000000000000000000", "2 normals for 3 positions")]
    [InlineData("TEXCOORD_0", 5126, "VEC2", 2, false, "00000000000000000000000000000000", "2 texture coordinates for 3 positions")]
    [InlineData("target", 5126, "VEC3", 2, false, "000000000000000000000000000000000000000000000000", "2 morph target positions for 3 positions")]
    public void AccessorsMustHoldWhatTheirUseReads(string use, int componentType, string type, int count, bool normalized, string payload, string message)
    {
        var refused = Assert.Throws<InvalidDataException>(() => Primitive(use, componentType, type, count, normalized, payload));
        Assert.Contains(message, refused.Message);
    }

    [Fact]
    public void AccessorsOfTheTypesTheirUseReadsAreRead()
    {
        Assert.Equal([0, 1, 2], Primitive("indices", 5121, "SCALAR", 3, false, "000102").Indices);
        Assert.Equal([0, 1, 2], Primitive("indices", 5123, "SCALAR", 3, false, "000001000200").Indices);
        Assert.Equal([0, 1, 2], Primitive("indices", 5125, "SCALAR", 3, false, "000000000100000002000000").Indices);
        Assert.Contains("out of range", Assert.Throws<InvalidDataException>(() => Primitive("indices", 5125, "SCALAR", 3, false, "000000000100000003000000")).Message);
        // Normalized signed bytes for normals, normalized unsigned bytes for texture coordinates and integer positions
        // (KHR_mesh_quantization's attributes) read as the values they state.
        Assert.All(Primitive("NORMAL", 5120, "VEC3", 3, true, "007F00007F00007F00").Normals, n => Assert.Equal(Vector3.UnitY, n));
        Assert.Equal([new(1, 0), new(0, 1), new(1, 1)], Primitive("TEXCOORD_0", 5121, "VEC2", 3, true, "FF0000FFFFFF").TexCoords);
        Assert.Equal([new(0, 0, 0), new(2, 0, 0), new(0, 2, 0)], Primitive("POSITION", 5122, "VEC3", 3, false, "000000000000020000000000000002000000").Positions);
        Assert.Equal([new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)], Primitive("target", 5126, "VEC3", 3, false,
            "00000000000000000000803F00000000000000000000803F00000000000000000000803F").Targets.Single());
    }

    [Fact]
    public void NodeHierarchiesAreTrees()
    {
        // A node under two parents, or listed twice by one, would be walked once for each path to it, doubling with every
        // level: sixty-four nodes each listing the next twice would be walked 2^63 times.
        string chain = string.Join(",", Enumerable.Range(0, 63).Select(i => $"{{\"children\":[{i + 1},{i + 1}]}}").Append("{}"));
        Assert.Contains("only one parent", Refused(Asset($"\"scenes\":[{{\"nodes\":[0]}}],\"nodes\":[{chain}]")).Message);
        // A diamond lists no more children than nodes, and is refused where the second parent is found.
        Assert.Contains("node 3 is listed as a child more than once", Refused(Asset("\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"children\":[1,2]},{\"children\":[3]},{\"children\":[3]},{}]")).Message);
        Assert.Contains("node 1 is listed as a child more than once", Refused(Asset("\"nodes\":[{\"children\":[1,1]},{}]")).Message);
        // A scene's roots are roots, each listed once.
        Assert.Contains("another node's child", Refused(Asset("\"scenes\":[{\"nodes\":[0,1]}],\"nodes\":[{\"children\":[1]},{}]")).Message);
        Assert.Contains("more than once", Refused(Asset("\"scenes\":[{\"nodes\":[0,0]}],\"nodes\":[{},{}]")).Message);

        // Trees read as before, with or without a scene.
        var doc = Read(Asset("\"scenes\":[{\"nodes\":[0,2]}],\"nodes\":[{\"name\":\"a\",\"children\":[1]},{\"name\":\"b\"},{\"name\":\"c\"}]"));
        Assert.Equal(["a", "b", "c"], doc.AllNodes().Select(n => n.Name));
        Assert.Equal(["a", "c"], Read(Asset("\"nodes\":[{\"name\":\"a\",\"children\":[1]},{\"name\":\"b\"},{\"name\":\"c\"}]")).Roots.Select(n => n.Name));
    }

    /// <summary>
    /// A model filled to 920 vertices, then a polygon needing four more (one fits) discarded at the limit: its first
    /// corner's vertex (and morph delta) go again, so a triangle needing one new vertex still fits.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void APolygonDiscardedAtTheVertexLimitLeavesNoVerticesBehind(bool morphing, bool discardedMorphs)
    {
        WorldMaterial material = new() { Color = new(1, 2, 3) };
        Vector3 delta = new(0, 0, 1);
        PolygonInput Polygon(bool morphs, params Vector3[] points) => new(points, [], [], morphs ? [.. points.Select(p => p + delta)] : [], material);
        ModelBuilder builder = new();
        for (int t = 0; t < 306; t++) Assert.True(builder.Add(Polygon(morphing, new(3 * t, 0, 0), new(3 * t + 1, 0, 0), new(3 * t, 1, 0))));
        Assert.True(builder.Add(Polygon(morphing, new(0, 0, 0), new(2000, 0, 0), new(2000, 1, 0))));
        Assert.Equal(920, builder.Model.Vertices.Count);
        int morphs = builder.Model.Morphs.Count;

        Assert.False(builder.Add(Polygon(discardedMorphs, new(3000, 0, 0), new(3001, 0, 0), new(3001, 1, 0), new(3000, 1, 0))));
        Assert.Equal(920, builder.Model.Vertices.Count);
        Assert.Equal(morphs, builder.Model.Morphs.Count);
        Assert.DoesNotContain(new Vector3(3000, 0, 0), builder.Model.Vertices);

        // The room the discarded polygon would have kept is there for the next one.
        Assert.True(builder.Add(Polygon(morphing, new(0, 0, 0), new(1, 0, 0), new(5000, 5000, 0))));
        Assert.Equal(921, builder.Model.Vertices.Count);
        Assert.Equal(new Vector3(5000, 5000, 0), builder.Model.Vertices[^1]);
        Assert.Equal(308, builder.Model.Polygons.Count);
        Assert.Equal([0, 1, 920], builder.Model.Polygons[^1].Vertices);
        // A model none of whose polygons morph keeps no morph deltas, even after a morphing polygon was discarded.
        var model = builder.Finish();
        Assert.Equal(morphing ? 921 : 0, model.Morphs.Count);
    }
}
