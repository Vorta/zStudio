using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationTextureLookupWorkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedFailedAndSuccessfulFullSubtreeSearchesReuseTraversalStorage(bool found)
    {
        var context = Context(4000);
        string target = found ? "n1" : "missing";
        List<string[]> commands = [];
        for (int i = 0; i < 1000; i++) { commands.Add(["FindNode", "world"]); commands.Add(["FindSubNode", target]); }
        commands.AddRange([["CycleTextureSetOn", "1"], ["CycleTextureSetMap", "frame"]]);
        var scripts = Scripts(commands);
        long before = GC.GetAllocatedBytesForCurrentThread();
        context.ReadTextureScript("mission", scripts, Token);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 8 * 1024 * 1024);
        Assert.Empty(context.Diagnostics);
        Assert.Equal(found, context.MaterialCycles.ContainsKey(0));
        if (found) Assert.Equal("frame", context.MaterialCycles[0].At(0));
    }

    [Fact]
    public void SourcedLookupsShareTheWorkLimitAndOnlyCompletedCyclesSurvive()
    {
        var context = Context(40);
        Dictionary<string, ScriptContent> scripts = new()
        {
            ["mission"] = new([["FindNode", "n1"], ["CycleTextureSetOn", "1"], ["CycleTextureSetMap", "before"],
                .. Enumerable.Repeat(new[] { "source", "repeat" }, 100), ["FindNode", "n1"], ["CycleTextureSetMap", "after"]], ""),
            ["repeat"] = new([["FindNode", "world"], ["FindSubNode", "missing"]], ""),
        };
        context.ReadTextureScript("mission", scripts, Token, maximumLookupWork: 10_000);
        Assert.Contains("node lookups exceed", Assert.Single(context.Diagnostics));
        Assert.Equal("before", context.MaterialCycles[0].At(0));
        context.ReadTextureScript("mission", Scripts([["FindNode", "n1"], ["CycleTextureSetOn", "1"], ["CycleTextureSetMap", "retry"]]), Token);
        Assert.Equal("retry", context.MaterialCycles[0].At(0));
    }

    [Fact]
    public void TraversalRetainsLastChildFirstOrderAndBoundsRawPartitionDuplicates()
    {
        var context = Context(3);
        var scene = context.Scene;
        scene.Nodes[1] = scene.Nodes[1] with { Name = "same" };
        scene.Nodes[3] = scene.Nodes[3] with { Name = "same", ModelIndex = 1 };
        scene.Models.Add(new(1, [], [], [], [new(1, 0, [], [], [], [])], new()));
        scene.Nodes[0].Data["partitions"] = new JsonArray(new JsonArray(new JsonObject { ["node_indices"] = new JsonArray(1, 3, 1, 3) }));
        context.ReadTextureScript("mission", Scripts([["FindNode", "world"], ["FindSubNode", "same"], ["CycleTextureSetOn", "1"], ["CycleTextureSetMap", "ordered"]]), Token);
        Assert.Equal([1], context.MaterialCycles.Keys);
        Assert.Equal("ordered", context.MaterialCycles[1].At(0));

        scene.Nodes[0].Data["partitions"] = new JsonArray(new JsonArray(new JsonObject
        { ["node_indices"] = new JsonArray(Enumerable.Repeat(1, 20_000).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) }));
        context.ReadTextureScript("mission", Scripts([["FindNode", "world"], ["FindSubNode", "missing"]]), Token, maximumLookupWork: 1000);
        Assert.Contains("node lookups exceed", Assert.Single(context.Diagnostics));
    }

    [Fact]
    public void CancellationDuringRawChildEnumerationPropagatesAndNextReadSucceeds()
    {
        // Enumeration is lazy: cancellation after one accepted child must be checked before any further raw edge.
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var context = Context(40);
        using var children = SceneBuilder.Children(context.Scene.Nodes[0], new LookupWorkBudget(token: cancel.Token)).GetEnumerator();
        Assert.True(children.MoveNext());
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => children.MoveNext());
        Assert.ThrowsAny<OperationCanceledException>(() => context.ReadTextureScript("mission", Scripts([["FindNode", "world"]]), cancel.Token));
        context.ReadTextureScript("mission", Scripts([["FindNode", "n1"], ["CycleTextureSetOn", "1"], ["CycleTextureSetMap", "retry"]]), Token);
        Assert.Equal("retry", context.MaterialCycles[0].At(0));
    }

    [Fact]
    public void LongGlobalLookupOperandsAreChargedBeforeRepeatedHashing()
    {
        var context = Context(1);
        var scripts = Scripts([["FindNode", new string('x', 1_000_000)]]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        context.ReadTextureScript("mission", scripts, Token, maximumLookupWork: 1000);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 20_000);
        Assert.Contains("node lookups exceed", Assert.Single(context.Diagnostics));
    }

    private static Dictionary<string, ScriptContent> Scripts(List<string[]> commands) => new() { ["mission"] = new(commands, "") };
    private static AnimationPreviewContext Context(int children)
    {
        GameScene scene = new();
        scene.Nodes.Add(new(0, "world", "world", null, [], Enumerable.Range(1, children).ToArray(), new(), new()));
        for (int i = 1; i <= children; i++) scene.Nodes.Add(new(i, $"n{i}", "object3d", 0, [0], [], new(), new()));
        scene.Models.Add(new(0, [], [], [], [new(0, 0, [], [], [], [])], new()));
        var world = new ZbdDocument("world.zbd", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        return new() { Package = new() { Prefix = [], Tail = [] }, World = world };
    }
}
