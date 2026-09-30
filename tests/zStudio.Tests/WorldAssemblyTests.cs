using System.Numerics;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldAssemblyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public Dictionary<string, byte[]> Files { get; } = files;
        public bool Exists(string relative) => Files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Files[relative];
    }

    private const string Script = """
        set worldName world
        SetModelDirectory ..\data\m1\models
        SetModelDirectory ..\data\common\models
        SetTextureDirectory ..\data\m1\textures
        NewWorld %worldName%
        FindNode %worldName%
        GameGenSetWorld %worldName%
        FindNode %worldName%
        WorldOrigin 0.0 512.0
        WorldExtents 512.0 -512.0
        WorldPartition 256 -256
        LoadGameGen m1.flt m1.flt
        DeleteTree m1.flt
        LoadGameGen crate.flt crate1
        FindNode world
        AddChild crate1
        FindNode crate1
        Object3DTranslate 300 0 100
        FindSubNode lid
        NodeSetDescription top
        GameZWriteZBDFile ..\m1\gamez.zbd
        """;

    private static WorldNode Node(string name, WorldModel? model = null)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Model = model, Flags = WorldGltf.DefaultCarried };
        node.SetPayloadInt(0, 0x28);
        return node;
    }

    private static WorldModel Quad(WorldMaterial material, float size, float y)
    {
        ModelBuilder builder = new();
        builder.Add(new([new(0, y, 0), new(size, y, 0), new(size, y, -size), new(0, y, -size)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], material));
        return builder.Finish();
    }

    private static byte[] Gltf(IReadOnlyList<WorldNode> roots, string stem, Dictionary<string, byte[]> files, string folder)
    {
        var (json, bin) = WorldGltf.Export(roots, 0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 0) }).Write(stem + ".bin");
        files[$"{folder}/{stem}.bin"] = bin;
        return json;
    }

    /// <summary>A mission database with a ground quad, and a crate whose two arms share one claw.</summary>
    private static MemoryFiles Project()
    {
        GameZWorld scratch = new(); WorldTexture rock = new("rock"); WorldMaterial stone = new() { Texture = rock, Flags = 0x1FF };
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes(Script),
            ["data/m1/textures/rock.png"] = [0],
        };
        files["data/m1/models/m1.gltf"] = Gltf([Node("ground", Quad(stone, 64, 0))], "m1", files, "data/m1/models");
        var body = Node("body", Quad(stone, 4, 1)); body.Children.Add(Node("lid", Quad(stone, 4, 2)));
        var claw = Node("claw", Quad(stone, 1, 3));
        var arm1 = Node("arm1"); var arm2 = Node("arm2");
        foreach (var arm in new[] { arm1, arm2 }) { arm.Children.Add(claw); claw.Parents.Add(arm); }
        files["data/common/models/crate.gltf"] = Gltf([body, arm1, arm2], "crate", files, "data/common/models");
        return new(files);
    }

    [Fact]
    public void ScriptsAssembleWorldsLikeTheOriginalBuild()
    {
        var project = Project();
        WorldAssembler assembler = new(project, Token);
        var world = assembler.Assemble("m1.gs");
        Assert.Empty(assembler.Warnings);
        var root = world.Nodes.Single(n => n.Class == WorldNodeClass.World);
        // The database survives its deleted root as world children; the crate is placed and its lid renamed.
        Assert.DoesNotContain(world.Nodes, n => n.Name == "m1.flt");
        var ground = world.Nodes.Single(n => n.Name == "ground");
        var crate = world.Nodes.Single(n => n.Name == "crate1");
        Assert.Equal([root], ground.Parents); Assert.Equal([root], crate.Parents);
        Assert.Equal(new Vector3(300, 0, 100), WorldUpdate.LocalMatrix(crate)!.Value.Translation);
        Assert.Equal((1, 1), (crate.GridColumn, crate.GridRow));
        Assert.Contains(world.Nodes, n => n.Name == "top"); Assert.DoesNotContain(world.Nodes, n => n.Name == "lid");
        // The claw is one node under both arms, so it lacks the single-parent flag.
        var claw = world.Nodes.Single(n => n.Name == "claw");
        Assert.Equal(["arm1", "arm2"], claw.Parents.Select(p => p.Name).Order());
        Assert.Equal(0u, claw.Flags & WorldUpdate.SingleParentFlag);
        Assert.NotEqual(0u, crate.Flags & WorldUpdate.SingleParentFlag);
        Assert.Equal(["rock"], world.Textures.Select(t => t.Name));
        Assert.Equal("data/m1/textures/rock.png", assembler.TextureFiles["rock"]);
        Assert.Contains("data/common/models/crate.gltf", assembler.ModelFiles);
        // The world writes and reads back.
        byte[] bytes = GameZWriter.Write(world, Token);
        var reread = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        Assert.Empty(WorldComparer.Compare(world, reread));
    }

    [Fact]
    public void ShippedWorldsDecomposeIntoTheSourcesThatRebuildThem()
    {
        var project = Project();
        var world = new WorldAssembler(project, Token).Assemble("m1.gs");
        byte[] bytes = GameZWriter.Write(world, Token);
        GameZWorld Shipped() => GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        var scripts = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["m1.gs"] = GameGenScriptText.Tokenize(Script),
        };

        // Decomposition undoes the script's edits: the crate load holds the file's parts under their file names.
        List<string> notes = [];
        var loads = WorldDecomposer.Decompose(Shipped(), ScriptTrace.Trace(n => scripts.GetValueOrDefault(n), "m1.gs", notes), notes);
        Assert.Empty(notes);
        var database = loads.Single(l => l.Database); var crate = loads.Single(l => l.NodeName == "crate1");
        Assert.Equal(["ground"], database.Content.Select(n => n.Name));
        Assert.Equal(["body", "arm1", "arm2"], crate.Content.Select(n => n.Name));
        Assert.Equal("lid", crate.Content[0].Children.Single().Name);

        // Reconstructed sources reassemble into the same world.
        var outputs = WorldSources.Reconstruct([new(1, Shipped())], n => scripts.GetValueOrDefault(n),
            (_, name) => $"data/m1/textures/{name}.png", new HashSet<string> { "data/m1/textures/rock.png" }, _ => 0, notes, Token);
        Assert.Empty(notes);
        Assert.Equal(["data/common/models/crate.bin", "data/common/models/crate.gltf", "data/m1/models/m1.bin", "data/m1/models/m1.gltf"], outputs.Select(o => o.Path).Order(StringComparer.Ordinal));
        MemoryFiles rebuilt = new(new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = project.Files["gamegen/m1.gs"], ["data/m1/textures/rock.png"] = [0] });
        foreach (var output in outputs) rebuilt.Files[output.Path] = output.Bytes;
        WorldAssembler again = new(rebuilt, Token);
        var reassembled = again.Assemble("m1.gs");
        Assert.Empty(again.Warnings);
        Assert.Empty(WorldComparer.Compare(Shipped(), reassembled));
    }

    [Fact]
    public void SharedNodesAndStraightCornersSurviveTheGltfProfile()
    {
        WorldMaterial plain = new() { Color = new(1, 2, 3) };
        // A shared node is written under each parent and joined again on import.
        var claw = Node("claw", Quad(plain, 1, 0));
        var arm1 = Node("arm1"); var arm2 = Node("arm2");
        foreach (var arm in new[] { arm1, arm2 }) { arm.Children.Add(claw); claw.Parents.Add(arm); }
        var (json, bin) = WorldGltf.Export([arm1, arm2], 0xFF, new() { Texture = _ => ("", 0) }).Write("arms.bin");
        Assert.Equal(2, Encoding.UTF8.GetString(json).Split("\"instance\"").Length - 1);
        var doc = GltfDocument.Read(json, _ => bin, Token);
        WorldGltf.ImportContext context = new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, n, _) => n ?? "x" };
        var arms = WorldGltf.Import(doc, "arms.gltf", 0xFF, context);
        Assert.Same(arms[0].Children.Single(), arms[1].Children.Single());
        Assert.Equal(2, arms[0].Children[0].Parents.Count);

        // Fans are rejoined, but never across a straight corner (the engine stored those as separate polygons), and a
        // zero-area triangle is not written.
        GltfPrimitive fan = new();
        fan.Positions.AddRange([new(0, 0, 0), new(2, 0, 0), new(2, 0, -2), new(0, 0, -2)]);
        fan.Indices.AddRange([0, 1, 2, 0, 2, 3]);
        Assert.Single(WorldGltf.MergeFans(fan, false));
        GltfPrimitive straight = new();
        straight.Positions.AddRange([new(0, 0, 0), new(2, 0, 0), new(2, 0, -1), new(2, 0, -2)]);
        straight.Indices.AddRange([0, 1, 2, 0, 2, 3]);
        Assert.Equal(2, WorldGltf.MergeFans(straight, false).Count);
        // A polygon that is not written as a plain fan (a repeated corner, a concave outline) keeps its exact corners.
        WorldModel shapes = new(); shapes.Vertices.AddRange([new(0, 0, 0), new(1, 0, 0), new(1, 0, -1), new(0, 0, -1), new(4, 0, 0), new(6, 0, 0), new(6, 0, -2), new(5, 0, -1), new(4, 0, -2)]);
        shapes.Polygons.Add(new() { Material = plain, Vertices = [0, 1, 2, 2, 3], Normals = [], Uvs = [] });
        shapes.Polygons.Add(new() { Material = plain, Vertices = [4, 5, 6, 7, 8], Normals = [], Uvs = [] });
        var exported = WorldGltf.Export([Node("shapes", shapes)], 0xFF, new() { Texture = _ => ("", 0) });
        var primitive = exported.Roots[0].Mesh!.Primitives.Single();
        Assert.Equal(5 * 3, primitive.Indices.Count);
        Assert.Equal([[0, 1, 2, 2, 3], [4, 5, 6, 7, 8]], WorldGltf.Polygons(primitive, false));
        // Without the record (an editor rebuilt the mesh), the triangles are merged as fans instead.
        primitive.Extras = null;
        var merged = WorldGltf.Polygons(primitive, false);
        Assert.NotEqual([[0, 1, 2, 2, 3], [4, 5, 6, 7, 8]], merged);
        Assert.Equal(5, merged.Sum(p => p.Length - 2));
    }

    [Fact]
    public void MalformedInstancesAndCyclesAreRefused()
    {
        var a = Node("a"); var b = Node("b");
        a.Children.Add(b); b.Parents.Add(a); b.Children.Add(a); a.Parents.Add(b);
        Assert.Throws<InvalidDataException>(() => WorldGltf.Export([a], 0xFF, new() { Texture = _ => ("", 0) }));
        var json = """
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"x","extras":{"recoil":{"instance":0}}}]}
        """u8.ToArray();
        var doc = GltfDocument.Read(json, _ => [], Token);
        WorldGltf.ImportContext context = new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, n, _) => n ?? "x" };
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(doc, "x.gltf", 0xFF, context));
    }
}
