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
    /// <summary>Removes node <paramref name="index"/> and its descendants. Meshes, materials and buffers stay (unused ones are valid glTF).</summary>
    public static void Remove(JsonObject root, int index) => Remove(root, [index]);
    /// <summary>Removes each of <paramref name="indices"/> (the copies of an instance's node) with its descendants.</summary>
    public static void Remove(JsonObject root, IReadOnlyCollection<int> indices)
    {
        var nodes = Nodes(root);
        HashSet<int> removed = [];
        foreach (int index in indices) removed.UnionWith(Subtree(nodes, index));
        // A removed node another node still lists (a second parent) would leave a dangling child.
        for (int i = 0; i < nodes.Count; i++)
            if (!removed.Contains(i) && Children(nodes, i).Any(c => removed.Contains(c) && !indices.Contains(c)))
                throw new InvalidDataException($"Node {i} also holds a part of node {indices.First()}; the file shares nodes in a way glTF does not allow.");
        foreach (var animation in root["animations"] as JsonArray ?? [])
            foreach (var channel in animation?["channels"] as JsonArray ?? [])
                if (channel?["target"]?["node"] is { } target && removed.Contains(GltfInteger.Int32(target)))
                    throw new InvalidDataException("An animation of the file moves a node the deletion removes; remove the animation in Blender first.");
        foreach (var skin in root["skins"] as JsonArray ?? [])
            if ((skin?["joints"] as JsonArray ?? []).Any(j => removed.Contains(GltfInteger.Int32(j))) || skin?["skeleton"] is { } s && removed.Contains(GltfInteger.Int32(s)))
                throw new InvalidDataException($"A skin of the file uses a node the deletion removes; remove it in Blender first.");
        foreach (int index in indices) Detach(root, nodes, index);
        int[] map = new int[nodes.Count]; int next = 0;
        for (int i = 0; i < nodes.Count; i++) map[i] = removed.Contains(i) ? -1 : next++;
        for (int i = nodes.Count - 1; i >= 0; i--) if (removed.Contains(i)) nodes.RemoveAt(i);
        Renumber(root, map);
    }

    /// <summary>
    /// Copies node <paramref name="index"/> and its descendants (sharing their meshes) and places the copy right after it,
    /// named <paramref name="name"/>. Shared parts inside the copy get new instance numbers, so the copy owns them; copies of
    /// an instance's copies pass one <paramref name="instances"/> map, so theirs are copies of one new instance again.
    /// Returns the copy's index. The file's other nodes keep their indices.
    /// </summary>
    public static int Duplicate(JsonObject root, int index, string name, Dictionary<long, long>? instances = null)
    {
        var nodes = Nodes(root);
        List<int> order = []; HashSet<int> seen = []; Collect(index);
        Dictionary<int, int> copies = []; for (int k = 0; k < order.Count; k++) copies[order[k]] = nodes.Count + k;
        long nextInstance = nodes.Select(n => Instance((((n as JsonObject)?["extras"] as JsonObject)?[WorldGltf.Key] as JsonObject)?["instance"]) ?? 0).DefaultIfEmpty(0).Max() + 1;
        instances ??= [];
        foreach (int original in order)
        {
            var copy = (JsonObject)nodes[original]!.DeepClone();
            if (copy["children"] is JsonArray children) copy["children"] = new JsonArray(children.Select(c => (JsonNode?)JsonValue.Create(copies[GltfInteger.Int32(c)])).ToArray());
            if ((copy["extras"] as JsonObject)?[WorldGltf.Key] is JsonObject marked && Instance(marked["instance"]) is long instance)
            {
                if (!instances.TryGetValue(instance, out long renumbered)) instances[instance] = renumbered = nextInstance++;
                marked["instance"] = renumbered;
            }
            nodes.Add(copy);
        }
        var top = (JsonObject)nodes[copies[index]]!;
        top["name"] = name;
        // The engine name too, so an editor's suffix rules (".001") never rename the copy on import.
        if (top["extras"] is not JsonObject extras) top["extras"] = extras = new JsonObject();
        if (extras[WorldGltf.Key] is not JsonObject recoil) extras[WorldGltf.Key] = recoil = new JsonObject();
        recoil["name"] = name;
        // Beside the original: in its parent's children, or among the scene roots that hold it.
        int copied = copies[index];
        if (Parent(nodes, index) is int parent) InsertAfter((JsonArray)nodes[parent]!["children"]!, index, copied);
        else foreach (var scene in root["scenes"] as JsonArray ?? []) if (scene?["nodes"] is JsonArray list && list.Any(n => GltfInteger.OptionalInt32(n, "node") == index)) InsertAfter(list, index, copied);
        return copied;

        void Collect(int i)
        {
            if (!seen.Add(i)) throw new InvalidDataException($"Node {i} is reached twice below node {index}.");
            if (order.Count >= GltfDocument.MaximumNodes) throw new InvalidDataException("The node has too many descendants to copy.");
            order.Add(i);
            foreach (int c in Children(nodes, i)) Collect(c);
        }
    }

    /// <summary>
    /// Moves node <paramref name="index"/> under <paramref name="parent"/> (null: a root of the default scene), keeping its
    /// place in the world: its local transform becomes its world transform relative to the new parent. It keeps its zone
    /// too: a node without a zone of its own takes its parent's, so when the new place would give it another, the one it
    /// had is written to it (<paramref name="currentZone"/>, the built node's, when it came from whatever loads the file;
    /// without it, such a move is refused).
    /// </summary>
    public static void Reparent(JsonObject root, int index, int? parent, uint? currentZone = null)
    {
        var nodes = Nodes(root);
        if (parent is int p && (p < 0 || p >= nodes.Count)) throw new InvalidDataException($"The file has no node {p}.");
        if (parent is int q && Subtree(nodes, index).Contains(q)) throw new InvalidDataException("A node cannot move under itself or one of its descendants.");
        if (Parent(nodes, index) == parent && (parent != null || SceneRoots(root).Contains(index))) return;
        Matrix4x4 world = World(root, index), parentWorld = parent is int np ? World(root, np) : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentWorld, out var inverse)) throw new InvalidDataException("The new parent's transform cannot be inverted (a zero scale).");
        var moved = (JsonObject)nodes[index]!;
        bool ownZone = OwnZone(moved) != null;
        uint? zoneBefore = ownZone ? null : FileZone(nodes, Parent(nodes, index));
        uint? inheritedZone = !ownZone && zoneBefore != FileZone(nodes, parent)
            ? zoneBefore ?? currentZone ?? throw new InvalidDataException("The node takes its zone from what loads the file, which differs between its copies; under the new parent the file's zone would replace it, so move it in Blender or the scripts.")
            : null;
        Detach(root, nodes, index);
        if (inheritedZone is uint keep)
        {
            // From outside the file, the zone is the built node's; without one (copies that differ), it cannot be kept.
            if (moved["extras"] is not JsonObject extras) moved["extras"] = extras = new JsonObject();
            if (extras[WorldGltf.Key] is not JsonObject engine) extras[WorldGltf.Key] = engine = new JsonObject();
            engine["zone"] = (int)(keep & 0xFF);
        }
        if (parent is int target)
        {
            var node = (JsonObject)nodes[target]!;
            if (node["children"] is not JsonArray children) node["children"] = children = [];
            children.Add(index);
        }
        else
        {
            var scenes = root["scenes"] as JsonArray ?? throw new InvalidDataException("The file has no scene to hold a root node.");
            int scene = GltfInteger.OptionalInt32(root["scene"], "scene") ?? 0;
            if (scene < 0 || scene >= scenes.Count) throw new InvalidDataException("The default scene does not exist.");
            if (scenes.Count == 0 || scenes[scene] is not JsonObject chosen) throw new InvalidDataException("The file has no scene to hold a root node.");
            if (chosen["nodes"] is not JsonArray list) chosen["nodes"] = list = [];
            list.Add(index);
        }
        // Under a far larger parent the node's own scale would fall below what an edit can read back (a flattened axis).
        // (A node already flattened stays so wherever it moves.)
        var local = world * inverse;
        // A parent whose matrix is sheared and badly conditioned inverts imprecisely: the node must land where it is.
        local.M14 = local.M24 = local.M34 = 0; local.M44 = 1;
        var placed = local * parentWorld;
        // Positions round with their magnitudes: the node's, its parent's and the local offset turned into the parent's space.
        static float Largest(Matrix4x4 m) => new[] { m.M41, m.M42, m.M43 }.Max(MathF.Abs);
        float basis = new[] { new Vector3(parentWorld.M11, parentWorld.M12, parentWorld.M13), new Vector3(parentWorld.M21, parentWorld.M22, parentWorld.M23), new Vector3(parentWorld.M31, parentWorld.M32, parentWorld.M33) }.Max(v => v.Length());
        float reach = MathF.Max(1, MathF.Max(Largest(world), MathF.Max(Largest(parentWorld), Largest(local) * basis)));
        for (int row = 0; row < 4; row++)
        {
            Vector3 wanted = new(world[row, 0], world[row, 1], world[row, 2]), got = new(placed[row, 0], placed[row, 1], placed[row, 2]);
            if (!((got - wanted).Length() <= 1e-4f * (row < 3 ? MathF.Max(wanted.Length(), 1e-12f) : reach)))
                throw new InvalidDataException("The new parent's transform cannot be undone precisely enough to keep the node in place (its matrix is sheared and badly conditioned); choose another parent.");
        }
        static bool Flat(Matrix4x4 m) => new Vector3(m.M11, m.M12, m.M13).Length() < 1e-6f || new Vector3(m.M21, m.M22, m.M23).Length() < 1e-6f || new Vector3(m.M31, m.M32, m.M33).Length() < 1e-6f;
        if (Flat(local) && !Flat(Local((JsonObject)nodes[index]!)))
            throw new InvalidDataException("Under the new parent the node's scale would fall below 1e-6 of its parent's, which flattens it for later edits; choose another parent.");
        if (MathF.Abs(local.M41) > SourceWorlds.MaximumCoordinate || MathF.Abs(local.M42) > SourceWorlds.MaximumCoordinate || MathF.Abs(local.M43) > SourceWorlds.MaximumCoordinate)
            throw new InvalidDataException($"Under the new parent the node's position would lie beyond ±{SourceWorlds.MaximumCoordinate:N0} of it; choose another parent.");
        SetLocal((JsonObject)nodes[index]!, local);
    }

    /// <summary>The zone a node states (its zone, or its zone word's low byte), which its children inherit.</summary>
    private static uint? OwnZone(JsonObject node) => WorldGltf.StatedZone((node["extras"] as JsonObject)?[WorldGltf.Key]);
    /// <summary>The zone a node at <paramref name="index"/> takes within the file: its own or its nearest ancestor's; null when it comes from outside.</summary>
    private static uint? FileZone(JsonArray nodes, int? index)
    {
        for (int depth = 0; index is int i && i >= 0 && i < nodes.Count && depth <= 1024; depth++, index = Parent(nodes, i))
            if (nodes[i] is JsonObject node && OwnZone(node) is uint zone) return zone;
        return null;
    }
    /// <summary>
    /// A node's local transform as the reader composes it (matrix, or scale · rotation · translation; identity when it states
    /// none). A malformed transform is refused as the reader refuses it (<see cref="GltfDocument.LocalTransform"/>).
    /// </summary>
    public static Matrix4x4 Local(JsonObject node) => GltfDocument.LocalTransform(node) ?? Matrix4x4.Identity;
    /// <summary>Sets a node's local transform, as translation, rotation and scale when it decomposes, otherwise as a matrix.</summary>
    public static void SetLocal(JsonObject node, Matrix4x4 m)
    {
        node.Remove("matrix"); node.Remove("translation"); node.Remove("rotation"); node.Remove("scale");
        if (m.IsIdentity) return;
        // TRS only when it gives the matrix back, each axis within a fraction of its own length: Decompose replaces axes
        // shorter than its epsilon, which an absolute comparison would let through as a turned object.
        if (Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation)
            && Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation) is var recomposed
            && Enumerable.Range(0, 3).All(row =>
            {
                Vector3 wanted = new(m[row, 0], m[row, 1], m[row, 2]), got = new(recomposed[row, 0], recomposed[row, 1], recomposed[row, 2]);
                return (got - wanted).Length() <= 1e-4f * wanted.Length();
            }))
        {
            if (translation != Vector3.Zero) node["translation"] = new JsonArray(translation.X, translation.Y, translation.Z);
            if (rotation != Quaternion.Identity) node["rotation"] = new JsonArray(rotation.X, rotation.Y, rotation.Z, rotation.W);
            if (scale != Vector3.One) node["scale"] = new JsonArray(scale.X, scale.Y, scale.Z);
            return;
        }
        node["matrix"] = new JsonArray(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    }
    /// <summary>A node's transform in the file's scene: its local transform under each ancestor's.</summary>
    public static Matrix4x4 World(JsonObject root, int index)
    {
        var nodes = Nodes(root);
        var m = Matrix4x4.Identity; int? at = index;
        for (int depth = 0; at is int i; depth++)
        {
            if (depth > GltfDocument.MaximumDepth) throw new InvalidDataException("The node hierarchy is cyclic or too deep.");
            m *= GltfDocument.LocalTransform((JsonObject)nodes[i]!, i) ?? Matrix4x4.Identity;
            at = Parent(nodes, i);
        }
        return m;
    }
    /// <summary>An instance marker as the importer reads it: a whole number, written as an integer or a float.</summary>
    private static long? Instance(JsonNode? node) => GltfInteger.TryInt64(node, out long i) && i is >= 1 and <= int.MaxValue ? i : null;
    /// <summary>
    /// Node <paramref name="index"/> and, when an instance holds it (a node the file places under several parents, written as
    /// copies with one mark, or a node inside one), the nodes standing for it in the instance's other copies: the importer
    /// reads the first copy and joins the others to it, so all of them must change alike. The node comes first.
    /// </summary>
    public static IReadOnlyList<int> InstanceCopies(JsonObject root, int index)
    {
        var nodes = Nodes(root);
        if (index < 0 || index >= nodes.Count) throw new InvalidDataException($"The file has no node {index}.");
        int[] parents = new int[nodes.Count], places = new int[nodes.Count];
        Array.Fill(parents, -1);
        for (int i = 0; i < nodes.Count; i++) { int k = 0; foreach (int c in Children(nodes, i)) { if (parents[c] < 0) { parents[c] = i; places[c] = k; } k++; } }
        // What the importer makes of a node: a marked node is its instance, a node below one the child at its place in the
        // instance's node; a node no instance holds is itself (no key).
        string?[] keys = new string?[nodes.Count]; bool[] known = new bool[nodes.Count];
        long? Mark(int i) => Instance(((nodes[i]!["extras"] as JsonObject)?[WorldGltf.Key] as JsonObject)?["instance"]);
        string? Key(int i)
        {
            List<int> chain = [];
            for (int at = i; !known[at]; at = parents[at])
            {
                chain.Add(at);
                if (Mark(at) != null || parents[at] < 0) break;
                if (chain.Count > GltfDocument.MaximumDepth) throw new InvalidDataException("The node hierarchy is cyclic or too deep.");
            }
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                int n = chain[k];
                keys[n] = Mark(n) is long mark ? $"i{mark}" : parents[n] >= 0 && keys[parents[n]] is { } above ? $"{above}/{places[n]}" : null;
                known[n] = true;
            }
            return keys[i];
        }
        if (Key(index) is not { } key) return [index];
        List<int> copies = [index];
        for (int i = 0; i < nodes.Count; i++) if (i != index && Key(i) == key) copies.Add(i);
        return copies;
    }
    /// <summary>
    /// The zone import gives each node the file places under several parents (by its instance mark, in the order import
    /// reaches them): the zone its first copy states, or the one that copy's parents in the file pass on to it; null when
    /// that comes from whatever loads the file. Import reads the first copy it reaches through the scene's roots and their
    /// children and joins the later ones to it, so moving or removing the node that holds that copy, or moving another
    /// copy's holder before it, gives the shared node the zone of the copy read instead.
    /// </summary>
    public static IReadOnlyList<(long Mark, uint? Zone)> InstanceZones(JsonObject root)
    {
        var nodes = Nodes(root);
        List<(long, uint?)> zones = [];
        HashSet<long> marks = []; HashSet<int> reached = [];
        foreach (int index in ImportRoots(root, nodes)) Walk(index, null, 0);
        return zones;

        void Walk(int i, uint? passed, int depth)
        {
            if (depth > GltfDocument.MaximumDepth || !reached.Add(i)) return;
            var node = (JsonObject)nodes[i]!;
            var engine = (node["extras"] as JsonObject)?[WorldGltf.Key] as JsonObject;
            // A terrain recipe's marker stands for its pieces; import reads no node below it.
            if (depth == 0 && engine?["terrain"] != null) return;
            uint? zone = OwnZone(node) ?? passed;
            if (Instance(engine?["instance"]) is long mark)
            {
                // A later copy is the node already read: import does not read below it.
                if (!marks.Add(mark)) return;
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
    public static int? Parent(JsonObject root, int index) => Parent(Nodes(root), index);

    private static JsonArray Nodes(JsonObject root) =>
        root["nodes"] as JsonArray is { } nodes && nodes.All(n => n is JsonObject) ? nodes : throw new InvalidDataException("The file has no valid node list.");
    private static IEnumerable<int> Children(JsonArray nodes, int i) => (nodes[i]!["children"] as JsonArray ?? []).Select(c => GltfInteger.Int32(c) is int k && k >= 0 && k < nodes.Count ? k : throw new InvalidDataException($"Node {i} lists an invalid child."));
    private static int? Parent(JsonArray nodes, int index)
    {
        if (index < 0 || index >= nodes.Count) throw new InvalidDataException($"The file has no node {index}.");
        for (int i = 0; i < nodes.Count; i++) if (Children(nodes, i).Contains(index)) return i;
        return null;
    }
    private static HashSet<int> SceneRoots(JsonObject root) => (root["scenes"] as JsonArray ?? []).SelectMany(s => s?["nodes"] as JsonArray ?? []).Select(n => GltfInteger.Int32(n)).ToHashSet();
    /// <summary>Node <paramref name="index"/> and every node below it in the file.</summary>
    public static IReadOnlySet<int> Descendants(JsonObject root, int index) => Subtree(Nodes(root), index);
    private static HashSet<int> Subtree(JsonArray nodes, int index)
    {
        if (index < 0 || index >= nodes.Count) throw new InvalidDataException($"The file has no node {index}.");
        HashSet<int> seen = []; Stack<int> pending = new([index]);
        while (pending.TryPop(out int i)) if (seen.Add(i)) foreach (int c in Children(nodes, i)) pending.Push(c);
        return seen;
    }
    /// <summary>Removes a node from its parent's children and from every scene's roots.</summary>
    private static void Detach(JsonObject root, JsonArray nodes, int index)
    {
        if (Parent(nodes, index) is int parent) Remove((JsonArray)nodes[parent]!["children"]!, index, nodes[parent] as JsonObject, "children");
        foreach (var scene in root["scenes"] as JsonArray ?? []) if (scene?["nodes"] is JsonArray list) Remove(list, index, null, null);
        static void Remove(JsonArray list, int value, JsonObject? owner, string? property)
        {
            for (int k = list.Count - 1; k >= 0; k--) if (GltfInteger.OptionalInt32(list[k], "node") == value) list.RemoveAt(k);
            // glTF requires a children array, when present, to be non-empty.
            if (owner != null && list.Count == 0) owner.Remove(property!);
        }
    }
    private static void InsertAfter(JsonArray list, int after, int value)
    {
        int at = list.Select((n, k) => (n, k)).First(p => GltfInteger.OptionalInt32(p.n, "node") == after).k;
        list.Insert(at + 1, value);
    }
    /// <summary>Applies a node renumbering (−1: removed) to every node reference of the file.</summary>
    private static void Renumber(JsonObject root, int[] map)
    {
        foreach (var node in Nodes(root))
            if (node!["children"] is JsonArray children) node["children"] = new JsonArray(children.Select(c => (JsonNode?)JsonValue.Create(map[GltfInteger.Int32(c)])).ToArray());
        foreach (var scene in root["scenes"] as JsonArray ?? [])
            if (scene?["nodes"] is JsonArray list) scene["nodes"] = new JsonArray(list.Select(n => map[GltfInteger.Int32(n)]).Where(n => n >= 0).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        foreach (var animation in root["animations"] as JsonArray ?? [])
            foreach (var channel in animation?["channels"] as JsonArray ?? [])
                if (channel?["target"] is JsonObject target && target["node"] is { } v) target["node"] = map[GltfInteger.Int32(v)];
        foreach (var skin in root["skins"] as JsonArray ?? [])
        {
            if (skin?["joints"] is JsonArray joints) skin["joints"] = new JsonArray(joints.Select(j => (JsonNode?)JsonValue.Create(map[GltfInteger.Int32(j)])).ToArray());
            if (skin?["skeleton"] is { } s) skin["skeleton"] = map[GltfInteger.Int32(s)];
        }
    }
}
