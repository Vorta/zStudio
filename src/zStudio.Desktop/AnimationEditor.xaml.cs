using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class AnimationEditor : FieldEditor, IDisposable
{
    private readonly DocumentModel document;
    private readonly int entryIndex;
    private readonly AssetResolver resolver;
    private readonly AnimationEditSession edits;
    private readonly MainViewModel? preferences;
    private MissionDifficulty SelectedDifficulty => Difficulty.SelectedItem is MissionDifficulty value ? value : MissionDifficulty.Medium;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenSource? seeking;
    private CancellationTokenSource? initializing;
    private readonly SemaphoreSlim simulationGate = new(1);
    private bool resetSimulation;
    private bool contextDirty;
    private Vector3? activationStart, activationTarget;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
    private readonly Stopwatch clock = new();
    private readonly SceneViewport viewport = new();
    private readonly AnimationAudio audio = new();
    private readonly List<string> audioDiagnostics = [];
    private bool audioDirty = true;
    private int audioRevision, audioPreparing;
    internal AnimationAudio Audio => audio;
    internal event Action<double, double>? AudioUpdated;
    private AnimationPreviewContext? context;
    private AnimationPlayer? player;
    private AnimationFrame? frame;
    private double lastClock, playbackTarget;
    private bool ready, changing, playing;
    private bool pendingPlay, customRange;
    private AnimationDuration? duration;
    private int contextGeneration;
    private SceneViewport.ViewPose? contextRefreshView;
    private int appliedSeed = 1;
    private float appliedHeight;
    private Guid selectedSequence, selectedEvent;
    public AnimationPreviewOptions Options { get; } = new();
    public SceneViewport Viewport => viewport;
    public AnimationFrame? CurrentFrame => frame;
    public bool IsPlaying => playing;
    public int EntryIndex => entryIndex;
    public AnimationDuration? Duration => duration;
    public event Action<JsonObject, byte[]>? InspectionChanged;
    public event Action<string>? StatusChanged;
    public event Func<Task>? SaveRequested;
    private AnimationEntry Entry => edits.Package.Entries[entryIndex];
    private AnimationSequence? Sequence => Entry.AllSequences.FirstOrDefault(s => s.Id == selectedSequence);
    private AnimationEvent? Event => Sequence?.Events.FirstOrDefault(e => e.Id == selectedEvent);

    public AnimationEditor(DocumentModel document, int entryIndex, AssetResolver resolver, CancellationToken token, MainViewModel? preferences = null)
    {
        this.preferences = preferences;
        this.document = document; this.entryIndex = entryIndex; this.resolver = resolver; edits = document.AnimationEdits!;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, document.Lifetime.Token);
        InitializeComponent(); ViewportHost.Content = viewport; InitializeWorkspaceViews();
        Difficulty.ItemsSource = MainViewModel.DifficultyChoices; Difficulty.SelectedItem = preferences?.Difficulty ?? MissionDifficulty.Medium;
        if (preferences != null) preferences.PropertyChanged += PreferencesChanged;
        viewport.Information += Note;
        audio.Diagnostic += message =>
        {
            if (disposed) return;
            Mute.IsChecked = audio.Muted; Mute.ToolTip = message;
            if (!audioDiagnostics.Contains(message)) audioDiagnostics.Add(message);
            Note(message);
        };
        viewport.NodeSelected += index => Note($"Scene node #{index}: {context?.Scene.Nodes[index].Name}");
        Timeline.SeekRequested += time => { if (ResolvePendingDrafts()) _ = SeekAsync(time); };
        timer.Tick += Tick; edits.Changed += EditsChanged;
        Seed.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SeedChanged(Seed, e); e.Handled = true; } };
        EndTime.KeyDown += (_, e) => { if (e.Key == Key.Enter) { RangeChanged(EndTime, e); e.Handled = true; } };
        RefreshLists(); ready = true; AudioChanged(this, new RoutedEventArgs());
    }
    public async Task InitializeAsync(string? worldPath = null)
    {
        contextRefreshView = null;
        initializing?.Cancel(); initializing?.Dispose(); initializing = PreviewOperation.Link(lifetime.Token);
        var token = initializing.Token;
        int generation = ++contextGeneration; seeking?.Cancel(); SetPlaying(false); LoadingPanel.Visibility = Visibility.Visible;
        context = null; player = null; audioDirty = true; ++audioRevision;
        LoadingText.Text = "Loading animation…";
        try
        {
            var loaded = await document.GetAnimationContextAsync(resolver, token, worldPath, SelectedDifficulty);
            if (generation != contextGeneration || disposed) return; context = loaded;
            changing = true; Lod.ItemsSource = SceneLods.Choices(context.Lods.Count()); Lod.SelectedIndex = 0; changing = false;
            token.ThrowIfCancellationRequested(); player = CreatePlayer(); frame = player.Frame();
            await UpdateDurationAsync(token);
            if (generation != contextGeneration || disposed) return;
            await viewport.ShowAnimationAsync(context, frame, resolver, ShowLevel.IsChecked == true, token, showHorizon: ShowHorizon.IsChecked == true, previewLifetime: lifetime.Token);
            LoadingText.Text = "Preparing audio…";
            await PrepareAudioAsync(token);
            token.ThrowIfCancellationRequested();
            LoadingPanel.Visibility = Visibility.Collapsed; Render();
            if (resetSimulation) await SeekAsync(0);
            Note($"Bound to {context.World.Path} · root #{context.ResolveRoot(Entry)} ({Entry.RootName})");
            StartPendingPlayback();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { if (generation == contextGeneration && !disposed) { pendingPlay = false; LoadingText.Text = ex.Message + "\nUse Scene → Choose GameZ… to choose the mission scene. Event editing is still available."; RecordPreviewError(ex.Message); } }
    }
    private AnimationPlayer CreatePlayer(AnimationPreviewContext? source = null) => new(source ?? context!, entryIndex, int.TryParse(Seed.Text, out int seed) ? seed : 1, Phase.SelectedIndex == 1)
    { ConditionOverride = Condition.SelectedIndex switch { 1 => true, 2 => false, _ => null }, ActivationStart = activationStart, ReferencePosition = activationTarget, LodLevel = Math.Max(0, Lod.SelectedIndex), GroundPlaneEnabled = GroundCollision.IsChecked == true, PreviewHeight = appliedHeight };
    private void SetPlaying(bool value)
    {
        playing = value;
        PlayIcon.Data = Geometry.Parse(value ? "M3,2 H7 V14 H3 Z M10,2 H14 V14 H10 Z" : "M4,2 L14,8 4,14 Z");
        PlayButton.ToolTip = value ? "Pause (Space)" : "Play (Space)";
        AutomationProperties.SetName(PlayButton, value ? "Pause" : "Play");
        if (value) { clock.Restart(); lastClock = 0; playbackTarget = player?.Time ?? 0; timer.Start(); } else { timer.Stop(); audio.Stop(); }
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (player == null || context == null || lifetime.IsCancellationRequested) { SetPlaying(false); return; }
        try
        {
            double now = clock.Elapsed.TotalSeconds, delta = Math.Min(.1, now - lastClock) * new[] { .25, .5, 1, 2, 4 }[Math.Clamp(Speed.SelectedIndex, 0, 4)]; lastClock = now;
            double time = playbackTarget + delta;
            if (time > SeekSlider.Maximum)
            {
                if (Loop.IsChecked == true) { player.Reset(); time = 0; audio.Stop(); }
                else { SetPlaying(false); time = Math.Min(time, SeekSlider.Maximum); if (customRange || duration?.IsFinite != true) Note("Preview limit reached. Extend the range in Settings to continue."); }
            }
            playbackTarget = time; frame = player.AdvanceTo(time, token: lifetime.Token); Render();
            long audioStart = Stopwatch.GetTimestamp();
            audio.Update(frame, context, playing);
            AudioUpdated?.Invoke(now, Stopwatch.GetElapsedTime(audioStart).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { SetPlaying(false); RecordPreviewError(ex.Message); }
    }
    public async Task SeekAsync(double seconds, bool preservePlayhead = false)
    {
        if (context == null || disposed || LoadingPanel.Visibility == Visibility.Visible) return;
        SetPlaying(false); seeking?.Cancel(); seeking?.Dispose(); seeking = PreviewOperation.Link(lifetime.Token);
        var token = seeking.Token; PlayButton.IsEnabled = false;
        try
        {
            await simulationGate.WaitAsync(token);
            try
            {
                bool refreshScene = contextDirty;
                var nextContext = context;
                var view = refreshScene ? contextRefreshView ??= viewport.CaptureView() : viewport.CaptureView();
                if (refreshScene)
                {
                    var updated = await document.GetAnimationContextAsync(resolver, token, difficulty: SelectedDifficulty);
                    token.ThrowIfCancellationRequested();
                    updated.RemapBindingsFrom(context); nextContext = updated;
                }
                var measured = resetSimulation ? await MeasureDurationAsync(nextContext, token) : duration;
                if (audioDirty) await PrepareAudioAsync(token, nextContext);
                token.ThrowIfCancellationRequested();
                double range = !customRange && resetSimulation ? measured?.Seconds ?? SeekSlider.Maximum : SeekSlider.Maximum;
                if (preservePlayhead && !customRange && seconds > range)
                    range = Math.Min(3600, seconds + (pendingPlay ? 5 : AnimationPlayer.StepSeconds));
                double target = Math.Clamp(seconds, 0, range);
                var nextPlayer = resetSimulation || player == null ? CreatePlayer(nextContext) : player;
                var nextFrame = await Task.Run(() => nextPlayer.AdvanceTo(target, true, token), token);
                token.ThrowIfCancellationRequested();
                if (refreshScene)
                {
                    try { await viewport.ShowAnimationAsync(nextContext, nextFrame, resolver, ShowLevel.IsChecked == true, token, Math.Max(0, Lod.SelectedIndex), ShowHorizon.IsChecked == true, lifetime.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException && !token.IsCancellationRequested)
                    {
                        // Resource failures after a render replacement must also keep the last valid scene.
                        if (frame != null)
                        {
                            await viewport.ShowAnimationAsync(context, frame, resolver, ShowLevel.IsChecked == true, token, Math.Max(0, Lod.SelectedIndex), ShowHorizon.IsChecked == true, lifetime.Token);
                            viewport.RestoreView(view); Render();
                        }
                        throw;
                    }
                    token.ThrowIfCancellationRequested(); viewport.RestoreView(view);
                    changing = true; int lod = Lod.SelectedIndex; Lod.ItemsSource = SceneLods.Choices(nextContext.Lods.Count()); Lod.SelectedIndex = Math.Clamp(lod, 0, Lod.Items.Count - 1); changing = false;
                }
                context = nextContext; player = nextPlayer; frame = nextFrame; duration = measured;
                contextDirty = false; contextRefreshView = null; resetSimulation = false; ApplyRange(range);
                Render();
            }
            finally { simulationGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { contextRefreshView = null; Note($"Preview was not updated; retaining {context?.Mission?.Layout.Label}. {ex.Message}"); }
        finally
        {
            if (!disposed && seeking?.Token == token)
            {
                PlayButton.IsEnabled = true;
                if (!token.IsCancellationRequested) StartPendingPlayback();
            }
        }
    }
    private void PresentationChanged(object sender, RoutedEventArgs e) { if (ready) Render(); }
    private void GridChanged(object sender, RoutedEventArgs e) { if (ready) viewport.SetGroundGrid(ShowGrid.IsChecked == true); }
    private void HeightKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) { HeightEditingFinished(sender, e); e.Handled = true; }
    }
    private void HeightLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Overflow reparenting, window deactivation and workspace layout are not input commits.
        if (Window.GetWindow(this) is MainWindow { IsChangingLayout: true } || e.NewFocus is not DependencyObject target ||
            Window.GetWindow(target) != Window.GetWindow(this) ||
            PropertyContext.FindAncestor<System.Windows.Controls.Primitives.Thumb>(target) != null ||
            PropertyContext.FindAncestor<System.Windows.Controls.Primitives.MenuBase>(target) != null) return;
        HeightEditingFinished(sender, e);
    }
    private void HeightEditingFinished(object sender, RoutedEventArgs e)
    {
        if (!ready || disposed) return;
        PreviewHeight.Text = appliedHeight.ToString(CultureInfo.InvariantCulture);
    }
    private async void HeightChanged(object sender, TextChangedEventArgs e) => await (optionWork = HeightChangedAsync(sender, e));
    private async Task HeightChangedAsync(object sender, TextChangedEventArgs e)
    {
        if (!ready || changing || disposed) return;
        // Empty and incomplete text is normal while replacing a number. Keep the
        // most recent valid height without interrupting typing or moving the caret.
        if (!float.TryParse(PreviewHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float height) || !float.IsFinite(height) || height is < -999 or > 999)
            return;
        if (height == appliedHeight) return;
        appliedHeight = height; resetSimulation = true; pendingPlay |= playing;
        await SeekAsync(frame?.Time ?? 0, preservePlayhead: true);
    }
    private async void GroundChanged(object sender, RoutedEventArgs e) => await (optionWork = GroundChangedAsync(sender, e));
    private async Task GroundChangedAsync(object sender, RoutedEventArgs e)
    {
        if (!ready || changing || disposed) return;
        viewport.SetGroundGrid(ShowGrid.IsChecked == true);
        resetSimulation = true;
        pendingPlay |= playing;
        await SeekAsync(frame?.Time ?? 0, preservePlayhead: true);
    }
    private void Render()
    {
        if (frame == null || disposed) return;
        MissionLayoutLabel.Text = context?.Mission?.Layout.Label ?? "Mission start";
        MissionLayoutLabel.ToolTip = context?.Mission?.Layout.Description;
        int root = context?.ResolveRoot(Entry) ?? -1;
        RootBindingLabel.Text = root >= 0 && root < context!.Scene.Nodes.Count
            ? $"Bound root: {context.Scene.Nodes[root].Name} · node #{root}" : $"Bound root: unresolved ({Entry.RootName})";
        viewport.SetGroundGrid(ShowGrid.IsChecked == true);
        viewport.UpdateAnimationFrame(frame, FollowCamera.IsChecked == true, Lighting.IsChecked == true);
        changing = true; SeekSlider.Value = Math.Min(SeekSlider.Maximum, frame.Time); changing = false;
        TimeLabel.Text = $"{Math.Round(frame.Time * 60):0} / {Math.Round(SeekSlider.Maximum * 60):0} f";
        TimeLabel.ToolTip = $"{frame.Time:0.000} / {SeekSlider.Maximum:0.000} seconds · 60 fps";
        AutomationProperties.SetName(TimeLabel, TimeLabel.ToolTip.ToString());
        Timeline.Frame = frame; Timeline.SelectedSequence = selectedSequence; Timeline.InvalidateVisual();
        var c = frame.ScreenColor;
        ScreenOverlay.Background = Lighting.IsChecked == true ? new SolidColorBrush(Color.FromScRgb(Math.Clamp(c.W, 0, 1), Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1))) : Brushes.Transparent;
        WaveOverlay.Children.Clear();
        if (Lighting.IsChecked == true && frame.ScreenWave.Z > 0)
        {
            var wave = frame.ScreenWave; double radius = Math.Clamp(wave.Z, 0, 10000);
            Ellipse circle = new() { Width = radius * 2, Height = radius * 2, Stroke = Brushes.LightSkyBlue, StrokeThickness = Math.Clamp(Math.Abs(wave.W), 1, 12), Opacity = .55 };
            Canvas.SetLeft(circle, wave.X - radius); Canvas.SetTop(circle, wave.Y - radius); WaveOverlay.Children.Add(circle);
        }
        if (frame.Time == 0 || Math.Round(frame.Time * 60) % 6 == 0 || !playing)
        {
            string camera = frame.Camera is { } currentCamera ? $"Camera: {currentCamera.Name} #{currentCamera.SourceNode} · {currentCamera.FieldOfView:0.#}° horizontal FOV" : "Camera: no animated camera pose at this time";
            string ground = GroundCollision.IsChecked == true ? "Ground: Y=0 · mesh-based flat-ground collision" : "Ground: collision disabled (authored gravity retained)";
            FollowCamera.Tag = "Follow the authored animation camera when available. " + camera;
            Diagnostics.Text = context?.Mission?.Layout.Description + "\n" + RootBindingLabel.Text + "\n" + camera + "\n" + ground + $" · preview height offset {appliedHeight:0.###} game units" + "\n\n" + string.Join("\n", frame.Diagnostics.Concat(audioDiagnostics));
            List<string> overrides = [];
            if (Condition.SelectedIndex != 0) overrides.Add(Condition.SelectedIndex == 1 ? "conditions forced true" : "conditions forced false");
            if (context?.RootOverrides.ContainsKey(entryIndex) == true) overrides.Add("root binding overridden");
            if (activationStart != null || activationTarget != null) overrides.Add("activation points overridden");
            if (appliedHeight != 0) overrides.Add($"height offset {appliedHeight:0.###} game units");
            OverrideBadge.Text = overrides.Count == 0 ? "" : "Preview overrides: " + string.Join(" · ",overrides);
            OverrideBadge.Visibility = overrides.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateRuntimeTools();
        }
    }
    private void RefreshLists()
    {
        RefreshProgram(); RefreshProperties(); CommandsChanged?.Invoke();
    }
    private void EditsChanged()
    {
        if (disposed) return;
        double time = frame?.Time ?? 0; contextDirty = true; resetSimulation = true; audioDirty = true; ++audioRevision;
        RefreshLists(); _ = SeekAsync(time);
    }
    private void TryEdit(Action action)
    {
        if (!committingDraft && (!ResolvePendingDrafts() || ResolvePropertyDrafts?.Invoke() == false)) return;
        try { SetPlaying(false); action(); }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        {
            if (committingDraft) throw;
            RecordPreviewError(ex.Message); MessageBox.Show(Window.GetWindow(this), ex.Message, "Animation edit", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private int SelectedEventIndex => Sequence?.Events.FindIndex(e => e.Id == selectedEvent) ?? -1;
    private void InsertEvent(AnimationEventSpec spec)
    {
        if (Sequence == null || !ResolvePendingDrafts()) return;
        TryEdit(() => { var id = edits.InsertEvent(entryIndex, selectedSequence, spec.Type, selectedEvent); SelectSource(selectedSequence, id); });
    }
    private void CopyEventClick(object sender, RoutedEventArgs e) { if (Event == null) return; TryEdit(() => { var id = edits.ChangeEventStructure(entryIndex, selectedSequence, selectedEvent, "duplicate"); SelectSource(selectedSequence, id); }); }
    private void DeleteEventClick(object sender, RoutedEventArgs e) { if (Event != null) TryEdit(() => edits.ChangeEventStructure(entryIndex, selectedSequence, selectedEvent, "delete")); }
    private void MoveEvent(int direction) { int at = SelectedEventIndex, to = at + direction; if (Sequence == null || at < 0 || to < 0 || to >= Sequence.Events.Count) return; TryEdit(() => edits.ChangeEventStructure(entryIndex, selectedSequence, selectedEvent, direction < 0 ? "up" : "down")); }
    private void MoveEventUpClick(object sender, RoutedEventArgs e) => MoveEvent(-1);
    private void MoveEventDownClick(object sender, RoutedEventArgs e) => MoveEvent(1);
    private void AddSequenceClick(object sender, RoutedEventArgs e) => TryEdit(() => edits.AddSequence(entryIndex));
    private void CopySequenceClick(object sender, RoutedEventArgs e) { if (Sequence != null) TryEdit(() => edits.AddSequence(entryIndex, selectedSequence)); }
    private void DeleteSequenceClick(object sender, RoutedEventArgs e) { if (Sequence != null) TryEdit(() => edits.DeleteSequence(entryIndex, selectedSequence)); }
    public void Undo() => TryEdit(edits.Undo);
    public void Redo() => TryEdit(edits.Redo);
    private void UndoClick(object sender, RoutedEventArgs e) => Undo();
    private void RedoClick(object sender, RoutedEventArgs e) => Redo();
    private async void SaveClick(object sender, RoutedEventArgs e) { SetPlaying(false); if (SaveRequested != null) await SaveRequested(); }
    public void TogglePlayback()
    {
        if (disposed || !ResolvePendingDrafts()) return;
        if (player == null || LoadingPanel.Visibility == Visibility.Visible || !PlayButton.IsEnabled) { pendingPlay = !pendingPlay; return; }
        if (!playing && player.Time >= SeekSlider.Maximum) { player.Reset(); frame = player.Frame(); Render(); }
        seeking?.Cancel(); SetPlaying(!playing);
    }
    private void StartPendingPlayback() { if (pendingPlay && !disposed && LoadingPanel.Visibility != Visibility.Visible && PlayButton.IsEnabled) { pendingPlay = false; TogglePlayback(); } }
    private void PlayClick(object sender, RoutedEventArgs e) => TogglePlayback();
    private void StopClick(object sender, RoutedEventArgs e) => Stop();
    private void PreviousFrameClick(object sender, RoutedEventArgs e) => Step(-1);
    private void NextFrameClick(object sender, RoutedEventArgs e) => Step(1);
    private async void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || changing) return;
        if (ResolvePendingDrafts()) await SeekAsync(e.NewValue);
        else { changing = true; SeekSlider.Value = frame?.Time ?? 0; changing = false; }
    }
    private async void PreviewOptionChanged(object sender, SelectionChangedEventArgs e) => await (optionWork = PreviewOptionChangedAsync(sender, e));
    private async Task PreviewOptionChangedAsync(object sender, SelectionChangedEventArgs e) { if (ready && !changing) { resetSimulation = true; await SeekAsync(0); } }
    private async void SeedChanged(object sender, RoutedEventArgs e) => await (optionWork = SeedChangedAsync(sender, e));
    private async Task SeedChangedAsync(object sender, RoutedEventArgs e) { if (!ready) return; if (!int.TryParse(Seed.Text, out int seed)) { Note("Seed must be a 32-bit integer."); Seed.Text = "1"; seed = 1; } if (appliedSeed == seed) return; appliedSeed = seed; resetSimulation = true; await SeekAsync(0); }
    private void RangeChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        if (!double.TryParse(EndTime.Text, CultureInfo.InvariantCulture, out double end) || !double.IsFinite(end) || end < AnimationPlayer.StepSeconds || end > 3600) { EndTime.Text = SeekSlider.Maximum.ToString(CultureInfo.InvariantCulture); Note("Range must be between 1/60 second and 3600 seconds."); return; }
        if (Math.Abs(end - SeekSlider.Maximum) < AnimationPlayer.StepSeconds / 2) return;
        customRange = true; ApplyRange(Math.Ceiling(end * 60) / 60); _ = SeekAsync(frame?.Time ?? 0);
    }
    private void TransportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 480;
        Grid.SetRow(TimeLabel, narrow ? 1 : 0); Grid.SetColumn(TimeLabel, narrow ? 0 : 2);
        Grid.SetColumnSpan(TimeLabel, narrow ? 3 : 1); Grid.SetColumnSpan(SeekSlider, narrow ? 2 : 1);
        TimeLabel.HorizontalAlignment = HorizontalAlignment.Right;
    }
    private void FitTraceClick(object sender, RoutedEventArgs e) { Timeline.Duration = SeekSlider.Maximum; Timeline.InvalidateVisual(); }
    private async void AutoRangeClick(object sender, RoutedEventArgs e) => await (optionWork = AutoRangeClickAsync(sender, e));
    private async Task AutoRangeClickAsync(object sender, RoutedEventArgs e) { customRange = false; if (duration != null) ApplyRange(duration.Seconds); await SeekAsync(frame?.Time ?? 0); }
    private async Task UpdateDurationAsync(CancellationToken token)
    {
        if (context == null) return;
        duration = await MeasureDurationAsync(context, token);
        ApplyRange(customRange ? SeekSlider.Maximum : duration.Seconds);
    }
    private Task<AnimationDuration> MeasureDurationAsync(AnimationPreviewContext source, CancellationToken token)
    {
        var measuring = CreatePlayer(source.Snapshot());
        return Task.Run(() => measuring.MeasureDuration(token), token);
    }
    private void ApplyRange(double seconds)
    {
        changing = true; SeekSlider.Maximum = seconds; Timeline.Duration = seconds;
        EndTime.Text = seconds.ToString("0.###", CultureInfo.InvariantCulture); changing = false;
        bool extended = !customRange && duration?.IsFinite == true && seconds > duration.Seconds + AnimationPlayer.StepSeconds / 2;
        RangeLabel.Text = extended ? "Auto duration · extended view" : customRange ? "Custom range" : duration?.Kind switch
        {
            AnimationDurationKind.Finite => "Auto duration", AnimationDurationKind.Looping => "Looping · preview range",
            AnimationDurationKind.OpenEnded => "Open-ended · preview range", _ => "Partial · preview range"
        };
        RangeLabel.ToolTip = duration?.Explanation + (extended ? " The view was extended to retain the playhead after changing ground collision or height; Auto restores the calculated range." : "");
        Timeline.InvalidateVisual(); Render();
    }
    private async Task PrepareAudioAsync(CancellationToken token, AnimationPreviewContext? source = null)
    {
        if (context == null) return;
        int revision = audioRevision;
        var snapshot = (source ?? context).Snapshot();
        audioPreparing++; audioDiagnostics.Clear();
        try
        {
            await audio.PrepareAsync(snapshot, entryIndex, token);
            if (revision == audioRevision) audioDirty = false;
        }
        finally { audioPreparing--; }
    }
    private async void AudioChanged(object sender, RoutedEventArgs e) => await (optionWork = AudioChangedAsync(sender, e));
    private async Task AudioChangedAsync(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        bool wasMuted = audio.Muted;
        audio.Muted = Mute.IsChecked == true; audio.Volume = (float)Volume.Value;
        if (audio.Muted) audio.Stop();
        else if (wasMuted && !audio.IsPrepared && context != null && audioPreparing == 0)
        {
            try { await PrepareAudioAsync(lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { RecordPreviewError(ex.Message); }
        }
    }
    private async void LevelChanged(object sender, RoutedEventArgs e) => await (optionWork = LevelChangedAsync(sender, e));
    private async Task LevelChangedAsync(object sender, RoutedEventArgs e)
    {
        if (ready && !PlayButton.IsEnabled) { contextDirty = true; resetSimulation = true; pendingPlay |= playing; _ = SeekAsync(frame?.Time ?? 0, preservePlayhead: true); return; }
        if (!ready || changing || context == null || frame == null || LoadingPanel.Visibility == Visibility.Visible) return;
        bool resume = playing; SetPlaying(false);
        initializing?.Cancel(); initializing?.Dispose(); initializing = PreviewOperation.Link(lifetime.Token);
        var token = initializing.Token; LoadingPanel.Visibility = Visibility.Visible;
        try
        {
            await viewport.ShowAnimationAsync(context, frame, resolver, ShowLevel.IsChecked == true, token, Math.Max(0, Lod.SelectedIndex), ShowHorizon.IsChecked == true, lifetime.Token);
            token.ThrowIfCancellationRequested(); Render(); if (resume) SetPlaying(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RecordPreviewError(ex.Message); }
        finally { if (!token.IsCancellationRequested && !disposed) LoadingPanel.Visibility = Visibility.Collapsed; }
        if (resetSimulation && !token.IsCancellationRequested && !disposed)
        {
            pendingPlay |= resume;
            await SeekAsync(frame?.Time ?? 0, preservePlayhead: true);
        }
    }
    private async void LodChanged(object sender, SelectionChangedEventArgs e) => await (optionWork = LodChangedAsync(sender, e));
    private async Task LodChangedAsync(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changing || player == null) return;
        try
        {
            await simulationGate.WaitAsync(lifetime.Token);
            try { player.LodLevel = Math.Max(0, Lod.SelectedIndex); frame = player.Frame(); }
            finally { simulationGate.Release(); }
            if (ShowLevel.IsChecked == true) LevelChanged(sender, e);
            else { Render(); viewport.FrameAnimation(frame); }
        }
        catch (OperationCanceledException) { }
    }
    private void FrameClick(object sender, RoutedEventArgs e) { if (frame != null) viewport.FrameAnimation(frame); }
    private async void WorldClick(object sender, RoutedEventArgs e) { OpenFileDialog dialog = new() { Filter = "GameZ scene|*.zbd", Title = "Choose this animation's mission scene" }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) await InitializeAsync(dialog.FileName); }
    private async void BindClick(object sender, RoutedEventArgs e)
    {
        if (context == null) return; SetPlaying(false);
        TextBox query = new() { Margin = new(8), ToolTip = "Filter scene nodes" }; ListBox list = new() { DisplayMemberPath = "Label", Margin = new(8), SelectionMode = SelectionMode.Single };
        var choices = context.Scene.Nodes.Select(n => new NodeChoice(n.Index, $"#{n.Index} · {n.Name} ({n.Class})" + (context.Mission?.Actors.FirstOrDefault(a => a.Root == n.Index) is { } actor ? $" · {actor.PlacementSource} · {context.WorldTransform(n.Index).Translation}" : ""))).ToArray(); list.ItemsSource = choices.Where(c => c.Label.Contains(Entry.RootName, StringComparison.OrdinalIgnoreCase)).Take(500).ToArray();
        query.TextChanged += (_, _) => list.ItemsSource = choices.Where(c => c.Label.Contains(query.Text, StringComparison.OrdinalIgnoreCase)).Take(500).ToArray();
        Button bind = new() { Content = "Bind selected root", Margin = new(8), IsDefault = true };
        DockPanel panel = new(); DockPanel.SetDock(query, Dock.Top); DockPanel.SetDock(bind, Dock.Bottom); panel.Children.Add(query); panel.Children.Add(bind); panel.Children.Add(list);
        Window dialog = new() { Owner = Window.GetWindow(this), Title = "Preview root binding · first 500 matches", Width = 520, Height = 600, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        bind.Click += (_, _) => { if (list.SelectedItem != null) dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true && list.SelectedItem is NodeChoice selected) { context.RootOverrides[entryIndex] = selected.Index; resetSimulation = true; await SeekAsync(0); if (frame != null) viewport.FrameAnimation(frame); Note("Preview root bound to " + selected.Label); }
    }
    private void Note(string text) { if (disposed) return; StatusChanged?.Invoke(text); Diagnostics.Text = text + "\n" + Diagnostics.Text; }
    public void Pause() { pendingPlay = false; SetPlaying(false); }
    public void Dispose()
    {
        if (disposed) return; if (operationDiagnostics != null) operationDiagnostics.CollectionChanged -= OperationDiagnosticsChanged; disposed = true; ready = false; pendingPlay = false; SetPlaying(false); lifetime.Cancel(); initializing?.Cancel(); initializing?.Dispose(); seeking?.Cancel(); seeking?.Dispose(); edits.Changed -= EditsChanged; if (preferences != null) preferences.PropertyChanged -= PreferencesChanged; audio.Dispose(); viewport.Dispose(); lifetime.Dispose(); GC.SuppressFinalize(this);
    }
    private sealed record SequenceChoice(Guid Id, string Label);
    private sealed record EventChoice(Guid Id, int Index, string Name, string Mode, string Threshold);
    private sealed record NodeChoice(int Index, string Label);
}
