using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

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
        Assert.Equal(AnimationDurationKind.Finite, duration.Kind); Assert.Equal(frames, duration.Frames);
        Assert.Equal(before.Time, player.Time); Assert.Equal(before.Nodes, player.Frame().Nodes);
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
        Assert.True(duration.IsFinite); Assert.InRange(duration.Frames, 179, 181);
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
        Assert.True(runtime.IsFinite); Assert.InRange(runtime.Frames, 178, 184);
        Assert.Equal(60, new AnimationPlayer(context, 0, resetPhase: true).MeasureDuration(TestContext.Current.CancellationToken).Frames);
    }

    [Fact]
    public void FiniteLoopsAndControllersAreNotMistakenForIndefinitePrograms()
    {
        var package = Fixture(); var entry = package.Entries[0];
        var motion = AnimationCatalog.Create(11); motion.SetInt(16, 1); motion.SetFloat(140, .5f);
        var loop = AnimationCatalog.Create(30); loop.SetInt(12, 1); loop.SetInt(16, 3);
        entry.Sequences[0].Events.AddRange([motion, loop]);
        var finite = new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(finite.IsFinite); Assert.InRange(finite.Frames, 90, 94);
        loop.SetInt(16, 65535);
        Assert.Equal(AnimationDurationKind.Looping, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
        var controller = new AnimationSequence(new byte[64]); controller.Name = "stopper";
        var stop = AnimationCatalog.Create(23); stop.SetInt(44, 0); stop.Threshold = 2; controller.Events.Add(stop); entry.Sequences.Add(controller);
        var stopped = new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(stopped.IsFinite); Assert.InRange(stopped.Frames, 119, 121);
    }

    [Fact]
    public void WaitingUnboundedAndUnsupportedProgramsHaveExplicitPreviewRanges()
    {
        var package = Fixture(); var sequence = package.Entries[0].Sequences[0];
        var motion = AnimationCatalog.Create(10); motion.SetInt(16, 1); motion.SetInt(12, 4); sequence.Events.Add(motion);
        Assert.Equal(AnimationDurationKind.OpenEnded, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
        sequence.ResetMode = 3;
        Assert.Equal(AnimationDurationKind.OpenEnded, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
        sequence.ResetMode = 0; motion.Bytes[0] = 255;
        Assert.Equal(AnimationDurationKind.Unavailable, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
        package.Entries[0].SetFloat(164, 0); // Cleanup replaces the blocked runtime sequence.
        Assert.Equal(AnimationDurationKind.Unavailable, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
    }

    [Fact]
    public void SnapshotProtectsDurationFromLaterEditsAndAnalysisHonorsCancellation()
    {
        var package = Fixture(); var motion = AnimationCatalog.Create(11); motion.SetInt(16, 1); package.Entries[0].Sequences[0].Events.Add(motion);
        var snapshot = Context(package).Snapshot(); motion.SetFloat(140, 12);
        var player = new AnimationPlayer(snapshot, 0);
        Assert.Equal(60, player.MeasureDuration(TestContext.Current.CancellationToken).Frames);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => player.MeasureDuration(cancel.Token));
    }

    [Fact]
    public void DurationFollowsTheSelectedConditionalBranch()
    {
        var package = Fixture(); var events = package.Entries[0].Sequences[0].Events;
        var shortMotion = AnimationCatalog.Create(11); shortMotion.SetInt(16, 1);
        var longMotion = shortMotion.Duplicate(); longMotion.SetFloat(140, 3);
        events.AddRange([AnimationCatalog.Create(31), shortMotion, AnimationCatalog.Create(32), longMotion, AnimationCatalog.Create(34)]);
        Assert.Equal(60, new AnimationPlayer(Context(package), 0) { ConditionOverride = true }.MeasureDuration(TestContext.Current.CancellationToken).Frames);
        Assert.Equal(180, new AnimationPlayer(Context(package), 0) { ConditionOverride = false }.MeasureDuration(TestContext.Current.CancellationToken).Frames);
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
        Assert.True(duration.IsFinite); Assert.InRange(duration.Frames, 120, 122);
        launch.SetShort(48, 0); launch.SetText(12, "missing", 20);
        Assert.Equal(AnimationDurationKind.Unavailable, new AnimationPlayer(Context(package), 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
    }
}
