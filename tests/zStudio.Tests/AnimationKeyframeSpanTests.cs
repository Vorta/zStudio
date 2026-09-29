using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    private static AnimationKeyframe Segment(float start, float end, float x, float rate)
    {
        var frame = AnimationKeyframe.Create(1); frame.Start = start; frame.End = end;
        frame.SetVector(frame.ChannelOffset(0), new(x, 0, 0)); frame.SetVector(frame.ChannelOffset(0) + 16, new(rate, 0, 0)); return frame;
    }
    private static (AnimationPackage Package, AnimationEvent Event) KeyframeFixture(params AnimationKeyframe[] frames)
    {
        var package = Fixture(); var ev = AnimationCatalog.Create(12); ev.SetInt(12, 1);
        ev = ev.WithKeyframes(frames); package.Entries[0].Sequences[0].Events.Add(ev); return (package, ev);
    }
    [Fact]
    public void RecoilReversedSpanBetweenMatchingSegmentsIsSkippedLikeRetail()
    {
        // Shape of retail m3pickup motion_script*.1 frames 8-11: 4..4.5, 4.5..4, 4..4.5, 4.5..5.
        var (package, _) = KeyframeFixture(Segment(0, 1, 0, 1), Segment(1, .5f, 100, 0), Segment(.5f, 2, 10, 1));
        var early = new AnimationPlayer(Context(package), 0).EvaluateForTest(.9);
        Assert.InRange(early.Nodes[0].Transform.M41, .899f, .901f);
        var player = new AnimationPlayer(Context(package), 0); var frame = player.EvaluateForTest(1.5);
        Assert.InRange(frame.Nodes[0].Transform.M41, 10.999f, 11.001f);
        Assert.DoesNotContain(frame.Sequences, s => s.State == "Unavailable");
        Assert.Equal(frame.Nodes, player.EvaluateForTest(1.5, true).Nodes);
        Assert.Equal(AnimationDurationKind.Finite, player.MeasureDuration(TestContext.Current.CancellationToken).Kind);
    }
    [Fact]
    public void RecoilReversedFinalSpanWaitsForItsStartAndUsesRetailLocalTime()
    {
        // Retail 0x45AE90 accumulates end - max(cursor, start): a finished reversed span samples negative local time.
        var (package, _) = KeyframeFixture(Segment(0, 1, 0, 1), Segment(2, 1.5f, 10, 2));
        var waiting = new AnimationPlayer(Context(package), 0).EvaluateForTest(1.75);
        Assert.InRange(waiting.Nodes[0].Transform.M41, .999f, 1.001f);
        Assert.Contains(waiting.Sequences, s => s.State == "Running");
        var finished = new AnimationPlayer(Context(package), 0).EvaluateForTest(2.5);
        Assert.InRange(finished.Nodes[0].Transform.M41, 8.999f, 9.001f);
        Assert.DoesNotContain(finished.Sequences, s => s.State == "Unavailable");
    }
    [Fact]
    public void RecoilNegativeStartMeasuresLocalTimeFromTheEventCursor()
    {
        // Retail starts the sample cursor at the event start; a negative start adds no pre-start delay.
        var (package, _) = KeyframeFixture(Segment(-1, 2, 0, 1));
        var frame = new AnimationPlayer(Context(package), 0).EvaluateForTest(1);
        Assert.InRange(frame.Nodes[0].Transform.M41, .999f, 1.001f);
    }
    [Fact]
    public void Mw3ReversedSpanPreviewIsUnavailableWithoutChangingTheStream()
    {
        byte[] prefix = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 39);
        var package = new AnimationPackage { Prefix = prefix, Tail = [] };
        var entry = new AnimationEntry(new byte[316], 0, 80); entry.SetText(32, "animated"); entry.SetFloat(164, -1); package.Entries.Add(entry);
        var sequence = new AnimationSequence(new byte[64]); sequence.Name = "reversed"; entry.Sequences.Add(sequence);
        var ev = AnimationCatalog.Create(12, 39); ev.SetInt(12, -100);
        ev = ev.WithKeyframes([Segment(0, 1, 5, 0).ForVersion(39), Segment(2, 1, 9, 0).ForVersion(39)]); sequence.Events.Add(ev);
        byte[] original = AnimationWriter.Write(package, TestContext.Current.CancellationToken);
        package = AnimationPackage.Read(original, TestContext.Current.CancellationToken);
        Assert.Equal(2, package.Entries[0].Sequences[0].Events[0].Keyframes(TestContext.Current.CancellationToken).Count);
        var frame = new AnimationPlayer(Context(package), 0).EvaluateForTest(.5);
        Assert.Equal(Vector3.Zero, Assert.Single(frame.Nodes).Transform.Translation);
        Assert.Contains(frame.Sequences, s => s.State == "Unavailable");
        Assert.Contains(frame.Diagnostics, s => s.Contains("unverified runtime meaning"));
        Assert.Equal(original, AnimationWriter.Write(package, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AmbiguousDescendantNamesFollowEachGameResolver(bool mw3)
    {
        uint version = mw3 ? 39u : 28u;
        byte[] prefix = new byte[mw3 ? 80 : 72]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), version);
        var package = new AnimationPackage { Prefix = prefix, Tail = [] };
        var entry = new AnimationEntry(new byte[mw3 ? 316 : 308], 0, prefix.Length); entry.SetText(0, "ambiguous"); entry.SetText(32, "animated"); entry.SetFloat(164, -1); package.Entries.Add(entry);
        entry.References[1].Add(new(new byte[40])); var reference = new AnimationRecord(new byte[40]); reference.SetText(0, "part", 36); entry.References[1].Add(reference);
        var sequence = new AnimationSequence(new byte[64]); sequence.Name = "motion"; entry.Sequences.Add(sequence);
        var ev = AnimationCatalog.Create(12, version); ev.SetInt(12, 1);
        sequence.Events.Add(ev.WithKeyframes([Segment(0, 1, 5, 0).ForVersion(version)]));
        AnimationPreviewContext World(params string[] names)
        {
            var doc = new ZbdDocument("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, mw3 ? 27u : 15u, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
            doc.Scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [], []));
            JsonObject Data() => new() { ["flags"] = 0, ["opacity"] = 1f, ["scale"] = JsonData.Vector(Vector3.One), ["transform"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0) };
            doc.Scene.Nodes.Add(new(0, "animated", "object3d", 0, [], Enumerable.Range(1, names.Length).ToArray(), new JsonObject { ["flags"] = 4 }, Data()));
            for (int i = 0; i < names.Length; i++) doc.Scene.Nodes.Add(new(i + 1, names[i], "object3d", 0, [0], [], new JsonObject { ["flags"] = 4 }, Data()));
            return new() { Package = package, World = doc };
        }
        float X(AnimationFrame frame, int node) => frame.Nodes.Single(n => n.SourceNode == node).Transform.M41;
        var ambiguous = new AnimationPlayer(World("part", "part"), 0).EvaluateForTest(.5);
        if (mw3)
        {
            // Names are not unique MW3 identities: an ambiguous reference must not animate an arbitrary match.
            Assert.Equal(0, X(ambiguous, 1)); Assert.Equal(0, X(ambiguous, 2));
            Assert.Contains(ambiguous.Diagnostics, d => d.Contains("unresolved node reference"));
        }
        else { Assert.Equal(5, X(ambiguous, 1)); Assert.Equal(0, X(ambiguous, 2)); } // RECOIL keeps its first-match loader order.
        var unique = new AnimationPlayer(World("part", "other"), 0).EvaluateForTest(.5);
        Assert.Equal(5, X(unique, 1)); Assert.Equal(0, X(unique, 2));
    }
}
