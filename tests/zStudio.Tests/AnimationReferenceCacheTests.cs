using System.Buffers.Binary;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationReferenceCacheTests
{
    [Theory]
    [InlineData(15)]
    [InlineData(27)]
    public void RepeatedNodeReferencesReuseOneResolutionUntilTheSceneChanges(int version)
    {
        // A deep chain: each uncached lookup traverses the whole bound subtree and, for MW3, the global node list too.
        var world = World(version, 20_000, 19_999);
        var entry = new AnimationEntry(new byte[308], 0, 0); entry.SetText(0, "a"); entry.SetText(32, "n0");
        entry.References[1].Add(new(new byte[40])); var target = new AnimationRecord(new byte[40]); target.SetText(0, "target", 36); entry.References[1].Add(target);
        var package = new AnimationPackage { Prefix = [], Tail = [] }; package.Entries.Add(entry);
        var context = new AnimationPreviewContext { Package = package, World = world };
        Assert.Equal(19_999, context.ResolveNode(entry, 1)); Assert.Equal(19_999, context.ResolveInstanceNode(entry, 1, 0));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { context.ResolveNode(entry, 1); context.ResolveInstanceNode(entry, 1, 0); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64_000, $"Allocated {allocated:N0} bytes");
        // Edits replace entry objects, so a retargeted reference is a separate resolution.
        var retargeted = entry.Clone(TestContext.Current.CancellationToken); retargeted.References[1][1].SetText(0, "n5", 36);
        Assert.Equal(5, context.ResolveNode(retargeted, 1)); Assert.Equal(19_999, context.ResolveNode(entry, 1));
        if (version != 15) return;
        // Replacing the mission replaces the scene, so roots and references resolve against it.
        var moved = World(version, 10, 3);
        context.Mission = MissionSceneLoader.Build(moved, null, null, null, null, token: TestContext.Current.CancellationToken);
        Assert.Equal(0, context.ResolveRoot(entry)); Assert.Equal(3, context.ResolveNode(entry, 1));
    }
    /// <summary>A world whose nodes form one parent chain from n0, with <c>target</c> at <paramref name="target"/>.</summary>
    private static ZbdDocument World(int version, int count, int target)
    {
        byte[] header = new byte[36]; BinaryPrimitives.WriteUInt32LittleEndian(header, 0x02971222); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)version);
        GameScene scene = new();
        for (int i = 0; i < count; i++)
            scene.Nodes.Add(new(i, i == target ? "target" : "n" + i, "object3d", null, i == 0 ? [] : [i - 1], i == count - 1 ? [] : [i + 1], new(), new()));
        return new("gamez.zbd", new(header.Length, DateTime.MinValue), new(FormatFamily.GameZ, (uint)version, Recognition.Supported, "fixture"), header) { Scene = scene };
    }
}
