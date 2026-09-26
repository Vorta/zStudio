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
        static (doc, asset, token) => Task.Run(() => ExportService.AssetJson(doc, asset, token), token);
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
    internal bool ResolvePropertiesDrafts(DocumentModel? doc = null) => propertiesWindow == null || doc != null && propertiesWindow.Document != doc || propertiesWindow.ResolvePendingDrafts();
    private void UndoDocument(DocumentModel doc, bool redo)
    {
        if (doc.IsDisposed || !ResolvePropertiesDrafts(doc) || shownDocument == doc && animation?.ResolvePendingDrafts() == false) return;
        if (shownDocument == doc) { animation?.Pause(); scene?.CancelPickupDrag(); }
        if (doc.PickupEdits is { } pickup) { if (redo) pickup.Redo(); else pickup.Undo(); }
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
        long request = ++propertyRequest;
        if (doc.IsDisposed) return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (asset.Kind == AssetKind.Animation && doc.AnimationEdits != null) return OpenAnimationProperties(doc, asset.Index, Guid.Empty, Guid.Empty);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, doc.Lifetime.Token);
            var json = await LoadAssetPropertiesAsync(doc.Document, asset, cancellation.Token);
            if (doc.IsDisposed || request != propertyRequest) return null;
            cancellation.Token.ThrowIfCancellationRequested();
            if (automation && propertiesWindow?.HasPendingDrafts == true)
                throw new StudioCommandException("pending_drafts", "Properties input changed while loading. Resolve drafts before retargeting.");
            var window = GetPropertiesWindow(); bool accepted = window.SetReadOnly(doc, $"{asset.Name} · {asset.Kind} #{asset.Index}", json);
            PresentProperties(window, accepted);
            return accepted && request == propertyRequest && propertiesWindow == window && window.Document == doc ? window : null;
        }
        catch (OperationCanceledException) when (doc.IsDisposed || request != propertyRequest) { return null; }
        catch (OperationCanceledException) when (!automation) { }
        catch (Exception ex) when (!automation && ex is IOException or InvalidDataException or ArgumentException) { Report(ex); }
        return null;
    }
    private void PropertiesClick(object sender, RoutedEventArgs e) => OpenCurrentProperties();
    internal async void OpenCurrentProperties()
    {
        if (ViewModel.SelectedDocument is not { } doc) return;
        if (AssetGrid.IsKeyboardFocusWithin && doc.SelectedAsset is { } asset) { await OpenAssetPropertiesAsync(doc, asset.Record); return; }
        if (inspectedSceneSource is { } item) { OpenSceneProperties(doc, item); return; }
        if (animation is { } editor)
        {
            var target = editor.PropertySelection; OpenAnimationProperties(doc, editor.EntryIndex, target.Sequence, target.Event); return;
        }
        ++propertyRequest;
        var window = GetPropertiesWindow();
        if (selectedNode is int node && properties != null)
        {
            string name = (scene?.PreviewScene ?? doc.Document.Scene)?.Nodes.ElementAtOrDefault(node)?.Name ?? "Scene object";
            bool opened = scene?.PickupAt(node)?.Pickup is { } pickup && doc.PickupEdits?.Find(pickup.Source) != null
                ? window.SetPickup(doc, pickup.Source, $"{name} · node #{node}", properties)
                : window.SetReadOnly(doc, $"{name} · node #{node}", properties);
            PresentProperties(window, opened);
        }
        else if (doc.SelectedAsset is { } selected) await OpenAssetPropertiesAsync(doc, selected.Record);
        else PresentProperties(window, window.SetReadOnly(doc, "Archive", doc.Document.Metadata));
    }
    private void OpenSceneProperties(DocumentModel doc, SceneTreeItem item)
    {
        ++propertyRequest;
        var window = GetPropertiesWindow();
        PresentProperties(window, window.SetReadOnly(doc, $"{item.Node.Name} · node #{item.Node.Index}", item.Node.Metadata));
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
    { if (contextAsset is { } asset && assetContextDocument is { IsDisposed: false } doc) await OpenAssetPropertiesAsync(doc, asset.Record); }
    private void SceneContextTarget(object sender, MouseButtonEventArgs e)
    {
        contextScene = PropertyContext.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as SceneTreeItem;
        sceneContextDocument = ViewModel.SelectedDocument; scenePointerContext = true; e.Handled = true;
    }
    private void SceneContextOpening(object sender, RoutedEventArgs e)
    {
        if (!scenePointerContext) { contextScene = DocumentSceneTree.SelectedItem as SceneTreeItem; sceneContextDocument = ViewModel.SelectedDocument; }
        scenePointerContext = false; ScenePropertiesMenu.IsEnabled = contextScene != null;
    }
    private void ScenePropertiesClick(object sender, RoutedEventArgs e)
    { if (contextScene is { } item && sceneContextDocument is { IsDisposed: false } doc) OpenSceneProperties(doc, item); }
}
