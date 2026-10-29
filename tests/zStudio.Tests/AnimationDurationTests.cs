using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    [Theory]
    [InlineData(.25f, 15)]
    [InlineData(1f, 60)]
    [InlineData(3f, 180)]
    public void DurationUsesTimedMotionWithoutPaddingOrChangingPlayback(float seconds, int frames)
    {
        var package = Fixture();
        var motion = AnimationCatalog.Create(11); motion.SetInt(16, 1); motion.SetFloat(140, seconds);
        package.Entries[0].Sequences[0].Events.Add(motion);
        byte[] source = Pack(package);
        var player = new AnimationPlayer(Context(package), 0);
        var before = player.EvaluateForTest(.1);
        var duration = player.MeasureDuration(TestContext.Current.CancellationToken);
        Assert.Equal(AnimationDurationKind.Finite, duration.Kind); Assert.Equal(frames, duration.Ticks);
        Assert.Equal(before.Time, player.Time); Assert.Equal(before.Nodes, player.Frame(TestContext.Current.CancellationToken).Nodes);
        Assert.Equal(source, Pack(package));
    }

    [Fact]
    public void DurationUsesLongestConcurrentSequenceAndItsThreshold()
    {
        var package = Fixture(); var entry = package.Entries[0];
        var shortMotion = AnimationCatalog.Create(11); shortMotion.SetInt(16, 1); shortMotion.SetFloat(140, .25f);
        entry.Sequences[0].Events.Add(shortMotion);
        var longMotion = AnimationCatalog.Create(11); longMotion.SetInt(16, 1); longMotion.SetFloat(140, 2); longMotion.Threshold = 1;
        var second = new AnimationSequence(new byte[64]); second.Name = "second"; second.Events.Add(longMotion); entry.Sequences.Add(second);
        var duration = new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(duration.IsFinite); Assert.InRange(duration.Ticks, 179, 181);
    }

    [Fact]
    public void DurationIncludesRelativeWaitKeyframesAndCleanup()
    {
        var package = Fixture(); var entry = package.Entries[0];
        var ev = AnimationCatalog.Create(12); ev.SetInt(12, 1);
        var key = AnimationKeyframe.Create(); key.End = 1; ev = ev.WithKeyframes([key]); entry.Sequences[0].Events.Add(ev);
        var hide = AnimationCatalog.Create(6); hide.StartMode = 3; hide.Threshold = .5f; entry.Sequences[0].Events.Add(hide);
        entry.SetFloat(164, .5f);
        var cleanup = AnimationCatalog.Create(11); cleanup.SetInt(16, 1); cleanup.SetFloat(140, 1); entry.Primary.Events.Add(cleanup);
        var context = Context(package);
        var runtime = new AnimationPlayer(context, 0).MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(runtime.IsFinite); Assert.InRange(runtime.Ticks, 178, 184);
        Assert.Equal(60, new AnimationPlayer(context, 0, resetPhase: true).MeasureDuration(TestContext.Current.CancellationToken).Ticks);
    }

    // The Auto range's rules (owner decision; docs/agents/preview.md, "Animation runtime and duration").
    [Fact]
    public void AutoRangeEndsLoopsAfterOneIterationFallsFiveSecondsAfterTheLastTimedEventAndStartsAtTheLatestEnd()
    {
        var token = TestContext.Current.CancellationToken;
        // A loop that repeats forever ends where it has gone round once (a 6 s pass, then LOOP).
        var package = Fixture(); var entry = package.Entries[0];
        var motion = AnimationCatalog.Create(11); motion.SetInt(16, 1); motion.SetFloat(140, 6);
        var loop = AnimationCatalog.Create(30); loop.SetInt(12, 1); loop.SetInt(16, 65535);
        entry.Sequences[0].Events.AddRange([motion, loop]);
        var looping = new AnimationPlayer(Context(package), 0).MeasureDuration(token);
        Assert.Equal(AnimationDurationKind.Looping, looping.Kind); Assert.InRange(looping.Ticks, 360, 362);
        Assert.Equal("Loops: motion once round", looping.Explanation); Assert.Null(looping.LastTimedSeconds);
        // A one-tick loop (one instant event, then LOOP) started at 20 s is shown for 1 s from its pass.
        entry.Sequences[0].Events.Remove(motion); entry.Sequences[0].Events.Insert(0, AnimationCatalog.Create(6));
        var late = new AnimationPlayer(Context(package), 0, 1, false, token, new AnimationSchedule([new(Guid.Empty, 0, 20)], package.Entries.Count)).MeasureDuration(token);
        Assert.Equal(AnimationDurationKind.Looping, late.Kind); Assert.InRange(late.Ticks, 1259, 1261); Assert.Contains("at least 1 s", late.Explanation);

        // Motion without a timed end (a fall onto the ground) goes on: 5 s after the last timed event (6.1 s), whatever comes later.
        var (context, _) = GroundFixture(500);
        var later = new AnimationSequence(new byte[64]) { Name = "later" };
        var mark = AnimationCatalog.Create(6); mark.SetShort(16, 1); mark.SetInt(12, 1); mark.Threshold = 6.1f; later.Events.Add(mark);
        context.Package.Entries[0].Sequences.Add(later);
        var player = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        var falling = player.MeasureDuration(token);
        Assert.Equal(AnimationDurationKind.OpenEnded, falling.Kind); Assert.InRange(falling.LastTimedSeconds!.Value, 6.08, 6.12);
        Assert.InRange(falling.Seconds, 11.08, 11.14); Assert.Contains("a piece falls until it hits the ground", falling.Explanation);
        // The same answer from a played and sought player: the analysis is deterministic and never moves the playhead.
        player.EvaluateForTest(8); player.EvaluateForTest(2, true);
        Assert.Equal(falling, player.MeasureDuration(token)); Assert.Equal(2, player.Time, 6);
        // A landing (at about 0.9 s) ends only the fall: the event after it that its animation clock starts at 3 s keeps its time,
        // so the run ends there; one that waits 2 s after the landing follows the landing (the fall, 5 s after its start).
        var (landing, _) = GroundFixture();
        var shown = AnimationCatalog.Create(6); shown.SetShort(16, 1); shown.Threshold = 3; landing.Package.Entries[0].Sequences[0].Events.Add(shown);
        var landed = new AnimationPlayer(landing, 0) { GroundPlaneEnabled = true }.MeasureDuration(token);
        Assert.Equal(AnimationDurationKind.Finite, landed.Kind); Assert.InRange(landed.Ticks, 178, 181);
        shown.StartMode = 3; shown.Threshold = 2;
        var afterLanding = new AnimationPlayer(landing, 0) { GroundPlaneEnabled = true }.MeasureDuration(token);
        Assert.Equal(AnimationDurationKind.OpenEnded, afterLanding.Kind); Assert.Equal(0, afterLanding.LastTimedSeconds);
        Assert.Contains("a piece falls until it hits the ground", afterLanding.Explanation);
        // Only a dispatch counts: in an IF's taken body that event keeps its time; in its skipped body (IF false) the run never
        // starts it, and the fall's 5 s rule end stands.
        var (branch, _) = GroundFixture(); var test = AnimationCatalog.Create(31); test.SetInt(12, 1);
        var inside = AnimationCatalog.Create(6); inside.SetShort(16, 1); inside.Threshold = 3;
        branch.Package.Entries[0].Sequences[0].Events.AddRange([test, inside, AnimationCatalog.Create(34)]);
        AnimationDuration Branch(bool value) => new AnimationPlayer(branch, 0)
            { GroundPlaneEnabled = true, ExecutionPolicy = new(conditionOverrides: [new("", 0, test.Id, value)]) }.MeasureDuration(token);
        var taken = Branch(true);
        Assert.Equal(AnimationDurationKind.Finite, taken.Kind); Assert.InRange(taken.Ticks, 178, 181);
        var skipped = Branch(false);
        Assert.Equal(AnimationDurationKind.OpenEnded, skipped.Kind); Assert.Equal(300, skipped.Ticks); Assert.Equal(0, skipped.LastTimedSeconds);
        Assert.Contains("a piece falls until it hits the ground", skipped.Explanation);

        // A sequence waiting for a release holds Auto open only while its binding runs: once the binding has ended (a 1 s motion,
        // then nothing) nothing can release it; a binding a polling loop keeps going still waits for it.
        var waits = Fixture(); var waiter = waits.Entries[0];
        var moved = AnimationCatalog.Create(11); moved.SetInt(16, 1); moved.SetFloat(140, 1); waiter.Sequences[0].Events.Add(moved);
        var waiting = new AnimationSequence(new byte[64]) { Name = "waiting", ResetMode = 3 }; waiting.Events.Add(AnimationCatalog.Create(6)); waiter.Sequences.Add(waiting);
        var ended = new AnimationPlayer(Context(waits), 0).MeasureDuration(token);
        Assert.Equal(AnimationDurationKind.Finite, ended.Kind); Assert.Equal(60, ended.Ticks);
        var poll = new AnimationSequence(new byte[64]) { Name = "poll" }; var again = AnimationCatalog.Create(30); again.SetInt(16, 65535);
        poll.Events.AddRange([AnimationCatalog.Create(6), again]); waiter.Sequences.Add(poll);
        var polled = new AnimationPlayer(Context(waits), 0).MeasureDuration(token);
        Assert.Equal(AnimationDurationKind.OpenEnded, polled.Kind); Assert.InRange(polled.LastTimedSeconds!.Value, .99, 1.01);
        Assert.Contains("waiting waits for a release", polled.Explanation);

        // Several starts: the latest end (a 2 s motion started at 10 s, after the primary's 1 s).
        var starts = Fixture(); var first = AnimationCatalog.Create(11); first.SetInt(16, 1); first.SetFloat(140, 1); starts.Entries[0].Sequences[0].Events.Add(first);
        var second = PolicyEntry(starts, "second"); second.Sequences[0].Events.Add(PolicyMotion(2));
        var schedule = new AnimationSchedule([new(Guid.Empty, 0, 0), new(Guid.NewGuid(), second.Index, 10)], starts.Entries.Count);
        var both = new AnimationPlayer(Context(starts), 0, 1, false, token, schedule).MeasureDuration(token);
        Assert.Equal(AnimationDurationKind.Finite, both.Kind); Assert.InRange(both.Ticks, 719, 722);
    }

    [Fact]
    public void FiniteLoopsAndControllersAreNotMistakenForIndefinitePrograms()
    {
        var package = Fixture(); var entry = package.Entries[0];
        var motion = AnimationCatalog.Create(11); motion.SetInt(16, 1); motion.SetFloat(140, .5f);
        var loop = AnimationCatalog.Create(30); loop.SetInt(12, 1); loop.SetInt(16, 3);
        entry.Sequences[0].Events.AddRange([motion, loop]);
        var finite = new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(finite.IsFinite); Assert.InRange(finite.Ticks, 90, 94);
        loop.SetInt(16, 65535);
        Assert.Equal(AnimationDurationKind.Looping, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
        var controller = new AnimationSequence(new byte[64]); controller.Name = "stopper";
        var stop = AnimationCatalog.Create(23); stop.SetInt(44, 0); stop.Threshold = 2; controller.Events.Add(stop); entry.Sequences.Add(controller);
        var stopped = new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(stopped.IsFinite); Assert.InRange(stopped.Ticks, 119, 121);
    }

    [Fact]
    public void WaitingUnboundedAndUnsupportedProgramsHaveExplicitPreviewRanges()
    {
        var package = Fixture(); var sequence = package.Entries[0].Sequences[0];
        var motion = AnimationCatalog.Create(10); motion.SetInt(16, 1); motion.SetInt(12, 4); sequence.Events.Add(motion);
        Assert.Equal(AnimationDurationKind.OpenEnded, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
        // A binding whose one sequence waits for a release ends at once: nothing is left to release it.
        sequence.ResetMode = 3;
        Assert.Equal(AnimationDurationKind.Finite, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
        sequence.ResetMode = 0; motion.Bytes[0] = 255;
        Assert.Equal(AnimationDurationKind.Unavailable, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
        package.Entries[0].SetFloat(164, 0); // Cleanup replaces the blocked runtime sequence.
        Assert.Equal(AnimationDurationKind.Unavailable, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
    }

    [Fact]
    public void SnapshotProtectsDurationFromLaterEditsAndAnalysisHonorsCancellation()
    {
        var package = Fixture(); var motion = AnimationCatalog.Create(11); motion.SetInt(16, 1); package.Entries[0].Sequences[0].Events.Add(motion);
        var context = Context(package);
        // Another program's limit while the mission was prepared is inherited as a note, not this program's limit.
        context.Diagnostics.Add("Mission initialization: Preview instance limit reached (256). A looping emitter may be producing too many children.");
        var snapshot = context.Snapshot(); motion.SetFloat(140, 12);
        var player = new AnimationPlayer(snapshot, 0);
        var duration = player.MeasureDuration(TestContext.Current.CancellationToken);
        Assert.Equal(AnimationDurationKind.Finite, duration.Kind); Assert.Equal(60, duration.Ticks);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => player.MeasureDuration(cancel.Token));
    }

    [Fact]
    public void DurationFollowsTheSelectedConditionalBranch()
    {
        var package = Fixture(); var events = package.Entries[0].Sequences[0].Events;
        var shortMotion = AnimationCatalog.Create(11); shortMotion.SetInt(16, 1);
        var longMotion = new AnimationEvent((byte[])shortMotion.Bytes.Clone()) { Version = shortMotion.Version }; longMotion.SetFloat(140, 3);
        var condition = AnimationCatalog.Create(31); condition.SetInt(12, 1);
        events.AddRange([condition, shortMotion, AnimationCatalog.Create(32), longMotion, AnimationCatalog.Create(34)]);
        int Ticks(bool value) => new AnimationPlayer(Context(package), 0) { ExecutionPolicy = new(conditionOverrides: [new("", 0, condition.Id, value)]) }
            .MeasureDuration(TestContext.Current.CancellationToken).Ticks;
        Assert.Equal(60, Ticks(true)); Assert.Equal(180, Ticks(false));
    }

    [Fact]
    public void NestedAnimationOutlivesItsLauncherAndMissingChildrenAreNotCalledComplete()
    {
        var package = Fixture(); var parent = package.Entries[0];
        var child = new AnimationEntry((byte[])parent.Bytes.Clone(), 1, -1); child.SetText(0, "child");
        var motion = AnimationCatalog.Create(11); motion.SetInt(16, -100); motion.SetFloat(140, 2);
        var sequence = new AnimationSequence(new byte[64]); sequence.Events.Add(motion); child.Sequences.Add(sequence); package.Entries.Add(child);
        var launch = AnimationCatalog.Create(24); launch.SetText(12, "child", 20); launch.SetShort(48, 1); parent.Sequences[0].Events.Add(launch);
        var duration = new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(duration.IsFinite); Assert.InRange(duration.Ticks, 120, 122);
        launch.SetShort(48, 0); launch.SetText(12, "missing", 20);
        Assert.Equal(AnimationDurationKind.Unavailable, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
    }
}
