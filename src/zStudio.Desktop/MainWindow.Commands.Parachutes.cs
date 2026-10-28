using System.Globalization;
using zStudio.Automation;
using zStudio.Core;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private object ParachuteState() => parachutes is not { } s ? new { visible = false } : new
    {
        visible = true, snapshot = s.Id, world = s.World.SessionId, revision = s.World.Revision,
        owner = (s.Owner ?? s.World).SessionId, ownerPath = s.Owner?.Path ?? s.World.SourceWorld?.DefinitionsPath, dirty = s.World.IsDirty,
        source = s.Owner == null, selected = s.Selected, entry = s.Entry, analysisRefusal = s.AnalysisRefusal,
        scenario = s.Scenario, run = s.Run, purpose = "verifiedDropWindows", baseline = s.SceneGeneration, stale = s.Stale, staleReason = s.Stale ? s.StaleReason : null,
        player = s.Policy?.Rules.Player is { } player ? new { kind = player.Kind.ToString(), position = player.Kind == zStudio.Core.Animation.AnimationPlayerReferenceKind.Custom ? new[] { player.Position.X, player.Position.Y, player.Position.Z } : null } : null,
        cycleSeconds = s.Analysis?.CycleSeconds, rootNode = s.Analysis?.RootNode,
        candidates = s.Discovery.Candidates.Select(c => new { entry = c.EntryIndex, name = c.Name, root = c.RootName, windows = c.Windows.Count }).ToArray(),
        diagnostics = s.Discovery.Diagnostics.Take(16).Select(d => Bounded(d, 512)).ToArray(), diagnosticCount = s.Discovery.Diagnostics.Count,
        windows = s.Analysis?.Windows.Select(w => new { id = w.Id, start = w.Start, duration = w.Duration, effectiveStart = w.EffectiveStart, effectiveEnd = w.EffectiveEnd, minimumStart = w.MinimumStart, maximumStart = w.MaximumStart, conditional = w.Conditional, moveRefusal = w.MoveRefusal }).ToArray(),
        sampleCount = s.Analysis?.Samples.Count ?? 0, releaseCount = s.Analysis?.Releases.Count ?? 0,
        draft = parachuteDraft is not { } d ? null : new { token = d.Token, input = d.Input, start = d.Plan?.NewStart, error = d.Error, ready = d.Plan != null && d.Preview != null && d.Error == null, window = d.Window.Id }
    };
    private void RegisterParachuteCommands(StudioCommands registry)
    {
        RegisterJob(registry, "animation_paths", "With definition, analyze explicitly evaluates bounded source animation paths for an exact binding using the current finite child-inclusion policy; show/hide only changes compatible cached guides and never evaluates; clear discards the selected definition's cached paths. Frame its guides, preview/exit the shared world transport, state reads snapshots, samples pages points and markers pages dispatch traces. Selection and expansion are passive. preview_state.animationPaths reports status, diagnostics and per-path Kind (object or camera). Source paths use standable map terrain and stop procedural object motion at world Y -500 or 500 without impact cues; keyframes and cameras are unrestricted. Camera guides are orange, objects teal. With no definition, inspect and relocate complete existing VTOL drop windows in a Whole world. Show analyzes the first discovered flight; unavailable flights retain the candidate list, entry and analysisRefusal without a partial path. Analyze selects an exact entry. The verified-drop analysis admits only recognized flight-to-callback-911 callsites and does not change the main preview's inclusion or playback; the callback's conditions follow the map's animation scenario (its player, condition switches and rules), and a change to them makes the shown analysis stale (stale true; analyze and moves refused) until show runs again. Conditional release markers come from a frozen bounded runtime, never landing predictions; a release gated by the player's range appears only when the scenario's player is in range, so set it near the flight with animation_options activationTarget (on a compiled Whole world with no animation selected too). A source map's windows can be moved: select/begin/set/confirm/cancel share the GUI draft and one undo step, set takes a local start time or an exact path segment plus fraction to disambiguate crossings, and the edit uses token-preserving project history and in-memory scene evaluation (Apply creates no archives; Save writes the owning animation definitions; undo/redo/save target the project). A compiled world is read only: show, analyze, samples and releases work, its windows cannot be selected or moved. Samples/releases support offset/limit pagination without query; preview_state.parachute reports bounded state. No physical input is captured.",
            [PreviewParameter, DocumentParameter, RevisionParameter,
                P("action", "string", "Operation. With definition: source animation guides and preview; without definition: existing drop windows.", true, "show", "hide", "analyze", "select", "begin", "set", "confirm", "cancel", "undo", "redo", "save", "samples", "releases", "state", "frame", "preview", "exit", "markers", "clear"),
                new("definition", "integer", "Source definition index for generic motion paths.", Minimum: 0),
                new("seconds", "number", "Optional analyzed preview range (seconds).", NumberMinimum: 0.0166666666666667, NumberMaximum: 3600),
                P("snapshot", "string", "Current Parachute snapshot, required except show."),
                new("entry", "integer", "Exact discovered flight entry index for analyze.", Minimum: 0, Maximum: short.MaxValue),
                P("window", "string", "Current drop-window UUID for select."), P("token", "string", "Current draft token for set/confirm/cancel."),
                new("start", "number", "Local cycle start seconds; mutually exclusive with segment/fraction.", NumberMinimum: 0, NumberMaximum: 3600),
                new("segment", "integer", "Path sample index, followed by index+1; requires fraction.", Minimum: 0, Maximum: 216000),
                new("fraction", "number", "Fraction along the identified segment; requires segment.", NumberMinimum: 0, NumberMaximum: 1),
                ..PageParameters.Where(p => p.Name != "query")], true, async (a, token) =>
        {
            RequirePreview(a); var world = TargetDocument(a);
            if (world != shownDocument || a["revision"]!.GetValue<long>() != world.Revision)
                throw new StudioCommandException("revision_conflict", "Read the displayed world's current document and revision.");
            string action = Text(a, "action");
            if (a.ContainsKey("definition"))
                return await SourceAnimationPathCommandAsync(a, world, action, token);
            if (action == "show")
            {
                if (retiringScene != null) throw new StudioCommandException("not_ready", "The view is reloading after a source edit; read zstudio_state again shortly.");
                RequireNoDrafts(); await ShowParachutesAsync(token); return Result(new { parachute = ParachuteState() });
            }
            if (!a.ContainsKey("snapshot")) throw new StudioCommandException("invalid_argument", "Supply the current Parachute snapshot.");
            var state = RequireParachutes(Text(a, "snapshot"));
            if (action is "samples" or "releases")
            {
                var analysis = parachuteDraft?.DisplayedPreview ?? state.Analysis;
                return action == "samples"
                    ? Page(analysis?.Samples.Select((p, i) => new { segment = i, time = p.Time, position = JsonData.Vector(p.Position) }) ?? [], a)
                    : Page(analysis?.Releases.Select(p => new { window = p.WindowId, time = p.Time, position = JsonData.Vector(p.Position), conditional = p.Conditional }) ?? [], a);
            }
            if (action is not ("set" or "confirm" or "cancel")) RequireNoDrafts();
            DocumentModel? changed = null;
            if (action == "hide") HideParachutes();
            else if (action == "analyze")
            {
                if (!a.ContainsKey("entry")) throw new StudioCommandException("invalid_argument", "Supply the flight entry index.");
                await AnalyzeParachuteAsync(Int(a, "entry"), token);
            }
            else if (action == "select") await SelectParachuteAsync(GuidArg(a, "window"));
            else if (action == "begin") { await parachuteSelectionWork; BeginParachute(); }
            else if (action is "undo" or "redo") await ParachuteHistoryAsync(action == "redo", token);
            else if (action == "save")
            {
                changed = ParachuteSaveWorld(Text(a, "snapshot"));
                await SaveSourceWorldAsync(state.World, token); CommitRunningJob();
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
                    float start = time ? (float)Number(a, "start") : zStudio.Core.Animation.VtolDropWindows.PlanMove(state.Analysis!, draft.Window.Id, Int(a, "segment"), (float)Number(a, "fraction")).NewStart;
                    draft.Input = start.ToString("G9", CultureInfo.InvariantCulture); state.Viewport.SetParachutePointerMove(false);
                    QueueParachutePreview(draft, token: token); UpdateParachutePanel(); await draft.Work.WaitAsync(token); token.ThrowIfCancellationRequested();
                    RequireParachuteDraft(draft.Token);
                }
            }
            return Result(new { parachute = ParachuteState(), document = changed == null ? null : DocumentState(changed) });
        });
    }
}
