using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;

internal static class GroundCheck
{
    public static async Task<int> RunAsync(string[] roots)
    {
        try { return await CheckAsync(roots); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task<int> CheckAsync(string[] roots)
    {
        foreach (string rootArg in roots)
        {
            string root = Path.GetFullPath(rootArg), path = Path.Combine(root, "m1", "anim.zbd");
            using AssetResolver resolver = new(root);
            byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
            var doc = await resolver.OpenCachedAsync(path, default);
            var context = await AnimationPreviewContext.LoadAsync(doc.Animations!, path, resolver);
            var entry = context.Package.Entries.First(e => e.Name == "vtol_destruction1");
            byte[] source = AnimationWriter.Write(context.Package);
            var motions = entry.Sequences.SelectMany(s => s.Events).Where(e => e.Type == 10 && (e.U32(12) & 1) != 0).ToArray();
            var contactNodes = motions.ToDictionary(e => e.Id, e => context.Descendants(context.ResolveNode(entry, e.I32(16), context.ResolveRoot(entry))).ToHashSet());
            var debris = contactNodes.Values.SelectMany(n => n).ToHashSet();
            var expectedReleases = motions.Where(e => (e.U32(12) & 0x800) != 0).Select(e => e.Text(208)).ToArray();
            var bank = AnimationAudioDependencies.Collect(context.Package, entry.Index);
            foreach (float height in new[] { 0f, 40f, 100f })
            {
            var player = new AnimationPlayer(context, entry.Index) { GroundPlaneEnabled = true, PreviewHeight = height };
            var duration = player.MeasureDuration();
            Require(player.Time == 0, "Duration advanced visible time");
            Console.WriteLine($"{rootArg}, height {height}: {duration}");
            int contacts = 0, checkedVertices = 0; double largestStep = 0; AnimationFrame frame = player.Frame();
            for (int i = 0; i < 2400; i++)
            {
                long start = Stopwatch.GetTimestamp(); frame = player.Step();
                largestStep = Math.Max(largestStep, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                contacts += frame.Sounds.Count;
                foreach (var cue in frame.Sounds) Require(bank.Names.Contains(cue.Name), "Impact audio was not preloaded: " + cue.Name);
                var participating = frame.Trace.Where(t => contactNodes.ContainsKey(t.Event)).SelectMany(t => contactNodes[t.Event]).ToHashSet();
                foreach (var pose in frame.Nodes.Where(n => participating.Contains(n.SourceNode) && n.Visible))
                {
                    var model = context.Scene.Models[pose.Model];
                    foreach (int vertex in model.Polygons.SelectMany(p => p.Vertices).Distinct())
                    {
                        var p = model.Vertices[vertex] + (model.Morphs.Length == model.Vertices.Length ? model.Morphs[vertex] * pose.Morph : Vector3.Zero);
                        float y = Vector3.Transform(p, pose.Transform).Y; checkedVertices++;
                        Require(y >= -.002f, $"{context.Scene.Nodes[pose.SourceNode].Name} penetrated Y=0 at {frame.Time:0.000}s: {y}");
                    }
                }
                if (i is 59 or 239 or 479 or 1799) Console.WriteLine($" t={frame.Time:0.00}: " + string.Join("; ", frame.Sequences.Where(s => s.Instance == 1 && s.Name.StartsWith("fly_", StringComparison.Ordinal)).Select(s => $"{s.Name} {s.State} event {s.EventIndex}")));
                if (player.IsComplete && frame.ActiveSounds.Count == 0) break;
            }
            foreach (string release in expectedReleases)
                Require(frame.Trace.Any(t => entry.Sequences.First(s => s.Name == release).Id == t.Sequence), "Ground did not release " + release);
            Require(contacts > 0, "No impact sample was emitted");
            Require(duration.IsFinite && player.IsComplete, "Collision animation should have a finite completion");
            Require(frame.Time <= duration.Seconds + AnimationPlayer.StepSeconds + 1e-7, $"Duration clipped collision stages: completed {frame.Time}, range {duration.Seconds}");
            var end = frame; player.AdvanceTo(.5, true); var replay = player.AdvanceTo(end.Time, true);
            Require(end.Nodes.SequenceEqual(replay.Nodes) && end.Sequences.SequenceEqual(replay.Sequences), "Ground seek diverged");
            var alternate = new AnimationPlayer(context, entry.Index) { GroundPlaneEnabled = true, LodLevel = 10, PreviewHeight = height };
            Require(end.Nodes.Select(p => p.Transform).SequenceEqual(alternate.AdvanceTo(end.Time, true).Nodes.Select(p => p.Transform)), "LOD altered ground physics");
            var disabled = new AnimationPlayer(context, entry.Index).AdvanceTo(8, true);
            Require(disabled.Nodes.Where(p => debris.Contains(p.SourceNode)).Any(p => p.Transform.M42 < -5), "Grid disabled did not restore free falling");
            Require(AnimationWriter.Write(context.Package).AsSpan().SequenceEqual(source), "Ground changed serialized bytes");
            Require(SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(hash), "Source changed");
            Console.WriteLine($"PASS: {checkedVertices} vertex contacts checked, {contacts} sound cues, {expectedReleases.Length} impact releases, completion {end.Time:0.000}s, max simulation step {largestStep:0.00}ms. Seek, LOD, duration, disabled collision and source preservation verified.");
            Console.WriteLine("Diagnostics: " + string.Join(" | ", frame.Diagnostics));
            }
        }
        return 0;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
