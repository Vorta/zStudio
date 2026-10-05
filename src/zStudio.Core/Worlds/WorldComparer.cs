using System.Numerics;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>A difference between two worlds, at a node path.</summary>
public sealed record WorldDifference(string Path, string Field, string Expected, string Actual);

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
    internal WorldComparisonNode(string path, string name, WorldNode? expected, WorldNode? actual) { Path = path; Name = name; Expected = expected; Actual = actual; }
    /// <summary>The node's name path, as <see cref="WorldDifference.Path"/> gives it.</summary>
    public string Path { get; }
    public string Name { get; }
    public WorldNode? Expected { get; }
    public WorldNode? Actual { get; }
    /// <summary>What differs in the pair itself (its fields, the number of its children).</summary>
    public List<WorldDifference> Differences { get; } = [];
    /// <summary>The pair's children, matched by name and structure, in the expected world's order; the actual world's own after them.</summary>
    public List<WorldComparisonNode> Children { get; } = [];
    public WorldComparisonStatus Status => Expected == null ? WorldComparisonStatus.OnlyActual : Actual == null ? WorldComparisonStatus.OnlyExpected
        : Differences.Count > 0 ? WorldComparisonStatus.Changed : WorldComparisonStatus.Same;
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
}

/// <summary>Two worlds compared as one merged tree of nodes, with their differences and node slots.</summary>
public sealed class WorldComparison
{
    /// <summary>The world node's pair (its members below it), then the nodes neither world places under a parent.</summary>
    public required IReadOnlyList<WorldComparisonNode> Roots { get; init; }
    public required IReadOnlyDictionary<WorldNode, int> ExpectedSlots { get; init; }
    public required IReadOnlyDictionary<WorldNode, int> ActualSlots { get; init; }
    /// <summary>Every difference, flattened (at most the limit given).</summary>
    public required List<WorldDifference> Differences { get; init; }
    /// <summary>Names more than one node has in either world, with the node lookups by each name find.</summary>
    public required IReadOnlyList<WorldNameBinding> Bindings { get; init; }
    /// <summary>Merged tree nodes by status (a node shared by several parents counts once per place).</summary>
    public required IReadOnlyDictionary<WorldComparisonStatus, int> Counts { get; init; }
    /// <summary>Whether the merged tree stopped growing at <see cref="WorldComparer.MaximumTreeNodes"/>.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Compares two worlds by meaning: nodes are matched by name path (repeated names by their structure, children and
/// position, then in order), and each pair's class, carried and derived flags, zone, local transform, class data, model
/// geometry and grid cell are compared, along with the world, its lights and the texture names. Node slots, stored
/// pointers, child order and polygon order do not matter; <see cref="CompareTree"/> also reports the slots and which
/// repeated names lookups would bind elsewhere.
/// </summary>
public static class WorldComparer
{
    /// <summary>The most nodes the merged tree holds (a node under several parents appears under each).</summary>
    public const int MaximumTreeNodes = 500_000;

    public static List<WorldDifference> Compare(GameZWorld expected, GameZWorld actual, int limit = 10_000) => CompareTree(expected, actual, limit).Differences;

    public static WorldComparison CompareTree(GameZWorld expected, GameZWorld actual, int limit = 10_000, CancellationToken token = default)
    {
        List<WorldDifference> differences = [];
        List<WorldComparisonNode> roots = [];
        int made = 0; bool truncated = false;
        void Add(WorldComparisonNode? node, string path, string field, object? a, object? b)
        {
            WorldDifference difference = new(path, field, $"{a}", $"{b}");
            node?.Differences.Add(difference);
            if (differences.Count < limit) differences.Add(difference);
        }
        WorldComparisonNode? New(string path, string name, WorldNode? a, WorldNode? b)
        {
            if (made >= MaximumTreeNodes) { truncated = true; return null; }
            made++;
            return new(path, name, a, b);
        }
        var worldA = expected.Nodes.FirstOrDefault(n => n.Class == WorldNodeClass.World); var worldB = actual.Nodes.FirstOrDefault(n => n.Class == WorldNodeClass.World);
        if (worldA == null || worldB == null) Add(null, "", "world", worldA?.Name, worldB?.Name);
        else
        {
            var world = New("world", worldA.Name, worldA, worldB)!;
            roots.Add(world);
            CompareClassData(world, worldA, worldB, "world");
            Match(world, "world", Members(worldA), Members(worldB), true, 0);
        }
        // The nodes no parent holds (templates the build loaded, cameras, lights): a merged level of their own.
        WorldComparisonNode detached = new("", "", null, null);
        Match(detached, "", expected.Nodes.Where(n => n.Parents.Count == 0 && n.Class != WorldNodeClass.World).ToList(), actual.Nodes.Where(n => n.Parents.Count == 0 && n.Class != WorldNodeClass.World).ToList(), false, 0);
        roots.AddRange(detached.Children);
        var texturesA = expected.Textures.Select(t => t.Name.ToLowerInvariant()).ToHashSet(); var texturesB = actual.Textures.Select(t => t.Name.ToLowerInvariant()).ToHashSet();
        foreach (var t in texturesA.Except(texturesB).Order()) Add(null, "textures", "missing", t, "");
        foreach (var t in texturesB.Except(texturesA).Order()) Add(null, "textures", "extra", "", t);

        Dictionary<WorldComparisonStatus, int> counts = Enum.GetValues<WorldComparisonStatus>().ToDictionary(s => s, _ => 0);
        Dictionary<WorldNode, WorldNode> counterpart = new(ReferenceEqualityComparer.Instance);
        Dictionary<WorldNode, List<WorldComparisonNode>> places = new(ReferenceEqualityComparer.Instance);
        int Summarize(WorldComparisonNode node)
        {
            counts[node.Status]++;
            if (node.Expected != null)
            {
                if (node.Actual != null) counterpart.TryAdd(node.Expected, node.Actual);
                if (!places.TryGetValue(node.Expected, out var list)) places[node.Expected] = list = [];
                list.Add(node);
            }
            int below = 0;
            foreach (var child in node.Children) below += Summarize(child) + (child.Status == WorldComparisonStatus.Same ? 0 : 1);
            node.ChangedBelow = below;
            return below;
        }
        foreach (var root in roots) Summarize(root);
        var slotsA = GameZWriter.NodeSlots(expected); var slotsB = GameZWriter.NodeSlots(actual);
        List<WorldNameBinding> bindings = [];
        var byNameB = actual.Nodes.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var group in expected.Nodes.GroupBy(n => n.Name))
        {
            token.ThrowIfCancellationRequested();
            if (!byNameB.TryGetValue(group.Key, out var others) || group.Count() < 2 && others.Count < 2) continue;
            var first = group.MaxBy(n => slotsA[n])!; var found = others.MaxBy(n => slotsB[n])!;
            var match = counterpart.GetValueOrDefault(first);
            WorldNameBinding binding = new(group.Key, group.Count(), others.Count, first, match, found,
                match != null && !ReferenceEquals(match, found) && Interchangeable(match, found, 0));
            bindings.Add(binding);
            if (!binding.Same) foreach (var place in places.GetValueOrDefault(first) ?? []) place.BindsElsewhere = true;
        }
        return new() { Roots = roots, ExpectedSlots = slotsA, ActualSlots = slotsB, Differences = differences, Bindings = bindings, Counts = counts, Truncated = truncated };

        static List<WorldNode> Members(WorldNode world) => [.. world.Children.Concat(world.Areas.SelectMany(a => a.Nodes)).Distinct(ReferenceEqualityComparer.Instance).Cast<WorldNode>()];

        void Match(WorldComparisonNode parent, string path, List<WorldNode> a, List<WorldNode> b, bool worldChildren, int depth)
        {
            token.ThrowIfCancellationRequested();
            Dictionary<WorldNode, WorldNode> pairs = new(ReferenceEqualityComparer.Instance);
            HashSet<WorldNode> paired = new(ReferenceEqualityComparer.Instance);
            foreach (var group in a.GroupBy(n => n.Name))
            {
                var others = b.Where(n => n.Name == group.Key).ToList(); var mine = group.ToList();
                if (mine.Count != others.Count) Add(null, $"{path}/{group.Key}", "count", mine.Count, others.Count);
                foreach (var (x, y) in PairUp(mine, others)) { pairs[x] = y; paired.Add(y); }
            }
            foreach (var name in b.Select(n => n.Name).Distinct().Except(a.Select(n => n.Name))) Add(null, $"{path}/{name}", "extra", "", name);
            foreach (var node in a)
            {
                string at = $"{path}/{node.Name}";
                var child = pairs.TryGetValue(node, out var other) ? CompareNode(at, node, other, worldChildren, depth) : Only(at, node, true, depth);
                if (child != null) parent.Children.Add(child);
            }
            foreach (var node in b.Where(n => !paired.Contains(n)))
                if (Only($"{path}/{node.Name}", node, false, depth) is { } child) parent.Children.Add(child);
        }

        // A node only one world has, with its subtree.
        WorldComparisonNode? Only(string path, WorldNode node, bool expectedSide, int depth)
        {
            var result = New(path, node.Name, expectedSide ? node : null, expectedSide ? null : node);
            if (result == null || depth > 256) return result;
            foreach (var child in node.Children)
                if (Only($"{path}/{child.Name}", child, expectedSide, depth + 1) is { } inner) result.Children.Add(inner);
            return result;
        }

        WorldComparisonNode? CompareNode(string path, WorldNode a, WorldNode b, bool worldChild, int depth)
        {
            var node = New(path, a.Name, a, b);
            if (node == null || depth > 256) return node;
            if (a.Class != b.Class) { Add(node, path, "class", a.Class, b.Class); return node; }
            uint carried = WorldGltf.CarriedFlags;
            if ((a.Flags & carried) != (b.Flags & carried)) Add(node, path, "flags.carried", $"{a.Flags & carried:X8}", $"{b.Flags & carried:X8}");
            if ((a.Flags & ~carried) != (b.Flags & ~carried)) Add(node, path, "flags.derived", $"{a.Flags & ~carried:X8}", $"{b.Flags & ~carried:X8}");
            if ((a.Zone & 0xFF) != (b.Zone & 0xFF)) Add(node, path, "zone", a.Zone & 0xFF, b.Zone & 0xFF);
            if (worldChild && (a.GridColumn, a.GridRow) != (b.GridColumn, b.GridRow)) Add(node, path, "cell", (a.GridColumn, a.GridRow), (b.GridColumn, b.GridRow));
            if (a.Class == WorldNodeClass.Object3D)
            {
                var ma = WorldUpdate.LocalMatrix(a) ?? Matrix4x4.Identity; var mb = WorldUpdate.LocalMatrix(b) ?? Matrix4x4.Identity;
                if (!Close(ma, mb)) Add(node, path, "matrix", Format(ma), Format(mb));
                if ((a.PayloadInt(0) & 0x3F) != (b.PayloadInt(0) & 0x3F) && !(Close(ma, Matrix4x4.Identity) && Close(mb, Matrix4x4.Identity))) Add(node, path, "object.flags", $"{a.PayloadInt(0):X}", $"{b.PayloadInt(0):X}");
                for (int o = 0x18; o < 0x30; o += 4) if (a.PayloadFloat(o) != b.PayloadFloat(o)) { Add(node, path, "object.trs", o, $"{a.PayloadFloat(o)}/{b.PayloadFloat(o)}"); break; }
            }
            else CompareClassData(node, a, b, path);
            if ((a.Model == null) != (b.Model == null)) Add(node, path, "model", a.Model != null, b.Model != null);
            else if (a.Model != null && b.Model != null) CompareModel(node, path, a.Model, b.Model);
            if (a.Children.Count != b.Children.Count) Add(node, path, "children", string.Join(",", a.Children.Select(c => c.Name)), string.Join(",", b.Children.Select(c => c.Name)));
            Match(node, path, a.Children, b.Children, false, depth + 1);
            return node;
        }

        void CompareModel(WorldComparisonNode node, string path, WorldModel a, WorldModel b)
        {
            if (a.Mode != b.Mode || a.Flags != b.Flags) Add(node, path, "model.mode", $"{a.Mode}:{a.Flags:X}", $"{b.Mode}:{b.Flags:X}");
            if (a.Points.Count != b.Points.Count) Add(node, path, "model.points", a.Points.Count, b.Points.Count);
            if (a.Morphs.Count != b.Morphs.Count) Add(node, path, "model.morphs", a.Morphs.Count, b.Morphs.Count);
            if (a.BoundsCentre != b.BoundsCentre || a.BoundsRadius != b.BoundsRadius) Add(node, path, "model.sphere", $"{a.BoundsCentre} {a.BoundsRadius}", $"{b.BoundsCentre} {b.BoundsRadius}");
            // Polygons compare as multisets: a model may hold the same polygon twice.
            var pa = Polygons(a); var pb = Polygons(b);
            var onlyA = Remaining(pa, pb); var onlyB = Remaining(pb, pa);
            if (onlyA.Count > 0 || onlyB.Count > 0)
                Add(node, path, "model.polygons", $"{pa.Count}: {Bounded(onlyA.FirstOrDefault())}", $"{pb.Count} ({pa.Count - onlyA.Count} identical): {Bounded(onlyB.FirstOrDefault())}");
        }

        void CompareClassData(WorldComparisonNode node, WorldNode a, WorldNode b, string path)
        {
            // Stored pointers and runtime counters are not compared.
            HashSet<int> skip = a.Class switch
            {
                WorldNodeClass.World => [0x04, 0x08, 0x0C, 0x80, 0x90, 0x94, 0x98, 0x9C, 0xA0, 0xA4],
                WorldNodeClass.Light => [0xDC, 0xE0],
                WorldNodeClass.Camera => [0, 4, 8, 12],
                _ => [],
            };
            for (int o = 0; o + 4 <= a.Payload.Length; o += 4)
                if (!skip.Contains(o) && BitConverter.ToUInt32(a.Payload, o) != BitConverter.ToUInt32(b.Payload, o))
                    Add(node, path, $"data+{o}", BitConverter.ToSingle(a.Payload, o), BitConverter.ToSingle(b.Payload, o));
            if (a.Class == WorldNodeClass.Camera)
                foreach (var (x, y, field) in new[] { (a.CameraWorld, b.CameraWorld, "camera.world"), (a.CameraWindow, b.CameraWindow, "camera.window"), (a.CameraHorizon, b.CameraHorizon, "camera.horizon") })
                    if (x?.Name != y?.Name) Add(node, path, field, x?.Name, y?.Name);
            if (a.Class == WorldNodeClass.World)
            {
                if (!a.WorldLights.Select(l => l.Name).SequenceEqual(b.WorldLights.Select(l => l.Name))) Add(node, path, "world.lights", string.Join(",", a.WorldLights.Select(l => l.Name)), string.Join(",", b.WorldLights.Select(l => l.Name)));
                if (a.Areas.Count != b.Areas.Count) Add(node, path, "world.areas", a.Areas.Count, b.Areas.Count);
                else for (int i = 0; i < a.Areas.Count; i++)
                        if (!a.Areas[i].Nodes.Select(n => n.Name).Order().SequenceEqual(b.Areas[i].Nodes.Select(n => n.Name).Order())) Add(node, path, $"world.area{i}", string.Join(",", a.Areas[i].Nodes.Select(n => n.Name).Order()), string.Join(",", b.Areas[i].Nodes.Select(n => n.Name).Order()));
            }
        }
    }

    /// <summary>
    /// Whether two nodes of one world are indistinguishable: the same parents, and the same name, class, flags, zone, grid
    /// cell, transform or class data, model and children, all the way down.
    /// </summary>
    internal static bool Interchangeable(WorldNode x, WorldNode y, int depth)
    {
        if (ReferenceEquals(x, y)) return true;
        if (depth > 64 || x.Name != y.Name || x.Class != y.Class || x.Flags != y.Flags || (x.Zone & 0xFF) != (y.Zone & 0xFF)
            || (x.GridColumn, x.GridRow) != (y.GridColumn, y.GridRow) || x.Children.Count != y.Children.Count) return false;
        if (depth == 0 && !x.Parents.ToHashSet(ReferenceEqualityComparer.Instance).SetEquals(y.Parents)) return false;
        if (x.Class == WorldNodeClass.Object3D)
        {
            if (!Close(WorldUpdate.LocalMatrix(x) ?? Matrix4x4.Identity, WorldUpdate.LocalMatrix(y) ?? Matrix4x4.Identity) || (x.PayloadInt(0) & 0x3F) != (y.PayloadInt(0) & 0x3F)) return false;
            for (int o = 0x18; o < 0x30; o += 4) if (x.PayloadFloat(o) != y.PayloadFloat(o)) return false;
        }
        else if (!x.Payload.AsSpan().SequenceEqual(y.Payload)) return false;
        if (!ReferenceEquals(x.Model, y.Model))
        {
            if (x.Model == null || y.Model == null || x.Model.Mode != y.Model.Mode || x.Model.Flags != y.Model.Flags || x.Model.Points.Count != y.Model.Points.Count
                || x.Model.Morphs.Count != y.Model.Morphs.Count || x.Model.BoundsCentre != y.Model.BoundsCentre || x.Model.BoundsRadius != y.Model.BoundsRadius) return false;
            var px = Polygons(x.Model); var py = Polygons(y.Model);
            if (px.Count != py.Count || Remaining(px, py).Count > 0) return false;
        }
        var cx = x.Children.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(PairKey, StringComparer.Ordinal).ToList();
        var cy = y.Children.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(PairKey, StringComparer.Ordinal).ToList();
        for (int i = 0; i < cx.Count; i++) if (!Interchangeable(cx[i], cy[i], depth + 1)) return false;
        return true;
    }

    /// <summary>
    /// Pairs nodes of one name: identical copies (structure, children's names and position) first, so that moving, adding or
    /// removing one copy leaves the others paired with themselves; then the same structure at the nearest position; then the
    /// nearest position; then order (only order past <see cref="MaximumNearestPairs"/> candidate pairs).
    /// </summary>
    internal static List<(WorldNode A, WorldNode B)> PairUp(IReadOnlyList<WorldNode> mine, IReadOnlyList<WorldNode> others)
    {
        List<(WorldNode, WorldNode)> pairs = [];
        Dictionary<WorldNode, (string Key, string Structure, Vector3 At)> keys = new(ReferenceEqualityComparer.Instance);
        (string Key, string Structure, Vector3 At) Keys(WorldNode node)
        {
            if (keys.TryGetValue(node, out var known)) return known;
            var at = node.Class == WorldNodeClass.Object3D && WorldUpdate.LocalMatrix(node) is { } m ? m.Translation : Vector3.Zero;
            string structure = $"{Signature(node)}|{string.Join(",", node.Children.Select(c => c.Name))}";
            return keys[node] = ($"{structure}|{Round(at)}", structure, at);
        }
        Dictionary<string, Queue<WorldNode>> identical = new(StringComparer.Ordinal);
        foreach (var node in others) { string key = Keys(node).Key; if (!identical.TryGetValue(key, out var queue)) identical[key] = queue = new(); queue.Enqueue(node); }
        HashSet<WorldNode> taken = new(ReferenceEqualityComparer.Instance);
        List<WorldNode> restA = [];
        foreach (var node in mine)
            if (identical.TryGetValue(Keys(node).Key, out var queue) && queue.Count > 0) { var other = queue.Dequeue(); pairs.Add((node, other)); taken.Add(other); }
            else restA.Add(node);
        List<WorldNode> restB = [.. others.Where(n => !taken.Contains(n))];
        foreach (bool sameStructure in new[] { true, false })
        {
            if (restA.Count == 0 || restB.Count == 0 || (long)restA.Count * restB.Count > MaximumNearestPairs) break;
            List<(float Distance, int I, int J)> candidates = [];
            for (int i = 0; i < restA.Count; i++)
                for (int j = 0; j < restB.Count; j++)
                    if (!sameStructure || Keys(restA[i]).Structure == Keys(restB[j]).Structure)
                        candidates.Add((Vector3.DistanceSquared(Keys(restA[i]).At, Keys(restB[j]).At), i, j));
            candidates.Sort();
            bool[] usedA = new bool[restA.Count], usedB = new bool[restB.Count];
            foreach (var (_, i, j) in candidates)
                if (!usedA[i] && !usedB[j]) { usedA[i] = usedB[j] = true; pairs.Add((restA[i], restB[j])); }
            restA = [.. restA.Where((_, i) => !usedA[i])]; restB = [.. restB.Where((_, j) => !usedB[j])];
        }
        var orderedA = restA.OrderBy(n => Keys(n).Key, StringComparer.Ordinal).ToList();
        var orderedB = restB.OrderBy(n => Keys(n).Key, StringComparer.Ordinal).ToList();
        for (int k = 0; k < Math.Min(orderedA.Count, orderedB.Count); k++) pairs.Add((orderedA[k], orderedB[k]));
        return pairs;
    }
    /// <summary>Candidate pairs of one name compared by position before the rest pair by order.</summary>
    private const int MaximumNearestPairs = 65536;

    internal static string PairKey(WorldNode node)
    {
        var at = node.Class == WorldNodeClass.Object3D && WorldUpdate.LocalMatrix(node) is { } m ? Round(m.Translation) : Vector3.Zero;
        return $"{Signature(node)}|{string.Join(",", node.Children.Select(c => c.Name))}|{at}";
    }
    private static string Signature(WorldNode node, int depth = 0) => depth > 6 ? "" :
        $"{node.Class}:{node.Model?.Polygons.Count}:{node.Children.Count}[{string.Join(",", node.Children.Select(c => Signature(c, depth + 1)))}]";
    private static List<string> Polygons(WorldModel m) => m.Polygons.Select(p => string.Join(";", p.Vertices.Select((v, i) => $"{Round(m.Vertices[v])}|{(p.Uvs.Length > 0 ? p.Uvs[i].ToString() : "")}"))
        + $"#{p.Material?.Texture?.Name}{p.Material?.Color}{p.Material?.Flags & 0xFF:X}s{p.Material?.Soil}p{p.Priority}f{p.Flags & 0x100:X}z{p.Zone:X}n{p.Normals.Length > 0}").ToList();
    /// <summary>The entries of <paramref name="a"/> left after removing one match in <paramref name="b"/> for each.</summary>
    private static List<string> Remaining(List<string> a, List<string> b)
    {
        Dictionary<string, int> counts = [];
        foreach (var s in b) counts[s] = counts.GetValueOrDefault(s) + 1;
        List<string> left = [];
        foreach (var s in a) if (counts.GetValueOrDefault(s) > 0) counts[s]--; else left.Add(s);
        return left;
    }
    private static string Bounded(string? text) => text == null ? "" : text.Length > 400 ? text[..400] + "…" : text;
    private static Vector3 Round(Vector3 v) => new(MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3));
    private static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) if (Math.Abs(a[r, c] - b[r, c]) > 1e-3f * (1 + Math.Abs(a[r, c]))) return false;
        return true;
    }
    private static string Format(Matrix4x4 m) => $"[{m.M11:G4} {m.M12:G4} {m.M13:G4}; {m.M21:G4} {m.M22:G4} {m.M23:G4}; {m.M31:G4} {m.M32:G4} {m.M33:G4}; {m.M41:G6} {m.M42:G6} {m.M43:G6}]";
}
