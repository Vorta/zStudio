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

    /// <summary>The shape's area as clean polygons: overlaps merged, holes inside their polygons, islands as polygons of their own.</summary>
    public static IReadOnlyList<TerrainOutline> Normalize(IReadOnlyList<TerrainOutline> shape) => Outlines(Area(shape));

    /// <summary>The shape with <paramref name="stroke"/> added (painted) or subtracted (erased).</summary>
    public static IReadOnlyList<TerrainOutline> Paint(IReadOnlyList<TerrainOutline> shape, IReadOnlyList<TerrainOutline> stroke, bool add)
    {
        var result = add ? Clipper.Union(Area(shape), Area(stroke), FillRule.NonZero, Precision) : Clipper.Difference(Area(shape), Area(stroke), FillRule.NonZero, Precision);
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
        PathsD all = [];
        foreach (var polygon in shape)
        {
            foreach (var p in polygon.Outer) Check(p);
            PathsD outer = [Path(polygon.Outer)];
            var piece = polygon.Holes.Count == 0 ? Clipper.Union(outer, FillRule.NonZero) : Clipper.Difference(outer, [.. polygon.Holes.Select(Path)], FillRule.NonZero, Precision);
            all.AddRange(piece);
        }
        return Clipper.Union(all, FillRule.NonZero);
    }
    private static PathD Path(IReadOnlyList<Vector2> ring) => [.. ring.Select(p => new PointD(p.X, p.Y))];
    private static void Check(Vector2 p)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || Math.Abs(p.X) > TerrainRecipe.MaximumCoordinate || Math.Abs(p.Y) > TerrainRecipe.MaximumCoordinate)
            throw new InvalidDataException($"Shape points are finite and within ±{TerrainRecipe.MaximumCoordinate:N0}.");
    }

    /// <summary>Clean paths as polygons with holes, in a stable order (by first point), each ring starting at its smallest point.</summary>
    private static IReadOnlyList<TerrainOutline> Outlines(PathsD paths)
    {
        ClipperD clipper = new(Precision);
        clipper.AddSubject(paths);
        PolyTreeD tree = new();
        clipper.Execute(ClipType.Union, FillRule.NonZero, tree);
        List<TerrainOutline> result = [];
        Visit(tree);
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
                    Visit(hole);
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
