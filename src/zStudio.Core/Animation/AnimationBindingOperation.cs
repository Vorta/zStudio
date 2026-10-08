using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Animation;

/// <summary>One preparation/frontier's bounded, immutable-topology name and subtree queries.</summary>
internal sealed class AnimationBindingOperation(AnimationPreviewContext context, CancellationToken token,
    long maximum = LookupWorkBudget.MaximumUnits, Action<long>? reserved = null)
{
    private readonly LookupWorkBudget work = new(maximum, token);
    private readonly Dictionary<(int Root, int Limit), Subtree> subtrees = [];
    private readonly Dictionary<(int Limit, bool Live), Dictionary<string, List<int>>> globals = [];
    private readonly Dictionary<(AnimationEntry Entry, string Name, int Root, AnimationBinding Binding), int> resolved = [];
    private readonly Dictionary<AnimationEntry, int> roots = [];
    private int[]? positions;
    private GameScene? scene;
    private int count = -1;
    internal long Used => work.UsedUnits;
    internal bool Exhausted => work.Exhausted;
    internal void Reserve(long amount)
    {
        try { work.Reserve(amount); }
        catch (InvalidDataException ex) when (work.Exhausted) { throw Refused(ex); }
        reserved?.Invoke(Used);
        token.ThrowIfCancellationRequested();
    }
    private static IOException Refused(Exception ex) => new("Animation binding exceeds its aggregate work or retained lookup allowance. Simplify repeated roots/references or initialize fewer actors.", ex);
    internal void Refresh()
    {
        Reserve(1);
        if (ReferenceEquals(scene, context.Scene) && count == context.Scene.Nodes.Count) return;
        // Mission placement can append nodes between cleanup and startup. Old maps must not survive that boundary.
        Invalidate(); scene = context.Scene; count = scene.Nodes.Count;
    }
    internal void Invalidate()
    {
        Reserve((long)subtrees.EnsureCapacity(0) + globals.EnsureCapacity(0) + resolved.EnsureCapacity(0) + roots.EnsureCapacity(0));
        subtrees.Clear(); globals.Clear(); resolved.Clear(); roots.Clear(); positions = null;
    }
    internal int LoadedRoot(AnimationEntry entry)
    {
        Refresh(); Reserve(1);
        if (roots.TryGetValue(entry, out int root)) return root;
        bool mw3 = context.World.Game == GameVariant.MechWarrior3;
        var named = Named(entry.RootName, mw3 ? count : context.LoadedLimit, live: !mw3);
        if (mw3) root = named.Count == 1 ? named[0] : -1;
        else if (named.Count == 0) root = -1;
        else
        {
            if (positions == null)
            {
                Reserve(8L * context.Package.Entries.Count);
                positions = NameLookups.RootPositions(context.Package.Entries, name => Named(name, context.LoadedLimit).Count);
            }
            int position = entry.Index >= 0 && entry.Index < positions.Length && context.Package.Entries[entry.Index].RootName == entry.RootName ? positions[entry.Index] : -1;
            root = named[Math.Clamp(position, 0, named.Count - 1)];
        }
        Reserve(64); roots.Add(entry, root); return root;
    }
    internal int Resolve(AnimationEntry entry, string name, int root, AnimationBinding binding)
    {
        Refresh(); Reserve(name.Length + 1L);
        var key = (entry, name, root, binding);
        if (resolved.TryGetValue(key, out int value)) return value;
        Reserve(96L + 2L * name.Length);
        value = context.ResolveName(entry, name, root, binding, this);
        resolved.Add(key, value); return value;
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
        Refresh(); limit = Math.Min(limit, count);
        Reserve(1);
        if (subtrees.TryGetValue((root, limit), out var known)) return known;
        Reserve(256);
        Subtree tree = new(); HashSet<int> seen = []; Stack<int> pending = new(); pending.Push(root);
        while (pending.TryPop(out int index))
        {
            Reserve(1);
            if (index < 0 || index >= limit) continue;
            Reserve(32);
            if (!seen.Add(index)) continue;
            var node = scene!.Nodes[index];
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
        subtrees.Add((root, limit), tree);
        return tree;
    }
    internal IReadOnlyList<int> Named(string name, int limit, bool live = true)
    {
        Refresh(); limit = Math.Min(limit, count); Reserve(1 + name.Length);
        if (!globals.TryGetValue((limit, live), out var map))
        {
            Reserve(128); map = new(StringComparer.Ordinal);
            for (int i = limit - 1; i >= 0; i--)
            {
                Reserve(1); var node = scene!.Nodes[i];
                if (live && node.Class == "none") continue;
                Reserve(96L + node.Name.Length);
                if (!map.TryGetValue(node.Name, out var nodes)) map.Add(node.Name, nodes = []);
                nodes.Add(node.Index);
            }
            globals.Add((limit, live), map);
        }
        return map.TryGetValue(name, out var found) ? found : [];
    }
    private sealed class Subtree
    {
        internal readonly List<int> Nodes = [];
        internal readonly Dictionary<string, int> First = new(StringComparer.Ordinal), Unique = new(StringComparer.Ordinal);
    }
}
