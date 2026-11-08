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
            File.WriteAllText(Path.Combine(folder, "net_01.zrd"), "( version ( 106 ) node_00 ( 12 ( 1.0 2.0 3.0 ) ( 1 ) wait_interval ( 1 2.0 ) ) node_01 ( 12 ( 4.0 5.0 6.0 ) ( 0 ) ) )\r\n");
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
        var placedRefusal = await job("source_world_add_model", new() { ["document"] = document.SessionId, ["revision"] = document.Revision,
            ["model"] = model, ["name"] = "fixture_01", ["position"] = new { x = 5, y = 0, z = 0 }, ["definitionFiles"] = Array.Empty<string>() }, "failed");
        Assert.True(placedRefusal["code"]!.GetValue<string>() == "invalid_argument", placedRefusal.ToJsonString());
        Assert.False(main.ViewModel.SelectedDocument!.IsDirty);
        await job("animation_mode", new() { ["preview"] = preview, ["document"] = document.SessionId, ["revision"] = document.Revision, ["action"] = "open", ["entry"] = 1 }, "completed");
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
}
