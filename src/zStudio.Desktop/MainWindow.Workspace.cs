using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private WorkspaceLayout Layout => ViewModel.Settings.GetWorkspace();
    private bool detachingWorkspace;
    private bool arrangingWorkspace, navigatorCollapsed, navigatorTemporary, inspectorTemporary, toolsMaximized;
    private string previousPreset = "Edit";
    private DocumentModel? commandDocument;
    private void InitializeWorkspace()
    {
        var layout = Layout;
        // A source project's workspace lives while any of its worlds is open; its last close decides its edits.
        ViewModel.Documents.CollectionChanged += (_, e) =>
        {
            if (e.Action is not System.Collections.Specialized.NotifyCollectionChangedAction.Add) ReleaseUnusedSourceWorkspace();
            // An approval waits for a release deferred by a rebuild or an opening; the release clears it.
            if (!ViewModel.Documents.Any(d => d.SourceWorld != null) && sourceWorkspace == null) discardApprovedWorkspace = null;
        };
        NavigationTabs.SelectedIndex = layout.BrowserTab; InspectorTabs.SelectedIndex = layout.InspectorTab; ToolTabs.SelectedIndex = layout.ToolTab;
        InitializeResponsiveNavigator();
        InitializeChrome(); ApplyMetrics();
        foreach (var submenu in new[] { RecentMenu })
            submenu.Loaded += (_,_) =>
            {
                // Fluent's submenu-header template omits the shared checkbox gutter
                // used by its leaf items (File contains the backup checkbox).
                submenu.ApplyTemplate();
                if (submenu.Template.FindName("MenuItemContent",submenu) is Grid grid && grid.ColumnDefinitions[0].SharedSizeGroup != "MenuItemCheckBoxIconColumnGroup")
                {
                    foreach (UIElement child in grid.Children) Grid.SetColumn(child,Grid.GetColumn(child) + 1);
                    grid.ColumnDefinitions.Insert(0,new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "MenuItemCheckBoxIconColumnGroup" });
                }
            };
        foreach (var splitter in new[] { FilesSplitter,NavigatorSplitter,InspectorSplitter,ToolsSplitter })
            splitter.KeyUp += (_,e) => { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) WorkspaceSplitterCompleted(splitter,new DragCompletedEventArgs(0,0,false)); };
        Loaded += (_, _) => ArrangeWorkspace();
        ViewModel.PropertyChanged += DocumentChanged;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.RootPath) or nameof(MainViewModel.HasRoot) or nameof(MainViewModel.IsSourceProject) or nameof(MainViewModel.SelectedDocument)) UpdateDocumentCommands();
            if (args.PropertyName is nameof(MainViewModel.HasRoot) or nameof(MainViewModel.IsSourceProject) or nameof(MainViewModel.SelectedDocument)) ArrangeWorkspace();
            if (args.PropertyName == nameof(MainViewModel.RootPath) && ViewModel.SelectedDocument == null) SelectNavigatorSection(0);
            // The brush paints the shown source world only; another root ends it.
            if (args.PropertyName == nameof(MainViewModel.RootPath)) terrainBrush = null;
            if (args.PropertyName is nameof(MainViewModel.RootPath) or nameof(MainViewModel.SelectedDocument)) ApplyTerrainBrush(scene);
            if (args.PropertyName is nameof(MainViewModel.GlobalQuery) or nameof(MainViewModel.SearchIndexComplete) or nameof(MainViewModel.SearchIndexNotice)) UpdateSearchHint();
        };
        // An interrupted save of a source project is reported, and resolved only on the user's decision. A root an MCP request
        // opened is not followed by a dialog (it would hold the workspace from that client): Problems lists the saves, and
        // source_recovery / source_recovery_resolve (or Save, which asks again) decide them.
        ViewModel.RootPublished += () => { if (SourceProjectRoot is { } project) _ = CheckSourceRecoveryAsync(project, ask: !ViewModel.RootOpenedWithoutPrompts); };
        ViewModel.RootPublished += ApplyProjectView;
        ViewModel.RootPublished += () => _ = RefreshSourceMapPickerAsync();
        ViewModel.SearchResults.CollectionChanged += (_, _) => UpdateSearchHint();
        ViewModel.Problems.CollectionChanged += (_, _) => UpdateDocumentCommands();
        UpdateSearchHint();
        UpdateDocumentCommands();
    }
    private void UpdateDocumentCommands()
    {
        // The polygon zone tool ends with its map (closed, or another one chosen).
        if (zoneTool.Session is { } zoneSession && (zoneSession.IsDisposed || ViewModel.SelectedDocument is { } selected && selected.SourceWorld != zoneSession)) LeaveZoneTool();
        RefreshPresentation();
        UpdateSourceInputBlock();
        RefreshSceneTree();
        var doc = ViewModel.SelectedDocument;
        ObserveDocumentCommands(doc);
        RebaseAnimationMode();
        bool hasDocument = doc != null;
        UpdateSourceMapPresentation();
        // Move off an unavailable page before collapsing its tab. Preserve Files
        // and Search selections when the document context changes.
        UpdateNavigatorAvailability();
        InspectorPaneButton.IsEnabled = !ViewModel.IsSourceProject && animation != null;
        NavigatorPaneButton.IsEnabled = !ViewModel.IsSourceProject && ViewModel.HasRoot;
        ToolsPaneButton.IsEnabled = hasDocument || ViewModel.Problems.Count > 0;
        DocumentCommands.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        // Compiled documents are read only: the title row shows their name without Undo, Redo and Save.
        DocumentUndo.Visibility = DocumentRedo.Visibility = DocumentSave.Visibility = doc?.SourceWorld != null ? Visibility.Visible : Visibility.Collapsed;
        CloseDocumentMenu.IsEnabled = ValidateMenu.IsEnabled = ReloadMenu.IsEnabled = hasDocument;
        WelcomeTitle.Text = ViewModel.HasRoot ? "Choose a file to inspect" : "Explore Recoil’s assets";
        WelcomeDescription.Text = ViewModel.HasRoot ? "Open a ZBD file from Files, or search for an asset across this folder." : "Textures, worlds, models, audio, scripts, and animation sequences — together in one workspace.";
        WelcomeChoices.Visibility = ViewModel.HasRoot ? Visibility.Collapsed : Visibility.Visible;
        WelcomeHelp.Text = ViewModel.HasRoot ? "Double-click a file or select it and press Enter.\nUse File → Open workspace to open a source project." : "";
        WelcomeHelp.Visibility = WelcomeHelp.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeHelp.Margin = new(0, ViewModel.HasRoot ? 24 : 8, 0, 0);
        // A source map's Animation menu opens definitions in Animation mode and drives its transport.
        AnimationMenu.IsEnabled = animation != null || doc?.SourceWorld != null;
        foreach (var column in AssetGrid.Columns.Skip(1)) column.Visibility = doc is { SourceWorld: null, Document.Animations: not null } ? Visibility.Visible : Visibility.Collapsed;
        // A rebuilding source world accepts no other edit or save until the rebuilt world is shown.
        bool rebuilding = doc?.SourceWorld is { } rebuildingWorld && (rebuildingWorld.IsRebuilding || sourceWorkspaceBusy || rebuildingWorld.Workspace.IsSaving || scene?.IsZonePainting == true || zoneTool.Applying);
        // Only a source world saves, undoes and redoes; compiled documents are read only.
        DocumentSave.IsEnabled = !rebuilding && doc?.SourceWorld != null;
        DocumentSave.ToolTip = doc?.SourceWorld is { } sourceWorld ? SourceSaveTip(sourceWorld.Workspace) : "Save (Ctrl+S)";
        DocumentUndo.IsEnabled = !rebuilding && doc?.SourceWorld?.Workspace.CanUndo == true;
        DocumentRedo.IsEnabled = !rebuilding && doc?.SourceWorld?.Workspace.CanRedo == true;
        SaveMenu.IsEnabled = DocumentSave.IsEnabled;
        UndoMenu.IsEnabled = DocumentUndo.IsEnabled; RedoMenu.IsEnabled = DocumentRedo.IsEnabled;
        DocumentUndo.ToolTip = doc?.SourceWorld?.Workspace.UndoLabel is string undo ? "Undo: " + undo + " (Ctrl+Z)" : "Undo (Ctrl+Z)";
        DocumentRedo.ToolTip = doc?.SourceWorld?.Workspace.RedoLabel is string redo ? "Redo: " + redo + " (Ctrl+Y)" : "Redo (Ctrl+Y)";
    }
    /// <summary>What Save writes in a source project: every changed source file, not only this world's.</summary>
    private static string SourceSaveTip(zStudio.Core.Sources.SourceWorkspace workspace)
    {
        var dirty = workspace.DirtyFiles;
        return dirty.Count == 0 ? "The source project has no unsaved edits (Ctrl+S)"
            : $"Save the source project's {dirty.Count} changed file{(dirty.Count == 1 ? "" : "s")} (Ctrl+S):\n" + string.Join("\n", dirty.Take(12)) + (dirty.Count > 12 ? $"\n… and {dirty.Count - 12} more" : "");
    }
    private void ObserveDocumentCommands(DocumentModel? document)
    {
        if (commandDocument == document) return;
        if (commandDocument is { } previous)
        {
            previous.SourceWorldChanged -= UpdateDocumentCommands;
        }
        commandDocument = document;
        if (document != null)
        {
            document.SourceWorldChanged += UpdateDocumentCommands;
        }
    }
    private void AttachAnimationWorkspace(AnimationEditor editor)
    {
        AttachInspection(editor.Viewport);
        editor.SceneHierarchyChanged += RefreshSceneTree;
        int preferredTool = Layout.ToolTab, preferredInspector = Layout.InspectorTab; detachingWorkspace = true;
        ProgramHost.Content = editor.ProgramView;
        ReferencesHost.Content = editor.ReferencesView; PreviewSetupHost.Content = editor.PreviewSetupView;
        DispatchHost.Content = editor.DispatchView; EventLogHost.Content = editor.EventLogView;
        RuntimeHost.Content = editor.RuntimeView; ProblemsHost.Content = editor.ProblemsView;
        editor.SetOperationDiagnostics(ViewModel.Problems);
        editor.SourceSelectionChanged += UpdateDocumentCommands;
        editor.SetupRequested += () => { Layout.InspectorVisible = true; inspectorTemporary = true; InspectorTabs.SelectedItem = PreviewSetupTab; ArrangeWorkspace(); };
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
        arrangingWorkspace = true;
        using var lease = BeginLayoutChange();
        try
        {
            RefreshPresentation();
            UpdateSourceMapPresentation();
            if (presentationUiHidden)
            {
                // The view alone: no panes or splitters (their preferences stay as they are).
                if (ViewModel.IsSourceProject) ArrangeSourcePanes(false);
                NavigatorHost.Visibility = NavigatorSplitter.Visibility = InspectorHost.Visibility = InspectorSplitter.Visibility = BottomTools.Visibility = ToolsSplitter.Visibility = Visibility.Collapsed;
                FilesColumn.Width = NavigatorSplitterColumn.Width = PropertiesColumn.Width = PropertiesSplitterColumn.Width = new(0);
                ToolsRow.Height = ToolsSplitterRow.Height = new(0);
                animation?.ShowInspectorReplacement(false);
                Workspace.Visibility = Visibility.Visible;
                return;
            }
            if (ViewModel.IsSourceProject) { ArrangeSourceMapWorkspace(); return; }
            var layout = Layout; bool focus = layout.Preset == "Focus preview";
            double width = Workbench.ActualWidth;
            // Keep both side panes when the center still has a usable width.
            double navigatorBreakpoint = layout.NavigatorWidth + (animation != null && layout.InspectorVisible ? layout.InspectorWidth + 12 : 6) + 360;
            navigatorCollapsed = width < navigatorBreakpoint + (navigatorCollapsed ? 40 : 0);
            bool nav = ViewModel.HasRoot && layout.NavigatorVisible && !focus && (!navigatorCollapsed || navigatorTemporary);
            bool inspector = animation != null && layout.InspectorVisible && !focus;
            double splitThreshold = layout.FilesWidth + layout.NavigatorWidth + 600 + 12 + (inspector ? layout.InspectorWidth + 6 : 0);
            bool splitFiles = nav && width >= splitThreshold + (filesDetached ? 0 : 40);
            SetFilesDetached(splitFiles);
            double navWidth = nav ? layout.NavigatorWidth + (splitFiles ? layout.FilesWidth + 6 : 0) : 0;
            DetachedFilesColumn.Width = new(splitFiles ? layout.FilesWidth : 0);
            DetachedFilesColumn.MinWidth = splitFiles ? 200 : 0;
            DetachedFilesColumn.MaxWidth = splitFiles ? 650 : double.PositiveInfinity;
            FilesSplitterColumn.Width = new(splitFiles ? 6 : 0);
            NavigatorContentColumn.Width = new(1,GridUnitType.Star);
            bool replacement = inspector && width - navWidth - layout.InspectorWidth - 12 < 360;
            if (replacement && !inspectorTemporary) inspector = false;
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
        }
        finally
        {
            arrangingWorkspace = false;
        }
    }
    private void WorkspaceSplitterCompleted(object sender, DragCompletedEventArgs e)
    {
        if (arrangingWorkspace) return;
        // A source map's panes keep session widths; the compiled explorer's preferences stay as they are.
        if (ViewModel.IsSourceProject && sender == InspectorSplitter)
        { sourcePropertiesWidth = Math.Clamp(PropertiesColumn.ActualWidth, 260, 650); ArrangeWorkspace(); return; }
        if (ViewModel.IsSourceProject && sender == NavigatorSplitter)
        { sourceOutlinerWidth = Math.Clamp(FilesColumn.ActualWidth, 200, 650); ArrangeWorkspace(); return; }
        if (ViewModel.IsSourceProject && sender == ToolsSplitter && InAnimationMode)
        {
            // The panel's height (Diagnostics' strip keeps its own).
            double strip = sourceDock?.RowDefinitions[2].ActualHeight is double rows && rows > 0 ? rows + 6 : 0;
            animationPanelHeight = Math.Max(AnimationPanel.MinimumPanelHeight, ToolsRow.ActualHeight - strip); RememberAnimationLayout(); ArrangeWorkspace(); return;
        }
        if (ViewModel.IsSourceProject && sender == ToolsSplitter)
        { sourceToolsHeight = Math.Clamp(ToolsRow.ActualHeight, 100, 700); RememberSourceTools(); ArrangeWorkspace(); return; }
        var layout = Layout;
        if (sender == NavigatorSplitter) layout.NavigatorWidth = FilesColumn.ActualWidth - (filesDetached ? layout.FilesWidth + 6 : 0);
        if (sender == FilesSplitter) { layout.FilesWidth = DetachedFilesColumn.ActualWidth; layout.NavigatorWidth = NavigatorContentColumn.ActualWidth; }
        if (sender == InspectorSplitter) layout.InspectorWidth = PropertiesColumn.ActualWidth;
        if (sender == ToolsSplitter) { layout.ToolsHeight = ToolsRow.ActualHeight; toolsMaximized = false; }
        layout.Normalize(); ArrangeWorkspace(); SaveWorkspacePreferences();
    }
    private void SaveWorkspacePreferences()
    {
        if (detachingWorkspace) return;
        if (animation != null && ProgramHost.Content != null && InspectorTabs.SelectedIndex >= 0) Layout.InspectorTab = InspectorTabs.SelectedIndex;
        Layout.ToolTab = Math.Max(0, ToolTabs.SelectedIndex);
    }
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
        if (ViewModel.IsSourceProject) { SetToolsVisible(!SourceToolsVisible); return; }
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
    /// <summary>The workbench's compact metrics: 13-point text, 32-DIP controls with 16-DIP icons.</summary>
    private void ApplyMetrics()
    {
        using var lease = BeginLayoutChange();
        propertiesWindow?.ApplyAppearance();
        FontSize = 13;
        Resources["PreviewControlSize"] = 32d;
        Resources["PreviewIconSize"] = 16d;
        Resources["WorkspaceControlPadding"] = new Thickness(8, 4, 8, 4);
    }
    private void InspectorSelectionChanged(object sender, SelectionChangedEventArgs e) { if (ready && e.Source == InspectorTabs) SaveWorkspacePreferences(); }
    private void ToolSelectionChanged(object sender, SelectionChangedEventArgs e) { if (ready && e.Source == ToolTabs) SaveWorkspacePreferences(); }
    private void AnimationPlayClick(object sender, RoutedEventArgs e) => animation?.TogglePlayback();
    private void AnimationStopClick(object sender, RoutedEventArgs e) => animation?.Stop();
    private void AnimationPreviousClick(object sender, RoutedEventArgs e) => animation?.Step(-1);
    private void AnimationNextClick(object sender, RoutedEventArgs e) => animation?.Step(1);
    private void PreviewSetupClick(object sender, RoutedEventArgs e) { if (animation != null) { Layout.InspectorVisible = true; inspectorTemporary = true; InspectorTabs.SelectedItem = PreviewSetupTab; ArrangeWorkspace(); } }
    private async void FileProblemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        bool row = false;
        for (var source = e.OriginalSource as DependencyObject; source != null && !row; source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            row = source is DataGridRow;
        if (row && FileProblems.SelectedItem is StudioProblem problem) await NavigateProblemAsync(problem);
    }
    internal async Task NavigateProblemAsync(StudioProblem problem)
    {
        // A source project opens no individual files (and hides the status text): the problem's whole text is what there is to see.
        if (ViewModel.IsSourceProject) { DetailDialog.Show(this, "Diagnostics", $"{problem.Severity} · {problem.Category}\n{problem.Message}\n\n{problem.Details}"); return; }
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
