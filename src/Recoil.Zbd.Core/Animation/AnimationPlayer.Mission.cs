using System.Numerics;

namespace Recoil.Zbd.Core.Animation;

public sealed partial class AnimationPlayer
{
    private bool IsWorldNode(int index)
    {
        HashSet<int> seen = [];
        while (index >= 0 && index < context.Scene.Nodes.Count && seen.Add(index))
        {
            var node = context.Scene.Nodes[index]; if (node.Class == "world") return true;
            index = node.Parents.FirstOrDefault(-1);
        }
        return false;
    }
    private Node SharedNode(int index)
    {
        if (sharedNodes.TryGetValue(index, out var found)) return found;
        var source = context.Scene.Nodes[index]; var matrix = SceneBuilder.LocalTransform(source);
        var node = new Node
        {
            Source = index, RenderId = (uint)index, Parent = source.Parents.FirstOrDefault(-1), Exact = matrix,
            Position = matrix.Translation, Scale = Vec(source.Data["scale"], Vector3.One), Euler = Vec(source.Data["rotate"]),
            Active = source.Class is not ("object3d" or "lod") || (source.Metadata.UInt("flags") & 4) != 0,
            Alpha = source.Data.Float("opacity", 1), AlphaEnabled = (source.Data.UInt("flags") & 2) != 0,
            PendingPlacement = source.Metadata["preview_pending_placement"]?.GetValue<bool>() == true
        };
        node.Rotation = AnimationMath.FromEuler(node.Euler);
        if (source.Class == "camera")
        {
            node.Fov = source.Data.Float("fov_h_base", MathF.PI / 3);
            if (Matrix4x4.Decompose(matrix, out var scale, out var rotation, out _))
            { node.Scale = scale; node.Rotation = rotation; node.Euler = AnimationMath.ToEuler(rotation); }
        }
        sharedNodes[index] = node; return node;
    }
    private Node? ParentNode(Instance instance, int index) => index < 0 || index >= context.Scene.Nodes.Count ? null :
        instance.Nodes.TryGetValue(index, out var node) ? node : instance.Shared ? SharedNode(index) : null;

    /// <summary>Run only the immediate initialization frontier on an owned scene copy, without advancing time.</summary>
    internal static HashSet<int> ApplyInitialization(AnimationPreviewContext context, bool cleanup, string[] startup, List<string> diagnostics, CancellationToken token)
    {
        var entries = cleanup ? context.Package.Entries.Where(e => e.Index > 0 && e.Bytes[152] is not (2 or 5)).SelectMany(e =>
            ((e.U32(148) & 0x20) != 0 ? new[] { (Entry: e, Primary: true) } : []).Concat(e.Bytes[153] == 4 && e.Bytes[152] != 4 ? [(Entry: e, Primary: false)] : [])) :
            startup.Select(name => context.Package.Entries.FirstOrDefault(e => e.Name == name)).OfType<AnimationEntry>().Select(e => (Entry: e, Primary: false));
        return ApplyInitialization(context, entries.Select(e => (e.Entry, e.Primary, (int?)null)), diagnostics, token);
    }

    internal static HashSet<int> ApplyInitialization(AnimationPreviewContext context,
        IEnumerable<(AnimationEntry Entry, bool Primary, int? Root)> entries, List<string> diagnostics, CancellationToken token)
    {
        HashSet<int> positioned = [];
        if (context.Package.Entries.Count == 0) return positioned;
        var player = new AnimationPlayer(context, 0) { initializingScene = true };
        player.instances.Clear(); player.sharedNodes.Clear(); player.notes.Clear(); player.nextId = 0;
        foreach (var (entry, primary, boundRoot) in entries)
        {
            token.ThrowIfCancellationRequested();
            int root = boundRoot ?? context.ResolveRoot(entry);
            if (root < 0 || root >= context.Scene.Nodes.Count) { diagnostics.Add($"Mission initialization: unresolved root for {entry.Name}."); continue; }
            player.instances.Clear(); player.dispatchBudget = 10000;
            player.AddInstance(entry, null, boundRoot, primary);
            HashSet<Sequence> visited = [];
            bool progress;
            do
            {
                progress = false;
                foreach (var instance in player.instances.ToArray())
                    foreach (var sequence in instance.Sequences.ToArray())
                        if (sequence.State is 0 or 1 && visited.Add(sequence))
                        {
                            token.ThrowIfCancellationRequested(); player.Run(instance, sequence, 0); progress = true;
                            if (sequence.ObservedInfiniteLoop) player.notes.Add($"{entry.Name}: zero-time initialization loop was bounded.");
                        }
            } while (progress && player.dispatchBudget > 0);
            foreach (var instance in player.instances.Where(i => i.Shared || (i.Entry.U32(148) & 0x8000) == 0))
                foreach (var node in instance.Nodes.Values)
                {
                    var source = context.Scene.Nodes[node.Source];
                    if (source.Class is not ("object3d" or "lod")) continue;
                    source.Metadata["flags"] = node.Active ? source.Metadata.UInt("flags") | 4u : source.Metadata.UInt("flags") & ~4u;
                    if (node.Changed && source.Class == "object3d")
                    {
                        try { MissionSceneLoader.SetPose(context.Scene, node.Source, node.Local); }
                        catch (InvalidDataException ex) { player.notes.Add($"{source.Name}: invalid initialization pose was ignored: {ex.Message}"); continue; }
                    }
                    if (source.Class == "object3d")
                    {
                        source.Data["opacity"] = node.Alpha;
                        source.Data["flags"] = node.AlphaEnabled ? source.Data.UInt("flags") | 2u : source.Data.UInt("flags") & ~2u;
                    }
                    if (node.PositionWritten) positioned.Add(node.Source);
                }
        }
        diagnostics.AddRange(player.notes.Select(n => "Mission initialization: " + n));
        return positioned;
    }
}
