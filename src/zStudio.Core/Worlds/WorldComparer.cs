using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>A difference between two worlds, at a node path.</summary>
public sealed class WorldDifference
{
    private readonly WorldComparisonNode? node; private readonly string? name;
    public WorldDifference(string path, string field, string expected, string actual) { name = path; Field = field; Expected = expected; Actual = actual; }
    /// <summary>A difference at <paramref name="node"/>, or at a name below it.</summary>
    internal WorldDifference(WorldComparisonNode node, string? name, string field, string expected, string actual) : this(name!, field, expected, actual) => this.node = node;
    /// <summary>The node's name path ("world/crate/lid"), made when asked, so differences keep no path of their own.</summary>
    public string Path => node == null ? name ?? "" : name == null ? node.Path : node.Path + "/" + name;
    public string Field { get; }
    public string Expected { get; }
    public string Actual { get; }
    public override string ToString() => $"{Path} {Field}: {Expected} / {Actual}";
}

/// <summary>How a node of one world compares with its counterpart in another.</summary>
public enum WorldComparisonStatus
{
    /// <summary>Both worlds have the node, and nothing compared differs.</summary>
    Same,
    /// <summary>Both worlds have the node, and some of its fields or the number of its children differ.</summary>
    Changed,
    /// <summary>Only the expected world has the node.</summary>
    OnlyExpected,
    /// <summary>Only the actual world has the node.</summary>
    OnlyActual,
}

/// <summary>A node of the merged tree of two worlds: a matched pair of nodes, or a node only one of them has.</summary>
public sealed class WorldComparisonNode
{
    internal WorldComparisonNode(WorldComparisonNode? parent, string segment, string name, WorldNode? expected, WorldNode? actual) { Parent = parent; this.segment = segment; Name = name; Expected = expected; Actual = actual; }
    private readonly string segment;
    internal WorldComparisonNode? Parent { get; }
    /// <summary>
    /// The node's name path, as <see cref="WorldDifference.Path"/> gives it; made from its parents when asked, so a deep tree
    /// keeps no path per node.
    /// </summary>
    public string Path
    {
        get
        {
            List<string> segments = [];
            for (var node = this; node != null; node = node.Parent) segments.Add(node.segment);
            segments.Reverse();
            return string.Join("/", segments);
        }
    }
    public string Name { get; }
    public WorldNode? Expected { get; }
    public WorldNode? Actual { get; }
    /// <summary>
    /// What differs in the pair itself (its fields, the number of its children): at most
    /// <see cref="WorldComparer.MaximumNodeDifferences"/> of <see cref="DifferenceCount"/>, described once for every place of
    /// the pair and listed here when asked.
    /// </summary>
    public IReadOnlyList<WorldDifference> Differences => differences ??= [.. Described.Select(d => new WorldDifference(this, null, d.Field, d.Expected, d.Actual))];
    private IReadOnlyList<WorldDifference>? differences;
    internal (string Field, string Expected, string Actual)[] Described { get; set; } = [];
    /// <summary>How many differences the pair has, listed or not.</summary>
    public int DifferenceCount { get; internal set; }
    /// <summary>The pair's children, matched by name and structure, in the expected world's order; the actual world's own after them.</summary>
    public List<WorldComparisonNode> Children { get; } = [];
    /// <summary>Whether some of its children are not in the tree: the tree reached <see cref="WorldComparer.MaximumTreeNodes"/> nodes or 256 levels.</summary>
    public bool Truncated { get; internal set; }
    public WorldComparisonStatus Status => Expected == null ? WorldComparisonStatus.OnlyActual : Actual == null ? WorldComparisonStatus.OnlyExpected
        : DifferenceCount > 0 ? WorldComparisonStatus.Changed : WorldComparisonStatus.Same;
    /// <summary>How many nodes below this one are not the same.</summary>
    public int ChangedBelow { get; internal set; }
    /// <summary>
    /// The engine finds a name's node with the highest slot first; this node is that node in the expected world, but the
    /// actual world's highest slot of the name holds another node, so lookups by the name bind elsewhere.
    /// </summary>
    public bool BindsElsewhere { get; internal set; }
}

/// <summary>A name several nodes share, and whether lookups by it find the matching node in both worlds.</summary>
/// <param name="Expected">The expected world's node with the name's highest slot, which lookups find first.</param>
/// <param name="Counterpart">That node's match in the actual world, if it has one.</param>
/// <param name="Actual">The actual world's node with the name's highest slot.</param>
/// <param name="Interchangeable">The actual world's node is another one than the counterpart, but indistinguishable from it: the
/// same parents and the same contents all the way down, so lookups find the same thing.</param>
public sealed record WorldNameBinding(string Name, int ExpectedCount, int ActualCount, WorldNode Expected, WorldNode? Counterpart, WorldNode Actual, bool Interchangeable = false)
{
    public bool Same => ReferenceEquals(Counterpart, Actual) || Interchangeable;
    /// <summary>
    /// The actual world's node is another one than the counterpart, and the comparison had made too many copy checks to tell
    /// whether it is indistinguishable from it (<see cref="WorldComparison.UncheckedBindings"/>): it counts as another node.
    /// </summary>
    public bool Unchecked { get; init; }
}

/// <summary>Two worlds compared as one merged tree of nodes, with their differences and node slots.</summary>
public sealed class WorldComparison
{
    /// <summary>The world node's pair (its members below it), then the nodes neither world places under a parent.</summary>
    public required IReadOnlyList<WorldComparisonNode> Roots { get; init; }
    public required IReadOnlyDictionary<WorldNode, int> ExpectedSlots { get; init; }
    public required IReadOnlyDictionary<WorldNode, int> ActualSlots { get; init; }
    /// <summary>Every difference of the tree, flattened (at most the limit given, of <see cref="DifferenceCount"/>).</summary>
    public required List<WorldDifference> Differences { get; init; }
    /// <summary>How many differences the tree has, listed or not.</summary>
    public int DifferenceCount { get; init; }
    /// <summary>Names more than one node has in either world, with the node lookups by each name find.</summary>
    public required IReadOnlyList<WorldNameBinding> Bindings { get; init; }
    /// <summary>Merged tree nodes by status (a node shared by several parents counts once per place).</summary>
    public required IReadOnlyDictionary<WorldComparisonStatus, int> Counts { get; init; }
    /// <summary>
    /// Each expected node's match in the actual world, at its first place; it includes the places the tree does not show,
    /// unless <see cref="PairingTruncated"/>.
    /// </summary>
    public required IReadOnlyDictionary<WorldNode, WorldNode> Counterparts { get; init; }
    /// <summary>
    /// Whether the merged tree stopped growing at <see cref="WorldComparer.MaximumTreeNodes"/> nodes or 256 levels (the nodes
    /// it cut are <see cref="WorldComparisonNode.Truncated"/>); the pairs below the cut are matched for
    /// <see cref="Counterparts"/> but not compared.
    /// </summary>
    public bool Truncated { get; init; }
    /// <summary>Whether matching stopped too (256 levels, or <see cref="WorldComparer.MaximumTreeNodes"/> more children paired below the cut), so <see cref="Counterparts"/> may lack nodes.</summary>
    public bool PairingTruncated { get; init; }
    /// <summary>
    /// Whether pairing some copies of a repeated name nearest first stopped early, leaving them to pair in order: a name had
    /// more than <see cref="WorldComparer.MaximumNearestPairs"/> candidate pairs, or the comparison had examined
    /// <see cref="WorldComparer.MaximumComparisonPairs"/>. Their differences, and the lookups that depend on them, may come
    /// from the pairing rather than from the worlds.
    /// </summary>
    public bool ApproximatePairing { get; init; }
    /// <summary>
    /// How many <see cref="Bindings"/> count as another node only because the comparison had made too many copy checks to tell
    /// whether the node is indistinguishable from the counterpart (<see cref="WorldNameBinding.Unchecked"/>).
    /// </summary>
    public int UncheckedBindings { get; init; }
}

/// <summary>
/// Compares two worlds by meaning: nodes are matched by name path (repeated names by their structure, children and
/// position, then in order), and each pair's class, carried and derived flags, zone, local transform, class data (with the
/// nodes it names), grid cell and model are compared, along with the world, its lights and the texture directory entries and variant links. A model
/// compares every value it keeps: display mode and flags, scrolling, morph factor, point entries (lens flares), bounds and
/// polygons, whose corners carry their positions, UVs, normals and morph deltas and whose materials all their fields. Node
/// slots, stored pointers, runtime counters, child order and polygon order do not matter; <see cref="CompareTree"/> also
/// reports the slots and which repeated names lookups would bind elsewhere.
/// </summary>
public static class WorldComparer
{
    /// <summary>The most nodes the merged tree holds (a node under several parents appears under each).</summary>
    public const int MaximumTreeNodes = 500_000;
    /// <summary>The most differences a pair lists (<see cref="WorldComparisonNode.DifferenceCount"/> counts them all).</summary>
    public const int MaximumNodeDifferences = 64;
    /// <summary>About the most characters of names a difference lists.</summary>
    private const int MaximumText = 512;
    /// <summary>The most characters of differences a comparison describes (each pair once); past them, differences are only counted.</summary>
    private const long MaximumDescribedText = 1L << 26;

    /// <summary>The differences, flattened; a comparison the limit or the tree's size cut ends with a "truncated" line saying so.</summary>
    public static List<WorldDifference> Compare(GameZWorld expected, GameZWorld actual, int limit = 10_000)
    {
        var comparison = CompareTree(expected, actual, limit);
        if (comparison.Truncated || comparison.DifferenceCount > comparison.Differences.Count)
            comparison.Differences.Add(new("", "truncated", $"{comparison.DifferenceCount} differences, {comparison.Differences.Count} listed",
                comparison.Truncated ? $"the merged tree stops at {MaximumTreeNodes} nodes or 256 levels" : ""));
        return comparison.Differences;
    }

    public static WorldComparison CompareTree(GameZWorld expected, GameZWorld actual, int limit = 10_000, CancellationToken token = default)
    {
        Memo memo = new(token) { ExpectedTextures = new(expected.Textures, token), ActualTextures = new(actual.Textures, token) };
        List<WorldDifference> differences = [];
        List<WorldComparisonNode> roots = [];
        Dictionary<WorldNode, WorldNode> counterpart = new(ReferenceEqualityComparer.Instance);
        List<(WorldComparisonNode Node, WorldNode A, WorldNode B, bool WorldChild, bool Root)> pending = [];
        // Area tables often repeat nodes already placed under another parent. Their membership is compared as class
        // data; only area-only nodes need an additional tree place. Cache each world's list across shared instances.
        HashSet<WorldNode> inChildren = new(expected.Nodes.Concat(actual.Nodes).SelectMany(n => n.Children), ReferenceEqualityComparer.Instance);
        Dictionary<WorldNode, List<WorldNode>> worldChildren = new(ReferenceEqualityComparer.Instance);
        HashSet<(WorldNode, WorldNode)> expanded = [];
        // A pair's own differences, worked out once however many places share it; described while a pair lists fewer than
        // MaximumNodeDifferences and the comparison fewer than MaximumDescribedText characters, counted always.
        Dictionary<(WorldNode, WorldNode, bool), (int Count, (string, string, string)[] Described)> owns = [];
        int made = 0, found = 0, unshown = MaximumTreeNodes; bool truncated = false, pairingTruncated = false;
        void Place(WorldComparisonNode node, (int Count, (string, string, string)[] Described) own)
        {
            node.DifferenceCount = own.Count; node.Described = own.Described; found += own.Count;
            foreach (var (field, a, b) in own.Described) if (differences.Count < limit) differences.Add(new(node, null, field, a, b));
        }
        // A difference no pair holds (how many nodes have a name, the textures): only in the flat list.
        void Other(WorldComparisonNode? at, string name, string field, object? a, object? b)
        {
            found++;
            if (differences.Count < limit) differences.Add(at == null ? new(name, field, $"{a}", $"{b}") : new(at, name, field, $"{a}", $"{b}"));
        }
        WorldComparisonNode? New(WorldComparisonNode parent, string name, WorldNode? a, WorldNode? b)
        {
            if (made >= MaximumTreeNodes) { truncated = parent.Truncated = true; return null; }
            made++;
            return new(parent, name, name, a, b);
        }
        var worldA = expected.Nodes.FirstOrDefault(n => n.Class == WorldNodeClass.World); var worldB = actual.Nodes.FirstOrDefault(n => n.Class == WorldNodeClass.World);
        if (worldA != null)
        {
            // Slots may reorder distinct worlds. Prefer the unique authored name before the legacy single-root pairing.
            var named = actual.Nodes.Where(n => n.Class == WorldNodeClass.World && n.Name == worldA.Name).Take(2).ToArray();
            if (named.Length == 1) worldB = named[0];
        }
        if (worldA == null || worldB == null) Other(null, "", "world", worldA?.Name, worldB?.Name);
        else
        {
            WorldComparisonNode world = new(null, "world", worldA.Name, worldA, worldB); made++;
            roots.Add(world); counterpart[worldA] = worldB; expanded.Add((worldA, worldB));
            pending.Add((world, worldA, worldB, false, true));
            Match(world, Members(worldA), Members(worldB), true, 0);
        }
        // The nodes no parent holds (templates the build loaded, cameras, lights): a merged level of their own.
        WorldComparisonNode detached = new(null, "", "", null, null);
        bool pairedWorld = worldA != null && worldB != null;
        Match(detached, expected.Nodes.Where(n => n.Parents.Count == 0 && !(pairedWorld && ReferenceEquals(n, worldA))).ToList(), actual.Nodes.Where(n => n.Parents.Count == 0 && !(pairedWorld && ReferenceEquals(n, worldB))).ToList(), false, 0);
        roots.AddRange(detached.Children);
        // References may point to a later sibling or detached node. Establish every counterpart before comparing
        // class references; names alone lose identity when two distinct nodes share a name.
        memo.Counterparts = counterpart;
        var slotsA = GameZWriter.NodeSlots(expected); var slotsB = GameZWriter.NodeSlots(actual);
        memo.ExpectedSlots = slotsA; memo.ActualSlots = slotsB;
        memo.RepeatedNames = expected.Nodes.GroupBy(n => n.Name, StringComparer.Ordinal).Concat(actual.Nodes.GroupBy(n => n.Name, StringComparer.Ordinal))
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (node, a, b, worldChild, root) in pending) Place(node, Differ(a, b, worldChild, root));
        var textureOwner = pairedWorld ? roots[0] : null;
        var textureDetails = textureOwner?.Described.ToList();
        foreach (var difference in WorldTextureComparison.Compare(expected.Textures, actual.Textures, token))
        {
            Other(textureOwner, difference.Name, difference.Field, difference.Expected, difference.Actual);
            if (textureOwner == null) continue;
            textureOwner.DifferenceCount++;
            // The same bounded details are visible on the world row in the GUI and MCP, even with a zero flat-list limit.
            if (textureDetails!.Count >= MaximumNodeDifferences || memo.DescribedText >= MaximumDescribedText) continue;
            string field = difference.Name + "." + difference.Field;
            memo.DescribedText += field.Length + difference.Expected.Length + difference.Actual.Length;
            textureDetails.Add((field, difference.Expected, difference.Actual));
        }
        if (textureOwner != null) textureOwner.Described = [.. textureDetails!];

        Dictionary<WorldComparisonStatus, int> counts = Enum.GetValues<WorldComparisonStatus>().ToDictionary(s => s, _ => 0);
        Dictionary<WorldNode, List<WorldComparisonNode>> places = new(ReferenceEqualityComparer.Instance);
        int Summarize(WorldComparisonNode node)
        {
            counts[node.Status]++;
            if (node.Expected != null)
            {
                if (!places.TryGetValue(node.Expected, out var list)) places[node.Expected] = list = [];
                list.Add(node);
            }
            int below = 0;
            foreach (var child in node.Children) below += Summarize(child) + (child.Status == WorldComparisonStatus.Same ? 0 : 1);
            node.ChangedBelow = below;
            return below;
        }
        foreach (var root in roots) Summarize(root);
        List<WorldNameBinding> bindings = [];
        var byNameB = actual.Nodes.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var group in expected.Nodes.GroupBy(n => n.Name))
        {
            token.ThrowIfCancellationRequested();
            int count = group.Count();
            if (!byNameB.TryGetValue(group.Key, out var others) || count < 2 && others.Count < 2) continue;
            var first = group.MaxBy(n => slotsA[n])!; var highest = others.MaxBy(n => slotsB[n])!;
            var match = counterpart.GetValueOrDefault(first);
            bool other = match != null && !ReferenceEquals(match, highest), interchangeable = other && memo.Interchangeable(match!, highest, 0);
            // The checks are shared by every name: once they run out, a copy is not told apart, and the binding says so.
            WorldNameBinding binding = new(group.Key, count, others.Count, first, match, highest, interchangeable) { Unchecked = other && !interchangeable && memo.ChecksExhausted };
            bindings.Add(binding);
            if (!binding.Same) foreach (var place in places.GetValueOrDefault(first) ?? []) place.BindsElsewhere = true;
        }
        return new()
        {
            Roots = roots, ExpectedSlots = slotsA, ActualSlots = slotsB, Differences = differences, DifferenceCount = found, Bindings = bindings, Counts = counts,
            Counterparts = counterpart, Truncated = truncated, PairingTruncated = pairingTruncated, ApproximatePairing = memo.Approximate,
            UncheckedBindings = bindings.Count(b => b.Unchecked),
        };

        static List<WorldNode> Members(WorldNode world) => [.. world.Children.Concat(world.Areas.SelectMany(a => a.Nodes)).Distinct(ReferenceEqualityComparer.Instance).Cast<WorldNode>()];
        List<WorldNode> Children(WorldNode node)
        {
            if (node.Class != WorldNodeClass.World) return node.Children;
            if (!worldChildren.TryGetValue(node, out var children))
                worldChildren[node] = children = [.. node.Children.Concat(node.Areas.SelectMany(a => a.Nodes).Where(n => !inChildren.Contains(n))).Distinct(ReferenceEqualityComparer.Instance).Cast<WorldNode>()];
            return children;
        }

        // Pairs two lists of children under parent; with no parent (below the tree's cut) it only matches them, for Counterparts.
        void Match(WorldComparisonNode? parent, List<WorldNode> a, List<WorldNode> b, bool worldChildren, int depth)
        {
            token.ThrowIfCancellationRequested();
            // Most pairs are leaves: nothing to match, and nothing to allocate for it.
            if (a.Count == 0 && b.Count == 0) return;
            Dictionary<WorldNode, WorldNode> pairs = new(ReferenceEqualityComparer.Instance);
            HashSet<WorldNode> paired = new(ReferenceEqualityComparer.Instance);
            var byName = b.ToLookup(n => n.Name, StringComparer.Ordinal);
            foreach (var group in a.GroupBy(n => n.Name, StringComparer.Ordinal))
            {
                var others = byName[group.Key].ToList(); var mine = group.ToList();
                if (parent != null && mine.Count != others.Count) Other(parent, group.Key, "count", mine.Count, others.Count);
                // A pair at many places, or parents holding the same children, pairs them once.
                foreach (var (x, y) in memo.Pairs(mine, others)) { pairs[x] = y; paired.Add(y); }
            }
            if (parent == null) { foreach (var node in a) if (pairs.TryGetValue(node, out var other)) Pair(null, node, other, worldChildren, depth); return; }
            foreach (var name in b.Select(n => n.Name).Distinct().Except(a.Select(n => n.Name))) Other(parent, name, "extra", "", name);
            foreach (var node in a)
                if ((pairs.TryGetValue(node, out var other) ? Pair(parent, node, other, worldChildren, depth) : Only(parent, node, true, depth)) is { } child) parent.Children.Add(child);
            foreach (var node in b)
                if (!paired.Contains(node) && Only(parent, node, false, depth) is { } child) parent.Children.Add(child);
        }

        // A node only one world has, with its subtree.
        WorldComparisonNode? Only(WorldComparisonNode parent, WorldNode node, bool expectedSide, int depth)
        {
            var result = New(parent, node.Name, expectedSide ? node : null, expectedSide ? null : node);
            if (result == null) return null;
            var children = Children(node);
            if (depth > 256) { if (children.Count > 0) truncated = result.Truncated = true; return result; }
            foreach (var child in children)
                if (Only(result, child, expectedSide, depth + 1) is { } inner) result.Children.Add(inner);
            return result;
        }

        WorldComparisonNode? Pair(WorldComparisonNode? parent, WorldNode a, WorldNode b, bool worldChild, int depth)
        {
            counterpart.TryAdd(a, b);
            bool again = !expanded.Add((a, b));
            var node = parent == null ? null : New(parent, a.Name, a, b);
            var childrenA = Children(a); var childrenB = Children(b);
            if (node == null)
            {
                // Below the cut, each pair is matched once more (a node shared by several parents is not), so every node
                // keeps its counterpart within a bounded amount of work.
                if (again || a.Class != b.Class) return null;
                if (depth > 256 || (unshown -= 1 + childrenA.Count) < 0) { pairingTruncated |= childrenA.Count > 0 && childrenB.Count > 0; return null; }
                Match(null, childrenA, childrenB, a.Class == WorldNodeClass.World, depth + 1);
                return null;
            }
            pending.Add((node, a, b, worldChild, false));
            if (a.Class != b.Class) return node;
            if (depth > 256) { if (childrenA.Count + childrenB.Count > 0) truncated = node.Truncated = true; pairingTruncated |= childrenA.Count > 0 && childrenB.Count > 0; }
            else Match(node, childrenA, childrenB, a.Class == WorldNodeClass.World, depth + 1);
            return node;
        }

        // The pair's own differences (a world root's: its class data only), all counted, described while the pair lists fewer
        // than MaximumNodeDifferences and the comparison has described fewer than MaximumDescribedText characters.
        (int Count, (string, string, string)[] Described) Differ(WorldNode a, WorldNode b, bool worldChild, bool root = false)
        {
            if (!root && owns.TryGetValue((a, b, worldChild), out var known)) return known;
            Found own = new(memo);
            memo.OwnSame(a, b, worldChild, root, own);
            (int, (string, string, string)[]) result = (own.Count, [.. own.Described]);
            if (!root) owns[(a, b, worldChild)] = result;
            return result;
        }
    }

    /// <summary>A pair's differences as they are found: each counted, made into text only while it is described.</summary>
    private sealed class Found(Memo memo)
    {
        public int Count { get; private set; }
        public List<(string Field, string Expected, string Actual)> Described { get; } = [];
        public void Add(string field, object? x, object? y)
        {
            Count++;
            if (Described.Count >= MaximumNodeDifferences || memo.DescribedText >= MaximumDescribedText) return;
            string expected = $"{x}", actual = $"{y}"; memo.DescribedText += expected.Length + actual.Length;
            Described.Add((field, expected, actual));
        }
    }
    /// <summary>Names a difference shows (<see cref="Names"/>), made into text only when it is described.</summary>
    private sealed class Listed(IEnumerable<string> names)
    {
        public Listed(IEnumerable<WorldNode> nodes) : this(nodes.Select(n => n.Name)) { }
        public override string ToString() => Names(names);
    }
    /// <summary>A matrix a difference shows, made into text only when it is described.</summary>
    private sealed class Shown(Matrix4x4 matrix)
    {
        public override string ToString() => Format(matrix);
    }

    /// <summary>
    /// Whether two nodes of one world are indistinguishable: the same parents, and the same name, class, flags, zone, grid
    /// cell, transform or class data, model and children, all the way down, as a comparison sees them (so not their stored
    /// pointers or runtime counters).
    /// </summary>
    internal static bool Interchangeable(WorldNode x, WorldNode y, int depth = 0, CancellationToken token = default) => Interchangeable(x, y, out _, depth, token);
    /// <param name="unchecked">Whether the check ran out of its budget, so the nodes count as different without being told apart.</param>
    internal static bool Interchangeable(WorldNode x, WorldNode y, out bool @unchecked, int depth = 0, CancellationToken token = default)
    {
        Memo memo = new(token);
        bool same = memo.Interchangeable(x, y, depth);
        @unchecked = !same && memo.ChecksExhausted;
        return same;
    }

    /// <summary>
    /// Pairs nodes of one name: identical copies first (the same contents all the way down, then the same structure,
    /// children's names and position), so that moving, adding or removing one copy leaves the others paired with themselves;
    /// then the same structure at the nearest position; then the nearest position; then order (positions that are not finite,
    /// or past <see cref="MaximumNearestPairs"/> candidate pairs of the name or <see cref="MaximumComparisonPairs"/> of the
    /// comparison, <see cref="WorldComparison.ApproximatePairing"/>).
    /// </summary>
    internal static List<(WorldNode A, WorldNode B)> PairUp(IReadOnlyList<WorldNode> mine, IReadOnlyList<WorldNode> others) => PairUp(mine, others, new(default));
    private static List<(WorldNode A, WorldNode B)> PairUp(IReadOnlyList<WorldNode> mine, IReadOnlyList<WorldNode> others, Memo memo)
    {
        List<(WorldNode, WorldNode)> pairs = [];
        List<WorldNode> restA = [.. mine], restB = [.. others];
        for (int pass = 0; pass < 2 && restA.Count > 0 && restB.Count > 0; pass++)
        {
            (ulong, Vector3) Key(WorldNode node) => pass == 0 ? (memo.Identity(node), Vector3.Zero) : (memo.Structure(node), Round(At(node)));
            Dictionary<(ulong, Vector3), Queue<WorldNode>> identical = [];
            foreach (var node in restB) { var key = Key(node); if (!identical.TryGetValue(key, out var queue)) identical[key] = queue = new(); queue.Enqueue(node); }
            HashSet<WorldNode> taken = new(ReferenceEqualityComparer.Instance); List<WorldNode> left = [];
            foreach (var node in restA)
                if (identical.TryGetValue(Key(node), out var queue) && queue.TryDequeue(out var other)) { pairs.Add((node, other)); taken.Add(other); }
                else left.Add(node);
            restA = left; restB = [.. restB.Where(n => !taken.Contains(n))];
        }
        foreach (bool sameStructure in new[] { true, false })
            if (restA.Count > 0 && restB.Count > 0) Nearest(ref restA, ref restB, sameStructure, memo, pairs);
        var orderedA = restA.OrderBy(n => Order(n, memo)).ToList(); var orderedB = restB.OrderBy(n => Order(n, memo)).ToList();
        for (int k = 0; k < Math.Min(orderedA.Count, orderedB.Count); k++) pairs.Add((orderedA[k], orderedB[k]));
        return pairs;
        static (ulong, float, float, float) Order(WorldNode node, Memo memo) { var at = Round(At(node)); return (memo.Structure(node), at.X, at.Y, at.Z); }
    }
    /// <summary>Remaining pairs of one name are all listed and sorted when there are at most this many.</summary>
    private const int AllPairs = 65536;
    /// <summary>The most candidate pairs of one name listed by position before the rest pair by order.</summary>
    public const int MaximumNearestPairs = 1 << 21;
    /// <summary>
    /// The most pairs of positions a comparison examines to pair copies nearest first, over all its names and places; past
    /// them, copies pair by order (<see cref="WorldComparison.ApproximatePairing"/>).
    /// </summary>
    public const long MaximumComparisonPairs = 1L << 24;

    /// <summary>
    /// Pairs nodes nearest first (ties by their order), as if every pair were listed by distance: rounds over a doubling
    /// radius list only the pairs within it, found on a grid of that cell size, so a large group lists few pairs and pairs the
    /// same way. A pair within the radius whose nodes are both left would have been taken in that round, so each round
    /// continues the full order exactly. Past <see cref="MaximumNearestPairs"/> listed or the comparison's
    /// <see cref="MaximumComparisonPairs"/> examined, the rest are left to pair by order.
    /// </summary>
    private static void Nearest(ref List<WorldNode> restA, ref List<WorldNode> restB, bool sameStructure, Memo memo, List<(WorldNode, WorldNode)> pairs)
    {
        List<WorldNode> a = restA, b = restB;
        Vector3[] at = [.. a.Concat(b).Select(At)];
        bool[] usedA = new bool[a.Count], usedB = new bool[b.Count];
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in at) if (Finite(p)) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        double span = Finite(max - min) ? Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z)) : 0;
        long listed = 0;
        bool Allowed(int i, int j) => !sameStructure || memo.Structure(a[i]) == memo.Structure(b[j]);
        double Distance(int i, int j) { Vector3 p = at[i], q = at[a.Count + j]; double x = (double)p.X - q.X, y = (double)p.Y - q.Y, z = (double)p.Z - q.Z; return x * x + y * y + z * z; }
        // One list for every round of the comparison: a round's pairs are sorted and taken before the next is listed.
        var candidates = memo.Candidates;
        for (double radius = Math.Max(1e-3, span / (1 << 30)); ; radius *= 2)
        {
            memo.Token.ThrowIfCancellationRequested();
            int[] ia = [.. Enumerable.Range(0, a.Count).Where(i => !usedA[i] && Finite(at[i]))], jb = [.. Enumerable.Range(0, b.Count).Where(j => !usedB[j] && Finite(at[a.Count + j]))];
            if (ia.Length == 0 || jb.Length == 0) break;
            bool all = (long)ia.Length * jb.Length <= AllPairs || radius >= 2 * span;
            // The pairs this round may still list, and examine, before the rest pair by order.
            long room = MaximumNearestPairs - listed, examinable = memo.Examinable;
            if (all && ((long)ia.Length * jb.Length > room || (long)ia.Length * jb.Length > examinable)) { memo.Approximate = true; break; }
            candidates.Clear();
            bool stopped = false;
            if (all) { foreach (int i in ia) foreach (int j in jb) if (Allowed(i, j)) candidates.Add((Distance(i, j), i, j)); memo.Examined((long)ia.Length * jb.Length); }
            else
            {
                // Cells as wide as the radius: a pair within it lies in neighbouring cells.
                Dictionary<(long, long, long), List<int>> cells = [];
                (long, long, long) Cell(Vector3 p) => ((long)Math.Floor(((double)p.X - min.X) / radius), (long)Math.Floor(((double)p.Y - min.Y) / radius), (long)Math.Floor(((double)p.Z - min.Z) / radius));
                foreach (int j in jb) { var cell = Cell(at[a.Count + j]); if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = []; list.Add(j); }
                long examined = 0;
                foreach (int i in ia)
                {
                    var (cx, cy, cz) = Cell(at[i]);
                    for (long x = cx - 1; x <= cx + 1 && !stopped; x++) for (long y = cy - 1; y <= cy + 1 && !stopped; y++) for (long z = cz - 1; z <= cz + 1 && !stopped; z++)
                                if (cells.TryGetValue((x, y, z), out var list))
                                    foreach (int j in list)
                                    {
                                        if (++examined > examinable) { stopped = true; break; }
                                        if ((examined & 0xFFFFF) == 0) memo.Token.ThrowIfCancellationRequested();
                                        double d = Distance(i, j);
                                        if (d > radius * radius || !Allowed(i, j)) continue;
                                        if (candidates.Count >= room) { stopped = true; break; }
                                        candidates.Add((d, i, j));
                                    }
                    if (stopped) break;
                }
                memo.Examined(examined);
            }
            if (stopped) { memo.Approximate = true; break; }
            listed += candidates.Count;
            candidates.Sort();
            foreach (var (_, i, j) in candidates)
                if (!usedA[i] && !usedB[j]) { usedA[i] = usedB[j] = true; pairs.Add((a[i], b[j])); }
            if (all) break;
        }
        candidates.Clear();
        restA = [.. a.Where((_, i) => !usedA[i])]; restB = [.. b.Where((_, j) => !usedB[j])];
        static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    }

    /// <summary>A node's structure (class, polygon and child counts six levels down), its children's names and its rounded position.</summary>
    internal static string PairKey(WorldNode node) => $"{new Memo(default).Structure(node):X16}|{Round(At(node))}";
    private static Vector3 At(WorldNode node) => node.Class == WorldNodeClass.Object3D && WorldUpdate.LocalMatrix(node) is { } m ? m.Translation : Vector3.Zero;
    /// <summary>Class data words a comparison skips: stored pointers and runtime counters.</summary>
    private static int[] Skipped(WorldNodeClass kind) => kind switch
    {
        WorldNodeClass.World => [0x04, 0x08, 0x0C, 0x80, 0x90, 0x94, 0x98, 0x9C, 0xA0, 0xA4],
        WorldNodeClass.Light => [0xDC, 0xE0],
        WorldNodeClass.Camera => [0, 4, 8, 12],
        _ => [],
    };

    /// <summary>
    /// What a comparison works out once, however many places share it: each node's structure and contents, each model's
    /// polygons, each pair of models' differences and which copies are indistinguishable.
    /// </summary>
    private sealed class Memo(CancellationToken token)
    {
        public WorldTextureComparison.Directory? ExpectedTextures { get; init; }
        public WorldTextureComparison.Directory? ActualTextures { get; init; }
        public IReadOnlyDictionary<WorldNode, WorldNode>? Counterparts { get; set; }
        public IReadOnlyDictionary<WorldNode, int>? ExpectedSlots { get; set; }
        public IReadOnlyDictionary<WorldNode, int>? ActualSlots { get; set; }
        public HashSet<string> RepeatedNames { get; set; } = new(StringComparer.Ordinal);
        public CancellationToken Token => token;
        private readonly Dictionary<(WorldNode, int), ulong> signatures = [];
        private readonly Dictionary<WorldNode, ulong> structures = new(ReferenceEqualityComparer.Instance), identities = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(WorldModel, WorldTextureComparison.Directory?, bool), Polygon[]> polygons = [];
        private readonly Dictionary<WorldModel, Shape> shapes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<WorldModel, ulong> models = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(WorldModel, WorldModel, bool), (string Field, string Expected, string Actual)[]> modelDifferences = [];
        private readonly Dictionary<(WorldNode, WorldNode, int), bool> interchangeable = [];
        /// <summary>
        /// Copies sharing copies could ask about the same nodes at very many places: past this many checks, copies count as
        /// different (<see cref="ChecksExhausted"/>).
        /// </summary>
        private int checks = 1 << 20;
        public bool ChecksExhausted => checks < 0;
        private readonly Dictionary<Group, List<(WorldNode A, WorldNode B)>> pairings = [];
        private long examinable = MaximumComparisonPairs;
        /// <summary>How many more pairs of positions <see cref="Nearest"/> may examine in this comparison.</summary>
        public long Examinable => Math.Max(0, examinable);
        public void Examined(long count) => examinable -= count;
        /// <summary>Whether some copies were left to pair by order (<see cref="WorldComparison.ApproximatePairing"/>).</summary>
        public bool Approximate { get; set; }
        /// <summary>The candidate pairs of <see cref="Nearest"/>'s current round (one list, so a comparison allocates it once).</summary>
        public List<(double Distance, int I, int J)> Candidates { get; } = [];
        /// <summary>How many characters of differences the comparison has described (<see cref="MaximumDescribedText"/>).</summary>
        public long DescribedText { get; set; }

        /// <summary>
        /// Whether two nodes themselves are the same as a comparison sees them, in the order it lists what differs: class;
        /// flags, zone and grid cell (a world member's); the transform, or the class data with the nodes it names (stored
        /// pointers, slots and runtime counters skipped); the model's retained values; the number of children. A world root's:
        /// its name and class data. With <paramref name="found"/>, every difference is added to it; without, the first ends the check.
        /// </summary>
        public bool OwnSame(WorldNode a, WorldNode b, bool worldChild, bool root, Found? found)
        {
            bool same = true;
            if (a.Name != b.Name && Differs("name", a.Name, b.Name)) return false;
            if (root) return ClassDataSame() && same;
            if (a.Class != b.Class) { Differs("class", a.Class, b.Class); return false; }
            uint carried = WorldGltf.CarriedFlags;
            if ((a.Flags & carried) != (b.Flags & carried) && Differs("flags.carried", $"{a.Flags & carried:X8}", $"{b.Flags & carried:X8}")) return false;
            if ((a.Flags & ~carried) != (b.Flags & ~carried) && Differs("flags.derived", $"{a.Flags & ~carried:X8}", $"{b.Flags & ~carried:X8}")) return false;
            if ((a.Zone & 0xFF) != (b.Zone & 0xFF) && Differs("zone", a.Zone & 0xFF, b.Zone & 0xFF)) return false;
            if (worldChild && (a.GridColumn, a.GridRow) != (b.GridColumn, b.GridRow) && Differs("cell", (a.GridColumn, a.GridRow), (b.GridColumn, b.GridRow))) return false;
            if (a.Class == WorldNodeClass.Object3D)
            {
                var ma = WorldUpdate.LocalMatrix(a) ?? Matrix4x4.Identity; var mb = WorldUpdate.LocalMatrix(b) ?? Matrix4x4.Identity;
                if (!Close(ma, mb) && Differs("matrix", new Shown(ma), new Shown(mb))) return false;
                if (ObjectFlags(a) != ObjectFlags(b)
                    && Differs("object.flags", $"{a.PayloadInt(0):X}", $"{b.PayloadInt(0):X}")) return false;
                // Alpha override, software colour and colour alpha are authored class data, even while inactive.
                // Only the cached world matrix (+0x60) is omitted; an identity local matrix does not disable appearance.
                for (int o = 4; o <= 0x14; o += 4)
                    if (Bits(a.PayloadFloat(o)) != Bits(b.PayloadFloat(o))
                        && Differs(o == 4 ? "object.alphaScale" : o == 0x14 ? "object.colorAlpha" : $"object.color+{o - 8}", a.PayloadFloat(o), b.PayloadFloat(o))) return false;
                for (int o = 0x18; o < 0x30; o += 4)
                    if (Bits(a.PayloadFloat(o)) != Bits(b.PayloadFloat(o))) { if (Differs("object.trs", o, $"{a.PayloadFloat(o)}/{b.PayloadFloat(o)}")) return false; break; }
            }
            if ((a.Model == null) != (b.Model == null)) { if (Differs("model", a.Model != null, b.Model != null)) return false; }
            // A model shared by many nodes is compared once.
            else if (a.Model != null && b.Model != null)
                foreach (var (field, x, y) in ModelDifferences(a.Model, b.Model, found == null)) if (Differs(field, x, y)) return false;
            if (a.Children.Count != b.Children.Count && Differs("children", new Listed(a.Children), new Listed(b.Children))) return false;
            // Class data last: a world's many cells must not crowd out the fields above.
            return (a.Class == WorldNodeClass.Object3D || ClassDataSame()) && same;

            // Records a difference; whether it ends the check.
            bool Differs(string field, object? x, object? y) { same = false; found?.Add(field, x, y); return found == null; }
            bool ClassDataSame()
            {
                var skip = Skipped(a.Class);
                if (a.Payload.Length != b.Payload.Length && Differs("data", a.Payload.Length, b.Payload.Length)) return false;
                for (int o = 0; o + 4 <= Math.Min(a.Payload.Length, b.Payload.Length); o += 4)
                    if (Array.IndexOf(skip, o) < 0 && BitConverter.ToUInt32(a.Payload, o) != BitConverter.ToUInt32(b.Payload, o)
                        && Differs($"data+{o}", BitConverter.ToSingle(a.Payload, o), BitConverter.ToSingle(b.Payload, o))) return false;
                // Slot numbers can change, but each reference must still name its established counterpart.
                if (a.Class == WorldNodeClass.Camera)
                    foreach (var (x, y, field) in new[] { (a.CameraWorld, b.CameraWorld, "camera.world"), (a.CameraWindow, b.CameraWindow, "camera.window"),
                        (a.CameraHorizon, b.CameraHorizon, "camera.horizon"), (a.CameraHorizonXZ, b.CameraHorizonXZ, "camera.horizonXZ") })
                        if (!ReferenceSame(x, y) && Differs(field, ReferenceLabel(x, true), ReferenceLabel(y, false))) return false;
                if (a.Class == WorldNodeClass.Light && !ReferencesSame(a.AttachedWorlds, b.AttachedWorlds) && Differs("light.worlds", ReferenceList(a.AttachedWorlds, true), ReferenceList(b.AttachedWorlds, false))) return false;
                if (a.Class != WorldNodeClass.World) return true;
                if (!ReferencesSame(a.WorldLights, b.WorldLights) && Differs("world.lights", ReferenceList(a.WorldLights, true), ReferenceList(b.WorldLights, false))) return false;
                if (!ReferencesSame(a.WorldSounds, b.WorldSounds) && Differs("world.sounds", ReferenceList(a.WorldSounds, true), ReferenceList(b.WorldSounds, false))) return false;
                if (a.Areas.Count != b.Areas.Count) return !Differs("world.areas", a.Areas.Count, b.Areas.Count);
                for (int i = 0; i < a.Areas.Count; i++)
                {
                    // Every area is compared; only the differences listed are described.
                    Token.ThrowIfCancellationRequested();
                    var x = a.Areas[i].Nodes; var y = b.Areas[i].Nodes;
                    if (!MembersSame(x, y) && Differs($"world.area{i}", ReferenceList(x, true), ReferenceList(y, false))) return false;
                }
                return true;
            }
            WorldNode? MatchReference(WorldNode x) => found == null ? x : Counterparts?.GetValueOrDefault(x);
            string ReferenceLabel(WorldNode? node, bool expected) => node == null ? "(none)" : !RepeatedNames.Contains(node.Name) ? node.Name : $"{node.Name} [slot {(expected ? ExpectedSlots : ActualSlots)?.GetValueOrDefault(node, -1) ?? -1}]";
            Listed ReferenceList(List<WorldNode> nodes, bool expected) => new(nodes.Select(n => ReferenceLabel(n, expected)));
            bool ReferenceSame(WorldNode? x, WorldNode? y) => x == null ? y == null : y != null && ReferenceEquals(MatchReference(x), y);
            bool ReferencesSame(List<WorldNode> x, List<WorldNode> y) => x.Count == y.Count && x.Where((node, i) => !ReferenceSame(node, y[i])).Any() == false;
            bool MembersSame(List<WorldNode> x, List<WorldNode> y)
            {
                if (x.Count != y.Count) return false;
                // Interchangeable compares the actual area contents recursively in ChildrenSame, not through a
                // cross-world counterpart map. Keep that semantic-copy check separate from stored reference identity.
                if (found == null) return true;
                Dictionary<WorldNode, int> counts = new(ReferenceEqualityComparer.Instance);
                foreach (var node in y) counts[node] = counts.GetValueOrDefault(node) + 1;
                foreach (var node in x)
                {
                    if (MatchReference(node) is not { } mapped || !counts.TryGetValue(mapped, out int remaining) || remaining == 0) return false;
                    counts[mapped] = remaining - 1;
                }
                return true;
            }
        }

        /// <summary>
        /// <see cref="PairUp"/>, once for each pair of lists of copies: a pair of nodes at many places, or parents holding the
        /// same children, pairs the same children the same way, at the cost of one.
        /// </summary>
        public List<(WorldNode A, WorldNode B)> Pairs(List<WorldNode> mine, List<WorldNode> others)
        {
            // A name only one side has pairs nothing; a name on one node each pairs at once.
            if (mine.Count == 0 || others.Count == 0) return [];
            if (mine.Count + others.Count <= 2) return PairUp(mine, others, this);
            Group key = new(mine, others);
            if (!pairings.TryGetValue(key, out var known)) pairings[key] = known = PairUp(mine, others, this);
            return known;
        }
        /// <summary>Two lists of nodes, equal when they hold the same nodes in the same order.</summary>
        private readonly struct Group(List<WorldNode> a, List<WorldNode> b) : IEquatable<Group>
        {
            private readonly List<WorldNode> a = a, b = b;
            private readonly int hash = Hash(a, b);
            public bool Equals(Group other) => Same(a, other.a) && Same(b, other.b);
            public override bool Equals(object? obj) => obj is Group other && Equals(other);
            public override int GetHashCode() => hash;
            private static bool Same(List<WorldNode> x, List<WorldNode> y)
            {
                if (x.Count != y.Count) return false;
                for (int i = 0; i < x.Count; i++) if (!ReferenceEquals(x[i], y[i])) return false;
                return true;
            }
            private static int Hash(List<WorldNode> a, List<WorldNode> b)
            {
                HashCode hash = new();
                foreach (var node in a) hash.Add(RuntimeHelpers.GetHashCode(node));
                hash.Add(a.Count);
                foreach (var node in b) hash.Add(RuntimeHelpers.GetHashCode(node));
                return hash.ToHashCode();
            }
        }

        /// <summary>The node's class, polygon and child counts six levels down, and its children's names.</summary>
        public ulong Structure(WorldNode node)
        {
            if (structures.TryGetValue(node, out ulong known)) return known;
            ulong hash = Signature(node, 0);
            foreach (var child in node.Children) hash = Mix(hash, Text(child.Name));
            return structures[node] = hash;
        }
        private ulong Signature(WorldNode node, int depth)
        {
            if (depth > 6) return 0;
            if (signatures.TryGetValue((node, depth), out ulong known)) return known;
            ulong hash = Mix(Mix((ulong)node.Class, node.Model == null ? ulong.MaxValue : (ulong)node.Model.Polygons.Count), (ulong)node.Children.Count);
            foreach (var child in node.Children) hash = Mix(hash, Signature(child, depth + 1));
            return signatures[(node, depth)] = hash;
        }

        /// <summary>The node's contents all the way down, rounded transforms included, its children in any order.</summary>
        public ulong Identity(WorldNode root)
        {
            if (identities.TryGetValue(root, out ulong known)) return known;
            HashSet<WorldNode> open = new(ReferenceEqualityComparer.Instance) { root };
            Stack<(WorldNode Node, int Next)> stack = new([(root, 0)]);
            while (stack.TryPop(out var top))
            {
                var (node, next) = top;
                if (next < node.Children.Count)
                {
                    stack.Push((node, next + 1));
                    var child = node.Children[next];
                    if (!identities.ContainsKey(child) && open.Add(child)) stack.Push((child, 0));
                    continue;
                }
                ulong children = 0;
                foreach (var child in node.Children) children += Mix(0, identities.GetValueOrDefault(child));
                identities[node] = Mix(Mix(Own(node), children), (ulong)node.Children.Count);
                open.Remove(node);
            }
            return identities[root];
        }
        private ulong Own(WorldNode node)
        {
            ulong hash = Mix(Mix(Mix(Text(node.Name), (ulong)node.Class), node.Flags), node.Zone & 0xFF);
            if (node.Class == WorldNodeClass.Object3D)
            {
                var m = WorldUpdate.LocalMatrix(node) ?? Matrix4x4.Identity;
                for (int r = 0; r < 4; r++) for (int c = 0; c < 3; c++) hash = Mix(hash, Bits(MathF.Round(m[r, c], 3)));
                hash = Mix(hash, ObjectFlags(node));
                for (int o = 4; o <= 0x14; o += 4) hash = Mix(hash, Bits(node.PayloadFloat(o)));
                for (int o = 0x18; o < 0x30; o += 4) hash = Mix(hash, Bits(node.PayloadFloat(o)));
            }
            else
            {
                var skip = Skipped(node.Class);
                for (int o = 0; o + 4 <= node.Payload.Length; o += 4) if (Array.IndexOf(skip, o) < 0) hash = Mix(hash, BitConverter.ToUInt32(node.Payload, o));
                // The nodes its class data names, by name as comparisons see them (a world's areas aside).
                if (node.Class == WorldNodeClass.Camera)
                    foreach (var named in new[] { node.CameraWorld, node.CameraWindow, node.CameraHorizon, node.CameraHorizonXZ }) hash = Mix(hash, named == null ? 0 : Text(named.Name));
                foreach (var named in node.AttachedWorlds) hash = Mix(hash, Text(named.Name));
                foreach (var named in node.WorldLights) hash = Mix(hash, Text(named.Name));
                foreach (var named in node.WorldSounds) hash = Mix(hash, Text(named.Name));
            }
            return Mix(hash, node.Model == null ? 0 : Model(node.Model));
        }
        private ulong Model(WorldModel model)
        {
            if (models.TryGetValue(model, out ulong known)) return known;
            ulong hash = Mix(Mix(Mix(model.Mode, model.Flags), (ulong)model.Points.Count << 32 | (uint)model.Morphs.Count), Bits(model.BoundsRadius));
            hash = Mix(Mix(Mix(hash, Bits(model.BoundsCentre.X)), Bits(model.BoundsCentre.Y)), Bits(model.BoundsCentre.Z));
            hash = Mix(Mix(Mix(Mix(hash, Bits(model.MorphFactor)), Bits(model.ScrollU)), Bits(model.ScrollV)), model.ScrollFrame);
            foreach (var point in model.Points) hash = Mix(hash, PointHash(point));
            ulong all = 0;
            var shape = Geometry(model);
            foreach (var polygon in model.Polygons) all = Mix(all, Polygon.HashOf(shape, polygon, TextureIdentity.Of(polygon.Material?.Texture, null, false)));
            return models[model] = Mix(Mix(hash, all), (ulong)model.Polygons.Count);
        }

        private Shape Geometry(WorldModel model)
        {
            if (!shapes.TryGetValue(model, out var shape)) shapes[model] = shape = new(model);
            return shape;
        }

        private Polygon[] Polygons(WorldModel model, WorldTextureComparison.Directory? directory = null, bool exact = false)
        {
            if (polygons.TryGetValue((model, directory, exact), out var known)) return known;
            var shape = Geometry(model);
            return polygons[(model, directory, exact)] = [.. model.Polygons.Select(p =>
            {
                var texture = TextureIdentity.Of(p.Material?.Texture, directory, exact);
                return new Polygon(shape, p, texture, Polygon.HashOf(shape, p, texture));
            })];
        }

        /// <summary>The differences of two models (each pair of models is compared once).</summary>
        public (string Field, string Expected, string Actual)[] ModelDifferences(WorldModel a, WorldModel b, bool sameWorld)
        {
            if (ReferenceEquals(a, b) && (sameWorld || ReferenceEquals(ExpectedTextures, ActualTextures))) return [];
            if (modelDifferences.TryGetValue((a, b, sameWorld), out var known)) return known;
            token.ThrowIfCancellationRequested();
            List<(string, string, string)> list = [];
            if (a.Mode != b.Mode || a.Flags != b.Flags) list.Add(("model.mode", $"{a.Mode}:{a.Flags:X}", $"{b.Mode}:{b.Flags:X}"));
            // Texture scrolling and the morph factor drive the model at run time from these values.
            if (Bits(a.ScrollU) != Bits(b.ScrollU) || Bits(a.ScrollV) != Bits(b.ScrollV) || a.ScrollFrame != b.ScrollFrame)
                list.Add(("model.scroll", $"{a.ScrollU} {a.ScrollV} {a.ScrollFrame}", $"{b.ScrollU} {b.ScrollV} {b.ScrollFrame}"));
            if (Bits(a.MorphFactor) != Bits(b.MorphFactor)) list.Add(("model.morphFactor", $"{a.MorphFactor}", $"{b.MorphFactor}"));
            // Point entries (lens flares) in order: their authored words and rounded points.
            if (a.Points.Count != b.Points.Count) list.Add(("model.points", $"{a.Points.Count}", $"{b.Points.Count}"));
            else
            {
                int first = -1, identical = 0;
                for (int i = 0; i < a.Points.Count; i++) if (PointSame(a.Points[i], b.Points[i])) identical++; else if (first < 0) first = i;
                if (first >= 0) list.Add(("model.points", $"{a.Points.Count}: entry {first} {Describe(a.Points[first])}", $"{b.Points.Count} ({identical} identical): entry {first} {Describe(b.Points[first])}"));
            }
            if (a.Morphs.Count != b.Morphs.Count) list.Add(("model.morphs", $"{a.Morphs.Count}", $"{b.Morphs.Count}"));
            if (!a.BoundsCentre.Equals(b.BoundsCentre) || Bits(a.BoundsRadius) != Bits(b.BoundsRadius)) list.Add(("model.sphere", $"{a.BoundsCentre} {a.BoundsRadius}", $"{b.BoundsCentre} {b.BoundsRadius}"));
            // Polygons carry the rest (their corners' normals and morph deltas, their materials' values) and compare as
            // multisets: a model may hold the same polygon twice.
            var pa = Polygons(a, sameWorld ? ActualTextures : ExpectedTextures, sameWorld && ActualTextures == null);
            var pb = Polygons(b, ActualTextures, sameWorld && ActualTextures == null);
            var onlyA = Polygon.Unmatched(pa, pb); var onlyB = Polygon.Unmatched(pb, pa);
            if (onlyA.Count > 0 || onlyB.Count > 0)
                list.Add(("model.polygons", $"{pa.Length}: {Polygon.Describe(onlyA.First)}", $"{pb.Length} ({pa.Length - onlyA.Count} identical): {Polygon.Describe(onlyB.First)}"));
            else
                for (int i = 0; i < pa.Length; i++)
                    if (!pa[i].Matches(pb[i]))
                    {
                        list.Add(("model.polygonOrder", $"polygon {i}: {Polygon.Describe(pa[i])}", $"polygon {i}: {Polygon.Describe(pb[i])}"));
                        break;
                    }
            return modelDifferences[(a, b, sameWorld)] = [.. list];
        }

        public bool Interchangeable(WorldNode x, WorldNode y, int depth)
        {
            if (ReferenceEquals(x, y)) return true;
            if (--checks < 0 || depth > 64 || x.Name != y.Name || x.Class != y.Class || x.Flags != y.Flags || (x.Zone & 0xFF) != (y.Zone & 0xFF)
                || (x.GridColumn, x.GridRow) != (y.GridColumn, y.GridRow) || x.Children.Count != y.Children.Count) return false;
            if ((checks & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            if (depth == 0 && !x.Parents.ToHashSet(ReferenceEqualityComparer.Instance).SetEquals(y.Parents)) return false;
            if (interchangeable.TryGetValue((x, y, depth), out bool known)) return known;
            // What a comparison reports of a pair, so copies that differ only where it looks past (stored pointers, slots,
            // runtime counters) are told apart no more than a comparison would; the grid cell counts wherever the node is,
            // since the engine reads it to find a node's place in the world (gwNodeGetWorldChild).
            bool same = OwnSame(x, y, true, false, null) && ChildrenSame(x, y, depth);
            interchangeable[(x, y, depth)] = same;
            return same;
        }
        /// <summary>The children as multisets: each of x's takes an unused one of y's indistinguishable from it, those of its structure and position first.</summary>
        private bool ChildrenSame(WorldNode x, WorldNode y, int depth)
        {
            if (!ChildrenSame(x.Children, y.Children, depth)) return false;
            // A World may hold a node only through an area table. Equal member names alone do not make two worlds
            // interchangeable: those names may identify different geometry or authored state.
            if (x.Class == WorldNodeClass.World)
            {
                if (x.Areas.Count != y.Areas.Count) return false;
                for (int i = 0; i < x.Areas.Count; i++)
                    if (!ChildrenSame(x.Areas[i].Nodes, y.Areas[i].Nodes, depth)) return false;
            }
            return true;
        }
        private bool ChildrenSame(List<WorldNode> x, List<WorldNode> y, int depth)
        {
            if (x.Count != y.Count) return false;
            if (x.Count == 0) return true;
            bool[] used = new bool[y.Count];
            Dictionary<(string, ulong, Vector3), List<int>> placed = [];
            Dictionary<string, List<int>> named = new(StringComparer.Ordinal);
            (string, ulong, Vector3) Key(WorldNode node) => (node.Name, Structure(node), Round(At(node)));
            for (int j = 0; j < y.Count; j++)
            {
                var child = y[j];
                if (!placed.TryGetValue(Key(child), out var list)) placed[Key(child)] = list = []; list.Add(j);
                if (!named.TryGetValue(child.Name, out list)) named[child.Name] = list = []; list.Add(j);
            }
            foreach (var child in x)
            {
                int found = placed.TryGetValue(Key(child), out var list) ? Take(list, child) : -1;
                if (found < 0 && named.TryGetValue(child.Name, out list)) found = Take(list, child);
                if (found < 0) return false;
                used[found] = true;
            }
            return true;
            // The first unused candidate indistinguishable from child; candidates taken meanwhile leave the list.
            int Take(List<int> list, WorldNode child)
            {
                for (int k = 0; k < list.Count; k++)
                {
                    int j = list[k];
                    if (used[j]) { list[k--] = list[^1]; list.RemoveAt(list.Count - 1); continue; }
                    if (Interchangeable(child, y[j], depth + 1)) return j;
                }
                return -1;
            }
        }
    }

    /// <summary>
    /// Whether a point entry's word at <paramref name="offset"/> is authored: not its vertex count (+12, which its points
    /// give) or one of its <see cref="WorldGltf.RuntimePointFields"/>, which reconstruction does not keep either.
    /// </summary>
    private static bool AuthoredPointWord(int offset)
    {
        if (offset == 12) return false;
        foreach (var (start, length) in WorldGltf.RuntimePointFields) if (offset >= start && offset < start + length) return false;
        return true;
    }
    /// <summary>Whether two point entries have the same authored words and rounded points, in order.</summary>
    private static bool PointSame(WorldPoint x, WorldPoint y)
    {
        if (x.Record.Length != y.Record.Length || x.Vertices.Length != y.Vertices.Length) return false;
        for (int o = 0; o + 4 <= x.Record.Length; o += 4)
            if (AuthoredPointWord(o) && BitConverter.ToUInt32(x.Record, o) != BitConverter.ToUInt32(y.Record, o)) return false;
        for (int k = 0; k < x.Vertices.Length; k++) if (!Round(x.Vertices[k]).Equals(Round(y.Vertices[k]))) return false;
        return true;
    }
    private static ulong PointHash(WorldPoint point)
    {
        ulong hash = Mix((ulong)point.Record.Length, (ulong)point.Vertices.Length);
        for (int o = 0; o + 4 <= point.Record.Length; o += 4) if (AuthoredPointWord(o)) hash = Mix(hash, BitConverter.ToUInt32(point.Record, o));
        foreach (var v in point.Vertices) { var r = Round(v); hash = Mix(Mix(Mix(hash, Bits(r.X)), Bits(r.Y)), Bits(r.Z)); }
        return hash;
    }
    /// <summary>A point entry as a difference shows it: its authored words, then its points (at most 400 characters).</summary>
    private static string Describe(WorldPoint point)
    {
        StringBuilder text = new();
        for (int o = 0; o + 4 <= point.Record.Length; o += 4) if (AuthoredPointWord(o)) text.Append($"{BitConverter.ToUInt32(point.Record, o):X8} ");
        text.Append('|');
        for (int k = 0; k < point.Vertices.Length && text.Length <= 400; k++) text.Append(k > 0 ? ";" : "").Append(Round(point.Vertices[k]));
        return text.Length > 400 ? text.ToString(0, 400) + "…" : text.ToString();
    }

    /// <summary>A model's points as its polygons compare them: positions, normals and morph deltas, rounded.</summary>
    private sealed class Shape(WorldModel model)
    {
        public Vector3[] Vertices { get; } = [.. model.Vertices.Select(Round)];
        public Vector3[] Normals { get; } = [.. model.Normals.Select(Round)];
        public Vector3[] Morphs { get; } = [.. model.Morphs.Select(Round)];
    }

    /// <summary>
    /// A polygon as models compare it: its corners at rounded positions with their UVs, normals and morph deltas, its
    /// material's values and its draw attributes.
    /// </summary>
    private readonly record struct TextureIdentity(string Name, int Occurrence, WorldTexture? Exact)
    {
        public static TextureIdentity Of(WorldTexture? texture, WorldTextureComparison.Directory? directory, bool exact)
        {
            if (texture == null) return new("", 0, null);
            var id = directory?.Target(texture);
            return new(id?.Name ?? texture.Name, id?.Occurrence ?? -1, exact ? texture : null);
        }
        public bool Equals(TextureIdentity other) => Occurrence == other.Occurrence && ReferenceEquals(Exact, other.Exact) && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
        public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Name), Occurrence, Exact);
        public ulong Hash { get { ulong hash = 0xCBF29CE484222325; foreach (char c in Name) hash = (hash ^ char.ToUpperInvariant(c)) * 0x100000001B3; return Mix(hash, unchecked((ulong)Occurrence)); } }
        public string Describe(WorldTexture? texture) => texture == null ? "" : JsonData.ShownText(texture.Name) + (Occurrence > 0 ? $" [{Occurrence}]" : "");
    }

    private readonly record struct Polygon(Shape Shape, WorldPolygon Source, TextureIdentity Texture, ulong Hash)
    {
        private Vector3 Corner(int i) => Source.Vertices[i] is int v && v >= 0 && v < Shape.Vertices.Length ? Shape.Vertices[v] : new(float.NaN);
        private (bool, Vector2) Uv(int i) => Source.Uvs.Length == 0 ? (false, default) : (true, i < Source.Uvs.Length ? Source.Uvs[i] : new(float.NaN));
        /// <summary>The corner's normal, when the polygon stores normals (the engine lights it with them).</summary>
        private (bool, Vector3) Normal(int i) => Source.Normals.Length == 0 ? (false, default)
            : (true, i < Source.Normals.Length && Source.Normals[i] is int n && n >= 0 && n < Shape.Normals.Length ? Shape.Normals[n] : new(float.NaN));
        /// <summary>The corner's morph delta: zero where the model has none for its vertex, as glTF exports write it.</summary>
        private Vector3 Morph(int i) => Source.Vertices[i] is int v && v >= 0 && v < Shape.Morphs.Length ? Shape.Morphs[v] : Vector3.Zero;
        /// <summary>The material flags a world stores: 0x100 from the texture, 0x400 only with one (<see cref="GameZWriter"/>).</summary>
        private static uint Stored(WorldMaterial m) => m.Texture != null ? m.Flags | 0x100u : m.Flags & ~0x500u;
        /// <summary>The polygon flags a world stores besides those its corners and normals give (the count, 0x200).</summary>
        private static uint Flags(WorldPolygon p) => p.Flags & ~0x2FFu;
        public static ulong HashOf(Shape shape, WorldPolygon source, TextureIdentity texture)
        {
            Polygon p = new(shape, source, texture, 0);
            var m = source.Material;
            ulong hash = Mix(Mix(Mix(Mix((ulong)source.Vertices.Length, (ulong)source.Priority), Flags(source)), source.Zone), source.Normals.Length > 0 ? 1UL : 0);
            hash = m == null ? Mix(hash, ulong.MaxValue) : Mix(Mix(Mix(Mix(Mix(Mix(hash, texture.Hash), Bits(m.Color.X)), Bits(m.Color.Y)), Bits(m.Color.Z)), Stored(m)), m.Soil);
            if (m != null) hash = Mix(Mix(Mix(Mix(hash, m.PackedColor), Bits(m.Field14)), Bits(m.Field18)), Bits(m.Field1C));
            for (int i = 0; i < source.Vertices.Length; i++)
            {
                var (c, (has, uv)) = (p.Corner(i), p.Uv(i));
                hash = Mix(Mix(Mix(Mix(hash, Bits(c.X)), Bits(c.Y)), Bits(c.Z)), has ? Bits(uv.X) | (ulong)Bits(uv.Y) << 32 : ulong.MaxValue);
                if (p.Normal(i) is (true, var n)) hash = Mix(Mix(Mix(hash, Bits(n.X)), Bits(n.Y)), Bits(n.Z));
                if (p.Morph(i) is var d && d != Vector3.Zero) hash = Mix(Mix(Mix(hash, Bits(d.X)), Bits(d.Y)), Bits(d.Z));
            }
            return hash;
        }
        public bool Matches(Polygon other)
        {
            WorldPolygon a = Source, b = other.Source;
            if (Hash != other.Hash || a.Vertices.Length != b.Vertices.Length || a.Priority != b.Priority || Flags(a) != Flags(b) || a.Zone != b.Zone
                || (a.Normals.Length > 0) != (b.Normals.Length > 0)) return false;
            if (a.Material is { } x ? b.Material is not { } y || Texture != other.Texture || !x.Color.Equals(y.Color) || Stored(x) != Stored(y) || x.Soil != y.Soil
                || x.PackedColor != y.PackedColor || !x.Field14.Equals(y.Field14) || !x.Field18.Equals(y.Field18) || !x.Field1C.Equals(y.Field1C) : b.Material != null) return false;
            for (int i = 0; i < a.Vertices.Length; i++)
                if (!Corner(i).Equals(other.Corner(i)) || !Uv(i).Equals(other.Uv(i)) || !Normal(i).Equals(other.Normal(i)) || !Morph(i).Equals(other.Morph(i))) return false;
            return true;
        }
        /// <summary>How many of <paramref name="a"/> are left after removing one match in <paramref name="b"/> for each, and the first of them.</summary>
        public static (int Count, Polygon? First) Unmatched(Polygon[] a, Polygon[] b)
        {
            Dictionary<Polygon, int> counts = new(Comparer.Instance);
            foreach (var p in b) counts[p] = counts.GetValueOrDefault(p) + 1;
            int left = 0; Polygon? first = null;
            foreach (var p in a)
                if (counts.TryGetValue(p, out int n) && n > 0) counts[p] = n - 1;
                else { left++; first ??= p; }
            return (left, first);
        }
        /// <summary>The polygon as a difference shows it (at most 400 characters).</summary>
        public static string Describe(Polygon? polygon)
        {
            if (polygon is not { } p) return "";
            StringBuilder text = new();
            // Each corner: position|UV, then |n and its normal, |m and its morph delta where it has them.
            for (int i = 0; i < p.Source.Vertices.Length && text.Length <= 400; i++)
            {
                text.Append(i > 0 ? ";" : "").Append($"{p.Corner(i)}|{(p.Uv(i) is (true, var uv) ? uv.ToString() : "")}");
                if (p.Normal(i) is (true, var normal)) text.Append($"|n{normal}");
                if (p.Morph(i) is var delta && delta != Vector3.Zero) text.Append($"|m{delta}");
            }
            var m = p.Source.Material;
            text.Append($"#{p.Texture.Describe(m?.Texture)}{m?.Color}{(m == null ? null : Stored(m)):X}");
            if (m != null && (m.PackedColor != (m.Texture != null ? 0x7FFF : 0) || m.Field14 != 0 || m.Field18 != 0.5f || m.Field1C != 0.5f)) text.Append($"c{m.PackedColor:X}/{m.Field14}/{m.Field18}/{m.Field1C}");
            text.Append($"s{m?.Soil}p{p.Source.Priority}f{Flags(p.Source):X}z{p.Source.Zone:X}n{p.Source.Normals.Length > 0}");
            return text.Length > 400 ? text.ToString(0, 400) + "…" : text.ToString();
        }
        private sealed class Comparer : IEqualityComparer<Polygon>
        {
            public static readonly Comparer Instance = new();
            public bool Equals(Polygon x, Polygon y) => x.Matches(y);
            public int GetHashCode(Polygon p) => (int)p.Hash ^ (int)(p.Hash >> 32);
        }
    }

    /// <summary>Names joined by commas, kept to about <see cref="MaximumText"/> characters; a longer list ends with how many it holds.</summary>
    private static string Names(IEnumerable<string> names)
    {
        StringBuilder text = new(); int count = 0, shown = 0;
        foreach (var name in names) { if (text.Length < MaximumText) text.Append(shown++ > 0 ? "," : "").Append(name); count++; }
        return shown < count ? text.Append($",… ({count} in all)").ToString() : text.ToString();
    }
    private static ulong Text(string text) { ulong hash = 0xCBF29CE484222325; foreach (char c in text) hash = (hash ^ c) * 0x100000001B3; return hash; }
    /// <summary>A float's bits, with 0 and -0 alike and every NaN alike, as <see cref="float.Equals(float)"/> compares them.</summary>
    private static uint Bits(float value) => value == 0 ? 0 : float.IsNaN(value) ? 0x7FC00000 : BitConverter.SingleToUInt32Bits(value);
    /// <summary>A deterministic mix of a hash and a value (keys must order the same way in every run).</summary>
    private static ulong Mix(ulong hash, ulong value)
    {
        ulong z = (hash ^ value * 0xC2B2AE3D27D4EB4FUL) + 0x9E3779B97F4A7C15UL + (hash << 6) + (hash >> 2);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    private static Vector3 Round(Vector3 v) => new(MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3));
    // At identity the allocator's TRS/identity/authored-matrix/cache bits can differ without changing the transform.
    // Alpha override (0x02), visibility (0x04) and other retained bits still describe different behavior.
    private static uint ObjectFlags(WorldNode node) => unchecked((uint)node.PayloadInt(0))
        & (Close(WorldUpdate.LocalMatrix(node) ?? Matrix4x4.Identity, Matrix4x4.Identity) ? ~0x39u : uint.MaxValue);
    private static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) if (Math.Abs(a[r, c] - b[r, c]) > 1e-3f * (1 + Math.Abs(a[r, c]))) return false;
        return true;
    }
    private static string Format(Matrix4x4 m) => $"[{m.M11:G4} {m.M12:G4} {m.M13:G4}; {m.M21:G4} {m.M22:G4} {m.M23:G4}; {m.M31:G4} {m.M32:G4} {m.M33:G4}; {m.M41:G6} {m.M42:G6} {m.M43:G6}]";
}
