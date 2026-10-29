namespace zStudio.Core.Animation;

/// <summary>What an excluded call would do to a binding of the run in the game: STOP_ANIMATION, RESET_ANIMATION or INVALIDATE_ANIMATION by name.</summary>
public enum AnimationExcludedEffectKind { Stop, Reset, Invalidate }

/// <summary>A call on the way from an excluded call to its effect: the calling binding, its call event and the binding it calls.</summary>
public sealed record AnimationExcludedLink(int Caller, Guid Event, int Target);

/// <summary>
/// An excluded call path that would change the run (design/animation-redesign.md §15): in the game its animation, or one it
/// calls in turn, stops, resets or invalidates <see cref="Affected"/>, a binding that is part of the run.
/// <see cref="EffectEntry"/> and <see cref="EffectEvent"/> are the by-name event that does it; <see cref="Seconds"/> estimates
/// when on the shared clock (null: later, at a time the authored clocks alone do not set).
/// </summary>
public sealed record AnimationExcludedEffect(AnimationExcludedCall Call, AnimationExcludedEffectKind Effect, int Affected, int EffectEntry, Guid EffectEvent)
{
    public double? Seconds { get; init; }
    /// <summary>The calls below the excluded one that lead to the effect, outermost first (empty when its own animation has it).</summary>
    public IReadOnlyList<AnimationExcludedLink> Through { get; init; } = [];
    /// <summary>The deepest call path on the way: including it, with the calls that lead to it, runs the effect.</summary>
    public string IncludePath { get; init; } = "";
    /// <summary>The by-name event, or a call on the way, sits inside an IF, ELSEIF or ELSE branch: it happens only if that holds.</summary>
    public bool Conditional { get; init; }
    /// <summary>A stable identity: the excluded path, the effect's event and the affected binding.</summary>
    public string Id => Call.Path + "|" + EffectEntry.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + EffectEvent.ToString("N")
        + "|" + Affected.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Finds the excluded calls of a run that would change it (docs/agents/preview.md, "Source-world animation session"). An
/// excluded call is flagged when its animation, or anything it would call in turn, stops, resets or invalidates by name a binding
/// that is part of the run: a scenario start or a binding the run started (its included calls), unless the run would refuse
/// the call's start at its dispatch (<see cref="AnimationExcludedCall.Refused"/>) or the run's evidence shows the game would
/// ignore the event at its time (a stop of a binding that had ended by then). Names resolve as the player
/// resolves them (calls: a positive entry cache, else the first entry of the name; by-name events: the first entry of the name,
/// its base record, never entry 0; pattern names are bound when the map compiles). The walk over what an excluded call would run
/// is static, bounded (<see cref="MaximumDepth"/> calls deep, <see cref="MaximumVisits"/> bindings, each binding at most once on
/// a chain, so a call cycle ends) and cached per called binding; a call into a binding that is part of the run is not followed
/// (the run already runs it). The estimate is the call's dispatch in the run plus the time the stopping event's clocks give it
/// (its threshold, after the events before it in its sequence), when every event on the way runs on authored clocks; otherwise
/// later (null): after motion without a timed end, a waited call, a finite loop, in a sequence that waits for its release or in
/// a range- or zone-gated binding. Nothing past a constant spin or a loop that repeats forever is reached. Branches are not
/// decided: an effect inside an IF, ELSEIF or ELSE branch, or below a call inside one, is <see cref="AnimationExcludedEffect.Conditional"/>
/// and says so. Evidence only: it
/// never changes the run. Read-only over the index's frozen package.
/// </summary>
public sealed class AnimationExcludedEffects
{
    public const int MaximumDepth = 8;
    public const int MaximumVisits = 4096;
    /// <summary>The effects one called binding's walk keeps, and the most a run reports.</summary>
    public const int MaximumReach = 64, MaximumEffects = 256;
    private readonly AnimationGraphIndex index;
    private readonly Dictionary<int, Reach[]> cache = [];
    private readonly Lock gate = new();
    private readonly record struct Reach(AnimationExcludedEffectKind Kind, int Affected, int Owner, Guid Event, double? Offset, AnimationExcludedLink[] Through, bool Conditional);

    internal AnimationExcludedEffects(AnimationGraphIndex index) => this.index = index;

    /// <summary>
    /// The flagged effects of the excluded calls <paramref name="frame"/> reached, in the order the calls first dispatched.
    /// <paramref name="starts"/> are the scenario's starts (part of the run before they run); the bindings the frame started
    /// are the rest of it.
    /// </summary>
    public IReadOnlyList<AnimationExcludedEffect> Find(AnimationFrame frame, IEnumerable<int>? starts = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.ExcludedCalls.Count == 0) return [];
        var entries = index.Package.Entries;
        HashSet<int> run = [];
        foreach (int start in starts ?? []) run.Add(start);
        foreach (var binding in frame.Bindings) if (binding.Instance != null) run.Add(binding.Entry);
        foreach (var instance in frame.Instances) if (!instance.Cleanup) run.Add(instance.Entry);
        Dictionary<int, AnimationBindingStatus> states = [];
        foreach (var binding in frame.Bindings) states[binding.Entry] = binding;
        List<AnimationExcludedEffect> found = [];
        foreach (var call in frame.ExcludedCalls)
        {
            token.ThrowIfCancellationRequested();
            // A start the run would refuse (its prerequisites unmet, invalidated, a one-shot used) changes nothing.
            if (call.Target <= 0 || call.Target >= entries.Count || run.Contains(call.Target) || call.Refused != null) continue;
            foreach (var reach in Walk(call.Target, token))
            {
                if (!run.Contains(reach.Affected) || reach.Through.Any(link => run.Contains(link.Target))) continue;
                double? seconds = reach.Offset is double offset ? call.Time + offset : null;
                if (Ignored(reach.Kind, seconds, states.GetValueOrDefault(reach.Affected), frame.Time)) continue;
                string path = call.Path;
                try { foreach (var link in reach.Through) path = AnimationExecutionPolicy.ChildPath(path, link.Caller, link.Event, link.Target); }
                catch (InvalidDataException) { continue; }
                found.Add(new(call, reach.Kind, reach.Affected, reach.Owner, reach.Event)
                    { Seconds = seconds, Through = reach.Through, IncludePath = path, Conditional = reach.Conditional });
                if (found.Count >= MaximumEffects) return found;
            }
        }
        return found;
    }
    /// <summary>
    /// Whether the run's evidence shows the game would ignore the effect at its time: the binding was then (and is at the frame)
    /// in a state the by-name event does nothing in (a stop needs it running; a reset does nothing to a stopped or unavailable one;
    /// an invalidate nothing to an unavailable one). Only once the frame has passed that time and the binding's state has not
    /// changed since before it.
    /// </summary>
    private static bool Ignored(AnimationExcludedEffectKind kind, double? seconds, AnimationBindingStatus? binding, double now)
    {
        if (seconds is not double at || binding == null || now < at || binding.Time > at + AnimationPlayer.StepSeconds) return false;
        return kind switch
        {
            AnimationExcludedEffectKind.Stop => binding.State is not (AnimationBindingLifecycle.Running or AnimationBindingLifecycle.RunningInvalidated or AnimationBindingLifecycle.Paused),
            AnimationExcludedEffectKind.Reset => binding.State is AnimationBindingLifecycle.Stopped or AnimationBindingLifecycle.Unavailable,
            _ => binding.State == AnimationBindingLifecycle.Unavailable,
        };
    }

    /// <summary>"train_fall_trigger is excluded: in the game it stops destroy_tr_bridge at about 23 s" (or "… later").</summary>
    public string Describe(AnimationExcludedEffect effect)
    {
        string verb = effect.Effect switch { AnimationExcludedEffectKind.Stop => "stops", AnimationExcludedEffectKind.Reset => "resets", _ => "invalidates" };
        string who = effect.Through.Count == 0 ? "it" : Name(effect.EffectEntry) + (effect.Through.Count == 1 ? ", which it calls," : ", which it calls in turn,");
        return $"{Name(effect.Call.Target)} is excluded: in the game {who} {verb} {Name(effect.Affected)} {When(effect.Seconds)}"
            + (effect.Conditional ? " if a condition on the way holds" : "");
    }
    /// <summary>Timing's marker on the affected group: "would be stopped by excluded train_fall_trigger".</summary>
    public string Marker(AnimationExcludedEffect effect) => "would be " + effect.Effect switch
    {
        AnimationExcludedEffectKind.Stop => "stopped", AnimationExcludedEffectKind.Reset => "reset", _ => "invalidated",
    } + " by excluded " + Name(effect.Call.Target);
    /// <summary>"at about 23 s" (to a tenth of a second), or "later".</summary>
    public static string When(double? seconds) => seconds is double t ? "at about " + AnimationSeconds.Short(Math.Round(t, 1)) : "later";
    private string Name(int entry) => entry > 0 && entry < index.Package.Entries.Count ? AnimationPhrases.Safe(index.Package.Entries[entry].Name) : "an animation";

    /// <summary>What the called binding would stop, reset or invalidate, with when after its start (cached; deterministic).</summary>
    private Reach[] Walk(int root, CancellationToken token)
    {
        lock (gate)
        {
            if (cache.TryGetValue(root, out var cached)) return cached;
            var entries = index.Package.Entries;
            List<Reach> found = [];
            List<AnimationExcludedLink> chain = [];
            HashSet<int> onChain = [root];
            int visits = 0;
            Visit(root, 0, 0, false);
            return cache[root] = [.. found];

            void Visit(int entry, double? start, int depth, bool conditional)
            {
                if (++visits > MaximumVisits || found.Count >= MaximumReach) return;
                var data = entries[entry];
                // A range or zone gate freezes the binding's clocks while it holds it: its times are not authored alone.
                if (data.Bytes.Length >= 152 && (data.U32(148) & (2 | 8)) != 0) start = null;
                foreach (var sequence in data.Sequences)
                    foreach (var (ev, at, branch) in Dispatches(sequence, start, token))
                    {
                        if (index.Relate(ev, token) is not { Target: int target } relation) continue;
                        switch (relation.Kind)
                        {
                            case AnimationGraphRelation.StopAnimation or AnimationGraphRelation.ResetAnimation or AnimationGraphRelation.InvalidateAnimation:
                                if (found.Count >= MaximumReach) return;
                                var kind = relation.Kind switch
                                {
                                    AnimationGraphRelation.StopAnimation => AnimationExcludedEffectKind.Stop,
                                    AnimationGraphRelation.ResetAnimation => AnimationExcludedEffectKind.Reset,
                                    _ => AnimationExcludedEffectKind.Invalidate,
                                };
                                found.Add(new(kind, target, entry, ev.Id, at, [.. chain], conditional || branch));
                                break;
                            case AnimationGraphRelation.Call or AnimationGraphRelation.CallWait:
                                // Each binding at most once on a chain: a call cycle ends where it would go round again.
                                if (target <= 0 || depth + 1 >= MaximumDepth || !onChain.Add(target)) break;
                                chain.Add(new(entry, ev.Id, target));
                                Visit(target, at, depth + 1, conditional || branch);
                                chain.RemoveAt(chain.Count - 1); onChain.Remove(target);
                                break;
                        }
                    }
            }
        }
    }

    /// <summary>
    /// The events of a runtime sequence its instance would dispatch on a first pass, each with when after the instance's start
    /// (null when its clocks are not authored alone), as the player runs them: one after another, each once its predecessor
    /// completed and its START_TIME threshold allows (the animation clock from the instance's start, the sequence clock from
    /// the first event's, after-previous from the predecessor's completion), and whether it sits in a condition's branch (its
    /// IF decides whether it runs; branches are not decided here). It stops where the player would: a malformed event, a first
    /// event whose sequence clock never runs, past a constant spin or a loop that repeats forever.
    /// </summary>
    private static IEnumerable<(AnimationEvent Event, double? At, bool Branch)> Dispatches(AnimationSequence sequence, double? start, CancellationToken token)
    {
        // A sequence starts at once (0, 1), waits for its release (3) or never runs (complete, unavailable).
        if (sequence.ResetMode is not (0 or 1 or 3)) yield break;
        double? previous = sequence.ResetMode == 3 ? null : start, first = null;
        int depth = 0;
        var events = sequence.Events;
        for (int i = 0; i < events.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var ev = events[i];
            if (ev.Spec is not { } spec || ev.Bytes.Length < spec.Size || ev.StartMode is < 1 or > 3 || !float.IsFinite(ev.Threshold)) yield break;
            double threshold = ev.Threshold;
            if (i == 0 && ev.StartMode is 2 or 3 && threshold > 0) yield break;
            double? at = ev.StartMode switch
            {
                1 => Later(previous, start + threshold),
                2 => i == 0 ? previous : Later(previous, first + threshold),
                _ => i == 0 ? previous : previous + Math.Max(0, threshold),
            };
            if (i == 0) first = at;
            if (ev.Type == 34) depth = Math.Max(0, depth - 1);
            yield return (ev, at, depth > 0 && ev.Type is not (32 or 33));
            if (ev.Type == 31) depth++;
            double? end = Completion(ev, at, token, out bool endless);
            if (endless) yield break;
            previous = end;
        }
        static double? Later(double? a, double? b) => a is double x && b is double y ? Math.Max(x, y) : null;
    }

    /// <summary>When an event dispatched at <paramref name="at"/> completes, when its own fields set it; <paramref name="endless"/> when it never does.</summary>
    private static double? Completion(AnimationEvent ev, double? at, CancellationToken token, out bool endless)
    {
        endless = false;
        double? After(float duration) => at is double a && float.IsFinite(duration) ? a + Math.Max(0, duration) : null;
        switch (ev.Type)
        {
            case 5: return After(ev.F32(108));
            case 11: return After(ev.F32(140));
            case 14: return After(ev.F32(32));
            case 18: return After(ev.F32(80));
            case 21: return After(ev.F32(104));
            case 36: return After(ev.F32(60));
            case 37: return After(ev.F32(108));
            case 12:
                {
                    // The stream completes once every sample has started and finished (the player's PlaybackEnd).
                    try
                    {
                        var frames = new KeyframeStream(ev.Bytes, ev.Version, token);
                        if (frames.Count > 16384) return null;
                        float end = 0;
                        foreach (var frame in frames) end = Math.Max(end, Math.Max(frame.Start, frame.End));
                        return After(end);
                    }
                    catch (InvalidDataException) { endless = true; return null; }
                }
            case 10:
                {
                    if ((ev.U32(12) & 0x400) != 0) return After(ev.F32(ev.Version == 39 ? 328 : 248));
                    // A constant spin turns until a stop or reset ends it; other motion without a timer ends when it lands or stops.
                    endless = AnimationPlayer.SpinRevolution(ev) != null;
                    return null;
                }
            case 24 when (ev.I16(46) & 16) != 0: return null;
            case 30:
                endless = AnimationPhrases.Loop(ev).Mode == "forever";
                return null;
            default: return at;
        }
    }
}
