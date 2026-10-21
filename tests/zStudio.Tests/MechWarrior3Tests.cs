using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Export;
using Xunit;

namespace zStudio.Tests;

public sealed class MechWarrior3Tests
{
    [Fact]
    public void MotionInspectionCapsPartNamesAndCountsWhileExportRetainsAllParts()
    {
        var token = TestContext.Current.CancellationToken;
        var seed = MotionClip.Read(MotionBytes(), token);
        string name = new('x', 4096); byte[] nameBytes = new byte[4100];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(nameBytes, 4096); System.Text.Encoding.Latin1.GetBytes(name).CopyTo(nameBytes, 4);
        var part = seed.Parts[0] with { Name = name, NameBytes = nameBytes };
        var clip = new MotionClip { Header = seed.Header, LoopTime = seed.LoopTime, FrameCount = seed.FrameCount, Parts = Enumerable.Repeat(part, 4096).ToArray() };
        byte[] source = Write(clip);
        var doc = FormatRegistry.Default.OpenBytes("motion.zbd", ResourceReaderTests.Archive(("large_motion", source)), token: token);
        var asset = Assert.Single(doc.Assets); Assert.IsType<MotionClip>(asset.Content);
        var metadata = asset.Metadata["motion"]!;
        Assert.InRange(metadata["parts"]!.AsArray().Count, 1, 32); Assert.Equal(4096, metadata["part_count"]!.GetValue<int>());
        Assert.True(metadata["parts_truncated"]!.GetValue<bool>());
        var row = metadata["parts"]![0]!; Assert.Equal(128, row["name"]!.GetValue<string>().Length);
        Assert.Equal(4096, row["name_characters"]!.GetValue<int>()); Assert.True(row["name_truncated"]!.GetValue<bool>());
        Assert.True(ExportService.AssetJson(doc, asset, token, boundedZrd: true).ToJsonString().Length < 20_000);
        var complete = ExportService.AssetJson(doc, asset, token)["properties"]!["motion"]!;
        Assert.Equal(4096, complete["parts"]!.AsArray().Count); Assert.Equal(name, complete["parts"]![4095]!["name"]!.GetValue<string>());
    }
    [Fact]
    public void DuplicateMotionTracksLeaveTheAmbiguousNodeAtItsStoredPose()
    {
        var clip = MotionClip.Read(MotionBytes(), TestContext.Current.CancellationToken);
        var duplicate = new MotionClip { Header = clip.Header, LoopTime = clip.LoopTime, FrameCount = clip.FrameCount, Parts = [clip.Parts[0], clip.Parts[0]] };
        GameScene scene = new(); scene.Models.Add(new(0, [], [], [], [], []));
        scene.Nodes.Add(new(0, "body", "object3d", 0, [], [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
        var library = new AssetDocument("library.zbd", new(0, DateTime.MinValue), new(FormatFamily.Archive, 1, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        var preview = new MotionPreview(duplicate, library, new(0, 0, 1, 0, 1));
        Assert.Equal(Matrix4x4.Identity, Assert.Single(preview.At(.5, token: TestContext.Current.CancellationToken).Nodes).Transform);
        Assert.Equal(2, preview.Diagnostics.Count(d => d.Contains("ambiguous node")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedMw3PlacementsKeepIndependentInstancesAndCoordinateIdentities(bool nested)
    {
        var token = TestContext.Current.CancellationToken;
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-mw3-repeat-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            ZrdNode Scalar(float v) => ZrdNode.Create(ZrdKind.Float) with { Bits = BitConverter.SingleToUInt32Bits(v) };
            ZrdNode Array(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
            ZrdNode Name() => ZrdNode.Create(ZrdKind.String) with { Text = "actor_01" };
            ZrdNode Placement(float x) => Array(ZrdNode.Create(ZrdKind.Int), Array(Scalar(x), Scalar(2), Scalar(3)), Scalar(0));
            byte[] source = ResourceReaderTests.Archive(("aiv.zrd", ZrdWriter.Write(Array(Name(), Placement(10), Name(), Placement(20)), token)));
            string reader = Path.Combine(folder, "readerm1.zbd"); await File.WriteAllBytesAsync(reader, source, token);
            GameScene scene = new(); scene.Models.Add(new(0, [], [], [], [], []));
            scene.Nodes.Add(new(0, "world", "world", null, [], [1], [], []));
            if (nested) scene.Nodes.Add(new(1, "stored parent", "object3d", null, [0], [2], new() { ["flags"] = 4 },
                new() { ["transform"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 100, 0, 0) }));
            int actorIndex = nested ? 2 : 1;
            scene.Nodes.Add(new(actorIndex, "actor_01", "object3d", 0, [nested ? 1 : 0], [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
            var world = new AssetDocument(Path.Combine(folder, "gamez.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ, 27, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
            byte[] worldHeader = new byte[36]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(worldHeader, 0x02971222); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(worldHeader.AsSpan(4), 27);
            await File.WriteAllBytesAsync(world.Path, worldHeader, token);
            using AssetResolver resolver = new(folder);
            var mission = await MissionSceneLoader.LoadAsync(world, resolver, token: token);
            Assert.Equal(2, mission.Actors.Count); Assert.Equal(2, mission.Actors.Select(a => a.Root).Distinct().Count());
            Assert.Equal(2, mission.Actors.Select(a => a.CoordinateSource).Distinct().Count());
            Assert.All(mission.Actors, a => Assert.Equal(a.PlacementPosition, SceneBuilder.LocalTransform(mission.Scene.Nodes[a.Root]).Translation));
            Assert.Equal(new[] { 10f, 20f }, SceneBuilder.Assemble(mission.Scene, token: token).Placements.Select(p => p.Transform.M41));
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, token);
            var first = mission.Actors[0].CoordinateSource!;
            var moved = edits.PreviewTransform(first, new(new(30, 2, 3), edits.Rotation(first)));
            Assert.Equal(new Vector3(30, 2, 3), Assert.Single(moved).Value.Position);
            Assert.Equal(new Vector3(20, 2, 3), edits.Position(mission.Actors[1].CoordinateSource!));
            // A refresh with unchanged inputs (now naming the adopted reader) reuses the layout; a rebuilt one maps every actor.
            Assert.Same(mission, await MissionSceneLoader.LoadAsync(world, resolver, token: token));
            MissionSceneLoader.Invalidate(world);
            var again = await MissionSceneLoader.LoadAsync(world, resolver, token: token);
            Assert.NotSame(mission, again);
            Assert.All(mission.Actors, a => Assert.Equal(a.Root, again.RemapNodeFrom(mission, a.Root)));
            Assert.Equal(Matrix4x4.Identity, SceneBuilder.LocalTransform(scene.Nodes[actorIndex])); Assert.Equal(new[] { actorIndex }, scene.Nodes[nested ? 1 : 0].Children);
            Assert.Equal(source, await File.ReadAllBytesAsync(reader, token));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void WorldVersionsHaveDistinctProbesAndTriangleStripsHaveAlternatingWinding()
    {
        byte[] header = new byte[36]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0x02971222);
        foreach (uint version in new uint[] { 15, 27, 41 })
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), version);
            Assert.Equal(version == 41 ? Recognition.UnsupportedVersion : Recognition.Supported, FormatRegistry.Probe(header, [], 36).Recognition);
        }
        Assert.Equal([0, 1, 2, 2, 1, 3, 2, 3, 4], GeometryBuilder.TriangleStrip(5));
    }
    [Fact]
    public void Version39KeyframeEditsKeepSplineBytesAndVersionedRecordSizes()
    {
        var ev = AnimationCatalog.Create(12, 39);
        var frame = ev.Keyframes(TestContext.Current.CancellationToken).Single(); Assert.Equal(76, frame.ChannelStride);
        frame.SetFloat(frame.ChannelOffset(0) + 28, 1.234f);
        var changed = ev.WithKeyframes([frame]); var cloned = changed.Clone(TestContext.Current.CancellationToken);
        Assert.Equal(39u, cloned.Version); Assert.Equal(1, cloned.I32(16));
        Assert.Equal(1.234f, cloned.Keyframes(TestContext.Current.CancellationToken)[0].F32(40));
        Assert.Equal(328, AnimationCatalog.Find(10, 39)!.DurationOffset);
        Assert.Equal(248, AnimationCatalog.Find(10)!.DurationOffset);
        Assert.NotNull(AnimationCatalog.Find(42, 39)); Assert.Null(AnimationCatalog.Find(42));
        Assert.DoesNotContain(AnimationCatalog.ForVersion(39), e => e.Support.StartsWith("Engine-based", StringComparison.Ordinal));
        frame.SetInt(frame.ChannelOffset(0) + 32, unchecked((int)0x7FA12345));
        Assert.Equal(frame.Bytes, ev.WithKeyframes([frame]).Keyframes(TestContext.Current.CancellationToken)[0].Bytes);
        var channels = frame.WithChannels(7, 39); Assert.Equal(frame.Bytes.AsSpan(12, 76).ToArray(), channels.Bytes.AsSpan(12, 76).ToArray());
        Assert.Equal(frame.Bytes, channels.WithChannels(1, 39).Bytes);
        var procedural = AnimationCatalog.Create(10, 39); Assert.Equal(-1, procedural.I16(240)); Assert.Equal(-1, procedural.I16(280)); Assert.Equal(-1, procedural.I16(320));
    }
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 12)]
    public void Version39KeyframesRejectCountsThatDoNotDescribeTheCompletePayload(int count, int trailingBytes)
    {
        var ev = AnimationCatalog.Create(12, 39).WithKeyframes([AnimationKeyframe.Create(1), AnimationKeyframe.Create(7)]);
        byte[] bytes = new byte[ev.Bytes.Length + trailingBytes]; ev.Bytes.CopyTo(bytes, 0);
        ev = new AnimationEvent(bytes) { Version = 39 }; ev.SetInt(4, bytes.Length); ev.SetInt(16, count);
        byte[] original = bytes.ToArray();
        Assert.Throws<InvalidDataException>(() => ev.Keyframes(TestContext.Current.CancellationToken));
        Assert.NotNull(ev.ToJson(TestContext.Current.CancellationToken)["keyframe_diagnostic"]);
        Assert.Equal(original, ev.Bytes);
    }
    [Fact]
    public void Version39KeyframesAcceptExactEmptyAndMixedChannelStreamsAndRejectTruncation()
    {
        var token = TestContext.Current.CancellationToken;
        var empty = AnimationCatalog.Create(12, 39).WithKeyframes([]);
        Assert.Empty(empty.Keyframes(token)); Assert.Equal(0, empty.I32(16)); Assert.Equal(36, empty.Bytes.Length);
        var mixed = empty.WithKeyframes([AnimationKeyframe.Create(0), AnimationKeyframe.Create(1), AnimationKeyframe.Create(7)]);
        Assert.Equal(new[] { 12, 88, 240 }, mixed.Keyframes(token).Select(f => f.Bytes.Length)); Assert.Equal(3, mixed.I32(16));
        foreach (int length in new[] { 16, 35, 36, mixed.Bytes.Length - 1 })
            Assert.Throws<InvalidDataException>(() => new AnimationEvent(mixed.Bytes[..length]) { Version = 39 }.Keyframes(token));
        var recoil = AnimationCatalog.Create(12).WithKeyframes([AnimationKeyframe.Create(1), AnimationKeyframe.Create(7)]);
        recoil.SetInt(16, -1); Assert.Equal(2, recoil.Keyframes(token).Count); // RECOIL's field is reserved, not a count.
    }
    [Fact]
    public async Task OptionalBaseGameCorpusParsesAndAnimationsRoundTrip()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_MW3_CORPUS"); if (root == null) return;
        var token = TestContext.Current.CancellationToken; int worlds = 0, animations = 0, motions = 0, assemblies = 0, invalidTransforms = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.zbd", SearchOption.AllDirectories))
        {
            var doc = await FormatRegistry.Default.OpenAsync(file, token);
            Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "Error");
            if (doc.Probe.Family == FormatFamily.GameZ)
            {
                worlds++; Assert.Equal(27u, doc.Probe.Version); Assert.NotNull(doc.Scene);
                var textures = doc.Assets.Where(a => a.Kind == AssetKind.TextureReference).ToArray();
                Assert.All(textures, a => { Assert.Equal(40, a.Length); Assert.False(string.IsNullOrWhiteSpace(a.Name)); Assert.DoesNotContain(a.Name, char.IsControl); });
                Assert.Equal(textures.Length, textures.Select(a => a.Offset).Distinct().Count());
                foreach (var model in doc.Scene.Models) _ = GeometryBuilder.Build(model, token: token);
            }
            if (doc.Animations is { } package)
            {
                animations++; Assert.Equal(39u, package.Version); Assert.Equal(doc.Bytes.ToArray(), AnimationWriter.Write(package, token));
                foreach (var ev in package.Entries.SelectMany(e => e.AllSequences).SelectMany(s => s.Events))
                {
                    if (ev.Type == 12)
                    {
                        Assert.Equal(ev.I32(16), ev.Keyframes(token).Count);
                        if (ev.KeyframePreviewDiagnostic(token) is { } unsupported)
                        {
                            invalidTransforms++;
                            // Retail data includes reversed time spans. Do not invent their runtime meaning:
                            // keep the stream inspectable/editable and surface the unsupported preview.
                            Assert.Contains("unverified runtime meaning", unsupported);
                            Assert.Equal(unsupported, ev.ToPreviewJson(token)["keyframe_diagnostic"]!.GetValue<string>());
                            Assert.Equal(unsupported, Assert.Throws<InvalidDataException>(() => ev.PlaybackKeyframes()).Message);
                            Assert.Contains(doc.Diagnostics, d => d.Severity == "Warning" && d.Offset == ev.SourceOffset);
                        }
                    }
                    if (ev.Spec != null) Assert.True(ev.Bytes.Length >= ev.Spec.Size, $"{file}: event {ev.Type} is smaller than its layout.");
                }
            }
            foreach (var asset in doc.Assets)
            {
                if (asset.Content is MotionClip) motions++;
                if (asset.Content is MechAssembly assembly) { assemblies++; Assert.NotEmpty(SceneBuilder.ForAsset(doc.Scene!, asset, token: token).Placements); }
            }
        }
        Assert.Equal(6, worlds); Assert.Equal(6, animations); Assert.Equal(258, motions); Assert.Equal(57, assemblies); Assert.Equal(456, invalidTransforms);
    }
    /// <summary>The bytes of <paramref name="clip"/> as a motion member stores them.</summary>
    private static byte[] Write(MotionClip clip)
    {
        using MemoryStream s = new(); using BinaryWriter w = new(s);
        byte[] header = clip.Header.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(4), clip.LoopTime);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), clip.FrameCount); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), clip.Parts.Count);
        w.Write(header);
        foreach (var part in clip.Parts)
        {
            w.Write(part.NameBytes.Span); w.Write(part.Flags);
            foreach (var f in part.Frames) { w.Write(f.Translation.X); w.Write(f.Translation.Y); w.Write(f.Translation.Z); }
            foreach (var f in part.Frames) { w.Write(f.Rotation.W); w.Write(f.Rotation.X); w.Write(f.Rotation.Y); w.Write(f.Rotation.Z); }
        }
        return s.ToArray();
    }
    internal static byte[] MotionBytes()
    {
        using MemoryStream s = new(); using BinaryWriter w = new(s);
        w.Write(4); w.Write(2f); w.Write(2); w.Write(1); w.Write(-1f); w.Write(1f);
        w.Write(4); w.Write("body"u8); w.Write(12);
        foreach (var p in new[] { Vector3.Zero, new Vector3(2, 0, 0), Vector3.Zero }) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        for (int i = 0; i < 3; i++) { w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f); }
        return s.ToArray();
    }
}
