using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationTextureLoadScopeTests
{
    [Fact]
    public void TextureSubnodeLookupIgnoresLaterMissionClonesAndKeepsRuntimeLookupOrder()
    {
        GameScene scene = new();
        // The later loaded gun uses material 1. A mission vehicle cloned from the earlier tank is then appended to
        // the world; its gun shares the earlier template's material 0. Texture setup ran before that clone existed.
        scene.Nodes.AddRange([Node(0, "world", null, [], [1, 3, 4]), Node(1, "tank", 0, [0], [2]),
            Node(2, "gun", 0, [1], []), Node(3, "gun", 1, [0], []), Node(4, "tank_01", 0, [0], [5]), Node(5, "gun", 0, [4], [])]);
        scene.Models.Add(new(0, [], [], [], [new(0, 0, [], [], [], [])], []));
        scene.Models.Add(new(1, [], [], [], [new(MaterialIndex: 1, Flags: 0, [], [], [], [])], []));
        Assert.Equal(0, scene.Models[0].Polygons[0].MaterialIndex);
        Assert.Equal(1, scene.Models[1].Polygons[0].MaterialIndex);
        var world = new ZbdDocument("world.zbd", new(0, DateTime.MinValue),
            new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = world, LoadedNodeCount = 4 };

        context.ReadTextureScript("mission", new Dictionary<string, ScriptContent>
        {
            ["mission"] = new([["FindNode", "world"], ["FindSubNode", "gun"], ["CycleTextureSetOn", "1"], ["CycleTextureSetMap", "cycle"]], "")
        }, TestContext.Current.CancellationToken);

        Assert.Equal([1], context.MaterialCycles.Keys);
        Assert.Equal("cycle", context.MaterialCycles[1].At(0));
        Assert.Equal(5, context.FindSubBelow(0, "gun")); // Runtime search still includes the newly placed vehicle.
        Assert.Equal([1, 3, 4], scene.Nodes[0].Children);
    }

    private static GameNode Node(int index, string name, int? model, int[] parents, int[] children) =>
        new(index, name, model.HasValue ? "object3d" : "world", model, parents, children, new(), new());
}
