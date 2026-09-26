using System.Windows;
using Recoil.Zbd.Core;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private sealed record StaticSceneOptions(int Lod, bool Horizon, PackChoice? Pack);
    private StaticSceneOptions? publishedStaticOptions;
    private CancellationTokenSource? staticRefresh;
    private StaticSceneOptions ReadStaticSceneOptions() => new(LodCombo.SelectedIndex, BackdropEnabled.IsChecked == true, TexturePackCombo.SelectedItem as PackChoice);
    private void RestoreStaticSceneOptions(StaticSceneOptions options)
    {
        bool wasUpdating = updating; updating = true;
        try { LodCombo.SelectedIndex = options.Lod; BackdropEnabled.IsChecked = options.Horizon; TexturePackCombo.SelectedItem = options.Pack; }
        finally { updating = wasUpdating; }
    }

    private async Task RefreshStaticSceneAsync(DocumentModel doc, AssetRecord asset)
    {
        var previous = scene!;
        var retainedOptions = publishedStaticOptions!;
        var requested = ReadStaticSceneOptions();
        var resolver = ViewModel.Resolver!;
        staticRefresh?.Cancel(); difficultyRefresh?.Cancel();
        using var request = PreviewOperation.Link(preview.Token);
        staticRefresh = request;
        var token = request.Token;
        SceneViewport? replacement = null;
        bool OwnsRequest() => staticRefresh == request && scene == previous && shownDocument == doc && shownAsset?.Id == asset.Id && !doc.IsDisposed;
        ViewModel.Status = "Updating preview…";
        try
        {
            token.ThrowIfCancellationRequested();
            var mission = asset.Kind == AssetKind.World ? await MissionSceneLoader.LoadAsync(doc.Document, resolver, token: token, difficulty: ViewModel.Difficulty) : null;
            if (mission != null) await doc.GetPickupEditsAsync(resolver, token);
            token.ThrowIfCancellationRequested();
            // Keep all partially built meshes off the displayed viewport. ShowAsync
            // yields during GPU object creation, so cancellation must not touch it.
            replacement = new SceneViewport();
            await replacement.ShowAsync(doc.Document, asset, resolver, requested.Pack?.Path, requested.Lod, token, requested.Horizon, mission);
            token.ThrowIfCancellationRequested();
            if (!OwnsRequest()) return;

            var view = previous.CaptureView();
            int? selection = selectedNode, isolate = isolatedNode;
            var pickup = selection is int s ? previous.PickupAt(s)?.Pickup?.Source : null;
            previous.CancelPickupDrag(); DetachPickupEditor();
            flyRequest++;
            scene = replacement; replacement = null;
            scene.Information += text => { PreviewInfo.Text = text; PreviewInfo.ToolTip = text; };
            scene.NodeSelected += InspectNode;
            ConfigurePickupScene(scene); ConfigureFlyScene(scene);
            SceneHost.Content = scene;
            ApplySceneOptions();
            if (mission != null) AttachPickupEditor(doc);
            scene.RestoreView(view);
            isolatedNode = mission != null && previous.Mission != null && isolate is int oldIsolate
                ? mission.RemapNodeFrom(previous.Mission, oldIsolate) is >= 0 and int mapped ? mapped : null : isolate;
            if (isolatedNode != null) scene.Isolate(isolatedNode);
            selectedNode = mission != null && previous.Mission != null
                ? RemapPickupSelection(pickup, mission) ?? (selection is int oldSelection && mission.RemapNodeFrom(previous.Mission, oldSelection) is >= 0 and int mappedSelection ? mappedSelection : null)
                : selection;
            if (selectedNode is int node) InspectNode(node);
            publishedStaticOptions = requested; previewId = Guid.NewGuid();
            PreviewInfo.Text = scene.PreviewSummary; PreviewInfo.ToolTip = scene.PreviewSummary;
            if (mission != null) WorldDifficulty.ToolTip = mission.Layout.Description;
            ShowStaticPreviewProblems(doc, asset);
            EmptyPreview.Visibility = Visibility.Collapsed;
            previous.Dispose(); SynchronizeFly();
            ViewModel.Status = mission?.Layout.Description ?? scene.PreviewSummary;
        }
        catch (OperationCanceledException)
        {
            if (OwnsRequest()) { RestoreStaticSceneOptions(retainedOptions); ViewModel.Status = "Preview refresh canceled; previous scene retained."; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (OwnsRequest()) { RestoreStaticSceneOptions(retainedOptions); ViewModel.Status = "Previous preview retained. " + ex.Message; ViewModel.AddProblem(ViewModel.Status); }
        }
        finally
        {
            replacement?.Dispose();
            if (staticRefresh == request) staticRefresh = null;
        }
    }
}
