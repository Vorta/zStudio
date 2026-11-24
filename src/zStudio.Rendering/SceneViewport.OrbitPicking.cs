using System.Windows;
using System.Windows.Media.Media3D;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    public bool IsOrbitPickingReady => view.IsReady && viewport.ActualWidth > 0 && viewport.ActualHeight > 0;
    public bool IsNavigationPointInside(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y)
        && point.X >= 0 && point.Y >= 0 && point.X < viewport.ActualWidth && point.Y < viewport.ActualHeight;

    /// <summary>Retain the nearest rendered scene surface as a world-space orbit pivot.
    /// Uses geometric material picking, including transparent scene triangles.</summary>
    public bool PickOrbitPivot(Point point)
    {
        return !IsFlyActive && !IsPickupDragging && !IsAiPathPointerTransform && !IsParachutePointerMoving && TryNavigationSurface(point, out var hit) && SetOrbitPivot(hit);
    }

    /// <summary>Query a scene surface without changing the camera, selection or navigation references.</summary>
    internal bool TryNavigationSurface(Point point, out Point3D surface)
    {
        surface = default;
        if (!TrySceneSurface(point, out var hit)) return false;
        surface = new(hit!.Point.X, hit.Point.Y, hit.Point.Z); return true;
    }
    /// <param name="accept">Skips hits it declines (the polygon zone tool looks through what it cannot paint).</param>
    private bool TrySceneSurface(Point point, out RenderHit? surface, Func<RenderHit, bool>? accept = null)
    {
        surface = null;
        if (!IsOrbitPickingReady || !IsNavigationPointInside(point) || view.Camera is not RenderCamera camera) return false;
        // A navigation query can precede the next render update. Facades must
        // face this camera as well as the ray; simulation poses remain unchanged.
        if (cameraPoseDirty) RefreshAnimationCamera();
        var forward = camera.LookDirection; forward.Normalize();
        foreach (var hit in SceneHits(point))
        {
            if (hit.Node is not MeshNode mesh || !IsOrbitSurface(mesh, hit.Tag)) continue;
            var candidate = new Point3D(hit.Point.X, hit.Point.Y, hit.Point.Z);
            if (camera is PerspectiveRenderCamera && Vector3D.DotProduct(candidate - camera.Position, forward) < 1e-6) continue;
            if (accept?.Invoke(hit) == false) continue;
            surface = hit; return true;
        }
        return false;
    }

    private IEnumerable<RenderHit> SceneHits(Point point, bool rendererCoordinates = false)
    {
        if (view.Camera is not RenderCamera camera) return [];
        Point3D origin;
        Vector3D direction;
        if (rendererCoordinates)
        {
            var ray = view.UnProject(point);
            origin = new(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
            direction = new(ray.Direction.X, ray.Direction.Y, ray.Direction.Z);
        }
        else if (!TryNavigationRay(point, out origin, out direction)) return [];
        if (camera is OrthographicRenderCamera)
        {
            // Use current pose and bounds even when an input arrives before that pose's first frame.
            double start = Math.Min(0, camera.NearPlaneDistance);
            if (Finite(sceneMin) && Finite(sceneMax))
                start = Math.Min(start, NearestBoundsDepth(camera.Position, direction) - Math.Max(minimumClipDistance * 8, OrthographicDepthRoundoff(camera.Position)));
            origin += direction * start;
        }
        var hits = view.HitTest(RenderCamera.ToFloat(origin), RenderCamera.ToFloat(direction));
        return hits.Where(hit => Finite(hit.Point)).OrderBy(hit => hit.Distance);
    }

    /// <summary>Current-camera ray, available before rendering and over empty space.</summary>
    internal bool TryNavigationRay(Point point, out Point3D origin, out Vector3D direction)
        => TryCameraRay(point, out origin, out direction, true);

    private bool TryCameraRay(Point point, out Point3D origin, out Vector3D direction, bool requireInside)
    {
        origin = default; direction = default;
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0 ||
            requireInside && !IsNavigationPointInside(point) || view.Camera is not RenderCamera camera) return false;
        // The renderer's own pointer rays use the previous frame's camera matrices.
        // Use the current pose without forcing a render or controller tick.
        var projection = camera.CreateProjectionMatrix(Aspect);
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double x = (2 * point.X / viewport.ActualWidth - 1) / projection.M11;
        double y = (1 - 2 * point.Y / viewport.ActualHeight) / projection.M22;
        origin = camera.Position;
        direction = forward;
        if (camera is OrthographicRenderCamera) origin += right * x + up * y;
        else { direction += right * x + up * y; direction.Normalize(); }
        return double.IsFinite(origin.X) && double.IsFinite(origin.Y) && double.IsFinite(origin.Z)
            && double.IsFinite(direction.X) && double.IsFinite(direction.Y) && double.IsFinite(direction.Z);
    }

    private bool IsOrbitSurface(MeshNode mesh, int? instance)
    {
        if (!mesh.IsRendering || !mesh.IsHitTestVisible || mesh.Visibility != Visibility.Visible || !mesh.IsDepthClipEnabled) return false;
        if (visiblePlacements.TryGetValue(mesh, out var items))
            return instance is int index && index >= 0 && index < items.Length && !IsHorizon(items[index].NodeIndex);
        return inspectionMeshes.TryGetValue(mesh, out var item) && item.Runtime != null && !IsHorizon(item.Node);
    }

    internal bool SetOrbitPivot(Point3D point)
    {
        if (view.Camera is not RenderCamera camera || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z)) return false;
        var forward = camera.LookDirection; forward.Normalize();
        double depth = Vector3D.DotProduct(point - camera.Position, forward);
        if (!double.IsFinite(depth) || camera is PerspectiveRenderCamera && depth < 1e-6) return false;
        // Picking establishes an orbit target, not a new zoom speed. Preserve the
        // initial fallback too when no zoom/framing operation has established it.
        navigationReferenceDistance ??= camera.LookDirection.Length;
        orbitPivot = point;
        if (depth >= 1e-6) camera.LookDirection = forward * depth;
        return true;
    }
}
