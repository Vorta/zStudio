using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationEffectCycleBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedRootsReuseSuccessfulAndMissingResults(bool modeled)
    {
        var context = Context(24, modeled);
        Add(context, "first", 0);
        long once = context.BindEffectCycles(Token, LookupWorkBudget.MaximumUnits);
        for (int i = 1; i < 20; i++) Add(context, $"effect{i}", 0);
        // Per additional template only a cache query, and (when modeled) a pending binding, are required.
        // A second full 24-node traversal cannot fit this extra allowance.
        context.BindEffectCycles(Token, once + 19 * 65);
        if (modeled) Assert.Equal("effect19", Assert.Single(context.MaterialCycles).Value.At(0));
        else Assert.Empty(context.MaterialCycles);
        Assert.Equal(25, context.Scene.Nodes.Count);
    }

    [Fact]
    public void DistinctRootsShareOneAllowanceAndRefusalLeavesExistingCyclesForRetry()
    {
        var context = Context(12, true);
        Add(context, "first", 0);
        long single = context.BindEffectCycles(Token, LookupWorkBudget.MaximumUnits);
        var scene = context.Scene;
        for (int i = 0; i < 6; i++)
        {
            int root = scene.Nodes.Count;
            scene.Nodes.Add(Node(root, null, [.. Enumerable.Range(1, 12)]));
            Add(context, $"distinct{i}", root);
        }
        TextureCycle sentinel = new(["saved"], 2, true);
        context.MaterialCycles[0] = sentinel;
        var error = Assert.Throws<InvalidDataException>(() => context.BindEffectCycles(Token, single + 80));
        Assert.Contains("effect material discovery", error.Message);
        Assert.Same(sentinel, Assert.Single(context.MaterialCycles).Value);
        context.BindEffectCycles(Token, LookupWorkBudget.MaximumUnits);
        Assert.Equal("distinct5", context.MaterialCycles[0].At(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void CancellationBeforeOrDuringTraversalDoesNotPublishAndRetrySucceeds(long cancelAt)
    {
        var context = Context(12, true);
        Add(context, "effect", 0);
        TextureCycle sentinel = new(["script"], 1, false);
        context.MaterialCycles[0] = sentinel;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Assert.ThrowsAny<OperationCanceledException>(() => context.BindEffectCycles(cancel.Token,
            LookupWorkBudget.MaximumUnits, used => { if (used >= cancelAt) cancel.Cancel(); }));
        Assert.Same(sentinel, Assert.Single(context.MaterialCycles).Value);
        context.BindEffectCycles(Token, LookupWorkBudget.MaximumUnits);
        Assert.Equal("effect", context.MaterialCycles[0].At(0));
    }

    [Fact]
    public void CachedRootIterationObservesCancellationBeforePublishingAnyEffect()
    {
        var context = Context(4, true);
        Add(context, "first", 0);
        long one = context.BindEffectCycles(Token, LookupWorkBudget.MaximumUnits);
        Add(context, "second", 0);
        TextureCycle sentinel = new(["saved"], 1, false);
        context.MaterialCycles[0] = sentinel;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        // The first call ends with one publication reservation. In the second call that same unit is the
        // second template's cache query; cancellation there must leave the staged first binding unpublished.
        Assert.ThrowsAny<OperationCanceledException>(() => context.BindEffectCycles(cancel.Token,
            LookupWorkBudget.MaximumUnits, used => { if (used >= one) cancel.Cancel(); }));
        Assert.Same(sentinel, Assert.Single(context.MaterialCycles).Value);
        context.BindEffectCycles(Token, LookupWorkBudget.MaximumUnits);
        Assert.Equal("second", context.MaterialCycles[0].At(0));
    }

    [Fact]
    public void DepthFirstFirstModelAndLastEffectOverrideSavedAndScriptCycles()
    {
        var context = Context(3, true);
        // First branch's descendant must beat the root's later directly modeled child.
        context.Scene.Nodes[0] = Node(0, null, [1, 2]);
        context.Scene.Nodes[1] = Node(1, null, [3]);
        context.Scene.Nodes[2] = Node(2, 1, []);
        context.Scene.Models.Add(new(1, [], [], [], [new(1, 0, [], [], [], [])], new()));
        context.MaterialCycles[0] = new(["saved"], 1, false);
        context.ReadTextureScript("setup", new Dictionary<string, ScriptContent>
        {
            ["setup"] = new([["FindNode", "n3"], ["CycleTextureSetOn", "1"], ["CycleTextureSetMap", "script"]], "")
        }, Token);
        Assert.Equal("script", context.MaterialCycles[0].At(0));
        Add(context, "first", 0);
        context.Effects.Add("last", new("last", "n0", 0, ["last0", "last1"], 3, true));
        context.Effects.Add("empty", new("empty", "n0", 0, [], 9, false));
        context.BindEffectCycles(Token, LookupWorkBudget.MaximumUnits);
        var cycle = Assert.Single(context.MaterialCycles).Value;
        Assert.Equal(["last0", "last1"], cycle.Textures);
        Assert.Equal(3, cycle.Speed); Assert.True(cycle.Loop);
        // New invocation cannot reuse a memo after public scene changes.
        context.Scene.Nodes[0] = Node(0, null, [2, 1]);
        context.BindEffectCycles(Token, LookupWorkBudget.MaximumUnits);
        Assert.Equal("last0", context.MaterialCycles[1].At(0));
        Assert.Equal("last0", context.MaterialCycles[0].At(0));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(9, false)]
    [InlineData(1, false)]
    public void FirstNonnegativeModelStopsEvenWhenInvalidOrEmpty(int firstModel, bool findsLater)
    {
        var context = Context(2, true);
        context.Scene.Nodes[1] = Node(1, firstModel, []);
        context.Scene.Models.Add(new(1, [], [], [], [], new()));
        Add(context, "effect", 0);
        Add(context, "sameRoot", 0);
        Add(context, "missing", -1);
        Add(context, "outside", 100);
        context.BindEffectCycles(Token, LookupWorkBudget.MaximumUnits);
        Assert.Equal(findsLater, context.MaterialCycles.ContainsKey(0));
        if (findsLater) Assert.Equal("sameRoot", Assert.Single(context.MaterialCycles).Value.At(0));
        else Assert.Empty(context.MaterialCycles);
    }

    [Fact]
    public void RawPartitionDuplicatesAreChargedBeforeSnapshotAndCyclesTerminate()
    {
        var context = Context(2, true);
        context.Scene.Nodes[0] = Node(0, null, [1, 2]) with { Class = "world" };
        context.Scene.Nodes[1] = Node(1, null, [0]);
        context.Scene.Nodes[0].Data["partitions"] = new JsonArray(new JsonArray(new JsonObject
        { ["node_indices"] = new JsonArray(Enumerable.Repeat(1, 100).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) }));
        Add(context, "effect", 0);
        Assert.Contains("effect material discovery", Assert.Throws<InvalidDataException>(() =>
            context.BindEffectCycles(Token, 200)).Message);
        Assert.Empty(context.MaterialCycles);
        context.BindEffectCycles(Token, LookupWorkBudget.MaximumUnits);
        Assert.Equal("effect", Assert.Single(context.MaterialCycles).Value.At(0));
        Assert.Equal(100, context.Scene.Nodes[0].Data["partitions"]![0]![0]!["node_indices"]!.AsArray().Count);
    }

    [Fact]
    public async Task OrdinaryLoadUsesReaderBackedWorldAndLastSharedMaterialEffect()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zstudio-effect-cycles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            GameZWorld world = new() { NodeCapacity = 8, ModelCapacity = 4, MaterialCapacity = 4 };
            WorldMaterial material = new(); world.Materials.Add(material);
            WorldModel model = new(); model.Vertices.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]);
            model.Polygons.Add(new() { Material = material, Vertices = [0, 1, 2] }); world.Models.Add(model);
            WorldNode root = new("fxroot", WorldNodeClass.Object3D), leaf = new("leaf", WorldNodeClass.Object3D) { Model = model };
            root.Children.Add(leaf); leaf.Parents.Add(root); world.Nodes.AddRange([root, leaf]);
            byte[] bytes = GameZWriter.Write(world, Token);
            string worldPath = Path.Combine(directory, "gamez.zbd"); File.WriteAllBytes(worldPath, bytes);
            byte[] resource = ResourceEditingTests.Archive(("effects.zrd", ZrdWriter.Write(ZrdText.Parse(
                "( ( fxroot NAME ( first ) MAPS ( first ) ) ( fxroot NAME ( last ) MAPS ( last0 last1 ) SPEED ( 2 ) LOOPING ( ON ) ) )", Token), Token)));
            string resourcePath = Path.Combine(directory, "resources.zbd"); File.WriteAllBytes(resourcePath, resource);
            byte[] prefix = new byte[72]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), 28);
            AnimationPackage package = new() { Prefix = prefix, Tail = [] };
            using AssetResolver resolver = new(directory);
            var context = await AnimationPreviewContext.LoadAsync(package, Path.Combine(directory, "anim.zbd"), resolver, worldPath, Token);
            Assert.Equal(2, context.Effects.Count);
            var cycle = Assert.Single(context.MaterialCycles).Value;
            Assert.Equal(["last0", "last1"], cycle.Textures); Assert.Equal(2, cycle.Speed); Assert.True(cycle.Loop);
            Assert.Equal(bytes, File.ReadAllBytes(worldPath)); Assert.Equal(resource, File.ReadAllBytes(resourcePath));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Add(AnimationPreviewContext context, string name, int root) =>
        context.Effects.Add(name, new(name, $"n{root}", root, [name], 1, false));

    private static AnimationPreviewContext Context(int children, bool modeled)
    {
        GameScene scene = new();
        scene.Nodes.Add(Node(0, null, [.. Enumerable.Range(1, children)]));
        for (int i = 1; i <= children; i++) scene.Nodes.Add(Node(i, modeled && i == children ? 0 : null, []));
        scene.Models.Add(new(0, [], [], [], [new(0, 0, [], [], [], [])], new()));
        var world = new ZbdDocument("world.zbd", new(0, DateTime.MinValue),
            new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        return new() { Package = new() { Prefix = [], Tail = [] }, World = world };
    }
    private static GameNode Node(int index, int? model, int[] children) =>
        new(index, $"n{index}", "object3d", model, [], children, new(), new());
}
