using System.Numerics;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Terrain;

/// <summary>A corner of terrain geometry in world space.</summary>
public readonly record struct TerrainCorner(Vector3 Position, Vector3 Normal, Vector2 Uv)
{
    // Generated normals stay in the original triangle's linear field until output. Normalizing an intermediate
    // cut would change the direction produced by later cuts; authored corners retain their stored values.
    internal bool Interpolated { get; init; }
}
/// <summary>
/// A polygon of a terrain surface (an engine polygon as the model stores it, or a triangle), with the index of its
/// material in the compile's material list. Polygons the splitter does not cut are kept as they are.
/// </summary>
public readonly record struct TerrainFace(int Material, IReadOnlyList<TerrainCorner> Corners)
{
    public TerrainFace(int material, TerrainCorner a, TerrainCorner b, TerrainCorner c) : this(material, [a, b, c]) { }
}
/// <summary>A surface to compile: the recipe's entry and its polygons in world space.</summary>
public sealed record TerrainSurfaceGeometry(TerrainSurface Surface, IReadOnlyList<TerrainFace> Faces);
/// <summary>What the compiler needs of a material: the polygon zone word it carries when no region sets zones.</summary>
public readonly record struct TerrainMaterialInfo(uint ZoneWord);

/// <summary>
/// The world's area grid in plan view (WorldOrigin, WorldExtents, WorldPartition): cell (column, row) spans x from
/// OriginX + column × CellX and z from OriginZ + row × CellZ (CellZ is negative, so rows run toward −z).
/// </summary>
public sealed record TerrainGrid(float OriginX, float OriginZ, float SizeX, float SizeZ, float CellX, float CellZ, int Columns, int Rows)
{
    /// <summary>The cell holding a point, or (−1, −1) outside the grid.</summary>
    public (int Column, int Row) Cell(float x, float z)
    {
        double col = Math.Floor((x - (double)OriginX) / CellX), row = Math.Floor((z - (double)OriginZ) / CellZ);
        return col < 0 || col >= Columns || row < 0 || row >= Rows ? (-1, -1) : ((int)col, (int)row);
    }
}

/// <summary>A polygon of a compiled piece: its corners, material and polygon attributes (null: the material's).</summary>
public sealed record TerrainPolygonOutput(int Material, TerrainCorner[] Corners, uint ZoneWord, uint? Soil, int? Priority);
/// <summary>A compiled piece: one node of the world, inside one cell (or outside the grid at −1), with its node attributes.</summary>
public sealed record TerrainPiece(string Name, int Surface, int Column, int Row, uint CarriedFlags, byte Zone, IReadOnlyList<TerrainPolygonOutput> Polygons);
/// <summary>A compile's pieces and findings.</summary>
public sealed record TerrainCompilation(IReadOnlyList<TerrainPiece> Pieces, IReadOnlyList<string> Warnings);

/// <summary>
/// The terrain splitter (rules version <see cref="TerrainRecipe.CurrentCompiler"/>). Surfaces are cut at the grid's cell
/// lines and along region outlines in plan view; each part takes the attributes of the layers covering it (the recipe's
/// defaults, its surface's defaults, then the regions in order). Parts are grouped into pieces by surface, cell and node
/// attributes, and a piece that would pass the engine's model limits is divided. Every cut point on an edge is inserted
/// into each polygon sharing that edge, so neighbouring pieces meet without cracks or T-junctions. The same inputs
/// always give the same pieces.
/// </summary>
public static class TerrainCompiler
{
    /// <summary>Vertices and normals per piece, kept below the engine's 921 with room for the model builder's merging.</summary>
    public const int VertexBudget = 860;
    /// <summary>
    /// Vertices and normals per crater-capable piece (CanModify, not ClipTo): each crater rebuilds a touched piece with up to
    /// 26 more vertices, and past the 922nd the engine drops that polygon and every later one, so 16 craters (a cell's
    /// limit) must fit (engine-evidence, "Craters and quicksand").
    /// </summary>
    public const int CraterVertexBudget = 450;
    /// <summary>Corners per polygon of a crater-capable piece: a crater inside one polygon is cut on a 32-entry edge stack.</summary>
    public const int CraterCorners = 30;
    /// <summary>Distance below which a point counts as on a cut line (the model builder merges vertices within 0.001).</summary>
    private const double OnLine = 1e-3;
    public const int MaximumFragments = 4_000_000;
    // Counts all created corners, including discarded intermediate cuts and junction repairs. A final-output
    // limit alone leaves the splitter free to retain far more geometry than the world reader can accept.
    internal const int MaximumCreatedCorners = 4_000_000;
    internal const long MaximumWork = 100_000_000;
    /// <summary>Polygons painting may add before the build reports it: more than any shipped world holds in all (at most 15,119).</summary>
    internal const int PaintedPolygonNotice = 16_000;

    public static TerrainCompilation Compile(string label, TerrainRecipe recipe, IReadOnlyList<TerrainSurfaceGeometry> surfaces, IReadOnlyList<TerrainMaterialInfo> materials, TerrainGrid grid, CancellationToken token = default)
        => Compile(label, recipe, surfaces, materials, grid, token, MaximumCreatedCorners);

    internal static TerrainCompilation Compile(string label, TerrainRecipe recipe, IReadOnlyList<TerrainSurfaceGeometry> surfaces, IReadOnlyList<TerrainMaterialInfo> materials, TerrainGrid grid, CancellationToken token, int maximumCreatedCorners, long maximumWork = MaximumWork)
    {
        if (grid.CellX <= 0 || grid.CellZ >= 0 || grid.Columns < 1 || grid.Rows < 1) throw new InvalidDataException("Terrain needs the world's grid: WorldOrigin, WorldExtents and WorldPartition must run before the mission database is loaded.");
        Compiler compiler = new(token, maximumCreatedCorners, maximumWork);
        List<string> warnings = [];
        // Layers 1 and 2: the recipe's defaults, then each surface's.
        for (int s = 0; s < surfaces.Count; s++)
        {
            var state = State.Initial.Apply(recipe.Defaults).Apply(surfaces[s].Surface.Defaults);
            foreach (var face in surfaces[s].Faces)
            {
                if (face.Material < 0 || face.Material >= materials.Count) throw new InvalidDataException($"A polygon of surface {surfaces[s].Surface.Id} has no material.");
                compiler.AddFace(s, face, state);
            }
        }
        // Cell lines first, so region cuts work on small parts and every part lies in one cell.
        compiler.CutAtGrid(grid);
        // Layer 3: the regions, in order.
        var surfaceIndices = compiler.IndexSurfaces(surfaces);
        int unpainted = compiler.Count;
        foreach (var region in recipe.Regions)
        {
            HashSet<int> on = compiler.RegionSurfaces(region, surfaceIndices, surfaces.Count);
            compiler.ApplyRegion(region, on);
        }
        // Every polygon is drawn on its own, so frame time follows their number (engine-evidence, "Frame time"); detailed
        // painting can multiply a terrain's. The world still loads, so this is reported rather than refused.
        if (compiler.Count - unpainted > PaintedPolygonNotice)
            warnings.Add($"Its painted regions cut its surfaces' {unpainted:N0} polygons into {compiler.Count:N0}. Every shipped world holds under {PaintedPolygonNotice:N0} polygons in all, and the engine draws each polygon on its own, so frame rate falls as they grow; paint fewer, larger strokes, or erase detail that is not needed.");
        compiler.RepairJunctions();
        var pieces = compiler.Pieces(label, recipe, surfaces, materials, grid, warnings);
        return new(pieces, warnings);
    }

    /// <summary>The attributes a part has after the layers that cover it.</summary>
    private readonly record struct State(uint Carried, int NodeZone, TerrainZones? Zones, uint? Soil, int? Priority)
    {
        public static State Initial => new(WorldGltf.DefaultCarried, TerrainAttributes.AutoZone, null, null, null);
        public State Apply(TerrainAttributes a)
        {
            uint carried = a.Flags ?? Carried;
            if (a.Collision is { } collision) carried = collision ? carried | 0x10 : carried & ~0x10u;
            if (a.Standable is { } standable) carried = standable ? carried | 0x08 : carried & ~0x08u;
            if (a.NodeGate is { } gate) carried = gate ? carried | 0x01000000 : carried & ~0x01000000u;
            if (a.Craters is { } craters) carried = carried & ~0x30000u | craters switch { TerrainCraters.Allowed => 0x10000u, TerrainCraters.Blocked => 0x20000u, _ => 0u };
            return new(carried, a.NodeZone ?? NodeZone, a.Zones ?? Zones, a.Soil ?? Soil, a.Priority ?? Priority);
        }
    }

    /// <summary>A segment fragments' edges lie on: a source triangle edge or a cut's chord, with its points by parameter.</summary>
    private sealed class Segment(Vector3 a, Vector3 b)
    {
        public Vector3 A { get; } = a; public Vector3 B { get; } = b;
        /// <summary>Points on the segment by parameter, as first computed, so every polygon sharing the segment uses the same position.</summary>
        public SortedDictionary<double, Vector3> Points { get; } = new() { [0] = a, [1] = b };
        private double[]? keys; private Vector3[]? values;
        /// <summary>The points as arrays by parameter, for junction repair once cutting has put every point.</summary>
        public (double[] Keys, Vector3[] Values) Sorted()
        {
            if (keys == null || keys.Length != Points.Count) { keys = [.. Points.Keys]; values = [.. Points.Values]; }
            return (keys, values!);
        }
        public Vector3 At(double t, Line? line)
        {
            if (Points.TryGetValue(t, out var known)) return known;
            Vector3 p = new((float)(A.X + (B.X - (double)A.X) * t), (float)(A.Y + (B.Y - (double)A.Y) * t), (float)(A.Z + (B.Z - (double)A.Z) * t));
            // An axis-aligned cut puts its points exactly on the line, so parts stay inside their cells.
            if (line is { Axis: 'x' } x) p.X = (float)x.Offset; else if (line is { Axis: 'z' } z) p.Z = (float)z.Offset;
            Points[t] = p;
            return p;
        }
    }
    /// <summary>A line in plan view: points p with Normal · (p.x, p.z) = Offset; Axis marks x = c or z = c.</summary>
    private readonly record struct Line(double NX, double NZ, double Offset, char Axis = '\0')
    {
        public double Distance(Vector3 p) => NX * p.X + NZ * p.Z - Offset;
        public double Distance(Vector2 p) => NX * p.X + NZ * p.Y - Offset;
        public static Line X(double x) => new(1, 0, x, 'x');
        public static Line Z(double z) => new(0, 1, z, 'z');
        public static Line Through(Vector2 a, Vector2 b)
        {
            double nx = -(b.Y - (double)a.Y), nz = b.X - (double)a.X, length = Math.Sqrt(nx * nx + nz * nz);
            nx /= length; nz /= length;
            // One orientation per line, so the same edge listed either way cuts the same way.
            if (nx < 0 || nx == 0 && nz < 0) { nx = -nx; nz = -nz; }
            return new(nx, nz, nx * a.X + nz * a.Y);
        }
    }
    /// <summary>An edge of a part: the segment it lies on and the segment parameters at its start and end corners.</summary>
    private readonly record struct EdgeRef(Segment Segment, double T0, double T1);
    private sealed class Part
    {
        public required int Surface, Material;
        public required List<TerrainCorner> Corners;
        public required List<EdgeRef> Edges;
        public required State State;
        /// <summary>Whether a line cuts the polygon into two; a polygon that is not convex is cut as its fan.</summary>
        public bool Convex = true;
        /// <summary>The corners still describe an authored fan, whose first corner and interpolation must remain unchanged.</summary>
        public bool AuthoredFan;
        /// <summary>An authored fan whose corners the build changed (junction repair added some, or the crater limits split it).</summary>
        public bool Changed;
        public (Vector3 Min, Vector3 Max) Bounds()
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var c in Corners) { min = Vector3.Min(min, c.Position); max = Vector3.Max(max, c.Position); }
            return (min, max);
        }
        public Vector3 Centroid()
        {
            // Area-weighted centre of the convex polygon, which lies inside it.
            Vector3 origin = Corners[0].Position, sum = Vector3.Zero; float total = 0;
            for (int i = 1; i + 1 < Corners.Count; i++)
            {
                float area = Vector3.Cross(Corners[i].Position - origin, Corners[i + 1].Position - origin).Length();
                sum += (origin + Corners[i].Position + Corners[i + 1].Position) / 3 * area; total += area;
            }
            var centre = total > 0 ? sum / total : Corners.Aggregate(Vector3.Zero, (s, c) => s + c.Position) / Corners.Count;
            WorldNumbers.Vector(centre);
            return centre;
        }
        /// <summary>
        /// A point inside the polygon in plan view, at its height there: the middle of the widest span that a line along x,
        /// halfway across the widest gap between its corners' z (so the line meets no corner), cuts from it. A polygon that is
        /// not convex can have its centre outside it (in a notch); this point lies between two of its edges.
        /// </summary>
        public Vector3 Interior()
        {
            int n = Corners.Count;
            double[] zs = new double[n];
            for (int i = 0; i < n; i++) zs[i] = Corners[i].Position.Z;
            Array.Sort(zs);
            double z = 0, gap = 0;
            for (int i = 0; i + 1 < n; i++)
                if (zs[i + 1] - zs[i] > gap) { gap = zs[i + 1] - zs[i]; z = (zs[i] + zs[i + 1]) / 2; }
            if (!(gap > 0)) return Centroid();
            List<(double X, double Y)> hits = [];
            for (int i = 0; i < n; i++)
            {
                var p = Corners[i].Position; var q = Corners[(i + 1) % n].Position;
                if (p.Z > z == q.Z > z) continue;
                double f = (z - p.Z) / ((double)q.Z - p.Z);
                hits.Add((p.X + (q.X - (double)p.X) * f, p.Y + (q.Y - (double)p.Y) * f));
            }
            hits.Sort((a, b) => a.X.CompareTo(b.X));
            // Between the first and second crossing, the third and fourth and so on, the line is inside.
            double x = 0, y = 0, width = 0;
            for (int i = 0; i + 1 < hits.Count; i += 2)
                if (hits[i + 1].X - hits[i].X > width) { width = hits[i + 1].X - hits[i].X; x = (hits[i].X + hits[i + 1].X) / 2; y = (hits[i].Y + hits[i + 1].Y) / 2; }
            if (!(width > 0)) return Centroid();
            Vector3 point = new((float)x, (float)y, (float)z);
            WorldNumbers.Vector(point);
            return point;
        }
    }

    /// <summary>
    /// A region outline's edges in a uniform grid over the plan-view area it is asked about: its bounds, within the reach
    /// of the parts it cuts when that is given. An edge is listed in every bucket its box overlaps (clamped to the grid),
    /// so a part finds the edges near it, and a point's ray towards +x the edges along its row. Edges wholly beyond the
    /// grid's +x side can only meet such rays, and are listed by row in one more column; edges wholly before it, above or
    /// below it meet neither. Only an outline far larger than the parts (everywhere but a few erased strokes) leaves any
    /// out: indexing all of it would spread the parts' whole area over a bucket or two holding every stroke's edges. Each
    /// bucket's centre keeps the rings a ray from it crosses an odd number of times, so a point's side is found from its
    /// own bucket's edges rather than by a ray along the whole row.
    /// </summary>
    internal sealed class Outline
    {
        /// <summary>Bucket entries at most; an outline of long edges crossing many buckets uses one bucket instead.</summary>
        private const long MaximumEntries = 16_000_000;
        private readonly TerrainShape shape;
        private readonly (Vector2 A, Vector2 B, Vector2 Min, Vector2 Max, int Ring)[] edges;
        /// <summary>Each ring's polygon (as its index), negative for the outer ring of polygon −1 − value.</summary>
        private readonly int[] ringPolygon;
        private readonly int[][] buckets;
        private readonly int columns, rows;
        /// <summary>The outline's bounds; the indexed grid; the area queries may come from (all of it when not given).</summary>
        private readonly Vector2 min, max, low, size;
        private readonly (Vector2 Min, Vector2 Max)? reach;
        private readonly bool indexed;
        private readonly int[] seen, ringSeen, oddHoles;
        private readonly bool[] parity;
        private readonly List<int> crossed = [];
        private int pass;
        /// <summary>
        /// Each bucket's centre and the rings a ray from it towards +x crosses an odd number of times; null when the centres
        /// cannot serve (one rounds into another bucket, or the rings to keep pass <see cref="MaximumOddRings"/>).
        /// </summary>
        private readonly float[] centreX = [], centreY = [];
        private readonly int[]? oddStart, oddCount, oddRings;
        private const int MaximumOddRings = 4_000_000;
        private readonly List<int> local = [];

        private readonly Action<long>? charge;
        /// <param name="reach">
        /// The plan-view box every <see cref="Near"/> box and <see cref="Inside"/> point lies in (the parts' bounds); a
        /// query outside it is answered from every edge.
        /// </param>
        public Outline(TerrainShape shape, Action<long>? charge = null, (Vector2 Min, Vector2 Max)? reach = null)
        {
            this.charge = charge;
            this.shape = shape;
            this.reach = reach;
            List<(Vector2 A, Vector2 B, Vector2 Min, Vector2 Max, int Ring)> list = [];
            List<int> rings = [];
            for (int p = 0; p < shape.Polygons.Count; p++)
            {
                var polygon = shape.Polygons[p];
                bool outer = true;
                foreach (var points in polygon.Holes.Prepend(polygon.Outer))
                {
                    charge?.Invoke(points.Count);
                    for (int i = 0; i < points.Count; i++)
                    {
                        var a = points[i]; var b = points[(i + 1) % points.Count];
                        if (a != b) list.Add((a, b, Vector2.Min(a, b), Vector2.Max(a, b), rings.Count));
                    }
                    rings.Add(outer ? -1 - p : p);
                    outer = false;
                }
            }
            charge?.Invoke(list.Count);
            edges = [.. list]; ringPolygon = [.. rings]; parity = new bool[rings.Count]; ringSeen = new int[rings.Count]; oddHoles = new int[shape.Polygons.Count]; seen = new int[edges.Length];
            min = edges.Length == 0 ? default : edges.Aggregate(new Vector2(float.MaxValue), (m, e) => Vector2.Min(m, e.Min));
            max = edges.Length == 0 ? default : edges.Aggregate(new Vector2(float.MinValue), (m, e) => Vector2.Max(m, e.Max));
            // The grid covers the outline's bounds where queries can come from.
            low = reach is { } within ? Vector2.Max(min, within.Min) : min;
            Vector2 high = reach != null ? Vector2.Min(max, reach.Value.Max) : max;
            indexed = edges.Length > 0 && low.X <= high.X && low.Y <= high.Y;
            List<int> framed = [], beyond = [];
            if (indexed)
                for (int i = 0; i < edges.Length; i++)
                {
                    var e = edges[i];
                    if (e.Max.Y < low.Y || e.Min.Y > high.Y || e.Max.X < low.X) continue;
                    (e.Min.X > high.X ? beyond : framed).Add(i);
                }
            int side = Math.Clamp((int)Math.Sqrt(framed.Count / 2.0), 1, 1024);
            (columns, rows, size) = Layout(side, high);
            long entries = Entries();
            if (entries > MaximumEntries) { (columns, rows, size) = Layout(1, high); entries = Entries(); }
            charge?.Invoke(edges.Length + entries);
            int stride = columns + 1;
            var lists = new List<int>[stride * rows];
            foreach (int i in framed)
                for (int r = Row(edges[i].Min.Y); r <= Row(edges[i].Max.Y); r++)
                    for (int c = Column(edges[i].Min.X); c <= Column(edges[i].Max.X); c++)
                        (lists[r * stride + c] ??= []).Add(i);
            foreach (int i in beyond)
                for (int r = Row(edges[i].Min.Y); r <= Row(edges[i].Max.Y); r++)
                    (lists[r * stride + columns] ??= []).Add(i);
            buckets = [.. lists.Select(l => l?.ToArray() ?? [])];
            if (indexed) (centreX, centreY, oddStart, oddCount, oddRings) = References();

            long Entries() => framed.Sum(i => (long)(Column(edges[i].Max.X) - Column(edges[i].Min.X) + 1) * (Row(edges[i].Max.Y) - Row(edges[i].Min.Y) + 1))
                + beyond.Sum(i => (long)(Row(edges[i].Max.Y) - Row(edges[i].Min.Y) + 1));
        }
        /// <summary>
        /// The rings odd at each bucket's centre, by the same ray towards +x <see cref="Inside"/> casts: along each row's centre
        /// line, from its last centre back to its first, toggling each ring whose edge the line crosses between them.
        /// </summary>
        private (float[], float[], int[]?, int[]?, int[]?) References()
        {
            float[] xs = new float[columns], ys = new float[rows];
            for (int c = 0; c < columns; c++) xs[c] = low.X + (c + 0.5f) * size.X;
            for (int r = 0; r < rows; r++) ys[r] = low.Y + (r + 0.5f) * size.Y;
            if (Enumerable.Range(0, columns).Any(c => Column(xs[c]) != c) || Enumerable.Range(0, rows).Any(r => Row(ys[r]) != r)) return (xs, ys, null, null, null);
            int[] start = new int[rows * columns], count = new int[rows * columns], position = new int[ringPolygon.Length];
            bool[] odd = new bool[ringPolygon.Length];
            List<int> rings = [], current = [];
            List<(double X, int Ring)> hits = [];
            for (int r = 0; r < rows; r++)
            {
                pass++;
                hits.Clear();
                double y = ys[r];
                for (int c = 0; c <= columns; c++)
                {
                    charge?.Invoke(1);
                    foreach (int i in buckets[r * (columns + 1) + c])
                    {
                        charge?.Invoke(1);
                        if (seen[i] == pass) continue;
                        seen[i] = pass;
                        var (a, b, _, _, ring) = edges[i];
                        if (a.Y > y != b.Y > y) hits.Add(((b.X - a.X) * (y - (double)a.Y) / (b.Y - (double)a.Y) + a.X, ring));
                    }
                }
                charge?.Invoke(hits.Count * (1L + BitOperations.Log2((uint)hits.Count + 1)));
                hits.Sort((p, q) => q.X.CompareTo(p.X));
                int h = 0;
                for (int c = columns - 1; c >= 0; c--)
                {
                    for (; h < hits.Count && xs[c] < hits[h].X; h++) Toggle(hits[h].Ring);
                    charge?.Invoke(1 + current.Count);
                    if (rings.Count + (long)current.Count > MaximumOddRings) return (xs, ys, null, null, null);
                    start[r * columns + c] = rings.Count; count[r * columns + c] = current.Count;
                    rings.AddRange(current);
                }
                foreach (int ring in current) odd[ring] = false;
                current.Clear();
            }
            return (xs, ys, start, count, [.. rings]);

            void Toggle(int ring)
            {
                if (odd[ring] = !odd[ring]) { position[ring] = current.Count; current.Add(ring); return; }
                int at = position[ring], last = current[^1];
                current[at] = last; position[last] = at;
                current.RemoveAt(current.Count - 1);
            }
        }
        private (int, int, Vector2) Layout(int side, Vector2 high) => (side, side, new(Math.Max((high.X - low.X) / side, 1e-3f), Math.Max((high.Y - low.Y) / side, 1e-3f)));
        private int Column(float x) => Cell((x - low.X) / size.X, columns);
        private int Row(float z) => Cell((z - low.Y) / size.Y, rows);
        // Clamped before converting: coordinates far outside the grid divide to values beyond int's range.
        private static int Cell(float at, int count) => at <= 0 ? 0 : at >= count - 1 ? count - 1 : (int)at;
        /// <summary>Whether the grid answers for a plan-view box: it lies within the reach (anywhere without one).</summary>
        private bool Indexed(float x0, float z0, float x1, float z1) => indexed && (reach is not { } r || x0 >= r.Min.X && z0 >= r.Min.Y && x1 <= r.Max.X && z1 <= r.Max.Y);

        /// <summary>Whether a part's plan-view box meets the outline's.</summary>
        public bool Overlaps(Vector3 lo, Vector3 hi) => edges.Length > 0 && !(hi.X < min.X || lo.X > max.X || hi.Z < min.Y || lo.Z > max.Y);

        /// <summary>The edges whose boxes meet a plan-view box, in outline order.</summary>
        public IEnumerable<(Vector2 A, Vector2 B)> Near(Vector3 lo, Vector3 hi)
        {
            pass++;
            List<int> found = [];
            if (!Indexed(lo.X, lo.Z, hi.X, hi.Z))
            {
                charge?.Invoke(edges.Length);
                for (int i = 0; i < edges.Length; i++) Consider(i);
            }
            else
                for (int r = Row(lo.Z); r <= Row(hi.Z); r++)
                    for (int c = Column(lo.X); c <= Column(hi.X); c++)
                    {
                        charge?.Invoke(1);
                        foreach (int i in buckets[r * (columns + 1) + c])
                        {
                            charge?.Invoke(1);
                            Consider(i);
                        }
                    }
            found.Sort();
            return found.Select(i => (edges[i].A, edges[i].B));

            void Consider(int i)
            {
                if (seen[i] != pass && edges[i].Max.X >= lo.X && edges[i].Min.X <= hi.X && edges[i].Max.Y >= lo.Z && edges[i].Min.Y <= hi.Z) { seen[i] = pass; found.Add(i); }
            }
        }

        /// <summary>Whether a point lies in the shape (inside an outer ring and none of its holes, within the height range).</summary>
        public bool Inside(Vector3 point)
        {
            if (shape.MinY is { } minY && point.Y < minY || shape.MaxY is { } maxY && point.Y > maxY) return false;
            Vector2 p = new(point.X, point.Z);
            if (edges.Length == 0 || p.X < min.X || p.X > max.X || p.Y < min.Y || p.Y > max.Y) return false;
            pass++;
            // A ray towards +x crosses each ring an odd number of times when the point is inside it.
            if (!Indexed(p.X, p.Y, p.X, p.Y))
            {
                charge?.Invoke(edges.Length);
                for (int i = 0; i < edges.Length; i++) Cross(i);
            }
            else if (!FromCentre(p))
            {
                int row = Row(p.Y);
                for (int c = Column(p.X); c <= columns; c++)
                {
                    charge?.Invoke(1);
                    foreach (int i in buckets[row * (columns + 1) + c])
                    {
                        charge?.Invoke(1);
                        if (seen[i] == pass) continue;
                        seen[i] = pass;
                        Cross(i);
                    }
                }
            }
            // Inside a polygon's outer ring and none of its holes. Only the rings the ray crossed can be odd, so the
            // answer and the reset cost what the ray found, not the shape's number of rings (strokes erased as holes).
            charge?.Invoke(2L * crossed.Count);
            bool inside = false;
            foreach (int ring in crossed)
                if (ringPolygon[ring] < 0 && parity[ring] && oddHoles[-1 - ringPolygon[ring]] == 0) inside = true;
            foreach (int ring in crossed)
            {
                if (parity[ring] && ringPolygon[ring] >= 0) oddHoles[ringPolygon[ring]]--;
                parity[ring] = false;
            }
            crossed.Clear();
            return inside;

            void Cross(int i)
            {
                var (a, b, _, _, ring) = edges[i];
                if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - (double)a.Y) / (b.Y - (double)a.Y) + a.X) Toggle(ring);
            }
        }
        private void Toggle(int ring)
        {
            if (ringSeen[ring] != pass) { ringSeen[ring] = pass; crossed.Add(ring); }
            parity[ring] = !parity[ring];
            if (ringPolygon[ring] >= 0) oddHoles[ringPolygon[ring]] += parity[ring] ? 1 : -1;
        }
        /// <summary>
        /// A point's parities from its bucket's centre: the rings odd there, toggled by the bucket's edges that the segment
        /// from the centre to the point crosses (a vertex on that segment counts as lying to its right). Every edge the
        /// segment can meet lies in the bucket, so this costs the bucket's edges rather than a ray along the whole row.
        /// False, having toggled nothing, when the centre or the point lies on one of those edges (or nearer to one than
        /// <see cref="Touching"/>, where rounding could tell the two tests apart): there only the ray's own rule decides.
        /// </summary>
        private bool FromCentre(Vector2 p)
        {
            if (oddStart is not { } starts || oddCount is not { } counts || oddRings is not { } odd) return false;
            int row = Row(p.Y), column = Column(p.X), bucket = row * columns + column;
            Vector2 q = new(centreX[column], centreY[row]);
            var list = buckets[row * (columns + 1) + column];
            charge?.Invoke(1 + list.Length + counts[bucket]);
            local.Clear();
            foreach (int i in list)
            {
                var (a, b, lo, hi, ring) = edges[i];
                double length = Math.Sqrt((b.X - (double)a.X) * (b.X - (double)a.X) + (b.Y - (double)a.Y) * (b.Y - (double)a.Y));
                double fromCentre = Orient(a, b, q), fromPoint = Orient(a, b, p);
                if (On(fromCentre, q) || On(fromPoint, p)) return false;
                if (fromCentre == 0 || fromPoint == 0 || fromCentre > 0 == fromPoint > 0) continue;
                if (Orient(q, p, a) > 0 != Orient(q, p, b) > 0) local.Add(ring);

                bool On(double orient, Vector2 c) => Math.Abs(orient) <= Touching * length
                    && c.X >= lo.X - Touching && c.X <= hi.X + Touching && c.Y >= lo.Y - Touching && c.Y <= hi.Y + Touching;
            }
            for (int k = 0; k < counts[bucket]; k++) Toggle(odd[starts[bucket] + k]);
            foreach (int ring in local) Toggle(ring);
            return true;

            static double Orient(Vector2 a, Vector2 b, Vector2 c) => (b.X - (double)a.X) * (c.Y - (double)a.Y) - (b.Y - (double)a.Y) * (c.X - (double)a.X);
        }
        /// <summary>A distance from an edge within which the ray decides (far above the rounding of either test at ±1,000,000).</summary>
        private const double Touching = 1e-6;
    }

    private sealed class Compiler(CancellationToken token, int maximumCreatedCorners, long maximumWork)
    {
        private readonly List<Part> parts = [];
        public int Count => parts.Count;
        private readonly Dictionary<(Vector3, Vector3), Segment> sourceEdges = [];
        /// <summary>Each surface's plan-view box (see <see cref="Reach"/>).</summary>
        private readonly Dictionary<int, (Vector2 Min, Vector2 Max)> reaches = [];
        private long work;
        private int createdCorners;

        private void ChargeCorners(int count)
        {
            token.ThrowIfCancellationRequested();
            if (count < 0 || count > maximumCreatedCorners - createdCorners)
                throw new InvalidDataException($"Terrain cutting exceeds its {maximumCreatedCorners:N0}-corner intermediate geometry limit; reduce the surfaces or region detail.");
            createdCorners += count;
        }

        private void Tick(long count = 1)
        {
            token.ThrowIfCancellationRequested();
            if (count < 0 || count > maximumWork - work)
                throw new InvalidDataException($"Terrain cutting exceeds its {maximumWork:N0}-step work limit; reduce the surfaces or regions.");
            work += count;
        }
        private static bool Less(Vector3 a, Vector3 b) => a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;
        private EdgeRef SourceEdge(Vector3 p, Vector3 q)
        {
            bool forward = Less(p, q);
            var key = forward ? (p, q) : (q, p);
            if (!sourceEdges.TryGetValue(key, out var segment)) sourceEdges[key] = segment = new(key.Item1, key.Item2);
            return forward ? new(segment, 0, 1) : new(segment, 1, 0);
        }

        public void AddFace(int surface, TerrainFace face, State state)
        {
            Tick();
            var corners = face.Corners;
            Tick(corners.Count);
            if (corners.Any(c => !Finite(c.Position) || !Finite(c.Normal) || !float.IsFinite(c.Uv.X) || !float.IsFinite(c.Uv.Y)))
                throw new InvalidDataException("A terrain polygon has a non-finite derived coordinate; no geometry was accepted.");
            // Recorded glTF fans can exceed one engine polygon. Keep them through cutting;
            // the output model builder splits/fans them without discarding their geometry.
            if (corners.Count < 3 || Area(corners) < 1e-6) return;
            if (parts.Count >= MaximumFragments) throw new InvalidDataException($"The terrain has more than {MaximumFragments:N0} polygons.");
            ChargeCorners(corners.Count);
            var (min, max) = reaches.TryGetValue(surface, out var reach) ? reach : (new Vector2(float.MaxValue), new Vector2(float.MinValue));
            foreach (var c in corners) { Vector2 p = new(c.Position.X, c.Position.Z); min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            reaches[surface] = (min, max);
            // A polygon that is not convex stays as it is unless a line must cut it (see Both).
            parts.Add(new()
            {
                Surface = surface, Material = face.Material, Corners = [.. corners],
                Edges = [.. corners.Select((c, i) => SourceEdge(c.Position, corners[(i + 1) % corners.Count].Position))], State = state,
                Convex = corners.Count == 3 || Convex(corners),
                AuthoredFan = true,
            });
            static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        }
        /// <summary>Whether the authored fan is planar and turns the same way at every corner (straight corners allowed).</summary>
        private static bool Convex(IReadOnlyList<TerrainCorner> corners)
        {
            Vector3 normal = Vector3.Zero;
            Vector3 origin = corners[0].Position;
            for (int i = 1; i + 1 < corners.Count; i++)
            {
                normal = Vector3.Cross(corners[i].Position - origin, corners[i + 1].Position - origin);
                if (normal.LengthSquared() > 1e-12f) break;
            }
            float length = WorldNumbers.Finite(normal.Length());
            if (length == 0) return false;
            normal /= length;
            for (int i = 0; i < corners.Count; i++)
            {
                var a = corners[i].Position; var b = corners[(i + 1) % corners.Count].Position; var c = corners[(i + 2) % corners.Count].Position;
                // A cancelling or warped fan still contains real triangles. Clip those triangles separately instead
                // of treating its zero summed normal as proof that the whole polygon is convex.
                if (MathF.Abs(Vector3.Dot(a - origin, normal)) > 1e-4f || Vector3.Dot(Vector3.Cross(b - a, c - b), normal) < -1e-6f) return false;
            }
            return true;
        }

        public void CutAtGrid(TerrainGrid grid)
        {
            // Every cell line, and the world's own edges, so parts inside the world fit its cells.
            Tick((long)grid.Columns + grid.Rows + 4);
            SortedSet<double> xs = [grid.OriginX + (double)grid.SizeX], zs = [grid.OriginZ + (double)grid.SizeZ];
            for (int i = 0; i <= grid.Columns; i++) xs.Add(grid.OriginX + i * (double)grid.CellX);
            for (int j = 0; j <= grid.Rows; j++) zs.Add(grid.OriginZ + j * (double)grid.CellZ);
            double[] xLines = [.. xs], zLines = [.. zs];
            List<Part> result = [];
            foreach (var part in parts)
            {
                Tick((long)xLines.Length + zLines.Length + part.Corners.Count);
                List<Part> pending = [part];
                var (min, max) = part.Bounds();
                foreach (double x in Between(xLines, min.X, max.X)) pending = [.. pending.SelectMany(p => Both(p, Line.X(x)))];
                foreach (double z in Between(zLines, min.Z, max.Z)) pending = [.. pending.SelectMany(p => Both(p, Line.Z(z)))];
                result.AddRange(pending);
                if (result.Count > MaximumFragments) throw new InvalidDataException($"Cutting the terrain at the grid makes more than {MaximumFragments:N0} parts.");
            }
            parts.Clear(); parts.AddRange(result);
            static IEnumerable<double> Between(double[] lines, float lo, float hi) => lines.Where(v => v > lo + OnLine && v < hi - OnLine);
        }

        public Dictionary<string, int> IndexSurfaces(IReadOnlyList<TerrainSurfaceGeometry> surfaces)
        {
            Tick(surfaces.Count);
            Dictionary<string, int> indices = new(StringComparer.Ordinal);
            for (int i = 0; i < surfaces.Count; i++) indices.TryAdd(surfaces[i].Surface.Id, i);
            return indices;
        }

        public HashSet<int> RegionSurfaces(TerrainRegion region, IReadOnlyDictionary<string, int> indices, int surfaceCount)
        {
            // Selection itself spends work even when none of the surfaces has any geometry. Charge before
            // allocating and use the shared index rather than rebuilding/searching the surface list for each ID.
            Tick(1L + (region.Surfaces.Count == 0 ? surfaceCount : region.Surfaces.Count));
            HashSet<int> selected = [];
            if (region.Surfaces.Count == 0)
                for (int i = 0; i < surfaceCount; i++) selected.Add(i);
            else
                foreach (string id in region.Surfaces)
                    if (indices.TryGetValue(id, out int index)) selected.Add(index);
            return selected;
        }

        public void ApplyRegion(TerrainRegion region, HashSet<int> surfaces)
        {
            Tick(parts.Count); // Refuse before allocating the region's whole-part copy.
            List<Part> result = new(parts.Count);
            var shape = region.Shape;
            var outline = shape == null ? null : new Outline(shape, count => Tick(count), Reach(surfaces));
            foreach (var part in parts)
            {
                Tick();
                if (!surfaces.Contains(part.Surface)) { result.Add(part); continue; }
                if (outline == null) { part.State = part.State.Apply(region.Set); result.Add(part); continue; }
                Tick(part.Corners.Count);
                var (min, max) = part.Bounds();
                if (!outline.Overlaps(min, max)) { result.Add(part); continue; }
                var crossing = outline.Near(min, max).Where(e => Crosses(part, e.A, e.B)).ToArray();
                Cut(part, crossing, region, outline, result);
            }
            parts.Clear(); parts.AddRange(result);
        }

        /// <summary>Edges a piece may carry before it is halved across them (see <see cref="Cut"/>).</summary>
        private const int PieceEdges = 64;
        /// <summary>
        /// Cuts a part along the outline edges that cross it, so that each resulting piece is wholly inside or outside and
        /// takes its attributes from its centre. A piece is cut only by an edge whose segment crosses it (not merely its
        /// line), with the first such edge in outline order; the later edges go on only to the pieces they can still reach.
        /// So each edge is tested near where it runs rather than against every piece of the part, and the pieces number
        /// about the edges' corners, not the pieces their lines would make. A piece carrying many edges (a detailed
        /// outline over a large polygon) is first halved across them by a line along x or z, so no piece passes long
        /// lists on.
        /// </summary>
        private void Cut(Part part, (Vector2 A, Vector2 B)[] crossing, TerrainRegion region, Outline outline, List<Part> result)
        {
            Tick(crossing.Length);
            Stack<(Part Piece, int[] Edges, int From, bool Halve)> pending = new();
            pending.Push((part, [.. Enumerable.Range(0, crossing.Length)], 0, true));
            while (pending.TryPop(out var item))
            {
                var (piece, edges, from, halve) = item;
                Tick();
                if (halve && edges.Length - from > PieceEdges)
                {
                    if (Halving(piece, edges, from, crossing) is { } across && Cuts(piece, across) is { } halves)
                    {
                        for (int h = halves.Count - 1; h >= 0; h--) pending.Push((halves[h], Reaching(halves[h], across, edges, from, crossing), 0, true));
                        continue;
                    }
                    // No line along x or z divides these edges (they are long and cross each other); later pieces of this one
                    // carry fewer of them, but would not divide them either.
                    halve = false;
                }
                int k = from; List<Part>? pieces = null; Line line = default;
                for (; k < edges.Length && pieces == null; k++)
                {
                    var (a, b) = crossing[edges[k]];
                    if (!Crosses(piece, a, b)) continue;
                    line = Line.Through(a, b);
                    pieces = Cuts(piece, line);
                }
                if (pieces == null)
                {
                    // No edge runs inside the piece, so it lies wholly inside or outside: judged at a point inside it. An
                    // uncut polygon that is not convex is one the outline only passes in its notch, where its centre can lie.
                    if (!piece.Convex) Tick(piece.Corners.Count * (2L + BitOperations.Log2((uint)piece.Corners.Count)));
                    if (outline.Inside(piece.Convex ? piece.Centroid() : piece.Interior())) piece.State = piece.State.Apply(region.Set);
                    result.Add(piece);
                    if (result.Count > MaximumFragments) throw new InvalidDataException($"Region {region.Name} cuts the terrain into more than {MaximumFragments:N0} parts.");
                    continue;
                }
                for (int h = pieces.Count - 1; h >= 0; h--) pending.Push((pieces[h], Reaching(pieces[h], line, edges, k, crossing), 0, halve));
            }
        }
        /// <summary>The pieces a line cuts a piece into, or null when it does not cut it.</summary>
        private List<Part>? Cuts(Part piece, Line line)
        {
            var pieces = Both(piece, line).ToList();
            return pieces.Count == 1 && pieces[0] == piece ? null : pieces;
        }
        /// <summary>The edges from <paramref name="from"/> on that can reach a piece: not wholly beyond its extent across the line that made it.</summary>
        private int[] Reaching(Part piece, Line line, int[] edges, int from, (Vector2 A, Vector2 B)[] crossing)
        {
            Tick(piece.Corners.Count + (long)(edges.Length - from));
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var c in piece.Corners) { double d = line.Distance(c.Position); lo = Math.Min(lo, d); hi = Math.Max(hi, d); }
            List<int> reaching = [];
            for (int i = from; i < edges.Length; i++)
            {
                var (a, b) = crossing[edges[i]];
                double da = line.Distance(a), db = line.Distance(b);
                if (Math.Max(da, db) >= lo - OnLine && Math.Min(da, db) <= hi + OnLine) reaching.Add(edges[i]);
            }
            return [.. reaching];
        }
        /// <summary>
        /// A line along x or z through the middle of a piece's edges (their centres' median) that leaves each side at most
        /// three quarters of them, or null when neither does.
        /// </summary>
        private Line? Halving(Part piece, int[] edges, int from, (Vector2 A, Vector2 B)[] crossing)
        {
            int count = edges.Length - from;
            var (min, max) = piece.Bounds();
            double[] centres = new double[count];
            foreach (bool alongX in max.X - min.X >= max.Z - min.Z ? new[] { true, false } : [false, true])
            {
                Tick(3L * count + (long)count * BitOperations.Log2((uint)count));
                for (int i = 0; i < count; i++) { var (a, b) = crossing[edges[from + i]]; centres[i] = alongX ? (a.X + (double)b.X) / 2 : (a.Y + (double)b.Y) / 2; }
                Array.Sort(centres);
                // On a float, so the cut's points (pinned to the line) lie exactly on it.
                double at = (float)centres[count / 2];
                if (at <= (alongX ? min.X : min.Z) + OnLine || at >= (alongX ? max.X : max.Z) - OnLine) continue;
                int below = 0, above = 0;
                for (int i = 0; i < count; i++)
                {
                    var (a, b) = crossing[edges[from + i]];
                    double lo = alongX ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y), hi = alongX ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y);
                    if (lo <= at + OnLine) below++;
                    if (hi >= at - OnLine) above++;
                }
                if (Math.Max(below, above) <= count * 3L / 4) return alongX ? Line.X(at) : Line.Z(at);
            }
            return null;
        }
        /// <summary>
        /// The plan-view box of the polygons of <paramref name="surfaces"/>: where a region's outline is asked about. Cuts
        /// put their points between existing corners (or on a cell line between them), so every part's corners stay inside
        /// it; the outline answers anything outside it (such as a rounded centre) from every edge.
        /// </summary>
        private (Vector2 Min, Vector2 Max) Reach(HashSet<int> surfaces)
        {
            Tick(surfaces.Count);
            Vector2 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (int surface in surfaces)
                if (reaches.TryGetValue(surface, out var reach)) { min = Vector2.Min(min, reach.Min); max = Vector2.Max(max, reach.Max); }
            return (min, max);
        }

        /// <summary>
        /// Whether an outline edge crosses a part in plan view: its line divides the part, and the segment itself runs inside
        /// it for more than <see cref="OnLine"/> (for an edge shorter than that, its middle lies inside). An edge whose line
        /// crosses the part only beyond the segment's ends does not cut it, so the line's extension cannot slice pieces the
        /// outline never reaches.
        /// </summary>
        private bool Crosses(Part part, Vector2 a, Vector2 b)
        {
            int n = part.Corners.Count;
            Tick(3L * n);
            var line = Line.Through(a, b);
            bool below = false, above = false;
            foreach (var c in part.Corners) { double d = line.Distance(c.Position); below |= d < -OnLine; above |= d > OnLine; }
            if (!(below && above)) return false;
            // Where the line enters and leaves the polygon, by the segment's parameter (0 at a, 1 at b); for a polygon that
            // is not convex, the span of all its crossings.
            double dx = b.X - (double)a.X, dz = b.Y - (double)a.Y, length = dx * dx + dz * dz;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                var p = part.Corners[i].Position; var q = part.Corners[(i + 1) % n].Position;
                double dp = line.Distance(p), dq = line.Distance(q), x, z;
                if (Math.Abs(dp) <= OnLine) (x, z) = (p.X, p.Z);
                else if (dp < -OnLine && dq > OnLine || dp > OnLine && dq < -OnLine)
                {
                    double f = dp / (dp - dq);
                    (x, z) = (p.X + (q.X - (double)p.X) * f, p.Z + (q.Z - (double)p.Z) * f);
                }
                else continue;
                double s = ((x - a.X) * dx + (z - a.Y) * dz) / length;
                lo = Math.Min(lo, s); hi = Math.Max(hi, s);
            }
            double margin = Math.Min(OnLine / Math.Sqrt(length), 0.5);
            return hi > margin && lo < 1 - margin;
        }
        /// <summary>The parts of a convex polygon on either side of a line (one when the line misses it).</summary>
        private IEnumerable<Part> Both(Part part, Line line)
        {
            Tick();
            int n = part.Corners.Count;
            Tick(n);
            int[] side = new int[n]; bool negative = false, positive = false;
            for (int i = 0; i < n; i++)
            {
                double d = line.Distance(part.Corners[i].Position);
                side[i] = d < -OnLine ? -1 : d > OnLine ? 1 : 0;
                negative |= side[i] < 0; positive |= side[i] > 0;
            }
            if (!negative || !positive) return [part];
            // Even a convex authored polygon can carry a non-affine UV mapping. Cutting it as one polygon would
            // introduce another fan's diagonals and interpolate unrelated corners. Clip its original triangles.
            if (!part.Convex || part.AuthoredFan && n > 3) return Fan(part).SelectMany(p => Both(p, line)).ToList();
            ChargeCorners(n + 4); // The two sides together have n original corners and two copies of each cut.
            // The polygon's points in order: its corners, with a cut point on each edge the line crosses.
            List<(TerrainCorner Corner, int Side, int Edge, double T)> points = [];
            for (int i = 0; i < n; i++)
            {
                var edge = part.Edges[i];
                points.Add((part.Corners[i], side[i], i, edge.T0));
                int j = (i + 1) % n;
                if (side[i] * side[j] >= 0) continue;
                var segment = edge.Segment;
                double da = line.Distance(segment.A), db = line.Distance(segment.B);
                double t = Math.Clamp(da / (da - db), Math.Min(edge.T0, edge.T1), Math.Max(edge.T0, edge.T1));
                var position = segment.At(t, line);
                double s = edge.T1 == edge.T0 ? 0 : (t - edge.T0) / (edge.T1 - edge.T0);
                points.Add((Mix(part.Corners[i], part.Corners[j], s, position), 0, i, t));
            }
            // The chord: the line between the two points on it, one segment both parts share.
            var onLine = points.Where(p => p.Side == 0).ToList();
            if (onLine.Count != 2) return part.Corners.Count > 3 ? Fan(part).SelectMany(p => Both(p, line)).ToList() : [part];
            bool forward = Less(onLine[0].Corner.Position, onLine[1].Corner.Position);
            Segment chord = forward ? new(onLine[0].Corner.Position, onLine[1].Corner.Position) : new(onLine[1].Corner.Position, onLine[0].Corner.Position);
            double ChordT(Vector3 p) => p == chord.A ? 0 : 1;
            List<Part> result = [];
            foreach (int keep in new[] { -1, 1 })
            {
                var chosen = points.Where(p => p.Side == 0 || p.Side == keep).ToList();
                if (chosen.Count < 3) continue;
                List<TerrainCorner> corners = [.. chosen.Select(p => p.Corner)];
                List<EdgeRef> edges = [];
                for (int k = 0; k < chosen.Count; k++)
                {
                    var from = chosen[k]; var to = chosen[(k + 1) % chosen.Count];
                    // Two points on the line are the chord; otherwise the edge runs along the original edge the first point starts.
                    if (from.Side == 0 && to.Side == 0) edges.Add(new(chord, ChordT(from.Corner.Position), ChordT(to.Corner.Position)));
                    else
                    {
                        var original = part.Edges[from.Edge];
                        double end = to.Edge == from.Edge ? to.T : original.T1;
                        edges.Add(new(original.Segment, from.T, end));
                    }
                }
                if (Area(corners) < 1e-6) continue;
                result.Add(new() { Surface = part.Surface, Material = part.Material, Corners = corners, Edges = edges, State = part.State });
            }
            return result.Count == 0 ? [part] : result;
        }
        /// <summary>
        /// A polygon that is not convex as the fan of triangles the engine draws it with, from its first corner: the
        /// fan's inner edges are new segments each pair of neighbouring triangles shares.
        /// </summary>
        private IEnumerable<Part> Fan(Part part)
        {
            var c = part.Corners; int n = c.Count;
            Segment Diagonal(int k) => Less(c[0].Position, c[k].Position) ? new(c[0].Position, c[k].Position) : new(c[k].Position, c[0].Position);
            EdgeRef From(Segment s, Vector3 start) => start == s.A ? new(s, 0, 1) : new(s, 1, 0);
            Segment? previous = null;
            for (int i = 1; i + 1 < n; i++)
            {
                ChargeCorners(3);
                var next = i + 1 < n - 1 ? Diagonal(i + 1) : null;
                List<EdgeRef> edges =
                [
                    i == 1 ? part.Edges[0] : From(previous!, c[0].Position),
                    part.Edges[i],
                    next == null ? part.Edges[n - 1] : From(next, c[i + 1].Position),
                ];
                previous = next;
                // A recorded fan may start with (or contain) a zero-area triangle. Keep advancing its diagonals,
                // but do not emit an empty fragment that the model builder would legitimately discard.
                if (ModelBuilder.Straight(c[0].Position, c[i].Position, c[i + 1].Position)) continue;
                yield return new() { Surface = part.Surface, Material = part.Material, Corners = [c[0], c[i], c[i + 1]], Edges = edges, State = part.State };
            }
        }
        private static float Area(IReadOnlyList<TerrainCorner> corners)
        {
            float sum = 0;
            for (int i = 1; i + 1 < corners.Count; i++)
                sum += Vector3.Cross(corners[i].Position - corners[0].Position, corners[i + 1].Position - corners[0].Position).Length();
            return WorldNumbers.Finite(sum / 2);
        }
        private static TerrainCorner Mix(TerrainCorner a, TerrainCorner b, double s, Vector3 position)
        {
            float f = (float)Math.Clamp(s, 0, 1);
            var normal = Vector3.Lerp(a.Normal, b.Normal, f);
            return new(position, normal, Vector2.Lerp(a.Uv, b.Uv, f)) { Interpolated = true };
        }

        /// <summary>Inserts into every edge the points other parts put on its segment, so no part ends at another's edge.</summary>
        public void RepairJunctions()
        {
            foreach (var part in parts)
            {
                Tick();
                for (int i = part.Corners.Count - 1; i >= 0; i--)
                {
                    var edge = part.Edges[i];
                    double lo = Math.Min(edge.T0, edge.T1), hi = Math.Max(edge.T0, edge.T1);
                    // The segment's points strictly inside this edge, found by parameter: many neighbouring parts may share a
                    // densely cut segment, so each finds its own span rather than reading every point. Counted before the
                    // lists are created.
                    var (keys, values) = edge.Segment.Sorted();
                    Tick(1 + BitOperations.Log2((uint)keys.Length));
                    int first = Bound(keys, lo + 1e-12, true), end = Bound(keys, hi - 1e-12, false), count = end - first;
                    if (count <= 0) continue;
                    Tick(count);
                    ChargeCorners(count);
                    var inside = Enumerable.Range(first, count).ToList();
                    if (edge.T0 > edge.T1) inside.Reverse();
                    var from = part.Corners[i]; var to = part.Corners[(i + 1) % part.Corners.Count];
                    List<TerrainCorner> added = []; List<EdgeRef> split = [];
                    double previous = edge.T0;
                    foreach (int k in inside)
                    {
                        double t = keys[k], s = (t - edge.T0) / (edge.T1 - edge.T0);
                        added.Add(Mix(from, to, s, values[k]));
                        split.Add(new(edge.Segment, previous, t)); previous = t;
                    }
                    split.Add(new(edge.Segment, previous, edge.T1));
                    part.Changed = true;
                    part.Corners.InsertRange(i + 1, added);
                    part.Edges.RemoveAt(i); part.Edges.InsertRange(i, split);
                }
            }
        }
        /// <summary>The first index whose key is above <paramref name="value"/> (<paramref name="above"/>) or at least it.</summary>
        private static int Bound(double[] keys, double value, bool above)
        {
            int lo = 0, hi = keys.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (above ? keys[mid] <= value : keys[mid] < value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        public List<TerrainPiece> Pieces(string label, TerrainRecipe recipe, IReadOnlyList<TerrainSurfaceGeometry> surfaces, IReadOnlyList<TerrainMaterialInfo> materials, TerrainGrid grid, List<string> warnings)
        {
            // Group parts by surface, cell and node attributes; with an automatic node zone, also by polygon zones.
            var groups = new SortedDictionary<(int Surface, int Row, int Column, uint Carried, int NodeZone, uint ZoneKey), List<Part>>();
            foreach (var part in parts)
            {
                Tick(part.Corners.Count);
                var centre = part.Centroid();
                var (column, row) = grid.Cell(centre.X, centre.Z);
                uint zones = part.State.Zones?.Word ?? materials[part.Material].ZoneWord;
                var key = (part.Surface, row, column, part.State.Carried, part.State.NodeZone, part.State.NodeZone == TerrainAttributes.AutoZone ? zones : 0u);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                list.Add(part);
            }
            List<TerrainPiece> pieces = [];
            Dictionary<(int, int, int), int> perCell = [];
            foreach (var (key, list) in groups)
            {
                token.ThrowIfCancellationRequested();
                uint carried = key.Carried; byte zone;
                if (key.NodeZone != TerrainAttributes.AutoZone) zone = (byte)key.NodeZone;
                else
                {
                    // One shared zone names the node; a transition (several zones) or "any" admits every zone, with the gate on.
                    int count = (int)(key.ZoneKey & 0xFF);
                    zone = count == 1 ? (byte)(key.ZoneKey >> 8) : (byte)0xFF;
                    if (count != 1) carried |= 0x01000000;
                }
                // A crater-capable node (CanModify; with ClipTo too it counts as ClipTo) keeps the crater limits.
                bool craters = (carried & 0x30000) == 0x10000;
                foreach (var group in Divide(craters ? [.. list.SelectMany(Limit)] : list, craters ? CraterVertexBudget : VertexBudget))
                {
                    int index = perCell.TryGetValue((key.Surface, key.Row, key.Column), out int seen) ? seen + 1 : 0;
                    perCell[(key.Surface, key.Row, key.Column)] = index;
                    string cell = key.Column < 0 ? "out" : $"{key.Column:D2}x{key.Row:D2}";
                    string name = Name(label, surfaces[key.Surface].Surface.Id, key.Surface, cell, index);
                    var polygons = group.SelectMany(p => Output(p).Select(corners => new TerrainPolygonOutput(p.Material, corners, p.State.Zones?.Word ?? materials[p.Material].ZoneWord, p.State.Soil, p.State.Priority))).ToArray();
                    pieces.Add(new(name, key.Surface, key.Column, key.Row, carried, zone, polygons));
                }
            }
            return pieces;
        }

        /// <summary>Divides a group whose vertices or normals pass the budget, retaining polygon encounter order.</summary>
        private IEnumerable<List<Part>> Divide(List<Part> group, int budget)
        {
            Stack<List<Part>> pending = new([group]);
            List<List<Part>> done = [];
            while (pending.TryPop(out var list))
            {
                token.ThrowIfCancellationRequested();
                if (Counts(list, budget) <= budget) { done.Add(list); continue; }
                // Junction repair may give one polygon more corners than a model can hold. Split its fan before
                // grouping, rather than treating a single polygon as indivisible and letting ModelBuilder discard it.
                if (list.Count == 1) { pending.Push(Fan(list[0]).ToList()); continue; }
                int half = list.Count / 2;
                // Spatial sorting would change precedence for overlapping faces. Contiguous halves may be less
                // compact within their cell, but retain the same encounter order through any additional splits.
                // Pushed second half first, so the first half is finished (and named) first.
                Tick(list.Count);
                pending.Push(list.GetRange(half, list.Count - half));
                pending.Push(list.GetRange(0, half));
            }
            return done;
        }
        /// <summary>A conservative bound on the vertex and emitted-normal counts a group would store (past the budget, budget + 1).</summary>
        private int Counts(List<Part> list, int budget)
        {
            // ModelBuilder can merge these exact values, never create additional ones. Rounded integer buckets
            // are not its corner identity and can overflow for finite coordinates, hiding a required split.
            HashSet<Vector3> vertices = [], normals = [];
            foreach (var part in list)
                foreach (var c in part.Corners)
                {
                    Tick();
                    vertices.Add(c.Position);
                    normals.Add(OutputNormal(c));
                    if (vertices.Count > budget || normals.Count > budget) return budget + 1;
                }
            return Math.Max(vertices.Count, normals.Count);
        }

        /// <summary>
        /// A crater-capable piece's polygon as polygons of at most <see cref="CraterCorners"/> corners, split along chords
        /// between its corners: no corner is added, and every corner stays on the edges it shares with neighbouring polygons
        /// (junction repair's included). An authored fan becomes consecutive fans from its first corner, which draw the same
        /// triangles; a fan of only straight triangles there (corners on a line through the first) holds nothing to draw and
        /// is left out. A derived convex piece is halved between two corners on different edge lines, so both halves keep
        /// area.
        /// </summary>
        private IEnumerable<Part> Limit(Part part)
        {
            int n = part.Corners.Count;
            if (n <= CraterCorners) return [part];
            Tick(n);
            List<int[]> polygons = [];
            if (part.AuthoredFan) Fans([.. Enumerable.Range(0, n)]);
            else
            {
                Stack<int[]> pending = new([[.. Enumerable.Range(0, n)]]);
                while (pending.TryPop(out var polygon))
                {
                    if (polygon.Length <= CraterCorners) { polygons.Add(polygon); continue; }
                    // A sliver whose corners all lie within OnLine of fewer than three lines is fanned instead.
                    if (Across(polygon) is not { } across) { Fans(polygon); continue; }
                    var (i, j) = across; int m = polygon.Length;
                    pending.Push([.. Enumerable.Range(0, (i - j + m) % m + 1).Select(k => polygon[(j + k) % m])]);
                    pending.Push([.. Enumerable.Range(0, (j - i + m) % m + 1).Select(k => polygon[(i + k) % m])]);
                }
            }
            ChargeCorners(polygons.Sum(p => p.Length));
            return polygons.Select(p => new Part
            {
                Surface = part.Surface, Material = part.Material, State = part.State, Convex = part.Convex, AuthoredFan = part.AuthoredFan, Changed = true,
                Corners = [.. p.Select(k => part.Corners[k])],
                // An edge between neighbouring corners is the polygon's own; any other is a new chord.
                Edges = [.. p.Select((k, e) => p[(e + 1) % p.Length] == (k + 1) % n ? part.Edges[k] : ChordEdge(part.Corners[k].Position, part.Corners[p[(e + 1) % p.Length]].Position))],
            });

            Vector3 At(int k) => part.Corners[k].Position;
            // Consecutive fans from the polygon's first corner, of at most CraterCorners corners each, leaving out a fan whose
            // triangles are all straight. A fan ends where the next one starts on a steady triangle when it can (see Output).
            void Fans(int[] polygon)
            {
                var apex = At(polygon[0]);
                for (int a = 1; a + 1 < polygon.Length;)
                {
                    int b = Math.Min(polygon.Length - 1, a + CraterCorners - 2);
                    if (b < polygon.Length - 1)
                        for (int k = b; k > a; k--)
                            if (Steady(apex, At(polygon[k]), At(polygon[k + 1]))) { b = k; break; }
                    if (Enumerable.Range(a, b - a).Any(i => !ModelBuilder.Straight(apex, At(polygon[i]), At(polygon[i + 1]))))
                        polygons.Add([polygon[0], .. polygon[a..(b + 1)]]);
                    a = b;
                }
            }
            // Two corners of a convex polygon (as positions in it) whose chord halves its corners as nearly as it can, with the
            // corners on no common edge line: the middle of its longest edge, and the corner across from it.
            (int, int)? Across(int[] polygon)
            {
                int m = polygon.Length;
                Tick(4L * m);
                var p = polygon.Select(k => part.Corners[k].Position).ToArray();
                // Corners where the boundary turns start each edge line; a corner within OnLine of its neighbours' line does not.
                bool[] turn = new bool[m];
                for (int k = 0; k < m; k++)
                {
                    Vector3 a = p[(k + m - 1) % m], b = p[k], c = p[(k + 1) % m];
                    double ux = b.X - (double)a.X, uy = b.Y - (double)a.Y, uz = b.Z - (double)a.Z, vx = c.X - (double)a.X, vy = c.Y - (double)a.Y, vz = c.Z - (double)a.Z;
                    double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx, span = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                    turn[k] = span == 0 || Math.Sqrt(cx * cx + cy * cy + cz * cz) / span > OnLine;
                }
                int first = Array.IndexOf(turn, true);
                if (first < 0) return null;
                int[] line = new int[m];
                int lines = 0;
                for (int s = 0; s < m; s++) { int k = (first + s) % m; if (turn[k]) lines++; line[k] = lines - 1; }
                if (lines < 3) return null;
                // The longest edge line: from its turning corner to the next.
                int start = first, length = 0;
                for (int s = 0, from = first; s < m; s++)
                {
                    int k = (first + s + 1) % m;
                    if (!turn[k] && s + 1 < m) continue;
                    int run = (k - from + m) % m;
                    if (run == 0) run = m;
                    if (run > length) { length = run; start = from; }
                    from = k;
                }
                int i = (start + length / 2) % m;
                bool Share(int a, int b) => line[a] == line[b] || turn[a] && (line[a] + lines - 1) % lines == line[b] || turn[b] && (line[b] + lines - 1) % lines == line[a] || turn[a] && turn[b] && (line[a] + lines - 1) % lines == (line[b] + lines - 1) % lines;
                for (int offset = 0; offset < m; offset++)
                    foreach (int d in new[] { m / 2 + offset, m / 2 - offset })
                        if (d >= 2 && d <= m - 2 && !Share(i, (i + d) % m)) return (i, (i + d) % m);
                return null;
            }
        }
        private static EdgeRef ChordEdge(Vector3 from, Vector3 to)
        {
            Segment segment = Less(from, to) ? new(from, to) : new(to, from);
            return from == segment.A ? new(segment, 0, 1) : new(segment, 1, 0);
        }
        private static Vector3 OutputNormal(TerrainCorner corner)
        {
            var normal = corner.Normal;
            if (!corner.Interpolated) return normal;
            double length = Math.Sqrt((double)normal.X * normal.X + (double)normal.Y * normal.Y + (double)normal.Z * normal.Z);
            return length > 0 ? new((float)(normal.X / length), (float)(normal.Y / length), (float)(normal.Z / length)) : normal;
        }
        /// <summary>
        /// A part's output polygons. Authored fans keep their triangles and generated corner normals are normalized only after
        /// every cut and junction repair. The engine takes a polygon's plane from its first three corners, so a polygon the build
        /// made or changed that does not start on a <see cref="Steady"/> triangle (corners junction repair put on an authored
        /// fan's first edge, or a cut piece's corners on one line) starts again at its steadiest: a cut piece or a triangle (with corners on its edges)
        /// is turned, which draws the same triangle or plane; an authored fan of more corners gives up its leading triangles
        /// (those corners and its first real triangle) as a triangle of their own, so every triangle it draws stays the same.
        /// </summary>
        private IEnumerable<TerrainCorner[]> Output(Part part)
        {
            TerrainCorner[] corners = part.AuthoredFan ? [.. part.Corners] : Ordered(part.Corners);
            for (int i = 0; i < corners.Length; i++)
            {
                var corner = corners[i];
                corners[i] = corner with { Normal = OutputNormal(corner), Interpolated = false };
            }
            int n = corners.Length;
            Tick(n);
            // An authored fan the build did not change stays as recorded. A cut piece lies in one source triangle's plane, so
            // its start must also give that plane at every corner.
            if (n < 3 || part.AuthoredFan && !part.Changed
                || Steady(corners[0].Position, corners[1].Position, corners[2].Position) && (part.AuthoredFan || Off(corners, 0) <= PlaneTolerance)) return [corners];
            if (!part.AuthoredFan || Turns(corners) <= 3) return [Steadiest(corners)];
            // Fan triangle i (first corner, i, i + 1) and whether any from i on is steady.
            bool[] steady = new bool[n], later = new bool[n + 1];
            for (int i = n - 2; i >= 1; i--) { steady[i] = Steady(corners[0].Position, corners[i].Position, corners[i + 1].Position); later[i] = steady[i] || later[i + 1]; }
            List<TerrainCorner[]> result = [];
            for (int from = 1; ;)
            {
                // The fan from the first corner over corners from..n − 1.
                TerrainCorner[] rest = [corners[0], .. corners[from..]];
                if (steady[from]) { result.Add(rest); break; }
                int a = from;
                while (a < n - 1 && !steady[a]) a++;
                // No steady triangle left, or none after this one: what remains is that triangle with corners on its edges.
                if (a >= n - 1 || !later[a + 1]) { result.Add(Steadiest(rest)); break; }
                result.Add(Steadiest([corners[0], .. corners[from..(a + 2)]]));
                from = a + 1;
            }
            ChargeCorners(result.Sum(p => p.Length) - n);
            return result;
        }
        /// <summary>
        /// A planar polygon (a cut piece, or a triangle with corners on its edges) turned to start at a steady triangle where
        /// the engine's plane passes nearest its corners (of the eight with the largest smallest heights). A sliver with no
        /// steady triangle (a few thousandths wide, so any plane along it fits) starts at its widest angle.
        /// </summary>
        private TerrainCorner[] Steadiest(TerrainCorner[] corners)
        {
            int n = corners.Length;
            Tick(9L * n);
            var starts = Enumerable.Range(0, n).Select(s => (Start: s, Steadiness: Steadiness(corners[s].Position, corners[(s + 1) % n].Position, corners[(s + 2) % n].Position)))
                .OrderByDescending(s => s.Steadiness.Height).ThenBy(s => s.Start).ToArray();
            var steady = starts.Where(s => s.Steadiness.Shape >= SteadyShape && s.Steadiness.Height >= SteadyHeight).Take(8).ToArray();
            int best = steady.Length > 0 ? steady.MinBy(s => Off(corners, s.Start)).Start : Enumerable.Range(0, n).MaxBy(s => Sine(corners[s].Position, corners[(s + 1) % n].Position, corners[(s + 2) % n].Position));
            return [.. corners[best..], .. corners[..best]];

            static double Sine(Vector3 a, Vector3 b, Vector3 c)
            {
                double lengths = (double)Vector3.Distance(a, b) * Vector3.Distance(a, c);
                return lengths > 0 ? Vector3.Cross(b - a, c - a).Length() / lengths : 0;
            }
        }
        /// <summary>
        /// How far the polygon's corners lie from the engine's plane through those from <paramref name="start"/> (its normal
        /// as the probes compute it, <see cref="ZoneProbe.PlaneNormal"/>); infinite when that plane is not a number.
        /// </summary>
        private static double Off(TerrainCorner[] corners, int start)
        {
            int n = corners.Length;
            Vector3 origin = corners[start].Position, normal = ZoneProbe.PlaneNormal(origin, corners[(start + 1) % n].Position, corners[(start + 2) % n].Position);
            if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z)) return double.PositiveInfinity;
            double worst = 0;
            foreach (var corner in corners)
                worst = Math.Max(worst, Math.Abs(normal.X * (corner.Position.X - (double)origin.X) + normal.Y * (corner.Position.Y - (double)origin.Y) + normal.Z * (corner.Position.Z - (double)origin.Z)));
            return worst;
        }
        /// <summary>Corners where the boundary turns: farther than OnLine from the line through their neighbours.</summary>
        private static int Turns(TerrainCorner[] corners)
        {
            int n = corners.Length, count = 0;
            for (int k = 0; k < n; k++)
            {
                Vector3 a = corners[(k + n - 1) % n].Position, b = corners[k].Position, c = corners[(k + 1) % n].Position;
                double ux = b.X - (double)a.X, uy = b.Y - (double)a.Y, uz = b.Z - (double)a.Z, vx = c.X - (double)a.X, vy = c.Y - (double)a.Y, vz = c.Z - (double)a.Z;
                double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx, span = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                if (span == 0 || Math.Sqrt(cx * cx + cy * cy + cz * cz) / span > OnLine) count++;
            }
            return count;
        }
        /// <summary>
        /// The least shape (doubled area over the longest edge squared, which bounds the sine at each corner) and the least
        /// height a polygon's first three corners must have for the engine's plane through them to be the polygon's: the
        /// height keeps them well apart beyond the model builder's 0.001 vertex merging.
        /// </summary>
        private const double SteadyShape = 1e-3, SteadyHeight = 0.01;
        /// <summary>How far a planar polygon's corners may lie from the engine's plane through its first three.</summary>
        private const double PlaneTolerance = 0.005;
        private static bool Steady(Vector3 a, Vector3 b, Vector3 c) => Steadiness(a, b, c) is var (shape, height) && shape >= SteadyShape && height >= SteadyHeight;
        private static (double Shape, double Height) Steadiness(Vector3 a, Vector3 b, Vector3 c)
        {
            double ux = b.X - (double)a.X, uy = b.Y - (double)a.Y, uz = b.Z - (double)a.Z, vx = c.X - (double)a.X, vy = c.Y - (double)a.Y, vz = c.Z - (double)a.Z;
            double wx = vx - ux, wy = vy - uy, wz = vz - uz;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx, cross = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            double longest = Math.Max(ux * ux + uy * uy + uz * uz, Math.Max(vx * vx + vy * vy + vz * vz, wx * wx + wy * wy + wz * wz));
            return longest > 0 ? (cross / longest, cross / Math.Sqrt(longest)) : (0, 0);
        }

        /// <summary>A derived convex piece of one triangle, starting where the first three are not on one line. Authored fans must retain their first corner.</summary>
        private static TerrainCorner[] Ordered(List<TerrainCorner> corners)
        {
            int n = corners.Count;
            for (int start = 0; start < n; start++)
            {
                var a = corners[start].Position; var b = corners[(start + 1) % n].Position; var c = corners[(start + 2) % n].Position;
                if (Vector3.Cross(b - a, c - a).LengthSquared() > 1e-10f) return [.. corners.Skip(start), .. corners.Take(start)];
            }
            return [.. corners];
        }
        private static string Name(string label, string surface, int surfaceIndex, string cell, int index)
        {
            string suffix = $"_{cell}" + (index > 0 ? $"_{index}" : "");
            string head = $"{label}_{surface}";
            if (head.Length + suffix.Length > 34)
            {
                // A shortened head ends with ~ (which labels and surface ids never hold, so no full name can equal it) and the
                // surface's place in the recipe (two base-36 digits; at most 256 surfaces), so surfaces whose ids begin alike
                // keep apart.
                const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
                head = $"{head[..(34 - suffix.Length - 3)]}~{digits[surfaceIndex / 36 % 36]}{digits[surfaceIndex % 36]}";
            }
            return head + suffix;
        }
    }
}
