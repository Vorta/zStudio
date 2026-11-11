using System.Globalization;
using System.Text.Json;
using zStudio.Core.Formats;

namespace zStudio.Core;

/// <summary>A mission resource the valve map reads: its project path and parsed tree.</summary>
public sealed record Mw3ValveSource(string Path, ZrdNode Root);

/// <summary>What the mission has for a reaction to name; null sets are unknown (nothing is reported against them).</summary>
public sealed record Mw3ValveCatalog(IReadOnlySet<string>? Vehicles = null, IReadOnlySet<string>? Sounds = null, IReadOnlySet<string>? Animations = null);

/// <summary>
/// A reaction's editable fields as text, so an untouched number keeps its stored form. <see cref="Kind"/> is the stored tag
/// (sound, sound_once, shutdown, delayupdate…). Target: the sound, animation, vehicle or valve. Operand: on/off, the team,
/// the strategy, or a timer's seconds. When: the valve value that triggers it (update and delayupdate have none).
/// </summary>
public sealed record Mw3ValveReactionSpec(string Kind, string? Target = null, string? Operand = null, string? When = null,
    string? X = null, string? Y = null, string? Z = null, string? Yaw = null);

/// <summary>One action of one valves.zrd record: the record's occurrence in the file and the stored tag identify it.</summary>
public sealed record Mw3ValveReaction(string Source, Guid Record, int Occurrence, Mw3ValveReactionSpec Spec, string Text, string? Problem = null);

/// <summary>
/// Something outside the valve's own records that sets it (<see cref="Mw3ValveMap.SetRoles"/>) or waits on it. Owner is the
/// vehicle, target, network node, objective group or other valve; Value the value set or expected.
/// </summary>
public sealed record Mw3ValveLink(string Role, string Source, Guid Record, string Owner, string Text, int? Value = null,
    string? Network = null, int? Node = null, string? Actor = null, string? Valve = null, int? Entry = null);

/// <summary>One valves.zrd record of a valve, as the Source section shows it.</summary>
public sealed record Mw3ValveRecord(string Source, Guid Record, int Occurrence, string? Problem);

public sealed record Mw3ValveInfo(string Name)
{
    /// <summary>A valves.zrd record names it; otherwise only other files do (the game makes it on first mention, at zero).</summary>
    public bool Defined => Records.Count > 0;
    /// <summary>
    /// The starting value as stored (the last record's that holds one; 0 without one). A computed valve keeps the word in
    /// its file but ignores it; it is shown and edited as stored, so that making the valve a switch again starts where the file says.
    /// </summary>
    public int Initial { get; init; }
    public string? Condition { get; init; }
    public IReadOnlyList<string> Inputs { get; init; } = [];
    public IReadOnlyList<Mw3ValveRecord> Records { get; init; } = [];
    public IReadOnlyList<Mw3ValveReaction> Reactions { get; init; } = [];
    public IReadOnlyList<Mw3ValveLink> SetBy { get; init; } = [];
    public IReadOnlyList<Mw3ValveLink> UsedBy { get; init; } = [];
    public IReadOnlyList<string> Problems { get; init; } = [];
    /// <summary>What it does in a few words: its reactions, else what waits on it, else what sets it.</summary>
    public string Summary { get; init; } = "";
    /// <summary>Every name it mentions (vehicles, sounds, animations, valves, regions), for search.</summary>
    public IReadOnlyList<string> Mentions { get; init; } = [];
}

/// <summary>
/// The mission's valves by exact name, gathered from valves.zrd (definitions and reactions), aiv.zrd (vehicle triggers),
/// targets.zrd (health thresholds), the networks (route gates, assignments, deploy points) and the map's objectives.
/// A reading aid over the shared trees: source records stay authoritative and are never changed here.
/// </summary>
public sealed class Mw3ValveMap
{
    public const int MaximumValves = 4096, MaximumLinks = 65_536, ShownLinks = 256;
    public static readonly string[] SetRoles = ["trigger", "health", "objective", "route", "timer", "fire", "deploy", "animation"];
    public static readonly Mw3ValveMap Empty = new([], null, false);
    public IReadOnlyList<Mw3ValveInfo> Valves { get; }
    /// <summary>The mission's valves.zrd path, when one was read.</summary>
    public string? DefinitionSource { get; }
    /// <summary>More names or links than the map keeps: the rest is in the source files.</summary>
    public bool Truncated { get; }
    private readonly Dictionary<string, Mw3ValveInfo> byName;
    private Mw3ValveMap(IReadOnlyList<Mw3ValveInfo> valves, string? definitionSource, bool truncated)
    {
        Valves = valves; DefinitionSource = definitionSource; Truncated = truncated;
        byName = valves.ToDictionary(v => v.Name, StringComparer.Ordinal);
    }
    public Mw3ValveInfo? Find(string name) => byName.GetValueOrDefault(name);

    private sealed class Builder(string name)
    {
        public readonly string Name = name;
        public int Initial; public string? Condition; public string[] Inputs = [];
        public readonly List<Mw3ValveRecord> Records = [];
        public readonly List<Mw3ValveReaction> Reactions = [];
        public readonly List<Mw3ValveLink> SetBy = [], UsedBy = [];
        public readonly List<string> Problems = [];
        public readonly HashSet<string> Mentions = new(StringComparer.Ordinal);
    }

    /// <param name="mission">The mission's folder name (m1, ia2…): the objectives block that belongs to it.</param>
    /// <param name="callbacks">The animations that set valves through callbacks 42–49 (<see cref="Mw3ValveSimulation.CallbackValves"/>); listed for the valves the files name.</param>
    public static Mw3ValveMap Build(IEnumerable<Mw3ValveSource> sources, string? mission, Mw3ValveCatalog? catalog = null, CancellationToken token = default,
        IEnumerable<Mw3ValveCallback>? callbacks = null)
    {
        Dictionary<string, Builder> valves = new(StringComparer.Ordinal);
        string? definitionSource = null; bool truncated = false; int links = 0;
        Builder? Get(string name)
        {
            if (valves.TryGetValue(name, out var found)) return found;
            if (valves.Count == MaximumValves || name.Length > 256) { truncated = true; return null; }
            valves.Add(name, found = new(name)); return found;
        }
        void Link(bool sets, string name, Mw3ValveLink link, params string?[] mentions)
        {
            if (Get(name) is not { } valve) return;
            if (++links > MaximumLinks) { truncated = true; return; }
            (sets ? valve.SetBy : valve.UsedBy).Add(link);
            foreach (string? mention in mentions) if (mention is { Length: > 0 and <= 256 } && valve.Mentions.Count < 64) valve.Mentions.Add(mention);
        }
        foreach (var source in sources.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string member = System.IO.Path.GetFileName(source.Path);
            if (member.Equals("valves.zrd", StringComparison.OrdinalIgnoreCase))
            {
                definitionSource ??= source.Path;
                int occurrence = -1;
                foreach (var record in MissionAiValves.Records(member, source.Root, token))
                {
                    occurrence++;
                    if (Get(record.Name) is not { } valve) continue;
                    string? problem = Mw3ValveProgram.SourceProblem(record.Value);
                    valve.Records.Add(new(source.Path, record.Id, occurrence, problem));
                    if (problem != null) { valve.Problems.Add($"Record {valve.Records.Count} is not as the game reads it ({problem.TrimEnd('.')}); it is kept as stored."); continue; }
                    string? condition = Mw3ValveProgram.Conditions.FirstOrDefault(k => Mw3ValveProgram.HasTag(record.Value, k));
                    if (condition != null)
                    {
                        string[] inputs = [.. (Mw3ValveProgram.Find(record.Value, "namelist")?.Children ?? []).Where(n => n.Kind == ZrdKind.String).Select(n => n.Text).Take(256)];
                        if (valve.Condition != null)
                        {
                            // The game keeps the first computed definition and drops this whole record: its value and reactions too.
                            const string dropped = "A second computed definition: the game keeps the first and drops this whole record, with its reactions.";
                            valve.Records[^1] = valve.Records[^1] with { Problem = dropped };
                            valve.Problems.Add("A second computed definition: the game keeps the first and drops that whole record, with its reactions.");
                            continue;
                        }
                        valve.Condition = condition; valve.Inputs = inputs;
                        foreach (string input in inputs)
                            Link(false, input, new("computed", source.Path, record.Id, valve.Name, $"{ConditionText(condition)} ({inputs.Length} input{(inputs.Length == 1 ? "" : "s")})", Valve: valve.Name), valve.Name);
                        foreach (string input in inputs) valve.Mentions.Add(input);
                    }
                    if (Mw3ValveProgram.Find(record.Value, "value") is { Children: [{ Kind: ZrdKind.Int or ZrdKind.Float } word] }) valve.Initial = unchecked((int)word.Bits);
                    foreach (string kind in Mw3ValveProgram.ActionOrder)
                    {
                        string stored = kind is "sound" or "animation" && Mw3ValveProgram.HasTag(record.Value, kind + "_once") ? kind + "_once" : kind;
                        if (Mw3ValveProgram.Find(record.Value, stored) is not { Kind: ZrdKind.Array } arguments) continue;
                        var spec = Spec(stored, arguments);
                        string? missing = Missing(spec, catalog);
                        valve.Reactions.Add(new(source.Path, record.Id, occurrence, spec, ReactionText(spec), missing));
                        if (missing != null) valve.Problems.Add(missing);
                        if (spec.Target is { Length: > 0 } target) valve.Mentions.Add(target);
                        if (kind is "update" or "delayupdate" && spec.Target is { } next)
                            Link(true, next, new(kind == "update" ? "fire" : "timer", source.Path, record.Id, valve.Name,
                                kind == "update" ? "fires its reactions whenever it changes" : $"sets it to its own value {Seconds(spec.Operand)} after it changes", Valve: valve.Name), valve.Name);
                    }
                    // A record with several reactions of one kind: the game builds one (the card edits that one's fields only).
                    if (record.Value.Children.Where(n => n.Kind == ZrdKind.String).Select(n => n.Text is "sound_once" ? "sound" : n.Text is "animation_once" ? "animation" : n.Text)
                        .Where(t => Mw3ValveProgram.ActionOrder.Contains(t)).GroupBy(t => t, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } repeated)
                        valve.Problems.Add($"A record stores more than one {repeated.Key} reaction: the game uses only one of them (a once-only one before a plain one, else the first).");
                }
            }
            else if (member.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase)) Vehicles(source);
            else if (member.Equals("targets.zrd", StringComparison.OrdinalIgnoreCase)) Targets(source);
            else if (member.Equals("objectives.zrd", StringComparison.OrdinalIgnoreCase)) Objectives(source);
            else if (MissionAiNetworks.IsCandidate(member, mw3: true) && MissionAiValves.IsNetwork(source.Root)) Network(source, member);
        }
        // An animation sets the valve named as the node it binds; only valves the files name are listed.
        foreach (var callback in callbacks ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (!valves.ContainsKey(callback.Valve)) continue;
            Link(true, callback.Valve, new("animation", "", Guid.Empty, callback.Animation, callback.Values is [var only] ? $"its events set it to {only}" : "its events set it to 0 and to 1",
                callback.Values is [var value] ? value : null, Entry: callback.Entry), callback.Animation);
        }
        List<Mw3ValveInfo> result = [];
        foreach (var valve in valves.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Name, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            result.Add(new(valve.Name)
            {
                Initial = valve.Initial, Condition = valve.Condition, Inputs = valve.Inputs, Records = valve.Records, Reactions = valve.Reactions,
                SetBy = valve.SetBy, UsedBy = valve.UsedBy, Problems = [.. valve.Problems.Distinct(StringComparer.Ordinal).Take(16)],
                Summary = Summary(valve), Mentions = [.. valve.Mentions.Order(StringComparer.Ordinal)],
            });
        }
        return new(result, definitionSource, truncated);

        void Vehicles(Mw3ValveSource source)
        {
            var fields = MissionAiValves.Fields(source.Root).Children;
            for (int i = 0; i + 1 < fields.Count; i += 2)
            {
                token.ThrowIfCancellationRequested();
                if (fields[i].Kind != ZrdKind.String || fields[i + 1].Kind != ZrdKind.Array) continue;
                string vehicle = fields[i].Text;
                if (Mw3ValveProgram.Find(fields[i + 1], "vlist") is not { Kind: ZrdKind.Array } list) continue;
                foreach (var trigger in list.Children)
                {
                    var c = trigger.Children;
                    if (trigger.Kind != ZrdKind.Array || c.Count < 16 || c[15].Kind != ZrdKind.String) continue;
                    int value = On(c, 0) ? 1 : 0;
                    string? region = c[9].Kind == ZrdKind.String ? c[9].Text : null;
                    Link(true, c[15].Text, new("trigger", source.Path, c[15].Id, vehicle, $"{TriggerText(c)} → {value}", value, Actor: vehicle), vehicle, region);
                }
            }
        }
        void Targets(Mw3ValveSource source)
        {
            var fields = MissionAiValves.Fields(source.Root).Children;
            for (int i = 0; i + 1 < fields.Count; i += 2)
            {
                token.ThrowIfCancellationRequested();
                if (fields[i].Kind != ZrdKind.String || fields[i + 1].Kind != ZrdKind.Array) continue;
                string target = fields[i].Text;
                if (Mw3ValveProgram.Find(fields[i + 1], "health_below") is not { Kind: ZrdKind.Array } thresholds) continue;
                for (int j = 0; j + 2 < thresholds.Children.Count; j += 3)
                {
                    if (thresholds.Children[j] is not { Kind: ZrdKind.String } name) continue;
                    int value = Mw3ValveRuntime.Integer(thresholds, j + 1); float health = Mw3ValveRuntime.Number(thresholds, j + 2);
                    string when = health <= 0 ? "when destroyed" : $"health below {Number(health)}";
                    Link(true, name.Text, new("health", source.Path, name.Id, target, $"{when} → {value}", value, Actor: target), target);
                }
            }
        }
        void Objectives(Mw3ValveSource source)
        {
            // objectives.zrd holds every mission of the map: only this mission's block names this mission's valves.
            var fields = MissionAiValves.Fields(source.Root).Children; ZrdNode? block = null;
            for (int i = 0; mission != null && i + 1 < fields.Count; i += 2)
                if (fields[i].Kind == ZrdKind.String && fields[i].Text.Equals(mission, StringComparison.OrdinalIgnoreCase) && fields[i + 1].Kind == ZrdKind.Array) { block = fields[i + 1]; break; }
            if (block != null) Walk(block, "objective", 0);
            void Walk(ZrdNode node, string group, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (node.Kind != ZrdKind.Array || depth > 64) return;
                var c = node.Children;
                for (int i = 0; i < c.Count; i++)
                {
                    if (c[i].Kind != ZrdKind.String || i + 1 >= c.Count || c[i + 1].Kind != ZrdKind.Array) { Walk(c[i], group, depth + 1); continue; }
                    var value = c[i + 1];
                    if (c[i].Text == "valve_change" && Mw3ValveProgram.Find(value, "valve") is { Children: [{ Kind: ZrdKind.String } watched] })
                    {
                        int expected = Mw3ValveProgram.Find(value, "value") is { Children: [{ Kind: ZrdKind.Int or ZrdKind.Float } word] } ? unchecked((int)word.Bits) : 1;
                        Link(false, watched.Text, new("objective", source.Path, c[i].Id, group, group == "fail objective" ? $"fails the mission when it is {expected}" : $"is met when it is {expected}", expected));
                    }
                    else if (c[i].Text == "set_valve" && value.Children is [{ Kind: ZrdKind.String } set, { Kind: ZrdKind.Int or ZrdKind.Float } assigned])
                        Link(true, set.Text, new("objective", source.Path, c[i].Id, group, $"when its condition is met → {unchecked((int)assigned.Bits)}", unchecked((int)assigned.Bits)));
                    // The mission block's groups name their objectives: primary, secondary, final, fail.
                    Walk(value, depth == 0 && c[i].Text is "primary" or "secondary" or "final" or "fail" ? c[i].Text + " objective" : group, depth + 1);
                    i++;
                }
            }
        }
        void Network(Mw3ValveSource source, string member)
        {
            string network = System.IO.Path.GetFileNameWithoutExtension(member);
            foreach (var record in MissionAiValves.Records(member, source.Root, token))
            {
                var c = record.Value.Children;
                string where = record.Kind == "node" ? $"{network} · node {record.NodeIndex}" : $"{network} · link {record.From} → {string.Join(", ", record.TargetNodes.Take(4))}";
                int? node = record.NodeIndex ?? record.From;
                if (record.Name == "valve" && c.Count >= 2 && c[1].Kind == ZrdKind.String)
                {
                    bool enabled = Mw3ValveRuntime.Integer(record.Value, 0) != 0; int? expected = c.Count > 2 ? Mw3ValveRuntime.Integer(record.Value, 2) : null;
                    Link(false, c[1].Text, new("gate", source.Path, record.Id, where, !enabled ? "gate switched off" : expected is int e ? $"passes while it is {e}"
                        : "the last link passes while it is 1, the others while it is 0", expected, source.Path, node));
                }
                else if (record.Name == "valveunion")
                    foreach (var term in c)
                        if (term.Children.Count >= 2 && term.Children[1].Kind == ZrdKind.String)
                        {
                            int expected = term.Children.Count > 2 ? Mw3ValveRuntime.Integer(term, 2) : 1;
                            Link(false, term.Children[1].Text, new("union", source.Path, record.Id, where, Mw3ValveRuntime.Integer(term, 0) == 0 ? "term switched off"
                                : $"passes while it is {expected}, or another of its {c.Count} valves holds", expected, source.Path, node));
                        }
                if (record.Name == "valve_assign" && c.Count >= 2 && c[0].Kind == ZrdKind.String)
                {
                    int value = Mw3ValveRuntime.Integer(record.Value, 1);
                    Link(true, c[0].Text, new("route", source.Path, record.Id, where, $"a vehicle driving through → {value}", value, source.Path, node));
                }
            }
            // Deploy points and waits name valves beside their route: listed so the valve is found, without guessing more.
            var fields = MissionAiValves.Fields(source.Root).Children;
            for (int i = 0; i + 1 < fields.Count; i += 2)
            {
                token.ThrowIfCancellationRequested();
                if (fields[i].Kind != ZrdKind.String || !MissionAiNetworks.TryNodeIndex(fields[i].Text, out int index) || !MissionAiNetworks.IsSpatial(fields[i + 1], true, token)) continue;
                var c = fields[i + 1].Children;
                for (int j = 3; j + 1 < c.Count; j += 2)
                {
                    if (c[j].Kind != ZrdKind.String || c[j].Text is not ("deploy" or "mcbwait") || c[j + 1].Kind != ZrdKind.Array) continue;
                    foreach (int at in c[j].Text == "deploy" ? new[] { 0, 2 } : [0])
                        if (c[j + 1].Children.ElementAtOrDefault(at) is { Kind: ZrdKind.String, Text.Length: > 0 } named)
                            Link(true, named.Text, new("deploy", source.Path, named.Id, $"{network} · node {index}", c[j].Text == "deploy" ? "named by this deploy point" : "named by this wait point",
                                Network: source.Path, Node: index));
                }
            }
        }
    }

    private static bool On(IReadOnlyList<ZrdNode> c, int index) => index < c.Count && c[index].Kind is ZrdKind.Int or ZrdKind.Float && c[index].Bits != 0;
    private static string Word(IReadOnlyList<ZrdNode> c, int index) => index < c.Count && c[index].Kind is ZrdKind.Int or ZrdKind.Float ? c[index].Value : "?";
    /// <summary>A vehicle trigger's conditions in the order the game tests them: the first that holds sets the valve.</summary>
    public static string TriggerText(IReadOnlyList<ZrdNode> c)
    {
        List<string> conditions = [];
        if (On(c, 3)) conditions.Add("hit");
        if (On(c, 6)) conditions.Add($"heat at least {Word(c, 7)}");
        if (On(c, 4)) conditions.Add($"health at most {Word(c, 5)}");
        if (On(c, 1)) conditions.Add($"ammo at most {Word(c, 2)}");
        if (On(c, 12)) conditions.Add($"enemy within {Word(c, 13)}");
        if (On(c, 10)) conditions.Add($"enemy in sight within {Word(c, 11)}");
        bool inside = On(c, 8), outside = c.Count >= 17 && On(c, 16);
        if (c.Count > 9 && c[9].Kind == ZrdKind.String && (inside || outside))
            conditions.Add(inside && outside ? $"anywhere ({MissionAiValves.Short(c[9].Text, 64)})" : $"{(inside ? "inside" : "outside")} {MissionAiValves.Short(c[9].Text, 64)}");
        return conditions.Count == 0 ? "no condition switched on" : string.Join(" or ", conditions);
    }

    public static string ConditionText(string? condition) => condition switch
    {
        "any_nonzero" => "on while any input is on", "any_zero" => "on while any input is off",
        "all_nonzero" => "on while all inputs are on", "all_zero" => "on while all inputs are off", _ => "switch",
    };
    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Seconds(string? operand) => operand is { Length: > 0 } ? operand + " s" : "later";

    /// <summary>A reaction's arguments as its editable fields.</summary>
    public static Mw3ValveReactionSpec Spec(string kind, ZrdNode arguments)
    {
        var c = arguments.Children;
        string? Name(int i) => i < c.Count && c[i].Kind == ZrdKind.String ? c[i].Text : null;
        string? Raw(int i) => i < c.Count && c[i].Kind is ZrdKind.Int or ZrdKind.Float ? unchecked((int)c[i].Bits).ToString(CultureInfo.InvariantCulture) : null;
        string? Decimal(IReadOnlyList<ZrdNode> list, int i) => i < list.Count && list[i].Kind is ZrdKind.Int or ZrdKind.Float ? list[i].Value : null;
        return kind switch
        {
            "sound" or "sound_once" or "animation" or "animation_once" or "destroy" => new(kind, Name(0), When: Raw(1)),
            "scan_aim" or "scan_hit" or "scan_time" or "canleave" or "shutdown" or "setteam" or "attack_strategy" => new(kind, Name(0), Raw(1), Raw(2)),
            "teleport" => c.ElementAtOrDefault(1) is { Kind: ZrdKind.Array } xyz
                ? new(kind, Name(0), When: Raw(3), X: Decimal(xyz.Children, 0), Y: Decimal(xyz.Children, 1), Z: Decimal(xyz.Children, 2), Yaw: Decimal(c, 2)) : new(kind, Name(0), When: Raw(3)),
            "missionreset" or "missionabort" or "missioncomplete" => new(kind, When: Raw(0)),
            "update" => new(kind, Name(0)),
            // delayupdate keeps the raw word as its float seconds.
            "delayupdate" => new(kind, Name(0), c.Count > 1 && c[1].Kind is ZrdKind.Int or ZrdKind.Float ? Number(Mw3ValveRuntime.Number(arguments, 1)) : null),
            _ => new(kind),
        };
    }

    /// <summary>A reaction's fields in stored order (a teleport's position flattened), by the layout its kind stores.</summary>
    private static (int Layout, string?[] Fields) Flat(Mw3ValveReactionSpec spec) => spec.Kind switch
    {
        "sound" or "sound_once" or "animation" or "animation_once" or "destroy" => (1, [spec.Target, spec.When]),
        "scan_aim" or "scan_hit" or "scan_time" or "canleave" or "shutdown" or "setteam" or "attack_strategy" => (2, [spec.Target, spec.Operand, spec.When]),
        "teleport" => (3, [spec.Target, spec.X, spec.Y, spec.Z, spec.Yaw, spec.When]),
        "missionreset" or "missionabort" or "missioncomplete" => (4, [spec.When]),
        "update" => (5, [spec.Target]),
        "delayupdate" => (6, [spec.Target, spec.Operand]),
        _ => (0, []),
    };
    private static ZrdNode[]? FlatNodes(int layout, ZrdNode arguments)
    {
        var c = arguments.Children;
        if (layout == 3) return c.Count == 4 && c[1] is { Kind: ZrdKind.Array, Children.Count: 3 } xyz ? [c[0], xyz.Children[0], xyz.Children[1], xyz.Children[2], c[2], c[3]] : null;
        return [.. c];
    }
    /// <summary>
    /// The arguments for an edited reaction: every field that still reads as it was stored keeps its original node, so an
    /// untouched number keeps its kind, its text and its place (a teleport's whole-number coordinates stay whole numbers).
    /// </summary>
    public static ZrdNode Arguments(Mw3ValveReactionSpec spec, string storedKind, ZrdNode stored)
    {
        var fresh = Arguments(spec);
        var (layout, fields) = Flat(spec); var (storedLayout, storedFields) = Flat(Spec(storedKind, stored));
        if (layout == 0 || layout != storedLayout || FlatNodes(layout, stored) is not { } old || FlatNodes(layout, fresh) is not { } created || old.Length != fields.Length || created.Length != fields.Length)
        {
            // Another kind's fields: the stored list stays the list and each value takes the place of the one stored there
            // (its token changes in place; one that reads the same is kept), so the comments and layout around them stay.
            return stored with { Children = [.. fresh.Children.Select((node, i) => i >= stored.Children.Count || node.Kind == ZrdKind.Array || stored.Children[i].Kind == ZrdKind.Array ? node
                : node.Kind == stored.Children[i].Kind && node.Text == stored.Children[i].Text && node.Bits == stored.Children[i].Bits ? stored.Children[i] : node with { Id = stored.Children[i].Id })] };
        }
        // A changed field replaces its token in place: it keeps the stored node's identity, also when a whole number becomes
        // a fraction, so the comments and layout around it stay.
        ZrdNode[] nodes = [.. created.Select((node, i) => fields[i] == storedFields[i] ? old[i] : node.Kind != ZrdKind.Array && old[i].Kind != ZrdKind.Array ? node with { Id = old[i].Id } : node)];
        if (layout != 3) return stored with { Children = nodes };
        var position = stored.Children[1];
        return stored with { Children = [nodes[0], nodes[1..4].SequenceEqual(position.Children) ? position : position with { Children = nodes[1..4] }, nodes[4], nodes[5]] };
    }

    /// <summary>The arguments a reaction stores, from its fields; a field the kind needs and cannot read is refused.</summary>
    public static ZrdNode Arguments(Mw3ValveReactionSpec spec)
    {
        ZrdNode Name(string? text, string what)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 256) throw new InvalidDataException($"Enter {what} (at most 256 characters).");
            return ZrdNode.Create(ZrdKind.String, JsonSerializer.Serialize(text));
        }
        ZrdNode Whole(string? text, string what) => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? ZrdNode.Create(ZrdKind.Int, number.ToString(CultureInfo.InvariantCulture)) : throw new InvalidDataException($"Enter {what} as a whole number.");
        ZrdNode Real(string? text, string what, bool positive = false) => float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number) && (!positive || number >= 0)
            ? ZrdNode.Create(ZrdKind.Float, number.ToString("R", CultureInfo.InvariantCulture)) : throw new InvalidDataException($"Enter {what} as a {(positive ? "number of 0 or more" : "number")}.");
        ZrdNode[] children = spec.Kind switch
        {
            "sound" or "sound_once" => [Name(spec.Target, "the sound's name"), Whole(spec.When, "the triggering value")],
            "animation" or "animation_once" => [Name(spec.Target, "the animation's name"), Whole(spec.When, "the triggering value")],
            "destroy" => [Name(spec.Target, "the vehicle or object to destroy"), Whole(spec.When, "the triggering value")],
            "scan_aim" or "scan_hit" or "scan_time" or "canleave" or "shutdown" => [Name(spec.Target, "the vehicle's name"), Whole(spec.Operand, "on (1) or off (0)"), Whole(spec.When, "the triggering value")],
            "setteam" => [Name(spec.Target, "the vehicle's name"), Whole(spec.Operand, "the team"), Whole(spec.When, "the triggering value")],
            "attack_strategy" => [Name(spec.Target, "the vehicle's name"), Whole(spec.Operand, "the attack strategy"), Whole(spec.When, "the triggering value")],
            "teleport" => [Name(spec.Target, "the vehicle's name"), ZrdNode.Create(ZrdKind.Array) with { Children = [Real(spec.X, "X"), Real(spec.Y, "Y"), Real(spec.Z, "Z")] },
                Real(spec.Yaw, "the heading"), Whole(spec.When, "the triggering value")],
            "missionreset" or "missionabort" or "missioncomplete" => [Whole(spec.When, "the triggering value")],
            "update" => [Name(spec.Target, "the valve to fire")],
            "delayupdate" => [Name(spec.Target, "the valve to set"), Real(spec.Operand, "the delay in seconds", positive: true)],
            _ => throw new InvalidDataException("Choose a reaction the game knows."),
        };
        return ZrdNode.Create(ZrdKind.Array) with { Children = children };
    }

    /// <summary>What a reaction does, in words ("wakes owens_01", "plays com_mech once").</summary>
    public static string ReactionText(Mw3ValveReactionSpec spec)
    {
        string target = spec.Target is { Length: > 0 } name ? MissionAiValves.Short(name, 64) : "?";
        bool on = spec.Operand is not (null or "0");
        return spec.Kind switch
        {
            "sound" => $"plays {target}", "sound_once" => $"plays {target} once",
            "animation" => $"starts animation {target}", "animation_once" => $"starts animation {target} once",
            "shutdown" => on ? $"powers down {target}" : $"wakes {target}",
            "destroy" => $"destroys {target}", "teleport" => $"teleports {target}",
            "setteam" => $"moves {target} to team {spec.Operand ?? "?"}", "attack_strategy" => $"sets {target}'s attack strategy to {spec.Operand ?? "?"}",
            "scan_aim" => $"turns {target}'s aim scan {(on ? "on" : "off")}", "scan_hit" => $"turns {target}'s hit scan {(on ? "on" : "off")}",
            "scan_time" => $"turns {target}'s timed scan {(on ? "on" : "off")}", "canleave" => on ? $"lets {target} leave its network" : $"keeps {target} on its network",
            "update" => $"fires {target}", "delayupdate" => $"sets {target} after {Seconds(spec.Operand)}",
            "missionabort" => "aborts the mission", "missionreset" => "mission reset (no effect in the game)", "missioncomplete" => "mission complete (no effect in the game)",
            _ => spec.Kind,
        };
    }

    private static string? Missing(Mw3ValveReactionSpec spec, Mw3ValveCatalog? catalog)
    {
        if (catalog == null || spec.Target is not { Length: > 0 } target) return null;
        string name = MissionAiValves.Short(target, 64);
        return spec.Kind switch
        {
            "sound" or "sound_once" when catalog.Sounds is { } sounds && !sounds.Contains(target) => $"No sound is named {name}: the reaction does nothing.",
            "animation" or "animation_once" when catalog.Animations is { } animations && !animations.Contains(target) => $"No animation is named {name}: the reaction does nothing.",
            "shutdown" or "destroy" or "teleport" or "setteam" or "attack_strategy" or "scan_aim" or "scan_hit" or "scan_time" or "canleave"
                when catalog.Vehicles is { } vehicles && !vehicles.Contains(target) && target != "player" => $"This mission has no vehicle or target named {name}: the reaction does nothing.",
            _ => null,
        };
    }

    private static string Summary(Builder valve)
    {
        if (valve.Reactions.Count > 0)
        {
            // Timers and sounds are counted; the reactions that change the mission are named.
            int timers = valve.Reactions.Count(r => r.Spec.Kind == "delayupdate"), sounds = valve.Reactions.Count(r => r.Spec.Kind is "sound" or "sound_once");
            var named = valve.Reactions.Where(r => r.Spec.Kind is not ("delayupdate" or "sound" or "sound_once")).Select(r => r.Text).ToList();
            if (named.Count == 0 && sounds == 1) { named.Add(valve.Reactions.First(r => r.Spec.Kind is "sound" or "sound_once").Text); sounds = 0; }
            if (named.Count == 0 && timers == 1) { named.Add(valve.Reactions.First(r => r.Spec.Kind == "delayupdate").Text); timers = 0; }
            List<string> parts = [.. named.Take(2)];
            if (named.Count > 2) parts.Add($"{named.Count - 2} more");
            if (timers > 0) parts.Add(timers == 1 ? "1 timer" : $"{timers} timers");
            if (sounds > 0) parts.Add(sounds == 1 ? "1 sound" : $"{sounds} sounds");
            return string.Join(" · ", parts);
        }
        if (valve.Condition != null) return valve.Inputs.Length == 1 ? ConditionText(valve.Condition).Replace("any input", "its input").Replace("all inputs are", "its input is")
            : ConditionText(valve.Condition).Replace("any input", $"any of {valve.Inputs.Length}").Replace("all inputs", $"all {valve.Inputs.Length}");
        int gates = valve.UsedBy.Count(l => l.Role is "gate" or "union");
        if (gates > 0) return gates == 1 ? $"gates {valve.UsedBy.First(l => l.Role is "gate" or "union").Owner}" : $"gates {gates} route links";
        if (valve.UsedBy.FirstOrDefault(l => l.Role == "objective") is { } objective) return objective.Owner;
        if (valve.UsedBy.FirstOrDefault(l => l.Role == "computed") is { } computed) return $"input of {MissionAiValves.Short(computed.Owner, 64)}";
        if (valve.SetBy.Count > 0) return $"set by {MissionAiValves.Short(valve.SetBy[0].Owner, 64)}" + (valve.SetBy.Count > 1 ? $" and {valve.SetBy.Count - 1} more" : "");
        return "";
    }
}
