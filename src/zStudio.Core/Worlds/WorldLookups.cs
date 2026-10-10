using System.Security.Cryptography;
using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
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
public sealed record SourceLookupChange(SourceLookup Before, SourceLookup After)
{
    /// <summary>
    /// Whether the worlds were too large to tell every copy apart (the comparison paired some copies in order, or ran out of
    /// copy checks): the lookup may still find the same node.
    /// </summary>
    public bool Uncertain { get; init; }
}

/// <summary>
/// What a mission looked up by name in a world (the world file's bytes and its lookups), to compare later builds with. The
/// world is read when a build's lookups first may differ, and kept for the builds after it.
/// </summary>
/// <param name="provenance">Where the world's nodes came from, by slot, when a source build made it.</param>
public sealed class SourceLookupBaseline(ReadOnlyMemory<byte> world, IReadOnlyList<SourceLookup> lookups, IReadOnlyDictionary<int, WorldNodeProvenance>? provenance = null)
{
    private GameZWorld? read;
    public IReadOnlyList<SourceLookup> Lookups { get; } = lookups;
    /// <summary>
    /// The lookups that find another node in <paramref name="after"/> (<see cref="WorldLookups.Changes"/>); no world is read
    /// unless one may differ. With the provenance of both builds (<paramref name="sources"/> for <paramref name="after"/>), a
    /// node an edit moved to another parent is still the node it was.
    /// </summary>
    public IReadOnlyList<SourceLookupChange> Changes(Func<GameZWorld> after, IReadOnlyList<SourceLookup> lookups, CancellationToken token = default, IReadOnlyDictionary<int, WorldNodeProvenance>? sources = null) =>
        WorldLookups.Differ(Lookups, lookups) ? WorldLookups.Changes(World(token), Lookups, after(), lookups, token, provenance is { } before && sources is { } now ? (before, now) : null) : [];
    /// <summary>The baseline's world, read once (only read afterwards, so concurrent comparisons may share it).</summary>
    internal GameZWorld World(CancellationToken token)
    {
        if (Volatile.Read(ref read) is { } known) return known;
        var parsed = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", world.ToArray(), token: token), token);
        return Interlocked.CompareExchange(ref read, parsed, null) ?? parsed;
    }
}

public static class WorldLookups
{
    /// <summary>The script the game runs when it loads a mission (archive mode), which reads the world and sets up texture effects.</summary>
    public static string LoadScript(string mission) => $"{SourceProject.GameGenFolder}/{mission}_zbd.gs";

    /// <summary>
    /// The names FindNode looks up in the scripts the game runs when it loads <paramref name="mission"/>: its load script and
    /// the scripts that one sources, in order, except macros (<c>%worldName%</c>). <paramref name="read"/> gives a project
    /// file's bytes, or null when it does not exist. Commands match as the retail interpreter matches them (case-sensitive
    /// prefixes: <c>source</c>, and <c>FindNode</c> in DispatchCoreCommand 0x4c20a0), and <c>Quit</c> (exactly) ends its script.
    /// Scripts are followed as deep as a build follows them (<see cref="WorldAssembler.MaximumScriptDepth"/> levels) and
    /// read up to <see cref="WorldAssembler.MaximumInstructions"/> instructions in all, each script once. Scripts sourced
    /// deeper, more instructions, and a source line naming its script with a macro (which only the game's run resolves) or
    /// outside the gamegen folder are refused with <see cref="InvalidDataException"/>: the lookups of a script are never
    /// left out unread.
    /// </summary>
    public static IReadOnlyList<(string Source, string Name)> FindNodes(Func<string, byte[]?> read, string mission, CancellationToken token = default)
        => FindNodes(read, mission, token, WorldAssembler.MaximumScriptSourceBytes, GameGenScriptText.MaximumTokens);

    internal static IReadOnlyList<(string Source, string Name)> FindNodes(Func<string, byte[]?> read, string mission, CancellationToken token,
        long maximumScriptBytes, long maximumScriptTokens)
        => FindNodes((path, _) => read(path), mission, token, maximumScriptBytes, maximumScriptTokens);

    /// <summary>Reads lookup scripts with their remaining cumulative source allowance admitted before file allocation.</summary>
    public static IReadOnlyList<(string Source, string Name)> FindNodes(Func<string, long, byte[]?> read, string mission, CancellationToken token = default)
        => FindNodes(read, mission, token, WorldAssembler.MaximumScriptSourceBytes, GameGenScriptText.MaximumTokens);

    internal static IReadOnlyList<(string Source, string Name)> FindNodes(Func<string, long, byte[]?> read, string mission, CancellationToken token,
        long maximumScriptBytes, long maximumScriptTokens)
    {
        if (maximumScriptBytes is < 0 or > WorldAssembler.MaximumScriptSourceBytes) throw new ArgumentOutOfRangeException(nameof(maximumScriptBytes));
        if (maximumScriptTokens is < 0 or > GameGenScriptText.MaximumTokens) throw new ArgumentOutOfRangeException(nameof(maximumScriptTokens));
        List<(string, string)> names = []; HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase); int instructions = 0;
        long scriptBytes = 0, scriptTokens = 0;
        void Run(string path, int depth)
        {
            if (!visited.Add(path)) return;
            if (depth > WorldAssembler.MaximumScriptDepth)
                throw new InvalidDataException($"{path}: the scripts source each other more than {WorldAssembler.MaximumScriptDepth} levels deep, deeper than a build follows them, so the names they look up are not known.");
            token.ThrowIfCancellationRequested();
            if (read(path, Math.Min(SourceProject.MaximumSourceTextBytes, maximumScriptBytes - scriptBytes)) is not { } bytes) return;
            if (bytes.LongLength > maximumScriptBytes - scriptBytes)
                throw new InvalidDataException("The lookup scripts together exceed the 64 MiB source limit, so their node lookups cannot be established.");
            scriptBytes += bytes.Length;
            // Bounded like every script source before it is decoded.
            string text;
            try { text = GameGenScriptText.Decode(bytes, token); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
            long tokensInScript = GameGenScriptText.CountTokens(text, token);
            if (tokensInScript > maximumScriptTokens - scriptTokens)
                throw new InvalidDataException("The lookup scripts together exceed four million tokens, so their node lookups cannot be established.");
            scriptTokens += tokensInScript;
            foreach (var tokens in GameGenScriptText.TokenizeCancellable(text, token))
            {
                token.ThrowIfCancellationRequested();
                if (++instructions > WorldAssembler.MaximumInstructions)
                    throw new InvalidDataException($"{path}: the scripts hold more than {WorldAssembler.MaximumInstructions:N0} instructions, more than a build runs, so the names they look up are not known.");
                if (tokens.Count > 0 && ScriptConditions.IsQuit(tokens[0])) return;
                if (tokens.Count < 2) continue;
                if (ScriptConditions.IsSource(tokens[0]))
                {
                    // A script only the game's run names (a macro), or one outside the gamegen folder, cannot be read here.
                    if (ScriptConditions.HasMacro(tokens[1]))
                        throw new InvalidDataException($"{path} sources a script its macros name ({JsonData.ShownText(tokens[1], 64)}), which only the game's run resolves, so the names that script looks up are not known.");
                    if (Sourced(tokens[1]) is { } sourced) Run(sourced, depth + 1);
                    else if (Outside(tokens[1]))
                        throw new InvalidDataException($"{path} sources {JsonData.ShownText(tokens[1], 64)}, outside the project's {SourceProject.GameGenFolder} folder, so the names that script looks up are not known.");
                }
                else if (tokens[0].StartsWith("FindNode", StringComparison.Ordinal) && !tokens[1].StartsWith('%')) names.Add((path, tokens[1]));
            }
        }
        Run(LoadScript(mission), 0);
        return names;
        // The script a line names inside gamegen (".\" and repeated separators allowed); null for none, or one outside it.
        static string? Sourced(string argument)
        {
            if (System.IO.Path.IsPathRooted(argument)) return null;
            string[] parts = Parts(argument);
            return parts.Length == 0 || parts.Contains("..") ? null : $"{SourceProject.GameGenFolder}/{string.Join('/', parts)}";
        }
        static bool Outside(string argument) => System.IO.Path.IsPathRooted(argument) || Parts(argument).Contains("..");
        static string[] Parts(string argument) => [.. argument.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".")];
    }

    /// <summary>
    /// Every lookup by name <paramref name="mission"/> makes when the game loads it, resolved as the engine resolves it
    /// (LoadZbd's ResolveNodeByName: the attachment's subtree, the root's, then the whole world, highest slot first): the
    /// texture-effect scripts' FindNode, each animation's root (the binding loop's chain), its attachment when the root's
    /// subtree lacks it, and each node name inside an animation that neither subtree has.
    /// </summary>
    public static IReadOnlyList<SourceLookup> Resolve(string mission, GameZWorld world, AnimationPackage? animations, IEnumerable<(string Source, string Name)> findNodes, CancellationToken token = default)
        => Resolve(mission, world, animations, findNodes, token, new LookupWorkBudget(token: token));

    internal static IReadOnlyList<SourceLookup> Resolve(string mission, GameZWorld world, AnimationPackage? animations, IEnumerable<(string Source, string Name)> findNodes,
        CancellationToken token, LookupWorkBudget lookupWork)
    {
        token.ThrowIfCancellationRequested(); lookupWork.Reserve(2L * world.Nodes.Count);
        var slots = GameZWriter.NodeSlots(world);
        var byName = world.Nodes.GroupBy(n => { token.ThrowIfCancellationRequested(); return lookupWork.Name(n); }, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                token.ThrowIfCancellationRequested();
                return g.OrderByDescending(n => slots[n]).ToList();
            }, StringComparer.Ordinal);
        int Count(string name) => byName.TryGetValue(name, out var list) ? list.Count : 0;
        WorldNode? Highest(string name) => byName.TryGetValue(name, out var list) ? list[0] : null;
        List<SourceLookup> result = []; HashSet<(string, string, int, string)> seen = [];
        // Many lookups find one node (66 hit walls attach to one wall1): its path and fingerprint are made once.
        Dictionary<WorldNode, (string Path, string Fingerprint)> described = new(ReferenceEqualityComparer.Instance);
        // A different target can share most descendants with a previous target. Keep the signature memo for this entire
        // immutable lookup snapshot, with the same aggregate allowance as subtree name discovery.
        WorldComparer.PairKeyMemo fingerprints = new(token, lookupWork);
        void Add(string kind, string name, string source, WorldNode? found, int occurrence = 0)
        {
            token.ThrowIfCancellationRequested(); lookupWork.Reserve(1);
            if (!seen.Add((kind, source, occurrence, name))) return;
            (string Path, string Fingerprint)? node = found == null ? null : described.TryGetValue(found, out var known) ? known : described[found] = (Path(found, token, lookupWork), Fingerprint(found, fingerprints));
            result.Add(new(mission, kind, name, source, Count(name), found == null ? -1 : slots[found], node?.Path) { Fingerprint = node?.Fingerprint, Occurrence = occurrence });
        }
        foreach (var (source, name) in findNodes) { token.ThrowIfCancellationRequested(); Add(SourceLookup.TextureEffect, name, source, Highest(name)); }
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
                    attachment = bound == null ? null : lookupWork.FindSub(bound, entry.AttachName, firstChildFirst: true);
                    if (attachment == null) { attachment = Highest(entry.AttachName); Add(SourceLookup.AnimationAttachment, entry.AttachName, entry.Name, attachment, occurrence); }
                }
                // Tracked nodes and node references (LoadZbd skips each table's reserved first record); a name the subtrees
                // lack is the entry's own light or sound before the whole world (ResolveNodeByName).
                HashSet<string>? below = null;
                foreach (var name in new[] { 0, 1 }.SelectMany(table => entry.References[table].Skip(1)).Select(r => r.Text(0, 36)))
                {
                    token.ThrowIfCancellationRequested(); lookupWork.Reserve(1);
                    if (name.Length == 0 || name == root) continue;
                    below ??= [.. lookupWork.Subtree(new[] { bound, attachment }.OfType<WorldNode>()).Select(lookupWork.Name),
                        .. new[] { 2, 3 }.SelectMany(table => entry.References[table].Skip(1)).Select(r => r.Text(0, 36))];
                    if (!below.Contains(name)) Add(SourceLookup.AnimationName, name, entry.Name, Highest(name), occurrence);
                }
                // Activation prerequisites on nodes: a path's first name is looked up in the whole world, each further name
                // inside the node before it (FindSubNodeByName); a path ends with a mode 2 record.
                bool pathStart = true;
                foreach (var prerequisite in entry.References[6])
                {
                    token.ThrowIfCancellationRequested(); lookupWork.Reserve(1);
                    byte mode = prerequisite.Bytes.Length > 4 ? prerequisite.Bytes[4] : (byte)0;
                    if (mode is not (2 or 3)) continue;
                    string name = prerequisite.Text(12, 28);
                    if (pathStart && name.Length > 0) Add(SourceLookup.AnimationPrerequisite, name, entry.Name, Highest(name), occurrence);
                    pathStart = mode == 2;
                }
            }
        }
        return result;
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
    public static IReadOnlyList<SourceLookupChange> Changes(GameZWorld before, IReadOnlyList<SourceLookup> a, GameZWorld after, IReadOnlyList<SourceLookup> b, CancellationToken token = default,
        (IReadOnlyDictionary<int, WorldNodeProvenance> Before, IReadOnlyDictionary<int, WorldNodeProvenance> After)? sources = null)
    {
        var later = b.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First());
        // Without any difference in what the lookups find (slot, path and candidates), nothing changed and no pairing is needed.
        var candidates = a.Where(x => later.TryGetValue(x.Key, out var y) && x.MayDiffer(y)).ToList();
        if (candidates.Count == 0) return [];
        // Every node's counterpart, including those of places too many for the comparison's tree.
        var comparison = WorldComparer.CompareTree(before, after, token: token);
        var counterpart = comparison.Counterparts; bool approximate = comparison.ApproximatePairing || comparison.PairingTruncated;
        var nodesBefore = Nodes(before); var nodesAfter = Nodes(after);
        List<SourceLookupChange> changes = [];
        foreach (var x in candidates)
        {
            var y = later[x.Key];
            WorldNode? p = nodesBefore.GetValueOrDefault(x.Slot), q = nodesAfter.GetValueOrDefault(y.Slot);
            bool @unchecked = false;
            bool same = p == null ? q == null : q != null && (ReferenceEquals(counterpart.GetValueOrDefault(p), q) || counterpart.GetValueOrDefault(p) is { } c && WorldComparer.Interchangeable(c, q, out @unchecked, 0, token));
            if (!same) changes.Add(new(x, y) { Uncertain = approximate || @unchecked });
        }
        return changes;
        static Dictionary<int, WorldNode> Nodes(GameZWorld world) => GameZWriter.NodeSlots(world).ToDictionary(p => p.Value, p => p.Key);
    }

    /// <summary>A digest of the node's structure (its pairing key: classes and children's names six levels down, which grows with the subtree), transform and flags.</summary>
    private static string Fingerprint(WorldNode node, WorldComparer.PairKeyMemo fingerprints) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        fingerprints.Key(node) + "|" + (node.Class == WorldNodeClass.Object3D && WorldUpdate.LocalMatrix(node) is { } m ? m.ToString() : "") + "|" + Convert.ToHexString(node.Payload.AsSpan(0, Math.Min(node.Payload.Length, 4))))));

    /// <summary>A node's path: its first parents' names from the top (a world's name for its members), unnamed nodes as "(unnamed)".</summary>
    public static string Path(WorldNode node) => Path(node, default, null);
    private static string Path(WorldNode node, CancellationToken token, LookupWorkBudget? work)
    {
        List<string> names = []; HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
        for (var n = node; n != null && names.Count < 64; n = n.Parents.FirstOrDefault())
        {
            token.ThrowIfCancellationRequested(); work?.Reserve(1);
            if (!seen.Add(n)) break;
            string name = n.Name; names.Add(name.Length == 0 ? "(unnamed)" : name);
        }
        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>A one-line description of a change for warnings; <paramref name="before"/> says what it is compared with.</summary>
    public static string Describe(SourceLookupChange change, string before = "")
    {
        BoundedDiagnostics note = new(); Describe(note, change, before); return note.Messages[0];
    }
    /// <summary>The report's shared allowance gates even the nested description before it is formatted.</summary>
    internal static void Describe(BoundedDiagnostics notes, SourceLookupChange change, string before = "") =>
        notes.Add($"{change.After.Mission}: {Describe(change.After)} finds {change.After.Found ?? "no node"}{(change.After.Slot >= 0 ? $" (slot {change.After.Slot})" : "")} instead of {change.Before.Found ?? "no node"}{(change.Before.Slot >= 0 ? $" (slot {change.Before.Slot})" : "")}{before}{(change.Uncertain ? " (possibly: the worlds are too large to tell every copy apart)" : "")}.");
    /// <summary>What makes a lookup: "FindNode scrollramp8 in gamegen/support/tex_fxm6.gw", "the root scrollramp8 of animation ramp_scroll".</summary>
    public static string Describe(SourceLookup lookup) => lookup.Kind switch
    {
        SourceLookup.TextureEffect => $"FindNode {JsonData.ShownText(lookup.Name, 64)} in {JsonData.ShownText(lookup.Source, 192)}",
        SourceLookup.AnimationRoot => $"the root {JsonData.ShownText(lookup.Name, 64)} of animation {JsonData.ShownText(lookup.Source, 192)}",
        SourceLookup.AnimationAttachment => $"the attachment {JsonData.ShownText(lookup.Name, 64)} of animation {JsonData.ShownText(lookup.Source, 192)}",
        SourceLookup.AnimationPrerequisite => $"the prerequisite node {JsonData.ShownText(lookup.Name, 64)} of animation {JsonData.ShownText(lookup.Source, 192)}",
        _ => $"the name {JsonData.ShownText(lookup.Name, 64)} in animation {JsonData.ShownText(lookup.Source, 192)}",
    };
}
