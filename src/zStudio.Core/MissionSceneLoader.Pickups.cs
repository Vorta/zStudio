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

    internal static void PlacePickups(GameScene scene, List<int> sources, List<MissionActor> actors, HashSet<int> positioned,
        int worldRoot, string worldPath, int originalNodeCount, JsonNode? tree, MissionResourceSource resource, MissionLayoutSelection layout,
        Func<int, string, int> cloneTree, BoundedDiagnostics diagnostics, CancellationToken token, MissionPlacementState placement, MissionPlacementBudget budget)
    {
        int[] suffixes = new int[40];
        string? originalArchive = null; MissionResourceSource? normalizedSource = null;
        foreach (var node in scene.Nodes.Take(originalNodeCount))
        {
            budget.Name(node.Name);
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
                    new(originalArchive ??= budget.SourceIdentity(worldPath), -1, "GAMEZ", node.Index), "GameZ placed pickup");
            }
            catch (InvalidDataException ex) when (!budget.Exhausted) { diagnostics.Add($"Mission pickup node #{node.Index} '{node.Name}': {ex.Message}"); }
        }
        if (tree == null) { diagnostics.Add($"Mission pickups: {layout.PickupResource} is unavailable; pickup placements could not be recovered."); return; }
        var records = PickupRecords(tree);
        if (worldRoot < 0) { diagnostics.Add($"Mission pickups: missing world root; pickup instances could not be attached."); return; }
        for (int index = 0; index < records.Count; index++)
        {
            budget.Take();
            try
            {
                if (records[index]?["children"] is not JsonArray row || row.Count != 5 || row[0].Text("type") != "string")
                    throw new InvalidDataException("Expected type, amount, XYZ position, XYZ rotation and respawn delay.");
                string name = row[0].Text("value");
                budget.Name(name);
                var type = MissionPickupType.Catalog.FirstOrDefault(t => t.Name == name)
                    ?? throw new InvalidDataException($"Unknown pickup type '{JsonData.ShownText(name, 192)}'.");
                if (row[1].Text("type") != "int" || !int.TryParse(row[1]?["value"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
                    throw new InvalidDataException("Expected an integer pickup amount.");
                Vector3 position = Vector(row[2]), rotation = Vector(row[3]); float delay = Number(row[4]);
                var match = placement.Template(type.TemplateName);
                if (match.Count != 1) throw new InvalidDataException($"Missing or ambiguous template {type.TemplateName} ({match.Count} matches).");
                var template = scene.Nodes[match.Root];
                RequireBvol(template.Index);
                var scale = new Vector3(template.Data["scale"].Float("x", 1), template.Data["scale"].Float("y", 1), template.Data["scale"].Float("z", 1));
                var matrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(AnimationMath.FromEuler(rotation)) * Matrix4x4.CreateTranslation(position);
                if (!float.IsFinite(matrix.GetDeterminant())) throw new InvalidDataException("Nonfinite pickup template scale/rotation.");
                string instanceName;
                do { budget.Name(type.TemplateName); instanceName = type.TemplateName + (suffixes[type.Index]++).ToString("D2", CultureInfo.InvariantCulture); } while (!placement.UseName(instanceName));
                int root = cloneTree(template.Index, instanceName);
                SetPose(scene, root, matrix); HideBvol(root);
                scene.Nodes[root] = scene.Nodes[root] with { Parents = [worldRoot] };
                placement.Attach(root, aiv: false);
                normalizedSource ??= new(budget.SourceIdentity(resource.ArchivePath), resource.AssetIndex, budget.SourceIdentity(resource.ResourceName));
                budget.TextCopy(layout.PickupResource.Length + name.Length + 64L);
                Add(root, type, amount, position, rotation, delay,
                    new(normalizedSource.ArchivePath, normalizedSource.AssetIndex, normalizedSource.ResourceName, index),
                    $"{layout.PickupResource} #{index} · {layout.Difficulty} · {name}");
            }
            catch (InvalidDataException ex) when (!budget.Exhausted) { diagnostics.Add($"Mission pickup {resource.ResourceName} #{index} in {resource.ArchivePath}: {ex.Message}"); }
        }

        int RequireBvol(int root)
        {
            budget.Take(scene.Nodes[root].Children.Length);
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
            placement.Actor(root);
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
