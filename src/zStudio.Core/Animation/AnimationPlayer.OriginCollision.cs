using System.Numerics;

namespace zStudio.Core.Animation;

public sealed partial class AnimationPlayer
{
    private sealed record OriginSurface(Matrix4x4 Transform, float Morph, AnimationTerrain Geometry);
    private readonly Dictionary<int, OriginSurface> originTerrain = [];
    private static readonly HashSet<int> NoExcludedSurfaces = [];

    // Runtime geometry is derived, never checkpoint state. Compare world transforms and morphs
    // before reusing it, including ancestor changes between two events in the same tick.
    private float? OriginFloor(Instance instance, Node node)
    {
        Vector3 point = World(instance, node).Translation + Vector3.UnitY * PreviewHeight;
        var excluded = new HashSet<int>(sharedNodes.Keys);
        var assembly = bindingOperation!.Descendants(instance.Root, context.Scene.Nodes.Count).ToHashSet();
        excluded.UnionWith(assembly);
        float? height = Terrain!.Height(point, -10000, excluded, ReserveGround);
        var ancestry = BeginFrameAncestry(bindingOperation);
        var owner = instances.FirstOrDefault(i => i.Shared) ?? new Instance { Entry = instance.Entry, Shared = true };
        foreach (var (index, part) in sharedNodes.ToArray())
        {
            ReserveGround(1);
            if (assembly.Contains(index) || context.Scene.Nodes[index].ModelIndex is not (>= 0 and int model) ||
                (context.Scene.Nodes[index].Metadata.UInt("flags") & 8) == 0) continue;
            var pose = ancestry.Get(owner, part, 0);
            if (!pose.Visible) continue;
            var world = pose.World;
            world.Translation += Vector3.UnitY * PreviewHeight;
            if (!originTerrain.TryGetValue(index, out var cached) || cached.Transform != world || cached.Morph != part.Morph)
            {
                long proposed = context.Scene.Models[model].Polygons.Sum(p => Math.Max(0L, p.Vertices.LongLength - 2));
                long retained = originTerrain.Sum(p => (long)p.Value.Geometry.TriangleCount) - (cached?.Geometry.TriangleCount ?? 0);
                if (proposed + retained > 1_000_000) throw new InvalidDataException("Animated collision geometry exceeds its triangle allowance.");
                var geometry = new AnimationTerrain(context.Scene, index, world, part.Morph, ReserveGround);
                originTerrain[index] = cached = new(world, part.Morph, geometry);
            }
            if (cached.Geometry.Height(point, -10000, NoExcludedSurfaces, ReserveGround) is float y && (height == null || y > height)) height = y;
        }
        EndFrameAncestry(ancestry);
        return height;
    }

    private bool MoveWithOriginCollision(Instance instance, Node node, AnimationEvent ev, float dt, out bool contact)
    {
        Vector3 velocity = ev.Vector(88), delta = velocity * dt;
        Vector3 point = World(instance, node).Translation + Vector3.UnitY * PreviewHeight;
        contact = false; bool timeout = false;
        if (delta.Y < 0)
        {
            if (OriginFloor(instance, node) is float floor)
            {
                if (point.Y + delta.Y < floor)
                {
                    contact = true;
                    delta.Y = floor - point.Y;
                    if (Math.Abs(velocity.X) >= .100000001f || Math.Abs(velocity.Z) >= .100000001f)
                        delta.Y += Math.Abs(velocity.Y * dt * .5f);
                }
            }
            else if ((ev.U32(12) & 0x400) == 0)
            {
                int offset = ev.Version == 39 ? 328 : 248;
                ev.SetFloat(offset, ev.F32(offset) + dt);
                timeout = ev.F32(offset) > 15;
            }
        }
        Position(node, delta, true);
        return (contact || timeout) && (GroundImpact(instance, node, ev) || timeout);
    }
}
