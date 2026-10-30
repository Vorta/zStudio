using System.Globalization;

namespace zStudio.Core.Animation;

/// <summary>
/// The Structure view's model of one opened binding (docs/agents/preview.md, "Animation structure"): its activation, its
/// definition frame (cleanup, then runtime sequences in source order) with rows, condition and loop blocks, the reference
/// nodes of the bindings it calls or names (one node per definition, collapsed unless expanded; an expanded one is one frame
/// showing one of its bindings at a time), the bindings that call it, and the edges between them. Calls and by-name events resolve as the runtime does
/// (the first entry of the name); a call to a binding already on its call path is a back-link. Deterministic, bounded and
/// paged; identities are entries, sequence and event GUIDs of this compilation, never names. Read-only and immutable.
/// </summary>
public sealed class AnimationGraph
{
    public const int MaximumPageSize = 100;
    /// <summary>Expanded reference nodes (one frame each) a graph shows at once.</summary>
    public const int MaximumExpandedFrames = 64;
    /// <summary>Events one grouped row or one bundled edge lists.</summary>
    public const int MaximumMembers = 100;
    /// <summary>Rows a sequence node shows before "Show all n events".</summary>
    public const int VisibleRows = 12;

    private readonly List<AnimationGraphNode> nodes = [];
    private readonly Dictionary<string, AnimationGraphNode> nodeById = new(StringComparer.Ordinal);
    private readonly List<AnimationGraphEdge> edges = [];
    private readonly Dictionary<(int Entry, Guid Sequence), SequenceLayout> layouts = [];
    private readonly HashSet<string> expanded;
    private readonly Dictionary<string, int> members;

    public AnimationGraphIndex Index { get; }
    /// <summary>The opened binding: the scenario's primary start, whose instance path is "".</summary>
    public int Entry { get; }
    public Guid Id { get; } = Guid.NewGuid();
    public IReadOnlySet<string> Expanded => expanded;
    /// <summary>The member each expanded group's frame shows, where one other than its first was chosen.</summary>
    public IReadOnlyDictionary<string, int> Members => members;
    public int NodeCount => nodes.Count;
    /// <summary>Some expanded groups have no frame: the graph already shows <see cref="MaximumExpandedFrames"/> expanded frames.</summary>
    public bool ExpansionTruncated { get; private set; }
    public int EdgeCount => edges.Count;

    private AnimationGraph(AnimationGraphIndex index, int entry, HashSet<string> expanded, Dictionary<string, int> members, CancellationToken token)
    {
        Index = index; Entry = entry; this.expanded = expanded; this.members = members;
        new Builder(this, token).Build();
    }

    /// <summary>
    /// The graph of <paramref name="entry"/>, with the reference groups in <paramref name="expanded"/> shown as one frame each,
    /// on the member <paramref name="members"/> chooses (else the group's first).
    /// </summary>
    public static AnimationGraph Create(AnimationGraphIndex index, int entry, IEnumerable<string>? expanded = null, CancellationToken token = default,
        IReadOnlyDictionary<string, int>? members = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (entry <= 0 || entry >= index.Package.Entries.Count) throw new InvalidDataException("Choose an animation binding of this package (entry 0 is the compiler's placeholder).");
        return new(index, entry, new(expanded ?? [], StringComparer.Ordinal), members == null ? new(StringComparer.Ordinal) : new(members, StringComparer.Ordinal), token);
    }

    /// <summary>The same graph with a reference group expanded or collapsed. Expansion shows; it never includes, runs or evaluates.</summary>
    public AnimationGraph WithExpanded(string group, bool value, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (value == expanded.Contains(group)) return this;
        HashSet<string> next = new(expanded, StringComparer.Ordinal);
        if (value) next.Add(group); else next.Remove(group);
        var graph = new AnimationGraph(Index, Entry, next, new(members, StringComparer.Ordinal), token);
        if (value && graph.ExpansionTruncated && !ExpansionTruncated)
            throw new InvalidOperationException($"Collapse another called animation first ({MaximumExpandedFrames} expanded).");
        return graph;
    }

    /// <summary>The same graph with an expanded group's frame showing <paramref name="entry"/>, one of its members.</summary>
    public AnimationGraph WithMember(string group, int entry, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (nodeById.GetValueOrDefault(ReferenceId(group))?.Reference is not { } reference || !reference.Entries.Contains(entry))
            throw new InvalidDataException("Choose one of the called animation's bindings.");
        if (members.TryGetValue(group, out int chosen) && chosen == entry) return this;
        return new(Index, Entry, new(expanded, StringComparer.Ordinal), new(members, StringComparer.Ordinal) { [group] = entry }, token);
    }

    /// <summary>Nodes in layout order (column, then order; a frame's activation and sequences follow it).</summary>
    public AnimationStructurePage<AnimationGraphNode> Nodes(int offset = 0, int limit = MaximumPageSize)
    {
        CheckPage(offset, limit);
        return Page(nodes.Skip(offset).Take(limit).ToList(), nodes.Count, offset);
    }
    public AnimationGraphNode? Node(string id) => nodeById.GetValueOrDefault(id);
    /// <summary>The node that shows <paramref name="entry"/>: its frame, else the reference node holding it.</summary>
    public AnimationGraphNode? NodeOf(int entry) =>
        nodeById.GetValueOrDefault(FrameId(entry)) ?? nodes.FirstOrDefault(n => n.Reference?.Entries.Contains(entry) == true);
    public AnimationStructurePage<AnimationGraphEdge> Edges(int offset = 0, int limit = MaximumPageSize)
    {
        CheckPage(offset, limit);
        return Page(edges.Skip(offset).Take(limit).ToList(), edges.Count, offset);
    }
    public AnimationGraphEdge? Edge(string id) => edges.FirstOrDefault(e => e.Id == id);
    /// <summary>The edges leaving or entering a node or row.</summary>
    public IReadOnlyList<AnimationGraphEdge> EdgesOf(string id) => edges.Where(e => e.From == id || e.FromNode == id || e.To == id).ToArray();

    /// <summary>A shown sequence's rows (grouped), paged.</summary>
    public AnimationStructurePage<AnimationGraphRow> Rows(int entry, Guid sequence, int offset = 0, int limit = MaximumPageSize)
    {
        CheckPage(offset, limit);
        var layout = Layout(entry, sequence);
        return Page(layout.Rows.Skip(offset).Take(limit).ToList(), layout.Rows.Count, offset);
    }
    /// <summary>A shown sequence's rows with every grouped row split into its members.</summary>
    public AnimationStructurePage<AnimationGraphRow> Events(int entry, Guid sequence, int offset = 0, int limit = MaximumPageSize)
    {
        CheckPage(offset, limit);
        var layout = Layout(entry, sequence);
        return Page(layout.Events.Skip(offset).Take(limit).ToList(), layout.Events.Count, offset);
    }
    /// <summary>The row of an event in a shown sequence (its group when grouped).</summary>
    public AnimationGraphRow? Row(string id) => layouts.Values.Select(l => l.RowById.GetValueOrDefault(id)).FirstOrDefault(r => r != null);
    /// <summary>The row that shows <paramref name="eventId"/> (its grouped row) in a shown binding.</summary>
    public AnimationGraphRow? RowOf(int entry, Guid eventId) => layouts.Where(p => p.Key.Entry == entry)
        .Select(p => p.Value.RowByEvent.GetValueOrDefault(eventId)).FirstOrDefault(r => r != null);
    public IReadOnlyList<AnimationGraphBlock> Blocks(int entry, Guid sequence) => Layout(entry, sequence).Blocks;
    public AnimationGraphBlock? Block(string id) => layouts.Values.SelectMany(l => l.Blocks).FirstOrDefault(b => b.Id == id);
    /// <summary>Every condition (IF and ELSEIF) of the shown frames.</summary>
    public IEnumerable<(AnimationGraphFrame Frame, AnimationGraphBlock Block, AnimationGraphBranch Branch)> Conditions()
    {
        foreach (var node in nodes)
            if (node.Frame is { } frame)
                foreach (var sequence in Index.Package.Entries[frame.Entry].AllSequences)
                    if (layouts.TryGetValue((frame.Entry, sequence.Id), out var layout))
                        foreach (var block in layout.Blocks)
                            foreach (var branch in block.Branches)
                                if (branch.Condition != null) yield return (frame, block, branch);
    }

    /// <summary>The callers index for any binding of the map (the reverse lookup of calls and by-name events).</summary>
    public AnimationStructurePage<AnimationGraphCaller> Callers(int entry, int offset = 0, int limit = MaximumPageSize) => Index.Callers(entry, offset, limit);

    /// <summary>
    /// A binding's activation as the graph's Activation node shows it (triggers, gates, reset behaviour, prerequisites), without
    /// its callers: for an Inspector or definition card of a binding the shown graph does not hold.
    /// </summary>
    public static AnimationGraphActivation Activation(AnimationGraphIndex index, int entry)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (entry <= 0 || entry >= index.Package.Entries.Count) throw new ArgumentOutOfRangeException(nameof(entry));
        return AnimationGraphRows.Activation(index, index.Package.Entries[entry], [], 0);
    }

    public static string FrameId(int entry) => "frame:" + entry.ToString(CultureInfo.InvariantCulture);
    /// <summary>A runtime sequence's compiled number as a compiled file lists it (0 is the first after the cleanup), or -1.</summary>
    public static int CompiledNumber(AnimationEntry entry, Guid sequence)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int i = 0;
        foreach (var candidate in entry.AllSequences) { if (candidate.Id == sequence) return i - 1; i++; }
        return -1;
    }
    public static string ActivationId(int entry) => "activation:" + entry.ToString(CultureInfo.InvariantCulture);
    public static string SequenceId(int entry, Guid sequence) => string.Create(CultureInfo.InvariantCulture, $"sequence:{entry}:{sequence:N}");
    public static string RowId(int entry, Guid ev) => string.Create(CultureInfo.InvariantCulture, $"row:{entry}:{ev:N}");
    public static string ReferenceId(string group) => "ref:" + group;

    /// <summary>
    /// The runtime sequence of its own binding an event acts on, as the player resolves it (slot, else the first of the name):
    /// CALL_SEQUENCE releases, STOP_SEQUENCE stops, a motion with contact release (flag 0x800) releases on ground contact.
    /// </summary>
    public static (AnimationGraphRelation Kind, AnimationSequence Sequence)? SequenceTarget(AnimationEntry entry, AnimationEvent ev)
    {
        (AnimationGraphRelation Kind, string Name, int Slot)? intra = ev.Type switch
        {
            22 when ev.Bytes.Length >= 48 => (AnimationGraphRelation.Release, ev.Text(12), ev.I32(44)),
            23 when ev.Bytes.Length >= 48 => (AnimationGraphRelation.StopSequence, ev.Text(12), ev.I32(44)),
            10 when ev.Bytes.Length >= 242 && (ev.U32(12) & 0x800) != 0 => (AnimationGraphRelation.ContactRelease, ev.Text(208), ev.I16(240)),
            _ => null,
        };
        if (intra is not { } found) return null;
        var target = found.Slot >= 0 && found.Slot < entry.Sequences.Count ? entry.Sequences[found.Slot] : entry.Sequences.FirstOrDefault(s => s.Name == found.Name);
        return target == null ? null : (found.Kind, target);
    }

    private SequenceLayout Layout(int entry, Guid sequence) => layouts.TryGetValue((entry, sequence), out var layout) ? layout
        : throw new InvalidDataException("The animation sequence is not shown in this graph.");

    internal static void CheckPage(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit), "Use offset >= 0 and limit 1–100.");
    }
    internal static AnimationStructurePage<T> Page<T>(List<T> rows, int total, int offset) =>
        new(rows.AsReadOnly(), total, offset, rows.Count < total - (long)offset ? offset + rows.Count : null);

    internal sealed record SequenceLayout(AnimationGraphSequence Sequence, IReadOnlyList<AnimationGraphRow> Rows, IReadOnlyList<AnimationGraphRow> Events,
        IReadOnlyList<AnimationGraphBlock> Blocks, IReadOnlyDictionary<string, AnimationGraphRow> RowById, IReadOnlyDictionary<Guid, AnimationGraphRow> RowByEvent);

    /// <summary>
    /// Lays the graph out breadth first by call depth: each frame's related bindings join one reference node per definition
    /// (first relation first), an expanded reference node adds one frame for its chosen binding, and only frames add nodes;
    /// then every edge.
    /// </summary>
    private sealed class Builder(AnimationGraph graph, CancellationToken token)
    {
        private readonly AnimationGraphIndex index = graph.Index;
        private AnimationPackage Package => index.Package;
        private readonly List<FrameState> frames = [];
        private readonly Dictionary<int, FrameState> frameOf = [];
        private readonly Dictionary<string, GroupState> groups = new(StringComparer.Ordinal);
        private readonly List<GroupState> groupOrder = [];
        /// <summary>The reference node each binding on screen belongs to (the opened binding has none).</summary>
        private readonly Dictionary<int, GroupState> groupOfEntry = [];
        private readonly Dictionary<int, string> firstPath = [];
        private readonly Dictionary<string, EdgeState> edgeStates = new(StringComparer.Ordinal);
        private readonly List<EdgeState> edgeOrder = [];

        private sealed class FrameState(int entry, string? path, int depth, int column, int[] chain, GroupState? group)
        {
            public int Entry { get; } = entry; public string? Path { get; } = path; public int Depth { get; } = depth;
            public int Column { get; } = column; public int[] Chain { get; } = chain; public GroupState? Group { get; } = group;
        }
        private sealed class GroupState(string key, int column, int order, int depth, FrameState? discoverer, bool calling)
        {
            public string Key { get; } = key; public int Column { get; } = column; public int Order { get; } = order; public int Depth { get; } = depth;
            public FrameState? Discoverer { get; } = discoverer; public bool Calling { get; } = calling;
            public List<int> Entries { get; } = [];
            public string? UnresolvedName { get; init; }
            /// <summary>The frame of an expanded group (its shown member).</summary>
            public FrameState? Frame { get; set; }
            public string Id => ReferenceId(Key);
        }
        private sealed class EdgeState(AnimationGraphRelation relation, string from, string fromNode, string to, bool backLink)
        {
            public AnimationGraphRelation Relation { get; } = relation; public string From { get; } = from; public string FromNode { get; } = fromNode;
            public string To { get; } = to; public bool BackLink { get; } = backLink;
            public int Count; public bool Wait, Local; public string? Note;
            public List<Guid> Events { get; } = []; public List<string> Paths { get; } = []; public SortedSet<int> Targets { get; } = []; public SortedSet<int> AlsoNamed { get; } = [];
        }

        public void Build()
        {
            var root = new FrameState(graph.Entry, "", 0, 1, [graph.Entry], null);
            AddFrame(root);
            List<FrameState> level = [root];
            int expandedFrames = 0;
            while (level.Count > 0)
            {
                List<GroupState> touched = [];
                foreach (var frame in level) Discover(frame, touched);
                List<FrameState> next = [];
                foreach (var group in touched.Distinct())
                {
                    // An expanded group is one frame: its chosen member (else its first), so it counts once against the budget.
                    if (group.UnresolvedName != null || !graph.expanded.Contains(group.Key) || group.Frame != null) continue;
                    var candidates = group.Entries.Where(e => !frameOf.ContainsKey(e)).Order().ToList();
                    if (candidates.Count == 0) continue;
                    if (expandedFrames >= MaximumExpandedFrames) { graph.ExpansionTruncated = true; continue; }
                    int entry = graph.members.TryGetValue(group.Key, out int chosen) && candidates.Contains(chosen) ? chosen : candidates[0];
                    var discoverer = group.Discoverer!;
                    var frame = new FrameState(entry, firstPath.GetValueOrDefault(entry), group.Depth, group.Column + 1, [.. discoverer.Chain, entry], group);
                    group.Frame = frame;
                    AddFrame(frame); next.Add(frame); expandedFrames++;
                }
                level = next;
            }
            AddCallingGroups();
            foreach (var frame in frames) FrameEdges(frame);
            foreach (var group in groupOrder) GroupEdges(group);
            foreach (var frame in frames) PrerequisiteEdges(frame);
            foreach (var edge in edgeOrder) graph.edges.Add(Finish(edge));
            PublishNodes();
        }

        private void AddFrame(FrameState frame)
        {
            frames.Add(frame); frameOf[frame.Entry] = frame;
            var entry = Package.Entries[frame.Entry];
            bool many = 1 + entry.Sequences.Count > 12; int order = 0;
            foreach (var sequence in Shown(entry))
            {
                token.ThrowIfCancellationRequested();
                graph.layouts[(entry.Index, sequence.Id)] = AnimationGraphRows.Layout(index, entry, sequence, order, many && order >= 6, token);
                order++;
            }
        }
        /// <summary>The cleanup (when it has events or a source record), then the runtime sequences in source order.</summary>
        private IEnumerable<AnimationSequence> Shown(AnimationEntry entry)
        {
            if (entry.Primary.Events.Count > 0 || index.Provenance?.FindSequence(entry.Primary.Id)?.RecordId != null) yield return entry.Primary;
            foreach (var sequence in entry.Sequences) yield return sequence;
        }

        private void Discover(FrameState frame, List<GroupState> touched)
        {
            var entry = Package.Entries[frame.Entry];
            foreach (var sequence in Shown(entry))
                foreach (var ev in sequence.Events)
                {
                    token.ThrowIfCancellationRequested();
                    if (index.Relate(ev, token) is not { } relation) continue;
                    if (relation.Target is not int target)
                    {
                        var unresolved = Group("u:" + relation.Name, frame.Depth * 2 + 2, frame.Depth + 1, frame, false, relation.Name);
                        touched.Add(unresolved);
                        continue;
                    }
                    if (frame.Chain.Contains(target) || frameOf.ContainsKey(target)) continue;
                    if (relation.Kind is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait && frame.Path != null && !firstPath.ContainsKey(target))
                        if (Path(frame.Path, frame.Entry, ev.Id, target) is { } path) firstPath[target] = path;
                    if (groupOfEntry.TryGetValue(target, out var known)) { touched.Add(known); continue; }
                    var group = Group(index.Group(target), frame.Depth * 2 + 2, frame.Depth + 1, frame, false);
                    group.Entries.Add(target); groupOfEntry[target] = group; touched.Add(group);
                }
        }
        private GroupState Group(string key, int column, int depth, FrameState? discoverer, bool calling, string? unresolved = null)
        {
            if (groups.TryGetValue(key, out var group)) return group;
            group = new(key, column, groupOrder.Count(g => g.Column == column), depth, discoverer, calling) { UnresolvedName = unresolved };
            groups.Add(key, group); groupOrder.Add(group);
            return group;
        }

        /// <summary>Bindings that call or name the opened binding without being on screen: reference nodes under its activation.</summary>
        private void AddCallingGroups()
        {
            foreach (var caller in index.AllCallers(graph.Entry))
            {
                token.ThrowIfCancellationRequested();
                if (caller.Caller == graph.Entry || frameOf.ContainsKey(caller.Caller) || groupOfEntry.ContainsKey(caller.Caller)) continue;
                var group = Group(index.Group(caller.Caller), 0, 0, null, true);
                if (!group.Calling) continue;
                group.Entries.Add(caller.Caller); groupOfEntry[caller.Caller] = group;
            }
        }

        private void FrameEdges(FrameState frame)
        {
            var entry = Package.Entries[frame.Entry];
            foreach (var sequence in Shown(entry))
            {
                var layout = graph.layouts[(entry.Index, sequence.Id)];
                string node = SequenceId(entry.Index, sequence.Id);
                bool cleanup = sequence == entry.Primary;
                for (int i = 0; i < sequence.Events.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var ev = sequence.Events[i];
                    string from = layout.RowByEvent.TryGetValue(ev.Id, out var row) ? row.Id : RowId(entry.Index, ev.Id);
                    if (index.Relate(ev, token) is { } relation)
                    {
                        bool local = AnimationPhrases.IsLocalChild(entry, ev);
                        var kind = relation.Kind == AnimationGraphRelation.Call && local ? AnimationGraphRelation.LocalChild : relation.Kind;
                        if (relation.Target is not int target) { Add(kind, from, node, ReferenceId("u:" + relation.Name), false, ev, null, null, relation.Wait, local); continue; }
                        string? path = relation.Kind is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait && frame.Path != null
                            ? Path(frame.Path, entry.Index, ev.Id, target) : null;
                        int chained = Array.IndexOf(frame.Chain, target);
                        if (chained >= 0) Add(kind, from, node, ActivationId(frame.Chain[chained]), IsCall(kind), ev, path, target, relation.Wait, local, NameShares(relation));
                        else if (groupOfEntry.TryGetValue(target, out var group)) Add(kind, from, node, group.Id, false, ev, path, target, relation.Wait, local, NameShares(relation));
                        continue;
                    }
                    // Releases, sequence stops and contact releases act on this binding's runtime sequences, never during its cleanup.
                    if (SequenceTarget(entry, ev) is not { } intra) continue;
                    var edge = Add(intra.Kind, from, node, SequenceId(entry.Index, intra.Sequence.Id), false, ev, null, null, false, false);
                    if (cleanup) edge.Note = "no effect during cleanup: the runtime sequences do not run then";
                }
            }
        }

        /// <summary>A collapsed reference node's own relations, drawn only to nodes on screen and back to its call path.</summary>
        private void GroupEdges(GroupState group)
        {
            // An expanded group's frame draws its shown member's relations from its rows.
            if (group.UnresolvedName != null || group.Frame != null) return;
            foreach (int member in group.Entries)
            {
                if (frameOf.ContainsKey(member)) continue;
                var entry = Package.Entries[member];
                int[] chain = group.Calling ? [] : [.. group.Discoverer!.Chain, member];
                foreach (var sequence in entry.AllSequences)
                    foreach (var ev in sequence.Events)
                    {
                        token.ThrowIfCancellationRequested();
                        if (index.Relate(ev, token) is not { Target: int target } relation) continue;
                        bool local = AnimationPhrases.IsLocalChild(entry, ev);
                        var kind = relation.Kind == AnimationGraphRelation.Call && local ? AnimationGraphRelation.LocalChild : relation.Kind;
                        string? path = relation.Kind is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait && firstPath.TryGetValue(member, out var parent)
                            ? Path(parent, member, ev.Id, target) : null;
                        if (target == member) { Add(kind, group.Id, group.Id, group.Id, IsCall(kind), ev, path, target, relation.Wait, local); continue; }
                        if (group.Calling && target == graph.Entry) { Add(kind, group.Id, group.Id, ActivationId(graph.Entry), false, ev, null, target, relation.Wait, local, NameShares(relation)); continue; }
                        int chained = Array.IndexOf(chain, target);
                        if (chained >= 0) { Add(kind, group.Id, group.Id, ActivationId(chain[chained]), IsCall(kind), ev, path, target, relation.Wait, local, NameShares(relation)); continue; }
                        if (frameOf.TryGetValue(target, out var frame) && frame.Group == null) continue;
                        if (groupOfEntry.TryGetValue(target, out var other)) Add(kind, group.Id, group.Id, other.Id, false, ev, path, target, relation.Wait, local, NameShares(relation));
                    }
            }
        }

        private void PrerequisiteEdges(FrameState frame)
        {
            var entry = Package.Entries[frame.Entry];
            foreach (var record in entry.References[6])
            {
                if (record.Bytes.Length < 48 || record.Bytes[4] != 1 || index.First(record.Text(8, 32)) is not int target) continue;
                string? from = frameOf.TryGetValue(target, out var shown) && shown.Group == null ? ActivationId(target)
                    : groupOfEntry.TryGetValue(target, out var group) ? group.Id : null;
                if (from == null) continue;
                var edge = Edge(AnimationGraphRelation.Prerequisite, from, from, ActivationId(frame.Entry), false);
                edge.Count++; edge.Targets.Add(target);
            }
        }

        private IReadOnlyList<int> NameShares(AnimationGraphIndex.Relation relation) =>
            relation.Kind is AnimationGraphRelation.StopAnimation or AnimationGraphRelation.ResetAnimation or AnimationGraphRelation.InvalidateAnimation
                ? index.Named(relation.Name).Where(e => e != relation.Target).Take(MaximumMembers).ToArray() : [];

        private EdgeState Add(AnimationGraphRelation relation, string from, string fromNode, string to, bool backLink, AnimationEvent ev, string? path, int? target,
            bool wait, bool local, IReadOnlyList<int>? alsoNamed = null)
        {
            var edge = Edge(relation, from, fromNode, to, backLink);
            edge.Count++; edge.Wait |= wait; edge.Local |= local;
            if (edge.Events.Count < MaximumMembers) edge.Events.Add(ev.Id);
            if (path != null && edge.Paths.Count < MaximumMembers && !edge.Paths.Contains(path)) edge.Paths.Add(path);
            if (target is int t) edge.Targets.Add(t);
            foreach (int other in alsoNamed ?? []) if (edge.AlsoNamed.Count < MaximumMembers) edge.AlsoNamed.Add(other);
            return edge;
        }
        private EdgeState Edge(AnimationGraphRelation relation, string from, string fromNode, string to, bool backLink)
        {
            string id = $"{Name(relation)}:{from}>{to}{(backLink ? ":back" : "")}";
            if (edgeStates.TryGetValue(id, out var edge)) return edge;
            edge = new(relation, from, fromNode, to, backLink);
            edgeStates.Add(id, edge); edgeOrder.Add(edge);
            return edge;
        }
        private static AnimationGraphEdge Finish(EdgeState edge) =>
            new($"{Name(edge.Relation)}:{edge.From}>{edge.To}{(edge.BackLink ? ":back" : "")}", edge.Relation, edge.From, edge.FromNode, edge.To, edge.Count, Label(edge))
            {
                BackLink = edge.BackLink, Wait = edge.Wait, Local = edge.Local, Note = edge.Note,
                Events = edge.Events.AsReadOnly(), Paths = edge.Paths.AsReadOnly(), TargetEntries = edge.Targets.ToArray(), AlsoNamed = edge.AlsoNamed.ToArray(),
            };
        private static string? Label(EdgeState edge)
        {
            string? label = edge.BackLink ? "calls back (cycle)" : edge.Relation switch
            {
                AnimationGraphRelation.CallWait => "waits",
                AnimationGraphRelation.LocalChild => "local · stops with caller",
                AnimationGraphRelation.Release => "release",
                AnimationGraphRelation.ContactRelease => "on contact",
                AnimationGraphRelation.StopSequence or AnimationGraphRelation.StopAnimation => "stop",
                AnimationGraphRelation.ResetAnimation => "reset",
                AnimationGraphRelation.InvalidateAnimation => "invalidate",
                AnimationGraphRelation.Prerequisite => "needed",
                _ => null,
            };
            if (edge.Count > 1) label = label == null ? $"×{edge.Count}" : $"{label} ×{edge.Count}";
            return label;
        }
        internal static string Name(AnimationGraphRelation relation) => relation switch
        {
            AnimationGraphRelation.Call => "call", AnimationGraphRelation.CallWait => "callWait", AnimationGraphRelation.LocalChild => "localChild",
            AnimationGraphRelation.Release => "release", AnimationGraphRelation.ContactRelease => "contactRelease", AnimationGraphRelation.StopSequence => "stopSequence",
            AnimationGraphRelation.StopAnimation => "stopAnimation", AnimationGraphRelation.ResetAnimation => "resetAnimation",
            AnimationGraphRelation.InvalidateAnimation => "invalidateAnimation", _ => "prerequisite",
        };
        private static bool IsCall(AnimationGraphRelation relation) => relation is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild;
        private static string? Path(string parent, int caller, Guid ev, int target)
        {
            try { return AnimationExecutionPolicy.ChildPath(parent, caller, ev, target); }
            catch (InvalidDataException) { return null; }
        }

        private void PublishNodes()
        {
            var root = frameOf[graph.Entry];
            Add(new(ActivationId(graph.Entry), AnimationGraphNodeKind.Activation, 0, 0, 0, "Activation")
                { Entry = graph.Entry, Activation = Activation(graph.Entry) });
            foreach (var group in groupOrder.Where(g => g.Calling)) Add(Reference(group));
            int columns = Math.Max(root.Column, groupOrder.Count == 0 ? 0 : groupOrder.Max(g => g.Column + 1));
            for (int column = 1; column <= columns; column++)
            {
                foreach (var group in groupOrder.Where(g => !g.Calling && g.Column == column).OrderBy(g => g.Order)) Add(Reference(group));
                int order = 0;
                foreach (var frame in frames.Where(f => f.Column == column))
                {
                    var entry = Package.Entries[frame.Entry];
                    string id = FrameId(frame.Entry);
                    var source = index.Provenance?.FindEntry(frame.Entry);
                    Add(new(id, AnimationGraphNodeKind.Frame, column, order++, frame.Depth, $"{entry.Name} · {entry.RootName} · #{entry.Index}"
                        + (source == null ? "" : " · " + System.IO.Path.GetFileName(source.File)) + (index.Options.CompiledNumbering ? " · read only" : ""))
                    {
                        Entry = frame.Entry, Parent = frame.Group?.Id,
                        Frame = new(frame.Entry, entry.Name, entry.RootName, entry.AttachName, frame.Path, frame.Depth, Shown(entry).Count())
                        {
                            File = source?.File, Source = source, Group = frame.Group?.Key,
                            Members = frame.Group == null ? [] : frame.Group.Entries.Order().Take(MaximumMembers).ToArray(),
                            MemberIndex = frame.Group == null ? 0 : Math.Max(0, frame.Group.Entries.Order().Take(MaximumMembers).ToList().IndexOf(frame.Entry)),
                        },
                    });
                    if (frame.Group != null)
                        Add(new(ActivationId(frame.Entry), AnimationGraphNodeKind.Activation, column, 0, frame.Depth, "Activation")
                            { Entry = frame.Entry, Parent = id, Activation = Activation(frame.Entry) });
                    foreach (var sequence in Shown(entry))
                    {
                        var info = graph.layouts[(entry.Index, sequence.Id)].Sequence;
                        // An expanded frame holds its activation first.
                        Add(new(SequenceId(entry.Index, sequence.Id), AnimationGraphNodeKind.Sequence, column, info.Index + (frame.Group != null ? 1 : 0), frame.Depth,
                            index.Options.CompiledNumbering && !info.Cleanup ? $"sequence {CompiledNumber(entry, sequence.Id)} · {info.Name}" : info.Name)
                            { Entry = frame.Entry, Parent = id, Sequence = info });
                    }
                }
            }
        }
        private void Add(AnimationGraphNode node) { graph.nodes.Add(node); graph.nodeById[node.Id] = node; }

        private AnimationGraphNode Reference(GroupState group)
        {
            string name = index.GroupName(group.Key);
            if (group.UnresolvedName != null)
            {
                string map = index.Options.Map is { Length: > 0 } m ? " in " + m : "";
                return new(group.Id, AnimationGraphNodeKind.Reference, group.Column, group.Order, group.Depth, name)
                {
                    Reference = new(group.Key, name, [], 0)
                    { Unresolved = true, Detail = $"No animation named {AnimationPhrases.Safe(group.UnresolvedName)}{map}", Summary = "Unavailable" },
                };
            }
            var members = group.Entries.Order().ToArray();
            var first = Package.Entries[members[0]];
            var source = index.Provenance?.FindEntry(first.Index);
            string file = source == null ? "" : System.IO.Path.GetFileName(source.File) + " · ";
            string roots = AnimationPhrases.Range(members.Select(m => Package.Entries[m].RootName).ToArray());
            string range = members.Length == 1 ? $"#{members[0]}" : members[^1] - members[0] == members.Length - 1 ? $"#{members[0]}–#{members[^1]}" : $"#{members[0]} … #{members[^1]}";
            int sequences = first.Sequences.Count, events = first.Sequences.Sum(s => s.Events.Count);
            string summary = $"{ShortActivation(first)} · {sequences} sequence{(sequences == 1 ? "" : "s")} · {events} event{(events == 1 ? "" : "s")}";
            List<AnimationGraphOutgoing> calls = [];
            foreach (int member in members)
                foreach (var ev in Package.Entries[member].AllSequences.SelectMany(s => s.Events))
                {
                    // One line of its own calls, by called definition.
                    if (index.Relate(ev, token) is not { Kind: AnimationGraphRelation.Call or AnimationGraphRelation.CallWait } relation) continue;
                    bool back = relation.Target is int t && (t == member || !group.Calling && group.Discoverer!.Chain.Contains(t));
                    string? node = relation.Target is int target && groupOfEntry.TryGetValue(target, out var other) ? other.Id
                        : relation.Target is int self && frameOf.ContainsKey(self) ? FrameId(self) : null;
                    string called = relation.Target is int resolved ? index.GroupName(index.Group(resolved)) : AnimationPhrases.Safe(relation.Name);
                    int at = calls.FindIndex(c => c.Name == called && c.BackLink == back);
                    if (at >= 0) calls[at] = calls[at] with { Events = calls[at].Events + 1 };
                    else if (calls.Count < MaximumMembers) calls.Add(new(called, 1, back, node));
                }
            HashSet<int> needed = [.. members.SelectMany(index.NeededBy)];
            var neededBy = needed.Order().Select(index.Group).Distinct().Take(MaximumMembers).Select(g =>
                index.Provenance?.FindEntry(index.GroupEntries(g)[0]) is { } at ? $"{index.GroupName(g)} ({System.IO.Path.GetFileName(at.File)})" : index.GroupName(g)).ToArray();
            string? caution = null;
            var named = index.Named(first.Name);
            int definitions = named.Select(index.Group).Distinct().Count();
            if (definitions > 1)
            {
                var picked = Package.Entries[named[0]];
                string where = index.Provenance?.FindEntry(picked.Index) is { } at ? System.IO.Path.GetFileName(at.File) + " " : "";
                caution = $"{definitions} definitions are named {AnimationPhrases.Safe(first.Name)}; calls use the first ({where}#{picked.Index})";
            }
            bool isExpanded = graph.expanded.Contains(group.Key) && !group.Calling;
            return new(group.Id, AnimationGraphNodeKind.Reference, group.Column, group.Calling ? group.Order + 1 : group.Order, group.Depth, $"{name} · {members.Length} binding{(members.Length == 1 ? "" : "s")}")
            {
                Reference = new(group.Key, name, members.Take(MaximumMembers).ToArray(), members.Length)
                {
                    Calling = group.Calling, Expanded = isExpanded, Expandable = !group.Calling, File = source?.File, Member = group.Frame?.Entry,
                    Detail = $"{file}{roots} · {range}", Summary = summary, Calls = calls.AsReadOnly(), NeededBy = neededBy, Caution = caution,
                },
            };
        }

        private AnimationGraphActivation Activation(int entryIndex)
        {
            var entry = Package.Entries[entryIndex];
            List<AnimationGraphCallerGroup> callers = [];
            var all = index.AllCallers(entryIndex);
            // Its own events (an invalidate of itself) are not callers.
            foreach (var group in all.Where(c => c.Caller != entryIndex).GroupBy(c => index.Group(c.Caller)))
            {
                string? node = groups.TryGetValue(group.Key, out var shown) ? shown.Id : group.FirstOrDefault(c => frameOf.ContainsKey(c.Caller)) is { } framed ? FrameId(framed.Caller) : null;
                string? back = node == null ? null : edgeOrder.FirstOrDefault(e => e.BackLink && e.To == ActivationId(entryIndex) && (e.FromNode == node || e.FromNode.StartsWith($"sequence:{group.First().Caller}:", StringComparison.Ordinal))) is { } edge
                    ? $"{Name(edge.Relation)}:{edge.From}>{edge.To}:back" : null;
                if (callers.Count < MaximumMembers) callers.Add(new(group.Key, index.GroupName(group.Key), group.Select(c => c.Caller).Distinct().Count(), group.Count(), node, back)
                    { Relations = group.Select(c => c.Relation).Distinct().Order().ToArray() });
            }
            return AnimationGraphRows.Activation(index, entry, callers, all.Count);
        }

        private string ShortActivation(AnimationEntry entry)
        {
            byte mode = entry.Bytes.Length > 153 ? entry.Bytes[153] : (byte)0;
            float health = entry.Bytes.Length >= 176 ? entry.F32(172) : 0;
            string text = mode switch
            {
                3 => "When called", 4 => "On startup", 1 => $"Collision hit · health {AnimationPhrases.Number(health)}",
                2 => $"Weapon or collision hit · health {AnimationPhrases.Number(health)}", _ => $"Weapon hit · health {AnimationPhrases.Number(health)}",
            };
            return index.StartsNewGame(entry.Index) ? "New game start · " + text : text;
        }
    }
}
