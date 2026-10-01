using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Gltf;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>What an exported material stands for: an engine material plus the polygon attributes glTF keeps per material.</summary>
public readonly record struct SurfaceKey(WorldMaterial Material, int Priority, bool ShowBackFace, uint Zone, bool Normals);

/// <summary>
/// The RECOIL profile of glTF used by source projects. glTF nodes are engine nodes (object3d, or lod with extras),
/// meshes are display instances (shared meshes are shared models), materials are engine materials together with the
/// per-polygon draw attributes, and textures are PNG files named like the pack textures. Engine-only values live in
/// <c>extras.recoil</c> (Blender custom properties); a value that equals its default is omitted.
/// </summary>
public static partial class WorldGltf
{
    public const string Key = "recoil";
    /// <summary>Node flag bits a source carries: surface tests, landmark, crater/clip, overwrite, variant gate and game bits. The rest are derived.</summary>
    public const uint CarriedFlags = 0x08 | 0x10 | 0x20 | 0x40 | 0x80 | 0x10000 | 0x20000 | 0x800000 | 0x01000000 | 0x70000000;
    /// <summary>What the original loader gave a plain node: altitude and intersection surfaces and the variant gate.</summary>
    public const uint DefaultCarried = 0x01000018;
    public const uint DefaultZone = 0xFF, DefaultPolygonZone = 0xFFFFFF00, DefaultModelFlags = 0x3;

    [GeneratedRegex(@"\.\d{3}\z")] private static partial Regex BlenderSuffix();

    // ---------------------------------------------------------------- export

    /// <summary>How an exporter names textures and refers to external parts.</summary>
    public sealed class ExportContext
    {
        /// <summary>The image URI (relative to the glTF file) and the clamp word for a texture.</summary>
        public required Func<WorldTexture, (string Uri, int Addressing)> Texture { get; init; }
        /// <summary>For a node that is an external reference, the referenced file's URI; its children are then not written.</summary>
        public Func<WorldNode, string?> Reference { get; init; } = _ => null;
        /// <summary>Clear the runtime state of point entries (elapsed time, packed state, heap words, flare runtime values) to compare content.</summary>
        public bool Canonical { get; init; }
        internal Dictionary<WorldModel, GltfMesh> Meshes { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<SurfaceKey, GltfMaterial> Materials { get; } = [];
        internal HashSet<string> MaterialNames { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A glTF document whose scene roots are <paramref name="roots"/>; <paramref name="parentZone"/> is the zone they would
    /// inherit. <paramref name="loadRoot"/> is the node a script load creates for the file: its flags come from the file.
    /// </summary>
    public static GltfDocument Export(IReadOnlyList<WorldNode> roots, uint parentZone, ExportContext context, WorldNode? loadRoot = null)
    {
        GltfDocument doc = new();
        if (loadRoot != null && (loadRoot.Flags & CarriedFlags) != DefaultCarried)
            doc.SceneExtras = new() { [Key] = new JsonObject { ["rootFlags"] = $"0x{loadRoot.Flags & CarriedFlags:X8}" } };
        // Count how often each node is reached: a node under several parents is written under each (glTF nodes have
        // one parent) and marked so that import joins the copies again.
        Dictionary<WorldNode, int> reached = new(ReferenceEqualityComparer.Instance); HashSet<WorldNode> path = new(ReferenceEqualityComparer.Instance);
        int visits = 0;
        void Count(WorldNode node)
        {
            if (++visits > MaximumExportedNodes) throw new InvalidDataException($"The model hierarchy expands to more than {MaximumExportedNodes} nodes.");
            if (!path.Add(node)) throw new InvalidDataException($"Node {node.Name} is its own ancestor.");
            if (path.Count > WorldUpdate.MaximumDepth) throw new InvalidDataException($"The model hierarchy is deeper than {WorldUpdate.MaximumDepth} levels.");
            reached[node] = reached.GetValueOrDefault(node) + 1;
            if (reached[node] == 1 && context.Reference(node) == null) foreach (var child in node.Children) Count(child);
            path.Remove(node);
        }
        foreach (var root in roots) Count(root);
        ExportState state = new()
        {
            Duplicates = new(reached.Keys.GroupBy(n => n.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.Ordinal),
            Shared = new(reached.Where(r => r.Value > 1).Select(r => r.Key), ReferenceEqualityComparer.Instance),
        };
        foreach (var root in roots) doc.Roots.Add(ExportNode(root, parentZone, context, state));
        return doc;
    }
    /// <summary>The largest hierarchy an export writes, counting every copy of a shared node.</summary>
    public const int MaximumExportedNodes = 200_000;
    private sealed class ExportState
    {
        public required HashSet<string> Duplicates { get; init; }
        public required HashSet<WorldNode> Shared { get; init; }
        public Dictionary<WorldNode, int> Instances { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private static GltfNode ExportNode(WorldNode node, uint parentZone, ExportContext context, ExportState state)
    {
        if (node.Class is not (WorldNodeClass.Object3D or WorldNodeClass.Lod))
            throw new InvalidDataException($"Node {node.Name} is a {node.Class} node; model files hold object3d and lod nodes.");
        GltfNode result = new() { Name = node.Name };
        JsonObject extras = [];
        // Import drops a .NNN suffix (Blender's copies), so a name that has one is written out.
        if (state.Duplicates.Contains(node.Name) || BlenderSuffix().IsMatch(node.Name)) extras["name"] = node.Name;
        // Every copy of a shared node carries its instance number; the first copy in the file is the one imported.
        if (state.Shared.Contains(node))
        {
            if (!state.Instances.TryGetValue(node, out int instance)) state.Instances[node] = instance = state.Instances.Count + 1;
            extras["instance"] = instance;
        }
        uint carried = node.Flags & CarriedFlags;
        if (carried != DefaultCarried) extras["flags"] = $"0x{carried:X8}";
        uint zone = node.Zone & 0xFF;
        if (zone != parentZone) extras["zone"] = (int)zone;
        if ((node.Zone & ~0xFFu) != 0) extras["zoneWord"] = $"0x{node.Zone:X8}";
        if (node.Class == WorldNodeClass.Lod)
        {
            extras["class"] = "lod";
            JsonArray fields = [];
            for (int i = 0; i < 20; i++) fields.Add(i is 0 or 12 or 17 or 18 ? (JsonNode)node.PayloadInt(i * 4) : node.PayloadFloat(i * 4));
            extras["lod"] = fields;
        }
        else if (WorldUpdate.LocalMatrix(node) is { } matrix && !matrix.IsIdentity) result.Matrix = matrix;
        if (node.Model != null)
        {
            // A glTF mesh needs a primitive and editors drop one without (Blender omits it), so a model without polygons
            // (a lens flare's points) keeps its values with each node that uses it.
            var mesh = ExportMesh(node.Model, context);
            if (mesh.Primitives.Count > 0) result.Mesh = mesh;
            else extras["model"] = mesh.Extras?[Key]?.DeepClone() ?? new JsonObject();
        }
        if (context.Reference(node) is { } uri) extras["ref"] = uri;
        else foreach (var child in node.Children) result.Children.Add(ExportNode(child, zone, context, state));
        if (extras.Count > 0) result.Extras = new() { [Key] = extras };
        return result;
    }

    private static GltfMesh ExportMesh(WorldModel model, ExportContext context)
    {
        if (context.Meshes.TryGetValue(model, out var existing)) return existing;
        GltfMesh mesh = new() { Name = $"model{context.Meshes.Count}" };
        GltfPrimitive? primitive = null; SurfaceKey? key = null;
        Dictionary<(int V, int N, float U, float W), int> corners = [];
        // What each stored polygon became, per primitive: a fan's triangle count, or the corners of a polygon written
        // any other way; recorded where fan merging alone would not restore the polygons.
        List<(GltfPrimitive Primitive, bool Textured, List<JsonNode> Polygons)> groups = [];
        foreach (var polygon in model.Polygons)
        {
            if (polygon.Material == null) continue;
            // Zero-area triangles (a repeated corner in a few shipped polygons) draw nothing and are not written.
            var points = polygon.Vertices.Select(v => model.Vertices[v]).ToArray();
            var triangles = Triangulate(points).Where(t => !ModelBuilder.Straight(points[t.Item1], points[t.Item2], points[t.Item3])).ToList();
            if (triangles.Count == 0) continue;
            bool fan = triangles.Count == points.Length - 2 && triangles.Select((t, k) => t == (0, k + 1, k + 2)).All(x => x);
            SurfaceKey k = new(polygon.Material, polygon.Priority, (polygon.Flags & 0x100) != 0, polygon.Zone, polygon.Normals.Length > 0);
            // Consecutive polygons with the same surface share a primitive, keeping the stored polygon order.
            if (primitive == null || key != k)
            {
                primitive = new() { Material = ExportMaterial(k, context) }; mesh.Primitives.Add(primitive); key = k; corners.Clear();
                if (model.Morphs.Count > 0) primitive.Targets.Add([]);
                groups.Add((primitive, polygon.Material.Texture != null, []));
            }
            int[] index = new int[polygon.Vertices.Length];
            // Every corner is written, including one only a skipped triangle used, so a corner list can name it.
            for (int i = 0; i < points.Length; i++)
            {
                var uv = polygon.Uvs.Length > 0 ? polygon.Uvs[i] : Vector2.Zero;
                var corner = (polygon.Vertices[i], polygon.Normals.Length > 0 ? polygon.Normals[i] : -1, uv.X, uv.Y);
                if (!corners.TryGetValue(corner, out index[i]))
                {
                    index[i] = corners[corner] = primitive.Positions.Count;
                    primitive.Positions.Add(model.Vertices[polygon.Vertices[i]]);
                    if (polygon.Normals.Length > 0) primitive.Normals.Add(model.Normals[polygon.Normals[i]]);
                    if (polygon.Uvs.Length > 0) primitive.TexCoords.Add(uv);
                    if (model.Morphs.Count > 0) primitive.Targets[0].Add(polygon.Vertices[i] < model.Morphs.Count ? model.Morphs[polygon.Vertices[i]] : Vector3.Zero);
                }
            }
            foreach (var (a, b, c) in triangles) primitive.Indices.AddRange([index[a], index[b], index[c]]);
            groups[^1].Polygons.Add(fan ? triangles.Count : new JsonObject { ["triangles"] = triangles.Count, ["corners"] = new JsonArray(index.Select(i => (JsonNode)i).ToArray()) });
        }
        if (model.Morphs.Count > 0) mesh.Weights.Add(model.MorphFactor);
        foreach (var (target, textured, polygons) in groups)
            if (polygons.Any(p => p is JsonObject) || !MergeFans(target, textured).Select(p => p.Length - 2).SequenceEqual(polygons.Select(p => p.GetValue<int>())))
                target.Extras = new() { [Key] = new JsonObject { ["polygons"] = new JsonArray([.. polygons]) } };
        JsonObject extras = [];
        if (model.Mode != 0) extras["mode"] = (int)model.Mode;
        if (model.Flags != DefaultModelFlags) extras["flags"] = (int)model.Flags;
        if (model.ScrollU != 0 || model.ScrollV != 0 || model.ScrollFrame != 0) extras["scroll"] = new JsonArray(model.ScrollU, model.ScrollV, (long)model.ScrollFrame);
        if (model.MorphFactor != 0) extras["morphFactor"] = model.MorphFactor;
        if (model.Points.Count > 0)
            extras["points"] = new JsonArray(model.Points.Select(p => (JsonNode)new JsonObject
            {
                ["record"] = Convert.ToHexStringLower(context.Canonical ? CanonicalPoint(p.Record) : StoredPoint(p.Record)),
                ["vertices"] = new JsonArray(p.Vertices.Select(v => (JsonNode)new JsonArray(v.X, v.Y, v.Z)).ToArray()),
            }).ToArray());
        if (extras.Count > 0) mesh.Extras = new() { [Key] = extras };
        context.Meshes[model] = mesh;
        return mesh;
    }

    /// <summary>A point entry as a world file stores it: the vertex list pointer (+44) is a runtime value, written as 0.</summary>
    private static byte[] StoredPoint(byte[] record)
    {
        byte[] copy = (byte[])record.Clone();
        if (copy.Length >= 48) Array.Clear(copy, 44, 4);
        return copy;
    }
    /// <summary>A point entry without its runtime fields: elapsed time and packed state (+16..+27), packed colour and list pointer (+40..+47) and the flare's runtime values (+60..+75).</summary>
    private static byte[] CanonicalPoint(byte[] record)
    {
        byte[] copy = (byte[])record.Clone();
        Array.Clear(copy, 16, 12); Array.Clear(copy, 40, 8); Array.Clear(copy, 60, 16);
        return copy;
    }

    private static GltfMaterial ExportMaterial(SurfaceKey key, ExportContext context)
    {
        if (context.Materials.TryGetValue(key, out var existing)) return existing;
        var m = key.Material; JsonObject extras = [];
        byte opacity = (byte)(m.Flags & 0xFF);
        GltfMaterial material = new() { DoubleSided = key.ShowBackFace };
        string name;
        if (m.Texture != null)
        {
            var (uri, addressing) = context.Texture(m.Texture);
            material.ImageUri = uri; material.ClampS = (addressing & 1) != 0; material.ClampT = (addressing & 2) != 0;
            extras["texture"] = m.Texture.Name; name = m.Texture.Name;
        }
        else
        {
            material.BaseColor = new(m.Color.X / 255f, m.Color.Y / 255f, m.Color.Z / 255f, 1);
            extras["color"] = new JsonArray(m.Color.X, m.Color.Y, m.Color.Z);
            name = FormattableString.Invariant($"color_{(int)m.Color.X:X2}{(int)m.Color.Y:X2}{(int)m.Color.Z:X2}");
        }
        if (opacity != 0xFF)
        {
            extras["opacity"] = (int)opacity; material.BaseColor = material.BaseColor with { W = opacity / 255f }; material.AlphaMode = "BLEND";
            name += $"~o{opacity:X2}";
        }
        uint extraFlags = (uint)m.Flags & ~0x1FFu;
        if (extraFlags != 0) extras["flags"] = $"0x{extraFlags:X4}";
        if (m.Soil != 0) { extras["soil"] = (int)m.Soil; name += $"~s{m.Soil}"; }
        if (m.Field14 != 0 || m.Field18 != 0.5f || m.Field1C != 0.5f) extras["fields"] = new JsonArray(m.Field14, m.Field18, m.Field1C);
        if (m.PackedColor != (m.Texture != null ? 0x7FFF : 0)) extras["packedColor"] = $"0x{m.PackedColor:X4}";
        if (key.Priority != 0) { extras["priority"] = key.Priority; name += $"~p{key.Priority}"; }
        if (key.ShowBackFace) { extras["backface"] = true; name += "~b"; }
        if (key.Zone != DefaultPolygonZone) { extras["zone"] = $"0x{key.Zone:X8}"; name += $"~z{key.Zone:X8}"; }
        if (key.Normals) extras["normals"] = true; else name += "~flat";
        string unique = name; for (int i = 2; !context.MaterialNames.Add(unique); i++) unique = $"{name}~{i}";
        material.Name = unique; material.Extras = new() { [Key] = extras };
        context.Materials[key] = material;
        return material;
    }

    /// <summary>Triangles of a polygon: a fan from the first corner when it is convex, ear clipping otherwise.</summary>
    public static IEnumerable<(int, int, int)> Triangulate(IReadOnlyList<Vector3> points)
    {
        int n = points.Count; if (n < 3) yield break;
        Vector3 normal = Vector3.Zero;
        for (int i = 0; i < n; i++) normal += Vector3.Cross(points[i], points[(i + 1) % n]);
        bool convex = true;
        for (int i = 0; i < n && convex; i++)
            if (Vector3.Dot(Vector3.Cross(points[(i + 1) % n] - points[i], points[(i + 2) % n] - points[(i + 1) % n]), normal) < -1e-9f) convex = false;
        if (convex) { for (int i = 1; i + 1 < n; i++) yield return (0, i, i + 1); yield break; }
        List<int> remaining = Enumerable.Range(0, n).ToList(); int guard = n * n;
        while (remaining.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int a = remaining[(i + remaining.Count - 1) % remaining.Count], b = remaining[i], c = remaining[(i + 1) % remaining.Count];
                if (Vector3.Dot(Vector3.Cross(points[b] - points[a], points[c] - points[b]), normal) <= 0) continue;
                bool inside = remaining.Any(p => p != a && p != b && p != c && Inside(points[p], points[a], points[b], points[c], normal));
                if (inside) continue;
                yield return (a, b, c); remaining.RemoveAt(i); clipped = true; break;
            }
            if (!clipped) break;
        }
        for (int i = 1; i + 1 < remaining.Count; i++) yield return (remaining[0], remaining[i], remaining[i + 1]);
        static bool Inside(Vector3 p, Vector3 a, Vector3 b, Vector3 c, Vector3 n) =>
            Vector3.Dot(Vector3.Cross(b - a, p - a), n) > 0 && Vector3.Dot(Vector3.Cross(c - b, p - b), n) > 0 && Vector3.Dot(Vector3.Cross(a - c, p - c), n) > 0;
    }

    // ---------------------------------------------------------------- import

    /// <summary>State shared by one load (a LoadGameGen call): its models are shared by repeated references, as the original loader shared them.</summary>
    public sealed class ImportContext
    {
        public required GameZWorld World { get; init; }
        /// <summary>Loads an external reference relative to the referencing file: the document and the path it came from.</summary>
        public required Func<string, string, (GltfDocument Document, string Path)> Reference { get; init; }
        /// <summary>Texture name for a material's image URI (relative to the file at the given path).</summary>
        public required Func<string, string?, string, string> TextureName { get; init; }
        public List<string> Warnings { get; } = [];
        /// <summary>Called for each node a glTF node becomes, with the file's path and the glTF node (a shared node once).</summary>
        public Action<WorldNode, string, GltfNode>? NodeImported { get; init; }
        /// <summary>Cancels a load between nodes and between batches of polygons.</summary>
        public CancellationToken Token { get; init; }
        /// <summary>Reads a file relative to a referencing file (terrain recipes): its bytes and project path.</summary>
        public Func<string, string, (byte[] Bytes, string Path)>? ReadFile { get; init; }
        /// <summary>The world's grid, for the mission database load only; terrain recipes elsewhere are refused.</summary>
        public Func<Terrain.TerrainGrid>? Grid { get; init; }
        /// <summary>Called for each node a terrain recipe compiles to, with the recipe's path, the piece and its surface's id.</summary>
        public Action<WorldNode, string, Terrain.TerrainPiece, string>? TerrainPieceImported { get; init; }
        /// <summary>Texture files the load referenced, by texture name.</summary>
        public Dictionary<string, string> TextureFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Each texture's clamp word (1 clamps U, 2 clamps V) from the first sampler that uses it; the pack stores it.</summary>
        public Dictionary<string, int> TextureAddressing { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<(string Path, GltfMesh Mesh), WorldModel> Models { get; } = [];
        /// <summary>Models without polygons, carried in node extras, shared by identical values within a file.</summary>
        internal Dictionary<(string Path, string Values), WorldModel> ValueModels { get; } = [];
        internal Dictionary<(string Path, GltfDocument Doc), bool> Loading { get; } = [];
        /// <summary>Nodes this load created; with the world's, never more than a world can hold.</summary>
        internal int Created { get; set; }
        /// <summary>The world's materials and textures by value and name, built on first use (a load only adds to them).</summary>
        internal Dictionary<MaterialKey, WorldMaterial>? MaterialIndex { get; set; }
        internal Dictionary<string, WorldTexture>? TextureIndex { get; set; }
    }

    /// <summary>Engine nodes for a document's scene roots, loaded from <paramref name="path"/> under a parent with <paramref name="parentZone"/>.</summary>
    /// <remarks>Malformed engine values are reported as <see cref="InvalidDataException"/>.</remarks>
    public static List<WorldNode> Import(GltfDocument doc, string path, uint parentZone, ImportContext context) => Import(doc, path, parentZone, context, 0);

    private static List<WorldNode> Import(GltfDocument doc, string path, uint parentZone, ImportContext context, int depth)
    {
        if (!context.Loading.TryAdd((path, doc), true)) throw new InvalidDataException($"{path} references itself.");
        Dictionary<int, WorldNode> instances = [];
        try
        {
            List<WorldNode> roots = [];
            foreach (var root in doc.Roots)
            {
                // A terrain recipe stands where its pieces go among the database's roots; it is not a game node itself.
                if (root.Extras?[Key] is JsonObject marker && marker["terrain"] is { } recipe)
                {
                    if (depth > 0) throw new InvalidDataException($"{path}: the terrain recipe {recipe} must be a root of the mission database, not of a referenced file.");
                    roots.AddRange(ImportTerrain(Text(recipe, "terrain", path), path, context));
                }
                else roots.Add(ImportNode(root, path, parentZone, context, instances, depth));
            }
            return roots;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or ArgumentException or NullReferenceException or IndexOutOfRangeException)
        { throw new InvalidDataException($"{path}: an engine value in the extras is malformed: {ex.Message}", ex); }
        finally { context.Loading.Remove((path, doc)); }
    }

    /// <summary><paramref name="depth"/> counts levels across external references, which continue the hierarchy.</summary>
    private static WorldNode ImportNode(GltfNode source, string path, uint parentZone, ImportContext context, Dictionary<int, WorldNode> instances, int depth)
    {
        if (depth >= GltfDocument.MaximumDepth) throw new InvalidDataException($"{path}: the node hierarchy, with its external references, is deeper than {GltfDocument.MaximumDepth} levels.");
        context.Token.ThrowIfCancellationRequested();
        var extras = source.Extras?[Key] as JsonObject;
        if (extras?["terrain"] != null) throw new InvalidDataException($"{path}: node {source.Name} names a terrain recipe but is not a root of the mission database.");
        // Later copies of a shared node are the same node under another parent.
        long? mark = extras?["instance"] is { } marker ? Integer(marker, "instance", path) : null;
        if (mark is < 1 or > int.MaxValue) throw new InvalidDataException($"{path}: node {source.Name} has an invalid instance number.");
        int? instance = (int?)mark;
        if (instance is { } shared && instances.TryGetValue(shared, out var existing)) return existing;
        // References can repeat a file any number of times; a world holds a bounded number of nodes.
        if (++context.Created + context.World.Nodes.Count > GameZWorld.MaximumNodeCapacity)
            throw new InvalidDataException($"{path}: the model expands to more nodes than a world holds ({GameZWorld.MaximumNodeCapacity:N0}).");
        bool lod = extras?["class"] is { } kind && Text(kind, "class", path) == "lod";
        string name = extras?["name"] is { } authored ? Text(authored, "name", path) : BlenderSuffix().Replace(source.Name, "");
        WorldNode node = new(name, lod ? WorldNodeClass.Lod : WorldNodeClass.Object3D);
        if (instance is { } first) instances[first] = node;
        context.NodeImported?.Invoke(node, path, source);
        uint carried = extras?["flags"] is { } flags ? Hex(flags, "flags", path) & CarriedFlags : DefaultCarried;
        node.Flags = (lod ? 0x0108001Cu : 0x0308001Cu) & ~CarriedFlags | carried;
        node.BoundsFlags = 4;
        uint zone = extras?["zone"] is { } z ? (uint)Integer(z, "zone", path) & 0xFF : parentZone;
        node.Zone = extras?["zoneWord"] is { } word ? Hex(word, "zoneWord", path) : zone;
        if (lod)
        {
            var fields = extras?["lod"] is { } values ? values as JsonArray ?? throw new InvalidDataException($"{path}: node {name} has an invalid lod record.") : [];
            for (int i = 0; i < 20 && i < fields.Count; i++)
                if (i is 0 or 12 or 17 or 18) node.SetPayloadInt(i * 4, (int)Integer(fields[i], "lod", path, int.MinValue, int.MaxValue));
                else node.SetPayloadFloat(i * 4, Real(fields[i], "lod", path));
        }
        else
        {
            var matrix = source.Matrix ?? Matrix4x4.Identity;
            float[] rows = [matrix.M11, matrix.M12, matrix.M13, matrix.M21, matrix.M22, matrix.M23, matrix.M31, matrix.M32, matrix.M33, matrix.M41, matrix.M42, matrix.M43];
            if (!rows.All(float.IsFinite)) throw new InvalidDataException($"{path}: node {name} has a transform with a non-finite value.");
            node.SetPayloadInt(0, matrix.IsIdentity ? 0x28 : 0x30);
            node.SetPayloadFloat(0x24, 1); node.SetPayloadFloat(0x28, 1); node.SetPayloadFloat(0x2C, 1);
            for (int i = 0; i < 12; i++) node.SetPayloadFloat(0x30 + i * 4, rows[i]);
        }
        if (source.Mesh != null) node.Model = ImportMesh(source.Mesh, path, context);
        else if (extras?["model"] is { } values) node.Model = ImportValues(values as JsonObject ?? throw new InvalidDataException($"{path}: node {name} has an invalid model record."), path, context);
        if (extras?["ref"] is { } referenceValue)
        {
            var (doc, referencedPath) = context.Reference(Text(referenceValue, "ref", path), path);
            foreach (var child in Import(doc, referencedPath, zone, context, depth + 1)) Link(node, child);
        }
        foreach (var child in source.Children) Link(node, ImportNode(child, path, zone, context, instances, depth + 1));
        return node;
        static void Link(WorldNode parent, WorldNode child)
        {
            // A shared node found inside its own copy would make the graph cyclic.
            if (ReferenceEquals(parent, child) || Descends(parent, child, 0)) throw new InvalidDataException($"Node {child.Name} would become its own ancestor.");
            if (!parent.Children.Contains(child)) { parent.Children.Add(child); child.Parents.Add(parent); }
        }
        static bool Descends(WorldNode node, WorldNode ancestor, int depth) => depth <= 256 && node.Parents.Any(p => ReferenceEquals(p, ancestor) || Descends(p, ancestor, depth + 1));
    }

    private static WorldModel ImportMesh(GltfMesh mesh, string path, ImportContext context)
    {
        if (context.Models.TryGetValue((path, mesh), out var existing)) return existing;
        ModelBuilder builder = new();
        var model = builder.Model;
        ApplyValues(model, mesh.Extras?[Key] as JsonObject, mesh.Weights.Count > 0 ? mesh.Weights[0] : 0, path);
        foreach (var primitive in mesh.Primitives)
        {
            context.Token.ThrowIfCancellationRequested();
            var (material, priority, backface, zone, storesNormals) = ImportMaterial(primitive.Material, path, context);
            // Editors write normals for every surface (Blender does); a material with engine values says whether its
            // polygons stored them, so flat surfaces stay flat.
            bool textured = material.Texture != null, normals = storesNormals != false && primitive.Normals.Count == primitive.Positions.Count;
            if (textured && primitive.TexCoords.Count != primitive.Positions.Count)
            {
                context.Warnings.Add($"{path}: mesh {mesh.Name} uses texture {material.Texture!.Name} without texture coordinates; they were set to zero.");
            }
            var targets = primitive.Targets.Count > 0 && primitive.Targets[0].Count == primitive.Positions.Count ? primitive.Targets[0] : null;
            int added = 0;
            foreach (var corners in Polygons(primitive, textured))
            {
                if ((++added & 1023) == 0) context.Token.ThrowIfCancellationRequested();
                Vector3[] points = corners.Select(i => primitive.Positions[i]).ToArray();
                PolygonInput input = new(points,
                    textured ? corners.Select(i => i < primitive.TexCoords.Count ? primitive.TexCoords[i] : Vector2.Zero).ToArray() : [],
                    normals ? corners.Select(i => primitive.Normals[i]).ToArray() : [],
                    targets != null ? corners.Select(i => primitive.Positions[i] + targets[i]).ToArray() : [],
                    material, priority, backface, zone);
                builder.Add(input);
            }
        }
        foreach (var warning in builder.Warnings.Distinct()) context.Warnings.Add($"{path}: mesh {mesh.Name}: {warning}");
        builder.Finish();
        context.World.Models.Add(model);
        context.Models[(path, mesh)] = model;
        return model;
    }

    /// <summary>A model without polygons (point entries only) from a node's <c>model</c> values; identical values share one model.</summary>
    private static WorldModel ImportValues(JsonObject values, string path, ImportContext context)
    {
        string key = values.ToJsonString();
        if (context.ValueModels.TryGetValue((path, key), out var existing)) return existing;
        ModelBuilder builder = new();
        ApplyValues(builder.Model, values, 0, path);
        var model = builder.Finish();
        context.World.Models.Add(model);
        context.ValueModels[(path, key)] = model;
        return model;
    }

    /// <summary>The model values glTF cannot express: display mode and flags, scrolling, morph factor and point entries.</summary>
    private static void ApplyValues(WorldModel model, JsonObject? extras, float morphFactor, string path)
    {
        // The export writes these words as signed integers.
        model.Mode = extras?["mode"] is { } mode ? unchecked((uint)Integer(mode, "mode", path, int.MinValue, uint.MaxValue)) : 0;
        model.Flags = extras?["flags"] is { } flags ? unchecked((uint)Integer(flags, "flags", path, int.MinValue, uint.MaxValue)) : DefaultModelFlags;
        if (extras?["scroll"] is JsonArray scroll && scroll.Count == 3)
        { model.ScrollU = Real(scroll[0], "scroll", path); model.ScrollV = Real(scroll[1], "scroll", path); model.ScrollFrame = (uint)Integer(scroll[2], "scroll", path, 0, uint.MaxValue); }
        model.MorphFactor = extras?["morphFactor"] is { } factor ? Real(factor, "morphFactor", path) : morphFactor;
        foreach (var point in extras?["points"] as JsonArray ?? [])
        {
            if (point?["record"] is not { } text || Text(text, "point record", path).Length != 152 || !IsHex(text.GetValue<string>()))
                throw new InvalidDataException($"{path}: a point entry record needs 76 bytes of hexadecimal.");
            var vertices = point["vertices"] is { } list ? list as JsonArray ?? throw new InvalidDataException($"{path}: a point entry has invalid vertices.") : [];
            model.Points.Add(new()
            {
                Record = Convert.FromHexString(text.GetValue<string>()),
                Vertices = vertices.Select(v => v is JsonArray { Count: 3 } xyz ? new Vector3(Real(xyz[0], "point", path), Real(xyz[1], "point", path), Real(xyz[2], "point", path))
                    : throw new InvalidDataException($"{path}: a point entry has an invalid vertex.")).ToArray(),
            });
        }
        static bool IsHex(string s) => s.All(char.IsAsciiHexDigit);
    }

    /// <summary>
    /// A primitive's polygons. Where fan merging would not restore the stored polygons, the export records each one in
    /// the primitive's extras: a fan as its triangle count, any other polygon as its triangle count and corner list.
    /// When that record still describes the primitive's triangles (an editor that rebuilt the mesh drops or breaks it),
    /// it is followed; otherwise fans are merged.
    /// </summary>
    internal static List<int[]> Polygons(GltfPrimitive primitive, bool textured)
    {
        var indices = primitive.Indices;
        if ((primitive.Extras?[Key] as JsonObject)?["polygons"] is not JsonArray entries || entries.Count > indices.Count / 3) return MergeFans(primitive, textured);
        List<int[]> result = []; int t = 0, triangles = indices.Count / 3;
        foreach (var entry in entries)
        {
            if (entry is JsonObject listed)
            {
                long n = JsonData.Integer(listed["triangles"], -1);
                if (n < 1 || n > triangles - t || listed["corners"] is not JsonArray list || list.Count < 3 || list.Count > ModelBuilder.MaximumCorners) return MergeFans(primitive, textured);
                int[] corners = list.Select(c => (int)Math.Clamp(JsonData.Integer(c, -1), -1, int.MaxValue)).ToArray();
                if (corners.Any(c => c < 0 || c >= primitive.Positions.Count)) return MergeFans(primitive, textured);
                // Its triangles must lie on its corners.
                for (int k = 0; k < n; k++) for (int j = 0; j < 3; j++) if (Array.IndexOf(corners, indices[(t + k) * 3 + j]) < 0) return MergeFans(primitive, textured);
                result.Add(corners); t += (int)n;
                continue;
            }
            long count = JsonData.Integer(entry, -1);
            if (count < 1 || count > triangles - t) return MergeFans(primitive, textured);
            int a = indices[t * 3]; List<int> polygon = [a, indices[t * 3 + 1], indices[t * 3 + 2]]; bool fan = true;
            for (int k = 1; k < count && fan; k++)
            {
                int o = (t + k) * 3;
                if (indices[o] == a && indices[o + 1] == polygon[^1]) polygon.Add(indices[o + 2]); else fan = false;
            }
            if (fan) result.Add([.. polygon]);
            else for (int k = 0; k < count; k++) { int o = (t + k) * 3; result.Add([indices[o], indices[o + 1], indices[o + 2]]); }
            t += (int)count;
        }
        return t == triangles ? result : MergeFans(primitive, textured);
    }

    /// <summary>
    /// Polygons from a triangle list: consecutive triangles that continue a fan from the same first corner are joined
    /// while the polygon stays planar and convex and its texture mapping stays affine, which restores the polygons
    /// <see cref="Export"/> wrote as fans. Anything else stays a triangle, which the engine draws the same way.
    /// </summary>
    internal static List<int[]> MergeFans(GltfPrimitive primitive, bool textured)
    {
        List<int[]> polygons = []; List<int>? current = null;
        var p = primitive.Positions; var uv = primitive.TexCoords;
        for (int t = 0; t + 2 < primitive.Indices.Count; t += 3)
        {
            int a = primitive.Indices[t], b = primitive.Indices[t + 1], c = primitive.Indices[t + 2];
            if (current != null && a == current[0] && b == current[^1] && current.Count < ModelBuilder.SplitCorners && Extends(current, c)) { current.Add(c); continue; }
            if (current != null) polygons.Add([.. current]);
            current = [a, b, c];
        }
        if (current != null) polygons.Add([.. current]);
        return polygons;

        bool Extends(List<int> polygon, int c)
        {
            List<Vector3> points = [.. polygon.Select(i => p[i]), p[c]];
            if (!ModelBuilder.Coplanar(points)) return false;
            Vector3 normal = Vector3.Cross(p[polygon[1]] - p[polygon[0]], p[polygon[2]] - p[polygon[0]]);
            // A straight corner past the first would be dropped by AddPolygonEx, so the engine never stored one: those
            // triangles were separate polygons.
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p0 = points[i], p1 = points[(i + 1) % points.Count], p2 = points[(i + 2) % points.Count];
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p1), normal) < 0 || ((i + 1) % points.Count != 0 && ModelBuilder.Straight(p0, p1, p2))) return false;
            }
            if (!textured || uv.Count != p.Count) return true;
            // The engine extrapolates corners past the first triangle affinely; keep only fans whose mapping is affine.
            return ModelBuilder.Affine(points, [.. polygon.Select(i => uv[i]), uv[c]], ModelBuilder.AffineTolerance / 4);
        }
    }

    /// <summary>
    /// The engine material and polygon attributes of a glTF material. <c>StoresNormals</c> is whether its polygons keep
    /// normals: from its engine values when it has them (<c>normals</c>), otherwise null (use the primitive's normals).
    /// </summary>
    private static (WorldMaterial Material, int Priority, bool BackFace, uint Zone, bool? StoresNormals) ImportMaterial(GltfMaterial? source, string path, ImportContext context)
    {
        var extras = source?.Extras?[Key] as JsonObject;
        int priority = extras?["priority"] is { } p ? (int)Integer(p, "priority", path, int.MinValue, int.MaxValue) : 0;
        bool backface = extras?["backface"] is { } b ? Flag(b, "backface", path) : source?.DoubleSided ?? false;
        uint zone = extras?["zone"] is { } z ? Hex(z, "zone", path) : DefaultPolygonZone;
        bool? normals = extras == null ? null : extras["normals"] is { } n && Flag(n, "normals", path);
        WorldMaterial material = new();
        string? textureName = null;
        string? namedTexture = extras?["texture"] is { } t ? Text(t, "texture", path) : null;
        if (source?.ImageUri != null || namedTexture != null)
        {
            textureName = context.TextureName(source?.ImageUri ?? "", namedTexture, path);
            int addressing = (source?.ClampS == true ? 1 : 0) | (source?.ClampT == true ? 2 : 0);
            if (source?.ImageUri != null && context.TextureAddressing.TryAdd(textureName, addressing) is false && context.TextureAddressing[textureName] != addressing)
                context.Warnings.Add($"{path}: texture {textureName} is sampled with different edge modes; the pack keeps the first.");
        }
        else if (source?.EmbeddedImage == true)
            context.Warnings.Add($"{path}: material {source.Name} has an embedded image; texture packs are built from PNG files, so save the image as a PNG beside the model (for example with Blender's glTF Separate format). The surface is untextured.");
        uint opacity = extras?["opacity"] is { } o ? (uint)Integer(o, "opacity", path, 0, 255)
            : source != null && source.AlphaMode == "BLEND" && source.BaseColor.W < 1 ? (uint)Math.Clamp(MathF.Round(source.BaseColor.W * 255), 0, 255) : 0xFF;
        uint extraFlags = extras?["flags"] is { } f ? Hex(f, "flags", path) & ~0x1FFu : 0;
        material.Flags = (ushort)(opacity & 0xFF | extraFlags);
        if (textureName != null)
        {
            material.Texture = Texture(context, textureName);
            material.Color = new(255); material.PackedColor = 0x7FFF; material.Flags |= 0x100;
        }
        else
        {
            material.Color = extras?["color"] is JsonArray color && color.Count == 3 ? new(Real(color[0], "color", path), Real(color[1], "color", path), Real(color[2], "color", path))
                : source != null ? new(MathF.Round(source.BaseColor.X * 255), MathF.Round(source.BaseColor.Y * 255), MathF.Round(source.BaseColor.Z * 255)) : new(200);
            material.PackedColor = 0;
        }
        if (extras?["packedColor"] is { } packed) material.PackedColor = (ushort)Hex(packed, "packedColor", path);
        if (extras?["fields"] is JsonArray fields && fields.Count == 3) { material.Field14 = Real(fields[0], "fields", path); material.Field18 = Real(fields[1], "fields", path); material.Field1C = Real(fields[2], "fields", path); }
        material.Soil = extras?["soil"] is { } soil ? unchecked((uint)Integer(soil, "soil", path, int.MinValue, uint.MaxValue)) : 0;
        return (Shared(context, material), priority, backface, zone, normals);
    }

    /// <summary>The world's texture directory entry for a name, added on first use.</summary>
    public static WorldTexture Texture(GameZWorld world, string name)
    {
        var existing = world.Textures.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        WorldTexture texture = new(name.ToLowerInvariant()); world.Textures.Add(texture); return texture;
    }
    /// <summary>The texture entry for a name within a load, found through an index so a file with many textures stays linear.</summary>
    private static WorldTexture Texture(ImportContext context, string name)
    {
        if (context.TextureIndex == null)
        {
            context.TextureIndex = new(StringComparer.OrdinalIgnoreCase);
            foreach (var t in context.World.Textures) context.TextureIndex.TryAdd(t.Name, t);
        }
        if (context.TextureIndex.TryGetValue(name, out var existing)) return existing;
        return context.TextureIndex[name] = Texture(context.World, name);
    }
    /// <summary>
    /// Materials are shared by value, as the engine's FindOrClone shares identical slots: the first identical one is
    /// used. The index keeps a file with many primitives from comparing each against every material.
    /// </summary>
    private static WorldMaterial Shared(ImportContext context, WorldMaterial material)
    {
        if (context.MaterialIndex == null)
        {
            context.MaterialIndex = [];
            foreach (var m in context.World.Materials) context.MaterialIndex.TryAdd(MaterialKey.Of(m), m);
        }
        if (context.MaterialIndex.TryGetValue(MaterialKey.Of(material), out var existing)) return existing;
        context.World.Materials.Add(material); context.MaterialIndex[MaterialKey.Of(material)] = material;
        return material;
    }
    /// <summary>A material's values; adding 0 makes −0 equal 0, as the engine's comparison does (import values are finite).</summary>
    internal readonly record struct MaterialKey(ushort Flags, ushort PackedColor, float R, float G, float B, WorldTexture? Texture, float Field14, float Field18, float Field1C, uint Soil)
    {
        public static MaterialKey Of(WorldMaterial m) => new(m.Flags, m.PackedColor, m.Color.X + 0f, m.Color.Y + 0f, m.Color.Z + 0f, m.Texture, m.Field14 + 0f, m.Field18 + 0f, m.Field1C + 0f, m.Soil);
    }

    /// <summary>The flags a load root takes from its file (scene extras), or null for the loader's default.</summary>
    /// <summary>A glTF node's engine name: its recorded name, else its glTF name without an editor's copy suffix (".001").</summary>
    public static string EngineName(GltfNode node) =>
        node.Extras?[Key]?["name"] is JsonValue n && n.TryGetValue(out string? named) ? named : BlenderSuffix().Replace(node.Name, "");
    public static uint? RootFlags(GltfDocument doc) => (doc.SceneExtras?[Key] as JsonObject)?["rootFlags"] is { } flags ? Hex(flags, "rootFlags", "the scene") & CarriedFlags : null;

    // Engine values in extras. Editors may rewrite their types (Blender stores a list mixing whole and fractional numbers
    // as floats, so 1 comes back as 1.0), so whole numbers are accepted in either form; anything else is invalid data.

    private static InvalidDataException Invalid(string what, string path, JsonNode? node) =>
        new($"{path}: the engine value '{what}' is invalid ({node?.ToJsonString() ?? "null"}).");
    private static long Integer(JsonNode? node, string what, string path, long minimum = long.MinValue, long maximum = long.MaxValue)
    {
        if (node is JsonValue value)
        {
            long? whole = value.TryGetValue(out long a) ? a : value.TryGetValue(out int b) ? b : value.TryGetValue(out uint c) ? c : null;
            if (whole == null && (value.TryGetValue(out double d) ? d : value.TryGetValue(out float f) ? f : double.NaN) is var number && number == Math.Floor(number) && Math.Abs(number) < 9e15)
                whole = (long)number;
            if (whole is { } w && w >= minimum && w <= maximum) return w;
        }
        throw Invalid(what, path, node);
    }
    private static float Real(JsonNode? node, string what, string path)
    {
        if (node is JsonValue value)
        {
            // Parsed text converts to float directly (rounding once); values built in memory may hold other types.
            if (value.TryGetValue(out float single) && float.IsFinite(single)) return single;
            if (value.TryGetValue(out double number) && float.IsFinite((float)number)) return (float)number;
            if (value.TryGetValue(out long whole)) return whole;
            if (value.TryGetValue(out int small)) return small;
        }
        throw Invalid(what, path, node);
    }
    private static string Text(JsonNode? node, string what, string path) => node is JsonValue value && value.TryGetValue(out string? text) ? text : throw Invalid(what, path, node);
    /// <summary>A switch: true or false, or 1 or 0 (an editor's integer property).</summary>
    private static bool Flag(JsonNode? node, string what, string path) =>
        node is JsonValue value && value.TryGetValue(out bool flag) ? flag : Integer(node, what, path, 0, 1) == 1;
    private static uint Hex(JsonNode? node, string what, string path)
    {
        string text = Text(node, what, path);
        return uint.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint result) ? result : throw Invalid(what, path, node);
    }
}
