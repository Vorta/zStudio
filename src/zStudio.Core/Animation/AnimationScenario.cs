using System.Globalization;
using System.Numerics;
using zStudio.Core.Worlds;

namespace zStudio.Core.Animation;

/// <summary>The game's Effects detail option that ANIMATION_LOD conditions test (High with Direct3D, Medium with the software renderer).</summary>
public enum AnimationEffectsDetail { Low = 0, Medium = 1, High = 2 }

/// <summary>What the preview plays: the runtime sequences, or the cleanup alone.</summary>
public enum AnimationScenarioPhase { Runtime, Cleanup }

/// <summary>A condition switch: the game's rule with the scenario's inputs, or forced true or false.</summary>
public enum AnimationConditionChoice { Game, True, False }

/// <summary>What Include all did: the call paths it added, and those it left excluded and why.</summary>
public sealed record AnimationIncludeAllResult(int Included, int BackLinks, int Unresolved, int OverBudget)
{
    public string Summary => $"Included {Included} call path{(Included == 1 ? "" : "s")}" +
        (BackLinks + Unresolved + OverBudget == 0 ? "." : "; " + string.Join(", ", new[]
        {
            BackLinks > 0 ? $"{BackLinks} back-link{(BackLinks == 1 ? "" : "s")} stay excluded" : null,
            Unresolved > 0 ? $"{Unresolved} call{(Unresolved == 1 ? "" : "s")} to missing animations" : null,
            OverBudget > 0 ? $"{OverBudget} beyond the {AnimationExecutionPolicy.MaximumPaths:N0}-path budget" : null,
        }.OfType<string>()) + ".");
}

/// <summary>
/// One call path the starts can reach: the calling binding and event, the binding it starts, its depth below its start, and
/// whether it calls back into its own chain (a back-link: listed, never followed or included by Include all).
/// </summary>
public sealed record AnimationScenarioCallPath(string Path, string Parent, int Caller, int Target, Guid Event, int Depth, bool BackLink);

/// <summary>Every call path the starts reach (at most <see cref="AnimationExecutionPolicy.MaximumPaths"/>), with the calls to missing animations counted.</summary>
public sealed record AnimationScenarioCallPaths(IReadOnlyList<AnimationScenarioCallPath> Paths, int Unresolved, bool Truncated);

/// <summary>
/// The preview setup of one map (docs/agents/preview.md, "Animation scenario"): its starts on the shared clock (at most 32;
/// the opened binding is the primary, <see cref="Guid.Empty"/>, whose instance path is ""), the call paths it includes, the
/// condition switches it forces (IF and ELSEIF, per instance path, entry and event), its inputs (player, random seed,
/// effects detail, phase, height, beam origin) and its rules (prerequisites, range and zone gates). Immutable; identities
/// belong to one compilation, and <see cref="AnimationScenarioSession.Rebind"/> remaps them after a recompile.
/// </summary>
public sealed record AnimationScenario
{
    public static AnimationScenario Default { get; } = new();
    /// <summary>The default scenario of <paramref name="primary"/>: it starts at 0 s.</summary>
    public static AnimationScenario For(int primary) => Default.WithPrimary(primary);
    /// <summary>
    /// The default scenario of a compiled animation's isolated preview: <paramref name="primary"/> at 0 s, every call it reaches
    /// included (<see cref="AllCalls"/>), range gates ignored (the isolated preview has no game player to measure from until one is
    /// set), the other inputs and rules as in a map.
    /// </summary>
    public static AnimationScenario Isolated(int primary)
    {
        var isolated = new AnimationScenario { IsolatedPreview = true, AllCalls = true, RangeGates = AnimationRangeGateRule.Ignore };
        // Entry 0 (the compiler's placeholder) has nothing to start: its scenario has no start.
        return primary > 0 ? isolated.WithPrimary(primary) : isolated;
    }

    private readonly IReadOnlyList<AnimationScheduledStart> starts = [];
    private readonly IReadOnlyList<string> includedCalls = [];
    private readonly IReadOnlyList<string> excludedCalls = [];
    private readonly IReadOnlyList<AnimationConditionOverride> conditions = [];
    private readonly AnimationPlayerReference player = AnimationPlayerReference.AtStart;
    private readonly float height;
    private readonly Vector3? beamOrigin;
    private sealed record IncludedSet(IReadOnlyList<string> Source, HashSet<string> Set);
    private IncludedSet? included;

    /// <summary>The starts in the order given; at most one primary (<see cref="Guid.Empty"/>).</summary>
    public IReadOnlyList<AnimationScheduledStart> Starts
    {
        get => starts;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var copy = value.ToArray(); HashSet<Guid> ids = [];
            if (copy.Length > AnimationSchedule.MaximumStarts) throw new InvalidDataException($"Use at most {AnimationSchedule.MaximumStarts} starts, including the primary.");
            foreach (var start in copy)
                if (start == null || !ids.Add(start.Id) || start.Entry <= 0 || !double.IsFinite(start.Seconds) || start.Seconds is < 0 or > AnimationSeconds.Maximum)
                    throw new InvalidDataException("Each start needs its own identity, an animation binding (entry 0 is the compiler's placeholder) and a time within 0–3600 seconds.");
            starts = Array.AsReadOnly(copy);
        }
    }
    /// <summary>The included call paths, canonical and in ordinal order.</summary>
    public IReadOnlyList<string> IncludedCalls
    {
        get => includedCalls;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var copy = value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (copy.Length > AnimationExecutionPolicy.MaximumPaths) throw new InvalidDataException($"Include at most {AnimationExecutionPolicy.MaximumPaths:N0} call paths.");
            foreach (string path in copy) CheckPath(path, false);
            includedCalls = Array.AsReadOnly(copy);
        }
    }
    /// <summary>
    /// A compiled animation's isolated preview (never saved): its one start is the shown animation, and its calls are switched
    /// by exclusion (<see cref="AllCalls"/>) until Exclude all, after which <see cref="IncludedCalls"/> lists what runs.
    /// </summary>
    public bool IsolatedPreview { get; init; }
    /// <summary>Every call the starts reach runs except <see cref="ExcludedCalls"/> and what lies below them (an isolated preview's default).</summary>
    public bool AllCalls { get; init; }
    /// <summary>With <see cref="AllCalls"/>: the call paths left out, canonical and in ordinal order; each excludes the paths below it.</summary>
    public IReadOnlyList<string> ExcludedCalls
    {
        get => excludedCalls;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var copy = value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (copy.Length > AnimationExecutionPolicy.MaximumPaths) throw new InvalidDataException($"Exclude at most {AnimationExecutionPolicy.MaximumPaths:N0} call paths.");
            foreach (string path in copy) CheckPath(path, false);
            excludedCalls = Array.AsReadOnly(copy);
        }
    }
    /// <summary>The forced conditions (IF and ELSEIF), one per instance path, entry and event.</summary>
    public IReadOnlyList<AnimationConditionOverride> Conditions
    {
        get => conditions;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var copy = value.ToArray(); HashSet<(string, int, Guid)> keys = [];
            if (copy.Length > AnimationExecutionPolicy.MaximumPaths) throw new InvalidDataException($"Force at most {AnimationExecutionPolicy.MaximumPaths:N0} conditions.");
            foreach (var item in copy)
            {
                ArgumentNullException.ThrowIfNull(item); CheckPath(item.InstancePath, true);
                if (item.Entry <= 0 || !keys.Add((item.InstancePath, item.Entry, item.Event))) throw new InvalidDataException("A condition is forced at most once per call path, on an animation binding.");
            }
            conditions = Array.AsReadOnly(copy);
        }
    }
    /// <summary>Where range conditions and gates measure from: at start (default), a custom point, or no player.</summary>
    public AnimationPlayerReference Player { get => player; init => player = value ?? throw new ArgumentNullException(nameof(value)); }
    /// <summary>The preview's random table seed (random in the game).</summary>
    public int Seed { get; init; } = 1;
    public AnimationEffectsDetail EffectsDetail { get; init; } = AnimationEffectsDetail.High;
    public AnimationScenarioPhase Phase { get; init; } = AnimationScenarioPhase.Runtime;
    /// <summary>A preview-only world-Y offset, −999…999 game units.</summary>
    public float Height
    {
        get => height;
        init => height = float.IsFinite(value) && value is >= -999 and <= 999 ? value : throw new InvalidDataException("Height must be between -999 and 999 game units.");
    }
    /// <summary>Where beam events start that take the activation's input position; null is the binding's root.</summary>
    public Vector3? BeamOrigin
    {
        get => beamOrigin;
        init => beamOrigin = value is not Vector3 p || float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) ? value : throw new InvalidDataException("The beam origin must be finite.");
    }
    public AnimationPrerequisiteRule Prerequisites { get; init; } = AnimationPrerequisiteRule.IgnoreForScenarioStarts;
    public AnimationRangeGateRule RangeGates { get; init; } = AnimationRangeGateRule.Game;
    public AnimationZoneGateRule ZoneGates { get; init; } = AnimationZoneGateRule.Ignore;

    /// <summary>The opened binding, when one is.</summary>
    public int? Primary => starts.FirstOrDefault(s => s.Id == Guid.Empty)?.Entry;
    public AnimationScheduledStart? Start(Guid id) => starts.FirstOrDefault(s => s.Id == id);
    /// <summary>Anything differs from the defaults of its primary (the summary chip's accent dot).</summary>
    public bool IsCustomized => !Equals(Primary is int primary ? IsolatedPreview ? Isolated(primary) : For(primary) : Default);
    /// <summary>The custom player position, which beam events also take as their input target.</summary>
    public Vector3? CustomPlayer => player.Kind == AnimationPlayerReferenceKind.Custom ? player.Position : null;

    public bool Includes(string path)
    {
        if (AllCalls) return !ExcludedByPath(path);
        var set = included;
        if (set == null || !ReferenceEquals(set.Source, includedCalls)) included = set = new(includedCalls, new(includedCalls, StringComparer.Ordinal));
        return set.Set.Contains(path);
    }
    /// <summary>With <see cref="AllCalls"/>: <paramref name="path"/> or a path above it is excluded.</summary>
    private bool ExcludedByPath(string path) => excludedCalls.Any(e => path == e || path.StartsWith(e + "/", StringComparison.Ordinal));
    /// <summary>The switch of one condition in one call path.</summary>
    public AnimationConditionChoice Choice(string instancePath, int entry, Guid ev) =>
        conditions.FirstOrDefault(c => c.InstancePath == instancePath && c.Entry == entry && c.Event == ev) is { } forced
            ? forced.Value ? AnimationConditionChoice.True : AnimationConditionChoice.False : AnimationConditionChoice.Game;

    /// <summary>The run's schedule: the primary and the other starts.</summary>
    public AnimationSchedule Schedule(int entryCount) => Primary == null
        ? throw new InvalidDataException("The scenario has no primary start: open an animation binding first.") : new(starts, entryCount);

    /// <summary>The player's admission, condition overrides and rules; <paramref name="viewZones"/> are the view's zones that View zone gates test.</summary>
    public AnimationExecutionPolicy ToExecutionPolicy(ZoneSet? viewZones = null)
    {
        var rules = new AnimationScenarioRules
        {
            Player = player, Prerequisites = Prerequisites, RangeGates = RangeGates, ZoneGates = ZoneGates,
            ViewZones = ZoneGates == AnimationZoneGateRule.View ? viewZones : null,
        };
        return AllCalls ? AnimationExecutionPolicy.AllCalls(rules, conditions, excludedCalls) : new(includedCalls, conditions, rules);
    }

    /// <summary>
    /// Makes <paramref name="entry"/> the primary: in place when it is already a start (its paths become the primary's), else
    /// at the previous primary's time. With <paramref name="keepPrevious"/> the previous primary stays as an ordinary start
    /// and keeps its paths and switches; without, they go.
    /// </summary>
    public AnimationScenario WithPrimary(int entry, bool keepPrevious = true)
    {
        if (entry <= 0) throw new InvalidDataException("Choose an animation binding (entry 0 is the compiler's placeholder).");
        var previous = Start(Guid.Empty);
        if (previous?.Entry == entry) return this;
        var existing = starts.FirstOrDefault(s => s.Id != Guid.Empty && s.Entry == entry);
        List<AnimationScheduledStart> next = [];
        Guid moved = Guid.NewGuid();
        string movedRoot = "/@" + moved.ToString("N");
        string? promotedRoot = existing == null ? null : existing.Path;
        foreach (var start in starts)
        {
            if (start.Id == Guid.Empty) { if (keepPrevious) next.Add(start with { Id = moved }); continue; }
            if (existing != null && start.Id == existing.Id) continue;
            next.Add(start);
        }
        next.Insert(0, new(Guid.Empty, entry, existing?.Seconds ?? previous?.Seconds ?? 0));
        if (next.Count > AnimationSchedule.MaximumStarts) throw new InvalidOperationException($"Remove a start first ({AnimationSchedule.MaximumStarts} starts).");
        string? Rehome(string path)
        {
            if (promotedRoot != null && (path == promotedRoot || path.StartsWith(promotedRoot + "/", StringComparison.Ordinal))) return path[promotedRoot.Length..];
            if (path.Length == 0 || path[0] == '/' && !path.StartsWith("/@", StringComparison.Ordinal)) return previous != null && keepPrevious ? movedRoot + path : null;
            return path;
        }
        return this with
        {
            Starts = next,
            IncludedCalls = includedCalls.Select(Rehome).OfType<string>().Where(p => p.Length > 0).ToArray(),
            ExcludedCalls = excludedCalls.Select(Rehome).OfType<string>().Where(p => p.Length > 0).ToArray(),
            Conditions = conditions.Select(c => Rehome(c.InstancePath) is { } path ? c with { InstancePath = path } : null).OfType<AnimationConditionOverride>().ToArray(),
        };
    }

    /// <summary>Adds a start (never the primary) at <paramref name="seconds"/>.</summary>
    public AnimationScenario WithStart(int entry, double seconds, Guid? id = null)
    {
        Guid identity = id ?? Guid.NewGuid();
        if (identity == Guid.Empty) throw new InvalidDataException("The primary start is the opened binding; add another start with its own identity.");
        return this with { Starts = [.. starts, new(identity, entry, seconds)] };
    }
    /// <summary>Removes a start with its call paths and switches. The primary cannot be removed.</summary>
    public AnimationScenario WithoutStart(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("The primary start cannot be removed.");
        if (Start(id) is not { } start) return this;
        bool Keep(string path) => !(path == start.Path || path.StartsWith(start.Path + "/", StringComparison.Ordinal));
        return this with
        {
            Starts = starts.Where(s => s.Id != id).ToArray(),
            IncludedCalls = includedCalls.Where(Keep).ToArray(), ExcludedCalls = excludedCalls.Where(Keep).ToArray(), Conditions = conditions.Where(c => Keep(c.InstancePath)).ToArray(),
        };
    }
    public AnimationScenario WithStartTime(Guid id, double seconds) => Start(id) == null
        ? throw new InvalidDataException("The start is not part of this scenario.") : this with { Starts = starts.Select(s => s.Id == id ? s with { Seconds = seconds } : s).ToArray() };

    /// <summary>Includes a call path, or excludes it with every path below it.</summary>
    public AnimationScenario WithInclusion(string path, bool value)
    {
        CheckPath(path, false);
        if (AllCalls)
        {
            // Including a path includes the calls that lead to it (no exclusion above it stays); excluding it covers what is below it.
            if (value) return Includes(path) ? this : this with { ExcludedCalls = excludedCalls.Where(e => path != e && !path.StartsWith(e + "/", StringComparison.Ordinal)).ToArray() };
            return this with { ExcludedCalls = [.. excludedCalls.Where(e => !e.StartsWith(path + "/", StringComparison.Ordinal)), path] };
        }
        if (value) return Includes(path) ? this : this with { IncludedCalls = [.. includedCalls, path] };
        return this with { IncludedCalls = includedCalls.Where(p => p != path && !p.StartsWith(path + "/", StringComparison.Ordinal)).ToArray() };
    }
    /// <summary>Only the starts run; an isolated preview leaves its all-calls default (Include all brings it back).</summary>
    public AnimationScenario ExcludeAll() => AllCalls ? this with { AllCalls = false, ExcludedCalls = [], IncludedCalls = [] }
        : includedCalls.Count == 0 ? this : this with { IncludedCalls = [] };

    /// <summary>Sets one condition's switch in one call path; Game removes its override.</summary>
    public AnimationScenario WithCondition(string instancePath, int entry, Guid ev, AnimationConditionChoice choice)
    {
        var rest = conditions.Where(c => c.InstancePath != instancePath || c.Entry != entry || c.Event != ev);
        return this with { Conditions = choice == AnimationConditionChoice.Game ? rest.ToArray() : [.. rest, new(instancePath, entry, ev, choice == AnimationConditionChoice.True)] };
    }

    /// <summary>
    /// Includes every call path the starts reach, except back-links (a call to a binding already on its path) and calls to
    /// missing animations, up to the path budget, keeping the paths already included.
    /// </summary>
    public (AnimationScenario Scenario, AnimationIncludeAllResult Result) IncludeAll(AnimationPackage package, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (IsolatedPreview)
        {
            // An isolated preview runs every call again, back-links too (the game refuses a call to a running animation itself).
            var reach = CallPaths(package, token);
            return (this with { AllCalls = true, ExcludedCalls = [], IncludedCalls = [] }, new(reach.Paths.Count, 0, reach.Unresolved, reach.Truncated ? 1 : 0));
        }
        HashSet<string> paths = new(includedCalls, StringComparer.Ordinal);
        int added = 0, backLinks = 0, unresolved = 0, over = 0;
        AnimationEntryLookup lookup = new(package);
        Queue<(string Path, int Entry, int[] Chain)> pending = new(starts.Where(s => s.Entry < package.Entries.Count).Select(s => (s.Path, s.Entry, new[] { s.Entry })));
        while (pending.TryDequeue(out var item))
            foreach (var sequence in package.Entries[item.Entry].AllSequences)
                foreach (var ev in sequence.Events)
                {
                    token.ThrowIfCancellationRequested();
                    if (ev.Type is not (19 or 24) || ev.Bytes.Length < 80) continue;
                    var target = lookup.ResolveChild(ev, token);
                    if (target == null) { unresolved++; continue; }
                    if (item.Chain.Contains(target.Index)) { backLinks++; continue; }
                    string path;
                    try { path = AnimationExecutionPolicy.ChildPath(item.Path, item.Entry, ev.Id, target.Index); }
                    catch (InvalidDataException) { over++; continue; }
                    if (!paths.Contains(path))
                    {
                        if (paths.Count >= AnimationExecutionPolicy.MaximumPaths) { over++; continue; }
                        paths.Add(path); added++;
                    }
                    pending.Enqueue((path, target.Index, [.. item.Chain, target.Index]));
                }
        return (this with { IncludedCalls = paths.ToArray() }, new(added, backLinks, unresolved, over));
    }

    /// <summary>
    /// Every call path the starts reach, in breadth-first order from the starts in their order, whether included or not: what
    /// the Scenario tab lists under Called animations. A back-link is listed and not followed; a call to a missing animation
    /// is only counted. At most <see cref="AnimationExecutionPolicy.MaximumPaths"/> paths.
    /// </summary>
    public AnimationScenarioCallPaths CallPaths(AnimationPackage package, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        List<AnimationScenarioCallPath> paths = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int unresolved = 0; bool truncated = false;
        AnimationEntryLookup lookup = new(package);
        Queue<(string Path, int Entry, int[] Chain)> pending = new(starts.Where(s => s.Entry < package.Entries.Count).Select(s => (s.Path, s.Entry, new[] { s.Entry })));
        while (pending.TryDequeue(out var item) && !truncated)
            foreach (var sequence in package.Entries[item.Entry].AllSequences)
            {
                foreach (var ev in sequence.Events)
                {
                    token.ThrowIfCancellationRequested();
                    if (ev.Type is not (19 or 24) || ev.Bytes.Length < 80) continue;
                    var target = lookup.ResolveChild(ev, token);
                    if (target == null) { unresolved++; continue; }
                    string path;
                    try { path = AnimationExecutionPolicy.ChildPath(item.Path, item.Entry, ev.Id, target.Index); }
                    catch (InvalidDataException) { truncated = true; break; }
                    if (!seen.Add(path)) continue;
                    if (paths.Count >= AnimationExecutionPolicy.MaximumPaths) { truncated = true; break; }
                    bool backLink = item.Chain.Contains(target.Index);
                    paths.Add(new(path, item.Path, item.Entry, target.Index, ev.Id, item.Chain.Length, backLink));
                    if (!backLink) pending.Enqueue((path, target.Index, [.. item.Chain, target.Index]));
                }
                if (truncated) break;
            }
        return new(paths.AsReadOnly(), unresolved, truncated);
    }

    private static void CheckPath(string path, bool allowRoot)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > AnimationExecutionPolicy.MaximumPathCharacters || !allowRoot && path.Length == 0 || path.Length > 0 && path[0] != '/')
            throw new InvalidDataException("Invalid revision-scoped animation call path.");
    }

    public bool Equals(AnimationScenario? other) => other is not null && (ReferenceEquals(this, other) ||
        starts.SequenceEqual(other.starts) && includedCalls.SequenceEqual(other.includedCalls, StringComparer.Ordinal) && conditions.SequenceEqual(other.conditions)
        && IsolatedPreview == other.IsolatedPreview && AllCalls == other.AllCalls && excludedCalls.SequenceEqual(other.excludedCalls, StringComparer.Ordinal)
        && player == other.player && Seed == other.Seed && EffectsDetail == other.EffectsDetail && Phase == other.Phase && height.Equals(other.height)
        && beamOrigin == other.beamOrigin && Prerequisites == other.Prerequisites && RangeGates == other.RangeGates && ZoneGates == other.ZoneGates);
    public override int GetHashCode() => HashCode.Combine(starts.Count, includedCalls.Count, conditions.Count, player, Seed, EffectsDetail, Phase,
        HashCode.Combine(height, beamOrigin, Prerequisites, RangeGates, ZoneGates, AllCalls, excludedCalls.Count));

    /// <summary>A one-line summary: "2 starts · 24 calls included · 1 override" (an isolated preview: "all calls · 2 excluded").</summary>
    public string Summary => string.Join(" · ", new[]
    {
        $"{starts.Count} start{(starts.Count == 1 ? "" : "s")}",
        AllCalls ? "all calls" + (excludedCalls.Count > 0 ? $" · {excludedCalls.Count.ToString("N0", CultureInfo.InvariantCulture)} excluded" : "") : null,
        includedCalls.Count > 0 ? $"{includedCalls.Count.ToString("N0", CultureInfo.InvariantCulture)} call{(includedCalls.Count == 1 ? "" : "s")} included" : null,
        conditions.Count > 0 ? $"{conditions.Count} override{(conditions.Count == 1 ? "" : "s")}" : null,
    }.OfType<string>());
}

/// <summary>One scenario change: what it was, what it is now, and why ("edit", "reset", "restore", "remap").</summary>
public sealed record AnimationScenarioChange(AnimationScenario Previous, AnimationScenario Current, string Reason);

/// <summary>Something a restore or remap could not keep, said instead of dropped silently.</summary>
public sealed record AnimationScenarioNotice(string Kind, string Text);

/// <summary>The compilation a scenario's identities belong to: its package and, for a source map, its source provenance.</summary>
public sealed record AnimationScenarioSource(AnimationPackage Package, AnimationSourceProvenance? Provenance)
{
    private AnimationScenarioAddresses? addresses;
    internal AnimationScenarioAddresses Addresses => addresses ??= new(this);
}

/// <summary>
/// The one scenario of a map session, behind every view of it (Scenario tab, summary, switches, starts, MCP): the current
/// immutable scenario, a change event, and the compilation its identities belong to. Not thread-safe: one owner thread.
/// </summary>
public sealed class AnimationScenarioSession
{
    public AnimationScenarioSession(AnimationScenarioSource source, AnimationScenario? initial = null, IEnumerable<AnimationScenarioNotice>? notices = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Current = initial ?? AnimationScenario.Default;
        Notices = (notices ?? []).ToArray();
    }

    public AnimationScenarioSource Source { get; private set; }
    public AnimationScenario Current { get; private set; }
    /// <summary>What the last restore or remap could not keep.</summary>
    public IReadOnlyList<AnimationScenarioNotice> Notices { get; private set; }
    public event Action<AnimationScenarioChange>? Changed;

    /// <summary>Replaces the scenario; no event when nothing changed.</summary>
    public void Set(AnimationScenario next, string reason = "edit")
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.Equals(Current)) return;
        var previous = Current; Current = next;
        Changed?.Invoke(new(previous, next, reason));
    }
    public void Reset() => Set(AnimationScenario.Default, "reset");

    /// <summary>The scenario's durable form: source addresses instead of this compilation's identities. Null without provenance.</summary>
    public AnimationScenarioRecord? ToRecord() => AnimationScenarioRecord.From(Current, Source);

    /// <summary>
    /// Moves the scenario to a new compilation of the same map (after an edit, undo or redo): starts, paths and switches are
    /// remapped through source provenance (exact source addresses, never names); what no longer matches is dropped and
    /// listed in <see cref="Notices"/>.
    /// </summary>
    public IReadOnlyList<AnimationScenarioNotice> Rebind(AnimationScenarioSource next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (ReferenceEquals(next.Package, Source.Package)) { Source = next; return []; }
        List<AnimationScenarioNotice> notices = [];
        AnimationScenario remapped;
        if (AnimationScenarioRecord.From(Current, Source, notices) is { } record) remapped = record.Resolve(next, notices);
        else
        {
            remapped = AnimationScenario.Default with
            {
                Player = Current.Player, Seed = Current.Seed, EffectsDetail = Current.EffectsDetail, Phase = Current.Phase, Height = Current.Height,
                BeamOrigin = Current.BeamOrigin, Prerequisites = Current.Prerequisites, RangeGates = Current.RangeGates, ZoneGates = Current.ZoneGates,
            };
            if (Current.Starts.Count + Current.IncludedCalls.Count + Current.Conditions.Count > 0)
                notices.Add(new("source", "The animations have no source addresses, so the scenario's starts, call paths and switches could not be kept."));
        }
        Source = next; Notices = notices.AsReadOnly();
        Set(remapped, "remap");
        return Notices;
    }

    /// <summary>A session restored from a saved scenario (<see cref="AnimationScenarioRecord.ToJson"/>), or the default one.</summary>
    public static AnimationScenarioSession Restore(AnimationScenarioSource source, System.Text.Json.Nodes.JsonObject? saved)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (saved == null) return new(source);
        List<AnimationScenarioNotice> notices = [];
        var record = AnimationScenarioRecord.FromJson(saved, notices);
        return new(source, record?.Resolve(source, notices) ?? AnimationScenario.Default, notices);
    }
}
