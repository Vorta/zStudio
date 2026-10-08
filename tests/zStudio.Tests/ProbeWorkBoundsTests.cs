using System.Numerics;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ProbeWorkBoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ReaderAcceptedSharedModelsSpendOneAggregateAllowance()
    {
        // A small vertex table does not limit work: every instance scans the same 10,000 no-hit triangles.
        var world = ReadWorld(128, 10_000);
        Assert.Equal(4, world.Children[0].Model!.Vertices.Count);
        Assert.Same(world.Children[0].Model, world.Children[^1].Model);
        Assert.Contains("polygon-work", Assert.Throws<InvalidDataException>(() =>
            ZoneProbe.Probe(world, 20, 20, ZoneSet.Cleared, token: Token)).Message);
        Assert.Contains("no complete result", Assert.Throws<InvalidDataException>(() =>
            TerrainProbe.At(world.Children, 20, 20, Token)).Message);

        // A hit in the first polygon avoids scanning the tail; neither failure changes the next query or source.
        var hits = ZoneProbe.Probe(world, 1, 1, ZoneSet.Cleared, token: Token);
        Assert.Equal(ZoneProbe.MaximumHits, hits.Hits.Count);
        Assert.True(hits.Full);
        Assert.Equal(128, TerrainProbe.At(world.Children, 1, 1, Token).Count);
        Assert.Equal(10_000, world.Children[0].Model!.Polygons.Count);
    }

    [Fact]
    public void NoHitPolygonScansDoNotAllocateACornerArrayForEveryOccurrence()
    {
        var world = ReadWorld(64, 10_000);
        _ = ZoneProbe.Probe(world, 1, 1, ZoneSet.Cleared, token: Token); // warm the hit and traversal paths
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = ZoneProbe.Probe(world, 20, 20, ZoneSet.Cleared, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(result.Hits);
        Assert.False(result.Full);
        // The old reader-backed 640,000-polygon query allocated 76.8 MB. Leave ample room for node traversal.
        Assert.True(allocated < 1024 * 1024, $"The no-hit probe allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void CornersAreChargedBeforePolygonInspectionAndTheBudgetSpansNodes()
    {
        var world = ReadWorld(2, 10);
        // Exactly one node plus ten three-corner polygons: 41 units. Repeated nodes must share that allowance.
        Assert.Empty(TerrainProbe.At(world.Children.Take(1), 20, 20, Token, 41));
        Assert.Throws<InvalidDataException>(() => TerrainProbe.At(world.Children, 20, 20, Token, 41));
        Assert.Throws<InvalidDataException>(() => ZoneProbe.Probe(world, 20, 20, ZoneSet.Cleared,
            ZoneProbeKind.Point, ZoneProbe.Top, Token, 41));

        var wide = ReadWorld(1, 1, 255);
        Assert.Throws<InvalidDataException>(() => TerrainProbe.At(wide.Children, 20, 20, Token, 255));
        Assert.Throws<InvalidDataException>(() => ZoneProbe.Probe(wide, 20, 20, ZoneSet.Cleared,
            ZoneProbeKind.Point, ZoneProbe.Top, Token, 255));
        Assert.Empty(TerrainProbe.At(wide.Children, 20, 20, Token, 257));
        Assert.Empty(ZoneProbe.Probe(wide, 20, 20, ZoneSet.Cleared,
            ZoneProbeKind.Point, ZoneProbe.Top, Token, 257).Hits);
    }

    [Fact]
    public void ComparisonStillDisclosesIncompleteWorkInsteadOfReturningPartialSuccess()
    {
        var nodes = ReadWorld(2, 2).Children;
        var limited = TerrainProbe.Compare(nodes, nodes, 100, Token, null, 1000, 1);
        Assert.False(limited.Complete);
        Assert.Contains("no comparison result", limited.Limitation);
        Assert.Equal(0, limited.Samples);
        Assert.Empty(limited.Examples);
        var complete = TerrainProbe.Compare(nodes, nodes, 100, Token);
        Assert.True(complete.Complete);
        Assert.True(complete.Samples > 0);
        Assert.Equal(0, complete.Mismatches);
    }

    [Fact]
    public void EveryPublicProbeEntryObservesCancellationEvenForAnEmptyWorld()
    {
        var world = ReadWorld(0, 1);
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => ZoneProbe.Probe(world, 0, 0, ZoneSet.Cleared, token: cancel.Token));
        Assert.Throws<OperationCanceledException>(() => ZoneProbe.CameraZones(world, Vector3.Zero, ZoneSet.Cleared, ZoneSet.Cleared, token: cancel.Token));
        Assert.Throws<OperationCanceledException>(() => ZoneProbe.VehicleZones(world, Vector3.Zero, ZoneSet.Cleared, 1, token: cancel.Token));
        Assert.Throws<OperationCanceledException>(() => ZoneProbe.SpawnZones(world, Vector3.Zero, false, cancel.Token));
        Assert.Throws<OperationCanceledException>(() => ZoneProbe.ViewZones(world, Vector3.Zero, ZoneSet.Cleared, cancel.Token));
        Assert.Throws<OperationCanceledException>(() => TerrainProbe.At(world.Children, 0, 0, cancel.Token));
        Assert.Throws<OperationCanceledException>(() => TerrainProbe.Compare(world.Children, world.Children, 1, cancel.Token));
    }

    [Fact]
    public void TerrainCancellationBetweenNodesDoesNotReturnTheHitsAlreadyCollected()
    {
        var world = ReadWorld(2, 1);
        using CancellationTokenSource cancel = new();
        IEnumerable<WorldNode> CancelAfterFirst()
        {
            yield return world.Children[0];
            cancel.Cancel();
            yield return world.Children[1];
        }
        Assert.Throws<OperationCanceledException>(() => TerrainProbe.At(CancelAfterFirst(), 1, 1, cancel.Token));
        Assert.Equal(2, TerrainProbe.At(world.Children, 1, 1, Token).Count);
    }

    [Fact]
    public void ComparisonChargesUniqueModelVerticesBeforeScanningAndReusesTheirBounds()
    {
        var nodes = ReadWorld(128, 1, vertices: 100_000).Children;
        // The old implementation scans 204.8 million vertices here without charging any of them.
        var tooSmall = TerrainProbe.Compare(nodes, nodes, 100, Token, null, 1000, 99_999);
        Assert.False(tooSmall.Complete);
        Assert.Equal(0, tooSmall.Samples);
        // Two 128-hit sorts now reserve 7,936 work units in addition to node/index/probe work. This allowance
        // fits the one 100,000-vertex scan and that bounded overhead, but cannot fit even a second vertex scan.
        var shared = TerrainProbe.Compare(nodes, nodes, 100, Token, null, 1000, 120_000);
        Assert.True(shared.Complete);
        Assert.Equal(1, shared.Samples);
        Assert.Equal(0, shared.Mismatches);
    }

    [Fact]
    public void ComparisonCancelsDuringNodeEnumerationBeforeMaterializingTheRemainder()
    {
        var root = ReadWorld(1, 1);
        using CancellationTokenSource cancel = new();
        int visited = 0;
        var nodes = new ObservedNodes(root.Children[0], 10_000, () =>
        {
            if (++visited == 2) cancel.Cancel();
        });
        Assert.Throws<OperationCanceledException>(() => TerrainProbe.Compare(nodes, [], 100, cancel.Token));
        Assert.Equal(2, visited);
    }

    [Fact]
    public void ComparisonChargesParentTraversalAndIndexEntriesBeforeRetainingThem()
    {
        var root = ReadWorld(1, 1);
        var node = root.Children[0];
        // Public in-memory worlds may have many parent links; account for the complete scan before Any/iteration.
        node.Parents.Clear();
        for (int i = 0; i < 100; i++) node.Parents.Add(new("parent", WorldNodeClass.Object3D));
        var limitedParents = TerrainProbe.Compare(root.Children, root.Children, 100, Token, root, 1000, 100);
        Assert.False(limitedParents.Complete);
        var validParents = TerrainProbe.Compare(root.Children, root.Children, 100, Token, root, 1000, 1000);
        Assert.True(validParents.Complete);
        var limitedIndex = TerrainProbe.Compare(root.Children, root.Children, 100, Token, root, 2, 1000);
        Assert.False(limitedIndex.Complete);
    }

    private sealed class ObservedNodes(WorldNode node, int count, Action observe) : IReadOnlyList<WorldNode>
    {
        public int Count => count;
        public WorldNode this[int index] => node;
        public IEnumerator<WorldNode> GetEnumerator()
        {
            for (int i = 0; i < count; i++) { observe(); yield return node; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static WorldNode ReadWorld(int copies, int polygons, int corners = 3, int vertices = 4)
    {
        WorldModel model = new();
        // The unused far vertex makes the correctly cached box cover the no-hit query at (20,20).
        model.Vertices.AddRange([new(0, 0, 0), new(0, 0, 10), new(10, 0, 0), new(100, 0, 100)]);
        for (int i = model.Vertices.Count; i < vertices; i++) model.Vertices.Add(new(100, 0, 100));
        WorldMaterial material = new();
        for (int i = 0; i < polygons; i++) model.Polygons.Add(new()
        {
            Vertices = Enumerable.Range(0, corners).Select(v => v % 3).ToArray(), Material = material, Zone = 0xFFFF0501
        });
        GameZWorld source = new();
        WorldNode root = new("world", WorldNodeClass.World);
        source.Nodes.Add(root); source.Models.Add(model); source.Materials.Add(material);
        for (int i = 0; i < copies; i++)
        {
            WorldNode node = new($"node{i}", WorldNodeClass.Object3D)
            {
                Flags = 0x1C | ZoneProbe.BoundsFlag, Model = model,
                CachedBounds = new(new(0, 0, 0), new(100, 0, 100))
            };
            node.SetPayloadInt(0, 0x28);
            node.Parents.Add(root); root.Children.Add(node); source.Nodes.Add(node);
        }
        byte[] bytes = GameZWriter.Write(source, Token);
        var decoded = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("probe.zbd", bytes, token: Token), Token);
        return decoded.Nodes.Single(n => n.Class == WorldNodeClass.World);
    }
}
