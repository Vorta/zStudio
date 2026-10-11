using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationBindingWorkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void CopiedChildrenShareLifetimeAdmissionWithCheckpointsAndResetReleasesIt()
    {
        // Each20-node activation fits the ordinary binding work allowance. Repeated activations must
        // still stop before retained private poses/reset copies/checkpoints exceed a small live limit.
        var context = Context(20, 2, 1);
        var parent = context.Package.Entries[1]; var child = context.Package.Entries[2];
        child.SetInt(148, 0x8000);
        var wait = AnimationCatalog.Create(11); wait.SetInt(16, 0); wait.SetFloat(140, 1);
        var launch = AnimationCatalog.Create(24); launch.SetText(12, child.Name, 20); launch.SetShort(48, 2); launch.SetShort(50, -1);
        var loop = AnimationCatalog.Create(30); loop.SetInt(12, 1); loop.SetInt(16, 65535);
        AnimationSequence emitter = new(new byte[64]); emitter.Name = "emitter"; emitter.Events.AddRange([wait, launch, loop]); parent.Sequences.Add(emitter);
        AnimationSequence lingering = new(new byte[64]); lingering.Name = "lingering";
        var delay = AnimationCatalog.Create(11); delay.SetInt(16, 0); delay.SetFloat(140, 3600); lingering.Events.Add(delay); child.Sequences.Add(lingering);
        byte[] original = AnimationWriter.Write(context.Package, Token);
        const long limit = 128 * 1024;
        long otherState = 0;
        AnimationPlayer player = new(context, 1, 1, false, Token, limit, () => otherState);
        long initial = player.RetainedStateBytes;
        var accepted = player.AdvanceTo(1.5, token: Token);
        Assert.Equal(2, player.RetainedInstanceCount);
        Assert.True(player.RetainedCheckpointCount > 1);
        player.AdvanceTo(.5, true, Token);
        var replayed = player.AdvanceTo(1.5, true, Token);
        Assert.Equal(accepted.Sequences, replayed.Sequences);
        player.Reset(Token);
        Assert.Equal(initial, player.RetainedStateBytes);
        var reset = player.Frame(Token);
        // The same owner can temporarily lend the remaining allowance to a duration/reset snapshot.
        // A replacement must refuse before discarding the accepted zero state, then retry after release.
        otherState = limit - initial;
        Assert.Throws<AnimationPlayer.StateLimitException>(() => player.Reset(Token));
        Assert.Equal(0, player.Time);
        Assert.Equal(reset.Sequences, player.Frame(Token).Sequences);
        otherState = 0;
        player.Reset(Token);
        Assert.Equal(accepted.Sequences, player.AdvanceTo(1.5, true, Token).Sequences);
        var refused = player.AdvanceTo(20, token: Token);
        Assert.Contains(refused.Diagnostics, text => text.Contains("retained node/event allowance", StringComparison.Ordinal));
        Assert.Contains(refused.Sequences, state => state.State == "Unavailable");
        Assert.InRange(player.RetainedInstanceCount, 2, 15);
        Assert.InRange(player.RetainedStateBytes, initial, limit);
        double time = player.Time;
        Assert.False(player.MeasureDuration(Token).IsFinite);
        Assert.Equal(time, player.Time);
        player = new(context, 1, 1, false, Token, limit);
        Assert.Equal(initial, player.RetainedStateBytes);
        Assert.Equal(accepted.Sequences, player.AdvanceTo(1.5, true, Token).Sequences);
        Assert.Equal(original, AnimationWriter.Write(context.Package, Token));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => player.AdvanceTo(.5, true, canceled.Token));
        Assert.Equal(1.5, player.Time, 6);
        Assert.Equal(accepted.Sequences, player.AdvanceTo(1.5, true, Token).Sequences);

        // Supported fanout: many first-start admissions must not rescan all other live sequences.
        // A small case exposes quadratic accounting through deterministic visits, without a stress run.
        var fanout = Context(2, 2, 1); fanout.Package.Entries[2].SetInt(148, 0x8000);
        AnimationSequence burst = new(new byte[64]);
        for (int i = 0; i < 8; i++) burst.Events.Add(launch.Clone(Token));
        fanout.Package.Entries[1].Sequences.Add(burst);
        for (int i = 0; i < 32; i++)
        {
            AnimationSequence parallel = new(new byte[64]); parallel.Events.Add(delay.Clone(Token));
            fanout.Package.Entries[2].Sequences.Add(parallel);
        }
        fanout = new() { World = fanout.World, Package = AnimationPackage.Read(AnimationWriter.Write(fanout.Package, Token), Token) };
        // Only the shared root's render pose needs these unbound ancestors; private child roots use
        // their baked world pose. Each newly discovered parent must debit bytes without a full scan.
        int firstAncestor = fanout.Scene.Nodes.Count, lastAncestor = firstAncestor + 15;
        fanout.Scene.Models.Add(new(0, [System.Numerics.Vector3.Zero], [], [], [], new()));
        fanout.Scene.Nodes[1] = fanout.Scene.Nodes[1] with { ModelIndex = 0, Parents = [lastAncestor] };
        fanout.Scene.Nodes[0] = fanout.Scene.Nodes[0] with { Children = [firstAncestor] };
        for (int i = firstAncestor; i <= lastAncestor; i++)
            fanout.Scene.Nodes.Add(Node(i, "ancestor" + i, [i == lastAncestor ? 1 : i + 1]) with { Parents = [i == firstAncestor ? 0 : i - 1] });
        var many = new AnimationPlayer(fanout, 1, 1, false, Token);
        long ancestorVisits = many.RetainedAccountingSequenceVisits;
        var first = many.Step(Token);
        Assert.Equal(9, first.Nodes.Count);
        Assert.InRange(many.RetainedAccountingSequenceVisits - ancestorVisits, 0, 2048);
        Assert.Equal(9, many.RetainedInstanceCount);
        long visits = many.RetainedAccountingSequenceVisits;
        var started = many.Step(Token);
        Assert.Equal(256, started.Sequences.Count(s => s.State == "Running"));
        Assert.InRange(many.RetainedAccountingSequenceVisits - visits, 0, 2 * 256);
    }

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

        // A stable turret phase must reuse the loaded-world lookup while still charging each reset.
        // Sixteen tiny turrets beside unrelated nodes/entries fit; repeated whole-map rebuilds do not.
        var turrets = Context(256, 512, 1);
        int firstTurret = turrets.Scene.Nodes.Count;
        for (int i = 0; i < 16; i++)
            turrets.Scene.Nodes.Add(Node(firstTurret + i, $"turret{i:00}", []) with { Parents = [0] });
        var ai = ZrdText.Parse("( DESTROY_ANIM ( entry1 ) TURRET ( \"turret**\" ( ) ) )", Token).ToJson(Token);
        AnimationBindingOperation turretWork = new(turrets, Token, 300_000);
        MissionSceneLoader.InitializeTurrets(turrets.Scene, turrets, ai, [], [], Token, turretWork);
        Assert.All(turrets.Scene.Nodes.Skip(firstTurret), n => Assert.Equal("entry1", n.Metadata.Text("preview_turret_reset")));
        // A separate frontier must discard both indexes after same-count scene and package edits.
        turrets.Scene.Nodes[1] = turrets.Scene.Nodes[1] with { Name = "renamed" };
        turrets.Package.Entries[1].SetText(0, "renamedEntry");
        List<string> notes = [];
        AnimationPlayer.ApplyInitialization(turrets, false, ["renamedEntry"], notes, Token, turretWork);
        Assert.Contains(notes, n => n.Contains("unresolved root for renamedEntry", StringComparison.Ordinal));
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
