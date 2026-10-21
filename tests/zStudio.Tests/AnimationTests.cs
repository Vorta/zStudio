using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void WriterPreservesOpaqueBytesAndDuplicatedPrimaryUntilEdited()
    {
        var package = Fixture(); package.Entries[0].Bytes[210] = 0xCC;
        var unknown = new AnimationEvent(new byte[17]); unknown.Bytes[0] = 255; unknown.Bytes[1] = 1; unknown.SetInt(4, 17); unknown.Bytes[16] = 0xAB;
        package.Entries[0].Sequences[0].Events.Add(unknown);
        byte[] bytes = Pack(package), roundtrip = Pack(Parse(bytes)); Assert.Equal(bytes, roundtrip);
        Assert.Equal(0xCC, roundtrip[package.Prefix.Length + 210]); Assert.Contains((byte)0xAB, roundtrip);
        var copy = Parse(bytes); copy.Entries[0].Primary.Name = "changed";
        var changed = Pack(copy); Assert.Equal((byte)'c', changed[package.Prefix.Length + 196]);
    }
    [Fact]
    public void MalformedEventPayloadIsPreserved()
    {
        var package = Fixture(); var sequence = package.Entries[0].Sequences[0]; sequence.Events.Add(AnimationCatalog.Create(6));
        byte[] bytes = Pack(package); int header = package.Prefix.Length + 308 + 80 + 64;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(header + 64 + 4), int.MaxValue);
        var parsed = Parse(bytes); Assert.False(parsed.Entries[0].Sequences[0].IsEditable); Assert.NotEmpty(parsed.Diagnostics);
        Assert.Equal(bytes, Pack(parsed));
    }
    [Fact]
    public void RelativeDelayWaitsAfterPreviousTimedEventAndSeekIsDeterministic()
    {
        var package = Fixture(); var motion = AnimationCatalog.Create(11); motion.SetInt(16, 1); motion.SetVector(44, new(6, 0, 0)); motion.SetVector(56, new(6, 0, 0));
        var hide = AnimationCatalog.Create(6); hide.SetShort(16, 1); hide.SetInt(12, 0); hide.StartMode = 3; hide.Threshold = .5f;
        package.Entries[0].Sequences[0].Events.AddRange([motion, hide]); AnimationPlayer player = new(Context(package), 0);
        var frame = player.EvaluateForTest(.75); Assert.InRange(frame.Nodes[0].Transform.M41, 4.49f, 4.51f); Assert.True(frame.Nodes[0].Visible);
        Assert.True(player.EvaluateForTest(1.4).Nodes[0].Visible); var end = player.EvaluateForTest(1.6); Assert.False(end.Nodes[0].Visible);
        player.EvaluateForTest(.2, true); var replay = player.EvaluateForTest(1.6, true); Assert.Equal(end.Nodes, replay.Nodes); Assert.Equal(end.Trace, replay.Trace);
        // A seek canceled inside a tick (here while the hide event is admitted) stops at a tick boundary that an
        // uncanceled run reaches, so resuming the same player gives the same pose, dispatches and sequence states.
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        AnimationPlayer? interrupted = null;
        interrupted = new(Context(package), 0, 1, false, TestContext.Current.CancellationToken, AnimationPlayer.MaximumRetainedStateBytes,
            () => { if (interrupted?.Time > 1.4) canceled.Cancel(); return 0; });
        Assert.ThrowsAny<OperationCanceledException>(() => interrupted.AdvanceTo(1.6, true, canceled.Token));
        Assert.InRange(interrupted.Time, 1.4, 1.59);
        var resumed = interrupted.EvaluateForTest(1.6, true);
        Assert.Equal(end.Nodes, resumed.Nodes); Assert.Equal(end.Trace, resumed.Trace);
        Assert.Equal(end.Sequences, resumed.Sequences); Assert.Equal(end.Diagnostics, resumed.Diagnostics);
    }
    [Fact]
    public void TickRefusedPartWayIsReplayedRatherThanContinued()
    {
        // Cleanup admission is refused after the motion ended but before the effect aged in that tick. The player must
        // not show or continue that partial tick; the next seek replays it and matches an unrefused run.
        var package = Fixture(); var entry = package.Entries[0]; entry.SetFloat(164, 0);
        entry.References[5].Add(new(new byte[36])); var spark = new AnimationRecord(new byte[36]); spark.SetText(0, "spark"); entry.References[5].Add(spark);
        var effect = AnimationCatalog.Create(3); effect.SetShort(12, 1);
        var motion = AnimationCatalog.Create(11); motion.SetInt(12, 1); motion.SetInt(16, 1); motion.SetVector(44, new(3, 0, 0)); motion.SetVector(56, new(6, 0, 0)); motion.SetFloat(140, .5f);
        entry.Sequences[0].Events.AddRange([effect, motion]);
        AnimationPreviewContext Sparks() { var context = Context(package); context.Effects.Add("spark", new("spark", "animated", 0, [], 1, false)); return context; }
        var expected = new AnimationPlayer(Sparks(), 0).EvaluateForTest(.7);
        bool refuse = true; AnimationPlayer? player = null;
        player = new(Sparks(), 0, 1, false, TestContext.Current.CancellationToken, AnimationPlayer.MaximumRetainedStateBytes,
            () => refuse && player?.Time > .4 ? AnimationPlayer.MaximumRetainedStateBytes : 0);
        Assert.Throws<AnimationPlayer.StateLimitException>(() => player.EvaluateForTest(.7));
        Assert.Throws<IOException>(() => player.Frame(TestContext.Current.CancellationToken));
        refuse = false;
        var resumed = player.EvaluateForTest(.7);
        Assert.Equal(expected.Nodes, resumed.Nodes); Assert.Equal(expected.Trace, resumed.Trace); Assert.Equal(expected.Sequences, resumed.Sequences);
    }
    [Fact]
    public void PlayerRefusedAtItsStateAllowanceRecoversLikeANewPlayer()
    {
        // Only the reset state fits: the motion is refused admission, and the cleanup that follows escapes the tick at the
        // allowance. That partial state must not count against its own replacement, so seeking, stepping and resetting
        // give a new player's outcomes with the same allowance.
        var package = Fixture(); var entry = package.Entries[0]; entry.SetFloat(164, 0);
        var motion = AnimationCatalog.Create(11); motion.SetInt(12, 1); motion.SetInt(16, 1); motion.SetVector(56, new(6, 0, 0)); motion.SetFloat(140, .5f);
        entry.Sequences[0].Events.Add(motion);
        var context = Context(package); var token = TestContext.Current.CancellationToken;
        long allowance = new AnimationPlayer(context, 0).RetainedStateBytes + 200; // the reset state, its snapshot and clone scratch
        AnimationPlayer Limited() => new(context, 0, 1, false, token, allowance);
        var fresh = Limited(); var start = fresh.Frame(token);
        Assert.Throws<AnimationPlayer.StateLimitException>(() => fresh.EvaluateForTest(.5));
        var player = Limited();
        Assert.Throws<AnimationPlayer.StateLimitException>(() => player.EvaluateForTest(.5));
        var rewound = player.EvaluateForTest(0, true);
        Assert.Equal(start.Nodes, rewound.Nodes); Assert.Equal(start.Sequences, rewound.Sequences); Assert.Equal(start.Diagnostics, rewound.Diagnostics);
        Assert.Throws<AnimationPlayer.StateLimitException>(() => player.Step(token));
        player.Reset(token);
        Assert.Equal(start.Nodes, player.Frame(token).Nodes);
        Assert.Throws<AnimationPlayer.StateLimitException>(() => player.EvaluateForTest(.5));
    }
    [Fact]
    public void WaitingSequenceReleaseAndStopUseVerifiedStateValues()
    {
        var package = Fixture(); var entry = package.Entries[0]; var waiting = entry.Sequences[0]; waiting.ResetMode = 3;
        var move = AnimationCatalog.Create(7); move.SetShort(28, 1); move.SetVector(16, new(12, 0, 0)); waiting.Events.Add(move);
        var control = new AnimationSequence(new byte[64]); control.Name = "controller";
        var release = AnimationCatalog.Create(22); release.SetText(12, waiting.Name); release.Threshold = .5f; control.Events.Add(release); entry.Sequences.Insert(0, control);
        AnimationPlayer player = new(Context(package), 0); Assert.Equal(0, player.EvaluateForTest(.4).Nodes[0].Transform.M41);
        Assert.Equal(12, player.EvaluateForTest(.6).Nodes[0].Transform.M41);
    }
    [Fact]
    public void PrimarySequenceDoesNotRunAlongsideNormalPlayback()
    {
        var package = Fixture(); var reset = AnimationCatalog.Create(7); reset.SetShort(28, 1); reset.SetVector(16, new(99, 0, 0)); package.Entries[0].Primary.Events.Add(reset);
        Assert.Equal(0, new AnimationPlayer(Context(package), 0).EvaluateForTest(.1).Nodes[0].Transform.M41);
        Assert.Equal(99, new AnimationPlayer(Context(package), 0, resetPhase: true).EvaluateForTest(.1).Nodes[0].Transform.M41);
    }
    [Fact]
    public void KeyframesUseBaseRateAndRetailRotationVectorLength()
    {
        var package = Fixture(); var ev = AnimationCatalog.Create(12); ev.SetInt(12, 1);
        var key = AnimationKeyframe.Create(7); key.End = 2; key.SetVector(key.ChannelOffset(0), new(10, 0, 0)); key.SetVector(key.ChannelOffset(0) + 16, new(2, 0, 0));
        key.SetVector(key.ChannelOffset(1) + 16, new(0, MathF.PI / 4, 0)); ev = ev.WithKeyframes([key]); package.Entries[0].Sequences[0].Events.Add(ev);
        var pose = new AnimationPlayer(Context(package), 0).EvaluateForTest(1).Nodes[0]; Assert.InRange(pose.Transform.M41, 11.99f, 12.01f);
        var direction = Vector3.TransformNormal(Vector3.UnitZ, pose.Transform); Assert.InRange(direction.X, .999f, 1.001f); Assert.InRange(direction.Z, -.001f, .001f);
    }
    [Fact]
    public void InvalidAndUnknownEventsPauseOnlyTheirSequence()
    {
        var package = Fixture(); var ev = AnimationCatalog.Create(6); ev.Bytes[0] = 255; package.Entries[0].Sequences[0].Events.Add(ev);
        var frame = new AnimationPlayer(Context(package), 0).EvaluateForTest(.1); Assert.Contains(frame.Diagnostics, s => s.Contains("unverified")); Assert.Equal("Unavailable", frame.Sequences[0].State);
    }
    private static byte[] Pack(AnimationPackage package) => AnimationWriter.Write(package, TestContext.Current.CancellationToken);
    [Fact]
    public void OpacityRequiresOverrideFlagAndInheritsFromParent()
    {
        var package = Fixture(); var context = Context(package); context.Scene.Nodes[0].Data["opacity"] = 0f;
        Assert.Equal(1, new AnimationPlayer(context,0).EvaluateForTest(.1).Nodes[0].Opacity);
        context.Scene.Nodes[0].Data["flags"] = 2;
        Assert.Equal(0, new AnimationPlayer(context,0).EvaluateForTest(.1).Nodes[0].Opacity);
        var opacity = AnimationCatalog.Create(13); opacity.SetShort(12,1); opacity.SetShort(20,1); opacity.SetFloat(16,.35f); package.Entries[0].Sequences[0].Events.Add(opacity);
        Assert.Equal(.35f, new AnimationPlayer(context,0).EvaluateForTest(.1).Nodes[0].Opacity);
        opacity.SetShort(12,0);
        Assert.Equal(1, new AnimationPlayer(context,0).EvaluateForTest(.1).Nodes[0].Opacity);
    }
    [Fact]
    public void NullPositionBasisDoesNotAddTheBoundRootTwice()
    {
        var package = Fixture(); var context = Context(package); context.Scene.Nodes[0].Data["transform"] = new JsonArray(1,0,0,0,1,0,0,0,1,10,0,0);
        var position = AnimationCatalog.Create(7); position.SetShort(28,1); position.SetVector(16,new(3,0,0)); package.Entries[0].Sequences[0].Events.Add(position);
        Assert.Equal(3,new AnimationPlayer(context,0).EvaluateForTest(.1).Nodes[0].Transform.M41);
    }
    [Fact]
    public void CountedLoopTerminatesAndDoesNotMutateSerializedEvents()
    {
        var package = Fixture(); var sequence = package.Entries[0].Sequences[0];
        var position = AnimationCatalog.Create(7); position.SetShort(28,1); position.SetInt(12,1); position.SetVector(16,new(1,0,0)); sequence.Events.Add(position);
        var loop = AnimationCatalog.Create(30); loop.SetInt(16,3); sequence.Events.Add(loop); byte[] original = Pack(package);
        var frame = new AnimationPlayer(Context(package),0).EvaluateForTest(.2);
        Assert.Equal(3,frame.Nodes[0].Transform.M41); Assert.Equal("Complete",frame.Sequences[0].State); Assert.Equal(original,Pack(package));
    }
    [Fact]
    public void StopSequenceDoesNotReleaseWaitingSequence()
    {
        var package = Fixture(); var entry = package.Entries[0]; var waiting = new AnimationSequence(new byte[64]) { Name = "waiting", ResetMode = 3 }; entry.Sequences.Add(waiting);
        var move = AnimationCatalog.Create(7); move.SetShort(28,1); move.SetVector(16,new(5,0,0)); waiting.Events.Add(move);
        var stop = AnimationCatalog.Create(23); stop.SetText(12,"waiting"); entry.Sequences[0].Events.Add(stop);
        var frame = new AnimationPlayer(Context(package),0).EvaluateForTest(.1); Assert.Equal(0,frame.Nodes[0].Transform.M41); Assert.Equal("Complete",frame.Sequences[1].State);
    }
    [Fact]
    public void KeyframeResizePreservesUnknownHeadersAndSegmentBytes()
    {
        var package = Fixture(); var ev = AnimationCatalog.Create(12); ev.Bytes[2] = 0x79; ev.SetInt(16,unchecked((int)0xfedcba98));
        var segment = AnimationKeyframe.Create(1); segment.SetFloat(24,123); ev = ev.WithKeyframes([segment,AnimationKeyframe.Create(7)]); package.Entries[0].Sequences[0].Events.Add(ev);
        var reopened = Parse(Pack(package)).Entries[0].Sequences[0].Events[0];
        Assert.Equal(0x79,reopened.Bytes[2]); Assert.Equal(0xfedcba98u,reopened.U32(16)); Assert.Equal(2,reopened.Keyframes(TestContext.Current.CancellationToken).Count); Assert.Equal(123,reopened.Keyframes(TestContext.Current.CancellationToken)[0].F32(24));
    }
    [Fact]
    public void CleanupRestoresOnlyTrackedTransforms()
    {
        var package = Fixture(); var entry = package.Entries[0]; entry.SetInt(148,0x40); entry.SetFloat(164,0);
        var move = AnimationCatalog.Create(7); move.SetShort(28,1); move.SetVector(16,new(7,0,0)); entry.Sequences[0].Events.Add(move);
        Assert.Equal(7,new AnimationPlayer(Context(package),0).EvaluateForTest(.1).Nodes[0].Transform.M41);
        var tracked = new AnimationRecord(new byte[96]); tracked.SetText(0,"animated"); entry.References[0].Add(tracked);
        Assert.Equal(0,new AnimationPlayer(Context(package),0).EvaluateForTest(.1).Nodes[0].Transform.M41);
    }
    private static AnimationPackage Parse(byte[] bytes) => AnimationPackage.Read(bytes, TestContext.Current.CancellationToken);
    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(-40)]
    public void OneShotSoundRemainsActiveForUnmuteResumeAndSeekUntilItEnds(float height)
    {
        var package = Fixture(); var entry = package.Entries[0];
        var sample = new AnimationRecord(new byte[36]); sample.SetText(0, "sound"); entry.References[4].Add(sample);
        var ev = AnimationCatalog.Create(1); ev.SetShort(12, 0); entry.Sequences[0].Events.Add(ev);
        var context = Context(package);
        byte[] wave = new byte[8044]; using (var writer = new BinaryWriter(new MemoryStream(wave)))
        {
            writer.Write("RIFF"u8); writer.Write(8036); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(8000);
            writer.Write((short)1); writer.Write((short)8); writer.Write("data"u8); writer.Write(8000);
        }
        context.Sounds.Add("sound", new("sound", "sound.wav", false, wave));
        var duration = new AnimationPlayer(context, 0).MeasureDuration(TestContext.Current.CancellationToken);
        Assert.True(duration.IsFinite); Assert.Equal(60, duration.Frames);
        var player = new AnimationPlayer(context, 0) { PreviewHeight = height }; var start = player.EvaluateForTest(.1);
        Assert.Single(start.Sounds); Assert.Single(start.ActiveSounds);
        Assert.Equal(height, start.Sounds[0].Position.Y); Assert.Equal(height, start.ActiveSounds[0].Position.Y);
        var resume = player.EvaluateForTest(.7); Assert.Empty(resume.Sounds);
        Assert.Equal(start.ActiveSounds, resume.ActiveSounds);
        Assert.Empty(player.EvaluateForTest(1.1).ActiveSounds);
        var seek = player.EvaluateForTest(.7, true); Assert.Empty(seek.Sounds);
        Assert.Equal(resume.ActiveSounds, seek.ActiveSounds);
    }
    [Fact]
    public void ProceduralGravityTurnsAnUpwardLaunchIntoAFall()
    {
        var package = Fixture(); var motion = AnimationCatalog.Create(10);
        motion.SetInt(12, 0x405); motion.SetInt(16, 1); motion.SetFloat(24, -9.8f);
        motion.SetVector(64, new(0, 5, 0)); motion.SetFloat(248, 3);
        package.Entries[0].Sequences[0].Events.Add(motion); byte[] source = Pack(package);
        var player = new AnimationPlayer(Context(package), 0);
        Assert.InRange(player.EvaluateForTest(.5).Nodes[0].Transform.M42, 1.30f, 1.33f);
        var falling = player.EvaluateForTest(2);
        Assert.InRange(falling.Nodes[0].Transform.M42, -9.45f, -9.40f);
        player.EvaluateForTest(.25, true);
        Assert.Equal(falling.Nodes, player.EvaluateForTest(2, true).Nodes);
        Assert.Equal(source, Pack(package));
    }
    [Theory]
    [InlineData(0x405, false)]
    [InlineData(0x2405, true)]
    public void ProceduralGravityUsesTheRequestedBasis(int flags, bool worldBasis)
    {
        var package = Fixture(); var context = Context(package);
        context.Scene.Nodes[0].Data["transform"] = new JsonArray(0,1,0,-1,0,0,0,0,1,0,0,0);
        var motion = AnimationCatalog.Create(10); motion.SetInt(12, flags); motion.SetInt(16, 1);
        motion.SetFloat(24, -9.8f); motion.SetFloat(248, 2);
        package.Entries[0].Sequences[0].Events.Add(motion);
        var position = new AnimationPlayer(context, 0).EvaluateForTest(1).Nodes[0].Transform.Translation;
        Assert.InRange(worldBasis ? position.X : position.Y, -4.82f, -4.81f);
        Assert.InRange(worldBasis ? position.Y : position.X, -.0001f, .0001f);
    }
    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(90, -1, 0)]
    public void RandomLaunchUsesYawThenPitchSpeedAndAcceleration(float yaw, float x, float z)
    {
        var package = Fixture(); var motion = AnimationCatalog.Create(10);
        motion.SetInt(12, 0x408); motion.SetInt(16, 1); motion.SetFloat(248, 2);
        motion.SetFloat(32, yaw); motion.SetFloat(36, yaw);
        motion.SetFloat(40, 0); motion.SetFloat(44, 0);
        motion.SetFloat(48, 6); motion.SetFloat(52, 6);
        motion.SetFloat(56, -2); motion.SetFloat(60, -2);
        package.Entries[0].Sequences[0].Events.Add(motion);
        var pose = new AnimationPlayer(Context(package), 0).EvaluateForTest(1).Nodes[0];
        // Sixty explicit Euler steps: 6 m/s launch, -2 m/s² along that direction.
        Assert.InRange(pose.Transform.M41, x * 5.016667f - .001f, x * 5.016667f + .001f);
        Assert.InRange(pose.Transform.M43, z * 5.016667f - .001f, z * 5.016667f + .001f);
        Assert.Equal(0, pose.Transform.M42);
    }
    private static AnimationPackage Fixture()
    {
        byte[] prefix = new byte[72]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), 28); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(20), 1 << 16);
        var package = new AnimationPackage { Prefix = prefix, Tail = [0xA5, 0x5A] };
        var entry = new AnimationEntry(new byte[308], 0, 72); entry.SetText(0, "fixture"); entry.SetText(32, "animated"); entry.SetFloat(164, -1);
        entry.References[1].Add(new(new byte[40])); var reference = new AnimationRecord(new byte[40]); reference.SetText(0, "animated", 36); entry.References[1].Add(reference);
        var sequence = new AnimationSequence(new byte[64]); sequence.Name = "motion"; entry.Sequences.Add(sequence); package.Entries.Add(entry);
        return Parse(Pack(package));
    }
    private static AnimationPreviewContext Context(AnimationPackage package)
    {
        var doc = new AssetDocument("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        doc.Scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [], []));
        doc.Scene.Nodes.Add(new(0, "animated", "object3d", 0, [], [], new JsonObject { ["flags"] = 4 }, new JsonObject { ["flags"] = 0, ["opacity"] = 1f, ["scale"] = JsonData.Vector(Vector3.One), ["transform"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0) }));
        return new() { Package = package, World = doc };
    }
}

internal static class AnimationTestExtensions
{
    public static AnimationFrame EvaluateForTest(this AnimationPlayer player, double time, bool seek = false) => player.AdvanceTo(time, seek, TestContext.Current.CancellationToken);
}
