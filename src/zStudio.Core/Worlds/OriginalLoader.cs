using System.Text;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// The order in which RECOIL's build tool created and freed nodes when it loaded a model file (<c>LoadGameGen</c>). The
/// tool is lost; the order is what the shipped worlds' node slots show, given the engine's node table (one free list,
/// last freed first; see <see cref="WorldAssembler"/>) and docs/engine-evidence.md:
/// <list type="number">
/// <item>Each file the load's records reference is loaded once into a cache, in the order of the first reference to
/// it: the referenced file loaded as a load of its own (its own caches, a root, its records), kept until the end.</item>
/// <item>The nodes the file's records share (OpenFlight instance definitions, which their instance references attach
/// without making nodes), each with its subtree.</item>
/// <item>The load's root.</item>
/// <item>The file's records in order, a node and then its children. The content of an external reference is copied
/// from its cache only after the node of the file's next record has been created, except that a reference with records
/// of its own is copied at once, before them; when a reference is the file's last record, an end node is created for
/// the copy and freed after it. A copy expands shared nodes along every edge.</item>
/// <item>The caches are freed in the order they were made, each as the engine's DestroyNodeRecursive frees a tree: a
/// node's children in order, a shared node with the last parent that lets it go.</item>
/// </list>
/// A file named by two different paths is cached twice: <see cref="Hooks.File"/> returns the path as the reference
/// wrote it. Parts of a mission database (files of groups and objects it references) load like any other file. A
/// reference to a file without nodes is a reference too: its cache is a root alone and its empty copy waits for the next
/// record (see <see cref="Hooks.IsReference"/>).
/// </summary>
internal static class OriginalLoader
{
    /// <summary>
    /// The most nodes the caches of one load may mirror, nested caches included. The shipped loads mirror a few thousand;
    /// a crafted file that nests references to a large file many levels deep would otherwise mirror it at every level.
    /// </summary>
    public const int MaximumCachedNodes = 1 << 20;

    /// <summary>
    /// How many nodes caches may mirror before the work is refused. Each outermost load (its nested caches included) may
    /// mirror <see cref="MaximumCachedNodes"/>, as the build has it; the database inference also counts every load it
    /// simulates, in every reading it tries, against one budget for the whole inference (<see cref="Load"/>), so that a
    /// refusal stops the inference once rather than one reading at a time.
    /// </summary>
    internal sealed class MirrorBudget(int limit, Func<int, string> refusal, MirrorBudget? total = null)
    {
        private int count;
        public void Take() { total?.Take(); if (++count > limit) throw new InvalidDataException(refusal(limit)); }
        /// <summary>One load's budget, also counted against this one.</summary>
        public MirrorBudget Load() => new(MaximumCachedNodes, Refusal, this);
        /// <summary>One load's budget, as the build has it.</summary>
        public static MirrorBudget PerLoad() => new(MaximumCachedNodes, Refusal);
        private static string Refusal(int limit) => $"The files a model references, nested in each other, make the loader cache more than {limit:N0} nodes.";
    }

    public sealed class Hooks
    {
        /// <summary>The engine took a slot for the node.</summary>
        public required Action<WorldNode> Allocate { get; init; }
        /// <summary>The engine returned the node's slot to its free list.</summary>
        public required Action<WorldNode> Free { get; init; }
        /// <summary>For an external reference, its content: the children that come from the referenced file; otherwise empty.</summary>
        public required Func<WorldNode, IReadOnlyList<WorldNode>> Content { get; init; }
        /// <summary>The file a reference names; references to one file share its cache.</summary>
        public required Func<WorldNode, string> File { get; init; }
        /// <summary>
        /// The loader read the node from the file it loads, rather than copying it from a cache: the node's model was made
        /// then (a few shipped models of nodes with children were made after their children's, which the files' face order
        /// decided and the worlds do not record). Inside a cache's load, the node the cache's copy mirrors (whose model
        /// every copy of the cache shares).
        /// </summary>
        public Action<WorldNode>? Read { get; init; }
        /// <summary>
        /// Whether the node is an external reference. A reference to a file without nodes has no content, but the loader
        /// still caches the file (a root alone) and copies it after the next record like any other. By default a node with
        /// content is a reference.
        /// </summary>
        public Func<WorldNode, bool>? IsReference { get; init; }
        public CancellationToken Token { get; init; }
        /// <summary>The budget the caches' mirrors count against (see <see cref="MirrorBudget"/>); by default one of the outermost load's own.</summary>
        internal MirrorBudget? Mirrored { get; init; }
    }

    private static bool Referenced(WorldNode node, IReadOnlyList<WorldNode> content, Hooks hooks) => hooks.IsReference?.Invoke(node) ?? content.Count > 0;
    /// <summary>Membership in a reference's content: a set once the content is wide, so a wide file costs no more than its size.</summary>
    private static Func<WorldNode, bool> Within(IReadOnlyList<WorldNode> content)
    {
        if (content.Count == 0) return static _ => false;
        if (content.Count <= 8) return content.Contains;
        HashSet<WorldNode> set = new(content, ReferenceEqualityComparer.Instance);
        return set.Contains;
    }

    /// <summary>
    /// Loads <paramref name="records"/> (the file's top-level records, enumerated as nodes are created) under
    /// <paramref name="root"/>; <paramref name="cached"/> are the records whose references are cached and whose shared
    /// nodes are defined first (the same records unless the caller is still deciding their order).
    /// </summary>
    public static void Load(WorldNode root, IEnumerable<WorldNode> records, IReadOnlyList<WorldNode> cached, Hooks hooks)
    {
        List<WorldNode> caches = [];
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        // One count for the outermost load and every cache inside it.
        var mirrored = hooks.Mirrored ?? MirrorBudget.PerLoad();
        foreach (var reference in References(cached, hooks))
            if (files.Add(hooks.File(reference))) caches.Add(Cache(reference, hooks, mirrored));
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
        foreach (var definition in Definitions(cached, hooks)) Define(definition);
        hooks.Allocate(root);
        WorldNode? pending = null;
        foreach (var record in records) Record(record);
        if (pending != null)
        {
            WorldNode end = new("", WorldNodeClass.Object3D);
            hooks.Allocate(end); Flush(); hooks.Free(end);
        }
        foreach (var cache in caches) Destroy(cache, hooks);

        void Record(WorldNode node)
        {
            // An instance reference attaches its definition, made before the root; it makes no node.
            if (!seen.Add(node)) return;
            hooks.Token.ThrowIfCancellationRequested();
            hooks.Allocate(node); hooks.Read?.Invoke(node);
            Flush();
            var content = hooks.Content(node); var inner = Within(content);
            if (Referenced(node, content, hooks))
            {
                // A reference with records of its own is copied at once, before them; otherwise after the next record.
                if (node.Children.Any(child => !inner(child))) foreach (var copied in content) Copy(copied);
                else pending = node;
            }
            foreach (var child in node.Children) if (!inner(child)) Record(child);
        }
        void Flush()
        {
            if (pending == null) return;
            var reference = pending; pending = null;
            foreach (var node in hooks.Content(reference)) Copy(node);
        }
        // A copy of a cached file: its nodes in order, with the content of the references inside it.
        void Copy(WorldNode node)
        {
            if (!seen.Add(node)) return;
            hooks.Token.ThrowIfCancellationRequested();
            hooks.Allocate(node);
            foreach (var child in node.Children) Copy(child);
        }
        // A shared node of the file with its subtree, made like a copy but read from the file (a reference's content copied).
        void Define(WorldNode node)
        {
            if (!seen.Add(node)) return;
            hooks.Token.ThrowIfCancellationRequested();
            hooks.Allocate(node); hooks.Read?.Invoke(node);
            var inner = Within(hooks.Content(node));
            foreach (var child in node.Children) if (inner(child)) Copy(child); else Define(child);
        }
    }

    /// <summary>The first reference to each file among the records, in record order, not looking inside references' content.</summary>
    private static IEnumerable<WorldNode> References(IEnumerable<WorldNode> records, Hooks hooks)
    {
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
        Stack<IEnumerator<WorldNode>> stack = new();
        stack.Push(records.GetEnumerator());
        while (stack.Count > 0)
        {
            var level = stack.Peek();
            if (!level.MoveNext()) { stack.Pop(); continue; }
            var node = level.Current;
            if (!seen.Add(node)) continue;
            hooks.Token.ThrowIfCancellationRequested();
            var content = hooks.Content(node);
            if (Referenced(node, content, hooks)) yield return node;
            // A reference's own records follow its content and are the file's records too.
            var inner = Within(content);
            stack.Push(node.Children.Where(child => !inner(child)).GetEnumerator());
        }
    }

    /// <summary>Nodes the records reach along more than one edge (not through references' content), in order of first reach.</summary>
    private static List<WorldNode> Definitions(IReadOnlyList<WorldNode> records, Hooks hooks)
    {
        Dictionary<WorldNode, int> reached = new(ReferenceEqualityComparer.Instance);
        List<WorldNode> order = [];
        void Walk(WorldNode node)
        {
            int count = reached.GetValueOrDefault(node);
            reached[node] = count + 1;
            if (count > 0) return;
            hooks.Token.ThrowIfCancellationRequested();
            order.Add(node);
            var inner = Within(hooks.Content(node));
            foreach (var child in node.Children) if (!inner(child)) Walk(child);
        }
        foreach (var record in records) Walk(record);
        return [.. order.Where(node => reached[node] > 1)];
    }

    /// <summary>A file's cache: its own nodes (copies of a reference's content), loaded as a load of their own under a root named for the file.</summary>
    private static WorldNode Cache(WorldNode reference, Hooks hooks, MirrorBudget mirrored)
    {
        Dictionary<WorldNode, WorldNode> original = new(ReferenceEqualityComparer.Instance);
        // Each node's content once per cache: asked per child and per nested cache level, it would multiply with every level.
        Dictionary<WorldNode, IReadOnlyList<WorldNode>> contents = new(ReferenceEqualityComparer.Instance);
        Dictionary<WorldNode, HashSet<WorldNode>> contentSets = new(ReferenceEqualityComparer.Instance);
        Dictionary<WorldNode, IReadOnlyList<WorldNode>> copies = new(ReferenceEqualityComparer.Instance);
        IReadOnlyList<WorldNode> ContentOf(WorldNode node) => contents.TryGetValue(node, out var found) ? found : contents[node] = hooks.Content(node);
        HashSet<WorldNode> ContentSet(WorldNode node) => contentSets.TryGetValue(node, out var found) ? found : contentSets[node] = new(ContentOf(node), ReferenceEqualityComparer.Instance);
        var root = Mirror(reference.Name, ContentOf(reference), original, ContentOf, hooks.Token, mirrored);
        Load(root, root.Children.ToList(), root.Children.ToList(), new()
        {
            Allocate = hooks.Allocate, Free = hooks.Free,
            Content = copy => copies.TryGetValue(copy, out var found) ? found : copies[copy] = original.TryGetValue(copy, out var node)
                ? [.. copy.Children.Where(c => original.TryGetValue(c, out var oc) && ContentSet(node).Contains(oc))] : [],
            File = copy => original.TryGetValue(copy, out var node) ? hooks.File(node) : copy.Name,
            Read = hooks.Read == null ? null : copy => { if (original.TryGetValue(copy, out var node)) hooks.Read(node); },
            IsReference = hooks.IsReference == null ? null : copy => original.TryGetValue(copy, out var node) && hooks.IsReference(node),
            Token = hooks.Token, Mirrored = mirrored,
        });
        return root;
    }

    /// <summary>
    /// The graph of a file as it is loaded, from <paramref name="content"/> (a copy of it): a root named
    /// <paramref name="name"/> over mirrors of the content, the content of references inside it copied as it is. A copy
    /// expands the file's instances, so the file's own identical unnamed subtrees are one shared node again: an OpenFlight
    /// instance definition has no name of its own, but its copies keep its children, models and transforms.
    /// </summary>
    internal static WorldNode Mirror(string name, IReadOnlyList<WorldNode> content, Dictionary<WorldNode, WorldNode> original, Func<WorldNode, IReadOnlyList<WorldNode>> contentOf,
        CancellationToken token = default, MirrorBudget? mirrored = null)
    {
        mirrored ??= MirrorBudget.PerLoad();
        Dictionary<WorldNode, WorldNode> mirror = new(ReferenceEqualityComparer.Instance);
        Dictionary<int, WorldNode> definitions = [];
        Shapes shapes = new();
        WorldNode Make(WorldNode node, bool copied)
        {
            if (mirror.TryGetValue(node, out var made)) return made;
            token.ThrowIfCancellationRequested();
            int? key = !copied && node.Name.Length == 0 ? shapes.Of(node) : null;
            if (key is { } shape && definitions.TryGetValue(shape, out var shared)) return shared;
            mirrored.Take();
            made = new(node.Name, node.Class); mirror[node] = made; original[made] = node;
            if (key is { } first) definitions[first] = made;
            var inner = Within(contentOf(node));
            foreach (var child in node.Children) made.Children.Add(Make(child, copied || inner(child)));
            return made;
        }
        WorldNode root = new(name, WorldNodeClass.Object3D);
        foreach (var node in content) root.Children.Add(Make(node, false));
        return root;
    }

    /// <summary>
    /// Numbers identical subtrees alike: a node's name, class, model and own transform with its children's numbers. Each
    /// node is numbered once, so a graph whose shared nodes are reached along many edges costs no more than its size.
    /// </summary>
    private sealed class Shapes
    {
        private readonly Dictionary<WorldNode, int> known = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<WorldModel, int> models = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> numbers = new(StringComparer.Ordinal);
        public int Of(WorldNode node)
        {
            if (known.TryGetValue(node, out int number)) return number;
            string name = node.Name;
            StringBuilder key = new();
            key.Append(name.Length).Append(':').Append(name).Append('|').Append((int)node.Class).Append('|')
                .Append(node.Model is not { } model ? -1 : models.TryGetValue(model, out int m) ? m : models[model] = models.Count).Append('|');
            // An object's local matrix, a level of detail's ranges: what an instance's copies keep from its definition.
            if (node.Class == WorldNodeClass.Object3D && WorldUpdate.LocalMatrix(node) != null) key.Append(Convert.ToHexString(node.Payload, 0x30, 48));
            else if (node.Class == WorldNodeClass.Lod) key.Append(Convert.ToHexString(node.Payload));
            foreach (var child in node.Children) key.Append(',').Append(Of(child));
            string text = key.ToString();
            if (!numbers.TryGetValue(text, out number)) numbers[text] = number = numbers.Count;
            return known[node] = number;
        }
    }

    /// <summary>The engine's DestroyNodeRecursive on a graph no one else holds: children in order, each freed once its last parent lets it go.</summary>
    private static void Destroy(WorldNode root, Hooks hooks) => Destroy(root, hooks.Free);
    internal static void Destroy(WorldNode root, Action<WorldNode> free)
    {
        Dictionary<WorldNode, int> parents = new(ReferenceEqualityComparer.Instance);
        HashSet<WorldNode> visited = new(ReferenceEqualityComparer.Instance);
        void Count(WorldNode node) { if (!visited.Add(node)) return; foreach (var child in node.Children) { parents[child] = parents.GetValueOrDefault(child) + 1; Count(child); } }
        Count(root);
        void Free(WorldNode node) { foreach (var child in node.Children) if (--parents[child] == 0) Free(child); free(node); }
        Free(root);
    }
}
