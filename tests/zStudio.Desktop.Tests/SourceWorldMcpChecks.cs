using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Desktop;
using zStudio.Mcp;
using zStudio.Rendering;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>Source worlds through the real named-pipe MCP connection: a model from another mission, undo, save and reload.</summary>
internal static class SourceWorldMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);

            var none = await Job("source_world_open", new() { ["mission"] = "m1" }, "failed"); Assert.Equal("no_project", none["code"]!.GetValue<string>());
            await main.ViewModel.OpenRootAsync(fixture.Root, token);
            Assert.Contains(main.ViewModel.Files, f => f.RelativePath == Path.Combine("project", "gamegen", "m1.gs"));
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            Assert.Empty(Directory.EnumerateFiles(fixture.Project, "*.zbd", SearchOption.AllDirectories));
            Assert.True(main.ViewModel.IsSourceProject);
            Assert.Empty(main.ViewModel.Files); Assert.Empty(main.ViewModel.Folders);
            Assert.True(main.ViewModel.SearchIndexComplete);
            Assert.Equal(["m1", "m2"], (await Call("source_world_list", new()))["items"]!.AsArray().Select(m => m!["mission"]!.GetValue<string>()));
            Assert.Equal("source_map_required", (await Job("open_document", new() { ["path"] = fixture.Path("gamegen/m1.gs") }, "failed"))["code"]!.GetValue<string>());
            var unknown = await Job("source_world_open", new() { ["mission"] = "m9" }, "failed"); Assert.Equal("invalid_argument", unknown["code"]!.GetValue<string>());
            AssertPickerLoading(false);
            var mapButtons = (StackPanel)main.FindName("SourceMapButtons");
            while (mapButtons.Children.Count == 0) await Task.Delay(10, token);
            // Maps 1–6 are single-player and the rest multiplayer, each a six-column group.
            var mapGroups = mapButtons.Children.OfType<StackPanel>().Select(g => g.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>().Single()).ToArray();
            Assert.All(mapGroups, g => Assert.Equal(6, g.Columns));
            Assert.Equal(["m1", "m2"], mapGroups[0].Children.OfType<Button>().Select(b => ((TextBlock)b.Content).Text));
            Assert.All(mapGroups.SelectMany(g => g.Children.OfType<Button>()).Where(b => b.IsEnabled), b => Assert.Null(b.ToolTip));

            // Hold the first mission scan, before any source workspace or document is acquired. Even reopening
            // the same path replaces its lifetime; the old GUI request must not adopt that new generation.
            var readMissions = main.ReadSourceWorldMissionsAsync;
            TaskCompletionSource scanEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<IReadOnlyList<string>> scanRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken scanToken = default;
            main.ReadSourceWorldMissionsAsync = (_, ct) => { scanToken = ct; scanEntered.SetResult(); return scanRelease.Task; };
            try
            {
                var opening = SourceTask<DocumentModel>("OpenSourceWorldAsync", "m1", token, false);
                await scanEntered.Task.WaitAsync(token);
                AssertPickerLoading(true);
                long generation = main.ViewModel.WorkspaceGeneration;
                await main.ViewModel.OpenRootAsync(fixture.Project, token);
                Assert.True(main.ViewModel.WorkspaceGeneration > generation); Assert.True(scanToken.IsCancellationRequested);
                string status = main.ViewModel.Status;
                scanRelease.SetResult(["m1", "m2"]); // Deliberately ignore cancellation to exercise the publication guard.
                Assert.Equal("context_changed", (await Assert.ThrowsAsync<StudioCommandException>(() => opening)).Code);
                Assert.Empty(main.ViewModel.Documents); Assert.Null(main.ViewModel.SelectedDocument);
                Assert.Null(typeof(MainWindow).GetField("sourceWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main));
                Assert.Equal(status, main.ViewModel.Status);
                AssertPickerLoading(false);
            }
            finally { main.ReadSourceWorldMissionsAsync = readMissions; scanRelease.TrySetResult([]); }

            // A newer root owns navigation as soon as its asynchronous preflight begins, before its
            // WorkspaceGeneration changes. Protect both an existing activation and a newly built document.
            await SupersededOpening(existing: true);
            await SupersededOpening(existing: false);

            // Two world requests also reserve their order before either mission scan finishes.
            // Releasing the older scan first must not let it displace the newer request.
            TaskCompletionSource<IReadOnlyList<string>> firstScan = new(TaskCreationOptions.RunContinuationsAsynchronously),
                lastScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int scans = 0;
            main.ReadSourceWorldMissionsAsync = (_, _) => ++scans == 1 ? firstScan.Task : lastScan.Task;
            try
            {
                var first = SourceTask<DocumentModel>("OpenSourceWorldAsync", "m1", token, false);
                var last = SourceTask<DocumentModel>("OpenSourceWorldAsync", "m1", token, false);
                Assert.Equal(2, scans);
                firstScan.SetResult(["m1", "m2"]);
                Assert.Equal("context_changed", (await Assert.ThrowsAsync<StudioCommandException>(() => first)).Code);
                Assert.Empty(main.ViewModel.Documents);
                AssertPickerLoading(true); // Older completion must not remove the newer opening's feedback.
                lastScan.SetResult(["m1", "m2"]);
                Assert.Same(await last, Assert.Single(main.ViewModel.Documents));
                AssertPickerLoading(false);
            }
            finally { main.ReadSourceWorldMissionsAsync = readMissions; firstScan.TrySetResult([]); lastScan.TrySetResult([]); }
            await main.ViewModel.OpenRootAsync(fixture.Project, token);

            // Maps supersedes a pending opening even before it has acquired a document.
            TaskCompletionSource<IReadOnlyList<string>> mapsScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.ReadSourceWorldMissionsAsync = (_, _) => mapsScan.Task;
            try
            {
                var pendingOpening = SourceTask<DocumentModel>("OpenSourceWorldAsync", "m1", token, false);
                AssertPickerLoading(true);
                Assert.True(await SourceTask<bool>("ReturnToSourceMapsAsync", true, token));
                AssertPickerLoading(false);
                mapsScan.SetResult(["m1", "m2"]);
                Assert.Equal("context_changed", (await Assert.ThrowsAsync<StudioCommandException>(() => pendingOpening)).Code);
                Assert.Empty(main.ViewModel.Documents);
            }
            finally { main.ReadSourceWorldMissionsAsync = readMissions; mapsScan.TrySetResult([]); }

            // Cancellation before publication restores the picker without leaving a loading overlay.
            using (var canceled = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                TaskCompletionSource<IReadOnlyList<string>> canceledScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
                main.ReadSourceWorldMissionsAsync = (_, _) => canceledScan.Task;
                try
                {
                    var opening = SourceTask<DocumentModel>("OpenSourceWorldAsync", "m1", canceled.Token, false);
                    AssertPickerLoading(true);
                    canceled.Cancel(); canceledScan.SetResult(["m1", "m2"]);
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
                    AssertPickerLoading(false); Assert.Empty(main.ViewModel.Documents);
                }
                finally { main.ReadSourceWorldMissionsAsync = readMissions; canceledScan.TrySetResult([]); }
            }

            void AssertPickerLoading(bool loading)
            {
                var message = (TextBlock)main.FindName("SourceMapLoading");
                Assert.Equal("Loading...", message.Text);
                Assert.Equal(loading ? Visibility.Visible : Visibility.Collapsed, message.Visibility);
                Assert.Equal(loading ? Visibility.Collapsed : Visibility.Visible, ((FrameworkElement)main.FindName("SourceMapPickerContent")).Visibility);
                if (loading) Assert.Equal(Visibility.Visible, ((ScrollViewer)main.FindName("SourceMapPicker")).Visibility);
                else if (main.ViewModel.SelectedDocument != null)
                {
                    Assert.Equal(Visibility.Collapsed, ((ScrollViewer)main.FindName("SourceMapPicker")).Visibility);
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)main.FindName("Workspace")).Visibility);
                }
            }

            // Tools shows the source commands (maps open from the map picker).
            typeof(MainWindow).GetMethod("ToolsMenuOpened", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, main)]);
            var addModel = (MenuItem)main.FindName("AddSourceModelMenu");
            Assert.Equal(Visibility.Visible, addModel.Visibility); Assert.False(addModel.IsEnabled);

            // The world is evaluated from sources with source editing tools.
            var opened = (await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!;
            var doc = Document(opened);
            Assert.Equal("m1", opened["sourceWorld"]!["mission"]!.GetValue<string>());
            var sourceState = opened["sourceWorld"]!.AsObject();
            Assert.False(sourceState["evaluating"]!.GetValue<bool>());
            Assert.NotNull(sourceState["sourceRevision"]); Assert.NotNull(sourceState["diagnostics"]);
            Assert.False(sourceState.ContainsKey("rebuilding")); Assert.False(sourceState.ContainsKey("builtRevision")); Assert.False(sourceState.ContainsKey("outputs"));
            Assert.Equal("m1 world (sources)", doc.Title);
            // The document identity is the actual mission script; no preview archive exists.
            Assert.Equal(fixture.Path("gamegen/m1.gs"), doc.Path, ignoreCase: true);
            Assert.Null(doc.ModelEdits);
            await Preview();
            // The map's back arrow replaces Maps; a source world has no Unlock editing, Scene or Selection menus: the card's Edit starts each edit.
            Assert.Equal(Visibility.Visible, ((FrameworkElement)main.FindName("SourceMapsButton")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)main.FindName("PickupTools")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)main.FindName("SceneMenus")).Visibility);
            Assert.False(doc.EditingLocked);
            Assert.Contains("unsupported", (await Call("scene_lock", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["locked"] = true }, true)).GetValue<string>());
            // Validation must follow the referenced sources, not merely parse the entry script.
            string validationModel = fixture.Path("data/m1/models/m1.gltf");
            byte[] validationBytes = await File.ReadAllBytesAsync(validationModel, token);
            var validationStamp = File.GetLastWriteTimeUtc(validationModel);
            try
            {
                await File.WriteAllTextAsync(validationModel, "{}", token);
                await Job("validate", new() { ["document"] = Id(doc) }, "failed");
                Assert.Empty(Directory.EnumerateFiles(fixture.Project, "*.zbd", SearchOption.AllDirectories));
            }
            finally { await File.WriteAllBytesAsync(validationModel, validationBytes, token); File.SetLastWriteTimeUtc(validationModel, validationStamp); }
            // Opening it again activates the same document.
            Assert.Equal(Id(doc), (await Job("source_world_open", new() { ["mission"] = "M1" }))["document"]!["id"]!.GetValue<string>());

            // Replacing a clean map keeps exactly one document and rejects the old lifetime identity.
            var replaced = doc;
            await Job("source_world_open", new() { ["mission"] = "m2" });
            Assert.True(replaced.IsDisposed); Assert.Single(main.ViewModel.Documents);
            doc = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            Assert.Single(main.ViewModel.Documents);

            var models = await Call("source_world_models", new() { ["query"] = "bft" });
            Assert.Equal("data/m2/models/bft/tank.gltf", models["items"]![0]!["path"]!.GetValue<string>());
            var definitions = await Call("source_world_definitions", new() { ["document"] = Id(doc), ["name"] = "tank" });
            Assert.Equal(SourceWorldFixture.TankDefinitions, definitions["files"]![0]!["path"]!.GetValue<string>());

            // Adding a model from m2 rebuilds the world into a replacement document; the old one is released.
            var added = Document((await Job("source_world_add_model", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["model"] = fixture.Tank, ["name"] = "tank" }))["document"]!);
            Assert.True(doc.IsDisposed); Assert.NotEqual(doc.SessionId, added.SessionId);
            Assert.True(File.Exists(doc.Path));
            Assert.True(added.IsDirty); Assert.Equal("m1 world (sources) *", added.Title);
            Assert.Equal("unsaved_changes", (await Job("source_world_open", new() { ["mission"] = "m2" }, "failed"))["code"]!.GetValue<string>());
            Assert.Same(added, Assert.Single(main.ViewModel.Documents));
            Assert.Contains(added.PreviewDocument.Scene!.Nodes, n => n.Name == "tank");
            // One change of the project's workspace: the script and the animation list.
            Assert.Equal(["data/m1/zrdr/anim.zad", "gamegen/m1.gs"], added.SourceWorld!.Workspace.History.Single().Files.Select(f => f.Relative).Order(StringComparer.Ordinal));
            Assert.Contains("enemies\\\\tank.zad", System.Text.Encoding.Latin1.GetString(added.SourceWorld.Workspace.Read("data/m1/zrdr/anim.zad")!));
            var stale = await Job("source_world_add_model", new() { ["document"] = Id(doc), ["revision"] = 0, ["model"] = fixture.Tank, ["name"] = "tank2" }, "failed");
            Assert.Equal("stale_document", stale["code"]!.GetValue<string>());
            var bad = await Job("source_world_add_model", new() { ["document"] = Id(added), ["revision"] = added.Revision, ["model"] = "gamegen/m1.gs", ["name"] = "x" }, "failed");
            Assert.Equal("invalid_argument", bad["code"]!.GetValue<string>());
            var placed = Document((await Job("source_world_add_model", new()
            {
                ["document"] = Id(added), ["revision"] = added.Revision, ["model"] = fixture.Tank, ["name"] = "tank_wreck",
                ["position"] = new Dictionary<string, object?> { ["x"] = 100, ["y"] = 0, ["z"] = -50 }, ["heading"] = 90, ["definitionFiles"] = Array.Empty<string>()
            }))["document"]!);
            await Preview();
            Assert.Contains(placed.PreviewDocument.Scene!.Nodes, n => n.Name == "tank_wreck");
            // Parts is presentation-only across viewport replacement.
            Assert.False(placed.EditingLocked);
            int wreck = placed.PreviewDocument.Scene.Nodes.Single(n => n.Name == "tank_wreck").Index;
            string partsPreview = (await Call("state", new()))["preview"]!.GetValue<string>();
            var parts = await Call("source_map_parts", new() { ["preview"] = partsPreview, ["owner"] = wreck });
            var hull = Assert.Single(parts["children"]!["items"]!.AsArray())!;
            int hullNode = hull["Node"]!.GetValue<int>();
            Assert.True(hull["Editable"]!.GetValue<bool>()); Assert.True(hull["DefaultVisible"]!.GetValue<bool>());
            await Call("scene_selection", new() { ["preview"] = partsPreview, ["action"] = "select", ["node"] = hullNode });
            var viewport = (SceneViewport)typeof(MainWindow).GetField("scene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.Equal(wreck, viewport.SelectedInspection?.SelectionNode); Assert.Equal(hullNode, viewport.SelectedInspection?.Node);
            Assert.Equal(fixture.Tank, (await Job("source_blender_checkout", new() { ["document"] = Id(placed), ["node"] = hullNode }))["model"]!.GetValue<string>());
            var partsHistory = (placed.Revision, placed.SourceWorld!.Workspace.UndoCount, placed.SourceWorld.Workspace.CanRedo, placed.IsDirty);
            byte[] originalModel = File.ReadAllBytes(fixture.Path(fixture.Tank));
            partsPreview = (await Job("source_map_parts_visibility", new()
            {
                ["preview"] = partsPreview, ["document"] = Id(placed), ["revision"] = placed.Revision,
                ["owner"] = wreck, ["action"] = "set", ["part"] = hullNode, ["visible"] = false
            }))["preview"]!.GetValue<string>();
            parts = await Call("source_map_parts", new() { ["preview"] = partsPreview, ["owner"] = wreck });
            hull = Assert.Single(parts["children"]!["items"]!.AsArray())!;
            Assert.False(hull["Visible"]!.GetValue<bool>()); Assert.False(hull["EffectiveVisible"]!.GetValue<bool>());
            await Call("scene_selection", new() { ["preview"] = partsPreview, ["action"] = "isolate", ["node"] = wreck });
            viewport = (SceneViewport)typeof(MainWindow).GetField("scene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            viewport.RestoreView(new(new(120, 340, 560), new(0, 0, -10), new(0, 1, 0), 60));
            var partsCamera = viewport.CaptureView();
            partsPreview = (await Job("source_map_parts_visibility", new()
            {
                ["preview"] = partsPreview, ["document"] = Id(placed), ["revision"] = placed.Revision, ["owner"] = wreck, ["action"] = "reset"
            }))["preview"]!.GetValue<string>();
            parts = await Call("source_map_parts", new() { ["preview"] = partsPreview, ["owner"] = wreck });
            hull = Assert.Single(parts["children"]!["items"]!.AsArray())!;
            Assert.True(hull["Visible"]!.GetValue<bool>()); Assert.True(hull["EffectiveVisible"]!.GetValue<bool>());
            viewport = (SceneViewport)typeof(MainWindow).GetField("scene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.Equal(partsCamera, viewport.CaptureView());
            await Call("scene_selection", new() { ["preview"] = partsPreview, ["action"] = "show_all" });
            Assert.Equal(partsHistory, (placed.Revision, placed.SourceWorld.Workspace.UndoCount, placed.SourceWorld.Workspace.CanRedo, placed.IsDirty));
            Assert.Equal(originalModel, File.ReadAllBytes(fixture.Path(fixture.Tank)));
            // Exports read the scripts on disk, so the pending additions must be saved or discarded first.
            var unsaved = await Job("source_export", new() { ["outputs"] = new[] { "m1/gamez.zbd" } }, "failed");
            Assert.Equal("unsaved_changes", unsaved["code"]!.GetValue<string>()); Assert.Contains("m1 world", unsaved["message"]!.GetValue<string>());

            // A placed model cannot be named like a node of its own (the tank's root node is "hull"): AddChild finds the
            // newest node with the name, which would attach that inner node and leave the placed root out of the world.
            var shadowed = await Job("source_world_add_model", new()
            {
                ["document"] = Id(placed), ["revision"] = placed.Revision, ["model"] = fixture.Tank, ["name"] = "hull",
                ["position"] = new Dictionary<string, object?> { ["x"] = 10, ["y"] = 0, ["z"] = 10 }, ["definitionFiles"] = Array.Empty<string>()
            }, "failed");
            Assert.Equal("build_failed", shadowed["code"]!.GetValue<string>()); Assert.Contains("hull", shadowed["message"]!.GetValue<string>());
            Assert.False(placed.IsDisposed); Assert.Equal(2, placed.SourceWorld!.Workspace.UndoCount); Assert.False(placed.SourceWorld!.Workspace.CanRedo);

            // An edit canceled while its world rebuilds is withdrawn: the shown world never had it.
            using (CancellationTokenSource cancel = new())
            {
                var canceled = SourceTask<DocumentModel>("AddSourceModelAsync", placed, new SourceWorldAddition(new(fixture.Tank, "tank_canceled"), []), cancel.Token);
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            }
            Assert.False(placed.IsDisposed); Assert.Equal(2, placed.SourceWorld!.Workspace.UndoCount); Assert.False(placed.SourceWorld!.Workspace.CanRedo);

            // While an edit is prepared or rebuilds the world, nothing else edits or saves it: an edit it cannot
            // be built with (here a malformed glTF) never reaches the project or changes accepted history.
            string scriptPath = fixture.Path("gamegen/m1.gs"); byte[] scriptBefore = await File.ReadAllBytesAsync(scriptPath, token);
            fixture.Write("data/m1/models/broken.gltf", "{");
            var failing = SourceTask<DocumentModel>("AddSourceModelAsync", placed, new SourceWorldAddition(new("data/m1/models/broken.gltf", "broken"), []), CancellationToken.None);
            Assert.True(placed.SourceWorld!.IsRebuilding); Assert.Same(placed, main.ViewModel.SelectedDocument);
            foreach (string command in new[] { "DocumentSave", "DocumentUndo" }) Assert.False(((UIElement)main.FindName(command)).IsEnabled, command);
            Assert.Equal("busy", (await Assert.ThrowsAsync<StudioCommandException>(async () => await SourceTask<IReadOnlyList<string>>("SaveSourceWorldAsync", placed, CancellationToken.None))).Code);
            Assert.Equal("busy", (await Assert.ThrowsAsync<StudioCommandException>(() => SourceTask<DocumentModel>("UndoSourceWorldAsync", placed, false, CancellationToken.None))).Code);
            Assert.Equal("build_failed", (await Assert.ThrowsAsync<StudioCommandException>(() => failing)).Code);
            Assert.Equal(scriptBefore, await File.ReadAllBytesAsync(scriptPath, token));
            Assert.False(placed.IsDisposed); Assert.Equal(2, placed.SourceWorld!.Workspace.UndoCount); Assert.False(placed.SourceWorld!.Workspace.CanRedo);
            Assert.False(placed.SourceWorld!.IsRebuilding);
            foreach (string command in new[] { "DocumentSave", "DocumentUndo" }) Assert.True(((UIElement)main.FindName(command)).IsEnabled, command);
            File.Delete(fixture.Path("data/m1/models/broken.gltf"));

            // Undo and redo rebuild too.
            var undone = Document(await Call("undo_redo", new() { ["document"] = Id(placed), ["revision"] = placed.Revision, ["action"] = "undo" }));
            Assert.DoesNotContain(undone.PreviewDocument.Scene!.Nodes, n => n.Name == "tank_wreck");
            var redone = Document(await Call("undo_redo", new() { ["document"] = Id(undone), ["revision"] = undone.Revision, ["action"] = "redo" }));
            Assert.Contains(redone.PreviewDocument.Scene!.Nodes, n => n.Name == "tank_wreck");

            // Save writes the script and the animation list; there is no Save As.
            var saveAs = await Job("save_document", new() { ["document"] = Id(redone), ["revision"] = redone.Revision, ["destination"] = Path.Combine(fixture.Root, "x.zbd") }, "failed");
            Assert.Equal("invalid_argument", saveAs["code"]!.GetValue<string>());
            var saved = await Job("save_document", new() { ["document"] = Id(redone), ["revision"] = redone.Revision });
            Assert.Equal(["data/m1/zrdr/anim.zad", "gamegen/m1.gs"], saved["written"]!.AsArray().Select(w => w!.GetValue<string>()).Order(StringComparer.Ordinal));
            Assert.Empty(new SourcePublisher(fixture.Project).FindInterrupted(token));
            Assert.False(redone.IsDirty);
            string script = fixture.Path("gamegen/m1.gs");
            Assert.Contains("LoadGameGen tank.gltf tank\r\n", await File.ReadAllTextAsync(script, token));

            // A file touched by another world must not invalidate this build.
            const string unrelated = "data/m2/models/unrelated.gltf";
            fixture.Write(unrelated, """{"asset":{"version":"2.0"},"nodes":[]}""");
            var workspace = redone.SourceWorld!.Workspace;
            var unrelatedBytes = workspace.Read(unrelated, token)!;
            Assert.DoesNotContain(unrelated, redone.SourceBuild!.Dependencies);
            workspace.Apply("Another world edit", [(unrelated, unrelatedBytes.Concat(new byte[] { 32 }).ToArray())], token);
            await workspace.SaveAsync(token);
            await File.AppendAllTextAsync(fixture.Path(unrelated), "# outside m1\\r\\n", token);
            Assert.Contains(unrelated, workspace.ExternalChanges());
            await main.ViewModel.CheckExternalChangesAsync(); Assert.False(redone.IsStale);
            Assert.False(await Task.Run(() => redone.SourceInputsChanged(token), token));
            await File.WriteAllBytesAsync(fixture.Path(unrelated), unrelatedBytes, token);
            await workspace.ReloadAsync(token);

            // A changed model marks the world stale; reloading rebuilds it from disk.
            await main.ViewModel.CheckExternalChangesAsync(); Assert.False(redone.IsStale);
            File.SetLastWriteTimeUtc(fixture.Path(fixture.Tank), DateTime.UtcNow.AddMinutes(2));
            await main.ViewModel.CheckExternalChangesAsync(); Assert.True(redone.IsStale);
            var reloaded = Document(await Job("reload_document", new() { ["document"] = Id(redone), ["revision"] = redone.Revision }));
            Assert.False(reloaded.IsStale); Assert.Contains(reloaded.PreviewDocument.Scene!.Nodes, n => n.Name == "tank_wreck");
            // A touched, saved source can change without changing its file stamp. Reload must see its new authoritative bytes.
            var sourceStamp = File.GetLastWriteTimeUtc(script);
            string beforeSameStamp = await File.ReadAllTextAsync(script, token);
            string afterSameStamp = beforeSameStamp.Replace("set worldName world", "set worldName other", StringComparison.Ordinal);
            Assert.NotEqual(beforeSameStamp, afterSameStamp);
            await File.WriteAllTextAsync(script, afterSameStamp, token); File.SetLastWriteTimeUtc(script, sourceStamp);
            Assert.Contains("gamegen/m1.gs", reloaded.SourceWorld!.Workspace.ExternalChanges());
            reloaded = Document(await Job("reload_document", new() { ["document"] = Id(reloaded), ["revision"] = reloaded.Revision }));
            Assert.Contains(reloaded.PreviewDocument.Scene!.Nodes, n => n.Name == "other");
            Assert.False(reloaded.IsStale);

            // After the script changes on disk, the pending edits cannot be kept and reloading starts from the file.
            var pending = Document((await Job("source_world_add_model", new() { ["document"] = Id(reloaded), ["revision"] = reloaded.Revision, ["model"] = "data/m1/models/m1.gltf", ["name"] = "extra", ["definitionFiles"] = Array.Empty<string>() }))["document"]!);
            // The GUI's Reload found no conflict, so it asked nothing; the script changing before the rebuild's own check
            // refuses the reload instead of dropping the edits unasked.
            Assert.Same(pending, main.ViewModel.SelectedDocument);
            var rebuild = main.ViewModel.ReloadSourceWorld!;
            main.ViewModel.ReloadSourceWorld = async (d, accepted, t) =>
            {
                await File.AppendAllTextAsync(script, "# elsewhere\r\n", t); File.SetLastWriteTimeUtc(script, DateTime.UtcNow.AddMinutes(3));
                return await rebuild(d, accepted, t);
            };
            // A source map's reload failure goes to its caller (the GUI lists it and opens Diagnostics).
            try { Assert.Contains("changed on disk while the workspace holds unsaved edits", (await Assert.ThrowsAsync<zStudio.Automation.StudioCommandException>(() => main.ViewModel.ReloadAsync())).Message); }
            finally { main.ViewModel.ReloadSourceWorld = rebuild; }
            Assert.False(pending.IsDisposed); Assert.True(pending.SourceWorld!.Workspace.IsFileDirty("gamegen/m1.gs"));
            var refused = await Job("reload_document", new() { ["document"] = Id(pending), ["revision"] = pending.Revision }, "failed");
            Assert.Equal("unsaved_changes", refused["code"]!.GetValue<string>());
            var conflict = await Job("save_document", new() { ["document"] = Id(pending), ["revision"] = pending.Revision }, "failed");
            Assert.Equal("external_change", conflict["code"]!.GetValue<string>());
            Assert.EndsWith("# elsewhere\r\n", await File.ReadAllTextAsync(script, token));
            var undoneExtra = Document(await Call("undo_redo", new() { ["document"] = Id(pending), ["revision"] = pending.Revision, ["action"] = "undo" }));
            var fresh = Document(await Job("reload_document", new() { ["document"] = Id(undoneExtra), ["revision"] = undoneExtra.Revision }));
            Assert.False(fresh.SourceWorld!.Workspace.CanUndo); Assert.Contains(fresh.PreviewDocument.Scene!.Nodes, n => n.Name == "tank");

            // Closing the world removes its private files.
            await Call("close_document", new() { ["document"] = Id(fresh), ["revision"] = fresh.Revision });

            async Task Preview()
            {
                var work = (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                await work.WaitAsync(token);
            }
            async Task SupersededOpening(bool existing)
            {
                if (existing)
                {
                    await Job("source_world_open", new() { ["mission"] = "m1" });
                    main.ViewModel.SelectedDocument = null;
                }
                var checkRoot = main.ViewModel.CheckRootExistsAsync;
                TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously),
                    release = new(TaskCreationOptions.RunContinuationsAsynchronously),
                    rootEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> rootRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (existing) main.ReadSourceWorldMissionsAsync = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return ["m1", "m2"]; };
                else main.SourceBuildStep = p => { if (p.Item == "Built") { entered.TrySetResult(); release.Task.Wait(token); } };
                main.ViewModel.CheckRootExistsAsync = (_, _) => { rootEntered.TrySetResult(); return rootRelease.Task; };
                Task? newer = null;
                try
                {
                    var opening = Job("source_world_open", new() { ["mission"] = "m1" }, "failed");
                    await entered.Task.WaitAsync(token);
                    long generation = main.ViewModel.WorkspaceGeneration;
                    newer = main.ViewModel.OpenRootAsync(fixture.Project, token);
                    await rootEntered.Task.WaitAsync(token);
                    Assert.Equal(generation, main.ViewModel.WorkspaceGeneration);
                    main.ViewModel.Status = "Newer root is checking";
                    release.SetResult();
                    Assert.Equal("context_changed", (await opening)["code"]!.GetValue<string>());
                    Assert.Null(main.ViewModel.SelectedDocument);
                    Assert.Equal(existing ? 1 : 0, main.ViewModel.Documents.Count);
                    Assert.Equal("Newer root is checking", main.ViewModel.Status);
                    rootRelease.SetResult(true); await newer;
                    Assert.True(main.ViewModel.WorkspaceGeneration > generation);
                    Assert.Empty(main.ViewModel.Documents);
                }
                finally
                {
                    main.ReadSourceWorldMissionsAsync = readMissions; main.SourceBuildStep = null;
                    main.ViewModel.CheckRootExistsAsync = checkRoot;
                    release.TrySetResult(); rootRelease.TrySetResult(true);
                    if (newer != null) await newer;
                }
            }
            // The GUI's own entry points, called on the dispatcher as its commands call them.
            Task<T> SourceTask<T>(string method, params object[] arguments)
            {
                try { return (Task<T>)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, arguments)!; }
                catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException); throw; }
            }
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
            static string Id(DocumentModel d) => d.SessionId.ToString();
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, bool error = false)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == error, text);
                return error ? JsonValue.Create(text)! : JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
