using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>Native authoring must use source missions, including library actors, through the same GUI/MCP workspace.</summary>
internal static class Mw3SourceAuthoringMcpChecks
{
    internal static async Task Run(MainWindow main, string project,
        Func<string, Dictionary<string, object?>, Task<JsonNode>> call,
        Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        const string model = "data/common/mechs/models/fixture.gltf";
        Directory.CreateDirectory(Path.Combine(project, "data/lab/models"));
        File.WriteAllText(Path.Combine(project, "gamegen/lab.gs"), "# library setup, not a campaign map\r\n");
        var maps = (await call("source_world_list", new()))["items"]!.AsArray();
        Assert.DoesNotContain(maps, item => item!["mission"]!.GetValue<string>() == "lab");
        Directory.CreateDirectory(Path.Combine(project, "data/common/mechs/models"));
        Directory.CreateDirectory(Path.Combine(project, "data/common/mechs/textures"));
        var json = JsonNode.Parse(File.ReadAllBytes(Path.Combine(project, "data/c1/models/c1.gltf")))!;
        json["scenes"]![0]!["extras"]![WorldGltf.Key]!["mechLibraryMember"] = "mech_fixture.flt";
        int rootNode = json["scenes"]![0]!["nodes"]![0]!.GetValue<int>();
        json["nodes"]![rootNode]!["name"] = "fixture";
        json["nodes"]![rootNode]!["extras"]![WorldGltf.Key]!["name"] = "fixture";
        foreach (var image in json["images"]!.AsArray()) image!["uri"] = "../textures/mechskin.png";
        foreach (var material in json["materials"]!.AsArray()) material!["extras"]![WorldGltf.Key]!["texture"] = "mechskin";
        File.WriteAllText(Path.Combine(project, model), json.ToJsonString());
        File.Copy(Path.Combine(project, "data/c1/models/c1.bin"), Path.Combine(project, "data/common/mechs/models/c1.bin"));
        File.Copy(Path.Combine(project, "data/c1/textures/rock.png"), Path.Combine(project, "data/common/mechs/textures/mechskin.png"));
        foreach (string mission in new[] { "m1", "mp1" })
        {
            string folder = Directory.CreateDirectory(Path.Combine(project, $"data/c1/zrdr/aipath/{mission}")).FullName;
            File.WriteAllText(Path.Combine(folder, "aiv.zrd"), $"( fixture_01 ( 0 ( {(mission == "m1" ? "1.0" : "7.0")} 2.0 3.0 ) 0.0 ) )\r\n");
            File.WriteAllText(Path.Combine(folder, "net_01.zrd"), "( version ( 106 ) node_00 ( 12 ( 1.0 2.0 3.0 ) ( 1 ) wait_interval ( 1 2.0 ) valve (1 go 1) ) node_01 ( 12 ( 4.0 5.0 6.0 ) ( 0 ) ) )\r\n");
            if (mission == "m1") File.WriteAllText(Path.Combine(folder, "valves.zrd"), "( go ( shutdown ( fixture_01 0 1 ) delayupdate ( later 1.0 ) ) )\r\n");
        }
        const string nestedDefinition = "data/c1/zrdr/native-definitions/ground.zad";
        Directory.CreateDirectory(Path.Combine(project, "data/c1/zrdr/native-definitions"));
        File.Move(Path.Combine(project, Mw3TestCorpus.Definitions), Path.Combine(project, nestedDefinition));
        string nativeDefinition = File.ReadAllText(Path.Combine(project, nestedDefinition));
        string withRange = nativeDefinition.Replace("ACTIVATION ( ON_CALL )", "ACTIVATION ( ON_CALL ) EXECUTION_BY_RANGE ( 450.0 800.0 )", StringComparison.Ordinal);
        Assert.NotEqual(nativeDefinition, withRange); File.WriteAllText(Path.Combine(project, nestedDefinition), withRange);
        File.WriteAllText(Path.Combine(project, "data/c1/zrdr/anim.zad"), "ANIMATION_DEFINITIONS ( ANIMATION_PATH ( \"../data/c1/zrdr\" ) ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( native-parent.zad ) ) )\r\n");
        File.WriteAllText(Path.Combine(project, "data/c1/zrdr/native-parent.zad"), "ANIMATION_DEFINITIONS ( ANIMATION_PATH ( \"../data/c1/zrdr/native-definitions\" ) ANIMATION_PATH ( \"../data/c1/zrdr/envmodels\" ) ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( ground.zad ) ) )\r\n");
        await job("source_world_open", new() { ["mission"] = "c1", ["sourceMission"] = "data/c1/zrdr/aipath/m1" }, "completed");
        var document = main.ViewModel.SelectedDocument!;
        var source = Assert.IsType<SourceSceneSnapshot>(document.Document);
        Assert.Equal(27u, source.Probe.Version); Assert.True(source.Animations != null, string.Join("; ", source.Notes)); Assert.Equal(39u, source.Animations.Version);
        var initialized = source.MissionScene(MissionDifficulty.Medium, token);
        Assert.True(initialized.Actors.Count == 1, string.Join("; ", initialized.Diagnostics));
        var actor = initialized.Actors[0];
        Assert.Equal(model, actor.SourceModel);
        var state = await call("state", new());
        Assert.NotNull(state["sourceCapabilities"]);
        string preview = state["preview"]!.GetValue<string>();
        var tree = await call("scene_tree", new() { ["document"] = document.SessionId });
        int bound = initialized.AnimationEntryRoots.First(p => p.Key > 0).Value;
        var uses = await call("scene_tree", new() { ["document"] = document.SessionId, ["preview"] = preview, ["context"] = tree["context"]!.GetValue<string>(), ["action"] = "animationUses", ["node"] = bound });
        Assert.True(uses["total"]!.GetValue<int>() > 0);
        preview = (await call("state", new()))["preview"]!.GetValue<string>();
        await call("camera", new() { ["preview"] = preview, ["action"] = "frame", ["node"] = bound });
        var missions = await job("missions", new() { ["preview"] = preview }, "completed");
        Assert.Equal("data/c1/zrdr/aipath/m1", missions["selected"]!.GetValue<string>());
        var checkout = await job("source_blender_checkout", new() { ["document"] = document.SessionId, ["node"] = actor.Root }, "completed");
        Assert.Equal(model, checkout["model"]!.GetValue<string>());
        var refused = await job("source_group_add", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["name"] = "fixture_01" }, "failed");
        Assert.Equal("invalid_argument", refused["code"]!.GetValue<string>());
        Assert.False(main.ViewModel.SelectedDocument!.IsDirty);
        document = main.ViewModel.SelectedDocument!;
        preview = (await call("state", new()))["preview"]!.GetValue<string>();
        var simulation = await job("ai_valve_preview", new() { ["preview"] = preview, ["action"] = "prepare" }, "completed");
        string simulationId = simulation["simulation"]!.GetValue<string>();
        simulation = await job("ai_valve_preview", new() { ["preview"] = preview, ["simulation"] = simulationId, ["action"] = "set", ["name"] = "go", ["value"] = 1 }, "completed");
        Assert.Equal(1, simulation["timerCount"]!.GetValue<int>());
        Assert.False(document.IsDirty);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var viewport = (zStudio.Rendering.SceneViewport)typeof(MainWindow).GetField("scene", hidden)!.GetValue(main)!;
        var aiNetwork = viewport.AiNetworks.Networks.First(); var ai = aiNetwork.Nodes[0];
        var aiTree = (SceneTreeModel)typeof(MainWindow).GetField("sceneTree", hidden)!.GetValue(main)!;
        typeof(MainWindow).GetMethod("SelectSectionRow", hidden)!.Invoke(main, [aiTree.FindSection("network:" + aiNetwork.Id)!, false]);
        await job("ai_valve_preview", new() { ["preview"] = preview, ["simulation"] = simulationId, ["action"] = "set", ["name"] = "go", ["value"] = 0 }, "completed");
        await job("ai_valve_preview", new() { ["preview"] = preview, ["simulation"] = simulationId, ["action"] = "traverse", ["node"] = ai.Id, ["slot"] = 0 }, "failed");
        var preserved = await job("ai_valve_preview", new() { ["preview"] = preview, ["action"] = "state" }, "completed");
        Assert.Equal(simulationId, preserved["simulation"]!.GetValue<string>());
        await job("ai_valve_preview", new() { ["preview"] = preview, ["simulation"] = simulationId, ["action"] = "set", ["name"] = "go", ["value"] = 1 }, "completed");
        simulation = await job("ai_valve_preview", new() { ["preview"] = preview, ["simulation"] = simulationId, ["action"] = "traverse", ["node"] = ai.Id, ["slot"] = 0 }, "completed");
        Assert.True(viewport.SelectAiNode(ai.Id));
        var inspection = Assert.IsType<zStudio.Rendering.SceneInspection>(viewport.SelectedInspection);
        var extra = typeof(MainWindow).GetMethod("ValveInspectionSections", hidden)!;
        var controls = (System.Windows.FrameworkElement)extra.Invoke(main, [viewport, inspection])!;
        // The node's Add fields keep what is typed while the test steps: only its Test part follows the test.
        var typed = VisualChildren(controls).OfType<System.Windows.Controls.TextBox>().First();
        typed.Text = "7";
        await job("ai_valve_preview", new() { ["preview"] = preview, ["simulation"] = simulationId, ["action"] = "step" }, "completed");
        Assert.Same(controls, extra.Invoke(main, [viewport, inspection]));
        Assert.Equal("7", typed.Text);
        Assert.Contains(VisualChildren(controls).OfType<System.Windows.Controls.Button>(), b => b.Content as string == "Drive through" && b.IsEnabled);
        // The Valves rows show the test's values; next runs the waiting timers.
        var goRow = aiTree.FindSection("valve:go")!;
        Assert.Equal("1", goRow.Meta); Assert.True(goRow.ValveIsOn);
        Assert.True(aiTree.Roots.Single(r => r.HasValveTest).ValveTestOn);
        simulation = await job("ai_valve_preview", new() { ["preview"] = preview, ["simulation"] = simulationId, ["action"] = "next" }, "completed");
        Assert.Equal(0, simulation["timerCount"]!.GetValue<int>());
        Assert.Equal("1", aiTree.FindSection("valve:later")!.Meta);
        // The valve card while the test runs: selecting the valve shows its Test section (the retained one, moved to the card
        // as shown now), and what is typed in it stays through the test's refreshes.
        var selectRow = typeof(MainWindow).GetMethod("SelectSectionRow", hidden)!;
        selectRow.Invoke(main, [goRow, false]);
        var testCard = (SourceSectionCard)typeof(MainWindow).GetField("sectionCard", hidden)!.GetValue(main)!;
        System.Windows.Controls.TextBox Other() => VisualChildren(testCard).OfType<System.Windows.Controls.TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Other value");
        Other().Text = "17";
        selectRow.Invoke(main, [goRow, false]);
        await job("ai_valve_preview", new() { ["preview"] = preview, ["simulation"] = simulationId, ["action"] = "step" }, "completed");
        Assert.Equal("17", Other().Text);
        simulation = await job("ai_valve_preview", new() { ["preview"] = preview, ["simulation"] = simulationId, ["action"] = "reset" }, "completed");
        Assert.NotEqual(simulationId, simulation["simulation"]!.GetValue<string>()); Assert.Equal(0, simulation["timerCount"]!.GetValue<int>());
        simulationId = simulation["simulation"]!.GetValue<string>();
        // Stop must retire attachment and lifetime even while an evaluator owns the busy flag.
        var lifetime = (CancellationTokenSource)typeof(MainWindow).GetField("valveLifetime", hidden)!.GetValue(main)!;
        var lifetimeToken = lifetime.Token;
        typeof(MainWindow).GetField("valveBusy", hidden)!.SetValue(main, true);
        try { typeof(MainWindow).GetMethod("StopValveSimulation", hidden)!.Invoke(main, null); }
        finally { typeof(MainWindow).GetField("valveBusy", hidden)!.SetValue(main, false); }
        Assert.True(lifetimeToken.IsCancellationRequested);
        Assert.Null(typeof(MainWindow).GetField("valveSimulation", hidden)!.GetValue(main));
        Assert.Null(typeof(MainWindow).GetField("valveAttachment", hidden)!.GetValue(main));
        simulation = await job("ai_valve_preview", new() { ["preview"] = preview, ["action"] = "start" }, "completed");
        simulationId = simulation["simulation"]!.GetValue<string>();
        await job("ai_valve_preview", new() { ["preview"] = preview, ["simulation"] = simulationId, ["action"] = "stop" }, "completed");
        Assert.Equal("", goRow.Meta);
        string valveSource = ((SourceSceneSnapshot)document.Document).SourceMission + "/valves.zrd";
        string Stored(string path) => System.Text.Encoding.Latin1.GetString(main.ViewModel.SelectedDocument!.SourceWorld!.Workspace.Read(path, token)!);
        // The mission's valves by name, and one read as its card shows it.
        var valves = (await job("source_ai_valve", new() { ["document"] = document.SessionId, ["action"] = "valves" }, "completed"))["items"]!.AsArray();
        Assert.Equal(["go", "later"], valves.Select(v => v!["name"]!.GetValue<string>()));
        var read = (await job("source_ai_valve", new() { ["document"] = document.SessionId, ["action"] = "valve", ["name"] = "go" }, "completed"))["valve"]!;
        Assert.Equal("sets later after 1 s", read["reactions"]!.AsArray().Single(r => r!["fields"]!["kind"]!.GetValue<string>() == "delayupdate")!["text"]!.GetValue<string>());
        Assert.Contains(read["usedBy"]!.AsArray(), l => l!["role"]!.GetValue<string>() == "gate" && l["node"]!.GetValue<int>() == 0);
        // set changes the delay in place: one undoable change that keeps the rest of the record.
        await job("source_ai_valve", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "set", ["name"] = "go",
            ["changes"] = new { reactions = new[] { new { occurrence = 0, storedKind = "delayupdate", fields = new { kind = "delayupdate", target = "later", operand = "2.5" } } } } }, "completed");
        document = main.ViewModel.SelectedDocument!;
        Assert.True(document.IsDirty);
        Assert.Equal("( go ( shutdown ( fixture_01 0 1 ) delayupdate ( later 2.5 ) ) )\r\n", Stored(valveSource));
        await call("undo_redo", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "undo" });
        Assert.False(main.ViewModel.SelectedDocument!.IsDirty);
        document = main.ViewModel.SelectedDocument!;
        // The valve's card: its draft through MCP, then Apply; a draft change is refused while Apply prepares.
        var worldTree = (SceneTreeModel)typeof(MainWindow).GetField("sceneTree", hidden)!.GetValue(main)!;
        var valveRow = worldTree.FindSection("valve:go")!;
        Assert.True(valveRow.Selectable);
        typeof(MainWindow).GetMethod("SelectSectionRow", hidden)!.Invoke(main, [valveRow, false]);
        var valveCard = (SourceSectionCard)typeof(MainWindow).GetField("sectionCard", hidden)!.GetValue(main)!;
        Assert.Equal("go", valveCard.ShownValve!.Name); Assert.False(valveCard.HasDraft);
        await job("source_ai_valve", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "draft", ["name"] = "go",
            ["changes"] = new { initial = "1", reactions = new[] { new { fields = new { kind = "sound_once", target = "alarm", when = "1" } } } } }, "completed");
        Assert.True(valveCard.HasDraft);
        // Framing another valve selects it, which would replace this card: with its draft pending the selection is refused.
        var refusal = Assert.IsType<zStudio.Automation.StudioCommandException>(Assert.Throws<TargetInvocationException>(() =>
            typeof(MainWindow).GetMethod("RequireNoSelectionDrafts", hidden)!.Invoke(main, [document])).InnerException);
        Assert.Equal("pending_drafts", refusal.Code);
        {
            TaskCompletionSource valveDraftEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim valveDraftRelease = new(false);
            main.SourceEditPreparing = ct => { valveDraftEntered.TrySetResult(); Assert.True(valveDraftRelease.Wait(TimeSpan.FromSeconds(15), ct)); };
            var valveApply = valveCard.ApplyAsync();
            try
            {
                await valveDraftEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                document = main.ViewModel.SelectedDocument!;
                var duringApply = await job("source_ai_valve", new() { ["document"] = document.SessionId, ["revision"] = document.Revision,
                    ["action"] = "draft", ["name"] = "go", ["changes"] = new { initial = "5" }, ["draft"] = valveCard.DraftToken }, "failed");
                Assert.Equal("busy", duringApply["code"]!.GetValue<string>());
            }
            finally { valveDraftRelease.Set(); main.SourceEditPreparing = null; }
            await valveApply;
        }
        document = main.ViewModel.SelectedDocument!;
        Assert.Contains("value ( 1 )", Stored(valveSource)); Assert.Contains("sound_once ( alarm 1 )", Stored(valveSource)); Assert.DoesNotContain("( 5 )", Stored(valveSource));
        await call("undo_redo", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "undo" });
        Assert.False(main.ViewModel.SelectedDocument!.IsDirty);
        // rename follows every file of the mission that names the valve, as one change.
        document = main.ViewModel.SelectedDocument!;
        await job("source_ai_valve", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "rename", ["name"] = "go", ["newName"] = "open_gate" }, "completed");
        document = main.ViewModel.SelectedDocument!;
        Assert.StartsWith("( open_gate ( shutdown", Stored(valveSource)); Assert.Contains("valve (1 open_gate 1)", Stored("data/c1/zrdr/aipath/m1/net_01.zrd"));
        await call("undo_redo", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "undo" });
        Assert.False(main.ViewModel.SelectedDocument!.IsDirty);
        document = main.ViewModel.SelectedDocument!;
        source = Assert.IsType<SourceSceneSnapshot>(document.Document);
        preview = (await call("state", new()))["preview"]!.GetValue<string>();
        var placedRefusal = await job("source_world_add_model", new() { ["document"] = document.SessionId, ["revision"] = document.Revision,
            ["model"] = model, ["name"] = "fixture_01", ["position"] = new { x = 5, y = 0, z = 0 }, ["definitionFiles"] = Array.Empty<string>() }, "failed");
        Assert.True(placedRefusal["code"]!.GetValue<string>() == "invalid_argument", placedRefusal.ToJsonString());
        Assert.False(main.ViewModel.SelectedDocument!.IsDirty);
        await job("ai_valve_preview", new() { ["preview"] = preview, ["action"] = "prepare" }, "completed");
        await job("animation_mode", new() { ["preview"] = preview, ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "open", ["entry"] = 1 }, "completed");
        Assert.Null(typeof(MainWindow).GetField("valveSimulation", hidden)!.GetValue(main));
        preview = (await call("state", new()))["preview"]!.GetValue<string>();
        var rangeState = await job("animation_paths", new() { ["preview"] = preview, ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "ranges" }, "completed");
        var gate = rangeState["ranges"]!["ranges"]!.AsArray().Single(r => r!["kind"]!.GetValue<string>() == "gate")!;
        var center = gate["center"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
        foreach (double distance in new[] { 100d, 450d, 800d })
        {
            await job("animation_scenario", new() { ["preview"] = preview, ["action"] = "inputs", ["player"] = new { kind = "custom", x = center[0] + distance, y = center[1], z = center[2] } }, "completed");
            rangeState = await job("animation_paths", new() { ["preview"] = preview, ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "ranges" }, "completed");
            gate = rangeState["ranges"]!["ranges"]!.AsArray().Single(r => r!["kind"]!.GetValue<string>() == "gate")!;
            Assert.Equal(450, gate["minimumRadius"]!.GetValue<double>()); Assert.Equal(800, gate["radius"]!.GetValue<double>());
            Assert.True(gate["outerExclusive"]!.GetValue<bool>()); Assert.Equal(distance == 450, gate["inside"]!.GetValue<bool>());
        }
        await job("animation_scenario", new() { ["preview"] = preview, ["action"] = "inputs", ["player"] = new { kind = "atStart" } }, "completed");
        var records = (await job("source_animation", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["definition"] = 0, ["action"] = "read" }, "completed"))["items"]!.AsArray();
        var motion = records.Single(r => r!["Kind"]!.GetValue<string>() == "OBJECT_MOTION_SI_SCRIPT")!;
        var context = await job("source_animation", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["definition"] = 0, ["action"] = "context", ["record"] = motion["Id"]!.GetValue<string>() }, "completed");
        Assert.Equal(nestedDefinition, context["Definition"]!["File"]!.GetValue<string>());
        Assert.EndsWith("envmodels/ground.zan", context["Script"]!["File"]!.GetValue<string>());
        string activation = records[0]!["Fields"]!.AsArray().Single(f => f!["Key"]!.GetValue<string>() == "ACTIVATION")!["Id"]!.GetValue<string>();
        var draft = await job("source_animation", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["definition"] = 0, ["action"] = "set",
            ["changes"] = new[] { new { field = activation, values = new[] { "ON_STARTUP" } } } }, "completed");
        await job("source_animation", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["definition"] = 0, ["action"] = "apply", ["token"] = draft["draft"]!["token"]!.GetValue<string>() }, "completed");
        document = main.ViewModel.SelectedDocument!;
        Assert.Contains("ON_STARTUP", System.Text.Encoding.Latin1.GetString(document.SourceWorld!.Workspace.Read(nestedDefinition, token)!));
        await call("undo_redo", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "undo" });
        document = main.ViewModel.SelectedDocument!;
        Assert.False(document.IsDirty);
        preview = (await call("state", new()))["preview"]!.GetValue<string>();
        await job("animation_transport", new() { ["preview"] = preview, ["action"] = "seek", ["seconds"] = .05 }, "completed");
        double playhead = (await call("state", new()))["animationTime"]!.GetValue<double>();
        await job("missions", new() { ["preview"] = preview, ["sourceMission"] = "data/c1/zrdr/aipath/mp1" }, "completed");
        var switched = await call("state", new());
        Assert.Equal(playhead, switched["animationTime"]!.GetValue<double>(), 6);
        preview = switched["preview"]!.GetValue<string>();
        await job("animation_options", new() { ["preview"] = preview, ["changes"] = new { replay = true } }, "completed");
        await job("animation_transport", new() { ["preview"] = preview, ["action"] = "play" }, "completed");
        var preparing = main.PreparingSourceWorldModel;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new(false);
        main.PreparingSourceWorldModel = ct => { entered.TrySetResult(); release.Wait(ct); preparing?.Invoke(ct); };
        try
        {
            var pending = await call("missions", new() { ["preview"] = preview, ["sourceMission"] = "data/c1/zrdr/aipath/m1" });
            await entered.Task.WaitAsync(token);
            // MCP jobs share a serial queue; a user can still press the GUI transport
            // while a mission job is rebuilding. Invoke that same shared handler.
            await (Task)typeof(MainWindow).GetMethod("WorldAnimationTransportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, ["pause", 0d, token, false])!;
            // A rejected GUI option refresh restores source choices without entering the compiled-reader resolver.
            var publishedField = typeof(MainWindow).GetField("publishedStaticOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var publishedOptions = publishedField.GetValue(main);
            Assert.NotNull(publishedOptions);
            typeof(MainWindow).GetMethod("RestoreStaticSceneOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, [publishedOptions]);
            release.Set();
            string operation = pending["id"]!.GetValue<string>();
            while (pending["State"]!.GetValue<string>() is "queued" or "running")
            { await Task.Delay(10, token); pending = await call("operation", new() { ["id"] = operation }); }
            Assert.Equal("completed", pending["State"]!.GetValue<string>());
            switched = await call("state", new());
            Assert.False(switched["animationPlaying"]!.GetValue<bool>());
        }
        finally { release.Set(); main.PreparingSourceWorldModel = preparing; }
        await job("missions", new() { ["preview"] = switched["preview"]!.GetValue<string>(), ["sourceMission"] = "data/c1/zrdr/aipath/mp1" }, "completed");
        switched = await call("state", new());
        await job("animation_mode", new() { ["preview"] = switched["preview"]!.GetValue<string>(), ["action"] = "exit" }, "completed");
        document = main.ViewModel.SelectedDocument!;
        source = Assert.IsType<SourceSceneSnapshot>(document.Document);
        Assert.Equal("data/c1/zrdr/aipath/mp1", source.SourceMission);
        Assert.Equal(7, Assert.Single(source.MissionScene(MissionDifficulty.Medium, token).Actors).PlacementPosition!.Value.X);
        string firstValveSource = source.SourceMission + "/valves.zrd";
        var valveSources = (await job("source_ai_valve", new() { ["document"] = document.SessionId, ["action"] = "sources" }, "completed"))["items"]!.AsArray();
        Assert.Contains(valveSources, v => v!["source"]!.GetValue<string>() == firstValveSource && !v["exists"]!.GetValue<bool>());
        // A mission without valves.zrd gets the file with its first valve.
        await job("source_ai_valve", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "new", ["name"] = "first_valve" }, "completed");
        document = main.ViewModel.SelectedDocument!;
        Assert.Contains("first_valve", System.Text.Encoding.Latin1.GetString(document.SourceWorld!.Workspace.Read(firstValveSource, token)!));
        await call("undo_redo", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "undo" });
        document = main.ViewModel.SelectedDocument!; Assert.False(document.IsDirty);
        Assert.Null(document.SourceWorld!.Workspace.Read(firstValveSource, token));
        const string network = "data/c1/zrdr/aipath/mp1/net_01.zrd";
        await job("source_ai_network", new()
        {
            ["document"] = document.SessionId, ["revision"] = document.Revision, ["network"] = network,
            ["links"] = new[] { new { node = 0, targets = Array.Empty<int>() } }
        }, "completed");
        document = main.ViewModel.SelectedDocument!;
        Assert.True(document.IsDirty);
        Assert.Contains("wait_interval ( 1 2.0 )", System.Text.Encoding.Latin1.GetString(document.SourceWorld!.Workspace.Read(network, token)!));
        await call("undo_redo", new() { ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "undo" });
        Assert.False(main.ViewModel.SelectedDocument!.IsDirty);
    }
    private static IEnumerable<System.Windows.DependencyObject> VisualChildren(System.Windows.DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in VisualChildren(child)) yield return nested;
        }
    }
}
