using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>
/// MCP on a compiled animation (design/animation-redesign.md §20): animation_mode state and layout, animation_graph,
/// animation_timing and animation_scenario read the same compiled preview the GUI shows, with its identities; nothing edits
/// stored data (the scenario is the preview's).
/// </summary>
public partial class MainWindow
{
    /// <summary>animation_mode on a compiled animation: its state and layout (open, exit and history are a source map's).</summary>
    private object CompiledModeCommand(JsonObject a, string action)
    {
        var editor = RequireCompiled();
        var kept = ShownKept!;
        switch (action)
        {
            case "state": break;
            case "layout":
                {
                    // The whole request is checked before any of it applies.
                    if (a.ContainsKey("tree")) throw new StudioCommandException("unsupported", "A compiled file has no World tree: the Navigator stays on the left (workspace_view navigator).");
                    double? height = a.ContainsKey("panelHeight") ? Number(a, "panelHeight") : null;
                    if (height is double h && (!double.IsFinite(h) || h < AnimationPanel.MinimumPanelHeight)) throw new StudioCommandException("invalid_argument", "The panel is at least 240 DIP high.");
                    string? view = a.ContainsKey("view") ? Text(a, "view") : null;
                    if (view is not (null or "structure" or "timing" or "split")) throw new StudioCommandException("invalid_argument", "The view is structure, timing or split.");
                    string? tab = a.ContainsKey("rightTab") ? Text(a, "rightTab") : null;
                    if (tab is not (null or "inspector" or "scenario")) throw new StudioCommandException("invalid_argument", "The right tab is inspector or scenario.");
                    bool? diagnostics = a.ContainsKey("diagnostics") ? Flag(a, "diagnostics") : null;
                    double? stripHeight = DiagnosticsStripHeight(a);
                    if (view != null) { kept.View = view; compiledPanel?.SetView(view); }
                    if (height is double panel) kept.PanelHeight = panel;
                    if (diagnostics is bool strip) kept.Diagnostics = strip;
                    if (stripHeight is double stripTall) Layout.ToolsHeight = stripTall;
                    if (tab != null) ShowCompiledTab(tab);
                    ArrangeWorkspace();
                    break;
                }
            default: throw new StudioCommandException("unsupported", "A compiled animation is shown when it is selected (select_asset); open, exit, back, forward and binding belong to a source map's Animation mode.");
        }
        var entry = shownDocument!.Document.Animations!.Entries[editor.EntryIndex];
        return new
        {
            preview = previewId, active = true, compiled = true, readOnly = true,
            opened = new { document = shownDocument.SessionId, entry = editor.EntryIndex, name = entry.Name, root = entry.RootName, file = CompiledFileName(shownDocument), bytes = entry.SourceLength },
            scenario = CompiledScenarioSummary(editor.Scenario),
            layout = new
            {
                view = kept.View ?? (Workbench.ActualWidth >= 2200 ? "split" : "structure"), shownView = compiledPanel?.ShownView, splitAvailable = compiledPanel?.SplitAvailable ?? true, compact = compiledPanel?.Compact ?? false,
                panelHeight = compiledPanel?.ActualHeight > 0 ? compiledPanel.ActualHeight : kept.PanelHeight, rightTab = kept.RightTab, diagnostics = kept.Diagnostics, diagnosticsHeight = Layout.ToolsHeight,
            },
        };
    }

    private object CompiledGraphSummary()
    {
        var layout = compiledLayout;
        var view = compiledGraphView;
        var overlay = compiledOverlay;
        return new
        {
            id = layout?.Graph.Id, entry = layout?.Graph.Entry, compiled = true,
            nodeCount = layout?.Graph.NodeCount, edgeCount = layout?.Graph.EdgeCount, routes = layout?.Routes.Count,
            expanded = layout?.Graph.Expanded.Order(StringComparer.Ordinal).ToArray(), expansionTruncated = layout?.Graph.ExpansionTruncated,
            bounds = layout == null ? null : new { width = layout.Bounds.Width, height = layout.Bounds.Height },
            selection = compiledSelection,
            viewport = view == null ? null : new
            {
                zoom = view.Zoom, center = new[] { view.Center.X, view.Center.Y }, visible = new { x = view.Visible.X, y = view.Visible.Y, width = view.Visible.Width, height = view.Visible.Height },
                level = view.Level switch { AnimationGraphLevel.Rows => "rows", AnimationGraphLevel.Compact => "compact", AnimationGraphLevel.Headers => "headers", _ => "chips" },
                runOverlay = ShownKept?.RunOverlay ?? true, shown = view.IsVisible, realised = view.RealisedCount,
            },
            run = overlay == null ? null : new
            {
                time = overlay.Time, stale = false,
                player = overlay.Player is { } player ? new { position = player.Position is { } p ? new[] { p.X, p.Y, p.Z } : null, source = player.Source, frozen = player.Frozen } : null,
            },
        };
    }

    /// <summary>animation_graph on a compiled animation: read, expand, collapse, select, inspector, viewport and callers.</summary>
    private async Task<object> CompiledGraphCommandAsync(JsonObject a, string action, int offset, int limit)
    {
        var layout = RequireCompiledGraph();
        RefreshCompiledGraph();
        layout = compiledLayout ?? layout;
        object? result = null;
        switch (action)
        {
            case "read":
                result = Text(a, "section", "nodes") switch
                {
                    "nodes" => GraphNodesPage(layout, offset, limit, compiledOverlay, compiledGraphView!),
                    "edges" => GraphEdgesPage(layout, offset, limit, compiledOverlay, animation!.Scenario),
                    "rows" => GraphRowsPage(layout, a.ContainsKey("node") ? Text(a, "node") : throw new StudioCommandException("invalid_argument", "Supply the sequence node."), offset, limit, compiledOverlay, animation!.Scenario),
                    _ => throw new StudioCommandException("invalid_argument", "Read nodes, edges or rows."),
                };
                break;
            case "expand" or "collapse":
                {
                    bool value = action == "expand";
                    if (Flag(a, "all")) CompiledPresentation(value ? "expandAll" : "collapseAll", null);
                    else if (a.ContainsKey("row")) CompiledPresentation(value ? "expandRow" : "collapseRow", Text(a, "row"));
                    else if (a.ContainsKey("node"))
                    {
                        string node = Text(a, "node");
                        var placement = layout.Node(node) ?? throw new StudioCommandException("stale_record", "The graph has no node with that identity; read it again.");
                        int? member = a.ContainsKey("member") ? Int(a, "member") : null;
                        if (placement.Node.Kind == AnimationGraphNodeKind.Reference) CompiledExpand(node, value, member);
                        else if (placement.Node.Frame?.Group != null && member is int shown && value) CompiledMember(node, shown);
                        else if (member != null) throw new StudioCommandException("invalid_argument", "A member is chosen on a reference node or an expanded group's frame.");
                        else if (placement.Node.Kind == AnimationGraphNodeKind.Sequence) CompiledPresentation(value ? Flag(a, "showAll") ? "showAll" : "open" : "collapse", node);
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
                    await CompiledSelectAsync(id);
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    result = new { selection = compiledSelection, inspector = BuildCompiledInspection()?.Json() };
                    break;
                }
            case "inspector":
                RefreshCompiledInspector();
                result = new { selection = compiledSelection, inspector = BuildCompiledInspection()?.Json() };
                break;
            case "viewport":
                {
                    var view = compiledGraphView!;
                    if (!view.IsVisible || view.ActualWidth <= 0) throw new StudioCommandException("not_ready", "Show the Structure view first (animation_mode layout view structure or split).");
                    if (a.ContainsKey("runOverlay")) { ShownKept!.RunOverlay = Flag(a, "runOverlay"); view.SetRunOverlay(ShownKept.RunOverlay); }
                    if (Flag(a, "fit")) view.FitView();
                    if (a.ContainsKey("zoom"))
                    {
                        double zoom = Number(a, "zoom");
                        if (!double.IsFinite(zoom) || zoom is < AnimationGraphView.MinimumZoom or > AnimationGraphView.MaximumZoom) throw new StudioCommandException("invalid_argument", "Zoom is 0.25–2.");
                        view.ZoomTo(zoom);
                    }
                    if (a["center"] is JsonArray { Count: 2 } center)
                    {
                        double x = center[0]!.GetValue<double>(), y = center[1]!.GetValue<double>();
                        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new StudioCommandException("invalid_argument", "Use a finite centre.");
                        view.CenterOn(new Point(x, y));
                    }
                    if (a.ContainsKey("reveal"))
                    {
                        string id = Text(a, "reveal");
                        if (layout.Node(id) == null && layout.Item(id) == null) throw new StudioCommandException("stale_record", "The graph has no node or row with that identity; read it again.");
                        view.Reveal(id);
                    }
                    break;
                }
            case "callers":
                {
                    int entry = a.ContainsKey("entry") ? Int(a, "entry") : layout.Graph.Entry;
                    if (entry <= 0 || entry >= layout.Graph.Index.Package.Entries.Count) throw new StudioCommandException("invalid_argument", "Choose an animation of this file.");
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
        return new { graph = CompiledGraphSummary(), result };
    }

    private object CompiledTimingPresentation()
    {
        var view = compiledTimingView!; var timing = compiledTiming;
        return new
        {
            view = view.View, visibleRange = new[] { view.VisibleStart, view.VisibleEnd }, labelWidth = view.LabelWidth, thresholds = view.Thresholds,
            selection = new { lane = view.SelectedLane, mark = view.SelectedMark, @event = view.SelectedEvent },
            time = view.State.Time, range = new[] { view.State.RangeStart, view.State.RangeEnd }, evaluated = view.State.Evaluated, stale = false,
            message = view.State.Message ?? (timing != null && !view.State.Evaluated ? "Nothing has run yet. Play, or click the ruler to seek; the preview runs the scenario up to that time." : null),
            lanes = timing?.Lanes.Count ?? 0, allLanes = timing?.AllLanes.Count ?? 0, marks = timing?.MarkCount ?? 0, log = timing?.Log.Count ?? 0,
            retained = timing?.Retained ?? 0, discarded = timing?.Discarded ?? 0, runExtent = timing?.RunStart is double a ? new[] { a, timing.RunEnd ?? a } : null,
            truncated = timing?.Truncated ?? false, phasesDropped = timing?.PhasesDropped ?? 0, building = false, compiled = true,
        };
    }

    /// <summary>animation_timing on a compiled animation: the same lanes, marks, Log and options; starts and thresholds are read only.</summary>
    private async Task<object> CompiledTimingCommandAsync(JsonObject a, string action, int offset, int limit)
    {
        var paging = new JsonObject { ["offset"] = offset, ["limit"] = limit };
        object? result = null;
        switch (action)
        {
            case "read": RequireCompiledTiming(); break;
            case "lanes":
                result = Page(RequireCompiledTiming().Lanes, paging, project: TimingLaneJson, maximumRowBytes: TimingLaneBytes).Data;
                break;
            case "marks":
                {
                    var lane = RequireCompiledLane(Text(a, "lane"));
                    var items = lane.Marks.Select(m => (m.Start, Mark: (AnimationTimingMark?)m, Band: (AnimationTimingBand?)null, Index: 0))
                        .Concat(lane.Bands.Select((b, i) => (b.Start, Mark: (AnimationTimingMark?)null, Band: (AnimationTimingBand?)b, Index: i))).OrderBy(x => x.Start);
                    result = Page(items, paging, project: x => x.Mark is { } m ? TimingMarkJson(m, _ => false) : TimingBandJson(lane.Id, x.Index, x.Band!),
                        maximumRowBytes: x => TimingItemBytes(lane.Id, x.Mark, x.Band)).Data;
                    break;
                }
            case "log":
                result = Page(RequireCompiledTiming().Log, paging, project: r => new { time = r.Time, instance = r.Instance, sequence = r.Sequence, @event = r.Event, result = r.Result, lane = r.Lane, mark = r.Mark, instanceId = r.InstanceId, occurrence = r.Occurrence, entry = r.Entry, eventId = r.EventId },
                    maximumRowBytes: TimingLogBytes).Data;
                break;
            case "options":
                RequireCompiledTiming();
                ApplyTimingOptions(a, compiledTimingView!);
                break;
            case "select":
                {
                    string lane = Text(a, "lane");
                    if (lane.Length == 0) { RequireCompiledTiming(); compiledTimingView!.Select(null); break; }
                    await CompiledSelectLaneAsync(lane); break;
                }
            case "occurrence": result = await CompiledSelectMarkAsync(Text(a, "lane"), Text(a, "mark"), true); break;
            case "expand":
                if (!a.ContainsKey("expanded")) throw new StudioCommandException("invalid_argument", "Supply expanded.");
                CompiledTimingToggle(Text(a, "lane"), Flag(a, "expanded")); break;
            case "threshold": throw new StudioCommandException("read_only", "A compiled file is read only: edit start thresholds in a source project's map (Animation mode).");
            default: throw new StudioCommandException("invalid_argument", "Unknown Timing operation.");
        }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        RefreshCompiledTiming();
        return new { timing = CompiledTimingPresentation(), result };
    }

    /// <summary>
    /// animation_scenario on a compiled animation (design §20): conditions, include, includeAll, excludeAll, inputs (player,
    /// seed, effects detail, phase, height, beam origin, range, world, root: null clears the override), pick, rules and reset.
    /// Starts are the shown animation's one, at 0 s.
    /// </summary>
    private async Task<object> CompiledScenarioCommandAsync(JsonObject a, string action)
    {
        var editor = RequireCompiled();
        object? result = null;
        switch (action)
        {
            case "read": break;
            case "starts" or "fitRange": throw new StudioCommandException("unsupported", "The compiled preview plays its one animation from 0 s: select another animation in the file (select_asset); the range is inputs {range}.");
            case "include":
                {
                    var paths = (a["paths"] as JsonArray ?? throw new StudioCommandException("invalid_argument", "Supply the call paths.")).Select(p => p!.GetValue<string>()).ToArray();
                    if (!a.ContainsKey("included")) throw new StudioCommandException("invalid_argument", "Supply included.");
                    await CompiledIncludeAsync(paths, Flag(a, "included")); break;
                }
            case "includeAll": result = await CompiledIncludeAllAsync(); break;
            case "excludeAll": await CompiledExcludeAllAsync(); break;
            case "conditions":
                {
                    List<(string, int, Guid, AnimationConditionChoice)> switches = [];
                    foreach (var item in (a["conditions"] as JsonArray ?? throw new StudioCommandException("invalid_argument", "Supply the conditions.")).OfType<JsonObject>())
                    {
                        if (!Guid.TryParse(Text(item, "event"), out Guid ev)) throw new StudioCommandException("invalid_argument", "Use an event GUID.");
                        var choice = Text(item, "value") switch { "true" => AnimationConditionChoice.True, "false" => AnimationConditionChoice.False, "game" => AnimationConditionChoice.Game, _ => throw new StudioCommandException("invalid_argument", "A switch is game, true or false.") };
                        switches.Add((Text(item, "instancePath"), Int(item, "entry"), ev, choice));
                    }
                    await CompiledConditionsAsync(switches); break;
                }
            case "inputs":
                {
                    // The whole request is checked before any of it applies: the bound root's node and the range first.
                    int? root = a.ContainsKey("root") && a["root"] is not null ? Int(a, "root") : null;
                    if (root is int node && (editor.PreviewContext is not { } prepared || node < 0 || node >= prepared.Scene.Nodes.Count))
                        throw new StudioCommandException(editor.PreviewContext == null ? "not_ready" : "invalid_argument", editor.PreviewContext == null ? "No scene is loaded." : "The scene has no such node.");
                    if (a["range"] is JsonArray checkedRange && checkedRange.Count != 0 && !(checkedRange.Count == 2 && checkedRange[0]!.GetValue<double>() == 0))
                        throw new StudioCommandException("invalid_argument", "The range is [0, end] in seconds, or [] for Auto.");
                    if (a["range"] is JsonArray { Count: 2 } endRange && endRange[1]!.GetValue<double>() is var end && (!double.IsFinite(end) || end < AnimationPlayer.StepSeconds || end > AnimationSeconds.Maximum))
                        throw new StudioCommandException("invalid_argument", "The range ends between 1/60 second and 3,600 seconds.");
                    if (a.ContainsKey("world") && Text(a, "world") is { Length: > 0 } chosenWorld && !System.IO.File.Exists(chosenWorld)) throw new StudioCommandException("not_found", "No such GameZ file.");
                    var next = editor.Scenario;
                    if (a["player"] is JsonObject player)
                        next = next with
                        {
                            Player = Text(player, "kind") switch
                            {
                                "atStart" => AnimationPlayerReference.AtStart, "none" => AnimationPlayerReference.None,
                                "custom" => AnimationPlayerReference.Custom(new((float)Number(player, "x"), (float)Number(player, "y"), (float)Number(player, "z"))),
                                _ => throw new StudioCommandException("invalid_argument", "The player is atStart, custom or none."),
                            }
                        };
                    if (a.ContainsKey("seed")) next = next with { Seed = Int(a, "seed") };
                    if (a.ContainsKey("effectsDetail")) next = next with { EffectsDetail = Enum.Parse<AnimationEffectsDetail>(Text(a, "effectsDetail")) };
                    if (a.ContainsKey("phase")) next = next with { Phase = Text(a, "phase") == "cleanup" ? AnimationScenarioPhase.Cleanup : AnimationScenarioPhase.Runtime };
                    if (a.ContainsKey("height")) next = next with { Height = (float)Number(a, "height") };
                    if (a["beamOrigin"] is JsonArray origin)
                        next = next with
                        {
                            BeamOrigin = origin.Count == 0 ? null : origin.Count == 3 ? new Vector3(origin[0]!.GetValue<float>(), origin[1]!.GetValue<float>(), origin[2]!.GetValue<float>())
                                : throw new StudioCommandException("invalid_argument", "The beam origin is three numbers, or [] for the root.")
                        };
                    try { if (!next.Equals(editor.Scenario)) await CompiledScenarioAsync(next); }
                    catch (ArgumentException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
                    // The compiled preview's world: the mission scene, the bound root (null clears the override) and the range.
                    if (a.ContainsKey("world")) await CompiledWorldAsync(Text(a, "world") is { Length: > 0 } world ? world : null);
                    if (a.ContainsKey("root")) await CompiledRootAsync(a["root"] is null ? null : Int(a, "root"));
                    if (a["range"] is JsonArray range)
                    {
                        if (range.Count == 0) await CompiledRangeAsync(null);
                        else if (range.Count == 2 && range[0]!.GetValue<double>() == 0) await CompiledRangeAsync(range[1]!.GetValue<double>());
                        else throw new StudioCommandException("invalid_argument", "The range is [0, end] in seconds, or [] for Auto.");
                    }
                    break;
                }
            case "pick":
                {
                    if (a["screenPoint"] is not JsonArray { Count: 2 } point) throw new StudioCommandException("invalid_argument", "Supply screenPoint [x, y].");
                    var placed = await CompiledPickAsync(Text(a, "target", "player"), new Point(point[0]!.GetValue<double>(), point[1]!.GetValue<double>()));
                    result = new { picked = new[] { placed.X, placed.Y, placed.Z } };
                    break;
                }
            case "rules":
                {
                    var next = editor.Scenario;
                    if (a.ContainsKey("prerequisites")) next = next with { Prerequisites = PrerequisiteRule(Text(a, "prerequisites")) };
                    if (a.ContainsKey("rangeGates")) next = next with { RangeGates = Text(a, "rangeGates") == "ignore" ? AnimationRangeGateRule.Ignore : AnimationRangeGateRule.Game };
                    if (a.ContainsKey("zoneGates")) next = next with { ZoneGates = Text(a, "zoneGates") == "view" ? AnimationZoneGateRule.View : AnimationZoneGateRule.Ignore };
                    await CompiledScenarioAsync(next); break;
                }
            case "reset": await CompiledResetAsync(); break;
            default: throw new StudioCommandException("invalid_argument", "Unknown scenario operation.");
        }
        return new { scenario = CompiledScenarioReadState(a), result, animation = editor.PreviewState() };
    }
    /// <summary>A compiled Whole world without an animation: its drop-window analysis measures from the scenario's player.</summary>
    private bool CompiledWorldShown => animation == null && IsAiWorld && shownDocument is { SourceWorld: null } && !ViewModel.IsSourceProject;
    /// <summary>animation_scenario on a compiled Whole world: read, and inputs {player} for the VTOL drop-window analysis.</summary>
    private Task<object> CompiledWorldScenarioAsync(JsonObject a, string action)
    {
        if (action == "inputs")
        {
            if (a.Select(p => p.Key).Any(k => k is not ("preview" or "action" or "player"))) throw new StudioCommandException("unsupported", "A compiled Whole world's scenario takes player alone (the player its VTOL drop-window analysis measures from); select an animation of its anim.zbd for the rest.");
            if (a["player"] is not JsonObject player) throw new StudioCommandException("invalid_argument", "Supply the player.");
            var reference = Text(player, "kind") switch
            {
                "atStart" => AnimationPlayerReference.AtStart, "none" => AnimationPlayerReference.None,
                "custom" => AnimationPlayerReference.Custom(new((float)Number(player, "x"), (float)Number(player, "y"), (float)Number(player, "z"))),
                _ => throw new StudioCommandException("invalid_argument", "The player is atStart, custom or none."),
            };
            SetWorldAnimationScenario(worldAnimationConfiguration with { Player = reference });
        }
        var scenario = worldAnimationConfiguration;
        return Task.FromResult<object>(new
        {
            scenario = new { compiledWorld = true, inputs = new { player = new { kind = scenario.Player.Kind switch { AnimationPlayerReferenceKind.Custom => "custom", AnimationPlayerReferenceKind.None => "none", _ => "atStart" }, position = scenario.CustomPlayer is { } p ? new[] { p.X, p.Y, p.Z } : null } } },
            parachute = ParachuteState(),
        });
    }
    /// <summary>The compiled scenario as animation_scenario read reports it (as a map's, plus its world, bound root and range).</summary>
    private object CompiledScenarioReadState(JsonObject a)
    {
        var view = BuildCompiledScenarioView();
        int offset = a.ContainsKey("offset") ? Int(a, "offset") : 0, limit = a.ContainsKey("limit") ? Int(a, "limit") : 100;
        if (offset < 0 || limit is < 1 or > 100) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–100.");
        var scenario = view.Scenario;
        const int FirstPaths = 16;
        object Path(ScenarioPathRow p) => new { path = p.Path, label = p.Label, included = p.Included, backLink = p.BackLink };
        object? groupPage = null;
        if (a.ContainsKey("group"))
        {
            string key = Text(a, "group");
            var group = view.Groups.FirstOrDefault(g => g.Key == key) ?? throw new StudioCommandException("stale_record", "The animation reaches no such group of calls; read the scenario again.");
            var rows = group.Paths.Skip(offset).Take(limit).Select(Path).ToArray();
            groupPage = new { group = group.Key, caller = group.Label, total = group.Paths.Count, offset, nextOffset = offset + rows.Length < group.Paths.Count ? (int?)(offset + rows.Length) : null, paths = rows };
        }
        var shownGroups = groupPage != null ? [] : view.Groups.Skip(offset).Take(limit).ToArray();
        var compiled = view.Compiled!;
        return new
        {
            file = compiled.File, compiled = true, summary = CompiledScenarioSummary(scenario), customized = CompiledCustomized(), allCalls = scenario.AllCalls,
            excluded = scenario.ExcludedCalls.Take(100).ToArray(), excludedCount = scenario.ExcludedCalls.Count,
            starts = view.Starts.Select(s => new { id = s.Id, entry = s.Entry, name = s.Name, root = s.Root, seconds = s.Seconds, primary = s.Primary }).ToArray(),
            calls = new
            {
                included = view.Included, reachable = view.Reachable, unresolved = view.Unresolved, truncated = view.Truncated, includeAll = view.IncludeResult, totalGroups = view.Groups.Count,
                groups = shownGroups.Select(g => new
                {
                    group = g.Key, caller = g.Label, included = g.Included, total = g.Total, backLink = g.BackLink,
                    paths = g.Paths.Take(FirstPaths).Select(Path).ToArray(), pathsNextOffset = g.Paths.Count > FirstPaths ? (int?)FirstPaths : null,
                }).ToArray(),
                nextOffset = groupPage == null && offset + shownGroups.Length < view.Groups.Count ? (int?)(offset + shownGroups.Length) : null,
                groupPage,
            },
            conditions = view.Overrides.Take(100).Select(o => new { instancePath = o.Override.InstancePath, entry = o.Override.Entry, @event = o.Override.Event, value = o.Override.Value ? "true" : "false", label = o.Label }).ToArray(),
            conditionCount = scenario.Conditions.Count,
            inputs = new
            {
                player = new { kind = scenario.Player.Kind switch { AnimationPlayerReferenceKind.Custom => "custom", AnimationPlayerReferenceKind.None => "none", _ => "atStart" }, position = scenario.CustomPlayer is { } p ? new[] { p.X, p.Y, p.Z } : null },
                seed = scenario.Seed, effectsDetail = scenario.EffectsDetail.ToString(), phase = scenario.Phase == AnimationScenarioPhase.Cleanup ? "cleanup" : "runtime", height = scenario.Height,
                beamOrigin = scenario.BeamOrigin is { } o ? new[] { o.X, o.Y, o.Z } : null,
                range = compiled.CustomRange ? new[] { 0, compiled.RangeEnd } : null, rangeDescription = compiled.Range,
                world = new { shown = compiled.World, chosen = compiled.WorldChosen },
                root = new { bound = compiled.Root, overridden = compiled.RootOverridden, node = animation?.PreviewContext is { } context ? context.ResolveRoot(shownDocument!.Document.Animations!.Entries[animation.EntryIndex]) : (int?)null },
            },
            rules = new { prerequisites = AnimationScenarioPanel.RuleName(scenario.Prerequisites), rangeGates = scenario.RangeGates == AnimationRangeGateRule.Game ? "game" : "ignore", zoneGates = scenario.ZoneGates == AnimationZoneGateRule.View ? "view" : "ignore" },
            picking = compiledPick,
        };
    }
}
