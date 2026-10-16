using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void MissionVehiclesCloneTemplatesAndKeepSourceDataUnchanged()
    {
        var context = MissionFixture(false); string original = context.Scene.Nodes[0].Data.ToJsonString();
        var aiv = Arr(Str("animated_01"), Spawn(10,2,30,90), Str("animated_02"), Spawn(50,6,70,-90));
        var mission = BuildMission(context.World, null, aiv, Arr(Str("animated"), Arr()), null);
        Assert.Equal(2, mission.Actors.Count); Assert.NotEqual(mission.Actors[0].Root, mission.Actors[1].Root);
        Assert.Equal(new Vector3(10,2,30), SceneBuilder.LocalTransform(mission.Scene.Nodes[mission.Actors[0].Root]).Translation);
        var forward = Vector3.TransformNormal(-Vector3.UnitZ, SceneBuilder.LocalTransform(mission.Scene.Nodes[mission.Actors[0].Root]));
        Assert.InRange(forward.X, -1.001f, -.999f);
        Assert.Equal(2, SceneBuilder.Assemble(mission.Scene, token: TestContext.Current.CancellationToken).Placements.Count);
        Assert.All(mission.Actors, a => Assert.Equal(0, a.SourceRoot));
        Assert.Equal(original, context.Scene.Nodes[0].Data.ToJsonString()); Assert.Equal(2, context.Scene.Nodes.Count);
    }
    [Fact]
    public void SourceTransformsRetainInitializedChildrenAndRefuseInitializationDependencies()
    {
        var token = TestContext.Current.CancellationToken;
        var source = MissionFixture();
        var root = source.Scene.Nodes[0];
        source.Scene.Nodes[0] = root with { Children = [2] };
        source.Scene.Nodes.Add(root with { Index = 2, Name = "gun", Parents = [0], Children = [],
            Data = (JsonObject)root.Data.DeepClone(), Metadata = (JsonObject)root.Metadata.DeepClone() });
        var cleanup = MissionEntry(1, "reset", "animated"); cleanup.SetInt(148, 0x20);
        var reference = new AnimationRecord(new byte[40]); reference.SetText(0, "gun", 36); cleanup.References[1].Add(reference);
        var rotate = AnimationCatalog.Create(9); rotate.SetShort(28, 2); rotate.SetVector(16, new(0, .75f, 0)); cleanup.Primary.Events.Add(rotate);
        var hide = AnimationCatalog.Create(6); hide.SetShort(16, 2); hide.SetInt(12, 0); cleanup.Primary.Events.Add(hide);
        source.Package.Entries.Add(cleanup);
        var initial = BuildMission(source.World, source.Package, null, null, null);
        GameScene after = new(); after.Models.AddRange(source.Scene.Models); after.Materials.AddRange(source.Scene.Materials); after.Textures.AddRange(source.Scene.Textures);
        foreach (var node in source.Scene.Nodes) after.Nodes.Add(node with { Data = (JsonObject)node.Data.DeepClone(), Metadata = (JsonObject)node.Metadata.DeepClone() });
        MissionSceneLoader.SetPose(after, 0, Matrix4x4.CreateTranslation(12, 3, 4));
        var incremental = Assert.IsType<MissionSceneContext>(initial.WithSourceTransforms(source.Scene, after, token));
        var complete = BuildMission(new SceneDocument(source.World.Path, after, source.World.Game), source.Package, null, null, null);
        for (int i = 0; i < after.Nodes.Count; i++)
        {
            Assert.Equal(SceneBuilder.LocalTransform(complete.Scene.Nodes[i]), SceneBuilder.LocalTransform(incremental.Scene.Nodes[i]));
            Assert.Equal(complete.Scene.Nodes[i].Metadata.UInt("flags"), incremental.Scene.Nodes[i].Metadata.UInt("flags"));
        }
        Assert.Same(initial.Scene.Nodes[2], incremental.Scene.Nodes[2]);
        Assert.Equal(Vector3.Zero, SceneBuilder.LocalTransform(initial.Scene.Nodes[0]).Translation);
        Assert.Equal(Vector3.Zero, SceneBuilder.LocalTransform(source.Scene.Nodes[0]).Translation);
        // Even an initialization write equal to the old source is authoritative and must not be overwritten.
        var set = AnimationCatalog.Create(7); set.SetShort(28, 1); set.SetVector(16, Vector3.Zero); cleanup.Primary.Events.Add(set);
        Assert.Null(BuildMission(source.World, source.Package, null, null, null).WithSourceTransforms(source.Scene, after, token));
        cleanup.Primary.Events.Remove(set);
        // A child initialized from the root's world position depends on the root even though it never writes it.
        var offset = AnimationCatalog.Create(7); offset.SetShort(28, 2); offset.SetShort(30, 1); cleanup.Primary.Events.Add(offset);
        Assert.Null(BuildMission(source.World, source.Package, null, null, null).WithSourceTransforms(source.Scene, after, token));
        cleanup.Primary.Events.Remove(offset);
        // Dormancy also reads the authored root when a future animation can place it. A move to the origin changes visibility.
        var future = MissionEntry(2, "future", "animated"); var position = AnimationCatalog.Create(7);
        position.SetShort(28, 1); position.SetVector(16, new(100, 0, 0)); position.Threshold = 1; future.Sequences[0].Events.Add(position);
        source.Package.Entries.Add(future);
        var positionedWorld = new SceneDocument(source.World.Path, after, source.World.Game);
        var nonzero = BuildMission(positionedWorld, source.Package, null, null, null);
        Assert.DoesNotContain(0, nonzero.DormantRoots);
        Assert.Contains(0, BuildMission(source.World, source.Package, null, null, null).DormantRoots);
        Assert.Null(nonzero.WithSourceTransforms(after, source.Scene, token));
        source.Package.Entries.Remove(future);
        // The same equality trap also occurs when an AIV record places an existing authored root.
        Assert.Null(BuildMission(source.World, null, Arr(Str("animated"), Spawn(0, 0, 0, 0)), Arr(Str("animated"), Arr()), null)
            .WithSourceTransforms(source.Scene, after, token));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => initial.WithSourceTransforms(source.Scene, after, canceled.Token));
    }
    [Fact]
    public void SourceTransformsRefuseChangedInitializationBindingAcrossPartitions()
    {
        var source = MissionFixture(); var root = source.Scene.Nodes[0];
        source.Scene.Nodes[0] = root with { Children = [2] };
        source.Scene.Nodes[1] = source.Scene.Nodes[1] with { Children = [], Data = Partitions(0, 3) };
        source.Scene.Nodes.Add(root with { Index = 2, Name = "part", Parents = [0], Children = [], Data = (JsonObject)root.Data.DeepClone(), Metadata = (JsonObject)root.Metadata.DeepClone() });
        source.Scene.Nodes.Add(root with { Index = 3, Name = "other", Parents = [1], Children = [4], Data = (JsonObject)root.Data.DeepClone(), Metadata = (JsonObject)root.Metadata.DeepClone() });
        source.Scene.Nodes.Add(root with { Index = 4, Name = "part", Parents = [3], Children = [], Data = (JsonObject)root.Data.DeepClone(), Metadata = (JsonObject)root.Metadata.DeepClone() });
        var setup = MissionEntry(1, "setup", "world"); setup.SetInt(148, 0x20);
        var part = new AnimationRecord(new byte[40]); part.SetText(0, "part", 36); setup.References[1].Add(part);
        var position = AnimationCatalog.Create(7); position.SetShort(28, 2); position.SetVector(16, new(9, 0, 0)); setup.Primary.Events.Add(position);
        source.Package.Entries.Add(setup);
        var initial = BuildMission(source.World, source.Package, null, null, null);
        GameScene after = new(); after.Models.AddRange(source.Scene.Models); after.Materials.AddRange(source.Scene.Materials); after.Textures.AddRange(source.Scene.Textures);
        foreach (var node in source.Scene.Nodes) after.Nodes.Add(node with { Data = (JsonObject)node.Data.DeepClone(), Metadata = (JsonObject)node.Metadata.DeepClone() });
        MissionSceneLoader.SetPose(after, 0, Matrix4x4.CreateTranslation(12, 0, 0));
        Assert.NotNull(initial.WithSourceTransforms(source.Scene, after, TestContext.Current.CancellationToken));
        after.Nodes[1] = after.Nodes[1] with { Data = Partitions(3, 0) };
        var complete = BuildMission(new SceneDocument(source.World.Path, after, source.World.Game), source.Package, null, null, null);
        Assert.Equal(9, SceneBuilder.LocalTransform(initial.Scene.Nodes[2]).M41);
        Assert.Equal(0, SceneBuilder.LocalTransform(initial.Scene.Nodes[4]).M41);
        Assert.Equal(0, SceneBuilder.LocalTransform(complete.Scene.Nodes[2]).M41);
        Assert.Equal(9, SceneBuilder.LocalTransform(complete.Scene.Nodes[4]).M41);
        Assert.Null(initial.WithSourceTransforms(source.Scene, after, TestContext.Current.CancellationToken));
        // Crossing out of a cell changes only derived overflow membership; mission-added actors must survive both directions.
        var aiv = Arr(Str("other_01"), Spawn(50, 0, 0, 0)); var vehicles = Arr(Str("other"), Arr());
        var placed = BuildMission(source.World, null, aiv, vehicles, null);
        after.Nodes[1] = after.Nodes[1] with { Children = [0], Data = new() { ["partitions"] = new JsonArray(new JsonArray(new JsonObject { ["node_indices"] = new JsonArray(3) })) } };
        var outside = Assert.IsType<MissionSceneContext>(placed.WithSourceTransforms(source.Scene, after, TestContext.Current.CancellationToken));
        var outsideFull = BuildMission(new SceneDocument(source.World.Path, after, source.World.Game), null, aiv, vehicles, null);
        Assert.Equal(SceneBuilder.Children(outsideFull.Scene.Nodes[1]), SceneBuilder.Children(outside.Scene.Nodes[1]));
        Assert.Equal(placed.Actors, outside.Actors);
        Assert.Same(placed.Scene.Nodes[placed.Actors[0].Root], outside.Scene.Nodes[placed.Actors[0].Root]);
        var inside = Assert.IsType<MissionSceneContext>(outside.WithSourceTransforms(after, source.Scene, TestContext.Current.CancellationToken));
        Assert.Equal(SceneBuilder.Children(placed.Scene.Nodes[1]), SceneBuilder.Children(inside.Scene.Nodes[1]));
        Assert.Equal(placed.Scene.Nodes[1].Children, inside.Scene.Nodes[1].Children);
        static JsonObject Partitions(int a, int b) => new() { ["partitions"] = new JsonArray(new JsonArray(new JsonObject { ["node_indices"] = new JsonArray(a, b) })) };
    }
    [Theory]
    [InlineData("rumv_easy_01", "rumv_easy")]
    [InlineData("bft_00", "bft")]
    [InlineData("rumv_easy", "rumv_easy")]
    public void VehicleTemplateNamesPreserveNonNumericSuffixes(string actor, string template) => Assert.Equal(template, MissionSceneLoader.VehicleTemplateName(actor));

    [Fact]
    public void InvalidSpawnAndMissingTemplateAreDiagnosedWithoutMovingStoredGeometry()
    {
        var context = MissionFixture(false);
        var invalid = Spawn(1,2,3,0); invalid["children"]![1]!["children"]![0]!["value"] = "NaN";
        var mission = BuildMission(context.World, null, Arr(Str("animated_01"), invalid, Str("missing_01"), Spawn(4,5,6,0)), Arr(Str("animated"), Arr(), Str("missing"), Arr()), null);
        Assert.Empty(mission.Actors); Assert.Contains(mission.Diagnostics, n => n.Contains("Nonfinite")); Assert.Contains(mission.Diagnostics, n => n.Contains("Missing template"));
        Assert.Equal(Vector3.Zero, SceneBuilder.LocalTransform(mission.Scene.Nodes[0]).Translation);
    }
    [Fact]
    public void InitializationRunsFlaggedCleanupAndSamplesStartupAtExactlyZero()
    {
        var context = MissionFixture(); var entry = MissionEntry(1, "setup", "animated"); entry.SetInt(148, 0x20);
        var cleanup = AnimationCatalog.Create(7); cleanup.SetShort(28,1); cleanup.SetVector(16,new(99,0,0)); entry.Primary.Events.Add(cleanup);
        var ev = AnimationCatalog.Create(12); ev.SetInt(12,1); var key = AnimationKeyframe.Create(); key.SetVector(12,new(12,3,4)); key.SetVector(28,new(60,0,0)); entry.Sequences[0].Events.Add(ev.WithKeyframes([key]));
        context.Package.Entries.Add(entry); byte[] bytes = Pack(context.Package);
        var initial = BuildMission(context.World, context.Package, null, null, null);
        Assert.Equal(99, SceneBuilder.LocalTransform(initial.Scene.Nodes[0]).M41);
        var started = BuildMission(context.World, context.Package, null, null, Arr(Str("NEW_GAME_START"), Str("setup")));
        Assert.Equal(new Vector3(12,3,4), SceneBuilder.LocalTransform(started.Scene.Nodes[0]).Translation);
        // Nested startup lists retain their authored order, including repeated names. The final distinct
        // animation must run after setup, without advancing either program beyond its immediate frontier.
        var last = MissionEntry(2, "last", "animated");
        var move = AnimationCatalog.Create(7); move.SetShort(28, 1); move.SetVector(16, new(31, 2, 1)); last.Sequences[0].Events.Add(move);
        context.Package.Entries.Add(last); bytes = Pack(context.Package);
        var nested = Arr(Arr(Str("NEW_GAME_START"), Arr(Str("setup"), Arr(Str("setup"), Str("last")))));
        Assert.Equal(new Vector3(31, 2, 1), SceneBuilder.LocalTransform(BuildMission(context.World, context.Package, null, null, nested).Scene.Nodes[0]).Translation);

        // Repeated matching ancestors used to enumerate every descendant before any operation admission.
        // A small shared allowance exercises the actual Build path without a large allocation/time probe.
        JsonNode leaves = Arr([.. Enumerable.Range(0, 128).Select(_ => (JsonNode)Str("missing")), Str("setup")]);
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(new Vector3(12, 3, 4), SceneBuilder.LocalTransform(MissionSceneLoader.BuildWithBudget(context.World,
            new MissionPlacementBudget(token, maximumWork: 32768), context.Package, null, null,
            Arr(Str("NEW_GAME_START"), leaves.DeepClone()), token: token).Scene.Nodes[0]).Translation);
        JsonNode overlapping = leaves;
        for (int i = 0; i < 24; i++) overlapping = Arr(Str("NEW_GAME_START"), overlapping);
        Assert.Throws<InvalidDataException>(() => MissionSceneLoader.BuildWithBudget(context.World,
            new MissionPlacementBudget(token, maximumWork: 32768), context.Package, null, null, overlapping, token: token));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        int reservations = 0;
        var canceledBudget = new MissionPlacementBudget(cancel.Token, reserved: () => { if (++reservations == 5000) cancel.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => MissionSceneLoader.BuildWithBudget(context.World,
            canceledBudget, context.Package, null, null, overlapping, token: cancel.Token));
        Assert.Equal(new Vector3(12, 3, 4), SceneBuilder.LocalTransform(BuildMission(context.World, context.Package, null, null,
            Arr(Str("NEW_GAME_START"), Str("setup"))).Scene.Nodes[0]).Translation);
        Assert.Equal(bytes, Pack(context.Package)); Assert.Equal(Vector3.Zero, SceneBuilder.LocalTransform(context.Scene.Nodes[0]).Translation);
    }
    [Fact]
    public void UnplacedScriptedActorsStayHiddenUntilAbsoluteMotionAndReplayDeterministically()
    {
        var context = MissionFixture(); var ev = AnimationCatalog.Create(7); ev.SetShort(28,1); ev.SetVector(16,new(12,0,0)); ev.Threshold = 1;
        context.Package.Entries[0].Sequences[0].Events.Add(ev);
        context.Mission = BuildMission(context.World, context.Package, null, null, null);
        Assert.Contains(0, context.Mission.DormantRoots); Assert.Empty(SceneBuilder.Assemble(context.Scene, token: TestContext.Current.CancellationToken).Placements);
        AnimationPlayer player = new(context,0); Assert.False(Assert.Single(player.EvaluateForTest(.5).Nodes).Visible);
        var after = player.EvaluateForTest(1.2); Assert.True(Assert.Single(after.Nodes).Visible); Assert.Equal(12, after.Nodes[0].Transform.M41);
        player.EvaluateForTest(.5,true); Assert.Equal(after.Nodes, player.EvaluateForTest(1.2,true).Nodes);
    }
    [Fact]
    public void WorldAndNestedAnimationShareOneActorIncludingSeekSnapshots()
    {
        var context = MissionFixture(); var parent = context.Package.Entries[0]; parent.SetText(32,"world");
        var child = MissionEntry(1,"child","animated"); var position = AnimationCatalog.Create(7); position.SetShort(28,1); position.SetVector(16,new(42,0,0)); child.Sequences[0].Events.Add(position); context.Package.Entries.Add(child);
        var launch = AnimationCatalog.Create(24); launch.SetText(12,"child",20); launch.Threshold = .1f; parent.Sequences[0].Events.Add(launch);
        AnimationPlayer player = new(context,0); var after = player.EvaluateForTest(1.1); Assert.Equal(42, Assert.Single(after.Nodes).Transform.M41);
        player.EvaluateForTest(.05,true); Assert.Equal(after.Nodes,player.EvaluateForTest(1.1,true).Nodes);
        player.Reset(TestContext.Current.CancellationToken); Assert.Equal(0,Assert.Single(player.Frame(TestContext.Current.CancellationToken).Nodes).Transform.M41);
    }
    [Fact]
    public void CopiedAnimationRootsRemainDistinctFromTheWorldActor()
    {
        var context = MissionFixture(); var parent = context.Package.Entries[0]; parent.SetText(32,"world");
        var child = MissionEntry(1,"copy","animated"); child.SetInt(148,0x8000); context.Package.Entries.Add(child);
        var launch = AnimationCatalog.Create(24); launch.SetText(12,"copy",20); parent.Sequences[0].Events.Add(launch);
        var poses = new AnimationPlayer(context,0).EvaluateForTest(AnimationPlayer.StepSeconds).Nodes;
        Assert.Equal(2,poses.Count); Assert.Equal(2,poses.Select(p=>p.Id).Distinct().Count());
    }
    [Fact]
    public void ExplicitActorBindingDoesNotAlsoRenderItsTemplate()
    {
        var context = MissionFixture(false);
        context.Mission = BuildMission(context.World,null,Arr(Str("animated_01"),Spawn(9,2,7,0)),Arr(Str("animated"),Arr()),null);
        int root = context.Mission.Actors[0].Root; context.RootOverrides[0] = root;
        Assert.Equal(root, context.ResolveNode(context.Package.Entries[0],1));
        var pose = Assert.Single(new AnimationPlayer(context,0).Frame(TestContext.Current.CancellationToken).Nodes);
        Assert.Equal(root,pose.SourceNode); Assert.Equal(new Vector3(9,2,7),pose.Transform.Translation);
    }
    [Fact]
    public void MissionInitializationCancellationLeavesOriginalUntouched()
    {
        var context = MissionFixture(false); using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MissionSceneLoader.Build(context.World,null,Arr(Str("animated_01"),Spawn(1,2,3,0)),Arr(Str("animated"),Arr()),null,token:cancellation.Token));
        Assert.Equal(2,context.Scene.Nodes.Count);
    }
    [Fact]
    public void HorizonBindingsPreserveExplicitXzModeAndFindUnboundMissionHorizon()
    {
        var context = MissionFixture(); context.Scene.Nodes[0] = context.Scene.Nodes[0] with { Name="horizon" };
        Assert.True(Assert.Single(MissionSceneContext.FindHorizons(context.Scene)).FollowHeight);
        context.Scene.Nodes.Add(new(2,"camera","camera",null,[],[],[],new() { ["focus_node_xz"] = 0 }));
        Assert.False(Assert.Single(MissionSceneContext.FindHorizons(context.Scene)).FollowHeight);
    }
    private static MissionSceneContext BuildMission(ISceneDocument world, AnimationPackage? package, JsonNode? aiv, JsonNode? vehicles, JsonNode? starts) => MissionSceneLoader.Build(world, package, aiv, vehicles, starts, token: TestContext.Current.CancellationToken);
    [Fact]
    public void AutomaticActivationInitializesBeforeExplicitStartup()
    {
        var context = MissionFixture(); var entry = MissionEntry(1,"automatic","animated"); entry.Bytes[153]=4;
        var position=AnimationCatalog.Create(7); position.SetShort(28,1); position.SetVector(16,new(17,3,9)); entry.Sequences[0].Events.Add(position); context.Package.Entries.Add(entry);
        var mission=BuildMission(context.World,context.Package,null,null,null);
        Assert.Equal(new Vector3(17,3,9),SceneBuilder.LocalTransform(mission.Scene.Nodes[0]).Translation);

        // The load runs every cleanup before any started entry's events (0x45fb30), and nothing it or the startup list
        // marked running starts again (0x45d930): a later cleanup cannot undo "mover", and neither launch repeats its step.
        context=MissionFixture(); var mover=MissionEntry(1,"mover","animated"); mover.Bytes[153]=4;
        var step=AnimationCatalog.Create(7); step.SetShort(28,1); step.SetInt(12,1); step.SetVector(16,new(0,5,0)); mover.Sequences[0].Events.Add(step);
        var hide=AnimationCatalog.Create(6); hide.SetShort(16,1); hide.SetInt(12,0); mover.Sequences[0].Events.Add(hide);
        var later=AnimationCatalog.Create(6); later.SetShort(16,1); later.Threshold=5; mover.Sequences[0].Events.Add(later);
        var launcher=MissionEntry(2,"launcher","animated"); launcher.Bytes[153]=4; var launch=AnimationCatalog.Create(24); launch.SetText(12,"mover",20); launcher.Sequences[0].Events.Add(launch);
        var reset=MissionEntry(3,"reset","animated"); reset.SetInt(148,0x20); var show=AnimationCatalog.Create(6); show.SetShort(16,1); show.SetInt(12,1); reset.Primary.Events.Add(show);
        context.Package.Entries.AddRange([mover,launcher,reset]);
        var node=BuildMission(context.World,context.Package,null,null,Arr(Str("NEW_GAME_START"),Str("mover"))).Scene.Nodes[0];
        Assert.Equal(new Vector3(0,5,0),SceneBuilder.LocalTransform(node).Translation); Assert.Equal(0u,node.Metadata.UInt("flags")&4);

        // A range-gated entry runs only while the player's spawn (10,2,30), 1004 squared units from its node, is in range (0x45d010).
        context=MissionFixture();
        foreach (var (name,range,lift) in new[] { ("far",1004f,10f), ("near",1005f,1f) })
        {
            var gated=MissionEntry(context.Package.Entries.Count,name,"animated"); gated.Bytes[153]=4; gated.SetInt(148,2); gated.SetFloat(160,range);
            var up=AnimationCatalog.Create(7); up.SetShort(28,1); up.SetInt(12,1); up.SetVector(16,new(0,lift,0)); gated.Sequences[0].Events.Add(up); context.Package.Entries.Add(gated);
        }
        node=BuildMission(context.World,context.Package,Arr(Str("animated_01"),Spawn(10,2,30,0)),Arr(Str("animated"),Arr()),null).Scene.Nodes[0];
        Assert.Equal(new Vector3(0,1,0),SceneBuilder.LocalTransform(node).Translation);
    }
    [Fact]
    public void CyclicStartupIsBoundedAndDoesNotChangePrograms()
    {
        var context=MissionFixture(); var entry=MissionEntry(1,"cycle","animated"); var launch=AnimationCatalog.Create(24); launch.SetText(12,"cycle",20); entry.Sequences[0].Events.Add(launch); context.Package.Entries.Add(entry);
        byte[] before=Pack(context.Package);
        var mission=BuildMission(context.World,context.Package,null,null,Arr(Str("NEW_GAME_START"),Str("cycle")));
        // ActivateRuntime does not start a running animation again (m6/m13 transporters restart each other at zero time).
        Assert.DoesNotContain(mission.Diagnostics,n=>n.Contains("instance limit",StringComparison.OrdinalIgnoreCase)); Assert.Equal(before,Pack(context.Package));
    }
    [Fact]
    public void InvalidTemplateHierarchyRollsBackOnlyItsPreviewClone()
    {
        var context=MissionFixture(false); context.Scene.Nodes[0]=context.Scene.Nodes[0] with { Children=[0] };
        var mission=BuildMission(context.World,null,Arr(Str("animated_01"),Spawn(1,2,3,0)),Arr(Str("animated"),Arr()),null);
        Assert.Empty(mission.Actors); Assert.Equal(2,mission.Scene.Nodes.Count); Assert.Equal(2,mission.SourceNodes.Count);
        Assert.Contains(mission.Diagnostics,n=>n.Contains("Cyclic"));
    }
    [Fact]
    public void IndividualEffectInspectionCanShowAnOtherwiseUnplacedActor()
    {
        var context=MissionFixture(); var motion=MissionEntry(1,"later_motion","animated"); var position=AnimationCatalog.Create(7); position.SetShort(28,1); position.SetVector(16,new(100,0,0)); motion.Sequences[0].Events.Add(position); context.Package.Entries.Add(motion);
        context.Mission=BuildMission(context.World,context.Package,null,null,null);
        Assert.Contains(0,context.Mission.DormantRoots);
        var frame=new AnimationPlayer(context,0).Frame(TestContext.Current.CancellationToken); Assert.True(Assert.Single(frame.Nodes).Visible);
        Assert.Contains(frame.Diagnostics,n=>n.Contains("individual preview"));
        Assert.Empty(SceneBuilder.Assemble(context.Scene,token:TestContext.Current.CancellationToken).Placements);
    }
    private static AnimationPreviewContext MissionFixture(bool attached = true)
    {
        var context = Context(Fixture()); context.Scene.Nodes[0] = context.Scene.Nodes[0] with { Parents = attached ? [1] : [] };
        context.Scene.Nodes.Add(new(1,"world","world",null,[],attached ? [0] : [],new() { ["flags"] = 4 },[])); return context;
    }
    private static AnimationEntry MissionEntry(int index, string name, string root)
    {
        var entry = new AnimationEntry(new byte[308],index,0); entry.SetText(0,name); entry.SetText(32,root); entry.SetFloat(164,-1);
        entry.References[1].Add(new(new byte[40])); var reference = new AnimationRecord(new byte[40]); reference.SetText(0,root,36); entry.References[1].Add(reference);
        entry.Sequences.Add(new(new byte[64]) { Name="motion" }); return entry;
    }
    private static JsonObject Arr(params JsonNode[] nodes) => new() { ["type"]="array", ["children"]=new JsonArray(nodes) };
    private static JsonObject Str(string value) => new() { ["type"]="string",["value"]=value };
    private static JsonObject Num(float value) => new() { ["type"]="float",["value"]=value };
    private static JsonObject Spawn(float x,float y,float z,float yaw) => Arr(Num(0),Arr(Num(x),Num(y),Num(z)),Num(yaw));
}
