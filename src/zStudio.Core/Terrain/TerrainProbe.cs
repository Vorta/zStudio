using System.Numerics;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Terrain;

/// <summary>What the altitude probe finds in one node at a point: the height, the polygon's zones and soil, and the node's flags and zone.</summary>
public readonly record struct TerrainHit(float Height, uint ZoneWord, uint Soil, uint Flags, byte NodeZone, string Node);

/// <summary>Probe comparison of two sets of terrain nodes, as the acceptance of a conversion reports it.</summary>
/// <remarks>
/// <see cref="Mismatches"/> counts samples whose hits differ in number, zones, soil or node attributes;
/// <see cref="HeightOnly"/> those where only heights differ, by at most <see cref="MaximumHeightDifference"/>.
/// <see cref="Revealed"/> counts samples where the second set finds ground the first hid from the probe: geometry
/// overhanging into a neighbouring cell, which queries from that cell do not search.
/// </remarks>
public sealed record TerrainProbeReport(int Samples, int Hits, int Mismatches, int HeightOnly, float MaximumHeightDifference, IReadOnlyList<string> Examples)
{
    public int Revealed { get; init; }
    /// <summary>The spacing the samples took (wider than requested over an area that would exceed <see cref="TerrainProbe.MaximumSamples"/>).</summary>
    public float Spacing { get; init; }
    public bool Complete { get; init; } = true;
    public string? Limitation { get; init; }
}

/// <summary>
/// The engine's altitude probe in plan view (UpdateCameraVariantFromCameraPos, retail 0x406470, with the polygon search
/// at 0x484960): in each node, the first polygon in entry order that faces up, contains the point and whose plane lies at
/// or below the probe's start (<see cref="Top"/>) there; a near-vertical polygon is passed over. Nodes must have
/// identity transforms (terrain pieces do); the hits are listed by physical height, retaining encounter order at equal
/// height. Reported heights are rounded to two decimals only after ordering.
/// </summary>
public static class TerrainProbe
{
    private const float Tolerance = 1e-4f;
    /// <summary>The probe line starts at y = 500 (0x43FA0000); a polygon whose plane is higher there is passed over.</summary>
    public const float Top = 500;

    /// <summary>Returns a complete bounded probe, or throws if its aggregate node/polygon work exceeds the allowance.</summary>
    public static List<TerrainHit> At(IEnumerable<WorldNode> nodes, float x, float z, CancellationToken token = default)
        => At(nodes, x, z, token, ProbeWorkBudget.MaximumProbeWork);
    internal static List<TerrainHit> At(IEnumerable<WorldNode> nodes, float x, float z, CancellationToken token, long maximumWork)
    {
        try { return At(nodes, x, z, new ProbeWorkBudget(maximumWork, token, "The altitude probe exceeded its bounded node or polygon-work budget; no complete result is available.")); }
        catch (ProbeWorkLimitException ex) { throw new InvalidDataException(ex.Message, ex); }
    }
    private static List<TerrainHit> At(IEnumerable<WorldNode> nodes, float x, float z, ProbeWorkBudget budget)
    {
        budget.Take(0);
        List<(TerrainHit Hit, float Height, int Order)> hits = [];
        foreach (var node in nodes)
        {
            budget.Take(1);
            if (node.Model is not { } model) continue;
            foreach (var polygon in model.Polygons)
            {
                budget.Take(1L + polygon.Vertices.Length);
                if (Height(model, polygon, x, z, budget) is float y && y <= Top)
                {
                    budget.Take(1);
                    hits.Add((new(MathF.Round(y, 2), polygon.Zone, polygon.Material?.Soil ?? 0, node.Flags & WorldGltf.CarriedFlags, (byte)node.Zone, node.Name), y, hits.Count));
                    break;
                }
            }
        }
        // Sorting by attributes hides a changed first hit at equal height. The engine keeps the first candidate
        // whose height is strictly higher than the previous best; rounded display heights cannot decide that order.
        // Reserve a conservative comparison/move allowance for introsort and the final result before doing either.
        budget.Take(hits.Count < 2 ? hits.Count : (long)hits.Count * (2 * BitOperations.Log2((uint)hits.Count) + 17));
        hits.Sort((a, b) => a.Height != b.Height ? a.Height.CompareTo(b.Height) : a.Order.CompareTo(b.Order));
        budget.Take(0);
        return hits.Select(h => h.Hit).ToList();
    }

    /// <summary>The height where an upward-facing polygon covers (x, z), or null.</summary>
    public static float? Height(WorldModel model, WorldPolygon polygon, float x, float z) => Height(model, polygon, x, z, null);
    private static float? Height(WorldModel model, WorldPolygon polygon, float x, float z, ProbeWorkBudget? budget)
    {
        var v = polygon.Vertices;
        if (v.Length < 3) return null;
        // Newell normal; the probe only hits polygons that face up.
        Vector3 normal = Vector3.Zero;
        for (int i = 0; i < v.Length; i++)
        {
            if ((i & 1023) == 0) budget?.Take(0);
            var a = model.Vertices[v[i]]; var b = model.Vertices[v[(i + 1) % v.Length]];
            normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        // A vertical polygon (no plan-view area, up to rounding) is never under a vertical probe line.
        if (normal.Y <= 1e-6f * normal.Length()) return null;
        // Every edge keeps the point on its inner side, within the probe's tolerance.
        double sign = 0;
        for (int i = 0; i < v.Length; i++)
        {
            if ((i & 1023) == 0) budget?.Take(0);
            var a = model.Vertices[v[i]]; var b = model.Vertices[v[(i + 1) % v.Length]];
            sign += (double)a.X * b.Z - (double)b.X * a.Z;
        }
        if (Math.Abs(sign) < 1e-6) return null;
        sign = Math.Sign(sign);
        for (int i = 0; i < v.Length; i++)
        {
            if ((i & 1023) == 0) budget?.Take(0);
            var a = model.Vertices[v[i]]; var b = model.Vertices[v[(i + 1) % v.Length]];
            double cross = ((double)b.X - a.X) * (z - (double)a.Z) - ((double)b.Z - a.Z) * (x - (double)a.X);
            if (sign * cross < -Tolerance) return null;
        }
        var p = model.Vertices[v[0]];
        return p.Y - (normal.X * (x - p.X) + normal.Z * (z - p.Z)) / normal.Y;
    }

    /// <summary>
    /// Samples a grid of points (offset so they never fall on cell lines) over the nodes' plan-view bounds and compares
    /// the probe's hits in <paramref name="before"/> and <paramref name="after"/>: heights, polygon zones, soils, node flags and zones.
    /// </summary>
    /// <remarks>
    /// With <paramref name="grid"/> (the world's area grid), a probe at a point searches only the nodes of its cell and the
    /// overflow list, as the engine's does; each node's cell and encounter order come from its own world's membership,
    /// and both worlds share the grid. Standalone nodes without an owning world use the supplied order. This samples
    /// candidate hits; it does not simulate every runtime gate, vehicle sample or player-state-dependent selection.
    /// </remarks>
    /// <summary>The most sample points a comparison takes; a larger area samples more sparsely.</summary>
    public const double MaximumSamples = 4_000_000;
    public static TerrainProbeReport Compare(IReadOnlyList<WorldNode> before, IReadOnlyList<WorldNode> after, float spacing, CancellationToken token = default, WorldNode? grid = null)
        => Compare(before, after, spacing, token, grid, 1_000_000, 200_000_000);
    internal static TerrainProbeReport Compare(IReadOnlyList<WorldNode> before, IReadOnlyList<WorldNode> after, float spacing, CancellationToken token, WorldNode? grid, long maximumIndexEntries, long maximumWork)
    {
        if (!float.IsFinite(spacing) || spacing <= 0) throw new ArgumentOutOfRangeException(nameof(spacing));
        try { return CompareCore(before, after, spacing, token, grid, new(maximumWork, token, ComparisonLimitation), new(maximumIndexEntries, token, ComparisonLimitation)); }
        catch (ProbeWorkLimitException ex) { return new(0, 0, 0, 0, 0, []) { Complete = false, Limitation = ex.Message, Spacing = spacing }; }
    }
    private const string ComparisonLimitation = "The altitude comparison exceeded its bounded index or polygon-work budget; no comparison result is available.";
    private static TerrainProbeReport CompareCore(IReadOnlyList<WorldNode> before, IReadOnlyList<WorldNode> after, float spacing, CancellationToken token, WorldNode? grid, ProbeWorkBudget work, ProbeWorkBudget index)
    {
        token.ThrowIfCancellationRequested();
        Dictionary<WorldNode, (int, int)>? cellsA = null, cellsB = null;
        if (grid != null)
        {
            (before, cellsA) = EncounterOrder(before, work, index);
            (after, cellsB) = EncounterOrder(after, work, index);
        }
        ModelBounds bounds = new(work, index);
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity, maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        bool any = false;
        foreach (var node in before.Concat(after))
        {
            work.Take(1);
            if (node.Model is not { Vertices.Count: > 0 } model) continue;
            var box = bounds.Of(model);
            minX = Math.Min(minX, box.X); minZ = Math.Min(minZ, box.Y);
            maxX = Math.Max(maxX, box.Z); maxZ = Math.Max(maxZ, box.W);
            any = true;
        }
        if (!any) return new(0, 0, 0, 0, 0, []);
        // At most MaximumSamples points: a wider spacing over a large area (and integer steps, which never stall).
        const float offsetX = 0.37f, offsetZ = 0.29f;
        double width = (double)maxX - minX - offsetX, depth = (double)maxZ - minZ - offsetZ;
        if (!double.IsFinite(width) || !double.IsFinite(depth)) throw new ProbeWorkLimitException(ComparisonLimitation);
        long countX, countZ;
        while (true)
        {
            double columns = Math.Max(0, Math.Floor(width / spacing) + 1), rows = Math.Max(0, Math.Floor(depth / spacing) + 1);
            if (columns * rows <= MaximumSamples) { countX = (long)columns; countZ = (long)rows; break; }
            spacing *= (float)Math.Max(1.01, Math.Sqrt(columns * rows / MaximumSamples));
            if (!float.IsFinite(spacing)) throw new ProbeWorkLimitException(ComparisonLimitation);
        }
        var indexA = Index(before, spacing * 8, index, work, bounds); var indexB = Index(after, spacing * 8, index, work, bounds);
        cellsA ??= Cells(before, grid, index, work); cellsB ??= Cells(after, grid, index, work);
        int samples = 0, hits = 0, mismatches = 0, heightOnly = 0, revealed = 0; float maximum = 0; List<string> examples = [];
        for (long i = 0; i < countX; i++)
        {
            token.ThrowIfCancellationRequested();
            float x = minX + offsetX + i * spacing;
            for (long j = 0; j < countZ; j++)
            {
                work.Take(1);
                float z = minZ + offsetZ + j * spacing;
                samples++;
                var cell = grid == null ? (-1, -1) : PointCell(grid, x, z);
                var a = At(Visible(indexA(x, z), cellsA, cell), x, z, work); var b = At(Visible(indexB(x, z), cellsB, cell), x, z, work);
                // Ground the engine never found at this point in the first set, because its node sits in another cell.
                if (grid != null && !Same(a, b) && Same(At(indexA(x, z), x, z, work), b)) { revealed++; continue; }
                hits += a.Count;
                if (a.Count == b.Count && a.Zip(b).All(p => p.First.ZoneWord == p.Second.ZoneWord && p.First.Soil == p.Second.Soil && p.First.Flags == p.Second.Flags && p.First.NodeZone == p.Second.NodeZone))
                {
                    float difference = a.Zip(b).Select(p => Math.Abs(p.First.Height - p.Second.Height)).DefaultIfEmpty(0).Max();
                    if (difference <= 0.02f) continue;
                    heightOnly++; maximum = Math.Max(maximum, difference);
                    if (difference < 1) continue;
                }
                mismatches++;
                if (examples.Count < 16) examples.Add($"({x:0.##}, {z:0.##}): before {Describe(a)}; after {Describe(b)}");
            }
        }
        return new(samples, hits, mismatches, heightOnly, maximum, examples) { Revealed = revealed, Spacing = spacing };
        static bool Same(List<TerrainHit> a, List<TerrainHit> b) => a.Count == b.Count && a.Zip(b).All(p => Math.Abs(p.First.Height - p.Second.Height) <= 0.02f && p.First.ZoneWord == p.Second.ZoneWord
            && p.First.Soil == p.Second.Soil && p.First.Flags == p.Second.Flags && p.First.NodeZone == p.Second.NodeZone);
        static string Describe(List<TerrainHit> hits) => hits.Count == 0 ? "nothing" : string.Join(", ", hits.Take(8).Select(h => $"{JsonData.ShownText(h.Node, 128)} y {h.Height} zones 0x{h.ZoneWord:X8} soil {h.Soil} flags 0x{h.Flags:X8} zone {h.NodeZone}")) + (hits.Count > 8 ? $" … ({hits.Count} hits)" : "");
    }
    /// <summary>
    /// Callers commonly supply slot-ordered nodes. Restore the owning world's actual cell/overflow and child order;
    /// the before and after nodes belong to separate worlds even though the comparison uses the same grid geometry.
    /// </summary>
    private static (IReadOnlyList<WorldNode> Nodes, Dictionary<WorldNode, (int, int)>? Cells) EncounterOrder(IReadOnlyList<WorldNode> nodes, ProbeWorkBudget work, ProbeWorkBudget index)
    {
        const string unknown = "The altitude comparison cannot establish one owning world's encounter order for these nodes; no comparison result is available.";
        HashSet<WorldNode> selected = new(ReferenceEqualityComparer.Instance);
        WorldNode? owner = null;
        bool standalone = false;
        foreach (var node in nodes)
        {
            work.Take(1);
            if (selected.Contains(node)) continue;
            index.Take(1); selected.Add(node);
            HashSet<WorldNode> visited = new(ReferenceEqualityComparer.Instance);
            Stack<WorldNode> pending = new();
            index.Take(1); visited.Add(node); pending.Push(node);
            WorldNode? own = null;
            while (pending.TryPop(out var current))
            {
                work.Take(1L + current.Parents.Count);
                if (current.Class == WorldNodeClass.World)
                {
                    if (own != null && !ReferenceEquals(own, current)) throw new ProbeWorkLimitException(unknown);
                    own = current;
                    continue;
                }
                foreach (var parent in current.Parents)
                {
                    if (visited.Contains(parent)) continue;
                    index.Take(1); visited.Add(parent); pending.Push(parent);
                }
            }
            if (own == null) standalone = true;
            else
            {
                if (owner != null && !ReferenceEquals(owner, own)) throw new ProbeWorkLimitException(unknown);
                owner = own;
            }
        }
        if (owner == null) return (nodes, null);
        if (standalone) throw new ProbeWorkLimitException(unknown);
        List<WorldNode> ordered = [];
        Dictionary<WorldNode, (int, int)> locations = new(ReferenceEqualityComparer.Instance);
        int columns = owner.PayloadInt(0x78);
        for (int i = 0; i < owner.Areas.Count; i++)
        {
            work.Take(1);
            if (columns <= 0) throw new ProbeWorkLimitException(unknown);
            Append(owner.Areas[i].Nodes, (i % columns, i / columns));
        }
        Append(owner.Children, (-1, -1));
        if (locations.Count != selected.Count) throw new ProbeWorkLimitException(unknown);
        return (ordered, locations);

        void Append(IReadOnlyList<WorldNode> roots, (int, int) cell)
        {
            foreach (var root in roots)
            {
                work.Take(1);
                Stack<WorldNode> pending = new();
                index.Take(1); pending.Push(root);
                while (pending.TryPop(out var node))
                {
                    work.Take(1L + node.Children.Count);
                    if (selected.Contains(node))
                    {
                        // This comparison indexes nodes, not path-specific instances. A shared node in different
                        // cells needs distinct occurrence identities; never label every copy with its first parent's cell.
                        if (locations.TryGetValue(node, out var previous) && previous != cell)
                            throw new ProbeWorkLimitException("The altitude comparison cannot represent a shared node in different cells; no comparison result is available.");
                        if (!locations.ContainsKey(node)) { index.Take(1); locations.Add(node, cell); }
                        index.Take(1); ordered.Add(node);
                    }
                    // Do not deduplicate shared occurrences: the engine encounters them through each parent.
                    index.Take(node.Children.Count);
                    for (int i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
                }
            }
        }
    }
    /// <summary>
    /// Each node's grid cell: the cell the world placed it (or its top-level ancestor) in, (−1, −1) for the world's own list.
    /// </summary>
    private static Dictionary<WorldNode, (int, int)>? Cells(IReadOnlyList<WorldNode> nodes, WorldNode? grid, ProbeWorkBudget index, ProbeWorkBudget work)
    {
        if (grid == null) return null;
        Dictionary<WorldNode, (int, int)> cells = new(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
        {
            work.Take(1);
            if (cells.ContainsKey(node)) continue;
            index.Take(1);
            var top = node;
            for (int depth = 0; depth < 256 && top.Parents.Count > 0; depth++)
            {
                work.Take(1L + top.Parents.Count);
                bool inWorld = false;
                for (int i = 0; i < top.Parents.Count; i++)
                {
                    if ((i & 1023) == 0) work.Take(0);
                    if (top.Parents[i].Class == WorldNodeClass.World) { inWorld = true; break; }
                }
                if (inWorld) break;
                top = top.Parents[0];
            }
            cells[node] = (top.GridColumn, top.GridRow);
        }
        return cells;
    }
    /// <summary>The cell a probe at a point searches (with the world's own list): the one holding it, none outside the world.</summary>
    private static (int, int) PointCell(WorldNode grid, float x, float z) => WorldUpdate.GridIndex(grid, x, x, z, z);
    private static IEnumerable<WorldNode> Visible(IEnumerable<WorldNode> nodes, Dictionary<WorldNode, (int, int)>? cells, (int, int) cell) =>
        cells == null ? nodes : nodes.Where(n => cells[n] is var c && (c == (-1, -1) || c == cell));
    /// <summary>The nodes whose plan-view bounds hold a point, through a coarse grid.</summary>
    private static Func<float, float, IEnumerable<WorldNode>> Index(IReadOnlyList<WorldNode> nodes, float cell, ProbeWorkBudget budget, ProbeWorkBudget work, ModelBounds bounds)
    {
        Dictionary<(int, int), List<(WorldNode Node, Vector4 Box)>> grid = [];
        foreach (var node in nodes)
        {
            work.Take(1);
            if (node.Model is not { Vertices.Count: > 0 } model) continue;
            Vector4 box = bounds.Of(model);
            double width = Math.Floor(box.Z / (double)cell) - Math.Floor(box.X / (double)cell) + 1;
            double height = Math.Floor(box.W / (double)cell) - Math.Floor(box.Y / (double)cell) + 1;
            double entries = width * height;
            if (!double.IsFinite(entries) || entries > long.MaxValue || new[] { box.X, box.Y, box.Z, box.W }.Any(v => Math.Abs(v / (double)cell) >= int.MaxValue - 1)) throw new ProbeWorkLimitException(ComparisonLimitation);
            budget.Take((long)entries);
            work.Take((long)entries);
            for (int i = (int)MathF.Floor(box.X / cell); i <= (int)MathF.Floor(box.Z / cell); i++)
            {
                work.Take(0);
                for (int j = (int)MathF.Floor(box.Y / cell); j <= (int)MathF.Floor(box.W / cell); j++)
                {
                    if ((j & 1023) == 0) work.Take(0);
                    if (!grid.TryGetValue((i, j), out var list)) grid[(i, j)] = list = [];
                    list.Add((node, box));
                }
            }
        }
        return (x, z) => grid.TryGetValue(((int)MathF.Floor(x / cell), (int)MathF.Floor(z / cell)), out var list)
            ? list.Where(e => { work.Take(1); return x >= e.Box.X - 0.01f && x <= e.Box.Z + 0.01f && z >= e.Box.Y - 0.01f && z <= e.Box.W + 0.01f; }).Select(e => e.Node) : [];
    }

    /// <summary>Terrain probes require identity transforms; cache the actual model vertices, never stale node boxes.</summary>
    private sealed class ModelBounds(ProbeWorkBudget work, ProbeWorkBudget index)
    {
        private readonly Dictionary<WorldModel, Vector4> boxes = new(ReferenceEqualityComparer.Instance);

        public Vector4 Of(WorldModel model)
        {
            if (boxes.TryGetValue(model, out var box)) return box;
            work.Take(model.Vertices.Count);
            index.Take(1);
            box = new(float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < model.Vertices.Count; i++)
            {
                if ((i & 1023) == 0) work.Take(0);
                var p = model.Vertices[i];
                box = new(Math.Min(box.X, p.X), Math.Min(box.Y, p.Z), Math.Max(box.Z, p.X), Math.Max(box.W, p.Z));
            }
            boxes.Add(model, box);
            return box;
        }
    }
}
