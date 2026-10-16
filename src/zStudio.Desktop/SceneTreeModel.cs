using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using zStudio.Core;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

internal sealed class SceneTreeState
{
    internal Dictionary<string, bool> Expanded { get; } = [];
    internal string? Selection { get; set; }
    internal string[] SelectionPath { get; set; } = [];
}

internal sealed record SceneTreeIdentity(string Key, int SourceNode, string? Placement, string? ActorName = null)
{
    internal string? PlacementPreview()
    {
        if (Placement == null) return null;
        if (ActorName == null) return Clip(Placement, 2048);
        const string separator = " · ";
        if (ActorName.Length >= 2048) return ActorName[..2048] + "…";
        int room = 2048 - ActorName.Length;
        if (room < separator.Length) return ActorName + separator[..room] + "…";
        room -= separator.Length;
        return ActorName + separator + Clip(Placement, room);
    }
    private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…";
}

internal sealed class SceneTreeModel
{
    internal string Context { get; } = Guid.NewGuid().ToString("N");
    internal SceneHierarchy Hierarchy { get; }
    internal SceneTreeState State { get; }
    internal string Source { get; }
    internal bool IsPreview { get; }
    internal SourceMapSelection? Projection { get; }
    internal SceneTreeItem? Selected { get; private set; }
    internal IReadOnlyList<SceneTreeItem> Roots { get; }
    private readonly Func<int, SceneTreeIdentity> identity;
    private readonly Dictionary<string, SceneTreeItem> rows = [];
    internal event Action<SceneTreeItem>? Collapsing;
    /// <summary>Whether a node is hidden in the view (the outliner's eye); null outside source maps.</summary>
    internal Func<int, bool>? IsHiddenNode { get; set; }
    /// <summary>Every row created so far, for refreshing their hidden state.</summary>
    internal IEnumerable<SceneTreeItem> CreatedRows => rows.Values;
    internal void OnCollapsing(SceneTreeItem row) => Collapsing?.Invoke(row);
    internal SceneTreeModel(GameScene scene, string source, bool isPreview, SceneTreeState state, Func<int, SceneTreeIdentity>? identity = null, IReadOnlySet<int>? included = null, SourceMapSelection? projection = null)
    {
        Hierarchy = new(scene, included: included); Source = source; IsPreview = isPreview; State = state;
        Projection = projection;
        this.identity = projection == null ? identity ?? (index => new("node:" + index, index, null))
            : index => new(projection.Identities.GetValueOrDefault(index) ?? "node:" + index, index >= 0 ? identity?.Invoke(index).SourceNode ?? index : -1,
                projection.AuthoredNodes.GetValueOrDefault(index)?.File ?? projection.Boundaries.GetValueOrDefault(index)?.Source);
        var roots = (projection?.Roots ?? Hierarchy.Roots).Select(i => New(i, null, "root", 0)).ToList();
        if (projection == null && Hierarchy.UnlinkedRoots.Count > 0) roots.Add(New(null, null, "unlinked", 0));
        // A source map's later passes follow the world as their own trees.
        foreach (var pass in projection?.Passes ?? []) roots.Add(New(null, null, "pass:" + pass.Kind, 0));
        Roots = roots;
        var retainedPath = state.SelectionPath;
        IReadOnlyList<SceneTreeItem> level = Roots;
        foreach (string key in retainedPath)
        {
            var row = level.FirstOrDefault(r => r.StateKey == key);
            if (row == null) break;
            if (key == retainedPath[^1] && row.Selectable) Select(row);
            else level = row.Children;
        }
    }
    internal SceneTreeIdentity Identity(int node) => identity(node);
    internal IReadOnlyList<SceneHierarchyEdge> Children(int node) => Projection?.Children(node) ?? Hierarchy.Children(node);
    internal SceneTreeItem New(int? node, SceneTreeItem? parent, string kind, int slot)
    {
        var row = new SceneTreeItem(this, node, parent, kind, slot);
        rows.Add(row.Id, row);
        if (row.StateKey == State.Selection && row.Selectable) Select(row);
        return row;
    }
    internal SceneTreeItem? Find(string id) => rows.GetValueOrDefault(id);
    internal void Select(SceneTreeItem? row)
    {
        if (Selected != row) { var previous = Selected; Selected = row; previous?.SetSelected(false); }
        row?.SetSelected(true);
        State.Selection = row?.StateKey;
        Stack<string> path = new(); for (var current = row; current != null; current = current.Parent) path.Push(current.StateKey);
        State.SelectionPath = path.ToArray();
    }
    internal SceneTreeItem? Reveal(int node)
    {
        node = Projection?.Owner(node) ?? node;
        if (Selected?.Index == node) { ExpandAncestors(Selected); return Selected; }
        var path = Projection?.Path(node) ?? Hierarchy.PathTo(node);
        if (path.Count == 0) return null;
        int unlinkedDepth = Projection == null && Hierarchy.UnlinkedRoots.Contains(path[0]) || Projection?.PassOf(path[0]) != null ? 1 : 0;
        int depth = path.Count - 1 + unlinkedDepth;
        if (depth > SceneHierarchy.MaximumDepth || depth == SceneHierarchy.MaximumDepth && Children(node).Count > 0) return null;
        var root = Roots.FirstOrDefault(r => r.Index == path[0]);
        if (root == null && Projection?.PassOf(path[0]) is { } pass && Roots.FirstOrDefault(r => r.Kind == "pass:" + pass.Kind) is { } group)
        { group.IsExpanded = true; root = group.Children.FirstOrDefault(r => r.Index == path[0]); }
        if (root == null && Roots.FirstOrDefault(r => r.Kind == "unlinked") is { } unlinked)
        { unlinked.IsExpanded = true; root = unlinked.Children.FirstOrDefault(r => r.Index == path[0]); }
        if (root == null) return null;
        var current = root;
        foreach (int child in path.Skip(1))
        {
            current.IsExpanded = true;
            var next = current.Children.FirstOrDefault(r => r.Index == child && r.Problem == null);
            if (next == null) return null;
            current = next;
        }
        ExpandAncestors(current); return current;
    }
    internal static void ExpandAncestors(SceneTreeItem row)
    { for (var parent = row.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true; }
}

public sealed class SceneTreeItem : INotifyPropertyChanged
{
    internal SceneTreeModel Owner { get; }
    internal SceneTreeItem? Parent { get; }
    internal string StateKey { get; }
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public int? Index { get; }
    public string Kind { get; }
    public string? Problem { get; }
    public GameNode? Node => Index is int i && i >= 0 && i < Owner.Hierarchy.Scene.Nodes.Count ? Owner.Hierarchy.Scene.Nodes[i] : null;
    public bool SourceGroup => Index is int i && Owner.Projection?.IsSourceGroup(i) == true;
    /// <summary>A source map's later pass (aiv, puppies, anim) shown as a top-level tree of its own.</summary>
    internal SourceMapPass? Pass => Kind.StartsWith("pass:", StringComparison.Ordinal) ? Owner.Projection?.Passes.FirstOrDefault(p => "pass:" + p.Kind == Kind) : null;
    /// <summary>The mission animations bound to this object.</summary>
    public IReadOnlyList<string>? Animations => Index is int i ? Owner.Projection?.Animations.GetValueOrDefault(i) : null;
    public SourceSelectionNode? Authored => Index is int i ? Owner.Projection?.AuthoredNodes.GetValueOrDefault(i) : null;
    public bool Synthetic => SourceGroup || Index is int i && Owner.Projection?.Boundaries.GetValueOrDefault(i)?.Synthetic == true;
    public bool Selectable => Problem == null && (Node != null || Synthetic);
    public bool ReferencedObject => Index is int i && Owner.Projection?.IsBoundary(i) == true;
    public string DisplayName => Owner.Projection != null && Index is int i ? Owner.Projection.DisplayName(i) : Node?.Name ?? "Missing node";
    public string? ReadOnlyReason => SourceGroup ? "This authored group is removed during compilation. Its hierarchy can be inspected and it can be renamed (right-click); other group edits are unavailable."
        : Index is int i ? Owner.Projection?.Boundaries.GetValueOrDefault(i)?.ReadOnlyReason : null;
    public string NodeType => Pass != null ? "pass" : SourceGroup ? "group" : ReferencedObject ? "reference" : Node?.Class switch
    { "object3d" when Node.ModelIndex != null => "mesh", "object3d" => "group", { } kind => kind, _ => Kind };
    public int Depth { get; }
    public bool Shared => Index is int i && (Owner.Projection?.ParentCount(i) ?? (Node != null ? Owner.Hierarchy.Parents(i).Count : 0)) > 1;
    public int ChildCount => Problem != null ? 0 : Kind == "unlinked" ? Owner.Hierarchy.UnlinkedRoots.Count : Pass is { } pass ? pass.Roots.Count :
        Index is int i && (Node != null || Synthetic) ? Owner.Children(i).Count : 0;
    public string Label => Pass is { } pass ? $"{pass.Label} · {pass.Roots.Count}" : Owner.Projection != null && Selectable ? Short(DisplayName, 180) + (Animations != null ? " ⟳" : "") : Kind == "unlinked" ? $"Unlinked nodes · {ChildCount}" : Node is { } n ?
        $"{Short(n.Name, 180)} · {n.Class} #{n.Index}" + (ChildCount > 0 ? $" · {ChildCount} {(ChildCount == 1 ? "child" : "children")}" : "") +
        (Shared ? " · shared" : "") + (Kind == "world partition" ? " · partition reference" : "") + (Problem == null ? "" : " · " + Problem) : $"Missing node #{Index}";
    public string Glyph => Pass != null ? "◫" : Problem != null || !Selectable && Kind != "unlinked" ? "!" : Kind == "unlinked" ? "◇" : NodeType switch
    { "reference" => "⬡", "world" => "◎", "lod" => "▱", "camera" => "▣", "light" => "☼", "mesh" => "△", "group" => "▰", _ => Shared ? "↗" : "▧" };
    /// <summary>A source map's outliner shows its own row: type icon and colour, name, zone and hide eye.</summary>
    public bool IsSourceMap => Owner.Projection != null;
    /// <summary>The outliner's type: world, group, geometry, object (a placed model) or node (lights, cameras, LOD…).</summary>
    public string Category => Pass != null || Kind == "unlinked" ? "group" : Problem != null ? "node" : NodeType switch
    { "world" => "world", "group" or "lod" => "group", "mesh" => "geometry", "reference" => "object", _ => "node" };
    public string OutlinerIcon => OutlinerKinds.Icon(Category);
    public System.Windows.Media.Brush OutlinerBrush => OutlinerKinds.Brush(Category);
    /// <summary>The name alone: groups show no counts.</summary>
    public string OutlinerLabel => Pass is { } pass ? pass.Label : Kind == "unlinked" ? "Unlinked nodes" : Problem != null ? Label : Short(DisplayName, 180) + (Animations != null ? " ⟳" : "");
    /// <summary>An object's zone, at the row's right.</summary>
    public string Meta => Category == "object" && Node?.Metadata["zone_id"] is System.Text.Json.Nodes.JsonValue zone && ZoneWord(zone) is long word
        ? "Zone " + ((word & 0xFF) == 0xFF ? "Any" : (word & 0xFF).ToString(System.Globalization.CultureInfo.InvariantCulture)) : "";
    private static long? ZoneWord(System.Text.Json.Nodes.JsonValue value) =>
        value.TryGetValue<uint>(out uint word) ? word : value.TryGetValue<int>(out int signed) ? signed : value.TryGetValue<long>(out long wide) ? wide : null;
    public bool CanHide => IsSourceMap && Index is int && Problem == null;
    public bool IsHidden => Index is int i && Owner.IsHiddenNode?.Invoke(i) == true;
    public string EyeIcon => IsHidden ? "EyeOff" : "Eye";
    public string EyeToolTip => IsHidden ? "Show in viewport" : "Hide in viewport";
    internal void NotifyHidden() { Changed(nameof(IsHidden)); Changed(nameof(EyeIcon)); Changed(nameof(EyeToolTip)); }
    public string ToolTip => (Pass is { } pass ? pass.Description : ToolTipCore) + (Animations is { } animations ? "\nAnimations: " + string.Join(", ", animations.Select(a => Short(a, 64))) : "");
    private string ToolTipCore => Authored is { } authored ? $"{Short(DisplayName, 2048)} · {NodeType}\nSource: {Short(authored.File, 2048)} · glTF node #{authored.SourceNode}\n" +
        $"Tree parent: {(Parent == null ? "none" : Short(Parent.DisplayName, 200))}\nCompiled node: {authored.CompiledNode?.ToString() ?? "none"}" + (ReadOnlyReason == null ? "" : "\n" + ReadOnlyReason)
        : Synthetic ? $"{Short(DisplayName, 2048)} · referenced source group\n{ReadOnlyReason}" : Node is { } node ? $"{Short(node.Name, 2048)} · {(ReferencedObject ? "referenced object" : node.Class)} #{node.Index}\n" +
        $"Tree parent: {(Parent?.Node is { } p ? Short(p.Name, 200) + " #" + p.Index : "none")} · {Kind}\n" +
        $"Stored parents: {string.Join(", ", node.Parents.Take(32))}{(node.Parents.Length > 32 ? " …" : "")}\n" +
        $"Source node: #{Owner.Identity(node.Index).SourceNode} · model: {node.ModelIndex?.ToString() ?? "none"}\n" +
        Short(Owner.Source, 2048) + (Owner.Identity(node.Index).PlacementPreview() is { } placement ? "\n" + placement : "") +
        (Problem == null ? "" : "\n" + Problem) : Kind == "unlinked" ? "Nodes outside the world roots, including disconnected cyclic components." : $"Invalid scene reference #{Index}.";
    private bool isExpanded, isSelected;
    private IReadOnlyList<SceneTreeItem>? children;
    public bool IsExpanded
    {
        get => isExpanded;
        set { if (isExpanded == value) return; isExpanded = value; Owner.State.Expanded[StateKey] = value; if (!value) Owner.OnCollapsing(this); Changed(nameof(IsExpanded)); }
    }
    public bool IsSelected { get => isSelected; set { if (isSelected != value) { isSelected = value; Changed(nameof(IsSelected)); } } }
    internal void SetSelected(bool value) => IsSelected = value;
    internal SceneTreeItem(SceneTreeModel owner, int? index, SceneTreeItem? parent, string kind, int slot)
    {
        Owner = owner; Index = index; Parent = parent; Kind = kind; Depth = (parent?.Depth ?? -1) + 1;
        if (index != null && Node == null && !Synthetic) Problem = "invalid reference";
        for (var ancestor = parent; ancestor != null; ancestor = ancestor.Parent)
            if (index != null && ancestor.Index == index) { Problem = "cycle reference"; break; }
        if (Depth >= SceneHierarchy.MaximumDepth && ChildCount > 0) Problem = "hierarchy depth limit";
        // Include sibling occurrence, never display names, in path identity.
        StateKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((parent?.StateKey ?? "root") + "/" +
            (Node == null && !Synthetic ? kind + ":" + index : owner.Identity(index!.Value).Key) + "/" + kind + "/" + slot)));
        isExpanded = owner.State.Expanded.GetValueOrDefault(StateKey, parent == null && Node?.Class == "world");
    }
    public IReadOnlyList<SceneTreeItem> Children => children ??= CreateChildren();
    private IReadOnlyList<SceneTreeItem> CreateChildren()
    {
        if (Problem != null) return [];
        if (Kind == "unlinked") return Owner.Hierarchy.UnlinkedRoots.Select(i => Owner.New(i, this, "unlinked root", 0)).ToArray();
        if (Pass is { } pass) return pass.Roots.Select(i => Owner.New(i, this, pass.Kind, 0)).ToArray();
        if (Index is not int node || !Selectable) return [];
        Dictionary<(int, string), int> occurrences = [];
        return Owner.Children(node).Select(edge =>
        {
            var key = (edge.Node, edge.Kind); int occurrence = occurrences.GetValueOrDefault(key); occurrences[key] = occurrence + 1;
            return Owner.New(edge.Node, this, edge.Kind, occurrence);
        }).ToArray();
    }
    private static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "…";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
