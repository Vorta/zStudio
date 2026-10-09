using System.Buffers.Binary;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class DatabaseInferenceMembershipTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Fixture(GameZWorld World, WorldDecomposition Build, WorldNode Reference, WorldNode Reordered);

    private static Fixture ReadWideDatabase(int count, bool reorderChildren = false, bool repeatedNames = false)
    {
        // A cache mismatch makes Trim and Combined separate a wide part's copied content from its own records.
        // The former List.Contains predicate compared each child with the complete content list on every attempt.
        List<WorldNode> objects = [.. Enumerable.Range(0, count).Select(i => new WorldNode(repeatedNames && i > 0 ? "same" : $"n{i}", WorldNodeClass.Object3D))];
        WorldNode reference = new("empty.flt", WorldNodeClass.Object3D), tail = new("tail", WorldNodeClass.Object3D);
        objects.AddRange([reference, tail]);
        foreach (var node in objects) { node.Flags = 0x0108001C; node.SetPayloadInt(0, 0x28); }
        // Inference sorts these actual source children before trying its caches. A refused candidate must undo it.
        if (reorderChildren)
        {
            objects[0].Children.AddRange([objects[2], objects[1]]);
            objects[1].Parents.Add(objects[0]); objects[2].Parents.Add(objects[0]);
        }
        WorldNode root = new("world", WorldNodeClass.World) { Flags = 0x0108001C };
        root.Children.AddRange(objects);
        foreach (var node in objects) node.Parents.Add(root);
        GameZWorld world = new() { FreeHead = 1, NodeCapacity = count + 10 };
        world.Nodes.Add(root); world.Nodes.AddRange(objects);
        int[] chain = [1, count + 4, 2, count + 5, count + 6];
        for (int i = 0; i < chain.Length; i++)
        {
            byte[] record = new byte[GameZWriter.NodeSlotSize];
            record[0] = (byte)'g'; // A historical name distinguishes a freed record from the never-used tail.
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(record.Length - 4), i + 1 < chain.Length ? (uint)chain[i + 1] : 0xFFFFFF);
            world.FreedSlots.Add(chain[i], record);
        }
        // Exercise accepted serialized input, including live/free slot identity and graph validation.
        world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(world, Token), token: Token), Token);
        root = world.Nodes.Single(n => n.Class == WorldNodeClass.World);
        reference = world.Nodes.Single(n => n.Name == "empty.flt");
        var parent = world.Nodes.Single(n => n.Name == "n0");
        var content = world.Nodes.Where(n => n != root && !parent.Children.Contains(n)).ToList();
        TracedInstruction instruction = new("m1.gs", "LoadGameGen", [], [], null, []);
        LoadedModel database = new("m1.flt", "m1.flt", instruction, true, null, content, false) { Step = 1 };
        return new(world, new([database], new Dictionary<int, WorldNode> { [0] = root }), reference, parent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateMembershipAndCopyWorkShareABudgetAndRetryKeepsDistinctIdentities(bool repeatedNames)
    {
        var fixture = ReadWideDatabase(512, repeatedNames: repeatedNames);
        var children = fixture.World.Nodes.Select(n => (Node: n, Children: n.Children.ToArray())).ToArray();
        List<string> notes = [];
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => DatabaseRecords.Infer(fixture.World, fixture.Build,
            n => n == fixture.Reference, "m1", notes, Token, matchingLimit: 10_000));
        Assert.Contains("work limit", error.Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 16L << 20);
        foreach (var (node, original) in children) Assert.Equal(original, node.Children);
        Assert.Empty(notes);

        // A new operation has a fresh allowance and still keeps the same inexact but representable reading.
        var result = DatabaseRecords.Infer(fixture.World, fixture.Build, n => n == fixture.Reference, "m1", notes, Token);
        Assert.NotNull(result);
        Assert.Single(result.Parts);
        Assert.Contains(notes, n => n.Contains("not every node takes its shipped slot", StringComparison.Ordinal));
        HashSet<WorldNode> retained = new(WorldAssembler.Subtree(result.Roots), ReferenceEqualityComparer.Instance);
        Assert.All(fixture.World.Nodes.Where(n => n.Class != WorldNodeClass.World), n => Assert.Contains(n, retained));
        // A successful retry also retains each source node's original child order.
        foreach (var (node, original) in children) Assert.Equal(original, node.Children);
    }

    [Fact]
    public void CancellationAfterCandidateReorderingPropagatesAndRestoresTheDecodedWorld()
    {
        var fixture = ReadWideDatabase(512, reorderChildren: true);
        var children = fixture.World.Nodes.Select(n => (Node: n, Children: n.Children.ToArray())).ToArray();
        using CancellationTokenSource stop = new();
        bool IsReference(WorldNode node)
        {
            if (fixture.Reordered.Children[0].Name == "n1") stop.Cancel();
            return node == fixture.Reference;
        }
        List<string> notes = [];
        var error = Assert.ThrowsAny<OperationCanceledException>(() => DatabaseRecords.Infer(fixture.World, fixture.Build,
            IsReference, "m1", notes, stop.Token));
        Assert.Equal(stop.Token, error.CancellationToken);
        foreach (var (node, original) in children) Assert.Equal(original, node.Children);
        Assert.Empty(notes);
    }
}
