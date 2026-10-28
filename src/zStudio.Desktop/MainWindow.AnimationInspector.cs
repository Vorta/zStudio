using System.Globalization;
using System.Numerics;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// Animation mode's Inspector (design/animation-redesign.md §11) and the map layout's definition card (§6): what the selection
/// is (the definition, its activation, a sequence, an event, a condition, a call, a reference node), built from the graph, the
/// run at the playhead, the scenario and the source as an <see cref="AnimationInspection"/> the card draws and MCP reads. The
/// notices of the opened animation (§15) are built here too: the badges on graph nodes and Timing lanes, the view bar's chip
/// and each card's Notices section.
/// </summary>
public partial class MainWindow
{
    /// <summary>What the Inspector shows beyond the graph's selection: a Timing mark, a lane, or a selection without a graph node.</summary>
    private sealed record InspectorTarget(string Kind, int Entry, string Path)
    {
        public string? Graph { get; init; }
        public Guid Sequence { get; init; }
        public Guid Event { get; init; }
        public string? Mark { get; init; }
    }
    private InspectorTarget? animationInspected;
    private readonly System.Diagnostics.Stopwatch animationInspectorClock = System.Diagnostics.Stopwatch.StartNew();
    private (DocumentModel Document, long Revision, int Definition, string Record, SourceAnimationEdits.SourceContext? Context, string? Failure)? inspectorSource;
    private (AnimationFrame? Frame, Guid Graph, AnimationScenario Scenario, int Toggles, IReadOnlyList<AnimationNotice> Notices)? animationNoticeCache;
    private const int InspectorRows = 12, InspectorBindings = 32, InspectorPaths = 8;

    /// <summary>The Inspector follows a graph selection (a node or row id).</summary>
    private void InspectGraph(string? id)
    {
        if (id == null || animationGraphLayout is not { } layout) { animationInspected = null; return; }
        if ((layout.Item(id)?.Item.Row ?? layout.Graph.Row(id)) is { } row)
            animationInspected = new(RowKind(layout.Graph, row), row.Entry, FramePath(layout.Graph, row.Entry)) { Graph = row.Id, Sequence = row.Sequence, Event = row.Event };
        else if (layout.Node(id)?.Node is { } node)
            animationInspected = node.Kind switch
            {
                AnimationGraphNodeKind.Sequence => new("sequence", node.Entry ?? -1, FramePath(layout.Graph, node.Entry ?? -1)) { Graph = id, Sequence = node.Sequence!.Id },
                AnimationGraphNodeKind.Reference => new("reference", node.Reference!.Entries.FirstOrDefault(-1), "") { Graph = id },
                AnimationGraphNodeKind.Activation => new("activation", node.Entry ?? -1, FramePath(layout.Graph, node.Entry ?? -1)) { Graph = id },
                _ => new("definition", node.Entry ?? -1, FramePath(layout.Graph, node.Entry ?? -1)) { Graph = id },
            };
        else animationInspected = null;
    }
    private (Guid Index, int Entry, AnimationGraph Graph)? inspectorGraph;
    /// <summary>The graph a selection's rows live in: the shown one when it draws that binding's frame, else that binding's own (cached).</summary>
    private AnimationGraph? GraphFor(SourceSceneSnapshot source, AnimationGraphLayout? layout, int entry)
    {
        if (layout?.Graph.Node(AnimationGraph.FrameId(entry)) != null) return layout.Graph;
        if (source.Animations is not { } package || entry <= 0 || entry >= package.Entries.Count) return layout?.Graph;
        var index = TimingIndex(source, package);
        if (inspectorGraph is { } cached && cached.Entry == entry && ReferenceEquals(cached.Graph.Index, index)) return cached.Graph;
        try { var graph = AnimationGraph.Create(index, entry); inspectorGraph = (Guid.NewGuid(), entry, graph); return graph; }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.InvalidDataException) { return layout?.Graph; }
    }
    private static string FramePath(AnimationGraph graph, int entry) => graph.Node(AnimationGraph.FrameId(entry))?.Frame?.Path ?? "";
    private static string RowKind(AnimationGraph graph, AnimationGraphRow row) =>
        row.Role == AnimationGraphRowRole.Question ? "condition"
        : graph.EdgesOf(row.Id).Any(e => e.From == row.Id && e.Relation != AnimationGraphRelation.Prerequisite) ? "call" : "event";

    /// <summary>
    /// Shows the Inspector card for the selection (Animation mode) or the definition card (map layout). While playing, the
    /// card follows the run at most ten times a second; at rest every change shows at once.
    /// </summary>
    private void RefreshAnimationInspector(bool runtimeOnly = false)
    {
        if (sectionCard is not { Entry: DefinitionEntry } card || card.AnimationModel == null) return;
        if (runtimeOnly && worldAnimationSession?.IsPlaying == true && animationInspectorClock.ElapsedMilliseconds < 100) return;
        animationInspectorClock.Restart();
        AnimationInspection? inspection;
        try { inspection = InAnimationMode && sourceChromeActive ? BuildAnimationInspection() : BuildDefinitionSummary(); }
        catch (Exception ex) when (ex is StudioCommandException or InvalidOperationException or ArgumentException or System.IO.InvalidDataException)
        { ViewModel.Status = "Inspector unavailable: " + ex.Message; inspection = null; }
        // Only the model's evidence can be unavailable; drawing it never fails quietly.
        if (inspection != null) { card.Inspect(inspection, InAnimationMode); Announce(inspection); }
        RefreshAnimationPaneHeader();
    }

    // ---------------------------------------------------------------- the Inspector cards

    /// <summary>The card of Animation mode's selection; with nothing selected, the opened definition's.</summary>
    private AnimationInspection? BuildAnimationInspection()
    {
        if (animationMode is not { } mode || mode.Document != shownDocument || mode.Document.Document is not SourceSceneSnapshot source) return null;
        var target = animationInspected ?? new InspectorTarget("definition", mode.Entry, "");
        var layout = CurrentAnimationGraphLayout();
        var notices = AnimationNotices();
        AnimationInspection inspection = target.Kind switch
        {
            "sequence" when GraphFor(source, layout, target.Entry) is { } graph => SequenceCard(source, graph, target),
            "event" or "condition" or "call" when GraphFor(source, layout, target.Entry) is { } graph => EventCard(source, graph, target),
            "reference" when layout != null && target.Graph != null => ReferenceCard(source, layout, target.Graph),
            "activation" => DefinitionCard(source, layout, target.Entry, mode.Definition, scrollTo: "activation"),
            _ => DefinitionCard(source, layout, target.Entry > 0 ? target.Entry : mode.Entry, mode.Definition),
        };
        // The item's notices come first (design §15): what concerns this item expanded (refused or ignored starts, unavailable
        // calls, limits, notes naming it); the preview's generic approximations only on the definition's card, as one collapsed line.
        bool definitionCard = inspection.Kind is "definition" or "activation";
        var own = notices.Where(n => n.Generic ? definitionCard && n.Entry == inspection.Entry : Concerns(inspection, n, layout)).ToArray();
        if (own.Length == 0) return inspection with { Notices = own };
        var specific = own.Where(n => !n.Generic).ToArray();
        // One line per approximation, however many sequences of the binding it came from.
        var generic = own.Where(n => n.Generic).GroupBy(n => n.Text).Select(g => g.First() with { Count = g.Sum(n => n.Count) }).ToArray();
        List<InspectorItem> shownNotices = [.. specific.Take(6).Select(n => (InspectorItem)new InspectorText(n.Text + (n.Count > 1 ? $" (×{n.Count:N0})" : "") + n.Where, n.Severity == "Warning" ? "caution" : "info"))];
        if (specific.Length > 6) shownNotices.Add(new InspectorText($"and {specific.Length - 6} more: the view bar's notices chip lists every notice."));
        if (generic.Length > 0)
            shownNotices.Add(new InspectorNoticeGroup(AnimationNotice.Plural(generic.Length, "1 preview approximation", "preview approximations"),
                [.. generic.Take(32).Select(n => n.Text + (n.Count > 1 ? $" (×{n.Count:N0})" : ""))]));
        InspectorSection noticesSection = new("notices", "Notices")
        {
            Summary = specific.Length > 0 ? specific.Length.ToString(CultureInfo.InvariantCulture) : AnimationNotice.Plural(generic.Length, "1 approximation", "approximations"),
            Live = true, Items = shownNotices,
        };
        return inspection with { Notices = own, Sections = [noticesSection, .. inspection.Sections] };
    }
    /// <summary>
    /// A generic preview approximation: a support note about how the preview runs this kind of event at all ("Approximate:
    /// effect rendering", "Engine-based; collision uses explicit preview override", nested activation order, collision surfaces),
    /// not about this item. The cards group them as one collapsed line; badges and the chip's count leave them out.
    /// </summary>
    private static bool GenericApproximation(string category, string severity, string message) =>
        category == "Support" && severity == "Information" && GenericNotes.Any(prefix => message.StartsWith(prefix, StringComparison.Ordinal));
    private static readonly string[] GenericNotes =
    [
        "Approximate:", "Engine-based", "Nested animation activation uses", "Preview collision", "Flat-ground preview collision", "Ground collision is disabled",
        "Inherited gameplay launch velocity", "Beam activation points use", "Animation checkpoint storage", "Additional distinct preview diagnostics",
    ];
    private static bool Concerns(AnimationInspection inspection, AnimationNotice notice, AnimationGraphLayout? layout)
    {
        if (inspection.Selection is { } selected && (notice.Node == selected || notice.Row == selected)) return true;
        return inspection.Kind switch
        {
            "definition" or "activation" => notice.Entry == inspection.Entry && notice.Sequence == null,
            "sequence" => notice.Node == inspection.Selection,
            "reference" => notice.Node == inspection.Selection,
            _ => notice.Event is Guid ev && layout?.Graph.Row(inspection.Selection ?? "")?.Event == ev,
        };
    }

    private AnimationInspection DefinitionCard(SourceSceneSnapshot source, AnimationGraphLayout? layout, int entry, int fallbackDefinition, string? scrollTo = null)
    {
        var package = source.Animations;
        int definitionIndex = source.AnimationProvenance?.FindEntry(entry)?.Definition ?? fallbackDefinition;
        var definition = source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == definitionIndex)
            ?? throw new StudioCommandException("stale_record", "The definition is not in this map.");
        var model = CardModel(definitionIndex);
        bool opened = animationMode?.Definition == definitionIndex;
        List<InspectorSection> sections = [];
        // Bindings: the root object links, Frame, and which one Animation mode plays.
        sections.Add(BindingsSection(definition, opened));
        // Activation: the editable settings with their Use check boxes; the activation phrases above them.
        AnimationGraphActivation? activation = ActivationOf(layout, package, source, entry);
        List<InspectorItem> activationItems = [];
        if (activation != null)
            activationItems.Add(new InspectorText(string.Join(" · ", activation.Triggers.Where(t => t.Kind != "attach").Select(t => t.Phrase)), "body"));
        if (model?.Records.FirstOrDefault(r => r.Id == "settings") is { } settings)
            activationItems.AddRange(settings.Fields.Where(f => AnimationSourceFields.Settings.Contains(f.Key)).Select(f => (InspectorItem)new InspectorField(f.Id) { Label = ActivationLabel(f.Key), Help = ActivationHelp(f.Key) }));
        else activationItems.Add(new InspectorText(model == null ? "Loading the definition's source…" : "This definition has no activation settings to edit."));
        sections.Add(new("activation", "Activation") { Summary = activation?.Triggers.FirstOrDefault(t => t.Kind is "whenCalled" or "onStartup" or "weaponHit" or "collisionHit" or "weaponOrCollisionHit")?.Phrase.Split(' ', 4) is { } words ? Trim(string.Join(' ', words.Take(3)), 24) : null, Items = activationItems });
        // Prerequisites: each group as list editors with ✓/○ satisfaction at the playhead.
        if (PrerequisitesSection(model, activation, entry) is { } prerequisites) sections.Add(prerequisites);
        sections.Add(CallsSection(layout, package, source, entry, activation));
        // Drop windows: only for a definition with recognised flight-to-callback-911 calls (design §14); the paths are the view's Paths chip.
        if (InAnimationMode && opened && DropCandidates(definition) is { Count: > 0 } flights)
            sections.Add(new("drops", "Drop windows")
            {
                Summary = parachutes is { Analysis: { } analysis } shown && shown.Entry is int analyzed && definition.Entries.Contains(analyzed) ? AnimationNotice.Plural(analysis.Windows.Count, "1 window", "windows") : AnimationNotice.Plural(flights.Count, "1 flight", "flights"),
                Items = [new InspectorHost("drops") { Note = "VTOL drop windows: analyze, list, select, move along path, start, Apply / Revert (animation_paths without a definition)." }],
            });
        sections.Add(SourceSection(definition, "settings", model));
        string root = entry > 0 && package != null && entry < package.Entries.Count ? $" · {package.Entries[entry].RootName} #{entry}" : "";
        return new(scrollTo == "activation" ? "activation" : "definition", definition.Name, "Definition · " + System.IO.Path.GetFileName(definition.File) + root)
        {
            Glyph = "Definition", Selection = layout?.Node(scrollTo == "activation" ? AnimationGraph.ActivationId(entry) : AnimationGraph.FrameId(entry)) != null
                ? (scrollTo == "activation" ? AnimationGraph.ActivationId(entry) : AnimationGraph.FrameId(entry)) : null,
            Definition = definitionIndex, Entry = entry, Record = "settings", Path = "", ScrollTo = scrollTo, Sections = sections,
        };
    }
    private static string ActivationLabel(string key) => key switch
    {
        "ACTIVATION" => "Activation", "HEALTH" => "Health", "RESET_TIME" => "Reset time", "EXECUTION_BY_RANGE" => "Execution by range",
        "EXECUTION_BY_ZONE" => "Execution by zone", "EXECUTION_PRIORITY" => "Priority", "SAVE_LOG" => "Save log", "NETWORK_LOG" => "Network log",
        _ => SourceAnimationEdits.Caption(key),
    };
    private static string? ActivationHelp(string key) => key switch
    {
        "ACTIVATION" => "How the game starts it: when called, on startup, or after weapon or collision damage.",
        "HEALTH" => "Damage the hit activations count down before the animation starts.",
        "RESET_TIME" => "Negative: keeps its final pose. Otherwise the cleanup runs this long after a stop, restoring the rest pose. A single value also runs the cleanup at mission load. −99: one-shot.",
        "EXECUTION_BY_RANGE" => "Runs only while the player is within this distance of the attach node; its clocks freeze while out of range.",
        "EXECUTION_BY_ZONE" => "Runs only while the camera's zones allow the attach node's zone.",
        "EXECUTION_PRIORITY" => "Default 4; openings use 1 (they run before priority-4 animations).",
        _ => null,
    };

    private InspectorSection BindingsSection(SourceAnimationDefinition definition, bool opened)
    {
        var mission = scene?.Mission;
        var package = (shownDocument?.Document as SourceSceneSnapshot)?.Animations;
        List<InspectorBinding> rows = [];
        foreach (int e in definition.Entries.Take(InspectorBindings))
        {
            string root = package != null && e < package.Entries.Count ? package.Entries[e].RootName : definition.RootName;
            int? node = mission != null && mission.AnimationEntryRoots.TryGetValue(e, out int r) && r >= 0 ? r : null;
            string label = node is int n && sceneTree?.Projection is { } projection ? projection.DisplayName(n) : root;
            rows.Add(new(e, $"{label} · #{e}", node, opened && animationMode?.Entry == e, node != null));
        }
        List<InspectorItem> items = [];
        if (definition.Entries.Count == 0)
            items.Add(new InspectorText($"Not bound on {scene?.Mission?.Layout.Difficulty.ToString() ?? "this difficulty"}: {(shownDocument?.Document as SourceSceneSnapshot)?.Mission ?? "this map"} has no node named {definition.RootName}. Structure still shows the definition."));
        else
        {
            items.Add(new InspectorBindings(rows) { More = Math.Max(0, definition.Entries.Count - InspectorBindings), Choose = opened && InAnimationMode && definition.Entries.Count > 1 });
            if (opened && InAnimationMode) items.Add(new InspectorText(definition.Entries.Count > 1 ? "Animation mode plays the chosen binding." : "Animation mode plays this binding."));
        }
        return new("bindings", "Bindings") { Summary = definition.Entries.Count.ToString(CultureInfo.InvariantCulture), Items = items };
    }

    private AnimationGraphActivation? ActivationOf(AnimationGraphLayout? layout, AnimationPackage? package, SourceSceneSnapshot source, int entry)
    {
        if (entry <= 0 || package == null || entry >= package.Entries.Count) return null;
        if (layout?.Graph.Node(AnimationGraph.ActivationId(entry))?.Activation is { } shown) return shown;
        var index = TimingIndex(source, package);
        try { return AnimationGraph.Activation(index, entry); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.InvalidDataException) { return null; }
    }

    private InspectorSection? PrerequisitesSection(SourceAnimationEdits.Definition? model, AnimationGraphActivation? activation, int entry)
    {
        var records = model?.Records.Where(r => r.Kind == "prerequisites").ToArray() ?? [];
        if (records.Length == 0 && activation is not { Prerequisites.Count: > 0 }) return null;
        var marks = PrerequisiteMarks(activation, entry);
        List<InspectorItem> items = [];
        foreach (var record in records)
        {
            if (record.Fields.Count == 0) continue;
            items.Add(new InspectorText(record.Name, "body"));
            foreach (var field in record.Fields)
                items.Add(field.Key switch
                {
                    "ANIMATION_LIST" => new InspectorField(field.Id) { Label = "Animation list · one name per line", Layout = "list", Marks = field.Values.Select(v => marks.GetValueOrDefault(v) is { } m ? new InspectorMark(v, m.Mark, m.Status) : new InspectorMark(v, "·", "")).ToArray() },
                    "OBJECT_ACTIVE_LIST" => new InspectorField(field.Id) { Label = field.Label, Marks = [marks.GetValueOrDefault(string.Join('/', field.Values)) is { } m ? new InspectorMark(string.Join('/', field.Values), m.Mark, m.Status) : new(string.Join('/', field.Values), "·", "active?")] },
                    "MINIMUM_TO_SATISFY" => new InspectorField(field.Id) { Label = "Minimum to satisfy" },
                    _ => new InspectorField(field.Id),
                });
        }
        if (records.Length == 0 && activation != null)
            foreach (var group in activation.Prerequisites)
            {
                items.Add(new InspectorText(group.Header, "body"));
                foreach (var item in group.Items)
                    items.Add(marks.TryGetValue(item.Target, out var mark) ? new InspectorPair(mark.Mark, item.Target + (mark.Status.Length > 0 ? " · " + mark.Status : "")) : new InspectorPair("·", item.Target));
            }
        items.Add(new InspectorText(PrerequisiteNote(entry), "help"));
        string? summary = activation?.Prerequisites.FirstOrDefault()?.Header;
        return new("prerequisites", "Prerequisites") { Summary = summary is null ? null : Trim(summary.Replace("Prerequisites · ", "", StringComparison.Ordinal), 22), Items = items };
    }
    /// <summary>
    /// ✓/○ per prerequisite at the playhead: at the binding's latest start attempt its check there, else an animation item's
    /// state now (met unless stopped); object items show "active?" until a start evaluates them.
    /// </summary>
    private Dictionary<string, (string Mark, string Status)> PrerequisiteMarks(AnimationGraphActivation? activation, int entry)
    {
        Dictionary<string, (string, string)> marks = new(StringComparer.Ordinal);
        if (activation == null) return marks;
        var overlay = animationGraphOverlay;
        var binding = overlay?.Binding(entry);
        foreach (var item in activation.Prerequisites.SelectMany(g => g.Items))
        {
            (string, string) mark;
            if (overlay != null && binding?.Prerequisites is { } start && start.Result.Checks.FirstOrDefault(c => c.Record == item.Record) is { } check)
                mark = check.Satisfied switch { true => ("✓", "met"), false => ("○", start.Outcome == "refused" ? "unmet · refused" : start.Outcome == "ignored" ? "unmet · ignored for this start" : "unmet"), _ => ("–", "skipped · node not bound") };
            else if (item.Kind == "object") mark = ("·", "active?");
            else if (item.Entry is not int needed) mark = ("○", "unresolved");
            else if (overlay == null) mark = ("·", "not stopped?");
            else mark = overlay.Binding(needed).State != AnimationBindingLifecycle.Stopped ? ("✓", "not stopped") : ("○", "stopped");
            marks.TryAdd(item.Target, mark);
        }
        return marks;
    }
    private string PrerequisiteNote(int entry)
    {
        var scenario = worldAnimationConfiguration;
        string rule = scenario.Prerequisites switch
        {
            AnimationPrerequisiteRule.Check => "Check", AnimationPrerequisiteRule.Ignore => "Ignore", _ => "Ignore for scenario starts",
        };
        string text = $"The game checks these on every start. Scenario → Rules → Prerequisites: {rule}.";
        if (animationGraphOverlay?.Binding(entry) is { Prerequisites: { } start })
            text += start.Outcome switch
            {
                "met" => $" Met at {AnimationSeconds.Format(start.Time)}.",
                "refused" => $" Unmet at {AnimationSeconds.Format(start.Time)}: the start was refused.",
                "ignored" => $" Unmet at {AnimationSeconds.Format(start.Time)} ({start.Result.Matched} of {start.Result.Minimum}): the game refuses this start; the scenario ignored them.",
                _ => "",
            };
        return text;
    }

    private InspectorSection CallsSection(AnimationGraphLayout? layout, AnimationPackage? package, SourceSceneSnapshot source, int entry, AnimationGraphActivation? activation)
    {
        List<InspectorItem> items = [];
        if (package != null && entry > 0 && entry < package.Entries.Count)
        {
            // The shown graph when it is this binding's, else this binding's own (cached, so a refresh builds it once).
            AnimationGraph? graph = layout?.Graph.Entry == entry ? layout.Graph : GraphFor(source, null, entry);
            if (graph != null)
            {
                List<string> calls = [];
                for (int offset = 0; ;)
                {
                    var page = graph.Nodes(offset);
                    foreach (var node in page.Rows.Where(n => n.Kind == AnimationGraphNodeKind.Reference && n.Reference is { Calling: false } && n.Depth <= 1))
                        calls.Add(node.Reference!.Name + (node.Reference.EntryCount > 1 ? $" ({node.Reference.EntryCount})" : "") + (node.Reference.Unresolved ? " (unavailable)" : ""));
                    if (page.NextOffset is not int next || calls.Count >= 64) break;
                    offset = next;
                }
                items.Add(new InspectorPair("Calls", calls.Count == 0 ? "nothing" : Trim(string.Join(", ", calls), 600)));
                var callers = (graph.Node(AnimationGraph.ActivationId(entry))?.Activation ?? activation)?.Callers ?? [];
                items.Add(new InspectorPair("Called by", callers.Count == 0 ? "nothing in this map" : Trim(string.Join(", ", callers.Select(c => c.Name + (c.Bindings > 1 ? $" ({c.Bindings})" : "") + (c.BackLink != null ? " ↩ cycle" : ""))), 600)));
                var entryData = package.Entries[entry];
                int conditions = entryData.AllSequences.Sum(s => s.Events.Count(e => e.Type is 31 or 33)), loops = entryData.AllSequences.Sum(s => s.Events.Count(e => e.Type == 30));
                items.Add(new InspectorPair("Sequences", $"Cleanup + {Math.Max(0, entryData.AllSequences.Count() - 1)}" + (conditions > 0 ? $" · {AnimationNotice.Plural(conditions, "1 condition", "conditions")}" : "") + (loops > 0 ? $" · {AnimationNotice.Plural(loops, "1 loop", "loops")}" : "")));
            }
        }
        if (items.Count == 0) items.Add(new InspectorText("Calls and callers are known once the map's animations are built."));
        return new("calls", "Calls and callers") { Items = items };
    }

    private InspectorSection SourceSection(SourceAnimationDefinition definition, string? record, SourceAnimationEdits.Definition? model)
    {
        var context = record == null ? null : SourceContextFor(definition.Index, record);
        var excerpt = context?.Context?.Definition;
        InspectorSource item = new(definition.File, record)
        {
            Line = excerpt?.FirstLine, Excerpt = excerpt?.Text, Truncated = excerpt?.Truncated == true,
            Note = context?.Failure ?? context?.Context?.Note ?? (record != null && context?.Context == null ? "Loading the source excerpt…" : null),
            Missions = model?.Missions ?? [],
        };
        return new("source", "Source") { Summary = excerpt != null ? "line " + excerpt.FirstLine.ToString(CultureInfo.InvariantCulture) : null, Expanded = false, Items = [item] };
    }
    /// <summary>The source excerpt of a record, read once in the background per revision; the card refreshes when it arrives.</summary>
    private (SourceAnimationEdits.SourceContext? Context, string? Failure)? SourceContextFor(int definition, string record)
    {
        if (shownDocument is not { SourceWorld: not null } document) return null;
        if (inspectorSource is { } cached && cached.Document == document && cached.Revision == document.Revision && cached.Definition == definition && cached.Record == record)
            return (cached.Context, cached.Failure);
        inspectorSource = (document, document.Revision, definition, record, null, null);
        string? script = null;
        _ = LoadSourceAsync();
        return (null, null);
        async Task LoadSourceAsync()
        {
            try
            {
                var context = await ReadAnimationContextAsync(document, definition, record, null, document.Lifetime.Token, script);
                if (inspectorSource is { } now && now.Document == document && now.Definition == definition && now.Record == record) { inspectorSource = now with { Context = context }; RefreshAnimationInspector(); }
            }
            catch (Exception ex) when (ex is StudioCommandException or System.IO.IOException or System.IO.InvalidDataException or OperationCanceledException)
            {
                if (inspectorSource is { } now && now.Document == document && now.Definition == definition && now.Record == record && ex is not OperationCanceledException)
                { inspectorSource = now with { Failure = "The source excerpt is unavailable: " + ex.Message }; RefreshAnimationInspector(); }
            }
        }
    }

    private AnimationInspection SequenceCard(SourceSceneSnapshot source, AnimationGraph graph, InspectorTarget target)
    {
        string id = target.Graph ?? AnimationGraph.SequenceId(target.Entry, target.Sequence);
        var sequence = graph.Node(id)?.Sequence ?? throw new StudioCommandException("stale_record", "The graph has no sequence with that identity; read it again.");
        var provenance = source.AnimationProvenance?.FindSequence(sequence.Id);
        int definitionIndex = provenance?.Definition ?? source.AnimationProvenance?.FindEntry(sequence.Entry)?.Definition ?? -1;
        var model = CardModel(definitionIndex);
        var record = provenance?.RecordId is { } recordId ? model?.Records.FirstOrDefault(r => r.Id == recordId) : null;
        string name = sequence.Cleanup ? "Cleanup" : sequence.Name;
        List<InspectorItem> main = [new InspectorPair("Name", sequence.Cleanup ? "Cleanup (RESET_STATE)" : sequence.Name)];
        if (sequence.Cleanup) main.Add(new InspectorText("Runs when the animation stops or is reset" + (graph.Node(AnimationGraph.ActivationId(sequence.Entry))?.Activation?.Triggers.Any(t => t.Kind == "loadCleanup") == true ? ", and at mission load." : ".")));
        else
        {
            if (record?.Fields.FirstOrDefault(f => f.Key == "ACTIVATION") is { } waits)
                main.Add(new InspectorField(waits.Id) { Label = "Waits for release", Layout = "check", Help = "ACTIVATION ( ON_CALL ): the sequence starts only when a Release (CALL_SEQUENCE) or a bouncing motion releases it; otherwise it starts at once with the animation." });
            else main.Add(new InspectorPair("Waits for release", sequence.WaitsForRelease ? "yes · ACTIVATION ( ON_CALL )" : "no · starts at once with the animation"));
            if (sequence.IgnoredStartTime) main.Add(new InspectorText("START_TIME ignored: the game ignores START_TIME on a whole sequence.", "caution"));
        }
        List<InspectorItem> releases = [];
        foreach (var edge in graph.EdgesOf(id).Where(e => e.To == id && e.Relation is AnimationGraphRelation.Release or AnimationGraphRelation.ContactRelease).Take(16))
            releases.Add(new InspectorPair(edge.Relation == AnimationGraphRelation.ContactRelease ? "Released on contact by" : "Released by", RowWhere(graph, edge.From)) { Link = new(RowWhere(graph, edge.From), "select", edge.From) });
        foreach (var edge in graph.EdgesOf(id).Where(e => e.To == id && e.Relation == AnimationGraphRelation.StopSequence).Take(16))
            releases.Add(new InspectorPair("Stopped by", RowWhere(graph, edge.From)) { Link = new(RowWhere(graph, edge.From), "select", edge.From) });
        if (sequence.WaitsForRelease && !releases.Any(r => r is InspectorPair { Label: "Released by" or "Released on contact by" })) releases.Add(new InspectorPair("Released by", "nothing in this definition"));
        foreach (var loop in graph.Blocks(sequence.Entry, sequence.Id).Where(b => b.Loop != null).Select(b => b.Loop!))
            releases.Add(new InspectorPair("Loop", loop.Label + (loop.WaitsForRelease ? " · each pass waits for a new release" : "")));
        if (releases.Count == 0) releases.Add(new InspectorText(sequence.Cleanup ? "Stop and Reset animation run it." : "It starts at once with the animation; nothing stops it but the animation's stop or reset."));
        List<InspectorSection> sections =
        [
            new("sequence", "Sequence") { Summary = sequence.Cleanup ? "RESET_STATE" : "SEQUENCE_DEFINITION", Items = main },
            new("releases", "Releases and stops") { Items = releases },
            SequenceRunSection(id, sequence),
        ];
        if (source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == definitionIndex) is { } definition) sections.Add(SourceSection(definition, provenance?.RecordId, model));
        return new("sequence", name, sequence.Cleanup ? "Cleanup sequence" : sequence.WaitsForRelease ? "Sequence · waits for release" : "Sequence · starts at once")
        {
            Glyph = sequence.Cleanup ? "graph:cleanup" : "Structure", Selection = id, Definition = definitionIndex, Entry = sequence.Entry, Record = provenance?.RecordId,
            Path = target.Path, Sections = sections,
        };
    }
    private InspectorSection SequenceRunSection(string node, AnimationGraphSequence sequence)
    {
        var run = animationGraphOverlay?.Sequence(node);
        var frame = worldAnimationSession?.CurrentFrame ?? worldAnimationSession?.LastFrame;
        if (run == null || frame == null)
            return new("run", "Run") { Summary = frame == null ? "no run" : "not started", Live = true, Items = [new InspectorText("Play or seek to see this sequence run.")] };
        List<InspectorItem> items = [new InspectorPair("At", AnimationSeconds.Format(frame.Time)), new InspectorPair("State", run.Label)];
        if (run.Pass > 0) items.Add(new InspectorPair("Pass", run.Pass.ToString(CultureInfo.InvariantCulture)));
        if (run.ThresholdRemaining is double remaining) items.Add(new InspectorPair("Waits", $"{AnimationSeconds.Short(remaining)} more for its next event's threshold"));
        if (run.ReleasedBy is { } releaser)
        {
            string at = run.ReleasedAt is double time ? " at " + AnimationSeconds.Format(time) : "";
            var graph = animationGraphLayout?.Graph;
            items.Add(graph?.Row(releaser) != null ? new InspectorPair("Released by", RowWhere(graph, releaser) + at) { Link = new(RowWhere(graph, releaser) + at, "select", releaser) }
                : new InspectorPair("Released by", releaser + at));
        }
        if (run.NoReleaserLeft) items.Add(new InspectorText("No releaser left: nothing can release it again in this run.", "caution"));
        if (run.PausedBy != null) items.Add(new InspectorPair("Paused", run.PausedBy + " (its clocks freeze)"));
        if (run.BlockedCall != null) items.Add(new InspectorPair("Blocked", "waits for excluded call " + run.BlockedCall) { Link = run.BlockedBy is { } edge ? new("Include it", "include", edge) : null });
        if (run.Instance is long instance && frame.Sequences.FirstOrDefault(s => s.Instance == instance && s.Sequence == sequence.Id) is { } status)
        {
            items.Add(new InspectorPair("Sequence clock", AnimationSeconds.Format(status.SequenceElapsed)));
            items.Add(new InspectorPair("Animation clock", AnimationSeconds.Format(status.AnimationElapsed)));
        }
        return new("run", "Run") { Summary = run.Label, Live = true, Items = items };
    }
    private static string RowWhere(AnimationGraph graph, string rowId) => graph.Row(rowId) is { } row
        ? (graph.Node(AnimationGraph.SequenceId(row.Entry, row.Sequence))?.Label ?? "sequence") + " › " + row.Label : rowId;

    private AnimationInspection EventCard(SourceSceneSnapshot source, AnimationGraph graph, InspectorTarget target)
    {
        var row = (target.Graph != null ? graph.Row(target.Graph) : null) ?? graph.RowOf(target.Entry, target.Event)
            ?? throw new StudioCommandException("stale_record", "The graph has no row with that identity; read it again.");
        var package = source.Animations!;
        var provenance = source.AnimationProvenance?.FindEvent(row.Event);
        int definitionIndex = provenance?.Definition ?? source.AnimationProvenance?.FindEntry(row.Entry)?.Definition ?? -1;
        var model = CardModel(definitionIndex);
        var record = provenance?.RecordId is { } recordId ? model?.Records.FirstOrDefault(r => r.Id == recordId) : null;
        string sequenceName = graph.Node(AnimationGraph.SequenceId(row.Entry, row.Sequence))?.Label ?? "sequence";
        var condition = row.Role == AnimationGraphRowRole.Question ? ConditionOf(graph, row) : null;
        string kind = condition != null ? "condition" : RowKind(graph, row);
        List<InspectorSection> sections = [];
        var operands = record?.Fields.Where(f => f.Key != "START_TIME").Select(f => (InspectorItem)new InspectorField(f.Id) { Label = OperandLabel(f) }).ToList() ?? [];
        if (condition != null)
        {
            List<InspectorItem> items = [new InspectorPair("Question", condition.Question)];
            items.AddRange(operands);
            items.Add(new InspectorText(condition.Rule));
            if (record != null) items.Add(new InspectorText($"{row.Keyword} ( {string.Join(" ", record.Fields.Where(f => f.Key != "START_TIME" && f.Present).Select(f => $"{f.Key} ( {string.Join(' ', f.Values)} )"))} )", "mono"));
            sections.Add(new("condition", "Condition") { Summary = condition.Operand switch { "playerRange" => "PLAYER_RANGE", "random" => "RANDOM_WEIGHT", "effectsDetail" => "ANIMATION_LOD", "cover" or "playerCover" => "NODE_UNDERCOVER", _ => row.Keyword }, Items = items });
            sections.Add(PreviewSection(graph, row, condition));
            sections.Add(OutcomesSection(graph, row));
        }
        else
        {
            List<InspectorItem> items = [.. operands];
            if (row.Callback is { } callback) items.Add(new InspectorText($"Game callback {callback.Value}: {callback.Meaning} (preview: {callback.Preview})", "info"));
            if (row.Type == 32) items.Add(new InspectorText("The ELSE marker opens the \"if false\" branch. A taken branch that reaches it waits for its threshold, then skips to ENDIF."));
            else if (row.Type == 34) items.Add(new InspectorText("ENDIF closes the condition block."));
            else if (row.Type == 27 && row.Target != null) items.Add(new InspectorText($"Invalidate acts on one binding: the first named {row.Target}, base instance. It keeps running if it runs; every later start is refused for the rest of the mission. Loops keep running.", "info"));
            if (row.Diagnostic != null) items.Add(new InspectorText(row.Diagnostic, "caution"));
            if (items.Count == 0) items.Add(new InspectorText(record == null && model == null ? "Loading the definition's source…" : "This event has no operands to edit."));
            if (record?.Fields.Any(f => f.ReadOnly != null && f.Key != "START_TIME") == true && provenance?.ReadOnlyReason != null) items.Add(new InspectorText(provenance.ReadOnlyReason));
            sections.Add(new("event", "Event") { Summary = row.Keyword, Items = items });
            if (kind == "call") sections.Add(TargetSection(source, graph, row));
        }
        sections.Add(ThresholdSection(record, row, provenance));
        if (condition == null) sections.Add(OccurrencesSection(row, target.Mark));
        if (source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == definitionIndex) is { } definition) sections.Add(SourceSection(definition, provenance?.RecordId, model));
        string subtitle = condition != null ? $"Condition · {(condition.ElseIf ? "ELSEIF" : "IF")} · {sequenceName}" : (kind == "call" ? row.Family switch { AnimationEventFamily.Release => "Release", AnimationEventFamily.Stop => "Stop", _ => "Call" } : "Event") + " · " + sequenceName;
        string title = condition?.Question ?? row.Label;
        return new(kind, title, subtitle)
        {
            Glyph = "graph:" + AnimationGraphGlyphs.Family(row), Selection = row.Id, Definition = definitionIndex, Entry = row.Entry, Record = provenance?.RecordId, Path = target.Path,
            Sections = sections,
        };
    }
    private static string OperandLabel(SourceAnimationEdits.Field field) => field.Key switch
    {
        "PLAYER_RANGE" => "Radius", "RANDOM_WEIGHT" => "Weight (chance 0–1)", "ANIMATION_LOD" => "Effects detail level", "NODE_UNDERCOVER" => "Node and distance",
        "NAME" => "Name", "AT_NODE" => "At node", "LOOP_COUNT" => "Loop count", "LOOP_TIME" => "Loop time (s)", "RUN_TIME" => "Run time (s)", "VALUE" => "Value",
        _ => field.Label,
    };
    private static AnimationGraphCondition? ConditionOf(AnimationGraph graph, AnimationGraphRow row) =>
        graph.Blocks(row.Entry, row.Sequence).SelectMany(b => b.Branches).FirstOrDefault(b => b.Marker == row.Event)?.Condition;

    /// <summary>The switch for each call path that reaches the condition, and the game rule in words with the current inputs.</summary>
    private InspectorSection PreviewSection(AnimationGraph graph, AnimationGraphRow row, AnimationGraphCondition condition)
    {
        var scenario = worldAnimationConfiguration;
        var paths = ConditionPaths(row.Entry);
        var run = animationGraphOverlay?.Condition(row.Id);
        var frame = worldAnimationSession?.CurrentFrame ?? worldAnimationSession?.LastFrame;
        List<InspectorItem> items = [];
        // A switch for each start and included call path that reaches it; excluded paths (never run) are summed up.
        var excluded = paths.Where(p => p.Path.Length > 0 && !p.Path.StartsWith("/@", StringComparison.Ordinal) && !scenario.Includes(p.Path)).ToList();
        var running = paths.Except(excluded).ToList();
        if (running.Count == 0 && excluded.Count > 0) { running = [excluded[0]]; excluded.RemoveAt(0); }
        foreach (var (path, label) in running.Take(InspectorPaths))
        {
            var latest = frame?.Conditions.LastOrDefault(c => c.Event == row.Event && c.InstancePath == path);
            items.Add(new InspectorSwitch($"{condition.Question.TrimEnd('?')}, preview", path, row.Entry, row.Event, scenario.Choice(path, row.Entry, row.Event), label)
            { Outcome = latest == null ? null : $"{(latest.Value ? "true" : "false")} · {(latest.Forced ? "forced" : "game")} · {AnimationSeconds.Format(latest.Time)}" });
        }
        if (running.Count > InspectorPaths) items.Add(new InspectorText($"and {running.Count - InspectorPaths} more call paths reach it (animation_scenario conditions sets any of them)."));
        if (excluded.Count > 0) items.Add(new InspectorText($"{excluded.Count} excluded call path{(excluded.Count == 1 ? "" : "s")} also reach{(excluded.Count == 1 ? "es" : "")} it ({Trim(excluded[0].Label, 60)}{(excluded.Count > 1 ? ", …" : "")}): include one to give it a switch here."));
        if (paths.Count == 0) items.Add(new InspectorText("No call path of the scenario reaches this condition: the switch applies once a start or an included call reaches it."));
        if (running.Count > 1) items.Add(new InspectorText($"{running.Count} call paths run it; each has its own switch."));
        var pass = run?.Latest;
        string rule = ConditionRule(condition, row, pass, scenario);
        var forced = paths.Select(p => scenario.Choice(p.Path, row.Entry, row.Event)).FirstOrDefault(c => c != AnimationConditionChoice.Game);
        if (forced != AnimationConditionChoice.Game && paths.Count > 0) rule += $" Forced {(forced == AnimationConditionChoice.True ? "true" : "false")} for {(paths.Count == 1 ? "this call path" : "a call path")}.";
        items.Add(new InspectorText(rule, "info"));
        if (condition.Operand is "playerRange" or "playerCover") items.Add(new InspectorLink("Player…", "tab", "scenario") { Tip = "The scenario's player: at start, a custom point or no player" });
        string summary = paths.Count == 0 ? "Game" : scenario.Choice(paths[0].Path, row.Entry, row.Event) switch { AnimationConditionChoice.True => "Forced true", AnimationConditionChoice.False => "Forced false", _ => "Game" };
        return new("preview", "Preview") { Summary = summary, Live = true, Items = items };
    }
    /// <summary>The call paths whose instances run a condition of <paramref name="entry"/>: the starts of that binding, then the scenario's call paths reaching it.</summary>
    private List<(string Path, string Label)> ConditionPaths(int entry)
    {
        List<(string, string)> paths = [];
        if (animationMode is not { Entry: > 0 } mode || shownDocument?.Document.Animations is not { } package) return paths;
        if (mode.Entry == entry) paths.Add(("", $"{package.Entries[entry].Name} #{entry} (primary start)"));
        foreach (var start in worldAnimationConfiguration.Starts.Where(s => s.Id != Guid.Empty && s.Entry == entry))
            paths.Add((start.Path, $"{package.Entries[entry].Name} #{entry} (start at {AnimationSeconds.Format(start.Seconds)})"));
        try
        {
            foreach (var call in worldAnimationConfiguration.CallPaths(package).Paths.Where(p => p.Target == entry).Take(64))
                paths.Add((call.Path, $"{package.Entries[call.Caller].Name} #{call.Caller} → {package.Entries[entry].Name} #{entry}" + (call.BackLink ? " ↩" : "") + (worldAnimationConfiguration.Includes(call.Path) ? "" : " (excluded)")));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
        return paths;
    }
    /// <summary>
    /// The game rule in words with the current inputs and its result (design §10): "The player (at start, 2,010 from rlbridge) is
    /// outside 64: false." · "Seed 1, draw 3: 0.71 &gt; 0.4: false. In the game this is random (40 %)." · "Effects detail High ≥ High: true."
    /// </summary>
    private string ConditionRule(AnimationGraphCondition condition, AnimationGraphRow row, AnimationConditionPass? pass, AnimationScenario scenario)
    {
        string Result(bool value) => value ? "true" : "false";
        switch (condition.Operand)
        {
            case "playerRange":
                {
                    double radius = condition.Radius ?? 0;
                    string attach = condition.Node ?? "the attach node";
                    if (scenario.Player.Kind == AnimationPlayerReferenceKind.None && (pass == null || pass.Unavailable == "no player"))
                        return "Scenario → Player is \"No player\": every range condition is false.";
                    string source = PlayerSourceWords(animationGraphOverlay?.Player?.Source ?? scenario.Player.Kind switch { AnimationPlayerReferenceKind.Custom => "custom", _ => "aiv start" });
                    if (pass is { Unavailable: "no player" })
                        return $"The player ({source}) has no position on this map (no player start on this difficulty), so within {AnimationPhrases.Number(radius)} of {attach} is false.";
                    double? distance = pass?.Measured ?? (RangeGeometry(row.Entry) is { Center: { } c, Player: { } p } ? Vector3.Distance(c, p) : null);
                    if (distance is not double d) return $"True when the player ({source}) is within {AnimationPhrases.Number(radius)} of {attach}. The player's position is unknown on this difficulty.";
                    bool inside = pass?.Value ?? d <= radius;
                    if (pass is { Forced: true }) inside = d <= radius;
                    return $"The player ({source}, {d.ToString("N0", CultureInfo.InvariantCulture)} from {attach}) is {(d <= radius ? "inside" : "outside")} {AnimationPhrases.Number(radius)}: {Result(d <= radius)}." + (pass == null ? " (Not evaluated at the playhead yet.)" : "");
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
            case "cover" or "playerCover":
                {
                    string what = condition.Operand == "cover" ? condition.Node ?? "the node" : "the player";
                    string height = AnimationPhrases.Number(condition.CoverHeight ?? 50);
                    if (pass == null) return $"True when a segment from {what} straight up for {height} hits solid, raycastable geometry in the current zones. Not evaluated at the playhead yet.";
                    if (pass.Unavailable != null) return $"Not evaluable ({pass.Unavailable}): false.";
                    return pass.Value ? $"Something within {height} above {what}" + (pass.Measured is double up ? $" ({up.ToString("0.#", CultureInfo.InvariantCulture)} up)" : "") + ": true." : $"Nothing within {height} above {what}: false.";
                }
            default: return condition.Rule;
        }
    }
    private static string PlayerSourceWords(string source) => source switch
    {
        "aiv start" => "at start", "opening handover" => "at start, the opening's handover", "custom" => "custom point", "none" => "no player",
        "callback 10" or "callback 17" => "moved by " + source, _ => source,
    };
    /// <summary>A range test's geometry: the attach node's position, the player reference now (or as the scenario sets it) and where it came from.</summary>
    private (Vector3? Center, Vector3? Player, string Attach)? RangeGeometry(int entry)
    {
        if (animationGraphIndex?.Package is not { } package || entry <= 0 || entry >= package.Entries.Count) return null;
        Vector3? center = null;
        var binding = package.Entries[entry];
        if (scene?.Mission is { } mission && scene.PreviewScene is { } data && mission.AnimationEntryRoots.TryGetValue(entry, out int root) && root >= 0 && root < data.Nodes.Count)
        {
            int attach = binding.AttachName == binding.RootName ? root : FindBelow(data, root, binding.AttachName) ?? root;
            center = zStudio.Rendering.SceneViewport.WorldTransform(data, attach).Translation;
        }
        var scenario = worldAnimationConfiguration;
        Vector3? player = animationGraphOverlay?.Player?.Position
            ?? (scenario.Player.Kind == AnimationPlayerReferenceKind.Custom ? scenario.CustomPlayer
                : scenario.Player.Kind == AnimationPlayerReferenceKind.None ? null : scene?.Mission?.PlayerStart);
        return (center, player, binding.AttachName);
    }

    private InspectorSection OutcomesSection(AnimationGraph graph, AnimationGraphRow row)
    {
        var frame = worldAnimationSession?.CurrentFrame ?? worldAnimationSession?.LastFrame;
        var results = frame?.Conditions.Where(c => c.Event == row.Event).ToArray() ?? [];
        if (results.Length == 0)
            return new("outcomes", "Outcomes") { Summary = "none yet", Live = true, Items = [new InspectorText(frame == null ? "Play or seek: each pass that tests the condition is listed here." : "The condition has not been reached in the run.")] };
        bool paths = results.Select(r => r.InstancePath).Distinct().Skip(1).Any();
        var shown = results.TakeLast(InspectorRows).ToArray();
        int first = results.Length - shown.Length;
        var rows = shown.Select((c, i) => new InspectorTableRow(paths
            ? [(first + i + 1).ToString(CultureInfo.InvariantCulture), AnimationSeconds.Format(c.Time), c.Value ? "true" : "false", c.Forced ? "forced" : "game", c.InstancePath.Length == 0 ? "primary" : Trim(c.InstancePath, 28)]
            : [(first + i + 1).ToString(CultureInfo.InvariantCulture), AnimationSeconds.Format(c.Time), c.Value ? "true" : "false", c.Forced ? "forced" : "game"]) { Seek = AnimationSeconds.OccurrenceTime(c.Time) }).ToArray();
        List<InspectorItem> items = [new InspectorTable(paths ? ["Pass", "Time", "Value", "How", "Call path"] : ["Pass", "Time", "Value", "How"], rows) { More = first }];
        var latest = results[^1];
        var block = graph.Blocks(row.Entry, row.Sequence).FirstOrDefault(b => b.Branches.Any(br => br.Marker == row.Event));
        if (block != null)
        {
            int index = block.Branches.ToList().FindIndex(br => br.Marker == row.Event);
            var taken = latest.Value ? block.Branches[index] : block.Branches.ElementAtOrDefault(index + 1);
            if (taken != null)
                items.Add(new InspectorText($"Taken at {AnimationSeconds.Format(latest.Time)}: {taken.Label}" + (taken.Empty ? " (nothing)" : $" ({AnimationNotice.Plural(taken.Last - taken.First + 1, "1 event", "events")})") + "."));
        }
        return new("outcomes", "Outcomes") { Summary = AnimationNotice.Plural(results.Length, "1 pass", "passes"), Live = true, Items = items };
    }

    private InspectorSection TargetSection(SourceSceneSnapshot source, AnimationGraph graph, AnimationGraphRow row)
    {
        var package = source.Animations!;
        List<InspectorItem> items = [];
        var edges = graph.EdgesOf(row.Id).Where(e => e.From == row.Id && e.Relation != AnimationGraphRelation.Prerequisite).ToArray();
        string Binding(int entry) => entry > 0 && entry < package.Entries.Count
            ? $"{package.Entries[entry].RootName} · #{entry}" + (source.AnimationProvenance?.FindEntry(entry) is { } at ? " · " + System.IO.Path.GetFileName(at.File) : "") : "#" + entry.ToString(CultureInfo.InvariantCulture);
        foreach (var edge in edges)
        {
            switch (edge.Relation)
            {
                case AnimationGraphRelation.Release or AnimationGraphRelation.ContactRelease or AnimationGraphRelation.StopSequence:
                    {
                        string verb = edge.Relation == AnimationGraphRelation.StopSequence ? "Stops" : edge.Relation == AnimationGraphRelation.ContactRelease ? "Releases on ground contact" : "Releases";
                        string name = graph.Node(edge.To)?.Label ?? row.Target ?? edge.To;
                        items.Add(new InspectorPair(verb, name) { Link = new(name, "select", edge.To) });
                        if (edge.Relation != AnimationGraphRelation.StopSequence) items.Add(new InspectorText($"Releases {name} in this instance when it waits for release (Waits for release, ACTIVATION ( ON_CALL )); a loop pass waits for a new release."));
                        break;
                    }
                case AnimationGraphRelation.StopAnimation or AnimationGraphRelation.ResetAnimation or AnimationGraphRelation.InvalidateAnimation:
                    {
                        string verb = edge.Relation switch { AnimationGraphRelation.StopAnimation => "Stops", AnimationGraphRelation.ResetAnimation => "Resets", _ => "Invalidates" };
                        string resolved = edge.TargetEntries.Count > 0 ? $"{row.Target} → {Binding(edge.TargetEntries[0])}" : $"No animation named {row.Target} in this map";
                        items.Add(new InspectorPair(verb, resolved) { Link = graph.Node(edge.To) != null ? new(resolved, "select", edge.To) : null });
                        items.Add(new InspectorText("Acts on one binding: the first in entry order with that exact name, its base instance." + (edge.AlsoNamed.Count > 0 ? $" Also named {row.Target}: {string.Join(", ", edge.AlsoNamed.Take(8).Select(e => "#" + e.ToString(CultureInfo.InvariantCulture)))} (not affected)." : "")));
                        if (edge.Note != null) items.Add(new InspectorText(edge.Note, "caution"));
                        break;
                    }
                default:
                    {
                        string resolved = edge.TargetEntries.Count == 0 ? $"No animation named {row.Target} in this map"
                            : edge.TargetEntries.Count == 1 ? $"{row.Target} → {Binding(edge.TargetEntries[0])}" : $"{edge.TargetEntries.Count} calls → {Binding(edge.TargetEntries[0])} …";
                        items.Add(new InspectorPair("Resolves to", resolved) { Link = graph.Node(edge.To) != null ? new(resolved, "select", edge.To) : null });
                        items.Add(new InspectorPair("Waits", edge.Wait ? "yes: the calling event runs while the call lives (WAIT_FOR_COMPLETION)" : "no"));
                        if (edge.Local) items.Add(new InspectorPair("Local child", "stopped when its caller stops (LOCAL_NAME)"));
                        if (edge.BackLink) items.Add(new InspectorText("A call back to an animation that is running or invalidated is refused; including it shows the refusal.", "info"));
                        items.Add(IncludeItem("Included in preview", edge.Paths, edge.BackLink));
                        break;
                    }
            }
        }
        if (row.Diagnostic != null) items.Add(new InspectorText(row.Diagnostic, "caution"));
        return new("target", "Target") { Summary = row.Target, Live = true, Items = items };
    }
    /// <summary>"Included in preview" for call paths: the scenario's inclusion, and how many paths below them excluding takes with it.</summary>
    private InspectorInclude IncludeItem(string label, IReadOnlyList<string> paths, bool backLink)
    {
        var scenario = worldAnimationConfiguration;
        if (paths.Count == 0) return new(label, paths, null, 0, "This call's frame is reached only by name: its call paths are listed in the Scenario tab.") { Enabled = false };
        int included = paths.Count(scenario.Includes);
        int below = 0;
        if (shownDocument?.Document.Animations is { } package)
            try { below = scenario.CallPaths(package).Paths.Count(p => paths.Any(path => p.Path.StartsWith(path + "/", StringComparison.Ordinal))); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
        bool? value = included == 0 ? false : included == paths.Count ? true : null;
        string help = (paths.Count == 1 ? "One call path." : $"{paths.Count} call paths ({included} included).")
            + (below > 0 ? $" Excluding also excludes the {below} call path{(below == 1 ? "" : "s")} below {(paths.Count == 1 ? "it" : "them")}." : "")
            + (backLink ? " A back-link starts excluded." : "");
        return new(label, paths, value, below, help);
    }

    private InspectorSection ThresholdSection(SourceAnimationEdits.Record? record, AnimationGraphRow row, AnimationEventSource? provenance)
    {
        var field = record?.Fields.FirstOrDefault(f => f.Key == "START_TIME");
        bool present = field?.Present ?? !(row.Clock == 1 && row.Threshold == 0);
        string clock = field is { Present: true, Values.Length: 2 } ? field.Values[0] : row.Clock switch { 2 => "SEQUENCE_OFFSET", 3 => "EVENT_OFFSET", _ => "ANIMATION_OFFSET" };
        double seconds = field is { Present: true, Values.Length: 2 } && double.TryParse(field.Values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) ? parsed : row.Threshold;
        string? readOnly = provenance == null ? "This event has no source record in this map." : provenance.ReadOnlyReason ?? field?.ReadOnly ?? (provenance.TimingFieldId == null ? "This event's start threshold cannot be edited." : null);
        string sentence = present ? char.ToUpperInvariant(AnimationPhrases.ThresholdWords(row.Clock, (float)seconds)[0]) + AnimationPhrases.ThresholdWords(row.Clock, (float)seconds)[1..] + ". Earlier events, waits and conditions can delay it."
            : "Without START_TIME an event may start as soon as the sequence reaches it.";
        string? observed = null;
        if (animationTiming is { } timing)
        {
            var marks = timing.AllLanes.SelectMany(l => l.Marks).Where(m => m.Event == row.Event).ToArray();
            var time = worldAnimationSession?.State.Time ?? 0;
            var latest = marks.Where(m => m.Start <= time + 1e-9).MaxBy(m => m.Start) ?? marks.MinBy(m => m.Start);
            if (latest != null)
            {
                observed = "Observed: " + AnimationSeconds.Format(latest.Start);
                if (latest.Bracket is { } bracket)
                {
                    string anchor = bracket.Clock switch { 2 => "its sequence's start", 3 => "the previous event", _ => "its animation's start" };
                    observed += $" ({AnimationSeconds.Format(latest.Start - bracket.Anchor)} after {anchor})" + (bracket.Gap is { } gap ? "; later than its threshold: " + gap : "");
                }
                observed += ".";
            }
        }
        // The row's threshold note when it says more than the sentence (what the game does with it: "ignored after a false IF").
        string? note = row.ThresholdNote is { } said && !string.Equals(said, AnimationPhrases.ThresholdWords(row.Clock, row.Threshold), StringComparison.OrdinalIgnoreCase) ? char.ToUpperInvariant(said[0]) + said[1..] + "." : null;
        InspectorThreshold threshold = new(field?.Id ?? provenance?.TimingFieldId, present, clock, seconds) { ReadOnly = readOnly, Sentence = sentence, Observed = observed, Note = note };
        return new("threshold", "Start threshold") { Summary = present ? row.ThresholdBadge ?? "anim ≥ 0 s" : "none", Items = [threshold] };
    }

    private InspectorSection OccurrencesSection(AnimationGraphRow row, string? selectedMark)
    {
        if (animationTiming is not { } timing || worldAnimationSession?.CurrentFrame == null && worldAnimationSession?.LastFrame == null)
            return new("occurrences", "Occurrences") { Summary = "0", Live = true, Items = [new InspectorText("Play or seek: each time the event runs is listed here.")] };
        var marks = timing.AllLanes.SelectMany(l => l.Marks).Where(m => row.Members.Count > 1 ? row.Members.Contains(m.Event) : m.Event == row.Event).OrderBy(m => m.Start).ToArray();
        if (marks.Length == 0)
            return new("occurrences", "Occurrences") { Summary = "0", Live = true, Items = [new InspectorTable(["#", "Shared clock", "Own clock", "Result"], []) { Empty = "No occurrence in the evaluated run." }] };
        var shown = marks.Take(InspectorRows).ToList();
        if (selectedMark != null && marks.FirstOrDefault(m => m.Id == selectedMark) is { } selected && !shown.Contains(selected)) shown.Add(selected);
        var rows = shown.Select(m => new InspectorTableRow([(Array.IndexOf(marks, m) + 1).ToString(CultureInfo.InvariantCulture), AnimationSeconds.Format(m.Start),
            m.Bracket is { } b ? "+" + AnimationSeconds.Format(m.Start - b.Anchor) : "", Trim(m.Result.Length > 0 ? m.Result : m.Kind.ToString().ToLowerInvariant(), 40)])
            { Seek = AnimationSeconds.OccurrenceTime(m.Start), Selected = m.Id == selectedMark }).ToArray();
        return new("occurrences", "Occurrences") { Summary = marks.Length.ToString("N0", CultureInfo.InvariantCulture), Live = true,
            Items = [new InspectorTable(["#", "Shared clock", "Own clock", "Result"], rows) { More = marks.Length - shown.Count }] };
    }

    private AnimationInspection ReferenceCard(SourceSceneSnapshot source, AnimationGraphLayout layout, string id)
    {
        var graph = layout.Graph;
        var reference = graph.Node(id)?.Reference ?? throw new StudioCommandException("stale_record", "The graph has no reference node with that identity; read it again.");
        List<InspectorItem> animation = [];
        if (reference.Detail.Length > 0) animation.Add(new InspectorText(reference.Detail, "body"));
        if (reference.Summary.Length > 0) animation.Add(new InspectorText(reference.Summary, "body"));
        if (reference.Calls.Count > 0) animation.Add(new InspectorPair("Calls", Trim(string.Join(", ", reference.Calls.Select(c => c.Name + (c.Events > 1 ? $" ({c.Events})" : "") + (c.BackLink ? " ↩" : ""))), 400)));
        if (reference.NeededBy.Count > 0) animation.Add(new InspectorPair("Needed by", string.Join(", ", reference.NeededBy.Take(8))));
        if (reference.Caution != null) animation.Add(new InspectorText(reference.Caution, "caution"));
        List<InspectorLink> actions = [];
        if (reference.Entries.Count > 0 && !reference.Unresolved) actions.Add(new("Open", "open", id) { Primary = true, Tip = "Open it in Animation mode (Ctrl+Enter); Back returns" });
        if (reference.Expandable && !reference.Unresolved) actions.Add(new(reference.Expanded ? "Collapse" : "Expand here", "expand", id) { Tip = "Show its frame to the right (Enter); presentation only" });
        if (actions.Count > 0) animation.Add(new InspectorActions(actions));
        List<InspectorItem> paths = [];
        foreach (var edge in graph.EdgesOf(id).Where(e => e.To == id && e.Relation is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild).Take(16))
            paths.Add(IncludeItem(RowWhere(graph, edge.From) + (edge.Count > 1 ? $" (×{edge.Count})" : ""), edge.Paths, edge.BackLink));
        if (paths.Count == 0) paths.Add(new InspectorText(reference.Calling ? "It calls the opened animation; its call paths belong to its own starts." : "No call shown in the graph reaches it by a call path."));
        else paths.Add(new InspectorText("Excluding a path also excludes the paths below it."));
        var run = animationGraphOverlay?.Reference(id);
        List<InspectorItem> runItems = run == null ? [new InspectorText("No instance yet.")]
            : [new InspectorText(run.Label, "body"), .. run.States.Select(s => (InspectorItem)new InspectorPair(AnimationRunOverlay.Label(s.State, null), AnimationNotice.Plural(s.Bindings, "1 binding", "bindings")))];
        int definition = reference.Entries.Count > 0 ? source.AnimationProvenance?.FindEntry(reference.Entries[0])?.Definition ?? -1 : -1;
        return new("reference", reference.Name, reference.Unresolved ? "Unavailable" : reference.Calling ? "Calling animation" : "Called animation")
        {
            Glyph = "Definition", Selection = id, Definition = definition, Entry = reference.Entries.FirstOrDefault(-1), Path = "",
            Sections =
            [
                new("animation", "Animation") { Summary = reference.EntryCount > 1 ? AnimationNotice.Plural(reference.EntryCount, "1 binding", "bindings") : null, Items = animation },
                new("callPaths", "Call paths") { Live = true, Items = paths },
                new("run", "Run") { Summary = run?.Instances > 0 ? AnimationNotice.Plural(run.Instances, "1 instance", "instances") : null, Live = true, Items = runItems },
            ],
        };
    }

    /// <summary>The card's source model when it shows <paramref name="definition"/> of the shown map at its revision.</summary>
    private SourceAnimationEdits.Definition? CardModel(int definition) =>
        sectionCard is { AnimationModel: { } model } card && model.Index == definition && card.Document == shownDocument && card.ModelRevision == shownDocument?.Revision ? model : null;
    private static string Trim(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    // ---------------------------------------------------------------- the map layout's definition card

    /// <summary>
    /// The map layout's read-mostly definition card (design §6): Open in Animation mode, Bindings, an Activation summary, Calls
    /// and callers, and Source. No editing and no footer: the fields move to Animation mode's Inspector.
    /// </summary>
    private AnimationInspection? BuildDefinitionSummary()
    {
        if (SelectedAnimationDefinition() is not { } selected || shownDocument?.Document is not SourceSceneSnapshot source) return null;
        var definition = selected.Definition;
        var package = source.Animations;
        int entry = sourceAnimationEntry is int chosen && definition.Entries.Contains(chosen) ? chosen : definition.Entries.FirstOrDefault(-1);
        var model = CardModel(definition.Index);
        var activation = package == null ? null : ActivationOf(null, package, source, entry);
        List<InspectorItem> activationItems = [];
        if (activation != null)
        {
            string starts = activation.Triggers.FirstOrDefault(t => t.Kind is "whenCalled" or "onStartup" or "newGameStart" or "weaponHit" or "collisionHit" or "weaponOrCollisionHit")?.Phrase ?? "When called";
            activationItems.Add(new InspectorPair("Starts", starts + (activation.Callers.Count > 0 ? " · by " + string.Join(", ", activation.Callers.Take(3).Select(c => c.Name + (c.Bindings > 1 ? $" ({c.Bindings})" : ""))) : "")));
            if (activation.Prerequisites.Count > 0) activationItems.Add(new InspectorPair("Prerequisites", string.Join("; ", activation.Prerequisites.Select(g => g.Header.Replace("Prerequisites · ", "", StringComparison.Ordinal)))));
            string reset = string.Join(" · ", activation.Triggers.Where(t => t.Kind is "poseKept" or "cleansUp" or "oneShot" or "loadCleanup").Select(t => t.Kind == "loadCleanup" ? "cleanup at load" : t.Phrase.ToLowerInvariant()));
            if (reset.Length > 0) activationItems.Add(new InspectorPair("Reset", reset));
            foreach (var gate in activation.Triggers.Where(t => t.Kind is "rangeGate" or "zoneGate" or "priority")) activationItems.Add(new InspectorPair(gate.Kind == "priority" ? "Priority" : "Gate", gate.Phrase));
        }
        else activationItems.Add(new InspectorPair("Starts", definition.Activation switch { "ON_CALL" => "When called", "ON_STARTUP" => "On startup", null => "Weapon hit", var other => other }));
        List<InspectorItem> open = [];
        if (definition.Entries.Count > 0) open.Add(new InspectorLink("Open in Animation mode", "open", entry.ToString(CultureInfo.InvariantCulture)) { Primary = true, Tip = "Open this definition in Animation mode (Enter on its World tree row). The camera stays where it is; nothing plays until you play it." });
        List<InspectorSection> sections =
        [
            new("open", "") { Items = open },
            BindingsSection(definition, false),
            new("activation", "Activation") { Summary = activation?.Triggers.FirstOrDefault()?.Phrase is { } phrase ? Trim(phrase, 24) : null, Items = activationItems },
            CallsSection(null, package, source, entry, activation),
            SourceSection(definition, model == null ? null : "settings", model),
        ];
        return new("map", definition.Name, "Definition · " + System.IO.Path.GetFileName(definition.File))
        { Glyph = "Definition", Definition = definition.Index, Entry = entry, Record = "settings", Path = "", Sections = sections };
    }

    private string? animationTransportNote;
    /// <summary>
    /// A status message of the run (path progress, audio): in Animation mode the transport's status line says it (a source
    /// map's status bar shows no messages); a later run or seek replaces it.
    /// </summary>
    private void TransportNote(string note)
    {
        ViewModel.Status = note;
        if (!InAnimationMode) return;
        animationTransportNote = JsonData.ShownText(note, 240);
        RefreshWorldAnimationRun(true);
    }

    // ---------------------------------------------------------------- the card's links, the pane header

    /// <summary>A card's link or button: the same operations the graph, the transport and the scenario use.</summary>
    private async Task InspectorLinkAsync(InspectorLink link)
    {
        switch (link.Action)
        {
            case "select": await SelectAnimationGraphAsync(link.Target, CancellationToken.None); break;
            case "object":
                if (int.TryParse(link.Target, NumberStyles.Integer, CultureInfo.InvariantCulture, out int node) && sceneTree?.Reveal(node) is { Selectable: true } row
                    && await ResolveSelectionDraftsAsync(sceneTreeDocument)) SelectSceneTreeRow(row);
                break;
            case "open":
                if (link.Target.StartsWith("ref:", StringComparison.Ordinal)) await GraphOpenAsync(link.Target);
                else if (int.TryParse(link.Target, NumberStyles.Integer, CultureInfo.InvariantCulture, out int entry) && SelectedAnimationDefinition() is { } definition && sceneTreeDocument is { } document)
                    await OpenAnimationFromGuiAsync(document, definition.Definition.Index, entry > 0 ? entry : null);
                break;
            case "expand":
                if (animationGraphLayout?.Node(link.Target)?.Node.Reference is { } reference) GraphExpand(link.Target, !reference.Expanded);
                break;
            case "seek":
                if (double.TryParse(link.Target, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) await WorldAnimationTransportAsync("seek", seconds);
                break;
            case "tab": ShowAnimationRightTab(link.Target); break;
            case "include": GraphInclude(link.Target, true); break;
            case "frame":
                if (int.TryParse(link.Target, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bound) && scene?.Mission is { } mission && mission.AnimationEntryRoots.TryGetValue(bound, out int root) && root >= 0
                    && !scene.TryFrameNodes([root])) ViewModel.Status = "Nothing shown to frame.";
                break;
            case "diagnostics": animationDiagnostics = true; RememberAnimationLayout(); ArrangeWorkspace(); break;
        }
    }

    /// <summary>Animation mode's right pane header (above Inspector | Scenario): the selection's tile, name and kind, Frame and Close.</summary>
    private void RefreshAnimationPaneHeader()
    {
        bool mode = InAnimationMode && sourceChromeActive && !presentationUiHidden;
        AnimationPaneHeader.Visibility = mode ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (sectionCard != null) sectionCard.TitleShown = !mode;
        if (dockedCard != null) dockedCard.TitleShown = !mode;
        if (!mode) return;
        string title, subtitle, glyph; System.Windows.Media.Brush? brush = null;
        bool scenarioTab = animationRightTab == "scenario";
        if (scenarioTab)
        {
            title = "Scenario"; glyph = "Scenario";
            subtitle = "Kept for " + ((shownDocument?.Document as SourceSceneSnapshot)?.Mission ?? "this map") + " · changes re-evaluate to the playhead";
        }
        else if (SectionSelection != null && sectionCard?.Entry != null)
        {
            title = sectionCard.HeaderTitle; subtitle = sectionCard.HeaderSubtitle; glyph = sectionCard.HeaderGlyph;
            if (sectionCard.Entry is DefinitionEntry definition && !glyph.StartsWith("graph:", StringComparison.Ordinal)) brush = OutlinerKinds.SectionBrush(definition);
        }
        else if (dockedCard is { } card) { (title, subtitle) = card.Header; glyph = "Cube"; }
        else { title = "Nothing selected"; subtitle = "Select a node, row or mark"; glyph = "Pointer"; }
        AnimationPaneTitle.Text = title; AnimationPaneTitle.ToolTip = title;
        AnimationPaneSubtitle.Text = subtitle; AnimationPaneSubtitle.ToolTip = subtitle;
        System.Windows.FrameworkElement icon;
        if (glyph.StartsWith("graph:", StringComparison.Ordinal))
        {
            var palette = AnimationGraphPalette.For(!OutlinerKinds.LightTheme);
            string name = glyph["graph:".Length..];
            var graphIcon = new AnimationGraphGlyph { Glyph = name, Size = 18, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            System.Windows.Documents.TextElement.SetForeground(graphIcon, name switch
            {
                "state" => palette.State, "motion" or "key" => palette.Motion, "camera" => palette.Camera, "sound" => palette.Sound, "effect" => palette.Effect,
                "release" => palette.Release, "stop" or "invalidate" => palette.Stop, _ => palette.T2,
            });
            icon = graphIcon;
        }
        else
        {
            var preview = new PreviewIcon { Kind = glyph, Width = 18, Height = 18, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            if (brush != null) preview.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, brush);
            else preview.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            icon = preview;
        }
        AnimationPaneTile.Child = icon;
        System.Windows.Automation.AutomationProperties.SetName(AnimationPaneHeader, title + ", " + subtitle);
        AnimationPaneFrame.Visibility = AnimationPaneClose.Visibility = scenarioTab ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }
    private void AnimationPaneCloseClick(object sender, System.Windows.RoutedEventArgs e) => _ = ReportUi(() => ClearAnimationSelectionAsync(CancellationToken.None));
    private void AnimationPaneFrameClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (scene is not { } viewport) return;
        int[] nodes = animationGraphOutline is { Length: > 0 } named ? named : SectionSelection == null && selectedNode is int node ? [node]
            : animationMode is { } mode && sceneTree?.Sections?.DefinitionRoots.GetValueOrDefault(mode.Definition) is { } roots ? [.. roots.Take(4096)] : [];
        if (nodes.Length == 0 || !viewport.TryFrameNodes(nodes)) ViewModel.Status = "Nothing shown to frame.";
    }
    /// <summary>
    /// Clears Animation mode's selection (Esc after every other use of it, the header's ✕): the graph's and Timing's
    /// selection and a picked object go, and the Inspector shows the opened definition again.
    /// </summary>
    private async Task ClearAnimationSelectionAsync(CancellationToken token)
    {
        if (animationMode is not { } mode) return;
        animationGraphSelection = null; animationGraphOutline = null; animationInspected = null;
        animationGraphView?.Select(null); animationTimingView?.Select(null);
        // An open draft keeps its card (switching the card's definition would drop it): only the selection goes.
        if (mode.Document == shownDocument && !HasSectionDraft() && (SectionSelection == null || SelectedAnimationDefinition()?.Definition.Index != mode.Definition))
            await EnsureAnimationCardAsync(mode.Document, mode.Definition, mode.Entry > 0 ? mode.Entry : null, token, preserveTimeline: true);
        ApplySectionPresentation();
        RefreshAnimationInspector();
    }

    /// <summary>
    /// scene_inspect's animations of an inspected object (its card's sections): the first 8 animations affecting it with the
    /// total (scene_tree animationUses pages them all) and, in Animation mode, the opened definition's events naming it.
    /// </summary>
    private object? InspectionAnimations(zStudio.Rendering.SceneViewport viewport, zStudio.Rendering.SceneInspection item)
    {
        if (viewport != scene || sceneTree?.Projection is not { } projection || sceneTree.Sections is not { } sections || shownDocument?.SourceWorld == null) return null;
        int node = projection.Owner(item.SelectionNode >= 0 ? item.SelectionNode : item.Node);
        var uses = projection.AnimationUses.GetValueOrDefault(node);
        var links = uses == null ? [] : AnimationLinks(sections, uses);
        var naming = InAnimationMode ? GraphRowsNaming(item.SelectionNode >= 0 ? item.SelectionNode : node) : null;
        if (links.Length == 0 && naming is not { Count: > 0 }) return null;
        return new
        {
            total = links.Length, truncated = uses?.Truncated == true,
            affecting = links.Take(MissionAnimationUses.ShownNames).Select(l => new { definition = l.Definition.Definition.Index, entry = l.Entry, name = l.Name, reason = l.Reason }).ToArray(),
            eventsNaming = naming?.Take(64).Select(r => new { row = r.Id, entry = r.Entry, label = r.Label }).ToArray(), eventsNamingCount = naming?.Count,
        };
    }

    // ---------------------------------------------------------------- notices

    /// <summary>The notices of the opened animation on the view bar's chip, and their badges on graph nodes and Timing lanes.</summary>
    private void RefreshAnimationNotices()
    {
        var notices = AnimationNotices();
        animationPanel?.SetNotices(notices);
        var (nodes, lanes) = NoticeBadges(notices);
        animationGraphView?.SetNotices(nodes);
        animationTimingView?.SetNotices(lanes);
    }
    /// <summary>A notice's Go to: its row or node in the graph, else its lane in Timing (shown if hidden).</summary>
    private async Task GoToNoticeAsync(AnimationNotice notice)
    {
        var layout = CurrentAnimationGraphLayout();
        string? graph = notice.Row is { } row && layout?.Item(row) != null ? row : notice.Node is { } node && layout?.Node(node) != null ? node : null;
        if (graph != null)
        {
            if (animationPanel?.ShownView == "timing") { animationView = "structure"; ArrangeWorkspace(); }
            await SelectAnimationGraphAsync(graph, CancellationToken.None);
        }
        else if (notice.Lane is { } lane)
        {
            if (animationPanel?.ShownView == "structure") { animationView = "timing"; ArrangeWorkspace(); }
            await SelectTimingLaneAsync(lane, CancellationToken.None);
        }
    }

    private (string Key, string State)? announced;
    /// <summary>
    /// Run-state changes are announced politely for the selected item only (design §17): its state at the playhead, when it
    /// changed while the same item stays selected.
    /// </summary>
    private void Announce(AnimationInspection inspection)
    {
        string? state = inspection.Sections.FirstOrDefault(s => s.Id is "run" or "outcomes")?.Summary;
        if (state == null) { announced = null; return; }
        if (announced is { } previous && previous.Key == inspection.Key && previous.State != state
            && System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(AnimationPaneHeader) is { } peer)
            peer.RaiseNotificationEvent(System.Windows.Automation.AutomationNotificationKind.Other, System.Windows.Automation.AutomationNotificationProcessing.MostRecent,
                $"{inspection.Title}: {state}", "animation-selection");
        announced = (inspection.Key, state);
    }

    /// <summary>
    /// Every notice of the opened animation (design §15) at the playhead: the run's preview diagnostics about its bindings,
    /// refused and prerequisite-ignored starts, unavailable calls and sequence diagnostics of the graph, and preview limits.
    /// Each names the graph node and Timing lane its badge counts on.
    /// </summary>
    private IReadOnlyList<AnimationNotice> AnimationNotices()
    {
        if (!InAnimationMode || CurrentAnimationGraphLayout() is not { } layout) return [];
        var frame = worldAnimationSession?.CurrentFrame ?? worldAnimationSession?.LastFrame;
        var scenario = worldAnimationConfiguration;
        if (animationNoticeCache is { } cached && ReferenceEquals(cached.Frame, frame) && cached.Graph == layout.Graph.Id && ReferenceEquals(cached.Scenario, scenario) && cached.Toggles == animationTimingToggles)
            return cached.Notices;
        var computed = NoticesFor(layout, frame, animationTiming);
        animationNoticeCache = (frame, layout.Graph.Id, scenario, animationTimingToggles, computed);
        return computed;
    }
    /// <summary>The notices of a shown graph's items and the run (Animation mode's and the compiled preview's chip, badges and cards).</summary>
    private static IReadOnlyList<AnimationNotice> NoticesFor(AnimationGraphLayout layout, AnimationFrame? frame, AnimationTiming? timing)
    {
        var graph = layout.Graph;
        List<AnimationNotice> notices = [];
        HashSet<int> shown = [];
        foreach (var placement in layout.Nodes)
        {
            if (placement.Node.Entry is int e) shown.Add(e);
            if (placement.Node.Reference is { } reference) foreach (int member in reference.Entries) shown.Add(member);
        }
        string? NodeOf(int entry, Guid? sequence = null)
        {
            if (sequence is Guid s && layout.Node(AnimationGraph.SequenceId(entry, s)) != null) return AnimationGraph.SequenceId(entry, s);
            if (layout.Node(AnimationGraph.ActivationId(entry)) != null) return AnimationGraph.ActivationId(entry);
            if (layout.Node(AnimationGraph.FrameId(entry)) != null) return AnimationGraph.FrameId(entry);
            return layout.Nodes.FirstOrDefault(n => n.Node.Reference?.Entries.Contains(entry) == true)?.Id;
        }
        string? LaneOf(int entry, Guid? sequence = null) => timing == null ? null : sequence is Guid s
            ? timing.AllLanes.FirstOrDefault(l => l.Kind == AnimationTimingLaneKind.Sequence && l.Sequences.Any(q => q.Entry == entry && q.Sequence == s))?.Id
              ?? timing.AllLanes.FirstOrDefault(l => l.Kind == AnimationTimingLaneKind.Group && l.Members.Contains(entry))?.Id
            : timing.AllLanes.FirstOrDefault(l => l.Kind == AnimationTimingLaneKind.Group && l.Members.Contains(entry))?.Id;
        // Static: what the graph knows about the shown sequences, rows and references.
        foreach (var placement in layout.Nodes)
        {
            var node = placement.Node;
            if (node.Sequence is { Diagnostics.Count: > 0 } sequence)
                foreach (string text in sequence.Diagnostics.Take(4))
                    notices.Add(new($"seq:{node.Id}:{notices.Count}", "support", "Warning", $"{node.Label}: {text}") { Node = node.Id, Entry = sequence.Entry, Sequence = sequence.Id, Lane = LaneOf(sequence.Entry, sequence.Id), Category = "Support" });
            if (node.Reference is { Unresolved: true } unresolved)
                notices.Add(new($"ref:{node.Id}", "unavailable", "Warning", unresolved.Caution ?? $"No animation named {unresolved.Name} in this map: calls to it do nothing.") { Node = node.Id, Category = "Resource" });
        }
        if (frame != null)
        {
            // Map-wide preview problems (the mission's initialization, resources) stay in Diagnostics; a note the player repeats for
            // every sequence of a binding is one notice on the binding; unmet prerequisites are the structured notice below.
            var issues = frame.Issues.Where(i => i.Category != "Resource" && i.Source.Entry > 0 && shown.Contains(i.Source.Entry)
                    && !i.Message.Contains("activation prerequisites are unmet", StringComparison.Ordinal))
                .GroupBy(i => (i.Source.Entry, i.Category, i.Message))
                .Select(g => g.Count() == 1 ? g.First() : g.First() with { Source = new(g.Key.Entry), Count = g.Sum(i => i.Count) });
            foreach (var issue in issues.Take(200))
            {
                bool generic = GenericApproximation(issue.Category, issue.Severity, issue.Message);
                string kind = generic ? "approximation" : issue.Message.Contains("omitted", StringComparison.Ordinal) || issue.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) ? "limit"
                    : issue.Category == "Support" ? issue.Message.Contains("approximat", StringComparison.OrdinalIgnoreCase) ? "approximation" : "support" : "preview";
                int entry = issue.Source.Entry;
                string? row = issue.Source.Event is Guid ev && entry > 0 ? graph.RowOf(entry, ev)?.Id : null;
                notices.Add(new($"issue:{notices.Count}", kind, issue.Severity, JsonData.ShownText(issue.Message, 400))
                {
                    Node = entry > 0 ? NodeOf(entry, issue.Source.Sequence) : null, Row = row, Lane = entry > 0 ? LaneOf(entry, issue.Source.Sequence) : null,
                    Entry = entry > 0 ? entry : null, Sequence = issue.Source.Sequence, Event = issue.Source.Event, Time = issue.FirstTime, Count = issue.Count, Category = issue.Category,
                    Generic = generic,
                });
            }
            foreach (var group in frame.Refusals.Where(r => shown.Contains(r.Entry)).GroupBy(r => r.Entry))
            {
                var last = group.Last();
                notices.Add(new($"refused:{group.Key}", "refused", "Warning", $"Refused start of {last.Name} #{last.Entry}: {AnimationRunOverlay.Refusal(last)}" + (last.ScenarioStart ? " (a scenario start)" : ""))
                { Node = NodeOf(group.Key), Lane = LaneOf(group.Key), Entry = group.Key, Time = last.Time, Count = group.Count(), Category = "Preview" });
            }
            foreach (var group in frame.PrerequisiteStarts.Where(p => p.Outcome == "ignored" && shown.Contains(p.Entry)).GroupBy(p => p.Entry))
            {
                var last = group.Last();
                string needed = last.Result.Minimum > 0 ? $"{last.Result.Matched} of {last.Result.Minimum}" : "unmet";
                notices.Add(new($"ignored:{group.Key}", "ignoredPrerequisites", "Information", $"Prerequisites ignored for {last.Name} #{last.Entry} ({needed}): the game refuses this start; the scenario's rule let it run")
                { Node = layout.Node(AnimationGraph.ActivationId(group.Key)) != null ? AnimationGraph.ActivationId(group.Key) : NodeOf(group.Key), Lane = LaneOf(group.Key), Entry = group.Key, Time = last.Time, Count = group.Count(), Category = "Preview" });
            }
            if (frame.TraceDropped > 0)
                notices.Add(new("limit:trace", "limit", "Information", $"The run keeps the latest occurrences: {frame.TraceDropped:N0} older ones were discarded.") { Category = "Preview" });
            if (frame.PhasesDropped > 0)
                notices.Add(new("limit:phases", "limit", "Information", $"The run keeps the latest sequence state changes: {frame.PhasesDropped:N0} older ones were discarded, so Timing's earliest bands may be missing.") { Category = "Preview" });
            if (frame.ConditionsDropped > 0)
                notices.Add(new("limit:conditions", "limit", "Information", $"The run keeps the latest condition outcomes: {frame.ConditionsDropped:N0} older ones were discarded.") { Category = "Preview" });
        }
        // The same note on several bindings of one node (a called group's members) is one notice there.
        IReadOnlyList<AnimationNotice> result = notices.GroupBy(n => (n.Node ?? n.Id, n.Kind, n.Text))
            .Select(g => g.Count() == 1 ? g.First() : g.First() with { Count = g.Sum(n => n.Count) }).ToList().AsReadOnly();
        return result;
    }
    /// <summary>The ⚠ counts the graph's nodes and Timing's lanes show.</summary>
    private (IReadOnlyDictionary<string, int> Nodes, IReadOnlyDictionary<string, int> Lanes) NoticeBadges(IReadOnlyList<AnimationNotice> notices)
    {
        Dictionary<string, int> nodes = new(StringComparer.Ordinal), lanes = new(StringComparer.Ordinal);
        foreach (var notice in notices)
        {
            // Badges count what concerns an item; the preview's generic approximations are grouped on the definition's card.
            if (notice.Generic) continue;
            if (notice.Node is { } node) nodes[node] = nodes.GetValueOrDefault(node) + 1;
            if (notice.Lane is { } lane) lanes[lane] = lanes.GetValueOrDefault(lane) + 1;
        }
        return (nodes, lanes);
    }
}
