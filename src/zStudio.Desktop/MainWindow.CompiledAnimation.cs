using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>
/// The compiled animation preview (design/animation-redesign.md §19): a compiled root's animation shown with Animation mode's
/// components, read only. The Navigator stays on the left; the command bar carries the title, a Read only tag, the view's
/// labelled commands and the scenario chip; the isolated preview (<see cref="AnimationEditor"/>) is on top with the one
/// transport, Structure, Timing and Split under it; the right pane is Inspector | Scenario. The scenario (inputs, rules,
/// switches, call exclusions over every call) and the presentation are kept per document across asset selections. Nothing
/// here writes stored data.
/// </summary>
public partial class MainWindow
{
    /// <summary>What a compiled document's previews keep across asset selections (never saved: the file is read only).</summary>
    private sealed class CompiledPreviewKept
    {
        public AnimationPreviewOptions Options { get; } = new();
        public AnimationScenario Scenario { get; set; } = AnimationScenario.Isolated(0);
        public double Speed { get; set; } = 1;
        public bool Replay { get; set; }
        public bool Muted { get; set; }
        public double Volume { get; set; } = .5;
        public string? View { get; set; }
        public string RightTab { get; set; } = "inspector";
        public double SplitShare { get; set; } = .58;
        public double? PanelHeight { get; set; }
        public bool Diagnostics { get; set; }
        public bool RunOverlay { get; set; } = true;
        public Dictionary<int, HashSet<string>> Expanded { get; } = [];
        public Dictionary<int, GraphPresentation> Presentation { get; } = [];
        public HashSet<string> TimingToggled { get; } = new(StringComparer.Ordinal);
        public int TimingToggles { get; set; }
        public string? LastIncludeAll { get; set; }
    }
    private readonly ConditionalWeakTable<DocumentModel, CompiledPreviewKept> compiledKept = new();
    private CompiledPreviewKept KeptFor(DocumentModel document) => compiledKept.GetValue(document, _ => new());
    /// <summary>A compiled root's animation is shown (the compiled preview).</summary>
    internal bool CompiledAnimationShown => animation != null && shownDocument is { SourceWorld: null } && !ViewModel.IsSourceProject;
    private CompiledPreviewKept? ShownKept => CompiledAnimationShown ? KeptFor(shownDocument!) : null;
    private AnimationEditor RequireCompiled() => CompiledAnimationShown ? animation! : throw new StudioCommandException("not_ready", "Select an animation of a compiled file first.");

    private AnimationPanel? compiledPanel;
    private AnimationGraphView? compiledGraphView;
    private AnimationTimingView? compiledTimingView;
    private SourceSectionCard? compiledCard;
    private AnimationScenarioPanel? compiledScenarioPanel;
    private DockPanel? compiledBar, compiledPane;
    private ContentControl? compiledCommandsHost;
    private TextBlock? compiledTitle, compiledCaption, compiledChipText, compiledPaneTitle, compiledPaneSubtitle;
    private Button? compiledChip;
    private System.Windows.Shapes.Ellipse? compiledChipDot;
    private Border? compiledTile;
    private ToggleButton? compiledInspectorTab, compiledScenarioTab;
    private ContentControl? compiledCardHost, compiledScenarioHost;
    private ViewportChip? compiledPickBanner;
    private string? compiledPick;

    private AnimationGraphIndex? compiledIndex;
    private AnimationGraph? compiledGraph;
    private AnimationGraphLayout? compiledLayout;
    private string? compiledGraphError;
    private AnimationRunOverlay? compiledOverlay;
    private AnimationFrame? compiledOverlayFrame;
    private AnimationScenario? compiledOverlayScenario;
    private string? compiledSelection, compiledMark;
    private AnimationTiming? compiledTiming;
    private AnimationFrame? compiledTimingFrame;
    private AnimationScenario? compiledTimingScenario;
    private int compiledTimingBuilt = -1;
    private double compiledTimingInterval = 100;
    private readonly System.Diagnostics.Stopwatch compiledGraphClock = System.Diagnostics.Stopwatch.StartNew(), compiledTimingClock = System.Diagnostics.Stopwatch.StartNew();
    private IReadOnlyList<AnimationNotice> compiledNotices = [];

    // ---------------------------------------------------------------- showing

    /// <summary>A new compiled preview for <paramref name="index"/>: it adopts the document's kept scenario and transport.</summary>
    private AnimationEditor CreateCompiledEditor(DocumentModel doc, int index, CancellationToken lifetime)
    {
        var kept = KeptFor(doc);
        var editor = new AnimationEditor(doc, index, ViewModel.Resolver!, lifetime, ViewModel, kept.Options);
        editor.Adopt(kept.Scenario, kept.Speed, kept.Replay, kept.Muted, kept.Volume);
        // What the file keeps is what this animation shows: the inputs and rules carry over, the call exclusions and condition
        // overrides of another animation's call paths do not (they would come back only while nothing changed).
        kept.Scenario = editor.Scenario;
        editor.SetOperationDiagnostics(ViewModel.Problems);
        editor.ScenarioChanged += scenario => { kept.Scenario = scenario; RefreshCompiledScenarioPanel(); RefreshCompiledChrome(); };
        editor.StateChanged += () => { if (animation == editor) RefreshCompiledRun(editor.IsPlaying); };
        return editor;
    }
    private void AttachCompiledAnimation(AnimationEditor editor)
    {
        AttachInspection(editor.Viewport);
        editor.SceneHierarchyChanged += RefreshSceneTree;
        EnsureCompiledChrome();
        var kept = KeptFor(shownDocument!);
        // A new animation of the same file: its own selection; the file's index stays.
        compiledSelection = null; compiledMark = null; compiledGraph = null; compiledLayout = null; compiledOverlay = null; compiledTiming = null; compiledTimingFrame = null;
        if (compiledIndex?.Package != shownDocument!.Document.Animations) compiledIndex = null;
        if (editor.Commands.Parent is Panel owner) owner.Children.Remove(editor.Commands);
        compiledCommandsHost!.Content = editor.Commands;
        compiledPanel!.SetView(kept.View ?? (Workbench.ActualWidth >= 2200 ? "split" : "structure"));
        compiledPanel.SetSplit(kept.SplitShare);
        compiledGraphView!.SetRunOverlay(kept.RunOverlay);
        compiledPanel.TipShown = false;
        RefreshCompiledChrome(); RefreshCompiledScenarioPanel(); RefreshCompiledRun();
        ArrangeWorkspace(); UpdateDocumentCommands();
    }
    private void DetachCompiledAnimation()
    {
        EndCompiledPick();
        if (compiledCommandsHost != null) compiledCommandsHost.Content = null;
        compiledSelection = null; compiledMark = null; compiledGraph = null; compiledLayout = null; compiledOverlay = null; compiledTiming = null; compiledTimingFrame = null;
        compiledGraphView?.SetState(AnimationGraphState.Empty, "none");
        compiledTimingView?.SetState(AnimationTimingState.Empty, "none");
        foreach (var problem in compiledProblems) ViewModel.Problems.Remove(problem);
        compiledProblems.Clear(); compiledProblemsKey = "";
        RefreshCompiledChrome();
        ArrangeWorkspace(); UpdateDocumentCommands();
    }

    private void EnsureCompiledChrome()
    {
        if (compiledBar != null) return;
        // The command bar: the title block (name, Read only, caption), the view's labelled commands, the scenario chip.
        compiledBar = new() { Margin = new(10, 0, 10, 4), MinHeight = 46, LastChildFill = true, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(compiledBar, "Animation commands");
        compiledTitle = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 };
        Border readOnly = new() { CornerRadius = new(9), Padding = new(8, 1, 8, 2), Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "Read only", FontSize = 11 } };
        readOnly.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        readOnly.ToolTip = "A compiled game file: the preview, Structure, Timing and the Inspector read it; nothing here changes it. Edit a source project to change animations.";
        AutomationProperties.SetName(readOnly, "Read only");
        StackPanel nameRow = new() { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(compiledTitle); nameRow.Children.Add(readOnly);
        compiledCaption = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 };
        compiledCaption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel title = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 14, 0) };
        title.Children.Add(nameRow); title.Children.Add(compiledCaption);
        DockPanel.SetDock(title, Dock.Left); compiledBar.Children.Add(title);
        compiledChipDot = new() { Width = 7, Height = 7, Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        compiledChipDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentFillColorDefaultBrush");
        compiledChipText = new() { VerticalAlignment = VerticalAlignment.Center };
        StackPanel chip = new() { Orientation = Orientation.Horizontal }; chip.Children.Add(compiledChipDot); chip.Children.Add(compiledChipText);
        compiledChip = new() { Content = chip };
        compiledChip.SetResourceReference(StyleProperty, "ScenarioChip");
        compiledChip.Click += (_, _) => _ = ReportUi(() => { ShowCompiledTab("scenario"); return Task.CompletedTask; });
        DockPanel.SetDock(compiledChip, Dock.Right); compiledBar.Children.Add(compiledChip);
        compiledCommandsHost = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        compiledBar.Children.Add(compiledCommandsHost);
        // Full width under the title bar, where a source map has its command bar (design §19, the mockup's compiled variant).
        DockPanel.SetDock(compiledBar, Dock.Top);
        var shell = (Panel)SourceCommandBar.Parent;
        shell.Children.Insert(shell.Children.IndexOf(SourceCommandBar) + 1, compiledBar);

        // The right pane: the selection's header, Inspector | Scenario, the card or the Scenario tab.
        compiledPane = new();
        AutomationProperties.SetName(compiledPane, "Inspector pane");
        Border header = new() { Padding = new(12, 12, 8, 4) };
        DockPanel headerRow = new();
        Button close = new() { Content = new PreviewIcon { Kind = "Close", Width = 16, Height = 16 }, ToolTip = "Clear the selection (Esc)" };
        close.SetResourceReference(StyleProperty, "CardCloseButton"); AutomationProperties.SetName(close, "Clear the selection");
        close.Click += (_, _) => _ = ReportUi(() => CompiledSelectAsync(""));
        Button frameButton = new() { Content = new PreviewIcon { Kind = "FrameSelection", Width = 16, Height = 16 }, ToolTip = "Frame the animation's pose in the view" };
        frameButton.SetResourceReference(StyleProperty, "CardCloseButton"); AutomationProperties.SetName(frameButton, "Frame the animation");
        frameButton.Click += (_, _) => animation?.FramePreview();
        DockPanel.SetDock(close, Dock.Right); headerRow.Children.Add(close);
        DockPanel.SetDock(frameButton, Dock.Right); headerRow.Children.Add(frameButton);
        compiledTile = new() { Width = 34, Height = 34, CornerRadius = new(6), BorderThickness = new(1), Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        compiledTile.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        compiledTile.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        DockPanel.SetDock(compiledTile, Dock.Left); headerRow.Children.Add(compiledTile);
        compiledPaneTitle = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        compiledPaneSubtitle = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        compiledPaneSubtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        StackPanel names = new() { VerticalAlignment = VerticalAlignment.Center }; names.Children.Add(compiledPaneTitle); names.Children.Add(compiledPaneSubtitle);
        headerRow.Children.Add(names);
        header.Child = headerRow;
        AutomationProperties.SetName(header, "Selection");
        DockPanel.SetDock(header, Dock.Top); compiledPane.Children.Add(header);
        Border tabs = new() { Padding = new(16, 0, 12, 0), BorderThickness = new(0, 0, 0, 1) };
        tabs.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        StackPanel tabRow = new() { Orientation = Orientation.Horizontal };
        compiledInspectorTab = new() { Content = "Inspector", Tag = "inspector", ToolTip = "The card of the selection" };
        compiledScenarioTab = new() { Content = "Scenario", Tag = "scenario", ToolTip = "The compiled preview's scenario: conditions, inputs, rules, world, bound root and range" };
        foreach (var tab in new[] { compiledInspectorTab, compiledScenarioTab })
        {
            tab.SetResourceReference(StyleProperty, "PaneTab");
            AutomationProperties.SetName(tab, (string)tab.Content);
            tab.Click += (_, _) => _ = ReportUi(() => { ShowCompiledTab((string)tab.Tag); return Task.CompletedTask; });
            tabRow.Children.Add(tab);
        }
        tabs.Child = tabRow;
        DockPanel.SetDock(tabs, Dock.Top); compiledPane.Children.Add(tabs);
        Grid content = new();
        compiledCard = new() { TitleShown = false };
        compiledCard.InspectorLinkInvoked = link => _ = ReportUi(() => CompiledLinkAsync(link));
        compiledCard.InspectorConditionChosen = (item, choice) => _ = ReportUi(() => CompiledConditionsAsync([(item.Path, item.Entry, item.Event, choice)]));
        compiledCard.InspectorIncludeChosen = (item, value) => _ = ReportUi(() => CompiledIncludeAsync(item.Paths, value));
        compiledCard.HeaderChanged += RefreshCompiledPaneHeader;
        compiledCard.CloseRequested = () => _ = ReportUi(() => CompiledSelectAsync(""));
        compiledCardHost = new() { Content = compiledCard, Focusable = false };
        compiledScenarioPanel = new() { BindingChoices = () => [] };
        compiledScenarioPanel.Requested += (action, value) => _ = ReportUi(async () =>
        {
            try { await ApplyCompiledScenarioRequestAsync(action, value); }
            catch { RefreshCompiledScenarioPanel(); throw; }
        });
        compiledScenarioHost = new() { Content = compiledScenarioPanel, Focusable = false, Visibility = Visibility.Collapsed };
        content.Children.Add(compiledCardHost); content.Children.Add(compiledScenarioHost);
        compiledPane.Children.Add(content);
        Border paneFrame = new() { Child = compiledPane };
        paneFrame.SetResourceReference(StyleProperty, "SourcePane");
        CompiledPaneHost.Content = paneFrame;

        // The panel under the view: the one transport, Structure, Timing, Split.
        compiledPanel = new();
        compiledPanel.SetFitRange("Auto range", "The playback range from 0 s to the measured duration");
        compiledPanel.TransportRequested += action => _ = ReportUi(() => CompiledTransportAsync(action));
        compiledPanel.SeekRequested += seconds => _ = ReportUi(() => CompiledTransportAsync("seek", seconds));
        compiledPanel.OptionRequested += (key, value) => _ = ReportUi(() => CompiledOptionAsync(key, value));
        compiledPanel.ViewRequested += view => { if (ShownKept is { } kept) kept.View = view; ArrangeWorkspace(); };
        compiledPanel.SplitChanged += share => { if (ShownKept is { } kept) kept.SplitShare = share; };
        compiledPanel.FitRangeRequested += () => _ = ReportUi(() => CompiledRangeAsync(null));
        compiledPanel.DurationRequested += () => _ = ReportUi(async () => { if (CompiledAnimationShown) { await animation!.MeasureAgainAsync(); RefreshCompiledRun(); RefreshCompiledScenarioPanel(); } });
        compiledPanel.DiagnosticsRequested += () => { if (ShownKept is { } kept) kept.Diagnostics = true; ArrangeWorkspace(); };
        compiledPanel.NoticeRequested += notice => _ = ReportUi(() => CompiledSelectAsync(notice.Row ?? notice.Node ?? ""));
        compiledGraphView = new();
        compiledGraphView.SelectionRequested += id => _ = ReportUi(() => CompiledSelectAsync(id));
        compiledGraphView.ConditionRequested += (row, choice) => _ = ReportUi(() => CompiledConditionAsync(row.Id, choice));
        compiledGraphView.IncludeRequested += (edge, value) => _ = ReportUi(() => CompiledIncludeAsync(edge.Paths, value));
        compiledGraphView.ExpandRequested += (node, value) => _ = ReportUi(() => { CompiledExpand(node, value); return Task.CompletedTask; });
        compiledGraphView.MemberRequested += (frame, entry) => _ = ReportUi(() => { CompiledMember(frame, entry); return Task.CompletedTask; });
        compiledGraphView.OpenRequested += node => _ = ReportUi(() => CompiledOpenReferenceAsync(node));
        compiledGraphView.PresentationRequested += (action, id) => _ = ReportUi(() => { CompiledPresentation(action, id); return Task.CompletedTask; });
        compiledGraphView.SeekRequested += seconds => _ = ReportUi(() => CompiledTransportAsync("seek", seconds));
        compiledGraphView.RerunRequested += () => _ = ReportUi(() => CompiledTransportAsync("seek", animation?.CurrentFrame?.Time ?? 0));
        compiledGraphView.RunOverlayChanged += value => { if (ShownKept is { } kept) kept.RunOverlay = value; };
        compiledGraphView.IncludeAllRequested += choice => _ = ReportUi(() => choice == "none" ? CompiledExcludeAllAsync() : CompiledIncludeAllAsync());
        compiledGraphView.EditTimingRequested += () => _ = ReportUi(() => { ShowCompiledTab("inspector"); compiledCard?.FocusAnimationTiming(); return Task.CompletedTask; });
        compiledTimingView = new();
        compiledTimingView.SeekRequested += (seconds, final) => _ = ReportUi(() => CompiledTransportAsync("seek", seconds));
        compiledTimingView.LaneSelected += lane => _ = ReportUi(() => CompiledSelectLaneAsync(lane.Id));
        compiledTimingView.MarkSelected += (lane, mark) => _ = ReportUi(async () => { await CompiledSelectMarkAsync(lane.Id, mark.Id, seek: true); });
        compiledTimingView.GroupToggled += (group, expanded) => _ = ReportUi(() => { CompiledTimingToggle(group, expanded); return Task.CompletedTask; });
        compiledTimingView.StartMoved += (_, _) => { Notice("The compiled preview plays its one animation from 0 s."); RefreshCompiledTiming(); };
        compiledTimingView.StartRemoved += _ => Notice("The compiled preview plays its one animation from 0 s; select another animation in Assets.");
        compiledTimingView.ThresholdProposed += (_, _) => { Notice("A compiled file is read only: edit start thresholds in a source project."); RefreshCompiledTiming(); };
        compiledTimingView.EditTimingRequested += () => { ShowCompiledTab("inspector"); compiledCard?.FocusAnimationTiming(); };
        compiledTimingView.ScenarioRequested += () => ShowCompiledTab("scenario");
        compiledPanel.SetStructure(compiledGraphView, compiledGraphView.Tools);
        compiledPanel.SetTiming(compiledTimingView, compiledTimingView.Tools);
        CompiledAnimationHost.Content = compiledPanel;
    }

    /// <summary>The command bar's title block, the scenario chip and the pane's tabs.</summary>
    private void RefreshCompiledChrome()
    {
        bool shown = CompiledAnimationShown && !presentationUiHidden;
        if (compiledBar == null) return;
        compiledBar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        PreviewHeading.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        if (!CompiledAnimationShown || animation is not { } editor || shownDocument is not { } doc) return;
        var entry = doc.Document.Animations!.Entries[editor.EntryIndex];
        compiledTitle!.Text = entry.Name; compiledTitle.ToolTip = entry.Name;
        compiledCaption!.Text = $"Animation · {CompiledFileName(doc)} · #{entry.Index} · {entry.SourceLength:N0} bytes";
        compiledCaption.ToolTip = $"{doc.Path} · entry #{entry.Index} · source 0x{entry.SourceOffset:X}";
        var scenario = editor.Scenario;
        string full = "Scenario · " + CompiledScenarioSummary(scenario);
        compiledChipText!.Text = Workbench.ActualWidth < NarrowCommandBar ? "Scenario" : full;
        bool customized = CompiledCustomized();
        compiledChip!.ToolTip = full + (customized ? "\nDiffers from the defaults." : "") + "\nShow the Scenario tab";
        compiledChipDot!.Visibility = customized ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(compiledChip, full);
        RefreshCompiledPane();
    }
    /// <summary>"m4/anim.zbd": the file with its folder, as the mockup's caption names it.</summary>
    private static string CompiledFileName(DocumentModel doc) => FolderAndName(doc.Path);
    private static string FolderAndName(string path)
    {
        string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return folder.Length == 0 ? Path.GetFileName(path) : folder + "/" + Path.GetFileName(path);
    }
    /// <summary>"1 start · all calls · 2 excluded · 1 forced".</summary>
    private static string CompiledScenarioSummary(AnimationScenario scenario) => string.Join(" · ", new[]
    {
        $"{scenario.Starts.Count} start{(scenario.Starts.Count == 1 ? "" : "s")}",
        scenario.AllCalls ? "all calls" + (scenario.ExcludedCalls.Count > 0 ? $" · {scenario.ExcludedCalls.Count} excluded" : "") : scenario.IncludedCalls.Count > 0 ? $"{scenario.IncludedCalls.Count} call{(scenario.IncludedCalls.Count == 1 ? "" : "s")}" : "no calls",
        scenario.Conditions.Count > 0 ? $"{scenario.Conditions.Count} forced" : null,
    }.OfType<string>());

    private void ShowCompiledTab(string tab)
    {
        if (tab is not ("inspector" or "scenario")) throw new StudioCommandException("invalid_argument", "Choose the inspector or scenario tab.");
        var kept = ShownKept ?? throw new StudioCommandException("not_ready", "Select an animation of a compiled file first.");
        kept.RightTab = tab;
        if (!Layout.InspectorVisible) { Layout.InspectorVisible = true; inspectorTemporary = true; }
        RefreshCompiledPane(); ArrangeWorkspace();
    }
    private void RefreshCompiledPane()
    {
        if (compiledPane == null || ShownKept is not { } kept) return;
        bool scenario = kept.RightTab == "scenario";
        compiledInspectorTab!.IsChecked = !scenario; compiledScenarioTab!.IsChecked = scenario;
        compiledCardHost!.Visibility = scenario ? Visibility.Collapsed : Visibility.Visible;
        compiledScenarioHost!.Visibility = scenario ? Visibility.Visible : Visibility.Collapsed;
        RefreshCompiledPaneHeader();
    }
    private void RefreshCompiledPaneHeader()
    {
        if (compiledPaneTitle == null || ShownKept is not { } kept || compiledCard == null) return;
        string title, subtitle, glyph;
        if (kept.RightTab == "scenario")
        {
            title = "Scenario"; glyph = "Scenario";
            subtitle = $"Preview settings of {CompiledFileName(shownDocument!)} · changes re-evaluate to the playhead";
        }
        else { title = compiledCard.HeaderTitle; subtitle = compiledCard.HeaderSubtitle; glyph = compiledCard.HeaderGlyph; }
        compiledPaneTitle.Text = title; compiledPaneTitle.ToolTip = title;
        compiledPaneSubtitle!.Text = subtitle; compiledPaneSubtitle.ToolTip = subtitle;
        FrameworkElement icon;
        if (glyph.StartsWith("graph:", StringComparison.Ordinal))
        {
            var palette = AnimationGraphPalette.For(!OutlinerKinds.LightTheme);
            string name = glyph["graph:".Length..];
            var graphIcon = new AnimationGraphGlyph { Glyph = name, Size = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Documents.TextElement.SetForeground(graphIcon, name switch
            {
                "state" => palette.State, "motion" or "key" => palette.Motion, "camera" => palette.Camera, "sound" => palette.Sound, "effect" => palette.Effect,
                "release" => palette.Release, "stop" or "invalidate" => palette.Stop, _ => palette.T2,
            });
            icon = graphIcon;
        }
        else
        {
            var preview = new PreviewIcon { Kind = glyph, Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            preview.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            icon = preview;
        }
        compiledTile!.Child = icon;
    }

    /// <summary>
    /// The compiled layout (design §19): the Navigator, the view on top with the panel under it (its kept height, 52 % of the
    /// column by default) and Diagnostics under the panel when asked, the right pane at its width.
    /// </summary>
    private void ArrangeCompiledAnimation(bool focus)
    {
        EnsureCompiledChrome();
        var kept = ShownKept!;
        double column = Workbench.ActualHeight;
        double maximum = Math.Max(AnimationPanel.MinimumPanelHeight, column - 160 - 6);
        double panel = Math.Clamp(kept.PanelHeight ?? Math.Round(column * (Workbench.ActualWidth < 1300 ? .56 : .52)), Math.Min(AnimationPanel.MinimumPanelHeight, maximum), maximum);
        double strip = kept.Diagnostics ? DiagnosticsStrip(Layout.ToolsHeight, maximum, ref panel) : 0;
        if (focus) { panel = 0; strip = 0; }
        compiledDiagnosticsShown = strip;
        ToolsRow.Height = new(panel + (strip > 0 ? 6 + strip : 0)); ToolsSplitterRow.Height = new(panel > 0 ? 6 : 0);
        BottomTools.Visibility = ToolsSplitter.Visibility = panel > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompiledPanelRow.Height = new(1, GridUnitType.Star); CompiledPanelRow.MinHeight = panel > 0 ? AnimationPanel.MinimumPanelHeight : 0;
        CompiledPanelGap.Height = new(strip > 0 ? 6 : 0);
        ((RowDefinition)BottomToolsContent.RowDefinitions[2]).Height = new(strip);
        CompiledAnimationHost.Visibility = Visibility.Visible;
        CompiledPanelSplitter.Visibility = strip > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTabs.Visibility = strip > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolsButtons.Visibility = Visibility.Collapsed;
        RelatedTab.Visibility = BytesTab.Visibility = Visibility.Collapsed;
        // The strip is Problems alone; the bottom tools' own tab (Layout.ToolsTab) comes back with the next viewer.
        compiledToolsTabForced = true;
        if (ToolTabs.SelectedItem != ProblemsTab) ToolTabs.SelectedItem = ProblemsTab;
        compiledPanel!.SetView(kept.View ?? (Workbench.ActualWidth >= 2200 ? "split" : "structure"));
        compiledPanel.SetSplit(kept.SplitShare);
    }
    /// <summary>Any other viewer: the bottom tools as the compiled explorer has them.</summary>
    private void ArrangeCompiledTools()
    {
        CompiledPanelRow.Height = new(0); CompiledPanelRow.MinHeight = 0; CompiledPanelGap.Height = new(0);
        ((RowDefinition)BottomToolsContent.RowDefinitions[2]).Height = new(1, GridUnitType.Star);
        CompiledAnimationHost.Visibility = Visibility.Collapsed; CompiledPanelSplitter.Visibility = Visibility.Collapsed;
        ToolTabs.Visibility = Visibility.Visible; ToolsButtons.Visibility = Visibility.Visible;
        RelatedTab.Visibility = BytesTab.Visibility = Visibility.Visible;
        if (compiledToolsTabForced) { compiledToolsTabForced = false; ToolTabs.SelectedIndex = Layout.ToolsTab; }
    }
    private bool compiledToolsTabForced;
    private void CompiledPanelSplitterCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ShownKept is not { } kept) return;
        double strip = ((RowDefinition)BottomToolsContent.RowDefinitions[2]).ActualHeight;
        if (strip > 0) Layout.ToolsHeight = Math.Clamp(strip, 96, 700);
        kept.PanelHeight = Math.Max(AnimationPanel.MinimumPanelHeight, CompiledPanelRow.ActualHeight);
        ArrangeWorkspace();
    }

    // ---------------------------------------------------------------- the run

    /// <summary>
    /// Shows the run on the transport, Structure, Timing and the Inspector. While playing the graph follows at most ten times a
    /// second and Timing as often as its projection allows; at rest every change shows at once.
    /// </summary>
    private void RefreshCompiledRun() => RefreshCompiledRun(false);
    private void RefreshCompiledRun(bool runtimeOnly)
    {
        if (compiledPanel == null || !CompiledAnimationShown || animation is not { } editor) return;
        var frame = editor.CurrentFrame;
        string? failure = editor.PreviewFailure;
        string coverage = editor.Loading ? editor.LoadingMessage
            : frame is { } shown ? $"Evaluated 0–{AnimationSeconds.Short(Math.Round(shown.Time, 1))} · {shown.Trace.Count:N0} occurrence{(shown.Trace.Count == 1 ? "" : "s")}"
                + (shown.TraceDropped > 0 ? $" · {shown.TraceDropped:N0} older discarded" : "") + (shown.PhasesDropped > 0 ? $" · {shown.PhasesDropped:N0} older state changes discarded" : "")
            : "Not evaluated";
        compiledPanel.SetState(new()
        {
            Playing = editor.IsPlaying, CanPlay = editor.CanSeek && failure == null, CanSeek = editor.CanSeek, CanStop = editor.CanSeek, Busy = editor.Loading && failure == null,
            Time = frame?.Time ?? 0, RangeStart = 0, RangeEnd = editor.RangeEnd, Speed = editor.Speed, Volume = editor.Volume, Muted = editor.Muted, Replay = editor.Replay,
            FollowCamera = editor.Options.FollowCamera,
            Status = failure != null ? "Couldn't prepare the preview: " + failure + " Structure and the Inspector still show the stored data." : coverage,
            Failed = failure != null,
            Duration = editor.Duration is { } measured ? $"Duration: {measured.Kind} · {AnimationSeconds.Short(measured.Seconds)} · {measured.Explanation}" : null,
        });
        RefreshCompiledGraph(runtimeOnly);
        RefreshCompiledTiming(runtimeOnly);
        RefreshCompiledProblems(runtimeOnly);
        RefreshCompiledNotices();
        RefreshCompiledInspector(runtimeOnly);
        if (!editor.IsPlaying) RefreshCompiledScenarioLive();
    }

    private const int CompiledStripRows = 500;
    private readonly List<StudioProblem> compiledProblems = [];
    private string compiledProblemsKey = "";
    private readonly System.Diagnostics.Stopwatch compiledProblemsClock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>
    /// The compiled preview's own problems (the run's issues, a failed preparation, audio) in Diagnostics beside the file's
    /// problems, with their severity and where they come from; listed again only when they change (while playing at most
    /// twice a second), gone with the preview.
    /// </summary>
    private void RefreshCompiledProblems(bool runtimeOnly = false)
    {
        if (runtimeOnly && animation?.IsPlaying == true && compiledProblemsClock.ElapsedMilliseconds < 500) return;
        compiledProblemsClock.Restart();
        // Errors and warnings first (and the note of what the run left out after its distinct-note limit), then the rest; past
        // 500 rows the strip says how many more there are and where every one is listed.
        var all = CompiledAnimationShown && animation is { } editor ? editor.CurrentProblems().Where(r => r.FileProblem == null)
            .Select(r => r.Entry < 0 && r.Message == AnimationPlayer.OmittedDiagnostics ? r with { Message = OmittedNotice(r.Count) } : r)
            .OrderBy(r => r.Severity == "Error" ? 0 : r.Severity == "Warning" || r.Entry < 0 && r.Category == "Support" ? 1 : 2).ToArray() : [];
        var rows = all.Take(CompiledStripRows).ToList();
        if (all.Length > CompiledStripRows)
            rows.Add(new("Information", "Preview", "Diagnostics", $"{all.Length - CompiledStripRows:N0} more preview problems are not listed here: MCP animation_runtime problems lists every one.", 1, -1, null, null, "The strip lists the first 500, errors and warnings first."));
        string key = string.Join('\u0001', rows.Select(r => r.Severity + '\u0002' + r.Category + '\u0002' + r.Scope + '\u0002' + r.Message));
        // A new root clears Problems: the listed rows are then gone and go back in.
        if (key == compiledProblemsKey && (compiledProblems.Count == 0 || ViewModel.Problems.Contains(compiledProblems[0]))) return;
        compiledProblemsKey = key;
        foreach (var problem in compiledProblems) ViewModel.Problems.Remove(problem);
        compiledProblems.Clear();
        string? file = shownDocument?.Path;
        foreach (var row in rows)
        {
            var problem = new StudioProblem(row.Severity, row.Category, row.Message, file, row.Entry > 0 ? row.Entry : null) { Source = row.Scope, AnimationPreview = true };
            compiledProblems.Add(problem); ViewModel.Problems.Add(problem);
        }
    }

    /// <summary>The shown animation's graph: one index per file (grouped by name; sequences carry their compiled numbers).</summary>
    private AnimationGraphLayout? CompiledGraphLayout()
    {
        compiledGraphError = null;
        if (!CompiledAnimationShown || animation is not { } editor || shownDocument?.Document.Animations is not { } package) { compiledGraph = null; compiledLayout = null; return null; }
        var kept = ShownKept!;
        if (compiledIndex?.Package != package)
        {
            compiledIndex = AnimationGraphIndex.Create(package, null, new AnimationGraphOptions { CompiledNumbering = true, Map = CompiledFileName(shownDocument) });
            compiledGraph = null;
        }
        int entry = editor.EntryIndex;
        if (entry <= 0 || entry >= package.Entries.Count)
        {
            compiledGraphError = "Entry 0 is the compiler's placeholder: it has nothing to show. Select another animation in Assets.";
            compiledGraph = null; compiledLayout = null; return null;
        }
        var expanded = kept.Expanded.TryGetValue(entry, out var found) ? found : kept.Expanded[entry] = new(StringComparer.Ordinal);
        var presentation = CompiledPresentationOf(entry);
        if (compiledGraph == null || compiledGraph.Index != compiledIndex || compiledGraph.Entry != entry || !compiledGraph.Expanded.SetEquals(expanded)
            || compiledGraph.Members.Count != presentation.Members.Count || presentation.Members.Any(m => !compiledGraph.Members.TryGetValue(m.Key, out int chosen) || chosen != m.Value))
        {
            try { compiledGraph = AnimationGraph.Create(compiledIndex, entry, expanded, members: presentation.Members); compiledLayout = null; compiledOverlay = null; }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { compiledGraph = null; compiledLayout = null; compiledGraphError = ex.Message; return null; }
        }
        var options = presentation.Options;
        if (compiledLayout == null || compiledLayout.Graph != compiledGraph || !SameLayoutOptions(compiledLayout.Options, options))
            compiledLayout = AnimationGraphLayout.Create(compiledGraph, options);
        return compiledLayout;
    }
    private static bool SameLayoutOptions(AnimationGraphLayoutOptions a, AnimationGraphLayoutOptions b) =>
        a.Collapsed.SetEquals(b.Collapsed) && a.Opened.SetEquals(b.Opened) && a.ShowAll.SetEquals(b.ShowAll) && a.ExpandedRows.SetEquals(b.ExpandedRows) && a.PassStrips.SetEquals(b.PassStrips);
    private GraphPresentation CompiledPresentationOf(int entry)
    {
        var kept = ShownKept!;
        return kept.Presentation.TryGetValue(entry, out var found) ? found : kept.Presentation[entry] = new();
    }
    private AnimationGraphLayout RequireCompiledGraph()
    {
        RequireCompiled();
        return CompiledGraphLayout() ?? throw new StudioCommandException("not_ready", compiledGraphError ?? "The Structure graph is unavailable.");
    }

    private void RefreshCompiledGraph(bool runtimeOnly = false)
    {
        if (compiledGraphView == null) return;
        var view = compiledGraphView;
        if (!CompiledAnimationShown || animation is not { } editor) { view.SetState(AnimationGraphState.Empty, "none"); return; }
        if (runtimeOnly && editor.IsPlaying && compiledGraphClock.ElapsedMilliseconds < 100) return;
        compiledGraphClock.Restart();
        var layout = CompiledGraphLayout();
        if (layout == null) { view.SetState(AnimationGraphState.Empty with { Message = compiledGraphError ?? "The Structure graph is unavailable." }, "none"); return; }
        var scenario = editor.Scenario;
        var frame = editor.CurrentFrame;
        if (frame == null) compiledOverlay = null;
        else if (compiledOverlay == null || compiledOverlay.Graph != layout.Graph.Id || !ReferenceEquals(frame, compiledOverlayFrame) || !ReferenceEquals(scenario, compiledOverlayScenario))
        {
            try { compiledOverlay = AnimationRunOverlay.Create(layout.Graph, frame, scenario); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException) { compiledOverlay = null; ViewModel.Status = "Run overlay unavailable: " + ex.Message; }
            compiledOverlayFrame = frame; compiledOverlayScenario = scenario;
        }
        // A question tested more often than its outcome line holds gets a pass strip of its own (kept for the file).
        if (compiledOverlay is { } shownRun)
        {
            bool added = false;
            var presentation = CompiledPresentationOf(layout.Graph.Entry);
            foreach (var condition in shownRun.Conditions)
                if (condition.PassCount > AnimationGraphLayout.OutcomePips && layout.Item(condition.Row) is { } band
                    && !band.Node.Items.Any(i => i.Kind == AnimationGraphItemKind.Passes && i.Row?.Id == condition.Row))
                    added |= presentation.PassStrips.Add(condition.Row);
            if (added && CompiledGraphLayout() is { } widened) layout = widened;
        }
        if (compiledSelection != null && layout.Node(compiledSelection) == null && layout.Item(compiledSelection) == null) compiledSelection = null;
        view.SetState(new()
        {
            Layout = layout, Scenario = scenario, Overlay = compiledOverlay, Stale = editor.PreviewFailure != null && compiledOverlay != null, StaleByEdit = false, Compiled = true,
        }, $"{shownDocument!.SessionId:N}:compiled:{layout.Graph.Entry}");
        if (view.Selection != compiledSelection) view.Select(compiledSelection);
        if (view.RunOverlay != ShownKept!.RunOverlay) view.SetRunOverlay(ShownKept.RunOverlay);
    }

    private void RefreshCompiledTiming(bool runtimeOnly = false)
    {
        if (compiledTimingView == null) return;
        var view = compiledTimingView;
        if (!CompiledAnimationShown || animation is not { } editor || shownDocument?.Document.Animations is not { } package) { compiledTiming = null; view.SetState(AnimationTimingState.Empty, "none"); return; }
        var kept = ShownKept!;
        string key = $"{shownDocument.SessionId:N}:compiled:{editor.EntryIndex}";
        if (editor.EntryIndex <= 0) { compiledTiming = null; view.SetState(AnimationTimingState.Empty with { Message = "Entry 0 is the compiler's placeholder: it has nothing to run." }, key); return; }
        if (runtimeOnly && editor.IsPlaying && compiledTimingClock.ElapsedMilliseconds < compiledTimingInterval) { view.SetTime(editor.CurrentFrame?.Time ?? 0); return; }
        compiledTimingClock.Restart();
        var frame = editor.CurrentFrame;
        var scenario = editor.Scenario;
        CompiledGraphLayout();
        if (compiledTiming == null || !ReferenceEquals(frame, compiledTimingFrame) || !ReferenceEquals(scenario, compiledTimingScenario) || compiledTimingBuilt != kept.TimingToggles)
        {
            var options = new AnimationTimingOptions { Starts = scenario.Starts, Index = compiledIndex, Toggled = new HashSet<string>(kept.TimingToggled, StringComparer.Ordinal), IsolatedStart = true };
            var cost = System.Diagnostics.Stopwatch.StartNew();
            try { compiledTiming = frame == null ? AnimationTiming.Empty(package, options) : AnimationTiming.Create(frame, package, options); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException) { compiledTiming = null; ViewModel.Status = "Timing unavailable: " + ex.Message; }
            compiledTimingFrame = frame; compiledTimingScenario = scenario; compiledTimingBuilt = kept.TimingToggles;
            compiledTimingInterval = Math.Clamp(cost.Elapsed.TotalMilliseconds * 4, 100, 1000);
        }
        view.SetState(new()
        {
            Timing = compiledTiming, Time = frame?.Time ?? 0, RangeStart = 0, RangeEnd = editor.RangeEnd, CanSeek = editor.CanSeek, Playing = editor.IsPlaying,
            Evaluated = frame != null, Stale = false, StaleByEdit = false, CanEditThreshold = _ => false, StartsReadOnly = true,
            Message = editor.PreviewFailure is { } failure ? "Couldn't prepare the preview: " + failure + " Structure and the Inspector still show the stored data." : null,
        }, key);
    }
    private AnimationTiming RequireCompiledTiming()
    {
        RequireCompiled();
        RefreshCompiledTiming();
        return compiledTiming ?? throw new StudioCommandException("not_ready", animation?.EntryIndex <= 0 ? "Entry 0 is the compiler's placeholder: it has nothing to run." : "Timing is unavailable.");
    }
    private AnimationTimingLane RequireCompiledLane(string id) => RequireCompiledTiming().Lane(id) ?? throw new StudioCommandException("stale_record", "Timing has no lane with that identity; read the lanes again.");

    // ---------------------------------------------------------------- operations (GUI and MCP)

    /// <summary>Selects a node, row or edge's row of the graph ("" clears): the Inspector shows its card, Timing scrolls to its lane.</summary>
    private Task CompiledSelectAsync(string id)
    {
        var layout = RequireCompiledGraph();
        if (id.Length > 0 && layout.Node(id) == null && layout.Item(id) == null && layout.Graph.Row(id) == null)
            throw new StudioCommandException("stale_record", "The graph has no node or row with that identity; read it again.");
        compiledSelection = id.Length == 0 ? null : id; compiledMark = null;
        compiledGraphView!.Select(compiledSelection);
        if (id.Length > 0 && ShownKept!.RightTab != "inspector") ShowCompiledTab("inspector");
        if (layout.Graph.Row(id) is { } row) CompiledRevealLane(row.Entry, row.Sequence, row.Event);
        else if (layout.Node(id)?.Node.Sequence is { } sequence) CompiledRevealLane(sequence.Entry, sequence.Id, null);
        else compiledTimingView?.Select(null);
        RefreshCompiledInspector();
        return Task.CompletedTask;
    }
    private void CompiledRevealLane(int entry, Guid sequence, Guid? ev)
    {
        if (compiledTimingView == null || compiledTiming is not { } timing) return;
        var lane = timing.AllLanes.FirstOrDefault(l => l.Kind == AnimationTimingLaneKind.Sequence && l.Sequences.Any(s => s.Entry == entry && s.Sequence == sequence));
        if (lane == null) { compiledTimingView.Select(null); return; }
        bool opened = false;
        var kept = ShownKept!;
        for (string? group = lane.Group; group != null && timing.Lane(group) is { } row; group = row.Group)
            if (!row.Expanded) { if (!kept.TimingToggled.Remove(group)) kept.TimingToggled.Add(group); opened = true; }
        if (opened) { kept.TimingToggles++; RefreshCompiledTiming(); lane = compiledTiming?.AllLanes.FirstOrDefault(l => l.Id == lane.Id); }
        if (lane != null) compiledTimingView.Select(lane.Id, null, ev);
    }
    private async Task CompiledSelectLaneAsync(string id)
    {
        var lane = RequireCompiledLane(id);
        if (lane.Kind == AnimationTimingLaneKind.Starts) { compiledTimingView!.Select(id); ShowCompiledTab("scenario"); return; }
        var layout = CompiledGraphLayout();
        string? graph = null;
        if (lane.Kind == AnimationTimingLaneKind.Group)
        {
            int member = lane.Members.FirstOrDefault();
            graph = layout?.Node(AnimationGraph.FrameId(member)) != null ? AnimationGraph.FrameId(member) : layout?.Nodes.FirstOrDefault(n => n.Node.Reference?.Entries.Contains(member) == true)?.Id;
        }
        else if (lane.Sequences.FirstOrDefault() is { } sequence && layout?.Node(AnimationGraph.SequenceId(sequence.Entry, sequence.Sequence)) != null)
            graph = AnimationGraph.SequenceId(sequence.Entry, sequence.Sequence);
        compiledSelection = graph; compiledMark = null;
        compiledGraphView!.Select(graph);
        compiledTimingView!.Select(id);
        ShowCompiledTab("inspector");
        RefreshCompiledInspector();
        await Task.CompletedTask;
    }
    private async Task<object> CompiledSelectMarkAsync(string laneId, string markId, bool seek)
    {
        var lane = RequireCompiledLane(laneId);
        var mark = lane.Marks.FirstOrDefault(m => m.Id == markId) ?? throw new StudioCommandException("stale_record", "The lane has no mark with that identity; read its marks again.");
        var layout = CompiledGraphLayout();
        string? graph = null;
        if (mark.Event != Guid.Empty) graph = layout?.Graph.RowOf(mark.Entry, mark.Event)?.Id is { } row && layout.Item(row) != null ? row : layout?.Graph.RowOf(mark.Entry, mark.Event)?.Id;
        else if (mark.Entry > 0 && layout?.Node(AnimationGraph.FrameId(mark.Entry)) != null) graph = AnimationGraph.FrameId(mark.Entry);
        compiledSelection = graph; compiledMark = markId;
        compiledGraphView!.Select(graph);
        compiledTimingView!.Select(laneId, markId, mark.Event == Guid.Empty ? null : mark.Event);
        ShowCompiledTab("inspector");
        RefreshCompiledInspector();
        if (seek && animation is { } editor && editor.CanSeek)
        {
            compiledTimingView.RevealTime(mark.Start);
            await editor.TransportAsync("seek", Math.Min(editor.RangeEnd, AnimationSeconds.OccurrenceTime(mark.Start)));
            RefreshCompiledTiming();
            if (compiledTiming?.Lane(laneId)?.Marks.Any(m => m.Id == markId) == true) compiledTimingView.Select(laneId, markId, mark.Event == Guid.Empty ? null : mark.Event);
        }
        return new { lane = laneId, mark = markId, seconds = mark.Start, playhead = AnimationSeconds.OccurrenceTime(mark.Start), actualSeconds = animation?.CurrentFrame?.Time, graph };
    }
    private void CompiledTimingToggle(string groupId, bool expanded)
    {
        var lane = RequireCompiledLane(groupId);
        if (lane.Kind != AnimationTimingLaneKind.Group) throw new StudioCommandException("invalid_argument", "Only an instance group opens or closes.");
        if (lane.Expanded == expanded) return;
        var kept = ShownKept!;
        if (!kept.TimingToggled.Remove(groupId)) kept.TimingToggled.Add(groupId);
        kept.TimingToggles++;
        RefreshCompiledTiming();
    }

    /// <summary>A scenario change: playback stops and the run is evaluated again up to the playhead; every view follows.</summary>
    private async Task CompiledScenarioAsync(AnimationScenario next)
    {
        var editor = RequireCompiled();
        try { await editor.ApplyScenarioAsync(next); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        RefreshCompiledScenarioPanel(); RefreshCompiledChrome(); RefreshCompiledRun();
    }
    private Task CompiledConditionAsync(string rowId, AnimationConditionChoice choice)
    {
        var layout = RequireCompiledGraph();
        var row = layout.Graph.Row(rowId) ?? throw new StudioCommandException("stale_record", "The graph has no row with that identity; read it again.");
        if (row.Type is not (31 or 33)) throw new StudioCommandException("invalid_argument", "Only an IF or ELSEIF row has a Game | True | False switch.");
        string path = layout.Graph.Node(AnimationGraph.FrameId(row.Entry))?.Frame?.Path
            ?? throw new StudioCommandException("not_ready", "This frame is reached only by name, so no call path runs its conditions; open it to force them.");
        return CompiledConditionsAsync([(path, row.Entry, row.Event, choice)]);
    }
    /// <summary>Condition switches (IF and ELSEIF) in call paths of the shown animation, as one change.</summary>
    private Task CompiledConditionsAsync(IReadOnlyList<(string InstancePath, int Entry, Guid Event, AnimationConditionChoice Choice)> switches)
    {
        var editor = RequireCompiled();
        var package = shownDocument!.Document.Animations!;
        var next = editor.Scenario;
        foreach (var (path, entry, ev, choice) in switches)
        {
            if (entry <= 0 || entry >= package.Entries.Count || package.Entries[entry].AllSequences.SelectMany(s => s.Events).FirstOrDefault(e => e.Id == ev) is not { Type: 31 or 33 })
                throw new StudioCommandException("stale_record", "Choose an IF or ELSEIF event of this file (the graph's rows).");
            if (path.Length > 0)
            {
                var reach = next.CallPaths(package);
                if (reach.Paths.FirstOrDefault(p => p.Path == path) is not { } call || call.Target != entry)
                    throw new StudioCommandException("stale_record", "The call path does not reach this animation; read the scenario's call paths again.");
            }
            else if (entry != editor.EntryIndex) throw new StudioCommandException("invalid_argument", "An empty instance path is the shown animation's own; give the call path for a called one.");
            try { next = next.WithCondition(path, entry, ev, choice); }
            catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        }
        return CompiledScenarioAsync(next);
    }
    /// <summary>Includes call paths (with the calls that lead to them) or excludes them (with every path below them).</summary>
    private Task CompiledIncludeAsync(IReadOnlyList<string> paths, bool value)
    {
        var editor = RequireCompiled();
        if (paths.Count == 0) throw new StudioCommandException("not_ready", "This call's frame is reached only by name: its call paths are listed in the Scenario tab.");
        if (paths.Count > AnimationExecutionPolicy.MaximumPaths) throw new StudioCommandException("invalid_argument", "Use a finite list of call paths.");
        var package = shownDocument!.Document.Animations!;
        var known = editor.Scenario.CallPaths(package).Paths.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        var next = editor.Scenario;
        foreach (string path in paths)
        {
            if (!known.Contains(path)) throw new StudioCommandException("stale_record", "The call path is not one the shown animation reaches; read the scenario's call paths again.");
            if (!value || next.AllCalls) { next = next.WithInclusion(path, value, package); continue; }
            for (string at = path; at.Length > 0; at = at[..Math.Max(0, at.LastIndexOf('/'))]) next = next.WithInclusion(at, true);
        }
        ShownKept!.LastIncludeAll = null;
        return CompiledScenarioAsync(next);
    }
    private async Task<AnimationIncludeAllResult> CompiledIncludeAllAsync()
    {
        var editor = RequireCompiled();
        var (next, result) = editor.Scenario.IncludeAll(shownDocument!.Document.Animations!);
        ShownKept!.LastIncludeAll = result.Summary;
        await CompiledScenarioAsync(next);
        return result;
    }
    private Task CompiledExcludeAllAsync()
    {
        var editor = RequireCompiled();
        ShownKept!.LastIncludeAll = null;
        return CompiledScenarioAsync(editor.Scenario.ExcludeAll());
    }
    private void CompiledExpand(string nodeId, bool value, int? member = null)
    {
        var layout = RequireCompiledGraph();
        var reference = layout.Node(nodeId)?.Node.Reference ?? throw new StudioCommandException("stale_record", "The graph has no reference node with that identity; read it again.");
        if (!reference.Expandable || reference.Unresolved) throw new StudioCommandException("invalid_argument", "Only a called animation expands into its frames.");
        if (member is int chosen && !reference.Entries.Contains(chosen)) throw new StudioCommandException("invalid_argument", "Choose one of the called animation's bindings (reference.entries).");
        if (!value && member != null) throw new StudioCommandException("invalid_argument", "A member is chosen when expanding.");
        try { _ = layout.Graph.WithExpanded(reference.Group, value); }
        catch (InvalidOperationException ex) { throw new StudioCommandException("limit_exceeded", ex.Message); }
        var kept = ShownKept!;
        int entry = layout.Graph.Entry;
        var expanded = kept.Expanded.TryGetValue(entry, out var found) ? found : kept.Expanded[entry] = new(StringComparer.Ordinal);
        if (value) expanded.Add(reference.Group); else expanded.Remove(reference.Group);
        if (member is int shown) CompiledPresentationOf(entry).Members[reference.Group] = shown;
        RefreshCompiledGraph();
    }
    private void CompiledMember(string frameId, int member)
    {
        var layout = RequireCompiledGraph();
        var frame = layout.Node(frameId)?.Node.Frame;
        if (frame?.Group == null) throw new StudioCommandException("stale_record", "The graph has no expanded group's frame with that identity; read it again.");
        CompiledExpand(AnimationGraph.ReferenceId(frame.Group), true, member);
    }
    private void CompiledPresentation(string action, string? id)
    {
        var layout = RequireCompiledGraph();
        var presentation = CompiledPresentationOf(layout.Graph.Entry);
        string Sequence() => id is { } given && layout.Node(given)?.Node.Kind == AnimationGraphNodeKind.Sequence ? given
            : throw new StudioCommandException("stale_record", "The graph has no sequence node with that identity; read it again.");
        switch (action)
        {
            case "collapse": { string node = Sequence(); presentation.Collapsed.Add(node); presentation.Opened.Remove(node); break; }
            case "open": { string node = Sequence(); presentation.Collapsed.Remove(node); presentation.Opened.Add(node); break; }
            case "showAll": { string node = Sequence(); presentation.ShowAll.Add(node); presentation.Collapsed.Remove(node); presentation.Opened.Add(node); break; }
            case "expandRow" or "collapseRow":
                {
                    var row = id == null ? null : layout.Graph.Row(id);
                    if (row is not { Members.Count: > 1 }) throw new StudioCommandException("invalid_argument", "Only a grouped row lists its members.");
                    if (action == "expandRow") presentation.ExpandedRows.Add(row.Id); else presentation.ExpandedRows.Remove(row.Id);
                    break;
                }
            case "collapseAll":
                presentation.Opened.Clear();
                foreach (var node in layout.Nodes.Where(n => n.Node.Kind == AnimationGraphNodeKind.Sequence)) presentation.Collapsed.Add(node.Id);
                break;
            case "expandAll":
                presentation.Collapsed.Clear();
                foreach (var node in layout.Nodes.Where(n => n.Node.Kind == AnimationGraphNodeKind.Sequence)) presentation.Opened.Add(node.Id);
                break;
            default: throw new StudioCommandException("invalid_argument", "Unknown presentation change.");
        }
        RefreshCompiledGraph();
    }
    /// <summary>A reference node's Open: that animation is selected in Assets (its preview keeps the file's scenario and presentation).</summary>
    private async Task CompiledOpenReferenceAsync(string nodeId)
    {
        var layout = RequireCompiledGraph();
        var reference = layout.Node(nodeId)?.Node.Reference ?? throw new StudioCommandException("stale_record", "The graph has no reference node with that identity; read it again.");
        if (reference.Entries.Count == 0) throw new StudioCommandException("not_found", "No animation of this file has that name.");
        await CompiledOpenEntryAsync(reference.Entries[0]);
    }
    /// <summary>Alt+Enter or Properties… on an animation: it is shown (selected in Assets) and its Inspector card takes the focus.</summary>
    private async Task FocusCompiledInspectorAsync(DocumentModel doc, AssetRecord asset)
    {
        if (!CompiledAnimationShown || shownDocument != doc || animation?.EntryIndex != asset.Index)
        {
            if (doc.Assets.FirstOrDefault(a => a.Record.Id == asset.Id) is { } item) { doc.SelectedAsset = item; AssetGrid.ScrollIntoView(item); }
            for (int attempt = 0; attempt < 100 && !(CompiledAnimationShown && shownDocument == doc && animation?.EntryIndex == asset.Index); attempt++) await Task.Delay(30);
            if (!CompiledAnimationShown) return;
        }
        ShowCompiledTab("inspector");
        await Dispatcher.InvokeAsync(() => compiledCard?.FocusFirstField(), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private async Task CompiledOpenEntryAsync(int entry)
    {
        var doc = shownDocument ?? throw new StudioCommandException("not_ready", "Open a compiled file first.");
        var asset = doc.Assets.FirstOrDefault(a => a.Record.Kind == AssetKind.Animation && a.Index == entry) ?? throw new StudioCommandException("stale_record", "The animation is not in this file.");
        doc.Query = ""; doc.KindFilter = "All types";
        doc.SelectedAsset = asset; AssetGrid.ScrollIntoView(asset);
        await Task.CompletedTask;
    }

    private async Task CompiledTransportAsync(string action, double seconds = 0)
    {
        var editor = RequireCompiled();
        switch (action)
        {
            case "play" or "pause" when editor.CanSeek || editor.IsPlaying: if (action == "play" != editor.IsPlaying) editor.TogglePlayback(); break;
            case "play" or "pause": editor.TogglePlayback(); break;
            case "stop": editor.Stop(); break;
            case "start": await editor.TransportAsync("start"); break;
            case "previous": editor.Step(-1); break;
            case "next": editor.Step(1); break;
            case "seek": await editor.SeekAsync(Math.Clamp(seconds, 0, editor.RangeEnd)); break;
            default: throw new StudioCommandException("invalid_argument", "Unknown transport action.");
        }
    }
    private async Task CompiledOptionAsync(string key, object value)
    {
        var editor = RequireCompiled();
        var kept = ShownKept!;
        switch (key)
        {
            case "speed": editor.SetTransport(newSpeed: (double)value); kept.Speed = (double)value; break;
            case "replay": editor.SetTransport(newReplay: (bool)value); kept.Replay = (bool)value; break;
            case "mute": editor.SetTransport(newMuted: (bool)value); kept.Muted = (bool)value; break;
            case "volume": editor.SetTransport(newVolume: (double)value); kept.Volume = (double)value; break;
            case "followCamera": editor.Options.FollowCamera = (bool)value; break;
            case "playbackRange":
                {
                    var pair = (double[])value;
                    if (pair[0] != 0) throw new StudioCommandException("invalid_argument", "The compiled preview's range starts at 0 s; set its end.");
                    await CompiledRangeAsync(pair[1]); break;
                }
        }
        RefreshCompiledRun();
    }
    /// <summary>
    /// The playback range's end (null: Auto, the measured duration). A custom range belongs to the shown animation: another
    /// animation of the file opens with Auto (its own duration), so nothing carries over unseen.
    /// </summary>
    private async Task CompiledRangeAsync(double? end)
    {
        var editor = RequireCompiled();
        try { await editor.SetRangeAsync(end); }
        catch (ArgumentOutOfRangeException ex) { throw new StudioCommandException("invalid_argument", ex.Message.Split(" (Parameter", 2)[0]); }
        RefreshCompiledScenarioPanel(); RefreshCompiledRun(); RefreshCompiledChrome();
    }
    /// <summary>Whether the compiled preview differs from its defaults: the scenario, a custom range, a bound-root override or a chosen world.</summary>
    private bool CompiledCustomized() => animation is { } editor && CompiledAnimationShown
        && (editor.Scenario.IsCustomized || editor.CustomRange || editor.RootOverridden || compiledWorldChosen.TryGetValue(shownDocument!, out _));
}
