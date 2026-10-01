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
}

/// <summary>
/// The engine's altitude probe in plan view (UpdateCameraVariantFromCameraPos, retail 0x406470, with the polygon search
/// at 0x484960): in each node, the first polygon in entry order that faces up, contains the point and whose plane lies at
/// or below the probe's start (<see cref="Top"/>) there; a near-vertical polygon is passed over. Nodes must have
/// identity transforms (terrain pieces do); the hits are listed by height.
/// </summary>
public static class TerrainProbe
{
    private const float Tolerance = 1e-4f;
    /// <summary>The probe line starts at y = 500 (0x43FA0000); a polygon whose plane is higher there is passed over.</summary>
    public const float Top = 500;

    public static List<TerrainHit> At(IEnumerable<WorldNode> nodes, float x, float z)
    {
        List<TerrainHit> hits = [];
        foreach (var node in nodes)
        {
            if (node.Model is not { } model) continue;
            foreach (var polygon in model.Polygons)
                if (Height(model, polygon, x, z) is float y && y <= Top)
                {
                    hits.Add(new(MathF.Round(y, 2), polygon.Zone, polygon.Material?.Soil ?? 0, node.Flags & WorldGltf.CarriedFlags, (byte)node.Zone, node.Name));
                    break;
                }
        }
        hits.Sort((a, b) => a.Height != b.Height ? a.Height.CompareTo(b.Height) : a.ZoneWord != b.ZoneWord ? a.ZoneWord.CompareTo(b.ZoneWord) : a.Flags.CompareTo(b.Flags));
        return hits;
    }

    /// <summary>The height where an upward-facing polygon covers (x, z), or null.</summary>
    public static float? Height(WorldModel model, WorldPolygon polygon, float x, float z)
    {
        var v = polygon.Vertices;
        if (v.Length < 3) return null;
        // Newell normal; the probe only hits polygons that face up.
        Vector3 normal = Vector3.Zero;
        for (int i = 0; i < v.Length; i++) { var a = model.Vertices[v[i]]; var b = model.Vertices[v[(i + 1) % v.Length]]; normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y)); }
        // A vertical polygon (no plan-view area, up to rounding) is never under a vertical probe line.
        if (normal.Y <= 1e-6f * normal.Length()) return null;
        // Every edge keeps the point on its inner side, within the probe's tolerance.
        double sign = 0;
        for (int i = 0; i < v.Length; i++)
        {
            var a = model.Vertices[v[i]]; var b = model.Vertices[v[(i + 1) % v.Length]];
            sign += (double)a.X * b.Z - (double)b.X * a.Z;
        }
        if (Math.Abs(sign) < 1e-6) return null;
        sign = Math.Sign(sign);
        for (int i = 0; i < v.Length; i++)
        {
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
    /// overflow list, as the engine's does; each node's cell is the one its own world recorded for it, and both worlds share the grid.
    /// </remarks>
    /// <summary>The most sample points a comparison takes; a larger area samples more sparsely.</summary>
    public const double MaximumSamples = 4_000_000;
    public static TerrainProbeReport Compare(IReadOnlyList<WorldNode> before, IReadOnlyList<WorldNode> after, float spacing, CancellationToken token = default, WorldNode? grid = null)
    {
        if (!(spacing > 0)) throw new ArgumentOutOfRangeException(nameof(spacing));
        var all = before.Concat(after).Where(n => n.Model is { Vertices.Count: > 0 }).ToArray();
        if (all.Length == 0) return new(0, 0, 0, 0, 0, []);
        float minX = all.Min(n => n.Model!.Vertices.Min(p => p.X)), maxX = all.Max(n => n.Model!.Vertices.Max(p => p.X));
        float minZ = all.Min(n => n.Model!.Vertices.Min(p => p.Z)), maxZ = all.Max(n => n.Model!.Vertices.Max(p => p.Z));
        // At most MaximumSamples points: a wider spacing over a large area (and integer steps, which never stall).
        const float offsetX = 0.37f, offsetZ = 0.29f;
        double columns = Math.Max(0, Math.Floor((maxX - minX - offsetX) / spacing) + 1), rows = Math.Max(0, Math.Floor((maxZ - minZ - offsetZ) / spacing) + 1);
        if (columns * rows > MaximumSamples) spacing *= (float)Math.Sqrt(columns * rows / MaximumSamples);
        long countX = Math.Max(0, (long)Math.Floor((maxX - minX - offsetX) / spacing) + 1), countZ = Math.Max(0, (long)Math.Floor((maxZ - minZ - offsetZ) / spacing) + 1);
        var indexA = Index(before, spacing * 8); var indexB = Index(after, spacing * 8);
        var cellsA = Cells(before, grid); var cellsB = Cells(after, grid);
        int samples = 0, hits = 0, mismatches = 0, heightOnly = 0, revealed = 0; float maximum = 0; List<string> examples = [];
        for (long i = 0; i < countX; i++)
        {
            token.ThrowIfCancellationRequested();
            float x = minX + offsetX + i * spacing;
            for (long j = 0; j < countZ; j++)
            {
                float z = minZ + offsetZ + j * spacing;
                samples++;
                var cell = grid == null ? (-1, -1) : PointCell(grid, x, z);
                var a = At(Visible(indexA(x, z), cellsA, cell), x, z); var b = At(Visible(indexB(x, z), cellsB, cell), x, z);
                // Ground the engine never found at this point in the first set, because its node sits in another cell.
                if (grid != null && !Same(a, b) && Same(At(indexA(x, z), x, z), b)) { revealed++; continue; }
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
        static string Describe(List<TerrainHit> hits) => hits.Count == 0 ? "nothing" : string.Join(", ", hits.Select(h => $"{h.Node} y {h.Height} zones 0x{h.ZoneWord:X8} soil {h.Soil} flags 0x{h.Flags:X8} zone {h.NodeZone}"));
    }
    /// <summary>
    /// Each node's grid cell: the cell the world placed it (or its top-level ancestor) in, (−1, −1) for the world's own list.
    /// </summary>
    private static Dictionary<WorldNode, (int, int)>? Cells(IReadOnlyList<WorldNode> nodes, WorldNode? grid)
    {
        if (grid == null) return null;
        Dictionary<WorldNode, (int, int)> cells = new(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
        {
            var top = node;
            for (int depth = 0; depth < 256 && top.Parents.Count > 0 && !top.Parents.Any(p => p.Class == WorldNodeClass.World); depth++) top = top.Parents[0];
            cells[node] = (top.GridColumn, top.GridRow);
        }
        return cells;
    }
    /// <summary>The cell a probe at a point searches (with the world's own list): the one holding it, none outside the world.</summary>
    private static (int, int) PointCell(WorldNode grid, float x, float z) => WorldUpdate.GridIndex(grid, x, x, z, z);
    private static IEnumerable<WorldNode> Visible(IEnumerable<WorldNode> nodes, Dictionary<WorldNode, (int, int)>? cells, (int, int) cell) =>
        cells == null ? nodes : nodes.Where(n => cells[n] is var c && (c == (-1, -1) || c == cell));
    /// <summary>The nodes whose plan-view bounds hold a point, through a coarse grid.</summary>
    private static Func<float, float, IEnumerable<WorldNode>> Index(IReadOnlyList<WorldNode> nodes, float cell)
    {
        Dictionary<(int, int), List<(WorldNode Node, Vector4 Box)>> grid = [];
        foreach (var node in nodes)
        {
            if (node.Model is not { Vertices.Count: > 0 } model) continue;
            Vector4 box = new(model.Vertices.Min(p => p.X), model.Vertices.Min(p => p.Z), model.Vertices.Max(p => p.X), model.Vertices.Max(p => p.Z));
            for (int i = (int)MathF.Floor(box.X / cell); i <= (int)MathF.Floor(box.Z / cell); i++)
                for (int j = (int)MathF.Floor(box.Y / cell); j <= (int)MathF.Floor(box.W / cell); j++)
                {
                    if (!grid.TryGetValue((i, j), out var list)) grid[(i, j)] = list = [];
                    list.Add((node, box));
                }
        }
        return (x, z) => grid.TryGetValue(((int)MathF.Floor(x / cell), (int)MathF.Floor(z / cell)), out var list)
            ? list.Where(e => x >= e.Box.X - 0.01f && x <= e.Box.Z + 0.01f && z >= e.Box.Y - 0.01f && z <= e.Box.W + 0.01f).Select(e => e.Node) : [];
    }
}
