using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Conversion repeats each selected piece's primitives, even when its input mesh is shared. Reserve the complete
/// expanded output before cloning metadata, collecting primitives or asking the glTF writer to allocate buffers.
/// </summary>
internal sealed class TerrainConversionSerializationBudget(TerrainConversionSerializationBudget.Limits limits, CancellationToken token)
{
    internal sealed record Limits(long BinaryBytes = GltfDocument.MaximumBufferBytes,
        long JsonBytes = GltfDocument.MaximumJsonBytes, long Components = GltfDocument.MaximumDecodedElements,
        long Primitives = GltfDocument.MaximumPrimitives, long Accessors = GltfDocument.MaximumEntries,
        long Targets = GltfDocument.MaximumPrimitives)
    {
        internal static Limits Default { get; } = new();
    }

    private sealed record MeshSize(long Binary, long Components, long Json, long Primitives, long Accessors, long Targets);
    private readonly Dictionary<GltfMesh, MeshSize> meshes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<GltfMaterial> materials = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JsonNode, long> metadata = new(ReferenceEqualityComparer.Instance);
    private long binary, components, json, primitives, accessors, targets;
    private bool neutralMaterial;

    internal static void Check(TerrainConversionPlan plan, IReadOnlyDictionary<int, GltfNode> nodes, Limits limits, CancellationToken token)
    {
        if (plan.Groups.Count > TerrainRecipe.MaximumSurfaces)
            throw new InvalidDataException("Terrain conversion exceeds the recipe's surface limit.");
        TerrainConversionSerializationBudget budget = new(limits, token);
        // Root, scene, buffer URI, indices and surrounding indentation. URI escaping uses at most nine bytes per
        // UTF-16 unit; the binary basename is shorter than the planned surface path.
        budget.Json(2048L + 18L * plan.Surfaces.Length);
        foreach (var group in plan.Groups)
        {
            token.ThrowIfCancellationRequested();
            budget.Json(1024L + 12L * group.Id.Length + budget.Metadata(group.ModelValues) + budget.Metadata(group.Appearance));
            foreach (int index in group.Nodes)
            {
                token.ThrowIfCancellationRequested();
                if (!nodes.TryGetValue(index, out var node) || node.Mesh == null)
                    throw new InvalidDataException($"{plan.Database} changed since the conversion was planned.");
                budget.Mesh(node.Mesh);
            }
        }
    }

    private void Mesh(GltfMesh mesh)
    {
        if (!meshes.TryGetValue(mesh, out var size))
        {
            long meshBinary = 0, meshComponents = 0, meshJson = 0, meshPrimitives = 0, meshAccessors = 0, meshTargets = 0;
            foreach (var p in mesh.Primitives)
            {
                token.ThrowIfCancellationRequested();
                // Match GltfDocument.Write: empty primitives do not appear in the emitted mesh.
                if (p.Positions.Count == 0 || p.Indices.Count == 0) continue;
                long attributes = 3L * p.Positions.Count;
                int count = 2; // POSITION and indices.
                if (p.Normals.Count == p.Positions.Count) { attributes += 3L * p.Normals.Count; count++; }
                if (p.TexCoords.Count == p.Positions.Count) { attributes += 2L * p.TexCoords.Count; count++; }
                foreach (var target in p.Targets) { attributes += 3L * target.Count; count++; }
                meshComponents += attributes + p.Indices.Count;
                // Every view can need at most three alignment bytes. Indices use the writer's vertex-count rule.
                meshBinary += 4 * attributes + (p.Positions.Count > 65535 ? 4L : 2L) * p.Indices.Count + 3L * count;
                meshAccessors += count;
                meshTargets += p.Targets.Count;
                meshPrimitives++;
                meshJson += 512L + 768L * count + Metadata(p.Extras);
                Check(meshBinary, limits.BinaryBytes, GltfDocument.MaximumBufferBytes, "binary");
                Check(meshComponents, limits.Components, GltfDocument.MaximumDecodedElements, "decoded component");
                Check(meshAccessors, limits.Accessors, GltfDocument.MaximumEntries, "accessor");
                Check(meshPrimitives, limits.Primitives, GltfDocument.MaximumPrimitives, "primitive");
                Check(meshTargets, limits.Targets, GltfDocument.MaximumPrimitives, "morph target");
                Check(meshJson, limits.JsonBytes, GltfDocument.MaximumJsonBytes, "JSON");
                Material(p.Material);
            }
            meshes.Add(mesh, size = new(meshBinary, meshComponents, meshJson, meshPrimitives, meshAccessors, meshTargets));
        }
        // The input facts are cached; their expanded geometry and descriptions are charged at every occurrence.
        Check(binary += size.Binary, limits.BinaryBytes, GltfDocument.MaximumBufferBytes, "binary");
        Check(components += size.Components, limits.Components, GltfDocument.MaximumDecodedElements, "decoded component");
        Check(primitives += size.Primitives, limits.Primitives, GltfDocument.MaximumPrimitives, "primitive");
        Check(accessors += size.Accessors, limits.Accessors, GltfDocument.MaximumEntries, "accessor");
        Check(targets += size.Targets, limits.Targets, GltfDocument.MaximumPrimitives, "morph target");
        Json(size.Json);
    }

    private void Material(GltfMaterial? material)
    {
        if (material == null)
        {
            if (!neutralMaterial) { Json(1536); neutralMaterial = true; }
            return;
        }
        if (!materials.Add(material)) return;
        // The writer shares material identity and image URIs, so charging an image per material is conservative.
        Json(1536L + 6L * material.Name.Length + 18L * (material.ImageUri?.Length ?? 0) + Metadata(material.Extras));
    }

    private void Json(long count) => Check(json += count, limits.JsonBytes, GltfDocument.MaximumJsonBytes, "JSON");

    private long Metadata(JsonNode? node)
    {
        if (node == null) return 0;
        if (metadata.TryGetValue(node, out long known)) return known;
        long bytes = Measure(node, 0);
        metadata.Add(node, bytes);
        return bytes;
    }

    private long Measure(JsonNode? node, int depth)
    {
        token.ThrowIfCancellationRequested();
        if (depth > GltfDocument.MaximumDepth) throw Refusal("metadata depth");
        // Reserve whitespace at the writer's output nesting as well as punctuation; string keys/values use the
        // worst JSON escaping. Raw scalar spans avoid serializing or decoding a huge number/string to measure it.
        long bytes = 64L + 4L * depth;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    bytes += 6L * key.Length + Measure(value, depth + 1);
                    Check(bytes, limits.JsonBytes, GltfDocument.MaximumJsonBytes, "JSON");
                }
                break;
            case JsonArray array:
                foreach (var value in array)
                {
                    bytes += Measure(value, depth + 1);
                    Check(bytes, limits.JsonBytes, GltfDocument.MaximumJsonBytes, "JSON");
                }
                break;
            case JsonValue value when value.TryGetValue(out JsonElement element):
                bytes += 6L * JsonMarshal.GetRawUtf8Value(element).Length;
                break;
            case JsonValue value when value.TryGetValue(out string? text):
                bytes += 6L * (text?.Length ?? 0);
                break;
            case JsonValue:
                bytes += 128; // Programmatically constructed numeric/boolean scalar.
                break;
        }
        Check(bytes, limits.JsonBytes, GltfDocument.MaximumJsonBytes, "JSON");
        return bytes;
    }

    private static void Check(long value, long limit, long readerLimit, string kind)
    {
        if (value < 0 || value > Math.Min(limit, readerLimit)) throw Refusal(kind);
    }
    private static InvalidDataException Refusal(string kind) => new($"Terrain conversion exceeds its expanded {kind} serialization budget; convert fewer pieces or remove unused mesh data and metadata before retrying.");
}
