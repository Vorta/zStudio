using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using zStudio.Core;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private DocumentModel? pickupDocument;
    private void ConfigurePickupScene(SceneViewport viewport)
    {
        ConfigureTerrainScene(viewport);
        ConfigureZoneScene(viewport);
        // A source object's fields are editable without a draft: its handles open one first, as typing does.
        viewport.CanStartPickupEdit = () => CurrentInspectionCard is { ObjectApplying: false } card &&
            pickupDocument is { EditingLocked: false } doc && (card.HasDraft || card.ObjectEditor != null && card.OpenObjectDraftForGesture()) &&
            card.DraftDocument == doc && card.DraftRevision == doc.Revision &&
            (card.ObjectEditing || doc.PickupEdits?.HasExternalChanges() == false) &&
            (propertiesWindow == null || propertiesWindow.Document != doc || propertiesWindow.ResolvePendingDrafts()) && card.DraftRevision == doc.Revision;
    }
    private void DetachPickupEditor()
    {
        CloseParachutes();
        CancelAiPathDraft(); scene?.SetAiPathEditing(null);
        scene?.CancelPickupDrag();
        if (pickupDocument != null) pickupDocument.PickupEditsChanged -= PickupEditsChanged;
        pickupDocument = null; PickupTools.Visibility = Visibility.Collapsed;
        PickupTools.IsEnabled = SceneHost.IsEnabled = EditingUnlocked.IsEnabled = true;
    }
    private void AttachPickupEditor(DocumentModel document)
    {
        pickupDocument = document; document.PickupEditsChanged += PickupEditsChanged;
        // A 1998 demo world opens read-only: its placements stay locked (inspection and hover remain).
        EditingUnlocked.IsEnabled = PickupPlacementEditSession.ReadOnlyWorld(document.Document.Probe) == null;
        SetSceneEditingLocked(document, document.EditingLocked);
        PickupTools.Visibility = document.SourceWorld == null ? Visibility.Visible : Visibility.Collapsed; PickupEditsChanged();
        if (!document.PickupDiagnosticsReported && document.PickupEdits is { } edits)
        {
            foreach (string note in edits.Diagnostics) ViewModel.AddProblem("Pickup editor: " + note,"Warning",document.Path);
            document.PickupDiagnosticsReported = true;
        }
    }
    private void PickupEditsChanged()
    {
        if (pickupDocument?.PickupEdits is not { } edits || scene?.Mission is not { } mission) return;
        scene.SetMissionCoordinates(edits);
        if (aiPropertiesArchive != null && propertiesWindow?.Document == pickupDocument) propertiesWindow.MarkAiSnapshotStale();
        UpdateDocumentCommands();
        if (selectedNode is int node && scene.PickupAt(node) != null && properties != null)
        {
            properties["preview_world_position"] = JsonData.Vector(scene.PickupPosition(scene.PickupAt(node)!.Root));
            SetProperties(properties);
        }
    }
    private void EditingUnlockedChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || updating || pickupDocument == null) return;
        if (!ResolvePropertiesDrafts(pickupDocument)) { updating = true; EditingUnlocked.IsChecked = !pickupDocument.EditingLocked; updating = false; return; }
        SetSceneEditingLocked(pickupDocument, EditingUnlocked.IsChecked != true);
    }
    private void SetSceneEditingLocked(DocumentModel document, bool locked)
    {
        // Unlock editing is the inverse of the shared document/MCP editing lock.
        // A read-only world (a 1998 demo) is never unlocked.
        document.EditingLocked = locked || PickupPlacementEditSession.ReadOnlyWorld(document.Document.Probe) != null;
        locked = document.EditingLocked;
        if (pickupDocument != document) return;
        bool previous = updating; updating = true;
        try { EditingUnlocked.IsChecked = !locked; }
        finally { updating = previous; }
        scene?.SetEditingLocked(locked);
    }
    private async void SaveCurrentClick(object sender, RoutedEventArgs e)
    { if (ViewModel.SelectedDocument is { } doc) await SaveCurrentAsync(doc); }
    private async void SaveCurrentAsClick(object sender, RoutedEventArgs e)
    { if (ViewModel.SelectedDocument is { } doc) await SaveCurrentAsync(doc, true); }
    private async Task<bool> SaveCurrentAsync(DocumentModel document, bool saveAs = false)
    {
        // Coordinate inspection can attach a second session to any mission document. It must not
        // replace the document's own save route, nor make a partial save count as a saved document.
        bool saved = await (document.SourceWorld != null ? SaveSourceWorldDocumentAsync(document)
            : document.ContentEdits != null ? SaveContentDocumentAsync(document, saveAs)
            : document.ResourceEdits != null ? SaveResourceDocumentAsync(document, saveAs)
            : document.AnimationEdits != null ? SaveAnimationAsync(document)
            : document.ModelEdits?.IsDirty == true || saveAs && document.ModelEdits?.HasModelImports == true ? SaveModelDocumentAsync(document, saveAs)
            : document.PickupEdits is { } coordinates && (coordinates.Records.Count > 0 || coordinates.OtherCoordinates.Count > 0) ? SavePickupsAsync(document, saveAs)
            : SaveAnimationAsync(document));
        if (!saved) return false;
        document = LiveDocument(document);
        if (document.SourceWorld == null && document.PickupEdits?.IsDirty == true && !await SavePickupsAsync(document, saveAs)) return false;
        if (!LiveDocument(document).IsDirty) return true;
        ViewModel.Status = "Some document changes remain unsaved.";
        return false;
    }
    private async Task<bool> SavePickupsAsync(DocumentModel document, bool saveAs)
    {
        if (document.PickupEdits is not { } edits || !ResolvePropertiesDrafts(document)) return false;
        System.Windows.Input.Keyboard.ClearFocus(); scene?.CancelPickupDrag();
        if (!saveAs && !edits.IsDirty) return true;
        try
        {
            Dictionary<string, string>? destinations = null;
            // A map loads every mission reader; Save As copies the archives its history edited.
            var scope = edits.EditedArchivePaths is { Count: > 0 } edited ? edited : edits.ArchivePaths;
            foreach (string source in scope)
            {
                if (!saveAs && !edits.IsArchiveDirty(source)) continue;
                string target = edits.TargetPath(source);
                bool protectedTarget;
                try { protectedTarget = ProtectedPaths.IsProtected(target); }
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
            var result = await SavePickupDestinationsAsync(document, destinations, ViewModel.Settings.CreateBackupOnSave);
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
