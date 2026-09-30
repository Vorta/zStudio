using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceProjectCorpusTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryShippedFileRebuildsByteIdenticallyFromTheReconstructedSources()
    {
        string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (string.IsNullOrEmpty(corpus)) return;
        string project = Path.Combine(Path.GetTempPath(), "zstudio-source-corpus-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = await SourceExtractor.ExtractAsync(corpus, project, token: Token);
            var report = await SourcePacker.VerifyAsync(project, token: Token);
            Assert.Equal(manifest.Outputs.Count, report.Identical);
            Assert.Equal(0, report.Changed + report.Failed);
            // Resources, sound banks and prepared scripts are rebuilt from sources, not copied.
            Assert.All(manifest.Outputs.Where(o => Path.GetFileName(o.Path).Equals("zrdr.zbd", StringComparison.OrdinalIgnoreCase) || o.Path.StartsWith("sounds", StringComparison.OrdinalIgnoreCase)),
                o => Assert.Equal("archive", o.Family));
            Assert.Equal("scripts", Assert.Single(manifest.Outputs, o => o.Path.Equals("interp.zbd", StringComparison.OrdinalIgnoreCase)).Family);
            Assert.True(File.Exists(Path.Combine(project, "gamegen", "support", "common.gw")));
            Assert.True(File.Exists(Path.Combine(project, "data", "m1", "zrdr", "envmodels", "frcgate.zrd")));
            Assert.True(File.Exists(Path.Combine(project, "data", "common", "zrdr", "explosns", "he_dirt.zrd")));
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "zstudio-source-notes.txt"), manifest.Notes);
        }
        finally { if (Directory.Exists(project)) Directory.Delete(project, true); }
    }
}
