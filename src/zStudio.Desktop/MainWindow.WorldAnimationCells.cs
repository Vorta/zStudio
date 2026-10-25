using System.Globalization;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private int worldAnimationCellGeneration;
    private long worldAnimationEditingSeek;
    private async Task SeekWorldAnimationFrameAsync(double seconds, CancellationToken token = default)
    {
        long request = ++worldAnimationEditingSeek;
        var target = WorldAnimationTargetNow;
        try
        {
            RequireNoDrafts();
            WorldTimeline.SetRequestedTime(seconds);
            await WorldAnimationTransportAsync("seek", seconds, token, editingFrame: true);
            if (request != worldAnimationEditingSeek || target != WorldAnimationTargetNow)
                throw new StudioCommandException("context_changed", "The editing position changed during the seek.");
            WorldTimeline.SetRequestedTime(seconds);
        }
        catch { if (request == worldAnimationEditingSeek) WorldTimeline.SetRequestedTime(null); throw; }
    }
    private async Task<SourceAnimationEdits.SourceContext> ReadAnimationContextAsync(DocumentModel document, int definition,
        string record, double? scriptTime, CancellationToken token, string? boundScript = null)
    {
        long revision = document.Revision;
        var workspace = document.SourceWorld!.Workspace.BeginPreparedEdit().Workspace;
        var context = await Task.Run(() => SourceAnimationEdits.ReadContext(new AnimationProjectFiles(workspace, token),
            SourceBuilder.AnimationRoot(document.SourceWorld.Mission), definition, record, scriptTime, token, boundScript), token);
        if (document != shownDocument || document.IsDisposed || revision != document.Revision)
            throw new StudioCommandException("context_changed", "The source map changed while reading frame context.");
        return context;
    }

    private async Task<object> WorldAnimationCellAsync(string rowId, string markId, bool seek, long? occurrence = null,
        int offset = 0, CancellationToken token = default)
    {
        RequireNoDrafts(); int generation = ++worldAnimationCellGeneration;
        if (offset < 0) throw new StudioCommandException("invalid_argument", "Use a nonnegative page offset.");
        string? expectedRecord = null; int expectedDefinition = -1;
        var row = worldAnimationSourceRows.Concat(worldAnimationRunRows).FirstOrDefault(r => r.Id == rowId)
            ?? throw new StudioCommandException("stale_record", "The timeline row is unavailable.");
        var mark = row.Marks.FirstOrDefault(m => m.Id == markId)
            ?? throw new StudioCommandException("stale_record", "The frame item is unavailable.");
        if (row.IsWindowClock) { await SelectWorldAnimationDropAsync(row, token); return new { row = rowId, mark = markId }; }
        var target = WorldAnimationTargetNow ?? throw new StudioCommandException("not_ready", "Choose an animation binding.");
        var run = worldAnimationSession?.RunId;
        var current = worldAnimationSession?.CurrentFrame;
        var source = worldAnimationRowSources[rowId];
        var observed = mark.Occurrence is long ordinal ? (current ?? worldAnimationSession?.LastFrame)?.Trace.FirstOrDefault(t => t.Occurrence == ordinal) : null;
        int entry = observed?.Entry ?? source.Entry;
        Guid sequence = observed?.Sequence ?? source.Sequence, ev = observed?.Event ?? source.Event;
        string path = observed?.InstancePath ?? source.Path;
        await WorldAnimationSelectSourceAsync(sequence, ev, token, entry, path, preserveTimeline: true);
        Check();
        var provenance = GetWorldAnimationStructurePlan().Events(entry, sequence, 0, 100).Rows.FirstOrDefault(e => e.Id == ev)?.Source;
        // Events beyond the first structural page have the same exact compiler provenance.
        provenance ??= (target.Document.Document as SourceSceneSnapshot)?.AnimationProvenance?.FindEvent(ev);
        expectedRecord = provenance?.RecordId; expectedDefinition = provenance?.Definition ?? -1;
        SourceAnimationEdits.SourceContext? context = provenance?.RecordId is string record
            ? await ReadAnimationContextAsync(target.Document, provenance.Definition, record, mark.KeyIndex.HasValue ? mark.Start : null, token, provenance.Script) : null;
        Check();
        string detail = $"Frame {AnimationTimelineFrames.Frame(mark.Start):0.######} · {mark.Start:G9} s · {row.TimingLabel}";
        if (mark.KeyIndex is int keyIndex)
        {
            var key = GetWorldAnimationStructurePlan().Keyframes(entry, sequence, ev, keyIndex, 1, token).Rows.Single();
            detail += $"\nRead only · position {key.Position} · velocity {key.Velocity}\nRotation {key.Rotation} · spin {key.Spin}\nScale {key.Scale} · growth {key.Growth}";
        }
        var matches = current?.Trace.Where(t => t.Entry == entry && t.Sequence == sequence && t.Event == ev && t.InstancePath == path
            && (mark.Occurrence == null || t.Occurrence == mark.Occurrence)).ToArray() ?? [];
        var selected = occurrence.HasValue ? matches.FirstOrDefault(t => t.Occurrence == occurrence) : matches.Length == 1 ? matches[0] : null;
        if (occurrence.HasValue && selected == null) throw new StudioCommandException("stale_record", "The selected occurrence is no longer current.");
        double? requested = selected == null ? null : mark.KeyIndex.HasValue ? selected.Start + mark.Start : selected.Start;
        if (requested.HasValue && (requested < selected!.Start || requested > (selected.End ?? current!.Time) + 1e-6)) requested = null;
        string? reason = requested == null ? matches.Length > 1 ? "Choose an execution occurrence to seek." : "No current execution time is available. Inspecting source only." : null;
        WorldTimeline.SelectRow(rowId, markId);
        if (seek && requested is double time)
        {
            Check();
            if (run != worldAnimationSession?.RunId) throw new StudioCommandException("context_changed", "The evaluated run changed.");
            await SeekWorldAnimationFrameAsync(time, token);
            Check();
        }
        var page = matches.Skip(offset).Take(100).Select(t => (Id: t.Occurrence, Label: $"Seek occurrence {t.Occurrence} · {t.Start:0.######} s")).ToArray();
        Action<long> choose = id => _ = ReportUi(async () => { if (run != worldAnimationSession?.RunId) throw new StudioCommandException("context_changed", "The evaluated run changed."); await WorldAnimationCellAsync(rowId, markId, true, id, token: CancellationToken.None); });
        Action? more = offset + page.Length < matches.Length ? () => _ = ReportUi(async () => { if (run != worldAnimationSession?.RunId) throw new StudioCommandException("context_changed", "The evaluated run changed."); await WorldAnimationCellAsync(rowId, markId, false, offset: offset + 100); }) : null;
        SectionCard().SetAnimationContext(context, detail + (reason == null ? "" : "\n" + reason), matches.Length > 1 ? page : [], choose, more);
        return new { row = rowId, mark = markId, frame = AnimationTimelineFrames.Frame(mark.Start), seconds = mark.Start,
            context, requestedSeconds = requested, actualSeconds = worldAnimationSession?.State.Time, reason,
            occurrences = page.Select(p => new { occurrence = p.Id, label = p.Label }), total = matches.Length,
            nextOffset = offset + page.Length < matches.Length ? (int?)(offset + page.Length) : null };
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (generation != worldAnimationCellGeneration || target != WorldAnimationTargetNow || target.Document != shownDocument || target.Revision != target.Document.Revision)
                throw new StudioCommandException("context_changed", "The frame selection changed.");
            if (expectedRecord != null && !SectionCard().IsAnimationRecord(target.Document, expectedDefinition, expectedRecord))
                throw new StudioCommandException("context_changed", "The inspected event changed.");
        }
    }

    private async Task ProposeWorldAnimationFrameAsync(string rowId, double frame, CancellationToken token)
    {
        double seconds = AnimationTimelineFrames.Seconds(frame);
        if (!double.IsFinite(frame) || !double.IsFinite(seconds)) throw new StudioCommandException("invalid_argument", "Use a finite frame number.");
        RequireNoDrafts();
        var row = worldAnimationSourceRows.FirstOrDefault(r => r.Id == rowId);
        if (row?.CanEditTiming != true || !worldAnimationRowSources.TryGetValue(rowId, out var source))
            throw new StudioCommandException("read_only", "This row has no editable event timing.");
        var target = WorldAnimationTargetNow ?? throw new StudioCommandException("not_ready", "Choose an animation binding.");
        int generation = ++worldAnimationCellGeneration;
        await WorldAnimationSelectSourceAsync(source.Sequence, source.Event, token, source.Entry, source.Path, preserveTimeline: true);
        token.ThrowIfCancellationRequested();
        if (generation != worldAnimationCellGeneration || target != WorldAnimationTargetNow || target.Document != shownDocument || target.Revision != target.Document.Revision)
            throw new StudioCommandException("context_changed", "The event selection changed.");
        var provenance = (target.Document.Document as SourceSceneSnapshot)?.AnimationProvenance?.FindEvent(source.Event)
            ?? throw new StudioCommandException("stale_record", "The event source is unavailable.");
        if (provenance.TimingFieldId == null) throw new StudioCommandException("read_only", "The event timing is unavailable.");
        var ev = target.Document.Document.Animations!.Entries[source.Entry].AllSequences.Single(s => s.Id == source.Sequence).Events.Single(e => e.Id == source.Event);
        string mode = ev.StartMode switch { 2 => "SEQUENCE_OFFSET", 3 => "EVENT_OFFSET", _ => "ANIMATION_OFFSET" };
        SectionCard().SetAnimationChanges([new(provenance.TimingFieldId, [mode, seconds.ToString("R", CultureInfo.InvariantCulture)])]);
        WorldTimeline.SelectRow(rowId); SectionCard().FocusAnimationTiming();
    }
}
