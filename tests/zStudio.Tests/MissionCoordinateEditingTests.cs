using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    private static JsonNode CoordinateInt(int value) => new JsonObject { ["type"] = "int", ["value"] = value };
    private static JsonNode AiCoordinateNode(float x = 1) => Arr(CoordinateInt(12), Arr(Num(x), Num(2), Num(3)), Arr(CoordinateInt(-7), CoordinateInt(0), CoordinateInt(-1)));

    [Fact]
    public async Task TankSelectionFollowsOnlyUniqueCoordinateCounterpartsAcrossDifficulty()
    {
        await WithMissionArchiveAsync(new()
        {
            ["vehicle.zrd"] = Zrd(Arr(Str("animated"), Arr())),
            ["aiv.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(10, 2, 3, 45))),
            ["aiv_hard.zrd"] = Zrd(Arr(Str("animated_99"), Spawn(10, 2, 3, 45))),
            ["aiv_easy.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(50, 2, 3, 45))),
        }, async (world, resolver) =>
        {
            var token = TestContext.Current.CancellationToken;
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, token); edits.BindCoordinateTemplates(world.Scene!);
            var medium = await MissionSceneLoader.LoadAsync(world, resolver, token: token, difficulty: MissionDifficulty.Medium);
            var hard = await MissionSceneLoader.LoadAsync(world, resolver, token: token, difficulty: MissionDifficulty.Hard);
            var easy = await MissionSceneLoader.LoadAsync(world, resolver, token: token, difficulty: MissionDifficulty.Easy);
            var actor = Assert.Single(medium.Actors, a => a.CoordinateSource != null);
            var matches = edits.Scope(actor.CoordinateSource!).Sources.ToHashSet();
            Assert.Equal(Assert.Single(hard.Actors, a => a.CoordinateSource != null).Root, hard.RemapNodeFrom(medium, actor.Root, matches));
            Assert.Equal(-1, easy.RemapNodeFrom(medium, actor.Root, matches)); // Same name is not proof of identity.
            Assert.Equal(-1, easy.RemapNodeFrom(medium, actor.Root)); // Callers without a verified scope must never fall back to names.
            Assert.Equal(-1, hard.RemapNodeFrom(medium, actor.Root));
            Assert.Equal(actor.Root, medium.RemapNodeFrom(medium, actor.Root)); // Exact source provenance survives refresh/fallback layouts.
            var previous = new Recoil.Zbd.Core.Animation.AnimationPreviewContext { Package = new() { Prefix = new byte[72], Tail = [] }, World = world, Mission = medium };
            previous.RootOverrides[0] = actor.Root;
            var next = new Recoil.Zbd.Core.Animation.AnimationPreviewContext { Package = previous.Package, World = world, Mission = easy };
            next.RemapBindingsFrom(previous);
            Assert.Empty(next.RootOverrides); Assert.Contains(next.Diagnostics, d => d.Contains("cleared"));
        });
    }

    [Fact]
    public async Task CoordinatePartialSaveAsRetainsEveryCopyAndNeverRetriesIntoSources()
    {
        await WithMissionArchiveAsync(new() { ["net_01.zrd"] = Zrd(Arr(Str("node_00"), AiCoordinateNode())) }, async (world, resolver) =>
        {
            var token = TestContext.Current.CancellationToken;
            string root = Path.GetDirectoryName(world.Path)!;
            var first = await resolver.OpenCachedAsync(Path.Combine(root, "resources.zbd"), token);
            string secondPath = Path.Combine(root, "other.zbd");
            await File.WriteAllBytesAsync(secondPath, first.Bytes.ToArray(), token);
            var second = FormatRegistry.Default.OpenBytes(secondPath, first.Bytes.ToArray(), FileStamp.Read(secondPath));
            var edits = PickupPlacementEditSession.Create([]);
            edits.AddCoordinates(new[] { (first, first.Assets[0]), (second, second.Assets[0]) });
            foreach (var coordinate in edits.OtherCoordinates) edits.MoveTo(coordinate.Source, new(4, 5, 6));
            string copy1 = Path.Combine(root, "copy1.zbd"), copy2 = Path.Combine(root, "copy2.zbd");
            var publish = edits.PublishFile;
            edits.PublishFile = (temp, target, replace, backup) => { if (target == copy2) throw new IOException("Injected publication failure"); publish(temp, target, replace, backup); };
            var result = await edits.SaveAsync(new Dictionary<string, string> { [first.Path] = copy1, [second.Path] = copy2 }, token: token);
            Assert.Equal(new[] { copy1 }, result.SavedPaths); Assert.Single(result.Errors);
            Assert.Equal(copy2, edits.TargetPath(second.Path)); Assert.True(edits.IsDirty);
            edits.PublishFile = publish;
            await File.WriteAllTextAsync(copy2, "competing file", token);
            Assert.True(edits.HasExternalChanges());
            await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: token));
            Assert.Equal("competing file", await File.ReadAllTextAsync(copy2, token)); File.Delete(copy2);
            Assert.False(edits.HasExternalChanges());
            Assert.Empty((await edits.SaveAsync(token: token)).Errors); Assert.False(edits.IsDirty);
            Assert.Equal(first.Bytes.ToArray(), await File.ReadAllBytesAsync(first.Path, token));
            Assert.Equal(second.Bytes.ToArray(), await File.ReadAllBytesAsync(second.Path, token));
            // Unchanged documents still have an outstanding copy after failed publication.
            string copy3 = Path.Combine(root, "copy3.zbd");
            edits.PublishFile = (_, _, _, _) => throw new IOException("Unavailable");
            Assert.Single((await edits.SaveAsync(new Dictionary<string, string> { [first.Path] = copy3 }, token: token)).Errors);
            Assert.True(edits.IsDirty); Assert.True(edits.IsArchiveDirty(first.Path));
            edits.PublishFile = publish;
            Assert.Empty((await edits.SaveAsync(token: token)).Errors); Assert.False(edits.IsDirty);
        });
    }

    [Fact]
    public async Task DuplicateTankRecordsRemainInspectableWithoutAnAmbiguousPreviewTransform()
    {
        await WithMissionArchiveAsync(new()
        {
            ["aiv.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(1, 2, 3, 0), Str("animated_01"), Spawn(7, 8, 9, 0))),
            ["vehicle.zrd"] = Zrd(Arr(Str("animated"), Arr())),
        }, async (world, resolver) =>
        {
            var mission = await MissionSceneLoader.LoadAsync(world, resolver, token: TestContext.Current.CancellationToken);
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken);
            edits.BindCoordinateTemplates(world.Scene!);
            Assert.Equal(2, mission.Actors.Count(a => a.CoordinateSource != null));
            Assert.Single(mission.Actors.Where(a => a.CoordinateSource != null).Select(a => a.Root).Distinct());
            Assert.Empty(edits.TankPreviewPositions(mission));
        });
    }

    [Fact]
    public async Task ShadowedTankResourceHasReadOnlyScope()
    {
        await WithMissionArchiveAsync(new() { ["aiv.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(1, 2, 3, 0))) }, async (world, resolver) =>
        {
            var archive = await resolver.OpenCachedAsync(Path.Combine(Path.GetDirectoryName(world.Path)!, "resources.zbd"), TestContext.Current.CancellationToken);
            var shadow = FormatRegistry.Default.OpenBytes(Path.Combine(Path.GetDirectoryName(world.Path)!, "shadow.zbd"), archive.Bytes.ToArray());
            var edits = PickupPlacementEditSession.Create([]);
            edits.AddCoordinates(new[] { (archive, archive.Assets[0]), (shadow, shadow.Assets[0]) });
            edits.BindCoordinateTemplates(world.Scene!);
            var inactive = edits.OtherCoordinates.Single(r => r.Difficulties.Count == 0);
            Assert.Contains("Read-only", edits.Scope(inactive.Source).Description);
            Assert.Throws<InvalidDataException>(() => edits.MoveTo(inactive.Source, Vector3.Zero));
        });
    }

    [Fact]
    public async Task MissionCoordinatesShareArchiveUndoAndVerifiedSaveWithoutChangingLinksOrHeading()
    {
        await WithMissionArchiveAsync(new()
        {
            ["puppies.zrd"] = Zrd(PickupList(PickupRow())),
            ["net_01.zrd"] = Zrd(Arr(Str("node_00"), AiCoordinateNode())),
            ["aiv.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(20, 2, 3, 90))),
            ["aiv_easy.zrd"] = Zrd(Arr(Str("animated_02"), Spawn(20, 2, 3, 90))),
            ["aiv_hard.zrd"] = Zrd(Arr(Str("animated_03"), Spawn(20, 2, 3, 90))),
            ["unrelated.bin"] = [18, 29, 190, 41]
        }, async (world, resolver) =>
        {
            var token = TestContext.Current.CancellationToken;
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, token);
            edits.BindCoordinateTemplates(world.Scene!);
            Assert.Empty(edits.Diagnostics);
            var pickup = Assert.Single(edits.Records);
            var ai = Assert.Single(edits.OtherCoordinates, r => r.Kind == "ai");
            var tank = edits.OtherCoordinates.Single(r => r.Kind == "tank" && r.Difficulties.Contains(MissionDifficulty.Medium));
            Assert.Equal(3, edits.Scope(tank.Source).Sources.Count);
            string archive = Assert.Single(edits.ArchivePaths);
            byte[] original = edits.EncodeArchive(archive);
            edits.MoveTo(pickup.Source, new(1, 22, 3));
            edits.MoveTo(ai.Source, new(5.125f, -9, 7));
            edits.MoveTo(tank.Source, new(21, 3, 4));
            Assert.All(edits.Scope(tank.Source).Sources, s => Assert.Equal(new Vector3(21, 3, 4), edits.Position(s)));
            edits.Undo(); Assert.Equal(tank.OriginalPosition, edits.Position(tank.Source)); Assert.Equal(new Vector3(5.125f, -9, 7), edits.Position(ai.Source));
            edits.Redo(); byte[] changed = edits.EncodeArchive(archive);
            var saved = await edits.SaveAsync(token: token); Assert.Empty(saved.Errors); Assert.False(edits.IsDirty);
            Assert.Equal(changed, await File.ReadAllBytesAsync(archive, token));
            var reopened = FormatRegistry.Default.OpenBytes(archive, await File.ReadAllBytesAsync(archive, token), token: token);
            var graph = MissionAiNetworks.Read(reopened.Assets.Where(a => MissionAiNetworks.IsCandidate(a.Name)).Select(a => (reopened, a)), token);
            var node = Assert.Single(Assert.Single(graph.Networks).Nodes);
            Assert.Equal(new Vector3(5.125f, -9, 7), node.Position); Assert.Equal(new[] { -7, 0, -1 }, node.Links.Select(l => l.TargetIndex)); Assert.Equal(12, node.RawValue);
            var baseline = FormatRegistry.Default.OpenBytes(archive, original, token: token);
            var before = baseline.Assets.Single(a => a.Name == "unrelated.bin"); var after = reopened.Assets.Single(a => a.Name == "unrelated.bin");
            Assert.Equal(baseline.Slice(before.Offset, before.Length).ToArray(), reopened.Slice(after.Offset, after.Length).ToArray());
            foreach (var resource in reopened.Assets.Where(a => a.Name.StartsWith("aiv")))
            {
                var root = ZrdDecoder.Read(reopened.Slice(resource.Offset, resource.Length), token);
                Assert.Equal(90, BitConverter.UInt32BitsToSingle(root.Children[1].Children[2].Bits));
            }
            edits.Undo(); edits.Undo(); edits.Undo(); Assert.Equal(original, edits.EncodeArchive(archive)); Assert.True(edits.IsDirty);
        });
    }

    [Fact]
    public async Task TankLinksPreserveOriginalPoseAndSkipAmbiguousCounterpartsAndDeduplicateFallback()
    {
        await WithMissionArchiveAsync(new()
        {
            ["aiv.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(1, 2, 3, 0))),
            ["aiv_easy.zrd"] = Zrd(Arr(Str("animated_02"), Spawn(1, 2, 3, 0), Str("animated_03"), Spawn(1, 2, 3, 0))),
        }, async (world, resolver) =>
        {
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken);
            edits.BindCoordinateTemplates(world.Scene!);
            var selected = edits.OtherCoordinates.Single(r => r.Difficulties.Contains(MissionDifficulty.Medium));
            Assert.Contains(MissionDifficulty.Hard, selected.Difficulties); Assert.Single(edits.Scope(selected.Source).Sources);
            edits.MoveTo(selected.Source, new(4, 5, 6)); edits.MoveTo(selected.Source, new(7, 8, 9));
            Assert.All(edits.OtherCoordinates.Where(r => r.Source != selected.Source), r => Assert.Equal(r.OriginalPosition, edits.Position(r.Source)));
            Assert.Equal(new Vector3(7, 8, 9), edits.Position(selected.Source));
            Assert.Throws<InvalidDataException>(() => edits.MoveTo(selected.Source, new(float.NaN, 0, 0)));
        });
    }

    [Fact]
    public async Task InvalidAiCoordinateRetainsValidNeighboursAndCannotBeMovedOutOfPreviewRange()
    {
        await WithMissionArchiveAsync(new() { ["net_01.zrd"] = Zrd(Arr(Str("node_00"), AiCoordinateNode(float.NaN), Str("node_01"), AiCoordinateNode())) }, async (world, resolver) =>
        {
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken);
            var valid = Assert.Single(edits.OtherCoordinates); Assert.Equal("node_01", valid.Name);
            Assert.Throws<InvalidDataException>(() => edits.MoveTo(valid.Source, new(2e12f, 0, 0)));
            Assert.False(edits.IsDirty);
        });
    }
}
