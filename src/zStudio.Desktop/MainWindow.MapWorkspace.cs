using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using zStudio.Automation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private long sourceMapPickerRequest;
    private object? sourceMapOpening;

    private void UpdateSourceMapPresentation()
    {
        bool source = ViewModel.IsSourceProject;
        SourceMapPicker.Visibility = source && (sourceMapOpening != null || ViewModel.SelectedDocument == null) ? Visibility.Visible : Visibility.Collapsed;
        SourceMapPickerContent.Visibility = sourceMapOpening == null ? Visibility.Visible : Visibility.Collapsed;
        SourceMapLoading.Visibility = sourceMapOpening != null ? Visibility.Visible : Visibility.Collapsed;
        if (source) Welcome.Visibility = Visibility.Collapsed;
        else Welcome.Visibility = ViewModel.SelectedDocument == null ? Visibility.Visible : Visibility.Collapsed;
        // A source project has its own pane toggles (World tree, Properties, Diagnostics) and layout; the compiled explorer's do nothing there.
        NavigatorPaneButton.Visibility = InspectorPaneButton.Visibility = ToolsPaneButton.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
        WorkspacePresetMenu.Visibility = ResetLayoutMenu.Visibility = source || !ViewModel.HasRoot ? Visibility.Collapsed : Visibility.Visible;
        CloseDocumentMenu.Header = source ? "_Close map" : "_Close file";
        ExportMenu.Visibility = source ? Visibility.Visible : Visibility.Collapsed;
        // An open map moves the tree, the commands and the card into the source editor's chrome (see ApplySourceChrome).
        // Source layout never overwrites the compiled explorer's saved tab and pane preferences.
        ApplySourceChrome();
    }

    private void ArrangeSourceMapWorkspace()
    {
        SetFilesDetached(false);
        NavigatorHost.Visibility = NavigatorSplitter.Visibility = Visibility.Collapsed;
        FilesColumn.Width = NavigatorSplitterColumn.Width = new(0);
        bool open = ViewModel.SelectedDocument?.SourceWorld != null && sourceMapOpening == null;
        ArrangeSourcePanes(open);
        InspectorTabs.Visibility = Visibility.Collapsed;
        Grid.SetColumn(InspectorHost, 4); Grid.SetColumnSpan(InspectorHost, 1);
        InspectorHost.VerticalAlignment = VerticalAlignment.Stretch;
        InspectorHost.Height = double.NaN; InspectorHost.Margin = new(0);
        Workspace.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        // A map's failures are listed only there (its status bar shows no messages): Diagnostics fits on short screens too
        // (1080p at 175%, 768p at 125%), leaving the view at least 250.
        double available = Math.Max(0, Workbench.ActualHeight - 250);
        bool tools = open && SourceToolsVisible && available >= 80;
        bool transport = open && HasWorldAnimationTarget;
        UpdateWorldAnimationDock(tools);
        ToolsRow.Height = new(tools ? Math.Min(Math.Max(180, sourceToolsHeight ?? 300), available) : transport ? Math.Min(106, available) : 0);
        ToolsSplitterRow.Height = new(tools ? 6 : 0);
        BottomTools.Visibility = tools || transport ? Visibility.Visible : Visibility.Collapsed;
        ToolsSplitter.Visibility = tools ? Visibility.Visible : Visibility.Collapsed;
        RefreshSourceChrome();
    }

    private async Task RefreshSourceMapPickerAsync()
    {
        sourceMapOpening = null;
        long request = ++sourceMapPickerRequest;
        SourceMapButtons.Children.Clear();
        UpdateSourceMapPresentation(); ArrangeWorkspace();
        if (!ViewModel.IsSourceProject) return;
        string root = ViewModel.RootPath;
        long generation = ViewModel.WorkspaceGeneration;
        SourceMapPickerMessage.Text = "Finding maps…";
        try
        {
            var token = ViewModel.WorkspaceToken;
            var missions = await Task.Run(() => zStudio.Core.Sources.SourceWorlds.Missions(root, token), token);
            if (!Current()) return;
            SourceMapPickerMessage.Text = missions.Count == 0 ? "No maps found. A map needs a data/mN folder and a matching gamegen/mN.gs script." : "";
            // Six columns: maps 1–6 are the single-player campaign, the others multiplayer.
            var single = Section("Single player"); var multi = Section("Multiplayer");
            foreach (string mission in missions)
            {
                var button = MapButton(mission);
                button.Click += async (_, _) => await RunUi(async () =>
                {
                    if (!Current()) return;
                    SourceMapPickerMessage.Text = "";
                    // A map that cannot open says why on the picker itself (Diagnostics shows only with a map open).
                    try { await OpenSourceWorldAsync(mission, CancellationToken.None); }
                    catch (Exception ex) when (ex is StudioCommandException { Code: not "context_changed" } or InvalidDataException or IOException or UnauthorizedAccessException)
                    { if (Current() && ViewModel.SelectedDocument == null) SourceMapPickerMessage.Text = $"Could not open {mission}: " + Bounded(ex.Message, 2048); throw; }
                });
                (MissionNumber(mission) is >= 1 and <= 6 ? single : multi).Children.Add(button);
            }
            var create = MapButton("New"); create.IsEnabled = false; create.Opacity = .45;
            create.ToolTip = "Creating a new map is not available yet."; ToolTipService.SetShowOnDisabled(create, true);
            multi.Children.Add(create);
            foreach (var grid in new[] { single, multi })
                if (grid.Children.Count > 0) SourceMapButtons.Children.Add((UIElement)grid.Tag!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or StudioCommandException or OperationCanceledException)
        {
            if (Current()) SourceMapPickerMessage.Text = "Could not list maps: " + Bounded(ex.Message, 512);
        }
        bool Current() => request == sourceMapPickerRequest && generation == ViewModel.WorkspaceGeneration && ViewModel.IsSourceProject && ViewModel.RootPath == root;
        static Button MapButton(string label) => new()
        {
            Content = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center },
            Width = 120, Height = 88, Margin = new(6), FontSize = 22,
            Padding = new(12),
        };
        static int MissionNumber(string mission) => mission.Length > 1 && int.TryParse(mission.AsSpan(1), out int number) ? number : 0;
        // A titled group of maps; its Tag is the element the picker shows.
        static UniformGrid Section(string title)
        {
            UniformGrid grid = new() { Columns = 6, HorizontalAlignment = HorizontalAlignment.Left };
            StackPanel section = new() { Margin = new(0, 0, 0, 20) };
            section.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Opacity = .8, Margin = new(6, 0, 0, 6) });
            section.Children.Add(new Border { Height = 1, Margin = new(6, 0, 6, 8), Opacity = .25, Background = SystemColors.GrayTextBrush });
            section.Children.Add(grid);
            grid.Tag = section; return grid;
        }
    }

    private async void SourceMapsClick(object sender, RoutedEventArgs e) => await RunUi(async () =>
    {
        if (await ReturnToSourceMapsAsync(false, CancellationToken.None))
            await RefreshSourceMapPickerAsync();
    });
}
