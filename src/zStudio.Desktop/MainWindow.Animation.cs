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
        if (!ResolvePropertiesDrafts(document) || shownDocument == document && animation?.ResolvePendingDrafts() == false) return false;
        System.Windows.Input.Keyboard.ClearFocus(); scene?.CancelPickupDrag(); if (!document.IsDirty) return true;
        bool pickup = document.PickupEdits?.IsDirty == true; string saveLabel = pickup ? "Save" : "Save As…";
        animation?.Pause(); string choice = "Cancel";
        StackPanel panel = new() { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = $"Save changes to {document.Title.TrimEnd(' ', '*')}?", FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = pickup ? "Save updates the pickup placement archives. Protected reference datasets require saving a copy elsewhere." : "Save As writes a new animation pack and preserves the original source.", Margin = new(0,12,0,20), TextWrapping = TextWrapping.Wrap });
        WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        Window dialog = new() { Owner = this, Title = "Unsaved changes", Width = 470, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        foreach (string label in new[] { saveLabel, "Discard", "Cancel" })
        {
            Button button = new() { Content = label, MinWidth = 95, Margin = new(4), Padding = new(10,7,10,7), IsCancel = label == "Cancel", IsDefault = label == saveLabel };
            button.Click += (_, _) => { choice = label; dialog.Close(); }; buttons.Children.Add(button);
        }
        dialog.ShowDialog(); return choice == "Discard" || choice == saveLabel && await SaveCurrentAsync(document);
    }
}
