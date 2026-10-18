using System.Globalization;

namespace zStudio.Desktop;

/// <summary>
/// Keeps a source map's open outliner rows across a rebuild that replaces the map's document (a rename, a copy, a zone
/// change…). Row identities include the content of their source files, so an edit changes them; the rows are found again by
/// a path of what does not change: each row's source file and glTF node (or its pass; rows without an authored node, such as
/// units and pickups, by their type and name), with its occurrence among equal siblings.
/// </summary>
public partial class MainWindow
{
    private sealed record TreeLayout(string Path, HashSet<string> Expanded, HashSet<string> Prefixes, string? Anchor);
    private TreeLayout? pendingTreeLayout;
    /// <summary>The row to scroll back to after the next rebuild (the renamed group), as a layout path.</summary>
    private string? pendingTreeAnchor;

    private static string LayoutKey(SceneTreeItem row) =>
        row.Pass != null ? row.Kind :
        row.Authored is { } authored ? "a:" + authored.File.ToUpperInvariant() + ":" + authored.SourceNode.ToString(CultureInfo.InvariantCulture) :
        row.Kind == "unlinked" ? "unlinked" : "n:" + row.NodeType + ":" + row.DisplayName;
    /// <summary>
    /// A hidden row's key: an authored row by its file and name (a delete renumbers the file's nodes, so an index could name
    /// another object); the occurrence among equally named siblings tells copies apart. A renamed object shows again.
    /// </summary>
    private static string HiddenKey(SceneTreeItem row) =>
        row.Authored is { } authored ? "a:" + authored.File.ToUpperInvariant() + ":" + row.DisplayName : LayoutKey(row);
    /// <summary>The row's layout path: each level's key with its occurrence among siblings that share it.</summary>
    private static string LayoutPath(SceneTreeItem row, Dictionary<SceneTreeItem, string>? paths = null) => RowPath(row, LayoutKey, paths ?? []);
    private static string HiddenPath(SceneTreeItem row, Dictionary<SceneTreeItem, string> paths) => RowPath(row, HiddenKey, paths);
    private static string RowPath(SceneTreeItem row, Func<SceneTreeItem, string> keyOf, Dictionary<SceneTreeItem, string> paths)
    {
        if (paths.TryGetValue(row, out var known)) return known;
        string parent = row.Parent == null ? "" : RowPath(row.Parent, keyOf, paths) + "/";
        // Each level's segments are worked out once, for all its rows (a world can have thousands of siblings).
        Dictionary<string, int> occurrences = [];
        foreach (var sibling in row.Parent?.Children ?? row.Owner.Roots)
        {
            string key = keyOf(sibling); int occurrence = occurrences.GetValueOrDefault(key); occurrences[key] = occurrence + 1;
            paths[sibling] = parent + key + "#" + occurrence.ToString(CultureInfo.InvariantCulture);
        }
        return paths[row];
    }

    /// <summary>Remembers the shown map's open rows before its document is replaced.</summary>
    private void CaptureTreeLayout()
    {
        string? anchor = pendingTreeAnchor; pendingTreeAnchor = null;
        if (shownDocument?.SourceWorld == null || sceneTreeDocument != shownDocument || sceneTree is not { Projection: not null, IsPreview: true } tree) return;
        HashSet<string> expanded = [], prefixes = [];
        Dictionary<SceneTreeItem, string> paths = [];
        foreach (var row in tree.CreatedRows)
        {
            if (!row.IsExpanded || expanded.Count >= 10_000) continue;
            string path = LayoutPath(row, paths);
            expanded.Add(path);
            for (int slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1)) prefixes.Add(path[..slash]);
        }
        pendingTreeLayout = new(shownDocument.Path, expanded, prefixes, anchor);
    }

    /// <summary>Opens again, in the rebuilt map's tree, the rows that were open; what no longer exists stays closed.</summary>
    private void RestoreTreeLayout(DocumentModel? doc)
    {
        if (pendingTreeLayout is not { } layout) return;
        if (doc == null || !string.Equals(layout.Path, doc.Path, StringComparison.OrdinalIgnoreCase)) { pendingTreeLayout = null; return; }
        if (sceneTree is not { Projection: not null, IsPreview: true } tree) return;
        pendingTreeLayout = null;
        SceneTreeItem? anchor = null;
        Stack<(SceneTreeItem Row, string Path)> pending = new();
        void Level(IReadOnlyList<SceneTreeItem> rows, string parent)
        {
            Dictionary<string, int> occurrences = [];
            foreach (var row in rows)
            {
                string key = LayoutKey(row); int occurrence = occurrences.GetValueOrDefault(key); occurrences[key] = occurrence + 1;
                pending.Push((row, (parent.Length == 0 ? "" : parent + "/") + key + "#" + occurrence.ToString(CultureInfo.InvariantCulture)));
            }
        }
        Level(tree.Roots, "");
        bool changing = changingSceneTree; changingSceneTree = true;
        try
        {
            while (pending.TryPop(out var current))
            {
                if (current.Path == layout.Anchor) anchor = current.Row;
                bool open = layout.Expanded.Contains(current.Path);
                if (open) current.Row.IsExpanded = true;
                // Rows inside a closed one can still have been open: their children are created only when needed.
                if (open || layout.Prefixes.Contains(current.Path) || layout.Anchor?.StartsWith(current.Path + "/", StringComparison.Ordinal) == true)
                    Level(current.Row.Children, current.Path);
            }
        }
        finally { changingSceneTree = changing; }
        if (anchor != null && tree.Selected == null) ScheduleSceneTreeReveal(anchor);
    }
}
