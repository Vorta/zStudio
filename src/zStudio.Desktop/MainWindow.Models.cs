using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private Task modelRefreshWork = Task.CompletedTask;
    private void RegisterModelCommands(StudioCommands r)
    {
        RegisterJob(r, "model_bundle_export", "Export all authored models under a node or an animation root as assembled and local OBJ/MTL/PNG files with stable indices, transforms and source fingerprint.",
            [.. AssetParameters, P("destination", "string", "Full path of an export folder outside the source tree.", true), new("rootNode", "integer", "Explicit GameZ root node, required for ambiguous animation roots.", Minimum: 0, Maximum: int.MaxValue), P("texturePack", "string", "Optional preferred texture pack.")], true,
            async (a, token) => Result(await ExportModelBundleAsync(TargetDocument(a), TargetAsset(TargetDocument(a), a), FullPath(a, "destination"), a.ContainsKey("rootNode") ? Int(a,"rootNode") : null, Text(a,"texturePack") is { Length: > 0 } pack ? pack : null, token)));
        RegisterJob(r, "model_replace", "Validate an explicit version-1 OBJ/PNG replacement manifest, then accept the entire batch as one document undo step. Model indices and source SHA-256 must match. Shared file ownership is acquired before acceptance; conflicting writers leave the document unchanged. Saves use save_document. texturePacks previews at most 64 paths of 512 characters, with texturePackCount/texturePacksTruncated; all prepared packs remain part of the edit and save.",
            [DocumentParameter, RevisionParameter, P("manifest", "string", "Absolute replacement manifest JSON path.", true)], false, async (a, token) =>
            {
                var doc = TargetDocument(a, true); await ReplaceModelsAsync(doc, FullPath(a, "manifest"), doc.Revision, token);
                var packs = FileResultPreview.Paths(doc.ModelEdits!.Current.Textures.Keys, doc.ModelEdits.Current.Textures.Count);
                return Result(new { document = DocumentState(doc), totalModels = doc.ModelEdits.Current.World.Scene!.Models.Count,
                    texturePacks = packs.Values, texturePackCount = packs.Count, texturePacksTruncated = packs.Truncated });
            });
    }
    private async Task<ModelBundleResult> ExportModelBundleAsync(DocumentModel doc, AssetRecord asset, string destination, int? explicitRoot, string? pack, CancellationToken token)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export or validation is already running.");
        var resolver = ViewModel.Resolver ?? throw new StudioCommandException("no_workspace", "Open a root first.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token); operation = cancellation; CancelOperationItem.IsEnabled = true;
        try
        {
            var world = doc.PreviewDocument; int root;
            if (asset.Kind == AssetKind.Animation)
            {
                var context = await doc.GetAnimationContextAsync(resolver, cancellation.Token, difficulty: ViewModel.Difficulty); world = context.World;
                string name = doc.AnimationEdits!.Package.Entries[asset.Index].RootName;
                var roots = world.Scene!.Nodes.Where(n => n.Name == name).ToArray();
                root = explicitRoot ?? (roots.Length == 1 ? roots[0].Index : throw new InvalidDataException("Animation root is missing or ambiguous. Supply an explicit root node index."));
            }
            else root = explicitRoot ?? (asset.Kind == AssetKind.Node ? asset.Index : SceneLods.PreviewRoot(world.Scene ?? throw new InvalidDataException("Select an animation or GameZ model/node."), asset) ?? throw new InvalidDataException("Select a node-bound model."));
            var result = await Task.Run(() => new ExportService(resolver).ExportModelBundleAsync(world, root, destination, pack, cancellation.Token), cancellation.Token);
            ViewModel.Status = $"Exported {result.Models} models to {result.Directory}"; return result;
        }
        finally { operation = null; CancelOperationItem.IsEnabled = false; }
    }
    private async Task ReplaceModelsAsync(DocumentModel doc, string manifest, long revision, CancellationToken token)
    {
        var edits = doc.ModelEdits ?? throw new InvalidDataException("Open the matching GameZ v15 file to replace models.");
        var resolver = ViewModel.Resolver ?? throw new StudioCommandException("no_workspace", "Open a root first.");
        RequireNoDrafts();
        RequireSingleModelOwner();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token);
        ViewModel.Status = "Validating model replacement and all texture variants…";
        var batch = await Task.Run(() => ModelImport.ReadAsync(manifest, cancellation.Token), cancellation.Token);
        var prepared = await edits.PrepareAsync(batch, resolver, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested(); RequireAutomationMutationAvailable(); RequireNoDrafts();
        if (doc.IsDisposed || !ViewModel.Documents.Contains(doc)) throw new StudioCommandException("stale_document", "The target document was closed.");
        if (doc.Revision != revision) throw new StudioCommandException("revision_conflict", "The document changed while importing; no replacement was accepted.");
        RequireSingleModelOwner();
        edits.Accept(prepared);
        // Publication is complete. Rebuild accepted snapshots using the document lifetime, not a revoked request.
        using (PreviewOperation.Begin(CancellationToken.None)) await RefreshModelDependentsAsync(doc);
        ViewModel.Status = $"Replaced {batch.Models.Count} models · unsaved · {prepared.Textures.Count} texture variants";
        void RequireSingleModelOwner()
        {
            if (ViewModel.Documents.Any(other => other != doc && other.ModelEdits?.HasModelImports == true && Path.GetDirectoryName(other.Path)!.Equals(Path.GetDirectoryName(doc.Path), StringComparison.OrdinalIgnoreCase)))
                throw new StudioCommandException("shared_texture_owner", "Another GameZ document owns model edits in this mission. Save/discard and close it before importing into a different GameZ file.");
        }
    }
    private async Task RefreshModelDependentsAsync(DocumentModel doc)
    {
        foreach (var open in ViewModel.Documents) open.InvalidateMissionContext();
        if (propertiesWindow?.Document == doc) await propertiesWindow.AssetRefreshWork;
        await previewWork;
        if (animation != null) await animation.RefreshModelContextAsync();
        else if (shownDocument == doc) await ShowAsset(doc, doc.SelectedAsset?.Record ?? shownAsset);
        UpdateDocumentCommands();
    }
    private async Task<ModelSaveResult> SaveModelsAsync(DocumentModel doc, string? directory, CancellationToken token)
    {
        using var save = BeginDocumentSave();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token);
        var result = await doc.ModelEdits!.SaveAsync(directory, cancellation.Token);
        if (ViewModel.Resolver is { } resolver) await resolver.InvalidateAsync(result.SavedPaths, doc.Lifetime.Token);
        foreach (var open in ViewModel.Documents) open.InvalidateMissionContext();
        foreach (string error in result.Errors) ViewModel.AddProblem(error, file: doc.Path);
        if (directory != null) doc.LastSavedCopy = Path.Combine(directory, Path.GetFileName(doc.Path));
        doc.IsStale = false;
        ViewModel.Status = $"Saved and verified {result.SavedPaths.Count} files";
        return result;
    }
    private async void ExportModelBundleClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is not { SelectedAsset: { } asset } doc) return;
        OpenFolderDialog dialog = new() { Title = "Export referenced models" }; if (dialog.ShowDialog(this) != true) return;
        try { await ExportModelBundleAsync(doc, asset.Record, dialog.FolderName, null, PreferredPack, CancellationToken.None); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); MessageBox.Show(this, ex.Message, "Model export"); }
    }
    private async void ReplaceModelsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is not { } doc || !ResolvePropertiesDrafts(doc)) return;
        if (doc.SelectedAsset?.Record.Content is Recoil.Zbd.Core.Formats.MechAssembly)
        { await ResourceUiAsync(() => ReplaceMechModelDialogAsync(doc)); return; }
        if (doc.ModelEdits == null) return;
        OpenFileDialog dialog = new() { Title = "Replace models · select replacement manifest", Filter = "Replacement manifest|*.json" }; if (dialog.ShowDialog(this) != true) return;
        try { await ReplaceModelsAsync(doc, dialog.FileName, doc.Revision, CancellationToken.None); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); MessageBox.Show(this, ex.Message, "Model replacement"); }
    }
    private async Task<bool> SaveModelDocumentAsync(DocumentModel doc, bool saveAs)
    {
        if (!ResolvePropertiesDrafts(doc)) return false;
        try
        {
            string? directory = null;
            if (saveAs || PickupPlacementEditSession.IsProtectedPath(doc.ModelEdits!.TargetPath(doc.Path)))
            {
                OpenFolderDialog dialog = new() { Title = "Save model and texture copies to a new mission folder" }; if (dialog.ShowDialog(this) != true) return false;
                directory = dialog.FolderName;
            }
            var result = await SaveModelsAsync(doc, directory, CancellationToken.None);
            if (result.Errors.Count > 0) { MessageBox.Show(this, string.Join("\n",result.Errors), "Remaining model changes are unsaved"); return false; }
            return doc.PickupEdits?.IsDirty != true || await SavePickupsAsync(doc, saveAs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); MessageBox.Show(this, ex.Message, "Model save"); return false; }
    }
}
