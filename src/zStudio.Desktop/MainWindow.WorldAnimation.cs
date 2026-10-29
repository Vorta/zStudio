using System.Globalization;
using System.IO;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Sources;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Animation mode's opened binding on a map document, with the built map (<see cref="DocumentModel.Document"/>) it plays. It
    /// holds no revision: a save changes the document's revision, not its built map, so the target, its
    /// run and what shows it stay; MCP reports the document's revision.
    /// </summary>
    private sealed record WorldAnimationTarget(DocumentModel Document, AssetDocument Map, int Definition, int Entry)
    {
        /// <summary>Whether the document still shows the built map (an edit, undo, redo or reload rebuilds it).</summary>
        public bool Built => Document.Document == Map;
    }
    private WorldAnimationSession? worldAnimationSession;
    private Guid worldAnimationScenarioEpoch = Guid.NewGuid();
    /// <summary>The shown world's baseline: renewed when the world is replaced or rebuilt (another map, difficulty, LOD, an edit), never by Animation mode or the scenario.</summary>
    private Guid worldAnimationBaseline = Guid.NewGuid();
    private long worldAnimationPreparation;
    private bool worldAnimationPreparing;
    private AnimationStructurePlan? worldAnimationPlan;
    private DocumentModel? worldAnimationPlanDocument;
    private AssetDocument? worldAnimationPlanMap;
    private AnimationDuration? worldAnimationDuration;
    /// <summary>The time a running re-evaluation seeks to: a later scenario change re-evaluates to it again (P2-1).</summary>
    private (WorldAnimationSession Session, double Seconds, long Id)? worldAnimationPendingPlayhead;
    private long worldAnimationReevaluationId;
    /// <summary>Whether an edit (a draft, a scenario change) rather than a plain Stop retired the run's last frame.</summary>
    private bool worldAnimationStaleByEdit = true;
    private AnimationScenario WorldAnimationConfiguration => worldAnimationConfiguration;
    /// <summary>The binding Animation mode plays (its opened definition's chosen binding): the scenario's primary start.</summary>
    private WorldAnimationTarget? WorldAnimationTargetNow => animationMode;
    private bool HasWorldAnimationTarget => animationMode != null;

    /// <summary>A rebuild (difficulty, LOD, a source edit) stops the run; Animation mode stays open on the rebuilt map.</summary>
    private WorldAnimationTarget? CaptureWorldAnimationRebuild()
    {
        if (animationMode == null) return null;
        var saved = animationMode;
        StopWorldAnimationForEdit(); return saved;
    }

    private void RestoreWorldAnimationRebuild(WorldAnimationTarget? saved)
    {
        if (saved == null) return;
        // The map's scenario outlived the rebuild in its session (remapped after a recompile); the baseline is new.
        worldAnimationBaseline = Guid.NewGuid();
        worldAnimationScenarioEpoch = Guid.NewGuid(); worldAnimationDuration = null;
        ParachuteScenarioChanged();
        RebaseAnimationMode();
        // The rebuilt scene (difficulty, LOD) measures Auto again, in the background.
        RefreshWorldAnimationRun();
    }

    private AnimationStructurePlan GetWorldAnimationStructurePlan()
    {
        if (shownDocument is not { Document: SourceSceneSnapshot source } document || source.Animations == null)
            throw new StudioCommandException("not_ready", "Open a source map with animations first.");
        if (worldAnimationPlan == null || worldAnimationPlanDocument != document || worldAnimationPlanMap != source)
        {
            worldAnimationPlan = AnimationStructurePlan.Create(source.Animations, source.AnimationProvenance);
            worldAnimationPlanDocument = document; worldAnimationPlanMap = source;
        }
        return worldAnimationPlan;
    }

    private void RequireWorldAnimationTarget(DocumentModel document, int definition, int entry)
    {
        if (document != shownDocument || document.Document is not SourceSceneSnapshot source || scene?.Mission == null)
            throw new StudioCommandException("not_ready", "Open a source world first.");
        if (!source.AnimationDefinitions.Definitions.Any(d => d.Index == definition && d.Entries.Contains(entry)))
            throw new StudioCommandException("stale_record", "Choose an exact binding of the animation definition.");
        if (WorldAnimationTargetNow is { } active && (active.Document != document || !active.Built))
            throw new StudioCommandException("stale_record", "The animation source changed; open its binding again.");
    }

    private async Task<AnimationPreviewContext> PrepareWorldAnimationContextAsync(DocumentModel document, CancellationToken token)
    {
        if (document != shownDocument || document.Document is not SourceSceneSnapshot source || scene?.Mission is not { } baseline)
            throw new StudioCommandException("not_ready", "The initialized world must be visible before preparing an animation.");
        var viewport = scene; int generation = viewport.SceneGeneration;
        var context = await Task.Run(() => AnimationPreviewContext.FromSourceBaseline(source, baseline, token), token);
        token.ThrowIfCancellationRequested();
        // The built map, not the revision: a save meanwhile leaves the context as it is.
        if (document != shownDocument || document.Document != source || scene != viewport || generation != viewport.SceneGeneration)
            throw new StudioCommandException("context_changed", "The world changed while preparing the animation.");
        return context;
    }

    /// <summary>
    /// The shown map's animation context for Auto's measurement and the run's preparation: one preparation of the built map on the
    /// shown scene serves both (whichever asks first prepares it, the other awaits the same work; design §13: Auto is measured as a
    /// definition opens, so the first Play does not prepare it again). Another map, scene or baseline prepares a new one; leaving
    /// the mode or replacing the world releases it.
    /// </summary>
    private (DocumentModel Document, AssetDocument Map, SceneViewport Viewport, int Generation, MissionSceneContext Baseline, Task<AnimationPreviewContext> Task, CancellationTokenSource Cancellation)? worldAnimationContextShare;

    private async Task<AnimationPreviewContext> SharedWorldAnimationContextAsync(DocumentModel document, CancellationToken token)
    {
        if (document != shownDocument || document.Document is not SourceSceneSnapshot source || scene?.Mission is not { } baseline)
            throw new StudioCommandException("not_ready", "The initialized world must be visible before preparing an animation.");
        var viewport = scene; int generation = viewport.SceneGeneration;
        if (worldAnimationContextShare is not { } share || share.Document != document || share.Map != source || share.Viewport != viewport
            || share.Generation != generation || share.Baseline != baseline || share.Task.IsCanceled || share.Task.IsFaulted)
        {
            ReleaseWorldAnimationContext();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, document.Lifetime.Token);
            var preparing = Task.Run(() => AnimationPreviewContext.FromSourceBaseline(source, baseline, cancellation.Token), cancellation.Token);
            // The share outlives its first caller: it ends (and observes a failure nobody awaits) on its own.
            _ = preparing.ContinueWith(done => { _ = done.Exception; cancellation.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            share = (document, source, viewport, generation, baseline, preparing, cancellation);
            worldAnimationContextShare = share;
        }
        AnimationPreviewContext context;
        try { context = await share.Task.WaitAsync(token); }
        // A released share (another scene or map) is a changed world, not the caller's cancellation.
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new StudioCommandException("context_changed", "The world changed while preparing the animation."); }
        token.ThrowIfCancellationRequested();
        // The built map, not the revision: a save meanwhile leaves the context as it is.
        if (document != shownDocument || document.Document != source || scene != viewport || generation != viewport.SceneGeneration)
            throw new StudioCommandException("context_changed", "The world changed while preparing the animation.");
        return context;
    }

    /// <summary>Releases the shared context (Exit, another map, a replaced world): a preparation still running stops.</summary>
    private void ReleaseWorldAnimationContext()
    {
        if (worldAnimationContextShare is { } share && !share.Task.IsCompleted)
            try { share.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        worldAnimationContextShare = null;
    }

    /// <summary>An analysis player: a scenario of its primary alone analyzes <paramref name="entry"/>; scheduled starts keep their own primary.</summary>
    private static AnimationPlayer CreateWorldAnalysisPlayer(AnimationPreviewContext context, int entry, AnimationScenario configuration, CancellationToken token, int lod = 0)
    {
        if (configuration.Starts.Count <= 1 && configuration.Primary != entry) configuration = configuration.WithPrimary(entry, keepPrevious: false);
        return WorldAnimationSession.Player(context, entry, configuration, lod, token);
    }

    private async Task<WorldAnimationSession> EnsureWorldAnimationSessionAsync(CancellationToken token)
    {
        if (worldAnimationSession is { IsDisposed: false } existing) return existing;
        if (animationMode is not { Entry: > 0 } target)
            throw new StudioCommandException("not_ready", animationMode == null ? "Open an animation in Animation mode first." : "The opened definition has no binding in this map.");
        RequireWorldAnimationTarget(target.Document, target.Definition, target.Entry);
        var viewport = scene!; int generation = viewport.SceneGeneration; long request = ++worldAnimationPreparation;
        Guid scenario = worldAnimationScenarioEpoch;
        worldAnimationPreparing = true; RefreshWorldAnimationRun();
        try
        {
            // The context Auto's measurement prepared as the definition opened (or is preparing): one preparation for both.
            var context = await SharedWorldAnimationContextAsync(target.Document, token);
            RequireNoDrafts();
            if (request != worldAnimationPreparation || scenario != worldAnimationScenarioEpoch || animationMode != target)
                throw new OperationCanceledException("The animation request was replaced.", token);
            var session = new WorldAnimationSession(context, target.Entry, viewport, ViewModel.Resolver!,
                () => shownDocument == target.Document && target.Built && scene == viewport
                    && viewport.SceneGeneration == generation && animationMode == target,
                target.Document.Lifetime.Token, Math.Max(0, LodCombo.SelectedIndex));
            session.SetScenario(worldAnimationConfiguration);
            session.SetRange(worldAnimationRangeStart, worldAnimationRangeEnd);
            session.Speed = worldAnimationSpeed; session.Volume = worldAnimationVolume; session.Muted = worldAnimationMuted;
            session.Replay = worldAnimationReplay; session.FollowCamera = worldAnimationFollowCamera; session.Effects = worldAnimationEffects;
            worldAnimationSession = session;
            session.Changed += () =>
            {
                if (worldAnimationSession != session) return;
                bool previouslyAttached = sourceAnimationPreview;
                sourceAnimationPreview = session.IsAttached || session.IsLoading;
                var actual = session.State;
                worldAnimationFollowCamera = actual.FollowCamera; worldAnimationMuted = actual.Muted; worldAnimationEffects = actual.Effects;
                worldAnimationRangeStart = actual.RangeStart; worldAnimationRangeEnd = actual.RangeEnd; worldAnimationSpeed = actual.Speed;
                worldAnimationReplay = actual.Replay; worldAnimationVolume = actual.Volume;
                if (!session.IsAttached) ApplyWorldAnimationScreenEffects(null);
                RefreshWorldAnimationRun(true);
                if (sourceAnimationPreview != previouslyAttached) { UpdateSourceAnimationControls(); ArrangeWorkspace(); }
            };
            session.FramePublished += frame => { if (worldAnimationSession == session) ApplyWorldAnimationScreenEffects(frame); };
            session.Diagnostic += note => { if (worldAnimationSession == session) TransportNote(note); };
            // Auto was measured as the definition opened; one still missing (the world was not ready then, or it changed meanwhile) is measured now, in the background.
            if (worldAnimationDuration == null && worldAnimationMeasuring == null) _ = MeasureWorldAnimationRangeAsync();
            return session;
        }
        finally { if (request == worldAnimationPreparation) { worldAnimationPreparing = false; RefreshWorldAnimationRun(); } }
    }

    private async Task WorldAnimationTransportAsync(string action, double seconds = 0, CancellationToken token = default)
    {
        if (action is "play" or "seek" or "stop" or "start" or "previous" or "next") animationTransportNote = null;
        if (action == "exit") { await ExitAnimationModeAsync(token); return; }
        if (action == "stop") { worldAnimationPendingPlayhead = null; StopWorldAnimationForEdit(); worldAnimationStaleByEdit = false; RefreshWorldAnimationRun(); return; }
        if (action == "pause") { ++worldAnimationPreparation; worldAnimationPreparing = false; worldAnimationSession?.Pause(); RefreshWorldAnimationRun(); return; }
        // The range's Auto (measured again when needed) and Extend +5 s.
        if (action == "auto") { token.ThrowIfCancellationRequested(); await WorldAnimationAutoRangeAsync(); return; }
        if (action == "extend") { token.ThrowIfCancellationRequested(); await WorldAnimationExtendAsync(token); return; }
        RequireNoDrafts();
        if (scene?.IsFlyActive == true || scene?.IsPickupDragging == true || ZoneToolEngaged || scene?.TerrainBrushActive == true || scene?.HasTransformDraft == true || scene?.AiPathEditingNetwork != null)
            throw new StudioCommandException("busy", "Finish navigation or zone editing before playing an animation.");
        // The range start (Home) is a seek; previous and next step one 1/60 s tick.
        if (action == "start") { action = "seek"; seconds = worldAnimationSession?.RangeStart ?? worldAnimationRangeStart; }
        // The user moved the playhead: a pending re-evaluation's time is no longer it.
        worldAnimationPendingPlayhead = null;
        animationFailure = null;
        var session = await EnsureWorldAnimationSessionAsync(token);
        await session.TransportAsync(action, seconds, token);
    }

    private void StopWorldAnimationForEdit()
    {
        // The kept last frame is from before an edit (a draft, a scenario change) unless the transport's Stop says otherwise.
        worldAnimationStaleByEdit = true;
        ++worldAnimationPreparation; worldAnimationPreparing = false; worldAnimationSession?.Stop();
        sourceAnimationPreview = false; ApplyWorldAnimationScreenEffects(null);
        RefreshWorldAnimationRun(); UpdateSourceAnimationControls();
    }

    /// <summary>Ends the run: the session goes and the static map shows; the scenario and Animation mode stay.</summary>
    private void DetachWorldAnimationRun()
    {
        ++worldAnimationPreparation; worldAnimationPreparing = false;
        CancelWorldAnimationMeasure();
        var session = worldAnimationSession; worldAnimationSession = null; session?.Dispose();
        sourceAnimationPreview = false;
        ApplyWorldAnimationScreenEffects(null);
        RefreshWorldAnimationRun(); RefreshRowEyes(); UpdateSourceAnimationControls();
    }

    /// <summary>The shown world is being replaced (a rebuild or another map): its run ends; Animation mode follows the map (<see cref="RebaseAnimationMode"/>).</summary>
    private void ClearWorldAnimationTarget()
    {
        DetachWorldAnimationRun(); ReleaseWorldAnimationContext(); sourceAnimationEntry = null; worldAnimationPlan = null; worldAnimationPlanDocument = null; worldAnimationPlanMap = null; worldAnimationBaseline = Guid.NewGuid();
        worldAnimationScenarioEpoch = Guid.NewGuid(); worldAnimationDuration = null;
        animationTimingToggled.Clear(); animationTimingToggles++; animationTimingIndex = null;
        ParachuteScenarioChanged();
        RefreshWorldAnimationRun(); ArrangeWorkspace();
    }

    /// <summary>
    /// Changes the map's scenario (every view and MCP change comes here). It stops playback and silently re-evaluates the run up
    /// to the playhead, as a seek does, so the change shows at once (owner decision); <see cref="worldAnimationReevaluation"/> is that work.
    /// </summary>
    /// <param name="excluded">The call paths the change excluded; with <paramref name="excludeAll"/> every one (Exclude all). The marked included
    /// paths below them go too.</param>
    private void SetWorldAnimationScenario(AnimationScenario configuration, string reason = "edit", IReadOnlyCollection<string>? excluded = null, bool excludeAll = false)
    {
        RequireNoDrafts();
        if (WorldAnimationTargetNow is { Entry: > 0 } target)
        {
            if (configuration.Primary == null) configuration = configuration.WithPrimary(target.Entry, keepPrevious: false);
            if (configuration.Primary != target.Entry)
                throw new StudioCommandException("invalid_argument", "Keep the primary start on the opened binding.");
            try { _ = configuration.Schedule(target.Document.Document.Animations!.Entries.Count); }
            catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        }
        // The playhead the change re-evaluates up to: a re-evaluation still running keeps its time for the next change (a second
        // switch click during the first's seek), else the shown run's.
        var previous = worldAnimationSession;
        double? playhead = worldAnimationPendingPlayhead is { } pending && pending.Session == previous && previous is { IsDisposed: false } ? pending.Seconds
            : previous is { IsDisposed: false, IsAttached: true, CurrentFrame: { } frame } ? frame.Time : null;
        StopWorldAnimationForEdit(); worldAnimationSession?.SetScenario(configuration);
        if (excludeAll || excluded != null) WorldScenarios().Exclude(configuration, excludeAll ? null : excluded, reason);
        else WorldScenarios().Set(configuration, reason);
        worldAnimationScenarioEpoch = Guid.NewGuid(); worldAnimationDuration = null;
        // The changed scenario's Auto range is measured again, in the background (the older measurement stops).
        MeasureWorldAnimationRangeWhenDue();
        ParachuteScenarioChanged();
        foreach (int definition in shownAnimationPaths.ToArray()) scene?.ClearAnimationPaths(definition.ToString(CultureInfo.InvariantCulture));
        shownAnimationPaths.Clear(); RefreshPathsChip(); RefreshWorldAnimationRun(); RefreshRowEyes();
        if (playhead is double time && previous is { } session && session == worldAnimationSession)
        {
            long id = ++worldAnimationReevaluationId;
            worldAnimationPendingPlayhead = (session, time, id);
            worldAnimationReevaluation = ReevaluateWorldAnimationAsync(session, time, id);
        }
        else worldAnimationPendingPlayhead = null;
    }

    /// <summary>
    /// Runs the changed scenario from 0 to <paramref name="seconds"/> without audio, playback or camera movement. A later
    /// change supersedes it (and re-evaluates to the same time); a successful one clears the transport's failure line.
    /// </summary>
    private async Task ReevaluateWorldAnimationAsync(WorldAnimationSession session, double seconds, long id)
    {
        try
        {
            if (session != worldAnimationSession || session.IsDisposed) return;
            await session.TransportAsync("seek", seconds, shutdownToken);
            if (id == worldAnimationReevaluationId && session == worldAnimationSession && animationFailure != null) { animationFailure = null; RefreshWorldAnimationRun(true); }
        }
        catch (OperationCanceledException) { }
        catch (StudioCommandException ex) when (ex.Code is "context_changed" or "superseded" or "stale_record" or "stale_preview") { }
        catch (InvalidOperationException) when (session.IsDisposed || session != worldAnimationSession) { }
        finally { if (worldAnimationPendingPlayhead?.Id == id) worldAnimationPendingPlayhead = null; }
    }

    private int ValidateWorldAnimationCallPath(int rootEntry, string path)
    {
        if (path.Length > AnimationExecutionPolicy.MaximumPathCharacters) throw new StudioCommandException("invalid_argument", "The dependency path is too long.");
        var plan = GetWorldAnimationStructurePlan(); int current = rootEntry; string parent = "";
        string remainder = path;
        if (path.StartsWith("/@", StringComparison.Ordinal))
        {
            string prefix = path.Split('/')[1];
            var activation = WorldAnimationActivations().FirstOrDefault(s => s.Path == "/" + prefix)
                ?? throw new StudioCommandException("stale_record", "The scheduled activation is unavailable.");
            current = activation.Entry; parent = activation.Path; remainder = path[parent.Length..];
        }
        foreach (string segment in remainder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string expected = parent + "/" + segment; AnimationStructureCall? found = null;
            for (int offset = 0; ;)
            {
                var page = plan.Calls(current, parent, offset); found = page.Rows.FirstOrDefault(c => c.Path == expected);
                if (found != null || page.NextOffset == null) break;
                offset = page.NextOffset.Value;
            }
            if (found == null) throw new StudioCommandException("stale_record", "The call path is not in this source revision.");
            current = found.TargetEntry ?? -1; parent = expected;
            if (current < 0 && parent != path) throw new StudioCommandException("stale_record", "An unavailable child cannot have descendants.");
        }
        if (parent != path) throw new StudioCommandException("invalid_argument", "Use a canonical call path from the structural plan.");
        return current;
    }
}
