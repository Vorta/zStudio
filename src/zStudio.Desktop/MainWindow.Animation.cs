using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private async void SaveAnimationClick(object sender, RoutedEventArgs e) { if (ViewModel.SelectedDocument is { } doc) await SaveAnimationAsync(doc); }
    private void UndoAnimationClick(object sender, RoutedEventArgs e) { scene?.CancelPickupDrag(); if (ViewModel.SelectedDocument is { } doc) UndoDocument(doc, false); }
    private void RedoAnimationClick(object sender, RoutedEventArgs e) { scene?.CancelPickupDrag(); if (ViewModel.SelectedDocument is { } doc) UndoDocument(doc, true); }
    private async Task<bool> SaveAnimationAsync(DocumentModel doc)
    {
        if (doc.AnimationEdits is not { } edits) { ViewModel.Status = "Saving is available for animation packs and editable mission pickups."; return false; }
        if (!ResolvePropertiesDrafts(doc) || shownDocument == doc && animation?.ResolvePendingDrafts() == false) return false;
        System.Windows.Input.Keyboard.ClearFocus(); if (shownDocument == doc) animation?.Pause();
        SaveFileDialog dialog = new() { Title = "Save a new animation pack outside the source dataset", Filter = "Animation pack|*.zbd", DefaultExt = ".zbd", AddExtension = true, FileName = "anim-edited.zbd", OverwritePrompt = false,
            InitialDirectory = doc.LastSavedCopy == null ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(doc.LastSavedCopy)! };
        if (dialog.ShowDialog(this) != true) return false;
        IsEnabled = false; if (propertiesWindow != null) propertiesWindow.IsEnabled = false;
        try
        {
            await SaveAnimationToPathAsync(doc, dialog.FileName);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); MessageBox.Show(this, ex.Message, "Animation Save As", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        finally { IsEnabled = true; if (propertiesWindow != null) propertiesWindow.IsEnabled = true; }
    }
    private async Task<bool> ConfirmDocumentCloseAsync(DocumentModel document)
    {
        if (!await ResolvePropertiesDraftsAsync(document) || shownDocument == document && animation?.ResolvePendingDrafts() == false) return false;
        // A committed draft may have rebuilt a source world: the decision is about the document it shows now.
        document = LiveDocument(document);
        if (!closingAllDocuments && !ViewModel.ClosingAllDocuments && document.SourceWorld is { IsRebuilding: true })
        {
            ViewModel.Status = "This world is rebuilding after an edit; close it when it is shown, or the edit is taken back.";
            return false;
        }
        System.Windows.Input.Keyboard.ClearFocus(); scene?.CancelPickupDrag(); if (!document.IsDirty) return true;
        if (document.SourceWorld is { } world)
        {
            if (!closingAllDocuments && !ViewModel.ClosingAllDocuments && !OtherSourceWorldOpen(document) && SourceWorldPending(document))
            {
                ViewModel.Status = "A world of this source project is opening or rebuilding; try again when it is shown, so its unsaved edits can be decided.";
                return false;
            }
            // The project's edits stay with its other open worlds; they are decided when the last one closes.
            if (!closingAllDocuments && !ViewModel.ClosingAllDocuments && OtherSourceWorldOpen(document)) return true;
            // Closing every document: one Discard covers the project's other worlds in the same decision.
            if ((closingAllDocuments || ViewModel.ClosingAllDocuments) && discardApprovedWorkspace is { } approved && approved.Workspace == world.Workspace && approved.Revision == world.Workspace.Revision) return true;
        }
        bool pickup = document.SourceWorld != null || document.ContentEdits != null || document.PickupEdits?.IsDirty == true || document.ResourceEdits != null || document.ModelEdits?.IsDirty == true; string saveLabel = pickup ? "Save" : "Save As…";
        animation?.Pause(); string choice = "Cancel";
        StackPanel panel = new() { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = $"Save changes to {document.Title.TrimEnd(' ', '*')}?", FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        string detail = document.SourceWorld is { } project ? $"Save writes every changed file of the source project ({project.Workspace.DirtyFiles.Count}): {string.Join(", ", project.Workspace.DirtyFiles.Take(6))}{(project.Workspace.DirtyFiles.Count > 6 ? ", …" : "")}. Discard drops the project's unsaved edits."
            : pickup ? "Save verifies changes before updating working files. Protected reference datasets require saving a copy elsewhere." : "Save As writes a new animation pack and preserves the original source.";
        panel.Children.Add(new TextBlock { Text = detail, Margin = new(0,12,0,20), TextWrapping = TextWrapping.Wrap });
        WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        Window dialog = new() { Owner = this, Title = "Unsaved changes", Width = 470, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        foreach (string label in new[] { saveLabel, "Discard", "Cancel" })
        {
            Button button = new() { Content = label, MinWidth = 95, Margin = new(4), Padding = new(10,7,10,7), IsCancel = label == "Cancel", IsDefault = label == saveLabel };
            button.Click += (_, _) => { choice = label; dialog.Close(); }; buttons.Children.Add(button);
        }
        dialog.ShowDialog();
        if (choice == "Discard" && document.SourceWorld is { } discarded) discardApprovedWorkspace = (discarded.Workspace, discarded.Workspace.Revision);
        return choice == "Discard" || choice == saveLabel && await SaveCurrentAsync(document);
    }
}
