using System.Windows;
using zStudio.Core;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    /// <param name="Tank">A source map's player tank choice (its session's), which the scene draws.</param>
    private sealed record StaticSceneOptions(int Lod, bool Horizon, PackChoice? Pack, MissionDifficulty Difficulty, string? Mission, PlayerTankState? Tank = null);
    private bool restoringStaticOptions;
    private StaticSceneOptions? publishedStaticOptions;
    private CancellationTokenSource? staticRefresh;
    private Task<Guid?>? staticRefreshWork;
    private long staticRefreshGeneration;
    private bool HasPublishedStaticScene => animation == null && scene?.PreviewScene != null && publishedStaticOptions != null &&
        SceneHost.Visibility == Visibility.Visible && shownAsset != null;
    private StaticSceneOptions ReadStaticSceneOptions() => new(LodCombo.SelectedIndex, BackdropEnabled.IsChecked == true, TexturePackCombo.SelectedItem as PackChoice, ViewModel.Difficulty, shownDocument == null ? null : ViewModel.Resolver?.SelectedMission(shownDocument.Path),
        shownDocument?.SourceWorld?.PlayerTank);
    private void RestoreStaticSceneOptions(StaticSceneOptions options)
    {
        bool wasUpdating = updating; updating = true; restoringStaticOptions = true;
        try
        {
            LodCombo.SelectedIndex = options.Lod; BackdropEnabled.IsChecked = options.Horizon; TexturePackCombo.SelectedItem = options.Pack;
            // Only a RECOIL layout restores the shared preference; an MW3 world without readers has no difficulty to restore.
            if (scene?.Mission is { Layout.DifficultyApplies: true } && options.Mission == null) ViewModel.Difficulty = options.Difficulty;
            // A choice the view did not take returns to the one it shows.
            if (shownDocument?.SourceWorld is { } session && session.PlayerTank != options.Tank) { session.PlayerTank = options.Tank; RestorePlayerTankRadios(); }
            if (options.Mission != null && shownDocument != null)
            {
                ViewModel.Resolver?.SelectMission(shownDocument.Path, options.Mission);
                WorldMission.SelectedItem = WorldMission.Items.Cast<MissionVariant>().FirstOrDefault(m => m.Archive.Equals(options.Mission, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally { updating = wasUpdating; restoringStaticOptions = false; }
    }

    private Task<Guid?> RefreshStaticSceneAsync(DocumentModel doc, AssetRecord asset, IReadOnlyDictionary<string, bool>? partOverrides = null)
    {
        if (doc.SourceWorld != null && (sourceWorkspaceBusy || doc.SourceWorld.IsRebuilding))
        {
            if (publishedStaticOptions != null) RestoreStaticSceneOptions(publishedStaticOptions);
            Notice("Wait for source evaluation before changing scene options.");
            return Task.FromResult<Guid?>(null);
        }
        if (!ResolveInspectionDrafts(doc)) { if (publishedStaticOptions != null) RestoreStaticSceneOptions(publishedStaticOptions); return Task.FromResult<Guid?>(null); }
        long generation = ++staticRefreshGeneration;
        var work = RefreshStaticSceneCoreAsync(doc, asset, partOverrides);
        // A synchronous status/binding callback may already have started a newer
        // refresh before the async core reaches its first await.
        if (generation == staticRefreshGeneration) staticRefreshWork = work;
        return work;
    }

    private async Task<Guid?> RefreshStaticSceneCoreAsync(DocumentModel doc, AssetRecord asset, IReadOnlyDictionary<string, bool>? partOverrides = null)
    {
        if (doc.SourceWorld != null) StopWorldAnimationForEdit();
        var previous = scene!;
        var retainedOptions = publishedStaticOptions!;
        var requested = ReadStaticSceneOptions();
        long inspectionRevision = doc.Revision;
        var resolver = ViewModel.Resolver!;
        staticRefresh?.Cancel();
        using var request = PreviewOperation.Link(preview.Token);
        staticRefresh = request;
        var token = request.Token;
        // The player tank's radios wait for this refresh unless it draws their choice.
        RefreshPlayerTankViews();
        SceneViewport? replacement = null;
        bool swapped = false;
        bool OwnsRequest() => staticRefresh == request && scene == previous && shownDocument == doc && shownAsset?.Id == asset.Id && !doc.IsDisposed;
        ViewModel.Status = "Updating preview…";
        try
        {
            token.ThrowIfCancellationRequested();
            // The mission is captured with the other options; a concurrent selection cannot change it mid-load.
            var mission = asset.Kind == AssetKind.World ? await MissionSceneLoader.LoadAsync(doc.Document, resolver, token: token, difficulty: requested.Difficulty,
                mission: requested.Mission, exactMission: ExactMissionFor(doc.Path, requested.Mission) != null, playerTank: requested.Tank) : null;
            if (mission != null) await doc.GetPickupEditsAsync(resolver, token);
            token.ThrowIfCancellationRequested();
            // Keep all partially built meshes off the displayed viewport. ShowAsync
            // yields during GPU object creation, so cancellation must not touch it.
            replacement = new SceneViewport();
            if (mission != null) await PrepareSourceMapSelectionAsync(doc, mission.Scene, mission, token);
            ConfigureSourceMapPresentation(replacement, doc, mission, partOverrides);
            await replacement.ShowAsync(doc.Document, asset, resolver, requested.Pack?.Path, requested.Lod, token, requested.Horizon, mission);
            token.ThrowIfCancellationRequested();
            if (!OwnsRequest()) return null;
            await RefreshAssetInspectionAsync(doc, asset, token);
            if (!OwnsRequest()) return null;
            if (doc.Revision != inspectionRevision || HasInspectionDraft || HasAiPathDraft || parachuteDraft != null)
                throw new InvalidOperationException("Preview refresh retained the current scene because a new edit or position draft arrived.");

            var view = previous.CaptureView();
            string? aiSelection = previous.SelectedAiNode;
            int? selection = selectedNode, isolate = isolatedNode;
            var hidden = hiddenRows.ToArray();
            var pickup = selection is int s ? previous.PickupAt(s)?.Pickup?.Source : null;
            int? Remap(int oldNode)
            {
                if (SourceSelection(doc, previous.PreviewScene) is { } before && SourceSelection(doc, mission?.Scene) is { } after)
                {
                    int owner = before.Owner(oldNode);
                    if (!before.Identities.TryGetValue(owner, out string? key)) return null;
                    var matchesByIdentity = after.Identities.Where(p => p.Value == key).Take(2).Select(p => p.Key).ToArray();
                    return matchesByIdentity.Length == 1 ? matchesByIdentity[0] : null;
                }
                var source = previous.ActorAt(oldNode)?.CoordinateSource;
                var matches = source != null && doc.PickupEdits?.Coordinate(source) != null ? doc.PickupEdits.Scope(source).Sources.ToHashSet(doc.PickupEdits.SourceComparer) : null;
                int mapped = mission!.RemapNodeFrom(previous.Mission!, oldNode, matches);
                return mapped >= 0 ? mapped : null;
            }
            var savedAnimation = CaptureWorldAnimationRebuild();
            previous.CancelPickupDrag(); DetachPickupEditor();
            flyRequest++;
            scene = replacement; replacement = null; swapped = true;
            PublishSourceMapParts(scene, doc);
            scene.Information += text => { PreviewInfo.Text = text; PreviewInfo.ToolTip = text; };
            WatchPreviewFailures(scene);
            scene.NodeSelected += InspectNode;
            ConfigureAiScene(scene);
            ConfigurePickupScene(scene); ConfigureFlyScene(scene);
            SceneHost.Content = scene;
            ApplySceneOptions();
            RefreshZoneToolScene();
            if (asset.Kind == AssetKind.World) AttachPickupEditor(doc);
            scene.SetValveOptions(previous.ValveOverlayVisible,
                mission?.Layout.MissionArchive == previous.Mission?.Layout.MissionArchive ? previous.ValveFilter : null);
            ApplyAiOptions();
            isolatedNode = isolatedGroup ? null : mission != null && previous.Mission != null && isolate is int oldIsolate
                ? Remap(oldIsolate) : isolate;
            ApplyIsolation(); RefreshRowEyes();
            RemapHidden(hidden, old => mission != null && previous.Mission != null ? Remap(old) : old);
            // A refresh retains the user's camera.
            scene.RestoreView(view);
            selectedNode = mission != null && previous.Mission != null
                ? RemapPickupSelection(pickup, mission) ?? (selection is int oldSelection ? Remap(oldSelection) : null)
                : selection;
            if (selectedNode is int node)
            {
                InspectNode(node);
                // A source map's tree-only selection (a light, a hidden object) has nothing to pick in the view: the card selects it.
                if (!scene.SelectInspectionNode(node) && doc.SourceWorld != null)
                    try { FocusSourceObjectCard(doc, node); } catch (zStudio.Automation.StudioCommandException) { }
            }
            if (aiSelection != null && previous.AiNetworks.Find(aiSelection) is { } oldAi && scene.AiNetworks.Find(aiSelection) is { } newAi &&
                oldAi.Node.SourceOffset == newAi.Node.SourceOffset) scene.SelectAiNode(aiSelection);
            publishedStaticOptions = requested with { Mission = mission?.Layout.MissionArchive ?? requested.Mission }; previewId = Guid.NewGuid();
            AdoptPlayerTank(doc, mission);
            ApplyTerrainBrush(scene);
            if (mission != null) ViewModel.AdoptMissionFallback(doc.Path, mission.Layout);
            PreviewInfo.Text = scene.PreviewSummary; PreviewInfo.ToolTip = scene.PreviewSummary;
            if (mission != null) WorldDifficulty.ToolTip = mission.Layout.Description;
            ShowStaticPreviewProblems(doc, asset);
            EmptyPreview.Visibility = Visibility.Collapsed;
            RefreshPresentation(previewSettled: true);
            RefreshSceneTree();
            RestoreWorldAnimationRebuild(savedAnimation);
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
            // Once swapped, the retired view goes even when publishing the new one failed: its devices and GPU buffers are native
            // (released only by Dispose, which is idempotent).
            if (swapped) previous.Dispose();
            if (staticRefresh == request) { staticRefresh = null; RefreshPlayerTankViews(); }
        }
        return null;
    }
}
