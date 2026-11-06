using System.Buffers.Binary;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class GltfMetadataTests
{
    [Theory]
    [InlineData("recoil")]
    [InlineData("zstudio")]
    public void UnmarkedTypedTerrainReferencesPreserveModelMaterialAndLeafAppearance(string key)
    {
        var token = TestContext.Current.CancellationToken;
        GltfPrimitive primitive = new() { Material = new() { Extras = new() { [key] = new JsonObject
            { ["color"] = new JsonArray(40, 50, 60), ["opacity"] = 200, ["normals"] = false } } } };
        primitive.Positions.AddRange([System.Numerics.Vector3.Zero, new(4, 0, 0), new(0, 0, -4)]); primitive.Indices.AddRange([0, 1, 2]);
        GltfMesh mesh = new() { Extras = new() { [key] = new JsonObject { ["flags"] = 17, ["scroll"] = new JsonArray(0.1f, 0.2f, 0) } } }; mesh.Primitives.Add(primitive);
        GltfDocument surface = new(); surface.Roots.Add(new() { Name = "surface", Mesh = mesh, Extras = new() { [key] = new JsonObject
            { ["appearance"] = new JsonObject { ["flags"] = "0x2", ["alphaScale"] = 0.5f } } } });
        var recipe = new zStudio.Core.Terrain.TerrainRecipe(1, [new("surface", "surface.gltf", "surface", zStudio.Core.Terrain.TerrainAttributes.None)], zStudio.Core.Terrain.TerrainAttributes.None, []);
        GltfDocument marker = new(); marker.Roots.Add(new() { Name = "terrain", Extras = new() { [WorldGltf.Key] = new JsonObject { ["terrain"] = "terrain.json" } } });
        var nodes = WorldGltf.Import(marker, "data/m1/models/m1.gltf", 255, new()
        {
            World = new(), Token = token, Reference = (_, _) => (surface, "data/m1/models/surface.gltf"),
            ReadFile = (_, _) => (recipe.Write(), "data/m1/models/terrain.json"), TextureName = (_, _, _) => "unused", Grid = () => new(0, 4, 4, -4, 4, -4, 1, 1),
        });
        var node = Assert.Single(nodes, n => n.Model != null); var model = node.Model!; var material = model.Polygons[0].Material!;
        Assert.Equal(0.5f, node.PayloadFloat(4)); Assert.Equal(17u, model.Flags); Assert.Equal(0.1f, model.ScrollU); Assert.Equal(0.2f, model.ScrollV);
        Assert.Equal(new System.Numerics.Vector3(40, 50, 60), material.Color); Assert.Equal(200u, material.Flags);
    }

    [Fact]
    public void PresentationReportsNamespaceChangesAndRemainsIdempotent()
    {
        var root = JsonNode.Parse("""{"asset":{"version":"2.0"},"scenes":[{"extras":{"recoil":{"sourceFormat":4}}}]}""")!.AsObject();
        Assert.True(WorldGltf.ApplyPresentation(root, _ => null, false));
        Assert.NotNull(root["scenes"]![0]!["extras"]![WorldGltf.Key]);
        Assert.False(WorldGltf.ApplyPresentation(root, _ => null, false));
    }

    [Theory]
    [InlineData("recoil")]
    [InlineData("zstudio")]
    public void TypedTerrainReferencesCannotDiscardAncestorAppearance(string key)
    {
        var surface = new GltfDocument();
        var parent = new GltfNode { Name = "parent", Extras = new() { [key] = new JsonObject { ["appearance"] = new JsonObject { ["flags"] = "0x2", ["alphaScale"] = 0.5f } } } };
        var leaf = new GltfNode { Name = "surface", Mesh = new() };
        GltfPrimitive primitive = new() { Material = new() };
        primitive.Positions.AddRange([System.Numerics.Vector3.Zero, new(4, 0, 0), new(0, 0, -4)]); primitive.Indices.AddRange([0, 1, 2]);
        leaf.Mesh.Primitives.Add(primitive); parent.Children.Add(leaf); surface.Roots.Add(parent);
        var recipe = new zStudio.Core.Terrain.TerrainRecipe(1, [new("surface", "surface.gltf", "surface", zStudio.Core.Terrain.TerrainAttributes.None)], zStudio.Core.Terrain.TerrainAttributes.None, []);
        var marker = new GltfDocument(); marker.Roots.Add(new() { Name = "terrain", Extras = new() { [key] = new JsonObject { ["terrain"] = "terrain.json" } } });
        var context = new WorldGltf.ImportContext
        {
            World = new(), Reference = (_, _) => (surface, "data/m1/models/surface.gltf"),
            ReadFile = (_, _) => (recipe.Write(), "data/m1/models/terrain.json"), TextureName = (_, _, _) => "unused",
            Grid = () => new(0, 4, 4, -4, 4, -4, 1, 1),
        };
        var error = Assert.Throws<InvalidDataException>(() => WorldGltf.Import(marker, "data/m1/models/m1.gltf", 255, context));
        Assert.Contains("ancestor with Object3D appearance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RawZonePatchPreservesLegacyAttributesAndCanonicalPrecedence()
    {
        var token = TestContext.Current.CancellationToken;
        var root = JsonNode.Parse("""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0,1],"extras":{"recoil":{"sourceFormat":4,"game":"mw3"}}}],
             "nodes":[{"name":"display","extras":{"recoil":{"name":"recorded","landmark":true,"ref":"part.gltf"}}},
                      {"name":"canonical","extras":{"zstudio":{},"recoil":{"name":"hidden","landmark":true}}}]}
            """)!.AsObject();
        var doc = GltfDocument.Read(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()), _ => throw new InvalidOperationException(), token);
        var profile = WorldGltf.CaptureZoneProfile(doc, token: token);
        WorldGltf.PatchEmbeddedZones(root, doc, profile, token);
        var legacy = root["nodes"]![0]!["extras"]![WorldGltf.Key]!;
        Assert.Equal("recorded", legacy["name"]!.GetValue<string>());
        Assert.Equal("part.gltf", legacy["ref"]!.GetValue<string>());
        Assert.True(legacy["landmark"]!.GetValue<bool>());
        Assert.Equal("mw3", root["scenes"]![0]!["extras"]![WorldGltf.Key]!["game"]!.GetValue<string>());
        var canonical = root["nodes"]![1]!["extras"]![WorldGltf.Key]!;
        Assert.Null(canonical["name"]); Assert.Null(canonical["landmark"]);
        Assert.DoesNotContain("\"recoil\"", root.ToJsonString());
    }

    [Theory]
    [InlineData("recoil", false)]
    [InlineData("zstudio", false)]
    [InlineData("recoil", true)]
    public void ColdTerrainDependencyInspectionHonorsNamespacePrecedence(string key, bool canonicalEmpty)
    {
        var token = TestContext.Current.CancellationToken;
        JsonObject extras = new() { [key] = new JsonObject { ["ref"] = "part.gltf" } };
        if (canonicalEmpty) extras[WorldGltf.Key] = new JsonObject();
        JsonObject root = new() { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["nodes"] = new JsonArray(new JsonObject { ["extras"] = extras }) };
        var bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        var referenced = zStudio.Core.Sources.SourceTerrain.Referenced("data/m1/models/m1.gltf", (path, _) => path.EndsWith("m1.gltf", StringComparison.Ordinal) ? bytes : "{}"u8.ToArray(), token);
        if (canonicalEmpty) Assert.Empty(referenced);
        else Assert.Contains("data/m1/models/part.gltf", referenced);
    }
    [Fact]
    public void SharedNativeInstancesCannotSilentlyDiscardDifferentCornerColors()
    {
        GltfDocument doc = new(); GltfMaterial material = new();
        foreach (var color in new[] { System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY })
        {
            GltfPrimitive primitive = new() { Material = material };
            primitive.Positions.AddRange([System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ]);
            primitive.Indices.AddRange([0, 1, 2]); primitive.Colors.AddRange([color, color, color]);
            GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
            doc.Roots.Add(new() { Name = "shared", Mesh = mesh, Extras = new() { [WorldGltf.LegacyKey] = new JsonObject { ["instance"] = 1 } } });
        }
        Assert.Throws<InvalidDataException>(() => WorldGltf.CheckInstances(doc, "shared.gltf"));
        doc.Roots[1].Mesh!.Primitives[0].Colors.Clear(); doc.Roots[1].Mesh!.Primitives[0].Colors.AddRange(doc.Roots[0].Mesh!.Primitives[0].Colors);
        WorldGltf.CheckInstances(doc, "shared.gltf");
    }
    [Theory]
    [InlineData("0.0", "recoil")]
    [InlineData("0e0", "zstudio")]
    public void LightweightIdentityUsesTheSharedExactIntegerRules(string scene, string key)
    {
        var token = TestContext.Current.CancellationToken;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("""
            {"asset":{"version":"2.0"},"scene":SCENE,"scenes":[{"nodes":[],"extras":{"KEY":{"sourceFormat":4,"game":"mw3","mechLibraryMember":"test.flt"}}}],"nodes":[]}
            """.Replace("SCENE", scene, StringComparison.Ordinal).Replace("KEY", key, StringComparison.Ordinal));
        Assert.Empty(GltfDocument.Read(bytes, _ => throw new InvalidOperationException(), token).Roots);
        Assert.Equal("test.flt", WorldGltf.ReadSourceIdentity(bytes, token).LibraryMember);
    }
    // Namespace compatibility must not change source semantics, container payloads or caller-owned source bytes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyMetadataReadsAndWritesCanonicallyWithoutChangingPayloads(bool binary)
    {
        var token = TestContext.Current.CancellationToken;
        var root = JsonNode.Parse("""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0],"extras":{"recoil":{"sourceFormat":4,"game":"mw3","mechLibraryMember":"test.flt"}}}],
             "nodes":[{"name":"recoil","extras":{"recoil":{"landmark":true},"artist":{"recoil":"leave alone","extras":{"recoil":{"literal":true}}}}}],
             "buffers":[{"byteLength":4}]}
            """)!.AsObject();
        byte[] data = [1, 2, 3, 4];
        if (!binary) root["buffers"]![0]!["uri"] = "mesh.bin";
        byte[] json = GltfJson.Write(root, false, token); // Writer canonicalizes even caller-provided legacy extras.
        Assert.Null(root["scenes"]![0]!["extras"]![WorldGltf.LegacyKey]);
        Legacy(root);
        byte[] legacy = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        byte[] input = binary ? Glb(legacy, data) : legacy; byte[] original = (byte[])input.Clone();
        var doc = GltfDocument.Read(input, _ => data, token);
        Assert.Equal("mw3", WorldGltf.ReadSourceIdentity(input, token).Game);
        Assert.Equal("test.flt", WorldGltf.ReadSourceIdentity(input, token).LibraryMember);
        Assert.True(doc.Roots[0].Extras![WorldGltf.Key]!["landmark"]!.GetValue<bool>());
        Assert.Equal("leave alone", doc.Roots[0].Extras!["artist"]!["recoil"]!.GetValue<string>());
        Assert.True(doc.Roots[0].Extras!["artist"]!["extras"]!["recoil"]!["literal"]!.GetValue<bool>());
        var written = doc.Write("mesh.bin", token);
        Assert.Equal(original, input); Assert.Null(JsonNode.Parse(written.Json)!["nodes"]![0]!["extras"]![WorldGltf.LegacyKey]);
        var parsed = GltfMetadata.Parse(GltfDocument.ContainerJson(input, token, out _), token)!;
        byte[] patched = zStudio.Core.Sources.SourceEmbeddedZones.Repack(input, parsed, token);
        _ = GltfDocument.ContainerJson(patched, token, out var payload);
        if (binary) Assert.True(payload.SequenceEqual(data));
    }

    [Fact]
    public void CanonicalPresenceWinsAtEveryMetadataOwner()
    {
        var token = TestContext.Current.CancellationToken;
        var root = JsonNode.Parse("""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0],"extras":{"zstudio":{},"recoil":{"game":"mw3"}}}],
             "nodes":[{"extras":{"zstudio":{"landmark":false},"recoil":{"landmark":true}}}],
             "meshes":[{"extras":{"recoil":{"flags":9}},"primitives":[{"extras":{"zstudio":{"polygons":[1]},"recoil":{"polygons":[2]}}}]}],
             "materials":[{"extras":{"zstudio":{"opacity":12},"recoil":{"opacity":255}}}]}
            """)!.AsObject();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        Assert.Null(WorldGltf.ReadSourceIdentity(bytes, token).Game);
        GltfMetadata.Normalize(root, token);
        Assert.Empty(root["scenes"]![0]!["extras"]![WorldGltf.Key]!.AsObject());
        Assert.False(root["nodes"]![0]!["extras"]![WorldGltf.Key]!["landmark"]!.GetValue<bool>());
        Assert.Equal(9, root["meshes"]![0]!["extras"]![WorldGltf.Key]!["flags"]!.GetValue<int>());
        Assert.Equal(1, root["meshes"]![0]!["primitives"]![0]!["extras"]![WorldGltf.Key]!["polygons"]![0]!.GetValue<int>());
        Assert.Equal(12, root["materials"]![0]!["extras"]![WorldGltf.Key]!["opacity"]!.GetValue<int>());
        Assert.DoesNotContain("\"recoil\"", root.ToJsonString());
        GltfDocument legacy = new() { SceneExtras = JsonNode.Parse("""{"recoil":{"sourceFormat":4,"loadRoot":{"landmark":true,"zone":"any"}}}""")!.AsObject() };
        Assert.Equal(0x80u, WorldGltf.RootFlags(legacy));
        Assert.NotNull(WorldGltf.EmbeddedLoadRoot(legacy));
    }

    private static void Legacy(JsonNode node)
    {
        if (node is JsonObject owner)
        {
            if (owner["extras"] is JsonObject extras && extras.Remove(WorldGltf.Key, out var metadata)) extras[WorldGltf.LegacyKey] = metadata;
            foreach (var pair in owner) if (pair.Key != "extras" && pair.Value != null) Legacy(pair.Value);
        }
        else if (node is JsonArray list) foreach (var child in list) if (child != null) Legacy(child);
    }
    private static byte[] Glb(byte[] json, byte[] data)
    {
        int length = (json.Length + 3) & ~3; byte[] bytes = new byte[28 + length + data.Length]; "glTF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 2); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), length); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4E4F534A);
        bytes.AsSpan(20, length).Fill(32); json.CopyTo(bytes, 20); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20 + length), data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + length), 0x004E4942); data.CopyTo(bytes, 28 + length); return bytes;
    }
}
