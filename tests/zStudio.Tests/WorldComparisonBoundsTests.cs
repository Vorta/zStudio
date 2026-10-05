using System.Numerics;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Comparing worlds stays within bounded work and memory on worlds far from retail sizes (deep shared hierarchies, many
/// copies, large models and lists), pairs copies by what they are, and says when its tree is cut.
/// </summary>
public sealed class WorldComparisonBoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WorldNode Node(string name, Vector3? at = null, float yaw = 0)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried };
        node.SetPayloadInt(0, at == null ? 0x28 : 0x20);
        if (at is { } p)
        {
            float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
            float[] matrix = [c, 0, -s, 0, 1, 0, s, 0, c, p.X, p.Y, p.Z];
            for (int i = 0; i < matrix.Length; i++) node.SetPayloadFloat(0x30 + i * 4, matrix[i]);
        }
        return node;
    }
    private static WorldNode Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); return child; }
    /// <summary>A world of a root and every node below it, in the order first reached; <paramref name="order"/> may reorder the slots after the root.</summary>
    private static GameZWorld World(WorldNode root, Func<List<WorldNode>, IEnumerable<WorldNode>>? order = null)
    {
        GameZWorld world = new(); List<WorldNode> all = []; HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
        Stack<WorldNode> stack = new([root]);
        while (stack.TryPop(out var node))
        {
            if (!seen.Add(node)) continue;
            all.Add(node);
            foreach (var child in node.Children.Concat(node.Areas.SelectMany(a => a.Nodes)).Reverse()) stack.Push(child);
        }
        world.Nodes.AddRange(order == null ? all : [all[0], .. order(all.Skip(1).ToList())]);
        return world;
    }
    /// <summary>Levels of two nodes (a, b), each a child of both nodes of the level above: 2^k places at level k from 2k nodes.</summary>
    private static void Ladder(WorldNode top, int levels)
    {
        List<WorldNode> above = [top];
        for (int k = 0; k < levels; k++)
        {
            WorldNode a = Node("a"), b = Node("b");
            foreach (var parent in above) { Link(parent, a); Link(parent, b); }
            above = [a, b];
        }
    }
    private static Vector3 Position(WorldNode node) => WorldUpdate.LocalMatrix(node)!.Value.Translation;

    [Fact]
    public void DeepSharedHierarchiesKeepNoPathPerNode()
    {
        // Two hundred levels of the longest names, then levels each shared by both nodes above: 32,766 places whose paths
        // would hold about 7,000 characters each.
        GameZWorld Build()
        {
            WorldNode root = new("world1", WorldNodeClass.World), at = root;
            for (int i = 0; i < 200; i++) at = Link(at, Node($"level-{i:000}-of-a-long-chain-of-names"));
            Ladder(at, 14);
            return World(root);
        }
        var retail = Build(); var rebuilt = Build();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(comparison.Truncated);
        Assert.Equal(200 + (1 << 15) - 2, comparison.Counts[WorldComparisonStatus.Same] - 1);
        // Paths are made when asked: the eagerly built ones alone came to about 460 MB.
        Assert.True(allocated < 200L * 1024 * 1024, $"{allocated:N0} bytes allocated");
        var deepest = comparison.Roots[0];
        while (deepest.Children.Count > 0) deepest = deepest.Children[^1];
        Assert.Equal(1 + 200 + 14, deepest.Path.Split('/').Length);
        Assert.StartsWith("world/level-000-of-a-long-chain-of-names/level-001", deepest.Path);
        Assert.EndsWith("/b/b", deepest.Path);
    }

    [Fact]
    public async Task CopiesOfSharedHierarchiesAreToldApartOnce()
    {
        // Two identical crates, each holding forty levels shared by both nodes above (2^40 paths down): indistinguishable.
        GameZWorld Build(bool swapped)
        {
            WorldNode root = new("world1", WorldNodeClass.World), first = Link(root, Node("crate")), second = Link(root, Node("crate"));
            Ladder(first, 40); Ladder(second, 40);
            // The rebuilt world gives its highest crate slot to the other crate, so the lookup asks whether they are interchangeable.
            return World(root, swapped ? all => [.. all.Skip(81), .. all.Take(81)] : null);
        }
        var retail = Build(false); var rebuilt = Build(true);
        var crates = rebuilt.Nodes.Where(n => n.Name == "crate").ToList();
        Assert.True(await Task.Run(() => WorldComparer.Interchangeable(crates[0], crates[1], 0, Token), Token).WaitAsync(TimeSpan.FromSeconds(30), Token));
        var comparison = await Task.Run(() => WorldComparer.CompareTree(retail, rebuilt, token: Token), Token).WaitAsync(TimeSpan.FromSeconds(120), Token);
        var crate = Assert.Single(comparison.Bindings, b => b.Name == "crate");
        Assert.False(ReferenceEquals(crate.Counterpart, crate.Actual));
        Assert.True(crate.Interchangeable && crate.Same);
        // The merged tree stops growing, and says so, but every node is still paired.
        Assert.True(comparison.Truncated); Assert.False(comparison.PairingTruncated);
        Assert.Equal(retail.Nodes.Count, comparison.Counterparts.Count);
    }

    [Fact]
    public void ComparisonsTooLargeForTheTreeSaySoAndKeepEveryCounterpart()
    {
        // Thirty levels shared by both nodes above fill the tree before it reaches the yard after them, and the walls in it.
        // The rebuilt world has one node more before the walls, so their slots move and their lookup is checked.
        GameZWorld Build(bool extra)
        {
            WorldNode root = new("world1", WorldNodeClass.World);
            Ladder(Link(root, Node("ladder")), 30);
            var yard = Link(root, Node("yard"));
            Link(yard, Node("wall", new(1, 0, 0))); Link(yard, Node("wall", new(2, 0, 0)));
            var world = World(root);
            if (extra) world.Nodes.Insert(world.Nodes.Count - 2, Node("extra"));
            return world;
        }
        var retail = Build(false); var rebuilt = Build(true);
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: Token);
        Assert.True(comparison.Truncated); Assert.False(comparison.PairingTruncated);
        Assert.Equal(WorldComparer.MaximumTreeNodes, comparison.Counts.Values.Sum());
        // The world's row says it leaves out children: the yard.
        Assert.True(comparison.Roots[0].Truncated);
        Assert.Equal(["ladder"], comparison.Roots[0].Children.Select(c => c.Name));
        // The walls lie past the tree's end, yet are paired: the highest wall is the same node, so no lookup changed.
        foreach (var wall in retail.Nodes.Where(n => n.Name == "wall")) Assert.Equal(Position(wall), Position(comparison.Counterparts[wall]));
        (string, string)[] findNodes = [("gamegen/support/tex_fxm1.gw", "wall")];
        var before = WorldLookups.Resolve("m1", retail, null, findNodes, Token); var after = WorldLookups.Resolve("m1", rebuilt, null, findNodes, Token);
        Assert.NotEqual(before.Single().Slot, after.Single().Slot);
        Assert.Empty(WorldLookups.Changes(retail, before, rebuilt, after, Token));
        // The flat list says it is not the whole comparison.
        var truncated = Assert.Single(WorldComparer.Compare(retail, rebuilt), d => d.Field == "truncated");
        Assert.Contains("500000 nodes", truncated.Actual);
    }

    [Fact]
    public void HierarchiesDeeperThanTheTreeSaySo()
    {
        // A chain of 300 nodes; the rebuilt world moves the 258th, the first the tree does not open.
        GameZWorld Build(bool moved)
        {
            WorldNode root = new("world1", WorldNodeClass.World), at = root;
            for (int i = 0; i < 300; i++) at = Link(at, Node($"n{i}", moved && i == 257 ? new(1, 0, 0) : null));
            return World(root);
        }
        var comparison = WorldComparer.CompareTree(Build(false), Build(true), token: Token);
        var cut = comparison.Roots[0];
        while (cut.Children.Count > 0) cut = cut.Children[0];
        Assert.Equal("n257", cut.Name);
        Assert.True(cut.Truncated && comparison.Truncated && comparison.PairingTruncated);
        Assert.Contains(cut.Differences, d => d.Field == "matrix");
        Assert.Contains(WorldComparer.Compare(Build(false), Build(true)), d => d.Field == "truncated");
    }

    [Fact]
    public void CopiesAtOnePlacePairByTheirContents()
    {
        // Two crates at one place, one turned a quarter: the rebuilt world lists them the other way round.
        GameZWorld Build(bool reversed)
        {
            WorldNode root = new("world1", WorldNodeClass.World);
            WorldNode straight = Node("crate", new(5, 0, 0)), turned = Node("crate", new(5, 0, 0), MathF.PI / 2);
            foreach (var crate in reversed ? new[] { turned, straight } : [straight, turned]) Link(root, crate);
            return World(root);
        }
        var comparison = WorldComparer.CompareTree(Build(false), Build(true), token: Token);
        Assert.Empty(comparison.Differences);
        Assert.Equal(0, comparison.Roots[0].ChangedBelow);
    }

    [Fact]
    public void ManyCopiesOfANamePairByPosition()
    {
        // Three hundred walls on a grid 10 apart (more candidate pairs than are listed at once), every one nudged by up to
        // 1.5, so that neither their order nor any sort of their positions keeps them together.
        GameZWorld Build(bool nudged)
        {
            WorldNode root = new("world1", WorldNodeClass.World);
            for (int i = 0; i < 15; i++)
                for (int j = 0; j < 20; j++)
                    Link(root, Node("wall", new(10 * i + (nudged ? ((i * 7 + j * 3) % 5 - 2) * 0.5f : 0), 0, 10 * j + (nudged ? ((i * 3 + j * 5) % 5 - 2) * 0.5f : 0))));
            if (nudged) root.Children.Reverse();
            return World(root);
        }
        var walls = WorldComparer.CompareTree(Build(false), Build(true), token: Token).Roots[0].Children;
        Assert.Equal(300, walls.Count);
        Assert.All(walls, w => Assert.InRange(Vector3.Distance(Position(w.Actual!), Position(w.Expected!)), 0, 1.5f));
    }

    [Fact]
    public void PairDifferencesAreBoundedBeforeTheyAreDescribed()
    {
        // A world whose 5,000 cells all hold another node, a crate that lost all but one of its 2,000 lids, and a world
        // inside it whose 100 cells differ and which lost its lid.
        GameZWorld Build(bool rebuilt)
        {
            WorldNode root = new("world1", WorldNodeClass.World), member = Link(root, Node(rebuilt ? "y" : "x"));
            for (int i = 0; i < 5000; i++) { WorldArea area = new(); area.Nodes.Add(member); root.Areas.Add(area); }
            var crate = Link(root, Node("crate"));
            for (int i = 0; i < (rebuilt ? 1 : 2000); i++) Link(crate, Node($"lid{i:0000}"));
            var inner = Link(root, new WorldNode("inner", WorldNodeClass.World));
            for (int i = 0; i < 100; i++) { WorldArea area = new(); area.Nodes.Add(member); inner.Areas.Add(area); }
            if (!rebuilt) Link(inner, Node("lid"));
            return World(root);
        }
        var comparison = WorldComparer.CompareTree(Build(false), Build(true), token: Token);
        var world = comparison.Roots[0];
        Assert.Equal(5000, world.DifferenceCount);
        Assert.Equal(WorldComparer.MaximumNodeDifferences, world.Differences.Count);
        Assert.Equal(WorldComparisonStatus.Changed, world.Status);
        var children = Assert.Single(world.Children.Single(c => c.Name == "crate").Differences, d => d.Field == "children");
        Assert.InRange(children.Expected.Length, 1, 600);
        Assert.EndsWith("(2000 in all)", children.Expected);
        Assert.Equal("lid0000", children.Actual);
        Assert.True(comparison.DifferenceCount > 5000);
        // The fields the window's property lines highlight are listed before the many cells.
        var inner = world.Children.Single(c => c.Name == "inner");
        Assert.Equal((101, 64), (inner.DifferenceCount, inner.Differences.Count));
        Assert.Equal("children", inner.Differences[0].Field);
    }

    [Fact]
    public void DifferencesOfSharedNodesAreDescribedOnce()
    {
        // Twelve levels of two worlds (any node may be one), each under both above: 8,190 places of nodes whose 64 cells
        // all hold other members, with the longest names.
        GameZWorld Build(bool rebuilt)
        {
            WorldNode root = new("world1", WorldNodeClass.World);
            List<WorldNode> members = [.. Enumerable.Range(0, 9).Select(i => Link(root, Node($"member-{i}-with-the-longest-name-xxxxx")))];
            List<WorldNode> above = [Link(root, Node("ladder"))];
            for (int k = 0; k < 12; k++)
            {
                List<WorldNode> level = [new("a", WorldNodeClass.World), new("b", WorldNodeClass.World)];
                foreach (var node in level)
                {
                    for (int i = 0; i < 64; i++) { WorldArea area = new(); area.Nodes.AddRange(members.Skip(rebuilt ? 1 : 0).Take(8)); node.Areas.Add(area); }
                    foreach (var parent in above) Link(parent, node);
                }
                above = level;
            }
            return World(root);
        }
        var retail = Build(false); var rebuilt = Build(true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Described at every place, they came to about 600 MB.
        Assert.True(allocated < 150L * 1024 * 1024, $"{allocated:N0} bytes allocated");
        Assert.Equal(8190 * 64, comparison.DifferenceCount);
        Assert.Equal(10_000, comparison.Differences.Count);
        var deepest = comparison.Roots[0].Children.Single(c => c.Name == "ladder");
        while (deepest.Children.Count > 0) deepest = deepest.Children[^1];
        Assert.Equal((64, 64), (deepest.DifferenceCount, deepest.Differences.Count));
        Assert.All(deepest.Differences, d => Assert.Equal(deepest.Path, d.Path));
        Assert.EndsWith("/b/b", deepest.Path);
    }

    [Fact]
    public void ModelsSharedByManyNodesAreComparedOnce()
    {
        // 500 nodes share a model of 1,000 triangles; the rebuilt model gives one triangle another texture.
        WorldModel Model(string texture)
        {
            WorldModel model = new(); WorldMaterial plain = new() { Texture = new("rock") }, other = new() { Texture = new(texture) };
            for (int i = 0; i < 1000; i++)
            {
                for (int k = 0; k < 3; k++) model.Vertices.Add(new(i, k, i * 0.5f + k));
                model.Polygons.Add(new() { Vertices = [3 * i, 3 * i + 1, 3 * i + 2], Uvs = [new(0, 0), new(1, 0), new(0, 1)], Material = i == 7 ? other : plain });
            }
            return model;
        }
        GameZWorld Build(WorldModel model)
        {
            WorldNode root = new("world1", WorldNodeClass.World);
            for (int i = 0; i < 500; i++) Link(root, Node($"rock{i:000}")).Model = model;
            return World(root);
        }
        var retail = Build(Model("rock")); var rebuilt = Build(Model("lava"));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var polygons = comparison.Roots[0].Children.Select(c => Assert.Single(c.Differences)).ToList();
        Assert.Equal(500, polygons.Count);
        Assert.All(polygons, d => Assert.Equal("model.polygons", d.Field));
        Assert.StartsWith($"1000: {new Vector3(7, 0, 3.5f)}|{new Vector2(0, 0)};", polygons[0].Expected);
        Assert.Contains("#rock", polygons[0].Expected); Assert.Contains("(999 identical)", polygons[0].Actual); Assert.Contains("#lava", polygons[0].Actual);
        // Each model's polygons are read once, not once per node: per node, that came to about 300 MB.
        Assert.True(allocated < 48L * 1024 * 1024, $"{allocated:N0} bytes allocated");
    }

    [Fact]
    public void PairingKeysOfSharedHierarchiesStaySmall()
    {
        // Six levels of twelve nodes, each holding every node of the level below: its structure written out six levels
        // down would be about 45 million characters.
        WorldNode top = Node("top"); List<WorldNode> above = [top];
        for (int level = 0; level < 6; level++)
        {
            List<WorldNode> nodes = [.. Enumerable.Range(0, 12).Select(i => Node($"n{level}{i:00}"))];
            foreach (var parent in above) foreach (var node in nodes) Link(parent, node);
            above = nodes;
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        string key = WorldComparer.PairKey(top);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8L * 1024 * 1024);
        Assert.InRange(key.Length, 1, 64);
        // The key still tells structures apart.
        WorldNode other = Node("top"); foreach (var child in top.Children.Skip(1)) Link(other, child);
        Assert.NotEqual(key, WorldComparer.PairKey(other));
    }
}
