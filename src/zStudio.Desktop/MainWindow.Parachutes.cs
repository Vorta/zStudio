using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
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
        public bool Stale;
        public string? StaleReason;
        public readonly Guid Scenario = Guid.NewGuid();
        /// <summary>The scenario the analysis used: its player reference, condition switches and rules.</summary>
        public AnimationExecutionPolicy? Policy;
        public Guid Run = Guid.NewGuid();
        public readonly int SceneGeneration = viewport.SceneGeneration;
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
    private Task parachuteSelectionWork = Task.CompletedTask;
    private long parachuteGeneration;
    private const string ParachuteAnalysisStatus = "Analyzing VTOL drop windows…";
    private bool synchronizingParachutes, acceptingParachute;
    internal Action<CancellationToken>? ParachuteAnalysisPreparing { get; set; }
    private sealed record ParachuteChoice(int Entry, string Label) { public override string ToString() => Label; }
    /// <summary>A row of the window list: the window, its start on the flight sequence's clock and its duration.</summary>
    internal sealed record ParachuteWindowChoice(Guid Id, string Window, string Start, string Duration)
    {
        public override string ToString() => $"{Window}: {Start} · {Duration} window";
    }
    /// <summary>The map's recognised flights (VTOL flight-to-callback-911 programs), discovered once per animation package.</summary>
    private (AnimationPackage Package, VtolDropDiscovery Discovery)? dropDiscovery;
    private ViewportChip? dropMoveBanner;
    private VtolDropDiscovery? DropDiscovery(AnimationPackage? package)
    {
        if (package == null) return null;
        if (dropDiscovery is { } cached && ReferenceEquals(cached.Package, package)) return cached.Discovery;
        try { dropDiscovery = (package, VtolDropWindows.Discover(package)); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { dropDiscovery = (package, new VtolDropDiscovery([], [Bounded(ex.Message, 300)])); }
        return dropDiscovery.Value.Discovery;
    }
    /// <summary>The recognised flights among <paramref name="definition"/>'s bindings (drop windows are offered only for those, design §14).</summary>
    private IReadOnlyList<VtolDropCandidate> DropCandidates(SourceAnimationDefinition definition) =>
        DropDiscovery((shownDocument?.Document as SourceSceneSnapshot)?.Animations)?.Candidates.Where(c => definition.Entries.Contains(c.EntryIndex)).ToArray() ?? [];
    /// <summary>The flight Animation mode's drop windows analyze: the opened binding when it is one, else the opened definition's first.</summary>
    private int OpenedFlightEntry()
    {
        if (animationMode is not { } mode || shownDocument?.Document is not SourceSceneSnapshot source
            || source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == mode.Definition) is not { } definition)
            throw new StudioCommandException("not_ready", "Drop windows belong to Animation mode: open the flight's definition (animation_mode open).");
        var candidates = DropCandidates(definition);
        if (candidates.Count == 0) throw new StudioCommandException("unsupported", "The opened definition has no recognised flight-to-callback-911 calls: open a flight's definition to analyze its drop windows.");
        return candidates.FirstOrDefault(c => c.EntryIndex == mode.Entry)?.EntryIndex ?? candidates[0].EntryIndex;
    }
    /// <summary>
    /// The drop-window panel's host: in a source map the Inspector's Definition → Drop windows section (Animation mode), otherwise
    /// (a compiled Whole world) the floating panel over the view.
    /// </summary>
    private void HostParachutePanel(bool inCard)
    {
        if (inCard)
        {
            if (ParachutePanel.Parent == PreviewOverlayChrome) PreviewOverlayChrome.Children.Remove(ParachutePanel);
            ParachutePanel.Width = double.NaN; ParachutePanel.Margin = new(0, 2, 0, 0); ParachutePanel.Padding = new(0);
            ParachutePanel.HorizontalAlignment = HorizontalAlignment.Stretch; ParachutePanel.VerticalAlignment = VerticalAlignment.Top;
            ParachutePanel.BorderThickness = new(0); ParachutePanel.Background = Brushes.Transparent;
            return;
        }
        // A compiled Whole world: the Drop windows chip's flyout over the view (read only).
        HostParachuteInChip();
    }
    /// <summary>
    /// Move along path's banner (the Flycam banner style): "Moving drop window 2 · click a point on the flight path · right-click
    /// or Esc cancels". It shows while the view moves a window's start and ends with it (placed, cancelled, Exit, map end, rebase).
    /// </summary>
    private void RefreshDropMoveBanner()
    {
        bool moving = parachuteDraft is { } draft && draft.Session.Viewport == scene && scene?.IsParachutePointerMoving == true && InAnimationMode;
        if (!moving && dropMoveBanner == null) return;
        if (dropMoveBanner == null)
        {
            dropMoveBanner = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 12, 0, 0), MinHeight = 36, Padding = new(12, 0, 4, 0), FontSize = 13 };
            AutomationProperties.SetName(dropMoveBanner, "Moving a drop window");
            AutomationProperties.SetLiveSetting(dropMoveBanner, AutomationLiveSetting.Polite);
            StackPanel content = new() { Orientation = Orientation.Horizontal };
            var icon = new PreviewIcon { Kind = "Parachute", Width = 15, Height = 15, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(PreviewIcon.ForegroundProperty, "AccentFillColorDefaultBrush");
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0) });
            Button cancel = new() { Content = "Cancel", Height = 28, Padding = new(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(cancel, "Cancel moving the drop window");
            cancel.Click += (_, _) => CancelParachuteDraft();
            content.Children.Add(cancel);
            dropMoveBanner.Content = content;
            PreviewOverlayChrome.Children.Add(dropMoveBanner);
        }
        if (moving)
        {
            int number = parachuteDraft!.Session.Analysis?.Windows.ToList().FindIndex(w => w.Id == parachuteDraft.Window.Id) + 1 ?? 1;
            ((TextBlock)((StackPanel)dropMoveBanner.Content).Children[1]).Text = $"Moving drop window {number} · click a point on the flight path · right-click or Esc cancels";
        }
        dropMoveBanner.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>The view's move ends with Animation mode, its map or a rebuild: the draft goes back (as Esc does), never left half done.</summary>
    private void EndDropMove()
    {
        if (parachuteDraft != null && scene?.IsParachutePointerMoving == true) CancelParachuteDraft();
        RefreshDropMoveBanner();
    }

    private void ConfigureParachuteScene(SceneViewport viewport)
    {
        viewport.ParachuteSelectionChanging = () => parachuteLoading == null && parachuteDraft == null && ResolvePropertiesDrafts();
        viewport.ParachuteWindowSelected += id =>
        {
            if (parachutes is not { } state || state.Viewport != viewport) return;
            state.Selected = id; UpdateParachutePanel();
            if (id != null && state.World.SourceWorld != null && state.Entry is int entry)
            {
                parachuteSelectionWork = RevealParachuteCardAsync(state, entry);
                _ = ReportUi(() => parachuteSelectionWork);
            }
        };
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
        // A click on the flight path places the window's start there (the draft stays for Apply or Revert); right-click cancels.
        viewport.ParachuteConfirmRequested += () =>
        {
            if (parachuteDraft is not { Accepting: false } draft || draft.Session.Viewport != viewport) return;
            viewport.SetParachutePointerMove(false); UpdateParachutePanel();
        };
        viewport.ParachuteCancelRequested += CancelParachuteDraft;
    }

    private async Task RevealParachuteCardAsync(ParachuteSession state, int entry)
    {
        await EnsureAnimationCardForEntryAsync(state.World, entry, state.World.Lifetime.Token);
        if (parachutes != state || shownDocument != state.World || state.World.Revision != state.WorldRevision)
            throw new StudioCommandException("context_changed", "The selected flight changed while opening its controls.");
        // In Animation mode a window's marker shows the opened definition's card at Drop windows.
        if (InAnimationMode && animationInspected != null)
        {
            animationGraphSelection = null; animationGraphOutline = null; animationInspected = null;
            animationGraphView?.Select(null); animationTimingView?.Select(null);
            ApplySectionPresentation();
        }
        if (InAnimationMode) ShowAnimationRightTab("inspector");
        RefreshAnimationInspector();
        UpdateParachutePanel();
        sectionCard?.RevealSection("drops");
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
        CloseParachutes(); ViewModel.Status = "The flight's definition changed. Analyze drop windows again for its current animation.";
    }
    /// <summary>The drop-window analysis's world for Save: a source world, saved as the project is (save_document), whatever gesture runs.</summary>
    private DocumentModel ParachuteSaveWorld(string? snapshot = null)
    {
        var world = RequireParachutes(snapshot).World;
        return world.SourceWorld != null ? world : throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
    }
    private ParachuteSession RequireParachutes(string? snapshot = null, bool edit = false)
    {
        var state = parachutes ?? throw new StudioCommandException("not_ready", "Analyze drop windows first: in Animation mode on a VTOL flight, or with a compiled Whole world's Drop windows chip.");
        if (snapshot != null && snapshot != state.Id) throw new StudioCommandException("stale_snapshot", "Read the current drop-window snapshot.");
        if (!IsAiWorld || shownDocument != state.World || scene != state.Viewport || state.World.IsDisposed || state.Owner?.IsDisposed == true ||
            state.World.Revision != state.WorldRevision || (state.Owner?.Revision ?? state.World.Revision) != state.OwnerRevision ||
            state.Viewport.SceneGeneration != state.SceneGeneration || state.Stale)
            throw new StudioCommandException("context_changed", "The world or animation changed; close and reopen the route editor.");
        if (state.Policy is { } used && !SameDropScenario(used, worldAnimationConfiguration.ToExecutionPolicy()))
            throw new StudioCommandException("context_changed", "The animation scenario changed; show the drop windows again to analyze them for it.");
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
        // A source map's drop windows are the opened flight's (Inspector → Definition → Drop windows in Animation mode).
        if (world.SourceWorld != null)
        {
            int flight = OpenedFlightEntry();
            if (entry is int asked && asked != flight && !DropCandidates(((SourceSceneSnapshot)world.Document).AnimationDefinitions.Definitions.First(d => d.Index == animationMode!.Definition)).Any(c => c.EntryIndex == asked))
                throw new StudioCommandException("invalid_argument", "Choose a flight of the opened definition (its recognised flight-to-callback-911 bindings).");
            entry ??= flight;
        }
        else HostParachutePanel(inCard: false);
        CloseParachutes();
        var viewport = scene!; long request = ++parachuteGeneration, revision = world.Revision;
        int baselineGeneration = viewport.SceneGeneration;
        // The analysis belongs to the scenario as it is now; a change to it makes the result another scenario's.
        var scenario = worldAnimationConfiguration.ToExecutionPolicy();
        started?.Invoke(request);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, world.Lifetime.Token, ViewModel.WorkspaceToken, shutdownToken);
        parachuteLoading = cancellation;
        ViewModel.Status = ParachuteAnalysisStatus;
        void Current()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (request != parachuteGeneration || shownDocument != world || scene != viewport || world.Revision != revision || viewport.SceneGeneration != baselineGeneration)
                throw new StudioCommandException("context_changed", "The world changed while its drop windows were analyzed.");
            if (!SameDropScenario(scenario, worldAnimationConfiguration.ToExecutionPolicy()))
                throw new StudioCommandException("context_changed", "The animation scenario changed while drop windows were analyzed.");
        }
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(world.Path)!, "anim.zbd");
            DocumentModel? owner = null; long ownerRevision = 0; AnimationPreviewContext context;
            if (world.SourceWorld != null)
            {
                if (world.SourceInputsChanged(verifyContent: false)) throw new StudioCommandException("stale_document", "Reload the world after source changes.");
                context = await PrepareWorldAnimationContextAsync(world, cancellation.Token);
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
            var state = new ParachuteSession(world, owner, viewport, context, discovery) { Policy = scenario };
            var candidate = entry is int index ? discovery.Candidates.FirstOrDefault(c => c.EntryIndex == index) : discovery.Candidates.FirstOrDefault();
            if (entry != null && candidate == null) throw new StudioCommandException("stale_record", "The requested flight program is unavailable.");
            if (candidate != null)
            {
                state.Entry = candidate.EntryIndex;
                (state.Analysis, state.AnalysisRefusal) = await Task.Run(() => AnalyzeParachuteCandidate(context, candidate, scenario, cancellation.Token), cancellation.Token);
            }
            Current();
            if (owner != null && (owner.IsDisposed || owner.Revision != ownerRevision)) throw new StudioCommandException("context_changed", "The animation owner changed during analysis.");
            RequireParachutePublication(viewport);
            // Analysis publishes evidence only. Selecting a window is an explicit inspection action;
            // doing it here would change the Inspector's selection behind the user's back.
            state.Selected = null; parachutes = state;
            world.PropertyChanged += ParachuteDocumentChanged; world.Disposing += CloseParachutes;
            if (owner != null) { owner.PropertyChanged += ParachuteDocumentChanged; owner.Disposing += CloseParachutes; }
            if (parachuteLoading == cancellation) parachuteLoading = null;
            PublishParachuteOverlay(); RefreshWorldAnimationDropRelationships(); UpdateParachutePanel();
            CompleteParachuteStatus(request, world, viewport, ParachuteAnalysisResult(state));
        }
        catch (Exception ex) when (request != parachuteGeneration && ex is OperationCanceledException or StudioCommandException or IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new StudioCommandException("context_changed", "A newer drop-window request superseded this analysis."); }
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
        if (state.World.SourceWorld != null && (animationMode is not { } opened || ((SourceSceneSnapshot)state.World.Document).AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == opened.Definition)?.Entries.Contains(entry) != true))
            throw new StudioCommandException("invalid_argument", "Choose a flight of the opened definition (its recognised flight-to-callback-911 bindings).");
        long request = ++parachuteGeneration; parachuteLoading?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, state.World.Lifetime.Token, state.Owner?.Lifetime.Token ?? default, shutdownToken);
        parachuteLoading = cancellation;
        ViewModel.Status = ParachuteAnalysisStatus;
        UpdateParachutePanel();
        try
        {
            var scenario = worldAnimationConfiguration.ToExecutionPolicy();
            var result = await Task.Run(() => AnalyzeParachuteCandidate(state.Context, candidate, scenario, cancellation.Token), cancellation.Token);
            if (request != parachuteGeneration) throw new StudioCommandException("context_changed", "A newer drop-window analysis superseded this request.");
            RequireParachutes(state.Id); cancellation.Token.ThrowIfCancellationRequested();
            if (!SameDropScenario(scenario, worldAnimationConfiguration.ToExecutionPolicy()))
                throw new StudioCommandException("context_changed", "The animation scenario changed while drop windows were analyzed.");
            RequireParachutePublication(state.Viewport);
            if (parachuteLoading == cancellation) parachuteLoading = null;
            state.Entry = candidate.EntryIndex; state.Run = Guid.NewGuid(); state.Policy = scenario; (state.Analysis, state.AnalysisRefusal) = result;
            state.Selected = null; PublishParachuteOverlay(); UpdateParachutePanel();
            CompleteParachuteStatus(request, state.World, state.Viewport, ParachuteAnalysisResult(state));
        }
        catch (Exception ex) when (request != parachuteGeneration && ex is OperationCanceledException or StudioCommandException or IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new StudioCommandException("context_changed", "A newer drop-window request superseded this analysis."); }
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
    /// <summary>The callback's conditions follow the map's animation scenario: its overrides (for the flight as root start) and rules.</summary>
    private (VtolDropSnapshot? Snapshot, string? Refusal) AnalyzeParachuteCandidate(AnimationPreviewContext context, VtolDropCandidate candidate, AnimationExecutionPolicy scenario, CancellationToken token)
    {
        ParachuteAnalysisPreparing?.Invoke(token);
        try { return (VtolDropWindows.Analyze(context, candidate, token, scenario), null); }
        catch (InvalidDataException ex) { token.ThrowIfCancellationRequested(); return (null, Bounded(ex.Message, 512)); }
    }
    /// <summary>Whether two scenarios analyze drop windows alike: the same rules (the player among them) and condition switches; their call inclusion is not used.</summary>
    private static bool SameDropScenario(AnimationExecutionPolicy a, AnimationExecutionPolicy b) =>
        a.Rules == b.Rules && a.ConditionOverrides.ToHashSet().SetEquals(b.ConditionOverrides);
    /// <summary>A scenario change leaves the shown drop analysis as another scenario's evidence: stale until shown again.</summary>
    private void ParachuteScenarioChanged()
    {
        if (parachutes is { Policy: { } used } && !SameDropScenario(used, worldAnimationConfiguration.ToExecutionPolicy()))
            InvalidateWorldAnimationDropsForEdit("Drop analysis is stale: the animation scenario changed (its player, condition switches or rules). Show the drop windows again to analyze them for it.");
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
    private async Task SelectParachuteAsync(Guid id)
    {
        var state = RequireParachutes(); RequireNoDrafts();
        if (state.World.SourceWorld == null) throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
        if (state.Analysis?.Windows.Any(w => w.Id == id) != true) throw new StudioCommandException("stale_record", "Choose a current drop window.");
        bool accepted;
        try { accepted = state.Viewport.SelectParachuteWindow(id); }
        catch (InvalidOperationException ex) { throw new StudioCommandException("busy", ex.Message); }
        if (!accepted) throw new StudioCommandException("busy", "Finish the current selection operation first.");
        await parachuteSelectionWork;
        state.Selected = id; UpdateParachutePanel();
    }
    private void BeginParachute(bool pointer = false)
    {
        var state = RequireParachutes(edit: true); RequireNoDrafts();
        StopWorldAnimationForEdit();
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
        if (token != null && token != draft.Token) throw new StudioCommandException("stale_draft", "Read the current drop-window draft token.");
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
        if (MessageBox.Show(this, "Discard the pending drop-window move? Choose No to return to Apply.", "Drop-window move", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
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
        string action = (sender as FrameworkElement)?.Tag as string ?? "";
        if (action is not ("confirm" or "cancel") && !await ResolvePropertiesDraftsAsync()) return;
        if (action == "analyze" && CompiledWorldShown) await ShowParachutesAsync(CancellationToken.None, parachutes?.Entry);
        else if (action == "analyze" && animationMode is { } mode) await AnalyzeWorldDropWindowsAsync(mode.Document, mode.Definition, OpenedFlightEntry(), CancellationToken.None);
        else if (action == "begin") BeginParachute(pointer: true);
        else if (action == "confirm") await ConfirmParachuteFromGuiAsync();
        else if (action == "cancel") CancelParachuteDraft();
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
                else if (ParachuteWindows.SelectedItem is ParachuteWindowChoice window) await SelectParachuteAsync(window.Id);
            }
            finally { UpdateParachutePanel(); }
        });
    }
    private void ParachuteInputChanged(object sender, TextChangedEventArgs e)
    {
        if (synchronizingParachutes) return;
        // Typing a start for the selected window proposes a move: the draft begins with the first change.
        if (parachuteDraft == null)
        {
            string typed = ParachuteStart.Text; int caret = ParachuteStart.CaretIndex;
            try { BeginParachute(); }
            catch (StudioCommandException ex) { Report(ex); UpdateParachutePanel(); return; }
            synchronizingParachutes = true;
            try { ParachuteStart.Text = typed; ParachuteStart.CaretIndex = Math.Min(caret, typed.Length); }
            finally { synchronizingParachutes = false; }
        }
        if (parachuteDraft is not { } draft) return;
        if (draft.Accepting) { UpdateParachutePanel(); return; }
        draft.Input = ParachuteStart.Text; draft.Session.Viewport.SetParachutePointerMove(false); QueueParachutePreview(draft, debounce: true); UpdateParachutePanel();
    }
    /// <summary>
    /// Timing's Drop windows lane (design §14): under the opened flight's sequence lane, each analyzed window on every flight
    /// cycle the run reached (a cycle starts with the drop schedule's first event), the moved window at its proposed start.
    /// </summary>
    private AnimationTimingDrops? TimingDrops(AnimationTiming? timing, AnimationPackage package)
    {
        if (timing == null || parachutes is not { Analysis: { } analysis } state || state.World != shownDocument || state.World.SourceWorld == null || animationMode is not { } mode
            || state.World.Document is not SourceSceneSnapshot source || source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == mode.Definition) is not { } definition) return null;
        var candidate = analysis.Candidate;
        if (!definition.Entries.Contains(candidate.EntryIndex) || candidate.EntryIndex >= package.Entries.Count) return null;
        bool Of(AnimationTimingLane lane, Guid sequence) => lane.Kind == AnimationTimingLaneKind.Sequence && lane.Sequences.Any(s => s.Entry == candidate.EntryIndex && s.Sequence == sequence);
        var anchor = timing.Lanes.Where(l => Of(l, candidate.MotionSequenceId)).OrderBy(l => l.Path.Length).FirstOrDefault();
        if (anchor == null) return null;
        var schedule = timing.AllLanes.Where(l => Of(l, candidate.ScheduleSequenceId)).OrderBy(l => l.Path == anchor.Path ? 0 : 1).FirstOrDefault();
        Guid first = package.Entries[candidate.EntryIndex].AllSequences.FirstOrDefault(s => s.Id == candidate.ScheduleSequenceId)?.Events.FirstOrDefault()?.Id ?? Guid.Empty;
        var cycles = schedule?.Marks.Where(m => m.Event == first).Select(m => m.Start).Distinct().Order().Take(64).ToArray() ?? [];
        var preview = parachuteDraft?.DisplayedPreview;
        List<AnimationTimingDropBar> bars = [];
        foreach (double cycle in cycles)
            for (int i = 0; i < analysis.Windows.Count; i++)
            {
                var window = analysis.Windows[i];
                bool moved = parachuteDraft?.Window.Id == window.Id && preview?.Windows.FirstOrDefault(w => w.Id == window.Id) is not null;
                var shown = moved ? preview!.Windows.First(w => w.Id == window.Id) : window;
                double maximum = Math.Min(window.MaximumStart, analysis.CycleSeconds);
                // The authored start and duration on the flight sequence's clock (what a drag moves and the list shows), not the dispatch tick.
                bars.Add(new(window.Id, i + 1, cycle, shown.Start, window.Duration, window.MinimumStart, maximum) { Moved = moved });
            }
        return new(anchor.Id, bars, !state.Stale && parachuteDraft?.Accepting != true)
        {
            Selected = state.Selected, Stale = state.Stale,
            Note = state.Stale ? "Stale: analyze the drop windows again." : cycles.Length == 0 ? "Play or seek: the windows show on every flight cycle the run reaches." : "",
        };
    }
    /// <summary>A window clicked on Timing's lane: selected, as a click on its marker in the view selects it.</summary>
    private async Task SelectDropWindowAsync(Guid id)
    {
        var state = RequireParachutes();
        if (state.Selected != id) await SelectParachuteAsync(id);
        if (InAnimationMode) { ShowAnimationRightTab("inspector"); sectionCard?.RevealSection("drops"); }
    }
    /// <summary>A window dragged on Timing's lane: its start (on the flight sequence's clock) through the guarded draft; Apply keeps it.</summary>
    private async Task MoveDropWindowAsync(Guid id, double start)
    {
        var state = RequireParachutes(edit: true);
        if (parachuteDraft is { } open && open.Window.Id != id)
            throw new StudioCommandException("pending_drafts", "Apply or revert the move of the other drop window first.");
        if (parachuteDraft == null)
        {
            if (state.Selected != id) await SelectParachuteAsync(id);
            BeginParachute();
        }
        var draft = RequireParachuteDraft();
        draft.Input = start.ToString("G9", CultureInfo.InvariantCulture);
        QueueParachutePreview(draft); UpdateParachutePanel();
        if (InAnimationMode) { ShowAnimationRightTab("inspector"); sectionCard?.RevealSection("drops"); }
        await draft.Work;
    }
    /// <summary>"Admits 2 callsites: drop_schedule › Call callback at 1.000 s, at 5.000 s → callback (CALLBACK 911)".</summary>
    private string DropCallsites(VtolDropCandidate candidate)
    {
        var package = (shownDocument?.Document as SourceSceneSnapshot)?.Animations ?? parachutes?.Context.Package;
        if (package == null || candidate.EntryIndex >= package.Entries.Count || candidate.CallbackEntryIndex >= package.Entries.Count) return "";
        var flight = package.Entries[candidate.EntryIndex];
        string schedule = flight.AllSequences.FirstOrDefault(s => s.Id == candidate.ScheduleSequenceId)?.Name ?? "schedule";
        string callback = package.Entries[candidate.CallbackEntryIndex].Name;
        string times = string.Join(", ", candidate.Windows.Take(16).Select(w => "at " + AnimationSeconds.Format(w.Start))) + (candidate.Windows.Count > 16 ? ", …" : "");
        return $"Admits {AnimationNotice.Plural(candidate.Windows.Count, "1 callsite", "callsites")}: {schedule} › Call {callback} {times} → {callback} (CALLBACK 911, #{candidate.CallbackEntryIndex}). Nothing else of the flight's calls runs in the analysis.";
    }
    private static DataTemplate ParachuteWindowTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        foreach (var width in new[] { new GridLength(58), new GridLength(1, GridUnitType.Star), new GridLength(72) })
        {
            var column = new FrameworkElementFactory(typeof(ColumnDefinition)); column.SetValue(ColumnDefinition.WidthProperty, width); grid.AppendChild(column);
        }
        int index = 0;
        foreach (var (path, right) in new[] { ("Window", false), ("Start", false), ("Duration", true) })
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(path));
            text.SetValue(Grid.ColumnProperty, index++);
            text.SetValue(TextBlock.FontSizeProperty, 12.5);
            text.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
            if (right) text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            grid.AppendChild(text);
        }
        var template = new DataTemplate { VisualTree = grid }; template.Seal();
        return template;
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
            bool source = shownDocument?.SourceWorld != null;
            // A source map offers drop windows in Animation mode's Definition card of a flight (design §14); a compiled Whole
            // world shows its floating panel once MCP shows them.
            var openedDefinition = source && InAnimationMode && sectionCard?.Document == shownDocument && sectionCard?.Entry is DefinitionEntry shownDefinition
                && shownDefinition.Definition.Index == animationMode!.Definition ? shownDefinition.Definition : null;
            var candidates = openedDefinition == null ? [] : DropCandidates(openedDefinition);
            bool offered = source ? candidates.Count > 0 : parachutes != null || CompiledWorldShown;
            ParachutePanel.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
            var state = parachutes is { } shownState && (!source || shownState.Entry is int analyzed && openedDefinition?.Entries.Contains(analyzed) == true) ? shownState : null;
            bool busy = sourceWorkspaceBusy || parachuteLoading != null || parachuteDraft?.Accepting == true;
            ParachuteAnalyze.Visibility = source || CompiledWorldShown ? Visibility.Visible : Visibility.Collapsed;
            ParachuteAnalyze.IsEnabled = !busy && parachuteDraft == null && offered;
            ParachuteAnalyze.Content = parachuteLoading != null ? "Analyzing drop windows…" : state == null ? "Analyze drop windows" : "Analyze again";
            ParachutePolicy.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
            // The callsites the analysis admits: each window's call of the callback-911 program, a visible list.
            var candidate = state?.Discovery.Candidates.FirstOrDefault(c => c.EntryIndex == state.Entry)
                ?? (animationMode is { } mode ? candidates.FirstOrDefault(c => c.EntryIndex == mode.Entry) : null) ?? candidates.FirstOrDefault();
            ParachuteCallsites.Text = candidate == null ? "" : DropCallsites(candidate);
            ParachuteCallsites.Visibility = candidate == null ? Visibility.Collapsed : Visibility.Visible;
            bool analyzedHere = state != null;
            foreach (UIElement part in new UIElement[] { ParachuteWindowsHeader, ParachuteWindows }) part.Visibility = analyzedHere && state!.Analysis != null ? Visibility.Visible : Visibility.Collapsed;
            if (state == null)
            {
                ParachuteOwner.Visibility = ParachuteFlights.Visibility = ParachuteMoveButtons.Visibility = ParachuteDraftPanel.Visibility = Visibility.Collapsed;
                ParachutePanel.IsEnabled = true;
                ParachuteMessage.Text = !offered ? "" : parachuteLoading != null ? ParachuteAnalysisStatus
                    : "Not analyzed. Analyze drop windows runs the flight's calls to callback 911 with the scenario's player, switches and rules; the main run stays as it is.";
                return;
            }
            ParachutePanel.IsEnabled = !busy;
            bool editable = state.World.SourceWorld != null;
            ParachuteOwner.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
            ParachuteOwner.Text = editable ? "" : $"{state.Owner?.Title} · read only";
            ParachuteOwner.ToolTip = state.Owner?.Path ?? state.World.SourceWorld?.DefinitionsPath;
            ParachuteFlights.ItemsSource = state.Discovery.Candidates.Where(c => !editable || openedDefinition!.Entries.Contains(c.EntryIndex))
                .Select(c => new ParachuteChoice(c.EntryIndex, $"#{c.EntryIndex} {ShortAiText(c.Name)} · {ShortAiText(c.RootName)}")).ToArray();
            ParachuteFlights.SelectedItem = ParachuteFlights.Items.Cast<ParachuteChoice>().FirstOrDefault(c => c.Entry == state.Entry);
            ParachuteWindows.ItemTemplate ??= ParachuteWindowTemplate();
            ParachuteWindows.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var preview = parachuteDraft?.DisplayedPreview;
            ParachuteWindows.ItemsSource = state.Analysis?.Windows.Select((w, i) =>
            {
                double start = parachuteDraft?.Window.Id == w.Id && preview?.Windows.FirstOrDefault(p => p.Id == w.Id) is { } moved ? moved.Start : w.Start;
                return new ParachuteWindowChoice(w.Id, (i + 1).ToString(CultureInfo.InvariantCulture), AnimationSeconds.Format(start), AnimationSeconds.Format(w.Duration));
            }).ToArray() ?? [];
            ParachuteWindows.SelectedItem = ParachuteWindows.Items.Cast<ParachuteWindowChoice>().FirstOrDefault(w => w.Id == state.Selected);
            var selectedWindow = state.Analysis?.Windows.FirstOrDefault(w => w.Id == state.Selected);
            // The start box shows with a selected window: typing proposes a move (Apply / Revert); Move along path picks it in the view.
            ParachuteDraftPanel.Visibility = editable && (parachuteDraft != null || selectedWindow != null) ? Visibility.Visible : Visibility.Collapsed;
            ParachuteStart.IsEnabled = parachuteDraft != null || !state.Stale && selectedWindow is { MoveRefusal: null };
            ParachuteMoveButtons.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
            ParachuteMoveButtons.IsEnabled = parachuteDraft == null && !state.Stale && selectedWindow is { MoveRefusal: null };
            ParachuteFlights.IsEnabled = parachuteDraft == null;
            ParachuteFlights.Visibility = !editable || ParachuteFlights.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            ParachuteWindows.IsEnabled = parachuteDraft == null && editable;
            ParachuteStart.Text = parachuteDraft?.Input ?? (selectedWindow?.Start.ToString("G9", CultureInfo.InvariantCulture) ?? "");
            ParachuteConfirm.IsEnabled = parachuteDraft is { Plan: not null, Preview: not null, Error: null };
            ParachuteRevert.IsEnabled = parachuteDraft != null;
            ParachuteMessage.Text = state.Stale ? state.StaleReason ?? "Drop analysis is stale after an event draft. Apply or discard the draft, then explicitly analyze drop windows again." : parachuteDraft is { } d ? d.Error ?? (d.Preview == null ? "Analyzing proposed release positions…"
                    : (state.Viewport.IsParachutePointerMoving ? "Click a point on the flight path to place the start (right-click or Esc cancels). " : "") + $"Start range {AnimationSeconds.Format(d.Window.MinimumStart)}–{(d.Window.MaximumStart > 3600 ? "end" : AnimationSeconds.Format(d.Window.MaximumStart))}. Apply makes it one undo step.")
                : state.AnalysisRefusal ?? selectedWindow?.MoveRefusal ?? (state.Analysis is { } a ? $"{AnimationSeconds.Format(a.CycleSeconds)} cycle · {a.Releases.Count} eligible releases (orange in the view; starts cyan). Conditions still apply; markers are not landing predictions."
                : state.Discovery.Diagnostics.Count == 0 ? "No supported callback-911 flight programs in this mission." : string.Join("\n", state.Discovery.Diagnostics.Take(3).Select(s => Bounded(s, 300))));
        }
        finally { synchronizingParachutes = false; RefreshDropMoveBanner(); RefreshWorldAnimationRun(); RefreshDropChip(); }
    }
}
