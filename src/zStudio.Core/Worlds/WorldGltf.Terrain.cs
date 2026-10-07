using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;

namespace Recoil.Zbd.Core.Worlds;

public static partial class WorldGltf
{
    /// <summary>
    /// The pieces a terrain recipe compiles to, in place of the mission database root that names it
    /// (<c>extras.recoil.terrain</c>): its surfaces are read from their glTF files, cut and painted by the recipe (see
    /// <see cref="TerrainCompiler"/>), and each piece becomes a top-level node with identity transform and its own model.
    /// </summary>
    private static List<WorldNode> ImportTerrain(string uri, string from, ImportContext context)
        => ImportTerrain(uri, from, context, TerrainCompiler.MaximumCreatedCorners);

    internal static List<WorldNode> ImportTerrain(string uri, string from, ImportContext context, long maximumInputCorners)
    {
        long inputCorners = 0;
        var grid = context.Grid?.Invoke() ?? throw new InvalidDataException($"{from}: the terrain recipe {JsonData.ShownText(uri)} must be a root of the mission database (the load after GameGenSetWorld), whose world grid it is cut for.");
        var read = context.ReadFile ?? throw new InvalidDataException($"{from}: this load cannot read terrain recipes.");
        var (bytes, recipePath) = read(uri, from);
        var recipe = TerrainRecipe.Parse(bytes, recipePath);
        List<TerrainSurfaceGeometry> surfaces = [];
        List<TerrainMaterialInfo> infos = [];
        List<(WorldMaterial Material, int Priority, bool BackFace, bool Normals)> materials = [];
        Dictionary<(string, GltfMaterial?), int> indices = [];
        // Model values (lighting, scrolling, display mode) are a surface mesh's, and every piece of the surface takes them.
        List<(JsonObject? Values, float Morph, string Path)> models = [];
        foreach (var surface in recipe.Surfaces)
        {
            context.Token.ThrowIfCancellationRequested();
            var (doc, path) = context.Reference(surface.Model, recipePath);
            var matches = Placed(doc).Where(p => EngineName(p.Node) == surface.Node).Take(2).ToList();
            if (matches.Count != 1) throw new InvalidDataException($"{recipePath}: surface {surface.Id} names node {surface.Node}, which {path} has {(matches.Count == 0 ? "no" : "more than one")} of.");
            var (node, world) = matches[0];
            var mesh = node.Mesh ?? throw new InvalidDataException($"{recipePath}: surface {surface.Id} ({surface.Node} in {path}) has no mesh.");
            ValidateMesh(mesh, path);
            var values = mesh.Extras?[Key] as JsonObject;
            if (values?["points"] is JsonArray { Count: > 0 }) throw new InvalidDataException($"{recipePath}: surface {surface.Id} ({surface.Node} in {path}) has point entries (lens flares), which terrain pieces cannot share; keep it an object.");
            if (values?["mode"] is JsonValue mode && !(mode.TryGetValue(out double m) && m == 0)) throw new InvalidDataException($"{recipePath}: surface {surface.Id} ({surface.Node} in {path}) is a facade or point model (mode {JsonData.Shown(mode, asText: true)}); keep it an object.");
            if (mesh.Primitives.Any(p => p.Targets.Count > 0)) throw new InvalidDataException($"{recipePath}: surface {surface.Id} ({surface.Node} in {path}) has morph targets; terrain is static.");
            models.Add((values, mesh.Weights.Count > 0 ? mesh.Weights[0] : 0, path));
            if (!FiniteMatrix(world) || !Matrix4x4.Invert(world, out var inverse) || !FiniteMatrix(inverse))
                throw new InvalidDataException($"{recipePath}: surface {surface.Id} has a non-finite or singular derived transform.");
            var normalMatrix = Matrix4x4.Transpose(inverse);
            // A mirroring transform turns polygons over; reversed corners keep them facing as authored.
            bool mirrored = world.GetDeterminant() < 0;
            List<TerrainFace> faces = [];
            foreach (var primitive in mesh.Primitives)
            {
                // Charge the triangle stream and any extra recorded polygon corners before Polygons builds its lists.
                // Each surface is charged, even when several nodes share the same mesh.
                long corners = TerrainInputCornerBound(primitive);
                if (corners > maximumInputCorners - inputCorners)
                    throw new InvalidDataException($"{recipePath}: terrain input exceeds its {maximumInputCorners:N0}-corner geometry limit; reduce the surfaces or mesh detail.");
                inputCorners += corners;
                if (!indices.TryGetValue((path, primitive.Material), out int index))
                {
                    var (material, priority, backface, zone, stores) = ImportMaterial(primitive.Material, path, context);
                    indices[(path, primitive.Material)] = index = materials.Count;
                    materials.Add((material, priority, backface, stores ?? primitive.Normals.Count == primitive.Positions.Count));
                    infos.Add(new(zone));
                }
                bool normals = primitive.Normals.Count == primitive.Positions.Count, uvs = primitive.TexCoords.Count == primitive.Positions.Count;
                TerrainCorner Corner(int i, Vector3 face)
                {
                    var position = Vector3.Transform(primitive.Positions[i], world);
                    WorldNumbers.Vector(position);
                    var normal = normals ? Vector3.TransformNormal(primitive.Normals[i], normalMatrix) : face;
                    return new(position, TerrainNormal(normal, face), uvs ? primitive.TexCoords[i] : Vector2.Zero);
                }
                // The engine polygons the file records (fans and listed polygons), so uncut polygons stay as they were.
                int added = 0;
                foreach (var listed in Polygons(primitive, materials[index].Material.Texture != null, context.PolygonWork))
                {
                    var polygon = listed;
                    if ((++added & 4095) == 0) context.Token.ThrowIfCancellationRequested();
                    Vector3 newell = Vector3.Zero;
                    for (int i = 0; i < polygon.Length; i++)
                    {
                        var a = Vector3.Transform(primitive.Positions[polygon[i]], world); var b = Vector3.Transform(primitive.Positions[polygon[(i + 1) % polygon.Length]], world);
                        WorldNumbers.Vector(a); WorldNumbers.Vector(b);
                        newell += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
                    }
                    // The engine takes the plane and the fan from the first corner, so it stays first.
                    if (mirrored) { newell = -newell; polygon = [polygon[0], .. polygon[1..].Reverse()]; }
                    var face = TerrainNormal(newell, Vector3.UnitY);
                    faces.Add(new(index, [.. polygon.Select(i => Corner(i, face))]));
                }
            }
            surfaces.Add(new(surface, faces));
        }
        string label = Label(recipePath);
        var compiled = TerrainCompiler.Compile(label, recipe, surfaces, infos, grid, context.Token);
        foreach (string warning in compiled.Warnings) context.Warnings.Add($"{recipePath}: {warning}");
        List<WorldNode> nodes = [];
        foreach (var piece in compiled.Pieces)
        {
            context.Token.ThrowIfCancellationRequested();
            if (++context.Created + context.World.Nodes.Count > GameZWorld.MaximumNodeCapacity)
                throw new InvalidDataException($"{recipePath}: the terrain compiles to more pieces than a world holds ({GameZWorld.MaximumNodeCapacity:N0} nodes).");
            WorldNode node = new(piece.Name, WorldNodeClass.Object3D) { BoundsFlags = 4, Zone = piece.Zone };
            node.Flags = 0x0308001Cu & ~CarriedFlags | piece.CarriedFlags & CarriedFlags;
            node.SetPayloadInt(0, 0x28);
            node.SetPayloadFloat(0x24, 1); node.SetPayloadFloat(0x28, 1); node.SetPayloadFloat(0x2C, 1);
            node.SetPayloadFloat(0x30, 1); node.SetPayloadFloat(0x40, 1); node.SetPayloadFloat(0x50, 1);
            ModelBuilder builder = new();
            foreach (var polygon in piece.Polygons)
            {
                var source = materials[polygon.Material];
                var material = polygon.Soil is { } soil && soil != source.Material.Soil ? SoilVariant(context, source.Material, soil) : source.Material;
                bool textured = material.Texture != null;
                builder.Add(new([.. polygon.Corners.Select(c => c.Position)], textured ? [.. polygon.Corners.Select(c => c.Uv)] : [],
                    source.Normals ? [.. polygon.Corners.Select(c => c.Normal)] : [], [], material, polygon.Priority ?? source.Priority, source.BackFace, polygon.ZoneWord));
            }
            foreach (var warning in builder.Warnings.Distinct()) context.Warnings.Add($"{recipePath}: piece {piece.Name}: {warning}");
            node.Model = builder.Finish();
            var (modelValues, morph, modelPath) = models[piece.Surface];
            ApplyValues(node.Model, modelValues, morph, modelPath);
            context.AddModel(node.Model);
            context.TerrainPieceImported?.Invoke(node, recipePath, piece, recipe.Surfaces[piece.Surface].Id);
            nodes.Add(node);
        }
        return nodes;
    }

    private static bool FiniteMatrix(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    private static Vector3 TerrainNormal(Vector3 normal, Vector3 fallback)
    {
        WorldNumbers.Vector(normal);
        // Normalize in double precision: finite large components must not become a zero normal through length overflow.
        double length = Math.Sqrt((double)normal.X * normal.X + (double)normal.Y * normal.Y + (double)normal.Z * normal.Z);
        return length == 0 ? fallback : new((float)(normal.X / length), (float)(normal.Y / length), (float)(normal.Z / length));
    }

    private static long TerrainInputCornerBound(GltfPrimitive primitive)
    {
        long count = primitive.Indices.Count;
        if (primitive.Extras?[Key]?["polygons"] is not JsonArray entries || entries.Count > primitive.Indices.Count / 3) return count;
        foreach (var entry in entries)
            if (entry is JsonObject listed && listed["corners"] is JsonArray corners)
            {
                long triangles = JsonData.Integer(listed["triangles"], -1);
                if (triangles > 0 && triangles <= primitive.Indices.Count / 3)
                    count += Math.Max(0, corners.Count - 3 * triangles);
            }
        return count;
    }

    /// <summary>Every node of a document with its transform in the document's scene.</summary>
    private static IEnumerable<(GltfNode Node, Matrix4x4 World)> Placed(GltfDocument doc)
    {
        Stack<(GltfNode, Matrix4x4, int)> pending = new(doc.Roots.AsEnumerable().Reverse().Select(r => (r, Matrix4x4.Identity, 0)));
        while (pending.TryPop(out var item))
        {
            var (node, parent, depth) = item;
            if (depth > GltfDocument.MaximumDepth) throw new InvalidDataException("The glTF node hierarchy is too deep.");
            var world = (node.Matrix ?? Matrix4x4.Identity) * parent;
            yield return (node, world);
            for (int i = node.Children.Count - 1; i >= 0; i--) pending.Push((node.Children[i], world, depth + 1));
        }
    }
    /// <summary>The material with another soil, shared with an identical one the world already has.</summary>
    private static WorldMaterial SoilVariant(ImportContext context, WorldMaterial material, uint soil) => Shared(context, new()
    {
        Flags = material.Flags, PackedColor = material.PackedColor, Color = material.Color, Texture = material.Texture,
        Field14 = material.Field14, Field18 = material.Field18, Field1C = material.Field1C, Soil = soil,
    });
    /// <summary>A short name for a recipe's pieces: its file name without the extension, letters, digits and _ only.</summary>
    private static string Label(string recipePath)
    {
        string name = Path.GetFileName(recipePath);
        if (name.EndsWith(TerrainRecipe.Extension, StringComparison.OrdinalIgnoreCase)) name = name[..^TerrainRecipe.Extension.Length];
        string label = new([.. name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(12)]);
        return label.Length == 0 ? "terrain" : label;
    }
}
