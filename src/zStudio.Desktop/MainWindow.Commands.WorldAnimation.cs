using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private void RegisterWorldAnimationCommands(StudioCommands registry)
    {
        RegisterAnimationModeCommands(registry);
        RegisterAnimationGraphCommands(registry);
        RegisterAnimationTimingCommands(registry);
    }

    private void RegisterAnimationModeCommands(StudioCommands registry)
    {
        RegisterJob(registry, "animation_mode", "Animation mode of the shown source map (the GUI's Open ▷ on a World tree definition row, Enter, a double-click, an object card's animation links, the Animation menu; Exit animation and Shift+Esc). open enters it on a definition (definition from scene_tree or source_world_definitions) or an exact binding (entry), or switches the opened one (the previous one goes to back); the opened binding becomes the scenario's primary start (in place when it is already a start; the previous primary stays a start; a binding of the definition whose primary an edit took away, also while the map was closed, stands in for it at its time until it matches again). Entering never moves the camera, plays, evaluates paths or measures a duration; drafts must be resolved first. exit returns to the map layout: the run stops, the scenario is kept (state.keptScenario) and the definition stays selected with its card. back and forward walk the opened definitions; binding plays another binding of the opened definition. layout sets the panel's view (structure, timing, split; split needs 1,300 DIP), its height, the right pane's tab, whether the World tree (remembered for the profile) and the Diagnostics strip (remembered for the project) show, and the strip's height (diagnosticsHeight, its splitter). state reads all of it. Each open, binding and exit replaces the preview ID.",
            [PreviewParameter,
             P("action", "string", "Operation.", true, "state", "open", "exit", "back", "forward", "binding", "layout"),
             P("document", "string", "Document lifetime ID from zstudio_state (open, binding)."),
             new("revision", "integer", "Expected document revision (open, binding).", Minimum: 0, Maximum: long.MaxValue),
             new("definition", "integer", "Source definition index (open): scene_tree's definition rows or source_world_definitions.", Minimum: 0),
             new("entry", "integer", "Exact binding (compiled entry): open opens on it (its definition when no definition is given); binding switches to it.", Minimum: 1),
             P("view", "string", "layout: the panel's view.", false, "structure", "timing", "split"),
             new("panelHeight", "number", "layout: the panel's height in DIP, at least 240 (the view keeps 160).", NumberMinimum: 240, NumberMaximum: 4000),
             P("rightTab", "string", "layout: the right pane's tab.", false, "inspector", "scenario"),
             P("tree", "boolean", "layout: show the World tree in Animation mode."),
             P("diagnostics", "boolean", "layout: show the Diagnostics strip under the panel."),
             new("diagnosticsHeight", "number", "layout: the Diagnostics strip's height in DIP, 96–700 (given at most the room the panel leaves above the view's 160, at least 96, the panel keeping its height; state reports the height shown; workspace_view toolsHeight in the mode sets the same height).", NumberMinimum: 96, NumberMaximum: 700)], false, async (a, token) =>
        {
            RequirePreview(a);
            string action = Text(a, "action");
            if (CompiledAnimationShown) { var compiledState = CompiledModeCommand(a, action); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded); return Result(compiledState); }
            switch (action)
            {
                case "state": break;
                case "open":
                case "binding":
                    {
                        var document = TargetDocument(a);
                        if (document != shownDocument || document.SourceWorld == null || document.Document is not SourceSceneSnapshot source)
                            throw new StudioCommandException("unsupported", "Open a source map first.");
                        if (!a.ContainsKey("revision") || document.Revision != a["revision"]!.GetValue<long>()) throw new StudioCommandException("revision_conflict", "Read the source map's current revision.");
                        int? entry = a.ContainsKey("entry") ? Int(a, "entry") : null;
                        int definition;
                        if (action == "binding")
                        {
                            var opened = animationMode ?? throw new StudioCommandException("not_ready", "Open an animation in Animation mode first.");
                            definition = opened.Definition;
                            if (entry == null) throw new StudioCommandException("invalid_argument", "Supply the binding's entry.");
                        }
                        else if (a.ContainsKey("definition")) definition = Int(a, "definition");
                        else if (entry is int binding) definition = source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Entries.Contains(binding))?.Index
                            ?? throw new StudioCommandException("stale_record", "No definition of this map has that binding.");
                        else throw new StudioCommandException("invalid_argument", "Supply a definition or an exact binding.");
                        await OpenAnimationModeAsync(document, definition, entry, token);
                        break;
                    }
                case "exit": RequireNoDrafts(); await ExitAnimationModeAsync(token); break;
                case "back": case "forward": RequireNoDrafts(); await AnimationHistoryAsync(action == "forward", token); break;
                case "layout":
                    if (!InAnimationMode) throw new StudioCommandException("not_ready", "Open an animation in Animation mode first.");
                    // The whole request is checked before any of it applies.
                    double? layoutHeight = a.ContainsKey("panelHeight") ? Number(a, "panelHeight") : null;
                    if (layoutHeight is double checkedHeight && (!double.IsFinite(checkedHeight) || checkedHeight < AnimationPanel.MinimumPanelHeight)) throw new StudioCommandException("invalid_argument", "The panel is at least 240 DIP high.");
                    if (a.ContainsKey("view") && Text(a, "view") is not ("structure" or "timing" or "split")) throw new StudioCommandException("invalid_argument", "The view is structure, timing or split.");
                    if (a.ContainsKey("rightTab") && Text(a, "rightTab") is not ("inspector" or "scenario")) throw new StudioCommandException("invalid_argument", "The right tab is inspector or scenario.");
                    double? stripHeight = DiagnosticsStripHeight(a);
                    if (a.ContainsKey("view")) animationView = Text(a, "view");
                    if (layoutHeight is double height) { animationPanelHeight = height; RememberAnimationLayout(); }
                    if (stripHeight is double strip) animationDiagnosticsHeight = strip;
                    if (a.ContainsKey("tree")) ViewModel.Settings.AnimationModeWorldTree = Flag(a, "tree");
                    if (a.ContainsKey("diagnostics")) { animationDiagnostics = Flag(a, "diagnostics"); RememberAnimationLayout(); }
                    if (a.ContainsKey("rightTab")) ShowAnimationRightTab(Text(a, "rightTab"));
                    ArrangeWorkspace();
                    break;
                default: throw new StudioCommandException("invalid_argument", "Unknown Animation mode operation.");
            }
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            return Result(AnimationModeState());
        });
        RegisterJob(registry, "animation_scenario", "The shown map's animation scenario (the Scenario tab and the command bar's scenario chip): starts on the shared clock (at most 32; the primary, id 00000000-0000-0000-0000-000000000000, is Animation mode's opened binding), included call paths, condition switches (IF and ELSEIF, per call path), inputs (player at start, custom or none; random seed; effects detail; phase; height; beam origin) and rules (prerequisites check, ignoreStarts (default) or ignore; range gates game or ignore; zone gates ignore (default) or view). Every change stops playback and re-evaluates the run up to the playhead, as a seek does, before it returns (a change during another's re-evaluation re-evaluates to the same playhead; conditions sets all its switches as one change); it is saved with the map in the project's .zstudio/settings.json and survives Exit, other definitions, difficulty changes and source edits: what a source edit leaves unmatched (a start, an included call path, an override) stays in it, marked and never run, is saved with it, and comes back when an undo, a redo or a later edit makes it match (read reports unmatched: counts per kind, the kinds kept, saved with the scenario, and the items in words, each naming its start or caller and the sequence; unmatched with resolve remove or keep and a kind is the Scenario tab's Remove · Keep, also with a draft open; excluding a call path, excludeAll or includeAll scope opened (the other starts' paths) drops the marked paths below it, under a stand-in the marked primary's same ones too; reset clears them too). read lists it (in the map layout too) with the call paths the starts reach, grouped by calling and called definition (Include all leaves back-links excluded): groups paged by offset and limit, each with its first 16 paths; read with group pages that group's paths. include includes paths with the calls that lead to them, or excludes them with every path below them. includeAll includes every reachable call path but back-links (scope opened: only those below the opened binding's start, the other starts' calls excluded). pick places the player or the beam origin on the surface under a point of the 3D view (DIP, as Pick in view does). reset returns everything to the defaults with the opened binding (after Exit, the kept one) as primary. fitRange sets the playback range to the latest start plus 30 s. starts, include, includeAll, excludeAll, conditions, pick and fitRange need Animation mode. On a compiled animation (its isolated preview) the scenario is the preview's, never saved; its inputs, rules and world are kept for the file's other animations, while call exclusions, condition switches, the root override and a custom range belong to the shown animation (reset returns all of them to the defaults): one start (the shown animation at 0 s; starts and fitRange are refused), every call included by default (allCalls; include excludes a path with what lies below it, includeAll brings every call back, excludeAll runs the animation alone), condition switches as in a map, inputs (player, seed, effectsDetail, phase, height, beamOrigin, and the world: range [0, end] or [] for Auto, world (a GameZ path; \"\" automatic), root (a scene node; null clears the override: Clear override)), pick on its view, rules and reset. On a compiled Whole world it reads and sets inputs {player} alone (the VTOL drop-window analysis measures from it).",
            [PreviewParameter,
             P("action", "string", "Operation.", true, "read", "starts", "include", "includeAll", "excludeAll", "conditions", "inputs", "pick", "rules", "unmatched", "reset", "fitRange"),
             new("starts", "array", "starts: the whole list; ids are GUIDs (a new start may omit its id), the primary keeps the empty GUID.", Items: new("", "object", "A start.", Properties:
                [P("id", "string", "Start GUID; omit for a new start."), new("entry", "integer", "Exact binding.", true, Minimum: 1), new("seconds", "number", "Start time on the shared clock.", true, NumberMinimum: 0, NumberMaximum: 3600)]), MinItems: 1, MaxItems: 32),
             new("paths", "array", "include: canonical call paths from read (calls.groups[].paths[].path).", Items: new("", "string", "Call path."), MinItems: 1, MaxItems: 4096),
             P("included", "boolean", "include: include (true) or exclude (false)."),
             new("conditions", "array", "conditions: switches to set, in order.", Items: new("", "object", "A switch.", Properties:
                [P("instancePath", "string", "Call path of the instance; empty for the opened binding's own conditions.", true), new("entry", "integer", "Binding of the condition.", true, Minimum: 1),
                 P("event", "string", "IF or ELSEIF event GUID.", true), P("value", "string", "game (the game's rule with the scenario's inputs), true or false.", true, "game", "true", "false")]), MinItems: 1, MaxItems: 4096),
             new("player", "object", "inputs: where range conditions and gates measure from.", Properties:
                [P("kind", "string", "atStart (the first AIV spawn, moved by callbacks 10 and 17, frozen by 11), custom (x, y, z) or none.", true, "atStart", "custom", "none"),
                 new("x", "number", "Custom X."), new("y", "number", "Custom Y."), new("z", "number", "Custom Z.")]),
             new("seed", "integer", "inputs: the preview's random seed (random in the game).", Minimum: int.MinValue, Maximum: int.MaxValue),
             P("effectsDetail", "string", "inputs: the game option ANIMATION_LOD tests.", false, "High", "Medium", "Low"),
             P("phase", "string", "inputs: runtime or cleanup.", false, "runtime", "cleanup"),
             new("height", "number", "inputs: preview-only world height offset.", NumberMinimum: -999, NumberMaximum: 999),
             new("beamOrigin", "array", "inputs: where beams start that take the activation's input position; [] is the root (default).", Items: new("", "number", "Coordinate."), MaxItems: 3),
             new("range", "array", "inputs, a compiled animation: the playback range [0, end] in seconds; [] is Auto (the measured duration).", Items: new("", "number", "Seconds.", NumberMinimum: 0, NumberMaximum: 3600), MaxItems: 2),
             P("world", "string", "inputs, a compiled animation: the GameZ scene it binds in (Choose GameZ…); \"\" is the automatic one beside the file."),
             new("root", "integer", "inputs, a compiled animation: bind its root to this scene node (Bind root…); null clears the override (Clear override).", Minimum: 0, Maximum: int.MaxValue, Nullable: true),
             P("target", "string", "pick: what to place.", false, "player", "beamOrigin"),
             new("screenPoint", "array", "pick: a point of the 3D view in DIP from its top left.", Items: new("", "number", "DIP."), MinItems: 2, MaxItems: 2),
             P("prerequisites", "string", "rules: check, ignoreStarts or ignore.", false, "check", "ignoreStarts", "ignore"),
             P("rangeGates", "string", "rules: game or ignore.", false, "game", "ignore"),
             P("zoneGates", "string", "rules: ignore or view (the view's zones).", false, "ignore", "view"),
             P("scope", "string", "includeAll: all (every reachable call path but back-links; default) or opened (every path below the opened binding's start, excluding the other starts' calls: Include ▾ → This animation only).", false, "all", "opened"),
             P("group", "string", "read: a group of call paths (calls.groups[].group) whose paths to page."),
             P("resolve", "string", "unmatched: remove the marked items of kind for good, or keep them marked (they come back when an undo, a redo or an edit makes them match; the question is not asked again).", false, "remove", "keep"),
             P("kind", "string", "unmatched: which marked items: starts, paths (included call paths), conditions (overrides) or all.", false, "starts", "paths", "conditions", "all"),
             new("offset", "integer", "read: the first group, or with group the first path.", Minimum: 0, Maximum: int.MaxValue),
             new("limit", "integer", "read: groups, or with group paths, per page (at most 100).", Minimum: 1, Maximum: 100)], false, async (a, token) =>
        {
            RequirePreview(a);
            string action = Text(a, "action");
            // Remove or Keep of marked items changes no run, as in the Scenario tab: allowed with a draft open.
            if (action is not ("read" or "unmatched")) RequireNoDrafts();
            if (CompiledAnimationShown) return Result(await CompiledScenarioCommandAsync(a, action));
            if (CompiledWorldShown && action is "read" or "inputs") return Result(await CompiledWorldScenarioAsync(a, action));
            RequireScenarioMap();
            object? result = null;
            // This call awaits only the re-evaluation it starts: an earlier call's failed one is that call's to report.
            var before = worldAnimationReevaluation;
            switch (action)
            {
                case "read": break;
                case "starts":
                    {
                        var values = a["starts"] as JsonArray ?? throw new StudioCommandException("invalid_argument", "Supply the starts.");
                        List<AnimationScheduledStart> starts = [];
                        foreach (var item in values.OfType<JsonObject>())
                            starts.Add(new(item.ContainsKey("id") ? GuidArg(item, "id") : Guid.NewGuid(), Int(item, "entry"), Number(item, "seconds")));
                        ScenarioStarts(starts); break;
                    }
                case "include":
                    {
                        var paths = (a["paths"] as JsonArray ?? throw new StudioCommandException("invalid_argument", "Supply the call paths.")).Select(p => p!.GetValue<string>()).ToArray();
                        if (!a.ContainsKey("included")) throw new StudioCommandException("invalid_argument", "Supply included.");
                        ScenarioInclude(paths, Flag(a, "included")); break;
                    }
                case "includeAll":
                    string scope = a.ContainsKey("scope") ? Text(a, "scope") : "all";
                    if (scope is not ("all" or "opened")) throw new StudioCommandException("invalid_argument", "includeAll's scope is all or opened.");
                    result = ScenarioIncludeAll(token, opened: scope == "opened"); break;
                case "excludeAll": ScenarioExcludeAll(); break;
                case "conditions":
                    {
                        // Every switch of the call is one scenario change: one stop and one re-evaluation to the playhead.
                        List<(string, int, Guid, AnimationConditionChoice)> switches = [];
                        foreach (var item in (a["conditions"] as JsonArray ?? throw new StudioCommandException("invalid_argument", "Supply the conditions.")).OfType<JsonObject>())
                        {
                            if (!Guid.TryParse(Text(item, "event"), out Guid ev)) throw new StudioCommandException("invalid_argument", "Use an event GUID.");
                            var choice = Text(item, "value") switch { "true" => AnimationConditionChoice.True, "false" => AnimationConditionChoice.False, "game" => AnimationConditionChoice.Game, _ => throw new StudioCommandException("invalid_argument", "A switch is game, true or false.") };
                            switches.Add((Text(item, "instancePath"), Int(item, "entry"), ev, choice));
                        }
                        ScenarioConditions(switches);
                        break;
                    }
                case "inputs":
                    {
                        var next = worldAnimationConfiguration;
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
                        try { ScenarioInputs(next); }
                        catch (ArgumentException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
                        break;
                    }
                case "pick":
                    {
                        if (a["screenPoint"] is not JsonArray { Count: 2 } point) throw new StudioCommandException("invalid_argument", "Supply screenPoint [x, y].");
                        RequireOpenedBinding();
                        var placed = ScenarioPick(Text(a, "target", "player"), new Point(point[0]!.GetValue<double>(), point[1]!.GetValue<double>()));
                        result = new { picked = new[] { placed.X, placed.Y, placed.Z } };
                        break;
                    }
                case "rules":
                    {
                        var next = worldAnimationConfiguration;
                        if (a.ContainsKey("prerequisites")) next = next with { Prerequisites = PrerequisiteRule(Text(a, "prerequisites")) };
                        if (a.ContainsKey("rangeGates")) next = next with { RangeGates = Text(a, "rangeGates") == "ignore" ? AnimationRangeGateRule.Ignore : AnimationRangeGateRule.Game };
                        if (a.ContainsKey("zoneGates")) next = next with { ZoneGates = Text(a, "zoneGates") == "view" ? AnimationZoneGateRule.View : AnimationZoneGateRule.Ignore };
                        ScenarioInputs(next); break;
                    }
                case "unmatched": ScenarioUnmatched(Text(a, "resolve"), Text(a, "kind", "all")); break;
                case "reset": ScenarioReset(); break;
                case "fitRange": RequireOpenedBinding(); await FitWorldAnimationScenario(); break;
                default: throw new StudioCommandException("invalid_argument", "Unknown scenario operation.");
            }
            // The change shows at once: the run is re-evaluated up to the playhead before the result returns.
            if (!ReferenceEquals(worldAnimationReevaluation, before)) await worldAnimationReevaluation;
            return Result(new { scenario = ScenarioReadState(a), result, animation = InAnimationMode ? WorldAnimationCommandState : null });
        });
    }

    /// <summary>animation_mode layout's diagnosticsHeight (the Diagnostics strip under the panel), checked: 96–700 DIP.</summary>
    private static double? DiagnosticsStripHeight(JsonObject a)
    {
        if (!a.ContainsKey("diagnosticsHeight")) return null;
        double height = Number(a, "diagnosticsHeight");
        return double.IsFinite(height) && height is >= 96 and <= 700 ? height : throw new StudioCommandException("invalid_argument", "The Diagnostics strip is 96–700 DIP high.");
    }
    /// <summary>Animation mode as animation_mode state reports it.</summary>
    private object AnimationModeState()
    {
        var source = shownDocument?.Document as SourceSceneSnapshot;
        object Definition(int definition, int entry) => new { definition, entry = entry > 0 ? (int?)entry : null, name = DefinitionName(definition) };
        object? opened = null;
        if (animationMode is { } mode && source?.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == mode.Definition) is { } definition)
            opened = new
            {
                document = mode.Document.SessionId, revision = mode.Revision, definition = mode.Definition, name = definition.Name, file = definition.File,
                entry = mode.Entry > 0 ? (int?)mode.Entry : null, binding = mode.Entry > 0 ? BindingLabel(mode.Entry) : null, bindings = definition.Entries.Take(256).ToArray(),
                bound = mode.Entry > 0,
            };
        var scenario = worldAnimationConfiguration;
        object? kept = KeptAnimation() is { } previous
            ? new { definition = previous.Definition, entry = previous.Entry > 0 ? (int?)previous.Entry : null, name = DefinitionName(previous.Definition), starts = scenario.Starts.Count, summary = ScenarioSummary(scenario), customized = scenario.IsCustomized }
            : null;
        return new
        {
            preview = previewId, active = InAnimationMode, opened,
            back = animationBack.Select(h => Definition(h.Definition, h.Entry)).ToArray(), forward = animationForward.Select(h => Definition(h.Definition, h.Entry)).ToArray(),
            keptScenario = kept, scenario = ScenarioSummary(scenario),
            layout = new
            {
                view = animationView ?? DefaultAnimationView(), shownView = animationPanel?.ShownView, splitAvailable = animationPanel?.SplitAvailable ?? true, compact = animationPanel?.Compact ?? false,
                panelHeight = InAnimationMode && animationPanel?.ActualHeight > 0 ? animationPanel.ActualHeight : animationPanelHeight,
                rightTab = animationRightTab, tree = ViewModel.Settings.AnimationModeWorldTree, diagnostics = animationDiagnostics,
                // The strip's height as shown (clamped to the room the panel leaves), else the height it will ask for.
                diagnosticsHeight = InAnimationMode && animationDiagnostics && animationDiagnosticsShown > 0 ? animationDiagnosticsShown : animationDiagnosticsHeight,
            },
        };
    }
}
