using System.Globalization;
using System.Text.Json.Nodes;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private sealed record WorldAnimationRowSource(int Entry, string Path, Guid Sequence = default, Guid Event = default,
        AnimationStructureCall? Call = null, string? MoreKey = null);
    private readonly Dictionary<string, WorldAnimationRowSource> worldAnimationRowSources = [];
    private readonly HashSet<string> worldAnimationExpanded = [];
    private readonly HashSet<string> worldAnimationCollapsed = [];
    private readonly System.Diagnostics.Stopwatch worldAnimationRowsClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Dictionary<string, int> worldAnimationPageCounts = [];
    private IReadOnlyList<WorldAnimationTimelineRow> worldAnimationSourceRows = [], worldAnimationRunRows = [];
    private double worldAnimationRangeStart, worldAnimationRangeEnd = 30, worldAnimationSpeed = 1, worldAnimationVolume = .5;
    private bool worldAnimationMuted, worldAnimationReplay, worldAnimationFollowCamera, worldAnimationEffects = true;

    private WorldAnimationTimeline WorldTimeline
    {
        get
        {
            if (worldTimeline != null) return worldTimeline;
            var timeline = new WorldAnimationTimeline(); worldTimeline = timeline;
            timeline.TransportRequested += action => _ = ReportUi(() => WorldAnimationTransportAsync(action));
            timeline.SeekRequested += time => _ = ReportUi(() => SeekWorldAnimationFrameAsync(time));
            timeline.ExpansionRequested += (row, expanded) => _ = ReportUi(() => { WorldAnimationExpandRow(row.Id, expanded); return Task.CompletedTask; });
            timeline.RowSelected += row => _ = ReportUi(() => SelectWorldAnimationTimelineRowAsync(row));
            timeline.EditTimingRequested += row => _ = ReportUi(async () => { await SelectWorldAnimationTimelineRowAsync(row); ShowAnimationRightTab("inspector"); sectionCard?.FocusAnimationTiming(); });
            timeline.OccurrenceSelected += (row, mark) => _ = ReportUi(async () => { await WorldAnimationCellAsync(row.Id, mark.Id, true); });
            timeline.TimingMoveRequested += (row, time) => _ = ReportUi(() => row.ActivationId is Guid id ? MoveWorldAnimationActivation(id, time) : ProposeWorldAnimationFrameAsync(row.Id, AnimationTimelineFrames.Frame(time), CancellationToken.None));
            timeline.InclusionRequested += (row, included) => _ = ReportUi(() =>
            {
                if (worldAnimationRowSources.TryGetValue(row.Id, out var source) && source.Call != null && WorldAnimationTargetNow is { } target)
                    SetWorldAnimationCallIncluded(target.Entry, source.Call.Path, included);
                return Task.CompletedTask;
            });
            timeline.GuideVisibilityRequested += (unusedRow, shown) => _ = ReportUi(() =>
            {
                if (WorldAnimationTargetNow is { Entry: >= 0 } target)
                {
                    if (shown) ShowSourceAnimationPaths(target.Document, target.Definition, target.Entry);
                    else HideSourceAnimationPaths(target.Definition);
                }
                RefreshWorldAnimationTimeline(); return Task.CompletedTask;
            });
            timeline.OptionRequested += (key, value) => _ = ReportUi(() => WorldAnimationOptionAsync(key, value));
            timeline.WindowMoveRequested += (row, start) => _ = ReportUi(() => MoveWorldAnimationDropAsync(row, start, CancellationToken.None));
            return timeline;
        }
    }

    private void RefreshWorldAnimationTimeline(bool runtimeOnly = false)
    {
        var target = WorldAnimationTargetNow; var state = worldAnimationSession?.State;
        bool current = target != null && target.Document == shownDocument && target.Revision == target.Document.Revision;
        string? blocked = sectionCard?.HasDraft == true && target != null ? $"Apply or revert the change to {DefinitionName(target.Definition)} to play"
            : parachuteDraft != null ? "Confirm or cancel the drop-window move to play" : null;
        bool playable = current && target!.Entry > 0 && blocked == null;
        var frame = worldAnimationSession?.CurrentFrame;
        string coverage = worldAnimationDuration is { } duration ? $"{duration.Kind} · {duration.Seconds:0.###} s · {duration.Explanation}"
            : frame is { } shown ? $"Evaluated 0–{shown.Time:0.0} s · {shown.Trace.Count:N0} occurrence{(shown.Trace.Count == 1 ? "" : "s")}" + (shown.TraceDropped > 0 ? $" · {shown.TraceDropped:N0} older discarded" : "")
            : worldAnimationSession?.LastFrame != null ? "Stopped · the last run is kept as stale evidence"
            : "Not evaluated";
        var timelineState = new WorldAnimationTimelineState
        {
            Status = worldAnimationPreparing ? "Preparing" : state?.Status ?? (target != null ? "Unprepared" : "Closed"),
            Playing = state?.Playing == true, CanPlay = playable, CanSeek = playable,
            CanStop = worldAnimationPreparing || worldAnimationSession != null,
            Busy = worldAnimationPreparing || state?.Loading == true, HasSession = worldAnimationSession != null,
            EvidenceIdentity = (worldAnimationSession?.RunId ?? worldAnimationSession?.LastRunId)?.ToString("N") ?? "",
            Time = state?.Time ?? 0, RangeStart = state?.RangeStart ?? worldAnimationRangeStart, RangeEnd = state?.RangeEnd ?? worldAnimationRangeEnd,
            Speed = state?.Speed ?? worldAnimationSpeed, Volume = state?.Volume ?? worldAnimationVolume,
            Muted = state?.Muted ?? worldAnimationMuted, Replay = state?.Replay ?? worldAnimationReplay, FollowCamera = state?.FollowCamera ?? worldAnimationFollowCamera,
            Coverage = coverage,
        };
        string? failure = animationFailure ?? state?.Failure;
        animationPanel?.SetState(new()
        {
            Playing = timelineState.Playing, CanPlay = playable, CanSeek = playable, CanStop = timelineState.CanStop, Busy = timelineState.Busy,
            Time = timelineState.Time, RangeStart = timelineState.RangeStart, RangeEnd = timelineState.RangeEnd, Speed = timelineState.Speed,
            Volume = timelineState.Volume, Muted = timelineState.Muted, Replay = timelineState.Replay, FollowCamera = timelineState.FollowCamera,
            Status = failure ?? (target is { Entry: <= 0 } ? "Not bound on " + (scene?.Mission?.Layout.Difficulty.ToString() ?? "this difficulty") : coverage),
            Failed = failure != null, PlayBlocked = blocked,
            Duration = worldAnimationDuration is { } measured ? $"Duration: {measured.Kind} · {AnimationSeconds.Short(measured.Seconds)} · {measured.Explanation}" : null,
        });
        RefreshAnimationGraph(runtimeOnly);
        if (worldTimeline == null && target == null) return;
        var timeline = WorldTimeline;
        timeline.SetState(timelineState);
        // The ruler follows every frame; rebuilding retained occurrence rows is limited to ten updates per second.
        if (runtimeOnly && state?.Playing == true && worldAnimationRowsClock.ElapsedMilliseconds < 100) return;
        worldAnimationRowsClock.Restart();
        if (!runtimeOnly)
        {
            worldAnimationRowSources.Clear();
            worldAnimationSourceRows = current && target!.Entry > 0 ? WorldAnimationSourceRows(target) : [];
        }
        else foreach (string row in worldAnimationRowSources.Keys.Where(k => k.StartsWith("run:", StringComparison.Ordinal)).ToArray()) worldAnimationRowSources.Remove(row);
        worldAnimationRunRows = WorldAnimationRunRows();
        timeline.SetRows(worldAnimationSourceRows, worldAnimationRunRows);
        // The scenario's live readouts (the player now, what its rules affect) follow the run when it rests.
        if (state?.Playing != true) RefreshScenarioLive();
    }

    private object WorldAnimationTimelineRows(string view, int offset, int limit)
    {
        if (view is not ("source" or "run") || offset < 0 || limit is < 1 or > 100) throw new StudioCommandException("invalid_argument", "Use source/run, offset >= 0, limit 1–100.");
        var rows = view == "source" ? worldAnimationSourceRows : worldAnimationRunRows;
        // A looping sequence may retain thousands of marks. Do not copy or serialize them with its row.
        var page = Page(rows, new JsonObject { ["offset"] = offset, ["limit"] = limit }, project: row => new
        {
            row.Id, row.Label, row.Depth, row.IsGroup, row.Expandable, row.Expanded,
            row.Included, row.InclusionEnabled, row.InclusionReason, row.GuidesShown,
            row.CanEditTiming, row.ActivationId, row.TimingLabel, row.Detail, markCount = row.Marks.Count,
            row.IsWindowClock, row.WindowClockEnd, row.CanMoveWindow, row.WindowMoveReason,
            row.WindowStartThreshold, row.NativeClockStart, row.NativeClockEnd, row.IsSourceClock, row.StartThreshold
        }, maximumRowBytes: row => 2048 + InspectionResultBudget.Text(row.Id) + InspectionResultBudget.Text(row.Label)
            + InspectionResultBudget.Text(row.InclusionReason) + InspectionResultBudget.Text(row.TimingLabel)
            + InspectionResultBudget.Text(row.Detail) + InspectionResultBudget.Text(row.WindowMoveReason)).Data.AsObject();
        var items = page["items"]; page.Remove("items"); page["rows"] = items;
        return page;
    }

    private object WorldAnimationTimelineMarks(string id, int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–100.");
        var row = worldAnimationSourceRows.Concat(worldAnimationRunRows).FirstOrDefault(r => r.Id == id)
            ?? throw new StudioCommandException("stale_record", "The timeline row is unavailable.");
        return Page(row.Marks, new JsonObject { ["offset"] = offset, ["limit"] = limit },
            maximumRowBytes: mark => 512 + InspectionResultBudget.Text(mark.Id) + InspectionResultBudget.Text(mark.Label)).Data;
    }

    private void WorldAnimationExpandRow(string id, bool expanded)
    {
        if (!worldAnimationSourceRows.Any(r => r.Id == id && r.Expandable)) throw new StudioCommandException("stale_record", "The expandable timeline row is unavailable.");
        if (expanded && worldAnimationExpanded.Count >= AnimationStructurePlan.MaximumDependencyRecords) throw new StudioCommandException("limit_exceeded", "Collapse some branches before expanding more animation paths.");
        if (expanded) { worldAnimationExpanded.Add(id); worldAnimationCollapsed.Remove(id); }
        else { worldAnimationExpanded.Remove(id); worldAnimationCollapsed.Add(id); }
        RefreshWorldAnimationTimeline();
    }

    private async Task WorldAnimationSelectRowAsync(string id, long? occurrence = null)
    {
        var row = worldAnimationSourceRows.Concat(worldAnimationRunRows).FirstOrDefault(r => r.Id == id)
            ?? throw new StudioCommandException("stale_record", "The timeline row is unavailable.");
        if (occurrence is long ordinal)
        {
            var mark = row.Marks.FirstOrDefault(m => m.Occurrence == ordinal) ?? throw new StudioCommandException("stale_record", "The occurrence is not on this row.");
            var frame = worldAnimationSession?.CurrentFrame ?? worldAnimationSession?.LastFrame;
            var dispatched = frame?.Trace.FirstOrDefault(t => t.Occurrence == ordinal) ?? throw new StudioCommandException("stale_record", "The occurrence is no longer retained.");
            await WorldAnimationSelectSourceAsync(dispatched.Sequence, dispatched.Event, CancellationToken.None, dispatched.Entry, dispatched.InstancePath, preserveTimeline: true);
            WorldTimeline.SelectRow(id, mark.Id);
        }
        else { await SelectWorldAnimationTimelineRowAsync(row); WorldTimeline.SelectRow(id); }
    }

    private IReadOnlyList<WorldAnimationTimelineRow> WorldAnimationSourceRows(WorldAnimationTarget target)
    {
        var plan = GetWorldAnimationStructurePlan(); List<WorldAnimationTimelineRow> rows = []; int remainingMarks = 40000;
        foreach (var activation in WorldAnimationActivations()) AddEntry(activation.Entry, activation.Path, 0, true);
        if (rows.Count >= AnimationStructurePlan.MaximumDependencyRecords || remainingMarks == 0)
            rows.Add(new("source:limit", "Structural display limit reached") { Detail = "Collapse branches to display other records; all records remain available through paged structure inspection." });
        rows.AddRange(WorldAnimationDropRows(target.Entry));
        return rows;

        void AddEntry(int entry, string path, int depth, bool root = false)
        {
            if (rows.Count >= AnimationStructurePlan.MaximumDependencyRecords) return;
            string id = "entry:" + entry + ":" + path;
            bool expanded = root ? !worldAnimationCollapsed.Contains(id) : worldAnimationExpanded.Contains(id);
            var value = plan.Entry(entry);
            Add(new(id, value.Name + $" · #{entry}")
            {
                Depth = depth, IsGroup = true, Expandable = true, Expanded = expanded,
                GuidesShown = root && path.Length == 0 ? shownAnimationPaths.Contains(target.Definition) : null,
                Detail = $"Bound root: {value.Root}; source structure, no execution implied"
            }, new(entry, path));
            if (!expanded) return;
            if (root && WorldAnimationActivations().FirstOrDefault(s => s.Path == path) is { } start)
                Add(new("activation:" + start.Id, "Start animation")
                {
                    Depth = depth + 1, ActivationId = start.Id,
                    TimingLabel = $"Frame {AnimationTimelineFrames.Frame(start.Seconds):0.###} · {start.Seconds:0.###} s",
                    Detail = "Preview activation on the shared timeline; drag to change start time",
                    Marks = [new("activation:" + start.Id, WorldAnimationTimelineMarkKind.Instant, start.Seconds)]
                }, new(entry, path));
            int requested = worldAnimationPageCounts.GetValueOrDefault(id, 100);
            for (int offset = 0; offset < requested && rows.Count < AnimationStructurePlan.MaximumDependencyRecords; offset += 100)
            {
                var page = plan.Sequences(entry, offset);
                foreach (var sequence in page.Rows)
                {
                    if (rows.Count >= AnimationStructurePlan.MaximumDependencyRecords) break;
                    string sequenceId = id + ":sequence:" + sequence.Id;
                    bool opened = worldAnimationExpanded.Contains(sequenceId) || root && !worldAnimationCollapsed.Contains(sequenceId);
                    Add(new(sequenceId, sequence.Name.Length == 0 ? (sequence.Cleanup ? "Cleanup" : "Sequence") : sequence.Name)
                    {
                        Depth = depth + 1, IsGroup = true, Expandable = true, Expanded = opened,
                        TimingLabel = sequence.Cleanup ? "Cleanup phase" : sequence.Waiting ? "Waiting for release" : "Concurrent sequence",
                        Detail = sequence.Source?.IgnoredStartTime == true ? "Sequence @START_TIME is ignored by the game" : "Source order; separate animation, sequence and event clocks"
                    }, new(entry, path, sequence.Id));
                    if (opened) AddEvents(entry, path, sequence, sequenceId, depth + 2);
                }
                if (page.NextOffset == null) break;
                if (page.NextOffset >= requested) AddMore(id, entry, path, depth + 1, page.Total - page.NextOffset.Value);
            }
        }
        void AddEvents(int entry, string path, AnimationStructureSequence sequence, string sequenceId, int depth)
        {
            int requested = worldAnimationPageCounts.GetValueOrDefault(sequenceId, 100);
            for (int offset = 0; offset < requested && rows.Count < AnimationStructurePlan.MaximumDependencyRecords; offset += 100)
            {
                var page = plan.Events(entry, sequence.Id, offset, parentPath: path);
                foreach (var ev in page.Rows)
                {
                    if (rows.Count >= AnimationStructurePlan.MaximumDependencyRecords) break;
                    string id = sequenceId + ":event:" + ev.Id;
                    bool expanded = worldAnimationExpanded.Contains(id);
                    Add(new(id, ev.Call is { } targetCall ? "→ " + targetCall.TargetName : ev.Name)
                    {
                        Depth = depth, Expandable = ev.Call?.TargetEntry != null || ev.KeyframeCount > 0, Expanded = expanded,
                        Included = ev.Call != null ? worldAnimationConfiguration.Includes(ev.Call.Path) : null,
                        InclusionEnabled = ev.Call != null && CanIncludeWorldAnimationCall(target.Entry, path),
                        InclusionReason = "Include this callsite in preview; parent calls must also be included",
                        CanEditTiming = ev.Source?.TimingFieldId != null && ev.Source.ReadOnlyReason == null,
                        TimingLabel = $"{ClockLabel(ev.Clock)} · frame {AnimationTimelineFrames.Frame(ev.Threshold):0.###} ({ev.Threshold:G9} s)",
                        IsSourceClock = true, StartThreshold = ev.Threshold,
                        Marks = [new(id + ":start", WorldAnimationTimelineMarkKind.Authored, ev.Threshold, Label: "Event start threshold")],
                        Detail = ev.Call is { } call ? $"{ev.Name} · {call.TargetName} · {(call.WaitForCompletion ? "waits for child completion; excluded call blocks this sequence" : "does not wait; excluded call is omitted")}" : ev.Diagnostic ?? ev.Support
                    }, new(entry, path, sequence.Id, ev.Id, ev.Call));
                    if (expanded && ev.Call?.TargetEntry is int child) AddEntry(child, ev.Call.Path, depth + 1);
                    if (expanded && ev.KeyframeCount > 0) AddKeys(entry, path, sequence.Id, ev.Id, id, depth + 1);
                }
                if (page.NextOffset == null) break;
                if (page.NextOffset >= requested) AddMore(sequenceId, entry, path, depth, page.Total - page.NextOffset.Value);
            }
        }
        void AddKeys(int entry, string path, Guid sequence, Guid ev, string eventId, int depth)
        {
            int requested = Math.Min(worldAnimationPageCounts.GetValueOrDefault(eventId, 100), Math.Max(0, remainingMarks - 1));
            List<WorldAnimationTimelineMark> marks = [];
            int? more = null;
            for (int offset = 0; offset < requested; offset += 100)
            {
                var page = plan.Keyframes(entry, sequence, ev, offset, Math.Min(100, requested - offset));
                foreach (var key in page.Rows)
                {
                    string detail = key.End < key.Start ? "Reversed sample span" : key.End == key.Start ? "Instantaneous sample / cut" : "Motion segment";
                    marks.Add(new(eventId + ":sample:" + key.Index, WorldAnimationTimelineMarkKind.Keyframe, key.Start, key.End,
                        $"Key {key.Index} · {key.Start:G9}–{key.End:G9} s · {detail}", KeyIndex: key.Index));
                    if (key.Index == page.Total - 1 && key.End != key.Start)
                        marks.Add(new(eventId + ":end:" + key.Index, WorldAnimationTimelineMarkKind.Keyframe, key.End,
                            Label: $"Final boundary · {key.End:G9} s", KeyIndex: key.Index));
                }
                if (page.NextOffset == null) break;
                if (page.NextOffset >= requested) more = page.Total - page.NextOffset.Value;
            }
            var source = (target.Document.Document as SourceSceneSnapshot)?.AnimationProvenance?.FindEvent(ev);
            string label = source?.Script is { } script ? System.IO.Path.GetFileName(script) : "Motion keys";
            Add(new(eventId + ":track", label)
            {
                Depth = depth, IsSourceClock = true, NativeClockStart = 0,
                NativeClockEnd = marks.Count == 0 ? 1 : marks.Max(m => Math.Max(m.Start, m.End ?? m.Start)),
                Marks = marks, TimingLabel = $"Script clock · {source?.ScriptFrameRate:G9} fps",
                Detail = "Read-only motion keys. Positions use a 40 fps display; source times and script rate are preserved."
            }, new(entry, path, sequence, ev));
            if (more is int remaining) AddMore(eventId, entry, path, depth, remaining);
        }
        void AddMore(string key, int entry, string path, int depth, int remaining)
            => Add(new("more:" + key, $"Show next page · {remaining} more") { Depth = depth, Detail = "Load at most 100 source records" }, new(entry, path, MoreKey: key));
        void Add(WorldAnimationTimelineRow row, WorldAnimationRowSource source)
        {
            if (rows.Count >= AnimationStructurePlan.MaximumDependencyRecords) return;
            if (row.Marks.Count > remainingMarks) { remainingMarks = 0; return; }
            remainingMarks -= row.Marks.Count;
            rows.Add(row); worldAnimationRowSources[row.Id] = source;
        }
    }

    private IReadOnlyList<WorldAnimationTimelineRow> WorldAnimationRunRows()
    {
        var frame = worldAnimationSession?.CurrentFrame ?? worldAnimationSession?.LastFrame;
        if (frame == null) return [];
        List<WorldAnimationTimelineRow> rows = [];
        bool stale = worldAnimationSession?.CurrentFrame == null;
        string run = (stale ? worldAnimationSession?.LastRunId : worldAnimationSession?.RunId)?.ToString("N") ?? "unavailable";
        if (stale) rows.Add(new("run:" + run + ":stale", "Stale evidence — stopped run") { IsGroup = true, Detail = "This evidence does not drive the restored world." });
        if (frame.TraceDropped > 0) rows.Add(new("run:" + run + ":truncated", $"{frame.TraceDropped} older occurrences discarded") { Detail = "Retained observations are incomplete", Marks = [new(run + ":discarded", WorldAnimationTimelineMarkKind.Truncation, 0)] });
        var traces = frame.Trace.GroupBy(t => (t.Instance, t.Sequence)).ToDictionary(g => g.Key, g => g.ToArray());
        var observedInstances = frame.Trace.GroupBy(t => t.Instance).ToDictionary(g => g.Key, g => g.ToArray());
        var liveInstances = frame.Instances.ToDictionary(i => i.Instance);
        foreach (long instanceId in liveInstances.Keys.Union(observedInstances.Keys).Order())
        {
            liveInstances.TryGetValue(instanceId, out var instance);
            var observed = observedInstances.GetValueOrDefault(instanceId) ?? [];
            int entry = instance?.Entry ?? observed[0].Entry;
            string path = instance?.Path ?? observed[0].InstancePath;
            string name = instance?.Name ?? worldAnimationSession!.Context.Package.Entries[entry].Name;
            string id = "run:" + run + ":" + instanceId;
            // Historical trace coverage is not a lifetime or an inferred child-completion time.
            rows.Add(new(id, name + $" · instance {instanceId}")
            {
                IsGroup = true, Detail = $"{(instance == null ? "Retained observations; instance retired" : instance.Finished ? "Finished" : instance.Cleanup ? "Cleanup" : "Runtime")} · {path}",
                Marks = instance is { Finished: false } ? [new(id, WorldAnimationTimelineMarkKind.Running, instance.StartedAt, frame.Time)] : []
            });
            worldAnimationRowSources[id] = new(entry, path);
            var liveSequences = frame.Sequences.Where(s => s.Instance == instanceId).ToDictionary(s => s.Sequence);
            foreach (Guid sequenceIdValue in liveSequences.Keys.Union(observed.Select(t => t.Sequence)))
            {
                liveSequences.TryGetValue(sequenceIdValue, out var sequence);
                string sequenceId = id + ":" + sequenceIdValue;
                var events = traces.GetValueOrDefault((instanceId, sequenceIdValue)) ?? [];
                var authoredSequence = worldAnimationSession!.Context.Package.Entries[entry].AllSequences.FirstOrDefault(s => s.Id == sequenceIdValue);
                string sequenceName = sequence?.Name ?? authoredSequence?.Name ?? "Retired sequence";
                Guid? activeEvent = sequence?.State == "Running" && instance is { Finished: false } ? authoredSequence?.Events.ElementAtOrDefault(sequence.EventIndex)?.Id : null;
                long? activeOccurrence = activeEvent.HasValue ? events.LastOrDefault(e => e.Event == activeEvent && e.End == null)?.Occurrence : null;
                rows.Add(new(sequenceId, sequenceName)
                {
                    Depth = 1, TimingLabel = sequence?.State ?? "Retained observations", Detail = sequence == null ? "Observed occurrences retained after the scheduler released this sequence."
                        : $"Animation {sequence.AnimationElapsed:0.###}; sequence {sequence.SequenceElapsed:0.###}; event {sequence.EventElapsed:0.###} s · iteration {sequence.Iteration}",
                    Marks = events.Select(e =>
                    {
                        bool active = e.Occurrence == activeOccurrence;
                        bool incomplete = e.End == null && !active;
                        var kind = e.Disposition == "excluded_wait" || e.Status.Contains("excluded", StringComparison.OrdinalIgnoreCase) && e.Status.Contains("wait", StringComparison.OrdinalIgnoreCase) ? WorldAnimationTimelineMarkKind.Blocked
                            : e.ChildEntry != null ? WorldAnimationTimelineMarkKind.Call : e.End == null ? active ? WorldAnimationTimelineMarkKind.Running : WorldAnimationTimelineMarkKind.Truncation
                            : e.End > e.Start ? WorldAnimationTimelineMarkKind.Interval : WorldAnimationTimelineMarkKind.Instant;
                        return new WorldAnimationTimelineMark("occurrence:" + run + ":" + e.Occurrence, kind, e.Start,
                            incomplete ? e.Start : active && e.End == null ? frame.Time : e.End,
                            e.Name + " · " + e.Status + (incomplete ? " · completion not observed" : active ? " · running at observed time" : ""), e.Occurrence);
                    }).ToArray()
                });
                worldAnimationRowSources[sequenceId] = new(entry, path, sequenceIdValue);
            }
        }
        return rows;
    }

    private async Task SelectWorldAnimationTimelineRowAsync(WorldAnimationTimelineRow row)
    {
        if (row.ActivationId.HasValue) { ShowAnimationRightTab("scenario"); return; }
        if (row.Id.StartsWith("drop:", StringComparison.Ordinal)) { await SelectWorldAnimationDropAsync(row, CancellationToken.None); return; }
        if (!worldAnimationRowSources.TryGetValue(row.Id, out var source)) return;
        if (source.MoreKey is { } more)
        {
            worldAnimationPageCounts[more] = Math.Min(AnimationStructurePlan.MaximumDependencyRecords, worldAnimationPageCounts.GetValueOrDefault(more, 100) + 100);
            RefreshWorldAnimationTimeline(); return;
        }
        await WorldAnimationSelectSourceAsync(source.Sequence, source.Event, CancellationToken.None, source.Entry, source.Path, preserveTimeline: true);
        // The graph shows the same selection.
        if (animationGraphLayout is { } layout && InAnimationMode)
        {
            string? id = source.Event != Guid.Empty ? layout.Graph.RowOf(source.Entry, source.Event)?.Id : source.Sequence != Guid.Empty ? AnimationGraph.SequenceId(source.Entry, source.Sequence) : null;
            if (id != null && (layout.Item(id) != null || layout.Node(id) != null)) { animationGraphSelection = id; GraphView.Select(id); }
        }
    }

    private async Task WorldAnimationSelectSourceAsync(Guid sequence, Guid ev, CancellationToken token, int? entry = null, string path = "", bool preserveTimeline = false)
    {
        RequireNoDrafts(); var target = WorldAnimationTargetNow ?? throw new StudioCommandException("not_ready", "Select an animation binding first.");
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

    private static string ClockLabel(byte mode) => mode switch { 0 => "Immediate", 1 => "Animation clock", 2 => "Sequence clock", 3 => "Event clock", _ => $"Clock {mode}" };
}
