using System.IO;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class SceneTreeActorOwnershipTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RepeatedAivProducerKeepsEveryOccurrenceAndTreeRejectsAmbiguousSelection()
    {
        const int count = 128;
        var scene = Scene(Enumerable.Range(1, count).ToArray());
        scene.Nodes[0] = scene.Nodes[0] with { Name = "tank", Parents = [count + 1] };
        for (int i = 1; i <= count; i++) scene.Nodes.Add(Node(i, [], [0]));
        scene.Nodes.Add(Node(count + 1, [0]) with { Name = "world", Class = "world" });
        var world = new ZbdDocument("mission.zbd", new(0, DateTime.MinValue),
            new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        var rows = Enumerable.Range(0, count).SelectMany(i => new JsonNode[]
        {
            Str("tank"), Arr(Number(0), Arr(Number(i), Number(0), Number(0)), Number(0))
        }).ToArray();
        var mission = MissionSceneLoader.Build(world, null, Arr(rows), Arr(Str("tank"), Arr()), null,
            token: Token, aivSource: new(Path.GetFullPath("placements.zbd"), 0, "aiv.zrd"));
        Assert.Equal(count, mission.Actors.Count);
        Assert.Equal(Enumerable.Range(0, count), mission.Actors.Select(a => a.CoordinateSource!.RecordIndex));
        var identities = MainWindow.TreeIdentities(mission.Scene, mission, null);
        var refreshed = MainWindow.TreeIdentities(mission.Scene, mission, null);
        for (int i = 0; i <= count; i++)
        {
            Assert.Equal("Ambiguous mission instance", identities(i).Placement);
            Assert.NotEqual(identities(i).Key, refreshed(i).Key);
            Assert.Null(mission.ActorAt(i)); // Renderer inspection has the same ambiguity outcome.
        }
        Assert.Equal("source:" + (count + 1), identities(count + 1).Key);
        SceneTreeState state = new();
        var tree = Tree(mission.Scene, mission, state); tree.Select(tree.Reveal(1));
        Assert.NotNull(tree.Selected);
        Assert.Null(Tree(mission.Scene, mission, state).Selected);
        Assert.False(scene.Nodes[0].Data.ContainsKey("transform"));
    }

    [Fact]
    public void OnlyThePublishedSceneReusesPreparedOwnershipAndCancellationCannotPublishFallback()
    {
        var scene = Scene([1], [], []);
        MissionActor actor = new(0, 7, "vehicle", "aiv");
        var mission = Mission(scene, [actor]);
        var cached = mission.PrepareActorIndex(scene, Token);
        Assert.Same(cached, mission.PrepareActorIndex(scene, Token));
        var other = Scene([2], [], []);
        var fallback = mission.PrepareActorIndex(other, Token);
        Assert.NotSame(cached, fallback);
        Assert.Same(actor, cached.At(1)); Assert.Null(cached.At(2));
        Assert.Null(fallback.At(1)); Assert.Same(actor, fallback.At(2));
        var identity = MainWindow.TreeIdentities(other, mission, null);
        Assert.Equal("source:1", identity(1).Key);
        Assert.Equal("aiv", identity(2).Placement);
        Assert.Same(actor, mission.ActorAt(1)); // Fallback must not replace the baseline's index.
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => mission.PrepareActorIndex(scene, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => mission.PrepareActorIndex(other, canceled.Token));
        Assert.Same(actor, mission.PrepareActorIndex(other, Token).At(2));
        var oversized = new GameScene();
        oversized.Nodes.AddRange(Enumerable.Repeat(Node(0, []), 200_001));
        Assert.Throws<InvalidDataException>(() => MainWindow.TreeIdentities(oversized, mission, null));
        Assert.Same(cached, mission.PrepareActorIndex(scene, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TreePreparationAllocationGrowsLinearlyForRepeatedRoots(bool otherTopology)
    {
        var small = Repeated(2000); var large = Repeated(4000);
        var smallScene = otherTopology ? Fan(2000) : small.Scene;
        var largeScene = otherTopology ? Fan(4000) : large.Scene;
        _ = MainWindow.TreeIdentities(smallScene, small, null); // Warm outside the allocation measurement.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var first = MainWindow.TreeIdentities(smallScene, small, null);
        long smallBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var second = MainWindow.TreeIdentities(largeScene, large, null);
        long largeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(largeBytes < smallBytes * 3 + 32_768, $"Tree preparation allocated {smallBytes:N0} then {largeBytes:N0} bytes.");
        Assert.True(largeBytes < 2 * 1024 * 1024, $"Tree preparation allocated {largeBytes:N0} bytes.");
        Assert.Equal("Ambiguous mission instance", first(2000).Placement);
        Assert.Equal("Ambiguous mission instance", second(4000).Placement);
        var index = large.PrepareActorIndex(largeScene, Token);
        Assert.Equal(4001, index.NodeVisits); Assert.Equal(4000, index.EdgeVisits);
        Assert.True(index.IsAmbiguous(4000));
    }

    [Fact]
    public void EqualRecordsAndRepeatedReferencesRemainAmbiguousWhileAbsentNodesRetainSourceIdentity()
    {
        var scene = Scene([1], [], []);
        MissionActor actor = new(0, 0, "same", "source");
        foreach (var other in new[] { actor, actor with { } })
        {
            var mission = Mission(scene, [actor, other, new(-1, 0, "invalid", "source")]);
            var index = mission.PrepareActorIndex(scene, Token);
            var first = MainWindow.TreeIdentities(scene, mission, null);
            var second = MainWindow.TreeIdentities(scene, mission, null);
            Assert.True(index.IsAmbiguous(0)); Assert.True(index.IsAmbiguous(1));
            Assert.False(index.IsAmbiguous(2)); Assert.Null(index.At(2));
            Assert.NotEqual(first(1).Key, second(1).Key);
            Assert.Equal("source:2", first(2).Key); Assert.Equal(first(2).Key, second(2).Key);
            Assert.False(index.IsAmbiguous(-1)); Assert.False(index.IsAmbiguous(3));
            Assert.Equal("source:-1", first(-1).Key); Assert.Equal("source:3", first(3).Key);
        }
        var unowned = MainWindow.TreeIdentities(scene, null, null);
        Assert.Equal("source:1", unowned(1).Key);
    }

    [Fact]
    public void SharedPathsCyclesAndWorldPartitionsKeepUniqueAndAmbiguousOwnershipSeparate()
    {
        var scene = Scene([1, 1, -1, 99], [3], [], [1], []);
        scene.Nodes[0] = scene.Nodes[0] with { Class = "world", Data = new JsonObject
        {
            ["partitions"] = new JsonArray(new JsonArray(new JsonObject { ["node_indices"] = new JsonArray(2, 2, -1, 99) }))
        } };
        MissionActor outer = new(0, 0, "outer", "world"), inner = new(1, 1, "inner", "nested");
        var mission = Mission(scene, [outer, inner]);
        // A distinct topology instance must take the bounded fallback, including partition children.
        var baseline = Mission(Scene([], [], [], [], []), [outer, inner]);
        foreach (var context in new[] { mission, baseline })
        {
            var identity = MainWindow.TreeIdentities(scene, context, null);
            Assert.Equal("world", identity(0).Placement); Assert.Equal("world", identity(2).Placement);
            Assert.Equal("Ambiguous mission instance", identity(1).Placement);
            Assert.Equal("Ambiguous mission instance", identity(3).Placement);
            Assert.Equal("source:4", identity(4).Key);
        }
        var unique = Mission(scene, [outer]);
        var uniqueIdentity = MainWindow.TreeIdentities(scene, unique, null);
        Assert.Equal("world", uniqueIdentity(3).Placement);
        Assert.Same(outer, unique.ActorAt(3));
    }

    [Fact]
    public void UniqueCoordinateProvenanceRetainsSelectionAfterCloneReordering()
    {
        MissionPickupSource source = new("archive", 2, "aiv.zrd", 5);
        var before = Mission(Scene([1, 2], [], []), [new(1, 7, "tank", "aiv", CoordinateSource: source)], [0, 7, 8]);
        var after = Mission(Scene([2, 1], [], []), [new(2, 7, "tank", "aiv", CoordinateSource: source)], [0, 8, 7]);
        SceneTreeState state = new(); var old = Tree(before.Scene, before, state); old.Select(old.Reveal(1));
        Assert.Equal(2, Tree(after.Scene, after, state).Selected?.Index);
        var unrelated = Mission(after.Scene, [after.Actors[0] with { CoordinateSource = source with { RecordIndex = 6 } }], [0, 8, 7]);
        Assert.Null(Tree(unrelated.Scene, unrelated, state).Selected);
    }

    private static MissionSceneContext Repeated(int count) => Mission(Fan(count),
        Enumerable.Range(0, count).Select(i => new MissionActor(0, 0, "actor", "aiv",
            CoordinateSource: new("archive", 0, "aiv.zrd", i))).ToList());
    private static GameScene Fan(int count)
    {
        var scene = Scene(Enumerable.Range(1, count).ToArray());
        for (int i = 1; i <= count; i++) scene.Nodes.Add(Node(i, [], [0]));
        return scene;
    }
    private static MissionSceneContext Mission(GameScene scene, List<MissionActor> actors, List<int>? sources = null)
        => new(scene, sources ?? Enumerable.Range(0, scene.Nodes.Count).ToList(), actors, [], [],
            MissionLayoutSelection.For(MissionDifficulty.Medium), scene.Nodes.Count, Token);
    private static SceneTreeModel Tree(GameScene scene, MissionSceneContext mission, SceneTreeState state)
        => new(scene, "fixture", true, state, MainWindow.TreeIdentities(scene, mission, null));
    private static GameScene Scene(params int[][] edges)
    {
        GameScene scene = new();
        for (int i = 0; i < edges.Length; i++) scene.Nodes.Add(Node(i, edges[i]));
        return scene;
    }
    private static GameNode Node(int index, int[] children, int[]? parents = null)
        => new(index, "node", "object3d", null, parents ?? [], children, new() { ["flags"] = 8 }, new());
    private static JsonObject Arr(params JsonNode[] children) => new() { ["type"] = "array", ["children"] = new JsonArray(children) };
    private static JsonObject Str(string value) => new() { ["type"] = "string", ["value"] = value };
    private static JsonObject Number(int value) => new() { ["type"] = "int", ["value"] = value };
}
