using System.IO;
using zStudio.Automation;
using zStudio.Core.Animation;

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

    /// <summary>Opening a definition (entering, another one, Back, Forward, the binding picker) starts on Auto.</summary>
    private void ResetWorldAnimationRange() { worldAnimationRangeAuto = true; worldAnimationRangeExtended = false; }
    private void CancelWorldAnimationMeasure()
    {
        ++worldAnimationMeasureId; worldAnimationMeasure?.Cancel(); worldAnimationMeasure = null; worldAnimationMeasuring = null;
    }

    /// <summary>
    /// Measures the scenario's Auto range in the background, on the prepared run's frozen context (a new context when no run is
    /// prepared, or the shown world changed under it); it never touches the run. A newer measurement, a scenario change, a
    /// rebuild, another definition or Exit supersedes it; Auto, when it is chosen, follows the result.
    /// </summary>
    private Task<AnimationDuration?> MeasureWorldAnimationRangeAsync()
    {
        CancelWorldAnimationMeasure();
        if (animationMode is not { Entry: > 0 } target) return Task.FromResult<AnimationDuration?>(null);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        worldAnimationMeasure = cancellation; long id = worldAnimationMeasureId;
        Guid epoch = worldAnimationScenarioEpoch; var configuration = worldAnimationConfiguration; int lod = Math.Max(0, LodCombo.SelectedIndex);
        var frozen = worldAnimationSession is { IsCurrent: true } session ? session.Context : null;
        var task = MeasureAsync();
        if (!task.IsCompleted) worldAnimationMeasuring = task;
        return task;

        async Task<AnimationDuration?> MeasureAsync()
        {
            var token = cancellation.Token;
            try
            {
                var context = frozen ?? await PrepareWorldAnimationContextAsync(target.Document, token);
                var measured = await Task.Run(() => CreateWorldAnalysisPlayer(context.Snapshot(), target.Entry, configuration, token, lod).MeasureDuration(token), token);
                // The target holds no revision: a save meanwhile keeps the measurement; a scenario change, a rebuild or another binding does not.
                if (id != worldAnimationMeasureId || epoch != worldAnimationScenarioEpoch || target != animationMode) return null;
                worldAnimationDuration = measured;
                if (worldAnimationRangeAuto) ApplyWorldAnimationAutoRange();
                return measured;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) when (ex is StudioCommandException or InvalidDataException or IOException or InvalidOperationException)
            {
                if (id == worldAnimationMeasureId && ex is not StudioCommandException { Code: "context_changed" or "superseded" }) TransportNote("Auto range unavailable: " + ex.Message);
                return null;
            }
            finally
            {
                cancellation.Dispose();
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

    /// <summary>Extend +5 s (the range flyout, the ruler's "+5 s", the transport's hint, MCP animation_transport extend): the range's end moves 5 s later and the range is Custom.</summary>
    private void WorldAnimationExtend()
    {
        if (animationMode is not { Entry: > 0 }) throw new StudioCommandException("not_ready", animationMode == null ? "Open an animation in Animation mode first." : "The opened definition has no binding in this map.");
        RequireNoDrafts(shownDocument);
        double start = worldAnimationSession?.RangeStart ?? worldAnimationRangeStart, end = worldAnimationSession?.RangeEnd ?? worldAnimationRangeEnd;
        if (end >= AnimationSeconds.Maximum - 1e-9) throw new StudioCommandException("invalid_argument", "The range already ends at 3,600 s, the preview's limit.");
        double next = Math.Min(AnimationSeconds.Maximum, end + AnimationRangeText.ExtendSeconds);
        worldAnimationSession?.SetRange(start, next, keepPlaying: true);
        worldAnimationRangeStart = start; worldAnimationRangeEnd = Math.Ceiling(next * 60 - 1e-7) / 60;
        worldAnimationRangeAuto = false; worldAnimationRangeExtended = true; animationFailure = null;
        RefreshWorldAnimationRun();
    }

    /// <summary>The range as the transport, Timing's ruler and MCP report it: Auto or Custom, why it ends there, and the hint at a cut end.</summary>
    private (string Text, bool CanAuto, bool CanExtend, string? Hint) WorldAnimationRangeState()
    {
        var state = worldAnimationSession?.State;
        double start = state?.RangeStart ?? worldAnimationRangeStart, end = state?.RangeEnd ?? worldAnimationRangeEnd;
        bool measuring = worldAnimationMeasuring != null, bound = animationMode is { Entry: > 0 };
        string text = AnimationRangeText.Describe(worldAnimationRangeAuto, start, end, worldAnimationDuration, measuring, "measured when the run is first prepared (Play or a seek)");
        // Playback rests at the range's end while the run goes on.
        string? hint = worldAnimationSession is { IsDisposed: false, AtCutEnd: true }
            ? AnimationRangeText.Hint(worldAnimationRangeAuto, worldAnimationRangeExtended, worldAnimationDuration) : null;
        return (text, bound && AnimationRangeText.CanAuto(worldAnimationRangeAuto, worldAnimationDuration, measuring), bound && end < AnimationSeconds.Maximum - 1e-9, hint);
    }
}
