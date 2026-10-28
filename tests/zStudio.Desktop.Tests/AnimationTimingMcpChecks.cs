using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core.Sources;
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>
/// The Timing view's MCP path (animation_timing) and its GUI twin on a small destroy_tr_bridge: lanes and marks (mainsplash
/// waiting for release, the release mark pointing at it), a step of one tick read back in seconds, a threshold proposed
/// through the source draft and cancelled, the Log paged, a start moved through the scenario (MCP and the Starts lane's
/// UI Automation), an occurrence selected and sought, and the seconds-only threshold editor.
/// </summary>
internal static class AnimationTimingMcpChecks
{
    private const string Definitions = """
        ANIMATION_DEFINITIONS ( ANIMATION_LIST (
          ANIMATION_DEFINITION (
            NAME ( ground ) ANIMATION_NAME ( destroy_tr_bridge ) ACTIVATION ( ON_CALL ) RESET_TIME ( -1 )
            SEQUENCE_DEFINITION ( NAME ( healthy_to_destroyed ) CALL_ANIMATION ( NAME ( rdrail_destroy01 ) ) )
            SEQUENCE_DEFINITION ( NAME ( flare_sequence ) IF ( PLAYER_RANGE ( 64 ) ) ELSE () OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( ACTIVE ) ) ENDIF () )
            SEQUENCE_DEFINITION ( NAME ( move_piece3 ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( ACTIVE ) START_TIME ( ANIMATION_OFFSET 0.5 ) ) CALL_SEQUENCE ( NAME ( mainsplash ) START_TIME ( EVENT_OFFSET 0.25 ) ) )
            SEQUENCE_DEFINITION ( NAME ( mainsplash ) ACTIVATION ( ON_CALL ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( ACTIVE ) ) LOOP ( LOOP_COUNT ( 2 ) START_TIME ( EVENT_OFFSET 0.1 ) ) ) )
          ANIMATION_DEFINITION (
            NAME ( ground ) ANIMATION_NAME ( rdrail_destroy01 ) ACTIVATION ( ON_CALL ) RESET_TIME ( -1 )
            SEQUENCE_DEFINITION ( NAME ( fall ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( ACTIVE ) START_TIME ( ANIMATION_OFFSET 0.2 ) ) ) )
        ) )
        """;

    internal static async Task Run()
    {
        ThresholdEditor();
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
            await Job("animation_mode", new() { ["action"] = "layout", ["view"] = "timing", ["preview"] = await Preview() });

            // Before anything runs: the empty state, the Starts lane with the primary start, the start's group and its lanes.
            var empty = await Timing("read");
            Assert.StartsWith("Nothing has run yet", empty["timing"]!["message"]!.GetValue<string>());
            await Job("animation_transport", new() { ["action"] = "seek", ["seconds"] = 2.0, ["preview"] = await Preview() });

            // Lanes and marks: mainsplash waits for release (released by move_piece3), the release mark points at its lane.
            var lanes = await Lanes();
            var starts = lanes.Single(l => l["kind"]!.GetValue<string>() == "starts");
            Assert.Equal(0, starts["starts"]![0]!["seconds"]!.GetValue<double>());
            string Lane(string name) => lanes.First(l => l["kind"]!.GetValue<string>() == "sequence" && l["label"]!.GetValue<string>() == name)["id"]!.GetValue<string>();
            string splash = Lane("mainsplash"), move = Lane("move_piece3");
            var splashMarks = await Marks(splash);
            var waiting = splashMarks.First(m => m["band"]!.GetValue<bool>() && m["kind"]!.GetValue<string>() == "waitingRelease");
            Assert.Contains("released by move_piece3", waiting["label"]!.GetValue<string>());
            Assert.Equal(0, waiting["start"]!.GetValue<double>(), 3);
            Assert.Contains(splashMarks, m => m["kind"]!.GetValue<string>() == "loop");
            var moveMarks = await Marks(move);
            var release = moveMarks.Single(m => m["kind"]!.GetValue<string>() == "release");
            Assert.Equal(splash, release["targetLane"]!.GetValue<string>());
            Assert.Equal("≥ 0.25 s after previous", release["bracket"]!["label"]!.GetValue<string>());
            Assert.True(release["bracket"]!["editable"]!.GetValue<bool>());
            Assert.Contains(await Marks(Lane("flare_sequence")), m => m["kind"]!.GetValue<string>() == "condition" && m["value"] != null);
            Assert.Contains(await Marks(Lane("healthy_to_destroyed")), m => m["kind"]!.GetValue<string>() == "excluded");

            // The transport steps one 1/60 s tick and reads back seconds.
            await Job("animation_transport", new() { ["action"] = "next", ["preview"] = await Preview() });
            Assert.Equal(2 + 1.0 / 60, (await Call("state", new()))["animationTime"]!.GetValue<double>(), 6);
            await Job("animation_transport", new() { ["action"] = "previous", ["preview"] = await Preview() });
            Assert.Equal(2, (await Call("state", new()))["animationTime"]!.GetValue<double>(), 6);

            // The Log, paged.
            var log = (await Timing("log", new() { ["limit"] = 2 }))["result"]!;
            Assert.Equal(2, log["items"]!.AsArray().Count); Assert.Equal(2, log["nextOffset"]!.GetValue<int>());
            Assert.True(log["total"]!.GetValue<int>() > 2);
            Assert.Contains((await Timing("log"))["result"]!["items"]!.AsArray(), r => r!["result"]!.GetValue<string>() == "released mainsplash" && r["mark"]!.GetValue<string>() == release["id"]!.GetValue<string>());

            // An occurrence selects its event and seeks to it; the graph's row follows.
            var picked = (await Timing("occurrence", new() { ["lane"] = move, ["mark"] = release["id"]!.GetValue<string>() }))!;
            Assert.Equal(release["id"]!.GetValue<string>(), picked["timing"]!["selection"]!["mark"]!.GetValue<string>());
            Assert.Equal(release["start"]!.GetValue<double>(), (await Call("state", new()))["animationTime"]!.GetValue<double>(), 1.0 / 60 + 1e-6);
            Assert.NotNull(picked["result"]!["graph"]);

            // A threshold proposal opens the card's source draft (seconds, in the event's own clock); cancelling removes it.
            var card = (SourceSectionCard)typeof(MainWindow).GetField("sectionCard", flags)!.GetValue(main)!;
            var proposed = await Timing("threshold", new() { ["event"] = release["event"]!.GetValue<string>(), ["seconds"] = 0.75 });
            Assert.True(card.HasDraft);
            var change = Assert.Single(proposed["result"]!["draft"]!["changes"]!.AsArray())!;
            Assert.Equal(["EVENT_OFFSET", "0.75"], change["Values"]!.AsArray().Select(v => v!.GetValue<string>()));
            await Job("source_animation", new() { ["action"] = "cancel", ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["definition"] = bridge, ["token"] = card.DraftToken });
            Assert.False(card.HasDraft); Assert.False(doc.IsDirty);
            // source_animation set takes the same threshold in seconds (startClock, startSeconds) for the selected event.
            await Job("source_animation", new() { ["action"] = "set", ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["definition"] = bridge, ["startClock"] = "sequence", ["startSeconds"] = 0.5 });
            var set = Assert.Single(System.Text.Json.JsonSerializer.SerializeToNode(card.AnimationDraft)!["changes"]!.AsArray())!;
            Assert.Equal(["SEQUENCE_OFFSET", "0.5"], set["Values"]!.AsArray().Select(v => v!.GetValue<string>()));
            await Job("source_animation", new() { ["action"] = "cancel", ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["definition"] = bridge, ["token"] = card.DraftToken });
            Assert.False(card.HasDraft);

            // Starts move through the scenario: MCP animation_scenario starts, then the Starts lane's start through UI Automation.
            await Job("animation_scenario", new() { ["action"] = "starts", ["preview"] = await Preview(), ["starts"] = new[] { new { id = Guid.Empty, entry, seconds = 1.0 } } });
            lanes = await Lanes();
            Assert.Equal(1, lanes.Single(l => l["kind"]!.GetValue<string>() == "starts")["starts"]![0]!["seconds"]!.GetValue<double>());
            await Job("animation_transport", new() { ["action"] = "seek", ["seconds"] = 3.0, ["preview"] = await Preview() });
            main.UpdateLayout();
            var view = Descendants(main).OfType<AnimationTimingView>().Single();
            var surface = Descendants(view).OfType<AnimationTimingView.TimingSurface>().Single();
            var peers = Walk(UIElementAutomationPeer.CreatePeerForElement(surface)!).ToArray();
            Assert.Contains(peers, p => p.GetAutomationControlType() == AutomationControlType.ListItem && p.GetName().StartsWith("mainsplash, ", StringComparison.Ordinal));
            Assert.True(peers.Any(p => p.GetAutomationControlType() == AutomationControlType.Button && p.GetName().StartsWith("Release mainsplash, ", StringComparison.Ordinal)),
                string.Join(" | ", peers.Select(p => p.GetAutomationControlType() + ":" + p.GetName()).Take(60)));
            var start = peers.Single(p => p.GetAutomationControlType() == AutomationControlType.Slider && p.GetName().StartsWith("★ destroy_tr_bridge starts at 1.000 s", StringComparison.Ordinal));
            ((IRangeValueProvider)start.GetPattern(PatternInterface.RangeValue)!).SetValue(1.5);
            await Task.Delay(100, token);
            await ((Task)typeof(MainWindow).GetField("worldAnimationReevaluation", flags)!.GetValue(main)!).WaitAsync(token);
            Assert.Equal(1.5, (await Job("animation_scenario", new() { ["action"] = "read", ["preview"] = await Preview() }))["scenario"]!["starts"]![0]!["seconds"]!.GetValue<double>());

            // A called group: closed by default, expand opens it (presentation only); the presentation options round-trip.
            await Job("animation_scenario", new() { ["action"] = "includeAll", ["preview"] = await Preview() });
            await Job("animation_transport", new() { ["action"] = "seek", ["seconds"] = 3.0, ["preview"] = await Preview() });
            lanes = await Lanes();
            var called = lanes.Single(l => l["kind"]!.GetValue<string>() == "group" && l["label"]!.GetValue<string>().StartsWith("rdrail_destroy01", StringComparison.Ordinal));
            Assert.False(called["expanded"]!.GetValue<bool>());
            Assert.DoesNotContain(lanes, l => l["group"]?.GetValue<string>() == called["id"]!.GetValue<string>());
            await Timing("expand", new() { ["lane"] = called["id"]!.GetValue<string>(), ["expanded"] = true });
            Assert.Contains(await Lanes(), l => l["label"]!.GetValue<string>() == "fall" && l["group"]?.GetValue<string>() == called["id"]!.GetValue<string>());
            var options = (await Timing("options", new() { ["changes"] = new { thresholds = true, visibleRange = new[] { 1.0, 4.0 }, labelWidth = 260.0, view = "log" } }))["timing"]!;
            Assert.Equal((true, 1d, 4d, 260d, "log"), (options["thresholds"]!.GetValue<bool>(), options["visibleRange"]![0]!.GetValue<double>(), options["visibleRange"]![1]!.GetValue<double>(), options["labelWidth"]!.GetValue<double>(), options["view"]!.GetValue<string>()));
            var wide = await client.CallToolAsync("zstudio_animation_timing", new Dictionary<string, object?> { ["action"] = "options", ["preview"] = await Preview(), ["changes"] = new { labelWidth = 900.0 } }, cancellationToken: token);
            Assert.True(wide.IsError == true, "The label column is 160–420 DIP.");
            await Timing("options", new() { ["changes"] = new { view = "lanes", fit = "run" } });

            async Task<JsonObject[]> Lanes() => (await Timing("lanes"))["result"]!["items"]!.AsArray().OfType<JsonObject>().ToArray();
            async Task<JsonObject[]> Marks(string lane) => (await Timing("marks", new() { ["lane"] = lane }))["result"]!["items"]!.AsArray().OfType<JsonObject>().ToArray();
            async Task<JsonNode> Timing(string action, Dictionary<string, object?>? extra = null)
            {
                var args = extra ?? new(); args["action"] = action; args["preview"] = await Preview();
                return await Job("animation_timing", args);
            }
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

    /// <summary>The Inspector's start threshold, in seconds only: loading never proposes, − / + step on the 0.025 s grid, the ruler takes any value.</summary>
    private static void ThresholdEditor()
    {
        var threshold = new AnimationThresholdEditor();
        List<double> proposals = []; threshold.ValueProposed += proposals.Add;
        List<string> edits = []; threshold.TextEdited += edits.Add;
        const double authored = -1.23456789;
        threshold.SetValue("EVENT_OFFSET", authored);
        threshold.Measure(new Size(320, 240)); threshold.Arrange(new Rect(0, 0, 320, 240)); threshold.UpdateLayout();
        Assert.Equal(authored, threshold.Value); Assert.Empty(proposals); Assert.Empty(edits);
        var boxes = Descendants(threshold).OfType<TextBox>().ToArray();
        var numeric = Assert.Single(boxes);
        Assert.Equal("Start threshold in seconds", AutomationProperties.GetName(numeric));
        Assert.DoesNotContain(Descendants(threshold).OfType<TextBlock>(), t => t.Text.Contains("frame", StringComparison.OrdinalIgnoreCase));
        numeric.Text = "-";
        Assert.Equal("-", Assert.Single(edits)); Assert.Empty(proposals);
        var later = Descendants(threshold).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Later by 0.025 seconds");
        threshold.SetValue("EVENT_OFFSET", .1);
        later.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(.125, Assert.Single(proposals), 9);
        var ruler = Descendants(threshold).OfType<FrameworkElement>().Single(e => AutomationProperties.GetName(e) == "Start threshold ruler in seconds");
        var value = Assert.IsAssignableFrom<IValueProvider>(UIElementAutomationPeer.CreatePeerForElement(ruler)!.GetPattern(PatternInterface.Value));
        value.SetValue("-2.000123");
        Assert.Equal(-2.000123, proposals[^1]); Assert.Equal(.1, threshold.Value);
        threshold.SetValue("EVENT_OFFSET", -2.000123, false);
        Assert.Throws<ElementNotEnabledException>(() => value.SetValue("3"));
    }

    private static IEnumerable<AutomationPeer> Walk(AutomationPeer peer)
    {
        foreach (var child in peer.GetChildren() ?? [])
        {
            yield return child;
            foreach (var descendant in Walk(child)) yield return descendant;
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
