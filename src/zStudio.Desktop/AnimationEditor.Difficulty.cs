using System.ComponentModel;
using System.Windows.Controls;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public partial class AnimationEditor
{
    private void PreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Difficulty) && preferences != null)
            Difficulty.SelectedItem = preferences.Difficulty;
    }
    private async void DifficultyChanged(object sender, SelectionChangedEventArgs e) => await (optionWork = DifficultyChangedAsync(sender, e));
    private async Task DifficultyChangedAsync(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changing || disposed) return;
        if (preferences != null) preferences.Difficulty = SelectedDifficulty;
        contextDirty = true; resetSimulation = true; pendingPlay |= playing;
        if (context == null) await InitializeAsync();
        else await SeekAsync(frame?.Time ?? 0, preservePlayhead: true);
    }
}
