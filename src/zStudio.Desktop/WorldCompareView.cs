using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Windows.Media;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// Two worlds compared as the Compare worlds window shows them: the merged node tree as rows, the window's filter, and
/// the selected row. MCP reads the same rows with filters of its own.
/// </summary>
internal sealed class WorldCompareView
{
    public string Context { get; } = Guid.NewGuid().ToString("N");
    public string RetailPath { get; }
    public string RebuiltPath { get; }
    public WorldComparison Comparison { get; }
    public int RetailNodes { get; }
    public int RebuiltNodes { get; }
    /// <summary>The worlds' file versions: 15, or 13 for a 1998 demo world.</summary>
    public uint RetailVersion { get; init; } = 15;
    public uint RebuiltVersion { get; init; } = 15;
    public IReadOnlyList<WorldCompareRow> Roots { get; }
    public WorldCompareRow? Selected { get; private set; }
    /// <summary>The window's filter: only rows that differ or hold differences, and a name filter.</summary>
    public bool DifferencesOnly { get; private set; }
    public string Query { get; private set; } = "";
    public event Action? FilterChanged;
    /// <summary>The most rows a name filter opens to show its matches.</summary>
    private const int MaximumOpened = 2000;
    private readonly Dictionary<string, WorldCompareRow> rows = [];
    private readonly Dictionary<WorldComparisonNode, int> notableBelow = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, HashSet<WorldComparisonNode>> queryHits = new(StringComparer.OrdinalIgnoreCase);

    public WorldCompareView(string retailPath, string rebuiltPath, WorldComparison comparison, int retailNodes, int rebuiltNodes)
    {
        RetailPath = retailPath; RebuiltPath = rebuiltPath; Comparison = comparison; RetailNodes = retailNodes; RebuiltNodes = rebuiltNodes;
        int Count(WorldComparisonNode node)
        {
            int below = 0;
            foreach (var child in node.Children) below += Count(child) + (Notable(child) ? 1 : 0);
            notableBelow[node] = below;
            return below;
        }
        foreach (var root in comparison.Roots) Count(root);
        Roots = [.. comparison.Roots.Select(r => New(r, null))];
    }

    internal WorldCompareRow New(WorldComparisonNode source, WorldCompareRow? parent)
    {
        var row = new WorldCompareRow(this, source, parent);
        rows.Add(row.Id, row);
        return row;
    }
    public WorldCompareRow? Find(string id) => rows.GetValueOrDefault(id);
    internal IEnumerable<WorldCompareRow> Created => rows.Values;

    public static bool Notable(WorldComparisonNode node) => node.Status != WorldComparisonStatus.Same || node.BindsElsewhere;
    public int NotableBelow(WorldComparisonNode node) => notableBelow.GetValueOrDefault(node);

    /// <summary>Whether a row passes a filter: it differs or holds differences, and its name or a name below it matches.</summary>
    public bool Passes(WorldCompareRow row, bool differencesOnly, string query) =>
        (!differencesOnly || Notable(row.Source) || NotableBelow(row.Source) > 0) && (query.Length == 0 || Hits(query).Contains(row.Source));
    private HashSet<WorldComparisonNode> Hits(string query)
    {
        if (queryHits.TryGetValue(query, out var hits)) return hits;
        hits = new(ReferenceEqualityComparer.Instance);
        bool Visit(WorldComparisonNode node)
        {
            bool any = node.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            foreach (var child in node.Children) any |= Visit(child);
            if (any) hits.Add(node);
            return any;
        }
        foreach (var root in Comparison.Roots) Visit(root);
        if (queryHits.Count > 16) queryHits.Clear();
        return queryHits[query] = hits;
    }

    public void SetFilter(bool differencesOnly, string query)
    {
        query = query.Trim();
        if (differencesOnly == DifferencesOnly && query == Query) return;
        DifferencesOnly = differencesOnly; Query = query;
        foreach (var row in Created) row.Refilter();
        // A name filter opens the rows that hold matches below them, however deep, so they show (at most a few thousand rows,
        // nearest first).
        if (query.Length > 0)
        {
            Queue<WorldCompareRow> open = new(Roots.Where(r => r.IsVisibleInFilter));
            for (int opened = 0; opened < MaximumOpened && open.TryDequeue(out var row);)
                if (row.Visible.Count > 0 && !row.Source.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                { row.IsExpanded = true; opened++; foreach (var child in row.Visible) open.Enqueue(child); }
        }
        FilterChanged?.Invoke();
    }
    public IReadOnlyList<WorldCompareRow> VisibleRoots => [.. Roots.Where(r => Passes(r, DifferencesOnly, Query))];

    public void Select(WorldCompareRow? row)
    {
        if (Selected == row) return;
        var previous = Selected; Selected = row;
        previous?.SetSelected(false); row?.SetSelected(true);
        Selection?.Invoke();
    }
    public event Action? Selection;

    public int? RetailSlot(WorldComparisonNode node) => node.Expected is { } n && Comparison.ExpectedSlots.TryGetValue(n, out int s) ? s : null;
    public int? RebuiltSlot(WorldComparisonNode node) => node.Actual is { } n && Comparison.ActualSlots.TryGetValue(n, out int s) ? s : null;

    public static string Status(WorldComparisonNode node) => node.Status switch
    {
        WorldComparisonStatus.Same => "same",
        WorldComparisonStatus.Changed => "changed",
        WorldComparisonStatus.OnlyExpected => "only retail",
        _ => "only rebuilt",
    };

    /// <summary>One line per compared property of a row, retail and rebuilt side by side, then any other difference.</summary>
    public IReadOnlyList<WorldCompareDetail> Details(WorldCompareRow row)
    {
        var node = row.Source; var a = node.Expected; var b = node.Actual;
        List<WorldCompareDetail> details = [];
        HashSet<string> fields = [.. node.Differences.Select(d => d.Field)];
        // A property differs as the comparison found it: by its fields, not by how the values read. Slots and the order of
        // parents and children are shown but never differences in themselves.
        void Add(string field, string? retail, string? rebuilt, params string[] differenceFields) =>
            details.Add(new(field, retail ?? "—", rebuilt ?? "—", a != null && b != null && differenceFields.Any(fields.Contains)));
        Add("Slot", RetailSlot(node)?.ToString(CultureInfo.InvariantCulture), RebuiltSlot(node)?.ToString(CultureInfo.InvariantCulture));
        Add("Class", a?.Class.ToString(), b?.Class.ToString(), "class");
        string? retailParents = a == null ? null : Names(a.Parents), rebuiltParents = b == null ? null : Names(b.Parents);
        details.Add(new("Parents", retailParents ?? "—", rebuiltParents ?? "—", a != null && b != null && !a.Parents.Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(b.Parents.Select(p => p.Name).Order(StringComparer.Ordinal))));
        Add("Children", a == null ? null : Names(a.Children), b == null ? null : Names(b.Children), "children");
        Add("Flags", a == null ? null : $"0x{a.Flags:X8}", b == null ? null : $"0x{b.Flags:X8}", "flags.carried", "flags.derived");
        Add("Zone", a == null ? null : (a.Zone & 0xFF).ToString(CultureInfo.InvariantCulture), b == null ? null : (b.Zone & 0xFF).ToString(CultureInfo.InvariantCulture), "zone");
        Add("Grid cell", a == null ? null : $"{a.GridColumn}, {a.GridRow}", b == null ? null : $"{b.GridColumn}, {b.GridRow}", "cell");
        Add("Position", a == null ? null : Position(a), b == null ? null : Position(b), "matrix", "object.trs");
        Add("Model", a == null ? null : Model(a), b == null ? null : Model(b), "model", "model.mode", "model.points", "model.morphs", "model.sphere", "model.polygons");
        foreach (var difference in node.Differences)
            if (difference.Field is not ("class" or "children" or "flags.carried" or "flags.derived" or "zone" or "cell"))
                details.Add(new(difference.Field, Short(difference.Expected, 512), Short(difference.Actual, 512), true));
        return details;

        static string Names(IEnumerable<WorldNode> nodes) { var list = nodes.Take(13).Select(n => n.Name.Length == 0 ? "(unnamed)" : n.Name).ToList(); return list.Count == 0 ? "none" : string.Join(", ", list.Take(12)) + (list.Count > 12 ? ", …" : ""); }
        static string Position(WorldNode n) => n.Class == WorldNodeClass.Object3D && WorldUpdate.LocalMatrix(n) is { } m ? Format(m.Translation) : "—";
        static string Format(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}");
        static string Model(WorldNode n) => n.Model is { } m ? $"{m.Polygons.Count} polygons, {m.Vertices.Count} vertices" : "none";
    }
    public static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>A property of a compared node in both worlds.</summary>
internal sealed record WorldCompareDetail(string Field, string Retail, string Rebuilt, bool Differs);

/// <summary>A row of the merged node tree: a pair of nodes, or a node only one world has.</summary>
public sealed class WorldCompareRow : INotifyPropertyChanged
{
    private static readonly Brush ChangedBrush = Frozen(Color.FromRgb(0xE0, 0x8A, 0x1E)), RetailBrush = Frozen(Color.FromRgb(0xD9, 0x4A, 0x4A)),
        RebuiltBrush = Frozen(Color.FromRgb(0x3C, 0xA5, 0x5C)), SameBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    internal WorldCompareView Owner { get; }
    internal WorldComparisonNode Source { get; }
    internal WorldCompareRow? Parent { get; }
    public string Id { get; } = Guid.NewGuid().ToString("N");
    internal WorldCompareRow(WorldCompareView owner, WorldComparisonNode source, WorldCompareRow? parent)
    {
        Owner = owner; Source = source; Parent = parent;
        isExpanded = parent == null && source.Expected?.Class == WorldNodeClass.World;
    }
    public string Name => Source.Name.Length == 0 ? "(unnamed)" : WorldCompareView.Short(Source.Name, 180);
    public string Glyph => (Source.Status switch { WorldComparisonStatus.Same => "·", WorldComparisonStatus.Changed => "≠", WorldComparisonStatus.OnlyExpected => "−", _ => "+" }) + (Source.BindsElsewhere ? "⚑" : "");
    public Brush GlyphBrush => Source.Status switch { WorldComparisonStatus.Same => Source.BindsElsewhere ? ChangedBrush : SameBrush, WorldComparisonStatus.Changed => ChangedBrush, WorldComparisonStatus.OnlyExpected => RetailBrush, _ => RebuiltBrush };
    /// <summary>Class and slots, and how many differences lie below.</summary>
    public string Detail
    {
        get
        {
            var node = Source; string cls = (node.Expected ?? node.Actual)!.Class.ToString();
            int? a = Owner.RetailSlot(node), b = Owner.RebuiltSlot(node);
            string slots = a == null ? $"rebuilt #{b}" : b == null ? $"retail #{a}" : a == b ? $"#{a}" : $"#{a} → #{b}";
            int below = Owner.NotableBelow(node);
            return $"{cls} · {slots}" + (node.Differences.Count > 0 ? $" · {node.Differences.Count} {(node.Differences.Count == 1 ? "difference" : "differences")}" : "")
                + (below > 0 ? $" · {below} below" : "") + (node.BindsElsewhere ? " · whole-world lookup finds another node" : "");
        }
    }
    public string ToolTip => $"{WorldCompareView.Short(Source.Path, 1024)}\n{WorldCompareView.Status(Source)}" +
        (Source.BindsElsewhere ? "\nA whole-world lookup of this name (highest slot first) finds this node in the retail world, but another node in the rebuilt one. Animations bind roots and fall back to such lookups; names inside an animation are searched in its own subtrees first." : "") +
        string.Concat(Source.Differences.Take(6).Select(d => $"\n{d.Field}: {WorldCompareView.Short(d.Expected, 120)} → {WorldCompareView.Short(d.Actual, 120)}")) + (Source.Differences.Count > 6 ? "\n…" : "");

    private IReadOnlyList<WorldCompareRow>? children, visible;
    public IReadOnlyList<WorldCompareRow> Children => children ??= [.. Source.Children.Select(c => Owner.New(c, this))];
    /// <summary>The children the window's filter shows.</summary>
    public IReadOnlyList<WorldCompareRow> Visible => visible ??= [.. Children.Where(c => Owner.Passes(c, Owner.DifferencesOnly, Owner.Query))];
    internal bool IsVisibleInFilter => Owner.Passes(this, Owner.DifferencesOnly, Owner.Query);
    internal void Refilter() { if (visible == null) return; visible = null; Changed(nameof(Visible)); }

    private bool isExpanded, isSelected;
    public bool IsExpanded { get => isExpanded; set { if (isExpanded != value) { isExpanded = value; Changed(nameof(IsExpanded)); } } }
    /// <summary>Whether this is the comparison's selected row (<see cref="WorldCompareView.Select"/> chooses it).</summary>
    public bool IsSelected => isSelected;
    internal void SetSelected(bool value) { if (isSelected != value) { isSelected = value; Changed(nameof(IsSelected)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
