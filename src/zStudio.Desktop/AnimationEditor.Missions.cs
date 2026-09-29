using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public partial class AnimationEditor
{
    internal Func<string, Task>? MissionRequested { get; set; }
    internal string? Mw3WorldPath => context?.World.Game == GameVariant.MechWarrior3 ? context.World.Path : null;
    internal string? MissionArchive => context?.Mission?.Layout.MissionArchive;
    internal IReadOnlyList<MissionVariant> MissionChoices => Mission.Items.Cast<MissionVariant>().ToArray();
    private async Task ConfigureMissionsAsync(CancellationToken token)
    {
        var choices = Mw3WorldPath is { } path ? await MissionSceneLoader.Mw3MissionsAsync(path, resolver, token) : [];
        token.ThrowIfCancellationRequested(); if (disposed) return;
        bool wasChanging = changing; changing = true;
        try
        {
            Difficulty.Visibility = Mw3WorldPath == null ? Visibility.Visible : Visibility.Collapsed;
            Mission.Visibility = Mw3WorldPath != null ? Visibility.Visible : Visibility.Collapsed;
            Mission.ItemsSource = choices; Mission.SelectedItem = choices.FirstOrDefault(m => m.Archive.Equals(MissionArchive, StringComparison.OrdinalIgnoreCase));
            if (context?.Mission is { } mission && Mw3WorldPath is { } world) preferences?.AdoptMissionFallback(world, mission.Layout);
        }
        finally { changing = wasChanging; }
    }
    private void SynchronizeMissionSelection()
    {
        bool wasChanging = changing; changing = true;
        try
        {
            Mission.SelectedItem = MissionChoices.FirstOrDefault(m => m.Archive.Equals(MissionArchive, StringComparison.OrdinalIgnoreCase));
            if (context?.Mission is { } mission && Mw3WorldPath is { } world) preferences?.AdoptMissionFallback(world, mission.Layout);
        }
        finally { changing = wasChanging; }
    }
    private async void MissionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changing || disposed || MissionRequested == null || Mission.SelectedItem is not MissionVariant choice) return;
        try { await MissionRequested(choice.Archive); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Note(ex.Message); }
        finally { if (!disposed) SynchronizeMissionSelection(); }
    }
}
