using System.Globalization;
using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private object ParachuteState() => parachutes is not { } s ? new { visible = false } : new
    {
        visible = true, snapshot = s.Id, world = s.World.SessionId, revision = s.World.Revision,
        owner = (s.Owner ?? s.World).SessionId, ownerRevision = (s.Owner ?? s.World).Revision,
        ownerPath = s.Owner?.Path ?? s.World.SourceWorld?.DefinitionsPath, dirty = (s.Owner ?? s.World).IsDirty,
        source = s.Owner == null, selected = s.Selected, entry = s.Entry, analysisRefusal = s.AnalysisRefusal,
        cycleSeconds = s.Analysis?.CycleSeconds, rootNode = s.Analysis?.RootNode,
        candidates = s.Discovery.Candidates.Select(c => new { entry = c.EntryIndex, name = c.Name, root = c.RootName, windows = c.Windows.Count }).ToArray(),
        diagnostics = s.Discovery.Diagnostics.Take(16).Select(d => Bounded(d, 512)).ToArray(), diagnosticCount = s.Discovery.Diagnostics.Count,
        windows = s.Analysis?.Windows.Select(w => new { id = w.Id, start = w.Start, duration = w.Duration, effectiveStart = w.EffectiveStart, effectiveEnd = w.EffectiveEnd, minimumStart = w.MinimumStart, maximumStart = w.MaximumStart, conditional = w.Conditional, moveRefusal = w.MoveRefusal }).ToArray(),
        sampleCount = s.Analysis?.Samples.Count ?? 0, releaseCount = s.Analysis?.Releases.Count ?? 0,
        draft = parachuteDraft is not { } d ? null : new { token = d.Token, input = d.Input, start = d.Plan?.NewStart, error = d.Error, ready = d.Plan != null && d.Preview != null && d.Error == null, window = d.Window.Id }
    };
    private void RegisterParachuteCommands(StudioCommands registry)
    {
        RegisterJob(registry, "parachute", "Inspect and relocate complete existing VTOL drop windows in a Whole world. Show analyzes the first discovered flight; unavailable flights retain the candidate list, entry and analysisRefusal without a partial path. Analyze selects an exact entry. Conditional release markers come from a frozen bounded runtime, never landing predictions. Select/begin/set/confirm/cancel share the GUI draft and one undo step. Set a local start time or exact path segment plus fraction to disambiguate crossings. Compiled edits belong to the returned animation owner; source edits use token-preserving project history and rebuild. Undo/redo/save explicitly target that owner. save requires a NEW destination for compiled animations; source saves retain their project destination. Samples/releases support offset/limit pagination without query; preview_state.parachute reports bounded state. No physical input is captured.",
            [PreviewParameter, DocumentParameter, RevisionParameter,
                P("action", "string", "Operation.", true, "show", "hide", "analyze", "select", "begin", "set", "confirm", "cancel", "undo", "redo", "save", "samples", "releases"),
                P("snapshot", "string", "Current Parachute snapshot, required except show."),
                new("ownerRevision", "integer", "Current animation owner revision for compiled begin/set/confirm/undo/redo/save.", Minimum: 0),
                new("entry", "integer", "Exact discovered flight entry index for analyze.", Minimum: 0, Maximum: short.MaxValue),
                P("window", "string", "Current drop-window UUID for select."), P("token", "string", "Current draft token for set/confirm/cancel."),
                new("start", "number", "Local cycle start seconds; mutually exclusive with segment/fraction.", NumberMinimum: 0, NumberMaximum: 3600),
                new("segment", "integer", "Path sample index, followed by index+1; requires fraction.", Minimum: 0, Maximum: 216000),
                new("fraction", "number", "Fraction along the identified segment; requires segment.", NumberMinimum: 0, NumberMaximum: 1),
                P("destination", "string", "New animation Save As file outside the source root, only for compiled save."), ..PageParameters.Where(p => p.Name != "query")], true, async (a, token) =>
        {
            RequirePreview(a); var world = TargetDocument(a);
            if (world != shownDocument || a["revision"]!.GetValue<long>() != world.Revision)
                throw new StudioCommandException("revision_conflict", "Read the displayed world's current document and revision.");
            string action = Text(a, "action");
            if (action == "show") { RequireNoDrafts(); await ShowParachutesAsync(token); return Result(new { parachute = ParachuteState() }); }
            if (!a.ContainsKey("snapshot")) throw new StudioCommandException("invalid_argument", "Supply the current Parachute snapshot.");
            var state = RequireParachutes(Text(a, "snapshot"));
            if (state.Owner != null && action is "begin" or "set" or "confirm" or "undo" or "redo" or "save")
                if (a["ownerRevision"]?.GetValue<long>() != state.Owner.Revision) throw new StudioCommandException("revision_conflict", "Supply the current animation ownerRevision.");
            if (action is "samples" or "releases")
            {
                var analysis = parachuteDraft?.DisplayedPreview ?? state.Analysis;
                return action == "samples"
                    ? Page(analysis?.Samples.Select((p, i) => new { segment = i, time = p.Time, position = JsonData.Vector(p.Position) }) ?? [], a)
                    : Page(analysis?.Releases.Select(p => new { window = p.WindowId, time = p.Time, position = JsonData.Vector(p.Position), conditional = p.Conditional }) ?? [], a);
            }
            if (action is not ("set" or "confirm" or "cancel")) RequireNoDrafts();
            DocumentModel? changed = null;
            if (action == "hide") CloseParachutes();
            else if (action == "analyze")
            {
                if (!a.ContainsKey("entry")) throw new StudioCommandException("invalid_argument", "Supply the flight entry index.");
                await AnalyzeParachuteAsync(Int(a, "entry"), token);
            }
            else if (action == "select") SelectParachute(GuidArg(a, "window"));
            else if (action == "begin") BeginParachute();
            else if (action is "undo" or "redo") await ParachuteHistoryAsync(action == "redo", token);
            else if (action == "save")
            {
                changed = state.Owner ?? state.World;
                if (state.Owner == null)
                {
                    if (a.ContainsKey("destination")) throw new StudioCommandException("invalid_argument", "Source worlds save their source project; omit destination.");
                    await SaveSourceWorldAsync(state.World, token); CommitRunningJob();
                }
                else
                {
                    string destination = FullPath(a, "destination");
                    if (destination.Length == 0) throw new StudioCommandException("invalid_argument", "Supply a new animation-copy destination.");
                    await SaveAnimationToPathAsync(state.Owner, destination, token); CommitRunningJob();
                }
            }
            else
            {
                var draft = RequireParachuteDraft(Text(a, "token"));
                if (action == "cancel") CancelParachuteDraft();
                else if (action == "confirm") changed = await ConfirmParachuteAsync(token);
                else
                {
                    RequireParachuteInput(draft);
                    bool time = a.ContainsKey("start"), segment = a.ContainsKey("segment"), fraction = a.ContainsKey("fraction");
                    if (time == segment || segment != fraction) throw new StudioCommandException("invalid_argument", "Supply start, or segment and fraction together.");
                    float start = time ? (float)Number(a, "start") : Recoil.Zbd.Core.Animation.VtolDropWindows.PlanMove(state.Analysis!, draft.Window.Id, Int(a, "segment"), (float)Number(a, "fraction")).NewStart;
                    draft.Input = start.ToString("G9", CultureInfo.InvariantCulture); state.Viewport.SetParachutePointerMove(false);
                    QueueParachutePreview(draft, token: token); UpdateParachutePanel(); await draft.Work.WaitAsync(token); token.ThrowIfCancellationRequested();
                    RequireParachuteDraft(draft.Token);
                }
            }
            return Result(new { parachute = ParachuteState(), document = changed == null ? null : DocumentState(changed) });
        });
    }
}
