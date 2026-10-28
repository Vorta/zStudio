namespace zStudio.Core.Animation;

/// <summary>A point of the Structure view's layout, in DIP from its top left.</summary>
public readonly record struct AnimationGraphPoint(double X, double Y);

/// <summary>A rectangle of the Structure view's layout, in DIP.</summary>
public readonly record struct AnimationGraphRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public AnimationGraphPoint Center => new(X + Width / 2, Y + Height / 2);
    public bool Intersects(AnimationGraphRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
    public bool Contains(AnimationGraphPoint point) => point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;
    public AnimationGraphRect Union(AnimationGraphRect other)
    {
        double x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
        return new(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }
}

/// <summary>What one horizontal band of a laid-out node shows.</summary>
public enum AnimationGraphItemKind
{
    /// <summary>A sequence's state line ("Waits for release", "Cleanup · runs on stop or reset").</summary>
    StateLine,
    /// <summary>A collapsed sequence's strip of family glyphs with counts.</summary>
    Summary,
    Row,
    /// <summary>One event of an expanded grouped row.</summary>
    Member,
    /// <summary>An IF or ELSEIF question.</summary>
    Question,
    /// <summary>The outcome line under a question: outcome, pips and the Game | True | False switch.</summary>
    Outcome,
    /// <summary>A branch label: "if true", "else if", "if false"; an ELSE marker selects through it.</summary>
    Branch,
    /// <summary>The thin closing line of a condition block, which selects its ENDIF.</summary>
    EndIf,
    /// <summary>"Show all n events" under the first twelve rows.</summary>
    ShowAll,
    Trigger, Caller, PrerequisiteHeader, Prerequisite,
    /// <summary>"+n more" under the first prerequisites.</summary>
    More,
    /// <summary>A reserved run-overlay line (activation prerequisites, reference nodes).</summary>
    RunLine,
    /// <summary>A reference node's text line.</summary>
    Line,
}

/// <summary>
/// One band of a node at <see cref="Y"/> (relative to the node's top) and <see cref="Height"/>. Rows, questions, branch labels
/// and the closing line carry their graph row; condition items their block and branch; <see cref="Depth"/> is the condition
/// nesting (each level insets 6 DIP) and <see cref="Looped"/> marks rows a loop bracket spans.
/// </summary>
public sealed record AnimationGraphItem(AnimationGraphItemKind Kind, double Y, double Height)
{
    public AnimationGraphRow? Row { get; init; }
    public AnimationGraphBlock? Block { get; init; }
    public AnimationGraphBranch? Branch { get; init; }
    public int Depth { get; init; }
    public bool Looped { get; init; }
    /// <summary>ShowAll: the events not shown; More: the items not listed; Summary: the events.</summary>
    public int Count { get; init; }
    public string? Text { get; init; }
    public string? Keyword { get; init; }
    public AnimationGraphTrigger? Trigger { get; init; }
    public AnimationGraphCallerGroup? Caller { get; init; }
    public AnimationGraphPrerequisiteGroup? Group { get; init; }
    public AnimationGraphPrerequisite? Prerequisite { get; init; }
    public double Bottom => Y + Height;
}

/// <summary>A condition block's box or a loop's bracket in a sequence node (relative to the node's top).</summary>
public sealed record AnimationGraphBox(AnimationGraphBlock Block, double Top, double Bottom, int Depth);

/// <summary>A laid-out node: its bounds, its bands and boxes, and the frame and frame column it sits in.</summary>
public sealed record AnimationGraphPlacement(AnimationGraphNode Node, AnimationGraphRect Bounds)
{
    public string Id => Node.Id;
    /// <summary>The frame a sequence or an expanded frame's activation sits in.</summary>
    public string? Frame { get; init; }
    /// <summary>The column inside its frame (0 the leftmost), or −1 outside frames.</summary>
    public int Lane { get; init; } = -1;
    public bool Collapsed { get; init; }
    public double HeaderHeight { get; init; }
    public IReadOnlyList<AnimationGraphItem> Items { get; init; } = [];
    public IReadOnlyList<AnimationGraphBox> Boxes { get; init; } = [];
    /// <summary>Rows a sequence node does not show (collapsed, or beyond its first twelve).</summary>
    public int HiddenRows { get; init; }
}

/// <summary>How an edge ends at its target (design §8, "Edges").</summary>
public enum AnimationGraphEnd { Arrow, OpenArrow, TBar, Reset, Invalidate, Diamond }

/// <summary>Where an edge's label sits: before its end on the line, after its start, or not at all (its tooltip says it).</summary>
public enum AnimationGraphLabelAlign { None, End, Start }

/// <summary>
/// One routed edge: an orthogonal polyline (no segment crosses a node), its end mark, whether it leaves a row's out-port, and
/// where its label sits. Edges into one target share the last corridor lane, so they bundle into one trunk.
/// </summary>
public sealed record AnimationGraphRoute(AnimationGraphEdge Edge, IReadOnlyList<AnimationGraphPoint> Points, AnimationGraphEnd End)
{
    /// <summary>The edge leaves a shown row's out-port (calls carry their include toggle there).</summary>
    public bool FromPort { get; init; }
    /// <summary>The row whose port it leaves is hidden: it leaves the node's header or its Show all line.</summary>
    public bool FromHidden { get; init; }
    /// <summary>A by-name event naming its own binding ("Invalidate x · this animation"): it ends on its frame's header.</summary>
    public bool Self { get; init; }
    public AnimationGraphLabelAlign LabelAlign { get; init; }
    /// <summary>The label's anchor: its text ends there (<see cref="AnimationGraphLabelAlign.End"/>) or starts there, on its baseline.</summary>
    public AnimationGraphPoint Label { get; init; }
    /// <summary>The label's text ("release", "calls back (cycle) ×8", "invalidate · this animation"), shown once per bundle.</summary>
    public string? Text { get; init; }
    public AnimationGraphPoint Start => Points[0];
    public AnimationGraphPoint EndPoint => Points[^1];
}

/// <summary>The view's presentation choices the layout follows: collapsed and opened sequences, Show all, expanded grouped rows.</summary>
public sealed record AnimationGraphLayoutOptions
{
    public static AnimationGraphLayoutOptions Default { get; } = new();
    /// <summary>Sequence nodes collapsed to their header and summary strip.</summary>
    public IReadOnlySet<string> Collapsed { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Sequence nodes opened although collapsed by default (more than 12 sequences).</summary>
    public IReadOnlySet<string> Opened { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Sequence nodes showing every row ("Show all n events").</summary>
    public IReadOnlySet<string> ShowAll { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Grouped rows listing their members.</summary>
    public IReadOnlySet<string> ExpandedRows { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool IsCollapsed(AnimationGraphNode node) => node.Sequence is { } sequence
        && (Collapsed.Contains(node.Id) || sequence.CollapsedByDefault && !Opened.Contains(node.Id));
}

/// <summary>
/// The Structure view's deterministic layout (design §8, "Layout"): the activation column, the definition frame filled column
/// by column (232 wide, 24 gutters; a column takes nodes until it is as tall as the tallest node or 340 DIP), the called
/// animations' column, expanded frames to the right (call depth = column), and every edge routed orthogonally through the
/// gutters between columns, a frame's bottom channel, the outer channel above all frames (back-links) or the channel below
/// everything (prerequisites), so no edge crosses a node. The GUI draws it and MCP reports it; the same inputs give the
/// same layout. Sizes are DIP at 100 % zoom; the view's semantic zoom changes what nodes draw, never where they are.
/// </summary>
public sealed class AnimationGraphLayout
{
    public const double NodeWidth = 232, ColumnGap = 24, FrameGap = 48, ActivationGap = 40, Margin = 24, ColumnBudget = 340;
    public const double FrameHeader = 34, FramePadding = 12, FrameTop = 46, FrameBottom = 14;
    public const double Header = 28, StateHeight = 18, SummaryHeight = 20, RowHeight = 22, MemberHeight = 20, QuestionHeight = 26,
        OutcomeHeight = 24, BranchHeight = 20, EndIfHeight = 6, BlockMargin = 3, BodyTop = 4, BodyBottom = 5, BlockInset = 6;
    public const double TriggerHeight = 22, PrerequisiteHeaderHeight = 24, PrerequisiteHeight = 19, RunNoteHeight = 34, LineHeight = 18;
    public const int ShownPrerequisites = 8;
    /// <summary>Spacing of parallel edges in a gutter or a channel.</summary>
    public const double LaneSpacing = 4;

    private readonly List<AnimationGraphPlacement> nodes = [];
    private readonly Dictionary<string, AnimationGraphPlacement> nodeById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AnimationGraphPlacement Node, AnimationGraphItem Item)> itemByRow = new(StringComparer.Ordinal);
    private readonly List<AnimationGraphRoute> routes = [];

    public AnimationGraph Graph { get; }
    public AnimationGraphLayoutOptions Options { get; }
    public IReadOnlyList<AnimationGraphPlacement> Nodes => nodes;
    public IReadOnlyList<AnimationGraphRoute> Routes => routes;
    /// <summary>Everything drawn: nodes, frames, channels and labels, with the margin.</summary>
    public AnimationGraphRect Bounds { get; private set; }

    public AnimationGraphPlacement? Node(string id) => nodeById.GetValueOrDefault(id);
    /// <summary>The node and band that show a row (its question for an IF or ELSEIF, its branch label for an ELSE, its line for an ENDIF, a member line).</summary>
    public (AnimationGraphPlacement Node, AnimationGraphItem Item)? Item(string row) => itemByRow.TryGetValue(row, out var found) ? found : null;
    public AnimationGraphRoute? Route(string edge) => routes.FirstOrDefault(r => r.Edge.Id == edge);

    private AnimationGraphLayout(AnimationGraph graph, AnimationGraphLayoutOptions options, CancellationToken token)
    {
        Graph = graph; Options = options;
        new Builder(this, token).Build();
    }

    public static AnimationGraphLayout Create(AnimationGraph graph, AnimationGraphLayoutOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return new(graph, options ?? AnimationGraphLayoutOptions.Default, token);
    }

    private sealed class Builder(AnimationGraphLayout layout, CancellationToken token)
    {
        private readonly AnimationGraph graph = layout.Graph;
        private readonly AnimationGraphLayoutOptions options = layout.Options;
        private readonly List<AnimationGraphNode> all = [];
        private readonly Dictionary<string, Sized> sized = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FrameInfo> frames = new(StringComparer.Ordinal);
        /// <summary>Where each row's edges leave: its band, the Show all line or the header (relative y).</summary>
        private readonly Dictionary<string, (string Node, double Y, bool Shown)> anchors = new(StringComparer.Ordinal);
        /// <summary>The band that shows each row, by node.</summary>
        private readonly Dictionary<string, (string Node, AnimationGraphItem Item)> bands = new(StringComparer.Ordinal);
        private readonly HashSet<string> labelled = new(StringComparer.Ordinal);
        private readonly Dictionary<int, (double X, double Right)> columns = [];
        private readonly Dictionary<string, Lanes> lanes = new(StringComparer.Ordinal);
        private double top, bottomChannel;

        private sealed class Sized(AnimationGraphNode node, double height)
        {
            public AnimationGraphNode Node { get; } = node; public double Height { get; set; } = height;
            public List<AnimationGraphItem> Items { get; } = []; public List<AnimationGraphBox> Boxes { get; } = [];
            public bool Collapsed; public double HeaderHeight = Header; public int Hidden;
            public double X, Y; public string? Frame; public int Lane = -1;
            public AnimationGraphRect Rect => new(X, Y, NodeWidth, Height);
        }
        private sealed class FrameInfo(Sized frame)
        {
            public Sized Frame { get; } = frame;
            public List<List<Sized>> Columns { get; } = [];
            public double ColumnsHeight;
            public double Width => FramePadding * 2 + Columns.Count * NodeWidth + Math.Max(0, Columns.Count - 1) * ColumnGap;
            public double LaneX(int lane) => Frame.X + FramePadding + lane * (NodeWidth + ColumnGap);
            public double ChannelTop => Frame.Y + FrameTop + ColumnsHeight + 8;
        }
        /// <summary>Parallel lanes in a gutter or channel: one per key, in first-use order, wrapping at its capacity.</summary>
        private sealed class Lanes
        {
            private readonly Dictionary<string, int> keys = new(StringComparer.Ordinal);
            public int Count => keys.Count;
            public int Of(string key) { if (!keys.TryGetValue(key, out int lane)) keys.Add(key, lane = keys.Count); return lane; }
        }
        private Lanes LanesOf(string key) => lanes.TryGetValue(key, out var found) ? found : lanes[key] = new();

        public void Build()
        {
            for (int offset = 0; ;)
            {
                var page = graph.Nodes(offset);
                all.AddRange(page.Rows);
                if (page.NextOffset is not int next) break;
                offset = next;
            }
            foreach (var node in all)
            {
                token.ThrowIfCancellationRequested();
                sized[node.Id] = node.Kind switch
                {
                    AnimationGraphNodeKind.Sequence => SizeSequence(node),
                    AnimationGraphNodeKind.Activation => SizeActivation(node),
                    AnimationGraphNodeKind.Reference => SizeReference(node),
                    _ => new Sized(node, FrameHeader),
                };
            }
            // Each frame's nodes (its activation first for an expanded frame) fill columns as tall as its tallest node or 340 DIP.
            foreach (var node in all.Where(n => n.Kind == AnimationGraphNodeKind.Frame))
            {
                var info = frames[node.Id] = new(sized[node.Id]);
                var members = all.Where(n => n.Parent == node.Id && n.Kind != AnimationGraphNodeKind.Reference).Select(n => sized[n.Id]).ToList();
                double budget = Math.Max(ColumnBudget, members.Count == 0 ? 0 : members.Max(m => m.Height)), y = 0;
                info.Columns.Add([]);
                foreach (var member in members)
                {
                    if (y > 0 && y + member.Height > budget) { info.Columns.Add([]); y = 0; }
                    member.Frame = node.Id; member.Lane = info.Columns.Count - 1;
                    info.Columns[^1].Add(member);
                    y += member.Height + FramePadding;
                }
                info.ColumnsHeight = info.Columns.Max(c => c.Count == 0 ? 0 : c.Sum(m => m.Height) + (c.Count - 1) * FramePadding);
            }
            // Lanes are allocated in edge order before positions, so frames and channels reserve their room.
            var edges = AllEdges();
            foreach (var edge in edges) Plan(edge, false);
            Place();
            foreach (var edge in edges)
            {
                token.ThrowIfCancellationRequested();
                if (Plan(edge, true) is { } route) layout.routes.Add(route);
            }
            Publish();
        }

        private List<AnimationGraphEdge> AllEdges()
        {
            List<AnimationGraphEdge> edges = [];
            for (int offset = 0; ;)
            {
                var page = graph.Edges(offset);
                edges.AddRange(page.Rows);
                if (page.NextOffset is not int next) return edges;
                offset = next;
            }
        }

        // ------------------------------------------------------------ node sizes and bands

        private Sized SizeSequence(AnimationGraphNode node)
        {
            var sequence = node.Sequence!;
            Sized result = new(node, 0) { Collapsed = options.IsCollapsed(node) };
            double y = Header;
            if (sequence.StateLine != null) { result.Items.Add(new(AnimationGraphItemKind.StateLine, y, StateHeight) { Text = sequence.StateLine }); y += StateHeight; }
            var rows = AllRows(sequence);
            Dictionary<Guid, AnimationGraphRow> members = [];
            if (rows.Any(r => r.Members.Count > 1 && options.ExpandedRows.Contains(r.Id)))
                foreach (var member in AllEvents(sequence)) members[member.Event] = member;
            var blocks = graph.Blocks(sequence.Entry, sequence.Id);
            var loops = blocks.Where(b => b.Loop != null).ToArray();
            bool Looped(AnimationGraphRow row) => loops.Any(l => row.Index >= l.First && row.Index <= l.Last);
            if (result.Collapsed)
            {
                result.Items.Add(new(AnimationGraphItemKind.Summary, y, SummaryHeight) { Count = sequence.EventCount });
                foreach (var row in rows) AnchorRow(node.Id, row, Header / 2, false);
                result.Hidden = rows.Count;
                result.Height = y + SummaryHeight + 2;
                return result;
            }
            y += BodyTop;
            bool all = options.ShowAll.Contains(node.Id);
            int shown = 0;
            Stack<(AnimationGraphBlock Block, double Top, int Depth)> open = [];
            List<(AnimationGraphBlock Block, double Top, double Bottom)> loopSpans = [];
            double loopTop = double.NaN;
            for (int i = 0; i < rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = rows[i];
                if (!all && shown >= AnimationGraph.VisibleRows)
                {
                    // Close what is open, then "Show all n events"; hidden rows leave through that line.
                    while (open.Count > 0) { var (block, blockTop, depth) = open.Pop(); y += BlockMargin; result.Boxes.Add(new(block, blockTop, y, depth)); }
                    int hiddenEvents = rows.Skip(i).Sum(r => Math.Max(1, r.Members.Count));
                    var more = new AnimationGraphItem(AnimationGraphItemKind.ShowAll, y, RowHeight) { Count = hiddenEvents };
                    result.Items.Add(more);
                    foreach (var hidden in rows.Skip(i)) AnchorRow(node.Id, hidden, y + RowHeight / 2, false);
                    result.Hidden = rows.Count - i;
                    y += RowHeight;
                    break;
                }
                shown++;
                bool looped = Looped(row);
                if (looped && double.IsNaN(loopTop)) loopTop = y;
                int level = open.Count;
                switch (row.Role)
                {
                    case AnimationGraphRowRole.Question when row.Type == 31 && graph.Block(row.Block ?? "") is { } block:
                        {
                            y += BlockMargin;
                            open.Push((block, y - BlockMargin, level + 1));
                            var branch = block.Branches.FirstOrDefault(b => b.Marker == row.Event);
                            y = Question(result, node.Id, row, block, branch, level + 1, looped, y);
                            break;
                        }
                    case AnimationGraphRowRole.Question when open.Count > 0:
                        {
                            var block = open.Peek().Block;
                            var branch = block.Branches.FirstOrDefault(b => b.Marker == row.Event);
                            y = Question(result, node.Id, row, block, branch, level, looped, y);
                            break;
                        }
                    case AnimationGraphRowRole.Else when open.Count > 0:
                        {
                            var block = open.Peek().Block;
                            var branch = block.Branches.FirstOrDefault(b => b.Marker == row.Event);
                            var item = new AnimationGraphItem(AnimationGraphItemKind.Branch, y, BranchHeight) { Row = row, Block = block, Branch = branch, Depth = level, Looped = looped };
                            result.Items.Add(item); Anchor(node.Id, row, item);
                            y += BranchHeight;
                            break;
                        }
                    case AnimationGraphRowRole.EndIf when open.Count > 0:
                        {
                            var (block, blockTop, depth) = open.Pop();
                            if (block.Branches.Count > 0 && block.Branches[^1].Implicit)
                            {
                                result.Items.Add(new(AnimationGraphItemKind.Branch, y, BranchHeight) { Block = block, Branch = block.Branches[^1], Depth = depth, Looped = looped });
                                y += BranchHeight;
                            }
                            var item = new AnimationGraphItem(AnimationGraphItemKind.EndIf, y, EndIfHeight) { Row = row, Block = block, Depth = depth, Looped = looped };
                            result.Items.Add(item); Anchor(node.Id, row, item);
                            y += EndIfHeight + BlockMargin;
                            result.Boxes.Add(new(block, blockTop, y - BlockMargin, depth));
                            break;
                        }
                    default:
                        {
                            var item = new AnimationGraphItem(AnimationGraphItemKind.Row, y, RowHeight) { Row = row, Depth = level, Looped = looped };
                            result.Items.Add(item); Anchor(node.Id, row, item);
                            y += RowHeight;
                            if (row.Members.Count > 1 && options.ExpandedRows.Contains(row.Id))
                                foreach (Guid member in row.Members)
                                    if (members.TryGetValue(member, out var memberRow))
                                    {
                                        var line = new AnimationGraphItem(AnimationGraphItemKind.Member, y, MemberHeight) { Row = memberRow, Depth = level, Looped = looped };
                                        result.Items.Add(line); Anchor(node.Id, memberRow, line, edges: false);
                                        y += MemberHeight;
                                    }
                            break;
                        }
                }
                if (row.Role == AnimationGraphRowRole.Loop && graph.Block(row.Block ?? "") is { Loop: not null } loopBlock && !double.IsNaN(loopTop))
                    loopSpans.Add((loopBlock, loopTop, y));
            }
            while (open.Count > 0)
            {
                // An IF without ENDIF: its box closes with the sequence.
                var (block, blockTop, depth) = open.Pop();
                y += BlockMargin; result.Boxes.Add(new(block, blockTop, y, depth));
            }
            // A loop whose LOOP row is beyond the first twelve still brackets what is shown.
            foreach (var loop in loops.Where(l => loopSpans.All(s => s.Block.Id != l.Id)))
                if (!double.IsNaN(loopTop)) loopSpans.Add((loop, loopTop, y));
            foreach (var (block, spanTop, spanBottom) in loopSpans) result.Boxes.Add(new(block, spanTop, spanBottom, 0));
            result.Height = y + BodyBottom;
            return result;
        }

        private double Question(Sized node, string id, AnimationGraphRow row, AnimationGraphBlock block, AnimationGraphBranch? branch, int depth, bool looped, double y)
        {
            var question = new AnimationGraphItem(AnimationGraphItemKind.Question, y, QuestionHeight) { Row = row, Block = block, Branch = branch, Depth = depth, Looped = looped };
            node.Items.Add(question); Anchor(id, row, question);
            y += QuestionHeight;
            node.Items.Add(new(AnimationGraphItemKind.Outcome, y, OutcomeHeight) { Row = row, Block = block, Branch = branch, Depth = depth, Looped = looped });
            y += OutcomeHeight;
            node.Items.Add(new(AnimationGraphItemKind.Branch, y, BranchHeight) { Block = block, Branch = branch, Depth = depth, Looped = looped });
            return y + BranchHeight;
        }

        private void Anchor(string node, AnimationGraphRow row, AnimationGraphItem item, bool edges = true)
        {
            bands[row.Id] = (node, item);
            if (edges) anchors[row.Id] = (node, item.Y + item.Height / 2, true);
        }
        private void AnchorRow(string node, AnimationGraphRow row, double y, bool shown)
        {
            anchors[row.Id] = (node, y, shown);
            foreach (Guid member in row.Members) anchors.TryAdd(AnimationGraph.RowId(row.Entry, member), (node, y, shown));
        }

        private List<AnimationGraphRow> AllRows(AnimationGraphSequence sequence)
        {
            List<AnimationGraphRow> rows = [];
            for (int offset = 0; ;)
            {
                var page = graph.Rows(sequence.Entry, sequence.Id, offset);
                rows.AddRange(page.Rows);
                if (page.NextOffset is not int next) return rows;
                offset = next;
            }
        }
        private List<AnimationGraphRow> AllEvents(AnimationGraphSequence sequence)
        {
            List<AnimationGraphRow> rows = [];
            for (int offset = 0; ;)
            {
                var page = graph.Events(sequence.Entry, sequence.Id, offset);
                rows.AddRange(page.Rows);
                if (page.NextOffset is not int next) return rows;
                offset = next;
            }
        }

        private Sized SizeActivation(AnimationGraphNode node)
        {
            var activation = node.Activation!;
            Sized result = new(node, 0);
            double y = Header;
            foreach (var trigger in activation.Triggers)
            {
                result.Items.Add(new(AnimationGraphItemKind.Trigger, y, TriggerHeight) { Trigger = trigger, Text = trigger.Phrase, Keyword = trigger.Keyword });
                y += TriggerHeight;
            }
            // Callers that are also called from here point at their back-link; the others are reference nodes under the activation.
            foreach (var caller in activation.Callers.Where(c => c.Node != null && graph.Node(c.Node)?.Reference?.Calling != true))
            {
                result.Items.Add(new(AnimationGraphItemKind.Caller, y, TriggerHeight) { Caller = caller, Text = $"Called by {caller.Name} ({caller.Bindings})" });
                y += TriggerHeight;
            }
            foreach (var group in activation.Prerequisites)
            {
                result.Items.Add(new(AnimationGraphItemKind.PrerequisiteHeader, y, PrerequisiteHeaderHeight) { Group = group, Text = "Prerequisites · " + (group.Required ? (group.Items.Count == 1 ? "required" : $"all {group.Items.Count}") : group.Minimum == 0 ? $"any of {group.Items.Count}" : $"{group.Minimum} of {group.Items.Count}") });
                y += PrerequisiteHeaderHeight;
                foreach (var item in group.Items.Take(ShownPrerequisites))
                {
                    result.Items.Add(new(AnimationGraphItemKind.Prerequisite, y, PrerequisiteHeight) { Group = group, Prerequisite = item, Text = item.Target });
                    y += PrerequisiteHeight;
                }
                if (group.Items.Count > ShownPrerequisites)
                {
                    result.Items.Add(new(AnimationGraphItemKind.More, y, PrerequisiteHeight) { Group = group, Count = group.Items.Count - ShownPrerequisites });
                    y += PrerequisiteHeight;
                }
            }
            if (activation.Prerequisites.Count > 0) { result.Items.Add(new(AnimationGraphItemKind.RunLine, y + 2, RunNoteHeight)); y += RunNoteHeight + 4; }
            result.Height = y + 4;
            return result;
        }

        private Sized SizeReference(AnimationGraphNode node)
        {
            var reference = node.Reference!;
            Sized result = new(node, 0);
            double y = Header + 3;
            void Line(string? text, string kind)
            {
                if (string.IsNullOrEmpty(text)) return;
                result.Items.Add(new(AnimationGraphItemKind.Line, y, LineHeight) { Text = text, Keyword = kind });
                y += LineHeight;
            }
            Line(reference.Detail, "detail");
            Line(reference.Summary, "summary");
            if (reference.Calls.Count > 0)
                Line("calls " + string.Join(" · ", reference.Calls.Take(4).Select(c => c.Name + (c.BackLink ? " ↩" : ""))) + (reference.Calls.Count > 4 ? " …" : ""), "calls");
            if (reference.NeededBy.Count > 0) Line("needed by " + string.Join(", ", reference.NeededBy.Take(3)) + (reference.NeededBy.Count > 3 ? " …" : ""), "neededBy");
            Line(reference.Caution, "caution");
            result.Items.Add(new(AnimationGraphItemKind.RunLine, y, LineHeight));
            y += LineHeight;
            result.Height = y + 5;
            return result;
        }

        // ------------------------------------------------------------ positions

        private void Place()
        {
            top = Margin + 16 + LanesOf("top").Count * LaneSpacing;
            int maxColumn = all.Count == 0 ? 0 : all.Max(n => n.Column);
            double x = Margin;
            Dictionary<int, double> columnRight = [];
            for (int column = 0; column <= maxColumn; column++)
            {
                token.ThrowIfCancellationRequested();
                double right = x;
                if (column == 0)
                {
                    // The activation, then the bindings calling it (not called back) stacked under it.
                    var activation = all.FirstOrDefault(n => n.Column == 0 && n.Kind == AnimationGraphNodeKind.Activation);
                    double y = top + FrameTop;
                    if (activation != null)
                    {
                        var placed = sized[activation.Id]; placed.X = x; placed.Y = y; y += placed.Height + 28;
                    }
                    foreach (var node in all.Where(n => n.Column == 0 && n.Kind == AnimationGraphNodeKind.Reference))
                    {
                        var placed = sized[node.Id]; placed.X = x; placed.Y = y; y += placed.Height + 12;
                    }
                    right = x + NodeWidth;
                }
                else
                {
                    double y = top;
                    double referenceY = top + FrameTop;
                    foreach (var node in all.Where(n => n.Column == column && n.Kind is AnimationGraphNodeKind.Frame or AnimationGraphNodeKind.Reference))
                    {
                        if (node.Kind == AnimationGraphNodeKind.Reference)
                        {
                            var placed = sized[node.Id]; placed.X = x; placed.Y = referenceY; referenceY += placed.Height + 14;
                            right = Math.Max(right, x + NodeWidth);
                            continue;
                        }
                        var info = frames[node.Id];
                        var frame = info.Frame;
                        // An expanded frame sits beside its reference node, under the frame above it.
                        double wanted = node.Parent is { } parent && sized.TryGetValue(parent, out var owner) ? owner.Y - FrameTop : top;
                        frame.X = x; frame.Y = Math.Max(wanted, y);
                        double channel = LanesOf("frame:" + node.Id).Count;
                        frame.Height = FrameTop + info.ColumnsHeight + FrameBottom + (channel > 0 ? 8 + channel * LaneSpacing : 0);
                        for (int lane = 0; lane < info.Columns.Count; lane++)
                        {
                            double ny = frame.Y + FrameTop;
                            foreach (var member in info.Columns[lane]) { member.X = info.LaneX(lane); member.Y = ny; ny += member.Height + FramePadding; }
                        }
                        y = frame.Y + frame.Height + 24;
                        right = Math.Max(right, x + info.Width);
                    }
                }
                columns[column] = (x, right);
                columnRight[column] = right;
                x = right + (column == 0 ? ActivationGap : FrameGap);
            }
            double bottom = sized.Values.Select(s => s.Y + s.Height).DefaultIfEmpty(top).Max();
            bottomChannel = bottom + 16;
        }

        private void Publish()
        {
            foreach (var node in all)
            {
                var s = sized[node.Id];
                double width = node.Kind == AnimationGraphNodeKind.Frame ? frames[node.Id].Width : NodeWidth;
                var placement = new AnimationGraphPlacement(node, new(s.X, s.Y, width, s.Height))
                {
                    Frame = s.Frame, Lane = s.Lane, Collapsed = s.Collapsed, HeaderHeight = node.Kind == AnimationGraphNodeKind.Frame ? FrameHeader : Header,
                    Items = s.Items.AsReadOnly(), Boxes = s.Boxes.AsReadOnly(), HiddenRows = s.Hidden,
                };
                layout.nodes.Add(placement); layout.nodeById[node.Id] = placement;
            }
            foreach (var (row, (node, item)) in bands) layout.itemByRow[row] = (layout.nodeById[node], item);
            var bounds = layout.nodes.Count == 0 ? new AnimationGraphRect(0, 0, Margin * 2, Margin * 2) : layout.nodes.Select(n => n.Bounds).Aggregate((a, b) => a.Union(b));
            foreach (var route in layout.routes)
                foreach (var point in route.Points) bounds = bounds.Union(new(point.X, point.Y, 0, 0));
            double channelBottom = bottomChannel + LanesOf("bottom").Count * LaneSpacing;
            if (LanesOf("bottom").Count > 0) bounds = bounds.Union(new(bounds.X, channelBottom, 0, 0));
            layout.Bounds = new(0, 0, bounds.Right + Margin, bounds.Bottom + Margin);
        }

        // ------------------------------------------------------------ routing

        private int ColumnOf(Sized node) => node.Node.Column;

        /// <summary>
        /// Plans one edge's route: with <paramref name="place"/> false it only reserves lanes (before positions are known), else
        /// it returns the routed polyline. Both passes take the same decisions in the same order, so lanes match.
        /// </summary>
        private AnimationGraphRoute? Plan(AnimationGraphEdge edge, bool place)
        {
            if (!sized.TryGetValue(edge.FromNode, out var source) || !sized.TryGetValue(edge.To, out var target)) return null;
            // A reference node naming itself (its own invalidate) is said on the node.
            if (source == target) return null;
            var end = edge.Relation switch
            {
                AnimationGraphRelation.Release or AnimationGraphRelation.ContactRelease => AnimationGraphEnd.OpenArrow,
                AnimationGraphRelation.StopSequence or AnimationGraphRelation.StopAnimation => AnimationGraphEnd.TBar,
                AnimationGraphRelation.ResetAnimation => AnimationGraphEnd.Reset,
                AnimationGraphRelation.InvalidateAnimation => AnimationGraphEnd.Invalidate,
                AnimationGraphRelation.Prerequisite => AnimationGraphEnd.Diamond,
                _ => AnimationGraphEnd.Arrow,
            };
            List<AnimationGraphPoint> points = [];
            bool fromRow = edge.From.StartsWith("row:", StringComparison.Ordinal);
            bool hidden = false;
            double sx = source.X + NodeWidth, sy = source.Y + Header / 2;
            if (fromRow)
            {
                if (!anchors.TryGetValue(edge.From, out var anchor)) return null;
                hidden = !anchor.Shown; sy = source.Y + anchor.Y;
            }

            if (edge.Relation == AnimationGraphRelation.Prerequisite)
            {
                // From the needed binding's node, down the gutter left of its column to the channel below everything, then up
                // the activation's left gutter into its prerequisites header, where a diamond marks it.
                var header = target.Items.FirstOrDefault(i => i.Kind == AnimationGraphItemKind.PrerequisiteHeader);
                int laneDown = LanesOf("left:" + ColumnOf(source)).Of(edge.Id), laneBelow = LanesOf("bottom").Of(edge.Id);
                int laneLeft = LanesOf("left:" + ColumnOf(target)).Of("to:" + edge.To);
                if (!place) return null;
                double startY = source.Y + Header / 2, gx = ColumnLeftLane(ColumnOf(source), laneDown), by = bottomChannel + laneBelow * LaneSpacing;
                double ax = ColumnLeftLane(ColumnOf(target), laneLeft), ty = target.Y + (header == null ? Header / 2 : header.Y + header.Height / 2);
                points.AddRange([new(source.X, startY), new(gx, startY), new(gx, by), new(ax, by), new(ax, ty), new(target.X, ty)]);
                return Route(edge, points, end, false, hidden, false, null);
            }
            if (!fromRow && target.Node.Kind == AnimationGraphNodeKind.Activation && ColumnOf(source) == ColumnOf(target))
            {
                // A binding calling the opened one, stacked under its activation: up the activation's left gutter.
                int lane = LanesOf("left:" + ColumnOf(target)).Of(edge.Id);
                if (!place) return null;
                double ax = ColumnLeftLane(ColumnOf(target), lane), ty = target.Y + Header / 2;
                points.AddRange([new(source.X, sy), new(ax, sy), new(ax, ty), new(target.X, ty)]);
                return Route(edge, points, end, false, false, false, null);
            }
            var sourceFrame = source.Frame != null ? frames[source.Frame] : null;
            if (fromRow && sourceFrame != null && target.Node.Kind == AnimationGraphNodeKind.Activation && target.Node.Entry == source.Node.Entry)
            {
                // A by-name event naming its own binding: up its gutter to its frame's header.
                int lane = LanesOf(GutterKey(source.Frame!, source.Lane)).Of(edge.Id);
                if (!place) return null;
                double gx = GutterLane(sourceFrame, source.Lane, lane), hy = sourceFrame.Frame.Y + FrameHeader;
                points.AddRange([new(sx, sy), new(gx, sy), new(gx, hy)]);
                string text = (edge.Label ?? "") + " · this animation";
                var at = new AnimationGraphPoint(gx + 5, hy + 11);
                bool free = Free(new(gx + 4, hy + 1, Width(text), 11));
                return Route(edge, points, end, true, hidden, true, free && labelled.Add("self:" + edge.To + ":" + edge.Relation) ? (AnimationGraphLabelAlign.Start, at, text) : null);
            }

            // Same frame: the next frame column directly, else through the frame's bottom channel.
            if (sourceFrame != null && target.Frame == source.Frame && target.Node.Kind == AnimationGraphNodeKind.Sequence)
            {
                double ty = target.Y + EntryY(target, edge);
                if (target.Lane == source.Lane + 1)
                {
                    int lane = LanesOf(GutterKey(source.Frame!, source.Lane)).Of("to:" + edge.To);
                    if (!place) return null;
                    double gx = GutterLane(sourceFrame, source.Lane, lane);
                    points.AddRange([new(sx, sy), new(gx, sy), new(gx, ty), new(target.X, ty)]);
                }
                else
                {
                    int laneOut = LanesOf(GutterKey(source.Frame!, source.Lane)).Of(edge.Id);
                    int laneChannel = LanesOf("frame:" + source.Frame).Of(edge.Id);
                    int laneEnter = LanesOf(GutterKey(source.Frame!, target.Lane - 1)).Of("to:" + edge.To);
                    if (!place) return null;
                    double gx = GutterLane(sourceFrame, source.Lane, laneOut), cy = sourceFrame.ChannelTop + laneChannel * LaneSpacing;
                    double tx = GutterLane(sourceFrame, target.Lane - 1, laneEnter);
                    points.AddRange([new(sx, sy), new(gx, sy), new(gx, cy), new(tx, cy), new(tx, ty), new(target.X, ty)]);
                }
                return Route(edge, points, end, fromRow, hidden, false, LabelBefore(edge, points));
            }

            // Out of the frame (or the reference column) into the corridor after the source's column.
            int sourceColumn = ColumnOf(source), targetColumn = ColumnOf(target);
            bool last = sourceFrame == null || source.Lane >= sourceFrame.Columns.Count - 1;
            bool toActivation = target.Node.Kind == AnimationGraphNodeKind.Activation;
            bool sameColumn = !toActivation && targetColumn == sourceColumn;
            bool next = !toActivation && targetColumn == sourceColumn + 1;
            int laneGutter = last ? 0 : LanesOf(GutterKey(source.Frame!, source.Lane)).Of(edge.Id);
            int laneFrame = last ? 0 : LanesOf("frame:" + source.Frame).Of(edge.Id);
            int laneCorridor = LanesOf("corridor:" + sourceColumn).Of(next || sameColumn || toActivation ? "to:" + edge.To : edge.Id);
            int laneTop = toActivation ? LanesOf("top").Of("to:" + edge.To) : 0;
            int laneLeftIn = toActivation && targetColumn > 0 ? LanesOf("left:" + targetColumn).Of("to:" + edge.To) : 0;
            int laneBottom = !toActivation && !next && !sameColumn ? LanesOf("bottom").Of(edge.Id) : 0;
            int laneIn = !toActivation && !next && !sameColumn ? LanesOf("corridor:" + (targetColumn - 1)).Of("to:" + edge.To) : 0;
            if (!place) return null;
            points.Add(new(sx, sy));
            double cx = CorridorLane(sourceColumn, laneCorridor);
            if (!last)
            {
                double gx = GutterLane(sourceFrame!, source.Lane, laneGutter), cy = sourceFrame!.ChannelTop + laneFrame * LaneSpacing;
                points.AddRange([new(gx, sy), new(gx, cy), new(cx, cy)]);
            }
            else points.Add(new(cx, sy));
            if (toActivation)
            {
                // Through the outer channel above every frame: down into the opened binding's activation, or into an expanded
                // frame's activation from the gutter left of its column.
                double ty = Margin + 8 + laneTop * LaneSpacing;
                string text = edge.Label ?? "";
                if (targetColumn == 0)
                {
                    double ax = target.X + 72 + (laneTop % 4) * 10;
                    points.AddRange([new(cx, ty), new(ax, ty), new(ax, target.Y)]);
                    var at = new AnimationGraphPoint(ax + 8, ty - 3);
                    return Route(edge, points, end, fromRow, hidden, false, labelled.Add("top:" + edge.To + ":" + text) ? (AnimationGraphLabelAlign.Start, at, (edge.BackLink ? "↩ " : "") + text) : null);
                }
                double lx = ColumnLeftLane(targetColumn, laneLeftIn), ey = target.Y + Header / 2;
                points.AddRange([new(cx, ty), new(lx, ty), new(lx, ey), new(target.X, ey)]);
                return Route(edge, points, end, fromRow, hidden, false, labelled.Add("top:" + edge.To + ":" + text) ? (AnimationGraphLabelAlign.Start, new(lx + 8, ty - 3), (edge.BackLink ? "↩ " : "") + text) : null);
            }
            double entry = target.Y + EntryY(target, edge);
            if (next) points.AddRange([new(cx, entry), new(target.X, entry)]);
            else if (sameColumn) points.AddRange([new(cx, entry), new(target.X + NodeWidth, entry)]);
            else
            {
                // Further columns: along the channel below everything, then up the corridor before the target's column.
                double by = bottomChannel + laneBottom * LaneSpacing, tx = CorridorLane(targetColumn - 1, laneIn);
                points.AddRange([new(cx, by), new(tx, by), new(tx, entry), new(target.X, entry)]);
            }
            return Route(edge, points, end, fromRow, hidden, false, LabelBefore(edge, points));
        }

        /// <summary>The label before an edge's end, on its last horizontal run, when it stays off every node; else none (the tooltip says it).</summary>
        private (AnimationGraphLabelAlign, AnimationGraphPoint, string)? LabelBefore(AnimationGraphEdge edge, List<AnimationGraphPoint> points)
        {
            if (string.IsNullOrEmpty(edge.Label) || points.Count < 2) return null;
            var endPoint = points[^1];
            double width = Width(edge.Label);
            bool leftward = points[^2].X > endPoint.X;
            AnimationGraphRect box = leftward ? new(endPoint.X + 10, endPoint.Y - 13, width, 11) : new(endPoint.X - 10 - width, endPoint.Y - 13, width, 11);
            if (!Free(box) || !labelled.Add(edge.To + ":" + edge.Relation + ":" + edge.Label)) return null;
            return leftward ? (AnimationGraphLabelAlign.Start, new(box.X, endPoint.Y - 3), edge.Label) : (AnimationGraphLabelAlign.End, new(box.Right, endPoint.Y - 3), edge.Label);
        }
        /// <summary>A label's rough width at 10.5 DIP, to keep it off nodes.</summary>
        private static double Width(string text) => text.Length * 5.6 + 4;
        private bool Free(AnimationGraphRect rect) => !sized.Values.Any(s => s.Node.Kind != AnimationGraphNodeKind.Frame && s.Rect.Intersects(rect));

        /// <summary>Where an edge enters a node: a waiting sequence's release pill, else its header.</summary>
        private static double EntryY(Sized target, AnimationGraphEdge edge) =>
            edge.Relation is AnimationGraphRelation.Release or AnimationGraphRelation.ContactRelease && target.Items.FirstOrDefault(i => i.Kind == AnimationGraphItemKind.StateLine) is { } state
                ? state.Y + state.Height / 2 : Header / 2;

        private static string GutterKey(string frame, int lane) => $"gutter:{frame}:{lane}";
        /// <summary>A lane in the gutter right of a frame column (the frame's right padding after its last column; its left padding for −1).</summary>
        private static double GutterLane(FrameInfo frame, int lane, int index)
        {
            if (lane < 0) return frame.Frame.X + 4 + (index % 2) * LaneSpacing;
            double right = frame.LaneX(lane) + NodeWidth;
            if (lane >= frame.Columns.Count - 1) return right + 4 + (index % 2) * LaneSpacing;
            return right + 6 + (index % 4) * LaneSpacing;
        }
        /// <summary>A lane in the corridor between a graph column and the next.</summary>
        private double CorridorLane(int column, int index)
        {
            var (_, right) = columns.TryGetValue(column, out var found) ? found : (0, 0);
            double gap = column == 0 ? ActivationGap : FrameGap;
            int capacity = Math.Max(1, (int)((gap - 16) / LaneSpacing) + 1);
            return right + 8 + (index % capacity) * LaneSpacing;
        }
        /// <summary>A lane in the corridor left of a graph column.</summary>
        private double ColumnLeftLane(int column, int index)
        {
            // Counted from the column's side, so they stay clear of the trunks counted from the corridor's other side.
            if (column <= 0 || !columns.TryGetValue(column, out var at)) return Margin - 6 - (index % 3) * LaneSpacing;
            return at.X - 8 - (index % 6) * LaneSpacing;
        }

        private static AnimationGraphRoute Route(AnimationGraphEdge edge, List<AnimationGraphPoint> points, AnimationGraphEnd end, bool fromPort, bool hidden, bool self,
            (AnimationGraphLabelAlign Align, AnimationGraphPoint At, string Text)? label)
        {
            // Drop repeated points and merge collinear runs, so corners are real corners.
            List<AnimationGraphPoint> clean = [];
            foreach (var point in points)
            {
                if (clean.Count > 0 && Math.Abs(clean[^1].X - point.X) < 0.01 && Math.Abs(clean[^1].Y - point.Y) < 0.01) continue;
                if (clean.Count >= 2)
                {
                    var a = clean[^2]; var b = clean[^1];
                    bool vertical = Math.Abs(a.X - b.X) < 0.01 && Math.Abs(b.X - point.X) < 0.01, horizontal = Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(b.Y - point.Y) < 0.01;
                    if (vertical || horizontal) clean[^1] = point; else clean.Add(point);
                }
                else clean.Add(point);
            }
            if (clean.Count == 1) clean.Add(clean[0]);
            return new(edge, clean.AsReadOnly(), end)
            {
                FromPort = fromPort && !hidden, FromHidden = hidden, Self = self,
                LabelAlign = label?.Align ?? AnimationGraphLabelAlign.None, Label = label?.At ?? default, Text = label?.Text,
            };
        }
    }
}
