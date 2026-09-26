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
    internal static StudioResult Page<T>(IEnumerable<T> source, JsonObject a, Func<T, string>? search = null)
    {
        int offset = Int(a, "offset"), limit = Int(a, "limit", 100);
        if (offset < 0 || limit is < 1 or > 200) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–200.");
        if (search != null && Text(a, "query") is { Length: > 0 } query) source = source.Where(item => search(item).Contains(query, StringComparison.OrdinalIgnoreCase));
        var all = source.ToArray(); return Result(new { total = all.Length, offset, nextOffset = (long)offset + limit < all.Length ? (int?)(offset + limit) : null, items = all.Skip(offset).Take(limit) });
    }
    private static AssetRecord TargetAsset(DocumentModel doc, JsonObject a)
    {
        if (!a.ContainsKey("index") || !Enum.TryParse<AssetKind>(Text(a, "kind"), out var kind) || !Enum.IsDefined(kind)) throw new StudioCommandException("invalid_argument", "Provide a valid asset kind and explicit index.");
        return doc.Document.Assets.SingleOrDefault(x => x.Kind == kind && x.Index == Int(a, "index")) ?? throw new StudioCommandException("stale_asset", "Asset not found in this document.");
    }
    private static readonly StudioParameter[] AssetParameters = [DocumentParameter, P("kind", "string", "Asset kind returned by assets.", true), P("index", "integer", "Authored record index.", true)];

    private void RegisterWorkspaceCommands(StudioCommands r)
    {
        RegisterJob(r, "open_root", "Open and index a ZBD root in the visible workspace. Dirty documents must be explicitly saved or closed first.", [P("path", "string", "Absolute ZBD root directory.", true)], false, async (a, token) =>
        {
            RequireNoDrafts(); if (ViewModel.Documents.Any(d => d.IsDirty)) throw new StudioCommandException("unsaved_changes", "Save or explicitly discard dirty documents before changing roots.");
            string path = Path.GetFullPath(Text(a,"path"));
            await ViewModel.OpenRootAsync(path, token);
            if (!ViewModel.RootPath.Equals(path,StringComparison.OrdinalIgnoreCase)) throw new StudioCommandException("context_changed","Workspace was replaced during indexing.");
            UpdateRecent(); return Result(new { ViewModel.RootPath, ViewModel.Status, files = ViewModel.Files.Count });
        });
        Register(r, "files", "List recognized files in the current root.", false, PageParameters, a => Page(ViewModel.Files.Where(f => f.RelativePath.Contains(Text(a, "query"), StringComparison.OrdinalIgnoreCase)), a));
        Register(r, "search", "Search indexed assets without altering GUI selection; names are not identities.", false, PageParameters, a => Page(ViewModel.SearchIndex(Text(a, "query")), a));
        Register(r, "related", "List related indexed assets for an explicit asset, retaining source identities.", false, [.. AssetParameters, .. PageParameters], a =>
        { var d = TargetDocument(a); return Page(FindRelated(TargetAsset(d, a), d), a, x => x.Name + " " + x.File); });
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
            RequireNoDrafts(); string path = Path.GetFullPath(Text(a, "path"));
            if (!ViewModel.HasRoot) await ViewModel.OpenRootAsync(Path.GetDirectoryName(path)!, token);
            var doc = await ViewModel.OpenFileAsync(path, token) ?? throw new StudioCommandException("open_failed", ViewModel.Status);
            NavigationTabs.SelectedItem = AssetsTab; await previewWork;
            if (doc.IsDisposed || ViewModel.SelectedDocument != doc) throw new StudioCommandException("context_changed","The active document changed while opening.");
            return Result(DocumentState(doc));
        });
        Register(r, "assets", "List assets by stable kind/index in an open document.", false, [DocumentParameter, .. PageParameters], a =>
        {
            var doc = TargetDocument(a); return Page(doc.Document.Assets.Where(x => x.Name.Contains(Text(a, "query"), StringComparison.OrdinalIgnoreCase)).Select(x => new { x.Kind, x.Index, x.Name, x.Offset, x.Length, x.Summary }), a);
        });
        RegisterJob(r, "select_asset", "Select an asset in the GUI and await its preview; does not retarget Properties.", AssetParameters, false, async (a, token) =>
        {
            RequireNoDrafts(); var doc = TargetDocument(a); var asset = TargetAsset(doc, a); ViewModel.SelectedDocument = doc;
            doc.Query = ""; doc.KindFilter = "All types"; doc.SelectedAsset = doc.Assets.Single(x => x.Record == asset); NavigationTabs.SelectedItem = AssetsTab;
            await previewWork;
            if (shownDocument != doc || shownAsset?.Id != asset.Id) throw new StudioCommandException("context_changed", "The user selected another preview.");
            if (EmptyPreview.Visibility == System.Windows.Visibility.Visible) throw new StudioCommandException("preview_unavailable",EmptyPreview.Text);
            return Result(new { document = DocumentState(doc), asset = asset.Id, ViewModel.Status });
        });
        Register(r, "inspect_asset", "Read stored asset metadata and content. Animation edited state is returned separately from source.", false, AssetParameters, async (a, token) =>
        {
            var doc = TargetDocument(a); var asset = TargetAsset(doc, a);
            var source = await Task.Run(() => ExportService.AssetJson(doc.Document, asset, token), token);
            return Result(new { document = doc.SessionId, doc.Revision, source, edited = asset.Kind == AssetKind.Animation ? doc.AnimationEdits?.Package.Entries[asset.Index].ToJson() : null });
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
            var doc = TargetDocument(a, true); if (doc.IsDirty && !Flag(a, "discard")) throw new StudioCommandException("unsaved_changes", "Save or explicitly discard this document.");
            ViewModel.CloseResolved(doc); return Result(new { closed = doc.SessionId });
        });
        RegisterJob(r, "reload_document", "Reload a clean document from disk; dirty documents must first be saved or explicitly closed.", [DocumentParameter, RevisionParameter], false, async (a, token) =>
        {
            var doc = TargetDocument(a, true); if (doc.IsDirty) throw new StudioCommandException("unsaved_changes", "Save or explicitly close with discard before reloading.");
            string path = doc.Path; ViewModel.CloseResolved(doc);
            var next = await ViewModel.OpenFileAsync(path, token) ?? throw new StudioCommandException("open_failed", ViewModel.Status);
            await previewWork; token.ThrowIfCancellationRequested();
            if (next.IsDisposed || ViewModel.SelectedDocument != next) throw new StudioCommandException("context_changed", "The active document changed while reloading.");
            return Result(DocumentState(next));
        });
        Register(r, "undo_redo", "Undo or redo one accepted edit in the specified document.", true, [DocumentParameter, RevisionParameter, P("action", "string", "History direction.", true, "undo", "redo")], a =>
        {
            var d = TargetDocument(a, true); UndoDocument(d, Text(a, "action") == "redo"); return Result(DocumentState(d));
        });
        RegisterJob(r, "save_document", "Verified save: animations require a NEW destination outside the source root; pickups save owning archives or explicit new destinations.",
            [DocumentParameter, RevisionParameter, P("destination", "string", "New animation archive path."), new("destinations", "object", "Pickup source archive path to new Save As path map.", AdditionalProperties: new("", "string", "New Save As path for this source archive.")), P("backup", "boolean", "Pickup backup preference; defaults to app setting.")], false, async (a, token) =>
        {
            var d = TargetDocument(a, true);
            IsEnabled = false; if (propertiesWindow != null) propertiesWindow.IsEnabled = false;
            try
            {
                if (d.AnimationEdits != null)
                {
                    if (Text(a, "destination").Length == 0) throw new StudioCommandException("destination_required", "Animation Save As requires a new output path.");
                    await SaveAnimationToPathAsync(d, Text(a, "destination"), token); return Result(DocumentState(d));
                }
                if (d.PickupEdits is not { } edits) throw new StudioCommandException("unsupported", "This document does not support saving edits.");
                var destinations = (a["destinations"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase);
                var saved = await SavePickupDestinationsAsync(d, destinations, Flag(a, "backup", ViewModel.Settings.CreateBackupOnSave), token);
                return Result(new { document = DocumentState(d), result = saved });
            }
            finally { IsEnabled = true; if (propertiesWindow != null) propertiesWindow.IsEnabled = true; }
        });
        RegisterJob(r, "export", "Export assets through the existing deterministic exporter into a new folder outside the source tree.",
            [DocumentParameter, P("destination", "string", "Destination directory.", true), new("assets", "array", "Optional list of {kind,index}; omitted exports all.", Items: new("", "object", "Asset identity.", Properties: [new("kind", "string", "Asset kind.", true, Enum.GetNames<AssetKind>()), P("index", "integer", "Authored record index.", true)])), P("jsonOnly", "boolean", "Export inspection JSON."), P("lod", "integer", "LOD rank; default 0."), P("texturePack", "string", "Optional preferred texture pack path.")], true, async (a, token) =>
        {
            var d = TargetDocument(a); var assets = a["assets"] is JsonArray list ? list.Select(x => TargetAsset(d, x as JsonObject ?? throw new StudioCommandException("invalid_argument", "Asset must contain kind/index."))).ToArray() : d.Document.Assets.ToArray();
            return Result(await ExportAssetsAsync(d, assets, Text(a, "destination"), Flag(a, "jsonOnly"), Text(a, "texturePack") is { Length: > 0 } pack ? pack : null, Int(a, "lod"), token));
        });
        RegisterJob(r, "validate", "Validate the source archive on disk, not pending edits. Returns structured diagnostics.", [DocumentParameter], true, async (a, token) => Result(await ValidateDocumentSourceAsync(TargetDocument(a), token)));
        Register(r, "problems", "List file/operation problems with original severity and source context.", false, PageParameters, a => Page(ViewModel.Problems, a, p => p.Message + " " + p.File + " " + p.Severity + " " + p.Category));
        RegisterOperationCommands(r);
    }
}
