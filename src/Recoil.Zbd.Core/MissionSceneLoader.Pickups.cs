using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Core;

public static partial class MissionSceneLoader
{
    // Pickup::InitAndLoadPuppySpawns 0x41DE70, SpawnAt 0x41DA20 and
    // AssignBvolGroupAndId 0x41DB60. These are placement lists, not name/value pairs.
    private static JsonArray PickupRecords(JsonNode? tree)
    {
        if (tree == null) return [];
        if (tree["children"] is not JsonArray root || root.Count != 1 || root[0]?["children"] is not JsonArray records)
            throw new InvalidDataException("Expected a root containing one pickup placement list.");
        return records;
    }

    private static void PlacePickups(GameScene scene, List<int> sources, List<MissionActor> actors, HashSet<int> positioned,
        int worldRoot, string worldPath, int originalNodeCount, JsonNode? tree, MissionResourceSource resource, MissionLayoutSelection layout,
        Func<int, string, int> cloneTree, List<string> diagnostics, CancellationToken token)
    {
        int[] suffixes = new int[40];
        var usedNames = scene.Nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var node in scene.Nodes.Take(originalNodeCount))
        {
            token.ThrowIfCancellationRequested();
            if (node.Class != "object3d" || node.Name.Length <= 5 || !node.Name.StartsWith("pu", StringComparison.Ordinal)
                || !int.TryParse(node.Name.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id / 100 >= 40) continue;
            int typeIndex = id / 100; suffixes[typeIndex] = Math.Max(suffixes[typeIndex], id % 100 + 1);
            try
            {
                HideBvol(node.Index);
                var type = MissionPickupType.Catalog[typeIndex];
                var pose = SceneBuilder.LocalTransform(node);
                var rotation = new Vector3(node.Data["rotate"].Float("x"), node.Data["rotate"].Float("y"), node.Data["rotate"].Float("z"));
                Add(node.Index, type, type.DefaultAmount, pose.Translation, rotation, 0,
                    new(worldPath.ToUpperInvariant(), -1, "GAMEZ", node.Index), "GameZ placed pickup");
            }
            catch (InvalidDataException ex) { diagnostics.Add($"Mission pickup node #{node.Index} '{node.Name}': {ex.Message}"); }
        }
        if (tree == null) { diagnostics.Add($"Mission pickups: {layout.PickupResource} is unavailable; pickup placements could not be recovered."); return; }
        var records = PickupRecords(tree);
        if (worldRoot < 0) { diagnostics.Add("Mission pickups: missing world root; pickup instances could not be attached."); return; }
        for (int index = 0; index < records.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (records[index]?["children"] is not JsonArray row || row.Count != 5 || row[0].Text("type") != "string")
                    throw new InvalidDataException("Expected type, amount, XYZ position, XYZ rotation and respawn delay.");
                string name = row[0].Text("value");
                var type = MissionPickupType.Catalog.FirstOrDefault(t => t.Name == name)
                    ?? throw new InvalidDataException($"Unknown pickup type '{name}'.");
                if (row[1].Text("type") != "int" || !int.TryParse(row[1]?["value"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
                    throw new InvalidDataException("Expected an integer pickup amount.");
                Vector3 position = Vector(row[2]), rotation = Vector(row[3]); float delay = Number(row[4]);
                var templates = scene.Nodes.Take(originalNodeCount).Where(n => n.Class == "object3d" && n.Name == type.TemplateName).ToArray();
                if (templates.Length != 1) throw new InvalidDataException($"Missing or ambiguous template {type.TemplateName} ({templates.Length} matches).");
                var template = templates[0];
                RequireBvol(template.Index);
                var scale = new Vector3(template.Data["scale"].Float("x", 1), template.Data["scale"].Float("y", 1), template.Data["scale"].Float("z", 1));
                var matrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(AnimationMath.FromEuler(rotation)) * Matrix4x4.CreateTranslation(position);
                if (!float.IsFinite(matrix.GetDeterminant())) throw new InvalidDataException("Nonfinite pickup template scale/rotation.");
                string instanceName;
                do { instanceName = type.TemplateName + (suffixes[type.Index]++).ToString("D2", CultureInfo.InvariantCulture); } while (!usedNames.Add(instanceName));
                int root = cloneTree(template.Index, instanceName);
                SetPose(scene, root, matrix); HideBvol(root);
                scene.Nodes[root] = scene.Nodes[root] with { Parents = [worldRoot] };
                scene.Nodes[worldRoot] = scene.Nodes[worldRoot] with { Children = [.. scene.Nodes[worldRoot].Children, root] };
                Add(root, type, amount, position, rotation, delay,
                    new(resource.ArchivePath.ToUpperInvariant(), resource.AssetIndex, resource.ResourceName.ToUpperInvariant(), index),
                    $"{layout.PickupResource} #{index} · {layout.Difficulty} · {name}");
            }
            catch (InvalidDataException ex) { diagnostics.Add($"Mission pickup {resource.ResourceName} #{index} in {resource.ArchivePath}: {ex.Message}"); }
        }

        int RequireBvol(int root)
        {
            int child = scene.Nodes[root].Children.FirstOrDefault(c => c >= 0 && c < scene.Nodes.Count && scene.Nodes[c].Name == "bvol", -1);
            return child >= 0 ? child : throw new InvalidDataException("Missing required bvol child.");
        }
        void HideBvol(int root)
        {
            int bvol = RequireBvol(root);
            scene.Nodes[bvol].Metadata["flags"] = scene.Nodes[bvol].Metadata.UInt("flags") & ~4u;
            scene.Nodes[root].Metadata["flags"] = (scene.Nodes[root].Metadata.UInt("flags") & ~8u) | 0x20u;
        }
        void Add(int root, MissionPickupType type, int amount, Vector3 position, Vector3 rotation, float delay, MissionPickupSource source, string label)
        {
            var pickup = new MissionPickup(type.Index, type.Name, amount, amount == 0 ? type.DefaultAmount : amount, position, rotation, delay, source);
            positioned.Add(root); actors.Add(new(root, sources[root], scene.Nodes[root].Name, label, pickup));
            var metadata = scene.Nodes[root].Metadata;
            metadata["preview_pickup_type"] = type.Name; metadata["preview_pickup_amount"] = pickup.EffectiveAmount;
            metadata["preview_pickup_respawn_delay"] = delay; metadata["preview_placement_source"] = label;
        }
        static Vector3 Vector(JsonNode? node)
        {
            if (node?["children"] is not JsonArray { Count: 3 } values) throw new InvalidDataException("Expected three pickup position/rotation components.");
            return new(Number(values[0]), Number(values[1]), Number(values[2]));
        }
    }
}
