using System.Collections;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AssetJsonCancellationTests
{
    [Theory]
    [InlineData(AssetKind.Raw)]
    [InlineData(AssetKind.Model)]
    [InlineData(AssetKind.World)]
    [InlineData(AssetKind.Script)]
    [InlineData(AssetKind.Sound)]
    [InlineData(AssetKind.Animation)]
    public void CanceledInspectionDoesNotStartDecoding(AssetKind kind)
    {
        var doc = Document();
        var asset = doc.Add(kind, 0, "fixture", 0, 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => ExportService.AssetJson(doc, asset, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public void VectorAndIndexExpansionObserveCancellationDuringEnumeration()
    {
        using var vectors = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => JsonData.Vectors(CancelDuringEnumeration(vectors, Vector3.One), vectors.Token));
        using var indices = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => JsonData.Integers(CancelDuringEnumeration(indices, 1), indices.Token));

        static IEnumerable<T> CancelDuringEnumeration<T>(CancellationTokenSource cancellation, T value)
        {
            yield return value;
            cancellation.Cancel();
            yield return value;
            throw new InvalidOperationException("Expansion continued beyond cancellation.");
        }
    }

    [Fact]
    public void ScriptInspectionStopsInsideInstructionEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        var doc = Document();
        var asset = doc.Add(AssetKind.Script, 0, "script", 0, 0,
            content: new ScriptContent(new CancelingInstructions(cancellation), ""));
        Assert.Throws<OperationCanceledException>(() => ExportService.AssetJson(doc, asset, cancellation.Token));
    }

    [Fact]
    public void ModelPropertiesRetainOrderingSpecialFloatsAndIndependentMetadata()
    {
        var doc = Document();
        JsonObject metadata = new() { ["unknown"] = new JsonArray(7, null, "untouched") };
        var polygon = new Polygon(0, 0, [2, 0, 1], [1, 0, 2], [new(.25f, .5f)], metadata);
        var model = new GameModel(0, [new(1, float.NaN, 3)], [Vector3.UnitY], [Vector3.Zero], [polygon], []);
        var asset = doc.Add(AssetKind.Model, 0, "model", 0, 0, metadata, model);
        var result = ExportService.AssetJson(doc, asset, TestContext.Current.CancellationToken);
        Assert.True(JsonNode.DeepEquals(JsonData.Vectors(model.Vertices, TestContext.Current.CancellationToken), result["vertices"]));
        Assert.True(JsonNode.DeepEquals(new JsonArray(2, 0, 1), result["polygons"]![0]!["vertex_indices"]));
        Assert.Equal(.25, result["polygons"]![0]!["uvs"]![0]!["u"]!.GetValue<double>());
        Assert.Equal(JsonData.Number(float.NaN).ToJsonString(), result["vertices"]![0]!["y"]!.ToJsonString());
        result["properties"]!["unknown"]![0] = 99;
        result["polygons"]![0]!["unknown"]![0] = 88;
        Assert.Equal(7, metadata["unknown"]![0]!.GetValue<int>());
    }

    [Fact]
    public void WorldAndScriptPropertiesMatchStoredData()
    {
        var doc = Document();
        doc.Scene = new();
        JsonObject nodeMetadata = new() { ["name"] = "node", ["nested"] = new JsonArray(new JsonObject { ["value"] = 42 }, null) };
        JsonObject material = new() { ["texture_index"] = -1, ["color"] = new JsonObject { ["r"] = .5 } };
        doc.Scene.Nodes.Add(new(0, "node", "object3d", null, [], [], nodeMetadata, []));
        doc.Scene.Materials.Add(material);
        var world = doc.Add(AssetKind.World, 0, "world", 0, 0);
        var result = ExportService.AssetJson(doc, world, TestContext.Current.CancellationToken);
        Assert.True(JsonNode.DeepEquals(new JsonArray(nodeMetadata.DeepClone()), result["nodes"]));
        Assert.True(JsonNode.DeepEquals(new JsonArray(material.DeepClone()), result["materials"]));
        result["nodes"]![0]!["nested"]![0]!["value"] = 12;
        Assert.Equal(42, nodeMetadata["nested"]![0]!["value"]!.GetValue<int>());

        var script = new ScriptContent([["FindNode", "duplicate"], [], ["unknown", "a_b", ""]], "");
        var asset = doc.Add(AssetKind.Script, 0, "script", 0, 0, content: script);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(script.Instructions),
            ExportService.AssetJson(doc, asset, TestContext.Current.CancellationToken)["instructions"]));
    }

    [Fact]
    public void SoundPropertiesRetainWaveShapeAndCueOrder()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(96u); writer.Write("WAVEfmt "u8);
            writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(8000u);
            writer.Write(8000u); writer.Write((ushort)1); writer.Write((ushort)8);
            writer.Write("cue "u8); writer.Write(52u); writer.Write(2u);
            foreach (uint id in new uint[] { 7, 2 })
            {
                writer.Write(id); writer.Write(new byte[16]); writer.Write(id);
            }
            writer.Write("data"u8); writer.Write(0u);
        }
        byte[] bytes = stream.ToArray();
        var doc = new ZbdDocument("fixture", new(bytes.Length, DateTime.MinValue), new(FormatFamily.Wave, null, Recognition.Supported, "fixture"), bytes);
        var asset = doc.Add(AssetKind.Sound, 0, "sound", 0, bytes.Length);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(WaveDecoder.Read(bytes, TestContext.Current.CancellationToken)),
            ExportService.AssetJson(doc, asset, TestContext.Current.CancellationToken)["wave"]));
    }

    [Fact]
    public void AnimationExpansionPreservesKeyframesAndObservesCancellation()
    {
        var entry = new AnimationEntry(new byte[308], 0, 72);
        var header = new byte[32]; header[0] = 12; header[1] = 1;
        var frames = new[] { AnimationKeyframe.Create(), AnimationKeyframe.Create(7) };
        var animationEvent = new AnimationEvent(header).WithKeyframes(frames);
        entry.Primary.Events.Add(animationEvent);
        var doc = Document();
        doc.Animations = new() { Prefix = [], Tail = [] };
        doc.Animations.Entries.Add(entry);
        var asset = doc.Add(AssetKind.Animation, 0, "animation", 0, 0);
        var result = ExportService.AssetJson(doc, asset, TestContext.Current.CancellationToken);
        var events = result["properties"]!["sequences"]![0]!["events"]!;
        Assert.Equal(Convert.ToHexStringLower(animationEvent.Bytes), events[0]!["raw_hex"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(new JsonArray(frames.Select(f => (JsonNode?)f.ToJson()).ToArray()), events[0]!["keyframes"]));
        Assert.Equal("reset_stop", result["properties"]!["sequences"]![0]!["phase"]!.GetValue<string>());

        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => entry.ToJson(cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => animationEvent.ToJson(cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => animationEvent.Keyframes(cancellation.Token));
    }

    [Fact]
    public void ChunkedHexRetainsAllBytesAcrossBoundaries()
    {
        var bytes = Enumerable.Range(0, 12345).Select(i => (byte)i).ToArray();
        Assert.Equal(Convert.ToHexStringLower(bytes), JsonData.Hex(bytes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AnimationSnapshotPreservesIdentityAndUnknownBytesWithoutSharedBuffers()
    {
        var entry = new AnimationEntry(new byte[308], 0, 72);
        var unknown = Enumerable.Range(0, 12345).Select(i => (byte)i).ToArray(); unknown[0] = 254;
        var sequence = new AnimationSequence(new byte[64], 400);
        sequence.Events.Add(new AnimationEvent(unknown, 464)); entry.Sequences.Add(sequence);
        entry.References[1].Add(new([3, 1, 4]));
        var snapshot = entry.Clone(TestContext.Current.CancellationToken);
        Assert.Equal(sequence.Id, snapshot.Sequences[0].Id);
        Assert.Equal(sequence.Events[0].Id, snapshot.Sequences[0].Events[0].Id);
        Assert.Equal(unknown, snapshot.Sequences[0].Events[0].Bytes);
        snapshot.Sequences[0].Events[0].Bytes[4096] = 99;
        snapshot.References[1][0].Bytes[0] = 9;
        Assert.Equal(0, unknown[4096]);
        Assert.Equal(3, entry.References[1][0].Bytes[0]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => entry.Clone(cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => sequence.Clone(false, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => sequence.Events[0].Clone(cancellation.Token));
    }

    private static ZbdDocument Document() => new("fixture", new(0, DateTime.MinValue),
        new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty);

    private sealed class CancelingInstructions(CancellationTokenSource cancellation) : IReadOnlyList<string[]>
    {
        public int Count => 3;
        public string[] this[int index] => throw new InvalidOperationException("Use the enumerator.");
        public IEnumerator<string[]> GetEnumerator()
        {
            yield return ["first"];
            cancellation.Cancel();
            yield return ["second"];
            throw new InvalidOperationException("Inspection continued beyond cancellation.");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
