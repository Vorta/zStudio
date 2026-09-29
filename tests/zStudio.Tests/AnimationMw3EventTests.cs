using System.Buffers.Binary;
using System.Numerics;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void Mw3CleanupWaterAndLavaInvalidCachesRetainUnrelatedTargetsOnDuplication()
    {
        byte[] prefix = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 39);
        var package = new AnimationPackage { Prefix = prefix, Tail = [] };
        var entry = new AnimationEntry(new byte[316], 0, 80); package.Entries.Add(entry); entry.Primary.Name = "cleanup";
        var procedural = AnimationCatalog.Create(10, 39); procedural.SetInt(12, 0x800);
        procedural.SetText(208, "cleanup"); procedural.SetShort(240, -1);
        procedural.SetText(248, "water"); procedural.SetShort(280, -1);
        procedural.SetText(288, "lava"); procedural.SetShort(320, -1); entry.Primary.Events.Add(procedural);
        var session = new AnimationEditSession(package); session.AddSequence(0, entry.Primary.Id);
        var copy = package.Entries[0].Sequences.Single(); var ev = Assert.Single(copy.Events);
        Assert.Equal(copy.Name, ev.Text(208)); Assert.Equal("water", ev.Text(248)); Assert.Equal("lava", ev.Text(288));
        Assert.Equal("water", AnimationPackage.Read(AnimationWriter.Write(package, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken).Entries[0].Sequences[0].Events[0].Text(248));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Mw3MalformedKeyframeCountsNeverApplyTransformsAndPreserveSourceBytes(int count)
    {
        byte[] prefix = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 39);
        var package = new AnimationPackage { Prefix = prefix, Tail = [] };
        var entry = new AnimationEntry(new byte[316], 0, 80); entry.SetText(32, "animated"); entry.SetFloat(164, -1); package.Entries.Add(entry);
        var sequence = new AnimationSequence(new byte[64]); sequence.Name = "malformed"; entry.Sequences.Add(sequence);
        var ev = AnimationCatalog.Create(12, 39); ev.SetInt(12, -100);
        var key = AnimationKeyframe.Create(); key.SetVector(12, new(99, 88, 77));
        ev = ev.WithKeyframes([key]); ev.SetInt(16, count); package.Entries[0].Sequences[0].Events.Add(ev);
        byte[] original = AnimationWriter.Write(package, TestContext.Current.CancellationToken);
        package = AnimationPackage.Read(original, TestContext.Current.CancellationToken);
        var player = new AnimationPlayer(Context(package), 0); var frame = player.EvaluateForTest(.1);
        Assert.Equal(Vector3.Zero, Assert.Single(frame.Nodes).Transform.Translation);
        Assert.Contains(frame.Sequences, s => s.State == "Unavailable");
        Assert.Contains(frame.Diagnostics, s => s.Contains("keyframe count"));
        Assert.Equal(frame.Nodes, player.EvaluateForTest(.1, true).Nodes);
        Assert.Equal(original, AnimationWriter.Write(package, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(41, false)]
    [InlineData(42, false)]
    [InlineData(41, true)]
    [InlineData(42, true)]
    public void Mw3GameplayMarkersRetainTraceAndContinueWithoutSideEffects(byte type, bool cleanup)
    {
        byte[] prefix = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 39);
        var package = new AnimationPackage { Prefix = prefix, Tail = [] };
        var entry = new AnimationEntry(new byte[316], 0, 80); entry.SetText(32, "animated"); entry.SetFloat(164, -1); package.Entries.Add(entry);
        var sequence = cleanup ? entry.Primary : new AnimationSequence(new byte[64]); sequence.Name = "markers";
        if (!cleanup) entry.Sequences.Add(sequence);
        var marker = AnimationCatalog.Create(type, 39); sequence.Events.Add(marker);
        var move = AnimationCatalog.Create(7, 39); move.SetShort(28, -100); move.SetVector(16, new(7, 8, 9)); move.Threshold = .05f; sequence.Events.Add(move);
        byte[] original = AnimationWriter.Write(package, TestContext.Current.CancellationToken);
        var player = new AnimationPlayer(Context(package), 0, resetPhase: cleanup);
        var frame = player.EvaluateForTest(.1);
        Assert.Equal(new Vector3(7, 8, 9), Assert.Single(frame.Nodes).Transform.Translation);
        Assert.DoesNotContain(frame.Sequences, s => s.State == "Unavailable");
        var trace = Assert.Single(frame.Trace, t => t.Event == marker.Id);
        Assert.NotNull(trace.End); Assert.StartsWith("Not executed:", trace.Status);
        Assert.Empty(frame.Sounds); Assert.Empty(frame.ActiveSounds); Assert.Empty(frame.Lights);
        Assert.Contains(frame.Issues, issue => issue.Message.Contains("Not executed:") && issue.Severity == "Information");
        player.EvaluateForTest(0, true); var replay = player.EvaluateForTest(.1, true);
        Assert.Equal(frame.Nodes, replay.Nodes); Assert.Equal(frame.Trace, replay.Trace);
        Assert.Equal(AnimationDurationKind.Finite, player.MeasureDuration(TestContext.Current.CancellationToken).Kind);
        Assert.Equal(original, AnimationWriter.Write(package, TestContext.Current.CancellationToken));
    }
}
