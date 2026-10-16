using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;

namespace zStudio.Core.Worlds;

public static partial class WorldGltf
{
    internal readonly record struct NodeTransformChange(int Index, Matrix4x4 Before, Matrix4x4 After);

    /// <summary>Proves that the complete source changes only node TRS/matrices, using the importer codec.</summary>
    internal static IReadOnlyList<NodeTransformChange>? TransformChanges(byte[] before, byte[] after, CancellationToken token)
    {
        var oldJson = GltfDocument.ContainerJson(before, token, out var oldBinary);
        var newJson = GltfDocument.ContainerJson(after, token, out var newBinary);
        if (!oldBinary.SequenceEqual(newBinary)) return null;
        var oldRoot = JsonNode.Parse(oldJson, documentOptions: new() { MaxDepth = 64 }) as JsonObject;
        var newRoot = JsonNode.Parse(newJson, documentOptions: new() { MaxDepth = 64 }) as JsonObject;
        if (oldRoot == null || newRoot == null || oldRoot["nodes"] is not JsonArray oldNodes || newRoot["nodes"] is not JsonArray newNodes
            || oldNodes.Count != newNodes.Count || oldNodes.Count > GltfDocument.MaximumNodes) return null;
        oldRoot.Remove("nodes"); newRoot.Remove("nodes");
        if (!JsonNode.DeepEquals(oldRoot, newRoot)) return null;
        List<NodeTransformChange> changes = [];
        for (int i = 0; i < oldNodes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (JsonNode.DeepEquals(oldNodes[i], newNodes[i])) continue;
            if (oldNodes[i] is not JsonObject oldNode || newNodes[i] is not JsonObject newNode) return null;
            var from = GltfDocument.LocalTransform(oldNode, i) ?? Matrix4x4.Identity;
            var to = GltfDocument.LocalTransform(newNode, i) ?? Matrix4x4.Identity;
            foreach (string field in new[] { "matrix", "translation", "rotation", "scale" })
            { oldNode.Remove(field); newNode.Remove(field); }
            if (!JsonNode.DeepEquals(oldNode, newNode) || Mirrors(from) != Mirrors(to)) return null;
            changes.Add(new(i, from, to));
        }
        return changes;
    }
}
