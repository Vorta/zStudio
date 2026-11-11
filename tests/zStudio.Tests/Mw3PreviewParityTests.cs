using System.Buffers.Binary;
using System.Numerics;
using zStudio.Core;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

public sealed class Mw3PreviewParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnimationRestartCleanupFiresBeforeLaterValveUpdates(bool sameTrigger)
    {
        string gate = sameTrigger ? "go" : "actor_01";
        using var fixture = new Mw3MissionFixture(gate); var package = Package();
        fixture.World.Scene!.Nodes[1] = fixture.World.Scene.Nodes[1] with { Name = gate };
        var entry = Entry(package, "restart", gate); entry.SetInt(148, 0x10030);
        var sequence = new AnimationSequence(new byte[64]); entry.Sequences.Add(sequence);
        var on = AnimationCatalog.Create(35, 39); on.SetInt(12, 43); sequence.Events.Add(on);
        var off = AnimationCatalog.Create(35, 39); off.SetInt(12, 42); entry.Primary.Events.Add(off);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, TestContext.Current.CancellationToken);
        var context = new AnimationPreviewContext { World = fixture.World, Package = package, Mission = mission };
        context.Sounds.Add("sample", new("sample", "sample.wav", false, SourceFixture.Tone(new(22050, 16, 1), 48, null)));
        string definitions = sameTrigger ? "(go (animation (restart 1)))" : "(go (animation (restart 1) update (actor_01)) actor_01 (sound (sample 1)))";
        var simulation = new Mw3ValveSimulation(context, Mw3ValveProgram.Read(zStudio.Core.Sources.ZrdText.Parse(definitions, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        simulation.Set("go", 1, TestContext.Current.CancellationToken); simulation.Advance(.05, TestContext.Current.CancellationToken);
        simulation.Update("go", TestContext.Current.CancellationToken);
        Assert.False(simulation.Faulted); Assert.Equal(1, simulation.Runtime.Value(gate));
        if (!sameTrigger) Assert.Single(simulation.CurrentFrame.ActiveSounds, s => s.Name == "sample");
    }
    [Fact]
    public async Task AnimationCallbackPrecedesDueValveTimersAtTheSameFrameClock()
    {
        using var fixture = new Mw3MissionFixture("actor_01"); var package = Package();
        var entry = Entry(package, "load", "actor"); entry.Bytes[153] = 4; entry.SetInt(148, 0x10030);
        var sequence = new AnimationSequence(new byte[64]); entry.Sequences.Add(sequence);
        var callback = AnimationCatalog.Create(35, 39); callback.SetInt(12, 43); callback.Threshold = .01f; sequence.Events.Add(callback);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, TestContext.Current.CancellationToken);
        var context = new AnimationPreviewContext { World = fixture.World, Package = package, Mission = mission.ValveStartupBaseline(TestContext.Current.CancellationToken) };
        var simulation = new Mw3ValveSimulation(context, Mw3ValveProgram.Read(zStudio.Core.Sources.ZrdText.Parse("(go (delayupdate (actor 0.0)))", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        simulation.Update("go", TestContext.Current.CancellationToken);
        simulation.Advance(AnimationPlayer.StepSeconds, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 1, 0 }, simulation.Runtime.Trace.Where(t => t.Valve == "actor" && t.Kind == "set").Select(t => t.Value));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeleportFollowsOwnedParentingAfterAnimationAttachOrDetach(bool attach)
    {
        using var fixture = new Mw3MissionFixture("actor_01"); var package = Package(); _ = Context(fixture, package); var scene = fixture.World.Scene!;
        scene.Nodes[0] = scene.Nodes[0] with { Children = attach ? [1, 2] : [1] };
        scene.Nodes[1] = scene.Nodes[1] with { Name = "actor_01", Children = attach ? [] : [2] };
        scene.Nodes.Add(new(2, "child", "object3d", 0, attach ? [0] : [1], [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
        var entry = Entry(package, "reparent", "actor_01"); var reference = new AnimationRecord(new byte[40]); reference.SetText(0, "child", 36); entry.References[1].Add(reference);
        var sequence = new AnimationSequence(new byte[64]); entry.Sequences.Add(sequence);
        var parenting = AnimationCatalog.Create((byte)(attach ? 15 : 16), 39); parenting.SetShort(12, -100); parenting.SetShort(14, 1); sequence.Events.Add(parenting);
        var active = AnimationCatalog.Create(6, 39); active.SetShort(16, 1); active.SetInt(12, 1); sequence.Events.Add(active);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, TestContext.Current.CancellationToken);
        var context = new AnimationPreviewContext { World = fixture.World, Package = package, Mission = mission };
        var simulation = new Mw3ValveSimulation(context, Mw3ValveProgram.Read(zStudio.Core.Sources.ZrdText.Parse("(go (animation (reparent 1)) warp (teleport (actor_01 (100 0 0) 0 1)))", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        simulation.Set("go", 1, TestContext.Current.CancellationToken); simulation.Set("warp", 1, TestContext.Current.CancellationToken);
        Assert.Equal(attach ? 100 : 0, Assert.Single(simulation.CurrentFrame.Nodes, n => n.SourceNode == 2).Transform.Translation.X);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadCallbacksBindPostPlacementAndRejectUnchangedSharedAncestors(bool ambiguous)
    {
        using var fixture = new Mw3MissionFixture("actor_01"); var package = Package(); var scene = fixture.World.Scene!;
        scene.Nodes[0] = scene.Nodes[0] with { Children = ambiguous ? [1, 2] : [2] };
        scene.Nodes[1] = scene.Nodes[1] with { Name = ambiguous ? "actor" : "actor_01", Parents = ambiguous ? [2, 0] : [2] };
        scene.Nodes.Add(new(2, "formation", "object3d", null, [0], [1], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
        var entry = Entry(package, "load", scene.Nodes[1].Name); entry.Bytes[153] = 4; entry.SetInt(148, 0x10030);
        var sequence = new AnimationSequence(new byte[64]); entry.Sequences.Add(sequence);
        var callback = AnimationCatalog.Create(35, 39); callback.SetInt(12, 45); callback.Threshold = .1f; sequence.Events.Add(callback);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, TestContext.Current.CancellationToken);
        var context = new AnimationPreviewContext { World = fixture.World, Package = package, Mission = mission.ValveStartupBaseline(TestContext.Current.CancellationToken) };
        var simulation = new Mw3ValveSimulation(context, new([]), TestContext.Current.CancellationToken);
        simulation.Advance(.2, TestContext.Current.CancellationToken);
        Assert.Equal(0, simulation.Runtime.Value("formation"));
        Assert.Equal(ambiguous ? 0 : 1, simulation.Runtime.Value("world"));
    }
    [Fact]
    public async Task ValveSimulationRetainsLoadCallbacksContinuationsAndLoopingSounds()
    {
        using var fixture = new Mw3MissionFixture("actor_01"); var package = Package(); _ = Context(fixture, package);
        fixture.World.Scene!.Nodes[1] = fixture.World.Scene.Nodes[1] with { Name = "actor_01" };
        var entry = Entry(package, "load_valve", "actor_01"); entry.Bytes[153] = 4; entry.SetInt(148, 0x10030);
        var sequence = new AnimationSequence(new byte[64]); entry.Sequences.Add(sequence);
        var cycle = AnimationCatalog.Create(17, 39); cycle.SetShort(16, -100); cycle.SetShort(18, 3); sequence.Events.Add(cycle);
        var first = AnimationCatalog.Create(35, 39); first.SetInt(12, 43); sequence.Events.Add(first);
        var startupSound = AnimationCatalog.Create(2, 39); startupSound.SetText(12, "startup_tone"); startupSound.SetInt(52, 1); sequence.Events.Add(startupSound);
        var later = AnimationCatalog.Create(35, 39); later.SetInt(12, 42); later.Threshold = .1f; sequence.Events.Add(later);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, TestContext.Current.CancellationToken);
        var context = new AnimationPreviewContext { World = fixture.World, Package = package, Mission = mission.ValveStartupBaseline(TestContext.Current.CancellationToken) };
        context.Sounds.Add("loop", new("loop", "loop.wav", true, SourceFixture.Tone(new(22050, 16, 1), 48, null)));
        context.Sounds.Add("startup_tone", context.Sounds["loop"] with { Name = "startup_tone", Loop = false });
        var simulation = new Mw3ValveSimulation(context, Mw3ValveProgram.Read(zStudio.Core.Sources.ZrdText.Parse("(actor_01 (sound_once (loop 1)))", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(1, simulation.Runtime.Value("actor_01"));
        Assert.Equal(3, Assert.Single(simulation.CurrentFrame.Nodes, n => n.SourceNode == mission.Actors[0].Root).Variant);
        Assert.Contains(simulation.CurrentFrame.Sounds, s => s.Name == "startup_tone");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => simulation.Set("actor_01", 0, canceled.Token));
        Assert.Equal(1, simulation.Runtime.Value("actor_01"));
        simulation.Advance(.2, TestContext.Current.CancellationToken);
        Assert.Equal(0, simulation.Runtime.Value("actor_01"));
        Assert.True(Assert.Single(simulation.CurrentFrame.ActiveSounds, s => s.Name == "loop").Persistent);
        Assert.False(simulation.Faulted);
    }
    [Fact]
    public async Task ValveActionsRespectAnimationActivationPrerequisites()
    {
        using var fixture = new Mw3MissionFixture("actor_01"); var package = Package();
        var entry = Entry(package, "guarded", "actor");
        var prerequisite = new AnimationRecord(new byte[48]); prerequisite.Bytes[4] = 2;
        prerequisite.SetInt(0, 1); prerequisite.SetInt(8, 1); prerequisite.SetText(12, "missing_required_node", 28);
        entry.References[6].Add(prerequisite); entry.Bytes[273] = 1;
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, TestContext.Current.CancellationToken);
        var context = new AnimationPreviewContext { World = fixture.World, Package = package, Mission = mission };
        var simulation = new Mw3ValveSimulation(context, Mw3ValveProgram.Read(zStudio.Core.Sources.ZrdText.Parse("(go (animation (guarded 1)))", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        simulation.Set("go", 1, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(simulation.CurrentFrame.Instances, i => i.Name == "guarded");
        Assert.Contains(simulation.CurrentFrame.Refusals, r => r.Reason == "prerequisites");
    }
    [Fact]
    public async Task ValveTeleportReplacesTheCurrentAnimatedRootWithAnAbsoluteNumericPose()
    {
        using var fixture = new Mw3MissionFixture("actor_01"); var package = Package(); _ = Context(fixture, package);
        fixture.World.Scene!.Nodes[1] = fixture.World.Scene.Nodes[1] with { Name = "actor_01" };
        var entry = Entry(package, "move", "actor_01");
        var sequence = new AnimationSequence(new byte[64]); entry.Sequences.Add(sequence);
        var move = AnimationCatalog.Create(7, 39); move.SetShort(28, -100); move.SetVector(16, new(10, 0, 0)); sequence.Events.Add(move);
        var laterMove = AnimationCatalog.Create(7, 39); laterMove.SetShort(28, -100); laterMove.SetVector(16, new(20, 0, 0)); laterMove.Threshold = .1f; sequence.Events.Add(laterMove);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, TestContext.Current.CancellationToken);
        var context = new AnimationPreviewContext { World = fixture.World, Package = package, Mission = mission };
        var program = Mw3ValveProgram.Read(zStudio.Core.Sources.ZrdText.Parse("(go (animation (move 1)) warp (teleport (actor_01 (100 0 0) 90 1)))", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        program.ValidateForEditing(); var simulation = new Mw3ValveSimulation(context, program, TestContext.Current.CancellationToken);
        simulation.Set("go", 1, TestContext.Current.CancellationToken);
        int root = Assert.Single(mission.Actors).Root;
        Assert.Equal(10, Assert.Single(simulation.CurrentFrame.Nodes, n => n.SourceNode == root).Transform.Translation.X);
        simulation.Set("warp", 1, TestContext.Current.CancellationToken);
        Assert.Equal(new Vector3(100, 0, 0), Assert.Single(simulation.CurrentFrame.Nodes, n => n.SourceNode == root).Transform.Translation);
        Assert.Equal(90, simulation.Actors["actor_01"].Yaw);
        simulation.Advance(.2, TestContext.Current.CancellationToken);
        Assert.Equal(20, Assert.Single(simulation.CurrentFrame.Nodes, n => n.SourceNode == root).Transform.Translation.X);
        var parked = new Mw3ValveSimulation(context, program, TestContext.Current.CancellationToken);
        parked.Set("warp", 1, TestContext.Current.CancellationToken);
        Assert.Equal(new Vector3(100, 0, 0), Assert.Single(parked.CurrentFrame.Nodes, n => n.SourceNode == root).Transform.Translation);
    }
    [Fact]
    public async Task ValveTargetDeathWritesDescendingRemainingThresholdsBeforeLaterUpdates()
    {
        using var fixture = new Mw3MissionFixture("actor_01"); var package = Package();
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, TestContext.Current.CancellationToken);
        var target = zStudio.Core.Sources.ZrdText.Parse("(actor_01 (health_below (gate 2 0.2 gate 1 0.8)))", TestContext.Current.CancellationToken);
        mission.AiNetworks = mission.AiNetworks with { ValveSources = [new("fixture", 0, "targets.zrd", target, ReadOnlyMemory<byte>.Empty)] };
        var context = new AnimationPreviewContext { World = fixture.World, Package = package, Mission = mission };
        context.Sounds.Add("loop", new("loop", "loop.wav", true, SourceFixture.Tone(new(22050, 16, 1), 48, null)));
        context.Sounds.Add("short", context.Sounds["loop"] with { Name = "short", Loop = false });
        var simulation = new Mw3ValveSimulation(context, Mw3ValveProgram.Read(zStudio.Core.Sources.ZrdText.Parse("(go (destroy (actor_01 1) update (gate)) gate (sound (loop 0)) clock (delayupdate (click 0.0)) click (sound (short 1)))", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        simulation.Set("go", 1, TestContext.Current.CancellationToken);
        Assert.Equal(2, simulation.Runtime.Value("gate")); Assert.True(simulation.Actors["actor_01"].Destroyed);
        Assert.Empty(simulation.CurrentFrame.ActiveSounds);
        simulation.Set("gate", 9, TestContext.Current.CancellationToken); simulation.Update("go", TestContext.Current.CancellationToken);
        Assert.Equal(9, simulation.Runtime.Value("gate")); Assert.Empty(simulation.CurrentFrame.ActiveSounds);
        simulation.Set("clock", 1, TestContext.Current.CancellationToken);
        simulation.Advance(AnimationPlayer.StepSeconds, TestContext.Current.CancellationToken);
        Assert.InRange(Math.Abs(Assert.Single(simulation.CurrentFrame.ActiveSounds).StartedAt - simulation.Time), 0, 0.000001);
    }
    // Map diagnostics must preserve preview limitations and group call sites instead of flooding the workspace.
    [Fact]
    public async Task MissionDiagnosticsKeepSeverityAndBoundGroupedCallDetails()
    {
        using var fixture = new Mw3MissionFixture(); var package = Package(); var entry = Entry(package, "effects", "actor");
        var motion = AnimationCatalog.Create(10, 39); motion.SetInt(12, 1024); motion.SetInt(16, -100); entry.Primary.Events.Add(motion);
        var sequence = new AnimationSequence(new byte[64]); sequence.SetText(0, "smoke"); entry.Sequences.Add(sequence);
        for (int i = 0; i < 70; i++)
        { var call = AnimationCatalog.Create(24, 39); call.SetText(12, "missing_smoke"); sequence.Events.Add(call); }
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, TestContext.Current.CancellationToken);
        var limitation = Assert.Single(mission.PreviewDiagnostics, d => d.Message.Contains("procedural motion with flat-ground"));
        Assert.Equal("Information", limitation.Severity); Assert.Equal("Preview limitation", limitation.Category);
        var missing = Assert.Single(mission.PreviewDiagnostics, d => d.Category == "Animation references");
        Assert.Equal("Warning", missing.Severity); Assert.Equal(70, missing.Count);
        Assert.Contains("missing_smoke", missing.Message); Assert.Contains("Entry #1", missing.Details); Assert.Contains("sequence smoke", missing.Details);
        Assert.Contains("additional call sites omitted", missing.Details);
        Assert.InRange(missing.Details!.Length, 1, MissionDiagnosticCollector.MaximumDetails);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(43)]
    [InlineData(50)]
    public void NativeCallbackPresentationDoesNotClaimRecoilGameplayEffects(int value)
    {
        var package = Package(); var entry = Entry(package, "callback", "actor");
        var callback = AnimationCatalog.Create(35, 39); callback.SetInt(12, value); entry.Primary.Events.Add(callback);
        var token = TestContext.Current.CancellationToken;
        var graph = AnimationGraph.Create(AnimationGraphIndex.Create(package, token: token), entry.Index, token: token);
        var row = Assert.Single(graph.Rows(entry.Index, entry.Primary.Id).Rows);
        Assert.Equal($"Game callback {value} · traced", row.Label);
        Assert.Equal("traced", row.Callback!.Preview);
        Assert.Contains("MechWarrior 3", row.Callback.Meaning);
        Assert.Equal("moves the player reference", AnimationPhrases.Callback(10, 28).Preview);
        Assert.Equal("does nothing", AnimationPhrases.Callback(50, 28).Short);
    }

    // Retail #N bindings must survive a new compilation and new runtime GUIDs, without collapsing to the root name.
    [Fact]
    public void RepeatedNativeBindingsKeepDistinctScenarioAddresses()
    {
        var token = TestContext.Current.CancellationToken;
        var files = new NativeDefinitions();
        var before = AnimationCompiler.Evaluate(files, "data/c1/zrdr/anim.zad", ["shrubgrp", "shrubgrp", "shrubgrp"], null, zStudio.Core.Formats.FormatRegistry.MaximumDocumentBytes, token, captureSourceBindings: true, game: GameVariant.MechWarrior3);
        var after = AnimationCompiler.Evaluate(files, "data/c1/zrdr/anim.zad", ["shrubgrp", "shrubgrp", "shrubgrp"], null, zStudio.Core.Formats.FormatRegistry.MaximumDocumentBytes, token, captureSourceBindings: true, game: GameVariant.MechWarrior3);
        var source = new AnimationScenarioSource(before.Package, before.Provenance);
        var record = AnimationScenarioRecord.From(AnimationScenario.For(2), source)!;
        List<AnimationScenarioNotice> notices = [];
        var parsed = AnimationScenarioRecord.FromJson(record.ToJson(), notices)!;
        Assert.Equal(2, parsed.Resolve(source, notices).Primary);
        Assert.Equal(2, parsed.Resolve(new(after.Package, after.Provenance), notices).Primary);
        Assert.Empty(notices);
        Assert.NotEqual(before.Provenance!.FindEntry(1)!.BindingProof, before.Provenance.FindEntry(2)!.BindingProof);
    }
    private sealed class NativeDefinitions : zStudio.Core.Worlds.IProjectFiles
    {
        public GameVariant Game => GameVariant.MechWarrior3;
        private readonly byte[] bytes = System.Text.Encoding.Latin1.GetBytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( \"shrubgrp#\" ) ACTIVATION ( ON_CALL ) SEQUENCE_DEFINITION ( NAME ( hide ) OBJECT_ACTIVE_STATE ( NAME ( shrubgrp ) STATE ( INACTIVE ) ) ) ) ) )");
        public bool Exists(string path) => path == "data/c1/zrdr/anim.zad";
        public byte[] Read(string path, CancellationToken token, zStudio.Core.Worlds.ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); limits.Validate(bytes); return bytes; }
    }
    // Consequential native preview regression: raw retail branches are all active until the load resets run.
    [Fact]
    public async Task MissionLoadInitializesBranchesAndRetainsAnimationNavigationAndPlayerStart()
    {
        using var fixture = new Mw3MissionFixture("player", "tower_01");
        var scene = fixture.World.Scene!;
        scene.Nodes[0] = scene.Nodes[0] with { Children = [1, 5, 6] };
        scene.Nodes[1] = scene.Nodes[1] with { Name = "tower", Parents = [0], Children = [2, 3, 4], Data = new() };
        foreach (string name in new[] { "healthy", "destroy", "dbase", "shrubgrp", "shrubgrp" })
        {
            int index = scene.Nodes.Count;
            scene.Nodes.Add(new(index, name, "object3d", null, [index < 5 ? 1 : 0], [], new() { ["flags"] = 4 }, new()));
        }
        foreach (var node in scene.Nodes.Where(n => n.Class == "object3d"))
            node.Data["transform"] = new System.Text.Json.Nodes.JsonArray(1,0,0,0,1,0,0,0,1,0,0,0);
        var package = Package(); var tower = Entry(package, "tower", "tower");
        tower.SetText(68, "healthy");
        foreach (string name in new[] { "healthy", "destroy", "dbase" })
        {
            var reference = new AnimationRecord(new byte[40]); reference.SetText(0, name, 36); tower.References[1].Add(reference);
            var active = AnimationCatalog.Create(6, 39); active.SetInt(12, name == "healthy" ? 1 : 0);
            active.SetShort(16, (short)(tower.References[1].Count - 1)); tower.Primary.Events.Add(active);
        }
        var first = Entry(package, "dest_shrubgrp#0", "shrubgrp");
        var second = Entry(package, "dest_shrubgrp#1", "shrubgrp");
        var range = Entry(package, "range_start", "shrubgrp"); range.Bytes[153] = 4; range.SetInt(148, 0x10022); range.SetFloat(160, 200);
        var rangeSequence = new AnimationSequence(new byte[64]); range.Sequences.Add(rangeSequence);
        var hide = AnimationCatalog.Create(6, 39); hide.SetShort(16, -100); hide.SetInt(12, 0); rangeSequence.Events.Add(hide);
        Entry(package, "start_single_player_m1", "world");
        var token = TestContext.Current.CancellationToken;
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, token);
        Assert.True(Active(mission.Scene.Nodes[2])); Assert.False(Active(mission.Scene.Nodes[3]), string.Join("; ", mission.Diagnostics)); Assert.False(Active(mission.Scene.Nodes[4]));
        Assert.All(scene.Nodes.Skip(2), n => Assert.True(Active(n))); // Owned baseline, never authored flags.
        Assert.Equal(6, mission.AnimationEntryRoots[first.Index]); Assert.Equal(5, mission.AnimationEntryRoots[second.Index]);
        Assert.Contains(1, mission.AnimationUses.Keys);
        Assert.False(Active(mission.Scene.Nodes[6])); // An in-range ON_STARTUP sees the native player spawn.
        var actor = Assert.Single(mission.Actors);
        Assert.False(Active(mission.Scene.Nodes.Single(n => n.Name == "destroy" && n.Parents.Contains(actor.Root))));
        Assert.Equal(new Vector3(10, 2, 3), mission.PlayerStart);
        Assert.Equal(new[] { "start_single_player_m1" }, mission.StartupAnimations);
        var navigation = new AnimationPreviewContext { World = fixture.World, Package = package, Mission = mission, LoadedNodeCount = scene.Nodes.Count };
        var external = new AnimationRecord(new byte[40]); external.SetText(0, "shrubgrp", 36); tower.References[1].Add(external);
        var eventOutside = AnimationCatalog.Create(6, 39); eventOutside.SetShort(16, (short)(tower.References[1].Count - 1));
        Assert.Equal(new[] { 6 }, navigation.EventNodes(tower, eventOutside, 1));
        eventOutside.SetShort(16, -200); Assert.Equal(new[] { 1 }, navigation.EventNodes(tower, eventOutside, 1));
        // A supplied edited program is part of the native cache identity.
        tower.Primary.Events[1].SetInt(12, 1);
        var changed = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, package, token);
        Assert.True(Active(changed.Scene.Nodes[3])); Assert.False(Active(mission.Scene.Nodes[3]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NativeResetHonorsAutomaticRestoration(bool automatic, bool dormant)
    {
        using var fixture = new Mw3MissionFixture(); var package = Package();
        var entry = Entry(package, "move", "actor"); entry.SetInt(148, automatic ? 0x10020 : 0x20); entry.SetFloat(164, dormant ? -1 : 0);
        var tracked = new AnimationRecord(new byte[108]); tracked.SetText(0, "actor", 36); entry.References[0].Add(tracked);
        var sequence = new AnimationSequence(new byte[64]); entry.Sequences.Add(sequence);
        var move = AnimationCatalog.Create(7, 39); move.SetShort(28, -100); move.SetVector(16, new(7, 8, 9)); sequence.Events.Add(move);
        var alpha = AnimationCatalog.Create(13, 39); alpha.SetShort(12, 1); alpha.SetShort(20, -100); alpha.SetFloat(16, .25f); sequence.Events.Add(alpha);
        var context = Context(fixture, package);
        AnimationSchedule? schedule = null;
        if (dormant)
        {
            var reset = Entry(package, "reset_move", "actor"); var calls = new AnimationSequence(new byte[64]); reset.Sequences.Add(calls);
            var call = AnimationCatalog.Create(26, 39); call.SetText(12, "move"); calls.Events.Add(call);
            schedule = new([new(Guid.Empty, entry.Index, 0), new(Guid.NewGuid(), reset.Index, .2)], package.Entries.Count);
        }
        var frame = new AnimationPlayer(context, entry.Index, 1, false, TestContext.Current.CancellationToken, schedule).EvaluateForTest(.3);
        Assert.Equal(automatic ? Vector3.Zero : new(7, 8, 9), Assert.Single(frame.Nodes).Transform.Translation);
        Assert.Equal(automatic ? 1 : .25f, Assert.Single(frame.Nodes).Opacity);
    }

    [Fact]
    public void NativePrerequisiteThresholdIsIndependentOfPufferCountAndDiffuseUpdatesRemainVisible()
    {
        using var fixture = new Mw3MissionFixture(); var package = Package(); var entry = Entry(package, "light", "actor");
        entry.Bytes[268] = 7; entry.SetStoredCount(9, 2); Assert.Equal(2, entry.MinimumToSatisfy);
        var context = Context(fixture, package);
        var prerequisites = new AnimationPlayer(context, entry.Index).Prerequisites(entry.Index, TestContext.Current.CancellationToken);
        Assert.False(prerequisites.Passed); Assert.Equal(2, prerequisites.Minimum);
        entry.SetStoredCount(9, 0);
        var sequence = new AnimationSequence(new byte[64]); entry.Sequences.Add(sequence);
        var light = AnimationCatalog.Create(4, 39); light.SetText(12, "spark"); light.SetInt(52, 1); light.SetInt(48, 0x78);
        light.SetFloat(108, 10); light.SetVector(112, Vector3.UnitZ); light.SetFloat(124, 0); light.SetFloat(128, 1); sequence.Events.Add(light);
        var update = AnimationCatalog.Create(4, 39); update.SetText(12, "spark"); update.SetInt(52, 1); update.SetInt(48, 0x40); update.SetFloat(128, .5f); update.Threshold = .05f; sequence.Events.Add(update);
        var wait = AnimationCatalog.Create(7, 39); wait.Threshold = 1; sequence.Events.Add(wait);
        var player = new AnimationPlayer(context, entry.Index);
        Assert.Equal(1, Assert.Single(player.EvaluateForTest(.02).Lights).Intensity);
        var result = Assert.Single(player.EvaluateForTest(.1).Lights);
        Assert.Equal(0, result.Ambient); Assert.Equal(.5f, result.Diffuse); Assert.Equal(.5f, result.Intensity);
    }

    private static bool Active(GameNode node) => (node.Metadata.UInt("flags") & 4) != 0;
    private static AnimationPreviewContext Context(Mw3MissionFixture fixture, AnimationPackage package)
    {
        var scene = fixture.World.Scene!; scene.Nodes[0] = scene.Nodes[0] with { Children = [1] }; scene.Nodes[1] = scene.Nodes[1] with { ModelIndex = 0, Parents = [0] };
        scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [], []));
        return new() { World = fixture.World, Package = package };
    }
    private static AnimationPackage Package()
    {
        byte[] prefix = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 39);
        var package = new AnimationPackage { Prefix = prefix, Tail = [] };
        package.Entries.Add(new(new byte[316], 0, -1)); return package;
    }
    private static AnimationEntry Entry(AnimationPackage package, string name, string root)
    {
        var entry = new AnimationEntry(new byte[316], package.Entries.Count, -1); entry.SetText(0, name); entry.SetText(32, root); entry.SetText(68, root);
        entry.SetInt(148, 0x10020); entry.SetFloat(164, -1); entry.References[1].Add(new(new byte[40]));
        package.Entries.Add(entry); return entry;
    }
}
