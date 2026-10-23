using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private sealed class ParachuteSession(DocumentModel world, DocumentModel? owner, SceneViewport viewport,
        AnimationPreviewContext context, VtolDropDiscovery discovery)
    {
        public readonly DocumentModel World = world;
        public readonly DocumentModel? Owner = owner;
        public readonly SceneViewport Viewport = viewport;
        public readonly AnimationPreviewContext Context = context;
        public readonly VtolDropDiscovery Discovery = discovery;
        public long WorldRevision = world.Revision, OwnerRevision = owner?.Revision ?? world.Revision;
        public readonly string Id = Guid.NewGuid().ToString();
        public VtolDropSnapshot? Analysis;
        public int? Entry;
        public string? AnalysisRefusal;
        public Guid? Selected;
    }
    private sealed class ParachuteDraft(ParachuteSession session, VtolDropWindow window)
    {
        public readonly ParachuteSession Session = session;
        public readonly VtolDropWindow Window = window;
        public readonly string Token = Guid.NewGuid().ToString();
        public string Input = window.Start.ToString("G9", CultureInfo.InvariantCulture);
        public string? Error;
        public VtolDropMovePlan? Plan;
        public VtolDropSnapshot? Preview;
        // Incomplete/rejected input invalidates acceptance but retains the last renderer publication.
        public VtolDropSnapshot? DisplayedPreview;
        public CancellationTokenSource? Preparing;
        public Task Work = Task.CompletedTask;
        public int Generation;
        public bool Accepting;
        public AiPathDraftLifetime Lifetime = AiPathDraftLifetime.Pending;
        public long? RolledBackRevision;
    }
    private ParachuteSession? parachutes;
    private ParachuteDraft? parachuteDraft;
    private CancellationTokenSource? parachuteLoading;
    private long parachuteGeneration;
    private const string ParachuteAnalysisStatus = "Analyzing VTOL drop windows…";
    private bool synchronizingParachutes, acceptingParachute;
    internal Action<CancellationToken>? ParachuteAnalysisPreparing { get; set; }
    private sealed record ParachuteChoice(int Entry, string Label) { public override string ToString() => Label; }
    private sealed record ParachuteWindowChoice(Guid Id, string Label) { public override string ToString() => Label; }

    private void ConfigureParachuteScene(SceneViewport viewport)
    {
        viewport.ParachuteSelectionChanging = () => parachuteLoading == null && parachuteDraft == null && ResolvePropertiesDrafts();
        viewport.ParachuteWindowSelected += id =>
        { if (parachutes is { } state && state.Viewport == viewport) { state.Selected = id; UpdateParachutePanel(); } };
        viewport.ParachuteMoveRequested += (segment, fraction) =>
        {
            if (parachuteDraft is not { Accepting: false } draft || draft.Session.Viewport != viewport) return;
            try
            {
                var plan = VtolDropWindows.PlanMove(draft.Session.Analysis!, draft.Window.Id, segment, (float)fraction);
                draft.Input = plan.NewStart.ToString("G9", CultureInfo.InvariantCulture);
                QueueParachutePreview(draft, debounce: true); UpdateParachutePanel();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
            { draft.Error = ex.Message; UpdateParachutePanel(); }
        };
        viewport.ParachuteConfirmRequested += () => _ = ReportUi(ConfirmParachuteFromGuiAsync);
        viewport.ParachuteCancelRequested += CancelParachuteDraft;
    }

    private void CloseParachutes()
    {
        ++parachuteGeneration; parachuteLoading?.Cancel();
        CancelParachuteDraft();
        if (parachutes is { } state)
        {
            state.World.PropertyChanged -= ParachuteDocumentChanged; state.World.Disposing -= CloseParachutes;
            if (state.Owner != null) { state.Owner.PropertyChanged -= ParachuteDocumentChanged; state.Owner.Disposing -= CloseParachutes; }
            state.Viewport.SetParachuteOverlay([], []);
        }
        parachutes = null; UpdateParachutePanel();
    }
    private void ParachuteDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (acceptingParachute || parachutes is not { } state) return;
        if (state.World.Revision == state.WorldRevision && (state.Owner?.Revision ?? state.World.Revision) == state.OwnerRevision) return;
        CloseParachutes(); ViewModel.Status = "The Parachute owner changed. Open Parachute again to analyze its current animation.";
    }
    /// <summary>The route editor's world for Save: a source world, saved as the project is (save_document), whatever gesture runs.</summary>
    private DocumentModel ParachuteSaveWorld(string? snapshot = null)
    {
        var world = RequireParachutes(snapshot).World;
        return world.SourceWorld != null ? world : throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
    }
    private ParachuteSession RequireParachutes(string? snapshot = null, bool edit = false)
    {
        var state = parachutes ?? throw new StudioCommandException("not_ready", "Open Parachute in a Whole world first.");
        if (snapshot != null && snapshot != state.Id) throw new StudioCommandException("stale_snapshot", "Read the current Parachute snapshot.");
        if (!IsAiWorld || shownDocument != state.World || scene != state.Viewport || state.World.IsDisposed || state.Owner?.IsDisposed == true ||
            state.World.Revision != state.WorldRevision || (state.Owner?.Revision ?? state.World.Revision) != state.OwnerRevision)
            throw new StudioCommandException("context_changed", "The world or animation changed; close and reopen the route editor.");
        if (edit)
        {
            if (state.World.SourceWorld == null) throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
            if (parachuteLoading != null || sourceWorkspaceBusy || state.World.SourceWorld?.IsRebuilding == true || state.Viewport.IsFlyActive || state.Viewport.IsPickupDragging ||
                state.Viewport.AiPathEditingNetwork != null || state.Viewport.TerrainBrushActive || state.Viewport.ZonePaintActive)
                throw new StudioCommandException("busy", "Finish the current editing or navigation operation first.");
        }
        return state;
    }
    private async Task ShowParachutesAsync(CancellationToken token, int? entry = null, Action<long>? started = null)
    {
        RequireNoDrafts();
        if (!IsAiWorld || shownDocument is not { IsDisposed: false } world || ViewModel.Resolver is not { } resolver)
            throw new StudioCommandException("unsupported", "Open a Whole world to inspect VTOL drop windows.");
        CloseParachutes();
        var viewport = scene!; long request = ++parachuteGeneration, revision = world.Revision;
        started?.Invoke(request);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, world.Lifetime.Token, ViewModel.WorkspaceToken, shutdownToken);
        parachuteLoading = cancellation;
        ViewModel.Status = ParachuteAnalysisStatus;
        void Current()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (request != parachuteGeneration || shownDocument != world || scene != viewport || world.Revision != revision)
                throw new StudioCommandException("context_changed", "The world changed while its drop windows were analyzed.");
        }
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(world.Path)!, "anim.zbd");
            DocumentModel? owner = null; long ownerRevision = 0; AnimationPreviewContext context;
            if (world.SourceWorld != null)
            {
                if (world.SourceInputsChanged(verifyContent: false)) throw new StudioCommandException("stale_document", "Reload the world after source changes.");
                context = await Task.Run(() => AnimationPreviewContext.FromSources((zStudio.Core.Sources.SourceSceneSnapshot)world.Document,
                    ViewModel.Difficulty, cancellation.Token), cancellation.Token);
                Current();
            }
            else
            {
                owner = await ViewModel.OpenFileAsync(path, cancellation.Token, Current, activate: false, reportStatus: false)
                    ?? throw new StudioCommandException("open_failed", "Cannot open this world's animation archive.");
                Current();
                if (owner.Document.Animations == null) throw new StudioCommandException("unsupported", "This world has no animation archive.");
                ownerRevision = owner.Revision;
                context = await owner.GetAnimationContextAsync(resolver, cancellation.Token, world.Path, ViewModel.Difficulty);
                if (owner.IsDisposed || owner.Revision != ownerRevision) throw new StudioCommandException("context_changed", "The animation owner changed during analysis.");
            }
            Current();
            var discovery = await Task.Run(() => VtolDropWindows.Discover(context.Package, cancellation.Token), cancellation.Token);
            Current();
            if (owner != null && (owner.IsDisposed || owner.Revision != ownerRevision)) throw new StudioCommandException("context_changed", "The animation owner changed during analysis.");
            var state = new ParachuteSession(world, owner, viewport, context, discovery);
            var candidate = entry is int index ? discovery.Candidates.FirstOrDefault(c => c.EntryIndex == index) : discovery.Candidates.FirstOrDefault();
            if (entry != null && candidate == null) throw new StudioCommandException("stale_record", "The requested flight program is unavailable.");
            if (candidate != null)
            {
                state.Entry = candidate.EntryIndex;
                (state.Analysis, state.AnalysisRefusal) = await Task.Run(() => AnalyzeParachuteCandidate(context, candidate, cancellation.Token), cancellation.Token);
            }
            Current();
            if (owner != null && (owner.IsDisposed || owner.Revision != ownerRevision)) throw new StudioCommandException("context_changed", "The animation owner changed during analysis.");
            RequireParachutePublication(viewport);
            // A compiled world's drop windows are shown, never selected for a move.
            state.Selected = world.SourceWorld == null ? null : state.Analysis?.Windows.FirstOrDefault()?.Id; parachutes = state;
            world.PropertyChanged += ParachuteDocumentChanged; world.Disposing += CloseParachutes;
            if (owner != null) { owner.PropertyChanged += ParachuteDocumentChanged; owner.Disposing += CloseParachutes; }
            if (parachuteLoading == cancellation) parachuteLoading = null;
            if (world.SourceWorld != null && state.Entry is int flightEntry) { await EnsureAnimationCardForEntryAsync(world, flightEntry, cancellation.Token); Current(); ViewModel.Status = ParachuteAnalysisStatus; }
            PublishParachuteOverlay(); UpdateParachutePanel();
            CompleteParachuteStatus(request, world, viewport, ParachuteAnalysisResult(state));
        }
        catch (Exception ex) when (request != parachuteGeneration && ex is OperationCanceledException or StudioCommandException or IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new StudioCommandException("context_changed", "A newer Parachute request superseded this analysis."); }
        catch (Exception ex) when (ex is OperationCanceledException or StudioCommandException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            CompleteParachuteStatus(request, world, viewport, ParachuteAnalysisFailure(ex));
            throw;
        }
        finally { if (parachuteLoading == cancellation) parachuteLoading = null; UpdateParachutePanel(); }
    }
    private async Task AnalyzeParachuteAsync(int entry, CancellationToken token)
    {
        var state = RequireParachutes(); RequireNoDrafts();
        var candidate = state.Discovery.Candidates.FirstOrDefault(c => c.EntryIndex == entry) ?? throw new StudioCommandException("stale_record", "Choose a discovered flight entry.");
        long request = ++parachuteGeneration; parachuteLoading?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, state.World.Lifetime.Token, state.Owner?.Lifetime.Token ?? default, shutdownToken);
        parachuteLoading = cancellation;
        ViewModel.Status = ParachuteAnalysisStatus;
        UpdateParachutePanel();
        try
        {
            var result = await Task.Run(() => AnalyzeParachuteCandidate(state.Context, candidate, cancellation.Token), cancellation.Token);
            if (request != parachuteGeneration) throw new StudioCommandException("context_changed", "A newer Parachute analysis superseded this request.");
            RequireParachutes(state.Id); cancellation.Token.ThrowIfCancellationRequested();
            RequireParachutePublication(state.Viewport);
            if (parachuteLoading == cancellation) parachuteLoading = null;
            state.Entry = candidate.EntryIndex; (state.Analysis, state.AnalysisRefusal) = result;
            state.Selected = state.World.SourceWorld == null ? null : state.Analysis?.Windows.FirstOrDefault()?.Id; PublishParachuteOverlay(); UpdateParachutePanel();
            CompleteParachuteStatus(request, state.World, state.Viewport, ParachuteAnalysisResult(state));
        }
        catch (Exception ex) when (request != parachuteGeneration && ex is OperationCanceledException or StudioCommandException or IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new StudioCommandException("context_changed", "A newer Parachute request superseded this analysis."); }
        catch (Exception ex) when (ex is OperationCanceledException or StudioCommandException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            CompleteParachuteStatus(request, state.World, state.Viewport, ParachuteAnalysisFailure(ex));
            throw;
        }
        finally { if (parachuteLoading == cancellation) parachuteLoading = null; UpdateParachutePanel(); }
    }
    private void CompleteParachuteStatus(long request, DocumentModel world, SceneViewport viewport, string status, string? completedStatus = null)
    {
        if (request != parachuteGeneration || world.IsDisposed || shownDocument != world || ViewModel.SelectedDocument != world || scene != viewport ||
            ViewModel.Status != ParachuteAnalysisStatus && (completedStatus == null || ViewModel.Status != completedStatus)) return;
        // An independent status update owns the presentation now; completing our still-visible
        // progress does not resolve or mutate any draft begun while the analysis was running.
        ViewModel.Status = status;
    }
    private void HideParachutes()
    {
        if (parachutes is { } state)
            CompleteParachuteStatus(parachuteGeneration, state.World, state.Viewport, "VTOL drop windows hidden.", ParachuteAnalysisResult(state));
        CloseParachutes();
    }
    private static string ParachuteAnalysisResult(ParachuteSession state) => state.AnalysisRefusal is { } refusal
        ? "VTOL flight unavailable: " + refusal
        : state.Analysis is { } analysis ? $"VTOL drop windows ready: {analysis.Windows.Count} windows · {analysis.Releases.Count} eligible releases."
        : "No supported VTOL drop-window flights found.";
    private static string ParachuteAnalysisFailure(Exception error) => error is OperationCanceledException
        ? "VTOL drop-window analysis canceled." : "VTOL drop-window analysis failed: " + Bounded(error.Message, 512);
    private (VtolDropSnapshot? Snapshot, string? Refusal) AnalyzeParachuteCandidate(AnimationPreviewContext context, VtolDropCandidate candidate, CancellationToken token)
    {
        ParachuteAnalysisPreparing?.Invoke(token);
        try { return (VtolDropWindows.Analyze(context, candidate, token), null); }
        catch (InvalidDataException ex) { token.ThrowIfCancellationRequested(); return (null, Bounded(ex.Message, 512)); }
    }
    private void RequireParachutePublication(SceneViewport viewport)
    {
        RequireNoDrafts();
        if (sourceWorkspaceBusy || viewport.IsFlyActive || viewport.IsPickupDragging || viewport.HasTransformDraft ||
            viewport.AiPathEditingNetwork != null || viewport.TerrainBrushActive || viewport.ZonePaintActive)
            throw new StudioCommandException("busy", "Finish the active editing or navigation operation before publishing drop windows.");
    }
    private void PublishParachuteOverlay()
    {
        if (parachutes is not { } state) return;
        if (state.Analysis is not { } analysis) { state.Viewport.SetParachuteOverlay([], []); return; }
        Guid? selected = state.Selected;
        state.Viewport.SetParachuteOverlay(analysis.Samples.Select(p => new ParachutePathPoint(p.Time, p.Position)).ToArray(),
            analysis.Windows.Select(w => new ParachuteWindowOverlay(w.Id, w.EffectiveStart,
                analysis.Releases.Where(r => r.WindowId == w.Id).Select(r => new ParachuteRelease(r.Time, r.Position)).ToArray())).ToArray());
        state.Selected = selected;
        if (selected != null && state.World.SourceWorld != null) state.Viewport.SelectParachuteWindow(selected);
    }
    private void SelectParachute(Guid id)
    {
        var state = RequireParachutes(); RequireNoDrafts();
        if (state.World.SourceWorld == null) throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
        if (state.Analysis?.Windows.Any(w => w.Id == id) != true) throw new StudioCommandException("stale_record", "Choose a current drop window.");
        bool accepted;
        try { accepted = state.Viewport.SelectParachuteWindow(id); }
        catch (InvalidOperationException ex) { throw new StudioCommandException("busy", ex.Message); }
        if (!accepted) throw new StudioCommandException("busy", "Finish the current selection operation first.");
        state.Selected = id; UpdateParachutePanel();
    }
    private void BeginParachute(bool pointer = false)
    {
        var state = RequireParachutes(edit: true); RequireNoDrafts();
        EndZoneToolFor("moving a drop window");
        var window = state.Analysis?.Windows.FirstOrDefault(w => w.Id == state.Selected) ?? throw new StudioCommandException("selection_required", "Select a drop window.");
        if (!state.Viewport.SelectParachuteWindow(window.Id)) throw new StudioCommandException("busy", "Finish the current selection operation first.");
        parachuteDraft = new(state, window) { Plan = VtolDropWindows.PlanMove(state.Analysis!, window.Id, window.Start), Preview = state.Analysis, DisplayedPreview = state.Analysis };
        state.Viewport.SetParachutePreview(window.Id, window.EffectiveStart, state.Analysis!.Releases.Where(r => r.WindowId == window.Id).Select(r => new ParachuteRelease(r.Time, r.Position)).ToArray());
        state.Viewport.SetParachutePointerMove(pointer); UpdateParachutePanel();
    }
    private ParachuteDraft RequireParachuteDraft(string? token = null)
    {
        var draft = parachuteDraft ?? throw new StudioCommandException("no_draft", "Begin moving a drop window first.");
        if (token != null && token != draft.Token) throw new StudioCommandException("stale_draft", "Read the current Parachute draft token.");
        if (RequireParachutes(draft.Session.Id, edit: true) != draft.Session) throw new StudioCommandException("context_changed", "The draft's analysis changed.");
        return draft;
    }
    private void QueueParachutePreview(ParachuteDraft draft, bool debounce = false, CancellationToken token = default)
    {
        RequireParachuteInput(draft);
        draft.Preparing?.Cancel(); draft.Preparing?.Dispose();
        draft.Preparing = CancellationTokenSource.CreateLinkedTokenSource(token, draft.Session.World.Lifetime.Token, draft.Session.Owner?.Lifetime.Token ?? default, shutdownToken);
        draft.Preview = null; draft.Plan = null; draft.Error = null;
        int generation = ++draft.Generation; string input = draft.Input; var cancellation = draft.Preparing;
        draft.Work = PrepareAsync();
        async Task PrepareAsync()
        {
            try
            {
                if (input.Length > 64 || !float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float start) || !float.IsFinite(start))
                    throw new InvalidDataException("Enter a finite local start time in seconds.");
                var plan = VtolDropWindows.PlanMove(draft.Session.Analysis!, draft.Window.Id, start);
                if (debounce) await Task.Delay(100, cancellation.Token);
                var preview = await Task.Run(() => VtolDropWindows.PreviewMove(draft.Session.Context, draft.Session.Analysis!, plan, cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested(); RequireParachuteDraft(draft.Token);
                if (draft.Generation != generation) return;
                draft.Plan = plan; draft.Preview = preview;
                draft.Session.Viewport.SetParachutePreview(draft.Window.Id, preview.Windows.Single(w => w.Id == draft.Window.Id).EffectiveStart,
                    preview.Releases.Where(r => r.WindowId == draft.Window.Id).Select(r => new ParachuteRelease(r.Time, r.Position)).ToArray());
                draft.DisplayedPreview = preview;
            }
            catch (OperationCanceledException) { if (draft.Generation == generation && parachuteDraft == draft) draft.Error = "Preview canceled; enter the start time again to retry."; }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or InvalidOperationException or StudioCommandException)
            { if (draft.Generation == generation && parachuteDraft == draft) draft.Error = ex.Message; }
            finally { if (parachuteDraft == draft && draft.Generation == generation) UpdateParachutePanel(); }
        }
    }
    private static void RequireParachuteInput(ParachuteDraft draft)
    {
        if (draft.Accepting) throw new StudioCommandException("busy", "The drop-window move is being confirmed. Wait for it to finish or cancel it.");
    }
    private void CancelParachuteDraft() => ReleaseParachuteDraft(AiPathDraftLifetime.Canceled);
    private void ReleaseParachuteDraft(AiPathDraftLifetime lifetime)
    {
        if (parachuteDraft is not { } draft) return;
        draft.Lifetime = lifetime; ++draft.Generation; draft.Preparing?.Cancel(); draft.Preparing?.Dispose(); draft.Preparing = null;
        parachuteDraft = null; draft.Session.Viewport.SetParachutePointerMove(false); draft.Session.Viewport.ClearParachutePreview(); UpdateParachutePanel();
        // An object shown while the draft was open is selected in the view once whatever ended the draft has finished.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => ReselectShownSelection());
    }
    private bool ResolveParachuteDraft(DocumentModel? document = null)
    {
        if (parachuteDraft is not { } draft || document != null && document != draft.Session.World && document != draft.Session.Owner) return true;
        if (automationRequest.Value) return false;
        if (MessageBox.Show(this, "Discard the pending drop-window move? Choose No to return to Confirm.", "Parachute draft", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        CancelParachuteDraft(); return true;
    }
    private async Task ConfirmParachuteFromGuiAsync()
    {
        var draft = parachuteDraft; var state = draft?.Session; long generation = parachuteGeneration;
        try { await ConfirmParachuteAsync(CancellationToken.None); }
        catch (Exception ex) when (draft != null && state != null && ex is OperationCanceledException or StudioCommandException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // GUI cancellation/supersession is already reflected by the owning operation or newer
            // workspace. Do not let ReportUi turn it into a new error, status or Problems navigation.
            bool superseded = generation != parachuteGeneration || parachutes != state || shownDocument != state.World ||
                ViewModel.SelectedDocument != state.World || scene != state.Viewport || state.World.IsDisposed || state.Owner?.IsDisposed == true;
            bool canceledDraft = draft.Lifetime == AiPathDraftLifetime.Canceled &&
                (ex is OperationCanceledException || ex is StudioCommandException { Code: "no_draft" or "stale_draft" });
            if (superseded || canceledDraft || ex is OperationCanceledException && ViewModel.Status == "VTOL drop-window move canceled.") return;
            throw;
        }
    }
    private async Task<DocumentModel> ConfirmParachuteAsync(CancellationToken token)
    {
        var draft = RequireParachuteDraft(); RequireParachuteInput(draft);
        draft.Accepting = true;
        draft.Session.Viewport.SetParachutePointerMove(false); UpdateParachutePanel();
        try
        {
            await draft.Work.WaitAsync(token); RequireParachuteDraft(draft.Token);
            RequireNoDrafts(committingParachute: draft);
            if (draft.Plan == null || draft.Preview == null || draft.Error != null) throw new StudioCommandException("invalid_argument", draft.Error ?? "Wait for a valid preview.");
            // The worker receives this immutable accepted plan, never a mutable GUI draft field.
            return await ConfirmParachutePlanAsync(draft, draft.Plan, token);
        }
        finally { draft.Accepting = false; UpdateParachutePanel(); }
    }
    private async Task<DocumentModel> ConfirmParachutePlanAsync(ParachuteDraft draft, VtolDropMovePlan plan, CancellationToken token)
    {
        RequireNoDrafts(committingParachute: draft);
        var state = draft.Session;
        token.ThrowIfCancellationRequested();
        if (plan.NewStart == draft.Window.Start)
        { ReleaseParachuteDraft(AiPathDraftLifetime.Completed); return state.Owner ?? state.World; }
        if (state.World.SourceWorld is { } source)
        {
            long generation = parachuteGeneration;
            CancellationToken? preparationToken = null;
            try
            {
                acceptingParachute = true;
                var result = await PrepareSourceWorldEditAsync(state.World, "Moving VTOL drop window", (workspace, ct) =>
                    workspace.Apply("Move VTOL drop window", SourceVtolDropEdits.Prepare(workspace, source.Mission, state.World.Document.Scene!.Nodes.Select(n => n.Name).ToArray(), state.Context.Package, plan, ct).Select(c => (c.Relative, (byte[]?)c.Content)), ct),
                    token, committingParachute: draft, preparationStarted: owned => preparationToken = owned);
                draft.Lifetime = AiPathDraftLifetime.Completed;
                return result;
            }
            catch (Exception ex)
            {
                if (draft.Lifetime == AiPathDraftLifetime.ReleasedForAcceptance && parachuteDraft == null && parachutes == state && shownDocument == state.World && scene == state.Viewport &&
                    !state.World.IsDisposed && source.Owner == state.World && state.World.Revision == (draft.RolledBackRevision ?? state.WorldRevision))
                {
                    state.WorldRevision = state.OwnerRevision = state.World.Revision;
                    draft.RolledBackRevision = null; draft.Lifetime = AiPathDraftLifetime.Pending; parachuteDraft = draft;
                    if (draft.Preview is { } preview) state.Viewport.SetParachutePreview(draft.Window.Id, preview.Windows.Single(w => w.Id == draft.Window.Id).EffectiveStart,
                        preview.Releases.Where(r => r.WindowId == draft.Window.Id).Select(r => new ParachuteRelease(r.Time, r.Position)).ToArray());
                    UpdateParachutePanel();
                }
                if (preparationToken is { } owned && operation == null && generation == parachuteGeneration && parachutes == state &&
                    shownDocument == state.World && ViewModel.SelectedDocument == state.World && scene == state.Viewport &&
                    !state.World.IsDisposed && source.Owner == state.World &&
                    (ViewModel.Status == "Preparing Moving VTOL drop window…" || ViewModel.Status == SourcePreparationFailureStatus("Moving VTOL drop window", ex)
                        || owned.IsCancellationRequested && ViewModel.Status == "Canceling current operation…") &&
                    ex is OperationCanceledException or StudioCommandException or IOException or InvalidDataException or UnauthorizedAccessException)
                    ViewModel.Status = draft.Lifetime == AiPathDraftLifetime.Canceled || owned.IsCancellationRequested || ex is OperationCanceledException
                        ? "VTOL drop-window move canceled."
                        : "VTOL drop-window move failed: " + Bounded(ex.Message, 512);
                throw;
            }
            finally { acceptingParachute = false; }
        }
        throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
    }
    private async Task ParachuteHistoryAsync(bool redo, CancellationToken token)
    {
        var state = RequireParachutes(); RequireNoDrafts();
        if (state.World.SourceWorld == null) throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
        await UndoSourceWorldAsync(state.World, redo, token);
    }
    private async void ParachuteActionClick(object sender, RoutedEventArgs e) => await ReportUi(async () =>
    {
        string action = (sender as FrameworkElement)?.Tag as string ?? "show";
        if (action is not ("confirm" or "cancel") && !await ResolvePropertiesDraftsAsync()) return;
        if (action == "show") { if (parachutes != null) HideParachutes(); else await ShowParachutesAsync(CancellationToken.None); }
        else if (action == "begin") BeginParachute(pointer: true);
        else if (action == "numeric") BeginParachute();
        else if (action == "confirm") await ConfirmParachuteFromGuiAsync();
        else if (action == "cancel") CancelParachuteDraft();
        else if (action is "undo" or "redo") await ParachuteHistoryAsync(action == "redo", CancellationToken.None);
        else if (action == "save") await SaveSourceWorldDocumentAsync(ParachuteSaveWorld());
    });
    private async void ParachuteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizingParachutes || !ready || parachutes == null) return;
        await ReportUi(async () =>
        {
            try
            {
                if (!await ResolvePropertiesDraftsAsync()) return;
                if (sender == ParachuteFlights && ParachuteFlights.SelectedItem is ParachuteChoice flight) await AnalyzeParachuteAsync(flight.Entry, CancellationToken.None);
                else if (ParachuteWindows.SelectedItem is ParachuteWindowChoice window) SelectParachute(window.Id);
            }
            finally { UpdateParachutePanel(); }
        });
    }
    private void ParachuteInputChanged(object sender, TextChangedEventArgs e)
    {
        if (synchronizingParachutes || parachuteDraft is not { } draft) return;
        if (draft.Accepting) { UpdateParachutePanel(); return; }
        draft.Input = ParachuteStart.Text; draft.Session.Viewport.SetParachutePointerMove(false); QueueParachutePreview(draft, debounce: true); UpdateParachutePanel();
    }
    private bool ParachuteKeyboard(KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != ModifierKeys.None || CameraInputIsNative(e.OriginalSource as DependencyObject) || CameraInputIsNative(System.Windows.Input.Keyboard.FocusedElement as DependencyObject)) return false;
        if (parachuteDraft == null)
        { if (e.Key != Key.Escape || parachuteLoading == null) return false; parachuteLoading.Cancel(); e.Handled = true; return true; }
        if (e.Key == Key.Escape) { e.Handled = true; CancelParachuteDraft(); return true; }
        if (e.Key != Key.Enter) return false;
        e.Handled = true; if (!e.IsRepeat) _ = ReportUi(ConfirmParachuteFromGuiAsync); return true;
    }
    private void UpdateParachutePanel()
    {
        if (ParachutePanel == null) return;
        sectionCard?.SetExternalAnimationDraft(parachuteDraft != null);
        synchronizingParachutes = true;
        try
        {
            ParachutePanel.Visibility = parachutes == null ? Visibility.Collapsed : Visibility.Visible;
            if (parachutes is not { } state) return;
            ParachutePanel.IsEnabled = !sourceWorkspaceBusy && parachuteLoading == null && parachuteDraft?.Accepting != true;
            bool editable = state.World.SourceWorld != null;
            ParachuteOwner.Text = editable ? "Animation sources · project undo and save" : $"{state.Owner?.Title} · read only";
            ParachuteOwner.ToolTip = state.Owner?.Path ?? state.World.SourceWorld?.DefinitionsPath;
            ParachuteHistory.Visibility = Visibility.Collapsed;
            ParachuteUndo.IsEnabled = state.World.SourceWorld?.Workspace.CanUndo == true;
            ParachuteRedo.IsEnabled = state.World.SourceWorld?.Workspace.CanRedo == true;
            ParachuteFlights.ItemsSource = state.Discovery.Candidates.Select(c => new ParachuteChoice(c.EntryIndex, $"#{c.EntryIndex} {ShortAiText(c.Name)} · {ShortAiText(c.RootName)}")).ToArray();
            ParachuteFlights.SelectedItem = ParachuteFlights.Items.Cast<ParachuteChoice>().FirstOrDefault(c => c.Entry == state.Entry);
            ParachuteWindows.ItemsSource = state.Analysis?.Windows.Select((w, i) => new ParachuteWindowChoice(w.Id, $"{i + 1}: {w.Start:G6} s · {w.Duration:G6} s window")).ToArray() ?? [];
            ParachuteWindows.SelectedItem = ParachuteWindows.Items.Cast<ParachuteWindowChoice>().FirstOrDefault(w => w.Id == state.Selected);
            ParachuteDraftPanel.Visibility = parachuteDraft == null ? Visibility.Collapsed : Visibility.Visible;
            var selectedWindow = state.Analysis?.Windows.FirstOrDefault(w => w.Id == state.Selected);
            ParachuteMoveButtons.Visibility = parachuteDraft == null && editable ? Visibility.Visible : Visibility.Collapsed;
            ParachuteMoveButtons.IsEnabled = selectedWindow is { MoveRefusal: null };
            ParachuteFlights.IsEnabled = parachuteDraft == null;
            ParachuteFlights.Visibility = state.World.SourceWorld != null ? Visibility.Collapsed : Visibility.Visible;
            ParachuteWindows.IsEnabled = parachuteDraft == null && editable;
            ParachuteStart.Text = parachuteDraft?.Input ?? "";
            ParachuteConfirm.IsEnabled = parachuteDraft is { Plan: not null, Preview: not null, Error: null };
            ParachuteMessage.Text = parachuteDraft is { } d ? d.Error ?? (d.Preview == null ? "Analyzing proposed release positions…" : $"Start range {d.Window.MinimumStart:G6}–{d.Window.MaximumStart:G6} s. Confirm accepts one undo step.")
                : state.AnalysisRefusal ?? selectedWindow?.MoveRefusal ?? (state.Analysis is { } a ? $"{a.CycleSeconds:G6} s cycle · {a.Releases.Count} eligible releases. Conditions still apply; markers are not landing predictions."
                : state.Discovery.Diagnostics.Count == 0 ? "No supported callback-911 flight programs in this mission." : string.Join("\n", state.Discovery.Diagnostics.Take(3).Select(s => Bounded(s, 300))));
        }
        finally { synchronizingParachutes = false; }
    }
}
