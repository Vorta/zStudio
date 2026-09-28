using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

internal sealed class SceneTreeState
{
    internal Dictionary<string, bool> Expanded { get; } = [];
    internal string? Selection { get; set; }
    internal string[] SelectionPath { get; set; } = [];
}

internal sealed record SceneTreeIdentity(string Key, int SourceNode, string? Placement);

internal sealed class SceneTreeModel
{
    internal string Context { get; } = Guid.NewGuid().ToString("N");
    internal SceneHierarchy Hierarchy { get; }
    internal SceneTreeState State { get; }
    internal string Source { get; }
    internal bool IsPreview { get; }
    internal SceneTreeItem? Selected { get; private set; }
    internal IReadOnlyList<SceneTreeItem> Roots { get; }
    private readonly Func<int, SceneTreeIdentity> identity;
    private readonly Dictionary<string, SceneTreeItem> rows = [];
    internal event Action<SceneTreeItem>? Collapsing;
    internal void OnCollapsing(SceneTreeItem row) => Collapsing?.Invoke(row);
    internal SceneTreeModel(GameScene scene, string source, bool isPreview, SceneTreeState state, Func<int, SceneTreeIdentity>? identity = null)
    {
        Hierarchy = new(scene); Source = source; IsPreview = isPreview; State = state;
        this.identity = identity ?? (index => new("node:" + index, index, null));
        var roots = Hierarchy.Roots.Select(i => New(i, null, "root", 0)).ToList();
        if (Hierarchy.UnlinkedRoots.Count > 0) roots.Add(New(null, null, "unlinked", 0));
        Roots = roots;
        var retainedPath = state.SelectionPath;
        IReadOnlyList<SceneTreeItem> level = Roots;
        foreach (string key in retainedPath)
        {
            var row = level.FirstOrDefault(r => r.StateKey == key);
            if (row == null) break;
            if (key == retainedPath[^1] && row.Node != null && row.Problem == null) Select(row);
            else level = row.Children;
        }
    }
    internal SceneTreeIdentity Identity(int node) => identity(node);
    internal SceneTreeItem New(int? node, SceneTreeItem? parent, string kind, int slot)
    {
        var row = new SceneTreeItem(this, node, parent, kind, slot);
        rows.Add(row.Id, row);
        if (row.StateKey == State.Selection && row.Node != null) Select(row);
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
        if (Selected?.Index == node) { ExpandAncestors(Selected); return Selected; }
        var path = Hierarchy.PathTo(node);
        if (path.Count == 0) return null;
        int unlinkedDepth = Hierarchy.UnlinkedRoots.Contains(path[0]) ? 1 : 0;
        int depth = path.Count - 1 + unlinkedDepth;
        if (depth > SceneHierarchy.MaximumDepth || depth == SceneHierarchy.MaximumDepth && Hierarchy.Children(node).Count > 0) return null;
        var root = Roots.FirstOrDefault(r => r.Index == path[0]);
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
    public int Depth { get; }
    public bool Shared => Index is int i && Node != null && Owner.Hierarchy.Parents(i).Count > 1;
    public int ChildCount => Problem != null ? 0 : Kind == "unlinked" ? Owner.Hierarchy.UnlinkedRoots.Count :
        Index is int i && Node != null ? Owner.Hierarchy.Children(i).Count : 0;
    public string Label => Kind == "unlinked" ? $"Unlinked nodes · {ChildCount}" : Node is { } n ?
        $"{Short(n.Name, 180)} · {n.Class} #{n.Index}" + (ChildCount > 0 ? $" · {ChildCount} {(ChildCount == 1 ? "child" : "children")}" : "") +
        (Shared ? " · shared" : "") + (Kind == "world partition" ? " · partition reference" : "") + (Problem == null ? "" : " · " + Problem) : $"Missing node #{Index}";
    public string Glyph => Problem != null || Node == null && Kind != "unlinked" ? "!" : Kind == "unlinked" ? "◇" : Shared ? "↗" : "▧";
    public string ToolTip => Node is { } node ? $"{Short(node.Name, 2048)} · {node.Class} #{node.Index}\n" +
        $"Tree parent: {(Parent?.Node is { } p ? Short(p.Name, 200) + " #" + p.Index : "none")} · {Kind}\n" +
        $"Stored parents: {string.Join(", ", node.Parents.Take(32))}{(node.Parents.Length > 32 ? " …" : "")}\n" +
        $"Source node: #{Owner.Identity(node.Index).SourceNode} · model: {node.ModelIndex?.ToString() ?? "none"}\n" +
        Short(Owner.Source, 2048) + (Owner.Identity(node.Index).Placement is { } placement ? "\n" + Short(placement, 2048) : "") +
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
        if (index != null && Node == null) Problem = "invalid reference";
        for (var ancestor = parent; ancestor != null; ancestor = ancestor.Parent)
            if (index != null && ancestor.Index == index) { Problem = "cycle reference"; break; }
        if (Depth >= SceneHierarchy.MaximumDepth && ChildCount > 0) Problem = "hierarchy depth limit";
        // Include sibling occurrence, never display names, in path identity.
        StateKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((parent?.StateKey ?? "root") + "/" +
            (Node == null ? kind + ":" + index : owner.Identity(index!.Value).Key) + "/" + kind + "/" + slot)));
        isExpanded = owner.State.Expanded.GetValueOrDefault(StateKey, parent == null && Node?.Class == "world");
    }
    public IReadOnlyList<SceneTreeItem> Children => children ??= CreateChildren();
    private IReadOnlyList<SceneTreeItem> CreateChildren()
    {
        if (Problem != null) return [];
        if (Kind == "unlinked") return Owner.Hierarchy.UnlinkedRoots.Select(i => Owner.New(i, this, "unlinked root", 0)).ToArray();
        if (Node is not { } node) return [];
        Dictionary<(int, string), int> occurrences = [];
        return Owner.Hierarchy.Children(node.Index).Select(edge =>
        {
            var key = (edge.Node, edge.Kind); int occurrence = occurrences.GetValueOrDefault(key); occurrences[key] = occurrence + 1;
            return Owner.New(edge.Node, this, edge.Kind, occurrence);
        }).ToArray();
    }
    private static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "…";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
