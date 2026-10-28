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
