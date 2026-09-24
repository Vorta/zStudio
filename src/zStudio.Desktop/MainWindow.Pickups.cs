using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Recoil.Zbd.Core;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private DocumentModel? pickupDocument;
    private void ConfigurePickupScene(SceneViewport viewport)
    {
        viewport.CanStartPickupEdit = () => ResolvePropertiesDrafts(pickupDocument);
        viewport.PickupMoveCommitted += (root, position) =>
        {
            if (pickupDocument?.PickupEdits is { } edits && viewport.PickupAt(root)?.Pickup is { } pickup)
                CommitPickupPosition(edits, pickup.Source, position);
        };
    }
    private void DetachPickupEditor()
    {
        scene?.CancelPickupDrag();
        if (pickupDocument != null) pickupDocument.PickupEditsChanged -= PickupEditsChanged;
        pickupDocument = null; PickupTools.Visibility = Visibility.Collapsed;
        PickupTools.IsEnabled = SceneHost.IsEnabled = true;
    }
    private void AttachPickupEditor(DocumentModel document)
    {
        pickupDocument = document; document.PickupEditsChanged += PickupEditsChanged;
        updating = true; PickupLocked.IsChecked = document.PickupsLocked; updating = false;
        PickupTools.Visibility = Visibility.Visible; PickupEditsChanged();
        if (!document.PickupDiagnosticsReported && document.PickupEdits is { } edits)
        {
            foreach (string note in edits.Diagnostics) ViewModel.AddProblem("Pickup editor: " + note,"Warning",document.Path);
            document.PickupDiagnosticsReported = true;
        }
    }
    private void PickupEditsChanged()
    {
        if (pickupDocument?.PickupEdits is not { } edits || scene?.Mission is not { } mission) return;
        scene.SetPickupPositions(edits.PreviewPositions(mission));
        UpdateDocumentCommands();
        if (selectedNode is int node && scene.PickupAt(node) != null && properties != null)
        {
            properties["preview_world_position"] = JsonData.Vector(scene.PickupPosition(scene.PickupAt(node)!.Root));
            SetProperties(properties);
        }
    }
    private void PickupLockedChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || updating || pickupDocument == null) return;
        if (!ResolvePropertiesDrafts(pickupDocument)) { updating = true; PickupLocked.IsChecked = pickupDocument.PickupsLocked; updating = false; return; }
        pickupDocument.PickupsLocked = PickupLocked.IsChecked == true;
        scene?.SetPickupLocked(pickupDocument.PickupsLocked);
    }
    private void CommitPickupPosition(PickupPlacementEditSession edits, MissionPickupSource source, Vector3 position)
    {
        try
        {
            if (edits.MoveTo(source, position)) ViewModel.Status = $"Moved {edits.Find(source)!.Type} · {edits.Scope(source).Description} · unsaved";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { Report(ex); PickupEditsChanged(); }
    }
    private async void SaveCurrentClick(object sender, RoutedEventArgs e)
    { if (ViewModel.SelectedDocument is { } doc) await SaveCurrentAsync(doc); }
    private async void SaveCurrentAsClick(object sender, RoutedEventArgs e)
    { if (ViewModel.SelectedDocument is { } doc) await SaveCurrentAsync(doc, true); }
    private Task<bool> SaveCurrentAsync(DocumentModel document, bool saveAs = false) => document.PickupEdits is { Records.Count: > 0 }
        ? SavePickupsAsync(document, saveAs) : SaveAnimationAsync(document);
    private void BackupOnSaveChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        ViewModel.Settings.CreateBackupOnSave = BackupOnSave.IsChecked;
        try { ViewModel.Settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report(ex); }
    }
    private async Task<bool> SavePickupsAsync(DocumentModel document, bool saveAs)
    {
        if (document.PickupEdits is not { } edits || !ResolvePropertiesDrafts(document)) return false;
        System.Windows.Input.Keyboard.ClearFocus(); scene?.CancelPickupDrag();
        if (!saveAs && !edits.IsDirty) return true;
        try
        {
            Dictionary<string, string>? destinations = null;
            foreach (string source in edits.ArchivePaths)
            {
                if (!saveAs && !edits.IsArchiveDirty(source)) continue;
                string target = edits.TargetPath(source);
                bool protectedTarget;
                try { protectedTarget = PickupPlacementEditSession.IsProtectedPath(target); }
                // Save As can recover edits even if the original drive is no longer available.
                // Use Documents as its starting folder; the chosen destination is verified on save.
                catch (IOException) when (saveAs) { protectedTarget = true; }
                if (!saveAs && !protectedTarget) continue;
                SaveFileDialog dialog = new() { Title = "Save pickup archive copy · " + Path.GetFileName(source), Filter = "ZBD archive|*.zbd", DefaultExt = ".zbd",
                    AddExtension = true, FileName = Path.GetFileName(source), OverwritePrompt = false,
                    InitialDirectory = protectedTarget ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(target)! };
                if (dialog.ShowDialog(this) != true) return false;
                destinations ??= new(StringComparer.OrdinalIgnoreCase); destinations[source] = dialog.FileName;
            }
            IsEnabled = false; if (propertiesWindow != null) propertiesWindow.IsEnabled = false;
            ViewModel.Status = "Saving and verifying pickup placements…";
            var result = await edits.SaveAsync(destinations, ViewModel.Settings.CreateBackupOnSave, document.Lifetime.Token);
            if (result.SavedPaths.Count > 0)
            {
                if (ViewModel.Resolver is { } resolver) await resolver.InvalidateAsync(result.SavedPaths, document.Lifetime.Token);
                foreach (var doc in ViewModel.Documents) doc.InvalidateMissionContext();
                ViewModel.CheckExternalChanges();
                ViewModel.Status = "Saved and verified: " + string.Join("; ", result.SavedPaths);
            }
            if (result.Errors.Count > 0)
            {
                string message = "Saved: " + (result.SavedPaths.Count == 0 ? "none" : string.Join("; ", result.SavedPaths)) + "\n" + string.Join("\n", result.Errors) + "\nRemaining changes are unsaved.";
                ViewModel.AddProblem(message); MessageBox.Show(this, message, "Pickup save", MessageBoxButton.OK, MessageBoxImage.Information); return false;
            }
            return !edits.IsDirty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { Report(ex); MessageBox.Show(this, ex.Message, "Pickup save", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        finally { IsEnabled = true; if (propertiesWindow != null) propertiesWindow.IsEnabled = true; PickupEditsChanged(); }
    }
    private int? RemapPickupSelection(MissionPickupSource? source, MissionSceneContext mission)
    {
        if (source == null || pickupDocument?.PickupEdits is not { } edits) return null;
        var match = edits.MatchIn(source, mission);
        return match == null ? null : mission.Actors.FirstOrDefault(a => a.Pickup?.Source == match)?.Root;
    }
}
