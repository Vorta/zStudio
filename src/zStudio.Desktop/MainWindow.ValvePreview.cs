using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// The valve test (design/mw3-valves-ui.md §3): the mission's valves run in memory from 0 s. One switch starts and ends it
/// (the Valves row, the valve card, MCP ai_valve_preview); a banner over the view carries its clock and transport, valve rows
/// show live values and the selected valve's card its value, timers and the test's activity. Nothing is saved.
/// </summary>
public partial class MainWindow
{
    private Mw3ValveSimulation? valveSimulation;
    private DocumentModel? valveDocument;
    private SourceSceneSnapshot? valveMap;
    private SceneViewport? valveViewport;
    private Guid? valveAttachment;
    private AnimationAudio? valveAudio;
    private DispatcherTimer? valveTimer;
    private bool valveBusy, valveStarting;
    private long valveGeneration;
    private CancellationTokenSource? valveLifetime;
    private string[] valvePreparedSounds = [];
    private string? valveAudioProblem, valveFailure;
    private readonly Stopwatch valveClock = new();
    private double valvePlayOrigin;
    /// <summary>
    /// The test as its last finished step left it: everything the banner, the rows and the cards show. They never read the
    /// simulation itself, which a background step may be changing (its reads count work and guard recursion in shared state).
    /// </summary>
    private sealed record ValveTestView(Guid Simulation, double Time, bool Halted, bool Aborted, IReadOnlyDictionary<string, int> Values, Mw3ValveTimer[] Timers,
        Mw3ValveTrace[] Trace, IReadOnlyDictionary<string, Mw3ValveActorState> Actors, IReadOnlyDictionary<string, Mw3ValveRoute> Routes);
    private ValveTestView? valveView;
    /// <summary>The selected valve's Test section while the test runs: its controls stay (and what is typed in them) while its readouts follow the test.</summary>
    private sealed class ValveTestPanel(string valve, Guid simulation)
    {
        internal readonly string Valve = valve;
        internal readonly Guid Simulation = simulation;
        internal readonly CardSection Section = new("Test");
        internal readonly WrapPanel Controls = new() { Margin = new(0, 0, 0, 4) };
        internal readonly StackPanel Inputs = new(), Live = new();
        internal readonly List<Button> Buttons = [];
        internal Button? Off, On;
        internal int? Shown;
    }
    private ValveTestPanel? valveTestPanel;
    private long valveShownSequence = -1;
    private readonly Stopwatch valveShown = new();
    private ViewportChip? valveBanner;
    private TextBlock? valveBannerText;
    private Button? valveBannerPlay, valveBannerNext;

    /// <summary>The valve test runs or is starting.</summary>
    private bool ValveTesting => valveSimulation != null || valveStarting;
    bool ISourceSectionState.ValveTesting => ValveTesting;
    int? ISourceSectionState.ValveValue(string name) => valveView is { } view ? view.Values.GetValueOrDefault(name) : null;

    private Mw3ValveSimulation CurrentValveSimulation()
    {
        if (valveBusy) throw new StudioCommandException("busy", "A valve test operation is running.");
        if (valveSimulation == null || valveDocument != shownDocument || valveMap != shownDocument?.Document || valveViewport != scene)
            throw new StudioCommandException("stale_record", "Start the valve test for the current mission.");
        return valveSimulation;
    }
    private void StopValveSimulation()
    {
        ++valveGeneration;
        valveLifetime?.Cancel(); valveLifetime?.Dispose(); valveLifetime = null;
        valveTimer?.Stop(); valveClock.Stop();
        valveAudio?.Stop();
        var viewport = valveViewport; var attachment = valveAttachment; var audio = valveAudio;
        valveAudio = null;
        valveSimulation = null; valveAttachment = null; valveDocument = null; valveMap = null; valveViewport = null;
        valvePreparedSounds = []; valveAudioProblem = null; valveView = null; valveTestPanel = null; valveStarting = false; valveShownSequence = -1;
        if (attachment is { } id) viewport?.DetachWorldAnimation(id);
        viewport?.SetValveRouteStates(null); audio?.Dispose();
    }
    /// <summary>Ends the test (its switch, a source edit, another mission, Animation mode): the view, the rows and the cards show the map at rest.</summary>
    private void EndValveTest()
    {
        bool was = ValveTesting;
        StopValveSimulation();
        if (was) RefreshValveTestUi(force: true);
    }
    /// <summary>The Valves row's Test, the card's button and the banner's End test.</summary>
    private void ToggleValveTest()
    {
        if (ValveTesting) { EndValveTest(); ViewModel.Status = "Valve test ended."; return; }
        _ = RunUi(() => ValvePreviewActionAsync("prepare", "", 0, null, "", -1, CancellationToken.None));
    }
    private object ValvePreviewState()
    {
        if (valveBusy) return new { loading = true };
        if (valveSimulation == null) return new { prepared = false };
        var simulation = CurrentValveSimulation();
        if (simulation.Runtime.Halted) return new { prepared = true, simulation = simulation.Id, time = simulation.Time, halted = true, trace = simulation.Runtime.Trace.TakeLast(64) };
        var values = simulation.Runtime.Values();
        return new { prepared = true, simulation = simulation.Id, time = simulation.Time, playing = valveTimer?.IsEnabled == true,
            halted = simulation.Runtime.Halted || simulation.Faulted, aborted = simulation.Aborted,
            audio = new { prepared = valveAudio?.IsPrepared == true, muted = valveAudio?.Muted == true, problem = valveAudioProblem },
            values = values.Take(64).Select(v => new { name = MissionAiValves.Short(v.Name), nameCharacters = v.Name.Length, v.Value, v.Condition }), valueCount = values.Count,
            valuesTruncated = values.Count > 64, timers = simulation.Runtime.Timers.Take(64).Select(t => new { target = MissionAiValves.Short(t.Target), t.Value, t.Due }), timerCount = simulation.Runtime.Timers.Count,
            nextTimer = simulation.Runtime.Timers.Count > 0 ? simulation.Runtime.Timers.Min(t => t.Due) : (float?)null,
            actors = simulation.Actors.Values.Take(64).Select(a => new { name = MissionAiValves.Short(a.Name), nameCharacters = a.Name.Length,
                a.PoweredDown, a.Destroyed, a.Team, a.Strategy, position = a.Position is { } p ? JsonData.Vector(p) : null, a.Yaw, a.Switches }),
            actorCount = simulation.Actors.Count, actorsTruncated = simulation.Actors.Count > 64, trace = simulation.Runtime.Trace.TakeLast(64) };
    }
    private async Task ValvePreviewActionAsync(string action, string name, int value, double? seconds, string node, int slot, CancellationToken token)
    {
        if (action == "state") return;
        if (action == "stop") { EndValveTest(); return; }
        token.ThrowIfCancellationRequested();
        RequireNoDrafts();
        if (valveBusy) throw new StudioCommandException("busy", "A valve test operation is running.");
        if (action is "prepare" or "reset")
        {
            if (InAnimationMode) throw new StudioCommandException("busy", "Exit Animation mode before testing valves.");
            var document = shownDocument ?? throw new StudioCommandException("not_ready", "Open an MW3 source map.");
            var source = ValveSource(document);
            var viewport = scene ?? throw new StudioCommandException("not_ready", "The world is not ready.");
            var baseline = viewport.Mission ?? throw new StudioCommandException("not_ready", "The mission is not ready.");
            int lod = Math.Max(0, LodCombo.SelectedIndex); int sceneGeneration = viewport.SceneGeneration;
            StopValveSimulation(); long generation = valveGeneration; valveBusy = valveStarting = true; valveFailure = null;
            valveLifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            using var prepare = CancellationTokenSource.CreateLinkedTokenSource(token, valveLifetime.Token);
            token = prepare.Token;
            RefreshValveTestUi(force: true);
            try
            {
                var context = await Task.Run(() => AnimationPreviewContext.FromSourceBaseline(source, baseline.ValveStartupBaseline(token), token), token);
                var resource = source.Resources.Values.FirstOrDefault(r => System.IO.Path.GetFileName(r.Path).Equals("valves.zrd", StringComparison.OrdinalIgnoreCase));
                var program = resource?.Root == null ? new Mw3ValveProgram([]) : Mw3ValveProgram.Read(resource.Root, token);
                var simulation = await Task.Run(() => new Mw3ValveSimulation(context, program, token, lod), token);
                token.ThrowIfCancellationRequested();
                if (generation != valveGeneration || document != shownDocument || source != document.Document || scene != viewport || baseline != viewport.Mission ||
                    sceneGeneration != viewport.SceneGeneration || lod != Math.Max(0, LodCombo.SelectedIndex))
                    throw new StudioCommandException("context_changed", "The mission changed while the valve test started.");
                valveDocument = document; valveMap = source; valveViewport = viewport; valveSimulation = simulation;
                valveAttachment = viewport.AttachWorldAnimation(context, ViewModel.Resolver!, sceneGeneration, shutdownToken);
                valveAudio = new AnimationAudio { Volume = .5f };
                valveAudio.Diagnostic += message =>
                {
                    if (generation != valveGeneration) return;
                    valveAudioProblem = MissionAiValves.Short(message.Replace("Uncheck Mute to retry.", "Restart the test to retry."), 512);
                    ViewModel.Status = valveAudioProblem;
                };
                await PrepareValveAudioAsync(simulation, token, force: true);
                token.ThrowIfCancellationRequested();
                if (generation != valveGeneration || source != shownDocument?.Document || viewport != scene ||
                    sceneGeneration != viewport.SceneGeneration || lod != Math.Max(0, LodCombo.SelectedIndex))
                    throw new StudioCommandException("context_changed", "The mission changed while the valve test prepared its sounds.");
            }
            catch (Exception ex)
            {
                valveBusy = false; StopValveSimulation();
                // The card and the banner say why the test did not start (a record the game cannot read, say).
                if (ex is not OperationCanceledException) valveFailure = MissionAiValves.Short(ex.Message, 400);
                RefreshValveTestUi(force: true); throw;
            }
            finally { valveBusy = false; valveStarting = false; }
            PublishValveFrame(); RefreshValveTestUi(force: true);
            ViewModel.Status = "Testing valves: set one on its card or row. Nothing is saved.";
            return;
        }
        var current = CurrentValveSimulation();
        using var evaluation = CancellationTokenSource.CreateLinkedTokenSource(token, valveLifetime?.Token ?? shutdownToken);
        token = evaluation.Token;
        long operation = valveGeneration;
        if (action == "pause") { valveTimer?.Stop(); valveClock.Stop(); valveAudio?.Stop(); RefreshValveTestUi(force: true); return; }
        if (action == "play")
        {
            valvePlayOrigin = current.Time; valveClock.Restart();
            valveTimer ??= new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
            valveTimer.Tick -= ValveTick; valveTimer.Tick += ValveTick; valveTimer.Start(); RefreshValveTestUi(force: true); return;
        }
        valveTimer?.Stop(); valveClock.Stop();
        var networkNode = action == "traverse" ? scene!.AiNetworks.Find(node) ?? throw new StudioCommandException("stale_record", "Read the current AI node.") : ((AiNetwork Network, AiNode Node)?)null;
        if (action == "next" && current.Runtime.Timers.Count == 0) throw new StudioCommandException("invalid_argument", "No timer is waiting.");
        valveBusy = true;
        bool evaluated = false;
        try
        {
            await Task.Run(() =>
            {
                switch (action)
                {
                    case "set": if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a valve name."); current.Set(name, value, token); break;
                    case "update": if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a valve name."); current.Update(name, token); break;
                    case "step": current.Advance(current.Time + AnimationPlayer.StepSeconds, token); break;
                    case "advance": current.Advance(seconds ?? throw new ArgumentException("Enter an absolute time."), token); break;
                    // To the first tick on which the next timer runs: one due a moment later is still waiting.
                    case "next": current.Advance(Mw3ValveSimulation.TimerTime(current.Time, current.Runtime.Timers.Min(t => t.Due)), token); break;
                    case "traverse": current.Traverse(networkNode!.Value.Network, networkNode.Value.Node, slot, token); break;
                    default: throw new ArgumentException("Unknown valve test operation.");
                }
            }, token);
            evaluated = true;
            await PrepareValveAudioAsync(current, token);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            if (operation == valveGeneration && (evaluated || current.Faulted || token.IsCancellationRequested ||
                ex is not (ArgumentException or InvalidDataException))) { valveBusy = false; EndValveTest(); }
            throw;
        }
        finally { valveBusy = false; }
        if (operation != valveGeneration || current != valveSimulation || valveMap != shownDocument?.Document || valveViewport != scene)
        { EndValveTest(); throw new StudioCommandException("context_changed", "The mission changed during the valve test."); }
        PublishValveFrame(); RefreshValveTestUi(force: true);
    }
    private async void ValveTick(object? sender, EventArgs e)
    {
        if (valveBusy || valveTimer?.IsEnabled != true) return;
        try
        {
            var current = CurrentValveSimulation();
            long generation = valveGeneration;
            double target = Math.Min(3600, Math.Min(current.Time + .1, valvePlayOrigin + valveClock.Elapsed.TotalSeconds));
            valveBusy = true;
            var token = valveLifetime?.Token ?? shutdownToken;
            try
            {
                await Task.Run(() => current.Advance(target, token), token);
                await PrepareValveAudioAsync(current, token);
                token.ThrowIfCancellationRequested();
            }
            finally { valveBusy = false; }
            if (generation != valveGeneration || current != valveSimulation || valveMap != shownDocument?.Document || valveViewport != scene) { EndValveTest(); return; }
            PublishValveFrame(); RefreshValveTestUi(force: false);
            if (target == 3600) { valveTimer.Stop(); RefreshValveTestUi(force: true); }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        { EndValveTest(); if (ex is not OperationCanceledException) ViewModel.Status = ex.Message; }
    }
    /// <summary>
    /// A step finished (never while one runs): the view takes the frame and the route states, and the test's state is copied
    /// into <see cref="valveView"/> for everything that shows it.
    /// </summary>
    private void PublishValveFrame()
    {
        if (valveSimulation is not { } simulation || valveAttachment is not { } attachment || valveViewport != scene || valveBusy) return;
        scene!.UpdateWorldAnimation(attachment, simulation.CurrentFrame);
        valveAudio?.Update(simulation.CurrentFrame, true);
        Dictionary<string, Mw3ValveRoute> routes = []; Dictionary<string, bool> passable = [];
        Dictionary<string, int> values = new(StringComparer.Ordinal);
        try
        {
            if (!simulation.Runtime.Halted && !simulation.Faulted)
            {
                foreach (var network in scene.AiNetworks.Networks)
                foreach (var n in network.Nodes)
                foreach (var link in n.PreviewLinks.Where(l => l.Target != null))
                {
                    if (routes.Count == SceneViewport.MaximumRenderedAiLinks) break;
                    var route = Mw3ValveRoutes.Evaluate(network, n, link.Slot, simulation.Runtime);
                    routes[n.Id + "|" + link.Slot] = route; passable[n.Id + "|" + link.Slot] = route.Passable;
                }
                foreach (var valve in simulation.Runtime.Values()) values[valve.Name] = valve.Value;
            }
        }
        // A condition the test cannot evaluate halts it: the view says so and keeps what it had.
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException) { routes.Clear(); passable.Clear(); values.Clear(); }
        bool halted = simulation.Runtime.Halted || simulation.Faulted;
        scene.SetValveRouteStates(halted ? null : passable);
        valveView = new(simulation.Id, simulation.Time, halted, simulation.Aborted, halted ? valveView?.Values ?? values : values, [.. simulation.Runtime.Timers],
            [.. simulation.Runtime.Trace], simulation.Actors.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal), routes);
    }
    private async Task PrepareValveAudioAsync(Mw3ValveSimulation simulation, CancellationToken token, bool force = false)
    {
        token.ThrowIfCancellationRequested();
        var audio = valveAudio;
        if (audio == null || simulation != valveSimulation) return;
        var names = simulation.CurrentFrame.ActiveSounds.Concat(simulation.CurrentFrame.Sounds).Where(c => !c.Stop)
            .Select(c => c.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!force && names.SequenceEqual(valvePreparedSounds)) return;
        await audio.PrepareAsync(simulation.Context, 0, null, true, token, soundNames: names);
        token.ThrowIfCancellationRequested();
        if (simulation == valveSimulation && audio == valveAudio) valvePreparedSounds = names;
    }

    // ---------------------------------------------------------------- what shows the test

    /// <summary>
    /// The banner, the Valves rows and the cards follow the test. While it plays, the clock follows every frame and the rest
    /// only when something happened or at most five times a second (<paramref name="force"/>: now).
    /// </summary>
    private void RefreshValveTestUi(bool force)
    {
        RefreshValveBanner();
        long sequence = valveView is { Halted: false } view ? view.Trace.LastOrDefault()?.Sequence ?? 0 : -1;
        if (!force && sequence == valveShownSequence && valveShown.IsRunning && valveShown.ElapsedMilliseconds < 200) return;
        bool happened = force || sequence != valveShownSequence;
        valveShownSequence = sequence; valveShown.Restart();
        sceneTree?.RefreshValveRows();
        sectionCard?.RefreshValveTest();
        // An AI node's card shows its links open or closed, a vehicle's what the test did to it: they follow what happened
        // (the test starting or ending, a valve changing), not the clock. The card's own selection counts: a hidden object
        // selected in the World tree has a card and no selection in the view.
        if (happened && CurrentInspectionCard is { Selection: not null } inspection) inspection.Refresh();
        if (sourceChromeActive) RefreshSourceStatus();
    }
    private void RefreshValveBanner()
    {
        bool shown = ValveTesting && scene != null && SceneHost.Visibility == Visibility.Visible && !InAnimationMode;
        if (!shown) { if (valveBanner != null) valveBanner.Visibility = Visibility.Collapsed; return; }
        if (valveBanner == null)
        {
            valveBanner = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 12, 0, 0), MinHeight = 36, Padding = new(12, 0, 4, 0), FontSize = 13 };
            AutomationProperties.SetName(valveBanner, "Valve test");
            StackPanel content = new() { Orientation = Orientation.Horizontal };
            var icon = new PreviewIcon { Kind = "Valve", Width = 15, Height = 15, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(PreviewIcon.ForegroundProperty, "AccentFillColorDefaultBrush");
            content.Children.Add(icon);
            valveBannerText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0), MinWidth = 150 };
            valveBannerText.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
            content.Children.Add(valveBannerText);
            Button Icon(string kind, string name, Func<string> action)
            {
                Button button = new() { Content = new PreviewIcon { Kind = kind, Width = 14, Height = 14 }, Width = 32, Height = 28, Padding = new(0), Margin = new(0, 0, 2, 0), ToolTip = name, VerticalAlignment = VerticalAlignment.Center };
                button.SetResourceReference(StyleProperty, "CommandButton");
                ToolTipService.SetInitialShowDelay(button, 0);
                AutomationProperties.SetName(button, name);
                button.Click += async (_, _) => await RunUi(() => ValvePreviewActionAsync(action(), "", 0, null, "", -1, CancellationToken.None));
                content.Children.Add(button);
                return button;
            }
            valveBannerPlay = Icon("Play", "Play", () => valveTimer?.IsEnabled == true ? "pause" : "play");
            Icon("StepForward", "Step one tick (1/60 s)", () => "step");
            valveBannerNext = Icon("SkipForward", "Skip to the next timer", () => "next");
            Icon("ToStart", "Restart the test at 0 s", () => "reset");
            Button end = new() { Content = "End test", Height = 28, Padding = new(12, 0, 12, 0), Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(end, "End the valve test");
            end.Click += (_, _) => { EndValveTest(); ViewModel.Status = "Valve test ended."; };
            content.Children.Add(end);
            valveBanner.Content = content;
            PreviewOverlayChrome.Children.Add(valveBanner);
        }
        var shownTest = valveSimulation != null && !valveStarting ? valveView : null;
        bool playing = valveTimer?.IsEnabled == true, ready = shownTest != null;
        int timers = shownTest is { Halted: false } ? shownTest.Timers.Length : 0;
        valveBannerText!.Text = shownTest == null ? "Starting the valve test…"
            : shownTest.Halted ? "Valve test halted · restart it"
            : $"Testing valves · {shownTest.Time.ToString("F3", CultureInfo.InvariantCulture)} s" + (timers > 0 ? $" · {timers} timer{(timers == 1 ? "" : "s")}" : "") + (shownTest.Aborted ? " · mission aborted" : "");
        ((PreviewIcon)valveBannerPlay!.Content).Kind = playing ? "Pause" : "Play";
        valveBannerPlay.ToolTip = playing ? "Pause" : "Play";
        AutomationProperties.SetName(valveBannerPlay, playing ? "Pause" : "Play");
        foreach (var button in ((StackPanel)valveBanner.Content).Children.OfType<Button>()) button.IsEnabled = ready || ReferenceEquals(button, ((StackPanel)valveBanner.Content).Children[^1]);
        valveBannerNext!.IsEnabled = ready && timers > 0;
        valveBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The valve card's Test section: the Test valves button; while testing the valve's value and inputs, the timers waiting
    /// and the test's activity. While the test runs the same section is returned for the same valve: its controls (and a
    /// value being typed) stay, only its readouts are rebuilt.
    /// </summary>
    private FrameworkElement BuildValveTestPanel(Mw3ValveInfo valve)
    {
        if (valveSimulation == null || valveStarting || valveView is not { } view)
        {
            valveTestPanel = null;
            StackPanel idle = new() { Margin = new(0, 0, 0, 2) };
            var start = SourceSectionCard.ValveTextButton("Play", ValveTesting ? "Starting the test…" : "Test valves", "Flip valves in memory and watch timers, routes and vehicles react. Nothing is saved.", ToggleValveTest, accent: true);
            start.IsEnabled = !ValveTesting && !InAnimationMode;
            idle.Children.Add(start);
            if (valveFailure != null)
            {
                TextBlock failed = new() { Text = "The test did not start: " + valveFailure, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
                failed.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
                idle.Children.Add(failed);
            }
            return idle;
        }
        if (valveTestPanel is not { } panel || panel.Valve != valve.Name || panel.Simulation != view.Simulation)
        {
            panel = valveTestPanel = new(valve.Name, view.Simulation);
            Button Action(string text, string tip, string action, int value)
            {
                Button button = new() { Content = text, Height = 30, Padding = new(12, 0, 12, 0), Margin = new(0, 0, 6, 4), ToolTip = tip };
                AutomationProperties.SetName(button, text);
                button.Click += async (_, _) => await RunUi(() => ValvePreviewActionAsync(action, valve.Name, value, null, "", -1, CancellationToken.None));
                panel.Controls.Children.Add(button); panel.Buttons.Add(button);
                return button;
            }
            if (valve.Condition == null)
            {
                panel.Off = Action("Off (0)", "Set it to 0, as a trigger or route would", "set", 0);
                panel.On = Action("On (1)", "Set it to 1, as a trigger or route would", "set", 1);
                TextBox other = new() { Text = "2", Width = 56, Height = 30, MaxLength = 11, Margin = new(0, 0, 4, 4) };
                AutomationProperties.SetName(other, "Other value");
                panel.Controls.Children.Add(other);
                Button set = new() { Content = "Set", Height = 30, Padding = new(10, 0, 10, 0), Margin = new(0, 0, 6, 4), ToolTip = "Set it to this value" };
                AutomationProperties.SetName(set, "Set to the other value");
                set.Click += async (_, _) => await RunUi(async () =>
                {
                    if (!int.TryParse(other.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) throw new StudioCommandException("invalid_argument", "Enter a whole number.");
                    await ValvePreviewActionAsync("set", valve.Name, number, null, "", -1, CancellationToken.None);
                });
                panel.Controls.Children.Add(set); panel.Buttons.Add(set);
            }
            Action("Fire", "Run its reactions without changing its value, as another valve's update does", "update", 0);
            panel.Section.Body.Children.Add(panel.Inputs); panel.Section.Body.Children.Add(panel.Controls); panel.Section.Body.Children.Add(panel.Live);
        }
        int current = view.Values.GetValueOrDefault(valve.Name);
        panel.Section.Summary = view.Halted ? "halted" : "= " + current.ToString(CultureInfo.InvariantCulture);
        foreach (var button in panel.Buttons) button.IsEnabled = !valveBusy && !view.Halted;
        if (panel.Shown != current || panel.Off == null)
        {
            // The value it holds now is the accent one.
            panel.Shown = current;
            if (panel.Off != null) { if (current == 0) panel.Off.SetResourceReference(StyleProperty, "AccentButtonStyle"); else panel.Off.ClearValue(StyleProperty); }
            if (panel.On != null) { if (current == 1) panel.On.SetResourceReference(StyleProperty, "AccentButtonStyle"); else panel.On.ClearValue(StyleProperty); }
        }
        panel.Inputs.Children.Clear(); panel.Live.Children.Clear();
        if (view.Halted)
        {
            panel.Live.Children.Add(TestLine("The test stopped on an error. Restart it from the banner over the view.", secondary: true));
            return panel.Section;
        }
        if (valve.Condition != null)
        {
            // A computed valve cannot be set: its inputs decide.
            panel.Inputs.Children.Add(TestLine($"Computed: {Mw3ValveMap.ConditionText(valve.Condition)}.", secondary: true));
            foreach (string input in valve.Inputs.Take(12))
            {
                DockPanel line = new() { Margin = new(0, 1, 0, 1) };
                Button open = new() { Content = JsonData.ShownText(input, 64), Height = 26, Padding = new(8, 0, 8, 0), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Show this valve" };
                open.SetResourceReference(StyleProperty, "CommandButton"); open.Width = double.NaN;
                open.Click += (_, _) => SelectValve(input);
                TextBlock state = new() { Text = "= " + view.Values.GetValueOrDefault(input).ToString(CultureInfo.InvariantCulture), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
                DockPanel.SetDock(state, Dock.Right); line.Children.Add(state); line.Children.Add(open);
                panel.Inputs.Children.Add(line);
            }
        }
        // Timers waiting: this valve's first, then the others, soonest first.
        var timers = view.Timers.OrderBy(t => t.Target == valve.Name ? 0 : 1).ThenBy(t => t.Due).Take(6).ToArray();
        if (timers.Length > 0)
        {
            panel.Live.Children.Add(TestHeading("Waiting"));
            foreach (var timer in timers)
            {
                DockPanel line = new() { Margin = new(0, 1, 0, 1) };
                float due = timer.Due; double now = view.Time;
                Button skip = new() { Content = "Skip to", Height = 26, Padding = new(8, 0, 8, 0), FontSize = 12, ToolTip = "Advance the test until this timer has run", IsEnabled = !valveBusy };
                skip.Click += async (_, _) => await RunUi(() => ValvePreviewActionAsync("advance", "", 0, Mw3ValveSimulation.TimerTime(valveView?.Time ?? now, due), "", -1, CancellationToken.None));
                DockPanel.SetDock(skip, Dock.Right); line.Children.Add(skip);
                var icon = new PreviewIcon { Kind = "Timer", Width = 13, Height = 13, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                icon.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
                line.Children.Add(new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = timer.Target == valve.Name ? FontWeights.SemiBold : FontWeights.Normal,
                    Text = $"{JsonData.ShownText(timer.Target, 48)} → {timer.Value} in {Math.Max(0, timer.Due - view.Time).ToString("F1", CultureInfo.InvariantCulture)} s" });
                panel.Live.Children.Add(line);
            }
            if (view.Timers.Length > timers.Length) panel.Live.Children.Add(TestLine($"and {view.Timers.Length - timers.Length} more", secondary: true));
        }
        // The test's activity in the order it happened (the latest 14 lines): a valve's new value, then what that did.
        var trace = view.Trace;
        panel.Live.Children.Add(TestHeading(trace.Length == 0 ? "Activity · nothing yet" : "Activity"));
        var map = sceneTree?.Sections?.ValveMap;
        if (trace.Length > 14) panel.Live.Children.Add(TestLine($"{trace.Length - 14:N0} earlier (ai_valve_preview trace lists them)", secondary: true));
        foreach (var entry in trace.Skip(Math.Max(0, trace.Length - 14)))
        {
            bool mine = entry.Valve == valve.Name, set = entry.Kind == "set";
            // What a change did is indented under it.
            TextBlock line = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(set ? 0 : 14, 1, 0, 1), FontWeight = mine && set ? FontWeights.SemiBold : FontWeights.Normal };
            if (set)
            {
                Run time = new(entry.Time.ToString("F3", CultureInfo.InvariantCulture) + " s  ") { FontFamily = new("Cascadia Mono, Consolas"), FontSize = 11 };
                time.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
                line.Inlines.Add(time);
            }
            line.Inlines.Add(new Run(ValveTraceText(entry, map)));
            if (entry.Kind is "unavailable" or "ignored" or "timer quirk") line.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            else if (!mine || !set) line.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            panel.Live.Children.Add(line);
        }
        if (valveAudioProblem != null) panel.Live.Children.Add(TestLine(valveAudioProblem, secondary: true));
        return panel.Section;

        static TextBlock TestLine(string text, bool secondary)
        {
            TextBlock line = new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 1, 0, 3) };
            if (secondary) line.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            return line;
        }
        static TextBlock TestHeading(string text)
        {
            TextBlock heading = new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new(0, 8, 0, 3) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            return heading;
        }
    }
    /// <summary>One line of the test's activity in words.</summary>
    private static string ValveTraceText(Mw3ValveTrace entry, Mw3ValveMap? map)
    {
        string valve = entry.Valve;
        switch (entry.Kind)
        {
            case "set": return $"{valve} → {entry.Value}";
            case "timer": return "timer: " + entry.Detail;
            case "unavailable": return "not done: " + entry.Detail;
            case "ignored": case "timer quirk": return entry.Detail;
        }
        // A reaction: as its card says it ("wakes owens_01").
        var reaction = entry.Record is { } record ? map?.Find(valve)?.Reactions.FirstOrDefault(r => r.Record == record && r.Spec.Kind == entry.Kind) : null;
        return reaction?.Text ?? (entry.Detail.Length > 0 ? entry.Kind + " " + entry.Detail : entry.Kind);
    }
    /// <summary>A Set by row's Fire while testing: the valve takes the value that trigger, threshold or route would give it.</summary>
    private Action? ValveFireFor(Mw3ValveInfo valve, Mw3ValveLink link)
    {
        if (valveSimulation == null || valveStarting || valve.Condition != null) return null;
        // A timer or update of another valve fires by setting that valve; a deploy point names no value.
        if (link.Value is not int value) return null;
        return () => _ = RunUi(() => ValvePreviewActionAsync("set", valve.Name, value, null, "", -1, CancellationToken.None));
    }

    // ---------------------------------------------------------------- an AI node's valves (its card in the view)

    /// <summary>
    /// A selected AI node's Valves section: its gates and assignments in words, each valve a link; Add gate… with a valve
    /// picker; while testing, each outgoing link open or closed with Drive through. A vehicle's card shows its state in the test.
    /// </summary>
    private (object? Source, string? Node, StackPanel? Panel, Border? Test) valveNodePanel;
    private FrameworkElement? ValveInspectionSections(SceneViewport viewport, SceneInspection inspection)
    {
        if (viewport != scene || shownDocument?.Document is not SourceSceneSnapshot { Game: GameVariant.MechWarrior3 }) return null;
        if (inspection.AiNode is { } id && viewport.AiNetworks.Find(id) is { } ai)
        {
            // The same node of the same built map keeps its section (and what is typed in it): only its Test part follows the test.
            if (valveNodePanel.Source == shownDocument.Document && valveNodePanel.Node == id && valveNodePanel.Panel is { } kept)
            { valveNodePanel.Test!.Child = NodeTest(); return kept; }
            var map = sceneTree?.Sections?.ValveMap;
            StackPanel panel = new();
            CardSection section = new("Valves");
            var records = MissionAiValves.ForNode(ai.Network, ai.Node).Take(16).ToArray();
            section.Summary = records.Length == 0 ? "none" : records.Length.ToString(CultureInfo.CurrentCulture);
            foreach (var record in records)
            {
                // A union passes when any of its terms holds: each term has its own switch and its own value (1 when omitted).
                var named = record.Name == "valveunion"
                    ? record.Value.Children.Where(term => term.Children.Count >= 2 && term.Children[1].Kind == ZrdKind.String).Select(term => (Name: term.Children[1].Text,
                        What: Mw3ValveRuntime.Integer(term, 0) == 0 ? $"union term on {JsonData.ShownText(term.Children[1].Text, 48)}, switched off"
                            : $"passes while {JsonData.ShownText(term.Children[1].Text, 48)} is {(term.Children.Count > 2 ? Mw3ValveRuntime.Integer(term, 2) : 1)}"
                                + (record.Value.Children.Count > 1 ? ", or another of its terms holds" : "")))
                    : MissionAiValves.References(record).Select(reference => (reference.Name, What: record.Name switch
                    {
                        "valve_assign" => $"sets {JsonData.ShownText(reference.Name, 48)} to {Mw3ValveRuntime.Integer(record.Value, 1)} when a vehicle drives through",
                        _ => Mw3ValveRuntime.Integer(record.Value, 0) == 0 ? $"gate on {JsonData.ShownText(reference.Name, 48)}, switched off"
                            : record.Value.Children.Count > 2 ? $"passes while {JsonData.ShownText(reference.Name, 48)} is {Mw3ValveRuntime.Integer(record.Value, 2)}"
                            : $"last link passes while {JsonData.ShownText(reference.Name, 48)} is 1, the others while it is 0",
                    }));
                foreach (var (valveName, what) in named.Take(8))
                {
                    DockPanel line = new() { Margin = new(0, 2, 0, 2) };
                    Button open = new() { Content = new PreviewIcon { Kind = "GoTo", Width = 13, Height = 13 }, Width = 28, Height = 28, Padding = new(0), ToolTip = "Show valve " + JsonData.ShownText(valveName, 64) };
                    open.SetResourceReference(StyleProperty, "CommandButton");
                    AutomationProperties.SetName(open, "Show valve " + JsonData.ShownText(valveName, 64));
                    open.Click += (_, _) => { if (ResolveInspectionDrafts()) SelectValve(valveName); };
                    DockPanel.SetDock(open, Dock.Right); line.Children.Add(open);
                    line.Children.Add(new TextBlock { Text = (record.Kind == "edge" ? $"Link {record.From} → {string.Join(", ", record.TargetNodes.Take(4))}: " : "") + what, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                    section.Body.Children.Add(line);
                }
            }
            if (records.Length == 0)
            {
                TextBlock none = new() { Text = "No valve gates this node or is set here.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 4) };
                none.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                section.Body.Children.Add(none);
            }
            // Add a gate or an assignment: a valve and a value, never zReader text.
            if (ai.Node.Source != null)
            {
                ComboBox kind = new() { Height = 30, Margin = new(0, 6, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
                kind.Items.Add(new ComboBoxItem { Content = "Gate: pass only while a valve holds a value", Tag = "valve" });
                kind.Items.Add(new ComboBoxItem { Content = "Set a valve when a vehicle drives through", Tag = "valve_assign" });
                kind.SelectedIndex = 0;
                AutomationProperties.SetName(kind, "What to add");
                ComboBox name = new() { IsEditable = true, IsTextSearchEnabled = false, Height = 30, MaxDropDownHeight = 320, HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = map?.Valves.Select(v => v.Name).ToArray() ?? [] };
                AutomationProperties.SetName(name, "Valve");
                TextBox value = new() { Text = "1", Width = 56, Height = 30, MaxLength = 11, Margin = new(6, 0, 6, 0) };
                AutomationProperties.SetName(value, "Value");
                Button add = new() { Content = "Add", Height = 30, Padding = new(12, 0, 12, 0) };
                add.Click += async (_, _) => await RunUi(async () =>
                {
                    if (shownDocument == null) return;
                    string attribute = (string)((ComboBoxItem)kind.SelectedItem).Tag, valve = name.Text.Trim();
                    if (valve.Length == 0 || valve.Length > 256 || valve.Contains('"')) throw new StudioCommandException("invalid_argument", "Choose or type the valve's name.");
                    if (!int.TryParse(value.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) throw new StudioCommandException("invalid_argument", "Enter the value as a whole number.");
                    string quoted = System.Text.Json.JsonSerializer.Serialize(valve);
                    await AttachValveAsync(shownDocument, ai.Network.Id, ai.Node.Index, attribute, attribute == "valve" ? $"(1 {quoted} {number})" : $"({quoted} {number})", CancellationToken.None);
                });
                DockPanel row = new();
                DockPanel.SetDock(add, Dock.Right); row.Children.Add(add);
                DockPanel.SetDock(value, Dock.Right); row.Children.Add(value);
                row.Children.Add(name);
                section.Body.Children.Add(kind); section.Body.Children.Add(row);
            }
            panel.Children.Add(section);
            Border testHost = new() { Child = NodeTest() };
            panel.Children.Add(testHost);
            valveNodePanel = (shownDocument.Document, id, panel, testHost);
            return panel;
            // While testing: each outgoing link open or closed, and Drive through.
            FrameworkElement? NodeTest()
            {
                if (valveSimulation == null || valveStarting || valveViewport != viewport || valveView is not { Halted: false } view) return null;
                CardSection test = new("Test: drive through");
                foreach (var link in ai.Node.PreviewLinks.Where(l => l.Target != null))
                {
                    // As the last finished step left it (a link beyond the view's drawn links has no state).
                    if (!view.Routes.TryGetValue(id + "|" + link.Slot, out var route)) continue;
                    DockPanel line = new() { Margin = new(0, 2, 0, 2) };
                    int slot = link.Slot;
                    Button drive = new() { Content = "Drive through", Height = 28, Padding = new(10, 0, 10, 0), FontSize = 12, IsEnabled = route.Passable && !valveBusy,
                        ToolTip = route.Passable ? (route.Assignments.Count > 0 ? "Sets " + string.Join(", ", route.Assignments.Take(4).Select(a => $"{JsonData.ShownText(a.Valve, 32)} to {a.Value}")) : "Open: nothing is set on the way")
                            : string.Join(" ", route.Reasons.Take(4)) };
                    ToolTipService.SetShowOnDisabled(drive, true);
                    drive.Click += async (_, _) => await RunUi(() => ValvePreviewActionAsync("traverse", "", 0, null, id, slot, CancellationToken.None));
                    DockPanel.SetDock(drive, Dock.Right); line.Children.Add(drive);
                    TextBlock text = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    text.Inlines.Add(new Run($"→ node {link.TargetIndex}  "));
                    Run open = new(route.Passable ? "open" : "closed · " + string.Join(" ", route.Reasons.Take(2)));
                    open.SetResourceReference(TextElement.ForegroundProperty, route.Passable ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");
                    text.Inlines.Add(open);
                    line.Children.Add(text);
                    test.Body.Children.Add(line);
                }
                return test.Body.Children.Count > 0 ? test : null;
            }
        }
        if (valveSimulation != null && valveView is { } shown && viewport.Mission?.InspectedActorAt(inspection.SelectionNode) is { } actor &&
            shown.Actors.TryGetValue(actor.Name, out var state) && (state.PoweredDown != null || state.Destroyed || state.Team != null || state.Strategy != null || state.Position != null || state.Switches.Count > 0))
        {
            CardSection section = new("Valve test");
            List<string> lines = [];
            if (state.Destroyed) lines.Add("Destroyed");
            if (state.PoweredDown is bool down) lines.Add(down ? "Powered down" : "Awake");
            if (state.Team is int team) lines.Add($"Team {team}");
            if (state.Strategy is int strategy) lines.Add($"Attack strategy {strategy}");
            if (state.Position != null) lines.Add("Teleported");
            foreach (var (name, on) in state.Switches) lines.Add($"{name} {(on ? "on" : "off")}");
            section.Summary = lines[0];
            section.Body.Children.Add(new TextBlock { Text = string.Join(" · ", lines), FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            StackPanel panel = new(); panel.Children.Add(section);
            return panel;
        }
        return null;
    }
}
