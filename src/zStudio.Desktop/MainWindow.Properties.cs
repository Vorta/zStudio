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
            Editing = doc => { if (doc == shownDocument) animation?.Pause(); },
            // Closing discards the zone draft (Closed below); it is decided first, so declining asks nothing more.
            ResolveClosingDrafts = ResolveZoneDrafts,
        };
        window.Closed += (_, _) => { if (presentationProperties == window) presentationProperties = null; if (propertiesWindow == window) { CancelZoneDraft(close: true); propertiesWindow = null; ++propertyRequest; } };
        window.Retargeted += UpdateSourceInputBlock;
        propertiesWindow = window; return window;
    }
    private void PresentProperties(PropertiesWindow window, bool accepted)
    {
        if (!accepted) return;
        using var lease = BeginLayoutChange();
        if (presentationUiHidden) { presentationProperties = window; if (window.IsVisible) window.Hide(); return; }
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    /// <summary>
    /// Refuses (MCP, without a dialog) while Properties holds unfinished input or a map zone draft, which retargeting or
    /// closing it would otherwise ask about or discard.
    /// </summary>
    private void RequireNoPropertiesDrafts(string message)
    {
        if (propertiesWindow?.HasPendingDrafts == true) throw new StudioCommandException("pending_drafts", message);
        if (HasZoneDraft) throw new StudioCommandException("pending_drafts", "Apply or cancel the map zone draft before continuing.");
    }
    internal bool ResolvePropertiesDrafts(DocumentModel? doc = null) => ResolveZoneDrafts(doc) && ResolveInspectionDrafts(doc) && (propertiesWindow == null || doc != null && propertiesWindow.Document != doc || propertiesWindow.ResolvePendingDrafts());
    internal async Task<bool> ResolvePropertiesDraftsAsync(DocumentModel? doc = null) => ResolveZoneDrafts(doc) && await ResolveInspectionDraftsAsync(doc) && (propertiesWindow == null || doc != null && propertiesWindow.Document != doc || await propertiesWindow.ResolvePendingDraftsAsync());
    private void UndoDocument(DocumentModel doc, bool redo)
    {
        doc = LiveDocument(doc);
        if (doc.SourceWorld is { } world)
        {
            if (world.IsRebuilding || sourceWorkspaceBusy) ViewModel.Status = "A world of this source project is still rebuilding; undo and redo are available when it is shown.";
            else if (redo ? world.Workspace.CanRedo : world.Workspace.CanUndo)
            {
                // An object's edit mode without a change does not hold history back.
                if (inspectionDraft is { ObjectEditing: true, HasObjectDraft: false, ObjectApplying: false } clean && clean.DraftDocument == doc) clean.CancelDraft();
                sourceWorldWork = RunUi(() => UndoSourceWorldAsync(doc, redo, CancellationToken.None));
            }
            return;
        }
        if (doc.ContentEdits != null) { contentWork = UndoContentAsync(doc, redo); return; }
        if (doc.ResourceEdits != null) { resourceWork = UndoResourcesAsync(doc, redo); return; }
        if (doc.IsDisposed || !ResolvePropertiesDrafts(doc) || shownDocument == doc && animation?.ResolvePendingDrafts() == false) return;
        if (shownDocument == doc) { animation?.Pause(); scene?.CancelPickupDrag(); }
        // Auxiliary coordinate inspection/history must not shadow an animation document's own
        // available steps. Coordinate history remains available when that primary direction is empty.
        if (doc.AnimationEdits is { } edits && (redo ? edits.CanRedo : edits.CanUndo)) { if (redo) edits.Redo(); else edits.Undo(); }
        else if (doc.ModelEdits != null || doc.PickupEdits != null)
        { bool model = doc.NextSceneEditIsModel(redo); doc.UndoScene(redo); if (model) modelRefreshWork = RefreshModelDependentsAsync(doc); }
        UpdateDocumentCommands();
    }
    internal PropertiesWindow? OpenAnimationProperties(DocumentModel doc, int entry, Guid sequence, Guid ev)
    {
        ++propertyRequest;
        if (doc.IsDisposed) return null;
        var window = GetPropertiesWindow(); bool accepted = window.SetAnimation(doc, entry, sequence, ev);
        PresentProperties(window, accepted); return accepted ? window : null;
    }
    internal async Task<PropertiesWindow?> OpenAssetPropertiesAsync(DocumentModel doc, AssetRecord asset, CancellationToken cancellationToken = default, bool automation = false)
    {
        Guid? resourceMember = doc.Assets.FirstOrDefault(row => ReferenceEquals(row.Record, asset))?.ResourceId;
        if (!automation && !await ResolvePropertiesDraftsAsync()) return null;
        long request = ++propertyRequest;
        if (doc.IsDisposed) return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (doc.ScriptEdits != null)
        {
            if (resourceMember == null) throw new StudioCommandException("stale_asset", "Read the current script identity before opening Properties.");
            return await OpenScriptPropertiesAsync(doc, resourceMember.Value, null, automation);
        }
        if (doc.ResourceEdits != null)
        {
            if (resourceMember == null) throw new StudioCommandException("stale_asset", "Read the current member identity before opening Properties.");
            bool valves = await UsesValvePropertiesAsync(doc, resourceMember.Value, cancellationToken);
            if (request != propertyRequest || doc.IsDisposed) return null; // A newer Properties request or closing superseded this one.
            return await OpenResourcePropertiesAsync(doc, resourceMember.Value, null, cancellationToken, automation, valves: valves);
        }
        if (asset.Kind == AssetKind.Animation && doc.AnimationEdits != null) return OpenAnimationProperties(doc, asset.Index, Guid.Empty, Guid.Empty);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, doc.Lifetime.Token);
            long revision = doc.Revision; var snapshot = doc.PreviewDocument;
            asset = snapshot.Assets.SingleOrDefault(a => a.Id == asset.Id) ?? throw new StudioCommandException("stale_asset", "This record is absent from the current edit.");
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
    private void PropertiesClick(object sender, RoutedEventArgs e) => OpenCurrentProperties();
    internal async void OpenCurrentProperties()
    {
        if (ViewModel.SelectedDocument is not { } doc) return;
        if (!await ResolvePropertiesDraftsAsync()) return;
        if (doc.SourceWorld != null && CurrentInspectionCard?.ObjectNode is int sourceObject) { FocusSourceObjectCard(doc, sourceObject); return; }
        if (scriptGrid.IsKeyboardFocusWithin && scriptGrid.SelectedItem is ScriptRow scriptRow && doc.SelectedAsset?.ResourceId is Guid script)
        { await OpenScriptPropertiesAsync(doc, script, scriptRow.Id); return; }
        if (CentralTree.IsKeyboardFocusWithin && CentralTree.SelectedItem is ResourceTreeItem resource && doc.SelectedAsset?.ResourceId is Guid member)
        { await OpenResourcePropertiesAsync(doc, member, resource.Node.Id); return; }
        if (AssetGrid.IsKeyboardFocusWithin && doc.SelectedAsset is { } asset) { await OpenAssetPropertiesAsync(doc, asset.Record); return; }
        if (DocumentSceneTree.IsKeyboardFocusWithin && sceneTree?.Selected is { Selectable: true } selectedRow)
        { await OpenScenePropertiesAsync(doc, selectedRow); return; }
        if (inspectedSceneSource is { } item) { await OpenScenePropertiesAsync(doc, item); return; }
        if (IsAiWorld && scene?.AiPathEditingNetwork != null) { await OpenAiPathPropertiesAsync(); return; }
        if (IsAiWorld && scene?.SelectedAiNode is { } aiNode) { await OpenAiPropertiesAsync(aiNode, false); return; }
        if (animation is { } editor)
        {
            var target = editor.PropertySelection; OpenAnimationProperties(doc, editor.EntryIndex, target.Sequence, target.Event); return;
        }
        ++propertyRequest;
        var window = GetPropertiesWindow();
        if (selectedNode is int node && properties != null)
        {
            string name = (motion?.Viewport.PreviewScene ?? scene?.PreviewScene ?? doc.Document.Scene)?.Nodes.ElementAtOrDefault(node)?.Name ?? "Scene object";
            // A source world's own objects (not placed copies) edit their sources.
            if (doc.SourceWorld != null && scene?.PickupAt(node) == null && SourceObjectNode(node) is int sourceNode && doc.SourceBuild?.Provenance.ContainsKey(sourceNode) == true)
            { await ShowSourceObjectPropertiesAsync(doc, sourceNode); return; }
            bool opened = scene?.PickupAt(node)?.Pickup is { } pickup && doc.PickupEdits?.Find(pickup.Source) != null
                ? window.SetPickup(doc, pickup.Source, $"{name} · node #{node}", properties, SourcePickupMove(doc))
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
            if (item.Index is int sourceIndex) FocusSourceObjectCard(doc, sourceIndex);
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
        if (!item.Owner.IsPreview && doc.PreviewDocument.Assets.FirstOrDefault(a => a.Kind == AssetKind.Node && a.Index == node.Index) is { } asset)
            return await OpenAssetPropertiesAsync(doc, asset, token, automation);
        ++propertyRequest;
        if (doc.SourceWorld != null && scene?.Mission?.ActorAt(node.Index) is { } actor)
        {
            var actorWindow = GetPropertiesWindow();
            bool shown = actor.Pickup is { } pickup && doc.PickupEdits?.Find(pickup.Source) != null
                ? actorWindow.SetPickup(doc, pickup.Source, item.DisplayName, SceneTreeProperties(item), SourcePickupMove(doc))
                : actorWindow.SetReadOnly(doc, item.DisplayName, SceneTreeProperties(item));
            PresentProperties(actorWindow, shown); return shown ? actorWindow : null;
        }
        if (doc.SourceWorld != null && doc.SourceBuild?.Provenance.ContainsKey(node.Index) == true)
            return await ShowSourceObjectPropertiesAsync(doc, node.Index, token) ? propertiesWindow : null;
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
        if (doc.ResourceEdits != null && asset.ResourceId is Guid member)
            await ResourceUiAsync(async () => { bool valves = await UsesValvePropertiesAsync(doc, member, doc.Lifetime.Token); if (!doc.IsDisposed) await OpenResourcePropertiesAsync(doc, member, null, valves: valves); });
        else if (doc.ScriptEdits != null && asset.ResourceId is Guid script) await ResourceUiAsync(async () => { await OpenScriptPropertiesAsync(doc, script, null); });
        else await OpenAssetPropertiesAsync(doc, asset.Record);
    }
    private void SceneContextTarget(object sender, MouseButtonEventArgs e)
    {
        contextScene = PropertyContext.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as SceneTreeItem;
        sceneContextDocument = ViewModel.SelectedDocument; scenePointerContext = true; e.Handled = true;
    }
    private void SceneContextOpening(object sender, RoutedEventArgs e)
    {
        if (!scenePointerContext) { contextScene = DocumentSceneTree.SelectedItem as SceneTreeItem; sceneContextDocument = ViewModel.SelectedDocument; }
        scenePointerContext = false;
        if (sceneContextDocument?.SourceWorld != null)
        {
            // Source worlds offer only Rename, for the source-only rows whose names the build removes.
            ScenePropertiesMenu.Visibility = Visibility.Collapsed;
            bool rename = contextScene?.Authored is { } authored && CanRenameRemoved(sceneContextDocument, authored) && contextScene.Owner == sceneTree && sceneContextDocument == shownDocument;
            SceneRenameMenu.Visibility = rename ? Visibility.Visible : Visibility.Collapsed;
            SceneRenameMenu.IsEnabled = rename && sceneContextDocument.SourceWorld is { IsRebuilding: false } && !sourceWorkspaceBusy;
            if (!rename && sender is ContextMenu menu) menu.IsOpen = false;
            e.Handled = true; return;
        }
        SceneRenameMenu.Visibility = Visibility.Collapsed;
        ScenePropertiesMenu.Visibility = Visibility.Visible;
        ScenePropertiesMenu.IsEnabled = contextScene is { Selectable: true } && contextScene.Owner == sceneTree;
    }
    private async void ScenePropertiesClick(object sender, RoutedEventArgs e)
    { if (contextScene is { } item && sceneContextDocument is { IsDisposed: false } doc && await ResolvePropertiesDraftsAsync()) await OpenScenePropertiesAsync(doc, item); }
}
