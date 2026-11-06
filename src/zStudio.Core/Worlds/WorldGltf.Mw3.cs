using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;

namespace zStudio.Core.Worlds;

public static partial class WorldGltf
{
    /// <summary>MW3's source profile keeps the engine polygon/corner records in the recipe and edits indexed positions
    /// in glTF. Changing triangulation or adding a transform is refused, rather than silently discarded.</summary>
    internal static (byte[] Json, byte[] Binary, int[] Triangles) ExportMw3Model(GameModel model, string binaryName, CancellationToken token)
    {
        GltfDocument document = new() { SceneExtras = new JsonObject { [Key] = new JsonObject { ["sourceFormat"] = 4, ["game"] = "mw3", ["model"] = model.Index } } };
        GltfPrimitive primitive = new(); primitive.Positions.AddRange(model.Vertices);
        foreach (var polygon in model.Polygons)
            for (int i = 1; i + 1 < polygon.Vertices.Length; i++) primitive.Indices.AddRange([polygon.Vertices[0], polygon.Vertices[i], polygon.Vertices[i + 1]]);
        // A point-only model still needs a valid triangle primitive for the shared glTF codec.
        if (primitive.Indices.Count == 0) throw new InvalidDataException("MW3 point-only geometry uses its typed position source.");
        GltfMesh mesh = new() { Name = $"MW3 model {model.Index}" }; mesh.Primitives.Add(primitive);
        document.Roots.Add(new() { Name = mesh.Name, Mesh = mesh }); var (json, binary) = document.Write(binaryName, token);
        var root = JsonNode.Parse(json)!; root["buffers"]![0]!["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(binary);
        return (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(root), [], primitive.Indices.ToArray());
    }

    internal static Vector3[] ReadMw3Model(byte[] json, Func<string, byte[]> resolve, int count, IReadOnlyList<int> triangles, CancellationToken token)
    {
        var document = GltfDocument.Read(json, resolve, token);
        if (document.SceneExtras?[Key]?["sourceFormat"]?.GetValue<int>() != 4 || document.SceneExtras?[Key]?["game"]?.GetValue<string>() != "mw3")
            throw new InvalidDataException("Expected an MW3 source-format-4 model.");
        var nodes = document.AllNodes().ToArray();
        if (nodes.Length != 1 || nodes[0].Weights.Count != 0 || nodes[0].Matrix is { } matrix && matrix != Matrix4x4.Identity || nodes[0].Mesh is not { Primitives.Count: 1 } mesh || mesh.Weights.Count != 0)
            throw new InvalidDataException("MW3 position sources require one mesh and no node transform. Transform and hierarchy authoring are not supported yet.");
        var primitive = mesh.Primitives[0];
        if (primitive.Positions.Count != count || !primitive.Indices.SequenceEqual(triangles) || primitive.Targets.Count != 0 || primitive.Normals.Count != 0 || primitive.TexCoords.Count != 0)
            throw new InvalidDataException("MW3 glTF position editing keeps vertex count and polygon topology; adding normals, UVs or morphs to the position mesh is not supported.");
        if (primitive.Material is { } material && (material.ImageUri != null || material.EmbeddedImage || material.BaseColor != Vector4.One || material.MetallicFactor != 0 || material.AlphaMode != "OPAQUE" || material.AlphaCutoff != 0.5f || material.DoubleSided))
            throw new InvalidDataException("MW3 material changes on the position mesh are not supported.");
        return primitive.Positions.ToArray();
    }
}
