using System.Numerics;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>Two worlds compared as one merged tree: matched by parent-child structure, not by node order or slots.</summary>
public sealed class WorldComparisonTreeTests
{
    private static WorldNode Node(string name, params WorldNode[] children)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried };
        node.SetPayloadInt(0, 0x28);
        foreach (var child in children) { node.Children.Add(child); child.Parents.Add(node); }
        return node;
    }
    /// <summary>The node placed at <paramref name="to"/>: its stored matrix (rows X, Y, Z, translation) instead of identity.</summary>
    private static WorldNode Moved(WorldNode node, Vector3 to)
    {
        node.SetPayloadInt(0, 0x20);
        float[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1, to.X, to.Y, to.Z];
        for (int i = 0; i < matrix.Length; i++) node.SetPayloadFloat(0x30 + i * 4, matrix[i]);
        return node;
    }
    /// <summary>A world holding <paramref name="members"/>, then the nodes no parent holds, in slot order as listed.</summary>
    private static GameZWorld World(WorldNode[] members, WorldNode[] detached, Func<List<WorldNode>, List<WorldNode>>? order = null)
    {
        GameZWorld world = new();
        WorldNode root = new("world1", WorldNodeClass.World);
        foreach (var member in members) { root.Children.Add(member); member.Parents.Add(root); }
        List<WorldNode> all = [root];
        void Add(WorldNode n) { if (!all.Contains(n)) all.Add(n); foreach (var c in n.Children) Add(c); }
        foreach (var n in members.Concat(detached)) Add(n);
        world.Nodes.AddRange(order?.Invoke(all) ?? all);
        return world;
    }

    [Fact]
    public void NodesMatchByStructureAndReportWhatDiffers()
    {
        // Retail: a crate with a lid, a gate, a lamp; two smoke templates no parent holds.
        var retail = World([Node("crate", Node("lid")), Node("gate"), Moved(Node("lamp"), new(1, 2, 3))], [Node("smoke1", Node("puff")), Node("smoke1")]);
        // Rebuilt, in another node order: the lid is missing, the lamp moved, a sign was added under the gate.
        var rebuilt = World([Node("gate", Node("sign")), Moved(Node("lamp"), new(4, 5, 6)), Node("crate")], [Node("smoke1"), Node("smoke1", Node("puff"))],
            all => [all[0], .. all.Skip(1).Reverse()]);
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: TestContext.Current.CancellationToken);

        var world = comparison.Roots[0];
        Assert.Equal(("world1", WorldComparisonStatus.Same), (world.Name, world.Status));
        // Children follow the retail order whatever the rebuilt one; each pair knows both nodes and slots.
        Assert.Equal(["crate", "gate", "lamp"], world.Children.Select(c => c.Name));
        var crate = world.Children[0];
        Assert.Equal(WorldComparisonStatus.Changed, crate.Status);
        Assert.Contains(crate.Differences, d => d.Field == "children" && d.Expected == "lid" && d.Actual == "");
        Assert.Equal(("world/crate/lid", WorldComparisonStatus.OnlyExpected), (crate.Children.Single().Path, crate.Children.Single().Status));
        var gate = world.Children[1];
        Assert.Equal(WorldComparisonStatus.OnlyActual, gate.Children.Single(c => c.Name == "sign").Status);
        var lamp = world.Children[2];
        Assert.Equal(WorldComparisonStatus.Changed, lamp.Status);
        Assert.Contains(lamp.Differences, d => d.Field == "matrix");
        Assert.Equal(1, comparison.ExpectedSlots[crate.Expected!]); Assert.NotEqual(comparison.ExpectedSlots[crate.Expected!], comparison.ActualSlots[crate.Actual!]);
        // Three nodes differ below the world: the crate, its lid, the sign (the gate itself is changed too) and the lamp.
        Assert.Equal(world.Children.Sum(c => (c.Status == WorldComparisonStatus.Same ? 0 : 1) + c.ChangedBelow), world.ChangedBelow);
        Assert.Equal(5, world.ChangedBelow);
        // The smoke templates pair up by structure, so the one with a puff matches the one with a puff.
        var smokes = comparison.Roots.Skip(1).ToList();
        Assert.Equal(["smoke1", "smoke1"], smokes.Select(s => s.Name));
        Assert.All(smokes, s => Assert.Equal(WorldComparisonStatus.Same, s.Status));
        Assert.Single(smokes, s => s.Children.Count == 1);
        // The flat comparison is this tree's differences.
        Assert.Equal(WorldComparer.Compare(retail, rebuilt).Select(d => (d.Path, d.Field)), comparison.Differences.Select(d => (d.Path, d.Field)));
    }

    [Fact]
    public void RepeatedNamesReportWhichNodeLookupsFind()
    {
        // The engine finds a name's highest slot first. In the retail world that is the smoke with a puff; the rebuilt world
        // gives its highest smoke slot to the other one, so lookups by the name find another node.
        var retail = World([Node("smoke1"), Node("smoke1", Node("puff"))], []);
        var rebuilt = World([Node("smoke1"), Node("smoke1", Node("puff"))], [], all => [all[0], all[2], all[3], all[1]]);
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: TestContext.Current.CancellationToken);
        var binding = Assert.Single(comparison.Bindings);
        Assert.Equal(("smoke1", 2, 2, false), (binding.Name, binding.ExpectedCount, binding.ActualCount, binding.Same));
        var flagged = Assert.Single(comparison.Roots[0].Children, c => c.BindsElsewhere);
        Assert.Single(flagged.Children);
        // The trees themselves are the same: only the order differs.
        Assert.Empty(comparison.Differences);
        Assert.Equal(0, comparison.Roots[0].ChangedBelow);

        var same = WorldComparer.CompareTree(retail, retail, token: TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(same.Bindings).Same);
        Assert.DoesNotContain(same.Roots[0].Children, c => c.BindsElsewhere);
    }

    [Fact]
    public void LookupsThatFindAnIndistinguishableCopyFindTheSameThing()
    {
        // Two identical crates under the world: whichever the highest slot holds, lookups find the same thing.
        var retail = World([Node("crate", Node("lid")), Node("crate", Node("lid"))], []);
        var rebuilt = World([Node("crate", Node("lid")), Node("crate", Node("lid"))], [], all => [all[0], all[3], all[4], all[1], all[2]]);
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: TestContext.Current.CancellationToken);
        var crate = Assert.Single(comparison.Bindings, b => b.Name == "crate");
        Assert.False(ReferenceEquals(crate.Counterpart, crate.Actual));
        Assert.True(crate.Interchangeable && crate.Same);
        Assert.DoesNotContain(comparison.Roots[0].Children, c => c.BindsElsewhere);

        // Moved apart, they are told apart: the lookup finds another crate.
        var placed = World([Moved(Node("crate", Node("lid")), new(1, 0, 0)), Moved(Node("crate", Node("lid")), new(2, 0, 0))], []);
        var swapped = World([Moved(Node("crate", Node("lid")), new(1, 0, 0)), Moved(Node("crate", Node("lid")), new(2, 0, 0))], [], all => [all[0], all[3], all[4], all[1], all[2]]);
        var moved = WorldComparer.CompareTree(placed, swapped, token: TestContext.Current.CancellationToken);
        var binding = Assert.Single(moved.Bindings, b => b.Name == "crate");
        Assert.False(binding.Interchangeable || binding.Same);
        Assert.Single(moved.Roots[0].Children, c => c.BindsElsewhere);
        // A difference deeper down tells them apart too.
        var deep = World([Node("crate", Node("lid")), Node("crate", Moved(Node("lid"), new(0, 1, 0)))], [], all => [all[0], all[3], all[4], all[1], all[2]]);
        Assert.False(Assert.Single(WorldComparer.CompareTree(World([Node("crate", Node("lid")), Node("crate", Moved(Node("lid"), new(0, 1, 0)))], []), deep,
            token: TestContext.Current.CancellationToken).Bindings, b => b.Name == "crate").Same);
    }
}
