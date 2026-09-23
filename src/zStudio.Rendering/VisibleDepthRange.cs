using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Media.Media3D;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;

namespace Recoil.Zbd.Rendering;

/// <summary>Conservative depth bounds of geometry intersecting the camera's view.
/// A world AABB containing the camera cannot provide a useful near plane.</summary>
internal sealed class VisibleDepthRange
{
    private static readonly ConditionalWeakTable<MeshGeometry3D, Bounds> bounds = new();
    private readonly Point3D eye;
    private readonly Vector3D forward, right, up;
    private readonly double horizontal, vertical, floor;
    public double Near { get; private set; } = double.PositiveInfinity;
    public double Far { get; private set; } = double.NegativeInfinity;

    public VisibleDepthRange(Point3D eye, Vector3D look, Vector3D cameraUp, double fieldOfView, double aspect, double floor)
    {
        this.eye = eye; forward = look; forward.Normalize();
        right = Vector3D.CrossProduct(forward, cameraUp); right.Normalize();
        up = Vector3D.CrossProduct(right, forward);
        // A small guard band prevents projection changes as a surface grazes an edge.
        vertical = Math.Tan(fieldOfView * Math.PI / 360) * 1.05;
        horizontal = vertical * aspect; this.floor = floor;
    }

    public void Include(MeshGeometry3D mesh, Matrix3D transform)
    {
        if (mesh.Positions is not { Count: > 0 } positions || mesh.Indices is not { Count: > 0 } indices) return;
        var box = bounds.GetValue(mesh, static m => new(m));
        Span<Vector3D> corners = stackalloc Vector3D[8];
        for (int i = 0; i < 8; i++) corners[i] = Camera(new((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z), transform);
        bool contained = true;
        for (int plane = 0; plane < 5; plane++)
        {
            int inside = 0;
            foreach (var corner in corners) if (Distance(corner, plane) >= 0) inside++;
            if (inside == 0) return;
            if (inside != 8) contained = false;
        }
        if (contained) { foreach (var p in corners) Include(p.Z); return; }

        // Only intersecting bounds need triangle refinement. This matters for large
        // terrain models: their box may surround the camera while their surface doesn't.
        Span<Vector3D> a = stackalloc Vector3D[12]; Span<Vector3D> b = stackalloc Vector3D[12];
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            a[0] = Camera(positions[indices[i]], transform); a[1] = Camera(positions[indices[i + 1]], transform); a[2] = Camera(positions[indices[i + 2]], transform);
            int count = 3;
            for (int plane = 0; plane < 5 && count > 0; plane++)
            {
                int output = 0; var previous = a[count - 1]; double pd = Distance(previous, plane);
                for (int j = 0; j < count; j++)
                {
                    var current = a[j]; double cd = Distance(current, plane);
                    if ((pd >= 0) != (cd >= 0)) b[output++] = previous + (current - previous) * (pd / (pd - cd));
                    if (cd >= 0) b[output++] = current;
                    previous = current; pd = cd;
                }
                var swap = a; a = b; b = swap; count = output;
            }
            for (int j = 0; j < count; j++) Include(a[j].Z);
        }
    }
    private Vector3D Camera(Vector3 p, Matrix3D transform)
    {
        // Keep camera subtraction and projection in double precision at world coordinates.
        var v = transform.Transform(new Point3D(p.X, p.Y, p.Z)) - eye;
        return new(Vector3D.DotProduct(v, right), Vector3D.DotProduct(v, up), Vector3D.DotProduct(v, forward));
    }
    private double Distance(Vector3D p, int plane) => plane switch
    { 0 => p.Z * horizontal + p.X, 1 => p.Z * horizontal - p.X, 2 => p.Z * vertical + p.Y, 3 => p.Z * vertical - p.Y, _ => p.Z - floor };
    private void Include(double depth) { Near = Math.Min(Near, depth); Far = Math.Max(Far, depth); }
    private sealed class Bounds
    {
        public Vector3 Min { get; } = new(float.PositiveInfinity);
        public Vector3 Max { get; } = new(float.NegativeInfinity);
        public Bounds(MeshGeometry3D geometry)
        { foreach (var p in geometry.Positions!) { Min = Vector3.Min(Min, p); Max = Vector3.Max(Max, p); } }
    }
}
