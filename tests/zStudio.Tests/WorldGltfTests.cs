using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldGltfTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static (WorldNode Root, GameZWorld World) Sample()
    {
        GameZWorld world = new(); WorldTexture rock = new("rock"); world.Textures.Add(rock);
        WorldMaterial textured = new() { Texture = rock, Flags = 0x1FF, Soil = 4 }, glass = new() { Color = new(10, 20, 30), Flags = 0x0066, PackedColor = 0 };
        world.Materials.AddRange([textured, glass]);
        ModelBuilder builder = new();
        builder.Add(new([new(0, 0, 0), new(4, 0, 0), new(4, 0, -4), new(0, 0, -4)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], textured, Priority: 2));
        builder.Add(new([new(0, 0, 0), new(0, 3, 0), new(4, 0, 0)], [], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [], glass, ShowBackFace: true, Zone: 0xFFFF0301));
        var model = builder.Finish(); world.Models.Add(model);
        WorldNode root = new("tank", WorldNodeClass.Object3D) { Flags = 0x0308009C, Zone = 3 };
        root.SetPayloadInt(0, 0x30);
        float[] m = [1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 6, 7];
        for (int i = 0; i < 12; i++) root.SetPayloadFloat(0x30 + i * 4, m[i]);
        WorldNode lod = new("l1", WorldNodeClass.Lod); lod.SetPayloadInt(0, 1); lod.SetPayloadFloat(8, 128); lod.SetPayloadFloat(12, 16384); lod.SetPayloadInt(68, 1);
        WorldNode hull = new("healthy", WorldNodeClass.Object3D) { Model = model, Zone = 3 }; hull.SetPayloadInt(0, 0x28);
        WorldNode other = new("healthy", WorldNodeClass.Object3D) { Model = model, Zone = 5 }; other.SetPayloadInt(0, 0x28);
        root.Children.AddRange([lod, other]); lod.Children.Add(hull);
        return (root, world);
    }

    [Fact]
    public void ModelsSurviveTheGltfProfile()
    {
        var (root, _) = Sample();
        var exported = WorldGltf.Export([root], 0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 2) });
        var (json, bin) = exported.Write("tank.bin");
        string text = Encoding.UTF8.GetString(json);
        Assert.Contains("\"uri\": \"../textures/rock.png\"", text);
        Assert.Contains("\"wrapT\": 33071", text);
        var doc = GltfDocument.Read(json, uri => uri == "tank.bin" ? bin : throw new FileNotFoundException(uri), Token);
        GameZWorld target = new();
        WorldGltf.ImportContext context = new() { World = target, Reference = (_, _) => throw new InvalidOperationException(), TextureName = (uri, name, _) => name ?? Path.GetFileNameWithoutExtension(uri) };
        var imported = WorldGltf.Import(doc, "tank.gltf", 0xFF, context).Single();
        Assert.Equal("tank", imported.Name);
        Assert.Equal(0x0308009Cu & WorldGltf.CarriedFlags, imported.Flags & WorldGltf.CarriedFlags);
        Assert.Equal(3u, imported.Zone);
        Assert.Equal(new Vector3(5, 6, 7), WorldUpdate.LocalMatrix(imported)!.Value.Translation);
        var lod = imported.Children[0];
        Assert.Equal(WorldNodeClass.Lod, lod.Class);
        Assert.Equal(16384f, lod.PayloadFloat(12)); Assert.Equal(1, lod.PayloadInt(68));
        // Duplicate names are kept through extras, and zones are inherited unless they change.
        Assert.Equal(["healthy", "healthy"], [lod.Children[0].Name, imported.Children[1].Name]);
        Assert.Equal(3u, lod.Children[0].Zone); Assert.Equal(5u, imported.Children[1].Zone);
        // Shared models stay shared; polygons, UVs, normals and surface attributes come back.
        var model = lod.Children[0].Model!;
        Assert.Same(model, imported.Children[1].Model);
        Assert.Equal(2, model.Polygons.Count);
        var quad = model.Polygons[0];
        Assert.Equal(4, quad.Vertices.Length); Assert.Equal(2, quad.Priority); Assert.Equal("rock", quad.Material!.Texture!.Name); Assert.Equal(4u, quad.Material.Soil);
        Assert.Equal([new(0, 0), new(1, 0), new(1, 1), new(0, 1)], quad.Uvs);
        var tri = model.Polygons[1];
        Assert.Equal(0x100u, tri.Flags); Assert.Equal(0xFFFF0301u, tri.Zone); Assert.Equal(3, tri.Normals.Length);
        Assert.Equal(new Vector3(10, 20, 30), tri.Material!.Color); Assert.Equal(0x66, tri.Material.Flags & 0xFF);
        Assert.Equal(5, model.Vertices.Count);
    }

    [Fact]
    public void BlenderStyleFilesImport()
    {
        // Blender writes TRS, renames duplicate objects with .NNN and keeps custom properties as extras.
        var json = """
        {"asset":{"version":"2.0","generator":"Khronos glTF Blender I/O"},"scene":0,"scenes":[{"nodes":[0]}],
         "nodes":[{"name":"crate.001","translation":[1,2,3],"rotation":[0,0.7071068,0,0.7071068],"scale":[2,2,2],"mesh":0,"extras":{"recoil":{"flags":"0x01000098"}}}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1,"material":0}]}],
         "materials":[{"name":"paint","pbrMetallicRoughness":{"baseColorFactor":[1,0.5,0,1]}}],
         "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":1,"componentType":5121,"count":3,"type":"SCALAR"}],
         "bufferViews":[{"buffer":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":3}],
         "buffers":[{"byteLength":39,"uri":"data:application/octet-stream;base64,AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAgD8AAAAAAAEC"}]}
        """;
        var doc = GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => throw new FileNotFoundException(), Token);
        GameZWorld world = new();
        var node = WorldGltf.Import(doc, "crate.gltf", 0xFF, new() { World = world, Reference = (_, _) => throw new InvalidOperationException(), TextureName = (u, n, _) => n ?? u }).Single();
        Assert.Equal("crate", node.Name);
        Assert.Equal(0x01000098u, node.Flags & WorldGltf.CarriedFlags);
        var matrix = WorldUpdate.LocalMatrix(node)!.Value;
        Assert.Equal(new Vector3(1, 2, 3), matrix.Translation);
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitX, matrix), new Vector3(1, 2, 1)) < 1e-5f); // scaled 2 and turned 90° about Y
        Assert.Equal(new Vector3(255, 128, 0), node.Model!.Polygons[0].Material!.Color);
    }

    [Fact]
    public void MalformedFilesAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read("""{"asset":{"version":"1.0"}}"""u8, _ => [], Token));
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read("""{"asset":{"version":"2.0"},"extensionsRequired":["KHR_draco_mesh_compression"]}"""u8, _ => [], Token));
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read("""{"asset":{"version":"2.0"},"scenes":[{"nodes":[0]}],"nodes":[{"children":[1]},{"children":[0]}]}"""u8, _ => [], Token));
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read("""{"asset":{"version":"2.0"},"buffers":[{"byteLength":100,"uri":"x.bin"}]}"""u8, _ => new byte[4], Token));
        var node = new JsonObject();
        Assert.NotNull(node);
    }

    [Fact]
    public void ThePolygonBuilderFollowsTheEngine()
    {
        WorldMaterial plain = new() { Color = new(1, 2, 3) };
        WorldTexture t = new("t"); WorldMaterial textured = new() { Texture = t };
        ModelBuilder builder = new();
        // Vertices within 0.001 merge; an exactly straight corner is dropped unless it is the first.
        builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(2, 0, -1), new(0.0005f, 0, -1)], [], [], [], plain));
        Assert.Equal(4, builder.Model.Polygons[0].Vertices.Length);
        builder.Add(new([new(0, 0, 0.0004f), new(2, 0, -1), new(0, 0, -1)], [], [], [], plain));
        Assert.Equal(new[] { 0, 2, 3 }, builder.Model.Polygons[1].Vertices);
        // Non-planar polygons fan into triangles.
        builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(1, 1, -1), new(0, 0, -1)], [], [], [], plain));
        Assert.Equal(4, builder.Model.Polygons.Count);
        // UVs shift to their tile and quantize to 1/256; non-affine corners are extrapolated.
        builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(1, 0, -1), new(0, 0, -1)], [new(2.3f, 5.1f), new(3.3f, 5.1f), new(3.3f, 6.1f), new(2.9f, 6.9f)], [], [], textured));
        var uvs = builder.Model.Polygons[^1].Uvs;
        Assert.Equal(new Vector2(0.30078125f, 0.1015625f), uvs[0]);
        Assert.Equal(new Vector2(0.30078125f, 1.1015625f), uvs[3]);
        Assert.All(uvs, uv => Assert.Equal(uv.X * 256, MathF.Round(uv.X * 256)));
        var model = builder.Finish();
        Assert.NotEqual(0, model.BoundsRadius);
    }
}
