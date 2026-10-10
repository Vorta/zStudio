using System.Buffers.Binary;
using System.Numerics;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationFrameAncestryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void NativeReaderFrameKeepsExactChildToRootArithmeticAndNearestAlpha()
    {
        var context = Context(4, 3);
        context.Scene.Nodes[1].Data["flags"] = 2;
        context.Scene.Nodes[1].Data["opacity"] = .2f;
        context.Scene.Nodes[3].Data["flags"] = 2;
        context.Scene.Nodes[3].Data["opacity"] = .7f;
        var player = new AnimationPlayer(context, 1, 1, false, Token);
        var frame = player.Frame(Token);
        Assert.Equal(3, frame.Nodes.Count);
        foreach (var pose in frame.Nodes)
        {
            var node = context.Scene.Nodes[pose.SourceNode];
            Matrix4x4 expected = SceneBuilder.LocalTransform(node);
            int parent = node.Parents.FirstOrDefault(-1);
            while (parent >= 0)
            {
                var ancestor = context.Scene.Nodes[parent];
                expected *= SceneBuilder.LocalTransform(ancestor);
                parent = ancestor.Parents.FirstOrDefault(-1);
            }
            Assert.Equal(expected, pose.Transform);
            Assert.Equal(.7f, pose.Opacity);
            Assert.True(pose.Visible);
        }
    }

    [Fact]
    public void WorkIsSharedAcrossLeavesAndRefusalDoesNotReplaceThePreviousFrame()
    {
        var context = Context(6, 8);
        var player = new AnimationPlayer(context, 1, 1, false, Token);
        long used = 0;
        var accepted = player.Frame(Token, LookupWorkBudget.MaximumUnits, value => used = value);
        Assert.InRange(used, 64L * 6 * 8, 100_000);
        var error = Assert.Throws<IOException>(() => player.Frame(Token, used - 1));
        Assert.Contains("Animation binding", error.Message);
        Assert.Equal(0, player.Time);
        Assert.Equal(accepted.Nodes, player.Frame(Token, used).Nodes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void MidFrameCancellationDiscardsItsCacheAndAllowsExactRetry(long cancelAt)
    {
        var context = Context(5, 4);
        var player = new AnimationPlayer(context, 1, 1, false, Token);
        var expected = player.Frame(Token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Assert.ThrowsAny<OperationCanceledException>(() => player.Frame(canceled.Token, LookupWorkBudget.MaximumUnits,
            used => { if (used >= cancelAt) canceled.Cancel(); }));
        Assert.Equal(expected.Nodes, player.Frame(Token).Nodes);
        Assert.Equal(0, player.Time);
    }

    [Fact]
    public void ResetReadsChangedAlphaAndVisibilityInsteadOfKeepingAFrameCache()
    {
        var context = Context(2, 2);
        var player = new AnimationPlayer(context, 1, 1, false, Token);
        Assert.All(player.Frame(Token).Nodes, p => Assert.True(p.Visible));
        context.Scene.Nodes[2].Metadata["flags"] = 0;
        context.Scene.Nodes[2].Data["flags"] = 2;
        context.Scene.Nodes[2].Data["opacity"] = .3f;
        player.Reset(Token);
        Assert.All(player.Frame(Token).Nodes, p => { Assert.False(p.Visible); Assert.Equal(.3f, p.Opacity); });
    }

    [Fact]
    public void ParentCyclesStopAtTheSameFirstRepeatedSource()
    {
        var context = Context(3, 2);
        // Reader-backed nodes, followed by a public preview-only topology mutation.
        context.Scene.Nodes[1] = context.Scene.Nodes[1] with { Parents = [3] };
        var player = new AnimationPlayer(context, 1, 1, false, Token);
        foreach (var pose in player.Frame(Token).Nodes)
        {
            var node = context.Scene.Nodes[pose.SourceNode];
            Matrix4x4 expected = SceneBuilder.LocalTransform(node);
            HashSet<int> seen = [node.Index]; int parent = node.Parents.FirstOrDefault(-1);
            while (parent >= 0 && seen.Add(parent))
            { expected *= SceneBuilder.LocalTransform(context.Scene.Nodes[parent]); parent = context.Scene.Nodes[parent].Parents.FirstOrDefault(-1); }
            Assert.Equal(expected, pose.Transform);
        }
    }

    [Fact]
    public void LodSelectionIsRecomputedAtEachFrameWithoutChangingStoredNodes()
    {
        var context = Context(2, 2);
        context.Scene.Nodes[2] = context.Scene.Nodes[2] with { Class = "lod" };
        context.Scene.Nodes[2].Data["range_near_sq"] = 0;
        context.Scene.Nodes[2].Data["range_far_sq"] = 100;
        context.Scene.Nodes.Add(new(5, "far", "lod", null, [1], [],
            new() { ["flags"] = 4 }, new() { ["range_near_sq"] = 100, ["range_far_sq"] = 10000 }));
        context.Scene.Nodes[1] = context.Scene.Nodes[1] with { Children = [2, 5] };
        var player = new AnimationPlayer(context, 1, 1, false, Token);
        var first = player.Frame(Token);
        Assert.All(first.Nodes, p => Assert.True(p.Visible));
        player.LodLevel = 1;
        Assert.All(player.Frame(Token).Nodes, p => Assert.False(p.Visible));
        player.LodLevel = 0;
        Assert.Equal(first.Nodes, player.Frame(Token).Nodes);
        Assert.Equal(new[] { 2, 5 }, context.Scene.Nodes[1].Children);
    }

    private static AnimationPreviewContext Context(int depth, int leaves)
    {
        byte[] prefix = new byte[72]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), 28);
        AnimationPackage package = new() { Prefix = prefix, Tail = [] };
        for (int i = 0; i < 2; i++)
        {
            AnimationEntry entry = new(new byte[308], i, 72);
            entry.SetText(0, "entry" + i); entry.SetText(32, "root"); entry.SetText(68, "root"); entry.SetFloat(164, -1);
            package.Entries.Add(entry);
        }
        package = AnimationPackage.Read(AnimationWriter.Write(package, Token), Token);
        GameZWorld world = new(); WorldNode top = new("world", WorldNodeClass.World); world.Nodes.Add(top);
        WorldModel model = new(); model.Vertices.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]);
        WorldMaterial material = new(); world.Materials.Add(material);
        model.Polygons.Add(new() { Material = material, Vertices = [0, 1, 2] }); world.Models.Add(model);
        WorldNode parent = top;
        for (int i = 1; i <= depth + leaves; i++)
        {
            WorldNode node = new(i == 1 ? "root" : "n" + i, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried | 4 };
            node.SetPayloadInt(0, 0x20);
            Matrix4x4 local = Matrix4x4.CreateScale(1.01f, .93f, 1.07f)
                * Matrix4x4.CreateRotationY(.137f * i) * Matrix4x4.CreateTranslation(.1f * i, .3f, -.7f);
            float[] words = [local.M11, local.M12, local.M13, local.M21, local.M22, local.M23,
                local.M31, local.M32, local.M33, local.M41, local.M42, local.M43];
            for (int word = 0; word < words.Length; word++) node.SetPayloadFloat(0x30 + 4 * word, words[word]);
            if (i > depth) node.Model = model;
            parent.Children.Add(node); node.Parents.Add(parent); world.Nodes.Add(node);
            if (i <= depth) parent = node;
        }
        var document = FormatRegistry.Default.OpenBytes("world.zbd", GameZWriter.Write(world, Token), token: Token);
        Assert.NotNull(document.Scene);
        return new() { World = document, Package = package };
    }
}
