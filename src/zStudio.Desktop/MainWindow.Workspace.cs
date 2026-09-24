using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private WorkspaceLayout Layout => ViewModel.Settings.GetWorkspace();
    private bool detachingWorkspace;
    private bool arrangingWorkspace, navigatorCollapsed, navigatorTemporary, inspectorTemporary, toolsMaximized;
    private string previousPreset = "Edit";
    internal bool IsChangingLayout { get; private set; }
    private void InitializeWorkspace()
    {
        var layout = Layout;
        NavigationTabs.SelectedIndex = layout.BrowserTab; InspectorTabs.SelectedIndex = layout.InspectorTab; ToolTabs.SelectedIndex = layout.ToolTab;
        InitializeChrome(); ApplyDensity();
        RecentMenu.Loaded += (_,_) =>
        {
            // Fluent's submenu-header template omits the shared checkbox gutter
            // used by its leaf items (File contains the backup checkbox).
            RecentMenu.ApplyTemplate();
            if (RecentMenu.Template.FindName("MenuItemContent",RecentMenu) is Grid grid && grid.ColumnDefinitions[0].SharedSizeGroup != "MenuItemCheckBoxIconColumnGroup")
            {
                foreach (UIElement child in grid.Children) Grid.SetColumn(child,Grid.GetColumn(child) + 1);
                grid.ColumnDefinitions.Insert(0,new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "MenuItemCheckBoxIconColumnGroup" });
            }
        };
        foreach (var splitter in new[] { NavigatorSplitter,InspectorSplitter,ToolsSplitter })
            splitter.KeyUp += (_,e) => { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) WorkspaceSplitterCompleted(splitter,new DragCompletedEventArgs(0,0,false)); };
        Loaded += (_, _) => ArrangeWorkspace();
        ViewModel.PropertyChanged += DocumentChanged;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.RootPath) or nameof(MainViewModel.HasRoot) or nameof(MainViewModel.SelectedDocument)) UpdateDocumentCommands();
            if (args.PropertyName is nameof(MainViewModel.HasRoot) or nameof(MainViewModel.SelectedDocument)) ArrangeWorkspace();
            if (args.PropertyName == nameof(MainViewModel.RootPath) && ViewModel.SelectedDocument == null) NavigationTabs.SelectedItem = FilesTab;
            if (args.PropertyName == nameof(MainViewModel.GlobalQuery)) UpdateSearchHint();
        };
        ViewModel.SearchResults.CollectionChanged += (_, _) => UpdateSearchHint();
        ViewModel.Problems.CollectionChanged += (_, _) => UpdateDocumentCommands();
        UpdateSearchHint();
        UpdateDocumentCommands();
    }
    private void UpdateDocumentCommands()
    {
        var doc = ViewModel.SelectedDocument;
        bool hasDocument = doc != null;
        bool hasScene = doc?.SceneRoots.Count > 0;
        // Move off an unavailable page before collapsing its tab. Preserve Files
        // and Search selections when the document context changes.
        if ((!hasDocument && AssetsTab.IsSelected) || (!hasScene && DocumentSceneTab.IsSelected))
            NavigationTabs.SelectedItem = hasDocument ? AssetsTab : FilesTab;
        AssetsTab.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        DocumentSceneTab.Visibility = hasScene ? Visibility.Visible : Visibility.Collapsed;
        AssetsTab.IsEnabled = hasDocument; DocumentSceneTab.IsEnabled = hasScene;
        InspectorVisibilityMenu.IsEnabled = InspectorPaneButton.IsEnabled = animation != null;
        PropertiesMenu.IsEnabled = hasDocument;
        ToolsVisibilityMenu.IsEnabled = ToolsPaneButton.IsEnabled = hasDocument || ViewModel.Problems.Count > 0;
        DocumentCommands.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        ExportSelectedMenu.IsEnabled = ExportAllMenu.IsEnabled = ExportJsonMenu.IsEnabled = CloseDocumentMenu.IsEnabled = ValidateMenu.IsEnabled = ReloadMenu.IsEnabled = hasDocument;
        BackupOnSave.IsEnabled = doc?.PickupEdits != null;
        WelcomeTitle.Text = ViewModel.HasRoot ? "Choose a file to inspect" : "Explore Recoil’s assets";
        WelcomeDescription.Text = ViewModel.HasRoot ? "Open a ZBD file from Files, or search for an asset across this folder." : "Textures, worlds, models, audio, scripts, and animation sequences — together in one workspace.";
        WelcomeOpen.Visibility = ViewModel.HasRoot ? Visibility.Collapsed : Visibility.Visible;
        WelcomeHelp.Text = ViewModel.HasRoot ? "Double-click a file or select it and press Enter.\nUse File → Open folder to change your ZBD root." : "Open the folder containing image.zbd and the mission directories.\nYou can also drop a folder or a ZBD file here.";
        AnimationMenu.IsEnabled = animation != null;
        CopyEventJsonMenu.IsEnabled = animation != null;
        foreach (var column in AssetGrid.Columns.Skip(1)) column.Visibility = doc?.AnimationEdits != null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in AnimationMenu.Items.OfType<MenuItem>()) if (item.Tag is string command) item.IsEnabled = animation?.CanRunCommand(command) == true;
        DocumentSave.IsEnabled = doc?.AnimationEdits != null || doc?.PickupEdits != null;
        DocumentSave.ToolTip = doc?.PickupEdits != null ? "Save pickup placements to the owning archive (Ctrl+S)" : doc?.AnimationEdits != null ? "Save the animation pack to a new file (Ctrl+S)" : "Save (Ctrl+S)";
        DocumentUndo.IsEnabled = doc?.AnimationEdits?.CanUndo == true || doc?.PickupEdits?.CanUndo == true;
        DocumentRedo.IsEnabled = doc?.AnimationEdits?.CanRedo == true || doc?.PickupEdits?.CanRedo == true;
        SaveMenu.IsEnabled = SaveAsMenu.IsEnabled = DocumentSave.IsEnabled;
        UndoMenu.IsEnabled = DocumentUndo.IsEnabled; RedoMenu.IsEnabled = DocumentRedo.IsEnabled;
        DocumentUndo.ToolTip = doc?.AnimationEdits?.UndoDescription is string undo ? "Undo: " + undo + " (Ctrl+Z)" : "Undo (Ctrl+Z)";
        DocumentRedo.ToolTip = doc?.AnimationEdits?.RedoDescription is string redo ? "Redo: " + redo + " (Ctrl+Y)" : "Redo (Ctrl+Y)";
    }
    private void AttachAnimationWorkspace(AnimationEditor editor)
    {
        int preferredTool = Layout.ToolTab, preferredInspector = Layout.InspectorTab; detachingWorkspace = true;
        ProgramHost.Content = editor.ProgramView;
        editor.PropertiesRequested += (sequence, ev) => OpenAnimationProperties(shownDocument!, editor.EntryIndex, sequence, ev);
        var ownerDocument = shownDocument!;
        editor.ResolvePropertyDrafts = () => ResolvePropertiesDrafts(ownerDocument);
        ReferencesHost.Content = editor.ReferencesView; PreviewSetupHost.Content = editor.PreviewSetupView;
        DispatchHost.Content = editor.DispatchView; EventLogHost.Content = editor.EventLogView;
        RuntimeHost.Content = editor.RuntimeView; ProblemsHost.Content = editor.ProblemsView;
        editor.SetOperationDiagnostics(ViewModel.Problems);
        editor.SourceSelectionChanged += UpdateDocumentCommands;
        editor.SetupRequested += () => { Layout.InspectorVisible = true; inspectorTemporary = true; InspectorTabs.SelectedItem = PreviewSetupTab; ArrangeWorkspace(); };
        editor.CommandsChanged += UpdateDocumentCommands;
        editor.FileProblemSelected += async problem => await NavigateProblemAsync(problem);
        editor.SourceNavigationRequested += (entry,sequence,ev) => NavigateAnimationSource(entry,sequence,ev);
        ReferencesTab.IsEnabled = PreviewSetupTab.IsEnabled = ProgramTab.IsEnabled = true;
        DispatchTab.IsEnabled = EventLogTab.IsEnabled = RuntimeTab.IsEnabled = true;
        ReferencesTab.Visibility = PreviewSetupTab.Visibility = ProgramTab.Visibility = DispatchTab.Visibility = EventLogTab.Visibility = RuntimeTab.Visibility = Visibility.Visible;
        InspectorTabs.SelectedIndex = preferredInspector;
        ToolTabs.SelectedIndex = preferredTool; detachingWorkspace = false;
        DiagnosticsExpander.Visibility = Visibility.Collapsed;
        ArrangeWorkspace(); UpdateDocumentCommands();
    }
    private void DetachAnimationWorkspace()
    {
        detachingWorkspace = true;
        ProgramHost.Content = ReferencesHost.Content = PreviewSetupHost.Content = null;
        DispatchHost.Content = EventLogHost.Content = RuntimeHost.Content = ProblemsHost.Content = null;
        ReferencesTab.IsEnabled = PreviewSetupTab.IsEnabled = ProgramTab.IsEnabled = false;
        DispatchTab.IsEnabled = EventLogTab.IsEnabled = RuntimeTab.IsEnabled = false;
        ReferencesTab.Visibility = PreviewSetupTab.Visibility = ProgramTab.Visibility = DispatchTab.Visibility = EventLogTab.Visibility = RuntimeTab.Visibility = Visibility.Collapsed;
        DiagnosticsExpander.Visibility = Visibility.Visible;
        if (ToolTabs.SelectedIndex is 0 or 1 or 3) ToolTabs.SelectedIndex = 4;
        detachingWorkspace = false;
        ArrangeWorkspace(); UpdateDocumentCommands();
    }
    private void WorkbenchSizeChanged(object sender, SizeChangedEventArgs e) => ArrangeWorkspace();
    private void ArrangeWorkspace()
    {
        if (!ready || arrangingWorkspace || Workbench.ActualWidth <= 0) return;
        arrangingWorkspace = IsChangingLayout = true;
        try
        {
            var layout = Layout; bool focus = layout.Preset == "Focus preview";
            double width = Workbench.ActualWidth;
            // Keep both side panes when the center still has a usable width.
            double navigatorBreakpoint = layout.NavigatorWidth + (animation != null && layout.InspectorVisible ? layout.InspectorWidth + 12 : 6) + 360;
            navigatorCollapsed = width < navigatorBreakpoint + (navigatorCollapsed ? 40 : 0);
            bool nav = ViewModel.HasRoot && layout.NavigatorVisible && !focus && (!navigatorCollapsed || navigatorTemporary);
            bool inspector = animation != null && layout.InspectorVisible && !focus;
            double navWidth = nav ? layout.NavigatorWidth : 0;
            bool replacement = inspector && width - navWidth - layout.InspectorWidth - 12 < 360;
            if (replacement && !inspectorTemporary) inspector = false;
            NavigatorVisibilityMenu.IsChecked = nav; InspectorVisibilityMenu.IsChecked = inspector;
            NavigatorHost.Visibility = nav ? Visibility.Visible : Visibility.Collapsed;
            NavigatorSplitter.Visibility = nav ? Visibility.Visible : Visibility.Collapsed;
            FilesColumn.Width = new(navWidth); NavigatorSplitterColumn.Width = new(nav ? 6 : 0);
            PropertiesColumn.Width = new(inspector && !replacement ? layout.InspectorWidth : 0);
            PropertiesSplitterColumn.Width = new(inspector && !replacement ? 6 : 0);
            InspectorSplitter.Visibility = inspector && !replacement ? Visibility.Visible : Visibility.Collapsed;
            InspectorHost.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
            InspectorTabs.Visibility = animation != null ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(InspectorHost, replacement ? 2 : 4); Grid.SetColumnSpan(InspectorHost, replacement ? 3 : 1);
            bool replaceViewport = replacement && inspector && animation != null;
            animation?.ShowInspectorReplacement(replaceViewport);
            Workspace.Visibility = shownDocument == null ? Visibility.Collapsed : replacement && inspector && animation == null ? Visibility.Hidden : Visibility.Visible;
            InspectorHost.VerticalAlignment = replaceViewport ? VerticalAlignment.Top : VerticalAlignment.Stretch;
            InspectorHost.Height = replaceViewport ? animation!.ViewportRegion.ActualHeight : double.NaN;
            InspectorHost.Margin = replaceViewport ? new(0,animation!.ViewportRegion.TranslatePoint(new(),Workbench).Y,0,0) : new(0);
            double previewChrome = animation is { ActualHeight: > 0 } ? animation.ActualHeight - animation.Viewport.ActualHeight + 78 : 190;
            double availableTools = Workbench.ActualHeight - Math.Max(400, 220 + previewChrome);
            bool tools = ViewModel.HasRoot && (ViewModel.SelectedDocument != null || ViewModel.Problems.Count > 0) && layout.ToolsVisible && !focus && availableTools >= 100;
            ToolsRow.Height = new(tools ? Math.Min(toolsMaximized ? availableTools : layout.ToolsHeight, availableTools) : 0);
            ToolsSplitterRow.Height = new(tools ? 6 : 0);
            BottomTools.Visibility = ToolsSplitter.Visibility = tools ? Visibility.Visible : Visibility.Collapsed;
            ToolsVisibilityMenu.IsChecked = tools;
        }
        finally
        {
            arrangingWorkspace = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => IsChangingLayout = false);
        }
    }
    private void WorkspaceSplitterCompleted(object sender, DragCompletedEventArgs e)
    {
        if (arrangingWorkspace) return;
        var layout = Layout;
        if (sender == NavigatorSplitter) layout.NavigatorWidth = FilesColumn.ActualWidth;
        if (sender == InspectorSplitter) layout.InspectorWidth = PropertiesColumn.ActualWidth;
        if (sender == ToolsSplitter) { layout.ToolsHeight = ToolsRow.ActualHeight; toolsMaximized = false; }
        layout.Normalize(); ArrangeWorkspace(); SaveWorkspacePreferences();
    }
    private void SaveWorkspacePreferences()
    {
        if (detachingWorkspace) return;
        Layout.BrowserTab = NavigationTabs.SelectedIndex;
        if (animation != null && ProgramHost.Content != null && InspectorTabs.SelectedIndex >= 0) Layout.InspectorTab = InspectorTabs.SelectedIndex;
        Layout.ToolTab = Math.Max(0, ToolTabs.SelectedIndex);
    }
    private void NavigatorClick(object sender, RoutedEventArgs e) { Layout.NavigatorVisible = ((MenuItem)sender).IsChecked; navigatorTemporary = Layout.NavigatorVisible; ArrangeWorkspace(); }
    private void NavigatorButtonClick(object sender, RoutedEventArgs e)
    {
        if (Layout.Preset == "Focus preview") Layout.Preset = previousPreset;
        if (navigatorCollapsed) { navigatorTemporary = !navigatorTemporary; Layout.NavigatorVisible = true; }
        else Layout.NavigatorVisible = !Layout.NavigatorVisible;
        ArrangeWorkspace();
    }
    private void InspectorButtonClick(object sender, RoutedEventArgs e)
    {
        if (Layout.Preset == "Focus preview") Layout.Preset = previousPreset;
        Layout.InspectorVisible = InspectorHost.Visibility != Visibility.Visible; inspectorTemporary = Layout.InspectorVisible; ArrangeWorkspace();
    }
    private void ToolsButtonClick(object sender, RoutedEventArgs e)
    {
        if (Layout.Preset == "Focus preview") Layout.Preset = previousPreset;
        Layout.ToolsVisible = !Layout.ToolsVisible; ArrangeWorkspace();
    }
    private void MaximizeToolsClick(object sender, RoutedEventArgs e) { toolsMaximized = !toolsMaximized; ArrangeWorkspace(); }
    private void PresetClick(object sender, RoutedEventArgs e)
    {
        string preset = ((MenuItem)sender).Header.ToString()!;
        if (preset == "Focus preview" && Layout.Preset == preset) { Layout.Preset = previousPreset; ArrangeWorkspace(); return; }
        else if (preset == "Focus preview") previousPreset = Layout.Preset;
        Layout.Preset = preset;
        if (preset != "Focus preview") { Layout.NavigatorVisible = Layout.InspectorVisible = true; Layout.ToolsVisible = preset != "Inspect"; }
        if (preset == "Debug") { Layout.ToolsHeight = 300; ToolTabs.SelectedIndex = 2; }
        ArrangeWorkspace();
    }
    private void DensityClick(object sender, RoutedEventArgs e) { Layout.Density = ((MenuItem)sender).Header.ToString()!; ApplyDensity(); }
    private void ApplyDensity()
    {
        IsChangingLayout = true;
        propertiesWindow?.ApplyAppearance();
        FontSize = Layout.Density == "Comfortable" ? 15 : 13;
        Resources["PreviewControlSize"] = Layout.Density == "Comfortable" ? 40d : 32d;
        Resources["PreviewIconSize"] = Layout.Density == "Comfortable" ? 20d : 16d;
        Resources["WorkspaceControlPadding"] = Layout.Density == "Comfortable" ? new Thickness(12, 8, 12, 8) : new Thickness(8, 4, 8, 4);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => IsChangingLayout = false);
    }
    private void InspectorSelectionChanged(object sender, SelectionChangedEventArgs e) { if (ready && e.Source == InspectorTabs) SaveWorkspacePreferences(); }
    private void ToolSelectionChanged(object sender, SelectionChangedEventArgs e) { if (ready && e.Source == ToolTabs) SaveWorkspacePreferences(); }
    private void AnimationPlayClick(object sender, RoutedEventArgs e) => animation?.TogglePlayback();
    private void AnimationStopClick(object sender, RoutedEventArgs e) => animation?.Stop();
    private void AnimationPreviousClick(object sender, RoutedEventArgs e) => animation?.Step(-1);
    private void AnimationNextClick(object sender, RoutedEventArgs e) => animation?.Step(1);
    private void AnimationAddEventClick(object sender, RoutedEventArgs e) => animation?.ChooseEvent();
    private void AnimationAddSequenceClick(object sender, RoutedEventArgs e) => animation?.AddSequence();
    private void AnimationDuplicateClick(object sender, RoutedEventArgs e) => animation?.DuplicateRecord();
    private void AnimationDeleteClick(object sender, RoutedEventArgs e) => animation?.DeleteRecord();
    private void PreviewSetupClick(object sender, RoutedEventArgs e) { if (animation != null) { Layout.InspectorVisible = true; inspectorTemporary = true; InspectorTabs.SelectedItem = PreviewSetupTab; ArrangeWorkspace(); } }
    private void CopyEventJsonClick(object sender, RoutedEventArgs e) => animation?.CopySelectedEvent();
    private async void FileProblemDoubleClick(object sender, MouseButtonEventArgs e) { if (FileProblems.SelectedItem is StudioProblem problem) await NavigateProblemAsync(problem); }
    internal async Task NavigateProblemAsync(StudioProblem problem)
    {
        if (problem.File == null) { ViewModel.Status = problem.Details; return; }
        await RunUi(async () =>
        {
            var doc = await ViewModel.OpenFileAsync(problem.File); if (doc == null) return;
            var target = problem.ResolveAsset(doc.Document.Assets);
            var match = doc.Assets.FirstOrDefault(a => ReferenceEquals(a.Record, target));
            if (match != null)
            {
                doc.Query = ""; doc.KindFilter = "All types";
                doc.SelectedAsset = match; AssetGrid.ScrollIntoView(match);
            }
            ViewModel.Status = problem.Message;
        });
    }
    private async void NavigateAnimationSource(int entry,Guid sequence,Guid ev)
    {
        var doc = ViewModel.SelectedDocument;
        if (doc == null || animation?.ResolvePendingDrafts() == false) return;
        var asset = doc.Assets.FirstOrDefault(a => a.Record.Kind == Core.AssetKind.Animation && a.Index == entry);
        if (asset == null) { ViewModel.Status = "The dispatched source entry is no longer available in this document."; return; }
        doc.SelectedAsset = asset; AssetGrid.ScrollIntoView(asset);
        for (int attempt = 0; attempt < 100 && ViewModel.SelectedDocument == doc && doc.SelectedAsset == asset; attempt++)
        {
            if (animation is { } current && current.EntryIndex == entry) { current.SelectSource(sequence,ev); return; }
            await Task.Delay(30);
        }
    }
}
