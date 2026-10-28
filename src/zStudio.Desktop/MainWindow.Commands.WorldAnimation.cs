using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private static StudioParameter WorldTimelineChanges => new("changes", "object", "Timeline presentation and the transport's playback options; the scenario is animation_scenario, source timing source_animation.", Properties:
    [
        P("view", "string", "Timeline projection.", false, "source", "run"),
        new("labelWidth", "number", "Preferred timeline label width in DIP; displayed width also fits the viewport.", NumberMinimum: 80, NumberMaximum: 720), P("followPlayhead", "boolean", "Scroll with live playback."),
        P("fit", "string", "Fit the timeline viewport.", false, "selection", "evaluated", "range"),
        new("visibleRange", "array", "Visible start/end seconds; independent of playback range.", Items: new("", "number", "Seconds.", NumberMinimum: 0, NumberMaximum: 3600), MinItems: 2, MaxItems: 2),
        new("range", "array", "Playback start/end seconds.", Items: new("", "number", "Seconds.", NumberMinimum: 0, NumberMaximum: 3600), MinItems: 2, MaxItems: 2),
        P("followCamera", "boolean", "Follow the evaluated camera; manual navigation disengages."), P("effects", "boolean", "Preview effects."),
        P("replay", "boolean", "Loop the playback range."), P("mute", "boolean", "Mute audio."),
        new("volume", "number", "Audio volume.", NumberMinimum: 0, NumberMaximum: 1),
        new("speed", "number", "Playback rate: 0.25, 0.5, 1, 2 or 4.", NumberMinimum: .25, NumberMaximum: 4)
    ]);

    private void RegisterWorldAnimationCommands(StudioCommands registry)
    {
        RegisterAnimationModeCommands(registry);
        RegisterJob(registry, "animation_timeline", "Inspect and operate Animation mode's Timing view (the opened binding's source and run rows on the shared clock). Structural paging and row selection never evaluate animation. The 40 fps cell action inspects source and optionally seeks a current occurrence; frame seeks a one-based editing position, timing proposes its existing event threshold draft. Source thresholds and Run observations are distinct. Keyframe samples are read only. animation_mode opens and exits; animation_scenario holds starts, call paths, condition switches, inputs and rules; animation_transport plays; animation_paths evaluates guides. Source timing edits use source_animation, verified drop moves use animation_paths without definition.",
            [PreviewParameter, DocumentParameter, RevisionParameter,
             P("action", "string", "Operation.", true, "read", "structure", "rows", "marks", "cell", "frame", "timing", "expand", "row", "options", "select", "evaluate_paths", "duration"),
             P("section", "string", "Structural page.", false, "entries", "sequences", "events", "calls", "keyframes", "ignored"),
             new("entry", "integer", "Exact compiled binding index for structure and select.", Minimum: 0),
             P("activation", "string", "Optional scheduled start GUID for structural calls and selection; otherwise paths are canonical and primary is the default."),
             P("sequence", "string", "Sequence GUID."), P("event", "string", "Event GUID."),
             P("path", "string", "Canonical callsite path from the structural plan; empty for root."),
             P("view", "string", "Rows projection.", false, "source", "run"),
             P("row", "string", "Timeline row identity from rows."), P("expanded", "boolean", "Expand or collapse a source row."),
             P("mark", "string", "Exact cell item identity from marks for cell inspection."),
             P("seek", "boolean", "For cell: also seek an available current execution occurrence; ambiguous occurrences are returned for selection."),
             new("frame", "number", "40 fps editing frame: frame 1 is time zero. frame seeks; timing proposes the row's existing event-start draft. Fractional frames preserve precise times."),
             new("occurrence", "integer", "Retained Run mark occurrence to inspect; does not seek.", Minimum: 0, Maximum: long.MaxValue),
             WorldTimelineChanges,
             new("offset", "integer", "Page offset.", Minimum: 0, Maximum: int.MaxValue),
             new("limit", "integer", "Page size, default 100.", Minimum: 1, Maximum: 100)], true, async (a, token) =>
        {
            RequirePreview(a);
            var document = TargetDocument(a);
            if (document != shownDocument || document.SourceWorld == null || document.Document is not SourceSceneSnapshot)
                throw new StudioCommandException("unsupported", "Open a source map first.");
            if (document.Revision != a["revision"]!.GetValue<long>())
                throw new StudioCommandException("revision_conflict", "Read the source map's current revision.");
            string action = Text(a, "action");
            if (action == "rows") return Result(WorldAnimationTimelineRows(a.ContainsKey("view") ? Text(a, "view") : WorldTimeline.ViewMode, Int(a, "offset"), Int(a, "limit", 100)));
            if (action == "marks") return Result(WorldAnimationTimelineMarks(Text(a, "row"), Int(a, "offset"), Int(a, "limit", 100)));
            if (action == "cell") return Result(await WorldAnimationCellAsync(Text(a, "row"), Text(a, "mark"), Flag(a, "seek"),
                a.ContainsKey("occurrence") ? a["occurrence"]!.GetValue<long>() : null, Int(a, "offset"), token));
            if (action is "frame" or "timing")
            {
                if (!a.ContainsKey("frame")) throw new StudioCommandException("invalid_argument", "Supply a frame number.");
                double frame = Number(a, "frame"), seconds = AnimationTimelineFrames.Seconds(frame);
                if (!double.IsFinite(frame) || !double.IsFinite(seconds)) throw new StudioCommandException("invalid_argument", "Use a finite frame number.");
                if (action == "timing") await ProposeWorldAnimationFrameAsync(Text(a, "row"), frame, token);
                else
                {
                    if (seconds is < 0 or > 3600) throw new StudioCommandException("invalid_argument", "Preview frames must be within 1–144001.");
                    await SeekWorldAnimationFrameAsync(seconds, token);
                }
                return Result(new { presentation = WorldTimeline.PresentationState, actualSeconds = worldAnimationSession?.State.Time, draft = SectionCard().AnimationDraft });
            }
            if (action == "structure")
            {
                var plan = GetWorldAnimationStructurePlan();
                int offset = Int(a, "offset"), limit = Int(a, "limit", 100);
                string section = Text(a, "section"), path = WorldAnimationScopedPath(a);
                int entry = a.ContainsKey("entry") ? Int(a, "entry") : a.ContainsKey("activation")
                    ? WorldAnimationActivations().Single(s => s.Id == GuidArg(a, "activation")).Entry : WorldAnimationTargetNow?.Entry ?? -1;
                if (section == "entries")
                {
                    List<AnimationStructureEntry> rows = [];
                    for (int i = offset; i < plan.EntryCount && rows.Count < limit; i++) { token.ThrowIfCancellationRequested(); rows.Add(plan.Entry(i)); }
                    return Result(new { plan = plan.Id, provenance = plan.ProvenanceId, rows, total = plan.EntryCount, offset, nextOffset = rows.Count < plan.EntryCount - (long)offset ? (int?)(offset + rows.Count) : null });
                }
                if (entry < 0) throw new StudioCommandException("invalid_argument", "Supply an exact entry binding.");
                object page = section switch
                {
                    "sequences" => plan.Sequences(entry, offset, limit, token),
                    "events" => plan.Events(entry, GuidArg(a, "sequence"), offset, limit, path, token),
                    "calls" => plan.Calls(entry, path, offset, limit, token),
                    "keyframes" => plan.Keyframes(entry, GuidArg(a, "sequence"), GuidArg(a, "event"), offset, limit, token),
                    "ignored" => plan.Ignored(entry, GuidArg(a, "sequence"), offset, limit, token),
                    _ => throw new StudioCommandException("invalid_argument", "Choose a structural section.")
                };
                return Result(new { plan = plan.Id, provenance = plan.ProvenanceId, page });
            }
            if (action is not ("read" or "options" or "expand")) RequireNoDrafts(document);
            switch (action)
            {
                case "expand":
                    if (!a.ContainsKey("expanded")) throw new StudioCommandException("invalid_argument", "Supply expanded.");
                    WorldAnimationExpandRow(Text(a, "row"), Flag(a, "expanded")); break;
                case "row": await WorldAnimationSelectRowAsync(Text(a, "row"), a.ContainsKey("occurrence") ? a["occurrence"]!.GetValue<long>() : null); break;
                case "options":
                    var changes = a["changes"] as JsonObject ?? throw new StudioCommandException("invalid_argument", "Supply changes.");
                    foreach (var (name, value) in changes) await WorldAnimationOptionAsync(name, value ?? throw new StudioCommandException("invalid_argument", "Supply explicit option values."), token);
                    break;
                case "select": await WorldAnimationSelectSourceAsync(GuidArg(a, "sequence"), GuidArg(a, "event"), token, a.ContainsKey("entry") ? Int(a, "entry") : a.ContainsKey("activation") ? WorldAnimationActivations().Single(s => s.Id == GuidArg(a, "activation")).Entry : null, WorldAnimationScopedPath(a), preserveTimeline: true); break;
                case "evaluate_paths": await WorldAnimationTransportAsync("evaluatePaths", 0, token); break;
                case "duration": await WorldAnimationTransportAsync("duration", 0, token); break;
                case "read": break;
                default: throw new StudioCommandException("invalid_argument", "Unknown timeline operation.");
            }
            return Result(new { animation = WorldAnimationCommandState, presentation = WorldTimeline.PresentationState });
        });
    }

    private void RegisterAnimationModeCommands(StudioCommands registry)
    {
        RegisterJob(registry, "animation_mode", "Animation mode of the shown source map (the GUI's Open ▷ on a World tree definition row, Enter, a double-click, an object card's animation links, the Animation menu; Exit animation and Shift+Esc). open enters it on a definition (definition from scene_tree or source_world_definitions) or an exact binding (entry), or switches the opened one (the previous one goes to back); the opened binding becomes the scenario's primary start (in place when it is already a start; the previous primary stays a start). Entering never moves the camera, plays, evaluates paths or measures a duration; drafts must be resolved first. exit returns to the map layout: the run stops, the scenario is kept (state.keptScenario) and the definition stays selected with its card. back and forward walk the opened definitions; binding plays another binding of the opened definition. layout sets the panel's view (structure, timing, split; split needs 1,300 DIP), its height, the right pane's tab and whether the World tree (remembered for the profile) and the Diagnostics strip (remembered for the project) show. state reads all of it. Each open, binding and exit replaces the preview ID.",
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
             P("diagnostics", "boolean", "layout: show the Diagnostics strip under the panel.")], false, async (a, token) =>
        {
            RequirePreview(a);
            string action = Text(a, "action");
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
                    if (a.ContainsKey("view")) animationView = Text(a, "view");
                    if (a.ContainsKey("panelHeight"))
                    {
                        double height = Number(a, "panelHeight");
                        if (!double.IsFinite(height) || height < AnimationPanel.MinimumPanelHeight) throw new StudioCommandException("invalid_argument", "The panel is at least 240 DIP high.");
                        animationPanelHeight = height; RememberAnimationLayout();
                    }
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
        RegisterJob(registry, "animation_scenario", "The shown map's animation scenario (the Scenario tab and the command bar's scenario chip): starts on the shared clock (at most 32; the primary, id 00000000-0000-0000-0000-000000000000, is Animation mode's opened binding), included call paths, condition switches (IF and ELSEIF, per call path), inputs (player at start, custom or none; random seed; effects detail; phase; height; beam origin) and rules (prerequisites check, ignoreStarts (default) or ignore; range gates game or ignore; zone gates ignore (default) or view). Every change stops playback and re-evaluates the run up to the playhead, as a seek does, before it returns; it is saved with the map in the project's .zstudio/settings.json and survives Exit, other definitions, difficulty changes and source edits. read lists it with the call paths the starts reach, grouped by calling and called definition (Include all leaves back-links excluded). include includes paths with the calls that lead to them, or excludes them with every path below them. pick places the player or the beam origin on the surface under a point of the 3D view (DIP, as Pick in view does). reset returns everything to the defaults with the opened binding as primary. fitRange sets the playback range to the latest start plus 30 s. starts, include, includeAll, excludeAll, conditions, pick and fitRange need Animation mode.",
            [PreviewParameter,
             P("action", "string", "Operation.", true, "read", "starts", "include", "includeAll", "excludeAll", "conditions", "inputs", "pick", "rules", "reset", "fitRange"),
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
             P("target", "string", "pick: what to place.", false, "player", "beamOrigin"),
             new("screenPoint", "array", "pick: a point of the 3D view in DIP from its top left.", Items: new("", "number", "DIP."), MinItems: 2, MaxItems: 2),
             P("prerequisites", "string", "rules: check, ignoreStarts or ignore.", false, "check", "ignoreStarts", "ignore"),
             P("rangeGates", "string", "rules: game or ignore.", false, "game", "ignore"),
             P("zoneGates", "string", "rules: ignore or view (the view's zones).", false, "ignore", "view")], false, async (a, token) =>
        {
            RequirePreview(a); RequireScenarioMap();
            string action = Text(a, "action");
            object? result = null;
            if (action != "read") RequireNoDrafts();
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
                case "includeAll": result = ScenarioIncludeAll(token); break;
                case "excludeAll": ScenarioExcludeAll(); break;
                case "conditions":
                    foreach (var item in (a["conditions"] as JsonArray ?? throw new StudioCommandException("invalid_argument", "Supply the conditions.")).OfType<JsonObject>())
                    {
                        if (!Guid.TryParse(Text(item, "event"), out Guid ev)) throw new StudioCommandException("invalid_argument", "Use an event GUID.");
                        var choice = Text(item, "value") switch { "true" => AnimationConditionChoice.True, "false" => AnimationConditionChoice.False, "game" => AnimationConditionChoice.Game, _ => throw new StudioCommandException("invalid_argument", "A switch is game, true or false.") };
                        ScenarioCondition(Text(item, "instancePath"), Int(item, "entry"), ev, choice);
                    }
                    break;
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
                case "reset": ScenarioReset(); break;
                case "fitRange": RequireOpenedBinding(); await FitWorldAnimationScenario(); break;
                default: throw new StudioCommandException("invalid_argument", "Unknown scenario operation.");
            }
            // The change shows at once: the run is re-evaluated up to the playhead before the result returns.
            await worldAnimationReevaluation;
            return Result(new { scenario = ScenarioReadState(), result, animation = InAnimationMode ? WorldAnimationCommandState : null });
        });
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
        object? kept = !InAnimationMode && animationKept is { } previous && previous.Document.SourceWorld != null && previous.Document.SourceWorld == shownDocument?.SourceWorld
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
            },
        };
    }
}
