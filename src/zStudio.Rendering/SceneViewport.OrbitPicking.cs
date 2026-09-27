using System.Windows;
using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using HitTestResult = HelixToolkit.SharpDX.HitTestResult;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    public bool IsOrbitPickingReady => viewport.RenderContext != null && viewport.ActualWidth > 0 && viewport.ActualHeight > 0;
    public bool IsNavigationPointInside(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y)
        && point.X >= 0 && point.Y >= 0 && point.X < viewport.ActualWidth && point.Y < viewport.ActualHeight;

    /// <summary>Retain the nearest rendered scene surface as a world-space orbit pivot.
    /// Uses geometric material picking, including transparent scene triangles.</summary>
    public bool PickOrbitPivot(Point point)
    {
        return !IsFlyActive && !IsPickupDragging && TryNavigationSurface(point, out var hit) && SetOrbitPivot(hit);
    }

    /// <summary>Query a scene surface without changing the camera, selection or navigation references.</summary>
    internal bool TryNavigationSurface(Point point, out Point3D surface)
    {
        surface = default;
        if (!IsOrbitPickingReady || !IsNavigationPointInside(point)) return false;
        if (viewport.Camera is not ProjectionCamera camera) return false;
        // FindHits/UnProject uses the previous render context's camera matrices.
        // Construct the ray from the current camera, then use the renderer's
        // triangle/instance hit tests, without forcing a render or controller tick.
        var projection = camera.CreateProjectionMatrix(Aspect);
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double x = (2 * point.X / viewport.ActualWidth - 1) / projection.M11;
        double y = (1 - 2 * point.Y / viewport.ActualHeight) / projection.M22;
        var origin = camera.Position;
        var direction = forward;
        if (camera is OrthographicCamera) origin += right * x + up * y;
        else { direction += right * x + up * y; direction.Normalize(); }
        // Adaptive clipping is finalized on the next frame. Starting at the eye
        // keeps immediate/substep queries independent of the previous near plane.
        var ray = new HelixToolkit.Maths.Ray(new Vector3((float)origin.X, (float)origin.Y, (float)origin.Z),
            new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z));
        var context = new HitTestContext(viewport.RenderContext, ray, new Vector2((float)point.X, (float)point.Y));
        List<HitTestResult> hits = [];
        foreach (var element in viewport.Items) element.HitTest(context, ref hits);
        foreach (var hit in hits.OrderBy(h => h.Distance))
        {
            if (!hit.IsValid || hit.ModelHit is not MeshGeometryModel3D mesh || !IsOrbitSurface(mesh, hit.Tag)) continue;
            var candidate = new Point3D(hit.PointHit.X, hit.PointHit.Y, hit.PointHit.Z);
            if (!double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y) || !double.IsFinite(candidate.Z)
                || Vector3D.DotProduct(candidate - camera.Position, forward) < 1e-6) continue;
            surface = candidate; return true;
        }
        return false;
    }

    private bool IsOrbitSurface(MeshGeometryModel3D mesh, object? instance)
    {
        if (!mesh.IsRendering || !mesh.IsHitTestVisible || mesh.Visibility != Visibility.Visible || !mesh.IsDepthClipEnabled) return false;
        if (visiblePlacements.TryGetValue(mesh, out var items))
            return instance is int index && index >= 0 && index < items.Length && !IsHorizon(items[index].NodeIndex);
        return animatedMeshes.Values.Any(items => items.Any(item => ReferenceEquals(item.Mesh, mesh)));
    }

    internal bool SetOrbitPivot(Point3D point)
    {
        if (viewport.Camera is not ProjectionCamera camera || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z)) return false;
        var forward = camera.LookDirection; forward.Normalize();
        double depth = Vector3D.DotProduct(point - camera.Position, forward);
        if (!double.IsFinite(depth) || depth < 1e-6) return false;
        // Picking establishes an orbit target, not a new zoom speed. Preserve the
        // initial fallback too when no zoom/framing operation has established it.
        navigationReferenceDistance ??= camera.LookDirection.Length;
        orbitPivot = point;
        camera.LookDirection = forward * depth;
        return true;
    }
}
