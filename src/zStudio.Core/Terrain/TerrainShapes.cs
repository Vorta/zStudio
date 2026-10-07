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
        CheckComplexity([line], radius, 64);
        var area = Clipper.InflatePaths([line], radius, JoinType.Round, EndType.Round, 2.0, Precision, Math.Max(radius / 64, 0.01));
        return Outlines(area);
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
    // The sweep itself has a separate work limit, including disjoint Y intervals.
    private static void CheckComplexity(IEnumerable<PathD> paths, double expansion = 0, int weight = 1)
    {
        List<(double Left, double Right, double Bottom, double Top)> edges = [];
        foreach (var path in paths)
        {
            if ((long)(edges.Count + path.Count) * weight > MaximumPoints) throw ComplexityError();
            for (int i = 0; i < path.Count; i++)
            {
                var a = path[i]; var b = path[(i + 1) % path.Count];
                edges.Add((Math.Min(a.x, b.x) - expansion - 0.001, Math.Max(a.x, b.x) + expansion + 0.001,
                    Math.Min(a.y, b.y) - expansion - 0.001, Math.Max(a.y, b.y) + expansion + 0.001));
            }
        }
        edges.Sort((a, b) => a.Left.CompareTo(b.Left));
        List<(double Left, double Right, double Bottom, double Top)> active = [];
        long candidates = (long)edges.Count * weight * weight;
        long work = 0;
        foreach (var edge in edges)
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                if (++work > 4_000_000) throw ComplexityError();
                var other = active[i];
                if (other.Right < edge.Left)
                {
                    active[i] = active[^1]; active.RemoveAt(active.Count - 1);
                }
                else if (other.Top >= edge.Bottom && edge.Top >= other.Bottom)
                    candidates += (long)weight * weight;
                if (candidates > 1_000_000) throw ComplexityError();
            }
            active.Add(edge);
        }
        if (candidates > 1_000_000) throw ComplexityError();
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
