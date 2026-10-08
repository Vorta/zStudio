using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Review fixes for name lookups and Compare worlds: children of a pair at many places are paired once and within one
/// budget for the comparison, a comparison that runs out of copy checks says so, MechWarrior 3 keeps its own unresolved
/// names as warnings, and an editor-chosen root binds its attach node as the game's loader binds one.
/// </summary>
public sealed class LookupCompareReviewFixTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WorldNode Node(string name, Vector3? at = null, uint flags = 0)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried | flags };
        node.SetPayloadInt(0, at == null ? 0x28 : 0x20);
        if (at is { } p)
        {
            float[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1, p.X, p.Y, p.Z];
            for (int i = 0; i < matrix.Length; i++) node.SetPayloadFloat(0x30 + i * 4, matrix[i]);
        }
        return node;
    }
    private static WorldNode Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); return child; }
    /// <summary>A world of a root and every node below it, in the order first reached.</summary>
    private static GameZWorld World(WorldNode root)
    {
        GameZWorld world = new(); HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
        Stack<WorldNode> stack = new([root]);
        while (stack.TryPop(out var node))
        {
            if (!seen.Add(node)) continue;
            world.Nodes.Add(node);
            foreach (var child in node.Children.AsEnumerable().Reverse()) stack.Push(child);
        }
        return world;
    }
    /// <summary>Each place's child pairs, in order.</summary>
    private static List<(WorldNode?, WorldNode?)[]> PairsAt(WorldComparison comparison, string name) =>
        [.. Places(comparison.Roots).Where(n => n.Name == name).Select(n => n.Children.Select(c => (c.Expected, c.Actual)).ToArray())];
    private static IEnumerable<WorldComparisonNode> Places(IEnumerable<WorldComparisonNode> nodes)
    {
        foreach (var node in nodes) { yield return node; foreach (var below in Places(node.Children)) yield return below; }
    }

    [Fact]
    public void ChildrenOfAPairAtManyPlacesArePairedOnce()
    {
        // The review's case: a shared node with 2,000 children of one name under 250 parents, every child moved by 2. Paired
        // again at each place, this took 81 s and 31.7 GB.
        GameZWorld Build(bool moved)
        {
            WorldNode root = new("world1", WorldNodeClass.World), shared = Node("shared");
            for (int i = 0; i < 250; i++) Link(Link(root, Node($"p{i}")), shared);
            for (int j = 0; j < 2000; j++) Link(shared, Node("piece", moved ? new(2, 0, 0) : Vector3.Zero));
            return World(root);
        }
        var retail = Build(false); var rebuilt = Build(true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 512L * 1024 * 1024, $"{allocated:N0} bytes allocated");
        // The tree holds as many places as it can, and each pairs the children the same way.
        Assert.True(comparison.Truncated);
        var places = PairsAt(comparison, "shared").Where(p => p.Length == 2000).ToList();
        Assert.True(places.Count > 200, $"{places.Count} whole places");
        Assert.All(places, p => Assert.Equal(places[0], p));
        // 4,000,000 candidate pairs of one name are more than are paired by position: in order, and the comparison says so.
        Assert.True(comparison.ApproximatePairing);
    }

    [Fact]
    public void ParentsHoldingTheSameChildrenPairThemOnce()
    {
        // 250 parents each hold the same 300 children of one name, every child moved by 2: each place's children cost 180,000
        // examined pairs, 45 million together, far past the comparison's budget, unless the same children are paired once.
        GameZWorld Build(bool moved)
        {
            WorldNode root = new("world1", WorldNodeClass.World);
            List<WorldNode> parents = [.. Enumerable.Range(0, 250).Select(i => Link(root, Node($"p{i}")))];
            for (int j = 0; j < 300; j++) { var piece = Node("piece", moved ? new(2, 0, 0) : Vector3.Zero); foreach (var parent in parents) Link(parent, piece); }
            return World(root);
        }
        var comparison = WorldComparer.CompareTree(Build(false), Build(true), token: Token);
        Assert.False(comparison.ApproximatePairing);
        var places = comparison.Roots[0].Children.Select(r => r.Children.Select(c => (c.Expected, c.Actual)).ToArray()).ToList();
        Assert.Equal(250, places.Count);
        Assert.All(places, p => Assert.Equal(places[0], p));
        Assert.Equal(300, places[0].Select(p => p.Actual).Distinct().Count());
    }

    [Fact]
    public void CopiesInOtherOrdersStayWithinTheComparisonsBudget()
    {
        // Sixty parents each hold the same 1,400 children of one name, each in another order, so no two lists of copies are
        // the same; every child is moved by 2. Each list is within a name's limit (1.96 million candidate pairs), but all of
        // them together are past the comparison's: the rest pair in order, and the comparison says so.
        GameZWorld Build(bool moved, int parents)
        {
            WorldNode root = new("world1", WorldNodeClass.World);
            List<WorldNode> pieces = [.. Enumerable.Range(0, 1400).Select(_ => Node("piece", moved ? new(2, 0, 0) : Vector3.Zero))];
            for (int i = 0; i < parents; i++) { var parent = Link(root, Node($"p{i}")); foreach (var piece in pieces.Skip(i * 20).Concat(pieces.Take(i * 20))) Link(parent, piece); }
            return World(root);
        }
        // One such list is paired by position.
        Assert.False(WorldComparer.CompareTree(Build(false, 1), Build(true, 1), token: Token).ApproximatePairing);
        var retail = Build(false, 60); var rebuilt = Build(true, 60);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(comparison.ApproximatePairing);
        Assert.True(allocated < 768L * 1024 * 1024, $"{allocated:N0} bytes allocated");
        // Every child is still paired at every place.
        Assert.Equal(60, comparison.Roots[0].Children.Count);
        Assert.All(comparison.Roots[0].Children, row => Assert.All(row.Children, c => Assert.True(c.Expected != null && c.Actual != null)));
    }

    [Fact]
    public void LookupChangesTheComparisonCannotConfirmSaySo()
    {
        // The crate a lookup finds becomes the copy whose 1,500 slats are listed the other way round: telling the two apart
        // takes more checks than a comparison makes, so the change is reported as possible rather than certain.
        GameZWorld Build(bool rebuilt)
        {
            WorldNode root = new("world1", WorldNodeClass.World);
            List<WorldNode> crates = [];
            for (int c = 0; c < 2; c++)
            {
                var crate = Link(root, Node("crate"));
                foreach (int i in c == 0 ? Enumerable.Range(0, 1500) : Enumerable.Range(0, 1500).Reverse()) Link(crate, Node("slat", flags: (uint)i << 9));
                crates.Add(crate);
            }
            var world = World(root);
            if (rebuilt) { int i = world.Nodes.IndexOf(crates[0]), j = world.Nodes.IndexOf(crates[1]); (world.Nodes[i], world.Nodes[j]) = (world.Nodes[j], world.Nodes[i]); }
            return world;
        }
        static SourceLookup Lookup(GameZWorld world, string fingerprint)
        {
            var top = GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken).Where(p => p.Key.Name == "crate").MaxBy(p => p.Value);
            return new("m1", SourceLookup.TextureEffect, "crate", "gamegen/support/tex_fxm1.gw", 2, top.Value, WorldLookups.Path(top.Key)) { Fingerprint = fingerprint };
        }
        GameZWorld before = Build(false), after = Build(true);
        var comparison = WorldComparer.CompareTree(before, after, token: Token);
        Assert.False(comparison.ApproximatePairing || comparison.PairingTruncated);
        var change = Assert.Single(WorldLookups.Changes(before, [Lookup(before, "reversed")], after, [Lookup(after, "forward")], Token));
        Assert.True(change.Uncertain);
        Assert.Contains("possibly", WorldLookups.Describe(change));
    }

    [Fact]
    public void CopyChecksThatRunOutAreReported()
    {
        // Two crates whose 1,500 slats differ only in their flags, listed the other way round in the second: telling the
        // crates apart takes about 1.1 million checks, more than a comparison makes. The barrels after them are plain copies.
        GameZWorld Build(bool rebuilt, bool crates)
        {
            WorldNode root = new("world1", WorldNodeClass.World);
            List<WorldNode> pair = [];
            if (crates)
                for (int c = 0; c < 2; c++)
                {
                    var crate = Link(root, Node("crate"));
                    foreach (int i in c == 0 ? Enumerable.Range(0, 1500) : Enumerable.Range(0, 1500).Reverse()) Link(crate, Node("slat", flags: (uint)i << 9));
                    pair.Add(crate);
                }
            WorldNode first = Link(root, Node("barrel", new(5, 0, 0))), second = Link(root, Node("barrel", new(5, 0, 0)));
            var world = World(root);
            // The rebuilt world gives the highest slot of each name to the other copy.
            if (rebuilt)
                foreach (var (a, b) in pair.Count == 2 ? new[] { (pair[0], pair[1]), (first, second) } : [(first, second)])
                {
                    int i = world.Nodes.IndexOf(a), j = world.Nodes.IndexOf(b);
                    (world.Nodes[i], world.Nodes[j]) = (world.Nodes[j], world.Nodes[i]);
                }
            return world;
        }
        var plain = WorldComparer.CompareTree(Build(false, false), Build(true, false), token: Token);
        var barrel = Assert.Single(plain.Bindings, b => b.Name == "barrel");
        Assert.True(barrel.Interchangeable && barrel.Same && !barrel.Unchecked);
        Assert.Equal(0, plain.UncheckedBindings);

        var comparison = WorldComparer.CompareTree(Build(false, true), Build(true, true), token: Token);
        barrel = Assert.Single(comparison.Bindings, b => b.Name == "barrel");
        Assert.False(barrel.Same);
        Assert.True(barrel.Unchecked);
        Assert.Equal(comparison.Bindings.Count(b => b.Unchecked), comparison.UncheckedBindings);
        Assert.True(comparison.UncheckedBindings >= 1);
    }

    private static GameNode SceneNode(int index, string name, int[] parents, int[] children, string type = "object3d") =>
        new(index, name, type, type == "object3d" ? 0 : null, parents, children, new JsonObject { ["flags"] = 4 },
            new JsonObject { ["flags"] = 0, ["opacity"] = 1f, ["scale"] = JsonData.Vector(Vector3.One), ["transform"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0) });
    private static AnimationRecord Named(int size, string name) { var record = new AnimationRecord(new byte[size]); record.SetText(0, name, 36); return record; }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MechWarrior3KeepsItsUnresolvedOwnNamesAsWarnings(bool mw3)
    {
        // An entry whose node reference names its own sound: RECOIL's ResolveNodeByName finds the sound node before the
        // world's, which the preview notes as information; MW3 has no such evidence, so the unresolved name stays a warning.
        uint version = mw3 ? 39u : 28u;
        byte[] prefix = new byte[mw3 ? 80 : 72]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), version);
        var package = new AnimationPackage { Prefix = prefix, Tail = [] };
        var entry = new AnimationEntry(new byte[mw3 ? 316 : 308], 0, prefix.Length); entry.SetText(0, "chopper"); entry.SetText(32, "heli"); entry.SetText(68, "heli"); entry.SetFloat(164, -1);
        package.Entries.Add(entry);
        entry.References[1].AddRange([new(new byte[40]), Named(40, "choppr_snd1")]);
        entry.References[3].AddRange([new(new byte[44]), Named(44, "choppr_snd1")]);
        var sequence = new AnimationSequence(new byte[64]) { Name = "motion" }; entry.Sequences.Add(sequence);
        var show = AnimationCatalog.Create(6, version); show.SetShort(16, 1); show.SetInt(12, 1); sequence.Events.Add(show);
        var doc = new ZbdDocument("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, mw3 ? 27u : 15u, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        doc.Scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [], []));
        doc.Scene.Nodes.Add(SceneNode(0, "heli", [], []));
        var frame = new AnimationPlayer(new AnimationPreviewContext { Package = package, World = doc }, 0).EvaluateForTest(.1);
        var note = Assert.Single(frame.Issues, i => i.Message.StartsWith("chopper: ", StringComparison.Ordinal) && i.Message.Contains("reference 1", StringComparison.Ordinal));
        if (mw3) Assert.Equal(("Warning", "Preview", "chopper: unresolved node reference 1."), (note.Severity, note.Category, note.Message));
        else Assert.Equal(("Information", "Support"), (note.Severity, note.Category));
    }

    [Fact]
    public void AChosenRootFindsItsAttachNodeAsTheLoaderDoes()
    {
        // A hit wall attached to a wall outside its root (1999 m4–m13: hit_wall_102, root dwall02, attaches to one of the
        // wall1). LoadZbd binds it at the first dwall02 and finds the attachment in the whole world; choosing the other dwall02
        // in the editor binds it there the same way, so its names resolve under the wall and the game does not disable it.
        GameScene scene = new();
        scene.Nodes.AddRange([SceneNode(0, "world", [], [1, 3, 5], "world"), SceneNode(1, "dwall02", [0], [2]), SceneNode(2, "healthy", [1], []),
            SceneNode(3, "dwall02", [0], [4]), SceneNode(4, "healthy", [3], []), SceneNode(5, "wall1", [0], [6]), SceneNode(6, "healthy", [5], [])]);
        scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [], []));
        var world = new ZbdDocument("gamez.zbd", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        var package = new AnimationPackage { Prefix = [], Tail = [] };
        AnimationEntry Entry(int index, string root, string attach, params string[] references)
        {
            var entry = new AnimationEntry(new byte[308], index, 0); entry.SetText(0, $"anim{index}"); entry.SetText(32, root); entry.SetText(68, attach); entry.SetFloat(164, -1);
            entry.References[1].Add(new(new byte[40]));
            foreach (string name in references) entry.References[1].Add(Named(40, name));
            entry.Sequences.Add(new(new byte[64]) { Name = "motion" });
            return entry;
        }
        var hit = Entry(1, "dwall02", "wall1", "healthy");
        var move = AnimationCatalog.Create(7); move.SetShort(28, 1); move.SetVector(16, new(0, 42, 0)); hit.Sequences[0].Events.Add(move);
        package.Entries.AddRange([Entry(0, "", ""), hit]);
        var context = new AnimationPreviewContext { Package = package, World = world };
        Assert.Equal((3, AnimationBinding.Loaded, 6), (context.ResolveRoot(hit), context.Binding(hit, 3), context.ResolveNode(hit, 1)));
        context.RootOverrides[1] = 1;
        Assert.Equal(AnimationBinding.Chosen, context.Binding(hit, 1));
        Assert.Equal(6, context.ResolveNode(hit, 1));
        Assert.Equal(6, context.ResolveTrackedNode(hit, "healthy", 1));
        Assert.False(context.RebindDisables(hit, 1, AnimationBinding.Chosen));
        var frame = new AnimationPlayer(context, 1).EvaluateForTest(.5);
        Assert.Equal(42, frame.Nodes.Single(p => p.SourceNode == 6).Transform.Translation.Y);
        Assert.DoesNotContain(frame.Diagnostics, d => d.Contains("the game disables", StringComparison.Ordinal));
        // A child started at the chosen root is a rebinding, which looks the attachment up only inside the node.
        Assert.True(context.RebindDisables(hit, 1, AnimationBinding.Rebound));
        Assert.Equal(2, context.ResolveInstanceNode(hit, 1, 1, AnimationBinding.Rebound));
    }
}
