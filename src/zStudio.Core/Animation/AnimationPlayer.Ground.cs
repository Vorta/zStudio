using System.Numerics;

namespace zStudio.Core.Animation;

public sealed partial class AnimationPlayer
{
    // These caches contain source identities/indices only, never runtime poses.
    // They can survive Reset/seek; every height uses the current checkpoint pose.
    private readonly Dictionary<int, int[]> groundVertices = [];
    private readonly Dictionary<(AnimationEntry Entry, int Root, int Node), HashSet<int>> groundNodes = [];

    private float PrepareGround(Instance instance, Node node)
    {
        var points = GroundPoints(instance, node);
        float bottom = points.Min(p => p.Y);
        if (Terrain != null)
        {
            Vector3 origin = World(instance, node).Translation + Vector3.UnitY * PreviewHeight;
            // Only resolve overlap with a floor below the origin, never a roof overhead.
            if (node.GroundSupported) origin.Y = Math.Max(origin.Y, node.GroundHeight + .001f);
            float lift = 0;
            foreach (var point in points)
            {
                if (point.Y > origin.Y) continue;
                float? height = Terrain.Height(new(point.X, origin.Y, point.Z), point.Y, groundNodes[(instance.Entry, instance.Root, node.Source)], ReserveGround);
                if (height is float y) lift = Math.Max(lift, y - point.Y);
            }
            var (min, max) = GroundBounds(points);
            if (Terrain.FootprintHeight(min, max, origin.Y, groundNodes[(instance.Entry, instance.Root, node.Source)], ReserveGround) is float support)
                lift = Math.Max(lift, support - bottom);
            if (lift > 0) { LiftFromGround(instance, node, lift); bottom += lift; }
            return bottom;
        }
        if (bottom < 0) LiftFromGround(instance, node, -bottom);
        return Math.Max(0, bottom);
    }

    private bool ResolveGround(Instance instance, Node node, AnimationEvent ev, float previousBottom, out bool contact)
    {
        contact = false;
        float bottom = GroundBottom(instance, node);
        if (bottom >= 0) return false;
        LiftFromGround(instance, node, -bottom);
        if (bottom >= previousBottom) return false;
        contact = true;
        node.GroundSupported = true;
        return GroundImpact(instance, node, ev);
    }

    private bool GroundImpact(Instance instance, Node node, AnimationEvent ev)
    {
        // The origin bound wins over mesh support at a surface at the edge of the preview world.
        if (ConstrainProcedural(instance, node, World(instance, node).Translation))
        { NoteProceduralLimit(instance, node); return true; }

        // Retail zEffect::HandleNodeAnimEvent 0x45A4DB–0x45A6C0.
        // The preview substitutes mesh support for the engine's origin ray pick.
        Vector3 velocity = ev.Vector(88), acceleration = ev.Vector(100);
        float speedSquared = velocity.LengthSquared(), accelerationSquared = acceleration.LengthSquared();
        if (!float.IsFinite(speedSquared) || !float.IsFinite(accelerationSquared))
            throw new InvalidDataException("Ground contact has nonfinite velocity or acceleration.");
        bool settled = speedSquared < Math.Max(accelerationSquared, 1e-8f);
        if (!settled) ev.SetVector(88, velocity * .199999988f);

        uint flags = ev.U32(12);
        // A cleanup's contact names a runtime sequence, which does not run during it: no release.
        if ((flags & 0x800) != 0 && !instance.Cleanup)
        {
            int index = ev.I16(240);
            var target = index >= 0 && index < instance.Sequences.Count ? instance.Sequences[index]
                : instance.Sequences.FirstOrDefault(s => s.Data.Name == ev.Text(208));
            if (target == null) { unavailableDuration = true; AddNote($"Ground impact: unresolved release sequence '{ev.Text(208)}' (index {index})."); }
            else if (target.State == 3) ReleaseSequence(target, ev, instance, Time, "contact");
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
        if (OriginCollision && Terrain != null || !GroundPlaneEnabled && Terrain == null) return;
        HashSet<Node> corrected = [];
        foreach (var instance in instances)
            foreach (var node in instance.Nodes.Values.Where(n => n.GroundSupported && n.Active && !n.PendingPlacement))
            {
                if (!corrected.Add(node)) continue;
                try
                {
                    PrepareGround(instance, node);
                    if (ConstrainProcedural(instance, node, World(instance, node).Translation)) NoteProceduralLimit(instance, node);
                }
                catch (InvalidDataException ex)
                {
                    node.GroundSupported = false; unavailableDuration = true;
                    AddNote($"Ground support for node #{node.Source}: {ex.Message}");
                }
            }
    }

    private void LiftFromGround(Instance instance, Node node, float height)
        => MoveWorld(instance, node, new(0, height, 0));

    private void MoveWorld(Instance instance, Node node, Vector3 displacement)
    {
        Matrix4x4 parent = node.Parent >= 0 && ParentNode(instance, node.Parent) is { } p ? World(instance, p) : Matrix4x4.Identity;
        if (!float.IsFinite(displacement.LengthSquared()) || !Matrix4x4.Invert(parent, out var inverse))
            throw new InvalidDataException($"Ground contact for node #{node.Source} cannot invert its parent transform.");
        Position(node, Vector3.TransformNormal(displacement, inverse), true);
    }

    private float GroundBottom(Instance instance, Node node) => GroundPoints(instance, node).Min(p => p.Y);

    private void ReserveGround(long work) => bindingOperation!.Reserve(work);

    private List<Vector3> GroundPoints(Instance instance, Node node)
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
                ReserveGround(1);
                if (!seen.Add(index) || !instance.Nodes.ContainsKey(index) || index != node.Source && independent.Contains(index)) continue;
                collected.Add(index);
                foreach (int child in SceneBuilder.Children(context.Scene.Nodes[index])) pending.Push(child);
            }
            groundNodes[key] = nodes = collected.ToHashSet();
        }
        List<Vector3> points = [];
        foreach (int index in nodes)
        {
            var part = instance.Nodes[index];
            if (!part.Active || part.PendingPlacement) continue;
            if (context.Scene.Nodes[index].ModelIndex is not int modelIndex || modelIndex < 0 || modelIndex >= context.Scene.Models.Count) continue;
            var model = context.Scene.Models[modelIndex];
            if (!groundVertices.TryGetValue(modelIndex, out var vertices))
            {
                foreach (var polygon in model.Polygons) ReserveGround(1L + polygon.Vertices.Length);
                // Use referenced vertices only; unused storage must not enlarge contact.
                if (model.Polygons.Any(p => p.Vertices.Any(v => v < 0 || v >= model.Vertices.Length)))
                    AddNote($"Ground contact: model #{modelIndex} has invalid polygon indices; valid geometry is used.");
                groundVertices[modelIndex] = vertices = model.Polygons.Where(p => p.Vertices.Length >= 3 && p.Vertices.All(v => v >= 0 && v < model.Vertices.Length))
                    .SelectMany(p => p.Vertices).Distinct().Take(65537).Order().ToArray();
                if (vertices.Length > 65536) throw new InvalidDataException("Ground support exceeds 65,536 mesh vertices.");
            }
            Matrix4x4 world = World(instance, part);
            bool morph = model.Morphs.Length == model.Vertices.Length;
            foreach (int vertex in vertices)
            {
                ReserveGround(1);
                if (points.Count >= 65536) throw new InvalidDataException("Ground support exceeds 65,536 mesh vertices.");
                Vector3 position = model.Vertices[vertex] + (morph ? model.Morphs[vertex] * part.Morph : Vector3.Zero);
                position = Vector3.Transform(position, world) + Vector3.UnitY * PreviewHeight;
                if (!float.IsFinite(position.LengthSquared())) throw new InvalidDataException($"Ground contact: model #{modelIndex} has nonfinite transformed geometry.");
                points.Add(position);
            }
        }
        Vector3 origin = World(instance, node).Translation + Vector3.UnitY * PreviewHeight;
        if (points.Count == 0)
        {
            AddNote($"Ground contact: node #{node.Source} has no usable mesh; its origin is used.");
            points.Add(origin);
        }
        if (!float.IsFinite(origin.LengthSquared())) throw new InvalidDataException($"Ground contact: node #{node.Source} has a nonfinite world position.");
        // Include support below the origin, even when a large floor fits between the object's vertices.
        points.Add(new(origin.X, points.Min(p => p.Y), origin.Z));
        return points;
    }

    private bool ResolveTerrain(Instance instance, Node node, AnimationEvent ev, List<Vector3> before, Vector3 previousOrigin, out bool contact)
    {
        var after = GroundPoints(instance, node);
        var excluded = groundNodes[(instance.Entry, instance.Root, node.Source)];
        float? first = null;
        for (int i = 0; i < Math.Min(before.Count, after.Count); i++)
            if (Terrain!.Contact(before[i], after[i], excluded, ReserveGround) is float t && (first == null || t < first)) first = t;
        var (fromMin, fromMax) = GroundBounds(before); var (toMin, toMax) = GroundBounds(after);
        if (Terrain!.ContactFootprint(fromMin, fromMax, toMin, toMax, excluded, ReserveGround) is float footprint && (first == null || footprint < first)) first = footprint;
        contact = first != null;
        if (first is not float fraction) return false;
        Vector3 origin = World(instance, node).Translation;
        MoveWorld(instance, node, (previousOrigin - origin) * (1 - fraction));
        PrepareGround(instance, node);
        node.GroundSupported = true; node.GroundHeight = GroundBottom(instance, node);
        return GroundImpact(instance, node, ev);
    }

    private bool ConstrainProcedural(Instance instance, Node node, Vector3 previousOrigin)
    {
        if (!ProceduralLimitsEnabled || context.Scene.Nodes[node.Source].Class == "camera") return false;
        Vector3 current = World(instance, node).Translation;
        float y = current.Y + PreviewHeight;
        if (!float.IsFinite(y)) throw new InvalidDataException("Procedural motion has a nonfinite world position.");
        if (y is >= -500 and <= 500) return false;
        float boundary = Math.Clamp(y, -500, 500) - PreviewHeight;
        float t = current.Y == previousOrigin.Y ? 0 : Math.Clamp((boundary - previousOrigin.Y) / (current.Y - previousOrigin.Y), 0, 1);
        Vector3 target = Vector3.Lerp(previousOrigin, current, t); target.Y = boundary;
        MoveWorld(instance, node, target - current);
        node.GroundSupported = false;
        return true;
    }
    private void NoteProceduralLimit(Instance instance, Node node) => AddNote(
        $"Procedural motion for node #{node.Source} stopped at the preview Y limit ({World(instance, node).Translation.Y + PreviewHeight:0.###}).", "Support", "Information");
    private static (Vector3 Min, Vector3 Max) GroundBounds(List<Vector3> points)
    {
        Vector3 min = points[0], max = points[0];
        foreach (var point in points) { min = Vector3.Min(min, point); max = Vector3.Max(max, point); }
        return (min, max);
    }
}
