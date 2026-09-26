using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldSurfaceHighlightTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(5u, true)]
    [InlineData(uint.MaxValue, true)]
    public void SoilUsesTheMaterialValueIncludingUnknownNonzeroTypes(uint soil, bool expected)
    {
        var scene = Fixture(); scene.Materials[0]["soil"] = soil;
        string before = scene.Materials[0].ToJsonString();
        Assert.Equal(expected, WorldSurfaceHighlights.Matches(WorldSurfaceHighlights.Classify(scene, 1, 0), WorldHighlightMode.NonDefaultSoils));
        Assert.Equal(before, scene.Materials[0].ToJsonString());
    }

    [Theory]
    [InlineData(0x00000004u, false, false)]
    [InlineData(0x00010004u, true, false)]
    [InlineData(0x00020004u, false, true)]
    [InlineData(0x00030004u, true, true)]
    [InlineData(0xFFFCFFFFu, false, false)]
    public void FlagsAreIndependentAndIgnoreUnrelatedBits(uint flags, bool modify, bool clip)
    {
        var scene = Fixture(); scene.Nodes[1].Metadata["flags"] = flags;
        var kind = WorldSurfaceHighlights.Classify(scene, 1, 0);
        Assert.Equal(modify, WorldSurfaceHighlights.Matches(kind, WorldHighlightMode.CanModify));
        Assert.Equal(clip, WorldSurfaceHighlights.Matches(kind, WorldHighlightMode.ClipTo));
        Assert.False(WorldSurfaceHighlights.Matches(kind, WorldHighlightMode.None));
        Assert.Equal(flags, scene.Nodes[1].Metadata.UInt("flags"));
    }

    [Fact]
    public void SharedModelsAndNamesDoNotShareNodeFlagsOrInheritParentFlags()
    {
        var scene = Fixture();
        Assert.Equal(scene.Nodes[0].Name, scene.Nodes[1].Name);
        Assert.Equal(scene.Nodes[0].ModelIndex, scene.Nodes[1].ModelIndex);
        Assert.Equal(WorldSurfaceKind.CanModify | WorldSurfaceKind.ClipTo, WorldSurfaceHighlights.NodeKind(scene, 0));
        Assert.Equal(WorldSurfaceKind.Default, WorldSurfaceHighlights.NodeKind(scene, 1));
    }

    [Fact]
    public void MixedMaterialsAndUnavailableReferencesClassifyIndependently()
    {
        var scene = Fixture(); scene.Materials.Add(new() { ["soil"] = 3 }); scene.Materials.Add(new());
        Assert.False(WorldSurfaceHighlights.Matches(WorldSurfaceHighlights.Classify(scene, 1, 0), WorldHighlightMode.NonDefaultSoils));
        Assert.True(WorldSurfaceHighlights.Matches(WorldSurfaceHighlights.Classify(scene, 1, 1), WorldHighlightMode.NonDefaultSoils));
        foreach (int material in new[] { -1, 2, 3, int.MaxValue })
            Assert.Equal(WorldSurfaceKind.Default, WorldSurfaceHighlights.Classify(scene, 1, material));
        Assert.Equal(WorldSurfaceKind.Default, WorldSurfaceHighlights.NodeKind(scene, -1));
        Assert.Equal(WorldSurfaceKind.Default, WorldSurfaceHighlights.NodeKind(scene, int.MaxValue));
        // A missing material must not discard valid node flags.
        Assert.True(WorldSurfaceHighlights.Matches(WorldSurfaceHighlights.Classify(scene, 0, -1), WorldHighlightMode.CanModify));
    }

    private static GameScene Fixture()
    {
        GameScene scene = new(); scene.Materials.Add(new JsonObject { ["soil"] = 0 });
        scene.Nodes.Add(new(0, "duplicate", "object3d", 0, [], [1], new() { ["flags"] = 0x30004 }, new()));
        scene.Nodes.Add(new(1, "duplicate", "object3d", 0, [0], [], new() { ["flags"] = 4 }, new()));
        return scene;
    }
}
