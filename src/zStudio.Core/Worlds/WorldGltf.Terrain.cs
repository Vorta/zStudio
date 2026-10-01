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
    {
        var grid = context.Grid?.Invoke() ?? throw new InvalidDataException($"{from}: the terrain recipe {uri} must be a root of the mission database (the load after GameGenSetWorld), whose world grid it is cut for.");
        var read = context.ReadFile ?? throw new InvalidDataException($"{from}: this load cannot read terrain recipes.");
        var (bytes, recipePath) = read(uri, from);
        var recipe = TerrainRecipe.Parse(bytes, recipePath);
        List<TerrainSurfaceGeometry> surfaces = [];
        List<TerrainMaterialInfo> infos = [];
        List<(WorldMaterial Material, int Priority, bool BackFace, bool Normals)> materials = [];
        Dictionary<(string, GltfMaterial?), int> indices = [];
        foreach (var surface in recipe.Surfaces)
        {
            context.Token.ThrowIfCancellationRequested();
            var (doc, path) = context.Reference(surface.Model, recipePath);
            var matches = Placed(doc).Where(p => (p.Node.Extras?[Key]?["name"] is JsonValue n && n.TryGetValue(out string? named) ? named : BlenderSuffix().Replace(p.Node.Name, "")) == surface.Node).Take(2).ToList();
            if (matches.Count != 1) throw new InvalidDataException($"{recipePath}: surface {surface.Id} names node {surface.Node}, which {path} has {(matches.Count == 0 ? "no" : "more than one")} of.");
            var (node, world) = matches[0];
            var mesh = node.Mesh ?? throw new InvalidDataException($"{recipePath}: surface {surface.Id} ({surface.Node} in {path}) has no mesh.");
            Matrix4x4.Invert(world, out var inverse);
            var normalMatrix = Matrix4x4.Transpose(inverse);
            List<TerrainFace> faces = [];
            foreach (var primitive in mesh.Primitives)
            {
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
                    var normal = normals ? Vector3.TransformNormal(primitive.Normals[i], normalMatrix) : face;
                    return new(position, normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : face, uvs ? primitive.TexCoords[i] : Vector2.Zero);
                }
                // The engine polygons the file records (fans and listed polygons), so uncut polygons stay as they were.
                int added = 0;
                foreach (var polygon in Polygons(primitive, materials[index].Material.Texture != null))
                {
                    if ((++added & 4095) == 0) context.Token.ThrowIfCancellationRequested();
                    Vector3 newell = Vector3.Zero;
                    for (int i = 0; i < polygon.Length; i++)
                    {
                        var a = Vector3.Transform(primitive.Positions[polygon[i]], world); var b = Vector3.Transform(primitive.Positions[polygon[(i + 1) % polygon.Length]], world);
                        newell += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
                    }
                    var face = newell.LengthSquared() > 0 ? Vector3.Normalize(newell) : Vector3.UnitY;
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
            context.World.Models.Add(node.Model);
            context.TerrainPieceImported?.Invoke(node, recipePath, piece, recipe.Surfaces[piece.Surface].Id);
            nodes.Add(node);
        }
        return nodes;
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
