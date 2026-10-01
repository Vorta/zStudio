using System.IO;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Convert to editable terrain on shipped missions: the converted terrain gives the altitude probe the same heights, zones,
/// soils and node attributes everywhere, in a world built from the same project. Runs with ZSTUDIO_SOURCE_PROJECT (a
/// reconstructed project, only read) or ZSTUDIO_CORPUS (reconstructed into a temporary folder first).
/// </summary>
public sealed class TerrainConversionCorpusTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("m1")]
    [InlineData("m5")]
    [InlineData("m6")]
    public async Task ConvertedTerrainProbesLikeTheShippedPieces(string mission)
    {
        string? project = Environment.GetEnvironmentVariable("ZSTUDIO_SOURCE_PROJECT");
        string temp = Path.Combine(Path.GetTempPath(), "zstudio-terrain-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (string.IsNullOrEmpty(project))
            {
                string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS");
                if (string.IsNullOrEmpty(corpus)) return;
                project = Path.Combine(temp, "project");
                await SourceExtractor.ExtractAsync(corpus, project, token: Token);
            }
            SourceWorkspace workspace = new(project);
            string database = $"data/{mission}/models/{mission}.gltf";
            var before = await SourceWorlds.BuildPreviewAsync(project, mission, Path.Combine(temp, "before"), workspace.Overlay(), token: Token);
            var plan = SourceTerrainConversion.Plan(workspace, database, SourceTerrainConversion.References(workspace, before.Dependencies, Token), Token);
            var output = TestContext.Current.TestOutputHelper;
            output?.WriteLine($"{mission}: {plan.Converted} pieces into {plan.Groups.Count} surfaces; kept {plan.Kept.Count}: " +
                string.Join("; ", plan.Kept.GroupBy(k => k.Reason).Select(g => $"{g.Key} ×{g.Count()}")));
            Assert.True(plan.Converted > 0);
            SourceTerrainConversion.Apply(workspace, plan, Token);
            var after = await SourceWorlds.BuildPreviewAsync(project, mission, Path.Combine(temp, "after"), workspace.Overlay(), token: Token);
            var (worldA, nodesA) = await Read(before, p => p.ModelFile == database && p.Database && plan.Groups.Any(g => g.Nodes.Contains(p.ModelNode)));
            var (worldB, nodesB) = await Read(after, p => p.Terrain == plan.Recipe);
            output?.WriteLine($"{mission}: {nodesA.Count} pieces before, {nodesB.Count} after; nodes {worldA.Nodes.Count} → {worldB.Nodes.Count}");
            Assert.Equal(nodesA.Count, plan.Converted);
            // Every piece fits the engine's limits and its cell.
            Assert.All(nodesB, n => Assert.True(n.Model!.Vertices.Count <= 921 && n.Model.Normals.Count <= 921, $"{n.Name}: {n.Model.Vertices.Count} vertices"));
            var report = TerrainProbe.Compare(nodesA, nodesB, 4, Token);
            output?.WriteLine($"{mission}: {report.Samples} samples, {report.Hits} hits, {report.Mismatches} differ, {report.HeightOnly} differ only in height (at most {report.MaximumHeightDifference})");
            // As the engine searches: only the point's cell and the world's own list.
            var cells = TerrainProbe.Compare(nodesA, nodesB, 4, Token, worldA.Nodes.First(n => n.Class == WorldNodeClass.World));
            output?.WriteLine($"{mission} by cell: {cells.Mismatches} differ, {cells.Revealed} newly found along cell edges");
            foreach (string example in cells.Examples) output?.WriteLine("  cell: " + example);
            Assert.Equal(0, cells.Mismatches);
            foreach (string example in report.Examples) output?.WriteLine(example);
            // Diagnostic: the polygons the converted terrain's probe hits where the shipped pieces had none.
            foreach (string example in report.Examples.Where(e => e.Contains("before nothing")).Take(2))
            {
                var at = example[1..example.IndexOf(')')].Split(", ").Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                foreach (var node in nodesB)
                    foreach (var polygon in node.Model!.Polygons)
                        if (TerrainProbe.Height(node.Model, polygon, at[0], at[1]) is float y && y <= TerrainProbe.Top)
                        {
                            output?.WriteLine($"  {node.Name}: {string.Join(" ", polygon.Vertices.Select(i => node.Model.Vertices[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}");
                            break;
                        }
                foreach (var node in nodesA)
                    foreach (var polygon in node.Model!.Polygons)
                    {
                        var v = polygon.Vertices.Select(i => node.Model.Vertices[i]).ToArray();
                        if (v.Min(p => p.X) - 1 <= at[0] && v.Max(p => p.X) + 1 >= at[0] && v.Min(p => p.Z) - 1 <= at[1] && v.Max(p => p.Z) + 1 >= at[1])
                            output?.WriteLine($"  before {node.Name}: {string.Join(" ", v.Select(p => p.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}");
                    }
            }
            Assert.True(report.Hits > 0);
            Assert.Equal(0, report.Mismatches);
        }
        finally { try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { } }
    }

    private static async Task<(GameZWorld World, List<WorldNode> Nodes)> Read(SourceWorldBuild build, Func<WorldNodeProvenance, bool> select)
    {
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        var slots = GameZWriter.NodeSlots(world);
        return (world, world.Nodes.Where(n => slots.TryGetValue(n, out int slot) && build.Provenance.TryGetValue(slot, out var p) && select(p)).ToList());
    }
}
