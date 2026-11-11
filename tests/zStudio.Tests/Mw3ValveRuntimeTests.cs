using System.Text;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class Mw3ValveRuntimeTests
{
    [Fact]
    public async Task OptionalRetailC1StartupConnectsAnimationCallbacksAndValveReactions()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_MW3_SOURCE_PROJECT");
        if (root == null || SourceProject.Game(root, TestContext.Current.CancellationToken) != GameVariant.MechWarrior3) return;
        var token = TestContext.Current.CancellationToken;
        var source = await SourceWorlds.LoadSceneAsync(root, "c1", token: token);
        var baseline = source.MissionScene(MissionDifficulty.Medium, token);
        var resource = source.Resources.Values.Single(r => Path.GetFileName(r.Path) == "valves.zrd");
        byte[] before = File.ReadAllBytes(Path.Combine(root, resource.Path));
        var context = zStudio.Core.Animation.AnimationPreviewContext.FromSourceBaseline(source, baseline.ValveStartupBaseline(token), token);
        var simulation = new Mw3ValveSimulation(context, Mw3ValveProgram.Read(resource.Root!, token), token);
        // Startup starts call_ramp; its attach is outside the authored 500-unit range.
        Assert.Equal(0, simulation.Runtime.Value("bramp"));
        Assert.Contains(simulation.CurrentFrame.Instances, i => i.Name == "call_ramp");
        simulation.Set("bramp", 1, token);
        Assert.False(simulation.Actors["owens_01"].PoweredDown);
        Assert.Contains(simulation.Runtime.Timers, t => t.Target == "truck_go" && t.Due == 18);
        simulation.Advance(18, token); Assert.Equal(0, simulation.Runtime.Value("truck_go"));
        simulation.Advance(18 + zStudio.Core.Animation.AnimationPlayer.StepSeconds, token); Assert.Equal(1, simulation.Runtime.Value("truck_go"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, resource.Path)));
    }
    private static ZrdNode Parse(string text) => ZrdText.Parse(text, TestContext.Current.CancellationToken);
    private static Mw3ValveRuntime Runtime(string text) => new(Mw3ValveProgram.Read(Parse(text), TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("(sound alarm)")]
    [InlineData("(value (1 2))")]
    [InlineData("(all_nonzero namelist (go 1))")]
    [InlineData("(sound)")]
    [InlineData("(value)")]
    [InlineData("(all_zero namelist (sound) sound (tone 1))")]
    public void SourceEditsRefuseMalformedRecognizedProperties(string parameters)
    {
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.Plan("valves.zrd", Encoding.Latin1.GetBytes("(go (value (0)))"), "update", 0, "go", parameters, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void SourceAssociationValidationAndRawInitialWordsSurviveEditing()
    {
        byte[] network = Encoding.Latin1.GetBytes("(version (106) node_00 (12 (0.0 0.0 0.0) (0) valve (1 gate) valve_assign (passed 1)))");
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.Plan("net_01.zrd", network, "update", 0, null, "(1)", TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.Plan("net_01.zrd", network, "update", 1, null, "(passed)", TestContext.Current.CancellationToken));
        Assert.Equal(BitConverter.SingleToInt32Bits(1), Runtime("(go (value (1.0)))").Value("go"));
        Assert.Equal(1, Runtime("(go (namelist (other) all_zero))").Value("go"));
        var terminal = MissionAiValves.Records("valves.zrd", Parse("(go (namelist (other) all_zero))"), TestContext.Current.CancellationToken).Single();
        Assert.Contains(MissionAiValves.References(terminal, TestContext.Current.CancellationToken), r => r.Name == "other" && r.Role == "compound input");
        var objective = MissionAiValves.Records("objectives.zrd", Parse("(objective (valve_change (valve (gate) value (1))))"), TestContext.Current.CancellationToken).Single();
        Assert.Null(MissionAiValves.Problem(objective, TestContext.Current.CancellationToken));
        var health = MissionAiValves.Records("targets.zrd", Parse("(actor (health_below (normal 1 35.0 set_valve 1 0.0)))"), TestContext.Current.CancellationToken).ToArray();
        Assert.Equal("set_valve", Assert.Single(MissionAiValves.References(health[1], TestContext.Current.CancellationToken)).Name);
    }

    // The native change notifications and timer frontier drive ordinary shipped dialogue and activation chains.
    [Fact]
    public void RepeatedDefinitionsConditionsOnceAndRetailTimerFrontier()
    {
        var runtime = Runtime("""
            (go (sound_once (first 1) sound (ignored 1))
             go (shutdown (actor 0 1) delayupdate (later 2.0))
             any (any_nonzero namelist (go other) sound (compound 1))
             later (delayupdate (last 1.0))
             last (sound (final 1)))
            """);
        runtime.Set("go", 1);
        Assert.Equal(new[] { "sound_once", "shutdown", "sound" }, runtime.DrainEffects().Select(e => e.Kind));
        Assert.Equal(1, runtime.Value("any"));
        runtime.Set("go", 1); Assert.Empty(runtime.DrainEffects());
        runtime.Tick(2); Assert.Equal(0, runtime.Value("later"));
        runtime.Tick(2.01f); Assert.Equal(1, runtime.Value("later"));
        Assert.Empty(runtime.Timers); // Retail lone-timer replacement drops the newly made timer.
        Assert.Contains(runtime.Trace, t => t.Kind == "timer quirk");
        runtime.Set("other", 1); Assert.Single(runtime.DrainEffects()); // Compound value stayed one; its reactions still fire.
        runtime.Set("go", 0); runtime.Set("go", 1);
        Assert.DoesNotContain(runtime.DrainEffects(), e => e.Kind == "sound_once");
        Assert.Equal(0, runtime.Value("GO")); // Names are exact.
    }

    [Fact]
    public void CompositeTimersVisitNewTimersAndComputedWritesKeepTheirValue()
    {
        var runtime = Runtime("(a (delayupdate (b 1.0)) a (delayupdate (hold 10.0)) b (delayupdate (c 1.0)) test (all_zero namelist (a b) sound (sample 1)))");
        Assert.Equal(1, runtime.Value("test"));
        runtime.Set("test", 0); Assert.Equal(1, runtime.Value("test")); Assert.Single(runtime.DrainEffects());
        runtime.Set("a", 1); runtime.Tick(1.1f);
        Assert.Equal(2, runtime.Timers.Count);
        runtime.Tick(2.2f); Assert.Equal(1, runtime.Value("c"));
        var retained = Runtime("(go (delayupdate (early 1.0)) go (delayupdate (later 10.0)) later (delayupdate (last 1.0)))");
        retained.Set("go", 1); retained.Tick(1.1f); Assert.Single(retained.Timers);
        retained.Tick(10.1f); Assert.Single(retained.Timers); // Native slot remains a composite across ticks.
        retained.Tick(11.2f); Assert.Equal(1, retained.Value("last"));
    }

    [Fact]
    public void SourceChangesKeepCommentsOccurrencesAndRejectImmediateCycles()
    {
        const string text = "(\n# header\ngo (sound (\"old\" 1)) # retained\n\ngo (delayupdate (later 2.0))\n)";
        var edited = SourceValveEdits.Plan("valves.zrd", Encoding.Latin1.GetBytes(text), "update", 0, "go", "(sound (\"new\" 1))", TestContext.Current.CancellationToken);
        string changed = Encoding.Latin1.GetString(edited.Content);
        Assert.Equal(text.Replace("\"old\"", "new"), changed);
        Assert.Throws<InvalidDataException>(() => SourceValveEdits.Plan("valves.zrd", edited.Content, "update", 0, "go", "(update (go))", TestContext.Current.CancellationToken));
        var removed = SourceValveEdits.Plan("valves.zrd", edited.Content, "delete", 1, null, null, TestContext.Current.CancellationToken);
        Assert.Single(MissionAiValves.Records("valves.zrd", Parse(Encoding.Latin1.GetString(removed.Content)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OutgoingDefaultsUsePhysicalNonnegativeSlotsAndAssignmentsDoNotGate()
    {
        var root = Parse("(version (106) node_00 (12 (0.0 0.0 0.0) (1 -1 2) valve (1 go)) node_01 (12 (1.0 0.0 0.0) ()) node_02 (12 (2.0 0.0 0.0) ()) node_03 ((0 1 2) valve_assign (passed 7)))");
        var net = MissionAiNetworks.Decode("test", "", 0, "net_01.zrd", root, TestContext.Current.CancellationToken);
        var runtime = Runtime("()");
        Assert.True(Mw3ValveRoutes.Evaluate(net, net.Nodes[0], 0, runtime).Passable);
        Assert.False(Mw3ValveRoutes.Evaluate(net, net.Nodes[0], 2, runtime).Passable);
        Assert.Single(Mw3ValveRoutes.Evaluate(net, net.Nodes[0], 0, runtime).Assignments);
        runtime.Set("go", 1);
        Assert.True(Mw3ValveRoutes.Evaluate(net, net.Nodes[0], 2, runtime).Passable);
    }
}
