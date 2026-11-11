using zStudio.Core.Formats;

namespace zStudio.Core;

public sealed record Mw3ValveAction(string Kind, ZrdNode Arguments);
public sealed record Mw3ValveDefinition(Guid Record, string Name, int? Initial, string? Condition,
    IReadOnlyList<string> Inputs, IReadOnlyList<Mw3ValveAction> Actions);

/// <summary>The native reader's semantic projection. Source trees remain authoritative and unmodified.</summary>
public sealed record Mw3ValveProgram(IReadOnlyList<Mw3ValveDefinition> Definitions)
{
    public void ValidateForEditing()
    {
        foreach (var definition in Definitions)
            if (Problem(definition) is { } problem) throw new InvalidDataException(problem);
        _ = new Mw3ValveRuntime(this).Values();
        Dictionary<string, List<string>> edges = new(StringComparer.Ordinal);
        HashSet<string> compound = new(StringComparer.Ordinal);
        foreach (var definition in Definitions)
        {
            if (definition.Condition != null && definition.Inputs.Count > 0)
            {
                if (!compound.Add(definition.Name)) throw new InvalidDataException("A valve cannot have two compound definitions.");
                foreach (string input in definition.Inputs) Edge(input, definition.Name);
            }
            foreach (var action in definition.Actions)
                if (action.Kind == "update" && Mw3ValveRuntime.Text(action.Arguments, 0) is { } target) Edge(definition.Name, target);
        }
        Dictionary<string, byte> state = new(StringComparer.Ordinal);
        foreach (string name in edges.Keys) Visit(name, 0);
        void Edge(string from, string to)
        {
            if (!edges.TryGetValue(from, out var list)) edges.Add(from, list = []);
            list.Add(to);
        }
        void Visit(string name, int depth)
        {
            if (state.TryGetValue(name, out byte s))
            { if (s == 1) throw new InvalidDataException("The edit creates an immediate valve reaction cycle."); return; }
            if (depth >= 128) throw new InvalidDataException("Valve reaction nesting exceeds preview limits.");
            state[name] = 1;
            if (edges.TryGetValue(name, out var list)) foreach (string target in list) Visit(target, depth + 1);
            state[name] = 2;
        }
    }
    public static string? Problem(Mw3ValveDefinition definition)
    {
        if (definition.Condition != null && definition.Inputs.Count == 0) return "A compound valve requires a nonempty namelist.";
        foreach (var action in definition.Actions)
        {
            var c = action.Arguments.Children;
            bool Word(int i) => i < c.Count && c[i].Kind is ZrdKind.Int or ZrdKind.Float;
            bool Name(int i) => i < c.Count && c[i].Kind == ZrdKind.String;
            bool Number(int i) => Word(i) && float.IsFinite(Mw3ValveRuntime.Number(action.Arguments, i));
            bool valid = action.Kind switch
            {
                "sound" or "sound_once" or "animation" or "animation_once" or "destroy" => c.Count == 2 && Name(0) && Word(1),
                "scan_aim" or "scan_hit" or "scan_time" or "canleave" or "shutdown" or "setteam" or "attack_strategy" => c.Count == 3 && Name(0) && Word(1) && Word(2),
                "delayupdate" => c.Count == 2 && Name(0) && Number(1),
                "update" => c.Count == 1 && Name(0),
                "missionreset" or "missionabort" or "missioncomplete" => c.Count == 1 && Word(0),
                "teleport" => c.Count == 4 && Name(0) && c[1].Kind == ZrdKind.Array && c[1].Children.Count == 3 &&
                    c[1].Children.All(n => n.Kind == ZrdKind.Int || n.Kind == ZrdKind.Float && float.IsFinite(BitConverter.UInt32BitsToSingle(n.Bits))) &&
                    Word(2) && float.IsFinite(Mw3ValveRuntime.Scalar(action.Arguments, 2)) && Word(3),
                _ => true
            };
            if (!valid) return $"Invalid {action.Kind} parameters in valve {MissionAiValves.Short(definition.Name)}.";
        }
        return null;
    }
    public const int MaximumValves = 16_384, MaximumReactions = 131_072;
    public static readonly string[] Conditions = ["any_nonzero", "any_zero", "all_nonzero", "all_zero"];
    public static readonly string[] ActionOrder = ["sound", "animation", "scan_aim", "scan_hit", "scan_time", "canleave",
        "shutdown", "destroy", "setteam", "attack_strategy", "teleport", "missionreset", "missionabort", "missioncomplete", "update", "delayupdate"];

    public static string? SourceProblem(ZrdNode value)
    {
        if (value.Kind != ZrdKind.Array) return "Expected ordered valve properties.";
        foreach (string tag in ActionOrder.Concat(["sound_once", "animation_once"]))
            if (HasTag(value, tag) && (Find(value, tag) is not { Kind: ZrdKind.Array } args ||
                Problem(new(Guid.Empty, "source", null, null, [], [new(tag, args)])) is { }))
                return $"Invalid {tag} parameters.";
        if (HasTag(value, "value") && Find(value, "value") == null) return "Expected one initial value word.";
        if (Find(value, "value") is { } initial && (initial.Kind != ZrdKind.Array || initial.Children.Count != 1 ||
            initial.Children[0].Kind is not (ZrdKind.Int or ZrdKind.Float))) return "Expected one initial value word.";
        if (Conditions.Any(k => HasTag(value, k)) && (Find(value, "namelist") is not { Kind: ZrdKind.Array } names ||
            names.Children.Count == 0 || names.Children.Any(n => n.Kind != ZrdKind.String))) return "Expected a nonempty namelist of valve names.";
        return null;
    }

    /// <summary>
    /// The definitions the game builds: a second computed definition of a valve is dropped with its whole record
    /// (its value and reactions too), as <see cref="Mw3ValveRuntime"/> does.
    /// </summary>
    public Mw3ValveProgram Effective()
    {
        HashSet<string> computed = new(StringComparer.Ordinal);
        return new([.. Definitions.Where(d => !(d.Condition != null && d.Inputs.Count > 0) || computed.Add(d.Name))]);
    }

    /// <param name="skipUnreadable">Leave out records the game does not read as written instead of refusing the file (what an edit of another record is checked against).</param>
    public static Mw3ValveProgram Read(ZrdNode root, CancellationToken token = default, bool skipUnreadable = false)
    {
        var body = MissionAiValves.Fields(root);
        List<Mw3ValveDefinition> definitions = [];
        if (body.Kind != ZrdKind.Array) throw new InvalidDataException("Valve definitions need an ordered array.");
        int reactions = 0;
        for (int i = 0; i + 1 < body.Children.Count; i += 2)
        {
            token.ThrowIfCancellationRequested();
            var key = body.Children[i]; var value = body.Children[i + 1];
            if (key.Kind != ZrdKind.String) continue;
            if (SourceProblem(value) is { } problem)
            {
                if (skipUnreadable) continue;
                throw new InvalidDataException($"Valve {MissionAiValves.Short(key.Text)}: {problem}");
            }
            if (definitions.Count == MaximumReactions) throw new InvalidDataException("Too many valve definition occurrences for preview.");
            string? condition = Conditions.FirstOrDefault(k => HasTag(value, k));
            var names = Find(value, "namelist");
            if (names?.Children.Count > MaximumValves) throw new InvalidDataException("Too many compound valve inputs.");
            string[] inputs = names?.Kind == ZrdKind.Array
                ? names.Children.Where(n => n.Kind == ZrdKind.String).Select(n => n.Text).ToArray() : [];
            if (inputs.Length > MaximumValves) throw new InvalidDataException("Too many compound valve inputs.");
            List<Mw3ValveAction> actions = [];
            foreach (string kind in ActionOrder)
            {
                var args = Find(value, kind);
                string selected = kind;
                if (kind is "sound" or "animation" && Find(value, kind + "_once") is { } once) { args = once; selected += "_once"; }
                if (args == null || args.Kind != ZrdKind.Array) continue;
                if (++reactions > MaximumReactions) throw new InvalidDataException("Too many valve reactions for preview.");
                actions.Add(new(selected, args));
            }
            var initial = Find(value, "value");
            definitions.Add(new(key.Id, key.Text, initial?.Children.FirstOrDefault() is { Kind: ZrdKind.Int or ZrdKind.Float } n ? unchecked((int)n.Bits) : null,
                condition, inputs, actions));
        }
        return new(definitions);
    }

    // zReader's native tag search descends through ordered lists; it is not a dictionary.
    public static bool HasTag(ZrdNode node, string key) => node.Kind == ZrdKind.Array &&
        node.Children.Any(c => c.Kind == ZrdKind.String && c.Text == key || c.Kind == ZrdKind.Array && HasTag(c, key));
    public static ZrdNode? Find(ZrdNode node, string key) => TryFind(node, key, out var value) ? value : null;
    private static bool TryFind(ZrdNode node, string key, out ZrdNode? value)
    {
        value = null;
        if (node.Kind != ZrdKind.Array) return false;
        var c = node.Children;
        for (int i = 0; i < c.Count; i++)
        {
            if (c[i].Kind == ZrdKind.String && c[i].Text == key)
            { value = i + 1 < c.Count ? c[i + 1] : null; return true; }
            if (c[i].Kind == ZrdKind.Array && TryFind(c[i], key, out value)) return true;
        }
        return false;
    }
}
