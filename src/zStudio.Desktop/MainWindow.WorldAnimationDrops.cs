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
        RefreshWorldAnimationTimeline();
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

    private IReadOnlyList<WorldAnimationTimelineRow> WorldAnimationDropRows(int targetEntry)
    {
        if (parachutes is not { Analysis: { } analysis } state || state.Entry != targetEntry || state.Stale ||
            state.World != shownDocument || state.World.Revision != state.WorldRevision ||
            state.Viewport.SceneGeneration != state.SceneGeneration) return [];
        var shown = parachuteDraft?.DisplayedPreview ?? analysis;
        return shown.Windows.Select((window, index) => new WorldAnimationTimelineRow(
            $"drop:{state.Id}:{window.Id:D}", $"Drop window {index + 1}")
        {
            Depth = 1,
            IsWindowClock = true,
            WindowClockEnd = shown.CycleSeconds,
            WindowStartThreshold = window.Start,
            CanMoveWindow = window.MoveRefusal == null && state.World.SourceWorld != null && parachuteDraft == null,
            WindowMoveReason = window.MoveRefusal ?? "Move changes the CALL threshold after validation; STOP duration is preserved.",
            TimingLabel = $"SEQUENCE_OFFSET {window.Start.ToString("G9", CultureInfo.InvariantCulture)} s",
            Detail = $"Verified flight clock · {window.Duration.ToString("G9", CultureInfo.InvariantCulture)} s window" +
                (window.Conditional ? " · PLAYER_RANGE conditional; markers are not landing predictions" : " · markers are not landing predictions"),
            Marks = [new(window.Id.ToString("D"), WorldAnimationTimelineMarkKind.Window,
                window.EffectiveStart, window.EffectiveEnd, "Move drop window")]
        }).ToArray();
    }

    private async Task MoveWorldAnimationDropAsync(WorldAnimationTimelineRow row, double start, CancellationToken token)
    {
        if (!double.IsFinite(start) || start < -float.MaxValue || start > float.MaxValue)
            throw new StudioCommandException("invalid_argument", "Enter a finite drop-window time.");
        await SelectWorldAnimationDropAsync(row, token);
        BeginParachute();
        var draft = RequireParachuteDraft();
        draft.Input = ((float)start).ToString("R", CultureInfo.InvariantCulture);
        QueueParachutePreview(draft, token: token);
        await draft.Work;
        UpdateParachutePanel();
        // The normal Properties Confirm/Cancel controls own the single prepared transaction.
    }

    private async Task SelectWorldAnimationDropAsync(WorldAnimationTimelineRow row, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = RequireParachutes(edit: true);
        string prefix = $"drop:{state.Id}:";
        if (!row.Id.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParse(row.Id[prefix.Length..], out Guid id))
            throw new StudioCommandException("stale_snapshot", "Analyze this flight's drop windows again.");
        await SelectParachuteAsync(id);
        token.ThrowIfCancellationRequested();
    }
}
