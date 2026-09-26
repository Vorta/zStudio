using System.ComponentModel;
using System.Windows;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private async void DifficultyPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!restoringStaticOptions && e.PropertyName == nameof(MainViewModel.Difficulty))
            await (previewWork = RefreshWorldDifficultyAsync());
    }
    private Task RefreshWorldDifficultyAsync()
    {
        if (!ready || SceneHost.Visibility != Visibility.Visible || scene?.Mission == null || publishedStaticOptions == null ||
            ViewModel.SelectedDocument is not { } doc || ViewModel.Resolver == null || shownAsset?.Kind != AssetKind.World)
            return Task.CompletedTask;
        return RefreshStaticSceneAsync(doc, shownAsset);
    }
}
