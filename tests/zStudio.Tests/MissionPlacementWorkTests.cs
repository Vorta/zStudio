using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class MissionPlacementWorkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SmallPlacementGrowthIsLinearAndPublishesOneOrderedAdjacency()
    {
        var world = World(Node(0, "tank"), Node(1, "world", "world"));
        string[] before = Snapshot(world);
        var four = new MissionPlacementBudget(Token); var eight = new MissionPlacementBudget(Token);
        var a = Build(world, four, Actors(4)); var b = Build(world, eight, Actors(8));
        Assert.Equal(4, a.Actors.Count); Assert.Equal(8, b.Actors.Count);
        Assert.Equal(Enumerable.Range(2, 8), b.Scene.Nodes[1].Children);
        Assert.Equal(1, four.RootPublications); Assert.Equal(1, eight.RootPublications);
        Assert.InRange(eight.Work - four.Work, 1, 3000);
        Assert.True(eight.Work < 2 * four.Work); // Shared fixed initial-scene/index work, linear per-row work.
        Assert.All(b.Actors, actor => Assert.Equal(0, actor.SourceRoot));
        Assert.Equal(new Vector3(8, 2, 3), SceneBuilder.LocalTransform(b.Scene.Nodes[b.Actors[^1].Root]).Translation);
        Assert.Equal(before, Snapshot(world));
    }

    [Fact]
    public void LiveIndexUsesLastCurrentFullOrdinalNameAndIncludesAllLiveClasses()
    {
        var world = World(Node(0, "tank"), Node(1, "tank"), Node(2, "tank", "none"),
            Node(3, "Tank"), Node(4, "world", "world"));
        var result = Build(world, new(Token), Arr(Str("tank"), Spawn(5), Str("Tank"), Spawn(6)), Vehicles("tank", "Tank"));
        Assert.Equal(new[] { 1, 3 }, result.Actors.Select(a => a.Root));
        Assert.Equal(new[] { 1, 3 }, result.Scene.Nodes[4].Children);
        world.Scene!.Nodes.Add(Node(5, "tank", "camera"));
        var refused = Build(world, new(Token), Arr(Str("tank"), Spawn(9)));
        Assert.Empty(refused.Actors);
        Assert.Contains(refused.Diagnostics, d => d.Contains("camera", StringComparison.Ordinal));
    }

    [Fact]
    public void SuccessfulCloneDescendantsEnterLiveIndexButCannotBecomeCloneSources()
    {
        var world = World(Node(0, "tank", children: [1]), Node(1, "turret", parents: [0]), Node(2, "world", "world"));
        var result = Build(world, new(Token), Arr(Str("tank_01"), Spawn(1), Str("turret"), Spawn(2), Str("turret_01"), Spawn(3)), Vehicles("tank", "turret"));
        Assert.Equal(new[] { 3, 4 }, result.Actors.Select(a => a.Root));
        Assert.Equal(new[] { 0, 1 }, result.Actors.Select(a => a.SourceRoot));
        Assert.Equal(5, result.Scene.Nodes.Count);
        Assert.Equal(2, SceneBuilder.LocalTransform(result.Scene.Nodes[4]).M41);
        Assert.Contains(result.Diagnostics, d => d.Contains("Invalid or excessive mission hierarchy", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedAuthoredRowsKeepProvenanceAndLastPoseWithOneRootEdge()
    {
        var world = World(Node(0, "tank"), Node(1, "world", "world", children: [0, 0]));
        var source = new MissionResourceSource(Path.GetFullPath("placements.zbd"), 7, "aiv_easy.zrd");
        var result = MissionSceneLoader.BuildWithBudget(world, new MissionPlacementBudget(Token), null,
            Arr(Str("tank"), Spawn(2), Str("tank"), Spawn(8)), Vehicles("tank"), null, token: Token,
            selection: new(MissionDifficulty.Easy, "aiv_easy.zrd", "vehicle.zrd", "puppies.zrd"), aivSource: source);
        Assert.Equal(2, result.Actors.Count);
        Assert.All(result.Actors, a => Assert.Equal(0, a.Root));
        Assert.Equal(new[] { 0, 1 }, result.Actors.Select(a => a.CoordinateSource!.RecordIndex));
        Assert.Equal(new[] { 0 }, result.Scene.Nodes[1].Children);
        Assert.Equal(8, SceneBuilder.LocalTransform(result.Scene.Nodes[0]).M41);
        Assert.All(result.Actors, a => Assert.Contains("Easy", a.PlacementSource));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PickupOnlyPreservesOriginalDuplicateEdgesAndAivDeduplicatesThemOnce(bool withAiv)
    {
        var world = PickupWorld();
        var budget = new MissionPlacementBudget(Token);
        var result = MissionSceneLoader.BuildWithBudget(world, budget, null, withAiv ? Arr(Str("tank"), Spawn(1)) : null,
            Vehicles("tank"), null, token: Token, pickups: PickupRows(2));
        Assert.Equal(withAiv ? new[] { 0, 4, 6 } : new[] { 0, 0, 4, 6 }, result.Scene.Nodes[3].Children);
        Assert.Equal(1, budget.RootPublications);
        var pickups = result.Actors.Where(a => a.Pickup != null).ToArray();
        Assert.Equal(new[] { "pu00100", "pu00101" }, pickups.Select(a => a.Name));
        Assert.All(pickups, a => { Assert.Equal(1, a.SourceRoot); Assert.Equal(3, a.Pickup!.EffectiveAmount); });
        Assert.Equal(new[] { 0, 1 }, pickups.Select(a => a.Pickup!.Source.RecordIndex));
        Assert.All(pickups, a => Assert.Equal(0u, result.Scene.Nodes[result.Scene.Nodes[a.Root].Children.Single()].Metadata.UInt("flags") & 4));
    }

    [Fact]
    public void PickupTemplateIndexExcludesAppendedClonesAndRetainsOriginalAmbiguity()
    {
        var world = PickupWorld();
        var result = MissionSceneLoader.BuildWithBudget(world, new MissionPlacementBudget(Token), null,
            Arr(Str("pu001_01"), Spawn(1)), Vehicles("pu001"), null, token: Token, pickups: PickupRows(1));
        Assert.Single(result.Actors, a => a.Pickup != null);
        world.Scene!.Nodes.Add(Node(4, "pu001", children: [2]));
        var ambiguous = MissionSceneLoader.BuildWithBudget(world, new MissionPlacementBudget(Token), null, null, null, null, token: Token, pickups: PickupRows(1));
        Assert.Empty(ambiguous.Actors);
        Assert.Contains(ambiguous.Diagnostics, d => d.Contains("2 matches", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedPickupCloneConsumesItsNameBeforeTheNextRecord()
    {
        var world = PickupWorld(); var scene = world.Scene!;
        var budget = new MissionPlacementBudget(Token); var state = new MissionPlacementState(scene, 4, 3, budget); state.Initialize();
        List<int> sources = [0, 1, 2, 3]; List<MissionActor> actors = []; List<string> attempted = []; List<string> notes = [];
        MissionSceneLoader.PlacePickups(scene, sources, actors, [], 3, world.Path, 4, PickupRows(2),
            new(world.Path, 7, "puppies.zrd"), MissionLayoutSelection.For(MissionDifficulty.Medium), Clone,
            new BoundedDiagnostics(notes), Token, state, budget);
        Assert.Equal(new[] { "pu00100", "pu00101" }, attempted);
        var accepted = Assert.Single(actors); Assert.Equal("pu00101", accepted.Name);
        Assert.Equal(1, accepted.Pickup!.Source.RecordIndex);
        Assert.Contains(notes, n => n.Contains("fixture clone refusal", StringComparison.Ordinal));

        int Clone(int template, string name)
        {
            Assert.Equal(1, template); attempted.Add(name);
            if (attempted.Count == 1) throw new InvalidDataException("fixture clone refusal");
            scene.Nodes.Add(Node(4, name, children: [5])); scene.Nodes.Add(Node(5, "bvol", parents: [4]));
            sources.Add(1); sources.Add(2); state.Cloned(4); return 4;
        }
    }

    [Fact]
    public void CloneReciprocalParentsKeepRepeatedOccurrenceAndPostorder()
    {
        var world = World(Node(0, "tank", children: [1, 2]), Node(1, "shared", parents: [2, 2, 0]),
            Node(2, "branch", parents: [0], children: [1, 1]), Node(3, "world", "world"));
        var result = Build(world, new(Token), Actors(1));
        Assert.Equal(new[] { 5, 6 }, result.Scene.Nodes[4].Children);
        Assert.Equal(new[] { 5, 5 }, result.Scene.Nodes[6].Children);
        Assert.Equal(new[] { 6, 6, 4 }, result.Scene.Nodes[5].Parents);
        Assert.Equal(new[] { 0, 1, 2 }, result.SourceNodes.Skip(4));
    }

    [Fact]
    public void CyclicCloneDoesNotPolluteLaterLiveLookup()
    {
        var world = World(Node(0, "tank", children: [0]), Node(1, "good"), Node(2, "world", "world"));
        var result = Build(world, new(Token), Arr(Str("tank_01"), Spawn(1), Str("good_01"), Spawn(2)), Vehicles("tank", "good"));
        var actor = Assert.Single(result.Actors); Assert.Equal(3, actor.Root); Assert.Equal(1, actor.SourceRoot);
        Assert.Equal(4, result.Scene.Nodes.Count); Assert.Equal(4, result.SourceNodes.Count);
        Assert.Contains(result.Diagnostics, d => d.Contains("Cyclic", StringComparison.Ordinal));
    }

    [Fact]
    public void TemplateTraversingWorldSeesPendingEdgesBeforeTheFinalPublication()
    {
        var world = World(Node(0, "world", "world", parents: [2], children: [1]),
            Node(1, "leaf", parents: [0]), Node(2, "tank", children: [0]));
        string[] before = Snapshot(world); var budget = new MissionPlacementBudget(Token);
        var result = Build(world, budget, Actors(2));
        Assert.Equal(3, Assert.Single(result.Actors).Root);
        Assert.Equal(6, result.Scene.Nodes.Count);
        Assert.Equal(new[] { 1, 3 }, result.Scene.Nodes[0].Children);
        Assert.Equal(1, budget.RootPublications);
        Assert.Contains(result.Diagnostics, d => d.Contains("Invalid or excessive mission hierarchy", StringComparison.Ordinal));
        // The second template walk sees the first new actor through the world; cloning new nodes remains refused.
        Assert.Equal(before, Snapshot(world));
    }

    [Fact]
    public void OperationBudgetRefusesAcrossPlacementsWithoutPublishingAndFreshRetrySucceeds()
    {
        var world = PickupWorld(); string[] before = Snapshot(world);
        var full = new MissionPlacementBudget(Token);
        var result = MissionSceneLoader.BuildWithBudget(world, full, null, Actors(2), Vehicles("tank"), null, token: Token, pickups: PickupRows(2));
        var limited = new MissionPlacementBudget(Token, maximumWork: full.Work - 1);
        var error = Assert.Throws<InvalidDataException>(() => MissionSceneLoader.BuildWithBudget(world, limited, null, Actors(2), Vehicles("tank"), null, token: Token, pickups: PickupRows(2)));
        Assert.Contains("construction work or metadata budget", error.Message);
        Assert.True(limited.Exhausted); Assert.Equal(before, Snapshot(world));
        var retry = MissionSceneLoader.BuildWithBudget(world, new MissionPlacementBudget(Token), null, Actors(2), Vehicles("tank"), null, token: Token, pickups: PickupRows(2));
        Assert.Equal(result.Actors, retry.Actors);
        Assert.Equal(result.Scene.Nodes[3].Children, retry.Scene.Nodes[3].Children);
    }

    [Fact]
    public void AivRowsShareFullNormalizedSourceStringsAndKeepDistinctRecordIdentities()
    {
        var world = World(Node(0, "tank"), Node(1, "world", "world"));
        string archive = Path.Combine("mixed-source", new string('x', 80) + ".zbd");
        string resource = "aiv_" + new string('y', 80) + ".zrd";
        var source = new MissionResourceSource(archive, 9, resource);
        var result = MissionSceneLoader.BuildWithBudget(world, new MissionPlacementBudget(Token), null,
            Actors(3), Vehicles("tank"), null, token: Token, aivSource: source);
        var identities = result.Actors.Select(a => Assert.IsType<MissionPickupSource>(a.CoordinateSource)).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, identities.Select(i => i.RecordIndex));
        Assert.All(identities, i =>
        {
            Assert.Equal(Path.GetFullPath(archive).ToUpperInvariant(), i.ArchivePath);
            Assert.Equal(resource.ToUpperInvariant(), i.ResourceName); Assert.Equal(9, i.AssetIndex);
            Assert.Same(identities[0].ArchivePath, i.ArchivePath); Assert.Same(identities[0].ResourceName, i.ResourceName);
        });
        Assert.NotEqual(identities[0], identities[1]);
        Assert.Same(result.Actors[0].PlacementSource, result.Actors[2].PlacementSource);
    }

    [Fact]
    public void PickupRowsShareCompleteUppercaseOnlyIdentityWithoutCanonicalizingThePath()
    {
        var world = PickupWorld();
        string archive = "mixed/../" + new string('p', 80) + ".zbd", resource = "puppies_" + new string('q', 80) + ".zrd";
        var result = MissionSceneLoader.BuildWithBudget(world, new MissionPlacementBudget(Token), null, null, null, null,
            token: Token, pickups: PickupRows(3), pickupSource: new(archive, 4, resource));
        var identities = result.Actors.Select(a => Assert.IsType<MissionPickup>(a.Pickup).Source).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, identities.Select(i => i.RecordIndex));
        Assert.All(identities, i =>
        {
            Assert.Equal(archive.ToUpperInvariant(), i.ArchivePath); Assert.Equal(resource.ToUpperInvariant(), i.ResourceName);
            Assert.Same(identities[0].ArchivePath, i.ArchivePath); Assert.Same(identities[0].ResourceName, i.ResourceName);
        });
        Assert.Contains("..", identities[0].ArchivePath); Assert.NotEqual(identities[0], identities[1]);
    }

    [Fact]
    public void OriginalGameZPickupsShareTheFullWorldIdentity()
    {
        var world = World(Node(0, "pu00100", children: [1]), Node(1, "bvol", parents: [0]),
            Node(2, "pu00101", children: [3]), Node(3, "bvol", parents: [2]), Node(4, "world", "world", children: [0, 2]));
        var result = Build(world, new(Token), null);
        Assert.Equal(2, result.Actors.Count);
        var first = result.Actors[0].Pickup!.Source; var second = result.Actors[1].Pickup!.Source;
        Assert.Equal(world.Path.ToUpperInvariant(), first.ArchivePath); Assert.Same(first.ArchivePath, second.ArchivePath);
        Assert.Equal("GAMEZ", first.ResourceName); Assert.Equal(0, first.RecordIndex); Assert.Equal(2, second.RecordIndex);
    }

    [Fact]
    public void IdentityAdmissionPrecedesNormalizationAndUnusedInvalidSourcesStayUnused()
    {
        var world = World(Node(0, "tank"), Node(1, "world", "world")); string[] before = Snapshot(world);
        var control = new MissionPlacementBudget(Token); _ = Build(world, control, Actors(1));
        var invalid = new MissionResourceSource(new string('x', 256) + "\0", 1, "aiv.zrd");
        var limited = new MissionPlacementBudget(Token, maximumWork: control.Work + 32);
        var error = Assert.Throws<InvalidDataException>(() => MissionSceneLoader.BuildWithBudget(world, limited, null,
            Actors(1), Vehicles("tank"), null, token: Token, aivSource: invalid));
        Assert.Contains("construction work or metadata budget", error.Message); Assert.True(limited.Exhausted);
        // If normalization ran before the reservation this would throw the invalid-path ArgumentException instead.
        var unused = MissionSceneLoader.BuildWithBudget(world, new MissionPlacementBudget(Token), null,
            null, null, null, token: Token, aivSource: invalid);
        Assert.Empty(unused.Actors);
        var retry = MissionSceneLoader.BuildWithBudget(world, new MissionPlacementBudget(Token), null,
            Actors(1), Vehicles("tank"), null, token: Token, aivSource: new("valid.zbd", 1, "aiv.zrd"));
        Assert.Equal(Path.GetFullPath("valid.zbd").ToUpperInvariant(), Assert.Single(retry.Actors).CoordinateSource!.ArchivePath);
        Assert.Equal(before, Snapshot(world));
    }

    [Fact]
    public void RepeatedMetadataIsAdmittedBeforeCloneIncludingColdJsonAndCustomValues()
    {
        var world = World(Node(0, "tank"), Node(1, "world", "world"));
        world.Scene!.Nodes[0].Metadata["extra"] = JsonNode.Parse("{\"values\":[1,2,3],\"text\":\"\\u0061\\u0062\"}");
        var baseline = new MissionPlacementBudget(Token); _ = Build(world, baseline, null);
        var one = new MissionPlacementBudget(Token); var accepted = Build(world, one, Actors(1));
        Assert.True(one.CloneBytes > baseline.CloneBytes);
        var limited = new MissionPlacementBudget(Token, maximumCloneBytes: one.CloneBytes);
        Assert.Throws<InvalidDataException>(() => Build(world, limited, Actors(2)));
        Assert.True(limited.Exhausted);
        Assert.True(JsonNode.DeepEquals(world.Scene.Nodes[0].Metadata["extra"], accepted.Scene.Nodes[2].Metadata["extra"]));
        var custom = new CustomMetadata(); world.Scene.Nodes[0].Metadata["custom"] = JsonValue.Create(custom);
        Assert.Throws<InvalidDataException>(() => Build(world, new(Token), null));
        Assert.Equal(0, custom.Reads);
    }

    [Fact]
    public void ColdMetadataRefusalDoesNotHydrateTheOriginalContainer()
    {
        var world = World(Node(0, "tank"), Node(1, "world", "world"));
        var cold = JsonNode.Parse("{\"\\u006eame\":[\"\\u0061\",\"b\",\"c\"]}")!.AsObject();
        world.Scene!.Nodes[0] = world.Scene.Nodes[0] with { Metadata = cold };
        var underlying = JsonData.UnderlyingElement; Assert.NotNull(underlying);
        Assert.True(underlying(cold).HasValue);
        var budget = new MissionPlacementBudget(Token, maximumCloneBytes: 900);
        Assert.Throws<InvalidDataException>(() => Build(world, budget, null));
        Assert.True(budget.Exhausted);
        Assert.True(underlying(cold).HasValue);
        Assert.Equal(2, world.Scene.Nodes.Count);
    }

    [Fact]
    public void CancellationAtAReservationBoundaryKeepsTheSourceAndTokenIdentity()
    {
        var world = World(Node(0, "tank"), Node(1, "world", "world")); string[] before = Snapshot(world);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int reservations = 0;
        var budget = new MissionPlacementBudget(canceled.Token, reserved: () => { if (++reservations == 12) canceled.Cancel(); });
        var error = Assert.Throws<OperationCanceledException>(() => MissionSceneLoader.BuildWithBudget(world, budget, null, Actors(2), Vehicles("tank"), null, token: canceled.Token));
        Assert.Equal(canceled.Token, error.CancellationToken); Assert.Equal(before, Snapshot(world));
        Assert.Equal(2, Build(world, new(Token), Actors(2)).Actors.Count);
    }

    private sealed class CustomMetadata
    {
        public int Reads { get; private set; }
        public string Value { get { Reads++; return "not serialized during admission"; } }
    }
    private static MissionSceneContext Build(ZbdDocument world, MissionPlacementBudget budget, JsonNode? actors, JsonNode? vehicles = null)
        => MissionSceneLoader.BuildWithBudget(world, budget, null, actors, vehicles ?? Vehicles("tank"), null, token: Token);
    private static ZbdDocument World(params GameNode[] nodes)
    {
        GameScene scene = new(); scene.Nodes.AddRange(nodes);
        return new(Path.GetFullPath("placement-work.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "test"), Array.Empty<byte>()) { Scene = scene };
    }
    private static GameNode Node(int index, string name, string kind = "object3d", int[]? parents = null, int[]? children = null)
        => new(index, name, kind, null, parents ?? [], children ?? [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 });
    private static ZbdDocument PickupWorld() => World(Node(0, "tank"), Node(1, "pu001", children: [2]), Node(2, "bvol", parents: [1]), Node(3, "world", "world", children: [0, 0]));
    private static string[] Snapshot(ZbdDocument world) => world.Scene!.Nodes.Select(n => $"{n.Index}:{n.Name}:{string.Join(',', n.Parents)}:{string.Join(',', n.Children)}:{n.Data.ToJsonString()}:{n.Metadata.ToJsonString()}").ToArray();
    private static JsonObject Arr(params JsonNode[] nodes) => new() { ["type"] = "array", ["children"] = new JsonArray(nodes) };
    private static JsonObject Str(string value) => new() { ["type"] = "string", ["value"] = value };
    private static JsonObject Num(float value) => new() { ["type"] = "float", ["value"] = value };
    private static JsonObject Spawn(float x) => Arr(Num(0), Arr(Num(x), Num(2), Num(3)), Num(0));
    private static JsonObject Vehicles(params string[] names) => Arr(names.SelectMany(n => new JsonNode[] { Str(n), Arr() }).ToArray());
    private static JsonObject Actors(int count) => Arr(Enumerable.Range(1, count).SelectMany(i => new JsonNode[] { Str($"tank_{i:D2}"), Spawn(i) }).ToArray());
    private static JsonObject PickupRows(int count) => Arr(Arr(Enumerable.Range(0, count).Select(_ => (JsonNode)Arr(Str("HEMORTAR_AMMO"),
        new JsonObject { ["type"] = "int", ["value"] = 0 }, Arr(Num(4), Num(5), Num(6)), Arr(Num(0), Num(0), Num(0)), Num(12))).ToArray()));
}

public sealed partial class AnimationTests
{
    [Fact]
    public void MissionStartupSeesPublishedPlacementEdgesBeforeResolvingDuplicateNames()
    {
        var context = MissionFixture(false);
        var template = context.Scene.Nodes[0];
        context.Scene.Nodes[0] = template with { Children = [2] };
        context.Scene.Nodes.Add(template with { Index = 2, Name = "target", Parents = [0], Children = [],
            Data = (JsonObject)template.Data.DeepClone(), Metadata = (JsonObject)template.Metadata.DeepClone() });
        context.Scene.Nodes.Add(template with { Index = 3, Name = "target", Parents = [], Children = [],
            Data = (JsonObject)template.Data.DeepClone(), Metadata = (JsonObject)template.Metadata.DeepClone() });
        var startup = MissionEntry(1, "startup", "world");
        startup.References[1][1].SetText(0, "target", 36);
        var position = AnimationCatalog.Create(7); position.SetShort(28, 1); position.SetVector(16, new(77, 0, 0));
        startup.Sequences[0].Events.Add(position); context.Package.Entries.Add(startup);
        byte[] original = Pack(context.Package);
        var mission = MissionSceneLoader.Build(context.World, context.Package,
            Arr(Str("animated"), Spawn(1, 2, 3, 0)), Arr(Str("animated"), Arr()),
            Arr(Str("NEW_GAME_START"), Str("startup")), token: TestContext.Current.CancellationToken);
        // Subtree lookup must find the lower slot now attached through the placed original root. Without the
        // publication before startup, world-wide fallback would move the higher, unattached same-name node.
        Assert.Equal(77, SceneBuilder.LocalTransform(mission.Scene.Nodes[2]).M41);
        Assert.Equal(0, SceneBuilder.LocalTransform(mission.Scene.Nodes[3]).M41);
        Assert.Equal(new[] { 0 }, mission.Scene.Nodes[1].Children);
        Assert.Empty(context.Scene.Nodes[1].Children); Assert.Equal(original, Pack(context.Package));
    }
}
