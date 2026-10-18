using System.Text.Json.Nodes;
using zStudio.Core.Formats;

namespace zStudio.Core.Worlds;

/// <summary>Projects evaluated source geometry into the shared scene without serializing a game archive.</summary>
public static class WorldScene
{
    public static GameScene Create(GameZWorld world, CancellationToken token = default) => Create(world, null, token);

    internal static GameScene Create(GameZWorld world, GameScene? geometry, CancellationToken token)
    {
        GameZWriter.Validate(world, token);
        var slots = GameZWriter.NodeSlots(world, token);
        var textures = Index(world.Textures); var materials = Index(world.Materials); var models = Index(world.Models);
        var references = GameZWriter.RefCounts(world);
        int Slot(WorldNode? node) => node == null ? -1 : slots.TryGetValue(node, out int slot) ? slot : throw new InvalidDataException($"Node {node.Name} is not in the world.");
        GameScene scene = new();
        if (geometry != null)
        { scene.Textures.AddRange(geometry.Textures); scene.Materials.AddRange(geometry.Materials); scene.Models.AddRange(geometry.Models); }
        else
        {
        foreach (var texture in world.Textures)
        {
            token.ThrowIfCancellationRequested();
            scene.Textures.Add(new() { ["name"] = texture.Name, ["state"] = texture.State,
                ["next_variant_index"] = texture.NextVariant == null ? null : JsonValue.Create(textures[texture.NextVariant]) });
        }
        foreach (var material in world.Materials)
        {
            token.ThrowIfCancellationRequested();
            int flags = material.Texture == null ? material.Flags & ~0x500 : material.Flags | 0x100;
            scene.Materials.Add(new() { ["alpha"] = flags & 255, ["flags"] = flags >> 8, ["rgb"] = material.PackedColor,
                ["color"] = new JsonObject { ["r"] = material.Color.X, ["g"] = material.Color.Y, ["b"] = material.Color.Z }, ["texture_index"] = material.Texture == null ? 0 : textures[material.Texture],
                ["field20"] = material.Field14, ["field24"] = material.Field18, ["field28"] = material.Field1C,
                ["soil"] = material.Soil, ["cycle_ptr"] = "0x00000000" });
        }
        foreach (var model in world.Models)
        {
            token.ThrowIfCancellationRequested();
            JsonArray lights = [];
            foreach (var point in model.Points)
            {
                token.ThrowIfCancellationRequested();
                var light = Decode(point.Record, "GAMEZ_POINT_LIGHT_LAYOUT");
                light["vertex_count"] = point.Vertices.Length; light["vertices"] = JsonData.Vectors(point.Vertices, token); lights.Add(light);
            }
            JsonObject metadata = new() { ["model_type"] = model.Mode, ["flags"] = model.Flags,
                ["parent_count"] = references.GetValueOrDefault(model), ["polygon_count"] = model.Polygons.Count,
                ["vertex_count"] = model.Vertices.Count, ["normal_count"] = model.Normals.Count, ["morph_count"] = model.Morphs.Count,
                ["light_count"] = model.Points.Count, ["morph_factor"] = model.MorphFactor, ["tex_scroll_u"] = model.ScrollU,
                ["tex_scroll_v"] = model.ScrollV, ["tex_scroll_frame"] = model.ScrollFrame,
                ["bbox_mid"] = JsonData.Vector(model.BoundsCentre), ["bbox_diag"] = model.BoundsRadius, ["lights"] = lights };
            List<Polygon> polygons = [];
            foreach (var polygon in model.Polygons)
            {
                token.ThrowIfCancellationRequested();
                uint flags = (polygon.Flags & ~0x2ffu) | (uint)polygon.Vertices.Length | (polygon.Normals.Length == 0 ? 0 : 0x200u);
                int material = polygon.Material == null ? -1 : materials[polygon.Material];
                polygons.Add(new(material, flags, [.. polygon.Vertices], [.. polygon.Normals], [.. polygon.Uvs],
                    new() { ["flags"] = flags, ["priority"] = polygon.Priority, ["material_index"] = material, ["zone_set"] = Zones(polygon.Zone) }));
            }
            scene.Models.Add(new(models[model], [.. model.Vertices], [.. model.Normals], [.. model.Morphs], [.. polygons], metadata));
        }
        }
        foreach (object entry in GameZWriter.SlotSequence(world))
        {
            token.ThrowIfCancellationRequested(); int index = scene.Nodes.Count;
            if (entry is not WorldNode node)
            {
                var info = GameZLayouts.For(15).Decode(((byte[])entry).AsMemory(0, 192), "GAMEZ_NODE_BASE_LAYOUT");
                info["node_class"] = "none";
                scene.Nodes.Add(new(index, info.Text("name"), "none", null, [], [], info, []));
                continue;
            }
            string kind = node.Class == WorldNodeClass.Plain ? "none" : node.Class.ToString().ToLowerInvariant();
            int[] parents = node.Parents.Select(Slot).ToArray(), children = node.Children.Select(Slot).ToArray();
            JsonObject data = node.Class is WorldNodeClass.Plain ? [] : node.Class is WorldNodeClass.Window
                ? GameZReader.ReadNodeData(new(node.Payload), kind, GameZLayouts.For(15))
                : Decode(node.Payload, "GAMEZ_" + node.Class.ToString().ToUpperInvariant() + "_LAYOUT");
            if (node.Class == WorldNodeClass.Camera)
            {
                data["world_index"] = node.CameraWorld == null ? null : JsonValue.Create(Slot(node.CameraWorld)); data["window_index"] = node.CameraWindow == null ? null : JsonValue.Create(Slot(node.CameraWindow));
                data["focus_node_xy"] = node.CameraHorizon == null ? null : JsonValue.Create(Slot(node.CameraHorizon)); data["focus_node_xz"] = node.CameraHorizonXZ == null ? null : JsonValue.Create(Slot(node.CameraHorizonXZ));
            }
            if (node.Class is WorldNodeClass.Light or WorldNodeClass.Sound)
            { data["attached_count"] = node.AttachedWorlds.Count; data["attached_indices"] = JsonData.Integers(node.AttachedWorlds.Select(Slot), token); }
            if (node.Class == WorldNodeClass.World)
            {
                data["light_count"] = node.WorldLights.Count; data["light_indices"] = JsonData.Integers(node.WorldLights.Select(Slot), token);
                data["sound_count"] = node.WorldSounds.Count; data["sound_indices"] = JsonData.Integers(node.WorldSounds.Select(Slot), token);
                int columns = data.Int("virt_partition_x_count"), rows = data.Int("virt_partition_z_count");
                if (columns < 0 || rows < 0 || (columns == 0) != (rows == 0) || (long)columns * rows != node.Areas.Count)
                    throw new InvalidDataException($"World {node.Name} has an inconsistent partition grid.");
                JsonArray partitions = [];
                for (int row = 0; row < rows; row++)
                {
                    JsonArray cells = [];
                    for (int column = 0; column < columns; column++)
                    {
                        token.ThrowIfCancellationRequested(); var area = node.Areas[row * columns + column];
                        var cell = Decode(area.Record, "GAMEZ_WORLD_PARTITION_LAYOUT");
                        cell["node_count"] = area.Nodes.Count; cell["node_indices"] = JsonData.Integers(area.Nodes.Select(Slot), token); cells.Add(cell);
                    }
                    partitions.Add(cells);
                }
                data["partitions"] = partitions;
            }
            int? model = node.Model == null ? null : models[node.Model];
            JsonObject metadata = new() { ["name"] = node.Name, ["flags"] = node.Flags, ["field040"] = node.AuxFlags,
                ["update_flags"] = node.BoundsFlags, ["zone_id"] = node.Zone, ["node_class"] = kind, ["model_index"] = model is int modelIndex ? JsonValue.Create(modelIndex) : null,
                ["action_priority"] = node.Priority, ["area_partition"] = new JsonObject { ["x"] = node.GridColumn, ["z"] = node.GridRow },
                ["parent_count"] = parents.Length, ["child_count"] = children.Length,
                ["parent_indices"] = JsonData.Integers(parents), ["child_indices"] = JsonData.Integers(children),
                ["node_bbox"] = Box(node.CachedBounds), ["model_bbox"] = Box(node.PrimaryBounds), ["child_bbox"] = Box(node.SecondaryBounds), ["data"] = data };
            scene.Nodes.Add(new(index, node.Name, kind, model, parents, children, metadata, data));
        }
        return scene;
    }

    private static JsonObject Decode(byte[] bytes, string layout) => FieldLayouts.Decode(bytes, FieldLayouts.Definitions["layouts"]![layout]!.AsArray());
    internal static JsonArray Zones(uint packed)
    {
        JsonArray result = [];
        for (int i = 0; i < Math.Min(packed & 255, 3); i++) result.Add((long)(sbyte)(packed >> (8 + i * 8)));
        return result;
    }
    private static JsonObject Box(WorldBox box) => new() { ["a"] = JsonData.Vector(box.Min), ["b"] = JsonData.Vector(box.Max) };
    private static Dictionary<T, int> Index<T>(IReadOnlyList<T> values) where T : class
    {
        Dictionary<T, int> result = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < values.Count; i++) result.TryAdd(values[i], i);
        return result;
    }
}
