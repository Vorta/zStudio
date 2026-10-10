using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Carries a model's order and the map zone assignments of shared geometry through a Blender round trip. Blender keeps
/// every triangle with its corners in order (but one of a primitive's triangles that use the same three vertices, which
/// go back on the vertices of the one kept), but lists a mesh's triangles by material (the materials in the order they
/// first appear, the triangles of each in their order), drops the polygon boundaries a primitive records, splits vertices
/// by normal and lists children (and the file's roots) by name. So nodes correspond by engine name (a name the file uses
/// once anywhere; repeated names under corresponding parents, a node at the same place first, then in order; a renamed
/// node holding the same geometry at the same place), and triangles by position, in order within a material, a changed
/// run between unchanged triangles position by position when both sides have as many. Corresponding nodes go back to the
/// order they had (<see cref="RestoreOrder"/>) and corresponding polygons to the model's polygon order; a polygon all of
/// whose triangles correspond, consecutively, gets its recorded boundary back and keeps its zone words in every map.
/// A node Blender duplicated keeps what it copies (its engine name, marks and geometry, and its children's; <see cref="FindCopies"/>)
/// and takes the zones and map references of the node it copies, as a copy made in the world editor does; a node moved
/// to another parent where its name repeats is found the same way. Other faces and nodes take the model's own zones.
/// </summary>
internal sealed partial class SourceBlenderZones
{
    private readonly SourceMapZoneBindings bindings;
    private readonly GltfDocument before;
    private readonly string model;
    private readonly PolygonWorkBudget work;
    private readonly WorldGltf.ZoneLayout oldLayout;
    /// <summary>Each export node and the project node it continues.</summary>
    private readonly Dictionary<GltfNode, GltfNode> match;
    /// <summary>Each export node Blender copied from a project node (in a duplicated subtree), and the project node it copies.</summary>
    private readonly Dictionary<GltfNode, GltfNode> copies = new(ReferenceEqualityComparer.Instance);
    /// <summary>The top of each copied subtree and the project node it copies, in the export's order.</summary>
    private readonly List<(GltfNode Copy, GltfNode Original)> copiedTops = [];
    /// <summary>New export nodes (file index) that copy one of several project nodes the maps bind differently: why no copy was taken.</summary>
    private readonly Dictionary<int, string> undecided = [];
    /// <summary>Each export node (file index) and the project node it is (file index).</summary>
    private readonly Dictionary<int, int> origins = [];
    private readonly HashSet<int> newNodes = [];
    private readonly List<GltfNode> lostNodes = [];
    /// <summary>Per export mesh (file index): the project mesh (layout ordinal) and, per polygon written, the polygon it continues (-1: new).</summary>
    private readonly Dictionary<int, (int Mesh, List<int> Polygons)> meshes = [];
    /// <summary>
    /// Per project mesh (layout ordinal) that an export mesh continues: its polygons the export keeps (whole, or in pieces
    /// that keep their zones), those of them it keeps every triangle of, and how many new faces it has.
    /// </summary>
    private readonly Dictionary<int, (HashSet<int> Kept, HashSet<int> Whole, int Added)> continued = [];
    private readonly Dictionary<GltfNode, int> oldOrdinals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GltfMesh, int> oldMeshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GltfNode, GltfNode?> oldParents = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, GltfNode> exportParents = [];
    private readonly Dictionary<int, string> exportNames = [];
    private readonly Dictionary<int, GltfNode> exportNodes = [];
    private Dictionary<long, int>? accessorUses, viewUses;
    private readonly List<(int Buffer, byte[] Data, JsonObject Primitive, int Type, int Count)> appended = [];

    /// <summary>Finds what each node of <paramref name="export"/> continues in <paramref name="before"/>, the project's model, whose assignments <paramref name="bindings"/> hold.</summary>
    private SourceBlenderZones(SourceMapZoneBindings bindings, GltfDocument before, GltfDocument export, string model, CancellationToken token)
    {
        this.bindings = bindings; this.before = before; this.model = model;
        work = new(token);
        oldLayout = WorldGltf.ZoneLayout.Read(before, token, work);
        for (int i = 0; i < oldLayout.Meshes.Count; i++) oldMeshes.Add(oldLayout.Meshes[i], i);
        match = MatchNodes(export, work);
        FindCopies(export);
        foreach (var node in export.AllNodes().Distinct())
        {
            if (match.TryGetValue(node, out var old) || copies.TryGetValue(node, out old)) origins[node.Index] = old.Index;
            else { origins[node.Index] = -1; newNodes.Add(node.Index); }
        }
        HashSet<GltfNode> matchedOld = new(match.Values, ReferenceEqualityComparer.Instance);
        lostNodes.AddRange(oldLayout.Nodes.Where(n => !matchedOld.Contains(n)));
    }

    /// <summary>
    /// Finds what each node of <paramref name="export"/> (as read, before <see cref="RestoreOrder"/> and <see cref="Carry"/>
    /// change it) continues in <paramref name="before"/>, the project's model, whose assignments <paramref name="bindings"/> hold.
    /// </summary>
    internal static SourceBlenderZones Match(SourceMapZoneBindings bindings, GltfDocument before, GltfDocument export, string model, CancellationToken token) =>
        new(bindings, before, export, model, token);

    [GeneratedRegex(@"\.(\d{3,9})\z")] private static partial Regex Suffix();

    private readonly record struct Key(Vector3 A, Vector3 B, Vector3 C);
    private readonly record struct Triangle(int Primitive, int Index, int Polygon, Key Key);
    /// <summary>A polygon as the update writes it: its triangles in its primitive, its record, the project polygon it continues (-1: new) and the first project triangle it holds (-1: none).</summary>
    private readonly record struct Piece(int First, int Count, JsonObject Entry, int Polygon, long Place);

    /// <summary>
    /// Lists the export's roots and each node's children (in <paramref name="export"/> and <paramref name="root"/>, its JSON)
    /// in the order the project's model had them. Blender lists them by name, but the build allocates node slots in this
    /// order and a lookup by name finds the highest slot of the name, so the order decides which of several nodes of one name
    /// scripts and animations find, and it is the order the world lists its objects in (docs/engine-evidence.md). Children
    /// that continue children of the node their parent continues keep their order there; the others (new nodes, and nodes
    /// moved from another parent) follow them in the export's order. Under a new parent, nodes that continue the project's
    /// keep their order in the project's file. A reorder the export does make shows as the rebuilt world's lookup changes.
    /// </summary>
    internal void RestoreOrder(GltfDocument export, JsonObject root)
    {
        var jsonNodes = root["nodes"] as JsonArray ?? [];
        long chosen = 0;
        if (root["scenes"] is JsonArray scenes && (root["scene"] is not { } scene || GltfInteger.TryInt64(scene, out chosen)) && chosen >= 0 && chosen < scenes.Count
            && scenes[(int)chosen]?["nodes"] is JsonArray sceneNodes)
            Reorder(export.Roots, before.Roots, sceneNodes);
        foreach (var node in export.AllNodes().Distinct().ToList())
            if (node.Index >= 0 && node.Index < jsonNodes.Count && jsonNodes[node.Index]?["children"] is JsonArray children)
                Reorder(node.Children, match.TryGetValue(node, out var old) ? old.Children : null, children);

        void Reorder(List<GltfNode> list, List<GltfNode>? previous, JsonArray json)
        {
            work.Charge(1L + 2L * list.Count + (previous?.Count ?? 0));
            if (list.Count < 2 || json.Count != list.Count) return;
            // The JSON lists what was read, in the same order.
            for (int i = 0; i < list.Count; i++) if (!GltfInteger.TryInt64(json[i], out long index) || index != list[i].Index) return;
            Dictionary<GltfNode, int> places = new(ReferenceEqualityComparer.Instance);
            if (previous != null) for (int i = 0; i < previous.Count; i++) places.TryAdd(previous[i], i);
            (int Group, long Place) Order(GltfNode node, int at) =>
                !match.TryGetValue(node, out var old) ? (1, at)
                : previous == null ? (0, oldOrdinals[old])
                : places.TryGetValue(old, out int place) ? (0, place) : (1, at);
            // A stable sort: nodes of one place keep the export's order.
            List<GltfNode> ordered = [.. list.Select((node, at) => (Node: node, Order: Order(node, at))).OrderBy(n => n.Order).Select(n => n.Node)];
            if (ordered.SequenceEqual(list, ReferenceEqualityComparer.Instance)) return;
            list.Clear(); list.AddRange(ordered);
            json.Clear(); foreach (var node in ordered) json.Add(node.Index);
        }
    }

    /// <summary>
    /// Gives each export node that continues a project node the project's exact local transform (in <paramref name="root"/>,
    /// copied from <paramref name="project"/>, the project's JSON) where the export's differs only by the rounding of
    /// Blender's arithmetic. Blender holds world matrices in single precision and writes each node's transform relative to its
    /// parent again, which moves a node nobody moved by a few units in the last place of its parent's position (up to 3e-4
    /// at 2,700 units in 1999 m1's database) and gives nodes without a transform of their own one. Such a transform counts
    /// as the node's own: a script's rotation and scale of the node stop applying, a group seems moved, and a level-of-detail
    /// node cannot hold one.
    /// </summary>
    internal void RestoreTransforms(JsonObject root, JsonObject project)
    {
        var jsonNodes = root["nodes"] as JsonArray ?? []; var projectNodes = project["nodes"] as JsonArray ?? [];
        // How far each project node's parents stand from the file's origin: the rounding grows with the farthest.
        Dictionary<GltfNode, float> distance = new(ReferenceEqualityComparer.Instance);
        Stack<(GltfNode Node, Matrix4x4 Parent, float Far, int Depth)> pending = new(before.Roots.AsEnumerable().Reverse().Select(r => (r, Matrix4x4.Identity, 0f, 0)));
        while (pending.TryPop(out var entry))
        {
            work.Charge(1);
            if (entry.Depth > GltfDocument.MaximumDepth || !distance.TryAdd(entry.Node, entry.Far)) continue;
            var placed = (entry.Node.Matrix ?? Matrix4x4.Identity) * entry.Parent;
            float far = MathF.Max(entry.Far, placed.Translation.Length());
            foreach (var child in entry.Node.Children) pending.Push((child, placed, far, entry.Depth + 1));
        }
        foreach (var (node, old) in match)
        {
            work.Charge(1);
            Matrix4x4 exported = node.Matrix ?? Matrix4x4.Identity, previous = old.Matrix ?? Matrix4x4.Identity;
            if (exported == previous || !distance.TryGetValue(old, out float far) || !Rounded(previous, exported, far)
                || node.Index < 0 || node.Index >= jsonNodes.Count || jsonNodes[node.Index] is not JsonObject target
                || old.Index < 0 || old.Index >= projectNodes.Count || projectNodes[old.Index] is not JsonObject source) continue;
            foreach (string key in (string[])["matrix", "translation", "rotation", "scale"])
            {
                target.Remove(key);
                if (source[key] is { } value) target[key] = value.DeepClone();
            }
            node.Matrix = old.Matrix;
        }

        // Within 16 units in the last place of single precision: of the parent's distance for a position, of 1 otherwise.
        static bool Rounded(Matrix4x4 a, Matrix4x4 b, float far)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                {
                    float p = a[r, c], q = b[r, c], size = MathF.Max(1, MathF.Max(MathF.Abs(p), MathF.Abs(q)));
                    if (!(MathF.Abs(p - q) <= 2e-6f * (r == 3 ? MathF.Max(size, far) : size))) return false;
                }
            return true;
        }
    }

    /// <summary>
    /// Finds what each triangle of <paramref name="export"/> continues in the project's model, puts its polygons back in the
    /// model's polygon order and writes the recovered polygon boundaries into the export's JSON (<paramref name="root"/>). A
    /// recovered polygon whose triangles name different copies of a corner (Blender splits a vertex by face normal) is made
    /// to name one of them: <paramref name="buffer"/> gives a writable copy of a buffer (by index) for that and for the index
    /// data of polygons that go back between another primitive's; <paramref name="replace"/> replaces a buffer that grows.
    /// </summary>
    internal void Carry(GltfDocument export, JsonObject root, Func<int, byte[]> buffer, Action<int, byte[]> replace)
    {
        var exportNodes = export.AllNodes().Distinct().ToList();
        // Meshes: the project mesh the matched (or copied) nodes of an export mesh held (by name when they held several or none).
        var jsonNodes = root["nodes"] as JsonArray ?? [];
        Dictionary<GltfMesh, int> exportMeshIndex = new(ReferenceEqualityComparer.Instance);
        List<GltfMesh> exportMeshes = [];
        foreach (var node in exportNodes)
            if (node.Mesh is { } mesh && !exportMeshIndex.ContainsKey(mesh) && GltfInteger.TryInt64(jsonNodes[node.Index]?["mesh"], out long index))
            { exportMeshIndex.Add(mesh, (int)index); exportMeshes.Add(mesh); }
        Dictionary<GltfMesh, List<GltfMesh>> held = new(ReferenceEqualityComparer.Instance);
        foreach (var node in exportNodes)
            if (node.Mesh is { } mesh && (match.TryGetValue(node, out var old) || copies.TryGetValue(node, out old)) && old.Mesh is { } previous)
            {
                if (!held.TryGetValue(mesh, out var list)) held[mesh] = list = [];
                if (!list.Contains(previous)) list.Add(previous);
            }
        var named = oldLayout.Meshes.GroupBy(m => m.Name, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var mesh in exportMeshes)
        {
            string name = WorldGltf.StripBlenderSuffix(mesh.Name);
            var sources = held.GetValueOrDefault(mesh) ?? [];
            GltfMesh? source = sources.Count == 1 ? sources[0] : sources.FirstOrDefault(m => m.Name == name) ?? sources.FirstOrDefault() ?? named.GetValueOrDefault(name);
            if (source == null) continue;
            Align(oldMeshes[source], source, mesh, exportMeshIndex[mesh], root, buffer);
        }
        Append(root, buffer, replace);
    }

    /// <summary>
    /// Rewrites every map's assignments for <paramref name="updated"/> (the export as the update writes it, read back): what
    /// corresponds keeps each map's zones, the rest takes the model's own (no polygon zone; a node its parent's zone and the
    /// gate its flags give). <c>Lost</c> names, per map, the zoned faces and nodes the export replaced with new ones, whose
    /// zones therefore cannot follow; <c>Removed</c> the zoned faces it no longer has, without new ones in their place;
    /// <c>Added</c> counts new faces and nodes.
    /// </summary>
    internal (IReadOnlyList<(string Relative, byte[] Content)> Changes, IReadOnlyList<string> Lost, IReadOnlyList<string> Removed, int AddedFaces, int AddedNodes) Remap(GltfDocument updated, JsonObject root, CancellationToken token)
    {
        var layout = WorldGltf.ZoneLayout.Read(updated, token);
        var own = WorldGltf.CaptureZoneProfile(updated, token: token);
        Dictionary<GltfNode, int> ordinals = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < layout.Nodes.Count; i++) ordinals.Add(layout.Nodes[i], i);
        Dictionary<GltfMesh, int> meshOrdinals = new(ReferenceEqualityComparer.Instance), meshIndex = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < layout.Meshes.Count; i++) meshOrdinals.Add(layout.Meshes[i], i);
        var jsonNodes = root["nodes"] as JsonArray ?? [];
        foreach (var node in layout.Nodes)
            if (node.Mesh is { } mesh && GltfInteger.TryInt64(jsonNodes[node.Index]?["mesh"], out long index)) meshIndex.TryAdd(mesh, (int)index);
        int addedFaces = 0;
        foreach (var mesh in layout.Meshes)
            addedFaces += meshIndex.TryGetValue(mesh, out int index) && meshes.TryGetValue(index, out var source) ? source.Polygons.Count(q => q < 0) : own.MeshPolygons[meshOrdinals[mesh]].Count;
        IReadOnlyList<uint> Words(SourceMapZoneAsset asset, GltfMesh mesh)
        {
            var defaults = own.MeshPolygons[meshOrdinals[mesh]];
            if (!meshIndex.TryGetValue(mesh, out int index) || !meshes.TryGetValue(index, out var source)) return defaults;
            if (source.Polygons.Count != defaults.Count) throw new InvalidDataException($"{model}: the polygons recovered for mesh {index} were not read back as written.");
            var previous = asset.Profile.MeshPolygons[source.Mesh];
            uint[] words = new uint[defaults.Count];
            for (int i = 0; i < words.Length; i++) words[i] = source.Polygons[i] >= 0 ? previous[source.Polygons[i]] : defaults[i];
            return words;
        }
        // A new node marked as a placement whose model the maps name needs that name from a map: only a node of the project's
        // model, or a copy of one, has it.
        foreach (var node in layout.Nodes)
        {
            if (!newNodes.Contains(node.Index) || node.Extras?[WorldGltf.Key]?[WorldGltf.ZoneReference] == null) continue;
            string why = undecided.GetValueOrDefault(node.Index, "");
            throw new InvalidDataException($"{model}: node {ShownPath(node.Index)} (glTF node {node.Index}) is marked {WorldGltf.ZoneReference}, a placement whose model each map names, "
                + $"but it is no node of the model as the project holds it, nor a copy of one{why}. Duplicate the placement together with its parent (or keep the copy beside a node of its name), "
                + "give it ref (the referenced model's path) instead, or remove the mark.");
        }
        var changes = bindings.Remap(before, updated, origins, token, node => own.Nodes[ordinals[node]], Words);

        // Replaced, not just removed: a project mesh that lost zoned faces while its export has new ones, and a lost node
        // with a zone of its own where a new node now stands under the same parent.
        Dictionary<string, List<string>> lost = new(StringComparer.Ordinal);
        Dictionary<int, string> holders = [];
        foreach (var node in before.AllNodes()) if (node.Mesh is { } mesh && oldMeshes.TryGetValue(mesh, out int ordinal)) holders.TryAdd(ordinal, WorldGltf.EngineName(node));
        // New nodes by the project node their parent is (-1: a root of the file); those under a new parent stand nowhere known.
        Dictionary<int, HashSet<WorldNodeZone>> arrivals = [];
        foreach (var node in layout.Nodes)
        {
            if (!newNodes.Contains(node.Index)) continue;
            int parent = exportParents.TryGetValue(node.Index, out var p) ? origins[p.Index] : -1;
            if (parent < 0 && exportParents.ContainsKey(node.Index)) continue;
            if (!arrivals.TryGetValue(parent, out var list)) arrivals[parent] = list = [];
            list.Add(own.Nodes[ordinals[node]]);
        }
        // Removed: the zoned faces of a project mesh the export keeps without new faces in their place.
        Dictionary<string, List<string>> removed = new(StringComparer.Ordinal);
        var held = Held().ToList();
        foreach (var (map, asset) in bindings.Assets)
        {
            token.ThrowIfCancellationRequested();
            int faces = 0, deleted = 0; SortedSet<string> where = new(StringComparer.Ordinal), gone = new(StringComparer.Ordinal); List<string> nodes = [];
            foreach (var (mesh, kept, _, added) in held)
            {
                var words = asset.Profile.MeshPolygons[mesh];
                int zoned = 0;
                for (int q = 0; q < words.Count; q++) if (!kept.Contains(q) && words[q] != WorldGltf.DefaultPolygonZone) zoned++;
                if (zoned == 0) continue;
                if (added > 0) faces += zoned; else deleted += zoned;
                if (holders.TryGetValue(mesh, out var holder)) (added > 0 ? where : gone).Add(holder);
            }
            foreach (var node in lostNodes)
            {
                var assigned = asset.Profile.Nodes[oldOrdinals[node]];
                if (arrivals.TryGetValue(oldParents.GetValueOrDefault(node)?.Index ?? -1, out var standing) && standing.Any(zone => !SameEffect(assigned, zone)))
                    nodes.Add(WorldGltf.EngineName(node));
            }
            string mapName = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(map))) ?? map;
            static string Faces(int count, SortedSet<string> holders) => $"{count:N0} zoned face{(count == 1 ? "" : "s")} of {string.Join(", ", holders.Take(4).Select(h => JsonData.ShownText(h)))}{(holders.Count > 4 ? " and more" : "")}";
            if (deleted > 0)
            {
                if (!removed.TryGetValue(mapName, out var list)) removed[mapName] = list = [];
                list.Add($"{Path.GetFileName(asset.LogicalPath)}: {Faces(deleted, gone)}");
            }
            if (faces == 0 && nodes.Count == 0) continue;
            List<string> parts = [];
            if (faces > 0) parts.Add(Faces(faces, where));
            if (nodes.Count > 0) parts.Add($"node{(nodes.Count == 1 ? "" : "s")} {string.Join(", ", nodes.Take(4).Select(n => JsonData.ShownText(n)))}{(nodes.Count > 4 ? $" and {nodes.Count - 4} more" : "")}");
            if (!lost.TryGetValue(mapName, out var replaced)) lost[mapName] = replaced = [];
            replaced.Add($"{Path.GetFileName(asset.LogicalPath)}: {string.Join("; ", parts)}");
        }
        static IReadOnlyList<string> Shown(Dictionary<string, List<string>> maps) => [.. maps.Take(8).Select(m => $"{m.Key} ({string.Join("; ", m.Value.Take(4))})")];
        return (changes, Shown(lost), Shown(removed), addedFaces, newNodes.Count);
    }

    /// <summary>
    /// Each project mesh the export continues, or that a node the export continues held: the polygons the export keeps of it
    /// (whole, or in pieces that keep their zones), those it keeps every triangle of, and how many new faces its export
    /// meshes have (none, and nothing kept, when the export holds no geometry for it). A mesh only removed nodes held is left
    /// out: those nodes are reported.
    /// </summary>
    private IEnumerable<(int Mesh, HashSet<int> Kept, HashSet<int> Whole, int Added)> Held()
    {
        HashSet<GltfNode> matched = new(match.Values, ReferenceEqualityComparer.Instance);
        HashSet<int> seen = [];
        foreach (var (mesh, (kept, whole, added)) in continued) { seen.Add(mesh); yield return (mesh, kept, whole, added); }
        foreach (var node in oldLayout.Nodes)
            if (node.Mesh is { } mesh && matched.Contains(node) && oldMeshes.TryGetValue(mesh, out int ordinal) && seen.Add(ordinal)) yield return (ordinal, [], [], 0);
    }

    /// <summary>
    /// The faces of the project's model the export no longer has (<see cref="Held"/>), by the node that held them: Blender
    /// deleted them, or rebuilt them as new faces. A face the export keeps only some triangles of (one triangle of a quad
    /// deleted) counts too, and among <c>Partial</c>: its other triangles stay, with its zones. Faces of nodes the export
    /// removed are not counted (the nodes are reported).
    /// </summary>
    internal (int Total, int Partial, IReadOnlyList<(string Holder, int Faces)> Meshes) RemovedFaces()
    {
        Dictionary<int, string> holders = [];
        foreach (var node in before.AllNodes()) if (node.Mesh is { } mesh && oldMeshes.TryGetValue(mesh, out int ordinal)) holders.TryAdd(ordinal, WorldGltf.EngineName(node));
        List<(string, int)> result = []; int total = 0, partial = 0;
        foreach (var (mesh, kept, whole, _) in Held())
        {
            work.Charge(1 + oldLayout.Meshes[mesh].Primitives.Count + kept.Count);
            int part = kept.Count(q => !whole.Contains(q));
            int removed = oldLayout.Meshes[mesh].Primitives.Sum(p => oldLayout.Polygons[p].Count) - kept.Count + part;
            if (removed <= 0) continue;
            total += removed; partial += part; result.Add((holders.GetValueOrDefault(mesh, ""), removed));
        }
        return (total, partial, result);
    }

    /// <summary>Whether two node assignments give the node the same zone word and gate wherever it is loaded.</summary>
    private static bool SameEffect(WorldNodeZone a, WorldNodeZone b) =>
        a.Gate == b.Gate && a.Inherit == b.Inherit && (a.Inherit ? (a.Word & ~255u) == (b.Word & ~255u) : a.Word == b.Word);

    // ------------------------------------------------------------------------------------------------ nodes

    private Dictionary<GltfNode, GltfNode> MatchNodes(GltfDocument export, PolygonWorkBudget work)
    {
        Dictionary<GltfNode, GltfNode> match = new(ReferenceEqualityComparer.Instance);
        HashSet<GltfNode> taken = new(ReferenceEqualityComparer.Instance);
        var oldList = before.AllNodes().Distinct().ToList(); var newList = export.AllNodes().Distinct().ToList();
        for (int i = 0; i < oldList.Count; i++) { oldOrdinals[oldList[i]] = i; foreach (var child in oldList[i].Children) oldParents.TryAdd(child, oldList[i]); }
        foreach (var root in before.Roots) oldParents.TryAdd(root, null);
        foreach (var node in newList) { exportNames[node.Index] = WorldGltf.EngineName(node); exportNodes[node.Index] = node; foreach (var child in node.Children) exportParents.TryAdd(child.Index, node); }
        // A name used once on each side is the same node wherever it went.
        var oldNames = oldList.GroupBy(WorldGltf.EngineName).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
        var newNames = newList.GroupBy(n => exportNames[n.Index]).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        foreach (var node in newList)
            if (newNames.Contains(exportNames[node.Index]) && oldNames.TryGetValue(exportNames[node.Index], out var old)) { match[node] = old; taken.Add(old); }
        // Repeated names under corresponding parents (parents first): the node at the same place, then in order (Blender
        // numbers repeated names in the order it read them); then a renamed node holding the same geometry at the same place.
        Pair(null, null);
        foreach (var node in newList) if (match.TryGetValue(node, out var old)) Pair(node, old);
        return match;

        void Pair(GltfNode? exported, GltfNode? previous)
        {
            var next = (exported == null ? export.Roots : exported.Children).Where(n => !match.ContainsKey(n)).OrderBy(n => Suffix().Match(n.Name) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0).ToList();
            var prior = (previous == null ? before.Roots : previous.Children).Where(n => !taken.Contains(n)).ToList();
            work.Charge(1L + next.Count + prior.Count);
            var byName = prior.GroupBy(WorldGltf.EngineName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var group in next.GroupBy(n => exportNames[n.Index], StringComparer.Ordinal))
            {
                var candidates = byName.GetValueOrDefault(group.Key) ?? [];
                List<GltfNode> rest = [];
                foreach (var node in group)
                {
                    work.Charge(candidates.Count);
                    if (candidates.FirstOrDefault(o => Close(o.Matrix, node.Matrix)) is { } same) { Take(node, same); candidates.Remove(same); }
                    else rest.Add(node);
                }
                for (int i = 0; i < rest.Count && i < candidates.Count; i++) Take(rest[i], candidates[i]);
            }
            var renamed = next.Where(n => !match.ContainsKey(n)).ToList(); var gone = prior.Where(o => !taken.Contains(o)).ToList();
            work.Charge((long)renamed.Count * gone.Count);
            foreach (var node in renamed)
            {
                work.Charge(renamed.Count);
                var same = gone.Where(o => Alike(o, node)).Take(2).ToList();
                if (same.Count == 1 && renamed.Count(n => Alike(same[0], n)) == 1) { Take(node, same[0]); gone.Remove(same[0]); }
            }
        }
        void Take(GltfNode node, GltfNode old) { match[node] = old; taken.Add(old); }
        bool Alike(GltfNode old, GltfNode node) =>
            Close(old.Matrix, node.Matrix) && (old.Mesh == null) == (node.Mesh == null) && (old.Mesh == null || Triangles(old.Mesh, node.Mesh!));
    }
    private readonly Dictionary<GltfMesh, long> imported = new(ReferenceEqualityComparer.Instance);

    /// <summary>As many triangles as the project's mesh, or as Blender's import keeps of it (one of those that share their vertices).</summary>
    private bool Triangles(GltfMesh old, GltfMesh exported)
    {
        long count = exported.Primitives.Sum(p => (long)p.Indices.Count);
        if (old.Primitives.Sum(p => (long)p.Indices.Count) == count) return true;
        if (!imported.TryGetValue(old, out long kept))
        {
            kept = 0;
            foreach (var primitive in old.Primitives)
            {
                work.Charge(1L + primitive.Indices.Count / 3);
                HashSet<(int, int, int)> sets = [];
                for (int t = 0; t * 3 + 2 < primitive.Indices.Count; t++) if (Sorted(primitive, t) is { A: >= 0 } set && sets.Add(set)) kept += 3;
            }
            imported[old] = kept;
        }
        return kept == count;
    }

    /// <summary>
    /// Finds what export nodes no project node continues (<see cref="MatchNodes"/>) copy. Blender's duplicate keeps a node's
    /// custom properties, children and geometry and names each copy anew (<c>.001</c>), so a copy is a node whose engine name,
    /// engine marks (a map's or a part's reference, class, shared instance, model) and geometry (as many triangles, on the same
    /// positions) are a project node's, and its children alike in turn (by name) copy that node's. Of several such project nodes: the one at
    /// the same place in the world (a copy not moved yet, or a node only moved to another parent); else those beside it
    /// (under the node its parent continues: a duplicate stays under its original's parent); else those the export no longer
    /// has where they were (it is that node, moved); else all. Among those, the one at its own transform, or the first when
    /// every map binds them alike; otherwise it is new. A copy takes the zones and map references of the node it copies in
    /// every map, as a copy made in the world editor does; a moved node continues the node it is.
    /// </summary>
    private void FindCopies(GltfDocument export)
    {
        Dictionary<string, List<GltfNode>> named = new(StringComparer.Ordinal);
        foreach (var node in oldLayout.Nodes)
        {
            string name = WorldGltf.EngineName(node);
            if (!named.TryGetValue(name, out var list)) named[name] = list = [];
            list.Add(node);
        }
        HashSet<GltfNode> taken = new(match.Values, ReferenceEqualityComparer.Instance);
        Dictionary<GltfNode, Matrix4x4> oldWorld = new(ReferenceEqualityComparer.Instance), newWorld = new(ReferenceEqualityComparer.Instance);
        foreach (var node in export.AllNodes().Distinct().ToList())
        {
            if (match.ContainsKey(node) || copies.ContainsKey(node) || !named.TryGetValue(exportNames[node.Index], out var sameName)) continue;
            work.Charge(sameName.Count);
            List<(GltfNode Original, List<(GltfNode Copy, GltfNode Original)> Pairs)> alike = [];
            foreach (var old in sameName) if (Copied(node, old) is { } pairs) alike.Add((old, pairs));
            if (alike.Count == 0) continue;
            var placed = World(node, newWorld, n => exportParents.GetValueOrDefault(n.Index));
            var tier = alike.Where(a => Close(World(a.Original, oldWorld, n => oldParents.GetValueOrDefault(n)), placed)).ToList();
            if (tier.Count != 1)
            {
                GltfNode? parent = exportParents.GetValueOrDefault(node.Index);
                GltfNode? beside = parent == null ? null : match.GetValueOrDefault(parent) ?? copies.GetValueOrDefault(parent);
                tier = alike.Where(a => parent == null ? oldParents.GetValueOrDefault(a.Original) == null : beside != null && ReferenceEquals(oldParents.GetValueOrDefault(a.Original), beside)).ToList();
                if (tier.Count == 0) tier = alike.Where(a => !taken.Contains(a.Original)).ToList();
                if (tier.Count == 0) tier = alike;
            }
            var chosen = tier.Count == 1 ? tier[0] : tier.Where(a => Close(a.Original.Matrix, node.Matrix)).Take(2).Count() == 1 ? tier.First(a => Close(a.Original.Matrix, node.Matrix))
                : tier.Skip(1).All(a => Equivalent(tier[0].Pairs, a.Pairs)) ? tier[0] : default;
            if (chosen.Original == null)
            {
                undecided[node.Index] = $"; it is like {tier.Count:N0} nodes named {JsonData.ShownText(exportNames[node.Index])} ({string.Join(", ", tier.Take(3).Select(a => ShownPath(a.Original)))}{(tier.Count > 3 ? ", …" : "")}), which the maps give different models or zones, so which one it copies cannot be told";
                continue;
            }
            if (!taken.Contains(chosen.Original))
            {
                // The node moved: it continues the node it is, with its subtree.
                foreach (var (copy, original) in chosen.Pairs) if (!match.ContainsKey(copy) && taken.Add(original)) match[copy] = original;
                continue;
            }
            foreach (var (copy, original) in chosen.Pairs) if (!match.ContainsKey(copy)) copies.TryAdd(copy, original);
            copiedTops.Add((node, chosen.Original));
        }

        // A node's transform in the file's space, each node's once.
        Matrix4x4 World(GltfNode node, Dictionary<GltfNode, Matrix4x4> known, Func<GltfNode, GltfNode?> parentOf)
        {
            if (known.TryGetValue(node, out var placed)) return placed;
            List<GltfNode> chain = [node];
            for (var at = parentOf(node); at != null && !known.ContainsKey(at) && chain.Count <= GltfDocument.MaximumDepth; at = parentOf(at)) chain.Add(at);
            work.Charge(chain.Count);
            var top = parentOf(chain[^1]);
            var transform = top != null && known.TryGetValue(top, out var above) ? above : Matrix4x4.Identity;
            for (int i = chain.Count - 1; i >= 0; i--) known[chain[i]] = transform = (chain[i].Matrix ?? Matrix4x4.Identity) * transform;
            return transform;
        }
    }

    /// <summary>
    /// Whether <paramref name="node"/> (of the export) is a copy of <paramref name="original"/> (of the project's model), of
    /// its engine name and kind (<see cref="SameKind"/>): the pairs of the copy's subtree and the original's, its root first;
    /// null when it is not one. Children pair by name (each name's in order, the copy's as Blender numbered them) where they
    /// are of the same kind; a child added to the copy, or one unlike its counterpart, is left to <see cref="FindCopies"/>.
    /// </summary>
    private List<(GltfNode Copy, GltfNode Original)>? Copied(GltfNode node, GltfNode original)
    {
        if (exportNames[node.Index] != WorldGltf.EngineName(original) || !SameKind(original, node)) return null;
        List<(GltfNode, GltfNode)> pairs = [];
        Stack<(GltfNode Copy, GltfNode Original, int Depth)> pending = new([(node, original, 0)]);
        while (pending.TryPop(out var pair))
        {
            work.Charge(1L + pair.Copy.Children.Count + pair.Original.Children.Count);
            pairs.Add((pair.Copy, pair.Original));
            if (pair.Depth >= GltfDocument.MaximumDepth) continue;
            Dictionary<string, Queue<GltfNode>> children = new(StringComparer.Ordinal);
            foreach (var child in pair.Original.Children)
            {
                string name = WorldGltf.EngineName(child);
                if (!children.TryGetValue(name, out var queue)) children[name] = queue = new();
                queue.Enqueue(child);
            }
            foreach (var child in pair.Copy.Children.OrderBy(n => Suffix().Match(n.Name) is { Success: true } m ? long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0))
                if (children.TryGetValue(exportNames[child.Index], out var queue) && queue.TryDequeue(out var previous) && SameKind(previous, child))
                    pending.Push((child, previous, pair.Depth + 1));
        }
        return pairs;
    }

    /// <summary>The engine marks that make a node what it is, and its geometry: the same in a copy Blender made of it.</summary>
    private bool SameKind(GltfNode old, GltfNode node)
    {
        var a = old.Extras?[WorldGltf.Key] as JsonObject; var b = node.Extras?[WorldGltf.Key] as JsonObject;
        foreach (string key in (string[])[WorldGltf.ZoneReference, "ref", "class", "instance", "model"])
            if (!JsonNode.DeepEquals(a?[key], b?[key])) return false;
        if ((old.Mesh == null) != (node.Mesh == null)) return false;
        return old.Mesh == null || Triangles(old.Mesh, node.Mesh!) && Positions(old.Mesh).SetEquals(Positions(node.Mesh!));
    }
    private readonly Dictionary<GltfMesh, HashSet<Vector3>> positions = new(ReferenceEqualityComparer.Instance);
    /// <summary>The positions a mesh's triangles use (a negative zero as zero), each mesh once.</summary>
    private HashSet<Vector3> Positions(GltfMesh mesh)
    {
        if (positions.TryGetValue(mesh, out var known)) return known;
        HashSet<Vector3> result = [];
        foreach (var primitive in mesh.Primitives)
        {
            work.Charge(1L + primitive.Indices.Count);
            foreach (int index in primitive.Indices) { var v = primitive.Positions[index]; result.Add(new(v.X + 0f, v.Y + 0f, v.Z + 0f)); }
        }
        return positions[mesh] = result;
    }

    /// <summary>
    /// Whether every map gives the nodes and meshes of two project subtrees, paired through one copy's (the same nodes of it
    /// paired in both), the same zones and references.
    /// </summary>
    private bool Equivalent(List<(GltfNode Copy, GltfNode Original)> a, List<(GltfNode Copy, GltfNode Original)> b)
    {
        Dictionary<GltfNode, GltfNode> other = new(ReferenceEqualityComparer.Instance);
        foreach (var (copy, original) in b) other[copy] = original;
        if (a.Count != b.Count || a.Any(p => !other.ContainsKey(p.Copy))) return false;
        foreach (var (_, asset) in bindings.Assets)
        {
            work.Charge(1L + a.Count + asset.References.Count);
            var marks = References(asset);
            foreach (var (copy, x) in a)
            {
                var y = other[copy];
                int i = oldOrdinals[x], j = oldOrdinals[y];
                if (!SameEffect(asset.Profile.Nodes[i], asset.Profile.Nodes[j])) return false;
                var (r, s) = (marks.GetValueOrDefault(i), marks.GetValueOrDefault(j));
                if (r?.Asset != s?.Asset || r?.Spelling != s?.Spelling) return false;
                if (x.Mesh is { } m && y.Mesh is { } n && !ReferenceEquals(m, n))
                {
                    var (p, q) = (asset.Profile.MeshPolygons[oldMeshes[m]], asset.Profile.MeshPolygons[oldMeshes[n]]);
                    work.Charge(1L + p.Count);
                    if (!p.SequenceEqual(q)) return false;
                }
            }
        }
        return true;
    }
    private readonly Dictionary<SourceMapZoneAsset, Dictionary<int, SourceMapZoneReference>> references = new(ReferenceEqualityComparer.Instance);
    private Dictionary<int, SourceMapZoneReference> References(SourceMapZoneAsset asset)
    {
        if (!references.TryGetValue(asset, out var known))
        {
            references[asset] = known = [];
            foreach (var reference in asset.References) known.TryAdd(reference.Node, reference);
        }
        return known;
    }

    /// <summary>
    /// The nodes copied in Blender (the top of each copied subtree, by its path in the export) and the project node each
    /// copies (by its path in the model), in the export's order: they keep that node's zones and map references.
    /// </summary>
    internal IReadOnlyList<(string Copy, string Original)> Copies => [.. copiedTops.Select(c => (ShownPath(c.Copy.Index), ShownPath(c.Original)))];

    /// <summary>
    /// The nodes the export continues under another engine name that no recorded name gave them (renamed in Blender, where
    /// <see cref="WorldGltf.FollowShownNames"/> follows recorded ones): old and new name, in the export's order.
    /// </summary>
    internal IReadOnlyList<(string Old, string New)> Renamed()
    {
        List<(string, string)> renamed = [];
        foreach (var (node, old) in match.OrderBy(m => m.Key.Index))
        {
            if ((node.Extras?[WorldGltf.Key] as JsonObject)?["name"] != null) continue;
            string was = WorldGltf.EngineName(old), now = exportNames[node.Index];
            if (!string.Equals(was, now, StringComparison.Ordinal)) renamed.Add((was, now));
        }
        return renamed;
    }

    /// <summary>A node of the export by its path of Blender's names (unique in Blender), shown briefly: its last eight steps.</summary>
    private string ShownPath(int index)
    {
        List<string> steps = [];
        for (var at = exportNodes.GetValueOrDefault(index); at != null && steps.Count <= 8; at = exportParents.GetValueOrDefault(at.Index))
            steps.Add(at.Name.Length > 0 ? JsonData.ShownText(at.Name, 40) : $"#{at.Index}");
        if (steps.Count > 8) steps[8] = "…";
        steps.Reverse();
        return JsonData.ShownText(string.Join("/", steps), 400);
    }
    /// <summary>A node of the project's model by its path of engine names, shown briefly: its last eight steps.</summary>
    private string ShownPath(GltfNode node)
    {
        List<string> steps = [];
        for (GltfNode? at = node; at != null && steps.Count <= 8; at = oldParents.GetValueOrDefault(at))
            steps.Add(JsonData.ShownText(WorldGltf.EngineName(at), 40) is { Length: > 0 } name ? name : "(unnamed)");
        if (steps.Count > 8) steps[8] = "…";
        steps.Reverse();
        return JsonData.ShownText(string.Join("/", steps), 400);
    }

    /// <summary>Two local transforms equal within the precision a round trip through another program keeps.</summary>
    private static bool Close(Matrix4x4? a, Matrix4x4? b)
    {
        var x = a ?? Matrix4x4.Identity; var y = b ?? Matrix4x4.Identity;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                float p = x[r, c], q = y[r, c];
                if (MathF.Abs(p - q) > 1e-4f * MathF.Max(1, MathF.Max(MathF.Abs(p), MathF.Abs(q)))) return false;
            }
        return true;
    }

    // ------------------------------------------------------------------------------------------------ triangles

    private void Align(int oldMesh, GltfMesh source, GltfMesh mesh, int meshIndex, JsonObject root, Func<int, byte[]> buffer)
    {
        // The project's triangles, with their polygons (counted through the mesh), grouped by material in first-appearance order.
        // A triangle's place in the model is its primitive's first place plus its index there.
        List<Triangle> old = []; int polygon = 0;
        int[] firstOf = new int[source.Primitives.Count];
        for (int p = 0; p < source.Primitives.Count; p++)
        {
            var primitive = source.Primitives[p]; int t = 0; firstOf[p] = old.Count;
            foreach (int count in oldLayout.Triangles[primitive])
            {
                for (int k = 0; k < count; k++, t++) old.Add(new(p, t, polygon, KeyOf(primitive, t)));
                polygon++;
            }
        }
        work.Charge(old.Count);
        List<List<Triangle>> next = [];
        Dictionary<Key, int> exported = [];
        for (int j = 0; j < mesh.Primitives.Count; j++)
        {
            var primitive = mesh.Primitives[j]; List<Triangle> list = [];
            for (int t = 0; t * 3 + 2 < primitive.Indices.Count; t++) { list.Add(new(j, t, -1, KeyOf(primitive, t))); exported[list[^1].Key] = exported.GetValueOrDefault(list[^1].Key) + 1; }
            work.Charge(list.Count); next.Add(list);
        }
        // Blender's import keeps one of a primitive's triangles that use the same three vertices (a back-to-back pair of
        // triangles sharing their vertices; its mesh validation removes the others). The one it kept is the one whose corners
        // the export has (the first when the export has none of them, moved or deleted); the others take no part in the
        // correspondence and go back after it, on the vertices the export gives the one kept (Restore, below).
        Dictionary<(int, int), List<Triangle>> dropped = [];
        foreach (var set in old.GroupBy(t => (t.Primitive, Vertices: Sorted(source.Primitives[t.Primitive], t.Index))).Where(g => g.Count() > 1 && g.Key.Vertices.A >= 0))
        {
            List<Triangle> members = [.. set], kept = [];
            Dictionary<Key, int> left = [];
            foreach (var t in members)
                if ((left.TryGetValue(t.Key, out int count) ? count : left[t.Key] = exported.GetValueOrDefault(t.Key)) > 0) { left[t.Key]--; kept.Add(t); }
            if (kept.Count == members.Count) continue;
            if (kept.Count == 0) kept.Add(members[0]);
            foreach (var t in members) if (!kept.Contains(t)) dropped[(t.Primitive, t.Index)] = kept;
        }
        var groups = old.Where(t => !dropped.ContainsKey((t.Primitive, t.Index))).GroupBy(t => (object?)source.Primitives[t.Primitive].Material ?? source).Select(g => g.ToList()).ToList();
        Dictionary<Key, HashSet<int>> keyGroups = [];
        for (int g = 0; g < groups.Count; g++) foreach (var t in groups[g]) { if (!keyGroups.TryGetValue(t.Key, out var set)) keyGroups[t.Key] = set = []; set.Add(g); }

        Dictionary<(int Primitive, int Index), Triangle> paired = [];   // export triangle -> project triangle
        HashSet<(int, int)> used = [];                                   // project triangles paired
        Dictionary<(int, int), (int, int)> vertices = [];               // export vertex -> project vertex
        void Bind(Triangle n, Triangle o) { paired[(n.Primitive, n.Index)] = o; used.Add((o.Primitive, o.Index)); }
        void Corners(Triangle n, Triangle o)
        {
            for (int c = 0; c < 3; c++)
                vertices.TryAdd((n.Primitive, mesh.Primitives[n.Primitive].Indices[n.Index * 3 + c]), (o.Primitive, source.Primitives[o.Primitive].Indices[o.Index * 3 + c]));
        }
        List<List<(Triangle New, Triangle Old)>> gaps = [];
        HashSet<int> claimed = [];
        for (int j = 0; j < next.Count; j++)
        {
            // The material group most of its unchanged triangles come from; without any, the one of its name, or of its rank.
            Dictionary<int, int> votes = [];
            foreach (var t in next[j]) if (keyGroups.TryGetValue(t.Key, out var set)) foreach (int g in set) votes[g] = votes.GetValueOrDefault(g) + 1;
            int group = votes.Count > 0 ? votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key).First().Key : -1;
            if (group < 0)
            {
                string name = WorldGltf.StripBlenderSuffix(mesh.Primitives[j].Material?.Name ?? "");
                for (int g = 0; g < groups.Count && group < 0; g++)
                    if (!claimed.Contains(g) && source.Primitives[groups[g][0].Primitive].Material?.Name == name) group = g;
                if (group < 0 && next.Count == groups.Count && !claimed.Contains(j)) group = j;
            }
            if (group < 0) continue;
            claimed.Add(group);
            var previous = groups[group].Where(t => !used.Contains((t.Primitive, t.Index))).ToList();
            // Each primitive numbers its changed runs from 0: a run is this primitive's own (another primitive whose triangles
            // come from the same material, such as faces given a new one, can hold a run over the same project triangles).
            int numbered = gaps.Count;
            foreach (var (n, o, exact, gap) in Sequence(next[j], previous, work))
            {
                if (exact) { Bind(n, o); Corners(n, o); }
                else { while (gaps.Count <= numbered + gap) gaps.Add([]); gaps[numbered + gap].Add((n, o)); }
            }
        }
        // A changed run counts as the same faces only if every vertex it uses stays one project vertex, and none of its project
        // triangles is taken (unchanged in a later primitive, or by an earlier run): each pairs with one export triangle at most.
        foreach (var gap in gaps)
        {
            if (gap.Count == 0) continue;
            Dictionary<(int, int), (int, int)> tentative = [];
            bool consistent = true;
            foreach (var (n, o) in gap)
                for (int c = 0; c < 3 && consistent; c++)
                {
                    var key = (n.Primitive, mesh.Primitives[n.Primitive].Indices[n.Index * 3 + c]);
                    var value = (o.Primitive, source.Primitives[o.Primitive].Indices[o.Index * 3 + c]);
                    if ((vertices.TryGetValue(key, out var known) || tentative.TryGetValue(key, out known)) && known != value) consistent = false;
                    else tentative[key] = value;
                }
            if (!consistent || gap.Any(p => used.Contains((p.Old.Primitive, p.Old.Index)))) continue;
            foreach (var (n, o) in gap) Bind(n, o);
            foreach (var (key, value) in tentative) vertices.TryAdd(key, value);
        }
        // Faces given another material: unchanged triangles left on both sides whose position is unique there.
        var leftOld = old.Where(t => !used.Contains((t.Primitive, t.Index)) && !dropped.ContainsKey((t.Primitive, t.Index))).GroupBy(t => t.Key).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
        foreach (var group in next.SelectMany(l => l).Where(t => !paired.ContainsKey((t.Primitive, t.Index))).GroupBy(t => t.Key).Where(g => g.Count() == 1))
            if (leftOld.TryGetValue(group.Key, out var o)) { Bind(group.First(), o); Corners(group.First(), o); }

        var jsonPrimitives = root["meshes"]?[meshIndex]?["primitives"] as JsonArray;
        var originals = new JsonObject[mesh.Primitives.Count];
        for (int j = 0; j < originals.Length; j++)
            originals[j] = jsonPrimitives != null && jsonPrimitives.Count == originals.Length && jsonPrimitives[j] is JsonObject json ? json
                : throw new InvalidDataException($"{model}: the export's mesh {meshIndex} has no primitive {j}.");
        HashSet<int> grown = Restore(source, mesh, old, firstOf, dropped, paired, used, j => IndexTarget(root, originals[j]) != null);

        // A polygon all of whose triangles correspond is carried over: whole, consecutively in one primitive, it keeps its
        // boundary; otherwise (faces given another material) each piece the import joins from its triangles alone keeps its zones.
        Dictionary<int, List<(int First, int Count, int Polygon, int[] Corners)>> recovered = [];
        Dictionary<int, Dictionary<int, int>> rewrites = [];
        Dictionary<int, bool> patchable = [];
        Dictionary<int, int> found = [], sizes = [];
        foreach (var o in paired.Values) found[o.Polygon] = found.GetValueOrDefault(o.Polygon) + 1;
        foreach (var o in old) sizes[o.Polygon] = sizes.GetValueOrDefault(o.Polygon) + 1;
        HashSet<int> carried = [.. sizes.Where(s => found.GetValueOrDefault(s.Key) == s.Value).Select(s => s.Key)];
        var reverse = paired.ToDictionary(p => (p.Value.Primitive, p.Value.Index), p => p.Key);
        polygon = 0;
        for (int p = 0; p < source.Primitives.Count; p++)
        {
            var primitive = source.Primitives[p]; int t = 0; var corners = oldLayout.Polygons[primitive];
            for (int q = 0; q < corners.Count; q++, polygon++)
            {
                int count = oldLayout.Triangles[primitive][q], first = t; t += count;
                work.Charge(count);
                if (!reverse.TryGetValue((p, first), out var start)) continue;
                var target = mesh.Primitives[start.Primitive];
                bool whole = true;
                for (int k = 1; k < count && whole; k++) whole = reverse.TryGetValue((p, first + k), out var at) && at == (start.Primitive, start.Index + k);
                if (!whole) continue;
                // One export vertex per project corner: the first the triangles name; the others must be copies of it.
                Dictionary<int, int> chosen = []; Dictionary<int, int> changes = [];
                for (int k = 0; k < count && whole; k++)
                    for (int c = 0; c < 3 && whole; c++)
                    {
                        int from = primitive.Indices[(first + k) * 3 + c], at = (start.Index + k) * 3 + c, to = target.Indices[at];
                        if (!chosen.TryAdd(from, to) && chosen[from] != to)
                        {
                            if (Same(target, chosen[from], to)) changes[at] = chosen[from];
                            else whole = false;
                        }
                    }
                int[] mapped = [.. corners[q].Where(chosen.ContainsKey).Select(c => chosen[c])];
                if (changes.Count > 0 && !patchable.ContainsKey(start.Primitive)) patchable[start.Primitive] = IndexTarget(root, originals[start.Primitive]) != null;
                if (!whole || mapped.Length < 3 || changes.Count > 0 && !patchable[start.Primitive]) continue;
                if (!recovered.TryGetValue(start.Primitive, out var list)) recovered[start.Primitive] = list = [];
                list.Add((start.Index, count, polygon, mapped));
                if (!rewrites.TryGetValue(start.Primitive, out var patch)) rewrites[start.Primitive] = patch = [];
                foreach (var (at, to) in changes) patch[at] = to;
            }
        }
        // The corners rewritten in memory first: the polygons below are read from them, and so is the index data written.
        foreach (var (j, patch) in rewrites) foreach (var (at, to) in patch) mesh.Primitives[j].Indices[at] = to;

        // Every primitive's polygons: the recovered ones, and the rest joined into fans as an import would join them.
        List<List<Piece>> pieces = []; int added = 0;
        for (int j = 0; j < mesh.Primitives.Count; j++)
        {
            var primitive = mesh.Primitives[j];
            var list = (recovered.GetValueOrDefault(j) ?? []).OrderBy(r => r.First).ToList();
            List<Piece> written = []; int triangles = primitive.Indices.Count / 3, at = 0, upcoming = 0;
            GltfPrimitive? fans = null;
            while (at < triangles)
            {
                if (upcoming < list.Count && list[upcoming].First == at)
                {
                    var r = list[upcoming++];
                    written.Add(new(at, r.Count, new JsonObject { ["triangles"] = r.Count, ["corners"] = new JsonArray([.. r.Corners.Select(c => (JsonNode)c)]) }, r.Polygon, Place(j, at, r.Count)));
                    at += r.Count; continue;
                }
                int end = upcoming < list.Count ? list[upcoming].First : triangles;
                if (fans == null)
                {
                    fans = new() { Material = primitive.Material };
                    fans.Positions.AddRange(primitive.Positions); fans.TexCoords.AddRange(primitive.TexCoords);
                }
                fans.Indices.Clear(); fans.Indices.AddRange(primitive.Indices.Skip(at * 3).Take((end - at) * 3));
                foreach (var polygonCorners in WorldGltf.MergeFans(fans, WorldGltf.ZoneLayout.Textured(primitive), work))
                {
                    int count = polygonCorners.Length - 2, from = -1;
                    // A piece of one project polygon (all its triangles from it) keeps that polygon's zones.
                    for (int k = 0; k < count; k++)
                    {
                        int origin = paired.TryGetValue((j, at + k), out var o) ? o.Polygon : -1;
                        from = k == 0 ? origin : from == origin ? from : -1;
                    }
                    written.Add(new(at, count, new JsonObject { ["triangles"] = count, ["corners"] = new JsonArray([.. polygonCorners.Select(c => (JsonNode)c)]) }, from, Place(j, at, count)));
                    at += count;
                    if (from < 0) added++;
                }
                at = end;
            }
            pieces.Add(written);
        }
        // The first project triangle a piece holds, by its place in the project's model; -1 when it holds none.
        long Place(int j, int first, int count)
        {
            long place = -1;
            for (int k = 0; k < count; k++)
                if (paired.TryGetValue((j, first + k), out var o) && (place < 0 || firstOf[o.Primitive] + o.Index < place)) place = firstOf[o.Primitive] + o.Index;
            return place;
        }

        // In the model's polygon order: Blender lists a mesh's triangles by material, so where the model's materials interleave
        // (the engine draws a model's polygons in order, which decides how coplanar and overlapping polygons show), the polygons
        // of one primitive go back between the others' as runs of primitives that share its material and vertices.
        List<(JsonObject Json, int Primitive, int Start, int End)> primitives = [.. pieces.Select((list, j) => (originals[j], j, 0, list.Count))];
        var runs = Interleave(pieces);
        if (!InOrder(runs, pieces) && runs.All(r => r.Start == 0 && r.End == pieces[r.Primitive].Count || IndexTarget(root, originals[r.Primitive]) != null))
        {
            primitives.Clear();
            foreach (var (j, start, end) in runs)
            {
                if (start == 0 && end == pieces[j].Count) { primitives.Add((originals[j], j, start, end)); continue; }
                var (bufferIndex, type) = IndexTarget(root, originals[j])!.Value;
                var clone = (JsonObject)originals[j].DeepClone(); clone.Remove("indices");
                List<int> values = [];
                for (int u = start; u < end; u++)
                {
                    work.Charge(pieces[j][u].Count);
                    values.AddRange(mesh.Primitives[j].Indices.GetRange(pieces[j][u].First * 3, pieces[j][u].Count * 3));
                }
                byte[] data = new byte[(long)values.Count * Size(type)];
                WriteIndices(data, values, Size(type), meshIndex);
                appended.Add((bufferIndex, data, clone, type, values.Count));
                primitives.Add((clone, j, start, end));
            }
            // Primitives without triangles stay, after the others.
            for (int j = 0; j < pieces.Count; j++) if (pieces[j].Count == 0) primitives.Add((originals[j], j, 0, 0));
            jsonPrimitives!.Clear();
            foreach (var (json, _, _, _) in primitives) jsonPrimitives.Add(json);
        }
        // Index data a whole primitive keeps is rewritten where its corners were (a primitive that gained the triangles Blender
        // dropped has new data); a primitive in several runs has new data.
        foreach (int j in rewrites.Where(r => r.Value.Count > 0).Select(r => r.Key).Union(grown))
            if (primitives.Any(p => ReferenceEquals(p.Json, originals[j]))) Patch(root, originals[j], meshIndex, mesh.Primitives[j], buffer);

        // Record every primitive's polygons, and what each continues, in the order they are written.
        List<int> sources = [];
        foreach (var (json, j, start, end) in primitives)
        {
            JsonArray entries = [];
            for (int u = start; u < end; u++) { entries.Add(pieces[j][u].Entry); sources.Add(pieces[j][u].Polygon); }
            if (json["extras"] is null) json["extras"] = new JsonObject();
            if (json["extras"] is not JsonObject extras) throw new InvalidDataException($"{model}: primitive {j} of the export's mesh {meshIndex} has extras that are not an object.");
            if (extras[WorldGltf.Key] is null) extras[WorldGltf.Key] = new JsonObject();
            if (extras[WorldGltf.Key] is not JsonObject engine) throw new InvalidDataException($"{model}: primitive {j} of the export's mesh {meshIndex} has engine values that are not an object.");
            engine["polygons"] = entries;
        }
        meshes[meshIndex] = (oldMesh, sources);
        if (!continued.TryGetValue(oldMesh, out var state)) continued[oldMesh] = state = ([], [], 0);
        state.Kept.UnionWith(carried); state.Kept.UnionWith(sources.Where(q => q >= 0)); state.Whole.UnionWith(carried);
        continued[oldMesh] = (state.Kept, state.Whole, state.Added + added);
    }

    /// <summary>
    /// Gives back the project triangles Blender's import dropped (<paramref name="dropped"/>, each with the triangles of its
    /// vertices Blender kept). Each goes into the export primitive holding the kept triangle the export continues, on the
    /// export vertices of that triangle's corners (so it follows them where they moved), in the place the project's model
    /// has it: after what the export continues of the triangle before it, or before what it continues of the one after it.
    /// One whose kept triangles the export does not continue (deleted or rebuilt in Blender) is gone with them, as is one
    /// whose primitive's index data cannot be written (<paramref name="writable"/>). Returns the export primitives that
    /// gained triangles; <paramref name="paired"/> follows their new places.
    /// </summary>
    private HashSet<int> Restore(GltfMesh source, GltfMesh mesh, List<Triangle> old, int[] firstOf, Dictionary<(int, int), List<Triangle>> dropped,
        Dictionary<(int Primitive, int Index), Triangle> paired, HashSet<(int, int)> used, Func<int, bool> writable)
    {
        HashSet<int> grown = [];
        if (dropped.Count == 0) return grown;
        work.Charge(paired.Count + old.Count);
        Dictionary<(int, int), (int Primitive, int Index)> partner = [];
        foreach (var (n, o) in paired) partner[(o.Primitive, o.Index)] = n;
        Dictionary<int, List<(int Before, long Order, int[] Corners, Triangle Old)>> inserts = [];
        Dictionary<int, bool> canWrite = [];
        foreach (var o in old)
        {
            if (!dropped.TryGetValue((o.Primitive, o.Index), out var kept)) continue;
            work.Charge(kept.Count);
            Triangle? twin = null; (int Primitive, int Index) at = default;
            foreach (var k in kept) if (partner.TryGetValue((k.Primitive, k.Index), out at)) { twin = k; break; }
            if (twin is not { } t) continue;
            int j = at.Primitive;
            if (!(canWrite.TryGetValue(j, out bool can) ? can : canWrite[j] = writable(j))) continue;
            var from = source.Primitives[o.Primitive].Indices; var target = mesh.Primitives[j].Indices;
            int[] corners = new int[3];
            for (int c = 0; c < 3; c++)
            {
                // Both name the same three vertices.
                int vertex = from[o.Index * 3 + c], k = 0;
                while (from[t.Index * 3 + k] != vertex) k++;
                corners[c] = target[at.Index * 3 + k];
            }
            int before = at.Index + 1;
            int p = o.Index - 1, q = o.Index + 1, count = from.Count / 3;
            while (p >= 0 && dropped.ContainsKey((o.Primitive, p))) { work.Charge(1); p--; }
            while (q < count && dropped.ContainsKey((o.Primitive, q))) { work.Charge(1); q++; }
            if (p >= 0 && partner.TryGetValue((o.Primitive, p), out var previous) && previous.Primitive == j) before = previous.Index + 1;
            else if (q < count && partner.TryGetValue((o.Primitive, q), out var following) && following.Primitive == j) before = following.Index;
            if (!inserts.TryGetValue(j, out var list)) inserts[j] = list = [];
            list.Add((before, firstOf[o.Primitive] + o.Index, corners, o));
        }
        foreach (var (j, list) in inserts)
        {
            list.Sort((a, b) => a.Before != b.Before ? a.Before.CompareTo(b.Before) : a.Order.CompareTo(b.Order));
            var indices = mesh.Primitives[j].Indices;
            int count = indices.Count / 3, u = 0;
            work.Charge(count + list.Count);
            int[] place = new int[count];
            List<int> rebuilt = new(indices.Count + 3 * list.Count);
            List<((int, int) At, Triangle Old)> added = [];
            for (int i = 0; i <= count; i++)
            {
                for (; u < list.Count && list[u].Before == i; u++) { added.Add(((j, rebuilt.Count / 3), list[u].Old)); rebuilt.AddRange(list[u].Corners); }
                if (i == count) break;
                place[i] = rebuilt.Count / 3;
                rebuilt.AddRange(indices.GetRange(3 * i, 3));
            }
            indices.Clear(); indices.AddRange(rebuilt);
            var moved = paired.Where(e => e.Key.Primitive == j).ToList();
            foreach (var (key, _) in moved) paired.Remove(key);
            foreach (var (key, value) in moved) paired[(j, place[key.Index])] = value;
            foreach (var (key, value) in added) { paired[key] = value; used.Add((value.Primitive, value.Index)); }
            grown.Add(j);
        }
        return grown;
    }

    /// <summary>A triangle's three vertices in increasing order, or (-1, -1, -1) when it names one twice.</summary>
    private static (int A, int B, int C) Sorted(GltfPrimitive primitive, int triangle)
    {
        int a = primitive.Indices[3 * triangle], b = primitive.Indices[3 * triangle + 1], c = primitive.Indices[3 * triangle + 2];
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return a == b || b == c ? (-1, -1, -1) : (a, b, c);
    }

    private static Key KeyOf(GltfPrimitive primitive, int triangle)
    {
        // A negative zero is the same position.
        Vector3 At(int c) { var v = primitive.Positions[primitive.Indices[triangle * 3 + c]]; return new(v.X + 0f, v.Y + 0f, v.Z + 0f); }
        return new(At(0), At(1), At(2));
    }

    /// <summary>Whether two vertices of a primitive differ at most in their normal (Blender's split of one vertex by face).</summary>
    private static bool Same(GltfPrimitive primitive, int a, int b) =>
        primitive.Positions[a] == primitive.Positions[b]
        && (primitive.TexCoords.Count != primitive.Positions.Count || primitive.TexCoords[a] == primitive.TexCoords[b])
        && primitive.Targets.All(t => t.Count != primitive.Positions.Count || t[a] == t[b]);

    /// <summary>
    /// Pairs export triangles with project triangles in order: positions found once on each side anchor the longest
    /// ordered run, equal neighbours extend it, and a changed run between anchors pairs position by position when both
    /// sides have as many triangles (reported as a numbered gap, kept only if its vertices stay consistent).
    /// </summary>
    private static IEnumerable<(Triangle New, Triangle Old, bool Exact, int Gap)> Sequence(List<Triangle> next, List<Triangle> previous, PolygonWorkBudget work)
    {
        Dictionary<Key, int> oldCount = [], newCount = [], oldAt = [];
        for (int i = 0; i < previous.Count; i++) { oldCount[previous[i].Key] = oldCount.GetValueOrDefault(previous[i].Key) + 1; oldAt[previous[i].Key] = i; }
        foreach (var t in next) newCount[t.Key] = newCount.GetValueOrDefault(t.Key) + 1;
        List<(int New, int Old)> unique = [];
        for (int i = 0; i < next.Count; i++)
            if (oldCount.GetValueOrDefault(next[i].Key) == 1 && newCount[next[i].Key] == 1) unique.Add((i, oldAt[next[i].Key]));
        work.Charge(next.Count + previous.Count + 2L * unique.Count);
        // The longest run of anchors ordered on both sides (patience sorting).
        List<int> tails = []; int[] back = new int[unique.Count];
        for (int i = 0; i < unique.Count; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (unique[tails[mid]].Old < unique[i].Old) lo = mid + 1; else hi = mid; }
            back[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
        }
        List<(int New, int Old)> anchors = [];
        for (int i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = back[i]) anchors.Add(unique[i]);
        anchors.Reverse();
        anchors.Add((next.Count, previous.Count));
        int pn = -1, po = -1, gap = 0;
        List<(Triangle, Triangle, bool, int)> result = [];
        foreach (var (an, ao) in anchors)
        {
            int sn = pn + 1, so = po + 1, en = an - 1, eo = ao - 1;
            while (sn <= en && so <= eo && next[sn].Key == previous[so].Key) result.Add((next[sn++], previous[so++], true, -1));
            List<(Triangle, Triangle, bool, int)> tail = [];
            while (en >= sn && eo >= so && next[en].Key == previous[eo].Key) tail.Add((next[en--], previous[eo--], true, -1));
            if (en >= sn && en - sn == eo - so)
            {
                for (int k = 0; sn + k <= en; k++) result.Add((next[sn + k], previous[so + k], false, gap));
                gap++;
            }
            tail.Reverse(); result.AddRange(tail);
            if (an < next.Count) result.Add((next[an], previous[ao], true, -1));
            pn = an; po = ao;
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------------ buffers

    /// <summary>
    /// For a triangle list read through an index accessor in a buffer file, whose values a recovered polygon can rewrite: that
    /// buffer, where index data written for the primitive goes, and the accessor's component type; otherwise null.
    /// </summary>
    private static (int Buffer, int Type)? IndexTarget(JsonObject root, JsonObject primitive)
    {
        if (!GltfInteger.TryInt64(primitive["indices"], out long accessor) || GltfInteger.TryInt64(primitive["mode"], out long mode) && mode != 4
            || Item(root["accessors"], accessor) is not { } indices || !GltfInteger.TryInt64(indices["componentType"], out long type) || type is not (5121 or 5123 or 5125)
            || !GltfInteger.TryInt64(indices["bufferView"], out long view) || Item(root["bufferViews"], view) is not { } bufferView
            || !GltfInteger.TryInt64(bufferView["buffer"], out long buffer) || Item(root["buffers"], buffer)?["uri"] is not JsonValue uri
            || !uri.TryGetValue(out string? file) || file.StartsWith("data:", StringComparison.Ordinal)) return null;
        return ((int)buffer, (int)type);
        static JsonObject? Item(JsonNode? list, long index) => list is JsonArray array && index >= 0 && index < array.Count ? array[(int)index] as JsonObject : null;
    }
    private static int Size(int type) => type switch { 5121 => 1, 5123 => 2, _ => 4 };
    /// <summary>Writes index values as <paramref name="size"/>-byte little-endian integers.</summary>
    private void WriteIndices(Span<byte> target, IReadOnlyList<int> values, int size, int meshIndex)
    {
        for (int i = 0; i < values.Count; i++)
        {
            uint value = (uint)values[i];
            if (size < 4 && value >> (8 * size) != 0) throw new InvalidDataException($"{model}: the export's indices of mesh {meshIndex} cannot be rewritten.");
            switch (size)
            {
                case 1: target[i] = (byte)value; break;
                case 2: BinaryPrimitives.WriteUInt16LittleEndian(target[(2 * i)..], (ushort)value); break;
                default: BinaryPrimitives.WriteUInt32LittleEndian(target[(4 * i)..], value); break;
            }
        }
    }

    /// <summary>
    /// The runs of polygons a mesh's primitives are written in, in the model's polygon order: each primitive keeps its own
    /// order, and the primitives' polygons interleave by the first project triangle each holds. A polygon that holds none
    /// follows the one before it in its primitive (or precedes the first that holds one), and a primitive holding none comes
    /// after the others.
    /// </summary>
    private List<(int Primitive, int Start, int End)> Interleave(List<List<Piece>> pieces)
    {
        long[][] places = new long[pieces.Count][];
        PriorityQueue<int, (long Place, int Primitive)> heads = new();
        for (int j = 0; j < pieces.Count; j++)
        {
            var list = pieces[j]; var place = places[j] = new long[list.Count];
            work.Charge(1L + 2L * list.Count);
            long last = -1, following = long.MaxValue;
            for (int u = 0; u < list.Count; u++) place[u] = last = list[u].Place >= 0 ? list[u].Place : last;
            for (int u = list.Count - 1; u >= 0; u--) { if (list[u].Place >= 0) following = list[u].Place; if (place[u] < 0) place[u] = following; }
            if (list.Count > 0) heads.Enqueue(j, (place[0], j));
        }
        List<(int Primitive, int Start, int End)> runs = [];
        int[] at = new int[pieces.Count];
        while (heads.TryDequeue(out int j, out _))
        {
            int u = at[j]++;
            if (runs.Count > 0 && runs[^1].Primitive == j && runs[^1].End == u) runs[^1] = (j, runs[^1].Start, u + 1);
            else runs.Add((j, u, u + 1));
            if (at[j] < pieces[j].Count) heads.Enqueue(j, (places[j][at[j]], j));
        }
        return runs;
    }
    /// <summary>Whether the runs are the primitives, whole and in order (nothing to put back).</summary>
    private static bool InOrder(List<(int Primitive, int Start, int End)> runs, List<List<Piece>> pieces)
    {
        int k = 0;
        for (int j = 0; j < pieces.Count; j++)
        {
            if (pieces[j].Count == 0) continue;
            if (k >= runs.Count || runs[k] != (j, 0, pieces[j].Count)) return false;
            k++;
        }
        return k == runs.Count;
    }

    /// <summary>
    /// Writes a primitive's index values (with the corners a recovered polygon rewrote and the triangles given back): in place
    /// when the accessor and its view are the primitive's own and as long, otherwise (Blender shares identical index data, or
    /// the primitive gained triangles) as a new accessor that <see cref="Append"/>
    /// writes after the buffer's data, through <paramref name="buffer"/> (a writable copy of a buffer).
    /// </summary>
    private void Patch(JsonObject root, JsonObject json, int meshIndex, GltfPrimitive primitive, Func<int, byte[]> buffer)
    {
        if (accessorUses == null)
        {
            accessorUses = []; viewUses = [];
            foreach (var mesh in root["meshes"] as JsonArray ?? [])
                foreach (var other in mesh?["primitives"] as JsonArray ?? [])
                {
                    if (GltfInteger.TryInt64(other?["indices"], out long a)) accessorUses[a] = accessorUses.GetValueOrDefault(a) + 1;
                    foreach (var attribute in other?["attributes"] as JsonObject ?? []) if (GltfInteger.TryInt64(attribute.Value, out long b)) accessorUses[b] = accessorUses.GetValueOrDefault(b) + 1;
                    foreach (var target in other?["targets"] as JsonArray ?? []) foreach (var attribute in target as JsonObject ?? []) if (GltfInteger.TryInt64(attribute.Value, out long c)) accessorUses[c] = accessorUses.GetValueOrDefault(c) + 1;
                }
            foreach (var accessor in root["accessors"] as JsonArray ?? []) if (GltfInteger.TryInt64(accessor?["bufferView"], out long v)) viewUses[v] = viewUses.GetValueOrDefault(v) + 1;
        }
        int accessorIndex = GltfInteger.Int32(json["indices"]);
        var indices = (JsonObject)root["accessors"]![accessorIndex]!;
        int viewIndex = GltfInteger.Int32(indices["bufferView"]), type = GltfInteger.Int32(indices["componentType"]), size = Size(type);
        var view = (JsonObject)root["bufferViews"]![viewIndex]!;
        int bufferIndex = GltfInteger.Int32(view["buffer"]);
        bool own = accessorUses[accessorIndex] == 1 && viewUses![viewIndex] == 1 && indices["sparse"] == null && view["byteStride"] == null
            && GltfInteger.TryInt64(indices["count"], out long count) && count == primitive.Indices.Count;
        long length = (long)primitive.Indices.Count * size;
        if (own)
        {
            var bytes = buffer(bufferIndex);
            long offset = (GltfInteger.OptionalInt64(view["byteOffset"], "byteOffset") ?? 0) + (GltfInteger.OptionalInt64(indices["byteOffset"], "byteOffset") ?? 0);
            if (offset < 0 || offset + length > bytes.Length) throw new InvalidDataException($"{model}: the export's indices of mesh {meshIndex} cannot be rewritten.");
            WriteIndices(bytes.AsSpan((int)offset, (int)length), primitive.Indices, size, meshIndex);
            return;
        }
        accessorUses[accessorIndex]--;
        byte[] data = new byte[length]; WriteIndices(data, primitive.Indices, size, meshIndex);
        appended.Add((bufferIndex, data, json, type, primitive.Indices.Count));
    }

    /// <summary>Writes the index data of primitives that shared it after their buffers' data (each buffer grown once), as new accessors.</summary>
    private void Append(JsonObject root, Func<int, byte[]> buffer, Action<int, byte[]> replace)
    {
        foreach (var group in appended.GroupBy(a => a.Buffer))
        {
            var bytes = buffer(group.Key);
            long total = bytes.Length;
            foreach (var item in group) total = ((total + 3) & ~3L) + item.Data.Length;
            if (total > int.MaxValue) throw new InvalidDataException($"{model}: the export's buffer {group.Key} cannot take the rewritten indices.");
            byte[] grown = new byte[total]; bytes.CopyTo(grown, 0);
            long offset = bytes.Length;
            var views = (JsonArray)root["bufferViews"]!; var accessors = (JsonArray)root["accessors"]!;
            foreach (var (_, data, primitive, type, count) in group)
            {
                offset = (offset + 3) & ~3L;
                data.CopyTo(grown, offset);
                views.Add(new JsonObject { ["buffer"] = group.Key, ["byteOffset"] = offset, ["byteLength"] = data.Length, ["target"] = 34963 });
                accessors.Add(new JsonObject { ["bufferView"] = views.Count - 1, ["componentType"] = type, ["count"] = count, ["type"] = "SCALAR" });
                primitive["indices"] = accessors.Count - 1;
                offset += data.Length;
            }
            root["buffers"]![group.Key]!["byteLength"] = grown.Length;
            replace(group.Key, grown);
        }
        appended.Clear();
    }
}
