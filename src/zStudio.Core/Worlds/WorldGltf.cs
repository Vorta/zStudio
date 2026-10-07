using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Formats;

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
    // Object3D transform bookkeeping is represented by the glTF transform. The remaining flags and +4..+0x14
    // appearance values are authored data, independent of the matrix and retained even when their effect is inactive.
    private const uint ObjectTransformFlags = 0x39;

    [GeneratedRegex(@"\.\d{3}\z")] private static partial Regex BlenderSuffix();

    // ---------------------------------------------------------------- export

    /// <summary>How an exporter names textures and refers to external parts.</summary>
    public sealed class ExportContext
    {
        /// <summary>The image URI (relative to the glTF file) and the clamp word for a texture.</summary>
        public required Func<WorldTexture, (string Uri, int Addressing)> Texture { get; init; }
        /// <summary>For a node that is an external reference, the referenced file's URI; its children from that file are then not written.</summary>
        public Func<WorldNode, string?> Reference { get; init; } = _ => null;
        /// <summary>
        /// For a reference, the children that come from the referenced file (null: all of them). The others are records of
        /// the file being written: a reference with records of its own (a mission database's part), written as its children.
        /// </summary>
        public Func<WorldNode, IReadOnlyCollection<WorldNode>?> Content { get; init; } = _ => null;
        /// <summary>Nodes that are groups of a mission database (written with <c>extras.recoil.group</c>; see <see cref="IsGroup"/>).</summary>
        public Func<WorldNode, bool> Group { get; init; } = _ => false;
        /// <summary>Clear the runtime state of point entries (elapsed time, packed state, heap words, flare runtime values) to compare content.</summary>
        public bool Canonical { get; init; }
        public CancellationToken Token { get; init; }
        private PolygonWorkBudget? polygonWork;
        internal PolygonWorkBudget PolygonWork => polygonWork ??= new(Token);
        internal Dictionary<WorldModel, GltfMesh> Meshes { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<SurfaceKey, GltfMaterial> Materials { get; } = [];
        internal HashSet<string> MaterialNames { get; } = new(StringComparer.Ordinal);
        internal Dictionary<WorldNode, IReadOnlyList<WorldNode>> ReferenceChildren { get; } = new(ReferenceEqualityComparer.Instance);
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
        ExportJsonBudget jsonBudget = new(roots.Count);
        int visits = 0;
        void Count(WorldNode node, bool identity = true)
        {
            if (++visits > MaximumExportedNodes) throw new InvalidDataException($"The model hierarchy expands to more than {MaximumExportedNodes} nodes.");
            if (!path.Add(node)) throw new InvalidDataException($"Node {node.Name} is its own ancestor.");
            if (path.Count > GltfDocument.MaximumDepth) throw new InvalidDataException($"The exported glTF hierarchy is deeper than {GltfDocument.MaximumDepth} levels.");
            // A small GameZ DAG can expand to hundreds of thousands of glTF copies. Charge the emitted metadata
            // at every occurrence before ExportNode builds any of those nodes or clones their engine extras.
            jsonBudget.Add(node, context);
            int seen = reached.GetValueOrDefault(node);
            if (identity) reached[node] = seen + 1;
            // The writer expands descendants on EVERY occurrence, so its preflight must count that same expansion.
            // Instance markers describe original graph edges, not the inherited copies below a shared ancestor.
            foreach (var child in OwnChildren(node, context)) Count(child, identity && seen == 0);
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
    /// <summary>
    /// Conservative size of the indented JSON Write emits, before building its DOM. Counts include escaped strings,
    /// per-occurrence node/model values and each unique mesh's primitive, accessor and material descriptions. Geometry
    /// samples live in the separate binary buffer; polygon/point records in extras live in JSON and count here.
    /// </summary>
    private sealed class ExportJsonBudget(int roots)
    {
        private long bytes = 512L + 20L * roots;
        private readonly HashSet<WorldModel> models = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SurfaceKey> materials = [];
        internal void Add(WorldNode node, ExportContext context)
        {
            // Includes every optional engine scalar, duplicate name/instance mark, enclosing properties/indentation,
            // six-byte JSON escaping, and the largest six-digit node indices accepted by the writer.
            Charge(768L + 12L * node.Name.Length + 20L * node.Children.Count);
            if (context.Reference(node) is { } reference) Charge(32L + 6L * reference.Length);
            if (node.Class == WorldNodeClass.Lod) Charge(768);
            else if (WorldUpdate.LocalMatrix(node) is { IsIdentity: false }) Charge(512);
            if (node.Model is not { } model) return;
            // Point-only models are copied into each node's extras. Charging these values at every occurrence also
            // safely covers models whose polygons all disappear as degenerate and therefore become point-only.
            Charge(256L + 400L * model.Points.Count);
            foreach (var point in model.Points) Charge(256L * point.Vertices.Length);
            if (!models.Add(model)) return;
            SurfaceKey? previous = null;
            foreach (var polygon in model.Polygons)
            {
                if (polygon.Material is not { } material) continue;
                // A recorded polygon may need an explicit corner list after triangulation.
                Charge(64L + 16L * polygon.Vertices.Length);
                SurfaceKey surface = new(material, polygon.Priority, (polygon.Flags & 0x100) != 0, polygon.Zone, polygon.Normals.Length > 0);
                if (surface != previous) { Charge(3200); previous = surface; }
                if (!materials.Add(surface)) continue;
                Charge(1536);
                if (material.Texture is { } texture)
                {
                    var (uri, _) = context.Texture(texture);
                    // URI escaping can expand each UTF-8 byte to %XX, then the JSON encoder can escape characters.
                    // A UTF-16 code unit takes at most nine ASCII URI characters (surrogate pairs take fewer each).
                    Charge(12L * texture.Name.Length + 9L * uri.Length);
                }
            }
        }
        private void Charge(long amount)
        {
            bytes += amount;
            if (bytes > GltfDocument.MaximumJsonBytes)
                throw new InvalidDataException("The model's expanded glTF source exceeds the 32 MiB JSON budget. Split the model into referenced files or reduce shared-node copies/engine metadata before exporting.");
        }
    }
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
        else
        {
            if (WorldUpdate.LocalMatrix(node) is { } matrix && !matrix.IsIdentity) result.Matrix = matrix;
            uint flags = unchecked((uint)node.PayloadInt(0)) & ~ObjectTransformFlags;
            if (flags != 0 || Enumerable.Range(1, 5).Any(i => node.PayloadFloat(i * 4) != 0))
                extras["appearance"] = new JsonObject
                {
                    ["flags"] = $"0x{flags:X8}", ["alphaScale"] = node.PayloadFloat(4),
                    ["color"] = new JsonArray(node.PayloadFloat(8), node.PayloadFloat(12), node.PayloadFloat(16)),
                    ["colorAlpha"] = node.PayloadFloat(20),
                };
        }
        if (node.Model != null)
        {
            // A glTF mesh needs a primitive and editors drop one without (Blender omits it), so a model without polygons
            // (a lens flare's points) keeps its values with each node that uses it.
            var mesh = ExportMesh(node.Model, context);
            if (mesh.Primitives.Count > 0) result.Mesh = mesh;
            else extras["model"] = mesh.Extras?[Key]?.DeepClone() ?? new JsonObject();
        }
        if (context.Group(node)) extras["group"] = true;
        if (context.Reference(node) is { } uri) extras["ref"] = uri;
        foreach (var child in OwnChildren(node, context)) result.Children.Add(ExportNode(child, zone, context, state));
        if (extras.Count > 0) result.Extras = new() { [Key] = extras };
        return result;
    }

    /// <summary>The children written in the file itself: all for a node, those not from the referenced file for a reference.</summary>
    private static IEnumerable<WorldNode> OwnChildren(WorldNode node, ExportContext context)
    {
        if (context.Reference(node) == null) return node.Children;
        if (context.ReferenceChildren.TryGetValue(node, out var children)) return children;
        var content = context.Content(node);
        // Snapshot once: a part may contain tens of thousands of children, and Count and emission both visit it.
        // Repeated linear membership scans would make even a reference with no emitted children quadratic.
        HashSet<WorldNode>? members = content == null ? null : new(content, ReferenceEqualityComparer.Instance);
        return context.ReferenceChildren[node] = members == null ? [] : node.Children.Where(child => !members.Contains(child)).ToArray();
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
            if (polygons.Any(p => p is JsonObject) || !MergeFans(target, textured, context.PolygonWork).Select(p => p.Length - 2).SequenceEqual(polygons.Select(p => p.GetValue<int>())))
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
    /// <summary>A point entry's runtime fields: elapsed time and packed state (+16..+27), packed colour and list pointer (+40..+47) and the flare's runtime values (+60..+75).</summary>
    internal static readonly (int Start, int Length)[] RuntimePointFields = [(16, 12), (40, 8), (60, 16)];
    /// <summary>A point entry without its <see cref="RuntimePointFields"/>.</summary>
    private static byte[] CanonicalPoint(byte[] record)
    {
        byte[] copy = (byte[])record.Clone();
        foreach (var (start, length) in RuntimePointFields) Array.Clear(copy, start, length);
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

    /// <summary>
    /// State shared by one load (a LoadGameGen call). The original loader read a referenced file once per cache, and
    /// every copy from that cache shares its models (see <see cref="OriginalLoader"/>): within one reading of a file, the
    /// references that name a file by the same text share one reading of it, with its models, and each reading reads the
    /// files it references afresh. A file named by another text (a second path), or read again by another file, has models
    /// of its own.
    /// </summary>
    public sealed class ImportContext
    {
        public required GameZWorld World { get; init; }
        /// <summary>Loads an external reference relative to the referencing file: the document and the path it came from.</summary>
        public required Func<string, string, (GltfDocument Document, string Path)> Reference { get; init; }
        /// <summary>Texture name for a material's image URI (relative to the file at the given path).</summary>
        public required Func<string, string?, string, string> TextureName { get; init; }
        public List<string> Warnings { get; } = [];
        /// <summary>
        /// Called for each node a glTF node becomes, with the file's path, the glTF node (a shared node once, from its first
        /// copy) and, for a shared node or a node inside one, its place there.
        /// </summary>
        public Action<WorldNode, string, GltfNode, InstancePlace?>? NodeImported { get; init; }
        /// <summary>While a referenced file is imported, the node that references it (the innermost, for nested references).</summary>
        public WorldNode? Referencing => referencing.Count > 0 ? referencing[^1] : null;
        internal readonly List<WorldNode> referencing = [];
        /// <summary>Cancels a load between nodes and between batches of polygons.</summary>
        public CancellationToken Token { get; init; }
        private PolygonWorkBudget? polygonWork;
        internal PolygonWorkBudget PolygonWork { get => polygonWork ??= new(Token); init => polygonWork = value; }
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
        /// <summary>
        /// Models by the reading of their file (see <see cref="Reading"/>) and mesh, and point-only models by the reading
        /// and node; readings compare ignoring case, as the loader's caches do.
        /// </summary>
        internal Dictionary<(string Reading, GltfMesh Mesh), Dictionary<float, WorldModel>> Models { get; } = new(ReadingComparer<GltfMesh>.Instance);
        internal Dictionary<(string Reading, GltfNode Node), WorldModel> ValueModels { get; } = new(ReadingComparer<GltfNode>.Instance);
        internal Dictionary<(string Path, GltfDocument Doc), bool> Loading { get; } = [];
        // One immutable parsed document can be imported by many reference paths. Validate its authored semantics once,
        // before the first import mutates the world's pools, rather than rechecking every expanded copy.
        internal HashSet<GltfDocument> Validated { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>Nodes this load created; with the world's, never more than a world can hold.</summary>
        internal int Created { get; set; }
        /// <summary>The world's materials and textures by value and name, built on first use (a load only adds to them).</summary>
        internal Dictionary<MaterialKey, WorldMaterial>? MaterialIndex { get; set; }
        internal Dictionary<string, WorldTexture>? TextureIndex { get; set; }
        private WorldGeometryBudget? geometry;
        internal void AddModel(WorldModel model)
        {
            if (geometry == null)
            {
                geometry = new();
                foreach (var existing in World.Models) geometry.Add(existing);
            }
            GameZLayouts.CheckEntries("model", (long)World.Models.Count + 1);
            geometry.Add(model);
            World.Models.Add(model);
        }
    }

    /// <summary>Engine nodes for a document's scene roots, loaded from <paramref name="path"/> under a parent with <paramref name="parentZone"/>.</summary>
    /// <remarks>Malformed engine values are reported as <see cref="InvalidDataException"/>.</remarks>
    public static List<WorldNode> Import(GltfDocument doc, string path, uint parentZone, ImportContext context) => Import(doc, path, path, parentZone, context, 0);

    /// <summary>Shared build/Blender preflight: refuse model semantics GameZ cannot represent before accepting sources.</summary>
    internal static void ValidateSupported(GltfDocument doc, string path)
    {
        _ = EngineValues(doc.SceneExtras, path);
        HashSet<GltfMesh> meshes = [];
        HashSet<GltfMaterial> materials = [];
        Dictionary<string, int> addressing = new(StringComparer.OrdinalIgnoreCase);
        foreach (var node in doc.AllNodes())
        {
            _ = ValidatedEngineName(node, path);
            if (EngineValues(node.Extras, path)?["class"] is { } kind && Text(kind, "class", path) == "lod"
                && node.Matrix is { IsIdentity: false })
                throw new InvalidDataException($"{path}: LOD node {JsonData.ShownText(EngineName(node))} has a transform the game cannot store. Move its objects or place the LOD below a transformed object instead.");
            _ = Appearance(node.Extras?[Key] as JsonObject, path);
            if (node.Mesh is { } mesh && meshes.Add(mesh))
            {
                ValidateMesh(mesh, path);
                foreach (var primitive in mesh.Primitives)
                {
                    var material = primitive.Material;
                    if (material == null || !materials.Add(material)) continue;
                    ValidateMaterial(material, path);
                    if (material.ImageUri == null) continue;
                    string name = material.Extras?[Key]?["texture"] is { } named ? Text(named, "texture", path) : Path.GetFileNameWithoutExtension(material.ImageUri);
                    int mode = (material.ClampS ? 1 : 0) | (material.ClampT ? 2 : 0);
                    if (!addressing.TryAdd(name, mode) && addressing[name] != mode)
                        throw new InvalidDataException($"{path}: texture {JsonData.ShownText(name)} is sampled with different edge modes; use one mode per texture or give the images distinct names.");
                }
            }
        }
    }

    /// <summary>Compatibility for old reconstructed point-only meshes, validated with the importer that preserves their records.</summary>
    internal static bool IsLegacyPointMesh(GltfMesh mesh)
    {
        if (mesh.Extras?[Key] is not JsonObject values || values["points"] is not JsonArray { Count: > 0 }) return false;
        WorldModel model = new();
        ApplyValues(model, values, 0, "Legacy point mesh");
        WorldNumbers.Model(model);
        return model.Points.Count > 0;
    }

    private static void ValidateMesh(GltfMesh mesh, string path)
    {
        _ = EngineValues(mesh.Extras, path);
        if (mesh.Weights.Count > 1 || mesh.Primitives.Any(p => p.Targets.Count > 1))
            throw new InvalidDataException($"{path}: mesh {JsonData.ShownText(mesh.Name)} has multiple morph targets; RECOIL stores one shape key per model. Export at most one shape key.");
        foreach (var primitive in mesh.Primitives)
        {
            _ = EngineValues(primitive.Extras, path);
            _ = EngineValues(primitive.Material?.Extras, path);
            if (primitive.Material is { } material && (material.ImageUri != null || material.EmbeddedImage || material.Extras?[Key]?["texture"] != null)
                && primitive.TexCoords.Count != primitive.Positions.Count)
            {
                ValidateMaterial(material, path);
                throw new InvalidDataException($"{path}: mesh {JsonData.ShownText(mesh.Name)} needs TEXCOORD_{primitive.TextureCoordinateSet}, which its material selects, with one texture coordinate per vertex. Export that UV set with the mesh.");
            }
        }
    }

    private static void ValidateMaterial(GltfMaterial? source, string path)
    {
        if (EngineValues(source?.Extras, path) is { } engine)
        {
            if (engine["flags"] is { } flags) _ = MaterialWord(flags, "flags", path);
            if (engine["packedColor"] is { } packed) _ = MaterialWord(packed, "packedColor", path);
            foreach (string field in new[] { "fields", "color" })
                if (engine.TryGetPropertyValue(field, out var value))
                {
                    if (value is not JsonArray { Count: 3 } vector) throw new InvalidDataException($"{path}: material {field} must be an array of exactly three finite numbers.");
                    foreach (var component in vector) _ = Real(component, field, path);
                }
        }
        if (source is { MetallicFactor: not 0 })
            throw new InvalidDataException($"{path}: the glTF material has metallicFactor {source.MetallicFactor} (1 when no material is assigned); assign a material with metallicFactor 0 for RECOIL.");
        if (source?.EmbeddedImage == true)
            throw new InvalidDataException($"{path}: material {JsonData.ShownText(source.Name)} has an embedded image; save it as an external PNG beside the model (for example with Blender's glTF Separate format).");
        if (source is { AlphaMode: "MASK" } && (source.ImageUri != null || source.EmbeddedImage || source.Extras?[Key]?["texture"] != null)
            && !(source.Extras?[Key]?["texture"] != null && source.AlphaCutoff == 0.5f &&
                (source.BaseColor.W == 1 || source.BaseColor.W == 0 && source.Extras?[Key]?["opacity"] != null)))
            throw new InvalidDataException($"{path}: textured MASK material {JsonData.ShownText(source.Name)} needs a cutoff that RECOIL cannot preserve. Bake the mask into a binary-alpha PNG and use BLEND. Reconstructed keyed-texture presentation keeps its recorded engine material.");
        if (source != null && (source.ImageUri != null || source.EmbeddedImage || source.Extras?[Key]?["texture"] != null) &&
            (source.BaseColor.X != 1 || source.BaseColor.Y != 1 || source.BaseColor.Z != 1))
            throw new InvalidDataException($"{path}: textured material {JsonData.ShownText(source.Name)} has a base-colour tint that RECOIL cannot preserve. Bake the colour into its PNG and export with a white RGB base colour.");
    }

    /// <summary>
    /// Which reading of a file a reference copies: the reading of the file that holds the reference, and the reference's
    /// text as written there (the key the original loader cached the file by).
    /// </summary>
    private static string Reading(string holder, string uri) => holder + "\u001F" + uri;

    /// <param name="reading">This reading of the file (see <see cref="ImportContext"/>): its models are its own.</param>
    private static List<WorldNode> Import(GltfDocument doc, string path, string reading, uint parentZone, ImportContext context, int depth)
    {
        if (!context.Validated.Contains(doc))
        {
            ValidateSupported(doc, path);
            // Ordinary source loading follows the original first-definition instance contract. Later records may
            // inherit other zones or omit the first record's children. Editor updates validate agreement separately.
            context.Validated.Add(doc);
        }
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
                    if (depth > 0) throw new InvalidDataException($"{path}: the terrain recipe {JsonData.Shown(recipe, asText: true)} must be a root of the mission database, not of a referenced file.");
                    roots.AddRange(ImportTerrain(Text(recipe, "terrain", path), path, context));
                }
                else roots.Add(ImportNode(root, path, reading, parentZone, context, instances, depth));
            }
            return roots;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or ArgumentException or NullReferenceException or IndexOutOfRangeException)
        { throw new InvalidDataException($"{path}: an engine value in the extras is malformed: {ex.Message}", ex); }
        finally { context.Loading.Remove((path, doc)); }
    }

    /// <summary>
    /// <paramref name="depth"/> counts levels across external references, which continue the hierarchy; <paramref name="place"/>
    /// is the node's place in a shared node of its file, if it is inside one.
    /// </summary>
    private static WorldNode ImportNode(GltfNode source, string path, string reading, uint parentZone, ImportContext context, Dictionary<int, WorldNode> instances, int depth, InstancePlace? place = null)
    {
        if (depth >= GltfDocument.MaximumDepth) throw new InvalidDataException($"{path}: the node hierarchy, with its external references, is deeper than {GltfDocument.MaximumDepth} levels.");
        context.Token.ThrowIfCancellationRequested();
        var extras = source.Extras?[Key] as JsonObject;
        if (extras?["terrain"] != null) throw new InvalidDataException($"{path}: node {JsonData.ShownText(source.Name)} names a terrain recipe but is not a root of the mission database.");
        // Later copies of a shared node are the same node under another parent.
        long? mark = extras?["instance"] is { } marker ? Integer(marker, "instance", path) : null;
        if (mark is < 1 or > int.MaxValue) throw new InvalidDataException($"{path}: node {JsonData.ShownText(source.Name)} has an invalid instance number.");
        int? instance = (int?)mark;
        if (instance is { } shared && instances.TryGetValue(shared, out var existing)) return existing;
        // References can repeat a file any number of times; a world holds a bounded number of nodes.
        if (++context.Created + context.World.Nodes.Count > GameZWorld.MaximumNodeCapacity)
            throw new InvalidDataException($"{path}: the model expands to more nodes than a world holds ({GameZWorld.MaximumNodeCapacity:N0}).");
        bool lod = extras?["class"] is { } kind && Text(kind, "class", path) == "lod";
        string name = ValidatedEngineName(source, path);
        WorldNode node = new(name, lod ? WorldNodeClass.Lod : WorldNodeClass.Object3D);
        if (instance is { } first) { instances[first] = node; place = new(first); }
        context.NodeImported?.Invoke(node, path, source, place);
        uint carried = extras?["flags"] is { } flags ? Hex(flags, "flags", path) & CarriedFlags : DefaultCarried;
        node.Flags = (lod ? 0x0108001Cu : 0x0308001Cu) & ~CarriedFlags | carried;
        node.BoundsFlags = 4;
        // A node's zone is the low byte of its zone word: zone states it (also over the word's), and it is what the node's
        // children inherit, so a word without a zone of its own passes its low byte on (see StatedZone).
        uint? word = extras?["zoneWord"] is { } w ? Hex(w, "zoneWord", path) : null;
        uint zone = extras?["zone"] is { } z ? (uint)Integer(z, "zone", path) & 0xFF : word is { } stated ? stated & 0xFF : parentZone;
        node.Zone = word is { } full ? full & ~0xFFu | zone : zone;
        if (lod)
        {
            var fields = extras?["lod"] is { } values ? values as JsonArray ?? throw new InvalidDataException($"{path}: node {JsonData.ShownText(name)} has an invalid lod record.") : [];
            for (int i = 0; i < 20 && i < fields.Count; i++)
                if (i is 0 or 12 or 17 or 18) node.SetPayloadInt(i * 4, (int)Integer(fields[i], "lod", path, int.MinValue, int.MaxValue));
                else node.SetPayloadFloat(i * 4, Real(fields[i], "lod", path));
        }
        else
        {
            var matrix = source.Matrix ?? Matrix4x4.Identity;
            float[] rows = [matrix.M11, matrix.M12, matrix.M13, matrix.M21, matrix.M22, matrix.M23, matrix.M31, matrix.M32, matrix.M33, matrix.M41, matrix.M42, matrix.M43];
            if (!rows.All(float.IsFinite)) throw new InvalidDataException($"{path}: node {JsonData.ShownText(name)} has a transform with a non-finite value.");
            node.SetPayloadInt(0, matrix.IsIdentity ? 0x28 : 0x30);
            node.SetPayloadFloat(0x24, 1); node.SetPayloadFloat(0x28, 1); node.SetPayloadFloat(0x2C, 1);
            for (int i = 0; i < 12; i++) node.SetPayloadFloat(0x30 + i * 4, rows[i]);
            if (Appearance(extras, path) is { } appearance)
            {
                node.SetPayloadInt(0, node.PayloadInt(0) | unchecked((int)appearance.Flags));
                node.SetPayloadFloat(4, appearance.AlphaScale);
                node.SetPayloadFloat(8, appearance.Color.X); node.SetPayloadFloat(12, appearance.Color.Y); node.SetPayloadFloat(16, appearance.Color.Z);
                node.SetPayloadFloat(20, appearance.ColorAlpha);
            }
        }
        if (source.Mesh != null) node.Model = ImportMesh(source.Mesh, path, reading, context, source.Weights.Count > 0 ? source.Weights[0] : null);
        else if (extras?["model"] is { } values) node.Model = ImportValues(values as JsonObject ?? throw new InvalidDataException($"{path}: node {JsonData.ShownText(name)} has an invalid model record."), source, path, reading, context);
        if (extras?["ref"] is { } referenceValue)
        {
            string uri = Text(referenceValue, "ref", path);
            var (doc, referencedPath) = context.Reference(uri, path);
            context.referencing.Add(node);
            try { foreach (var child in Import(doc, referencedPath, Reading(reading, uri), zone, context, depth + 1)) Link(node, child); }
            finally { context.referencing.RemoveAt(context.referencing.Count - 1); }
        }
        // Inside a shared node each child's place is its name and how many earlier siblings have it (see InstancePlace).
        Dictionary<string, int>? named = place == null ? null : new(StringComparer.Ordinal);
        for (int i = 0; i < source.Children.Count; i++)
        {
            InstancePlace? at = null;
            if (named != null)
            {
                string childName = EngineName(source.Children[i]);
                int occurrence = named.GetValueOrDefault(childName); named[childName] = occurrence + 1;
                at = new(place!.Number, place, i, childName, occurrence);
            }
            Link(node, ImportNode(source.Children[i], path, reading, zone, context, instances, depth + 1, at));
        }
        return node;
        static void Link(WorldNode parent, WorldNode child)
        {
            // A shared node found inside its own copy would make the graph cyclic.
            if (ReferenceEquals(parent, child) || Descends(parent, child, 0)) throw new InvalidDataException($"Node {child.Name} would become its own ancestor.");
            if (!parent.Children.Contains(child)) { parent.Children.Add(child); child.Parents.Add(parent); }
        }
        static bool Descends(WorldNode node, WorldNode ancestor, int depth) => depth <= 256 && node.Parents.Any(p => ReferenceEquals(p, ancestor) || Descends(p, ancestor, depth + 1));
    }

    private static WorldModel ImportMesh(GltfMesh mesh, string path, string reading, ImportContext context, float? nodeWeight)
    {
        ValidateMesh(mesh, path);
        float weight = nodeWeight ?? (mesh.Extras?[Key]?["morphFactor"] is { } factor ? Real(factor, "morphFactor", path) : mesh.Weights.Count > 0 ? mesh.Weights[0] : 0);
        if (!float.IsFinite(weight)) throw new InvalidDataException($"{path}: mesh {JsonData.ShownText(mesh.Name)} has a non-finite morph weight.");
        if (context.Models.TryGetValue((reading, mesh), out var variants) && variants.TryGetValue(weight, out var existing)) return existing;
        ModelBuilder builder = new();
        var model = builder.Model;
        ApplyValues(model, mesh.Extras?[Key] as JsonObject, mesh.Weights.Count > 0 ? mesh.Weights[0] : 0, path);
        model.MorphFactor = weight;
        foreach (var primitive in mesh.Primitives)
        {
            context.Token.ThrowIfCancellationRequested();
            var (material, priority, backface, zone, storesNormals) = ImportMaterial(primitive.Material, path, context);
            // Editors write normals for every surface (Blender does); a material with engine values says whether its
            // polygons stored them, so flat surfaces stay flat.
            bool textured = material.Texture != null, normals = storesNormals != false && primitive.Normals.Count == primitive.Positions.Count;
            var targets = primitive.Targets.Count > 0 && primitive.Targets[0].Count == primitive.Positions.Count ? primitive.Targets[0] : null;
            int added = 0;
            foreach (var corners in Polygons(primitive, textured, context.PolygonWork))
            {
                if ((++added & 1023) == 0) context.Token.ThrowIfCancellationRequested();
                Vector3[] points = corners.Select(i => primitive.Positions[i]).ToArray();
                PolygonInput input = new(points,
                    textured ? corners.Select(i => primitive.TexCoords[i]).ToArray() : [],
                    normals ? corners.Select(i => primitive.Normals[i]).ToArray() : [],
                    targets != null ? corners.Select(i => primitive.Positions[i] + targets[i]).ToArray() : [],
                    material, priority, backface, zone);
                builder.Add(input);
            }
        }
        foreach (var warning in builder.Warnings.Distinct()) context.Warnings.Add($"{path}: mesh {JsonData.ShownText(mesh.Name)}: {warning}");
        builder.Finish();
        context.AddModel(model);
        if (variants == null) context.Models[(reading, mesh)] = variants = [];
        variants[weight] = model;
        return model;
    }

    /// <summary>
    /// A model without polygons (point entries only) from a node's <c>model</c> values. Each node of a reading has its own,
    /// as the original loader read each object's geometry (1999 <c>redsprks.flt</c>'s four pieces have four models of the
    /// same values), and copies of one cache share them like meshes.
    /// </summary>
    private static WorldModel ImportValues(JsonObject values, GltfNode source, string path, string reading, ImportContext context)
    {
        if (context.ValueModels.TryGetValue((reading, source), out var existing)) return existing;
        ModelBuilder builder = new();
        ApplyValues(builder.Model, values, 0, path);
        var model = builder.Finish();
        context.AddModel(model);
        context.ValueModels[(reading, source)] = model;
        return model;
    }

    /// <summary>Keys of a reading and a glTF object: the reading ignoring case, the object by reference.</summary>
    private sealed class ReadingComparer<T> : IEqualityComparer<(string Reading, T Item)> where T : class
    {
        public static readonly ReadingComparer<T> Instance = new();
        public bool Equals((string Reading, T Item) x, (string Reading, T Item) y) => ReferenceEquals(x.Item, y.Item) && StringComparer.OrdinalIgnoreCase.Equals(x.Reading, y.Reading);
        public int GetHashCode((string Reading, T Item) key) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Reading), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Item));
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
    internal static List<int[]> Polygons(GltfPrimitive primitive, bool textured, PolygonWorkBudget? work = null)
    {
        work ??= new(); work.CheckCancellation();
        var indices = primitive.Indices;
        if ((primitive.Extras?[Key] as JsonObject)?["polygons"] is not JsonArray entries || entries.Count > indices.Count / 3) return MergeFans(primitive, textured, work);
        List<int[]> result = []; int t = 0, triangles = indices.Count / 3;
        foreach (var entry in entries)
        {
            work.Charge(1);
            if (entry is JsonObject listed)
            {
                long n = JsonData.Integer(listed["triangles"], -1);
                if (n < 1 || n > triangles - t || listed["corners"] is not JsonArray list || list.Count < 3 || list.Count > ModelBuilder.MaximumCorners) return MergeFans(primitive, textured, work);
                int[] corners = list.Select(c => (int)Math.Clamp(JsonData.Integer(c, -1), -1, int.MaxValue)).ToArray();
                if (corners.Any(c => c < 0 || c >= primitive.Positions.Count)) return MergeFans(primitive, textured, work);
                // Its triangles must lie on its corners.
                for (int k = 0; k < n; k++)
                {
                    work.Charge(3L * corners.Length);
                    for (int j = 0; j < 3; j++) if (Array.IndexOf(corners, indices[(t + k) * 3 + j]) < 0) return MergeFans(primitive, textured, work);
                }
                result.Add(corners); t += (int)n;
                continue;
            }
            long count = JsonData.Integer(entry, -1);
            if (count < 1 || count > triangles - t) return MergeFans(primitive, textured, work);
            int a = indices[t * 3]; List<int> polygon = [a, indices[t * 3 + 1], indices[t * 3 + 2]]; bool fan = true;
            for (int k = 1; k < count && fan; k++)
            {
                work.Charge(1);
                int o = (t + k) * 3;
                if (indices[o] == a && indices[o + 1] == polygon[^1]) polygon.Add(indices[o + 2]); else fan = false;
            }
            if (fan) result.Add([.. polygon]);
            else for (int k = 0; k < count; k++) { work.Charge(1); int o = (t + k) * 3; result.Add([indices[o], indices[o + 1], indices[o + 2]]); }
            t += (int)count;
        }
        return t == triangles ? result : MergeFans(primitive, textured, work);
    }

    /// <summary>
    /// Polygons from a triangle list: consecutive triangles that continue a fan from the same first corner are joined
    /// while the polygon stays planar and convex and its texture mapping stays affine, which restores the polygons
    /// <see cref="Export"/> wrote as fans. Anything else stays a triangle, which the engine draws the same way.
    /// </summary>
    internal static List<int[]> MergeFans(GltfPrimitive primitive, bool textured, PolygonWorkBudget? work = null)
    {
        work ??= new(); work.CheckCancellation();
        List<int[]> polygons = []; List<int>? current = null;
        var p = primitive.Positions; var uv = primitive.TexCoords;
        for (int t = 0; t + 2 < primitive.Indices.Count; t += 3)
        {
            work.Charge(1);
            int a = primitive.Indices[t], b = primitive.Indices[t + 1], c = primitive.Indices[t + 2];
            if (current != null && a == current[0] && b == current[^1] && current.Count < ModelBuilder.SplitCorners && Extends(current, c)) { current.Add(c); continue; }
            if (current != null) polygons.Add([.. current]);
            current = [a, b, c];
        }
        if (current != null) polygons.Add([.. current]);
        return polygons;

        bool Extends(List<int> polygon, int c)
        {
            work.Charge(3L * (polygon.Count + 1));
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
            return ModelBuilder.Affine(points, [.. polygon.Select(i => uv[i]), uv[c]], ModelBuilder.AffineTolerance / 4, work);
        }
    }

    /// <summary>
    /// The engine material and polygon attributes of a glTF material. <c>StoresNormals</c> is whether its polygons keep
    /// normals: from its engine values when it has them (<c>normals</c>), otherwise null (use the primitive's normals).
    /// </summary>
    private static (WorldMaterial Material, int Priority, bool BackFace, uint Zone, bool? StoresNormals) ImportMaterial(GltfMaterial? source, string path, ImportContext context)
    {
        ValidateMaterial(source, path);
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
                throw new InvalidDataException($"{path}: texture {JsonData.ShownText(textureName)} is sampled with different edge modes; use one mode per texture or give the images distinct names.");
        }
        uint opacity = extras?["opacity"] is { } o ? (uint)Integer(o, "opacity", path, 0, 255)
            : source is { AlphaMode: "MASK" } && source.ImageUri == null && !source.EmbeddedImage && namedTexture == null ? (source.BaseColor.W < source.AlphaCutoff ? 0u : 255u)
            : source != null && source.AlphaMode == "BLEND" && source.BaseColor.W < 1 ? (uint)Math.Clamp(MathF.Round(source.BaseColor.W * 255), 0, 255) : 0xFF;
        uint extraFlags = extras?["flags"] is { } f ? MaterialWord(f, "flags", path) & ~0x1FFu : 0;
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
        if (extras?["packedColor"] is { } packed) material.PackedColor = MaterialWord(packed, "packedColor", path);
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

    /// <summary>A glTF node's engine name: its recorded name, else its glTF name without an editor's copy suffix (".001").</summary>
    public static string EngineName(GltfNode node) =>
        (node.Extras?[Key] as JsonObject)?["name"] is JsonValue n && n.TryGetValue(out string? named) ? named : BlenderSuffix().Replace(node.Name, "");
    private static string ValidatedEngineName(GltfNode node, string path)
    {
        string name;
        if (EngineValues(node.Extras, path)?.TryGetPropertyValue("name", out var authored) == true)
            name = Text(authored, "name", path);
        else
        {
            // At most the four-character Blender suffix can disappear; reject before regex/string copies.
            if (node.Name.Length > 39) throw InvalidName();
            name = BlenderSuffix().Replace(node.Name, "");
        }
        if (name.Length > 35 || name.Any(c => c == 0 || c > 255)) throw InvalidName();
        return name;
        InvalidDataException InvalidName() => new($"{path}: node {JsonData.ShownText(node.Name)} needs an engine name of at most 35 Latin-1 characters without NUL; shorten or rename it before importing.");
    }

    private static (uint Flags, float AlphaScale, Vector3 Color, float ColorAlpha)? Appearance(JsonObject? extras, string path)
    {
        if (extras?.TryGetPropertyValue("appearance", out var value) != true) return null;
        if (value is not JsonObject appearance || extras["class"] is { } kind && Text(kind, "class", path) == "lod")
            throw new InvalidDataException($"{path}: appearance must be an Object3D appearance object, not a LOD value.");
        uint flags = appearance["flags"] is { } f ? Hex(f, "appearance flags", path) : 0;
        if ((flags & ObjectTransformFlags) != 0) throw new InvalidDataException($"{path}: appearance flags cannot contain Object3D transform bookkeeping bits; use the node's glTF transform.");
        Vector3 color = Vector3.Zero;
        if (appearance.TryGetPropertyValue("color", out var c))
        {
            if (c is not JsonArray { Count: 3 } xyz) throw new InvalidDataException($"{path}: appearance color must contain three finite numbers.");
            color = new(Real(xyz[0], "appearance color", path), Real(xyz[1], "appearance color", path), Real(xyz[2], "appearance color", path));
        }
        return (flags, appearance["alphaScale"] is { } scale ? Real(scale, "appearance alphaScale", path) : 0,
            color, appearance["colorAlpha"] is { } alpha ? Real(alpha, "appearance colorAlpha", path) : 0);
    }
    private static JsonObject? EngineValues(JsonObject? extras, string path)
    {
        if (extras?.TryGetPropertyValue(Key, out var values) != true) return null;
        return values as JsonObject ?? throw new InvalidDataException($"{path}: extras.{Key} must be an object when present.");
    }
    /// <summary>
    /// Whether a node is a group of a mission database (<c>extras.recoil.group</c>): the original database's group records,
    /// which the build creates with the database and deletes with it (<c>DeleteTree %dbName%</c>), so that the objects below
    /// them join the world. A group has no geometry and no transform of its own.
    /// </summary>
    public static bool IsGroup(GltfNode node, string path) => (node.Extras?[Key] as JsonObject)?["group"] is { } group && Flag(group, "group", path);
    /// <summary>The flags a load root takes from its file (scene extras), or null for the loader's default.</summary>
    public static uint? RootFlags(GltfDocument doc) => (doc.SceneExtras?[Key] as JsonObject)?["rootFlags"] is { } flags ? Hex(flags, "rootFlags", "the scene") & CarriedFlags : null;
    /// <summary>
    /// The zone a node's engine values (<c>extras.recoil</c>) state, which its children inherit: its <c>zone</c>, else its
    /// <c>zoneWord</c>'s low byte. Null when they state none (the node takes its parent's) or hold a value import refuses.
    /// </summary>
    public static uint? StatedZone(JsonNode? engine)
    {
        if (engine is not JsonObject values) return null;
        if (values["zone"] is { } zone)
            return GltfInteger.TryInt64(zone, out long value) ? (uint)value & 0xFF : null;
        return values["zoneWord"] is JsonValue word && word.TryGetValue(out string? text)
            && uint.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint full) ? full & 0xFF : null;
    }

    // Engine values in extras. Editors may rewrite their types (Blender stores a list mixing whole and fractional numbers
    // as floats, so 1 comes back as 1.0), so whole numbers are accepted in either form; anything else is invalid data.

    /// <summary>The refusal of an engine value, which shows only a preview of it: a value can be as large as the extras may be.</summary>
    private static InvalidDataException Invalid(string what, string path, JsonNode? node) =>
        new($"{path}: the engine value '{what}' is invalid ({JsonData.Shown(node)}).");
    private static ushort MaterialWord(JsonNode value, string what, string path)
    {
        uint number = Hex(value, what, path);
        if (number > ushort.MaxValue) throw new InvalidDataException($"{path}: the engine value '{what}' must fit an unsigned 16-bit material field ({JsonData.Shown(value)}).");
        return (ushort)number;
    }
    private static long Integer(JsonNode? node, string what, string path, long minimum = long.MinValue, long maximum = long.MaxValue)
    {
        if (GltfInteger.TryInt64(node, out long whole) && whole >= minimum && whole <= maximum) return whole;
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
