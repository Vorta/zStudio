using System.Windows;
using System.Windows.Controls;
using zStudio.Core;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private IReadOnlyList<InspectorNode> ZrdTree(DocumentModel doc,AssetRecord asset,System.Text.Json.Nodes.JsonNode hierarchy)
    {
        if (!doc.DataTreeExpansion.TryGetValue(asset.Id,out var expansion)) doc.DataTreeExpansion.Add(asset.Id,expansion = []);
        // Expand only a bounded structural envelope initially. User choices belong to
        // this source record, and subsequent refreshes never force it back open.
        return new InspectorNode("ZRD data",hierarchy,expansion,initialDepth:4).Children;
    }
    private readonly List<StudioProblem> staticPreviewProblems = [];
    private void ClearStaticPreviewProblems()
    {
        foreach (var problem in staticPreviewProblems) ViewModel.Problems.Remove(problem);
        staticPreviewProblems.Clear(); PreviewNotices.Visibility = Visibility.Collapsed;
    }
    private void ShowStaticPreviewProblems(DocumentModel document, AssetRecord asset)
    {
        if (scene == null) return;
        ClearStaticPreviewProblems();
        // Source evaluation notes already belong to ReportSourceBuild's file/operation rows. Core also
        // carries them into mission contexts for headless consumers; suppress only the individually reported
        // source warnings, leaving notes beyond that display cap available to the preview.
        // Compare full identities only when MissionSceneLoader's 1023-character-plus-ellipsis bound leaves
        // them unchanged; do not collapse distinct longer warnings merely because their previews match.
        var sourceNotes = document.Document is zStudio.Core.Sources.SourceSceneSnapshot source
            ? source.Notes.Take(SourceWarningsShown).Where(note => note.Length <= 1023 || note.Length == 1024 && note[^1] == '…')
                .ToHashSet(StringComparer.Ordinal) : null;
        foreach (var note in scene.PreviewDiagnostics)
        {
            if (note.Severity == "Warning" && sourceNotes?.Contains(note.Message) == true) continue;
            // Asset scope is known; these renderer notes do not supply per-actor source identities.
            var problem = new StudioProblem(note.Severity, "Preview", note.Message, document.Path, asset.Index, asset.Offset);
            staticPreviewProblems.Add(problem); ViewModel.Problems.Add(problem);
        }
        PreviewNotices.Content = $"{staticPreviewProblems.Count} preview notices";
        PreviewNotices.Visibility = staticPreviewProblems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowPreviewFooter(scene);
        RefreshSourceStatus();
    }
    // The loaded preview's footer (also restored when the view renders again after a render failure); a render failure the
    // view still has stays in view.
    private void ShowPreviewFooter(zStudio.Rendering.SceneViewport preview)
    {
        if (preview.RenderFailure is { } failure) { PreviewInfo.Text = failure; PreviewInfo.ToolTip = failure; return; }
        PreviewInfo.Text = (preview.Mission is { } mission ? mission.Layout.Label + " · " : "") + preview.PreviewSummary;
        PreviewInfo.ToolTip = preview.Mission?.Layout.Description ?? preview.PreviewSummary;
    }
    private StudioProblem? previewFailureProblem;
    // A source map hides the footer: there a render failure goes to Problems and opens Diagnostics, as the map's other failures
    // do. The next good frame (or another failure) replaces it.
    private void ShowPreviewFailure(string text)
    {
        ClearPreviewFailure();
        if (!sourceChromeActive) return;
        previewFailureProblem = new StudioProblem("Error", "Preview", text, ViewModel.SelectedDocument?.Path);
        ViewModel.Problems.Add(previewFailureProblem);
        SetToolsVisible(true, problems: true);
    }
    private void ClearPreviewFailure()
    {
        if (previewFailureProblem == null) return;
        ViewModel.Problems.Remove(previewFailureProblem); previewFailureProblem = null;
    }
    // A preview that becomes the current one: the last one's failure goes, a failure of its own load is listed.
    private void WatchPreviewFailures(zStudio.Rendering.SceneViewport preview)
    {
        ClearPreviewFailure();
        preview.PreviewFailed += text => { if (ReferenceEquals(preview, scene)) ShowPreviewFailure(text); };
        preview.PreviewRestored += () => { if (ReferenceEquals(preview, scene)) { ClearPreviewFailure(); ShowPreviewFooter(preview); } };
        if (preview.RenderFailure is { } failure && ReferenceEquals(preview, scene)) ShowPreviewFailure(failure);
    }
    private void PreviewNoticesClick(object sender, RoutedEventArgs e)
    {
        SetToolsVisible(true, problems: true);
        FileProblems.SelectedItem = staticPreviewProblems.FirstOrDefault();
    }
    private void FileProblemSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FileProblemDetailsButton.IsEnabled = FileProblems.SelectedItem is StudioProblem;
    }
    private void FileProblemDetailsClick(object sender, RoutedEventArgs e)
    {
        if (FileProblems.SelectedItem is StudioProblem p) DetailDialog.Show(this,"Notice details",$"{p.Severity} · {p.Category}\n{p.Message}\n\n{p.Details}");
    }
    private void ClearAssetFilterClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is { } doc) { doc.Query = ""; doc.KindFilter = "All types"; }
    }
    private void UpdateSearchHint()
    {
        string results = ViewModel.GlobalQuery.Trim().Length < 2 ? "Search this root: type at least two characters in Search all assets." : ViewModel.SearchResults.Count == 0 ? "No matching indexed assets. Try a shorter name or a file path." : (ViewModel.SearchIsLimited ? "First 500 indexed matches · refine your search for more specific results." : $"{ViewModel.SearchResults.Count:N0} indexed results.") + " Double-click or press Enter to open the exact record.";
        SearchHint.Text = ViewModel.SearchIndexNotice.Length == 0 ? results : ViewModel.SearchIndexNotice + "\n" + results;
        RelatedLimit.Text = ViewModel.SearchIndexComplete
            ? "Limited name-based matches: reference, index or result limits were reached. Use Search to find other indexed assets."
            : ViewModel.SearchIndexNotice;
    }
    private async void SearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && SearchList.SelectedItem is SearchHit hit) { e.Handled = true; await Navigate(hit); }
    }
}
