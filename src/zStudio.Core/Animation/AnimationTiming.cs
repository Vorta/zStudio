using System.Globalization;

namespace zStudio.Core.Animation;

/// <summary>A Timing row: the Starts lane, an instance group (one binding, or one definition's bindings reached the same way) or a sequence lane.</summary>
public enum AnimationTimingLaneKind { Starts, Group, Sequence }

/// <summary>
/// What a mark shows (design §9, "Marks"): an executed instant or interval, a running event, an evaluated condition, events a
/// branch skipped, an excluded, duplicate, refused or unresolved call, a blocked wait, a release, a loop pass or a refused start.
/// </summary>
public enum AnimationTimingMarkKind { Executed, Interval, Running, Condition, Skipped, Excluded, Duplicate, Blocked, Release, Loop, Refused, Unresolved }

/// <summary>A lane's state over a span: waiting for release or a threshold, paused by a gate, blocked, stopped, complete, unavailable, or a group's binding state.</summary>
public enum AnimationTimingBandKind { WaitingForRelease, WaitingForThreshold, Paused, Blocked, Stopped, Complete, Unavailable, Binding }

/// <summary>One scenario start on the Starts lane: its binding, time, whether it is the primary, and what the run did with it.</summary>
public sealed record AnimationTimingStart(Guid Id, int Entry, double Seconds, string Label, bool Primary)
{
    /// <summary>The start ran only because the scenario's prerequisite rule ignored unmet prerequisites (the caution dot).</summary>
    public bool PrerequisitesIgnored { get; init; }
    /// <summary>Why the run refused the start, when it did ("prerequisites 0 of 5").</summary>
    public string? Refused { get; init; }
}

/// <summary>A lane's state from <see cref="Start"/> to <see cref="End"/>; <see cref="Open"/> when it still holds at the frame's time.</summary>
public sealed record AnimationTimingBand(AnimationTimingBandKind Kind, double Start, double End, string Label)
{
    public bool Open { get; init; }
    /// <summary>The event the band waits at, when it waits at one.</summary>
    public Guid? Event { get; init; }
    public long Instance { get; init; }
}

/// <summary>
/// A threshold bracket: from the observed anchor (the instance's start, the sequence's start or the previous event's completion)
/// to anchor + threshold, in the event's own clock. <see cref="Gap"/> names why the occurrence came later, where it is known.
/// </summary>
public sealed record AnimationTimingBracket(byte Clock, double Threshold, double Anchor, string Label)
{
    public double End => Anchor + Threshold;
    public string? Gap { get; init; }
}

/// <summary>One mark on a lane, from the run's evidence; <see cref="Occurrence"/> is the trace occurrence (0 for a refused start).</summary>
public sealed record AnimationTimingMark(string Id, AnimationTimingMarkKind Kind, double Start, string Label)
{
    public double? End { get; init; }
    public int Entry { get; init; }
    public Guid Sequence { get; init; }
    public Guid Event { get; init; }
    public byte Type { get; init; }
    public AnimationEventFamily Family { get; init; } = AnimationEventFamily.State;
    public long Instance { get; init; }
    public long Occurrence { get; init; }
    /// <summary>A condition's outcome and whether the scenario forced it.</summary>
    public bool? Value { get; init; }
    public bool Forced { get; init; }
    /// <summary>Events a skipped mark stands for.</summary>
    public int Count { get; init; } = 1;
    /// <summary>The Log's result ("launched", "false · game", "released mainsplash", "until 21.500 s").</summary>
    public string Result { get; init; } = "";
    /// <summary>A release's released lane, and when it released it.</summary>
    public string? TargetLane { get; init; }
    public AnimationTimingBracket? Bracket { get; init; }
}

/// <summary>One Timing row. A group's <see cref="Members"/> are its bindings; a sequence lane's <see cref="Sequences"/> are its sequence in each.</summary>
public sealed record AnimationTimingLane(string Id, AnimationTimingLaneKind Kind, string Label, int Depth)
{
    /// <summary>The group a lane belongs to, or a group's parent group.</summary>
    public string? Group { get; init; }
    public IReadOnlyList<int> Members { get; init; } = [];
    public IReadOnlyList<long> Instances { get; init; } = [];
    public IReadOnlyList<AnimationTimingSequence> Sequences { get; init; } = [];
    public int SequenceIndex { get; init; } = -1;
    public bool Cleanup { get; init; }
    public bool WaitsForRelease { get; init; }
    /// <summary>A group shows its lanes (top-level groups are open, called ones closed, unless changed).</summary>
    public bool Expanded { get; init; }
    public int LaneCount { get; init; }
    public double? Started { get; init; }
    public double? Ended { get; init; }
    /// <summary>A group's binding state ("running · invalidated"); a lane's state at the frame's time.</summary>
    public string State { get; init; } = "";
    public AnimationSequenceRunState? Run { get; init; }
    /// <summary>The instance path of the group's first instance (the scenario start's path for a top-level group).</summary>
    public string Path { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public IReadOnlyList<AnimationTimingMark> Marks { get; init; } = [];
    public IReadOnlyList<AnimationTimingBand> Bands { get; init; } = [];
    public IReadOnlyList<AnimationTimingStart> Starts { get; init; } = [];
}

public sealed record AnimationTimingSequence(int Entry, Guid Sequence);

/// <summary>One Log row (newest last): Time · Instance · Sequence · Event · Result, and the lane and mark it stands for.</summary>
public sealed record AnimationTimingLogRow(double Time, string Instance, string Sequence, string Event, string Result, string Lane, string Mark)
{
    public long InstanceId { get; init; }
    public long Occurrence { get; init; }
    public int Entry { get; init; }
    public Guid EventId { get; init; }
}

/// <summary>What a projection needs besides the frame: the scenario's starts, the map's grouping and which groups are open.</summary>
public sealed record AnimationTimingOptions
{
    public IReadOnlyList<AnimationScheduledStart> Starts { get; init; } = [];
    /// <summary>Groups the instances by definition as the Structure graph does; without it, by animation name.</summary>
    public AnimationGraphIndex? Index { get; init; }
    public AnimationSourceProvenance? Provenance { get; init; }
    /// <summary>Called groups opened, and top-level groups closed, against their defaults.</summary>
    public IReadOnlySet<string> Toggled { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// The Timing view's projection of one run frame (design §9): the Starts lane, instance groups (scenario starts, then the
/// instances their calls reached, nested under their caller and grouped by definition as the graph groups them), one lane
/// per sequence with its state bands and marks, threshold brackets from observed anchors, and the Log. Pure and bounded by
/// the frame's own evidence; the GUI and MCP animation_timing read it and never recompute the run.
/// </summary>
public sealed class AnimationTiming
{
    public const int MaximumLanes = 4096, MaximumBandsPerLane = 2000;
    private readonly List<AnimationTimingLane> lanes = [], all = [];
    private readonly Dictionary<string, AnimationTimingLane> byId = new(StringComparer.Ordinal);

    public double Time { get; }
    /// <summary>The rows the view shows, in order (lanes of closed groups are left out).</summary>
    public IReadOnlyList<AnimationTimingLane> Lanes => lanes;
    /// <summary>Every row, closed groups' lanes included.</summary>
    public IReadOnlyList<AnimationTimingLane> AllLanes => all;
    public IReadOnlyList<AnimationTimingLogRow> Log { get; }
    public long Retained { get; }
    public long Discarded { get; }
    /// <summary>The earliest and latest evidence (Fit run).</summary>
    public double? RunStart { get; }
    public double? RunEnd { get; }
    public int MarkCount { get; }
    public bool Truncated { get; }

    public AnimationTimingLane? Lane(string id) => byId.GetValueOrDefault(id);

    private sealed class Group(string key, string? parent, int depth)
    {
        public string Key = key; public string? Parent = parent; public int Depth = depth;
        public AnimationScheduledStart? Start;
        public readonly List<long> Instances = [];
        public readonly SortedSet<int> Entries = [];
        public readonly List<Group> Children = [];
        public double First = double.PositiveInfinity;
    }
    private sealed record InstanceInfo(long Id, int Entry, string Path, long? Parent, double? Started, bool Live, bool Finished, bool Cleanup);
    private sealed record EventInfo(AnimationEntry Entry, AnimationSequence Sequence, int Index, AnimationEvent Event);

    private readonly AnimationPackage package;
    private readonly AnimationTimingOptions options;
    private readonly Dictionary<Guid, EventInfo> events = [];
    private readonly Dictionary<Guid, AnimationSequence> sequenceOf = [];
    private readonly Dictionary<Guid, string> phrases = [];

    private AnimationTiming(AnimationFrame frame, AnimationPackage package, AnimationTimingOptions options, CancellationToken token)
    {
        this.package = package; this.options = options; Time = frame.Time;
        // ---- instances and their parents
        Dictionary<long, InstanceInfo> instances = [];
        foreach (var live in frame.Instances)
            instances[live.Instance] = new(live.Instance, live.Entry, live.Path, live.ParentInstance, live.StartedAt, !live.Finished, live.Finished, live.Cleanup);
        Dictionary<long, long> launchedBy = [];
        Dictionary<long, double> firstSeen = [];
        foreach (var t in frame.Trace)
        {
            if (t.ChildInstance is long child && t.Disposition == "launched") launchedBy.TryAdd(child, t.Instance);
            firstSeen.TryAdd(t.Instance, t.Start);
            if (!instances.ContainsKey(t.Instance) && t.Entry >= 0 && t.Entry < package.Entries.Count)
                instances[t.Instance] = new(t.Instance, t.Entry, t.InstancePath, null, null, false, true, false);
        }
        Dictionary<long, double> finished = [];
        Dictionary<(long, Guid), List<AnimationSequencePhase>> phases = [];
        Dictionary<(long, Guid), List<AnimationSequencePhase>> released = [];
        foreach (var phase in frame.Phases)
        {
            token.ThrowIfCancellationRequested();
            firstSeen.TryAdd(phase.Instance, phase.Time);
            if (phase.State == AnimationSequencePhaseState.Finished) { finished[phase.Instance] = phase.Time; continue; }
            if (!phases.TryGetValue((phase.Instance, phase.Sequence), out var list)) phases[(phase.Instance, phase.Sequence)] = list = [];
            list.Add(phase);
            if (phase.ReleasedBy is Guid by && phase.ReleasedByInstance is long releaser)
            {
                if (!released.TryGetValue((releaser, by), out var targets)) released[(releaser, by)] = targets = [];
                targets.Add(phase);
            }
        }
        // ---- the starts, each a top-level group
        var starts = options.Starts.Count > 0 ? options.Starts.ToArray() : [];
        Dictionary<string, Group> groups = new(StringComparer.Ordinal);
        List<Group> roots = [];
        foreach (var start in starts.OrderBy(s => s.Id == Guid.Empty ? 0 : 1).ThenBy(s => s.Seconds).ThenBy(s => s.Id))
        {
            var group = new Group(StartKey(start), null, 0) { Start = start, First = start.Seconds };
            group.Entries.Add(start.Entry);
            groups[group.Key] = group; roots.Add(group);
        }
        AnimationScheduledStart? StartOf(string path)
        {
            AnimationScheduledStart? found = null;
            foreach (var start in starts)
                if (start.Id != Guid.Empty && (path == start.Path || path.StartsWith(start.Path + "/", StringComparison.Ordinal))) found = start;
            return found ?? starts.FirstOrDefault(s => s.Id == Guid.Empty);
        }
        Dictionary<long, Group?> groupOf = [];
        Group? GroupOf(long id, int depth)
        {
            if (groupOf.TryGetValue(id, out var known)) return known;
            if (depth > 64 || !instances.TryGetValue(id, out var info)) return null;
            groupOf[id] = null; // a cycle in parents resolves to no group
            Group? parent = null;
            long? parentId = info.Parent ?? (launchedBy.TryGetValue(id, out long by) ? by : null);
            if (parentId is long p && instances.ContainsKey(p)) parent = GroupOf(p, depth + 1);
            Group? result;
            if (parent == null && StartOf(info.Path) is { } start && groups.TryGetValue(StartKey(start), out var top) && top.Entries.Contains(info.Entry) && top.Instances.All(i => instances[i].Entry == info.Entry))
                result = top;
            else
            {
                var owner = parent ?? (StartOf(info.Path) is { } s && groups.TryGetValue(StartKey(s), out var g) ? g : null);
                string key = (owner?.Key ?? "orphan") + ">" + (options.Index?.Group(info.Entry) ?? "n:" + package.Entries[info.Entry].Name);
                if (!groups.TryGetValue(key, out result))
                {
                    result = new(key, owner?.Key, (owner?.Depth ?? -1) + 1);
                    groups[key] = result;
                    if (owner != null) owner.Children.Add(result); else roots.Add(result);
                }
            }
            result.Instances.Add(id); result.Entries.Add(info.Entry);
            double first = info.Started ?? firstSeen.GetValueOrDefault(id, double.PositiveInfinity);
            result.First = Math.Min(result.First, first);
            groupOf[id] = result;
            return result;
        }
        foreach (long id in instances.Keys.Order()) { token.ThrowIfCancellationRequested(); GroupOf(id, 0); }
        // The events and sequences of every binding the run or the starts touch, by identity.
        foreach (int entry in instances.Values.Select(i => i.Entry).Concat(starts.Select(s => s.Entry)).Distinct())
        {
            if (entry < 0 || entry >= package.Entries.Count) continue;
            foreach (var sequence in package.Entries[entry].AllSequences)
            {
                sequenceOf.TryAdd(sequence.Id, sequence);
                for (int i = 0; i < sequence.Events.Count; i++) events.TryAdd(sequence.Events[i].Id, new(package.Entries[entry], sequence, i, sequence.Events[i]));
            }
        }
        // A top-level group's own instances come first: the start's binding, in its start order.
        // ---- the lanes of every group, and where each instance's sequence goes
        Dictionary<(long, Guid), string> laneOf = [];
        Dictionary<string, Builder> builders = new(StringComparer.Ordinal);
        foreach (var group in groups.Values)
        {
            int representative = group.Entries.Count > 0 ? group.Entries.Min : 0;
            if (representative <= 0 || representative >= package.Entries.Count) continue;
            int count = package.Entries[representative].AllSequences.Count();
            for (int index = 0; index < count; index++)
            {
                string id = LaneId(group.Key, index);
                var builder = builders[id] = new(id);
                foreach (int member in group.Entries)
                {
                    var sequence = member > 0 && member < package.Entries.Count ? package.Entries[member].AllSequences.ElementAtOrDefault(index) : null;
                    if (sequence != null) builder.Sequences.Add(new(member, sequence.Id));
                }
            }
            foreach (long instance in group.Instances)
            {
                var entry = package.Entries[instances[instance].Entry];
                int index = 0;
                foreach (var sequence in entry.AllSequences) laneOf[(instance, sequence.Id)] = LaneId(group.Key, index++);
            }
        }
        // ---- marks from the trace (in occurrence order), skipped spans and refused starts
        var conditions = frame.Conditions.GroupBy(c => (c.Occurrence, c.Event)).ToDictionary(g => g.Key, g => g.Last());
        HashSet<(long, Guid)> shownConditions = [];
        Dictionary<(long, Guid), AnimationTrace> previous = [];
        Dictionary<long, AnimationTrace> occurrences = [];
        Dictionary<(long, Guid), double> passStart = [];
        List<AnimationTimingLogRow> log = [];
        int marks = 0;
        double runStart = double.PositiveInfinity, runEnd = double.NegativeInfinity;
        foreach (var t in frame.Trace)
        {
            token.ThrowIfCancellationRequested();
            if (!laneOf.TryGetValue((t.Instance, t.Sequence), out string? laneId) || !builders.TryGetValue(laneId, out var lane)) continue;
            var info = Event(t.Event);
            byte type = info?.Event.Type ?? 0;
            var key = (t.Instance, t.Sequence);
            occurrences[t.Occurrence] = t;
            previous.TryGetValue(key, out var before); previous[key] = t;
            if (info?.Index == 0) passStart[key] = t.Start;
            if (type is 32 or 34) continue; // ELSE and ENDIF mark structure, not actions
            string label = info == null ? AnimationPhrases.Safe(t.Name) : Phrase(info);
            double end = t.End ?? (finished.TryGetValue(t.Instance, out double done) ? done : frame.Time);
            AnimationTimingMarkKind kind = t.Disposition switch
            {
                "excluded" => AnimationTimingMarkKind.Excluded,
                "excluded_wait" => AnimationTimingMarkKind.Blocked,
                "duplicate" => AnimationTimingMarkKind.Duplicate,
                "refused" => AnimationTimingMarkKind.Refused,
                "unresolved" or "unavailable" => AnimationTimingMarkKind.Unresolved,
                _ when type is 31 or 33 => AnimationTimingMarkKind.Condition,
                _ when type == 22 => AnimationTimingMarkKind.Release,
                _ when type == 30 => AnimationTimingMarkKind.Loop,
                _ when t.End == null => AnimationTimingMarkKind.Running,
                _ when t.End > t.Start + 1e-9 => AnimationTimingMarkKind.Interval,
                _ => AnimationTimingMarkKind.Executed,
            };
            bool? value = null; bool forced = false;
            if (kind == AnimationTimingMarkKind.Condition && conditions.TryGetValue((t.Occurrence, t.Event), out var outcome)) { value = outcome.Value; forced = outcome.Forced; shownConditions.Add((t.Occurrence, t.Event)); }
            string? target = null; string result;
            if (kind == AnimationTimingMarkKind.Release && released.TryGetValue((t.Instance, t.Event), out var targets)
                && targets.Where(p => p.Time >= t.Start - 1e-6).OrderBy(p => p.Time).FirstOrDefault() is { } releasedPhase)
                laneOf.TryGetValue((releasedPhase.Instance, releasedPhase.Sequence), out target);
            result = kind switch
            {
                AnimationTimingMarkKind.Condition => value is bool v ? (v ? "true" : "false") + (forced ? " · forced" : " · game") : "not evaluated · an earlier branch ran",
                AnimationTimingMarkKind.Release => target != null && builders.TryGetValue(target, out var released2) ? "released " + released2.Name(package) : "nothing waited",
                AnimationTimingMarkKind.Excluded => "excluded",
                AnimationTimingMarkKind.Blocked => "excluded · waits",
                AnimationTimingMarkKind.Duplicate => "duplicate",
                AnimationTimingMarkKind.Refused => "refused · " + t.Status.Replace("Start refused: ", "", StringComparison.Ordinal),
                AnimationTimingMarkKind.Unresolved => t.Status.Length > 0 ? AnimationPhrases.Safe(t.Status) : "unresolved",
                AnimationTimingMarkKind.Running => "running",
                AnimationTimingMarkKind.Interval => "until " + AnimationSeconds.Format(end),
                _ => t.Disposition == "launched" ? "launched" : t.Disposition == "applied" ? "applied" : "done",
            };
            AnimationTimingBracket? bracket = null;
            if (info != null && info.Event.Bytes.Length >= 12 && info.Event.Threshold > 0 && double.IsFinite(info.Event.Threshold))
            {
                byte clock = info.Event.StartMode;
                double? anchor = clock switch
                {
                    1 => instances.TryGetValue(t.Instance, out var owner) ? owner.Started ?? firstSeen.GetValueOrDefault(t.Instance, double.NaN) : null,
                    2 => info.Index > 0 && passStart.TryGetValue(key, out double started) ? started : null,
                    3 => info.Index > 0 ? before?.End : PhaseStart(phases, key, AnimationSequencePhaseState.WaitingForThreshold, t.Start),
                    _ => null,
                };
                if (anchor is double a0 && Math.Max(0, a0) is var a && double.IsFinite(a) && a <= t.Start + 1e-6)
                {
                    string words = clock switch { 2 => " after the sequence starts", 3 => " after previous", _ => " after the animation starts" };
                    string? gap = null;
                    double due = a + info.Event.Threshold;
                    if (t.Start > due + AnimationSeconds.Tick + 1e-6)
                        gap = Cause(phases, key, due, t.Start) ?? (clock != 3 && before?.End is double busy && busy > due + 1e-6 ? "previous event running" : null);
                    bracket = new(clock, info.Event.Threshold, a, "≥ " + AnimationSeconds.Short(info.Event.Threshold) + words) { Gap = gap };
                }
            }
            var mark = new AnimationTimingMark(laneId + "#o" + t.Occurrence.ToString(CultureInfo.InvariantCulture), kind, t.Start, label)
            {
                End = kind is AnimationTimingMarkKind.Interval or AnimationTimingMarkKind.Running or AnimationTimingMarkKind.Blocked ? end : t.End,
                Entry = t.Entry, Sequence = t.Sequence, Event = t.Event, Type = type, Family = AnimationPhrases.Family(type), Instance = t.Instance,
                Occurrence = t.Occurrence, Value = value, Forced = forced, Result = result, TargetLane = target, Bracket = bracket,
            };
            lane.Marks.Add(mark); marks++;
            runStart = Math.Min(runStart, t.Start); runEnd = Math.Max(runEnd, mark.End ?? t.Start);
            log.Add(new(t.Start, BindingLabel(t.Entry, t.Instance), lane.Name(package), label, result, laneId, mark.Id) { InstanceId = t.Instance, Occurrence = t.Occurrence, Entry = t.Entry, EventId = t.Event });
        }
        // An ELSEIF a false IF reached is evaluated inside the IF's dispatch: its outcome has no occurrence of its own.
        foreach (var condition in frame.Conditions)
        {
            if (shownConditions.Contains((condition.Occurrence, condition.Event)) || Event(condition.Event) is not { } info) continue;
            if (!laneOf.TryGetValue((condition.Instance, info.Sequence.Id), out string? laneId) || !builders.TryGetValue(laneId, out var lane)) continue;
            string label = Phrase(info), result = (condition.Value ? "true" : "false") + (condition.Forced ? " · forced" : " · game");
            var mark = new AnimationTimingMark(laneId + "#c" + condition.Occurrence.ToString(CultureInfo.InvariantCulture) + ":" + condition.Event.ToString("N")[..8], AnimationTimingMarkKind.Condition, condition.Time, label)
            {
                Entry = info.Entry.Index, Sequence = info.Sequence.Id, Event = condition.Event, Type = info.Event.Type, Family = AnimationEventFamily.Flow, Instance = condition.Instance,
                Occurrence = condition.Occurrence, Value = condition.Value, Forced = condition.Forced, Result = result,
            };
            lane.Marks.Add(mark); marks++;
            runStart = Math.Min(runStart, condition.Time); runEnd = Math.Max(runEnd, condition.Time);
            log.Add(new(condition.Time, BindingLabel(info.Entry.Index, condition.Instance), lane.Name(package), label, result, laneId, mark.Id) { InstanceId = condition.Instance, Occurrence = condition.Occurrence, Entry = info.Entry.Index, EventId = condition.Event });
        }
        foreach (var span in frame.Skipped)
        {
            token.ThrowIfCancellationRequested();
            if (!laneOf.TryGetValue((span.Instance, span.Sequence), out string? laneId) || !builders.TryGetValue(laneId, out var lane)) continue;
            var info = Event(span.First);
            int entry = instances.TryGetValue(span.Instance, out var owner) ? owner.Entry : info?.Entry.Index ?? 0;
            string first = info == null ? "events" : Phrase(info);
            string label = span.Count > 1 ? $"{first} … ({span.Count.ToString(CultureInfo.InvariantCulture)})" : first;
            string result = span.Reason == "taken" ? "skipped · branch taken" : "skipped · condition false";
            // The skip happens at the IF, ELSEIF or ELSE that decided it.
            double at = occurrences.TryGetValue(span.Occurrence, out var marker) && marker.Event == span.Marker ? marker.Start : span.Time;
            var mark = new AnimationTimingMark(laneId + "#k" + span.Occurrence.ToString(CultureInfo.InvariantCulture) + ":" + span.First.ToString("N")[..8], AnimationTimingMarkKind.Skipped, at, label)
            {
                Entry = entry, Sequence = span.Sequence, Event = span.First, Type = info?.Event.Type ?? 0, Family = AnimationPhrases.Family(info?.Event.Type ?? 0),
                Instance = span.Instance, Occurrence = span.Occurrence, Count = span.Count, Result = result,
            };
            lane.Marks.Add(mark); marks++;
            runStart = Math.Min(runStart, at); runEnd = Math.Max(runEnd, at);
            log.Add(new(at, BindingLabel(entry, span.Instance), lane.Name(package), label, result, laneId, mark.Id) { InstanceId = span.Instance, Occurrence = span.Occurrence, Entry = entry, EventId = span.First });
        }
        // Refused scenario starts sit on their group's row; refused calls already sit on the calling lane.
        Dictionary<string, List<AnimationTimingMark>> groupMarks = new(StringComparer.Ordinal);
        Dictionary<Guid, string> refusedStarts = [];
        foreach (var refusal in frame.Refusals.Where(r => r.ScenarioStart))
        {
            if (StartOf(refusal.InstancePath) is not { } start || start.Entry != refusal.Entry || !groups.TryGetValue(StartKey(start), out var group)) continue;
            string reason = AnimationRunOverlay.Refusal(refusal);
            refusedStarts[start.Id] = reason;
            string id = "group:" + group.Key + "#r" + refusal.Occurrence.ToString(CultureInfo.InvariantCulture) + ":" + refusal.Time.ToString("R", CultureInfo.InvariantCulture);
            if (!groupMarks.TryGetValue(group.Key, out var list)) groupMarks[group.Key] = list = [];
            var mark = new AnimationTimingMark(id, AnimationTimingMarkKind.Refused, refusal.Time, "Start " + Name(refusal.Entry))
            { Entry = refusal.Entry, Occurrence = refusal.Occurrence, Result = "refused · " + reason };
            list.Add(mark); marks++;
            log.Add(new(refusal.Time, BindingLabel(refusal.Entry, null), "", "Start " + Name(refusal.Entry), "refused · " + reason, "group:" + group.Key, id) { Occurrence = refusal.Occurrence, Entry = refusal.Entry });
        }
        HashSet<Guid> ignored = [];
        foreach (var check in frame.PrerequisiteStarts.Where(p => p.ScenarioStart && p.Outcome == "ignored"))
            if (StartOf(check.InstancePath) is { } start && start.Entry == check.Entry) ignored.Add(start.Id);
        // ---- bands from the phases
        foreach (var ((instance, sequence), list) in phases)
        {
            token.ThrowIfCancellationRequested();
            if (!laneOf.TryGetValue((instance, sequence), out string? laneId) || !builders.TryGetValue(laneId, out var lane)) continue;
            double stop = finished.TryGetValue(instance, out double done) ? done : frame.Time;
            var status = frame.Sequences.FirstOrDefault(s => s.Instance == instance && s.Sequence == sequence);
            for (int i = 0; i < list.Count; i++)
            {
                var phase = list[i];
                bool open = i == list.Count - 1;
                double end = open ? stop : list[i + 1].Time;
                if (end < phase.Time) end = phase.Time;
                var waitingAt = WaitingEvent(sequence, phase.EventIndex);
                switch (phase.State)
                {
                    case AnimationSequencePhaseState.WaitingForRelease:
                        {
                            string label = "waiting for release";
                            if (!open && list[i + 1].ReleasedBy is Guid by && Event(by) is { } releaser) label += " · released by " + AnimationPhrases.Safe(releaser.Sequence.Name) + " › " + Phrase(releaser);
                            else if (open && !finished.ContainsKey(instance) && NoReleaserLeft(frame, instance, sequence)) label = "waiting · no releaser left";
                            lane.Bands.Add(new(AnimationTimingBandKind.WaitingForRelease, phase.Time, end, label) { Open = open, Instance = instance });
                            break;
                        }
                    case AnimationSequencePhaseState.WaitingForThreshold when end > phase.Time + 1e-6:
                        {
                            // Phases have tick resolution: the wait ends where its event's occurrence started.
                            if (waitingAt != null && lane.Marks.FirstOrDefault(m => m.Instance == instance && m.Event == waitingAt.Event.Id && m.Start >= phase.Time - 1e-6 && m.Start <= end + 1e-6) is { } reached)
                            { end = reached.Start; open = false; }
                            if (end <= phase.Time + 1e-6) break;
                            string label = "waiting for threshold";
                            if (waitingAt != null && AnimationPhrases.ThresholdBadge(waitingAt.Event.StartMode, waitingAt.Event.Threshold) is { } badge)
                            {
                                double? due = !open ? end : status == null ? null : frame.Time + Math.Max(0, waitingAt.Event.Threshold - waitingAt.Event.StartMode switch { 2 => status.SequenceElapsed, 3 => status.EventElapsed, _ => status.AnimationElapsed });
                                label += " · " + badge + (due is double d ? " → " + AnimationSeconds.Format(d) : "");
                            }
                            lane.Bands.Add(new(AnimationTimingBandKind.WaitingForThreshold, phase.Time, end, label) { Open = open, Event = waitingAt?.Event.Id, Instance = instance });
                            break;
                        }
                    case AnimationSequencePhaseState.Paused:
                        lane.Bands.Add(new(AnimationTimingBandKind.Paused, phase.Time, end, "paused · " + (phase.PausedBy ?? "gate")) { Open = open, Instance = instance });
                        break;
                    case AnimationSequencePhaseState.Blocked:
                        {
                            string called = waitingAt != null && waitingAt.Event.Type is 19 or 24 ? AnimationPhrases.Safe(AnimationAudioDependencies.ChildName(waitingAt.Event)) : "";
                            lane.Bands.Add(new(AnimationTimingBandKind.Blocked, phase.Time, end, called.Length > 0 ? "blocked · waits for excluded call " + called : "blocked · waits for an excluded call") { Open = open, Event = waitingAt?.Event.Id, Instance = instance });
                            break;
                        }
                    case AnimationSequencePhaseState.Stopped:
                        lane.Bands.Add(new(AnimationTimingBandKind.Stopped, phase.Time, end, "stopped") { Open = open, Instance = instance });
                        break;
                    case AnimationSequencePhaseState.Complete:
                        lane.Bands.Add(new(AnimationTimingBandKind.Complete, phase.Time, phase.Time, "complete") { Instance = instance });
                        break;
                    case AnimationSequencePhaseState.Unavailable:
                        lane.Bands.Add(new(AnimationTimingBandKind.Unavailable, phase.Time, end, "unavailable") { Open = open, Instance = instance });
                        break;
                }
            }
        }
        // ---- the rows, depth first: a group, its lanes, then the groups its calls reached
        var startLane = new AnimationTimingLane("starts", AnimationTimingLaneKind.Starts, "Starts", 0)
        {
            Starts = starts.OrderBy(s => s.Seconds).ThenBy(s => s.Id == Guid.Empty ? 0 : 1).Select(s => new AnimationTimingStart(s.Id, s.Entry, s.Seconds,
                (s.Id == Guid.Empty ? "★ " : "") + Name(s.Entry), s.Id == Guid.Empty)
                { PrerequisitesIgnored = ignored.Contains(s.Id), Refused = refusedStarts.GetValueOrDefault(s.Id) }).ToArray(),
            State = $"{starts.Length.ToString(CultureInfo.InvariantCulture)} of {AnimationSchedule.MaximumStarts.ToString(CultureInfo.InvariantCulture)}",
            Tooltip = "The scenario's starts on the shared clock: drag one to move it (0.025 s; Shift 0.1 s; Ctrl free); right-click to remove it.",
        };
        Add(startLane, true);
        bool truncated = false;
        foreach (var root in roots.OrderBy(r => r.Start?.Id == Guid.Empty ? 0 : 1).ThenBy(r => r.Start?.Seconds ?? r.First).ThenBy(r => r.Key, StringComparer.Ordinal))
            Emit(root, true);
        Truncated = truncated;
        Log = log.OrderBy(r => r.Time).ThenBy(r => r.Occurrence).ToArray();
        Retained = frame.Trace.Count; Discarded = frame.TraceDropped;
        MarkCount = marks;
        if (double.IsFinite(runStart)) { RunStart = runStart; RunEnd = Math.Max(runEnd, frame.Time); }

        void Emit(Group group, bool visible)
        {
            if (all.Count >= MaximumLanes) { truncated = true; return; }
            bool top = group.Parent == null && group.Start != null;
            bool expanded = top ^ options.Toggled.Contains("group:" + group.Key);
            var members = group.Entries.Where(e => e > 0 && e < package.Entries.Count).ToArray();
            int laneCount = members.Length == 0 ? 0 : package.Entries[members[0]].AllSequences.Count();
            // A start the run refused (or has not reached) has no instance: nothing started.
            double? started = group.Instances.Count > 0 && double.IsFinite(group.First) ? group.First : null;
            var ended = group.Instances.Count > 0 && group.Instances.All(finished.ContainsKey) ? group.Instances.Max(i => finished[i]) : (double?)null;
            string state = GroupState(frame, members, group, refusedStarts);
            string label = GroupLabel(group, members);
            var row = new AnimationTimingLane("group:" + group.Key, AnimationTimingLaneKind.Group, label, group.Depth)
            {
                Group = group.Parent == null ? null : "group:" + group.Parent, Members = members, Instances = group.Instances.Order().ToArray(),
                Expanded = expanded, LaneCount = laneCount, Started = started, Ended = ended, State = state,
                Path = group.Start?.Path ?? (group.Instances.Count > 0 ? instances[group.Instances.Min()].Path : ""),
                Marks = groupMarks.GetValueOrDefault(group.Key) ?? [],
                Bands = started is double s && group.Instances.Count > 0 ? GroupBands(s, ended, frame.Time, state) : [],
                Tooltip = label + (started is double at ? " · started " + AnimationSeconds.Format(at) : " · not started") + (state.Length > 0 ? " · " + state : ""),
            };
            Add(row, visible);
            for (int index = 0; index < laneCount; index++)
            {
                if (all.Count >= MaximumLanes) { truncated = true; return; }
                if (!builders.TryGetValue(LaneId(group.Key, index), out var builder)) continue;
                Add(builder.Build(package, group, index, members, frame, instances, phases, finished), visible && expanded);
            }
            foreach (var child in group.Children.OrderBy(c => c.First).ThenBy(c => c.Key, StringComparer.Ordinal)) Emit(child, visible && expanded);
        }
        void Add(AnimationTimingLane lane, bool visible)
        {
            all.Add(lane); byId[lane.Id] = lane;
            if (visible) lanes.Add(lane);
        }
    }

    public static AnimationTiming Create(AnimationFrame frame, AnimationPackage package, AnimationTimingOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(package);
        return new(frame, package, options ?? new(), token);
    }

    /// <summary>An empty projection: the Starts lane and the starts' groups with their sequences, nothing run.</summary>
    public static AnimationTiming Empty(AnimationPackage package, AnimationTimingOptions options, CancellationToken token = default) =>
        new(new AnimationFrame(0, [], [], [], [], null, null, default, default, [], [], []), package, options, token);

    private static string StartKey(AnimationScheduledStart start) => "s:" + start.Id.ToString("N")[..12];
    private static string LaneId(string group, int index) => "lane:" + group + ":" + index.ToString(CultureInfo.InvariantCulture);

    private EventInfo? Event(Guid id) => events.GetValueOrDefault(id);
    private EventInfo? WaitingEvent(Guid sequence, int index) =>
        sequenceOf.TryGetValue(sequence, out var found) && index >= 0 && index < found.Events.Count ? Event(found.Events[index].Id) : null;
    private string Phrase(EventInfo info)
    {
        if (phrases.TryGetValue(info.Event.Id, out string? known)) return known;
        string? script = options.Provenance?.FindEvent(info.Event.Id)?.Script;
        string phrase = AnimationPhrases.Event(info.Entry, info.Event, script is null ? null : System.IO.Path.GetFileName(script));
        phrases[info.Event.Id] = phrase;
        return phrase;
    }
    private string Name(int entry) => entry > 0 && entry < package.Entries.Count ? AnimationPhrases.Safe(package.Entries[entry].Name) : "binding #" + entry.ToString(CultureInfo.InvariantCulture);
    private string BindingLabel(int entry, long? instance) => entry > 0 && entry < package.Entries.Count
        ? $"{Name(entry)} #{entry.ToString(CultureInfo.InvariantCulture)}" + (instance is long i ? " · " + i.ToString(CultureInfo.InvariantCulture) : "")
        : "binding #" + entry.ToString(CultureInfo.InvariantCulture);
    private string GroupLabel(Group group, IReadOnlyList<int> members)
    {
        if (members.Count == 0) return "Start";
        var first = package.Entries[members[0]];
        string name = members.Count > 1 ? AnimationPhrases.Safe(options.Index?.GroupName(options.Index.Group(members[0])) ?? first.Name) : AnimationPhrases.Safe(first.Name);
        if (group.Instances.Count > 1) return name + " ×" + group.Instances.Count.ToString(CultureInfo.InvariantCulture);
        return members.Count > 1 ? name + " · " + members.Count.ToString(CultureInfo.InvariantCulture)
            : name + " · " + AnimationPhrases.Safe(first.RootName) + " #" + first.Index.ToString(CultureInfo.InvariantCulture);
    }
    private static string GroupState(AnimationFrame frame, IReadOnlyList<int> members, Group group, Dictionary<Guid, string> refused)
    {
        if (group.Instances.Count == 0)
            return group.Start != null && refused.TryGetValue(group.Start.Id, out string? reason) ? "refused · " + reason : group.Start != null && frame.Time < group.Start.Seconds ? "not started" : "";
        var states = members.Select(m => frame.Bindings.FirstOrDefault(b => b.Entry == m)).Where(b => b != null)
            .GroupBy(b => AnimationRunOverlay.Label(b!.State, b.PausedBy).ToLowerInvariant()).ToArray();
        if (states.Length == 0) return "";
        if (states.Length == 1) return states[0].Key + (members.Count > 1 ? " ×" + states[0].Count().ToString(CultureInfo.InvariantCulture) : "");
        return string.Join(", ", states.OrderByDescending(s => s.Count()).Select(s => s.Key + " ×" + s.Count().ToString(CultureInfo.InvariantCulture)));
    }
    private static IReadOnlyList<AnimationTimingBand> GroupBands(double started, double? ended, double time, string state)
    {
        string running = state.Contains("invalidated", StringComparison.Ordinal) && state.StartsWith("running", StringComparison.Ordinal) ? "running · invalidated" : "running";
        if (ended is double end)
            return [new(AnimationTimingBandKind.Binding, started, end, running), new(AnimationTimingBandKind.Binding, end, Math.Max(end, time), state.Length > 0 ? state : "finished") { Open = true }];
        return [new(AnimationTimingBandKind.Binding, started, Math.Max(started, time), state.Length > 0 ? state : running) { Open = true }];
    }
    private static double? PhaseStart(Dictionary<(long, Guid), List<AnimationSequencePhase>> phases, (long, Guid) key, AnimationSequencePhaseState state, double before)
    {
        if (!phases.TryGetValue(key, out var list)) return null;
        double? found = null;
        foreach (var phase in list) { if (phase.Time > before + 1e-6) break; if (phase.State == state) found = phase.Time; }
        return found;
    }
    private static string? Cause(Dictionary<(long, Guid), List<AnimationSequencePhase>> phases, (long, Guid) key, double from, double to)
    {
        if (!phases.TryGetValue(key, out var list)) return null;
        AnimationSequencePhaseState? at = null;
        foreach (var phase in list)
        {
            if (phase.Time > to - 1e-6) break;
            if (phase.Time <= from + 1e-6) { at = phase.State; continue; }
            at ??= phase.State;
            if (phase.State is AnimationSequencePhaseState.WaitingForRelease or AnimationSequencePhaseState.Paused or AnimationSequencePhaseState.Blocked) { at = phase.State; break; }
        }
        return at switch
        {
            AnimationSequencePhaseState.WaitingForRelease => "waited for release",
            AnimationSequencePhaseState.Paused => "paused by a gate",
            AnimationSequencePhaseState.Blocked => "blocked by an excluded call",
            AnimationSequencePhaseState.Running => "previous event running",
            _ => null,
        };
    }
    /// <summary>Whether every event that releases this sequence in its instance is done (none can release it again).</summary>
    private bool NoReleaserLeft(AnimationFrame frame, long instance, Guid sequence)
    {
        var owner = frame.Instances.FirstOrDefault(i => i.Instance == instance);
        if (owner == null || owner.Entry <= 0 || owner.Entry >= package.Entries.Count) return false;
        var entry = package.Entries[owner.Entry];
        var runtime = entry.Sequences;
        int slot = runtime.FindIndex(s => s.Id == sequence);
        if (slot < 0) return false;
        string name = runtime[slot].Name;
        foreach (var candidate in runtime)
        {
            int index = candidate.Events.FindIndex(e => e.Type == 22 && e.Bytes.Length >= 48 && (e.I32(44) == slot || e.I32(44) < 0 && e.Text(12) == name));
            if (index < 0) continue;
            var status = frame.Sequences.FirstOrDefault(s => s.Instance == instance && s.Sequence == candidate.Id);
            bool loops = candidate.Events.Any(e => e.Type == 30);
            if (status?.State is "Waiting for threshold" or "Running" or "Waiting for release" && (status.EventIndex <= index || loops)) return false;
        }
        return true;
    }

    private sealed class Builder(string id)
    {
        public readonly string Id = id;
        public readonly List<AnimationTimingSequence> Sequences = [];
        public readonly List<AnimationTimingMark> Marks = [];
        public readonly List<AnimationTimingBand> Bands = [];
        public string Name(AnimationPackage package)
        {
            if (Sequences.Count == 0) return "";
            var sequence = package.Entries[Sequences[0].Entry].AllSequences.FirstOrDefault(s => s.Id == Sequences[0].Sequence);
            return sequence == null ? "" : sequence == package.Entries[Sequences[0].Entry].Primary ? "Cleanup" : sequence.Name.Length == 0 ? "Sequence" : AnimationPhrases.Safe(sequence.Name);
        }
        public AnimationTimingLane Build(AnimationPackage package, Group group, int index, IReadOnlyList<int> members, AnimationFrame frame,
            Dictionary<long, InstanceInfo> instances, Dictionary<(long, Guid), List<AnimationSequencePhase>> phases, Dictionary<long, double> finished)
        {
            string name = Name(package);
            var first = Sequences.Count == 0 ? null : package.Entries[Sequences[0].Entry].AllSequences.FirstOrDefault(s => s.Id == Sequences[0].Sequence);
            bool waits = first?.ResetMode == 3;
            string label = name + (group.Instances.Count > 1 ? " ×" + group.Instances.Count.ToString(CultureInfo.InvariantCulture) : "");
            // The state at the frame's time: the latest live instance's, else what its last phase left.
            AnimationSequenceRunState run = AnimationSequenceRunState.NotReached; string state = "not started";
            HashSet<Guid> ids = [.. Sequences.Select(s => s.Sequence)];
            var live = group.Instances.Where(i => instances.TryGetValue(i, out var info) && info.Live).OrderByDescending(i => i).ToArray();
            AnimationSequenceStatus? status = null;
            foreach (long instance in live) { status = frame.Sequences.FirstOrDefault(s => s.Instance == instance && ids.Contains(s.Sequence)); if (status != null) break; }
            var owner = status == null ? null : frame.Instances.FirstOrDefault(i => i.Instance == status.Instance);
            bool paused = status != null && frame.Bindings.Any(b => b.Instance == status.Instance && b.State == AnimationBindingLifecycle.Paused);
            if (status != null && owner is { Finished: false })
            {
                (run, state) = status.State switch
                {
                    "Waiting for threshold" => paused ? (AnimationSequenceRunState.Paused, "paused") : (AnimationSequenceRunState.WaitingForThreshold, "waiting for threshold"),
                    "Running" => paused ? (AnimationSequenceRunState.Paused, "paused") : (AnimationSequenceRunState.Running, "running"),
                    "Complete" => (AnimationSequenceRunState.Complete, "complete"),
                    "Waiting for release" => (AnimationSequenceRunState.WaitingForRelease, "waiting for release"),
                    "Awaiting excluded dependency" => (AnimationSequenceRunState.Blocked, "blocked"),
                    _ => (AnimationSequenceRunState.Unavailable, "unavailable"),
                };
                if (owner.Cleanup && index != 0) (run, state) = (AnimationSequenceRunState.Stopped, "stopped");
            }
            else if (Bands.Count > 0 || Marks.Count > 0)
            {
                var last = group.Instances.SelectMany(i => Sequences.Select(s => phases.GetValueOrDefault((i, s.Sequence)))).Where(l => l is { Count: > 0 }).Select(l => l![^1]).OrderBy(p => p.Time).LastOrDefault();
                (run, state) = last?.State switch
                {
                    AnimationSequencePhaseState.Stopped => (AnimationSequenceRunState.Stopped, "stopped"),
                    AnimationSequencePhaseState.WaitingForRelease => (AnimationSequenceRunState.WaitingForRelease, "waiting for release"),
                    AnimationSequencePhaseState.Unavailable => (AnimationSequenceRunState.Unavailable, "unavailable"),
                    _ => (AnimationSequenceRunState.Complete, "complete"),
                };
            }
            var bands = Merge(Bands);
            double? started = Marks.Count > 0 ? Marks.Min(m => m.Start) : null;
            return new(Id, AnimationTimingLaneKind.Sequence, label, group.Depth + 1)
            {
                Group = "group:" + group.Key, Members = members, Instances = group.Instances.Order().ToArray(), Sequences = Sequences.ToArray(),
                SequenceIndex = index, Cleanup = index == 0, WaitsForRelease = waits, Run = run, State = state, Started = started,
                Path = group.Instances.Count > 0 && instances.TryGetValue(group.Instances.Min(), out var info2) ? info2.Path : group.Start?.Path ?? "",
                Marks = Marks.OrderBy(m => m.Start).ThenBy(m => m.Occurrence).ToArray(), Bands = bands,
                Tooltip = (index == 0 ? "Cleanup · runs on stop or reset" : name + (waits ? " · waits for release" : "")) + " · " + state,
            };
        }
        /// <summary>Overlapping bands of one kind (several instances in a group) as one; at most <see cref="MaximumBandsPerLane"/>.</summary>
        private static IReadOnlyList<AnimationTimingBand> Merge(List<AnimationTimingBand> bands)
        {
            List<AnimationTimingBand> merged = [];
            foreach (var kind in bands.GroupBy(b => b.Kind))
            {
                AnimationTimingBand? current = null;
                foreach (var band in kind.OrderBy(b => b.Start))
                {
                    if (current != null && band.Kind != AnimationTimingBandKind.Complete && band.Start <= current.End + 1e-6)
                    {
                        current = current with { End = Math.Max(current.End, band.End), Open = current.Open || band.Open && band.End >= current.End };
                        continue;
                    }
                    if (current != null) merged.Add(current);
                    current = band;
                }
                if (current != null) merged.Add(current);
            }
            return merged.OrderBy(b => b.Start).Take(MaximumBandsPerLane).ToArray();
        }
    }
}
