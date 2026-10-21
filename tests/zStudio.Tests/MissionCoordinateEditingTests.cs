using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Formats;
using Xunit;

namespace zStudio.Tests;

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
            var previous = new zStudio.Core.Animation.AnimationPreviewContext { Package = new() { Prefix = new byte[72], Tail = [] }, World = world, Mission = medium };
            previous.RootOverrides[0] = actor.Root;
            var next = new zStudio.Core.Animation.AnimationPreviewContext { Package = previous.Package, World = world, Mission = easy };
            next.RemapBindingsFrom(previous);
            Assert.Empty(next.RootOverrides); Assert.Contains(next.Diagnostics, d => d.Contains("cleared"));
        });
    }

[Fact]
    public async Task DuplicateTankRecordsRemainInspectable()
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
            Assert.Equal(2, edits.OtherCoordinates.Count(c => c.Kind == "tank"));
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
            Assert.Throws<InvalidDataException>(() => edits.PreviewTransform(inactive.Source, new(Vector3.Zero, inactive.Rotation)));
        });
    }

}
