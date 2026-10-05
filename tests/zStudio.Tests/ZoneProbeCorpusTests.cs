using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>How the shipped worlds meet the engine's zone probes (see docs/world-editor-plan.md, "Zone probes").</summary>
public sealed class ZoneProbeCorpusTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ShippedWorldsKeepEveryProbeWithinItsLimits()
    {
        string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (string.IsNullOrEmpty(corpus)) return;
        foreach (string path in Directory.GetFiles(corpus, "gamez.zbd", SearchOption.AllDirectories))
        {
            var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token), Token);
            var root = world.Nodes.Single(n => n.Class == WorldNodeClass.World);
            Assert.Equal(0, root.PayloadInt(0x50)); // queries outside the grid are not clamped
            float x0 = root.PayloadFloat(0x34), z0 = root.PayloadFloat(0x38), width = root.PayloadFloat(0x3C), depth = root.PayloadFloat(0x40);
            int ground = 0;
            for (float x = x0 + 0.37f; x < x0 + width; x += 16)
                for (float z = z0 - 0.29f; z > z0 + depth; z -= 16)
                {
                    var vehicle = ZoneProbe.Probe(root, x, z, ZoneSet.Cleared, ZoneProbeKind.Vehicle);
                    // No point holds 32 surfaces, and no altitude surface lies above the probes' start.
                    Assert.False(vehicle.Full || ZoneProbe.Probe(root, x, z, ZoneSet.Cleared).Full, $"{path} ({x}, {z})");
                    Assert.Equal(vehicle.Hits.Count, ZoneProbe.Probe(root, x, z, ZoneSet.Cleared, ZoneProbeKind.Vehicle, float.MaxValue).Hits.Count);
                    if (vehicle.Hits.Count > 0) ground++;
                }
            // Every mission has ground under a large part of its grid (the probe's winding and transforms find it).
            Assert.True(ground > 1000, $"{path}: ground at {ground} samples");
        }
    }
}
