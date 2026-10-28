using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Automation;

namespace zStudio.Desktop;

internal static class PropertyContext
{
    internal static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (var current = element; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is T target) return target;
        return null;
    }
}

public partial class MainWindow
{
    private PropertiesWindow? propertiesWindow;
    private AssetItem? contextAsset;
    private SceneTreeItem? contextScene;
    private SceneTreeItem? inspectedSceneSource;
    private DocumentModel? assetContextDocument, sceneContextDocument;
    private bool assetPointerContext, scenePointerContext;
    private long propertyRequest;
    internal Func<AssetDocument, AssetRecord, CancellationToken, Task<JsonObject>> LoadAssetPropertiesAsync { get; set; } =
        static (doc, asset, token) => Task.Run(() => ExportService.AssetJson(doc, asset, token, boundedZrd: true), token);
    internal PropertiesWindow? OpenPropertiesWindow => propertiesWindow;

    private PropertiesWindow GetPropertiesWindow()
    {
        if (propertiesWindow != null) return propertiesWindow;
        var window = new PropertiesWindow(this, ViewModel)
        {
            SaveRequested = SaveCurrentAsync,
            UndoRequested = UndoDocument,
        };
        window.Closed += (_, _) => { if (presentationProperties == window) presentationProperties = null; if (propertiesWindow == window) { propertiesWindow = null; ++propertyRequest; } };
        window.Retargeted += UpdateSourceInputBlock;
        window.PendingDraftStarted += () =>
        {
            if (window.Document?.SourceWorld is { } editing && shownDocument?.SourceWorld?.Workspace == editing.Workspace)
                StopWorldAnimationForEdit();
        };
        propertiesWindow = window; return window;
    }
    private void PresentProperties(PropertiesWindow window, bool accepted)
    {
        if (!accepted) return;
        using var lease = BeginLayoutChange();
        // Hidden UI keeps the window for when the UI shows again.
        if (presentationUiHidden) { presentationProperties = window; if (window.IsVisible) window.Hide(); return; }
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    /// <summary>Refuses (MCP, without a dialog) while Properties holds unfinished input, which retargeting or closing it would otherwise ask about.</summary>
    private void RequireNoPropertiesDrafts(string message)
    {
        if (propertiesWindow?.HasPendingDrafts == true) throw new StudioCommandException("pending_drafts", message);
    }
    internal bool ResolvePropertiesDrafts(DocumentModel? doc = null) => ResolveInspectionDrafts(doc) && (propertiesWindow == null || doc != null && propertiesWindow.Document != doc || propertiesWindow.ResolvePendingDrafts());
    /// <summary>
    /// Before choosing another object (the world tree, search, Recently selected, a row's Frame): only the selection's own
    /// draft is resolved, as a viewport click does. Choosing does not retarget Properties, so Properties input stays.
    /// </summary>
    internal bool ResolveSelectionDrafts(DocumentModel? doc = null) => ResolveInspectionDrafts(doc);
    internal async Task<bool> ResolvePropertiesDraftsAsync(DocumentModel? doc = null) => await ResolveInspectionDraftsAsync(doc) && (propertiesWindow == null || doc != null && propertiesWindow.Document != doc || await propertiesWindow.ResolvePendingDraftsAsync());
    private void UndoDocument(DocumentModel doc, bool redo)
    {
        doc = LiveDocument(doc);
        if (doc.SourceWorld is { } world)
        {
            // Ctrl+Z and Ctrl+Y wait while a polygon zone stroke is held or applying (its result would come after them).
            if (scene?.IsZonePainting == true) return;
            if (zoneTool.Applying) { ShowZoneRefusal("Undo and redo wait until the polygon zone strokes have applied."); return; }
            if (world.IsRebuilding || sourceWorkspaceBusy) Notice("A world of this source project is still rebuilding; undo and redo are available when it is shown.");
            else if (redo ? world.Workspace.CanRedo : world.Workspace.CanUndo)
            {
                // An object's edit mode without a change does not hold history back.
                if (inspectionDraft is { ObjectEditing: true, HasObjectDraft: false, ObjectApplying: false } clean && clean.DraftDocument == doc) clean.CancelDraft();
                sourceWorldWork = RunUi(() => UndoSourceWorldAsync(doc, redo, CancellationToken.None));
            }
        }
        // A compiled document is read only: it has no history.
    }
    internal async Task<PropertiesWindow?> OpenAssetPropertiesAsync(DocumentModel doc, AssetRecord asset, CancellationToken cancellationToken = default, bool automation = false)
    {
        if (!automation && !await ResolvePropertiesDraftsAsync()) return null;
        long request = ++propertyRequest;
        if (doc.IsDisposed) return null;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, doc.Lifetime.Token);
            long revision = doc.Revision; var snapshot = doc.Document;
            asset = snapshot.Assets.SingleOrDefault(a => a.Id == asset.Id) ?? throw new StudioCommandException("stale_asset", "This record is no longer in the document.");
            var json = await LoadAssetPropertiesAsync(snapshot, asset, cancellation.Token);
            if (doc.IsDisposed || request != propertyRequest) return null;
            cancellation.Token.ThrowIfCancellationRequested();
            if (doc.Revision != revision) throw new StudioCommandException("revision_conflict", "The document changed while loading Properties. Open the current record again.");
            if (automation) RequireNoPropertiesDrafts("Properties input changed while loading. Resolve drafts before retargeting.");
            var window = GetPropertiesWindow(); bool accepted = window.SetAsset(doc, asset, json);
            PresentProperties(window, accepted);
            return accepted && request == propertyRequest && propertiesWindow == window && window.Document == doc ? window : null;
        }
        catch (OperationCanceledException) when (doc.IsDisposed || request != propertyRequest) { return null; }
        catch (OperationCanceledException) when (!automation) { }
        catch (Exception ex) when (!automation && ex is IOException or InvalidDataException or ArgumentException or StudioCommandException) { Report(ex); }
        return null;
    }
    internal async void OpenCurrentProperties()
    {
        if (ViewModel.SelectedDocument is not { } doc) return;
        // A selected zone, network or definition: its card is the docked pane (its own draft stays as it is).
        if (doc.SourceWorld != null && SectionSelection != null) { sectionCard?.FocusFirst(); return; }
        if (!await ResolvePropertiesDraftsAsync()) return;
        if (doc.SourceWorld != null && CurrentInspectionCard?.ObjectNode is int sourceObject)
        {
            // A drag or Flycam keeps the selection; Problems says why instead of an error dialog.
            try { FocusSourceObjectCard(doc, sourceObject, focus: true); }
            catch (StudioCommandException ex) when (ex.Code == "busy") { Notice(ex.Message); }
            catch (StudioCommandException ex) { Report(ex); }
            return;
        }
        if (AssetGrid.IsKeyboardFocusWithin && doc.SelectedAsset is { } asset) { await OpenAssetPropertiesAsync(doc, asset.Record); return; }
        if (DocumentSceneTree.IsKeyboardFocusWithin && sceneTree?.Selected is { Selectable: true } selectedRow)
        {
            // A drag or Flycam keeps the selection; the status says why instead of an error dialog.
            try { await OpenScenePropertiesAsync(doc, selectedRow); }
            catch (StudioCommandException ex) when (ex.Code == "busy") { Notice(ex.Message); }
            catch (Exception ex) when (ex is StudioCommandException or IOException or UnauthorizedAccessException) { Report(ex); }
            return;
        }
        if (inspectedSceneSource is { } item)
        {
            try { await OpenScenePropertiesAsync(doc, item); }
            catch (StudioCommandException ex) when (ex.Code == "busy") { Notice(ex.Message); }
            catch (Exception ex) when (ex is StudioCommandException or IOException or UnauthorizedAccessException) { Report(ex); }
            return;
        }
        if (IsAiWorld && scene?.AiPathEditingNetwork != null) { await OpenAiPathPropertiesAsync(); return; }
        if (IsAiWorld && scene?.SelectedAiNode is { } aiNode) { await OpenAiPropertiesAsync(aiNode, false); return; }
        ++propertyRequest;
        var window = GetPropertiesWindow();
        if (selectedNode is int node && properties != null)
        {
            string name = (motion?.Viewport.PreviewScene ?? scene?.PreviewScene ?? doc.Document.Scene)?.Nodes.ElementAtOrDefault(node)?.Name ?? "Scene object";
            // A source world's own objects edit their sources; placed copies and mission actors open here: a pickup in its editable placement form, vehicles and animation-placed objects read-only.
            if (doc.SourceWorld != null && scene?.PickupAt(node) == null && SourceObjectNode(node) is int sourceNode && doc.SourceBuild?.Provenance.ContainsKey(sourceNode) == true
                && scene?.Mission?.InspectedActorAt(SourceSelectionOwner(doc, sourceNode)) == null)
            {
                try { await ShowSourceObjectPropertiesAsync(doc, sourceNode); }
                catch (StudioCommandException ex) when (ex.Code == "busy") { Notice(ex.Message); }
                catch (Exception ex) when (ex is StudioCommandException or IOException or UnauthorizedAccessException) { Report(ex); }
                return;
            }
            bool opened = doc.SourceWorld != null && scene?.PickupAt(node)?.Pickup is { } pickup && doc.PickupEdits?.Find(pickup.Source) != null
                ? window.SetPickup(doc, pickup.Source, $"{name} · node #{node}", properties, SourcePickupMove(doc)!)
                : window.SetReadOnly(doc, $"{name} · node #{node}", properties);
            PresentProperties(window, opened);
        }
        else if (doc.SelectedAsset is { } selected) await OpenAssetPropertiesAsync(doc, selected.Record);
        else PresentProperties(window, window.SetReadOnly(doc, "Archive", doc.Document.Metadata));
    }
    private async Task<PropertiesWindow?> OpenScenePropertiesAsync(DocumentModel doc, SceneTreeItem item, CancellationToken token = default, bool automation = false)
    {
        if (doc.IsDisposed || doc != sceneTreeDocument || item.Owner != sceneTree || !item.Selectable)
            return null;
        if (doc.SourceWorld != null)
        {
            if (IsSourceTerrain(doc, item.Index)) return await OpenSourceTerrainAsync(doc, item.Index!.Value, token) ? propertiesWindow : null;
            if (item.Index is int sourceIndex) FocusSourceObjectCard(doc, sourceIndex, focus: true);
            return null;
        }
        if (item.Synthetic || item.ReadOnlyReason != null)
        {
            ++propertyRequest;
            var readOnlyWindow = GetPropertiesWindow();
            bool shown = readOnlyWindow.SetReadOnly(doc, item.DisplayName, SceneTreeProperties(item));
            PresentProperties(readOnlyWindow, shown); return shown ? readOnlyWindow : null;
        }
        var node = item.Node!;
        if (!item.Owner.IsPreview && doc.Document.Assets.FirstOrDefault(a => a.Kind == AssetKind.Node && a.Index == node.Index) is { } asset)
            return await OpenAssetPropertiesAsync(doc, asset, token, automation);
        ++propertyRequest;
        var window = GetPropertiesWindow(); bool opened = window.SetReadOnly(doc, $"{node.Name} · node #{node.Index}", SceneTreeProperties(item));
        PresentProperties(window, opened); return opened ? window : null;
    }
    private void AssetContextTarget(object sender, MouseButtonEventArgs e)
    {
        contextAsset = PropertyContext.FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as AssetItem;
        assetContextDocument = ViewModel.SelectedDocument; assetPointerContext = true; e.Handled = true; // Do not change the multi-selection used by Export.
    }
    private void AssetContextOpening(object sender, RoutedEventArgs e)
    {
        if (!assetPointerContext) { contextAsset = AssetGrid.CurrentItem as AssetItem ?? AssetGrid.SelectedItem as AssetItem; assetContextDocument = ViewModel.SelectedDocument; }
        assetPointerContext = false; AssetPropertiesMenu.IsEnabled = contextAsset != null;
    }
    private async void AssetPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (contextAsset is not { } asset || assetContextDocument is not { IsDisposed: false } doc) return;
        await OpenAssetPropertiesAsync(doc, asset.Record);
    }
    /// <summary>Whether, and on which row, the World tree had the keyboard when its context menu was opened (Group by type gives it back).</summary>
    private (bool Had, SceneTreeItem? Row) sceneMenuFocus;
    private void SceneContextTarget(object sender, MouseButtonEventArgs e)
    {
        sceneMenuFocus = (DocumentSceneTree.IsKeyboardFocusWithin, FocusedSceneRow());
        contextScene = PropertyContext.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as SceneTreeItem;
        sceneContextDocument = ViewModel.SelectedDocument; scenePointerContext = true; e.Handled = true;
    }
    private void SceneContextOpening(object sender, RoutedEventArgs e)
    {
        if (!scenePointerContext) { contextScene = DocumentSceneTree.SelectedItem as SceneTreeItem; sceneContextDocument = ViewModel.SelectedDocument; }
        scenePointerContext = false;
        bool addPickup = sceneContextDocument is { SourceWorld: not null } && sceneContextDocument == shownDocument &&
            contextScene?.Owner == sceneTree && contextScene?.Pass?.Kind == "puppies";
        SceneAddPickupMenu.Visibility = addPickup ? Visibility.Visible : Visibility.Collapsed;
        SceneAddPickupMenu.IsEnabled = addPickup && sceneContextDocument!.SourceWorld is { IsRebuilding: false } && !sourceWorkspaceBusy;
        SceneGroupByTypeMenu.Visibility = addPickup && contextScene!.HasGroupByType ? Visibility.Visible : Visibility.Collapsed;
        SceneGroupByTypeMenu.IsChecked = pickupsByType;
        SceneGroupByTypeMenu.IsEnabled = SceneAddPickupMenu.IsEnabled;
        bool resetParts = contextScene?.Owner == sceneTree && contextScene?.Index is int partNode && sceneTree?.Projection is { } partProjection
            && partProjection.IsBoundary(partProjection.Owner(partNode)) && sceneContextDocument == shownDocument;
        SceneResetPartsMenu.Visibility = resetParts ? Visibility.Visible : Visibility.Collapsed;
        SceneResetPartsMenu.IsEnabled = resetParts && !sourceMapPartsBusy && !sourceWorkspaceBusy;
        if (sceneContextDocument?.SourceWorld != null)
        {
            // Source rows offer Rename, and the Pickups pass offers creation.
            ScenePropertiesMenu.Visibility = Visibility.Collapsed;
            bool rename = contextScene?.Authored is { } authored && CanRenameRemoved(sceneContextDocument, authored) && contextScene.Owner == sceneTree && sceneContextDocument == shownDocument;
            SceneRenameMenu.Visibility = rename ? Visibility.Visible : Visibility.Collapsed;
            SceneRenameMenu.IsEnabled = rename && sceneContextDocument.SourceWorld is { IsRebuilding: false } && !sourceWorkspaceBusy;
            // A definition or binding row: Open in Animation mode and Copy animation name.
            bool animationRow = contextScene is { CanOpenAnimation: true } && contextScene.Owner == sceneTree && sceneContextDocument == shownDocument;
            SceneOpenAnimationMenu.Visibility = SceneCopyAnimationMenu.Visibility = SceneFrameAnimationMenu.Visibility = SceneAnimationSourceMenu.Visibility = SceneAnimationPropertiesMenu.Visibility
                = animationRow ? Visibility.Visible : Visibility.Collapsed;
            if (animationRow) SceneFrameAnimationMenu.IsEnabled = contextScene!.CanFrame;
            if (!rename && !addPickup && !resetParts && !animationRow && sender is ContextMenu menu) menu.IsOpen = false;
            e.Handled = true; return;
        }
        SceneRenameMenu.Visibility = SceneOpenAnimationMenu.Visibility = SceneCopyAnimationMenu.Visibility = SceneFrameAnimationMenu.Visibility = SceneAnimationSourceMenu.Visibility = SceneAnimationPropertiesMenu.Visibility = Visibility.Collapsed;
        ScenePropertiesMenu.Visibility = Visibility.Visible;
        ScenePropertiesMenu.IsEnabled = contextScene is { Selectable: true } && contextScene.Owner == sceneTree;
    }
    private async void ScenePropertiesClick(object sender, RoutedEventArgs e)
    { if (contextScene is { } item && sceneContextDocument is { IsDisposed: false } doc && await ResolvePropertiesDraftsAsync()) await OpenScenePropertiesAsync(doc, item); }
}
