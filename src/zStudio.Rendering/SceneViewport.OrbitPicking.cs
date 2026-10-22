using System.Windows;
using System.Windows.Media.Media3D;
using zStudio.Rendering.Direct3D;
using Vector3 = System.Numerics.Vector3;

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
        if (!IsOrbitPickingReady || view.Camera is not RenderCamera camera
            || !TryNavigationRay(point, out var origin, out var direction)) return false;
        // A navigation query can precede the next render update. Facades must
        // face this camera as well as the ray; simulation poses remain unchanged.
        if (cameraPoseDirty) RefreshAnimationCamera();
        var forward = camera.LookDirection; forward.Normalize();
        // Adaptive clipping is finalized on the next frame. Starting at the eye
        // keeps immediate/substep queries independent of the previous near plane.
        var hits = view.HitTest(new Vector3((float)origin.X, (float)origin.Y, (float)origin.Z),
            new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z), point);
        foreach (var hit in hits.OrderBy(h => h.Distance))
        {
            if (hit.Node is not MeshNode mesh || !IsOrbitSurface(mesh, hit.Tag)) continue;
            var candidate = new Point3D(hit.Point.X, hit.Point.Y, hit.Point.Z);
            if (!double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y) || !double.IsFinite(candidate.Z)
                || Vector3D.DotProduct(candidate - camera.Position, forward) < 1e-6 || accept?.Invoke(hit) == false) continue;
            surface = hit; return true;
        }
        return false;
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
        if (!double.IsFinite(depth) || depth < 1e-6) return false;
        // Picking establishes an orbit target, not a new zoom speed. Preserve the
        // initial fallback too when no zoom/framing operation has established it.
        navigationReferenceDistance ??= camera.LookDirection.Length;
        orbitPivot = point;
        camera.LookDirection = forward * depth;
        return true;
    }
}
