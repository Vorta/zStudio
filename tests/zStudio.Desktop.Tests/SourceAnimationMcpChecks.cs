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
        fixture.Write(VtolDropFixture.Root, VtolDropFixture.Definitions());
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
            var languageJob = await Call("source_language", new() { ["action"] = "symbols", ["path"] = VtolDropFixture.Root });
            Assert.True(languageJob["Cancellable"]!.GetValue<bool>());
            var languageSymbols = await AwaitJob(languageJob);
            Assert.NotEmpty(languageSymbols["items"]!.AsArray());
            var filteredLanguage = await Job("source_language", new() { ["action"] = "symbols", ["path"] = VtolDropFixture.Root, ["query"] = "no-such-symbol" });
            Assert.Empty(filteredLanguage["items"]!.AsArray());
            var languageHover = await Job("source_language", new() { ["action"] = "hover", ["path"] = VtolDropFixture.Root,
                ["position"] = VtolDropFixture.Definitions().IndexOf("ANIMATION_DEFINITIONS", StringComparison.Ordinal) });
            Assert.Contains("ANIMATION_DEFINITIONS", languageHover["hover"]!["Markdown"]!.GetValue<string>());
            var languageCompletion = await Job("source_language", new() { ["action"] = "completion", ["path"] = VtolDropFixture.Root,
                ["position"] = VtolDropFixture.Definitions().IndexOf("ANIMATION_DEFINITIONS", StringComparison.Ordinal) });
            Assert.Contains(languageCompletion["items"]!.AsArray(), item => item!["Label"]!.GetValue<string>() == "ANIMATION_DEFINITIONS"
                && item["Documentation"]!.GetValue<string>().Contains("shared gravity", StringComparison.Ordinal));
            Assert.Contains(languageCompletion["items"]!.AsArray(), item => item!["ReplacementSpan"]?["Length"]?.GetValue<int>() == "ANIMATION_DEFINITIONS".Length);
            var records = (await Animation("read"))["items"]!.AsArray();
            var activation = records[0]!["Fields"]!.AsArray().Single(f => f!["Key"]!.GetValue<string>() == "ACTIVATION")!;
            string field = activation["Id"]!.GetValue<string>();
            await Animation("begin");
            var card = (SourceSectionCard)typeof(MainWindow).GetField("sectionCard", flags)!.GetValue(main)!;
            var firstCall = records.First(record => record!["Kind"]!.GetValue<string>() == "CALL_ANIMATION")!;
            card.SelectAnimationRecord(firstCall["Id"]!.GetValue<string>());
            main.UpdateLayout();
            var thresholdEditor = Descendants(card).OfType<WorldAnimationThresholdEditor>().Single(editor => editor.ClockMode == "SEQUENCE_OFFSET");
            var thresholdText = Descendants(thresholdEditor).OfType<TextBox>().Single();
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
            Assert.Equal(VtolDropFixture.Definitions(), File.ReadAllText(fixture.Path(VtolDropFixture.Root)));
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
            Assert.Null(typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
            await Timeline("options", new() { ["changes"] = new { range = new[] { 0.0, 60.0 }, labelWidth = 260.0 } });
            // Browsing a definition only inspects source; explicit path evaluation has separate state.
            var paths = await Job("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["definition"] = 0, ["action"] = "analyze" });
            Assert.NotEmpty(paths["paths"]!["definitions"]!.AsArray());
            Assert.Equal(60, paths["paths"]!["definitions"]![0]!["seconds"]!.GetValue<double>());
            Assert.Null(typeof(MainWindow).GetField("worldAnimationSession", flags)!.GetValue(main));
            Assert.All(paths["paths"]!["definitions"]!.AsArray().SelectMany(d => d!["paths"]!.AsArray()),
                p => Assert.Contains(p!["Kind"]!.GetValue<string>(), new[] { "object", "camera" }));
            // Dedicated flight analysis must not replace another unpinned candidate or reset its scenario.
            await Timeline("target", new() { ["definition"] = 1 });
            await Timeline("options", new() { ["changes"] = new { seed = 17 } });
            var candidateBeforeDrops = typeof(MainWindow).GetField("worldAnimationCandidate", flags)!.GetValue(main);
            var scenarioBeforeDrops = typeof(MainWindow).GetField("worldAnimationScenarioEpoch", flags)!.GetValue(main);
            Assert.NotNull(candidateBeforeDrops);
            Assert.Null(typeof(MainWindow).GetField("worldAnimationPinned", flags)!.GetValue(main));
            paths = await Job("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["action"] = "show" });
            Assert.True(paths["parachute"]?["windows"] is JsonArray, paths.ToJsonString() + " · " + main.ViewModel.Status);
            Assert.Equal(candidateBeforeDrops, typeof(MainWindow).GetField("worldAnimationCandidate", flags)!.GetValue(main));
            Assert.Equal(scenarioBeforeDrops, typeof(MainWindow).GetField("worldAnimationScenarioEpoch", flags)!.GetValue(main));
            Assert.Equal(17, Assert.IsType<WorldAnimationScenario>(typeof(MainWindow).GetField("worldAnimationConfiguration", flags)!.GetValue(main)).Seed);
            Assert.Equal(1, Assert.IsType<DefinitionEntry>(card.Entry).Definition.Index);
            Assert.Null(paths["parachute"]!["selected"]);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)main.FindName("ParachutePanel")).Visibility);
            paths = await Job("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["action"] = "analyze", ["snapshot"] = paths["parachute"]!["snapshot"]!.GetValue<string>(), ["entry"] = deliveryEntry });
            Assert.Equal(candidateBeforeDrops, typeof(MainWindow).GetField("worldAnimationCandidate", flags)!.GetValue(main));
            Assert.Equal(scenarioBeforeDrops, typeof(MainWindow).GetField("worldAnimationScenarioEpoch", flags)!.GetValue(main));
            Assert.Equal(17, Assert.IsType<WorldAnimationScenario>(typeof(MainWindow).GetField("worldAnimationConfiguration", flags)!.GetValue(main)).Seed);
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
                    Assert.Null(typeof(MainWindow).GetField("worldAnimationCandidate", flags)!.GetValue(main));
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
            Assert.Empty(session.Scenario.Policy.IncludedCalls);
            Assert.True(session.IsAttached);
            Assert.False(((Control)main.FindName("FlyEnabled")).IsEnabled);
            Assert.Equal("stale_preview", (await Job("animation_transport", new() { ["preview"] = mapPreview, ["action"] = "pause" }, "failed"))["code"]!.GetValue<string>());
            Assert.IsType<DefinitionEntry>(((SceneTreeModel)typeof(MainWindow).GetField("sceneTree", flags)!.GetValue(main)!).Selected!.Section);
            await Timeline("scenario");
            Assert.Contains(Descendants(card).OfType<Expander>(), e => e.Header as string == "Scenario");
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
            Assert.Null(typeof(MainWindow).GetField("worldAnimationCandidate", flags)!.GetValue(main));
            Assert.NotNull(typeof(MainWindow).GetField("worldAnimationPinned", flags)!.GetValue(main));
            Assert.True(session.IsAttached);
            // Reselecting the already cached definition restores only the candidate; it must not replace or run the pinned session.
            var currentTree = (SceneTreeModel)typeof(MainWindow).GetField("sceneTree", flags)!.GetValue(main)!;
            var cachedAnimationRow = currentTree.FindSection(card.Entry!.Key)!;
            typeof(MainWindow).GetMethod("SelectSectionRow", flags)!.Invoke(main, [cachedAnimationRow, false]);
            Assert.NotNull(typeof(MainWindow).GetField("worldAnimationCandidate", flags)!.GetValue(main));
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
            await Timeline("include", new() { ["root"] = deliveryEntry, ["path"] = calls[0]!["Path"]!.GetValue<string>(), ["included"] = true });
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
            var instances = await Call("animation_runtime", new() { ["preview"] = animationPreview, ["section"] = "instances" });
            Assert.NotEmpty(instances["items"]!.AsArray());
            await Call("animation_runtime", new() { ["preview"] = animationPreview, ["section"] = "conditions" });
            typeof(MainWindow).GetMethod("ClearSectionSelection", flags)!.Invoke(main, []);
            await Timeline("exit");
            Assert.Equal("stale_preview", (await Job("animation_transport", new() { ["preview"] = animationPreview, ["action"] = "pause" }, "failed"))["code"]!.GetValue<string>());
            var changes = new[] { new { field, values = new[] { "ON_STARTUP" } } };
            Assert.Equal("context_changed", (await Job("source_animation", Args("set", new() { ["changes"] = changes }), "failed"))["code"]!.GetValue<string>());
            Assert.False(card.HasDraft);
            var firstMap = doc;
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
