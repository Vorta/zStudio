using System.IO;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private IReadOnlyList<AnimationScheduledStart> WorldAnimationActivations()
    {
        var target = WorldAnimationTargetNow ?? throw new StudioCommandException("not_ready", "Choose an animation binding.");
        return worldAnimationConfiguration.Starts.Count == 0 ? [new(Guid.Empty, target.Entry, 0)] : worldAnimationConfiguration.Starts;
    }

    private bool IsWorldAnimationActivationPath(string path) => path.Length == 0 ||
        worldAnimationConfiguration.Starts.Any(s => s.Id != Guid.Empty && s.Path == path);

    private void SetWorldAnimationActivations(IReadOnlyList<AnimationScheduledStart> starts)
    {
        var target = WorldAnimationTargetNow ?? throw new StudioCommandException("not_ready", "Choose an animation binding.");
        _ = new AnimationSchedule(starts, target.Document.Document.Animations!.Entries.Count);
        if (starts.Any(s => s.Entry == 0))
            throw new StudioCommandException("invalid_argument", "Choose an animation binding; entry 0 is the compiler's empty placeholder.");
        var removed = WorldAnimationActivations().Where(old => old.Id != Guid.Empty && !starts.Any(s => s.Id == old.Id && s.Entry == old.Entry)).Select(s => s.Path).ToArray();
        bool Keep(string path) => !removed.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));
        var scenario = worldAnimationConfiguration;
        SetWorldAnimationScenario(scenario with
        {
            Starts = starts.ToArray(), IncludedCalls = scenario.IncludedCalls.Where(Keep).ToArray(), Conditions = scenario.Conditions.Where(c => Keep(c.InstancePath)).ToArray(),
        });
    }

    private async Task FitWorldAnimationScenario()
    {
        double end = Math.Min(3600, WorldAnimationActivations().Max(s => s.Seconds) + 30);
        await WorldAnimationOptionAsync("playbackRange", new[] { 0d, end });
        TimingView.SetVisibleRange(0, end);
        RefreshWorldAnimationRun();
    }

    // ---------------------------------------------------------------- the Auto range (design §13 Range)

    /// <summary>The playback range follows Auto (the measured run) until it is lengthened or edited (Custom); opening a definition returns to Auto.</summary>
    private bool worldAnimationRangeAuto = true;
    /// <summary>Extend +5 s lengthened the Custom range: playback stopping at its end hints Extend again.</summary>
    private bool worldAnimationRangeExtended;
    private CancellationTokenSource? worldAnimationMeasure;
    private long worldAnimationMeasureId;
    /// <summary>The Auto measurement running in the background, if any.</summary>
    private Task<AnimationDuration?>? worldAnimationMeasuring;
    /// <summary>What a measurement is for: the scenario's epoch, the opened binding on its built map, the world's baseline and the scene showing it.</summary>
    private sealed record WorldAnimationMeasureKey(Guid Epoch, WorldAnimationTarget Target, Guid Baseline, SceneViewport Scene, int Generation);
    /// <summary>What the latest measurement was started for: each is measured once, and a result is kept only while it is still the shown one.</summary>
    private WorldAnimationMeasureKey? worldAnimationMeasuredFor;
    /// <summary>What Auto would measure now: the opened binding on the shown, built map (null when there is nothing to measure).</summary>
    private WorldAnimationMeasureKey? WorldAnimationMeasureNow() =>
        animationMode is { Entry: > 0 } target && target.Document == shownDocument && target.Built && scene is { Mission: not null } viewport
            ? new(worldAnimationScenarioEpoch, target, worldAnimationBaseline, viewport, viewport.SceneGeneration) : null;
    /// <summary>The range Auto plays until its first measurement arrives (the transport shows no end meanwhile: "of 0–… s").</summary>
    private const double WorldAnimationProvisionalEnd = 30;

    /// <summary>
    /// Opening a definition (entering, another one, Back, Forward, the binding picker) starts on Auto, with no end until its
    /// measurement arrives (a Custom range and the previous definition's Auto end belonged to the one before).
    /// </summary>
    private void ResetWorldAnimationRange()
    {
        worldAnimationRangeAuto = true; worldAnimationRangeExtended = false;
        worldAnimationRangeStart = 0; worldAnimationRangeEnd = WorldAnimationProvisionalEnd;
    }
    private void CancelWorldAnimationMeasure()
    {
        ++worldAnimationMeasureId; worldAnimationMeasure?.Cancel(); worldAnimationMeasure = null; worldAnimationMeasuring = null;
    }
    /// <summary>Auto is waiting for its measurement: the transport shows no end, and Extend +5 s waits for one.</summary>
    private bool WorldAnimationRangePending => worldAnimationRangeAuto && worldAnimationDuration == null && worldAnimationMeasuring != null;

    /// <summary>
    /// Starts Auto's measurement when Animation mode shows a bound definition whose scenario is not measured on the shown world
    /// (design §13: as the definition or binding opens, after a scenario change, a rebuild or another difficulty or LOD), in the
    /// background: it never plays, seeks, moves the view or evaluates paths (design §7). Each scenario, binding, world and scene is
    /// measured once; a newer one supersedes a measurement still running.
    /// </summary>
    private void MeasureWorldAnimationRangeWhenDue()
    {
        if (worldAnimationDuration != null || WorldAnimationMeasureNow() == null) return;
        // The scenario first: a remap of its identities after a rebuild renews the epoch.
        _ = worldAnimationConfiguration;
        if (WorldAnimationMeasureNow() is not { } now || now == worldAnimationMeasuredFor) return;
        _ = MeasureWorldAnimationRangeAsync();
    }

    /// <summary>
    /// Measures the scenario's Auto range in the background, on the prepared run's frozen context, else on the context the run's
    /// preparation shares (<see cref="SharedWorldAnimationContextAsync"/>); it never touches the run. A newer measurement, a scenario
    /// change, a rebuild, another definition or Exit supersedes it; Auto, when it is chosen, follows the result.
    /// </summary>
    private Task<AnimationDuration?> MeasureWorldAnimationRangeAsync()
    {
        CancelWorldAnimationMeasure();
        if (animationMode is not { Entry: > 0 } target) return Task.FromResult<AnimationDuration?>(null);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        worldAnimationMeasure = cancellation; long id = worldAnimationMeasureId;
        // The scenario before its epoch: reading it may remap it (a rebuild), which renews the epoch.
        var configuration = worldAnimationConfiguration; int lod = Math.Max(0, LodCombo.SelectedIndex);
        var key = WorldAnimationMeasureNow(); worldAnimationMeasuredFor = key;
        var frozen = worldAnimationSession is { IsCurrent: true } session ? session.Context : null;
        var task = MeasureAsync();
        if (!task.IsCompleted) worldAnimationMeasuring = task;
        return task;

        async Task<AnimationDuration?> MeasureAsync()
        {
            var token = cancellation.Token;
            try
            {
                var context = frozen ?? await SharedWorldAnimationContextAsync(target.Document, token);
                var measured = await Task.Run(() => CreateWorldAnalysisPlayer(context.Snapshot(), target.Entry, configuration, token, lod).MeasureDuration(token), token);
                // The target holds no revision: a save meanwhile keeps the measurement; a scenario change, a rebuild, another binding
                // or another scene does not (the refresh below measures what shows now).
                if (id != worldAnimationMeasureId || key == null || WorldAnimationMeasureNow() != key) return null;
                worldAnimationDuration = measured;
                if (worldAnimationRangeAuto) ApplyWorldAnimationAutoRange();
                return measured;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) when (ex is StudioCommandException or InvalidDataException or IOException or InvalidOperationException)
            {
                // A world that changed or is not shown yet is measured again once it shows (a new epoch, baseline or scene).
                if (id == worldAnimationMeasureId && ex is not StudioCommandException { Code: "context_changed" or "superseded" or "not_ready" }) TransportNote("Auto range unavailable: " + ex.Message);
                return null;
            }
            finally
            {
                cancellation.Dispose();
                // The refresh shows the result (or measures again when the scenario changed meanwhile).
                if (id == worldAnimationMeasureId) { worldAnimationMeasure = null; worldAnimationMeasuring = null; RefreshWorldAnimationRun(); }
            }
        }
    }

    /// <summary>Auto's range on the run: 0 s to the measured end (playback goes on to it).</summary>
    private void ApplyWorldAnimationAutoRange()
    {
        if (worldAnimationDuration is not { } measured) return;
        double end = Math.Clamp(measured.Seconds, AnimationSeconds.Tick, AnimationSeconds.Maximum);
        worldAnimationRangeStart = 0; worldAnimationRangeEnd = Math.Ceiling(end * 60 - 1e-7) / 60;
        if (worldAnimationSession is { IsDisposed: false, IsCurrent: true } session) session.SetRange(0, end, keepPlaying: true);
    }

    /// <summary>
    /// Auto (the range flyout's Auto, MCP animation_transport auto): the range follows the measured run again; it measures again
    /// when no measurement is kept (or the kept one timed out). Returns the Auto result.
    /// </summary>
    private async Task<AnimationDuration?> WorldAnimationAutoRangeAsync()
    {
        if (animationMode is not { Entry: > 0 }) throw new StudioCommandException("not_ready", animationMode == null ? "Open an animation in Animation mode first." : "The opened definition has no binding in this map.");
        RequireNoDrafts(shownDocument);
        worldAnimationRangeAuto = true; worldAnimationRangeExtended = false; animationFailure = null;
        if (worldAnimationDuration is { TimedOut: false } && worldAnimationMeasuring == null) ApplyWorldAnimationAutoRange();
        else await (worldAnimationMeasuring ?? MeasureWorldAnimationRangeAsync());
        RefreshWorldAnimationRun();
        return worldAnimationDuration;
    }

    /// <summary>
    /// Extend +5 s (the range flyout, the ruler's "+5 s", the transport's hint, MCP animation_transport extend): the range's end
    /// moves 5 s later and the range is Custom. With <paramref name="play"/> (the hint's, MCP play) playback goes on from the
    /// playhead, as Play; otherwise playing goes on and a resting playhead stays. An Auto end still measuring is waited for.
    /// </summary>
    private async Task WorldAnimationExtendAsync(bool play = false, CancellationToken token = default)
    {
        if (animationMode is not { Entry: > 0 }) throw new StudioCommandException("not_ready", animationMode == null ? "Open an animation in Animation mode first." : "The opened definition has no binding in this map.");
        RequireNoDrafts(shownDocument);
        if (WorldAnimationRangePending && worldAnimationMeasuring is { } measuring) { await measuring.WaitAsync(token); RequireNoDrafts(shownDocument); }
        double start = worldAnimationSession?.RangeStart ?? worldAnimationRangeStart, end = worldAnimationSession?.RangeEnd ?? worldAnimationRangeEnd;
        if (end >= AnimationSeconds.Maximum - 1e-9) throw new StudioCommandException("invalid_argument", "The range already ends at 3,600 s, the preview's limit.");
        double next = Math.Min(AnimationSeconds.Maximum, end + AnimationRangeText.ExtendSeconds);
        worldAnimationSession?.SetRange(start, next, keepPlaying: true);
        worldAnimationRangeStart = start; worldAnimationRangeEnd = Math.Ceiling(next * 60 - 1e-7) / 60;
        worldAnimationRangeAuto = false; worldAnimationRangeExtended = true; animationFailure = null;
        RefreshWorldAnimationRun();
        // The hint's Extend plays on from where playback stopped.
        if (play && worldAnimationSession?.IsPlaying != true) await WorldAnimationTransportAsync("play", 0, token);
    }

    /// <summary>The range as the transport, Timing's ruler and MCP report it: Auto or Custom, why it ends there, whether Auto still measures it, and the hint at a cut end.</summary>
    private (string Text, bool CanAuto, bool CanExtend, string? Hint, bool Pending) WorldAnimationRangeState()
    {
        var state = worldAnimationSession?.State;
        double start = state?.RangeStart ?? worldAnimationRangeStart, end = state?.RangeEnd ?? worldAnimationRangeEnd;
        bool measuring = worldAnimationMeasuring != null, bound = animationMode is { Entry: > 0 }, pending = WorldAnimationRangePending;
        string text = AnimationRangeText.Describe(worldAnimationRangeAuto, start, end, worldAnimationDuration, measuring);
        // Playback rests at the range's end while the run goes on.
        string? hint = worldAnimationSession is { IsDisposed: false, AtCutEnd: true }
            ? AnimationRangeText.Hint(worldAnimationRangeAuto, worldAnimationRangeExtended, worldAnimationDuration) : null;
        return (text, bound && AnimationRangeText.CanAuto(worldAnimationRangeAuto, worldAnimationDuration, measuring), bound && !pending && end < AnimationSeconds.Maximum - 1e-9, hint, pending);
    }
}
