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
            var records = (await Animation("read"))["items"]!.AsArray();
            var activation = records[0]!["Fields"]!.AsArray().Single(f => f!["Key"]!.GetValue<string>() == "ACTIVATION")!;
            string field = activation["Id"]!.GetValue<string>();
            await Animation("begin");
            var card = (SourceSectionCard)typeof(MainWindow).GetField("sectionCard", flags)!.GetValue(main)!;
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
            // A restored card may still be analyzing automatically; explicitly await the analysis whose markers we use.
            var paths = await Job("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["definition"] = 0, ["action"] = "analyze" });
            Assert.NotEmpty(paths["paths"]!["definitions"]!.AsArray());
            Assert.True(paths["parachute"]?["windows"] is JsonArray, paths.ToJsonString() + " · " + main.ViewModel.Status);
            var panel = (FrameworkElement)main.FindName("ParachutePanel");
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
            // A viewport pick while the specialized flight analysis is pending owns the newer selection.
            TaskCompletionSource analyzing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new SemaphoreSlim(0))
            {
                main.ParachuteAnalysisPreparing = ct => { analyzing.TrySetResult(); release.Wait(ct); };
                try
                {
                    current = await Call("state", new());
                    var pending = await Call("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["definition"] = 0, ["action"] = "analyze" });
                    await analyzing.Task.WaitAsync(token);
                    var viewport = Assert.IsType<zStudio.Rendering.SceneViewport>(((ContentControl)main.FindName("SceneHost")).Content);
                    int ground = viewport.PreviewScene!.Nodes.Single(n => n.Name == "ground").Index;
                    Assert.True(viewport.SelectInspectionNode(ground));
                    release.Release();
                    string operation = pending["id"]!.GetValue<string>();
                    while (pending["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); pending = await Call("operation", new() { ["id"] = operation }); }
                    Assert.NotEqual("completed", pending["State"]!.GetValue<string>());
                    Assert.Equal(ground, viewport.SelectedInspection!.SelectionNode);
                }
                finally { release.Release(); main.ParachuteAnalysisPreparing = null; }
            }
            current = await Call("state", new());
            string mapPreview = current["preview"]!.GetValue<string>();
            await Job("animation_paths", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["preview"] = current["preview"]!.GetValue<string>(), ["definition"] = 0, ["action"] = "preview" });
            var editor = Assert.IsType<AnimationEditor>(((ContentControl)main.FindName("AnimationHost")).Content);
            Assert.False(editor.Options.Collision);
            Assert.False(((Control)main.FindName("FlyEnabled")).IsEnabled);
            Assert.Equal("stale_preview", (await Job("animation_transport", new() { ["preview"] = mapPreview, ["action"] = "pause" }, "failed"))["code"]!.GetValue<string>());
            Assert.IsType<DefinitionEntry>(((SceneTreeModel)typeof(MainWindow).GetField("sceneTree", flags)!.GetValue(main)!).Selected!.Section);
            var range = Descendants(editor).OfType<Button>().Single(b => b.Content as string == "Range…");
            range.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, range));
            Assert.NotNull(editor.PreviewSetupView.Parent);
            main.SetPickupsByType(false);
            Assert.True(((TreeView)main.FindName("DocumentSceneTree")).IsEnabled);
            current = await Call("state", new());
            string animationPreview = current["preview"]!.GetValue<string>();
            await Job("scene_options", new() { ["preview"] = current["preview"]!.GetValue<string>(), ["changes"] = new { wireframe = true, textures = false, faceOrientation = true } });
            Assert.True(editor.Viewport.Wireframe); Assert.True(editor.Viewport.FaceOrientationEnabled);
            typeof(MainWindow).GetMethod("ClearSectionSelection", flags)!.Invoke(main, []);
            Assert.Null(((ContentControl)main.FindName("AnimationHost")).Content);
            Assert.True(((Control)main.FindName("FlyEnabled")).IsEnabled);
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
