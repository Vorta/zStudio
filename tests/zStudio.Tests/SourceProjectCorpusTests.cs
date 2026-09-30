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
    public async Task ReconstructedSourcesExportGameEquivalentFilesAndRoundTrip()
    {
        string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (string.IsNullOrEmpty(corpus)) return;
        string work = Path.Combine(Path.GetTempPath(), "zstudio-source-corpus-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(work, "project"), exported = Path.Combine(work, "zbd"), again = Path.Combine(work, "again");
        try
        {
            var report = await SourceExtractor.ExtractAsync(corpus, project, token: Token);
            // Animation definitions changed after the shipped animations were compiled are rebuilt from them; nothing else is noted.
            const string Rebuilt = " was changed after the shipped animations were compiled; it was rebuilt from anim.zbd.";
            Assert.All(report.Notes, n => Assert.EndsWith(Rebuilt, n));
            var rebuilt = report.Notes.Select(n => Path.GetFileName(n[..n.IndexOf(':')])).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(1, report.Families["scripts"]); Assert.Equal(3, report.Families["sounds"]);
            Assert.Equal(Directory.GetFiles(corpus, "gamez.zbd", SearchOption.AllDirectories).Length, report.Families["worlds"]);
            Assert.Equal(Directory.GetFiles(corpus, "anim.zbd", SearchOption.AllDirectories).Length, report.Families["animations"]);
            Assert.Equal(Directory.GetFiles(corpus, "zrdr.zbd", SearchOption.AllDirectories).Length, report.Families["resources"]);
            Assert.All(report.NotReconstructed, f => Assert.DoesNotContain("zrdr.zbd", f));
            // The original layout, and nothing zStudio-specific.
            Assert.Equal(["data", "gamegen"], Directory.GetFileSystemEntries(project).Select(Path.GetFileName).Order().ToArray());
            Assert.True(File.Exists(Path.Combine(project, "data", "m1", "zrdr", "envmodels", "frcgate.zrd")));
            Assert.True(File.Exists(Path.Combine(project, "gamegen", "support", "common.gw")));

            var export = await SourceBuilder.ExportAsync(project, exported, token: Token);
            Assert.Equal(0, export.Failed);
            Assert.Equal(SourceBuilder.Plan(project).Count, export.Built);

            foreach (string archive in Directory.GetFiles(corpus, "zrdr.zbd", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(corpus, archive);
                var shipped = Members(archive); var built = Members(Path.Combine(exported, relative));
                Assert.Equal(shipped.Keys.Order(StringComparer.OrdinalIgnoreCase), built.Keys.Order(StringComparer.OrdinalIgnoreCase));
                Assert.All(shipped.Where(m => !rebuilt.Contains(m.Key)), m => Assert.Equal(m.Value, built[m.Key]));
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

            // Exported files carry their sources' folders, so reconstructing them restores the same tree.
            var second = await SourceExtractor.ExtractAsync(exported, again, token: Token);
            Assert.Empty(second.Notes);
            var first = Tree(project); var reconstructed = Tree(again);
            Assert.Equal(first.Keys.Order(StringComparer.OrdinalIgnoreCase), reconstructed.Keys.Order(StringComparer.OrdinalIgnoreCase));
            Assert.All(first, f => Assert.Equal(f.Value, reconstructed[f.Key]));
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
    private static Dictionary<string, byte[]> Tree(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
}
