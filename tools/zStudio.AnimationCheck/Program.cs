using System.Security.Cryptography;
using System.Text.Json;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;

if (args.Contains("--mission")) return await MissionCheck.RunAsync(args.Where(a => a != "--mission").ToArray());
if (args.Contains("--difficulty")) return await DifficultyCheck.RunAsync(args.Where(a => a != "--difficulty").ToArray());
if (args.Contains("--camera")) return await CameraCheck.RunAsync(args.Where(a => a != "--camera").ToArray());
if (args.Contains("--ground")) return await GroundCheck.RunAsync(args.Where(a => a != "--ground").ToArray());

int entries = 0, sequences = 0, events = 0, previews = 0; List<object> reports = []; List<string> failures = [];
foreach (string root in args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
{
    using AssetResolver resolver = new(Path.GetFullPath(root));
    foreach (string path in Directory.EnumerateFiles(root, "*.zbd", SearchOption.AllDirectories).Where(p => FormatRegistry.Probe(p).Family == FormatFamily.Animation))
    {
        Console.WriteLine("Checking " + path); byte[] original = File.ReadAllBytes(path); var package = AnimationPackage.Read(original);
        byte[] packed = AnimationWriter.Write(package); if (!original.AsSpan().SequenceEqual(packed)) failures.Add(path + ": no-op bytes differ");
        var reopened = AnimationPackage.Read(packed); if (!packed.AsSpan().SequenceEqual(AnimationWriter.Write(reopened))) failures.Add(path + ": reopen bytes differ");
        entries += package.Entries.Count; sequences += package.Entries.Sum(e => e.AllSequences.Count()); events += package.Entries.Sum(e => e.AllSequences.Sum(s => s.Events.Count));
        var context = await AnimationPreviewContext.LoadAsync(package, path, resolver);
        string[] names = ["pu000", "vtol_destruction1", "chutes", "shak", "bftmbrst.flt", "burning_model", "redsprks.flt", "bft_00", "expnuke.flt", "liteupsky", "fire_bft", "lock"];
        var selected = args.Contains("--all") ? package.Entries.Where(e => e.RootName.Length > 0) : package.Entries.Where(e => names.Contains(e.Name)).Take(20);
        foreach (var entry in selected)
        {
            try
            {
                AnimationPlayer player = new(context, entry.Index) { GroundPlaneEnabled = args.Contains("--ground-enabled") }; var frame = player.AdvanceTo(args.Contains("--all") ? .1 : 2, true);
                if (frame.Nodes.Any(n => !float.IsFinite(n.Transform.GetDeterminant()) || !float.IsFinite(n.Opacity))) failures.Add(path + "/" + entry.Index + ": nonfinite pose");
                if (!args.Contains("--all"))
                {
                    player.AdvanceTo(.5, true); var replay = player.AdvanceTo(2, true);
                    if (!frame.Nodes.SequenceEqual(replay.Nodes) || !frame.Sequences.SequenceEqual(replay.Sequences)) failures.Add(path + "/" + entry.Index + ": seek diverged");
                }
                var duration = args.Contains("--all") ? null : player.MeasureDuration();
                if (duration?.Frames is < 1 or > 216000 || player.Time != frame.Time) failures.Add(path + "/" + entry.Index + ": invalid duration or changed player time");
                previews++; reports.Add(new { path, entry.Index, entry.Name, frames = duration?.Frames, seconds = duration?.Seconds, durationKind = duration?.Kind.ToString(), poses = frame.Nodes.Count, events = frame.Trace.Count, diagnostics = frame.Diagnostics });
            }
            catch (Exception ex) { failures.Add(path + "/" + entry.Index + ": " + ex); }
        }
        if (!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(original))) failures.Add(path + ": source changed");
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { entries, sequences, events, previews, failures, reports }, new JsonSerializerOptions { WriteIndented = true }));
return failures.Count == 0 ? 0 : 1;
