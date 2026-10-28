using System.Globalization;

namespace zStudio.Core.Animation;

/// <summary>Rows, condition and loop blocks of one sequence, and a binding's activation, in the animation vocabulary.</summary>
internal static class AnimationGraphRows
{
    public static AnimationGraph.SequenceLayout Layout(AnimationGraphIndex index, AnimationEntry entry, AnimationSequence sequence, int order, bool collapsed, CancellationToken token)
    {
        var events = sequence.Events;
        bool cleanup = sequence == entry.Primary;
        var source = index.Provenance?.FindSequence(sequence.Id);
        var (blocks, membership, diagnostics) = Blocks(index, entry, sequence);
        List<AnimationGraphRow> single = new(events.Count);
        for (int i = 0; i < events.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            single.Add(Row(index, entry, sequence, i, membership[i], blocks));
        }
        // Consecutive events of one kind whose operands differ only by a name form one row.
        List<AnimationGraphRow> rows = [];
        Dictionary<Guid, AnimationGraphRow> byEvent = [];
        for (int i = 0; i < single.Count;)
        {
            string? key = GroupKey(index, entry, events[i], single[i]);
            int end = i + 1;
            if (key != null)
                while (end < single.Count && end - i < AnimationGraph.MaximumMembers && GroupKey(index, entry, events[end], single[end]) == key) end++;
            var row = single[i];
            if (end - i > 1)
            {
                var names = Enumerable.Range(i, end - i).Select(j => PrimaryName(entry, events[j])!).ToArray();
                row = row with
                {
                    Label = Template(entry, events[i], single[i])!.Replace("\u0000", AnimationPhrases.Range(names), StringComparison.Ordinal) + $" ({names.Length})",
                    Members = Enumerable.Range(i, end - i).Select(j => events[j].Id).ToArray(), Target = null, TargetEntry = null,
                };
            }
            rows.Add(row);
            for (int j = i; j < end; j++) byEvent[events[j].Id] = row;
            i = end;
        }
        List<string> states = [];
        if (cleanup)
        {
            bool opening = index.StartsNewGame(entry.Index);
            bool load = entry.Bytes.Length >= 152 && (entry.U32(148) & 0x20) != 0;
            states.Add(opening ? "Cleanup · ends the opening" : "Cleanup · runs on stop or reset" + (load ? " and at mission load" : ""));
        }
        else if (sequence.ResetMode == 3) states.Add("Waits for release");
        if (source?.IgnoredStartTime == true) states.Add("START_TIME ignored");
        if (!sequence.IsEditable) diagnostics.Add("Part of this sequence could not be read; its remaining bytes are kept as they are.");
        var info = new AnimationGraphSequence(entry.Index, sequence.Id, order, cleanup ? "Cleanup" : sequence.Name.Length == 0 ? "(unnamed)" : AnimationPhrases.Safe(sequence.Name),
            cleanup, !cleanup && sequence.ResetMode == 3, events.Count, rows.Count)
        {
            StateLine = states.Count == 0 ? null : string.Join(" · ", states), IgnoredStartTime = source?.IgnoredStartTime == true,
            CollapsedByDefault = collapsed, Source = source, Diagnostics = diagnostics.AsReadOnly(),
            Summary = single.GroupBy(r => r.Family).OrderBy(g => g.Key).Select(g => new AnimationGraphFamilyCount(g.Key, g.Count())).ToArray(),
        };
        return new(info, rows.AsReadOnly(), single.AsReadOnly(), blocks.AsReadOnly(), rows.ToDictionary(r => r.Id, StringComparer.Ordinal), byEvent);
    }

    private static AnimationGraphRow Row(AnimationGraphIndex index, AnimationEntry entry, AnimationSequence sequence, int i, (int Block, int Branch) member, List<AnimationGraphBlock> blocks)
    {
        var ev = sequence.Events[i];
        byte type = ev.Bytes.Length > 0 ? ev.Type : (byte)0;
        string keyword = index.Provenance?.FindEvent(ev.Id)?.Kind is { Length: > 0 } written ? AnimationPhrases.Safe(written) : AnimationPhrases.Keyword(type);
        var role = type switch { 31 or 33 => AnimationGraphRowRole.Question, 32 => AnimationGraphRowRole.Else, 34 => AnimationGraphRowRole.EndIf, 30 => AnimationGraphRowRole.Loop, _ => AnimationGraphRowRole.Event };
        bool complete = ev.Bytes.Length >= 12 && ev.Spec is { } spec && ev.Bytes.Length >= spec.Size;
        var source = index.Provenance?.FindEvent(ev.Id);
        string label = AnimationPhrases.Event(entry, ev, source?.Script is { Length: > 0 } script ? System.IO.Path.GetFileName(script) : null);
        string? block = member.Block >= 0 ? blocks[member.Block].Id : null;
        int? branch = member.Block >= 0 && member.Branch >= 0 ? member.Branch : null;
        // A question or branch row reads from its block.
        var owner = blocks.FirstOrDefault(b => b.Branches.Any(br => br.Marker == ev.Id));
        if (owner != null)
        {
            var marked = owner.Branches.First(br => br.Marker == ev.Id);
            block = owner.Id; branch = marked.Index;
            label = marked.Condition?.Question ?? marked.Label;
        }
        else if (type == 34 && blocks.FirstOrDefault(b => b.EndIf == ev.Id) is { } closed) { block = closed.Id; branch = null; label = "end if"; }
        else if (type == 30 && blocks.FirstOrDefault(b => b.Loop?.Event == ev.Id) is { Loop: { } loop } looped) { block = looped.Id; label = "Loop " + LoopLabel(loop); }
        byte clock = ev.Bytes.Length >= 12 ? ev.StartMode : (byte)1; float threshold = ev.Bytes.Length >= 12 ? ev.Threshold : 0;
        bool timed = !(clock == 1 && threshold == 0);
        string? note = type == 33 && timed ? "ignored after a false IF" : type is 32 && timed ? "waits, then skips" : timed ? AnimationPhrases.ThresholdWords(clock, threshold) : null;
        string? target = null; int? targetEntry = null; string? diagnostic = complete ? null : "The event's record is incomplete or of an unknown kind.";
        if (complete && index.Relate(ev) is { } relation) { target = relation.Name; targetEntry = relation.Target; }
        else if (complete && type is 22 or 23) target = ev.Text(12);
        AnimationCallbackMeaning? callback = complete && type == 35 ? AnimationPhrases.Callback(ev.I32(12)) : null;
        if (complete && type is 22 or 23 && sequence == entry.Primary) diagnostic = "No effect: a cleanup's sequence events name runtime sequences, which do not run during it.";
        return new(AnimationGraph.RowId(entry.Index, ev.Id), entry.Index, sequence.Id, ev.Id, i, type, keyword, AnimationPhrases.Family(type), role, label)
        {
            Clock = clock, Threshold = threshold, ThresholdBadge = AnimationPhrases.ThresholdBadge(clock, threshold), ThresholdNote = note,
            Block = block, Branch = branch, Members = [ev.Id], Target = target, TargetEntry = targetEntry, Callback = callback, Source = source, Diagnostic = diagnostic,
        };
    }

    private static string LoopLabel(AnimationGraphLoop loop) => loop.Mode switch { "count" => $"×{loop.Count}", "time" => "for " + AnimationSeconds.Short(loop.Seconds ?? 0), _ => "forever" };

    /// <summary>The name an event's phrase is about, for grouping rows that differ only by it.</summary>
    private static string? PrimaryName(AnimationEntry entry, AnimationEvent ev)
    {
        if (ev.Spec is not { } spec || ev.Bytes.Length < spec.Size) return null;
        return ev.Type switch
        {
            1 => AnimationPhrases.TableName(entry, 4, ev.I16(12)), 2 or 22 or 23 or 24 or 25 or 26 or 27 => ev.Text(12), 3 => AnimationPhrases.TableName(entry, 5, ev.I16(12)),
            6 => AnimationPhrases.NodeName(entry, ev.I16(16)), 19 => ev.Text(16), _ => null,
        };
    }
    /// <summary>The row's phrase with its name taken out ("Call \0 and wait"), or null when the row never groups.</summary>
    private static string? Template(AnimationEntry entry, AnimationEvent ev, AnimationGraphRow row)
    {
        if (row.Role != AnimationGraphRowRole.Event || PrimaryName(entry, ev) is not { Length: > 0 } name) return null;
        string shown = AnimationPhrases.Safe(name);
        int at = row.Label.IndexOf(shown, StringComparison.Ordinal);
        return at < 0 ? null : row.Label[..at] + "\u0000" + row.Label[(at + shown.Length)..];
    }
    /// <summary>
    /// Rows group when they are one series: the same phrase around names that differ only by their digits (fill_can1 … 9),
    /// and, for calls and by-name events, targets of one definition.
    /// </summary>
    private static string? GroupKey(AnimationGraphIndex index, AnimationEntry entry, AnimationEvent ev, AnimationGraphRow row)
    {
        if (Template(entry, ev, row) is not { } template || PrimaryName(entry, ev) is not { } name) return null;
        string series = string.Concat(name.Where(c => !char.IsAsciiDigit(c)));
        string target = row.TargetEntry is int t ? index.Group(t) : "";
        return string.Create(CultureInfo.InvariantCulture, $"{row.Type}|{row.Block}|{row.Branch}|{row.Clock}|{row.Threshold:R}|{series}|{target}|{template}");
    }

    /// <summary>
    /// The sequence's condition blocks (IF, its ELSEIF and ELSE branches, ENDIF) and loop brackets (event 0 to each LOOP),
    /// and which block and branch holds each event. The game's scan is depth-unaware; a nested IF reads as its own block.
    /// </summary>
    private static (List<AnimationGraphBlock> Blocks, (int Block, int Branch)[] Membership, List<string> Diagnostics) Blocks(AnimationGraphIndex index, AnimationEntry entry, AnimationSequence sequence)
    {
        var events = sequence.Events;
        List<AnimationGraphBlock> blocks = [];
        var membership = Enumerable.Repeat((-1, -1), events.Count).ToArray();
        List<string> diagnostics = [];
        Stack<(int Block, List<AnimationGraphBranch> Branches, int Head)> open = [];
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];
            if (ev.Bytes.Length < 12) continue;
            switch (ev.Type)
            {
                case 31:
                    {
                        if (open.Count > 0) membership[i] = (open.Peek().Block, open.Peek().Branches.Count - 1);
                        blocks.Add(new($"if:{entry.Index}:{ev.Id:N}", "condition", entry.Index, sequence.Id, i, events.Count - 1));
                        List<AnimationGraphBranch> branches = [new(0, "ifTrue", "if true", ev.Id, i + 1, i, false, false) { Condition = Condition(entry, ev, ev, false) }];
                        open.Push((blocks.Count - 1, branches, i));
                        break;
                    }
                case 32 or 33 when open.Count > 0:
                    {
                        var (block, branches, head) = open.Peek();
                        Close(branches, i - 1);
                        if (ev.Type == 33) branches.Add(new(branches.Count, "elseIf", "else if", ev.Id, i + 1, i, false, false) { Condition = Condition(entry, ev, events[head], true) });
                        else branches.Add(new(branches.Count, "ifFalse", "if false", ev.Id, i + 1, i, false, false));
                        break;
                    }
                case 34 when open.Count > 0:
                    {
                        var (block, branches, _) = open.Pop();
                        Close(branches, i - 1);
                        if (branches[^1].Kind != "ifFalse") branches.Add(new(branches.Count, "ifFalse", "if false (nothing)", null, i, i - 1, true, true));
                        blocks[block] = blocks[block] with { Last = i, EndIf = ev.Id, Branches = branches.AsReadOnly() };
                        if (open.Count > 0) membership[i] = (open.Peek().Block, open.Peek().Branches.Count - 1);
                        break;
                    }
                case 32 or 33 or 34:
                    diagnostics.Add($"Event {i + 1} ({AnimationPhrases.Keyword(ev.Type)}) has no IF before it; the game skips it.");
                    break;
                case 30:
                    {
                        var (mode, count, seconds) = AnimationPhrases.Loop(ev);
                        bool waits = sequence != entry.Primary && sequence.ResetMode == 3;
                        var loop = new AnimationGraphLoop(ev.Id, mode, count, seconds, AnimationPhrases.LoopText(ev), waits);
                        blocks.Add(new($"loop:{entry.Index}:{ev.Id:N}", "loop", entry.Index, sequence.Id, 0, i) { Loop = loop, Diagnostic = waits ? "each pass waits for a new release" : null });
                        if (open.Count > 0) membership[i] = (open.Peek().Block, open.Peek().Branches.Count - 1);
                        break;
                    }
                default:
                    if (open.Count > 0) membership[i] = (open.Peek().Block, open.Peek().Branches.Count - 1);
                    break;
            }
        }
        while (open.Count > 0)
        {
            var (block, branches, head) = open.Pop();
            Close(branches, events.Count - 1);
            blocks[block] = blocks[block] with
            {
                Branches = branches.AsReadOnly(),
                Diagnostic = "This IF has no ENDIF: the game stops the sequence when a false test looks for its end.",
            };
            diagnostics.Add($"Event {head + 1} (IF) has no ENDIF.");
        }
        return (blocks, membership, diagnostics);

        static void Close(List<AnimationGraphBranch> branches, int last)
        {
            var current = branches[^1];
            bool empty = last < current.First;
            branches[^1] = current with { Last = last, Empty = empty, Label = empty ? current.Label + " (nothing)" : current.Label };
        }
    }

    /// <summary>The question in human terms, and the game's rule in words, as the runtime tests the operand bits (1, 2, 8, 0x10, 4).</summary>
    private static AnimationGraphCondition Condition(AnimationEntry entry, AnimationEvent ev, AnimationEvent head, bool elseIf)
    {
        if (ev.Bytes.Length < 24) return new(ev.Id, "none", "Incomplete condition", "The condition's record is incomplete; the game reads it as false.") { ElseIf = elseIf };
        uint mask = ev.U32(12);
        string attach = AnimationPhrases.Safe(entry.AttachName);
        AnimationGraphCondition condition;
        if ((mask & 1) != 0)
        {
            float weight = ev.F32(20);
            condition = new(ev.Id, "random", $"Random · {AnimationPhrases.Number(weight * 100)} % chance",
                $"In the game a fresh random draw, true with a chance of about {AnimationPhrases.Number(weight * 100)} %; the preview draws from the scenario's seeded table so a run repeats.")
                { Probability = weight };
        }
        else if ((mask & 2) != 0)
        {
            double radius = Math.Sqrt(Math.Max(0, ev.F32(20)));
            condition = new(ev.Id, "playerRange", $"Player within {AnimationPhrases.Number(radius)} of {attach}?",
                $"True when the distance from the player to the attach node {attach} is at most {AnimationPhrases.Number(radius)}.") { Radius = radius, Node = entry.AttachName };
        }
        else if ((mask & 8) != 0)
        {
            float height = ev.F32(20);
            condition = new(ev.Id, "playerCover", $"Anything within {AnimationPhrases.Number(height)} above the player?",
                $"True when a segment from the player straight up for {AnimationPhrases.Number(height)} hits solid, raycastable geometry in the current zones.") { CoverHeight = height };
        }
        else if ((mask & 0x10) != 0)
        {
            float height = ev.F32(20);
            // The game tests the node the chain's first IF names, also for its ELSEIF.
            string node = head.Bytes.Length >= 20 ? AnimationPhrases.NodeName(entry, head.I32(16)) : "(none)";
            condition = new(ev.Id, "cover", $"Anything within {AnimationPhrases.Number(height)} above {node}?",
                $"True when a segment from {node}'s origin straight up for {AnimationPhrases.Number(height)} hits solid, raycastable geometry in the current zones (its own subtree excluded).")
                { CoverHeight = height, Node = node };
        }
        else if ((mask & 4) != 0)
        {
            int level = ev.I32(20);
            string detail = level switch { >= 2 => "high", 1 => "medium", _ => "low" };
            string question = level switch { >= 2 => "Effects detail High?", 1 => "Effects detail Medium or higher?", _ => "Effects detail Low or higher? (always)" };
            condition = new(ev.Id, "effectsDetail", question,
                $"True when the game's Effects detail option is at least {char.ToUpperInvariant(detail[0]) + detail[1..]} (High with Direct3D; the software renderer forces Medium).") { EffectsDetail = detail };
        }
        else condition = new(ev.Id, "none", "No condition", "The event names no operand; the game reads it as false.");
        if (elseIf) condition = condition with { ElseIf = true, Question = "else, " + char.ToLowerInvariant(condition.Question[0]) + condition.Question[1..], ThresholdIgnored = !(ev.StartMode == 1 && ev.Threshold == 0) };
        return condition;
    }

    /// <summary>The activation node: triggers, gates, reset behaviour, priority, attach node and prerequisite groups.</summary>
    public static AnimationGraphActivation Activation(AnimationGraphIndex index, AnimationEntry entry, IReadOnlyList<AnimationGraphCallerGroup> callers, int callerCount)
    {
        List<AnimationGraphTrigger> triggers = [];
        string attach = AnimationPhrases.Safe(entry.AttachName);
        bool header = entry.Bytes.Length >= 180;
        byte mode = header ? entry.Bytes[153] : (byte)3;
        uint flags = header ? entry.U32(148) : 0;
        float health = header ? entry.F32(172) : 0, reset = header ? entry.F32(164) : 0;
        bool opening = index.StartsNewGame(entry.Index);
        if (opening) triggers.Add(new("newGameStart", "New game start (startanims.zrd) · clears its prerequisites for good", "NEW_GAME_START"));
        string damage = $"to {attach} or anything under it";
        triggers.Add(mode switch
        {
            3 => new("whenCalled", "When called", "ON_CALL"),
            4 => new("onStartup", "On startup (mission load; prerequisites checked)", "ON_STARTUP"),
            1 => new("collisionHit", $"Starts after {AnimationPhrases.Number(health)} collision damage {damage}", "COLLIDE_HIT") { Value = health },
            2 => new("weaponOrCollisionHit", $"Starts after {AnimationPhrases.Number(health)} weapon or collision damage {damage}", "WEAPON_OR_COLLIDE_HIT") { Value = health },
            _ => new("weaponHit", $"Starts after {AnimationPhrases.Number(health)} weapon damage {damage}", "WEAPON_HIT") { Value = health },
        });
        if ((flags & 2) != 0)
        {
            double range = Math.Sqrt(Math.Max(0, entry.F32(160)));
            triggers.Add(new("rangeGate", $"Runs only while the player is within {AnimationPhrases.Number(range)} of {attach} (its clocks freeze while out of range)", "EXECUTION_BY_RANGE") { Value = range });
        }
        if ((flags & 8) != 0) triggers.Add(new("zoneGate", $"Runs only while the camera's zones allow {attach}'s zone", "EXECUTION_BY_ZONE"));
        if (Math.Abs(reset + 99) < .1f) triggers.Add(new("oneShot", "One-shot: refused while running or finished", "RESET_TIME -99") { Value = reset });
        else if (reset < 0) triggers.Add(new("poseKept", "Keeps its final pose", "RESET_TIME " + AnimationPhrases.Number(reset).Replace('−', '-')) { Value = reset });
        else triggers.Add(new("cleansUp", $"Cleans up {AnimationSeconds.Short(reset)} after a stop, restoring its rest pose", "RESET_TIME " + AnimationPhrases.Number(reset)) { Value = reset });
        if ((flags & 0x20) != 0) triggers.Add(new("loadCleanup", "Cleanup at mission load, and before every restart from finished", "RESET_TIME"));
        byte priority = header ? entry.Bytes[154] : (byte)4;
        if (priority != 4) triggers.Add(new("priority", priority < 4 ? $"Priority {priority} (runs before priority-4 animations)" : $"Priority {priority} (the default is 4)", "EXECUTION_PRIORITY") { Value = priority });
        triggers.Add(new("attach", "Attach node " + attach, entry.AttachName == entry.RootName ? "NAME" : "ANIMATION_ROOT_NAME"));
        return new(entry.Index, entry.AttachName, triggers.AsReadOnly(), Prerequisites(index, entry))
            { NewGameStart = opening, Callers = callers, CallerCount = callerCount };
    }

    /// <summary>The prerequisite records as groups: required items, and the options a minimum of which must be met.</summary>
    private static IReadOnlyList<AnimationGraphPrerequisiteGroup> Prerequisites(AnimationGraphIndex index, AnimationEntry entry)
    {
        List<AnimationGraphPrerequisite> required = [], options = [];
        List<string> path = [];
        var records = entry.References[6];
        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Bytes.Length < 48) continue;
            int mode = record.Bytes[4]; bool option = record.I32(0) != 0;
            AnimationGraphPrerequisite? item = null;
            if (mode == 3) { path.Add(record.Text(12, 28)); continue; }
            if (mode == 1) { string name = record.Text(8, 32); item = new(i, "animation", AnimationPhrases.Safe(name), index.First(name)); }
            else if (mode == 2) { path.Add(record.Text(12, 28)); item = new(i, "object", AnimationPhrases.Safe(string.Join('/', path)), null); path.Clear(); }
            if (item == null) continue;
            var list = option ? options : required;
            if (list.Count < AnimationGraph.MaximumMembers) list.Add(item);
        }
        int minimum = entry.Bytes.Length > 268 ? entry.Bytes[268] : 0;
        List<AnimationGraphPrerequisiteGroup> groups = [];
        if (required.Count > 0) groups.Add(new(true, required.Count, required.Count == 1 ? "Required" : $"Required · all {required.Count}", required.AsReadOnly()));
        if (options.Count > 0) groups.Add(new(false, minimum, minimum == 0 ? $"Any of {options.Count} (none needed)" : $"At least {minimum} of {options.Count}", options.AsReadOnly()));
        return groups.AsReadOnly();
    }
}
