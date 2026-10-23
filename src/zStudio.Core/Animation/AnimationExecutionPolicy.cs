using System.Collections.ObjectModel;
using System.Globalization;

namespace zStudio.Core.Animation;

/// <summary>A condition intervention in one revision-scoped dependency path, never a source edit.</summary>
public sealed record AnimationConditionOverride(string InstancePath, int Entry, Guid Event, bool Value);

/// <summary>
/// Immutable, finite admission for a manual preview. An absent policy preserves ordinary runtime execution;
/// an empty policy admits only the root. Identities belong to the frozen package and must not survive recompilation.
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
    internal long RetainedBytes { get; }

    public AnimationExecutionPolicy(IEnumerable<string>? includedCalls = null,
        IEnumerable<AnimationConditionOverride>? conditionOverrides = null)
    {
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

    public bool Includes(string childPath) => included.Contains(childPath);
    internal bool TryInclude(string childPath, out string canonical) => included.TryGetValue(childPath, out canonical!);
    public bool TryCondition(string instancePath, int entry, Guid eventId, out bool value)
        => conditions.TryGetValue((instancePath, entry, eventId), out value);

    private static void ValidatePath(string path, bool allowRoot)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > MaximumPathCharacters || !allowRoot && path.Length == 0 || path.Length > 0 && path[0] != '/')
            throw new InvalidDataException("Invalid revision-scoped animation call path.");
    }
}
