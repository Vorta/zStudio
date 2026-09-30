using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Desktop;

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
    internal Func<ZbdDocument, AssetRecord, CancellationToken, Task<JsonObject>> LoadAssetPropertiesAsync { get; set; } =
        static (doc, asset, token) => Task.Run(() => ExportService.AssetJson(doc, asset, token, boundedZrd: true), token);
    internal PropertiesWindow? OpenPropertiesWindow => propertiesWindow;

    private PropertiesWindow GetPropertiesWindow()
    {
        if (propertiesWindow != null) return propertiesWindow;
        var window = new PropertiesWindow(this, ViewModel)
        {
            SaveRequested = SaveCurrentAsync,
            UndoRequested = UndoDocument,
            Editing = doc => { if (doc == shownDocument) animation?.Pause(); }
        };
        window.Closed += (_, _) => { if (propertiesWindow == window) { propertiesWindow = null; ++propertyRequest; } };
        propertiesWindow = window; return window;
    }
    private static void PresentProperties(PropertiesWindow window, bool accepted)
    {
        if (!accepted) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    internal bool ResolvePropertiesDrafts(DocumentModel? doc = null) => ResolveInspectionDrafts(doc) && (propertiesWindow == null || doc != null && propertiesWindow.Document != doc || propertiesWindow.ResolvePendingDrafts());
    internal async Task<bool> ResolvePropertiesDraftsAsync(DocumentModel? doc = null) => ResolveInspectionDrafts(doc) && (propertiesWindow == null || doc != null && propertiesWindow.Document != doc || await propertiesWindow.ResolvePendingDraftsAsync());
    private void UndoDocument(DocumentModel doc, bool redo)
    {
        if (doc.SourceWorld is { } world)
        {
            if (redo ? world.Edits.CanRedo : world.Edits.CanUndo) sourceWorldWork = RunUi(() => UndoSourceWorldAsync(doc, redo, CancellationToken.None));
            return;
        }
        if (doc.ContentEdits != null) { contentWork = UndoContentAsync(doc, redo); return; }
        if (doc.ResourceEdits != null) { resourceWork = UndoResourcesAsync(doc, redo); return; }
        if (doc.IsDisposed || !ResolvePropertiesDrafts(doc) || shownDocument == doc && animation?.ResolvePendingDrafts() == false) return;
        if (shownDocument == doc) { animation?.Pause(); scene?.CancelPickupDrag(); }
        if (doc.ModelEdits != null || doc.PickupEdits != null)
        { bool model = doc.NextSceneEditIsModel(redo); doc.UndoScene(redo); if (model) modelRefreshWork = RefreshModelDependentsAsync(doc); }
        else if (doc.AnimationEdits is { } edits) { if (redo) edits.Redo(); else edits.Undo(); }
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
            if (automation && propertiesWindow?.HasPendingDrafts == true)
                throw new StudioCommandException("pending_drafts", "Properties input changed while loading. Resolve drafts before retargeting.");
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
        if (scriptGrid.IsKeyboardFocusWithin && scriptGrid.SelectedItem is ScriptRow scriptRow && doc.SelectedAsset?.ResourceId is Guid script)
        { await OpenScriptPropertiesAsync(doc, script, scriptRow.Id); return; }
        if (CentralTree.IsKeyboardFocusWithin && CentralTree.SelectedItem is ResourceTreeItem resource && doc.SelectedAsset?.ResourceId is Guid member)
        { await OpenResourcePropertiesAsync(doc, member, resource.Node.Id); return; }
        if (AssetGrid.IsKeyboardFocusWithin && doc.SelectedAsset is { } asset) { await OpenAssetPropertiesAsync(doc, asset.Record); return; }
        if (DocumentSceneTree.IsKeyboardFocusWithin && sceneTree?.Selected is { Node: not null, Problem: null } selectedRow)
        { await OpenScenePropertiesAsync(doc, selectedRow); return; }
        if (inspectedSceneSource is { } item) { await OpenScenePropertiesAsync(doc, item); return; }
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
            bool opened = scene?.PickupAt(node)?.Pickup is { } pickup && doc.PickupEdits?.Find(pickup.Source) != null
                ? window.SetPickup(doc, pickup.Source, $"{name} · node #{node}", properties)
                : window.SetReadOnly(doc, $"{name} · node #{node}", properties);
            PresentProperties(window, opened);
        }
        else if (doc.SelectedAsset is { } selected) await OpenAssetPropertiesAsync(doc, selected.Record);
        else PresentProperties(window, window.SetReadOnly(doc, "Archive", doc.Document.Metadata));
    }
    private Task<PropertiesWindow?> OpenScenePropertiesAsync(DocumentModel doc, SceneTreeItem item, CancellationToken token = default, bool automation = false)
    {
        if (doc.IsDisposed || doc != sceneTreeDocument || item.Owner != sceneTree || item.Node is not { } node || item.Problem != null)
            return Task.FromResult<PropertiesWindow?>(null);
        if (!item.Owner.IsPreview && doc.PreviewDocument.Assets.FirstOrDefault(a => a.Kind == AssetKind.Node && a.Index == node.Index) is { } asset)
            return OpenAssetPropertiesAsync(doc, asset, token, automation);
        ++propertyRequest;
        var window = GetPropertiesWindow(); bool opened = window.SetReadOnly(doc, $"{node.Name} · node #{node.Index}", SceneTreeProperties(item));
        PresentProperties(window, opened); return Task.FromResult<PropertiesWindow?>(opened ? window : null);
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
        scenePointerContext = false; ScenePropertiesMenu.IsEnabled = contextScene is { Node: not null, Problem: null } && contextScene.Owner == sceneTree;
    }
    private async void ScenePropertiesClick(object sender, RoutedEventArgs e)
    { if (contextScene is { } item && sceneContextDocument is { IsDisposed: false } doc && await ResolvePropertiesDraftsAsync()) await OpenScenePropertiesAsync(doc, item); }
}
