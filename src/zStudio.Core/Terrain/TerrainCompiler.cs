using System.Numerics;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Terrain;

/// <summary>A corner of terrain geometry in world space.</summary>
public readonly record struct TerrainCorner(Vector3 Position, Vector3 Normal, Vector2 Uv);
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
    /// <summary>Distance below which a point counts as on a cut line (the model builder merges vertices within 0.001).</summary>
    private const double OnLine = 1e-3;
    public const int MaximumFragments = 4_000_000;

    public static TerrainCompilation Compile(string label, TerrainRecipe recipe, IReadOnlyList<TerrainSurfaceGeometry> surfaces, IReadOnlyList<TerrainMaterialInfo> materials, TerrainGrid grid, CancellationToken token = default)
    {
        if (grid.CellX <= 0 || grid.CellZ >= 0 || grid.Columns < 1 || grid.Rows < 1) throw new InvalidDataException("Terrain needs the world's grid: WorldOrigin, WorldExtents and WorldPartition must run before the mission database is loaded.");
        Compiler compiler = new(token);
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
        foreach (var region in recipe.Regions)
        {
            HashSet<int> on = region.Surfaces.Count == 0 ? [.. Enumerable.Range(0, surfaces.Count)] : [.. region.Surfaces.Select(id => surfaces.ToList().FindIndex(s => s.Surface.Id == id)).Where(i => i >= 0)];
            compiler.ApplyRegion(region, on);
        }
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
            return total > 0 ? sum / total : Corners.Aggregate(Vector3.Zero, (s, c) => s + c.Position) / Corners.Count;
        }
    }

    private sealed class Compiler(CancellationToken token)
    {
        private readonly List<Part> parts = [];
        private readonly Dictionary<(Vector3, Vector3), Segment> sourceEdges = [];
        private int work;

        private void Tick() { if ((++work & 4095) == 0) token.ThrowIfCancellationRequested(); }
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
            if (corners.Count < 3 || corners.Count > ModelBuilder.MaximumCorners || corners.Any(c => !Finite(c.Position)) || Area([.. corners]) < 1e-6) return;
            if (parts.Count >= MaximumFragments) throw new InvalidDataException($"The terrain has more than {MaximumFragments:N0} polygons.");
            // A polygon that is not convex stays as it is unless a line must cut it (see Both).
            parts.Add(new()
            {
                Surface = surface, Material = face.Material, Corners = [.. corners],
                Edges = [.. corners.Select((c, i) => SourceEdge(c.Position, corners[(i + 1) % corners.Count].Position))], State = state,
                Convex = corners.Count == 3 || Convex(corners),
            });
            static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        }
        /// <summary>Whether a polygon turns the same way at every corner (a straight corner allowed), about its own normal.</summary>
        private static bool Convex(IReadOnlyList<TerrainCorner> corners)
        {
            Vector3 normal = Vector3.Zero;
            for (int i = 0; i < corners.Count; i++)
            {
                var a = corners[i].Position; var b = corners[(i + 1) % corners.Count].Position;
                normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
            }
            for (int i = 0; i < corners.Count; i++)
            {
                var a = corners[i].Position; var b = corners[(i + 1) % corners.Count].Position; var c = corners[(i + 2) % corners.Count].Position;
                if (Vector3.Dot(Vector3.Cross(b - a, c - b), normal) < -1e-6f * normal.Length()) return false;
            }
            return true;
        }

        public void CutAtGrid(TerrainGrid grid)
        {
            // Every cell line, and the world's own edges, so parts inside the world fit its cells.
            SortedSet<double> xs = [grid.OriginX + (double)grid.SizeX], zs = [grid.OriginZ + (double)grid.SizeZ];
            for (int i = 0; i <= grid.Columns; i++) xs.Add(grid.OriginX + i * (double)grid.CellX);
            for (int j = 0; j <= grid.Rows; j++) zs.Add(grid.OriginZ + j * (double)grid.CellZ);
            double[] xLines = [.. xs], zLines = [.. zs];
            List<Part> result = [];
            foreach (var part in parts)
            {
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

        public void ApplyRegion(TerrainRegion region, HashSet<int> surfaces)
        {
            List<Part> result = new(parts.Count);
            var shape = region.Shape;
            var outline = shape == null ? null : new Outline(shape);
            foreach (var part in parts)
            {
                Tick();
                if (!surfaces.Contains(part.Surface)) { result.Add(part); continue; }
                if (outline == null) { part.State = part.State.Apply(region.Set); result.Add(part); continue; }
                var (min, max) = part.Bounds();
                if (!outline.Overlaps(min, max)) { result.Add(part); continue; }
                // Cut along the outline edges that cross this part; each resulting part is then wholly inside or outside. A
                // sub-part is cut only by an edge that crosses it, so a detailed outline does not shatter the whole part.
                var crossing = outline.Near(min, max).Where(e => Crosses(part, e.A, e.B)).ToArray();
                List<Part> pending = [part];
                foreach (var e in crossing)
                {
                    var line = Line.Through(e.A, e.B);
                    pending = [.. pending.SelectMany(p => Crosses(p, e.A, e.B) ? Both(p, line) : [p])];
                    if (pending.Count > MaximumFragments) throw new InvalidDataException($"Region {region.Name} cuts the terrain into more than {MaximumFragments:N0} parts.");
                }
                foreach (var p in pending)
                {
                    var centre = p.Centroid();
                    if (outline.Inside(centre)) p.State = p.State.Apply(region.Set);
                    result.Add(p);
                }
                if (result.Count > MaximumFragments) throw new InvalidDataException($"Region {region.Name} cuts the terrain into more than {MaximumFragments:N0} parts.");
            }
            parts.Clear(); parts.AddRange(result);
        }

        /// <summary>Whether an outline edge meets a part's polygon in plan view.</summary>
        private static bool Crosses(Part part, Vector2 a, Vector2 b)
        {
            var line = Line.Through(a, b);
            bool below = false, above = false;
            foreach (var c in part.Corners) { double d = line.Distance(c.Position); below |= d < -OnLine; above |= d > OnLine; }
            if (!(below && above)) return false;
            // The edge's line crosses the polygon; the edge itself does when its extent along the line overlaps the polygon's.
            double dx = b.X - (double)a.X, dz = b.Y - (double)a.Y, length = dx * dx + dz * dz;
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var c in part.Corners) { double s = ((c.Position.X - a.X) * dx + (c.Position.Z - a.Y) * dz) / length; lo = Math.Min(lo, s); hi = Math.Max(hi, s); }
            return hi >= 0 && lo <= 1;
        }
        /// <summary>
        /// A region outline's edges in a uniform grid over its plan-view bounds. An edge is listed in every bucket its box
        /// overlaps, so a part finds the edges near it, and a point's ray towards +x the edges along its row.
        /// </summary>
        private sealed class Outline
        {
            /// <summary>Bucket entries at most; an outline of long edges crossing many buckets uses one bucket instead.</summary>
            private const long MaximumEntries = 16_000_000;
            private readonly TerrainShape shape;
            private readonly (Vector2 A, Vector2 B, Vector2 Min, Vector2 Max, int Ring)[] edges;
            private readonly (int Outer, int[] Holes)[] polygons;
            private readonly int[][] buckets;
            private readonly int columns, rows;
            private readonly Vector2 min, max, size;
            private readonly int[] seen;
            private readonly bool[] parity;
            private int pass;

            public Outline(TerrainShape shape)
            {
                this.shape = shape;
                List<(Vector2 A, Vector2 B, Vector2 Min, Vector2 Max, int Ring)> list = [];
                List<(int, int[])> rings = [];
                int ring = 0;
                foreach (var polygon in shape.Polygons)
                {
                    int outer = ring;
                    foreach (var points in polygon.Holes.Prepend(polygon.Outer))
                    {
                        for (int i = 0; i < points.Count; i++)
                        {
                            var a = points[i]; var b = points[(i + 1) % points.Count];
                            if (a != b) list.Add((a, b, Vector2.Min(a, b), Vector2.Max(a, b), ring));
                        }
                        ring++;
                    }
                    rings.Add((outer, [.. Enumerable.Range(outer + 1, polygon.Holes.Count)]));
                }
                edges = [.. list]; polygons = [.. rings]; parity = new bool[ring]; seen = new int[edges.Length];
                min = edges.Length == 0 ? default : edges.Aggregate(new Vector2(float.MaxValue), (m, e) => Vector2.Min(m, e.Min));
                max = edges.Length == 0 ? default : edges.Aggregate(new Vector2(float.MinValue), (m, e) => Vector2.Max(m, e.Max));
                int side = Math.Clamp((int)Math.Sqrt(edges.Length / 2.0), 1, 1024);
                (columns, rows, size) = Layout(side);
                long entries = edges.Sum(e => (long)(Column(e.Max.X) - Column(e.Min.X) + 1) * (Row(e.Max.Y) - Row(e.Min.Y) + 1));
                if (entries > MaximumEntries) (columns, rows, size) = Layout(1);
                var lists = new List<int>[columns * rows];
                for (int i = 0; i < edges.Length; i++)
                    for (int r = Row(edges[i].Min.Y); r <= Row(edges[i].Max.Y); r++)
                        for (int c = Column(edges[i].Min.X); c <= Column(edges[i].Max.X); c++)
                            (lists[r * columns + c] ??= []).Add(i);
                buckets = [.. lists.Select(l => l?.ToArray() ?? [])];
            }
            private (int, int, Vector2) Layout(int side) => (side, side, new(Math.Max((max.X - min.X) / side, 1e-3f), Math.Max((max.Y - min.Y) / side, 1e-3f)));
            private int Column(float x) => Math.Clamp((int)((x - min.X) / size.X), 0, columns - 1);
            private int Row(float z) => Math.Clamp((int)((z - min.Y) / size.Y), 0, rows - 1);

            /// <summary>Whether a part's plan-view box meets the outline's.</summary>
            public bool Overlaps(Vector3 lo, Vector3 hi) => edges.Length > 0 && !(hi.X < min.X || lo.X > max.X || hi.Z < min.Y || lo.Z > max.Y);

            /// <summary>The edges whose boxes meet a plan-view box, in outline order.</summary>
            public IEnumerable<(Vector2 A, Vector2 B)> Near(Vector3 lo, Vector3 hi)
            {
                pass++;
                List<int> found = [];
                for (int r = Row(lo.Z); r <= Row(hi.Z); r++)
                    for (int c = Column(lo.X); c <= Column(hi.X); c++)
                        foreach (int i in buckets[r * columns + c])
                            if (seen[i] != pass && edges[i].Max.X >= lo.X && edges[i].Min.X <= hi.X && edges[i].Max.Y >= lo.Z && edges[i].Min.Y <= hi.Z) { seen[i] = pass; found.Add(i); }
                found.Sort();
                return found.Select(i => (edges[i].A, edges[i].B));
            }

            /// <summary>Whether a point lies in the shape (inside an outer ring and none of its holes, within the height range).</summary>
            public bool Inside(Vector3 point)
            {
                if (shape.MinY is { } minY && point.Y < minY || shape.MaxY is { } maxY && point.Y > maxY) return false;
                Vector2 p = new(point.X, point.Z);
                if (edges.Length == 0 || p.X < min.X || p.X > max.X || p.Y < min.Y || p.Y > max.Y) return false;
                pass++;
                Array.Clear(parity);
                // A ray towards +x crosses each ring an odd number of times when the point is inside it.
                int row = Row(p.Y);
                for (int c = Column(p.X); c < columns; c++)
                    foreach (int i in buckets[row * columns + c])
                    {
                        if (seen[i] == pass) continue;
                        seen[i] = pass;
                        var (a, b, _, _, ring) = edges[i];
                        if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - (double)a.Y) / (b.Y - (double)a.Y) + a.X) parity[ring] = !parity[ring];
                    }
                foreach (var (outer, holes) in polygons)
                    if (parity[outer] && !holes.Any(h => parity[h])) return true;
                return false;
            }
        }

        /// <summary>The parts of a convex polygon on either side of a line (one when the line misses it).</summary>
        private IEnumerable<Part> Both(Part part, Line line)
        {
            Tick();
            int n = part.Corners.Count;
            int[] side = new int[n]; bool negative = false, positive = false;
            for (int i = 0; i < n; i++)
            {
                double d = line.Distance(part.Corners[i].Position);
                side[i] = d < -OnLine ? -1 : d > OnLine ? 1 : 0;
                negative |= side[i] < 0; positive |= side[i] > 0;
            }
            if (!negative || !positive) return [part];
            if (!part.Convex) return Fan(part).SelectMany(p => Both(p, line)).ToList();
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
                var next = i + 1 < n - 1 ? Diagonal(i + 1) : null;
                List<EdgeRef> edges =
                [
                    i == 1 ? part.Edges[0] : From(previous!, c[0].Position),
                    part.Edges[i],
                    next == null ? part.Edges[n - 1] : From(next, c[i + 1].Position),
                ];
                previous = next;
                yield return new() { Surface = part.Surface, Material = part.Material, Corners = [c[0], c[i], c[i + 1]], Edges = edges, State = part.State };
            }
        }
        private static float Area(IReadOnlyList<TerrainCorner> corners)
        {
            Vector3 sum = Vector3.Zero;
            for (int i = 1; i + 1 < corners.Count; i++) sum += Vector3.Cross(corners[i].Position - corners[0].Position, corners[i + 1].Position - corners[0].Position);
            return sum.Length() / 2;
        }
        private static TerrainCorner Mix(TerrainCorner a, TerrainCorner b, double s, Vector3 position)
        {
            float f = (float)Math.Clamp(s, 0, 1);
            var normal = Vector3.Lerp(a.Normal, b.Normal, f);
            return new(position, normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : normal, Vector2.Lerp(a.Uv, b.Uv, f));
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
                    var inside = edge.Segment.Points.Keys.Where(t => t > lo + 1e-12 && t < hi - 1e-12).ToList();
                    if (inside.Count == 0) continue;
                    if (edge.T0 > edge.T1) inside.Reverse();
                    var from = part.Corners[i]; var to = part.Corners[(i + 1) % part.Corners.Count];
                    List<TerrainCorner> added = []; List<EdgeRef> split = [];
                    double previous = edge.T0;
                    foreach (double t in inside)
                    {
                        double s = (t - edge.T0) / (edge.T1 - edge.T0);
                        added.Add(Mix(from, to, s, edge.Segment.Points[t]));
                        split.Add(new(edge.Segment, previous, t)); previous = t;
                    }
                    split.Add(new(edge.Segment, previous, edge.T1));
                    part.Corners.InsertRange(i + 1, added);
                    part.Edges.RemoveAt(i); part.Edges.InsertRange(i, split);
                }
            }
        }

        public List<TerrainPiece> Pieces(string label, TerrainRecipe recipe, IReadOnlyList<TerrainSurfaceGeometry> surfaces, IReadOnlyList<TerrainMaterialInfo> materials, TerrainGrid grid, List<string> warnings)
        {
            // Group parts by surface, cell and node attributes; with an automatic node zone, also by polygon zones.
            var groups = new SortedDictionary<(int Surface, int Row, int Column, uint Carried, int NodeZone, uint ZoneKey), List<Part>>();
            foreach (var part in parts)
            {
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
                foreach (var group in Divide(list))
                {
                    int index = perCell.TryGetValue((key.Surface, key.Row, key.Column), out int seen) ? seen + 1 : 0;
                    perCell[(key.Surface, key.Row, key.Column)] = index;
                    string cell = key.Column < 0 ? "out" : $"{key.Column:D2}x{key.Row:D2}";
                    string name = Name(label, surfaces[key.Surface].Surface.Id, cell, index);
                    var polygons = group.Select(p => new TerrainPolygonOutput(p.Material, Ordered(p.Corners), p.State.Zones?.Word ?? materials[p.Material].ZoneWord, p.State.Soil, p.State.Priority)).ToArray();
                    pieces.Add(new(name, key.Surface, key.Column, key.Row, carried, zone, polygons));
                }
            }
            return pieces;
        }

        /// <summary>Divides a group whose vertices or normals pass the budget at the median of its parts' centres, along its longer side.</summary>
        private IEnumerable<List<Part>> Divide(List<Part> group)
        {
            Stack<List<Part>> pending = new([group]);
            List<List<Part>> done = [];
            while (pending.TryPop(out var list))
            {
                token.ThrowIfCancellationRequested();
                if (list.Count <= 1 || Counts(list) <= VertexBudget) { done.Add(list); continue; }
                var centres = list.Select(p => p.Centroid()).ToArray();
                float spanX = centres.Max(c => c.X) - centres.Min(c => c.X), spanZ = centres.Max(c => c.Z) - centres.Min(c => c.Z);
                var order = Enumerable.Range(0, list.Count).OrderBy(i => spanX >= spanZ ? centres[i].X : centres[i].Z).ThenBy(i => spanX >= spanZ ? centres[i].Z : centres[i].X).ThenBy(i => i).ToArray();
                int half = order.Length / 2;
                // Pushed second half first, so the first half is finished (and named) first.
                pending.Push([.. order.Skip(half).Select(i => list[i])]);
                pending.Push([.. order.Take(half).Select(i => list[i])]);
            }
            return done;
        }
        /// <summary>The larger of the distinct vertex and normal counts a group would store.</summary>
        private static int Counts(List<Part> list)
        {
            HashSet<(long, long, long)> vertices = [], normals = [];
            foreach (var part in list)
                foreach (var c in part.Corners)
                {
                    vertices.Add(((long)Math.Round(c.Position.X * 1000.0), (long)Math.Round(c.Position.Y * 1000.0), (long)Math.Round(c.Position.Z * 1000.0)));
                    normals.Add(((long)Math.Round(c.Normal.X * 10000.0), (long)Math.Round(c.Normal.Y * 10000.0), (long)Math.Round(c.Normal.Z * 10000.0)));
                }
            return Math.Max(vertices.Count, normals.Count);
        }
        /// <summary>The corners starting where the first three are not on one line, which engine polygons need for their plane.</summary>
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
        private static string Name(string label, string surface, string cell, int index)
        {
            string suffix = $"_{cell}" + (index > 0 ? $"_{index}" : "");
            string head = $"{label}_{surface}";
            if (head.Length + suffix.Length > 34)
            {
                // A shortened head ends with a hash of the whole one, so surfaces whose ids begin alike keep apart.
                uint hash = 2166136261;
                foreach (char c in head) hash = (hash ^ c) * 16777619;
                head = $"{head[..(34 - suffix.Length - 5)]}_{hash & 0xFFFF:x4}";
            }
            return head + suffix;
        }
    }
}
