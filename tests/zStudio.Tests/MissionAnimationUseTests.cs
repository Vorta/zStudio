using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void AnimationUsesResolveRecipientsWithoutMistakingPositionReferencesOrRunningEvents()
    {
        var context = TurretFixture();
        var child = ResetEntry(2, "child"); context.Package.Entries.Add(child);
        var caller = context.Package.Entries[1];
        int target = Ref(caller, "tur_02"), position = Ref(caller, "tur_A3");
        var call = AnimationCatalog.Create(24); call.SetText(12, "child"); call.SetShort(44, (short)target);
        call.SetShort(46, 1); call.SetShort(52, (short)position); call.SetShort(50, -1);
        caller.Sequences[0].Events.Add(call);
        // Recursive activation is a finite association graph, not a playback request.
        var cycle = AnimationCatalog.Create(24); cycle.SetText(12, "child");
        cycle.SetShort(44, (short)Ref(child, "tur_02")); cycle.SetShort(50, -1);
        child.Sequences[0].Events.Add(cycle);
        // An actual external write gets a badge; an offset reference alone does not.
        var write = AnimationCatalog.Create(7); write.SetShort(28, (short)Ref(child, "tur_003"));
        write.SetShort(30, (short)Ref(child, "tur_A3")); child.Sequences[0].Events.Add(write);
        var state = NodeState(context.Scene); var bytes = Pack(context.Package);
        var mission = MissionSceneLoader.Build(context.World, context.Package, null, null, null, token: TestContext.Current.CancellationToken);
        Assert.Contains(mission.AnimationUses[7].Uses, u => u.Name == "child" && u.Origin == "indirect");
        Assert.Contains(mission.AnimationUses[17].Uses, u => u.Name == "child" && u.Reason.Contains("outside its bound root"));
        Assert.False(mission.AnimationUses.ContainsKey(12));
        Assert.Equal(bytes, Pack(context.Package)); Assert.Equal(state, NodeState(context.Scene));
        Assert.DoesNotContain(mission.Diagnostics, d => d.Contains("discovery is incomplete"));
        static int Ref(AnimationEntry entry, string name)
        { var r = new AnimationRecord(new byte[40]); r.SetText(0, name, 36); entry.References[1].Add(r); return entry.References[1].Count - 1; }
    }

    [Fact]
    public void AnimationUsesFollowVehiclePickupAndWeaponModelIdentity()
    {
        var context = MissionFixture(false);
        var template = context.Scene.Nodes[0];
        context.Scene.Nodes[0] = template with { Children = [2] };
        context.Scene.Nodes.Add(template with { Index = 2, Name = "weapon_model", Parents = [0], Children = [],
            Data = (JsonObject)template.Data.DeepClone(), Metadata = (JsonObject)template.Metadata.DeepClone() });
        context.Package.Entries[0].SetText(0, "");
        foreach (string name in new[] { "start", "animated", "transition", "model_fx", "position_fx" })
            context.Package.Entries.Add(MissionEntry(context.Package.Entries.Count, name, "animated"));
        var aiv = Arr(Str("animated_01"), Spawn(10, 2, 30, 90));
        var vehicles = Arr(Str("animated"), Arr(
            Str("common_mode"), Arr(Str("start_anims"), Arr(Str("start"))),
            Str("track_mode"), Arr(Str("mode"), Arr(Str("track")), Str("t2a_anims"), Arr(Str("transition")))));
        var weapons = Arr(Str("VERSION"), Arr(new JsonObject { ["type"] = "int", ["value"] = 2 }),
            Str("BALLISTICS"), Arr(Str("test"), Arr(Str("FLYOUT"), Arr(
                Str("MODEL"), Arr(Str("weapon_model")), Str("MODEL_ANIMATION"), Arr(Str("model_fx")),
                Str("ANIMATION_ATTACHED"), Arr(Str("position_fx"))))));
        var mission = MissionSceneLoader.Build(context.World, context.Package, aiv, vehicles, null,
            token: TestContext.Current.CancellationToken, weapons: weapons);
        int root = Assert.Single(mission.Actors).Root;
        Assert.False(mission.AnimationUses[root].HasInitialRoot);
        Assert.Equal(new[] { "start", "animated", "transition" }, mission.AnimationUses[root].Names);
        Assert.Contains(mission.AnimationUses[2].Uses, u => u.Name == "model_fx" && u.Reason.Contains("Weapon"));
        Assert.False(mission.AnimationUses.ContainsKey(Assert.Single(mission.Scene.Nodes[root].Children)));
        Assert.DoesNotContain(mission.AnimationUses[root].Uses, u => u.Name == "position_fx");
        Assert.DoesNotContain(mission.Diagnostics, d => d.Contains("discovery is incomplete"));
        // The same loader gives EFFECT precedence even when its array is empty.
        var flyout = weapons["children"]![3]!["children"]![1]!["children"]![1]!["children"]!.AsArray();
        flyout.Add(Str("EFFECT")); flyout.Add(Arr());
        var effect = MissionSceneLoader.Build(context.World, context.Package, aiv, vehicles, null,
            token: TestContext.Current.CancellationToken, weapons: weapons);
        Assert.False(effect.AnimationUses.ContainsKey(2));
        var pickup = PickupFixture(); pickup.Package.Entries[0].SetText(0, "");
        pickup.Package.Entries.Add(MissionEntry(1, "pu001", "pu001"));
        var placed = MissionSceneLoader.Build(pickup.World, pickup.Package, null, null, null,
            token: TestContext.Current.CancellationToken, pickups: PickupList(PickupRow()));
        Assert.Contains(placed.AnimationUses[Assert.Single(placed.Actors).Root].Uses, u => u.Name == "pu001" && u.Reason == "Pickup collection");
    }
}
