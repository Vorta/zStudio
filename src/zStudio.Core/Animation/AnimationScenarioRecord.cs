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
            foreach (var sequence in entry.AllSequences)
                foreach (var ev in sequence.Events)
                    if (provenance.FindEvent(ev.Id) is { RecordId: string record } source2)
                    {
                        eventOf[ev.Id] = (entry.Index, record, source2.Kind);
                        eventAt[(entry.Index, record)] = ev;
                    }
        }
        foreach (var binding in ambiguous) entryOf.Remove(binding);
    }

    public Binding? BindingOf(int entry) => bindingOf.TryGetValue(entry, out var binding) ? binding : null;
    public int? EntryOf(Binding binding) => entryOf.TryGetValue(binding, out int entry) ? entry : null;
    public (int Entry, string Record, string Kind)? EventOf(Guid ev) => eventOf.TryGetValue(ev, out var at) ? at : null;
    public AnimationEvent? EventAt(int entry, string record, string kind) =>
        eventAt.TryGetValue((entry, record), out var ev) && eventOf.TryGetValue(ev.Id, out var at) && at.Kind == kind ? ev : null;
}

/// <summary>
/// A scenario in its durable form (docs/agents/sources.md, the project's <c>.zstudio/settings.json</c>): bindings and events
/// by source address, call paths as a tree of (parent, call event, target binding), inputs and rules. It is how a scenario
/// is saved per map and how it moves to a new compilation; what no longer matches is dropped with a notice, never silently.
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
    private readonly List<(Guid Id, int Binding, double Seconds)> starts = [];
    private readonly List<(int Parent, int Event, int Target)> paths = [];
    private readonly List<int> includes = [];
    private readonly List<(int Path, int Event, bool Value)> conditions = [];
    private AnimationScenario inputs = AnimationScenario.Default;
    private int trimmed;

    private AnimationScenarioRecord() { }

    /// <summary>The durable form of <paramref name="scenario"/>, or null when <paramref name="source"/> has no provenance.</summary>
    public static AnimationScenarioRecord? From(AnimationScenario scenario, AnimationScenarioSource source, List<AnimationScenarioNotice>? notices = null)
    {
        ArgumentNullException.ThrowIfNull(scenario); ArgumentNullException.ThrowIfNull(source);
        var addresses = source.Addresses;
        if (!addresses.Available) return null;
        AnimationScenarioRecord record = new() { inputs = Inputs(scenario) };
        Dictionary<AnimationScenarioAddresses.Definition, int> definitionIndex = [];
        Dictionary<AnimationScenarioAddresses.Binding, int> bindingIndex = [];
        Dictionary<(int, string, string), int> eventIndex = [];
        Dictionary<(int, int, int), int> pathIndex = [];
        Dictionary<string, int> roots = new(StringComparer.Ordinal);
        int lost = 0;
        int Definition(AnimationScenarioAddresses.Definition definition)
        {
            if (definitionIndex.TryGetValue(definition, out int at)) return at;
            record.definitions.Add(definition); return definitionIndex[definition] = record.definitions.Count - 1;
        }
        int? Binding(int entry)
        {
            if (addresses.BindingOf(entry) is not { } binding) return null;
            if (bindingIndex.TryGetValue(binding, out int at)) return at;
            record.bindings.Add((Definition(binding.Definition), binding.Root)); return bindingIndex[binding] = record.bindings.Count - 1;
        }
        int? Event(int entry, Guid id)
        {
            if (addresses.EventOf(id) is not { } at || at.Entry != entry || addresses.BindingOf(entry) is not { } binding) return null;
            var key = (Definition(binding.Definition), at.Record, at.Kind);
            if (eventIndex.TryGetValue(key, out int index)) return index;
            record.events.Add(key); return eventIndex[key] = record.events.Count - 1;
        }
        foreach (var start in scenario.Starts)
        {
            if (Binding(start.Entry) is not int binding) { lost++; continue; }
            record.starts.Add((start.Id, binding, start.Seconds));
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
                var key = (parent, eventAt, targetAt);
                if (!pathIndex.TryGetValue(key, out int node)) { record.paths.Add(key); pathIndex[key] = node = record.paths.Count - 1; }
                parent = node; caller = target;
            }
            return parent;
        }
        foreach (string path in scenario.IncludedCalls.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal))
            if (Path(path) is int node && node >= 0) record.includes.Add(node); else lost++;
        foreach (var condition in scenario.Conditions)
            if (Path(condition.InstancePath) is int node && Event(condition.Entry, condition.Event) is int ev) record.conditions.Add((node, ev, condition.Value)); else lost++;
        if (lost > 0) notices?.Add(new("source", $"{lost} scenario item{(lost == 1 ? " has" : "s have")} no source address and cannot be kept."));
        return record;
    }

    /// <summary>The scenario in <paramref name="source"/>'s compilation; what no longer matches is dropped and listed in <paramref name="notices"/>.</summary>
    public AnimationScenario Resolve(AnimationScenarioSource source, List<AnimationScenarioNotice> notices)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(notices);
        var addresses = source.Addresses;
        if (!addresses.Available)
        {
            if (starts.Count + includes.Count + conditions.Count > 0) notices.Add(new("source", "The animations have no source addresses, so the scenario's starts, call paths and switches could not be kept."));
            return inputs;
        }
        int?[] entries = bindings.Select(b => addresses.EntryOf(new(definitions[b.Definition], b.Root))).ToArray();
        List<AnimationScheduledStart> resolvedStarts = [];
        var rootOf = new (string Path, int Entry)?[starts.Count];
        for (int i = 0; i < starts.Count; i++)
        {
            var (id, binding, seconds) = starts[i];
            if (entries[binding] is not int entry || entry <= 0)
            {
                var (file, root) = (definitions[bindings[binding].Definition].File, bindings[binding].Root);
                notices.Add(new("start", $"{(id == Guid.Empty ? "The primary start" : "A start")} at {AnimationSeconds.Format(seconds)} (bound at {AnimationPhrases.Safe(root)} by {AnimationPhrases.Safe(Path.GetFileName(file))}) no longer matches a binding and was removed, with its call paths and switches."));
                continue;
            }
            var start = new AnimationScheduledStart(id, entry, seconds);
            resolvedStarts.Add(start); rootOf[i] = (start.Path, entry);
        }
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
        List<string> included = []; int lostPaths = 0;
        foreach (int node in includes) if (node >= 0 && node < resolved.Length && resolved[node] is { } path) included.Add(path.Path); else lostPaths++;
        List<AnimationConditionOverride> forced = []; int lostConditions = 0;
        foreach (var (node, eventAt, value) in conditions)
        {
            (string Path, int Entry)? instance = node < 0 ? -1 - node < rootOf.Length ? rootOf[-1 - node] : null : node < resolved.Length ? resolved[node] : null;
            if (instance is { Entry: > 0 } at && eventAt >= 0 && eventAt < events.Count && addresses.BindingOf(at.Entry) is { } binding
                && binding.Definition == definitions[events[eventAt].Definition]
                && addresses.EventAt(at.Entry, events[eventAt].Record, events[eventAt].Kind) is { Type: 31 or 33 } ev)
                forced.Add(new(at.Path, at.Entry, ev.Id, value));
            else lostConditions++;
        }
        if (lostPaths > 0) notices.Add(new("include", $"{lostPaths} included call path{(lostPaths == 1 ? " no longer matches" : "s no longer match")} after the change and {(lostPaths == 1 ? "was" : "were")} removed."));
        if (lostConditions > 0) notices.Add(new("condition", $"{lostConditions} condition switch{(lostConditions == 1 ? " no longer matches" : "es no longer match")} after the change and {(lostConditions == 1 ? "was" : "were")} set back to Game."));
        if (trimmed > 0) notices.Add(new("trimmed", $"{trimmed} included call path{(trimmed == 1 ? " was" : "s were")} not saved: a map's saved scenario holds at most {MaximumBytes / 1024} KB."));
        // A start without the primary keeps no lineage of its own to the opened binding: drop nothing more, the GUI opens one.
        // Paths whose ancestors were lost are lost with them; the remaining set stays prefix-closed.
        HashSet<string> kept = new(included, StringComparer.Ordinal);
        included.RemoveAll(p => !Ancestors(p).All(a => a.Length == 0 || a.StartsWith("/@", StringComparison.Ordinal) && a.Length == 34 || kept.Contains(a)));
        return inputs with { Starts = resolvedStarts, IncludedCalls = included, Conditions = forced };
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
    /// The scenario as a map's saved setting, within <see cref="MaximumBytes"/>: when larger, the deepest included call paths
    /// are left out until it fits, and the record says how many (its restore reports them). Null without provenance.
    /// </summary>
    public static JsonObject? Save(AnimationScenario scenario, AnimationScenarioSource source, List<AnimationScenarioNotice>? notices = null)
    {
        var record = From(scenario, source, notices);
        if (record == null) return null;
        var json = record.ToJson();
        int dropped = 0;
        var paths = scenario.IncludedCalls.OrderByDescending(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal).ToList();
        while (Size(json) > MaximumBytes && paths.Count > 0)
        {
            int cut = Math.Max(1, paths.Count / 8);
            paths.RemoveRange(0, cut); dropped += cut;
            record = From(scenario with { IncludedCalls = paths }, source)!;
            record.trimmed = dropped; json = record.ToJson();
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
        HashSet<Guid> ids = [];
        foreach (var item in Array("starts", AnimationSchedule.MaximumStarts))
        {
            var t = Tuple(item, 3);
            string id = Text(t[0], 64);
            Guid start = id == "primary" ? Guid.Empty : Guid.TryParseExact(id, "D", out Guid parsed) && parsed != Guid.Empty ? parsed : throw new InvalidDataException("A saved start has no identity.");
            double seconds = t[2] is JsonValue s && s.TryGetValue(out double d) && double.IsFinite(d) && d is >= 0 and <= AnimationSeconds.Maximum ? d : throw new InvalidDataException("A saved start time is out of range.");
            if (!ids.Add(start)) throw new InvalidDataException("Two saved starts share an identity.");
            record.starts.Add((start, Int(t[1], 0, record.bindings.Count - 1), seconds));
        }
        foreach (var item in Array("paths", 2 * AnimationExecutionPolicy.MaximumPaths))
        {
            var t = Tuple(item, 3);
            record.paths.Add((Int(t[0], -record.starts.Count, record.paths.Count - 1), Int(t[1], 0, record.events.Count - 1), Int(t[2], -1, record.bindings.Count - 1)));
        }
        foreach (var item in Array("include", AnimationExecutionPolicy.MaximumPaths)) record.includes.Add(Int(item, 0, record.paths.Count - 1));
        foreach (var item in Array("conditions", AnimationExecutionPolicy.MaximumPaths))
        {
            var t = Tuple(item, 3);
            bool value = t[2] is JsonValue v && v.TryGetValue(out bool b) ? b : throw new InvalidDataException("A saved switch is not true or false.");
            record.conditions.Add((Int(t[0], -record.starts.Count, record.paths.Count - 1), Int(t[1], 0, record.events.Count - 1), value));
        }
        record.trimmed = json["trimmed"] is null ? 0 : Int(json["trimmed"], 0, AnimationExecutionPolicy.MaximumPaths);
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
