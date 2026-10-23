using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    // These tests protect source-map landing and bounded playback, including the shared seek/analysis path.
    [Theory]
    [InlineData(30, 11)]
    [InlineData(80, 51)]
    public void TerrainCatchesFastFallsOnTheFloorBelowTheObjectAndReplays(float start, float expected)
    {
        var (context, motion) = GroundFixture(start);
        AddFloor(context, 10); AddFloor(context, 50); AddFloor(context, 70, flags: 4); AddFloor(context, 65, flags: 8);
        motion.SetVector(64, new(0, -6000, 0));
        var terrain = new AnimationTerrain(context.Scene, TestContext.Current.CancellationToken);
        AnimationPlayer Player() => new(context, 0) { Terrain = terrain, ProceduralLimitsEnabled = true };
        byte[] original = Pack(context.Package);
        var player = Player(); var end = player.EvaluateForTest(3);
        Assert.True(player.IsComplete);
        Assert.InRange(end.Nodes.Single(n => n.SourceNode == 0).Transform.M42, expected - .001f, expected + .001f);
        Assert.True(player.MeasureDuration(TestContext.Current.CancellationToken).IsFinite);
        player.EvaluateForTest(.2, true); Assert.Equal(end.Nodes, player.EvaluateForTest(3, true).Nodes);
        var paths = Player().AnalyzePaths(5, TestContext.Current.CancellationToken);
        Assert.Equal("object", Assert.Single(paths.Paths).Kind);
        Assert.InRange(paths.Paths[0].Points[^1].Position.Y, expected - .001f, expected + .001f);
        Assert.True(paths.Paths[0].Points.Count < 100); Assert.Equal(original, Pack(context.Package));
    }

    [Fact]
    public void TerrainUsesTransformedSlopesAndExcludesTheFallingAssembly()
    {
        var (context, motion) = GroundFixture(25);
        context.Scene.Nodes[0].Metadata["flags"] = 12;
        AddFloor(context, 10, slope: .25f);
        var floor = context.Scene.Nodes[^1];
        floor.Data["flags"] = 0; floor.Data["transform"] = GroundMatrix(Matrix4x4.CreateTranslation(0, 5, 0));
        motion.SetVector(64, new(0, -30, 0));
        var player = new AnimationPlayer(context, 0) { Terrain = new(context.Scene), ProceduralLimitsEnabled = true };
        var frame = player.EvaluateForTest(3);
        var pose = frame.Nodes.Single(n => n.SourceNode == 0);
        Assert.InRange(pose.Transform.M42, 16.49f, 16.51f);
        Assert.True(player.IsComplete);
    }

    [Theory]
    [InlineData(0, -60000, 0, -500)]
    [InlineData(0, 60000, 0, 500)]
    [InlineData(600, -60, 0, 500)]
    [InlineData(-600, 60, 0, -500)]
    [InlineData(480, 60, 40, 500)]
    public void ProceduralBoundsStopOnlyThatMotionWithoutReleasingImpact(float start, float velocity, float height, float expected)
    {
        var (context, motion) = GroundFixture(start);
        motion.SetInt(12, 0x805); motion.SetVector(64, new(0, velocity, 0)); motion.SetText(208, "impact"); motion.SetShort(240, -1);
        var waiting = new AnimationSequence(new byte[64]) { Name = "impact", ResetMode = 3 };
        var impact = AnimationCatalog.Create(14); impact.SetInt(32, 1); impact.SetFloat(24, 1); waiting.Events.Add(impact);
        context.Package.Entries[0].Sequences.Add(waiting);
        var other = new AnimationSequence(new byte[64]) { Name = "other" };
        var delay = AnimationCatalog.Create(14); delay.SetFloat(32, 1); delay.SetFloat(24, 1); other.Events.Add(delay);
        context.Package.Entries[0].Sequences.Add(other);
        var player = new AnimationPlayer(context, 0) { Terrain = new(context.Scene), ProceduralLimitsEnabled = true, PreviewHeight = height };
        var first = player.Step(TestContext.Current.CancellationToken);
        Assert.InRange(first.Nodes[0].Transform.M42, expected - .001f, expected + .001f);
        Assert.False(player.IsComplete);
        var end = player.EvaluateForTest(2);
        Assert.Equal(first.Nodes.Select(n => n.Transform), end.Nodes.Select(n => n.Transform)); Assert.DoesNotContain(end.Trace, t => t.Event == impact.Id);
        Assert.Contains(end.Trace, t => t.Event == delay.Id && t.End != null);
        Assert.Single(end.Diagnostics, n => n.Contains("preview Y limit"));
        Assert.Empty(end.Sounds);
        player.EvaluateForTest(0, true); Assert.Equal(end.Nodes, player.EvaluateForTest(2, true).Nodes);
    }

    [Fact]
    public void TerrainContactReleasesLandingWhileInitialOverlapDoesNot()
    {
        var (context, motion) = GroundFixture(10.5f); AddFloor(context, 10);
        motion.SetVector(64, new(0, 12, 0)); motion.SetInt(12, 0x805); motion.SetText(208, "landed"); motion.SetShort(240, -1);
        var waiting = new AnimationSequence(new byte[64]) { Name = "landed", ResetMode = 3 };
        var mark = AnimationCatalog.Create(14); mark.SetInt(32, 1); mark.SetFloat(24, 1); waiting.Events.Add(mark);
        context.Package.Entries[0].Sequences.Add(waiting);
        var player = new AnimationPlayer(context, 0) { Terrain = new(context.Scene), ProceduralLimitsEnabled = true };
        Assert.DoesNotContain(player.Step(TestContext.Current.CancellationToken).Trace, t => t.Event == mark.Id);
        Assert.Contains(player.EvaluateForTest(4).Trace, t => t.Event == mark.Id);
    }

    [Fact]
    public void AuthoredKeyframesAndCameraPathsRemainOutsideTheProceduralBounds()
    {
        var package = Fixture(); var context = Context(package);
        var keyframe = AnimationKeyframe.Create(1); keyframe.Start = 0; keyframe.End = 1;
        keyframe.SetVector(keyframe.ChannelOffset(0), new(0, 600, 0)); keyframe.SetVector(keyframe.ChannelOffset(0) + 16, new(0, 60, 0));
        var motion = AnimationCatalog.Create(12); motion.SetInt(12, 1); motion = motion.WithKeyframes([keyframe]);
        package.Entries[0].Sequences[0].Events.Add(motion);
        var paths = new AnimationPlayer(context, 0) { ProceduralLimitsEnabled = true, PreviewHeight = 20 }.AnalyzePaths(2, TestContext.Current.CancellationToken);
        Assert.Equal("object", Assert.Single(paths.Paths).Kind); Assert.True(paths.Paths[0].Points[^1].Position.Y > 600);
        context.Scene.Nodes[0] = context.Scene.Nodes[0] with { Class = "camera", Data = new JsonObject { ["translate"] = JsonData.Vector(new(0, 600, 0)) } };
        var procedural = AnimationCatalog.Create(10); procedural.SetInt(12, 0x404); procedural.SetInt(16, 1); procedural.SetFloat(248, 1); procedural.SetVector(64, new(0, 60, 0));
        package.Entries[0].Sequences[0].Events.Clear(); package.Entries[0].Sequences[0].Events.Add(procedural);
        paths = new AnimationPlayer(context, 0) { ProceduralLimitsEnabled = true }.AnalyzePaths(2, TestContext.Current.CancellationToken);
        Assert.Equal("camera", Assert.Single(paths.Paths).Kind); Assert.True(paths.Paths[0].Points[^1].Position.Y > 600);
        Assert.DoesNotContain(paths.Diagnostics, d => d.Contains("preview Y limit"));
    }

    private static void AddFloor(AnimationPreviewContext context, float y, int flags = 12, float slope = 0)
    {
        var scene = context.Scene;
        int world = scene.Nodes.FindIndex(n => n.Class == "world");
        if (world < 0)
        {
            world = scene.Nodes.Count;
            scene.Nodes.Add(new(world, "world", "world", null, [], [0], new JsonObject { ["flags"] = 4 }, []));
            scene.Nodes[0] = scene.Nodes[0] with { Parents = [world] };
        }
        int model = scene.Models.Count, node = scene.Nodes.Count;
        scene.Models.Add(new(model, [new(-100, y - 100 * slope, -100), new(-100, y - 100 * slope, 100), new(100, y + 100 * slope, 100), new(100, y + 100 * slope, -100)], [], [], [new(0, 0, [0, 1, 2, 3], [], [], [])], []));
        scene.Nodes.Add(new(node, "floor", "object3d", model, [world], [], new JsonObject { ["flags"] = flags }, new JsonObject { ["flags"] = 8 }));
        scene.Nodes[world] = scene.Nodes[world] with { Children = [.. scene.Nodes[world].Children, node] };
    }
}
