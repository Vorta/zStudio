using System.Numerics;
using Recoil.Zbd.Core.Gltf;

namespace Recoil.Zbd.Core.Sources;

/// <summary>Bounded plan-view overlap tests on authored triangles, without clipping-grid quantization or polygon expansion.</summary>
internal sealed class TerrainConversionGeometry(CancellationToken token, int maximumTriangles = 200_000, long maximumWork = 8_000_000)
{
    private long triangles, work;
    internal readonly record struct Box(double Left, double Bottom, double Right, double Top)
    {
        public bool Overlaps(Box other) => Left < other.Right && other.Left < Right && Bottom < other.Top && other.Bottom < Top;
    }
    internal readonly record struct Triangle(Vector2 A, Vector2 B, Vector2 C)
    {
        public Box Bounds => new(Math.Min(A.X, Math.Min(B.X, C.X)), Math.Min(A.Y, Math.Min(B.Y, C.Y)), Math.Max(A.X, Math.Max(B.X, C.X)), Math.Max(A.Y, Math.Max(B.Y, C.Y)));
    }
    internal sealed record Sheet(Triangle[] Triangles, Box Bounds);
    private void Spend()
    {
        token.ThrowIfCancellationRequested();
        if (++work > maximumWork) throw new InvalidDataException("Terrain conversion exceeds its geometry comparison budget; convert a smaller database.");
    }
    internal Sheet Read(GltfMesh mesh)
    {
        // Charge all indices before allocating a triangle list, including degenerate triangles.
        foreach (var primitive in mesh.Primitives)
        {
            token.ThrowIfCancellationRequested();
            if ((triangles += primitive.Indices.Count / 3) > maximumTriangles)
                throw new InvalidDataException("Terrain conversion exceeds its triangle budget; convert a smaller database.");
        }
        List<Triangle> projected = [];
        foreach (var primitive in mesh.Primitives)
            for (int i = 0; i < primitive.Indices.Count; i += 3)
            {
                Spend();
                Vector2 Point(int at) { var p = primitive.Positions[primitive.Indices[at]]; return new(p.X, p.Z); }
                var triangle = new Triangle(Point(i), Point(i + 1), Point(i + 2));
                if (Cross(triangle.A, triangle.B, triangle.C) != 0) projected.Add(triangle);
            }
        var values = projected.ToArray();
        Array.Sort(values, (a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
        token.ThrowIfCancellationRequested();
        Box bounds = values.Length == 0 ? default : new(values.Min(t => t.Bounds.Left), values.Min(t => t.Bounds.Bottom), values.Max(t => t.Bounds.Right), values.Max(t => t.Bounds.Top));
        return new(values, bounds);
    }
    internal bool Overlaps(Sheet a, Sheet b)
    {
        Spend();
        if (!a.Bounds.Overlaps(b.Bounds)) return false;
        int first = 0;
        foreach (var left in a.Triangles)
        {
            Spend(); var bounds = left.Bounds;
            while (first < b.Triangles.Length && b.Triangles[first].Bounds.Right <= bounds.Left) { Spend(); first++; }
            for (int i = first; i < b.Triangles.Length; i++)
            {
                Spend(); var right = b.Triangles[i];
                if (right.Bounds.Left >= bounds.Right) break;
                if (bounds.Overlaps(right.Bounds) && PositiveOverlap(left, right)) return true;
            }
        }
        return false;
    }
    private static double Cross(Vector2 a, Vector2 b, Vector2 p) => ((double)b.X - a.X) * ((double)p.Y - a.Y) - ((double)b.Y - a.Y) * ((double)p.X - a.X);
    private static bool PositiveOverlap(Triangle left, Triangle right)
    {
        Span<Vector2> a = stackalloc Vector2[] { left.A, left.B, left.C };
        Span<Vector2> b = stackalloc Vector2[] { right.A, right.B, right.C };
        // Separating axes of both triangles: equality is an edge/point contact, not positive area.
        for (int side = 0; side < 2; side++)
            for (int i = 0; i < 3; i++)
            {
                var points = side == 0 ? a : b; var start = points[i]; var end = points[(i + 1) % 3];
                double a0 = Cross(start, end, a[0]), a1 = Cross(start, end, a[1]), a2 = Cross(start, end, a[2]);
                double b0 = Cross(start, end, b[0]), b1 = Cross(start, end, b[1]), b2 = Cross(start, end, b[2]);
                if (Math.Max(a0, Math.Max(a1, a2)) <= Math.Min(b0, Math.Min(b1, b2)) || Math.Max(b0, Math.Max(b1, b2)) <= Math.Min(a0, Math.Min(a1, a2))) return false;
            }
        return true;
    }
}
