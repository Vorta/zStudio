using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

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
        foreach (var note in scene.PreviewDiagnostics)
        {
            // Asset scope is known; these renderer notes do not supply per-actor source identities.
            var problem = new StudioProblem(note.Severity, "Preview", note.Message, document.Path, asset.Index, asset.Offset);
            staticPreviewProblems.Add(problem); ViewModel.Problems.Add(problem);
        }
        PreviewNotices.Content = $"{staticPreviewProblems.Count} preview notices";
        PreviewNotices.Visibility = staticPreviewProblems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewInfo.Text = (scene.Mission is { } mission ? mission.Layout.Difficulty + " · " : "") + scene.PreviewSummary;
        PreviewInfo.ToolTip = scene.Mission?.Layout.Description ?? scene.PreviewSummary;
    }
    private void PreviewNoticesClick(object sender, RoutedEventArgs e)
    {
        Layout.ToolsVisible = true; ToolTabs.SelectedIndex = 2; ArrangeWorkspace();
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
        SearchHint.Text = ViewModel.GlobalQuery.Trim().Length < 2 ? "Search this root: type at least two characters in Search all assets." : ViewModel.SearchResults.Count == 0 ? "No matching assets in this root. Try a shorter name or a file path." : (ViewModel.SearchIsLimited ? "First 500 matches · refine your search for more specific results." : $"{ViewModel.SearchResults.Count:N0} results.") + " Double-click or press Enter to open the exact record.";
    }
    private async void SearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && SearchList.SelectedItem is SearchHit hit) { e.Handled = true; await Navigate(hit); }
    }
}
