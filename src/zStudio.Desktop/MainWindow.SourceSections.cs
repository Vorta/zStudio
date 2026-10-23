using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// The World tree's sections beyond the scene: Zones (the zone overlay, each zone's colour and its label), AI Networks (the
/// AI overlay, each network's markers and its header settings) and Animations (definitions, events and paths).
/// Their eyes are view only and last for the session; their cards edit the sources through the shared edit path.
/// </summary>
public partial class MainWindow : ISourceSectionState
{
    private readonly ConditionalWeakTable<GameScene, SourceMapSections> mapSections = new();
    /// <summary>The zone overlay shown when the Zones eye turns on: polygon zones until another kind is chosen.</summary>
    private WorldHighlightMode zoneOverlayKind = WorldHighlightMode.PolygonZones;
    /// <summary>The zones whose colour the view shows, each on its own (all hidden at first); any shown turns the zone overlay on.</summary>
    private readonly HashSet<byte> shownZones = [];
    /// <summary>The AI networks the view shows, each on its own (all hidden at first); any shown turns the AI overlay on.</summary>
    private readonly HashSet<string> shownNetworks = new(StringComparer.Ordinal);
    /// <summary>
    /// The master eyes of Zones, AI Networks, AI Vehicles and Pickups: what pressing them does next. One changes when pressed,
    /// or when everything below it comes to be shown (or hidden) one by one; a mix leaves it as it was.
    /// </summary>
    private readonly Dictionary<string, bool> masterShown = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, bool> InitialMasters = new Dictionary<string, bool>
        { ["zones"] = false, ["aiNetworks"] = false, ["aiv"] = true, ["puppies"] = true };
    /// <summary>The map (its path) the zone and network eyes belong to: another map starts with every zone and network hidden.</summary>
    private string? sectionStateMap;
    private SourceSectionCard? sectionCard;
    private bool applyingSectionPresentation, sectionPresentationAgain;
    /// <summary>Group by type: the Pickups section lists a row per catalog type inside Weapons, Ammo and Other (for the session).</summary>
    private bool pickupsByType;

    // ---------------------------------------------------------------- what the rows show

    bool ISourceSectionState.ZoneOverlayOn => ZoneOverlayOn;
    private bool ZoneOverlayOn => worldHighlightMode is WorldHighlightMode.Zones or WorldHighlightMode.PolygonZones;
    bool ISourceSectionState.ZoneOverlayPolygons => (ZoneOverlayOn ? worldHighlightMode : zoneOverlayKind) == WorldHighlightMode.PolygonZones;
    bool ISourceSectionState.ZoneToolEngaged => ZoneToolEngaged;
    byte? ISourceSectionState.PaintZone => ZoneToolEngaged ? zoneTool.Zone : null;
    bool ISourceSectionState.ZoneShown(byte zone) => shownZones.Contains(zone);
    bool ISourceSectionState.AnyNetworkShown => shownNetworks.Count > 0;
    bool ISourceSectionState.NetworkShown(string network) => shownNetworks.Contains(network);
    bool ISourceSectionState.AnimationShown(int definition) => shownAnimationPaths.Contains(definition);
    bool ISourceSectionState.MasterShown(string section) => MasterShown(section);
    private bool MasterShown(string section) => masterShown.TryGetValue(section, out bool shown) ? shown : InitialMasters.TryGetValue(section, out bool initial) ? initial : section.StartsWith("puppies:", StringComparison.Ordinal);

    private SceneTreeItem? SectionSelection => sceneTree?.Selected is { IsSectionItem: true } row ? row : null;

    // ---------------------------------------------------------------- reading the sections

    /// <summary>The shown map's zones, AI networks and definitions, read once per shown scene.</summary>
    private SourceMapSections? MapSections(DocumentModel doc, GameScene data, MissionSceneContext? mission, SourceMapSelection projection)
    {
        if (mapSections.TryGetValue(data, out var known)) return known;
        if (doc.Document is not SourceSceneSnapshot snapshot || doc.SourceBuild is not { } build || doc.SourceWorld is not { } session) return null;
        long[] polygons = new long[256]; int[] placements = new int[256]; HashSet<string>?[] models = new HashSet<string>[256]; long none = 0;
        foreach (var (node, slot) in zStudio.Core.Worlds.GameZWriter.NodeSlots(snapshot.World))
        {
            if (node.Model is not { } model) continue;
            var origin = build.Provenance.GetValueOrDefault(slot);
            var mapping = origin?.ZonePolygons;
            HashSet<int> seen = []; HashSet<byte> touched = [];
            // Each placement counts its source polygons once (a build can split one into several).
            for (int i = 0; i < model.Polygons.Count; i++)
            {
                if (mapping != null && (i >= mapping.Count || !seen.Add(mapping[i]))) continue;
                if (SourceZoneEdits.PolygonZoneIds(model.Polygons[i].Zone) is not { } ids) continue;
                if (ids.Length == 0) { none++; continue; }
                foreach (byte zone in ids) { polygons[zone]++; touched.Add(zone); }
            }
            foreach (byte zone in touched) { placements[zone]++; (models[zone] ??= new(StringComparer.OrdinalIgnoreCase)).Add(origin?.ModelFile ?? "slot:" + slot.ToString(CultureInfo.InvariantCulture)); }
        }
        // The objects, geometry and empty nodes whose zone it is, as the outliner shows them (the world and the objects startup
        // animations place).
        int[] own = new int[256], inherited = new int[256], empties = new int[256];
        Stack<int> pending = new(projection.Roots.Concat(projection.Passes.Where(p => p.Kind == "anim").SelectMany(p => p.Roots)));
        HashSet<int> visited = [];
        while (pending.TryPop(out int row) && visited.Count < 200_000)
        {
            if (!visited.Add(row)) continue;
            foreach (var child in projection.Children(row)) pending.Push(child.Node);
            if (row < 0 || row >= data.Nodes.Count || projection.IsRuntimeCopy(row)) continue;
            var node = data.Nodes[row];
            bool empty = !projection.IsBoundary(row) && node.Class == "object3d" && node.ModelIndex == null && projection.Children(row).Count == 0;
            if (!projection.IsBoundary(row) && !(node.Class == "object3d" && node.ModelIndex != null) && !empty) continue;
            if (node.Metadata["zone_id"] is not JsonValue value || !(value.TryGetValue<uint>(out uint word) || value.TryGetValue<int>(out int signed) && (word = unchecked((uint)signed)) == word)) continue;
            int source = mission != null && row < mission.SourceNodes.Count ? mission.SourceNodes[row] : row;
            bool isInherited = build.Provenance.TryGetValue(source, out var origin) && origin.ImportedZoneInherited == true;
            if (isInherited) inherited[word & 0xFF]++; else own[word & 0xFF]++;
            if (empty) empties[word & 0xFF]++;
        }
        var labels = ZoneLabels(session);
        List<SourceZoneRow> zones = [];
        foreach (int zone in Enumerable.Range(0, 255).Append(255))
            if (polygons[zone] > 0 || own[zone] + inherited[zone] > 0 || zone != 255 && labels.ContainsKey((byte)zone))
                zones.Add(new((byte)zone, zone == 255 ? null : labels.GetValueOrDefault((byte)zone), polygons[zone], placements[zone], models[zone]?.Count ?? 0, own[zone], inherited[zone], empties[zone]));
        // The objects each definition binds to: the nodes its entries bind to, as tree rows.
        var entryRoots = mission?.AnimationEntryRoots ?? new Dictionary<int, int>();
        Dictionary<int, int[]> definitionRoots = [];
        foreach (var definition in snapshot.AnimationDefinitions.Definitions)
        {
            if (definition.Entries.Count == 0) continue;
            int[] rows = [.. definition.Entries.Select(e => entryRoots.TryGetValue(e, out int bound) ? projection.Owner(bound) : (int?)null).OfType<int>().Distinct()];
            if (rows.Length > 0) definitionRoots[definition.Index] = rows;
        }
        SourceMapSections sections = new()
        {
            Mission = session.Mission, Zones = zones, NoZonePolygons = none, Networks = mission?.AiNetworks.Networks ?? [],
            Definitions = snapshot.AnimationDefinitions, DefinitionRoots = definitionRoots,
        };
        mapSections.AddOrUpdate(data, sections);
        return sections;
    }

    // ---------------------------------------------------------------- presentation

    /// <summary>
    /// A map's zone and network eyes are its own: another map starts with every zone and network hidden. Whatever changes them
    /// calls this first, so a change is never taken back by the reset of a map it was made in.
    /// </summary>
    private void ResetSectionStateFor(DocumentModel? doc)
    {
        string? map = doc?.SourceWorld != null ? doc.Path : null;
        if (string.Equals(map, sectionStateMap, StringComparison.OrdinalIgnoreCase)) return;
        sectionStateMap = map; shownZones.Clear(); shownNetworks.Clear(); masterShown.Clear();
    }

    /// <summary>
    /// The zone overlay is on while a zone shows (in the kind the Zones row's switch chose) and off otherwise; another overlay
    /// chosen meanwhile (Soils, CanModify…) hid every zone (see <see cref="ZoneOverlayChosen"/>).
    /// </summary>
    private void SyncZoneOverlay()
    {
        if (shownZones.Count > 0 && !ZoneOverlayOn) SetWorldHighlightMode(zoneOverlayKind);
        else if (shownZones.Count == 0 && ZoneOverlayOn) SetWorldHighlightMode(WorldHighlightMode.None);
    }
    /// <summary>
    /// A source map's overlay was chosen (the Overlay group, MCP scene_options): a zone overlay with no zone shown shows every
    /// zone, as the overlay always did; any other overlay replaces the zones', so every zone hides.
    /// </summary>
    private void ZoneOverlayChosen(WorldHighlightMode mode)
    {
        if (!sourceChromeActive || shownDocument?.SourceWorld == null) return;
        ResetSectionStateFor(shownDocument);
        if (mode is not (WorldHighlightMode.Zones or WorldHighlightMode.PolygonZones)) { shownZones.Clear(); return; }
        if (shownZones.Count == 0) foreach (var zone in sceneTree?.Sections?.Zones ?? []) if (zone.Used) shownZones.Add(zone.Zone);
    }
    /// <summary>The AI overlay is on while a network shows (or one is being path-edited, which keeps its own filter).</summary>
    private void SyncSourceAiOverlay()
    {
        string? editing = scene?.AiPathEditingNetwork;
        bool visible = shownNetworks.Count > 0 || editing != null;
        if (aiVisible != visible || !aiThroughGeometry || aiNetworkFilter != editing) SetAiOptions(visible, true, editing);
    }
    /// <summary>A master eye follows what is below it once all of that is shown, or all hidden.</summary>
    private void ReconcileMasters()
    {
        if (sceneTree is { Projection: not null, Sections: { } sections })
        {
            Reconcile("zones", sections.Zones.Where(z => z.Used).Select(z => shownZones.Contains(z.Zone)));
        }
        Reconcile("aiNetworks", (scene?.AiNetworks.Networks ?? []).Select(n => shownNetworks.Contains(n.Id)));
        foreach (var pass in sceneTree is { Projection: not null } tree ? tree.Roots.Where(r => r.Pass is { Kind: "aiv" or "puppies" }) : [])
        {
            Reconcile(pass.Pass!.Kind, Shown(pass));
            // Pickups' Weapons, Ammo and Other rows, and their type rows, follow their own pickups.
            foreach (var group in pass.Children.Where(c => c.MasterKey != null))
            {
                Reconcile(group.MasterKey!, Shown(group));
                foreach (var type in group.Children.Where(c => c.MasterKey != null)) Reconcile(type.MasterKey!, Shown(type));
            }
        }
        static IEnumerable<bool> Shown(SceneTreeItem row) => SceneTreeModel.PassLeaves(row).Where(c => c.CanHide && c.Index is int).Select(c => !c.IsHidden);
        void Reconcile(string section, IEnumerable<bool> shown)
        {
            bool any = false, all = true, none = true;
            foreach (bool one in shown) { any = true; all &= one; none &= !one; }
            if (any && (all || none)) masterShown[section] = all;
        }
    }

    /// <summary>Applies the zone and network eyes, the selection's emphasis and a definition's outlines to the view; the rows follow.</summary>
    private void ApplySectionPresentation()
    {
        // The overlays and editing it changes come back here: the outer pass runs again once (at most twice more) instead.
        if (applyingSectionPresentation) { sectionPresentationAgain = true; return; }
        applyingSectionPresentation = true;
        try
        {
            for (int pass = 0; pass < 3; pass++)
            {
                sectionPresentationAgain = false;
                ApplySectionPresentationOnce();
                if (!sectionPresentationAgain) break;
            }
        }
        finally { applyingSectionPresentation = sectionPresentationAgain = false; }
    }
    private void ApplySectionPresentationOnce()
    {
        {
            ResetSectionStateFor(shownDocument);
            if (scene is not { } viewport || shownDocument?.SourceWorld == null || !sourceChromeActive) { RefreshRowEyes(); return; }
            // Zones and networks no longer in the map (or no longer used) drop out; the tree's sections are the shown map's
            // (a rebuild's viewport is not shown yet and has no networks).
            var sections = sceneTree?.Sections;
            if (sections != null)
            {
                shownNetworks.RemoveWhere(id => sections.Network(id) == null);
                shownZones.RemoveWhere(z => sections.Zone(z) is not { Used: true });
            }
            if (!ZoneToolEngaged) SyncZoneOverlay();
            SyncSourceAiOverlay();
            // The listed zones not shown are hidden; a zone only units or pickups carry is not listed, and keeps its colour.
            HashSet<byte> hidden = [.. (sections?.Zones ?? []).Where(z => z.Used && !shownZones.Contains(z.Zone)).Select(z => z.Zone)];
            // A hidden zone or network is not emphasised (the others would all turn faint around nothing).
            byte? zone = ZoneOverlayOn && SectionSelection?.Section is ZoneEntry selectedZone && shownZones.Contains(selectedZone.Zone.Zone) ? selectedZone.Zone.Zone : null;
            viewport.SetZonePresentation(hidden, zone);
            // A selected network, or the network of a selected node, is emphasised.
            HashSet<string> hiddenNetworks = new(viewport.AiNetworks.Networks.Select(n => n.Id).Where(id => !shownNetworks.Contains(id)), StringComparer.Ordinal);
            string? network = SectionSelection?.Section is NetworkEntry selectedNetwork ? selectedNetwork.Network.Id
                : viewport.SelectedAiNode is { } node && viewport.AiNetworks.Find(node) is { } found ? found.Network.Id : null;
            viewport.SetAiNetworkPresentation(hiddenNetworks, network != null && (hiddenNetworks.Contains(network) || aiNetworkFilter != null && aiNetworkFilter != network) ? null : network);
            int[] outlined = SectionSelection?.Section is DefinitionEntry definition && sceneTree?.Sections?.DefinitionRoots.GetValueOrDefault(definition.Definition.Index) is { } roots
                ? [.. roots.Take(SceneViewport.MaximumOutlinedNodes)] : [];
            if (!outlined.SequenceEqual(viewport.OutlinedNodes)) viewport.SetOutlinedNodes(outlined);
            RefreshRowEyes(); RefreshViewportChips();
        }
    }

    // ---------------------------------------------------------------- eyes and header controls

    /// <summary>
    /// A section row's eye. A zone's shows or hides its colour and a network's its nodes and links, each on its own; the master
    /// eye of Zones, AI Networks, AI Vehicles or Pickups shows or hides everything below it (<paramref name="hide"/>: what to
    /// do; null: what the master eye does next).
    /// </summary>
    internal void ToggleSectionEye(SceneTreeItem row, bool? hide = null)
    {
        if (row.Owner != sceneTree) throw new StudioCommandException("stale_record", "The tree row is no longer available.");
        ResetSectionStateFor(shownDocument);
        var sections = sceneTree.Sections;
        switch (row.MasterKey)
        {
            case "zones":
            {
                if (ZoneToolEngaged) throw new StudioCommandException("busy", "Zone colours are paused while Edit zones is on: the tool shows its own zone view.");
                bool hideAll = hide ?? MasterShown("zones");
                shownZones.Clear();
                if (!hideAll) foreach (var zone in sections?.Zones ?? []) if (zone.Used) shownZones.Add(zone.Zone);
                masterShown["zones"] = !hideAll;
                break;
            }
            case "aiNetworks":
                if (!ShowAllNetworks(!(hide ?? MasterShown("aiNetworks")))) return;
                break;
            // The AI Vehicles and Pickups rows, and Pickups' group and type rows.
            case { } pass when pass is "aiv" or "puppies" || pass.StartsWith("puppies:", StringComparison.Ordinal):
            {
                if (scene == null) throw new StudioCommandException("not_ready", "Show the map first.");
                if (scene.IsPickupDragging || scene.IsFlyActive) throw new StudioCommandException("busy", "Finish the transform drag or exit Fly first.");
                bool hideAll = hide ?? MasterShown(row.MasterKey);
                foreach (var child in SceneTreeModel.PassLeaves(row))
                {
                    if (!child.CanHide || child.Index is not int index) continue;
                    if (hideAll) hiddenRows[index] = HiddenNodesFor(sceneTree, index); else hiddenRows.Remove(index);
                }
                masterShown[row.MasterKey] = !hideAll;
                pendingHidden = null;
                ApplyHiddenRows();
                break;
            }
            default:
                switch (row.Section)
                {
                    case DefinitionEntry definition:
                        if (hide ?? shownAnimationPaths.Contains(definition.Definition.Index)) HideSourceAnimationPaths(definition.Definition.Index);
                        else if (shownDocument is { } document)
                        {
                            int entry = definition.Definition.Entries.Contains(sourceAnimationEntry ?? -1) ? sourceAnimationEntry!.Value
                                : definition.Definition.Entries.Count == 1 ? definition.Definition.Entries[0] : -1;
                            if (entry < 0) throw new StudioCommandException("not_ready", "Select an exact binding in Properties, then Evaluate paths before showing its cached guides.");
                            ShowSourceAnimationPaths(document, definition.Definition.Index, entry);
                        }
                        break;
                    case ZoneEntry zone:
                        if (ZoneToolEngaged) throw new StudioCommandException("busy", "Zone colours are paused while Edit zones is on.");
                        if (!zone.Zone.Used) throw new StudioCommandException("unsupported", "No polygon or object uses this zone: it has no colour to show.");
                        if (hide ?? shownZones.Contains(zone.Zone.Zone)) shownZones.Remove(zone.Zone.Zone); else shownZones.Add(zone.Zone.Zone);
                        break;
                    case NetworkEntry network:
                        string id = network.Network.Id;
                        if (hide ?? shownNetworks.Contains(id))
                        {
                            bool endsSelection = shownNetworks.Count == 1 || scene?.SelectedAiNode is { } selectedNode && scene.AiNetworks.Find(selectedNode)?.Network.Id == id;
                            if (shownNetworks.Contains(id) && !AiOverlayMayEnd(endsSelection)) return;
                            shownNetworks.Remove(id);
                        }
                        else shownNetworks.Add(id);
                        break;
                    default: throw new StudioCommandException("unsupported", "This row has nothing to hide in the view.");
                }
                break;
        }
        ReconcileMasters();
        ApplySectionPresentation();
    }
    /// <summary>
    /// The AI Networks row's master eye (and MCP scene_options aiNodes): every network of the view shown, or hidden. False when
    /// the user kept a node's unfinished input (nothing changed).
    /// </summary>
    private bool ShowAllNetworks(bool show)
    {
        ResetSectionStateFor(shownDocument);
        if (!show && shownNetworks.Count > 0 && !AiOverlayMayEnd()) return false;
        shownNetworks.Clear();
        if (show) foreach (var network in scene?.AiNetworks.Networks ?? []) shownNetworks.Add(network.Id);
        masterShown["aiNetworks"] = show;
        return true;
    }
    /// <summary>
    /// Hiding a network ends AI path editing on it, and hiding the last one (or the selected node's) ends the AI node
    /// selection: a pending AI path transform is decided first (refused), and a node's unfinished input is asked about (false:
    /// kept, nothing changes; MCP refuses unfinished input before it comes here).
    /// </summary>
    private bool AiOverlayMayEnd(bool overlayEnds = true)
    {
        if (HasAiPathDraft) throw new StudioCommandException("pending_drafts", "Confirm or cancel the AI path transform before hiding AI networks.");
        return !overlayEnds || ResolveInspectionDrafts();
    }
    /// <summary>
    /// Group by type (the Pickups row's toggle and context menu, MCP scene_options pickupsByType): the tree is built again with
    /// or without type rows. A pickup keeps its row identity, so its selection, eye and open state stay.
    /// </summary>
    internal void SetPickupsByType(bool on, bool focusPickups = false, (bool Had, SceneTreeItem? Row)? menuFocus = null)
    {
        if (pickupsByType == on) return;
        RequirePickupsTreeReady();
        var (hadFocus, focusedRow) = menuFocus ?? (DocumentSceneTree.IsKeyboardFocusWithin, FocusedSceneRow());
        // The Pickups row, or a group or type row (its group once the types go), that had the keyboard keeps it.
        string? focusedGroup = focusedRow?.Section switch { PickupGroupEntry g => g.Group, PickupTypeEntry t => t.Group, _ => null };
        focusPickups |= focusedRow?.Pass?.Kind == "puppies";
        pickupsByType = on;
        int? selected = sceneTree is { Projection: not null, Selected: { Placement: not null, Index: int node } } ? node : null;
        RefreshSceneTree();
        if (selected is int pickup && sceneTree is { } tree)
        {
            // The selected pickup's new type row opens, unless its group was closed.
            changingSceneTree = true;
            try { if (tree.Reveal(pickup, expand: false) is { Parent: { Section: PickupTypeEntry } type } && type.Parent is { IsExpanded: true }) type.IsExpanded = true; }
            finally { changingSceneTree = false; }
            ScheduleSceneTreeReveal();
        }
        RefreshRowEyes();
        // The rebuilt tree replaced the focused row or toggle: the keyboard returns to the Pickups row or group that had it, else
        // to the selected row when it is shown (as after a rebuild), otherwise to the Pickups row.
        if (hadFocus && sceneTree is { } built) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (sceneTree != built) return;
            var pickups = built.Roots.FirstOrDefault(r => r.Pass?.Kind == "puppies");
            var target = focusedGroup != null ? pickups?.Children.FirstOrDefault(c => c.Section is PickupGroupEntry g && g.Group == focusedGroup)
                : !focusPickups && built.Selected is { } row && Ancestors(row).All(a => a.IsExpanded) ? row : null;
            if (((target != null ? RealizeRow(target) : null) ?? (pickups != null ? RealizeRow(pickups) : null)) is { } container) FocusRow(container);
        });
        static IEnumerable<SceneTreeItem> Ancestors(SceneTreeItem row) { for (var at = row.Parent; at != null; at = at.Parent) yield return at; }
    }
    private SceneTreeItem? FocusedSceneRow() => DocumentSceneTree.IsKeyboardFocusWithin
        ? PropertyContext.FindAncestor<System.Windows.Controls.TreeViewItem>(System.Windows.Input.Keyboard.FocusedElement as DependencyObject)?.DataContext as SceneTreeItem : null;
    /// <summary>Group by type waits while an edit is evaluated and while a rebuild replaces the map (its open rows and hidden
    /// pickups are then remembered by tree path).</summary>
    private void RequirePickupsTreeReady()
    {
        if (shownDocument?.SourceWorld is { IsRebuilding: true } || sourceWorkspaceBusy || !DocumentSceneTree.IsEnabled || pendingTreeLayout != null || pendingHidden != null)
            throw new StudioCommandException("busy", "The World tree is waiting for the rebuilt map; change Group by type when it is shown.");
    }
    private void OutlinerGroupByTypeClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        try { SetPickupsByType(!pickupsByType, focusPickups: (sender as UIElement)?.IsKeyboardFocused == true); }
        catch (StudioCommandException ex) { Report(ex); }
    }
    private void SceneGroupByTypeClick(object sender, RoutedEventArgs e)
    {
        try { SetPickupsByType(!pickupsByType, menuFocus: sceneMenuFocus); }
        catch (StudioCommandException ex) { Report(ex); }
    }
    /// <summary>The Zones header's Objects | Polygons switch: the overlay kind the shown zones are coloured by.</summary>
    internal void ChooseZoneOverlay(bool polygons)
    {
        if (ZoneToolEngaged) throw new StudioCommandException("busy", "Zone colours are paused while Edit zones is on.");
        var mode = polygons ? WorldHighlightMode.PolygonZones : WorldHighlightMode.Zones;
        zoneOverlayKind = mode;
        if (ZoneOverlayOn && worldHighlightMode != mode) SetWorldHighlightMode(mode);
        else ApplySectionPresentation();
    }
    private void OutlinerZoneKindClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        try { ChooseZoneOverlay((sender as FrameworkElement)?.Name == "ZoneKindPolygons"); }
        catch (StudioCommandException ex) { Report(ex); }
    }
    /// <summary>A click on a section, scope, file or Pickups group or type row opens or closes it; those rows are never the selection.</summary>
    private void OutlinerRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A press on a row's button (a disabled one does not take it) is not a click on the row; a double-click toggles once.
        for (var at = e.OriginalSource as DependencyObject; at != null && at != sender; at = at is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is System.Windows.Controls.Primitives.ButtonBase) { e.Handled = true; return; }
        if (e.ClickCount > 1 && (sender as FrameworkElement)?.DataContext is SceneTreeItem { IsSourceMap: true, Selectable: false }) { e.Handled = true; return; }
        if ((sender as FrameworkElement)?.DataContext is not SceneTreeItem { Section: SectionHeader or DefinitionScopeEntry or DefinitionFileEntry or AnimationPageEntry or PickupGroupEntry or PickupTypeEntry } row && (sender as FrameworkElement)?.DataContext is not SceneTreeItem { Pass: not null }) return;
        var item = (SceneTreeItem)((FrameworkElement)sender).DataContext;
        if (item.ChildCount == 0) return;
        e.Handled = true;
        item.IsExpanded = !item.IsExpanded;
    }

    // ---------------------------------------------------------------- selection and the card

    /// <summary>
    /// Selects a zone, network or definition row: the object selection ends and the row's card shows in Properties. Restoring
    /// the selection a rebuild replaced only selects it again (the view and the zone tool stay as they are).
    /// </summary>
    private void SelectSectionRow(SceneTreeItem row, bool restoring = false)
    {
        if (!restoring && !selectingAnimationCard && row != sceneTree?.Selected) CancelSourceAnimationSelectionWork();
        if (row.Owner != sceneTree || !row.IsSectionItem || sceneTreeDocument is not { IsDisposed: false } doc)
            throw new StudioCommandException("stale_record", "The tree row is no longer available.");
        var viewport = sceneTree.IsPreview ? TreeViewport : null;
        if (viewport?.IsPickupDragging == true || viewport?.IsFlyActive == true) throw new StudioCommandException("busy", "Finish the pickup drag or exit Fly before selecting.");
        if (!restoring && !selectingAnimationCard && row.Section is not (DefinitionEntry or AnimationBindingEntry or AnimationCallEntry)) ClearWorldAnimationCandidate();
        changingSceneTree = true;
        try
        {
            selectedNode = null; inspectedSceneSource = null; treeInspectionTarget = null;
            if (viewport != null)
            {
                if (viewport.SelectedAiNode != null) viewport.SelectAiNode(null);
                viewport.SelectInspection(null, false); viewport.SelectFramingNode(null);
            }
            CurrentInspectionCard?.ClearSourceNode();
            sceneTree.Select(row); SceneTreeModel.ExpandAncestors(row);
        }
        finally { changingSceneTree = false; }
        switch (restoring ? null : row.Section)
        {
            // Selecting a network shows it (its eye opens and the AI overlay turns on).
            case NetworkEntry network:
                ResetSectionStateFor(shownDocument); shownNetworks.Add(network.Network.Id); ReconcileMasters();
                ViewModel.Status = $"Selected AI network {SourceMapSections.NetworkStem(network.Network)}";
                break;
            // While Edit zones is engaged, a zone row is the zone to paint.
            case ZoneEntry zone:
                if (ZoneToolEngaged) RunZoneTool(() => EngageZoneTool(doc, zone: zone.Zone.Zone));
                ViewModel.Status = $"Selected {row.OutlinerLabel}";
                break;
            case DefinitionEntry definition:
                ViewModel.Status = $"Selected animation definition {definition.Definition.Name}";
                break;
        }
        ShowSectionCard(row);
        ApplySectionPresentation();
        if (!restoring) RememberSelection(row);
        ScheduleSceneTreeReveal();
    }
    /// <summary>Clears a zone, network or definition selection (Escape, a click on empty space, the card's ✕), asking about its draft first.</summary>
    private bool ClearSectionSelection()
    {
        if (SectionSelection == null) return false;
        if (!ResolveSelectionDrafts()) return true;
        CancelSourceAnimationSelectionWork();
        ClearWorldAnimationCandidate();
        changingSceneTree = true;
        try { sceneTree!.Select(null); }
        finally { changingSceneTree = false; }
        return true;
    }
    /// <summary>The tree's selection changed (a row, the view or a rebuild): the pane shows the right card and the view follows.</summary>
    private void SceneTreeSelectionChanged()
    {
        if (!sourceChromeActive) return;
        if (SectionSelection is { } row) ShowSectionCard(row);
        ApplyCardDocking(); ApplySectionPresentation(); RefreshSourceStatus();
    }

    private SourceSectionCard SectionCard()
    {
        if (sectionCard != null) return sectionCard;
        sectionCard = new SourceSectionCard { IsEnabled = !sourceWorkspaceBusy };
        sectionCard.CloseRequested = () => ClearSectionSelection();
        sectionCard.ApplyLabel = async (zone, label) =>
        {
            if (sectionCard.Document is not { IsDisposed: false } doc) throw new StudioCommandException("stale_document", "The map was closed.");
            await NameZoneAsync(LiveDocument(doc), zone, label, CancellationToken.None, committingSection: true);
        };
        sectionCard.ApplyNetwork = async (network, changes) =>
        {
            if (sectionCard.Document is not { IsDisposed: false } doc) throw new StudioCommandException("stale_document", "The map was closed.");
            await EditNetworkAsync(LiveDocument(doc), network, changes, CancellationToken.None, committingSection: true);
        };
        sectionCard.ApplyAnimation = async (definition, changes) =>
        {
            if (sectionCard.Document is not { IsDisposed: false } doc) throw new StudioCommandException("stale_document", "The map was closed.");
            StopWorldAnimationForEdit();
            await EditSourceAnimationAsync(LiveDocument(doc), definition, changes, CancellationToken.None, committingSection: true);
        };
        sectionCard.PaintZone = zone => { if (sectionCard.Document is { IsDisposed: false } doc) RunZoneTool(() => EngageZoneTool(LiveDocument(doc), erase: false, zone: zone)); };
        sectionCard.SelectObject = row =>
        {
            if (!ResolveSelectionDrafts(sceneTreeDocument) || sceneTree?.Reveal(row) is not { Selectable: true } found) return;
            try { SelectSceneTreeRow(found); }
            catch (StudioCommandException ex) { Report(ex); }
        };
        sectionCard.DraftChanged += () => { if (sectionCard.HasDraft) { StopWorldAnimationForEdit(); InvalidateWorldAnimationDropsForEdit(); sourcePathLoading?.Cancel(); } refreshAnimationPathControls?.Invoke(); UpdateDocumentCommands(); };
        return sectionCard;
    }
    /// <summary>Shows a section row's card (again, after an edit published a new tree: the draft stays with its zone or network).</summary>
    private void ShowSectionCard(SceneTreeItem row)
    {
        if (sceneTreeDocument is not { } doc || doc.SourceWorld is not { } session) return;
        var card = SectionCard();
        switch (row.Section)
        {
            case AnimationBindingEntry or AnimationCallEntry:
                _ = ReportUi(() => ShowWorldAnimationTreeCardAsync(doc, row));
                break;
            case ZoneEntry zone:
                string? master = null;
                try { master = doc.SourceBuild is { } build ? ZoneMaster(session, build) : null; } catch (StudioCommandException) { }
                card.ShowZone(doc, zone.Zone, master, ZoneToolEngaged && zoneTool.Zone == zone.Zone.Zone, true, null);
                break;
            case NetworkEntry network:
                var header = network.Network.Source is { } source ? SourceAiNetworkEdits.Read(source) : new(new Dictionary<string, SourceAiNetworkEdits.StoredField?>(SourceAiNetworkEdits.Fields.Select(f => KeyValuePair.Create(f.Key, (SourceAiNetworkEdits.StoredField?)null))), []);
                bool text = doc.Document is SourceSceneSnapshot snapshot && snapshot.Resources.Values.FirstOrDefault(r => string.Equals(r.Path, network.Network.Id, StringComparison.OrdinalIgnoreCase)) is { Syntax: not null };
                card.ShowNetwork(doc, network.Network, header, text);
                break;
            case DefinitionEntry definition:
                if (card.ShowsAnimation(doc, definition)) { SetWorldAnimationDefinitionCandidate(doc, definition); break; }
                var projection = sceneTree?.Projection;
                int[] roots = sceneTree?.Sections?.DefinitionRoots.GetValueOrDefault(definition.Definition.Index) ?? [];
                var named = roots.Select(r => (r, projection?.DisplayName(r) ?? r.ToString(CultureInfo.InvariantCulture),
                    projection?.Parent(r) is int parent ? projection.DisplayName(parent) : projection?.PassOf(r)?.Label ?? "")).ToArray();
                var notes = doc.Document is SourceSceneSnapshot built ? built.Notes.Where(n => n.StartsWith(definition.Definition.File, StringComparison.OrdinalIgnoreCase)).Take(8).Select(n => JsonData.ShownText(n, 400)).ToArray() : [];
                card.ShowDefinition(doc, definition, named, notes, sceneTree?.Sections?.Definitions.Rejection, Math.Min(roots.Length, SceneViewport.MaximumOutlinedNodes));
                if (!selectingAnimationCard) _ = LoadAnimationCardAsync(doc, definition, CancellationToken.None);
                break;
        }
    }
    /// <summary>Asks about the section card's draft (Yes applies, No discards, Cancel keeps editing).</summary>
    private bool ResolveSectionDraft(DocumentModel? doc = null)
    {
        if (sectionCard is not { } card || doc != null && card.Document != doc) return true;
        if (card.Applying) return card.Accepting;
        if (!card.HasDraft) return true;
        var resolving = card.ResolvePendingAsync();
        return resolving.IsCompleted && resolving.GetAwaiter().GetResult();
    }
    private async Task<bool> ResolveSectionDraftAsync(DocumentModel? doc = null)
    {
        if (sectionCard is not { } card || doc != null && card.Document != doc) return true;
        return await card.ResolvePendingAsync();
    }
    private bool HasSectionDraft(DocumentModel? doc = null) => sectionCard is { } card && (card.HasDraft || card.Applying && !card.Accepting) && (doc == null || card.Document == doc);
    /// <summary>After an edit's preparation: the card's draft is still the one being applied, now released for acceptance.</summary>
    private void ReleaseSectionDraft(DocumentModel doc)
    {
        if (sectionCard is not { Applying: true } card || card.Document != doc)
            throw new StudioCommandException("context_changed", "The zone or AI network card changed while the edit was prepared.");
        card.ReleaseForAcceptance(); UpdateDocumentCommands();
    }

    // ---------------------------------------------------------------- edits

    /// <summary>Edits an AI network's header in its source file: one undoable change (GUI card and MCP alike).</summary>
    private async Task<DocumentModel> EditNetworkAsync(DocumentModel document, string network, IReadOnlyList<SourceAiNetworkEdits.Change> changes, CancellationToken token, bool committingSection = false)
    {
        RequireNoDrafts(document, committing: true, committingSection: committingSection);
        if (document.SourceWorld == null || document.Document is not SourceSceneSnapshot snapshot) throw new StudioCommandException("unsupported", "This document is not a source map.");
        if (!snapshot.Resources.Values.Any(r => string.Equals(r.Path, network, StringComparison.OrdinalIgnoreCase)) || !MissionAiNetworks.IsCandidate(System.IO.Path.GetFileName(network)))
            throw new StudioCommandException("stale_record", "The AI network is no longer in this map. Read its current networks.");
        if (changes.Count == 0) throw new StudioCommandException("invalid_argument", "Name at least one field to change.");
        try
        {
            return await PrepareSourceWorldEditAsync(document, "Editing AI network", (workspace, ct) =>
            {
                byte[] bytes = workspace.Read(network, ct) ?? throw new StudioCommandException("stale_record", $"{network} is no longer in the project.");
                SourceAiNetworkEdits.Edit plan;
                try { plan = SourceAiNetworkEdits.Plan(network, bytes, changes, ct); }
                catch (System.IO.InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
                catch (InvalidOperationException) { throw new StudioCommandException("unchanged", "Nothing changed."); }
                return workspace.Apply(plan.Label, [(network, (byte[]?)plan.Content)], ct);
            }, token, committingSection: committingSection);
        }
        catch (StudioCommandException ex) when (ex.Code == "unchanged") { return document; }
    }

    // ---------------------------------------------------------------- MCP

    private void RegisterSourceSectionCommands(StudioCommands registry)
    {
        RegisterJob(registry, "source_ai_network", "Read or edit an AI network's header in its source file (data/mN/zrdr/aipath/net_NN.zrd): name (at most 19 characters: letters, digits, _ - .), type (standard, hide, fixed, defilade; the game reads the first two letters), attack_strategy (head_on, circle, back_forth, follow_net, zigzag, sit; missing or unknown means head-on), attack_rad, attack_dwell, attack_buddy (0 or 1; turning it on also stores activate_buddy, without which the game ignores it), activate_buddy (0 or 1; the game reads only that it is stored), path_width (above 0), activate_rad, pursuit_params (two numbers; a stored pursuit_range alias is kept), not_pursuit_dwell, return_range and hide_times (two numbers). The World tree's AI network card edits the same fields; each read field gives the label the card shows (the developers' Net Edit dialog names, such as Attack Radius or Pursuit Radius Min and Max). Only the edited values change: a stored value is replaced in place and a missing key is added before the nodes, keeping comments and layout; one undoable change; the file (the network's ID, which vehicles name) is never renamed. Read returns each field's label, the names of a pair's two values, and what is stored (null when not stored). A binary source, a key stored twice and an invalid value are refused; nothing to change accepts no edit.",
            [DocumentParameter, RevisionParameter with { Required = false }, P("network", "string", "The network's ID: its project path, as ai_networks and scene_tree rows give it.", true),
             new("changes", "array", "edit: the fields to change (omit to read).", Items: new("", "object", "One field.", Properties: [P("key", "string", "The stored key.", true, [.. SourceAiNetworkEdits.Fields.Select(f => f.Key)]),
                new("values", "array", "Its value(s) as text: one, two for a pair, 0 or 1 for attack_buddy and activate_buddy.", true, Items: new("", "string", "A value."), MaxItems: 2)]), MaxItems: 16)], false,
            async (args, token) =>
            {
                var document = TargetDocument(args);
                string id = Text(args, "network");
                var network = (scene?.AiNetworks.Networks ?? []).FirstOrDefault(n => n.Id == id && document == shownDocument)
                    ?? throw new StudioCommandException("stale_record", "The AI network is not in the shown map. Read its current networks.");
                if (args["changes"] is not JsonArray changes) return Result(new { document = DocumentState(document), network = id, fields = NetworkFields(network) });
                if (args["revision"]?.GetValue<long>() != document.Revision) throw new StudioCommandException("revision_conflict", "Read the current document revision.");
                var requested = changes.OfType<JsonObject>().Select(c => new SourceAiNetworkEdits.Change(Text(c, "key"), c["values"] is JsonArray values ? [.. values.Select(v => v?.GetValue<string>() ?? "")] : [])).ToArray();
                var next = await EditNetworkAsync(document, id, requested, token);
                return Result(new { document = DocumentState(next), network = id });
            });
    }
    private static JsonObject NetworkFields(AiNetwork network)
    {
        JsonObject fields = [];
        if (network.Source is not { } source) return fields;
        var header = SourceAiNetworkEdits.Read(source);
        foreach (var field in SourceAiNetworkEdits.Fields)
            fields[field.Key] = new JsonObject
            {
                ["label"] = field.Caption, ["parts"] = field.Parts is { } parts ? new JsonArray([.. parts.Select(p => (JsonNode?)p)]) : null,
                ["stored"] = header.Fields[field.Key] is { } stored ? new JsonObject
                {
                    ["storedKey"] = stored.Key, ["values"] = new JsonArray([.. stored.Values.Select((_, i) => (JsonNode?)JsonData.ShownText(stored.Text(i), 256))]), ["repeated"] = stored.Repeated ? true : null,
                } : null,
            };
        return fields;
    }
    /// <summary>The section a scene_tree row reports: its kind and identity, what it shows and its eye.</summary>
    private object? DescribeSectionRow(SceneTreeItem row) => row.Section switch
    {
        SectionHeader { Section: "zones" } => new { section = "zones", master = MasterShown("zones"), shown = shownZones.Count,
            overlay = new { visible = ZoneOverlayOn, kind = (ZoneOverlayOn ? worldHighlightMode : zoneOverlayKind) == WorldHighlightMode.PolygonZones ? "polygons" : "objects", paused = ZoneToolEngaged } },
        SectionHeader { Section: "aiNetworks" } => new { section = "aiNetworks", master = MasterShown("aiNetworks"), shown = shownNetworks.Count, ai = new { visible = aiVisible } },
        SectionHeader header => new { section = header.Section },
        ZoneEntry zone => new { zoneId = zone.Zone.Zone, label = zone.Zone.Label is { } text ? JsonData.ShownText(text, 256) : null, polygons = zone.Zone.Polygons, placements = zone.Zone.Placements, models = zone.Zone.Models,
            objects = zone.Zone.Objects, ownObjects = zone.Zone.OwnObjects, inheritedObjects = zone.Zone.InheritedObjects, emptyObjects = zone.Zone.EmptyObjects, used = zone.Zone.Used, shown = shownZones.Contains(zone.Zone.Zone) },
        NoZonesEntry none => new { noZones = true, polygons = none.Polygons },
        NetworkEntry network => new { network = network.Network.Id, name = JsonData.ShownText(network.Network.Name, 256), nodes = network.Network.Nodes.Count, attackStrategy = DescribeAiStrategy(network.Network), diagnosticCount = network.Network.Diagnostics.Count, shown = shownNetworks.Contains(network.Network.Id) },
        DefinitionScopeEntry scope => new { definitionScope = scope.Name, folder = scope.Folder, definitions = scope.Count },
        DefinitionFileEntry file => new { definitionFile = file.Listing.File, occurrence = file.Listing.Occurrence, listedBy = file.Listing.ListedBy, definitions = file.Definitions.Count },
        DefinitionEntry definition => new { definitionFile = definition.Definition.File, ordinal = definition.Definition.Ordinal, occurrence = definition.Listing.Occurrence, name = definition.Definition.Name,
            rootName = definition.Definition.RootName, roots = definition.BindingKnown ? definition.Roots : (int?)null, bound = definition.BindingKnown ? definition.Roots > 0 : (bool?)null,
            rootNodes = sceneTree?.Sections?.DefinitionRoots.GetValueOrDefault(definition.Definition.Index)?.Take(64).ToArray() },
        AnimationBindingEntry binding => new { kind = "animationBinding", entry = binding.Binding.Entry, definition = binding.Definition.Definition.Index,
            rootName = binding.Binding.Root, name = binding.Binding.Name },
        AnimationCallEntry call => new { kind = "animationCall", rootEntry = call.RootEntry, callerEntry = call.Call.Entry, targetEntry = call.Call.TargetEntry,
            sequence = call.Call.Sequence, @event = call.Call.Event, path = call.Call.Path, parentPath = call.ParentPath, waitForCompletion = call.Call.WaitForCompletion,
            included = row.AnimationIncluded, inclusionEnabled = row.AnimationIncludeEnabled },
        AnimationPageEntry page => new { kind = "animationPage", rootEntry = page.RootEntry, entry = page.Entry, parentPath = page.ParentPath, offset = page.Offset },
        MissingFileEntry missing => new { definitionFile = missing.Listing.File, missing = true, listedBy = missing.Listing.ListedBy },
        NoteEntry note => new { note = note.Text },
        PickupGroupEntry group => new { kind = "pickupGroup", group = group.Group, count = group.Entries.Count, shown = group.Entries.Count(e => e.Row != null) },
        PickupTypeEntry type => new { kind = "pickupType", group = type.Group, pickupType = type.Type, shortName = row.Pickup?.ShortName, name = row.Pickup?.ManualName,
            count = type.Entries.Count, shown = type.Entries.Count(e => e.Row != null) },
        _ => null,
    };
    /// <summary>
    /// preview_state's view of the sections: the zones and networks shown, the master eyes (true: shown, so pressing hides),
    /// and the selection's emphasis or outlines.
    /// </summary>
    private object? SectionPreviewState() => scene is not { } viewport || shownDocument?.SourceWorld == null ? null : new
    {
        zones = new { overlay = ZoneOverlayOn, kind = (ZoneOverlayOn ? worldHighlightMode : zoneOverlayKind) == WorldHighlightMode.PolygonZones ? "polygons" : "objects", paused = ZoneToolEngaged,
            shown = shownZones.Order().Select(z => (int)z).ToArray(), master = MasterShown("zones"), emphasis = (int?)viewport.ZoneSelection },
        networks = new { shown = shownNetworks.Order(StringComparer.Ordinal).Take(256).ToArray(), shownCount = shownNetworks.Count, master = MasterShown("aiNetworks"), emphasis = viewport.AiEmphasizedNetwork },
        passes = new { aiv = MasterShown("aiv"), puppies = MasterShown("puppies"), pickupsByType },
        outlines = new { roots = viewport.OutlinedNodes.Count, boxes = viewport.OutlinedBoxes },
        selection = SectionSelection is { } row ? new { row = row.Id, kind = row.Category, key = row.Section!.Key } : null,
    };
}
