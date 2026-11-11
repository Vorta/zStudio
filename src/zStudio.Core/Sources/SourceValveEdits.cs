using System.Text.Json;
using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

/// <summary>Lossless occurrence-based valve edits over the shared text syntax.</summary>
public static class SourceValveEdits
{
    public sealed record Edit(byte[] Content, string Label);
    public static Edit Attach(byte[] bytes, int nodeIndex, string attribute, string parameters, CancellationToken token = default)
    {
        if (attribute is not ("valve" or "valveunion" or "valve_assign")) throw new InvalidDataException("Choose valve, valveunion or valve_assign.");
        var syntax = ZrdTextSyntax.Parse(bytes, token);
        if (!MissionAiValves.IsNetwork(syntax.Root)) throw new InvalidDataException("Valve attributes require a native version-106 network.");
        var network = MissionAiNetworks.Decode("edit", "", 0, "net_01.zrd", syntax.Root, token);
        var node = network.Nodes.SingleOrDefault(n => n.Index == nodeIndex);
        if (node?.Source == null || !network.ResolvedIndices.Contains(nodeIndex)) throw new InvalidDataException("Choose a uniquely resolved spatial node.");
        if (Mw3ValveProgram.Find(node.Source, attribute) != null) throw new InvalidDataException("This node already stores that attribute. Edit its occurrence instead.");
        var key = ZrdNode.Create(ZrdKind.String, JsonSerializer.Serialize(attribute)); var value = ParseParameters(parameters, token);
        var record = new AiValveRecord(key.Id, node.Source.Id, node.Source.Children.Count, key, value, "node", nodeIndex);
        if (MissionAiValves.Problem(record, token) is { } problem) throw new InvalidDataException(problem);
        var updated = Replace(syntax.Root, node.Source.Id, node.Source with { Children = [.. node.Source.Children, key, value] });
        var (text, lossless) = syntax.Rewrite(updated, token);
        if (!lossless) throw new InvalidDataException("Adding the attribute cannot preserve this source layout.");
        return new(ZrdTextSyntax.Encode(text), "Add AI node valve attribute");
    }
    public static Edit Plan(string member, byte[] bytes, string operation, int occurrence, string? name, string? parameters,
        CancellationToken token = default)
    {
        if (!ZrdText.LooksLikeText(bytes)) throw new InvalidDataException("Valve editing requires a text source.");
        var syntax = ZrdTextSyntax.Parse(bytes, token);
        var records = MissionAiValves.Records(member, syntax.Root, token).ToArray();
        bool definition = member.Equals("valves.zrd", StringComparison.OrdinalIgnoreCase);
        var body = MissionAiValves.Fields(syntax.Root);
        var children = body.Children.ToList();
        ZrdNode updated;
        if (operation == "create")
        {
            if (!definition) throw new InvalidDataException("Create definitions in valves.zrd; edit existing associations in their resource.");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new InvalidDataException("Enter a valve name of at most 256 characters.");
            children.Add(ZrdNode.Create(ZrdKind.String, JsonSerializer.Serialize(name)));
            children.Add(ParseParameters(parameters, token));
            updated = Replace(syntax.Root, body.Id, body with { Children = children });
        }
        else
        {
            if (occurrence < 0 || occurrence >= records.Length) throw new InvalidDataException("The valve occurrence is no longer available.");
            var record = records[occurrence];
            if (operation == "delete")
            {
                if (!definition) throw new InvalidDataException("Remove an association by editing its owning record.");
                children.RemoveRange(record.ChildIndex, 2);
                updated = Replace(syntax.Root, body.Id, body with { Children = children });
            }
            else if (operation == "update")
            {
                var value = ParseParameters(parameters, token);
                updated = Replace(syntax.Root, record.Value.Id, Merge(record.Value, value));
                if (definition && name != null)
                {
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new InvalidDataException("Enter a valve name of at most 256 characters.");
                    updated = Replace(updated, record.NameNode.Id, record.NameNode with { Text = name });
                }
            }
            else throw new InvalidDataException("Use create, update or delete.");
        }
        if (definition)
        {
            ValidateEdit(syntax.Root, updated, token);
        }
        else
        {
            var id = records[occurrence].Id;
            var result = MissionAiValves.Records(member, updated, token).FirstOrDefault(r => r.Id == id)
                ?? throw new InvalidDataException("The edit removes the association's recognized layout.");
            if (MissionAiValves.Problem(result, token) is { } problem) throw new InvalidDataException(problem);
        }
        var (text, lossless) = syntax.Rewrite(updated, token);
        if (!lossless) throw new InvalidDataException("This edit cannot preserve the surrounding source layout.");
        return new(ZrdTextSyntax.Encode(text), operation + " valve occurrence");
    }
    /// <summary>One reaction of a valve to change: an existing one by its record's occurrence and stored tag (a null spec removes it), or a new one.</summary>
    public sealed record ReactionChange(int? Occurrence, string? Kind, Mw3ValveReactionSpec? Spec);
    /// <summary>
    /// What a valve's card changes: its starting value, whether it is computed (a condition tag, "" for a plain switch) with
    /// its inputs, and its reactions. Null leaves a part as stored.
    /// </summary>
    public sealed record ValveChanges(string? Initial = null, string? Condition = null, IReadOnlyList<string>? Inputs = null, IReadOnlyList<ReactionChange>? Reactions = null)
    {
        public bool IsEmpty => Initial == null && Condition == null && Inputs == null && Reactions is null or { Count: 0 };
    }

    /// <summary>
    /// Changes one valve by name in valves.zrd, keeping every other token: a stored value is replaced in place, a new
    /// reaction becomes its own record after the valve's last one (as the shipped files write them), a removed reaction
    /// takes its record along once nothing is left in it. A valve without a record gets one. <paramref name="bytes"/> is
    /// null when the mission has no valves.zrd yet.
    /// </summary>
    public static Edit PlanValve(byte[]? bytes, string name, ValveChanges changes, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new InvalidDataException("Enter a valve name of at most 256 characters.");
        bytes ??= System.Text.Encoding.Latin1.GetBytes("()\r\n");
        if (!ZrdText.LooksLikeText(bytes)) throw new InvalidDataException("valves.zrd is stored as binary zReader data, so its valves can be edited only in a text source.");
        var syntax = ZrdTextSyntax.Parse(bytes, token);
        var body = MissionAiValves.Fields(syntax.Root);
        if (body.Kind != ZrdKind.Array) throw new InvalidDataException("valves.zrd does not hold a list of valves.");
        var records = MissionAiValves.Records("valves.zrd", syntax.Root, token).ToList();
        // The records of this valve, by their place in the file; edits replace them here and the body is rebuilt at the end.
        List<ZrdNode> children = [.. body.Children];
        List<(int Occurrence, int Key)> mine = [.. records.Select((r, i) => (i, r.ChildIndex)).Where(r => records[r.i].Name == name)];
        bool created = mine.Count == 0;
        if (created)
        {
            if (changes.IsEmpty) changes = changes with { Initial = "0" };
            children.Add(ZrdNode.Create(ZrdKind.String, JsonSerializer.Serialize(name))); children.Add(ZrdNode.Create(ZrdKind.Array));
            mine.Add((-1, children.Count - 2));
        }
        else if (changes.IsEmpty) throw new InvalidOperationException("Nothing changed.");
        foreach (var (_, key) in mine)
            if (Mw3ValveProgram.SourceProblem(children[key + 1]) is { } problem && Touches(key))
                throw new InvalidDataException($"A record of {MissionAiValves.Short(name, 64)} is not as the game reads it ({problem.TrimEnd('.')}). Correct it in a text editor first.");
        bool Touches(int key) => changes.Initial != null || changes.Condition != null || changes.Inputs != null
            || changes.Reactions?.Any(r => r.Occurrence is int occurrence && mine.Any(m => m.Occurrence == occurrence && m.Key == key)) == true;
        ZrdNode Value(int key) => children[key + 1];
        // A tag the game reads at the record's top level: its index, or -1 (nested lists are left to a text editor).
        static int Tag(ZrdNode record, string tag)
        {
            for (int i = 0; i < record.Children.Count; i++) if (record.Children[i].Kind == ZrdKind.String && record.Children[i].Text == tag) return i;
            return -1;
        }
        static ZrdNode With(ZrdNode record, IEnumerable<ZrdNode> items) => record with { Children = [.. items] };
        static ZrdNode Word(string tag) => ZrdNode.Create(ZrdKind.String, JsonSerializer.Serialize(tag));

        // What the draft asks for, checked at the end against what the game builds from the edited file.
        int? wantedStart = null; bool wantedKind = false; string? wantedCondition = null; string[] wantedInputs = [];
        List<(Guid Record, string Kind, ZrdNode? Arguments)> wantedReactions = [];
        if (changes.Initial is { } initial)
        {
            if (!int.TryParse(initial.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int start))
                throw new InvalidDataException("Enter the starting value as a whole number.");
            wantedStart = start;
            var number = ZrdNode.Create(ZrdKind.Int, start.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // The game applies every record's value in order: the last one stored decides.
            int holder = mine.Select(m => m.Key).LastOrDefault(k => Tag(Value(k), "value") is >= 0 and var at && at + 1 < Value(k).Children.Count && Value(k).Children[at + 1].Kind == ZrdKind.Array, -1);
            if (holder >= 0)
            {
                var record = Value(holder); int at = Tag(record, "value"); var list = record.Children[at + 1];
                var word = list.Children.Count == 1 ? InPlace(list.Children[0], number) : number;
                children[holder + 1] = With(record, record.Children.Select((n, i) => i == at + 1 ? list with { Children = [word] } : n));
            }
            else
            {
                int first = mine[0].Key; var record = Value(first);
                children[first + 1] = With(record, [Word("value"), ZrdNode.Create(ZrdKind.Array) with { Children = [number] }, .. record.Children]);
            }
        }
        if (changes.Condition != null || changes.Inputs != null)
        {
            // The game keeps the first computed definition and drops a second one's whole record: changing or removing the first
            // would silently promote the other, so the two are sorted out in the file first.
            if (mine.Count(m => Mw3ValveProgram.Conditions.Any(c => Mw3ValveProgram.HasTag(Value(m.Key), c))) > 1)
                throw new InvalidDataException($"{MissionAiValves.Short(name, 64)} has two computed definitions; the game keeps the first and drops the second record. Remove one of them in a text editor before changing its kind or inputs.");
            int holder = mine.Select(m => m.Key).FirstOrDefault(k => Mw3ValveProgram.Conditions.Any(c => Tag(Value(k), c) >= 0), -1);
            string? stored = holder < 0 ? null : Mw3ValveProgram.Conditions.First(c => Tag(Value(holder), c) >= 0);
            string? condition = changes.Condition == null ? stored : changes.Condition.Length == 0 ? null : changes.Condition;
            if (condition != null && !Mw3ValveProgram.Conditions.Contains(condition)) throw new InvalidDataException("Choose any_nonzero, any_zero, all_nonzero or all_zero, or a plain switch.");
            wantedKind = true; wantedCondition = condition;
            if (condition == null)
            {
                if (holder >= 0)
                {
                    var record = Value(holder); int at = Tag(record, stored!), names = Tag(record, "namelist");
                    HashSet<int> removed = [at];
                    // The shipped form is "all_nonzero namelist ( a b )": the tag's own value is the word namelist.
                    if (at + 1 < record.Children.Count && record.Children[at + 1].Kind == ZrdKind.Array) removed.Add(at + 1);
                    if (names >= 0) { removed.Add(names); if (names + 1 < record.Children.Count && record.Children[names + 1].Kind == ZrdKind.Array) removed.Add(names + 1); }
                    children[holder + 1] = With(record, record.Children.Where((_, i) => !removed.Contains(i)));
                }
            }
            else
            {
                string[] inputs = [.. (changes.Inputs ?? (holder >= 0 && Mw3ValveProgram.Find(Value(holder), "namelist") is { } kept ? [.. kept.Children.Where(n => n.Kind == ZrdKind.String).Select(n => n.Text)] : [])).Select(i => i.Trim()).Where(i => i.Length > 0)];
                if (inputs.Length == 0) throw new InvalidDataException("A computed valve needs at least one input valve.");
                if (inputs.Length > 256 || inputs.Any(i => i.Length > 256)) throw new InvalidDataException("A computed valve takes at most 256 inputs of at most 256 characters.");
                if (inputs.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("A computed valve cannot be its own input.");
                wantedInputs = inputs;
                if (holder >= 0)
                {
                    var record = Value(holder); int at = Tag(record, stored!), names = Tag(record, "namelist");
                    if (names < 0 || names + 1 >= record.Children.Count || record.Children[names + 1].Kind != ZrdKind.Array)
                        throw new InvalidDataException("This computed valve stores its inputs in a form the card does not edit. Correct it in a text editor.");
                    var updated = Names(record.Children[names + 1], inputs);
                    children[holder + 1] = With(record, record.Children.Select((n, i) => i == at && condition != stored ? n with { Text = condition } : i == names + 1 ? updated : n));
                }
                else
                {
                    int first = mine[0].Key; var record = Value(first); int names = Tag(record, "namelist");
                    if (names >= 0 && names + 1 < record.Children.Count && record.Children[names + 1].Kind == ZrdKind.Array)
                    {
                        // An input list the record still stores (without a condition the game ignores it) is the one the game
                        // will read: it becomes the valve's, with the condition before it as the shipped files write it.
                        var updated = Names(record.Children[names + 1], inputs);
                        children[first + 1] = With(record, record.Children.SelectMany((n, i) => i == names ? [Word(condition), n] : i == names + 1 ? [updated] : new[] { n }));
                    }
                    else children[first + 1] = With(record, [.. record.Children, Word(condition), Word("namelist"), ZrdNode.Create(ZrdKind.Array) with { Children = [.. inputs.Select(Word)] }]);
                }
            }
        }
        // Each existing reaction is found in its record as stored, removals apply first, and a record is checked as the draft
        // leaves it: the order of the changes never matters (removing one reaction and giving another its kind is one draft).
        List<ZrdNode> added = [];
        List<(int Key, Guid Tag, ReactionChange Change)> existing = [];
        foreach (var change in changes.Reactions ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (change.Occurrence is not int occurrence)
            {
                var spec = change.Spec ?? throw new InvalidDataException("A new reaction needs its fields.");
                var owner = Word(name); var fresh = Mw3ValveMap.Arguments(spec);
                added.Add(owner); added.Add(ZrdNode.Create(ZrdKind.Array) with { Children = [Word(spec.Kind), fresh] });
                wantedReactions.Add((owner.Id, spec.Kind, fresh));
                continue;
            }
            int key = -1; foreach (var m in mine) if (m.Occurrence == occurrence) { key = m.Key; break; }
            if (key < 0 || change.Kind == null) throw new InvalidDataException("The reaction is no longer in this valve. Read the valve again.");
            var record = Value(key); int at = Tag(record, change.Kind);
            if (at < 0 || at + 1 >= record.Children.Count || record.Children[at + 1].Kind != ZrdKind.Array) throw new InvalidDataException("The reaction is no longer in this valve. Read the valve again.");
            if (existing.Any(e => e.Tag == record.Children[at].Id)) throw new InvalidDataException("One reaction is changed twice.");
            // A record that stores several reactions of one kind has only one the game uses (a once-only sound or animation
            // before its plain one, else the first). Its fields can change; giving it another kind or removing it would
            // silently make a stored, unused reaction the active one.
            string family = Family(change.Kind);
            if (Kinds(body.Children[key + 1]).GetValueOrDefault(family) > 1 && (change.Spec == null || change.Spec.Kind != change.Kind))
                throw new InvalidDataException($"That record stores more than one {family} reaction and the game uses only one of them. Remove the unused one in a text editor before changing this reaction's kind or removing it.");
            existing.Add((key, record.Children[at].Id, change));
        }
        foreach (var (key, tag, change) in existing.OrderBy(e => e.Change.Spec == null ? 0 : 1))
        {
            var record = Value(key); int at = -1;
            for (int i = 0; i < record.Children.Count; i++) if (record.Children[i].Id == tag) { at = i; break; }
            if (change.Spec == null)
            {
                children[key + 1] = With(record, record.Children.Where((_, i) => i != at && i != at + 1));
                wantedReactions.Add((children[key].Id, change.Kind!, null));
                continue;
            }
            var arguments = Mw3ValveMap.Arguments(change.Spec, change.Kind!, record.Children[at + 1]);
            children[key + 1] = With(record, record.Children.Select((n, i) => i == at && change.Spec.Kind != change.Kind ? n with { Text = change.Spec.Kind } : i == at + 1 ? arguments : n));
            wantedReactions.Add((children[key].Id, change.Spec.Kind, arguments));
        }
        // A record holds one reaction of a kind (a once-only sound or animation takes the plain one's place): the draft may
        // not add a second, whatever a record stored before.
        static string Family(string kind) => kind is "sound_once" ? "sound" : kind is "animation_once" ? "animation" : kind;
        static Dictionary<string, int> Kinds(ZrdNode record) => record.Children.Where(n => n.Kind == ZrdKind.String)
            .Select(n => Family(n.Text)).Where(t => Mw3ValveProgram.ActionOrder.Contains(t))
            .GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (int key in existing.Select(e => e.Key).Distinct())
        {
            var before = Kinds(body.Children[key + 1]);
            foreach (var (kind, count) in Kinds(Value(key)))
                if (count > 1 && count > before.GetValueOrDefault(kind)) throw new InvalidDataException($"That record would hold two {kind} reactions; a record holds one of a kind.");
        }
        // New reactions follow the valve's last record; a record emptied by removals leaves the file (never the last one of a new valve).
        int after = mine.Max(m => m.Key) + 2;
        HashSet<int> emptied = [.. mine.Where(m => m.Occurrence >= 0 && Value(m.Key).Children.Count == 0 && body.Children[m.Key + 1].Children.Count > 0).Select(m => m.Key)];
        List<ZrdNode> rebuilt = [];
        for (int i = 0; i < children.Count; i++)
        {
            if (i == after) rebuilt.AddRange(added);
            if (emptied.Contains(i) || emptied.Contains(i - 1)) continue;
            rebuilt.Add(children[i]);
        }
        if (after >= children.Count) rebuilt.AddRange(added);
        var updatedRoot = Replace(syntax.Root, body.Id, body with { Children = rebuilt });
        if (ZrdTextSyntax.StructurallyEqual(updatedRoot, syntax.Root)) throw new InvalidOperationException("Nothing changed.");
        ValidateEdit(syntax.Root, updatedRoot, token);
        // The edit is accepted only when the game would build what the draft shows: the game finds a tag by searching the
        // record's lists in order, so a record written in an unusual form can make it read another value first.
        {
            var built = Mw3ValveProgram.Read(updatedRoot, token, skipUnreadable: true).Effective().Definitions;
            var definitions = built.Where(d => d.Name == name).ToArray();
            var computed = definitions.FirstOrDefault(d => d.Condition != null && d.Inputs.Count > 0);
            bool asShown = !wantedKind || (wantedCondition == null ? computed == null : computed != null && computed.Condition == wantedCondition && computed.Inputs.SequenceEqual(wantedInputs));
            asShown &= wantedStart == null || (definitions.LastOrDefault(d => d.Initial != null)?.Initial ?? 0) == wantedStart;
            // One draft can remove a reaction and give another its kind: what a record ends with per kind is what counts.
            Dictionary<(Guid Record, string Family), (string Kind, ZrdNode Arguments)?> expected = [];
            foreach (var (owner, kind, arguments) in wantedReactions.Where(w => w.Arguments == null)) expected[(owner, Family(kind))] = null;
            foreach (var (owner, kind, arguments) in wantedReactions.Where(w => w.Arguments != null)) expected[(owner, Family(kind))] = (kind, arguments!);
            foreach (var (key, wanted) in expected)
            {
                var action = built.FirstOrDefault(d => d.Record == key.Record)?.Actions.FirstOrDefault(a => Family(a.Kind) == key.Family);
                asShown &= wanted is not { } set ? action == null : action != null && action.Kind == set.Kind && ZrdTextSyntax.StructurallyEqual(action.Arguments, set.Arguments);
            }
            if (!asShown) throw new InvalidDataException($"valves.zrd stores {MissionAiValves.Short(name, 64)} in a form where this change would not take effect as shown (the game would read another value first). Edit it in a text editor.");
        }
        var (text, lossless) = syntax.Rewrite(updatedRoot, token);
        if (!lossless) throw new InvalidDataException("This edit cannot preserve the surrounding source layout.");
        return new(ZrdTextSyntax.Encode(text), created ? $"Add valve {MissionAiValves.Short(name, 48)}" : $"Edit valve {MissionAiValves.Short(name, 48)}");
    }

    /// <summary>Removes every valves.zrd record of a valve. What other files say about it stays (the game still makes it, at zero).</summary>
    public static Edit PlanRemove(byte[] bytes, string name, CancellationToken token = default)
    {
        if (!ZrdText.LooksLikeText(bytes)) throw new InvalidDataException("valves.zrd is stored as binary zReader data, so its valves can be edited only in a text source.");
        var syntax = ZrdTextSyntax.Parse(bytes, token); var body = MissionAiValves.Fields(syntax.Root);
        HashSet<int> keys = [.. MissionAiValves.Records("valves.zrd", syntax.Root, token).Where(r => r.Name == name).Select(r => r.ChildIndex)];
        if (keys.Count == 0) throw new InvalidDataException("valves.zrd has no record of this valve.");
        var updated = Replace(syntax.Root, body.Id, body with { Children = [.. body.Children.Where((_, i) => !keys.Contains(i) && !keys.Contains(i - 1))] });
        var (text, lossless) = syntax.Rewrite(updated, token);
        if (!lossless) throw new InvalidDataException("This edit cannot preserve the surrounding source layout.");
        return new(ZrdTextSyntax.Encode(text), $"Delete valve {MissionAiValves.Short(name, 48)}");
    }

    /// <summary>
    /// Renames a valve wherever one resource names it: its valves.zrd records, other valves' inputs and timers, vehicle
    /// triggers, health thresholds, route gates and assignments, deploy points, and this mission's objectives. Null when the
    /// file does not name it.
    /// </summary>
    public static Edit? PlanRename(string member, byte[] bytes, string name, string renamed, string? mission, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(renamed) || renamed.Length > 256 || renamed != renamed.Trim()) throw new InvalidDataException("Enter a valve name of at most 256 characters, without spaces around it.");
        if (!ZrdText.LooksLikeText(bytes)) return null;
        var syntax = ZrdTextSyntax.Parse(bytes, token);
        HashSet<Guid> operands = [];
        bool objectives = member.Equals("objectives.zrd", StringComparison.OrdinalIgnoreCase);
        // objectives.zrd holds every mission of the map: only this mission's block is this mission's valve.
        ZrdNode? scope = syntax.Root;
        if (objectives)
        {
            var fields = MissionAiValves.Fields(syntax.Root).Children; scope = null;
            for (int i = 0; mission != null && i + 1 < fields.Count; i += 2)
                if (fields[i].Kind == ZrdKind.String && fields[i].Text.Equals(mission, StringComparison.OrdinalIgnoreCase)) { scope = fields[i + 1]; break; }
            if (scope == null) return null;
        }
        foreach (var record in MissionAiValves.Records(member, syntax.Root, token))
        {
            if (objectives && scope!.Find(record.Id) == null) continue;
            foreach (var reference in MissionAiValves.References(record, token)) if (reference.Name == name) operands.Add(reference.Operand);
        }
        if (MissionAiNetworks.IsCandidate(member, mw3: true) && MissionAiValves.IsNetwork(syntax.Root))
            foreach (var node in MissionAiValves.Fields(syntax.Root).Children.Where(n => n.Kind == ZrdKind.Array))
                for (int j = 3; j + 1 < node.Children.Count; j += 2)
                    if (node.Children[j] is { Kind: ZrdKind.String, Text: "deploy" or "mcbwait" } key && node.Children[j + 1].Kind == ZrdKind.Array)
                        foreach (int at in key.Text == "deploy" ? new[] { 0, 2 } : [0])
                            if (node.Children[j + 1].Children.ElementAtOrDefault(at) is { Kind: ZrdKind.String } named && named.Text == name) operands.Add(named.Id);
        if (operands.Count == 0) return null;
        var updated = Rename(syntax.Root);
        if (member.Equals("valves.zrd", StringComparison.OrdinalIgnoreCase)) ValidateEdit(syntax.Root, updated, token);
        var (text, lossless) = syntax.Rewrite(updated, token);
        if (!lossless) throw new InvalidDataException($"Renaming cannot preserve the layout of {member}.");
        return new(ZrdTextSyntax.Encode(text), $"Rename valve {MissionAiValves.Short(name, 48)}");
        ZrdNode Rename(ZrdNode node) => operands.Contains(node.Id) ? node with { Text = renamed }
            : node.Kind != ZrdKind.Array ? node : node with { Children = [.. node.Children.Select(Rename)] };
    }

    /// <summary>
    /// Checks valves.zrd as an edit leaves it. A record the game read before must still read as written, and the definitions
    /// the game builds (unreadable records left out, a second computed definition dropped with its record) must have no
    /// immediate reaction cycle and no cyclic condition. What the file already had wrong is not this edit's: the same
    /// failure before the edit does not refuse it.
    /// </summary>
    private static void ValidateEdit(ZrdNode before, ZrdNode after, CancellationToken token)
    {
        HashSet<Guid> unreadable = [.. MissionAiValves.Records("valves.zrd", before, token).Where(r => Mw3ValveProgram.SourceProblem(r.Value) != null).Select(r => r.Id)];
        foreach (var record in MissionAiValves.Records("valves.zrd", after, token))
            if (!unreadable.Contains(record.Id) && Mw3ValveProgram.SourceProblem(record.Value) is { } problem)
                throw new InvalidDataException($"Valve {MissionAiValves.Short(record.Name, 64)}: {problem}");
        static string? Failure(ZrdNode root, CancellationToken token)
        {
            try { Mw3ValveProgram.Read(root, token, skipUnreadable: true).Effective().ValidateForEditing(); return null; }
            catch (InvalidDataException ex) { return ex.Message; }
        }
        if (Failure(after, token) is { } failure && failure != Failure(before, token)) throw new InvalidDataException(failure);
    }
    /// <summary>A number that replaces a stored number keeps its node's identity, so its token changes in place whatever its kind.</summary>
    /// <summary>
    /// An input list with other names; every row keeps its own token, so only the words that change are rewritten and the
    /// notes around them stay. As many names as before: row by row (a row given another name is that row). More or fewer:
    /// the longest run of names that stay, in order, keeps its rows; between them a new name takes the place of one that
    /// goes, the rest are added or removed.
    /// </summary>
    private static ZrdNode Names(ZrdNode list, IReadOnlyList<string> inputs)
    {
        ZrdNode[] old = [.. list.Children.Where(n => n.Kind == ZrdKind.String)];
        static ZrdNode Word(string name) => ZrdNode.Create(ZrdKind.String, JsonSerializer.Serialize(name));
        static ZrdNode Row(ZrdNode stored, string name) => stored.Text == name ? stored : Word(name) with { Id = stored.Id };
        if (old.Length == inputs.Count) return list with { Children = [.. inputs.Select((name, at) => Row(old[at], name))] };
        // Longest common subsequence of the stored and the wanted names (at most 256 of each).
        var run = new int[old.Length + 1, inputs.Count + 1];
        for (int i = old.Length - 1; i >= 0; i--)
            for (int j = inputs.Count - 1; j >= 0; j--)
                run[i, j] = old[i].Text == inputs[j] ? run[i + 1, j + 1] + 1 : Math.Max(run[i + 1, j], run[i, j + 1]);
        List<ZrdNode> rows = [];
        int a = 0, b = 0;
        while (a < old.Length || b < inputs.Count)
        {
            if (a < old.Length && b < inputs.Count && old[a].Text == inputs[b]) { rows.Add(old[a]); a++; b++; continue; }
            // Up to the next name that stays on both sides: what goes and what comes pair up in order.
            int goes = a, comes = b;
            while (a < old.Length && b < inputs.Count && old[a].Text != inputs[b]) { if (run[a + 1, b] >= run[a, b + 1]) a++; else b++; }
            if (a == old.Length) b = inputs.Count; else if (b == inputs.Count) a = old.Length;
            for (int k = 0; comes + k < b; k++) rows.Add(goes + k < a ? Row(old[goes + k], inputs[comes + k]) : Word(inputs[comes + k]));
        }
        return list with { Children = [.. rows] };
    }

    private static ZrdNode InPlace(ZrdNode stored, ZrdNode value) => stored.Kind == ZrdKind.Array || value.Kind == ZrdKind.Array ? value
        : ZrdTextSyntax.StructurallyEqual(stored, value) ? stored : value with { Id = stored.Id };

    private static ZrdNode ParseParameters(string? text, CancellationToken token)
    {
        if (text == null || text.Length > 65_536) throw new InvalidDataException("Enter at most 65,536 characters of valve parameters.");
        var parsed = ZrdText.Parse(text, token);
        return parsed.Children is [{ Kind: ZrdKind.Array } array] ? array : parsed;
    }
    private static ZrdNode Merge(ZrdNode before, ZrdNode after)
    {
        if (ZrdTextSyntax.StructurallyEqual(before, after)) return before;
        // A scalar that changes kind still replaces its token in place (its comments and place stay).
        if (before.Kind != after.Kind) return before.Kind != ZrdKind.Array && after.Kind != ZrdKind.Array ? after with { Id = before.Id } : after;
        if (before.Kind != ZrdKind.Array) return after with { Id = before.Id };
        return before with { Children = after.Children.Select((n, i) => i < before.Children.Count ? Merge(before.Children[i], n) : n).ToArray() };
    }
    private static ZrdNode Replace(ZrdNode node, Guid id, ZrdNode replacement) => node.Id == id ? replacement :
        node.Kind != ZrdKind.Array ? node : node with { Children = node.Children.Select(c => Replace(c, id, replacement)).ToArray() };
}
