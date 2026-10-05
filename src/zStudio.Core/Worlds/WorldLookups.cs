using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// The engine's lookups by name (docs/engine-evidence.md#name-lookups-and-node-slot-order). Every live node of every class
/// is in one list (bucket 6), the most recently created first, and a world file's nodes join it in slot order, so a lookup
/// finds the highest slot of a name first (<c>FindByTypeAndName</c> 0x44ecf0).
/// </summary>
public static class NameLookups
{
    /// <summary>The entry byte holding its activation state (zEffectAnimEntry +0x98); the root binding loop skips state 5.</summary>
    public const int StateOffset = 0x98, SkippedState = 5;

    /// <summary>Whether <c>LoadZbd</c>'s binding loop skips an entry: entry 0, and entries in state 5 (without breaking the chain).</summary>
    public static bool SkipsRoot(IReadOnlyList<AnimationEntry> entries, int index) =>
        index == 0 || entries[index].Bytes.Length > StateOffset && entries[index].Bytes[StateOffset] == SkippedState;

    /// <summary>
    /// Which of its root name's nodes (highest slot first) each entry binds to, or −1 for a skipped entry or a name no node
    /// has (0x45f5bf–0x45f689). An entry with the root name of the last search continues it with the next node of that name;
    /// a new name, or the end of the nodes, starts the search again at the highest slot.
    /// </summary>
    public static int[] RootPositions(IReadOnlyList<AnimationEntry> entries, Func<string, int> candidates)
    {
        int[] result = new int[entries.Count]; Array.Fill(result, -1);
        string? searched = null; int position = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (SkipsRoot(entries, i)) continue;
            string name = entries[i].RootName; int count = candidates(name);
            if (searched == name && position >= 0 && position + 1 < count) position++;
            else { searched = name; position = count > 0 ? 0 : -1; }
            result[i] = position;
        }
        return result;
    }
}

/// <summary>A lookup by name a mission makes when the game loads it, and the node it finds.</summary>
/// <param name="Kind">"texture effect" (FindNode in the scripts the mission runs), "animation root", "animation attachment" (the
/// node whose hits start an animation, when its root's subtree lacks it, so the whole world is searched), "animation name"
/// (a node or tracked-node name inside an animation that neither its attachment's nor its root's subtree has, nor the
/// animation's own lights and sounds, so it falls back to the whole world), or "animation prerequisite" (the first node of an
/// activation prerequisite's path, looked up in the whole world).</param>
/// <param name="Source">The script (project path) or animation entry that makes the lookup.</param>
/// <param name="Candidates">How many nodes have the name.</param>
/// <param name="Slot">The found node's slot in the world file, or −1 when no node has the name.</param>
/// <param name="Found">The found node's path, its first parents' names from the top.</param>
public sealed record SourceLookup(string Mission, string Kind, string Name, string Source, int Candidates, int Slot, string? Found)
{
    public const string TextureEffect = "texture effect", AnimationRoot = "animation root", AnimationAttachment = "animation attachment", AnimationName = "animation name", AnimationPrerequisite = "animation prerequisite";
    public bool Ambiguous => Candidates > 1;
    /// <summary>Of the animation entries named <see cref="Source"/>, which one makes the lookup (0 for the first): names are not unique.</summary>
    internal int Occurrence { get; init; }
    internal (string Kind, string Source, int Occurrence, string Name) Key => (Kind, Source, Occurrence, Name);
    /// <summary>The found node's structure, children's names and transform: two nodes that share a path and a slot number but differ show it.</summary>
    internal string? Fingerprint { get; init; }
    /// <summary>Whether another lookup may find another node: its slot, path, candidates or found node's fingerprint differ.</summary>
    internal bool MayDiffer(SourceLookup other) => Slot != other.Slot || Found != other.Found || Candidates != other.Candidates || Fingerprint != other.Fingerprint;
}

/// <summary>A lookup that finds another node than it did before.</summary>
public sealed record SourceLookupChange(SourceLookup Before, SourceLookup After);

public static class WorldLookups
{
    /// <summary>The script the game runs when it loads a mission (archive mode), which reads the world and sets up texture effects.</summary>
    public static string LoadScript(string mission) => $"{SourceProject.GameGenFolder}/{mission}_zbd.gs";

    /// <summary>
    /// The names FindNode looks up in the scripts the game runs when it loads <paramref name="mission"/>: its load script and
    /// the scripts that one sources, in order, except macros (<c>%worldName%</c>). <paramref name="read"/> gives a project
    /// file's bytes, or null when it does not exist.
    /// </summary>
    public static IReadOnlyList<(string Source, string Name)> FindNodes(Func<string, byte[]?> read, string mission)
    {
        List<(string, string)> names = []; HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        void Run(string path, int depth)
        {
            if (depth > 8 || !visited.Add(path) || read(path) is not { } bytes) return;
            foreach (var tokens in GameGenScriptText.Tokenize(Encoding.Latin1.GetString(bytes)))
            {
                if (tokens.Count < 2) continue;
                if (tokens[0].Equals("source", StringComparison.OrdinalIgnoreCase)) { if (Sourced(tokens[1]) is { } sourced) Run(sourced, depth + 1); }
                else if (tokens[0].Equals("FindNode", StringComparison.OrdinalIgnoreCase) && !tokens[1].StartsWith('%')) names.Add((path, tokens[1]));
            }
        }
        Run(LoadScript(mission), 0);
        return names;
        // The script a line names inside gamegen (".\" and repeated separators allowed); others are not followed.
        static string? Sourced(string argument)
        {
            if (System.IO.Path.IsPathRooted(argument)) return null;
            string[] parts = [.. argument.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".")];
            return parts.Length == 0 || parts.Contains("..") ? null : $"{SourceProject.GameGenFolder}/{string.Join('/', parts)}";
        }
    }

    /// <summary>
    /// Every lookup by name <paramref name="mission"/> makes when the game loads it, resolved as the engine resolves it
    /// (LoadZbd's ResolveNodeByName: the attachment's subtree, the root's, then the whole world, highest slot first): the
    /// texture-effect scripts' FindNode, each animation's root (the binding loop's chain), its attachment when the root's
    /// subtree lacks it, and each node name inside an animation that neither subtree has.
    /// </summary>
    public static IReadOnlyList<SourceLookup> Resolve(string mission, GameZWorld world, AnimationPackage? animations, IEnumerable<(string Source, string Name)> findNodes, CancellationToken token = default)
    {
        var slots = GameZWriter.NodeSlots(world);
        var byName = world.Nodes.GroupBy(n => n.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderByDescending(n => slots[n]).ToList(), StringComparer.Ordinal);
        int Count(string name) => byName.TryGetValue(name, out var list) ? list.Count : 0;
        WorldNode? Highest(string name) => byName.TryGetValue(name, out var list) ? list[0] : null;
        List<SourceLookup> result = []; HashSet<(string, string, int, string)> seen = [];
        void Add(string kind, string name, string source, WorldNode? found, int occurrence = 0)
        {
            if (seen.Add((kind, source, occurrence, name)))
                result.Add(new(mission, kind, name, source, Count(name), found == null ? -1 : slots[found], found == null ? null : Path(found)) { Fingerprint = found == null ? null : Fingerprint(found), Occurrence = occurrence });
        }
        foreach (var (source, name) in findNodes) Add(SourceLookup.TextureEffect, name, source, Highest(name));
        if (animations != null)
        {
            var entries = animations.Entries;
            var positions = NameLookups.RootPositions(entries, Count);
            Dictionary<string, int> named = new(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = entries[i]; string root = entry.RootName;
                int occurrence = named[entry.Name] = named.GetValueOrDefault(entry.Name, -1) + 1;
                if (NameLookups.SkipsRoot(entries, i)) continue;
                WorldNode? bound = positions[i] >= 0 ? byName[root][positions[i]] : null;
                Add(SourceLookup.AnimationRoot, root, entry.Name, bound, occurrence);
                // The attachment is looked up like any name; references then search its subtree before the root's.
                WorldNode? attachment = bound;
                if (entry.AttachName.Length > 0 && entry.AttachName != root)
                {
                    attachment = bound == null ? null : FirstBelow(bound, entry.AttachName);
                    if (attachment == null) { attachment = Highest(entry.AttachName); Add(SourceLookup.AnimationAttachment, entry.AttachName, entry.Name, attachment, occurrence); }
                }
                // Tracked nodes and node references (LoadZbd skips each table's reserved first record); a name the subtrees
                // lack is the entry's own light or sound before the whole world (ResolveNodeByName).
                HashSet<string>? below = null;
                foreach (var name in new[] { 0, 1 }.SelectMany(table => entry.References[table].Skip(1)).Select(r => r.Text(0, 36)))
                {
                    if (name.Length == 0 || name == root) continue;
                    below ??= [.. new[] { bound, attachment }.OfType<WorldNode>().SelectMany(WorldAssembler.Subtree).Select(n => n.Name),
                        .. new[] { 2, 3 }.SelectMany(table => entry.References[table].Skip(1)).Select(r => r.Text(0, 36))];
                    if (!below.Contains(name)) Add(SourceLookup.AnimationName, name, entry.Name, Highest(name), occurrence);
                }
                // Activation prerequisites on nodes: a path's first name is looked up in the whole world, each further name
                // inside the node before it (FindSubNodeByName); a path ends with a mode 2 record.
                bool pathStart = true;
                foreach (var prerequisite in entry.References[6])
                {
                    byte mode = prerequisite.Bytes.Length > 4 ? prerequisite.Bytes[4] : (byte)0;
                    if (mode is not (2 or 3)) continue;
                    string name = prerequisite.Text(12, 28);
                    if (pathStart && name.Length > 0) Add(SourceLookup.AnimationPrerequisite, name, entry.Name, Highest(name), occurrence);
                    pathStart = mode == 2;
                }
            }
        }
        return result;
        // ResolveNodeByName's subtree search: depth first, children in order, the node itself first.
        static WorldNode? FirstBelow(WorldNode node, string name)
        {
            HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); Stack<WorldNode> stack = new([node]);
            while (stack.TryPop(out var next))
            {
                if (!seen.Add(next)) continue;
                if (next.Name == name) return next;
                for (int i = next.Children.Count - 1; i >= 0; i--) stack.Push(next.Children[i]);
            }
            return null;
        }
    }

    /// <summary>
    /// Whether any lookup both lists make might find another node: its slot, path or number of candidates differs. When not,
    /// <see cref="Changes"/> has nothing to report and the worlds need not be read.
    /// </summary>
    public static bool Differ(IReadOnlyList<SourceLookup> a, IReadOnlyList<SourceLookup> b)
    {
        var later = b.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First());
        return a.Any(x => later.TryGetValue(x.Key, out var y) && x.MayDiffer(y));
    }

    /// <summary>
    /// The lookups that find another node in <paramref name="after"/> than in <paramref name="before"/>: the nodes are paired by
    /// structure (<see cref="WorldComparer.CompareTree"/>), so slots and node order do not matter, and an indistinguishable copy
    /// counts as the same node. Lookups only one world makes are not changes.
    /// </summary>
    public static IReadOnlyList<SourceLookupChange> Changes(GameZWorld before, IReadOnlyList<SourceLookup> a, GameZWorld after, IReadOnlyList<SourceLookup> b, CancellationToken token = default)
    {
        var later = b.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First());
        // Without any difference in what the lookups find (slot, path and candidates), nothing changed and no pairing is needed.
        var candidates = a.Where(x => later.TryGetValue(x.Key, out var y) && x.MayDiffer(y)).ToList();
        if (candidates.Count == 0) return [];
        // Every node's counterpart, including those of places too many for the comparison's tree.
        var counterpart = WorldComparer.CompareTree(before, after, token: token).Counterparts;
        var nodesBefore = Nodes(before); var nodesAfter = Nodes(after);
        List<SourceLookupChange> changes = [];
        foreach (var x in candidates)
        {
            var y = later[x.Key];
            WorldNode? p = nodesBefore.GetValueOrDefault(x.Slot), q = nodesAfter.GetValueOrDefault(y.Slot);
            bool same = p == null ? q == null : q != null && (ReferenceEquals(counterpart.GetValueOrDefault(p), q) || counterpart.GetValueOrDefault(p) is { } c && WorldComparer.Interchangeable(c, q, 0, token));
            if (!same) changes.Add(new(x, y));
        }
        return changes;
        static Dictionary<int, WorldNode> Nodes(GameZWorld world) => GameZWriter.NodeSlots(world).ToDictionary(p => p.Value, p => p.Key);
    }

    private static string Fingerprint(WorldNode node) =>
        WorldComparer.PairKey(node) + "|" + (node.Class == WorldNodeClass.Object3D && WorldUpdate.LocalMatrix(node) is { } m ? m.ToString() : "") + "|" + Convert.ToHexString(node.Payload.AsSpan(0, Math.Min(node.Payload.Length, 4)));

    /// <summary>A node's path: its first parents' names from the top (a world's name for its members), unnamed nodes as "(unnamed)".</summary>
    public static string Path(WorldNode node)
    {
        List<string> names = []; HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
        for (var n = node; n != null && seen.Add(n) && names.Count < 64; n = n.Parents.FirstOrDefault()) names.Add(n.Name.Length == 0 ? "(unnamed)" : n.Name);
        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>A one-line description of a change for warnings; <paramref name="before"/> says what it is compared with.</summary>
    public static string Describe(SourceLookupChange change, string before = "") =>
        $"{change.After.Mission}: {Describe(change.After)} finds {change.After.Found ?? "no node"}{(change.After.Slot >= 0 ? $" (slot {change.After.Slot})" : "")} instead of {change.Before.Found ?? "no node"}{(change.Before.Slot >= 0 ? $" (slot {change.Before.Slot})" : "")}{before}.";
    /// <summary>What makes a lookup: "FindNode scrollramp8 in gamegen/support/tex_fxm6.gw", "the root scrollramp8 of animation ramp_scroll".</summary>
    public static string Describe(SourceLookup lookup) => lookup.Kind switch
    {
        SourceLookup.TextureEffect => $"FindNode {lookup.Name} in {lookup.Source}",
        SourceLookup.AnimationRoot => $"the root {lookup.Name} of animation {lookup.Source}",
        SourceLookup.AnimationAttachment => $"the attachment {lookup.Name} of animation {lookup.Source}",
        SourceLookup.AnimationPrerequisite => $"the prerequisite node {lookup.Name} of animation {lookup.Source}",
        _ => $"the name {lookup.Name} in animation {lookup.Source}",
    };
}
