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
/// UI Automation), an occurrence selected and sought, an excluded call that would stop the bridge (its notice, its card's
/// Include and animation_scenario include), and the seconds-only threshold editor.
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
            SEQUENCE_DEFINITION ( NAME ( mainsplash ) ACTIVATION ( ON_CALL ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( ACTIVE ) ) LOOP ( LOOP_COUNT ( 2 ) START_TIME ( EVENT_OFFSET 0.1 ) ) )
            SEQUENCE_DEFINITION ( NAME ( lost_call ) CALL_ANIMATION ( NAME ( no_such_animation ) ) )
            SEQUENCE_DEFINITION ( NAME ( tilt ) OBJECT_MOTION_FROM_TO ( NAME ( ground ) ROTATE_FROM ( 0 0 0 ) ROTATE_TO ( 0 90 0 ) RUN_TIME ( 10.0 ) ) )
            SEQUENCE_DEFINITION ( NAME ( halt ) STOP_SEQUENCE ( NAME ( tilt ) START_TIME ( ANIMATION_OFFSET 1.0 ) ) ) )
          ANIMATION_DEFINITION (
            NAME ( ground ) ANIMATION_NAME ( rdrail_destroy01 ) ACTIVATION ( ON_CALL ) RESET_TIME ( -1 )
            SEQUENCE_DEFINITION ( NAME ( fall ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( ACTIVE ) START_TIME ( ANIMATION_OFFSET 0.2 ) ) )
            SEQUENCE_DEFINITION ( NAME ( stop_bridge ) STOP_ANIMATION ( NAME ( destroy_tr_bridge ) START_TIME ( ANIMATION_OFFSET 20 ) ) ) )
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
            // A custom range: the steps below go past the run's Auto end (under a second here), and a step stays in the range.
            await Job("animation_options", new() { ["preview"] = await Preview(), ["changes"] = new { playbackRange = new[] { 0.0, 30.0 } } });

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
            // An event a stop cuts short: tilt's turn ends at halt's STOP_SEQUENCE, an interval saying so; its graph row reads stopped.
            var turned = (await Marks(Lane("tilt"))).Single(m => !m["band"]!.GetValue<bool>());
            Assert.Equal(("interval", "Stopped by halt › Stop sequence tilt"), (turned["kind"]!.GetValue<string>(), turned["stopped"]!.GetValue<string>()));
            Assert.InRange(turned["end"]!.GetValue<double>(), 1 - 1.0 / 60 - 1e-6, 1 + 1e-6);
            var tilt = Guid.Parse(lanes.First(l => l["label"]!.GetValue<string>() == "tilt")["sequences"]![0]!["sequence"]!.GetValue<string>());
            var turnRow = (await Job("animation_graph", new() { ["action"] = "read", ["section"] = "rows", ["node"] = zStudio.Core.Animation.AnimationGraph.SequenceId(entry, tilt), ["preview"] = await Preview() }))["result"]!["items"]!.AsArray()
                .Single(r => r!["event"]!.GetValue<string>() == turned["event"]!.GetValue<string>())!["run"]!;
            Assert.Equal(("stopped", turned["end"]!.GetValue<double>()), (turnRow["disposition"]!.GetValue<string>(), turnRow["lastEnd"]!.GetValue<double>()));

            // The Condition card states the game rule in words with the current inputs, beside a switch per call path.
            var condition = (await Marks(Lane("flare_sequence"))).First(m => m["kind"]!.GetValue<string>() == "condition");
            var conditionCard = (await Job("animation_graph", new() { ["action"] = "select", ["preview"] = await Preview(),
                ["row"] = zStudio.Core.Animation.AnimationGraph.RowId(entry, Guid.Parse(condition["event"]!.GetValue<string>())) }))["result"]!["inspector"]!;
            Assert.Equal("condition", conditionCard["kind"]!.GetValue<string>());
            var preview = conditionCard["sections"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == "preview")!["items"]!.AsArray();
            Assert.Contains(preview, i => i!["type"]!.GetValue<string>() == "switch" && i["instancePath"]!.GetValue<string>() == "" && i["value"]!.GetValue<string>() == "game");
            string rule = preview.Single(i => i!["type"]!.GetValue<string>() == "text" && i["style"]!.GetValue<string>() == "info")!["text"]!.GetValue<string>();
            Assert.Contains("64", rule); Assert.StartsWith("The player", rule);
            Assert.Contains((await Job("animation_graph", new() { ["action"] = "inspector", ["preview"] = await Preview() }))["result"]!["inspector"]!["sections"]!.AsArray(), x => x!["id"]!.GetValue<string>() == "outcomes");
            // A notice: the call to an animation this map does not have, counted on its reference node.
            var notices = (await Call("animation_runtime", new() { ["preview"] = await Preview(), ["section"] = "notices" }))["items"]!.AsArray();
            var unavailable = notices.Single(n => n!["kind"]!.GetValue<string>() == "unavailable")!;
            Assert.Contains("no_such_animation", unavailable["text"]!.GetValue<string>()); Assert.StartsWith("ref:", unavailable["node"]!.GetValue<string>());
            // An excluded call that would change the run (design §15): rdrail_destroy01 stops destroy_tr_bridge 20 s after its call.
            var excluded = notices.Single(n => n!["kind"]!.GetValue<string>() == "excludedEffect")!;
            Assert.Equal("rdrail_destroy01 is excluded: in the game it stops destroy_tr_bridge at about 20 s", excluded["text"]!.GetValue<string>());
            var effect = excluded["excludedEffect"]!;
            Assert.Equal(("stop", entry, "destroy_tr_bridge", bridge), (effect["effect"]!.GetValue<string>(), effect["affected"]!["entry"]!.GetValue<int>(),
                effect["affected"]!["name"]!.GetValue<string>(), effect["excludingCall"]!["definition"]!.GetValue<int>()));
            Assert.InRange(effect["seconds"]!.GetValue<double>(), 20 - 2.0 / 60, 20 + 2.0 / 60);
            string includePath = effect["includePath"]!.GetValue<string>();
            Assert.Equal(effect["callPath"]!.GetValue<string>(), includePath);
            // Its port, and the affected binding's group, where Timing's marker sits at the estimated time.
            Assert.NotNull(effect["excludingCall"]!["edge"]);
            Assert.Equal("group", lanes.Single(l => l["id"]!.GetValue<string>() == excluded["lane"]!.GetValue<string>())["kind"]!.GetValue<string>());
            // Its card (the call's row) lists it with Include, the scenario's include of its path.
            var callCard = (await Job("animation_graph", new() { ["action"] = "select", ["preview"] = await Preview(), ["row"] = excluded["row"]!.GetValue<string>() }))["result"]!["inspector"]!;
            var callNotices = callCard["sections"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == "notices")!["items"]!.AsArray();
            Assert.Contains(callNotices, i => i!["type"]!.GetValue<string>() == "actions" && i["actions"]![0]!["action"]!.GetValue<string>() == "includePath" && i["actions"]![0]!["target"]!.GetValue<string>() == includePath);
            await Job("animation_scenario", new() { ["action"] = "include", ["preview"] = await Preview(), ["paths"] = new[] { includePath }, ["included"] = true });
            var included = (await Call("animation_runtime", new() { ["preview"] = await Preview(), ["section"] = "notices" }))["items"]!.AsArray();
            Assert.DoesNotContain(included, n => n!["kind"]!.GetValue<string>() == "excludedEffect");
            await Job("animation_scenario", new() { ["action"] = "include", ["preview"] = await Preview(), ["paths"] = new[] { includePath }, ["included"] = false });
            Assert.Contains((await Call("animation_runtime", new() { ["preview"] = await Preview(), ["section"] = "notices" }))["items"]!.AsArray(), n => n!["kind"]!.GetValue<string>() == "excludedEffect");

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
            // Every option is checked before any applies (one helper for maps and compiled previews): a decreasing range leaves the thresholds shown.
            var backwards = await Job("animation_timing", new() { ["action"] = "options", ["preview"] = await Preview(), ["changes"] = new { thresholds = false, visibleRange = new[] { 4.0, 1.0 } } }, "failed");
            Assert.Equal("invalid_argument", backwards["code"]!.GetValue<string>());
            Assert.True((await Timing("read"))["timing"]!["thresholds"]!.GetValue<bool>());
            await Timing("options", new() { ["changes"] = new { view = "lanes", fit = "run" } });

            // The Inspector's card path: the release's Start threshold + opens the source draft (Play waits), Revert removes it,
            // and Apply makes it one undo step.
            await Job("animation_graph", new() { ["action"] = "select", ["preview"] = await Preview(), ["row"] = zStudio.Core.Animation.AnimationGraph.RowId(entry, Guid.Parse(release["event"]!.GetValue<string>())) });
            main.UpdateLayout();
            var later = Descendants(card).OfType<AnimationThresholdEditor>().SelectMany(e => Descendants(e).OfType<Button>()).Single(b => AutomationProperties.GetName(b) == "Later by 0.025 seconds");
            later.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(card.HasDraft);
            Assert.Equal(["EVENT_OFFSET", "0.275"], Assert.Single(System.Text.Json.JsonSerializer.SerializeToNode(card.AnimationDraft)!["changes"]!.AsArray())!["Values"]!.AsArray().Select(v => v!.GetValue<string>()));
            Assert.Equal("Apply or revert the change to destroy_tr_bridge to play", Descendants(main).OfType<AnimationPanel>().Single().State.PlayBlocked);
            card.Discard();
            Assert.False(card.HasDraft); Assert.False(doc.IsDirty);
            main.UpdateLayout();
            later = Descendants(card).OfType<AnimationThresholdEditor>().SelectMany(e => Descendants(e).OfType<Button>()).Single(b => AutomationProperties.GetName(b) == "Later by 0.025 seconds");
            later.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await card.ApplyAsync();
            Assert.False(card.HasDraft);
            await ((Task)typeof(MainWindow).GetField("previewWork", flags)!.GetValue(main)!).WaitAsync(token);
            doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            Assert.True(doc.IsDirty); Assert.Equal(1, doc.SourceWorld!.Workspace.UndoCount);
            Assert.Contains("EVENT_OFFSET 0.275", System.Text.Encoding.Latin1.GetString(doc.SourceWorld.Workspace.Read(VtolDropFixture.Root)!));

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
