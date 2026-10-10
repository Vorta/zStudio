using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core;

/// <summary>Prepared ownership of mission nodes. Each authored actor occurrence remains a distinct identity.</summary>
public sealed class MissionActorIndex
{
    private const int MaximumSceneNodes = 200_000;
    private readonly int[] owners;
    private readonly IReadOnlyList<MissionActor> actors;
    internal long NodeVisits { get; private init; }
    internal long EdgeVisits { get; private init; }

    private MissionActorIndex(int[] owners, IReadOnlyList<MissionActor> actors)
    { this.owners = owners; this.actors = actors; }

    /// <summary>The unique authored occurrence, or null when ownership is absent or ambiguous.</summary>
    public MissionActor? At(int node) => node >= 0 && node < owners.Length && owners[node] > 0 ? actors[owners[node] - 1] : null;

    /// <summary>Whether multiple authored occurrences own this node, distinct from an unowned node.</summary>
    public bool IsAmbiguous(int node) => node >= 0 && node < owners.Length && owners[node] == -1;

    internal static MissionActorIndex Build(GameScene scene, IReadOnlyList<MissionActor> actors, CancellationToken token = default,
        long maximumWork = LookupWorkBudget.MaximumUnits)
    {
        LookupWorkBudget work = new(maximumWork, token);
        int count = scene.Nodes.Count;
        if (count > MaximumSceneNodes) throw new InvalidDataException("The mission has too many nodes to prepare actor inspection.");
        // Admit all retained labels and the bounded work queue before allocating. The input scene and actor list
        // already belong to this immutable mission snapshot; no per-actor subtree is copied or retained.
        work.Reserve(3L * count + actors.Count);
        int[] owners = new int[count], pending = new int[count];
        bool[] queued = new bool[count];
        int head = 0, tail = 0, waiting = 0;
        long nodeVisits = 0, edgeVisits = 0;

        // 0 = no actor, occurrence + 1 = one actor, -1 = ambiguous. Joining two occurrences is irreversible.
        // In particular two equal records, or the same object repeated twice, are still two authored occurrences.
        void Join(int node, int owner)
        {
            if (node < 0 || node >= count || owners[node] == -1 || owners[node] == owner) return;
            owners[node] = owners[node] == 0 ? owner : -1;
            if (queued[node]) return;
            queued[node] = true; pending[tail] = node; tail = (tail + 1) % count; waiting++;
        }
        for (int i = 0; i < actors.Count; i++)
        {
            work.Reserve(1);
            Join(actors[i].Root, i + 1);
        }
        while (waiting > 0)
        {
            work.Reserve(1);
            int node = pending[head]; head = (head + 1) % count; waiting--; queued[node] = false;
            int owner = owners[node]; nodeVisits++;
            // A node changes at most twice, so its outgoing edges are processed at most twice even through
            // cycles, shared descendants and many repeated roots. Shared traversal charges raw partition rows.
            foreach (int child in SceneBuilder.Children(scene.Nodes[node], work))
            {
                edgeVisits++;
                Join(child, owner);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(owners, actors) { NodeVisits = nodeVisits, EdgeVisits = edgeVisits };
    }
}
