using System.Numerics;
using Clipper2Lib;

namespace Recoil.Zbd.Core.Terrain;

/// <summary>
/// Region shapes as plan-view areas: a shape covers the union of its polygons, each minus its holes. Painting adds or
/// subtracts areas and keeps shapes as clean, non-overlapping polygons with holes (Clipper2, coordinates kept to 0.001).
/// </summary>
public static class TerrainShapes
{
    private const int Precision = 3;
    /// <summary>Points a shape may hold after an edit; a stroke that would exceed it is refused.</summary>
    public const int MaximumPoints = 200_000;

    /// <summary>Area within the clipping work budget, or a reason it is unavailable. Inspection must not reject a valid recipe because editing has stricter limits.</summary>
    public static TerrainArea InspectArea(IReadOnlyList<TerrainOutline> shape)
    {
        try { return new(SquareUnits(shape), null); }
        catch (InvalidDataException ex) { return new(null, ex.Message); }
    }

    /// <summary>The shape's area as clean polygons: overlaps merged, holes inside their polygons, islands as polygons of their own.</summary>
    public static IReadOnlyList<TerrainOutline> Normalize(IReadOnlyList<TerrainOutline> shape) => Outlines(Area(shape));

    /// <summary>The shape with <paramref name="stroke"/> added (painted) or subtracted (erased).</summary>
    public static IReadOnlyList<TerrainOutline> Paint(IReadOnlyList<TerrainOutline> shape, IReadOnlyList<TerrainOutline> stroke, bool add)
    {
        var subject = Area(shape); var clip = Area(stroke);
        CheckComplexity(subject.Concat(clip));
        var result = add ? Clipper.Union(subject, clip, FillRule.NonZero, Precision) : Clipper.Difference(subject, clip, FillRule.NonZero, Precision);
        return Outlines(result);
    }

    /// <summary>The area a round brush of <paramref name="radius"/> covers along <paramref name="path"/> (a single point is a disc).</summary>
    public static IReadOnlyList<TerrainOutline> Stroke(IReadOnlyList<Vector2> path, float radius)
    {
        if (path.Count == 0 || path.Count > 10_000) throw new InvalidDataException("A stroke has 1–10,000 points.");
        if (!float.IsFinite(radius) || radius < 0.01f || radius > 100_000) throw new InvalidDataException("A brush radius is 0.01–100,000 units.");
        foreach (var p in path) Check(p);
        PathD line = [.. path.Select(p => new PointD(p.X, p.Y))];
        if (line.Count == 1) line.Add(new PointD(line[0].x + 1e-3, line[0].y));
        // At this arc tolerance a full circle uses fewer than 64 edges. Bound offset
        // edge pairs before InflatePaths performs its own internal union.
        if (WithinComplexity([line], radius, 64)) return Outlines(Inflate(line, radius));
        // That whole-path reservation charges every pair of nearby segments, so it admits only a few dozen points of an
        // ordinary drag (the viewport samples every quarter radius). A longer stroke drops the points within 1/64 radius
        // of the line kept around them, which moves the painted edge at most that far, and is offset in pieces of
        // StrokePiecePoints points, each within the reservation. The pieces are united after checking their actual edges,
        // so only a stroke whose own outline is too complex (such as dense crossings) is refused.
        var simple = Simplify(line, radius / 64);
        if (simple.Count == 1) simple.Add(new PointD(simple[0].x + 1e-3, simple[0].y));
        // Pairs of segments within reach of each other bound where the pieces' outlines can cross: a dense scribble is
        // refused here, before anything is offset.
        CheckComplexity([simple], radius);
        PathsD pieces = []; long points = 0;
        for (int start = 0; start + 1 < simple.Count; start += StrokePiecePoints - 1)
        {
            PathD piece = [.. simple.Skip(start).Take(StrokePiecePoints)];
            CheckComplexity([piece], radius, 64);
            foreach (var part in Inflate(piece, radius))
            {
                if ((points += part.Count) > MaximumPoints) throw ComplexityError();
                pieces.Add(part);
            }
        }
        CheckComplexity(pieces);
        return Outlines(Clipper.Union(pieces, [], FillRule.NonZero, Precision));
    }
    /// <summary>Points of one offset piece of a long stroke: even when all its segments are near each other, a piece stays within the whole-path reservation.</summary>
    private const int StrokePiecePoints = 16;
    /// <summary>Segment-distance comparisons a stroke simplification may make; spans beyond them keep all their points.</summary>
    private const long SimplifyWork = 4_000_000;
    private static PathsD Inflate(PathD line, double radius) =>
        Clipper.InflatePaths([line], radius, JoinType.Round, EndType.Round, 2.0, Precision, Math.Max(radius / 64, 0.01));

    /// <summary>
    /// Douglas–Peucker measured to segments rather than lines, so a reversal is never cut short: the points within
    /// <paramref name="tolerance"/> of the segment kept around them are dropped. Every dropped point is within the tolerance
    /// of the kept line and every point of the kept line within it of the path, so a brush along either covers the same area
    /// to within the tolerance of its edge.
    /// </summary>
    private static PathD Simplify(PathD path, double tolerance)
    {
        bool[] keep = new bool[path.Count];
        keep[0] = keep[^1] = true;
        Stack<(int From, int To)> spans = new();
        spans.Push((0, path.Count - 1));
        double limit = tolerance * tolerance; long work = 0;
        while (spans.TryPop(out var span))
        {
            var (from, to) = span;
            if (to - from < 2) continue;
            if ((work += to - from - 1) > SimplifyWork)
            {
                for (int i = from + 1; i < to; i++) keep[i] = true;
                continue;
            }
            int farthest = -1; double distance = limit;
            for (int i = from + 1; i < to; i++)
            {
                double d = SegmentDistanceSquared(path[i], path[from], path[to]);
                if (d > distance) { distance = d; farthest = i; }
            }
            if (farthest < 0) continue;
            keep[farthest] = true;
            spans.Push((farthest, to)); spans.Push((from, farthest));
        }
        PathD result = [];
        for (int i = 0; i < path.Count; i++)
            if (keep[i] && (result.Count == 0 || result[^1].x != path[i].x || result[^1].y != path[i].y)) result.Add(path[i]);
        return result;

        static double SegmentDistanceSquared(PointD p, PointD a, PointD b)
        {
            double dx = b.x - a.x, dy = b.y - a.y, length = dx * dx + dy * dy;
            double t = length == 0 ? 0 : Math.Clamp(((p.x - a.x) * dx + (p.y - a.y) * dy) / length, 0, 1);
            double ex = p.x - (a.x + t * dx), ey = p.y - (a.y + t * dy);
            return ex * ex + ey * ey;
        }
    }

    /// <summary>Plan-view area of a shape.</summary>
    public static double SquareUnits(IReadOnlyList<TerrainOutline> shape) => Math.Abs(Clipper.Area(Area(shape)));
    public static int PointCount(IReadOnlyList<TerrainOutline> shape) => shape.Sum(p => p.Outer.Count + p.Holes.Sum(h => h.Count));

    /// <summary>Everywhere a shape could reach, for erasing from a region that covers its whole surfaces.</summary>
    public static IReadOnlyList<TerrainOutline> Everywhere { get; } =
        [new([new(-TerrainRecipe.MaximumCoordinate, -TerrainRecipe.MaximumCoordinate), new(TerrainRecipe.MaximumCoordinate, -TerrainRecipe.MaximumCoordinate), new(TerrainRecipe.MaximumCoordinate, TerrainRecipe.MaximumCoordinate), new(-TerrainRecipe.MaximumCoordinate, TerrainRecipe.MaximumCoordinate)], [])];

    /// <summary>A shape as Clipper paths covering the same area: each polygon minus its holes, unioned.</summary>
    private static PathsD Area(IReadOnlyList<TerrainOutline> shape)
    {
        if (shape.Sum(p => (long)p.Outer.Count + p.Holes.Sum(h => (long)h.Count)) > MaximumPoints)
            throw ComplexityError();
        PathsD all = [];
        foreach (var polygon in shape)
        {
            foreach (var p in polygon.Outer) Check(p);
            PathsD outer = [Path(polygon.Outer)];
            PathsD holes = [.. polygon.Holes.Select(Path)];
            CheckComplexity(outer.Concat(holes));
            var piece = holes.Count == 0 ? Clipper.Union(outer, [], FillRule.NonZero, Precision) : Clipper.Difference(outer, holes, FillRule.NonZero, Precision);
            if (all.Sum(p => (long)p.Count) + piece.Sum(p => (long)p.Count) > MaximumPoints) throw ComplexityError();
            all.AddRange(piece);
        }
        CheckComplexity(all);
        return Clipper.Union(all, [], FillRule.NonZero, Precision);
    }
    private static PathD Path(IReadOnlyList<Vector2> ring)
    {
        foreach (var p in ring) Check(p);
        return [.. ring.Select(p => new PointD(p.X, p.Y))];
    }

    private static InvalidDataException ComplexityError() => new("The shape exceeds the safe clipping complexity; use simpler outlines or shorter brush strokes.");

    // Bounding-box candidates conservatively bound intersections (and generated vertices).
    private static void CheckComplexity(IEnumerable<PathD> paths, double expansion = 0, int weight = 1)
    {
        if (!WithinComplexity(paths, expansion, weight)) throw ComplexityError();
    }
    private static bool WithinComplexity(IEnumerable<PathD> paths, double expansion, int weight)
    {
        List<(double Left, double Right, double Bottom, double Top)> edges = [];
        foreach (var path in paths)
        {
            if ((long)(edges.Count + path.Count) * weight > MaximumPoints) return false;
            for (int i = 0; i < path.Count; i++)
            {
                var a = path[i]; var b = path[(i + 1) % path.Count];
                edges.Add((Math.Min(a.x, b.x) - expansion - 0.001, Math.Max(a.x, b.x) + expansion + 0.001,
                    Math.Min(a.y, b.y) - expansion - 0.001, Math.Max(a.y, b.y) + expansion + 0.001));
            }
        }
        return Sweep(edges, weight);
    }
    /// <summary>
    /// Whether the edges and their candidate pairs (boxes overlapping along both axes), weighted, fit. A sweep along x holds
    /// the boxes it has passed until their right side, and counts those overlapping each new box along y from two prefix
    /// sums over their bottoms and tops (Fenwick trees over the distinct y values), so it costs about n log n for any shape
    /// rather than comparing each box with every box held (a shape spread along x, such as hundreds of separate strokes,
    /// holds many).
    /// </summary>
    private static bool Sweep(List<(double Left, double Right, double Bottom, double Top)> edges, int weight)
    {
        int n = edges.Count;
        long candidates = (long)n * weight * weight;
        if (candidates > 1_000_000) return false;
        if (n == 0) return true;
        double[] lefts = new double[n], rights = new double[n], ys = new double[2 * n];
        int[] byLeft = new int[n], byRight = new int[n];
        for (int i = 0; i < n; i++) { (lefts[i], rights[i], ys[2 * i], ys[2 * i + 1]) = edges[i]; byLeft[i] = byRight[i] = i; }
        Array.Sort(lefts, byLeft); Array.Sort(rights, byRight); Array.Sort(ys);
        int distinct = 0;
        for (int i = 0; i < ys.Length; i++) if (distinct == 0 || ys[i] != ys[distinct - 1]) ys[distinct++] = ys[i];
        // Each box's bottom and top as positions among the distinct values, from 1.
        int[] bottom = new int[n], top = new int[n];
        for (int i = 0; i < n; i++) { bottom[i] = Array.BinarySearch(ys, 0, distinct, edges[i].Bottom) + 1; top[i] = Array.BinarySearch(ys, 0, distinct, edges[i].Top) + 1; }
        int[] bottoms = new int[distinct + 1], tops = new int[distinct + 1];
        int released = 0;
        foreach (int i in byLeft)
        {
            // A box whose right side lies before this left side came earlier and is released.
            for (; released < n && rights[released] < edges[i].Left; released++) { Add(bottoms, bottom[byRight[released]], -1); Add(tops, top[byRight[released]], -1); }
            // Held boxes whose bottom is at most this top, less those whose top is below this bottom.
            candidates += (Sum(bottoms, top[i]) - Sum(tops, bottom[i] - 1)) * weight * weight;
            if (candidates > 1_000_000) return false;
            Add(bottoms, bottom[i], 1); Add(tops, top[i], 1);
        }
        return true;

        static void Add(int[] tree, int index, int value) { for (; index < tree.Length; index += index & -index) tree[index] += value; }
        static long Sum(int[] tree, int index) { long sum = 0; for (; index > 0; index -= index & -index) sum += tree[index]; return sum; }
    }
    private static void Check(Vector2 p)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || Math.Abs(p.X) > TerrainRecipe.MaximumCoordinate || Math.Abs(p.Y) > TerrainRecipe.MaximumCoordinate)
            throw new InvalidDataException($"Shape points are finite and within ±{TerrainRecipe.MaximumCoordinate:N0}.");
    }

    /// <summary>Clean paths as polygons with holes, in a stable order (by first point), each ring starting at its smallest point.</summary>
    private static IReadOnlyList<TerrainOutline> Outlines(PathsD paths)
    {
        CheckComplexity(paths);
        ClipperD clipper = new(Precision);
        clipper.AddSubject(paths);
        PolyTreeD tree = new();
        clipper.Execute(ClipType.Union, FillRule.NonZero, tree);
        List<TerrainOutline> result = [];
        Stack<PolyPathD> pending = new();
        pending.Push(tree);
        while (pending.TryPop(out var next)) Visit(next);
        if (PointCount(result) > MaximumPoints) throw new InvalidDataException($"The shape would have more than {MaximumPoints:N0} points; paint with a larger brush or fewer strokes.");
        return [.. result.OrderBy(o => o.Outer[0].X).ThenBy(o => o.Outer[0].Y)];

        void Visit(PolyPathD node)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var outer = node[i];
                List<IReadOnlyList<Vector2>> holes = [];
                for (int j = 0; j < outer.Count; j++)
                {
                    var hole = outer[j];
                    holes.Add(Ring(hole.Polygon!));
                    pending.Push(hole);
                }
                result.Add(new(Ring(outer.Polygon!), [.. holes.OrderBy(h => h[0].X).ThenBy(h => h[0].Y)]));
            }
        }
        static IReadOnlyList<Vector2> Ring(PathD path)
        {
            var points = path.Select(p => new Vector2((float)p.x, (float)p.y)).ToArray();
            int start = 0;
            for (int i = 1; i < points.Length; i++) if (points[i].X < points[start].X || points[i].X == points[start].X && points[i].Y < points[start].Y) start = i;
            return [.. points.Skip(start), .. points.Take(start)];
        }
    }
}

/// <summary>A bounded area calculation; a missing value carries an explicit explanation.</summary>
public sealed record TerrainArea(double? SquareUnits, string? Unavailable);
