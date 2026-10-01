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
    public static void Remove(JsonObject root, int index)
    {
        var nodes = Nodes(root);
        var removed = Subtree(nodes, index);
        // A removed node another node still lists (a second parent) would leave a dangling child.
        for (int i = 0; i < nodes.Count; i++)
            if (!removed.Contains(i) && Children(nodes, i).Any(c => removed.Contains(c) && c != index))
                throw new InvalidDataException($"Node {i} also holds a part of node {index}; the file shares nodes in a way glTF does not allow.");
        foreach (var animation in root["animations"] as JsonArray ?? [])
            foreach (var channel in animation?["channels"] as JsonArray ?? [])
                if (channel?["target"]?["node"] is JsonValue target && target.TryGetValue(out int t) && removed.Contains(t))
                    throw new InvalidDataException($"An animation of the file moves node {t}, which the deletion removes; remove the animation in Blender first.");
        foreach (var skin in root["skins"] as JsonArray ?? [])
            if ((skin?["joints"] as JsonArray ?? []).Any(j => j is JsonValue v && v.TryGetValue(out int k) && removed.Contains(k)) || skin?["skeleton"] is JsonValue s && s.TryGetValue(out int sk) && removed.Contains(sk))
                throw new InvalidDataException($"A skin of the file uses a node the deletion removes; remove it in Blender first.");
        Detach(root, nodes, index);
        int[] map = new int[nodes.Count]; int next = 0;
        for (int i = 0; i < nodes.Count; i++) map[i] = removed.Contains(i) ? -1 : next++;
        for (int i = nodes.Count - 1; i >= 0; i--) if (removed.Contains(i)) nodes.RemoveAt(i);
        Renumber(root, map);
    }

    /// <summary>
    /// Copies node <paramref name="index"/> and its descendants (sharing their meshes) and places the copy right after it,
    /// named <paramref name="name"/>. Shared parts inside the copy get new instance numbers, so the copy owns them.
    /// Returns the copy's index.
    /// </summary>
    public static int Duplicate(JsonObject root, int index, string name)
    {
        var nodes = Nodes(root);
        List<int> order = []; HashSet<int> seen = []; Collect(index);
        Dictionary<int, int> copies = []; for (int k = 0; k < order.Count; k++) copies[order[k]] = nodes.Count + k;
        long nextInstance = nodes.Select(n => Instance((((n as JsonObject)?["extras"] as JsonObject)?[WorldGltf.Key] as JsonObject)?["instance"]) ?? 0).DefaultIfEmpty(0).Max() + 1;
        Dictionary<long, long> instances = [];
        foreach (int original in order)
        {
            var copy = (JsonObject)nodes[original]!.DeepClone();
            if (copy["children"] is JsonArray children) copy["children"] = new JsonArray(children.Select(c => (JsonNode?)JsonValue.Create(copies[c!.GetValue<int>()])).ToArray());
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
        else foreach (var scene in root["scenes"] as JsonArray ?? []) if (scene?["nodes"] is JsonArray list && list.Any(n => n?.GetValue<int>() == index)) InsertAfter(list, index, copied);
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
    /// place in the world: its local transform becomes its world transform relative to the new parent.
    /// </summary>
    public static void Reparent(JsonObject root, int index, int? parent)
    {
        var nodes = Nodes(root);
        if (parent is int p && (p < 0 || p >= nodes.Count)) throw new InvalidDataException($"The file has no node {p}.");
        if (parent is int q && Subtree(nodes, index).Contains(q)) throw new InvalidDataException("A node cannot move under itself or one of its descendants.");
        if (Parent(nodes, index) == parent && (parent != null || SceneRoots(root).Contains(index))) return;
        Matrix4x4 world = World(root, index), parentWorld = parent is int np ? World(root, np) : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentWorld, out var inverse)) throw new InvalidDataException("The new parent's transform cannot be inverted (a zero scale).");
        Detach(root, nodes, index);
        if (parent is int target)
        {
            var node = (JsonObject)nodes[target]!;
            if (node["children"] is not JsonArray children) node["children"] = children = [];
            children.Add(index);
        }
        else
        {
            var scenes = root["scenes"] as JsonArray ?? throw new InvalidDataException("The file has no scene to hold a root node.");
            int scene = root["scene"] is JsonValue s && s.TryGetValue(out int si) && si >= 0 && si < scenes.Count ? si : 0;
            if (scenes.Count == 0 || scenes[scene] is not JsonObject chosen) throw new InvalidDataException("The file has no scene to hold a root node.");
            if (chosen["nodes"] is not JsonArray list) chosen["nodes"] = list = [];
            list.Add(index);
        }
        SetLocal((JsonObject)nodes[index]!, world * inverse);
    }

    /// <summary>A node's local transform as the reader composes it (matrix, or scale · rotation · translation).</summary>
    public static Matrix4x4 Local(JsonObject node)
    {
        if (node["matrix"] is JsonArray m && m.Count == 16)
        {
            float M(int k) => m[k]!.GetValue<float>();
            return new(M(0), M(1), M(2), M(3), M(4), M(5), M(6), M(7), M(8), M(9), M(10), M(11), M(12), M(13), M(14), M(15));
        }
        Vector3 t = node["translation"] is JsonArray tr ? new(tr[0]!.GetValue<float>(), tr[1]!.GetValue<float>(), tr[2]!.GetValue<float>()) : Vector3.Zero;
        Quaternion r = node["rotation"] is JsonArray ro ? new(ro[0]!.GetValue<float>(), ro[1]!.GetValue<float>(), ro[2]!.GetValue<float>(), ro[3]!.GetValue<float>()) : Quaternion.Identity;
        Vector3 s = node["scale"] is JsonArray sc ? new(sc[0]!.GetValue<float>(), sc[1]!.GetValue<float>(), sc[2]!.GetValue<float>()) : Vector3.One;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }
    /// <summary>Sets a node's local transform, as translation, rotation and scale when it decomposes, otherwise as a matrix.</summary>
    public static void SetLocal(JsonObject node, Matrix4x4 m)
    {
        node.Remove("matrix"); node.Remove("translation"); node.Remove("rotation"); node.Remove("scale");
        if (m.IsIdentity) return;
        if (Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation)
            && (Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation) - m) is var error
            && new[] { error.M11, error.M12, error.M13, error.M21, error.M22, error.M23, error.M31, error.M32, error.M33 }.All(e => MathF.Abs(e) < 1e-4f))
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
            m *= Local((JsonObject)nodes[i]!);
            at = Parent(nodes, i);
        }
        return m;
    }
    /// <summary>An instance marker as the importer reads it: a whole number, written as an integer or a float.</summary>
    private static long? Instance(JsonNode? node) => node is JsonValue v ? v.TryGetValue(out long i) ? i : v.TryGetValue(out double d) && d == Math.Floor(d) && Math.Abs(d) < 1e15 ? (long)d : null : null;
    /// <summary>The parent of a node, or null for a root.</summary>
    public static int? Parent(JsonObject root, int index) => Parent(Nodes(root), index);

    private static JsonArray Nodes(JsonObject root) =>
        root["nodes"] as JsonArray is { } nodes && nodes.All(n => n is JsonObject) ? nodes : throw new InvalidDataException("The file has no valid node list.");
    private static IEnumerable<int> Children(JsonArray nodes, int i) => (nodes[i]!["children"] as JsonArray ?? []).Select(c => c is JsonValue v && v.TryGetValue(out int k) && k >= 0 && k < nodes.Count ? k : throw new InvalidDataException($"Node {i} lists an invalid child."));
    private static int? Parent(JsonArray nodes, int index)
    {
        if (index < 0 || index >= nodes.Count) throw new InvalidDataException($"The file has no node {index}.");
        for (int i = 0; i < nodes.Count; i++) if (Children(nodes, i).Contains(index)) return i;
        return null;
    }
    private static HashSet<int> SceneRoots(JsonObject root) => (root["scenes"] as JsonArray ?? []).SelectMany(s => s?["nodes"] as JsonArray ?? []).Select(n => n!.GetValue<int>()).ToHashSet();
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
            for (int k = list.Count - 1; k >= 0; k--) if (list[k]?.GetValue<int>() == value) list.RemoveAt(k);
            // glTF requires a children array, when present, to be non-empty.
            if (owner != null && list.Count == 0) owner.Remove(property!);
        }
    }
    private static void InsertAfter(JsonArray list, int after, int value)
    {
        int at = list.Select((n, k) => (n, k)).First(p => p.n?.GetValue<int>() == after).k;
        list.Insert(at + 1, value);
    }
    /// <summary>Applies a node renumbering (−1: removed) to every node reference of the file.</summary>
    private static void Renumber(JsonObject root, int[] map)
    {
        foreach (var node in Nodes(root))
            if (node!["children"] is JsonArray children) node["children"] = new JsonArray(children.Select(c => (JsonNode?)JsonValue.Create(map[c!.GetValue<int>()])).ToArray());
        foreach (var scene in root["scenes"] as JsonArray ?? [])
            if (scene?["nodes"] is JsonArray list) scene["nodes"] = new JsonArray(list.Select(n => map[n!.GetValue<int>()]).Where(n => n >= 0).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        foreach (var animation in root["animations"] as JsonArray ?? [])
            foreach (var channel in animation?["channels"] as JsonArray ?? [])
                if (channel?["target"] is JsonObject target && target["node"] is JsonValue v && v.TryGetValue(out int t)) target["node"] = map[t];
        foreach (var skin in root["skins"] as JsonArray ?? [])
        {
            if (skin?["joints"] is JsonArray joints) skin["joints"] = new JsonArray(joints.Select(j => (JsonNode?)JsonValue.Create(map[j!.GetValue<int>()])).ToArray());
            if (skin?["skeleton"] is JsonValue s && s.TryGetValue(out int k)) skin["skeleton"] = map[k];
        }
    }
}
