using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// A compiled animation's isolated preview (design/animation-redesign.md §19): the player of one entry under its compiled
/// scenario (<see cref="Scenario"/>: seed, phase, height, player, beam origin, effects detail, condition switches and call
/// exclusions over every call), the scene it plays in, its sound and its labelled view commands. The window shows it with
/// Animation mode's components (transport, Structure, Timing, Inspector, Scenario), which read <see cref="CurrentFrame"/>
/// and raise their intents here. Nothing here edits stored data.
/// </summary>
public partial class AnimationEditor : FieldEditor, IDisposable
{
    private readonly DocumentModel document;
    private readonly int entryIndex;
    private readonly AssetResolver resolver;
    private readonly AnimationPackage package;
    private readonly MainViewModel? preferences;
    private MissionDifficulty SelectedDifficulty => Difficulty.SelectedItem is MissionDifficulty value ? value : MissionDifficulty.Medium;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenSource? seeking;
    private CancellationTokenSource? initializing;
    private readonly SemaphoreSlim simulationGate = new(1);
    private bool resetSimulation;
    private bool contextDirty;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
    private readonly Stopwatch clock = new();
    private readonly SceneViewport viewport = new();
    private readonly AnimationAudio audio;
    private readonly List<string> audioDiagnostics = [];
    private bool audioDirty = true;
    private int audioRevision, audioPreparing;
    internal AnimationAudio Audio => audio;
    internal event Action<double, double>? AudioUpdated;
    private AnimationPreviewContext? context;
    private AnimationPlayer? player;
    private AnimationFrame? frame;
    private string? renderFailure;
    private double lastClock, playbackTarget;
    private bool ready, changing, playing;
    private bool pendingPlay, customRange, extendedRange;
    private AnimationDuration? duration;
    /// <summary>The Auto measurement running in the background (with its cancellation and identity), if any.</summary>
    private Task<AnimationDuration?>? measuring;
    private CancellationTokenSource? measureCancellation;
    private long measureId;
    private int contextGeneration;
    private SceneViewport.ViewPose? contextRefreshView;
    private long settingsInputRevision;
    private AnimationScenario scenario;
    // The range until Auto's first measurement arrives (playback never waits for it).
    private double rangeEnd = 30, speed = 1, volume = .5;
    private bool replay, muted;
    private string status = "";
    public AnimationPreviewOptions Options { get; }
    public SceneViewport Viewport => viewport;
    public AnimationFrame? CurrentFrame => frame;
    public bool IsPlaying => playing;
    public int EntryIndex => entryIndex;
    public AnimationDuration? Duration => duration;
    /// <summary>The preview's scene (the bound mission and its root), once loaded.</summary>
    internal AnimationPreviewContext? PreviewContext => context;
    /// <summary>The preview's compiled scenario: its one start, inputs, rules, switches and call exclusions.</summary>
    internal AnimationScenario Scenario => scenario;
    /// <summary>The playback range's end (the range starts at 0 s), automatic unless <see cref="CustomRange"/>.</summary>
    internal double RangeEnd => rangeEnd;
    internal bool CustomRange => customRange;
    /// <summary>Extend +5 s lengthened the Custom range (playback stopping at its end hints Extend again).</summary>
    internal bool ExtendedRange => extendedRange;
    /// <summary>Auto's measurement runs in the background.</summary>
    internal bool Measuring => measuring != null;
    /// <summary>The Auto measurement running, if any (a check or a test can wait for it).</summary>
    internal Task<AnimationDuration?>? MeasuringTask => measuring;
    /// <summary>Auto is waiting for its measurement (as the preview loads, after a scenario or scene change): the range shows no end yet.</summary>
    internal bool RangePending => !customRange && duration == null && measuring != null;
    /// <summary>Extend +5 s can lengthen the range: a preview that seeks, an end below 3,600 s, and no Auto end still measuring.</summary>
    internal bool CanExtend => CanSeek && !RangePending && rangeEnd < AnimationSeconds.Maximum - 1e-9;
    /// <summary>Playback stopped at the range's end while the run goes on, at an end the 5 s rule set (Auto) or Extend lengthened: the transport's hint.</summary>
    internal string? RangeHint => !playing && !seekRunning && !Loading && player is { IsComplete: false, IsDependencyBlocked: false } && frame != null && frame.Time >= rangeEnd - 1e-7 ? AnimationRangeText.Hint(!customRange, extendedRange, duration) : null;
    internal double Speed => speed;
    internal bool Replay => replay;
    internal bool Muted => muted;
    internal double Volume => volume;
    internal bool Loading => LoadingPanel.Visibility == Visibility.Visible;
    internal string LoadingMessage => LoadingText.Text;
    /// <summary>Why the preview could not be prepared or shown (the scene, the world, a render limit), or null.</summary>
    internal string? PreviewFailure => renderFailure ?? preparationFailure ?? previewOperationFailure;
    /// <summary>Why the last preparation (a world, a scene) failed: it stays until a preparation succeeds; an option, scenario or transport change never clears it.</summary>
    private string? preparationFailure;
    internal bool CanSeek => player != null && !Loading && !seekRunning || renderFailure != null && context != null;
    /// <summary>A seek (or a rebuild) is evaluating: Play waits for it (a play asked meanwhile starts after it).</summary>
    private bool seekRunning;
    internal string StatusText => status;
    /// <summary>The run, the transport or the loading state changed: the window's panel, graph, Timing and Inspector follow.</summary>
    internal event Action? StateChanged;
    /// <summary>The scenario changed here (the Height box): the window keeps it for the document's other animations.</summary>
    internal event Action<AnimationScenario>? ScenarioChanged;
    public event Action<string>? StatusChanged;
    private AnimationEntry Entry => package.Entries[entryIndex];

    public AnimationEditor(DocumentModel document, int entryIndex, AssetResolver resolver, CancellationToken token, MainViewModel? preferences = null, AnimationPreviewOptions? options = null)
        : this(document, entryIndex, resolver, token, preferences, new AnimationAudio(), options) { }
    internal AnimationEditor(DocumentModel document, int entryIndex, AssetResolver resolver, CancellationToken token, MainViewModel? preferences, AnimationAudio audio, AnimationPreviewOptions? options = null)
    {
        this.audio = audio;
        this.preferences = preferences;
        Options = options ?? new();
        this.document = document; this.entryIndex = entryIndex; this.resolver = resolver; package = document.Document.Animations!;
        scenario = AnimationScenario.Isolated(entryIndex);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, document.Lifetime.Token);
        InitializeComponent(); ViewportHost.Content = viewport;
        Difficulty.ItemsSource = MainViewModel.DifficultyChoices; Difficulty.SelectedItem = preferences?.Difficulty ?? MissionDifficulty.Medium;
        if (preferences != null) preferences.PropertyChanged += PreferencesChanged;
        viewport.Information += Note;
        viewport.PreviewRestored += () => Note("3D preview restored.");
        viewport.ManualNavigationStarting += () => { if (FollowCamera.IsChecked == true) FollowCamera.IsChecked = false; };
        audio.Diagnostic += message =>
        {
            if (disposed) return;
            muted = audio.Muted;
            if (!audioDiagnostics.Contains(message)) audioDiagnostics.Add(message);
            Note(message);
        };
        viewport.NodeSelected += index => Note($"Scene node #{index}: {context?.Scene.Nodes[index].Name}");
        timer.Tick += Tick;
        PreviewHeight.TextChanged += (_, _) => { if (!changing) ++settingsInputRevision; };
        ready = true; AudioChanged();
    }

    /// <summary>
    /// The kept state of the document's compiled previews, before the first frame: the scenario's inputs, rules and switches
    /// (its start becomes this entry) and the transport's speed, loop and sound; the range is each animation's own.
    /// </summary>
    internal void Adopt(AnimationScenario kept, double keptSpeed, bool keptReplay, bool keptMuted, double keptVolume)
    {
        scenario = (entryIndex > 0 ? kept.WithPrimary(entryIndex, keepPrevious: false) : kept with { Starts = [] }) with { IsolatedPreview = true };
        speed = keptSpeed; replay = keptReplay; muted = keptMuted; volume = keptVolume;
        changing = true;
        try { PreviewHeight.Text = scenario.Height.ToString(CultureInfo.InvariantCulture); }
        finally { changing = false; }
        AudioChanged();
    }

    public async Task InitializeAsync(string? worldPath = null, bool recoverCanceledRequest = false)
    {
        var operationToken = PreviewOperation.Current;
        refreshingLevel = false; levelResume = false; levelRefreshView = null; levelRefreshSelection = null;
        contextRefreshView = null;
        initializing?.Cancel(); initializing?.Dispose(); initializing = PreviewOperation.Link(lifetime.Token);
        var token = initializing.Token;
        int generation = ++contextGeneration; seeking?.Cancel(); SetPlaying(false); LoadingPanel.Visibility = Visibility.Visible;
        context = null; player = null; audioDirty = true; ++audioRevision;
        // A new preparation replaces the last one's failure (it is reported again if this one fails too).
        LoadingText.Text = "Loading animation…"; renderFailure = null; preparationFailure = null; previewOperationFailure = null; Changed();
        try
        {
            token.ThrowIfCancellationRequested();
            var loaded = await document.GetAnimationContextAsync(resolver, token, worldPath, SelectedDifficulty);
            if (generation != contextGeneration || disposed) return; context = loaded;
            await ConfigureMissionsAsync(token);
            changing = true; Lod.ItemsSource = SceneLods.Choices(context.Lods.Count()); Lod.SelectedIndex = 0; changing = false;
            token.ThrowIfCancellationRequested(); player = CreatePlayer(token: token); frame = player.Frame(token);
            // The Auto range is measured with the preparation, in the background: the preview shows and plays meanwhile.
            duration = null; _ = MeasureInBackground(context);
            if (generation != contextGeneration || disposed) return;
            // GUI presentation choices may change while initial meshes upload.
            // Their handlers defer to initialization; publish only a matching scene.
            while (true)
            {
                bool includeLevel = ShowLevel.IsChecked == true, horizon = ShowHorizon.IsChecked == true;
                int lod = player.LodLevel; frame = player.Frame(token);
                await viewport.ShowAnimationAsync(context, frame, resolver, includeLevel, token, lod, horizon, lifetime.Token);
                LoadingText.Text = "Preparing audio…";
                await PrepareAudioAsync(token);
                token.ThrowIfCancellationRequested();
                if (includeLevel == (ShowLevel.IsChecked == true) && horizon == (ShowHorizon.IsChecked == true) && lod == player.LodLevel) break;
            }
            renderFailure = null;
            LoadingPanel.Visibility = Visibility.Collapsed; if (!Render()) return;
            if (resetSimulation) await SeekAsync(0);
            Note($"Bound to {context.World.Path} · root #{context.ResolveRoot(Entry)} ({Entry.RootName})");
            StartPendingPlayback();
        }
        catch (OperationCanceledException)
        {
            // Explicit MCP world rebinding keeps this editor alive. Recover its
            // current request without giving the canceled agent token ownership
            // of the retained preview. A newer bind/selection owns its own recovery.
            if (recoverCanceledRequest && operationToken.IsCancellationRequested && generation == contextGeneration && !disposed && !lifetime.IsCancellationRequested)
            {
                using var recovery = PreviewOperation.Begin(CancellationToken.None);
                await InitializeAsync(worldPath);
            }
            if (recoverCanceledRequest) throw;
        }
        catch (SceneViewport.RenderLimitException ex) { if (generation == contextGeneration && !disposed) RenderUnavailable(ex); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (generation == contextGeneration && !disposed)
            {
                preparationFailure = ex.Message; pendingPlay = false;
                // The failure says what still works: the stored data stays readable (design §19, §6.8 #9).
                LoadingText.Text = "Couldn't prepare the preview: " + ex.Message + "\nStructure and the Inspector still show the stored data. Scenario → World → Choose GameZ… picks the mission scene.";
                RecordPreviewError(ex.Message); Changed();
            }
        }
    }
    private AnimationPlayer CreatePlayer(AnimationPreviewContext? source = null, CancellationToken token = default) => CapturePlayer(source ?? context!, token)();
    private Func<AnimationPlayer> CapturePlayer(AnimationPreviewContext source, CancellationToken token)
    {
        // Only capture controls on the dispatcher; cold binding can then run in the owning request's worker.
        int selectedEntry = entryIndex;
        var shown = scenario;
        bool ground = GroundCollision.IsChecked == true;
        int lod = Math.Clamp(player?.LodLevel ?? Math.Max(0, Lod.SelectedIndex), 0, Math.Max(0, source.Lods.Count() - 1));
        // The isolated preview runs every call its scenario does not exclude, under its rules and condition switches; its
        // custom player is what range tests measure from (and beams aim at).
        var policy = shown.ToExecutionPolicy();
        return () => new(source, selectedEntry, shown.Seed, shown.Phase == AnimationScenarioPhase.Cleanup, token)
        { ExecutionPolicy = policy, ReferencePosition = shown.CustomPlayer, ActivationStart = shown.BeamOrigin, EffectLevel = (int)shown.EffectsDetail,
            LodLevel = lod, GroundPlaneEnabled = ground, ProceduralLimitsEnabled = true, PreviewHeight = shown.Height };
    }
    /// <summary>
    /// What a what-if of this preview's run belongs to (design §15): its frozen context and player settings (LOD, ground
    /// collision); null before the preview is prepared.
    /// </summary>
    internal object? WhatIfRun => context == null ? null
        : (context, Math.Clamp(player?.LodLevel ?? Math.Max(0, Lod.SelectedIndex), 0, Math.Max(0, context.Lods.Count() - 1)), GroundCollision.IsChecked == true);
    /// <summary>Runs one what-if of this preview's scenario to <paramref name="end"/>, off the UI thread, on a snapshot taken now.</summary>
    internal Func<string, CancellationToken, AnimationWhatIfResult> WhatIfRunner(double end)
    {
        var create = CapturePlayer(context!.Snapshot(), lifetime.Token);
        AnimationPlayer? template = null;
        AnimationWhatIfBaseline? baseline = null;
        return (path, token) =>
        {
            template ??= create();
            return template.RunWhatIf(path, baseline ??= template.RunBaseline(end, token), end, token);
        };
    }
    private void SetPlaying(bool value)
    {
        bool changed = playing != value;
        playing = value;
        if (value) { clock.Restart(); lastClock = 0; playbackTarget = player?.Time ?? 0; timer.Start(); } else { timer.Stop(); audio.Stop(); }
        // Only a change is news (a paused seek stops nothing): every listener redraws the panel, the graph and the Inspector.
        if (changed) Changed();
    }
    private void Changed() { if (!disposed) StateChanged?.Invoke(); }
    private void Tick(object? sender, EventArgs e)
    {
        if (player == null || context == null || lifetime.IsCancellationRequested) { SetPlaying(false); return; }
        try
        {
            double now = clock.Elapsed.TotalSeconds, delta = Math.Min(.1, now - lastClock) * speed; lastClock = now;
            double time = playbackTarget + delta;
            if (time > rangeEnd)
            {
                if (replay) { player.Reset(); time = 0; audio.Stop(); }
                // At an end the 5 s rule set, the transport hints Extend +5 s (RangeHint).
                else { SetPlaying(false); time = Math.Min(time, rangeEnd); }
            }
            playbackTarget = time; frame = player.AdvanceTo(time, token: lifetime.Token); if (!Render()) return;
            long audioStart = Stopwatch.GetTimestamp();
            audio.Update(frame, playing);
            AudioUpdated?.Invoke(now, Stopwatch.GetElapsedTime(audioStart).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { SetPlaying(false); RecordPreviewError(ex.Message); }
    }
    private long seekGeneration, publishedSeekGeneration;
    private CancellationToken seekOperationToken;
    public async Task SeekAsync(double seconds, bool preservePlayhead = false)
    {
        if (context == null || disposed || LoadingPanel.Visibility == Visibility.Visible) return;
        long generation = ++seekGeneration;
        var operationToken = seekOperationToken = PreviewOperation.Current;
        SetPlaying(false); seeking?.Cancel(); seeking?.Dispose(); seeking = PreviewOperation.Link(lifetime.Token);
        var token = seeking.Token; seekRunning = true; Changed();
        try
        {
            await simulationGate.WaitAsync(token);
            try
            {
                bool refreshScene = contextDirty, rebuildViewport = refreshScene || renderFailure != null;
                var nextContext = context;
                var view = refreshScene ? contextRefreshView ??= viewport.CaptureView() : viewport.CaptureView();
                if (refreshScene)
                {
                    var updated = await document.GetAnimationContextAsync(resolver, token, difficulty: SelectedDifficulty, exactMission: Mw3WorldPath is { } world ? ExactMission?.Invoke(world) : null);
                    token.ThrowIfCancellationRequested();
                    updated.RemapBindingsFrom(context); nextContext = updated;
                }
                // A new scenario or scene measures its Auto range again, in the background, once this seek shows it.
                bool remeasure = resetSimulation;
                if (audioDirty) await PrepareAudioAsync(token, nextContext);
                token.ThrowIfCancellationRequested();
                double range = rangeEnd;
                if (preservePlayhead && !customRange && seconds > range)
                    range = Math.Min(AnimationSeconds.Maximum, seconds + (pendingPlay ? 5 : AnimationPlayer.StepSeconds));
                double target = Math.Clamp(seconds, 0, range);
                var nextPlayer = resetSimulation || player == null ? CreatePlayer(nextContext, token) : player;
                var nextFrame = await Task.Run(() => nextPlayer.AdvanceTo(target, true, token), token);
                token.ThrowIfCancellationRequested();
                if (rebuildViewport)
                {
                    try { await viewport.ShowAnimationAsync(nextContext, nextFrame, resolver, ShowLevel.IsChecked == true, token, nextPlayer.LodLevel, ShowHorizon.IsChecked == true, lifetime.Token); }
                    catch (Exception ex) when (ex is not SceneViewport.RenderLimitException and not OperationCanceledException and not OutOfMemoryException and not StackOverflowException && !token.IsCancellationRequested)
                    {
                        // Resource failures after a render replacement must also keep the last valid scene.
                        if (frame != null)
                        {
                            await viewport.ShowAnimationAsync(context, frame, resolver, ShowLevel.IsChecked == true, token, player?.LodLevel ?? 0, ShowHorizon.IsChecked == true, lifetime.Token);
                            viewport.RestoreView(view); Render();
                        }
                        throw;
                    }
                    token.ThrowIfCancellationRequested(); viewport.RestoreView(view);
                    changing = true; int lod = Lod.SelectedIndex; Lod.ItemsSource = SceneLods.Choices(nextContext.Lods.Count()); Lod.SelectedIndex = Math.Clamp(lod, 0, Lod.Items.Count - 1); changing = false;
                }
                context = nextContext; SynchronizeMissionSelection(); player = nextPlayer; frame = nextFrame;
                if (rebuildViewport) renderFailure = null;
                contextDirty = false; contextRefreshView = null; resetSimulation = false; ApplyRange(range);
                if (remeasure) { duration = null; _ = MeasureInBackground(nextContext); }
                if (!Render()) return;
                previewOperationFailure = null;
                publishedSeekGeneration = generation;
            }
            finally { simulationGate.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (operationToken.IsCancellationRequested && !disposed && !lifetime.IsCancellationRequested && seeking?.Token == token)
            {
                // The request can be revoked after scene meshes or player state
                // have begun rebuilding. Complete the still-current GUI request
                // with its own lifetime, preserving the requested playhead/range.
                using var recovery = PreviewOperation.Begin(CancellationToken.None);
                await SeekAsync(seconds, preservePlayhead);
            }
        }
        catch (SceneViewport.RenderLimitException ex) { RenderUnavailable(ex); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { previewOperationFailure = ex.Message; contextRefreshView = null; Note($"Preview was not updated; retaining {context?.Mission?.Layout.Label}. {ex.Message}"); }
        finally
        {
            if (!disposed && seeking?.Token == token)
            {
                seekRunning = false;
                if (!token.IsCancellationRequested) StartPendingPlayback();
                Changed();
            }
        }
    }
    private void PresentationChanged(object sender, RoutedEventArgs e)
    {
        if (sender == FollowCamera)
        {
            if (FollowCamera.IsChecked == true) viewport.PrepareAuthoredCamera();
            else viewport.StopCameraMotion();
        }
        if (ready) { Render(); Changed(); }
    }
    internal void ToggleCameraFollow() => FollowCamera.IsChecked = FollowCamera.IsChecked != true;
    private void GridChanged(object sender, RoutedEventArgs e) { if (ready) viewport.SetGroundGrid(ShowGrid.IsChecked == true); }
    private void FaceOrientationChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        viewport.SetFaceOrientation(ShowFaceOrientation.IsChecked == true);
        Render();
    }
    private void HeightKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) { HeightEditingFinished(); e.Handled = true; }
    }
    private void HeightLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Overflow reparenting, window deactivation and workspace layout are not input commits.
        if (Window.GetWindow(this) is MainWindow { IsChangingLayout: true } || e.NewFocus is not DependencyObject target ||
            Window.GetWindow(target) != Window.GetWindow(this) ||
            PropertyContext.FindAncestor<System.Windows.Controls.Primitives.Thumb>(target) != null ||
            PropertyContext.FindAncestor<System.Windows.Controls.Primitives.MenuBase>(target) != null) return;
        HeightEditingFinished();
    }
    private void HeightEditingFinished()
    {
        if (!ready || disposed) return;
        changing = true;
        try { PreviewHeight.Text = scenario.Height.ToString(CultureInfo.InvariantCulture); }
        finally { changing = false; }
    }
    private async void HeightChanged(object sender, TextChangedEventArgs e) => await (optionWork = HeightChangedAsync());
    private async Task HeightChangedAsync()
    {
        if (!ready || changing || disposed) return;
        // Empty and incomplete text is normal while replacing a number. Keep the
        // most recent valid height without interrupting typing or moving the caret.
        if (!float.TryParse(PreviewHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float height) || !float.IsFinite(height) || height is < -999 or > 999)
            return;
        if (height == scenario.Height) return;
        scenario = scenario with { Height = height }; ScenarioChanged?.Invoke(scenario);
        resetSimulation = true; pendingPlay |= playing;
        await SeekAsync(frame?.Time ?? 0, preservePlayhead: true);
    }
    private async void GroundChanged(object sender, RoutedEventArgs e) => await (optionWork = GroundChangedAsync());
    private async Task GroundChangedAsync()
    {
        if (!ready || changing || disposed) return;
        viewport.SetGroundGrid(ShowGrid.IsChecked == true);
        resetSimulation = true;
        pendingPlay |= playing;
        await SeekAsync(frame?.Time ?? 0, preservePlayhead: true);
    }
    private void RenderUnavailable(SceneViewport.RenderLimitException error)
    {
        Pause(); renderFailure = previewOperationFailure = error.Message;
        player = null; frame = null; playbackTarget = 0; resetSimulation = true;
        viewport.Clear();
        ScreenOverlay.Background = Brushes.Transparent; WaveOverlay.Children.Clear();
        LoadingPanel.Visibility = Visibility.Collapsed;
        RecordPreviewError(error.Message); Changed();
    }
    private bool Render()
    {
        if (renderFailure != null) { previewOperationFailure = renderFailure; return false; }
        if (frame == null || disposed) return false;
        // Initialization measures duration before installing its viewport context.
        // Do not submit that candidate frame to the previous scene's renderer.
        if (LoadingPanel.Visibility == Visibility.Visible && !refreshingLevel) return true;
        try { viewport.UpdateAnimationFrame(frame, FollowCamera.IsChecked == true, Lighting.IsChecked == true); }
        catch (SceneViewport.RenderLimitException ex) { RenderUnavailable(ex); return false; }
        if (publishedHierarchy != context?.Scene) { publishedHierarchy = context?.Scene; SceneHierarchyChanged?.Invoke(); }
        viewport.SetGroundGrid(ShowGrid.IsChecked == true);
        var c = frame.ScreenColor;
        ScreenOverlay.Background = Lighting.IsChecked == true && !viewport.FaceOrientationEnabled ? new SolidColorBrush(Color.FromScRgb(Math.Clamp(c.W, 0, 1), Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1))) : Brushes.Transparent;
        WaveOverlay.Children.Clear();
        if (Lighting.IsChecked == true && !viewport.FaceOrientationEnabled && frame.ScreenWave.Z > 0)
        {
            var wave = frame.ScreenWave; double radius = Math.Clamp(wave.Z, 0, 10000);
            Ellipse circle = new() { Width = radius * 2, Height = radius * 2, Stroke = Brushes.LightSkyBlue, StrokeThickness = Math.Clamp(Math.Abs(wave.W), 1, 12), Opacity = .55 };
            Canvas.SetLeft(circle, wave.X - radius); Canvas.SetTop(circle, wave.Y - radius); WaveOverlay.Children.Add(circle);
        }
        // Follow camera names the camera it follows (at most ten times a second while playing).
        if (frame.Time == 0 || Math.Round(frame.Time * 60) % 6 == 0 || !playing)
            FollowCamera.ToolTip = "Follow camera — the view follows the animation's camera: " + CameraLabel + "; navigating turns it off (Numpad 0)";
        Changed();
        return true;
    }
    /// <summary>The animation's camera at the playhead ("camera1 #2 · 60° horizontal FOV"), as Follow camera and preview_state name it.</summary>
    internal string CameraLabel => frame?.Camera is { } camera ? $"{camera.Name} #{camera.SourceNode} · {camera.FieldOfView:0.#}° horizontal FOV" : "no animated camera pose at this time";
    /// <summary>The bound root as the scene resolves it ("rlbridge · node #3313"), or why it is unresolved.</summary>
    internal string BoundRootLabel()
    {
        int root = context?.ResolveRoot(Entry) ?? -1;
        return root >= 0 && root < context!.Scene.Nodes.Count ? $"{context.Scene.Nodes[root].Name} · node #{root}" : $"unresolved ({Entry.RootName})";
    }
    internal bool RootOverridden => context?.RootOverrides.ContainsKey(entryIndex) == true;
    internal async Task RefreshModelContextAsync(bool resourceChanges = false)
    {
        if (disposed) return;
        pendingPlay |= playing;
        if (resourceChanges) { audioDirty = true; ++audioRevision; }
        contextDirty = true; resetSimulation = true;
        await SeekAsync(frame?.Time ?? 0, preservePlayhead: true);
    }
    public void TogglePlayback()
    {
        if (disposed || !ResolvePendingDrafts()) return;
        if (renderFailure != null) { previewOperationFailure = renderFailure; Note("Preview unavailable; seek to rebuild a supported frame. " + renderFailure); return; }
        if (player == null || LoadingPanel.Visibility == Visibility.Visible || seekRunning) { pendingPlay = !pendingPlay; Changed(); return; }
        if (!playing && player.Time >= rangeEnd)
        {
            try
            {
                var reset = CreatePlayer(token: lifetime.Token);
                var resetFrame = reset.Frame(lifetime.Token);
                player = reset; frame = resetFrame; if (!Render()) return;
            }
            catch (OperationCanceledException) { return; }
            catch (IOException ex) { SetPlaying(false); previewOperationFailure = ex.Message; RecordPreviewError(ex.Message); return; }
        }
        seeking?.Cancel(); SetPlaying(!playing);
    }
    private void StartPendingPlayback() { if (pendingPlay && !disposed && LoadingPanel.Visibility != Visibility.Visible && !seekRunning) { pendingPlay = false; TogglePlayback(); } }
    /// <summary>The transport's options: speed (0.25–2), replay (loop the range), mute and volume.</summary>
    internal void SetTransport(double? newSpeed = null, bool? newReplay = null, bool? newMuted = null, double? newVolume = null)
    {
        if (newSpeed is double s) speed = s;
        if (newReplay is bool r) replay = r;
        if (newMuted is bool m) muted = m;
        if (newVolume is double v) volume = Math.Clamp(v, 0, 1);
        AudioChanged(); Changed();
    }
    /// <summary>The playback range: a Custom end (seconds), or Auto (the measured run, measured again when needed) when <paramref name="end"/> is null.</summary>
    internal async Task SetRangeAsync(double? end)
    {
        if (end is double custom)
        {
            if (!double.IsFinite(custom) || custom < AnimationPlayer.StepSeconds || custom > AnimationSeconds.Maximum)
                throw new ArgumentOutOfRangeException(nameof(end), "The range ends between 1/60 second and 3,600 seconds.");
            customRange = true; extendedRange = false; ApplyRange(Math.Ceiling(custom * 60) / 60);
        }
        else
        {
            customRange = false; extendedRange = false;
            if (duration is { TimedOut: false } known && measuring == null) ApplyRange(known.Seconds);
            else if (context != null) await (measuring ?? MeasureInBackground(context));
        }
        await SeekAsync(frame?.Time ?? 0);
    }
    /// <summary>Extend +5 s: the range's end moves 5 s later and the range is Custom; playback goes on.</summary>
    internal void ExtendRange()
    {
        if (rangeEnd >= AnimationSeconds.Maximum - 1e-9) throw new ArgumentOutOfRangeException(nameof(rangeEnd), "The range already ends at 3,600 s, the preview's limit.");
        customRange = true; extendedRange = true;
        ApplyRange(Math.Ceiling(Math.Min(AnimationSeconds.Maximum, rangeEnd + AnimationRangeText.ExtendSeconds) * 60 - 1e-7) / 60);
    }
    /// <summary>
    /// A scenario change (the Scenario tab, a condition switch, an include port, MCP animation_scenario): playback stops and
    /// the run is evaluated again up to the playhead.
    /// </summary>
    internal async Task SetScenarioAsync(AnimationScenario next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (disposed) return;
        // The same scenario again rebuilds only when an earlier rebuild did not finish (a failure keeps it pending).
        if (next.Equals(scenario) && !resetSimulation) return;
        // The sounds prepared are those of the calls that run: another call set prepares them again.
        if (next.AllCalls != scenario.AllCalls || !next.ExcludedCalls.SequenceEqual(scenario.ExcludedCalls) || !next.IncludedCalls.SequenceEqual(scenario.IncludedCalls)) { audioDirty = true; ++audioRevision; }
        scenario = next with { IsolatedPreview = true };
        changing = true;
        try { if (!PreviewHeight.IsKeyboardFocusWithin) PreviewHeight.Text = scenario.Height.ToString(CultureInfo.InvariantCulture); }
        finally { changing = false; }
        ScenarioChanged?.Invoke(scenario);
        pendingPlay = false; SetPlaying(false);
        resetSimulation = true;
        await (optionWork = SeekAsync(frame?.Time ?? 0, preservePlayhead: true));
    }
    /// <summary>
    /// Measures the scenario's Auto range in the background on a snapshot of <paramref name="source"/> (the preview's controls and
    /// scenario as they are now); a newer measurement, another scene or the preview's end supersedes it. Auto follows the result:
    /// playback goes on to the new end, and stops where it already passed it.
    /// </summary>
    private Task<AnimationDuration?> MeasureInBackground(AnimationPreviewContext source)
    {
        measureCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        measureCancellation = cancellation; long id = ++measureId;
        var create = CapturePlayer(source.Snapshot(), cancellation.Token);
        var task = MeasureAsync();
        if (!task.IsCompleted) measuring = task;
        Changed();
        return task;

        async Task<AnimationDuration?> MeasureAsync()
        {
            try
            {
                var token = cancellation.Token;
                var measured = await Task.Run(() => create().MeasureDuration(token), token);
                if (id != measureId || disposed) return null;
                duration = measured;
                if (!customRange)
                {
                    if (playing && frame?.Time > measured.Seconds) SetPlaying(false);
                    ApplyRange(measured.Seconds);
                }
                return measured;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                if (id == measureId && !disposed) Note("Auto range unavailable: " + ex.Message);
                return null;
            }
            finally
            {
                cancellation.Dispose();
                if (id == measureId) { measuring = null; measureCancellation = null; Changed(); }
            }
        }
    }
    private void ApplyRange(double seconds) { rangeEnd = seconds; Render(); Changed(); }
    /// <summary>"Auto · 5 s after the last timed event (6.100 s) · pieces fall until they hit the ground", "Custom · 0–30 s · …" (the flyout, the Scenario tab's Range).</summary>
    internal string RangeDescription => AnimationRangeText.Describe(!customRange, 0, rangeEnd, duration, measuring != null);
    private async Task PrepareAudioAsync(CancellationToken token, AnimationPreviewContext? source = null)
    {
        if (context == null) return;
        int revision = audioRevision;
        var snapshot = (source ?? context).Snapshot();
        audioPreparing++; audioDiagnostics.Clear();
        try
        {
            // Only the sounds of the calls the scenario runs (its excluded calls are left out).
            await audio.PrepareAsync(snapshot, entryIndex, scenario.ToExecutionPolicy(), true, token);
            if (revision == audioRevision) audioDirty = false;
        }
        finally { audioPreparing--; }
    }
    private void AudioChanged() => optionWork = AudioChangedAsync();
    private async Task AudioChangedAsync()
    {
        if (!ready) return;
        bool wasMuted = audio.Muted;
        audio.Muted = muted; audio.Volume = (float)volume;
        if (audio.Muted) audio.Stop();
        else if (wasMuted && !audio.IsPrepared && context != null && audioPreparing == 0)
        {
            using var request = PreviewOperation.Link(lifetime.Token);
            try { await PrepareAudioAsync(request.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { RecordPreviewError(ex.Message); }
        }
    }
    private long levelGeneration, publishedLevelGeneration;
    private CancellationToken levelOperationToken;
    private bool refreshingLevel, levelResume;
    private SceneViewport.ViewPose? levelRefreshView;
    private int? levelRefreshSelection;
    private async void LevelChanged(object sender, RoutedEventArgs e) => await (optionWork = LevelChangedAsync());
    private async Task LevelChangedAsync()
    {
        if (ready && !changing && renderFailure != null) { await SeekAsync(0); return; }
        if (ready && seekRunning) { contextDirty = true; resetSimulation = true; pendingPlay |= playing; await SeekAsync(frame?.Time ?? 0, preservePlayhead: true); return; }
        if (!ready || changing || context == null || frame == null || LoadingPanel.Visibility == Visibility.Visible && !refreshingLevel) return;
        long generation = ++levelGeneration;
        bool resume = playing || refreshingLevel && levelResume; levelResume = resume; SetPlaying(false);
        var operationToken = levelOperationToken = PreviewOperation.Current;
        initializing?.Cancel(); initializing?.Dispose(); initializing = PreviewOperation.Link(lifetime.Token);
        if (!refreshingLevel) { levelRefreshView = viewport.CaptureView(); levelRefreshSelection = viewport.FramingSelection; }
        var token = initializing.Token; refreshingLevel = true; LoadingPanel.Visibility = Visibility.Visible; Changed();
        var retainedView = levelRefreshView ?? viewport.CaptureView(); int? retainedSelection = levelRefreshSelection;
        try
        {
            await viewport.ShowAnimationAsync(context, frame, resolver, ShowLevel.IsChecked == true, token, player?.LodLevel ?? 0, ShowHorizon.IsChecked == true, lifetime.Token);
            token.ThrowIfCancellationRequested(); viewport.RestoreView(retainedView); viewport.SelectFramingNode(retainedSelection);
            if (!Render()) return;
            if (levelResume) SetPlaying(true); publishedLevelGeneration = generation;
        }
        catch (OperationCanceledException)
        {
            if (operationToken.IsCancellationRequested && !disposed && !lifetime.IsCancellationRequested && initializing?.Token == token)
            {
                using var recovery = PreviewOperation.Begin(CancellationToken.None);
                initializing.Dispose(); initializing = PreviewOperation.Link(lifetime.Token); token = initializing.Token;
                try
                {
                    await viewport.ShowAnimationAsync(context, frame, resolver, ShowLevel.IsChecked == true, token, player?.LodLevel ?? 0, ShowHorizon.IsChecked == true, lifetime.Token);
                    token.ThrowIfCancellationRequested(); viewport.RestoreView(retainedView); viewport.SelectFramingNode(retainedSelection);
                    if (!Render()) return;
                    if (levelResume) SetPlaying(true); publishedLevelGeneration = generation;
                }
                catch (OperationCanceledException) { }
                catch (SceneViewport.RenderLimitException ex) { RenderUnavailable(ex); return; }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { previewOperationFailure = ex.Message; RecordPreviewError(ex.Message); }
            }
        }
        catch (SceneViewport.RenderLimitException ex) { RenderUnavailable(ex); return; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { previewOperationFailure = ex.Message; RecordPreviewError(ex.Message); }
        finally
        {
            // A canceled MCP request leaves this editor alive. Only the current
            // refresh owns its overlay; an older request cannot hide a newer one.
            if (!disposed && initializing?.Token == token) { resume = levelResume; refreshingLevel = false; levelResume = false; levelRefreshView = null; levelRefreshSelection = null; LoadingPanel.Visibility = Visibility.Collapsed; Changed(); }
        }
        if (resetSimulation && !token.IsCancellationRequested && !disposed)
        {
            pendingPlay |= resume;
            await SeekAsync(frame?.Time ?? 0, preservePlayhead: true);
        }
    }
    private long lodGeneration, publishedLodGeneration;
    private CancellationToken lodOperationToken;
    private async void LodChanged(object sender, SelectionChangedEventArgs e) => await (optionWork = LodChangedAsync());
    private async Task LodChangedAsync()
    {
        if (!ready || changing) return;
        if (player == null)
        {
            if (renderFailure != null) await SeekAsync(0);
            return;
        }
        long generation = ++lodGeneration;
        lodOperationToken = PreviewOperation.Current;
        int requested = Lod.SelectedIndex, retained = player.LodLevel;
        using var request = PreviewOperation.Link(lifetime.Token);
        try
        {
            await simulationGate.WaitAsync(request.Token);
            try
            {
                request.Token.ThrowIfCancellationRequested();
                if (generation != lodGeneration || Lod.SelectedIndex != requested) return;
                player.LodLevel = Math.Max(0, requested); frame = player.Frame(request.Token);
            }
            finally { simulationGate.Release(); }
            if (ShowLevel.IsChecked == true) await LevelChangedAsync();
            else if (!Render()) return;
            if (renderFailure != null) return;
            if (generation == lodGeneration && Lod.SelectedIndex == requested) publishedLodGeneration = generation;
        }
        catch (OperationCanceledException)
        {
            if (!disposed && generation == lodGeneration && Lod.SelectedIndex == requested)
            {
                if (player != null) player.LodLevel = retained;
                bool wasChanging = changing; changing = true;
                try { Lod.SelectedIndex = Math.Clamp(retained, 0, Math.Max(0, Lod.Items.Count - 1)); }
                finally { changing = wasChanging; }
            }
        }
        catch (SceneViewport.RenderLimitException ex) { RenderUnavailable(ex); }
        catch (IOException ex)
        {
            if (!disposed && generation == lodGeneration && Lod.SelectedIndex == requested)
            {
                if (player != null) player.LodLevel = retained;
                bool wasChanging = changing; changing = true;
                try { Lod.SelectedIndex = Math.Clamp(retained, 0, Math.Max(0, Lod.Items.Count - 1)); }
                finally { changing = wasChanging; }
                SetPlaying(false); previewOperationFailure = ex.Message; RecordPreviewError(ex.Message);
            }
        }
    }
    private void FrameClick(object sender, RoutedEventArgs e) => FramePreview();
    internal bool FramePreview() { if (frame != null && viewport.TryFrame("asset")) return true; Note("No visible geometry is available to frame."); return false; }
    /// <summary>
    /// Scenario → World → Choose GameZ… (null: the automatic scene beside the file). A failed preview is reported; a canceled
    /// request recovers the shown one with its own lifetime.
    /// </summary>
    internal async Task BindWorldAsync(string? worldPath)
    {
        int generation = contextGeneration + 1;
        await InitializeAsync(worldPath ?? "", recoverCanceledRequest: true);
        if (disposed || contextGeneration != generation) throw new StudioCommandException("context_changed", "The requested animation scene was superseded.");
        if (player == null || LoadingPanel.Visibility == Visibility.Visible) throw new StudioCommandException("preview_unavailable", LoadingText.Text);
    }
    /// <summary>Scenario → Bound root: binds the preview's root to <paramref name="node"/>, or clears the override (null).</summary>
    internal async Task BindRootAsync(int? node)
    {
        if (context == null) throw new InvalidOperationException("No scene is loaded.");
        if (node is int index)
        {
            if (index < 0 || index >= context.Scene.Nodes.Count) throw new ArgumentOutOfRangeException(nameof(node), "The scene has no such node.");
            context.RootOverrides[entryIndex] = index;
        }
        else if (!context.RootOverrides.Remove(entryIndex)) return;
        SetPlaying(false); resetSimulation = true;
        await (optionWork = SeekAsync(0));
        // A new bound root (an override, or the game's binding again) is framed as an opened animation is.
        if (frame != null) viewport.FrameAnimation();
        Note(node is int bound ? "Preview root bound to " + BoundRootLabel() : "Preview root override cleared: " + BoundRootLabel());
    }
    /// <summary>The scene's nodes as root choices (Bind root…): name filter, at most 500.</summary>
    internal IReadOnlyList<AnimationRootChoices.Choice> RootChoices(string query, CancellationToken token)
    {
        if (context == null) return [];
        return new AnimationRootChoices(context.Scene, context.Mission?.Actors ?? [], context.WorldTransform, token).Page(query, token);
    }
    private void Note(string text)
    {
        if (disposed) return;
        status = text; StatusChanged?.Invoke(text);
    }
    public void Pause() { levelResume = false; pendingPlay = false; SetPlaying(false); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; ready = false; pendingPlay = false; SetPlaying(false); lifetime.Cancel(); initializing?.Cancel(); initializing?.Dispose(); seeking?.Cancel(); seeking?.Dispose(); if (preferences != null) preferences.PropertyChanged -= PreferencesChanged; audio.Dispose(); viewport.Dispose(); lifetime.Dispose(); GC.SuppressFinalize(this);
    }
}
