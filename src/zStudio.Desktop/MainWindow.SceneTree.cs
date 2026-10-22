using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private SceneTreeModel? sceneTree;
    private DocumentModel? sceneTreeDocument;
    private bool changingSceneTree;
    private string? treeInspectionTarget;
    private int sceneTreeRevealGeneration;
    private readonly HashSet<SceneTreeItem> collapsingSceneRows = [];
    private readonly ConditionalWeakTable<DocumentModel, Dictionary<string, SceneTreeState>> sceneTreeStates = new();
    private SceneViewport? TreeViewport => EmptyPreview.Visibility == Visibility.Collapsed && retiringScene == null ?
        motion?.Viewport ?? animation?.Viewport ?? (SceneHost.Visibility == Visibility.Visible ? scene : null) : null;

    private void RefreshSceneTree()
    {
        if (changingSceneTree) return;
        var doc = ViewModel.SelectedDocument;
        var viewport = doc == shownDocument ? TreeViewport : null;
        var data = viewport?.PreviewScene ?? doc?.Document.Scene;
        bool active = data != null && viewport?.PreviewScene == data;
        if (doc?.IsDisposed == true) { doc = null; data = null; }
        // A source map shows only its projected World tree: while a rebuild replaces the view (or its projection is prepared),
        // the shown tree stays, disabled, rather than the stored scene's compiled list.
        bool sourceMap = doc?.SourceWorld != null;
        if (sourceMap && (!active || SourceSelection(doc!, data) == null))
        {
            // The same map's tree (a rebuild replaces its document) stays until the rebuilt one is shown.
            if (sceneTree?.Projection != null && (sceneTreeDocument == doc || sceneTreeDocument?.SourceWorld is { } shownWorld && shownWorld.Workspace == doc!.SourceWorld!.Workspace && shownWorld.Mission == doc.SourceWorld.Mission))
            {
                if (DocumentSceneTree.IsKeyboardFocusWithin) outlinerFocusPending = true;
                DocumentSceneTree.IsEnabled = OutlinerResults.IsEnabled = OutlinerShowAll.IsEnabled = false; return;
            }
            data = null;
        }
        DocumentSceneTree.IsEnabled = OutlinerResults.IsEnabled = OutlinerShowAll.IsEnabled = true;
        if (sceneTreeDocument == doc && sceneTree?.Hierarchy.Scene == data && sceneTree?.IsPreview == active && ReferenceEquals(sceneTree?.Hierarchy.Included, active ? viewport?.InspectionNodes : null)
            && (sceneTree?.Projection == null || sceneTree.PickupsByType == pickupsByType))
        {
            // A rebuild that kept this tree gives the keyboard back to it too.
            if (outlinerFocusPending) { if (sceneTree?.Selected == null) outlinerFocusPending = false; else ScheduleSceneTreeReveal(); }
            return;
        }
        changingSceneTree = true;
        try
        {
            if (sceneTree != null) { sceneTree.Collapsing -= SceneTreeCollapsing; sceneTree.SelectionChanged -= SceneTreeSelectionChanged; }
            collapsingSceneRows.Clear();
            sceneTreeDocument = doc; treeInspectionTarget = null; inspectedSceneSource = null;
            if (doc == null || data == null) sceneTree = null;
            else if (!active) sceneTree = doc.StoredSceneTree;
            else
            {
                string source = viewport!.InspectionSourcePath ?? doc.Path;
                var states = sceneTreeStates.GetOrCreateValue(doc);
                string key = source.ToUpperInvariant();
                if (!states.TryGetValue(key, out var state))
                {
                    // Only presentation preferences are retained, never a hidden renderer.
                    if (states.Count >= 16) states.Remove(states.Keys.First());
                    states[key] = state = new();
                }
                var projection = SourceSelection(doc, data);
                sceneTree = new(data, source, true, state, TreeIdentities(data, viewport.Mission, doc.PickupEdits), viewport.InspectionNodes, projection,
                    projection != null ? MapSections(doc, data, viewport.Mission, projection) : null, pickupsByType) { SectionState = this };
            }
            if (sceneTree != null) { sceneTree.Collapsing += SceneTreeCollapsing; sceneTree.SelectionChanged += SceneTreeSelectionChanged; var hidden = hiddenRows.Values.SelectMany(n => n).ToHashSet(); sceneTree.IsHiddenNode = hidden.Contains; sceneTree.IsIsolatedNode = node => isolatedNode == node; }
            DocumentSceneTree.ItemsSource = sceneTree?.Roots;
            // Renderer replacement clears its framing identity. Restore it from
            // the tree's provenance match, together with source inspection, without
            // selecting a runtime copy, moving the camera or resolving drafts.
            if (sceneTree?.Selected is { Selectable: true, Index: int retained } selected)
            {
                inspectedSceneSource = selected;
                selectedNode = active && !selected.SourceGroup ? retained : null;
                SetProperties(SceneTreeProperties(selected));
                if (active && !selected.SourceGroup && viewport!.SelectedInspection == null && viewport.SelectedAiNode == null)
                    viewport.SelectFramingNode(retained);
            }
        }
        finally { changingSceneTree = false; }
        UpdateNavigatorAvailability();
        ScheduleSceneTreeReveal();
        RestoreHiddenIdentities(doc);
        RestoreTreeLayout(doc);
        // The keyboard goes back to a selected row (a restored zone, network or definition too); without one it stays where it went.
        if (sceneTree?.Selected == null) outlinerFocusPending = false;
        if (sourceChromeActive) { RefreshOutlinerFilter(); SceneTreeSelectionChanged(); }
    }

    internal static Func<int, SceneTreeIdentity> TreeIdentities(GameScene data, MissionSceneContext? mission, PickupPlacementEditSession? edits)
    {
        if (mission == null) return i => new("source:" + i, i, null);
        // Mission publication prepares ownership once. Other preview scenes use the same bounded
        // propagation against their own topology, never one descendant walk per actor occurrence.
        var actors = mission.PrepareActorIndex(data);
        string ambiguous = Guid.NewGuid().ToString("N");
        SceneTreeProvenance provenance = new(edits?.SourceComparer);
        Dictionary<MissionPickupSource, string> sources = new(edits?.SourceComparer ?? ReferenceEqualityComparer.Instance);
        var actorCounts = mission.Actors.GroupBy(a => (a.SourceRoot, a.Name)).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<MissionActor, string> actorKeys = new(ReferenceEqualityComparer.Instance);
        return index =>
        {
            int sourceNode = index >= 0 && index < mission.SourceNodes.Count ? mission.SourceNodes[index] : index;
            if (actors.IsAmbiguous(index)) return new(ambiguous + ":" + index, sourceNode, "Ambiguous mission instance");
            var actor = actors.At(index);
            if (actor == null) return new("source:" + sourceNode, sourceNode, null);
            var source = actor.Pickup?.Source ?? actor.CoordinateSource;
            string identity;
            if (source != null)
            {
                if (!sources.TryGetValue(source, out identity!))
                {
                    var matches = edits != null && (edits.Find(source) != null || edits.Coordinate(source) != null) ? edits.Scope(source).Sources : [source];
                    identity = provenance.Scope(matches);
                    sources[source] = identity;
                }
            }
            else if (actorCounts[(actor.SourceRoot, actor.Name)] != 1) identity = ambiguous + ":" + index;
            else if (!actorKeys.TryGetValue(actor, out identity!)) actorKeys[actor] = identity = provenance.Actor(actor.SourceRoot, actor.Name);
            return new(identity + ":source:" + sourceNode, sourceNode, actor.PlacementSource, actor.Name);
        };
    }

    private void RevealSceneNode(int index)
    {
        if (changingSceneTree) return;
        RefreshSceneTree();
        if (sceneTree?.IsPreview != true || sceneTreeDocument != shownDocument) return;
        changingSceneTree = true;
        try { if (sceneTree.Reveal(index) is { } row) sceneTree.Select(row); }
        finally { changingSceneTree = false; }
        ScheduleSceneTreeReveal();
    }
    private void SceneTreeInspectionChanged(SceneViewport viewport)
    {
        if (changingSceneTree || viewport != TreeViewport || shownDocument != ViewModel.SelectedDocument) return;
        RefreshSceneTree();
        string? target = viewport.SelectedInspection?.Target;
        if (target == treeInspectionTarget) return;
        treeInspectionTarget = target;
        if (viewport.SelectedInspection is { Node: >= 0 } selected)
        {
            bool inspectingSource = inspectedSceneSource != null;
            RevealSceneNode(selected.SelectionNode);
            if (sceneTree?.Selected is { } shownRow && shownRow.Index == selected.SelectionNode) RememberSelection(shownRow);
            // Retain source-inspection intent, but never the previously selected
            // row. The existing Properties window itself remains independently pinned.
            if (inspectingSource) inspectedSceneSource = sceneTree?.Selected?.Index == selected.SelectionNode ? sceneTree.Selected : null;
        }
        else if (viewport.SelectedInspection?.AiNode != null)
        {
            changingSceneTree = true;
            try { sceneTree?.Select(null); inspectedSceneSource = null; }
            finally { changingSceneTree = false; }
        }
    }
    private void SceneTreeCollapsing(SceneTreeItem row)
    {
        collapsingSceneRows.Add(row);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => collapsingSceneRows.Remove(row));
    }
    private void ClearSceneTreeSelection(SceneViewport viewport)
    {
        if (changingSceneTree || viewport != TreeViewport || shownDocument != ViewModel.SelectedDocument) return;
        changingSceneTree = true;
        try { sceneTree?.Select(null); inspectedSceneSource = null; selectedNode = null; treeInspectionTarget = null; }
        finally { changingSceneTree = false; }
    }
    private void SceneTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (changingSceneTree || e.NewValue is not SceneTreeItem row || row.Owner != sceneTree || sceneTree.Selected == row) return;
        var previous = sceneTree.Selected;
        // WPF promotes a hidden selected descendant to its collapsed ancestor.
        // That is presentation work, not intentional source navigation or a
        // reason to resolve a draft. Retain the logical descendant selection.
        if (collapsingSceneRows.Contains(row) && previous != null)
            for (var parent = previous.Parent; parent != null; parent = parent.Parent)
                if (parent == row) { Restore(); return; }
        if (!row.Selectable) { Restore(); return; }
        if (!ResolveSelectionDrafts(sceneTreeDocument) || animation?.ResolvePendingDrafts() == false) { Restore(); return; }
        try { if (row.IsSectionItem) SelectSectionRow(row); else SelectSceneTreeRow(row); }
        catch (StudioCommandException ex) { Notice(ex.Message); Restore(); }
        void Restore()
        {
            // A click selects its row while WPF is still moving keyboard focus to it. Selecting another row inside that
            // change makes the two rows take focus and select each other until the stack overflows: restore afterwards.
            var model = sceneTree;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (sceneTree != model || model == null || !row.IsSelected) return;
                changingSceneTree = true;
                try { row.IsSelected = false; model.Select(previous); }
                finally { changingSceneTree = false; }
            });
        }
    }
    private void SelectSceneTreeRow(SceneTreeItem row)
    {
        if (row.IsSectionItem) { SelectSectionRow(row); return; }
        if (row.Owner != sceneTree || row.Index is not int node || !row.Selectable || sceneTreeDocument is not { IsDisposed: false } doc)
            throw new StudioCommandException("stale_record", "The tree node is no longer available.");
        var viewport = sceneTree.IsPreview ? TreeViewport : null;
        if (viewport?.IsPickupDragging == true || viewport?.IsFlyActive == true) throw new StudioCommandException("busy", "Finish the pickup drag or exit Fly before selecting a node.");
        changingSceneTree = true;
        try
        {
            if (viewport != null && !row.SourceGroup)
            {
                // Revealing a runtime card never comes through here. An intentional
                // source selection may choose a visible pose; an existing exact copy is retained.
                if (viewport.SelectedInspection?.SelectionNode != node && !viewport.SelectInspectionNode(node)) viewport.SelectInspection(null, false);
                if (animation == null && node >= 0) InspectNode(node);
                selectedNode = node; viewport.SelectFramingNode(node);
            }
            else
            {
                selectedNode = null;
                if (viewport != null) { viewport.SelectInspection(null, false); viewport.SelectFramingNode(null); }
            }
            SetProperties(SceneTreeProperties(row));
            inspectedSceneSource = row;
            sceneTree.Select(row); SceneTreeModel.ExpandAncestors(row);
            if (doc.SourceWorld != null && viewport != null) FocusSourceObjectCard(doc, node);
            if (viewport?.InspectionContent is SceneInspectionCard card) card.Refresh();
            ViewModel.Status = row.SourceGroup ? $"Selected source group: {row.DisplayName}" : row.Synthetic ? $"Selected reference: {row.DisplayName}" : $"Selected node #{node}: {row.PickupNameWithKind ?? row.DisplayName}";
            RememberSelection(row);
        }
        finally { changingSceneTree = false; }
        ScheduleSceneTreeReveal();
    }
    private JsonObject SceneTreeProperties(SceneTreeItem row)
    {
        if (row.Synthetic) return new()
        {
            ["name"] = row.DisplayName, ["kind"] = row.SourceGroup ? "source group" : "source reference group", ["source_occurrence"] = row.StateKey,
            ["editable"] = false, ["reason"] = row.ReadOnlyReason, ["scene_source"] = row.Owner.Source,
            ["source_file"] = row.Authored?.File, ["gltf_node_index"] = row.Authored?.SourceNode,
            ["parent"] = row.Parent?.DisplayName,
        };
        var node = row.Node!; var properties = JsonData.PreviewObject(node.Metadata);
        properties["node_index"] = node.Index; properties["source_node_index"] = row.Owner.Identity(node.Index).SourceNode;
        properties["scene_source"] = row.Owner.Source;
        if (row.Authored is { } authored)
        {
            properties["source_file"] = authored.File; properties["gltf_node_index"] = authored.SourceNode;
            properties["source_occurrence"] = row.StateKey; properties["parent"] = row.Parent?.DisplayName;
            properties["compiled_node_index"] = authored.CompiledNode;
        }
        properties["hierarchy"] = row.Owner.Projection != null ? "Authored source hierarchy" : row.Owner.IsPreview ? "Bound preview scene (not runtime parenting)" : "Stored document scene";
        string prefix = row.Owner.Projection != null ? "compiled_" : "";
        properties[prefix + "parent_indices"] = JsonData.Integers(node.Parents.Take(64)); properties[prefix + "parent_count"] = node.Parents.Length;
        properties[prefix + "child_indices"] = JsonData.Integers(node.Children.Take(64)); properties[prefix + "child_count"] = node.Children.Length;
        if (node.Parents.Length > 64 || node.Children.Length > 64) properties["inspection_truncated"] = true;
        return properties;
    }
    private void ScheduleSceneTreeReveal(SceneTreeItem? reveal = null)
    {
        var model = sceneTree; var selected = reveal ?? model?.Selected;
        int generation = ++sceneTreeRevealGeneration;
        if (selected == null) return;
        // A hidden tree has no row to give the keyboard back to after a rebuild.
        if (!DocumentSceneTree.IsVisible) { outlinerFocusPending = false; return; }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (sceneTree != model || generation != sceneTreeRevealGeneration) return;
            if (!DocumentSceneTree.IsVisible) { outlinerFocusPending = false; return; }
            changingSceneTree = true;
            try
            {
                Stack<SceneTreeItem> path = new(); for (var item = selected; item != null; item = item.Parent) path.Push(item);
                ItemsControl parent = DocumentSceneTree;
                while (path.TryPop(out var item))
                {
                    parent.ApplyTemplate(); parent.UpdateLayout();
                    if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem)
                    {
                        var presenter = VisualChild<ItemsPresenter>(parent); presenter?.ApplyTemplate();
                        if (presenter != null && VisualTreeHelper.GetChildrenCount(presenter) > 0 && VisualTreeHelper.GetChild(presenter, 0) is VirtualizingPanel panel)
                            panel.BringIndexIntoViewPublic(parent.Items.IndexOf(item));
                        parent.UpdateLayout();
                    }
                    if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) break;
                    if (item == selected)
                    {
                        bool chosen = model!.Selected == item;
                        if (chosen) container.IsSelected = true;
                        container.BringIntoView();
                        // Only the selected row takes the keyboard back (a revealed row that is not selected would turn selected).
                        if (chosen) RestoreOutlinerFocus(container);
                    }
                    parent = container;
                }
            }
            finally { changingSceneTree = false; }
        });
    }
    private static T? VisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) return result;
            if (child is TreeViewItem) continue;
            if (VisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void RegisterSceneTreeCommand(StudioCommands commands)
    {
        Register(commands, "scene_tree", "Read the Document scene hierarchy, including stable preview placements, shared/partition references and disconnected nodes. Source maps show the World tree: the authored glTF hierarchy, including groups removed by compilation, under the World root, then section rows (sectionRow.section) and pass rows in a fixed order: World, Zones, AI Vehicles (pass aiv: units the AIV resources place), AI Networks, Pickups (pass puppies), Animations (keyframed) (pass anim: objects startup animations place or hide until placed) and Animations (definitions). Zones lists the map's zones (sectionRow zoneId, label, polygons, placements, models, objects = ownObjects + inheritedObjects with emptyObjects among them, used) and polygons without a zone; AI Networks lists the mission's networks (sectionRow network = the file's project path, name, nodes, attackStrategy); Animations (definitions) lists the .zad files the mission's anim.zad names, by folder and file, with each definition (sectionRow definitionFile, ordinal, occurrence, name, rootName, roots/bound: what it binds in this world, null when unknown) and listed files that are missing. Zones and AI networks are shown one by one and start hidden (sectionRow shown); any shown zone turns the zone overlay on and any shown network the AI overlay (always drawn through geometry, at half strength where it hides them). The Zones, AI Networks, AI Vehicles and Pickups rows carry master (true: shown, so hide is what their eye does next; it follows when everything below comes to be shown or hidden one by one); Zones also reports its overlay kind (objects or polygons) and paused while Edit zones is on. The aiv and puppies passes list every difficulty's units and pickups with the difficulties placing each; those the shown difficulty does not place have otherDifficulty=true, node=null and a placement summary, and cannot be selected; placed rows matched to their records carry the placement summary too (including how often a resource lists that placement). Pickups always hold three group rows, Weapons, Ammo and Other (category pickupGroup; sectionRow kind, group weapons/ammo/other, count and shown = placed on the shown difficulty), which open and close, hide and show (master) and frame their pickups but are never selected; with scene_options pickupsByType each holds a row per catalog type (category pickupType; sectionRow group, pickupType, shortName, name, count, shown). Pickup rows expose pickupType (catalog name), pickupKind (ammo, weapon, health or technology), pickupGroup, pickupNumber (per catalog type in list order over every difficulty), pickupShortName, pickupName (the game manual's name; ammo adds the word ammo), instanceName (the game's name, null on other-difficulty rows) and pickupTemplate; their Label is the short name and number (ERFPG #1). These fields are null on non-pickup rows. The player tank's row (the first vehicle the shown difficulty's AIV layout spawns, identified by placement, not name) has playerTank true, null elsewhere; preview_state.bft describes how the view draws it. A unit or pickup every difficulty places shows All as its difficulty tag. animations lists resolved initial-root and indirect animation associations (at most eight names, then an ellipsis). animationUses supplies up to eight entryIndex/name/origin/reason records (origin initialRoot or indirect); animationUsesTruncated discloses omitted details and animationHasInitialRoot and animationHasIndirect retain both origin summaries even when details are capped. The original cyan icon marks any initial-root association; indirect-only recipients use the same glyph at half HSL saturation. Position references and unknown impact targets do not imply animation ownership. sourceFile/gltfNode identify authored nodes, sourceOccurrence identifies a tree path, and compiledNode is an optional built slot. selectionOwner for source_map_parts is a signed current-preview identity; source-only authored groups have node=null and selectionOwner=null and support read-only row inspection. properties focuses the source-object card, or opens pinned Properties in other viewers. Read returns roots or a row's direct children, optionally filtered before paging; in a source map, search and/or types read the whole hierarchy as a flat list of matches with their parent paths, as the outliner's search box does (types narrows it by the legend's categories). A row's zone is null in the aiv and puppies passes. Source map rows carry category (world, group, geometry, empty: an Object3D without a model or children, which draws nothing and cannot be hidden or framed; object, node, zone, network, definition), zone and hidden. Expand/collapse/reveal only change tree presentation; hide/show hide a row's object (a removed group: what it holds) in the view only, like the outliner's eye, except that show, and an isolate that ends or moves an isolation (as scene_selection show_all and isolate), select in the view an object the card shows as hidden because it was selected in the tree while hidden (only with no draft open; after an ai_path or parachute draft ends this follows once the application is idle, after that command returned, so read scene_inspect or preview_state afterwards): the view selection, the card and scene_inspect selected change, and its placement can be edited; on the Zones, AI Networks, AI Vehicles (pass aiv) and Pickups (pass puppies) rows they hide or show everything below them, on a zone its colour and on a network its nodes and links (preview_state sections reports them); isolate works on world objects only (zones, networks, units and pickups are refused). select shares scene inspection and draft guards; selecting a zone, network or definition ends the object selection and opens its card in the Properties pane (a network is shown and emphasised, a definition's bound objects outlined; drafts target section), and properties also focuses the card's first field. Runtime parenting is not represented.", true,
            [DocumentParameter, P("action", "string", "Tree action; default read. Properties focuses the source-object card; other viewers open the same pinned window as the row context menu. hide/show (source maps) change only what the view draws; isolate (source maps) shows only the row's object, or everything again on the isolated row, as a middle-click on its eye. frame (source maps: the Pickups section's Weapons, Ammo and Other rows and their type rows) frames their pickups on the shown difficulty without selecting anything, as the row's Frame button.", false, "read", "expand", "collapse", "reveal", "select", "properties", "hide", "show", "isolate", "frame"),
                P("search", "string", "Source maps: search across the whole hierarchy (read without row): every word must appear, ignoring case, in a row's name (a pickup's also in its manual and catalog names, kind, template and game name); results are flat rows with path."),
                P("types", "string", "Source maps: comma-separated categories the flat read includes (group, geometry, empty, object, node, zone, network, definition); default all."),
                P("context", "string", "Current hierarchy context from read; required for all actions and row queries."),
                P("preview", "string", "Current preview ID from read; required for actions on a preview hierarchy."),
                P("row", "string", "Opaque row ID for child queries or actions."), new("node", "integer", "Scene node index or signed selectionOwner for reveal/select/properties instead of row, scoped to the current hierarchy.", Minimum: int.MinValue, Maximum: int.MaxValue), .. PageParameters], async (a, token) =>
        {
            var doc = TargetDocument(a); RefreshSceneTree();
            if (doc != sceneTreeDocument || sceneTree == null) throw new StudioCommandException("not_ready", "Document scene is unavailable for this document.");
            if (Int(a, "offset") < 0 || Int(a, "limit", 100) is < 1 or > 200) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–200.");
            string action = Text(a, "action", "read");
            if ((action != "read" || a["row"] != null || a["context"] != null) && Text(a, "context") != sceneTree.Context)
                throw new StudioCommandException("stale_context", "The hierarchy changed. Read its current roots.");
            if (sceneTree.IsPreview && (action != "read" || a["preview"] != null)) RequirePreview(a);
            if (a["row"] != null && a["node"] != null) throw new StudioCommandException("invalid_argument", "Choose a row or node identity.");
            var row = a["row"] == null ? null : sceneTree.Find(Text(a, "row")) ?? throw new StudioCommandException("stale_record", "Tree row unavailable.");
            if (action != "read")
            {
                if (action is "select" or "properties")
                {
                    if (action == "properties") RequireNoDrafts(); else RequireNoSelectionDrafts(doc);
                    if (sceneTree.IsPreview && TreeViewport is { } viewport && (viewport.IsPickupDragging || viewport.IsFlyActive))
                        throw new StudioCommandException("busy", "Finish the pickup drag or exit Fly before selecting a node.");
                }
                if (a["node"] != null)
                {
                    if (action is not ("select" or "reveal" or "properties")) throw new StudioCommandException("invalid_argument", "Expand, collapse, hide, show, isolate and frame require a row.");
                    changingSceneTree = true;
                    try { row = sceneTree.Reveal(Int(a, "node")); }
                    finally { changingSceneTree = false; }
                }
                if (row == null) throw new StudioCommandException("stale_record", "Supply an available tree row or node.");
                if (row.Pass != null && action is "select" or "properties") throw new StudioCommandException("invalid_argument", "A pass row groups the objects a later pass adds; select one of them.");
                if (row.PickupEntries != null && action is "select" or "properties")
                    throw new StudioCommandException("invalid_argument", $"{row.OutlinerLabel} is a group of pickups, not something to select; select one of its pickups.");
                if (action == "frame")
                {
                    if (row.PickupEntries == null) throw new StudioCommandException("invalid_argument", "frame works on the Pickups section's group and type rows; select other rows and frame the selection with camera.");
                    if (!row.CanFrame) throw new StudioCommandException("invalid_argument", row.PickupEntries.Count == 0 ? row.FrameToolTip + "." : "Its pickups are placed only on other difficulties; choose one of them (scene_options difficulty) to frame them.");
                    if (TreeViewport is { } framing && (framing.IsPickupDragging || framing.IsFlyActive)) throw new StudioCommandException("busy", "Finish the pickup drag or exit Fly before framing.");
                    if (!TryFrameSection(row)) throw new StudioCommandException("not_ready", "None of these pickups is shown in the view to frame: show them (hide/show) first.");
                    return Result(new { context = sceneTree.Context, document = doc.SessionId, revision = doc.Revision, row = DescribeTreeRow(row) });
                }
                if (row.Section != null && !row.IsSectionItem && action is "select" or "properties") throw new StudioCommandException("invalid_argument", "A section, scope or file row groups other rows; select one of them.");
                if ((row.Section != null || row.HasMasterEye || row.IsPassObject) && action == "isolate")
                    throw new StudioCommandException("unsupported", "Zones, AI networks, AI vehicles and pickups are shown and hidden one by one (hide/show on their rows), or all at once on their section's row; they are not isolated.");
                if ((row.Section != null || row.HasMasterEye) && action is "hide" or "show")
                {
                    if (!row.CanHide) throw new StudioCommandException("unsupported", "This row has nothing to hide in the view.");
                    // Hiding networks can end an AI node selection: unfinished input is refused, never asked about.
                    if (action == "hide" && (row.MasterKey == "aiNetworks" || row.Section is NetworkEntry)) RequireNoDrafts(doc);
                    ToggleSectionEye(row, action == "hide");
                    var described = DescribeTreeRow(row);
                    return Result(new { context = sceneTree.Context, document = doc.SessionId, revision = doc.Revision, row = described });
                }
                if (row.IsSectionItem && action is "select" or "properties")
                {
                    SelectSectionRow(row);
                    if (action == "properties") sectionCard?.FocusFirst();
                    return Result(new { context = sceneTree.Context, document = doc.SessionId, revision = doc.Revision, row = DescribeTreeRow(row) });
                }
                if (row.IsOtherDifficulty && action is "select" or "properties")
                    throw new StudioCommandException("invalid_argument", row.Placement!.Difficulties.Count == 1
                        ? $"Only {row.Placement.Difficulties[0]} places this; choose that difficulty (zstudio_scene_options difficulty) to select it."
                        : $"Only {string.Join(", ", row.Placement.Difficulties)} place this; choose one of them (zstudio_scene_options difficulty) to select it.");
                if (action is "hide" or "show")
                {
                    if (!row.CanHide || row.Index is not int hiddenIndex) throw new StudioCommandException("unsupported", row.IsEmptyNode ? "An empty node draws nothing: there is nothing to hide." : "Only a source map's object rows can be hidden in the view.");
                    SetRowHidden(row, hiddenIndex, action == "hide");
                }
                if (action == "isolate")
                {
                    if (!row.CanHide || row.Index is not int isolatedIndex) throw new StudioCommandException("unsupported", row.IsEmptyNode ? "An empty node draws nothing: there is nothing to isolate." : "Only a source map's object rows can be isolated in the view.");
                    ToggleRowIsolation(row, isolatedIndex);
                }
                if (action == "properties")
                {
                    var model = sceneTree;
                    if (doc.SourceWorld != null && !IsSourceTerrain(doc, row.Index))
                    {
                        SelectSceneTreeRow(row);
                        if (row.Index is int sourceNode && !row.SourceGroup && !FocusSourceObjectCard(doc, sourceNode)) throw new StudioCommandException("not_ready", "The source object panel is unavailable.");
                    }
                    else
                    {
                        var opened = await OpenScenePropertiesAsync(doc, row, token, automation: true);
                        if (sceneTree != model || opened == null) throw new StudioCommandException("context_changed", "The requested hierarchy Properties target was not published.");
                    }
                }
                else if (action == "select") SelectSceneTreeRow(row);
                else if (action == "expand") row.IsExpanded = true;
                else if (action == "collapse") row.IsExpanded = false;
                else if (action == "reveal") SceneTreeModel.ExpandAncestors(row);
                if (action == "reveal") ScheduleSceneTreeReveal(row);
            }
            else if (a["node"] != null) throw new StudioCommandException("invalid_argument", "Use node with reveal/select.");
            if (action == "read" && (a["search"] != null || a["types"] != null))
            {
                if (sceneTree.Projection == null) throw new StudioCommandException("unsupported", "Hierarchy search is available in source maps.");
                if (row != null) throw new StudioCommandException("invalid_argument", "search and types read the whole hierarchy; omit row.");
                string search = Text(a, "search").Trim();
                if (search.Length > 256) throw new StudioCommandException("invalid_argument", "Search text is limited to 256 characters.");
                var types = a["types"] == null ? OutlinerKinds.Categories.ToHashSet() : Text(a, "types").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
                if (types.Count == 0 || types.Any(t => !OutlinerKinds.Categories.Contains(t))) throw new StudioCommandException("invalid_argument", "types lists group, geometry, empty, object, node, zone, network and/or definition.");
                var hits = OutlinerMatches(sceneTree, search, types, 10_000, out bool truncated);
                var found = Page(hits, a, h => h.Row.Label, h => new { path = h.Path, item = DescribeTreeRow(h.Row) },
                    maximumRowBytes: h => 2048 + 6L * (h.Path.Length + h.Row.Label.Length + (h.Row.Authored?.File.Length ?? 0) + (h.Row.Animations?.Sum(n => n.Length + 4) ?? 0) + AnimationUseSize(h.Row) + (h.Row.ReadOnlyReason?.Length ?? 0) + (h.Row.Placement?.Detail.Length ?? 0)));
                return Result(new { context = sceneTree.Context, document = doc.SessionId, revision = doc.Revision, preview = sceneTree.IsPreview ? previewId.ToString() : null,
                    search, types = types.Order().ToArray(), matchesTruncated = truncated, matches = found.Data });
            }
            var page = Page(row == null ? sceneTree.Roots : row.Children, a, r => r.PickupName != null && !r.IsOtherDifficulty ? r.Label + " " + r.Placement!.Name : r.Label, r => DescribeTreeRow(r),
                maximumRowBytes: r => 2048 + 6L * (r.Label.Length + (r.Authored?.File.Length ?? 0) + (r.Pass?.Description.Length ?? 0) + (r.Animations?.Sum(a => a.Length + 4) ?? 0) + AnimationUseSize(r)
                    + (r.ReadOnlyReason?.Length ?? 0) + (r.Problem?.Length ?? 0) + (r.Placement?.Detail.Length ?? 0)));
            return Result(new { context = sceneTree.Context, document = doc.SessionId, revision = doc.Revision,
                preview = sceneTree.IsPreview ? previewId.ToString() : null, source = sceneTree.Source,
                hierarchy = sceneTree.Projection != null ? "authored source hierarchy" : sceneTree.IsPreview ? "bound preview scene" : "stored scene", selected = sceneTree.Selected?.Id,
                row = row == null ? null : DescribeTreeRow(row), children = page.Data });
        });
    }
    private static long AnimationUseSize(SceneTreeItem row) => row.AnimationUses?.Uses.Sum(u => 128L + u.Name.Length + u.Origin.Length + u.Reason.Length) ?? 0;
    private object DescribeTreeRow(SceneTreeItem row) => new { row = row.Id, node = row.Synthetic ? null : row.Index, row.Label,
        selectionOwner = row.Owner.Projection != null && row.Selectable && !row.SourceGroup ? row.Index : null,
        nodeType = row.NodeType,
        parent = row.Parent?.Id, relation = row.Kind, row.Shared, row.Problem, row.ChildCount, row.IsExpanded, row.IsSelected,
        synthetic = row.Synthetic, referencedObject = row.ReferencedObject, readOnlyReason = row.ReadOnlyReason,
        sourceFile = row.Authored?.File, gltfNode = row.Authored?.SourceNode, sourceOccurrence = row.Authored == null ? null : row.StateKey,
        compiledNode = row.Authored?.CompiledNode, sourceGroup = row.SourceGroup,
        sourceNode = row.Node == null ? (int?)null : row.Owner.Identity(row.Index!.Value).SourceNode,
        model = row.Node?.ModelIndex, storedParents = row.Node?.Parents.Take(32).ToArray(), storedParentCount = row.Node?.Parents.Length,
        pass = row.Pass?.Kind, passDescription = row.Pass?.Description, animations = row.Animations,
        animationUses = row.AnimationUses?.Uses.Select(u => new { entryIndex = u.EntryIndex, name = u.Name, origin = u.Origin, reason = u.Reason }).ToArray(), animationUsesTruncated = row.AnimationUses?.Truncated,
        animationHasInitialRoot = row.AnimationUses?.HasInitialRoot, animationHasIndirect = row.AnimationUses?.HasIndirect,
        category = row.IsSourceMap ? row.Category : null, zone = row.IsSourceMap ? row.Zone : null, hidden = row.IsSourceMap ? row.IsHidden : (bool?)null, isolated = row.IsSourceMap && row.IsIsolated ? true : (bool?)null,
        difficulties = row.Placement is { Difficulties.Count: > 0 } placement ? placement.Difficulties.Select(d => d.ToString()).ToArray() : null,
        pickupType = row.Placement != null ? row.PickupType : null, pickupKind = row.Placement != null ? row.PickupKind : null, pickupGroup = row.Placement != null ? row.Pickup?.Group ?? (row.Parent?.Section is PickupGroupEntry or PickupTypeEntry ? "other" : null) : null,
        pickupNumber = row.PickupName != null ? row.Placement!.Number : (int?)null, pickupShortName = row.PickupName != null ? row.Pickup!.ShortName : null,
        pickupName = row.PickupName != null ? row.Pickup!.ManualName : null, instanceName = row.PickupName != null && !row.IsOtherDifficulty ? row.Placement!.Name : null,
        pickupTemplate = row.PickupName != null ? row.Pickup!.TemplateName : null,
        otherDifficulty = row.IsOtherDifficulty ? true : (bool?)null, placement = row.Placement is { Detail.Length: > 0 } summary ? summary.Detail : null,
        sectionRow = row.Section == null ? null : DescribeSectionRow(row), master = row.HasMasterEye ? MasterShown(row.MasterKey!) : (bool?)null,
        // The AI Vehicles row of the spawning actor in a source map (two records can place one root: one row); the node's row elsewhere.
        playerTank = row.Owner == sceneTree && sceneTreeDocument == shownDocument && ShownPlayerTank?.Actor.Root is int tankRoot
            && (row.IsSourceMap ? row.Placement is { Row: int placed } && placed == tankRoot : !row.Synthetic && row.Index == tankRoot) ? true : (bool?)null };
}
