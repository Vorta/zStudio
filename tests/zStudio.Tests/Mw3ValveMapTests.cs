using System.Text;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>The valve card's model and edits: one valve per name over every mission file, and edits that keep the source's layout.</summary>
public sealed class Mw3ValveMapTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Mw3ValveSource Source(string path, string text) => new(path, ZrdText.Parse(text, Token));
    private const string Valves = "(\r\n  # the ramp\r\n  bramp (\r\n    shutdown ( owens_01 0 1 )\r\n  )\r\n  bramp (\r\n    delayupdate ( truck_go 18.0 ) # later\r\n  )\r\n  ready (\r\n    all_nonzero\r\n    namelist ( bramp gate )\r\n  )\r\n)\r\n";

    [Fact]
    public void OneValvePerNameGathersReactionsTriggersGatesAndThisMissionsObjectives()
    {
        // Must-have: the tree and card show this model; a use attributed to the wrong valve or mission misleads every edit.
        var map = Mw3ValveMap.Build(
        [
            Source("data/c1/zrdr/aipath/m1/valves.zrd", Valves),
            Source("data/c1/zrdr/aipath/m1/aiv.zrd", "(player (0 vlist ((1 0 0.25 0 0 0.999 0 0.8 1 reg_ramp 0 0.0 0 60.0 1 bramp 0) (0 0 0.25 1 0 0.5 0 0.8 0 0 0 0.0 1 60.0 1 gate 1))))"),
            Source("data/c1/zrdr/aipath/m1/targets.zrd", "(satdish (2 health (35.0) health_below (gate 1 35.0 truck_go 1 0.0)))"),
            Source("data/c1/zrdr/aipath/m1/net_02.zrd", "(version (106) node_00 (12 (0.0 0.0 0.0) (1) valve (1 gate 1) valve_assign (ready 1) deploy (mobilehq_01 1 \"MFB Baker\" 0 0)) node_01 (12 (1.0 0.0 0.0) ()))"),
            Source("data/c1/zrdr/objectives.zrd", "(m1 (primary (valve_change (valve (ready) value (1)))) m2 (fail (valve_change (valve (other_mission) value (1)))))"),
        ], "m1", new(Vehicles: new HashSet<string> { "player" }), Token,
            // An animation sets the valve named as the node it binds; one bound to a node no file names as a valve adds none.
            [new("bramp", 5, "call_ramp", [1]), new("rbrgtwr6", 6, "tower_fall", [0, 1])]);
        Assert.Equal(["bramp", "gate", "MFB Baker", "mobilehq_01", "ready", "truck_go"], map.Valves.Select(v => v.Name));
        var bramp = map.Find("bramp")!;
        Assert.Contains(bramp.SetBy, l => l is { Role: "animation", Owner: "call_ramp", Entry: 5, Value: 1 });
        Assert.Equal(["wakes owens_01", "sets truck_go after 18 s"], bramp.Reactions.Select(r => r.Text));
        Assert.Equal([0, 1], bramp.Reactions.Select(r => r.Occurrence));
        Assert.Equal("wakes owens_01 · 1 timer", bramp.Summary);
        Assert.Equal("inside reg_ramp → 1", bramp.SetBy.Single(l => l.Role == "trigger").Text);
        // A reaction naming a vehicle the mission lacks is the one definite problem the row marks.
        Assert.Contains(bramp.Problems, p => p.Contains("owens_01"));
        var gate = map.Find("gate")!;
        Assert.Equal(["trigger", "health"], gate.SetBy.Select(l => l.Role));
        Assert.Equal("hit or enemy within 60 → 0", gate.SetBy[0].Text);
        Assert.Equal("health below 35 → 1", gate.SetBy[1].Text);
        Assert.Contains(gate.UsedBy, l => l is { Role: "gate", Node: 0, Value: 1 } && l.Owner == "net_02 · node 0");
        Assert.Contains(gate.UsedBy, l => l is { Role: "computed", Valve: "ready" });
        var ready = map.Find("ready")!;
        Assert.Equal("all_nonzero", ready.Condition); Assert.Equal(["bramp", "gate"], ready.Inputs);
        Assert.Contains(ready.UsedBy, l => l.Role == "objective" && l.Owner == "primary objective");
        Assert.Contains(ready.SetBy, l => l.Role == "route");
        Assert.Equal("when destroyed → 1", map.Find("truck_go")!.SetBy.Single(l => l.Role == "health").Text);
        Assert.Contains(map.Find("truck_go")!.SetBy, l => l is { Role: "timer", Valve: "bramp" });
        // The view rings and frames a valve's nodes by the same links: a deploy point's valves are found by name.
        var net = MissionAiNetworks.Decode("test", "", 0, "net_02.zrd", ZrdText.Parse("(version (106) node_00 (12 (0.0 0.0 0.0) (1) deploy (mobilehq_01 1 \"MFB Baker\" 0 0)) node_01 (12 (1.0 0.0 0.0) () mcbwait (hold 1 2.0)))", Token), Token);
        Assert.True(MissionAiValves.HasAssociation(net, net.Nodes[0], "MFB Baker")); Assert.True(MissionAiValves.HasAssociation(net, net.Nodes[0], "mobilehq_01"));
        Assert.True(MissionAiValves.HasAssociation(net, net.Nodes[1], "hold")); Assert.False(MissionAiValves.HasAssociation(net, net.Nodes[1], "MFB Baker"));
        Assert.False(MissionAiValves.HasAssociation(net, net.Nodes[0], null));
        // A second computed definition is dropped by the game with its whole record: its reactions are not the valve's.
        var twice = Mw3ValveMap.Build([Source("data/c1/zrdr/aipath/m1/valves.zrd", "(done (all_nonzero namelist (a b)) done (any_zero namelist (c) sound (alarm 1)))")], "m1", token: Token).Find("done")!;
        Assert.Equal(("all_nonzero", 0), (twice.Condition, twice.Reactions.Count)); Assert.Equal(["a", "b"], twice.Inputs);
        Assert.NotNull(twice.Records[1].Problem); Assert.Single(twice.Problems);
    }

    [Fact]
    public void CardEditsChangeOnlyTheirTokensAndRenameFollowsEveryFile()
    {
        // Must-have: source files are authoritative; an edit that reflows comments or drops unknown text loses authored data.
        byte[] bytes = Encoding.Latin1.GetBytes(Valves);
        string Edit(SourceValveEdits.ValveChanges changes, string name = "bramp") => Encoding.Latin1.GetString(SourceValveEdits.PlanValve(bytes, name, changes, Token).Content);
        // A changed delay replaces one token; the comments and the other record stay byte for byte.
        Assert.Equal(Valves.Replace("18.0", "20.5"), Edit(new(Reactions: [new(1, "delayupdate", new("delayupdate", "truck_go", "20.5"))])));
        // Wake becomes a once-only sound in place: the tag and its arguments change, nothing else.
        Assert.Equal(Valves.Replace("shutdown ( owens_01 0 1 )", "sound_once ( com_mech 1 )"), Edit(new(Reactions: [new(0, "shutdown", new("sound_once", "com_mech", When: "1"))])));
        // A new reaction is its own record after the valve's last one; removing the only reaction of a record removes that record.
        string added = Edit(new(Reactions: [new(null, null, new("sound", "alarm", When: "1")), new(0, "shutdown", null)]));
        // (The comment directly above the removed record goes with it, as the rewriter documents.)
        Assert.DoesNotContain("shutdown", added); Assert.Contains("# later", added);
        var program = Mw3ValveProgram.Read(ZrdText.Parse(added, Token), Token);
        Assert.Equal(["bramp:delayupdate", "bramp:sound", "ready:"], program.Definitions.Select(d => d.Name + ":" + string.Join(",", d.Actions.Select(a => a.Kind))));
        // The starting value and a computed valve's inputs.
        Assert.Contains("value ( 1 )", Edit(new(Initial: "1")));
        Assert.Equal(Valves.Replace("all_nonzero", "any_zero").Replace("( bramp gate )", "( bramp truck_go )"), Edit(new(Condition: "any_zero", Inputs: ["bramp", "truck_go"]), "ready"));
        Assert.DoesNotContain("namelist", Edit(new(Condition: ""), "ready"));
        // A computed valve keeps its stored start (the game ignores it): the card shows what is stored, so switch → computed →
        // switch never applies a start other than the one shown.
        string computed = Encoding.Latin1.GetString(SourceValveEdits.PlanValve(Encoding.Latin1.GetBytes("(armed (value (1)) other (value (0)))"), "armed", new(Condition: "any_nonzero", Inputs: ["other"]), Token).Content);
        Assert.Contains("value (1)", computed);
        var armed = Mw3ValveMap.Build([Source("data/c1/zrdr/aipath/m1/valves.zrd", computed)], "m1", token: Token).Find("armed")!;
        Assert.Equal(("any_nonzero", 1), (armed.Condition, armed.Initial));
        Assert.Throws<InvalidDataException>(() => Edit(new(Condition: "all_zero", Inputs: ["ready"]), "ready"));
        // Two computed definitions: changing or removing the first would promote the one the game drops, so it is refused.
        byte[] twice = Encoding.Latin1.GetBytes("(done (all_nonzero namelist (a b)) done (any_zero namelist (c)))");
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.PlanValve(twice, "done", new(Condition: ""), Token));
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.PlanValve(twice, "done", new(Inputs: ["a"]), Token));
        // Editing one field of a reaction leaves the others as stored: whole-number coordinates stay whole numbers.
        Assert.Equal("(go (teleport (other (1 2 3) 0 1)))", Encoding.Latin1.GetString(SourceValveEdits.PlanValve(Encoding.Latin1.GetBytes("(go (teleport (mech (1 2 3) 0 1)))"), "go",
            new(Reactions: [new(0, "teleport", new("teleport", "other", When: "1", X: "1", Y: "2", Z: "3", Yaw: "0"))]), Token).Content));
        Assert.Equal("(go (teleport (mech (1 2.5 3) 0 1)))", Encoding.Latin1.GetString(SourceValveEdits.PlanValve(Encoding.Latin1.GetBytes("(go (teleport (mech (1 2 3) 0 1)))"), "go",
            new(Reactions: [new(0, "teleport", new("teleport", "mech", When: "1", X: "1", Y: "2.5", Z: "3", Yaw: "0"))]), Token).Content));
        // A whole number that becomes a fraction still changes in place: the comment after it and the lines around it stay.
        const string commented = "(go (teleport (mech (1 # keep Y\r\n  2 3) 0 1)))";
        Assert.Equal(commented.Replace("(1 #", "(1.5 #"), Encoding.Latin1.GetString(SourceValveEdits.PlanValve(Encoding.Latin1.GetBytes(commented), "go",
            new(Reactions: [new(0, "teleport", new("teleport", "mech", When: "1", X: "1.5", Y: "2", Z: "3", Yaw: "0"))]), Token).Content));
        Assert.Equal("(go (value (1) # start\r\n))", Encoding.Latin1.GetString(SourceValveEdits.PlanValve(Encoding.Latin1.GetBytes("(go (value (0.0) # start\r\n))"), "go", new(Initial: "1"), Token).Content));
        // One draft that removes a reaction and gives another its kind is valid whatever order its changes come in.
        byte[] both = Encoding.Latin1.GetBytes("(scream (sound (yell 1) delayupdate (later 2.0)))");
        SourceValveEdits.ReactionChange retime = new(0, "sound", new("delayupdate", "after", "5")), drop = new(0, "delayupdate", null);
        string forward = Encoding.Latin1.GetString(SourceValveEdits.PlanValve(both, "scream", new(Reactions: [retime, drop]), Token).Content);
        Assert.Equal("(scream (delayupdate (after 5.0)))", forward);
        Assert.Equal(forward, Encoding.Latin1.GetString(SourceValveEdits.PlanValve(both, "scream", new(Reactions: [drop, retime]), Token).Content));
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.PlanValve(both, "scream", new(Reactions: [retime]), Token));
        // Replacing one input changes only its word: the notes around it stay.
        string noted = "(all_up (all_nonzero namelist (\r\n  alpha\r\n  # the second gate\r\n  beta # opens late\r\n  gamma)))";
        Assert.Equal(noted.Replace("beta #", "delta #"), Encoding.Latin1.GetString(SourceValveEdits.PlanValve(Encoding.Latin1.GetBytes(noted), "all_up", new(Inputs: ["alpha", "delta", "gamma"]), Token).Content));
        // Two rows given other names are those rows: nothing moves, each note stays on its line.
        Assert.Equal(noted.Replace("beta #", "gamma #").Replace("gamma)))", "delta)))"), Encoding.Latin1.GetString(SourceValveEdits.PlanValve(Encoding.Latin1.GetBytes(noted), "all_up", new(Inputs: ["alpha", "gamma", "delta"]), Token).Content));
        // A removed row goes with its notes; the rows that stay keep theirs; an added one is new.
        string shorter = Encoding.Latin1.GetString(SourceValveEdits.PlanValve(Encoding.Latin1.GetBytes(noted), "all_up", new(Inputs: ["alpha", "gamma"]), Token).Content);
        Assert.True(!shorter.Contains("beta") && !shorter.Contains("opens late") && shorter.Contains("  alpha\r\n") && shorter.EndsWith("gamma)))", StringComparison.Ordinal), shorter);
        Assert.Equal(noted.Replace("gamma)))", "gamma\r\n  omega\r\n)))"), Encoding.Latin1.GetString(SourceValveEdits.PlanValve(Encoding.Latin1.GetBytes(noted), "all_up", new(Inputs: ["alpha", "beta", "gamma", "omega"]), Token).Content));
        // Skipping to a timer stops on the first tick that runs it (strictly after its time): one due a moment later still waits.
        double first = Mw3ValveSimulation.TimerTime(0, 1f);
        Assert.Equal(61, Math.Round(first * 60)); Assert.True(1f < (float)first && !(1.02f < (float)first));
        Assert.Equal(1, Math.Round(Mw3ValveSimulation.TimerTime(0, 0f) * 60)); Assert.Equal(31, Math.Round(Mw3ValveSimulation.TimerTime(.5, .25f) * 60));
        // A switch that still stores an input list (its condition commented out, say): making it computed uses that list, the
        // one the game reads, instead of adding a second one it would never reach.
        Assert.Equal("(gate ( all_nonzero namelist (new_input) sound (alarm 1)))", Encoding.Latin1.GetString(SourceValveEdits.PlanValve(
            Encoding.Latin1.GetBytes("(gate (namelist (old_input) sound (alarm 1)))"), "gate", new(Condition: "all_nonzero", Inputs: ["new_input"]), Token).Content));
        // An edit the game would not read as shown is refused: here the list is nested where the card does not reach it.
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.PlanValve(Encoding.Latin1.GetBytes("(gate ((namelist (old_input)) sound (alarm 1)))"), "gate", new(Condition: "all_nonzero", Inputs: ["new_input"]), Token));
        // A record that stores a plain and a once-only sound: the game uses the once-only one. Its fields can change, but
        // making it plain or removing it would make the other one the active sound, so that is refused.
        byte[] shadowed = Encoding.Latin1.GetBytes("(alarm (sound (old 1) sound_once (new 1)))");
        Assert.Equal("(alarm (sound (old 1) sound_once (newer 1)))", Encoding.Latin1.GetString(SourceValveEdits.PlanValve(shadowed, "alarm", new(Reactions: [new(0, "sound_once", new("sound_once", "newer", When: "1"))]), Token).Content));
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.PlanValve(shadowed, "alarm", new(Reactions: [new(0, "sound_once", new("sound", "new", When: "1"))]), Token));
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.PlanValve(shadowed, "alarm", new(Reactions: [new(0, "sound_once", null)]), Token));
        // What a file already has wrong (two computed definitions, as a shipped mission has) neither blocks another valve's
        // edit nor lets it add a reaction that can never finish.
        byte[] conflicted = Encoding.Latin1.GetBytes("(done (all_nonzero namelist (a b)) done (any_zero namelist (c)) start (sound (go 1)))");
        Assert.Contains("sound_once", Encoding.Latin1.GetString(SourceValveEdits.PlanValve(conflicted, "start", new(Reactions: [new(2, "sound", new("sound_once", "go", When: "1"))]), Token).Content));
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.PlanValve(conflicted, "start", new(Reactions: [new(null, null, new("update", "start"))]), Token));
        Assert.Contains("(go 2)", Encoding.Latin1.GetString(SourceValveEdits.Plan("valves.zrd", conflicted, "update", 2, "start", "(sound (go 2))", Token).Content));
        Assert.Throws<InvalidDataException>(() => Edit(new(Reactions: [new(null, null, new("update", "bramp"))])));
        // A valve no record names yet gets one; a mission without valves.zrd gets the file.
        string fresh = Edit(new(), "fresh");
        Assert.StartsWith(Valves[..^3], fresh);
        Assert.Contains(Mw3ValveProgram.Read(ZrdText.Parse(fresh, Token), Token).Definitions, d => d is { Name: "fresh", Initial: 0 });
        Assert.Contains("fresh", Encoding.Latin1.GetString(SourceValveEdits.PlanValve(null, "fresh", new(), Token).Content));
        // Rename: records, another valve's inputs, a trigger, a gate, a deploy point and only this mission's objective.
        Assert.Equal(Valves.Replace("bramp", "ramp"), Encoding.Latin1.GetString(SourceValveEdits.PlanRename("valves.zrd", bytes, "bramp", "ramp", "m1", Token)!.Content));
        string Rename(string member, string text, string name) => Encoding.Latin1.GetString(SourceValveEdits.PlanRename(member, Encoding.Latin1.GetBytes(text), name, "renamed", "m1", Token)!.Content);
        Assert.Equal("(player (0 vlist ((1 0 0.25 0 0 0.999 0 0.8 1 bramp 0 0.0 0 60.0 1 renamed 0))))", Rename("aiv.zrd", "(player (0 vlist ((1 0 0.25 0 0 0.999 0 0.8 1 bramp 0 0.0 0 60.0 1 bramp 0))))", "bramp"));
        Assert.Equal("(version (106) node_00 (12 (0.0 0.0 0.0) () valve (1 renamed 1) deploy (renamed 1 other 0 0)))", Rename("net_02.zrd", "(version (106) node_00 (12 (0.0 0.0 0.0) () valve (1 bramp 1) deploy (bramp 1 other 0 0)))", "bramp"));
        Assert.Equal("(m1 (primary (valve_change (valve (renamed)))) m2 (primary (valve_change (valve (bramp)))))", Rename("objectives.zrd", "(m1 (primary (valve_change (valve (bramp)))) m2 (primary (valve_change (valve (bramp)))))", "bramp"));
        Assert.Null(SourceValveEdits.PlanRename("targets.zrd", Encoding.Latin1.GetBytes("(satdish (2 health_below (other 1 0.0)))"), "bramp", "renamed", "m1", Token));
        Assert.DoesNotContain("bramp", Encoding.Latin1.GetString(SourceValveEdits.PlanRemove(bytes, "bramp", Token).Content).Replace("namelist ( bramp", ""));
    }

    [Fact]
    public async Task OptionalRetailC1MissionReadsAsTheShippedFilesSay()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_MW3_SOURCE_PROJECT");
        if (root == null || SourceProject.Game(root, Token) != GameVariant.MechWarrior3) return;
        var source = await SourceWorlds.LoadSceneAsync(root, "c1", token: Token);
        var callbacks = Mw3ValveSimulation.CallbackValves(source.Animations!, source.MissionScene(MissionDifficulty.Medium, Token), Token);
        var map = Mw3ValveMap.Build(source.Resources.Values.Where(r => r.Root != null).Select(r => new Mw3ValveSource(r.Path, r.Root!)), Path.GetFileName(source.SourceMission), token: Token, callbacks: callbacks);
        var bramp = map.Find("bramp")!;
        Assert.Equal(4, bramp.Records.Count);
        // The ramp's animation sets bramp through the scene node of that name (the test binds it the same way).
        Assert.Contains(bramp.SetBy, l => l is { Role: "animation", Owner: "call_ramp" });
        Assert.Equal("wakes owens_01 · 2 timers · 1 sound", bramp.Summary);
        Assert.Contains(map.Find("comcntr_down")!.SetBy, l => l is { Role: "health", Actor: "satdish" });
        Assert.Contains(map.Find("comcntr_down")!.UsedBy, l => l.Role == "objective");
        Assert.Contains(map.Find("mcbdeploy")!.UsedBy, l => l.Role == "gate");
        Assert.DoesNotContain(map.Valves, v => v.Name == "outside_c1m2");
    }
}
