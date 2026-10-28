using System.Collections.ObjectModel;

namespace zStudio.Core.Animation;

/// <summary>
/// The map-wide relations a Structure graph needs and cannot see from one binding: who calls or names each binding (the
/// callers index), whose prerequisites name it, the definition each binding comes from, and the first entry of each name,
/// as the runtime resolves calls (a positive entry cache, else the first entry of the name) and by-name events (the first
/// entry of the name, never entry 0). Read-only over a caller-owned frozen package; build again after an edit.
/// </summary>
public sealed class AnimationGraphIndex
{
    private readonly AnimationEntryLookup lookup;
    private readonly Dictionary<int, List<AnimationGraphCaller>> callers = [];
    private readonly Dictionary<int, List<int>> neededBy = [];
    private readonly Dictionary<string, int[]> groups = new(StringComparer.Ordinal);
    private readonly string[] groupOf;
    private readonly Dictionary<string, List<int>> names = new(StringComparer.Ordinal);
    private readonly HashSet<int> newGameStart = [];

    public AnimationPackage Package { get; }
    public AnimationSourceProvenance? Provenance { get; }
    public AnimationGraphOptions Options { get; }
    public Guid Id { get; } = Guid.NewGuid();

    private AnimationGraphIndex(AnimationPackage package, AnimationSourceProvenance? provenance, AnimationGraphOptions options, CancellationToken token)
    {
        Package = package; Provenance = provenance; Options = options; lookup = new(package);
        groupOf = new string[package.Entries.Count];
        Dictionary<string, List<int>> grouping = new(StringComparer.Ordinal);
        foreach (var entry in package.Entries)
        {
            token.ThrowIfCancellationRequested();
            string key = provenance?.FindEntry(entry.Index) is { } source ? "d" + source.Definition : "n:" + entry.Name;
            groupOf[entry.Index] = key;
            if (!grouping.TryGetValue(key, out var members)) grouping.Add(key, members = []);
            members.Add(entry.Index);
            if (!names.TryGetValue(entry.Name, out var named)) names.Add(entry.Name, named = []);
            named.Add(entry.Index);
        }
        foreach (var (key, members) in grouping) groups.Add(key, [.. members]);
        foreach (var entry in package.Entries)
        {
            foreach (var sequence in entry.AllSequences)
                foreach (var ev in sequence.Events)
                {
                    token.ThrowIfCancellationRequested();
                    if (Relate(ev, token) is not { Target: int target } relation) continue;
                    if (!callers.TryGetValue(target, out var list)) callers.Add(target, list = []);
                    list.Add(new(entry.Index, entry.Name, sequence.Id, ev.Id, relation.Kind, target));
                }
            foreach (var record in entry.References[6])
            {
                if (record.Bytes.Length < 48 || record.Bytes[4] != 1) continue;
                if (lookup.Find(record.Text(8, 32), token) is not { } required) continue;
                if (!neededBy.TryGetValue(required.Index, out var list)) neededBy.Add(required.Index, list = []);
                if (!list.Contains(entry.Index)) list.Add(entry.Index);
            }
        }
        foreach (string name in options.NewGameStart)
            if (lookup.Find(name, token) is { } opening) newGameStart.Add(opening.Index);
    }

    public static AnimationGraphIndex Create(AnimationPackage package, AnimationSourceProvenance? provenance = null,
        AnimationGraphOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new(package, provenance, options ?? AnimationGraphOptions.Default, token);
    }

    /// <summary>The definition group a binding belongs to: "d{definition}" with source provenance, else "n:{name}".</summary>
    public string Group(int entry) => groupOf[Require(entry)];
    /// <summary>A group's bindings in entry order.</summary>
    public IReadOnlyList<int> GroupEntries(string group) => groups.TryGetValue(group, out var members) ? members : [];
    /// <summary>A group's name as written ("rdrail_destroy0*"), else its bindings' shared name or common prefix with a star.</summary>
    public string GroupName(string group)
    {
        if (group.StartsWith("u:", StringComparison.Ordinal)) return group[2..];
        var members = GroupEntries(group);
        if (members.Count == 0) return group;
        if (group[0] == 'd' && int.TryParse(group.AsSpan(1), out int definition) && Options.DefinitionNames?.TryGetValue(definition, out var written) == true)
            return written;
        string first = Package.Entries[members[0]].Name;
        if (members.All(m => Package.Entries[m].Name == first)) return first;
        int common = first.Length;
        foreach (int member in members)
        {
            string name = Package.Entries[member].Name; int i = 0;
            while (i < common && i < name.Length && name[i] == first[i]) i++;
            common = i;
        }
        return first[..common] + "*";
    }
    /// <summary>Every binding with this exact name, in entry order (names are not unique: the first wins).</summary>
    public IReadOnlyList<int> Named(string name) => names.TryGetValue(name, out var list) ? list : [];
    /// <summary>The binding a name resolves to, as the runtime finds it: the first entry of the name.</summary>
    public int? First(string name) => lookup.Find(name)?.Index;
    /// <summary>Whether the mission's NEW_GAME_START list starts this binding.</summary>
    public bool StartsNewGame(int entry) => newGameStart.Contains(entry);

    /// <summary>Every call or by-name event of the map that reaches <paramref name="entry"/>, in entry, sequence and event order.</summary>
    public AnimationStructurePage<AnimationGraphCaller> Callers(int entry, int offset = 0, int limit = AnimationGraph.MaximumPageSize)
    {
        AnimationGraph.CheckPage(offset, limit); Require(entry);
        var list = callers.GetValueOrDefault(entry) ?? [];
        return AnimationGraph.Page(list.Skip(offset).Take(limit).ToList(), list.Count, offset);
    }
    internal IReadOnlyList<AnimationGraphCaller> AllCallers(int entry) => callers.GetValueOrDefault(entry) ?? (IReadOnlyList<AnimationGraphCaller>)[];
    /// <summary>The bindings whose activation prerequisites name <paramref name="entry"/> (the first of its name).</summary>
    public IReadOnlyList<int> NeededBy(int entry) => new ReadOnlyCollection<int>(neededBy.GetValueOrDefault(entry) ?? []);

    /// <summary>
    /// What an event relates its binding to: a call (19, 24 with its wait and local flags) or a stop, reset or invalidate by
    /// name (25–27). <see cref="Relation.Target"/> is null when the name resolves to nothing.
    /// </summary>
    internal Relation? Relate(AnimationEvent ev, CancellationToken token = default)
    {
        switch (ev.Type)
        {
            case 19 or 24 when ev.Bytes.Length >= 80:
                {
                    var target = lookup.ResolveChild(ev, token);
                    bool wait = ev.Type == 24 && (ev.I16(46) & 16) != 0;
                    return new(wait ? AnimationGraphRelation.CallWait : AnimationGraphRelation.Call, target?.Index, AnimationAudioDependencies.ChildName(ev), wait);
                }
            case 25 or 26 or 27 when ev.Bytes.Length >= 48:
                {
                    string name = ev.Text(12);
                    var target = lookup.Find(name, token);
                    var relation = ev.Type switch { 25 => AnimationGraphRelation.StopAnimation, 26 => AnimationGraphRelation.ResetAnimation, _ => AnimationGraphRelation.InvalidateAnimation };
                    return new(relation, target is { Index: > 0 } ? target.Index : null, name, false);
                }
            default: return null;
        }
    }
    internal readonly record struct Relation(AnimationGraphRelation Kind, int? Target, string Name, bool Wait);

    private int Require(int entry) => entry >= 0 && entry < Package.Entries.Count ? entry
        : throw new InvalidDataException("The animation binding is not part of this package.");
}
