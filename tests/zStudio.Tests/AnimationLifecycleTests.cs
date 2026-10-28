using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

/// <summary>Binding lifecycle, refusals and condition rules as the 1999 runtime applies them (docs/agents/engine-evidence.md, "Animation runtime").</summary>
public sealed partial class AnimationTests
{
    private static CancellationToken LifecycleToken => TestContext.Current.CancellationToken;
    private static AnimationEvent Named(byte type, string name, float at = 0) { var ev = AnimationCatalog.Create(type); ev.SetText(12, name); ev.Threshold = at; return ev; }
    private static AnimationEvent Step(float x) { var move = AnimationCatalog.Create(7); move.SetShort(28, 1); move.SetInt(12, 1); move.SetVector(16, new(x, 0, 0)); return move; }
    private static AnimationEvent Forever() { var loop = AnimationCatalog.Create(30); loop.SetInt(12, 1); loop.SetInt(16, 65535); return loop; }
    /// <summary>The fixture's node under a world, so instances share it as world-bound animations do.</summary>
    private static AnimationPreviewContext WorldContext(AnimationPackage package)
    {
        var context = Context(package);
        context.Scene.Nodes[0] = context.Scene.Nodes[0] with { Parents = [1] };
        context.Scene.Nodes.Add(new(1, "world", "world", null, [], [0], new(), new()));
        return context;
    }
    private static AnimationPlayer Scheduled(AnimationPreviewContext context, AnimationExecutionPolicy? policy, params (int Entry, double Seconds)[] starts)
        => new(context, 0, 1, false, LifecycleToken, new AnimationSchedule(starts.Select((s, i) => new AnimationScheduledStart(i == 0 ? Guid.Empty : Guid.NewGuid(), s.Entry, s.Seconds)), context.Package.Entries.Count))
            { ExecutionPolicy = policy };

    // Mismatches 2 and 3: INVALIDATE leaves a running loop running and refuses later starts; by-name controls act on the
    // first entry of the name only.
    [Fact]
    public void NamedControlsActOnTheFirstEntryAndInvalidateKeepsItsLoop()
    {
        var package = Fixture(); var root = package.Entries[0];
        var target = PolicyEntry(package, "target"); var twin = PolicyEntry(package, "target");
        target.Sequences[0].Events.AddRange([Step(1), Forever()]); twin.Sequences[0].Events.AddRange([Step(1), Forever()]);
        var invalidate = Named(27, "target", .5f); var stop = Named(25, "target", 1.5f);
        root.Sequences[0].Events.AddRange([invalidate, stop]);
        var player = Scheduled(Context(package), null, (0, 0), (target.Index, 0), (twin.Index, 0), (target.Index, 1));
        float X(AnimationFrame frame, int entry) => frame.Nodes.Single(n => n.Id >> 32 == frame.Instances.First(i => i.Entry == entry).Instance).Transform.M41;
        var early = player.EvaluateForTest(.6); var later = player.EvaluateForTest(1.2);
        Assert.True(X(later, target.Index) > X(early, target.Index) + 30);
        Assert.Equal(AnimationBindingLifecycle.RunningInvalidated, later.Bindings.Single(b => b.Entry == target.Index).State);
        var refused = Assert.Single(later.Refusals);
        Assert.Equal((target.Index, "invalidated", true), (refused.Entry, refused.Reason, refused.ScenarioStart)); Assert.InRange(refused.Time, .99, 1.01);
        var applied = later.Trace.Single(t => t.Event == invalidate.Id);
        Assert.Equal(("applied", (int?)target.Index), (applied.Disposition, applied.ChildEntry));
        var end = player.EvaluateForTest(2);
        Assert.Equal(AnimationBindingLifecycle.Invalidated, end.Bindings.Single(b => b.Entry == target.Index).State);
        Assert.Equal(AnimationBindingLifecycle.Running, end.Bindings.Single(b => b.Entry == twin.Index).State);
        Assert.True(X(end, twin.Index) > X(later, twin.Index) + 30);
        player.EvaluateForTest(.2, true); Assert.Equal(end.Bindings, player.EvaluateForTest(2, true).Bindings);
    }

    // Mismatches 1, 4 and 11: a reset restores the rest pose and runs the cleanup at once, also on a finished binding.
    [Fact]
    public void ResetOfAFinishedBindingRestoresItsRestPoseAndRunsItsCleanup()
    {
        var package = Fixture(); var root = package.Entries[0];
        var target = PolicyEntry(package, "target");
        var tracked = new AnimationRecord(new byte[96]); tracked.SetText(0, "animated"); target.References[0].Add(tracked);
        target.Sequences[0].Events.Add(Step(7));
        var hide = AnimationCatalog.Create(6); hide.SetShort(16, 1); hide.SetInt(12, 0); target.Primary.Events.Add(hide);
        var reset = Named(26, "target", .5f); root.Sequences[0].Events.Add(reset);
        var player = Scheduled(WorldContext(package), null, (0, 0), (target.Index, 0));
        var finished = player.EvaluateForTest(.4);
        Assert.Equal(7, finished.Nodes.Single(n => n.SourceNode == 0).Transform.M41);
        Assert.Equal(AnimationBindingLifecycle.FinishedPoseKept, finished.Bindings.Single(b => b.Entry == target.Index).State);
        var frame = player.EvaluateForTest(.55);
        var pose = frame.Nodes.Single(n => n.SourceNode == 0);
        Assert.Equal(0, pose.Transform.M41); Assert.False(pose.Visible);
        Assert.Equal(AnimationBindingLifecycle.Stopped, frame.Bindings.Single(b => b.Entry == target.Index).State);
        Assert.InRange(frame.Trace.Single(t => t.Event == hide.Id).Start - frame.Trace.Single(t => t.Event == reset.Id).Start, 0, AnimationPlayer.StepSeconds + 1e-9);
    }

    // Mismatches 5, 6 and 13: a finished flag-0x20 binding runs its cleanup before it starts again, RESET_TIME -99 starts
    // once, and a LOCAL_NAME child stops with its launcher.
    [Fact]
    public void RestartsCleanUpFirstOneShotsRefuseAndLocalChildrenStopWithTheirLauncher()
    {
        var package = Fixture(); var root = package.Entries[0];
        var rocket = PolicyEntry(package, "rocket"); rocket.SetInt(148, 0x20); rocket.Sequences[0].Events.Add(Step(5));
        var home = AnimationCatalog.Create(7); home.SetShort(28, 1); home.SetVector(16, Vector3.Zero); rocket.Primary.Events.Add(home);
        // The other bindings share the node without moving it.
        var once = PolicyEntry(package, "once"); once.SetFloat(164, -99); once.Sequences[0].Events.Add(AnimationCatalog.Create(35));
        var child = PolicyEntry(package, "child"); child.Sequences[0].Events.Add(Forever());
        var launcher = PolicyEntry(package, "launcher");
        var slot = new AnimationRecord(new byte[72]); slot.SetText(0, "child"); slot.SetInt(64, 1);
        launcher.References[7].AddRange([new(new byte[72]), slot]);
        var wait = AnimationCatalog.Create(35); wait.Threshold = .5f;
        var call = Child(24, "child", (short)child.Index); call.SetShort(50, 1); launcher.Sequences[0].Events.AddRange([call, wait]);
        var player = Scheduled(WorldContext(package), null, (0, 0), (rocket.Index, 0), (rocket.Index, 1), (once.Index, 0), (once.Index, 1), (launcher.Index, 0));
        var frame = player.EvaluateForTest(2);
        Assert.Equal(5, frame.Nodes.Single(n => n.SourceNode == 0).Transform.M41);
        var cleanup = frame.Trace.Where(t => t.Event == home.Id).ToArray();
        Assert.InRange(Assert.Single(cleanup).Start, .99, 1.02);
        Assert.Equal("oneShot", frame.Refusals.Single(r => r.Entry == once.Index).Reason);
        var launched = frame.Trace.Single(t => t.Event == call.Id).ChildInstance;
        Assert.True(frame.Instances.Single(i => i.Instance == launched).Finished);
        Assert.Equal(AnimationBindingLifecycle.FinishedPoseKept, frame.Bindings.Single(b => b.Entry == child.Index).State);
    }

    // Mismatches 7–9: range tests measure the player reference from the attach node; a range gate freezes a binding's clocks.
    [Fact]
    public void RangeTestsMeasureThePlayerFromTheAttachNodeAndGatesFreezeTheClocks()
    {
        var package = Fixture(); var entry = package.Entries[0]; entry.SetText(68, "attach");
        var test = AnimationCatalog.Create(31); test.SetInt(12, 2); test.SetFloat(20, 100);
        entry.Sequences[0].Events.AddRange([test, Step(1), AnimationCatalog.Create(34)]);
        AnimationPreviewContext Attached()
        {
            var context = Context(package);
            context.Scene.Nodes[0] = context.Scene.Nodes[0] with { Children = [1] };
            context.Scene.Nodes.Add(new(1, "attach", "object3d", null, [0], [], new JsonObject { ["flags"] = 4 },
                new JsonObject { ["flags"] = 0, ["opacity"] = 1f, ["scale"] = JsonData.Vector(Vector3.One), ["transform"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 100, 0, 0) }));
            return context;
        }
        AnimationConditionResult Outcome(AnimationScenarioRules rules) => Assert.Single(new AnimationPlayer(Attached(), 0)
            { ExecutionPolicy = new(rules: rules) }.EvaluateForTest(.1).Conditions);
        var near = Outcome(new() { Player = AnimationPlayerReference.Custom(new(100, 0, 0)) });
        Assert.Equal((true, "playerRange", (double?)0, (double?)10), (near.Value, near.Kind, near.Measured, near.Threshold));
        Assert.False(Outcome(new() { Player = AnimationPlayerReference.Custom(Vector3.Zero) }).Value);
        var none = Outcome(new() { Player = AnimationPlayerReference.None });
        Assert.Equal((false, (string?)"no player"), (none.Value, none.Unavailable));
        var skipped = new AnimationPlayer(Attached(), 0) { ExecutionPolicy = new(rules: new() { Player = AnimationPlayerReference.None }) }.EvaluateForTest(.1).Skipped;
        Assert.Equal(1, Assert.Single(skipped).Count);

        entry.SetInt(148, 2); entry.SetFloat(160, 100);
        var gated = new AnimationPlayer(Attached(), 0) { ExecutionPolicy = new(rules: new() { Player = AnimationPlayerReference.Custom(new(500, 0, 0)) }) }.EvaluateForTest(1);
        Assert.Empty(gated.Trace); Assert.Equal(0, gated.Sequences[0].AnimationElapsed);
        Assert.Equal(((string?)"out of range", AnimationBindingLifecycle.Paused), (gated.Bindings[0].PausedBy, gated.Bindings[0].State));
        var child = PolicyEntry(package, "child"); child.Sequences[0].Events.Add(Step(2));
        var call = Child(24, "child", (short)child.Index); entry.Sequences[0].Events.Add(call);
        var ignored = new AnimationPlayer(Attached(), 0)
            { ExecutionPolicy = new(rules: new() { Player = AnimationPlayerReference.Custom(new(500, 0, 0)), RangeGates = AnimationRangeGateRule.Ignore }) }.EvaluateForTest(1);
        Assert.NotEmpty(ignored.Trace);
        Assert.Equal("excluded", ignored.Trace.Single(t => t.Event == call.Id).Disposition);
        // The isolated viewer's policy admits every call under its rules, the custom player and ignored range gates among them.
        var isolated = new AnimationPlayer(Attached(), 0)
            { ExecutionPolicy = AnimationExecutionPolicy.AllCalls(new() { Player = AnimationPlayerReference.Custom(new(100, 0, 0)), RangeGates = AnimationRangeGateRule.Ignore }) }.EvaluateForTest(1);
        Assert.Equal(("launched", (int?)child.Index), (isolated.Trace.Single(t => t.Event == call.Id).Disposition, isolated.Trace.Single(t => t.Event == call.Id).ChildEntry));
        Assert.True(isolated.Conditions[0].Value);
    }

    // Mismatch 10: NODE_UNDERCOVER is an upward segment test against the intersection surfaces, skipping the node's own subtree.
    [Fact]
    public void CoverConditionsTestTheSurfacesAboveTheirNode()
    {
        var package = Fixture(); var entry = package.Entries[0];
        AnimationEvent Cover(float height) { var test = AnimationCatalog.Create(31); test.SetInt(12, 0x10); test.SetInt(16, 1); test.SetFloat(20, height); return test; }
        AnimationPreviewContext Roofed(uint roof = 0x14)
        {
            var context = Context(package);
            Polygon Quad(uint flags) => new(0, flags | 4, [0, 1, 2, 3], [], [], new JsonObject());
            context.Scene.Models.Add(new(1, [new(-5, 0, -5), new(5, 0, -5), new(5, 0, 5), new(-5, 0, 5)], [], [], [Quad(0x100)], new JsonObject()));
            context.Scene.Nodes[0] = context.Scene.Nodes[0] with { Parents = [1], Metadata = new JsonObject { ["flags"] = 0x14 } };
            context.Scene.Nodes.Add(new(1, "world", "world", null, [], [0, 2], new(), new()));
            context.Scene.Nodes.Add(new(2, "roof", "object3d", 1, [1], [], new JsonObject { ["flags"] = roof },
                new JsonObject { ["flags"] = 0, ["opacity"] = 1f, ["scale"] = JsonData.Vector(Vector3.One), ["transform"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 10, 0) }));
            return context;
        }
        entry.Sequences[0].Events.AddRange([Cover(20), AnimationCatalog.Create(34), Cover(5), AnimationCatalog.Create(34)]);
        var results = new AnimationPlayer(Roofed(), 0).EvaluateForTest(.1).Conditions;
        Assert.Equal((true, "cover", (double?)10, (string?)null), (results[0].Value, results[0].Kind, results[0].Measured, results[0].Unavailable));
        Assert.False(results[1].Value);
        var bare = new AnimationPlayer(Context(package), 0).EvaluateForTest(.1).Conditions[0];
        Assert.Equal((false, (string?)"the scene has no intersection surfaces"), (bare.Value, bare.Unavailable));
        // A surface hidden at load is cover once the run shows it (its active state is the run's, its surface flag static).
        var named = new AnimationRecord(new byte[40]); named.SetText(0, "roof", 36); entry.References[1].Add(named);
        Assert.False(new AnimationPlayer(Roofed(0x10), 0).EvaluateForTest(.1).Conditions[0].Value);
        var show = AnimationCatalog.Create(6); show.SetInt(12, 1); show.SetShort(16, (short)(entry.References[1].Count - 1));
        entry.Sequences[0].Events.Insert(0, show);
        Assert.True(new AnimationPlayer(Roofed(0x10), 0).EvaluateForTest(.1).Conditions[0].Value);
    }

    // Mismatch 13: prerequisites are checked on every start the run makes; the scenario's own starts follow its rule.
    [Fact]
    public void PrerequisitesRefuseStartsAsTheScenarioRuleSays()
    {
        var package = Fixture(); var root = package.Entries[0];
        var gate = PolicyEntry(package, "gate"); gate.SetInt(148, 0x20); gate.Sequences[0].Events.Add(PolicyMotion(.1f));
        var guarded = PolicyEntry(package, "guarded"); guarded.Sequences[0].Events.Add(PolicyMotion(.1f));
        var requirement = new AnimationRecord(new byte[48]); requirement.Bytes[4] = 1; requirement.SetText(8, "gate"); guarded.References[6].Add(requirement);
        var early = Child(24, "guarded", (short)guarded.Index); var late = Child(24, "guarded", (short)guarded.Index); late.Threshold = 1;
        root.Sequences[0].Events.AddRange([early, late]);
        var player = Scheduled(Context(package), null, (0, 0), (guarded.Index, 0), (gate.Index, .5));
        var frame = player.EvaluateForTest(1.5);
        var refused = Assert.Single(frame.Refusals);
        Assert.Equal(("prerequisites", false), (refused.Reason, refused.ScenarioStart));
        Assert.Equal(false, Assert.Single(refused.Prerequisites!.Checks).Satisfied);
        Assert.Equal("refused", frame.Trace.Single(t => t.Event == early.Id).Disposition);
        Assert.Equal("launched", frame.Trace.Single(t => t.Event == late.Id).Disposition);
        Assert.Contains(frame.Instances, i => i.Entry == guarded.Index && i.Path.Length > 0 && i.StartedAt == 0);
        var strict = Scheduled(Context(package), new(rules: new() { Prerequisites = AnimationPrerequisiteRule.Check }), (0, 0), (guarded.Index, 0)).EvaluateForTest(.1);
        Assert.Contains(strict.Refusals, r => r.Entry == guarded.Index && r.ScenarioStart);
    }
}
