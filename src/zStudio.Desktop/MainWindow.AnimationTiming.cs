using System.Globalization;
using System.Text.Json.Nodes;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// Animation mode's Timing view (design/animation-redesign.md §9): the run's projection on the shared clock, its selection
/// (kept in step with the graph, the Inspector and the 3D outlines), the Starts lane's drags and removals (through the
/// scenario operations, so a change stops playback and re-evaluates to the playhead), threshold proposals (through the
/// card's source draft) and MCP animation_timing. The GUI and MCP share every operation here.
/// </summary>
public partial class MainWindow
{
    private AnimationTimingView? animationTimingView;
    private AnimationTiming? animationTiming;
    private AnimationFrame? animationTimingFrame;
    private AnimationScenario? animationTimingScenario;
    private AnimationGraphIndex? animationTimingIndex;
    private int animationTimingToggles, animationTimingBuilt = -1;
    /// <summary>Groups opened (called ones) or closed (top-level ones) against their defaults, for the session.</summary>
    private readonly HashSet<string> animationTimingToggled = new(StringComparer.Ordinal);
    private readonly System.Diagnostics.Stopwatch animationTimingClock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>How often a playing run rebuilds the projection: ten times a second, less often when a long trace makes it costly.</summary>
    private double animationTimingInterval = 100;
    private double worldAnimationRangeStart, worldAnimationRangeEnd = 30, worldAnimationSpeed = 1, worldAnimationVolume = .5;
    private bool worldAnimationMuted, worldAnimationReplay, worldAnimationFollowCamera, worldAnimationEffects = true;

    private AnimationTimingView TimingView
    {
        get
        {
            if (animationTimingView != null) return animationTimingView;
            AnimationTimingView view = new();
            animationTimingView = view;
            view.SeekRequested += (seconds, final) => _ = ReportUi(() => WorldAnimationTransportAsync("seek", seconds));
            view.LaneSelected += lane => _ = ReportUi(() => SelectTimingLaneAsync(lane.Id, CancellationToken.None));
            view.MarkSelected += (lane, mark) => _ = ReportUi(() => SelectTimingOccurrenceAsync(lane.Id, mark.Id, true, CancellationToken.None));
            view.GroupToggled += (group, expanded) => _ = ReportUi(() => { TimingToggle(group, expanded); return Task.CompletedTask; });
            view.StartMoved += (id, seconds) => _ = ReportUi(() => { TimingMoveStart(id, seconds); return Task.CompletedTask; });
            view.StartRemoved += id => _ = ReportUi(() => { ScenarioRemoveStart(id); return Task.CompletedTask; });
            view.ThresholdProposed += (mark, seconds) => _ = ReportUi(() => ProposeTimingThresholdAsync(mark.Event, seconds, CancellationToken.None));
            view.EditTimingRequested += () => _ = ReportUi(() => { ShowAnimationRightTab("inspector"); sectionCard?.FocusAnimationTiming(); return Task.CompletedTask; });
            view.ScenarioRequested += () => { if (InAnimationMode) ShowAnimationRightTab("scenario"); };
            return view;
        }
    }

    /// <summary>
    /// Shows the run on the transport, the graph and Timing. While playing, the projections follow at most ten times a second
    /// (the playhead every frame); at rest every change shows at once.
    /// </summary>
    private void RefreshWorldAnimationRun(bool runtimeOnly = false)
    {
        var target = WorldAnimationTargetNow; var state = worldAnimationSession?.State;
        bool current = target != null && target.Document == shownDocument && target.Revision == target.Document.Revision;
        string? blocked = sectionCard?.HasDraft == true && target != null ? $"Apply or revert the change to {DefinitionName(target.Definition)} to play"
            : parachuteDraft != null ? "Confirm or cancel the drop-window move to play" : null;
        bool playable = current && target!.Entry > 0 && blocked == null;
        var frame = worldAnimationSession?.CurrentFrame;
        string coverage = frame is { } shown ? $"Evaluated 0–{AnimationSeconds.Short(Math.Round(shown.Time, 1))} · {shown.Trace.Count:N0} occurrence{(shown.Trace.Count == 1 ? "" : "s")}" + (shown.TraceDropped > 0 ? $" · {shown.TraceDropped:N0} older discarded" : "")
            : worldAnimationSession?.LastFrame != null ? worldAnimationStaleByEdit ? "Stale · from before your edit" : "Stopped · the last run is kept as stale evidence"
            : "Not evaluated";
        string? failure = animationFailure ?? state?.Failure;
        animationPanel?.SetState(new()
        {
            Playing = state?.Playing == true, CanPlay = playable, CanSeek = playable, CanStop = worldAnimationPreparing || worldAnimationSession != null,
            Busy = worldAnimationPreparing || state?.Loading == true,
            Time = state?.Time ?? 0, RangeStart = state?.RangeStart ?? worldAnimationRangeStart, RangeEnd = state?.RangeEnd ?? worldAnimationRangeEnd,
            Speed = state?.Speed ?? worldAnimationSpeed, Volume = state?.Volume ?? worldAnimationVolume, Muted = state?.Muted ?? worldAnimationMuted,
            Replay = state?.Replay ?? worldAnimationReplay, FollowCamera = state?.FollowCamera ?? worldAnimationFollowCamera,
            Status = failure ?? (target is { Entry: <= 0 } ? "Not bound on " + (scene?.Mission?.Layout.Difficulty.ToString() ?? "this difficulty") : animationTransportNote ?? coverage),
            Failed = failure != null, PlayBlocked = blocked,
            Duration = worldAnimationDuration is { } measured ? $"Duration: {measured.Kind} · {AnimationSeconds.Short(measured.Seconds)} · {measured.Explanation}" : null,
        });
        RefreshAnimationGraph(runtimeOnly);
        RefreshAnimationTiming(runtimeOnly, playable);
        RefreshAnimationNotices();
        RefreshAnimationInspector(runtimeOnly);
        // The scenario's live readouts (the player now, what its rules affect) follow the run when it rests.
        if (state?.Playing != true) RefreshScenarioLive();
    }

    /// <summary>The opened binding's map index, as the graph builds it (grouping by definition).</summary>
    private AnimationGraphIndex TimingIndex(SourceSceneSnapshot source, AnimationPackage package)
    {
        if (animationGraphIndex?.Package == package) return animationGraphIndex;
        if (animationTimingIndex?.Package != package)
            animationTimingIndex = AnimationGraphIndex.Create(package, source.AnimationProvenance, AnimationGraphOptions.For(source.AnimationDefinitions, scene?.Mission?.StartupAnimations, source.Mission));
        return animationTimingIndex;
    }

    private void RefreshAnimationTiming(bool runtimeOnly = false, bool? canSeek = null)
    {
        if (animationTimingView == null && !InAnimationMode) return;
        var view = TimingView;
        var state = worldAnimationSession?.State;
        if (!InAnimationMode || animationMode is not { } target || target.Document.Document is not SourceSceneSnapshot source || source.Animations is not { } package)
        {
            animationTiming = null; animationTimingFrame = null;
            view.SetState(AnimationTimingState.Empty, "none");
            return;
        }
        bool playing = worldAnimationSession?.IsPlaying == true;
        if (runtimeOnly && playing && animationTimingClock.ElapsedMilliseconds < animationTimingInterval) { view.SetTime(state?.Time ?? 0); return; }
        animationTimingClock.Restart();
        string key = $"{target.Document.SessionId:N}:{target.Definition}:{target.Entry}";
        if (target.Entry <= 0)
        {
            animationTiming = null;
            view.SetState(AnimationTimingState.Empty with { Message = NotBoundMessage(target) + " Nothing of it can run here." }, key);
            return;
        }
        var frame = worldAnimationSession?.CurrentFrame;
        bool stale = frame == null && worldAnimationSession?.LastFrame != null;
        frame ??= worldAnimationSession?.LastFrame;
        var scenario = worldAnimationConfiguration;
        var index = TimingIndex(source, package);
        if (animationTiming == null || !ReferenceEquals(frame, animationTimingFrame) || !ReferenceEquals(scenario, animationTimingScenario) || animationTimingBuilt != animationTimingToggles)
        {
            var options = new AnimationTimingOptions { Starts = WorldAnimationActivations(), Index = index, Provenance = source.AnimationProvenance, Toggled = new HashSet<string>(animationTimingToggled, StringComparer.Ordinal) };
            var cost = System.Diagnostics.Stopwatch.StartNew();
            try { animationTiming = frame == null ? AnimationTiming.Empty(package, options) : AnimationTiming.Create(frame, package, options); }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or InvalidOperationException or ArgumentException) { animationTiming = null; ViewModel.Status = "Timing unavailable: " + ex.Message; }
            animationTimingFrame = frame; animationTimingScenario = scenario; animationTimingBuilt = animationTimingToggles;
            animationTimingInterval = Math.Clamp(cost.Elapsed.TotalMilliseconds * 4, 100, 1000);
        }
        bool current = target.Document == shownDocument && target.Revision == target.Document.Revision;
        bool seekable = canSeek ?? (current && sectionCard?.HasDraft != true && parachuteDraft == null);
        var provenance = source.AnimationProvenance;
        bool drafting = sectionCard?.HasDraft == true;
        int? draftDefinition = drafting ? (sectionCard!.Entry as DefinitionEntry)?.Definition.Index : null;
        view.SetState(new()
        {
            Timing = animationTiming, Time = state?.Time ?? 0, RangeStart = state?.RangeStart ?? worldAnimationRangeStart, RangeEnd = state?.RangeEnd ?? worldAnimationRangeEnd,
            CanSeek = seekable, Playing = playing, Evaluated = frame != null, Stale = stale, StaleByEdit = worldAnimationStaleByEdit,
            CanEditThreshold = ev => provenance?.FindEvent(ev) is { TimingFieldId: not null, ReadOnlyReason: null } found && (!drafting || found.Definition == draftDefinition),
        }, key);
    }

    // ---------------------------------------------------------------- operations (GUI and MCP)

    private AnimationTiming RequireTiming()
    {
        if (!InAnimationMode) throw new StudioCommandException("not_ready", "Open an animation in Animation mode first.");
        RefreshAnimationTiming();
        return animationTiming ?? throw new StudioCommandException("not_ready", animationMode is { Entry: <= 0 } ? "The opened definition has no binding in this map." : "Timing is unavailable.");
    }
    private AnimationTimingLane RequireTimingLane(string id) => RequireTiming().Lane(id) ?? throw new StudioCommandException("stale_record", "Timing has no lane with that identity; read the lanes again.");

    /// <summary>
    /// Selects a lane: a sequence lane selects its sequence (the Inspector's card, the graph's node, the 3D outlines of the
    /// nodes its events name); a group its binding; the Starts lane opens the Scenario tab.
    /// </summary>
    private async Task SelectTimingLaneAsync(string id, CancellationToken token)
    {
        var lane = RequireTimingLane(id);
        var target = animationMode!;
        if (lane.Kind == AnimationTimingLaneKind.Starts) { TimingView.Select(id); ShowAnimationRightTab("scenario"); return; }
        RequireNoDrafts();
        var layout = CurrentAnimationGraphLayout();
        string? graph = null;
        if (lane.Kind == AnimationTimingLaneKind.Group)
        {
            int member = lane.Members.FirstOrDefault();
            if (member > 0) await EnsureAnimationCardForEntryAsync(target.Document, member, token, preserveTimeline: true);
            graph = layout?.Node(AnimationGraph.FrameId(member)) != null ? AnimationGraph.FrameId(member)
                : layout?.Nodes.FirstOrDefault(n => n.Node.Reference?.Entries.Contains(member) == true)?.Id;
            animationGraphOutline = scene?.Mission is { } mission ? [.. lane.Members.Select(e => mission.AnimationEntryRoots.TryGetValue(e, out int root) ? root : -1).Where(r => r >= 0).Distinct().Take(zStudio.Rendering.SceneViewport.MaximumOutlinedNodes)] : null;
            animationInspected = member > 0 ? new("definition", member, lane.Path) { Graph = graph } : null;
            ShowAnimationRightTab("inspector");
        }
        else
        {
            var sequence = lane.Sequences.FirstOrDefault() ?? throw new StudioCommandException("stale_record", "The lane has no sequence.");
            await SelectTimingSourceAsync(sequence.Sequence, Guid.Empty, sequence.Entry, lane.Path, token);
            graph = layout?.Node(AnimationGraph.SequenceId(sequence.Entry, sequence.Sequence)) != null ? AnimationGraph.SequenceId(sequence.Entry, sequence.Sequence) : null;
            var events = target.Document.Document.Animations!.Entries[sequence.Entry].AllSequences.FirstOrDefault(s => s.Id == sequence.Sequence)?.Events.Select(e => e.Id).ToArray() ?? [];
            animationGraphOutline = GraphOutline(sequence.Entry, events);
            animationInspected = new("sequence", sequence.Entry, lane.Path) { Graph = graph, Sequence = sequence.Sequence };
        }
        animationGraphSelection = graph;
        GraphView.Select(graph);
        TimingView.Select(id);
        ApplySectionPresentation();
        RefreshAnimationInspector();
    }

    /// <summary>
    /// Selects an occurrence (a mark): its event in the Inspector (with the occurrence and its source), the graph's row and
    /// the nodes it names; with <paramref name="seek"/> the playhead moves to it.
    /// </summary>
    private async Task<object> SelectTimingOccurrenceAsync(string laneId, string markId, bool seek, CancellationToken token)
    {
        var lane = RequireTimingLane(laneId);
        var mark = lane.Marks.FirstOrDefault(m => m.Id == markId) ?? throw new StudioCommandException("stale_record", "The lane has no mark with that identity; read its marks again.");
        RequireNoDrafts();
        var target = animationMode!;
        var layout = CurrentAnimationGraphLayout();
        string? graph = null;
        if (mark.Event != Guid.Empty && mark.Sequence != Guid.Empty)
        {
            string path = TimingInstancePath(mark.Instance) ?? lane.Path;
            await SelectTimingSourceAsync(mark.Sequence, mark.Event, mark.Entry, path, token);
            graph = layout?.Graph.RowOf(mark.Entry, mark.Event)?.Id is { } row && layout.Item(row) != null ? row : null;
            animationGraphOutline = GraphOutline(mark.Entry, [mark.Event]);
            var source = (SourceSceneSnapshot)target.Document.Document;
            string kind = GraphFor(source, layout, mark.Entry) is { } rows && rows.RowOf(mark.Entry, mark.Event) is { } shown ? shown.Role == AnimationGraphRowRole.Question ? "condition" : RowKind(rows, shown) : "event";
            animationInspected = new(kind, mark.Entry, path) { Graph = graph, Sequence = mark.Sequence, Event = mark.Event, Mark = markId };
            ShowAnimationRightTab("inspector");
        }
        else if (mark.Entry > 0)
        {
            await EnsureAnimationCardForEntryAsync(target.Document, mark.Entry, token, preserveTimeline: true);
            graph = layout?.Node(AnimationGraph.FrameId(mark.Entry)) != null ? AnimationGraph.FrameId(mark.Entry) : null;
            animationInspected = new("definition", mark.Entry, lane.Path) { Graph = graph };
            ShowAnimationRightTab("inspector");
        }
        animationGraphSelection = graph;
        GraphView.Select(graph);
        TimingView.Select(laneId, markId, mark.Event == Guid.Empty ? null : mark.Event);
        ApplySectionPresentation();
        RefreshAnimationInspector();
        if (seek && animationTimingView?.State.CanSeek != false)
        {
            TimingView.RevealTime(mark.Start);
            await WorldAnimationTransportAsync("seek", AnimationSeconds.OccurrenceTime(mark.Start), token);
            // The re-evaluated run keeps the occurrence's identity: select it again in the new projection.
            if (animationTiming?.Lane(laneId)?.Marks.Any(m => m.Id == markId) == true) TimingView.Select(laneId, markId, mark.Event == Guid.Empty ? null : mark.Event);
        }
        return new { lane = laneId, mark = markId, seconds = mark.Start, playhead = AnimationSeconds.OccurrenceTime(mark.Start), actualSeconds = worldAnimationSession?.State.Time, graph };
    }
    /// <summary>Selects a source sequence or event for the Inspector in its instance's call path, or the opened binding's when the path no longer validates.</summary>
    private async Task SelectTimingSourceAsync(Guid sequence, Guid ev, int entry, string path, CancellationToken token)
    {
        try { await WorldAnimationSelectSourceAsync(sequence, ev, token, entry, path, preserveTimeline: true); }
        catch (StudioCommandException ex) when (ex.Code == "stale_record" && path.Length > 0) { await WorldAnimationSelectSourceAsync(sequence, ev, token, entry, "", preserveTimeline: true); }
    }
    private string? TimingInstancePath(long instance)
    {
        var frame = animationTimingFrame;
        if (frame == null) return null;
        foreach (var live in frame.Instances) if (live.Instance == instance) return live.Path;
        foreach (var t in frame.Trace) if (t.Instance == instance) return t.InstancePath;
        return null;
    }

    /// <summary>Opens or closes an instance group (presentation only; never runs anything).</summary>
    private void TimingToggle(string groupId, bool expanded)
    {
        var lane = RequireTimingLane(groupId);
        if (lane.Kind != AnimationTimingLaneKind.Group) throw new StudioCommandException("invalid_argument", "Only an instance group opens or closes.");
        if (lane.Expanded == expanded) return;
        if (!animationTimingToggled.Remove(groupId)) animationTimingToggled.Add(groupId);
        animationTimingToggles++;
        RefreshAnimationTiming();
    }

    /// <summary>A Starts-lane drag: the start moves through the scenario (playback stops; the run re-evaluates to the playhead).</summary>
    private void TimingMoveStart(Guid id, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds is < 0 or > AnimationSeconds.Maximum) throw new StudioCommandException("invalid_argument", "A start is at 0–3,600 s.");
        var starts = WorldAnimationActivations();
        if (!starts.Any(s => s.Id == id)) throw new StudioCommandException("stale_record", "The start is not in the scenario.");
        ScenarioStarts(starts.Select(s => s.Id == id ? s with { Seconds = seconds } : s).ToArray());
    }

    /// <summary>Graph → Timing: the lane of a sequence (opening its closed groups) scrolls into view with the event selected.</summary>
    private void RevealTimingRow(int entry, string path, Guid sequence, Guid? ev)
    {
        if (animationTimingView == null || animationTiming is not { } timing) return;
        AnimationTimingLane? Find(AnimationTiming shown) => shown.AllLanes.Where(l => l.Kind == AnimationTimingLaneKind.Sequence && l.Sequences.Any(s => s.Entry == entry && s.Sequence == sequence))
            .OrderBy(l => l.Path == path ? 0 : 1).FirstOrDefault();
        var lane = Find(timing);
        if (lane == null) { TimingView.Select(null); return; }
        bool opened = false;
        for (string? group = lane.Group; group != null && timing.Lane(group) is { } row; group = row.Group)
            if (!row.Expanded) { if (!animationTimingToggled.Remove(group)) animationTimingToggled.Add(group); opened = true; }
        if (opened) { animationTimingToggles++; RefreshAnimationTiming(); lane = animationTiming is { } rebuilt ? Find(rebuilt) : null; }
        if (lane != null) TimingView.Select(lane.Id, null, ev);
    }

    /// <summary>
    /// Proposes an event's start threshold, in its own clock, through the card's source draft (a Timing bracket drag, MCP
    /// animation_timing threshold): the event is selected and its START_TIME field changes; Apply makes it one undo step.
    /// </summary>
    private async Task<object?> ProposeTimingThresholdAsync(Guid ev, double seconds, CancellationToken token)
    {
        if (!double.IsFinite(seconds) || seconds is < -AnimationSeconds.Maximum or > AnimationSeconds.Maximum) throw new StudioCommandException("invalid_argument", "Use a finite threshold in seconds.");
        var target = WorldAnimationTargetNow ?? throw new StudioCommandException("not_ready", "Open an animation in Animation mode first.");
        var provenance = (target.Document.Document as SourceSceneSnapshot)?.AnimationProvenance?.FindEvent(ev)
            ?? throw new StudioCommandException("stale_record", "The event is not in this map's sources; read the marks again.");
        // A draft already open on this definition takes the change (a second drag refines it); any other draft is resolved first.
        bool refining = SameDefinitionDraft(target, provenance.Definition);
        if (refining) RequireNoDrafts(target.Document, committing: true, committingSection: true); else RequireNoDrafts();
        if (provenance.TimingFieldId == null || provenance.ReadOnlyReason != null) throw new StudioCommandException("read_only", provenance.ReadOnlyReason ?? "This event's start threshold cannot be edited.");
        var timing = RequireTiming();
        string path = timing.AllLanes.FirstOrDefault(l => l.Sequences.Any(s => s.Entry == provenance.Entry && s.Sequence == provenance.Sequence))?.Path ?? "";
        long revision = target.Document.Revision;
        if (refining) { if (provenance.RecordId is { } record) SectionCard().SelectAnimationRecord(record); }
        else await SelectTimingSourceAsync(provenance.Sequence, ev, provenance.Entry, path, token);
        token.ThrowIfCancellationRequested();
        if (target != WorldAnimationTargetNow || target.Document != shownDocument || revision != target.Document.Revision)
            throw new StudioCommandException("context_changed", "The event selection changed.");
        // The clock comes from the open draft when it already changed it (a second drag keeps a chosen clock), else from the source.
        var card = SectionCard();
        SourceAnimationEdits.Change change;
        try { change = provenance.RecordId is { } recordId ? card.StartTimeChange(recordId, null, seconds) : throw new InvalidOperationException("This event has no source record."); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { throw new StudioCommandException("read_only", ex.Message); }
        if (change.Field != provenance.TimingFieldId) throw new StudioCommandException("context_changed", "The event's source record changed; read the marks again.");
        card.SetAnimationChanges([change]);
        // The Inspector shows the event whose threshold the drag proposes.
        if (InAnimationMode && animationInspected?.Event != ev)
        {
            var layout = CurrentAnimationGraphLayout();
            string? row = layout?.Graph.RowOf(provenance.Entry, ev)?.Id is { } found && layout.Item(found) != null ? found : null;
            animationInspected = new("event", provenance.Entry, path) { Graph = row, Sequence = provenance.Sequence, Event = ev };
            RefreshAnimationInspector();
        }
        ShowAnimationRightTab("inspector");
        return SectionCard().AnimationDraft;
    }

    /// <summary>Whether the card's open draft belongs to <paramref name="definition"/> of the shown map.</summary>
    private bool SameDefinitionDraft(WorldAnimationTarget target, int definition) =>
        sectionCard is { HasDraft: true } card && card.Document == target.Document && (card.Entry as DefinitionEntry)?.Definition.Index == definition;

    private async Task WorldAnimationSelectSourceAsync(Guid sequence, Guid ev, CancellationToken token, int? entry = null, string path = "", bool preserveTimeline = false)
    {
        RequireNoDrafts(); var target = WorldAnimationTargetNow ?? throw new StudioCommandException("not_ready", "Open an animation in Animation mode first.");
        int selected = entry ?? target.Entry;
        var plan = GetWorldAnimationStructurePlan();
        if (selected < 0 || selected >= plan.EntryCount) throw new StudioCommandException("stale_record", "Choose an available exact animation binding.");
        if (path.Length != 0 && ValidateWorldAnimationCallPath(target.Entry, path) != selected)
            throw new StudioCommandException("stale_record", "The call path does not reach this binding.");
        var package = target.Document.Document.Animations!;
        var selectedSequence = sequence == Guid.Empty ? null : package.Entries[selected].AllSequences.FirstOrDefault(s => s.Id == sequence);
        if (sequence != Guid.Empty && selectedSequence == null || ev != Guid.Empty && selectedSequence?.Events.Any(e => e.Id == ev) != true)
            throw new StudioCommandException("stale_record", "The source sequence or event is unavailable.");
        await EnsureAnimationCardForEntryAsync(target.Document, selected, token, preserveTimeline);
        if (target.Document.Document is SourceSceneSnapshot snapshot)
        {
            string? record = ev != Guid.Empty ? snapshot.AnimationProvenance?.FindEvent(ev)?.RecordId : snapshot.AnimationProvenance?.FindSequence(sequence)?.RecordId;
            if (record != null) SectionCard().SelectAnimationRecord(record);
        }
        // The Inspector shows the selected sequence or event.
        if (InAnimationMode && animationRightTab != "inspector") ShowAnimationRightTab("inspector");
    }

    private async Task<SourceAnimationEdits.SourceContext> ReadAnimationContextAsync(DocumentModel document, int definition,
        string record, double? scriptTime, CancellationToken token, string? boundScript = null)
    {
        long revision = document.Revision;
        var workspace = document.SourceWorld!.Workspace.BeginPreparedEdit().Workspace;
        var context = await Task.Run(() => SourceAnimationEdits.ReadContext(new AnimationProjectFiles(workspace, token),
            SourceBuilder.AnimationRoot(document.SourceWorld.Mission), definition, record, scriptTime, token, boundScript), token);
        if (document != shownDocument || document.IsDisposed || revision != document.Revision)
            throw new StudioCommandException("context_changed", "The source map changed while reading the source context.");
        return context;
    }

    // ---------------------------------------------------------------- MCP

    private static string TimingKind(AnimationTimingMarkKind kind) => kind switch
    {
        AnimationTimingMarkKind.Executed => "executed", AnimationTimingMarkKind.Interval => "interval", AnimationTimingMarkKind.Running => "running",
        AnimationTimingMarkKind.Condition => "condition", AnimationTimingMarkKind.Skipped => "skipped", AnimationTimingMarkKind.Excluded => "excluded",
        AnimationTimingMarkKind.Duplicate => "duplicate", AnimationTimingMarkKind.Blocked => "blocked", AnimationTimingMarkKind.Release => "release",
        AnimationTimingMarkKind.Loop => "loop", AnimationTimingMarkKind.Refused => "refused", _ => "unresolved",
    };
    private static string TimingKind(AnimationTimingBandKind kind) => kind switch
    {
        AnimationTimingBandKind.WaitingForRelease => "waitingRelease", AnimationTimingBandKind.WaitingForThreshold => "waitingThreshold", AnimationTimingBandKind.Paused => "paused",
        AnimationTimingBandKind.Blocked => "blocked", AnimationTimingBandKind.Stopped => "stopped", AnimationTimingBandKind.Complete => "complete",
        AnimationTimingBandKind.Unavailable => "unavailable", _ => "binding",
    };
    private static string? TimingRun(AnimationSequenceRunState? run) => run switch
    {
        null => null, AnimationSequenceRunState.NotReached => "notStarted", AnimationSequenceRunState.WaitingForThreshold => "waitingThreshold",
        AnimationSequenceRunState.WaitingForRelease => "waitingRelease", _ => JsonNamingPolicy(run.Value.ToString()),
    };
    private static string JsonNamingPolicy(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private object TimingPresentation()
    {
        var view = TimingView; var timing = animationTiming;
        return new
        {
            view = view.View, visibleRange = new[] { view.VisibleStart, view.VisibleEnd }, labelWidth = view.LabelWidth, thresholds = view.Thresholds,
            selection = new { lane = view.SelectedLane, mark = view.SelectedMark, @event = view.SelectedEvent },
            time = view.State.Time, range = new[] { view.State.RangeStart, view.State.RangeEnd }, evaluated = view.State.Evaluated, stale = view.State.Stale,
            message = view.State.Message ?? (timing != null && !view.State.Evaluated ? "Nothing has run yet. Play, or click the ruler to seek; the preview runs the scenario up to that time." : null),
            lanes = timing?.Lanes.Count ?? 0, allLanes = timing?.AllLanes.Count ?? 0, marks = timing?.MarkCount ?? 0, log = timing?.Log.Count ?? 0,
            retained = timing?.Retained ?? 0, discarded = timing?.Discarded ?? 0, runExtent = timing?.RunStart is double a ? new[] { a, timing.RunEnd ?? a } : null,
            truncated = timing?.Truncated ?? false,
        };
    }
    private static object TimingLaneJson(AnimationTimingLane lane) => new
    {
        id = lane.Id, kind = JsonNamingPolicy(lane.Kind.ToString()), label = lane.Label, depth = lane.Depth, group = lane.Group,
        expanded = lane.Kind == AnimationTimingLaneKind.Group ? lane.Expanded : (bool?)null, laneCount = lane.Kind == AnimationTimingLaneKind.Group ? lane.LaneCount : (int?)null,
        started = lane.Started, ended = lane.Ended, state = lane.State, run = TimingRun(lane.Run), waitsForRelease = lane.Kind == AnimationTimingLaneKind.Sequence ? lane.WaitsForRelease : (bool?)null,
        cleanup = lane.Cleanup ? true : (bool?)null, path = lane.Path, members = lane.Members.Take(16).ToArray(), memberCount = lane.Members.Count, instances = lane.Instances.Count,
        sequences = lane.Sequences.Take(16).Select(s => new { entry = s.Entry, sequence = s.Sequence }).ToArray(),
        markCount = lane.Marks.Count, bandCount = lane.Bands.Count, tooltip = lane.Tooltip,
        starts = lane.Kind == AnimationTimingLaneKind.Starts ? lane.Starts.Select(s => new { id = s.Id, entry = s.Entry, seconds = s.Seconds, label = s.Label, primary = s.Primary, prerequisitesIgnored = s.PrerequisitesIgnored, refused = s.Refused }).ToArray() : null,
    };
    private object TimingMarkJson(AnimationTimingMark mark, Func<Guid, bool> editable) => new
    {
        id = mark.Id, kind = TimingKind(mark.Kind), band = false, start = mark.Start, end = mark.End, label = mark.Label, result = mark.Result,
        entry = mark.Entry, sequence = mark.Sequence, @event = mark.Event, instance = mark.Instance, occurrence = mark.Occurrence,
        family = JsonNamingPolicy(mark.Family.ToString()), value = mark.Value, forced = mark.Kind == AnimationTimingMarkKind.Condition ? mark.Forced : (bool?)null,
        count = mark.Count, targetLane = mark.TargetLane,
        bracket = mark.Bracket is { } b ? new
        {
            clock = b.Clock switch { 2 => "sequence", 3 => "previous", _ => "animation" }, threshold = b.Threshold, anchor = b.Anchor, end = b.End, label = b.Label, gap = b.Gap,
            editable = editable(mark.Event),
        } : null,
    };
    private static object TimingBandJson(string lane, int index, AnimationTimingBand band) => new
    {
        id = lane + "#b" + index.ToString(CultureInfo.InvariantCulture), kind = TimingKind(band.Kind), band = true, start = band.Start, end = band.End, label = band.Label, open = band.Open,
        @event = band.Event, instance = band.Instance,
    };

    private void RegisterAnimationTimingCommands(StudioCommands registry)
    {
        RegisterJob(registry, "animation_timing", "Animation mode's Timing view: the run on the shared clock in seconds (the GUI's identities). read reports the presentation (lanes or log, visible range, label width, thresholds), the selection and counts. lanes pages the shown rows by at most 100: the Starts lane (its starts: entry, seconds, primary, prerequisitesIgnored where the scenario's rule let an unmet start run, refused with its reason), instance groups (a scenario start's binding, then the instances its calls reached, nested under their caller and grouped by definition as the graph groups them; started, ended, binding state, expanded) and sequence lanes (state at the playhead, run). marks pages one lane's marks and state bands by time: kinds executed, interval, running, condition (value, forced), skipped (count), excluded, duplicate, blocked, release (targetLane), loop, refused, unresolved, and bands (band true) waitingRelease (released by / no releaser left), waitingThreshold (anim ≥ 23 s → 43.000 s), paused, blocked, stopped, complete, unavailable and a group's binding state; an event with a start threshold whose anchor occurred carries its bracket (anchor, end, gap). log pages the Log (Time · Instance · Sequence · Event · Result, newest last; retained and discarded counts). options sets thresholds, visibleRange, fit (range, run or selection), labelWidth (160–420 DIP) and view (lanes or log). select selects a lane (a sequence lane its sequence: the Inspector, the graph and the 3D outlines follow; a group its binding; the Starts lane opens the Scenario tab), occurrence a mark (its event, with the occurrence and its source in the Inspector) and seeks to it. expand opens or closes a group (presentation only). threshold proposes an event's start threshold in its own clock, in seconds, through the same source draft as the bracket drag (source_animation apply or cancel). Starts move and go with animation_scenario starts; the playhead moves with animation_transport.",
            [PreviewParameter,
             P("action", "string", "Operation.", true, "read", "lanes", "marks", "log", "options", "select", "occurrence", "expand", "threshold"),
             P("lane", "string", "A lane identity from lanes (marks, select, occurrence, expand)."),
             P("mark", "string", "A mark identity from marks (occurrence)."),
             P("expanded", "boolean", "expand: open (true) or close (false) the group."),
             P("event", "string", "threshold: the event GUID (a mark's event)."),
             new("seconds", "number", "threshold: the new start threshold in the event's own clock, in seconds.", NumberMinimum: -3600, NumberMaximum: 3600),
             new("changes", "object", "options: the Timing view's presentation.", Properties:
             [
                 P("thresholds", "boolean", "Show threshold brackets (off by default)."),
                 new("visibleRange", "array", "The visible start and end seconds (independent of the playback range).", Items: new("", "number", "Seconds.", NumberMinimum: 0, NumberMaximum: 3600), MinItems: 2, MaxItems: 2),
                 P("fit", "string", "Fit the visible range.", false, "range", "run", "selection"),
                 new("labelWidth", "number", "The label column in DIP.", NumberMinimum: 160, NumberMaximum: 420),
                 P("view", "string", "Lanes or the Log.", false, "lanes", "log"),
             ]),
             new("offset", "integer", "Page offset.", Minimum: 0, Maximum: int.MaxValue),
             new("limit", "integer", "Page size, at most 100.", Minimum: 1, Maximum: 100)], false, async (a, token) =>
        {
            RequirePreview(a); RequireScenarioMap();
            string action = Text(a, "action");
            int offset = Int(a, "offset"), limit = Int(a, "limit", 100);
            if (offset < 0 || limit is < 1 or > 100) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–100.");
            var paging = new JsonObject { ["offset"] = offset, ["limit"] = limit };
            object? result = null;
            switch (action)
            {
                case "read": RequireTiming(); break;
                case "lanes":
                    result = Page(RequireTiming().Lanes, paging, project: TimingLaneJson, maximumRowBytes: l => 4096 + 2L * (l.Label.Length + l.Tooltip.Length + l.Path.Length) + 256L * Math.Min(32, l.Starts.Count)).Data;
                    break;
                case "marks":
                    {
                        var lane = RequireTimingLane(Text(a, "lane"));
                        var editable = animationTimingView?.State.CanEditThreshold ?? (_ => false);
                        var items = lane.Marks.Select(m => (m.Start, Mark: (AnimationTimingMark?)m, Band: (AnimationTimingBand?)null, Index: 0))
                            .Concat(lane.Bands.Select((b, i) => (b.Start, Mark: (AnimationTimingMark?)null, Band: (AnimationTimingBand?)b, Index: i))).OrderBy(x => x.Start);
                        result = Page(items, paging, project: x => x.Mark is { } m ? TimingMarkJson(m, editable) : TimingBandJson(lane.Id, x.Index, x.Band!),
                            maximumRowBytes: x => 1536 + 2L * ((x.Mark?.Label.Length ?? 0) + (x.Mark?.Result.Length ?? 0) + (x.Band?.Label.Length ?? 0))).Data;
                        break;
                    }
                case "log":
                    result = Page(RequireTiming().Log, paging, project: r => new { time = r.Time, instance = r.Instance, sequence = r.Sequence, @event = r.Event, result = r.Result, lane = r.Lane, mark = r.Mark, instanceId = r.InstanceId, occurrence = r.Occurrence, entry = r.Entry, eventId = r.EventId },
                        maximumRowBytes: r => 1024 + 2L * (r.Instance.Length + r.Sequence.Length + r.Event.Length + r.Result.Length + r.Lane.Length + r.Mark.Length)).Data;
                    break;
                case "options":
                    {
                        RequireTiming();
                        var changes = a["changes"] as JsonObject ?? throw new StudioCommandException("invalid_argument", "Supply changes.");
                        var view = TimingView;
                        foreach (var (name, value) in changes)
                        {
                            if (value == null) throw new StudioCommandException("invalid_argument", "Supply explicit option values.");
                            switch (name)
                            {
                                case "thresholds": view.SetThresholds(value.GetValue<bool>()); break;
                                case "visibleRange":
                                    {
                                        if (value is not JsonArray { Count: 2 } pair) throw new StudioCommandException("invalid_argument", "visibleRange is [start, end] in seconds.");
                                        double start = pair[0]!.GetValue<double>(), end = pair[1]!.GetValue<double>();
                                        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > AnimationSeconds.Maximum || end <= start) throw new StudioCommandException("invalid_argument", "visibleRange is an increasing pair within 0–3,600 s.");
                                        view.SetVisibleRange(start, end); break;
                                    }
                                case "fit":
                                    {
                                        string fit = value.GetValue<string>();
                                        if (fit is not ("range" or "run" or "selection")) throw new StudioCommandException("invalid_argument", "Fit range, run or selection.");
                                        if (!view.Fit(fit)) throw new StudioCommandException("not_ready", fit == "run" ? "Nothing has run yet." : "Select a lane or a mark first.");
                                        break;
                                    }
                                case "labelWidth":
                                    {
                                        double width = value.GetValue<double>();
                                        if (!double.IsFinite(width) || width is < AnimationTimingView.MinimumLabelWidth or > AnimationTimingView.MaximumLabelWidth) throw new StudioCommandException("invalid_argument", "The label column is 160–420 DIP.");
                                        view.SetLabelWidth(width); break;
                                    }
                                case "view":
                                    {
                                        string shown = value.GetValue<string>();
                                        if (shown is not ("lanes" or "log")) throw new StudioCommandException("invalid_argument", "The view is lanes or log.");
                                        view.SetView(shown); break;
                                    }
                                default: throw new StudioCommandException("invalid_argument", $"Unknown Timing option {name}: thresholds, visibleRange, fit, labelWidth or view. Playback options are animation_options; the scenario is animation_scenario.");
                            }
                        }
                        break;
                    }
                case "select":
                    {
                        string lane = Text(a, "lane");
                        if (lane.Length == 0) { RequireTiming(); TimingView.Select(null); break; }
                        await SelectTimingLaneAsync(lane, token); break;
                    }
                case "occurrence": result = await SelectTimingOccurrenceAsync(Text(a, "lane"), Text(a, "mark"), true, token); break;
                case "expand":
                    if (!a.ContainsKey("expanded")) throw new StudioCommandException("invalid_argument", "Supply expanded.");
                    TimingToggle(Text(a, "lane"), Flag(a, "expanded")); break;
                case "threshold":
                    {
                        if (!Guid.TryParse(Text(a, "event"), out Guid ev)) throw new StudioCommandException("invalid_argument", "Supply the event's GUID.");
                        if (!a.ContainsKey("seconds")) throw new StudioCommandException("invalid_argument", "Supply seconds.");
                        result = new { draft = await ProposeTimingThresholdAsync(ev, Number(a, "seconds"), token) };
                        break;
                    }
                default: throw new StudioCommandException("invalid_argument", "Unknown Timing operation.");
            }
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            return Result(new { timing = TimingPresentation(), result });
        });
    }
}
