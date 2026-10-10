using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Gltf;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>Rules for model files an editor that knows nothing of the engine wrote back (Blender).</summary>
public static partial class WorldGltf
{
    /// <summary>
    /// Marks, in a copy for an editor, a node that has no zone of its own and none from its parents in the file: it takes
    /// its zone from whatever loads the file (0xFF for a script's load, the referencing node's for a reference).
    /// </summary>
    public const string ZoneFromLoad = "zoneFromLoad";

    /// <summary>
    /// Prepares a copy of a model for an editor that moves nodes freely: a node without a zone of its own takes its
    /// parent's, so a move would change it. Each such node is given the zone its parents in the file give it, or, when that
    /// comes from whatever loads the file, the <see cref="ZoneFromLoad"/> mark. A node without a name (1999 m9's node under
    /// sgate1–sgate8) states its empty engine name: Blender names it after its place in the file (<c>Node_459</c>), which
    /// would otherwise become its engine name and differ between the copies of a shared node. <see cref="ImplicitZones"/>
    /// undoes all of this.
    /// </summary>
    public static void ExplicitZones(JsonObject root)
    {
        if (root["nodes"] is not JsonArray nodes) return;
        foreach (var item in nodes)
            if (item is JsonObject node && string.IsNullOrEmpty(Text(node["name"])) && ((node["extras"] as JsonObject)?[Key] as JsonObject)?["name"] == null && Engine(node) is { } named)
                named["name"] = "";
        var parents = Parents(nodes);
        var zones = FileZones(nodes, parents);
        // A shared node is one node, which import takes, with its zone, from its first copy in the file. A later copy
        // without a zone of its own under a parent of another zone states the first copy's, as the build reads it, so the
        // copies agree when the editor writes them back (CheckInstances refuses copies that differ).
        Dictionary<long, uint?> shared = [];
        bool stated = false;
        foreach (int i in ImportOrder(root, nodes))
        {
            if (nodes[i] is not JsonObject node || Mark(((node["extras"] as JsonObject)?[Key] as JsonObject)?["instance"]) is not { } mark) continue;
            if (!shared.TryGetValue(mark, out var first)) { shared[mark] = zones[i].Zone; continue; }
            if (first is { } zone && zones[i].Zone != zone && StatedZone((node["extras"] as JsonObject)?[Key]) == null && Engine(node) is { } copy) { copy["zone"] = (int)zone; stated = true; }
        }
        if (stated) zones = FileZones(nodes, parents);
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not JsonObject node || StatedZone((node["extras"] as JsonObject)?[Key]) != null || Engine(node) is not { } engine) continue;
            if (zones[i].Zone is { } zone) engine["zone"] = (int)zone; else engine[ZoneFromLoad] = true;
        }
    }

    /// <summary>
    /// Undoes <see cref="ExplicitZones"/> on what the editor wrote: a zone the node's new parent in the file gives it anyway
    /// is left out again, one a move made necessary stays, and the marks go. A marked node moved under a node that had a
    /// zone in the file would take that zone instead of the one it had, which the file does not hold: refused. (Under a
    /// marked node the artist gave a zone, it takes that zone, as the artist meant.) A node whose engine name is empty has
    /// no name again, so the name the editor made up for it (<c>Node_459</c>) does not stay in the file.
    /// </summary>
    public static void ImplicitZones(JsonObject root, string path)
    {
        if (root["nodes"] is not JsonArray nodes) return;
        var parents = Parents(nodes); var zones = FileZones(nodes, parents);
        bool[] marks = [.. nodes.Select(n => ((n as JsonObject)?["extras"] as JsonObject)?[Key] is JsonObject e && e[ZoneFromLoad] != null)];
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not JsonObject node || (node["extras"] as JsonObject)?[Key] is not JsonObject engine) continue;
            // Without a name, the engine name is the empty one the node states.
            if (Text(engine["name"]) is "") { engine.Remove("name"); node.Remove("name"); }
            var (passed, owner) = parents[i] >= 0 ? zones[parents[i]] : (null, -1);
            bool marked = engine.Remove(ZoneFromLoad);
            if (StatedZone(engine) is { } own)
            {
                if (own == passed && engine["zone"] != null && engine["zoneWord"] == null) engine.Remove("zone");
            }
            else if (marked && passed is { } zone && !marks[owner])
                throw new InvalidDataException($"{path}: {JsonData.ShownText(EngineName(node))} took its zone from what loads the file; under {JsonData.ShownText(EngineName((JsonObject)nodes[parents[i]]!))} it would take zone {zone} instead. Give it a zone of its own (custom property recoil → zone, 255 for any zone) or move it back.");
            // A node that had no engine values gets none.
            if (engine.Count == 0 && node["extras"] is JsonObject extras) { extras.Remove(Key); if (extras.Count == 0) node.Remove("extras"); }
        }
    }

    /// <summary>
    /// Makes the engine name a node records follow a rename made in the editor, in a file it wrote back (a Blender export of
    /// a checkout). A node records its name (<c>extras.recoil.name</c>) where the name repeats, looks like a Blender copy or,
    /// in a checkout, is empty, and import reads that record, not the node's name, so a node renamed in Blender would keep
    /// its old name. Names are compared without the editor's copy suffix (<c>.001</c>). A node is renamed when its name is
    /// neither the name it records nor one a node of the model as the project holds it (<paramref name="project"/>) has with
    /// that record, while the record is one the project's model has, or empty for its unnamed nodes: a record changed in the
    /// editor (recoil → name, also to name an unnamed node) is the artist's and stays. An unnamed node keeps no name while
    /// the editor shows the name it makes up for one (Blender's <c>Node_459</c>, or the name of the node's mesh). The record
    /// becomes the name without the suffix; a name the engine cannot store is refused, saying to rename the node. Returns
    /// each rename (old and new engine name) in the order of the file's nodes.
    /// </summary>
    public static IReadOnlyList<(string Old, string New)> FollowShownNames(JsonObject root, JsonObject project, string path)
    {
        if (root["nodes"] is not JsonArray nodes) return [];
        var meshes = root["meshes"] as JsonArray ?? [];
        // The records the project's model gives its nodes, and the names it gives them with: a record among them is the
        // checkout's, not one typed in the editor, and a name the project gives it with is no rename.
        HashSet<string> recorded = new(StringComparer.Ordinal);
        HashSet<(string Name, string Record)> given = [];
        foreach (var item in project["nodes"] as JsonArray ?? [])
        {
            if (item is not JsonObject node) continue;
            string name = Text(node["name"]) ?? "";
            if (Text(((node["extras"] as JsonObject)?[Key] as JsonObject)?["name"]) is { } record) { recorded.Add(record); given.Add((BlenderSuffix().Replace(name, ""), record)); }
            else if (name.Length == 0) recorded.Add("");
        }
        List<(string, string)> renamed = [];
        foreach (var item in nodes)
        {
            if (item is not JsonObject node || (node["extras"] as JsonObject)?[Key] is not JsonObject engine || Text(engine["name"]) is not { } old
                || !recorded.Contains(old) || Text(node["name"]) is not { Length: > 0 } name) continue;
            string shown = BlenderSuffix().Replace(name, "");
            if (shown.Length == 0 || shown == BlenderSuffix().Replace(old, "") || given.Contains((shown, old))) continue;
            // The name Blender gives a node without one: Node_ and its place in the file, or a mesh object's mesh name.
            if (old.Length == 0 && (MadeUpName().IsMatch(shown)
                || Index(node["mesh"], meshes.Count) is { } mesh && Text((meshes[mesh] as JsonObject)?["name"]) is { } meshName && BlenderSuffix().Replace(meshName, "") == shown)) continue;
            if (!IsEngineName(shown))
                throw new InvalidDataException($"{path}: node {JsonData.ShownText(name)} was renamed in Blender (from {(old.Length == 0 ? "an unnamed node" : JsonData.ShownText(old))}), but the engine stores names of at most 35 Latin-1 characters without NUL. Give it a shorter name in Blender and export again.");
            engine["name"] = shown;
            renamed.Add((old, shown));
        }
        return renamed;
    }
    [GeneratedRegex(@"\ANode_\d+\z")] private static partial Regex MadeUpName();

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
    /// The zone each node has from the file (its own or its nearest ancestor's) and the node that states it, or null and −1
    /// when it comes from what loads the file.
    /// </summary>
    private static (uint? Zone, int Owner)[] FileZones(JsonArray nodes, int[] parents)
    {
        var stated = nodes.Select(n => StatedZone(((n as JsonObject)?["extras"] as JsonObject)?[Key])).ToArray();
        var zones = new (uint? Zone, int Owner)[nodes.Count]; var done = new bool[nodes.Count]; var walked = new int[nodes.Count]; Array.Fill(walked, -1);
        List<int> chain = [];
        for (int i = 0; i < nodes.Count; i++)
        {
            if (done[i]) continue;
            // Up to a node already known, one that states a zone, or a root (whose zone comes from what loads the file); a
            // cycle (invalid glTF) ends where it closes.
            chain.Clear(); (uint? Zone, int Owner) inherited = (null, -1);
            for (int at = i; at >= 0 && walked[at] != i; at = parents[at])
            {
                if (done[at]) { inherited = zones[at]; break; }
                walked[at] = i; chain.Add(at);
                if (stated[at] != null) break;
            }
            for (int k = chain.Count - 1; k >= 0; k--) { int at = chain[k]; zones[at] = inherited = stated[at] is { } own ? (own, at) : inherited; done[at] = true; }
        }
        return zones;
    }
    /// <summary>A node's engine values, made when it has none; null when its extras have another shape.</summary>
    private static JsonObject? Engine(JsonObject node)
    {
        if (node["extras"] is null) node["extras"] = new JsonObject();
        if (node["extras"] is not JsonObject extras) return null;
        if (extras[Key] is null) extras[Key] = new JsonObject();
        return extras[Key] as JsonObject;
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
        if (x == null || y == null) return x == null && y == null;
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
        static bool Ignored(string key) => key is "zone" or "instance" or "name" or ZoneFromLoad;
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
