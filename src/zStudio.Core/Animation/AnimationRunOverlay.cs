namespace zStudio.Core.Animation;

/// <summary>Where a shown sequence is in the run at the playhead.</summary>
public enum AnimationSequenceRunState { NotReached, WaitingForThreshold, Running, WaitingForRelease, Complete, Stopped, Blocked, Paused, Unavailable }

/// <summary>What happened to a row's event in the run, by its latest pass.</summary>
public enum AnimationRowDisposition { NotReached, Current, Executed, Stopped, SkippedByCondition, Excluded, Duplicate, Refused, Unresolved, Unavailable }

/// <summary>A binding's state at the playhead, with the starts the run refused (the latest few) and why.</summary>
public sealed record AnimationRunBinding(int Entry, AnimationBindingLifecycle State, string Label)
{
    public string? PausedBy { get; init; }
    public bool Invalidated { get; init; }
    public long? Instance { get; init; }
    public double? Time { get; init; }
    public string? Cause { get; init; }
    public IReadOnlyList<AnimationStartRefusal> Refusals { get; init; } = [];
    public int RefusalCount { get; init; }
    /// <summary>Its latest start attempt's activation prerequisites (met, refused or ignored for that start), when it has any.</summary>
    public AnimationPrerequisiteStart? Prerequisites { get; init; }
    public int PrerequisiteStartCount { get; init; }
}

/// <summary>
/// A shown sequence at the playhead: its state, pass, the remaining threshold of the event it waits for, the event that last
/// released it and whether any releaser can still release it ("no releaser left").
/// </summary>
public sealed record AnimationRunSequence(string Node, AnimationSequenceRunState State, string Label)
{
    public long? Instance { get; init; }
    public int Pass { get; init; }
    public int EventIndex { get; init; }
    public double? ThresholdRemaining { get; init; }
    public string? ReleasedBy { get; init; }
    public double? ReleasedAt { get; init; }
    public string? ReleaseKind { get; init; }
    public IReadOnlyList<string> Releasers { get; init; } = [];
    public bool NoReleaserLeft { get; init; }
    public string? PausedBy { get; init; }
    /// <summary>A blocked sequence's excluded call: the edge whose port includes it, and the name it calls.</summary>
    public string? BlockedBy { get; init; }
    public string? BlockedCall { get; init; }
}

/// <summary>A row's events in the run: the latest disposition, and how often each happened (grouped rows count their members).</summary>
public sealed record AnimationRunRow(string Row, AnimationRowDisposition Disposition)
{
    public IReadOnlyList<AnimationRunCount> Counts { get; init; } = [];
    public int Occurrences { get; init; }
    public int Skipped { get; init; }
    public double? LastStart { get; init; }
    public double? LastEnd { get; init; }
    /// <summary>A refusal's or a launch's detail ("already running here", the child binding; "Call path too deep; child omitted"),
    /// or why a stopped occurrence ended early ("Stopped by piece3_hit_water › Stop sequence move_piece3").</summary>
    public string? Detail { get; init; }
    public int? ChildEntry { get; init; }
    public long? ChildInstance { get; init; }
}

public sealed record AnimationRunCount(AnimationRowDisposition Disposition, int Count);

/// <summary>One evaluation of an IF or ELSEIF: its pass, time, outcome and how it was decided.</summary>
public sealed record AnimationConditionPass(int Pass, double Time, bool Value, bool Forced, long Occurrence, string Kind, string Outcome)
{
    public double? Measured { get; init; }
    public double? Threshold { get; init; }
    public int? Draw { get; init; }
    public string? Unavailable { get; init; }
    public string InstancePath { get; init; } = "";
}

/// <summary>A condition's passes in the frame's instance (at most 40 listed, the latest last).</summary>
public sealed record AnimationRunCondition(string Row, Guid Event, int PassCount, IReadOnlyList<AnimationConditionPass> Passes)
{
    public AnimationConditionPass? Latest => Passes.Count == 0 ? null : Passes[^1];
}

/// <summary>A reference node in the run: "8 instances · finished, pose kept" / "excluded ×8 at 20.017 s" / "refused ×8 · invalidated".</summary>
public sealed record AnimationRunReference(string Node, int Instances, string Label)
{
    public IReadOnlyList<AnimationRunState> States { get; init; } = [];
    public int Excluded { get; init; }
    public int Refused { get; init; }
    public string? RefusedReason { get; init; }
    public double? LastTime { get; init; }
}

public sealed record AnimationRunState(AnimationBindingLifecycle State, int Bindings);

/// <summary>An edge in the run: reached (with its latest time), how often its calls were excluded or refused, and its scenario inclusion.</summary>
public sealed record AnimationRunEdge(string Edge, bool Reached, double? Time)
{
    public int Excluded { get; init; }
    public int Refused { get; init; }
    /// <summary>How many of the edge's call paths the scenario includes, when a scenario is given.</summary>
    public int? IncludedPaths { get; init; }
}

/// <summary>
/// A pure projection of one run frame onto a Structure graph (design §8, "Run overlay"): binding states, sequence states,
/// row dispositions, condition passes, reference nodes and edges, from what the player recorded (trace dispositions,
/// skipped spans, condition results, releasers, binding states and refusals). The GUI and MCP read it and never recompute
/// the run's semantics. A frame's instance is the opened binding's primary start (path "") or the frame's call path.
/// </summary>
public sealed class AnimationRunOverlay
{
    public const int MaximumPasses = 40;
    private readonly Dictionary<int, AnimationRunBinding> bindings = [];
    private readonly Dictionary<string, AnimationRunSequence> sequences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimationRunRow> rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimationRunCondition> conditions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimationRunReference> references = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnimationRunEdge> edges = new(StringComparer.Ordinal);

    public Guid Graph { get; }
    public double Time { get; }
    /// <summary>The player reference at this frame.</summary>
    public AnimationPlayerReferenceState? Player { get; }

    public AnimationRunBinding Binding(int entry) => bindings.GetValueOrDefault(entry) ?? Unstarted(entry);
    public AnimationRunSequence? Sequence(string node) => sequences.GetValueOrDefault(node);
    public AnimationRunRow? Row(string row) => rows.GetValueOrDefault(row);
    public AnimationRunCondition? Condition(string row) => conditions.GetValueOrDefault(row);
    /// <summary>Every shown condition with its passes.</summary>
    public IEnumerable<AnimationRunCondition> Conditions => conditions.Values;
    public AnimationRunReference? Reference(string node) => references.GetValueOrDefault(node);
    public AnimationRunEdge? Edge(string edge) => edges.GetValueOrDefault(edge);
    private readonly AnimationPackage package;
    private AnimationRunBinding Unstarted(int entry)
    {
        var state = entry >= 0 && entry < package.Entries.Count ? AnimationPlayer.InitialState(package.Entries[entry]) : AnimationBindingLifecycle.Unavailable;
        return new(entry, state, Label(state, null));
    }

    private AnimationRunOverlay(AnimationGraph graph, AnimationFrame frame, AnimationScenario? scenario, CancellationToken token)
    {
        Graph = graph.Id; Time = frame.Time; Player = frame.PlayerReference; package = graph.Index.Package;
        foreach (var status in frame.Bindings) bindings[status.Entry] = FromStatus(status);
        foreach (var group in frame.Refusals.GroupBy(r => r.Entry))
            if (!bindings.ContainsKey(group.Key)) bindings[group.Key] = Unstarted(group.Key);
        foreach (var group in frame.PrerequisiteStarts.GroupBy(p => p.Entry))
            if (!bindings.ContainsKey(group.Key)) bindings[group.Key] = Unstarted(group.Key);
        foreach (var (entry, binding) in bindings.ToArray())
        {
            var refused = frame.Refusals.Where(r => r.Entry == entry).ToArray();
            var checks = frame.PrerequisiteStarts.Where(p => p.Entry == entry).ToArray();
            if (refused.Length == 0 && checks.Length == 0) continue;
            bindings[entry] = binding with
            {
                Refusals = refused.TakeLast(8).ToArray(), RefusalCount = refused.Length,
                Label = refused.Length > 0 && binding.State == AnimationBindingLifecycle.NotStarted ? "Refused · " + Refusal(refused[^1]) : binding.Label,
                Prerequisites = checks.Length == 0 ? null : checks[^1], PrerequisiteStartCount = checks.Length,
            };
        }
        var nodes = AllNodes(graph);
        // Each shown frame's occurrences and instances: those the player holds, and those it released that left occurrences
        // (as Timing's groups). The shared state rule reads only these.
        List<(AnimationGraphFrame Shown, AnimationTrace[] Trace, long[] Owned)> shownFrames = [];
        foreach (var node in nodes)
        {
            if (node.Frame is not { } shown) continue;
            token.ThrowIfCancellationRequested();
            bool Owns(int entry, string path) => entry == shown.Entry && (shown.Path == null || path == shown.Path);
            var trace = frame.Trace.Where(t => Owns(t.Entry, t.InstancePath)).ToArray();
            shownFrames.Add((shown, trace, frame.Instances.Where(i => Owns(i.Entry, i.Path)).Select(i => i.Instance).Concat(trace.Select(t => t.Instance)).Distinct().Order().ToArray()));
        }
        var states = AnimationSequenceStates.Create(frame, package, shownFrames.SelectMany(f => f.Owned).ToHashSet(), token);
        foreach (var (shown, trace, owned) in shownFrames) Project(graph, frame, states, shown, trace, owned, token);
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (node.Reference is { } reference) references[node.Id] = ProjectReference(node.Id, reference, frame);
        }
        for (int offset = 0; ;)
        {
            var page = graph.Edges(offset);
            foreach (var edge in page.Rows) edges[edge.Id] = ProjectEdge(graph, edge, frame, scenario);
            if (page.NextOffset is not int next) break;
            offset = next;
        }
    }

    public static AnimationRunOverlay Create(AnimationGraph graph, AnimationFrame frame, AnimationScenario? scenario = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(frame);
        return new(graph, frame, scenario, token);
    }

    private static List<AnimationGraphNode> AllNodes(AnimationGraph graph)
    {
        List<AnimationGraphNode> all = [];
        for (int offset = 0; ;)
        {
            var page = graph.Nodes(offset);
            all.AddRange(page.Rows);
            if (page.NextOffset is not int next) return all;
            offset = next;
        }
    }

    private static AnimationRunBinding FromStatus(AnimationBindingStatus status) =>
        new(status.Entry, status.State, Label(status.State, status.PausedBy))
            { PausedBy = status.PausedBy, Invalidated = status.Invalidated, Instance = status.Instance, Time = status.Time, Cause = status.Cause };

    public static string Label(AnimationBindingLifecycle state, string? pausedBy) => state switch
    {
        AnimationBindingLifecycle.NotStarted => "Not started",
        AnimationBindingLifecycle.Running => "Running",
        AnimationBindingLifecycle.RunningInvalidated => "Running · invalidated",
        AnimationBindingLifecycle.FinishedPoseKept => "Finished · pose kept",
        AnimationBindingLifecycle.Stopped => "Stopped",
        AnimationBindingLifecycle.Invalidated => "Invalidated",
        AnimationBindingLifecycle.Paused => "Paused · " + (pausedBy ?? "gate"),
        _ => "Unavailable",
    };

    /// <summary>A refusal's reason in words ("prerequisites 0 of 5", "invalidated", "one-shot used", "already running here").</summary>
    public static string Refusal(AnimationStartRefusal refusal) => refusal.Reason switch
    {
        "prerequisites" when refusal.Prerequisites is { } p => p.Minimum > 0 ? $"prerequisites {p.Matched} of {p.Minimum}" : "prerequisites unmet",
        "prerequisites" => "prerequisites unmet",
        "oneShot" => "one-shot used",
        "alreadyRunning" => "already running here",
        "invalidated" => "invalidated",
        _ => "unavailable",
    };

    private void Project(AnimationGraph graph, AnimationFrame frame, AnimationSequenceStates states, AnimationGraphFrame shown, AnimationTrace[] trace, long[] owned, CancellationToken token)
    {
        var entry = package.Entries[shown.Entry];
        foreach (var sequence in entry.AllSequences)
        {
            token.ThrowIfCancellationRequested();
            string node = AnimationGraph.SequenceId(entry.Index, sequence.Id);
            if (graph.Node(node) == null) continue;
            var ran = trace.Where(t => t.Sequence == sequence.Id).ToArray();
            sequences[node] = ProjectSequence(graph, node, entry.Index, sequence, states.Of(owned.Select(i => (i, sequence.Id))));
            Dictionary<Guid, int> position = [];
            for (int i = 0; i < sequence.Events.Count; i++) position[sequence.Events[i].Id] = i;
            var skips = frame.Skipped.Where(s => s.Sequence == sequence.Id && (shown.Path == null || s.InstancePath == shown.Path)).ToArray();
            for (int offset = 0; ;)
            {
                var page = graph.Rows(entry.Index, sequence.Id, offset);
                foreach (var row in page.Rows)
                {
                    rows[row.Id] = ProjectRow(row, ran, skips, position);
                    if (row.Role == AnimationGraphRowRole.Question) conditions[row.Id] = ProjectCondition(row, frame, shown.Path);
                }
                if (page.NextOffset is not int next) break;
                offset = next;
            }
        }
    }

    /// <summary>A shown sequence by the rule Timing's lanes share (<see cref="AnimationSequenceStates"/>).</summary>
    private static AnimationRunSequence ProjectSequence(AnimationGraph graph, string node, int entry, AnimationSequence sequence, AnimationSequenceState run)
    {
        var state = run.State;
        var status = run.Status;
        string? paused = run.PausedBy;
        double? remaining = null;
        if (state == AnimationSequenceRunState.WaitingForThreshold && status != null && status.EventIndex >= 0 && status.EventIndex < sequence.Events.Count && sequence.Events[status.EventIndex].Bytes.Length >= 12)
        {
            var ev = sequence.Events[status.EventIndex];
            double clock = ev.StartMode switch { 2 => status.SequenceElapsed, 3 => status.EventElapsed, _ => status.AnimationElapsed };
            remaining = Math.Max(0, ev.Threshold - clock);
        }
        var releasers = graph.EdgesOf(node).Where(e => e.To == node && e.Relation is AnimationGraphRelation.Release or AnimationGraphRelation.ContactRelease).Select(e => e.From).ToArray();
        string? releasedBy = status?.ReleasedBy is Guid by && run.Instance != null ? graph.RowOf(entry, by)?.Id ?? AnimationGraph.RowId(entry, by) : null;
        // A blocked sequence waits at its excluded call: that call row's edge carries the include toggle.
        string? blockedBy = null, blockedCall = null;
        if (state == AnimationSequenceRunState.Blocked && status != null && status.EventIndex >= 0 && status.EventIndex < sequence.Events.Count
            && graph.RowOf(entry, sequence.Events[status.EventIndex].Id) is { } waiting)
        {
            var call = graph.EdgesOf(waiting.Id).FirstOrDefault(e => e.From == waiting.Id && e.Relation is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild);
            blockedBy = call?.Id;
            blockedCall = call != null && graph.Node(call.To) is { } target ? target.Reference?.Name ?? target.Label : waiting.Target;
        }
        string label = state switch
        {
            AnimationSequenceRunState.Running => "Running",
            AnimationSequenceRunState.Complete => "Complete",
            AnimationSequenceRunState.Stopped => "Stopped",
            AnimationSequenceRunState.WaitingForRelease => run.NoReleaserLeft ? "Waiting for release · no releaser left" : "Waiting for release",
            AnimationSequenceRunState.WaitingForThreshold => remaining is double r ? "Waiting for threshold · +" + AnimationSeconds.Short(r) : "Waiting for threshold",
            AnimationSequenceRunState.Blocked => blockedCall is { Length: > 0 } called ? "Blocked · waits for excluded call " + called : "Blocked · waits for an excluded call",
            AnimationSequenceRunState.Paused => "Paused · " + (paused ?? "gate"),
            AnimationSequenceRunState.Unavailable => "Unavailable",
            _ => "Not started",
        };
        return new(node, state, label)
        {
            Instance = run.Instance, Pass = status?.Iteration ?? 0, EventIndex = status?.EventIndex ?? 0, ThresholdRemaining = remaining,
            ReleasedBy = releasedBy, ReleasedAt = status?.ReleasedAt, ReleaseKind = status?.ReleaseKind, Releasers = releasers,
            NoReleaserLeft = run.NoReleaserLeft, PausedBy = paused,
            BlockedBy = blockedBy, BlockedCall = blockedCall,
        };
    }

    /// <summary>A call the player left out because its call path passed the allowance (a call cycle every call admits).</summary>
    private static bool TooDeep(AnimationTrace trace) => trace.Disposition is "excluded" or "excluded_wait"
        && trace.Status.StartsWith(AnimationExecutionPolicy.CallPathTooDeep, StringComparison.Ordinal);

    private static AnimationRunRow ProjectRow(AnimationGraphRow row, AnimationTrace[] ran, AnimationSkippedSpan[] skips, Dictionary<Guid, int> position)
    {
        Dictionary<AnimationRowDisposition, int> counts = [];
        AnimationRowDisposition latest = AnimationRowDisposition.NotReached; double latestTime = double.NegativeInfinity;
        int occurrences = 0, skipped = 0; double? lastStart = null, lastEnd = null; string? detail = null; int? child = null; long? childInstance = null;
        foreach (var member in row.Members)
        {
            var occurred = ran.Where(t => t.Event == member).ToArray();
            int index = position.GetValueOrDefault(member, -1);
            var memberSkips = skips.Where(s => position.TryGetValue(s.First, out int first) && position.TryGetValue(s.Last, out int last) && index >= first && index <= last).ToArray();
            occurrences += occurred.Length; skipped += memberSkips.Length;
            AnimationRowDisposition disposition = AnimationRowDisposition.NotReached; double time = double.NegativeInfinity;
            if (occurred.Length > 0)
            {
                var last = occurred[^1];
                disposition = last.End == null ? AnimationRowDisposition.Current : last.Disposition switch
                {
                    "excluded" or "excluded_wait" => AnimationRowDisposition.Excluded, "duplicate" => AnimationRowDisposition.Duplicate,
                    "refused" => AnimationRowDisposition.Refused, "unresolved" => AnimationRowDisposition.Unresolved, "unavailable" => AnimationRowDisposition.Unavailable,
                    // Cut short by a stop, reset or end: its last end is that stop.
                    _ => last.Stopped != null ? AnimationRowDisposition.Stopped : AnimationRowDisposition.Executed,
                };
                if (last.Disposition is "excluded_wait" && last.End == null) disposition = AnimationRowDisposition.Excluded;
                time = last.Start;
                if (lastStart == null || last.Start >= lastStart)
                {
                    lastStart = last.Start; lastEnd = last.End; child = last.ChildEntry ?? child; childInstance = last.ChildInstance ?? childInstance;
                    detail = last.Disposition == "refused" || TooDeep(last) ? last.Status : last.Stopped ?? detail;
                }
            }
            if (memberSkips.Length > 0 && memberSkips[^1].Time >= time) { disposition = AnimationRowDisposition.SkippedByCondition; time = memberSkips[^1].Time; }
            counts[disposition] = counts.GetValueOrDefault(disposition) + 1;
            if (time >= latestTime) { latest = disposition; latestTime = time; }
        }
        if (row.Members.Count > 1)
            latest = counts.OrderByDescending(p => p.Value).ThenByDescending(p => p.Key).First().Key;
        return new(row.Id, latest)
        {
            Counts = counts.OrderBy(p => p.Key).Select(p => new AnimationRunCount(p.Key, p.Value)).ToArray(), Occurrences = occurrences, Skipped = skipped,
            LastStart = lastStart, LastEnd = lastEnd, Detail = detail, ChildEntry = child, ChildInstance = childInstance,
        };
    }

    private static AnimationRunCondition ProjectCondition(AnimationGraphRow row, AnimationFrame frame, string? path)
    {
        var results = frame.Conditions.Where(c => c.Event == row.Event && (path == null || c.InstancePath == path)).ToArray();
        int skip = Math.Max(0, results.Length - MaximumPasses);
        var passes = results.Skip(skip).Select((c, i) => new AnimationConditionPass(skip + i + 1, c.Time, c.Value, c.Forced, c.Occurrence, c.Kind,
            $"{(c.Value ? "true" : "false")} · {(c.Forced ? "forced" : "game")} · {AnimationSeconds.Format(c.Time)}")
            { Measured = c.Measured, Threshold = c.Threshold, Draw = c.Draw, Unavailable = c.Unavailable, InstancePath = c.InstancePath }).ToArray();
        return new(row.Id, row.Event, results.Length, passes);
    }

    private AnimationRunReference ProjectReference(string node, AnimationGraphReference reference, AnimationFrame frame)
    {
        HashSet<int> members = [.. reference.Entries];
        var instances = frame.Trace.Where(t => members.Contains(t.Entry)).Select(t => t.Instance)
            .Concat(frame.Trace.Where(t => t.ChildEntry is int c && members.Contains(c) && t.Disposition == "launched" && t.ChildInstance != null).Select(t => t.ChildInstance!.Value))
            .Concat(frame.Instances.Where(i => members.Contains(i.Entry)).Select(i => i.Instance)).Distinct().Count();
        var excluded = frame.Trace.Where(t => t.ChildEntry is int c && members.Contains(c) && t.Disposition is "excluded" or "excluded_wait").ToArray();
        var refused = frame.Refusals.Where(r => members.Contains(r.Entry)).ToArray();
        var states = reference.Entries.Select(Binding).GroupBy(b => b.State).OrderBy(g => g.Key).Select(g => new AnimationRunState(g.Key, g.Count())).ToArray();
        double? last = frame.Trace.Where(t => members.Contains(t.Entry)).Select(t => (double?)t.Start).Max();
        string label;
        if (instances > 0)
            label = $"{instances} instance{(instances == 1 ? "" : "s")} · " + string.Join(", ", states.Select(s => Label(s.State, null).ToLowerInvariant().Replace(" · ", ", ", StringComparison.Ordinal)));
        else if (refused.Length > 0) label = $"refused ×{refused.Length} at {AnimationSeconds.Format(refused[^1].Time)} · {Refusal(refused[^1])}";
        else if (excluded.Length > 0) label = $"excluded ×{excluded.Length} at {AnimationSeconds.Format(excluded[^1].Start)}" + (TooDeep(excluded[^1]) ? " · call path too deep" : "");
        else label = "not reached";
        return new(node, instances, label)
        {
            States = states, Excluded = excluded.Length, Refused = refused.Length, RefusedReason = refused.Length == 0 ? null : Refusal(refused[^1]), LastTime = last,
        };
    }

    private static AnimationRunEdge ProjectEdge(AnimationGraph graph, AnimationGraphEdge edge, AnimationFrame frame, AnimationScenario? scenario)
    {
        HashSet<Guid> events = [.. edge.Events];
        string? path = edge.From.StartsWith("row:", StringComparison.Ordinal) && graph.Row(edge.From) is { } row ? graph.Node(AnimationGraph.FrameId(row.Entry))?.Frame?.Path : null;
        var occurred = frame.Trace.Where(t => events.Contains(t.Event) && (path == null || t.InstancePath == path)).ToArray();
        int? included = scenario == null || edge.Paths.Count == 0 ? null : edge.Paths.Count(scenario.Includes);
        return new(edge.Id, occurred.Length > 0, occurred.Length == 0 ? null : occurred.Max(t => t.Start))
        {
            Excluded = occurred.Count(t => t.Disposition is "excluded" or "excluded_wait"), Refused = occurred.Count(t => t.Disposition == "refused"), IncludedPaths = included,
        };
    }
}
