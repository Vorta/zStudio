using System.Runtime.InteropServices;

namespace zStudio.Core.Animation;

/// <summary>
/// A sequence's state at a frame's time by the shared rule (<see cref="AnimationSequenceStates"/>): the instance that
/// decided it, that instance's status at the frame (a live instance's, or a held finished one's), its last phase, and
/// whether a waiting sequence has a releaser left.
/// </summary>
public sealed record AnimationSequenceState(AnimationSequenceRunState State, long? Instance, Guid Sequence)
{
    public AnimationSequenceStatus? Status { get; init; }
    public AnimationSequencePhase? Phase { get; init; }
    /// <summary>The instance ended (finished, or released by the player) before the frame's time.</summary>
    public bool Ended { get; init; }
    public string? PausedBy { get; init; }
    public bool NoReleaserLeft { get; init; }

    public static readonly AnimationSequenceState NotStarted = new(AnimationSequenceRunState.NotReached, null, Guid.Empty);

    /// <summary>The Timing lane's words for a state ("not started", "waiting for threshold", "stopped" …).</summary>
    public static string Words(AnimationSequenceRunState state) => state switch
    {
        AnimationSequenceRunState.WaitingForThreshold => "waiting for threshold",
        AnimationSequenceRunState.Running => "running",
        AnimationSequenceRunState.Complete => "complete",
        AnimationSequenceRunState.WaitingForRelease => "waiting for release",
        AnimationSequenceRunState.Stopped => "stopped",
        AnimationSequenceRunState.Blocked => "blocked",
        AnimationSequenceRunState.Paused => "paused",
        AnimationSequenceRunState.Unavailable => "unavailable",
        _ => "not started",
    };
}

/// <summary>
/// The one per-sequence state rule the Timing lanes and the Structure graph's run overlay read (docs/agents/preview.md,
/// "Source-world animation session"), from what the player recorded in one frame:
/// <list type="bullet">
/// <item>a live instance's sequence is in its status at the frame: paused while a gate holds it running or waiting for its
/// threshold, stopped while its binding's stop delay runs (a stopped phase) or once a cleanup replaced it;</item>
/// <item>an ended instance's sequence (finished, or released by the player) is what it reached: complete only when it completed
/// (its last phase, a held instance's status, or its final event ended), unavailable when it was, otherwise stopped: a
/// sequence still running, waiting or paused when its instance ended ended with it (natural completion needs every sequence
/// complete, waiting for release or unavailable; anything else is a stop or reset of the binding);</item>
/// <item>a sequence with no phase, status or occurrence (a cleanup that never ran, an instance without evidence) is not
/// started.</item>
/// </list>
/// Of several instances (a group's, or one binding's restarts), the latest live instance with evidence for the sequence
/// decides, else the instance whose evidence ends last. A waiting sequence of a live instance has no releaser left when
/// every event of its instance that can release it (CALL_SEQUENCE, a motion's contact release: the graph's Release and
/// ContactRelease edges, cleanup aside) is past and its sequence does not loop.
/// </summary>
public sealed class AnimationSequenceStates
{
    private readonly AnimationPackage package;
    private readonly Dictionary<long, AnimationInstanceStatus> instances = [];
    private readonly Dictionary<long, int> entryOf = [];
    private readonly Dictionary<(long, Guid), AnimationSequenceStatus> statuses = [];
    private readonly Dictionary<(long, Guid), AnimationSequencePhase> lastPhase = [];
    private readonly Dictionary<(long, Guid), AnimationTrace> lastTrace = [];
    private readonly Dictionary<long, AnimationBindingStatus> pausedBases = [];
    private readonly Dictionary<int, Dictionary<Guid, (Guid Sequence, int Index, bool Loops)[]>> releasers = [];

    private AnimationSequenceStates(AnimationFrame frame, AnimationPackage package, IReadOnlySet<long>? only, CancellationToken token)
    {
        this.package = package;
        foreach (var instance in frame.Instances) { instances[instance.Instance] = instance; entryOf[instance.Instance] = instance.Entry; }
        foreach (var status in frame.Sequences)
            if (only == null || only.Contains(status.Instance)) statuses.TryAdd((status.Instance, status.Sequence), status);
        foreach (var t in frame.Trace)
        {
            if (only != null && !only.Contains(t.Instance)) continue;
            ref var last = ref CollectionsMarshal.GetValueRefOrAddDefault(lastTrace, (t.Instance, t.Sequence), out bool known);
            if (!known && t.Entry >= 0) entryOf.TryAdd(t.Instance, t.Entry);
            last = t;
        }
        token.ThrowIfCancellationRequested();
        foreach (var phase in frame.Phases)
            if (phase.State != AnimationSequencePhaseState.Finished && (only == null || only.Contains(phase.Instance))) lastPhase[(phase.Instance, phase.Sequence)] = phase;
        token.ThrowIfCancellationRequested();
        foreach (var binding in frame.Bindings)
            if (binding.State == AnimationBindingLifecycle.Paused && binding.Instance is long id) pausedBases[id] = binding;
    }

    /// <param name="only">The instances a caller asks about (all of the frame's when null).</param>
    public static AnimationSequenceStates Create(AnimationFrame frame, AnimationPackage package, IReadOnlySet<long>? only = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(package);
        return new(frame, package, only, token);
    }

    /// <summary>When a sequence's evidence ends: its last occurrence's end or its last phase (each list is in time order).</summary>
    private double LastSeen((long, Guid) key) => Math.Max(
        lastTrace.TryGetValue(key, out var t) ? t.End ?? t.Start : double.NegativeInfinity,
        lastPhase.TryGetValue(key, out var p) ? p.Time : double.NegativeInfinity);

    /// <summary>The instance runs at the frame's time: the player holds it and it has not finished.</summary>
    public bool Live(long instance) => instances.TryGetValue(instance, out var info) && !info.Finished;

    /// <summary>The state of one sequence of one instance.</summary>
    public AnimationSequenceState Of(long instance, Guid sequence) => Evaluate(instance, sequence) ?? AnimationSequenceState.NotStarted with { Sequence = sequence };

    /// <summary>The state of a shown sequence over its candidate instances, each with its own sequence of that lane or node.</summary>
    public AnimationSequenceState Of(IEnumerable<(long Instance, Guid Sequence)> candidates)
    {
        AnimationSequenceState? best = null; double bestSeen = double.NegativeInfinity; Guid first = Guid.Empty;
        foreach (var (instance, sequence) in candidates)
        {
            if (first == Guid.Empty) first = sequence;
            if (Evaluate(instance, sequence) is not { } state) continue;
            double seen = LastSeen((instance, sequence));
            if (best == null || Better(state, seen, best, bestSeen)) { best = state; bestSeen = seen; }
        }
        return best ?? AnimationSequenceState.NotStarted with { Sequence = first };

        // The latest live instance, else the one whose evidence ends last (the later instance on a tie).
        static bool Better(AnimationSequenceState state, double seen, AnimationSequenceState best, double bestSeen) =>
            !state.Ended && best.Ended || state.Ended == best.Ended && (state.Ended ? seen > bestSeen + 1e-9 || Math.Abs(seen - bestSeen) <= 1e-9 && state.Instance > best.Instance : state.Instance > best.Instance);
    }

    private AnimationSequenceState? Evaluate(long instance, Guid sequence)
    {
        var key = (instance, sequence);
        var status = statuses.GetValueOrDefault(key);
        var phase = lastPhase.GetValueOrDefault(key);
        var trace = lastTrace.GetValueOrDefault(key);
        if (status == null && phase == null && trace == null) return null;
        bool live = Live(instance);
        AnimationSequenceRunState state;
        string? pausedBy = null;
        if (live && status != null)
        {
            state = status.State switch
            {
                "Waiting for threshold" => AnimationSequenceRunState.WaitingForThreshold,
                "Running" => AnimationSequenceRunState.Running,
                "Complete" => AnimationSequenceRunState.Complete,
                "Waiting for release" => AnimationSequenceRunState.WaitingForRelease,
                "Awaiting excluded dependency" => AnimationSequenceRunState.Blocked,
                _ => AnimationSequenceRunState.Unavailable,
            };
            // A stop delay freezes the sequences until the cleanup replaces them (the player records them stopped).
            if (phase?.State == AnimationSequencePhaseState.Stopped && state is not (AnimationSequenceRunState.Complete or AnimationSequenceRunState.Unavailable))
                state = AnimationSequenceRunState.Stopped;
            else if (instances.TryGetValue(instance, out var info) && info.Cleanup && !Primary(instance, sequence)) state = AnimationSequenceRunState.Stopped;
            else if (state is AnimationSequenceRunState.Running or AnimationSequenceRunState.WaitingForThreshold
                && (phase?.State == AnimationSequencePhaseState.Paused || pausedBases.ContainsKey(instance)))
            {
                state = AnimationSequenceRunState.Paused;
                pausedBy = phase?.State == AnimationSequencePhaseState.Paused ? phase.PausedBy : pausedBases.GetValueOrDefault(instance)?.PausedBy;
                pausedBy ??= "gate";
            }
        }
        else
        {
            // Ended with its instance, or replaced by its live instance's cleanup: complete only when it completed (its last
            // phase, a held instance's status, or its final event ended, which a phase snapshot taken in the same tick misses:
            // a sequence whose last event stops its own binding), unavailable when it was, otherwise stopped.
            bool complete = phase?.State == AnimationSequencePhaseState.Complete || status?.State == "Complete" || trace != null && RanToEnd(instance, trace);
            bool unavailable = phase?.State == AnimationSequencePhaseState.Unavailable || status?.State == "Unavailable";
            state = complete ? AnimationSequenceRunState.Complete : unavailable ? AnimationSequenceRunState.Unavailable : AnimationSequenceRunState.Stopped;
        }
        return new(state, instance, sequence)
        {
            Status = status, Phase = phase, Ended = !live, PausedBy = pausedBy,
            NoReleaserLeft = live && state == AnimationSequenceRunState.WaitingForRelease && NoReleaserLeft(instance, sequence),
        };
    }

    private AnimationEntry? Entry(long instance) => entryOf.TryGetValue(instance, out int entry) && entry > 0 && entry < package.Entries.Count ? package.Entries[entry] : null;
    private bool Primary(long instance, Guid sequence) => Entry(instance)?.Primary.Id == sequence;
    /// <summary>The sequence's last occurrence is its final event and it ended (one that waits for release begins waiting again).</summary>
    private bool RanToEnd(long instance, AnimationTrace trace)
    {
        var entry = Entry(instance);
        var sequence = entry?.AllSequences.FirstOrDefault(s => s.Id == trace.Sequence);
        return sequence is { Events.Count: > 0 } && sequence.ResetMode != 3 && trace.End != null && sequence.Events[^1].Id == trace.Event;
    }

    /// <summary>
    /// Every event of the instance that can release <paramref name="sequence"/> (a CALL_SEQUENCE or a motion's contact
    /// release, as the graph's Release and ContactRelease edges; a cleanup's have no effect) is past, and none loops.
    /// </summary>
    public bool NoReleaserLeft(long instance, Guid sequence)
    {
        if (Entry(instance) is not { } entry) return false;
        if (!releasers.TryGetValue(entry.Index, out var map)) releasers[entry.Index] = map = Releasers(entry);
        if (!map.TryGetValue(sequence, out var list)) return true;
        foreach (var (from, index, loops) in list)
            if (statuses.GetValueOrDefault((instance, from)) is { State: "Waiting for threshold" or "Running" or "Waiting for release" } other && (other.EventIndex <= index || loops))
                return false;
        return true;
    }

    private static Dictionary<Guid, (Guid, int, bool)[]> Releasers(AnimationEntry entry)
    {
        Dictionary<Guid, List<(Guid, int, bool)>> found = [];
        foreach (var sequence in entry.Sequences)
        {
            bool loops = sequence.Events.Any(e => e.Type == 30);
            for (int i = 0; i < sequence.Events.Count; i++)
                if (AnimationGraph.SequenceTarget(entry, sequence.Events[i]) is { Kind: AnimationGraphRelation.Release or AnimationGraphRelation.ContactRelease } target)
                {
                    if (!found.TryGetValue(target.Sequence.Id, out var list)) found[target.Sequence.Id] = list = [];
                    list.Add((sequence.Id, i, loops));
                }
        }
        return found.ToDictionary(p => p.Key, p => p.Value.ToArray());
    }
}
