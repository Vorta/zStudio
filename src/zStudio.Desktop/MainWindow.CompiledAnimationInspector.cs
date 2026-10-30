using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>
/// The compiled preview's Inspector cards and Scenario tab (design/animation-redesign.md §19): Animation mode's cards built from
/// the graph, the run and the stored record, read only, with Stored (type, size, offsets, flags as hex) and Bytes (the original
/// source bytes, at most 4 KiB) on every card and References and Related on the definition; the Scenario tab's conditions,
/// inputs, rules, world, bound root and range.
/// </summary>
public partial class MainWindow
{
    /// <summary>At most this many original source bytes shown on a card and returned by animation_records bytes.</summary>
    internal const int CompiledByteLimit = 4096;
    private readonly System.Diagnostics.Stopwatch compiledInspectorClock = System.Diagnostics.Stopwatch.StartNew();

    private void RefreshCompiledInspector(bool runtimeOnly = false)
    {
        if (compiledCard == null || !CompiledAnimationShown) return;
        if (runtimeOnly && animation?.IsPlaying == true && compiledInspectorClock.ElapsedMilliseconds < 250) return;
        compiledInspectorClock.Restart();
        try
        {
            if (BuildCompiledInspection() is { } inspection) compiledCard.InspectCompiled(inspection);
        }
        catch (StudioCommandException) { compiledSelection = null; if (BuildCompiledInspection() is { } fallback) compiledCard.InspectCompiled(fallback); }
        RefreshCompiledPaneHeader();
    }

    /// <summary>The card of the compiled selection: the shown animation's definition card when nothing (or its frame) is selected.</summary>
    private AnimationInspection? BuildCompiledInspection()
    {
        if (!CompiledAnimationShown || animation is not { } editor || shownDocument?.Document.Animations is not { } package) return null;
        var layout = CompiledGraphLayout();
        int shownEntry = editor.EntryIndex;
        AnimationInspection card;
        string? id = compiledSelection;
        if (layout == null || id == null) card = CompiledDefinitionCard(layout, package, shownEntry, null);
        else if (layout.Graph.Row(id) is { } row) card = CompiledEventCard(layout, package, row);
        else if (layout.Node(id)?.Node is { } node)
            card = node.Kind switch
            {
                AnimationGraphNodeKind.Sequence => CompiledSequenceCard(layout, package, node),
                AnimationGraphNodeKind.Reference => CompiledReferenceCard(layout, package, node),
                AnimationGraphNodeKind.Activation => CompiledDefinitionCard(layout, package, node.Entry ?? shownEntry, id, "activation"),
                _ => CompiledDefinitionCard(layout, package, node.Entry ?? shownEntry, id),
            };
        else throw new StudioCommandException("stale_record", "The graph has no node or row with that identity; read it again.");
        var notices = compiledNotices.Where(n => Concerns(card, n, layout)).ToArray();
        var generic = notices.Where(n => n.Generic).ToArray();
        List<InspectorSection> sections = [];
        var specific = notices.Where(n => !n.Generic).ToArray();
        if (specific.Length > 0 || generic.Length > 0)
        {
            List<InspectorItem> items = [.. specific.Take(12).SelectMany(n => NoticeItems(n, n.Severity == "Information" ? "info" : "caution"))];
            if (generic.Length > 0) items.Add(new InspectorNoticeGroup(generic.Length == 1 ? "1 preview approximation" : $"{generic.Length} preview approximations", [.. generic.Select(n => n.Text).Distinct().Take(32)]));
            sections.Add(new("notices", "Notices") { Summary = specific.Length > 0 ? specific.Length.ToString(CultureInfo.InvariantCulture) : null, Live = true, Items = items });
        }
        return card with { Notices = notices, Sections = [.. sections, .. card.Sections] };
    }

    // ---------------------------------------------------------------- cards

    private AnimationInspection CompiledDefinitionCard(AnimationGraphLayout? layout, AnimationPackage package, int entryIndex, string? selection, string? scrollTo = null)
    {
        var doc = shownDocument!;
        var editor = animation!;
        if (entryIndex < 0 || entryIndex >= package.Entries.Count) entryIndex = editor.EntryIndex;
        var entry = package.Entries[entryIndex];
        bool shown = entryIndex == editor.EntryIndex;
        List<InspectorSection> sections = [];
        // Binding: what the preview binds it to (the shown animation), or the called binding's names.
        List<InspectorItem> binding = [new InspectorValue("Root", entry.RootName), new InspectorValue("Attach node", entry.AttachName)];
        if (shown)
        {
            // Live: Bind root…, Clear override, Choose GameZ… and a failed preparation change these without another selection.
            binding.Add(new InspectorValue("Bound to", editor.Loading && editor.PreviewFailure == null ? "loading…" : editor.BoundRootLabel() + (editor.RootOverridden ? " · override" : ""))
                { Help = editor.RootOverridden ? "A preview override: Scenario → Bound root → Clear override binds it as the game does." : "The first node with the root's name in the mission scene, as the game binds it." });
            if ((editor.PreviewContext?.World.Path ?? (compiledWorldChosen.TryGetValue(doc, out var chosen) ? chosen : null)) is { } world) binding.Add(new InspectorValue("World", world));
            if (editor.PreviewFailure is { } failure) binding.Add(new InspectorText("Couldn't prepare the preview: " + failure + " Scenario → World → Choose GameZ… picks the mission scene.", "caution"));
        }
        sections.Add(new("binding", "Binding") { Summary = "#" + entryIndex.ToString(CultureInfo.InvariantCulture), Live = shown, Items = binding });
        // Activation: the triggers, gates and reset as the graph reads them.
        var activation = layout == null ? null : AnimationGraph.Activation(layout.Graph.Index, entryIndex);
        if (activation != null)
        {
            List<InspectorItem> items = [.. activation.Triggers.Take(16).Select(t => (InspectorItem)new InspectorValue(KindWords(t.Kind), t.Phrase + (t.Keyword is { Length: > 0 } keyword ? " · " + keyword : "")) { Mono = false })];
            if (activation.AttachNode is { Length: > 0 } attach) items.Add(new InspectorValue("Attach node", attach));
            if (items.Count == 0) items.Add(new InspectorText("No activation settings stored."));
            sections.Add(new("activation", "Activation") { Summary = activation.Triggers.FirstOrDefault()?.Phrase ?? "", Items = items });
            if (activation.Prerequisites.Count > 0)
            {
                var run = compiledOverlay?.Binding(entryIndex);
                List<InspectorItem> prerequisites = [];
                foreach (var group in activation.Prerequisites.Take(8))
                {
                    prerequisites.Add(new InspectorText(group.Header + (compiledGraphView?.PrerequisiteGroupOutcome(run, group) is { } outcome ? " · " + outcome : ""), "body"));
                    foreach (var item in group.Items.Take(32))
                    {
                        var check = run?.Prerequisites?.Result.Checks.FirstOrDefault(c => c.Record == item.Record);
                        prerequisites.Add(new InspectorText((check == null ? "○ " : check.Satisfied == true ? "✓ " : "✗ ") + item.Target, "mono"));
                    }
                }
                prerequisites.Add(new InspectorText("The isolated preview runs this animation alone: prerequisites follow the scenario's rule (Scenario → Rules)."));
                sections.Add(new("prerequisites", "Prerequisites") { Summary = activation.Prerequisites.Count.ToString(CultureInfo.InvariantCulture), Live = true, Items = prerequisites });
            }
        }
        // Calls and callers: what it calls (reference nodes) and the file's events that reach it.
        if (layout != null)
        {
            List<InspectorItem> calls = [];
            var called = layout.Nodes.Where(n => n.Node.Reference is { Calling: false }).Select(n => n.Node.Reference!).ToArray();
            if (called.Length > 0) calls.Add(new InspectorValue("Calls", Trim(string.Join(", ", called.Take(16).Select(r => r.Name + (r.EntryCount > 1 ? $" ({r.EntryCount})" : ""))), 400)));
            var callers = layout.Graph.Callers(entryIndex, 0, 16);
            if (callers.Total > 0) calls.Add(new InspectorValue("Called by", Trim(string.Join(", ", callers.Rows.Select(c => $"{c.Name} #{c.Caller}").Distinct()), 400) + (callers.Total > callers.Rows.Count ? $" (+{callers.Total - callers.Rows.Count})" : "")));
            if (calls.Count == 0) calls.Add(new InspectorText("It calls no other animation and nothing in this file calls it."));
            sections.Add(new("calls", "Calls and callers") { Items = calls });
        }
        // Run: the binding's state at the playhead.
        if (compiledOverlay?.Binding(entryIndex) is { } bindingRun && editor.CurrentFrame is { } frame)
            sections.Add(new("run", "Run") { Summary = bindingRun.Label, Live = true, Items = [new InspectorValue("At", AnimationSeconds.Format(frame.Time)), new InspectorValue("State", bindingRun.Label)] });
        sections.Add(StoredSection("Animation entry", entry.SourceOffset, entry.SourceLength,
        [
            new InspectorValue("Sequences", $"{entry.Sequences.Count} runtime · cleanup {(entry.Primary.Events.Count > 0 ? entry.Primary.Events.Count + " events" : "empty")}"),
            new InspectorValue("Header", Hex(entry.Bytes.AsSpan(0, Math.Min(entry.Bytes.Length, 32)))) { Mono = true, Help = entry.Bytes.Length > 32 ? $"First 32 of {entry.Bytes.Length} header bytes; Bytes below shows the record." : null },
            new InspectorValue("Flags", entry.Bytes.Length >= 152 ? "0x" + entry.U32(148).ToString("X8", CultureInfo.InvariantCulture) : "not stored") { Mono = true },
        ]));
        sections.Add(BytesSection(doc, entry.SourceOffset, entry.SourceLength, $"Entry #{entry.Index} · {entry.Name}"));
        sections.Add(ReferencesSection(entry));
        sections.Add(RelatedSection(doc, entry));
        return new(selection != null && selection.StartsWith("activation:", StringComparison.Ordinal) ? "activation" : "definition", entry.Name, $"Animation #{entry.Index} · {CompiledFileName(doc)}")
        {
            Glyph = "Definition", Selection = selection, Entry = entryIndex, ScrollTo = scrollTo, Sections = sections,
        };
    }
    private AnimationInspection CompiledSequenceCard(AnimationGraphLayout layout, AnimationPackage package, AnimationGraphNode node)
    {
        var info = node.Sequence!;
        var entry = package.Entries[info.Entry];
        var sequence = entry.AllSequences.First(s => s.Id == info.Id);
        int number = AnimationGraph.CompiledNumber(entry, info.Id);
        List<InspectorItem> main =
        [
            new InspectorValue("Name", info.Cleanup ? "Cleanup (RESET_STATE)" : sequence.Name),
            new InspectorValue("Compiled number", info.Cleanup ? "cleanup · first stored sequence" : "sequence " + number.ToString(CultureInfo.InvariantCulture)),
            new InspectorValue("Events", info.EventCount.ToString(CultureInfo.InvariantCulture)),
            new InspectorValue("Initial state", AnimationEventPresentation.ResetState(sequence.ResetMode) + (info.WaitsForRelease ? " · waits for release (ACTIVATION ( ON_CALL ))" : "")),
        ];
        if (info.StateLine != null) main.Add(new InspectorText(info.StateLine));
        if (info.IgnoredStartTime) main.Add(new InspectorText("START_TIME ignored: the game ignores START_TIME on a whole sequence.", "caution"));
        List<InspectorItem> releases = [];
        foreach (var edge in layout.Graph.EdgesOf(node.Id).Where(e => e.To == node.Id && e.Relation is AnimationGraphRelation.Release or AnimationGraphRelation.ContactRelease or AnimationGraphRelation.StopSequence).Take(16))
            releases.Add(new InspectorPair(edge.Relation == AnimationGraphRelation.StopSequence ? "Stopped by" : edge.Relation == AnimationGraphRelation.ContactRelease ? "Released on contact by" : "Released by", RowWhere(layout.Graph, edge.From)) { Link = new(RowWhere(layout.Graph, edge.From), "select", edge.From) });
        if (releases.Count == 0) releases.Add(new InspectorText(info.Cleanup ? "Stop and Reset animation run it." : info.WaitsForRelease ? "Nothing in this animation releases it." : "It starts at once with the animation."));
        List<InspectorSection> sections =
        [
            new("sequence", "Sequence") { Summary = info.Cleanup ? "RESET_STATE" : "SEQUENCE_DEFINITION", Items = main },
            new("releases", "Releases and stops") { Items = releases },
            CompiledSequenceRun(node.Id, info),
            StoredSection("Sequence", sequence.SourceOffset, SequenceLength(sequence),
            [
                new InspectorValue("Reset mode", sequence.ResetMode.ToString(CultureInfo.InvariantCulture) + " · " + AnimationEventPresentation.ResetState(sequence.ResetMode)),
                new InspectorValue("Header", Hex(sequence.Bytes.AsSpan(0, Math.Min(sequence.Bytes.Length, 32)))) { Mono = true },
            ]),
            BytesSection(shownDocument!, sequence.SourceOffset, SequenceLength(sequence), $"Entry #{entry.Index} · {entry.Name} / Sequence {(info.Cleanup ? "Cleanup" : sequence.Name)}"),
        ];
        return new("sequence", info.Cleanup ? "Cleanup" : sequence.Name, (info.Cleanup ? "Cleanup sequence" : "Sequence " + number.ToString(CultureInfo.InvariantCulture)) + " · #" + entry.Index.ToString(CultureInfo.InvariantCulture))
        {
            Glyph = info.Cleanup ? "graph:cleanup" : "Structure", Selection = node.Id, Entry = info.Entry, Sections = sections,
        };
    }
    private InspectorSection CompiledSequenceRun(string node, AnimationGraphSequence sequence)
    {
        var run = compiledOverlay?.Sequence(node);
        var frame = animation?.CurrentFrame;
        if (run == null || frame == null)
            return new("run", "Run") { Summary = frame == null ? "no run" : "not started", Live = true, Items = [new InspectorText("Play or seek to see this sequence run.")] };
        List<InspectorItem> items = [new InspectorValue("At", AnimationSeconds.Format(frame.Time)), new InspectorValue("State", run.Label)];
        if (run.Pass > 0) items.Add(new InspectorValue("Pass", run.Pass.ToString(CultureInfo.InvariantCulture)));
        if (run.ThresholdRemaining is double remaining) items.Add(new InspectorValue("Waits", $"{AnimationSeconds.Short(remaining)} more for its next event's threshold"));
        if (run.ReleasedBy is { } releaser && compiledLayout?.Graph is { } graph)
            items.Add(new InspectorPair("Released by", RowWhere(graph, releaser) + (run.ReleasedAt is double at ? " at " + AnimationSeconds.Format(at) : "")) { Link = graph.Row(releaser) != null ? new(RowWhere(graph, releaser), "select", releaser) : null });
        if (run.NoReleaserLeft) items.Add(new InspectorText("No releaser left: nothing can release it again in this run.", "caution"));
        if (run.PausedBy != null) items.Add(new InspectorValue("Paused", run.PausedBy + " (its clocks freeze)"));
        if (run.BlockedCall != null) items.Add(new InspectorPair("Blocked", "waits for excluded call " + run.BlockedCall) { Link = run.BlockedBy is { } edge ? new("Include it", "include", edge) : null });
        if (run.Instance is long instance && frame.Sequences.FirstOrDefault(s => s.Instance == instance && s.Sequence == sequence.Id) is { } status)
        {
            items.Add(new InspectorValue("Sequence clock", AnimationSeconds.Format(status.SequenceElapsed)));
            items.Add(new InspectorValue("Animation clock", AnimationSeconds.Format(status.AnimationElapsed)));
        }
        return new("run", "Run") { Summary = run.Label, Live = true, Items = items };
    }
    private AnimationInspection CompiledEventCard(AnimationGraphLayout layout, AnimationPackage package, AnimationGraphRow row)
    {
        var graph = layout.Graph;
        var entry = package.Entries[row.Entry];
        var sequence = entry.AllSequences.First(s => s.Id == row.Sequence);
        var ev = sequence.Events.First(e => e.Id == row.Event);
        string sequenceName = graph.Node(AnimationGraph.SequenceId(row.Entry, row.Sequence))?.Label ?? "sequence";
        var condition = row.Role == AnimationGraphRowRole.Question ? ConditionOf(graph, row) : null;
        string kind = condition != null ? "condition" : RowKind(graph, row);
        var presentation = AnimationEventPresentation.Create(entry, ev);
        List<InspectorSection> sections = [];
        if (condition != null)
        {
            List<InspectorItem> items = [new InspectorValue("Question", condition.Question)];
            if (condition.Radius is double radius) items.Add(new InspectorValue("Radius", AnimationPhrases.Number(radius)) { Suffix = "units" });
            if (condition.Probability is double weight) items.Add(new InspectorValue("Weight", weight.ToString("0.###", CultureInfo.InvariantCulture)) { Help = "The chance 0–1 that it is true; random in the game." });
            if (condition.EffectsDetail is { } detail) items.Add(new InspectorValue("Effects detail level", detail));
            if (condition.Node is { } at) items.Add(new InspectorValue("Node", at));
            items.Add(new InspectorText(condition.Rule));
            items.Add(new InspectorText(StoredCondition(row, condition) ?? presentation.Summary, "mono"));
            sections.Add(new("condition", "Condition") { Summary = condition.Operand switch { "playerRange" => "PLAYER_RANGE", "random" => "RANDOM_WEIGHT", "effectsDetail" => "ANIMATION_LOD", "cover" or "playerCover" => "NODE_UNDERCOVER", _ => row.Keyword }, Items = items });
            sections.Add(CompiledPreviewSection(graph, row, condition));
            sections.Add(CompiledOutcomesSection(graph, row));
        }
        else
        {
            List<InspectorItem> items = [new InspectorValue("Event", row.Label), new InspectorValue("Kind", $"{ev.Name} · type {ev.Type}")];
            if (presentation.Summary.Length > 0) items.Add(new InspectorValue("Fields", presentation.Summary));
            if (row.Callback is { } callback) items.Add(new InspectorText($"Game callback {callback.Value}: {callback.Meaning} (preview: {callback.Preview})", "info"));
            if (row.Diagnostic != null) items.Add(new InspectorText(row.Diagnostic, "caution"));
            sections.Add(new("event", "Event") { Summary = row.Keyword, Items = items });
            if (kind == "call") sections.Add(CompiledCallSection(graph, row));
        }
        // The start threshold as stored (read only): its clock, value and what it means.
        List<InspectorItem> threshold = [];
        if (row.ThresholdBadge != null)
        {
            double seconds = row.Threshold;
            threshold.Add(new InspectorValue("Clock", row.Clock switch { 2 => "Sequence clock (SEQUENCE_OFFSET)", 3 => "After the previous event (EVENT_OFFSET)", _ => "Animation clock (ANIMATION_OFFSET)" }));
            threshold.Add(new InspectorValue("Value", AnimationSeconds.Value(seconds)) { Suffix = "s" });
            string words = AnimationPhrases.ThresholdWords(row.Clock == 0 ? (byte)1 : row.Clock, (float)seconds);
            threshold.Add(new InspectorText(char.ToUpperInvariant(words[0]) + words[1..] + ". Earlier events, waits and conditions can delay it."));
        }
        else
        {
            threshold.Add(new InspectorValue("Clock", "None: animation clock 0"));
            threshold.Add(new InspectorText("Without START_TIME an event may start as soon as the sequence reaches it."));
        }
        if (row.ThresholdNote != null) threshold.Add(new InspectorText(row.ThresholdNote));
        sections.Add(new("threshold", "Start threshold") { Summary = row.ThresholdBadge ?? "none", Items = threshold });
        if (condition == null) sections.Add(CompiledOccurrences(row));
        sections.Add(StoredSection($"Event type {ev.Type} · {ev.Name}", ev.SourceOffset, ev.Bytes.Length,
        [
            new InspectorValue("Start mode", ev.StartMode.ToString(CultureInfo.InvariantCulture)),
            new InspectorValue("Flags", ev.Bytes.Length >= 16 ? "0x" + ev.U32(12).ToString("X8", CultureInfo.InvariantCulture) : "not stored") { Mono = true, Help = "The 32-bit value at offset 12, where flagged events keep their options." },
        ]));
        sections.Add(BytesSection(shownDocument!, ev.SourceOffset, ev.Bytes.Length, $"Entry #{entry.Index} · {entry.Name} / Sequence {(sequence == entry.Primary ? "Cleanup" : sequence.Name)} / Event #{sequence.Events.IndexOf(ev)} · {ev.Name}"));
        string subtitle = condition != null ? $"Condition · {(condition.ElseIf ? "ELSEIF" : "IF")} · {sequenceName}" : (kind == "call" ? row.Family switch { AnimationEventFamily.Release => "Release", AnimationEventFamily.Stop => "Stop", _ => "Call" } : "Event") + " · " + sequenceName;
        return new(kind, condition?.Question ?? row.Label, subtitle)
        {
            Glyph = "graph:" + AnimationGraphGlyphs.Family(row), Selection = row.Id, Entry = row.Entry, Path = graph.Node(AnimationGraph.FrameId(row.Entry))?.Frame?.Path, Sections = sections,
        };
    }
    /// <summary>The condition's switch in each call path that runs it, and the rule in words with the scenario's inputs.</summary>
    private InspectorSection CompiledPreviewSection(AnimationGraph graph, AnimationGraphRow row, AnimationGraphCondition condition)
    {
        var editor = animation!;
        var scenario = editor.Scenario;
        var package = shownDocument!.Document.Animations!;
        List<(string Path, string Label)> paths = [];
        if (row.Entry == editor.EntryIndex) paths.Add(("", $"{package.Entries[row.Entry].Name} #{row.Entry} (the shown animation)"));
        // Back-links (a call back into its own chain) are summed up: the game refuses them while this animation runs.
        int backLinks = 0;
        foreach (var call in scenario.CallPaths(package).Paths.Where(p => p.Target == row.Entry).Take(64))
        {
            if (call.BackLink) { backLinks++; continue; }
            paths.Add((call.Path, $"{package.Entries[call.Caller].Name} #{call.Caller} → {package.Entries[row.Entry].Name} #{row.Entry}" + (scenario.Includes(call.Path) ? "" : " (excluded)")));
        }
        var frame = editor.CurrentFrame;
        List<InspectorItem> items = [];
        foreach (var (path, label) in paths.Take(InspectorPaths))
        {
            var latest = frame?.Conditions.LastOrDefault(c => c.Event == row.Event && c.InstancePath == path);
            items.Add(new InspectorSwitch($"{condition.Question.TrimEnd('?')}, preview", path, row.Entry, row.Event, scenario.Choice(path, row.Entry, row.Event), label)
            { Outcome = latest == null ? null : $"{(latest.Value ? "true" : "false")} · {(latest.Forced ? "forced" : "game")} · {AnimationSeconds.Format(latest.Time)}" });
        }
        if (paths.Count > InspectorPaths) items.Add(new InspectorText($"and {paths.Count - InspectorPaths} more call paths reach it (animation_scenario conditions sets any of them)."));
        if (backLinks > 0) items.Add(new InspectorText($"{AnimationNotice.Plural(backLinks, "1 back-link", "back-links")} (calls back into this chain) also reach it: the game refuses them while it runs (animation_scenario conditions sets their switches)."));
        if (paths.Count == 0) items.Add(new InspectorText("No call path of the preview reaches this condition."));
        var pass = compiledOverlay?.Condition(row.Id)?.Latest;
        items.Add(new InspectorText(CompiledConditionRule(condition, pass, scenario), "info"));
        if (condition.Operand is "playerRange" or "playerCover") items.Add(new InspectorLink("Player…", "tab", "scenario") { Tip = "The scenario's player: at start, a custom point or no player" });
        string summary = paths.Count == 0 ? "Game" : scenario.Choice(paths[0].Path, row.Entry, row.Event) switch { AnimationConditionChoice.True => "Forced true", AnimationConditionChoice.False => "Forced false", _ => "Game" };
        return new("preview", "Preview") { Summary = summary, Live = true, Items = items };
    }
    /// <summary>The game rule in words with the preview's inputs and the latest pass's measure.</summary>
    private static string CompiledConditionRule(AnimationGraphCondition condition, AnimationConditionPass? pass, AnimationScenario scenario)
    {
        string Result(bool value) => value ? "true" : "false";
        switch (condition.Operand)
        {
            case "playerRange":
                {
                    string radius = AnimationPhrases.Number(condition.Radius ?? 0), attach = condition.Node ?? "the attach node";
                    if (scenario.Player.Kind == AnimationPlayerReferenceKind.None) return "Scenario → Player is \"No player\": every range condition is false.";
                    string source = scenario.Player.Kind == AnimationPlayerReferenceKind.Custom ? "custom point" : "at start";
                    if (pass is { Unavailable: { } why }) return $"The player ({source}) has no position here ({why}), so within {radius} of {attach} is false.";
                    if (pass?.Measured is double d)
                        return $"The player ({source}, {d.ToString("N0", CultureInfo.InvariantCulture)} from {attach}) is {(d <= (condition.Radius ?? 0) ? "inside" : "outside")} {radius}: {Result(d <= (condition.Radius ?? 0))}.";
                    return $"True when the player ({source}) is within {radius} of {attach}. Not evaluated at the playhead yet.";
                }
            case "random":
                {
                    double weight = condition.Probability ?? 0;
                    string chance = (weight * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %";
                    if (pass is { Draw: int draw, Measured: double unit } && !pass.Forced)
                        return $"Seed {scenario.Seed}, draw {draw}: {unit.ToString("0.00", CultureInfo.InvariantCulture)} {(unit <= weight ? "≤" : ">")} {weight.ToString("0.###", CultureInfo.InvariantCulture)}: {Result(unit <= weight)}. In the game this is random ({chance}).";
                    return $"Seed {scenario.Seed}: the preview draws from a seeded table so a run repeats. In the game this is random ({chance}).";
                }
            case "effectsDetail":
                {
                    string needed = condition.EffectsDetail ?? "low";
                    int level = needed == "high" ? 2 : needed == "medium" ? 1 : 0;
                    return $"Effects detail {scenario.EffectsDetail} ≥ {char.ToUpperInvariant(needed[0]) + needed[1..]}: {Result((int)scenario.EffectsDetail >= level)}.";
                }
            default: return condition.Rule;
        }
    }
    private InspectorSection CompiledOutcomesSection(AnimationGraph graph, AnimationGraphRow row)
    {
        var frame = animation?.CurrentFrame;
        var results = frame?.Conditions.Where(c => c.Event == row.Event).ToArray() ?? [];
        if (results.Length == 0)
            return new("outcomes", "Outcomes") { Summary = "none yet", Live = true, Items = [new InspectorText(frame == null ? "Play or seek: each pass that tests the condition is listed here." : "The condition has not been reached in the run.")] };
        var shown = results.TakeLast(InspectorRows).ToArray();
        int first = results.Length - shown.Length;
        var rows = shown.Select((c, i) => new InspectorTableRow([(first + i + 1).ToString(CultureInfo.InvariantCulture), AnimationSeconds.Format(c.Time), c.Value ? "true" : "false", c.Forced ? "forced" : "game"])
        { Seek = AnimationSeconds.OccurrenceTime(c.Time) }).ToArray();
        List<InspectorItem> items = [new InspectorTable(["Pass", "Time", "Value", "How"], rows) { More = first }];
        var latest = results[^1];
        var block = graph.Blocks(row.Entry, row.Sequence).FirstOrDefault(b => b.Branches.Any(br => br.Marker == row.Event));
        if (block != null)
        {
            int index = block.Branches.ToList().FindIndex(br => br.Marker == row.Event);
            var taken = TakenBranch(block, index, latest, frame!.Conditions);
            if (taken != null) items.Add(new InspectorText($"Taken at {AnimationSeconds.Format(latest.Time)}: {taken.Label}."));
        }
        return new("outcomes", "Outcomes") { Summary = AnimationNotice.Plural(results.Length, "1 pass", "passes"), Live = true, Items = items };
    }
    /// <summary>A call's target and its include switch (every call runs in the compiled preview unless excluded).</summary>
    private InspectorSection CompiledCallSection(AnimationGraph graph, AnimationGraphRow row)
    {
        var scenario = animation!.Scenario;
        List<InspectorItem> items = [];
        foreach (var edge in graph.EdgesOf(row.Id).Where(e => e.From == row.Id && e.Relation != AnimationGraphRelation.Prerequisite).Take(8))
        {
            string target = graph.Node(edge.To)?.Reference?.Name ?? graph.Node(edge.To)?.Label ?? row.Target ?? edge.To;
            string verb = edge.Relation switch
            {
                AnimationGraphRelation.Release => "Releases", AnimationGraphRelation.ContactRelease => "Releases on ground contact", AnimationGraphRelation.StopSequence => "Stops",
                AnimationGraphRelation.StopAnimation => "Stops animation", AnimationGraphRelation.ResetAnimation => "Resets animation", AnimationGraphRelation.InvalidateAnimation => "Invalidates",
                AnimationGraphRelation.CallWait => "Calls and waits for", AnimationGraphRelation.LocalChild => "Calls as a local child", _ => "Calls",
            };
            items.Add(new InspectorPair(verb, target) { Link = graph.Node(edge.To) != null ? new(target, "select", edge.To) : null });
            if (edge.Relation is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild && edge.Paths.Count > 0)
            {
                int included = edge.Paths.Count(scenario.Includes);
                items.Add(new InspectorInclude("Included in preview", edge.Paths, included == edge.Paths.Count ? true : included == 0 ? false : null, 0,
                    "The compiled preview runs every call; excluding one leaves out what lies below it, and a waited call then blocks its sequence."));
            }
            if (edge.Note != null) items.Add(new InspectorText(edge.Note, "caution"));
        }
        if (items.Count == 0) items.Add(new InspectorText(row.Target is { } name ? $"No animation named {name} in this file: the call does nothing." : "The call names no animation."));
        return new("target", "Target") { Items = items, Live = true };
    }
    /// <summary>The event's occurrences from Timing's marks, as Animation mode's card lists them.</summary>
    private InspectorSection CompiledOccurrences(AnimationGraphRow row) =>
        OccurrencesSection(row, animation?.CurrentFrame == null ? null : compiledTiming, compiledMark);
    private AnimationInspection CompiledReferenceCard(AnimationGraphLayout layout, AnimationPackage package, AnimationGraphNode node)
    {
        var reference = node.Reference!;
        List<InspectorItem> items = [];
        if (reference.Detail.Length > 0) items.Add(new InspectorText(reference.Detail, "body"));
        if (reference.Summary.Length > 0) items.Add(new InspectorText(reference.Summary, "body"));
        items.Add(new InspectorValue("Bindings", reference.Entries.Count == 0 ? "none in this file" : string.Join(", ", reference.Entries.Take(16).Select(e => "#" + e.ToString(CultureInfo.InvariantCulture))) + (reference.EntryCount > 16 ? $" (+{reference.EntryCount - 16})" : "")));
        if (reference.Calls.Count > 0) items.Add(new InspectorValue("Calls", Trim(string.Join(", ", reference.Calls.Select(c => c.Name + (c.Events > 1 ? $" ×{c.Events}" : ""))), 300)));
        if (reference.Caution != null) items.Add(new InspectorText(reference.Caution, "caution"));
        List<InspectorLink> actions = [];
        if (reference.Entries.Count > 0) actions.Add(new("Open", "open", node.Id) { Tip = "Select this animation in Assets (its preview keeps this file's scenario)" });
        if (reference.Expandable && !reference.Unresolved) actions.Add(new(reference.Expanded ? "Collapse" : "Expand", "expand", node.Id) { Tip = "Show its frame in Structure (presentation only)" });
        if (actions.Count > 0) items.Add(new InspectorActions(actions));
        List<InspectorSection> sections = [new("animation", reference.Calling ? "Calling animation" : "Called animation") { Items = items }];
        if (compiledOverlay?.Reference(node.Id) is { } run)
            sections.Add(new("run", "Run") { Summary = run.Label, Live = true, Items = [new InspectorValue("State", run.Label), new InspectorValue("Instances", run.Instances.ToString(CultureInfo.InvariantCulture))] });
        if (reference.Entries.FirstOrDefault() is int first && first > 0 && first < package.Entries.Count)
        {
            var entry = package.Entries[first];
            sections.Add(StoredSection("Animation entry", entry.SourceOffset, entry.SourceLength, [new InspectorValue("Entry", "#" + first.ToString(CultureInfo.InvariantCulture) + " · " + entry.Name)]));
        }
        return new("reference", reference.Name, (reference.Calling ? "Calling animation" : "Called animation") + (reference.EntryCount > 1 ? $" · {reference.EntryCount} bindings" : ""))
        {
            Glyph = "Definition", Selection = node.Id, Entry = reference.Entries.FirstOrDefault(), Sections = sections,
        };
    }

    /// <summary>The stored condition in the definition language: "IF ( PLAYER_RANGE ( 64 ) )", or null for other operands.</summary>
    private static string? StoredCondition(AnimationGraphRow row, AnimationGraphCondition condition)
    {
        string? operand = condition.Operand switch
        {
            "playerRange" when condition.Radius is double radius => $"PLAYER_RANGE ( {AnimationPhrases.Number(radius)} )",
            "random" when condition.Probability is double weight => $"RANDOM_WEIGHT ( {weight.ToString("0.###", CultureInfo.InvariantCulture)} )",
            "effectsDetail" when condition.EffectsDetail is { } detail => $"ANIMATION_LOD ( {detail.ToUpperInvariant()} )",
            "cover" or "playerCover" => $"NODE_UNDERCOVER ( {condition.Node ?? "PLAYER"} {AnimationPhrases.Number(condition.CoverHeight ?? 50)} )",
            _ => null,
        };
        return operand == null ? null : $"{row.Keyword} ( {operand} )";
    }
    /// <summary>"whenCalled" → "When called": a trigger kind as a caption.</summary>
    private static string KindWords(string kind)
    {
        StringBuilder words = new();
        foreach (char c in kind)
        {
            if (char.IsUpper(c) && words.Length > 0) words.Append(' ').Append(char.ToLowerInvariant(c));
            else words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : c);
        }
        return words.ToString();
    }

    // ---------------------------------------------------------------- stored data

    private static InspectorSection StoredSection(string type, long offset, long length, IReadOnlyList<InspectorItem> extra) => new("stored", "Stored")
    {
        Summary = type, Expanded = false,
        Items = [new InspectorValue("Type", type), new InspectorValue("Record", $"{length:N0} bytes at 0x{offset:X}") { Mono = true }, .. extra],
    };
    private static InspectorSection BytesSection(DocumentModel doc, long offset, long length, string scope)
    {
        var bytes = CompiledSourceBytes(doc, offset, length, 0, CompiledByteLimit);
        string text = bytes.Length == 0 ? "No stored range." : HexDump(bytes.Span, offset);
        return new("bytes", "Bytes")
        {
            Summary = length.ToString("N0", CultureInfo.InvariantCulture) + " B", Expanded = false,
            Items = [new InspectorText("Original source bytes (not properties, not runtime memory) · " + scope + (length > CompiledByteLimit ? $" · first {CompiledByteLimit:N0} of {length:N0} bytes" : "")),
                new InspectorValue("Original source bytes", text) { Mono = true }],
        };
    }
    /// <summary>The original source bytes of a record's range from <paramref name="start"/> (relative to the record), at most <paramref name="count"/>.</summary>
    internal static ReadOnlyMemory<byte> CompiledSourceBytes(DocumentModel doc, long offset, long length, long start, int count)
    {
        var all = doc.Document.Bytes;
        if (offset < 0 || length <= 0 || start < 0 || start >= length || offset > all.Length || length > all.Length - offset) return ReadOnlyMemory<byte>.Empty;
        long take = Math.Min(Math.Min(count, CompiledByteLimit), length - start);
        return all.Slice((int)(offset + start), (int)take);
    }
    /// <summary>The stored length of a sequence record: its 64-byte header and its events (as the file lays it out).</summary>
    internal static long SequenceLength(AnimationSequence sequence) => 64L + sequence.I32(60);
    private static string HexDump(ReadOnlySpan<byte> bytes, long offset)
    {
        StringBuilder result = new();
        for (int i = 0; i < bytes.Length; i += 16)
        {
            var row = bytes.Slice(i, Math.Min(16, bytes.Length - i));
            result.Append((offset + i).ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            foreach (byte value in row) result.Append(value.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
            if (i + 16 < bytes.Length) result.Append('\n');
        }
        return result.ToString();
    }
    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
    /// <summary>The stored reference tables with their indices; index 0 (and the tables the runtime does not index) reserved.</summary>
    private static InspectorSection ReferencesSection(AnimationEntry entry)
    {
        string[] names = ["Tracked nodes", "Scene nodes", "Lights", "Sound nodes", "Samples", "Effect templates", "Activation conditions", "Child slots"];
        List<InspectorItem> items = [new InspectorText("Stored references: stable table indices; reserved index 0 and the tables events do not index are marked.")];
        int total = 0;
        for (int table = 0; table < names.Length && table < entry.References.Length; table++)
        {
            var records = entry.References[table];
            total += records.Count;
            if (records.Count == 0) continue;
            items.Add(new InspectorText($"{names[table]} ({records.Count})" + (table is 0 or 6 or 7 ? " · reserved table" : ""), "body"));
            // An activation condition keeps its name after an 8-byte header; the other tables start with it.
            int at = table == 6 ? 8 : 0;
            string Name(AnimationRecord r) => r.Bytes.Length > at ? r.Text(at, Math.Min(32, r.Bytes.Length - at)) : "";
            var rows = records.Take(64).Select((r, i) => new InspectorTableRow([i.ToString(CultureInfo.InvariantCulture), i == 0 ? "reserved" + (Name(r) is { Length: > 0 } named ? " · " + named : "") : Name(r) is { Length: > 0 } shown ? shown : "(no name)"])).ToArray();
            items.Add(new InspectorTable(["#", "Name"], rows) { More = Math.Max(0, records.Count - 64) });
        }
        return new("references", "References") { Summary = $"{entry.References.Count(t => t.Count > 0)} tables", Expanded = false, Items = items };
    }
    /// <summary>Assets of the root whose names match this animation's names (name matches, not a dependency graph).</summary>
    private InspectorSection RelatedSection(DocumentModel doc, AnimationEntry entry)
    {
        var related = ViewModel.Related([entry.Name, entry.RootName, entry.AttachName], doc.Path);
        List<InspectorItem> items = [];
        foreach (var hit in related.Items.Take(24))
            items.Add(new InspectorValue($"{hit.Kind} #{hit.Index}", $"{hit.Name} · {Path.GetFileName(Path.GetDirectoryName(hit.File))}/{Path.GetFileName(hit.File)}"));
        if (items.Count == 0) items.Add(new InspectorText("No name matches in this root."));
        else if (related.Items.Count > 24 || related.Truncated) items.Add(new InspectorText("Name matches, limited; Search finds others."));
        return new("related", "Related") { Summary = related.Items.Count.ToString(CultureInfo.InvariantCulture), Expanded = false, Items = items };
    }

    // ---------------------------------------------------------------- notices and links

    private void RefreshCompiledNotices()
    {
        if (compiledPanel == null) return;
        var layout = CompiledAnimationShown ? CompiledGraphLayout() : null;
        var (effects, checks) = layout == null ? ([], AnimationWhatIfChecks.Status.Empty) : CompiledExcludedEffects(layout);
        var notices = layout == null ? [] : NoticesFor(layout, animation?.CurrentFrame, compiledTiming, effects, checks).ToList();
        if (animation?.PreviewFailure is { } failure)
            notices.Insert(0, new("failure", "preview", "Error", "Couldn't prepare the preview: " + failure + " Structure and the Inspector still show the stored data.") { Category = "Preview" });
        compiledNotices = notices;
        compiledPanel.SetNotices(compiledNotices);
        var badges = NoticeBadges(compiledNotices);
        compiledGraphView?.SetNotices(badges.Nodes, badges.Ports);
        compiledTimingView?.SetNotices(badges.Lanes, badges.Markers);
    }
    private AnimationWhatIfChecks? compiledWhatIf;
    /// <summary>The compiled preview's what-if checks of its excluded calls (design §15); a result refreshes its notices and card.</summary>
    private AnimationWhatIfChecks CompiledWhatIf
    {
        get
        {
            if (compiledWhatIf != null) return compiledWhatIf;
            compiledWhatIf = new(shutdownToken);
            compiledWhatIf.Changed += () => { if (!CompiledAnimationShown) return; RefreshCompiledNotices(); RefreshCompiledInspector(runtimeOnly: true); };
            return compiledWhatIf;
        }
    }
    /// <summary>
    /// What the compiled preview's excluded calls do in the game (design §15): as in a map (<see cref="WorldExcludedEffects"/>), its
    /// shown animation the one start; nothing is scheduled while the preview loads, seeks or measures its Auto range.
    /// </summary>
    private (IReadOnlyList<AnimationExcludedEffect> Effects, AnimationWhatIfChecks.Status Status) CompiledExcludedEffects(AnimationGraphLayout layout)
    {
        if (animation is not { CurrentFrame: { ExcludedCalls.Count: > 0 } frame, WhatIfRun: { } run } editor) return ([], AnimationWhatIfChecks.Status.Empty);
        var index = layout.Graph.Index;
        if (index.Package.Entries.Count != editor.PreviewContext!.Package.Entries.Count) return ([], AnimationWhatIfChecks.Status.Empty);
        var scenario = editor.Scenario; var policy = scenario.ToExecutionPolicy();
        var candidates = index.ExcludedEffects.Candidates(frame, policy, [editor.EntryIndex]);
        double end = editor.RangeEnd;
        var status = CompiledWhatIf.Evaluate(new(run, editor.EntryIndex, scenario, end), candidates, editor.CanSeek && !editor.RangePending, () => editor.WhatIfRunner(end));
        return (ExcludedEffects(index, status, policy), status);
    }
    /// <summary>A card's link or button in the compiled preview: select, seek, expand, open (another animation of the file), tab, include.</summary>
    private async Task CompiledLinkAsync(InspectorLink link)
    {
        switch (link.Action)
        {
            case "select": await CompiledSelectAsync(link.Target); break;
            case "open": await CompiledOpenReferenceAsync(link.Target); break;
            case "expand": if (compiledLayout?.Node(link.Target)?.Node.Reference is { } reference) CompiledExpand(link.Target, !reference.Expanded); break;
            case "seek": if (double.TryParse(link.Target, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) await CompiledTransportAsync("seek", seconds); break;
            case "tab": ShowCompiledTab(link.Target); break;
            case "include": if (compiledLayout?.Graph.Edge(link.Target) is { } edge) await CompiledIncludeAsync(edge.Paths, true); break;
            case "includePath": await CompiledIncludeAsync(IncludePathsOf(compiledNotices, link.Target), true); break;
            case "diagnostics": if (ShownKept is { } kept) { kept.Diagnostics = true; ArrangeWorkspace(); } break;
        }
    }

    // ---------------------------------------------------------------- the Scenario tab

    private void RefreshCompiledScenarioPanel()
    {
        if (compiledScenarioPanel == null || !CompiledAnimationShown) return;
        compiledScenarioPanel.SetView(BuildCompiledScenarioView());
        compiledScenarioRange = CompiledRangeShown();
        RefreshCompiledScenarioLive();
    }
    /// <summary>The Range the Scenario tab shows (Auto's words and end, measuring or not): it follows a measurement that arrives later.</summary>
    private (string Text, double End, bool Pending, bool Custom)? compiledScenarioRange;
    private (string Text, double End, bool Pending, bool Custom)? CompiledRangeShown() =>
        animation is { } editor ? (editor.RangeDescription, editor.RangeEnd, editor.RangePending, editor.CustomRange) : null;
    private void RefreshCompiledScenarioLive()
    {
        if (compiledScenarioPanel?.View == null || animation is not { } editor) return;
        var frame = editor.CurrentFrame;
        string player = frame?.PlayerReference is { Position: { } at } reference
            ? string.Create(CultureInfo.InvariantCulture, $"Now {at.X:0.0}, {at.Y:0.0}, {at.Z:0.0} · {reference.Source}") + (reference.Frozen ? " · frozen" : "")
            : editor.PreviewContext?.Mission?.PlayerStart is { } start ? string.Create(CultureInfo.InvariantCulture, $"At {start.X:0.0}, {start.Y:0.0}, {start.Z:0.0} in this mission")
            : "The preview's scene has no player start.";
        string prerequisites = frame == null ? "" : frame.PrerequisiteStarts.Count(p => p.Outcome == "refused") is int refused and > 0 ? $"{refused} refused" : "none refused";
        string range = frame == null ? "" : $"{frame.Bindings.Count(b => b.PausedBy == "out of range")} paused";
        string zone = frame == null ? "" : $"{frame.Bindings.Count(b => b.PausedBy == "zone")} paused";
        compiledScenarioPanel.SetLive(new(player, prerequisites, range, zone));
    }
    /// <summary>The compiled scenario as the Scenario tab shows it: one start, the calls it reaches by caller and callee name, overrides, world.</summary>
    private AnimationScenarioView BuildCompiledScenarioView()
    {
        var editor = RequireCompiled();
        var doc = shownDocument!;
        var package = doc.Document.Animations!;
        var scenario = editor.Scenario;
        List<ScenarioStartRow> starts = [.. scenario.Starts.Where(s => s.Entry < package.Entries.Count).Select(s => new ScenarioStartRow(s.Id, s.Entry, package.Entries[s.Entry].Name, package.Entries[s.Entry].RootName, s.Seconds, s.Id == Guid.Empty))];
        var paths = scenario.CallPaths(package);
        List<ScenarioGroupRow> groups = [];
        foreach (var group in paths.Paths.GroupBy(p => (Caller: package.Entries[p.Caller].Name, Target: package.Entries[p.Target].Name, p.BackLink)))
        {
            var rows = group.Select(p => new ScenarioPathRow(p.Path, $"{package.Entries[p.Caller].Name} #{p.Caller} → {package.Entries[p.Target].Name} #{p.Target}" + (p.BackLink ? " ↩" : ""), scenario.Includes(p.Path), p.BackLink)).ToArray();
            groups.Add(new($"{group.Key.Caller}>{group.Key.Target}{(group.Key.BackLink ? ">back" : "")}", $"{group.Key.Caller} → {group.Key.Target}" + (group.Key.BackLink ? " ↩ cycle" : ""), rows.Count(r => r.Included), rows.Length, group.Key.BackLink, rows));
        }
        List<ScenarioOverrideRow> overrides = [];
        foreach (var condition in scenario.Conditions)
        {
            var entry = package.Entries.ElementAtOrDefault(condition.Entry);
            var sequence = entry?.AllSequences.FirstOrDefault(s => s.Events.Any(e => e.Id == condition.Event));
            var ev = sequence?.Events.First(e => e.Id == condition.Event);
            overrides.Add(new(condition, entry == null || ev == null ? "A condition no longer in this file"
                : $"{(sequence == entry.Primary ? "Cleanup" : sequence!.Name)} › {AnimationPhrases.Event(entry, ev)}" + (condition.Entry != editor.EntryIndex ? $" · {entry.Name}" : "")));
        }
        var context = editor.PreviewContext;
        string world = context?.World.Path is { } path ? FolderAndName(path) : editor.Loading ? "loading…" : "not loaded";
        return new(CompiledFileName(doc), scenario, true, starts, groups, paths.Paths.Count(p => scenario.Includes(p.Path)), paths.Paths.Count, paths.Unresolved, paths.Truncated,
            ShownKept!.LastIncludeAll, overrides)
        {
            Compiled = new(CompiledFileName(doc), world, compiledWorldChosen.TryGetValue(doc, out _), editor.Loading ? "loading…" : editor.BoundRootLabel(), editor.RootOverridden,
                editor.RangeDescription, editor.CustomRange, editor.RangeEnd) { RangePending = editor.RangePending },
        };
    }
    /// <summary>The mission scene chosen for a compiled file's previews (Choose GameZ…); absent for the automatic one.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentModel, string> compiledWorldChosen = new();

    /// <summary>One Scenario tab request of the compiled preview, named as animation_scenario's actions.</summary>
    private async Task ApplyCompiledScenarioRequestAsync(string action, object? value)
    {
        var editor = RequireCompiled();
        var scenario = editor.Scenario;
        switch (action)
        {
            case "includeAll": await CompiledIncludeAllAsync(); break;
            case "excludeAll": await CompiledExcludeAllAsync(); break;
            case "include": var (paths, include) = ((string[], bool))value!; await CompiledIncludeAsync(paths, include); break;
            case "player":
                var kind = (AnimationPlayerReferenceKind)value!;
                if (kind == AnimationPlayerReferenceKind.Custom) BeginCompiledPick("player");
                else await CompiledScenarioAsync(scenario with { Player = kind == AnimationPlayerReferenceKind.None ? AnimationPlayerReference.None : AnimationPlayerReference.AtStart });
                break;
            case "pick": BeginCompiledPick((string)value!); break;
            case "seed": await CompiledScenarioAsync(scenario with { Seed = (int)value! }); break;
            case "effectsDetail": await CompiledScenarioAsync(scenario with { EffectsDetail = (AnimationEffectsDetail)value! }); break;
            case "removeOverride": var removed = (AnimationConditionOverride)value!; await CompiledConditionsAsync([(removed.InstancePath, removed.Entry, removed.Event, AnimationConditionChoice.Game)]); break;
            case "goTo":
                {
                    var condition = (AnimationConditionOverride)value!;
                    if (CompiledGraphLayout()?.Graph.RowOf(condition.Entry, condition.Event) is { } row) await CompiledSelectAsync(row.Id);
                    break;
                }
            case "prerequisites": await CompiledScenarioAsync(scenario with { Prerequisites = PrerequisiteRule((string)value!) }); break;
            case "rangeGates": await CompiledScenarioAsync(scenario with { RangeGates = (string)value! == "ignore" ? AnimationRangeGateRule.Ignore : AnimationRangeGateRule.Game }); break;
            case "zoneGates": await CompiledScenarioAsync(scenario with { ZoneGates = (string)value! == "view" ? AnimationZoneGateRule.View : AnimationZoneGateRule.Ignore }); break;
            case "phase": await CompiledScenarioAsync(scenario with { Phase = (string)value! == "cleanup" ? AnimationScenarioPhase.Cleanup : AnimationScenarioPhase.Runtime }); break;
            case "height": await CompiledScenarioAsync(scenario with { Height = (float)value! }); break;
            case "clearBeamOrigin": await CompiledScenarioAsync(scenario with { BeamOrigin = null }); break;
            case "reset": await CompiledResetAsync(); break;
            case "world":
                if (value is string path) await CompiledWorldAsync(path.Length == 0 ? null : path);
                else
                {
                    OpenFileDialog dialog = new() { Filter = "GameZ scene|*.zbd", Title = "Choose this animation's mission scene" };
                    if (dialog.ShowDialog(this) == true) await CompiledWorldAsync(dialog.FileName);
                }
                break;
            case "bindRoot": if (ChooseCompiledRoot() is int node) await CompiledRootAsync(node); break;
            case "clearRoot": await CompiledRootAsync(null); break;
            case "range": await CompiledRangeAsync((double)value!); break;
            case "autoRange": await CompiledRangeAsync(null); break;
        }
    }
    private async Task CompiledResetAsync()
    {
        var editor = RequireCompiled();
        ShownKept!.LastIncludeAll = null;
        // Everything the Scenario tab sets goes back: the chosen world (to the automatic one), the bound root, the range, the scenario.
        if (compiledWorldChosen.TryGetValue(shownDocument!, out _)) await CompiledWorldAsync(null);
        if (editor.RootOverridden) await CompiledRootAsync(null);
        if (editor.CustomRange) await CompiledRangeAsync(null);
        await CompiledScenarioAsync(AnimationScenario.Isolated(editor.EntryIndex));
    }
    /// <summary>World: the mission scene the file's previews bind in (null: the automatic one beside the file).</summary>
    private async Task CompiledWorldAsync(string? path)
    {
        var editor = RequireCompiled();
        if (path != null && !File.Exists(path)) throw new StudioCommandException("not_found", "No such GameZ file.");
        if (path == null) compiledWorldChosen.Remove(shownDocument!); else compiledWorldChosen.AddOrUpdate(shownDocument!, path);
        await editor.BindWorldAsync(path);
        RefreshCompiledScenarioPanel(); RefreshCompiledRun(); RefreshCompiledInspector();
    }
    /// <summary>Bound root: an override to <paramref name="node"/>, or the game's binding again (null: Clear override).</summary>
    private async Task CompiledRootAsync(int? node)
    {
        var editor = RequireCompiled();
        if (editor.PreviewContext == null) throw new StudioCommandException("not_ready", "No scene is loaded.");
        try { await editor.BindRootAsync(node); }
        catch (ArgumentOutOfRangeException) { throw new StudioCommandException("invalid_argument", "The scene has no such node."); }
        await editor.AwaitOptionWorkAsync();
        if (editor.PreviewFailure is { } failure) throw new StudioCommandException("preview_unavailable", failure);
        RefreshCompiledScenarioPanel(); RefreshCompiledRun(); RefreshCompiledInspector();
    }
    /// <summary>Bind root…: a searchable list of the scene's nodes (the first 500 matches); Cancel keeps the binding.</summary>
    private int? ChooseCompiledRoot()
    {
        var editor = RequireCompiled();
        if (editor.PreviewContext == null) throw new StudioCommandException("not_ready", "No scene is loaded.");
        editor.Pause();
        var entry = shownDocument!.Document.Animations!.Entries[editor.EntryIndex];
        TextBox query = new() { Margin = new(12, 4, 12, 8), Text = entry.RootName };
        AutomationProperties.SetName(query, "Find a scene node by name, class or number");
        TextBlock caption = new() { Text = "Find a node", Margin = new(12, 12, 12, 0), FontSize = 12 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        ListBox list = new() { DisplayMemberPath = "Label", Margin = new(12, 0, 12, 8), SelectionMode = SelectionMode.Single };
        AutomationProperties.SetName(list, "Scene nodes");
        void Filter() { try { list.ItemsSource = editor.RootChoices(query.Text, CancellationToken.None); } catch (OperationCanceledException) { } }
        query.TextChanged += (_, _) => Filter(); Filter();
        TextBlock note = new() { Text = "The first 500 matches. The binding is a preview override; Clear override returns to the game's.", Margin = new(12, 0, 12, 8), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Button bind = new() { Content = "Bind", IsDefault = true, MinWidth = 90, Margin = new(0, 0, 8, 0) };
        bind.SetResourceReference(StyleProperty, "AccentButtonStyle");
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(12, 0, 12, 12) };
        buttons.Children.Add(bind); buttons.Children.Add(cancel);
        DockPanel panel = new();
        foreach (var top in new FrameworkElement[] { caption, query }) { DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top); }
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        DockPanel.SetDock(note, Dock.Bottom); panel.Children.Add(note);
        panel.Children.Add(list);
        Window dialog = new() { Owner = this, Title = "Bind root", Width = 520, Height = 600, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        bind.Click += (_, _) => { if (list.SelectedItem != null) dialog.DialogResult = true; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) dialog.DialogResult = true; };
        return dialog.ShowDialog() == true && list.SelectedItem is AnimationRootChoices.Choice selected ? selected.Index : null;
    }

    // ---------------------------------------------------------------- Pick in view (the compiled preview's view)

    private void BeginCompiledPick(string target)
    {
        var editor = RequireCompiled();
        if (target is not ("player" or "beamOrigin")) throw new StudioCommandException("invalid_argument", "Pick the player or the beam origin.");
        compiledPick = target;
        if (compiledPickBanner == null)
        {
            compiledPickBanner = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 12, 0, 0), MinHeight = 36, Padding = new(12, 0, 4, 0), FontSize = 13 };
            AutomationProperties.SetName(compiledPickBanner, "Pick in view");
            AutomationProperties.SetLiveSetting(compiledPickBanner, AutomationLiveSetting.Polite);
            StackPanel content = new() { Orientation = Orientation.Horizontal };
            var pin = new PreviewIcon { Kind = "Pin", Width = 15, Height = 15, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            pin.SetResourceReference(PreviewIcon.ForegroundProperty, "AccentFillColorDefaultBrush");
            content.Children.Add(pin);
            content.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0) });
            Button cancel = new() { Content = "Cancel", Height = 28, Padding = new(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(cancel, "Cancel picking");
            cancel.Click += (_, _) => EndCompiledPick();
            content.Children.Add(cancel);
            compiledPickBanner.Content = content;
        }
        if (compiledPickBanner.Parent is Panel old && old != editor.ViewportRegion) old.Children.Remove(compiledPickBanner);
        if (compiledPickBanner.Parent == null && editor.ViewportRegion is Panel region) region.Children.Add(compiledPickBanner);
        ((TextBlock)((StackPanel)compiledPickBanner.Content).Children[1]).Text = target == "player" ? "Click a surface to place the player · Esc cancels" : "Click a surface to place the beam origin · Esc cancels";
        compiledPickBanner.Visibility = Visibility.Visible;
        editor.Viewport.PreviewMouseDown -= CompiledPickMouseDown; editor.Viewport.PreviewMouseDown += CompiledPickMouseDown;
        editor.Viewport.Focus();
    }
    private void EndCompiledPick()
    {
        if (compiledPick == null && compiledPickBanner?.Visibility != Visibility.Visible) return;
        compiledPick = null;
        if (compiledPickBanner != null) compiledPickBanner.Visibility = Visibility.Collapsed;
        if (animation != null) animation.Viewport.PreviewMouseDown -= CompiledPickMouseDown;
        RefreshCompiledScenarioPanel();
    }
    private void CompiledPickMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (compiledPick is not { } target || animation is not { } editor || !CompiledAnimationShown) { EndCompiledPick(); return; }
        if (e.ChangedButton == MouseButton.Right) { e.Handled = true; EndCompiledPick(); return; }
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        var point = e.GetPosition(editor.Viewport.RenderSurface);
        if (editor.Viewport.ProbeInspection(point)?.Surface == null) return;
        compiledPick = null;
        _ = ReportUi(async () => { try { await CompiledPickAsync(target, point); } finally { EndCompiledPick(); } });
    }
    /// <summary>The player or the beam origin placed on the surface under <paramref name="point"/> of the preview's view (DIP).</summary>
    private async Task<System.Numerics.Vector3> CompiledPickAsync(string target, Point point)
    {
        var editor = RequireCompiled();
        if (target is not ("player" or "beamOrigin")) throw new StudioCommandException("invalid_argument", "Pick the player or the beam origin.");
        var surface = editor.Viewport.ProbeInspection(point)?.Surface ?? throw new StudioCommandException("not_found", "No surface under that point of the view.");
        await CompiledScenarioAsync(target == "player" ? editor.Scenario with { Player = AnimationPlayerReference.Custom(surface) } : editor.Scenario with { BeamOrigin = surface });
        return surface;
    }
}
