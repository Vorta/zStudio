using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationBindingWorkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ReaderBackedRepeatedReferencesReuseTheSameColdSubtreeAndCallback()
    {
        var context = Context(24, 1, 40); var entry = context.Package.Entries[1];
        AnimationBindingOperation work = new(context, Token);
        Assert.Equal(1, context.ResolveInstanceNode(entry, 1, 1, AnimationBinding.Loaded, work));
        long first = work.Used;
        for (int i = 2; i < entry.References[1].Count; i++)
            Assert.Equal(1, context.ResolveInstanceNode(entry, i, 1, AnimationBinding.Loaded, work));
        Assert.InRange(work.Used - first, 1, 1000); // No second24-node traversal fits; name queries remain charged.
        Assert.Equal(40, entry.References[1].Count - 1);
    }

    [Fact]
    public void WholeInitializationSharesWorkEvenWhenItsLiveInstancesAreCleared()
    {
        var one = Context(6, 3, 12); AnimationBindingOperation measured = new(one, Token);
        AnimationPlayer.ApplyInitialization(one, [(one.Package.Entries[1], true, null)], [], Token, measured);
        var all = Context(6, 3, 12);
        var refusal = Assert.Throws<IOException>(() => AnimationPlayer.ApplyInitialization(all,
            all.Package.Entries.Skip(1).Select(entry => (entry, true, (int?)null)), [], Token,
            new(all, Token, measured.Used + 100)));
        Assert.Contains("Animation binding", refusal.Message);
        var retry = Context(6, 3, 12);
        Assert.Empty(AnimationPlayer.ApplyInitialization(retry, true, [], [], Token));
        _ = new AnimationPlayer(retry, 1, 1, false, Token).Frame(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void FailedMissionPreparationCannotPublishOrChangeTheOriginalScene()
    {
        var context = Context(8, 2, 8);
        string[] original = context.Scene.Nodes.Select(n => n.Metadata.ToJsonString() + n.Data.ToJsonString()).ToArray();
        Assert.Throws<IOException>(() => MissionSceneLoader.BuildWithBudget(context.World, new(Token), context.Package,
            null, null, null, token: Token, maximumBindingWork: 100));
        Assert.Equal(original, context.Scene.Nodes.Select(n => n.Metadata.ToJsonString() + n.Data.ToJsonString()));
        Assert.NotNull(MissionSceneLoader.Build(context.World, context.Package, null, null, null, token: Token));
        Assert.Equal(original, context.Scene.Nodes.Select(n => n.Metadata.ToJsonString() + n.Data.ToJsonString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DormantPlacementSharesWorkForEntriesThatSkipInitialization(bool skippedState)
    {
        var context = Context(20, 4, 8);
        foreach (var entry in context.Package.Entries)
        {
            entry.SetInt(148, 0); entry.Bytes[152] = skippedState ? (byte)5 : (byte)0;
            var motion = AnimationCatalog.Create(7); motion.SetShort(28, 1); motion.SetVector(16, new(12, 0, 0));
            AnimationSequence sequence = new(new byte[64]) { Name = "later" }; sequence.Events.Add(motion); entry.Sequences.Add(sequence);
        }
        byte[] before = AnimationWriter.Write(context.Package, Token);
        var package = AnimationPackage.Read(before, Token);
        var refusal = Assert.Throws<IOException>(() => MissionSceneLoader.BuildWithBudget(context.World, new(Token), package,
            null, null, null, token: Token, maximumBindingWork: 2000));
        Assert.Contains("Animation binding", refusal.Message);
        Assert.Equal(before, AnimationWriter.Write(package, Token));
        Assert.Null(context.Scene.Nodes[1].Metadata["preview_pending_placement"]);
        var retry = MissionSceneLoader.Build(context.World, package, null, null, null, token: Token);
        Assert.Equal(1, Assert.Single(retry.DormantRoots));
        Assert.Empty(retry.Actors);
        Assert.True(retry.Scene.Nodes[1].Metadata["preview_pending_placement"]!.GetValue<bool>());
        Assert.Null(context.Scene.Nodes[1].Metadata["preview_pending_placement"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void CancellationDuringColdTraversalOrBeforeEntryPermitsFreshRetry(long cancelAt)
    {
        var context = Context(20, 1, 20); var entry = context.Package.Entries[1];
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        AnimationBindingOperation work = new(context, cancel.Token, reserved: used => { if (used >= cancelAt) cancel.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => context.ResolveInstanceNode(entry, 1, 1, AnimationBinding.Loaded, work));
        Assert.Equal(1, context.ResolveInstanceNode(entry, 1, 1, AnimationBinding.Loaded, new(context, Token)));
        Assert.ThrowsAny<OperationCanceledException>(() => new AnimationPlayer(context, 1, 1, false, cancel.Token));
    }

    [Fact]
    public void DfsOrderCallbackPrecedenceAndOwnedNodesRemainDistinct()
    {
        var context = Context(4, 1, 1); var entry = context.Package.Entries[1];
        var scene = context.Scene;
        scene.Nodes[1] = scene.Nodes[1] with { Children = [2, 3] };
        scene.Nodes[2] = scene.Nodes[2] with { Name = "branch", Children = [4] };
        scene.Nodes[3] = scene.Nodes[3] with { Name = "target", Children = [] };
        scene.Nodes[4] = scene.Nodes[4] with { Name = "target", Children = [] };
        AnimationBindingOperation operation = new(context, Token);
        Assert.Equal(4, operation.First(1, "target", scene.Nodes.Count));
        entry.SetText(68, "target");
        Assert.Equal(4, operation.Resolve(entry, "target", 1, AnimationBinding.Loaded));
        // An attachment outside the root is searched before the root, even for chosen-root aliases.
        scene.Nodes.Add(Node(5, "outside", []));
        scene.Nodes.Add(Node(6, "root", []));
        scene.Nodes[5] = Node(5, "outside", [6]); entry.SetText(68, "outside");
        Assert.Equal(6, new AnimationBindingOperation(context, Token).Resolve(entry, "root", 1, AnimationBinding.Chosen));
        entry.References[2].Add(new(new byte[40])); var owned = new AnimationRecord(new byte[40]); owned.SetText(0, "onlyLight", 36); entry.References[2].Add(owned);
        scene.Nodes.Add(Node(7, "onlyLight", []));
        Assert.Equal(-1, new AnimationBindingOperation(context, Token).Resolve(entry, "onlyLight", 1, AnimationBinding.Chosen));
    }

    [Fact]
    public void LoadedAndCurrentDomainsAndRebindDisableRulesArePreserved()
    {
        var original = Context(2, 1, 1);
        var context = new AnimationPreviewContext { World = original.World, Package = original.Package, LoadedNodeCount = original.Scene.Nodes.Count };
        var entry = context.Package.Entries[1];
        context.Scene.Nodes.Add(Node(3, "late", []));
        context.Scene.Nodes[1] = context.Scene.Nodes[1] with { Children = [2, 3] };
        AnimationBindingOperation operation = new(context, Token);
        Assert.Equal(-1, operation.Resolve(entry, "late", 1, AnimationBinding.Loaded));
        Assert.Equal(-1, operation.Resolve(entry, "late", 1, AnimationBinding.Copied));
        Assert.Equal(3, operation.Resolve(entry, "late", 1, AnimationBinding.Rebound));
        Assert.Equal(3, operation.Resolve(entry, "late", 1, AnimationBinding.Chosen));
        Assert.True(context.RebindDisables(entry, 1, AnimationBinding.Copied, operation));
        entry.SetText(68, "root");
        Assert.False(context.RebindDisables(entry, 1, AnimationBinding.Copied, operation));
    }

    [Fact]
    public void Mw3UsesUniqueLocalThenGlobalAndKeepsTrackedNamesLocal()
    {
        var context = Context(3, 1, 1); context.World.Game = GameVariant.MechWarrior3;
        var entry = context.Package.Entries[1];
        context.Scene.Nodes[2] = Node(2, "duplicate", []);
        context.Scene.Nodes[3] = Node(3, "duplicate", []);
        AnimationBindingOperation operation = new(context, Token);
        Assert.Equal(-1, operation.Resolve(entry, "duplicate", 1, AnimationBinding.Loaded));
        Assert.Equal(1, operation.Resolve(entry, "root", 1, AnimationBinding.Loaded));
        context.Scene.Nodes.Add(Node(4, "external", []));
        Assert.Equal(4, operation.Resolve(entry, "external", 1, AnimationBinding.Loaded));
        Assert.Equal(-1, context.ResolveTrackedNode(entry, "external", 1, AnimationBinding.Loaded, operation));
    }

    [Fact]
    public void DistinctRootsAndDuplicateRawEdgesShareTheSameAllowance()
    {
        var context = Context(8, 1, 1);
        context.Scene.Nodes[2] = context.Scene.Nodes[2] with { Children = [3, 4, 5, 6, 7, 8] };
        AnimationBindingOperation measured = new(context, Token);
        Assert.Equal(8, measured.Descendants(1, context.Scene.Nodes.Count).Count);
        AnimationBindingOperation limited = new(context, Token, measured.Used + 100);
        _ = limited.Descendants(1, context.Scene.Nodes.Count);
        Assert.Throws<IOException>(() => limited.Descendants(2, context.Scene.Nodes.Count));
        long ordinary = measured.Used;
        context.Scene.Nodes[1] = context.Scene.Nodes[1] with { Children = [2, 2, 2, 3, 4, 5, 6, 7, 8] };
        AnimationBindingOperation duplicates = new(context, Token);
        Assert.Equal(8, duplicates.Descendants(1, context.Scene.Nodes.Count).Count);
        Assert.True(duplicates.Used > ordinary);
    }

    [Fact]
    public void FreshOperationsObserveSameCountEditsAndSharedBudgetSurvivesInvalidation()
    {
        var context = Context(10, 1, 1); AnimationBindingOperation operation = new(context, Token);
        Assert.Equal(2, operation.First(1, "n2", context.Scene.Nodes.Count));
        long first = operation.Used;
        context.Scene.Nodes[2] = context.Scene.Nodes[2] with { Name = "renamed" };
        var fresh = new AnimationBindingOperation(context, Token);
        Assert.Equal(-1, fresh.First(1, "n2", context.Scene.Nodes.Count));
        Assert.Equal(2, fresh.First(1, "renamed", context.Scene.Nodes.Count));
        AnimationBindingOperation limited = new(context, Token, first + 100);
        _ = limited.Descendants(1, context.Scene.Nodes.Count);
        limited.Invalidate();
        Assert.Throws<IOException>(() => limited.Descendants(1, context.Scene.Nodes.Count));
    }

    private static GameNode Node(int index, string name, int[] children) =>
        new(index, name, "object3d", null, [], children, new JsonObject { ["flags"] = 4 }, new JsonObject { ["flags"] = 8 });

    private static AnimationPreviewContext Context(int nodes, int entries, int references)
    {
        byte[] prefix = new byte[72]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), 28);
        AnimationPackage package = new() { Prefix = prefix, Tail = [] };
        for (int i = 0; i <= entries; i++)
        {
            AnimationEntry entry = new(new byte[308], i, 72); entry.SetText(0, "entry" + i); entry.SetText(32, "root"); entry.SetText(68, "missing"); entry.SetInt(148, 0x20); entry.SetFloat(164, -1);
            entry.References[1].Add(new(new byte[40]));
            for (int r = 0; r < references; r++) { AnimationRecord reference = new(new byte[40]); reference.SetText(0, "root", 36); entry.References[1].Add(reference); }
            package.Entries.Add(entry);
        }
        package = AnimationPackage.Read(AnimationWriter.Write(package, Token), Token);
        GameZWorld world = new(); WorldNode top = new("world", WorldNodeClass.World), root = new("root", WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried };
        root.SetPayloadInt(0, 0x28); top.Children.Add(root); root.Parents.Add(top); world.Nodes.AddRange([top, root]);
        for (int i = 2; i <= nodes; i++)
        {
            WorldNode child = new("n" + i, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried }; child.SetPayloadInt(0, 0x28);
            root.Children.Add(child); child.Parents.Add(root); world.Nodes.Add(child);
        }
        var document = FormatRegistry.Default.OpenBytes("world.zbd", GameZWriter.Write(world, Token), token: Token);
        Assert.NotNull(document.Scene);
        return new() { World = document, Package = package };
    }
}
