using System.Text.Json.Nodes;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private static StudioParameter WorldTimelineChanges => new("changes", "object", "Preview scenario and timeline presentation; source timing is edited through source_animation.", Properties:
    [
        P("phase", "string", "Runtime or cleanup; selecting a cleanup row never changes phase.", false, "runtime", "cleanup"),
        new("seed", "integer", "Deterministic seed.", Minimum: int.MinValue, Maximum: int.MaxValue),
        new("height", "number", "Preview-only world height.", NumberMinimum: -999, NumberMaximum: 999),
        new("effectLevel", "integer", "Effect detail level.", Minimum: 0, Maximum: 2),
        P("referencePosition", "string", "Reference XYZ as comma-separated numbers, or blank for default."),
        P("activationStart", "string", "Activation start XYZ as comma-separated numbers, or blank for default."),
        new("includedCalls", "array", "Finite canonical call paths; ancestors must be included. Replaces the policy.", Items: new("", "string", "Call path."), MaxItems: 4096),
        new("conditions", "array", "Scoped condition overrides; replaces the override list.", Items: new("", "object", "Override.", Properties:
        [P("instancePath", "string", "Canonical instance lineage, empty for root.", true), new("entry", "integer", "Entry index.", true, Minimum: 0),
         P("event", "string", "Event GUID.", true), P("value", "boolean", "Condition value.", true)]), MaxItems: 4096),
        P("view", "string", "Timeline projection.", false, "source", "run"),
        new("labelWidth", "number", "Preferred timeline label width in DIP; displayed width also fits the viewport.", NumberMinimum: 80, NumberMaximum: 720), P("followPlayhead", "boolean", "Scroll with live playback."),
        P("fit", "string", "Fit the timeline viewport.", false, "selection", "evaluated", "range"),
        new("visibleRange", "array", "Visible start/end seconds; independent of playback range.", Items: new("", "number", "Seconds.", NumberMinimum: 0, NumberMaximum: 3600), MinItems: 2, MaxItems: 2),
        new("range", "array", "Playback start/end seconds.", Items: new("", "number", "Seconds.", NumberMinimum: 0, NumberMaximum: 3600), MinItems: 2, MaxItems: 2),
        P("followCamera", "boolean", "Follow the evaluated camera; manual navigation disengages."), P("effects", "boolean", "Preview effects."),
        P("replay", "boolean", "Replay the playback range."), P("mute", "boolean", "Mute audio."),
        new("volume", "number", "Audio volume.", NumberMinimum: 0, NumberMaximum: 1),
        new("speed", "number", "Playback rate: 0.25, 0.5, 1, 2 or 4.", NumberMinimum: .25, NumberMaximum: 4)
    ]);

    private void RegisterWorldAnimationCommands(StudioCommands registry)
    {
        RegisterJob(registry, "animation_timeline", "Inspect and operate the source world's integrated animation timeline. Structural paging and row selection never evaluate animation. The 40 fps cell action inspects source and optionally seeks a current occurrence; frame seeks a one-based editing position, timing proposes its existing event threshold draft. First Play/Seek pins the selected exact binding; use_selection stops and pins without playing. Calls are excluded by default and enabled per finite callsite path. Source thresholds and Run observations are distinct. Keyframe samples are read only. Scenario changes stop and detach the run. Existing animation_transport controls playback; animation_paths explicitly evaluates guides independently. Source timing edits use source_animation, verified drop moves use animation_paths without definition.",
            [PreviewParameter, DocumentParameter, RevisionParameter,
             P("action", "string", "Operation.", true, "read", "structure", "rows", "marks", "cell", "frame", "timing", "expand", "row", "target", "include", "options", "select", "use_selection", "exit", "dock", "evaluate_paths", "duration", "scenario"),
             P("section", "string", "Structural page.", false, "entries", "sequences", "events", "calls", "keyframes", "ignored"),
             new("definition", "integer", "Source definition index for target.", Minimum: 0),
             new("entry", "integer", "Exact compiled binding index; required when the definition expands to several bindings.", Minimum: 0),
             new("root", "integer", "Root binding for a call inclusion change.", Minimum: 0),
             P("sequence", "string", "Sequence GUID."), P("event", "string", "Event GUID."),
             P("path", "string", "Canonical callsite path from the structural plan; empty for root."),
             P("included", "boolean", "Include this call path."),
             P("view", "string", "Rows projection.", false, "source", "run"),
             P("row", "string", "Timeline row identity from rows."), P("expanded", "boolean", "Expand or collapse a source row."),
             P("mark", "string", "Exact cell item identity from marks for cell inspection."),
             P("seek", "boolean", "For cell: also seek an available current execution occurrence; ambiguous occurrences are returned for selection."),
             new("frame", "number", "40 fps editing frame: frame 1 is time zero. frame seeks; timing proposes the row's existing event-start draft. Fractional frames preserve precise times."),
             new("occurrence", "integer", "Retained Run mark occurrence to inspect; does not seek.", Minimum: 0, Maximum: long.MaxValue),
             P("dock", "string", "Bottom dock view.", false, "timeline", "diagnostics", "collapsed"),
             WorldTimelineChanges,
             new("offset", "integer", "Page offset.", Minimum: 0, Maximum: int.MaxValue),
             new("limit", "integer", "Page size, default 100.", Minimum: 1, Maximum: 100)], true, async (a, token) =>
        {
            RequirePreview(a);
            var document = TargetDocument(a);
            if (document != shownDocument || document.SourceWorld == null || document.Document is not SourceSceneSnapshot source)
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
                int entry = a.ContainsKey("entry") ? Int(a, "entry") : WorldAnimationTargetNow?.Entry ?? -1;
                string section = Text(a, "section"), path = Text(a, "path");
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
            if (action is not ("read" or "exit" or "dock" or "options" or "expand")) RequireNoDrafts(document);
            switch (action)
            {
                case "expand":
                    if (!a.ContainsKey("expanded")) throw new StudioCommandException("invalid_argument", "Supply expanded.");
                    WorldAnimationExpandRow(Text(a, "row"), Flag(a, "expanded")); break;
                case "row": await WorldAnimationSelectRowAsync(Text(a, "row"), a.ContainsKey("occurrence") ? a["occurrence"]!.GetValue<long>() : null); break;
                case "target":
                    if (!a.ContainsKey("definition")) throw new StudioCommandException("invalid_argument", "Supply a source definition.");
                    int definition = Int(a, "definition");
                    var selected = source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == definition)
                        ?? throw new StudioCommandException("stale_record", "The definition is unavailable.");
                    int binding = a.ContainsKey("entry") ? Int(a, "entry") : selected.Entries.Count == 1 ? selected.Entries[0] : -1;
                    if (binding < 0 || !selected.Entries.Contains(binding)) throw new StudioCommandException("invalid_argument", "Choose an exact binding of this definition.");
                    await EnsureAnimationCardForEntryAsync(document, binding, token);
                    SetWorldAnimationCandidate(document, definition, binding);
                    break;
                case "include":
                    if (!a.ContainsKey("included")) throw new StudioCommandException("invalid_argument", "Supply included.");
                    int root = a.ContainsKey("root") ? Int(a, "root") : WorldAnimationTargetNow?.Entry ?? -1;
                    if (root < 0) throw new StudioCommandException("invalid_argument", "Choose a root binding first.");
                    SetWorldAnimationCallIncluded(root, Text(a, "path"), Flag(a, "included"));
                    break;
                case "options":
                    var changes = a["changes"] as JsonObject ?? throw new StudioCommandException("invalid_argument", "Supply changes.");
                    foreach (var (name, value) in changes) await WorldAnimationOptionAsync(name, value ?? throw new StudioCommandException("invalid_argument", "Supply explicit option values."), token);
                    break;
                case "select": await WorldAnimationSelectSourceAsync(GuidArg(a, "sequence"), GuidArg(a, "event"), token, a.ContainsKey("entry") ? Int(a, "entry") : null, Text(a, "path")); break;
                case "use_selection": await WorldAnimationTransportAsync("useSelection", 0, token); break;
                case "exit": await WorldAnimationTransportAsync("exit", 0, token); break;
                case "evaluate_paths": await WorldAnimationTransportAsync("evaluatePaths", 0, token); break;
                case "duration": await WorldAnimationTransportAsync("duration", 0, token); break;
                case "scenario": await WorldAnimationTransportAsync("scenario", 0, token); break;
                case "dock":
                    string dock = Text(a, "dock");
                    if (dock == "collapsed") SetToolsVisible(false);
                    else { SetWorldAnimationDockView(dock); SetToolsVisible(true); }
                    break;
                case "read": break;
                default: throw new StudioCommandException("invalid_argument", "Unknown timeline operation.");
            }
            return Result(new { animation = WorldAnimationCommandState, presentation = WorldTimeline.PresentationState, dock = WorldAnimationDockView, expanded = SourceToolsVisible });
        });
    }
}
