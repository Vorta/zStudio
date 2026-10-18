using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;

namespace zStudio.Core.Worlds;

/// <summary>Hierarchy and shared-instance checks used by source editing and imports.</summary>
public static partial class WorldGltf
{
    /// <summary>The nodes in the order import reaches them: the scene's roots in order, each node before its children.</summary>
    internal static List<int> ImportOrder(JsonObject root, JsonArray nodes)
    {
        List<int> roots = [];
        if (root["scenes"] is JsonArray { Count: > 0 } scenes)
        {
            int scene = GltfInteger.OptionalInt32(root["scene"], "scene") ?? 0;
            foreach (var r in (scenes[scene] as JsonObject)?["nodes"] as JsonArray ?? []) if (Index(r, nodes.Count) is { } index) roots.Add(index);
        }
        else
        {
            // Without scenes, every node that is nobody's child is a root.
            var parents = Parents(nodes);
            for (int i = 0; i < nodes.Count; i++) if (parents[i] < 0) roots.Add(i);
        }
        List<int> order = []; bool[] seen = new bool[nodes.Count];
        Stack<int> pending = new(Enumerable.Reverse(roots));
        while (pending.TryPop(out int i))
        {
            if (seen[i]) continue;
            seen[i] = true; order.Add(i);
            if ((nodes[i] as JsonObject)?["children"] is JsonArray children)
                for (int k = children.Count - 1; k >= 0; k--) if (Index(children[k], nodes.Count) is { } child && !seen[child]) pending.Push(child);
        }
        return order;
    }

    /// <summary>Each node's parent in a glTF node list (the first that lists it), or −1.</summary>
    private static int[] Parents(JsonArray nodes)
    {
        int[] parents = new int[nodes.Count]; Array.Fill(parents, -1);
        for (int i = 0; i < nodes.Count; i++)
            foreach (var child in (nodes[i] as JsonObject)?["children"] as JsonArray ?? [])
                if (Index(child, nodes.Count) is { } c && c != i && parents[c] < 0) parents[c] = i;
        return parents;
    }
    /// <summary>
    /// Refuses a file whose copies of a shared node (<c>extras.recoil.instance</c>) differ in anything import reads: name,
    /// engine values, zone, transform, mesh or children, compared copy against first copy below the copies themselves.
    /// Import reads the first copy in the file and ignores the others, so an edit made to one copy only would be lost, or
    /// would move every placement (world-editor-plan: copies that disagree are rejected). A zone that comes from whatever
    /// loads the file is unknown here and agrees with any; a shared node inside a copy is compared with its own copies.
    /// </summary>
    public static void CheckInstances(GltfDocument doc, string path)
    {
        Dictionary<long, List<(GltfNode Node, uint? Zone)>> copies = [];
        HashSet<GltfNode> seen = new(ReferenceEqualityComparer.Instance);
        void Collect(GltfNode node, uint? parentZone, int depth)
        {
            if (depth > GltfDocument.MaximumDepth || !seen.Add(node)) return;
            uint? zone = StatedZone(node.Extras?[Key]) ?? parentZone;
            if (Mark(node) is { } mark)
            {
                if (!copies.TryGetValue(mark, out var list)) copies[mark] = list = [];
                list.Add((node, zone));
            }
            foreach (var child in node.Children) Collect(child, zone, depth + 1);
        }
        foreach (var root in doc.Roots) Collect(root, null, 0);
        long work = 0;
        // Each pair of distinct meshes is compared once: copies may all use one mesh, and the first copy's mesh compared with
        // it again for each copy would repeat a comparison of up to millions of elements and megabytes of values every time.
        Dictionary<(GltfMesh, GltfMesh), bool> meshes = [];
        foreach (var (mark, list) in copies)
            for (int k = 1; k < list.Count; k++)
                if (Difference(list[0].Node, list[0].Zone, list[k].Node, list[k].Zone, 0) is { } difference)
                    throw new InvalidDataException($"{path}: the copies of shared node {JsonData.ShownText(EngineName(list[0].Node))} (instance {mark}) differ in {difference}. Every copy is the same node, which import reads from the first copy; change all of them alike, or make one a node of its own by removing its recoil → instance property.");

        string? Difference(GltfNode a, uint? zoneA, GltfNode b, uint? zoneB, int depth)
        {
            if (++work > GltfDocument.MaximumNodes * 4L || depth > GltfDocument.MaximumDepth) throw new InvalidDataException($"{path}: the copies of its shared nodes are too large to compare.");
            if (EngineName(a) != EngineName(b)) return $"name ({JsonData.ShownText(EngineName(a))}, {JsonData.ShownText(EngineName(b))})";
            if (!a.Weights.SequenceEqual(b.Weights)) return $"the morph weights of {JsonData.ShownText(EngineName(a))}";
            if (!SameValues(a, b)) return $"the engine values of {JsonData.ShownText(EngineName(a))}";
            if (zoneA is { } za && zoneB is { } zb && za != zb) return $"the zone of {JsonData.ShownText(EngineName(a))} ({za}, {zb})";
            if (!SameMatrix(a.Matrix ?? Matrix4x4.Identity, b.Matrix ?? Matrix4x4.Identity)) return $"the transform of {JsonData.ShownText(EngineName(a))}";
            if (!MeshesAgree(a.Mesh, b.Mesh)) return $"the mesh of {JsonData.ShownText(EngineName(a))}";
            if (a.Children.Count != b.Children.Count) return $"the children of {JsonData.ShownText(EngineName(a))}";
            for (int k = 0; k < a.Children.Count; k++)
            {
                GltfNode childA = a.Children[k], childB = b.Children[k];
                if (Mark(childA) is { } inner && Mark(childB) == inner) continue;
                if (Difference(childA, StatedZone(childA.Extras?[Key]) ?? zoneA, childB, StatedZone(childB.Extras?[Key]) ?? zoneB, depth + 1) is { } found) return found;
            }
            return null;
        }
        bool MeshesAgree(GltfMesh? x, GltfMesh? y)
        {
            if (x == null || y == null || ReferenceEquals(x, y)) return SameMesh(x, y);
            if (!meshes.TryGetValue((x, y), out bool same)) meshes[(x, y)] = same = SameMesh(x, y);
            return same;
        }
    }

    /// <summary>A node's instance number, as import reads it (a whole number from 1); null for none or an invalid one.</summary>
    private static long? Mark(GltfNode node) => Mark((node.Extras?[Key] as JsonObject)?["instance"]);
    private static long? Mark(JsonNode? instance)
    {
        return GltfInteger.TryInt64(instance, out long n) && n is >= 1 and <= int.MaxValue ? n : null;
    }
    /// <summary>
    /// Whether two nodes have the same engine values apart from those copies may write differently: their zones (relative to
    /// each parent) and their instance numbers and names (compared on their own). Compared where they are, as
    /// <see cref="JsonNode.DeepEquals"/> compares objects, without copying either: the first copy is compared with every other,
    /// and a copy of its values for each comparison would repeat whatever those leave out (any size) as often as there are copies.
    /// </summary>
    private static bool SameValues(GltfNode a, GltfNode b)
    {
        JsonObject? x = a.Extras?[Key] as JsonObject, y = b.Extras?[Key] as JsonObject;
        if (StoredNodeFlags(x) != StoredNodeFlags(y)) return false;
        if (x == null || y == null) return (x ?? y)?.All(p => Ignored(p.Key)) != false;
        int compared = 0;
        foreach (var (name, value) in x)
        {
            if (Ignored(name)) continue;
            compared++;
            // As DeepEquals does: a missing value of the other compares as null.
            y.TryGetPropertyValue(name, out var other);
            if (!JsonNode.DeepEquals(value, other)) return false;
        }
        return compared == y.Count(p => !Ignored(p.Key));
        static bool Ignored(string key) => key is "zone" or "instance" or "name" || NodeFlagFields.Contains(key);
    }
    private static bool SameMatrix(Matrix4x4 a, Matrix4x4 b)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (!(Math.Abs(a[row, column] - b[row, column]) <= 1e-5f * Math.Max(1, Math.Max(Math.Abs(a[row, column]), Math.Abs(b[row, column]))))) return false;
        return true;
    }
    /// <summary>The same glTF mesh, or meshes an editor wrote separately with the same content.</summary>
    private static bool SameMesh(GltfMesh? a, GltfMesh? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Primitives.Count != b.Primitives.Count || !a.Weights.SequenceEqual(b.Weights) || !JsonNode.DeepEquals(a.Extras, b.Extras)) return false;
        for (int i = 0; i < a.Primitives.Count; i++)
        {
            GltfPrimitive p = a.Primitives[i], q = b.Primitives[i];
            if (!ReferenceEquals(p.Material, q.Material) || !p.Positions.SequenceEqual(q.Positions) || !p.Normals.SequenceEqual(q.Normals) || !p.TexCoords.SequenceEqual(q.TexCoords)
                || !p.Indices.SequenceEqual(q.Indices) || p.Targets.Count != q.Targets.Count || !p.Targets.Zip(q.Targets).All(t => t.First.SequenceEqual(t.Second)) || !JsonNode.DeepEquals(p.Extras, q.Extras)) return false;
        }
        return true;
    }
}
