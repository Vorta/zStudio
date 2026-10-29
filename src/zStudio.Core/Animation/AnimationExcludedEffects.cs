using System.Globalization;

namespace zStudio.Core.Animation;

/// <summary>What an excluded call does to a binding of the run in the game: STOP_ANIMATION, RESET_ANIMATION or INVALIDATE_ANIMATION by name.</summary>
public enum AnimationExcludedEffectKind { Stop, Reset, Invalidate }

/// <summary>A call on the way from an excluded call to its effect: the calling binding, its call event and the binding it calls.</summary>
public sealed record AnimationExcludedLink(int Caller, Guid Event, int Target);

/// <summary>
/// An excluded call path that changes the run (design/animation-redesign.md §15), as its what-if run established: in the game its
/// animation, or one it calls in turn, stops, resets or invalidates <see cref="Affected"/>, a binding of the run, at
/// <see cref="Seconds"/> on the shared clock. <see cref="EffectEntry"/>, <see cref="EffectSequence"/> and
/// <see cref="EffectEvent"/> are the by-name event that does it.
/// </summary>
public sealed record AnimationExcludedEffect(AnimationExcludedCall Call, AnimationExcludedEffectKind Effect, int Affected, int EffectEntry, Guid EffectEvent, double Seconds)
{
    public Guid EffectSequence { get; init; }
    /// <summary>The by-name event in words, as Timing names a stop ("train_fall_trigger › placeholder › Stop animation destroy_tr_bridge").</summary>
    public string By { get; init; } = "";
    /// <summary>The calls below the excluded one that led to the effect, outermost first (empty when its own animation did it).</summary>
    public IReadOnlyList<AnimationExcludedLink> Through { get; init; } = [];
    /// <summary>The excluded call's path: Include includes it with every path below it (<see cref="IncludePaths"/>).</summary>
    public string IncludePath => Call.Path;
    /// <summary>
    /// What Include includes, exactly what the what-if run included: the excluded call's path and the call paths below it the
    /// what-if ran (an isolated preview's: the path and the exclusions below it, which then go).
    /// </summary>
    public IReadOnlyList<string> IncludePaths { get; init; } = [];
    /// <summary>A stable identity: the excluded path, the effect's event and the affected binding.</summary>
    public string Id => Call.Path + "|" + EffectEntry.ToString(CultureInfo.InvariantCulture) + ":" + EffectEvent.ToString("N")
        + "|" + Affected.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Excluded calls that change the run (docs/agents/preview.md, "Source-world animation session"). Whether and when an excluded
/// call changes the run is what its what-if run shows (<see cref="AnimationPlayer.RunWhatIf"/>): the same scenario with that call
/// path and everything below it included, compared with the scenario as it is (<see cref="AnimationPlayer.RunBaseline"/>, once per
/// scenario and range). This class picks the calls worth that run (<see cref="Candidates"/>) and turns a
/// result into the effect a notice shows (<see cref="Effect"/>). The pick is static, cheap and over-selects but never misses:
/// a call is a candidate when its target, or anything it can call in turn or reset (cleanup and RESET_STATE included, every
/// branch, whatever the prerequisites), has a STOP_ANIMATION, RESET_ANIMATION or INVALIDATE_ANIMATION whose name resolves, as
/// the player resolves it (the first entry of the name, never entry 0), to a binding that can be part of the base run: a scenario
/// start or a target of an included call path (every binding where the scenario admits every call but its exclusions). The walk
/// is bounded (<see cref="MaximumVisits"/> bindings, each once, so a call cycle ends; past it every call is a candidate) and
/// cached per called binding. Read-only over the index's frozen package.
/// </summary>
public sealed class AnimationExcludedEffects
{
    public const int MaximumVisits = 4096;
    /// <summary>The by-name targets one walk keeps; past it the call is a candidate whatever the run.</summary>
    public const int MaximumTargets = 1024;
    private readonly AnimationGraphIndex index;
    private readonly Dictionary<int, int[]?> cache = [];
    private readonly Lock gate = new();

    internal AnimationExcludedEffects(AnimationGraphIndex index) => this.index = index;

    /// <summary>
    /// The excluded calls <paramref name="frame"/> reached that could change the run, in the order they first dispatched: those
    /// a what-if run should check. <paramref name="policy"/> is the run's admission and <paramref name="starts"/> its starts' bindings.
    /// </summary>
    public IReadOnlyList<AnimationExcludedCall> Candidates(AnimationFrame frame, AnimationExecutionPolicy policy, IEnumerable<int> starts, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(policy);
        if (frame.ExcludedCalls.Count == 0) return [];
        var members = Members(policy, starts);
        int count = index.Package.Entries.Count;
        List<AnimationExcludedCall> found = [];
        foreach (var call in frame.ExcludedCalls)
        {
            token.ThrowIfCancellationRequested();
            // A name that resolves to nothing starts nothing.
            if (call.Target <= 0 || call.Target >= count) continue;
            // Past the walk's bounds, or with every binding possibly of the run, any by-name target counts.
            bool candidate = Reach(call.Target, token) switch
            {
                null => true,
                var reach when members == null => reach.Length > 0,
                var reach => reach.Any(members.Contains),
            };
            if (candidate) found.Add(call);
        }
        return found;
    }
    /// <summary>
    /// The bindings that can be part of the base run: the starts and the targets of the included call paths; null where every
    /// call but the exclusions runs (any binding can be), or where a path does not name its target.
    /// </summary>
    private static HashSet<int>? Members(AnimationExecutionPolicy policy, IEnumerable<int> starts)
    {
        if (policy.IncludesAllCalls) return null;
        HashSet<int> members = [.. starts];
        foreach (string path in policy.IncludedCalls)
        {
            int colon = path.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(path.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int target)) return null;
            members.Add(target);
        }
        return members;
    }

    /// <summary>
    /// Every binding a by-name stop, reset or invalidate names in <paramref name="root"/> and what it can call or reset in turn,
    /// all of its sequences and cleanup, sorted; null past the walk's bounds. Cached; deterministic.
    /// </summary>
    private int[]? Reach(int root, CancellationToken token)
    {
        lock (gate)
        {
            if (cache.TryGetValue(root, out var cached)) return cached;
            var entries = index.Package.Entries;
            HashSet<int> targets = [], visited = [root];
            Queue<int> pending = new([root]);
            bool over = false;
            while (!over && pending.TryDequeue(out int entry))
                foreach (var sequence in entries[entry].AllSequences)
                {
                    foreach (var ev in sequence.Events)
                    {
                        token.ThrowIfCancellationRequested();
                        if (index.Relate(ev, token) is not { Target: int target } relation || target <= 0 || target >= entries.Count) continue;
                        if (relation.Kind is AnimationGraphRelation.StopAnimation or AnimationGraphRelation.ResetAnimation or AnimationGraphRelation.InvalidateAnimation)
                        {
                            targets.Add(target);
                            if (targets.Count > MaximumTargets) { over = true; break; }
                            // A reset runs the binding's cleanup: what that does counts too.
                            if (relation.Kind != AnimationGraphRelation.ResetAnimation) continue;
                        }
                        if (visited.Add(target))
                        {
                            if (visited.Count > MaximumVisits) { over = true; break; }
                            pending.Enqueue(target);
                        }
                    }
                    if (over) break;
                }
            return cache[root] = over ? null : [.. targets.Order()];
        }
    }

    /// <summary>
    /// The effect a what-if <paramref name="result"/> of <paramref name="call"/> found, or null when it changed nothing. Include
    /// includes what the what-if included: under <paramref name="policy"/>'s every-call admission, the call's path and the
    /// exclusions below it; otherwise its path and the paths below it the what-if ran.
    /// </summary>
    public AnimationExcludedEffect? Effect(AnimationExcludedCall call, AnimationWhatIfResult result, AnimationExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(call); ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(policy);
        if (result.Effect is not { } found || result.Path != call.Path) return null;
        List<string> include = [call.Path];
        foreach (string path in policy.IncludesAllCalls ? policy.ExcludedCalls : result.Included)
            if (path != call.Path && AnimationExecutionPolicy.Below(path, call.Path)) include.Add(path);
        return new(call, found.Effect, found.Affected, found.Entry, found.Event, found.Time)
        {
            EffectSequence = found.Sequence, By = found.By, Through = Links(call.Path, found.InstancePath), IncludePaths = include.AsReadOnly(),
        };
    }
    /// <summary>The calls from <paramref name="path"/> down to <paramref name="below"/> (its segments "/caller:event:target"); empty when it is not below.</summary>
    private static AnimationExcludedLink[] Links(string path, string below)
    {
        if (below.Length <= path.Length || !AnimationExecutionPolicy.Below(below, path)) return [];
        List<AnimationExcludedLink> links = [];
        foreach (string segment in below[(path.Length + 1)..].Split('/'))
        {
            string[] parts = segment.Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int caller)
                || !Guid.TryParseExact(parts[1], "N", out Guid ev) || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int target)) return [];
            links.Add(new(caller, ev, target));
        }
        return [.. links];
    }

    /// <summary>"train_fall_trigger is excluded: in the game it stops destroy_tr_bridge at 23.017 s".</summary>
    public string Describe(AnimationExcludedEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        string verb = effect.Effect switch { AnimationExcludedEffectKind.Stop => "stops", AnimationExcludedEffectKind.Reset => "resets", _ => "invalidates" };
        string who = effect.Through.Count == 0 && effect.EffectEntry == effect.Call.Target ? "it"
            : Name(effect.EffectEntry) + (effect.Through.Count == 1 ? ", which it calls," : ", which it calls in turn,");
        return $"{Name(effect.Call.Target)} is excluded: in the game {who} {verb} {Name(effect.Affected)} at {AnimationSeconds.Format(effect.Seconds)}";
    }
    /// <summary>Timing's marker on the affected group: "would be stopped by excluded train_fall_trigger".</summary>
    public string Marker(AnimationExcludedEffect effect) => "would be " + effect.Effect switch
    {
        AnimationExcludedEffectKind.Stop => "stopped", AnimationExcludedEffectKind.Reset => "reset", _ => "invalidated",
    } + " by excluded " + Name(effect.Call.Target);
    private string Name(int entry) => entry > 0 && entry < index.Package.Entries.Count ? AnimationPhrases.Safe(index.Package.Entries[entry].Name) : "an animation";
}
