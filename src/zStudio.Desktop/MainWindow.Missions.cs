using System.IO;
using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private long missionRequest;
    private string? MissionWorldPath => animation?.Mw3WorldPath ?? (shownAsset?.Kind == AssetKind.World && shownDocument?.PreviewDocument.Game == GameVariant.MechWarrior3 ? shownDocument.Path : null);
    private async Task PopulateWorldMissionsAsync(DocumentModel doc, CancellationToken token)
    {
        if (doc.PreviewDocument.Game != GameVariant.MechWarrior3 || ViewModel.Resolver == null) return;
        var choices = await MissionSceneLoader.Mw3MissionsAsync(doc.Path, ViewModel.Resolver, token);
        token.ThrowIfCancellationRequested(); bool wasUpdating = updating; updating = true;
        try
        {
            WorldMission.ItemsSource = choices; WorldMission.SelectedItem = choices.FirstOrDefault(m => m.Archive == ViewModel.Resolver.SelectedMission(doc.Path));
            WorldMission.Visibility = Visibility.Visible;
        }
        finally { updating = wasUpdating; }
    }
    private async void WorldMissionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updating || restoringStaticOptions || WorldMission.SelectedItem is not MissionVariant choice) return;
        try { await SelectMissionAsync(choice.Archive, false, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); }
        finally
        {
            if (scene?.Mission?.Layout.MissionArchive is { } current)
            {
                bool wasUpdating = updating; updating = true;
                try { WorldMission.SelectedItem = WorldMission.Items.Cast<MissionVariant>().FirstOrDefault(m => m.Archive == current); }
                finally { updating = wasUpdating; }
            }
        }
    }
    private async Task SelectMissionAsync(string archive, bool automation, CancellationToken token)
    {
        var doc = shownDocument ?? throw new StudioCommandException("not_ready", "Open a world or animation.");
        string path = MissionWorldPath ?? throw new StudioCommandException("unsupported", "Mission selection requires a MechWarrior 3 world or animation.");
        var resolver = ViewModel.Resolver!;
        var editor = animation; var asset = shownAsset;
        if (automation) RequireNoDrafts();
        else if (!await ResolvePropertiesDraftsAsync() || animation?.ResolvePendingDrafts() == false)
        {
            if (animation == null && publishedStaticOptions != null) RestoreStaticSceneOptions(publishedStaticOptions);
            return;
        }
        long request = ++missionRequest;
        var choices = await MissionSceneLoader.Mw3MissionsAsync(path, resolver, token);
        ValidateContext(); token.ThrowIfCancellationRequested();
        var choice = choices.SingleOrDefault(m => m.Archive.Equals(archive, StringComparison.OrdinalIgnoreCase)) ?? throw new StudioCommandException("invalid_argument", "Choose a reader returned by missions for this map.");
        string? previous = editor?.MissionArchive ?? scene?.Mission?.Layout.MissionArchive ?? resolver.SelectedMission(path);
        // A resolver selection can describe an in-flight request. Only a presented
        // mission is a completed no-op; retrying a pending selection must await it.
        if (previous == choice.Archive && resolver.SelectedMission(path) == choice.Archive) return;
        resolver.SelectMission(path, choice.Archive);
        foreach (var open in ViewModel.Documents) open.InvalidateMissionContext();
        try
        {
            token.ThrowIfCancellationRequested();
            if (editor != null)
            {
                await editor.RefreshModelContextAsync(resourceChanges: true);
                ValidateContext();
                if (editor.MissionArchive != choice.Archive) throw new StudioCommandException("preview_failed", "Mission preview could not be rebuilt; the previous mission is retained.");
            }
            else
            {
                var published = await RefreshStaticSceneAsync(doc, asset!);
                ValidateContext();
                if (published is not Guid) throw new StudioCommandException("preview_failed", "Mission preview could not be rebuilt; the previous mission is retained.");
            }
            ValidateContext();
            ViewModel.Settings.Mw3Missions ??= new(StringComparer.OrdinalIgnoreCase);
            if (ViewModel.Settings.Mw3Missions.Count >= 128 && !ViewModel.Settings.Mw3Missions.ContainsKey(path)) ViewModel.Settings.Mw3Missions.Remove(ViewModel.Settings.Mw3Missions.Keys.First());
            ViewModel.Settings.Mw3Missions[path] = choice.Archive; ViewModel.Settings.Save();
            if (editor == null) { bool prior = updating; updating = true; WorldMission.SelectedItem = WorldMission.Items.Cast<MissionVariant>().FirstOrDefault(m => m.Archive == choice.Archive); updating = prior; }
        }
        catch
        {
            if (OwnsContext() && previous != null)
            {
                resolver.SelectMission(path, previous); foreach (var open in ViewModel.Documents) open.InvalidateMissionContext();
                if (editor == null && publishedStaticOptions != null) RestoreStaticSceneOptions(publishedStaticOptions);
            }
            throw;
        }
        bool OwnsContext() => request == missionRequest && shownDocument == doc && shownAsset == asset && !doc.IsDisposed &&
            MissionWorldPath == path && ViewModel.Resolver == resolver && animation == editor;
        void ValidateContext()
        {
            if (!OwnsContext()) throw new StudioCommandException("context_changed", "Mission selection was superseded.");
        }
    }
    private void RegisterMissionCommands(StudioCommands registry)
    {
        RegisterJob(registry, "missions", "List the current MW3 map's authored mission readers, or select one for the visible world/animation. Selection waits for the displayed preview, retains archive edit history, playhead and camera, and rejects superseded requests with context_changed. Shared resources remain available; other mission readers do not contribute actors or AI networks.",
            [PreviewParameter, P("archive", "string", "Optional full reader path returned by this tool. Omit to list."), .. PageParameters], false, async (a, token) =>
        {
            RequirePreview(a); string path = MissionWorldPath ?? throw new StudioCommandException("unsupported", "Select a MechWarrior 3 world or animation.");
            var choices = await MissionSceneLoader.Mw3MissionsAsync(path, ViewModel.Resolver!, token); RequirePreview(a);
            if (a.ContainsKey("archive")) await SelectMissionAsync(Text(a, "archive"), true, token);
            return Result(new { preview = previewId, world = path, selected = ViewModel.Resolver!.SelectedMission(path), missions = Page(choices, a, m => m.Label + " " + m.Archive).Data });
        });
    }
}
