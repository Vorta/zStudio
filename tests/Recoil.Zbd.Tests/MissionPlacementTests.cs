using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void EveryTurretGetsItsOwnCleanupWithoutChangingStoredDataOrOtherObjects()
    {
        var context = TurretFixture(); string[] before = NodeState(context.Scene); byte[] animation = Pack(context.Package);
        context.Mission = BuildTurrets(context);
        foreach (int root in new[] { 2, 7 })
        {
            Assert.True(Active(context.Scene, root + 1)); Assert.False(Active(context.Scene, root + 2));
            Assert.False(Active(context.Scene, root + 3)); Assert.False(Active(context.Scene, root + 4));
        }
        foreach (int root in new[] { 12, 17 }) Assert.True(Active(context.Scene, root + 2)); // Wrong digit/length.
        Assert.Equal(animation, Pack(context.Package)); Assert.Equal(before, NodeState(context.World.Scene!));
        context.RootOverrides[1] = 7;
        var player = new AnimationPlayer(context, 1);
        var frame = player.EvaluateForTest(.1);
        Assert.True(frame.Nodes.Single(n => n.SourceNode == 9).Visible);
        Assert.False(frame.Nodes.Single(n => n.SourceNode == 8).Visible);
        Assert.False(Active(context.Scene, 4)); // Playback and another turret remain independent.
    }

    [Theory]
    [InlineData("override", 300, 300)]
    [InlineData("missing", 200, 100)]
    [InlineData("", 200, 100)]
    public void TurretResetUsesResolvedExplicitThenNamedThenDefault(string explicitName, int first, int second)
    {
        var context = TurretFixture();
        AddPosition(context.Package.Entries[1], 100);
        var named = ResetEntry(2, "tur_01"); AddPosition(named, 200); context.Package.Entries.Add(named);
        var explicitEntry = ResetEntry(3, "override"); AddPosition(explicitEntry, 300); context.Package.Entries.Add(explicitEntry);
        var mission = BuildTurrets(context, explicitName);
        Assert.Equal(first, SceneBuilder.LocalTransform(mission.Scene.Nodes[2]).M41);
        Assert.Equal(second, SceneBuilder.LocalTransform(mission.Scene.Nodes[7]).M41);
        static void AddPosition(AnimationEntry entry, int x)
        { var ev = AnimationCatalog.Create(7); ev.SetShort(28, -100); ev.SetVector(16, new(x, 0, 0)); entry.Primary.Events.Add(ev); }
    }

    [Fact]
    public void MissingTurretComponentCannotModifyAnotherInstancesComponent()
    {
        var context = TurretFixture();
        context.Scene.Nodes[4] = context.Scene.Nodes[4] with { Name = "other" };
        var mission = BuildTurrets(context);
        Assert.True(Active(mission.Scene, 4)); Assert.False(Active(mission.Scene, 9));
        Assert.Contains(mission.Diagnostics, d => d.Contains("unresolved node reference", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedTurretDefinitionsAreDiagnosedWithoutLosingTheScene()
    {
        var context = TurretFixture();
        var mission = MissionSceneLoader.Build(context.World, context.Package, null, null, null,
            token: TestContext.Current.CancellationToken, ai: Arr(Str("TURRET"), Arr(Str("tur_**"))));
        Assert.Equal(context.Scene.Nodes.Count, mission.Scene.Nodes.Count);
        Assert.True(Active(mission.Scene, 4)); Assert.Contains(mission.Diagnostics, d => d.Contains("ai.zrd initialization is incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public void PickupInstancesRetainPoseScaleAndMetadataAndHideCollisionGeometry()
    {
        var context = PickupFixture(); context.Scene.Nodes[0].Data["scale"] = JsonData.Vector(new(2, 3, 4));
        string[] original = NodeState(context.Scene);
        var mission = BuildPickups(context, PickupList(PickupRow(amount: 0, yaw: MathF.PI / 2), PickupRow(amount: 7)));
        Assert.Equal(2, mission.Actors.Count); Assert.NotEqual(mission.Actors[0].Root, mission.Actors[1].Root);
        var first = mission.Actors[0]; var pickup = Assert.IsType<MissionPickup>(first.Pickup);
        Assert.Equal(3, pickup.EffectiveAmount); Assert.Equal(0, pickup.AuthoredAmount); Assert.Equal(12.5f, pickup.RespawnDelay);
        Assert.Equal(0, pickup.Source.RecordIndex); Assert.Equal(1, mission.Actors[1].Pickup!.Source.RecordIndex);
        var pose = SceneBuilder.LocalTransform(mission.Scene.Nodes[first.Root]);
        Assert.Equal(new(12, 8, -5), pose.Translation);
        Assert.InRange(Vector3.TransformNormal(-Vector3.UnitZ, pose).X, -4.001f, -3.999f);
        Assert.True(Matrix4x4.Decompose(pose, out var scale, out _, out _)); Assert.InRange(Vector3.Distance(new(2, 3, 4), scale), 0, .00001f);
        Assert.All(mission.Actors, a => Assert.False(Active(mission.Scene, mission.Scene.Nodes[a.Root].Children.Single())));
        Assert.Equal(2, SceneBuilder.Assemble(mission.Scene, token: TestContext.Current.CancellationToken).Placements.Count);
        Assert.Equal(original, NodeState(context.Scene)); Assert.Equal(3, context.Scene.Nodes.Count);
    }

    [Fact]
    public void PickupCatalogIsEngineOrderedAndWeaponPickupsAreIncluded()
    {
        Assert.Equal(40, MissionPickupType.Catalog.Count); Assert.Equal("pu036", MissionPickupType.Catalog[36].TemplateName);
        var context = PickupFixture("pu017"); var result = BuildPickups(context, PickupList(PickupRow("ERFPG_WEAPON", 0)));
        Assert.Equal("ERFPG_WEAPON", Assert.Single(result.Actors).Pickup!.LogicalName);
        Assert.Equal(30, result.Actors[0].Pickup!.EffectiveAmount);
    }

    [Fact]
    public void AlreadyPlacedPickupRetainsItsHierarchyAndDoesNotGetClonedAgain()
    {
        var context = PickupFixture("pu00107"); context.Scene.Nodes[0] = context.Scene.Nodes[0] with { Parents = [1] };
        context.Scene.Nodes[1] = context.Scene.Nodes[1] with { Children = [0] };
        var mission = BuildPickups(context, PickupList());
        Assert.Equal(3, mission.Scene.Nodes.Count); Assert.Equal(0, Assert.Single(mission.Actors).Root);
        Assert.False(Active(mission.Scene, 2)); Assert.Equal("GAMEZ", mission.Actors[0].Pickup!.Source.ResourceName);
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("unknown")]
    [InlineData("nonfinite")]
    [InlineData("amount")]
    public void InvalidPickupRecordIsDiagnosedWhileValidSiblingIsRetained(string kind)
    {
        var invalid = PickupRow();
        switch (kind)
        {
            case "shape": invalid["children"]!.AsArray().RemoveAt(4); break;
            case "unknown": invalid["children"]![0]!["value"] = "UNKNOWN"; break;
            case "nonfinite": invalid["children"]![2]!["children"]![0]!["value"] = "Infinity"; break;
            case "amount": invalid["children"]![1]!["type"] = "float"; break;
        }
        var mission = BuildPickups(PickupFixture(), PickupList(invalid, PickupRow()));
        Assert.Equal(1, Assert.Single(mission.Actors).Pickup!.Source.RecordIndex);
        Assert.Contains(mission.Diagnostics, d => d.Contains("puppies.zrd #0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("template")]
    [InlineData("bvol")]
    [InlineData("cycle")]
    public void InvalidPickupTemplateDoesNotLeavePartialClones(string kind)
    {
        var context = PickupFixture();
        if (kind == "template") context.Scene.Nodes[0] = context.Scene.Nodes[0] with { Name = "other" };
        if (kind == "bvol") context.Scene.Nodes[2] = context.Scene.Nodes[2] with { Name = "other" };
        if (kind == "cycle") context.Scene.Nodes[2] = context.Scene.Nodes[2] with { Children = [0] };
        var mission = BuildPickups(context, PickupList(PickupRow()));
        Assert.Empty(mission.Actors); Assert.Equal(3, mission.Scene.Nodes.Count); Assert.Equal(3, mission.SourceNodes.Count);
        Assert.Contains(mission.Diagnostics, d => d.Contains("Mission pickup puppies.zrd #0", StringComparison.Ordinal));
    }

    [Fact]
    public void PickupRemappingUsesResourceRecordIdentityInsteadOfGeneratedNameOrCloneIndex()
    {
        var context = PickupFixture(); var records = PickupList(PickupRow(), PickupRow());
        var first = BuildPickups(context, records); var same = BuildPickups(context, records);
        var easy = BuildPickups(context, records, MissionDifficulty.Easy);
        Assert.Equal(same.Actors[0].Root, same.RemapNodeFrom(first, first.Actors[0].Root));
        Assert.Equal(-1, easy.RemapNodeFrom(first, first.Actors[0].Root));
        Assert.NotEqual(first.Actors[0].Pickup!.Source, first.Actors[1].Pickup!.Source);
    }

    [Fact]
    public async Task PickupResourcesFollowDifficultyWithMissingOnlyFallbackAndNoMerge()
    {
        await WithMissionArchiveAsync(new() {
            ["puppies.zrd"] = Zrd(PickupList(PickupRow(), PickupRow())), ["puppies_easy.zrd"] = Zrd(PickupList())
        }, async (world, resolver) => {
            PreparePickup(world.Scene!, "pu001");
            var medium = await Load(MissionDifficulty.Medium); var easy = await Load(MissionDifficulty.Easy); var hard = await Load(MissionDifficulty.Hard);
            Assert.Equal(2, medium.Actors.Count); Assert.Empty(easy.Actors); Assert.Equal(2, hard.Actors.Count);
            Assert.Equal("puppies_easy.zrd", easy.Layout.PickupResource); Assert.Equal("puppies.zrd", hard.Layout.PickupResource);
            Assert.Equal(hard.Actors[0].Root, hard.RemapNodeFrom(medium, medium.Actors[0].Root));
            Assert.Same(hard, await Load(MissionDifficulty.Hard));
            Task<MissionSceneContext> Load(MissionDifficulty difficulty) => MissionSceneLoader.LoadAsync(world, resolver, token: TestContext.Current.CancellationToken, difficulty: difficulty);
        });
    }

    [Fact]
    public async Task MalformedSelectedPickupListDoesNotFallBack()
    {
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList()), ["puppies_easy.zrd"] = Zrd(Arr(Str("invalid"))) }, async (world, resolver) => {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => MissionSceneLoader.LoadAsync(world, resolver, token: TestContext.Current.CancellationToken, difficulty: MissionDifficulty.Easy));
            Assert.Contains("puppies_easy.zrd", error.Message); Assert.Contains("pickup placement list", error.Message);
        });
    }

    private static AnimationPreviewContext TurretFixture()
    {
        var context = MissionFixture(false); context.Package.Entries[0].SetText(0, ""); context.Package.Entries.Add(ResetEntry(1, "destroy"));
        foreach (string name in new[] { "tur_01", "tur_02", "tur_A3", "tur_003" })
        {
            int root = context.Scene.Nodes.Count;
            Add(root, name, [1], [root + 1, root + 2, root + 3, root + 4], null);
            Add(root + 1, "healthy", [root], [], 0); Add(root + 2, "destroyed", [root], [], 0);
            Add(root + 3, "firepoint", [root], [], 0); Add(root + 4, "lightning", [root], [], 0);
            context.Scene.Nodes[1] = context.Scene.Nodes[1] with { Children = [.. context.Scene.Nodes[1].Children, root] };
        }
        return context;
        void Add(int id, string name, int[] parents, int[] children, int? model)
        { var source = context.Scene.Nodes[0]; context.Scene.Nodes.Add(source with { Index = id, Name = name, Parents = parents, Children = children, ModelIndex = model, Data = (JsonObject)source.Data.DeepClone(), Metadata = (JsonObject)source.Metadata.DeepClone() }); }
    }
    private static AnimationEntry ResetEntry(int index, string name)
    {
        var entry = MissionEntry(index, name, "tur_01"); entry.References[1][1].SetText(0, "healthy", 36);
        var reference = new AnimationRecord(new byte[40]); reference.SetText(0, "destroyed", 36); entry.References[1].Add(reference);
        entry.Primary.Events.Add(Activate(1, true)); entry.Primary.Events.Add(Activate(2, false));
        entry.Sequences[0].Events.Add(Activate(1, false)); entry.Sequences[0].Events.Add(Activate(2, true)); return entry;
    }
    private static AnimationEvent Activate(short reference, bool active)
    { var ev = AnimationCatalog.Create(6); ev.SetInt(12, active ? 1 : 0); ev.SetShort(16, reference); return ev; }
    private static MissionSceneContext BuildTurrets(AnimationPreviewContext context, string explicitName = "")
    {
        var definition = Arr(Str("DESTROY_ANIM"), Arr(Str(explicitName)), Str("PARTS"), Arr(Str("healthy"), Str("firepoint")), Str("EFFECT"), Arr(Str("lightning"), Num(.01f)));
        return MissionSceneLoader.Build(context.World, context.Package, null, null, null, token: TestContext.Current.CancellationToken,
            ai: Arr(Str("DESTROY_ANIM"), Arr(Str("destroy")), Str("TURRET"), Arr(Str("tur_**"), definition)));
    }
    private static bool Active(GameScene scene, int index) => (scene.Nodes[index].Metadata.UInt("flags") & 4) != 0;
    private static string[] NodeState(GameScene scene) => scene.Nodes.Select(n => n.Data.ToJsonString() + n.Metadata.ToJsonString()).ToArray();
    private static AnimationPreviewContext PickupFixture(string name = "pu001")
    { var context = MissionFixture(false); PreparePickup(context.Scene, name); return context; }
    private static void PreparePickup(GameScene scene, string name)
    {
        scene.Nodes[0] = scene.Nodes[0] with { Name = name, Children = [2] };
        var source = scene.Nodes[0]; scene.Nodes.Add(source with { Index = 2, Name = "bvol", Parents = [0], Children = [], Data = (JsonObject)source.Data.DeepClone(), Metadata = (JsonObject)source.Metadata.DeepClone() });
    }
    private static JsonObject PickupRow(string type = "HEMORTAR_AMMO", int amount = 1, float yaw = 0) =>
        Arr(Str(type), new JsonObject { ["type"] = "int", ["value"] = amount }, Arr(Num(12), Num(8), Num(-5)), Arr(Num(0), Num(yaw), Num(0)), Num(12.5f));
    private static JsonObject PickupList(params JsonNode[] rows) => Arr(Arr(rows));
    private static MissionSceneContext BuildPickups(AnimationPreviewContext context, JsonNode records, MissionDifficulty difficulty = MissionDifficulty.Medium) =>
        MissionSceneLoader.Build(context.World, null, null, null, null, token: TestContext.Current.CancellationToken, selection: MissionLayoutSelection.For(difficulty), pickups: records);
}
