using System.Numerics;

namespace Recoil.Zbd.Core.Animation;

public sealed partial class AnimationPlayer
{
    // These caches contain source identities/indices only, never runtime poses.
    // They can survive Reset/seek; every height uses the current checkpoint pose.
    private readonly Dictionary<int, int[]> groundVertices = [];
    private readonly Dictionary<(AnimationEntry Entry, int Root, int Node), int[]> groundNodes = [];

    private float PrepareGround(Instance instance, Node node)
    {
        float bottom = GroundBottom(instance, node);
        if (bottom < 0) LiftFromGround(instance, node, -bottom);
        return Math.Max(0, bottom);
    }

    private bool ResolveGround(Instance instance, Node node, AnimationEvent ev, float previousBottom)
    {
        float bottom = GroundBottom(instance, node);
        if (bottom >= 0) return false;
        LiftFromGround(instance, node, -bottom);
        if (bottom >= previousBottom) return false;
        node.GroundSupported = true;

        // Retail zEffect::HandleNodeAnimEvent 0x45A4DB–0x45A6C0.
        // The preview substitutes mesh support for the engine's origin ray pick.
        Vector3 velocity = ev.Vector(88), acceleration = ev.Vector(100);
        float speedSquared = velocity.LengthSquared(), accelerationSquared = acceleration.LengthSquared();
        if (!float.IsFinite(speedSquared) || !float.IsFinite(accelerationSquared))
            throw new InvalidDataException("Ground contact has nonfinite velocity or acceleration.");
        bool settled = speedSquared < Math.Max(accelerationSquared, 1e-8f);
        if (!settled) ev.SetVector(88, velocity * .199999988f);

        uint flags = ev.U32(12);
        if ((flags & 0x800) != 0)
        {
            int index = ev.I16(240);
            var target = index >= 0 && index < instance.Sequences.Count ? instance.Sequences[index]
                : instance.Sequences.FirstOrDefault(s => s.Data.Name == ev.Text(208));
            if (target == null) { unavailableDuration = true; AddNote($"Ground impact: unresolved release sequence '{ev.Text(208)}' (index {index})."); }
            else if (target.State == 3) target.State = 0;
        }
        if ((flags & 0x1000) != 0 && ev.I16(242) > 0)
        {
            int sample = ev.I16(242);
            if (sample >= instance.Entry.References[4].Count)
            { unavailableDuration = true; AddNote($"Ground impact: unresolved sample reference {sample}."); }
            else
            {
                float threshold = ev.F32(244) < 0 ? ev.F32(24) * 10 : ev.F32(244);
                float speed = ev.Vector(88).Length();
                if (!float.IsFinite(threshold)) throw new InvalidDataException("Ground impact sound has a nonfinite gain threshold.");
                float gain = threshold <= 0 || speed >= threshold ? 1 : speed / threshold;
                PlaySound(instance, ReferenceName(instance, 4, sample), false, false, 0, World(instance, node).Translation, gain);
            }
        }
        return settled;
    }

    private void ConstrainSettledGround()
    {
        if (!GroundPlaneEnabled) return;
        HashSet<Node> corrected = [];
        foreach (var instance in instances)
            foreach (var node in instance.Nodes.Values.Where(n => n.GroundSupported && n.Active && !n.PendingPlacement))
            {
                if (!corrected.Add(node)) continue;
                try { PrepareGround(instance, node); }
                catch (InvalidDataException ex)
                {
                    node.GroundSupported = false; unavailableDuration = true;
                    AddNote($"Ground support for node #{node.Source}: {ex.Message}");
                }
            }
    }

    private void LiftFromGround(Instance instance, Node node, float height)
    {
        Matrix4x4 parent = node.Parent >= 0 && ParentNode(instance, node.Parent) is { } p ? World(instance, p) : Matrix4x4.Identity;
        if (!float.IsFinite(height) || !Matrix4x4.Invert(parent, out var inverse))
            throw new InvalidDataException($"Ground contact for node #{node.Source} cannot invert its parent transform.");
        Position(node, Vector3.TransformNormal(new(0, height, 0), inverse), true);
    }

    private float GroundBottom(Instance instance, Node node)
    {
        var key = (instance.Entry, instance.Root, node.Source);
        if (!groundNodes.TryGetValue(key, out var nodes))
        {
            HashSet<int> independent = instance.Entry.AllSequences.SelectMany(s => s.Events)
                .Where(e => e.Type == 10 && e.Bytes.Length >= 252)
                .Select(e => NodeRef(instance, e.I32(16))?.Source ?? -1).ToHashSet();
            List<int> collected = []; HashSet<int> seen = []; Stack<int> pending = new([node.Source]);
            while (pending.TryPop(out int index))
            {
                if (!seen.Add(index) || !instance.Nodes.ContainsKey(index) || index != node.Source && independent.Contains(index)) continue;
                collected.Add(index);
                foreach (int child in SceneBuilder.Children(context.Scene.Nodes[index])) pending.Push(child);
            }
            groundNodes[key] = nodes = collected.ToArray();
        }
        float bottom = float.PositiveInfinity;
        foreach (int index in nodes)
        {
            var part = instance.Nodes[index];
            if (!part.Active || part.PendingPlacement) continue;
            if (context.Scene.Nodes[index].ModelIndex is not int modelIndex || modelIndex < 0 || modelIndex >= context.Scene.Models.Count) continue;
            var model = context.Scene.Models[modelIndex];
            if (!groundVertices.TryGetValue(modelIndex, out var vertices))
            {
                // Use referenced vertices only; unused storage must not enlarge contact.
                if (model.Polygons.Any(p => p.Vertices.Any(v => v < 0 || v >= model.Vertices.Length)))
                    AddNote($"Ground contact: model #{modelIndex} has invalid polygon indices; valid geometry is used.");
                groundVertices[modelIndex] = vertices = model.Polygons.Where(p => p.Vertices.Length >= 3 && p.Vertices.All(v => v >= 0 && v < model.Vertices.Length))
                    .SelectMany(p => p.Vertices).Distinct().Order().ToArray();
            }
            Matrix4x4 world = World(instance, part);
            bool morph = model.Morphs.Length == model.Vertices.Length;
            foreach (int vertex in vertices)
            {
                Vector3 position = model.Vertices[vertex] + (morph ? model.Morphs[vertex] * part.Morph : Vector3.Zero);
                float y = Vector3.Transform(position, world).Y;
                if (!float.IsFinite(y)) throw new InvalidDataException($"Ground contact: model #{modelIndex} has nonfinite transformed geometry.");
                bottom = Math.Min(bottom, y);
            }
        }
        if (float.IsPositiveInfinity(bottom))
        {
            AddNote($"Ground contact: node #{node.Source} has no usable mesh; its origin is used.");
            bottom = World(instance, node).Translation.Y;
        }
        if (!float.IsFinite(bottom)) throw new InvalidDataException($"Ground contact: node #{node.Source} has a nonfinite world position.");
        return bottom + PreviewHeight;
    }
}
