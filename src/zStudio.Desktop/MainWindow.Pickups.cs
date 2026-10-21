using System.Windows;
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
        // A source object's fields are editable without a draft: its handles open one first, as typing does. Compiled worlds are read only.
        viewport.CanStartPickupEdit = () => CurrentInspectionCard is { ObjectApplying: false } card &&
            pickupDocument is { SourceWorld: not null } doc && (card.HasDraft || card.ObjectEditor != null && card.OpenObjectDraftForGesture()) &&
            card.DraftDocument == doc && card.DraftRevision == doc.Revision &&
            (propertiesWindow == null || propertiesWindow.Document != doc || propertiesWindow.ResolvePendingDrafts()) && card.DraftRevision == doc.Revision;
    }
    private void DetachPickupEditor()
    {
        CloseParachutes();
        CancelAiPathDraft(); scene?.SetAiPathEditing(null);
        scene?.CancelPickupDrag();
        pickupDocument = null; SceneHost.IsEnabled = true;
    }
    private void AttachPickupEditor(DocumentModel document)
    {
        pickupDocument = document;
        scene?.SetPlacementEditing(document.SourceWorld != null);
        PickupEditsChanged();
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
    private async void SaveCurrentClick(object sender, RoutedEventArgs e)
    { if (ViewModel.SelectedDocument is { } doc) await SaveCurrentAsync(doc); }
    /// <summary>Saves a source world's project; a compiled document is read only and has nothing to save.</summary>
    private async Task<bool> SaveCurrentAsync(DocumentModel document)
    {
        if (document.SourceWorld == null) return true;
        if (!await SaveSourceWorldDocumentAsync(document)) return false;
        if (!LiveDocument(document).IsDirty) return true;
        ViewModel.Status = "Some document changes remain unsaved.";
        return false;
    }
    private int? RemapPickupSelection(MissionPickupSource? source, MissionSceneContext mission)
    {
        if (source == null || pickupDocument?.PickupEdits is not { } edits) return null;
        var match = edits.MatchIn(source, mission);
        return match == null ? null : mission.Actors.FirstOrDefault(a => a.Pickup?.Source == match)?.Root;
    }
}
