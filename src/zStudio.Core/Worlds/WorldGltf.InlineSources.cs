using System.Text.Json.Nodes;
using zStudio.Core.Gltf;

namespace zStudio.Core.Worlds;

public static partial class WorldGltf
{
    internal const string InlineSourceField = "inlineSource";

    /// <summary>An embedded inferred load boundary, not an assertion about a lost original filename.</summary>
    internal static string? InlineSourceKey(GltfNode source, string path)
        => InlineSource(source, path)?.Id;

    private static (string Id, int Count)? InlineSource(GltfNode source, string path)
    {
        if (source.Extras?[Key]?[InlineSourceField] is not { } value) return null;
        if (value is not JsonObject item || item.Count != 2 || item["id"] is not { } idValue || item["count"] is not { } countValue)
            throw new InvalidDataException($"{path}: an inline source must contain an id and a child count.");
        string id = Text(idValue, "inline source id", path);
        if (id.Length is < 1 or > 128 || id.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new InvalidDataException($"{path}: an inline source id must be a nonempty local identity of at most 128 characters.");
        int count = checked((int)Integer(countValue, "inline source child count", path, 0, source.Children.Count));
        if (source.Extras?[Key]?["ref"] != null || source.Extras?[Key]?[ZoneReference] != null || source.Mesh != null)
            throw new InvalidDataException($"{path}: an inline source cannot also contain an external reference or a mesh.");
        return (id, count);
    }

    private static int ImportInlineSource(GltfNode source, WorldNode holder, string path, string reading, uint zone,
        ImportContext context, int depth, bool mirrored, BoundZones? zones, bool terrainGroups, Action<WorldNode> link)
    {
        if (InlineSource(source, path) is not { } inline) return 0;
        string key = Reading(reading, "inline:" + inline.Id);
        context.InlineSourceKeys.Add(holder, key);
        List<WorldNode> content = [];
        context.InlineSourceContents.Add(holder, content);
        Dictionary<int, WorldNode> instances = [];
        context.referencing.Add(holder);
        try
        {
            for (int i = 0; i < inline.Count; i++)
            {
                var child = ImportNode(source.Children[i], path, key, zone, context, instances, depth + 1, mirrored,
                    zones: zones, terrainGroups: terrainGroups);
                content.Add(child); link(child);
            }
        }
        finally { context.referencing.RemoveAt(context.referencing.Count - 1); }
        return inline.Count;
    }
}
