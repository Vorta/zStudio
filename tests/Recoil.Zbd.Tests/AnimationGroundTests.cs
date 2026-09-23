using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Theory]
    [InlineData(-5)]
    [InlineData(-2000)]
    public void GroundStopsMeshWithoutTunnelingAndDisabledMotionKeepsFalling(float velocity)
    {
        var (context, motion) = GroundFixture(); motion.SetVector(64, new(0, velocity, 0));
        var source = Pack(context.Package);
        var grounded = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        for (int i = 0; i < 180; i++) AssertGrounded(context, grounded.Step(TestContext.Current.CancellationToken));
        Assert.True(grounded.IsComplete);
        Assert.InRange(grounded.Frame().Nodes[0].Transform.M42, .999f, 1.001f);
        var falling = new AnimationPlayer(context, 0).EvaluateForTest(3);
        Assert.True(falling.Nodes[0].Transform.M42 < -10);
        Assert.Equal(source, Pack(context.Package));
    }

    [Fact]
    public void GroundContactIncludesSpinScaleMorphAndIgnoresUnusedVertices()
    {
        var (context, motion) = GroundFixture();
        var model = context.Scene.Models[0];
        context.Scene.Models[0] = model with { Vertices = [.. model.Vertices, new(0, -1000, 0)], Morphs = [new(0, -2, 0), new(0, -1, 0), Vector3.Zero, Vector3.Zero] };
        motion.SetInt(12, 0x725); motion.SetFloat(248, 3); motion.SetVector(136, new(0, 0, 2));
        motion.SetVector(172, new(.1f, .2f, .3f)); motion.SetFloat(28, .5f);
        var player = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        for (int i = 0; i < 180; i++) AssertGrounded(context, player.Step(TestContext.Current.CancellationToken));
        var pose = player.Frame().Nodes[0];
        Assert.True(pose.Morph > 0); Assert.InRange(pose.Transform.M42, 0, 6);
    }

    [Fact]
    public void InitialOverlapIsLiftedWithoutReleasingImpactSequenceOnUpwardLaunch()
    {
        var (context, motion) = GroundFixture(0); motion.SetVector(64, new(0, 12, 0)); motion.SetInt(12, 0x805);
        motion.SetShort(240, -1); motion.SetText(208, "landed");
        var waiting = new AnimationSequence(new byte[64]) { Name = "landed", ResetMode = 3 };
        var mark = AnimationCatalog.Create(14); mark.SetInt(32, 1); mark.SetFloat(24, 1); waiting.Events.Add(mark);
        context.Package.Entries[0].Sequences.Add(waiting);
        var player = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        var first = player.Step(TestContext.Current.CancellationToken); AssertGrounded(context, first);
        Assert.DoesNotContain(first.Trace, t => t.Event == mark.Id);
        var last = player.EvaluateForTest(4);
        Assert.Contains(last.Trace, t => t.Event == mark.Id);
    }

    [Fact]
    public void ImpactsReleaseWaitingSequencePlayPreloadedSampleAndSurviveSeekWithGain()
    {
        var (context, motion) = GroundFixture(1.01f); motion.SetVector(64, new(0, -20, 0)); motion.SetInt(12, 0x1805);
        motion.SetText(208, "impact"); motion.SetShort(240, -1); motion.SetShort(242, 1); motion.SetFloat(244, 100);
        var entry = context.Package.Entries[0]; entry.References[4].Add(new(new byte[36]));
        var sample = new AnimationRecord(new byte[36]); sample.SetText(0, "thud"); entry.References[4].Add(sample);
        var wav = new byte[44 + 16000];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0); BitConverter.GetBytes(wav.Length - 8).CopyTo(wav, 4);
        System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8); BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20); BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(8000).CopyTo(wav, 24); BitConverter.GetBytes(16000).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32); BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(wav, 36); BitConverter.GetBytes(16000).CopyTo(wav, 40);
        context.Sounds.Add("thud", new("thud", "thud.wav", false, wav));
        var waiting = new AnimationSequence(new byte[64]) { Name = "impact", ResetMode = 3 };
        var marker = AnimationCatalog.Create(6); marker.SetShort(16, 1); marker.SetInt(12, 1); waiting.Events.Add(marker); entry.Sequences.Add(waiting);
        Assert.Contains("thud", AnimationAudioDependencies.Collect(context.Package, 0, TestContext.Current.CancellationToken).Names);
        var player = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        var first = player.Step(TestContext.Current.CancellationToken); Assert.Single(first.Sounds); Assert.InRange(first.Sounds[0].Gain, .0399f, .0401f);
        Assert.Contains(first.Trace, t => t.Event == marker.Id);
        var after = player.EvaluateForTest(.4); Assert.NotEmpty(after.ActiveSounds);
        player.EvaluateForTest(0, true); var replay = player.EvaluateForTest(.4, true);
        Assert.Equal(after.Nodes, replay.Nodes); Assert.Equal(after.Trace, replay.Trace); Assert.Equal(after.ActiveSounds, replay.ActiveSounds);
        var disabled = new AnimationPlayer(context, 0).EvaluateForTest(.4); Assert.Empty(disabled.ActiveSounds); Assert.DoesNotContain(disabled.Trace, t => t.Event == marker.Id);
    }

    [Fact]
    public void GroundDurationUsesIndependentSimulationAndSeekCheckpointsAreStable()
    {
        var (context, _) = GroundFixture(); var player = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        var before = player.EvaluateForTest(.5); var bytes = Pack(context.Package);
        var duration = player.MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(duration.IsFinite); Assert.InRange(duration.Seconds, .5, 3);
        Assert.Equal(.5, player.Time); Assert.Equal(before.Nodes, player.Frame().Nodes); Assert.Equal(bytes, Pack(context.Package));
        Assert.Equal(AnimationDurationKind.OpenEnded, new AnimationPlayer(context, 0).MeasureDuration(TestContext.Current.CancellationToken).Kind);
        var end = player.EvaluateForTest(3); player.EvaluateForTest(1.1, true);
        var replay = player.EvaluateForTest(3, true); Assert.Equal(end.Nodes, replay.Nodes); Assert.Equal(end.Sequences, replay.Sequences);
        player.Reset(); Assert.Equal(end.Nodes, player.EvaluateForTest(3).Nodes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void TransformedParentKeepsContactInWorldCoordinates(float height)
    {
        var (context, motion) = GroundFixture(0); var scene = context.Scene;
        var root = scene.Nodes[0];
        var parent = Matrix4x4.CreateScale(2, 3, 1) * Matrix4x4.CreateRotationZ(.2f) * Matrix4x4.CreateTranslation(10, 6, 20);
        root.Data["transform"] = GroundMatrix(parent); scene.Nodes[0] = root with { ModelIndex = null, Children = [1] };
        var data = (JsonObject)root.Data.DeepClone(); data["transform"] = GroundMatrix(Matrix4x4.Identity); data["scale"] = JsonData.Vector(Vector3.One);
        scene.Nodes.Add(new(1, "piece", "object3d", 0, [0], [], new JsonObject { ["flags"] = 4 }, data));
        var reference = new AnimationRecord(new byte[40]); reference.SetText(0, "piece", 36); context.Package.Entries[0].References[1].Add(reference); motion.SetInt(16, 2);
        var player = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true, PreviewHeight = height };
        for (int i = 0; i < 240; i++) AssertGrounded(context, player.Step(TestContext.Current.CancellationToken));
        var end = player.Frame(); player.LodLevel = 10;
        Assert.Equal(end.Nodes.Select(n => n.Transform), player.Frame().Nodes.Select(n => n.Transform));
        var alternate = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true, LodLevel = 10, PreviewHeight = height };
        Assert.Equal(end.Nodes.Select(n => n.Transform), alternate.EvaluateForTest(4).Nodes.Select(n => n.Transform));
    }

    [Fact]
    public void LandedDebrisStaysAboveGroundDuringAuthoredShrinkAndSinkWithoutExtraSounds()
    {
        var (context, motion) = GroundFixture(1.01f); motion.SetVector(64, new(0, -4, 0));
        var sink = AnimationCatalog.Create(10); sink.SetInt(12, 0x504); sink.SetInt(16, 1);
        sink.SetFloat(248, 2); sink.SetVector(76, new(0, -3, 0)); sink.SetVector(172, new(-.1f));
        context.Package.Entries[0].Sequences[0].Events.Add(sink);
        var player = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        for (int i = 0; i < 150; i++) { var f = player.Step(TestContext.Current.CancellationToken); AssertGrounded(context, f); Assert.Empty(f.Sounds); }
        Assert.True(player.IsComplete);
        var end = player.Frame(); player.EvaluateForTest(1, true); Assert.Equal(end.Nodes, player.EvaluateForTest(2.5, true).Nodes);
    }

    [Fact]
    public void LodSelectionCannotChangeCollisionEnvelopeOrTrajectory()
    {
        var (context, _) = GroundFixture(); var scene = context.Scene; var root = scene.Nodes[0];
        scene.Nodes[0] = root with { ModelIndex = null, Children = [1, 3] };
        scene.Models.Add(scene.Models[0] with { Index = 1, Vertices = [new(-2, -3, 0), new(2, -3, 0), new(0, 1, 0)] });
        scene.Nodes.Add(new(1, "near", "lod", null, [0], [2], [], new JsonObject { ["range_near_sq"] = 0, ["range_far_sq"] = 100 }));
        scene.Nodes.Add(new(2, "near-piece", "object3d", 0, [1], [], new JsonObject { ["flags"] = 4 }, new JsonObject { ["flags"] = 8 }));
        scene.Nodes.Add(new(3, "far", "lod", null, [0], [4], [], new JsonObject { ["range_near_sq"] = 100, ["range_far_sq"] = 1000 }));
        scene.Nodes.Add(new(4, "far-piece", "object3d", 1, [3], [], new JsonObject { ["flags"] = 4 }, new JsonObject { ["flags"] = 8 }));
        var high = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        var low = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true, LodLevel = 1 };
        for (int i = 0; i < 180; i++)
        {
            var a = high.Step(TestContext.Current.CancellationToken); var b = low.Step(TestContext.Current.CancellationToken);
            AssertGrounded(context, a); AssertGrounded(context, b);
            Assert.Equal(a.Nodes.Select(n => n.Transform), b.Nodes.Select(n => n.Transform));
        }
        Assert.Equal(2, high.Frame().Nodes.Single(n => n.Visible).SourceNode);
        Assert.Equal(4, low.Frame().Nodes.Single(n => n.Visible).SourceNode);
    }

    [Fact]
    public void SingularParentProducesAnActionableGroundDiagnostic()
    {
        var (context, motion) = GroundFixture(0); var root = context.Scene.Nodes[0];
        root.Data["transform"] = GroundMatrix(Matrix4x4.CreateScale(0, 1, 1));
        context.Scene.Nodes[0] = root with { ModelIndex = null, Children = [1] };
        context.Scene.Nodes.Add(new(1, "piece", "object3d", 0, [0], [], new JsonObject { ["flags"] = 4 }, new JsonObject { ["flags"] = 8 }));
        var reference = new AnimationRecord(new byte[40]); reference.SetText(0, "piece", 36); context.Package.Entries[0].References[1].Add(reference); motion.SetInt(16, 2);
        var frame = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true }.Step(TestContext.Current.CancellationToken);
        Assert.Contains(frame.Diagnostics, n => n.Contains("cannot invert its parent transform")); Assert.Equal("Unavailable", frame.Sequences[0].State);
    }

    [Fact]
    public void InvalidImpactReferencesProduceDiagnosticsAndUnavailableDuration()
    {
        var (context, motion) = GroundFixture(1.01f); motion.SetVector(64, new(0, -4, 0)); motion.SetInt(12, 0x1805);
        motion.SetShort(240, 1000); motion.SetText(208, "absent"); motion.SetShort(242, 1000);
        var player = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true };
        var frame = player.Step(TestContext.Current.CancellationToken);
        Assert.Contains(frame.Diagnostics, n => n.Contains("unresolved release sequence"));
        Assert.Contains(frame.Diagnostics, n => n.Contains("unresolved sample reference"));
        Assert.Equal(AnimationDurationKind.Unavailable, player.MeasureDuration(TestContext.Current.CancellationToken).Kind);
        Assert.Empty(frame.ActiveSounds);
    }

    [Fact]
    public void GroundReportsMissingInvalidAndNonfiniteGeometry()
    {
        var (context, _) = GroundFixture(); var model = context.Scene.Models[0];
        context.Scene.Models[0] = model with { Vertices = [] };
        var player = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true }; var frame = player.EvaluateForTest(2);
        Assert.Contains(frame.Diagnostics, n => n.Contains("invalid polygon")); Assert.Contains(frame.Diagnostics, n => n.Contains("origin is used"));
        Assert.InRange(frame.Nodes[0].Transform.M42, 0, .001f);
        context.Scene.Models[0] = model with { Vertices = [new(float.NaN, 0, 0), Vector3.One, Vector3.Zero] };
        frame = new AnimationPlayer(context, 0) { GroundPlaneEnabled = true }.Step(TestContext.Current.CancellationToken);
        Assert.Contains(frame.Diagnostics, n => n.Contains("nonfinite transformed geometry")); Assert.Equal("Unavailable", frame.Sequences[0].State);
    }

    private static (AnimationPreviewContext Context, AnimationEvent Motion) GroundFixture(float y = 5)
    {
        var package = Fixture(); var context = Context(package);
        context.Scene.Models[0] = new(0, [new(-2, -1, 0), new(2, -1, 0), new(0, 1, 0)], [], [], [new(0, 0, [0, 1, 2], [], [], [])], []);
        context.Scene.Nodes[0].Data["transform"] = GroundMatrix(Matrix4x4.CreateTranslation(0, y, 0));
        var motion = AnimationCatalog.Create(10); motion.SetInt(12, 5); motion.SetInt(16, 1); motion.SetFloat(24, -9.8f);
        package.Entries[0].Sequences[0].Events.Add(motion);
        return (context, motion);
    }
    private static JsonArray GroundMatrix(Matrix4x4 m) => new(m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43);
    private static void AssertGrounded(AnimationPreviewContext context, AnimationFrame frame)
    {
        foreach (var pose in frame.Nodes)
        {
            var model = context.Scene.Models[pose.Model];
            foreach (int index in model.Polygons.SelectMany(p => p.Vertices).Distinct())
            {
                var vertex = model.Vertices[index] + (model.Morphs.Length == model.Vertices.Length ? model.Morphs[index] * pose.Morph : Vector3.Zero);
                Assert.True(Vector3.Transform(vertex, pose.Transform).Y >= -.0001f, $"Mesh penetrates at {frame.Time}: {pose.Transform}");
            }
        }
    }
}
