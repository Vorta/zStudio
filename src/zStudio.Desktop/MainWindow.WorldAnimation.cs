using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private sealed record WorldAnimationTarget(DocumentModel Document, long Revision, int Definition, int Entry);
    private WorldAnimationTarget? worldAnimationCandidate, worldAnimationPinned;
    private WorldAnimationSession? worldAnimationSession;
    private WorldAnimationScenario worldAnimationConfiguration = new();
    private Guid worldAnimationScenarioEpoch = Guid.NewGuid();
    private long worldAnimationPreparation;
    private bool worldAnimationPreparing;
    private AnimationStructurePlan? worldAnimationPlan;
    private DocumentModel? worldAnimationPlanDocument;
    private long worldAnimationPlanRevision;
    private WorldAnimationTimeline? worldTimeline;
    private AnimationDuration? worldAnimationDuration;
    private WorldAnimationScenario WorldAnimationConfiguration => worldAnimationConfiguration;
    private WorldAnimationTarget? WorldAnimationTargetNow => worldAnimationPinned ?? worldAnimationCandidate;
    private bool HasWorldAnimationTarget => WorldAnimationTargetNow != null;
    private sealed record WorldAnimationRebuild(WorldAnimationTarget? Candidate, WorldAnimationTarget? Pinned, WorldAnimationScenario Scenario);

    private WorldAnimationRebuild? CaptureWorldAnimationRebuild()
    {
        if (!HasWorldAnimationTarget) return null;
        var saved = new WorldAnimationRebuild(worldAnimationCandidate, worldAnimationPinned, worldAnimationConfiguration);
        StopWorldAnimationForEdit(); return saved;
    }

    private void RestoreWorldAnimationRebuild(WorldAnimationRebuild? saved)
    {
        if (saved == null) return;
        var target = saved.Pinned ?? saved.Candidate;
        if (target == null || shownDocument != target.Document || target.Document.Revision != target.Revision || scene?.Mission == null) return;
        worldAnimationSession?.Dispose(); worldAnimationSession = null;
        worldAnimationCandidate = saved.Candidate; worldAnimationPinned = saved.Pinned;
        sourceAnimationEntry = saved.Candidate?.Entry is >= 0 ? saved.Candidate.Entry : null;
        worldAnimationConfiguration = saved.Scenario; worldAnimationScenarioEpoch = Guid.NewGuid(); worldAnimationDuration = null;
        RefreshWorldAnimationTimeline(); RefreshRowEyes(); ArrangeWorkspace();
    }

    private AnimationStructurePlan GetWorldAnimationStructurePlan()
    {
        if (shownDocument is not { Document: SourceSceneSnapshot source } document || source.Animations == null)
            throw new StudioCommandException("not_ready", "Open a source map with animations first.");
        if (worldAnimationPlan == null || worldAnimationPlanDocument != document || worldAnimationPlanRevision != document.Revision)
        {
            worldAnimationPlan = AnimationStructurePlan.Create(source.Animations, source.AnimationProvenance);
            worldAnimationPlanDocument = document; worldAnimationPlanRevision = document.Revision;
        }
        return worldAnimationPlan;
    }

    private void SetWorldAnimationCandidate(DocumentModel document, int definition, int entry)
    {
        if (document != shownDocument || document.Document is not SourceSceneSnapshot source) return;
        var found = source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == definition);
        if (found == null || entry >= 0 && !found.Entries.Contains(entry)) return;
        var candidate = new WorldAnimationTarget(document, document.Revision, definition, entry);
        if (worldAnimationPinned == null && worldAnimationCandidate != candidate) ResetWorldAnimationConfiguration();
        worldAnimationCandidate = candidate;
        _ = GetWorldAnimationStructurePlan();
        RefreshWorldAnimationTimeline(); RefreshRowEyes();
        SectionCard().SetAnimationSetup(WorldAnimationScenarioPanel());
        UpdateParachutePanel();
        ArrangeWorkspace();
    }

    private void ClearWorldAnimationCandidate()
    {
        if (worldAnimationCandidate == null) return;
        worldAnimationCandidate = null;
        // Clearing inspection does not cancel independent analysis already running for its captured scenario.
        RefreshWorldAnimationTimeline(); RefreshRowEyes(); ArrangeWorkspace();
    }

    private void SetWorldAnimationDefinitionCandidate(DocumentModel document, DefinitionEntry definition)
    {
        var entries = definition.Definition.Entries;
        int entry = entries.Contains(sourceAnimationEntry ?? -1) ? sourceAnimationEntry!.Value : entries.Count == 1 ? entries[0] : -1;
        SetWorldAnimationCandidate(document, definition.Definition.Index, entry);
    }

    private void RequireWorldAnimationTarget(DocumentModel document, int definition, int entry)
    {
        if (document != shownDocument || document.Document is not SourceSceneSnapshot source || scene?.Mission == null)
            throw new StudioCommandException("not_ready", "Open a source world first.");
        if (!source.AnimationDefinitions.Definitions.Any(d => d.Index == definition && d.Entries.Contains(entry)))
            throw new StudioCommandException("stale_record", "Choose an exact binding of the animation definition.");
        if (WorldAnimationTargetNow is { } active && (active.Document != document || active.Revision != document.Revision))
            throw new StudioCommandException("stale_record", "The animation source changed; select its binding again.");
    }

    private Task PinWorldAnimationAsync(DocumentModel document, int definition, int entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); RequireNoDrafts(); RequireWorldAnimationTarget(document, definition, entry);
        var target = new WorldAnimationTarget(document, document.Revision, definition, entry);
        if (worldAnimationPinned == target) return Task.CompletedTask;
        worldAnimationSession?.Dispose(); worldAnimationSession = null;
        ++worldAnimationPreparation; worldAnimationPreparing = false; sourceAnimationPreview = false;
        if (WorldAnimationTargetNow != target) ResetWorldAnimationConfiguration();
        worldAnimationPinned = target; previewId = Guid.NewGuid();
        RefreshWorldAnimationTimeline(); RefreshRowEyes(); UpdateSourceAnimationControls(); ArrangeWorkspace();
        return Task.CompletedTask;
    }

    private async Task<AnimationPreviewContext> PrepareWorldAnimationContextAsync(DocumentModel document, CancellationToken token)
    {
        if (document != shownDocument || document.Document is not SourceSceneSnapshot source || scene?.Mission is not { } baseline)
            throw new StudioCommandException("not_ready", "The initialized world must be visible before preparing an animation.");
        var viewport = scene; int generation = viewport.SceneGeneration; long revision = document.Revision;
        var context = await Task.Run(() => AnimationPreviewContext.FromSourceBaseline(source, baseline, token), token);
        token.ThrowIfCancellationRequested();
        if (document != shownDocument || revision != document.Revision || scene != viewport || generation != viewport.SceneGeneration)
            throw new StudioCommandException("context_changed", "The world changed while preparing the animation.");
        return context;
    }

    private AnimationPlayer CreateWorldAnalysisPlayer(AnimationPreviewContext context, int entry, WorldAnimationScenario configuration, CancellationToken token, int lod = 0)
        => new(context, configuration.Activations.FirstOrDefault(s => s.Id == Guid.Empty)?.Entry ?? entry, configuration.Seed, configuration.Cleanup, token,
            configuration.Schedule(configuration.Activations.FirstOrDefault(s => s.Id == Guid.Empty)?.Entry ?? entry, context.Package.Entries.Count))
        {
            ExecutionPolicy = configuration.Policy, Terrain = context.Terrain, OriginCollision = true, ProceduralLimitsEnabled = true,
            PreviewHeight = configuration.Height, ReferencePosition = configuration.ReferencePosition,
            ActivationStart = configuration.ActivationStart, EffectLevel = configuration.EffectLevel,
            LodLevel = Math.Max(0, lod)
        };

    private async Task<WorldAnimationSession> EnsureWorldAnimationSessionAsync(CancellationToken token)
    {
        if (worldAnimationSession is { IsDisposed: false } existing) return existing;
        if (worldAnimationPinned is not { Entry: >= 0 } target)
        {
            target = worldAnimationCandidate is { Entry: >= 0 } candidate ? candidate
                : throw new StudioCommandException("not_ready", "Select an exact animation binding first.");
            await PinWorldAnimationAsync(target.Document, target.Definition, target.Entry, token);
        }
        RequireWorldAnimationTarget(target.Document, target.Definition, target.Entry);
        var viewport = scene!; int generation = viewport.SceneGeneration; long request = ++worldAnimationPreparation;
        Guid scenario = worldAnimationScenarioEpoch;
        worldAnimationPreparing = true; RefreshWorldAnimationTimeline();
        try
        {
            var context = await PrepareWorldAnimationContextAsync(target.Document, token);
            RequireNoDrafts();
            if (request != worldAnimationPreparation || scenario != worldAnimationScenarioEpoch || worldAnimationPinned != target)
                throw new OperationCanceledException("The animation request was replaced.", token);
            var session = new WorldAnimationSession(context, target.Entry, viewport, ViewModel.Resolver!,
                () => shownDocument == target.Document && target.Document.Revision == target.Revision && scene == viewport
                    && viewport.SceneGeneration == generation && worldAnimationPinned == target,
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
                if (actual.Status == "Stopped") { ++worldAnimationEditingSeek; worldTimeline?.SetRequestedTime(null); }
                worldAnimationFollowCamera = actual.FollowCamera; worldAnimationMuted = actual.Muted; worldAnimationEffects = actual.Effects;
                worldAnimationRangeStart = actual.RangeStart; worldAnimationRangeEnd = actual.RangeEnd; worldAnimationSpeed = actual.Speed;
                worldAnimationReplay = actual.Replay; worldAnimationVolume = actual.Volume;
                if (!session.IsAttached) ApplyWorldAnimationScreenEffects(null);
                RefreshWorldAnimationTimeline(true);
                if (sourceAnimationPreview != previouslyAttached) { UpdateSourceAnimationControls(); ArrangeWorkspace(); }
            };
            session.FramePublished += frame => { if (worldAnimationSession == session) ApplyWorldAnimationScreenEffects(frame); };
            session.Diagnostic += note => { if (worldAnimationSession == session) ViewModel.Status = note; };
            return session;
        }
        finally { if (request == worldAnimationPreparation) { worldAnimationPreparing = false; RefreshWorldAnimationTimeline(); } }
    }

    private async Task WorldAnimationTransportAsync(string action, double seconds = 0, CancellationToken token = default, bool editingFrame = false)
    {
        if (action == "exit") { ExitWorldAnimation(); return; }
        if (action == "stop") { StopWorldAnimationForEdit(); return; }
        if (action == "pause") { ++worldAnimationPreparation; worldAnimationPreparing = false; worldAnimationSession?.Pause(); RefreshWorldAnimationTimeline(); return; }
        RequireNoDrafts();
        if (action == "useSelection")
        {
            var candidate = worldAnimationCandidate is { Entry: >= 0 } selected ? selected : throw new StudioCommandException("not_ready", "Choose an exact animation binding.");
            StopWorldAnimationForEdit(); worldAnimationSession?.Dispose(); worldAnimationSession = null;
            await PinWorldAnimationAsync(candidate.Document, candidate.Definition, candidate.Entry, token);
            RefreshWorldAnimationTimeline(); OpenWorldAnimationTimeline(); return;
        }
        if (action == "scenario") { await OpenWorldAnimationScenario(); return; }
        if (action == "evaluatePaths")
        {
            var target = WorldAnimationTargetNow is { Entry: >= 0 } selected ? selected : throw new StudioCommandException("not_ready", "Choose an exact animation binding.");
            await AnalyzeSourceAnimationAsync(target.Document, target.Definition, target.Entry, worldAnimationSession?.RangeEnd ?? worldAnimationRangeEnd, token); return;
        }
        if (action == "duration")
        {
            var target = WorldAnimationTargetNow is { Entry: >= 0 } selected ? selected : throw new StudioCommandException("not_ready", "Choose an exact animation binding.");
            Guid scenario = worldAnimationScenarioEpoch; var configuration = WorldAnimationConfiguration;
            var context = await PrepareWorldAnimationContextAsync(target.Document, token);
            int lod = Math.Max(0, LodCombo.SelectedIndex);
            var player = CreateWorldAnalysisPlayer(context, target.Entry, configuration, token, lod);
            var duration = await Task.Run(() => player.MeasureDuration(token), token);
            if (scenario != worldAnimationScenarioEpoch || target != WorldAnimationTargetNow) throw new StudioCommandException("context_changed", "The animation scenario changed during duration analysis.");
            worldAnimationDuration = duration; RefreshWorldAnimationTimeline(); return;
        }
        if (scene?.IsFlyActive == true || scene?.IsPickupDragging == true || ZoneToolEngaged || scene?.TerrainBrushActive == true || scene?.HasTransformDraft == true || scene?.AiPathEditingNetwork != null)
            throw new StudioCommandException("busy", "Finish navigation or zone editing before playing an animation.");
        if (!editingFrame && action is "seek" or "previous" or "next") { ++worldAnimationEditingSeek; worldTimeline?.SetRequestedTime(null); }
        var session = await EnsureWorldAnimationSessionAsync(token);
        OpenWorldAnimationTimeline(); await session.TransportAsync(action, seconds, token);
    }

    private void StopWorldAnimationForEdit()
    {
        ++worldAnimationEditingSeek;
        worldTimeline?.SetRequestedTime(null);
        ++worldAnimationPreparation; worldAnimationPreparing = false; worldAnimationSession?.Stop();
        sourceAnimationPreview = false; ApplyWorldAnimationScreenEffects(null);
        RefreshWorldAnimationTimeline(); UpdateSourceAnimationControls();
    }

    private void ExitWorldAnimation()
    {
        ++worldAnimationEditingSeek;
        worldTimeline?.SetRequestedTime(null);
        ++worldAnimationPreparation; worldAnimationPreparing = false;
        var session = worldAnimationSession; worldAnimationSession = null; session?.Dispose();
        bool pinned = worldAnimationPinned != null; worldAnimationPinned = null; sourceAnimationPreview = false;
        ApplyWorldAnimationScreenEffects(null);
        if (pinned) { previewId = Guid.NewGuid(); ResetWorldAnimationConfiguration(); }
        RefreshWorldAnimationTimeline(); RefreshRowEyes(); UpdateSourceAnimationControls(); ArrangeWorkspace();
    }

    private void ClearWorldAnimationTarget()
    {
        ExitWorldAnimation(); worldAnimationCandidate = null; sourceAnimationEntry = null; worldAnimationPlan = null; worldAnimationPlanDocument = null;
        ResetWorldAnimationConfiguration(); RefreshWorldAnimationTimeline(); ArrangeWorkspace();
    }

    private void ResetWorldAnimationConfiguration()
    {
        ++worldAnimationEditingSeek;
        worldTimeline?.SetRequestedTime(null);
        worldAnimationConfiguration = new(); worldAnimationScenarioEpoch = Guid.NewGuid(); worldAnimationDuration = null;
        worldAnimationExpanded.Clear(); worldAnimationCollapsed.Clear(); worldAnimationPageCounts.Clear();
    }

    private void SetWorldAnimationScenario(WorldAnimationScenario configuration)
    {
        RequireNoDrafts();
        if (WorldAnimationTargetNow is { Entry: >= 0 } target)
        {
            var schedule = configuration.Schedule(target.Entry, target.Document.Document.Animations!.Entries.Count);
            if (schedule.Starts.Single(s => s.Id == Guid.Empty).Entry != target.Entry)
                throw new StudioCommandException("invalid_argument", "Keep the primary activation bound to the selected animation.");
        }
        // Validate before publishing a scenario even when the run has not been prepared.
        if (!float.IsFinite(configuration.Height) || configuration.Height is < -999 or > 999 || configuration.EffectLevel is < 0 or > 2)
            throw new StudioCommandException("invalid_argument", "Height must be -999..999 and effect level 0..2.");
        StopWorldAnimationForEdit(); worldAnimationSession?.SetScenario(configuration);
        worldAnimationConfiguration = configuration; worldAnimationScenarioEpoch = Guid.NewGuid(); worldAnimationDuration = null;
        foreach (int definition in shownAnimationPaths.ToArray()) scene?.ClearAnimationPaths(definition.ToString(CultureInfo.InvariantCulture));
        shownAnimationPaths.Clear(); refreshAnimationPathControls?.Invoke(); RefreshWorldAnimationTimeline(); RefreshRowEyes();
    }

    private bool IsWorldAnimationCallIncluded(int rootEntry, string path)
        => WorldAnimationTargetNow?.Entry == rootEntry && worldAnimationConfiguration.Policy.Includes(path);

    private bool CanIncludeWorldAnimationCall(int rootEntry, string parentPath)
        => WorldAnimationTargetNow?.Entry == rootEntry && (IsWorldAnimationActivationPath(parentPath) || worldAnimationConfiguration.Policy.Includes(parentPath));

    private void SetWorldAnimationCallIncluded(int rootEntry, string path, bool value)
    {
        if (IsWorldAnimationActivationPath(path)) throw new StudioCommandException("invalid_argument", "The root is always included; choose a child call path.");
        string parentPath = path[..Math.Max(0, path.LastIndexOf('/'))];
        if (!CanIncludeWorldAnimationCall(rootEntry, parentPath)) throw new StudioCommandException("not_ready", "Use this root as the preview target and include its parent call first.");
        ValidateWorldAnimationCallPath(rootEntry, path);
        var included = worldAnimationConfiguration.Policy.IncludedCalls.ToHashSet(StringComparer.Ordinal);
        if (value) included.Add(path);
        else included.RemoveWhere(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal));
        SetWorldAnimationScenario(worldAnimationConfiguration with { Policy = new(included, worldAnimationConfiguration.Policy.ConditionOverrides) });
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
