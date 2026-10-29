using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Automation;
using zStudio.Core;
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
    /// <summary><see cref="animationFailure"/> is a GUI action refused for a pending draft: it goes once no draft remains.</summary>
    private bool animationFailureForDraft;
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
    /// start; for a marked primary of this definition it stands in). Drafts must be resolved first. The camera does not move
    /// and nothing runs.
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
        // Another binding of the primary's own definition (the binding picker) replaces the primary at its time; another
        // definition keeps the previous primary as an ordinary start (design §7, Entering).
        bool keepPrevious = !(WorldScenarios().Current.Primary is int primaryEntry && found.Entries.Contains(primaryEntry));
        if (animationMode != target)
        {
            if (binding > 0)
            {
                // The opened binding is the primary start; a full schedule refuses a 33rd start rather than dropping one.
                var scenarios = WorldScenarios();
                try { _ = scenarios.Current.WithPrimary(binding, keepPrevious); }
                catch (InvalidOperationException ex) { throw new StudioCommandException("limit_exceeded", ex.Message); }
            }
            if (!entering && history && animationMode!.Definition != definition)
            {
                animationBack.Add((animationMode.Definition, animationMode.Entry));
                if (animationBack.Count > AnimationHistory) animationBack.RemoveAt(0);
                animationForward.Clear();
            }
            if (entering && ZoneToolEngaged) LeaveZoneTool();
            // The drop-window analysis and the shown guides belong to the opened definition: opening another one closes them
            // (selecting another definition's items in the graph or Timing, which shows their card, keeps them).
            if (!entering && animationMode!.Definition != definition) { EndDropMove(); CloseParachutes(); HideAllAnimationGuides(); }
            DetachWorldAnimationRun();
            animationMode = target; animationKept = null; animationFailure = null; previewId = Guid.NewGuid();
            animationGraphSelection = null; animationGraphOutline = null; animationInspected = null;
            if (binding > 0)
            {
                var scenarios = WorldScenarios();
                // A binding of the definition whose primary an edit took away (also one made while the map was closed) only
                // stands in for it, at its time, and gives the place back when it matches again.
                if (scenarios.StandsInFor(binding)) scenarios.StandIn(binding, "open", keepPrevious);
                else scenarios.Set(scenarios.Current.WithPrimary(binding, keepPrevious), "open");
            }
            worldAnimationScenarioEpoch = Guid.NewGuid(); worldAnimationDuration = null;
            animationTimingToggled.Clear(); animationTimingToggles++;
            ParachuteScenarioChanged();
            if (entering && animationPanel != null) animationPanel.TipShown = !ViewModel.Settings.AnimationModeTipDismissed;
        }
        RefreshAnimationModeChrome(); RefreshWorldAnimationRun(); RefreshScenarioPanel(); ArrangeWorkspace();
        // The Inspector shows the opened definition's card: its World tree row is selected (the camera stays).
        await EnsureAnimationCardAsync(document, definition, binding > 0 ? binding : null, token);
        if (animationMode == target) { RefreshAnimationModeChrome(); RefreshWorldAnimationRun(); }
    }

    /// <summary>Back to the map layout: the run stops, the scenario is kept, the definition stays selected with its card.</summary>
    private async Task ExitAnimationModeAsync(CancellationToken token)
    {
        if (animationMode is not { } mode) return;
        // A typed start time, seed or height not yet committed is committed while the mode still holds its binding.
        scenarioPanel?.CommitPending();
        if (animationMode != mode) return;
        EndScenarioPick(); EndDropMove();
        // A source map's drop windows and shown guides belong to Animation mode (its Inspector, its Paths chip): they go with it.
        if (mode.Document.SourceWorld != null) { CloseParachutes(); HideAllAnimationGuides(); }
        DetachWorldAnimationRun();
        animationKept = (mode.Document, mode.Definition, mode.Entry);
        animationMode = null; animationFailure = null; previewId = Guid.NewGuid();
        animationGraphSelection = null; animationGraphOutline = null; animationInspected = null;
        animationBack.Clear(); animationForward.Clear();
        animationPanel?.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        RefreshAnimationModeChrome(); RefreshWorldAnimationRun(); ArrangeWorkspace();
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
        // The entry leaves the history before the open awaits, so a second Back pressed meanwhile takes the next one.
        from.RemoveAt(from.Count - 1); to.Add((current.Definition, current.Entry));
        try { await OpenAnimationModeAsync(current.Document, definition, binding, token, history: false); }
        catch when (animationMode == current)
        {
            // Refused before it switched: the history is as it was.
            if (to.Count > 0 && to[^1] == (current.Definition, current.Entry)) to.RemoveAt(to.Count - 1);
            from.Add((definition, entry));
            throw;
        }
        finally { RefreshAnimationModeChrome(); }
    }

    /// <summary>
    /// After the shown map's document or revision changed (a source edit, undo or redo, a rebuild): Animation mode follows its
    /// definition on the rebuilt map, on the scenario's (remapped) primary binding; another map ends it.
    /// </summary>
    private void RebaseAnimationMode()
    {
        RefreshKeptAnimation();
        if (animationMode is not { } mode) { EndScenarioPick(); EndDropMove(); return; }
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
        EndScenarioPick(); EndDropMove();
        DetachWorldAnimationRun();
        // The rebuilt map has new node numbers and graph identities: the graph's and Timing's selection and the 3D outline go.
        animationGraphSelection = null; animationGraphOutline = null; animationInspected = null;
        animationGraphView?.Select(null); animationTimingView?.Select(null);
        animationMode = new(document, document.Revision, mode.Definition, entry); previewId = Guid.NewGuid();
        if (entry > 0 && WorldScenarios().Current.Primary != entry)
            // The opened definition's binding: in place of a primary the edit took away it only stands in (it gives the place back on undo).
            try { WorldScenarios().StandIn(entry); }
            catch (InvalidOperationException) { }
        RefreshAnimationModeChrome(); RefreshWorldAnimationRun(); RefreshScenarioPanel(); ArrangeWorkspace(); ApplySectionPresentation();
    }
    private void EndAnimationModeForMap()
    {
        EndScenarioPick(); EndDropMove();
        if (shownDocument?.SourceWorld != null || parachutes?.World.SourceWorld != null) CloseParachutes();
        HideAllAnimationGuides();
        DetachWorldAnimationRun();
        animationMode = null; animationKept = null; animationFailure = null; previewId = Guid.NewGuid();
        animationBack.Clear(); animationForward.Clear();
        // The graph's presentation belongs to that map's definitions.
        animationGraphExpanded.Clear(); animationGraphPresentation.Clear(); animationGraphSelection = null; animationGraphOutline = null; animationInspected = null;
        RefreshAnimationModeChrome(); RefreshWorldAnimationRun(); ArrangeWorkspace();
    }

    /// <summary>Whether two documents show the same source map (a reload replaces the document of the same map).</summary>
    private static bool SameMap(DocumentModel a, DocumentModel? b) => b != null && (a == b || a.SourceWorld is { } x && b.SourceWorld is { } y
        && string.Equals(x.Root, y.Root, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Mission, y.Mission, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The kept Animation mode follows its map: a reload's new document takes it over; another map, a closed map or leaving
    /// the map's chrome forgets it (the scenario itself stays saved with its map).
    /// </summary>
    private void RefreshKeptAnimation()
    {
        if (animationKept is not { } kept) return;
        var current = ViewModel.SelectedDocument;
        if (current is { IsDisposed: false } && SameMap(kept.Document, current))
        {
            if (shownDocument == current && kept.Document != current)
            {
                animationKept = kept with { Document = current };
                // A scenario at its defaults is not saved, so the reloaded map restores one without a start: the kept binding
                // stays its primary (Return and the kept item then read "1 start", as before the reload).
                var scenarios = WorldScenarios();
                if (scenarios.Current.Primary == null && kept.Entry > 0 && current.Document is SourceSceneSnapshot source
                    && source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == kept.Definition)?.Entries.Contains(kept.Entry) == true)
                    try { scenarios.StandIn(kept.Entry, "restore"); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.IO.InvalidDataException) { }
            }
            return;
        }
        if (current?.SourceWorld != null || current == null && kept.Document.IsDisposed) animationKept = null;
    }
    /// <summary>
    /// The kept Animation mode of the shown map after Exit, re-resolved on the shown document: its binding is the scenario's
    /// (remapped) primary when that is a binding of the kept definition, else the kept binding while it still exists, else -1.
    /// </summary>
    private (DocumentModel Document, int Definition, int Entry)? KeptAnimation()
    {
        RefreshKeptAnimation();
        if (InAnimationMode || animationKept is not { } kept || shownDocument is not { Document: SourceSceneSnapshot source } shown || !SameMap(kept.Document, shown)) return null;
        var definition = source.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == kept.Definition);
        if (definition == null) return null;
        int entry = WorldScenarios().Current.Primary is int primary && definition.Entries.Contains(primary) ? primary
            : definition.Entries.Contains(kept.Entry) ? kept.Entry : -1;
        return (shown, kept.Definition, entry);
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
        // Every animation affecting the object, scrolling past a dozen; never silently capped.
        StackPanel rows = new();
        foreach (var link in links)
        {
            var line = AnimationLinkRow(document, link.Definition, link.Entry, link.Name, link.Reason);
            // Choosing closes the flyout.
            foreach (var choice in ((DockPanel)line).Children.OfType<Button>()) choice.Click += (_, _) => flyout.IsOpen = false;
            rows.Children.Add(line);
        }
        body.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (links.Length == 0) body.Children.Add(new TextBlock { Text = "None of them is a definition of this map.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        int unlisted = uses.Uses.Count - links.Length;
        if (unlisted > 0 || uses.Truncated)
        {
            TextBlock more = new() { Text = uses.Truncated ? $"and more: an object holds at most {MissionAnimationUses.MaximumUses} associations." : $"and {unlisted} more that are not definitions of this map", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            body.Children.Add(more);
        }
        Border frame = new() { Child = body, CornerRadius = new(8), BorderThickness = new(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        AutomationProperties.SetName(frame, title.Text);
        flyout.Child = frame; flyout.IsOpen = true;
    }
    /// <summary>A definition row's Frame bound objects: what it binds to (as its Frame button).</summary>
    private void SceneFrameAnimationClick(object sender, RoutedEventArgs e)
    {
        if (contextScene is { } row && row.Owner == sceneTree && row.Section is DefinitionEntry or AnimationBindingEntry && !TryFrameAnimationRow(row)) ViewModel.Status = "Nothing shown to frame.";
    }
    private bool TryFrameAnimationRow(SceneTreeItem row)
    {
        if (scene is not { } viewport) return false;
        if (row.Section is AnimationBindingEntry binding)
            return scene.Mission?.AnimationEntryRoots.TryGetValue(binding.Binding.Entry, out int root) == true && root >= 0 && viewport.TryFrameNodes([root]);
        return TryFrameSection(row);
    }
    /// <summary>A definition row's Show source: its source excerpt in a flyout beside the row.</summary>
    private void SceneAnimationSourceClick(object sender, RoutedEventArgs e)
    {
        if (contextScene is not { } row || row.Owner != sceneTree || sceneTreeDocument is not { IsDisposed: false, SourceWorld: not null } document) return;
        var definition = row.Section switch { DefinitionEntry d => d.Definition, AnimationBindingEntry b => b.Definition.Definition, _ => null };
        if (definition == null) return;
        _ = ReportUi(async () =>
        {
            var context = await ReadAnimationContextAsync(document, definition.Index, "settings", null, document.Lifetime.Token);
            StackPanel body = new() { Margin = new(12, 10, 12, 10), Width = 520 };
            body.Children.Add(new TextBlock { Text = definition.Name, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 2) });
            TextBlock where = new() { Text = $"{context.Definition.File} · line {context.Definition.FirstLine}", FontSize = 12, Margin = new(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            where.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            body.Children.Add(where);
            TextBox text = new() { Text = context.Definition.Text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new("Cascadia Mono, Consolas"), FontSize = 11.5, MaxHeight = 420,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(text, "Animation source excerpt");
            body.Children.Add(text);
            Border frame = new() { Child = body, CornerRadius = new(8), BorderThickness = new(1) };
            frame.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
            frame.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
            AutomationProperties.SetName(frame, "Source of " + definition.Name);
            Popup flyout = new() { Child = frame, StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Right, PlacementTarget = DocumentSceneTree, IsOpen = true };
            text.Focus();
        });
    }
    /// <summary>A definition row's Properties (Alt+Enter): selects it and focuses its card.</summary>
    private void SceneAnimationPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (contextScene is not { IsSectionItem: true } row || row.Owner != sceneTree) return;
        _ = ReportUi(async () =>
        {
            if (!await ResolveSelectionDraftsAsync(sceneTreeDocument)) return;
            if (sceneTree?.Selected != row) SelectSectionRow(row);
            sectionCard?.FocusFirst();
        });
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
        animationPanel.SetTiming(TimingView, TimingView.Tools);
        animationPanel.SetStructure(GraphView, GraphView.Tools);
    }

    /// <summary>Animation mode's bottom: the panel (its remembered height, 52 % of the column by default; 56 % narrow) and the Diagnostics strip under it.</summary>
    private void ArrangeAnimationPanel()
    {
        InitializeSourceDock();
        double column = Workbench.ActualHeight;
        bool narrow = Workbench.ActualWidth < 1300;
        double maximum = Math.Max(AnimationPanel.MinimumPanelHeight, column - 160 - 6);
        double panel = Math.Clamp(animationPanelHeight ?? Math.Round(column * (narrow ? .56 : .52)), Math.Min(AnimationPanel.MinimumPanelHeight, maximum), maximum);
        double strip = animationDiagnostics ? DiagnosticsStrip(animationDiagnosticsHeight, maximum, ref panel) : 0;
        animationDiagnosticsShown = strip;
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
    /// <summary>
    /// The Diagnostics strip's height under the panel (Animation mode and a compiled preview): the height asked for, clamped to the
    /// room the panel leaves above the view's 160 DIP, and at least 96; the panel keeps its height, and gives way only for those
    /// 96 (never below its minimum).
    /// </summary>
    private static double DiagnosticsStrip(double requested, double maximum, ref double panel)
    {
        double strip = Math.Max(96, Math.Min(Math.Max(96, requested), maximum - 6 - panel));
        if (panel + 6 + strip > maximum) panel = Math.Max(AnimationPanel.MinimumPanelHeight, maximum - 6 - strip);
        return strip;
    }
    /// <summary>The strip's height as last arranged (0 hidden): what animation_mode and workspace_view report.</summary>
    private double animationDiagnosticsShown, compiledDiagnosticsShown;
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
    /// <summary>Structure; Split where the panel is wide enough for both (2,560 DIP windows).</summary>
    private string DefaultAnimationView() => Workbench.ActualWidth >= 2200 ? "split" : "structure";
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
        // An option that changes the run (the playback range) asks about a pending draft first, as the GUI does elsewhere.
        panel.OptionRequested += (key, value) => _ = PlaybackOnlyOption(key) ? ReportUi(() => WorldAnimationOptionAsync(key, value)) : RunUi(() => WorldAnimationOptionAsync(key, value));
        panel.ViewRequested += view => { animationView = view; ArrangeWorkspace(); };
        panel.SplitChanged += share => animationSplitShare = share;
        panel.FitRangeRequested += () => _ = RunUi(FitWorldAnimationScenario);
        panel.DurationRequested += () => _ = ReportUi(() => WorldAnimationTransportAsync("duration"));
        // Dismissed once, the tip stays dismissed for the profile (saved at once, not only at exit).
        panel.TipDismissed += () =>
        {
            ViewModel.Settings.AnimationModeTipDismissed = true;
            try { ViewModel.Settings.Save(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        };
        panel.DiagnosticsRequested += () => { animationDiagnostics = true; RememberAnimationLayout(); ArrangeWorkspace(); };
        panel.NoticeRequested += notice => _ = ReportUi(() => GoToNoticeAsync(notice));
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
        RefreshAnimationPaneHeader();
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
        // The card shows the Inspector in Animation mode and the definition card in the map layout.
        RefreshAnimationInspector();
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
        var keptMode = presentationUiHidden ? null : KeptAnimation();
        animationKeptButton!.Visibility = keptMode != null ? Visibility.Visible : Visibility.Collapsed;
        if (keptMode is { } shownKept)
        {
            animationKeptText!.Text = $"Animation scenario kept · {scenario.Starts.Count} start{(scenario.Starts.Count == 1 ? "" : "s")}";
            animationKeptButton.ToolTip = $"Return to Animation mode on {DefinitionName(shownKept.Definition)}";
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

    /// <summary>Re-enters the kept Animation mode on the shown map's document, on the scenario's primary (remapped by edits) or the kept binding.</summary>
    private void ReturnToAnimationMode()
    {
        if (KeptAnimation() is not { } kept) return;
        _ = OpenAnimationFromGuiAsync(kept.Document, kept.Definition, kept.Entry > 0 ? kept.Entry : null);
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
    private sealed record DefinitionChoice(int Definition, string Name, string Detail)
    {
        /// <summary>What UI Automation reads for the list item.</summary>
        public override string ToString() => $"{Name}, {Detail}";
    }
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

    /// <summary>F6: the 3D view → the transport → the panel's view → the Inspector (Shift+F6 the other way).</summary>
    private void CycleAnimationFocus(bool back)
    {
        if (animationPanel == null) return;
        var focused = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
        int region = focused == null ? -1 : SourcePropertiesPane.IsAncestorOf(focused) ? 3 : animationPanel.FocusRegion == "transport" ? 1 : animationPanel.FocusRegion == "view" ? 2 : SceneHost.IsAncestorOf(focused) || ReferenceEquals(focused, SceneHost.Content) ? 0 : -1;
        // From outside the four, F6 starts at the 3D view and Shift+F6 at the Inspector.
        int start = region >= 0 ? region : back ? 0 : 3;
        for (int step = 1; step <= 4; step++)
        {
            int next = ((start + (back ? -step : step)) % 4 + 4) % 4;
            bool done = next switch
            {
                0 => SceneHost.Content is UIElement view && (view.Focusable && view.Focus() || view.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))),
                1 => animationPanel.FocusTransport(),
                2 => animationPanel.FocusView(),
                _ => FocusAnimationInspector(),
            };
            if (done) return;
        }
    }
    /// <summary>Alt+Enter: the Inspector's first field (its tab shown first).</summary>
    private bool FocusAnimationInspector()
    {
        if (!InAnimationMode) return false;
        if (animationRightTab != "inspector") ShowAnimationRightTab("inspector");
        if (!sourcePropertiesVisible) { sourcePropertiesVisible = true; ArrangeWorkspace(); }
        SourcePropertiesPane.UpdateLayout();
        if (SectionSelection != null && sectionCard is { Entry: not null } card) return card.FocusFirstField();
        return SourcePropertiesHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
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
        if (KeptAnimation() is { } kept)
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
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!AnimationModeKey(key, System.Windows.Input.Keyboard.Modifiers, e.OriginalSource as DependencyObject, e.IsRepeat)
            && !CompiledAnimationKey(key, System.Windows.Input.Keyboard.Modifiers, e.OriginalSource as DependencyObject)) return false;
        e.Handled = true; return true;
    }
    /// <summary>Space released after it went down over the Structure view: it plays or pauses unless it was used to pan.</summary>
    private void AnimationModeKeyUp(KeyEventArgs e)
    {
        if (e.Key != Key.Space || animationGraphView is not { SpaceHeld: true } graph) return;
        bool dragged = graph.SpaceUp();
        e.Handled = true;
        if (!dragged && InAnimationMode && sourceChromeActive) _ = ReportUi(() => WorldAnimationTransportAsync(worldAnimationSession?.IsPlaying == true ? "pause" : "play"));
    }
    /// <summary>One key of Animation mode, with its modifiers and where it was pressed: whether the mode used it.</summary>
    internal bool AnimationModeKey(Key key, ModifierKeys modifiers, DependencyObject? source, bool repeat)
    {
        if (!InAnimationMode || !sourceChromeActive) return false;
        if (key == Key.Escape && modifiers == ModifierKeys.Shift) { _ = ExitAnimationFromGuiAsync(); return true; }
        if (key is Key.Left or Key.Right && modifiers == ModifierKeys.Alt && !IsTextInput(source))
        {
            bool forward = key == Key.Right;
            // A held key walks no further than one step per press.
            if (!repeat && (forward ? animationForward.Count > 0 : animationBack.Count > 0)) _ = RunUi(() => AnimationHistoryAsync(forward, CancellationToken.None));
            return true;
        }
        if (modifiers == ModifierKeys.Control && key is Key.D1 or Key.D2 or Key.D3 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3)
        {
            animationView = key is Key.D1 or Key.NumPad1 ? "structure" : key is Key.D2 or Key.NumPad2 ? "timing" : "split";
            ArrangeWorkspace(); return true;
        }
        // F6 / Shift+F6 move the focus between the 3D view, the transport, the panel's view and the Inspector.
        if (key == Key.F6 && modifiers is ModifierKeys.None or ModifierKeys.Shift) { CycleAnimationFocus(modifiers == ModifierKeys.Shift); return true; }
        // Alt+Enter focuses the Inspector's first field.
        if (key == Key.Enter && modifiers == ModifierKeys.Alt) { FocusAnimationInspector(); return true; }
        if (IsTextInput(source)) return false;
        // Home / End in the 3D view: the playback range's start and end (Timing has its own).
        if (key is Key.Home or Key.End && modifiers == ModifierKeys.None && source is DependencyObject at && SceneHost.IsAncestorOf(at))
        {
            double target = key == Key.Home ? worldAnimationSession?.State.RangeStart ?? worldAnimationRangeStart : worldAnimationSession?.State.RangeEnd ?? worldAnimationRangeEnd;
            _ = ReportUi(() => WorldAnimationTransportAsync("seek", target));
            return true;
        }
        if (key == Key.Space && modifiers == ModifierKeys.None && !IsFocusedControl(source))
        {
            // Over the Structure view Space waits for its release: a drag meanwhile pans, else it plays or pauses then.
            if (animationGraphView is { IsVisible: true, IsMouseOver: true } graph && graph.Layout != null) { if (!repeat) graph.SpaceDown(); return true; }
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
    }
    /// <summary>
    /// The compiled preview's keys, as Animation mode's (§17, §19): , and . step one tick (Shift: one second), Ctrl+1/2/3 choose
    /// Structure, Timing, Split, F6 / Shift+F6 move the focus between the view, the transport, the panel's view and the Inspector,
    /// Home / End in the view go to the range's start and end. Space plays or pauses through the window's animation key.
    /// </summary>
    private bool CompiledAnimationKey(Key key, ModifierKeys modifiers, DependencyObject? source)
    {
        if (!CompiledAnimationShown || animation is not { } editor || compiledPanel == null) return false;
        if (modifiers == ModifierKeys.Control && key is Key.D1 or Key.D2 or Key.D3 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3)
        {
            if (ShownKept is { } kept) kept.View = key is Key.D1 or Key.NumPad1 ? "structure" : key is Key.D2 or Key.NumPad2 ? "timing" : "split";
            ArrangeWorkspace(); return true;
        }
        if (key == Key.F6 && modifiers is ModifierKeys.None or ModifierKeys.Shift) { CycleCompiledFocus(modifiers == ModifierKeys.Shift); return true; }
        if (IsTextInput(source)) return false;
        if (key is Key.Home or Key.End && modifiers == ModifierKeys.None && source is DependencyObject at && editor.ViewportRegion.IsAncestorOf(at))
        {
            _ = ReportUi(() => CompiledTransportAsync("seek", key == Key.Home ? 0 : editor.RangeEnd));
            return true;
        }
        if (key is Key.OemComma or Key.OemPeriod && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            bool forward = key == Key.OemPeriod;
            if (modifiers == ModifierKeys.Shift) _ = ReportUi(() => CompiledTransportAsync("seek", Math.Clamp((editor.CurrentFrame?.Time ?? 0) + (forward ? 1 : -1), 0, editor.RangeEnd)));
            else _ = ReportUi(() => CompiledTransportAsync(forward ? "next" : "previous"));
            return true;
        }
        return false;
    }
    /// <summary>
    /// Esc in the compiled preview, as <see cref="SourceEscape"/> after the focused control (bubbling): once nothing else uses it
    /// (Pick in view, a running operation's cancel, a drag), it clears the selection from anywhere in the preview but text boxes,
    /// lists, grids, trees, menus and flyouts, which keep it. It never leaves the animation.
    /// </summary>
    private bool CompiledEscape(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || System.Windows.Input.Keyboard.Modifiers != ModifierKeys.None || !CompiledAnimationShown || animation == null
            || operation is { IsCancellationRequested: false } || Mouse.Captured != null) return false;
        if (e.OriginalSource is DependencyObject origin && Window.GetWindow(origin) is { } window && window != this) return false;
        for (var up = e.OriginalSource as DependencyObject; up != null; up = up is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(up) : LogicalTreeHelper.GetParent(up))
            if (up is System.Windows.Controls.Primitives.TextBoxBase or ComboBox or MenuItem or Popup or ListBox or DataGrid or TreeView) return false;
        if (compiledSelection == null && compiledMark == null && compiledTimingView?.SelectedLane == null) return false;
        e.Handled = true; _ = ReportUi(() => CompiledSelectAsync(""));
        return true;
    }
    /// <summary>F6 in the compiled preview: the view, the transport, the panel's view and the Inspector, in turn.</summary>
    private void CycleCompiledFocus(bool back)
    {
        if (compiledPanel == null || animation is not { } editor) return;
        var focused = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
        int region = focused == null ? -1 : compiledPane?.IsAncestorOf(focused) == true ? 3 : compiledPanel.FocusRegion == "transport" ? 1 : compiledPanel.FocusRegion == "view" ? 2
            : editor.ViewportRegion.IsAncestorOf(focused) ? 0 : -1;
        int start = region >= 0 ? region : back ? 0 : 3;
        for (int step = 1; step <= 4; step++)
        {
            int next = ((start + (back ? -step : step)) % 4 + 4) % 4;
            bool done = next switch
            {
                0 => editor.Viewport is UIElement view && (view.Focusable && view.Focus() || view.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))),
                1 => compiledPanel.FocusTransport(),
                2 => compiledPanel.FocusView(),
                _ => compiledCard?.FocusFirstField() == true || compiledPane?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)) == true,
            };
            if (done) return;
        }
    }
    private static bool IsTextInput(DependencyObject? at)
    {
        for (var current = at; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is TextBoxBase or PasswordBox or ComboBox { IsEditable: true }) return true;
        return false;
    }
    private static bool IsFocusedControl(DependencyObject? at)
    {
        for (var current = at; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            // The World tree keeps Space for opening rows, and a focused button or field for itself; Structure's and Timing's rows play.
            if (current is ButtonBase or ComboBox or MenuItem or Slider or Thumb or TreeViewItem or DataGridRow) return true;
        return false;
    }
}
