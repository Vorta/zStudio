using System.Security.Cryptography;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;

internal static class DifficultyCheck
{
    public static async Task<int> RunAsync(string[] roots)
    {
        try
        {
            int layouts = 0;
            foreach (string rootArg in roots)
            {
                string root = Path.GetFullPath(rootArg); using AssetResolver resolver = new(root);
                int[][] counts = [[80,84,87], [106,108,111], [111,115,120], [80,85,88], [72,75,77], [144,144,144]];
                int[][] pickupCounts = [[64,64,64], [56,53,56], [49,49,49], [54,54,54], [37,37,37], [84,83,81], [16,16,16], [10,10,10], [46,46,46], [39,39,39], [16,16,16], [19,19,19], [54,54,54]];
                foreach (string directory in Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "gamez.zbd"))).Order(StringComparer.Ordinal))
                {
                    if (!int.TryParse(Path.GetFileName(directory).AsSpan(1), out int number)) continue;
                    var world = await resolver.OpenCachedAsync(Path.Combine(directory, "gamez.zbd"), default);
                    byte[] hash = SHA256.HashData(world.Bytes.Span); string[] nodes = world.Scene!.Nodes.Select(n => n.Data.ToJsonString() + n.Metadata.ToJsonString()).ToArray();
                    var missions = new Dictionary<MissionDifficulty, MissionSceneContext>();
                    foreach (var difficulty in Enum.GetValues<MissionDifficulty>())
                    {
                        var mission = await MissionSceneLoader.LoadAsync(world, resolver, difficulty: difficulty); missions[difficulty] = mission;
                        var actors = mission.Actors.Where(a => a.PlacementSource.StartsWith("aiv", StringComparison.Ordinal)).ToArray();
                        int expected = number <= 6 ? counts[number - 1][(int)difficulty] : 1;
                        Require(actors.Length == expected, $"{directory} {difficulty}: expected {expected} AIV actors, got {actors.Length}");
                        // m2 Easy repeats the identical bft_00 record; the engine's name lookup reuses that node.
                        Require(actors.GroupBy(a => a.Root).All(g => g.Select(a => a.Name).Distinct().Count() == 1), "Different AIV actors share roots");
                        if (number > 6) Require(mission.Layout.AivResource == "aiv.zrd", "Missing variant did not fall back");
                        Require(nodes.SequenceEqual(world.Scene.Nodes.Select(n => n.Data.ToJsonString() + n.Metadata.ToJsonString())), "Source nodes mutated");
                        var pickups = mission.Actors.Where(a => a.Pickup != null).ToArray();
                        Require(pickups.Length == pickupCounts[number - 1][(int)difficulty], $"{directory} {difficulty}: pickup count {pickups.Length}; {string.Join("; ", mission.Diagnostics.Where(d => d.StartsWith("Mission pickup", StringComparison.Ordinal)))}");
                        Require(pickups.Select(a => a.Root).Distinct().Count() == pickups.Length, "Pickups share clone roots");
                        var archive = await resolver.OpenCachedAsync(Path.Combine(directory, "zrdr.zbd"), default);
                        var resource = archive.Assets.First(a => a.Name == mission.Layout.PickupResource);
                        var rows = ZrdDecoder.Decode(archive.Slice(resource.Offset, resource.Length))["children"]![0]!["children"]!.AsArray();
                        var visible = SceneBuilder.Assemble(mission.Scene).Placements.Select(p => p.NodeIndex).ToHashSet();
                        foreach (var pickup in pickups)
                        {
                            var metadata = pickup.Pickup!; var row = rows[metadata.Source.RecordIndex]!["children"]!.AsArray();
                            var xyz = row[2]!["children"]!.AsArray();
                            Vector3 position = new(JsonData.Scalar(xyz[0]!["value"], float.NaN), JsonData.Scalar(xyz[1]!["value"], float.NaN), JsonData.Scalar(xyz[2]!["value"], float.NaN));
                            Require(Vector3.Distance(position, SceneBuilder.LocalTransform(mission.Scene.Nodes[pickup.Root]).Translation) < .001f, "Pickup is displaced from its source record");
                            var subtree = Descendants(mission.Scene, pickup.Root).ToArray();
                            Require(subtree.Any(visible.Contains), $"Pickup {pickup.Name} has no visible geometry");
                            Require(!subtree.Where(n => mission.Scene.Nodes[n].Name == "bvol").Any(visible.Contains), "Pickup collision geometry is visible");
                        }
                        var turrets = mission.Scene.Nodes.Where(n => n.Metadata["preview_turret_reset"] != null).ToArray();
                        foreach (var turret in turrets)
                        {
                            var subtree = Descendants(mission.Scene, turret.Index).Select(n => mission.Scene.Nodes[n]).ToArray();
                            var healthy = subtree.FirstOrDefault(n => n.Name == "healthy"); var destroyed = subtree.FirstOrDefault(n => n.Name == "destroyed");
                            if (healthy != null && destroyed != null)
                            {
                                Require((healthy.Metadata.UInt("flags") & 4) != 0 && (destroyed.Metadata.UInt("flags") & 4) == 0, $"{directory} {turret.Name}: healthy/destroyed overlap remains ({healthy.Index}:{healthy.Metadata.UInt("flags"):X}, {destroyed.Index}:{destroyed.Metadata.UInt("flags"):X}, reset {turret.Metadata["preview_turret_reset"]}); {string.Join("; ", mission.Diagnostics.Where(d => d.Contains("destroy_turret", StringComparison.Ordinal)))}");
                                Require(!Descendants(mission.Scene, destroyed.Index).Any(visible.Contains), "Destroyed turret geometry remains assembled");
                            }
                        }
                        if (number == 1) Require(turrets.Length >= 16, "m1 per-instance turret initialization missing");
                        Require(!mission.Diagnostics.Any(d => d.StartsWith("Mission pickup ", StringComparison.Ordinal)), "Pickup placement diagnostics remain");
                        Console.WriteLine($"PASS {Path.GetFileName(root)}/{Path.GetFileName(directory)} {difficulty}: {actors.Length} AIV, {pickups.Length} pickups, {turrets.Length} initialized turrets; {mission.Layout.PickupResource}"); layouts++;
                    }
                    if (number == 1)
                    {
                        Require(!missions[MissionDifficulty.Easy].Actors.Any(a => a.Name == "ntank_01"), "Easy retained medium-only tank");
                        Require(missions[MissionDifficulty.Medium].Actors.Any(a => a.Name == "ntank_01"), "Medium lost tank");
                        Require(missions[MissionDifficulty.Hard].Actors.Any(a => a.Name == "tren_10"), "Hard-specific tank missing");
                        Require(!missions[MissionDifficulty.Medium].Actors.Any(a => a.Name == "tren_10"), "Hard tank leaked into Medium");
                    }
                    Require(ReferenceEquals(missions[MissionDifficulty.Medium], await MissionSceneLoader.LoadAsync(world, resolver, difficulty: MissionDifficulty.Medium)), "Difficulty cache did not retain correct baseline");
                    Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(world.Path))), "World source bytes changed");
                }
                string animPath = Path.Combine(root, "m1", "anim.zbd"); var anim = await resolver.OpenCachedAsync(animPath, default);
                var context = await AnimationPreviewContext.LoadAsync(anim.Animations!, animPath, resolver);
                int entry = context.Package.Entries.First(e => e.Name == "start_single_player").Index;
                var original = new AnimationPlayer(context, entry).AdvanceTo(2, true);
                var hard = await context.WithDifficultyAsync(resolver, MissionDifficulty.Hard);
                var restored = await hard.WithDifficultyAsync(resolver, MissionDifficulty.Medium);
                var replay = new AnimationPlayer(restored, entry).AdvanceTo(2, true);
                Require(original.Nodes.SequenceEqual(replay.Nodes) && original.Camera == replay.Camera, "Difficulty round trip changed deterministic animation");
                Require(context.Sounds.All(p => ReferenceEquals(p.Value, hard.Sounds[p.Key])), "Difficulty rebuild lost warm sound dependencies");
            }
            Console.WriteLine($"PASS {layouts} difficulty layouts; turret visibility, pickup counts/positions/visible geometry, cache isolation, source preservation, deterministic intro and shared sound dependencies."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static IEnumerable<int> Descendants(GameScene scene, int root)
    {
        HashSet<int> seen = []; Stack<int> pending = new(); pending.Push(root);
        while (pending.TryPop(out int index)) if (index >= 0 && index < scene.Nodes.Count && seen.Add(index))
        { yield return index; foreach (int child in SceneBuilder.Children(scene.Nodes[index])) pending.Push(child); }
    }
}
