using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Animation;

/// <summary>One preparation/frontier's bounded, immutable-topology name and subtree queries.</summary>
/// <param name="maximum">The allowance; by default the scene's (<see cref="Allowance(AnimationPreviewContext)"/>).</param>
/// <param name="shared">Indexes an owner keeps for later operations (a player's playback ticks); by default this operation's own.</param>
internal sealed class AnimationBindingOperation(AnimationPreviewContext context, CancellationToken token,
    long? maximum = null, Action<long>? reserved = null, AnimationBindingOperation.Indexes? shared = null)
{
    /// <summary>The allowance of a scene of up to 16,384 nodes. Original m6 (4,955 nodes) starts in about 20 Mi.</summary>
    internal const long MaximumUnits = 96L * 1024 * 1024;
    /// <summary>
    /// A larger scene's allowance per node. Binding grows with the scene, not only with the animations: the name and subtree
    /// indexes hold every node, and every animation bound at the world root (m6's five load cleanups, m1's start) takes all of
    /// its nodes. Up to the 65,536 nodes a GameZ world holds (zStudio's builds allow that many), so it still bounds animations
    /// that bind large subtrees over and over.
    /// </summary>
    internal const long UnitsPerNode = MaximumUnits / 16384;
    internal const long MaximumScaledUnits = UnitsPerNode * GameZLayouts.MaximumTableEntries;
    internal static long Allowance(int nodes) => Math.Max(MaximumUnits, UnitsPerNode * Math.Clamp(nodes, 0, GameZLayouts.MaximumTableEntries));
    internal static long Allowance(AnimationPreviewContext context) => Allowance(SceneNodes(context));
    private static int SceneNodes(AnimationPreviewContext context) => (context.Mission?.Scene ?? context.World.Scene)?.Nodes.Count ?? 0;
    private readonly int sceneNodes = SceneNodes(context);
    private readonly long allowance = maximum ?? Allowance(context);
    private readonly LookupWorkBudget work = new(maximum ?? Allowance(context), token, ceiling: MaximumScaledUnits);
    private readonly Indexes cache = shared ?? new();
    // What a refusal names: the instances bound, their roots' nodes, the largest of them, and the work of the indexes.
    private long boundNodes, indexWork;
    private int instances, largestNodes, largestRoot;
    private AnimationEntry? largest;
    internal long Used => work.UsedUnits;
    internal bool Exhausted => work.Exhausted;

    /// <summary>
    /// One scene's name, subtree and root indexes. Every operation charges its own queries; <see cref="Retained"/> is the
    /// work that built the indexes, which an owner keeping them for later operations charges to each of those.
    /// </summary>
    internal sealed class Indexes
    {
        internal readonly Dictionary<(int Root, int Limit), Subtree> Subtrees = [];
        internal readonly Dictionary<(int Limit, bool Live), Dictionary<string, List<int>>> Globals = [];
        internal readonly Dictionary<(AnimationEntry Entry, string Name, int Root, AnimationBinding Binding), int> Resolved = [];
        internal readonly Dictionary<AnimationEntry, int> Roots = [];
        internal int[]? Positions;
        internal AnimationEntryLookup? Entries;
        internal GameScene? Scene;
        internal int Count = -1;
        internal long Retained;
    }

    internal void Reserve(long amount)
    {
        try { work.Reserve(amount); }
        catch (InvalidDataException ex) when (work.Exhausted) { throw Refused(ex); }
        reserved?.Invoke(Used);
        token.ThrowIfCancellationRequested();
    }
    /// <summary>Charge the work that built indexes kept from earlier operations (<see cref="Indexes.Retained"/>).</summary>
    internal void ReserveRetained() { Reserve(1 + cache.Retained); indexWork += cache.Retained; }
    /// <summary>An animation instance bound at <paramref name="root"/>, whose <paramref name="count"/> nodes it takes.</summary>
    internal void Bound(AnimationEntry entry, int root, int count)
    {
        instances++; boundNodes += count;
        if (largest != null && count <= largestNodes) return;
        largest = entry; largestRoot = root; largestNodes = count;
    }
    private IOException Refused(Exception ex)
    {
        var scene = context.Scene;
        string root = largestRoot >= 0 && largestRoot < scene.Nodes.Count ? JsonData.ShownText(scene.Nodes[largestRoot].Name, 64) : $"#{largestRoot}";
        string bound = largest == null ? "no animation instance was bound yet" :
            $"{instances:N0} animation instances were bound at roots holding {boundNodes:N0} nodes (the largest, {JsonData.ShownText(largest.Name, 64)} at {root}, holds {largestNodes:N0})";
        return new($"Animation binding needs more than its {allowance:N0} units of work for this {sceneNodes:N0}-node scene: {bound}, and its name and subtree indexes took {indexWork:N0}. " +
            "Bind fewer animations at large subtrees such as the world root, or simplify repeated roots and references.", ex);
    }
    internal void Refresh()
    {
        Reserve(1);
        if (ReferenceEquals(cache.Scene, context.Scene) && cache.Count == context.Scene.Nodes.Count) return;
        // Mission placement can append nodes between cleanup and startup. Old maps must not survive that boundary.
        Invalidate(); cache.Scene = context.Scene; cache.Count = cache.Scene.Nodes.Count;
    }
    internal void Invalidate()
    {
        Reserve((long)cache.Subtrees.EnsureCapacity(0) + cache.Globals.EnsureCapacity(0) + cache.Resolved.EnsureCapacity(0) + cache.Roots.EnsureCapacity(0));
        cache.Subtrees.Clear(); cache.Globals.Clear(); cache.Resolved.Clear(); cache.Roots.Clear();
        cache.Positions = null; cache.Entries = null; cache.Retained = 0;
    }
    /// <summary>Keep the work that built a complete index entry; a canceled or refused build leaves none.</summary>
    private void Retain(long start) { cache.Retained += Used - start; indexWork += Used - start; }
    internal AnimationEntryLookup EntryLookup()
    {
        Refresh();
        if (cache.Entries != null) return cache.Entries;
        long start = Used;
        // Preserve the turret index's conservative fixed-name/map allowance when sharing with players.
        Reserve(128L * context.Package.Entries.Count);
        var lookup = new AnimationEntryLookup(context.Package);
        // Build once with the operation token, including when a later child-name lookup has no token.
        _ = lookup.Find("", token);
        cache.Entries = lookup; Retain(start);
        return lookup;
    }
    internal int LoadedRoot(AnimationEntry entry)
    {
        Refresh(); Reserve(1);
        if (cache.Roots.TryGetValue(entry, out int root)) return root;
        bool mw3 = context.World.Game == GameVariant.MechWarrior3;
        var named = Named(entry.RootName, mw3 ? cache.Count : context.LoadedLimit, live: !mw3);
        if (mw3) root = named.Count == 1 ? named[0] : -1;
        else if (named.Count == 0) root = -1;
        else
        {
            if (cache.Positions == null)
            {
                Reserve(8L * context.Package.Entries.Count);
                cache.Positions = NameLookups.RootPositions(context.Package.Entries, name => Named(name, context.LoadedLimit).Count);
                cache.Retained += 8L * context.Package.Entries.Count; indexWork += 8L * context.Package.Entries.Count;
            }
            var positions = cache.Positions;
            int position = entry.Index >= 0 && entry.Index < positions.Length && context.Package.Entries[entry.Index].RootName == entry.RootName ? positions[entry.Index] : -1;
            root = named[Math.Clamp(position, 0, named.Count - 1)];
        }
        Reserve(64); cache.Roots.Add(entry, root); cache.Retained += 64; return root;
    }
    internal int Resolve(AnimationEntry entry, string name, int root, AnimationBinding binding)
    {
        Refresh(); Reserve(name.Length + 1L);
        var key = (entry, name, root, binding);
        if (cache.Resolved.TryGetValue(key, out int value)) return value;
        Reserve(96L + 2L * name.Length);
        value = context.ResolveName(entry, name, root, binding, this);
        cache.Resolved.Add(key, value); cache.Retained += 96L + 2L * name.Length; return value;
    }
    internal int Root(AnimationEntry entry)
    {
        Refresh();
        return context.ResolveRoot(entry, this);
    }
    internal int First(int root, string name, int limit) => Below(root, name, limit, unique: false);
    internal int Unique(int root, string name, int limit) => Below(root, name, limit, unique: true);
    private int Below(int root, string name, int limit, bool unique)
    {
        var tree = Tree(root, limit); Reserve(name.Length + 1L);
        return (unique ? tree.Unique : tree.First).GetValueOrDefault(name, -1);
    }
    internal IReadOnlyList<int> Descendants(int root, int limit) => Tree(root, limit).Nodes;
    private Subtree Tree(int root, int limit)
    {
        Refresh(); limit = Math.Min(limit, cache.Count);
        Reserve(1);
        if (cache.Subtrees.TryGetValue((root, limit), out var known)) return known;
        long start = Used;
        Reserve(256);
        Subtree tree = new(); HashSet<int> seen = []; Stack<int> pending = new(); pending.Push(root);
        while (pending.TryPop(out int index))
        {
            Reserve(1);
            if (index < 0 || index >= limit) continue;
            Reserve(32);
            if (!seen.Add(index)) continue;
            var node = cache.Scene!.Nodes[index];
            Reserve(160L + 3L * node.Name.Length);
            tree.Nodes.Add(index);
            tree.First.TryAdd(node.Name, index);
            if (!tree.Unique.TryAdd(node.Name, index)) tree.Unique[node.Name] = -1;
            List<int> children = [];
            try
            {
                // Children charges raw partition occurrences before its own deduplication storage.
                foreach (int child in SceneBuilder.Children(node, work)) { Reserve(16); children.Add(child); }
            }
            catch (InvalidDataException ex) when (work.Exhausted) { throw Refused(ex); }
            for (int i = children.Count - 1; i >= 0; i--) { Reserve(8); pending.Push(children[i]); }
        }
        Reserve(64);
        cache.Subtrees.Add((root, limit), tree); Retain(start);
        return tree;
    }
    internal IReadOnlyList<int> Named(string name, int limit, bool live = true)
    {
        Refresh(); limit = Math.Min(limit, cache.Count); Reserve(1 + name.Length);
        if (!cache.Globals.TryGetValue((limit, live), out var map))
        {
            long start = Used;
            Reserve(128); map = new(StringComparer.Ordinal);
            for (int i = limit - 1; i >= 0; i--)
            {
                Reserve(1); var node = cache.Scene!.Nodes[i];
                if (live && node.Class == "none") continue;
                Reserve(96L + node.Name.Length);
                if (!map.TryGetValue(node.Name, out var nodes)) map.Add(node.Name, nodes = []);
                nodes.Add(node.Index);
            }
            cache.Globals.Add((limit, live), map); Retain(start);
        }
        return map.TryGetValue(name, out var found) ? found : [];
    }
    internal sealed class Subtree
    {
        internal readonly List<int> Nodes = [];
        internal readonly Dictionary<string, int> First = new(StringComparer.Ordinal), Unique = new(StringComparer.Ordinal);
    }
}
