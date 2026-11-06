using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

/// <summary>Must-have source hierarchy workflow: persistent empty groups and real nested model placements.</summary>
public sealed class SourceGroupCreationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedDatabaseAndLiveCopiesCannotReceiveChildren(bool model)
    {
        using SourceWorldFixture fixture = new();
        string fresh = File.ReadAllText(fixture.Path("data/m1/models/m1.gltf")).Replace("\"ground\"", "\"fresh_surface\"", StringComparison.Ordinal);
        fixture.WritePartDatabase();
        const string partFile = "data/m1/models/m1_01.gltf";
        var part = JsonNode.Parse(File.ReadAllText(fixture.Path(partFile)))!;
        int shared = part["nodes"]!.AsArray().Count;
        part["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "Shared", ["children"] = part["scenes"]![0]!["nodes"]!.DeepClone(), ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["group"] = true } } });
        part["scenes"]![0]!["nodes"] = new JsonArray(shared); fixture.Write(partFile, part.ToJsonString());
        string script = File.ReadAllText(fixture.Path("gamegen/m1.gs"));
        fixture.Write("gamegen/m1.gs", script.Replace("GameZWriteZBDFile", "SetModelDirectory ..\\data\\m1\\models\nLoadGameGen m1_01.gltf decor\nObject3DRotate 0.0 90.0 0.0\nObject3DTranslate 100.0 0.0 0.0\nFindNode %worldName%\nAddChild decor\nGameZWriteZBDFile", StringComparison.Ordinal));
        fixture.Write("data/m1/models/fresh.gltf", fresh);
        SourceWorkspace workspace = new(fixture.Project);
        var before = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: Token);
        Assert.Contains("live model copy", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.CreationScope(workspace, before.BuildState, new(partFile, shared, "test"), Token)).Message);
        var candidate = workspace.BeginPreparedEdit().Workspace; var original = candidate.Overlay();
        // Final validation also protects callers whose target scope was captured before another affected map gained a live load.
        var scope = new SourceCreationScope(partFile, shared, "Shared", 2, ["m1"]);
        SourceModelAddition? addition = model ? new("data/m1/models/fresh.gltf", "fresh_new", new Vector3(32, 0, 0)) : null;
        var plan = SourceObjectEdits.PlanCreate(candidate, before.World, before.BuildState, scope, "fresh_new", addition, Token);
        candidate.Apply(plan.Edit.Label, plan.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", candidate.Overlay(), Token);
        Assert.Contains("live model copy", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.ValidateCreation(candidate, original, plan, built, addition, Token)).Message);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task EarlyObjectCannotReplaceAnExistingTextureIdentity()
    {
        using SourceWorldFixture fixture = new();
        string masterFile = "data/m1/models/m1.gltf";
        string masterText = File.ReadAllText(fixture.Path(masterFile));
        fixture.Write("data/m1/textures/existing/rock.png", File.ReadAllBytes(fixture.Path("data/m1/textures/rock.png")));
        File.Delete(fixture.Path("data/m1/textures/rock.png"));
        fixture.Write("data/m1/textures/new/rock.png", File.ReadAllBytes(fixture.Path("data/m2/textures/bft/camo.png")));
        fixture.Write("data/m1/models/fresh.gltf", masterText.Replace("../textures/rock.png", "../textures/new/rock.png", StringComparison.Ordinal).Replace("\"ground\"", "\"fresh_surface\"", StringComparison.Ordinal));
        var master = JsonNode.Parse(masterText.Replace("../textures/rock.png", "../textures/existing/rock.png", StringComparison.Ordinal))!;
        int early = master["nodes"]!.AsArray().Count;
        master["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "Early", ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["group"] = true } } });
        master["scenes"]![0]!["nodes"]!.AsArray().Insert(0, early);
        fixture.Write(masterFile, master.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var before = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: Token);
        var candidate = workspace.BeginPreparedEdit().Workspace; var original = candidate.Overlay();
        var scope = SourceObjectEdits.CreationScope(candidate, before.BuildState, new(masterFile, early, "test"), Token);
        var model = new SourceModelAddition("data/m1/models/fresh.gltf", "fresh_new", Vector3.Zero);
        var plan = SourceObjectEdits.PlanCreate(candidate, before.World, before.BuildState, scope, model.Name, model, Token);
        candidate.Apply(plan.Edit.Label, plan.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", candidate.Overlay(), Token);
        Assert.NotEqual(before.Textures["rock"].Path, built.Textures["rock"].Path);
        Assert.NotEqual(before.Textures["rock"].Hash, built.Textures["rock"].Hash);
        Assert.Contains("existing texture rock", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.ValidateCreation(candidate, original, plan, built, model, Token)).Message);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task NewDescendantCannotTakeAnExistingVehiclePlacement()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m2/zrdr/aiv.zrd", "( tank_01 ( 0 ( 10.0 20.0 30.0 ) 90.0 ) )");
        fixture.Write("data/m2/zrdr/vehicle.zrd", "( tank ( ) )");
        fixture.Write("data/m2/models/bft/fresh.gltf", File.ReadAllText(fixture.Path(fixture.Tank)).Replace("\"hull\"", "\"tank_01\"", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var before = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", token: Token);
        var candidate = workspace.BeginPreparedEdit().Workspace; var original = candidate.Overlay();
        var scope = SourceObjectEdits.CreationScope(candidate, before.BuildState, null, Token);
        var model = new SourceModelAddition("data/m2/models/bft/fresh.gltf", "fresh_new", Vector3.Zero);
        var plan = SourceObjectEdits.PlanCreate(candidate, before.World, before.BuildState, scope, model.Name, model, Token);
        candidate.Apply(plan.Edit.Label, plan.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", candidate.Overlay(), Token);
        Assert.NotEqual(Assert.Single(before.MissionScene(MissionDifficulty.Medium, Token).Actors).SourceRoot, Assert.Single(built.MissionScene(MissionDifficulty.Medium, Token).Actors).SourceRoot);
        Assert.Contains("vehicle or pickup placement", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.ValidateCreation(candidate, original, plan, built, model, Token)).Message);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task EarlyGroupCannotRetargetAWorldBoundAnimationReference()
    {
        using SourceWorldFixture fixture = new();
        var master = JsonNode.Parse(File.ReadAllText(fixture.Path("data/m1/models/m1.gltf")))!;
        fixture.Write("data/m1/models/fresh.gltf", master.ToJsonString());
        int early = master["nodes"]!.AsArray().Count;
        master["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "Early", ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["group"] = true } } });
        master["scenes"]![0]!["nodes"]!.AsArray().Insert(0, early);
        fixture.Write("data/m1/models/m1.gltf", master.ToJsonString());
        fixture.Write("data/m1/zrdr/gates.zad", "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( world ) ANIMATION_NAME ( sink ) ACTIVATION ( ON_CALL ) SEQUENCE_DEFINITION ( NAME ( off ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( INACTIVE ) ) ) ) ) ) )");
        SourceWorkspace workspace = new(fixture.Project);
        var before = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: Token);
        var candidate = workspace.BeginPreparedEdit().Workspace; var original = candidate.Overlay();
        var scope = SourceObjectEdits.CreationScope(candidate, before.BuildState, new("data/m1/models/m1.gltf", early, "test"), Token);
        var model = new SourceModelAddition("data/m1/models/fresh.gltf", "fresh_new", Vector3.Zero);
        var plan = SourceObjectEdits.PlanCreate(candidate, before.World, before.BuildState, scope, model.Name, model, Token);
        candidate.Apply(plan.Edit.Label, plan.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", candidate.Overlay(), Token);
        Assert.Contains("binding of animation sink", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.ValidateCreation(candidate, original, plan, built, model, Token)).Message);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task NewDescendantCannotRetargetAnExistingAnimation()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/models/fresh.gltf", File.ReadAllBytes(fixture.Path("data/m1/models/m1.gltf")));
        SourceWorkspace workspace = new(fixture.Project);
        var before = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: Token);
        var candidate = workspace.BeginPreparedEdit().Workspace;
        var original = candidate.Overlay();
        var scope = SourceObjectEdits.CreationScope(candidate, before.BuildState, null, Token);
        var model = new SourceModelAddition("data/m1/models/fresh.gltf", "fresh_new", Vector3.Zero);
        var plan = SourceObjectEdits.PlanCreate(candidate, before.World, before.BuildState, scope, model.Name, model, Token);
        candidate.Apply(plan.Edit.Label, plan.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", candidate.Overlay(), Token);
        var was = before.Lookups.Single(l => l.Kind == SourceLookup.AnimationRoot && l.Source == "sink");
        var now = built.Lookups.Single(l => l.Kind == SourceLookup.AnimationRoot && l.Source == "sink");
        Assert.NotEqual(was.Found, now.Found); Assert.Equal(2, now.Candidates);
        Assert.Contains("animation sink", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.ValidateCreation(candidate, original, plan, built, model, Token)).Message);
        Assert.False(workspace.IsDirty); Assert.Equal(0, workspace.UndoCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupOwnedNewObjectCannotBePlacedAtMisleadingCoordinates(bool childDependsOnRoot)
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m2/zrdr/anim.zad", $$"""
            ( ANIMATION_DEFINITIONS ( ANIMATION_LIST (
              ANIMATION_DEFINITION ( NAME ( "lamp*" ) ANIMATION_NAME ( "move_lamp*" ) ACTIVATION ( ON_STARTUP )
                SEQUENCE_DEFINITION ( NAME ( place ) OBJECT_TRANSLATE_STATE ( NAME ( {{(childDependsOnRoot ? "hull" : "INPUT_NODE")}} ) {{(childDependsOnRoot ? "AT_NODE ( INPUT_NODE 10 0 0 )" : "STATE ( 10 0 0 )")}} ) ) )
            ) ) )
            """);
        SourceWorkspace workspace = new(fixture.Project);
        var before = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", token: Token);
        var candidate = workspace.BeginPreparedEdit().Workspace;
        var original = candidate.Overlay();
        var scope = SourceObjectEdits.CreationScope(candidate, before.BuildState, null, Token);
        var model = new SourceModelAddition(fixture.Tank, "lamp1", new Vector3(32, 0, 0));
        var plan = SourceObjectEdits.PlanCreate(candidate, before.World, before.BuildState, scope, model.Name, model, Token);
        candidate.Apply(plan.Edit.Label, plan.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", candidate.Overlay(), Token);
        int root = Assert.Single(SourceObjectEdits.CreatedSlots(plan, built));
        Assert.Equal(32, SceneBuilder.LocalTransform(built.Scene!.Nodes[root]).M41);
        var initialized = built.MissionScene(MissionDifficulty.Medium, Token);
        Assert.Equal(childDependsOnRoot ? 32 : 10, SceneBuilder.LocalTransform(initialized.Scene.Nodes[root]).M41);
        if (childDependsOnRoot) Assert.Equal(42, SceneBuilder.LocalTransform(initialized.Scene.Nodes.Single(n => n.Name == "hull" && n.Index > root)).M41);
        Assert.Contains("mission initialization", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.ValidateCreation(candidate, original, plan, built, model, Token)).Message);
        Assert.False(workspace.IsDirty); Assert.Equal(0, workspace.UndoCount);
    }

    [Fact]
    public async Task EmptyWorldGroupsNestedObjectsSaveAndExportWithoutScriptEdits()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m2/models/m2.gltf", """{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[],"extras":{"zstudio":{"sourceFormat":4}}}],"nodes":[]}""");
        byte[] script = File.ReadAllBytes(fixture.Path("gamegen/m2.gs"));
        SourceWorkspace workspace = new(fixture.Project);
        var built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", token: Token);
        Assert.Empty(built.BuildState.AuthoredHierarchy.Nodes);
        var scope = SourceObjectEdits.CreationScope(workspace, built.BuildState, null, Token);
        Assert.Equal("data/m2/models/m2.gltf", scope.File);
        async Task<SourceCreationPlan> Create(SourceCreationScope into, string name, SourceModelAddition? model = null)
        {
            var before = workspace.Overlay();
            var plan = SourceObjectEdits.PlanCreate(workspace, built.World, built.BuildState, into, name, model, Token);
            workspace.Apply(plan.Edit.Label, plan.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
            built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", workspace.Overlay(), Token);
            SourceObjectEdits.ValidateCreation(workspace, before, plan, built, model, Token);
            return plan;
        }
        var group = await Create(scope, "Buildings");
        Assert.Contains(built.BuildState.AuthoredHierarchy.Nodes, n => n.Name == "Buildings" && n.Group && n.CompiledNode == null);
        scope = SourceObjectEdits.CreationScope(workspace, built.BuildState, new(group.Parent.File, group.Nodes[0], "test"), Token);
        var nested = await Create(scope, "Interior");
        scope = SourceObjectEdits.CreationScope(workspace, built.BuildState, new(scope.File, nested.Nodes[0], "test"), Token);
        var objectPlan = await Create(scope, "tank_new", new(fixture.Tank, "tank_new", new Vector3(12, 0, -20)));
        Assert.Single(SourceObjectEdits.CreatedSlots(objectPlan, built));
        var json = JsonNode.Parse(workspace.Read(scope.File, Token)!)!;
        Assert.True(json["nodes"]![objectPlan.Nodes[0]]!["extras"]![WorldGltf.Key]!["altitudeZoneCheck"]!.GetValue<bool>());
        Assert.Contains(objectPlan.Nodes[0], json["nodes"]![nested.Nodes[0]]!["children"]!.AsArray().Select(v => v!.GetValue<int>()));
        Assert.Equal(script, workspace.Read("gamegen/m2.gs", Token));
        workspace.Undo(); Assert.DoesNotContain("tank_new", System.Text.Encoding.UTF8.GetString(workspace.Read(scope.File, Token)!)); workspace.Redo();
        workspace.Save(Token);
        Assert.Contains((await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", token: Token)).BuildState.AuthoredHierarchy.Nodes, n => n.Name == "Interior" && n.Group);
        var exported = await SourceBuilder.ExportAsync(fixture.Project, Path.Combine(fixture.Root, "group-game"), ["m2/gamez.zbd", "m2/rgbatexture64.zbd", "m2/rtexture4.zbd"], token: Token);
        Assert.Equal(0, exported.Failed); Assert.Equal(script, File.ReadAllBytes(fixture.Path("gamegen/m2.gs")));
    }

    [Fact]
    public async Task SharedGroupChangesEveryCopyAndRejectsAssetsInvalidInAnotherMap()
    {
        using SourceWorldFixture fixture = new(); fixture.WritePartDatabase();
        const string part = "data/common/models/shared.gltf";
        var json = JsonNode.Parse(File.ReadAllBytes(fixture.Path("data/m1/models/m1_01.gltf")))!;
        var roots = json["scenes"]![0]!["nodes"]!.DeepClone();
        int at = json["nodes"]!.AsArray().Count;
        json["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "Shared", ["children"] = roots, ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["group"] = true } } });
        json["scenes"]![0]!["nodes"] = new JsonArray(at);
        fixture.Write(part, json.ToJsonString());
        fixture.Write("data/common/models/m1_01.bin", File.ReadAllBytes(fixture.Path("data/m1/models/m1_01.bin")));
        fixture.Write("data/common/textures/rock.png", File.ReadAllBytes(fixture.Path("data/m1/textures/rock.png")));
        string master = File.ReadAllText(fixture.Path("data/m1/models/m1.gltf")).Replace("m1_01.gltf", "../../common/models/shared.gltf", StringComparison.Ordinal);
        var partDocument = WorldAssembler.ReadModel(File.ReadAllBytes(fixture.Path(part)), part, (file, _) => File.ReadAllBytes(fixture.Path(file)), Token);
        var profile = WorldGltf.CaptureZoneProfile(partDocument, token: Token);
        var masterJson = JsonNode.Parse(master)!; uint zoneId = 3;
        foreach (var holder in masterJson["nodes"]!.AsArray().Where(n => n?["extras"]?[WorldGltf.Key]?["ref"] != null))
        {
            uint z = zoneId++;
            holder!["extras"]![WorldGltf.Key]!["zoneOverrides"] = WorldGltf.WriteZoneOverride(profile with { Nodes = profile.Nodes.Select(n => n with { Word = z, Inherit = false }).ToArray() }, Token);
        }
        master = masterJson.ToJsonString();
        fixture.Write("data/m1/models/m1.gltf", master);
        fixture.Write("data/m2/models/m2.gltf", master.Replace("m1.bin", "m2.bin", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: Token);
        var originalZones = built.World.Nodes.Where(n => n.Name == "crate").Select(n => n.Zone).ToArray();
        var scope = SourceObjectEdits.CreationScope(workspace, built.BuildState, new(part, at, "test"), Token);
        Assert.Equal(2, scope.Copies); Assert.Equal(["m1", "m2"], scope.Missions);
        var before = workspace.Overlay();
        var plan = SourceObjectEdits.PlanCreate(workspace, built.World, built.BuildState, scope, "Child", token: Token);
        workspace.Apply(plan.Edit.Label, plan.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), Token);
        SourceObjectEdits.ValidateCreation(workspace, before, plan, built, null, Token);
        Assert.Equal(originalZones, built.World.Nodes.Where(n => n.Name == "crate").Select(n => n.Zone));
        Assert.Equal(2, built.BuildState.AuthoredHierarchy.Nodes.Count(n => n.Name == "Child"));
        Assert.Equal(2, (await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", workspace.Overlay(), Token)).BuildState.AuthoredHierarchy.Nodes.Count(n => n.Name == "Child"));
        fixture.Write("data/m1/models/local.gltf", File.ReadAllText(fixture.Path(fixture.Tank)).Replace("../../textures/bft/camo.png", "../textures/camo.png", StringComparison.Ordinal));
        fixture.Write("data/m1/models/tank.bin", File.ReadAllBytes(fixture.Path("data/m2/models/bft/tank.bin")));
        fixture.Write("data/m1/textures/camo.png", File.ReadAllBytes(fixture.Path("data/m2/textures/bft/camo.png")));
        var candidate = workspace.BeginPreparedEdit().Workspace;
        var original = candidate.Overlay();
        var model = new SourceModelAddition("data/m1/models/local.gltf", "local_new", Vector3.Zero);
        var local = SourceObjectEdits.PlanCreate(candidate, built.World, built.BuildState, scope, model.Name, model, Token);
        candidate.Apply(local.Edit.Label, local.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var withModel = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", candidate.Overlay(), Token);
        Assert.Contains("data/m2/models", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.ValidateCreation(candidate, original, local, withModel, model, Token)).Message);
        Assert.Equal(1, workspace.UndoCount); // A rejected shared-map addition never entered the live workspace.
        fixture.Write("data/common/models/lamp.gltf", File.ReadAllText(fixture.Path("data/m1/models/local.gltf")).Replace("camo.png", "lampcamo.png", StringComparison.Ordinal));
        fixture.Write("data/common/models/tank.bin", File.ReadAllBytes(fixture.Path("data/m1/models/tank.bin")));
        fixture.Write("data/common/textures/lampcamo.png", File.ReadAllBytes(fixture.Path("data/m1/textures/camo.png")));
        candidate = workspace.BeginPreparedEdit().Workspace; original = candidate.Overlay();
        model = new("data/common/models/lamp.gltf", "tank", Vector3.Zero); // Only m2 already has this name.
        var collision = SourceObjectEdits.PlanCreate(candidate, built.World, built.BuildState, scope, model.Name, model, Token);
        candidate.Apply(collision.Edit.Label, collision.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        withModel = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", candidate.Overlay(), Token);
        Assert.Contains("m2", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.ValidateCreation(candidate, original, collision, withModel, model, Token)).Message);
        Assert.Equal(1, workspace.UndoCount);
        fixture.Write("data/m2/zrdr/anim.zad", """
            ( ANIMATION_DEFINITIONS ( ANIMATION_LIST (
              ANIMATION_DEFINITION ( NAME ( "lamp*" ) ANIMATION_NAME ( "blink*" ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( on ) OBJECT_ACTIVE_STATE ( NAME ( "part*" ) STATE ( INACTIVE ) ) ) )
            ) ) )
            """);
        Assert.NotNull((await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", workspace.Overlay(), Token)).Animations);
        candidate = workspace.BeginPreparedEdit().Workspace; original = candidate.Overlay();
        model = new("data/common/models/lamp.gltf", "lamp1", Vector3.Zero);
        var lamps = SourceObjectEdits.PlanCreate(candidate, built.World, built.BuildState, scope, model.Name, model, Token);
        candidate.Apply(lamps.Edit.Label, lamps.Edit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        withModel = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", candidate.Overlay(), Token);
        Assert.Null((await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", candidate.Overlay(), Token)).Animations);
        Assert.Contains("animation definitions unavailable", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.ValidateCreation(candidate, original, lamps, withModel, model, Token)).Message);
    }
}
