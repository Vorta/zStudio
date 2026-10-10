using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

internal static partial class DatabaseRecords
{
    /// <summary>
    /// The mission database as its files had it. The database file referenced other files holding parts of the world
    /// (OpenFlight external references), and the build copied each part in from a cache of it like any model: after the
    /// next record, or at once when the reference has records of its own. The parts and the files they reference were
    /// cached before the database's root in the order of their first references, a file named by two paths twice.
    /// <para>
    /// The database's nodes took slots in the order the loader made them: first the slots its caches' own loads freed and
    /// nothing took again (last freed first), then fresh ones. The deletion of its root freed the groups and the
    /// references to parts (children first), so the slots and the deletion's order give the tree of what was deleted. A
    /// part's copy shows where its references were copied whole (their content follows them at once); its cache freed the
    /// slots the shipped free list holds, in the cache order. That settles where a copy ends, which groups its objects
    /// followed rather than belonged to, which parts reference parts, and which references named a file by a second path
    /// (also inside the files the database references).
    /// </para>
    /// <para>
    /// The caches' frees are matched in order, and their simulation leaves the slots the records start with; the
    /// records are read again in that order until it holds (see <see cref="Infer"/>).
    /// </para>
    /// </summary>
    internal sealed class Parts
    {
        private readonly IReadOnlyDictionary<WorldNode, int> slot;
        private readonly Func<WorldNode, bool> isModelReference;
        private readonly Dictionary<int, WorldNode> live = [], top = [];
        private readonly List<int> order;
        private readonly Dictionary<int, int> position = [];
        private readonly HashSet<int> freed;
        private readonly Dictionary<int, int> deletedParent = [];
        private readonly Dictionary<int, List<int>> deletedChildren = [];
        private readonly string mission;
        private readonly CancellationToken token;
        /// <summary>The inference's budget for mirrored cache nodes, shared by every reading it tries.</summary>
        private readonly OriginalLoader.MirrorBudget budget;
        private readonly RetainedMatchBudget retainedBudget;

        /// <summary>The database's root: its records are its children.</summary>
        public WorldNode Root { get; }
        /// <summary>Deleted nodes the inference made (groups, references to parts, copied groups), with their shipped slots.</summary>
        public Dictionary<WorldNode, int> Made { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>References to parts, with the content copied from each part's file (its other children are its records).</summary>
        public Dictionary<WorldNode, List<WorldNode>> Content { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>Model references naming their file by another path than its first reference: the n-th path.</summary>
        public Dictionary<WorldNode, int> SecondPaths { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>
        /// Model files that named a file they reference several times once by another path (their own load cached it
        /// again): the file, the referenced file's name, and which of the file's references to that name (from 0).
        /// </summary>
        public HashSet<(string File, string Reference, int Occurrence)> FilePaths { get; } = [];
        /// <summary>Why the replay does not give every node its shipped slot, or null when it does.</summary>
        public string? Inexact { get; private set; }
        /// <summary>After the caches: the slots their loads freed and nothing took again (last freed first), and the high-water mark.</summary>
        public List<int>? Leftover { get; private set; }
        public int HighWater { get; private set; }
        /// <summary>The slot the database's root takes after the caches.</summary>
        public int ExpectedRoot => Leftover is { Count: > 0 } leftover ? leftover[0] : HighWater;
        /// <summary>How many nodes the replay gave their shipped slots before the first that missed.</summary>
        public int Matched { get; private set; }
        /// <summary>How many entries of the free list, from its bottom, the caches (and what was freed before them) account for.</summary>
        public int Consumed { get; private set; }
        public int EndSlot { get; private set; } = -1;

        private readonly Dictionary<WorldNode, WorldNode> nextRecord = new(ReferenceEqualityComparer.Instance);
        /// <summary>How many walks and cache simulations the search for where the caches end may run (a few seconds at most).</summary>
        private const int MaximumBoundaryAttempts = 24;
        /// <summary>How many of those the first cache read from the free list may run: the true boundary settles at once.</summary>
        private const int MaximumRetainedAttempts = 6;

        /// <param name="order">The slots the database load took after its caches, in order: its root first.</param>
        /// <param name="deletion">The deletion's part of the free list, from its top.</param>
        /// <param name="tolerant">Whether nodes the order leaves out (records made from slots it does not know yet) are passed over.</param>
        /// <param name="nested">Whether the deletion's tree nests by the order nodes were freed in (else by the order they were made in).</param>
        private Parts(IReadOnlyDictionary<WorldNode, int> slot, Func<WorldNode, bool> isModelReference, LoadedModel database, IReadOnlyList<int> order, List<int> deletion, string mission, OriginalLoader.MirrorBudget budget, RetainedMatchBudget retainedBudget, CancellationToken token, bool tolerant = false, bool nested = true, Retained? prefix = null)
        {
            this.slot = slot; this.isModelReference = isModelReference; this.mission = mission; this.budget = budget; this.token = token; this.tolerant = tolerant; this.prefix = prefix;
            this.retainedBudget = retainedBudget;
            token.ThrowIfCancellationRequested();
            deletedSlots = [.. deletion];
            foreach (var o in database.Content) top[slot[o]] = o;
            foreach (var n in WorldAssembler.Subtree(database.Content)) { token.ThrowIfCancellationRequested(); live[slot[n]] = n; }
            int root = order[0];
            this.order = [.. order];
            for (int i = 0; i < order.Count; i++) position[order[i]] = i;
            freed = [.. order.Where(s => !live.ContainsKey(s))];
            // The deletion's tree: allocation order and the deletion's post-order.
            var post = deletion.AsEnumerable().Reverse().ToList();
            Dictionary<int, int> postIndex = []; for (int i = 0; i < post.Count; i++) postIndex[post[i]] = i;
            var pre = order.Where(postIndex.ContainsKey).ToList();
            // A deleted node's parent is the node made before it that was freed next after it: the innermost one still
            // open. Made order is not the tree's preorder, since a reference's content is copied after the next record.
            SortedSet<int> made = [];
            if (!nested)
            {
                // The older reading: made order as the tree's preorder (a reference's delayed copy then hangs under the
                // record after it, which the walk still places as a copy).
                int at = 0;
                void Build(int depth = 0)
                {
                    token.ThrowIfCancellationRequested();
                    CheckDepth(depth);
                    int node = pre[at++]; deletedChildren[node] = []; int end = postIndex[node];
                    while (at < pre.Count && postIndex[pre[at]] < end) { deletedParent[pre[at]] = node; deletedChildren[node].Add(pre[at]); Build(depth + 1); }
                }
                if (pre.Count > 0) Build();
                Covered = at == pre.Count && pre.Count > 0 && pre[0] == root;
                Root = new WorldNode("root", WorldNodeClass.Object3D);
                Made[Root] = root;
                return;
            }
            foreach (int node in pre)
            {
                token.ThrowIfCancellationRequested();
                deletedChildren[node] = [];
                if (node != root)
                {
                    // Counting a range walks its entire contents; only its first entry is needed.
                    using var after = made.GetViewBetween(postIndex[node] + 1, int.MaxValue).GetEnumerator();
                    if (!after.MoveNext()) break;
                    int parent = post[after.Current];
                    deletedParent[node] = parent; deletedChildren[parent].Add(node);
                }
                made.Add(postIndex[node]);
            }
            // The tree must free its nodes in the deletion's order (children first, in the order they were made).
            List<int> freedOrder = [];
            void Post(int node, int depth = 0) { token.ThrowIfCancellationRequested(); CheckDepth(depth); foreach (int child in deletedChildren.GetValueOrDefault(node, [])) Post(child, depth + 1); freedOrder.Add(node); }
            if (pre.Count > 0 && pre[0] == root) Post(root);
            HashSet<int> inOrder = [.. pre];
            Covered = pre.Count > 0 && pre[0] == root && deletedParent.Count == pre.Count - 1 && (tolerant || freedOrder.SequenceEqual(post.Where(inOrder.Contains)));
            Root = new WorldNode("root", WorldNodeClass.Object3D);
            Made[Root] = root;
        }

        private bool Covered { get; }
        private readonly bool tolerant;
        /// <summary>The slots the database's deletion freed: its deleted records.</summary>
        private readonly HashSet<int> deletedSlots;
        /// <summary>The first cache, read from the free list, whose copy the fresh slots continue (a tolerant walk's hypothesis).</summary>
        private readonly Retained? prefix;
        /// <summary>A reference to <see cref="prefix"/>'s file, as the walk's first reference.</summary>
        private WorldNode? prefixReference;

        /// <summary>
        /// The parts model of <paramref name="database"/>, or null when the shipped slots do not fit it (its first records
        /// took slots below its root, the deletion's tree does not cover its freed slots, or a record cannot be placed).
        /// </summary>
        /// <param name="search">Whether to search every plausible boundary between the deletion's and the caches' entries, not
        /// only the one where the records start on one run of slots.</param>
        /// <param name="kept">Names the world's free slots kept (a world that left slots free), by slot.</param>
        /// <param name="restore">Puts the database's nodes' children back in the world's order: each reading starts from it.</param>
        /// <param name="mirrors">The inference's budget for mirrored cache nodes; exhausting it stops the inference, not just one reading.</param>
        public static Parts? Infer(IReadOnlyDictionary<WorldNode, int> slot, Func<WorldNode, bool> isModelReference, LoadedModel database, List<int> list, Table before, string mission,
            Action restore, OriginalLoader.MirrorBudget mirrors, RetainedMatchBudget retainedBudget, CancellationToken token, bool search = false, IReadOnlyDictionary<int, string>? kept = null)
        {
            if (list.Count == 0 || database.Content.Count == 0) return null;
            int root = list[0];
            HashSet<int> objectSlots = [.. WorldAssembler.Subtree(database.Content).Select(n => slot[n])];
            int max = objectSlots.Max();
            // Two starting guesses, each refined by the order its caches leave; the better replay is kept.
            List<Parts> results = [];
            if (!objectSlots.Any(s => s < root))
            {
                // The records took fresh slots from the root on, and the deletion freed what lies between.
                int count = 0; while (count < list.Count && list[count] >= root && list[count] <= max + 1 && !objectSlots.Contains(list[count])) count++;
                if (Refine(Attempt([.. Enumerable.Range(root, max - root + 2)], list.Take(count).ToList())) is { } fresh) results.Add(fresh);
            }
            if (results.All(r => r.Inexact != null))
            {
                // The root and first records took slots the caches' own loads freed (even when none lies below the root).
                // Their order follows from the caches, so a walk over the records made from fresh slots, passing over those
                // made before them, feeds the caches' simulation; where its caches end and their high-water mark must give
                // back the boundary it started from. Candidates are tried until one such boundary refines to an exact replay.
                HashSet<(int, int)> visited = [];
                int budget = MaximumBoundaryAttempts;
                foreach (var (deleted, high) in Boundaries())
                {
                    if (budget <= 0) break;
                    if (Settle(deleted, high, visited, ref budget, null) is not { } settled) continue;
                    if (Refine(settled) is { } leftover) { results.Add(leftover); if (leftover.Inexact == null) break; }
                }
                // The records may start inside the copy of the first cache the load made, so that the fresh slots continue a
                // copy whose reference took a slot below the mark (1999 m6's city part, copied from slot 1720 on into the
                // fresh 1804). Where the world kept that cache's slots free, the shipped free list holds it whole: the cache
                // read from there is the first, its copy's end is passed over, and the caches' simulation gives the slots
                // the records start on.
                if (search && results.All(r => r.Inexact != null) && kept is { Count: > 0 } && Retained.Read(list, kept, slot.Keys.Where(isModelReference), token) is { } retained)
                {
                    visited.Clear(); budget = MaximumRetainedAttempts;
                    Dictionary<int, WorldNode> live = [];
                    foreach (var n in WorldAssembler.Subtree(database.Content)) live[slot[n]] = n;
                    // A plateau's boundaries settle alike, so each mark is tried once.
                    HashSet<int> marks = [];
                    foreach (var (deleted, high) in Boundaries())
                    {
                        if (budget <= 0) break;
                        if (marks.Contains(high)) continue;
                        token.ThrowIfCancellationRequested();
                        // Only where the fresh slots can end the cache's copy (a quick check before any walk).
                        List<int> fresh = [root, .. Enumerable.Range(high, Math.Max(0, max - high + 2))];
                        if (retained.Continue(fresh, live, list.Take(deleted).ToHashSet(), retainedBudget, token) == null) continue;
                        marks.Add(high);
                        if (Settle(deleted, high, visited, ref budget, retained) is not { } settled) continue;
                        if (Refine(settled) is { } leftover) { results.Add(leftover); if (leftover.Inexact == null) break; }
                    }
                }
            }
            return results.OrderByDescending(r => r.Inexact == null).ThenByDescending(r => r.Matched).FirstOrDefault();

            // The order the caches leave: the slots their loads freed and nothing took again, last freed first, then fresh
            // slots from the caches' high-water mark; the deletion is what the list holds above the caches' frees.
            Parts? Refine(Parts? best)
            {
            for (int round = 0; round < 4 && best is { Inexact: not null, Leftover: { } leftover }; round++)
            {
                List<int> order = [.. leftover, .. Enumerable.Range(best.HighWater, Math.Max(0, max - best.HighWater + 2))];
                if (order[0] != root || order.SequenceEqual(best.order)) break;
                int deletion = list.Count - best.Consumed;
                if (deletion <= 0) break;
                var next = Attempt(order, list.Take(deletion).ToList());
                if (next == null) break;
                best = next;
            }
            // A walk that passed over records only shows where the records start; and every object must be one of the records.
            return best is { tolerant: false } && best.Complete() ? best : null;
            }

            // Where the deletion's entries may end and the caches' begin, with the caches' high-water mark, most likely first.
            // First the boundary where the record slots below that mark are one run right below the last cache's root, which
            // the deletion's last entry before the caches is (one cache's own caches freed them). Then every boundary whose
            // mark lies above the root, the longest runs of boundaries sharing a mark first: the caches' highest slot lies
            // deep in their frees, so the true mark holds over every boundary from the true one down to it.
            IEnumerable<(int Count, int High)> Boundaries()
            {
                List<(int Count, int High)> candidates = [];
                // Each boundary once: those the first rule finds are among the plateaus' too.
                HashSet<int> found = [];
                foreach (var (d, high, contiguous) in BoundaryCandidates(list, objectSlots, root, token))
                {
                    if (search) candidates.Add((d, high));
                    if (contiguous && found.Add(d)) yield return (d, high);
                }
                // Each plateau's first boundary, then each one's second, and so on: a plateau's first boundary usually settles on
                // its true one (1999 m6 settles from 78 on 81), and a long plateau must not take every attempt.
                var plateaus = candidates.GroupBy(c => c.High).OrderByDescending(g => g.Count()).ThenBy(g => g.First().Count).Select(g => g.ToList()).ToList();
                for (int rank = 0; plateaus.Any(p => rank < p.Count); rank++)
                    foreach (var plateau in plateaus.Where(p => rank < p.Count))
                    {
                        token.ThrowIfCancellationRequested();
                        if (found.Add(plateau[rank].Count)) yield return plateau[rank];
                    }
            }

            // From a boundary, the boundary its caches' simulation gives back, until it gives back its own: the tolerant attempt
            // there, or null when the walk fails or the boundaries cycle. Only an attempt costs budget; a boundary seen before
            // (reached again from another start) ends the walk at no cost.
            Parts? Settle(int deleted, int high, HashSet<(int, int)> visited, ref int budget, Retained? prefix)
            {
                while (visited.Add((deleted, high)) && budget-- > 0)
                {
                    var attempt = Attempt([root, .. Enumerable.Range(high, Math.Max(0, max - high + 2))], list.Take(deleted).ToList(), tolerant: true, prefix);
                    if (attempt?.Leftover is not { } leftover) return null;
                    int count = list.Count - attempt.Consumed;
                    if (count == deleted && attempt.HighWater == high) return leftover.FirstOrDefault(-1) == root || leftover.Count == 0 ? attempt : null;
                    if (count <= 0 || count >= list.Count || attempt.HighWater <= root) return null;
                    (deleted, high) = (count, attempt.HighWater);
                }
                return null;
            }

            Parts? Attempt(List<int> order, List<int> deletion, bool tolerant = false, Retained? prefix = null) =>
                Try(order, deletion, tolerant, nested: true, prefix) ?? Try(order, deletion, tolerant, nested: false, prefix);
            Parts? Try(List<int> order, List<int> deletion, bool tolerant, bool nested, Retained? prefix)
            {
                token.ThrowIfCancellationRequested();
                restore();
                Parts parts = new(slot, isModelReference, database, order, deletion, mission, mirrors, retainedBudget, token, tolerant, nested, prefix);
                if (!parts.Covered || !parts.Walk()) return null;
                // One child order for everything after the walk: the order the records were made in.
                if (!tolerant) parts.OrderChildren();
                parts.ParseCaches([.. Enumerable.Reverse(list)], before);
                parts.Replay(before, list);
                return parts;
            }
        }

        // ------------------------------------------------------------ the database's records

        private int at = 1;
        private WorldNode? pending, candidate, candidateParent;

        private bool Walk()
        {
            if (prefix != null)
            {
                // The fresh slots first continue the first cache's copy: that is passed over, and the cache is the first one.
                if (prefix.Continue(order, live, deletedSlots, retainedBudget, token) is not var (reference, passed)) return false;
                at = 1 + passed;
                prefixReference = reference;
                Content[reference] = [.. reference.Children];
            }
            try { Records(Root, order[0]); }
            catch (InvalidDataException) when (!retainedBudget.Exhausted) { return false; }
            return at >= order.Count - 1;
        }

        private void Records(WorldNode parent, int scope)
        {
            while (at < order.Count)
            {
                token.ThrowIfCancellationRequested();
                int x = order[at];
                if (freed.Contains(x))
                {
                    if (!deletedParent.TryGetValue(x, out int p))
                    {
                        // The end node a reference that is the file's last record needed (made and freed at once).
                        if (EndSlot < 0 && (pending != null || candidate != null)) { EndSlot = x; at++; AfterRecord(Deleted("end", x)); continue; }
                        return;
                    }
                    if (p != scope) return;
                    var group = Deleted("group", x); parent.Children.Add(group); at++;
                    AfterRecord(group);
                    candidate = group; candidateParent = parent;
                    Records(group, x);
                }
                else if (top.TryGetValue(x, out var o))
                {
                    // After a part's copy, the records that follow are not the reference's.
                    if (Content.ContainsKey(parent)) return;
                    parent.Children.Add(o); RecordObject(o);
                }
                else if (tolerant) at++;
                else throw new InvalidDataException($"slot {x} ({live[x].Name}) is not a record");
            }
        }

        private void RecordObject(WorldNode node)
        {
            if (order[at] != slot[node]) throw new InvalidDataException($"{node.Name} is not the next record");
            at++; AfterRecord(node);
            if (isModelReference(node)) { pending = node; return; }
            foreach (var child in InOrder(node)) RecordObject(child);
        }

        private void AfterRecord(WorldNode node)
        {
            if (pending != null)
            {
                var reference = pending; pending = null; candidate = null;
                foreach (var child in InOrder(reference)) Copy(child);
                return;
            }
            if (candidate == null) return;
            var r = candidate; candidate = null;
            if (!CopyStarts(node, Made[r])) return;
            // The node was taken for the reference's record; unless the deleted tree says so it is the record after it.
            bool own = Made.TryGetValue(node, out int ns) && deletedParent.TryGetValue(ns, out int np) && np == Made[r];
            if (!own && r.Children.Remove(node)) candidateParent!.Children.Add(node);
            nextRecord[r] = node;
            CopyBlock(r, Made[r], Made.ContainsKey(node) ? null : node.Children.FirstOrDefault());
        }

        private void Copy(WorldNode node)
        {
            if (at >= order.Count || order[at] != slot[node]) throw new InvalidDataException($"the copy of {node.Name} is not where its reference's content goes");
            at++;
            foreach (var child in InOrder(node)) Copy(child);
        }

        private bool CopyMatches(WorldNode node, ref int p)
        {
            if (p >= order.Count || order[p] != slot[node]) return false;
            p++;
            foreach (var child in InOrder(node)) if (!CopyMatches(child, ref p)) return false;
            return true;
        }

        /// <summary>
        /// A node's children in the order they were made, which is their order in the file: the world lists some in
        /// another order (scripts detached and attached level-of-detail nodes again).
        /// </summary>
        private IEnumerable<WorldNode> InOrder(WorldNode node) =>
            node.Children.Count < 2 || node.Children.Any(c => !slot.TryGetValue(c, out int s) || !position.ContainsKey(s)) ? node.Children : node.Children.OrderBy(c => position[slot[c]]);

        /// <summary>Whether every object of the database is among the records the walk placed.</summary>
        private bool Complete()
        {
            HashSet<WorldNode> placed = new(WorldAssembler.Subtree(Root), ReferenceEqualityComparer.Instance);
            return top.Values.All(placed.Contains);
        }

        /// <summary>Puts every database node's children in the order they were made (see <see cref="InOrder"/>).</summary>
        public void OrderChildren()
        {
            foreach (var node in live.Values)
                if (InOrder(node).ToList() is var made && !made.SequenceEqual(node.Children)) { node.Children.Clear(); node.Children.AddRange(made); }
        }

        private bool HasReference(WorldNode node)
        {
            CandidateWork();
            foreach (var child in WorldAssembler.Subtree(node))
            {
                CandidateWork();
                if (isModelReference(child)) return true;
            }
            return false;
        }
        private bool DeletedUnder(int x, int ancestor)
        {
            while (true)
            {
                CandidateWork();
                if (!deletedParent.TryGetValue(x, out int p)) return false;
                if (p == ancestor) return true;
                x = p;
            }
        }

        /// <summary>Whether a copy of the part <paramref name="reference"/> refers to follows its next record <paramref name="next"/>.</summary>
        private bool CopyStarts(WorldNode next, int reference)
        {
            if (at >= order.Count) return false;
            // An end node is made only for the copy of a reference that is the file's last record: what follows is that copy
            // (1999 m2's last part holds labradio alone).
            if (Made.TryGetValue(next, out int nextSlot) && nextSlot == EndSlot) return true;
            int x = order[at];
            bool record = !Made.ContainsKey(next);
            // The record's children wait: something was copied first.
            if (record && next.Children.Count > 0 && x != slot[next.Children[0]]) return true;
            bool own = Made.TryGetValue(next, out int ns) && deletedParent.TryGetValue(ns, out int np) && np == reference;
            // A reference with records of its own is copied at once, before them, so no copy waits for its own record (1999
            // m5's group 1171 holds the part reference 1172, whose copy follows the next record, g43).
            if (own) return false;
            // The reference's deleted children after a deleted record that is not its own.
            if (!record && !own && freed.Contains(x) && deletedParent.TryGetValue(x, out int p) && p == reference) return true;
            // A run of world objects copied whole (and the reference's deleted descendants) holding a reference copied inline.
            if (!top.ContainsKey(x) && !(freed.Contains(x) && deletedParent.TryGetValue(x, out int fp) && fp == reference)) return false;
            for (int scan = at; scan < order.Count;)
            {
                int y = order[scan];
                if (top.TryGetValue(y, out var o)) { int q = scan; if (!CopyMatches(o, ref q)) break; if (HasReference(o)) return true; scan = q; }
                else if (freed.Contains(y) && DeletedUnder(y, reference)) scan++;
                else break;
            }
            return false;
        }

        /// <summary>The copy of a part: deleted groups copied with it and world objects, in copy order.</summary>
        private void CopyBlock(WorldNode reference, int referenceSlot, WorldNode? stop)
        {
            List<WorldNode> content = [];
            Content[reference] = content;
            void Level(WorldNode holder, int holderSlot)
            {
                while (at < order.Count)
                {
                    int x = order[at];
                    if (stop != null && x == slot[stop]) return;
                    if (freed.Contains(x) && deletedParent.TryGetValue(x, out int p))
                    {
                        if (p != holderSlot) return;
                        var group = Deleted("group", x); holder.Children.Add(group); if (holder == reference) content.Add(group); at++; Level(group, x);
                    }
                    else if (top.TryGetValue(x, out var o))
                    {
                        int q = at; if (!CopyMatches(o, ref q)) return;
                        holder.Children.Add(o); if (holder == reference) content.Add(o); at = q;
                    }
                    else return;
                }
            }
            Level(reference, referenceSlot);
            if (content.Count == 0) Content.Remove(reference);
        }

        private WorldNode Deleted(string kind, int at)
        {
            WorldNode node = new(kind, WorldNodeClass.Object3D);
            Made[node] = at;
            return node;
        }

        // ------------------------------------------------------------ the caches

        /// <summary>The cache a part's reference copies: references with the same key copy one cache (one file).</summary>
        public string CacheKey(WorldNode reference) => Key(reference);
        private string Key(WorldNode node)
        {
            if (Content.TryGetValue(node, out var content)) return "part:" + Signatures.Of(content);
            string file = node.Name.ToLowerInvariant();
            if (SecondPaths.TryGetValue(node, out int path)) return $"{file}#{path}";
            return Place(node) is { } place && FilePaths.Contains(place) ? $"{file}#{place.File}" : file;
        }

        private Dictionary<WorldNode, (string File, string Reference, int Occurrence)>? places;
        /// <summary>Where a reference inside a model file's copy sits in that file: the file's name and its place among the file's references of that name.</summary>
        private (string File, string Reference, int Occurrence)? Place(WorldNode node)
        {
            if (places == null)
            {
                places = new(ReferenceEqualityComparer.Instance);
                HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
                void File(WorldNode reference)
                {
                    if (!seen.Add(reference)) return;
                    Dictionary<string, int> counts = [];
                    foreach (var inner in OwnReferences(reference.Children))
                    {
                        string name = inner.Name.ToLowerInvariant(); int k = counts.GetValueOrDefault(name); counts[name] = k + 1;
                        places[inner] = (reference.Name.ToLowerInvariant(), name, k);
                        File(inner);
                    }
                }
                foreach (var node2 in live.Values) if (isModelReference(node2)) File(node2);
            }
            return places.TryGetValue(node, out var place) ? place : null;
        }
        /// <summary>A file's references among <paramref name="records"/> in record order, not looking inside references or parts.</summary>
        private IEnumerable<WorldNode> OwnReferences(IEnumerable<WorldNode> records)
        {
            HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
            Stack<WorldNode> pending = new(records.Reverse());
            while (pending.TryPop(out var record))
            {
                token.ThrowIfCancellationRequested(); retainedBudget.Take();
                if (!seen.Add(record)) continue;
                if (Content.ContainsKey(record)) continue;
                if (!Made.ContainsKey(record) && isModelReference(record)) { yield return record; continue; }
                foreach (var child in Enumerable.Reverse(record.Children)) pending.Push(child);
            }
        }

        /// <summary>
        /// A part may itself reference a part: a deleted node inside the copy whose first children were copied from a file
        /// of their own (copies hold references' content inline), the rest following it. Tries each deleted node of the
        /// part's copy with each split of its children, with the groups closing where the cache shows, and keeps the first
        /// whose cache the shipped free list shows next.
        /// </summary>
        private bool NestedPart(WorldNode reference)
        {
            var content = Content[reference];
            List<(WorldNode Node, WorldNode? Parent)> candidates = [];
            void Collect(IEnumerable<WorldNode> nodes, WorldNode? parent) { foreach (var n in nodes) if (Made.ContainsKey(n) && !Content.ContainsKey(n)) { candidates.Add((n, parent)); Collect(n.Children, n); } }
            Collect(content, null);
            foreach (var (node, parent) in candidates)
            {
                var siblings = parent?.Children ?? content;
                var children = node.Children.ToList();
                for (int split = children.Count; split >= 1; split--)
                {
                    // The node's first children are its file's content; the rest follow it.
                    var rest = children.Skip(split).ToList();
                    node.Children.RemoveRange(split, rest.Count);
                    siblings.InsertRange(siblings.IndexOf(node) + 1, rest);
                    Content[node] = node.Children.ToList();
                    if (Pops(reference.Name, content, out var trial, out var freedOrder) && FreesNext(freedOrder, exact: true))
                    {
                        var own = OwnRecords(reference, content);
                        reference.Children.Clear(); reference.Children.AddRange(content); reference.Children.AddRange(own);
                        table = trial;
                        return true;
                    }
                    Content.Remove(node);
                    siblings.RemoveRange(siblings.IndexOf(node) + 1, rest.Count);
                    node.Children.AddRange(rest);
                }
            }
            return false;
        }

        /// <summary>
        /// A file naming another file several times may have named it once by another path, which its own load cached
        /// again. Tries each later reference of a repeated name in the file as that path, and keeps the one whose cache the
        /// shipped free list shows next.
        /// </summary>
        private bool OtherPath(WorldNode reference)
        {
            bool part = Content.TryGetValue(reference, out var partContent);
            var own = OwnReferences(part ? partContent! : reference.Children).ToList();
            foreach (var group in own.GroupBy(r => r.Name.ToLowerInvariant()).Where(g => g.Count() > 1))
                foreach (var node in ByOwnModels(group.Skip(1), c => group.TakeWhile(e => e != c)))
                {
                    int k = group.ToList().IndexOf(node);
                    (string, string, int) place = (reference.Name.ToLowerInvariant(), group.Key, k);
                    if (part) SecondPaths[node] = 1; else FilePaths.Add(place);
                    var trial = table.Clone(); var freedOrder = Cache(reference.Name, ContentOf(reference), trial);
                    if (FreesNext(freedOrder, exact: true)) { table = trial; return true; }
                    if (part) SecondPaths.Remove(node); else FilePaths.Remove(place);
                }
            return false;
        }
        /// <summary>A part's records by their names and shape, the deleted ones marked (see <see cref="Key"/>).</summary>
        private Shapes? signatureShapes;
        private Shapes Signatures => signatureShapes ??= new(node => node.Name + (Made.ContainsKey(node) ? "~" : ""));

        /// <summary>
        /// Copies of one cache share its models, and another cache of the file (a second path) has models of its own. Where
        /// several references of a file could have been the second path, the slots cannot tell them apart, but the models
        /// can: <paramref name="candidates"/> in order, those sharing models with an <paramref name="earlier"/> reference
        /// by the first path last (1999 m5's third btundr1.flt, in zone 11, has its own models and polygon zones).
        /// </summary>
        private IEnumerable<WorldNode> ByOwnModels(IEnumerable<WorldNode> candidates, Func<WorldNode, IEnumerable<WorldNode>> earlier)
        {
            ModelSharing sharing = new(Below, retainedBudget, token);
            return candidates.OrderBy(candidate =>
            {
                token.ThrowIfCancellationRequested(); retainedBudget.Take();
                foreach (var previous in earlier(candidate))
                {
                    retainedBudget.Take();
                    if (!SecondPaths.ContainsKey(previous) && sharing.Shares(previous, candidate)) return 1;
                }
                return 0;
            });
        }

        /// <summary>
        /// Every reference a second path's cache served. The slots show which reference made each cache, not which later
        /// references of the file copied it, but the models do: 1999 m1's fgull08 and fgull09 copy the cache fgull07 made,
        /// and m4's chemplnt.flt names chempipl.flt by its second path four times. Only later references can have copied
        /// it, as the cache was made for the first.
        /// </summary>
        public void FollowModels()
        {
            ModelSharing sharing = new(Below, retainedBudget, token);
            // The references of each file's records in order: the database's, and each part's.
            List<List<WorldNode>> files = [[.. ReferencesInOrder().Where(r => !Content.ContainsKey(r))]];
            foreach (var content in Content.Values) files.Add([.. OwnReferences(content)]);
            foreach (var references in files)
            {
                Dictionary<WorldNode, int> positions = new(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < references.Count; i++) { retainedBudget.Take(); positions.TryAdd(references[i], i); }
                List<(WorldNode Trigger, int Path)> paths = [];
                foreach (var pair in SecondPaths)
                {
                    retainedBudget.Take();
                    if (positions.ContainsKey(pair.Key)) paths.Add((pair.Key, pair.Value));
                }
                foreach (var (trigger, path) in paths)
                    for (int i = positions[trigger] + 1; i < references.Count; i++)
                    {
                        retainedBudget.Take();
                        var other = references[i];
                        if (!SecondPaths.ContainsKey(other) && other.Name.Equals(trigger.Name, StringComparison.OrdinalIgnoreCase) && sharing.Shares(trigger, other))
                            SecondPaths[other] = path;
                    }
            }
            // Model files: the later references to the file that copy the second path's cache, in any copy of the file.
            foreach (var (file, name, occurrence) in FilePaths.ToList())
                foreach (var copy in live.Values)
                {
                    retainedBudget.Take();
                    if (!isModelReference(copy) || !copy.Name.Equals(file, StringComparison.OrdinalIgnoreCase)) continue;
                    var named = OwnReferences(copy.Children).Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    for (int k = occurrence + 1; k < named.Count; k++)
                        if (sharing.Shares(named[occurrence], named[k])) FilePaths.Add((file, name, k));
                }
        }
        /// <summary>A reference and what its copy holds (a part's content, not its own records).</summary>
        private IEnumerable<WorldNode> Below(WorldNode node)
        {
            HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
            Stack<WorldNode> pending = new([node]);
            while (pending.TryPop(out var next))
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(next)) continue;
                yield return next;
                foreach (var child in Enumerable.Reverse(Content.TryGetValue(next, out var content) ? content : next.Children)) pending.Push(child);
            }
        }
        private IReadOnlyList<WorldNode> ContentOf(WorldNode node) => Content.TryGetValue(node, out var content) ? content : isModelReference(node) && !Made.ContainsKey(node) ? node.Children : [];

        // A candidate's content may change after a successful trim/pop, so keep this membership index local.
        // Preserve the reference's child order and duplicate occurrences; names do not identify copied records.
        private List<WorldNode> OwnRecords(WorldNode reference, IReadOnlyList<WorldNode> content)
        {
            HashSet<WorldNode> copied = new(ReferenceEqualityComparer.Instance);
            foreach (var node in content) { CandidateWork(); copied.Add(node); }
            List<WorldNode> own = [];
            foreach (var child in reference.Children)
            {
                CandidateWork();
                if (!copied.Contains(child)) own.Add(child);
            }
            return own;
        }
        /// <summary>
        /// Whether the loader caches what the node references: a part, or a model reference, also one to a file without nodes
        /// (no content, but a cache and a copy all the same, as the build has it).
        /// </summary>
        private bool Cached(WorldNode node) => Content.ContainsKey(node) || isModelReference(node) && !Made.ContainsKey(node);

        /// <summary>The database's references in record order, not looking inside parts' content.</summary>
        public List<WorldNode> References() => ReferencesInOrder();

        private List<WorldNode> ReferencesInOrder()
        {
            // A walk passing over records made before the fresh slots: their references were among the first (those not
            // inside another reference's content).
            HashSet<WorldNode>? held = null;
            if (prefixReference == null && tolerant)
            {
                held = new(ReferenceEqualityComparer.Instance);
                foreach (var node in live.Values)
                {
                    CandidateWork();
                    if (isModelReference(node)) foreach (var child in node.Children) { CandidateWork(); held.Add(child); }
                }
            }
            List<WorldNode> found = prefixReference != null ? [prefixReference] : held != null ? [.. live.Where(p =>
            {
                CandidateWork(2); // Filtering plus a retained/sorted candidate, before enumeration builds the list.
                return !position.ContainsKey(p.Key) && isModelReference(p.Value) && !held.Contains(p.Value);
            }).OrderBy(p => p.Key).Select(p => p.Value)] : [];
            HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
            void Walk(WorldNode node)
            {
                CandidateWork();
                if (!seen.Add(node)) return;
                if (Content.TryGetValue(node, out var content))
                {
                    found.Add(node);
                    foreach (var child in OwnRecords(node, content)) Walk(child);
                    return;
                }
                if (!Made.ContainsKey(node) && isModelReference(node)) { found.Add(node); return; }
                foreach (var child in node.Children) Walk(child);
            }
            Walk(Root);
            return found;
        }

        /// <summary>
        /// The first cache a database's load made, as the shipped free list holds it where the world kept its slots free
        /// (1999 m6's city part): the deepest run of the list's frees that is one tree made from consecutive fresh slots,
        /// nested by the order its slots were taken in and named by the names its slots kept. A node whose slot was taken
        /// again lost its name; it is a reference when its content was copied right after the next record (made under one
        /// of its ancestors), and the copy of the cache in the database, or the file's references elsewhere, name it.
        /// </summary>
        internal sealed class Retained
        {
            private const string Lost = "\u0001lost";
            private static bool Unknown(WorldNode node) => node.Name.StartsWith(Lost, StringComparison.Ordinal);
            private readonly WorldNode root;
            private readonly List<WorldNode> copyOrder;
            private readonly HashSet<WorldNode> lostReferences;
            private readonly ILookup<int, WorldNode> references;
            private readonly Shapes outlines = new(_ => ""); private readonly Dictionary<WorldNode, int> outlined = new(ReferenceEqualityComparer.Instance);
            private HashSet<string>? liveNames;

            private Retained(WorldNode root, HashSet<WorldNode> lostReferences, IEnumerable<WorldNode> references)
            {
                this.root = root; this.lostReferences = lostReferences;
                copyOrder = [];
                void Pre(WorldNode n) { copyOrder.Add(n); foreach (var c in n.Children) Pre(c); }
                foreach (var record in root.Children) Pre(record);
                this.references = references.ToLookup(Shape);
            }

            private int Shape(WorldNode n) => outlines.Of(n, outlined);

            /// <param name="references">The world's references, which name references whose names were lost.</param>
            public static Retained? Read(List<int> list, IReadOnlyDictionary<int, string> kept, IEnumerable<WorldNode> references, CancellationToken token)
            {
                var deep = Enumerable.Reverse(list).ToList();
                int start = -1, count = 0;
                // The deepest entries may be an end node freed before the caches.
                for (int from = 0; from < Math.Min(3, deep.Count); from++)
                {
                    int low = int.MaxValue, high = int.MinValue;
                    for (int k = 1; from + k <= deep.Count; k++)
                    {
                        token.ThrowIfCancellationRequested();
                        int s = deep[from + k - 1]; low = Math.Min(low, s); high = Math.Max(high, s);
                        if (high - low + 1 == k && s == low && k > count) { start = from; count = k; }
                    }
                }
                if (count < 2) return null;
                var block = deep.GetRange(start, count);
                if (!block.Any(kept.ContainsKey)) return null;
                // A node's parent is the innermost node made before it and freed after it. A node whose name was lost gets a
                // placeholder of its own: unnamed nodes of one shape would otherwise be one instance definition to the loader.
                Dictionary<int, int> post = []; for (int i = 0; i < block.Count; i++) post[block[i]] = i;
                Dictionary<int, WorldNode> nodes = [];
                Dictionary<WorldNode, WorldNode> parent = new(ReferenceEqualityComparer.Instance);
                foreach (int s in block) nodes[s] = new(kept.GetValueOrDefault(s, Lost + s), WorldNodeClass.Object3D);
                SortedSet<int> made = [];
                foreach (int s in block.Order())
                {
                    token.ThrowIfCancellationRequested();
                    using var after = made.GetViewBetween(post[s] + 1, int.MaxValue).GetEnumerator();
                    if (after.MoveNext()) { var p = nodes[block[after.Current]]; p.Children.Add(nodes[s]); parent[nodes[s]] = p; }
                    made.Add(post[s]);
                }
                var root = nodes[block[^1]];
                List<WorldNode> freedOrder = []; void Post(WorldNode n, int depth = 0) { token.ThrowIfCancellationRequested(); CheckDepth(depth); foreach (var c in n.Children) Post(c, depth + 1); freedOrder.Add(n); }
                Post(root);
                if (!freedOrder.SequenceEqual(block.Select(s => nodes[s]))) return null;
                // A reference's content follows the next record, which is made under one of the reference's ancestors.
                bool Ancestor(WorldNode a, WorldNode n) { for (var q = n; parent.TryGetValue(q, out var p); q = p) if (p == a) return true; return false; }
                HashSet<WorldNode> lost = new(ReferenceEqualityComparer.Instance);
                foreach (var (s, node) in nodes)
                    if (Unknown(node) && node.Children.Count > 0 && nodes.TryGetValue(s + 1, out var next) && nodes.TryGetValue(s + 2, out var first) && first == node.Children[0]
                        && parent.TryGetValue(next, out var holder) && Ancestor(holder, node))
                        lost.Add(node);
                return new(root, lost, references);
            }

            /// <summary>
            /// The hypothesis that the fresh slots (after the root in <paramref name="order"/>) first end the cache's copy: a
            /// reference to the cache's file (its children the cache's records, named where the copy shows them) and how many
            /// fresh slots the copy takes, or null when the copy's end is not there.
            /// </summary>
            public (WorldNode Reference, int Passed)? Continue(IReadOnlyList<int> order, IReadOnlyDictionary<int, WorldNode> live, IReadOnlySet<int> deleted, RetainedMatchBudget budget, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                liveNames ??= [.. live.Values.Select(n => n.Name)];
                for (int k = 0; k <= copyOrder.Count; k++)
                {
                    budget.Take();
                    int length = copyOrder.Count - k;
                    if (1 + length > order.Count) continue;
                    token.ThrowIfCancellationRequested();
                    bool fits = true;
                    for (int i = 0; i < length && fits; i++)
                    {
                        // Count before inspecting a slot, including failed suffixes and boundaries that never replay.
                        budget.Take();
                        var node = copyOrder[k + i]; int x = order[1 + i];
                        // An object of the same name, a reference where the cache had one, or a deleted record for a group (whose name
                        // no object of the database has).
                        fits = live.TryGetValue(x, out var o) ? Unknown(node) ? !lostReferences.Contains(node) || WorldSources.IsReference(o) : node.Name == o.Name
                            : deleted.Contains(x) && !lostReferences.Contains(node) && (Unknown(node) || !liveNames.Contains(node.Name));
                    }
                    if (!fits) continue;
                    // The copy names what the cache lost; the file's references elsewhere name the rest.
                    Dictionary<WorldNode, WorldNode> clone = new(ReferenceEqualityComparer.Instance);
                    WorldNode Clone(WorldNode n) { token.ThrowIfCancellationRequested(); budget.Take(); var c = new WorldNode(n.Name, WorldNodeClass.Object3D); clone[n] = c; foreach (var child in n.Children) c.Children.Add(Clone(child)); return c; }
                    var reference = new WorldNode("part", WorldNodeClass.Object3D);
                    foreach (var record in root.Children) reference.Children.Add(Clone(record));
                    for (int i = 0; i < length; i++)
                        if (Unknown(copyOrder[k + i]) && live.TryGetValue(order[1 + i], out var o)) clone[copyOrder[k + i]].Name = o.Name;
                    bool Agrees(WorldNode a, WorldNode b)
                    {
                        token.ThrowIfCancellationRequested(); budget.Take();
                        return (Unknown(a) || Unknown(b) || a.Name == b.Name) && a.Children.Count == b.Children.Count && a.Children.Zip(b.Children).All(p => Agrees(p.First, p.Second));
                    }
                    foreach (var node in lostReferences.Where(n => Unknown(clone[n])))
                    {
                        var files = references[Shape(node)].Where(r => Agrees(r, clone[node])).Select(r => r.Name.ToLowerInvariant()).Distinct().ToList();
                        if (files.Count != 1) return null;
                        clone[node].Name = files[0];
                    }
                    return (reference, length);
                }
                return null;
            }
        }

        private sealed class Slots
        {
            public List<int> Free = [];
            public int Next;
            public int Take() { if (Free.Count > 0) { int s = Free[^1]; Free.RemoveAt(Free.Count - 1); return s; } return Next++; }
            public void Push(int s) => Free.Add(s);
            public Slots Clone() => new() { Free = [.. Free], Next = Next };
        }

        /// <summary>A file's cache loaded on <paramref name="table"/>: its nodes' slots in the order its freeing frees them.</summary>
        private List<int> Cache(string name, IReadOnlyList<WorldNode> content, Slots table)
        {
            Dictionary<WorldNode, int> taken = new(ReferenceEqualityComparer.Instance);
            Dictionary<WorldNode, WorldNode> original = new(ReferenceEqualityComparer.Instance);
            // Each node's content once, as a set: asked per child, a wide part would cost its width for every child.
            Dictionary<WorldNode, HashSet<WorldNode>> contents = new(ReferenceEqualityComparer.Instance);
            Dictionary<WorldNode, IReadOnlyList<WorldNode>> copies = new(ReferenceEqualityComparer.Instance);
            HashSet<WorldNode> ContentSet(WorldNode node)
            {
                if (contents.TryGetValue(node, out var found)) return found;
                CandidateWork();
                found = new(ReferenceEqualityComparer.Instance);
                foreach (var child in ContentOf(node)) { CandidateWork(); found.Add(child); }
                return contents[node] = found;
            }
            IReadOnlyList<WorldNode> CopyContent(WorldNode node)
            {
                if (copies.TryGetValue(node, out var found)) return found;
                CandidateWork();
                List<WorldNode> content = [];
                if (original.TryGetValue(node, out var source))
                    foreach (var child in node.Children)
                    {
                        CandidateWork();
                        if (original.TryGetValue(child, out var childSource) && ContentSet(source).Contains(childSource)) content.Add(child);
                    }
                // This cache replay never changes child/content membership. Repeated loader callbacks can reuse it.
                return copies[node] = content;
            }
            var load = budget.Load();
            var root = OriginalLoader.Mirror(name, content, original, ContentOf, token, load);
            OriginalLoader.Load(root, root.Children.ToList(), root.Children.ToList(), new()
            {
                Allocate = n => taken[n] = table.Take(),
                Free = n => table.Push(taken[n]),
                Content = CopyContent,
                File = m => original.TryGetValue(m, out var o) ? Key(o) : m.Name,
                IsReference = m => original.TryGetValue(m, out var o) && Cached(o),
                Token = token, Mirrored = load,
            });
            List<int> freedOrder = [];
            OriginalLoader.Destroy(root, x => freedOrder.Add(taken[x]));
            return freedOrder;
        }

        private List<int> deep = [];
        private int offset = -1;
        private Slots table = new();

        private bool FreesNext(List<int> freedOrder, bool exact)
        {
            int start = offset >= 0 ? offset : Enumerable.Range(0, 3).FirstOrDefault(z => Fits(z, freedOrder, exact), -1);
            if (!Fits(start, freedOrder, exact)) return false;
            offset = start + freedOrder.Count;
            return true;
        }
        private bool Fits(int start, List<int> freedOrder, bool exact) => start >= 0 && start + freedOrder.Count <= deep.Count
            && (exact ? deep.Skip(start).Take(freedOrder.Count).SequenceEqual(freedOrder) : deep.Skip(start).Take(freedOrder.Count).ToHashSet().SetEquals(freedOrder));

        /// <summary>How many of a cache's frees, from its first, the shipped free list shows next.</summary>
        private int Matching(List<int> freedOrder)
        {
            int Prefix(int start) { int k = 0; while (k < freedOrder.Count && start + k < deep.Count && deep[start + k] == freedOrder[k]) k++; return k; }
            return offset >= 0 ? Prefix(offset) : Enumerable.Range(0, 3).Max(Prefix);
        }

        /// <summary>
        /// A part's copy shows which records its file had, not always where its groups closed: a group's last objects may
        /// have followed it (the file popped out of the group before them) rather than been its children. That leaves the
        /// order nodes are made in alone but changes the order the part's cache frees them, which settles it. Moves
        /// trailing objects out of groups while more of the cache's frees match, and keeps the moves only when all do.
        /// </summary>
        private bool Pops(string name, List<WorldNode> content, out Slots trial, out List<int> freedOrder)
        {
            trial = table.Clone(); freedOrder = Cache(name, content, trial);
            // Groups closing early leave the order nodes are made in alone, so they change only the order the cache frees
            // its slots, never which: when the slots differ, no reading of them fits.
            var plain = freedOrder;
            if (!(offset >= 0 ? Fits(offset, plain, exact: false) : Enumerable.Range(0, 3).Any(z => Fits(z, plain, exact: false)))) return false;
            int best = Matching(freedOrder);
            List<(WorldNode Group, WorldNode? Parent, int Count)> moves = [];
            IEnumerable<(WorldNode Group, WorldNode? Parent)> Groups()
            {
                Stack<(WorldNode, WorldNode?)> pending = new(content.Select(c => (c, (WorldNode?)null)).Reverse());
                while (pending.TryPop(out var item))
                {
                    var (node, parent) = item;
                    if (!Made.ContainsKey(node) || Content.ContainsKey(node)) continue;
                    yield return (node, parent);
                    foreach (var child in Enumerable.Reverse(node.Children)) pending.Push((child, node));
                }
            }
            List<WorldNode> Siblings(WorldNode? parent) => parent?.Children ?? content;
            void Move(WorldNode group, WorldNode? parent, int count)
            {
                var moved = group.Children.GetRange(group.Children.Count - count, count);
                group.Children.RemoveRange(group.Children.Count - count, count);
                var siblings = Siblings(parent); siblings.InsertRange(siblings.IndexOf(group) + 1, moved);
            }
            void Undo(WorldNode group, WorldNode? parent, int count)
            {
                var siblings = Siblings(parent); int at = siblings.IndexOf(group) + 1;
                group.Children.AddRange(siblings.GetRange(at, count)); siblings.RemoveRange(at, count);
            }
            for (int round = 0; round < 64 && best < freedOrder.Count; round++)
            {
                (WorldNode Group, WorldNode? Parent, int Count)? choice = null;
                foreach (var (group, parent) in Groups().ToList())
                {
                    int trailing = 0;
                    while (trailing < group.Children.Count && !Made.ContainsKey(group.Children[^(trailing + 1)])) trailing++;
                    for (int count = 1; count <= trailing; count++)
                    {
                        Move(group, parent, count);
                        int score = Matching(Cache(name, content, table.Clone()));
                        Undo(group, parent, count);
                        if (score > best) { best = score; choice = (group, parent, count); }
                    }
                }
                if (choice is not { } c) break;
                Move(c.Group, c.Parent, c.Count); moves.Add(c);
                trial = table.Clone(); freedOrder = Cache(name, content, trial);
            }
            var final = freedOrder;
            int start = offset >= 0 ? offset : Enumerable.Range(0, 3).FirstOrDefault(z => Fits(z, final, true), -1);
            if (Fits(start, final, exact: true)) return true;
            for (int k = moves.Count - 1; k >= 0; k--) Undo(moves[k].Group, moves[k].Parent, moves[k].Count);
            return false;
        }

        /// <summary>
        /// Matches the caches, in the order of their files' first references, to the shipped free list's deep end, where the
        /// load freed them: settling where each part's copy ends, which parts were copied at once, and second paths.
        /// </summary>
        private void ParseCaches(List<int> deepEnd, Table before)
        {
            deep = deepEnd;
            table = new() { Free = [.. before.Free.Reverse()], Next = before.Next };
            HashSet<string> cached = [];
            var references = ReferencesInOrder();
            int lastTrigger = -1;
            for (int i = 0; i < references.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var reference = references[i]; string key = Key(reference);
                if (cached.Contains(key)) continue;
                var trial = table.Clone(); var freedOrder = Cache(reference.Name, ContentOf(reference), trial);
                if (FreesNext(freedOrder, exact: true)) { table = trial; cached.Add(key); lastTrigger = i; continue; }
                if (Content.TryGetValue(reference, out var partContent) && Pops(reference.Name, partContent, out var popped, out var poppedOrder) && FreesNext(poppedOrder, exact: true))
                {
                    // The part's own records follow its content in the reference.
                    var own = OwnRecords(reference, partContent);
                    reference.Children.Clear(); reference.Children.AddRange(partContent); reference.Children.AddRange(own);
                    table = popped; cached.Add(key); lastTrigger = i; continue;
                }
                if (OtherPath(reference)) { cached.Add(key); lastTrigger = i; continue; }
                if (Content.ContainsKey(reference) && NestedPart(reference)) { cached.Add(key); lastTrigger = i; continue; }
                if (Content.ContainsKey(reference) && Trim(reference)) { cached.Add(Key(reference)); lastTrigger = i; continue; }
                if (Content.ContainsKey(reference) && Combined(reference)) { cached.Add(Key(reference)); lastTrigger = i; continue; }
                // A reference to a file already cached that makes the next cache named the file by another path.
                bool second = false;
                var between = references.Skip(lastTrigger + 1).Take(i - lastTrigger - 1).Where(r =>
                { retainedBudget.Take(); return !Content.ContainsKey(r) && !Made.ContainsKey(r); });
                foreach (var other in ByOwnModels(between, EarlierNamed))
                {
                    if (second) break;
                    int j = references.IndexOf(other);
                    var t2 = table.Clone(); var f2 = Cache(other.Name, ContentOf(other), t2);
                    if (!FreesNext(f2, exact: true)) continue;
                    SecondPaths[other] = SecondPaths.Count(p => p.Key.Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase)) + 1;
                    cached.Add(Key(other)); table = t2; lastTrigger = j; second = true;
                }
                if (second) { i--; continue; }
                // A deleted group whose first records are a part copied at once.
                if (Immediate(reference) is { } part)
                {
                    cached.Add(Key(part));
                    references = ReferencesInOrder(); i = references.IndexOf(part); lastTrigger = i;
                    continue;
                }
                Inexact ??= $"the cache of {Describe(reference)} is not the next one the shipped free list shows";
                // Load it as the model has it and go on: the caches after it still show where the database's records start.
                table = trial; cached.Add(key); lastTrigger = i;
                offset = Math.Max(offset, 0) + freedOrder.Count;
            }
            Leftover = [.. Enumerable.Reverse(table.Free)];
            HighWater = table.Next;
            Consumed = Math.Max(offset, 0);

            IEnumerable<WorldNode> EarlierNamed(WorldNode candidate)
            {
                foreach (var earlier in references)
                {
                    // Charge before filtering: a run of differently named references still scans the whole prefix.
                    retainedBudget.Take();
                    if (ReferenceEquals(earlier, candidate)) yield break;
                    if (earlier.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)) yield return earlier;
                }
            }
        }

        private string Describe(WorldNode reference) => Content.TryGetValue(reference, out var content)
            ? $"the part beginning with {content.FirstOrDefault()?.Name}" : reference.Name;

        /// <summary>
        /// A part's copy that ends earlier: its last plain world objects are records made after it, the next group's first
        /// records when the copy followed a group, else the records after the object it followed (1999 m5's part 1172
        /// follows g43, and g76, g84 and g92 are records of the same group). The shorter part may also name a file it
        /// references several times once by another path (see <see cref="OtherPath"/>), as 1999 m2's factory part does.
        /// </summary>
        private bool Trim(WorldNode reference)
        {
            var content = Content[reference];
            if (!nextRecord.TryGetValue(reference, out var next)) return false;
            WorldNode? holder = Made.ContainsKey(next) ? null : HolderOf(next);
            if (!Made.ContainsKey(next) && holder == null) return false;
            // The part's own records follow its content in the reference.
            var own = OwnRecords(reference, content);
            // The copy's records in order, each with the list that holds it; the plain objects it ends with may have been made
            // after it, even where the copy's last groups held them (they closed before them).
            List<(WorldNode Node, List<WorldNode> In)> items = [];
            void Items(List<WorldNode> list)
            {
                CandidateWork(1 + 2 * list.Count); // Snapshot and retained candidate entries, before either grows.
                foreach (var node in list.ToList())
                {
                    items.Add((node, list));
                    if (Made.ContainsKey(node) && !Content.ContainsKey(node)) Items(node.Children);
                }
            }
            Items(content);
            int trailing = 0;
            while (trailing < items.Count && items[^(trailing + 1)] is var (last, _) && !Made.ContainsKey(last) && !HasReference(last)) trailing++;
            for (int cut = 1; cut <= trailing; cut++)
            {
                CandidateWork(1 + cut);
                var moved = items.Skip(items.Count - cut).ToList();
                foreach (var (node, list) in moved) RemoveCandidate(list, node);
                var trial = table.Clone();
                bool fits = FreesNext(Cache(reference.Name, content, trial), exact: true);
                if (fits) table = trial;
                // The shorter part's groups may also close before their last objects (1999 m5's part 1891).
                else if (Pops(reference.Name, content, out var popped, out var poppedOrder) && FreesNext(poppedOrder, exact: true)) { fits = true; table = popped; }
                else fits = OtherPath(reference);
                if (fits)
                {
                    reference.Children.Clear(); reference.Children.AddRange(content); reference.Children.AddRange(own);
                    var nodes = moved.Select(m => m.Node).ToList();
                    if (holder == null) next.Children.InsertRange(0, nodes);
                    else holder.Children.InsertRange(holder.Children.IndexOf(next) + 1, nodes);
                    return true;
                }
                // Put them back where they were, in order.
                CandidateWork(1 + 2 * moved.Count);
                foreach (var group in moved.GroupBy(m => m.In)) group.Key.AddRange(group.Select(m => m.Node));
            }
            return false;
        }

        /// <summary>
        /// The readings above together: a part whose copy may end earlier (<see cref="Trim"/>), whose file may name a file it
        /// references several times once by another path (<see cref="OtherPath"/>), and whose groups may close before their
        /// last objects (<see cref="Pops"/>), as 1999 m5's last part does (clone_burger.flt by a second path, group 2190
        /// closing before fog_under8). Tries each shortening with each second path, plain and with groups closing early,
        /// and keeps the first whose cache the shipped free list shows next.
        /// </summary>
        private bool Combined(WorldNode reference)
        {
            var content = Content[reference];
            WorldNode? next = nextRecord.GetValueOrDefault(reference);
            WorldNode? holder = next == null || Made.ContainsKey(next) ? null : HolderOf(next);
            var own = OwnRecords(reference, content);
            List<(WorldNode Node, List<WorldNode> In)> items = [];
            void Items(List<WorldNode> list)
            {
                CandidateWork(1 + 2 * list.Count);
                foreach (var node in list.ToList())
                {
                    items.Add((node, list));
                    if (Made.ContainsKey(node) && !Content.ContainsKey(node)) Items(node.Children);
                }
            }
            Items(content);
            int trailing = 0;
            if (next != null && (Made.ContainsKey(next) || holder != null))
                while (trailing < items.Count && items[^(trailing + 1)] is var (last, _) && !Made.ContainsKey(last) && !HasReference(last)) trailing++;
            for (int cut = 0; cut <= trailing; cut++)
            {
                CandidateWork(1 + cut);
                var moved = items.Skip(items.Count - cut).ToList();
                foreach (var (node, list) in moved) RemoveCandidate(list, node);
                List<WorldNode?> paths = [null, .. OwnReferences(content).GroupBy(r => r.Name.ToLowerInvariant()).Where(g => g.Count() > 1).SelectMany(g => ByOwnModels(g.Skip(1), c => g.TakeWhile(e => e != c)))];
                foreach (var path in paths)
                {
                    if (path != null) SecondPaths[path] = 1;
                    var trial = table.Clone();
                    bool fits = FreesNext(Cache(reference.Name, content, trial), exact: true);
                    if (fits) table = trial;
                    else if (Pops(reference.Name, content, out var popped, out var poppedOrder) && FreesNext(poppedOrder, exact: true)) { fits = true; table = popped; }
                    if (fits)
                    {
                        reference.Children.Clear(); reference.Children.AddRange(content); reference.Children.AddRange(own);
                        var nodes = moved.Select(m => m.Node).ToList();
                        if (nodes.Count > 0 && holder == null) next!.Children.InsertRange(0, nodes);
                        else if (nodes.Count > 0) holder!.Children.InsertRange(holder.Children.IndexOf(next!) + 1, nodes);
                        return true;
                    }
                    if (path != null) SecondPaths.Remove(path);
                }
                CandidateWork(1 + 2 * moved.Count);
                foreach (var group in moved.GroupBy(m => m.In)) group.Key.AddRange(group.Select(m => m.Node));
            }
            return false;
        }

        /// <summary>The record that holds <paramref name="node"/> among its children in the database's tree.</summary>
        private WorldNode? HolderOf(WorldNode node)
        {
            WorldNode? Find(WorldNode parent)
            {
                if (parent.Children.Contains(node)) return parent;
                foreach (var child in parent.Children) if (Made.ContainsKey(child) && !Content.ContainsKey(child) && Find(child) is { } found) return found;
                return null;
            }
            return Find(Root);
        }

        /// <summary>
        /// A deleted record enclosing <paramref name="failing"/> whose first records were a part copied at once: its copy is
        /// the database's records from it to one of its later children, and its cache frees the same slots as the next run
        /// of the shipped free list.
        /// </summary>
        private WorldNode? Immediate(WorldNode failing)
        {
            Dictionary<WorldNode, WorldNode> up = new(ReferenceEqualityComparer.Instance);
            void Up(WorldNode n) { CandidateWork(); foreach (var c in n.Children) { up[c] = n; Up(c); } }
            Up(Root);
            List<WorldNode> chain = [];
            for (var q = failing; up.TryGetValue(q, out var p) && p != Root; q = p) { CandidateWork(); if (Made.ContainsKey(p)) chain.Insert(0, p); }
            if (Made.ContainsKey(failing)) chain.Add(failing);
            foreach (var group in chain)
            {
                CandidateWork();
                int groupSlot = Made[group], from = position[groupSlot];
                int spanEnd = from; void Span(int x) { CandidateWork(); spanEnd = Math.Max(spanEnd, position[x]); foreach (var c in deletedChildren.GetValueOrDefault(x, [])) Span(c); }
                Span(groupSlot);
                foreach (int end in deletedChildren[groupSlot].Skip(1).Select(c => position[c] - 1).Append(spanEnd))
                {
                    CandidateWork();
                    var items = Range(from + 1, end);
                    if (items.Count == 0) continue;
                    var trial = table.Clone();
                    var freedOrder = Cache(group.Name, items, trial);
                    // The same slots in another order still place the copy, but the database is then not exact.
                    bool exact = FreesNext(freedOrder, exact: true);
                    if (!exact && Fits(offset >= 0 ? offset : 0, freedOrder, exact: false) && Pops(group.Name, items, out var popped, out var poppedOrder) && FreesNext(poppedOrder, exact: true))
                    { exact = true; trial = popped; }
                    if (!exact && !FreesNext(freedOrder, exact: false)) { foreach (var made in WorldAssembler.Subtree(items).Where(Made.ContainsKey).ToList()) Made.Remove(made); continue; }
                    if (!exact) Inexact ??= $"the cache of the part beginning with {items[0].Name} frees its slots in another order than the shipped free list shows";
                    // The database's records in the range become the copy.
                    HashSet<int> inRange = [.. Enumerable.Range(from + 1, end - from).Select(z => order[z])];
                    HashSet<WorldNode> fresh = new(WorldAssembler.Subtree(items), ReferenceEqualityComparer.Instance);
                    HashSet<WorldNode> stale = new(Made.Where(p => inRange.Contains(p.Value) && !fresh.Contains(p.Key)).Select(p => p.Key), ReferenceEqualityComparer.Instance);
                    bool InRange(WorldNode c) => stale.Contains(c) || (!Made.ContainsKey(c) && slot.TryGetValue(c, out int cs) && inRange.Contains(cs));
                    var own = group.Children.Where(c => !InRange(c)).ToList();
                    HashSet<WorldNode> itemSet = new(ReferenceEqualityComparer.Instance);
                    foreach (var item in items) { CandidateWork(); itemSet.Add(item); }
                    void Strip(WorldNode n)
                    {
                        n.Children.RemoveAll(c => { CandidateWork(); return InRange(c) && !itemSet.Contains(c); });
                        foreach (var c in n.Children) if (Made.ContainsKey(c) && !fresh.Contains(c)) Strip(c);
                    }
                    Strip(Root);
                    foreach (var x in stale) { Made.Remove(x); Content.Remove(x); nextRecord.Remove(x); }
                    group.Children.Clear(); group.Children.AddRange(items); group.Children.AddRange(own);
                    Content[group] = items;
                    table = trial;
                    return group;
                }
            }
            return null;
        }

        /// <summary>The records in allocation positions from..to: deleted nodes by the deletion's tree, world objects whole under the innermost open deleted node.</summary>
        private List<WorldNode> Range(int from, int to)
        {
            Dictionary<int, WorldNode> made = [];
            List<WorldNode> items = [];
            for (int z = from; z <= to; z++)
            {
                CandidateWork();
                int x = order[z];
                if (top.TryGetValue(x, out var o))
                {
                    WorldNode? holder = null;
                    for (int y = z - 1; y >= from && holder == null; y--)
                    {
                        // A wide run without a deleted group scans every earlier allocation. Charge even misses,
                        // before probing: the next cache replay's node allowance cannot bound this triangular work.
                        CandidateWork();
                        if (made.TryGetValue(order[y], out var open) && Open(order[y], z)) holder = open;
                    }
                    if (holder == null) items.Add(o); else holder.Children.Add(o);
                    int count = 0;
                    foreach (var _ in WorldAssembler.Subtree(o)) { CandidateWork(); count++; }
                    z += count - 1;
                }
                else if (freed.Contains(x))
                {
                    var group = Deleted("group", x); made[x] = group;
                    if (deletedParent.TryGetValue(x, out int p) && made.TryGetValue(p, out var holder)) holder.Children.Add(group); else items.Add(group);
                }
            }
            return items;
        }

        private void RemoveCandidate(List<WorldNode> list, WorldNode node)
        {
            // Removal searches by identity then shifts the suffix. Charge both before List performs either scan.
            CandidateWork(1 + 2 * list.Count);
            list.Remove(node);
        }

        private void CandidateWork(int units = 1)
        {
            token.ThrowIfCancellationRequested();
            retainedBudget.Take(units);
        }

        private bool Open(int group, int z)
        {
            int last = position[group];
            void Last(int q) { CandidateWork(); last = Math.Max(last, position[q]); foreach (var c in deletedChildren.GetValueOrDefault(q, [])) Last(c); }
            Last(group);
            if (z < last) return true;
            for (int q = last + 1; q < z; q++)
            {
                CandidateWork();
                if (freed.Contains(order[q]) && !DeletedUnder(order[q], group)) return false;
            }
            return true;
        }

        // ------------------------------------------------------------ the replay

        /// <summary>Loads the inferred database as the build will, and records how far the slots match.</summary>
        private void Replay(Table before, List<int> list)
        {
            Slots t = new() { Free = [.. before.Free.Reverse()], Next = before.Next };
            Dictionary<WorldNode, int> taken = new(ReferenceEqualityComparer.Instance);
            string? mismatch = null; int matched = 0;
            OriginalLoader.Load(Root, Root.Children.ToList(), Root.Children.ToList(), new()
            {
                Allocate = n =>
                {
                    int s = t.Take(); taken[n] = s;
                    int shipped = slot.TryGetValue(n, out int sh) ? sh : Made.TryGetValue(n, out int ms) ? ms : -1;
                    if (shipped < 0) return;
                    if (shipped == s) matched++;
                    else mismatch ??= $"{(Made.ContainsKey(n) ? "a deleted record" : n.Name)} would take slot {s}, not {shipped}";
                },
                Free = n => t.Push(taken[n]),
                Content = ContentOf,
                File = Key,
                IsReference = Cached,
                Token = token, Mirrored = budget.Load(),
            });
            Matched = matched;
            if (mismatch != null) { Inexact ??= $"{mismatch} ({matched} nodes took their slots first)"; return; }
            var left = Enumerable.Reverse(t.Free).ToList();
            if (list.Count < left.Count || !list.Skip(list.Count - left.Count).SequenceEqual(left))
                Inexact ??= "its caches free their slots in another order than the shipped free list shows";
        }
    }
}
