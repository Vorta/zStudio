using System.Globalization;
using zStudio.Automation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private async Task AnalyzeWorldDropWindowsAsync(DocumentModel document, int definition, int entry, CancellationToken token)
    {
        RequireWorldAnimationTarget(document, definition, entry);
        await ShowParachutesAsync(token, entry);
        RefreshWorldAnimationDropRelationships();
        RefreshWorldAnimationRun();
    }

    private void RefreshWorldAnimationDropRelationships()
    {
        if (sectionCard == null || parachutes is not { Analysis: { } analysis } state ||
            sectionCard.Document != state.World || state.World.Document is not SourceSceneSnapshot { AnimationProvenance: { } provenance }) return;
        var records = analysis.Windows.SelectMany(window => new[] { window.CallEventId, window.StopEventId })
            .Select(id => provenance.FindEvent(id))
            .Where(item => item?.RecordId != null && item.Definition == (sectionCard.Entry as DefinitionEntry)?.Definition.Index)
            .Select(item => item!.RecordId!);
        sectionCard.SetAnimationWindowRecords(records);
    }

    // Generic event authoring deliberately does not inherit the verified-window restrictions.
    // Its old analysis remains visible only as stale evidence and cannot be moved or accepted.
    private void InvalidateWorldAnimationDropsForEdit(string? reason = null)
    {
        if (parachutes is not { } state || parachuteDraft != null) return;
        ++parachuteGeneration; parachuteLoading?.Cancel();
        state.Stale = true; state.StaleReason = reason;
        state.Viewport.SetParachuteOverlay([], []);
        UpdateParachutePanel();
    }
}
