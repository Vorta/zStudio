using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core.Sources;
using zStudio.Desktop;
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>
/// Source-world editing through the real named-pipe MCP connection: a pickup move that changes only the coordinate tokens
/// of its text sources (with its difficulty counterpart), project-wide undo, a world object moved and flagged through its
/// glTF node, and one save of every changed file.
/// </summary>
internal static class SourceEditingMcpChecks
{
    private const string Default = "# pickups\r\n(\r\n  ( HEMORTAR_AMMO 1 ( 12 8 -5 ) ( 0.0 0.0 0.0 ) 12.5 )   # hand-written\r\n  ( NANITE 50 ( 100.0 0.0 -100.0 ) ( 0.0 1.5707964 0.0 ) 30.0 )\r\n)\r\n";
    private const string Easy = "(\r\n  ( NANITE 80 ( 100.0 0.0 -100.0 ) ( 0.0 1.5707964 0.0 ) 30.0 )\r\n  ( HEMORTAR_AMMO 5 ( 12 8 -5 ) ( 0.0 0.0 0.0 ) 12.5 )\r\n)\r\n";

    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        fixture.WriteReferencedTurretDatabase();
        fixture.Write("data/m1/zrdr/puppies.zrd", Default); fixture.Write("data/m1/zrdr/puppies_easy.zrd", Easy);
        const string aiSource = "node_00 ( 12 ( 1.0 2.0 3.0 ) ( -7 -1 0 ) )\nnode_01 ( 23 ( 5.0 2.0 7.0 ) ( 0 -1 -1 ) )\n";
        fixture.Write("data/m1/zrdr/net_01.zrd", aiSource);
        fixture.Write("data/m2/zrdr/aiv.zrd", "( tank_01 ( 0 ( 10.0 20.0 30.0 ) 90.0 ) )\r\n# AIV remains source text\r\n");
        fixture.Write("data/m2/zrdr/vehicle.zrd", "( tank ( ) )");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            // A save that stopped after committing (before cleaning up) is reported when the project first opens in a session.
            var crashed = new SourcePublisher(fixture.Project) { Fault = (step, _) => { if (step == "cleanup") throw new SourcePublisher.Crash(); } };
            Assert.Throws<SourcePublisher.Crash>(() => crashed.Publish([new("gamegen/crash_note.gs", null, Encoding.ASCII.GetBytes("# note" + Environment.NewLine))], "Crashed save", token));
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            for (int wait = 0; wait < 500 && !main.ViewModel.Problems.Any(p => p.Message.Contains("interrupted save", StringComparison.Ordinal)); wait++) await Task.Delay(10, token);
            Assert.Contains(main.ViewModel.Problems, p => p.Message.Contains("interrupted save", StringComparison.Ordinal));
            string crashId = (await Call("source_recovery", new()))["saves"]![0]!["id"]!.GetValue<string>();
            await Job("source_recovery_resolve", new() { ["save"] = crashId, ["action"] = "complete" });
            Assert.DoesNotContain(main.ViewModel.Problems, p => p.Message.Contains("interrupted save", StringComparison.Ordinal));
            var doc = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            await Preview();

            main.SourceEditPreparing = ct => Assert.False(main.Dispatcher.CheckAccess());

            // Pickups of a source world come directly from the resource sources; moving one changes their coordinate tokens.
            var pickups = await Job("pickups", new() { ["document"] = Id(doc) });
            var ammo = pickups["items"]!.AsArray().First(p => p!["Type"]!.GetValue<string>() == "HEMORTAR_AMMO" && p["source"]!["ResourceName"]!.GetValue<string>() == "PUPPIES.ZRD")!;
            // Exercise the actual placement planner after an external resource grows beyond its text allowance.
            // A bounded stream writes valid whitespace padding; the planner must refuse before reading it all.
            string resourcePath = fixture.Path("data/m1/zrdr/puppies.zrd");
            DateTime resourceStamp = File.GetLastWriteTimeUtc(resourcePath);
            var placementSource = doc.PickupEdits!.Records.First(r => r.Type == "HEMORTAR_AMMO" && r.Source.ResourceName == "PUPPIES.ZRD").Source;
            var planner = typeof(MainWindow).GetMethod("PlanSourcePlacement", BindingFlags.Static | BindingFlags.NonPublic)!;
            var placement = new zStudio.Core.PlacementTransform(new(20.25f, 8, -5), System.Numerics.Vector3.Zero);
            long beforeRevision = doc.SourceWorld!.Workspace.Revision;
            try
            {
                await Task.Run(() =>
                {
                    using (FileStream padding = new(resourcePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        padding.Write(Encoding.ASCII.GetBytes(Default));
                        byte[] spaces = new byte[64 * 1024]; Array.Fill(spaces, (byte)' ');
                        while (padding.Length <= SourceProject.MaximumSourceTextBytes)
                        {
                            token.ThrowIfCancellationRequested();
                            padding.Write(spaces.AsSpan(0, (int)Math.Min(spaces.Length, SourceProject.MaximumSourceTextBytes + 1L - padding.Length)));
                        }
                    }
                    byte[] beforeHash;
                    using (var input = File.OpenRead(resourcePath)) beforeHash = System.Security.Cryptography.SHA256.HashData(input);
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    var failure = Assert.Throws<TargetInvocationException>(() => planner.Invoke(null, [doc, doc.SourceWorld.Workspace, placementSource, placement, token]));
                    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    var refused = Assert.IsType<InvalidDataException>(failure.InnerException);
                    Assert.Contains("Resource text exceeds", refused.Message, StringComparison.Ordinal);
                    Assert.True(allocated < 4 * 1024 * 1024, $"Placement refusal allocated {allocated:N0} bytes before its text limit.");
                    Assert.Equal(SourceProject.MaximumSourceTextBytes + 1L, new FileInfo(resourcePath).Length);
                    using (var input = File.OpenRead(resourcePath)) Assert.Equal(beforeHash, System.Security.Cryptography.SHA256.HashData(input));
                }, token);
                Assert.Equal(beforeRevision, doc.SourceWorld.Workspace.Revision);
                Assert.Empty(doc.SourceWorld.Workspace.History);
                Assert.False(doc.SourceWorld.Workspace.IsDirty);
            }
            finally
            {
                await File.WriteAllTextAsync(resourcePath, Default, Encoding.ASCII, CancellationToken.None);
                File.SetLastWriteTimeUtc(resourcePath, resourceStamp);
            }
            Assert.False(await Task.Run(() => doc.SourceInputsChanged(token), token));
            // Hold actual scene-card source preparation, then type newer GUI input: stale work must retain that draft.
            var scene = (zStudio.Rendering.SceneViewport)typeof(MainWindow).GetField("scene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            scene.SetAiOptions(true, true, null);
            string cardPreview = ((Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).ToString();
            string target = "ai:" + scene.AiNetworks.Id + ":" + scene.AiNetworks.Networks[0].Nodes[0].Id;
            await Call("scene_card", new() { ["preview"] = cardPreview, ["action"] = "select", ["target"] = target });
            await Call("scene_card", new() { ["preview"] = cardPreview, ["action"] = "begin", ["document"] = Id(doc), ["revision"] = doc.Revision });
            var card = (SceneInspectionCard)scene.InspectionContent!;
            TaskCompletionSource preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var proceed = new SemaphoreSlim(0))
            {
                main.SourceEditPreparing = ct => { Assert.False(main.Dispatcher.CheckAccess()); preparing.TrySetResult(); Assert.True(proceed.Wait(TimeSpan.FromSeconds(20), ct)); };
                var apply = Call("scene_card", new() { ["preview"] = cardPreview, ["action"] = "apply", ["document"] = Id(doc), ["revision"] = doc.Revision, ["token"] = card.DraftToken }, error: true);
                try
                {
                    var first = await Task.WhenAny(preparing.Task, apply).WaitAsync(token);
                    if (first == apply) Assert.Fail("Scene-card apply completed before reaching preparation: " + (await apply).ToJsonString());
                    card.SetDraft(card.DraftToken, ["2", "3", "4"], null, null, null);
                    proceed.Release();
                    Assert.Contains("draft_conflict", (await apply).GetValue<string>());
                    Assert.True(card.HasDraft); Assert.False(doc.SourceWorld!.Workspace.IsDirty); Assert.Empty(doc.SourceWorld.Workspace.History);
                }
                finally { proceed.Release(); }
            }
            main.SourceEditPreparing = ct => Assert.False(main.Dispatcher.CheckAccess());
            await Call("scene_card", new() { ["preview"] = cardPreview, ["action"] = "cancel", ["document"] = Id(doc), ["revision"] = doc.Revision, ["token"] = card.DraftToken });
            await Call("scene_card", new() { ["preview"] = cardPreview, ["action"] = "clear" });

            // AI path confirmation uses the actual source preparation/rebuild pipeline, retaining both node edits
            // as one source history step. Its new document is returned and stale preview/edit identities expire.
            var pathArgs = PathArguments("enter"); pathArgs["network"] = scene.AiNetworks.Networks[0].Id;
            await Job("ai_path", pathArgs);
            pathArgs = PathArguments("select"); pathArgs["nodes"] = scene.AiNetworks.Networks[0].Nodes.Select(n => n.Id).ToArray();
            await Job("ai_path", pathArgs);
            pathArgs = PathArguments("begin"); pathArgs["mode"] = "move";
            var pathDraft = await Job("ai_path", pathArgs);
            string pathToken = pathDraft["path"]!["draft"]!["token"]!.GetValue<string>();
            pathArgs = PathArguments("set"); pathArgs["token"] = pathToken; pathArgs["input"] = new[] { "10", "0", "0" };
            await Job("ai_path", pathArgs);
            Assert.False(doc.SourceWorld!.Workspace.IsDirty);

            // Escape during source preparation explicitly discards the draft. The failed confirmation must
            // not resurrect it when the held worker returns. No physical input or unattended dialog is used.
            TaskCompletionSource pathPreparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var releasePath = new SemaphoreSlim(0))
            {
                main.SourceEditPreparing = ct => { pathPreparing.TrySetResult(); Assert.True(releasePath.Wait(TimeSpan.FromSeconds(20), ct)); };
                pathArgs = PathArguments("confirm"); pathArgs["token"] = pathToken;
                var canceledPath = Job("ai_path", pathArgs, "failed");
                try
                {
                    await pathPreparing.Task.WaitAsync(token);
                    var automation = (AsyncLocal<bool>)typeof(MainWindow).GetField("automationRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    bool previousAutomation = automation.Value; automation.Value = true;
                    try
                    {
                        System.Windows.Input.Keyboard.ClearFocus();
                        scene.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                            System.Windows.PresentationSource.FromVisual(main), Environment.TickCount, System.Windows.Input.Key.Escape)
                            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    }
                    finally { automation.Value = previousAutomation; }
                    releasePath.Release();
                    Assert.Equal("no_draft", (await canceledPath)["code"]!.GetValue<string>());
                    Assert.Null((await Call("preview_state", new() { ["preview"] = cardPreview }))["ai"]!["path"]!["draft"]);
                    Assert.False(doc.IsDirty); Assert.Empty(doc.SourceWorld.Workspace.History);
                }
                finally { releasePath.Release(); main.SourceEditPreparing = ct => Assert.False(main.Dispatcher.CheckAccess()); }
            }

            pathArgs = PathArguments("begin"); pathArgs["mode"] = "move";
            pathDraft = await Job("ai_path", pathArgs); pathToken = pathDraft["path"]!["draft"]!["token"]!.GetValue<string>();
            pathArgs = PathArguments("set"); pathArgs["token"] = pathToken; pathArgs["input"] = new[] { "10", "0", "0" };
            await Job("ai_path", pathArgs);
            long beforeFailedPathRevision = doc.Revision;
            var prepareWorldModel = main.PreparingSourceWorldModel;
            main.PreparingSourceWorldModel = _ => throw new InvalidDataException("AI path rebuild failure fixture");
            try
            {
                pathArgs = PathArguments("confirm"); pathArgs["token"] = pathToken;
                Assert.Equal("build_failed", (await Job("ai_path", pathArgs, "failed"))["code"]!.GetValue<string>());
                Assert.False(doc.IsDisposed); Assert.False(doc.IsDirty); Assert.True(doc.Revision > beforeFailedPathRevision);
                Assert.Empty(doc.SourceWorld.Workspace.History);
                var restored = (await Call("preview_state", new() { ["preview"] = cardPreview }))["ai"]!["path"]!["draft"]!;
                Assert.Equal(pathToken, restored["token"]!.GetValue<string>());
                Assert.Equal(doc.Revision, restored["revision"]!.GetValue<long>());
                Assert.Equal("10", restored["input"]![0]!.GetValue<string>());
                Assert.Equal(aiSource, Text(doc.SourceWorld.Workspace.Read("data/m1/zrdr/net_01.zrd")));
            }
            finally { main.PreparingSourceWorldModel = prepareWorldModel; }
            // The same restored token can be confirmed again against the proven rollback's new revision.
            pathArgs = PathArguments("confirm"); pathArgs["token"] = pathToken;
            var sourcePathResult = await Job("ai_path", pathArgs);
            var transformedPath = Document(sourcePathResult["document"]!);
            Assert.Same(doc, transformedPath); Assert.False(doc.IsDisposed); Assert.False(sourcePathResult["path"]!["editing"]!.GetValue<bool>());
            string editedAi = aiSource.Replace("( 1.0 2.0 3.0 )", "( 11.0 2.0 3.0 )").Replace("( 5.0 2.0 7.0 )", "( 15.0 2.0 7.0 )");
            Assert.Equal(editedAi, Text(transformedPath.SourceWorld!.Workspace.Read("data/m1/zrdr/net_01.zrd")));
            Assert.Single(transformedPath.SourceWorld.Workspace.History);
            Assert.Equal(aiSource, File.ReadAllText(fixture.Path("data/m1/zrdr/net_01.zrd")));
            await Preview();
            doc = Document(await Call("undo_redo", new() { ["document"] = Id(transformedPath), ["revision"] = transformedPath.Revision, ["action"] = "undo" }));
            await Preview();
            Assert.False(doc.IsDirty); Assert.Equal(aiSource, Text(doc.SourceWorld!.Workspace.Read("data/m1/zrdr/net_01.zrd")));
            // Subsequent editing must use the rebuilt document's source identities.
            pickups = await Job("pickups", new() { ["document"] = Id(doc) });
            ammo = pickups["items"]!.AsArray().First(p => p!["Type"]!.GetValue<string>() == "HEMORTAR_AMMO" && p["source"]!["ResourceName"]!.GetValue<string>() == "PUPPIES.ZRD")!;

            var moved = Document(await Call("pickup_move", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["source"] = ammo["source"]!.DeepClone(), ["x"] = 20.25, ["y"] = 8, ["z"] = -5 }));
            Assert.Same(doc, moved); Assert.False(doc.IsDisposed); Assert.False(moved.EditingLocked);
            var workspace = moved.SourceWorld!.Workspace;
            Assert.Equal(["data/m1/zrdr/puppies.zrd", "data/m1/zrdr/puppies_easy.zrd"], workspace.DirtyFiles);
            Assert.Equal(Default.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), Text(workspace.Read("data/m1/zrdr/puppies.zrd")));
            Assert.Equal(Easy.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), Text(workspace.Read("data/m1/zrdr/puppies_easy.zrd")));
            Assert.StartsWith("# pickups", await File.ReadAllTextAsync(fixture.Path("data/m1/zrdr/puppies.zrd"), token));
            // The pending change is visible as a line diff of the working source against the disk.
            var changes = await Call("source_changes", new() { ["file"] = "data/m1/zrdr/puppies.zrd" });
            Assert.Equal(2, changes["workspace"]!["dirtyFileCount"]!.GetValue<int>());
            var lines = changes["diff"]!["Lines"]!.AsArray().Select(l => l!["Kind"]!.GetValue<string>() + l["Text"]!.GetValue<string>()).ToArray();
            Assert.Contains("-  ( HEMORTAR_AMMO 1 ( 12 8 -5 ) ( 0.0 0.0 0.0 ) 12.5 )   # hand-written", lines);
            Assert.Contains("+  ( HEMORTAR_AMMO 1 ( 20.25 8 -5 ) ( 0.0 0.0 0.0 ) 12.5 )   # hand-written", lines);
            // The evaluated sources hold the new position for both difficulties.
            var after = await Job("pickups", new() { ["document"] = Id(moved) });
            Assert.All(after["items"]!.AsArray().Where(p => p!["Type"]!.GetValue<string>() == "HEMORTAR_AMMO"), p => Assert.Equal(20.25f, p!["OriginalPosition"]!["X"]!.GetValue<float>()));

            // Properties commits a typed position through the same source edit: its own committing draft does not block it.
            // (The fixture has no pickup models, so the pickup is opened in Properties as the scene's card opens it.)
            var hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var window = (PropertiesWindow)typeof(MainWindow).GetMethod("GetPropertiesWindow", hidden)!.Invoke(main, [])!;
            var sourceMove = (Func<zStudio.Core.MissionPickupSource, System.Numerics.Vector3, Task>?)typeof(MainWindow).GetMethod("SourcePickupMove", hidden)!.Invoke(main, [moved]);
            var ammoSource = moved.PickupEdits!.Records.First(r => r.Type == "HEMORTAR_AMMO" && r.Source.ResourceName == "PUPPIES.ZRD").Source;
            Assert.True(window.SetPickup(moved, ammoSource, "HEMORTAR_AMMO", new JsonObject(), sourceMove));
            typeof(MainWindow).GetMethod("PresentProperties", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [window, true]);
            // Wait for the window's layout to finish, so its fields exist.
            await main.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Loaded);
            var pickupFields = main.OpenPropertiesWindow!.PickupFields!;
            var x = Descendants(pickupFields).OfType<System.Windows.Controls.TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Position X");
            var beforeTyped = moved.Document;
            x.Text = "21.5";
            x.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(x), Environment.TickCount, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            for (int wait = 0; wait < 1000 && (ReferenceEquals(beforeTyped, moved.Document) || moved.SourceWorld!.IsRebuilding); wait++) await Task.Delay(10, token);
            Assert.NotSame(beforeTyped, moved.Document); Assert.False(moved.IsDisposed);
            Assert.Equal(Default.Replace("( 12 8 -5 )", "( 21.5 8 -5 )"), Text(workspace.Read("data/m1/zrdr/puppies.zrd")));
            var typed = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            await Preview();
            for (int wait = 0; wait < 1000 && main.OpenPropertiesWindow?.Document != typed; wait++) await Task.Delay(10, token);
            Assert.Same(typed, main.OpenPropertiesWindow?.Document);
            Assert.NotNull(main.OpenPropertiesWindow!.PickupFields);
            var pinned = main.OpenPropertiesWindow.CurrentJson!;
            Assert.Equal(ammoSource.ResourceName, pinned["resource"]!.GetValue<string>());
            Assert.Equal(ammoSource.RecordIndex, pinned["placement_record"]!.GetValue<int>());
            Assert.Equal(Path.GetRelativePath(moved.SourceBuild!.Root, ammoSource.ArchivePath),
                Path.GetRelativePath(typed.SourceBuild!.Root, pinned["source_archive"]!.GetValue<string>()));
            Assert.Equal(21.5d, pinned["preview_world_position"]!["x"]!.GetValue<double>());
            var undoButton = (System.Windows.Controls.Button)typeof(PropertiesWindow).GetField("undo", hidden)!.GetValue(main.OpenPropertiesWindow)!;
            Assert.Equal(System.Windows.Visibility.Visible, undoButton.Visibility); Assert.True(undoButton.IsEnabled);
            // The Properties command follows its pinned owner even when there is no active document.
            main.ViewModel.SelectedDocument = null;
            var beforePropertiesUndo = typed.Document;
            undoButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            for (int wait = 0; wait < 1000 && (ReferenceEquals(beforePropertiesUndo, typed.Document) || typed.SourceWorld!.IsRebuilding); wait++) await Task.Delay(10, token);
            Assert.NotSame(beforePropertiesUndo, typed.Document); Assert.False(typed.IsDisposed);
            await ((Task)typeof(MainWindow).GetField("sourceWorldWork", hidden)!.GetValue(main)!).WaitAsync(token);
            moved = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
            main.ViewModel.SelectedDocument = moved; await Preview();
            Assert.Equal(Default.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), Text(workspace.Read("data/m1/zrdr/puppies.zrd")));
            string restoredArchive = Path.Combine(moved.SourceBuild!.Root, Path.GetRelativePath(typed.SourceBuild!.Root, pinned["source_archive"]!.GetValue<string>()));
            var undoneSource = moved.PickupEdits!.Records.Single(r => r.Source.ArchivePath.Equals(restoredArchive, StringComparison.OrdinalIgnoreCase)
                && r.Source.ResourceName == ammoSource.ResourceName && r.Source.AssetIndex == ammoSource.AssetIndex && r.Source.RecordIndex == ammoSource.RecordIndex).Source;
            window = (PropertiesWindow)typeof(MainWindow).GetMethod("GetPropertiesWindow", hidden)!.Invoke(main, [])!;
            sourceMove = (Func<zStudio.Core.MissionPickupSource, System.Numerics.Vector3, Task>?)typeof(MainWindow).GetMethod("SourcePickupMove", hidden)!.Invoke(main, [moved]);
            Assert.True(window.SetPickup(moved, undoneSource, "HEMORTAR_AMMO", new JsonObject(), sourceMove));
            typeof(MainWindow).GetMethod("PresentProperties", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [window, true]);
            var redoButton = (System.Windows.Controls.Button)typeof(PropertiesWindow).GetField("redo", hidden)!.GetValue(window)!;
            Assert.Equal(System.Windows.Visibility.Visible, redoButton.Visibility); Assert.True(redoButton.IsEnabled);
            // A Discard approval left from a close that did not happen (the world is still open) is forgotten when the next
            // close decision starts, so it never skips that decision's prompt.
            var approval = typeof(MainWindow).GetField("discardApprovedWorkspace", hidden)!;
            approval.SetValue(main, (workspace, workspace.Revision));
            main.ViewModel.CloseDecisionsStarting!();
            Assert.Null(approval.GetValue(main));

            // Undo is project-wide and restores the exact source bytes; redo brings the move back.
            var undone = Document(await Call("undo_redo", new() { ["document"] = Id(moved), ["revision"] = moved.Revision, ["action"] = "undo" }));
            Assert.False(undone.SourceWorld!.Workspace.IsDirty); Assert.Equal(Default, Text(workspace.Read("data/m1/zrdr/puppies.zrd")));
            // Close decisions run one at a time (one can wait for a rebuild and its prompt pumps messages): a later one waits.
            var decisions = (SemaphoreSlim)typeof(MainWindow).GetField("closeDecisions", hidden)!.GetValue(main)!;
            await decisions.WaitAsync(token);
            var waiting = (Task<bool>)typeof(MainWindow).GetMethod("ConfirmDocumentCloseAsync", hidden)!.Invoke(main, [undone])!;
            await Task.Delay(50, token);
            Assert.False(waiting.IsCompleted);
            decisions.Release();
            Assert.True(await waiting.WaitAsync(token));
            Assert.False(undone.IsDisposed);
            var redone = Document(await Call("undo_redo", new() { ["document"] = Id(undone), ["revision"] = undone.Revision, ["action"] = "redo" }));
            await Preview();

            // A world object: the database's ground knows its glTF node; moving and flagging it edit that node.
            int ground = redone.PreviewDocument.Scene!.Nodes.First(n => n.Name == "ground").Index;
            var described = await Call("source_world_object", new() { ["document"] = Id(redone), ["node"] = ground });
            Assert.Equal("data/m1/models/m1.gltf", described["origin"]!["modelFile"]!.GetValue<string>());
            Assert.True(described["origin"]!["database"]!.GetValue<bool>());
            // Transform application retains the document, viewport, camera and mesh buffers; source preparation
            // never blanks the viewer or shows a structural reload notice.
            var sceneField = typeof(MainWindow).GetField("scene", hidden)!;
            (bool Reloading, int? Previewed)? during = null; bool blanked = false;
            System.Windows.DependencyPropertyChangedEventHandler watch = (_, e) => { if (e.NewValue is false) blanked = true; };
            main.SceneHost.IsVisibleChanged += watch;
            Assert.True(main.SceneHost.IsVisible);
            // An ordinary transform does not swap the hosted viewport at all.
            List<object?> hosted = [];
            EventHandler swapped = (_, _) => hosted.Add(main.SceneHost.Content);
            var contentProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.ContentControl.ContentProperty, typeof(System.Windows.Controls.ContentControl));
            var before = main.SceneHost.Content;
            var retainedViewport = (zStudio.Rendering.SceneViewport)before!;
            var retainedPose = retainedViewport.CaptureView();
            var meshesField = typeof(zStudio.Rendering.SceneViewport).GetField("placements", hidden)!;
            var retainedMeshes = ((System.Collections.IDictionary)meshesField.GetValue(retainedViewport)!).Keys.Cast<object>().ToArray();
            contentProperty.AddValueChanged(main.SceneHost, swapped);
            DocumentModel placed;
            TaskCompletionSource transformPreparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var proceed = new SemaphoreSlim(0))
            {
                main.SourceEditPreparing = ct =>
                {
                    main.Dispatcher.Invoke(() => during = (main.IsViewReloading, ((zStudio.Rendering.SceneViewport?)sceneField.GetValue(main))?.ObjectTransformPreviewNode));
                    transformPreparing.TrySetResult(); Assert.True(proceed.Wait(TimeSpan.FromSeconds(20), ct));
                };
                var placementJob = Job("source_world_object_edit", new() { ["document"] = Id(redone), ["revision"] = redone.Revision, ["node"] = ground, ["position"] = new Dictionary<string, object?> { ["x"] = 10, ["y"] = 0, ["z"] = -10 } });
                try
                {
                    var first = await Task.WhenAny(transformPreparing.Task, placementJob).WaitAsync(token);
                    Assert.Same(transformPreparing.Task, first);
                    // Mutating MCP jobs are serialized; the visible controls are disabled while preparation owns the workspace.
                    Assert.False(main.SceneToolbar.IsEnabled);
                    proceed.Release();
                    var placementResult = await placementJob;
                    Assert.False(placementResult["unchanged"]!.GetValue<bool>());
                    placed = Document(placementResult["document"]!);
                }
                finally { proceed.Release(); }
            }
            main.SourceEditPreparing = null; main.SceneHost.IsVisibleChanged -= watch; contentProperty.RemoveValueChanged(main.SceneHost, swapped);
            Assert.NotNull(before);
            Assert.Empty(hosted);
            Assert.Same(redone, placed); Assert.Same(before, main.SceneHost.Content);
            Assert.Equal(retainedPose, retainedViewport.CaptureView());
            Assert.Equal(retainedMeshes, ((System.Collections.IDictionary)meshesField.GetValue(retainedViewport)!).Keys.Cast<object>().ToArray());
            Assert.Equal((false, (int?)ground), during);
            Assert.Empty(Directory.EnumerateFiles(fixture.Project, "*.zbd", SearchOption.AllDirectories));
            Assert.False(blanked);
            Assert.False(main.IsViewReloading);
            Assert.Same(sceneField.GetValue(main), main.SceneHost.Content);
            Assert.Null(typeof(MainWindow).GetField("retiringScene", hidden)!.GetValue(main));
            Assert.Contains("data/m1/models/m1.gltf", workspace.DirtyFiles);
            int groundAfter = placed.PreviewDocument.Scene!.Nodes.First(n => n.Name == "ground").Index;
            var position = (await Call("source_world_object", new() { ["document"] = Id(placed), ["node"] = groundAfter }))["object"]!["position"]!;
            Assert.Equal([10f, 0f, -10f], new[] { "x", "y", "z" }.Select(c => position[c]!.GetValue<float>()));
            var flagged = Document((await Job("source_world_object_edit", new() { ["document"] = Id(placed), ["revision"] = placed.Revision, ["node"] = groundAfter, ["flag"] = "0x10000", ["on"] = true }))["document"]!);
            var flags = (await Call("source_world_object", new() { ["document"] = Id(flagged), ["node"] = groundAfter }))["editableFlags"]!.AsArray();
            Assert.True(flags.Single(f => f!["bit"]!.GetValue<string>() == "0x10000")!["on"]!.GetValue<bool>());
            var refused = await Job("source_world_object_edit", new() { ["document"] = Id(flagged), ["revision"] = flagged.Revision, ["node"] = groundAfter }, "failed");
            Assert.Equal("invalid_argument", refused["code"]!.GetValue<string>());

            var editedWorld = flagged;

            // Fog the scripts never set is added after the instruction that created the world.
            await Preview();
            int world = editedWorld.PreviewDocument.Scene!.Nodes.First(n => n.Name == "world").Index;
            var worldState = await Call("source_world_object", new() { ["document"] = Id(editedWorld), ["node"] = world });
            Assert.Contains(worldState["propertyCommands"]!.AsArray(), c => c!["command"]!.GetValue<string>() == "WorldSetFogColor");
            var fogged = Document((await Job("source_world_command", new() { ["document"] = Id(editedWorld), ["revision"] = editedWorld.Revision, ["node"] = world, ["command"] = "WorldSetFogColor", ["arguments"] = new[] { "0.5", "0.25", "0.125" } }))["document"]!);
            Assert.Contains("NewWorld %worldName%\r\nWorldSetFogColor 0.5 0.25 0.125\r\n", Text(workspace.Read("gamegen/m1.gs")));
            // The next change of the same setting edits that instruction in place.
            int worldAfter = fogged.PreviewDocument.Scene!.Nodes.First(n => n.Name == "world").Index;
            editedWorld = Document((await Job("source_world_command", new() { ["document"] = Id(fogged), ["revision"] = fogged.Revision, ["node"] = worldAfter, ["command"] = "WorldSetFogColor", ["arguments"] = new[] { "1.0", "0.25", "0.125" } }))["document"]!);
            Assert.Contains("WorldSetFogColor 1.0 0.25 0.125\r\n", Text(workspace.Read("gamegen/m1.gs")));
            Assert.DoesNotContain("0.5 0.25 0.125", Text(workspace.Read("gamegen/m1.gs")));
            // Copy the ground beside itself, move the copy under the original, then delete it: each edits the database's glTF.
            await Preview();
            int groundNow = editedWorld.PreviewDocument.Scene!.Nodes.First(n => n.Name == "ground").Index;
            var copied = await Job("source_world_object_edit", new() { ["document"] = Id(editedWorld), ["revision"] = editedWorld.Revision, ["node"] = groundNow, ["action"] = "duplicate", ["name"] = "ground_copy", ["position"] = new Dictionary<string, object?> { ["x"] = 10, ["y"] = 0, ["z"] = 40 } });
            int copyNode = copied["copy"]!.GetValue<int>();
            var withCopy = Document(copied["document"]!);
            Assert.Equal(2, JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf")!)!["nodes"]!.AsArray().Count);
            await Preview();
            int groundThen = withCopy.PreviewDocument.Scene!.Nodes.First(n => n.Name == "ground").Index;
            var reparented = Document((await Job("source_world_object_edit", new() { ["document"] = Id(withCopy), ["revision"] = withCopy.Revision, ["node"] = copyNode, ["action"] = "parent", ["parent"] = groundThen }))["document"]!);
            Assert.Equal("ground", (await Call("source_world_object", new() { ["document"] = Id(reparented), ["node"] = copyNode }))["object"]!["parent"]!.GetValue<string>());
            editedWorld = Document((await Job("source_world_object_edit", new() { ["document"] = Id(reparented), ["revision"] = reparented.Revision, ["node"] = copyNode, ["action"] = "delete" }))["document"]!);
            Assert.Single(JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf")!)!["nodes"]!.AsArray());
            // Unsaved edits of other files do not block resolving an interrupted save; an unknown save is refused as such.
            Assert.Equal("invalid_argument", (await Job("source_recovery_resolve", new() { ["save"] = "missing", ["action"] = "roll_back" }, "failed"))["code"]!.GetValue<string>());

            // One save writes every changed file of the project together, and leaves no journal behind.
            var saved = await Job("save_document", new() { ["document"] = Id(editedWorld), ["revision"] = editedWorld.Revision });
            var written = saved["written"]!.AsArray().Select(w => w!.GetValue<string>()).ToArray();
            Assert.Equal(["data/m1/models/m1.gltf", "data/m1/zrdr/puppies.zrd", "data/m1/zrdr/puppies_easy.zrd", "gamegen/m1.gs"], written.Order(StringComparer.Ordinal));
            Assert.False(editedWorld.IsDirty);
            Assert.Equal(Default.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), await File.ReadAllTextAsync(fixture.Path("data/m1/zrdr/puppies.zrd"), Encoding.Latin1, token));
            Assert.Empty(new SourcePublisher(fixture.Project).FindInterrupted(token));
            // No save was interrupted; resolving an unknown one is refused.
            Assert.Equal(0, (await Call("source_recovery", new()))["saveCount"]!.GetValue<int>());
            Assert.Equal("invalid_argument", (await Job("source_recovery_resolve", new() { ["save"] = "missing", ["action"] = "roll_back" }, "failed"))["code"]!.GetValue<string>());

            // Changing maps closes the old lifetime. Edits and undo target only the current map.
            var second = Document((await Job("source_world_open", new() { ["mission"] = "m2" }))["document"]!);
            Assert.True(editedWorld.IsDisposed); Assert.Same(second, Assert.Single(main.ViewModel.Documents));
            await Preview();
            // An AIV token edit retains source record/selection identities even when its text width changes.
            var tankViewport = (zStudio.Rendering.SceneViewport)main.SceneHost.Content!;
            var tankActor = Assert.Single(tankViewport.Mission!.Actors, actor => actor.CoordinateSource != null);
            var tankSource = tankActor.CoordinateSource!;
            var tankKey = second.PickupEdits!.SourcePresentationKey(tankSource);
            Assert.NotNull(tankKey);
            // The world tree holds what the map itself holds; the units aiv.zrd places follow as a top-level pass of their own.
            var treeRoots = await Call("scene_tree", new() { ["document"] = Id(second) });
            string treeContext = treeRoots["context"]!.GetValue<string>();
            var rootRows = treeRoots["children"]!["items"]!.AsArray();
            async Task<int[]> TreeNodes(JsonNode row) => (await Call("scene_tree", new() { ["document"] = Id(second), ["context"] = treeContext, ["row"] = row["row"]!.GetValue<string>(), ["limit"] = 200 }))
                ["children"]!["items"]!.AsArray().Where(r => r!["node"] != null).Select(r => r!["node"]!.GetValue<int>()).ToArray();
            var aivRow = rootRows.Single(r => r!["pass"]?.GetValue<string>() == "aiv")!;
            Assert.Equal([tankActor.Root], await TreeNodes(aivRow));
            // The pass keeps its name whatever the difficulty, and each unit names the difficulties placing it (aiv.zrd serves all three here).
            Assert.Equal("aiv · 1", aivRow["Label"]!.GetValue<string>());
            var tankListed = (await Call("scene_tree", new() { ["document"] = Id(second), ["context"] = treeContext, ["row"] = aivRow["row"]!.GetValue<string>() }))["children"]!["items"]![0]!;
            Assert.Equal(["Easy", "Medium", "Hard"], tankListed["difficulties"]!.AsArray().Select(d => d!.GetValue<string>()));
            Assert.DoesNotContain(tankActor.Root, await TreeNodes(rootRows.First(r => r!["pass"] == null)!));
            // The outliner's search reads the whole hierarchy as a flat list with parent paths; its eye hides an
            // object in the view only, and Show all shows it again.
            var found = await Call("scene_tree", new() { ["document"] = Id(second), ["search"] = "tank", ["types"] = "object" });
            var tankMatch = found["matches"]!["items"]!.AsArray().Single(m => m!["item"]!["node"]?.GetValue<int>() == tankActor.Root)!;
            Assert.Equal("object", tankMatch["item"]!["category"]!.GetValue<string>());
            Assert.Equal(aivRow["Label"]!.GetValue<string>().Split(" · ")[0], tankMatch["path"]!.GetValue<string>());
            Assert.Empty((await Call("scene_tree", new() { ["document"] = Id(second), ["search"] = "tank", ["types"] = "group" }))["matches"]!["items"]!.AsArray());
            await Call("scene_tree", new() { ["document"] = Id(second), ["context"] = treeContext, ["preview"] = found["preview"]!.GetValue<string>(), ["action"] = "hide", ["row"] = tankMatch["item"]!["row"]!.GetValue<string>() });
            Assert.Contains(tankActor.Root, tankViewport.HiddenNodes); Assert.False(workspace.IsDirty);
            Assert.True((await Call("scene_tree", new() { ["document"] = Id(second), ["search"] = "tank", ["types"] = "object" }))["matches"]!["items"]![0]!["item"]!["hidden"]!.GetValue<bool>());
            await Call("scene_selection", new() { ["preview"] = found["preview"]!.GetValue<string>(), ["action"] = "show_all" });
            Assert.Empty(tankViewport.HiddenNodes);
            Assert.True(tankViewport.SelectInspectionNode(tankActor.Root));
            string tankTarget = tankViewport.SelectedInspection!.Target;
            // Clicking the pass row (no object of its own) keeps the selected unit. A refused click restores the previous row only
            // after WPF has moved focus: restoring inside that change made the two rows trade focus until the stack overflowed.
            var sceneTree = (System.Windows.Controls.TreeView)main.FindName("DocumentSceneTree");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var tankRow = Assert.IsType<SceneTreeItem>(sceneTree.SelectedItem);
            var passRow = (System.Windows.Controls.TreeViewItem)sceneTree.ItemContainerGenerator.ContainerFromItem(sceneTree.Items.OfType<SceneTreeItem>().Single(r => r.Pass != null && r.Label.StartsWith("aiv", StringComparison.Ordinal)));
            foreach (bool focusable in new[] { false, true })
            {
                passRow.Focusable = focusable; // A focusable row stands for any refused selection.
                passRow.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent });
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Same(tankRow, sceneTree.SelectedItem); Assert.Equal(tankTarget, tankViewport.SelectedInspection!.Target);
            }
            passRow.ClearValue(System.Windows.UIElement.FocusableProperty);
            string tankPreview = ((Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).ToString();
            await Call("scene_card", new() { ["preview"] = tankPreview, ["action"] = "begin", ["document"] = Id(second), ["revision"] = second.Revision });
            var tankCard = (SceneInspectionCard)tankViewport.InspectionContent!;
            await Call("scene_card", new() { ["preview"] = tankPreview, ["action"] = "set", ["document"] = Id(second), ["revision"] = second.Revision,
                ["token"] = tankCard.DraftToken, ["position"] = new[] { "1000.25", "20", "30" } });
            // A post-preparation failure retracts the transaction without discarding the user's exact draft.
            var beforeTankFailure = second.Revision; var tankHistory = workspace.History.ToArray(); string[] tankDraftPosition = ["1000.25", "20", "30"];
            var prepareTankModel = main.PreparingSourceWorldModel;
            main.PreparingSourceWorldModel = _ => throw new InvalidDataException("Placement publication failure fixture");
            try
            {
                var rejected = await Call("scene_card", new() { ["preview"] = tankPreview, ["action"] = "apply", ["document"] = Id(second),
                    ["revision"] = second.Revision, ["token"] = tankCard.DraftToken }, error: true);
                Assert.Contains("build_failed", rejected.GetValue<string>());
                Assert.Same(second, main.ViewModel.SelectedDocument); Assert.Same(tankViewport, main.SceneHost.Content);
                Assert.True(second.Revision > beforeTankFailure, "Rollback must advance the document revision.");
                Assert.True(tankCard.HasDraft, "Rollback must restore the released placement draft.");
                Assert.True((await Call("state", new()))["pendingSceneDrafts"]!.GetValue<bool>());
                Assert.Equal(second.Revision, tankCard.DraftRevision); Assert.Equal(tankSource, tankCard.DraftSource);
                var retainedDraft = System.Text.Json.JsonSerializer.SerializeToNode(tankCard.DescribeDraft())!;
                Assert.Equal(tankDraftPosition, retainedDraft["position"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray());
                Assert.Equal(tankHistory, workspace.History); Assert.False(workspace.IsDirty);
                Assert.DoesNotContain("1000.25", Text(workspace.Read("data/m2/zrdr/aiv.zrd")));
            }
            finally { main.PreparingSourceWorldModel = prepareTankModel; }
            var tankMoved = Document((await Call("scene_card", new() { ["preview"] = tankPreview, ["action"] = "apply", ["document"] = Id(second),
                ["revision"] = second.Revision, ["token"] = tankCard.DraftToken }))["document"]!);
            Assert.Same(second, tankMoved); Assert.Same(tankViewport, main.SceneHost.Content);
            Assert.Equal(tankTarget, tankViewport.SelectedInspection?.Target);
            Assert.Equal(tankKey, second.PickupEdits!.SourcePresentationKey(Assert.Single(second.PickupEdits.OtherCoordinates, row => row.Kind == "tank").Source));
            Assert.Contains("1000.25", Text(workspace.Read("data/m2/zrdr/aiv.zrd")));
            Assert.DoesNotContain("1000.25", File.ReadAllText(fixture.Path("data/m2/zrdr/aiv.zrd")));
            second = Document(await Call("undo_redo", new() { ["document"] = Id(second), ["revision"] = second.Revision, ["action"] = "undo" }));
            Assert.False(workspace.IsDirty); Assert.Empty(Directory.EnumerateFiles(fixture.Project, "*.zbd", SearchOption.AllDirectories));
            // A real referenced turret under nested authored groups follows the object card path, including
            // per-instance initialization. Applying a move across a world grid cell never evaluates the full world.
            var retainedTurretDocument = second;
            var turretViewport = (zStudio.Rendering.SceneViewport)main.SceneHost.Content!;
            var reachableTurretNodes = zStudio.Core.SceneBuilder.Assemble(turretViewport.Mission!.Scene).VisibleNodes;
            int turretNode = second.PreviewDocument.Scene!.Nodes.Single(n => n.Class == "object3d" && n.Name == "tur_101"
                && reachableTurretNodes.Contains(n.Index) && second.SourceBuild!.Provenance.ContainsKey(n.Index)).Index;
            await Call("scene_properties", new() { ["preview"] = PreviewId(), ["node"] = turretNode, ["open"] = true });
            var turretCard = (SceneInspectionCard)turretViewport.InspectionContent!;
            Assert.NotNull(turretCard.ObjectEditor);
            Assert.Equal("tur_101", turretCard.ObjectEditor.State.Name);
            var turretCamera = turretViewport.CaptureView();
            var turretMeshes = ((System.Collections.IDictionary)meshesField.GetValue(turretViewport)!).Keys.Cast<object>().ToArray();
            string turretTarget = turretViewport.SelectedInspection!.Target;
            var originalParts = TurretParts();
            Assert.Equal(new[] { "barrel", "healthy" }, originalParts.Keys.Order(StringComparer.Ordinal));
            Assert.DoesNotContain(zStudio.Core.SceneBuilder.Assemble(turretViewport.Mission!.Scene).Placements, p => p.Name == "wreck");
            List<string> evaluations = [], transformRefusals = [];
            main.SourceEditEvaluation = evaluations.Add;
            main.SourceTransformRefused = reason => { if (transformRefusals.Count < 8) transformRefusals.Add(reason[..Math.Min(reason.Length, 128)]); };
            var beforeTurretHistory = workspace.UndoCount;
            byte[] storedTurretWorld = File.ReadAllBytes(fixture.Path("data/m2/models/m2.gltf"));
            try
            {
                await Call("scene_card", new() { ["preview"] = PreviewId(), ["document"] = Id(second), ["revision"] = second.Revision,
                    ["action"] = "set", ["token"] = turretCard.DraftToken,
                    ["fields"] = new[] { new { field = "position", value = "300, 7, -40" } } });
                second = Document((await Call("scene_card", new() { ["preview"] = PreviewId(), ["document"] = Id(second), ["revision"] = second.Revision,
                    ["action"] = "apply", ["token"] = turretCard.DraftToken }))["document"]!);
                Assert.Equal(beforeTurretHistory + 1, workspace.UndoCount);
                await AssertRetainedTurret(new(300, 7, -40));
                second = Document(await Call("undo_redo", new() { ["document"] = Id(second), ["revision"] = second.Revision, ["action"] = "undo" }));
                await AssertRetainedTurret(System.Numerics.Vector3.Zero);
                second = Document(await Call("undo_redo", new() { ["document"] = Id(second), ["revision"] = second.Revision, ["action"] = "redo" }));
                await AssertRetainedTurret(new(300, 7, -40));
                Assert.Equal(new[] { "incremental", "incremental", "incremental" }, evaluations);
                Assert.Equal(storedTurretWorld, File.ReadAllBytes(fixture.Path("data/m2/models/m2.gltf")));
                // Saving an incremental snapshot must refresh its source baseline: the next edit must not
                // mistake our own write for an external change. Undo returns to the saved transform and is clean.
                var turretSaved = await Job("save_document", new() { ["document"] = Id(second), ["revision"] = second.Revision });
                Assert.Equal(new[] { "data/m2/models/m2.gltf" }, turretSaved["written"]!.AsArray().Select(v => v!.GetValue<string>()));
                await AssertRetainedTurret(new(300, 7, -40)); Assert.False(workspace.IsDirty);
                Assert.False(await Task.Run(() => second.SourceInputsChanged(token), token));
                byte[] savedTurretWorld = File.ReadAllBytes(fixture.Path("data/m2/models/m2.gltf"));
                Assert.False(storedTurretWorld.SequenceEqual(savedTurretWorld));
                var savedHistory = workspace.History.ToArray();
                turretCard = (SceneInspectionCard)turretViewport.InspectionContent!;
                // Fields are editable at once. Handles show only when toggled in the transform header (the same toggle hides
                // them), and Apply/Revert wait for a change.
                Assert.False(PositionField(turretCard, "X").IsReadOnly); Assert.False(turretViewport.TransformHandlesVisible);
                Assert.False(CardButton(turretCard, "Apply changes").IsEnabled); Assert.False(CardButton(turretCard, "Revert changes").IsEnabled);
                // The workspace may still be finishing the save; a toggle refused then would say so.
                while ((bool)typeof(MainWindow).GetField("sourceWorkspaceBusy", hidden)!.GetValue(main)! || second.SourceWorld!.IsRebuilding) await Task.Delay(10, token);
                ClickToggle(turretCard, "Move object");
                Assert.True(turretViewport.TransformHandlesVisible, ((System.Windows.Controls.TextBlock)typeof(SceneInspectionCard).GetField("error", hidden)!.GetValue(turretCard)!).Text);
                Assert.Equal("move", turretViewport.TransformMode);
                // A handle press on the unchanged object may start a drag: its draft opens first. A gesture that changes nothing closes it.
                Assert.True(turretViewport.CanStartPickupEdit!()); Assert.True(turretCard.ObjectEditing);
                typeof(SceneInspectionCard).GetMethod("ObjectGestureEnded", hidden)!.Invoke(turretCard, null); Assert.False(turretCard.ObjectEditing);
                await Call("scene_card", new() { ["preview"] = PreviewId(), ["document"] = Id(second), ["revision"] = second.Revision,
                    ["action"] = "set", ["token"] = turretCard.DraftToken, ["transformMode"] = "rotate" });
                Assert.Equal("rotate", turretViewport.TransformMode); Assert.True(turretViewport.TransformHandlesVisible);
                ClickToggle(turretCard, "Rotate object"); Assert.False(turretViewport.TransformHandlesVisible);
                // Change only X through the displayed GUI component. A stale Y baseline would silently
                // revert the first accepted vertical move when this compound Position field is applied.
                PositionField(turretCard, "X").Text = "301";
                Assert.True(CardButton(turretCard, "Apply changes").IsEnabled); Assert.True(CardButton(turretCard, "Revert changes").IsEnabled);
                second = Document((await Call("scene_card", new() { ["preview"] = PreviewId(), ["document"] = Id(second), ["revision"] = second.Revision,
                    ["action"] = "apply", ["token"] = turretCard.DraftToken }))["document"]!);
                await AssertRetainedTurret(new(301, 7, -40)); Assert.True(workspace.IsDirty);
                Assert.Equal(beforeTurretHistory + 2, workspace.UndoCount);
                Assert.Equal(savedHistory, workspace.History.Take(savedHistory.Length));
                second = Document(await Call("undo_redo", new() { ["document"] = Id(second), ["revision"] = second.Revision, ["action"] = "undo" }));
                await AssertRetainedTurret(new(300, 7, -40)); Assert.False(workspace.IsDirty);
                Assert.Equal(beforeTurretHistory + 1, workspace.UndoCount);
                Assert.Equal(savedTurretWorld, File.ReadAllBytes(fixture.Path("data/m2/models/m2.gltf")));
                // A direct MCP transform also refreshes the already selected clean object card.
                second = Document((await Job("source_world_object_edit", new() { ["document"] = Id(second), ["revision"] = second.Revision,
                    ["node"] = turretNode, ["position"] = new Dictionary<string, object?> { ["x"] = 302, ["y"] = 9, ["z"] = -40 } }))["document"]!);
                await AssertRetainedTurret(new(302, 9, -40));
                second = Document(await Call("undo_redo", new() { ["document"] = Id(second), ["revision"] = second.Revision, ["action"] = "undo" }));
                await AssertRetainedTurret(new(300, 7, -40)); Assert.False(workspace.IsDirty);
                Assert.Equal(Enumerable.Repeat("incremental", 7), evaluations);

                // Zone: the card's combo box takes Inherit, Any or a number, with the altitude zone check beside it. Both apply
                // with the other fields as one step, written on the node in m2.gltf; Inherit removes the node's own zone again.
                System.Text.Json.Nodes.JsonObject TurretZone() => System.Text.Json.Nodes.JsonNode.Parse(workspace.Read("data/m2/models/m2.gltf")!)!["nodes"]!.AsArray()
                    .Single(n => n!["name"]?.GetValue<string>() == "tur_101")!["extras"]?[zStudio.Core.Worlds.WorldGltf.Key]?.AsObject() ?? [];
                var zoneCard = (SceneInspectionCard)turretViewport.InspectionContent!;
                var zoneBox = Descendants(zoneCard.ObjectEditor!).OfType<System.Windows.Controls.ComboBox>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Zone");
                Assert.Equal("Inherit", zoneBox.Text); Assert.False(TurretZone().ContainsKey("zone"));
                // A source map always docks the card as its Properties pane: hiding the pane hides it, never floating it over the view.
                Assert.True(zoneCard.Docked); Assert.Same(zoneCard.PanelRoot, ((System.Windows.Controls.ContentControl)main.FindName("SourcePropertiesHost")).Content);
                await Call("workspace_view", new() { ["changes"] = new System.Text.Json.Nodes.JsonObject { ["inspector"] = false } });
                Assert.True(zoneCard.Docked); Assert.DoesNotContain(zoneCard.PanelRoot, zoneCard.Children.Cast<System.Windows.UIElement>());
                await Call("workspace_view", new() { ["changes"] = new System.Text.Json.Nodes.JsonObject { ["inspector"] = true } });
                // Diagnostics is a session pane: toggling it never changes the compiled explorer's saved Tools preference.
                bool savedTools = main.ViewModel.Settings.GetWorkspace().ToolsVisible;
                var diagnostics = await Call("workspace_view", new() { ["changes"] = new System.Text.Json.Nodes.JsonObject { ["tools"] = !savedTools } });
                Assert.Equal(!savedTools, diagnostics["sourceMap"]!["diagnostics"]!.GetValue<bool>()); Assert.Equal(savedTools, main.ViewModel.Settings.GetWorkspace().ToolsVisible);
                // The project remembers it (.zstudio/settings.json), so it opens that way next time.
                await main.ViewModel.ProjectSettingsWritten;
                Assert.Equal(!savedTools, zStudio.Core.Sources.SourceProjectSettings.ReadView(fixture.Project).Diagnostics);
                await Call("workspace_view", new() { ["changes"] = new System.Text.Json.Nodes.JsonObject { ["tools"] = savedTools } });
                zoneBox.Text = "255";
                // 255 is Any's number, never typed as a zone: Apply refuses it and keeps the draft.
                Assert.Contains("0 to 254", (await Call("scene_card", new() { ["preview"] = PreviewId(), ["document"] = Id(second), ["revision"] = second.Revision,
                    ["action"] = "apply", ["token"] = zoneCard.DraftToken }, error: true)).GetValue<string>());
                Assert.True(zoneCard.HasObjectDraft); Assert.Equal(savedTurretWorld, File.ReadAllBytes(fixture.Path("data/m2/models/m2.gltf")));
                zoneBox.Text = "5";
                var gate = Descendants(zoneCard.ObjectEditor!).OfType<System.Windows.Controls.CheckBox>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Altitude zone check", StringComparison.Ordinal));
                bool gated = gate.IsChecked == true;
                gate.IsChecked = !gated; gate.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(CardButton(zoneCard, "Apply changes").IsEnabled);
                second = Document((await Call("scene_card", new() { ["preview"] = PreviewId(), ["document"] = Id(second), ["revision"] = second.Revision,
                    ["action"] = "apply", ["token"] = zoneCard.DraftToken }))["document"]!);
                Assert.Equal(5, TurretZone()["zone"]!.GetValue<int>()); Assert.Equal(!gated, TurretZone()["altitudeZoneCheck"]?.GetValue<bool>() == true);
                // The selected turret's card shows the applied zone (a zone change may rebuild the world rather than patch it).
                async Task<SceneInspectionCard> TurretCard()
                {
                    for (int wait = 0; wait < 500; wait++)
                    {
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        if (main.SceneHost.Content is zStudio.Rendering.SceneViewport { InspectionContent: SceneInspectionCard { ObjectEditor: not null } shown }) return shown;
                        await Task.Delay(10, token);
                    }
                    throw new TimeoutException("The turret card did not return after the zone edit.");
                }
                zoneCard = await TurretCard();
                Assert.Equal("5", Descendants(zoneCard.ObjectEditor!).OfType<System.Windows.Controls.ComboBox>().Single().Text);
                await Call("scene_card", new() { ["preview"] = PreviewId(), ["document"] = Id(second), ["revision"] = second.Revision, ["action"] = "set",
                    ["token"] = zoneCard.DraftToken, ["fields"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["field"] = "zone", ["value"] = "inherit" }) });
                second = Document((await Call("scene_card", new() { ["preview"] = PreviewId(), ["document"] = Id(second), ["revision"] = second.Revision,
                    ["action"] = "apply", ["token"] = zoneCard.DraftToken }))["document"]!);
                Assert.False(TurretZone()["zone"] is { } kept && kept.ToString() != "inherit");
                second = Document(await Call("undo_redo", new() { ["document"] = Id(second), ["revision"] = second.Revision, ["action"] = "undo" }));
                second = Document(await Call("undo_redo", new() { ["document"] = Id(second), ["revision"] = second.Revision, ["action"] = "undo" }));
                Assert.Equal(savedTurretWorld, File.ReadAllBytes(fixture.Path("data/m2/models/m2.gltf")));
            }
            finally { main.SourceEditEvaluation = null; main.SourceTransformRefused = null; }

            // A group the build removes is renamed in its glTF file only (the tree's Rename, MCP sourceFile/gltfNode): the
            // compiled world keeps its nodes (only a slot the build left free carries the new name), a name the world uses is
            // refused, and Undo restores the file.
            var m2Nodes = System.Text.Json.Nodes.JsonNode.Parse(workspace.Read("data/m2/models/m2.gltf")!)!["nodes"]!.AsArray();
            int turretGroup = m2Nodes.Select((n, i) => (Name: n!["name"]?.GetValue<string>(), i)).Single(n => n.Name == "turret_group").i;
            var compiledNames = second.PreviewDocument.Scene!.Nodes.Select(n => n.Name).ToArray();
            Dictionary<string, object?> RenameGroup(string name) => new() { ["document"] = Id(second), ["revision"] = second.Revision, ["action"] = "rename",
                ["sourceFile"] = "data/m2/models/m2.gltf", ["gltfNode"] = turretGroup, ["name"] = name };
            var taken = await Job("source_world_object_edit", RenameGroup("tur_101"), "failed");
            Assert.Equal("invalid_argument", taken["code"]!.GetValue<string>()); Assert.Contains("already have a node named tur_101", taken["message"]!.GetValue<string>());
            // The outliner keeps the rows that were open across the rebuild the rename causes.
            var groupSearch = await Call("scene_tree", new() { ["document"] = Id(second), ["search"] = "turret_group" });
            var groupRow = groupSearch["matches"]!["items"]!.AsArray().Single()!["item"]!;
            await Call("scene_tree", new() { ["document"] = Id(second), ["context"] = groupSearch["context"]!.GetValue<string>(), ["preview"] = groupSearch["preview"]!.GetValue<string>(),
                ["action"] = "expand", ["row"] = groupRow["row"]!.GetValue<string>() });
            second = Document((await Job("source_world_object_edit", RenameGroup("gTurrets")))["document"]!);
            Assert.True((await Call("scene_tree", new() { ["document"] = Id(second), ["search"] = "gTurrets" }))["matches"]!["items"]!.AsArray().Single()!["item"]!["IsExpanded"]!.GetValue<bool>());
            Assert.Equal("gTurrets", System.Text.Json.Nodes.JsonNode.Parse(workspace.Read("data/m2/models/m2.gltf")!)!["nodes"]![turretGroup]!["name"]!.GetValue<string>());
            Assert.Equal(compiledNames.Select(n => n == "turret_group" ? "gTurrets" : n), second.PreviewDocument.Scene!.Nodes.Select(n => n.Name));
            second = Document(await Call("undo_redo", new() { ["document"] = Id(second), ["revision"] = second.Revision, ["action"] = "undo" }));
            Assert.False(workspace.IsDirty);

            Dictionary<string, System.Numerics.Matrix4x4> TurretParts() => zStudio.Core.SceneBuilder.Assemble(turretViewport.Mission!.Scene).Placements
                .Where(p => p.Name is "healthy" or "barrel").ToDictionary(p => p.Name, p => p.Transform, StringComparer.Ordinal);
            string PreviewId() => ((Guid)typeof(MainWindow).GetField("previewId", hidden)!.GetValue(main)!).ToString();
            static System.Windows.Controls.Button CardButton(SceneInspectionCard card, string name) => Descendants(card.PanelRoot).OfType<System.Windows.Controls.Button>()
                .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == name && b.IsVisible);
            static void ClickToggle(SceneInspectionCard card, string name) => Descendants(card.PanelRoot).OfType<System.Windows.Controls.Primitives.ToggleButton>()
                .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == name && b.IsVisible)
                .RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            static System.Windows.Controls.TextBox PositionField(SceneInspectionCard card, string axis) => Descendants(card.ObjectEditor!)
                .OfType<System.Windows.Controls.TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Position " + axis);
            async Task AssertRetainedTurret(System.Numerics.Vector3 offset)
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                main.UpdateLayout();
                var currentCard = (SceneInspectionCard)turretViewport.InspectionContent!;
                var editor = currentCard.ObjectEditor!;
                Assert.False(currentCard.HasObjectDraft); Assert.False(editor.HasPendingDrafts);
                // Check the rendered controls, accepted form baseline, and MCP descriptions independently.
                Assert.Equal(offset.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture), PositionField(currentCard, "Y").Text);
                Assert.Equal(offset, editor.State.Transform!.Value.Position);
                string expectedPosition = string.Join(", ", new[] { offset.X, offset.Y, offset.Z }.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                var field = editor.Describe()["fields"]!.AsArray().Single(f => f!["field"]!.GetValue<string>() == "position")!;
                Assert.Equal(expectedPosition, field["value"]!.GetValue<string>());
                Assert.Equal(expectedPosition, field["original"]!.GetValue<string>()); Assert.False(field["pending"]!.GetValue<bool>());
                var inspected = await Call("scene_inspect", new() { ["preview"] = PreviewId() });
                var described = inspected["draft"]!["content"]!["fields"]!.AsArray().Single(f => f!["field"]!.GetValue<string>() == "position")!;
                Assert.Equal(expectedPosition, described["value"]!.GetValue<string>());
                Assert.Equal(expectedPosition, described["original"]!.GetValue<string>());
                Assert.Equal("Position: " + expectedPosition, (await Call("scene_card", new() { ["preview"] = PreviewId(), ["action"] = "copy", ["field"] = "Position" }))["text"]!.GetValue<string>());
                Assert.True(ReferenceEquals(retainedTurretDocument, second), $"Turret document replaced; evaluation: {string.Join(", ", evaluations)}; refusal: {string.Join(", ", transformRefusals)}");
                Assert.Same(turretViewport, main.SceneHost.Content);
                Assert.False(main.IsViewReloading); Assert.Null(typeof(MainWindow).GetField("retiringScene", hidden)!.GetValue(main));
                Assert.Equal(turretCamera, turretViewport.CaptureView()); Assert.Equal(turretTarget, turretViewport.SelectedInspection?.Target);
                Assert.Equal(turretMeshes, ((System.Collections.IDictionary)meshesField.GetValue(turretViewport)!).Keys.Cast<object>().ToArray());
                Assert.True(turretViewport.SelectionBoundsVisible);
                var parts = TurretParts();
                foreach (var (name, original) in originalParts)
                {
                    var expected = original * System.Numerics.Matrix4x4.CreateTranslation(offset);
                    var actual = parts[name];
                    Assert.Equal(expected.Translation, actual.Translation);
                    // The shared glTF writer converts an authored matrix to TRS when it decomposes;
                    // quaternion normalization on reload can change this 45-degree basis by two float ULPs.
                    // Position and the affine column remain exact; tolerate only that tiny basis round trip.
                    for (int row = 0; row < 3; row++)
                        for (int column = 0; column < 3; column++)
                            Assert.InRange(MathF.Abs(expected[row, column] - actual[row, column]), 0f, 1e-6f);
                    for (int row = 0; row < 4; row++) Assert.Equal(expected[row, 3], actual[row, 3]);
                }
                Assert.DoesNotContain(zStudio.Core.SceneBuilder.Assemble(turretViewport.Mission!.Scene).Placements, p => p.Name == "wreck");
            }
            int ground2 = second.PreviewDocument.Scene!.Nodes.First(n => n.Name == "ground").Index;
            var edited2 = Document((await Job("source_world_object_edit", new() { ["document"] = Id(second), ["revision"] = second.Revision, ["node"] = ground2, ["position"] = new Dictionary<string, object?> { ["x"] = 5, ["y"] = 0, ["z"] = 5 } }))["document"]!);
            Assert.Equal(["data/m2/models/m2.gltf"], workspace.DirtyFiles);
            long priorUndoRevision = edited2.Revision;
            var undone2 = Document(await Call("undo_redo", new() { ["document"] = Id(edited2), ["revision"] = priorUndoRevision, ["action"] = "undo" }));
            Assert.False(workspace.IsDirty);
            var stale = await Job("source_world_object_edit", new() { ["document"] = Id(edited2), ["revision"] = priorUndoRevision, ["node"] = ground2, ["flag"] = "0x10000", ["on"] = true }, "failed");
            Assert.Equal("revision_conflict", stale["code"]!.GetValue<string>());
            var reloaded = Document(await Job("reload_document", new() { ["document"] = Id(undone2), ["revision"] = undone2.Revision }));
            await Call("close_document", new() { ["document"] = Id(reloaded), ["revision"] = reloaded.Revision });
            Assert.Empty(main.ViewModel.Documents);

            async Task Preview()
            {
                var work = (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                await work.WaitAsync(token);
            }
            Dictionary<string, object?> PathArguments(string action) => new()
            {
                ["preview"] = cardPreview, ["snapshot"] = scene.AiNetworks.Id,
                ["document"] = Id(doc), ["revision"] = doc.Revision, ["action"] = action
            };
            static string Text(byte[]? bytes) => Encoding.Latin1.GetString(bytes!);
            static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                    yield return child;
                    foreach (var below in Descendants(child)) yield return below;
                }
            }
            DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == (state["document"]?["id"] ?? state["id"])!.GetValue<string>());
            static string Id(DocumentModel d) => d.SessionId.ToString();
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, bool error = false)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == error, name + ": " + text);
                return error ? JsonValue.Create(text)! : JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments);
                if (job["id"] == null || job["State"] == null) return job;
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, name + ": " + job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            typeof(MainWindow).GetMethod("CancelAiPathDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, []);
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }
}
