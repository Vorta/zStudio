using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private void UndoDocumentClick(object sender, RoutedEventArgs e) { scene?.CancelPickupDrag(); if (ViewModel.SelectedDocument is { } doc) UndoDocument(doc, false); }
    private void RedoDocumentClick(object sender, RoutedEventArgs e) { scene?.CancelPickupDrag(); if (ViewModel.SelectedDocument is { } doc) UndoDocument(doc, true); }
    /// <summary>
    /// Close decisions run one at a time (a decision can wait for a rebuild, and its prompt pumps messages): a later one
    /// waits its turn, so prompts never stack, and a document an earlier decision closed needs no decision.
    /// </summary>
    private readonly SemaphoreSlim closeDecisions = new(1, 1);
    private async Task<bool> ConfirmDocumentCloseAsync(DocumentModel document)
    {
        await closeDecisions.WaitAsync();
        try
        {
            var live = LiveDocument(document);
            if (live.IsDisposed || !ViewModel.Documents.Contains(live)) return true;
            return await DecideDocumentCloseAsync(live);
        }
        finally { closeDecisions.Release(); }
    }
    private async Task<bool> DecideDocumentCloseAsync(DocumentModel document)
    {
        for (bool askingAgain = false; ; askingAgain = true)
        {
            if (!await ResolvePropertiesDraftsAsync(document) || shownDocument == document && animation?.ResolvePendingDrafts() == false) return false;
            // A committed draft may have rebuilt a source world: the decision is about the document it shows now.
            document = LiveDocument(document);
            if ((askingAgain || !closingAllDocuments && !ViewModel.ClosingAllDocuments) && document.SourceWorld != null && sourceWorkspaceBusy)
            {
                // Closing a rebuilding world would take its edit back unseen: the close waits for the world it rebuilds into.
                ViewModel.Status = "Waiting for the source world to finish rebuilding…";
                await SourceWorldsIdleAsync();
                document = LiveDocument(document);
            }
            System.Windows.Input.Keyboard.ClearFocus(); scene?.CancelPickupDrag(); if (!document.IsDirty) return ApproveClose(document, document.Revision);
            if (document.SourceWorld is { } world)
            {
                if (!closingAllDocuments && !ViewModel.ClosingAllDocuments && !OtherSourceWorldOpen(document) && SourceWorldPending(document))
                {
                    ViewModel.Status = "A world of this source project is opening or rebuilding; try again when it is shown, so its unsaved edits can be decided.";
                    return false;
                }
                // The project's edits stay with its other open worlds; they are decided when the last one closes.
                if (!closingAllDocuments && !ViewModel.ClosingAllDocuments && OtherSourceWorldOpen(document)) return ApproveClose(document, document.Revision);
                // Closing every document: one Discard covers the project's other worlds in the same decision.
                if ((closingAllDocuments || ViewModel.ClosingAllDocuments) && discardApprovedWorkspace is { } approved && approved.Workspace == world.Workspace && approved.Revision == world.Workspace.Revision)
                    return ApproveClose(document, document.Revision);
            }
            const string saveLabel = "Save";
            animation?.Pause(); string choice = "Cancel";
            // The answer covers the state the question shows. Its message loop still runs work started before it (an MCP job
            // whose planning ran while it opened), so a change accepted while it waits is asked about, not saved or dropped unseen.
            long shown = document.Revision; var project = document.SourceWorld?.Workspace; long shownProject = project?.Revision ?? 0;
            Window dialog = CreateUnsavedDialog(document, selected => choice = selected);
            dialog.ShowDialog();
            if (choice != "Discard" && choice != saveLabel) return false;
            var live = LiveDocument(document);
            if (live.IsDisposed || !ViewModel.Documents.Contains(live)) return true;
            if (project != null ? live.SourceWorld?.Workspace != project || project.Revision != shownProject : live.Revision != shown)
            {
                ViewModel.Status = $"{Bounded(live.Title.TrimEnd(' ', '*'), 256)} changed while its unsaved changes were asked about; asking again.";
                document = live;
                continue;
            }
            if (choice == "Discard")
            {
                if (project != null) discardApprovedWorkspace = (project, shownProject);
                return ApproveClose(document, shown);
            }
            if (!await SaveCurrentAsync(document)) return false;
            document = LiveDocument(document);
            if (document.IsDirty)
            {
                ViewModel.Status = "Close canceled: some document changes remain unsaved.";
                return false;
            }
            return ApproveClose(document, document.Revision);
        }
    }
    private static bool ApproveClose(DocumentModel document, long revision) { document.ApprovedCloseRevision = revision; return true; }

    /// <summary>Asks about a source project's unsaved edits (only source worlds have any).</summary>
    internal Window CreateUnsavedDialog(DocumentModel document, Action<string> selected, Size? available = null)
    {
        StackPanel panel = new();
        panel.Children.Add(new TextBlock { Text = $"Save changes to {document.Title.TrimEnd(' ', '*')}?", FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        string detail = document.SourceWorld is { } project ? $"Save writes every changed file of the source project ({project.Workspace.DirtyFiles.Count}): {string.Join(", ", project.Workspace.DirtyFiles.Take(6).Select(path => Bounded(path, 256)))}{(project.Workspace.DirtyFiles.Count > 6 ? ", …" : "")}. Discard drops the project's unsaved edits."
            : "";
        panel.Children.Add(new TextBlock { Text = detail, Margin = new(0,12,0,20), TextWrapping = TextWrapping.Wrap });
        if (document.SourceWorld is { } source)
        {
            StackPanel paths = new();
            foreach (string path in source.Workspace.DirtyFiles.Take(6))
                paths.Children.Add(new TextBox { Text = path, IsReadOnly = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 2, 0, 2) });
            panel.Children.Add(new Expander { Header = "Full source paths (first six)", Content = paths });
        }
        WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Window dialog = new() { Owner = this, Title = "Unsaved changes", ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Content = DialogLayout.WithActions(panel, buttons, 20) };
        DialogLayout.Constrain(dialog, new(470, 440), available);
        foreach (string label in new[] { "Save", "Discard", "Cancel" })
        {
            Button button = new() { Content = label, MinWidth = 95, Margin = new(4), Padding = new(10,7,10,7), IsCancel = label == "Cancel", IsDefault = label == "Save" };
            button.Click += (_, _) => { selected(label); dialog.Close(); }; buttons.Children.Add(button);
        }
        return dialog;
    }
}
