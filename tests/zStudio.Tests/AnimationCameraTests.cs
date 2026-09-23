using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void CameraKeyframesMoveTheEyeAndRotateNegativeZWithoutUsingAnglesAsPosition()
    {
        var package = Fixture(); var context = CameraContext(package);
        var ev = AnimationCatalog.Create(12); ev.SetInt(12, 1);
        var key = AnimationKeyframe.Create(3); key.End = 2;
        key.SetVector(key.ChannelOffset(0), new(2200, 40, 1200));
        key.SetVector(key.ChannelOffset(0) + 16, new(10, -5, 20));
        key.SetVector(key.ChannelOffset(1) + 16, new(0, MathF.PI / 4, 0));
        package.Entries[0].Sequences[0].Events.Add(ev.WithKeyframes([key]));
        byte[] original = Pack(package); AnimationPlayer player = new(context, 0);
        var camera = player.EvaluateForTest(1).Camera!;
        Near(new(2210, 35, 1220), camera.Position);
        Near(-Vector3.UnitX, camera.Target - camera.Position);
        Assert.Equal(60, camera.FieldOfView, .001f);
        Assert.Equal(0, camera.SourceNode); Assert.Equal("animated", camera.Name);
        player.EvaluateForTest(.2, true);
        Assert.Equal(camera, player.EvaluateForTest(1, true).Camera);
        Assert.Equal(original, Pack(package));
    }

    [Fact]
    public void CameraPositionAndRotationEventsPreserveIndependentChannelsAndStoredPose()
    {
        var package = Fixture(); var context = CameraContext(package);
        var position = AnimationCatalog.Create(7); position.SetShort(28, 1); position.SetInt(12, 1); position.SetVector(16, new(2, -1, 3));
        var rotation = AnimationCatalog.Create(9); rotation.SetShort(28, 1); rotation.SetVector(16, new(-MathF.PI / 6, 0, 0));
        package.Entries[0].Sequences[0].Events.AddRange([position, rotation]);
        var camera = new AnimationPlayer(context, 0).EvaluateForTest(.1).Camera!;
        Near(new(102, 39, 203), camera.Position);
        Near(new(0, -.5f, -MathF.Sqrt(3) / 2), camera.Target - camera.Position);
    }

    [Fact]
    public void CameraPoseIncludesAnimatedParentAndFovEventsConvertRadiansToDegrees()
    {
        var package = Fixture(); var context = CameraContext(package); var scene = context.Scene;
        scene.Nodes[0] = scene.Nodes[0] with { Parents = [1] };
        scene.Nodes.Add(new(1, "parent", "object3d", null, [2], [0], new() { ["flags"] = 4 }, new()
        {
            ["transform"] = new JsonArray(0, 0, -1, 0, 1, 0, 1, 0, 0, 1000, 10, 2000),
            ["rotate"] = JsonData.Vector(new(0, MathF.PI / 2, 0)), ["scale"] = JsonData.Vector(Vector3.One)
        }));
        scene.Nodes.Add(new(2, "world", "world", null, [], [1], [], []));
        var fov = AnimationCatalog.Create(21); fov.SetInt(12, 8); fov.SetInt(16, 1); fov.SetFloat(56, MathF.PI / 3); fov.SetFloat(60, MathF.PI / 2); fov.SetFloat(64, MathF.PI / 6); fov.SetFloat(104, 1);
        package.Entries[0].Sequences[0].Events.Add(fov);
        var camera = new AnimationPlayer(context, 0).EvaluateForTest(.5).Camera!;
        Near(new(1200, 50, 1900), camera.Position);
        Near(-Vector3.UnitX, camera.Target - camera.Position);
        Assert.Equal(75, camera.FieldOfView, .001f);
    }

    [Fact]
    public void CameraCachedMatrixModeSurvivesAParameterOnlyEvent()
    {
        var package = Fixture(); var context = CameraContext(package); var scene = context.Scene;
        scene.Nodes[0] = scene.Nodes[0] with { Parents = [1] };
        scene.Nodes.Add(new(1, "world", "world", null, [], [0], [], []));
        scene.Nodes[0].Data["flags"] = 2;
        scene.Nodes[0].Data["translate_matrix"] = new JsonArray(0, 0, -1, 0, 1, 0, 1, 0, 0, 400, 50, 700);
        var fov = AnimationCatalog.Create(20); fov.SetInt(12, 8); fov.SetInt(16, 1); fov.SetFloat(32, MathF.PI / 4);
        package.Entries[0].Sequences[0].Events.Add(fov);
        var camera = new AnimationPlayer(context, 0).EvaluateForTest(.1).Camera!;
        Near(new(400, 50, 700), camera.Position); Near(-Vector3.UnitX, camera.Target - camera.Position);
        Assert.Equal(45, camera.FieldOfView, .001f);
    }

    [Fact]
    public void InvalidCameraFovDoesNotProduceANonfiniteViewportPose()
    {
        var package = Fixture(); var context = CameraContext(package);
        var fov = AnimationCatalog.Create(20); fov.SetInt(12, 8); fov.SetInt(16, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(fov.Bytes.AsSpan(32), float.NaN);
        package.Entries[0].Sequences[0].Events.Add(fov);
        var frame = new AnimationPlayer(context, 0).EvaluateForTest(.1);
        Assert.Null(frame.Camera); Assert.Contains(frame.Diagnostics, n => n.Contains("invalid transform or field of view", StringComparison.Ordinal));
    }

    private static AnimationPreviewContext CameraContext(AnimationPackage package)
    {
        var context = Context(package);
        context.Scene.Nodes[0] = new(0, "animated", "camera", null, [], [], new() { ["flags"] = 4 }, new()
        {
            ["translate"] = JsonData.Vector(new(100, 40, 200)), ["rotate"] = JsonData.Vector(Vector3.Zero), ["fov_h_base"] = MathF.PI / 3
        });
        return context;
    }
    private static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < .001f, $"Expected {expected}, got {actual}");
}
