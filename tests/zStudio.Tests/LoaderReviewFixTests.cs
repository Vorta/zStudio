using System.Numerics;
using System.Text;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

/// <summary>
/// The zone probe's lights, the inference's references to files without nodes, its mirror budget and child order, the
/// copy a part's file is written from, and what a Blender checkout shows: which model is a pickup, and the zones of a
/// shared node's copies.
/// </summary>
public sealed class LoaderReviewFixTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------ the zone probe

    private const uint Surface = 0x1C;

    private static WorldNode ProbeNode(string name, WorldNodeClass kind = WorldNodeClass.Object3D, params (float X, float Y, float Z, float Size)[] quads)
    {
        WorldModel? model = quads.Length == 0 ? null : new();
        foreach (var (x, y, z, size) in quads)
        {
            int at = model!.Vertices.Count;
            model.Vertices.AddRange([new(x, y, z), new(x, y, z + size), new(x + size, y, z + size), new(x + size, y, z)]);
            model.Polygons.Add(new() { Material = new() { Flags = 0xFF }, Vertices = [at, at + 1, at + 2, at + 3], Zone = 0xFFFF0501 });
        }
        WorldNode node = new(name, kind) { Model = model, Flags = Surface };
        if (kind == WorldNodeClass.Object3D) node.SetPayloadInt(0, 0x28);
        return node;
    }
    private static void Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); }

    /// <summary>A 2 × 2-cell world (x 0..512, z 0..512) with <paramref name="cells"/> partitioned and <paramref name="listed"/> in its own list.</summary>
    private static WorldNode ProbeWorld(WorldNode[] cells, params WorldNode[] listed)
    {
        GameZWorld world = new();
        WorldNode root = new("world", WorldNodeClass.World); world.Nodes.Add(root);
        root.SetPayloadFloat(0x34, 0); root.SetPayloadFloat(0x38, 512); root.SetPayloadFloat(0x3C, 512); root.SetPayloadFloat(0x40, -512);
        root.SetPayloadFloat(0x44, 512); root.SetPayloadFloat(0x48, 0);
        WorldUpdate.SetPartition(root, 256, -256);
        HashSet<WorldNode> added = new(ReferenceEqualityComparer.Instance);
        void Add(WorldNode node)
        {
            if (!added.Add(node)) return;
            world.Nodes.Add(node); if (node.Model != null && !world.Models.Contains(node.Model)) world.Models.Add(node.Model);
            foreach (var child in node.Children) Add(child);
        }
        foreach (var node in cells.Concat(listed)) Add(node);
        WorldUpdate.RebuildBounds(world);
        WorldUpdate.Partition(root, cells);
        foreach (var node in listed) { root.Children.Add(node); node.Parents.Add(root); }
        return root;
    }

    [Fact]
    public void ALightPassesTheProbeOnUnderItsOwnOrientationNotItsWorldAngles()
    {
        // A pylon at (300, 0, 300) turned a quarter around y holds a lamp turned another quarter (its orientation, +0x08);
        // the lamp's world angles (+0x20) hold the half turn the update derives from both. The engine turns the lamp's
        // children by its own orientation under the pylon's transform (BuildPickCandidatesForLight 0x4443e0), so the deck,
        // x and z 0..200 under the lamp, lies at x and z 100..300. Turned by the world angles again under the pylon it would
        // lie at x 100..300 and z 300..500.
        var pylon = ProbeNode("pylon");
        var placed = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(300, 0, 300);
        float[] rows = [placed.M11, placed.M12, placed.M13, placed.M21, placed.M22, placed.M23, placed.M31, placed.M32, placed.M33, placed.M41, placed.M42, placed.M43];
        pylon.SetPayloadInt(0, 0x30);
        for (int i = 0; i < 12; i++) pylon.SetPayloadFloat(0x30 + i * 4, rows[i]);
        var lamp = ProbeNode("lamp", WorldNodeClass.Light);
        lamp.SetPayloadFloat(0x0C, MathF.PI / 2); lamp.SetPayloadFloat(0x24, MathF.PI);
        Link(pylon, lamp); Link(lamp, ProbeNode("deck", quads: (0, 7, 0, 200)));
        var world = ProbeWorld([ProbeNode("ground", quads: (0, 0, 0, 50))], pylon);
        // A cached box (in the pylon's own space) over the whole world lets the walk into the pylon, which has siblings in
        // the world's list.
        pylon.Flags |= ZoneProbe.BoundsFlag; pylon.CachedBounds = new(new(-1024, -1, -1024), new(1024, 1, 1024));
        foreach (var kind in new[] { ZoneProbeKind.Point, ZoneProbeKind.Vehicle })
        {
            Assert.Equal([7f], ZoneProbe.Probe(world, 150, 200, ZoneSet.Cleared, kind, token: TestContext.Current.CancellationToken).Hits.Select(h => h.Height));
            Assert.Empty(ZoneProbe.Probe(world, 150, 400, ZoneSet.Cleared, kind, token: TestContext.Current.CancellationToken).Hits);
        }
    }

    // ------------------------------------------------------------ the database inference

    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public Dictionary<string, byte[]> Files { get; } = files;
        public bool Exists(string relative) => Files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = Files[relative];
            // Original script fixtures remain FLT-based; the current source assembler receives reconstructed filenames.
            if (relative.StartsWith("gamegen/", StringComparison.Ordinal))
                result = Encoding.ASCII.GetBytes(GameGenScriptText.Write(GameGenScriptText.ProjectFileNames(GameGenScriptText.Tokenize(Encoding.ASCII.GetString(result)), new HashSet<string>())));
            limits.Validate(result); return result; }
    }

    private static byte[] Script(int mission, string loads, string before = "") => Encoding.ASCII.GetBytes($"""
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
        """);

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
    private static Dictionary<WorldNode, string> Refs(params (WorldNode Node, string Uri)[] references)
    {
        Dictionary<WorldNode, string> map = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, uri) in references) map[node] = uri;
        return map;
    }
    private static HashSet<WorldNode> Set(params WorldNode[] nodes) => new(nodes, ReferenceEqualityComparer.Instance);

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

    /// <summary>A mission built and read back as a shipped file.</summary>
    private static GameZWorld Shipped(Dictionary<string, byte[]> files, int mission) =>
        GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(new WorldAssembler(new MemoryFiles(files), Token).Assemble($"m{mission}.gs"), Token), token: Token), Token);

    /// <summary>Builds the missions, reads the worlds back as shipped files and reconstructs their sources.</summary>
    private static (List<WorldSources.Output> Outputs, List<string> Notes, Func<int, GameZWorld> Shipped) Reconstruct(Dictionary<string, byte[]> files, params int[] missions)
    {
        var scripts = Scripts(files);
        List<string> notes = [];
        var outputs = WorldSources.Reconstruct([.. missions.Select(m => new WorldSources.MissionWorld(m, Shipped(files, m)))], n => scripts.GetValueOrDefault(n),
            (_, _) => null, new HashSet<string>(), _ => 0, notes, Token);
        return (outputs, notes, m => Shipped(files, m));
    }

    /// <summary>Builds a mission from reconstructed sources (with the original scripts).</summary>
    private static GameZWorld Rebuild(Dictionary<string, byte[]> files, List<WorldSources.Output> outputs, int mission)
    {
        Dictionary<string, byte[]> rebuilt = files.Where(f => f.Key.StartsWith("gamegen/", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        foreach (var output in outputs) rebuilt[output.Path] = output.Bytes;
        return Shipped(rebuilt, mission);
    }

    /// <summary>Each slot's class and name, freed slots by their kept name.</summary>
    private static List<string> Slots(GameZWorld world)
    {
        var slots = GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken).ToDictionary(p => p.Value, p => p.Key);
        int count = Math.Max(slots.Count == 0 ? 0 : slots.Keys.Max() + 1, world.FreedSlots.Count == 0 ? 0 : world.FreedSlots.Keys.Max() + 1);
        return [.. Enumerable.Range(0, count).Select(s => slots.TryGetValue(s, out var n) ? $"{s}:{n.Class}:{n.Name}" : world.FreedSlots.ContainsKey(s) ? $"{s}:freed" : $"{s}:-")];
    }

    [Fact]
    public void AReferenceToAFileWithoutNodesIsReadAsTheBuildReadsIt()
    {
        // m1 references empty.gltf, a file without nodes: in a load before its database (as that file's last record), in the
        // database (before two copies of a part, which only the parts model reads), and in a load after it. The build caches
        // the file every time (a root alone) and copies nothing after the next record; the inference replays it so and the
        // reconstruction writes it as a reference. (A last load shows the slots its caches freed only when something takes
        // them again: the post does.)
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = Script(1, "LoadGameGen lamp.flt lamp1\nLoadGameGen post.flt post1", "LoadGameGen early.flt early") };
        Gltf(files, "data/m1/models", "post", [Node("stake", Quad(1, 3))]);
        files["data/m1/models/empty.gltf"] = Encoding.ASCII.GetBytes("""{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[]}]}""");
        WorldNode early = Node("empty.flt"), inDatabase = Node("empty.flt"), later = Node("empty.flt");
        Gltf(files, "data/m1/models", "early", [Node("pole", Quad(1, 0)), early], Refs((early, "empty.gltf")));
        // The part references it too (and a rod), so the part's own load caches it.
        Gltf(files, "data/m1/models", "r", [Node("rod", Quad(1, 7))]);
        WorldNode inPart = Node("empty.flt"), rod = Node("r.flt", null, Node("placeholder"));
        var group = Node("g", null, Node("crate", Quad(4, 1)), inPart, rod, Node("post", Quad(1, 0)));
        Gltf(files, "data/m1/models", "m1_01", [group], Refs((inPart, "empty.gltf"), (rod, "r.gltf")), Set(group));
        WorldNode first = Node("part.flt", null, Node("placeholder")), second = Node("part.flt", null, Node("placeholder"));
        Gltf(files, "data/m1/models", "m1", [Node("ground", Quad(64, 0)), inDatabase, first, second], Refs((inDatabase, "empty.gltf"), (first, "m1_01.gltf"), (second, "m1_01.gltf")), Set(first, second));
        Gltf(files, "data/m1/models", "lamp", [Node("base", Quad(1, 0)), later, Node("bulb", Quad(1, 2))], Refs((later, "empty.gltf")));

        var (outputs, notes, shipped) = Reconstruct(files, 1);
        Assert.All(notes, n => Assert.Contains("inferred load sections are unrecoverable", n));
        // Every reference names the file again, so the rebuilt world caches it where the shipped one did.
        Assert.Contains(outputs, o => o.Path == "data/m1/models/empty.gltf");
        foreach (string file in new[] { "early", "m1", "lamp" })
            Assert.Contains(System.Text.Json.Nodes.JsonNode.Parse(outputs.Single(o => o.Path == $"data/m1/models/{file}.gltf").Bytes)!["nodes"]!.AsArray(),
                n => (string?)n?["extras"]?["recoil"]?["ref"] == "empty.gltf");
        Assert.Equal(Slots(shipped(1)), Slots(Rebuild(files, outputs, 1)));
    }

    [Fact]
    public void AnInlinePartUsesItsFirstCopyAndLaterScriptEditsMakeNoFiles()
    {
        // m1's database copies one part twice; the part references r.flt (a rod). After the load the script marks the
        // newest rod, the second copy's, a landmark (FindNode finds the newest node of a name), so the copies' r.flt
        // references differ. The part's file is written from the first copy, which no script changed, and only its
        // reference is a file: the second copy's used to become an r.gltf of its own that no file uses, with a note.
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = Script(1, "LoadGameGen lamp.flt lamp1\nFindNode rod\nSetLandmark on") };
        Gltf(files, "data/m1/models", "r", [Node("rod", Quad(1, 7))]);
        var rod = Node("r.flt", null, Node("placeholder"));
        var group = Node("g", null, Node("crate", Quad(4, 1)), rod, Node("post", Quad(1, 0)));
        Gltf(files, "data/m1/models", "m1_01", [group], Refs((rod, "r.gltf")), Set(group));
        WorldNode first = Node("part.flt", null, Node("placeholder")), second = Node("part.flt", null, Node("placeholder"));
        Gltf(files, "data/m1/models", "m1", [Node("ground", Quad(64, 0)), first, second], Refs((first, "m1_01.gltf"), (second, "m1_01.gltf")), Set(first, second));
        Gltf(files, "data/m1/models", "lamp", [Node("base", Quad(1, 0)), Node("bulb", Quad(1, 2))]);

        var (outputs, notes, shipped) = Reconstruct(files, 1);
        Assert.All(notes, n => Assert.Contains("inferred load sections are unrecoverable", n));
        Assert.Equal(["data/m1/models/lamp.gltf", "data/m1/models/m1.gltf", "data/m1/models/r.gltf"],
            outputs.Select(o => o.Path).Where(p => p.EndsWith(".gltf", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        // The rod's file is the unchanged one; the build gives every copy that rod, and the script marks the second again.
        var rodNode = System.Text.Json.Nodes.JsonNode.Parse(outputs.Single(o => o.Path == "data/m1/models/r.gltf").Bytes)!["nodes"]!.AsArray().Single(n => (string?)n!["name"] == "rod")!;
        Assert.Equal(0x01000018u, WorldGltf.StoredNodeFlags(rodNode["extras"]![WorldGltf.Key]!.AsObject()));
        Assert.Null(rodNode["extras"]![WorldGltf.Key]!["flags"]);
        var rebuilt = Rebuild(files, outputs, 1);
        Assert.Equal(Slots(shipped(1)), Slots(rebuilt));
        Assert.Empty(WorldComparer.Compare(shipped(1), rebuilt));
    }

    /// <summary>m1's database: ground, a part of <paramref name="objects"/> objects, and a hut with a door and a window; a lamp loads after it.</summary>
    private static Dictionary<string, byte[]> HutProject(int objects)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = Script(1, "LoadGameGen lamp.flt lamp1") };
        var group = Node("g", null, [.. Enumerable.Range(0, objects).Select(i => Node($"crate{i}", Quad(1, i)))]);
        Gltf(files, "data/m1/models", "m1_01", [group], groups: Set(group));
        var part = Node("part.flt", null, Node("placeholder"));
        var hut = Node("hut", Quad(3, 0), Node("door", Quad(1, 1)), Node("window", Quad(1, 2)));
        Gltf(files, "data/m1/models", "m1", [Node("ground", Quad(64, 0)), part, hut], Refs((part, "m1_01.gltf")), Set(part));
        Gltf(files, "data/m1/models", "lamp", [Node("base", Quad(1, 0)), Node("bulb", Quad(1, 2))]);
        return files;
    }

    /// <summary>The shipped world with the hut's children listed last first (as a script detaching and attaching them would), and its decomposition.</summary>
    private static (GameZWorld World, WorldDecomposition Build, Func<WorldNode, bool> IsReference, WorldNode Hut) Crafted(Dictionary<string, byte[]> files)
    {
        var world = Shipped(files, 1);
        var hut = world.Nodes.Single(n => n.Name == "hut");
        hut.Children.Reverse();
        List<string> notes = [];
        var scripts = Scripts(files);
        var build = WorldDecomposer.DecomposeAll(world, ScriptTrace.Trace(n => scripts.GetValueOrDefault(n), "m1.gs", notes), notes);
        HashSet<WorldNode> roots = new(build.Loads.Where(l => l.Root != null).Select(l => l.Root!), ReferenceEqualityComparer.Instance);
        return (world, build, n => WorldSources.IsReference(n) && !roots.Contains(n), hut);
    }

    [Fact]
    public void CancellationDuringInferenceRestoresTheWorldsChildOrder()
    {
        var (world, build, isReference, hut) = Crafted(HutProject(objects: 40));
        Assert.Equal(["window", "door"], hut.Children.Select(c => c.Name));
        var children = world.Nodes.Select(n => (Node: n, Children: n.Children.ToArray())).ToArray();
        using CancellationTokenSource stop = new();
        bool canceledAfterReordering = false;
        bool IsReference(WorldNode node)
        {
            // The inference has reordered actual source children before replaying its candidate. Cancel from its
            // existing classification callback so neither a timer nor a worker's scheduling chooses the interleaving.
            if (!canceledAfterReordering && hut.Children[0].Name == "door")
            {
                canceledAfterReordering = true;
                stop.Cancel();
            }
            return isReference(node);
        }
        List<string> notes = [];
        var error = Assert.ThrowsAny<OperationCanceledException>(() => DatabaseRecords.Infer(world, build, IsReference, "m1", notes, stop.Token));
        Assert.True(canceledAfterReordering);
        Assert.Equal(stop.Token, error.CancellationToken);
        foreach (var (node, original) in children) Assert.Equal(original, node.Children);
        Assert.Empty(notes);

        // A canceled candidate must not poison a subsequent inference over the same world.
        Assert.NotNull(DatabaseRecords.Infer(world, build, isReference, "m1", notes, Token));
        Assert.Equal(["door", "window"], hut.Children.Select(c => c.Name));
        Assert.Empty(notes);
    }

    [Fact]
    public void TheMirrorBudgetStopsTheWholeInferenceAndLeavesTheWorldsChildOrder()
    {
        var files = HutProject(objects: 40);
        // The reading that fits caches the part once to match its frees and once more when it replays the database: two
        // loads mirroring 41 nodes each (the part's group and its crates). It also lists the hut's children in the order they
        // were made.
        var (world, build, isReference, hut) = Crafted(files);
        List<string> notes = [];
        Assert.NotNull(DatabaseRecords.Infer(world, build, isReference, "m1", notes, Token));
        Assert.Empty(notes);
        Assert.Equal(["door", "window"], hut.Children.Select(c => c.Name));
        // With 60 for the whole inference, that reading stops it at once (each load alone stays within the budget), and the
        // hut keeps the order the world lists, which the fallback (the world's object order) writes.
        (world, build, isReference, hut) = Crafted(files);
        var error = Assert.Throws<InvalidDataException>(() => DatabaseRecords.Infer(world, build, isReference, "m1", notes, Token, mirrorLimit: 60));
        Assert.Contains("more than 60 nodes in all", error.Message, StringComparison.Ordinal);
        Assert.Equal(["window", "door"], hut.Children.Select(c => c.Name));
        Assert.Empty(notes);
    }

    [Fact]
    public void ReferenceMatchingBudgetStopsInferenceAndRestoresChildOrder()
    {
        var files = HutProject(objects: 4);
        files["data/m1/models/empty.gltf"] = Encoding.ASCII.GetBytes("""{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[]}]}""");
        // A part with repeated empty references needs a second cache despite having no model identities to distinguish
        // it. The inference must rank these references, not skip them as though empty files were not references.
        WorldNode[] empty = [.. Enumerable.Range(0, 8).Select(_ => Node("empty.flt"))];
        var group = Node("g", null, empty);
        var references = empty.Select((node, i) => (node, uri: i == empty.Length - 1 ? "./empty.gltf" : "empty.gltf"))
            .ToDictionary(p => p.node, p => p.uri);
        Gltf(files, "data/m1/models", "m1_01", [group], references, Set(group));
        var (world, build, isReference, hut) = Crafted(files);
        var children = world.Nodes.Select(n => (Node: n, Children: n.Children.ToArray())).ToArray();
        List<string> notes = [];
        var error = Assert.Throws<InvalidDataException>(() => DatabaseRecords.Infer(world, build, isReference,
            "m1", notes, Token, matchingLimit: 1));
        Assert.Contains("work limit", error.Message);
        foreach (var (node, original) in children) Assert.Equal(original, node.Children);
        Assert.Equal(["window", "door"], hut.Children.Select(c => c.Name));
        Assert.Empty(notes);
        Assert.NotNull(DatabaseRecords.Infer(world, build, isReference, "m1", notes, Token));
    }
}
