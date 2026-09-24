using System.Numerics;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Theory]
    [InlineData(-1000)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(1000)]
    public void PreviewHeightRejectsInvalidRanges(float height)
    {
        var context = Context(Fixture());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnimationPlayer(context, 0) { PreviewHeight = height });
    }

    [Fact]
    public void HeightRaisesInitialPoseAndLengthensFallWithoutMovingTheGround()
    {
        var (context, _) = GroundFixture(); var source = Pack(context.Package);
        var original = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        var raised = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true, PreviewHeight = 40 };
        Assert.Equal(40, raised.Frame().Nodes[0].Transform.M42 - original.Frame().Nodes[0].Transform.M42);
        var duration = raised.MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(duration.IsFinite); Assert.True(duration.Seconds > original.MeasureDuration(TestContext.Current.CancellationToken).Seconds + 1);
        Assert.Equal(0, raised.Time);
        for (int i = 0; i < 600; i++) AssertGrounded(context, raised.Step(TestContext.Current.CancellationToken));
        Assert.True(raised.IsComplete);
        var end = raised.Frame(); Assert.InRange(end.Nodes[0].Transform.M42, .999f, 1.001f);
        raised.EvaluateForTest(.5, true); Assert.Equal(end.Nodes, raised.EvaluateForTest(10, true).Nodes);
        raised.Reset(); Assert.Equal(end.Nodes, raised.EvaluateForTest(10).Nodes);
        Assert.Equal(source, Pack(context.Package));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-30)]
    [InlineData(999)]
    [InlineData(-999)]
    public void HeightIsAddedOnceAfterAuthoredTransformsEffectsAndNestedLaunches(float height)
    {
        var package = Fixture(); var context = Context(package); var parent = package.Entries[0];
        var child = new AnimationEntry((byte[])parent.Bytes.Clone(), 1, -1); child.SetText(0, "child");
        var move = AnimationCatalog.Create(11); move.SetInt(16, -100); move.SetVector(32, new(2, 3, 4)); move.SetVector(56, new(0, 1, 0)); move.SetFloat(140, 2);
        var sequence = new AnimationSequence(new byte[64]); sequence.Events.Add(move); child.Sequences.Add(sequence); package.Entries.Add(child);
        var launch = AnimationCatalog.Create(24); launch.SetText(12, "child", 20); launch.SetShort(48, 1);
        parent.References[5].Add(new(new byte[36]));
        var reference = new AnimationRecord(new byte[36]); reference.SetText(0, "spark"); parent.References[5].Add(reference);
        context.Effects.Add("spark", new("spark", "animated", 0, [], 1, false));
        var effect = AnimationCatalog.Create(3); effect.SetShort(12, 1); effect.SetShort(14, 1); effect.SetVector(16, new(1, 2, 3));
        var light = AnimationCatalog.Create(4); light.SetInt(48, 2); light.SetInt(52, 1); light.SetInt(68, 1); light.SetVector(72, new(2, 3, 4));
        parent.Sequences[0].Events.AddRange([launch, effect, light, move.Duplicate()]);
        byte[] source = Pack(package);
        var baseline = new AnimationPlayer(context, 0).EvaluateForTest(.5);
        var raised = new AnimationPlayer(context, 0) { PreviewHeight = height }.EvaluateForTest(.5);
        Assert.True(baseline.Nodes.Count >= 3); Assert.NotEmpty(baseline.Lights);
        foreach (var pose in baseline.Nodes)
        {
            var expected = pose.Transform; expected.Translation += new Vector3(0, height, 0);
            Assert.Equal(expected, raised.Nodes.Single(n => n.Id == pose.Id).Transform);
        }
        Near(baseline.Lights[0].Position + new Vector3(0, height, 0), raised.Lights[0].Position);
        Assert.Equal(baseline.Trace, raised.Trace); Assert.Equal(source, Pack(package));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(-25)]
    public void PreviewHeightMovesCameraEyeAndTargetTogether(float height)
    {
        var package = Fixture(); var context = CameraContext(package);
        var keyEvent = AnimationCatalog.Create(12); keyEvent.SetInt(12, 1);
        var key = AnimationKeyframe.Create(3); key.End = 2;
        key.SetVector(key.ChannelOffset(0), new(100, 40, 200));
        key.SetVector(key.ChannelOffset(0) + 16, new(2, -1, 3));
        package.Entries[0].Sequences[0].Events.Add(keyEvent.WithKeyframes([key]));
        var baseline = new AnimationPlayer(context, 0).EvaluateForTest(1).Camera!;
        var raised = new AnimationPlayer(context, 0) { PreviewHeight = height }.EvaluateForTest(1).Camera!;
        Near(baseline.Position + new Vector3(0, height, 0), raised.Position);
        Near(baseline.Target + new Vector3(0, height, 0), raised.Target);
        Assert.Equal(baseline.FieldOfView, raised.FieldOfView);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-20)]
    public void NegativeHeightKeepsGroundAtZeroAndSeekingDeterministic(float height)
    {
        var (context, _) = GroundFixture(); var source = Pack(context.Package);
        var baseline = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        var lowered = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true, PreviewHeight = height };
        Assert.Equal(height, lowered.Frame().Nodes[0].Transform.M42 - baseline.Frame().Nodes[0].Transform.M42);
        var duration = lowered.MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(duration.IsFinite); Assert.True(duration.Seconds < baseline.MeasureDuration(TestContext.Current.CancellationToken).Seconds);
        Assert.Equal(0,lowered.Time);
        for (int i = 0; i < 180; i++) AssertGrounded(context,lowered.Step(TestContext.Current.CancellationToken));
        var end = lowered.Frame(); Assert.InRange(end.Nodes[0].Transform.M42,.999f,1.001f);
        lowered.EvaluateForTest(.5,true); Assert.Equal(end.Nodes,lowered.EvaluateForTest(3,true).Nodes);
        var ungrounded = new AnimationPlayer(context,0) { PreviewHeight = height }.EvaluateForTest(3);
        Assert.True(ungrounded.Nodes[0].Transform.M42 < 0);
        Assert.Equal(source,Pack(context.Package));
    }
}
