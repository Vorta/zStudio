using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>Convert to editable terrain on a small database: what converts, what stays, and the probe before and after.</summary>
public sealed class TerrainConversionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PiecesBecomeSurfacesAndTheProbeFindsTheSame()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteTerrainDatabase();
        SourceWorkspace workspace = new(fixture.Project);
        var before = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(fixture.Root, "before"), workspace.Overlay(), token: Token);
        var references = SourceTerrainConversion.References(workspace, before.Dependencies, Token);
        Assert.Contains("ground", references.Names);
        var plan = SourceTerrainConversion.Plan(workspace, "data/m1/models/m1.gltf", references, Token);
        // Ground is animated by name and the sky is a landmark; the rest becomes two surfaces, since flat_over stands over flat_a.
        Assert.Equal(["named by a script, resource or animation", "landmark (the horizon and other always-drawn nodes)"], plan.Kept.Select(k => k.Reason));
        Assert.Equal(3, plan.Converted);
        Assert.Equal([2, 1], plan.Groups.Select(g => g.Nodes.Count));
        Assert.Equal(["z3_01000018_1", "z3_01000018_2"], plan.Groups.Select(g => g.Id));
        SourceTerrainConversion.Apply(workspace, plan, Token);
        Assert.Equal(["data/m1/models/m1.gltf", "data/m1/models/m1_terrain.bin", "data/m1/models/m1_terrain.gltf", "data/m1/models/m1_terrain.terrain.json"], workspace.DirtyFiles);
        // The marker stands where flat_a stood: after ground, before sky.
        var database = JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf", Token)!)!;
        var roots = database["scenes"]![0]!["nodes"]!.AsArray().Select(n => database["nodes"]![n!.GetValue<int>()]!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["ground", "m1_terrain", "sky"], roots);
        var recipe = SourceTerrain.Read(workspace, plan.Recipe, Token);
        Assert.All(recipe.Surfaces, s => { Assert.Equal(3, s.Defaults.NodeZone); Assert.Equal(WorldGltf.DefaultCarried, s.Defaults.Flags); });
        var after = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(fixture.Root, "after"), workspace.Overlay(), token: Token);
        var a = await Nodes(before, p => p.Database && plan.Groups.Any(g => g.Nodes.Contains(p.ModelNode)));
        var b = await Nodes(after, p => p.Terrain == plan.Recipe);
        Assert.Equal(3, a.Count);
        // flat_b straddles x = 256: two pieces of the first surface (one per cell), one of the second.
        Assert.Equal(3, b.Count);
        var report = TerrainProbe.Compare(a, b, 2, Token);
        Assert.True(report.Hits > 500);
        Assert.Equal(0, report.Mismatches); Assert.Equal(0, report.HeightOnly);
        // Over flat_a the probe finds both sheets, the upper one from the second surface.
        var hits = TerrainProbe.At(b, 220, 320);
        Assert.Equal([0f, 10f], hits.Select(h => h.Height));
        workspace.Undo();
        Assert.False(workspace.IsDirty);
    }

    private static async Task<List<WorldNode>> Nodes(SourceWorldBuild build, Func<WorldNodeProvenance, bool> select)
    {
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        var slots = GameZWriter.NodeSlots(world);
        return world.Nodes.Where(n => slots.TryGetValue(n, out int slot) && build.Provenance.TryGetValue(slot, out var p) && select(p)).ToList();
    }
}
