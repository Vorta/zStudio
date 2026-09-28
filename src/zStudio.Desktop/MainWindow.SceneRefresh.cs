using System.Windows;
using Recoil.Zbd.Core;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private sealed record StaticSceneOptions(int Lod, bool Horizon, PackChoice? Pack, MissionDifficulty Difficulty);
    private bool restoringStaticOptions;
    private StaticSceneOptions? publishedStaticOptions;
    private CancellationTokenSource? staticRefresh;
    private Task<Guid?>? staticRefreshWork;
    private long staticRefreshGeneration;
    private bool HasPublishedStaticScene => animation == null && scene?.PreviewScene != null && publishedStaticOptions != null &&
        SceneHost.Visibility == Visibility.Visible && shownAsset != null;
    private StaticSceneOptions ReadStaticSceneOptions() => new(LodCombo.SelectedIndex, BackdropEnabled.IsChecked == true, TexturePackCombo.SelectedItem as PackChoice, ViewModel.Difficulty);
    private void RestoreStaticSceneOptions(StaticSceneOptions options)
    {
        bool wasUpdating = updating; updating = true; restoringStaticOptions = true;
        try
        {
            LodCombo.SelectedIndex = options.Lod; BackdropEnabled.IsChecked = options.Horizon; TexturePackCombo.SelectedItem = options.Pack;
            if (scene?.Mission != null) ViewModel.Difficulty = options.Difficulty;
        }
        finally { updating = wasUpdating; restoringStaticOptions = false; }
    }

    private Task<Guid?> RefreshStaticSceneAsync(DocumentModel doc, AssetRecord asset)
    {
        if (!ResolveInspectionDrafts(doc)) { if (publishedStaticOptions != null) RestoreStaticSceneOptions(publishedStaticOptions); return Task.FromResult<Guid?>(null); }
        long generation = ++staticRefreshGeneration;
        var work = RefreshStaticSceneCoreAsync(doc, asset);
        // A synchronous status/binding callback may already have started a newer
        // refresh before the async core reaches its first await.
        if (generation == staticRefreshGeneration) staticRefreshWork = work;
        return work;
    }

    private async Task<Guid?> RefreshStaticSceneCoreAsync(DocumentModel doc, AssetRecord asset)
    {
        var previous = scene!;
        var retainedOptions = publishedStaticOptions!;
        var requested = ReadStaticSceneOptions();
        long inspectionRevision = doc.Revision;
        var resolver = ViewModel.Resolver!;
        staticRefresh?.Cancel();
        using var request = PreviewOperation.Link(preview.Token);
        staticRefresh = request;
        var token = request.Token;
        SceneViewport? replacement = null;
        bool OwnsRequest() => staticRefresh == request && scene == previous && shownDocument == doc && shownAsset?.Id == asset.Id && !doc.IsDisposed;
        ViewModel.Status = "Updating preview…";
        try
        {
            token.ThrowIfCancellationRequested();
            var mission = asset.Kind == AssetKind.World ? await MissionSceneLoader.LoadAsync(doc.PreviewDocument, resolver, token: token, difficulty: requested.Difficulty) : null;
            if (mission != null) await doc.GetPickupEditsAsync(resolver, token);
            token.ThrowIfCancellationRequested();
            // Keep all partially built meshes off the displayed viewport. ShowAsync
            // yields during GPU object creation, so cancellation must not touch it.
            replacement = new SceneViewport();
            await replacement.ShowAsync(doc.PreviewDocument, asset, resolver, requested.Pack?.Path, requested.Lod, token, requested.Horizon, mission);
            token.ThrowIfCancellationRequested();
            if (!OwnsRequest()) return null;
            await RefreshAssetInspectionAsync(doc, asset, token);
            if (!OwnsRequest()) return null;
            if (doc.Revision != inspectionRevision || HasInspectionDraft)
                throw new InvalidOperationException("Preview refresh retained the current scene because a new edit or position draft arrived.");

            var view = previous.CaptureView();
            string? aiSelection = previous.SelectedAiNode;
            int? selection = selectedNode, isolate = isolatedNode;
            var pickup = selection is int s ? previous.PickupAt(s)?.Pickup?.Source : null;
            int Remap(int oldNode)
            {
                var source = previous.ActorAt(oldNode)?.CoordinateSource;
                var matches = source != null && doc.PickupEdits?.Coordinate(source) != null ? doc.PickupEdits.Scope(source).Sources.ToHashSet() : null;
                return mission!.RemapNodeFrom(previous.Mission!, oldNode, matches);
            }
            previous.CancelPickupDrag(); DetachPickupEditor();
            flyRequest++;
            scene = replacement; replacement = null;
            scene.Information += text => { PreviewInfo.Text = text; PreviewInfo.ToolTip = text; };
            scene.NodeSelected += InspectNode;
            ConfigureAiScene(scene);
            ConfigurePickupScene(scene); ConfigureFlyScene(scene);
            SceneHost.Content = scene;
            ApplySceneOptions();
            if (mission != null) AttachPickupEditor(doc);
            scene.RestoreView(view);
            isolatedNode = mission != null && previous.Mission != null && isolate is int oldIsolate
                ? Remap(oldIsolate) is >= 0 and int mapped ? mapped : null : isolate;
            if (isolatedNode != null) scene.Isolate(isolatedNode);
            selectedNode = mission != null && previous.Mission != null
                ? RemapPickupSelection(pickup, mission) ?? (selection is int oldSelection && Remap(oldSelection) is >= 0 and int mappedSelection ? mappedSelection : null)
                : selection;
            if (selectedNode is int node) { InspectNode(node); scene.SelectInspectionNode(node); }
            if (aiSelection != null && previous.AiNetworks.Find(aiSelection) is { } oldAi && scene.AiNetworks.Find(aiSelection) is { } newAi &&
                oldAi.Node.SourceOffset == newAi.Node.SourceOffset) scene.SelectAiNode(aiSelection);
            publishedStaticOptions = requested; previewId = Guid.NewGuid();
            PreviewInfo.Text = scene.PreviewSummary; PreviewInfo.ToolTip = scene.PreviewSummary;
            if (mission != null) WorldDifficulty.ToolTip = mission.Layout.Description;
            ShowStaticPreviewProblems(doc, asset);
            EmptyPreview.Visibility = Visibility.Collapsed;
            RefreshSceneTree();
            if (selectedNode is int retainedNode) RevealSceneNode(retainedNode);
            previous.Dispose(); SynchronizeFly();
            ViewModel.Status = mission?.Layout.Description ?? scene.PreviewSummary;
            return previewId;
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
        return null;
    }
}
