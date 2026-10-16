using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf.SharpDX;
using zStudio.Core;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private static Matrix3D SelectionPlacementTransform(MeshGeometryModel3D mesh, ScenePlacement[] items, int index)
    {
        var transform = ToWpf(mesh.Instances is { } instances && index < instances.Count ? instances[index] : items[index].Transform);
        transform.Append(mesh.Transform?.Value ?? Matrix3D.Identity);
        return transform;
    }

    private Matrix3D SelectionBoundsFrame(HashSet<int> selectedNodes)
    {
        if (PreviewScene == null || SelectedInspection is not { Node: >= 0 } selected || selectedNodes.Count == 0)
            return Matrix3D.Identity; // AI markers deliberately retain their world-aligned box.
        int owner = selected.SelectionNode;
        if (owner >= 0) owner = ActorAt(owner)?.Root ?? owner;
        else owner = CommonSelectionAncestor(selectedNodes);
        if (owner < 0 || owner >= PreviewScene.Nodes.Count) return Matrix3D.Identity;

        // Recover the owner's effective frame from a rendered descendant, rather
        // than its stored world matrix. This includes initialized mission poses,
        // current placement drafts and the renderer's final mesh transform.
        Matrix3D? rendererFrame = null;
        foreach (var (mesh, items) in visiblePlacements)
        {
            if (!mesh.IsRendering || mesh.Visibility != Visibility.Visible || !mesh.IsDepthClipEnabled) continue;
            for (int i = 0; i < items.Length; i++)
            {
                int current = items[i].NodeIndex;
                if (!selectedNodes.Contains(current)) continue;
                rendererFrame ??= mesh.Transform?.Value ?? Matrix3D.Identity;
                Matrix4x4 local = Matrix4x4.Identity; HashSet<int> seen = [];
                while (current != owner && current >= 0 && current < PreviewScene.Nodes.Count && seen.Add(current))
                {
                    var node = PreviewScene.Nodes[current];
                    local *= SceneBuilder.LocalTransform(node);
                    current = node.Parents.Length == 1 ? node.Parents[0] : -1;
                }
                if (current != owner || !Matrix4x4.Invert(local, out var inverseLocal)) continue;
                var frame = ToWpf(inverseLocal); frame.Append(SelectionPlacementTransform(mesh, items, i));
                if (UsableSelectionFrame(frame)) return frame;
            }
        }
        // A zero-scale descendant cannot be inverted, but its owner may still
        // have a usable frame. Use the same placement adjustment as normal Bounds.
        Matrix4x4 stored = Matrix4x4.Identity; HashSet<int> ancestors = []; int parent = owner;
        while (parent >= 0 && parent < PreviewScene.Nodes.Count && ancestors.Add(parent))
        {
            var node = PreviewScene.Nodes[parent]; stored *= SceneBuilder.LocalTransform(node);
            if (node.Parents.Length > 1) return Matrix3D.Identity;
            parent = node.Parents.FirstOrDefault(-1);
        }
        if (parent < 0)
        {
            var frame = ToWpf(AdjustPickupPlacement(new(owner, -1, "", stored)).Transform);
            frame.Append(rendererFrame ?? Matrix3D.Identity);
            if (UsableSelectionFrame(frame)) return frame;
        }
        // A singular/ambiguous owner has no usable object coordinate frame.
        return Matrix3D.Identity;
    }

    private int CommonSelectionAncestor(HashSet<int> selectedNodes)
    {
        if (PreviewScene == null) return -1;
        List<int>? common = null;
        foreach (int index in selectedNodes)
        {
            // Descendants do not constrain the common frame beyond their root.
            var parents = PreviewScene.Nodes[index].Parents;
            if (parents.Length == 1 && selectedNodes.Contains(parents[0])) continue;
            List<int> chain = []; HashSet<int> seen = []; int current = index;
            while (current >= 0 && current < PreviewScene.Nodes.Count && seen.Add(current))
            {
                chain.Add(current);
                var node = PreviewScene.Nodes[current];
                if (node.Parents.Length > 1) return -1;
                current = node.Parents.FirstOrDefault(-1);
            }
            if (current >= 0) return -1;
            if (common == null) common = chain;
            else { common.RemoveAll(candidate => !seen.Contains(candidate)); if (common.Count == 0) return -1; }
        }
        return common?.FirstOrDefault(-1) ?? -1;
    }

    private static bool UsableSelectionFrame(Matrix3D frame)
    {
        if (!frame.HasInverse) return false;
        var inverse = frame; inverse.Invert();
        return FiniteMatrix(frame) && FiniteMatrix(inverse);

        static bool FiniteMatrix(Matrix3D m) =>
            double.IsFinite(m.M11) && double.IsFinite(m.M12) && double.IsFinite(m.M13) && double.IsFinite(m.M14) &&
            double.IsFinite(m.M21) && double.IsFinite(m.M22) && double.IsFinite(m.M23) && double.IsFinite(m.M24) &&
            double.IsFinite(m.M31) && double.IsFinite(m.M32) && double.IsFinite(m.M33) && double.IsFinite(m.M34) &&
            double.IsFinite(m.OffsetX) && double.IsFinite(m.OffsetY) && double.IsFinite(m.OffsetZ) && double.IsFinite(m.M44);
    }
}
