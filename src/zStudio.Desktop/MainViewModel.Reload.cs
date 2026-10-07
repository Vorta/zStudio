using System.IO;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public sealed partial class MainViewModel
{
    internal Action<DocumentModel>? ValidateReload { get; set; }
    /// <summary>Rebuilds a source world from the project on disk (see MainWindow.SourceWorlds).</summary>
    internal Func<DocumentModel, bool, CancellationToken, Task<DocumentModel>>? ReloadSourceWorld { get; set; }

    internal async Task<DocumentModel> ReloadDocumentAsync(DocumentModel original, long revision, bool discardAccepted = false, CancellationToken cancellationToken = default)
    {
        if (original.SourceWorld != null && ReloadSourceWorld is { } rebuild) return await rebuild(original, discardAccepted, cancellationToken);
        long generation = ++navigationGeneration;
        var selected = SelectedDocument;
        string reloadPath = original.ContentEdits?.TargetPath(original.Path) ?? original.ResourceEdits?.TargetPath ?? original.ModelEdits?.TargetPath(original.Path) ?? original.Path;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(workspace.Token, original.Lifetime.Token, cancellationToken);
        void Validate()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (original.IsDisposed || !Documents.Contains(original) || generation != navigationGeneration || SelectedDocument != selected)
                throw new StudioCommandException("context_changed", "Reload was superseded; the existing document was retained where still open.");
            if (original.Revision != revision) throw new StudioCommandException("revision_conflict", "The document changed during reload; its current edits were retained.");
            if (!discardAccepted && original.IsDirty) throw new StudioCommandException("unsaved_changes", "Save or explicitly discard edits before reloading.");
            if (Documents.Any(d => d != original && d.Path.Equals(reloadPath, StringComparison.OrdinalIgnoreCase)))
                throw new StudioCommandException("destination_open", "The saved destination is already open. Close that document before reloading this saved copy.");
            ValidateReload?.Invoke(original);
        }
        Validate();
        Status = "Reloading " + Path.GetFileName(reloadPath) + "…";
        ZbdDocument loaded;
        try
        {
            loaded = await LoadDocumentAsync(reloadPath, request.Token).WaitAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (loaded.Probe.Recognition is Recognition.Malformed or Recognition.UnsupportedVersion ||
                original.Document.Probe.Recognition == Recognition.Supported && loaded.Probe.Recognition != Recognition.Supported ||
                loaded.Diagnostics.Any(d => d.Severity == "Error" && d.Message.StartsWith("Parsing stopped:", StringComparison.Ordinal)))
                throw new InvalidDataException("The replacement file could not be fully parsed. The existing document was retained.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new StudioCommandException("context_changed", "The document or workspace changed during reload."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { throw new StudioCommandException("open_failed", "Could not reload; the existing document was retained. " + ex.Message); }

        Validate();
        var replacement = new DocumentModel(loaded) { Query = original.Query, KindFilter = original.KindFilter };
        replacement.AttachResolver(Resolver);
        var asset = original.SelectedAsset?.Record;
        replacement.SelectedAsset = asset == null ? null : replacement.Assets.FirstOrDefault(a => a.Record.Kind == asset.Kind && a.Record.Index == asset.Index);
        // Parsing and every fallible asynchronous guard have finished. Publishing
        // the accepted document is the commit boundary; preview follows the normal
        // selection lifecycle and can independently report unavailable content.
        int index = Documents.IndexOf(original);
        Documents[index] = replacement;
        if (selected == original) SelectedDocument = replacement;
        original.Dispose();
        foreach (var diagnostic in loaded.Diagnostics) AddProblem(diagnostic.Message, diagnostic.Severity, loaded.Path, diagnostic.AssetIndex, diagnostic.Offset);
        Status = replacement.Description;
        return replacement;
    }

    private async Task ReloadSelectedAsync()
    {
        // Committing pending input first: it can rebuild the selected source world, which is then the one reloaded.
        if (ResolveDraftsAsync != null && !await ResolveDraftsAsync()) return;
        CloseDecisionsStarting?.Invoke();
        if (SelectedDocument is not { } original) return;
        // A source world keeps its pending edits across a rebuild unless its own files changed on disk.
        bool needsDecision = original.SourceWorld == null;
        if (original.SourceWorld is { } world)
        {
            var changed = await Task.Run(() => world.Workspace.ExternalChanges(original.Lifetime.Token));
            if (original.IsDisposed || SelectedDocument != original) return;
            needsDecision = changed.Any(world.Workspace.IsFileDirty);
        }
        if (needsDecision && !await CanRemoveAsync(original)) return;
        if (original.IsDisposed || SelectedDocument != original) return;
        // CanRemoveAsync may have saved edits or resolved input. That accepted
        // state is the snapshot; further edits during parsing reject publication.
        long generation = navigationGeneration + 1;
        try { await ReloadDocumentAsync(original, original.Revision, discardAccepted: true); }
        catch (StudioCommandException ex)
        {
            // A source world's reload does not take a navigation generation; its failure is always the request's to report.
            bool OwnsRequest() => original.SourceWorld != null ? ex.Code != "context_changed" : generation == navigationGeneration && !original.IsDisposed && SelectedDocument == original;
            if (OwnsRequest()) AddProblem(ex.Message, file: original.Path);
            if (OwnsRequest()) Status = ex.Message;
        }
    }
}
