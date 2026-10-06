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

    /// <summary>A mission database with a ground quad, and a crate whose two arms share one claw (and a collision volume, when asked).</summary>
    private static MemoryFiles Project(bool volume = false)
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
        List<WorldNode> crate = [body, arm1, arm2];
        if (volume) crate.Add(Node("bvol", Quad(new() { Color = new(63, 15, 254), Flags = 0xFF }, 8, 0)));
        files["data/common/models/crate.gltf"] = Gltf(crate, "crate", files, "data/common/models");
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
        var project = Project(volume: true);
        var world = new WorldAssembler(project, Token).Assemble("m1.gs");
        byte[] bytes = GameZWriter.Write(world, Token);
        GameZWorld Shipped() => GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        var scripts = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["m1.gs"] = GameGenScriptText.Tokenize(Script),
        };

        // Decomposition undoes the script's edits: the crate load holds the file's parts under their file names.
        List<string> notes = [];
        var loads = WorldDecomposer.Decompose(Shipped(), ScriptTrace.Trace(n => scripts.GetValueOrDefault(n), "m1.gs", notes), notes, TestContext.Current.CancellationToken);
        Assert.Empty(notes);
        var database = loads.Single(l => l.Database); var crate = loads.Single(l => l.NodeName == "crate1");
        Assert.Equal(["ground"], database.Content.Select(n => n.Name));
        Assert.Equal(["body", "arm1", "arm2", "bvol"], crate.Content.Select(n => n.Name));
        Assert.Equal("lid", crate.Content[0].Children.Single().Name);

        // Reconstructed sources reassemble into the same world, also with their hints for viewers (a transparent texture;
        // the crate's collision volume stays visible, since crate1 is no pickup and the game draws it).
        var outputs = WorldSources.Reconstruct([new(1, Shipped())], n => scripts.GetValueOrDefault(n),
            (_, name) => $"data/m1/textures/{name}.png", new HashSet<string> { "data/m1/textures/rock.png" }, _ => 0, notes, Token,
            transparency: path => path == "data/m1/textures/rock.png" ? TextureTransparency.Alpha : null);
        Assert.Empty(notes);
        string crateJson = Encoding.UTF8.GetString(outputs.Single(o => o.Path == "data/common/models/crate.gltf").Bytes);
        Assert.Contains("\"alphaMode\": \"BLEND\"", crateJson); Assert.DoesNotContain("~hidden", crateJson);
        Assert.Contains("\"alphaMode\": \"BLEND\"", Encoding.UTF8.GetString(outputs.Single(o => o.Path == "data/m1/models/m1.gltf").Bytes));
        Assert.Equal(["data/common/models/crate.bin", "data/common/models/crate.gltf", "data/m1/models/m1.bin", "data/m1/models/m1.gltf"], outputs.Select(o => o.Path).Order(StringComparer.Ordinal));
        MemoryFiles rebuilt = new(new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = project.Files["gamegen/m1.gs"], ["data/m1/textures/rock.png"] = [0] });
        foreach (var output in outputs) rebuilt.Files[output.Path] = output.Bytes;
        WorldAssembler again = new(rebuilt, Token);
        var reassembled = again.Assemble("m1.gs");
        Assert.Empty(again.Warnings);
        Assert.Empty(WorldComparer.Compare(Shipped(), reassembled));
    }

    [Fact]
    public void SharedVehiclesAndEffectsAreKeptInTheCommonFolders()
    {
        // Each mission's vehicle script loads tank.flt from the mission's own folder; m1 and m2 hold the same tank, m3 another.
        // Every mission's weapons script loads an effect from data/effects/models.
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        WorldMaterial paint = new() { Color = new(1, 2, 3), Flags = 0xFF };
        files["gamegen/support/weapons.gw"] = Encoding.ASCII.GetBytes("SetModelDirectory ..\\data\\effects\\models\nLoadGameGen spark.flt spark\n");
        files["data/effects/models/spark.gltf"] = Gltf([Node("flash", Quad(paint, 1, 2))], "spark", files, "data/effects/models");
        for (int m = 1; m <= 3; m++)
        {
            files[$"gamegen/m{m}.gs"] = Encoding.ASCII.GetBytes($"""
                set worldName world
                SetModelDirectory ..\data\m{m}\models
                SetModelDirectory ..\data\common\models
                SetModelDirectory ..\data\common\effects\models
                SetModelDirectory ..\data\effects\models
                NewWorld %worldName%
                FindNode %worldName%
                GameGenSetWorld %worldName%
                FindNode %worldName%
                WorldOrigin 0.0 512.0
                WorldExtents 512.0 -512.0
                WorldPartition 256 -256
                LoadGameGen m{m}.flt m{m}.flt
                DeleteTree m{m}.flt
                source support\weapons.gw
                source support\bft{m}.gw
                GameZWriteZBDFile ..\m{m}\gamez.zbd
                """);
            files[$"gamegen/support/bft{m}.gw"] = Encoding.ASCII.GetBytes($"SetModelDirectory ..\\data\\m{m}\\models\\bft\nLoadGameGen tank.flt tank\n");
            files[$"data/m{m}/models/m{m}.gltf"] = Gltf([Node("ground", Quad(paint, 64, 0))], $"m{m}", files, $"data/m{m}/models");
            files[$"data/m{m}/models/bft/tank.gltf"] = Gltf([Node("hull", Quad(paint, m == 3 ? 6 : 4, 1))], "tank", files, $"data/m{m}/models/bft");
        }
        MemoryFiles project = new(files);
        byte[][] shipped = [.. Enumerable.Range(1, 3).Select(m => GameZWriter.Write(new WorldAssembler(project, Token).Assemble($"m{m}.gs"), Token))];
        GameZWorld Shipped(int m) => GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", shipped[m - 1], token: Token), Token);
        var scripts = files.Where(f => f.Key.StartsWith("gamegen/", StringComparison.Ordinal)).ToDictionary(f => f.Key["gamegen/".Length..].Replace('/', '\\'),
            f => GameGenScriptText.Tokenize(Encoding.ASCII.GetString(f.Value)), StringComparer.OrdinalIgnoreCase);

        // The version two missions share is one file in data/common/models, which their loads search after their own
        // folders; m3's own version stays in its folder, found first. The effect is kept in data/common/effects/models,
        // which the weapons script's loads search after data/effects/models.
        List<string> notes = [];
        var outputs = WorldSources.Reconstruct([.. Enumerable.Range(1, 3).Select(m => new WorldSources.MissionWorld(m, Shipped(m)))], n => scripts.GetValueOrDefault(n),
            (_, _) => null, new HashSet<string>(), _ => 0, notes, Token);
        Assert.Empty(notes);
        Assert.Equal(["data/common/effects/models/spark.gltf", "data/common/models/tank.gltf", "data/m1/models/m1.gltf", "data/m2/models/m2.gltf", "data/m3/models/bft/tank.gltf", "data/m3/models/m3.gltf"],
            outputs.Select(o => o.Path).Where(p => p.EndsWith(".gltf", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        MemoryFiles rebuilt = new(files.Where(f => f.Key.StartsWith("gamegen/", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal));
        foreach (var output in outputs) rebuilt.Files[output.Path] = output.Bytes;
        for (int m = 1; m <= 3; m++)
        {
            WorldAssembler again = new(rebuilt, Token);
            var world = again.Assemble($"m{m}.gs");
            Assert.Empty(again.Warnings);
            Assert.Empty(WorldComparer.Compare(Shipped(m), world));
        }
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

    private static GameZWorld AssembleScript(string script, out WorldAssembler assembler, params (string Path, string Text)[] others)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes(script) };
        foreach (var (path, text) in others) files[path] = Encoding.ASCII.GetBytes(text);
        assembler = new(new MemoryFiles(files), Token);
        return assembler.Assemble("m1.gs");
    }

    [Fact]
    public void ConditionsAndSwitchesFollowTheRetailInterpreter()
    {
        // CZInterp::HandleBuiltinCommand: a condition holds when its macro is exactly TRUE (names are case-sensitive),
        // || and && combine left to right, and while skipping only endif counts, so the first endif ends the skip, even
        // one in another file. ParseBoolToken turns on only for on/true; a missing argument or 1 is off.
        const string script = """
            set A TRUE
            set B 1
            set lower true
            NewWorld world
            ifdef A
            NewObject3D a_on
            endif
            ifdef B
            NewObject3D b_on
            endif
            ifndef B
            NewObject3D not_b
            endif
            ifdef lower
            NewObject3D lower_on
            endif
            ifdef B || A
            NewObject3D either
            endif
            ifdef A && B
            NewObject3D both
            endif
            ifdef B
            ifdef A
            endif
            NewObject3D after_inner
            endif
            ifdef a
            NewObject3D case_sensitive
            endif
            source sub.gw
            NewObject3D skipped_by_sub
            endif
            NewObject3D resumed
            NewObject3D flags1
            SetLandmark on
            SetLandmark
            NewObject3D flags2
            SetLandmark 1
            NewObject3D flags3
            SetLandmark TRUE
            GameZWriteZBDFile ..\m1\gamez.zbd
            set cycle wave07
            CycleTextureSetMap %cycle%.tif
            """;
        var world = AssembleScript(script, out var assembler, ("gamegen/sub.gw", "ifdef B\nNewObject3D in_sub\n"));
        var names = world.Nodes.Select(n => n.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "a_on", "not_b", "either", "after_inner", "resumed" }, names);
        Assert.Empty(names.Intersect(["b_on", "lower_on", "both", "case_sensitive", "skipped_by_sub", "in_sub"]));
        uint Landmark(string name) => world.Nodes.Single(n => n.Name == name).Flags & WorldUpdate.LandmarkFlag;
        Assert.Equal([0u, 0u, WorldUpdate.LandmarkFlag], [Landmark("flags1"), Landmark("flags2"), Landmark("flags3")]);
        // Macros set after the world is written still expand in the texture registrations that follow.
        Assert.Contains("wave07", assembler.ScriptTextures);
        // A retail command that changes nodes but is not built is reported rather than silently ignored.
        AssembleScript("NewWorld world\nNewObject3D a\nNodeSetActive off\nGameZWriteZBDFile x\n", out var unsupported);
        Assert.Contains(unsupported.Warnings, w => w.Contains("NodeSetActive", StringComparison.Ordinal));

        // Reconstruction traces the same instructions.
        Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> scripts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["m1.gs"] = GameGenScriptText.Tokenize(script), ["sub.gw"] = GameGenScriptText.Tokenize("ifdef B\nNewObject3D in_sub\n"),
        };
        var traced = ScriptTrace.Trace(n => scripts.GetValueOrDefault(n), "m1.gs", []).Where(t => t.Command == "NewObject3D").Select(t => t.Args[0]).ToList();
        Assert.Equal(["a_on", "not_b", "either", "after_inner", "resumed", "flags1", "flags2", "flags3"], traced);
    }

    [Fact]
    public void MacroExpansionIsBoundedLikeTheRetailBuffer()
    {
        // Each line doubles the macro; forty lines would make a trillion characters. The retail expansion buffer holds
        // 1,024 bytes, so a longer expansion is refused.
        string script = "set x ab\n" + string.Concat(Enumerable.Repeat("set x %x%%x%\n", 40)) + "NewWorld world\nGameZWriteZBDFile x\n";
        var error = Assert.Throws<InvalidDataException>(() => AssembleScript(script, out _));
        Assert.Contains("1023", error.Message);
        var lines = GameGenScriptText.Tokenize(script);
        Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(n => n == "m1.gs" ? lines : null, "m1.gs", []));
    }

    [Fact]
    public void DatabasePartsAreFilesOfTheirOwnThatTheBuildCopies()
    {
        // The database references two parts (groups naming files of their own) and two objects reference one model by two
        // paths; the second part's reference also has a record of its own.
        WorldTexture rock = new("rock"); WorldMaterial stone = new() { Texture = rock, Flags = 0x1FF };
        Dictionary<string, byte[]> Files(string secondPath)
        {
            Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
            {
                ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes(Script),
                ["data/m1/textures/rock.png"] = [0],
                ["data/common/models/crate.gltf"] = Project().Files["data/common/models/crate.gltf"],
                ["data/common/models/crate.bin"] = Project().Files["data/common/models/crate.bin"],
            };
            files["data/m1/models/box.gltf"] = Gltf([Node("healthy", Quad(stone, 1, 0))], "box", files, "data/m1/models");
            Dictionary<WorldNode, string> references = new(ReferenceEqualityComparer.Instance);
            WorldNode Uses(string name, string uri) { var o = Node(name, Quad(stone, 2, 0)); var r = Node("box.flt"); r.Children.Add(Node("healthy")); o.Children.Add(r); references[r] = uri; return o; }
            HashSet<WorldNode> groups = new(ReferenceEqualityComparer.Instance);
            Dictionary<WorldNode, IReadOnlyCollection<WorldNode>> content = new(ReferenceEqualityComparer.Instance);
            byte[] Write(IReadOnlyList<WorldNode> roots, string stem)
            {
                var (json, bin) = WorldGltf.Export(roots, 0xFF, new()
                {
                    Texture = t => ($"../textures/{t.Name}.png", 0),
                    Reference = n => references.GetValueOrDefault(n), Group = groups.Contains, Content = n => content.GetValueOrDefault(n),
                }).Write(stem + ".bin");
                files[$"data/m1/models/{stem}.bin"] = bin;
                return json;
            }
            var yard = Node("yard"); groups.Add(yard); yard.Children.Add(Uses("crate", "box.gltf"));
            files["data/m1/models/m1_01.gltf"] = Write([yard, Node("post", Quad(stone, 1, 0))], "m1_01");
            files["data/m1/models/m1_02.gltf"] = Write([Node("tower", Quad(stone, 3, 0))], "m1_02");
            var part1 = Node("m1_01.flt"); groups.Add(part1); references[part1] = "m1_01.gltf"; part1.Children.Add(Node("placeholder")); content[part1] = [.. part1.Children];
            var part2 = Node("m1_02.flt"); groups.Add(part2); references[part2] = "m1_02.gltf"; var tower = Node("tower"); var sign = Node("sign", Quad(stone, 1, 0));
            part2.Children.Add(tower); part2.Children.Add(sign); content[part2] = [tower];
            files["data/m1/models/m1.gltf"] = Write([part1, Uses("lamp", "box.gltf"), Uses("lamp2", secondPath), part2], "m1");
            return files;
        }

        (GameZWorld World, WorldAssembler Assembler) Build(string secondPath)
        {
            var assembler = new WorldAssembler(new MemoryFiles(Files(secondPath)), Token);
            return (assembler.Assemble("m1.gs"), assembler);
        }
        var (world, assembler) = Build("./box.gltf");
        Assert.Empty(assembler.Warnings);
        var root = world.Nodes.Single(n => n.Class == WorldNodeClass.World);
        // The parts' objects join the world as they were made: the first part is copied after the next record, the second
        // (whose reference has a record of its own) at once, before that record. Groups and part references are deleted.
        string[] members = ["lamp", "crate", "post", "lamp2", "tower", "sign"];
        var cell = root.Areas.Single(a => a.Nodes.Any(n => n.Name == "lamp"));
        Assert.Equal(members, cell.Nodes.Where(c => members.Contains(c.Name)).Select(c => c.Name));
        Assert.DoesNotContain(world.Nodes, n => n.Name is "yard" or "m1_01.flt" or "m1_02.flt" or "placeholder");
        var crate = world.Nodes.Single(n => n.Name == "crate");
        Assert.Equal([root], crate.Parents);
        Assert.True(assembler.Provenance[crate].Database); Assert.True(assembler.Provenance[crate].Part);
        Assert.Equal("data/m1/models/m1_01.gltf", assembler.Provenance[crate].ModelFile);
        var lamp = world.Nodes.Single(n => n.Name == "lamp");
        Assert.True(assembler.Provenance[lamp].Database); Assert.False(assembler.Provenance[lamp].Part);
        // A model named by a second path is cached a second time: two more slots than when both name it alike.
        int Slots(GameZWorld w) => GameZWriter.NodeSlots(w).Values.Max();
        var (alike, _) = Build("box.gltf");
        Assert.Equal(Slots(alike) + 2, Slots(world));
        // Copies of one cache share its models; a second path's cache and the part's own reading of the file have their own.
        WorldModel Box(GameZWorld w, string user) => WorldAssembler.Subtree(w.Nodes.Single(n => n.Name == user)).Single(n => n.Name == "healthy").Model!;
        Assert.NotSame(Box(world, "lamp"), Box(world, "lamp2"));
        Assert.Same(Box(alike, "lamp"), Box(alike, "lamp2"));
        Assert.NotSame(Box(alike, "lamp"), Box(alike, "crate"));
        // Models are stored in the order the loader read them: the part's cache (with its own cache of the box) first,
        // then the database's caches, then the database's records.
        int At(GameZWorld w, WorldModel m) => w.Models.IndexOf(m);
        Assert.True(At(world, Box(world, "crate")) < At(world, Box(world, "lamp")));
        Assert.True(At(world, Box(world, "lamp")) < At(world, Box(world, "lamp2")));
        Assert.True(At(world, Box(world, "lamp2")) < At(world, world.Nodes.Single(n => n.Name == "lamp").Model!));
    }

    [Fact]
    public void SourceScriptsNameTheProjectsModelAndTextureFiles()
    {
        // Model files become .gltf (also through the macro LoadGameGen reads), textures .png; node names keep their spelling.
        var scripts = new[]
        {
            GameGenScriptText.Tokenize("set dbName m1.flt\nset other m1.flt\nLoadGameGen %dbName% %dbName%\nDeleteTree %dbName%\n"),
            GameGenScriptText.Tokenize("LoadGameGen rfpg_mzl.flt rfpg_mzl.flt\nLoadGameGen vtol.FLT vtol1\nFindNode sizzle.flt\nAddChild lite_ref.flt\nCycleTextureSetMap fire101.tif\nWriteTextureSetMap fire1 water.TGA\nTextureAdd name\n"),
        };
        var macros = GameGenScriptText.ModelMacros(scripts);
        Assert.Equal(["dbName"], macros);
        string Text(int i) => GameGenScriptText.Write(GameGenScriptText.ProjectFileNames(scripts[i], macros));
        Assert.Equal("set dbName m1.gltf\nset other m1.flt\nLoadGameGen %dbName% %dbName%\nDeleteTree %dbName%\n", Text(0));
        Assert.Equal("LoadGameGen rfpg_mzl.gltf rfpg_mzl.flt\nLoadGameGen vtol.gltf vtol1\nFindNode sizzle.flt\nAddChild lite_ref.flt\nCycleTextureSetMap fire101.png\nWriteTextureSetMap fire1 water.png\nTextureAdd name\n", Text(1));

        // The build loads the file a script names, and an OpenFlight name (older projects) finds its glTF.
        var project = Project();
        project.Files["data/m1/models/crate.glb"] = project.Files["data/common/models/crate.gltf"];
        var assembler = new WorldAssembler(project, Token);
        assembler.Assemble("m1.gs");
        Assert.Equal("data/m1/models/crate.glb", assembler.ResolveModel("crate.glb"));
        Assert.Equal("data/common/models/crate.gltf", assembler.ResolveModel("crate.gltf"));
        Assert.Equal("data/common/models/crate.gltf", assembler.ResolveModel("crate.flt"));
        Assert.Null(assembler.ResolveModel("missing.gltf"));
    }

    [Fact]
    public void NodeSetLightingAppliesToTheWholeSubtree()
    {
        // CZNode::AssignInt32ToDiRecursive: the models of the current node and of everything below it.
        var project = Project();
        project.Files["gamegen/m1.gs"] = Encoding.ASCII.GetBytes(Script.Replace("GameZWriteZBDFile", "FindNode crate1\nNodeSetLighting off\nGameZWriteZBDFile", StringComparison.Ordinal));
        var world = new WorldAssembler(project, Token).Assemble("m1.gs");
        var crate = world.Nodes.Single(n => n.Name == "crate1");
        var models = WorldAssembler.Subtree(crate).Select(n => n.Model).OfType<WorldModel>().ToList();
        Assert.Equal(3, models.Count);
        Assert.All(models, m => Assert.Equal(0u, m.Flags & 1));
        Assert.Equal(1u, world.Nodes.Single(n => n.Name == "ground").Model!.Flags & 1);
    }

    [Theory]
    [InlineData("1 -1")]
    [InlineData("0.001 -0.001")]
    [InlineData("0 0")]
    public void PartitionsTheReaderCannotHoldAreRefused(string cells)
    {
        // Every cell is allocated and written, and the reader accepts at most 65,536; a typo must not allocate billions.
        string script = $"NewWorld world\nWorldOrigin 0.0 512.0\nWorldExtents 512.0 -512.0\nWorldPartition {cells}\nGameZWriteZBDFile x\n";
        var error = Assert.Throws<InvalidDataException>(() => AssembleScript(script, out _));
        Assert.Contains("cell", error.Message);
    }

    [Fact]
    public void ScriptHierarchiesStayWithinBounds()
    {
        // Diamonds: every node of a level is a child of both nodes of the level above, so there are 2^60 paths from the
        // bottom to the top. Ancestor and FindSubNode searches visit each node once.
        StringBuilder diamond = new("NewWorld world\nNewObject3D a0\nNewObject3D b0\n");
        for (int i = 1; i <= 60; i++)
        {
            diamond.Append($"NewObject3D a{i}\nNewObject3D b{i}\n");
            foreach (string parent in new[] { $"a{i - 1}", $"b{i - 1}" })
                foreach (string child in new[] { $"a{i}", $"b{i}" }) diamond.Append($"FindNode {parent}\nAddChild {child}\n");
        }
        diamond.Append("FindNode a0\nFindSubNode missing\nFindNode a0\nFindSubNode b60\nNodeSetLighting off\nFindNode a60\nAddChild a0\nGameZWriteZBDFile x\n");
        var world = AssembleScript(diamond.ToString(), out var assembler);
        Assert.Equal(122, world.Nodes.Count(n => n.Class == WorldNodeClass.Object3D));
        Assert.Contains(assembler.Warnings, w => w.Contains("own ancestor", StringComparison.Ordinal));

        // A chain as deep as a world may be builds; a deeper one is refused instead of exhausting the stack.
        static string Chain(int depth)
        {
            StringBuilder text = new("NewWorld world\nNewObject3D n0\n");
            for (int i = 1; i < depth; i++) text.Append($"NewObject3D n{i}\nFindNode n{i - 1}\nAddChild n{i}\n");
            return text.Append("GameZWriteZBDFile x\n").ToString();
        }
        var deep = AssembleScript(Chain(WorldUpdate.MaximumDepth), out _);
        Assert.NotEmpty(GameZWriter.Write(deep, Token));
        var error = Assert.Throws<InvalidDataException>(() => AssembleScript(Chain(20_000), out _));
        Assert.Contains("deeper", error.Message);
    }

    [Fact]
    public void MaterialsAreSharedAcrossLoadsAndTextureNamesAreChecked()
    {
        // The ground and the crate use one material; identical materials in different loads share one slot.
        var world = new WorldAssembler(Project(), Token).Assemble("m1.gs");
        Assert.Single(world.Materials);

        // A file with many surfaces: each primitive's material is found by value, not by comparing with every other.
        const int count = 60_000;
        GltfDocument doc = new(); GltfMesh mesh = new() { Name = "many" };
        for (int i = 0; i < count; i++)
        {
            GltfPrimitive primitive = new() { Material = new() { Name = $"m{i}", BaseColor = new(i % 256 / 255f, i / 256 % 256 / 255f, i / 65536 / 255f, 1) } };
            primitive.Positions.AddRange([new(0, 0, 0), new(1, 0, 0), new(0, 0, -1)]); primitive.Indices.AddRange([0, 1, 2]);
            mesh.Primitives.Add(primitive);
        }
        doc.Roots.Add(new() { Name = "many", Mesh = mesh });
        GameZWorld target = new();
        WorldGltf.Import(doc, "many.gltf", 0xFF, new() { World = target, Reference = (_, _) => throw new InvalidDataException(), TextureName = (_, n, _) => n ?? "x" });
        Assert.Equal(count, target.Materials.Count);
        Assert.Equal(count, target.Models.Single().Polygons.Select(p => p.Material).Distinct().Count());

        // Texture names are stored in a Latin-1 field and name the pack's files, so a name with a path is refused.
        var project = Project();
        string crate = Encoding.UTF8.GetString(project.Files["data/common/models/crate.gltf"]);
        project.Files["data/common/models/crate.gltf"] = Encoding.UTF8.GetBytes(crate.Replace("\"texture\": \"rock\"", "\"texture\": \"../rock\"", StringComparison.Ordinal));
        var error = Assert.Throws<InvalidDataException>(() => new WorldAssembler(project, Token).Assemble("m1.gs"));
        Assert.Contains("Latin-1", error.Message);
    }

    [Fact]
    public async Task MalformedModelsFailTheirWorldWithTheirPath()
    {
        // A model a user edited by hand (here cut short) fails the world that loads it, named in the report, while
        // everything else is still checked.
        using var fixture = new SourceWorldFixture();
        fixture.Write("data/m1/models/m1.gltf", "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":");
        var report = await SourceBuilder.CheckAsync(fixture.Project, ["m1/gamez.zbd", "m2/gamez.zbd"], token: Token);
        var failed = Assert.Single(report.Outputs, o => o.Status == "failed");
        Assert.Equal("m1/gamez.zbd", failed.Path);
        Assert.Contains("data/m1/models/m1.gltf", failed.Error);
        Assert.Equal("built", report.Outputs.Single(o => o.Path == "m2/gamez.zbd").Status);
    }
}
