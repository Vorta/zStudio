using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;

internal static class MissionCheck
{
    public static async Task<int> RunAsync(string[] roots)
    {
        try
        {
            foreach (string rootArg in roots)
            {
                string root = Path.GetFullPath(rootArg), path = Path.Combine(root, "m1", "anim.zbd");
                using AssetResolver resolver = new(root);
                var document = await resolver.OpenCachedAsync(path, default); var package = document.Animations!;
                byte[] original = AnimationWriter.Write(package);
                var context = await AnimationPreviewContext.LoadAsync(package, path, resolver);
                var mission = context.Mission!;
                Console.WriteLine($"{root}: {mission.Actors.Count} placed actors, {mission.DormantRoots.Count} dormant actors, {mission.Scene.Nodes.Count} nodes");
                foreach (var (name, expected) in new (string, Vector3)[] { ("bft_00", new(2222.1f,3.9f,1294.2f)), ("vwbus_rebel", new(2116,32,1702)), ("tractr", new(2219.1235f,2.028507f,1307.1873f)), ("trailr", new(2223.177f,2.001464f,1290.161f)) })
                {
                    int index = context.Scene.Nodes.First(n => n.Name == name).Index;
                    var actual = context.WorldTransform(index).Translation;
                    Console.WriteLine($"  {name}: {actual}");
                    Require(Vector3.Distance(expected, actual) < .005f, name + " starting pose differs");
                }
                Require(mission.Actors.Count > 20, "Vehicle instances were not recovered");
                Require(mission.Actors.Where(a => a.Name.StartsWith("scou_easy_", StringComparison.Ordinal)).Select(a => a.Root).Distinct().Count() > 1, "Template instances collapsed");
                var entry = package.Entries.First(e => e.Name == "call_flufvtol");
                AnimationPlayer player = new(context, entry.Index);
                int vtol = context.Scene.Nodes.First(n => n.Name == "vtol2").Index;
                var children = context.Descendants(vtol).ToHashSet();
                var before = player.AdvanceTo(99, true);
                Require(!before.Nodes.Any(n => children.Contains(n.SourceNode) && n.Visible), "Dormant VTOL is visible before activation");
                var after = player.AdvanceTo(100.1, true);
                var visible = after.Nodes.Where(n => children.Contains(n.SourceNode) && n.Visible).ToArray();
                Require(visible.Length > 0, "VTOL did not appear after its launch");
                Require(visible.All(n => n.Transform.Translation.Length() > 1000), "VTOL has an origin ghost");
                Require(after.Nodes.Select(n => n.Id).Distinct().Count() == after.Nodes.Count, "Duplicate runtime render identity");
                player.AdvanceTo(98, true); var replay = player.AdvanceTo(100.1, true);
                Require(after.Nodes.SequenceEqual(replay.Nodes), "Shared world state diverged on seek");
                Require(AnimationWriter.Write(package).AsSpan().SequenceEqual(original), "Preview mutated animation records");
                Require(SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(original)), "Source bytes changed");
                Console.WriteLine(JsonSerializer.Serialize(new { mission.Diagnostics, visibleVtolParts = visible.Length }));
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
