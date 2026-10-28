using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// Animation mode's Structure view (design/animation-redesign.md §8): the opened binding's graph, its layout and presentation
/// (expanded called animations per definition for the session, collapsed and opened sequences, Show all, listed group
/// members), the run overlay at the playhead, and the selection that drives the Inspector, the 3D outlines and Timing. The GUI
/// and MCP animation_graph share every operation here; condition switches and call-port inclusion go through the scenario
/// operations (<see cref="ScenarioCondition"/>, <see cref="ScenarioInclude"/>), so a change stops playback and re-evaluates the
/// run up to the playhead.
/// </summary>
public partial class MainWindow
{
    private AnimationGraphView? animationGraphView;
    private AnimationGraphIndex? animationGraphIndex;
    private AnimationGraph? animationGraph;
    private AnimationGraphLayout? animationGraphLayout;
    private string? animationGraphError;
    /// <summary>The called animations expanded per definition, for the session (expansion is presentation; it never includes or runs anything).</summary>
    private readonly Dictionary<int, HashSet<string>> animationGraphExpanded = [];
    private readonly Dictionary<int, GraphPresentation> animationGraphPresentation = [];
    private AnimationRunOverlay? animationGraphOverlay;
    private AnimationFrame? animationGraphOverlayFrame;
    private AnimationScenario? animationGraphOverlayScenario;
    private readonly System.Diagnostics.Stopwatch animationGraphClock = System.Diagnostics.Stopwatch.StartNew();
    private string? animationGraphSelection;
    /// <summary>The nodes the graph selection names, outlined in the view instead of the definition's roots.</summary>
    private int[]? animationGraphOutline;
    private IReadOnlySet<string> animationGraphHighlight = new HashSet<string>(StringComparer.Ordinal);
    private bool animationGraphRunOverlay = true;

    private sealed class GraphPresentation
    {
        public HashSet<string> Collapsed { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Opened { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ShowAll { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ExpandedRows { get; } = new(StringComparer.Ordinal);
        /// <summary>Questions outside loops that once showed more passes than their outcome line holds: their pips keep a line of their own.</summary>
        public HashSet<string> PassStrips { get; } = new(StringComparer.Ordinal);
        /// <summary>The member each expanded group's frame shows.</summary>
        public Dictionary<string, int> Members { get; } = new(StringComparer.Ordinal);
        public AnimationGraphLayoutOptions Options => new()
        {
            Collapsed = new HashSet<string>(Collapsed, StringComparer.Ordinal), Opened = new HashSet<string>(Opened, StringComparer.Ordinal), ShowAll = new HashSet<string>(ShowAll, StringComparer.Ordinal),
            ExpandedRows = new HashSet<string>(ExpandedRows, StringComparer.Ordinal), PassStrips = new HashSet<string>(PassStrips, StringComparer.Ordinal),
        };
    }

    private AnimationGraphView GraphView
    {
        get
        {
            if (animationGraphView != null) return animationGraphView;
            AnimationGraphView view = new();
            animationGraphView = view;
            view.SelectionRequested += id => _ = ReportUi(() => SelectAnimationGraphAsync(id, CancellationToken.None));
            view.ConditionRequested += (row, choice) => _ = ReportUi(() => { GraphCondition(row.Id, choice); return Task.CompletedTask; });
            view.IncludeRequested += (edge, value) => _ = ReportUi(() => { GraphInclude(edge.Id, value); return Task.CompletedTask; });
            view.ExpandRequested += (node, value) => _ = ReportUi(() => { GraphExpand(node, value); return Task.CompletedTask; });
            view.MemberRequested += (frame, entry) => _ = ReportUi(() => { GraphMember(frame, entry); return Task.CompletedTask; });
            view.OpenRequested += node => _ = ReportUi(() => GraphOpenAsync(node));
            view.PresentationRequested += (action, id) => _ = ReportUi(() => { GraphPresentationChange(action, id); return Task.CompletedTask; });
            view.SeekRequested += seconds => _ = ReportUi(() => WorldAnimationTransportAsync("seek", seconds));
            view.RerunRequested += () => _ = ReportUi(() => WorldAnimationTransportAsync("seek", worldAnimationSession?.LastFrame?.Time ?? 0));
            view.RunOverlayChanged += value => { animationGraphRunOverlay = value; };
            view.IncludeAllRequested += choice => _ = ReportUi(() => { GraphIncludeAll(choice); return Task.CompletedTask; });
            view.EditTimingRequested += () => _ = ReportUi(() => { ShowAnimationRightTab("inspector"); sectionCard?.FocusAnimationTiming(); return Task.CompletedTask; });
            return view;
        }
    }

    // ------------------------------------------------------------ the graph

    /// <summary>The definition the presentation and expansion belong to.</summary>
    private int GraphDefinition => animationMode?.Definition ?? -1;
    private GraphPresentation Presentation => animationGraphPresentation.TryGetValue(GraphDefinition, out var found) ? found : animationGraphPresentation[GraphDefinition] = new();
    private HashSet<string> ExpandedGroups => animationGraphExpanded.TryGetValue(GraphDefinition, out var found) ? found : animationGraphExpanded[GraphDefinition] = new(StringComparer.Ordinal);

    /// <summary>The opened binding's graph, built again when the map's compilation, the binding or the expansion changed.</summary>
    private AnimationGraph? CurrentAnimationGraph()
    {
        animationGraphError = null;
        if (animationMode is not { Entry: > 0 } target || target.Document != shownDocument || target.Document.Document is not SourceSceneSnapshot source || source.Animations is not { } package)
        {
            animationGraph = null; animationGraphLayout = null; return null;
        }
        if (animationGraphIndex?.Package != package)
        {
            var options = AnimationGraphOptions.For(source.AnimationDefinitions, scene?.Mission?.StartupAnimations, source.Mission);
            animationGraphIndex = AnimationGraphIndex.Create(package, source.AnimationProvenance, options);
            animationGraph = null;
        }
        if (target.Entry >= package.Entries.Count) { animationGraph = null; return null; }
        var expanded = ExpandedGroups;
        var members = Presentation.Members;
        if (animationGraph == null || animationGraph.Index != animationGraphIndex || animationGraph.Entry != target.Entry || !animationGraph.Expanded.SetEquals(expanded)
            || animationGraph.Members.Count != members.Count || members.Any(m => !animationGraph.Members.TryGetValue(m.Key, out int chosen) || chosen != m.Value))
        {
            try
            {
                animationGraph = AnimationGraph.Create(animationGraphIndex, target.Entry, expanded, members: members);
                animationGraphLayout = null; animationGraphOverlay = null;
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { animationGraph = null; animationGraphError = ex.Message; return null; }
        }
        return animationGraph;
    }
    private AnimationGraphLayout? CurrentAnimationGraphLayout()
    {
        if (CurrentAnimationGraph() is not { } graph) { animationGraphLayout = null; return null; }
        var options = Presentation.Options;
        if (animationGraphLayout == null || animationGraphLayout.Graph != graph || !SameOptions(animationGraphLayout.Options, options))
            animationGraphLayout = AnimationGraphLayout.Create(graph, options);
        return animationGraphLayout;
        static bool SameOptions(AnimationGraphLayoutOptions a, AnimationGraphLayoutOptions b) =>
            a.Collapsed.SetEquals(b.Collapsed) && a.Opened.SetEquals(b.Opened) && a.ShowAll.SetEquals(b.ShowAll) && a.ExpandedRows.SetEquals(b.ExpandedRows) && a.PassStrips.SetEquals(b.PassStrips);
    }
    private AnimationGraphLayout RequireGraph()
    {
        if (!InAnimationMode) throw new StudioCommandException("not_ready", "Open an animation in Animation mode first.");
        return CurrentAnimationGraphLayout() ?? throw new StudioCommandException("not_ready", animationGraphError ?? (animationMode is { Entry: <= 0 } ? "The opened definition has no binding in this map." : "The Structure graph is unavailable."));
    }

    /// <summary>
    /// Shows the graph with the scenario and the run at the playhead. While playing, the run overlay follows at most ten times a
    /// second; at rest every frame shows at once.
    /// </summary>
    private void RefreshAnimationGraph(bool runtimeOnly = false)
    {
        if (animationGraphView == null && !InAnimationMode) return;
        var view = GraphView;
        if (!InAnimationMode || animationMode is not { } target)
        {
            animationGraphSelection = null; animationGraphOutline = null;
            view.SetState(AnimationGraphState.Empty, "none");
            return;
        }
        bool playing = worldAnimationSession?.IsPlaying == true;
        if (runtimeOnly && playing && animationGraphClock.ElapsedMilliseconds < 100) return;
        animationGraphClock.Restart();
        var layout = CurrentAnimationGraphLayout();
        if (layout == null)
        {
            string message = animationGraphError ?? (target.Entry <= 0
                ? $"Not bound on {scene?.Mission?.Layout.Difficulty.ToString() ?? "this difficulty"}: {(target.Document.Document as SourceSceneSnapshot)?.Mission ?? "this map"} has no node this definition names as its root. The Inspector still shows the definition."
                : "The Structure graph is unavailable.");
            view.SetState(AnimationGraphState.Empty with { Message = message }, "none");
            return;
        }
        var scenario = worldAnimationConfiguration;
        var frame = worldAnimationSession?.CurrentFrame;
        bool stale = frame == null && worldAnimationSession?.LastFrame != null;
        frame ??= worldAnimationSession?.LastFrame;
        if (frame == null) animationGraphOverlay = null;
        else if (animationGraphOverlay == null || animationGraphOverlay.Graph != layout.Graph.Id || !ReferenceEquals(frame, animationGraphOverlayFrame) || !ReferenceEquals(scenario, animationGraphOverlayScenario))
        {
            try { animationGraphOverlay = AnimationRunOverlay.Create(layout.Graph, frame, scenario); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException) { animationGraphOverlay = null; ViewModel.Status = "Run overlay unavailable: " + ex.Message; }
            animationGraphOverlayFrame = frame; animationGraphOverlayScenario = scenario;
        }
        // A question tested more often than its outcome line holds gets a pass strip of its own, kept for the session so the
        // nodes below it move once, not at every seek (a question in a loop has one from the start).
        if (animationGraphOverlay is { } shownRun)
        {
            bool added = false;
            foreach (var condition in shownRun.Conditions)
                if (condition.PassCount > AnimationGraphLayout.OutcomePips && layout.Item(condition.Row) is { } band
                    && !band.Node.Items.Any(i => i.Kind == AnimationGraphItemKind.Passes && i.Row?.Id == condition.Row))
                    added |= Presentation.PassStrips.Add(condition.Row);
            if (added && CurrentAnimationGraphLayout() is { } widened) layout = widened;
        }
        if (animationGraphSelection != null && layout.Node(animationGraphSelection) == null && layout.Item(animationGraphSelection) == null) animationGraphSelection = null;
        view.SetState(new()
        {
            Layout = layout, Scenario = scenario, Overlay = animationGraphOverlay, Stale = stale && animationGraphOverlay != null,
            Highlighted = animationGraphHighlight, ReadOnly = false,
        }, $"{target.Document.SessionId:N}:{target.Definition}:{target.Entry}");
        if (view.Selection != animationGraphSelection) view.Select(animationGraphSelection);
        if (view.RunOverlay != animationGraphRunOverlay) view.SetRunOverlay(animationGraphRunOverlay);
    }

    // ------------------------------------------------------------ operations (GUI and MCP)

    /// <summary>
    /// Selects a node or row of the graph ("" clears): the Inspector shows its card (the definition, sequence or event card
    /// until the Inspector redesign), the view outlines the nodes it names, and Timing scrolls to its lane where it has one.
    /// </summary>
    private async Task SelectAnimationGraphAsync(string id, CancellationToken token)
    {
        var layout = RequireGraph();
        var target = animationMode!;
        // As the timeline's selection: an open card draft is applied or reverted first.
        if (id.Length > 0) RequireNoDrafts();
        if (id.Length == 0)
        {
            animationGraphSelection = null; animationGraphOutline = null;
            GraphView.Select(null); ApplySectionPresentation();
            return;
        }
        var graph = layout.Graph;
        if ((layout.Item(id)?.Item.Row ?? graph.Row(id)) is { } row)
        {
            string path = graph.Node(AnimationGraph.FrameId(row.Entry))?.Frame?.Path ?? "";
            await WorldAnimationSelectSourceAsync(row.Sequence, row.Event, token, row.Entry, path, preserveTimeline: true);
            animationGraphOutline = GraphOutline(row.Entry, row.Members.Count > 0 ? row.Members : [row.Event]);
            RevealTimingRow(row.Entry, path, row.Sequence, row.Event);
        }
        else if (layout.Node(id) is { } placement)
        {
            var node = placement.Node;
            switch (node.Kind)
            {
                case AnimationGraphNodeKind.Sequence:
                    {
                        var sequence = node.Sequence!;
                        string path = graph.Node(AnimationGraph.FrameId(sequence.Entry))?.Frame?.Path ?? "";
                        await WorldAnimationSelectSourceAsync(sequence.Id, Guid.Empty, token, sequence.Entry, path, preserveTimeline: true);
                        var events = graph.Index.Package.Entries[sequence.Entry].AllSequences.FirstOrDefault(s => s.Id == sequence.Id)?.Events.Select(e => e.Id).ToArray() ?? [];
                        animationGraphOutline = GraphOutline(sequence.Entry, events);
                        RevealTimingRow(sequence.Entry, path, sequence.Id, null);
                        break;
                    }
                case AnimationGraphNodeKind.Reference when node.Reference is { Entries.Count: > 0 } reference:
                    await EnsureAnimationCardForEntryAsync(target.Document, reference.Entries[0], token, preserveTimeline: true);
                    animationGraphOutline = scene?.Mission is { } mission ? [.. reference.Entries.Select(e => mission.AnimationEntryRoots.TryGetValue(e, out int root) ? root : -1).Where(r => r >= 0).Distinct().Take(zStudio.Rendering.SceneViewport.MaximumOutlinedNodes)] : null;
                    break;
                case AnimationGraphNodeKind.Reference:
                    animationGraphOutline = [];
                    break;
                default:
                    // The frame or the activation: the definition, its roots outlined.
                    await EnsureAnimationCardForEntryAsync(target.Document, node.Entry ?? target.Entry, token, preserveTimeline: true);
                    animationGraphOutline = scene?.Mission is { } m && m.AnimationEntryRoots.TryGetValue(node.Entry ?? target.Entry, out int bound) ? [bound] : null;
                    break;
            }
            if (InAnimationMode && animationRightTab != "inspector") ShowAnimationRightTab("inspector");
        }
        else throw new StudioCommandException("stale_record", "The graph has no node or row with that identity; read it again.");
        animationGraphSelection = id;
        GraphView.Select(id);
        ApplySectionPresentation();
    }

    /// <summary>The nodes the events name, found under their binding's root as the runtime binds them (the first of each name).</summary>
    private int[]? GraphOutline(int entry, IReadOnlyList<Guid> events)
    {
        if (scene?.Mission is not { } mission || scene.PreviewScene is not { } data || !mission.AnimationEntryRoots.TryGetValue(entry, out int root) || root < 0 || root >= data.Nodes.Count) return null;
        if (animationGraphIndex?.Package is not { } package || entry >= package.Entries.Count) return null;
        var binding = package.Entries[entry];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var ev in binding.AllSequences.SelectMany(s => s.Events).Where(e => events.Contains(e.Id)))
            foreach (string name in AnimationPhrases.NodeNames(binding, ev)) names.Add(name);
        if (names.Count == 0) return null;
        List<int> found = [];
        foreach (string name in names)
        {
            if (name == binding.RootName) { found.Add(root); continue; }
            if (FindBelow(data, root, name) is int node) found.Add(node);
            if (found.Count >= zStudio.Rendering.SceneViewport.MaximumOutlinedNodes) break;
        }
        return found.Count == 0 ? null : [.. found.Distinct()];
    }
    /// <summary>The first node named <paramref name="name"/> in <paramref name="root"/>'s subtree, breadth first, bounded.</summary>
    private static int? FindBelow(GameScene data, int root, string name)
    {
        Queue<int> pending = new([root]); HashSet<int> seen = [];
        while (pending.TryDequeue(out int index) && seen.Count < 200_000)
        {
            if (!seen.Add(index) || index < 0 || index >= data.Nodes.Count) continue;
            if (data.Nodes[index].Name == name) return index;
            foreach (int child in data.Nodes[index].Children) pending.Enqueue(child);
        }
        return null;
    }
    /// <summary>Timing's lane of a sequence or event, where the timeline shows one.</summary>
    private void RevealTimingRow(int entry, string path, Guid sequence, Guid? ev)
    {
        if (worldTimeline == null) return;
        string id = "entry:" + entry.ToString(CultureInfo.InvariantCulture) + ":" + path + ":sequence:" + sequence + (ev is Guid e ? ":event:" + e : "");
        if (worldAnimationSourceRows.Any(r => r.Id == id))
            try { WorldTimeline.SelectRow(id); } catch (InvalidOperationException) { }
    }

    /// <summary>The rows naming the object selected in the 3D view (an amber bar), in the bindings whose root holds it.</summary>
    private void RefreshAnimationGraphHighlight()
    {
        HashSet<string> rows = new(StringComparer.Ordinal);
        if (InAnimationMode && animationGraphLayout is { } layout && scene?.Mission is { } mission && scene.PreviewScene is { } data && selectedNode is int node and >= 0 && node < data.Nodes.Count)
        {
            string name = data.Nodes[node].Name;
            foreach (var placement in layout.Nodes.Where(n => n.Node.Kind == AnimationGraphNodeKind.Sequence))
            {
                int entry = placement.Node.Entry ?? 0;
                if (!mission.AnimationEntryRoots.TryGetValue(entry, out int root) || !Holds(data, root, node)) continue;
                var binding = layout.Graph.Index.Package.Entries[entry];
                var sequence = binding.AllSequences.FirstOrDefault(s => s.Id == placement.Node.Sequence!.Id);
                if (sequence == null) continue;
                foreach (var ev in sequence.Events)
                {
                    var names = AnimationPhrases.NodeNames(binding, ev);
                    if (names.Contains(name) || node == root && names.Contains(binding.RootName))
                        if (layout.Graph.RowOf(entry, ev.Id) is { } row) rows.Add(row.Id);
                }
            }
        }
        if (rows.SetEquals(animationGraphHighlight)) return;
        animationGraphHighlight = rows;
        RefreshAnimationGraph();
        if (rows.Count > 0 && animationGraphLayout is { } shown)
            if (shown.Nodes.SelectMany(n => n.Items).FirstOrDefault(i => i.Row != null && rows.Contains(i.Row.Id))?.Row?.Id is { } first) GraphView.Reveal(first);
    }
    private static bool Holds(GameScene data, int root, int node)
    {
        HashSet<int> seen = [];
        for (int current = node; current >= 0 && current < data.Nodes.Count && seen.Add(current);)
        {
            if (current == root) return true;
            var parents = data.Nodes[current].Parents;
            if (parents.Length == 0) return false;
            current = parents[0];
        }
        return false;
    }

    /// <summary>A condition switch (IF or ELSEIF) of a shown frame, in that frame's call path, through the scenario.</summary>
    private void GraphCondition(string rowId, AnimationConditionChoice choice)
    {
        var layout = RequireGraph();
        var row = layout.Graph.Row(rowId) ?? throw new StudioCommandException("stale_record", "The graph has no row with that identity; read it again.");
        if (row.Type is not (31 or 33)) throw new StudioCommandException("invalid_argument", "Only an IF or ELSEIF row has a Game | True | False switch.");
        string path = layout.Graph.Node(AnimationGraph.FrameId(row.Entry))?.Frame?.Path
            ?? throw new StudioCommandException("not_ready", "This frame is reached only by name, so no call path runs its conditions; open it to force them.");
        ScenarioCondition(path, row.Entry, row.Event, choice);
    }
    /// <summary>A call port: includes (with the calls that lead to them) or excludes (with what is below them) the edge's call paths.</summary>
    private void GraphInclude(string edgeId, bool value)
    {
        var layout = RequireGraph();
        var edge = layout.Graph.Edge(edgeId) ?? throw new StudioCommandException("stale_record", "The graph has no edge with that identity; read it again.");
        if (edge.Relation is not (AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild))
            throw new StudioCommandException("invalid_argument", "Only a call's port includes or excludes call paths.");
        if (edge.Paths.Count == 0) throw new StudioCommandException("not_ready", "This call's frame is reached only by name: its call paths are listed in the Scenario tab.");
        ScenarioInclude(edge.Paths, value);
    }
    /// <summary>Include: all calls (Include all), this animation only (every path below the opened binding's start), or none.</summary>
    private void GraphIncludeAll(string choice)
    {
        var target = RequireOpenedBinding();
        switch (choice)
        {
            case "all": ScenarioIncludeAll(); break;
            case "none": ScenarioExcludeAll(); break;
            case "opened":
                {
                    var package = target.Document.Document.Animations!;
                    var (own, _) = AnimationScenario.For(target.Entry).IncludeAll(package);
                    lastIncludeAll = null;
                    SetWorldAnimationScenario(worldAnimationConfiguration with { IncludedCalls = own.IncludedCalls });
                    break;
                }
            default: throw new StudioCommandException("invalid_argument", "Include all, opened or none.");
        }
    }
    /// <summary>
    /// Expands or collapses a called animation's node: one frame for the group (at most 64 expanded at once), showing
    /// <paramref name="member"/> (one of its bindings) when given, else the member chosen before or its first.
    /// </summary>
    private void GraphExpand(string nodeId, bool value, int? member = null)
    {
        var layout = RequireGraph();
        var reference = layout.Node(nodeId)?.Node.Reference ?? throw new StudioCommandException("stale_record", "The graph has no reference node with that identity; read it again.");
        if (!reference.Expandable || reference.Unresolved) throw new StudioCommandException("invalid_argument", "Only a called animation expands into its frames.");
        if (member is int chosen && !reference.Entries.Contains(chosen)) throw new StudioCommandException("invalid_argument", "Choose one of the called animation's bindings (reference.entries).");
        if (!value && member != null) throw new StudioCommandException("invalid_argument", "A member is chosen when expanding.");
        try { _ = layout.Graph.WithExpanded(reference.Group, value); }
        catch (InvalidOperationException ex) { throw new StudioCommandException("limit_exceeded", ex.Message); }
        if (value) ExpandedGroups.Add(reference.Group); else ExpandedGroups.Remove(reference.Group);
        if (member is int shown) Presentation.Members[reference.Group] = shown;
        RefreshAnimationGraph();
    }
    /// <summary>The frame of an expanded group shows another of its bindings (the member switcher in its header).</summary>
    private void GraphMember(string frameId, int member)
    {
        var layout = RequireGraph();
        var frame = layout.Node(frameId)?.Node.Frame;
        if (frame?.Group == null) throw new StudioCommandException("stale_record", "The graph has no expanded group's frame with that identity; read it again.");
        GraphExpand(AnimationGraph.ReferenceId(frame.Group), true, member);
    }
    /// <summary>Opens a reference node's definition in Animation mode (Back returns).</summary>
    private async Task GraphOpenAsync(string nodeId)
    {
        var layout = RequireGraph();
        var reference = layout.Node(nodeId)?.Node.Reference ?? throw new StudioCommandException("stale_record", "The graph has no reference node with that identity; read it again.");
        if (reference.Entries.Count == 0) throw new StudioCommandException("not_found", "No animation of this map has that name.");
        var target = animationMode!;
        var source = (SourceSceneSnapshot)target.Document.Document;
        var definition = source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Entries.Contains(reference.Entries[0]))
            ?? throw new StudioCommandException("stale_record", "The called animation has no source definition in this map.");
        await OpenAnimationFromGuiAsync(target.Document, definition.Index, reference.Entries[0]);
    }
    /// <summary>collapse / open a sequence, showAll its rows, expandRow / collapseRow a grouped row's members, collapseAll / expandAll.</summary>
    private void GraphPresentationChange(string action, string? id)
    {
        var layout = RequireGraph();
        var presentation = Presentation;
        string Sequence() => id is { } given && layout.Node(given)?.Node.Kind == AnimationGraphNodeKind.Sequence ? given
            : throw new StudioCommandException("stale_record", "The graph has no sequence node with that identity; read it again.");
        switch (action)
        {
            case "collapse": { string node = Sequence(); presentation.Collapsed.Add(node); presentation.Opened.Remove(node); break; }
            case "open": { string node = Sequence(); presentation.Collapsed.Remove(node); presentation.Opened.Add(node); break; }
            case "showAll": { string node = Sequence(); presentation.ShowAll.Add(node); presentation.Collapsed.Remove(node); presentation.Opened.Add(node); break; }
            case "expandRow" or "collapseRow":
                {
                    var row = id == null ? null : layout.Graph.Row(id);
                    if (row is not { Members.Count: > 1 }) throw new StudioCommandException("invalid_argument", "Only a grouped row lists its members.");
                    if (action == "expandRow") presentation.ExpandedRows.Add(row.Id); else presentation.ExpandedRows.Remove(row.Id);
                    break;
                }
            case "collapseAll":
                presentation.Opened.Clear();
                foreach (var node in layout.Nodes.Where(n => n.Node.Kind == AnimationGraphNodeKind.Sequence)) presentation.Collapsed.Add(node.Id);
                break;
            case "expandAll":
                presentation.Collapsed.Clear();
                foreach (var node in layout.Nodes.Where(n => n.Node.Kind == AnimationGraphNodeKind.Sequence)) presentation.Opened.Add(node.Id);
                break;
            default: throw new StudioCommandException("invalid_argument", "Unknown presentation change.");
        }
        RefreshAnimationGraph();
    }

    /// <summary>
    /// What a PLAYER_RANGE row or a range gate draws (phase 6 draws it in the view): its attach node's position, the radius, the
    /// player reference at the playhead (or as the scenario sets it) and their distance.
    /// </summary>
    private object? GraphRangeDrawing(string id)
    {
        if (animationGraphLayout is not { } layout) return null;
        double radius; int entry;
        if (layout.Item(id)?.Item is { Kind: AnimationGraphItemKind.Question, Branch.Condition: { Operand: "playerRange", Radius: double r } } item) { radius = r; entry = item.Row!.Entry; }
        else if (layout.Node(id)?.Node.Activation is { } activation && activation.Triggers.FirstOrDefault(t => t.Kind == "rangeGate") is { Value: double gate }) { radius = gate; entry = activation.Entry; }
        else return null;
        Vector3? center = null;
        if (scene?.Mission is { } mission && scene.PreviewScene is { } data && mission.AnimationEntryRoots.TryGetValue(entry, out int root) && root >= 0 && root < data.Nodes.Count)
        {
            var binding = layout.Graph.Index.Package.Entries[entry];
            int attach = binding.AttachName == binding.RootName ? root : FindBelow(data, root, binding.AttachName) ?? root;
            center = zStudio.Rendering.SceneViewport.WorldTransform(data, attach).Translation;
        }
        var scenario = worldAnimationConfiguration;
        Vector3? player = animationGraphOverlay?.Player?.Position
            ?? (scenario.Player.Kind == AnimationPlayerReferenceKind.Custom ? scenario.CustomPlayer
                : scenario.Player.Kind == AnimationPlayerReferenceKind.None ? null : scene?.Mission?.PlayerStart);
        double? distance = center is { } c && player is { } p ? Vector3.Distance(c, p) : null;
        return new
        {
            center = center is { } at ? new[] { at.X, at.Y, at.Z } : null, radius,
            player = player is { } pl ? new[] { pl.X, pl.Y, pl.Z } : null,
            playerSource = animationGraphOverlay?.Player?.Source ?? scenario.Player.Kind.ToString(),
            distance, inside = distance is double d ? d <= radius : (bool?)null,
            attach = layout.Graph.Index.Package.Entries[entry].AttachName,
        };
    }
}
