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

/// <summary>
/// The presentation state the World tree's section rows show: which zones and AI networks the view shows (each on its own;
/// all hidden at first), the zone overlay's kind, and the master eyes of Zones, AI Networks, AI Vehicles and Pickups.
/// </summary>
internal interface ISourceSectionState
{
    /// <summary>A zone's colour shows (the zone overlay is on).</summary>
    bool ZoneOverlayOn { get; }
    bool ZoneOverlayPolygons { get; }
    bool ZoneToolEngaged { get; }
    byte? PaintZone { get; }
    bool ZoneShown(byte zone);
    /// <summary>A network shows (the AI overlay is on).</summary>
    bool AnyNetworkShown { get; }
    bool NetworkShown(string network);
    IReadOnlyList<SectionEntry> AnimationChildren(SectionEntry section, int maximum) => [];
    /// <summary>The master eye of a section ("zones", "aiNetworks") or pass ("aiv", "puppies"): shown or hidden.</summary>
    bool MasterShown(string section);
    /// <summary>The definition and binding opened in Animation mode (its rows' Open is pressed); null outside the mode.</summary>
    (int Definition, int Entry)? OpenedAnimation => null;
    /// <summary>After Exit, the definition whose scenario is kept and the scenario's starts (its row's dot); null otherwise.</summary>
    (int Definition, int Starts)? KeptAnimation => null;
}

internal sealed class SceneTreeModel
{
    internal string Context { get; } = Guid.NewGuid().ToString("N");
    internal SceneHierarchy Hierarchy { get; }
    internal SceneTreeState State { get; }
    internal string Source { get; }
    internal bool IsPreview { get; }
    internal SourceMapSelection? Projection { get; }
    /// <summary>A source map's sections beyond the scene (zones, AI networks, animation definitions); null elsewhere.</summary>
    internal SourceMapSections? Sections { get; }
    internal ISourceSectionState? SectionState { get; set; }
    internal SceneTreeItem? Selected { get; private set; }
    internal IReadOnlyList<SceneTreeItem> Roots { get; }
    private readonly Func<int, SceneTreeIdentity> identity;
    private readonly Dictionary<string, SceneTreeItem> rows = [];
    internal event Action<SceneTreeItem>? Collapsing;
    /// <summary>Another row (or none) became the selection.</summary>
    internal event Action? SelectionChanged;
    /// <summary>Whether a node is hidden in the view (the outliner's eye); null outside source maps.</summary>
    internal Func<int, bool>? IsHiddenNode { get; set; }
    internal Func<int, SourceMapNodeVisibility?>? PartVisibility { get; set; }
    /// <summary>Whether the view shows only this node (middle-click on its eye).</summary>
    internal Func<int, bool>? IsIsolatedNode { get; set; }
    /// <summary>Every row created so far, for refreshing their hidden state.</summary>
    internal IEnumerable<SceneTreeItem> CreatedRows => rows.Values;
    private int animationRecords;
    internal IReadOnlyList<SectionEntry> AnimationChildren(SectionEntry section)
    {
        int remaining = zStudio.Core.Animation.AnimationStructurePlan.MaximumDependencyRecords - animationRecords;
        if (remaining <= 0) return [new NoteEntry("Animation tree limit reached (4,096 rows). Close and reopen the map to browse a different branch.")];
        var entries = Sections == null ? [] : SectionState?.AnimationChildren(section, remaining) ?? [];
        animationRecords += entries.Count;
        return entries;
    }
    internal void OnCollapsing(SceneTreeItem row) => Collapsing?.Invoke(row);
    /// <summary>Pickups list a row per catalog type inside Weapons, Ammo and Other (Group by type).</summary>
    internal bool PickupsByType { get; }
    /// <summary>Animations list a row per start kind instead of the files (Group by start); it needs the summary.</summary>
    internal bool AnimationsByStart { get; }
    /// <summary>The World tree pane's width (DIP): below 260 the definitions' target text and Frame hide, from 400 the target adds how it starts.</summary>
    internal double PaneWidth { get; private set; }
    internal bool Narrow => PaneWidth > 0 && PaneWidth < 260;
    internal bool Wide => PaneWidth >= 400;
    /// <summary>The pane's width changed: the rows whose text or buttons follow it are told when its band (narrow, normal, wide) changes.</summary>
    internal void SetPaneWidth(double width)
    {
        bool narrow = Narrow, wide = Wide;
        PaneWidth = width;
        if (narrow != Narrow || wide != Wide) RefreshAnimationRows();
    }
    /// <summary>
    /// The Animations rows show again what the summary, the selection, Animation mode and the kept scenario say (their glyph,
    /// target, marks and buttons): only their properties change, never the rows (expansion, selection and focus stay).
    /// </summary>
    internal void RefreshAnimationRows()
    {
        foreach (var row in rows.Values) if (row.IsAnimationRow) row.NotifyAnimation();
    }
    internal SceneTreeModel(GameScene scene, string source, bool isPreview, SceneTreeState state, Func<int, SceneTreeIdentity>? identity = null, IReadOnlySet<int>? included = null, SourceMapSelection? projection = null,
        SourceMapSections? sections = null, bool pickupsByType = false, ISourceSectionState? sectionState = null, bool animationsByStart = false, double paneWidth = 0)
    {
        Hierarchy = new(scene, included: included); Source = source; IsPreview = isPreview; State = state;
        Projection = projection; Sections = projection == null ? null : sections; PickupsByType = pickupsByType; SectionState = sectionState;
        AnimationsByStart = animationsByStart && Sections?.Summary != null; PaneWidth = paneWidth;
        this.identity = projection == null ? identity ?? (index => new("node:" + index, index, null))
            : index => new(projection.Identities.GetValueOrDefault(index) ?? "node:" + index, index >= 0 ? identity?.Invoke(index).SourceNode ?? index : -1,
                projection.AuthoredNodes.GetValueOrDefault(index)?.File ?? projection.Boundaries.GetValueOrDefault(index)?.Source);
        var roots = (projection?.Roots ?? Hierarchy.Roots).Select(i => New(i, null, "root", 0)).ToList();
        if (projection == null && Hierarchy.UnlinkedRoots.Count > 0) roots.Add(New(null, null, "unlinked", 0));
        // A source map's sections follow the world, always in this order: its zones, the vehicles the mission adds and the
        // networks they follow, its pickups, the objects startup animations place and the animation definitions.
        if (projection != null)
        {
            void Pass(string kind) { if (projection.Passes.Any(p => p.Kind == kind)) roots.Add(New(null, null, "pass:" + kind, 0)); }
            void Section(string section) { if (Sections != null) roots.Add(New(null, null, "section:" + section, 0, section: new SectionHeader(section))); }
            Section("zones"); Pass("aiv"); Section("aiNetworks"); Pass("puppies"); Section("definitions");
        }
        Roots = roots;
        var retainedPath = state.SelectionPath;
        IReadOnlyList<SceneTreeItem> level = Roots;
        foreach (string key in retainedPath)
        {
            var row = FindRow(level, key);
            if (row == null) break;
            if (key == retainedPath[^1] && row.Selectable) Select(row);
            else level = row.Children;
        }
    }
    internal SceneTreeIdentity Identity(int node) => identity(node);
    internal IReadOnlyList<SceneHierarchyEdge> Children(int node) => Projection?.Children(node) ?? Hierarchy.Children(node);
    internal SceneTreeItem New(int? node, SceneTreeItem? parent, string kind, int slot, SourceMapPassEntry? placement = null, SectionEntry? section = null)
    {
        var row = new SceneTreeItem(this, node, parent, kind, slot, placement, section);
        rows.Add(row.Id, row);
        if (row.StateKey == State.Selection && row.Selectable) Select(row);
        return row;
    }
    internal SceneTreeItem? Find(string id) => rows.GetValueOrDefault(id);
    /// <summary>A section row found by its entry key (a zone, a network, a definition), created along its path if needed.</summary>
    internal SceneTreeItem? FindSection(string key)
    {
        Stack<SceneTreeItem> pending = new(Roots.Where(r => r.Section != null).Reverse());
        int visited = 0;
        while (pending.TryPop(out var row) && ++visited < 100_000)
        {
            if (row.Section?.Key == key) return row;
            if (row.Section is SectionHeader or DefinitionScopeEntry or DefinitionFileEntry or DefinitionGroupEntry)
                for (int i = row.Children.Count - 1; i >= 0; i--) pending.Push(row.Children[i]);
        }
        return null;
    }
    private (SceneTreeItem? Row, AnimationTreeSummary? Summary, IReadOnlySet<int> Definitions)? selectedDefinitions;
    /// <summary>The definitions bound to the object selected in the view or the World tree (its owning object), for the amber marks.</summary>
    internal IReadOnlySet<int>? SelectedDefinitions
    {
        get
        {
            var summary = Sections?.Summary;
            if (summary == null) return null;
            if (selectedDefinitions is { } cached && cached.Row == Selected && cached.Summary == summary) return cached.Definitions;
            IReadOnlySet<int> found = Selected is { Section: null, Index: int node } && Projection != null && summary.DefinitionsByRoot.GetValueOrDefault(Projection.Owner(node)) is { } bound
                ? bound.ToHashSet() : new HashSet<int>();
            selectedDefinitions = (Selected, summary, found);
            return found;
        }
    }
    internal void Select(SceneTreeItem? row)
    {
        bool changed = Selected != row;
        bool marked = changed && SelectedDefinitions is { Count: > 0 };
        if (changed) { var previous = Selected; Selected = row; previous?.SetSelected(false); }
        row?.SetSelected(true);
        // The amber marks follow the selected object (the rows that had them, or get them).
        if (changed && (marked || SelectedDefinitions is { Count: > 0 })) RefreshAnimationRows();
        if (changed) SelectionChanged?.Invoke();
        State.Selection = row?.StateKey;
        // Grouping rows are left out (Pickups' group and type rows, Animations' file, start and Common rows): a pickup's or a
        // definition's path is the same whether Group by type or by start is on or off.
        Stack<string> path = new(); for (var current = row; current != null; current = current.Parent) if (!current.IsGroupingRow) path.Push(current.StateKey);
        State.SelectionPath = path.ToArray();
    }
    /// <summary>The row of a node, created along its path if needed; with <paramref name="expand"/> its ancestors open.</summary>
    internal SceneTreeItem? Reveal(int node, bool expand = true)
    {
        if (Selected?.Index == node) { if (expand) ExpandAncestors(Selected); return Selected; }
        var path = Projection?.Path(node) ?? Hierarchy.PathTo(node);
        if (path.Count == 0) return null;
        int unlinkedDepth = Projection == null && Hierarchy.UnlinkedRoots.Contains(path[0]) || Projection?.PassOf(path[0]) != null ? 1 : 0;
        int depth = path.Count - 1 + unlinkedDepth;
        if (depth > SceneHierarchy.MaximumDepth || depth == SceneHierarchy.MaximumDepth && Children(node).Count > 0) return null;
        var root = Roots.FirstOrDefault(r => r.Index == path[0]);
        if (root == null && Projection?.PassOf(path[0]) is { } pass && (Roots.FirstOrDefault(r => r.Kind == "pass:" + pass.Kind)
            ?? Roots.FirstOrDefault(r => r.Section is SectionHeader { Section: "definitions" })?.Children.FirstOrDefault(r => r.Kind == "pass:" + pass.Kind)) is { } group)
        { if (expand) group.IsExpanded = true; root = PassRow(group, path[0]); }
        if (root == null && Roots.FirstOrDefault(r => r.Kind == "unlinked") is { } unlinked)
        { if (expand) unlinked.IsExpanded = true; root = unlinked.Children.FirstOrDefault(r => r.Index == path[0]); }
        if (root == null) return null;
        var current = root;
        foreach (int child in path.Skip(1))
        {
            if (expand) current.IsExpanded = true;
            var next = current.Children.FirstOrDefault(r => r.Index == child && r.Problem == null);
            if (next == null) return null;
            current = next;
        }
        if (expand) ExpandAncestors(current);
        return current;
    }
    /// <summary>A row of a selection path at this level, looking inside grouping rows (which paths leave out).</summary>
    internal static SceneTreeItem? FindRow(IReadOnlyList<SceneTreeItem> level, string key)
    {
        foreach (var row in level) if (row.StateKey == key) return row;
        foreach (var row in level) if (row.IsGroupingRow && FindRow(row.Children, key) is { } found) return found;
        return null;
    }
    internal static void ExpandAncestors(SceneTreeItem row)
    { for (var parent = row.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true; }
    /// <summary>A pass object's row: a unit directly below its pass, a pickup below its group (and type) row.</summary>
    private static SceneTreeItem? PassRow(SceneTreeItem pass, int node)
    {
        foreach (var child in pass.Children)
        {
            if (child.Index == node) return child;
            if (child.PickupEntries is { } entries && entries.Any(e => e.Row == node) && PassRow(child, node) is { } found) return found;
        }
        return null;
    }
    /// <summary>The pickup rows (leaves) of a Pickups, group or type row, or a unit pass's rows, creating them if needed.</summary>
    internal static IEnumerable<SceneTreeItem> PassLeaves(SceneTreeItem row)
    {
        foreach (var child in row.Children)
            if (child.Placement != null) yield return child;
            else if (child.PickupEntries != null) foreach (var leaf in PassLeaves(child)) yield return leaf;
    }
}

/// <summary>A World tree search result: a row with its parent path (a definition: what matched), or a subheader ("Animations · 7").</summary>
internal sealed record OutlinerHit(SceneTreeItem? Row, string Path, string? Header = null, IReadOnlyList<string>? Words = null)
{
    public bool IsHeader => Header != null;
    public string AccessibleName => Header ?? Row?.AccessibleName ?? "";
    public string HelpText => Row?.HelpText ?? "";
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
    /// <summary>A section row (a header, a zone, an AI network, a definition and the rows grouping them, or a note); null for scene rows.</summary>
    internal SectionEntry? Section { get; }
    /// <summary>The unit or pickup an aiv or puppies row lists, with the difficulties placing it; null for other rows.</summary>
    internal SourceMapPassEntry? Placement { get; }
    /// <summary>A unit or pickup only other difficulties place: listed (dimmed) without an object in the view.</summary>
    public bool IsOtherDifficulty => Placement is { Row: null };
    public GameNode? Node => Index is int i && i >= 0 && i < Owner.Hierarchy.Scene.Nodes.Count ? Owner.Hierarchy.Scene.Nodes[i] : null;
    public bool SourceGroup => Index is int i && Owner.Projection?.IsSourceGroup(i) == true;
    /// <summary>A source map's later pass (aiv, puppies, anim) shown as a top-level tree of its own.</summary>
    internal SourceMapPass? Pass => Kind.StartsWith("pass:", StringComparison.Ordinal) ? Owner.Projection?.Passes.FirstOrDefault(p => "pass:" + p.Kind == Kind) : null;
    /// <summary>The mission animations bound to this object.</summary>
    public IReadOnlyList<string>? Animations => Index is int i ? Owner.Projection?.Animations.GetValueOrDefault(i) : null;
    public MissionAnimationUses? AnimationUses => Index is int i ? Owner.Projection?.AnimationUses.GetValueOrDefault(i) : null;
    public SourceSelectionNode? Authored => Index is int i ? Owner.Projection?.AuthoredNodes.GetValueOrDefault(i) : null;
    public bool Synthetic => SourceGroup || Index is int i && Owner.Projection?.Boundaries.GetValueOrDefault(i)?.Synthetic == true;
    public bool Selectable => Problem == null && (IsSectionItem || Section == null && (Node != null || Synthetic));
    /// <summary>A zone, an AI network or a definition: selected for its own card, not a scene object.</summary>
    public bool IsSectionItem => Section is ZoneEntry or NetworkEntry or DefinitionEntry or AnimationBindingEntry;
    /// <summary>A definition or binding row: its Open (▷) enters Animation mode on it.</summary>
    public bool CanOpenAnimation => IsSourceMap && Section is DefinitionEntry { BindingKnown: true } or AnimationBindingEntry;
    public string OpenAnimationToolTip => IsOpenedAnimation ? "Opened in Animation mode"
        : Section is AnimationBindingEntry binding ? $"Open {Short(binding.Binding.Name, 64)} in Animation mode (Enter)" : "Open in Animation mode (Enter)";
    public bool ReferencedObject => Index is int i && Owner.Projection?.IsBoundary(i) == true;
    public string DisplayName => Section != null ? SectionLabel : PickupName ?? (IsOtherDifficulty ? Placement!.Name : Owner.Projection != null && Index is int i ? Owner.Projection.DisplayName(i) : Node?.Name ?? "Missing node");
    public string? ReadOnlyReason => SourceGroup ? "This authored group is removed during compilation. Its hierarchy can be inspected and it can be renamed (right-click); other group edits are unavailable."
        : Index is int i ? Owner.Projection?.Boundaries.GetValueOrDefault(i)?.ReadOnlyReason : null;
    public string NodeType => Section != null ? SectionCategory : Pass != null ? "pass" : IsOtherDifficulty ? "placement" : SourceGroup ? "group" : ReferencedObject ? "reference" : Node?.Class switch
    { "object3d" when Node.ModelIndex != null => "mesh", "object3d" => "group", { } kind => kind, _ => Kind };
    public int Depth { get; }
    public bool Shared => Index is int i && (Owner.Projection?.ParentCount(i) ?? (Node != null ? Owner.Hierarchy.Parents(i).Count : 0)) > 1;
    public int ChildCount => Problem != null ? 0 : Section is PickupGroupEntry or PickupTypeEntry ? PickupChildCount : Section != null ? SectionChildren().Count + (Section is SectionHeader { Section: "definitions" } && Owner.Projection?.Passes.Any(p => p.Kind == "anim") == true ? 1 : 0) : Kind == "unlinked" ? Owner.Hierarchy.UnlinkedRoots.Count : Pass is { } pass ? pass.Kind == "puppies" ? PickupPresentation.Groups.Length : Math.Max(1, pass.Entries.Count) :
        Index is int i && (Node != null || Synthetic) ? Owner.Children(i).Count : 0;
    public string Label => Section is SectionHeader or DefinitionScopeEntry or DefinitionGroupEntry or PickupGroupEntry or PickupTypeEntry ? $"{SectionLabel} · {Meta}" : Section != null ? SectionLabel : Pass is { } pass ? $"{pass.Label} · {pass.Entries.Count}" : Owner.Projection != null && (Selectable || IsOtherDifficulty) ? Short(DisplayName, 180) : Kind == "unlinked" ? $"Unlinked nodes · {ChildCount}" : Node is { } n ?
        $"{Short(n.Name, 180)} · {n.Class} #{n.Index}" + (ChildCount > 0 ? $" · {ChildCount} {(ChildCount == 1 ? "child" : "children")}" : "") +
        (Shared ? " · shared" : "") + (Kind == "world partition" ? " · partition reference" : "") + (Problem == null ? "" : " · " + Problem) : $"Missing node #{Index}";
    public string Glyph => Pass != null ? "◫" : Problem != null || !Selectable && Kind != "unlinked" ? "!" : Kind == "unlinked" ? "◇" : NodeType switch
    { "reference" => "⬡", "world" => "◎", "lod" => "▱", "camera" => "▣", "light" => "☼", "mesh" => "△", "group" => "▰", _ => Shared ? "↗" : "▧" };
    /// <summary>A source map's outliner shows its own row: type icon and colour, name, zone and hide eye.</summary>
    public bool IsSourceMap => Owner.Projection != null;
    /// <summary>The outliner's type: world, group, geometry, object (a placed model) or node (lights, cameras, LOD…).</summary>
    public string Category => Section != null ? SectionCategory : Pass != null || Kind == "unlinked" ? "group" : IsOtherDifficulty ? "object" : Problem != null ? "node" : IsEmptyNode ? "empty" : NodeType switch
    { "world" => "world", "group" or "lod" => "group", "mesh" => "geometry", "reference" => "object", _ => "node" };
    /// <summary>
    /// A source map's node with no geometry and no rows below it (an Object3D without a model): it draws nothing, but keeps its
    /// flags and zone. One with children stays a group, and a group the build removes keeps its type.
    /// </summary>
    public bool IsEmptyNode => IsSourceMap && Section == null && Pass == null && Problem == null && !SourceGroup && !ReferencedObject && !IsOtherDifficulty
        && Node is { Class: "object3d", ModelIndex: null } && ChildCount == 0;
    public string OutlinerIcon => Section switch
    {
        // A binding is its object (a cube); a definition and a group show how it starts (the plain definition until the summary is in).
        AnimationBindingEntry => "Cube", AnimationPageEntry => "Folder",
        DefinitionEntry => DefinitionSummary is { } summary ? AnimationTreeSummary.Glyph(summary.Start.Kind) : "Definition",
        DefinitionGroupEntry group => AnimationTreeSummary.Glyph(group.Start),
        { } section => OutlinerKinds.SectionIcon(section),
        _ => Pass is { } pass ? OutlinerKinds.PassIcon(pass.Kind) : OutlinerKinds.Icon(Category),
    };
    /// <summary>A pickup row's catalog type, or a Group by type row's.</summary>
    public string? PickupType => Section is PickupTypeEntry type ? type.Type : Placement?.PickupType;
    internal PickupPresentation? Pickup => PickupPresentation.For(PickupType);
    public string? PickupKind => Pickup?.Kind;
    public bool HasPickupIcon => Pickup != null;
    /// <summary>A weapon pickup's row: its render reaches into the empty chevron column (pickups never have children).</summary>
    public bool IsWeaponPickup => Placement != null && Pickup?.IsWeapon == true;
    /// <summary>A weapon type's row (Group by type): its render takes a 32 DIP box beside its chevron.</summary>
    public bool IsWeaponTypeRow => Section is PickupTypeEntry && Pickup?.IsWeapon == true;
    public System.Windows.Media.ImageSource? PickupImage => Pickup?.Image;
    public string? PickupPictureToolTip => Pickup?.PictureToolTip;
    /// <summary>A pickup's name: its type's short name and its number ("ERFPG #1"); null for other rows.</summary>
    public string? PickupName => Placement is { Number: > 0 } placement && Pickup is { } pickup ? $"{pickup.ShortName} #{placement.Number}" : null;
    /// <summary>For text without the group or picture (undo labels, notices): "ERFPG weapon #1", "Napalm ammo #3", "Nanite #2".</summary>
    public string? PickupNameWithKind => Placement is { Number: > 0 } placement && Pickup is { } pickup
        ? pickup.Kind is "weapon" or "ammo" ? $"{pickup.ShortName} {pickup.Kind} #{placement.Number}" : PickupName : null;
    /// <summary>The number after a pickup's short name ("#1"), which never trims; empty for other rows.</summary>
    public string PickupNumberText => PickupName != null ? "#" + Placement!.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    public bool HasPickupNumber => PickupName != null;
    /// <summary>A Pickups group or type row's pickups, in list order; null for other rows.</summary>
    internal IReadOnlyList<SourceMapPassEntry>? PickupEntries => Section switch { PickupGroupEntry group => group.Entries, PickupTypeEntry type => type.Entries, _ => null };
    /// <summary>The Pickups row's Group by type toggle (shown while the section lists pickups), and whether it is on.</summary>
    public bool HasGroupByType => IsSourceMap && Pass is { Kind: "puppies" } pass && pass.Entries.Count > 0;
    public bool PickupsByTypeOn => Owner.PickupsByType;
    public string GroupByTypeName => Owner.PickupsByType ? "Group pickups by type, on" : "Group pickups by type, off";
    /// <summary>Weapons, Ammo or Other: a tertiary folder that follows the theme.</summary>
    public bool IsPickupGroup => Section is PickupGroupEntry;
    /// <summary>An empty Weapons, Ammo or Other group: its Frame and eye wait, saying why.</summary>
    public bool IsEmptyPickupGroup => Section is PickupGroupEntry { Entries.Count: 0 };
    /// <summary>A group or type whose pickups are all placed only on other difficulties: dimmed, without Frame or eye.</summary>
    public bool IsOtherDifficultyGroup => PickupEntries is { Count: > 0 } entries && entries.All(e => e.Row == null);
    /// <summary>The Frame button's tooltip and name.</summary>
    public string FrameToolTip => PickupEntries is { } pickups
        ? pickups.Count == 0 ? $"No {PickupNoun(2)} in this mission" : $"Frame every {PickupNoun(1)} on {ShownDifficulty}"
        : Section is DefinitionEntry ? "Frame bound objects" : Section is AnimationBindingEntry binding ? "Frame " + Short(binding.Binding.Root, 64) : "Frame selection";
    /// <summary>What a screen reader names the row.</summary>
    public string AccessibleName => PickupEntries is { } pickups ? $"{SectionLabel}, {PickupCount(pickups.Count)}" + (Section is PickupTypeEntry && Pickup is { } type ? ", " + type.ManualName : "")
        : PickupName is { } name && Pickup is { } pickup ? $"{pickup.ShortName} {Placement!.Number}, {pickup.Kind} pickup, {pickup.ManualName}, "
            + (IsOtherDifficulty ? $"not on {ShownDifficulty}, placed on {Joined(Placement.Difficulties)}" : $"on {Joined(Placement.Difficulties)}") + (IsHidden ? ", hidden" : "")
        : AnimationAccessibleName ?? OutlinerLabel + SecondaryRunText.Replace(' ', ' ') + (IsHidden && CanHide ? ", hidden" : "");
    private string ShownDifficulty => Owner.Projection?.ShownDifficulty?.ToString() ?? "the shown difficulty";
    private static string Joined(IReadOnlyList<MissionDifficulty> difficulties) => difficulties.Count switch
    {
        0 => "no difficulty", 1 => difficulties[0].ToString(),
        _ => string.Join(", ", difficulties.Take(difficulties.Count - 1)) + " and " + difficulties[^1],
    };
    /// <summary>What a group or type row holds, in words: "weapon pickup(s)", "ammo pickup(s)", "health and technology pickup(s)".</summary>
    private string PickupNoun(int count) => Section switch
    {
        PickupTypeEntry when Pickup is { } type => $"{type.ShortName} {type.Kind} pickup{(count == 1 ? "" : "s")}",
        PickupTypeEntry => count == 1 ? "pickup" : "pickups",
        PickupGroupEntry group => count == 1 ? PickupPresentation.GroupPickups(group.Group)[..^1] : PickupPresentation.GroupPickups(group.Group),
        _ => count == 1 ? "pickup" : "pickups",
    };
    private string PickupCount(int count) => Section is PickupTypeEntry && Pickup is { } type
        ? $"{Count(count)} {type.Kind} pickup{(count == 1 ? "" : "s")}" : $"{Count(count)} {PickupNoun(count)}";
    public System.Windows.Media.Brush OutlinerBrush => Section switch
    {
        DefinitionEntry when DefinitionSummary is { } summary => AnimationTreeSummary.Brush(summary.Start.Kind, summary.Start.Known),
        DefinitionGroupEntry group => AnimationTreeSummary.Brush(group.Start),
        // Files, Common and bindings only sort or point: tertiary, as Pickups' groups.
        DefinitionFileEntry or DefinitionScopeEntry or AnimationBindingEntry => Tertiary,
        { } section => OutlinerKinds.SectionBrush(section),
        _ => Pass is { } pass ? OutlinerKinds.PassBrush(pass.Kind) : OutlinerKinds.Brush(Category),
    };
    private static System.Windows.Media.Brush Tertiary => System.Windows.Application.Current?.TryFindResource("TextFillColorTertiaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
    /// <summary>The name alone: groups show no counts.</summary>
    public string OutlinerLabel => Section != null ? SectionLabel : Pass is { } pass ? pass.Label : Kind == "unlinked" ? "Unlinked nodes" : PickupName != null ? Pickup!.ShortName
        : Problem != null && !IsOtherDifficulty ? Label : Short(DisplayName, 180);
    /// <summary>A section of the World tree (a later pass, the world, or Zones, AI Networks, Animations): set in SemiBold, with its count.</summary>
    public bool IsSectionHeader => Pass != null || Section is SectionHeader || Owner.Projection != null && Parent == null && Category == "world";
    /// <summary>Whether this object is an initial root or known indirect recipient.</summary>
    public bool IsAnimated => Animations != null;
    public System.Windows.Media.Brush AnimationBrush => OutlinerKinds.LightTheme
        ? AnimationUses?.HasInitialRoot == true ? InitialAnimationLightBrush : IndirectAnimationLightBrush
        : AnimationUses?.HasInitialRoot == true ? InitialAnimationBrush : IndirectAnimationBrush;
    private static readonly System.Windows.Media.Brush InitialAnimationLightBrush = AnimationColor(0x1F, 0x7F, 0x8C), IndirectAnimationLightBrush = AnimationColor(0x4F, 0x8A, 0x93);
    /// <summary>The theme changed: the brushes that follow it (a file row's icon, the animated mark) are read again.</summary>
    internal void NotifyTheme() { Changed(nameof(OutlinerBrush)); Changed(nameof(AnimationBrush)); Changed(nameof(BoundMarkBrush)); }
    public static readonly System.Windows.Media.Brush InitialAnimationBrush = AnimationColor(0x6F, 0xC8, 0xD6);
    // Half the original HSL saturation, with identical hue, lightness and alpha.
    public static readonly System.Windows.Media.Brush IndirectAnimationBrush = AnimationColor(0x89, 0xB5, 0xBC);
    private static System.Windows.Media.Brush AnimationColor(byte r, byte g, byte b)
    { var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    /// <summary>The aiv or puppies pass this row lies in (units and pickups the mission places after the world is loaded), or null.</summary>
    private readonly string? addedPass;
    public string? AnimationsToolTip => AnimationUses is { } uses
        ? (uses.HasInitialRoot ? "Initial animation root" + (uses.HasIndirect ? "; also receives animations indirectly" : "") : "Receives animation indirectly") + "\n" +
          string.Join("\n", uses.Uses.Take(MissionAnimationUses.ShownNames).Select(a => $"{Short(a.Name, 64)} — {a.Reason}"))
          + (uses.Uses.Count > MissionAnimationUses.ShownNames ? $"\nand {uses.Uses.Count - MissionAnimationUses.ShownNames:N0} more" + (uses.Truncated ? "+" : "") : uses.Truncated ? "\n…" : "")
        : null;
    /// <summary>
    /// The text at the row's right: the zone of an object or piece of geometry in the world ("Zone: 12"), or the difficulties
    /// placing an aiv or puppies unit or pickup ("E/M/H"). The units and pickups the mission places after the world is loaded
    /// have no zone, nor do other runtime copies; the anim pass's objects keep theirs.
    /// </summary>
    public string Meta => PickupEntries is { } pickups ? Count(pickups.Count) : Section != null ? SectionMeta : Pass is { } passRow ? passRow.Entries.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
        : Placement is { Difficulties.Count: > 0 } placement ? placement.Tag : Zone is { } zone ? "Zone: " + zone : "";
    /// <summary>The row's zone, "Any" or 0–254, or null (see <see cref="Meta"/>).</summary>
    public string? Zone => Category is "object" or "geometry" or "empty" && addedPass == null && Index is int i && Owner.Projection?.IsRuntimeCopy(i) == false
        && Node?.Metadata["zone_id"] is System.Text.Json.Nodes.JsonValue zone && ZoneWord(zone) is long word
        ? (word & 0xFF) == 0xFF ? "Any" : (word & 0xFF).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    public string? MetaToolTip => Section is ZoneEntry { Zone.Polygons: > 0 } zoneRow ? Polygons(zoneRow.Zone.Polygons) + " as placed" : Section is NoZonesEntry none ? Polygons(none.Polygons) + " as placed"
        : Placement is { Difficulties.Count: > 0 } placement ? "Placed on " + string.Join(", ", placement.Difficulties)
        : Section is DefinitionEntry { BindingKnown: true, Definition.Entries.Count: > 1 } several ? Count(several.Definition.Entries.Count) + " bindings" : null;
    private static long? ZoneWord(System.Text.Json.Nodes.JsonValue value) =>
        value.TryGetValue<uint>(out uint word) ? word : value.TryGetValue<int>(out int signed) ? signed : value.TryGetValue<long>(out long wide) ? wide : null;
    public bool CanHide => IsSourceMap && Problem == null && (Section != null ? SectionCanHide : HasMasterEye || Index is int && !IsEmptyNode);
    /// <summary>
    /// The Zones, AI Networks, AI Vehicles and Pickups rows, and the Pickups group and type rows with a pickup on the shown
    /// difficulty: their eye shows or hides everything below them.
    /// </summary>
    public bool HasMasterEye => IsSourceMap && (Section is SectionHeader { Section: "zones" or "aiNetworks" } || Pass is { Kind: "aiv" or "puppies" } || PickupShown);
    /// <summary>The section or pass whose master eye this row is: a pass's kind, or "puppies:group" and "puppies:group:TYPE".</summary>
    internal string? MasterKey => Section switch
    {
        SectionHeader { Section: "zones" or "aiNetworks" } header => header.Section,
        PickupGroupEntry group when PickupShown => "puppies:" + group.Group,
        PickupTypeEntry type when PickupShown => "puppies:" + type.Group + ":" + type.Type,
        _ => Pass is { Kind: "aiv" or "puppies" } pass ? pass.Kind : null,
    };
    /// <summary>A Pickups group or type row with a pickup the view shows.</summary>
    private bool PickupShown => PickupEntries?.Any(e => e.Row != null) == true;
    /// <summary>A unit or pickup the mission places after the world loads: hidden and shown one by one (no isolation).</summary>
    public bool IsPassObject => addedPass != null && Section == null;
    /// <summary>
    /// The row's Frame selection button: a selectable object, piece of geometry or node (not a group the build removes), an AI
    /// network, a definition or binding bound to an object the World tree lists (never only a template), or a Pickups group or type.
    /// </summary>
    public bool CanFrame => IsSourceMap && (Section != null ? Section is NetworkEntry || CanFrameAnimation || PickupShown : Index is int && Selectable && !SourceGroup && !IsEmptyNode);
    internal SourceMapNodeVisibility? VisibilityState => Index is int i ? Owner.PartVisibility?.Invoke(i) : null;
    public bool IsHidden => MasterKey is { } master ? State?.MasterShown(master) != true : Section != null ? SectionHidden
        : VisibilityState is { } part ? !part.Visible : Index is int i && Owner.IsHiddenNode?.Invoke(i) == true;
    public bool IsIsolated => Section == null && !IsPassObject && Index is int i && Owner.IsIsolatedNode?.Invoke(i) == true;
    /// <summary>A hidden row drawn faint: never a section or pass row, whose master eye shows only what pressing it does.</summary>
    public bool IsFaded => (IsHidden || VisibilityState?.EffectiveVisible == false) && !IsSectionHeader && Section is not DefinitionEntry && PickupEntries == null;
    public string EyeIcon => IsHidden ? "EyeOff" : IsIsolated ? "Isolate" : "Eye";
    public string EyeToolTip => Section != null || HasMasterEye ? SectionEyeToolTip : IsPassObject ? (IsHidden ? "Show in viewport" : "Hide in viewport")
        : (IsHidden ? "Show in viewport" : "Hide in viewport") + (VisibilityState?.Reason is { } reason ? "\n" + reason : "")
            + (IsIsolated ? "\nMiddle-click: show all objects again" : "\nMiddle-click: hide all other objects");
    internal void NotifyHidden()
    {
        foreach (string name in new[] { nameof(IsHidden), nameof(IsIsolated), nameof(IsFaded), nameof(EyeIcon), nameof(EyeToolTip), nameof(HeaderActive), nameof(HeaderPaused),
            nameof(ZoneKindPolygons), nameof(ZoneKindsEnabled), nameof(ZoneKindsToolTip), nameof(IsPaintZone), nameof(HeaderControlsToolTip), nameof(AccessibleName) }) Changed(name);
    }
    public string ToolTip => (Section != null ? SectionToolTip : Pass is { } pass ? pass.Description + PickupSummary(pass) : PickupName != null ? PickupToolTip
        : IsOtherDifficulty ? OtherDifficultyToolTip : ToolTipCore + (Placement is { Detail.Length: > 0 } placed ? "\nPlacement: " + placed.Detail : "")) + (Animations is { } animations ? "\nAnimations: " + string.Join(", ", animations.Select(a => Short(a, 64))) : "");
    /// <summary>The Pickups row's summary line: "64 pickups: 8 weapons, 44 ammo, 12 health." (kinds with none left out).</summary>
    private static string PickupSummary(SourceMapPass pass)
    {
        if (pass.Kind != "puppies" || pass.Entries.Count == 0) return "";
        var kinds = pass.Entries.GroupBy(e => PickupPresentation.For(e.PickupType)?.Kind ?? "other").ToDictionary(g => g.Key, g => g.Count());
        string[] parts = [.. new[] { ("weapon", "weapon", "weapons"), ("ammo", "ammo", "ammo"), ("health", "health", "health"), ("technology", "technology", "technology"), ("other", "other", "other") }
            .Where(k => kinds.ContainsKey(k.Item1)).Select(k => $"{Count(kinds[k.Item1])} {(kinds[k.Item1] == 1 ? k.Item2 : k.Item3)}")];
        return $"\n{Count(pass.Entries.Count)} pickup{(pass.Entries.Count == 1 ? "" : "s")}: {string.Join(", ", parts)}.";
    }
    /// <summary>A pickup's tooltip: its name and kind, the manual's name, its catalog and game names, where it is and its difficulties.</summary>
    private string PickupToolTip
    {
        get
        {
            var pickup = Pickup!; var placement = Placement!;
            string where = placement.Position is { } p ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"X {p.X:0.#}, Y {p.Y:0.#}, Z {p.Z:0.#}") : "";
            string amount = placement.Pickup is { } placed ? $"amount {Count(placed.EffectiveAmount)}" + (placed.AuthoredAmount == 0 ? " (the type's default)" : "") : "";
            string located = string.Join(" · ", new[] { where, amount }.Where(s => s.Length > 0));
            string difficulties = !IsOtherDifficulty ? $"Placed on {Joined(placement.Difficulties)}"
                : $"Placed on {Joined(placement.Difficulties)}. The view shows {ShownDifficulty}: " + (placement.Difficulties.Count == 1 ? $"choose {placement.Difficulties[0]} to see it." : "choose one of these to see it.");
            return $"{PickupName} · {pickup.Kind} pickup{(IsOtherDifficulty ? " · not on " + ShownDifficulty : "")}\n{pickup.ManualName}\n" +
                $"{pickup.Type} · {(IsOtherDifficulty ? "template " + pickup.TemplateName : "game name " + Short(placement.Name, 128))}" + (located.Length > 0 ? "\n" + located : "") + "\n" + difficulties
                + (placement.Listed is { Length: > 0 } listed ? "\n" + char.ToUpperInvariant(listed[0]) + listed[1..] : "");
        }
    }
    private string ToolTipCore => Authored is { } authored ? $"{Short(DisplayName, 2048)} · {(IsEmptyNode ? "empty\n" + OutlinerKinds.EmptyNote : NodeType)}\nSource: {Short(authored.File, 2048)} · glTF node #{authored.SourceNode}\n" +
        $"Tree parent: {(Parent == null ? "none" : Short(Parent.DisplayName, 200))}\nCompiled node: {authored.CompiledNode?.ToString() ?? "none"}" + (ReadOnlyReason == null ? "" : "\n" + ReadOnlyReason)
        : Synthetic ? $"{Short(DisplayName, 2048)} · referenced source group\n{ReadOnlyReason}" : Node is { } node ? $"{Short(node.Name, 2048)} · {(ReferencedObject ? "referenced object" : node.Class)} #{node.Index}\n" +
        (IsEmptyNode ? OutlinerKinds.EmptyNote + "\n" : "") +
        $"Tree parent: {(Parent?.Node is { } p ? Short(p.Name, 200) + " #" + p.Index : "none")} · {Kind}\n" +
        $"Stored parents: {string.Join(", ", node.Parents.Take(32))}{(node.Parents.Length > 32 ? " …" : "")}\n" +
        $"Source node: #{Owner.Identity(node.Index).SourceNode} · model: {node.ModelIndex?.ToString() ?? "none"}\n" +
        Short(Owner.Source, 2048) + (Owner.Identity(node.Index).PlacementPreview() is { } placement ? "\n" + placement : "") +
        (Problem == null ? "" : "\n" + Problem) : Kind == "unlinked" ? "Nodes outside the world roots, including disconnected cyclic components." : $"Invalid scene reference #{Index}.";
    private string OtherDifficultyToolTip => $"{Short(DisplayName, 2048)} · {Placement!.Detail}\nPlaced on {string.Join(", ", Placement.Difficulties)}; " +
        "the view shows another difficulty. Choose one of these difficulties to see it.";
    private bool isExpanded, isSelected;
    private IReadOnlyList<SceneTreeItem>? children;
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value) return;
            isExpanded = value; Owner.State.Expanded[StateKey] = value; if (!value) Owner.OnCollapsing(this); Changed(nameof(IsExpanded));
            if (IsGroupingRow) { Changed(nameof(HoldsBoundMark)); Changed(nameof(AccessibleName)); }
        }
    }
    public bool IsSelected
    {
        get => isSelected;
        set { if (isSelected != value) { isSelected = value; Changed(nameof(IsSelected)); if (Section is DefinitionEntry) Changed(nameof(ShowsBoundMark)); } }
    }
    internal void SetSelected(bool value) => IsSelected = value;
    internal SceneTreeItem(SceneTreeModel owner, int? index, SceneTreeItem? parent, string kind, int slot, SourceMapPassEntry? placement = null, SectionEntry? section = null)
    {
        Owner = owner; Index = index; Parent = parent; Kind = kind; Placement = placement; Section = section; Depth = (parent?.Depth ?? -1) + 1;
        addedPass = parent == null ? null : parent.Pass?.Kind is "aiv" or "puppies" ? parent.Pass.Kind : parent.addedPass;
        if (index != null && Node == null && !Synthetic) Problem = "invalid reference";
        for (var ancestor = parent; ancestor != null; ancestor = ancestor.Parent)
            if (index != null && ancestor.Index == index) { Problem = "cycle reference"; break; }
        if (Depth >= SceneHierarchy.MaximumDepth && ChildCount > 0) Problem = "hierarchy depth limit";
        // Include sibling occurrence, never display names, in path identity. A pickup keeps its key whichever group or type
        // row shows it (Group by type on or off): its path starts at the Pickups row.
        var keyParent = parent;
        while (keyParent?.IsGroupingRow == true) keyParent = keyParent.Parent;
        StateKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((keyParent?.StateKey ?? "root") + "/" +
            (section != null ? section.Key : placement is { Row: null } ? "placement:" + placement.Key : Node == null && !Synthetic ? kind + ":" + index : owner.Identity(index!.Value).Key) + "/" + kind + "/" + slot)));
        // A source map's outliner starts with the world and its later passes collapsed; Pickups' groups start open, its
        // type rows closed.
        isExpanded = owner.State.Expanded.GetValueOrDefault(StateKey, owner.Projection == null && parent == null && Node?.Class == "world" || section is PickupGroupEntry);
    }
    public IReadOnlyList<SceneTreeItem> Children => children ??= CreateChildren();
    // Search may inspect the dependency rows the user opened, but never expands a transitive call graph itself.
    internal IReadOnlyList<SceneTreeItem> SearchChildren => Section is DefinitionEntry or AnimationBindingEntry or AnimationPageEntry ? children ?? [] : Children;
    private IReadOnlyList<SceneTreeItem> CreateChildren()
    {
        if (Problem != null) return [];
        if (PickupEntries != null) return PickupChildren();
        if (Section != null)
        {
            Dictionary<string, int> slots = [];
            var rows = SectionChildren().Select(entry =>
            {
                int occurrence = slots.GetValueOrDefault(entry.Key); slots[entry.Key] = occurrence + 1;
                return Owner.New(null, this, KindOf(entry), occurrence, section: entry);
            }).ToList();
            // Objects placed by animations come last: a few objects after the map's definitions (§3).
            if (Section is SectionHeader { Section: "definitions" } && Owner.Projection?.Passes.Any(p => p.Kind == "anim") == true)
                rows.Add(Owner.New(null, this, "pass:anim", 0));
            return rows;
        }
        if (Kind == "unlinked") return Owner.Hierarchy.UnlinkedRoots.Select(i => Owner.New(i, this, "unlinked root", 0)).ToArray();
        // Pickups always hold Weapons, Ammo and Other, in that order (an empty one says so).
        if (Pass is { } pass) return pass.Kind == "puppies" ? [.. PickupPresentation.Groups.Select(g => Owner.New(null, this, "pickupGroup", 0,
                section: new PickupGroupEntry(g, [.. pass.Entries.Where(e => (PickupPresentation.For(e.PickupType)?.Group ?? "other") == g)])))]
            : pass.Entries.Count == 0 ? [Owner.New(null, this, "note", 0, section: new NoteEntry(PassEmpty(pass.Kind)))]
            : pass.Entries.Select(e => Owner.New(e.Row, this, pass.Kind, 0, e)).ToArray();
        if (Index is not int node || !Selectable) return [];
        Dictionary<(int, string), int> occurrences = [];
        return Owner.Children(node).Select(edge =>
        {
            var key = (edge.Node, edge.Kind); int occurrence = occurrences.GetValueOrDefault(key); occurrences[key] = occurrence + 1;
            return Owner.New(edge.Node, this, edge.Kind, occurrence);
        }).ToArray();
    }
    private static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "…";
    /// <summary>
    /// A Pickups group's rows: its pickups in list order, or with Group by type a row per catalog type (catalog order, so
    /// health comes before technology under Other); a type row's pickups; an empty group's note.
    /// </summary>
    private SceneTreeItem[] PickupChildren()
    {
        var entries = PickupEntries!;
        if (Section is PickupGroupEntry group)
        {
            if (entries.Count == 0) return [Owner.New(null, this, "note", 0, section: new NoteEntry($"No {PickupPresentation.GroupPickups(group.Group)} in this mission."))];
            if (Owner.PickupsByType)
                return [.. entries.GroupBy(e => e.PickupType ?? "").OrderBy(g => PickupPresentation.For(g.Key)?.Index ?? int.MaxValue).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => Owner.New(null, this, "pickupType", 0, section: new PickupTypeEntry(group.Group, g.Key, [.. g])))];
        }
        return [.. entries.Select(e => Owner.New(e.Row, this, "puppies", 0, e))];
    }
    private int PickupChildCount => PickupEntries!.Count == 0 ? (Section is PickupGroupEntry ? 1 : 0)
        : Section is PickupGroupEntry && Owner.PickupsByType ? PickupEntries.Select(e => e.PickupType ?? "").Distinct().Count() : PickupEntries.Count;

    // ---------------------------------------------------------------- section rows

    private IReadOnlyList<SectionEntry>? sectionChildren;
    /// <summary>A section row's entries: the zones, the networks, the definitions (by scope and file), or a note when there are none.</summary>
    private IReadOnlyList<SectionEntry> SectionChildren() => sectionChildren ??= Section switch
    {
        SectionHeader { Section: "zones" } when Owner.Sections is { } s => s.Zones.Count == 0 && s.NoZonePolygons == 0 ? [new NoteEntry("No zones yet. Paint polygon zones with Edit zones.")]
            : [.. s.Zones.Select(z => (SectionEntry)new ZoneEntry(z)), .. s.NoZonePolygons > 0 ? [new NoZonesEntry(s.NoZonePolygons)] : Array.Empty<SectionEntry>()],
        SectionHeader { Section: "aiNetworks" } when Owner.Sections is { } s => s.Networks.Count == 0 ? [new NoteEntry("No AI networks in this mission.")]
            : [.. s.Networks.OrderBy(SourceMapSections.NetworkNumber).ThenBy(n => n.Id, StringComparer.Ordinal).Select(n => new NetworkEntry(n))],
        SectionHeader { Section: "definitions" } when Owner.Sections is { } s => s.DefinitionScopes(Owner.AnimationsByStart) is { Count: > 0 } scopes ? scopes : [new NoteEntry("This map lists no animation definitions.")],
        DefinitionScopeEntry scope => scope.Items,
        DefinitionFileEntry file => file.Definitions,
        DefinitionGroupEntry group => group.Definitions,
        DefinitionEntry or AnimationBindingEntry or AnimationPageEntry => Owner.AnimationChildren(Section),
        _ => [],
    };
    private static string KindOf(SectionEntry entry) => entry switch
    {
        ZoneEntry => "zone", NoZonesEntry => "noZones", NetworkEntry => "network", DefinitionScopeEntry => "definitionScope", DefinitionGroupEntry => "definitionGroup",
        DefinitionFileEntry => "definitionFile", DefinitionEntry => "definition", AnimationBindingEntry => "animationBinding", AnimationPageEntry => "animationPage", MissingFileEntry => "missingFile", _ => "note",
    };
    private static string PassEmpty(string kind) => kind switch
    {
        "aiv" => "No AI vehicles in this mission.", _ => "No objects are placed by startup animations.",
    };
    private ISourceSectionState? State => Owner.SectionState;
    private string SectionCategory => Section switch
    {
        SectionHeader => "section", ZoneEntry => "zone", NoZonesEntry => "noZones", NetworkEntry => "network", DefinitionScopeEntry => "definitionScope", DefinitionGroupEntry => "definitionGroup",
        DefinitionFileEntry => "definitionFile", DefinitionEntry or AnimationBindingEntry => "definition", AnimationPageEntry => "definitionScope", MissingFileEntry => "missingFile", PickupGroupEntry => "pickupGroup", PickupTypeEntry => "pickupType", _ => "note",
    };
    private string SectionLabel => Section switch
    {
        SectionHeader header => OutlinerKinds.SectionLabel(header.Section),
        PickupGroupEntry group => PickupPresentation.GroupLabel(group.Group),
        PickupTypeEntry type => PickupPresentation.For(type.Type)?.ShortName ?? (type.Type.Length == 0 ? "Unknown type" : Short(type.Type, 64)),
        ZoneEntry zone => ZoneName(zone.Zone.Zone),
        NoZonesEntry => "No zones",
        NetworkEntry network => SourceMapSections.NetworkStem(network.Network),
        DefinitionScopeEntry scope => scope.Name,
        DefinitionGroupEntry group => AnimationTreeSummary.Label(group.Start),
        DefinitionFileEntry file => System.IO.Path.GetFileName(file.Listing.File),
        DefinitionEntry definition => definition.Definition.Name,
        // A binding by the name calls, stops and prerequisites use; its object follows as grey text.
        AnimationBindingEntry binding => binding.Binding.Name,
        AnimationPageEntry => "More…",
        MissingFileEntry missing => System.IO.Path.GetFileName(missing.Listing.File),
        NoteEntry note => note.Text,
        _ => "",
    };
    private static string ZoneName(byte zone) => zone == 255 ? "Any" : "Zone " + zone.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Polygons(long count) => count == 1 ? "1 polygon" : count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " polygons";
    private static string Count(long count) => count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
    /// <summary>
    /// The text after the name: a zone's label, a network's authored name (when it differs), a file listed again, Common's
    /// "shared by every map" (hidden in a narrow pane). A definition's and a binding's target is <see cref="TargetRunText"/>.
    /// </summary>
    public string? SecondaryText => Section switch
    {
        ZoneEntry { Zone.Zone: 255 } => "No zone limit",
        ZoneEntry zone => zone.Zone.Label,
        NetworkEntry network when network.Network.Name != SourceMapSections.NetworkStem(network.Network) => Short(network.Network.Name, 64),
        DefinitionScopeEntry when !Owner.Narrow => "shared by every map",
        DefinitionFileEntry { Listing.Occurrence: > 0 } => "listed again",
        _ => null,
    };
    /// <summary>The secondary text as it follows the name on the same line (an em space apart), or empty.</summary>
    public string SecondaryRunText => SecondaryText is { Length: > 0 } text ? "\u2003" + text : "";
    private string SectionMeta => Section switch
    {
        SectionHeader { Section: "zones" } => Count(Owner.Sections?.Zones.Count ?? 0),
        SectionHeader { Section: "aiNetworks" } => Count(Owner.Sections?.Networks.Count ?? 0),
        SectionHeader { Section: "definitions" } => Count(Owner.Sections?.Definitions.Definitions.Count ?? 0),
        ZoneEntry zone => zone.Zone.Polygons > 0 ? Count(zone.Zone.Polygons) : zone.Zone.Objects > 0 ? "objects only" : "Not used",
        NoZonesEntry none => Count(none.Polygons),
        NetworkEntry network => network.Network.Nodes.Count == 1 ? "1 node" : Count(network.Network.Nodes.Count) + " nodes",
        DefinitionScopeEntry scope => Count(scope.Count),
        DefinitionGroupEntry group => Count(group.Definitions.Count),
        DefinitionFileEntry file => Count(file.Definitions.Count),
        // Only several bindings say how many ("×8"); "1 binding" says nothing the target does not.
        DefinitionEntry { BindingKnown: false } => "—",
        DefinitionEntry definition => definition.Definition.Entries.Count > 1 ? "×" + Count(definition.Definition.Entries.Count) : "",
        AnimationPageEntry page => "from " + (page.Offset + 1),
        MissingFileEntry => "Missing",
        _ => "",
    };
    /// <summary>The colour square of a zone (its overlay colour) or an AI network (its attack strategy's), or null.</summary>
    public System.Windows.Media.Brush? Swatch => Section switch
    {
        ZoneEntry zone => ZonePicker.Swatch(zone.Zone.Zone),
        NetworkEntry network => OutlinerKinds.Frozen(zStudio.Rendering.AiNetworkColors.Hex(network.Network.AttackStrategy)),
        _ => null,
    };
    /// <summary>The dashed square of polygons without a zone.</summary>
    public bool SwatchDashed => Section is NoZonesEntry;
    public bool HasSwatch => Swatch != null || SwatchDashed;
    /// <summary>A section row without an icon or swatch: a note.</summary>
    public bool IsNote => Section is NoteEntry;
    /// <summary>A Pickups group or type placed only on other difficulties (dimmed).</summary>
    public bool IsDimmed => IsOtherDifficultyGroup;
    public bool HasWarning => Section is NetworkEntry { Network.Diagnostics.Count: > 0 } || Section is MissingFileEntry;
    public string? WarningToolTip => Section switch
    {
        NetworkEntry network => string.Join("\n", network.Network.Diagnostics.Take(3).Select(d => Short(d.Message, 200))) + (network.Network.Diagnostics.Count > 3 ? $"\n{network.Network.Diagnostics.Count - 3} more" : ""),
        MissingFileEntry missing => $"Listed by {missing.Listing.ListedBy} but not in the project: the build skips it.",
        _ => null,
    };
    /// <summary>Which header controls the row shows: "zones" (Objects | Polygons) or "aiNetworks" (none besides its eye).</summary>
    public string? SectionKind => Section is SectionHeader { Section: "zones" or "aiNetworks" } header ? header.Section : null;
    /// <summary>The header's overlay is on: some zone or network shows (its eye is lit).</summary>
    public bool HeaderActive => Section is SectionHeader header && (header.Section == "zones" ? State?.ZoneOverlayOn == true : header.Section == "aiNetworks" && State?.AnyNetworkShown == true);
    /// <summary>The header's controls wait while Edit zones shows its own zone view, or have nothing to show.</summary>
    public bool HeaderPaused => Section is ZoneEntry ? State?.ZoneToolEngaged == true : Section is SectionHeader { Section: "zones" } ? State?.ZoneToolEngaged == true || Owner.Sections?.Zones.Count is null or 0
        : Section is SectionHeader { Section: "aiNetworks" } && Owner.Sections?.Networks.Count is null or 0;
    public string? HeaderControlsToolTip => Section is SectionHeader { Section: "zones" } ? State?.ZoneToolEngaged == true ? "Paused while Edit zones is on: the tool shows its own zone view"
            : Owner.Sections?.Zones.Count is null or 0 ? "No zones yet: paint polygon zones with Edit zones" : null
        : Section is SectionHeader { Section: "aiNetworks" } && Owner.Sections?.Networks.Count is null or 0 ? "No AI networks in this mission" : null;
    public bool ZoneKindPolygons => State?.ZoneOverlayPolygons != false;
    /// <summary>Why the Objects | Polygons switch waits, or null when it takes input.</summary>
    public string? ZoneKindsToolTip => HeaderControlsToolTip ?? (State?.ZoneOverlayOn != true ? "Show a zone (its eye, or the eye on Zones) to colour zones by object or by polygon" : null);
    /// <summary>The Objects | Polygons switch takes input while a zone's colour shows (and Edit zones is off).</summary>
    public bool ZoneKindsEnabled => State?.ZoneOverlayOn == true && !HeaderPaused;
    /// <summary>The zone Edit zones paints with (its row shows the brush).</summary>
    public bool IsPaintZone => Section is ZoneEntry zone && State?.ZoneToolEngaged == true && State.PaintZone == zone.Zone.Zone;
    private bool SectionCanHide => Section switch
    {
        SectionHeader { Section: "zones" or "aiNetworks" } => true,
        ZoneEntry zone => zone.Zone.Used,
        NetworkEntry => true,
        PickupGroupEntry or PickupTypeEntry => PickupShown,
        _ => false,
    };
    private bool SectionHidden => Section switch
    {
        ZoneEntry zone => State?.ZoneShown(zone.Zone.Zone) != true,
        NetworkEntry network => State?.NetworkShown(network.Network.Id) != true,
        _ => false,
    };
    private string SectionEyeToolTip => MasterKey switch
    {
        "zones" => HeaderControlsToolTip ?? (IsHidden ? $"Show every zone's colour ({(ZoneKindPolygons ? "polygon zones" : "object zones")}). Replaces Soils, CanModify, ClipTo and Landmarks" : "Hide every zone's colour"),
        "aiNetworks" => HeaderControlsToolTip ?? (IsHidden ? "Show every AI network: its nodes and links in its attack-strategy colour" : "Hide every AI network"),
        "aiv" => IsHidden ? "Show every AI vehicle" : "Hide every AI vehicle",
        "puppies" => IsHidden ? "Show every pickup" : "Hide every pickup",
        { } key when key.StartsWith("puppies:", StringComparison.Ordinal) => IsHidden ? $"Show every {PickupNoun(1)}" : $"Hide every {PickupNoun(1)}",
        _ => Section switch
        {
            PickupGroupEntry or PickupTypeEntry => PickupEntries!.Count == 0 ? $"No {PickupNoun(2)} in this mission" : $"Placed only on other difficulties: choose one to see them",
            ZoneEntry zone => IsHidden ? $"Show {ZoneName(zone.Zone.Zone).ToLowerInvariant()}'s colour" : $"Hide {ZoneName(zone.Zone.Zone).ToLowerInvariant()}'s colour",
            NetworkEntry network => IsHidden ? $"Show {SourceMapSections.NetworkStem(network.Network)} in the view" : $"Hide {SourceMapSections.NetworkStem(network.Network)} in the view",
            _ => "",
        },
    };
    private string SectionToolTip => Section switch
    {
        SectionHeader { Section: "zones" } => "The map's zones: the zones its polygons carry and the zones of its objects. Click a zone for its details and label.",
        SectionHeader { Section: "aiNetworks" } => "The mission's AI networks (aipath/net_NN.zrd), each in its attack-strategy colour. Click one to show it and edit its settings.",
        SectionHeader { Section: "definitions" } => AnimationsToolTipText,
        DefinitionScopeEntry scope => $"Definitions from {scope.Folder} that every map lists. {Owner.Sections?.Mission ?? "This map"} lists {Count(scope.Count)} of them: "
            + "effects, enemy vehicles and turrets, pickups, the player tank's trails.\nEditing one changes every map that lists it (its card's Source names them).",
        DefinitionGroupEntry group => $"{AnimationTreeSummary.Label(group.Start)} · {Count(group.Definitions.Count)}\n{AnimationTreeSummary.Rule(group.Start)}",
        ZoneEntry { Zone: var zone } => $"{ZoneName(zone.Zone)}{(zone.Label != null ? " · " + Short(zone.Label, 256) : "")}\n"
            + (zone.Used ? $"{Polygons(zone.Polygons)} as placed · {(zone.Objects == 1 ? "1 object" : Count(zone.Objects) + " objects")} ({Count(zone.OwnObjects)} own, {Count(zone.InheritedObjects)} inherited)\nClick for its details and label."
                : "Not used by any polygon or object. Its label stays with the map."),
        NoZonesEntry => "Polygons without a zone. They are not a zone: the overlay leaves them untinted. The camera keeps its previous zones on them, and a vehicle on them passes every zone test.",
        NetworkEntry { Network: var network } => $"{SourceMapSections.NetworkStem(network)} · {StrategyText(network.AttackStrategy)}\nType {(network.Type.Length == 0 ? "not stored" : Short(network.Type, 64))} · "
            + $"{network.Nodes.Count} nodes · {network.Nodes.Sum(n => n.PreviewLinks.Count(l => l.Target != null))} links\n{network.Id}",
        AnimationBindingEntry binding => $"{Short(binding.Binding.Name, 128)} · binding #{binding.Binding.Entry} of {Short(binding.Definition.Definition.Name, 128)}\n"
            + $"Bound to {Short(binding.Binding.Root, 128)} · calls, stops and prerequisites use this name\nOpen (▷, Enter or double-click) enters Animation mode on this binding",
        AnimationPageEntry => "Expand to list the next bindings, up to 100. No animation is evaluated.",
        DefinitionFileEntry file => $"{file.Listing.File}{(file.Listing.Occurrence > 0 ? " · listed again" : "")}\n{Count(file.Definitions.Count)} definitions · listed by {file.Listing.ListedBy ?? "the map"}",
        DefinitionEntry entry => DefinitionToolTip(entry),
        MissingFileEntry missing => $"Listed by {missing.Listing.ListedBy} but not in the project: the build skips it.",
        NoteEntry note => note.Text,
        PickupGroupEntry or PickupTypeEntry => PickupGroupToolTip,
        _ => "",
    };
    /// <summary>"Weapons · 8 weapon pickups, all on Hard"; a type adds its manual, catalog and template names.</summary>
    private string PickupGroupToolTip
    {
        get
        {
            var entries = PickupEntries!;
            int shown = entries.Count(e => e.Row != null);
            string counts = entries.Count == 0 ? $"No {PickupNoun(2)} in this mission"
                : shown == entries.Count ? $"{PickupCount(entries.Count)}, all on {ShownDifficulty}"
                : shown == 0 ? $"{PickupCount(entries.Count)}, only on other difficulties"
                : $"{PickupCount(entries.Count)}: {Count(shown)} on {ShownDifficulty}, {Count(entries.Count - shown)} only on other difficulties";
            return $"{SectionLabel} · {counts}" + (Section is PickupTypeEntry type && Pickup is { } pickup ? $"\n{pickup.ManualName} · {type.Type} · template {pickup.TemplateName}" : "");
        }
    }
    internal static string StrategyText(AiAttackStrategy strategy) => strategy.State switch
    {
        AiAttackStrategyState.Missing => "No attack strategy stored: the game attacks head-on",
        AiAttackStrategyState.Stored when strategy.Kind == AiAttackStrategyKind.Unknown => $"Attack strategy “{Short(strategy.Value ?? "", 64)}” is not one the game knows: it attacks head-on",
        AiAttackStrategyState.Stored => OutlinerKinds.StrategyName(strategy.Kind) + $" ({Short(strategy.Value ?? "", 64)})",
        _ => "Attack strategy unavailable (invalid data): the game attacks head-on",
    };
    /// <summary>
    /// What a search matches: the name, and a zone's number and label, a network's name, strategy and type, a definition's
    /// NAME, target, file, bindings and start words (design §9), a binding's object.
    /// </summary>
    internal string SearchText => Section switch
    {
        ZoneEntry zone => $"{SectionLabel} {zone.Zone.Zone} {zone.Zone.Label}",
        NetworkEntry network => $"{SectionLabel} {network.Network.Name} {network.Network.AttackStrategy.Value} {OutlinerKinds.StrategyName(network.Network.AttackStrategy.Kind)} {network.Network.Type}",
        DefinitionEntry definition => DefinitionSummary?.SearchText ?? $"{SectionLabel} {definition.Definition.RootName} {System.IO.Path.GetFileName(definition.Definition.File)}",
        AnimationBindingEntry binding => $"{binding.Binding.Name} {binding.Binding.Root}",
        // A pickup by its name, the manual's name, its kind, catalog type, template and (placed) game name.
        _ when PickupName is { } name && Pickup is { } pickup => $"{name} {pickup.ManualName} {pickup.Kind} pickup {PickupPresentation.GroupLabel(pickup.Group)} {pickup.Type} {pickup.TemplateName}" + (IsOtherDifficulty ? "" : " " + Placement!.Name),
        _ => DisplayName + (Pass is { } pass ? " " + pass.Label : ""),
    };
    /// <summary>
    /// Whether a search matches: every word of the query, in any order, ignoring case ("napalm weapon", "pu021").
    /// </summary>
    internal bool Matches(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return true;
        string text = SearchText;
        foreach (var word in words)
            if (!text.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
    internal static string[] SearchWords(string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // ---------------------------------------------------------------- Animations rows (design/world-tree-animations.md)

    /// <summary>A row of the Animations section whose look follows the summary, the selection, Animation mode or the pane's width.</summary>
    internal bool IsAnimationRow => Section is SectionHeader { Section: "definitions" } or DefinitionScopeEntry or DefinitionGroupEntry or DefinitionFileEntry or DefinitionEntry or AnimationBindingEntry;
    /// <summary>A row that only groups others (Pickups' group and type rows; Animations' file, start and Common rows): paths and keys pass through it.</summary>
    internal bool IsGroupingRow => PickupEntries != null || Section is DefinitionFileEntry or DefinitionGroupEntry or DefinitionScopeEntry;
    /// <summary>How the definition starts and what it acts on; null until the map's summary is in, and on other rows.</summary>
    internal AnimationDefinitionSummary? DefinitionSummary => Section is DefinitionEntry definition && Owner.Sections?.Summary?.Definitions.TryGetValue(definition.Definition.Index, out var found) == true ? found : null;
    public bool IsAnimationBinding => Section is AnimationBindingEntry;
    /// <summary>Bound to nothing in this world: its name in tertiary text and its glyph at 55 % (§7), never a dimmed row.</summary>
    public bool IsUnboundDefinition => Section is DefinitionEntry { BindingKnown: true, Roots: 0 };
    /// <summary>
    /// The grey text after a definition's or binding's name (§5): what it acts on (a binding: its object), and from 400 DIP
    /// how it starts; hidden below 260 DIP and when it is the name. "template" named as its definition is the quiet run.
    /// </summary>
    public string TargetRunText => TargetText(quiet: false);
    public string TargetQuietRunText => TargetText(quiet: true);
    private string TargetText(bool quiet)
    {
        if (Owner.Narrow) return "";
        string text; bool isQuiet = false;
        switch (Section)
        {
            case AnimationBindingEntry binding: text = binding.Binding.Root; break;
            case DefinitionEntry definition when DefinitionSummary is { } summary:
                text = summary.Target?.Text ?? "";
                if (text == definition.Definition.Name) text = "";
                if (Owner.Wide) text = text.Length > 0 ? text + " · " + summary.Start.WideDetail : summary.Start.WideDetail;
                else isQuiet = summary.Target?.Quiet == true;
                break;
            default: return "";
        }
        return quiet != isQuiet || text.Length == 0 ? "" : "  " + Short(text, 256);
    }
    /// <summary>The definition or binding opened in Animation mode (its Open is pressed, §7): the definition, and the binding played when it has several.</summary>
    public bool IsOpenedAnimation => State?.OpenedAnimation is { } opened && Section switch
    {
        DefinitionEntry definition => definition.Definition.Index == opened.Definition,
        AnimationBindingEntry binding => binding.Binding.Entry == opened.Entry && binding.Definition.Definition.Index == opened.Definition,
        _ => false,
    };
    /// <summary>After Exit, the definition whose scenario is kept: an accent dot after its text (§7).</summary>
    public bool IsKeptAnimation => Section is DefinitionEntry definition && State?.KeptAnimation is { } kept && kept.Definition == definition.Definition.Index;
    public string KeptToolTip => State?.KeptAnimation is { } kept ? $"Animation scenario kept · {kept.Starts} start{(kept.Starts == 1 ? "" : "s")}. Open returns to it." : "";
    /// <summary>Bound to the object selected in the view or the World tree: the amber bar (§7); the selection's own bar replaces it.</summary>
    public bool AffectsSelection => Section is DefinitionEntry definition && Owner.SelectedDefinitions?.Contains(definition.Definition.Index) == true;
    public bool ShowsBoundMark => AffectsSelection && !IsSelected;
    /// <summary>A closed file, start group or Common holding a definition bound to the selected object: an amber dot before its count.</summary>
    public bool HoldsBoundMark => !IsExpanded && Owner.SelectedDefinitions is { Count: > 0 } selected && Section switch
    {
        DefinitionFileEntry file => file.Definitions.Any(d => selected.Contains(d.Definition.Index)),
        DefinitionGroupEntry group => group.Definitions.Any(d => selected.Contains(d.Definition.Index)),
        DefinitionScopeEntry scope => scope.Items.SelectMany(ScopeDefinitions).Any(d => selected.Contains(d.Definition.Index)),
        _ => false,
    };
    private static IEnumerable<DefinitionEntry> ScopeDefinitions(SectionEntry item) => item switch
    {
        DefinitionEntry definition => [definition], DefinitionFileEntry file => file.Definitions, DefinitionGroupEntry group => group.Definitions, _ => [],
    };
    /// <summary>The Structure view's bound colour (Animation mode's amber), lighter on the dark theme.</summary>
    public System.Windows.Media.Brush BoundMarkBrush => OutlinerKinds.LightTheme ? BoundLightBrush : BoundDarkBrush;
    internal static readonly System.Windows.Media.Brush BoundDarkBrush = AnimationColor(0xFF, 0xD8, 0x4A), BoundLightBrush = AnimationColor(0xC9, 0xA1, 0x00);
    /// <summary>A later definition with an earlier one's name: ⧉ in the status slot (§7).</summary>
    public bool HasDuplicateName => DefinitionSummary?.DuplicateOf != null;
    public string? DuplicateToolTip => Section is DefinitionEntry definition && DefinitionSummary is { DuplicateOf: int first }
        && Owner.Sections?.Definitions.Definitions.FirstOrDefault(d => d.Index == first) is { } earlier
        ? $"Another definition named {Short(definition.Definition.Name, 128)} comes first ({System.IO.Path.GetFileName(earlier.File)} · definition {earlier.Ordinal + 1}): calls, stops, resets and prerequisites by this name reach that one. This one still binds by NAME and starts on its own."
        : null;
    /// <summary>A definition bound to an object a World tree row lists, or a binding whose object one does: Frame frames it (never only a template).</summary>
    public bool CanFrameAnimation => Section switch
    {
        DefinitionEntry definition => DefinitionSummary?.Frameable ?? definition.Roots > 0,
        AnimationBindingEntry binding => Owner.Sections?.BindingRows.TryGetValue(binding.Binding.Entry, out int row) == true && Owner.Projection?.IsListed(row) == true,
        _ => false,
    };
    /// <summary>Below 260 DIP a definition's or binding's Frame hides; Open stays (§10).</summary>
    public bool HidesAnimationFrame => Section is DefinitionEntry or AnimationBindingEntry && Owner.Narrow;
    /// <summary>The Animations row's Group by start toggle (Group by type's icon), and whether it is on.</summary>
    public bool HasGroupByStart => IsSourceMap && Section is SectionHeader { Section: "definitions" } && Owner.Sections?.Definitions.Definitions.Count > 0;
    public bool AnimationsByStartOn => Owner.AnimationsByStart;
    public string GroupByStartName => Owner.AnimationsByStart ? "Group animations by how they start, on" : "Group animations by how they start, off";
    /// <summary>What a screen reader names a definition, binding, file, group or Common row (§11); null for other rows.</summary>
    private string? AnimationAccessibleName => Section switch
    {
        DefinitionEntry definition => DefinitionAccessibleName(definition),
        AnimationBindingEntry binding => $"{binding.Binding.Name}, binding of {binding.Definition.Definition.Name} on {binding.Binding.Root}, entry {binding.Binding.Entry}"
            + (IsOpenedAnimation ? ", opened in Animation mode" : ""),
        DefinitionGroupEntry group => $"{AnimationTreeSummary.Label(group.Start)}, {Count(group.Definitions.Count)} animation{(group.Definitions.Count == 1 ? "" : "s")}" + (HoldsBoundMark ? ", holds an animation bound to the selected object" : ""),
        DefinitionFileEntry file => $"{System.IO.Path.GetFileName(file.Listing.File)}{(file.Listing.Occurrence > 0 ? ", listed again" : "")}, {Count(file.Definitions.Count)} definitions" + (HoldsBoundMark ? ", holds an animation bound to the selected object" : ""),
        DefinitionScopeEntry scope => $"Common, shared by every map, {Count(scope.Count)} definitions" + (HoldsBoundMark ? ", holds an animation bound to the selected object" : ""),
        _ => null,
    };
    private string DefinitionAccessibleName(DefinitionEntry definition)
    {
        var summary = DefinitionSummary;
        string start = summary == null ? "" : ", " + char.ToLowerInvariant(summary.Start.Detail[0]) + summary.Start.Detail[1..].Replace(" · ", ", ", StringComparison.Ordinal);
        string acts = summary?.Target is not { } target ? "" : target.Kind == "none" ? ", not in this map" : ", acts on " + (target.Text.Length > 0 ? target.Text : definition.Definition.RootName);
        return $"{definition.Definition.Name}, animation definition{start}{acts}" + (definition.Definition.Entries.Count > 1 ? $", {Count(definition.Definition.Entries.Count)} bindings" : "")
            + $", {System.IO.Path.GetFileName(definition.Definition.File)}" + (HasDuplicateName ? ", same name as an earlier definition" : "")
            + (IsOpenedAnimation ? ", opened in Animation mode" : "") + (IsKeptAnimation ? ", animation scenario kept" : "") + (AffectsSelection ? ", bound to the selected object" : "");
    }
    /// <summary>The row's UI Automation help text: how a definition or binding opens, else the animations an object carries.</summary>
    public string HelpText => Section is DefinitionEntry or AnimationBindingEntry ? "Click shows its card; Enter opens Animation mode." : AnimationsToolTip ?? "";
    /// <summary>The tooltip's first line, set in SemiBold on Animations rows, and the rest.</summary>
    public string ToolTipTitle => IsAnimationRow && ToolTip.IndexOf('\n') is int at && at > 0 ? ToolTip[..at] : "";
    public string ToolTipBody => ToolTipTitle is { Length: > 0 } title ? ToolTip[title.Length..] : ToolTip;
    private string DefinitionToolTip(DefinitionEntry entry)
    {
        var definition = entry.Definition; var summary = DefinitionSummary;
        string start = summary?.Start.Detail ?? (definition.Activation ?? "WEAPON_HIT");
        string prerequisites = definition.Prerequisites is { } listed ? " · prerequisites: " + listed : "";
        string binds = !entry.BindingKnown ? "The animations of this map did not build, so binding is unknown."
            : entry.Roots == 0 ? $"No node in this world is named {Short(definition.RootName, 128)}, so this map has no animation from it."
            : $"Binds {Short(summary?.Target is { Kind: "object" or "objects" or "template", Text.Length: > 0 } target ? target.Text : definition.RootName, 128)}";
        string reset = double.TryParse(definition.ResetTime, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double time)
            ? Math.Abs(time + 99) < .1 ? " · one-shot" : time < 0 ? " · keeps its final pose" : $" · cleans up {time.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} s after a stop" : "";
        return $"{Short(definition.Name, 256)}\n{start}{prerequisites}\n{binds} · {definition.SequenceCount} sequence{(definition.SequenceCount == 1 ? "" : "s")}{reset}"
            + $"\n{definition.File} · definition {definition.Ordinal + 1}{(entry.Listing.Occurrence > 0 ? " · listed again" : "")}"
            + (DuplicateToolTip is { } duplicate ? "\n" + duplicate : "")
            + (entry.BindingKnown ? "\nClick shows its card · Open (▷, Enter, double-click) enters Animation mode" : "");
    }
    /// <summary>
    /// The Animations row's tooltip (§6): how many definitions the map lists, its own in how many files, Common's, how its own
    /// start, and the objects its animations place.
    /// </summary>
    private string AnimationsToolTipText
    {
        get
        {
            if (Owner.Sections is not { } sections) return "";
            string map = sections.Mission;
            var definitions = sections.Definitions.Definitions;
            var listings = sections.Definitions.Listings.ToDictionary(l => l.Index);
            bool Shared(SourceAnimationDefinition d) => listings.TryGetValue(d.Listing, out var l) && l.File.Replace('\\', '/').StartsWith("data/common/", StringComparison.OrdinalIgnoreCase);
            var own = definitions.Where(d => !Shared(d)).ToArray();
            int files = own.Select(d => d.Listing).Distinct().Count(), common = definitions.Count - own.Length;
            string text = $"{map} lists {Count(definitions.Count)} animation definition{(definitions.Count == 1 ? "" : "s")}: {Count(own.Length)} of its own in {Count(files)} file{(files == 1 ? "" : "s")}"
                + (common > 0 ? $", then {Count(common)} Common ones shared by every map." : ".");
            if (sections.Summary is { } summary)
            {
                int Of(params AnimationStartKind[] kinds) => own.Count(d => summary.Definitions.TryGetValue(d.Index, out var s) && kinds.Contains(s.Start.Kind));
                int opening = Of(AnimationStartKind.Opening), load = Of(AnimationStartKind.Load), startup = Of(AnimationStartKind.Startup), damage = Of(AnimationStartKind.Damage);
                int called = Of(AnimationStartKind.Called), game = Of(AnimationStartKind.Game), none = Of(AnimationStartKind.None);
                string[] parts = [.. new[] { (opening, "opening"), (load, "saved-game load"), (startup, "on startup"), (damage, "on damage") }.Where(p => p.Item1 > 0).Select(p => $"{Count(p.Item1)} {p.Item2}")];
                int own0 = opening + load + startup + damage;
                text += $"\n{Count(own0)} start{(own0 == 1 ? "s" : "")} on {(own0 == 1 ? "its" : "their")} own{(parts.Length > 0 ? " (" + string.Join(", ", parts) + ")" : "")} · "
                    + $"{Count(called)} {(called == 1 ? "is" : "are")} called by other animations · {Count(game)} {(game == 1 ? "is" : "are")} started by the game · {Count(none)} {(none == 1 ? "has" : "have")} no caller found.";
            }
            if (Owner.Projection?.Passes.FirstOrDefault(p => p.Kind == "anim") is { Entries.Count: > 0 } placed)
                text += $"\nThe last group lists the {Count(placed.Entries.Count)} object{(placed.Entries.Count == 1 ? "" : "s")} these animations place.";
            return text;
        }
    }
    /// <summary>A definition result's fields a word of the search is in, in the order name, rootName, target, file, binding, start; null for other rows.</summary>
    internal IReadOnlyList<string>? MatchedBy(IReadOnlyList<string> words) => DefinitionSummary is not { } summary ? null
        : [.. summary.SearchFields.Where(f => words.Any(w => f.Text.Contains(w, StringComparison.OrdinalIgnoreCase))).Select(f => f.Field).Distinct()];
    /// <summary>
    /// What a definition result shows in place of its tree path (§9): what matched when it is not the name or the target
    /// (its file, "NAME trbridge", a binding, how it starts), else its file ("Common › …" for a common one).
    /// </summary>
    internal string? ResultNote(IReadOnlyList<string> words)
    {
        if (Section is not DefinitionEntry definition) return null;
        string file = System.IO.Path.GetFileName(definition.Definition.File);
        bool common = false; for (var at = Parent; at != null; at = at.Parent) common |= at.Section is DefinitionScopeEntry;
        string where = common ? "Common › " + file : file;
        if (MatchedBy(words) is not { Count: > 0 } matched || matched.Contains("name") || matched.Contains("target") || matched.Contains("file")) return where;
        bool Has(string text) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        return matched[0] switch
        {
            "rootName" => "NAME " + Short(definition.Definition.RootName, 128),
            "binding" => "binding " + Short(DefinitionSummary!.SearchFields.First(f => f.Field == "binding" && Has(f.Text)).Text, 128),
            _ => AnimationTreeSummary.Label(DefinitionSummary!.Start.Kind),
        };
    }
    internal void NotifyAnimation()
    {
        foreach (string name in new[] { nameof(OutlinerIcon), nameof(OutlinerBrush), nameof(TargetRunText), nameof(TargetQuietRunText), nameof(SecondaryRunText), nameof(Meta),
            nameof(MetaToolTip), nameof(CanFrame), nameof(CanFrameAnimation), nameof(HidesAnimationFrame), nameof(IsOpenedAnimation), nameof(OpenAnimationToolTip),
            nameof(IsKeptAnimation), nameof(KeptToolTip), nameof(AffectsSelection), nameof(ShowsBoundMark), nameof(HoldsBoundMark), nameof(BoundMarkBrush),
            nameof(HasDuplicateName), nameof(DuplicateToolTip), nameof(ToolTip), nameof(ToolTipTitle), nameof(ToolTipBody), nameof(AccessibleName), nameof(HelpText),
            nameof(Label), nameof(HasGroupByStart), nameof(AnimationsByStartOn), nameof(GroupByStartName), nameof(IsUnboundDefinition) }) Changed(name);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
