using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

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
        player.Reset(); Assert.Equal(0,Assert.Single(player.Frame().Nodes).Transform.M41);
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
        var pose = Assert.Single(new AnimationPlayer(context,0).Frame().Nodes);
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
    private static MissionSceneContext BuildMission(ZbdDocument world, AnimationPackage? package, JsonNode? aiv, JsonNode? vehicles, JsonNode? starts) => MissionSceneLoader.Build(world, package, aiv, vehicles, starts, token: TestContext.Current.CancellationToken);
    [Fact]
    public void AutomaticActivationInitializesBeforeExplicitStartup()
    {
        var context = MissionFixture(); var entry = MissionEntry(1,"automatic","animated"); entry.Bytes[153]=4;
        var position=AnimationCatalog.Create(7); position.SetShort(28,1); position.SetVector(16,new(17,3,9)); entry.Sequences[0].Events.Add(position); context.Package.Entries.Add(entry);
        var mission=BuildMission(context.World,context.Package,null,null,null);
        Assert.Equal(new Vector3(17,3,9),SceneBuilder.LocalTransform(mission.Scene.Nodes[0]).Translation);
    }
    [Fact]
    public void CyclicStartupIsBoundedAndDoesNotChangePrograms()
    {
        var context=MissionFixture(); var entry=MissionEntry(1,"cycle","animated"); var launch=AnimationCatalog.Create(24); launch.SetText(12,"cycle",20); entry.Sequences[0].Events.Add(launch); context.Package.Entries.Add(entry);
        byte[] before=Pack(context.Package);
        var mission=BuildMission(context.World,context.Package,null,null,Arr(Str("NEW_GAME_START"),Str("cycle")));
        Assert.Contains(mission.Diagnostics,n=>n.Contains("instance limit",StringComparison.OrdinalIgnoreCase)); Assert.Equal(before,Pack(context.Package));
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
        var frame=new AnimationPlayer(context,0).Frame(); Assert.True(Assert.Single(frame.Nodes).Visible);
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
