using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;
using zStudio.Core;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private static Matrix3D SelectionPlacementTransform(MeshNode mesh, ScenePlacement[] items, int index)
    {
        var transform = ToWpf(mesh.Instances is { } instances && index < instances.Count ? instances[index] : items[index].Transform);
        transform.Append(ToWpf(mesh.Transform ?? Matrix4x4.Identity));
        return transform;
    }

    /// <summary>
    /// The bounds of a node's shown meshes (its own and those below it) along the axes of its parent, in which its local
    /// position is given: a copy placed beside it moves that position by the width. Null when nothing of it shows or its
    /// frame cannot be inverted.
    /// </summary>
    public (Vector3 Min, Vector3 Max)? BoundsInParentFrame(int node)
    {
        if (PreviewScene == null || node < 0 || node >= PreviewScene.Nodes.Count) return null;
        HashSet<int> nodes = []; Stack<int> pending = new([node]);
        while (pending.TryPop(out int index))
        {
            if (index < 0 || index >= PreviewScene.Nodes.Count || !nodes.Add(index)) continue;
            foreach (int child in SceneBuilder.Children(PreviewScene.Nodes[index])) pending.Push(child);
        }
        // The parent's frame is the node's own (as its selection box finds it) without its local transform.
        Matrix4x4 nodeLocal;
        try { nodeLocal = SceneBuilder.LocalTransform(PreviewScene.Nodes[node]); }
        catch (System.IO.InvalidDataException) { return null; }
        if (!Matrix4x4.Invert(nodeLocal, out var inverseLocal)) return null;
        var parentFrame = ToWpf(inverseLocal); parentFrame.Append(SelectionBoundsFrame(nodes, node));
        if (!UsableSelectionFrame(parentFrame)) return null;
        parentFrame.Invert();
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var (mesh, items) in visiblePlacements)
        {
            if (!mesh.IsRendering || mesh.Visibility != Visibility.Visible || !mesh.IsDepthClipEnabled ||
                mesh.Geometry is not { } geometry ||
                !staticMeshBounds.TryGetValue(geometry, out var local) || !Finite(local.Min) || !Finite(local.Max)) continue;
            for (int i = 0; i < items.Length; i++)
            {
                if (!nodes.Contains(items[i].NodeIndex)) continue;
                var transform = SelectionPlacementTransform(mesh, items, i);
                transform.Append(parentFrame);
                for (int c = 0; c < 8; c++)
                {
                    var point = transform.Transform(new Point3D((c & 1) == 0 ? local.Min.X : local.Max.X,
                        (c & 2) == 0 ? local.Min.Y : local.Max.Y, (c & 4) == 0 ? local.Min.Z : local.Max.Z));
                    Vector3 v = new((float)point.X, (float)point.Y, (float)point.Z);
                    min = Vector3.Min(min, v); max = Vector3.Max(max, v);
                }
            }
        }
        return Finite(min) && Finite(max) ? (min, max) : null;
    }

    /// <summary>The selection's frame, or with <paramref name="ownerNode"/> that node's frame, as rendered.</summary>
    private Matrix3D SelectionBoundsFrame(HashSet<int> selectedNodes, int? ownerNode = null)
    {
        if (PreviewScene == null || ownerNode == null && SelectedInspection is not { Node: >= 0 } || selectedNodes.Count == 0)
            return Matrix3D.Identity; // AI markers deliberately retain their world-aligned box.
        int owner;
        if (ownerNode is int given) owner = given;
        else
        {
            owner = SelectedInspection!.SelectionNode;
            owner = owner >= 0 ? ActorAt(owner)?.Root ?? owner : CommonSelectionAncestor(selectedNodes);
        }
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
                rendererFrame ??= ToWpf(mesh.Transform ?? Matrix4x4.Identity);
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
