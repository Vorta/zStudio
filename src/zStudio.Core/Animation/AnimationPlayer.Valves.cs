namespace zStudio.Core.Animation;

public sealed partial class AnimationPlayer
{
    /// <summary>A preview-owned callback sink, absent during ordinary animation analysis.</summary>
    public Func<int, int, int, bool>? NativeValveCallback { get; set; }
    internal Action<int, int>? NativeValveStarted { get; set; }
    internal double? ValveClock { get; set; }
    internal void BeginValveOperation() => dispatchBudget = 10000;
    internal int ValveParent(int node)
    {
        var authored = context.Scene.Nodes[node];
        if (sharedNodes.TryGetValue(node, out var owned) && owned.Parent != authored.Parents.FirstOrDefault(-1)) return owned.Parent;
        return authored.Parents is [var parent] ? parent : -1;
    }
    internal IReadOnlySet<int> ValveActorNodes(int root, CancellationToken token)
    {
        using var binding = BeginBinding(token);
        Dictionary<int, bool> belongs = new() { [root] = true };
        HashSet<int> result = [];
        foreach (var source in context.Scene.Nodes)
        {
            if (source.ModelIndex == null) continue;
            List<int> chain = []; HashSet<int> seen = []; int at = source.Index;
            while (at >= 0 && at < context.Scene.Nodes.Count && !belongs.ContainsKey(at) && seen.Add(at))
            {
                token.ThrowIfCancellationRequested(); bindingOperation!.Reserve(64);
                chain.Add(at); at = sharedNodes.TryGetValue(at, out var node) ? node.Parent : context.Scene.Nodes[at].Parents.FirstOrDefault(-1);
            }
            bool owned = belongs.GetValueOrDefault(at);
            foreach (int index in chain) belongs[index] = owned;
            if (owned) result.Add(source.Index);
        }
        return result;
    }
    internal IReadOnlyList<AnimationNodePose> ValveActorPoses(int root, IReadOnlySet<int> nodes, CancellationToken token)
    {
        using var binding = BeginBinding(token);
        List<AnimationNodePose> result = [];
        var ancestry = BeginFrameAncestry(bindingOperation!);
        Instance owner = new() { Entry = context.Package.Entries[0], Root = root, Shared = true };
        foreach (int index in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (context.Scene.Nodes[index].ModelIndex is not { } model) continue;
            var node = SharedNode(index); var pose = ancestry.Get(owner, node);
            result.Add(new(node.RenderId, index, model, pose.World, pose.Visible, pose.Opacity, node.Variant, node.Morph, Math.Max(0, Time - node.CycleStart)));
        }
        EndFrameAncestry(ancestry);
        return result;
    }
    internal void StartValveLoadAnimations(CancellationToken token)
    {
        using var binding = BeginBinding(token);
        dispatchBudget = 10000; rangeGated = true; playerStart = context.Mission?.PlayerStart;
        // Native mission setup binds every loaded definition after vehicle placement, before any first-frame events.
        foreach (var entry in context.Package.Entries)
            if (entry.Index > 0 && bindingOperation!.Root(entry) is >= 0 and var root) NativeValveStarted?.Invoke(entry.Index, root);
        foreach (var entry in context.Package.Entries)
            if (entry.Index > 0 && entry.Bytes[153] == 4 && entry.Bytes[152] is not (2 or 4 or 5 or 6))
                StartInitialization(entry, false, null, [], token);
    }
    internal System.Numerics.Matrix4x4 ValveWorldTransform(int node, CancellationToken token)
    {
        using var binding = BeginBinding(token);
        var instance = instances.FirstOrDefault(i => i.Shared && i.Nodes.ContainsKey(node));
        if (instance != null) return World(instance, instance.Nodes[node]);
        var world = System.Numerics.Matrix4x4.Identity;
        HashSet<int> seen = [];
        while (node >= 0 && node < context.Scene.Nodes.Count && seen.Add(node))
        {
            token.ThrowIfCancellationRequested();
            var state = SharedNode(node); world *= state.Local; node = state.Parent;
        }
        return world;
    }
    internal void TeleportValveActor(int root, System.Numerics.Vector3 position, float yaw, CancellationToken token)
    {
        using var binding = BeginBinding(token);
        var node = SharedNode(root);
        var parent = node.Parent < 0 ? System.Numerics.Matrix4x4.Identity : ValveWorldTransform(node.Parent, token);
        if (!System.Numerics.Matrix4x4.Invert(parent, out var inverse)) throw new InvalidDataException("The actor's parent transform is not invertible.");
        var world = System.Numerics.Matrix4x4.CreateRotationY(yaw * (MathF.PI / 180)) * System.Numerics.Matrix4x4.CreateTranslation(position);
        var local = world * inverse;
        if (!System.Numerics.Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation)) throw new InvalidDataException("The teleport pose is not representable.");
        node.Exact = local; node.Position = translation; node.Scale = scale; node.Rotation = rotation; node.Euler = AnimationMath.ToEuler(rotation); node.Changed = true;
    }
    public AnimationFrame FlushValveFrontier(CancellationToken token = default)
    {
        using var binding = BeginBinding(token);
        dispatchBudget = 10000;
        playerStart = context.Mission?.PlayerStart;
        RunInitialization(token);
        return Frame(token);
    }
    public bool StartValveAnimation(int entry, CancellationToken token = default)
    {
        if (entry <= 0 || entry >= context.Package.Entries.Count) return false;
        using var binding = BeginBinding(token);
        return AddInstance(context.Package.Entries[entry], null, null, started: true) != null;
    }
    internal void StartValveMissionStartup(int entry, CancellationToken token)
    {
        using var binding = BeginBinding(token);
        if (context.Package.Entries[entry].Bytes[152] is not (2 or 4 or 5 or 6)) StartInitialization(context.Package.Entries[entry], false, null, [], token);
    }
}
