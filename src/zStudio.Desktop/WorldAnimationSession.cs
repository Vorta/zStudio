using System.Diagnostics;
using System.Numerics;
using System.Windows.Threading;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>Inputs for an isolated run over the already initialized source-world baseline.</summary>
public sealed record WorldAnimationScenario
{
    public int Seed { get; init; } = 1;
    public bool Cleanup { get; init; }
    public float Height { get; init; }
    public Vector3? ReferencePosition { get; init; }
    public Vector3? ActivationStart { get; init; }
    public int EffectLevel { get; init; } = 2;
    public AnimationExecutionPolicy Policy { get; init; } = AnimationExecutionPolicy.RootOnly;
}

public sealed record WorldAnimationSessionState(Guid Session, Guid Scenario, Guid? Run, string Status,
    bool Playing, bool Loading, bool Attached, double Time, double RangeStart, double RangeEnd,
    double Speed, bool Replay, bool Muted, double Volume, bool FollowCamera, bool Effects,
    string? Failure, long TraceDropped);

/// <summary>
/// Owns source-world evaluation and audio, never a viewport or a source document. All public commands run on the
/// viewport dispatcher. A frozen context and an owning-document validity guard define its baseline lifetime.
/// </summary>
public sealed class WorldAnimationSession : IDisposable
{
    private readonly SceneViewport viewport;
    private readonly AssetResolver resolver;
    private readonly Func<bool> isCurrent;
    private readonly int worldGeneration, lod;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenSource scenarioLifetime;
    private CancellationTokenSource? request;
    private readonly SemaphoreSlim simulationGate = new(1), analysisGate = new(1);
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = new();
    private readonly AnimationAudio audio;
    private AnimationPlayer? player;
    private Guid? attachment;
    private long generation;
    private bool disposed, playing, loading, tickPending, replay, followCamera, effects = true;
    private bool audioDirty = true;
    private double speed = 1, rangeStart, rangeEnd = 30, lastClock, playbackTarget;
    private string status = "Unprepared";
    private string? failure;

    public Guid SessionId { get; } = Guid.NewGuid();
    public Guid ScenarioId { get; private set; } = Guid.NewGuid();
    public Guid? RunId { get; private set; }
    public Guid? LastRunId { get; private set; }
    public Guid? LastScenarioId { get; private set; }
    public AnimationPreviewContext Context { get; }
    public int EntryIndex { get; }
    public WorldAnimationScenario Scenario { get; private set; } = new();
    public AnimationFrame? CurrentFrame { get; private set; }
    public AnimationFrame? LastFrame { get; private set; }
    public bool IsPlaying => playing;
    public bool IsLoading => loading;
    public bool IsAttached => attachment.HasValue;
    public bool IsDisposed => disposed;
    public event Action? Changed;
    public event Action<AnimationFrame>? FramePublished;
    public event Action<string>? Diagnostic;

    public WorldAnimationSessionState State => new(SessionId, ScenarioId, RunId, status, playing, loading,
        IsAttached, CurrentFrame?.Time ?? 0, rangeStart, rangeEnd, speed, replay, audio.Muted, audio.Volume,
        followCamera, effects, failure, CurrentFrame?.TraceDropped ?? 0);

    public WorldAnimationSession(AnimationPreviewContext frozenContext, int entry, SceneViewport viewport,
        AssetResolver resolver, Func<bool> isCurrent, CancellationToken lifetime = default, int lod = 0)
        : this(frozenContext, entry, viewport, resolver, isCurrent, lifetime, lod, new AnimationAudio()) { }

    internal WorldAnimationSession(AnimationPreviewContext frozenContext, int entry, SceneViewport viewport,
        AssetResolver resolver, Func<bool> isCurrent, CancellationToken lifetime, int lod, AnimationAudio audio)
    {
        ArgumentNullException.ThrowIfNull(frozenContext);
        ArgumentOutOfRangeException.ThrowIfNegative(entry);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entry, frozenContext.Package.Entries.Count);
        viewport.Dispatcher.VerifyAccess();
        Context = frozenContext; EntryIndex = entry; this.viewport = viewport; this.resolver = resolver;
        this.isCurrent = isCurrent; worldGeneration = viewport.SceneGeneration;
        this.lod = Math.Clamp(lod, 0, Math.Max(0, frozenContext.Lods.Count() - 1));
        this.audio = audio; audio.Volume = .5f; audio.Diagnostic += AudioDiagnostic;
        this.lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        scenarioLifetime = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
        timer = new(DispatcherPriority.Render, viewport.Dispatcher) { Interval = TimeSpan.FromSeconds(1.0 / 60) };
        timer.Tick += Tick;
        viewport.ManualNavigationStarting += ManualNavigationStarting;
    }

    public double RangeStart => rangeStart;
    public double RangeEnd => rangeEnd;
    public double Speed
    {
        get => speed;
        set
        {
            Verify();
            if (value is not (.25 or .5 or 1 or 2 or 4)) throw Invalid("Speed must be 0.25, 0.5, 1, 2 or 4.");
            speed = value; Changed?.Invoke();
        }
    }
    public bool Replay { get => replay; set { Verify(); replay = value; Changed?.Invoke(); } }
    public bool Muted
    {
        get => audio.Muted;
        set
        {
            Verify(); audio.Muted = value;
            if (value) audio.Stop();
            else if (playing) _ = ResumeAudioAsync();
            Changed?.Invoke();
        }
    }
    public double Volume
    {
        get => audio.Volume;
        set
        {
            Verify();
            if (!double.IsFinite(value) || value is < 0 or > 1) throw Invalid("Volume must be between 0 and 1.");
            audio.Volume = (float)value; Changed?.Invoke();
        }
    }
    public bool FollowCamera
    {
        get => followCamera;
        set { Verify(); followCamera = value; RefreshPresentation(); Changed?.Invoke(); }
    }
    public bool Effects
    {
        get => effects;
        set { Verify(); effects = value; RefreshPresentation(); Changed?.Invoke(); }
    }

    public void SetRange(double start, double end)
    {
        Verify();
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > 3600 || end <= start)
            throw Invalid("Preview range must be an increasing interval within 0–3600 seconds.");
        start = Math.Floor(start * 60 + 1e-7) / 60;
        end = Math.Ceiling(end * 60 - 1e-7) / 60;
        Pause(); rangeStart = start; rangeEnd = end;
        if (CurrentFrame is { } frame && (frame.Time < start || frame.Time > end)) Stop();
        Changed?.Invoke();
    }

    public void SetScenario(WorldAnimationScenario scenario)
    {
        Verify(); ArgumentNullException.ThrowIfNull(scenario); ArgumentNullException.ThrowIfNull(scenario.Policy);
        if (!float.IsFinite(scenario.Height) || scenario.Height is < -999 or > 999)
            throw Invalid("Preview height must be between -999 and 999 game units.");
        if (scenario.EffectLevel is < 0 or > 2) throw Invalid("Effect level must be 0, 1 or 2.");
        if (!Finite(scenario.ReferencePosition) || !Finite(scenario.ActivationStart)) throw Invalid("Preview positions must be finite.");
        Stop();
        scenarioLifetime.Cancel(); scenarioLifetime.Dispose();
        scenarioLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        Scenario = scenario; ScenarioId = Guid.NewGuid(); audioDirty = true;
        Changed?.Invoke();
    }

    public async Task TransportAsync(string action, double seconds = 0, CancellationToken token = default)
    {
        Verify();
        switch (action)
        {
            case "pause": Pause(); return;
            case "stop": Stop(); return;
            case "exit": Dispose(); return;
            case "play":
                if (playing) return;
                bool restart = CurrentFrame?.Time >= rangeEnd - 1e-7 || status is "Complete" or "Blocked";
                if (restart) ResetRun();
                await EvaluateAsync(restart || CurrentFrame == null ? rangeStart : Math.Max(rangeStart, CurrentFrame.Time), true, false, token);
                return;
            case "previous": seconds = Math.Max(rangeStart, (CurrentFrame?.Time ?? rangeStart) - AnimationPlayer.StepSeconds); break;
            case "next": seconds = Math.Min(rangeEnd, (CurrentFrame?.Time ?? rangeStart) + AnimationPlayer.StepSeconds); break;
            case "seek": break;
            default: throw Invalid("Unknown animation transport action.");
        }
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 3600)
            throw Invalid("Seek position must be between zero and one hour.");
        SetPlaying(false);
        await EvaluateAsync(seconds, false, false, token);
    }

    /// <summary>Pause accepts no late frame, including from an in-flight initial Play or clock tick.</summary>
    public void Pause()
    {
        Verify(); CancelRequest(); loading = false; SetPlaying(false);
        status = CurrentFrame == null ? "Unprepared" : "Paused"; Changed?.Invoke();
    }

    /// <summary>Restore the authoritative static baseline; never simulate a seek to frame zero.</summary>
    public void Stop()
    {
        viewport.Dispatcher.VerifyAccess();
        if (disposed) return;
        CancelRequest(); loading = false; SetPlaying(false);
        if (attachment is Guid owned) { viewport.DetachWorldAnimation(owned); attachment = null; }
        RetireFrame(); player = null; RunId = null; status = "Stopped"; failure = null;
        Changed?.Invoke();
    }

    public async Task<AnimationPaths> AnalyzePathsAsync(double seconds, CancellationToken token = default)
    {
        Verify();
        if (!double.IsFinite(seconds) || seconds is <= 0 or > 3600) throw Invalid("Path range must be greater than zero and at most one hour.");
        Guid expected = ScenarioId; var scenario = Scenario;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, scenarioLifetime.Token);
        await analysisGate.WaitAsync(linked.Token);
        try
        {
            var result = await Task.Run(() => CreatePlayer(scenario, linked.Token).AnalyzePaths(seconds, linked.Token), linked.Token);
            CheckAnalysis(expected, linked.Token); return result;
        }
        finally { analysisGate.Release(); }
    }

    public async Task<AnimationDuration> MeasureDurationAsync(CancellationToken token = default)
    {
        Verify(); Guid expected = ScenarioId; var scenario = Scenario;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, scenarioLifetime.Token);
        await analysisGate.WaitAsync(linked.Token);
        try
        {
            var result = await Task.Run(() => CreatePlayer(scenario, linked.Token).MeasureDuration(linked.Token), linked.Token);
            CheckAnalysis(expected, linked.Token); return result;
        }
        finally { analysisGate.Release(); }
    }

    private AnimationPlayer CreatePlayer(WorldAnimationScenario scenario, CancellationToken token)
        => new(Context, EntryIndex, scenario.Seed, scenario.Cleanup, token)
        {
            ExecutionPolicy = scenario.Policy, Terrain = Context.Terrain, ProceduralLimitsEnabled = true,
            PreviewHeight = scenario.Height, ReferencePosition = scenario.ReferencePosition,
            ActivationStart = scenario.ActivationStart, EffectLevel = scenario.EffectLevel, LodLevel = lod
        };

    private async Task EvaluateAsync(double seconds, bool playAfter, bool tick, CancellationToken external)
    {
        Verify();
        CancelRequest();
        long expected = generation; Guid scenarioId = ScenarioId; var scenario = Scenario;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(external, lifetime.Token);
        request = pending;
        if (!tick) { loading = true; status = player == null ? "Preparing" : "Seeking"; Changed?.Invoke(); }
        try
        {
            await simulationGate.WaitAsync(pending.Token);
            try
            {
                CheckRequest(expected, scenarioId, pending.Token);
                var active = player;
                Guid run = active == null ? Guid.NewGuid() : RunId ?? Guid.NewGuid();
                var result = await Task.Run(() =>
                {
                    var next = active ?? CreatePlayer(scenario, pending.Token);
                    return (Player: next, Frame: next.AdvanceTo(seconds, seeking: !tick, token: pending.Token));
                }, pending.Token);
                CheckRequest(expected, scenarioId, pending.Token);
                if (playAfter && !tick && !audio.Muted)
                {
                    await PrepareAudioAsync(scenario, pending.Token);
                    CheckRequest(expected, scenarioId, pending.Token);
                    await audio.StartOutputAsync(pending.Token);
                    CheckRequest(expected, scenarioId, pending.Token);
                }
                if (attachment == null) attachment = viewport.AttachWorldAnimation(Context, resolver, worldGeneration, lifetime.Token);
                if (!viewport.UpdateWorldAnimation(attachment.Value, result.Frame, followCamera, effects))
                    throw new StudioCommandException("context_changed", "The world animation attachment was replaced.");
                if (active == null && CurrentFrame != null) RetireFrame();
                player = result.Player; CurrentFrame = result.Frame; RunId = run; failure = null;
                bool blocked = player.IsDependencyBlocked && result.Frame.ActiveSounds.Count == 0;
                bool complete = player.IsComplete && result.Frame.ActiveSounds.Count == 0;
                bool atEnd = result.Frame.Time >= rangeEnd - 1e-7;
                status = blocked ? "Blocked" : complete ? "Complete" : atEnd ? "Preview end" : playAfter ? "Playing" : "Paused";
                SetPlaying(playAfter && !blocked && !complete && !atEnd);
                if (playing) audio.Update(result.Frame, true);
                FramePublished?.Invoke(result.Frame);
            }
            finally { simulationGate.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (!disposed && generation == expected)
            {
                SetPlaying(false);
                status = CurrentFrame == null ? "Unprepared" : "Paused";
            }
            if (external.IsCancellationRequested) throw;
            if (!tick) throw new StudioCommandException("context_changed", "The animation request was superseded.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            if (!disposed && generation == expected)
            { SetPlaying(false); failure = ex.Message; status = "Unavailable"; Diagnostic?.Invoke(ex.Message); }
            if (!tick) throw;
        }
        finally
        {
            if (ReferenceEquals(request, pending)) { request = null; loading = false; Changed?.Invoke(); }
        }
    }

    private async void Tick(object? sender, EventArgs args)
    {
        if (tickPending || !playing || disposed) return;
        if (lifetime.IsCancellationRequested || !isCurrent() || viewport.SceneGeneration != worldGeneration) { Stop(); return; }
        tickPending = true;
        long expected = generation + 1;
        try
        {
            double now = clock.Elapsed.TotalSeconds;
            playbackTarget += Math.Min(.1, Math.Max(0, now - lastClock)) * speed; lastClock = now;
            double target = Math.Min(rangeEnd, playbackTarget);
            await EvaluateAsync(target, true, true, lifetime.Token);
            if (!disposed && generation == expected && status == "Preview end" && replay)
            {
                ResetRun(); expected = generation + 1;
                await EvaluateAsync(rangeStart, true, false, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (!disposed && generation == expected)
            {
                if (lifetime.IsCancellationRequested) Stop();
                else { SetPlaying(false); failure = ex.Message; status = "Unavailable"; Diagnostic?.Invoke(ex.Message); Changed?.Invoke(); }
            }
        }
        finally { tickPending = false; }
    }

    private void SetPlaying(bool value)
    {
        if (playing == value) { if (!value) audio.Stop(); return; }
        playing = value;
        if (value)
        { clock.Restart(); lastClock = 0; playbackTarget = CurrentFrame?.Time ?? rangeStart; timer.Start(); }
        else { timer.Stop(); clock.Stop(); audio.Stop(); }
    }

    private async Task PrepareAudioAsync(WorldAnimationScenario scenario, CancellationToken token)
    {
        if (!audioDirty && audio.IsPrepared) return;
        await audio.PrepareAsync(Context, EntryIndex, scenario.Policy, false, token);
        token.ThrowIfCancellationRequested(); audioDirty = false;
    }

    private async Task ResumeAudioAsync()
    {
        Guid expected = ScenarioId; Guid? run = RunId; var scenario = Scenario;
        try
        {
            await PrepareAudioAsync(scenario, scenarioLifetime.Token);
            if (disposed || !playing || audio.Muted || expected != ScenarioId || run != RunId || !isCurrent()) return;
            await audio.StartOutputAsync(scenarioLifetime.Token);
            if (!disposed && playing && !audio.Muted && expected == ScenarioId && run == RunId && CurrentFrame is { } frame && isCurrent())
                audio.Update(frame, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { if (!disposed && expected == ScenarioId) { Diagnostic?.Invoke(ex.Message); Changed?.Invoke(); } }
    }

    private void ResetRun()
    {
        // Keep the last accepted frame and its identity visible while the replacement prepares.
        // Pause or cancellation must not discard that frame or leave a phantom active run.
        CancelRequest(); SetPlaying(false); player = null;
    }
    private void RetireFrame()
    {
        if (CurrentFrame != null) { LastFrame = CurrentFrame; LastRunId = RunId; LastScenarioId = ScenarioId; }
        CurrentFrame = null;
    }
    private void CancelRequest() { ++generation; request?.Cancel(); request = null; }
    private void CheckRequest(long expected, Guid scenario, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Verify();
        if (expected != generation || scenario != ScenarioId)
            throw new StudioCommandException("context_changed", "The animation request was superseded.");
    }
    private void CheckAnalysis(Guid scenario, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Verify();
        if (scenario != ScenarioId) throw new StudioCommandException("context_changed", "The animation scenario changed during analysis.");
    }
    private void RefreshPresentation()
    {
        if (attachment is not Guid id || CurrentFrame is not { } frame) return;
        if (!viewport.UpdateWorldAnimation(id, frame, followCamera, effects))
            throw new StudioCommandException("context_changed", "The world animation attachment was replaced.");
        // The world renderer and the host's screen-space effects must refresh together while paused.
        // Re-publishing the accepted frame changes presentation only, without advancing simulation or audio.
        FramePublished?.Invoke(frame);
    }
    private void ManualNavigationStarting()
    {
        if (!followCamera || disposed) return;
        followCamera = false; Changed?.Invoke();
    }
    private void AudioDiagnostic(string message)
    {
        if (disposed) return;
        Diagnostic?.Invoke(message); Changed?.Invoke();
    }
    private void Verify()
    {
        viewport.Dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        if (lifetime.IsCancellationRequested || viewport.SceneGeneration != worldGeneration || !isCurrent())
            throw new StudioCommandException("context_changed", "The source world or animation session changed.");
    }
    private static bool Finite(Vector3? value) => value is not Vector3 p || float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static StudioCommandException Invalid(string message) => new("invalid_argument", message);

    public void Dispose()
    {
        viewport.Dispatcher.VerifyAccess();
        if (disposed) return;
        Stop(); disposed = true; lifetime.Cancel(); scenarioLifetime.Cancel();
        timer.Tick -= Tick; viewport.ManualNavigationStarting -= ManualNavigationStarting;
        audio.Diagnostic -= AudioDiagnostic; audio.Dispose();
        scenarioLifetime.Dispose(); lifetime.Dispose();
    }
}
