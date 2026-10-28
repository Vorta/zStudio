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
using zStudio.Core.Animation;
using zStudio.Automation;
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

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
            // A source map's drop windows are the opened flight's: Animation mode's Inspector → Definition → Drop windows.
            int delivery = ((zStudio.Core.Sources.SourceSceneSnapshot)world.Document).AnimationDefinitions.Definitions.Single(d => d.Name == "delivery").Index;
            Assert.Equal("not_ready", (await Job("animation_paths", new() { ["action"] = "show", ["preview"] = (await Call("state", new()))["preview"]!.GetValue<string>(), ["document"] = Id(world), ["revision"] = world.Revision }, "failed"))["code"]!.GetValue<string>());
            await Job("animation_mode", new() { ["action"] = "open", ["document"] = Id(world), ["revision"] = world.Revision, ["definition"] = delivery, ["preview"] = (await Call("state", new()))["preview"]!.GetValue<string>() });
            // Explicit export supplies the separate compiled-editor regression fixture.
            string compiled = Path.Combine(fixture.Root, "compiled");
            var exported = await zStudio.Core.Sources.SourceBuilder.ExportAsync(fixture.Project, compiled,
                ["m1/gamez.zbd", "m1/anim.zbd", "m1/zrdr.zbd"], token: token);
            Assert.All(exported.Outputs, output => Assert.Null(output.Error));
            var state = (await PathAction("show"))["parachute"]!;
            Assert.Equal(2, state["windows"]!.AsArray().Count);
            Assert.True((await PathAction("frame"))["framed"]!.GetValue<bool>());
            Assert.True(state["cycleSeconds"]!.GetValue<double>() >= 10);
            // Analysis is passive; choosing a window explicitly opens its editable source card.
            Assert.Null(state["selected"]); Assert.Null(Viewport().SelectedParachuteWindow);
            Assert.StartsWith("VTOL drop windows ready:", main.ViewModel.Status);
            Guid secondWindow = Guid.Parse(state["windows"]![1]!["id"]!.GetValue<string>());
            Assert.True(Viewport().SelectParachuteWindow(secondWindow));
            await ((Task)typeof(MainWindow).GetField("parachuteSelectionWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).WaitAsync(token);
            main.UpdateLayout();
            Assert.True(((FrameworkElement)main.FindName("ParachutePanel")).IsVisible);
            Assert.Equal(secondWindow, (await State())!["selected"]!.GetValue<Guid>());
            var windowsPicker = (Selector)main.FindName("ParachuteWindows");
            Assert.True(windowsPicker.IsVisible); Assert.True(windowsPicker.IsEnabled);
            windowsPicker.SelectedIndex = 0;
            await ((Task)typeof(MainWindow).GetField("parachuteSelectionWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).WaitAsync(token);
            Assert.Equal(Guid.Parse(state["windows"]![0]!["id"]!.GetValue<string>()), (await State())!["selected"]!.GetValue<Guid>());
            Assert.Equal((await State())!["selected"]!.GetValue<Guid>(), Viewport().SelectedParachuteWindow);
            Assert.True(((FrameworkElement)main.FindName("ParachuteMoveButtons")).IsEnabled);
            TaskCompletionSource analyzing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var releaseAnalysis = new SemaphoreSlim(0))
            {
                main.ParachuteAnalysisPreparing = ct => { analyzing.TrySetResult(); Assert.True(releaseAnalysis.Wait(TimeSpan.FromSeconds(20), ct)); };
                var analysisJob = await Call("animation_paths", await Arguments("analyze", new() { ["entry"] = state["entry"]!.GetValue<int>() }));
                var analysis = AwaitJob(analysisJob, "canceled");
                try
                {
                    var first = await Task.WhenAny(analyzing.Task, analysis).WaitAsync(token);
                    if (first == analysis) Assert.Fail("Analysis did not reach its worker: " + (await analysis).ToJsonString());
                    Assert.False(((UIElement)main.FindName("ParachutePanel")).IsEnabled);
                    var refusal = Assert.Throws<TargetInvocationException>(() => typeof(MainWindow).GetMethod("BeginParachute", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [false]));
                    Assert.Equal("busy", Assert.IsType<StudioCommandException>(refusal.InnerException).Code);
                    Assert.Null((await State())!["draft"]);
                    Assert.Equal("Analyzing VTOL drop windows…", main.ViewModel.Status);
                    await Call("operation", new() { ["id"] = analysisJob["id"]!.GetValue<string>(), ["cancel"] = true });
                    releaseAnalysis.Release(); await analysis;
                    Assert.Equal("VTOL drop-window analysis canceled.", main.ViewModel.Status);
                    main.ParachuteAnalysisPreparing = null;
                    await PathAction("analyze", new() { ["entry"] = state["entry"]!.GetValue<int>() });
                    Assert.StartsWith("VTOL drop windows ready:", main.ViewModel.Status);
                    Assert.True(((UIElement)main.FindName("ParachutePanel")).IsEnabled);
                    Assert.Null((await State())!["selected"]); Assert.Null(Viewport().SelectedParachuteWindow);
                }
                finally { releaseAnalysis.Release(); main.ParachuteAnalysisPreparing = null; }
            }
            main.ParachuteAnalysisPreparing = _ => throw new IOException("Parachute analysis failure fixture");
            try
            {
                await Job("animation_paths", await Arguments("analyze", new() { ["entry"] = state["entry"]!.GetValue<int>() }), "failed");
                Assert.Equal("VTOL drop-window analysis failed: Parachute analysis failure fixture", main.ViewModel.Status);
            }
            finally { main.ParachuteAnalysisPreparing = null; }
            // The callback's PLAYER_RANGE follows the map's scenario, never a forced value: with the scenario's reference
            // position at the flight path the player receives its drops.
            Assert.Equal("[]", (await PathAction("releases"))["items"]!.ToJsonString());
            var route = (await PathAction("samples"))["items"]![0]!["position"]!;
            string beside = string.Join(",", new[] { "x", "y", "z" }.Select(axis => route[axis]!.GetValue<double>().ToString(System.Globalization.CultureInfo.InvariantCulture)));
            // The scenario's player, set through animation_scenario (as the Scenario tab's Custom player does).
            await Job("animation_scenario", new() { ["preview"] = (await Call("state", new()))["preview"]!.GetValue<string>(), ["action"] = "inputs",
                ["player"] = new { kind = "custom", x = route["x"]!.GetValue<double>(), y = route["y"]!.GetValue<double>(), z = route["z"]!.GetValue<double>() } });
            // The shown analysis belongs to the previous scenario: stale, refusing analyze and moves, until show runs again.
            Assert.True((await State())!["stale"]!.GetValue<bool>());
            Assert.Equal("context_changed", (await Job("animation_paths", await Arguments("analyze", new() { ["entry"] = state["entry"]!.GetValue<int>() }), "failed"))["code"]!.GetValue<string>());
            state = (await PathAction("show"))["parachute"]!;
            Assert.False(state["stale"]!.GetValue<bool>()); Assert.Equal("Custom", state["player"]!["kind"]!.GetValue<string>());
            Assert.NotEqual("[]", (await PathAction("releases"))["items"]!.ToJsonString());
            Assert.Null(state["selected"]);
            await PathAction("select", new() { ["window"] = state["windows"]![0]!["id"]!.GetValue<string>() });
            state = (await PathAction("begin"))["parachute"]!;
            string draft = state["draft"]!["token"]!.GetValue<string>();
            var baselineReleases = (await PathAction("releases"))["items"]!.ToJsonString();
            await PathAction("set", new() { ["token"] = draft, ["start"] = 2 });
            var displayedReleases = (await PathAction("releases"))["items"]!.ToJsonString();
            Assert.NotEqual(baselineReleases, displayedReleases);
            var rendererPreview = (zStudio.Rendering.ParachuteWindowOverlay)typeof(zStudio.Rendering.SceneViewport)
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
            Assert.Equal(displayedFirstRelease, ((zStudio.Rendering.ParachuteWindowOverlay)typeof(zStudio.Rendering.SceneViewport)
                .GetField("parachutePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Viewport())!).Releases[0].Phase);
            Assert.Equal("invalid_argument", (await Job("animation_paths", await Arguments("confirm", new() { ["token"] = draft }), "failed"))["code"]!.GetValue<string>());
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
                var canceled = Job("animation_paths", await Arguments("confirm", new() { ["token"] = draft }), "failed");
                try
                {
                    var first = await Task.WhenAny(preparing.Task, canceled).WaitAsync(token);
                    if (first == canceled) Assert.Fail("Confirmation did not reach source preparation: " + (await canceled).ToJsonString());
                    Assert.False(Viewport().IsParachutePointerMoving);
                    // Already queued renderer/numeric callbacks cannot replace the accepted worker plan or its input.
                    ((Action<int, double>)typeof(zStudio.Rendering.SceneViewport).GetField("ParachuteMoveRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Viewport())!)(150, 0);
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
                    Assert.Equal("VTOL drop-window move canceled.", main.ViewModel.Status);
                    Assert.Null((await State())!["draft"]);
                    Assert.Equal(baselineReleases, (await PathAction("releases"))["items"]!.ToJsonString());
                    Assert.False(world.IsDirty); Assert.Empty(world.SourceWorld!.Workspace.History);
                }
                finally { release.Release(); main.SourceEditPreparing = null; }
            }

            await CheckGuiDiscard();
            state = (await PathAction("begin"))["parachute"]!;
            draft = state["draft"]!["token"]!.GetValue<string>();
            await PathAction("set", new() { ["token"] = draft, ["start"] = 2 });
            TaskCompletionSource preparingCancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new SemaphoreSlim(0))
            {
                main.SourceEditPreparing = ct => { preparingCancel.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(20), ct)); };
                int problems = main.ViewModel.Problems.Count;
                Click("confirm");
                try
                {
                    await preparingCancel.Task.WaitAsync(token);
                    main.CancelRunningOperation();
                    await GuiConfirmFinished();
                    Assert.Equal("VTOL drop-window move canceled.", main.ViewModel.Status);
                    Assert.Equal(problems, main.ViewModel.Problems.Count);
                    Assert.Equal(draft, (await State())!["draft"]!["token"]!.GetValue<string>());
                    Assert.Equal("2", (await State())!["draft"]!["input"]!.GetValue<string>());
                    Assert.False(world.IsDirty); Assert.Empty(world.SourceWorld!.Workspace.History);
                }
                finally { release.Release(); main.SourceEditPreparing = null; }
            }
            main.SourceEditPreparing = _ => throw new IOException("Parachute source preparation failure fixture");
            try
            {
                var failed = await Job("animation_paths", await Arguments("confirm", new() { ["token"] = draft }), "failed");
                Assert.Equal("io_failed", failed["code"]!.GetValue<string>());
                Assert.Equal("VTOL drop-window move failed: Parachute source preparation failure fixture", main.ViewModel.Status);
                Assert.Equal(draft, (await State())!["draft"]!["token"]!.GetValue<string>());
                Assert.False(world.IsDirty); Assert.Empty(world.SourceWorld!.Workspace.History);
                int problems = main.ViewModel.Problems.Count;
                Click("confirm"); await GuiConfirmFinished();
                Assert.Equal("Parachute source preparation failure fixture", main.ViewModel.Status);
                Assert.Equal(problems + 1, main.ViewModel.Problems.Count);
                Assert.Contains(main.ViewModel.Problems, p => p.Message == "Parachute source preparation failure fixture");
            }
            finally { main.SourceEditPreparing = null; }
            long revisionBeforeFailure = world.Revision;
            var prepareModel = main.PreparingSourceWorldModel;
            main.PreparingSourceWorldModel = _ => throw new InvalidDataException("Parachute rebuild failure fixture");
            try
            {
                var failed = await Job("animation_paths", await Arguments("confirm", new() { ["token"] = draft }), "failed");
                Assert.Equal("build_failed", failed["code"]!.GetValue<string>());
                Assert.Contains("was reverted", main.ViewModel.Status);
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
            TaskCompletionSource presenting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string[]> releasePresentation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var discover = main.DiscoverTexturePacksAsync;
            main.DiscoverTexturePacksAsync = (_, _, _) => { presenting.TrySetResult(); return releasePresentation.Task; };
            try
            {
                var accepted = PathAction("confirm", new() { ["token"] = draft });
                var first = await Task.WhenAny(presenting.Task, accepted).WaitAsync(token);
                if (first == accepted) { await accepted; Assert.Fail("Source confirmation did not reach replacement presentation."); }
                main.DiscoverTexturePacksAsync = discover;
                // Source projects now have one map and no concurrently open individual source files.
                // Keep this barrier focused on completion after presentation. Maps/newer-root supersession
                // is covered by SourceWorldMcpChecks; compiled owner/navigation races remain below.
                Assert.False(accepted.IsCompleted);
                releasePresentation.TrySetResult([]);
                world = Document((await accepted)["document"]!);
                Assert.Same(world, main.ViewModel.SelectedDocument);
            }
            finally { main.DiscoverTexturePacksAsync = discover; releasePresentation.TrySetResult([]); }
            main.ViewModel.SelectedDocument = world; await Preview();
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
            // The first compiled Show opens its companion before analysis; that subordinate load
            // must not replace progress with the animation description or steal terminal status.
            TaskCompletionSource compiledAnalyzing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var releaseCompiledAnalysis = new SemaphoreSlim(0))
            {
                main.ParachuteAnalysisPreparing = ct => { compiledAnalyzing.TrySetResult(); Assert.True(releaseCompiledAnalysis.Wait(TimeSpan.FromSeconds(20), ct)); };
                var compiledShow = PathAction("show");
                try
                {
                    var first = await Task.WhenAny(compiledAnalyzing.Task, compiledShow).WaitAsync(token);
                    if (first == compiledShow) Assert.Fail("Compiled Show did not reach analysis: " + (await compiledShow).ToJsonString());
                    Assert.Equal("Analyzing VTOL drop windows…", main.ViewModel.Status);
                    releaseCompiledAnalysis.Release();
                    state = (await compiledShow)["parachute"]!;
                    Assert.StartsWith("VTOL drop windows ready:", main.ViewModel.Status);
                }
                finally { releaseCompiledAnalysis.Release(); main.ParachuteAnalysisPreparing = null; }
            }
            var owner = main.ViewModel.Documents.Single(d => Id(d) == state["owner"]!.GetValue<string>());
            Assert.NotSame(world, owner); Assert.Same(world, main.ViewModel.SelectedDocument);
            Assert.EndsWith("anim.zbd", owner.Path, StringComparison.OrdinalIgnoreCase);
            // A compiled world's drop windows are shown and analyzed, never selected or moved.
            Assert.Null(state["selected"]);
            string compiledWindow = state["windows"]![0]!["id"]!.GetValue<string>();
            Assert.Equal("read_only", (await Job("animation_paths", await Arguments("select", new() { ["window"] = compiledWindow }), "failed"))["code"]!.GetValue<string>());
            Assert.Equal("read_only", (await Job("animation_paths", await Arguments("begin"), "failed"))["code"]!.GetValue<string>());
            Assert.False(((Selector)main.FindName("ParachuteWindows")).IsEnabled);
            Assert.False(owner.IsDirty); Assert.False(world.IsDirty);

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
            // A compiled world has no source scenario: MCP sets the player its drop analysis measures from.
            var compiledPlayer = await Job("animation_options", new() { ["preview"] = (await Call("state", new()))["preview"]!.GetValue<string>(), ["changes"] = new { activationTarget = beside } });
            Assert.Equal(3, compiledPlayer["activationTarget"]!.AsArray().Count);
            state = (await PathAction("show"))["parachute"]!;
            Assert.True(state["visible"]!.GetValue<bool>()); Assert.Equal(2, state["candidates"]!.AsArray().Count);
            Assert.Contains("did not complete", state["analysisRefusal"]!.GetValue<string>());
            Assert.StartsWith("VTOL flight unavailable:", main.ViewModel.Status);
            Assert.Equal(0, state["sampleCount"]!.GetValue<int>()); Assert.Equal(0, state["releaseCount"]!.GetValue<int>());
            Assert.Null(state["selected"]); Assert.Null(state["windows"]);
            int failedEntry = state["entry"]!.GetValue<int>();
            var picker = (ComboBox)main.FindName("ParachuteFlights");
            Assert.True(picker.IsEnabled); picker.SelectedIndex = 1;
            await UntilAsync(async () => (await State())?["entry"]?.GetValue<int>() != failedEntry && (await State())?["analysisRefusal"] == null);
            state = (await State())!;
            Assert.Equal(2, state["windows"]!.AsArray().Count); Assert.Equal(8, state["releaseCount"]!.GetValue<int>());
            Assert.StartsWith("VTOL drop windows ready:", main.ViewModel.Status);
            await PathAction("analyze", new() { ["entry"] = failedEntry });
            state = (await State())!;
            Assert.NotNull(state["analysisRefusal"]); Assert.Equal(0, state["sampleCount"]!.GetValue<int>());
            Assert.StartsWith("VTOL flight unavailable:", main.ViewModel.Status);
            Assert.Equal(0, state["releaseCount"]!.GetValue<int>()); Assert.Null(state["windows"]);
            var unsupportedQuery = await client.CallToolAsync("zstudio_animation_paths", await Arguments("samples", new() { ["query"] = "ignored" }), cancellationToken: token);
            Assert.True(unsupportedQuery.IsError);
            await PathAction("hide");
            Assert.Equal("VTOL drop windows hidden.", main.ViewModel.Status);

            bool Confirming() => (bool)typeof(MainWindow).GetField("acceptingParachute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            async Task GuiConfirmFinished()
            {
                await Until(() => !Confirming());
                // The semantic operation clears its flag before its outer GUI continuation reports
                // or suppresses errors. Drain that continuation before asserting visible effects.
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            async Task CheckGuiDiscard()
            {
                var pending = (await PathAction("begin"))["parachute"]!["draft"]!["token"]!.GetValue<string>();
                await PathAction("set", new() { ["token"] = pending, ["start"] = 2 });
                TaskCompletionSource preparingGui = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using var release = new SemaphoreSlim(0);
                main.SourceEditPreparing = ct => { preparingGui.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(20), ct)); };
                int problems = main.ViewModel.Problems.Count, toolTab = ((TabControl)main.FindName("ToolTabs")).SelectedIndex;
                Click("confirm");
                try
                {
                    await preparingGui.Task.WaitAsync(token);
                    System.Windows.Input.Keyboard.ClearFocus();
                    Viewport().RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(main), Environment.TickCount, System.Windows.Input.Key.Escape)
                        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    release.Release(); await GuiConfirmFinished();
                    Assert.Equal("VTOL drop-window move canceled.", main.ViewModel.Status);
                    Assert.Equal(problems, main.ViewModel.Problems.Count);
                    Assert.Equal(toolTab, ((TabControl)main.FindName("ToolTabs")).SelectedIndex);
                    Assert.False(world.IsDirty); Assert.Empty(world.SourceWorld!.Workspace.History);
                    Assert.Null((await State())!["draft"]);
                }
                finally { release.Release(); main.SourceEditPreparing = null; }
            }
            zStudio.Rendering.SceneViewport Viewport() => (zStudio.Rendering.SceneViewport)((ContentControl)main.FindName("SceneHost")).Content;
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
                if (action != "show") args["snapshot"] = (await State())!["snapshot"]!.GetValue<string>();
                return args;
            }
            async Task<JsonNode> PathAction(string action, Dictionary<string, object?>? extra = null) => await Job("animation_paths", await Arguments(action, extra));
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
                return await AwaitJob(job, expectedState);
            }
            async Task<JsonNode> AwaitJob(JsonNode job, string expectedState = "completed")
            {
                if (job["State"] == null) return job;
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expectedState, job.ToJsonString()); return job["result"]!;
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
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
