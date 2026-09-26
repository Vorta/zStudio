using System.Windows;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    /// <summary>Frame rendered geometry without changing source selection or animation time.</summary>
    public bool TryFrame(string target, int? node = null, bool manual = true)
    {
        if (target is not ("all" or "asset" or "selected")) throw new ArgumentException("Unknown framing target.", nameof(target));
        if (IsPickupDragging || IsFlyActive || viewport.Camera is not ProjectionCamera) return false;
        node ??= target == "selected" ? FramingSelection : null;
        HashSet<int>? selected = null;
        if (node is int root)
        {
            if (PreviewScene == null || root < 0 || root >= PreviewScene.Nodes.Count) return false;
            root = PickupAt(root)?.Root ?? root;
            selected = []; Stack<int> pending = new(); pending.Push(root);
            while (pending.TryPop(out int current))
            {
                if (current < 0 || current >= PreviewScene.Nodes.Count || !selected.Add(current)) continue;
                foreach (int child in SceneBuilder.Children(PreviewScene.Nodes[current])) pending.Push(child);
            }
        }
        bool assetOnly = animationFrame != null && node == null && target != "all";
        Rect3D bounds = Rect3D.Empty;
        if (!assetOnly)
            foreach (var mesh in meshes)
            {
                if (mesh.Visibility != Visibility.Visible || !mesh.IsRendering || !visiblePlacements.TryGetValue(mesh, out var items)) continue;
                for (int i = 0; i < items.Length; i++)
                {
                    if (IsHorizon(items[i].NodeIndex) || selected != null && !selected.Contains(items[i].NodeIndex)) continue;
                    var transform = ToWpf(mesh.Instances is { } instances && i < instances.Count ? instances[i] : items[i].Transform);
                    transform.Append(mesh.Transform?.Value ?? Matrix3D.Identity); Include(mesh, transform);
                }
            }
        if (animationFrame is { } frame)
            foreach (var pose in frame.Nodes)
            {
                if (IsHorizon(pose.SourceNode) || selected != null && !selected.Contains(pose.SourceNode) || !animatedMeshes.TryGetValue(pose.Id, out var items)) continue;
                foreach (var item in items)
                    if (item.Mesh.Visibility == Visibility.Visible && item.Mesh.IsRendering)
                        Include(item.Mesh, item.Mesh.Transform?.Value ?? Matrix3D.Identity);
            }
        if (bounds.IsEmpty) return false;
        if (manual && !BeginManualNavigation()) return false;
        StopCameraMotion();
        var view = CaptureView(); var forward = view.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, view.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        double halfWidth = 0, halfHeight = 0, halfDepth = 0;
        for (int i = 0; i < 8; i++)
        {
            var delta = new Point3D(bounds.X + ((i & 1) == 0 ? 0 : bounds.SizeX), bounds.Y + ((i & 2) == 0 ? 0 : bounds.SizeY), bounds.Z + ((i & 4) == 0 ? 0 : bounds.SizeZ)) - center;
            halfWidth = Math.Max(halfWidth, Math.Abs(Vector3D.DotProduct(delta, right)));
            halfHeight = Math.Max(halfHeight, Math.Abs(Vector3D.DotProduct(delta, up)));
            halfDepth = Math.Max(halfDepth, Math.Abs(Vector3D.DotProduct(delta, forward)));
        }
        double tangent = Math.Tan(view.FieldOfView * Math.PI / 360);
        double distance = Math.Max(1, halfDepth + Math.Max(halfHeight / tangent, halfWidth / (tangent * Aspect)) * 1.1);
        double width = Math.Max(.01, Math.Max(halfWidth, halfHeight * Aspect) * 2.2);
        RestoreView(view with { Position = center - forward * distance, LookDirection = forward * distance,
            OrthographicWidth = view.Projection == "orthographic" ? width : null });
        UpdateClipPlanes();
        return true;

        void Include(MeshGeometryModel3D mesh, Matrix3D transform)
        {
            if (mesh.Geometry is not MeshGeometry3D geometry || geometry.Positions == null) return;
            foreach (var p in geometry.Positions)
            {
                var point = transform.Transform(new Point3D(p.X, p.Y, p.Z));
                if (double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z)) bounds.Union(point);
            }
        }
    }
}
