using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// Animation mode (design/animation-redesign.md §6–§7, §13, §17): a layout of the map editor for one opened definition. The 3D
/// view stays on top with its camera, LOD and difficulty; the animation panel (transport, Structure | Timing | Split) is below
/// it; the right pane is Inspector | Scenario; Exit (Shift+Esc) returns to the map layout and keeps the scenario. Entering
/// never moves the camera, plays, evaluates paths or measures a duration.
/// </summary>
public partial class MainWindow
{
    /// <summary>The opened definition and the binding the mode plays (-1 when the definition is not bound on this map).</summary>
    private WorldAnimationTarget? animationMode;
    private readonly List<(int Definition, int Entry)> animationBack = [], animationForward = [];
    private const int AnimationHistory = 32;
    /// <summary>What Animation mode showed when it was exited on this map (Animation → Return to …, the status bar's kept item).</summary>
    private (DocumentModel Document, int Definition, int Entry)? animationKept;
    private AnimationPanel? animationPanel;
    private AnimationScenarioPanel? scenarioPanel;
    private string animationRightTab = "inspector";
    private string? animationView;
    private double? animationPanelHeight;
    private bool animationDiagnostics;
    private double animationDiagnosticsHeight = 140, animationSplitShare = .58;
    /// <summary>A preview failure in Animation mode: the transport's status line says it (Diagnostics only lists it).</summary>
    private string? animationFailure;
    private Task worldAnimationReevaluation = Task.CompletedTask;
    private Grid? sourceDock;
    private GridSplitter? diagnosticsSplitter;
    private StackPanel? animationScope;
    private Button? animationExit, animationDefinitionPicker, animationBindingPicker, animationBackButton, animationForwardButton, scenarioChip;
    private TextBlock? animationTitle, animationCaption, scenarioChipText, animationStatusText;
    private System.Windows.Shapes.Ellipse? scenarioChipDot;
    private StackPanel? animationStatusGroup;
    private Button? animationKeptButton;
    private TextBlock? animationKeptText;
    private Popup? animationDefinitionPopup, animationCommandOverflowPopup;
    private TextBlock? animationExitLabel;
    private Button? animationCommandOverflow;
    private StackPanel? animationOverflowItems;

    internal bool InAnimationMode => animationMode != null;
    private Task<bool> ResolveSelectionDraftsAsync(DocumentModel? doc) => ResolveInspectionDraftsAsync(doc);

    // ---------------------------------------------------------------- entering, switching, exiting

    /// <summary>
    /// Opens <paramref name="definition"/> in Animation mode on <paramref name="entry"/> (or the binding it shows, or its first):
    /// the binding becomes the scenario's primary start (in place when it already is a start; the previous primary stays a
    /// start). Drafts must be resolved first. The camera does not move and nothing runs.
    /// </summary>
    private async Task OpenAnimationModeAsync(DocumentModel document, int definition, int? entry, CancellationToken token, bool history = true)
    {
        token.ThrowIfCancellationRequested();
        if (document != shownDocument || !sourceChromeActive || document.Document is not SourceSceneSnapshot source || scene?.Mission == null)
            throw new StudioCommandException("not_ready", "Open a source map first.");
        var found = source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == definition)
            ?? throw new StudioCommandException("stale_record", "The animation definition is not in this map.");
        if (entry is int chosen && !found.Entries.Contains(chosen))
            throw new StudioCommandException("invalid_argument", "Choose a binding of this definition.");
        RequireNoDrafts(document);
        if (scene.IsFlyActive || scene.IsPickupDragging || scene.IsZonePainting || scene.TerrainBrushActive || scene.HasTransformDraft || scene.AiPathEditingNetwork != null)
            throw new StudioCommandException("busy", "Finish navigation or editing in the view before opening Animation mode.");
        int binding = entry ?? (animationMode?.Definition == definition && found.Entries.Contains(animationMode.Entry) ? animationMode.Entry
            : sourceAnimationEntry is int shown && found.Entries.Contains(shown) ? shown : found.Entries.Count > 0 ? found.Entries[0] : -1);
        var target = new WorldAnimationTarget(document, document.Revision, definition, binding);
        bool entering = animationMode == null;
        if (animationMode != target)
        {
            if (binding > 0)
            {
                // The opened binding is the primary start; a full schedule refuses a 33rd start rather than dropping one.
                var scenarios = WorldScenarios();
                try { _ = scenarios.Current.WithPrimary(binding, keepPrevious: true); }
                catch (InvalidOperationException ex) { throw new StudioCommandException("limit_exceeded", ex.Message); }
            }
            if (!entering && history && animationMode!.Definition != definition)
            {
                animationBack.Add((animationMode.Definition, animationMode.Entry));
                if (animationBack.Count > AnimationHistory) animationBack.RemoveAt(0);
                animationForward.Clear();
            }
            if (entering && ZoneToolEngaged) LeaveZoneTool();
            DetachWorldAnimationRun();
            animationMode = target; animationKept = null; animationFailure = null; previewId = Guid.NewGuid();
            if (binding > 0)
            {
                var scenarios = WorldScenarios();
                scenarios.Set(scenarios.Current.WithPrimary(binding, keepPrevious: true), "open");
            }
            worldAnimationScenarioEpoch = Guid.NewGuid(); worldAnimationDuration = null;
            worldAnimationExpanded.Clear(); worldAnimationCollapsed.Clear(); worldAnimationPageCounts.Clear();
            ParachuteScenarioChanged();
            if (entering && animationPanel != null) animationPanel.TipShown = !ViewModel.Settings.AnimationModeTipDismissed;
        }
        RefreshAnimationModeChrome(); RefreshWorldAnimationTimeline(); RefreshScenarioPanel(); ArrangeWorkspace();
        // The Inspector shows the opened definition's card: its World tree row is selected (the camera stays).
        await EnsureAnimationCardAsync(document, definition, binding > 0 ? binding : null, token);
        if (animationMode == target) { RefreshAnimationModeChrome(); RefreshWorldAnimationTimeline(); }
    }

    /// <summary>Back to the map layout: the run stops, the scenario is kept, the definition stays selected with its card.</summary>
    private async Task ExitAnimationModeAsync(CancellationToken token)
    {
        if (animationMode is not { } mode) return;
        DetachWorldAnimationRun();
        animationKept = (mode.Document, mode.Definition, mode.Entry);
        animationMode = null; animationFailure = null; previewId = Guid.NewGuid();
        animationBack.Clear(); animationForward.Clear();
        animationPanel?.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        RefreshAnimationModeChrome(); RefreshWorldAnimationTimeline(); ArrangeWorkspace();
        if (mode.Document == shownDocument && !mode.Document.IsDisposed && SelectedAnimationDefinition()?.Definition.Index != mode.Definition)
        {
            try { await EnsureAnimationCardAsync(mode.Document, mode.Definition, mode.Entry > 0 ? mode.Entry : null, token); }
            catch (StudioCommandException ex) when (ex.Code is "not_ready" or "stale_record" or "context_changed") { }
        }
    }

    private async Task AnimationHistoryAsync(bool forward, CancellationToken token)
    {
        var (from, to) = forward ? (animationForward, animationBack) : (animationBack, animationForward);
        if (animationMode is not { } current || from.Count == 0) throw new StudioCommandException("not_ready", forward ? "Nothing to go forward to." : "Nothing to go back to.");
        var (definition, entry) = from[^1];
        var source = current.Document.Document as SourceSceneSnapshot;
        int? binding = source?.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == definition)?.Entries.Contains(entry) == true ? entry : null;
        await OpenAnimationModeAsync(current.Document, definition, binding, token, history: false);
        from.RemoveAt(from.Count - 1); to.Add((current.Definition, current.Entry));
        RefreshAnimationModeChrome();
    }

    /// <summary>
    /// After the shown map's document or revision changed (a source edit, undo or redo, a rebuild): Animation mode follows its
    /// definition on the rebuilt map, on the scenario's (remapped) primary binding; another map ends it.
    /// </summary>
    private void RebaseAnimationMode()
    {
        if (animationMode is not { } mode) return;
        var document = shownDocument;
        if (document?.SourceWorld is not { } world || mode.Document.SourceWorld is not { } opened || world.Workspace != opened.Workspace || world.Mission != opened.Mission)
        {
            // Another map, or none: the mode ends with its own map (its scenario stays saved with that map).
            if (document != null && document.SourceWorld != null || ViewModel.SelectedDocument?.SourceWorld == null) EndAnimationModeForMap();
            return;
        }
        if (mode.Document == document && mode.Revision == document.Revision) return;
        if (document.Document is not SourceSceneSnapshot source || scene?.Mission == null) return;
        var definition = source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == mode.Definition);
        if (definition == null) { EndAnimationModeForMap(); Notice("The opened animation definition is no longer in the map; Animation mode ended."); return; }
        int entry = WorldScenarios().Current.Primary is int primary && definition.Entries.Contains(primary) ? primary
            : definition.Entries.Contains(mode.Entry) ? mode.Entry : definition.Entries.Count > 0 ? definition.Entries[0] : -1;
        DetachWorldAnimationRun();
        animationMode = new(document, document.Revision, mode.Definition, entry); previewId = Guid.NewGuid();
        if (entry > 0 && WorldScenarios().Current.Primary != entry)
            try { var scenarios = WorldScenarios(); scenarios.Set(scenarios.Current.WithPrimary(entry, keepPrevious: true), "open"); }
            catch (InvalidOperationException) { }
        RefreshAnimationModeChrome(); RefreshWorldAnimationTimeline(); RefreshScenarioPanel(); ArrangeWorkspace();
    }
    private void EndAnimationModeForMap()
    {
        DetachWorldAnimationRun();
        animationMode = null; animationKept = null; animationFailure = null; previewId = Guid.NewGuid();
        animationBack.Clear(); animationForward.Clear();
        RefreshAnimationModeChrome(); RefreshWorldAnimationTimeline(); ArrangeWorkspace();
    }

    /// <summary>The GUI's Open: drafts are asked about first (Apply / Discard / Cancel); Cancel stays where it is.</summary>
    private Task OpenAnimationFromGuiAsync(DocumentModel document, int definition, int? entry) => RunUi(async () =>
    {
        if (!await ResolveSelectionDraftsAsync(document)) return;
        await OpenAnimationModeAsync(document, definition, entry, CancellationToken.None);
    });
    private Task ExitAnimationFromGuiAsync() => RunUi(async () =>
    {
        if (animationMode is { } mode && !await ResolveSelectionDraftsAsync(mode.Document)) return;
        await ExitAnimationModeAsync(CancellationToken.None);
    });

    private void OutlinerOpenAnimationClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is SceneTreeItem row) OpenAnimationRow(row);
    }
    /// <summary>A definition or binding row's Open (▷, Enter, a double-click, its context menu).</summary>
    private void OpenAnimationRow(SceneTreeItem row)
    {
        if (row.Owner != sceneTree || sceneTreeDocument is not { IsDisposed: false } document || !row.CanOpenAnimation) return;
        var (definition, entry) = row.Section switch
        {
            AnimationBindingEntry binding => (binding.Definition.Definition.Index, (int?)binding.Binding.Entry),
            DefinitionEntry definitionRow => (definitionRow.Definition.Index, (int?)null),
            _ => (-1, null),
        };
        if (definition >= 0) _ = OpenAnimationFromGuiAsync(document, definition, entry);
    }
    private void SceneOpenAnimationClick(object sender, RoutedEventArgs e) { if (contextScene is { } row) OpenAnimationRow(row); }

    /// <summary>A row's animated mark: a flyout of the animations affecting the object, each with its reason and Open.</summary>
    private void OutlinerAnimationGlyphClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: SceneTreeItem { AnimationUses: { } uses } row } button || row.Owner != sceneTree || sceneTree.Sections is not { } sections
            || sceneTreeDocument is not { IsDisposed: false } document) return;
        var links = AnimationLinks(sections, uses);
        StackPanel body = new() { Margin = new(10, 8, 10, 8), Width = 330 };
        TextBlock title = new() { Text = "Animations affecting " + row.DisplayName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 0, 6) };
        body.Children.Add(title);
        Popup flyout = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PlacementTarget = button, VerticalOffset = 4 };
        foreach (var link in links)
        {
            var line = AnimationLinkRow(document, link.Definition, link.Entry, link.Name, link.Reason);
            // Choosing closes the flyout.
            foreach (var choice in ((DockPanel)line).Children.OfType<Button>()) choice.Click += (_, _) => flyout.IsOpen = false;
            body.Children.Add(line);
        }
        if (links.Length == 0) body.Children.Add(new TextBlock { Text = "None of them is a definition of this map.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (uses.Truncated)
        {
            TextBlock more = new() { Text = "More animations affect it; the list keeps the first 8.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            body.Children.Add(more);
        }
        Border frame = new() { Child = body, CornerRadius = new(8), BorderThickness = new(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        AutomationProperties.SetName(frame, title.Text);
        flyout.Child = frame; flyout.IsOpen = true;
    }
    private void SceneCopyAnimationNameClick(object sender, RoutedEventArgs e)
    {
        string? name = contextScene?.Section switch { DefinitionEntry d => d.Definition.Name, AnimationBindingEntry b => b.Binding.Name, _ => null };
        if (name != null) try { Clipboard.SetText(name); } catch (System.Runtime.InteropServices.ExternalException) { Notice("The clipboard is in use; copy the name again."); }
    }

    // ---------------------------------------------------------------- layout

    private void InitializeSourceDock()
    {
        if (sourceDock != null) return;
        sourceDock = new();
        sourceDock.RowDefinitions.Add(new() { Height = new(0) });
        sourceDock.RowDefinitions.Add(new() { Height = new(0) });
        sourceDock.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        animationPanel = CreateAnimationPanel(); animationPanel.Visibility = Visibility.Collapsed;
        sourceDock.Children.Add(animationPanel);
        diagnosticsSplitter = new() { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Background = Brushes.Transparent, Visibility = Visibility.Collapsed, Focusable = true };
        AutomationProperties.SetName(diagnosticsSplitter, "Resize Diagnostics");
        diagnosticsSplitter.DragCompleted += (_, _) => { animationDiagnosticsHeight = Math.Max(96, sourceDock.RowDefinitions[2].ActualHeight); animationPanelHeight = sourceDock.RowDefinitions[0].ActualHeight; RememberAnimationLayout(); ArrangeWorkspace(); };
        Grid.SetRow(diagnosticsSplitter, 1); sourceDock.Children.Add(diagnosticsSplitter);
        Border diagnostics = new() { ClipToBounds = true };
        diagnostics.SetResourceReference(StyleProperty, "SourcePane");
        sourceDiagnosticsContent = new() { Focusable = false, Margin = new(0, 0, 0, 2) };
        diagnostics.Child = sourceDiagnosticsContent;
        Grid.SetRow(diagnostics, 2); sourceDock.Children.Add(diagnostics);
        SourceDiagnosticsHost.Content = sourceDock;
        animationPanel.SetTiming(WorldTimeline, WorldTimeline.Tools);
    }

    /// <summary>Animation mode's bottom: the panel (its remembered height, 52 % of the column by default; 56 % narrow) and the Diagnostics strip under it.</summary>
    private void ArrangeAnimationPanel()
    {
        InitializeSourceDock();
        double column = Workbench.ActualHeight;
        bool narrow = Workbench.ActualWidth < 1300;
        double maximum = Math.Max(AnimationPanel.MinimumPanelHeight, column - 160 - 6);
        double panel = Math.Clamp(animationPanelHeight ?? Math.Round(column * (narrow ? .56 : .52)), Math.Min(AnimationPanel.MinimumPanelHeight, maximum), maximum);
        double strip = animationDiagnostics ? Math.Max(96, animationDiagnosticsHeight) : 0;
        // The strip never takes the view below 160 or the panel below its minimum; without room it stays hidden.
        if (strip > 0 && panel + 6 + strip > maximum) panel = Math.Max(AnimationPanel.MinimumPanelHeight, maximum - 6 - strip);
        if (strip > 0 && panel + 6 + strip > maximum) strip = 0;
        ToolsRow.Height = new(panel + (strip > 0 ? 6 + strip : 0)); ToolsSplitterRow.Height = new(6);
        BottomTools.Visibility = ToolsSplitter.Visibility = Visibility.Visible;
        sourceDock!.RowDefinitions[0].Height = strip > 0 ? new(1, GridUnitType.Star) : new(1, GridUnitType.Star);
        sourceDock.RowDefinitions[0].MinHeight = AnimationPanel.MinimumPanelHeight;
        sourceDock.RowDefinitions[1].Height = new(strip > 0 ? 6 : 0);
        sourceDock.RowDefinitions[2].Height = new(strip);
        sourceDock.RowDefinitions[2].MinHeight = strip > 0 ? 96 : 0;
        diagnosticsSplitter!.Visibility = strip > 0 ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)sourceDiagnosticsContent!.Parent).Visibility = strip > 0 ? Visibility.Visible : Visibility.Collapsed;
        animationPanel!.Visibility = Visibility.Visible;
        animationPanel.SetView(animationView ?? DefaultAnimationView());
        animationPanel.SetSplit(animationSplitShare);
    }
    /// <summary>The map layout's bottom: Diagnostics alone.</summary>
    private void ArrangeMapDock(bool tools)
    {
        InitializeSourceDock();
        sourceDock!.RowDefinitions[0].Height = new(0); sourceDock.RowDefinitions[0].MinHeight = 0;
        sourceDock.RowDefinitions[1].Height = new(0);
        sourceDock.RowDefinitions[2].Height = new(1, GridUnitType.Star); sourceDock.RowDefinitions[2].MinHeight = 0;
        animationPanel!.Visibility = Visibility.Collapsed; diagnosticsSplitter!.Visibility = Visibility.Collapsed;
        ((FrameworkElement)sourceDiagnosticsContent!.Parent).Visibility = tools ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>Until the Structure view is built, Timing; Split where the panel is wide enough (2,560 DIP windows).</summary>
    private string DefaultAnimationView() => Workbench.ActualWidth >= 2200 ? "split" : "timing";
    private void RememberAnimationLayout()
    {
        double? height = animationPanelHeight; bool strip = animationDiagnostics;
        ViewModel.RememberProjectSetting(root => SourceProjectSettings.WriteAnimationLayout(root, height, strip));
    }
    /// <summary>The project's remembered Animation mode layout (panel height, Diagnostics strip).</summary>
    private void ApplyAnimationProjectView(SourceProjectView? view)
    {
        animationPanelHeight = view?.AnimationPanelHeight is double height && double.IsFinite(height) ? Math.Clamp(height, AnimationPanel.MinimumPanelHeight, 4000) : null;
        animationDiagnostics = view?.AnimationDiagnostics ?? false;
    }

    private AnimationPanel CreateAnimationPanel()
    {
        AnimationPanel panel = new();
        panel.TransportRequested += action => _ = ReportUi(() => WorldAnimationTransportAsync(action));
        panel.SeekRequested += seconds => _ = ReportUi(() => WorldAnimationTransportAsync("seek", seconds));
        panel.OptionRequested += (key, value) => _ = ReportUi(() => WorldAnimationOptionAsync(key, value));
        panel.ViewRequested += view => { animationView = view; ArrangeWorkspace(); };
        panel.SplitChanged += share => animationSplitShare = share;
        panel.FitRangeRequested += () => _ = ReportUi(FitWorldAnimationScenario);
        panel.DurationRequested += () => _ = ReportUi(() => WorldAnimationTransportAsync("duration"));
        panel.TipDismissed += () => ViewModel.Settings.AnimationModeTipDismissed = true;
        panel.DiagnosticsRequested += () => { animationDiagnostics = true; RememberAnimationLayout(); ArrangeWorkspace(); };
        return panel;
    }

    // ---------------------------------------------------------------- the right pane

    private void AnimationPaneTabClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tab) ShowAnimationRightTab(tab);
    }
    private void ShowAnimationRightTab(string tab)
    {
        if (tab is not ("inspector" or "scenario")) throw new StudioCommandException("invalid_argument", "Choose the inspector or scenario tab.");
        if (!InAnimationMode) throw new StudioCommandException("not_ready", "Open an animation in Animation mode first.");
        animationRightTab = tab;
        if (!sourcePropertiesVisible) sourcePropertiesVisible = true;
        RefreshAnimationPane(); ArrangeWorkspace();
    }
    private void RefreshAnimationPane()
    {
        bool mode = InAnimationMode && sourceChromeActive;
        AnimationPaneTabs.Visibility = mode ? Visibility.Visible : Visibility.Collapsed;
        bool scenario = mode && animationRightTab == "scenario";
        AnimationInspectorTab.IsChecked = !scenario; AnimationScenarioTab.IsChecked = scenario;
        if (scenario && scenarioPanel == null) { scenarioPanel = CreateScenarioPanel(); AnimationScenarioHost.Content = scenarioPanel; RefreshScenarioPanel(); }
        AnimationScenarioHost.Visibility = scenario ? Visibility.Visible : Visibility.Collapsed;
        SourcePropertiesHost.Visibility = scenario ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- the command bar, title row and status bar

    /// <summary>Shows Animation mode's chrome (or the map layout's): the scope slot, the scenario chip, the status bar's items, the right pane.</summary>
    private void RefreshAnimationModeChrome()
    {
        if (!sourceChromeActive)
        {
            // Leaving the map: the groups a narrow window moved to ⋯ go back first.
            if (animationScope != null) { animationScope.Visibility = Visibility.Collapsed; ArrangeAnimationCommandBar(); }
            if (scenarioChip != null) scenarioChip.Visibility = Visibility.Collapsed;
            return;
        }
        EnsureAnimationChrome();
        bool mode = InAnimationMode && !presentationUiHidden;
        SourceScopeHost.Visibility = InAnimationMode ? Visibility.Collapsed : Visibility.Visible;
        animationScope!.Visibility = InAnimationMode ? Visibility.Visible : Visibility.Collapsed;
        scenarioChip!.Visibility = InAnimationMode ? Visibility.Visible : Visibility.Collapsed;
        if (animationMode is { } opened && opened.Document.Document is SourceSceneSnapshot source && source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == opened.Definition) is { } definition)
        {
            animationTitle!.Text = definition.Name;
            string root = opened.Entry > 0 && opened.Document == shownDocument ? BindingLabel(opened.Entry) : "Not bound on " + (scene?.Mission?.Layout.Difficulty.ToString() ?? "this difficulty");
            animationCaption!.Text = $"Definition · {System.IO.Path.GetFileName(definition.File)} · {root}";
            animationCaption.ToolTip = $"{definition.File} · definition {definition.Ordinal + 1}";
            animationDefinitionPicker!.ToolTip = $"{definition.Name}: choose another definition of this map";
            AutomationProperties.SetName(animationDefinitionPicker, "Opened definition " + definition.Name);
            animationBindingPicker!.Visibility = definition.Entries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            animationBackButton!.IsEnabled = animationBack.Count > 0; animationForwardButton!.IsEnabled = animationForward.Count > 0;
            animationBackButton.ToolTip = animationBack.Count > 0 ? $"Back to {DefinitionName(animationBack[^1].Definition)} (Alt+←)" : "Back (Alt+←)";
            animationForwardButton.ToolTip = animationForward.Count > 0 ? $"Forward to {DefinitionName(animationForward[^1].Definition)} (Alt+→)" : "Forward (Alt+→)";
            animationStatusText!.Text = "Animation mode · " + definition.Name;
        }
        animationStatusGroup!.Visibility = mode ? Visibility.Visible : Visibility.Collapsed;
        if (sectionCard != null) sectionCard.OpenAnimationShown = !InAnimationMode;
        ArrangeAnimationCommandBar();
        RefreshScenarioChip();
        RefreshAnimationPane();
        // Overlay and Zones hide in Animation mode (zone painting cannot run there).
        RefreshCommandGroups();
        SourceZonesGroup.Visibility = InAnimationMode ? Visibility.Collapsed : SourceZonesGroup.Visibility;
    }
    private string DefinitionName(int definition) => (shownDocument?.Document as SourceSceneSnapshot)?.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == definition)?.Name ?? "definition " + definition.ToString(CultureInfo.InvariantCulture);
    private string BindingLabel(int entry)
    {
        try { var binding = GetWorldAnimationStructurePlan().Entry(entry); return $"{binding.Root} #{entry}"; }
        catch (Exception ex) when (ex is StudioCommandException or ArgumentException or InvalidOperationException) { return "#" + entry.ToString(CultureInfo.InvariantCulture); }
    }

    /// <summary>
    /// A narrow window (design §7): Exit keeps a short label and the Camera and Display groups go to ⋯, so the command bar
    /// stays on one line; the map layout keeps its groups.
    /// </summary>
    private void ArrangeAnimationCommandBar()
    {
        bool narrow = InAnimationMode && Workbench.ActualWidth < NarrowCommandBar;
        animationExitLabel!.Text = narrow ? "Exit" : "Exit animation";
        animationCommandOverflow!.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        var groups = new FrameworkElement[] { SourceCameraGroup, SourceDisplayGroup };
        if (narrow && SourceCameraGroup.Parent == SourceCommandItems)
            foreach (var group in groups) { SourceCommandItems.Children.Remove(group); animationOverflowItems!.Children.Add(group); }
        else if (!narrow && SourceCameraGroup.Parent == animationOverflowItems)
        {
            animationCommandOverflowPopup!.IsOpen = false;
            int at = SourceCommandItems.Children.IndexOf(SourcePickers) + 1;
            foreach (var group in groups) { animationOverflowItems!.Children.Remove(group); SourceCommandItems.Children.Insert(at++, group); }
        }
    }
    private const double NarrowCommandBar = 1450;

    private void RefreshScenarioChip()
    {
        if (scenarioChip == null) return;
        var scenario = worldAnimationConfiguration;
        string full = "Scenario · " + ScenarioSummary(scenario);
        bool narrow = Workbench.ActualWidth < NarrowCommandBar;
        scenarioChipText!.Text = narrow ? $"Scenario · {scenario.Starts.Count}" : full;
        scenarioChip.ToolTip = full + (scenario.IsCustomized ? "\nDiffers from the defaults." : "") + "\nShow the Scenario tab";
        scenarioChipDot!.Visibility = scenario.IsCustomized ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(scenarioChip, full);
        // After Exit, the status bar keeps the scenario in view and re-enters on a click.
        bool kept = !InAnimationMode && animationKept is { } keptMode && keptMode.Document.SourceWorld != null && keptMode.Document.SourceWorld == shownDocument?.SourceWorld && !presentationUiHidden;
        animationKeptButton!.Visibility = kept ? Visibility.Visible : Visibility.Collapsed;
        if (kept)
        {
            animationKeptText!.Text = $"Animation scenario kept · {scenario.Starts.Count} start{(scenario.Starts.Count == 1 ? "" : "s")}";
            animationKeptButton.ToolTip = $"Return to Animation mode on {DefinitionName(animationKept!.Value.Definition)}";
        }
    }
    /// <summary>"2 starts · 24 calls · 1 forced".</summary>
    private static string ScenarioSummary(AnimationScenario scenario) => string.Join(" · ", new[]
    {
        $"{scenario.Starts.Count} start{(scenario.Starts.Count == 1 ? "" : "s")}",
        scenario.IncludedCalls.Count > 0 ? $"{scenario.IncludedCalls.Count.ToString("N0", CultureInfo.InvariantCulture)} call{(scenario.IncludedCalls.Count == 1 ? "" : "s")}" : null,
        scenario.Conditions.Count > 0 ? $"{scenario.Conditions.Count} forced" : null,
    }.OfType<string>());

    private void EnsureAnimationChrome()
    {
        if (animationScope != null) return;
        // The scope slot: Exit animation, the definition's title block (its picker, the binding picker), Back and Forward.
        StackPanel exitContent = new() { Orientation = Orientation.Horizontal };
        exitContent.Children.Add(new PreviewIcon { Kind = "Back", Width = 14, Height = 14, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        animationExitLabel = new() { Text = "Exit animation", VerticalAlignment = VerticalAlignment.Center };
        exitContent.Children.Add(animationExitLabel);
        animationExit = new() { Content = exitContent, Height = 32, Padding = new(10, 0, 14, 0), Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Return to the map layout (Shift+Esc). The scenario is kept." };
        AutomationProperties.SetName(animationExit, "Exit animation");
        animationExit.Click += (_, _) => _ = ExitAnimationFromGuiAsync();
        animationTitle = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 };
        StackPanel titleContent = new() { Orientation = Orientation.Horizontal };
        titleContent.Children.Add(animationTitle);
        titleContent.Children.Add(new PreviewIcon { Kind = "Chevron", Width = 12, Height = 12, Margin = new(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = .7 });
        animationDefinitionPicker = new() { Content = titleContent, Height = 22, Padding = new(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Left };
        animationDefinitionPicker.SetResourceReference(StyleProperty, "CommandButton"); animationDefinitionPicker.Width = double.NaN; animationDefinitionPicker.Height = 22;
        animationDefinitionPicker.Click += (_, _) => ShowDefinitionPicker();
        animationCaption = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
        animationCaption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        animationBindingPicker = new() { Content = new PreviewIcon { Kind = "Chevron", Width = 11, Height = 11 }, ToolTip = "Choose the binding Animation mode plays", Margin = new(2, 0, 0, 0) };
        animationBindingPicker.SetResourceReference(StyleProperty, "CommandButton"); animationBindingPicker.Width = 20; animationBindingPicker.Height = 18;
        AutomationProperties.SetName(animationBindingPicker, "Choose the binding");
        animationBindingPicker.Click += (_, _) => ShowBindingPicker();
        StackPanel caption = new() { Orientation = Orientation.Horizontal, Margin = new(4, 0, 0, 0) };
        caption.Children.Add(animationCaption); caption.Children.Add(animationBindingPicker);
        StackPanel title = new() { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(animationDefinitionPicker); title.Children.Add(caption);
        animationBackButton = HistoryButton("ChevronLeft", "Back (Alt+←)", "Back to the previous definition", forward: false);
        animationForwardButton = HistoryButton("ChevronRight", "Forward (Alt+→)", "Forward to the next definition", forward: true);
        animationScope = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 3, 0, 3), Visibility = Visibility.Collapsed };
        animationScope.Children.Add(animationExit); animationScope.Children.Add(title);
        animationScope.Children.Add(new Border { Width = 10 }); animationScope.Children.Add(animationBackButton); animationScope.Children.Add(animationForwardButton);
        // A narrow window's ⋯: the Camera and Display groups.
        animationOverflowItems = new() { Orientation = Orientation.Horizontal, Margin = new(6) };
        Border overflowFlyout = new() { Child = animationOverflowItems, CornerRadius = new(8), BorderThickness = new(1) };
        overflowFlyout.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        overflowFlyout.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        animationCommandOverflow = new() { Content = new PreviewIcon { Kind = "More", Width = 16, Height = 16 }, ToolTip = "Camera and Display", Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        animationCommandOverflow.SetResourceReference(StyleProperty, "CommandButton");
        AutomationProperties.SetName(animationCommandOverflow, "More commands: Camera and Display");
        animationCommandOverflowPopup = new() { Child = overflowFlyout, StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PlacementTarget = animationCommandOverflow, VerticalOffset = 4 };
        animationCommandOverflow.Click += (_, _) => animationCommandOverflowPopup.IsOpen = true;
        SourceCommandItems.Children.Insert(SourceCommandItems.Children.IndexOf(SourcePickers) + 1, animationCommandOverflow);
        AutomationProperties.SetName(animationScope, "Animation mode");
        SourceCommandItems.Children.Insert(SourceCommandItems.Children.IndexOf(SourceScopeHost) + 1, animationScope);

        // The scenario chip at the command bar's right end.
        scenarioChipDot = new() { Width = 7, Height = 7, Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        scenarioChipDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentFillColorDefaultBrush");
        scenarioChipText = new() { VerticalAlignment = VerticalAlignment.Center };
        StackPanel chip = new() { Orientation = Orientation.Horizontal };
        chip.Children.Add(scenarioChipDot); chip.Children.Add(scenarioChipText);
        scenarioChip = new() { Content = chip, Visibility = Visibility.Collapsed };
        scenarioChip.SetResourceReference(StyleProperty, "ScenarioChip");
        scenarioChip.Click += (_, _) => _ = ReportUi(() => { ShowAnimationRightTab("scenario"); return Task.CompletedTask; });
        DockPanel.SetDock(scenarioChip, Dock.Right);
        SourceCommandBar.Children.Insert(0, scenarioChip);

        // The status bar: "Animation mode · name" while in the mode; after Exit the kept scenario, which re-enters on a click.
        animationStatusText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
        animationStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        animationStatusGroup = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        animationStatusGroup.Children.Add(animationStatusText);
        Border divider = new() { Width = 1, Height = 14, Margin = new(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        animationStatusGroup.Children.Add(divider);
        int at = SourceStatusLeft.Children.IndexOf(StatusMission) + 2;
        SourceStatusLeft.Children.Insert(Math.Min(at, SourceStatusLeft.Children.Count), animationStatusGroup);
        System.Windows.Shapes.Ellipse keptDot = new() { Width = 6, Height = 6, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        keptDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentFillColorDefaultBrush");
        animationKeptText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        StackPanel keptContent = new() { Orientation = Orientation.Horizontal }; keptContent.Children.Add(keptDot); keptContent.Children.Add(animationKeptText);
        animationKeptButton = new() { Content = keptContent, Height = 24, Padding = new(8, 0, 8, 0), Margin = new(0, 0, 6, 0), Visibility = Visibility.Collapsed };
        animationKeptButton.SetResourceReference(StyleProperty, "CommandButton"); animationKeptButton.Width = double.NaN; animationKeptButton.Height = 24;
        AutomationProperties.SetName(animationKeptButton, "Return to Animation mode");
        animationKeptButton.Click += (_, _) => ReturnToAnimationMode();
        SourceStatusLeft.Children.Insert(Math.Min(at, SourceStatusLeft.Children.Count), animationKeptButton);

        Button HistoryButton(string icon, string tip, string name, bool forward)
        {
            Button button = new() { Content = new PreviewIcon { Kind = icon, Width = 14, Height = 14 }, ToolTip = tip, IsEnabled = false };
            button.SetResourceReference(StyleProperty, "CommandButton"); button.Width = 28; button.Height = 28;
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => _ = RunUi(() => AnimationHistoryAsync(forward, CancellationToken.None));
            return button;
        }
    }

    private void ReturnToAnimationMode()
    {
        if (animationKept is not { } kept || kept.Document.IsDisposed) return;
        var document = shownDocument ?? kept.Document;
        var source = document.Document as SourceSceneSnapshot;
        int? entry = source?.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == kept.Definition)?.Entries.Contains(kept.Entry) == true ? kept.Entry : null;
        _ = OpenAnimationFromGuiAsync(document, kept.Definition, entry);
    }

    /// <summary>The definition picker: a searchable list of the map's definitions; choosing one switches the opened definition.</summary>
    private void ShowDefinitionPicker()
    {
        if (animationMode is not { } mode || mode.Document.Document is not SourceSceneSnapshot source) return;
        var choices = source.AnimationDefinitions.Definitions.Select(d => new DefinitionChoice(d.Index,
            d.Name, $"{System.IO.Path.GetFileName(d.File)} · {(d.Entries.Count == 0 ? "not bound" : d.Entries.Count == 1 ? "1 binding" : d.Entries.Count.ToString(CultureInfo.InvariantCulture) + " bindings")}")).ToArray();
        TextBox search = new() { MinHeight = 30, Margin = new(0, 0, 0, 6) };
        AutomationProperties.SetName(search, "Find a definition by name, file or root");
        ListBox list = new() { Height = 320, Width = 360, BorderThickness = new(0), Background = Brushes.Transparent };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        AutomationProperties.SetName(list, "Definitions of this map");
        list.ItemTemplate = DefinitionChoiceTemplate();
        void Filter()
        {
            string query = search.Text.Trim();
            list.ItemsSource = query.Length == 0 ? choices : choices.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SelectedItem = list.Items.Cast<DefinitionChoice>().FirstOrDefault(c => c.Definition == mode.Definition);
        }
        search.TextChanged += (_, _) => Filter(); Filter();
        StackPanel body = new() { Margin = new(10) };
        TextBlock caption = new() { Text = "Open a definition", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) };
        body.Children.Add(caption); body.Children.Add(search); body.Children.Add(list);
        Border flyout = new() { Child = body, CornerRadius = new(8), BorderThickness = new(1) };
        flyout.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        flyout.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        animationDefinitionPopup = new() { Child = flyout, StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PlacementTarget = animationDefinitionPicker, VerticalOffset = 4 };
        void Choose()
        {
            if (list.SelectedItem is not DefinitionChoice choice) return;
            animationDefinitionPopup.IsOpen = false;
            if (choice.Definition != mode.Definition) _ = OpenAnimationFromGuiAsync(mode.Document, choice.Definition, null);
        }
        list.MouseLeftButtonUp += (_, _) => Choose();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Choose(); e.Handled = true; } };
        search.KeyDown += (_, e) => { if (e.Key == Key.Down && list.Items.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex); (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as UIElement)?.Focus(); e.Handled = true; } else if (e.Key == Key.Enter) { if (list.SelectedItem == null && list.Items.Count > 0) list.SelectedIndex = 0; Choose(); e.Handled = true; } };
        animationDefinitionPopup.IsOpen = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { search.Focus(); list.ScrollIntoView(list.SelectedItem ?? choices.FirstOrDefault()); });
    }
    private sealed record DefinitionChoice(int Definition, string Name, string Detail);
    private static DataTemplate DefinitionChoiceTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DefinitionChoice.Name)));
        var detail = new FrameworkElementFactory(typeof(TextBlock)); detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DefinitionChoice.Detail)));
        detail.SetValue(TextBlock.FontSizeProperty, 11.5); detail.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.AppendChild(name); panel.AppendChild(detail);
        return new DataTemplate { VisualTree = panel };
    }
    private void ShowBindingPicker()
    {
        if (animationMode is not { } mode || mode.Document.Document is not SourceSceneSnapshot source || source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == mode.Definition) is not { } definition) return;
        ContextMenu menu = new() { PlacementTarget = animationBindingPicker, Placement = PlacementMode.Bottom };
        foreach (int entry in definition.Entries.Take(256))
        {
            MenuItem item = new() { Header = new TextBlock { Text = BindingLabel(entry) }, IsCheckable = true, IsChecked = entry == mode.Entry };
            item.Click += (_, _) => { if (entry != mode.Entry) _ = OpenAnimationFromGuiAsync(mode.Document, mode.Definition, entry); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- the Animation menu

    /// <summary>The title row's Animation menu: in a map, Open in Animation mode / Return to …, or (in the mode) the transport and Exit.</summary>
    private void AnimationMenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != AnimationMenu) return;
        foreach (var dynamic in AnimationMenu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, "map")).ToArray()) AnimationMenu.Items.Remove(dynamic);
        bool map = sourceChromeActive;
        foreach (var item in AnimationMenu.Items.OfType<FrameworkElement>()) item.Visibility = map ? Visibility.Collapsed : Visibility.Visible;
        if (!map) return;
        if (InAnimationMode)
        {
            bool playing = worldAnimationSession?.IsPlaying == true;
            Add(playing ? "Pause" : "Play", "Space", () => WorldAnimationTransportAsync(playing ? "pause" : "play"));
            Add("Stop", null, () => WorldAnimationTransportAsync("stop"));
            Add("Step back one tick", ",", () => WorldAnimationTransportAsync("previous"));
            Add("Step forward one tick", ".", () => WorldAnimationTransportAsync("next"));
            AnimationMenu.Items.Add(new Separator { Tag = "map" });
            Add("Scenario", null, () => { ShowAnimationRightTab("scenario"); return Task.CompletedTask; });
            AnimationMenu.Items.Add(new Separator { Tag = "map" });
            Add("Exit animation mode", "Shift+Esc", () => { _ = ExitAnimationFromGuiAsync(); return Task.CompletedTask; });
            return;
        }
        var selected = SelectedAnimationDefinition();
        var open = Add("Open in Animation mode", "Enter", () =>
        {
            if (SelectedAnimationDefinition() is { } definition && sceneTreeDocument is { } document)
                _ = OpenAnimationFromGuiAsync(document, definition.Definition.Index, SectionSelection?.Section is AnimationBindingEntry binding ? binding.Binding.Entry : null);
            return Task.CompletedTask;
        });
        open.IsEnabled = selected is { BindingKnown: true };
        open.ToolTip = selected == null ? "Select an animation definition in the World tree first" : null;
        ToolTipService.SetShowOnDisabled(open, true);
        if (animationKept is { } kept && kept.Document.SourceWorld == shownDocument?.SourceWorld)
            Add("Return to " + DefinitionName(kept.Definition), null, () => { ReturnToAnimationMode(); return Task.CompletedTask; });

        MenuItem Add(string header, string? gesture, Func<Task> action)
        {
            MenuItem item = new() { Header = new TextBlock { Text = header }, InputGestureText = gesture ?? "", Tag = "map" };
            item.Click += (_, _) => _ = ReportUi(action);
            AnimationMenu.Items.Add(item); return item;
        }
    }

    // ---------------------------------------------------------------- keys

    /// <summary>
    /// Animation mode's keys (§17): Shift+Esc exits (plain Esc never does); Space plays or pauses outside text fields and
    /// focused buttons; , and . step one tick (Shift: one second); Ctrl+1/2/3 choose Structure, Timing, Split; Alt+←/→ go back
    /// and forward through opened definitions.
    /// </summary>
    private bool AnimationModeKey(KeyEventArgs e)
    {
        if (!AnimationModeKey(e.Key == Key.System ? e.SystemKey : e.Key, System.Windows.Input.Keyboard.Modifiers, e.OriginalSource as DependencyObject, e.IsRepeat)) return false;
        e.Handled = true; return true;
    }
    /// <summary>One key of Animation mode, with its modifiers and where it was pressed: whether the mode used it.</summary>
    internal bool AnimationModeKey(Key key, ModifierKeys modifiers, DependencyObject? source, bool repeat)
    {
        if (!InAnimationMode || !sourceChromeActive) return false;
        if (key == Key.Escape && modifiers == ModifierKeys.Shift) { _ = ExitAnimationFromGuiAsync(); return true; }
        if (key is Key.Left or Key.Right && modifiers == ModifierKeys.Alt && !TextInput(source))
        {
            bool forward = key == Key.Right;
            if (forward ? animationForward.Count > 0 : animationBack.Count > 0) _ = RunUi(() => AnimationHistoryAsync(forward, CancellationToken.None));
            return true;
        }
        if (modifiers == ModifierKeys.Control && key is Key.D1 or Key.D2 or Key.D3 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3)
        {
            animationView = key is Key.D1 or Key.NumPad1 ? "structure" : key is Key.D2 or Key.NumPad2 ? "timing" : "split";
            ArrangeWorkspace(); return true;
        }
        if (TextInput(source)) return false;
        if (key == Key.Space && modifiers == ModifierKeys.None && !FocusedControl(source))
        {
            if (!repeat) _ = ReportUi(() => WorldAnimationTransportAsync(worldAnimationSession?.IsPlaying == true ? "pause" : "play"));
            return true;
        }
        if (key is Key.OemComma or Key.OemPeriod && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            bool forward = key == Key.OemPeriod;
            if (modifiers == ModifierKeys.Shift)
            {
                double now = worldAnimationSession?.State.Time ?? 0;
                _ = ReportUi(() => WorldAnimationTransportAsync("seek", Math.Clamp(now + (forward ? 1 : -1), 0, AnimationSeconds.Maximum)));
            }
            else _ = ReportUi(() => WorldAnimationTransportAsync(forward ? "next" : "previous"));
            return true;
        }
        return false;

        static bool TextInput(DependencyObject? at)
        {
            for (var current = at; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                if (current is TextBoxBase or PasswordBox or ComboBox { IsEditable: true }) return true;
            return false;
        }
        static bool FocusedControl(DependencyObject? at)
        {
            for (var current = at; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                // The World tree keeps Space for opening rows, and a focused button or field for itself; the timeline's rows play.
                if (current is ButtonBase or ComboBox or MenuItem or Slider or Thumb or TreeViewItem or DataGridRow) return true;
            return false;
        }
    }
}
