namespace zStudio.Core.Animation;

/// <summary>
/// What a what-if run's included subtree did first to a binding of the base run (design/animation-redesign.md §15): a stop,
/// reset or invalidate by name of <see cref="Affected"/> at <see cref="Time"/> (the shared clock, where Timing ends what it
/// stopped), by the event <see cref="Event"/> of <see cref="Entry"/>'s sequence <see cref="Sequence"/> (its cleanup's for a
/// RESET_STATE event), run by the instance at call path <see cref="InstancePath"/>.
/// </summary>
public sealed record AnimationWhatIfEffect(AnimationExcludedEffectKind Effect, int Affected, double Time, int Entry, Guid Sequence, Guid Event, string InstancePath)
{
    /// <summary>The event in words, as Timing names what stopped an event: "train_fall_trigger › placeholder › Stop animation destroy_tr_bridge".</summary>
    public string By { get; init; } = "";
}

/// <summary>
/// A what-if run of an excluded call path (<see cref="AnimationPlayer.RunWhatIf"/>): the same scenario with <see cref="Path"/>
/// and every call path below it included, run to <see cref="Until"/> (the playback range's end, or earlier when the run ended).
/// <see cref="Effect"/> is the first stop, reset or invalidate it applied to a binding of the base run, or null when it changed
/// none. <see cref="Included"/> are the call paths at or below <see cref="Path"/> it ran, in the order they first dispatched:
/// including them runs the same.
/// </summary>
public sealed record AnimationWhatIfResult(string Path, AnimationWhatIfEffect? Effect, IReadOnlyList<string> Included, double Until)
{
    /// <summary>More paths ran than <see cref="AnimationPlayer.MaximumWhatIfPaths"/> (or their characters' allowance): <see cref="Included"/> lists the first.</summary>
    public bool IncludedTruncated { get; init; }
    /// <summary>Why the run (or its baseline) stopped before <see cref="Until"/> could reach the range's end (a preview allowance), or null.</summary>
    public string? Failure { get; init; }
}

/// <summary>
/// The stops, resets and invalidates by name the base run applies itself (<see cref="AnimationPlayer.RunBaseline"/>), by binding,
/// event, affected binding and time, to the playback range's end: an effect a what-if's subtree applies that the base run
/// applies as well (the subtree's call started an animation a moment before the base run's own call of it would have) changes
/// nothing, so it is not one (<see cref="AnimationPlayer.RunWhatIf"/>). With them the starts it refuses as invalidated, by binding
/// and time: a what-if's invalidate did not cause a refusal the base run makes anyway. At most <see cref="Maximum"/> in all.
/// </summary>
public sealed class AnimationWhatIfBaseline
{
    public const int Maximum = 65536;
    private readonly HashSet<(int Entry, Guid Event, int Affected, long Time)> applied = [];
    private readonly HashSet<(int Entry, long Time)> refused = [];
    /// <summary>How far the base run ran.</summary>
    public double Until { get; internal set; }
    public int Count => applied.Count + refused.Count;
    /// <summary>The base run applied more than <see cref="Maximum"/>: later ones are not known.</summary>
    public bool Truncated { get; private set; }
    /// <summary>Why the base run stopped before <see cref="Until"/> could reach the range's end, or null.</summary>
    public string? Failure { get; internal set; }
    private static long Key(double at) => (long)Math.Round(at * 1e6);
    internal void Add(int entry, Guid ev, int affected, double at)
    {
        if (Count >= Maximum) { Truncated = true; return; }
        applied.Add((entry, ev, affected, Key(at)));
    }
    internal bool Contains(int entry, Guid ev, int affected, double at) => applied.Contains((entry, ev, affected, Key(at)));
    internal void AddRefused(int entry, double at)
    {
        if (Count >= Maximum) { Truncated = true; return; }
        refused.Add((entry, Key(at)));
    }
    internal bool Refused(int entry, double at) => refused.Contains((entry, Key(at)));
}

public sealed partial class AnimationPlayer
{
    /// <summary>The most call paths a what-if lists as what it included (<see cref="AnimationWhatIfResult.Included"/>).</summary>
    public const int MaximumWhatIfPaths = AnimationExecutionPolicy.MaximumPaths;
    private const int MaximumWhatIfCharacters = 4 * 1024 * 1024;

    /// <summary>
    /// A what-if run's bookkeeping (its policy's <see cref="AnimationExecutionPolicy.Subtree"/>): the bindings of the base run
    /// (the scenario's starts and every binding a call outside the subtree started), what the base run applies itself, the first
    /// effect, invalidates of a binding not started yet that wait for the base run to start it, and the subtree's call paths.
    /// </summary>
    private sealed class WhatIf(string root)
    {
        public readonly string Root = root;
        public readonly HashSet<int> Base = [];
        public AnimationWhatIfBaseline? Baseline;
        public readonly Dictionary<int, (AnimationWhatIfEffect Effect, long Order)> Pending = [];
        public (AnimationWhatIfEffect Effect, long Order)? First;
        public long Order;
        public readonly List<string> Paths = [];
        public readonly HashSet<string> PathSet = new(StringComparer.Ordinal);
        public int Characters;
        public bool PathsTruncated;
    }
    private WhatIf? whatIf;
    /// <summary>A baseline run only (<see cref="RunBaseline"/>): what it applies by name.</summary>
    private AnimationWhatIfBaseline? baselineRecord;

    /// <summary>
    /// Runs the scenario as it is, from 0 s to <paramref name="end"/> or until the run ends, in a separate player as a what-if
    /// does (<see cref="RunWhatIf"/>), and records every stop, reset and invalidate by name it applies: the baseline of its
    /// what-ifs to the same end. This player is not changed. Deterministic.
    /// </summary>
    public AnimationWhatIfBaseline RunBaseline(double end, CancellationToken token = default)
    {
        var policy = ExecutionPolicy ?? throw new InvalidOperationException("A what-if run needs the scenario's call admission.");
        AnimationWhatIfBaseline baseline = new();
        AnimationPlayer probe;
        try { probe = Probe(policy, end, token); }
        catch (StateLimitException ex) { baseline.Failure = ex.Message; return baseline; }
        probe.baselineRecord = baseline;
        (double until, string? failure) = probe.RunTo(end, token);
        baseline.Until = until; baseline.Failure = failure;
        return baseline;
    }

    /// <summary>
    /// Runs the same scenario with the excluded call path <paramref name="path"/> and every call path below it included (design
    /// §15: an excluded call that would change the run), from 0 s to <paramref name="end"/> or until the run ends, in a separate
    /// player with this player's context, starts, inputs, rules and condition switches; this player is not changed. Records
    /// the first stop, reset or invalidate by name an instance of that subtree (or a cleanup its reset started) applies to a
    /// binding of the base run: a scenario start or a binding a call outside the subtree started, acting on the base run's own
    /// instance (an effect on what the subtree started itself changes nothing else). A stop counts when the binding was running,
    /// a reset unless it was stopped or unavailable, an invalidate when it was not invalidated already; an invalidate of a
    /// binding the base run has not started yet, or of one the subtree started early, counts once the base run's start of it is
    /// refused for it and the baseline does not refuse that start too. An effect
    /// <paramref name="baseline"/> (<see cref="RunBaseline"/> to the same end) shows the base run applies itself, the same event
    /// on the same binding at the same time, is not one. Deterministic.
    /// </summary>
    public AnimationWhatIfResult RunWhatIf(string path, AnimationWhatIfBaseline baseline, double end, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var policy = ExecutionPolicy ?? throw new InvalidOperationException("A what-if run needs the scenario's call admission.");
        AnimationPlayer probe;
        try { probe = Probe(policy.WithSubtree(path), end, token); }
        catch (StateLimitException ex) { return new(path, null, [], 0) { Failure = ex.Message }; }
        var state = probe.whatIf ?? throw new InvalidOperationException("A what-if run needs a subtree.");
        state.Baseline = baseline;
        var (until, failure) = probe.RunTo(end, token);
        return new(state.Root, state.First?.Effect, state.Paths.ToArray(), until) { IncludedTruncated = state.PathsTruncated, Failure = failure ?? baseline.Failure };
    }
    /// <summary>A separate player of this one's context, starts, inputs and rules under <paramref name="policy"/>.</summary>
    private AnimationPlayer Probe(AnimationExecutionPolicy policy, double end, CancellationToken token)
    {
        if (!double.IsFinite(end) || end < 0 || end > AnimationSeconds.Maximum) throw new InvalidDataException("Preview time must be between zero and one hour.");
        return new AnimationPlayer(context, entryIndex, Seed, resetPhase, token, maximumRetainedStateBytes,
            () => RetainedStateBytes + (otherRetainedState?.Invoke() ?? 0), schedule)
        {
            EffectLevel = EffectLevel, ExecutionPolicy = policy, LodLevel = LodLevel,
            ReferencePosition = ReferencePosition, ActivationStart = ActivationStart, GroundPlaneEnabled = GroundPlaneEnabled, Terrain = Terrain, OriginCollision = OriginCollision,
            ProceduralLimitsEnabled = ProceduralLimitsEnabled, PreviewHeight = PreviewHeight,
        };
    }
    /// <summary>Ticks a probe to <paramref name="end"/>: nothing happens once the run ended or only waits for excluded calls.</summary>
    private (double Until, string? Failure) RunTo(double end, CancellationToken token)
    {
        long target = (long)Math.Floor(end / StepSeconds + 1e-7);
        try { while (ticks < target && !IsComplete && !IsDependencyBlocked) Tick(token); }
        catch (IOException ex) { return (Time, ex.Message); }
        catch (InvalidDataException ex) { return (Time, ex.Message); }
        return (Time, null);
    }
    /// <summary>
    /// Starts a what-if's bookkeeping when the policy has a subtree: the scenario's starts are part of the base run. Called once
    /// the reset admitted the starts due at zero (none of them below the subtree).
    /// </summary>
    private void BeginWhatIf()
    {
        if (executionPolicy?.Subtree is not { } root) { whatIf = null; return; }
        whatIf = new(root);
        if (schedule != null) foreach (var start in schedule.Starts) whatIf.Base.Add(start.Entry);
        else whatIf.Base.Add(entryIndex);
        foreach (var instance in instances) if (!instance.WhatIf) whatIf.Base.Add(instance.Entry.Index);
    }
    /// <summary>Whether a new instance belongs to the what-if's subtree: its call path is in it, or a reset by the subtree started its cleanup.</summary>
    private bool InWhatIf(string callPath, bool primary) =>
        whatIf != null && (executionPolicy!.InSubtree(callPath) || primary && dispatchedEvent?.Instance.WhatIf == true);
    /// <summary>A call path the what-if's subtree admitted, first time only (bounded).</summary>
    private void RecordWhatIfPath(string path)
    {
        var state = whatIf!;
        if (state.PathSet.Contains(path)) return;
        if (state.Paths.Count >= MaximumWhatIfPaths || path.Length > MaximumWhatIfCharacters - state.Characters) { state.PathsTruncated = true; return; }
        state.Paths.Add(path); state.PathSet.Add(path); state.Characters += path.Length;
    }
    /// <summary>A baseline run's by-name event the game applies.</summary>
    private void BaselineByName(Instance caller, AnimationEvent ev, AnimationEntry target) =>
        baselineRecord!.Add(caller.Entry.Index, ev.Id, target.Index, dispatchedEvent?.At ?? Time);
    /// <summary>
    /// A by-name event of the subtree that the game applies (<paramref name="before"/>: the binding's state before it,
    /// <paramref name="acted"/>: the instance it acted on, if any): an effect on the base run, or an invalidate that waits for
    /// the base run to start that binding.
    /// </summary>
    private void WhatIfByName(Instance caller, AnimationEvent ev, AnimationEntry target, byte before, Instance? acted)
    {
        var state = whatIf!;
        // An invalidate of an invalidated binding changes nothing; an effect on what the subtree started is its own, unless an
        // invalidate then refuses a start from outside the subtree (a scenario start of the binding it ran early, say).
        bool own = acted?.WhatIf == true;
        if (ev.Type == 27 && before is 4 or 6 || own && ev.Type != 27) return;
        var kind = ev.Type switch { 25 => AnimationExcludedEffectKind.Stop, 26 => AnimationExcludedEffectKind.Reset, _ => AnimationExcludedEffectKind.Invalidate };
        string by = dispatchedEvent is { } d ? EventWords(d.Instance, d.Sequence, d.Event, true) : AnimationPhrases.Safe(caller.Entry.Name);
        AnimationWhatIfEffect effect = new(kind, target.Index, dispatchedEvent?.At ?? Time, caller.Entry.Index, dispatchedEvent?.Sequence.Data.Id ?? Guid.Empty, ev.Id, caller.CallPath) { By = by };
        // The base run applies it itself (its own call of that animation was refused because the subtree's started it first).
        if (state.Baseline?.Contains(effect.Entry, effect.Event, effect.Affected, effect.Time) == true) return;
        long order = ++state.Order;
        if (state.Base.Contains(target.Index) && !own) OfferWhatIf(effect, order);
        else if (kind == AnimationExcludedEffectKind.Invalidate) state.Pending.TryAdd(target.Index, (effect, order));
    }
    /// <summary>
    /// A start outside the subtree refused because a pending invalidate of the subtree invalidated its binding: that invalidate
    /// changed the base run, unless the base run refuses that start as invalidated too (its own invalidate did it).
    /// </summary>
    private void WhatIfRefused(AnimationEntry entry, string reason, string path, bool scenarioStart)
    {
        var state = whatIf!;
        if (reason != "invalidated" || !scenarioStart && executionPolicy!.InSubtree(path) || state.Baseline?.Refused(entry.Index, Time) == true) return;
        if (state.Pending.Remove(entry.Index, out var pending)) OfferWhatIf(pending.Effect, pending.Order);
    }
    private void OfferWhatIf(AnimationWhatIfEffect effect, long order)
    {
        var state = whatIf!;
        if (state.First is not { } first || effect.Time < first.Effect.Time - 1e-9 || Math.Abs(effect.Time - first.Effect.Time) <= 1e-9 && order < first.Order)
            state.First = (effect, order);
    }
}
