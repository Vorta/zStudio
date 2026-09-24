using System.ComponentModel;
using System.Windows;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private async void DifficultyPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Difficulty)) await RefreshWorldDifficultyAsync();
    }
    private async Task RefreshWorldDifficultyAsync()
    {
        if (!ready || SceneHost.Visibility != Visibility.Visible || scene?.Mission == null ||
            ViewModel.SelectedDocument is not { } doc || ViewModel.Resolver is not { } resolver || shownAsset?.Kind != AssetKind.World) return;
        difficultyRefresh?.Cancel(); difficultyRefresh?.Dispose();
        difficultyRefresh = CancellationTokenSource.CreateLinkedTokenSource(preview.Token, doc.Lifetime.Token);
        var token = difficultyRefresh.Token; var current = scene; var asset = shownAsset;
        pickupPanel.CommitPending(); current.CancelPickupDrag();
        var previous = current.Mission; var view = current.CaptureView(); int? selection = selectedNode, isolate = isolatedNode;
        var selectedPickup = selection is int s ? current.PickupAt(s)?.Pickup?.Source : null;
        var difficulty = ViewModel.Difficulty;
        PickupTools.IsEnabled = SceneHost.IsEnabled = PickupProperties.IsEnabled = false;
        ViewModel.Status = $"Loading {difficulty} mission layout…";
        try
        {
            await doc.GetPickupEditsAsync(resolver, token);
            var mission = await MissionSceneLoader.LoadAsync(doc.Document, resolver, token: token, difficulty: difficulty);
            token.ThrowIfCancellationRequested();
            await current.ShowAsync(doc.Document, asset, resolver, PreferredPack, LodCombo.SelectedIndex, token, BackdropEnabled.IsChecked == true, mission);
            token.ThrowIfCancellationRequested(); ApplySceneOptions(); PickupEditsChanged();
            isolatedNode = isolate is int isolated && mission.RemapNodeFrom(previous, isolated) is >= 0 and int nextIsolate ? nextIsolate : null;
            if (isolatedNode != null) current.Isolate(isolatedNode);
            current.RestoreView(view);
            selectedNode = RemapPickupSelection(selectedPickup, mission) ?? (selection is int index && mission.RemapNodeFrom(previous, index) is >= 0 and int mapped ? mapped : null);
            if (selectedNode is int node) InspectNode(node); else SetProperties(doc.Document.Metadata);
            WorldDifficulty.ToolTip = mission.Layout.Description;
            PreviewInfo.Text = mission.Layout.Description + " · " + PreviewInfo.Text;
            ViewModel.Status = mission.Layout.Description;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (!token.IsCancellationRequested)
            {
                ViewModel.Status = $"Preview was not updated; retaining {previous.Layout.Label}. {ex.Message}";
                ViewModel.Diagnostics.Add(ViewModel.Status);
            }
        }
        finally
        {
            if (difficultyRefresh?.Token == token) PickupTools.IsEnabled = SceneHost.IsEnabled = PickupProperties.IsEnabled = true;
        }
    }
}
