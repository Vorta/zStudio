using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private SceneTreeModel? sceneTree;
    private DocumentModel? sceneTreeDocument;
    private bool changingSceneTree;
    private string? treeInspectionTarget;
    private int sceneTreeRevealGeneration;
    private readonly HashSet<SceneTreeItem> collapsingSceneRows = [];
    private readonly ConditionalWeakTable<DocumentModel, Dictionary<string, SceneTreeState>> sceneTreeStates = new();
    private SceneViewport? TreeViewport => EmptyPreview.Visibility == Visibility.Collapsed ?
        animation?.Viewport ?? (SceneHost.Visibility == Visibility.Visible ? scene : null) : null;

    private void RefreshSceneTree()
    {
        if (changingSceneTree) return;
        var doc = ViewModel.SelectedDocument;
        var viewport = doc == shownDocument ? TreeViewport : null;
        var data = viewport?.PreviewScene ?? doc?.PreviewDocument.Scene;
        bool active = data != null && viewport?.PreviewScene == data;
        if (doc?.IsDisposed == true) { doc = null; data = null; }
        if (sceneTreeDocument == doc && sceneTree?.Hierarchy.Scene == data && sceneTree?.IsPreview == active) return;
        changingSceneTree = true;
        try
        {
            if (sceneTree != null) sceneTree.Collapsing -= SceneTreeCollapsing;
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
                sceneTree = new(data, source, true, state, TreeIdentities(data, viewport.Mission, doc.PickupEdits));
            }
            if (sceneTree != null) sceneTree.Collapsing += SceneTreeCollapsing;
            DocumentSceneTree.ItemsSource = sceneTree?.Roots;
            // Renderer replacement clears its framing identity. Restore it from
            // the tree's provenance match, together with source inspection, without
            // selecting a runtime copy, moving the camera or resolving drafts.
            if (sceneTree?.Selected is { Node: { } retained } selected)
            {
                inspectedSceneSource = selected;
                selectedNode = active ? retained.Index : null;
                SetProperties(SceneTreeProperties(selected));
                if (active && viewport!.SelectedInspection == null && viewport.SelectedAiNode == null)
                    viewport.SelectFramingNode(retained.Index);
            }
        }
        finally { changingSceneTree = false; }
        UpdateNavigatorAvailability();
        ScheduleSceneTreeReveal();
    }

    private static Func<int, SceneTreeIdentity> TreeIdentities(GameScene data, MissionSceneContext? mission, PickupPlacementEditSession? edits)
    {
        if (mission == null) return i => new("source:" + i, i, null);
        Dictionary<int, MissionActor?> actors = [];
        foreach (var actor in mission.Actors)
        {
            Stack<int> pending = new([actor.Root]); HashSet<int> seen = [];
            while (pending.TryPop(out int node))
            {
                if (node < 0 || node >= data.Nodes.Count || !seen.Add(node)) continue;
                if (!actors.TryAdd(node, actor)) actors[node] = null; // Ambiguous provenance cannot retain selection.
                foreach (int child in SceneBuilder.Children(data.Nodes[node])) pending.Push(child);
            }
        }
        string ambiguous = Guid.NewGuid().ToString("N");
        Dictionary<MissionPickupSource, string> sources = [];
        return index =>
        {
            int sourceNode = index < mission.SourceNodes.Count ? mission.SourceNodes[index] : index;
            if (!actors.TryGetValue(index, out var actor)) return new("source:" + sourceNode, sourceNode, null);
            if (actor == null) return new(ambiguous + ":" + index, sourceNode, "Ambiguous mission instance");
            var source = actor.Pickup?.Source ?? actor.CoordinateSource;
            string identity;
            if (source != null)
            {
                if (!sources.TryGetValue(source, out identity!))
                {
                    var matches = edits != null && (edits.Find(source) != null || edits.Coordinate(source) != null) ? edits.Scope(source).Sources : [source];
                    identity = string.Join("|", matches.Select(s => $"{s.ArchivePath}:{s.AssetIndex}:{s.RecordIndex}").Order(StringComparer.Ordinal));
                    sources[source] = identity;
                }
            }
            else identity = mission.Actors.Count(a => a.SourceRoot == actor.SourceRoot && a.Name == actor.Name) == 1
                ? "actor:" + actor.SourceRoot + ":" + actor.Name : ambiguous + ":" + index;
            return new(identity + ":source:" + sourceNode, sourceNode, actor.Name + " · " + actor.PlacementSource);
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
            RevealSceneNode(selected.Node);
            // Retain source-inspection intent, but never the previously selected
            // row. The existing Properties window itself remains independently pinned.
            if (inspectingSource) inspectedSceneSource = sceneTree?.Selected?.Index == selected.Node ? sceneTree.Selected : null;
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
        if (row.Node == null || row.Problem != null) { Restore(); return; }
        if (!ResolvePropertiesDrafts(sceneTreeDocument) || animation?.ResolvePendingDrafts() == false) { Restore(); return; }
        try { SelectSceneTreeRow(row); }
        catch (StudioCommandException ex) { ViewModel.Status = ex.Message; Restore(); }
        void Restore()
        {
            changingSceneTree = true;
            try { row.IsSelected = false; sceneTree?.Select(previous); }
            finally { changingSceneTree = false; }
        }
    }
    private void SelectSceneTreeRow(SceneTreeItem row)
    {
        if (row.Owner != sceneTree || row.Node is not { } node || row.Problem != null || sceneTreeDocument is not { IsDisposed: false } doc)
            throw new StudioCommandException("stale_record", "The tree node is no longer available.");
        var viewport = sceneTree.IsPreview ? TreeViewport : null;
        if (viewport?.IsPickupDragging == true || viewport?.IsFlyActive == true) throw new StudioCommandException("busy", "Finish the pickup drag or exit Fly before selecting a node.");
        changingSceneTree = true;
        try
        {
            if (viewport != null)
            {
                // Revealing a runtime card never comes through here. An intentional
                // source selection may choose a visible pose; an existing exact copy is retained.
                if (viewport.SelectedInspection?.Node != node.Index && !viewport.SelectInspectionNode(node.Index)) viewport.SelectInspection(null, false);
                if (animation == null) InspectNode(node.Index);
                selectedNode = node.Index; viewport.SelectFramingNode(node.Index);
            }
            else selectedNode = null;
            SetProperties(SceneTreeProperties(row));
            inspectedSceneSource = row;
            sceneTree.Select(row); SceneTreeModel.ExpandAncestors(row);
            ViewModel.Status = $"Selected node #{node.Index}: {node.Name}";
        }
        finally { changingSceneTree = false; }
        ScheduleSceneTreeReveal();
    }
    private JsonObject SceneTreeProperties(SceneTreeItem row)
    {
        var node = row.Node!; var properties = (JsonObject)node.Metadata.DeepClone();
        properties["node_index"] = node.Index; properties["source_node_index"] = row.Owner.Identity(node.Index).SourceNode;
        properties["scene_source"] = row.Owner.Source;
        properties["hierarchy"] = row.Owner.IsPreview ? "Bound preview scene (not runtime parenting)" : "Stored document scene";
        properties["parent_indices"] = JsonData.Integers(node.Parents);
        properties["child_indices"] = JsonData.Integers(node.Children);
        return properties;
    }
    private void ScheduleSceneTreeReveal(SceneTreeItem? reveal = null)
    {
        var model = sceneTree; var selected = reveal ?? model?.Selected;
        int generation = ++sceneTreeRevealGeneration;
        if (selected == null || !DocumentSceneTree.IsVisible) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (sceneTree != model || generation != sceneTreeRevealGeneration || !DocumentSceneTree.IsVisible) return;
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
                    if (item == selected) { if (model!.Selected == item) container.IsSelected = true; container.BringIntoView(); }
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
        Register(commands, "scene_tree", "Read the Document scene hierarchy, including stable preview placements, shared/partition references and disconnected nodes. Read returns roots or a row's direct children, optionally filtered before paging. Expand/collapse/reveal only change tree presentation; select shares scene inspection and draft guards. Runtime parenting is not represented.", true,
            [DocumentParameter, P("action", "string", "Tree action; default read. Properties opens the same pinned window as the row context menu.", false, "read", "expand", "collapse", "reveal", "select", "properties"),
                P("context", "string", "Current hierarchy context from read; required for all actions and row queries."),
                P("preview", "string", "Current preview ID from read; required for actions on a preview hierarchy."),
                P("row", "string", "Opaque row ID for child queries or actions."), new("node", "integer", "Scene node index for reveal/select/properties instead of row.", Minimum: 0, Maximum: int.MaxValue), .. PageParameters], async (a, token) =>
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
                    RequireNoDrafts(action == "properties" ? null : doc);
                    if (sceneTree.IsPreview && TreeViewport is { } viewport && (viewport.IsPickupDragging || viewport.IsFlyActive))
                        throw new StudioCommandException("busy", "Finish the pickup drag or exit Fly before selecting a node.");
                }
                if (a["node"] != null)
                {
                    if (action is not ("select" or "reveal" or "properties")) throw new StudioCommandException("invalid_argument", "Expand/collapse requires a row.");
                    changingSceneTree = true;
                    try { row = sceneTree.Reveal(Int(a, "node")); }
                    finally { changingSceneTree = false; }
                }
                if (row == null) throw new StudioCommandException("stale_record", "Supply an available tree row or node.");
                if (action == "properties")
                {
                    var model = sceneTree;
                    var opened = await OpenScenePropertiesAsync(doc, row, token, automation: true);
                    if (sceneTree != model || opened == null) throw new StudioCommandException("context_changed", "The requested hierarchy Properties target was not published.");
                }
                else if (action == "select") SelectSceneTreeRow(row);
                else if (action == "expand") row.IsExpanded = true;
                else if (action == "collapse") row.IsExpanded = false;
                else { SceneTreeModel.ExpandAncestors(row); }
                if (action == "reveal") ScheduleSceneTreeReveal(row);
            }
            else if (a["node"] != null) throw new StudioCommandException("invalid_argument", "Use node with reveal/select.");
            var page = Page(row == null ? sceneTree.Roots : row.Children, a, r => r.Label, r => DescribeTreeRow(r));
            return Result(new { context = sceneTree.Context, document = doc.SessionId, revision = doc.Revision,
                preview = sceneTree.IsPreview ? previewId.ToString() : null, source = sceneTree.Source,
                hierarchy = sceneTree.IsPreview ? "bound preview scene" : "stored scene", selected = sceneTree.Selected?.Id,
                row = row == null ? null : DescribeTreeRow(row), children = page.Data });
        });
    }
    private static object DescribeTreeRow(SceneTreeItem row) => new { row = row.Id, node = row.Index, row.Label,
        parent = row.Parent?.Id, relation = row.Kind, row.Shared, row.Problem, row.ChildCount, row.IsExpanded, row.IsSelected,
        sourceNode = row.Node == null ? (int?)null : row.Owner.Identity(row.Index!.Value).SourceNode,
        model = row.Node?.ModelIndex, storedParents = row.Node?.Parents.Take(32).ToArray(), storedParentCount = row.Node?.Parents.Length };
}
