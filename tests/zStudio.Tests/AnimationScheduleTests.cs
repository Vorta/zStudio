using System.Numerics;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void HiddenRuntimeSurfaceStopsCollidingImmediately()
    {
        var (context, motion) = GroundFixture(10); AddFloor(context, 0); AddFloor(context, 8);
        var top = context.Scene.Nodes[^1]; context.Scene.Nodes[^1] = top with { Name = "healthy" };
        var entry = context.Package.Entries[0];
        int reference = entry.References[1].Count;
        var record = new AnimationRecord(new byte[40]); record.SetText(0, "healthy", 36); entry.References[1].Add(record);
        var hide = AnimationCatalog.Create(6); hide.SetInt(12, 0); hide.SetShort(16, (short)reference);
        entry.Sequences[0].Events.Insert(0, hide);
        motion.SetVector(64, new(0, -20, 0));
        var player = new AnimationPlayer(context, 0) { Terrain = new(context.Scene), OriginCollision = true };
        var end = player.EvaluateForTest(2);
        Assert.False(end.Nodes.Single(n => n.SourceNode == top.Index).Visible);
        Assert.InRange(end.Nodes.Single(n => n.SourceNode == 0).Transform.M42, -.001f, .001f);
    }

    // Protect the reproduced bridge regression: mesh contact must not release origin-based impacts,
    // and a scripted sink after impact must not be held up by the old support latch.
    [Fact]
    public void OriginContactWaitsForTheOriginAndAllowsAuthoredSinking()
    {
        var (context, motion) = GroundFixture(11); AddFloor(context, 10);
        var entry = context.Package.Entries[0]; entry.Sequences[0].Name = "fall";
        motion.SetInt(12, 0x805); motion.SetText(208, "landed"); motion.SetShort(240, 1);
        var landed = new AnimationSequence(new byte[64]) { Name = "landed", ResetMode = 3 };
        var stop = AnimationCatalog.Create(23); stop.SetText(12, "fall");
        var sink = AnimationCatalog.Create(10); sink.SetInt(12, 0x404); sink.SetInt(16, 1); sink.SetVector(64, new(0, -2, 0)); sink.SetFloat(248, 1);
        landed.Events.AddRange([stop, sink]); entry.Sequences.Add(landed);
        var player = new AnimationPlayer(context, 0) { Terrain = new(context.Scene), OriginCollision = true };
        Assert.DoesNotContain(player.EvaluateForTest(.05).Trace, t => t.Event == sink.Id);
        var end = player.EvaluateForTest(2);
        Assert.InRange(end.Trace.Single(t => t.Event == sink.Id).Start, .4, .6);
        Assert.InRange(end.Nodes.Single(n => n.SourceNode == 0).Transform.M42, 7.8f, 8.1f);
        player.Reset(TestContext.Current.CancellationToken); Assert.Equal(end.Nodes, player.EvaluateForTest(2, true).Nodes);
    }

    // Scheduled water and destruction share morphs and final poses, including across rewind/reset.
    [Fact]
    public void ScheduledSurfaceChangesContactTimeAndKeepsItsCompletedPose()
    {
        var (context, motion) = GroundFixture(10); AddFloor(context, 0);
        var water = PolicyEntry(context.Package, "water"); water.SetText(32, "floor");
        var model = context.Scene.Models[^1]; context.Scene.Models[^1] = model with { Morphs = model.Vertices.Select(_ => new Vector3(0, 4, 0)).ToArray() };
        var morph = AnimationCatalog.Create(11); morph.SetInt(12, 8); morph.SetInt(16, -100);
        morph.SetFloat(24, 1); morph.SetFloat(28, .5f); morph.SetFloat(140, 2); water.Sequences[0].Events.Add(morph);
        motion.SetInt(12, 0x805); motion.SetText(208, "landed"); motion.SetShort(240, 1);
        var landed = new AnimationSequence(new byte[64]) { Name = "landed", ResetMode = 3 };
        var mark = AnimationCatalog.Create(35); landed.Events.Add(mark); context.Package.Entries[0].Sequences.Add(landed);
        double? previousFall = null;
        foreach (double start in new[] { 0d, 1d, 3d })
        {
            var schedule = new AnimationSchedule([new(Guid.NewGuid(), water.Index, 0), new(Guid.Empty, 0, start)], context.Package.Entries.Count);
            var player = new AnimationPlayer(context, 0, 1, false, TestContext.Current.CancellationToken, schedule)
                { OriginCollision = true, Terrain = new(context.Scene) };
            var end = player.EvaluateForTest(start + 3);
            double fall = end.Trace.First(t => t.Event == mark.Id).Start - start;
            if (previousFall.HasValue) Assert.True(fall < previousFall.Value, $"Expected higher water to shorten the fall: {fall} vs {previousFall}");
            previousFall = fall;
            Assert.Equal(1, end.Nodes.Single(n => n.SourceNode == context.Scene.Nodes.Count - 1).Morph);
            player.EvaluateForTest(.1, true); var replay = player.EvaluateForTest(start + 3, true);
            Assert.Equal(end.Nodes, replay.Nodes); Assert.Equal(end.Trace, replay.Trace);
            player.Reset(TestContext.Current.CancellationToken); Assert.Equal(end.Trace, player.EvaluateForTest(start + 3, true).Trace);
        }
    }

    // Empty intervals and scoped opt-in calls must survive duration probing and delayed activation.
    [Fact]
    public void DelayedScheduleWaitsAndIncludesChildrenOnlyAtTheirAuthoredCall()
    {
        var package = Fixture(); var child = PolicyEntry(package, "child"); child.Sequences[0].Events.Add(PolicyMotion(.2f));
        var call = Child(24, child.Name, (short)child.Index); call.Threshold = .5f; package.Entries[0].Sequences[0].Events.Add(call);
        var schedule = new AnimationSchedule([new(Guid.Empty, 0, 2)], package.Entries.Count);
        var policy = new AnimationExecutionPolicy([AnimationExecutionPolicy.ChildPath("", 0, call.Id, child.Index)]);
        var player = new AnimationPlayer(Context(package), 0, 1, false, TestContext.Current.CancellationToken, schedule) { ExecutionPolicy = policy };
        Assert.Empty(player.EvaluateForTest(1).Instances); Assert.False(player.IsComplete);
        var duration = player.MeasureDuration(TestContext.Current.CancellationToken);
        Assert.Equal(AnimationDurationKind.Finite, duration.Kind); Assert.True(duration.Seconds > 2.5);
        Assert.DoesNotContain(player.EvaluateForTest(2.2).Trace, t => t.Event == call.Id);
        var end = player.EvaluateForTest(3);
        Assert.InRange(end.Trace.Single(t => t.Event == call.Id).Start, 2.48, 2.52);
        player.Reset(TestContext.Current.CancellationToken); Assert.Equal(end.Trace, player.EvaluateForTest(3, true).Trace);
    }
}
