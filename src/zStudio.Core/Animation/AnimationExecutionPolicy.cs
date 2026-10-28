using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using zStudio.Core.Worlds;

namespace zStudio.Core.Animation;

/// <summary>A condition intervention in one revision-scoped dependency path, never a source edit. IF and ELSEIF events accept one.</summary>
public sealed record AnimationConditionOverride(string InstancePath, int Entry, Guid Event, bool Value);

/// <summary>Where range conditions and range gates measure from (docs/agents/preview.md, "Source-world animation session").</summary>
public enum AnimationPlayerReferenceKind
{
    /// <summary>The game's reference: the local player's AIV start, moved by callbacks 10 and 17 and frozen by callback 11.</summary>
    AtStart,
    /// <summary>A fixed point the scenario chooses; callbacks do not move it.</summary>
    Custom,
    /// <summary>No reference: range conditions are false and range-gated bindings never run.</summary>
    None,
}

/// <summary>The player reference a scenario starts from.</summary>
public sealed record AnimationPlayerReference
{
    public static AnimationPlayerReference AtStart { get; } = new(AnimationPlayerReferenceKind.AtStart, default);
    public static AnimationPlayerReference None { get; } = new(AnimationPlayerReferenceKind.None, default);
    public static AnimationPlayerReference Custom(Vector3 position) => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
        ? new(AnimationPlayerReferenceKind.Custom, position) : throw new ArgumentOutOfRangeException(nameof(position), "The player position must be finite.");
    public AnimationPlayerReferenceKind Kind { get; }
    /// <summary>The custom position; zero for the other kinds.</summary>
    public Vector3 Position { get; }
    private AnimationPlayerReference(AnimationPlayerReferenceKind kind, Vector3 position) { Kind = kind; Position = position; }
}

/// <summary>Which starts have their activation prerequisites checked (CheckActivationPrereqs 0x45db20).</summary>
public enum AnimationPrerequisiteRule
{
    /// <summary>As the game: every start, including the scenario's own, is refused while its prerequisites are unmet.</summary>
    Check,
    /// <summary>The scenario's own starts run whatever their prerequisites; every start the run makes is checked.</summary>
    IgnoreForScenarioStarts,
    /// <summary>Nothing is checked.</summary>
    Ignore,
}

/// <summary>Whether EXECUTION_BY_RANGE pauses a started binding while the player is outside its range.</summary>
public enum AnimationRangeGateRule { Game, Ignore }

/// <summary>Whether EXECUTION_BY_ZONE pauses a started binding while the view's zones exclude its attach node.</summary>
public enum AnimationZoneGateRule { Ignore, View }

/// <summary>A scenario's inputs and game rules for one preview run. Immutable; the defaults are the preview's.</summary>
public sealed record AnimationScenarioRules
{
    public static AnimationScenarioRules Default { get; } = new();
    private readonly AnimationPlayerReference player = AnimationPlayerReference.AtStart;
    private readonly ZoneSet? viewZones;
    public AnimationPlayerReference Player { get => player; init => player = value ?? throw new ArgumentNullException(nameof(value)); }
    public AnimationPrerequisiteRule Prerequisites { get; init; } = AnimationPrerequisiteRule.IgnoreForScenarioStarts;
    public AnimationRangeGateRule RangeGates { get; init; } = AnimationRangeGateRule.Game;
    public AnimationZoneGateRule ZoneGates { get; init; } = AnimationZoneGateRule.Ignore;
    /// <summary>
    /// The view camera's last complete zone set (one to three zones, none of them 0xFF), which View zone gates test. Null
    /// before the camera has one: zone-gated bindings then never run, as in the game before its first complete set.
    /// </summary>
    public ZoneSet? ViewZones
    {
        get => viewZones;
        init
        {
            if (value is { } zones && (zones.Count is < 1 or > 3 || zones.Ids.Any(id => id == 0xFF)))
                throw new ArgumentOutOfRangeException(nameof(value), "A view zone set needs one to three zones and no 0xFF (any).");
            viewZones = value;
        }
    }
}

/// <summary>
/// Immutable, finite admission for a manual preview, with the scenario's condition overrides and rules. An absent policy
/// preserves ordinary runtime execution (every call, default rules); <see cref="AllCalls"/> admits every call under its
/// rules; an empty policy admits only the root. Identities
/// belong to the frozen package and must not survive recompilation.
/// </summary>
public sealed class AnimationExecutionPolicy
{
    public const int MaximumPaths = 4096;
    public const int MaximumPathCharacters = 8192;
    private const int MaximumCharacters = 4 * 1024 * 1024;
    public static AnimationExecutionPolicy RootOnly { get; } = new();
    private readonly HashSet<string> included = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Path, int Entry, Guid Event), bool> conditions = [];
    public IReadOnlyList<string> IncludedCalls { get; }
    public IReadOnlyList<AnimationConditionOverride> ConditionOverrides { get; }
    /// <summary>The scenario's player reference, prerequisite, range and zone rules.</summary>
    public AnimationScenarioRules Rules { get; }
    internal long RetainedBytes { get; }

    public AnimationExecutionPolicy(IEnumerable<string>? includedCalls = null,
        IEnumerable<AnimationConditionOverride>? conditionOverrides = null, AnimationScenarioRules? rules = null)
    {
        Rules = rules ?? AnimationScenarioRules.Default;
        int characters = 0;
        if (includedCalls != null)
            foreach (string path in includedCalls)
            {
                ValidatePath(path, false);
                if (included.Contains(path)) continue;
                if (included.Count >= MaximumPaths || path.Length > MaximumCharacters - characters)
                    throw new InvalidDataException("Preview inclusion exceeds its dependency-path allowance.");
                characters += path.Length; included.Add(path);
            }
        List<AnimationConditionOverride> overrides = [];
        if (conditionOverrides != null)
            foreach (var item in conditionOverrides)
            {
                ArgumentNullException.ThrowIfNull(item);
                ValidatePath(item.InstancePath, true);
                ArgumentOutOfRangeException.ThrowIfNegative(item.Entry);
                var key = (item.InstancePath, item.Entry, item.Event);
                if (conditions.ContainsKey(key)) throw new InvalidDataException("A condition has more than one preview override.");
                if (overrides.Count >= MaximumPaths || item.InstancePath.Length > MaximumCharacters - characters)
                    throw new InvalidDataException("Preview conditions exceed their dependency-path allowance.");
                characters += item.InstancePath.Length; conditions.Add(key, item.Value); overrides.Add(item);
            }
        IncludedCalls = Array.AsReadOnly(included.Order(StringComparer.Ordinal).ToArray());
        ConditionOverrides = new ReadOnlyCollection<AnimationConditionOverride>(overrides);
        RetainedBytes = 256L + 256L * (included.Count + conditions.Count) + 2L * characters;
    }

    /// <summary>The same admission and overrides with other rules.</summary>
    public AnimationExecutionPolicy WithRules(AnimationScenarioRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules == Rules ? this : IncludesAllCalls ? AllCalls(rules, ConditionOverrides, ExcludedCalls) : new(IncludedCalls, ConditionOverrides, rules);
    }

    /// <summary>
    /// Every call is admitted, as at run time without a policy, but under the scenario's rules and overrides: the isolated
    /// preview of a compiled animation runs all its calls. Calls keep their canonical paths, which overrides may scope.
    /// <paramref name="excludedCalls"/> are left out with every path below them (the isolated preview's include switches).
    /// </summary>
    public static AnimationExecutionPolicy AllCalls(AnimationScenarioRules? rules = null, IEnumerable<AnimationConditionOverride>? conditionOverrides = null,
        IEnumerable<string>? excludedCalls = null)
    {
        var policy = new AnimationExecutionPolicy(null, conditionOverrides, rules) { IncludesAllCalls = true };
        if (excludedCalls != null)
        {
            int characters = 0;
            foreach (string path in excludedCalls)
            {
                ValidatePath(path, false);
                if (policy.excluded.Count >= MaximumPaths || path.Length > MaximumCharacters - characters)
                    throw new InvalidDataException("Preview exclusion exceeds its dependency-path allowance.");
                if (policy.excluded.Add(path)) characters += path.Length;
            }
            policy.ExcludedCalls = Array.AsReadOnly(policy.excluded.Order(StringComparer.Ordinal).ToArray());
        }
        return policy;
    }

    /// <summary>The policy admits every call (<see cref="AllCalls"/>) but <see cref="ExcludedCalls"/>; <see cref="IncludedCalls"/> is then empty.</summary>
    public bool IncludesAllCalls { get; private init; }
    /// <summary>With <see cref="IncludesAllCalls"/>: the call paths left out, each with every path below it.</summary>
    public IReadOnlyList<string> ExcludedCalls { get; private set; } = [];
    private readonly HashSet<string> excluded = new(StringComparer.Ordinal);
    /// <summary>Whether <paramref name="path"/> or a call path above it is excluded.</summary>
    private bool Excluded(string path)
    {
        if (excluded.Count == 0 || path.Length == 0) return false;
        if (excluded.Contains(path)) return true;
        for (int i = path.IndexOf('/', 1); i > 0; i = path.IndexOf('/', i + 1))
            if (excluded.Contains(path[..i])) return true;
        return false;
    }

    /// <summary>Canonical child path shared by structure inspection, runtime admission and audio discovery.</summary>
    public static string ChildPath(string parentPath, int callerEntry, Guid eventId, int targetEntry)
    {
        ValidatePath(parentPath, true);
        ArgumentOutOfRangeException.ThrowIfNegative(callerEntry);
        if (targetEntry < -1) throw new ArgumentOutOfRangeException(nameof(targetEntry));
        string segment = string.Create(CultureInfo.InvariantCulture, $"/{callerEntry}:{eventId:N}:{targetEntry}");
        if (parentPath.Length > MaximumPathCharacters - segment.Length)
            throw new InvalidDataException("Preview dependency path exceeds its length allowance.");
        return parentPath + segment;
    }

    public bool Includes(string childPath) => IncludesAllCalls ? !Excluded(childPath) : included.Contains(childPath);
    internal bool TryInclude(string childPath, out string canonical)
    {
        if (IncludesAllCalls) { canonical = childPath; return !Excluded(childPath); }
        return included.TryGetValue(childPath, out canonical!);
    }
    public bool TryCondition(string instancePath, int entry, Guid eventId, out bool value)
        => conditions.TryGetValue((instancePath, entry, eventId), out value);

    private static void ValidatePath(string path, bool allowRoot)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > MaximumPathCharacters || !allowRoot && path.Length == 0 || path.Length > 0 && path[0] != '/')
            throw new InvalidDataException("Invalid revision-scoped animation call path.");
    }
}
