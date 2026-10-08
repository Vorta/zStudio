using System.Collections;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>The original loader's emulation and the world writer stay within their inputs' size on crafted files.</summary>
public sealed class ReconstructionBoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WorldNode Node(string name, params WorldNode[] children)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D);
        foreach (var child in children) { node.Children.Add(child); child.Parents.Add(node); }
        return node;
    }

    /// <summary>A reference's content that counts how often its nodes are visited.</summary>
    private sealed class Counted(IReadOnlyList<WorldNode> nodes) : IReadOnlyList<WorldNode>
    {
        public long Visits;
        public WorldNode this[int index] { get { Visits++; return nodes[index]; } }
        public int Count => nodes.Count;
        public IEnumerator<WorldNode> GetEnumerator() { foreach (var node in nodes) { Visits++; yield return node; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static OriginalLoader.Hooks Hooks(Func<WorldNode, IReadOnlyList<WorldNode>> content, Action<WorldNode>? allocate = null, CancellationToken? token = null) => new()
    {
        Allocate = allocate ?? (_ => { }), Free = _ => { }, Content = content, File = n => n.Name, Token = token ?? Token,
    };

    [Fact]
    public async Task NestedSharedInstancesAreMirroredOnceEach()
    {
        // A referenced file whose two unnamed instances at each level both hold the two of the level below, 64 levels
        // deep: 130 nodes on 2^64 paths.
        WorldNode a = Node("", Node("leaf")), b = Node("", Node("leaf"));
        for (int i = 0; i < 64; i++) (a, b) = (Node("", a, b), Node("", a, b));
        var reference = Node("deep.flt", a, b); var next = Node("next");
        int allocations = 0;
        var load = Task.Run(() => OriginalLoader.Load(Node("root"), [reference, next], [reference, next], Hooks(n => n == reference ? [a, b] : [], allocate: _ => allocations++)), Token);
        Assert.Same(load, await Task.WhenAny(load, Task.Delay(10_000, Token)));
        await load;
        // The cache (alike instances are one definition), the root, the two records and the copy, each node once.
        Assert.InRange(allocations, 130, 400);
    }

    [Fact]
    public void AWideReferenceCostsItsWidth()
    {
        // A reference whose file holds 3,000 records: membership in its content is a set, not a scan per child.
        WorldNode[] records = [.. Enumerable.Range(0, 3000).Select(i => Node($"n{i}"))];
        var reference = Node("wide.flt", records);
        Counted content = new(records);
        OriginalLoader.Load(Node("root"), [reference, Node("next")], [reference, Node("next")], Hooks(n => n == reference ? content : []));
        Assert.InRange(content.Visits, records.Length, 20L * records.Length);
    }

    [Fact]
    public void CanceledLoadsStopEvenWhenTheirHooksDoNotAsk()
    {
        var reference = Node("a.flt", Node("x"));
        using CancellationTokenSource stopped = new(); stopped.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            OriginalLoader.Load(Node("root"), [reference], [reference], Hooks(n => n == reference ? [.. n.Children] : [], token: stopped.Token)));
        Assert.Throws<OperationCanceledException>(() =>
            OriginalLoader.Mirror("a.flt", [.. reference.Children], new(ReferenceEqualityComparer.Instance), _ => [], stopped.Token));
    }

    [Fact]
    public void CachesNestedDeepAndWideAreRefused()
    {
        // Each file references the next and holds 1,000 objects, 50 levels deep: every level's cache mirrors the copies of
        // all the files below it, 1.3 million nodes in all.
        WorldNode? inner = null;
        Dictionary<WorldNode, IReadOnlyList<WorldNode>> contents = new(ReferenceEqualityComparer.Instance);
        for (int level = 0; level < 50; level++)
        {
            List<WorldNode> records = [.. Enumerable.Range(0, 1000).Select(i => Node($"o{level}_{i}"))];
            if (inner != null) records.Add(inner);
            var reference = Node($"f{level}.flt", [.. records]);
            contents[reference] = records;
            inner = reference;
        }
        var error = Assert.Throws<InvalidDataException>(() => OriginalLoader.Load(Node("root"), [inner!], [inner!], Hooks(n => contents.GetValueOrDefault(n) ?? [])));
        Assert.Contains("1,048,576", error.Message);
    }

    [Fact]
    public void AWorldWhoseCachesExceedTheBoundIsReconstructedInItsOrderWithANote()
    {
        // A load before m1's database holds files nested 50 levels deep, 1,000 objects each: replaying it would cache
        // more than a load may, so the mission's database is noted and kept in the world's order; reconstruction goes on.
        GameZWorld world = new() { NodeCapacity = GameZWorld.MaximumNodeCapacity };
        WorldNode root = new("world", WorldNodeClass.World); world.Nodes.Add(root);
        WorldNode big = Node("big1"); world.Nodes.Add(big);
        WorldNode? inner = null;
        for (int level = 49; level >= 0; level--)
        {
            List<WorldNode> records = [.. Enumerable.Range(0, 1000).Select(i => Node($"o{level}_{i}"))];
            if (inner != null) records.Add(inner);
            inner = Node($"f{level}.flt", [.. records]);
        }
        big.Children.Add(inner!); inner!.Parents.Add(big);
        world.Nodes.AddRange(WorldAssembler.Subtree(inner).Reverse());
        var ground = Node("ground"); ground.Parents.Add(root); root.Children.Add(ground); world.Nodes.Add(ground);
        var script = GameGenScriptText.Tokenize("""
            NewWorld world
            LoadGameGen big.flt big1
            FindNode world
            GameGenSetWorld world
            LoadGameGen m1.flt m1.flt
            DeleteTree m1.flt
            GameZWriteZBDFile ..\m1\gamez.zbd
            """);
        List<string> notes = [];
        WorldSources.Reconstruct([new(1, world)], n => n.Equals("m1.gs", StringComparison.OrdinalIgnoreCase) ? script : null, (_, _) => null, new HashSet<string>(), _ => 0, notes, Token);
        Assert.Contains(notes, n => n.StartsWith("m1: the mission database keeps the world's object order", StringComparison.Ordinal) && n.Contains("1,048,576", StringComparison.Ordinal));
    }

    [Fact]
    public void FreedSlotsAboveTheLastNodeAreListedInOnePass()
    {
        // A build whose last load was deleted again leaves its slots free above every live node.
        GameZWorld world = new() { NodeCapacity = GameZWorld.MaximumNodeCapacity };
        world.Nodes.Add(new("world1", WorldNodeClass.World));
        for (int s = 1; s < 65_000; s++) world.FreedSlots[s] = new byte[196];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(0, GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)[world.Nodes[0]]);
        // Milliseconds; finding the last freed slot again for every slot took 20 s.
        Assert.InRange(watch.ElapsedMilliseconds, 0, 3000);
    }

    [Fact]
    public void AFilesRecordsSharingOneInstanceAreWalkedOnce()
    {
        // 20,000 records each place one instance holding 20,000 objects: the build looks the load's name up among the
        // records' nodes once, not once per record.
        const int Records = 20_000, Objects = 20_000;
        StringBuilder nodes = new(); List<string> roots = [];
        void Add(string json) { if (nodes.Length > 0) nodes.Append(','); nodes.Append(json); }
        int count = 0;
        for (int i = 0; i < Records; i++)
        {
            int record = count++, instance = count++;
            roots.Add(record.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add($"{{\"name\":\"r{i}\",\"children\":[{instance}]}}");
            string children = i == 0 ? $",\"children\":[{string.Join(",", Enumerable.Range(2 * Records, Objects))}]" : "";
            Add($"{{\"name\":\"shared\",\"extras\":{{\"recoil\":{{\"instance\":1}}}}{children}}}");
        }
        for (int i = 0; i < Objects; i++) Add($"{{\"name\":\"o{i}\"}}");
        string gltf = $"{{\"asset\":{{\"version\":\"2.0\"}},\"scene\":0,\"scenes\":[{{\"nodes\":[{string.Join(",", roots)}]}}],\"nodes\":[{nodes}]}}";
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes("""
                SetModelDirectory ..\data\m1\models
                NewWorld world
                WorldOrigin 0.0 512.0
                WorldExtents 512.0 -512.0
                WorldPartition 256 -256
                LoadGameGen big.flt big
                GameZWriteZBDFile ..\m1\gamez.zbd
                """),
            ["data/m1/models/big.gltf"] = Encoding.UTF8.GetBytes(gltf),
        };
        var assembler = new WorldAssembler(new MemoryFiles(files), Token);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var world = assembler.Assemble("m1.gs");
        Assert.Equal(1 + 1 + Records + 1 + Objects, world.Nodes.Count);
        // About 1.5 s alone; walking the instance once per record took about 50 s.
        Assert.InRange(watch.ElapsedMilliseconds, 0, 30_000);
    }

    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public bool Exists(string relative) => files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = files[relative]; limits.Validate(result); return result; }
    }
}
