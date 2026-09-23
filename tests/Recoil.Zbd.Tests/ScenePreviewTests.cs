using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ScenePreviewTests
{
    [Fact]
    public void LodRanksAreLocalToEachObjectAndClampToAvailableVariants()
    {
        var scene = LodScene();
        Assert.Equal([3, 8], SceneBuilder.Assemble(scene, token: TestContext.Current.CancellationToken).Placements.Select(p => p.NodeIndex));
        Assert.Equal([4, 9], SceneBuilder.Assemble(scene, 1, token: TestContext.Current.CancellationToken).Placements.Select(p => p.NodeIndex));
        Assert.Equal([4, 9], SceneBuilder.Assemble(scene, 20, token: TestContext.Current.CancellationToken).Placements.Select(p => p.NodeIndex));
        Assert.Equal(2, new SceneLods(scene).Count());
        scene.Nodes[1].Data["range_far_sq"] = 200; // Authored crossfade overlaps the next band.
        Assert.Equal([4, 9], SceneBuilder.Assemble(scene, 1, token: TestContext.Current.CancellationToken).Placements.Select(p => p.NodeIndex));
    }
    [Fact]
    public void ModelPreviewOffersSiblingLodsButKeepsUnrelatedModelsSeparate()
    {
        var scene = LodScene();
        var asset = new AssetRecord { Id = new("fixture", AssetKind.Model, 0), Name = "high", Content = scene.Models[0] };
        Assert.Equal(0, SceneLods.PreviewRoot(scene, asset));
        Assert.Equal([3], SceneBuilder.ForAsset(scene, asset, token: TestContext.Current.CancellationToken).Placements.Select(p => p.NodeIndex));
        Assert.Equal([4], SceneBuilder.ForAsset(scene, asset, 1, TestContext.Current.CancellationToken).Placements.Select(p => p.NodeIndex));
    }
    [Fact]
    public void AnimationLodChangesOnlyVisibilityAndSurvivesSeeking()
    {
        var context = Context(LodScene()); var player = new AnimationPlayer(context, 0);
        Assert.Equal([3], player.Frame().Nodes.Where(n => n.Visible).Select(n => n.SourceNode));
        player.LodLevel = 1;
        Assert.Equal([4], player.AdvanceTo(1, true, TestContext.Current.CancellationToken).Nodes.Where(n => n.Visible).Select(n => n.SourceNode));
        Assert.Equal([4], player.AdvanceTo(0, true, TestContext.Current.CancellationToken).Nodes.Where(n => n.Visible).Select(n => n.SourceNode));
        Assert.Equal(2, player.Frame().Nodes.Count); // Inactive variants retain their simulation state.
        player.LodLevel = 0;
        Assert.Equal([3], player.Frame().Nodes.Where(n => n.Visible).Select(n => n.SourceNode));
    }
    [Fact]
    public void TextureCyclesLoopClampReverseAndRespectVariantResets()
    {
        var cycle = new TextureCycle(["a", "b", "c"], 10, true);
        Assert.Equal("a", cycle.At(0)); Assert.Equal("b", cycle.At(.1)); Assert.Equal("a", cycle.At(.3));
        Assert.Equal("b", cycle.At(123.4)); Assert.Equal("b", cycle.At(.1));
        Assert.Equal("c", cycle.At(.1, 4));
        Assert.Equal("c", (cycle with { Loop = false }).At(100));
        Assert.Equal("c", (cycle with { Speed = -10 }).At(.1));
        Assert.Equal("b", (cycle with { Speed = 0 }).At(100, 1));
    }
    [Fact]
    public void ScriptCyclesFollowIncludesAndScopedNodesWithoutMutatingMaterials()
    {
        var scene = LodScene(); scene.Materials.Add(new JsonObject { ["alpha"] = 255 });
        scene.Models[0] = scene.Models[0] with { Polygons = [new(0, 0, [], [], [], [])] };
        var context = Context(scene);
        var scripts = new Dictionary<string, ScriptContent> {
            ["mission"] = new([["source", "common"], ["CycleTextureSetSpeed", "12"], ["quit"], ["CycleTextureSetSpeed", "99"]], ""),
            ["common"] = new([["FindNode", "first"], ["FindSubNode", "highA"], ["CycleTextureSetOn", "2"], ["CycleTextureSetLooping", "on"], ["CycleTextureSetMap", "a"], ["CycleTextureSetMap", "b"], ["source", "mission"]], "")
        };
        string before = scene.Materials[0].ToJsonString(); context.ReadTextureScript("mission", scripts);
        Assert.Equal("b", context.MaterialCycles[0].At(1.0 / 12)); Assert.Equal(12, context.MaterialCycles[0].Speed);
        Assert.Equal(before, scene.Materials[0].ToJsonString());
        Assert.Same(context.MaterialCycles[0], context.Snapshot().MaterialCycles[0]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransparentColorKeyUsesDecodedColorNotPaletteIndex(bool palette)
    {
        // Palette index zero is red; index one is the transparent zero color.
        byte[] bytes = palette ? [0, 1, 0, 248, 0, 0] : [0, 248, 0, 0];
        var doc = new ZbdDocument("key", new(bytes.Length, DateTime.MinValue), new(FormatFamily.TexturePack, null, Recognition.Supported, ""), bytes);
        var asset = doc.Add(AssetKind.Texture, 0, "key", 0, bytes.Length, content: new TextureInfo(2, 1, 3, palette ? 2 : 0, -1, 0, palette ? 2 : 4, -1, palette ? 2 : -1, palette ? 4 : 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 0, 0 }, TextureDecoder.Decode(doc, asset, TestContext.Current.CancellationToken).Rgba);
        asset.Content = ((TextureInfo)asset.Content!) with { Flags = 1 };
        Assert.Equal(255, TextureDecoder.Decode(doc, asset, TestContext.Current.CancellationToken).Rgba[7]);
    }
    private static AnimationPreviewContext Context(GameScene scene)
    {
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        var entry = new AnimationEntry(new byte[308], 0, 72); entry.SetText(32, "first"); entry.SetFloat(164, -1); package.Entries.Add(entry);
        return new() { Package = package, World = new("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, ""), ReadOnlyMemory<byte>.Empty) { Scene = scene } };
    }
    private static GameScene LodScene()
    {
        var scene = new GameScene();
        for (int i = 0; i < 4; i++) scene.Models.Add(new(i, [Vector3.Zero], [], [], [], []));
        scene.Nodes.AddRange([
            Node(0, "first", "object3d", null, [10], [1, 2]),
            Node(1, "nearA", "lod", null, [0], [3], 0, 100), Node(2, "farA", "lod", null, [0], [4], 100, 10000),
            Node(3, "highA", "object3d", 0, [1], []), Node(4, "lowA", "object3d", 1, [2], []),
            Node(5, "second", "object3d", null, [10], [6, 7]),
            Node(6, "nearB", "lod", null, [5], [8], 0, 900), Node(7, "farB", "lod", null, [5], [9], 900, 10000),
            Node(8, "highB", "object3d", 2, [6], []), Node(9, "lowB", "object3d", 3, [7], []),
            Node(10, "world", "world", null, [], [0, 5])]);
        return scene;
    }
    private static GameNode Node(int i, string name, string kind, int? model, int[] parents, int[] children, float near = 0, float far = 1) =>
        new(i, name, kind, model, parents, children, new JsonObject { ["flags"] = 4 },
            new JsonObject { ["flags"] = 8, ["range_near_sq"] = near, ["range_far_sq"] = far });
}
