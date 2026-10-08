using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class MissionActorIndexTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RepeatedPathsAreOneActorButNestedActorsMakeSharedNodesAmbiguous()
    {
        var scene = Scene([1, 2], [3, 3], [3], []);
        MissionActor first = new(0, 0, "first", "source"), second = new(2, 2, "second", "source");
        var one = MissionActorIndex.Build(scene, [first], Token);
        for (int i = 0; i < 4; i++) Assert.Same(first, one.At(i));
        var two = MissionActorIndex.Build(scene, [first, second], Token);
        Assert.Same(first, two.At(0)); Assert.Same(first, two.At(1));
        Assert.Null(two.At(2)); Assert.Null(two.At(3));
        Assert.Null(two.At(-1)); Assert.Null(two.At(4));
        // Closing the graph into a cycle must terminate and carry both actors' ambiguity through the cycle.
        scene.Nodes[3] = scene.Nodes[3] with { Children = [0] };
        var cycle = MissionActorIndex.Build(scene, [first, second], Token);
        for (int i = 0; i < 4; i++) Assert.Null(cycle.At(i));
        Assert.InRange(cycle.NodeVisits, 1, 2 * scene.Nodes.Count);
    }

    [Fact]
    public void EqualActorRecordsAndRepeatedObjectReferencesAreSeparateOccurrences()
    {
        var scene = Scene([1], []);
        MissionActor actor = new(0, 0, "same", "source");
        foreach (var other in new[] { actor, actor with { } })
        {
            var index = MissionActorIndex.Build(scene, [actor, other], Token);
            Assert.Null(index.At(0)); Assert.Null(index.At(1));
        }
        var missing = MissionActorIndex.Build(scene, [new(-1, -1, "absent", "source"), actor], Token);
        Assert.Same(actor, missing.At(1));
        Assert.Null(MissionActorIndex.Build(new GameScene(), [actor], Token).At(0));
    }

    [Fact]
    public void WorldPartitionsAndInvalidEdgesUseTheSharedTraversalSemantics()
    {
        var scene = Scene([1, -1, 99], [3], [], []);
        scene.Nodes[0] = scene.Nodes[0] with { Class = "world", Data = new JsonObject
        {
            ["partitions"] = new JsonArray(new JsonArray(new JsonObject { ["node_indices"] = new JsonArray(2, 2, -1, 99) }))
        } };
        MissionActor first = new(0, 0, "world actor", "source"), second = new(1, 1, "nested", "source");
        var index = MissionActorIndex.Build(scene, [first, second], Token);
        Assert.Same(first, index.At(0)); Assert.Same(first, index.At(2));
        Assert.Null(index.At(1)); Assert.Null(index.At(3));
    }

    [Fact]
    public void RepeatedRootPreparationRetainsAndTraversesOnlyLinearState()
    {
        var small = Repeated(2000); var large = Repeated(4000);
        _ = MissionActorIndex.Build(Scene([]), [], Token); // Warm the preparation machinery outside the measurement.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var a = MissionActorIndex.Build(small.Scene, small.Actors, Token);
        long first = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var b = MissionActorIndex.Build(large.Scene, large.Actors, Token);
        long second = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(second < first * 3 + 16_384, $"Preparation allocated {first:N0} then {second:N0} bytes.");
        Assert.True(second < 512 * 1024, $"Preparation allocated {second:N0} bytes.");
        Assert.Equal(4001, b.NodeVisits); Assert.Equal(4000, b.EdgeVisits);
        Assert.Null(a.At(2000)); Assert.Null(b.At(4000));
    }

    [Fact]
    public void RefusedAndCanceledPreparationLeavesInputsIntactAndCanRetry()
    {
        var fixture = Repeated(8);
        var original = fixture.Scene.Nodes.ToArray();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MissionActorIndex.Build(fixture.Scene, fixture.Actors, canceled.Token));
        Assert.Throws<InvalidDataException>(() => MissionActorIndex.Build(fixture.Scene, fixture.Actors, Token, maximumWork: 1));
        // Admission succeeds, then traversal exhausts the same aggregate allowance rather than publishing a partial index.
        Assert.Throws<InvalidDataException>(() => MissionActorIndex.Build(fixture.Scene, fixture.Actors, Token, maximumWork: 3 * 9 + 8 + 8));
        Assert.Equal(original, fixture.Scene.Nodes);
        var retry = MissionActorIndex.Build(fixture.Scene, fixture.Actors, Token);
        Assert.Null(retry.At(8)); Assert.Equal(9, retry.NodeVisits);
    }

    [Fact]
    public void ActualRepeatedAivRowsPrepareAmbiguousOwnershipBeforeMissionPublication()
    {
        const int count = 128;
        var scene = Scene(Enumerable.Range(1, count).ToArray());
        scene.Nodes[0] = scene.Nodes[0] with { Name = "tank", Parents = [count + 1] };
        for (int i = 1; i <= count; i++) scene.Nodes.Add(new(i, "leaf", "object3d", null, [0], [], new() { ["flags"] = 8 }, new()));
        scene.Nodes.Add(new(count + 1, "world", "world", null, [], [0], new(), new()));
        var world = new ZbdDocument("mission.zbd", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        JsonNode[] rows = Enumerable.Range(0, count).SelectMany(i => new JsonNode[] { Str("tank"), Arr(Number(0), Arr(Number(i), Number(0), Number(0)), Number(0)) }).ToArray();
        var budget = new MissionPlacementBudget(Token);
        var result = MissionSceneLoader.BuildWithBudget(world, budget, null, Arr(rows), Arr(Str("tank"), Arr()), null, token: Token,
            aivSource: new(Path.GetFullPath("placements.zbd"), 0, "aiv.zrd"));
        Assert.Equal(count, result.Actors.Count);
        Assert.Equal(Enumerable.Range(0, count), result.Actors.Select(a => a.CoordinateSource!.RecordIndex));
        Assert.True(budget.Work < MissionPlacementBudget.MaximumWork);
        for (int i = 0; i <= count; i++) Assert.Null(result.ActorAt(i));
        Assert.Equal(8, scene.Nodes[0].Data.Int("flags", 8));
        Assert.False(scene.Nodes[0].Data.ContainsKey("transform"));
    }

    private static (GameScene Scene, MissionActor[] Actors) Repeated(int count)
    {
        var scene = Scene(Enumerable.Range(1, count).ToArray());
        for (int i = 1; i <= count; i++) scene.Nodes.Add(new(i, "leaf", "object3d", null, [0], [], new(), new()));
        return (scene, Enumerable.Range(0, count).Select(i => new MissionActor(0, 0, "actor", "source", CoordinateSource: new("archive", 0, "aiv.zrd", i))).ToArray());
    }
    private static GameScene Scene(params int[][] edges)
    {
        GameScene scene = new();
        for (int i = 0; i < edges.Length; i++) scene.Nodes.Add(new(i, "node", "object3d", null, [], edges[i], new(), new()));
        return scene;
    }
    private static JsonObject Arr(params JsonNode[] children) => new() { ["type"] = "array", ["children"] = new JsonArray(children) };
    private static JsonObject Str(string value) => new() { ["type"] = "string", ["value"] = value };
    private static JsonObject Number(int value) => new() { ["type"] = "int", ["value"] = value };
}
