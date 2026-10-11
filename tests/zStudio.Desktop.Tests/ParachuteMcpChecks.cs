using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>The two owning-document paths share real protocol jobs and the visible draft's actual buttons.</summary>
internal static class ParachuteMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        fixture.Write(VtolDropFixture.Root, VtolDropFixture.Definitions());
        fixture.Write("data/m1/zrdr/flight.zan", VtolDropFixture.MotionScript);
        fixture.Write("data/m1/zrdr/puppies.zrd", "( ( HEMORTAR_AMMO 1 ( 0.0 0.0 0.0 ) ( 0.0 0.0 0.0 ) 1.0 ) )");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);

            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            var world = Document((await Job("source_world_open", new() { ["mission"] = "m1" }))["document"]!);
            await Preview();
            // Keep an independent compiled baseline before source confirmation replaces/removes the preview build.
            string compiled = Path.Combine(fixture.Root, "compiled");
            foreach (string path in Directory.EnumerateFiles(world.SourceBuild!.Folder, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(compiled, Path.GetRelativePath(world.SourceBuild.Folder, path));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(path, copy);
            }
            var state = (await PathAction("show"))["parachute"]!;
            Assert.Equal(2, state["windows"]!.AsArray().Count);
            Assert.True(state["cycleSeconds"]!.GetValue<double>() >= 10);
            Assert.Null(state["selected"]);
            await Unlock();
            await SelectFirstWindow();
            TaskCompletionSource analyzing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var releaseAnalysis = new SemaphoreSlim(0))
            {
                main.ParachuteAnalysisPreparing = ct => { analyzing.TrySetResult(); Assert.True(releaseAnalysis.Wait(TimeSpan.FromSeconds(20), ct)); };
                var analysis = PathAction("analyze", new() { ["entry"] = state["entry"]!.GetValue<int>() });
                try
                {
                    var first = await Task.WhenAny(analyzing.Task, analysis).WaitAsync(token);
                    if (first == analysis) Assert.Fail("Analysis did not reach its worker: " + (await analysis).ToJsonString());
                    Assert.False(((UIElement)main.FindName("ParachutePanel")).IsEnabled);
                    var refusal = Assert.Throws<TargetInvocationException>(() => typeof(MainWindow).GetMethod("BeginParachute", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [false]));
                    Assert.Equal("busy", Assert.IsType<StudioCommandException>(refusal.InnerException).Code);
                    Assert.Null((await State())!["draft"]);
                    releaseAnalysis.Release(); await analysis;
                    Assert.True(((UIElement)main.FindName("ParachutePanel")).IsEnabled);
                    Assert.Equal((await State())!["selected"]!.GetValue<Guid>(), Viewport().SelectedParachuteWindow);
                }
                finally { releaseAnalysis.Release(); main.ParachuteAnalysisPreparing = null; }
            }
            state = (await PathAction("begin"))["parachute"]!;
            string draft = state["draft"]!["token"]!.GetValue<string>();
            var baselineReleases = (await PathAction("releases"))["items"]!.ToJsonString();
            await PathAction("set", new() { ["token"] = draft, ["start"] = 2 });
            var displayedReleases = (await PathAction("releases"))["items"]!.ToJsonString();
            Assert.NotEqual(baselineReleases, displayedReleases);
            var rendererPreview = (Recoil.Zbd.Rendering.ParachuteWindowOverlay)typeof(Recoil.Zbd.Rendering.SceneViewport)
                .GetField("parachutePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Viewport())!;
            double displayedFirstRelease = rendererPreview.Releases[0].Phase;
            ((TextBox)main.FindName("ParachuteStart")).Text = "4";
            await UntilAsync(async () => (await State())?["draft"]?["error"] != null);
            state = (await State())!;
            Assert.False(state["draft"]!["ready"]!.GetValue<bool>());
            Assert.False(((Button)main.FindName("ParachuteConfirm")).IsEnabled);
            Assert.Equal("4", state["draft"]!["input"]!.GetValue<string>());
            Assert.Equal(state["draft"]!["error"]!.GetValue<string>(), ((TextBlock)main.FindName("ParachuteMessage")).Text);
            Assert.Equal(displayedReleases, (await PathAction("releases"))["items"]!.ToJsonString());
            Assert.Equal(displayedFirstRelease, ((Recoil.Zbd.Rendering.ParachuteWindowOverlay)typeof(Recoil.Zbd.Rendering.SceneViewport)
                .GetField("parachutePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Viewport())!).Releases[0].Phase);
            Assert.Equal("invalid_argument", (await Job("parachute", await Arguments("confirm", new() { ["token"] = draft }), "failed"))["code"]!.GetValue<string>());
            ((TextBox)main.FindName("ParachuteStart")).Text = "-";
            state = (await State())!;
            Assert.False(state["draft"]!["ready"]!.GetValue<bool>());
            Assert.Contains("finite", state["draft"]!["error"]!.GetValue<string>());
            Assert.Equal(displayedReleases, (await PathAction("releases"))["items"]!.ToJsonString());
            await PathAction("set", new() { ["token"] = draft, ["start"] = 2 });
            Assert.True((await State())!["draft"]!["ready"]!.GetValue<bool>());
            Assert.Equal(displayedReleases, (await PathAction("releases"))["items"]!.ToJsonString());

            // Escape while the source worker is held is an explicit discard, not a failed acceptance to restore.
            TaskCompletionSource preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new SemaphoreSlim(0))
            {
                Viewport().SetParachutePointerMove(true);
                main.SourceEditPreparing = ct => { preparing.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(20), ct)); };
                var canceled = Job("parachute", await Arguments("confirm", new() { ["token"] = draft }), "failed");
                try
                {
                    var first = await Task.WhenAny(preparing.Task, canceled).WaitAsync(token);
                    if (first == canceled) Assert.Fail("Confirmation did not reach source preparation: " + (await canceled).ToJsonString());
                    Assert.False(Viewport().IsParachutePointerMoving);
                    // Already queued renderer/numeric callbacks cannot replace the accepted worker plan or its input.
                    ((Action<int, double>)typeof(Recoil.Zbd.Rendering.SceneViewport).GetField("ParachuteMoveRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Viewport())!)(150, 0);
                    ((TextBox)main.FindName("ParachuteStart")).Text = "3";
                    var accepting = (await State())!["draft"]!;
                    Assert.Equal("2", accepting["input"]!.GetValue<string>());
                    Assert.Equal(2, accepting["start"]!.GetValue<float>());
                    Assert.True(accepting["ready"]!.GetValue<bool>());
                    var automation = (AsyncLocal<bool>)typeof(MainWindow).GetField("automationRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    bool previous = automation.Value; automation.Value = true;
                    try
                    {
                        System.Windows.Input.Keyboard.ClearFocus();
                        ((UIElement)((ContentControl)main.FindName("SceneHost")).Content).RaiseEvent(new System.Windows.Input.KeyEventArgs(
                            System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), Environment.TickCount, System.Windows.Input.Key.Escape)
                            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    }
                    finally { automation.Value = previous; }
                    release.Release();
                    Assert.Equal("no_draft", (await canceled)["code"]!.GetValue<string>());
                    Assert.Null((await State())!["draft"]);
                    Assert.Equal(baselineReleases, (await PathAction("releases"))["items"]!.ToJsonString());
                    Assert.False(world.IsDirty); Assert.Empty(world.SourceWorld!.Workspace.History);
                }
                finally { release.Release(); main.SourceEditPreparing = null; }
            }

            state = (await PathAction("begin"))["parachute"]!;
            draft = state["draft"]!["token"]!.GetValue<string>();
            await PathAction("set", new() { ["token"] = draft, ["start"] = 2 });
            long revisionBeforeFailure = world.Revision;
            var prepareModel = main.PreparingSourceWorldModel;
            main.PreparingSourceWorldModel = _ => throw new InvalidDataException("Parachute rebuild failure fixture");
            try
            {
                var failed = await Job("parachute", await Arguments("confirm", new() { ["token"] = draft }), "failed");
                Assert.Equal("build_failed", failed["code"]!.GetValue<string>());
                Assert.False(world.IsDisposed); Assert.False(world.IsDirty); Assert.True(world.Revision > revisionBeforeFailure);
                Assert.Empty(world.SourceWorld!.Workspace.History);
                state = (await State())!;
                Assert.Equal(world.Revision, state["revision"]!.GetValue<long>());
                Assert.Equal(draft, state["draft"]!["token"]!.GetValue<string>());
                Assert.Equal("2", state["draft"]!["input"]!.GetValue<string>());
                Assert.True(state["draft"]!["ready"]!.GetValue<bool>());
                Assert.Equal(VtolDropFixture.Definitions(), Encoding.Latin1.GetString(world.SourceWorld.Workspace.Read(VtolDropFixture.Root)!));
            }
            finally { main.PreparingSourceWorldModel = prepareModel; }
            // Retry uses the same restored intent/token and the proven rollback's current document revision.
            var before = world;
            var moved = await PathAction("confirm", new() { ["token"] = draft });
            world = Document(moved["document"]!); await Preview();
            Assert.True(before.IsDisposed);
            string expected = VtolDropFixture.Definitions().Replace("SEQUENCE_OFFSET 1", "SEQUENCE_OFFSET 2.0", StringComparison.Ordinal);
            Assert.Equal(expected, Encoding.Latin1.GetString(world.SourceWorld!.Workspace.Read(VtolDropFixture.Root)!));
            Assert.Single(world.SourceWorld.Workspace.History);
            Assert.Equal(VtolDropFixture.Definitions(), File.ReadAllText(fixture.Path(VtolDropFixture.Root)));
            // Source confirmation is one project history step, not a mutation of the cached animation archive.
            world = Document(await Call("undo_redo", new() { ["document"] = Id(world), ["revision"] = world.Revision, ["action"] = "undo" }));
            await Preview();
            Assert.False(world.IsDirty);
            Assert.Equal(VtolDropFixture.Definitions(), Encoding.Latin1.GetString(world.SourceWorld!.Workspace.Read(VtolDropFixture.Root)!));
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);

            await main.ViewModel.OpenRootAsync(compiled, token);
            world = Document(await Job("open_document", new() { ["path"] = Path.Combine(compiled, "m1", "gamez.zbd") }));
            await Preview();
            state = (await PathAction("show"))["parachute"]!;
            var owner = main.ViewModel.Documents.Single(d => Id(d) == state["owner"]!.GetValue<string>());
            Assert.NotSame(world, owner); Assert.Same(world, main.ViewModel.SelectedDocument);
            Assert.EndsWith("anim.zbd", owner.Path, StringComparison.OrdinalIgnoreCase);
            byte[] original = File.ReadAllBytes(owner.Path);
            long revision = owner.Revision;
            // Inspecting sibling coordinates is permitted on an animation owner; explicit animation controls must
            // still use AnimationEdits rather than dispatching through its now-present coordinate session.
            await Job("pickups", new() { ["document"] = Id(owner) });
            Assert.NotEmpty(owner.PickupEdits!.Records);
            await Unlock();
            await SelectFirstWindow();

            var staleOwner = await Arguments("begin"); staleOwner["ownerRevision"] = owner.Revision + 1;
            Assert.Equal("revision_conflict", (await Job("parachute", staleOwner, "failed"))["code"]!.GetValue<string>());
            state = (await PathAction("begin"))["parachute"]!;
            draft = state["draft"]!["token"]!.GetValue<string>();
            Assert.Equal("pending_drafts", Assert.Throws<StudioCommandException>(() => main.ViewModel.ValidateNavigationPublication!(false)).Code);
            // A clean owner's unfinished transform participates in both normal close and delayed automation close.
            var closeAutomation = (AsyncLocal<bool>)typeof(MainWindow).GetField("automationRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            bool oldAutomation = closeAutomation.Value; closeAutomation.Value = true;
            try
            {
                main.Close(); Assert.True(main.IsLoaded);
                await Until(() => !(bool)typeof(MainWindow).GetField("resolvingClose", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!);
                Assert.NotNull((await State())!["draft"]);
                typeof(MainWindow).GetField("automationCloseRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
                main.Close(); Assert.True(main.IsLoaded); Assert.NotNull((await State())!["draft"]);
            }
            finally { closeAutomation.Value = oldAutomation; }
            Assert.Equal("pending_drafts", (await Job("parachute", await Arguments("hide"), "failed"))["code"]!.GetValue<string>());
            // GUI cancellation is the same draft, with no preliminary discard dialog or accepted edit.
            Click("cancel");
            Assert.Null((await State())!["draft"]); Assert.Equal(revision, owner.Revision);

            state = (await PathAction("begin"))["parachute"]!;
            draft = state["draft"]!["token"]!.GetValue<string>();
            var stale = await Arguments("set", new() { ["token"] = "expired", ["start"] = 2 });
            Assert.Equal("stale_draft", (await Job("parachute", stale, "failed"))["code"]!.GetValue<string>());
            // Real TextChanged + routed Confirm protect GUI/protocol parity, not only the underlying command.
            ((TextBox)main.FindName("ParachuteStart")).Text = "2";
            await Until(() => ((Button)main.FindName("ParachuteConfirm")).IsEnabled);
            Click("confirm");
            await Until(() => owner.Revision == revision + 1);
            await UntilAsync(async () => (await State())?["ownerRevision"]?.GetValue<long>() == owner.Revision);
            Assert.False(world.IsDirty); Assert.True(owner.IsDirty); Assert.Same(world, main.ViewModel.SelectedDocument);
            Assert.Equal(2, FirstWindow(owner).Start);
            Assert.Equal(original, File.ReadAllBytes(owner.Path));
            await SupersedeRefresh("undo"); Assert.Equal(1, FirstWindow(owner).Start); Assert.False(owner.IsDirty);
            state = (await PathAction("begin"))["parachute"]!;
            draft = state["draft"]!["token"]!.GetValue<string>();
            await PathAction("set", new() { ["token"] = draft, ["start"] = 2 });
            await SupersedeRefresh("confirm", new() { ["token"] = draft }); Assert.Equal(2, FirstWindow(owner).Start);
            await PathAction("undo"); Assert.Equal(1, FirstWindow(owner).Start);
            await PathAction("redo"); Assert.Equal(2, FirstWindow(owner).Start);
            await Call("undo_redo", new() { ["document"] = Id(owner), ["revision"] = owner.Revision, ["action"] = "undo" });
            Assert.Equal(1, FirstWindow(owner).Start);
            await Call("undo_redo", new() { ["document"] = Id(owner), ["revision"] = owner.Revision, ["action"] = "redo" });
            Assert.Equal(2, FirstWindow(owner).Start);
            await PathAction("show");
            await SupersedeRefresh("show");
            await SupersedeRefresh("analyze");
            // The ordinary Files close path must reach animation Save As even after pickup inspection.
            // Canceling that destination picker keeps the accepted animation edit and document alive.
            int animationSavePrompts = 0;
            main.ShowingAnimationSaveDialog = _ => { animationSavePrompts++; return false; };
            try { await CloseOwnerWithSave(); }
            finally { main.ShowingAnimationSaveDialog = null; }
            Assert.Equal(1, animationSavePrompts);
            Assert.False(owner.IsDisposed); Assert.True(owner.AnimationEdits!.IsDirty);
            Assert.Null(owner.ApprovedCloseRevision); Assert.Equal(original, File.ReadAllBytes(owner.Path));
            string saved = Path.Combine(fixture.Root, "animation-copy.zbd");
            await PathAction("save", new() { ["destination"] = saved });
            var reopened = await FormatRegistry.Default.OpenAsync(saved, token);
            Assert.Equal(2, Assert.Single(VtolDropWindows.Discover(reopened.Animations!, token).Candidates).Windows[0].Start);
            Assert.Equal(original, File.ReadAllBytes(owner.Path)); Assert.False(owner.IsDirty);

            await PathAction("show");
            await SelectFirstWindow();
            state = (await PathAction("begin"))["parachute"]!;
            var oldArguments = await Arguments("confirm", new() { ["token"] = state["draft"]!["token"]!.GetValue<string>() });
            // An independent accepted owner edit expires the overlay and draft instead of retaining a stale write plan.
            owner.AnimationEdits!.Apply(1, "Change owner", e => e.SetFloat(164, e.F32(164) + 1));
            Assert.False((await State())!["visible"]!.GetValue<bool>());
            Assert.Equal("not_ready", (await Job("parachute", oldArguments, "failed"))["code"]!.GetValue<string>());

            // A document can hold accepted animation and coordinate edits together. Save-and-close
            // must persist both, rather than approving disposal after only one session was saved.
            await Job("pickups", new() { ["document"] = Id(owner) });
            await Call("pickup_lock", new() { ["document"] = Id(owner), ["revision"] = owner.Revision, ["locked"] = false });
            var placement = owner.PickupEdits!.Records[0];
            var position = owner.PickupEdits.Position(placement.Source);
            var coordinateSource = new Dictionary<string, object?> { ["ArchivePath"] = placement.Source.ArchivePath, ["AssetIndex"] = placement.Source.AssetIndex,
                ["ResourceName"] = placement.Source.ResourceName, ["RecordIndex"] = placement.Source.RecordIndex };
            await Call("pickup_move", new() { ["document"] = Id(owner), ["revision"] = owner.Revision, ["source"] = coordinateSource,
                ["x"] = position.X + 7, ["y"] = position.Y, ["z"] = position.Z });
            Assert.True(owner.AnimationEdits.IsDirty); Assert.True(owner.PickupEdits.IsDirty);
            string mixedCopy = Path.Combine(fixture.Root, "mixed-animation-copy.zbd");
            await Job("save_document", new() { ["document"] = Id(owner), ["revision"] = owner.Revision, ["destination"] = mixedCopy });
            Assert.False(owner.IsDirty); Assert.True(File.Exists(mixedCopy));
            // Repeat through the normal GUI close decision, which must save both sessions too.
            owner.AnimationEdits.Apply(1, "Change owner before close", e => e.SetFloat(164, e.F32(164) + 1));
            await Call("pickup_move", new() { ["document"] = Id(owner), ["revision"] = owner.Revision, ["source"] = coordinateSource,
                ["x"] = position.X + 8, ["y"] = position.Y, ["z"] = position.Z });
            string closedCopy = Path.Combine(fixture.Root, "closed-animation-copy.zbd");
            byte[] expectedAnimation = AnimationWriter.Write(owner.AnimationEdits.Package);
            var savedCoordinates = owner.PickupEdits;
            // Animation is the owner's primary history; coordinate steps remain reachable after
            // that requested direction is exhausted, for both undo and redo.
            while (owner.AnimationEdits.CanUndo)
                await Call("undo_redo", new() { ["document"] = Id(owner), ["revision"] = owner.Revision, ["action"] = "undo" });
            Assert.Equal(position.X + 8, savedCoordinates.Position(placement.Source).X);
            await Call("undo_redo", new() { ["document"] = Id(owner), ["revision"] = owner.Revision, ["action"] = "undo" });
            Assert.Equal(position.X + 7, savedCoordinates.Position(placement.Source).X);
            while (owner.AnimationEdits.CanRedo)
                await Call("undo_redo", new() { ["document"] = Id(owner), ["revision"] = owner.Revision, ["action"] = "redo" });
            await Call("undo_redo", new() { ["document"] = Id(owner), ["revision"] = owner.Revision, ["action"] = "redo" });
            Assert.Equal(position.X + 8, savedCoordinates.Position(placement.Source).X);
            Assert.Equal(expectedAnimation, AnimationWriter.Write(owner.AnimationEdits.Package));
            main.ShowingAnimationSaveDialog = dialog => { dialog.FileName = closedCopy; return true; };
            try { await CloseOwnerWithSave(); }
            finally { main.ShowingAnimationSaveDialog = null; }
            Assert.True(owner.IsDisposed); Assert.False(savedCoordinates.IsDirty);
            Assert.Equal(expectedAnimation, File.ReadAllBytes(closedCopy));
            var reopenedCoordinates = await PickupPlacementEditSession.LoadAsync(world.Path, main.ViewModel.Resolver!, token);
            Assert.Equal(position.X + 8, reopenedCoordinates.Position(placement.Source).X);

            // An ordinary source timing edit can make one discovered flight unavailable without
            // invalidating a healthy sibling. The picker must remain available, with no partial path.
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);
            var flights = VtolDropFixture.Files();
            flights.Content[VtolDropFixture.Root] = Encoding.ASCII.GetBytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( short.zad ) ANIMATION_DEFINITION_FILE ( good.zad ) ) )");
            flights.Content["data/m1/zrdr/short.zad"] = Encoding.Latin1.GetBytes(VtolDropFixture.Definitions().Replace("delivery", "short_flight").Replace("callback", "short_callback").Replace("SCRIPT_FRAME_RATE ( 10 )", "SCRIPT_FRAME_RATE ( 100 )"));
            flights.Content["data/m1/zrdr/good.zad"] = Encoding.Latin1.GetBytes(VtolDropFixture.Definitions().Replace("delivery", "good_flight").Replace("callback", "good_callback"));
            File.WriteAllBytes(Path.Combine(compiled, "m1", "anim.zbd"), AnimationWriter.Write(VtolDropFixture.Compile(flights, token).Package));
            await main.ViewModel.OpenRootAsync(compiled, token);
            world = Document(await Job("open_document", new() { ["path"] = Path.Combine(compiled, "m1", "gamez.zbd") }));
            await Preview();
            state = (await PathAction("show"))["parachute"]!;
            Assert.True(state["visible"]!.GetValue<bool>()); Assert.Equal(2, state["candidates"]!.AsArray().Count);
            Assert.Contains("did not complete", state["analysisRefusal"]!.GetValue<string>());
            Assert.Equal(0, state["sampleCount"]!.GetValue<int>()); Assert.Equal(0, state["releaseCount"]!.GetValue<int>());
            Assert.Null(state["selected"]); Assert.Null(state["windows"]);
            int failedEntry = state["entry"]!.GetValue<int>();
            var picker = (ComboBox)main.FindName("ParachuteFlights");
            Assert.True(picker.IsEnabled); picker.SelectedIndex = 1;
            await UntilAsync(async () => (await State())?["entry"]?.GetValue<int>() != failedEntry && (await State())?["analysisRefusal"] == null);
            state = (await State())!;
            Assert.Equal(2, state["windows"]!.AsArray().Count); Assert.Equal(8, state["releaseCount"]!.GetValue<int>());
            await PathAction("analyze", new() { ["entry"] = failedEntry });
            state = (await State())!;
            Assert.NotNull(state["analysisRefusal"]); Assert.Equal(0, state["sampleCount"]!.GetValue<int>());
            Assert.Equal(0, state["releaseCount"]!.GetValue<int>()); Assert.Null(state["windows"]);
            var unsupportedQuery = await client.CallToolAsync("zstudio_parachute", await Arguments("samples", new() { ["query"] = "ignored" }), cancellationToken: token);
            Assert.True(unsupportedQuery.IsError);

            async Task CloseOwnerWithSave()
            {
                Exception? dialogFailure = null;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    var dialog = main.OwnedWindows.Cast<Window>().SingleOrDefault(w => w.Title == "Unsaved changes");
                    if (dialog == null) return;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        var buttons = ((Grid)dialog.Content).Children.OfType<WrapPanel>().Single();
                        buttons.Children.OfType<Button>().Single(b => b.Content as string is "Save" or "Save As…")
                            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }
                    catch (Exception ex) { dialogFailure = ex; dialog.Close(); }
                };
                timer.Start();
                try { await main.ViewModel.CloseAsync(owner); if (dialogFailure != null) throw dialogFailure; }
                finally { timer.Stop(); }
            }

            async Task SupersedeRefresh(string action, Dictionary<string, object?>? extra = null)
            {
                TaskCompletionSource refreshing = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using var releaseRefresh = new SemaphoreSlim(0);
                // Hold the old worker even after supersession, so its canceled continuation runs only
                // after the real GUI has published a newer session and begun its independent draft.
                main.ParachuteAnalysisPreparing = _ => { refreshing.TrySetResult(); Assert.True(releaseRefresh.Wait(TimeSpan.FromSeconds(20))); };
                // The accepted mutations use the real protocol job. Ordinary GUI analysis uses its
                // existing ReportUi boundary so a stale cancellation cannot overwrite newer status.
                Task accepted;
                if (action is "show" or "analyze")
                {
                    int entry = (await State())!["entry"]!.GetValue<int>();
                    Func<Task> work = () => (Task)typeof(MainWindow).GetMethod(action == "show" ? "ShowParachutesAsync" : "AnalyzeParachuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(main, action == "show" ? [CancellationToken.None, null, null] : [entry, CancellationToken.None])!;
                    accepted = (Task)typeof(MainWindow).GetMethod("ReportUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [work])!;
                }
                else accepted = PathAction(action, extra);
                try
                {
                    var first = await Task.WhenAny(refreshing.Task, accepted).WaitAsync(token);
                    if (first == accepted) { await accepted; Assert.Fail("The operation did not reach its analysis worker."); }
                    main.ParachuteAnalysisPreparing = null;
                    // Analyze retains its old panel until replacement; hide it before the GUI Show.
                    if (action == "analyze") typeof(MainWindow).GetMethod("CloseParachutes", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, []);
                    var show = (Button)main.FindName("ParachuteButton");
                    Assert.True(show.IsEnabled); show.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, show));
                    await UntilAsync(async () => (await State())?["visible"]?.GetValue<bool>() == true);
                    Click("numeric");
                    var newer = (await State())!;
                    string snapshot = newer["snapshot"]!.GetValue<string>(), newerToken = newer["draft"]!["token"]!.GetValue<string>();
                    string input = newer["draft"]!["input"]!.GetValue<string>();
                    main.ViewModel.Status = "Newer Parachute draft retained";
                    releaseRefresh.Release(); await accepted;
                    var retained = (await State())!;
                    Assert.True(retained["visible"]!.GetValue<bool>());
                    Assert.Equal(snapshot, retained["snapshot"]!.GetValue<string>());
                    Assert.Equal(newerToken, retained["draft"]!["token"]!.GetValue<string>());
                    Assert.Equal(input, retained["draft"]!["input"]!.GetValue<string>());
                    Assert.Equal("Newer Parachute draft retained", main.ViewModel.Status);
                    Click("cancel");
                }
                finally { releaseRefresh.Release(); main.ParachuteAnalysisPreparing = null; }
            }
            async Task Unlock() => await Call("pickup_lock", new() { ["document"] = Id(world), ["revision"] = world.Revision, ["locked"] = false });
            Recoil.Zbd.Rendering.SceneViewport Viewport() => (Recoil.Zbd.Rendering.SceneViewport)((ContentControl)main.FindName("SceneHost")).Content;
            async Task SelectFirstWindow() => await PathAction("select", new() { ["window"] = (await State())!["windows"]![0]!["id"]!.GetValue<string>() });
            async Task<JsonNode?> State()
            {
                var current = await Call("state", new());
                return (await Call("preview_state", new() { ["preview"] = current["preview"]!.GetValue<string>() }))["parachute"];
            }
            async Task<Dictionary<string, object?>> Arguments(string action, Dictionary<string, object?>? extra = null)
            {
                var current = await Call("state", new());
                var args = extra ?? new(); args["action"] = action; args["preview"] = current["preview"]!.GetValue<string>();
                args["document"] = Id(world); args["revision"] = world.Revision;
                if (action != "show")
                {
                    var shown = (await State())!; args["snapshot"] = shown["snapshot"]!.GetValue<string>();
                    args["ownerRevision"] = shown["ownerRevision"]!.GetValue<long>();
                }
                return args;
            }
            async Task<JsonNode> PathAction(string action, Dictionary<string, object?>? extra = null) => await Job("parachute", await Arguments(action, extra));
            void Click(string action)
            {
                var panel = (DependencyObject)main.FindName("ParachutePanel");
                var button = Descendants(panel).OfType<Button>().Single(b => b.Tag as string == action);
                Assert.True(button.IsEnabled); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            }
            async Task Until(Func<bool> ready) { while (!ready()) await Task.Delay(10, token); }
            async Task UntilAsync(Func<Task<bool>> ready) { while (!await ready()) await Task.Delay(10, token); }
            async Task<JsonNode> Call(string name, Dictionary<string, object?> args)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.False(result.IsError == true, name + ": " + text); return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> args, string expectedState = "completed")
            {
                var job = await Call(name, args);
                if (job["State"] == null) return job;
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expectedState, name + ": " + job.ToJsonString()); return job["result"]!;
            }
            DocumentModel Document(JsonNode json) => main.ViewModel.Documents.Single(d => Id(d) == (json["document"]?["id"] ?? json["id"])!.GetValue<string>());
            async Task Preview() => await ((Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).WaitAsync(token);
        }
        finally
        {
            typeof(MainWindow).GetMethod("CloseParachutes", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, []);
            foreach (var document in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(document);
            main.Close();
        }
    }
    private static string Id(DocumentModel document) => document.SessionId.ToString();
    private static VtolDropWindow FirstWindow(DocumentModel owner) => Assert.Single(VtolDropWindows.Discover(owner.AnimationEdits!.Package).Candidates).Windows[0];
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
