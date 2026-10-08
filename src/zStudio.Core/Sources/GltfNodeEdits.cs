using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Structural edits of a glTF file's node hierarchy, on its JSON: removing a node with its descendants, copying one beside
/// itself and moving one under another parent. Every node index the file holds (children, scenes, animation targets, skin
/// joints) is renumbered with the change; everything else in the file stays as it was.
/// </summary>
public static class GltfNodeEdits
{
    /// <summary>A parsed topology for one immutable phase of an edit. Never retain it across a DOM mutation.</summary>
    internal sealed class Hierarchy
    {
        internal JsonObject Root { get; }
        internal JsonArray Nodes { get; }
        internal CancellationToken Token { get; }
        private readonly int[] parents;
        private readonly int[][] children;
        private readonly Dictionary<int, Matrix4x4> locals = [];
        private readonly GltfInteger.NumericWork numeric;
        private readonly Action? visited;
        private bool invalidated;
        internal Dictionary<int, int> CopiedNodes { get; } = [];
        internal long NumericBytes => numeric.Used;

        internal Hierarchy(JsonObject root, CancellationToken token = default, Action? visited = null,
            long maximumNumericWork = GltfInteger.NumericWork.DefaultMaximum)
        {
            Root = root; Token = token; this.visited = visited;
            numeric = new(maximumNumericWork, token);
            Check();
            Nodes = GltfNodeEdits.Nodes(root);
            if (Nodes.Count > GltfDocument.MaximumNodes) throw new InvalidDataException("The file has too many glTF nodes.");
            parents = new int[Nodes.Count]; Array.Fill(parents, -1);
            children = new int[Nodes.Count][];
            for (int i = 0; i < Nodes.Count; i++)
            {
                Check();
                List<int> list = [];
                foreach (var value in Nodes[i]!["children"] as JsonArray ?? [])
                {
                    Check();
                    int child = Integer(value);
                    if (child < 0 || child >= Nodes.Count) throw new InvalidDataException($"Node {i} lists an invalid child.");
                    list.Add(child);
                    // Public helpers have always followed the first parent in node-index order.
                    if (parents[child] < 0) parents[child] = i;
                }
                children[i] = [.. list];
            }
        }
        internal void Check()
        {
            if (invalidated) throw new InvalidOperationException("The hierarchy view cannot be used after the edit changed its document.");
            Token.ThrowIfCancellationRequested(); visited?.Invoke(); Token.ThrowIfCancellationRequested();
        }
        internal void Invalidate() => invalidated = true;
        internal int Integer(JsonNode? value) { Check(); return GltfInteger.Int32(value, "node", numeric); }
        internal long? Instance(JsonNode? value) { Check(); return GltfInteger.TryInt64(value, out long mark, numeric) && mark is >= 1 and <= int.MaxValue ? mark : null; }
        private void Index(int index)
        {
            Check();
            if (index < 0 || index >= Nodes.Count) throw new InvalidDataException($"The file has no node {index}.");
        }
        internal int? Parent(int index) { Index(index); return parents[index] < 0 ? null : parents[index]; }
        internal IReadOnlyList<int> Children(int index) { Index(index); return children[index]; }
        internal Matrix4x4 Local(int index)
        {
            Index(index);
            if (locals.TryGetValue(index, out var local)) return local;
            var node = (JsonObject)Nodes[index]!;
            // The shared transform parser remains authoritative; charge its numeric tokens before it parses them.
            foreach (string key in new[] { "matrix", "translation", "rotation", "scale" })
                if (node[key] is JsonArray values)
                    foreach (var value in values) GltfInteger.Admit(value, numeric);
            local = GltfDocument.LocalTransform(node, index) ?? Matrix4x4.Identity;
            locals.Add(index, local);
            return local;
        }
        internal Matrix4x4 World(int index)
        {
            Matrix4x4 world = Matrix4x4.Identity;
            int? at = index;
            for (int depth = 0; at is int i; depth++, at = Parent(i))
            {
                Check();
                if (depth > GltfDocument.MaximumDepth) throw new InvalidDataException("The node hierarchy is cyclic or too deep.");
                world *= Local(i);
            }
            return world;
        }
        internal uint? FileZone(int? index)
        {
            for (int depth = 0; index is int i; depth++, index = Parent(i))
            {
                Index(i);
                if (depth > GltfDocument.MaximumDepth) throw new InvalidDataException("The node hierarchy is cyclic or too deep.");
                if (OwnZone((JsonObject)Nodes[i]!) is uint zone) return zone;
            }
            return null;
        }
        internal HashSet<int> Descendants(int index)
        {
            Index(index);
            HashSet<int> seen = []; Stack<int> pending = new([index]);
            while (pending.TryPop(out int i))
            {
                Check();
                if (seen.Add(i)) foreach (int child in children[i]) pending.Push(child);
            }
            return seen;
        }
    }
    /// <summary>Removes node <paramref name="index"/> and its descendants. Meshes, materials and buffers stay (unused ones are valid glTF).</summary>
    public static void Remove(JsonObject root, int index) => Remove(root, [index]);
    /// <summary>Removes each of <paramref name="indices"/> (the copies of an instance's node) with its descendants.</summary>
    public static void Remove(JsonObject root, IReadOnlyCollection<int> indices, CancellationToken token = default)
    {
        var nodes = Nodes(root);
        HashSet<int> selected = [.. indices], removed = [];
        Stack<int> pending = new(selected);
        while (pending.TryPop(out int index))
        {
            token.ThrowIfCancellationRequested();
            if (index < 0 || index >= nodes.Count) throw new InvalidDataException($"The file has no node {index}.");
            if (removed.Add(index)) foreach (int child in Children(nodes, index)) pending.Push(child);
        }
        // A removed node another node still lists (a second parent) would leave a dangling child.
        for (int i = 0; i < nodes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!removed.Contains(i) && Children(nodes, i).Any(c => removed.Contains(c) && !selected.Contains(c)))
                throw new InvalidDataException($"Node {i} also holds a part of node {indices.First()}; the file shares nodes in a way glTF does not allow.");
        }
        foreach (var animation in root["animations"] as JsonArray ?? [])
            foreach (var channel in animation?["channels"] as JsonArray ?? [])
                if (channel?["target"]?["node"] is { } target && removed.Contains(GltfInteger.Int32(target)))
                    throw new InvalidDataException("An animation of the file moves a node the deletion removes; remove the animation in Blender first.");
        foreach (var skin in root["skins"] as JsonArray ?? [])
            if ((skin?["joints"] as JsonArray ?? []).Any(j => removed.Contains(GltfInteger.Int32(j))) || skin?["skeleton"] is { } s && removed.Contains(GltfInteger.Int32(s)))
                throw new InvalidDataException($"A skin of the file uses a node the deletion removes; remove it in Blender first.");
        int[] map = new int[nodes.Count]; int next = 0;
        List<JsonNode?> kept = new(nodes.Count - removed.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            map[i] = removed.Contains(i) ? -1 : next++;
            if (map[i] >= 0) kept.Add(nodes[i]);
        }
        // Removing alternating nodes with RemoveAt shifts the remaining array repeatedly. Detach once and retain
        // the node objects, then rewrite each surviving reference once (including all scenes and parent edges).
        nodes.Clear();
        foreach (var node in kept) nodes.Add(node);
        Renumber(root, map, token);
    }

    /// <summary>
    /// Copies node <paramref name="index"/> and its descendants (sharing their meshes) and places the copy right after it,
    /// named <paramref name="name"/>. Shared parts inside the copy get new instance numbers, so the copy owns them; copies of
    /// an instance's copies pass one <paramref name="instances"/> map, so theirs are copies of one new instance again.
    /// Returns the copy's index. The file's other nodes keep their indices.
    /// </summary>
    public static int Duplicate(JsonObject root, int index, string name, Dictionary<long, long>? instances = null) =>
        Duplicate(root, [index], name, instances)[0];

    /// <summary>
    /// Copies a disjoint set of instance copies in one pass. Each copy stays beside its original, including in every
    /// scene that lists a root. Existing indices/order and shared meshes stay intact; one new marker belongs to each
    /// copied instance group. Capacity, topology and cancellation are checked before publishing any JSON or marker map.
    /// </summary>
    public static IReadOnlyList<int> Duplicate(JsonObject root, IReadOnlyList<int> indices, string name,
        Dictionary<long, long>? instances = null, CancellationToken token = default) =>
        Duplicate(root, indices, name, instances, token, null);

    // The observer lets tests count actual bounded work and cancel inside preparation without timing a machine.
    internal static IReadOnlyList<int> Duplicate(JsonObject root, IReadOnlyList<int> indices, string name,
        Dictionary<long, long>? instances, CancellationToken token, Action? visited, int maximumNodes = GltfDocument.MaximumNodes,
        Dictionary<int, int>? copiedNodes = null)
    {
        void Visit() { token.ThrowIfCancellationRequested(); visited?.Invoke(); token.ThrowIfCancellationRequested(); }
        Visit();
        if (maximumNodes is < 1 or > GltfDocument.MaximumNodes) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        if (root["nodes"] is not JsonArray nodes || nodes.Count > maximumNodes)
            throw new InvalidDataException("The file has no supported node list.");
        if (indices.Count == 0) return [];
        int count = nodes.Count;
        if (indices.Count > count) throw new InvalidDataException("The copy selection lists more nodes than the file holds.");
        int[] parents = new int[count]; Array.Fill(parents, -1);
        HashSet<long> usedInstances = [];
        Dictionary<long, long> reserved = [];
        if (instances != null)
            foreach (var (original, marker) in instances)
            {
                Visit();
                if (marker is < 1 or > int.MaxValue) throw new InvalidDataException("A copied instance identity is outside the supported positive Int32 range.");
                reserved.Add(original, marker); usedInstances.Add(marker);
            }
        // Validate and index once. Calling Nodes/Parent or finding occupied markers separately for every selected
        // instance makes a shallow, reader-valid file cost quadratic work.
        for (int i = 0; i < count; i++)
        {
            Visit();
            if (nodes[i] is not JsonObject node) throw new InvalidDataException("The file has no valid node list.");
            if (Mark(node) is long marker) usedInstances.Add(marker);
            foreach (int child in Children(nodes, i))
            {
                Visit();
                if (parents[child] >= 0) throw new InvalidDataException($"Node {child} is listed as a child more than once.");
                parents[child] = i;
            }
        }
        List<int> order = [];
        Dictionary<int, int> copies = [];
        List<int> tops = new(indices.Count);
        Dictionary<int, int> beside = [];
        Stack<(int Index, int Depth)> pending = new();
        foreach (int index in indices)
        {
            Visit();
            if (index < 0 || index >= count) throw new InvalidDataException($"The file has no node {index}.");
            int top = count + order.Count;
            tops.Add(top);
            if (!beside.TryAdd(index, top)) throw new InvalidDataException($"Node {index} is selected more than once.");
            pending.Push((index, 1));
            while (pending.TryPop(out var item))
            {
                Visit();
                if (item.Depth > GltfDocument.MaximumDepth) throw new InvalidDataException("The node hierarchy is cyclic or too deep to copy.");
                if (!copies.TryAdd(item.Index, count + order.Count))
                    throw new InvalidDataException($"Node {item.Index} is reached twice by the selected copies; select disjoint subtrees.");
                if (order.Count >= maximumNodes - count)
                    throw new InvalidDataException($"The copies would exceed {maximumNodes:N0} glTF nodes; split the model before copying them.");
                order.Add(item.Index);
                if (nodes[item.Index]!["children"] is JsonArray children)
                    for (int c = children.Count - 1; c >= 0; c--)
                    {
                        Visit();
                        pending.Push((GltfInteger.Int32(children[c]), item.Depth + 1));
                    }
            }
        }
        long nextInstance = 1;
        foreach (int original in order)
        {
            Visit();
            if (Mark((JsonObject)nodes[original]!) is not long marker || reserved.ContainsKey(marker)) continue;
            // Positive Int32 identities may have holes even when Int32.MaxValue is occupied.
            while (usedInstances.Contains(nextInstance) && nextInstance <= int.MaxValue) { Visit(); nextInstance++; }
            if (nextInstance > int.MaxValue) throw new InvalidDataException("The file has no unused instance identity for the copy.");
            reserved.Add(marker, nextInstance); usedInstances.Add(nextInstance++);
        }

        List<JsonObject> appended = new(order.Count);
        foreach (int original in order)
        {
            Visit();
            var copy = (JsonObject)nodes[original]!.DeepClone();
            if (copy["children"] is JsonArray children)
            {
                JsonArray rewritten = [];
                foreach (var child in children) { Visit(); rewritten.Add(copies[GltfInteger.Int32(child)]); }
                copy["children"] = rewritten;
            }
            if ((copy["extras"] as JsonObject)?[WorldGltf.Key] is JsonObject marked && Instance(marked["instance"]) is long marker)
                marked["instance"] = reserved[marker];
            if (beside.ContainsKey(original))
            {
                copy["name"] = name;
                // Engine name as well: editor suffix rules must not rename this copy on import.
                if (copy["extras"] is not JsonObject extras) copy["extras"] = extras = new();
                if (extras[WorldGltf.Key] is not JsonObject recoil) extras[WorldGltf.Key] = recoil = new();
                recoil["name"] = name;
            }
            appended.Add(copy);
        }

        List<(JsonObject Owner, string Property, JsonArray Nodes)> rewrittenLists = [];
        for (int i = 0; i < count; i++)
        {
            Visit();
            if (nodes[i]!["children"] is JsonArray children) Rewrite((JsonObject)nodes[i]!, children, scene: false);
        }
        foreach (var scene in root["scenes"] as JsonArray ?? [])
        {
            Visit();
            if (scene is JsonObject owner && owner["nodes"] is JsonArray roots) Rewrite(owner, roots, scene: true);
        }
        // Prepare each adjacency list once. Repeated InsertAfter would still shift a wide sibling/root list once per
        // copy even with a parent index. Only lists receiving copies are replaced; all unrelated JSON stays identical.
        void Rewrite(JsonObject owner, JsonArray list, bool scene)
        {
            JsonArray? rewritten = null;
            for (int at = 0; at < list.Count; at++)
            {
                Visit();
                int original = GltfInteger.Int32(list[at]);
                if (original < 0 || original >= count) throw new InvalidDataException($"The file has no node {original}.");
                bool insert = (!scene || parents[original] < 0) && beside.TryGetValue(original, out _);
                if (insert && rewritten == null)
                {
                    rewritten = [];
                    for (int before = 0; before < at; before++) { Visit(); rewritten.Add(list[before]!.DeepClone()); }
                }
                if (rewritten == null) continue;
                rewritten.Add(list[at]!.DeepClone());
                if (insert) rewritten.Add(beside[original]);
            }
            if (rewritten != null) rewrittenLists.Add((owner, scene ? "nodes" : "children", rewritten));
        }

        Visit();
        // No cancellation or fallible input validation after this boundary. The caller edits a private candidate too,
        // but the helper itself leaves both the supplied DOM and shared marker map intact on preparation refusal.
        foreach (var copy in appended) nodes.Add(copy);
        foreach (var (owner, property, rewritten) in rewrittenLists) owner[property] = rewritten;
        if (instances != null) foreach (var pair in reserved) instances[pair.Key] = pair.Value;
        if (copiedNodes != null) foreach (var pair in copies) copiedNodes.Add(pair.Value, pair.Key);
        return tops;

        static long? Mark(JsonObject node) => Instance(((node["extras"] as JsonObject)?[WorldGltf.Key] as JsonObject)?["instance"]);
    }

    /// <summary>
    /// Moves node <paramref name="index"/> under <paramref name="parent"/> (null: a root of the default scene), keeping its
    /// place in the world: its local transform becomes its world transform relative to the new parent. It keeps its zone
    /// too: a node without a zone of its own takes its parent's, so when the new place would give it another, the one it
    /// had is written to it (<paramref name="currentZone"/>, the built node's, when it came from whatever loads the file;
    /// without it, such a move is refused).
    /// </summary>
    public static void Reparent(JsonObject root, int index, int? parent, uint? currentZone = null, CancellationToken token = default) =>
        Reparent(new Hierarchy(root, token), index, parent, currentZone);

    internal static void Reparent(Hierarchy hierarchy, int index, int? parent, uint? currentZone = null)
    {
        hierarchy.Check();
        var root = hierarchy.Root;
        var nodes = hierarchy.Nodes;
        if (parent is int p && (p < 0 || p >= nodes.Count)) throw new InvalidDataException($"The file has no node {p}.");
        if (parent is int q && hierarchy.Descendants(index).Contains(q)) throw new InvalidDataException("A node cannot move under itself or one of its descendants.");
        int? oldParent = hierarchy.Parent(index);
        // Prepare every affected scene/child list before mutation, including the selected root destination.
        List<(JsonObject Owner, string Key, JsonArray List)> lists = [];
        bool sceneRoot = false;
        JsonObject? chosen = null;
        if (parent == null)
        {
            if (root["scenes"] is not JsonArray scenes) throw new InvalidDataException("The file has no scene to hold a root node.");
            int scene = root["scene"] is { } selected ? hierarchy.Integer(selected) : 0;
            if (scene < 0 || scene >= scenes.Count || scenes[scene] is not JsonObject destination)
                throw new InvalidDataException("The default scene does not exist.");
            chosen = destination;
        }
        foreach (var item in root["scenes"] as JsonArray ?? [])
        {
            hierarchy.Check();
            if (item is not JsonObject scene) continue;
            JsonArray replacement = [];
            foreach (var value in scene["nodes"] as JsonArray ?? [])
            {
                hierarchy.Check();
                if (hierarchy.Integer(value) == index) sceneRoot = true;
                else replacement.Add(value?.DeepClone());
            }
            if (ReferenceEquals(scene, chosen)) replacement.Add(index);
            if (scene["nodes"] is JsonArray || ReferenceEquals(scene, chosen)) lists.Add((scene, "nodes", replacement));
        }
        if (oldParent == parent && (parent != null || sceneRoot)) return;
        if (oldParent is int from)
            lists.Add(((JsonObject)nodes[from]!, "children", new JsonArray(((JsonArray)nodes[from]!["children"]!)
                .Where(value => hierarchy.Integer(value) != index).Select(value => value?.DeepClone()).ToArray())));
        if (parent is int into)
        {
            var replacement = (JsonArray?)nodes[into]!["children"]?.DeepClone() ?? [];
            replacement.Add(index); lists.Add(((JsonObject)nodes[into]!, "children", replacement));
        }
        Matrix4x4 world = hierarchy.World(index), parentWorld = parent is int np ? hierarchy.World(np) : Matrix4x4.Identity;
        ObjectTransform.Finite(world); ObjectTransform.Finite(parentWorld);
        if (!Matrix4x4.Invert(parentWorld, out var inverse)) throw new InvalidDataException("The new parent's transform cannot be inverted (a zero scale).");
        ObjectTransform.Finite(inverse);
        var moved = (JsonObject)nodes[index]!;
        bool ownZone = OwnZone(moved) != null;
        uint? zoneBefore = ownZone ? null : hierarchy.FileZone(oldParent);
        uint? inheritedZone = !ownZone && zoneBefore != hierarchy.FileZone(parent)
            ? zoneBefore ?? currentZone ?? throw new InvalidDataException("The node takes its zone from what loads the file, which differs between its copies; under the new parent the file's zone would replace it, so move it in Blender or the scripts.")
            : null;
        // Under a far larger parent the node's own scale would fall below what an edit can read back (a flattened axis).
        // (A node already flattened stays so wherever it moves.)
        var local = world * inverse;
        ObjectTransform.Finite(local);
        // A parent whose matrix is sheared and badly conditioned inverts imprecisely: the node must land where it is.
        local.M14 = local.M24 = local.M34 = 0; local.M44 = 1;
        var placed = local * parentWorld;
        ObjectTransform.Finite(placed);
        // Positions round with their magnitudes: the node's, its parent's and the local offset turned into the parent's space.
        static float Largest(Matrix4x4 m) => new[] { m.M41, m.M42, m.M43 }.Max(MathF.Abs);
        double basis = new[] { new Vector3(parentWorld.M11, parentWorld.M12, parentWorld.M13), new Vector3(parentWorld.M21, parentWorld.M22, parentWorld.M23), new Vector3(parentWorld.M31, parentWorld.M32, parentWorld.M33) }.Max(ObjectTransform.Length);
        double reach = Math.Max(1, Math.Max(Largest(world), Math.Max(Largest(parentWorld), Largest(local) * basis)));
        for (int row = 0; row < 4; row++)
        {
            Vector3 wanted = new(world[row, 0], world[row, 1], world[row, 2]), got = new(placed[row, 0], placed[row, 1], placed[row, 2]);
            if (!(Distance(got, wanted) <= 1e-4 * (row < 3 ? Math.Max(ObjectTransform.Length(wanted), 1e-12) : reach)))
                throw new InvalidDataException("The new parent's transform cannot be undone precisely enough to keep the node in place (its matrix is sheared and badly conditioned); choose another parent.");
        }
        static bool Flat(Matrix4x4 m) => ObjectTransform.Length(new(m.M11, m.M12, m.M13)) < 1e-6 || ObjectTransform.Length(new(m.M21, m.M22, m.M23)) < 1e-6 || ObjectTransform.Length(new(m.M31, m.M32, m.M33)) < 1e-6;
        if (Flat(local) && !Flat(hierarchy.Local(index)))
            throw new InvalidDataException("Under the new parent the node's scale would fall below 1e-6 of its parent's, which flattens it for later edits; choose another parent.");
        if (MathF.Abs(local.M41) > SourceWorlds.MaximumCoordinate || MathF.Abs(local.M42) > SourceWorlds.MaximumCoordinate || MathF.Abs(local.M43) > SourceWorlds.MaximumCoordinate)
            throw new InvalidDataException($"Under the new parent the node's position would lie beyond ±{SourceWorlds.MaximumCoordinate:N0} of it; choose another parent.");
        JsonObject preparedLocal = new();
        SetLocal(preparedLocal, local);
        JsonObject? preparedExtras = null;
        if (inheritedZone is uint keep)
        {
            // From outside the file, the zone is the built node's; without one (copies that differ), it cannot be kept.
            var extras = preparedExtras = (JsonObject?)(moved["extras"] as JsonObject)?.DeepClone() ?? new();
            if (extras[WorldGltf.Key] is not JsonObject engine) extras[WorldGltf.Key] = engine = new JsonObject();
            engine["zone"] = (int)(keep & 0xFF);
        }
        hierarchy.Check();
        hierarchy.Invalidate();
        // All input validation, numeric work and cancellation precede this commit. The old index cannot be reused.
        foreach (var (owner, key, list) in lists)
        {
            if (key == "children" && list.Count == 0) owner.Remove(key);
            else owner[key] = list;
        }
        if (preparedExtras != null) moved["extras"] = preparedExtras;
        foreach (string key in new[] { "matrix", "translation", "rotation", "scale" })
        {
            moved.Remove(key);
            if (preparedLocal.Remove(key, out var value)) moved[key] = value;
        }
    }

    /// <summary>The zone a node states (its zone, or its zone word's low byte), which its children inherit.</summary>
    private static uint? OwnZone(JsonObject node) => WorldGltf.StatedZone((node["extras"] as JsonObject)?[WorldGltf.Key]);
    /// <summary>
    /// A node's local transform as the reader composes it (matrix, or scale · rotation · translation; identity when it states
    /// none). A malformed transform is refused as the reader refuses it (<see cref="GltfDocument.LocalTransform"/>).
    /// </summary>
    public static Matrix4x4 Local(JsonObject node) => GltfDocument.LocalTransform(node) ?? Matrix4x4.Identity;
    /// <summary>Sets a node's local transform, as translation, rotation and scale when it decomposes, otherwise as a matrix.</summary>
    public static void SetLocal(JsonObject node, Matrix4x4 m)
    {
        ObjectTransform.Finite(m);
        node.Remove("matrix"); node.Remove("translation"); node.Remove("rotation"); node.Remove("scale");
        if (m.IsIdentity) return;
        // TRS only when it gives the matrix back, each axis within a fraction of its own length: Decompose replaces axes
        // shorter than its epsilon, which an absolute comparison would let through as a turned object.
        if (Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation)
            && Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation) is var recomposed
            && Enumerable.Range(0, 3).All(row =>
            {
                Vector3 wanted = new(m[row, 0], m[row, 1], m[row, 2]), got = new(recomposed[row, 0], recomposed[row, 1], recomposed[row, 2]);
                return Distance(got, wanted) <= 1e-4 * ObjectTransform.Length(wanted);
            }))
        {
            if (translation != Vector3.Zero) node["translation"] = new JsonArray(translation.X, translation.Y, translation.Z);
            if (rotation != Quaternion.Identity) node["rotation"] = new JsonArray(rotation.X, rotation.Y, rotation.Z, rotation.W);
            if (scale != Vector3.One) node["scale"] = new JsonArray(scale.X, scale.Y, scale.Z);
            return;
        }
        node["matrix"] = new JsonArray(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    }
    private static double Distance(Vector3 a, Vector3 b)
    {
        double x = (double)a.X - b.X, y = (double)a.Y - b.Y, z = (double)a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }
    /// <summary>A node's transform in the file's scene: its local transform under each ancestor's.</summary>
    public static Matrix4x4 World(JsonObject root, int index, CancellationToken token = default) => new Hierarchy(root, token).World(index);
    /// <summary>An instance marker as the importer reads it: a whole number, written as an integer or a float.</summary>
    private static long? Instance(JsonNode? node) => GltfInteger.TryInt64(node, out long i) && i is >= 1 and <= int.MaxValue ? i : null;
    /// <summary>
    /// Node <paramref name="index"/> and, when an instance holds it (a node the file places under several parents, written as
    /// copies with one mark, or a node inside one), the nodes standing for it in the instance's other copies: the importer
    /// reads the first copy and joins the others to it, so all of them must change alike. The node comes first.
    /// </summary>
    public static IReadOnlyList<int> InstanceCopies(JsonObject root, int index, CancellationToken token = default) =>
        InstanceCopies(new Hierarchy(root, token), index);

    internal static IReadOnlyList<int> InstanceCopies(Hierarchy hierarchy, int index)
    {
        hierarchy.Check();
        var nodes = hierarchy.Nodes;
        if (index < 0 || index >= nodes.Count) throw new InvalidDataException($"The file has no node {index}.");
        int[] parents = new int[nodes.Count], places = new int[nodes.Count];
        Array.Fill(parents, -1);
        for (int i = 0; i < nodes.Count; i++) { int k = 0; foreach (int c in hierarchy.Children(i)) { hierarchy.Check(); if (parents[c] < 0) { parents[c] = i; places[c] = k; } k++; } }
        // What the importer makes of a node: a marked node is its instance, a node below one the child at its place in the
        // instance's node; a node no instance holds is itself (no key).
        string?[] keys = new string?[nodes.Count]; bool[] known = new bool[nodes.Count];
        long? Mark(int i) => ((nodes[i]!["extras"] as JsonObject)?[WorldGltf.Key] as JsonObject)?["instance"] is { } value ? hierarchy.Instance(value) : null;
        string? Key(int i)
        {
            List<int> chain = [];
            for (int at = i; !known[at]; at = parents[at])
            {
                hierarchy.Check();
                chain.Add(at);
                if (Mark(at) != null || parents[at] < 0) break;
                if (chain.Count > GltfDocument.MaximumDepth) throw new InvalidDataException("The node hierarchy is cyclic or too deep.");
            }
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                hierarchy.Check();
                int n = chain[k];
                keys[n] = Mark(n) is long mark ? $"i{mark}" : parents[n] >= 0 && keys[parents[n]] is { } above ? $"{above}/{places[n]}" : null;
                known[n] = true;
            }
            return keys[i];
        }
        if (Key(index) is not { } key) return [index];
        List<int> copies = [index];
        for (int i = 0; i < nodes.Count; i++) { hierarchy.Check(); if (i != index && Key(i) == key) copies.Add(i); }
        return copies;
    }
    /// <summary>
    /// The zone import gives each node the file places under several parents (by its instance mark, in the order import
    /// reaches them): the zone its first copy states, or the one that copy's parents in the file pass on to it; null when
    /// that comes from whatever loads the file. Import reads the first copy it reaches through the scene's roots and their
    /// children and joins the later ones to it, so moving or removing the node that holds that copy, or moving another
    /// copy's holder before it, gives the shared node the zone of the copy read instead.
    /// </summary>
    public static IReadOnlyList<(long Mark, uint? Zone)> InstanceZones(JsonObject root, CancellationToken token = default) =>
        VisitInstanceZones(root, null, token);

    /// <summary>
    /// Visits each first-read instance in import order. A supplied replacement zone is written to all its copies before
    /// visiting its descendants, so nested instances inherit the repaired outer zone without rescanning the whole file.
    /// </summary>
    internal static void PreserveInstanceZones(JsonObject root, Func<long, uint?, uint?> preserve, CancellationToken token = default) =>
        VisitInstanceZones(root, preserve, token);

    private static IReadOnlyList<(long Mark, uint? Zone)> VisitInstanceZones(JsonObject root, Func<long, uint?, uint?>? preserve, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var nodes = Nodes(root);
        Dictionary<long, List<JsonObject>> copies = [];
        if (preserve != null)
            foreach (JsonObject node in nodes.Cast<JsonObject>())
            {
                token.ThrowIfCancellationRequested();
                if ((node["extras"] as JsonObject)?[WorldGltf.Key] is JsonObject engine && Instance(engine["instance"]) is long mark)
                {
                    if (!copies.TryGetValue(mark, out var list)) copies[mark] = list = [];
                    list.Add(engine);
                }
            }
        List<(long, uint?)> zones = [];
        HashSet<long> marks = []; HashSet<int> reached = [];
        foreach (int index in ImportRoots(root, nodes)) Walk(index, null, 0);
        return zones;

        void Walk(int i, uint? passed, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > GltfDocument.MaximumDepth) throw new InvalidDataException("The node hierarchy is too deep to inspect instance zones.");
            if (!reached.Add(i)) return;
            var node = (JsonObject)nodes[i]!;
            var engine = (node["extras"] as JsonObject)?[WorldGltf.Key] as JsonObject;
            // A terrain recipe's marker stands for its pieces; import reads no node below it.
            if (depth == 0 && engine?["terrain"] != null) return;
            uint? zone = OwnZone(node) ?? passed;
            if (Instance(engine?["instance"]) is long mark)
            {
                // A later copy is the node already read: import does not read below it.
                if (!marks.Add(mark)) return;
                if (preserve?.Invoke(mark, zone) is uint keep)
                {
                    foreach (var copy in copies[mark])
                    {
                        token.ThrowIfCancellationRequested();
                        copy["zone"] = (int)(keep & 0xFF);
                    }
                    zone = keep & 0xFF;
                }
                zones.Add((mark, zone));
            }
            foreach (int child in Children(nodes, i)) Walk(child, zone, depth + 1);
        }
    }
    /// <summary>States <paramref name="zone"/> on every copy of the shared node <paramref name="mark"/>, so it has that zone whichever copy import reads.</summary>
    public static void SetInstanceZone(JsonObject root, long mark, uint zone)
    {
        foreach (var node in Nodes(root).OfType<JsonObject>())
            if ((node["extras"] as JsonObject)?[WorldGltf.Key] is JsonObject engine && Instance(engine["instance"]) == mark)
                engine["zone"] = (int)(zone & 0xFF);
    }
    /// <summary>The roots import reads, in its order: the default scene's nodes, or without scenes every node no node lists as a child.</summary>
    private static IEnumerable<int> ImportRoots(JsonObject root, JsonArray nodes)
    {
        if (root["scenes"] is JsonArray scenes && scenes.Count > 0)
        {
            int chosen = GltfInteger.OptionalInt32(root["scene"], "scene") ?? 0;
            return (scenes[Math.Clamp(chosen, 0, scenes.Count - 1)]?["nodes"] as JsonArray ?? [])
                .Select(n => GltfInteger.Int32(n)).Where(k => k >= 0 && k < nodes.Count).ToList();
        }
        HashSet<int> children = [.. Enumerable.Range(0, nodes.Count).SelectMany(i => Children(nodes, i))];
        return Enumerable.Range(0, nodes.Count).Where(i => !children.Contains(i)).ToList();
    }
    /// <summary>The parent of a node, or null for a root.</summary>
    public static int? Parent(JsonObject root, int index, CancellationToken token = default) => new Hierarchy(root, token).Parent(index);

    private static JsonArray Nodes(JsonObject root) =>
        root["nodes"] as JsonArray is { } nodes && nodes.All(n => n is JsonObject) ? nodes : throw new InvalidDataException("The file has no valid node list.");
    private static IEnumerable<int> Children(JsonArray nodes, int i) => (nodes[i]!["children"] as JsonArray ?? []).Select(c => GltfInteger.Int32(c) is int k && k >= 0 && k < nodes.Count ? k : throw new InvalidDataException($"Node {i} lists an invalid child."));
    /// <summary>Node <paramref name="index"/> and every node below it in the file.</summary>
    public static IReadOnlySet<int> Descendants(JsonObject root, int index, CancellationToken token = default) => new Hierarchy(root, token).Descendants(index);
    /// <summary>Applies a node renumbering (−1: removed) to every node reference of the file.</summary>
    private static void Renumber(JsonObject root, int[] map, CancellationToken token)
    {
        foreach (var node in Nodes(root))
        {
            token.ThrowIfCancellationRequested();
            if (node!["children"] is JsonArray children)
            {
                var retained = children.Select(c => map[GltfInteger.Int32(c)]).Where(i => i >= 0).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray();
                if (retained.Length == 0) ((JsonObject)node).Remove("children");
                else node["children"] = new JsonArray(retained);
            }
        }
        foreach (var scene in root["scenes"] as JsonArray ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (scene?["nodes"] is JsonArray list) scene["nodes"] = new JsonArray(list.Select(n => map[GltfInteger.Int32(n)]).Where(n => n >= 0).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        }
        foreach (var animation in root["animations"] as JsonArray ?? [])
            foreach (var channel in animation?["channels"] as JsonArray ?? [])
            {
                token.ThrowIfCancellationRequested();
                if (channel?["target"] is JsonObject target && target["node"] is { } v) target["node"] = map[GltfInteger.Int32(v)];
            }
        foreach (var skin in root["skins"] as JsonArray ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (skin?["joints"] is JsonArray joints) skin["joints"] = new JsonArray(joints.Select(j => (JsonNode?)JsonValue.Create(map[GltfInteger.Int32(j)])).ToArray());
            if (skin?["skeleton"] is { } s) skin["skeleton"] = map[GltfInteger.Int32(s)];
        }
    }
}
