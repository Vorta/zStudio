using System.Numerics;

namespace Recoil.Zbd.Core.Animation;

public sealed partial class AnimationPlayer
{
    private bool IsWorldNode(int index)
    {
        HashSet<int> seen = [];
        while (index >= 0 && index < context.Scene.Nodes.Count)
        {
            bindingOperation?.Reserve(40);
            if (!seen.Add(index)) break;
            var node = context.Scene.Nodes[index]; if (node.Class == "world") return true;
            index = node.Parents.FirstOrDefault(-1);
        }
        return false;
    }
    private Node SharedNode(int index, bool admitted = false)
    {
        if (sharedNodes.TryGetValue(index, out var found)) return found;
        if (!admitted) RequireState(NodeBytes + MapEntryBytes);
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
        sharedNodes[index] = node;
        // This adds one current-state node and one map slot. Existing checkpoints own their own
        // copies, so lazily walking ancestors must not rescan every sequence for every parent.
        AdjustRetainedBytes(NodeBytes + MapEntryBytes);
        return node;
    }
    private Node? ParentNode(Instance instance, int index) => index < 0 || index >= context.Scene.Nodes.Count ? null :
        instance.Nodes.TryGetValue(index, out var node) ? node : instance.Shared ? SharedNode(index) : null;

    /// <summary>
    /// A part of the mission load on an owned scene copy, without advancing time. Its events, like the game's first frame,
    /// run once every animation it starts is marked running (<c>ActivateRuntime</c> 0x45d930 sets state 2), so a launch
    /// without a target node starts none of them again.
    /// </summary>
    /// <param name="load">The load itself: <c>LoadAndInstantiate</c> (0x45fb30) stops every entry with a single
    /// <c>RESET_TIME</c> (flag 0x20), in entry order, running each cleanup at once, and marks the <c>ON_STARTUP</c> entries
    /// running; their events follow all the cleanups. Otherwise the <paramref name="startup"/> list, which
    /// <c>RunStartAnimsFromZrd</c> (0x4192d0) marks running, with its prerequisites cleared, after vehicles, turrets and pickups.</param>
    /// <param name="running">Filled by the load with the entries and roots it started, which the startup list cannot start again.</param>
    /// <param name="player">The local player's starting position, which range-gated entries test; without one none of them runs.</param>
    internal static HashSet<int> ApplyInitialization(AnimationPreviewContext context, bool load, string[] startup, List<string> diagnostics, CancellationToken token,
        AnimationBindingOperation? operation = null, HashSet<(int Entry, int Root)>? running = null, Vector3? player = null)
    {
        operation ??= new(context, token);
        // Selection and the player belong to this same frontier. Invalidate before creating either lookup.
        operation.Invalidate();
        AnimationEntryLookup lookup = operation.EntryLookup();
        HashSet<int> positioned = [];
        if (context.Package.Entries.Count == 0) return positioned;
        if (load)
            positioned.UnionWith(ApplyInitialization(context, context.Package.Entries.Where(e => e.Index > 0 && e.Bytes[152] is not (2 or 5) && (e.U32(148) & 0x20) != 0)
                .Select(e => (e, true, (int?)null)), diagnostics, token, operation, reuseTopology: true));
        operation.Reserve(1);
        // ActivateRuntime refuses the states 4-6, and the load starts no entry stored running (2).
        static bool Startable(AnimationEntry entry) => entry.Bytes[152] is not (2 or 4 or 5 or 6);
        var frame = new AnimationPlayer(context, operation) { dispatchBudget = 10000, rangeGated = true, playerStart = player };
        if (load)
        {
            foreach (var entry in context.Package.Entries)
                if (entry.Index > 0 && entry.Bytes[153] == 4 && Startable(entry)) frame.StartInitialization(entry, false, null, diagnostics, token);
            // The startup list is running too before the first frame runs these events.
            frame.loadRunning = [];
            foreach (string name in startup)
            {
                token.ThrowIfCancellationRequested();
                if (lookup.Find(name, token) is { } entry && Startable(entry) && operation.Root(entry) is >= 0 and int root) frame.loadRunning.Add((entry.Index, root));
            }
        }
        else
        {
            frame.loadRunning = running;
            foreach (string name in startup)
                if (lookup.Find(name, token) is { } entry && Startable(entry)) frame.StartInitialization(entry, false, null, diagnostics, token, prerequisitesCleared: true);
        }
        frame.RunInitialization(token);
        frame.PublishInitialization(positioned);
        if (load && running != null)
            foreach (var instance in frame.instances) if (instance.Shared) running.Add((instance.Entry.Index, instance.Root));
        diagnostics.AddRange(frame.notes.Select(n => "Mission initialization: " + n));
        return positioned;
    }

    /// <summary>Whether the frontier tests EXECUTION_BY_RANGE: in the frame updates, not in the cleanups StopAndCleanup runs at once.</summary>
    private bool rangeGated;
    /// <summary>The local player's starting position: the reference SetConditionalRefPos (0x458af0) stores before the first frame.</summary>
    private Vector3? playerStart;

    /// <summary>
    /// Whether a started instance runs its events in the first frames. <c>RunSequence</c> (0x45d010) runs an entry with
    /// <c>EXECUTION_BY_RANGE</c> (flag 0x02) only while the reference position is enabled and the squared distance from it to
    /// the callback node's world position (<c>GetConditionalRefPosDistanceSq</c> 0x45c640, all three axes) is at least the
    /// minimum at +0x9C and below the range at +0xA0. Every shipped entry stores 2 at +0x9B, so the game first tests in the
    /// third frame; the player has not moved by then, and the entry's own clocks have not started.
    /// </summary>
    private bool InRange(Instance instance)
    {
        if (!rangeGated || (instance.Entry.U32(148) & 2) == 0) return true;
        if (playerStart is not Vector3 player)
        { AddNote($"{instance.Entry.Name}: range-gated, and the player's starting position is unknown; it does not run, as in the game before the player exists.", "Support", "Information"); return false; }
        var operation = bindingOperation!;
        int callback = context.CallbackNode(instance.Entry, instance.Root, instance.Binding, operation);
        // GetWorldPosition failing reads as distance 0.
        float distance = callback < 0 || callback >= context.Scene.Nodes.Count ? 0 : Vector3.DistanceSquared(player,
            ParentNode(instance, callback) is { } node ? World(instance, node).Translation : context.WorldTransform(callback, operation).Translation);
        return distance >= instance.Entry.F32(156) && distance < instance.Entry.F32(160);
    }

    /// <summary>Each entry's immediate frontier on its own, in order: load cleanups and per-turret resets run synchronously.</summary>
    internal static HashSet<int> ApplyInitialization(AnimationPreviewContext context,
        IEnumerable<(AnimationEntry Entry, bool Primary, int? Root)> entries, List<string> diagnostics, CancellationToken token,
        AnimationBindingOperation? operation = null, bool reuseTopology = false)
    {
        HashSet<int> positioned = [];
        if (context.Package.Entries.Count == 0) return positioned;
        operation ??= new(context, token);
        operation.Reserve(1);
        // Another preparation phase may have changed same-count edges. The selector above and the
        // turret phase explicitly share lookups after their own boundary invalidation.
        if (!reuseTopology) operation.Invalidate();
        var player = new AnimationPlayer(context, operation);
        foreach (var (entry, primary, boundRoot) in entries)
        {
            player.instances.Clear(); player.InvalidateRetainedState(); player.dispatchBudget = 10000;
            if (!player.StartInitialization(entry, primary, boundRoot, diagnostics, token)) continue;
            player.RunInitialization(token);
            player.PublishInitialization(positioned);
        }
        diagnostics.AddRange(player.notes.Select(n => "Mission initialization: " + n));
        return positioned;
    }

    private bool StartInitialization(AnimationEntry entry, bool primary, int? boundRoot, List<string> diagnostics, CancellationToken token, bool prerequisitesCleared = false)
    {
        token.ThrowIfCancellationRequested();
        bindingOperation!.Reserve(1);
        int root = boundRoot ?? bindingOperation.Root(entry);
        if (root < 0 || root >= context.Scene.Nodes.Count) { diagnostics.Add($"Mission initialization: unresolved root for {entry.Name}."); return false; }
        AddInstance(entry, null, boundRoot, primary, prerequisitesCleared: prerequisitesCleared);
        return true;
    }

    /// <summary>Every started sequence's zero-time events, including the children they launch, once each.</summary>
    private void RunInitialization(CancellationToken token)
    {
        HashSet<Sequence> visited = [];
        // Each instance is tested once, before its first events, as RunSequence tests it once per frame.
        Dictionary<Instance, bool> gates = new(ReferenceEqualityComparer.Instance);
        bool progress;
        do
        {
            progress = false;
            foreach (var instance in instances.ToArray())
            {
                if (!gates.TryGetValue(instance, out bool runs)) gates[instance] = runs = instance.Cleanup || InRange(instance);
                if (!runs) continue;
                foreach (var sequence in instance.Sequences.ToArray())
                    if (sequence.State is 0 or 1 && visited.Add(sequence))
                    {
                        token.ThrowIfCancellationRequested(); Run(instance, sequence, 0); progress = true;
                        if (sequence.ObservedInfiniteLoop) AddNote($"{instance.Entry.Name}: zero-time initialization loop was bounded.");
                    }
            }
        } while (progress && dispatchBudget > 0);
        ReleaseReplacedSequences();
    }

    private void PublishInitialization(HashSet<int> positioned)
    {
        foreach (var instance in instances.Where(i => i.Shared || (i.Entry.U32(148) & 0x8000) == 0))
            foreach (var node in instance.Nodes.Values)
            {
                bindingOperation!.Reserve(64);
                var source = context.Scene.Nodes[node.Source];
                if (source.Class is not ("object3d" or "lod")) continue;
                source.Metadata["flags"] = node.Active ? source.Metadata.UInt("flags") | 4u : source.Metadata.UInt("flags") & ~4u;
                if (node.Changed && source.Class == "object3d")
                {
                    try { MissionSceneLoader.SetPose(context.Scene, node.Source, node.Local); }
                    catch (InvalidDataException ex) { AddNote($"{source.Name}: invalid initialization pose was ignored: {ex.Message}"); continue; }
                }
                if (source.Class == "object3d")
                {
                    source.Data["opacity"] = node.Alpha;
                    source.Data["flags"] = node.AlphaEnabled ? source.Data.UInt("flags") | 2u : source.Data.UInt("flags") & ~2u;
                }
                if (node.PositionWritten) positioned.Add(node.Source);
            }
    }
}
