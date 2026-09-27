using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using System.IO;

namespace Recoil.Zbd.Desktop;

public sealed partial class DocumentModel
{
    public TextureEditSession? TextureEdits { get; private set; }
    public ScriptEditSession? ScriptEdits { get; private set; }
    public ContentEditSession? ContentEdits => (ContentEditSession?)ScriptEdits ?? TextureEdits;
    public event Action? ContentEditsChanged;
    private ZbdDocument? contentMirror;
    private long mirrorGeneration;
    internal Task ContentMirrorWork { get; private set; } = Task.CompletedTask;
    public bool IsContentMirror => contentMirror != null;
    private void InitializeContentEdits(ZbdDocument doc)
    {
        if (doc.Diagnostics.Any(d => d.Severity == "Error")) return;
        if (doc.Probe.Family == FormatFamily.TexturePack && doc.Probe.Version == 1) TextureEdits = new(doc);
        if (doc.Scripts != null) ScriptEdits = new(doc);
        if (ContentEdits is not { } edits) return;
        edits.BeforeEdit += ClaimResourcePaths;
        edits.Changed += () =>
        {
            Revision++;
            if (edits.HasAcceptedEdits) workspaceResolver?.SetWorkspaceSnapshots(SessionId, edits.PublishedDocuments);
            RebuildContentAssets(); InvalidateMissionContext();
            OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(IsDirty)); ContentEditsChanged?.Invoke();
        };
        RebuildContentAssets();
    }
    private void ContentSnapshotsChanged()
    {
        if (IsDisposed || TextureEdits == null) return;
        var next = workspaceResolver?.WorkspaceSnapshot(Path, SessionId);
        if (ReferenceEquals(next, contentMirror)) return;
        long generation = ++mirrorGeneration;
        if (next == null && contentMirror != null)
        {
            // The owner may have saved, saved elsewhere, or discarded after an earlier save.
            // Read disk again; keep this view read-only until explicit Reload establishes a baseline.
            ContentMirrorWork = RestoreContentMirrorAsync(generation); return;
        }
        contentMirror = next; Revision++; RebuildContentAssets(); ContentEditsChanged?.Invoke();
    }
    private async Task RestoreContentMirrorAsync(long generation)
    {
        try
        {
            var disk = await FormatRegistry.Default.OpenAsync(Path, Lifetime.Token);
            if (IsDisposed || generation != mirrorGeneration) return;
            contentMirror = disk.Bytes.Span.SequenceEqual(Document.Bytes.Span) ? null : disk;
            IsStale = contentMirror != null; Revision++; RebuildContentAssets(); ContentEditsChanged?.Invoke();
        }
        catch (OperationCanceledException) when (IsDisposed) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { if (!IsDisposed && generation == mirrorGeneration) { IsStale = true; ContentEditsChanged?.Invoke(); } }
    }
    private void RebuildContentAssets()
    {
        var selectedId = SelectedAsset?.ResourceId; var selected = SelectedAsset?.Record.Id;
        var oldIndex = SelectedAsset?.Index ?? 0;
        if (ScriptEdits != null)
        {
            Assets.Clear();
            foreach (var record in PreviewDocument.Assets) Assets.Add(new(record) { ResourceId = ScriptEdits.Package.Entries[record.Index].Id });
            SelectedAsset = Assets.FirstOrDefault(a => a.ResourceId == selectedId) ?? Assets.ElementAtOrDefault(Math.Clamp(oldIndex, 0, Math.Max(0, Assets.Count - 1)));
        }
        else
        {
            var existing = Assets.ToDictionary(a => a.Record.Id);
            var records = PreviewDocument.Assets;
            foreach (var row in Assets.Where(a => !records.Any(r => r.Id == a.Record.Id)).ToArray()) Assets.Remove(row);
            foreach (var record in records)
            {
                if (existing.TryGetValue(record.Id, out var row)) { row.UpdateRecord(record); row.Thumbnail = null; row.ThumbnailRequested = false; }
                else Assets.Add(new(record));
            }
            SelectedAsset = Assets.FirstOrDefault(a => a.Record.Id == selected) ?? (selected == null ? null : Assets.FirstOrDefault());
        }
        Kinds = ["All types", .. Assets.Select(a => a.Kind).Distinct().Order()]; OnPropertyChanged(nameof(Kinds));
        if (!Kinds.Contains(KindFilter)) KindFilter = "All types";
        OnPropertyChanged(nameof(Description)); FilteredAssets.Refresh();
    }
}
