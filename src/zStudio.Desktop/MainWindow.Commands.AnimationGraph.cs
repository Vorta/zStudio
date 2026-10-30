using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private void RegisterAnimationGraphCommands(StudioCommands registry)
    {
        RegisterJob(registry, "animation_graph", "Animation mode's Structure view: the opened binding's node graph (the activation; the definition frame with the cleanup and runtime sequences, their grouped rows, condition blocks and loop brackets; reference nodes for the called and calling animations, an expanded one as one frame showing one of its bindings (its member); every edge: call, callWait, localChild, release, contactRelease, stopSequence, stopAnimation, resetAnimation, invalidateAnimation, prerequisite, with backLink for a call back to a binding on the path) with its deterministic layout (bounds in DIP at 100 %) and the run overlay at the playhead. Identities are the GUI's: frame:<entry>, activation:<entry>, sequence:<entry>:<guid>, row:<entry>:<guid>, ref:<group>, and edge ids from read. read pages nodes, edges (section edges) or one sequence's rows with its blocks (section rows, node) by at most 100, each with its run (binding, sequence or row state; a blocked sequence's blockedBy edge, whose port includes the call it waits for; an activation's prerequisites at its latest start attempt: met, unmet · refused or unmet · ignored for this start; a condition's passes, at most 40, with its Game/True/False switch and where its pips are drawn). expand and collapse change presentation only (never include, run or evaluate): a reference node expands into one frame for the group (at most 64 expanded; member chooses the binding it shows, as the frame header's switcher does), a sequence opens or collapses (showAll lists every row), a grouped row lists its members, all opens or collapses every sequence. select selects a node, row or edge (an edge selects its source row) as a click does: the Inspector shows its card (returned as inspector) and the view outlines the nodes it names; inspector reads the Inspector card as shown, without selecting: kind (definition, activation, sequence, event, condition, call, reference), title, subtitle, notices and sections with their items (field: a source field source_animation set changes; threshold: the Start threshold, set with source_animation set startClock/startSeconds, Add threshold with startSeconds, Clear with the field's changes and no values; switch: a condition's Game/True/False per call path, animation_scenario conditions; include: call paths, animation_scenario include; text: the game rule in words with the current inputs, notices; table: outcomes and occurrences with their seek seconds, animation_transport seek; bindings: animation_mode binding; links: select, open, expand); a PLAYER_RANGE question or a range-gated activation returns rangeDrawing (attach node position, radius, player, distance). viewport sets zoom (0.25–2), center, fit, reveal and the run overlay, every value checked before any applies. callers lists every call or by-name event of the map that reaches a binding. Condition switches are animation_scenario conditions and call ports animation_scenario include (the GUI's switches and ports use the same operations); animation_mode opens and exits.",
            [PreviewParameter,
             P("action", "string", "Operation.", true, "read", "expand", "collapse", "select", "viewport", "callers", "inspector"),
             P("section", "string", "read: nodes (default), edges, or rows (a sequence's rows and blocks; give node).", false, "nodes", "edges", "rows"),
             P("node", "string", "A node identity from read: read rows (a sequence), expand/collapse (a reference node or a sequence), select."),
             P("row", "string", "A row identity from read rows: select it, or expand/collapse a grouped row's members."),
             P("edge", "string", "An edge identity from read edges: select its source row."),
             P("all", "boolean", "expand/collapse: every sequence of the graph."),
             P("showAll", "boolean", "expand a sequence: also show every row (beyond the first twelve)."),
             new("member", "integer", "expand a reference node (or its expanded frame): the binding its one frame shows (reference.entries; the member switcher).", Minimum: 1),
             new("zoom", "number", "viewport: zoom, 0.25–2 (1 = 100 %).", NumberMinimum: .25, NumberMaximum: 2),
             new("center", "array", "viewport: the layout point (DIP) to centre the panel on.", Items: new("", "number", "DIP."), MinItems: 2, MaxItems: 2),
             P("fit", "boolean", "viewport: fit the whole graph in the panel (Ctrl+0)."),
             P("reveal", "string", "viewport: scroll a node or row into view."),
             P("runOverlay", "boolean", "viewport: show the run at the playhead on the graph (on by default)."),
             new("entry", "integer", "callers: the binding (compiled entry); the opened binding when omitted.", Minimum: 1),
             new("offset", "integer", "Page offset.", Minimum: 0, Maximum: int.MaxValue),
             new("limit", "integer", "Page size, at most 100.", Minimum: 1, Maximum: 100)], false, async (a, token) =>
        {
            RequirePreview(a);
            string action = Text(a, "action");
            int offset = Int(a, "offset"), limit = Int(a, "limit", 100);
            if (offset < 0 || limit is < 1 or > 100) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–100.");
            if (CompiledAnimationShown) return Result(await CompiledGraphCommandAsync(a, action, offset, limit));
            var layout = RequireGraph();
            object? result = null;
            switch (action)
            {
                case "read":
                    result = Text(a, "section", "nodes") switch
                    {
                        "nodes" => GraphNodesPage(layout, offset, limit, animationGraphOverlay, GraphView),
                        "edges" => GraphEdgesPage(layout, offset, limit, animationGraphOverlay, worldAnimationConfiguration),
                        "rows" => GraphRowsPage(layout, a.ContainsKey("node") ? Text(a, "node") : throw new StudioCommandException("invalid_argument", "Supply the sequence node."), offset, limit, animationGraphOverlay, worldAnimationConfiguration),
                        _ => throw new StudioCommandException("invalid_argument", "Read nodes, edges or rows."),
                    };
                    break;
                case "expand" or "collapse":
                    {
                        bool value = action == "expand";
                        if (Flag(a, "all")) GraphPresentationChange(value ? "expandAll" : "collapseAll", null);
                        else if (a.ContainsKey("row")) GraphPresentationChange(value ? "expandRow" : "collapseRow", Text(a, "row"));
                        else if (a.ContainsKey("node"))
                        {
                            string node = Text(a, "node");
                            var placement = layout.Node(node) ?? throw new StudioCommandException("stale_record", "The graph has no node with that identity; read it again.");
                            int? member = a.ContainsKey("member") ? Int(a, "member") : null;
                            if (placement.Node.Kind == AnimationGraphNodeKind.Reference) GraphExpand(node, value, member);
                            else if (placement.Node.Frame?.Group != null && member is int shown && value) GraphMember(node, shown);
                            else if (member != null) throw new StudioCommandException("invalid_argument", "A member is chosen on a reference node or an expanded group's frame.");
                            else if (placement.Node.Kind == AnimationGraphNodeKind.Sequence) GraphPresentationChange(value ? Flag(a, "showAll") ? "showAll" : "open" : "collapse", node);
                            else throw new StudioCommandException("invalid_argument", "Expand a reference node, a sequence or a grouped row.");
                        }
                        else throw new StudioCommandException("invalid_argument", "Supply node, row or all.");
                        break;
                    }
                case "select":
                    {
                        string id = a.ContainsKey("edge") ? layout.Graph.Edge(Text(a, "edge"))?.From ?? throw new StudioCommandException("stale_record", "The graph has no edge with that identity; read it again.")
                            : a.ContainsKey("row") ? Text(a, "row") : a.ContainsKey("node") ? Text(a, "node") : "";
                        if (id.StartsWith("ref:", StringComparison.Ordinal) && a.ContainsKey("edge")) throw new StudioCommandException("invalid_argument", "This edge leaves a reference node: select the node.");
                        // Clearing the selection needs no resolved drafts (as Esc in the view); selecting does.
                        if (id.Length > 0) RequireNoDrafts();
                        await SelectAnimationGraphAsync(id, token);
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                        result = new { selection = animationGraphSelection, rangeDrawing = animationGraphSelection == null ? null : GraphRangeDrawing(animationGraphSelection), outlined = animationGraphOutline?.Length,
                            inspector = BuildAnimationInspection()?.Json() };
                        break;
                    }
                case "inspector":
                    {
                        // The Inspector's card as the pane shows it: its kind, header, notices and sections (fields name source_animation fields).
                        RefreshAnimationInspector();
                        result = new { selection = animationGraphSelection, inspector = BuildAnimationInspection()?.Json() };
                        break;
                    }
                case "viewport":
                    {
                        var view = GraphView;
                        if (!view.IsVisible || view.ActualWidth <= 0) throw new StudioCommandException("not_ready", "Show the Structure view first (animation_mode layout view structure or split).");
                        GraphViewport(a, layout, view, shown => animationGraphRunOverlay = shown)();
                        break;
                    }
                case "callers":
                    {
                        int entry = a.ContainsKey("entry") ? Int(a, "entry") : layout.Graph.Entry;
                        if (entry <= 0 || entry >= layout.Graph.Index.Package.Entries.Count) throw new StudioCommandException("invalid_argument", "Choose a binding of this map.");
                        var page = layout.Graph.Callers(entry, offset, limit);
                        result = new
                        {
                            entry, name = layout.Graph.Index.Package.Entries[entry].Name, total = page.Total, offset = page.Offset, nextOffset = page.NextOffset,
                            items = page.Rows.Select(c => new { caller = c.Caller, name = c.Name, sequence = c.Sequence, @event = c.Event, relation = RelationName(c.Relation), target = c.Target, row = AnimationGraph.RowId(c.Caller, c.Event) }).ToArray(),
                        };
                        break;
                    }
                default: throw new StudioCommandException("invalid_argument", "Unknown graph operation.");
            }
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            return Result(new { graph = GraphSummary(), result });
        });
    }

    /// <summary>
    /// animation_graph viewport on a Structure view (Animation mode's and a compiled preview's alike), checked whole: what applies
    /// the run overlay (<paramref name="runOverlay"/> keeps the choice), fit, zoom, centre and reveal in that order, once every
    /// value is valid, so a refused call changes nothing.
    /// </summary>
    private static Action GraphViewport(JsonObject a, AnimationGraphLayout layout, AnimationGraphView view, Action<bool> runOverlay)
    {
        bool? overlay = a.ContainsKey("runOverlay") ? Flag(a, "runOverlay") : null;
        bool fit = Flag(a, "fit");
        double? zoom = a.ContainsKey("zoom") ? Number(a, "zoom") : null;
        if (zoom is double z && (!double.IsFinite(z) || z is < AnimationGraphView.MinimumZoom or > AnimationGraphView.MaximumZoom)) throw new StudioCommandException("invalid_argument", "Zoom is 0.25–2.");
        Point? center = null;
        if (a["center"] is JsonArray { Count: 2 } pair)
        {
            double x = pair[0]!.GetValue<double>(), y = pair[1]!.GetValue<double>();
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new StudioCommandException("invalid_argument", "Use a finite centre.");
            center = new(x, y);
        }
        string? reveal = a.ContainsKey("reveal") ? Text(a, "reveal") : null;
        if (reveal != null && layout.Node(reveal) == null && layout.Item(reveal) == null) throw new StudioCommandException("stale_record", "The graph has no node or row with that identity; read it again.");
        return () =>
        {
            if (overlay is bool shown) { runOverlay(shown); view.SetRunOverlay(shown); }
            if (fit) view.FitView();
            if (zoom is double chosen) view.ZoomTo(chosen);
            if (center is Point at) view.CenterOn(at);
            if (reveal != null) view.Reveal(reveal);
        };
    }

    internal static string RelationName(AnimationGraphRelation relation) => relation switch
    {
        AnimationGraphRelation.Call => "call", AnimationGraphRelation.CallWait => "callWait", AnimationGraphRelation.LocalChild => "localChild",
        AnimationGraphRelation.Release => "release", AnimationGraphRelation.ContactRelease => "contactRelease", AnimationGraphRelation.StopSequence => "stopSequence",
        AnimationGraphRelation.StopAnimation => "stopAnimation", AnimationGraphRelation.ResetAnimation => "resetAnimation",
        AnimationGraphRelation.InvalidateAnimation => "invalidateAnimation", _ => "prerequisite",
    };
    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>The graph, the viewport and the run as animation_graph reports them.</summary>
    private object GraphSummary()
    {
        var layout = animationGraphLayout;
        var view = animationGraphView;
        var overlay = view?.OverlayShown == true || animationGraphOverlay != null ? animationGraphOverlay : null;
        return new
        {
            id = layout?.Graph.Id, entry = layout?.Graph.Entry, definition = animationMode?.Definition,
            nodeCount = layout?.Graph.NodeCount, edgeCount = layout?.Graph.EdgeCount, routes = layout?.Routes.Count,
            expanded = layout?.Graph.Expanded.Order(StringComparer.Ordinal).ToArray(), expansionTruncated = layout?.Graph.ExpansionTruncated,
            bounds = layout == null ? null : new { width = layout.Bounds.Width, height = layout.Bounds.Height },
            selection = animationGraphSelection,
            viewport = view == null ? null : new
            {
                zoom = view.Zoom, center = new[] { view.Center.X, view.Center.Y }, visible = new { x = view.Visible.X, y = view.Visible.Y, width = view.Visible.Width, height = view.Visible.Height },
                level = view.Level switch { AnimationGraphLevel.Rows => "rows", AnimationGraphLevel.Compact => "compact", AnimationGraphLevel.Headers => "headers", _ => "chips" },
                runOverlay = animationGraphRunOverlay, shown = view.IsVisible, realised = view.RealisedCount,
            },
            run = overlay == null ? null : new
            {
                time = overlay.Time, stale = worldAnimationSession?.CurrentFrame == null,
                player = overlay.Player is { } player ? new { position = player.Position is { } p ? new[] { p.X, p.Y, p.Z } : null, source = player.Source, frozen = player.Frozen } : null,
            },
            highlighted = animationGraphHighlight.Count,
        };
    }

    private static object GraphNodesPage(AnimationGraphLayout layout, int offset, int limit, AnimationRunOverlay? overlay, AnimationGraphView view)
    {
        var page = layout.Graph.Nodes(offset, limit);
        return new
        {
            section = "nodes", total = page.Total, offset = page.Offset, nextOffset = page.NextOffset,
            items = page.Rows.Select(node =>
            {
                var placed = layout.Node(node.Id);
                object? run = overlay == null ? null : node.Kind switch
                {
                    AnimationGraphNodeKind.Sequence when overlay.Sequence(node.Id) is { } s => new { state = Lower(s.State.ToString()), label = s.Label, pass = s.Pass, eventIndex = s.EventIndex, thresholdRemaining = s.ThresholdRemaining, releasedBy = s.ReleasedBy, releasedAt = s.ReleasedAt, releasers = s.Releasers.Take(100).ToArray(), noReleaserLeft = s.NoReleaserLeft, pausedBy = s.PausedBy, blockedBy = s.BlockedBy, blockedCall = s.BlockedCall },
                    AnimationGraphNodeKind.Reference when overlay.Reference(node.Id) is { } r => new { instances = r.Instances, label = r.Label, excluded = r.Excluded, refused = r.Refused, refusedReason = r.RefusedReason, lastTime = r.LastTime, states = r.States.Select(s => new { state = Lower(s.State.ToString()), bindings = s.Bindings }).ToArray() },
                    AnimationGraphNodeKind.Activation or AnimationGraphNodeKind.Frame when node.Entry is int entry && overlay.Binding(entry) is { } b => new
                    {
                        state = Lower(b.State.ToString()), label = b.Label, invalidated = b.Invalidated, pausedBy = b.PausedBy, time = b.Time, refusals = b.Refusals.Select(r => new { time = r.Time, reason = AnimationRunOverlay.Refusal(r) }).ToArray(), refusalCount = b.RefusalCount,
                        prerequisites = b.Prerequisites is { } start ? new { time = start.Time, outcome = start.Outcome, matched = start.Result.Matched, minimum = start.Result.Minimum, instancePath = start.InstancePath, scenarioStart = start.ScenarioStart, starts = b.PrerequisiteStartCount } : null,
                        members = node.Frame?.Group is { } group && overlay.Reference(AnimationGraph.ReferenceId(group)) is { } membersRun ? membersRun.States.Select(s => new { state = Lower(s.State.ToString()), bindings = s.Bindings }).ToArray() : null,
                    },
                    _ => null,
                };
                var binding = overlay != null && node.Activation is { } shownActivation ? overlay.Binding(shownActivation.Entry) : null;
                return new
                {
                    id = node.Id, kind = Lower(node.Kind.ToString()), label = node.Label, column = node.Column, order = node.Order, depth = node.Depth, parent = node.Parent, entry = node.Entry,
                    bounds = placed == null ? null : new { x = placed.Bounds.X, y = placed.Bounds.Y, width = placed.Bounds.Width, height = placed.Bounds.Height },
                    lane = placed?.Lane, collapsed = placed?.Collapsed, hiddenRows = placed?.HiddenRows,
                    sequence = node.Sequence is { } sequence ? new
                    {
                        id = sequence.Id, index = sequence.Index, name = sequence.Name, cleanup = sequence.Cleanup, waitsForRelease = sequence.WaitsForRelease, eventCount = sequence.EventCount, rowCount = sequence.RowCount,
                        stateLine = sequence.StateLine, ignoredStartTime = sequence.IgnoredStartTime, collapsedByDefault = sequence.CollapsedByDefault, diagnostics = sequence.Diagnostics.Take(20).ToArray(),
                        summary = sequence.Summary.Select(s => new { family = Lower(s.Family.ToString()), count = s.Count }).ToArray(),
                    } : null,
                    reference = node.Reference is { } reference ? new
                    {
                        group = reference.Group, name = reference.Name, entries = reference.Entries, entryCount = reference.EntryCount, calling = reference.Calling, expanded = reference.Expanded, member = reference.Member,
                        expandable = reference.Expandable, unresolved = reference.Unresolved, file = reference.File, detail = reference.Detail, summary = reference.Summary,
                        calls = reference.Calls.Select(c => new { name = c.Name, events = c.Events, backLink = c.BackLink, node = c.Node }).ToArray(), neededBy = reference.NeededBy, caution = reference.Caution,
                    } : null,
                    activation = node.Activation is { } activation ? new
                    {
                        attach = activation.AttachNode, newGameStart = activation.NewGameStart, callerCount = activation.CallerCount,
                        triggers = activation.Triggers.Select(t => new { kind = t.Kind, phrase = t.Phrase, keyword = t.Keyword, value = t.Value }).ToArray(),
                        // At the binding's latest start attempt each item's check there (met, or unmet with what the start did); before
                        // any, an animation item is met unless stopped at the playhead and an object item is unknown (null).
                        prerequisites = activation.Prerequisites.Select(g => new
                        {
                            header = g.Header, required = g.Required, minimum = g.Minimum, outcome = view.PrerequisiteGroupOutcome(binding, g),
                            items = g.Items.Select(i =>
                            {
                                var check = binding?.Prerequisites?.Result.Checks.FirstOrDefault(c => c.Record == i.Record);
                                bool? met = check != null ? check.Satisfied : i.Entry is int needed && overlay != null ? overlay.Binding(needed).State != AnimationBindingLifecycle.Stopped : null;
                                return new { target = i.Target, kind = i.Kind, entry = i.Entry, met, status = overlay == null ? null : view.PrerequisiteRow(binding, i, overlay).Status, atStart = check != null };
                            }).ToArray(),
                        }).ToArray(),
                        callers = activation.Callers.Select(c => new { group = c.Group, name = c.Name, bindings = c.Bindings, events = c.Events, node = c.Node, backLink = c.BackLink, relations = c.Relations.Select(RelationName).ToArray() }).ToArray(),
                    } : null,
                    frame = node.Frame is { } frame ? new
                    {
                        entry = frame.Entry, name = frame.Name, root = frame.Root, attach = frame.Attach, path = frame.Path, depth = frame.Depth, sequenceCount = frame.SequenceCount, file = frame.File,
                        group = frame.Group, members = frame.Group == null ? null : frame.Members, memberIndex = frame.Group == null ? (int?)null : frame.MemberIndex,
                    } : null,
                    run,
                };
            }).ToArray(),
        };
    }

    private static object GraphEdgesPage(AnimationGraphLayout layout, int offset, int limit, AnimationRunOverlay? overlay, AnimationScenario scenario)
    {
        var page = layout.Graph.Edges(offset, limit);
        return new
        {
            section = "edges", total = page.Total, offset = page.Offset, nextOffset = page.NextOffset,
            items = page.Rows.Select(edge =>
            {
                var route = layout.Route(edge.Id);
                bool call = edge.Relation is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild;
                int included = edge.Paths.Count(scenario.Includes);
                return new
                {
                    id = edge.Id, relation = RelationName(edge.Relation), from = edge.From, fromNode = edge.FromNode, to = edge.To, count = edge.Count, label = edge.Label,
                    backLink = edge.BackLink, wait = edge.Wait, local = edge.Local, note = edge.Note, events = edge.Events.Take(100).ToArray(), paths = edge.Paths.Take(100).ToArray(),
                    targetEntries = edge.TargetEntries.Take(100).ToArray(), alsoNamed = edge.AlsoNamed.Take(100).ToArray(),
                    included = !call || edge.Paths.Count == 0 ? null : included == edge.Paths.Count ? "all" : included == 0 ? "none" : "some",
                    run = overlay?.Edge(edge.Id) is { } run ? new { reached = run.Reached, time = run.Time, excluded = run.Excluded, refused = run.Refused, includedPaths = run.IncludedPaths } : null,
                    route = route == null ? null : new { points = route.Points.Select(p => new[] { p.X, p.Y }).ToArray(), end = Lower(route.End.ToString()), fromPort = route.FromPort, fromHidden = route.FromHidden, self = route.Self, label = route.Text },
                };
            }).ToArray(),
        };
    }

    private static object GraphRowsPage(AnimationGraphLayout layout, string nodeId, int offset, int limit, AnimationRunOverlay? overlay, AnimationScenario scenario)
    {
        var node = layout.Node(nodeId)?.Node.Sequence ?? throw new StudioCommandException("stale_record", "The graph has no sequence node with that identity; read it again.");
        var graph = layout.Graph;
        var page = graph.Rows(node.Entry, node.Id, offset, limit);
        string? path = graph.Node(AnimationGraph.FrameId(node.Entry))?.Frame?.Path;
        string Choice(Guid ev) => path == null ? "game" : Lower(scenario.Choice(path, node.Entry, ev).ToString());
        return new
        {
            section = "rows", node = nodeId, instancePath = path, total = page.Total, offset = page.Offset, nextOffset = page.NextOffset,
            blocks = graph.Blocks(node.Entry, node.Id).Select(b => new
            {
                id = b.Id, kind = b.Kind, first = b.First, last = b.Last, endIf = b.EndIf, diagnostic = b.Diagnostic,
                loop = b.Loop is { } loop ? new { @event = loop.Event, mode = loop.Mode, count = loop.Count, seconds = loop.Seconds, label = loop.Label, waitsForRelease = loop.WaitsForRelease } : null,
                branches = b.Branches.Select(br => new
                {
                    index = br.Index, kind = br.Kind, label = br.Label, marker = br.Marker, first = br.First, last = br.Last, empty = br.Empty, @implicit = br.Implicit,
                    condition = br.Condition is { } c ? new { @event = c.Event, operand = c.Operand, question = c.Question, rule = c.Rule, elseIf = c.ElseIf, radius = c.Radius, probability = c.Probability, effectsDetail = c.EffectsDetail, coverHeight = c.CoverHeight, node = c.Node, thresholdIgnored = c.ThresholdIgnored, @switch = Choice(c.Event) } : null,
                }).ToArray(),
            }).ToArray(),
            items = page.Rows.Select(row =>
            {
                var band = layout.Item(row.Id);
                var run = overlay?.Row(row.Id);
                var condition = row.Role == AnimationGraphRowRole.Question ? overlay?.Condition(row.Id) : null;
                // Where the GUI draws the condition's pips: its pass strip (in a loop, or once tested more often than the outcome
                // line holds), else the outcome line (at most 3, the latest).
                var strip = row.Role == AnimationGraphRowRole.Question && band is { } shownBand ? shownBand.Node.Items.FirstOrDefault(i => i.Kind == AnimationGraphItemKind.Passes && i.Row?.Id == row.Id) : null;
                int pipCapacity = strip != null ? AnimationGraphLayout.StripPips : AnimationGraphLayout.OutcomePips;
                int pipsShown = condition == null || condition.PassCount <= 1 && strip == null ? 0 : Math.Min(pipCapacity, condition.Passes.Count);
                return new
                {
                    id = row.Id, @event = row.Event, index = row.Index, type = row.Type, keyword = row.Keyword, family = Lower(row.Family.ToString()), role = Lower(row.Role.ToString()), label = row.Label,
                    clock = row.Clock, threshold = row.Threshold, thresholdBadge = row.ThresholdBadge, thresholdNote = row.ThresholdNote, block = row.Block, branch = row.Branch,
                    memberCount = row.Members.Count, members = row.Members.Take(100).ToArray(), target = row.Target, targetEntry = row.TargetEntry,
                    callback = row.Callback is { } callback ? new { value = callback.Value, @short = callback.Short, meaning = callback.Meaning, preview = callback.Preview } : null, diagnostic = row.Diagnostic,
                    shown = band != null, band = band is { } b ? new { kind = Lower(b.Item.Kind.ToString()), y = b.Item.Y, height = b.Item.Height } : null,
                    @switch = row.Role == AnimationGraphRowRole.Question ? Choice(row.Event) : null,
                    run = run == null ? null : new
                    {
                        disposition = Lower(run.Disposition.ToString()), occurrences = run.Occurrences, skipped = run.Skipped, lastStart = run.LastStart, lastEnd = run.LastEnd, detail = run.Detail,
                        childEntry = run.ChildEntry, counts = run.Counts.Select(c => new { disposition = Lower(c.Disposition.ToString()), count = c.Count }).ToArray(),
                    },
                    passStrip = strip == null ? null : new { y = strip.Y, height = strip.Height },
                    condition = condition == null ? null : new
                    {
                        passCount = condition.PassCount, outcome = condition.Latest?.Outcome,
                        pips = new { line = strip != null ? "strip" : "outcome", shown = pipsShown, more = Math.Max(0, condition.PassCount - pipsShown) },
                        passes = condition.Passes.Select(p => new { pass = p.Pass, time = p.Time, value = p.Value, forced = p.Forced, kind = p.Kind, outcome = p.Outcome, measured = p.Measured, threshold = p.Threshold, draw = p.Draw, unavailable = p.Unavailable, instancePath = p.InstancePath }).ToArray(),
                    },
                };
            }).ToArray(),
        };
    }
}
