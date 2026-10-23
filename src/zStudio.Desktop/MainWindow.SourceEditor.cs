using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Automation;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// A source map's editor chrome: the grouped command bar, the outliner (left) and docked Properties (right) panes, the status
/// bar and the chips over the view. The existing controls move into it while a map is open and back afterwards, so every
/// command, binding and automation path stays the one the compiled explorer uses.
/// </summary>
public partial class MainWindow
{
    private sealed record MovedChrome(FrameworkElement Element, ItemsControl? Items, Panel? Panel, ContentControl? Host, int Index);
    private readonly List<MovedChrome> movedChrome = [];
    private readonly List<(DependencyObject Target, DependencyProperty Property, object Value)> restyledChrome = [];
    private readonly List<(FrameworkElement Element, EventHandler Handler)> watchedChrome = [];
    private bool sourceChromeActive;
    private Grid? difficultyPrefix;
    private Border? aiExtrasDivider;
    private bool sourceOutlinerVisible = true, sourcePropertiesVisible = true;
    // Diagnostics (the bottom pane) is the project's: shown or hidden, and its height, as the project was last viewed
    // (.zstudio/settings.json); without that, the compiled explorer's Tools preference, which it never changes.
    private bool? sourceToolsVisible; private double? sourceToolsHeight;
    private bool SourceToolsVisible => sourceToolsVisible ?? Layout.ToolsVisible;
    /// <summary>Shows or hides the bottom tools: a source project's Diagnostics pane, or the compiled explorer's Tools (optionally on Problems).</summary>
    private void SetToolsVisible(bool visible, bool problems = false)
    {
        // Only a change is remembered: a report that shows the open pane again writes nothing.
        if (ViewModel.IsSourceProject) { bool changed = SourceToolsVisible != visible; sourceToolsVisible = visible; if (changed) RememberSourceTools(); }
        else { Layout.ToolsVisible = visible; if (problems) ToolTabs.SelectedIndex = 2; }
        ArrangeWorkspace();
    }
    /// <summary>Remembers the project's Diagnostics pane as it now is.</summary>
    private void RememberSourceTools()
    {
        bool shown = SourceToolsVisible; double? height = sourceToolsHeight;
        ViewModel.RememberProjectSetting(root => zStudio.Core.Sources.SourceProjectSettings.WriteDiagnostics(root, shown, height));
    }
    /// <summary>A source project opens with Diagnostics as it was last viewed.</summary>
    private void ApplyProjectView()
    {
        var view = ViewModel.IsSourceProject ? ViewModel.ProjectView : null;
        sourceToolsVisible = view?.Diagnostics;
        sourceToolsHeight = view?.DiagnosticsHeight is double height ? Math.Clamp(height, 100, 700) : null;
    }
    private double sourceOutlinerWidth = 320, sourcePropertiesWidth = 340;
    private SceneInspectionCard? dockedCard;
    private SceneViewport? viewModeViewport;
    private readonly List<SceneInspectionCard.RecentSelection> recentSelections = [];
    private DocumentModel? recentDocument;
    /// <summary>Objects hidden in the view from the outliner's eye: the row's node (or selection owner) and the nodes it hides.</summary>
    private readonly Dictionary<int, int[]> hiddenRows = [];

    private long legendClosedAt = long.MinValue / 2;
    private void InitializeSourceEditor()
    {
        // Only a left press elsewhere closes the legend with a click that could reach the view. IsOpen changes during that
        // press (Closed follows only after the fade), so the button state is read there.
        DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup)).AddValueChanged(OutlinerLegend, (_, _) =>
        { if (!OutlinerLegend.IsOpen && Mouse.LeftButton == MouseButtonState.Pressed) legendClosedAt = Environment.TickCount64; });
        // The empty node's slate follows the theme.
        OutlinerLegend.Opened += (_, _) => LegendEmptyIcon.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, OutlinerKinds.Brush("empty"));
        // A replaced or shown scene brings its own card: dock it (or the next one) once the change settles.
        EventHandler sceneChanged = (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, RefreshSourceChrome);
        DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl)).AddValueChanged(SceneHost, sceneChanged);
        DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement)).AddValueChanged(SceneHost, sceneChanged);
    }
    private bool SourceChromeWanted => ViewModel.IsSourceProject && ViewModel.SelectedDocument?.SourceWorld != null && sourceMapOpening == null;

    /// <summary>Moves the controls into (or out of) the source map editor and shows its current state.</summary>
    private void ApplySourceChrome()
    {
        bool wanted = SourceChromeWanted;
        if (wanted != sourceChromeActive)
        {
            sourceChromeActive = wanted;
            if (wanted) EnterSourceChrome(); else LeaveSourceChrome();
            SourceChromeChangedPreviewFailure(wanted);
        }
        if (scene?.InspectionContent is SceneInspectionCard card)
        {
            card.IsSourceWorld = shownDocument?.SourceWorld != null;
            if (!sourceChromeActive) { card.WorldRebuilding = null; card.CategoryOf = null; card.PickupNameOf = null; card.ZoneLabel = null; card.RecentSelections = null; card.PickRecent = null; card.WorldSummary = null;
                card.ExtraSections = null; card.PlayerTankSummary = null; card.PickPlayerTank = null; }
        }
        RefreshSourceChrome();
    }

    private void EnterSourceChrome()
    {
        // The scope, then file commands, then LOD and difficulty.
        Move(DocumentSave, SourceFileCommands, "CommandButton", "Save (Ctrl+S)", "Save");
        Move(DocumentUndo, SourceFileCommands, "CommandButton", null, "Undo");
        Move(DocumentRedo, SourceFileCommands, "CommandButton", null, "Redo");
        Move(PreviewHeading, SourceScopeHost);
        Restyle(SourceMapsButton, StyleProperty, FindResource("CommandButton"));
        Restyle(PreviewTitle, TextBlock.FontSizeProperty, 13d); Restyle(PreviewSubtitle, TextBlock.FontSizeProperty, 11d);
        Restyle(PreviewHeading, MarginProperty, new Thickness(0));
        Move(LodCombo, SourcePickers);
        Restyle(LodCombo, ToolTipProperty, "Level of detail · LOD 0 is the highest detail of each object");
        // Difficulty reads "Difficulty Hard": a muted prefix inside the picker.
        Move(WorldDifficultyGroup, SourcePickers);
        Restyle(WorldDifficulty, WidthProperty, 174d); Restyle(WorldDifficulty, FontSizeProperty, 13d); Restyle(WorldDifficulty, PaddingProperty, new Thickness(76, 0, 8, 0));
        TextBlock prefix = new() { Text = "Difficulty", FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(13, 0, 0, 0) };
        prefix.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        WorldDifficultyGroup.Children.Remove(WorldDifficulty);
        difficultyPrefix = new(); difficultyPrefix.Children.Add(WorldDifficulty); difficultyPrefix.Children.Add(prefix);
        WorldDifficultyGroup.Children.Add(difficultyPrefix);
        // The viewport's commands, in labelled groups.
        Move(FrameScene, SourceCameraItems, "CommandGroupButton", "Frame all — fit all visible geometry in view");
        Move(FlyEnabled, SourceCameraItems, "CommandToggle", "Flycam — Esc restores normal controls");
        Move(Wireframe, SourceDisplayItems, "CommandToggle", "Wireframe — outline each polygon over the surfaces");
        Move(TexturesEnabled, SourceDisplayItems, "CommandToggle", "Textures");
        Move(BoundsEnabled, SourceDisplayItems, "CommandToggle", "Bounds — object bounding boxes");
        Move(BackdropEnabled, SourceDisplayItems, "CommandToggle", "Horizon");
        Move(WorldHighlights, SourceOverlayItems);
        Restyle(HighlightSoils, "CommandToggle", "Non-default soils — water, lava, sand");
        Restyle(HighlightCanModify, "CommandToggle", "CanModify");
        Restyle(HighlightClipTo, "CommandToggle", "ClipTo");
        Restyle(HighlightZones, "CommandToggle", "Show/hide object zones — the zone that decides whether each object is drawn");
        Restyle(HighlightPolygonZones, "CommandToggle", "Show/hide polygon zones — the zones each polygon makes current when the camera or a vehicle is on it; striped where it has several");
        Restyle(HighlightLandmarks, "CommandToggle", "Landmarks — geometry and objects that are never culled");
        Move(FaceOrientation, SourceOverlayItems, "CommandToggle", "Face orientation — show normal direction");
        // The zone overlays and the AI overlay are the World tree's Zones and AI Networks rows (their eyes): the compiled
        // toolbar keeps its own toggles, out of sight here; Show keeps VTOL route.
        Restyle(HighlightZones, VisibilityProperty, Visibility.Collapsed);
        Restyle(HighlightPolygonZones, VisibilityProperty, Visibility.Collapsed);
        foreach (var moved in new FrameworkElement[] { FrameScene, FlyEnabled, Wireframe, FaceOrientation, TexturesEnabled, BoundsEnabled, BackdropEnabled, WorldHighlights })
            Watch(moved);
        // The title keeps the map's name with an Edited pill; Save, Undo and Redo are in the command bar.
        ActiveDocumentTitle.Visibility = Visibility.Collapsed; SourceDocumentTitle.Visibility = Visibility.Visible;
        PaneButtons.Visibility = Visibility.Collapsed; StatusText.Visibility = Visibility.Collapsed;
        SourceStatusLeft.Visibility = SourceStatusRight.Visibility = Visibility.Visible;
        PresentationStatusBar.MinHeight = 30;
        AiLabel.Margin = new(10, 100, 10, 10);
        if (DocumentSceneTree.Parent is ContentControl tab) tab.Content = null;
        SourceTreeHost.Content = DocumentSceneTree;
        Restyle(DocumentSceneTree, ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        // Diagnostics is the problems page alone: no Related or Bytes, and no tabs.
        Move(ProblemsPage, SourceDiagnosticsHost);
        Restyle(ToolTabs, VisibilityProperty, Visibility.Collapsed); Restyle(SourceDiagnosticsHost, VisibilityProperty, Visibility.Visible);
        // Its notices stay open (no collapse), and the status bar's Diagnostics toggle replaces maximize and hide.
        Restyle(DiagnosticsExpander, StyleProperty, FindResource("FixedExpander")); Restyle(DiagnosticsExpander, Expander.IsExpandedProperty, true);
        Restyle(ToolsButtons, VisibilityProperty, Visibility.Collapsed);
        PreviewHost.SizeChanged += ClipPreviewHost; ClipPreviewHost(null, null);
        RefreshOutlinerFilter();
    }

    private void LeaveSourceChrome()
    {
        OutlinerLegend.IsOpen = false;
        // The World tree's zone and network eyes and a definition's outlines belong to the source map, not to the next world.
        scene?.SetZonePresentation(new HashSet<byte>(), null); scene?.SetAiNetworkPresentation(new HashSet<string>(StringComparer.Ordinal), null); scene?.SetOutlinedNodes([]);
        UndockCard();
        PreviewHost.SizeChanged -= ClipPreviewHost; PreviewHost.Clip = null;
        SourceTreeHost.Content = null; DocumentSceneTab.Content = DocumentSceneTree;
        // The compiled explorer's Document scene shows the whole tree: the outliner's flat list stays behind.
        DocumentSceneTree.Visibility = Visibility.Visible; OutlinerResults.ItemsSource = null; OutlinerResults.Visibility = OutlinerEmpty.Visibility = Visibility.Collapsed;
        outlinerSearchDelay.Stop();
        foreach (var (element, handler) in watchedChrome) DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement)).RemoveValueChanged(element, handler);
        watchedChrome.Clear();
        if (aiExtrasDivider != null) { System.Windows.Data.BindingOperations.ClearAllBindings(aiExtrasDivider); AiTools.Children.Remove(aiExtrasDivider); aiExtrasDivider = null; }
        if (difficultyPrefix != null) { difficultyPrefix.Children.Remove(WorldDifficulty); WorldDifficultyGroup.Children.Remove(difficultyPrefix); WorldDifficultyGroup.Children.Add(WorldDifficulty); difficultyPrefix = null; }
        for (int i = restyledChrome.Count - 1; i >= 0; i--)
        {
            var (target, property, value) = restyledChrome[i];
            if (value == DependencyProperty.UnsetValue) target.ClearValue(property); else target.SetValue(property, value);
        }
        restyledChrome.Clear();
        for (int i = movedChrome.Count - 1; i >= 0; i--)
        {
            var moved = movedChrome[i];
            if (moved.Element.Parent is Panel current) current.Children.Remove(moved.Element);
            else if (moved.Element.Parent is ContentControl host) host.Content = null;
            if (moved.Items != null) moved.Items.Items.Insert(moved.Index, moved.Element);
            else if (moved.Panel != null) moved.Panel.Children.Insert(moved.Index, moved.Element);
            else moved.Host!.Content = moved.Element;
        }
        movedChrome.Clear();
        ActiveDocumentTitle.Visibility = Visibility.Visible; SourceDocumentTitle.Visibility = EditedPill.Visibility = Visibility.Collapsed;
        PaneButtons.Visibility = Visibility.Visible; StatusText.Visibility = Visibility.Visible;
        SourceStatusLeft.Visibility = SourceStatusRight.Visibility = Visibility.Collapsed;
        PresentationStatusBar.MinHeight = 0;
        AiLabel.Margin = new(10);
        if (viewModeViewport != null) { viewModeViewport.ViewModeChanged -= RefreshViewMode; viewModeViewport = null; }
        SourceCommandBar.Visibility = ViewModeChip.Visibility = AiChip.Visibility = FlyBanner.Visibility = FlyFrame.Visibility = Visibility.Collapsed;
        PreviewToolbarHost.ClearValue(VisibilityProperty); PreviewFooter.ClearValue(VisibilityProperty);
        PreviewInner.Margin = new(4);
        SynchronizeFly();
    }

    /// <summary>Moves <paramref name="element"/> into <paramref name="target"/>, remembering where it came from; optionally restyled.</summary>
    private void Move(FrameworkElement element, FrameworkElement target, string? style = null, string? tooltip = null, string? icon = null)
    {
        MovedChrome origin = element.Parent switch
        {
            ToolBar toolbar => new(element, toolbar, null, null, toolbar.Items.IndexOf(element)),
            Panel panel => new(element, null, panel, null, panel.Children.IndexOf(element)),
            ContentControl host => new(element, null, null, host, 0),
            _ => throw new InvalidOperationException("Unexpected chrome parent for " + element.Name),
        };
        if (origin.Items != null) origin.Items.Items.Remove(element);
        else if (origin.Panel != null) origin.Panel.Children.Remove(element);
        else origin.Host!.Content = null;
        movedChrome.Add(origin);
        if (target is Panel panelTarget) panelTarget.Children.Add(element); else ((ContentControl)target).Content = element;
        if (style != null) Restyle(element, StyleProperty, FindResource(style));
        if (tooltip != null) Restyle(element, ToolTipProperty, tooltip);
        if (icon != null) Restyle(element, ContentControl.ContentProperty, new PreviewIcon { Kind = icon });
    }
    private void Restyle(FrameworkElement element, string style, string tooltip)
    { Restyle(element, StyleProperty, FindResource(style)); Restyle(element, ToolTipProperty, tooltip); }
    private void Restyle(DependencyObject target, DependencyProperty property, object value)
    {
        restyledChrome.Add((target, property, target.ReadLocalValue(property)));
        target.SetValue(property, value);
    }
    /// <summary>A group shows while any of its commands does.</summary>
    private void Watch(FrameworkElement element)
    {
        EventHandler handler = (_, _) => RefreshCommandGroups();
        DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement)).AddValueChanged(element, handler);
        watchedChrome.Add((element, handler));
    }
    private void RefreshCommandGroups()
    {
        foreach (var (group, items) in new[] { (SourceCameraGroup, SourceCameraItems), (SourceDisplayGroup, SourceDisplayItems), (SourceOverlayGroup, SourceOverlayItems), (SourceShowGroup, SourceShowItems) })
            group.Visibility = items.Children.Cast<UIElement>().Any(c => c.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ClipPreviewHost(object? sender, SizeChangedEventArgs? e)
    {
        // The view is a rounded frame between the panes (alone, with the UI hidden, it fills the window).
        PreviewHost.Clip = !presentationUiHidden && PreviewHost.ActualWidth > 0 && PreviewHost.ActualHeight > 0 ? new RectangleGeometry(new Rect(0, 0, PreviewHost.ActualWidth, PreviewHost.ActualHeight), 8, 8) : null;
    }

    /// <summary>Shows the chrome's current state: visibility, title, status, overlays and the docked card.</summary>
    private void RefreshSourceChrome()
    {
        if (!sourceChromeActive) return;
        SourceCommandBar.Visibility = presentationUiHidden ? Visibility.Collapsed : Visibility.Visible;
        PreviewToolbarHost.Visibility = PreviewFooter.Visibility = Visibility.Collapsed;
        PreviewInner.Margin = new(0);
        RefreshCommandGroups();
        var doc = ViewModel.SelectedDocument;
        SourceDocumentTitle.Text = doc?.SourceWorld?.Label ?? "";
        EditedPill.Visibility = doc?.IsDirty == true ? Visibility.Visible : Visibility.Collapsed;
        OutlinerPaneToggle.IsChecked = sourceOutlinerVisible; PropertiesPaneToggle.IsChecked = sourcePropertiesVisible;
        // The preference: a window too short for the pane hides it without turning it off.
        ToolsPaneToggle.IsChecked = SourceToolsVisible;
        RefreshSourceStatus(); RefreshViewportChips(); ApplyCardDocking();
    }
    private void RefreshSourceStatus()
    {
        if (!sourceChromeActive) return;
        var card = CurrentInspectionCard;
        StatusSelection.Text = SectionSelection is { } section ? section.Section switch
        {
            NetworkEntry => section.OutlinerLabel + " · AI network", DefinitionEntry => section.OutlinerLabel + " · animation definition",
            ZoneEntry zone => section.OutlinerLabel + (zone.Zone.Label is { } label ? " · " + label : ""), _ => section.OutlinerLabel,
        } : card?.SelectionLabel ?? "No selection";
        StatusMission.Text = scene?.Mission?.Layout.Label ?? "";
        RefreshPlayerTankStatus();
        StatusCounts.Text = scene?.PreviewSummary ?? "";
        StatusNoticeCount.Text = staticPreviewProblems.Count.ToString(CultureInfo.InvariantCulture);
        StatusNotices.Visibility = staticPreviewProblems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshViewportChips()
    {
        bool shown = sourceChromeActive && scene != null && SceneHost.Visibility == Visibility.Visible;
        bool flying = flyCamera?.IsActive == true;
        if (scene != viewModeViewport)
        {
            if (viewModeViewport != null) viewModeViewport.ViewModeChanged -= RefreshViewMode;
            viewModeViewport = scene;
            if (scene != null) scene.ViewModeChanged += RefreshViewMode;
        }
        ViewModeChip.Visibility = shown && !flying ? Visibility.Visible : Visibility.Collapsed;
        RefreshViewMode();
        FlyBanner.Visibility = shown && flying ? Visibility.Visible : Visibility.Collapsed;
        // The Zones group's overlays and tool work in a source map's Whole world.
        SourceZonesGroup.Visibility = shownAsset?.Kind == zStudio.Core.AssetKind.World && shownDocument?.SourceWorld != null ? Visibility.Visible : Visibility.Collapsed;
        // The polygon zone tool frames the view as Flycam does, with its own banner.
        FlyFrame.Visibility = shown && (flying || ZoneToolEngaged) ? Visibility.Visible : Visibility.Collapsed;
        RefreshZoneBanner();
        FlyHint.Visibility = !sourceChromeActive && flying ? Visibility.Visible : Visibility.Collapsed;
        if (flying && scene != null) FlyBannerSpeed.Text = $"· {scene.FlySpeed:0.##} units/s";
        // Status chips for the overlays the World tree's section rows turn on: which zones and networks show.
        bool zones = shown && !flying && ZoneOverlayOn && !ZoneToolEngaged && shownDocument?.SourceWorld != null;
        ZonesChip.Visibility = zones ? Visibility.Visible : Visibility.Collapsed;
        ZonesChipText.Text = (worldHighlightMode == zStudio.Core.WorldHighlightMode.PolygonZones ? "Polygon zones" : "Object zones");
        int usedZones = sceneTree?.Sections?.Zones.Count(z => z.Used) ?? 0;
        int shownUsed = sceneTree?.Sections?.Zones.Count(z => z.Used && shownZones.Contains(z.Zone)) ?? 0;
        ZonesChipMode.Text = shownUsed >= usedZones ? $"· all {usedZones}" : $"· {shownUsed} of {usedZones}";
        AiChip.Visibility = shown && aiVisible && IsAiWorld ? Visibility.Visible : Visibility.Collapsed;
        AiChip.Margin = new(10, zones ? 102 : 56, 10, 10);
        int networks = scene?.AiNetworks.Networks.Count ?? 0, shownCount = scene?.AiNetworks.Networks.Count(n => shownNetworks.Contains(n.Id)) ?? 0;
        AiChipText.Text = sourceChromeActive
            ? "AI networks · " + (aiNetworkFilter != null && scene?.AiNetworks.Networks.FirstOrDefault(n => n.Id == aiNetworkFilter) is { } isolatedNetwork ? SourceMapSections.NetworkStem(isolatedNetwork) + " only"
                : networks == 0 ? "none in this map" : shownCount == networks ? $"all {networks}" : $"{shownCount} of {networks}")
            : "AI nodes · " + ((AiNetworkCombo.SelectedItem as AiNetworkChoice)?.Label.Split(" · ")[0] ?? "All networks");
        AiChipMode.Text = aiThroughGeometry && !sourceChromeActive ? "· see-through" : "";
    }
    private void RefreshViewMode() { if (viewModeViewport != null) ViewModeLabel.Text = viewModeViewport.ViewMode; }

    /// <summary>Hosts the current card's panel in the Properties pane: a source map's card is always docked there (hiding the pane hides it).</summary>
    private void ApplyCardDocking()
    {
        var card = sourceChromeActive && SceneHost.Visibility == Visibility.Visible && scene?.InspectionContent is SceneInspectionCard current ? current : null;
        // While a rebuilt map replaces the shown one, the pane keeps the old card, disabled, until the new card docks.
        SourcePropertiesPane.IsEnabled = card != null || retiringScene == null;
        if (card == null && retiringScene != null && dockedCard != null && sourceChromeActive) return;
        if (card != null)
        {
            card.WorldRebuilding = () => rebuildEvaluating;
            card.CategoryOf = SelectionCategory;
            card.PickupNameOf = PickupNaming;
            card.ZoneLabel = zone => shownDocument?.SourceWorld is { IsDisposed: false } world ? ZoneLabels(world).GetValueOrDefault(zone) : null;
            card.RecentSelections = () => recentDocument == shownDocument ? recentSelections : [];
            card.PickRecent = PickRecentSelection;
            card.WorldSummary = WorldSummary;
            var viewport = scene!;
            card.ExtraSections = selected => PlayerTankSections(viewport, selected) ?? AnimationObjectLinks(viewport, selected);
            card.PlayerTankSummary = PlayerTankRowSummary;
            card.PickPlayerTank = PickPlayerTank;
        }
        if (dockedCard != null && !ReferenceEquals(dockedCard, card)) UndockCard();
        if (card != null && dockedCard == null)
        {
            card.SetDocked(true);
            SourcePropertiesHost.Content = card.PanelRoot;
            dockedCard = card;
        }
        // A selected zone, network or definition shows its own card in the pane; the object card waits beside it.
        object? shown = SectionSelection != null && sectionCard?.Entry != null ? sectionCard : dockedCard?.PanelRoot;
        if (dockedCard != null && !ReferenceEquals(SourcePropertiesHost.Content, shown)) SourcePropertiesHost.Content = shown;
    }
    private void UndockCard()
    {
        if (dockedCard == null) return;
        SourcePropertiesHost.Content = null;
        dockedCard.SetDocked(false);
        dockedCard = null;
    }

    /// <summary>A selected pickup as the World tree names it: "ERFPG #1", "Weapon pickup · pu01700", "ERFPG #1 · weapon pickup".</summary>
    private SceneInspectionCard.PickupNaming? PickupNaming(SceneInspection selected)
    {
        if (!sourceChromeActive || sceneTree?.Projection == null || selected.SelectionNode < 0) return null;
        var row = sceneTree.Selected is { Index: int index } current && index == selected.SelectionNode ? current : sceneTree.Reveal(selected.SelectionNode, expand: false);
        if (row?.PickupName is not { } name || row.Pickup is not { } pickup) return null;
        string game = row.Placement!.Name;
        return new(name, $"{char.ToUpperInvariant(pickup.Kind[0])}{pickup.Kind[1..]} pickup · {game}",
            $"{pickup.ManualName} · {pickup.Type} (catalog {pickup.Index}) · game name {game} (template {pickup.TemplateName})", $"{name} · {pickup.Kind} pickup");
    }
    /// <summary>The outliner type of a selection: from its tree row when the tree shows it, otherwise from its node.</summary>
    private string SelectionCategory(SceneInspection selected)
    {
        if (sceneTree?.Selected is { Index: int index } row && index == selected.SelectionNode) return row.Category;
        var data = scene?.PreviewScene; int node = selected.SelectionNode >= 0 ? selected.SelectionNode : selected.Node;
        if (shownDocument is { SourceWorld: not null } doc && SourceSelection(doc, data) is { } projection && projection.IsBoundary(projection.Owner(node))) return "object";
        if (data == null || node < 0 || node >= data.Nodes.Count) return "node";
        var n = data.Nodes[node];
        return n.Class switch
        {
            "world" => "world", "object3d" => n.ModelIndex != null ? "geometry" : SourceSelection(shownDocument!, data) is { } tree && tree.Children(node).Count == 0 ? "empty" : "group",
            "lod" => "group", _ => "node",
        };
    }
    /// <summary>Remembers a tree selection for the empty pane's Recently selected (four, newest first).</summary>
    private void RememberSelection(SceneTreeItem row)
    {
        if (!row.IsSourceMap || row.Index == null && !row.IsSectionItem) return;
        if (recentDocument != sceneTreeDocument) { recentSelections.Clear(); recentDocument = sceneTreeDocument; }
        string[] path = row.Owner.State.SelectionPath.ToArray();
        recentSelections.RemoveAll(r => r.Path.SequenceEqual(path));
        // A pass row (AI Vehicles) has no node of its own: its label names it.
        string parent = row.Parent?.PickupEntries != null ? string.Join(" › ", Ancestors(row).Reverse()) : row.Parent?.Pass?.Label ?? row.Parent?.DisplayName ?? "";
        recentSelections.Insert(0, new(row.DisplayName, parent, row.Category, path));
        // A pickup is found under Pickups › Weapons (› its type).
        static IEnumerable<string> Ancestors(SceneTreeItem row)
        { for (var at = row.Parent; at != null; at = at.Parent) yield return at.Pass?.Label ?? at.DisplayName; }
        if (recentSelections.Count > 4) recentSelections.RemoveRange(4, recentSelections.Count - 4);
    }
    /// <summary>A recent selection's row, found again by its path of tree identities (never by name), or null.</summary>
    private SceneTreeItem? RecentRow(SceneInspectionCard.RecentSelection entry)
    {
        IReadOnlyList<SceneTreeItem>? level = sceneTree?.Roots; SceneTreeItem? row = null;
        foreach (string key in entry.Path)
        {
            row = level == null ? null : SceneTreeModel.FindRow(level, key);
            if (row == null) return null;
            level = row.Children;
        }
        return row is { Selectable: true } ? row : null;
    }
    private void PickRecentSelection(SceneInspectionCard.RecentSelection entry)
    {
        var row = RecentRow(entry);
        if (row == null)
        {
            recentSelections.Remove(entry); Notice($"{entry.Name} is no longer in this world.");
            CurrentInspectionCard?.Refresh(); return;
        }
        if (!ResolveSelectionDrafts(sceneTreeDocument)) return;
        try { SelectSceneTreeRow(row); }
        catch (StudioCommandException ex) { Report(ex); }
    }
    private IReadOnlyList<(string Label, string Value)> WorldSummary()
    {
        List<(string, string)> rows = [("Scope", PreviewTitle.Text)];
        if (scene?.Mission?.Layout.Label is { } mission) rows.Add(("Mission", mission));
        if (scene?.PreviewSummary is { Length: > 0 } summary)
            foreach (string part in summary.Split(" · "))
            {
                int space = part.IndexOf(' ');
                if (space > 0) rows.Add((char.ToUpperInvariant(part[space + 1]) + part[(space + 2)..], part[..space]));
            }
        if (shownDocument != null && SourceWorldFile(shownDocument) is { } file)
            rows.Add(("Source", System.IO.Path.IsPathRooted(file) ? System.IO.Path.GetRelativePath(ViewModel.RootPath, file).Replace('\\', '/') : file));
        return rows;
    }

    /// <summary>The project file of a source map's world (its mission database glTF, such as data/m1/models/m1.gltf), or null.</summary>
    private static string? SourceWorldFile(DocumentModel doc)
    {
        if (doc.SourceWorld == null || doc.SourceBuild?.AuthoredHierarchy is not { } hierarchy) return null;
        foreach (var root in hierarchy.Roots)
            foreach (int child in root.Children)
                if (hierarchy.Nodes.FirstOrDefault(n => n.Id == child) is { Origin.Database: true } node) return node.File;
        return hierarchy.Nodes.FirstOrDefault(n => n.Origin.Database)?.File;
    }

    private void ArrangeSourcePanes(bool open)
    {
        bool outliner = open && sourceOutlinerVisible, properties = open && sourcePropertiesVisible;
        double width = Workbench.ActualWidth;
        double outlinerWidth = Math.Min(sourceOutlinerWidth, Math.Max(200, width - 380 - (properties ? sourcePropertiesWidth + 6 : 0)));
        double propertiesWidth = Math.Min(sourcePropertiesWidth, Math.Max(260, width - 380 - (outliner ? outlinerWidth + 6 : 0)));
        FilesColumn.Width = new(outliner ? outlinerWidth : 0); NavigatorSplitterColumn.Width = new(outliner ? 6 : 0);
        PropertiesColumn.Width = new(properties ? propertiesWidth : 0); PropertiesSplitterColumn.Width = new(properties ? 6 : 0);
        SourceOutlinerPane.Visibility = outliner ? Visibility.Visible : Visibility.Collapsed;
        SourcePropertiesPane.Visibility = properties ? Visibility.Visible : Visibility.Collapsed;
        // The gaps between the panes resize them (session widths).
        NavigatorSplitter.Visibility = outliner ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = properties ? Visibility.Visible : Visibility.Collapsed;
        InspectorHost.Visibility = Visibility.Collapsed;
    }

    private void SourcePaneClick(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "outliner": sourceOutlinerVisible = !sourceOutlinerVisible; break;
            case "properties": sourcePropertiesVisible = !sourcePropertiesVisible; break;
            case "tools": sourceToolsVisible = !SourceToolsVisible; RememberSourceTools(); break;
        }
        ArrangeWorkspace();
    }

    private void ViewModeClick(object sender, RoutedEventArgs e)
    {
        var active = ActiveNavigationViewport;
        ContextMenu menu = new() { PlacementTarget = ViewModeButton, Placement = PlacementMode.Bottom };
        string projection = active?.CaptureView().Projection ?? "perspective";
        foreach (var (title, action) in new (string, string)[] { ("Perspective", "perspective"), ("Orthographic", "orthographic"), ("", ""),
            ("Front", "front"), ("Back", "back"), ("Right", "right"), ("Left", "left"), ("Top", "top"), ("Bottom", "bottom"), ("Opposite", "opposite"), ("", ""),
            ("Frame all", "frameAll"), ("Frame selected", "frameSelected") })
        {
            if (title.Length == 0) { menu.Items.Add(new Separator()); continue; }
            MenuItem item = new() { Header = title, Tag = action, IsCheckable = action is "perspective" or "orthographic", IsChecked = action == projection };
            item.Click += CameraNavigationClick; menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void FlyExitClick(object sender, RoutedEventArgs e) { FlyEnabled.IsChecked = false; }

    /// <summary>Escape clears a source map's selection once nothing else uses it (a field, a drag, Flycam or a running operation).</summary>
    private bool SourceEscape(KeyEventArgs e)
    {
        // The open icon legend holds the mouse capture: Escape closes it first.
        if (e.Key == Key.Escape && OutlinerLegend.IsOpen) { OutlinerLegend.IsOpen = false; e.Handled = true; return true; }
        if (e.Key != Key.Escape || !sourceChromeActive || operation is { IsCancellationRequested: false } || Mouse.Captured != null) return false;
        var card = CurrentInspectionCard;
        if (card?.Selection == null && SectionSelection == null) return false;
        if (e.OriginalSource is DependencyObject origin && Window.GetWindow(origin) is { } window && window != this) return false;
        for (var source = e.OriginalSource as DependencyObject; source != null; source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            if (source is TextBoxBase or ComboBox or MenuItem or Popup) return false;
        if (scene?.IsPickupDragging == true || scene?.IsZonePainting == true || flyCamera?.IsActive == true) return false;
        e.Handled = true;
        if (SectionSelection != null) ClearSectionSelection(); else card!.ClearSelection();
        return true;
    }

    // The outliner: a search turns the tree into a flat list of matches with their parent paths.
    private sealed record OutlinerHit(SceneTreeItem Row, string Path);
    private const int MaximumOutlinerHits = 1000;
    private readonly System.Windows.Threading.DispatcherTimer outlinerSearchDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private void OutlinerSearchChanged(object sender, TextChangedEventArgs e)
    {
        OutlinerSearchHint.Visibility = OutlinerSearchIcon.Visibility = OutlinerSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        outlinerSearchDelay.Stop();
        if (outlinerSearchDelay.Tag == null) { outlinerSearchDelay.Tag = true; outlinerSearchDelay.Tick += (_, _) => { outlinerSearchDelay.Stop(); if (sourceChromeActive) RefreshOutlinerFilter(); }; }
        outlinerSearchDelay.Start();
    }
    /// <summary>The rows matching a search (every word, case-insensitive) and type filter, in tree order, with their parent paths.</summary>
    internal IReadOnlyList<(SceneTreeItem Row, string Path)> OutlinerMatches(SceneTreeModel model, string query, IReadOnlySet<string> types, int limit, out bool truncated)
    {
        List<(SceneTreeItem, string)> hits = []; truncated = false;
        string[] words = SceneTreeItem.SearchWords(query);
        Stack<(SceneTreeItem Row, string Path)> pending = new(model.Roots.Reverse().Select(r => (r, "")));
        int visited = 0;
        while (pending.TryPop(out var current))
        {
            // Bound the walk itself, not only the result, for a very large world.
            if (++visited > 200_000) { truncated = true; break; }
            var row = current.Row;
            if (row.Category != "world" && types.Contains(row.Category) && row.Matches(words))
            {
                if (hits.Count == limit) { truncated = true; break; }
                hits.Add((row, current.Path));
            }
            string name = row.Pass?.Label ?? row.DisplayName;
            string childPath = current.Path.Length == 0 ? name : current.Path + " › " + name;
            var children = row.Children;
            for (int i = children.Count - 1; i >= 0; i--) pending.Push((children[i], childPath));
        }
        return hits;
    }
    private bool showingOutlinerResults;
    private void RefreshOutlinerFilter()
    {
        string query = OutlinerSearch.Text.Trim();
        OutlinerSearchHint.Visibility = OutlinerSearchIcon.Visibility = OutlinerSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool flat = query.Length > 0;
        DocumentSceneTree.Visibility = flat ? Visibility.Collapsed : Visibility.Visible;
        OutlinerResults.Visibility = flat ? Visibility.Visible : Visibility.Collapsed;
        if (!flat || sceneTree?.Projection == null) { OutlinerResults.ItemsSource = null; OutlinerEmpty.Visibility = Visibility.Collapsed; return; }
        var hits = OutlinerMatches(sceneTree, query, OutlinerKinds.Categories.ToHashSet(), MaximumOutlinerHits, out bool truncated);
        showingOutlinerResults = true;
        try { OutlinerResults.ItemsSource = hits.Where(h => h.Row.Selectable).Select(h => new OutlinerHit(h.Row, h.Path)).ToArray(); OutlinerResults.SelectedItem = null; }
        finally { showingOutlinerResults = false; }
        OutlinerEmpty.Visibility = OutlinerResults.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OutlinerResults.ToolTip = truncated ? $"Showing the first {MaximumOutlinerHits:N0} matches; refine the search." : null;
    }
    private void OutlinerResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (showingOutlinerResults || OutlinerResults.SelectedItem is not OutlinerHit { Row: { Selectable: true } row }) return;
        try
        {
            if (!ResolveSelectionDrafts(sceneTreeDocument) || animation?.ResolvePendingDrafts() == false) return;
            SelectSceneTreeRow(row);
        }
        catch (StudioCommandException ex) { Report(ex); }
        finally
        {
            // The list is a way to choose, not a second selection: clear it so the same match can be chosen again.
            showingOutlinerResults = true;
            try { OutlinerResults.SelectedItem = null; } finally { showingOutlinerResults = false; }
        }
    }
    private void OutlinerCollapseAllClick(object sender, RoutedEventArgs e)
    {
        if (sceneTree == null) return;
        foreach (var row in sceneTree.CreatedRows.ToArray()) row.IsExpanded = false;
    }
    /// <summary>
    /// Show all shows every hidden object (units and pickups too) and ends isolation; zones and AI networks keep their own eyes.
    /// The AI Vehicles and Pickups master eyes follow.
    /// </summary>
    private void ShowAllSections() => ApplySectionPresentation();

    // A row's Frame selection: selects the row (resolving pending input first) and frames it, like the card's button.
    private void OutlinerFrameClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not SceneTreeItem { CanFrame: true } row) return;
        try
        {
            // A Pickups group or type row is never the selection: it frames its pickups on the shown difficulty.
            if (row.PickupEntries != null) { if (!TryFrameSection(row)) ViewModel.Status = "Nothing shown to frame."; return; }
            if (sceneTree?.Selected != row)
            {
                if (!ResolveSelectionDrafts(sceneTreeDocument) || animation?.ResolvePendingDrafts() == false) return;
                SelectSceneTreeRow(row);
            }
            if (row.IsSectionItem) { if (!TryFrameSection(row)) ViewModel.Status = "Nothing shown to frame."; }
            else RunCameraNavigation("frameSelected");
        }
        catch (StudioCommandException ex) { Report(ex); }
    }
    /// <summary>Frames a network (all of its nodes), what a definition binds to or a Pickups group's or type's pickups; false when nothing shown is there.</summary>
    internal bool TryFrameSection(SceneTreeItem row)
    {
        if (scene is not { } viewport) return false;
        return row.Section switch
        {
            NetworkEntry network => viewport.TryFrameAiNetwork(network.Network.Id),
            DefinitionEntry definition => viewport.FrameAnimationPaths(definition.Definition.Index.ToString(CultureInfo.InvariantCulture)) || sceneTree?.Sections?.DefinitionRoots.GetValueOrDefault(definition.Definition.Index) is { } roots && viewport.TryFrameNodes(roots.Take(4096).ToArray()),
            PickupGroupEntry or PickupTypeEntry => row.PickupEntries!.Select(e => e.Row).OfType<int>().Take(4096).ToArray() is { Length: > 0 } pickups && viewport.TryFrameNodes(pickups),
            _ => false,
        };
    }

    // Hide in viewport: presentation only, never a source edit.
    private void OutlinerEyeClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is SceneTreeItem { CanHide: true } section && (section.Section != null || section.HasMasterEye))
        {
            try { ToggleSectionEye(section); }
            catch (StudioCommandException ex) { Report(ex); }
            return;
        }
        if ((sender as FrameworkElement)?.Tag is not SceneTreeItem { CanHide: true, Index: int index } row) return;
        try { SetRowHidden(row, index, !row.IsHidden); }
        catch (StudioCommandException ex) { Report(ex); }
    }
    // Middle-click on a world object's eye: show only this object (what Isolate did), or everything again on the isolated one.
    // Zones, networks, units and pickups are shown and hidden one by one, and their sections' rows show or hide them all.
    private void OutlinerEyeMiddleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not SceneTreeItem { CanHide: true, Index: int index, Section: null, HasMasterEye: false, IsPassObject: false } row) return;
        try { ToggleRowIsolation(row, index); }
        catch (StudioCommandException ex) { Report(ex); }
    }
    internal void ToggleRowIsolation(SceneTreeItem row, int index)
    {
        if (scene == null || row.Owner != sceneTree) throw new StudioCommandException("stale_record", "The tree node is no longer available.");
        if (scene.IsPickupDragging || scene.IsFlyActive) throw new StudioCommandException("busy", "Finish the transform drag or exit Fly first.");
        int node = scene.PickupAt(index)?.Root ?? index;
        if (isolatedNode == node) isolatedNode = null;
        else
        {
            if (CurrentInspectionCard?.HasDraft == true) throw new StudioCommandException("pending_drafts", "Apply or discard the edit first.");
            isolatedNode = node;
        }
        ApplyIsolation(); ReselectShownSelection(); CurrentInspectionCard?.Refresh(); RefreshRowEyes();
    }
    /// <summary>
    /// An object selected in the tree while hidden (by its eye or another object's isolation) shows in its card as inactive;
    /// once shown it is selected in the view again, which makes its placement editable. Only with no draft open anywhere, so
    /// nothing asks (an AI path or drop-window draft retries this when it ends), and only for the shown map's object.
    /// </summary>
    /// <returns>Whether it was selected in the view (the card is then refreshed).</returns>
    private bool ReselectShownSelection()
    {
        if (scene is not { } view || shownDocument is not { } doc || CurrentInspectionCard is not { HasDraft: false, ObjectApplying: false } card) return false;
        if (HasInspectionDraft || aiPathDraft != null || parachuteDraft != null || HasSectionDraft() || sectionCard?.Applying == true) return false;
        if (card.Selection is not { Active: false } selected || selected.SelectionNode < 0
            || !selected.Target.StartsWith($"source:{doc.SessionId}:", StringComparison.Ordinal)) return false;
        // Without a shown mesh of it the view selects nothing and nothing changes (a light, a group, an object still hidden).
        if (!view.SelectInspectionNode(selected.SelectionNode)) return false;
        card.Refresh(); return true;
    }
    /// <summary>
    /// Shows only the isolated object, or everything again: a source group, which owns no node of its own, brings the
    /// objects the tree shows below it, the same ones its eye hides.
    /// </summary>
    private void ApplyIsolation(SceneViewport? viewport = null)
    {
        viewport ??= scene;
        if (viewport == null) return;
        int[]? members = isolatedNode is int node && node < 0 && viewport == scene && sceneTree?.Reveal(node, expand: false) is { Synthetic: true } ? HiddenNodesFor(sceneTree, node) : null;
        isolatedGroup = members != null;
        viewport.Isolate(isolatedNode, members);
    }
    /// <summary>Whether the isolation is a source group's: its members come from the tree, so a replaced scene ends it.</summary>
    private bool isolatedGroup;
    /// <summary>The rows' eyes follow what is hidden and isolated.</summary>
    private void RefreshRowEyes()
    {
        if (sceneTree == null) return;
        if (sourceChromeActive) ReconcileMasters();
        foreach (var row in sceneTree.CreatedRows) row.NotifyHidden();
    }
    internal void SetRowHidden(SceneTreeItem row, int index, bool hidden)
    {
        if (scene == null || row.Owner != sceneTree) throw new StudioCommandException("stale_record", "The tree node is no longer available.");
        if (scene.IsPickupDragging || scene.IsFlyActive) throw new StudioCommandException("busy", "Finish the transform drag or exit Fly first.");
        if (!hidden && !hiddenRows.ContainsKey(index) && row.IsHidden)
            throw new StudioCommandException("hidden_with_group", "It is hidden with a group above it: show that group.");
        if (hidden) hiddenRows[index] = HiddenNodesFor(sceneTree!, index);
        else hiddenRows.Remove(index);
        pendingHidden = null;
        ApplyHiddenRows();
    }
    /// <summary>A hidden row's object and everything the tree shows below it (a group the build removes has no node of its own).</summary>
    private static int[] HiddenNodesFor(SceneTreeModel model, int index)
    {
        List<int> nodes = [index]; HashSet<int> seen = [index];
        Stack<int> pending = new([index]);
        while (pending.TryPop(out int node) && nodes.Count < 100_000)
            foreach (var edge in model.Children(node))
                if (seen.Add(edge.Node)) { nodes.Add(edge.Node); pending.Push(edge.Node); }
        return nodes.ToArray();
    }
    /// <summary>
    /// What was hidden before a source rebuild replaced the map's document: each hidden row's path of file and name (with the
    /// occurrence among equally named siblings; an edit changes node indices and content identities, not these), or, for a row
    /// that cannot be found, its source identity. A renamed or deleted object (or group) shows again.
    /// </summary>
    private (string Path, string[] Layout, string[] Keys)? pendingHidden;
    private void CaptureHiddenIdentities()
    {
        if (hiddenRows.Count == 0 || shownDocument?.SourceWorld == null || sceneTree?.Projection is not { } projection) return;
        Dictionary<SceneTreeItem, string> paths = [];
        // Each hidden row is found (created if needed, nothing expanded): a row the tree never showed is kept too.
        var rows = hiddenRows.Keys.Select(k => sceneTree.Reveal(k, expand: false)).OfType<SceneTreeItem>()
            .Where(r => r.Index is int index && r.Problem == null && hiddenRows.ContainsKey(index)).Distinct().ToArray();
        string[] layout = [.. rows.Select(r => HiddenPath(r, paths)).Distinct()];
        HashSet<int> placed = [.. rows.Select(r => r.Index!.Value)];
        string[] keys = [.. hiddenRows.Keys.Where(k => !placed.Contains(k)).Select(k => projection.Identities.GetValueOrDefault(projection.Owner(k))).OfType<string>().Distinct()];
        pendingHidden = layout.Length == 0 && keys.Length == 0 ? null : (shownDocument.Path, layout, keys);
    }
    /// <summary>Hides again, in the rebuilt map, the rows found at the same layout paths and the objects whose identity matches exactly one row; the rest show.</summary>
    private void RestoreHiddenIdentities(DocumentModel? doc)
    {
        if (pendingHidden is not { } pending) return;
        if (doc == null) { pendingHidden = null; return; }
        if (!string.Equals(pending.Path, doc.Path, StringComparison.OrdinalIgnoreCase)) { pendingHidden = null; return; }
        if (sceneTree?.Projection is not { } projection || !sceneTree.IsPreview) return;
        pendingHidden = null;
        // Walk only toward the remembered paths; children are created as needed, nothing is expanded.
        HashSet<string> wanted = [.. pending.Layout];
        // Every path that leads to a wanted one: a segment ends in "#n" before its "/" (a "/" inside a file name never does).
        HashSet<string> leading = [];
        foreach (string path in wanted)
            for (int end = path.IndexOf('/'); end > 0; end = path.IndexOf('/', end + 1))
                if (char.IsAsciiDigit(path[end - 1])) leading.Add(path[..end]);
        Stack<(IReadOnlyList<SceneTreeItem> Rows, string Parent)> levels = new([(sceneTree.Roots, "")]);
        while (wanted.Count > 0 && levels.TryPop(out var level))
        {
            Dictionary<string, int> occurrences = [];
            foreach (var row in level.Rows)
            {
                string key = HiddenKey(row); int occurrence = occurrences.GetValueOrDefault(key); occurrences[key] = occurrence + 1;
                string path = (level.Parent.Length == 0 ? "" : level.Parent + "/") + key + "#" + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (wanted.Remove(path) && row.Index is int index && row.Problem == null) hiddenRows[index] = HiddenNodesFor(sceneTree, index);
                if (leading.Contains(path)) levels.Push((row.Children, path));
            }
        }
        foreach (string key in pending.Keys)
        {
            var matches = projection.Identities.Where(p => p.Value == key).Take(2).Select(p => p.Key).ToArray();
            if (matches.Length == 1) hiddenRows[matches[0]] = HiddenNodesFor(sceneTree, matches[0]);
        }
        ApplyHiddenRows();
    }
    /// <summary>Hides again what was hidden before a rebuild, by the same identities the selection follows; what has no match shows.</summary>
    private void RemapHidden(KeyValuePair<int, int[]>[] previous, Func<int, int?> remap)
    {
        hiddenRows.Clear(); pendingHidden = null;
        foreach (var (key, nodes) in previous)
            if (remap(key) is int mapped) hiddenRows[mapped] = nodes.Select(remap).OfType<int>().Distinct().ToArray();
        ApplyHiddenRows();
    }
    private void ApplyHiddenRows()
    {
        scene?.SetHiddenNodes(hiddenRows.Values.SelectMany(n => n));
        // The card follows at once: a unit shown again is selected, or its reason changes (an open draft, the eye).
        if (!ReselectShownSelection()) CurrentInspectionCard?.Refresh();
        if (sceneTree == null) return;
        HashSet<int> hidden = hiddenRows.Values.SelectMany(n => n).ToHashSet();
        sceneTree.IsHiddenNode = hidden.Contains;
        RefreshRowEyes();
    }
}
