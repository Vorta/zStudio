using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceProjectCorpusTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReconstructedSourcesExportGameEquivalentFiles()
    {
        string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (string.IsNullOrEmpty(corpus)) return;
        string work = Path.Combine(Path.GetTempPath(), "zstudio-source-corpus-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(work, "project"), exported = Path.Combine(work, "zbd"), again = Path.Combine(work, "again");
        try
        {
            var report = await SourceExtractor.ExtractAsync(corpus, project, token: Token);
            // Animation definitions shipped in another version than the animations were compiled from are rebuilt from
            // them; nothing else is noted. Definitions that name a sequence twice compile as shipped (the last name).
            const string Rebuilt = " is not the version the shipped animations were compiled from; it was rebuilt from anim.zbd.";
            Assert.All(report.Notes, n => Assert.EndsWith(Rebuilt, n));
            var rebuilt = report.Notes.Select(n => Path.GetFileName(n[..n.IndexOf(':')])).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("sbarm.zad", rebuilt); Assert.DoesNotContain("deathmulti.zad", rebuilt);
            Assert.Equal(1, report.Families["scripts"]); Assert.Equal(3, report.Families["sounds"]);
            Assert.Equal(Directory.GetFiles(corpus, "gamez.zbd", SearchOption.AllDirectories).Length, report.Families["worlds"]);
            Assert.Equal(Directory.GetFiles(corpus, "anim.zbd", SearchOption.AllDirectories).Length, report.Families["animations"]);
            Assert.Equal(Directory.GetFiles(corpus, "zrdr.zbd", SearchOption.AllDirectories).Length, report.Families["resources"]);
            Assert.All(report.NotReconstructed, f => Assert.DoesNotContain("zrdr.zbd", f));
            // The original layout, and nothing zStudio-specific.
            Assert.Equal(["data", "gamegen"], Directory.GetFileSystemEntries(project).Select(Path.GetFileName).Order().ToArray());
            Assert.True(File.Exists(Path.Combine(project, "data", "m1", "zrdr", "envmodels", "frcgate.zad")));
            Assert.True(File.Exists(Path.Combine(project, "gamegen", "support", "common.gw")));
            // Animation definitions are .zad files, never zReader resources; pickup.zrd keeps only its pickup data.
            Assert.True(File.Exists(Path.Combine(project, "data", "m1", "zrdr", "anim.zad")));
            Assert.True(File.Exists(Path.Combine(project, "data", "common", "zrdr", "pickup.zad")));
            Assert.All(Directory.GetFiles(Path.Combine(project, "data"), "*.zrd", SearchOption.AllDirectories), f =>
                Assert.False(Recoil.Zbd.Core.Animation.AnimationDefinitionSet.HoldsDefinitions(ZrdText.LooksLikeText(File.ReadAllBytes(f)) ? ZrdText.Parse(File.ReadAllBytes(f), Token) : ZrdDecoder.Read(File.ReadAllBytes(f), Token)), f));

            var export = await SourceBuilder.ExportAsync(project, exported, token: Token);
            Assert.Equal(0, export.Failed);
            Assert.Equal(SourceBuilder.Plan(project).Count, export.Built);

            foreach (string archive in Directory.GetFiles(corpus, "zrdr.zbd", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(corpus, archive);
                var built = Members(Path.Combine(exported, relative));
                // The shipped resources without the animation definitions, which stay in the project.
                Dictionary<string, byte[]> expected = new(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, bytes) in Members(archive))
                    if (ZrdDecoder.TryRead(bytes, Token) is { } tree && Recoil.Zbd.Core.Animation.AnimationDefinitionSet.HoldsDefinitions(tree))
                    { if (Recoil.Zbd.Core.Animation.AnimationDefinitionSet.Split(tree).Others is { } others) expected[name] = ZrdWriter.Write(others, Token); }
                    else expected[name] = bytes;
                Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), built.Keys.Order(StringComparer.OrdinalIgnoreCase));
                Assert.All(expected, m => Assert.Equal(m.Value, built[m.Key]));
            }
            // Animations rebuilt from the definitions and keyframe scripts match every shipped entry.
            foreach (string animations in Directory.GetFiles(corpus, "anim.zbd", SearchOption.AllDirectories))
            {
                var shipped = Recoil.Zbd.Core.Animation.AnimationPackage.Read(File.ReadAllBytes(animations), Token);
                var built = Recoil.Zbd.Core.Animation.AnimationPackage.Read(File.ReadAllBytes(Path.Combine(exported, Path.GetRelativePath(corpus, animations))), Token);
                Assert.Equal(shipped.Entries.Select(e => e.Name), built.Entries.Select(e => e.Name));
                Assert.All(Enumerable.Range(1, shipped.Entries.Count - 1), i => Assert.Null(Recoil.Zbd.Core.Animation.AnimationComparer.Difference(shipped.Entries[i], built.Entries[i])));
            }
            // Worlds rebuilt by their scripts have the shipped nodes, placements, flags, cells, models and textures; only the
            // grouping of coplanar triangles into polygons may differ, which draws the same surfaces.
            foreach (string world in Directory.GetFiles(corpus, "gamez.zbd", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(corpus, world);
                var differences = WorldComparer.Compare(World(world), World(Path.Combine(exported, relative)));
                Assert.All(differences, d => Assert.Equal("model.polygons", d.Field));
            }
            var scripts = Scripts(Path.Combine(corpus, "interp.zbd")); var builtScripts = Scripts(Path.Combine(exported, "interp.zbd"));
            Assert.Equal(scripts.Keys.Order(StringComparer.OrdinalIgnoreCase), builtScripts.Keys.Order(StringComparer.OrdinalIgnoreCase));
            Assert.All(scripts, s => Assert.Equal(s.Value, builtScripts[s.Key]));
            foreach (string bank in new[] { "soundsh.zbd", "soundsm.zbd", "soundsl.zbd" })
            {
                var shipped = Members(Path.Combine(corpus, bank)); var built = Members(Path.Combine(exported, bank));
                Assert.Equal(shipped.Keys.Order(StringComparer.OrdinalIgnoreCase), built.Keys.Order(StringComparer.OrdinalIgnoreCase));
                foreach (var (name, bytes) in shipped)
                {
                    var expected = WaveDecoder.Read(bytes, Token); var actual = WaveDecoder.Read(built[name], Token);
                    Assert.Equal((expected.SampleRate, expected.BitsPerSample, expected.Channels), (actual.SampleRate, actual.BitsPerSample, actual.Channels));
                    Assert.InRange(actual.DataLength / actual.BlockAlign - expected.DataLength / expected.BlockAlign, -2, 2);
                    Assert.Equal(expected.Cues.Count, actual.Cues.Count);
                    // The best-quality bank is the source; its samples already satisfy their declarations.
                    if (bank == "soundsh.zbd") Assert.Equal(bytes, built[name]);
                }
            }

            // Every keyframe script is the SI Animation Script the shipped keyframes came from.
            var scriptsWritten = Directory.GetFiles(Path.Combine(project, "data"), "*.zan", SearchOption.AllDirectories);
            Assert.NotEmpty(scriptsWritten);
            Assert.All(scriptsWritten, s => Assert.True(Recoil.Zbd.Core.Animation.SiAnimationScript.Recognize(File.ReadAllBytes(s)), s));
            // The shipped files are stamped, so their scripts carry the DKit messages of their exporter.
            Assert.All(scriptsWritten, s => Assert.Contains("\r\nWarning, file version ", File.ReadAllText(s, System.Text.Encoding.Latin1)));

            // A project is unpacked once, from the original files; its exports carry no definitions and are refused.
            var refused = await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(exported, again, token: Token));
            Assert.Equal(SourceExtractor.NotOriginal, refused.Message);
            Assert.False(Directory.Exists(again));

            // A vehicle only another mission loads (the 1999 light tank of m2–m6) added to m1: the export of m1 holds its
            // nodes, every texture its materials use in each pack, and the animations other missions list for it.
            var m1 = World(Path.Combine(exported, "m1", "gamez.zbd"));
            HashSet<string> present = new(m1.Nodes.Select(n => n.Name), StringComparer.Ordinal);
            var models = SourceWorlds.Models(project);
            var vehicle = models.FirstOrDefault(m => m.Path == "data/m2/models/bft/ltank.gltf") ??
                models.First(m => m.Folder.EndsWith("/models/bft", StringComparison.Ordinal) && !m.Folder.StartsWith("data/m1/", StringComparison.Ordinal) && !present.Contains(m.Name));
            Assert.DoesNotContain(vehicle.Name, present);
            var definitions = SourceWorlds.DefinitionsFor(project, "m1", vehicle.Name, token: Token);
            SourceWorkspace edits = new(project);
            SourceWorlds.AddModel(edits, "m1", new(new(vehicle.Path, vehicle.Name), definitions.Select(d => d.Path).ToArray()), Token);
            edits.Save(Token);
            string added = Path.Combine(work, "added");
            var withVehicle = await SourceBuilder.ExportAsync(project, added, ["m1/gamez.zbd", "m1/anim.zbd", "m1/rtexture16.zbd", "m1/texture2.zbd"], token: Token);
            Assert.Equal(0, withVehicle.Failed);
            var vehicleWorld = World(Path.Combine(added, "m1", "gamez.zbd"));
            var root = vehicleWorld.Nodes.Single(n => n.Name == vehicle.Name);
            Assert.Empty(root.Parents);
            HashSet<string> textures = new(StringComparer.OrdinalIgnoreCase);
            Collect(root, 0);
            Assert.NotEmpty(textures);
            foreach (string pack in new[] { "rtexture16.zbd", "texture2.zbd" })
            {
                var names = FormatRegistry.Default.OpenBytes(pack, File.ReadAllBytes(Path.Combine(added, "m1", pack)), token: Token).Assets.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Assert.All(textures, t => Assert.Contains(t, names));
            }
            var package = Recoil.Zbd.Core.Animation.AnimationPackage.Read(File.ReadAllBytes(Path.Combine(added, "m1", "anim.zbd")), Token);
            if (definitions.Count > 0) Assert.Contains(package.Entries, e => e.RootName == vehicle.Name);
            void Collect(WorldNode node, int depth)
            {
                Assert.True(depth < 512);
                foreach (var polygon in node.Model?.Polygons ?? []) if (polygon.Material?.Texture is { } texture) textures.Add(texture.Name);
                foreach (var child in node.Children) Collect(child, depth + 1);
            }
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
    }

    private static Dictionary<string, byte[]> Members(string path)
    {
        var doc = FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token);
        Dictionary<string, byte[]> members = new(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in doc.Assets) members.TryAdd(asset.Name, doc.Slice(asset.Offset, asset.Length).ToArray());
        return members;
    }
    private static GameZWorld World(string path) => GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token), Token);
    private static Dictionary<string, string> Scripts(string path)
    {
        var doc = FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token);
        return doc.Scripts!.Entries.ToDictionary(e => e.Name, e => string.Join("\n", e.Instructions.Select(i => string.Join("\u0001", i.Tokens))), StringComparer.OrdinalIgnoreCase);
    }
}
