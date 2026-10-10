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
            byte[] aliases = ZrdWriter.Write(ZrdText.Parse("( ( alias missing.wav ) ( alias one.wav LOOPED ) )", Token), Token);
            byte[] resource = ResourceEditingTests.Archive(("effects.zrd", ZrdWriter.Write(ZrdText.Parse(
                "( ( fxroot NAME ( first ) MAPS ( first ) ) ( fxroot NAME ( last ) MAPS ( last0 last1 ) SPEED ( 2 ) LOOPING ( ON ) ) )", Token), Token)),
                ("net_01.zrd", ZrdWriter.Write(ZrdText.Parse("( )", Token), Token)), ("sounds.zrd", aliases), ("sounds.zrd", aliases));
            // Two directory members reference the exact same sound-definition payload.
            int table = resource.Length - 8 - 4 * 148;
            resource.AsSpan(table + 2 * 148, 8).CopyTo(resource.AsSpan(table + 3 * 148, 8));
            string resourcePath = Path.Combine(directory, "resources.zbd"); File.WriteAllBytes(resourcePath, resource);
            byte[] sounds = ResourceEditingTests.Archive(("one.wav", SourceFixture.Tone(new(8000, 8, 1), 2, null)));
            string soundPath = Path.Combine(directory, "sounds.zbd"); File.WriteAllBytes(soundPath, sounds);
            var scriptPackage = FormatRegistry.Default.OpenBytes("scripts.zbd", ContentFixture.Scripts(), token: Token).Scripts!;
            var scriptEntry = scriptPackage.Entries[0] with { Name = "unused.gw", Instructions =
                [new(Guid.NewGuid(), [new string('x', 1024)], ReadOnlyMemory<byte>.Empty, null)] };
            byte[] scripts = PreparedScriptWriter.Write(scriptPackage with { Entries = [scriptEntry] }, Token);
            string scriptPath = Path.Combine(directory, "scripts.zbd"); File.WriteAllBytes(scriptPath, scripts);
            byte[] prefix = new byte[72]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), 28);
            AnimationPackage package = new() { Prefix = prefix, Tail = [] };
            using AssetResolver resolver = new(directory);
            long totalBytes = bytes.Length + resource.Length + sounds.Length + scripts.Length;
            var allowance = new PreviewResourceBudget(totalBytes);
            var context = await AnimationPreviewContext.LoadWithResourcesAsync(package, Path.Combine(directory, "anim.zbd"), resolver, allowance, worldPath, Token);
            Assert.Equal(2, context.Effects.Count);
            var cycle = Assert.Single(context.MaterialCycles).Value;
            Assert.Equal(["last0", "last1"], cycle.Textures); Assert.Equal(2, cycle.Speed); Assert.True(cycle.Loop);
            Assert.Equal("one.wav", context.Sounds["alias"].FileName); Assert.True(context.Sounds["alias"].Loop);
            Assert.Equal(2, context.Sounds.Count); // Raw filename plus the first alias whose WAV actually exists.
            Assert.Equal("net_01.zrd", Assert.Single(context.Mission!.AiNetworks.Networks).Member);
            // The same raw archives feed mission and audio phases. Exact admission succeeds without
            // counting/reopening them twice; neither warm snapshots nor a cached mission bypass refusal.
            var published = new[] { worldPath, resourcePath, soundPath, scriptPath }.Select(path =>
                FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token)).ToArray();
            resolver.SetWorkspaceSnapshots(Guid.NewGuid(), published);
            var rawRefusal = new PreviewResourceBudget(bytes.Length + resource.Length + sounds.Length - 1);
            await Assert.ThrowsAsync<InvalidDataException>(() => AnimationPreviewContext.LoadWithResourcesAsync(package,
                Path.Combine(directory, "anim.zbd"), resolver, rawRefusal, worldPath, Token));
            Assert.True(rawRefusal.Exhausted);
            // Tiny documents fit individually, but owned asset and document metadata must share admission.
            var first = new ZbdDocument("first.zbd", new(0, DateTime.MinValue),
                new(FormatFamily.Archive, null, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty);
            var second = new ZbdDocument("second.zbd", first.Stamp, first.Probe, ReadOnlyMemory<byte>.Empty);
            first.Add(AssetKind.Raw, 0, "metadata", 0, 0).Metadata["rows"] =
                new JsonArray(Enumerable.Range(0, 32).Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
            second.Metadata["rows"] = new JsonArray(Enumerable.Range(0, 32).Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
            new PreviewResourceBudget(0, 6 * 1024).Retain(second, Token);
            var metadataRefusal = new PreviewResourceBudget(0, 6 * 1024);
            metadataRefusal.Retain(first, Token);
            Assert.Throws<InvalidDataException>(() => metadataRefusal.Retain(second, Token));
            Assert.True(metadataRefusal.Exhausted);
            // An intact entry remains retained when a later malformed entry prevents a complete package.
            var longEntry = scriptEntry with { Instructions =
                [new(Guid.NewGuid(), [new string('x', 4096)], ReadOnlyMemory<byte>.Empty, null)] };
            byte[] partialBytes = PreparedScriptWriter.Write(scriptPackage with { Entries =
                [longEntry, scriptEntry with { Name = "broken.gw" }] }, Token);
            int brokenOffset = BinaryPrimitives.ReadInt32LittleEndian(partialBytes.AsSpan(12 + 128 + 124));
            BinaryPrimitives.WriteUInt32LittleEndian(partialBytes.AsSpan(brokenOffset + 4), uint.MaxValue);
            var partial = FormatRegistry.Default.OpenBytes("partial.zbd", partialBytes, token: Token);
            Assert.Null(partial.Scripts); Assert.IsType<ScriptContent>(Assert.Single(partial.Assets).Content);
            var scriptRefusal = new PreviewResourceBudget(partialBytes.Length, 12 * 1024);
            Assert.Throws<InvalidDataException>(() => scriptRefusal.Retain(partial, Token));
            Assert.True(scriptRefusal.Exhausted);
            var retry = await AnimationPreviewContext.LoadWithResourcesAsync(package, Path.Combine(directory, "anim.zbd"), resolver,
                new PreviewResourceBudget(totalBytes), worldPath, Token);
            Assert.Equal(2, retry.Effects.Count); Assert.Equal(2, retry.Sounds.Count);
            Assert.Equal("one.wav", retry.Sounds["alias"].FileName); Assert.True(retry.Sounds["alias"].Loop);
            Assert.Equal(bytes, File.ReadAllBytes(worldPath)); Assert.Equal(resource, File.ReadAllBytes(resourcePath));
            Assert.Equal(sounds, File.ReadAllBytes(soundPath)); Assert.Equal(scripts, File.ReadAllBytes(scriptPath));
            // Selected layout resources project JSON even when their rows have no known actor. Their
            // retained typed graphs fit this allowance, but the four additional projections do not.
            byte[] ignored = ZrdWriter.Write(ZrdText.Parse("( ignored ( " + string.Join(' ', Enumerable.Repeat("1", 256)) + " ) )", Token), Token);
            List<ZbdDocument> selected = [];
            foreach (string name in new[] { "aiv", "vehicle", "startanims", "ai" })
            {
                string path = Path.Combine(directory, name + ".zbd");
                byte[] content = ResourceEditingTests.Archive((name + ".zrd", ignored));
                File.WriteAllBytes(path, content);
                selected.Add(FormatRegistry.Default.OpenBytes(path, content, token: Token));
            }
            resolver.SetWorkspaceSnapshots(Guid.NewGuid(), selected);
            var projectionRefusal = new PreviewResourceBudget(maximumDecodedBytes: 512 * 1024);
            foreach (var document in published.Concat(selected)) projectionRefusal.Retain(document, Token);
            var selectedWorld = published.Single(d => d.Path == worldPath);
            Assert.Contains("decoded-content", (await Assert.ThrowsAsync<InvalidDataException>(() =>
                MissionSceneLoader.LoadWithResourcesAsync(selectedWorld, resolver, projectionRefusal, package, Token))).Message);
            Assert.True(projectionRefusal.Exhausted);
            var selectedRetry = await MissionSceneLoader.LoadAsync(selectedWorld, resolver, package, Token);
            Assert.Empty(selectedRetry.Actors);
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
