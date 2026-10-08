using System.Numerics;

namespace Recoil.Zbd.Core.Animation;

public sealed partial class AnimationPlayer
{
    // A frame does not execute events. Its cache is deliberately discarded before another frame:
    // parent changes, LOD selection, alpha overrides, placement and cleanup are mutable runtime state.
    private sealed class FrameAncestry(AnimationPlayer player, AnimationBindingOperation work)
    {
        private readonly Dictionary<(Instance, Node), Link> links = [];
        private readonly Dictionary<(Instance, Node), Pose> poses = [];
        private readonly HashSet<int> seen = [];
        internal readonly record struct Pose(Matrix4x4 World, bool Visible, float Opacity);
        private readonly record struct Link(Matrix4x4 Local, Node? Parent);

        private Link GetLink(Instance instance, Node node)
        {
            work.Reserve(1);
            var key = (instance, node);
            if (links.TryGetValue(key, out var link)) return link;
            work.Reserve(160);
            link = new(node.Local, player.ParentNode(instance, node.Parent));
            links.Add(key, link);
            return link;
        }

        internal Pose Get(Instance instance, Node node)
        {
            work.Reserve(1);
            var key = (instance, node);
            if (poses.TryGetValue(key, out var known)) return known;
            // Clear is proportional to retained capacity, not the number of current ancestors.
            work.Reserve(seen.EnsureCapacity(0));
            seen.Clear();
            work.Reserve(64);
            seen.Add(node.Source);
            var link = GetLink(instance, node);
            Matrix4x4 world = link.Local;
            bool visible = node.Active && !node.PendingPlacement;
            bool alphaFound = node.AlphaEnabled;
            float opacity = alphaFound ? Math.Clamp(node.Alpha, 0, 1) : 1;
            Node child = node;
            while (link.Parent is { } parent)
            {
                work.Reserve(64);
                if (!seen.Add(parent.Source)) break;
                link = GetLink(instance, parent);
                // Keep the existing child-to-root, left-associated arithmetic exactly. Multiplying
                // by a cached parent's world matrix would change floating-point rounding.
                world *= link.Local;
                if (visible && (!parent.Active || parent.PendingPlacement || !player.context.Lods.Includes(parent.Source, child.Source, player.LodLevel))) visible = false;
                if (!alphaFound && parent.AlphaEnabled) { opacity = Math.Clamp(parent.Alpha, 0, 1); alphaFound = true; }
                child = parent;
            }
            work.Reserve(128);
            Pose pose = new(world, visible, opacity);
            poses.Add(key, pose);
            return pose;
        }
    }
}
