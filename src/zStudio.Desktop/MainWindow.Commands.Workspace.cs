using System.IO;
using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private static readonly StudioParameter[] PageParameters = [P("offset", "integer", "Zero-based result offset."), P("limit", "integer", "Page size, 1–200; default 100."), P("query", "string", "Case-insensitive name/path or displayed-text filter, applied before pagination.")];
    internal static StudioResult Page<T>(IEnumerable<T> source, JsonObject a, Func<T, string>? search = null, Func<T, object>? project = null, Func<T, string, bool>? matches = null, Func<T, long>? maximumRowBytes = null)
    {
        int offset = Int(a, "offset"), limit = Int(a, "limit", 100);
        if (offset < 0 || limit is < 1 or > 200) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–200.");
        if (Text(a, "query") is { Length: > 0 } query)
        {
            if (matches != null) source = source.Where(item => matches(item, query));
            else if (search != null) source = source.Where(item => search(item).Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        int total = 0; List<object?> items = []; long bytes = 0; bool full = false;
        foreach (var item in source)
        {
            if (total >= offset && items.Count < limit && !full)
            {
                long cost = maximumRowBytes?.Invoke(item) ?? 0;
                if (cost < 0 || cost > InspectionResultBudget.PageBytes) throw new StudioCommandException("too_large", "A result row exceeds the supported page size.");
                if (bytes + cost > InspectionResultBudget.PageBytes && items.Count > 0) full = true;
                else { items.Add(project == null ? item : project(item)); bytes += cost; }
            }
            total++;
        }
        return Result(new { total, offset, nextOffset = (long)offset + items.Count < total ? (int?)(offset + items.Count) : null, items });
    }
    /// <summary>A positional page of a sequence whose size is known: only the returned rows are constructed.</summary>
    internal static StudioResult PageRange(int total, JsonObject a, Func<int, object> project)
    {
        int offset = Int(a, "offset"), limit = Int(a, "limit", 100);
        if (offset < 0 || limit is < 1 or > 200) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–200.");
        var items = Enumerable.Range(offset, (int)Math.Clamp((long)total - offset, 0, limit)).Select(project).ToArray();
        return Result(new { total, offset, nextOffset = (long)offset + limit < total ? (int?)(offset + limit) : null, items });
    }
    private static AssetRecord TargetAsset(DocumentModel doc, JsonObject a)
    {
        if (!a.ContainsKey("index") || !Enum.TryParse<AssetKind>(Text(a, "kind"), out var kind) || !Enum.IsDefined(kind)) throw new StudioCommandException("invalid_argument", "Provide a valid asset kind and explicit index.");
        return doc.PreviewDocument.Assets.SingleOrDefault(x => x.Kind == kind && x.Index == Int(a, "index")) ?? throw new StudioCommandException("stale_asset", "Asset not found in this document.");
    }
    private static readonly StudioParameter[] AssetParameters = [DocumentParameter, P("kind", "string", "Asset kind returned by assets.", true), P("index", "integer", "Authored record index.", true)];

    private void RegisterWorkspaceCommands(StudioCommands r)
    {
        RegisterJob(r, "open_root", "Open and index a ZBD root or source project in the visible workspace. Dirty documents must be explicitly saved or closed first.",
            [P("path", "string", "Absolute ZBD root or source project directory.", true),
             P("project", "boolean", "Require an initialized source project (a folder with data and gamegen), as the welcome screen's source project Open does; any other folder is refused with not_project. Default false.")], false, async (a, token) =>
        {
            RequireRootPublication();
            // A relative folder would resolve against zStudio's own folder, not the client's.
            if (!Path.IsPathFullyQualified(Text(a, "path"))) throw new StudioCommandException("invalid_argument", "Give the folder as a full path.");
            string path = Path.GetFullPath(Text(a,"path"));
            if (Flag(a, "project")) { await RequireSourceProjectAsync(path, token); RequireRootPublication(); }
            long workspaceGeneration = ViewModel.WorkspaceGeneration + 1;
            await ViewModel.OpenRootAsync(path, token, RequireRootPublication);
            if (ViewModel.WorkspaceGeneration != workspaceGeneration || !ViewModel.RootPath.Equals(path,StringComparison.OrdinalIgnoreCase)) throw new StudioCommandException("context_changed","Workspace was replaced during indexing.");
            UpdateRecent(); return Result(new { ViewModel.RootPath, ViewModel.Status, files = ViewModel.Files.Count });
        });
        Register(r, "files", "List recognized files in the current root.", false, PageParameters, a => Page(ViewModel.Files.Where(f => f.RelativePath.Contains(Text(a, "query"), StringComparison.OrdinalIgnoreCase)), a));
        Register(r, "search", "Search indexed assets without altering GUI selection; names are not identities.", false, PageParameters, a => Page(ViewModel.SearchIndex(Text(a, "query")), a));
        Register(r, "related", "List related indexed assets for an explicit asset, retaining source identities.", false, [.. AssetParameters, .. PageParameters], a =>
        {
            var d = TargetDocument(a); var related = FindRelated(TargetAsset(d, a), d);
            var page = Page(related.Items, a, matches: (x, query) => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.File.Contains(query, StringComparison.OrdinalIgnoreCase),
                maximumRowBytes: x => 256 + 6L * (x.Name.Length + 2L * x.File.Length));
            page.Data.AsObject()["truncated"] = related.Truncated;
            return page;
        });
        Register(r, "asset_filter", "Set the Assets list query and kind filter without changing the selected record.", true,
            [DocumentParameter, P("query", "string", "Name filter."), P("kind", "string", "Asset kind or All types.")], a =>
        {
            var d = TargetDocument(a); RequireNoDrafts(d);
            if (a.ContainsKey("kind")) { string kind = Text(a, "kind"); if (kind != "All types" && !Enum.TryParse<AssetKind>(kind, out _)) throw new StudioCommandException("invalid_argument", "Unknown kind filter."); d.KindFilter = kind; }
            if (a.ContainsKey("query")) d.Query = Text(a, "query");
            return Result(new { d.Query, d.KindFilter });
        });
        RegisterJob(r, "open_document", "Open or activate an archive and its visible preview.", [P("path", "string", "Absolute archive path.", true)], false, async (a, token) =>
        {
            RequireNoDrafts(); string path = Path.GetFullPath(FullPath(a, "path"));
            await ViewModel.EnsureRootForFileAsync(path, cancellationToken: token, beforePublish: RequireRootPublication);
            var doc = await ViewModel.OpenFileAsync(path, token, () => { RequireAutomationMutationAvailable(); RequireNoDrafts(); }) ?? throw new StudioCommandException("open_failed", ViewModel.Status);
            SelectNavigatorSection(1); await previewWork;
            if (doc.IsDisposed || ViewModel.SelectedDocument != doc) throw new StudioCommandException("context_changed","The active document changed while opening.");
            return Result(DocumentState(doc));
        });
        Register(r, "assets", "List current edited assets by current kind/index. Resource member/script UUIDs remain stable through reordering. Offset/Length describe the edited snapshot; sourceOffset/sourceLength identify original bytes, or are null for newly added records.", false, [DocumentParameter, .. PageParameters], a =>
        {
            var doc = TargetDocument(a);
            return Page(doc.Assets, a, x => x.Name, x =>
            {
                var source = doc.OriginalAsset(x.Record);
                return new { x.Record.Kind, x.Index, x.Name, x.Record.Offset, x.Record.Length, x.Summary, member = doc.ResourceEdits == null ? (Guid?)null : x.ResourceId, script = doc.ScriptEdits == null ? (Guid?)null : x.ResourceId, sourceOffset = source?.Offset, sourceLength = source?.Length };
            });
        });
        RegisterJob(r, "select_asset", "Select an asset in the GUI and await its preview; does not retarget Properties.", AssetParameters, false, async (a, token) =>
        {
            RequireNoDrafts(); var doc = TargetDocument(a); var asset = TargetAsset(doc, a); ViewModel.SelectedDocument = doc;
            doc.Query = ""; doc.KindFilter = "All types"; doc.SelectedAsset = doc.Assets.Single(x => x.Record.Kind == asset.Kind && x.Index == asset.Index); SelectNavigatorSection(1);
            await EnsureAssetPreviewAsync(doc, asset);
            if (shownDocument != doc || shownAsset?.Id != asset.Id) throw new StudioCommandException("context_changed", "The user selected another preview.");
            if (EmptyPreview.Visibility == System.Windows.Visibility.Visible) throw new StudioCommandException("preview_unavailable",EmptyPreview.Text);
            return Result(new { document = DocumentState(doc), asset = asset.Id, ViewModel.Status });
        });
        Register(r, "inspect_asset", "Read original asset metadata/content and a separately frozen edited snapshot at one revision. ZRD/script inspection bounds nodes, instructions and strings. Model/world/sound lists preview 32 records; nested metadata has node/depth/text budgets with properties_truncated. Motion metadata previews 32 parts with 128-character names; motion_records pages tracks. Animation inspection previews 4 records per reference table, 4 puffers and 16 sequence summaries; sequence Properties previews 8 events, event/tail raw previews use 256 bytes, and keyframe streams are omitted. Totals/truncation flags disclose omissions; animation_records/references/property_fields inspect individual records. JSON export retains complete data. Closed or changed documents reject stale results.", false, AssetParameters, async (a, token) =>
        {
            var doc = TargetDocument(a); var asset = TargetAsset(doc, a);
            return await InspectAssetAsync(doc, asset, token);
        });
        Register(r, "source_bytes", "Read at most 4096 original source bytes; these are not pending edits or runtime memory.", false,
            [DocumentParameter, new("offset", "integer", "Absolute byte offset.", true, Minimum: 0, Maximum: long.MaxValue), P("length", "integer", "Byte count, 0–4096.", true)], a =>
        {
            var d = TargetDocument(a); long offset = a["offset"]!.GetValue<long>(); int length = Int(a, "length");
            if (length is < 0 or > 4096) throw new StudioCommandException("invalid_argument", "Length must be 0–4096.");
            return Result(new { offset, length, scope = "original source", hex = Convert.ToHexString(d.Document.Slice(offset, length).Span) });
        });
        Register(r, "close_document", "Close a document. Explicit discard=true is required for unsaved edits; pending drafts are never discarded implicitly.", true,
            [DocumentParameter, RevisionParameter, P("discard", "boolean", "Explicitly discard this document's accepted unsaved edits.")], a =>
        {
            var doc = TargetDocument(a, true);
            if (doc.SourceWorld is { IsRebuilding: true }) throw new StudioCommandException("busy", "The world is rebuilding after an edit; close it when it is shown.");
            if (doc.IsDirty && !OtherSourceWorldOpen(doc) && SourceWorldPending(doc)) throw new StudioCommandException("busy", "A world of this source project is opening or rebuilding; close this one when it is shown.");
            if (doc.IsDirty && Flag(a, "discard") && doc.SourceWorld is { } discarded && !OtherSourceWorldOpen(doc)) discardApprovedWorkspace = (discarded.Workspace, discarded.Workspace.Revision);
            // A source world's edits belong to its project; closing one of several open worlds keeps them.
            if (doc.IsDirty && !Flag(a, "discard") && !OtherSourceWorldOpen(doc)) throw new StudioCommandException("unsaved_changes", doc.SourceWorld != null ? "Save or explicitly discard the source project's edits; this is its last open world." : "Save or explicitly discard this document.");
            ViewModel.CloseResolved(doc); return Result(new { closed = doc.SessionId });
        });
        RegisterJob(r, "reload_document", "Stage and reparse a clean document before replacing it, using the current model/resource Save As destination. An already-open destination, failure or pre-publication cancellation retains the document and preview. Dirty documents must first be saved or explicitly closed. A source world instead rebuilds from the project's current sources and pending edits. External changes to any dirty workspace source cause a conflict, including scripts, models, resources and animation definitions (.zad).", [DocumentParameter, RevisionParameter], false, async (a, token) =>
        {
            var doc = TargetDocument(a, true); if (doc.IsDirty && doc.SourceWorld == null) throw new StudioCommandException("unsaved_changes", "Save or explicitly close with discard before reloading.");
            bool active = ViewModel.SelectedDocument == doc;
            var selected = ViewModel.SelectedDocument;
            long generation = ViewModel.NavigationGeneration + 1;
            try
            {
                var next = await ViewModel.ReloadDocumentAsync(doc, doc.Revision, cancellationToken: token);
                // Source rebuilding owns the publication boundary and its non-rollback preview completion.
                // The original document's lifetime is already canceled; late request cancellation cannot retract it.
                if (doc.SourceWorld != null) return Result(DocumentState(next));
                if (active) await previewWork;
                token.ThrowIfCancellationRequested();
                if (next.IsDisposed || !ViewModel.Documents.Contains(next) || active && ViewModel.SelectedDocument != next)
                    throw new StudioCommandException("context_changed", "The reloaded document was closed or superseded before completion.");
                return Result(DocumentState(next));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                if (ViewModel.NavigationGeneration == generation && !doc.IsDisposed && ViewModel.SelectedDocument == selected)
                    ViewModel.Status = ex is OperationCanceledException ? "Reload canceled; the existing document was retained." : ex.Message;
                throw;
            }
        });
        Register(r, "undo_redo", "Undo or redo one accepted edit in the specified document.", true, [DocumentParameter, RevisionParameter, P("action", "string", "History direction.", true, "undo", "redo")], async (a, token) =>
        {
            var d = TargetDocument(a, true);
            if (d.SourceWorld != null) return Result(DocumentState(await UndoSourceWorldAsync(d, Text(a, "action") == "redo", token)));
            UndoDocument(d, Text(a, "action") == "redo"); if (d.ContentEdits != null) await contentWork.WaitAsync(token); else if (d.ResourceEdits != null) await resourceWork.WaitAsync(token); else if (d.ModelEdits != null) await modelRefreshWork.WaitAsync(token); return Result(DocumentState(d));
        });
        RegisterJob(r, "save_document", "Verified save: animations require a NEW destination outside the source root; pickup/AI/tank coordinates save owning archives or explicit new destinations; model edits save all texture variants before GameZ; ZAR/ZRD, script and texture saves verify and atomically replace each working destination or create new Save As files. Batch results preview at most 64 saved/remaining paths and 32 errors, each at most 512 characters, with SavedPathCount/SavedPathsTruncated, RemainingPathCount/RemainingPathsTruncated and ErrorCount/ErrorsTruncated. Source saves return written with writtenCount/writtenTruncated under the same path bound. All files are still saved; truncated previews are not full path identities. Global state.contentEdits reports fileCount with empty files/filesTruncated; per-document command results preview affected paths and targets. Partial content/coordinate Save As retains every requested destination; ordinary Save retries unpublished copies without overwriting existing files.",
            [DocumentParameter, RevisionParameter, P("destination", "string", "Full path of a new single-file Save As (animation, ZAR/ZRD, script or texture pack). Omit to save working files."), P("modelDirectory", "string", "Full path of the model Save As directory; all GameZ/texture destinations must be new. Omit for verified save to the working files."), new("destinations", "object", "Mission coordinate or texture batch source path to new Save As path map; cover every affected file.", AdditionalProperties: new("", "string", "New Save As full path for this source archive.")), P("backup", "boolean", "Mission coordinate backup preference; defaults to app setting.")], false, async (a, token) =>
        {
            var d = TargetDocument(a, true);
            // Save As paths are full paths, refused before anything is written (source keys name open files and are compared).
            string destination = FullPath(a, "destination"), modelDirectory = FullPath(a, "modelDirectory");
            if (a["destinations"] is JsonObject map) foreach (var (_, target) in map) FullPath(target?.GetValue<string>() ?? "", "every destinations path");
            IsEnabled = false; if (propertiesWindow != null) propertiesWindow.IsEnabled = false;
            try
            {
                if (d.SourceWorld != null)
                {
                    if (a.ContainsKey("destination") || a.ContainsKey("destinations") || a.ContainsKey("modelDirectory")) throw new StudioCommandException("invalid_argument", "A source world saves to its project's sources; it has no Save As.");
                    var written = await SaveSourceWorldAsync(d, token);
                    // The files were replaced: the job completes with them, even when MCP stops (and cancels it) meanwhile.
                    CommitRunningJob();
                    var preview = FileResultPreview.Paths(written, written.Count);
                    return Result(new { document = DocumentState(d), written = preview.Values, writtenCount = preview.Count, writtenTruncated = preview.Truncated });
                }
                if (d.ContentEdits != null)
                {
                    var targets = (a["destinations"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
                    if (a.ContainsKey("destination"))
                    {
                        if (targets != null) throw new StudioCommandException("invalid_argument", "Use destination or destinations, not both.");
                        targets = new(StringComparer.OrdinalIgnoreCase) { [d.Path] = destination };
                    }
                    var result = await SaveContentAsync(d, targets, token);
                    return Result(new { document = DocumentState(d), result = FileResultPreview.Saved(result.SavedPaths, result.Errors, result.RemainingPaths) });
                }
                if (d.ResourceEdits != null)
                { await SaveResourcesAsync(d, destination is { Length: > 0 } path ? path : null, token); return Result(DocumentState(d)); }
                if (d.AnimationEdits != null)
                {
                    if (destination.Length == 0) throw new StudioCommandException("destination_required", "Animation Save As requires a new output path.");
                    await SaveAnimationToPathAsync(d, destination, token); return Result(DocumentState(d));
                }
                ModelSaveResult? models = null;
                if (d.ModelEdits?.IsDirty == true || d.ModelEdits != null && a.ContainsKey("modelDirectory"))
                {
                    models = await SaveModelsAsync(d, modelDirectory is { Length: > 0 } directory ? directory : null, token);
                    if (models.Errors.Count > 0 || d.PickupEdits?.IsDirty != true)
                        return Result(new { document = DocumentState(d), models = FileResultPreview.Saved(models.SavedPaths, models.Errors) });
                }
                if (d.PickupEdits is not { } edits) throw new StudioCommandException("unsupported", "This document has no accepted unsaved edits.");
                var destinations = (a["destinations"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase);
                var saved = await SavePickupDestinationsAsync(d, destinations, Flag(a, "backup", ViewModel.Settings.CreateBackupOnSave), token);
                return Result(new { document = DocumentState(d), result = FileResultPreview.Saved(saved.SavedPaths, saved.Errors),
                    models = models == null ? null : FileResultPreview.Saved(models.SavedPaths, models.Errors) });
            }
            finally { IsEnabled = true; if (propertiesWindow != null) propertiesWindow.IsEnabled = true; }
        });
        RegisterJob(r, "export", "Export assets through the existing deterministic exporter into a new folder outside the source tree.",
            [DocumentParameter, P("destination", "string", "Full path of the destination directory.", true), new("assets", "array", "Optional list of {kind,index}; omitted exports all.", Items: new("", "object", "Asset identity.", Properties: [new("kind", "string", "Asset kind.", true, Enum.GetNames<AssetKind>()), P("index", "integer", "Authored record index.", true)])), P("jsonOnly", "boolean", "Export inspection JSON."), P("lod", "integer", "LOD rank; default 0."), P("texturePack", "string", "Optional preferred texture pack path.")], true, async (a, token) =>
        {
            var d = TargetDocument(a); var assets = a["assets"] is JsonArray list ? list.Select(x => TargetAsset(d, x as JsonObject ?? throw new StudioCommandException("invalid_argument", "Asset must contain kind/index."))).ToArray() : d.PreviewDocument.Assets.ToArray();
            return Result(await ExportAssetsAsync(d, assets, FullPath(a, "destination"), Flag(a, "jsonOnly"), Text(a, "texturePack") is { Length: > 0 } pack ? pack : null, Int(a, "lod"), token));
        });
        RegisterJob(r, "validate", "Validate the source archive on disk, not pending edits. Returns structured diagnostics.", [DocumentParameter], true, async (a, token) => Result(await ValidateDocumentSourceAsync(TargetDocument(a), token)));
        Register(r, "problems", "List file/operation problems with original severity and source context.", false, PageParameters, a => Page(ViewModel.Problems, a, p => p.Message + " " + p.File + " " + p.Severity + " " + p.Category));
        RegisterOperationCommands(r);
    }
    private void RequireRootPublication()
    {
        RequireAutomationMutationAvailable(); RequireNoDrafts();
        if (ViewModel.Documents.Any(d => d.IsDirty))
            throw new StudioCommandException("unsaved_changes", "Save or explicitly discard dirty documents before changing roots.");
    }
}
