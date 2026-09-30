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
        internal Dictionary<WorldModel, GltfMesh> Meshes { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<SurfaceKey, GltfMaterial> Materials { get; } = [];
        internal HashSet<string> MaterialNames { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>A glTF document whose scene roots are <paramref name="roots"/>; <paramref name="parentZone"/> is the zone they would inherit.</summary>
    public static GltfDocument Export(IReadOnlyList<WorldNode> roots, uint parentZone, ExportContext context)
    {
        GltfDocument doc = new();
        var duplicates = new HashSet<string>(Collect(roots, context).GroupBy(n => n.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.Ordinal);
        foreach (var root in roots) doc.Roots.Add(ExportNode(root, parentZone, context, duplicates));
        return doc;
    }
    private static IEnumerable<WorldNode> Collect(IEnumerable<WorldNode> roots, ExportContext context)
    {
        foreach (var n in roots)
        {
            yield return n;
            if (context.Reference(n) == null) foreach (var c in Collect(n.Children, context)) yield return c;
        }
    }

    private static GltfNode ExportNode(WorldNode node, uint parentZone, ExportContext context, HashSet<string> duplicates)
    {
        if (node.Class is not (WorldNodeClass.Object3D or WorldNodeClass.Lod))
            throw new InvalidDataException($"Node {node.Name} is a {node.Class} node; model files hold object3d and lod nodes.");
        GltfNode result = new() { Name = node.Name };
        JsonObject extras = [];
        if (duplicates.Contains(node.Name)) extras["name"] = node.Name;
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
        if (node.Model != null) result.Mesh = ExportMesh(node.Model, context);
        if (context.Reference(node) is { } uri) extras["ref"] = uri;
        else foreach (var child in node.Children) result.Children.Add(ExportNode(child, zone, context, duplicates));
        if (extras.Count > 0) result.Extras = new() { [Key] = extras };
        return result;
    }

    private static GltfMesh ExportMesh(WorldModel model, ExportContext context)
    {
        if (context.Meshes.TryGetValue(model, out var existing)) return existing;
        GltfMesh mesh = new() { Name = $"model{context.Meshes.Count}" };
        GltfPrimitive? primitive = null; SurfaceKey? key = null;
        Dictionary<(int V, int N, float U, float W), int> corners = [];
        foreach (var polygon in model.Polygons)
        {
            if (polygon.Material == null) continue;
            SurfaceKey k = new(polygon.Material, polygon.Priority, (polygon.Flags & 0x100) != 0, polygon.Zone, polygon.Normals.Length > 0);
            // Consecutive polygons with the same surface share a primitive, keeping the stored polygon order.
            if (primitive == null || key != k)
            {
                primitive = new() { Material = ExportMaterial(k, context) }; mesh.Primitives.Add(primitive); key = k; corners.Clear();
                if (model.Morphs.Count > 0) primitive.Targets.Add([]);
            }
            int[] index = new int[polygon.Vertices.Length];
            for (int i = 0; i < index.Length; i++)
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
            foreach (var (a, b, c) in Triangulate(polygon.Vertices.Select(v => model.Vertices[v]).ToArray())) primitive.Indices.AddRange([index[a], index[b], index[c]]);
        }
        if (model.Morphs.Count > 0) mesh.Weights.Add(model.MorphFactor);
        JsonObject extras = [];
        if (model.Mode != 0) extras["mode"] = (int)model.Mode;
        if (model.Flags != DefaultModelFlags) extras["flags"] = (int)model.Flags;
        if (model.ScrollU != 0 || model.ScrollV != 0 || model.ScrollFrame != 0) extras["scroll"] = new JsonArray(model.ScrollU, model.ScrollV, (long)model.ScrollFrame);
        if (model.MorphFactor != 0) extras["morphFactor"] = model.MorphFactor;
        if (model.Points.Count > 0)
            extras["points"] = new JsonArray(model.Points.Select(p => (JsonNode)new JsonObject
            {
                ["record"] = Convert.ToHexStringLower(p.Record),
                ["vertices"] = new JsonArray(p.Vertices.Select(v => (JsonNode)new JsonArray(v.X, v.Y, v.Z)).ToArray()),
            }).ToArray());
        if (extras.Count > 0) mesh.Extras = new() { [Key] = extras };
        context.Meshes[model] = mesh;
        return mesh;
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
        /// <summary>Texture files the load referenced, by texture name.</summary>
        public Dictionary<string, string> TextureFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<(string Path, GltfMesh Mesh), WorldModel> Models { get; } = [];
        internal Dictionary<(string Path, GltfDocument Doc), bool> Loading { get; } = [];
    }

    /// <summary>Engine nodes for a document's scene roots, loaded from <paramref name="path"/> under a parent with <paramref name="parentZone"/>.</summary>
    public static List<WorldNode> Import(GltfDocument doc, string path, uint parentZone, ImportContext context)
    {
        if (!context.Loading.TryAdd((path, doc), true)) throw new InvalidDataException($"{path} references itself.");
        try { return doc.Roots.Select(r => ImportNode(r, path, parentZone, context, 0)).ToList(); }
        finally { context.Loading.Remove((path, doc)); }
    }

    private static WorldNode ImportNode(GltfNode source, string path, uint parentZone, ImportContext context, int depth)
    {
        if (depth > 256) throw new InvalidDataException("The node hierarchy is too deep.");
        var extras = source.Extras?[Key] as JsonObject;
        bool lod = extras?["class"]?.GetValue<string>() == "lod";
        string name = extras?["name"]?.GetValue<string>() ?? BlenderSuffix().Replace(source.Name, "");
        WorldNode node = new(name, lod ? WorldNodeClass.Lod : WorldNodeClass.Object3D);
        uint carried = extras?["flags"] is { } flags ? ParseHex(flags) & CarriedFlags : DefaultCarried;
        node.Flags = (lod ? 0x0108001Cu : 0x0308001Cu) & ~CarriedFlags | carried;
        node.BoundsFlags = 4;
        uint zone = extras?["zone"] is { } z ? (uint)z.GetValue<int>() & 0xFF : parentZone;
        node.Zone = extras?["zoneWord"] is { } word ? ParseHex(word) : zone;
        if (lod)
        {
            var fields = extras?["lod"] as JsonArray ?? [];
            for (int i = 0; i < 20 && i < fields.Count; i++)
                if (i is 0 or 12 or 17 or 18) node.SetPayloadInt(i * 4, fields[i]!.GetValue<int>()); else node.SetPayloadFloat(i * 4, fields[i]!.GetValue<float>());
        }
        else
        {
            var matrix = source.Matrix ?? Matrix4x4.Identity;
            node.SetPayloadInt(0, matrix.IsIdentity ? 0x28 : 0x30);
            node.SetPayloadFloat(0x24, 1); node.SetPayloadFloat(0x28, 1); node.SetPayloadFloat(0x2C, 1);
            float[] rows = [matrix.M11, matrix.M12, matrix.M13, matrix.M21, matrix.M22, matrix.M23, matrix.M31, matrix.M32, matrix.M33, matrix.M41, matrix.M42, matrix.M43];
            for (int i = 0; i < 12; i++) node.SetPayloadFloat(0x30 + i * 4, rows[i]);
        }
        if (source.Mesh != null) node.Model = ImportMesh(source.Mesh, path, context);
        if (extras?["ref"]?.GetValue<string>() is { } reference)
        {
            var (doc, referencedPath) = context.Reference(reference, path);
            foreach (var child in Import(doc, referencedPath, zone, context)) Link(node, child);
        }
        foreach (var child in source.Children) Link(node, ImportNode(child, path, zone, context, depth + 1));
        return node;
        static void Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); }
    }

    private static WorldModel ImportMesh(GltfMesh mesh, string path, ImportContext context)
    {
        if (context.Models.TryGetValue((path, mesh), out var existing)) return existing;
        var extras = mesh.Extras?[Key] as JsonObject;
        ModelBuilder builder = new();
        var model = builder.Model;
        model.Mode = (uint)(extras?["mode"]?.GetValue<int>() ?? 0);
        model.Flags = (uint)(extras?["flags"]?.GetValue<int>() ?? (int)DefaultModelFlags);
        if (extras?["scroll"] is JsonArray scroll && scroll.Count == 3) { model.ScrollU = scroll[0]!.GetValue<float>(); model.ScrollV = scroll[1]!.GetValue<float>(); model.ScrollFrame = scroll[2]!.GetValue<uint>(); }
        model.MorphFactor = extras?["morphFactor"]?.GetValue<float>() ?? (mesh.Weights.Count > 0 ? mesh.Weights[0] : 0);
        foreach (var point in extras?["points"] as JsonArray ?? [])
        {
            byte[] record = Convert.FromHexString(point!["record"]!.GetValue<string>());
            if (record.Length != 76) throw new InvalidDataException("A point entry record has 76 bytes.");
            model.Points.Add(new() { Record = record, Vertices = (point["vertices"] as JsonArray ?? []).Select(v => new Vector3(v![0]!.GetValue<float>(), v[1]!.GetValue<float>(), v[2]!.GetValue<float>())).ToArray() });
        }
        foreach (var primitive in mesh.Primitives)
        {
            var (material, priority, backface, zone) = ImportMaterial(primitive.Material, path, context);
            bool textured = material.Texture != null, normals = primitive.Normals.Count == primitive.Positions.Count;
            if (textured && primitive.TexCoords.Count != primitive.Positions.Count)
            {
                context.Warnings.Add($"{path}: mesh {mesh.Name} uses texture {material.Texture!.Name} without texture coordinates; they were set to zero.");
            }
            var targets = primitive.Targets.Count > 0 && primitive.Targets[0].Count == primitive.Positions.Count ? primitive.Targets[0] : null;
            foreach (var corners in MergeFans(primitive, textured))
            {
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
            for (int i = 0; i < points.Count; i++)
                if (Vector3.Dot(Vector3.Cross(points[(i + 1) % points.Count] - points[i], points[(i + 2) % points.Count] - points[(i + 1) % points.Count]), normal) < 0) return false;
            if (!textured || uv.Count != p.Count) return true;
            // The engine extrapolates corners past the first triangle affinely; keep only fans whose mapping is affine.
            return ModelBuilder.Affine(points, [.. polygon.Select(i => uv[i]), uv[c]], ModelBuilder.AffineTolerance / 4);
        }
    }

    private static (WorldMaterial Material, int Priority, bool BackFace, uint Zone) ImportMaterial(GltfMaterial? source, string path, ImportContext context)
    {
        var extras = source?.Extras?[Key] as JsonObject;
        int priority = extras?["priority"]?.GetValue<int>() ?? 0;
        bool backface = extras?["backface"]?.GetValue<bool>() ?? source?.DoubleSided ?? false;
        uint zone = extras?["zone"] is { } z ? ParseHex(z) : DefaultPolygonZone;
        WorldMaterial material = new();
        string? textureName = null;
        if (source?.ImageUri != null || extras?["texture"] != null)
            textureName = context.TextureName(source?.ImageUri ?? "", extras?["texture"]?.GetValue<string>(), path);
        uint opacity = (uint)(extras?["opacity"]?.GetValue<int>() ?? (source != null && source.AlphaMode == "BLEND" && source.BaseColor.W < 1 ? (int)MathF.Round(source.BaseColor.W * 255) : 0xFF));
        uint extraFlags = extras?["flags"] is { } f ? ParseHex(f) & ~0x1FFu : 0;
        material.Flags = (ushort)(opacity & 0xFF | extraFlags);
        if (textureName != null)
        {
            material.Texture = Texture(context.World, textureName);
            material.Color = new(255); material.PackedColor = 0x7FFF; material.Flags |= 0x100;
        }
        else
        {
            material.Color = extras?["color"] is JsonArray color && color.Count == 3 ? new(color[0]!.GetValue<float>(), color[1]!.GetValue<float>(), color[2]!.GetValue<float>())
                : source != null ? new(MathF.Round(source.BaseColor.X * 255), MathF.Round(source.BaseColor.Y * 255), MathF.Round(source.BaseColor.Z * 255)) : new(200);
            material.PackedColor = 0;
        }
        if (extras?["packedColor"] is { } packed) material.PackedColor = (ushort)ParseHex(packed);
        if (extras?["fields"] is JsonArray fields && fields.Count == 3) { material.Field14 = fields[0]!.GetValue<float>(); material.Field18 = fields[1]!.GetValue<float>(); material.Field1C = fields[2]!.GetValue<float>(); }
        material.Soil = (uint)(extras?["soil"]?.GetValue<int>() ?? 0);
        return (Shared(context.World, material), priority, backface, zone);
    }

    /// <summary>The world's texture directory entry for a name, added on first use.</summary>
    public static WorldTexture Texture(GameZWorld world, string name)
    {
        var existing = world.Textures.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        WorldTexture texture = new(name.ToLowerInvariant()); world.Textures.Add(texture); return texture;
    }
    /// <summary>Materials are shared by value, as the engine's FindOrClone shares identical slots.</summary>
    private static WorldMaterial Shared(GameZWorld world, WorldMaterial material)
    {
        foreach (var m in world.Materials)
            if (m.Flags == material.Flags && m.PackedColor == material.PackedColor && m.Color == material.Color && ReferenceEquals(m.Texture, material.Texture)
                && m.Field14 == material.Field14 && m.Field18 == material.Field18 && m.Field1C == material.Field1C && m.Soil == material.Soil) return m;
        world.Materials.Add(material); return material;
    }

    private static uint ParseHex(JsonNode node)
    {
        string text = node.GetValue<string>();
        return uint.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
