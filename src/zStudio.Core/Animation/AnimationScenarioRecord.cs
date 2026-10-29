using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace zStudio.Core.Animation;

/// <summary>
/// The source addresses of one compilation, by which a scenario outlives it: a binding is its definition (file, ordinal in
/// the file, which listing of the file) and its root node; an event is its definition and its source record. Built on
/// demand from the nonserialized provenance; animation names are never used.
/// </summary>
internal sealed class AnimationScenarioAddresses
{
    internal readonly record struct Definition(string File, int Ordinal, int Occurrence);
    internal readonly record struct Binding(Definition Definition, string Root);
    private readonly Dictionary<int, Binding> bindingOf = [];
    private readonly Dictionary<Binding, int> entryOf = [];
    private readonly Dictionary<Guid, (int Entry, string Record, string Kind)> eventOf = [];
    private readonly Dictionary<(int Entry, string Record), AnimationEvent> eventAt = [];
    // Words only (marked items' labels): a sequence's name by its definition and source record, an animation's by its binding.
    private readonly Dictionary<(Definition Definition, string Record), string> sequenceNames = [];
    private readonly Dictionary<Binding, string> animationNames = [];
    public bool Available { get; }
    public AnimationEntryLookup Lookup { get; }
    public AnimationPackage Package { get; }

    public AnimationScenarioAddresses(AnimationScenarioSource source)
    {
        Package = source.Package; Lookup = new(source.Package);
        var provenance = source.Provenance;
        if (provenance == null) return;
        Available = true;
        Dictionary<(string File, int Ordinal), SortedSet<int>> listings = [];
        foreach (var entry in Package.Entries)
            if (provenance.FindEntry(entry.Index) is { } at)
            {
                if (!listings.TryGetValue((at.File, at.DefinitionOrdinal), out var set)) listings.Add((at.File, at.DefinitionOrdinal), set = []);
                set.Add(at.Definition);
            }
        HashSet<Binding> ambiguous = [];
        foreach (var entry in Package.Entries)
        {
            if (provenance.FindEntry(entry.Index) is not { } at) continue;
            int occurrence = listings[(at.File, at.DefinitionOrdinal)].Count(d => d < at.Definition);
            Binding binding = new(new(at.File, at.DefinitionOrdinal, occurrence), entry.RootName);
            bindingOf[entry.Index] = binding;
            if (!entryOf.TryAdd(binding, entry.Index)) ambiguous.Add(binding);
            animationNames.TryAdd(binding, entry.Name);
            foreach (var sequence in entry.AllSequences)
            {
                if (provenance.FindSequence(sequence.Id) is { RecordId: string sequenceRecord }) sequenceNames.TryAdd((binding.Definition, sequenceRecord), sequence.Name);
                foreach (var ev in sequence.Events)
                    if (provenance.FindEvent(ev.Id) is { RecordId: string record } source2)
                    {
                        eventOf[ev.Id] = (entry.Index, record, source2.Kind);
                        eventAt[(entry.Index, record)] = ev;
                    }
            }
        }
        foreach (var binding in ambiguous) entryOf.Remove(binding);
    }

    /// <summary>The name of the sequence that holds the event at <paramref name="eventRecord"/> of <paramref name="definition"/>, when this compilation has it.</summary>
    public string? SequenceName(Definition definition, string eventRecord)
    {
        int at = eventRecord.IndexOf("/event/", StringComparison.Ordinal);
        return at > 0 && sequenceNames.TryGetValue((definition, eventRecord[..at]), out string? name) ? name : null;
    }
    /// <summary>The animation's name at <paramref name="binding"/>, when this compilation binds it.</summary>
    public string? AnimationName(Binding binding) => animationNames.TryGetValue(binding, out string? name) ? name : null;

    public Binding? BindingOf(int entry) => bindingOf.TryGetValue(entry, out var binding) ? binding : null;
    public int? EntryOf(Binding binding) => entryOf.TryGetValue(binding, out int entry) ? entry : null;
    public (int Entry, string Record, string Kind)? EventOf(Guid ev) => eventOf.TryGetValue(ev, out var at) ? at : null;
    public AnimationEvent? EventAt(int entry, string record, string kind) =>
        eventAt.TryGetValue((entry, record), out var ev) && eventOf.TryGetValue(ev.Id, out var at) && at.Kind == kind ? ev : null;
}

/// <summary>What of a scenario no longer matches its compilation (kept and marked, design §12): counts per kind.</summary>
public sealed record AnimationScenarioMarks(int Starts, int Paths, int Conditions)
{
    public static AnimationScenarioMarks None { get; } = new(0, 0, 0);
    public int Count => Starts + Paths + Conditions;
    /// <summary>The count of one kind: starts, paths or conditions.</summary>
    public int Of(string kind) => kind switch { "starts" => Starts, "paths" => Paths, "conditions" => Conditions, _ => throw new ArgumentException("The kinds are starts, paths and conditions.", nameof(kind)) };
}

/// <summary>One item kept and marked because it no longer matches (Scenario tab, MCP animation_scenario read): its kind and words.</summary>
public sealed record AnimationScenarioMark(string Kind, string Text);

/// <summary>
/// A scenario in its durable form (docs/agents/sources.md, the project's <c>.zstudio/settings.json</c>): bindings and events
/// by source address, call paths as a tree of (parent, call event, target binding), inputs and rules. It is how a scenario
/// is saved per map and how it moves to a new compilation. What no longer matches is kept and marked (design §12), never
/// dropped silently: a start, an included call path or a condition switch that a source edit took away stays in the record,
/// is never run, and comes back when an undo, a redo or a later edit makes it match again, until it is removed.
/// </summary>
public sealed class AnimationScenarioRecord
{
    public const int Format = 1;
    /// <summary>The largest saved scenario of one map; larger ones keep fewer included call paths, and say how many they dropped.</summary>
    public const int MaximumBytes = 64 * 1024;
    private const int MaximumText = 1024;

    private readonly List<AnimationScenarioAddresses.Definition> definitions = [];
    private readonly List<(int Definition, string Root)> bindings = [];
    private readonly List<(int Definition, string Record, string Kind)> events = [];
    /// <summary>
    /// The starts and, parallel, whether each is marked. In a scenario's record an unmarked start is one of the scenario's; in
    /// the marked part (<see cref="Resolve(AnimationScenarioSource, List{AnimationScenarioNotice}, out AnimationScenarioRecord?)"/>'s
    /// <c>marked</c>) it is an anchor: a start of the scenario that marked call paths and switches hang off.
    /// </summary>
    private readonly List<(Guid Id, int Binding, double Seconds)> starts = [];
    private readonly List<bool> startMarked = [];
    private readonly List<(int Parent, int Event, int Target)> paths = [];
    private readonly List<int> includes = [], markedIncludes = [];
    private readonly List<(int Path, int Event, bool Value)> conditions = [], markedConditions = [];
    /// <summary>
    /// The start that stands in for the marked primary (−1: none): the opened definition's binding that became the primary after
    /// a recompile took the primary's binding away (<see cref="AnimationScenarioSession.StandIn"/>). It shares its identity with
    /// the marked start it stands in for (<see cref="StoodFor"/>): the primary's, and when another definition's binding becomes
    /// the primary, the ordinary start's it becomes (it still stands in, and is the primary stand-in again when its binding is
    /// opened again). When the marked start matches again it takes the stand-in's place and the stand-in goes, its call paths
    /// and switches moving to it.
    /// </summary>
    private int standIn = -1;
    /// <summary>The kinds of marked items the user chose to keep (Keep), saved so the question stays answered.</summary>
    private readonly SortedSet<string> kept = new(StringComparer.Ordinal);
    private AnimationScenario inputs = AnimationScenario.Default;
    private int trimmed;
    private static readonly string[] Kinds = ["starts", "paths", "conditions"];

    private AnimationScenarioRecord() { }

    /// <summary>What this record keeps marked: its marked starts, call paths and condition switches.</summary>
    public AnimationScenarioMarks Marks => new(startMarked.Count(m => m), markedIncludes.Count, markedConditions.Count);
    /// <summary>The kinds of marked items kept (Keep) when this record was saved or resolved, of the kinds it still marks.</summary>
    public IReadOnlyCollection<string> Kept => kept.Where(k => Marks.Of(k) > 0).ToArray();
    /// <summary>The time of the marked primary start, when one is marked.</summary>
    internal double? MarkedPrimarySeconds => MarkedPrimary() is int at ? starts[at].Seconds : null;
    /// <summary>The definition of the marked primary start's binding, when one is marked.</summary>
    internal AnimationScenarioAddresses.Definition? MarkedPrimaryDefinition => MarkedPrimary() is int at ? definitions[bindings[starts[at].Binding].Definition] : null;
    /// <summary>The scenario's primary stands in for the marked primary.</summary>
    internal bool PrimaryStandsIn => standIn >= 0 && starts[standIn].Id == Guid.Empty;
    private int? MarkedPrimary() => MarkedWith(Guid.Empty);
    /// <summary>The marked start the stand-in stands in for: the one that shares its identity.</summary>
    private int? StoodFor() => standIn >= 0 ? MarkedWith(starts[standIn].Id) : null;
    private int? MarkedWith(Guid id)
    {
        for (int i = 0; i < starts.Count; i++) if (startMarked[i] && starts[i].Id == id) return i;
        return null;
    }
    /// <summary>A copy sharing nothing mutable: the addresses, path tree, starts and inputs; the marked lists as <paramref name="copyMarked"/> says.</summary>
    private AnimationScenarioRecord Copy(bool copyMarked)
    {
        AnimationScenarioRecord part = new() { inputs = inputs, standIn = standIn, trimmed = trimmed };
        part.definitions.AddRange(definitions); part.bindings.AddRange(bindings); part.events.AddRange(events); part.paths.AddRange(paths);
        part.starts.AddRange(starts); part.startMarked.AddRange(startMarked); part.kept.UnionWith(kept);
        if (copyMarked) { part.markedIncludes.AddRange(markedIncludes); part.markedConditions.AddRange(markedConditions); }
        return part;
    }

    /// <summary>The durable form of <paramref name="scenario"/>, or null when <paramref name="source"/> has no provenance.</summary>
    public static AnimationScenarioRecord? From(AnimationScenario scenario, AnimationScenarioSource source, List<AnimationScenarioNotice>? notices = null) =>
        From(scenario, source, notices, null);

    /// <summary>
    /// The durable form of <paramref name="scenario"/> with the items <paramref name="marked"/> keeps marked (the marked part of an
    /// earlier resolve), or null when <paramref name="source"/> has no provenance. A marked item whose start the scenario no longer
    /// has (removed, or replaced by another binding) goes with that start.
    /// </summary>
    public static AnimationScenarioRecord? From(AnimationScenario scenario, AnimationScenarioSource source, List<AnimationScenarioNotice>? notices, AnimationScenarioRecord? marked) =>
        From(scenario, source, notices, marked, default);

    /// <summary>
    /// What a scenario change does to the marked items besides moving them onto it: its primary was made to stand in for the
    /// marked primary (<see cref="AnimationScenarioSession.StandIn"/>), it excluded every call path (Exclude all), or it excluded
    /// <paramref name="Excluded"/> with what lies below them (a start's own instance path: every call path of that start); an
    /// exclusion takes the marked included paths below it too, under a stand-in the marked primary's same ones as well.
    /// </summary>
    internal readonly record struct MarkedChange(bool StandIn = false, bool ExcludeAll = false, IReadOnlyCollection<string>? Excluded = null);

    internal static AnimationScenarioRecord? From(AnimationScenario scenario, AnimationScenarioSource source, List<AnimationScenarioNotice>? notices, AnimationScenarioRecord? marked, MarkedChange change)
    {
        ArgumentNullException.ThrowIfNull(scenario); ArgumentNullException.ThrowIfNull(source);
        var addresses = source.Addresses;
        if (!addresses.Available) return null;
        AnimationScenarioRecord record = new() { inputs = Inputs(scenario) };
        Dictionary<AnimationScenarioAddresses.Definition, int> definitionIndex = [];
        Dictionary<(int, string), int> bindingIndex = [];
        Dictionary<(int, string, string), int> eventIndex = [];
        Dictionary<(int, int, int), int> pathIndex = [];
        Dictionary<string, int> roots = new(StringComparer.Ordinal);
        int lost = 0;
        int Definition(AnimationScenarioAddresses.Definition definition)
        {
            if (definitionIndex.TryGetValue(definition, out int at)) return at;
            record.definitions.Add(definition); return definitionIndex[definition] = record.definitions.Count - 1;
        }
        int BindingAt(AnimationScenarioAddresses.Definition definition, string root)
        {
            var key = (Definition(definition), root);
            if (bindingIndex.TryGetValue(key, out int at)) return at;
            record.bindings.Add(key); return bindingIndex[key] = record.bindings.Count - 1;
        }
        int? Binding(int entry) => addresses.BindingOf(entry) is { } binding ? BindingAt(binding.Definition, binding.Root) : null;
        int EventAt(AnimationScenarioAddresses.Definition definition, string recordId, string kind)
        {
            var key = (Definition(definition), recordId, kind);
            if (eventIndex.TryGetValue(key, out int index)) return index;
            record.events.Add(key); return eventIndex[key] = record.events.Count - 1;
        }
        int? Event(int entry, Guid id)
        {
            if (addresses.EventOf(id) is not { } at || at.Entry != entry || addresses.BindingOf(entry) is not { } binding) return null;
            return EventAt(binding.Definition, at.Record, at.Kind);
        }
        int PathAt(int parent, int eventAt, int targetAt)
        {
            var key = (parent, eventAt, targetAt);
            if (!pathIndex.TryGetValue(key, out int node)) { record.paths.Add(key); pathIndex[key] = node = record.paths.Count - 1; }
            return node;
        }
        foreach (var start in scenario.Starts)
        {
            if (Binding(start.Entry) is not int binding) { lost++; continue; }
            record.starts.Add((start.Id, binding, start.Seconds)); record.startMarked.Add(false);
            roots[start.Path] = -1 - (record.starts.Count - 1);
        }
        // A path's reference: a start (-1 - index) or a node of the path tree; null when it cannot be addressed.
        int? Path(string path)
        {
            string root = path.StartsWith("/@", StringComparison.Ordinal) && path.Length >= 34 ? path[..34] : "";
            if (!roots.TryGetValue(root, out int parent)) return null;
            int caller = scenario.Starts.First(s => s.Path == root).Entry;
            foreach (string segment in path[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                // An unresolved call has no instance, so nothing is below it.
                if (caller < 0) return null;
                string[] parts = segment.Split(':');
                if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int from) || from != caller
                    || !Guid.TryParseExact(parts[1], "N", out Guid ev) || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int target)) return null;
                if (Event(caller, ev) is not int eventAt) return null;
                int targetAt = -1;
                if (target >= 0) { if (Binding(target) is not int bound) return null; targetAt = bound; }
                parent = PathAt(parent, eventAt, targetAt); caller = target;
            }
            return parent;
        }
        foreach (string path in scenario.IncludedCalls.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal))
            if (Path(path) is int node && node >= 0) record.includes.Add(node); else lost++;
        foreach (var condition in scenario.Conditions)
            if (Path(condition.InstancePath) is int node && Event(condition.Entry, condition.Event) is int ev) record.conditions.Add((node, ev, condition.Value)); else lost++;
        // A path's node when the record already has it (null when it does not): what an exclusion names. With anchor, the same
        // calls below that start reference instead (the marked primary a stand-in stands in for).
        int? Find(string path, int? anchor = null)
        {
            string root = path.StartsWith("/@", StringComparison.Ordinal) && path.Length >= 34 ? path[..34] : "";
            if (!roots.TryGetValue(root, out int parent)) return null;
            if (anchor is int other) parent = other;
            int caller = scenario.Starts.First(s => s.Path == root).Entry;
            foreach (string segment in path[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = segment.Split(':');
                if (caller < 0 || parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out Guid ev)
                    || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int target)) return null;
                if (addresses.EventOf(ev) is not { } at || at.Entry != caller || addresses.BindingOf(caller) is not { } binding
                    || !definitionIndex.TryGetValue(binding.Definition, out int definition) || !eventIndex.TryGetValue((definition, at.Record, at.Kind), out int eventAt)) return null;
                int targetAt = -1;
                if (target >= 0 && (addresses.BindingOf(target) is not { } bound || !definitionIndex.TryGetValue(bound.Definition, out int boundDefinition)
                    || !bindingIndex.TryGetValue((boundDefinition, bound.Root), out targetAt))) return null;
                if (!pathIndex.TryGetValue((parent, eventAt, targetAt), out parent)) return null;
                caller = target;
            }
            return parent;
        }
        if (marked != null)
        {
            // The marked items, by address: a marked start is kept; an anchor finds its start in the scenario by identity and
            // binding, else the one start on that binding (a primary that became an ordinary start), else its items go with it.
            int Bind(AnimationScenarioRecord from, int binding) => BindingAt(from.definitions[from.bindings[binding].Definition], from.bindings[binding].Root);
            var startOf = new int?[marked.starts.Count];
            int scenarioStarts = record.starts.Count;
            for (int i = 0; i < marked.starts.Count; i++)
            {
                if (marked.startMarked[i]) continue;
                var (id, binding, _) = marked.starts[i];
                int bound = Bind(marked, binding);
                int same = record.starts.FindIndex(s => s.Id == id && s.Binding == bound);
                var onBinding = Enumerable.Range(0, scenarioStarts).Where(s => record.starts[s].Binding == bound).ToArray();
                startOf[i] = same >= 0 ? same : onBinding.Length == 1 ? onBinding[0] : null;
            }
            // The primary made in the marked primary's stead stands in for it; a stand-in the scenario still has goes on standing
            // in, also as an ordinary start once another definition's binding became the primary.
            int primary = record.starts.FindIndex(s => s.Id == Guid.Empty);
            int? standsFor = null;
            if (change.StandIn && primary >= 0 && marked.MarkedPrimary() is int markedPrimary) { record.standIn = primary; standsFor = markedPrimary; }
            else if (marked.StoodFor() is int stood && startOf[marked.standIn] is int still) { record.standIn = still; standsFor = stood; }
            // Its role changed (another definition opened, or its binding opened again): the start it stands in for follows it.
            bool roleChanged = standsFor != null && !change.StandIn && (marked.starts[marked.standIn].Id == Guid.Empty) != (record.starts[record.standIn].Id == Guid.Empty);
            record.kept.UnionWith(marked.kept);
            List<(int Slot, int Order, int Start)> placed = [];
            for (int i = 0; i < marked.starts.Count; i++)
            {
                if (!marked.startMarked[i] || record.starts.Count >= AnimationSchedule.MaximumStarts * 2) continue;
                var (id, binding, seconds) = marked.starts[i];
                // The start a stand-in stands in for shares its identity; a marked primary whose place another binding took (not
                // one standing in for it) comes back as an ordinary start.
                if (i == standsFor) id = record.starts[record.standIn].Id;
                else if (record.starts.Any(s => s.Id == id)) id = Guid.NewGuid();
                record.starts.Add((id, Bind(marked, binding), seconds)); record.startMarked.Add(true);
                startOf[i] = record.starts.Count - 1;
                // It keeps its place among the starts: after the nearest earlier start of its part that the scenario still has, and
                // an ordinary start never before the primary; the start a stand-in stands in for, after a role change, right before it.
                int slot = 0;
                for (int a = i - 1; a >= 0; a--) if (!marked.startMarked[a] && startOf[a] is int s) { slot = s + 1; break; }
                if (slot == 0 && id != Guid.Empty && primary == 0) slot = 1;
                if (i == standsFor && roleChanged) slot = record.standIn;
                placed.Add((slot, i, record.starts.Count - 1));
            }
            var nodeOf = new int?[marked.paths.Count];
            bool[] needed = new bool[marked.paths.Count];
            void Need(int node) { for (int at = node; at >= 0 && !needed[at]; at = marked.paths[at].Parent) needed[at] = true; }
            foreach (int node in marked.markedIncludes) Need(node);
            foreach (var (node, _, _) in marked.markedConditions) if (node >= 0) Need(node);
            for (int i = 0; i < marked.paths.Count; i++)
            {
                if (!needed[i]) continue;
                var (parent, eventAt, target) = marked.paths[i];
                int? from = parent < 0 ? startOf[-1 - parent] is int s ? -1 - s : null : nodeOf[parent];
                if (from is not int mappedParent) continue;
                var (definition, recordId, kind) = marked.events[eventAt];
                nodeOf[i] = PathAt(mappedParent, EventAt(marked.definitions[definition], recordId, kind), target < 0 ? -1 : Bind(marked, target));
            }
            int dropped = 0;
            // An exclusion takes the marked included paths at or below it (Exclude all, every one; a start's own path, all of that
            // start's), as it takes the paths below it. Under a stand-in the marked primary's paths hang off the marked primary,
            // a binding of the same definition: an exclusion below the stand-in takes the marked primary's same calls too.
            int? standingFor = standsFor is int stoodFor && startOf[stoodFor] is int placedAt ? placedAt : null;
            string standInRoot = standingFor == null || record.starts[record.standIn].Id == Guid.Empty ? "" : "/@" + record.starts[record.standIn].Id.ToString("N");
            HashSet<int> excluded = [];
            foreach (string path in change.Excluded ?? [])
            {
                if (Find(path) is int node) excluded.Add(node);
                bool belowStandIn = standInRoot.Length == 0 ? !path.StartsWith("/@", StringComparison.Ordinal)
                    : path.StartsWith(standInRoot, StringComparison.Ordinal) && (path.Length == standInRoot.Length || path[standInRoot.Length] == '/');
                if (standingFor is int anchor && belowStandIn && Find(path, -1 - anchor) is int same) excluded.Add(same);
            }
            bool Excluded(int node)
            {
                int at = node;
                for (; at >= 0; at = record.paths[at].Parent) if (excluded.Contains(at)) return true;
                return excluded.Contains(at);
            }
            foreach (int node in marked.markedIncludes)
                if (nodeOf[node] is not int mapped) dropped++;
                else if (change.ExcludeAll || Excluded(mapped)) continue;
                else if (!record.includes.Contains(mapped) && !record.markedIncludes.Contains(mapped)) record.markedIncludes.Add(mapped);
            foreach (var (node, eventAt, value) in marked.markedConditions)
            {
                int? path = node < 0 ? startOf[-1 - node] is int s ? -1 - s : null : nodeOf[node];
                if (path is not int mapped) { dropped++; continue; }
                var (definition, recordId, kind) = marked.events[eventAt];
                int ev = EventAt(marked.definitions[definition], recordId, kind);
                if (!record.conditions.Any(c => c.Path == mapped && c.Event == ev) && !record.markedConditions.Any(c => c.Path == mapped && c.Event == ev))
                    record.markedConditions.Add((mapped, ev, value));
            }
            if (dropped > 0) notices?.Add(new("marked", $"{dropped} marked scenario item{(dropped == 1 ? " went with its start" : "s went with their starts")}, which the scenario no longer has."));
            if (placed.Count > 0) record.Reorder(scenarioStarts, placed);
        }
        if (lost > 0) notices?.Add(new("source", $"{lost} scenario item{(lost == 1 ? " has" : "s have")} no source address and cannot be kept."));
        return record;
    }

    /// <summary>
    /// Puts the marked starts, appended after the scenario's first <paramref name="scenarioStarts"/>, back in their places: each
    /// <paramref name="placed"/> start goes into its slot (0 before every start, s + 1 after start s), in its part's order.
    /// </summary>
    private void Reorder(int scenarioStarts, List<(int Slot, int Order, int Start)> placed)
    {
        var bySlot = placed.ToLookup(p => p.Slot);
        List<int> order = [];
        for (int slot = 0; slot <= scenarioStarts; slot++)
        {
            if (slot > 0) order.Add(slot - 1);
            order.AddRange(bySlot[slot].OrderBy(p => p.Order).Select(p => p.Start));
        }
        var newIndex = new int[starts.Count];
        for (int i = 0; i < order.Count; i++) newIndex[order[i]] = i;
        var oldStarts = starts.ToArray(); var oldMarked = startMarked.ToArray();
        for (int i = 0; i < order.Count; i++) { starts[i] = oldStarts[order[i]]; startMarked[i] = oldMarked[order[i]]; }
        int Map(int reference) => reference < 0 ? -1 - newIndex[-1 - reference] : reference;
        for (int i = 0; i < paths.Count; i++) paths[i] = paths[i] with { Parent = Map(paths[i].Parent) };
        for (int i = 0; i < conditions.Count; i++) conditions[i] = conditions[i] with { Path = Map(conditions[i].Path) };
        for (int i = 0; i < markedConditions.Count; i++) markedConditions[i] = markedConditions[i] with { Path = Map(markedConditions[i].Path) };
        if (standIn >= 0) standIn = newIndex[standIn];
    }

    /// <summary>The scenario in <paramref name="source"/>'s compilation; what no longer matches is listed in <paramref name="notices"/> and left out.</summary>
    public AnimationScenario Resolve(AnimationScenarioSource source, List<AnimationScenarioNotice> notices) => Resolve(source, notices, out _);

    /// <summary>
    /// The scenario in <paramref name="source"/>'s compilation, with every item that matches it (marked ones that match again
    /// among them). What does not match is kept in <paramref name="marked"/> (null when everything matches): never run, and
    /// resolved again with the next compilation. Items that stopped matching now are listed in <paramref name="notices"/>.
    /// </summary>
    public AnimationScenario Resolve(AnimationScenarioSource source, List<AnimationScenarioNotice> notices, out AnimationScenarioRecord? marked) => Resolve(source, notices, out marked, out _);

    /// <summary>As <see cref="Resolve(AnimationScenarioSource, List{AnimationScenarioNotice}, out AnimationScenarioRecord?)"/>, with <paramref name="lost"/>: per kind, the items that stopped matching now.</summary>
    internal AnimationScenario Resolve(AnimationScenarioSource source, List<AnimationScenarioNotice> notices, out AnimationScenarioRecord? marked, out AnimationScenarioMarks lost)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(notices);
        marked = null; lost = AnimationScenarioMarks.None;
        var addresses = source.Addresses;
        if (!addresses.Available)
        {
            if (starts.Count + includes.Count + conditions.Count > 0) notices.Add(new("source", "The animations have no source addresses, so the scenario's starts, call paths and switches could not be kept."));
            return inputs;
        }
        int?[] entries = bindings.Select(b => addresses.EntryOf(new(definitions[b.Definition], b.Root))).ToArray();
        List<AnimationScheduledStart> resolvedStarts = [];
        var rootOf = new (string Path, int Entry)?[starts.Count];
        bool[] startFailed = new bool[starts.Count];
        int lostStarts = 0;
        // A stand-in goes when the marked start it stands in for matches again (it takes its place back, with the stand-in's
        // identity) or when it no longer matches itself (it only stood in): never marked, its call paths and switches move to it.
        int? stoodFor = StoodFor();
        bool stoodForBack = stoodFor is int back && entries[starts[back].Binding] is > 0;
        bool standInGone = stoodFor != null && (stoodForBack || entries[starts[standIn].Binding] is not > 0);
        for (int i = 0; i < starts.Count; i++)
        {
            if (standInGone && i == standIn) continue;
            var (id, binding, seconds) = starts[i];
            // A start beyond the schedule's limit (marked ones restored while the scenario has 32) stays marked.
            if (entries[binding] is not int entry || entry <= 0 || resolvedStarts.Count >= AnimationSchedule.MaximumStarts || resolvedStarts.Any(s => s.Id == id))
            {
                if (i == standIn && stoodFor != null) { standInGone = true; continue; }
                startFailed[i] = true;
                if (!startMarked[i])
                {
                    lostStarts++;
                    var (file, root) = (definitions[bindings[binding].Definition].File, bindings[binding].Root);
                    notices.Add(new("start", $"{(id == Guid.Empty ? "The primary start" : "A start")} at {AnimationSeconds.Format(seconds)} (bound at {AnimationPhrases.Safe(root)} by {AnimationPhrases.Safe(Path.GetFileName(file))}) no longer matches a binding; it is kept, marked, with its call paths and switches, until it matches again or you remove it."));
                }
                continue;
            }
            var start = new AnimationScheduledStart(id, entry, seconds);
            resolvedStarts.Add(start); rootOf[i] = (start.Path, entry);
        }
        if (standInGone) rootOf[standIn] = rootOf[stoodFor!.Value];
        var resolved = new (string Path, int Entry)?[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            var (parent, eventAt, target) = paths[i];
            (string Path, int Entry)? from = parent < 0 ? -1 - parent < rootOf.Length ? rootOf[-1 - parent] : null : parent < i ? resolved[parent] : null;
            if (from is not { Entry: >= 0 } caller || eventAt < 0 || eventAt >= events.Count) continue;
            var (definition, record, kind) = events[eventAt];
            if (addresses.BindingOf(caller.Entry) is not { } binding || binding.Definition != definitions[definition]) continue;
            if (addresses.EventAt(caller.Entry, record, kind) is not { Type: 19 or 24 } ev) continue;
            int targetEntry = target < 0 ? -1 : target < entries.Length && entries[target] is int t ? t : -2;
            if (targetEntry == -2 || (addresses.Lookup.ResolveChild(ev)?.Index ?? -1) != targetEntry) continue;
            try { resolved[i] = (AnimationExecutionPolicy.ChildPath(caller.Path, caller.Entry, ev.Id, targetEntry), targetEntry); }
            catch (InvalidDataException) { }
        }
        List<(string Path, int Node, bool Marked)> included = [];
        List<int> failedIncludes = []; int lostPaths = 0;
        foreach (var (node, wasMarked) in includes.Select(n => (n, false)).Concat(markedIncludes.Select(n => (n, true))))
            if (node >= 0 && node < resolved.Length && resolved[node] is { } path) included.Add((path.Path, node, wasMarked));
            else { failedIncludes.Add(node); if (!wasMarked) lostPaths++; }
        // A path whose ancestors are not included stays marked with them: the included set stays prefix-closed.
        HashSet<string> keptPaths = new(included.Select(p => p.Path), StringComparer.Ordinal);
        foreach (var orphan in included.Where(p => !Ancestors(p.Path).All(a => a.Length == 0 || a.StartsWith("/@", StringComparison.Ordinal) && a.Length == 34 || keptPaths.Contains(a))).ToArray())
        {
            included.Remove(orphan); failedIncludes.Add(orphan.Node);
            if (!orphan.Marked) lostPaths++;
        }
        List<AnimationConditionOverride> forced = [];
        List<(int Path, int Event, bool Value)> failedConditions = []; int lostConditions = 0;
        foreach (var (item, wasMarked) in conditions.Select(c => (c, false)).Concat(markedConditions.Select(c => (c, true))))
        {
            var (node, eventAt, value) = item;
            (string Path, int Entry)? instance = node < 0 ? -1 - node < rootOf.Length ? rootOf[-1 - node] : null : node < resolved.Length ? resolved[node] : null;
            if (instance is { Entry: > 0 } at && eventAt >= 0 && eventAt < events.Count && addresses.BindingOf(at.Entry) is { } binding
                && binding.Definition == definitions[events[eventAt].Definition]
                && addresses.EventAt(at.Entry, events[eventAt].Record, events[eventAt].Kind) is { Type: 31 or 33 } ev)
            {
                // The same switch twice (a stand-in's and its marked primary's) is one; a different setting of it stays marked.
                var prior = forced.FirstOrDefault(f => f.InstancePath == at.Path && f.Entry == at.Entry && f.Event == ev.Id);
                if (prior == null) { forced.Add(new(at.Path, at.Entry, ev.Id, value)); continue; }
                if (prior.Value == value) continue;
            }
            failedConditions.Add(item); if (!wasMarked) lostConditions++;
        }
        if (lostPaths > 0) notices.Add(new("include", $"{lostPaths} included call path{(lostPaths == 1 ? " no longer matches" : "s no longer match")} after the change; {(lostPaths == 1 ? "it is" : "they are")} kept, marked, until {(lostPaths == 1 ? "it matches" : "they match")} again or you remove {(lostPaths == 1 ? "it" : "them")}."));
        if (lostConditions > 0) notices.Add(new("condition", $"{lostConditions} condition switch{(lostConditions == 1 ? " no longer matches" : "es no longer match")} after the change; {(lostConditions == 1 ? "it is" : "they are")} kept, marked, and the run uses the game's rule there until {(lostConditions == 1 ? "it matches" : "they match")} again or you remove {(lostConditions == 1 ? "it" : "them")}."));
        if (trimmed > 0) notices.Add(new("trimmed", $"{trimmed} included call path{(trimmed == 1 ? " was" : "s were")} not saved: a map's saved scenario holds at most {MaximumBytes / 1024} KB."));
        if (startFailed.Any(f => f) || failedIncludes.Count > 0 || failedConditions.Count > 0)
        {
            // The marked part shares this record's addresses and path tree; its unmarked starts are anchors (resolved starts).
            AnimationScenarioRecord part = new() { inputs = inputs };
            part.definitions.AddRange(definitions); part.bindings.AddRange(bindings); part.events.AddRange(events); part.paths.AddRange(paths);
            part.starts.AddRange(starts); part.startMarked.AddRange(startFailed); part.kept.UnionWith(kept);
            part.markedIncludes.AddRange(failedIncludes.Distinct()); part.markedConditions.AddRange(failedConditions);
            if (stoodFor is int primary)
            {
                // A stand-in that went leaves an inert anchor: what hung off it hangs off the marked primary (or its start).
                if (!standInGone) part.standIn = standIn;
                else
                {
                    int from = -1 - standIn, to = -1 - primary;
                    for (int i = 0; i < part.paths.Count; i++) if (part.paths[i].Parent == from) part.paths[i] = part.paths[i] with { Parent = to };
                    for (int i = 0; i < part.markedConditions.Count; i++) if (part.markedConditions[i].Path == from) part.markedConditions[i] = part.markedConditions[i] with { Path = to };
                }
            }
            marked = part;
        }
        lost = new(lostStarts, lostPaths, lostConditions);
        // A start without the primary keeps no lineage of its own to the opened binding: drop nothing more, the GUI opens one.
        return inputs with { Starts = resolvedStarts, IncludedCalls = included.Select(p => p.Path).ToArray(), Conditions = forced };
    }

    /// <summary>The marked items alone (the scenario's own starts become their anchors), or null when there are none.</summary>
    public AnimationScenarioRecord? MarkedPart()
    {
        if (Marks.Count == 0) return null;
        var part = Copy(copyMarked: true); part.trimmed = 0;
        return part;
    }

    /// <summary>
    /// The items this record keeps marked, in words (at most <paramref name="limit"/>): each names its start or the call it sits
    /// in, and the sequence, with names from <paramref name="names"/> (the current compilation) where it still has them. Two
    /// items never read the same: those that would are numbered.
    /// </summary>
    public IReadOnlyList<AnimationScenarioMark> MarkedItems(AnimationScenarioSource? names = null, int limit = 64)
    {
        var addresses = names is { Provenance: not null } ? names.Addresses : null;
        List<AnimationScenarioMark> items = [];
        string File(int definition) => AnimationPhrases.Safe(Path.GetFileName(definitions[definition].File));
        string Bound(int binding)
        {
            var (definition, root) = bindings[binding];
            string at = $"{AnimationPhrases.Safe(root)} · {File(definition)}";
            return addresses?.AnimationName(new(definitions[definition], root)) is { } name ? $"{AnimationPhrases.Safe(name)} at {at}" : "the animation bound at " + at;
        }
        string Start(int start) => $"{(starts[start].Id == Guid.Empty ? "the primary start" : "the start")} ({Bound(starts[start].Binding)})";
        // The instance an item sits in: a start, or the call path's target.
        string Instance(int path) => path < 0 ? Start(-1 - path) : paths[path].Target < 0 ? "a call to a missing animation" : Bound(paths[path].Target);
        string Sequence(int eventAt)
        {
            var (definition, record, _) = events[eventAt];
            return addresses?.SequenceName(definitions[definition], record) is { } name ? "sequence " + AnimationPhrases.Safe(name) : "a sequence of " + File(definition);
        }
        for (int i = 0; i < starts.Count && items.Count < limit; i++)
            if (startMarked[i])
            {
                string text = Start(i);
                items.Add(new("starts", $"{char.ToUpperInvariant(text[0])}{text[1..]} at {AnimationSeconds.Format(starts[i].Seconds)}"));
            }
        foreach (int node in markedIncludes.Take(Math.Max(0, limit - items.Count)))
        {
            var (parent, eventAt, target) = paths[node];
            items.Add(new("paths", target < 0 ? $"Included: a call to a missing animation by {Sequence(eventAt)} of {Instance(parent)}"
                : $"Included: {Bound(target)}, called by {Sequence(eventAt)} of {Instance(parent)}"));
        }
        foreach (var (node, eventAt, value) in markedConditions.Take(Math.Max(0, limit - items.Count)))
            items.Add(new("conditions", $"Forced {(value ? "true" : "false")}: the condition in {Sequence(eventAt)} of {Instance(node)}"));
        // Two calls of one sequence to one animation, or a start twice: numbered, so no two items read the same.
        var counts = items.GroupBy(i => i.Text, StringComparer.Ordinal).Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => (Count: g.Count(), Seen: 0), StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
            if (counts.TryGetValue(items[i].Text, out var count))
            {
                counts[items[i].Text] = count with { Seen = count.Seen + 1 };
                items[i] = items[i] with { Text = $"{items[i].Text} ({count.Seen + 1} of {count.Count})" };
            }
        return items;
    }

    /// <summary>
    /// The marked part without the items of <paramref name="kind"/> (starts, paths or conditions; a removed start takes the
    /// paths and switches that hang off it), or null when nothing stays marked.
    /// </summary>
    public AnimationScenarioRecord? WithoutMarked(string kind)
    {
        if (kind is not ("starts" or "paths" or "conditions")) throw new ArgumentException("The kinds are starts, paths and conditions.", nameof(kind));
        var part = Copy(copyMarked: false);
        part.includes.AddRange(includes); part.conditions.AddRange(conditions);
        if (kind == "starts")
        {
            int Root(int node) { while (node >= 0) node = paths[node].Parent; return -1 - node; }
            bool Gone(int reference) => startMarked[reference < 0 ? -1 - reference : Root(reference)];
            part.markedIncludes.AddRange(markedIncludes.Where(n => !Gone(n)));
            part.markedConditions.AddRange(markedConditions.Where(c => !Gone(c.Path)));
            for (int i = 0; i < part.startMarked.Count; i++) if (part.startMarked[i]) { part.startMarked[i] = false; part.starts[i] = part.starts[i] with { Id = Guid.NewGuid() }; }
            // Without its marked primary the stand-in is the primary.
            part.standIn = -1;
        }
        else
        {
            if (kind != "paths") part.markedIncludes.AddRange(markedIncludes);
            if (kind != "conditions") part.markedConditions.AddRange(markedConditions);
        }
        part.kept.Remove(kind);
        return part.Marks.Count > 0 ? part : null;
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        for (int at = path.LastIndexOf('/'); at > 0; at = path.LastIndexOf('/', at - 1)) yield return path[..at];
    }

    private static AnimationScenario Inputs(AnimationScenario scenario) => AnimationScenario.Default with
    {
        Player = scenario.Player, Seed = scenario.Seed, EffectsDetail = scenario.EffectsDetail, Phase = scenario.Phase, Height = scenario.Height,
        BeamOrigin = scenario.BeamOrigin, Prerequisites = scenario.Prerequisites, RangeGates = scenario.RangeGates, ZoneGates = scenario.ZoneGates,
    };

    /// <summary>
    /// The scenario as a map's saved setting, within <see cref="MaximumBytes"/>, with the items <paramref name="marked"/> keeps
    /// marked: when larger, the deepest included call paths are left out until it fits, and the record says how many (its restore
    /// reports them). Null without provenance.
    /// </summary>
    public static JsonObject? Save(AnimationScenario scenario, AnimationScenarioSource source, List<AnimationScenarioNotice>? notices = null, AnimationScenarioRecord? marked = null, IEnumerable<string>? kept = null)
    {
        var record = From(scenario, source, notices, marked);
        if (record == null) return null;
        // The kinds kept (Keep) are the session's: the question stays answered when the map is shown again.
        string[] keptKinds = kept == null ? [.. record.kept] : [.. kept.Where(Kinds.Contains)];
        record.kept.Clear(); record.kept.UnionWith(keptKinds);
        var json = record.ToJson();
        int dropped = 0;
        var paths = scenario.IncludedCalls.OrderByDescending(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal).ToList();
        while (Size(json) > MaximumBytes && paths.Count > 0)
        {
            int cut = Math.Max(1, paths.Count / 8);
            paths.RemoveRange(0, cut); dropped += cut;
            record = From(scenario with { IncludedCalls = paths }, source, null, marked)!;
            record.trimmed = dropped; record.kept.Clear(); record.kept.UnionWith(keptKinds); json = record.ToJson();
        }
        // Marked items are the first to go when even the starts and switches do not fit.
        if (Size(json) > MaximumBytes && marked != null)
        {
            record = From(scenario with { IncludedCalls = paths }, source)!; record.trimmed = dropped; json = record.ToJson();
            notices?.Add(new("trimmed", "The scenario's marked items were not saved: a map's saved scenario holds at most " + (MaximumBytes / 1024) + " KB."));
        }
        if (dropped > 0) notices?.Add(new("trimmed", $"{dropped} included call paths were not saved: a map's saved scenario holds at most {MaximumBytes / 1024} KB."));
        return json;
        static int Size(JsonObject value) => System.Text.Encoding.UTF8.GetByteCount(value.ToJsonString());
    }

    public JsonObject ToJson()
    {
        JsonArray Floats(Vector3 v) => [JsonValue.Create(v.X), JsonValue.Create(v.Y), JsonValue.Create(v.Z)];
        var json = new JsonObject
        {
            ["format"] = Format,
            ["definitions"] = new JsonArray([.. definitions.Select(d => (JsonNode)new JsonArray(d.File, d.Ordinal, d.Occurrence))]),
            ["bindings"] = new JsonArray([.. bindings.Select(b => (JsonNode)new JsonArray(b.Definition, b.Root))]),
            ["events"] = new JsonArray([.. events.Select(e => (JsonNode)new JsonArray(e.Definition, e.Record, e.Kind))]),
            ["starts"] = new JsonArray([.. starts.Select(s => (JsonNode)new JsonArray(s.Id == Guid.Empty ? "primary" : s.Id.ToString("D"), s.Binding, s.Seconds))]),
            ["paths"] = new JsonArray([.. paths.Select(p => (JsonNode)new JsonArray(p.Parent, p.Event, p.Target))]),
            ["include"] = new JsonArray([.. includes.Select(i => (JsonNode)JsonValue.Create(i))]),
            ["conditions"] = new JsonArray([.. conditions.Select(c => (JsonNode)new JsonArray(c.Path, c.Event, c.Value))]),
            ["player"] = inputs.Player.Kind switch { AnimationPlayerReferenceKind.Custom => Floats(inputs.Player.Position), AnimationPlayerReferenceKind.None => "none", _ => "atStart" },
            ["seed"] = inputs.Seed,
            ["effectsDetail"] = inputs.EffectsDetail switch { AnimationEffectsDetail.Low => "low", AnimationEffectsDetail.Medium => "medium", _ => "high" },
            ["phase"] = inputs.Phase == AnimationScenarioPhase.Cleanup ? "cleanup" : "runtime",
            ["height"] = inputs.Height,
            ["prerequisites"] = inputs.Prerequisites switch { AnimationPrerequisiteRule.Check => "check", AnimationPrerequisiteRule.Ignore => "ignore", _ => "ignoreStarts" },
            ["rangeGates"] = inputs.RangeGates == AnimationRangeGateRule.Ignore ? "ignore" : "game",
            ["zoneGates"] = inputs.ZoneGates == AnimationZoneGateRule.View ? "view" : "ignore",
        };
        // What no longer matched when it was saved: kept and marked, resolved again when the map is shown.
        if (startMarked.Any(m => m)) json["markedStarts"] = new JsonArray([.. Enumerable.Range(0, starts.Count).Where(i => startMarked[i]).Select(i => (JsonNode)JsonValue.Create(i))]);
        if (markedIncludes.Count > 0) json["markedInclude"] = new JsonArray([.. markedIncludes.Select(i => (JsonNode)JsonValue.Create(i))]);
        if (markedConditions.Count > 0) json["markedConditions"] = new JsonArray([.. markedConditions.Select(c => (JsonNode)new JsonArray(c.Path, c.Event, c.Value))]);
        if (standIn >= 0) json["standIn"] = standIn;
        if (Kept.Count > 0) json["keptMarks"] = new JsonArray([.. Kept.Select(k => (JsonNode)JsonValue.Create(k))]);
        if (inputs.BeamOrigin is Vector3 origin) json["beamOrigin"] = Floats(origin);
        if (trimmed > 0) json["trimmed"] = trimmed;
        return json;
    }

    /// <summary>A saved scenario, or null (with a notice) when it is not one this version writes. Damaged values fall back to defaults, each with a notice.</summary>
    public static AnimationScenarioRecord? FromJson(JsonObject json, List<AnimationScenarioNotice> notices)
    {
        ArgumentNullException.ThrowIfNull(json); ArgumentNullException.ThrowIfNull(notices);
        try { return Parse(json, notices); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException or JsonException or ArgumentException or OverflowException)
        {
            notices.Add(new("format", "The saved animation scenario could not be read and was not restored: " + JsonData.ShownText(ex.Message, 256)));
            return null;
        }
    }

    private static AnimationScenarioRecord? Parse(JsonObject json, List<AnimationScenarioNotice> notices)
    {
        if (json["format"] is not JsonValue format || !format.TryGetValue(out int version) || version != Format)
        {
            notices.Add(new("format", "The saved animation scenario is not one this version of zStudio writes; it was not restored."));
            return null;
        }
        AnimationScenarioRecord record = new();
        JsonArray Array(string key, int maximum)
        {
            if (json[key] is null) return [];
            if (json[key] is not JsonArray array || array.Count > maximum) throw new InvalidDataException($"{key} is not a list of at most {maximum:N0} items.");
            return array;
        }
        JsonArray Tuple(JsonNode? node, int count) => node is JsonArray tuple && tuple.Count == count ? tuple : throw new InvalidDataException("A saved scenario item has the wrong shape.");
        string Text(JsonNode? node, int maximum) => node is JsonValue value && value.TryGetValue(out string? text) && text.Length is > 0 && text.Length <= maximum ? text
            : throw new InvalidDataException("A saved scenario name is missing or too long.");
        int Int(JsonNode? node, int minimum, int maximum) => node is JsonValue value && value.TryGetValue(out int number) && number >= minimum && number <= maximum ? number
            : throw new InvalidDataException("A saved scenario number is out of range.");
        int limit = 4 * AnimationExecutionPolicy.MaximumPaths + 64;
        foreach (var item in Array("definitions", limit)) { var t = Tuple(item, 3); record.definitions.Add(new(Text(t[0], MaximumText), Int(t[1], 0, int.MaxValue), Int(t[2], 0, 4096))); }
        foreach (var item in Array("bindings", limit)) { var t = Tuple(item, 2); record.bindings.Add((Int(t[0], 0, record.definitions.Count - 1), Text(t[1], 64))); }
        foreach (var item in Array("events", limit)) { var t = Tuple(item, 3); record.events.Add((Int(t[0], 0, record.definitions.Count - 1), Text(t[1], MaximumText), Text(t[2], 128))); }
        // A scenario's own starts and the marked ones it kept (at most as many again).
        foreach (var item in Array("starts", 2 * AnimationSchedule.MaximumStarts))
        {
            var t = Tuple(item, 3);
            string id = Text(t[0], 64);
            Guid start = id == "primary" ? Guid.Empty : Guid.TryParseExact(id, "D", out Guid parsed) && parsed != Guid.Empty ? parsed : throw new InvalidDataException("A saved start has no identity.");
            double seconds = t[2] is JsonValue s && s.TryGetValue(out double d) && double.IsFinite(d) && d is >= 0 and <= AnimationSeconds.Maximum ? d : throw new InvalidDataException("A saved start time is out of range.");
            record.starts.Add((start, Int(t[1], 0, record.bindings.Count - 1), seconds)); record.startMarked.Add(false);
        }
        foreach (var item in Array("markedStarts", 2 * AnimationSchedule.MaximumStarts)) record.startMarked[Int(item, 0, record.starts.Count - 1)] = true;
        if (record.startMarked.Count(m => !m) > AnimationSchedule.MaximumStarts) throw new InvalidDataException($"A saved scenario has more than {AnimationSchedule.MaximumStarts} starts.");
        // A stand-in shares its identity with the marked start it stands in for: the one identity two starts may share.
        if (json["standIn"] is not null)
        {
            record.standIn = Int(json["standIn"], 0, record.starts.Count - 1);
            if (record.startMarked[record.standIn] || record.StoodFor() == null)
                throw new InvalidDataException("A saved stand-in is not a start beside the marked start it stands in for.");
        }
        HashSet<Guid> ids = [];
        for (int i = 0; i < record.starts.Count; i++)
            if (i != record.standIn && !ids.Add(record.starts[i].Id)) throw new InvalidDataException("Two saved starts share an identity.");
        foreach (var item in Array("paths", 2 * AnimationExecutionPolicy.MaximumPaths))
        {
            var t = Tuple(item, 3);
            record.paths.Add((Int(t[0], -record.starts.Count, record.paths.Count - 1), Int(t[1], 0, record.events.Count - 1), Int(t[2], -1, record.bindings.Count - 1)));
        }
        foreach (var item in Array("include", AnimationExecutionPolicy.MaximumPaths)) record.includes.Add(Int(item, 0, record.paths.Count - 1));
        foreach (var item in Array("markedInclude", AnimationExecutionPolicy.MaximumPaths)) record.markedIncludes.Add(Int(item, 0, record.paths.Count - 1));
        (int, int, bool) Condition(JsonNode? item)
        {
            var t = Tuple(item, 3);
            bool value = t[2] is JsonValue v && v.TryGetValue(out bool b) ? b : throw new InvalidDataException("A saved switch is not true or false.");
            return (Int(t[0], -record.starts.Count, record.paths.Count - 1), Int(t[1], 0, record.events.Count - 1), value);
        }
        foreach (var item in Array("conditions", AnimationExecutionPolicy.MaximumPaths)) record.conditions.Add(Condition(item));
        foreach (var item in Array("markedConditions", AnimationExecutionPolicy.MaximumPaths)) record.markedConditions.Add(Condition(item));
        record.trimmed = json["trimmed"] is null ? 0 : Int(json["trimmed"], 0, AnimationExecutionPolicy.MaximumPaths);
        foreach (var item in Array("keptMarks", Kinds.Length))
            record.kept.Add(item is JsonValue value && value.TryGetValue(out string? kind) && Kinds.Contains(kind) ? kind : throw new InvalidDataException("A saved kept kind is not starts, paths or conditions."));
        record.inputs = Inputs(json, notices);
        return record;
    }

    private static AnimationScenario Inputs(JsonObject json, List<AnimationScenarioNotice> notices)
    {
        var scenario = AnimationScenario.Default;
        T Read<T>(string key, T fallback, Func<JsonNode, T?> parse) where T : struct
        {
            if (json[key] is not { } node) return fallback;
            T? value = null;
            try { value = parse(node); } catch (Exception ex) when (ex is InvalidOperationException or FormatException or InvalidDataException) { }
            if (value is T found) return found;
            notices.Add(new("value", $"The saved scenario's {key} could not be read; it uses the default."));
            return fallback;
        }
        string? Name(JsonNode node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        Vector3? Point(JsonNode node) => node is JsonArray { Count: 3 } xyz && xyz.All(c => c is JsonValue v && v.TryGetValue(out float f) && float.IsFinite(f))
            ? new Vector3(xyz[0]!.GetValue<float>(), xyz[1]!.GetValue<float>(), xyz[2]!.GetValue<float>()) : null;
        var player = json["player"] switch
        {
            null => AnimationPlayerReference.AtStart,
            JsonValue value when value.TryGetValue(out string? kind) && kind == "atStart" => AnimationPlayerReference.AtStart,
            JsonValue value when value.TryGetValue(out string? kind) && kind == "none" => AnimationPlayerReference.None,
            JsonArray array when Point(array) is Vector3 p => AnimationPlayerReference.Custom(p),
            _ => null,
        };
        if (player == null) notices.Add(new("value", "The saved scenario's player could not be read; it uses the player at start."));
        int seed = Read("seed", 1, n => n is JsonValue v && v.TryGetValue(out int s) ? s : null);
        var detail = Read("effectsDetail", AnimationEffectsDetail.High, n => Name(n) switch { "high" => AnimationEffectsDetail.High, "medium" => AnimationEffectsDetail.Medium, "low" => AnimationEffectsDetail.Low, _ => null });
        var phase = Read("phase", AnimationScenarioPhase.Runtime, n => Name(n) switch { "runtime" => AnimationScenarioPhase.Runtime, "cleanup" => AnimationScenarioPhase.Cleanup, _ => null });
        float height = Read("height", 0f, n => n is JsonValue v && v.TryGetValue(out float h) && float.IsFinite(h) && h is >= -999 and <= 999 ? h : null);
        Vector3? beam = null;
        if (json["beamOrigin"] is { } beamNode && (beam = Point(beamNode)) == null)
            notices.Add(new("value", "The saved scenario's beam origin could not be read; it uses the binding's root."));
        var prerequisites = Read("prerequisites", AnimationPrerequisiteRule.IgnoreForScenarioStarts, n => Name(n) switch
            { "check" => AnimationPrerequisiteRule.Check, "ignoreStarts" => AnimationPrerequisiteRule.IgnoreForScenarioStarts, "ignore" => AnimationPrerequisiteRule.Ignore, _ => null });
        var range = Read("rangeGates", AnimationRangeGateRule.Game, n => Name(n) switch { "game" => AnimationRangeGateRule.Game, "ignore" => AnimationRangeGateRule.Ignore, _ => null });
        var zone = Read("zoneGates", AnimationZoneGateRule.Ignore, n => Name(n) switch { "ignore" => AnimationZoneGateRule.Ignore, "view" => AnimationZoneGateRule.View, _ => null });
        return scenario with
        {
            Player = player ?? AnimationPlayerReference.AtStart, Seed = seed, EffectsDetail = detail, Phase = phase, Height = height, BeamOrigin = beam,
            Prerequisites = prerequisites, RangeGates = range, ZoneGates = zone,
        };
    }
}
