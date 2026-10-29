using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core.Sources;
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>
/// The Structure view's MCP path (animation_graph) and its GUI twin on a small destroy_tr_bridge: the graph with its back-link
/// and its release into mainsplash, a forced IF and its outcome, an ELSEIF switch, an expanded reference node, a range drawing,
/// and a call port toggled through UI Automation landing in the same scenario.
/// </summary>
internal static class AnimationGraphMcpChecks
{
    private const string Definitions = """
        ANIMATION_DEFINITIONS ( ANIMATION_LIST (
          ANIMATION_DEFINITION (
            NAME ( ground ) ANIMATION_NAME ( destroy_tr_bridge ) ACTIVATION ( ON_CALL ) RESET_TIME ( -1 )
            SEQUENCE_DEFINITION ( NAME ( healthy_to_destroyed ) CALL_ANIMATION ( NAME ( rdrail_destroy01 ) ) )
            SEQUENCE_DEFINITION ( NAME ( flare_sequence ) IF ( PLAYER_RANGE ( 64 ) ) ELSE () OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( ACTIVE ) ) ENDIF () )
            SEQUENCE_DEFINITION ( NAME ( chance ) IF ( RANDOM_WEIGHT ( 0.4 ) ) CALLBACK ( VALUE ( 14 ) ) ELSEIF ( ANIMATION_LOD ( MEDIUM ) ) CALLBACK ( VALUE ( 15 ) ) ENDIF () )
            SEQUENCE_DEFINITION ( NAME ( move_piece3 ) CALL_SEQUENCE ( NAME ( mainsplash ) ) )
            SEQUENCE_DEFINITION ( NAME ( mainsplash ) ACTIVATION ( ON_CALL ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( ACTIVE ) ) LOOP ( LOOP_COUNT ( 4 ) START_TIME ( EVENT_OFFSET 0.1 ) ) ) )
          ANIMATION_DEFINITION (
            NAME ( ground ) ANIMATION_NAME ( rdrail_destroy01 ) ACTIVATION ( ON_CALL ) RESET_TIME ( -1 )
            SEQUENCE_DEFINITION ( NAME ( fall ) CALL_ANIMATION ( NAME ( destroy_tr_bridge ) ) ) )
        ) )
        """;

    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        fixture.Write(VtolDropFixture.Root, Definitions);
        // m3's player tank, with a drive-in that suspends the player (callback 11) and then hands it the tank's pose (callback 10).
        fixture.WritePlayerTank();
        fixture.Write("data/m3/zrdr/bftm3.zad", """
            (
              ANIMATION_DEFINITIONS (
                ANIMATION_LIST (
                  ANIMATION_DEFINITION (
                    NAME ( bft_00 ) ANIMATION_NAME ( drive_in ) ACTIVATION ( ON_CALL ) RESET_TIME ( -1.0 )
                    SEQUENCE_DEFINITION ( NAME ( drive ) CALLBACK ( VALUE ( 11 ) ) CALLBACK ( VALUE ( 10 ) START_TIME ( ANIMATION_OFFSET 1 ) ) )
                  )
                  ANIMATION_DEFINITION (
                    NAME ( ground ) ANIMATION_NAME ( m3_start_animation ) ACTIVATION ( ON_CALL ) RESET_TIME ( 0 -1 )
                    RESET_STATE ( CALLBACK ( VALUE ( 15 ) ) CALLBACK ( VALUE ( 10 ) ) )
                    SEQUENCE_DEFINITION ( NAME ( hold ) CALLBACK ( VALUE ( 14 ) ) )
                  )
                )
              )
            )
            """);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false, Width = 1600, Height = 1000 }; main.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            await Job("source_world_open", new() { ["mission"] = "m1" });
            await ((Task)typeof(MainWindow).GetField("previewWork", flags)!.GetValue(main)!).WaitAsync(token);
            var doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            var snapshot = (SourceSceneSnapshot)doc.Document;
            int bridge = snapshot.AnimationDefinitions.Definitions.Single(d => d.Name == "destroy_tr_bridge").Index;
            int entry = snapshot.Animations!.Entries.First(e => e.Name == "destroy_tr_bridge").Index;
            await Job("animation_mode", new() { ["action"] = "open", ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["definition"] = bridge, ["preview"] = await Preview() });
            await Job("animation_mode", new() { ["action"] = "layout", ["view"] = "structure", ["preview"] = await Preview() });

            // The graph: a back-link from the rails to the opened activation, a release into the waiting mainsplash.
            var nodes = (await Graph("read"))["result"]!["items"]!.AsArray().OfType<JsonObject>().ToArray();
            string Node(string label) => nodes.First(n => n["label"]!.GetValue<string>().StartsWith(label, StringComparison.Ordinal))["id"]!.GetValue<string>();
            string rails = nodes.Single(n => n["kind"]!.GetValue<string>() == "reference")["id"]!.GetValue<string>();
            var edges = (await Graph("read", new() { ["section"] = "edges" }))["result"]!["items"]!.AsArray().OfType<JsonObject>().ToArray();
            Assert.Contains(edges, e => e["backLink"]!.GetValue<bool>() && e["from"]!.GetValue<string>() == rails && e["to"]!.GetValue<string>() == $"activation:{entry}");
            Assert.Contains(edges, e => e["relation"]!.GetValue<string>() == "release" && e["to"]!.GetValue<string>() == Node("mainsplash") && e["route"]!["end"]!.GetValue<string>() == "openArrow");

            // The flare_sequence switch forced True, its outcome read back at the playhead.
            var flare = await Rows(Node("flare_sequence"));
            var question = flare.First(r => r["role"]!.GetValue<string>() == "question");
            await Job("animation_scenario", new() { ["action"] = "conditions", ["preview"] = await Preview(), ["conditions"] = new[] { new { instancePath = "", entry, @event = question["event"]!.GetValue<string>(), value = "true" } } });
            await Job("animation_transport", new() { ["action"] = "seek", ["seconds"] = 0.5, ["preview"] = await Preview() });
            question = (await Rows(Node("flare_sequence"))).First(r => r["id"]!.GetValue<string>() == question["id"]!.GetValue<string>());
            Assert.Equal("true", question["switch"]!.GetValue<string>());
            Assert.StartsWith("true · forced", question["condition"]!["outcome"]!.GetValue<string>());
            // A switch on a row that is no IF or ELSEIF is refused.
            var refused = await client.CallToolAsync("zstudio_animation_scenario", new Dictionary<string, object?> { ["action"] = "conditions", ["preview"] = await Preview(),
                ["conditions"] = new[] { new { instancePath = "", entry, @event = flare.First(r => r["role"]!.GetValue<string>() == "event")["event"]!.GetValue<string>(), value = "true" } } }, cancellationToken: token);
            var refusedJob = JsonNode.Parse(refused.Content.OfType<TextContentBlock>().Single().Text)!;
            Assert.Equal("invalid_argument", (await AwaitJob(refusedJob, "failed"))["code"]!.GetValue<string>());

            // An ELSEIF has its own switch.
            var chance = await Rows(Node("chance"));
            var elseIf = chance.First(r => r["type"]!.GetValue<int>() == 33);
            await Job("animation_scenario", new() { ["action"] = "conditions", ["preview"] = await Preview(), ["conditions"] = new[] { new { instancePath = "", entry, @event = elseIf["event"]!.GetValue<string>(), value = "false" } } });
            Assert.Equal("false", (await Rows(Node("chance"))).First(r => r["type"]!.GetValue<int>() == 33)["switch"]!.GetValue<string>());

            // Selecting the PLAYER_RANGE question draws its range; expanding the rails shows their frame (presentation only).
            var selected = (await Graph("select", new() { ["row"] = question["id"]!.GetValue<string>() }))["result"]!;
            Assert.Equal(64, selected["rangeDrawing"]!["radius"]!.GetValue<double>(), 3);
            // animation_paths ranges: the flare range test around its attach node, measured from the player (here far away).
            await Job("animation_scenario", new() { ["action"] = "inputs", ["preview"] = await Preview(), ["player"] = new { kind = "custom", x = 4000.0, y = 0.0, z = 4000.0 } });
            var ranges = await Paths("ranges");
            var flareRange = ranges["ranges"]!["ranges"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == question["id"]!.GetValue<string>())!;
            Assert.Equal("condition", flareRange["kind"]!.GetValue<string>());
            Assert.Equal(64, flareRange["radius"]!.GetValue<double>(), 3);
            Assert.False(flareRange["inside"]!.GetValue<bool>());
            Assert.Equal("false", flareRange["outcome"]!.GetValue<string>());
            Assert.True(flareRange["distance"]!.GetValue<double>() > 64);
            Assert.True(flareRange["drawn"]!.GetValue<bool>());
            Assert.Equal("custom", ranges["ranges"]!["player"]!["source"]!.GetValue<string>());
            // Show range tests (the Paths chip's toggle) draws every range test, selected or not.
            Assert.True((await Paths("overlay", new() { ["rangeTests"] = true }))["paths"]!["rangeTests"]!.GetValue<bool>());
            await Paths("overlay", new() { ["rangeTests"] = false });
            await Job("animation_scenario", new() { ["action"] = "inputs", ["preview"] = await Preview(), ["player"] = new { kind = "atStart" } });
            int included = (await Job("animation_scenario", new() { ["action"] = "read", ["preview"] = await Preview() }))["scenario"]!["calls"]!["included"]!.GetValue<int>();
            await Graph("expand", new() { ["node"] = rails });
            nodes = (await Graph("read"))["result"]!["items"]!.AsArray().OfType<JsonObject>().ToArray();
            Assert.True(nodes.Single(n => n["id"]!.GetValue<string>() == rails)["reference"]!["expanded"]!.GetValue<bool>());
            Assert.Contains(nodes, n => n["kind"]!.GetValue<string>() == "frame" && n["parent"]?.GetValue<string>() == rails);
            Assert.Equal(included, (await Job("animation_scenario", new() { ["action"] = "read", ["preview"] = await Preview() }))["scenario"]!["calls"]!["included"]!.GetValue<int>());

            // UI Automation reaches the nodes, their rows, switches and ports from the view's own peer; the GUI's call port is the
            // same include operation: toggled through UI Automation, the scenario includes the call.
            main.UpdateLayout();
            var view = Descendants(main).OfType<AnimationGraphView>().Single();
            var peers = Walk(UIElementAutomationPeer.CreatePeerForElement(view)!).ToArray();
            Assert.Contains(peers, p => p.GetClassName() == "AnimationGraphNode" && p.GetName().StartsWith("Sequence flare_sequence", StringComparison.Ordinal));
            Assert.Contains(peers, p => p.GetClassName() == "AnimationGraphBand" && p.GetName().Contains("of flare_sequence", StringComparison.Ordinal));
            Assert.Contains(peers, p => p.GetAutomationControlType() == AutomationControlType.RadioButton && p.GetName() == "True");
            var portPeer = peers.First(p => p is UIElementAutomationPeer { Owner: AnimationGraphPort candidate } && candidate.Edge.From.StartsWith("row:", StringComparison.Ordinal) && !candidate.Edge.BackLink);
            var port = (AnimationGraphPort)((UIElementAutomationPeer)portPeer).Owner;
            Assert.Equal(false, port.IsChecked);
            ((IToggleProvider)portPeer.GetPattern(PatternInterface.Toggle)!).Toggle();
            await Task.Delay(200, token);
            await ((Task)typeof(MainWindow).GetField("worldAnimationReevaluation", flags)!.GetValue(main)!).WaitAsync(token);
            var scenario = (await Job("animation_scenario", new() { ["action"] = "read", ["preview"] = await Preview() }))["scenario"]!;
            Assert.True(scenario["calls"]!["included"]!.GetValue<int>() > included);

            // The player reference over time: callback 11 freezes it, callback 10 moves it to the tank's root at 1 s.
            await Job("source_world_open", new() { ["mission"] = "m3" });
            await ((Task)typeof(MainWindow).GetField("previewWork", flags)!.GetValue(main)!).WaitAsync(token);
            var tankMap = main.ViewModel.Documents.Single(d => d.SourceWorld?.Mission == "m3");
            int drive = ((SourceSceneSnapshot)tankMap.Document).AnimationDefinitions.Definitions.Single(d => d.Name == "drive_in").Index;
            await Job("animation_mode", new() { ["action"] = "open", ["document"] = tankMap.SessionId.ToString(), ["revision"] = tankMap.Revision, ["definition"] = drive, ["preview"] = await Preview() });
            await Job("animation_transport", new() { ["action"] = "seek", ["seconds"] = 0.5, ["preview"] = await Preview() });
            var frozen = await Job("animation_runtime", new() { ["section"] = "player", ["preview"] = await Preview() });
            Assert.True(frozen["player"]!["frozen"]!.GetValue<bool>(), frozen.ToJsonString());
            Assert.Contains(frozen["player"]!["source"]!.GetValue<string>(), new[] { "aiv start", "opening handover" });
            await Job("animation_transport", new() { ["action"] = "seek", ["seconds"] = 1.5, ["preview"] = await Preview() });
            var moved = await Job("animation_runtime", new() { ["section"] = "player", ["preview"] = await Preview() });
            Assert.Equal("callback 10", moved["player"]!["source"]!.GetValue<string>());
            Assert.False(moved["player"]!["frozen"]!.GetValue<bool>());
            Assert.InRange(moved["player"]!["since"]!.GetValue<double>(), .99, 1.05);
            Assert.True(moved["changes"]!["total"]!.GetValue<int>() >= 3);
            Assert.StartsWith("callback 10 at 1.0", moved["label"]!.GetValue<string>());

            // The compiled preview (design §19, §20): the same bridge exported, opened read only, shown with Animation mode's components.
            string compiled = Path.Combine(fixture.Root, "compiled-preview");
            var exported = await SourceBuilder.ExportAsync(fixture.Project, compiled, ["m1/gamez.zbd", "m1/anim.zbd"], token: token);
            Assert.All(exported.Outputs, output => Assert.Null(output.Error));
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);
            await main.ViewModel.OpenRootAsync(compiled, token);
            string archiveId = (await Job("open_document", new() { ["path"] = Path.Combine(compiled, "m1", "anim.zbd") }))["id"]!.GetValue<string>();
            var archive = main.ViewModel.Documents.Single(d => d.SessionId.ToString() == archiveId);
            int compiledBridge = archive.Assets.First(a => a.Record.Kind == zStudio.Core.AssetKind.Animation && a.Name == "destroy_tr_bridge").Index;
            await Job("select_asset", new() { ["document"] = archive.SessionId.ToString(), ["kind"] = "Animation", ["index"] = compiledBridge });
            var compiledState = await Job("animation_mode", new() { ["action"] = "state", ["preview"] = await Preview() });
            Assert.True(compiledState["readOnly"]!.GetValue<bool>());
            // Graph read: compiled numbering, the back-link from the rails.
            var compiledNodes = (await Graph("read"))["result"]!["items"]!.AsArray().OfType<JsonObject>().ToArray();
            var compiledFlare = compiledNodes.Single(n => n["label"]!.GetValue<string>() == "sequence 1 · flare_sequence");
            Assert.Contains((await Graph("read", new() { ["section"] = "edges" }))["result"]!["items"]!.AsArray().OfType<JsonObject>(), e => e["backLink"]!.GetValue<bool>());
            // The playback range (design §13): Auto is the measured run (it ends with mainsplash's LOOP at 0.1 s, which sends the
            // ON_CALL sequence back to waiting); Extend +5 s makes it Custom, 5 s longer (the flyout's, the ruler's and the hint's).
            var autoRange = await Job("animation_transport", new() { ["action"] = "auto", ["preview"] = await Preview() });
            Assert.Equal("auto", autoRange["rangeState"]!["mode"]!.GetValue<string>());
            double autoEnd = autoRange["rangeState"]!["auto"]!["seconds"]!.GetValue<double>();
            Assert.Equal("Finite", autoRange["rangeState"]!["auto"]!["kind"]!.GetValue<string>()); Assert.InRange(autoEnd, .099, .12);
            Assert.Equal(autoEnd, autoRange["range"]![1]!.GetValue<double>(), 6);
            var longer = await Job("animation_transport", new() { ["action"] = "extend", ["preview"] = await Preview() });
            Assert.Equal("custom", longer["rangeState"]!["mode"]!.GetValue<string>()); Assert.True(longer["rangeState"]!["extended"]!.GetValue<bool>());
            Assert.Equal(autoEnd + 5, longer["range"]![1]!.GetValue<double>(), 2); Assert.False(longer["playing"]!.GetValue<bool>());
            // With play (the transport hint's Extend +5 s) playback goes on; play goes with extend only.
            var playedOn = await Job("animation_transport", new() { ["action"] = "extend", ["play"] = true, ["preview"] = await Preview() });
            Assert.True(playedOn["playing"]!.GetValue<bool>()); Assert.Equal(autoEnd + 10, playedOn["range"]![1]!.GetValue<double>(), 2);
            await Job("animation_transport", new() { ["action"] = "pause", ["preview"] = await Preview() });
            Assert.Equal("invalid_argument", (await Job("animation_transport", new() { ["action"] = "pause", ["play"] = true, ["preview"] = await Preview() }, "failed"))["code"]!.GetValue<string>());
            // Timing marks after a seek inside the lengthened range: the run's marks on a lane.
            await Job("animation_transport", new() { ["action"] = "seek", ["seconds"] = 0.5, ["preview"] = await Preview() });
            var lanes = (await Job("animation_timing", new() { ["action"] = "lanes", ["preview"] = await Preview() }))["result"]!["items"]!.AsArray().OfType<JsonObject>().ToArray();
            string markedLane = lanes.First(l => l["kind"]!.GetValue<string>() == "sequence" && l["markCount"]!.GetValue<int>() > 0)["id"]!.GetValue<string>();
            Assert.NotEmpty((await Job("animation_timing", new() { ["action"] = "marks", ["lane"] = markedLane, ["preview"] = await Preview() }))["result"]!["items"]!.AsArray());
            // A condition override: the flare IF forced True in the shown animation's own path, read back on its switch and outcome.
            var compiledQuestion = (await Rows(compiledFlare["id"]!.GetValue<string>())).First(r => r["role"]!.GetValue<string>() == "question");
            await Job("animation_scenario", new() { ["action"] = "conditions", ["preview"] = await Preview(), ["conditions"] = new JsonArray(new JsonObject { ["instancePath"] = "", ["entry"] = compiledBridge, ["event"] = compiledQuestion["event"]!.GetValue<string>(), ["value"] = "true" }) });
            await Job("animation_transport", new() { ["action"] = "seek", ["seconds"] = 0.5, ["preview"] = await Preview() });
            var forcedQuestion = (await Rows(compiledFlare["id"]!.GetValue<string>())).First(r => r["role"]!.GetValue<string>() == "question");
            Assert.Equal("true", forcedQuestion["switch"]!.GetValue<string>());
            Assert.StartsWith("true · forced", forcedQuestion["condition"]!["outcome"]!.GetValue<string>());
            // Bound root: an override, then root null clears it (Clear override).
            var root = (await Job("animation_scenario", new() { ["action"] = "read", ["preview"] = await Preview() }))["scenario"]!["inputs"]!["root"]!;
            int boundNode = root["node"]!.GetValue<int>();
            var overridden = await Job("animation_scenario", new() { ["action"] = "inputs", ["root"] = boundNode == 0 ? 1 : 0, ["preview"] = await Preview() });
            Assert.True(overridden["scenario"]!["inputs"]!["root"]!["overridden"]!.GetValue<bool>());
            var cleared = await Job("animation_scenario", new() { ["action"] = "inputs", ["root"] = null, ["preview"] = await Preview() });
            Assert.False(cleared["scenario"]!["inputs"]!["root"]!["overridden"]!.GetValue<bool>());
            Assert.Equal(boundNode, cleared["scenario"]!["inputs"]!["root"]!["node"]!.GetValue<int>());
            // Called animations: every call runs by default; include excludes a path (with what lies below it), excludeAll runs
            // the animation alone and includeAll brings every call back (the GUI's switches and Include all / Exclude all).
            var calls = (await Job("animation_scenario", new() { ["action"] = "read", ["preview"] = await Preview() }))["scenario"]!;
            Assert.True(calls["allCalls"]!.GetValue<bool>());
            string firstCall = calls["calls"]!["groups"]![0]!["paths"]![0]!["path"]!.GetValue<string>();
            var withoutOne = (await Job("animation_scenario", new() { ["action"] = "include", ["paths"] = new JsonArray(firstCall), ["included"] = false, ["preview"] = await Preview() }))["scenario"]!;
            Assert.Contains(firstCall, withoutOne["excluded"]!.AsArray().Select(p => p!.GetValue<string>()));
            Assert.True(withoutOne["calls"]!["included"]!.GetValue<int>() < calls["calls"]!["included"]!.GetValue<int>());
            var alone = (await Job("animation_scenario", new() { ["action"] = "excludeAll", ["preview"] = await Preview() }))["scenario"]!;
            Assert.False(alone["allCalls"]!.GetValue<bool>()); Assert.Equal(0, alone["calls"]!["included"]!.GetValue<int>());
            var everyCall = (await Job("animation_scenario", new() { ["action"] = "includeAll", ["preview"] = await Preview() }))["scenario"]!;
            Assert.True(everyCall["allCalls"]!.GetValue<bool>()); Assert.Equal(0, everyCall["excludedCount"]!.GetValue<int>());
            Assert.Equal(calls["calls"]!["included"]!.GetValue<int>(), everyCall["calls"]!["included"]!.GetValue<int>());
            // Bytes: up to 4,096 original source bytes of the record.
            var bytes = await Call("animation_records", new() { ["document"] = archive.SessionId.ToString(), ["entry"] = compiledBridge, ["action"] = "bytes", ["length"] = 4096 });
            Assert.Equal("original source bytes", bytes["label"]!.GetValue<string>());
            Assert.Equal(Math.Min(4096, bytes["recordLength"]!.GetValue<long>()), bytes["length"]!.GetValue<long>());
            Assert.Equal(2 * bytes["length"]!.GetValue<long>(), bytes["hex"]!.GetValue<string>().Length);
            // A removed animation_options key is refused with a pointer to animation_scenario (no aliases).
            var movedOption = await Job("animation_options", new() { ["changes"] = new JsonObject { ["seed"] = 3 }, ["preview"] = await Preview() }, "failed");
            Assert.Equal("moved", movedOption["code"]!.GetValue<string>());
            Assert.Contains("animation_scenario", movedOption["message"]!.GetValue<string>());
            // Every key and value is checked before any applies: {grid, volume 3} is refused whole and the grid stays as it was.
            async Task<bool> Grid() => (await Call("preview_state", new() { ["preview"] = await Preview() }))["animation"]!["options"]!["Grid"]!.GetValue<bool>();
            bool grid = await Grid();
            var refusedWhole = await Job("animation_options", new() { ["changes"] = new JsonObject { ["grid"] = !grid, ["volume"] = 3.0 }, ["preview"] = await Preview() }, "failed");
            Assert.Equal("invalid_argument", refusedWhole["code"]!.GetValue<string>());
            Assert.Equal(grid, await Grid());
            // So is animation_graph viewport: a reveal of no node refuses the call and the run overlay stays on.
            await Job("animation_mode", new() { ["action"] = "layout", ["view"] = "structure", ["preview"] = await Preview() });
            var viewportRefused = await Job("animation_graph", new() { ["action"] = "viewport", ["runOverlay"] = false, ["reveal"] = "row:none", ["preview"] = await Preview() }, "failed");
            Assert.Equal("stale_record", viewportRefused["code"]!.GetValue<string>());
            Assert.True((await Graph("read"))["graph"]!["viewport"]!["runOverlay"]!.GetValue<bool>());

            async Task<JsonNode> Paths(string action, Dictionary<string, object?>? extra = null)
            {
                var args = extra ?? new(); args["action"] = action; args["preview"] = await Preview();
                var shown = main.ViewModel.Documents.Single(d => d.SourceWorld?.Mission == "m1");
                args["document"] = shown.SessionId.ToString(); args["revision"] = shown.Revision;
                return await Job("animation_paths", args);
            }
            async Task<JsonNode> Graph(string action, Dictionary<string, object?>? extra = null)
            {
                var args = extra ?? new(); args["action"] = action; args["preview"] = await Preview();
                return await Job("animation_graph", args);
            }
            async Task<JsonObject[]> Rows(string node) => (await Graph("read", new() { ["section"] = "rows", ["node"] = node }))["result"]!["items"]!.AsArray().OfType<JsonObject>().ToArray();
            async Task<string> Preview() => (await Call("state", new()))["preview"]!.GetValue<string>();
            async Task<JsonNode> Call(string name, Dictionary<string, object?> args)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.False(result.IsError == true, name + ": " + text); return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> args, string expected = "completed") => await AwaitJob(await Call(name, args), expected);
            async Task<JsonNode> AwaitJob(JsonNode job, string expected = "completed")
            {
                if (job["State"] == null) return job;
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);
            main.Close();
        }
    }
    private static IEnumerable<AutomationPeer> Walk(AutomationPeer peer)
    {
        foreach (var child in peer.GetChildren() ?? [])
        {
            yield return child;
            foreach (var descendant in Walk(child)) yield return descendant;
        }
    }
    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
