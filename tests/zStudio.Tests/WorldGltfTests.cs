using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
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
        var (json, bin) = exported.Write("tank.bin", TestContext.Current.CancellationToken);
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
         "materials":[{"name":"paint","pbrMetallicRoughness":{"metallicFactor":0,"baseColorFactor":[1,0.5,0,1]}}],
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
        // Corners stay as authored, straight or repeated, as in the shipped models; vertices within 0.001 merge.
        builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(2, 0, -1), new(2, 0, -1), new(0.0005f, 0, -1)], [], [], [], plain));
        Assert.Equal(new[] { 0, 1, 2, 3, 3, 4 }, builder.Model.Polygons[0].Vertices);
        builder.Add(new([new(0, 0, 0.0004f), new(2, 0, -1), new(0, 0, -1)], [], [], [], plain));
        Assert.Equal(new[] { 0, 3, 4 }, builder.Model.Polygons[1].Vertices);
        // A polygon without area draws nothing.
        Assert.False(builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(0, 0, 0), new(3, 0, 0)], [], [], [], plain)));
        // Non-planar polygons stay whole, as the shipped models store them.
        builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(1, 1, -1), new(0, 0, -1)], [], [], [], plain));
        Assert.Equal(3, builder.Model.Polygons.Count); Assert.Equal(4, builder.Model.Polygons[^1].Vertices.Length);
        // UVs shift to their tile and quantize to 1/256; every corner keeps its own, affine or not.
        builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(1, 0, -1), new(0, 0, -1)], [new(2.3f, 5.1f), new(3.3f, 5.1f), new(3.3f, 6.1f), new(2.9f, 6.9f)], [], [], textured));
        var uvs = builder.Model.Polygons[^1].Uvs;
        Assert.Equal(new Vector2(0.30078125f, 0.1015625f), uvs[0]);
        Assert.Equal(new Vector2(0.8984375f, 1.8984375f), uvs[3]);
        Assert.All(uvs, uv => Assert.Equal(uv.X * 256, MathF.Round(uv.X * 256)));
        var model = builder.Finish();
        Assert.NotEqual(0, model.BoundsRadius);
    }

    /// <summary>A data URI holding little-endian floats, then optional bytes.</summary>
    private static string DataUri(float[] floats, byte[]? tail = null)
    {
        byte[] data = new byte[floats.Length * 4 + (tail?.Length ?? 0)];
        for (int i = 0; i < floats.Length; i++) BitConverter.TryWriteBytes(data.AsSpan(i * 4), floats[i]);
        tail?.CopyTo(data, floats.Length * 4);
        return "data:application/octet-stream;base64," + Convert.ToBase64String(data);
    }
    private static WorldGltf.ImportContext Context(GameZWorld world) =>
        new() { World = world, Reference = (_, _) => throw new InvalidDataException("no references"), TextureName = (u, n, _) => (n ?? Path.GetFileNameWithoutExtension(u)).ToLowerInvariant() };
    private static List<WorldNode> Import(string json, GameZWorld world, out WorldGltf.ImportContext context)
    {
        var doc = GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => throw new FileNotFoundException(), Token);
        context = Context(world);
        return WorldGltf.Import(doc, "model.gltf", 0xFF, context);
    }

    [Fact]
    public void ValuesBlenderRewritesImportUnchanged()
    {
        // Blender keeps extras as custom properties but stores a list mixing whole and fractional numbers as floats, so
        // the whole numbers come back as 1.0 (the LOD record of m6's teleporters is such a list).
        string lod = "[1.0, 0.0, 10.0, 100.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 446.59576]";
        // Blender writes a normal for every corner, also of flat surfaces; the material says whether the engine stored them.
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 0, -1];
        float[] normals = [0, 1, 0, 0, 1, 0, 0, 1, 0];
        string json = $$"""
        {"asset":{"version":"2.0","generator":"Khronos glTF Blender I/O"},"scene":0,"scenes":[{"nodes":[0]}],
         "nodes":[{"name":"switch","children":[1],"extras":{"recoil":{"class":"lod","lod":{{lod}} } } },
                  {"name":"part","mesh":0}],
         "meshes":[{"primitives":[
             {"attributes":{"POSITION":0,"NORMAL":1},"material":0},
             {"attributes":{"POSITION":0,"NORMAL":1},"material":1},
             {"attributes":{"POSITION":0,"NORMAL":1},"material":2}],
           "extras":{"recoil":{"scroll":[0.5, 1.25, 3.0]} } }],
         "materials":[{"name":"flat","pbrMetallicRoughness":{"metallicFactor":0},"extras":{"recoil":{"color":[10.0, 20.0, 30.0],"priority":2.0} } },
                      {"name":"smooth","pbrMetallicRoughness":{"metallicFactor":0},"extras":{"recoil":{"color":[10, 20, 31],"normals":true,"backface":1} } },
                      {"name":"new","pbrMetallicRoughness":{"metallicFactor":0,"baseColorFactor":[1,0,0,1]} }],
         "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"}],
         "bufferViews":[{"buffer":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":36}],
         "buffers":[{"byteLength":72,"uri":"{{DataUri([.. positions, .. normals])}}"}]}
        """;
        GameZWorld world = new();
        var root = Import(json, world, out _).Single();
        Assert.Equal(WorldNodeClass.Lod, root.Class);
        Assert.Equal(1, root.PayloadInt(0)); Assert.Equal(100f, root.PayloadFloat(12)); Assert.Equal(1, root.PayloadInt(68)); Assert.Equal(446.59576f, root.PayloadFloat(76));
        var model = root.Children.Single().Model!;
        Assert.Equal((0.5f, 1.25f, 3u), (model.ScrollU, model.ScrollV, model.ScrollFrame));
        Assert.Equal(3, model.Polygons.Count);
        Assert.Equal(2, model.Polygons[0].Priority);
        // Flat surfaces stay without normals; surfaces that had them, and new materials without engine values, keep them.
        Assert.Equal([0, 3, 3], model.Polygons.Select(p => p.Normals.Length));
        // A switch an editor stored as an integer still reads as one.
        Assert.Equal([0u, 0x100u, 0u], model.Polygons.Select(p => p.Flags & 0x100));
    }

    [Fact]
    public void SparseAccessorsApplyTheirValues()
    {
        // Blender writes morph targets as sparse accessors without a buffer view: only the moved corners are stored.
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 0, -1, 1, 0, -1];
        float[] moved = [0, 2, 0, 0, 3, 0];
        byte[] indices = [1, 3];
        string json = $$"""
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
         "nodes":[{"name":"flag","mesh":0}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":2,"material":0,"targets":[{"POSITION":1}]}],"weights":[0.5]}],
         "materials":[{"name":"cloth","pbrMetallicRoughness":{"metallicFactor":0},"extras":{"recoil":{"color":[1,2,3]} } }],
         "accessors":[{"bufferView":0,"componentType":5126,"count":4,"type":"VEC3"},
                      {"componentType":5126,"count":4,"type":"VEC3","sparse":{"count":2,"indices":{"bufferView":2,"componentType":5121},"values":{"bufferView":1} } },
                      {"bufferView":3,"componentType":5121,"count":6,"type":"SCALAR"}],
         "bufferViews":[{"buffer":0,"byteLength":48},{"buffer":0,"byteOffset":48,"byteLength":24},{"buffer":0,"byteOffset":72,"byteLength":2},{"buffer":0,"byteOffset":74,"byteLength":6}],
         "buffers":[{"byteLength":80,"uri":"{{DataUri([.. positions, .. moved], [.. indices, 0, 1, 2, 2, 1, 3])}}"}]}
        """;
        GameZWorld world = new();
        var model = Import(json, world, out _).Single().Model!;
        Assert.Equal(0.5f, model.MorphFactor);
        Dictionary<Vector3, Vector3> deltas = model.Vertices.Select((v, i) => (v, model.Morphs[i])).ToDictionary(p => p.v, p => p.Item2);
        Assert.Equal(new Vector3(0, 2, 0), deltas[new(1, 0, 0)]);
        Assert.Equal(new Vector3(0, 3, 0), deltas[new(1, 0, -1)]);
        Assert.Equal(Vector3.Zero, deltas[new(0, 0, 0)]);
    }

    [Theory]
    [InlineData("""{"asset":{"version":"2.0"},"nodes":[{"name":"x"}""")]
    [InlineData("""{"asset":{"version":"2.0"},"nodes":[{"name":7}]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"nodes":[{"name":"x","matrix":[1,0,0,0,0,1,0,0,0,0,1,0,"a",0,0,1]}]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"nodes":[{"name":"x","translation":[1]}]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"nodes":[{"name":"x","mesh":0}],"meshes":[{"primitives":[{"attributes":{"POSITION":3}}]}]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"accessors":[{"bufferView":0,"componentType":5126,"count":"3","type":"VEC3"}]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"accessors":[{"bufferView":4,"componentType":5126,"count":3,"type":"VEC3"}],"bufferViews":[]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}],"bufferViews":[{"buffer":0,"byteOffset":-12,"byteLength":36}],"buffers":[{"byteLength":36,"uri":"data:application/octet-stream;base64,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"accessors":[{"bufferView":0,"componentType":5126,"count":1000000,"type":"VEC3"}],"bufferViews":[{"buffer":0,"byteLength":12,"byteStride":0}],"buffers":[{"byteLength":12,"uri":"data:application/octet-stream;base64,AAAAAAAAAAAAAAAA"}]}""")]
    [InlineData("""{"asset":{"version":"2.0"},"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"accessors":[{"componentType":5126,"count":2,"type":"VEC3","sparse":{"count":1,"indices":{"bufferView":0,"componentType":5121},"values":{"bufferView":0}}}],"bufferViews":[{"buffer":0,"byteLength":1}],"buffers":[{"byteLength":1,"uri":"data:application/octet-stream;base64,Bw=="}]}""")]
    public void MalformedGltfIsRefusedAsInvalidData(string json)
    {
        // Every malformed file is reported as invalid data, which the build reports against the file, never as another exception.
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => [], Token));
    }

    [Theory]
    [InlineData("""{"recoil":{"flags":"0x1FFFFFFFFF"}}""")]
    [InlineData("""{"recoil":{"flags":7}}""")]
    [InlineData("""{"recoil":{"zone":"far"}}""")]
    [InlineData("""{"recoil":{"class":"lod","lod":[1, null, 3]}}""")]
    [InlineData("""{"recoil":{"class":"lod","lod":[1.5]}}""")]
    [InlineData("""{"recoil":{"name":["a"]}}""")]
    public void MalformedEngineValuesAreRefusedAsInvalidData(string extras)
    {
        string json = $$"""{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"x","extras":{{extras}}}]}""";
        Assert.Throws<InvalidDataException>(() => Import(json, new(), out _));
    }

    [Fact]
    public void DeepHierarchiesAreRefusedWithoutExhaustingTheStack()
    {
        // A chain far deeper than the stack could follow recursively.
        const int depth = 150_000;
        StringBuilder nodes = new();
        for (int i = 0; i < depth; i++) nodes.Append(i == 0 ? "" : ",").Append(i + 1 < depth ? $"{{\"children\":[{i + 1}]}}" : "{}");
        string json = $$"""{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{{nodes}}]}""";
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => [], Token));

        // External references continue the hierarchy: a chain of files, each within the limit, is limited as a whole.
        static GltfDocument Chain(int levels, string? reference)
        {
            GltfDocument doc = new(); GltfNode? parent = null;
            for (int i = 0; i < levels; i++)
            {
                GltfNode node = new() { Name = $"n{i}" };
                if (parent == null) doc.Roots.Add(node); else parent.Children.Add(node);
                parent = node;
            }
            if (reference != null) parent!.Extras = new() { ["recoil"] = new JsonObject { ["ref"] = reference } };
            return doc;
        }
        int loaded = 0;
        WorldGltf.ImportContext context = new()
        {
            World = new(), TextureName = (u, n, _) => n ?? u,
            Reference = (uri, _) => { loaded++; return (Chain(200, $"part{loaded + 1}.gltf"), uri); },
        };
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(Chain(200, "part1.gltf"), "part0.gltf", 0xFF, context));
        Assert.True(loaded < 10);
    }

    [Fact]
    public void ReferencesCannotMultiplyAHierarchyWithoutBound()
    {
        // Each file references the next twice: 2^30 copies of the last one, far more nodes than a world can hold.
        static GltfDocument Twice(int level)
        {
            GltfDocument doc = new();
            for (int i = 0; i < 2; i++) doc.Roots.Add(new() { Name = $"l{level}_{i}", Extras = level < 30 ? new() { ["recoil"] = new JsonObject { ["ref"] = $"level{level + 1}.gltf" } } : null });
            return doc;
        }
        Dictionary<string, GltfDocument> files = [];
        GltfDocument Load(string uri) => files.TryGetValue(uri, out var doc) ? doc : files[uri] = Twice(int.Parse(uri[5..^5]));
        WorldGltf.ImportContext context = new() { World = new(), TextureName = (u, n, _) => n ?? u, Reference = (uri, _) => (Load(uri), uri) };
        var error = Assert.Throws<InvalidDataException>(() => WorldGltf.Import(Load("level0.gltf"), "level0.gltf", 0xFF, context));
        Assert.Contains("nodes", error.Message);
    }

    [Fact]
    public void PointModelsAreWrittenSoEditorsKeepThem()
    {
        // A lens flare: a facade model with only a point entry. glTF meshes need a primitive, and Blender drops meshes
        // without one, so the model's values travel with each node that uses it.
        WorldModel flare = new() { Mode = 1 };
        byte[] record = new byte[76]; record[0] = 7; record[20] = 1;
        flare.Points.Add(new() { Record = record, Vertices = [new(0, 13.5f, 0)] });
        WorldUpdate.RebuildModel(flare);
        WorldNode a = new("halo", WorldNodeClass.Object3D) { Model = flare, Flags = WorldGltf.DefaultCarried }; a.SetPayloadInt(0, 0x28);
        WorldNode b = new("halo", WorldNodeClass.Object3D) { Model = flare, Flags = WorldGltf.DefaultCarried }; b.SetPayloadInt(0, 0x28);
        var (json, bin) = WorldGltf.Export([a, b], 0xFF, new() { Texture = _ => ("", 0) }).Write("halo.bin", TestContext.Current.CancellationToken);
        var parsed = JsonNode.Parse(json)!;
        Assert.All(parsed["meshes"] as JsonArray ?? [], m => Assert.NotEmpty(m!["primitives"]!.AsArray()));
        var doc = GltfDocument.Read(json, _ => bin, Token);
        GameZWorld world = new();
        var nodes = WorldGltf.Import(doc, "halo.gltf", 0xFF, Context(world));
        var model = nodes[0].Model!;
        // Each object read has a model of its own, as the original loader read each object's geometry (1999 redsprks.flt).
        Assert.NotSame(model, nodes[1].Model);
        Assert.Equal(model.Points.Single().Vertices, nodes[1].Model!.Points.Single().Vertices);
        Assert.Equal(1u, model.Mode);
        var point = Assert.Single(model.Points);
        Assert.Equal(new Vector3(0, 13.5f, 0), Assert.Single(point.Vertices));
        Assert.Equal(7, point.Record[0]); Assert.Equal(1, point.Record[20]);
        // Files written before keep loading: an empty mesh with the values still becomes the model.
        string old = """
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"halo","mesh":0}],
         "meshes":[{"primitives":[],"extras":{"recoil":{"mode":1,"points":[{"record":"07000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000","vertices":[[0,13.5,0]]}]}}}]}
        """;
        Assert.Single(Import(old, new(), out _).Single().Model!.Points);
    }

    [Fact]
    public void PointModelsAreSharedByTheCopiesOfOneCache()
    {
        // flare.gltf holds two point-only models (a lens flare's halo and glow). The loader reads a file once per reading:
        // copies of it by the same reference text share its models, as with meshes; another spelling (a second path) or
        // another holder reads it again.
        const string flare = """
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0,1]}],"nodes":[
         {"name":"halo","extras":{"recoil":{"model":{"mode":1,"points":[{"record":"07000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000","vertices":[[0,13.5,0]]}]}}}},
         {"name":"glow","extras":{"recoil":{"model":{"mode":1,"points":[{"record":"07000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000","vertices":[[0,13.5,0]]}]}}}}]}
        """;
        const string lamps = """
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0,1,2,3]}],"nodes":[
         {"name":"l1","extras":{"recoil":{"ref":"flare.gltf"}}},{"name":"l2","extras":{"recoil":{"ref":"flare.gltf"}}},
         {"name":"l3","extras":{"recoil":{"ref":"FLARE.gltf"}}},{"name":"l4","extras":{"recoil":{"ref":"./flare.gltf"}}}]}
        """;
        var flareDoc = GltfDocument.Read(Encoding.UTF8.GetBytes(flare), _ => throw new FileNotFoundException(), Token);
        GameZWorld world = new();
        WorldGltf.ImportContext context = new()
        {
            World = world, Reference = (uri, _) => (flareDoc, "flare.gltf"),
            TextureName = (u, n, _) => (n ?? Path.GetFileNameWithoutExtension(u)).ToLowerInvariant(),
        };
        var roots = WorldGltf.Import(GltfDocument.Read(Encoding.UTF8.GetBytes(lamps), _ => throw new FileNotFoundException(), Token), "lamps.gltf", 0xFF, context);
        WorldModel Model(int lamp, string name) => roots[lamp].Children.Single(c => c.Name == name).Model!;
        // Each object has its own model, even of equal values.
        Assert.NotSame(Model(0, "halo"), Model(0, "glow"));
        // Copies by one reference text share them; the loader matches reference text ignoring case.
        Assert.Same(Model(0, "halo"), Model(1, "halo")); Assert.Same(Model(0, "glow"), Model(1, "glow"));
        Assert.Same(Model(0, "halo"), Model(2, "halo"));
        // A second path reads the file again.
        Assert.NotSame(Model(0, "halo"), Model(3, "halo"));
        // The file's own load is another reading.
        var own = WorldGltf.Import(flareDoc, "flare.gltf", 0xFF, context);
        Assert.NotSame(Model(0, "halo"), own[0].Model);
        Assert.Equal(6, world.Models.Count);
    }

    [Fact]
    public void NamesThatLookLikeBlenderCopiesKeepTheirSuffix()
    {
        WorldNode gun = new("gun.001", WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried }; gun.SetPayloadInt(0, 0x28);
        var (json, bin) = WorldGltf.Export([gun], 0xFF, new() { Texture = _ => ("", 0) }).Write("gun.bin", TestContext.Current.CancellationToken);
        var node = WorldGltf.Import(GltfDocument.Read(json, _ => bin, Token), "gun.gltf", 0xFF, Context(new())).Single();
        Assert.Equal("gun.001", node.Name);
    }

    [Fact]
    public void ModelNormalsStayWithinTheEngineLimit()
    {
        // The game transforms a model's normals into a 1,024-entry buffer (retail g_zModel_TransformedNormals) and the
        // build stopped adding normals after 921, so a model never stores more; later polygons are drawn without normals.
        WorldMaterial plain = new() { Color = new(1, 2, 3) };
        ModelBuilder builder = new();
        for (int i = 0; i < 1200; i++)
        {
            float angle = i * 0.001f;
            Vector3 n = new(MathF.Cos(angle), 0, MathF.Sin(angle));
            int x = i % 10, z = i / 10 % 10;
            builder.Add(new([new(x, 0, -z), new(x + 1, 0, -z), new(x, 0, -z - 1)], [], [n, n, n], [], plain));
        }
        var model = builder.Finish();
        Assert.Equal(1200, model.Polygons.Count);
        Assert.True(model.Normals.Count <= ModelBuilder.MaximumVertices, $"{model.Normals.Count} normals");
        Assert.All(model.Polygons, p => Assert.All(p.Normals, n => Assert.InRange(n, 0, model.Normals.Count - 1)));
        Assert.Contains(model.Polygons, p => p.Normals.Length == 0);
        Assert.Contains(builder.Warnings, w => w.Contains("normals", StringComparison.Ordinal));
    }

    [Fact]
    public void EmbeddedImagesAreRefused()
    {
        // Blender's .glb export embeds images; accepting it must not publish plain surfaces.
        string json = $$"""
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"box","mesh":0}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0},"material":0}]}],
         "materials":[{"name":"paint","pbrMetallicRoughness":{"metallicFactor":0,"baseColorTexture":{"index":0} } }],
         "textures":[{"source":0}],"images":[{"bufferView":1,"mimeType":"image/png","name":"paint"}],
         "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}],
         "bufferViews":[{"buffer":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":4}],
         "buffers":[{"byteLength":40,"uri":"{{DataUri([0, 0, 0, 1, 0, 0, 0, 0, -1], [1, 2, 3, 4])}}"}]}
        """;
        Assert.Contains("embedded image", Assert.Throws<InvalidDataException>(() => Import(json, new(), out _)).Message);
    }

    [Fact]
    public void ViewersSeeTransparentTexturesAndNoCollisionVolumes()
    {
        WorldTexture glow = new("glow"), cut = new("cut"), rock = new("rock");
        WorldMaterial violet = new() { Color = new(63, 15, 254), Flags = 0xFF };
        static WorldModel Quad(WorldMaterial material, float y)
        {
            ModelBuilder builder = new();
            builder.Add(new([new(0, y, 0), new(1, y, 0), new(1, y, -1), new(0, y, -1)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], material));
            return builder.Finish();
        }
        static WorldNode Node(string name, WorldModel model) { WorldNode node = new(name, WorldNodeClass.Object3D) { Model = model, Flags = WorldGltf.DefaultCarried }; node.SetPayloadInt(0, 0x28); return node; }
        // The pickup's collision volume shares its engine material with a wall the game draws.
        List<WorldNode> roots = [Node("arc", Quad(new() { Texture = glow, Flags = 0x1FF }, 0)), Node("lock", Quad(new() { Texture = cut, Flags = 0x1FF }, 1)),
            Node("box", Quad(new() { Texture = rock, Flags = 0x1FF }, 2)), Node("wall", Quad(violet, 3)), Node("bvol", Quad(violet, 4))];
        var (json, bin) = WorldGltf.Export(roots, 0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 0) }).Write("ammo.bin", TestContext.Current.CancellationToken);
        static TextureTransparency? Transparency(string uri) => uri switch
        {
            "../textures/glow.png" => TextureTransparency.Alpha, "../textures/cut.png" => TextureTransparency.Keyed, "../textures/rock.png" => TextureTransparency.Opaque, _ => null,
        };
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.True(WorldGltf.ApplyPresentation(root, Transparency, pickup: true));
        int MaterialOf(string node)
        {
            int mesh = root["nodes"]!.AsArray().Single(n => (string?)n!["name"] == node)!["mesh"]!.GetValue<int>();
            return root["meshes"]![mesh]!["primitives"]![0]!["material"]!.GetValue<int>();
        }
        JsonNode Material(string node) => root["materials"]![MaterialOf(node)]!;
        // Graded alpha blends, alpha of only 0 or 255 is a mask at the cutoff the packs key at, opaque textures stay opaque.
        Assert.Equal("BLEND", (string?)Material("arc")["alphaMode"]);
        Assert.Equal("MASK", (string?)Material("lock")["alphaMode"]); Assert.Null(Material("lock")["alphaCutoff"]);
        Assert.Null(Material("box")["alphaMode"]); Assert.Null(Material("wall")["alphaMode"]);
        // The volume got a fully transparent copy of the wall's material, with its colour and its engine opacity.
        Assert.NotEqual(MaterialOf("wall"), MaterialOf("bvol"));
        var hidden = Material("bvol");
        Assert.Equal((string?)Material("wall")["name"] + "~hidden", (string?)hidden["name"]);
        Assert.Equal("MASK", (string?)hidden["alphaMode"]);
        Assert.Equal([0.24705882, 0.05882353, 0.99607843, 0], hidden["pbrMetallicRoughness"]!["baseColorFactor"]!.AsArray().Select(c => Math.Round(c!.GetValue<double>(), 8)));
        Assert.Equal(255, hidden["extras"]![WorldGltf.Key]!["opacity"]!.GetValue<int>());
        Assert.Equal([63, 15, 254], hidden["extras"]![WorldGltf.Key]!["color"]!.AsArray().Select(c => c!.GetValue<int>()));
        string presented = root.ToJsonString();
        Assert.False(WorldGltf.ApplyPresentation(root, Transparency, pickup: true));
        Assert.Equal(presented, root.ToJsonString());

        // Builds read the same engine values from either file, also after an editor turns the volume's material to BLEND.
        var blended = JsonNode.Parse(presented)!.AsObject();
        blended["materials"]![MaterialOf("bvol")]!["alphaMode"] = "BLEND";
        List<string> Surfaces(string gltf)
        {
            var doc = GltfDocument.Read(Encoding.UTF8.GetBytes(gltf), _ => bin, Token);
            WorldGltf.ImportContext context = new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (uri, name, _) => name ?? Path.GetFileNameWithoutExtension(uri) };
            return [.. WorldGltf.Import(doc, "ammo.gltf", 0xFF, context).SelectMany(n => n.Model!.Polygons.Select(p =>
                $"{n.Name} {p.Material!.Flags:X} {p.Material.PackedColor:X} {p.Material.Color} {p.Material.Texture?.Name} {p.Material.Soil} {p.Flags:X} {p.Zone:X} {p.Normals.Length}"))];
        }
        var expected = Surfaces(Encoding.UTF8.GetString(json));
        Assert.Contains(expected, surface => surface.StartsWith("bvol FF ", StringComparison.Ordinal));
        Assert.Equal(expected, Surfaces(presented));
        Assert.Equal(expected, Surfaces(blended.ToJsonString()));

        // Files of other shapes are left alone.
        var odd = JsonNode.Parse("""
            {"nodes":[{"name":"bvol","mesh":0},{"name":"bvol","mesh":"0"}],"meshes":[{"primitives":[{"material":99},{"material":1}]}],
             "materials":[{"pbrMetallicRoughness":{"metallicFactor":0,"baseColorTexture":{"index":7},"baseColorFactor":[1,"x"]}},5],"textures":[{"source":-1}]}
            """)!.AsObject();
        Assert.False(WorldGltf.ApplyPresentation(odd, _ => TextureTransparency.Alpha, pickup: true));

        // A volume an editor made translucent keeps the opacity import read from it, also in a material without engine
        // attributes (given normals, so import reads them as before).
        static JsonObject Volume(string extras) => JsonNode.Parse($$"""
            {"nodes":[{"name":"bvol","mesh":0}],"meshes":[{"primitives":[{"material":0}]}],
             "materials":[{"name":"glass","alphaMode":"BLEND","pbrMetallicRoughness":{"metallicFactor":0,"baseColorFactor":[1,1,1,0.5]}{{extras}}}]}
            """)!.AsObject();
        var recorded = Volume(""","extras":{"recoil":{"color":[1,2,3]}}""");
        Assert.True(WorldGltf.ApplyPresentation(recorded, _ => null, pickup: true));
        Assert.Equal(128, recorded["materials"]![0]!["extras"]![WorldGltf.Key]!["opacity"]!.GetValue<int>());
        Assert.Equal("MASK", (string?)recorded["materials"]![0]!["alphaMode"]);
        var bare = Volume("");
        Assert.True(WorldGltf.ApplyPresentation(bare, _ => null, pickup: true));
        Assert.Equal("MASK", (string?)bare["materials"]![0]!["alphaMode"]);
        Assert.Equal(128, bare["materials"]![0]!["extras"]![WorldGltf.Key]!["opacity"]!.GetValue<int>());
        Assert.True(bare["materials"]![0]!["extras"]![WorldGltf.Key]!["normals"]!.GetValue<bool>());
    }
}
