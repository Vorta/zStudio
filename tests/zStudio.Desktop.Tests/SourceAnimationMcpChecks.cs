using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core.Sources;
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class SourceAnimationMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        // A third definition holds an IF/ELSEIF chain, whose ELSEIF switch is an MCP path of its own.
        string definitions = VtolDropFixture.Definitions(); int listEnd = definitions.LastIndexOf("\n  )", StringComparison.Ordinal);
        definitions = definitions[..listEnd] + "\n    ANIMATION_DEFINITION ( NAME ( ground ) ANIMATION_NAME ( choices ) ACTIVATION ( ON_CALL ) RESET_TIME ( -1 )" +
            " SEQUENCE_DEFINITION ( NAME ( pick ) IF ( RANDOM_WEIGHT ( 0.5 ) ) CALLBACK ( VALUE ( 14 ) ) ELSEIF ( ANIMATION_LOD ( HIGH ) ) CALLBACK ( VALUE ( 15 ) ) ENDIF () ) )" + definitions[listEnd..];
        fixture.Write(VtolDropFixture.Root, definitions);
        fixture.Write("data/m1/zrdr/flight.zan", VtolDropFixture.MotionScript);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            await Job("source_world_open", new() { ["mission"] = "m1" });
            await Preview();
            var doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            var records = (await Animation("read"))["items"]!.AsArray();
            var activation = records[0]!["Fields"]!.AsArray().Single(f => f!["Key"]!.GetValue<string>() == "ACTIVATION")!;
            string field = activation["Id"]!.GetValue<string>();
            await Animation("begin");
            var card = (SourceSectionCard)typeof(MainWindow).GetField("sectionCard", flags)!.GetValue(main)!;
            var firstCall = records.First(record => record!["Kind"]!.GetValue<string>() == "CALL_ANIMATION")!;
            card.SelectAnimationRecord(firstCall["Id"]!.GetValue<string>());
            main.UpdateLayout();
            var thresholdEditor = Descendants(card).OfType<WorldAnimationThresholdEditor>().Single(editor => editor.ClockMode == "SEQUENCE_OFFSET");
            var thresholdText = Descendants(thresholdEditor).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == "Source timing threshold in seconds");
            thresholdText.Text = "-";
            Assert.True(card.HasDraft);
            var pendingThreshold = System.Text.Json.JsonSerializer.SerializeToNode(card.AnimationDraft)!["changes"]!.AsArray();
            Assert.Contains(pendingThreshold, change => change!["Values"]!.AsArray().Last()!.GetValue<string>() == "-");
            await Animation("cancel", new() { ["token"] = card.DraftToken });
            Assert.False(card.HasDraft);
            await Animation("set", new() { ["changes"] = new[] { new { field, values = new[] { "ON_STARTUP" } } } });
            Assert.True(card.HasDraft);
            string draft = card.DraftToken;
            main.SetPickupsByType(true);
            Assert.True(card.HasDraft); Assert.Equal(draft, card.DraftToken);
            await Animation("cancel", new() { ["token"] = draft });
            Assert.False(card.HasDraft);
            var controls = Descendants(card).OfType<ComboBox>().Where(c => AutomationProperties.GetName(c) == "Activation").ToArray();
            Assert.Contains(controls, c => c.SelectedItem as string == "ON_CALL");
            await Animation("set", new() { ["changes"] = new[] { new { field, values = new[] { "ON_STARTUP" } } } });
            await Animation("apply", new() { ["token"] = card.DraftToken });
            await Preview();
            doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            Assert.True(doc.IsDirty); Assert.Equal(1, doc.SourceWorld!.Workspace.UndoCount);
            Assert.Contains("ON_STARTUP", System.Text.Encoding.Latin1.GetString(doc.SourceWorld.Workspace.Read(VtolDropFixture.Root)!));
            Assert.Equal(definitions, File.ReadAllText(fixture.Path(VtolDropFixture.Root)));
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
            await Preview();
            doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            Assert.False(doc.IsDirty);
            await Animation("begin");
            var current = await Call("state", new());
            Assert.Null(typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
            Assert.Null(((ContentControl)main.FindName("AnimationHost")).Content);
            int deliveryEntry = Assert.Single(Assert.IsType<SourceSceneSnapshot>(doc.Document).AnimationDefinitions.Definitions.Single(d => d.Index == 0).Entries);
            var structure = await Timeline("structure", new() { ["section"] = "calls", ["entry"] = deliveryEntry });
            var calls = structure["page"]!["Rows"]!.AsArray();
            Assert.NotEmpty(calls);
            // Animation mode: Open enters it without moving the camera or running anything; the binding is the primary start.
            var cameraBeforeOpen = await Call("camera", new() { ["preview"] = current["preview"]!.GetValue<string>(), ["action"] = "read" });
            var opened = await Mode("open", new() { ["definition"] = 0 });
            Assert.True(opened["active"]!.GetValue<bool>()); Assert.Equal(deliveryEntry, opened["opened"]!["entry"]!.GetValue<int>());
            Assert.NotEqual(current["preview"]!.GetValue<string>(), opened["preview"]!.GetValue<string>());
            Assert.Null(typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
            var cameraAfterOpen = await Call("camera", new() { ["preview"] = opened["preview"]!.GetValue<string>(), ["action"] = "read" });
            Assert.Equal(cameraBeforeOpen["Position"]!.ToJsonString(), cameraAfterOpen["Position"]!.ToJsonString());
            Assert.IsType<DefinitionEntry>(card.Entry);
            // Include all includes every call path the starts reach but back-links, and says what it did; Exclude all clears them.
            var included = await Scenario("includeAll");
            Assert.True(included["scenario"]!["calls"]!["reachable"]!.GetValue<int>() > 0);
            Assert.Equal(included["scenario"]!["calls"]!["reachable"]!.GetValue<int>() - included["scenario"]!["calls"]!["groups"]!.AsArray().Where(g => g!["backLink"]!.GetValue<bool>()).Sum(g => g!["total"]!.GetValue<int>()),
                included["scenario"]!["calls"]!["included"]!.GetValue<int>());
            Assert.StartsWith("Included ", included["scenario"]!["calls"]!["includeAll"]!.GetValue<string>());
            Assert.Equal(0, (await Scenario("excludeAll"))["scenario"]!["calls"]!["included"]!.GetValue<int>());
            // A second scheduled binding must stay part of the same timeline when inspected.
            int secondaryEntry = calls.First(c => c!["TargetEntry"] != null)!["TargetEntry"]!.GetValue<int>();
            Guid scheduledActivation = Guid.NewGuid();
            var primaryCandidate = typeof(MainWindow).GetField("animationMode", flags)!.GetValue(main);
            await Scenario("starts", new() { ["starts"] = new[]
            {
                new { id = Guid.Empty, entry = deliveryEntry, seconds = 2d },
                new { id = scheduledActivation, entry = secondaryEntry, seconds = 0d }
            } });
            var scheduledRows = (await Timeline("rows", new() { ["view"] = "source" }))["rows"]!.AsArray();
            Assert.Equal(2, scheduledRows.Count(r => r!["ActivationId"] != null));
            string secondaryRow = $"entry:{secondaryEntry}:/@{scheduledActivation:N}";
            await Timeline("expand", new() { ["row"] = secondaryRow, ["expanded"] = false });
            var collapsedRows = (await Timeline("rows", new() { ["view"] = "source" }))["rows"]!.AsArray();
            Assert.False(collapsedRows.Single(r => r!["Id"]!.GetValue<string>() == secondaryRow)!["Expanded"]!.GetValue<bool>());
            Assert.DoesNotContain(collapsedRows, r => r!["ActivationId"]?.GetValue<Guid>() == scheduledActivation);
            await Timeline("expand", new() { ["row"] = secondaryRow, ["expanded"] = true });
            await Timeline("row", new() { ["row"] = secondaryRow });
            await Timeline("select", new() { ["activation"] = scheduledActivation, ["entry"] = secondaryEntry });
            Assert.Equal(primaryCandidate, typeof(MainWindow).GetField("animationMode", flags)!.GetValue(main));
            var scheduleTimeline = Assert.IsType<WorldAnimationTimeline>(typeof(MainWindow).GetField("worldTimeline", flags)!.GetValue(main));
            var scheduledModels = Assert.IsAssignableFrom<IReadOnlyList<WorldAnimationTimelineRow>>(typeof(MainWindow).GetField("worldAnimationSourceRows", flags)!.GetValue(main));
            scheduleTimeline.MoveTiming(scheduledModels.Single(r => r.ActivationId == Guid.Empty), 2.125);
            var configured = Assert.IsType<zStudio.Core.Animation.AnimationScenario>(typeof(MainWindow).GetProperty("worldAnimationConfiguration", flags)!.GetValue(main));
            Assert.Equal(2.125, configured.Starts.Single(s => s.Id == Guid.Empty).Seconds);
            Assert.False(doc.IsDirty);
            await Scenario("fitRange");
            Assert.Equal(32.125, scheduleTimeline.PresentationState.VisibleEnd);
            // The Scenario tab shows the same scenario, live: an MCP change appears there at once.
            await Mode("layout", new() { ["rightTab"] = "scenario" });
            var scenarioTab = Assert.IsType<AnimationScenarioPanel>(typeof(MainWindow).GetField("scenarioPanel", flags)!.GetValue(main));
            Assert.True(scenarioTab.IsVisible);
            Assert.Contains(Descendants(scenarioTab).OfType<Button>(), b => AutomationProperties.GetName(b) == "+ Add start…");
            await Scenario("inputs", new() { ["seed"] = 9 });
            Assert.Equal("9", Descendants(scenarioTab).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == "Random seed").Text);
            await Scenario("inputs", new() { ["seed"] = 1 });
            await Mode("layout", new() { ["rightTab"] = "inspector" });
            await Scenario("starts", new() { ["starts"] = new[] { new { id = Guid.Empty, entry = deliveryEntry, seconds = 0d } } });
            // Inspecting a called definition before Play must preserve the opened binding.
            var unpinnedRoot = typeof(MainWindow).GetField("animationMode", flags)!.GetValue(main);
            var beforeRows = (await Timeline("rows", new() { ["view"] = "source" }))["rows"]!.AsArray();
            var callRow = beforeRows.First(r => r!["Included"] != null && r["Expandable"]!.GetValue<bool>())!;
            await Timeline("expand", new() { ["row"] = callRow["Id"]!.GetValue<string>(), ["expanded"] = true });
            var childRows = (await Timeline("rows", new() { ["view"] = "source" }))["rows"]!.AsArray();
            var childEntry = childRows.First(r => r!["IsGroup"]!.GetValue<bool>() && r["Depth"]!.GetValue<int>() == 3)!;
            await Timeline("expand", new() { ["row"] = childEntry["Id"]!.GetValue<string>(), ["expanded"] = true });
            childRows = (await Timeline("rows", new() { ["view"] = "source" }))["rows"]!.AsArray();
            foreach (var childSequence in childRows.Where(r => r!["IsGroup"]!.GetValue<bool>() && r["Depth"]!.GetValue<int>() == 4).ToArray())
                await Timeline("expand", new() { ["row"] = childSequence!["Id"]!.GetValue<string>(), ["expanded"] = true });
            childRows = (await Timeline("rows", new() { ["view"] = "source" }))["rows"]!.AsArray();
            var childEvent = childRows.First(r => r!["Depth"]!.GetValue<int>() == 5 && r["markCount"]!.GetValue<int>() > 0)!;
            string childRowId = childEvent["Id"]!.GetValue<string>();
            var childMark = (await Timeline("marks", new() { ["row"] = childRowId }))["items"]![0]!;
            var childContext = await Timeline("cell", new() { ["row"] = childRowId, ["mark"] = childMark["Id"]!.GetValue<string>(), ["seek"] = true });
            Assert.NotNull(childContext["context"]); Assert.Null(childContext["requestedSeconds"]);
            Assert.Equal(unpinnedRoot, typeof(MainWindow).GetField("animationMode", flags)!.GetValue(main));
            await Mode("open", new() { ["definition"] = 0 });
            Assert.Null(typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
            await Timeline("options", new() { ["changes"] = new { range = new[] { 0.0, 60.0 }, labelWidth = 260.0 } });
            // Browsing a definition only inspects source; explicit path evaluation has separate state.
            current = await Call("state", new());
            var paths = await Job("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["definition"] = 0, ["action"] = "analyze" });
            Assert.NotEmpty(paths["paths"]!["definitions"]!.AsArray());
            Assert.Equal(60, paths["paths"]!["definitions"]![0]!["seconds"]!.GetValue<double>());
            Assert.Null(typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
            Assert.All(paths["paths"]!["definitions"]!.AsArray().SelectMany(d => d!["paths"]!.AsArray()),
                p => Assert.Contains(p!["Kind"]!.GetValue<string>(), new[] { "object", "camera" }));
            // Dedicated flight analysis must not replace the opened definition or reset its scenario.
            await Mode("open", new() { ["definition"] = 1 });
            await Scenario("inputs", new() { ["seed"] = 17 });
            var candidateBeforeDrops = typeof(MainWindow).GetField("animationMode", flags)!.GetValue(main);
            var scenarioBeforeDrops = typeof(MainWindow).GetField("worldAnimationScenarioEpoch", flags)!.GetValue(main);
            Assert.NotNull(candidateBeforeDrops);
            current = await Call("state", new());
            paths = await Job("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["action"] = "show" });
            Assert.True(paths["parachute"]?["windows"] is JsonArray, paths.ToJsonString() + " · " + main.ViewModel.Status);
            Assert.Equal(candidateBeforeDrops, typeof(MainWindow).GetField("animationMode", flags)!.GetValue(main));
            Assert.Equal(scenarioBeforeDrops, typeof(MainWindow).GetField("worldAnimationScenarioEpoch", flags)!.GetValue(main));
            Assert.Equal(17, Assert.IsType<zStudio.Core.Animation.AnimationScenario>(typeof(MainWindow).GetProperty("worldAnimationConfiguration", flags)!.GetValue(main)).Seed);
            Assert.Equal(1, Assert.IsType<DefinitionEntry>(card.Entry).Definition.Index);
            Assert.Null(paths["parachute"]!["selected"]);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)main.FindName("ParachutePanel")).Visibility);
            paths = await Job("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["action"] = "analyze", ["snapshot"] = paths["parachute"]!["snapshot"]!.GetValue<string>(), ["entry"] = deliveryEntry });
            Assert.Equal(candidateBeforeDrops, typeof(MainWindow).GetField("animationMode", flags)!.GetValue(main));
            Assert.Equal(scenarioBeforeDrops, typeof(MainWindow).GetField("worldAnimationScenarioEpoch", flags)!.GetValue(main));
            Assert.Equal(17, Assert.IsType<zStudio.Core.Animation.AnimationScenario>(typeof(MainWindow).GetProperty("worldAnimationConfiguration", flags)!.GetValue(main)).Seed);
            Assert.Null(paths["parachute"]!["selected"]);
            paths = await Job("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["action"] = "select", ["snapshot"] = paths["parachute"]!["snapshot"]!.GetValue<string>(), ["window"] = paths["parachute"]!["windows"]![0]!["id"]!.GetValue<string>() });
            Assert.Equal(0, Assert.IsType<DefinitionEntry>(card.Entry).Definition.Index);
            var panel = (FrameworkElement)main.FindName("ParachutePanel");
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Contains(panel, Descendants(card));
            // A retained flight marker must reopen its docked card after another section replaced it.
            var tree = (SceneTreeModel)typeof(MainWindow).GetField("sceneTree", flags)!.GetValue(main)!;
            var zone = tree.FindSection(new ZoneEntry(tree.Sections!.Zones.First()).Key)!;
            typeof(MainWindow).GetMethod("SelectSectionRow", flags)!.Invoke(main, [zone, false]);
            Assert.IsType<ZoneEntry>(card.Entry);
            var mapViewport = Assert.IsType<zStudio.Rendering.SceneViewport>(((ContentControl)main.FindName("SceneHost")).Content);
            TaskCompletionSource markerReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new SemaphoreSlim(0))
            {
                main.SourceAnimationReading = ct => { markerReading.TrySetResult(); release.Wait(ct); };
                try
                {
                    Assert.True(mapViewport.SelectParachuteWindow(Guid.Parse(paths["parachute"]!["windows"]![0]!["id"]!.GetValue<string>())));
                    await markerReading.Task.WaitAsync(token);
                    Assert.IsType<DefinitionEntry>(card.Entry);
                    Assert.DoesNotContain(Descendants(card).OfType<TextBox>(), input => input.IsEnabled);
                    release.Release();
                    await ((Task)typeof(MainWindow).GetField("parachuteSelectionWork", flags)!.GetValue(main)!).WaitAsync(token);
                }
                finally { release.Release(); main.SourceAnimationReading = null; }
            }
            Assert.IsType<DefinitionEntry>(tree.Selected!.Section);
            Assert.Contains(panel, Descendants(card)); Assert.True(panel.IsVisible);
            // Generic path analysis is independent of inspection. A viewport pick owns the newer selection,
            // while the already requested analysis may publish its compatible guides without opening a card.
            TaskCompletionSource analyzing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new SemaphoreSlim(0))
            {
                main.SourceAnimationPathPreparing = ct => { analyzing.TrySetResult(); release.Wait(ct); };
                try
                {
                    current = await Call("state", new());
                    var pending = await Call("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["definition"] = 0, ["action"] = "analyze" });
                    await analyzing.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                    var viewport = Assert.IsType<zStudio.Rendering.SceneViewport>(((ContentControl)main.FindName("SceneHost")).Content);
                    int ground = viewport.PreviewScene!.Nodes.Single(n => n.Name == "ground").Index;
                    Assert.True(viewport.SelectInspectionNode(ground));
                    release.Release();
                    var analyzed = await AwaitJob(pending);
                    Assert.NotEmpty(analyzed["paths"]!["definitions"]!.AsArray());
                    Assert.Equal(ground, viewport.SelectedInspection!.SelectionNode);
                    Assert.Equal(candidateBeforeDrops, typeof(MainWindow).GetField("animationMode", flags)!.GetValue(main));
                    Assert.Null(typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
                }
                finally { release.Release(); main.SourceAnimationPathPreparing = null; }
            }
            current = await Call("state", new());
            string mapPreview = current["preview"]!.GetValue<string>();
            await Job("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["definition"] = 0, ["action"] = "preview" });
            Assert.Null(((ContentControl)main.FindName("AnimationHost")).Content);
            Assert.Same(mapViewport, ((ContentControl)main.FindName("SceneHost")).Content);
            var session = Assert.IsType<WorldAnimationSession>(typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
            Assert.Empty(session.Scenario.IncludedCalls);
            Assert.True(session.IsAttached);
            // Opening another definition kept the previous primary as a start: this run plays the opened binding alone.
            Assert.Equal(2, session.Scenario.Starts.Count);
            await Scenario("starts", new() { ["starts"] = new[] { new { id = Guid.Empty, entry = deliveryEntry, seconds = 0d } } });
            Assert.Same(session, typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
            Assert.Single(session.Scenario.Starts); Assert.True(session.IsAttached);
            Assert.False(((Control)main.FindName("FlyEnabled")).IsEnabled);
            Assert.Equal("stale_preview", (await Job("animation_transport", new() { ["preview"] = mapPreview, ["action"] = "pause" }, "failed"))["code"]!.GetValue<string>());
            Assert.IsType<DefinitionEntry>(((SceneTreeModel)typeof(MainWindow).GetField("sceneTree", flags)!.GetValue(main)!).Selected!.Section);
            await Mode("layout", new() { ["rightTab"] = "scenario" });
            Assert.True(((FrameworkElement)main.FindName("AnimationScenarioHost")).IsVisible);
            await Mode("layout", new() { ["rightTab"] = "inspector" });
            main.SetPickupsByType(false);
            Assert.True(((TreeView)main.FindName("DocumentSceneTree")).IsEnabled);
            current = await Call("state", new());
            string animationPreview = current["preview"]!.GetValue<string>();
            var previewState = await Call("preview_state", new() { ["preview"] = animationPreview });
            Assert.Equal("terrain", previewState["animation"]!["collisionMode"]!.GetValue<string>());
            Assert.Equal(-500, previewState["animation"]!["proceduralYLimits"]!["minimum"]!.GetValue<int>());
            Assert.Equal("unsupported", (await Job("animation_options", new() { ["preview"] = animationPreview, ["changes"] = new { collision = false } }, "failed"))["code"]!.GetValue<string>());
            await Job("scene_options", new() { ["preview"] = current["preview"]!.GetValue<string>(), ["changes"] = new { wireframe = true, textures = false, faceOrientation = true } });
            Assert.True(mapViewport.Wireframe); Assert.True(mapViewport.FaceOrientationEnabled);
            typeof(MainWindow).GetMethod("ClearSectionSelection", flags)!.Invoke(main, []);
            Assert.Same(session, typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
            var openedBeforeReselect = typeof(MainWindow).GetField("animationMode", flags)!.GetValue(main);
            Assert.NotNull(openedBeforeReselect);
            Assert.True(session.IsAttached);
            // Reselecting the already opened definition must not replace or run the session.
            var currentTree = (SceneTreeModel)typeof(MainWindow).GetField("sceneTree", flags)!.GetValue(main)!;
            var cachedAnimationRow = currentTree.FindSection(card.Entry!.Key)!;
            typeof(MainWindow).GetMethod("SelectSectionRow", flags)!.Invoke(main, [cachedAnimationRow, false]);
            Assert.Equal(openedBeforeReselect, typeof(MainWindow).GetField("animationMode", flags)!.GetValue(main));
            Assert.Same(session, typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
            Assert.True(session.IsAttached);
            await Job("animation_transport", new() { ["preview"] = animationPreview, ["action"] = "seek", ["seconds"] = 2.0 });
            Assert.False(session.IsPlaying);
            Assert.All(session.CurrentFrame!.Instances, instance => Assert.Equal(deliveryEntry, instance.Entry));
            var scriptEvent = session.Context.Package.Entries[deliveryEntry].AllSequences.SelectMany(s => s.Events).Single(e => e.Type == 12);
            var activeScript = session.CurrentFrame.Trace.Single(t => t.Event == scriptEvent.Id && t.End == null);
            var observedRows = (IReadOnlyList<WorldAnimationTimelineRow>)typeof(MainWindow).GetField("worldAnimationRunRows", flags)!.GetValue(main)!;
            var observedScript = observedRows.SelectMany(r => r.Marks).Single(m => m.Occurrence == activeScript.Occurrence);
            Assert.Equal(WorldAnimationTimelineMarkKind.Running, observedScript.Kind);
            Assert.Equal(activeScript.Start, observedScript.Start); Assert.Equal(2, observedScript.End);
            // Selecting an animated source object is passive. Its first actual field draft returns to the static world,
            // preserving the typed text and rebinding the animated inspection identity to the baseline target.
            int animatedGround = mapViewport.PreviewScene!.Nodes.Single(n => n.Name == "ground").Index;
            await Job("scene_card", new() { ["preview"] = animationPreview, ["action"] = "select", ["node"] = animatedGround });
            var objectCard = Assert.IsType<SceneInspectionCard>(mapViewport.InspectionContent);
            var objectEditor = Assert.IsType<SourceObjectCardEditor>(objectCard.ObjectEditor);
            Assert.True(session.IsAttached);
            await Job("scene_card", new() { ["preview"] = animationPreview, ["action"] = "begin", ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            Assert.True(session.IsAttached);
            var positionX = Descendants(objectEditor).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Position X");
            positionX.Text = "3";
            Assert.False(session.IsAttached); Assert.Null(session.CurrentFrame);
            Assert.Same(objectEditor, objectCard.ObjectEditor);
            Assert.True(objectCard.HasObjectDraft); Assert.Equal("3", positionX.Text);
            Assert.Equal(objectCard.Selection!.Target, objectCard.DraftTarget);
            Assert.True(mapViewport.HasTransformDraft);
            await Job("scene_card", new() { ["preview"] = animationPreview, ["action"] = "cancel", ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["token"] = objectCard.DraftToken });
            Assert.False(objectCard.HasDraft); Assert.False(session.IsAttached);
            await Job("animation_transport", new() { ["preview"] = animationPreview, ["action"] = "seek", ["seconds"] = 2.0 });
            await Job("animation_transport", new() { ["preview"] = animationPreview, ["action"] = "stop" });
            Assert.False(session.IsAttached); Assert.Null(session.CurrentFrame);
            Assert.Same(mapViewport, ((ContentControl)main.FindName("SceneHost")).Content);
            Assert.Null(((ContentControl)main.FindName("AnimationHost")).Content);
            Assert.True(((Control)main.FindName("FlyEnabled")).IsEnabled);
            await Scenario("include", new() { ["paths"] = new[] { calls[0]!["Path"]!.GetValue<string>() }, ["included"] = true });
            await Job("animation_transport", new() { ["preview"] = animationPreview, ["action"] = "seek", ["seconds"] = 2.0 });
            Assert.Contains(session.CurrentFrame!.Instances, instance => instance.Entry != deliveryEntry);
            await Timeline("options", new() { ["changes"] = new { view = "run" } });
            var runRows = (await Timeline("rows", new() { ["view"] = "run" }))["rows"]!.AsArray();
            Assert.All(runRows, row => Assert.Null(row!["Marks"]));
            var eventRow = runRows.First(row => row!["markCount"]!.GetValue<int>() > 1)!;
            string rowId = eventRow["Id"]!.GetValue<string>();
            var markPage = await Timeline("marks", new() { ["row"] = rowId, ["limit"] = 1 });
            Assert.Single(markPage["items"]!.AsArray()); Assert.Equal(1, markPage["nextOffset"]!.GetValue<int>());
            var mark = markPage["items"]![0]!;
            var selectedMark = await Timeline("row", new() { ["row"] = rowId, ["occurrence"] = mark["Occurrence"]!.GetValue<long>() });
            Assert.Equal(mark["Id"]!.GetValue<string>(), selectedMark["presentation"]!["SelectedMark"]!.GetValue<string>());
            Assert.Equal(2, session.CurrentFrame.Time);
            // Frame cells share exact source identities, read-only context and the ordinary timing draft/history.
            var frameSeek = await Timeline("frame", new() { ["frame"] = 2.0 });
            Assert.Equal(2, frameSeek["presentation"]!["RequestedFrame"]!.GetValue<double>());
            Assert.Equal(1.0 / 60, frameSeek["actualSeconds"]!.GetValue<double>(), 8);
            // Same-frame seeks overlap under a deterministic simulation gate; the obsolete request cannot clear its successor.
            var timelineControl = (WorldAnimationTimeline)typeof(MainWindow).GetField("worldTimeline", flags)!.GetValue(main)!;
            var simulationGate = (SemaphoreSlim)typeof(WorldAnimationSession).GetField("simulationGate", flags)!.GetValue(session)!;
            await simulationGate.WaitAsync(token);
            Task oldSeek, newSeek;
            try
            {
                var seekFrame = typeof(MainWindow).GetMethod("SeekWorldAnimationFrameAsync", flags)!;
                oldSeek = (Task)seekFrame.Invoke(main, [.025, token])!;
                newSeek = (Task)seekFrame.Invoke(main, [.025, token])!;
                Assert.Equal(.025, timelineControl.RequestedTime);
            }
            finally { simulationGate.Release(); }
            var superseded = await Assert.ThrowsAsync<zStudio.Automation.StudioCommandException>(() => oldSeek);
            Assert.Contains("superseded", superseded.Message);
            await newSeek; Assert.Equal(.025, timelineControl.RequestedTime);
            await Job("animation_transport", new() { ["preview"] = animationPreview, ["action"] = "seek", ["seconds"] = 2.0 });
            Assert.Null(timelineControl.RequestedTime);
            await Timeline("frame", new() { ["frame"] = 401.0 });
            await Timeline("options", new() { ["changes"] = new { range = new[] { 0.0, 1.0 } } });
            Assert.Null(timelineControl.RequestedTime); Assert.Equal(0, session.State.Time);
            await Timeline("options", new() { ["changes"] = new { range = new[] { 0.0, 60.0 } } });
            await Job("animation_transport", new() { ["preview"] = animationPreview, ["action"] = "stop" });
            Assert.Null((await Timeline("read"))["presentation"]!["RequestedFrame"]);
            await Timeline("options", new() { ["changes"] = new { view = "source" } });
            var sourceRows = (await Timeline("rows", new() { ["view"] = "source" }))["rows"]!.AsArray();
            var timingRow = sourceRows.First(r => r!["CanEditTiming"]!.GetValue<bool>())!;
            string timingRowId = timingRow["Id"]!.GetValue<string>();
            var cellMark = (await Timeline("marks", new() { ["row"] = timingRowId }))["items"]![0]!;
            var inspected = await Timeline("cell", new() { ["row"] = timingRowId, ["mark"] = cellMark["Id"]!.GetValue<string>(), ["seek"] = false });
            Assert.NotNull(inspected["context"]!["Definition"]!["Text"]);
            Assert.False(doc.IsDirty); Assert.False(card.HasDraft);
            await Timeline("timing", new() { ["row"] = timingRowId, ["frame"] = 41.0 });
            Assert.True(card.HasDraft);
            var timingChanges = System.Text.Json.JsonSerializer.SerializeToNode(card.AnimationDraft)!["changes"]!.AsArray();
            Assert.Equal("1", Assert.Single(timingChanges)!["Values"]![1]!.GetValue<string>());
            await Animation("cancel", new() { ["token"] = card.DraftToken });
            Assert.False(card.HasDraft); Assert.False(doc.IsDirty);
            var context = await Animation("context", new() { ["record"] = firstCall["Id"]!.GetValue<string>() });
            Assert.Contains("callback", context["Definition"]!["Text"]!.GetValue<string>());
            Assert.True(context["Definition"]!["Text"]!.GetValue<string>().Length <= 8192);
            var instances = await Call("animation_runtime", new() { ["preview"] = animationPreview, ["section"] = "instances" });
            Assert.NotEmpty(instances["items"]!.AsArray());
            await Call("animation_runtime", new() { ["preview"] = animationPreview, ["section"] = "conditions" });
            await Mode("exit");
            Assert.Equal("stale_preview", (await Job("animation_transport", new() { ["preview"] = animationPreview, ["action"] = "pause" }, "failed"))["code"]!.GetValue<string>());
            typeof(MainWindow).GetMethod("ClearSectionSelection", flags)!.Invoke(main, []);
            var changes = new[] { new { field, values = new[] { "ON_STARTUP" } } };
            Assert.Equal("context_changed", (await Job("source_animation", Args("set", new() { ["changes"] = changes }), "failed"))["code"]!.GetValue<string>());
            Assert.False(card.HasDraft);
            var firstMap = doc;
            // Accept a frame proposal through the same source transaction, including history and disk reopening.
            await Mode("open", new() { ["definition"] = 0 });
            await Scenario("inputs", new() { ["seed"] = 23 });
            var persistenceRows = (await Timeline("rows", new() { ["view"] = "source" }))["rows"]!.AsArray();
            string persistenceRow = persistenceRows.First(r => r!["CanEditTiming"]!.GetValue<bool>())!["Id"]!.GetValue<string>();
            byte[] beforeTiming = doc.SourceWorld!.Workspace.Read(VtolDropFixture.Root)!;
            byte[] beforeScript = File.ReadAllBytes(fixture.Path("data/m1/zrdr/flight.zan"));
            await Timeline("timing", new() { ["row"] = persistenceRow, ["frame"] = 101.0 });
            await Animation("apply", new() { ["token"] = card.DraftToken }); await Preview();
            doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            // Animation mode and its scenario follow the definition onto the rebuilt map.
            var afterEdit = await Mode("state");
            Assert.True(afterEdit["active"]!.GetValue<bool>()); Assert.Equal(doc.Revision, afterEdit["opened"]!["revision"]!.GetValue<long>());
            Assert.Equal(23, Assert.IsType<zStudio.Core.Animation.AnimationScenario>(typeof(MainWindow).GetProperty("worldAnimationConfiguration", flags)!.GetValue(main)).Seed);
            byte[] afterTiming = doc.SourceWorld!.Workspace.Read(VtolDropFixture.Root)!;
            Assert.NotEqual(beforeTiming, afterTiming);
            Assert.Contains("2.5", System.Text.Encoding.Latin1.GetString(afterTiming));
            Assert.Equal(beforeTiming, File.ReadAllBytes(fixture.Path(VtolDropFixture.Root)));
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" }); await Preview();
            doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            Assert.Equal(beforeTiming, doc.SourceWorld!.Workspace.Read(VtolDropFixture.Root));
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "redo" }); await Preview();
            doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            Assert.Equal(afterTiming, doc.SourceWorld!.Workspace.Read(VtolDropFixture.Root));
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); await main.ViewModel.ProjectSettingsWritten.WaitAsync(token);
            await Call("close_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            await Job("source_world_open", new() { ["mission"] = "m1" }); await Preview();
            doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null); firstMap = doc;
            Assert.False(doc.IsDirty); Assert.Equal(afterTiming, doc.SourceWorld!.Workspace.Read(VtolDropFixture.Root));
            // The map's scenario is saved in the project and comes back with the map, through its edits, undo and redo too.
            Assert.False((await Mode("state"))["active"]!.GetValue<bool>());
            await Mode("open", new() { ["definition"] = 0 });
            Assert.Equal(23, Assert.IsType<zStudio.Core.Animation.AnimationScenario>(typeof(MainWindow).GetProperty("worldAnimationConfiguration", flags)!.GetValue(main)).Seed);
            Assert.NotNull(zStudio.Core.Sources.SourceProjectSettings.ReadAnimationScenario(fixture.Project, "m1"));
            // Force the chain's IF false and its ELSEIF true in the opened binding's own path, then read both outcomes.
            var choices = Assert.IsType<SourceSceneSnapshot>(doc.Document).AnimationDefinitions.Definitions.Single(d => d.Name == "choices");
            int choicesEntry = Assert.Single(choices.Entries);
            await Mode("open", new() { ["definition"] = choices.Index });
            var chain = doc.Document.Animations!.Entries[choicesEntry].Sequences[0].Events;
            Guid ifEvent = chain.Single(e => e.Type == 31).Id, elseIfEvent = chain.Single(e => e.Type == 33).Id;
            await Scenario("conditions", new() { ["conditions"] = new object[]
            {
                new { instancePath = "", entry = choicesEntry, @event = ifEvent.ToString(), value = "false" },
                new { instancePath = "", entry = choicesEntry, @event = elseIfEvent.ToString(), value = "true" },
            } });
            current = await Call("state", new());
            await Job("animation_transport", new() { ["preview"] = current["preview"]!.GetValue<string>(), ["action"] = "seek", ["seconds"] = 0.5 });
            current = await Call("state", new());
            var outcomes = (await Call("animation_runtime", new() { ["preview"] = current["preview"]!.GetValue<string>(), ["section"] = "conditions" }))["items"]!.AsArray();
            Assert.Contains(outcomes, c => c!["Event"]!.GetValue<Guid>() == ifEvent && !c["Value"]!.GetValue<bool>() && c["Forced"]!.GetValue<bool>());
            Assert.Contains(outcomes, c => c!["Event"]!.GetValue<Guid>() == elseIfEvent && c["Value"]!.GetValue<bool>() && c["Forced"]!.GetValue<bool>());
            // The transport steps one 1/60 s tick, in seconds, in MCP as in the GUI.
            current = await Call("state", new());
            await Job("animation_transport", new() { ["preview"] = current["preview"]!.GetValue<string>(), ["action"] = "next" });
            Assert.Equal(0.5 + 1.0 / 60, (await Call("state", new()))["animationTime"]!.GetValue<double>(), 6);
            await Job("animation_transport", new() { ["preview"] = current["preview"]!.GetValue<string>(), ["action"] = "previous" });
            Assert.Equal(0.5, (await Call("state", new()))["animationTime"]!.GetValue<double>(), 6);
            // A scenario change stops playback and re-evaluates up to the playhead: the switch's new outcome shows at once. Every
            // switch of one call is one change, so the call ends at the playhead.
            await Scenario("conditions", new() { ["conditions"] = new object[]
            {
                new { instancePath = "", entry = choicesEntry, @event = ifEvent.ToString(), value = "true" },
                new { instancePath = "", entry = choicesEntry, @event = elseIfEvent.ToString(), value = "game" },
            } });
            current = await Call("state", new());
            Assert.Equal(0.5, current["animationTime"]!.GetValue<double>(), 6);
            // Two quick changes (two switch clicks: the second during the first's re-evaluation) still end at the playhead.
            var condition = typeof(MainWindow).GetMethod("ScenarioCondition", flags)!;
            condition.Invoke(main, ["", choicesEntry, elseIfEvent, zStudio.Core.Animation.AnimationConditionChoice.True]);
            condition.Invoke(main, ["", choicesEntry, elseIfEvent, zStudio.Core.Animation.AnimationConditionChoice.Game]);
            await ((Task)typeof(MainWindow).GetField("worldAnimationReevaluation", flags)!.GetValue(main)!).WaitAsync(token);
            current = await Call("state", new());
            Assert.Equal(0.5, current["animationTime"]!.GetValue<double>(), 6);
            outcomes = (await Call("animation_runtime", new() { ["preview"] = current["preview"]!.GetValue<string>(), ["section"] = "conditions" }))["items"]!.AsArray();
            Assert.Contains(outcomes, c => c!["Event"]!.GetValue<Guid>() == ifEvent && c["Value"]!.GetValue<bool>() && c["Forced"]!.GetValue<bool>());
            // Shift+Esc exits (plain Esc never does); the scenario is kept and the definition stays selected. Pick in view ends
            // with the mode: no later click in the map layout places the player.
            Assert.False(main.AnimationModeKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None, null, false));
            Assert.True(main.InAnimationMode);
            typeof(MainWindow).GetMethod("BeginScenarioPick", flags)!.Invoke(main, ["player"]);
            Assert.Equal("player", (await Scenario("read"))["scenario"]!["picking"]!.GetValue<string>());
            Assert.True(main.AnimationModeKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.Shift, null, false));
            using (var exiting = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                exiting.CancelAfter(TimeSpan.FromSeconds(10));
                while (main.InAnimationMode) await Task.Delay(10, exiting.Token);
            }
            Assert.Null(typeof(MainWindow).GetField("scenarioPick", flags)!.GetValue(main));
            Assert.Null((await Scenario("read"))["scenario"]!["picking"]);
            var kept = await Mode("state");
            Assert.Equal(choices.Index, kept["keptScenario"]!["definition"]!.GetValue<int>());
            Assert.True(kept["keptScenario"]!["customized"]!.GetValue<bool>());
            Assert.Equal(choices.Index, Assert.IsType<DefinitionEntry>(card.Entry).Definition.Index);
            // Re-entering finds the scenario as it was left (its switch kept).
            await Mode("open", new() { ["definition"] = choices.Index });
            Assert.Contains((await Scenario("read"))["scenario"]!["conditions"]!.AsArray(), c => c!["event"]!.GetValue<Guid>() == ifEvent && c["value"]!.GetValue<string>() == "true");
            await Mode("exit");
            Assert.Equal(beforeScript, File.ReadAllBytes(fixture.Path("data/m1/zrdr/flight.zan")));
            await Job("source_world_open", new() { ["mission"] = "m2" }); await Preview();
            doc = main.ViewModel.Documents.Single(d => d.SourceWorld?.Mission == "m2");
            Assert.Equal("context_changed", (await Job("source_animation", Args("set", new() { ["changes"] = changes }), "failed"))["code"]!.GetValue<string>());
            Assert.False(firstMap.IsDirty); Assert.False(doc.IsDirty); Assert.False(card.HasDraft);
            // A begin superseded during its source read must fail, never return a different cached draft.
            TaskCompletionSource reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new SemaphoreSlim(0))
            {
                main.SourceAnimationReading = ct => { reading.TrySetResult(); release.Wait(ct); };
                try
                {
                    var pending = await Call("source_animation", Args("begin"));
                    await reading.Task.WaitAsync(token);
                    typeof(MainWindow).GetMethod("ClearSectionSelection", flags)!.Invoke(main, []);
                    release.Release();
                    Assert.Equal("context_changed", (await AwaitJob(pending, "failed"))["code"]!.GetValue<string>());
                    Assert.False(card.HasDraft);
                }
                finally { release.Release(); main.SourceAnimationReading = null; }
            }
            TaskCompletionSource previewReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondViewport = Assert.IsType<zStudio.Rendering.SceneViewport>(((ContentControl)main.FindName("SceneHost")).Content);
            using (var release = new SemaphoreSlim(0))
            {
                main.SourceAnimationReading = ct => { previewReading.TrySetResult(); release.Wait(ct); };
                try
                {
                    current = await Call("state", new());
                    var pending = await Call("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["definition"] = 0, ["action"] = "preview" });
                    await previewReading.Task.WaitAsync(token);
                    // Rendering state only: no FlyCameraSession or physical pointer capture.
                    secondViewport.SetFly(true);
                    release.Release();
                    Assert.Equal("busy", (await AwaitJob(pending, "failed"))["code"]!.GetValue<string>());
                    Assert.Null(((ContentControl)main.FindName("AnimationHost")).Content);
                }
                finally { secondViewport.SetFly(false); release.Release(); main.SourceAnimationReading = null; }
            }

            Dictionary<string, object?> Args(string action, Dictionary<string, object?>? extra = null)
            { var args = extra ?? new(); args["document"] = doc.SessionId.ToString(); args["revision"] = doc.Revision; args["definition"] = 0; args["action"] = action; return args; }
            Task<JsonNode> Animation(string action, Dictionary<string, object?>? extra = null) => Job("source_animation", Args(action, extra));
            async Task<JsonNode> Timeline(string action, Dictionary<string, object?>? extra = null)
            {
                var state = await Call("state", new()); var args = extra ?? new();
                args["document"] = doc.SessionId.ToString(); args["revision"] = doc.Revision;
                args["preview"] = state["preview"]!.GetValue<string>(); args["action"] = action;
                return await Job("animation_timeline", args);
            }
            async Task<JsonNode> Mode(string action, Dictionary<string, object?>? extra = null)
            {
                var state = await Call("state", new()); var args = extra ?? new();
                args["document"] = doc.SessionId.ToString(); args["revision"] = doc.Revision;
                args["preview"] = state["preview"]!.GetValue<string>(); args["action"] = action;
                return await Job("animation_mode", args);
            }
            async Task<JsonNode> Scenario(string action, Dictionary<string, object?>? extra = null)
            {
                var state = await Call("state", new()); var args = extra ?? new();
                args["preview"] = state["preview"]!.GetValue<string>(); args["action"] = action;
                return await Job("animation_scenario", args);
            }
            async Task Preview() => await ((Task)typeof(MainWindow).GetField("previewWork", flags)!.GetValue(main)!).WaitAsync(token);
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
            typeof(MainWindow).GetMethod("CloseParachutes", flags)!.Invoke(main, []);
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);
            main.Close();
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
