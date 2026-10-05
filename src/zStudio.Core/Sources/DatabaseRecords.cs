using System.Buffers.Binary;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Recovers a mission database's files from a shipped world: the database and the parts it referenced (files of their
/// own), their records in order with the group records the build deleted, and the references that named a file by
/// another path (also in the model files later loads read).
/// <para>
/// The build gave every node the next slot of the engine's node table as the original loader created it
/// (<see cref="OriginalLoader"/>), loaded the database, whose objects joined the world, and deleted its root with the
/// groups (<c>DeleteTree %dbName%</c>). The nodes made after the database took the freed slots, last freed first, so their
/// slots give the free list the deletion left, once it is known which later load made which nodes: identical loads and
/// a file a later load names twice by different paths look alike in the world, so each reading is tried. The parts
/// model (<see cref="Parts"/>) reads the database from that list. Otherwise the database as one file is tried: its next
/// record is the object that takes the slot the table gives next, or else a group. A result that does not give every
/// node its shipped slot is noted; without one, the database keeps its objects in the order the world lists them.
/// </para>
/// </summary>
internal static partial class DatabaseRecords
{
    /// <summary>
    /// The database file's top-level records in order; the group nodes the build deleted (also those in its parts); the
    /// references to its parts (files of their own) with the content copied from each; and the model references that
    /// named their file by another path than its first reference (the n-th path).
    /// </summary>
    internal sealed record Records(IReadOnlyList<WorldNode> Roots, IReadOnlySet<WorldNode> Groups, IReadOnlyDictionary<WorldNode, IReadOnlyList<WorldNode>> Parts, IReadOnlyDictionary<WorldNode, int> SecondPaths)
    {
        /// <summary>
        /// References in model files (read by later loads or referenced by the database) that named their file by another
        /// path than the file's other references to it (so the build cached it again): the model file's name, the
        /// referenced file's name, and which of that name's references in record order (from 0).
        /// </summary>
        public IReadOnlyList<(string LoadFile, string Reference, int Occurrence)> LaterPaths { get; init; } = [];
    }

    /// <summary>The engine's node table: the free list on top of the slots never used, in order.</summary>
    private sealed class Table
    {
        public Stack<int> Free = new();
        public int Next;
        public Dictionary<WorldNode, int> Slots { get; } = new(ReferenceEqualityComparer.Instance);
        public int Peek() => Free.Count > 0 ? Free.Peek() : Next;
        public int Take(WorldNode node) { int s = Free.Count > 0 ? Free.Pop() : Next++; Slots[node] = s; return s; }
        public void Release(WorldNode node) => Free.Push(Slots[node]);
    }

    /// <param name="isReference">Whether a node of the shipped world is an external reference (its children a referenced file's content).</param>
    public static Records? Infer(GameZWorld world, WorldDecomposition build, Func<WorldNode, bool> isReference, string mission, List<string> notes, CancellationToken token)
    {
        var slot = GameZWriter.NodeSlots(world);
        Records? Fail(string reason) { notes.Add($"{mission}: the mission database keeps the world's object order without its groups: {reason}"); return null; }
        var database = build.Loads.FirstOrDefault(l => l.Database);
        if (database == null || database.Content.Count == 0) return null;
        // The steps that made nodes, in order: script loads (root and file) and the other creating commands.
        List<(int Step, LoadedModel? Load, WorldNode? Node)> Steps(IEnumerable<LoadedModel> loads) => [.. loads.Where(l => !l.Database && l.Root != null).Select(l => (l.Step, Load: (LoadedModel?)l, Node: (WorldNode?)null))
            .Concat(build.Created.Select(c => (Step: c.Key, Load: (LoadedModel?)null, Node: (WorldNode?)c.Value))).OrderBy(s => s.Step)];
        var steps = Steps(build.Loads);
        IReadOnlyList<WorldNode> Content(WorldNode node) => isReference(node) ? node.Children : [];
        string File(WorldNode node) => node.Name.ToLowerInvariant();
        string? mismatch = null;
        void Check(WorldNode node, int taken) { if (mismatch == null && slot.TryGetValue(node, out int shipped) && shipped != taken) mismatch = $"{node.Name} would take slot {taken}, not {shipped}"; }

        // 1. Before the database: the slots from the first, as the nodes were made.
        Table before = new();
        foreach (var (_, load, node) in steps.Where(s => s.Step < database.Step))
        {
            token.ThrowIfCancellationRequested();
            if (load != null) OriginalLoader.Load(load.Root!, load.Content, load.Content, new() { Allocate = n => Check(n, before.Take(n)), Free = before.Release, Content = Content, File = File, Token = token });
            else Check(node!, before.Take(node!));
        }
        if (mismatch != null) return Fail($"before it, {mismatch}.");

        // 2. After the deletion the free list held every slot below the database's end that no live node holds, in an
        //    order the later nodes reveal: each entry is unknown until a node of the world takes it.
        HashSet<int> objectSlots = [.. WorldAssembler.Subtree(database.Content).Select(n => slot[n])];
        if (objectSlots.Count == 0) return Fail("it has no records.");
        int end = objectSlots.Max() + 1;
        HashSet<int> liveBefore = [.. before.Slots.Where(p => slot.ContainsKey(p.Key)).Select(p => p.Value)];
        List<int> freed = [.. Enumerable.Range(0, end).Where(s => !objectSlots.Contains(s) && !liveBefore.Contains(s))];
        HashSet<int> freedSet = [.. freed];
        var freedSlots = world.FreedSlots;
        var first = FreeList(steps, out string? failure);
        if (first == null) return Fail(failure!);
        if (first.Count == 0) return Fail("the deletion freed no slot for its records.");
        List<int> list = first;

        // 3. The parts model; when it gives every node its shipped slot it is the database's files.
        // The later loads as the slots alone cannot tell them apart: identical loads (one file under one name) the other
        // way round, and a file a later load names several times named once by another path (cached again). Each variant
        // gives another free list; the one the database's build reproduces best is kept. The variants are tried with the
        // quick starting guesses first, and only then with the search for where the caches end (see Parts.Infer).
        List<(LoadedModel A, LoadedModel B)?> swaps = [null, .. Interchangeable().Where(p => slot[p.A.Root!] == list[0] || slot[p.B.Root!] == list[0]).Select(p => ((LoadedModel, LoadedModel)?)p)];
        List<(string LoadFile, string Reference, int Occurrence)?> paths = [null, .. RepeatedReferences().Select(r => ((string, string, int)?)r)];
        // Each variant's free list once, when first needed (null when its later loads do not reproduce the world's slots).
        Dictionary<(int, int), List<int>?> computed = new() { [(0, 0)] = list };
        IEnumerable<(List<int> List, (string LoadFile, string Reference, int Occurrence)? Path)> Variants()
        {
            for (int i = 0; i < swaps.Count; i++)
                for (int j = 0; j < paths.Count; j++)
                {
                    if (!computed.TryGetValue((i, j), out var variant))
                    {
                        var swap = swaps[i]; var path = paths[j];
                        var loads = swap is var (a, b) ? build.Loads.Select(l => ReferenceEquals(l, a) ? a with { Root = b.Root, Content = b.Content } : ReferenceEquals(l, b) ? b with { Root = a.Root, Content = a.Content } : l).ToList() : [.. build.Loads];
                        HashSet<WorldNode> other = path is { } p ? Occurrences(loads, p) : new(ReferenceEqualityComparer.Instance);
                        computed[(i, j)] = variant = FreeList(Steps(loads), out _, other);
                    }
                    if (variant != null) yield return (variant, paths[j]);
                }
        }
        // A world that kept slots free keeps their last names (1999 m6): the first cache its database's load made, freed first.
        Dictionary<int, string> kept = [];
        foreach (var (s, bytes) in freedSlots) if (NameOf(bytes) is { Length: > 0 } name) kept[s] = name;
        Parts? parts = null;
        List<(string LoadFile, string Reference, int Occurrence)> laterPaths = [];
        foreach (bool search in new[] { false, true })
        {
            foreach (var (variant, path) in Variants())
            {
                if (Parts.Infer(slot, isReference, database, variant, before, mission, token, search, kept) is { } tried && (parts == null || tried.Inexact == null || tried.Matched > parts.Matched))
                { parts = tried; list = variant; laterPaths = path is { } chosen ? [chosen] : []; }
                if (parts is { Inexact: null }) break;
            }
            if (parts is { Inexact: null }) break;
        }
        // The second path a later load's free list needs also serves the file's later references that copy its cache,
        // whichever reading of the database below is kept: they all build on that free list.
        var followed = Follow(laterPaths);
        if (parts != null && parts.Inexact == null)
        {
            parts.FollowModels();
            return FromParts(parts, world, mission) with { LaterPaths = [.. followed, .. parts.FilePaths] };
        }
        // 4. Otherwise the database as one file, when its order gives every node its shipped slot; else the parts as far
        //    as they go, or the world's object order.
        var single = Single(out string? reason);
        if (single != null) return single with { LaterPaths = followed };
        if (parts != null)
        {
            notes.Add($"{mission}: the mission database is reconstructed with {parts.Content.Count} parts in files of their own, but not every node takes its shipped slot when built: {parts.Inexact}.");
            parts.FollowModels();
            return FromParts(parts, world, mission) with { LaterPaths = [.. followed, .. parts.FilePaths] };
        }
        return SlotOrder(reason!);

        // 5. Otherwise the slots alone: the objects and a group at every slot the deletion freed, in slot order. An object
        //    made later took a later slot, so of objects that share a name the shipped one is still made last and found
        //    first by name; the later loads reuse the groups' slots below the database's objects, as they did in the game.
        Records SlotOrder(string why)
        {
            notes.Add($"{mission}: the mission database keeps its objects in the order of their shipped slots, with a group at each slot the build freed, since its exact order is not known: {why}");
            int groupCount = 0;
            List<(int Slot, WorldNode Node)> sequence = [.. database.Content.Select(o => (slot[o], o))];
            HashSet<WorldNode> placeholders = new(ReferenceEqualityComparer.Instance);
            foreach (int s in freed)
            {
                var group = Group();
                group.Name = freedSlots.TryGetValue(s, out var bytes) && NameOf(bytes) is { Length: > 0 } kept ? kept : $"group{++groupCount}";
                placeholders.Add(group); sequence.Add((s, group));
            }
            return new([.. sequence.OrderBy(e => e.Slot).Select(e => e.Node)], placeholders, new Dictionary<WorldNode, IReadOnlyList<WorldNode>>(ReferenceEqualityComparer.Instance), new Dictionary<WorldNode, int>(ReferenceEqualityComparer.Instance));
        }

        // Files later loads read that name another file more than once: each later reference to it could have been
        // another path (the reference's position among that name's references in the file's records).
        IEnumerable<(string LoadFile, string Reference, int Occurrence)> RepeatedReferences()
        {
            HashSet<(string, string, int)> seen = [];
            foreach (var load in build.Loads.Where(l => !l.Database && l.Root != null && l.Step > database.Step))
                foreach (var group in OwnReferences(load).GroupBy(File))
                    for (int k = 1; k < group.Count(); k++)
                        if (seen.Add((load.File.ToLowerInvariant(), group.Key, k))) yield return (load.File.ToLowerInvariant(), group.Key, k);
        }
        // A load's references in record order, not looking inside their content.
        List<WorldNode> OwnReferences(LoadedModel load)
        {
            List<WorldNode> found = [];
            void Walk(WorldNode node) { if (isReference(node)) { found.Add(node); return; } foreach (var child in node.Children) Walk(child); }
            foreach (var record in load.Content) Walk(record);
            return found;
        }
        // A later load's second path also serves the file's later references that copy its cache (share its models).
        List<(string LoadFile, string Reference, int Occurrence)> Follow(List<(string LoadFile, string Reference, int Occurrence)> chosen)
        {
            HashSet<(string, string, int)> all = [.. chosen];
            foreach (var (loadFile, reference, occurrence) in chosen)
                foreach (var load in build.Loads.Where(l => !l.Database && l.Root != null && l.Step > database.Step && l.File.Equals(loadFile, StringComparison.OrdinalIgnoreCase)))
                {
                    var named = OwnReferences(load).Where(r => File(r) == reference).ToList();
                    for (int k = occurrence + 1; k < named.Count; k++)
                        if (SharesModels(WorldAssembler.Subtree(named[occurrence]), WorldAssembler.Subtree(named[k]))) all.Add((loadFile, reference, k));
                }
            return [.. all];
        }
        // The references a path variant names, in every later load of the file.
        HashSet<WorldNode> Occurrences(IEnumerable<LoadedModel> loads, (string LoadFile, string Reference, int Occurrence) path)
        {
            HashSet<WorldNode> found = new(ReferenceEqualityComparer.Instance);
            foreach (var load in loads.Where(l => !l.Database && l.Root != null && l.Step > database.Step && l.File.Equals(path.LoadFile, StringComparison.OrdinalIgnoreCase)))
                if (OwnReferences(load).Where(r => File(r) == path.Reference).ElementAtOrDefault(path.Occurrence) is { } reference) found.Add(reference);
            return found;
        }

        // Later loads of one file under one name whose nodes have the same names and shape.
        IEnumerable<(LoadedModel A, LoadedModel B)> Interchangeable()
        {
            var later = build.Loads.Where(l => !l.Database && l.Root != null && l.Step > database.Step).ToList();
            Shapes names = new(n => n.Name);
            var shapes = later.Select(l => names.Of([l.Root!])).ToList();
            for (int i = 0; i < later.Count; i++)
                for (int j = i + 1; j < later.Count; j++)
                    if (string.Equals(later[i].File, later[j].File, StringComparison.OrdinalIgnoreCase) && later[i].NodeName == later[j].NodeName && shapes[i] == shapes[j])
                        yield return (later[i], later[j]);
        }

        // The free list the deletion left, from its top, as the later steps reveal it.
        List<int>? FreeList(List<(int Step, LoadedModel? Load, WorldNode? Node)> steps, out string? failure, HashSet<WorldNode>? otherPaths = null)
        {
        failure = null; mismatch = null;
        Dictionary<int, int> entries = []; HashSet<int> claimed = [];
        Stack<int> pushed = new(); int taken = 0, next = end;
        Dictionary<WorldNode, int> marks = new(ReferenceEqualityComparer.Instance);
        void Take(WorldNode node)
        {
            // A pushed slot first, else the next unknown entry of the list, else a slot never used. Unknown entries are -(k + 1).
            int s = pushed.Count > 0 ? pushed.Pop() : taken < freed.Count ? -(++taken) : next++;
            marks[node] = s;
            if (!slot.TryGetValue(node, out int shipped) || mismatch != null) return;
            if (s < 0 && !entries.ContainsKey(-s - 1))
            {
                if (freedSet.Contains(shipped) && claimed.Add(shipped)) entries[-s - 1] = shipped;
                else mismatch = $"{node.Name} takes a freed slot, but its slot {shipped} is not one";
            }
            else if ((s < 0 ? entries[-s - 1] : s) != shipped) mismatch = $"{node.Name} would take slot {(s < 0 ? entries[-s - 1] : s)}, not {shipped}";
        }
        foreach (var (_, load, node) in steps.Where(s => s.Step > database.Step))
        {
            token.ThrowIfCancellationRequested();
            if (load != null) OriginalLoader.Load(load.Root!, load.Content, load.Content, new() { Allocate = Take, Free = n => pushed.Push(marks[n]), Content = Content, File = n => otherPaths != null && otherPaths.Contains(n) ? File(n) + "#2" : File(n), Token = token });
            else Take(node!);
        }
        if (mismatch != null) { failure = $"after it, {mismatch}."; return null; }
        if (pushed.Any(s => s < 0 && !entries.ContainsKey(-s - 1)) || Enumerable.Range(0, taken).Any(k => !entries.ContainsKey(k) && !pushed.Contains(-(k + 1))))
            { failure = "a freed slot was taken only by nodes the world no longer holds."; return null; }
        // The rest of the list is in the file's own free list, from its head: what the later loads freed and nothing took
        // again, then the entries no later node took.
        List<int> chain = [];
        for (int s = world.FreeHead ?? -1; freedSlots.ContainsKey(s) && chain.Count <= freedSlots.Count; s = (int)(BinaryPrimitives.ReadUInt32LittleEndian(freedSlots[s].AsSpan(GameZWriter.NodeSlotSize - 4)) & 0x00FFFFFF))
            chain.Add(s);
        List<int> leftover = [.. pushed.Select(s => s < 0 ? entries[-s - 1] : s).Where(s => s < end)];
        if (!chain.Take(leftover.Count).SequenceEqual(leftover)) { failure = "the file's free list does not begin with the slots the later loads freed."; return null; }
        List<int> untaken = [.. chain.Skip(leftover.Count).Take(freed.Count - taken)];
        if (untaken.Count != freed.Count - taken || untaken.Any(s => !freedSet.Contains(s) || !claimed.Add(s))) { failure = "the file's free list does not hold the freed slots no later node took."; return null; }
        // The list the deletion left, from its top.
        return [.. Enumerable.Range(0, taken).Select(k => entries[k]), .. untaken];
        }

        Records? Single(out string? reason)
        {
        reason = null;
        // The database load replayed: its records are the objects in the order the table hands out their slots, with a
        //    group wherever the next slot is no object's. Which references are inline copies, and so which files are
        //    cached, is refined in rounds.
        List<WorldNode> order = [.. database.Content.OrderBy(o => slot[o])];
        HashSet<WorldNode> inline = new(ReferenceEqualityComparer.Instance);
        var bySlot = database.Content.ToDictionary(o => slot[o]);
        for (int round = 0; ; round++)
        {
            token.ThrowIfCancellationRequested();
            mismatch = null;
            int inlineBefore = inline.Count;
            Table table = new() { Free = new(before.Free.Reverse()), Next = before.Next };
            HashSet<WorldNode> decided = new(ReferenceEqualityComparer.Instance);
            // An inline copy's content follows it at once, as records of the database; a reference's content waits for the next record.
            IReadOnlyList<WorldNode> DatabaseContent(WorldNode node)
            {
                if (!isReference(node) || inline.Contains(node)) return [];
                if (table.Slots.ContainsKey(node) && decided.Add(node) && node.Children.Count > 0 && table.Peek() == slot[node.Children[0]]) { inline.Add(node); return []; }
                return node.Children;
            }
            WorldNode root = new(database.NodeName, WorldNodeClass.Object3D);
            List<WorldNode> records = [], groups = []; HashSet<WorldNode> placed = new(ReferenceEqualityComparer.Instance);
            IEnumerable<WorldNode> Records()
            {
                while (placed.Count < order.Count && mismatch == null && groups.Count <= end)
                {
                    if (bySlot.TryGetValue(table.Peek(), out var o) && placed.Add(o)) { records.Add(o); yield return o; }
                    else { WorldNode group = Group(); groups.Add(group); records.Add(group); yield return group; }
                }
            }
            OriginalLoader.Load(root, Records(), order, new() { Allocate = n => Check(n, table.Take(n)), Free = table.Release, Content = DatabaseContent, File = File, Token = token });
            List<WorldNode> recorded = [.. records.Where(r => !groups.Contains(r))];
            bool settled = inline.Count == inlineBefore && recorded.SequenceEqual(order);
            order = [.. recorded, .. order.Where(o => !placed.Contains(o))];
            if (mismatch != null || placed.Count < bySlot.Count)
            {
                if (settled || round >= 12) { reason = $"in it, {mismatch ?? "an object never takes the next slot"}."; return null; }
                continue;
            }
            if (!settled && round < 12) continue;
            // 4. The deletion freed the groups and the root (children first) on top of what the load left free.
            List<int> left = [.. table.Free];
            if (table.Next != end || list.Count != groups.Count + 1 + left.Count || !list.Skip(groups.Count + 1).SequenceEqual(left))
                { reason = "the slots its load left free are not the ones the later nodes took."; return null; }
            List<int> post = [.. list.Take(groups.Count + 1).Reverse()];
            List<int> pre = [table.Slots[root], .. groups.Select(g => table.Slots[g])];
            Dictionary<int, int> position = []; for (int i = 0; i < post.Count; i++) position[post[i]] = i;
            Dictionary<int, int> parent = [];
            if (!pre.ToHashSet().SetEquals(post) || !Tree(0, pre.Count - 1, 0, post.Count - 1)) { reason = "its freed slots do not form one tree of groups."; return null; }
            bool Tree(int a, int b, int c, int d)
            {
                if (pre[a] != post[d]) return false;
                int i = a + 1, j = c;
                while (i <= b)
                {
                    if (!position.TryGetValue(pre[i], out int q) || q < j || q >= d || i + (q - j) > b) return false;
                    parent[pre[i]] = pre[a];
                    if (!Tree(i, i + (q - j), j, q)) return false;
                    i += q - j + 1; j = q + 1;
                }
                return j == d;
            }
            // 5. The records nested as they were made: an object belongs to the innermost group open when it was made. A
            //    group left free in the file keeps the name it had there.
            Dictionary<int, WorldNode> groupAt = groups.ToDictionary(g => table.Slots[g]);
            int count = 0;
            foreach (var group in groups)
                group.Name = freedSlots.TryGetValue(table.Slots[group], out var bytes) && NameOf(bytes) is { Length: > 0 } name ? name : $"group{++count}";
            int rootSlot = table.Slots[root];
            WorldNode Holder(int s) => s == rootSlot ? root : groupAt[s];
            Stack<int> open = new([rootSlot]);
            foreach (var record in records)
            {
                int s = table.Slots[record];
                if (groupAt.ContainsKey(s))
                {
                    while (open.Count > 0 && open.Peek() != parent[s]) open.Pop();
                    if (open.Count == 0) { reason = "a group opens outside its parent."; return null; }
                    Holder(open.Peek()).Children.Add(groupAt[s]);
                    open.Push(s);
                }
                else Holder(open.Peek()).Children.Add(record);
            }
            if (inline.Count > 0) { reason = "its objects copy files inline, which are parts of it the build cannot find"; return null; }
            return new([.. root.Children], new HashSet<WorldNode>(groups, ReferenceEqualityComparer.Instance), new Dictionary<WorldNode, IReadOnlyList<WorldNode>>(ReferenceEqualityComparer.Instance), new Dictionary<WorldNode, int>(ReferenceEqualityComparer.Instance));
        }
        }
    }

    /// <summary>
    /// The records of the parts model. Deleted nodes keep the names their freed slots still hold, else <c>groupN</c>; a
    /// reference to a part is named for the mission database and its part, <c>mN_NN.flt</c> (the files' names are lost),
    /// with the same name for references the replay cached as one: copies of one cache, one file.
    /// </summary>
    private static Records FromParts(Parts parts, GameZWorld world, string mission)
    {
        parts.OrderChildren();
        var freedSlots = world.FreedSlots;
        int groupCount = 0;
        var made = parts.Made.Where(p => !ReferenceEquals(p.Key, parts.Root) && p.Key.Name != "end").OrderBy(p => p.Value).ToList();
        // Which references copy one cache, before any of the copies' groups is renamed.
        Dictionary<WorldNode, string> keys = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, _) in made) if (parts.Content.ContainsKey(node)) keys[node] = parts.CacheKey(node);
        Dictionary<string, string> partNames = [];
        HashSet<WorldNode> groups = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, at) in made)
        {
            groups.Add(node);
            if (keys.TryGetValue(node, out var key))
            {
                if (!partNames.TryGetValue(key, out var name)) partNames[key] = name = $"{mission}_{partNames.Count + 1:D2}.flt";
                node.Name = name;
            }
            else node.Name = freedSlots.TryGetValue(at, out var bytes) && NameOf(bytes) is { Length: > 0 } kept ? kept : $"group{++groupCount}";
            // A deleted record: an object3d with no geometry and no transform, carrying the default flags.
            var shape = Group(); node.Flags = shape.Flags; node.Zone = shape.Zone; node.BoundsFlags = shape.BoundsFlags; node.SetPayloadInt(0, 0x28);
        }
        return new([.. parts.Root.Children], groups,
            parts.Content.ToDictionary<KeyValuePair<WorldNode, List<WorldNode>>, WorldNode, IReadOnlyList<WorldNode>>(p => p.Key, p => p.Value, ReferenceEqualityComparer.Instance),
            new Dictionary<WorldNode, int>(parts.SecondPaths, ReferenceEqualityComparer.Instance));
    }

    /// <summary>Whether two copies share a model: copies of one cache share its models, and another cache has models of its own.</summary>
    private static bool SharesModels(IEnumerable<WorldNode> a, IEnumerable<WorldNode> b)
    {
        HashSet<WorldModel> models = new(ReferenceEqualityComparer.Instance);
        foreach (var n in a) if (n.Model != null) models.Add(n.Model);
        return models.Count > 0 && b.Any(n => n.Model != null && models.Contains(n.Model));
    }

    /// <summary>
    /// Numbers subtrees alike when their nodes' labels and shapes are alike. The numbers stay comparable from call to call,
    /// and a call numbers each node once: a node reached along several edges (an instance) is not spelled out once per path.
    /// </summary>
    private sealed class Shapes(Func<WorldNode, string> label)
    {
        private readonly Dictionary<string, int> numbers = new(StringComparer.Ordinal);
        /// <summary>The subtrees of <paramref name="nodes"/> as they are now.</summary>
        public string Of(IEnumerable<WorldNode> nodes)
        {
            Dictionary<WorldNode, int> known = new(ReferenceEqualityComparer.Instance);
            return string.Join(";", nodes.Select(n => Of(n, known)));
        }
        /// <param name="known">The nodes numbered so far, while none of them changes.</param>
        public int Of(WorldNode node, Dictionary<WorldNode, int> known)
        {
            if (known.TryGetValue(node, out int number)) return number;
            string own = label(node);
            System.Text.StringBuilder text = new(); text.Append(own.Length).Append(':').Append(own);
            foreach (var child in node.Children) text.Append(',').Append(Of(child, known));
            string key = text.ToString();
            if (!numbers.TryGetValue(key, out number)) numbers[key] = number = numbers.Count;
            return known[node] = number;
        }
    }

    private static string NameOf(byte[] slotBytes) => new WorldNode("", WorldNodeClass.Object3D) { NameField = slotBytes[..36] }.Name;

    /// <summary>A group record: an object3d with no geometry and no transform.</summary>
    private static WorldNode Group()
    {
        WorldNode node = new("group", WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried, Zone = WorldGltf.DefaultZone, BoundsFlags = 4 };
        node.SetPayloadInt(0, 0x28);
        return node;
    }
}
