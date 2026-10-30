using System.Numerics;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void ExcludedWaitBlocksOnlyItsSequenceAndSurvivesSeeking()
    {
        var package = Fixture(); var root = package.Entries[0];
        var child = PolicyEntry(package, "child"); child.Sequences[0].Events.Add(PolicyMotion(3));
        var call = Child(24, child.Name, 1); call.SetShort(46, 16);
        var hide = AnimationCatalog.Create(6); hide.SetShort(16, 1); hide.SetInt(12, 0);
        root.Sequences[0].Events.AddRange([call, hide]);
        var concurrent = new AnimationSequence(new byte[64]) { Name = "concurrent" };
        var move = AnimationCatalog.Create(7); move.SetShort(28, 1); move.SetVector(16, new(7, 0, 0)); move.Threshold = .5f;
        concurrent.Events.Add(move); root.Sequences.Add(concurrent);
        byte[] source = Pack(package);
        var player = new AnimationPlayer(Context(package), 0) { ExecutionPolicy = AnimationExecutionPolicy.RootOnly };
        var frame = player.EvaluateForTest(2);
        Assert.True(frame.DependencyBlocked); Assert.False(player.IsComplete);
        Assert.True(frame.Nodes[0].Visible); Assert.Equal(7, frame.Nodes[0].Transform.M41);
        Assert.Single(frame.Instances);
        Assert.Equal("excluded_wait", frame.Trace.Single(t => t.Event == call.Id).Disposition);
        Assert.DoesNotContain(frame.Trace, t => t.Event == hide.Id);
        Assert.Equal("Awaiting excluded dependency", frame.Sequences[0].State);
        player.EvaluateForTest(1, true); var replay = player.EvaluateForTest(2, true);
        Assert.Equal(frame.Trace, replay.Trace); Assert.Equal(frame.Sequences, replay.Sequences);
        Assert.Equal(frame.Instances, replay.Instances); Assert.True(replay.DependencyBlocked);
        Assert.Equal(AnimationDurationKind.DependencyBlocked, player.MeasureDuration(TestContext.Current.CancellationToken).Kind);
        Assert.Contains("excluded dependency", new AnimationPlayer(Context(package), 0)
            { ExecutionPolicy = AnimationExecutionPolicy.RootOnly }.AnalyzePaths(2, TestContext.Current.CancellationToken).Status);
        Assert.Equal(source, Pack(package));
    }

    [Fact]
    public void IncludedParentWaitPropagatesBlockedDurationWithoutCompletingEitherCaller()
    {
        var package = Fixture(); var child = PolicyEntry(package, "child"); var leaf = PolicyEntry(package, "leaf");
        var outer = Child(24, child.Name, 1); outer.SetShort(46, 16);
        var inner = Child(24, leaf.Name, 2); inner.SetShort(46, 16);
        package.Entries[0].Sequences[0].Events.Add(outer); child.Sequences[0].Events.Add(inner);
        var policy = new AnimationExecutionPolicy([AnimationExecutionPolicy.ChildPath("", 0, outer.Id, 1)]);
        var player = new AnimationPlayer(Context(package), 0) { ExecutionPolicy = policy };
        var frame = player.EvaluateForTest(.1);
        Assert.True(frame.DependencyBlocked); Assert.False(player.IsComplete);
        Assert.Null(frame.Trace.Single(t => t.Event == outer.Id).End);
        Assert.Null(frame.Trace.Single(t => t.Event == inner.Id).End);
        Assert.Equal(AnimationDurationKind.DependencyBlocked, player.MeasureDuration(TestContext.Current.CancellationToken).Kind);
    }

    [Fact]
    public void ExcludedNonwaitingCallAdvancesWithoutCreatingAChild()
    {
        var package = Fixture(); var root = package.Entries[0]; var child = PolicyEntry(package, "child");
        child.Sequences[0].Events.Add(PolicyMotion(3));
        var call = Child(24, child.Name, 1);
        var move = AnimationCatalog.Create(7); move.SetShort(28, 1); move.SetVector(16, new(9, 0, 0));
        root.Sequences[0].Events.AddRange([call, move]);
        var player = new AnimationPlayer(Context(package), 0) { ExecutionPolicy = AnimationExecutionPolicy.RootOnly };
        var frame = player.EvaluateForTest(.1);
        Assert.True(player.IsComplete); Assert.True(frame.HasExcludedCalls); Assert.False(frame.DependencyBlocked);
        Assert.Single(frame.Instances); Assert.Equal(9, frame.Nodes[0].Transform.M41);
        Assert.Equal("excluded", frame.Trace.Single(t => t.Event == call.Id).Disposition);
        Assert.Contains("excluded", player.MeasureDuration(TestContext.Current.CancellationToken).Explanation);
    }

    [Fact]
    public void InclusionIsPerPathAndAudioDoesNotWalkExcludedDescendants()
    {
        var package = Fixture(); var root = package.Entries[0];
        var child = PolicyEntry(package, "coordinator"); var leaf = PolicyEntry(package, "leaf");
        var outer = Child(24, child.Name, 1); var inner = Child(24, leaf.Name, 2);
        root.Sequences[0].Events.Add(outer); child.Sequences[0].Events.AddRange([inner, PolicyMotion(3)]);
        leaf.Sequences[0].Events.Add(PolicyMotion(3));
        child.Primary.Events.Add(SoundNode("coordinator-cleanup")); leaf.Primary.Events.Add(SoundNode("leaf-cleanup"));
        string outerPath = AnimationExecutionPolicy.ChildPath("", 0, outer.Id, 1);
        string innerPath = AnimationExecutionPolicy.ChildPath(outerPath, 1, inner.Id, 2);
        var parentOnly = new AnimationExecutionPolicy([outerPath]);
        var omitted = new AnimationPlayer(Context(package), 0) { ExecutionPolicy = parentOnly }.EvaluateForTest(.1);
        Assert.Equal("excluded", omitted.Trace.Single(t => t.Event == inner.Id).Disposition);
        Assert.Equal(outerPath, omitted.Instances.Single(i => i.Entry == 1).Path);
        Assert.DoesNotContain(omitted.Instances, i => i.Entry == 2);
        Assert.Equal(["coordinator-cleanup"], AnimationAudioDependencies.Collect(package, 0, parentOnly, TestContext.Current.CancellationToken).Names);
        var complete = new AnimationExecutionPolicy([outerPath, innerPath]);
        var player = new AnimationPlayer(Context(package), 0) { ExecutionPolicy = complete };
        var admitted = player.EvaluateForTest(1.2);
        var leafInstance = admitted.Instances.Single(i => i.Entry == 2);
        Assert.Equal(innerPath, leafInstance.Path);
        Assert.Equal(admitted.Instances.Single(i => i.Entry == 1).Instance, leafInstance.ParentInstance);
        Assert.Equal(new[] { "coordinator-cleanup", "leaf-cleanup" }, AnimationAudioDependencies.Collect(package, 0, complete, TestContext.Current.CancellationToken).Names.Order());
        // Every call but an excluded one (the isolated preview's switches): its sounds are not prepared either.
        Assert.Equal(["coordinator-cleanup"], AnimationAudioDependencies.Collect(package, 0, AnimationExecutionPolicy.AllCalls(excludedCalls: [innerPath]), TestContext.Current.CancellationToken).Names);
        player.EvaluateForTest(.2, true); var replay = player.EvaluateForTest(1.2, true);
        Assert.Equal(admitted.Trace, replay.Trace); Assert.Equal(admitted.Instances, replay.Instances);
        Assert.Equal(admitted.Nodes, replay.Nodes);
    }

    // Must-have: a call cycle under every-call admission (the isolated preview) ends at the path allowance; the call past it
    // is excluded as too deep, never run with the opened binding's own path "", and Timing folds the cycle into its groups.
    [Fact]
    public void CallCycleUnderAllCallsStopsAtThePathAllowance()
    {
        var package = Fixture(); var ping = PolicyEntry(package, "ping"); var pong = PolicyEntry(package, "pong");
        ping.Sequences[0].Events.AddRange([PolicyMotion(.02f), Child(24, pong.Name, 2)]);
        pong.Sequences[0].Events.AddRange([PolicyMotion(.02f), Child(24, ping.Name, 1)]);
        var player = new AnimationPlayer(Context(package), 1) { ExecutionPolicy = AnimationExecutionPolicy.AllCalls() };
        long root = player.EvaluateForTest(0).Instances.Single().Instance;
        HashSet<long> launched = [];
        AnimationFrame frame = null!;
        for (double t = .5; t <= 10; t += .5)
        {
            frame = player.EvaluateForTest(t);
            Assert.DoesNotContain(frame.Instances, i => i.ParentInstance != null && i.Path.Length == 0);
            Assert.All(frame.Trace.Where(e => e.InstancePath.Length == 0), e => Assert.Equal(root, e.Instance));
            launched.UnionWith(frame.Trace.Where(e => e.Disposition == "launched").Select(e => e.ChildInstance!.Value));
        }
        var deep = Assert.Single(frame.Trace, e => e.Disposition == "excluded");
        Assert.StartsWith(AnimationExecutionPolicy.CallPathTooDeep, deep.Status);
        Assert.Equal(AnimationExecutionPolicy.MaximumPathCharacters / 37, launched.Count);
        Assert.True(player.IsComplete);
        var timing = AnimationTiming.Create(frame, package, new() { Starts = [new(Guid.Empty, 1, 0)] }, TestContext.Current.CancellationToken);
        Assert.Equal(2, timing.AllLanes.Count(l => l.Kind == AnimationTimingLaneKind.Group));
        Assert.All(timing.AllLanes, l => Assert.InRange(l.Id.Length, 1, 64));
        Assert.Contains(timing.Log, r => r.Result == "excluded · call path too deep");
    }

    [Fact]
    public void IncludedWaitedDuplicateDoesNotJoinTheExistingInstance()
    {
        var package = Fixture(); var root = package.Entries[0]; var child = PolicyEntry(package, "child");
        child.Sequences[0].Events.Add(PolicyMotion(3));
        var first = Child(24, child.Name, 1); var duplicate = Child(24, child.Name, 1); duplicate.SetShort(46, 16);
        var marker = AnimationCatalog.Create(35); marker.SetInt(12, 17);
        root.Sequences[0].Events.AddRange([first, duplicate, marker]);
        var policy = new AnimationExecutionPolicy([
            AnimationExecutionPolicy.ChildPath("", 0, first.Id, 1), AnimationExecutionPolicy.ChildPath("", 0, duplicate.Id, 1)]);
        var context = Context(package);
        context.Scene.Nodes[0] = context.Scene.Nodes[0] with { Parents = [1] };
        context.Scene.Nodes.Add(new(1, "world", "world", null, [], [0], new(), new()));
        var frame = new AnimationPlayer(context, 0) { ExecutionPolicy = policy }.EvaluateForTest(.1);
        var launched = frame.Trace.Single(t => t.Event == first.Id); var suppressed = frame.Trace.Single(t => t.Event == duplicate.Id);
        Assert.Equal("duplicate", suppressed.Disposition); Assert.Null(suppressed.ChildInstance);
        Assert.Equal(launched.ChildInstance, suppressed.ExistingInstance);
        Assert.Contains(frame.Trace, t => t.Event == marker.Id); Assert.False(frame.DependencyBlocked);
    }

    [Fact]
    public void IncludedUnresolvedWaitKeepsUnavailableAdvanceBehavior()
    {
        var package = Fixture(); var call = Child(24, "absent", -1); call.SetShort(46, 16);
        var marker = AnimationCatalog.Create(35); marker.SetInt(12, 17);
        package.Entries[0].Sequences[0].Events.AddRange([call, marker]);
        var policy = new AnimationExecutionPolicy([AnimationExecutionPolicy.ChildPath("", 0, call.Id, -1)]);
        var player = new AnimationPlayer(Context(package), 0) { ExecutionPolicy = policy };
        var frame = player.EvaluateForTest(.1);
        Assert.Equal("unresolved", frame.Trace.Single(t => t.Event == call.Id).Disposition);
        Assert.Contains(frame.Trace, t => t.Event == marker.Id); Assert.False(frame.DependencyBlocked);
        Assert.Equal(AnimationDurationKind.Unavailable, player.MeasureDuration(TestContext.Current.CancellationToken).Kind);
    }

    [Fact]
    public void LaunchAndConditionObservationsUseTheDispatchTimeWithinATick()
    {
        var package = Fixture(); var child = PolicyEntry(package, "child"); child.Sequences[0].Events.Add(PolicyMotion(3));
        var call = Child(24, child.Name, 1);
        var condition = AnimationCatalog.Create(31); condition.SetInt(12, 4); condition.SetInt(20, 1);
        package.Entries[0].Sequences[0].Events.AddRange([PolicyMotion(.005f), call, condition, AnimationCatalog.Create(34)]);
        var policy = new AnimationExecutionPolicy([AnimationExecutionPolicy.ChildPath("", 0, call.Id, 1)]);
        var frame = new AnimationPlayer(Context(package), 0) { ExecutionPolicy = policy }.Step(TestContext.Current.CancellationToken);
        var launch = frame.Trace.Single(t => t.Event == call.Id);
        Assert.InRange(launch.Start, .00499, .00501);
        Assert.Equal(launch.Start, frame.Instances.Single(i => i.Entry == 1).StartedAt);
        Assert.Equal(frame.Trace.Single(t => t.Event == condition.Id).Start, Assert.Single(frame.Conditions).Time);
    }

    [Fact]
    public void ScopedConditionOverrideIsObservedAndReplayedWithoutChangingSource()
    {
        var package = Fixture(); var condition = AnimationCatalog.Create(31); condition.SetInt(12, 1); condition.SetFloat(20, 0);
        var move = AnimationCatalog.Create(7); move.SetShort(28, 1); move.SetVector(16, new(4, 0, 0));
        package.Entries[0].Sequences[0].Events.AddRange([condition, move, AnimationCatalog.Create(34), PolicyMotion(3)]);
        var policy = new AnimationExecutionPolicy(conditionOverrides: [new("", 0, condition.Id, true)]);
        var player = new AnimationPlayer(Context(package), 0) { ExecutionPolicy = policy };
        var frame = player.EvaluateForTest(1.2);
        var result = Assert.Single(frame.Conditions); Assert.True(result.Value); Assert.True(result.Forced);
        Assert.Equal(condition.Id, result.Event); Assert.Contains(frame.Trace, t => t.Event == move.Id);
        player.EvaluateForTest(0, true); Assert.Empty(player.Frame(TestContext.Current.CancellationToken).Conditions);
        var replay = player.EvaluateForTest(1.2, true); Assert.Equal(frame.Conditions, replay.Conditions); Assert.Equal(frame.Trace, replay.Trace);
    }

    private static AnimationEntry PolicyEntry(AnimationPackage package, string name)
    {
        var original = package.Entries[0]; var entry = new AnimationEntry((byte[])original.Bytes.Clone(), package.Entries.Count, -1);
        entry.SetText(0, name); entry.SetFloat(164, -1);
        for (int table = 0; table < 8; table++) foreach (var record in original.References[table]) entry.References[table].Add(new((byte[])record.Bytes.Clone()));
        entry.Sequences.Add(new(new byte[64]) { Name = name }); package.Entries.Add(entry); return entry;
    }
    private static AnimationEvent PolicyMotion(float duration)
    {
        var motion = AnimationCatalog.Create(11); motion.SetInt(12, 1); motion.SetInt(16, -100);
        motion.SetVector(56, Vector3.UnitX); motion.SetVector(44, new(duration, 0, 0)); motion.SetFloat(140, duration); return motion;
    }
}
