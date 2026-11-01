using System.Globalization;
using System.IO;
using zStudio.Automation;
using System.Windows.Threading;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Sources;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private sealed record GuideBinding(string File, int Ordinal, int Occurrence, string Root, string Proof);
    private sealed record GuideStamp(AssetDocument Map, SceneViewport View, int Generation, Guid Baseline, Guid Scenario, int Entry, int Lod, double Range);
    private sealed class GuideRequest(string map, GuideBinding? binding, int entry)
    {
        public string Map = map;
        public GuideBinding? Binding = binding;
        public int Entry = entry;
        public double? Seconds;
        public GuideStamp? Ready;
        public AnimationPaths? Paths;
        public VtolDropSnapshot? Drops;
        public (string Call, string CallProof, string Stop, string StopProof)? Selection;
        public (float Start, float Duration)[]? SelectionLayout;
        public int SelectionIndex;
        public float? SelectionNewStart;
        public SceneViewport? GhostView;
        public int GhostGeneration = -1;
    }
    private GuideRequest? automaticPaths, automaticDrops;
    private AnimationPackage? guideAddressPackage;
    private readonly Dictionary<int, GuideBinding> guideAddresses = [];
    private DispatcherTimer? automaticGuidesTimer;
    private CancellationTokenSource? automaticGuidesCancellation;
    private Task automaticGuidesWork = Task.CompletedTask;
    private GuideStamp? automaticGuidesPending, automaticGuidesAttempt;
    private DateTime automaticGuidesDue;
    private string automaticGuidesState = "idle", automaticGuidesError = "";
    private GuideStamp? automaticGuideExpected;
    private bool AutomaticGuidesEnabled => ViewModel.Settings.AutoReevaluateAnimationPaths;
    private bool RetainedDropGhostVisible => scene != null && automaticDrops is { GhostView: { } view } d && view == scene
        && d.Ready != GuideNow(animationMode?.Entry ?? d.Entry);
    private bool RetainedPathGhostVisible => scene != null && automaticPaths is { GhostView: { } view } p && view == scene
        && p.Ready != GuideNow(animationMode?.Entry ?? p.Entry);
    private string? AutomaticGuideMessage => !AutomaticGuidesEnabled ? null : automaticGuidesState switch
    {
        "waiting" => "Waiting for the current edit or scene to finish. Previous guides are out of date.",
        "updating" => "Updating enabled paths and drop windows… Previous guides are out of date.",
        "failed" => "Automatic reevaluation failed: " + automaticGuidesError + " Use Evaluate paths or Analyze again to retry.",
        _ => null,
    };
    private const string AutomaticGuideTip = "Refresh enabled motion paths and VTOL drop markers after changes. Waits for unfinished edits; never starts a hidden or unrequested analysis.";

    private void EnsureAutomaticGuides()
    {
        if (automaticGuidesTimer != null) return;
        automaticGuidesTimer = new(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => CheckAutomaticGuides(), Dispatcher);
        Closed += (_, _) => { automaticGuidesTimer.Stop(); automaticGuidesCancellation?.Cancel(); };
    }
    private void SetAutomaticGuides(bool value)
    {
        ViewModel.Settings.AutoReevaluateAnimationPaths = value;
        ViewModel.Settings.Save();
        EnsureAutomaticGuides(); ResetAutomaticGuideWork();
        RefreshPathsChip(); UpdateParachutePanel();
    }
    private void ResetAutomaticGuideWork()
    {
        automaticGuidesCancellation?.Cancel();
        automaticGuidesPending = automaticGuidesAttempt = null;
        automaticGuidesError = ""; automaticGuidesState = "idle";
    }
    private void ForgetAutomaticGuides(bool paths = true, bool drops = true)
    {
        ResetAutomaticGuideWork();
        if (paths) { automaticPaths = null; scene?.ClearAnimationPaths("automatic-stale"); }
        if (drops) { automaticDrops = null; scene?.SetParachuteOverlay([], []); }
        if (paths && drops) { guideAddressPackage = null; guideAddresses.Clear(); }
    }
    private GuideBinding? GuideIdentity(DocumentModel document, int entry)
    {
        if (document.Document is not SourceSceneSnapshot { AnimationProvenance: { } provenance, Animations: { } package }) return null;
        if (guideAddressPackage != package)
        {
            guideAddresses.Clear(); guideAddressPackage = package;
            foreach (var group in package.Entries.Select(e => (Entry: e, Source: provenance.FindEntry(e.Index)))
                .Where(r => r.Source?.AddressProof != null).GroupBy(r => (r.Source!.File, r.Source.DefinitionOrdinal)))
            {
                var definitions = group.Select(r => r.Source!.Definition).Distinct().Order().ToArray();
                foreach (var row in group) guideAddresses[row.Entry.Index] = new(group.Key.File, group.Key.DefinitionOrdinal,
                    Array.BinarySearch(definitions, row.Source!.Definition), row.Entry.RootName, row.Source.AddressProof!);
            }
        }
        return guideAddresses.GetValueOrDefault(entry);
    }
    private GuideRequest NewGuideRequest(DocumentModel document, int entry) => new(document.Path, GuideIdentity(document, entry), entry);
    private int ResolveGuide(GuideRequest request, DocumentModel document)
    {
        if (request.Binding == null) return request.Entry;
        _ = GuideIdentity(document, request.Entry);
        var matches = guideAddresses.Where(p => p.Value == request.Binding).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Key : -1;
    }
    private GuideStamp? GuideNow(int entry)
    {
        if (shownDocument == null || scene == null) return null;
        return new(shownDocument.Document, scene, scene.SceneGeneration, worldAnimationBaseline, worldAnimationScenarioEpoch,
            entry, Math.Max(0, LodCombo.SelectedIndex), worldAnimationSession?.State.RangeEnd ?? worldAnimationRangeEnd);
    }
    private void RememberPathRequest(DocumentModel document, int entry, double? seconds)
    {
        ResetAutomaticGuideWork();
        automaticPaths = NewGuideRequest(document, entry); automaticPaths.Seconds = seconds;
    }
    private void RememberDropRequest(DocumentModel document, int entry)
    {
        ResetAutomaticGuideWork(); automaticDrops = NewGuideRequest(document, entry);
    }
    private void RememberAutomaticDropSelection(float? newStart = null)
    {
        if (automaticDrops == null || parachutes is not { Analysis: { } analysis, Selected: { } selected } state
            || state.World.Document is not SourceSceneSnapshot { AnimationProvenance: { } source }) return;
        var window = analysis.Windows.FirstOrDefault(w => w.Id == selected);
        if (window != null && source.FindEvent(window.CallEventId) is { RecordId: { } call, AddressProof: { } callProof }
            && source.FindEvent(window.StopEventId) is { RecordId: { } stop, AddressProof: { } stopProof })
        {
            if (newStart == null && automaticDrops.Selection == (call, callProof, stop, stopProof) && automaticDrops.SelectionLayout != null) return;
            automaticDrops.Selection = (call, callProof, stop, stopProof);
            automaticDrops.SelectionLayout = analysis.Windows.Select(w => (w.Start, w.Duration)).ToArray();
            automaticDrops.SelectionIndex = analysis.Windows.ToList().IndexOf(window);
            automaticDrops.SelectionNewStart = newStart;
        }
    }
    private bool AutomaticGuidesBlocked => HasGuiDraft || sourceWorkspaceBusy || worldAnimationRebuilding || retiringScene != null
        || shownDocument?.SourceWorld?.IsRebuilding == true || scene == null || scene.IsFlyActive || scene.IsPickupDragging
        || scene.HasTransformDraft || scene.AiPathEditingNetwork != null || scene.TerrainBrushActive || scene.IsZonePainting
        || worldAnimationMeasuring is { IsCompleted: false };

    private void RequireAutomaticGuidePublication()
    {
        if (!AutomaticGuidesEnabled || automaticGuideExpected is not { } expected || GuideNow(expected.Entry) != expected || AutomaticGuidesBlocked)
            throw new StudioCommandException("context_changed", "The automatic path request changed during analysis.");
    }
    private void AutomaticBindingChanged(DocumentModel document, int entry)
    {
        ResetAutomaticGuideWork();
        foreach (var request in new[] { automaticPaths, automaticDrops })
            if (request != null) { request.Binding = GuideIdentity(document, entry); request.Entry = entry; request.Ready = null; request.Selection = null; }
    }

    // A single observer retains visibility intent while ordinary teardown replaces caches and viewports.
    private void CheckAutomaticGuides()
    {
        if (!AutomaticGuidesEnabled || shutdownToken.IsCancellationRequested) return;
        if (automaticPaths == null && automaticDrops == null) return;
        if (shownDocument == null || sourceWorkspaceBusy || worldAnimationRebuilding || retiringScene != null) return;
        if (automaticPaths is { } path && !string.Equals(path.Map, shownDocument.Path, StringComparison.OrdinalIgnoreCase)) automaticPaths = null;
        if (automaticDrops is { } drop && !string.Equals(drop.Map, shownDocument.Path, StringComparison.OrdinalIgnoreCase)) automaticDrops = null;
        int entry = animationMode?.Entry ?? automaticDrops?.Entry ?? -1;
        var stamp = GuideNow(entry);
        if (stamp == null || automaticPaths == null && automaticDrops == null) return;
        bool needPaths = automaticPaths != null && automaticPaths.Ready != stamp;
        bool needDrops = automaticDrops != null && automaticDrops.Ready != stamp;
        if (!needPaths && !needDrops) { automaticGuidesState = "idle"; return; }
        if (automaticGuidesPending != stamp)
        {
            automaticGuidesCancellation?.Cancel(); automaticGuidesPending = stamp;
            automaticGuidesDue = DateTime.UtcNow.AddMilliseconds(250);
            automaticGuidesError = "";
            if (automaticPaths != null) automaticPaths.GhostView = null;
            if (automaticDrops != null) automaticDrops.GhostView = null;
        }
        ShowAutomaticGhosts();
        if (AutomaticGuidesBlocked || automaticGuidesWork.IsCompleted && (sourcePathLoading != null || parachuteLoading != null))
        {
            if (!automaticGuidesWork.IsCompleted) { automaticGuidesCancellation?.Cancel(); automaticGuidesAttempt = null; }
            automaticGuidesState = "waiting"; RefreshPathsChip(); UpdateParachutePanel(); return;
        }
        if (!automaticGuidesWork.IsCompleted || DateTime.UtcNow < automaticGuidesDue || automaticGuidesAttempt == stamp) return;
        automaticGuidesAttempt = stamp;
        automaticGuidesWork = RefreshAutomaticGuidesAsync(stamp, needPaths, needDrops);
    }
    private void ShowAutomaticGhosts()
    {
        if (scene == null) return;
        if (automaticPaths is { Paths: { } paths } p && p.Ready != GuideNow(animationMode?.Entry ?? p.Entry)
            && (p.GhostView != scene || p.GhostGeneration != scene.SceneGeneration))
        {
            scene.SetAnimationPaths("automatic-stale", paths.Paths, selected: false);
            p.GhostView = scene; p.GhostGeneration = scene.SceneGeneration;
        }
        if (automaticDrops is { Drops: { } drops } d && d.Ready != GuideNow(animationMode?.Entry ?? d.Entry)
            && (d.GhostView != scene || d.GhostGeneration != scene.SceneGeneration))
        {
            scene.SetParachuteOverlay(drops.Samples.Select(p => new ParachutePathPoint(p.Time, p.Position)).ToArray(),
                drops.Windows.Select(w => new ParachuteWindowOverlay(w.Id, w.EffectiveStart,
                    drops.Releases.Where(r => r.WindowId == w.Id).Select(r => new ParachuteRelease(r.Time, r.Position)).ToArray())).ToArray(), stale: true);
            d.GhostView = scene; d.GhostGeneration = scene.SceneGeneration;
        }
    }
    private async Task RefreshAutomaticGuidesAsync(GuideStamp stamp, bool paths, bool drops)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        automaticGuidesCancellation = cancellation;
        automaticGuideExpected = stamp;
        automaticGuidesState = "updating";
        try
        {
            var document = shownDocument!;
            if (drops && automaticDrops is { } d)
            {
                int entry = ResolveGuide(d, document);
                if (entry < 0) throw new InvalidDataException("The enabled drop flight is no longer available.");
                await ShowParachutesAsync(cancellation.Token, entry, automatic: true);
                if (parachutes is { Analysis: { } analysis } state)
                {
                    RequireAutomaticGuidePublication(); d.Drops = analysis; d.Ready = stamp;
                    if (d.Selection is { } selected && document.Document is SourceSceneSnapshot { AnimationProvenance: { } source })
                    {
                        var matches = analysis.Windows.Where(w => source.FindEvent(w.CallEventId)?.RecordId == selected.Call
                            && source.FindEvent(w.CallEventId)?.AddressProof == selected.CallProof
                            && source.FindEvent(w.StopEventId)?.RecordId == selected.Stop
                            && source.FindEvent(w.StopEventId)?.AddressProof == selected.StopProof).Take(2).ToArray();
                        bool sameLayout = d.SelectionLayout is { } layout && layout.Length == analysis.Windows.Count
                            && analysis.Windows.Select((w, i) => w.Duration == layout[i].Duration
                                && (w.Start == layout[i].Start || i == d.SelectionIndex && w.Start == d.SelectionNewStart)).All(v => v);
                        if (matches.Length == 1 && sameLayout) { state.Selected = matches[0].Id; scene!.SelectParachuteWindow(state.Selected, notify: false); UpdateParachutePanel(); }
                        else { d.Selection = null; d.SelectionLayout = null; d.SelectionNewStart = null; }
                    }
                }
            }
            if (paths && automaticPaths is { } p)
            {
                int entry = ResolveGuide(p, document);
                int definition = (document.Document as SourceSceneSnapshot)?.AnimationProvenance?.FindEntry(entry)?.Definition ?? -1;
                if (entry < 0 || definition < 0) throw new InvalidDataException("The enabled motion binding is no longer available.");
                await AnalyzeSourceAnimationAsync(document, definition, entry, p.Seconds, cancellation.Token, automatic: true);
                RequireAutomaticGuidePublication(); p.Paths = sourceAnimationPaths[definition].Analysis; p.Ready = stamp;
                scene!.ClearAnimationPaths("automatic-stale");
            }
            automaticGuidesState = "idle";
        }
        catch (OperationCanceledException) { }
        catch (StudioCommandException) when (cancellation.IsCancellationRequested || GuideNow(stamp.Entry) != stamp) { }
        catch (StudioCommandException) when (AutomaticGuidesBlocked)
        { automaticGuidesAttempt = null; automaticGuidesState = "waiting"; }
        catch (Exception ex) when (ex is StudioCommandException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            automaticGuidesState = "failed"; automaticGuidesError = Bounded(ex.Message, 512);
            TransportNote("Automatic path reevaluation: " + automaticGuidesError);
        }
        finally
        {
            if (automaticGuidesCancellation == cancellation) automaticGuidesCancellation = null;
            RefreshPathsChip(); UpdateParachutePanel();
        }
    }
}
