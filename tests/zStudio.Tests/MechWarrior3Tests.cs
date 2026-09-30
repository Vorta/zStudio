using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Export;
using Xunit;

namespace Recoil.Zbd.Tests;

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
        byte[] source = clip.Write(token);
        var doc = FormatRegistry.Default.OpenBytes("motion.zbd", ResourceEditingTests.Archive(("large_motion", source)), token: token);
        var asset = Assert.Single(doc.Assets); Assert.IsType<MotionClip>(asset.Content);
        var metadata = asset.Metadata["motion"]!;
        Assert.InRange(metadata["parts"]!.AsArray().Count, 1, 32); Assert.Equal(4096, metadata["part_count"]!.GetValue<int>());
        Assert.True(metadata["parts_truncated"]!.GetValue<bool>());
        var row = metadata["parts"]![0]!; Assert.Equal(128, row["name"]!.GetValue<string>().Length);
        Assert.Equal(4096, row["name_characters"]!.GetValue<int>()); Assert.True(row["name_truncated"]!.GetValue<bool>());
        Assert.True(ExportService.AssetJson(doc, asset, token, boundedZrd: true).ToJsonString().Length < 20_000);
        var complete = ExportService.AssetJson(doc, asset, token)["properties"]!["motion"]!;
        Assert.Equal(4096, complete["parts"]!.AsArray().Count); Assert.Equal(name, complete["parts"]![4095]!["name"]!.GetValue<string>());
        Assert.Equal(source, ((MotionClip)asset.Content!).Write(token));
    }
    [Fact]
    public void DuplicateMotionTracksLeaveTheAmbiguousNodeAtItsStoredPose()
    {
        var clip = MotionClip.Read(MotionBytes(), TestContext.Current.CancellationToken);
        var duplicate = new MotionClip { Header = clip.Header, LoopTime = clip.LoopTime, FrameCount = clip.FrameCount, Parts = [clip.Parts[0], clip.Parts[0]] };
        GameScene scene = new(); scene.Models.Add(new(0, [], [], [], [], []));
        scene.Nodes.Add(new(0, "body", "object3d", 0, [], [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
        var library = new ZbdDocument("library.zbd", new(0, DateTime.MinValue), new(FormatFamily.Archive, 1, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
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
            byte[] source = ResourceEditingTests.Archive(("aiv.zrd", ZrdWriter.Write(Array(Name(), Placement(10), Name(), Placement(20)), token)));
            string reader = Path.Combine(folder, "readerm1.zbd"); await File.WriteAllBytesAsync(reader, source, token);
            GameScene scene = new(); scene.Models.Add(new(0, [], [], [], [], []));
            scene.Nodes.Add(new(0, "world", "world", null, [], [1], [], []));
            if (nested) scene.Nodes.Add(new(1, "stored parent", "object3d", null, [0], [2], new() { ["flags"] = 4 },
                new() { ["transform"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 100, 0, 0) }));
            int actorIndex = nested ? 2 : 1;
            scene.Nodes.Add(new(actorIndex, "actor_01", "object3d", 0, [nested ? 1 : 0], [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
            var world = new ZbdDocument(Path.Combine(folder, "gamez.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ, 27, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
            byte[] worldHeader = new byte[36]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(worldHeader, 0x02971222); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(worldHeader.AsSpan(4), 27);
            await File.WriteAllBytesAsync(world.Path, worldHeader, token);
            using AssetResolver resolver = new(folder);
            var mission = await MissionSceneLoader.LoadAsync(world, resolver, token: token);
            Assert.Equal(2, mission.Actors.Count); Assert.Equal(2, mission.Actors.Select(a => a.Root).Distinct().Count());
            Assert.Equal(2, mission.Actors.Select(a => a.CoordinateSource).Distinct().Count());
            Assert.All(mission.Actors, a => Assert.Equal(a.PlacementPosition, SceneBuilder.LocalTransform(mission.Scene.Nodes[a.Root]).Translation));
            Assert.Equal(new[] { 10f, 20f }, SceneBuilder.Assemble(mission.Scene, token: token).Placements.Select(p => p.Transform.M41));
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, token);
            var first = mission.Actors[0]; Assert.True(edits.MoveTo(first.CoordinateSource!, new(30, 2, 3)));
            var positions = edits.TankPreviewPositions(mission);
            Assert.Equal(new Vector3(30, 2, 3), positions[first.Root]); Assert.Equal(new Vector3(20, 2, 3), positions[mission.Actors[1].Root]);
            var again = await MissionSceneLoader.LoadAsync(world, resolver, token: token);
            Assert.All(mission.Actors, a => Assert.Equal(a.Root, again.RemapNodeFrom(mission, a.Root)));
            Assert.Equal(Matrix4x4.Identity, SceneBuilder.LocalTransform(scene.Nodes[actorIndex])); Assert.Equal(new[] { actorIndex }, scene.Nodes[nested ? 1 : 0].Children);
            Assert.Equal(source, await File.ReadAllBytesAsync(reader, token));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void Version39WaterAndLavaReferencesProtectSequenceIdentity()
    {
        byte[] prefix = new byte[80]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 39);
        var package = new AnimationPackage { Prefix = prefix, Tail = [] }; var entry = new AnimationEntry(new byte[316], 0, 80); package.Entries.Add(entry);
        var sequence = new AnimationSequence(new byte[64]); sequence.Name = "impact"; entry.Sequences.Add(sequence);
        var ev = AnimationCatalog.Create(10, 39); ev.SetText(248, "impact"); ev.SetShort(280, 0); ev.SetText(288, "impact"); ev.SetShort(320, 0); entry.Primary.Events.Add(ev);
        var edits = new AnimationEditSession(package);
        Assert.Throws<InvalidDataException>(() => edits.DeleteSequence(0, sequence.Id));
        edits.RenameSequence(0, sequence.Id, "renamed"); ev = package.Entries[0].Primary.Events[0];
        Assert.Equal("renamed", ev.Text(248)); Assert.Equal("renamed", ev.Text(288)); Assert.Equal(-1, ev.I16(280)); Assert.Equal(-1, ev.I16(320));
        edits.Undo(); Assert.Equal("impact", package.Entries[0].Primary.Events[0].Text(248));
        var bytes = AnimationWriter.Write(package, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, AnimationWriter.Write(AnimationPackage.Read(bytes, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task OptionalVersion27ModelReplacementPreservesOtherRecords()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_MW3_CORPUS"); if (root == null) return;
        var token = TestContext.Current.CancellationToken;
        var world = await FormatRegistry.Default.OpenAsync(Path.Combine(root, "c1", "gamez.zbd"), token);
        var model = world.Scene!.Models.First(m => m.Morphs.Length == 0 && m.Metadata.Int("light_count") == 0 && m.Vertices.Length >= 3 && world.Scene.Nodes.Any(n => n.ModelIndex == m.Index));
        var mesh = new ImportedMesh(model.Vertices.Take(3).ToArray(), [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0,1,2]);
        byte[] bytes = ModelReplacementWriter.Replace(world, new Dictionary<int, ImportedMesh> { [model.Index] = mesh }, "mw3_edit_test", token);
        var changed = FormatRegistry.Default.OpenBytes(world.Path, bytes, token: token);
        Assert.DoesNotContain(changed.Diagnostics, d => d.Severity == "Error"); Assert.Equal(mesh.Positions, changed.Scene!.Models[model.Index].Vertices);
        Assert.Equal(world.Scene.Textures.Count + 1, changed.Scene.Textures.Count); Assert.Equal("mw3_edit_test", changed.Scene.Textures[^1].Text("name"));
        foreach (var original in world.Assets.Where(a => a.Kind is AssetKind.Model or AssetKind.TextureReference && !(a.Kind == AssetKind.Model && a.Index == model.Index)))
        {
            var copy = changed.Assets.Single(a => a.Kind == original.Kind && a.Index == original.Index);
            Assert.Equal(world.Slice(original.Offset, original.Length).ToArray(), changed.Slice(copy.Offset, copy.Length).ToArray());
        }
    }
    [Fact]
    public void MotionRoundTripEditingAndLoopSamplingRetainClosingSample()
    {
        byte[] bytes = MotionBytes(); var clip = MotionClip.Read(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, clip.Write(TestContext.Current.CancellationToken));
        Assert.Equal(new Vector3(1, 0, 0), clip.Sample(0, .5).Translation);
        Assert.Equal(clip.Sample(0, .25), clip.Sample(0, 2.25));
        var edited = clip.Edit("set", 0, 0, new(new(4, 5, 6), Quaternion.Identity));
        Assert.Equal(clip.Parts[0].Frames[^1], edited.Parts[0].Frames[^1]);
        Assert.Equal(Vector3.Zero, clip.Parts[0].Frames[0].Translation);
        Assert.Equal(edited.Write(TestContext.Current.CancellationToken), MotionClip.Read(edited.Write(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken).Write(TestContext.Current.CancellationToken));
        var inserted = clip.Edit("insert", frame: 0); Assert.Equal(3, inserted.FrameCount);
        Assert.Equal(bytes, inserted.Edit("delete", frame: 1).Write(TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => clip.Edit("timing", loopTime: float.NaN));
        Assert.Throws<InvalidDataException>(() => clip.Edit("set", 0, 0, new(Vector3.Zero, default)));
        Assert.Throws<InvalidDataException>(() => MotionClip.Read(bytes.AsMemory(0, bytes.Length - 1), TestContext.Current.CancellationToken));
        byte[] enormous = bytes.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(enormous.AsSpan(8), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => MotionClip.Read(enormous, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("set", 0)]
    [InlineData("set", 1)]
    [InlineData("insert", 0)]
    [InlineData("insert", 1)]
    [InlineData("delete", 0)]
    [InlineData("delete", 1)]
    public void MotionEditsPreserveDistinctClosingSamplesOnEveryTrack(string action, int frame)
    {
        var token = TestContext.Current.CancellationToken; var seed = MotionClip.Read(MotionBytes(), token);
        var first = seed.Parts[0] with { Frames = [.. seed.Parts[0].Frames.Take(2), new(new(7, 8, -0f), new(.25f, .5f, .75f, 2))] };
        var second = first with { Frames = [first.Frames[0], first.Frames[1], new(new(-7, -8, -9), new(1, 2, 3, 4))] };
        var clip = new MotionClip { Header = seed.Header, LoopTime = seed.LoopTime, FrameCount = seed.FrameCount, Parts = [first, second] };
        byte[] original = clip.Write(token);
        var edited = clip.Edit(action, 0, frame, new(new(4, 5, 6), Quaternion.Identity));
        var readback = MotionClip.Read(edited.Write(token), token);
        for (int p = 0; p < 2; p++)
        {
            Assert.Equal(Bits(clip.Parts[p].Frames[^1]), Bits(readback.Parts[p].Frames[^1]));
            Assert.Equal(edited.FrameCount + 1, readback.Parts[p].Frames.Count);
        }
        if (action == "set") Assert.Equal(clip.Parts[1].Frames, readback.Parts[1].Frames);
        if (action == "insert") Assert.Equal(original, edited.Edit("delete", frame: frame + 1).Write(token));
        Assert.Equal(original, clip.Write(token));
        static int[] Bits(MotionFrame f) => new[] { f.Translation.X, f.Translation.Y, f.Translation.Z, f.Rotation.W, f.Rotation.X, f.Rotation.Y, f.Rotation.Z }.Select(BitConverter.SingleToInt32Bits).ToArray();
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
    public async Task MotionEditsUseMemberIdentityAndVerifiedArchiveHistory()
    {
        var token = TestContext.Current.CancellationToken;
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-motion-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "motion.zbd"), copy = Path.Combine(folder, "copy.zbd");
            byte[] clip = MotionBytes(); using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
            writer.Write(clip); writer.Write(clip);
            for (int i = 0; i < 2; i++) { byte[] record = new byte[148]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record, i * clip.Length); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), clip.Length); "same"u8.CopyTo(record.AsSpan(8)); writer.Write(record); }
            writer.Write(1); writer.Write(2); byte[] source = stream.ToArray(); await File.WriteAllBytesAsync(path, source, token);
            var edits = new ResourceEditSession(await FormatRegistry.Default.OpenAsync(path, token)); var member = edits.Current.Members[1].Id;
            var prepared = await edits.PrepareMotionAsync(member, "set", 0, 1, new(new(3, 4, 5), Quaternion.Identity), token: token); edits.Accept(prepared);
            Assert.Equal(clip, edits.Current.Members[0].Data.ToArray()); Assert.True(edits.IsDirty);
            Assert.Equal(new Vector3(3, 4, 5), MotionClip.Read(edits.Member(member).Data, token).Parts[0].Frames[1].Translation);
            Assert.Throws<InvalidOperationException>(() => edits.Accept(prepared));
            edits.UndoRedo(false); Assert.False(edits.IsDirty); Assert.Equal(source, edits.Current.Document.Bytes.ToArray());
            edits.UndoRedo(true); await edits.SaveAsync(copy, token); Assert.False(edits.IsDirty);
            Assert.Equal(source, await File.ReadAllBytesAsync(path, token));
            var saved = await FormatRegistry.Default.OpenAsync(copy, token); Assert.Equal(edits.Current.Document.Bytes.ToArray(), saved.Bytes.ToArray());
            edits.Accept(await edits.PrepareMotionAsync(member, "insert", frame: 0, token: token)); Assert.Equal(3, MotionClip.Read(edits.Member(member).Data, token).FrameCount);
            edits.UndoRedo(false); Assert.False(edits.IsDirty);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task OptionalMissionIsolationMechEditingAndMotionHierarchy()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_MW3_CORPUS"); if (root == null) return;
        var token = TestContext.Current.CancellationToken; using AssetResolver resolver = new(root);
        var world = await resolver.OpenCachedAsync(Path.Combine(root, "c1", "gamez.zbd"), token);
        byte[] original = world.Bytes.ToArray(); var choices = await MissionSceneLoader.Mw3MissionsAsync(world.Path, resolver, token); Assert.True(choices.Count > 1);
        resolver.SelectMission(world.Path, choices[0].Archive); var first = await MissionSceneLoader.LoadAsync(world, resolver, token: token);
        Assert.NotEmpty(first.Actors); Assert.All(first.Actors, a => Assert.Equal(Path.GetFullPath(choices[0].Archive).ToUpperInvariant(), a.CoordinateSource!.ArchivePath));
        Assert.All(first.AiNetworks.Networks, n => Assert.Equal(choices[0].Archive, n.Archive));
        Assert.NotEmpty(first.AiNetworks.Networks.SelectMany(n => n.Constraints));
        var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, token);
        var actor = first.Actors.First(a => a.CoordinateSource != null); var source = actor.CoordinateSource!;
        Assert.True(edits.MoveTo(source, actor.PlacementPosition!.Value + Vector3.UnitX)); Assert.Single(edits.Scope(source).Sources);
        resolver.SelectMission(world.Path, choices[1].Archive); var second = await MissionSceneLoader.LoadAsync(world, resolver, token: token);
        Assert.All(second.AiNetworks.Networks, n => Assert.Equal(choices[1].Archive, n.Archive));
        Assert.DoesNotContain(second.Actors, a => a.CoordinateSource == source); Assert.True(edits.IsDirty); Assert.Equal(-1, second.RemapNodeFrom(first, actor.Root));
        resolver.SelectMission(world.Path, choices[0].Archive); var again = await MissionSceneLoader.LoadAsync(world, resolver, token: token);
        Assert.True(again.RemapNodeFrom(first, actor.Root) >= 0); Assert.Equal(original, world.Bytes.ToArray());
        string working = Path.Combine(Path.GetTempPath(), "zstudio-mw3-coordinates-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(working);
        try
        {
            byte[] sourceReader = await File.ReadAllBytesAsync(choices[0].Archive, token), expected = edits.EncodeArchive(choices[0].Archive);
            string copy = Path.Combine(working, "reader-copy.zbd");
            var saved = await edits.SaveAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [choices[0].Archive] = copy }, token: token);
            Assert.Empty(saved.Errors); Assert.False(edits.IsDirty); Assert.Equal(expected, await File.ReadAllBytesAsync(copy, token));
            Assert.Equal(sourceReader, await File.ReadAllBytesAsync(choices[0].Archive, token));
            Assert.DoesNotContain((await FormatRegistry.Default.OpenAsync(copy, token)).Diagnostics, d => d.Severity == "Error");
            edits.Undo(); Assert.Equal(sourceReader, edits.EncodeArchive(choices[0].Archive));
        }
        finally { Directory.Delete(working, true); }
        var library = await MotionLibrary.LoadAsync(Path.Combine(root, "motion.zbd"), resolver, token);
        var motions = await resolver.OpenCachedAsync(Path.Combine(root, "motion.zbd"), token); var motionAsset = motions.Assets.First(a => a.Content is MotionClip);
        int memberIndex = MotionLibrary.SuggestedMember(motionAsset.Name, library)!.Value; var assembly = (MechAssembly)library.Assets[memberIndex].Content!;
        var preview = new MotionPreview((MotionClip)motionAsset.Content!, library, assembly);
        Assert.NotEmpty(preview.At(.2, token: token).Nodes); Assert.Equal(preview.At(.2, token: token).Nodes, preview.At(.2, token: token).Nodes);
        var model = library.Scene!.Models[assembly.FirstModel]; var center = (model.Vertices.Aggregate(Vector3.Min) + model.Vertices.Aggregate(Vector3.Max)) / 2;
        var mesh = new ImportedMesh([center, center + new Vector3(.001f, 0, 0), center + new Vector3(0, .001f, 0)], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2]) { Colors = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ] };
        var resources = new ResourceEditSession(library); var member = resources.Current.Members[memberIndex].Id;
        resources.Accept(await resources.PrepareMechModelAsync(member, 0, mesh, model.Polygons[0].MaterialIndex, token));
        var changed = resources.Current.Document.Scene!.Models[assembly.FirstModel]; Assert.Equal(mesh.Positions, changed.Vertices); Assert.Equal(mesh.Colors.Select(c => c * 255), changed.Polygons[0].Colors);
        resources.UndoRedo(false); Assert.Equal(library.Bytes.ToArray(), resources.Current.Document.Bytes.ToArray());
    }
    [Fact]
    public async Task OptionalBaseGameCorpusParsesAndAnimationsAndMotionsRoundTrip()
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
                if (asset.Content is MotionClip clip) { motions++; Assert.Equal(doc.Slice(asset.Offset, asset.Length).ToArray(), clip.Write(token)); }
                if (asset.Content is MechAssembly assembly) { assemblies++; Assert.NotEmpty(SceneBuilder.ForAsset(doc.Scene!, asset, token: token).Placements); }
            }
        }
        Assert.Equal(6, worlds); Assert.Equal(6, animations); Assert.Equal(258, motions); Assert.Equal(57, assemblies); Assert.Equal(456, invalidTransforms);
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
