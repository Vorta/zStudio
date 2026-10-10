using System.Buffers.Binary;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldSourceReferenceBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly IReadOnlyList<IReadOnlyList<string>> Script = GameGenScriptText.Tokenize(
        "NewWorld world\nGameGenSetWorld world\nLoadGameGen m1.flt m1.flt\nDeleteTree m1.flt\n");

    [Fact]
    public void ReaderAcceptedDiamondRefusesBeforeCollectingExponentialReferenceOccurrences()
    {
        var world = ShippedDiamond(20);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => Reconstruct([new(1, world)]));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("reference traversal allowance", error.Message);
        // Previously 42 nodes in a <1 MiB accepted world allocated >131 MiB in this traversal before glTF refused it.
        Assert.True(allocated < 16L << 20, $"Reference expansion allocated {allocated:N0} bytes before refusal.");
    }

    [Fact]
    public void SeparateMissionTraversalsShareOneAllowance()
    {
        Assert.NotEmpty(Reconstruct([new(1, ShippedDiamond(3))], maximum: 30));
        var error = Assert.Throws<InvalidDataException>(() => Reconstruct([new(1, ShippedDiamond(3)), new(2, ShippedDiamond(3))], maximum: 30));
        Assert.Contains("reference traversal allowance", error.Message);
        Assert.NotEmpty(Reconstruct([new(1, ShippedDiamond(3))], maximum: 30));
    }

    private static List<WorldSources.Output> Reconstruct(IReadOnlyList<WorldSources.MissionWorld> missions, long maximum = WorldSources.MaximumReferenceVisits)
        => WorldSources.Reconstruct(missions, _ => Script, (_, _) => null, new HashSet<string>(), _ => 0, [], Token, maximumReferenceVisits: maximum);

    private static GameZWorld ShippedDiamond(int depth)
    {
        GameZWorld world = new() { FreeHead = 1, NodeCapacity = 128 };
        var root = Node("world", WorldNodeClass.World); var leaf = Node("empty.flt"); world.Nodes.Add(root);
        List<WorldNode[]> levels = [];
        for (int i = 0; i < depth; i++) { WorldNode[] level = [Node($"a{i}"), Node($"b{i}")]; levels.Add(level); world.Nodes.AddRange(level); }
        world.Nodes.Add(leaf);
        foreach (var node in levels[0]) Link(root, node);
        for (int i = 0; i < depth; i++)
            foreach (var parent in levels[i])
                foreach (var child in i + 1 < depth ? levels[i + 1] : new[] { leaf }) Link(parent, child);
        // A historical deleted group forces the accepted slot-order inference fallback, retaining the shared DAG.
        byte[] free = new byte[GameZWriter.NodeSlotSize]; free[0] = (byte)'g';
        BinaryPrimitives.WriteUInt32LittleEndian(free.AsSpan(free.Length - 4), 0xffffff); world.FreedSlots[1] = free;
        return GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(world, Token), token: Token), Token);
    }

    private static void Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); }
    private static WorldNode Node(string name, WorldNodeClass kind = WorldNodeClass.Object3D)
    {
        WorldNode node = new(name, kind) { Flags = 0x0108001C };
        if (kind == WorldNodeClass.Object3D) node.SetPayloadInt(0, 0x28);
        return node;
    }
}
