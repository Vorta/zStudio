using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Reconstruction infers a mission database's files from the shipped slots: worlds built from small projects, read back
/// as shipped files, reconstructed and built again keep every node in its slot.
/// </summary>
public sealed class ReconstructionInferenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public Dictionary<string, byte[]> Files { get; } = files;
        public bool Exists(string relative) => Files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Files[relative];
    }

    private static string Script(int mission, string loads, string before = "") => $"""
        set worldName world
        SetModelDirectory ..\data\m{mission}\models
        SetTextureDirectory ..\data\m{mission}\textures
        {before}
        NewWorld %worldName%
        FindNode %worldName%
        GameGenSetWorld %worldName%
        FindNode %worldName%
        WorldOrigin 0.0 512.0
        WorldExtents 512.0 -512.0
        WorldPartition 256 -256
        LoadGameGen m{mission}.flt m{mission}.flt
        DeleteTree m{mission}.flt
        {loads}
        GameZWriteZBDFile ..\m{mission}\gamez.zbd
        """;

    private static readonly WorldMaterial Paint = new() { Color = new(1, 2, 3), Flags = 0xFF };
    private static WorldNode Node(string name, WorldModel? model = null, params WorldNode[] children)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Model = model, Flags = WorldGltf.DefaultCarried };
        node.SetPayloadInt(0, 0x28);
        foreach (var child in children) { node.Children.Add(child); child.Parents.Add(node); }
        return node;
    }
    private static WorldModel Quad(float size, float y)
    {
        ModelBuilder builder = new();
        builder.Add(new([new(0, y, 0), new(size, y, 0), new(size, y, -size), new(0, y, -size)], [], [], [], Paint));
        return builder.Finish();
    }

    /// <summary>Writes <paramref name="roots"/> as <c>folder/stem.gltf</c>: <paramref name="references"/> name files, <paramref name="groups"/> are database groups.</summary>
    private static void Gltf(Dictionary<string, byte[]> files, string folder, string stem, IReadOnlyList<WorldNode> roots,
        IReadOnlyDictionary<WorldNode, string>? references = null, IReadOnlySet<WorldNode>? groups = null)
    {
        var (json, bin) = WorldGltf.Export(roots, 0xFF, new()
        {
            Texture = t => ($"../textures/{t.Name}.png", 0),
            Reference = n => references?.GetValueOrDefault(n), Content = n => references?.ContainsKey(n) == true ? [.. n.Children] : null,
            Group = n => groups?.Contains(n) == true,
        }).Write(stem + ".bin");
        files[$"{folder}/{stem}.gltf"] = json; files[$"{folder}/{stem}.bin"] = bin;
    }

    private static Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> Scripts(Dictionary<string, byte[]> files) =>
        files.Where(f => f.Key.StartsWith("gamegen/", StringComparison.Ordinal)).ToDictionary(f => f.Key["gamegen/".Length..].Replace('/', '\\'),
            f => GameGenScriptText.Tokenize(Encoding.ASCII.GetString(f.Value)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the missions, reads the worlds back as shipped files and reconstructs their sources.</summary>
    private static (List<WorldSources.Output> Outputs, List<string> Notes, Func<int, GameZWorld> Shipped) Reconstruct(Dictionary<string, byte[]> files, params int[] missions)
    {
        MemoryFiles project = new(files);
        Dictionary<int, byte[]> shipped = missions.ToDictionary(m => m, m => GameZWriter.Write(new WorldAssembler(project, Token).Assemble($"m{m}.gs"), Token));
        GameZWorld Shipped(int m) => GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", shipped[m], token: Token), Token);
        var scripts = Scripts(files);
        List<string> notes = [];
        var outputs = WorldSources.Reconstruct([.. missions.Select(m => new WorldSources.MissionWorld(m, Shipped(m)))], n => scripts.GetValueOrDefault(n),
            (_, _) => null, new HashSet<string>(), _ => 0, notes, Token);
        return (outputs, notes, Shipped);
    }

    /// <summary>Builds a mission from reconstructed sources (with the original scripts).</summary>
    private static GameZWorld Rebuild(Dictionary<string, byte[]> files, List<WorldSources.Output> outputs, int mission)
    {
        MemoryFiles rebuilt = new(files.Where(f => f.Key.StartsWith("gamegen/", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal));
        foreach (var output in outputs) rebuilt.Files[output.Path] = output.Bytes;
        var world = new WorldAssembler(rebuilt, Token).Assemble($"m{mission}.gs");
        return GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(world, Token), token: Token), Token);
    }

    /// <summary>Each slot's class and name, freed slots by their kept name.</summary>
    private static List<string> Slots(GameZWorld world)
    {
        var slots = GameZWriter.NodeSlots(world).ToDictionary(p => p.Value, p => p.Key);
        int count = Math.Max(slots.Count == 0 ? 0 : slots.Keys.Max() + 1, world.FreedSlots.Count == 0 ? 0 : world.FreedSlots.Keys.Max() + 1);
        return [.. Enumerable.Range(0, count).Select(s => slots.TryGetValue(s, out var n) ? $"{s}:{n.Class}:{n.Name}" : world.FreedSlots.ContainsKey(s) ? $"{s}:freed" : $"{s}:-")];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiesOfOnePartAreOneFileWhateverTheirGroupsKeptOfTheirNames(bool edited)
    {
        // A database referencing one part twice; the part's group holds a crate and a post. A load after the deletion takes
        // the slots of the second copy's group (its name is lost), while the first copy's group keeps its name. When
        // edited, the script then marks the newest crate (the second copy's) a landmark, so the copies differ too.
        string loads = "LoadGameGen lamp.flt lamp1" + (edited ? "\nFindNode crate\nSetLandmark on" : "");
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes(Script(1, loads)) };
        var group = Node("g", null, Node("crate", Quad(4, 1)), Node("post", Quad(1, 0)));
        Gltf(files, "data/m1/models", "m1_01", [group], groups: new HashSet<WorldNode>(ReferenceEqualityComparer.Instance) { group });
        WorldNode Reference() => Node("part.flt", null, Node("placeholder"));
        WorldNode first = Reference(), second = Reference();
        Gltf(files, "data/m1/models", "m1", [Node("ground", Quad(64, 0)), first, second],
            new Dictionary<WorldNode, string>(ReferenceEqualityComparer.Instance) { [first] = "m1_01.gltf", [second] = "m1_01.gltf" },
            new HashSet<WorldNode>(ReferenceEqualityComparer.Instance) { first, second });
        Gltf(files, "data/m1/models", "lamp", [Node("base", Quad(1, 0)), Node("bulb", Quad(1, 2))]);

        var (outputs, notes, shipped) = Reconstruct(files, 1);
        Assert.Empty(notes);
        // The inference cached both copies as one part, so they reference one file and the build caches it once.
        string[] parts = [.. outputs.Select(o => o.Path).Where(p => p.StartsWith("data/m1/models/m1_", StringComparison.Ordinal) && p.EndsWith(".gltf", StringComparison.Ordinal))];
        Assert.Equal(["data/m1/models/m1_01.gltf"], parts);
        var rebuilt = Rebuild(files, outputs, 1);
        Assert.Equal(Slots(shipped(1)), Slots(rebuilt));
        Assert.Empty(WorldComparer.Compare(shipped(1), rebuilt));
    }

    [Fact]
    public void ASecondPathIsKeptToTheFileWhoseSlotsShowedIt()
    {
        // m1's database references x.flt, which names r.flt twice, the second time by another path (so its load caches r
        // twice). m2's database references another x.flt, which names r.flt twice by one path, and so does the x.flt m1's
        // script loads from data/common/models before its database.
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        void X(string folder, int version, bool secondPath)
        {
            Gltf(files, folder, "r", [Node("rod", Quad(1, version))]);
            WorldNode r1 = Node("r.flt", null, Node("placeholder")), r2 = Node("r.flt", null, Node("placeholder"));
            Gltf(files, folder, "x", [Node("xbase", Quad(2, version), r1, r2)],
                new Dictionary<WorldNode, string>(ReferenceEqualityComparer.Instance) { [r1] = "r.gltf", [r2] = secondPath ? "./r.gltf" : "r.gltf" });
        }
        X("data/common/models", 3, false);
        for (int m = 1; m <= 2; m++)
        {
            files[$"gamegen/m{m}.gs"] = Encoding.ASCII.GetBytes(Script(m, "LoadGameGen lamp.flt lamp1", m == 1 ? "SetModelDirectory ..\\data\\common\\models\nLoadGameGen x.flt early" : ""));
            X($"data/m{m}/models", m, m == 1);
            WorldNode x = Node("x.flt", null, Node("placeholder"));
            Gltf(files, $"data/m{m}/models", $"m{m}", [Node("ground", Quad(64, 0)), Node("hut", Quad(3, 0), x), Node("tree", Quad(1, 5))],
                new Dictionary<WorldNode, string>(ReferenceEqualityComparer.Instance) { [x] = "x.gltf" });
            Gltf(files, $"data/m{m}/models", "lamp", [Node("base", Quad(1, 0)), Node("bulb", Quad(1, 2))]);
        }
        var (outputs, notes, shipped) = Reconstruct(files, 1, 2);
        Assert.Empty(notes);
        string Text(string path) => Encoding.UTF8.GetString(outputs.Single(o => o.Path == path).Bytes);
        Assert.Contains("./r.gltf", Text("data/m1/models/x.gltf"));
        Assert.DoesNotContain("./r.gltf", Text("data/m2/models/x.gltf"));
        Assert.DoesNotContain("./r.gltf", Text("data/common/models/x.gltf"));
        for (int m = 1; m <= 2; m++) Assert.Equal(Slots(shipped(m)), Slots(Rebuild(files, outputs, m)));
    }

    [Fact]
    public void LaterReferencesCopyingASecondPathsCacheNameItToo()
    {
        // After m1's database, f.flt names r.flt three times: the second and third time by another path, so its load caches
        // r twice and the third reference copies the second cache (and shares its models). The slots show only that the
        // second reference made a cache; the models show the third copied it.
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes(Script(1, "LoadGameGen f.flt f1\nLoadGameGen lamp.flt lamp1")) };
        Gltf(files, "data/m1/models", "r", [Node("rod", Quad(1, 0))]);
        WorldNode r1 = Node("r.flt", null, Node("placeholder")), r2 = Node("r.flt", null, Node("placeholder")), r3 = Node("r.flt", null, Node("placeholder"));
        Gltf(files, "data/m1/models", "f", [Node("frame", Quad(2, 0), r1, r2, r3)],
            new Dictionary<WorldNode, string>(ReferenceEqualityComparer.Instance) { [r1] = "r.gltf", [r2] = "./r.gltf", [r3] = "./r.gltf" });
        HashSet<WorldNode> groups = new(ReferenceEqualityComparer.Instance);
        WorldNode Group(int i) { var g = Node($"g{i}", null, Node($"o{i}", Quad(1, i))); groups.Add(g); return g; }
        Gltf(files, "data/m1/models", "m1", [Node("ground", Quad(64, 0)), .. Enumerable.Range(1, 12).Select(Group)], groups: groups);
        Gltf(files, "data/m1/models", "lamp", [Node("base", Quad(1, 0)), Node("bulb", Quad(1, 2)), Node("glass", Quad(1, 3)), Node("cap", Quad(1, 4))]);

        var (outputs, notes, shipped) = Reconstruct(files, 1);
        Assert.Empty(notes);
        string f = Encoding.UTF8.GetString(outputs.Single(o => o.Path == "data/m1/models/f.gltf").Bytes);
        Assert.Equal(2, f.Split("./r.gltf").Length - 1);
        var rebuilt = Rebuild(files, outputs, 1);
        Assert.Equal(Slots(shipped(1)), Slots(rebuilt));
        Assert.Equal(ModelUsers(shipped(1)), ModelUsers(rebuilt));
    }

    /// <summary>The slots of the nodes sharing each model.</summary>
    private static List<string> ModelUsers(GameZWorld world)
    {
        var slots = GameZWriter.NodeSlots(world);
        return [.. world.Nodes.Where(n => n.Model != null).GroupBy(n => n.Model!, ReferenceEqualityComparer.Instance).Select(g => string.Join(",", g.Select(n => slots[n]).Order())).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void AReferencesCopyNeverShadowsTheFileALoadFinds()
    {
        // Both missions' vehicle scripts load the same tank from their own vehicle folders, which reconstruction keeps as
        // one file in data/common/models. m1's jeep, which stays in m1's vehicle folder, references another file named
        // tank (its turret): the copy written beside the jeep must not be the tank m1's vehicle script finds first.
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        for (int m = 1; m <= 2; m++)
        {
            string vehicles = m == 1 ? "LoadGameGen tank.flt tank\nLoadGameGen jeep.flt jeep" : "LoadGameGen tank.flt tank";
            files[$"gamegen/m{m}.gs"] = Encoding.ASCII.GetBytes(Script(m, $"SetModelDirectory ..\\data\\common\\models\nSetModelDirectory ..\\data\\m{m}\\models\\bft\n{vehicles}"));
            Gltf(files, $"data/m{m}/models", $"m{m}", [Node("ground", Quad(64, 0))]);
            Gltf(files, $"data/m{m}/models/bft", "tank", [Node("hull", Quad(4, 1))]);
        }
        Gltf(files, "data/m1/models/bft/turret", "tank", [Node("gun", Quad(1, 3))]);
        WorldNode turret = Node("tank.flt", null, Node("placeholder"));
        Gltf(files, "data/m1/models/bft", "jeep", [Node("chassis", Quad(3, 1), turret)], new Dictionary<WorldNode, string>(ReferenceEqualityComparer.Instance) { [turret] = "turret/tank.gltf" });

        var (outputs, notes, shipped) = Reconstruct(files, 1, 2);
        Assert.Empty(notes);
        Assert.Contains(outputs, o => o.Path == "data/common/models/tank.gltf");
        Assert.DoesNotContain(outputs, o => o.Path == "data/m1/models/bft/tank.gltf");
        for (int m = 1; m <= 2; m++) Assert.Empty(WorldComparer.Compare(shipped(m), Rebuild(files, outputs, m)));
    }

    private static List<string> Load(IReadOnlyList<WorldNode> records, Dictionary<WorldNode, IReadOnlyList<WorldNode>> content, IReadOnlySet<WorldNode>? references = null)
    {
        List<string> events = [];
        OriginalLoader.Load(Node("root"), records, records, new()
        {
            Allocate = n => events.Add("+" + n.Name), Free = n => events.Add("-" + n.Name),
            Content = n => content.GetValueOrDefault(n) ?? [], File = n => n.Name,
            IsReference = references == null ? null : references.Contains, Token = Token,
        });
        return events;
    }

    [Fact]
    public void AReferenceToAFileWithoutNodesIsCachedAndCopiedLikeAnyOther()
    {
        // The loader caches the file (a root alone) and its empty copy waits for the next record; as the last record it
        // needs an end node too.
        var empty = Node("empty.flt"); var next = Node("next");
        HashSet<WorldNode> references = new(ReferenceEqualityComparer.Instance) { empty };
        Assert.Equal(["+empty.flt", "+root", "+empty.flt", "+next", "-empty.flt"], Load([empty, next], new(ReferenceEqualityComparer.Instance), references));
        Assert.Equal(["+empty.flt", "+root", "+next", "+empty.flt", "+", "-", "-empty.flt"], Load([next, empty], new(ReferenceEqualityComparer.Instance), references));

        // The build: m1's crate references a file without nodes, whose cache takes slot 1 before the database's root.
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes(Script(1, "LoadGameGen lamp.flt lamp1")) };
        files["data/m1/models/empty.gltf"] = Encoding.ASCII.GetBytes("""{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[]}]}""");
        var crate = Node("crate", Quad(2, 1));
        Gltf(files, "data/m1/models", "m1", [Node("ground", Quad(64, 0)), crate, Node("tree", Quad(1, 5))], new Dictionary<WorldNode, string>(ReferenceEqualityComparer.Instance) { [crate] = "empty.gltf" });
        Gltf(files, "data/m1/models", "lamp", [Node("base", Quad(1, 0)), Node("bulb", Quad(1, 2))]);
        var world = new WorldAssembler(new MemoryFiles(files), Token).Assemble("m1.gs");
        Assert.Equal(["0:World:world", "1:Object3D:base", "2:Object3D:lamp1", "3:Object3D:ground", "4:Object3D:crate", "5:Object3D:tree", "6:Object3D:bulb"], Slots(world));
    }

    [Fact]
    public void OnlyAlikeUnnamedSubtreesAreOneInstanceDefinition()
    {
        // A copy expands an instance along every edge; unnamed subtrees alike in names, models and transforms are one
        // definition of the cache again, unnamed subtrees with other models or transforms are not.
        WorldModel a = Quad(1, 0), b = Quad(2, 0);
        WorldNode Placement(string name, WorldModel model, float x)
        {
            var unnamed = Node("", model); unnamed.SetPayloadInt(0, 0x30);
            float[] rows = [1, 0, 0, 0, 1, 0, 0, 0, 1, x, 0, 0];
            for (int i = 0; i < 12; i++) unnamed.SetPayloadFloat(0x30 + i * 4, rows[i]);
            return Node(name, null, unnamed);
        }
        List<string> Cache(params WorldNode[] content)
        {
            var reference = Node("ring.flt", null, content);
            var events = Load([reference], new(ReferenceEqualityComparer.Instance) { [reference] = content });
            return events[..events.IndexOf("+root")];
        }
        Assert.Equal(["+", "+ring.flt", "+s1", "+s2"], Cache(Placement("s1", a, 3), Placement("s2", a, 3)));
        Assert.Equal(["+ring.flt", "+s1", "+", "+s2", "+"], Cache(Placement("s1", a, 3), Placement("s2", b, 3)));
        Assert.Equal(["+ring.flt", "+s1", "+", "+s2", "+"], Cache(Placement("s1", a, 3), Placement("s2", a, 4)));
    }

    /// <summary>
    /// A shipped world whose database had <paramref name="groups"/> groups, each holding one object, and whose free list
    /// the deletion cannot have left (shuffled): the inference searches every reading before it gives up.
    /// </summary>
    private static (GameZWorld World, WorldDecomposition Build) Unreadable(int groups)
    {
        GameZWorld world = new() { NodeCapacity = GameZWorld.MaximumNodeCapacity };
        WorldNode root = new("world1", WorldNodeClass.World); world.Nodes.Add(root);
        List<WorldNode> objects = [];
        for (int i = 0; i < groups; i++)
        {
            var o = Node($"o{i}", Quad(1, 0)); o.Parents.Add(root); root.Children.Add(o);
            world.Nodes.Add(o); objects.Add(o);
            world.FreedSlots[2 + 2 * i] = FreedSlot($"g{i}");
        }
        world.FreedSlots[1] = FreedSlot("m1.flt");
        List<int> chain = [.. world.FreedSlots.Keys.Order()];
        new Random(7).Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chain));
        for (int i = 0; i < chain.Count; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(world.FreedSlots[chain[i]].AsSpan(192), (uint)(i + 1 < chain.Count ? chain[i + 1] : 2 * groups + 2));
        world.FreeHead = chain[0];
        TracedInstruction Step(string command, params string[] args) => new("m1.gs", command, args, [], null, []);
        return (world, new([new("m1.flt", "m1.flt", Step("LoadGameGen", "m1.flt", "m1.flt"), true, null, objects, false) { Step = 1 }],
            new Dictionary<int, WorldNode> { [0] = root }));
        static byte[] FreedSlot(string name)
        {
            byte[] slot = new byte[196]; Encoding.ASCII.GetBytes(name).CopyTo(slot, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(slot.AsSpan(60), -1);
            return slot;
        }
    }

    [Fact]
    public async Task TheInferenceStopsSoonAfterItIsCanceled()
    {
        // A second in, the search for where the caches end (quadratic in the free list) is under way.
        var (world, build) = Unreadable(20_000);
        using CancellationTokenSource stop = new();
        long stopped = 0;
        var inference = Task.Run(() =>
        {
            try { return DatabaseRecords.Infer(world, build, _ => false, "m1", [], stop.Token); }
            finally { Volatile.Write(ref stopped, System.Diagnostics.Stopwatch.GetTimestamp()); }
        }, Token);
        await Task.Delay(1000, Token);
        // Canceled here and timed on the inference's own thread: a busy thread pool can delay a timer's cancellation, or
        // when this test resumes, but not how soon the inference sees the cancellation.
        long canceled = System.Diagnostics.Stopwatch.GetTimestamp();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inference.WaitAsync(TimeSpan.FromMinutes(2), Token));
        var latency = System.Diagnostics.Stopwatch.GetElapsedTime(canceled, Volatile.Read(ref stopped));
        Assert.True(latency < TimeSpan.FromSeconds(3), $"The inference stopped {latency.TotalMilliseconds:N0} ms after it was canceled.");
    }
}
