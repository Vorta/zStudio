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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeutralGeometryConversionKeepsMapAssignmentsAndUndo(bool grouped)
    {
        using SourceWorldFixture fixture = new(); fixture.WriteTerrainDatabase(grouped);
        const string database = "data/m1/models/m1.gltf", manifest = "data/m1/meta/zones.json";
        SourceWorkspace workspace = new(fixture.Project);
        byte[] bytes = File.ReadAllBytes(fixture.Path(database));
        var document = WorldAssembler.ReadModel(bytes, database, (path, _) => File.ReadAllBytes(fixture.Path(path)), Token);
        var profile = WorldGltf.CaptureZoneProfile(document, token: Token);
        // Both direct leaves and a group inherit the actual load root. The group's stored high bits survive,
        // but its inherited low byte is 5, not the stale 3 in its authored word.
        var sourceNodes = document.AllNodes().ToArray();
        profile = profile with { LoadRoot = new(5, true), Nodes = profile.Nodes.Select((zone, i) =>
            sourceNodes[i].Name == "g1" ? new WorldNodeZone(0x00010003, zone.Gate, true)
            : sourceNodes[i].Name.StartsWith("flat_", StringComparison.Ordinal) ? zone with { Inherit = true }
            : zone).ToArray() };
        var json = JsonNode.Parse(bytes)!.AsObject();
        foreach (var node in json["nodes"]!.AsArray())
        {
            if (node?["extras"]?["recoil"] is not JsonObject values) continue;
            values.Remove("zone"); values.Remove("zoneWord");
            if (values["flags"] is { } flags)
                values["flags"] = $"0x{(Convert.ToUInt32(flags.GetValue<string>()[2..], 16) & ~WorldGltf.ZoneGate):X8}";
        }
        foreach (var material in json["materials"]!.AsArray())
            (material?["extras"]?["recoil"] as JsonObject)?.Remove("zone");
        fixture.Write(database, json.ToJsonString());
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Path(manifest))!);
        File.WriteAllBytes(fixture.Path(manifest), new SourceMapZones([new(database, database, profile, [])]).Write(Token));
        workspace = new(fixture.Project);
        var before = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "original"), workspace.Overlay(), token: Token);
        var plan = SourceTerrainConversion.Plan(workspace, database, SourceTerrainConversion.References(workspace, before.Dependencies, "gamegen/m1.gs", Token), Token);
        Assert.Equal(3, plan.Converted); Assert.All(plan.Groups, g => Assert.Equal(5, g.Zone));
        SourceTerrainConversion.Apply(workspace, plan, Token);
        Assert.Contains(manifest, workspace.DirtyFiles);
        var map = SourceMapZones.Parse(workspace.Read(manifest, Token)!, Token);
        Assert.Contains(map.Assets, a => a.LogicalPath == plan.Surfaces);
        var retained = Assert.Single(map.Assets, a => a.LogicalPath == database).Profile;
        Assert.Equal(profile.LoadRoot, retained.LoadRoot);
        if (grouped) Assert.Contains(retained.Nodes, n => n.Word == 0x00010003 && n.Inherit);
        var after = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "converted"), workspace.Overlay(), token: Token);
        var a = await Nodes(before, p => p.Database && p.ModelFile == database && plan.Groups.Any(g => g.Nodes.Contains(p.ModelNode)));
        var b = await Nodes(after, p => plan.Recipes.Contains(p.Terrain));
        Assert.All(a, n => Assert.Equal(5u, n.Zone & 255u));
        Assert.All(b, n => Assert.Equal(5u, n.Zone & 255u));
        var report = TerrainProbe.Compare(a, b, 2, Token);
        AssertCompared(report); Assert.True(report.Hits > 0); Assert.Equal(0, report.Mismatches);
        workspace.Undo(); Assert.False(workspace.IsDirty);
        Assert.Single(SourceMapZones.Parse(workspace.Read(manifest, Token)!, Token).Assets);
    }

    [Fact]
    public async Task PiecesBecomeSurfacesAndTheProbeFindsTheSame()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteTerrainDatabase();
        SourceWorkspace workspace = new(fixture.Project);
        var before = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "before"), workspace.Overlay(), token: Token);
        var references = SourceTerrainConversion.References(workspace, before.Dependencies, $"gamegen/{before.Mission}.gs", Token);
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
        var after = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "after"), workspace.Overlay(), token: Token);
        var a = await Nodes(before, p => p.Database && plan.Groups.Any(g => g.Nodes.Contains(p.ModelNode)));
        var b = await Nodes(after, p => p.Terrain == plan.Recipe);
        Assert.Equal(3, a.Count);
        // flat_b straddles x = 256: two pieces of the first surface (one per cell), one of the second.
        Assert.Equal(3, b.Count);
        var report = TerrainProbe.Compare(a, b, 2, Token);
        AssertCompared(report);
        Assert.True(report.Hits > 500);
        Assert.Equal(0, report.Mismatches); Assert.Equal(0, report.HeightOnly);
        // Over flat_a the probe finds both sheets, the upper one from the second surface.
        var hits = TerrainProbe.At(b, 220, 320, token: TestContext.Current.CancellationToken);
        Assert.Equal([0f, 10f], hits.Select(h => h.Height));
        workspace.Undo();
        Assert.False(workspace.IsDirty);

        // Equal-height ties use encounter order. Attribute changes cannot sort the first run, and later runs
        // cannot jump ahead of kept roots or kept children of a transparent database group.
        using SourceWorldFixture ordered = new();
        const string orderedDatabase = "data/m1/models/m1.gltf";
        WorldNode Piece(string name, float x, uint zone)
        {
            ModelBuilder builder = new();
            builder.Add(new([new(x, 0, 8), new(x + 8, 0, 8), new(x + 8, 0, 0), new(x, 0, 0)], [], [], [],
                new() { Flags = 0xFF }, Zone: 0xFFFF0001u | zone << 8));
            WorldNode node = new(name, WorldNodeClass.Object3D) { Model = builder.Finish(), Flags = WorldGltf.DefaultCarried, Zone = zone };
            node.SetPayloadInt(0, 0x28); return node;
        }
        WorldNode holder = new("group", WorldNodeClass.Object3D); holder.SetPayloadInt(0, 0x28);
        holder.Children.AddRange([Piece("early", 32, 8), Piece("keep", 48, 7), Piece("late", 48, 6)]);
        var (orderedJson, orderedBin) = WorldGltf.Export(
            [Piece("first", 0, 4), Piece("second", 0, 3), Piece("ground", 16, 6), Piece("after_ground", 16, 5), holder],
            255, new() { Texture = t => ($"../textures/{t.Name}.png", 0), Group = n => ReferenceEquals(n, holder) }).Write("m1.bin", Token);
        ordered.Write(orderedDatabase, orderedJson); ordered.Write("data/m1/models/m1.bin", orderedBin);
        SourceWorkspace orderedWorkspace = new(ordered.Project);
        var orderedBefore = await SourceWorlds.BuildPreviewAsync(ordered.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(ordered.Project), "before"), orderedWorkspace.Overlay(), token: Token);
        var orderedReferences = SourceTerrainConversion.References(orderedWorkspace, orderedBefore.Dependencies, "gamegen/m1.gs", Token);
        orderedReferences.Names.Add("keep");
        var orderedPlan = SourceTerrainConversion.Plan(orderedWorkspace, orderedDatabase, orderedReferences, Token);
        Assert.Equal([4, 3, 5, 8, 6], orderedPlan.Groups.Select(g => g.Zone));
        Assert.Equal(4, orderedPlan.Recipes.Count);
        SourceTerrainConversion.Apply(orderedWorkspace, orderedPlan, Token);
        Assert.All(orderedPlan.Recipes, path => Assert.NotEmpty(SourceTerrain.Read(orderedWorkspace, path, Token).Surfaces));
        var orderedAfter = await SourceWorlds.BuildPreviewAsync(ordered.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(ordered.Project), "after"), orderedWorkspace.Overlay(), token: Token);
        async Task<WorldNode> Root(SourceWorldBuild build)
        {
            var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
            return Assert.Single(world.Nodes, n => n.Class == WorldNodeClass.World);
        }
        var originalRoot = await Root(orderedBefore); var convertedRoot = await Root(orderedAfter);
        foreach (var (x, expected) in new[] { (1f, new byte[] { 4, 3 }), (17f, new byte[] { 6, 5 }), (49f, new byte[] { 7, 6 }) })
        {
            var originalHits = ZoneProbe.Probe(originalRoot, x, 1, ZoneSet.Cleared, token: Token).Hits;
            var convertedHits = ZoneProbe.Probe(convertedRoot, x, 1, ZoneSet.Cleared, token: Token).Hits;
            Assert.Equal(expected, originalHits.Select(h => h.Zones.Id0));
            Assert.Equal(expected, convertedHits.Select(h => h.Zones.Id0));
        }
        orderedWorkspace.Undo(); Assert.False(orderedWorkspace.IsDirty);
    }

    [Fact]
    public async Task PiecesTakeTheZoneTheirGroupPassesDown()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteTerrainDatabase(grouped: true);
        const string Database = "data/m1/models/m1.gltf";
        // The group says zone 3 and its pieces say none, so the importer gives them zone 3: so must the surfaces.
        var json = JsonNode.Parse(File.ReadAllText(fixture.Path(Database)))!;
        foreach (var node in json["nodes"]!.AsArray())
        {
            string name = node!["name"]!.GetValue<string>();
            if (name == "g1") node["extras"]!["recoil"]!["zone"] = 3;
            else if (name.StartsWith("flat_", StringComparison.Ordinal)) node["extras"]!["recoil"]!.AsObject().Remove("zone");
        }
        fixture.Write(Database, json.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var before = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "zones"), workspace.Overlay(), token: Token);
        Assert.Null(before.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var plan = SourceTerrainConversion.Plan(workspace, Database, SourceTerrainConversion.References(workspace, before.Dependencies, $"gamegen/{before.Mission}.gs", Token), Token);
        Assert.Equal(3, plan.Converted);
        Assert.All(plan.Groups, g => Assert.Equal(3, g.Zone));
    }

    [Fact]
    public async Task PiecesInTheDatabasesGroupsConvertAndPartsStay()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteTerrainDatabase(grouped: true);
        SourceWorkspace workspace = new(fixture.Project);
        const string Database = "data/m1/models/m1.gltf";
        var before = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "before"), workspace.Overlay(), token: Token);
        Assert.Null(before.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var plan = SourceTerrainConversion.Plan(workspace, Database, SourceTerrainConversion.References(workspace, before.Dependencies, $"gamegen/{before.Mission}.gs", Token), Token);
        // The group's pieces convert; the part, whose file every copy of it shares, stays.
        Assert.Equal(["ground", "m1_01.flt", "sky"], plan.Kept.Select(k => k.Node));
        Assert.Equal("a part of the mission database in a file of its own", plan.Kept[1].Reason);
        Assert.Equal(3, plan.Converted);
        SourceTerrainConversion.Apply(workspace, plan, Token);
        Assert.DoesNotContain(workspace.DirtyFiles, f => f.EndsWith("m1_01.gltf", StringComparison.Ordinal));
        // The marker stays inside the transparent group, at its pieces' original position.
        var database = JsonNode.Parse(workspace.Read(Database, Token)!)!;
        var roots = database["scenes"]![0]!["nodes"]!.AsArray().Select(n => database["nodes"]![n!.GetValue<int>()]!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["ground", "g1", "m1_01.flt", "sky"], roots);
        var group = database["nodes"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "g1")!;
        Assert.Equal("m1_terrain", database["nodes"]![Assert.Single(group["children"]!.AsArray())!.GetValue<int>()]!["name"]!.GetValue<string>());
        var after = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "after"), workspace.Overlay(), token: Token);
        Assert.Null(after.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var a = await Nodes(before, p => p.Database && p.ModelFile == Database && plan.Groups.Any(g => g.Nodes.Contains(p.ModelNode)));
        Assert.Equal(["flat_a", "flat_b", "flat_over"], a.Select(n => n.Name).Order());
        var report = TerrainProbe.Compare(a, await Nodes(after, p => p.Terrain == plan.Recipe), 2, Token);
        AssertCompared(report);
        Assert.Equal(0, report.Mismatches); Assert.Equal(0, report.HeightOnly);
        Assert.Single(await Nodes(after, p => p.Part && p.ModelNodeName == "far"));
    }

    [Fact]
    public async Task ModelValuesTravelWithTheirPiecesAndBoundsDependentPiecesStay()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteTerrainDatabase();
        // flat_b is unlit (its own model flags); flat_over collides by its bounding box.
        string path = Path.Combine(fixture.Project, "data", "m1", "models", "m1.gltf");
        var gltf = JsonNode.Parse(File.ReadAllBytes(path))!;
        JsonObject Named(string name) => (JsonObject)gltf["nodes"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == name)!;
        var mesh = (JsonObject)gltf["meshes"]![Named("flat_b")["mesh"]!.GetValue<int>()]!;
        var meshExtras = mesh["extras"] as JsonObject ?? (JsonObject)(mesh["extras"] = new JsonObject());
        var values = meshExtras[WorldGltf.Key] as JsonObject ?? (JsonObject)(meshExtras[WorldGltf.Key] = new JsonObject());
        values["flags"] = 7;
        var over = Named("flat_over");
        var overExtras = over["extras"] as JsonObject ?? (JsonObject)(over["extras"] = new JsonObject());
        var recoil = overExtras[WorldGltf.Key] as JsonObject ?? (JsonObject)(overExtras[WorldGltf.Key] = new JsonObject());
        recoil["flags"] = $"{WorldGltf.DefaultCarried | 0x20:x8}";
        // A zone written as a whole float reads as the importer reads it.
        var a = Named("flat_a");
        ((JsonObject)a["extras"]![WorldGltf.Key]!)["zone"] = "THREE";
        // As Blender writes it: the literal text 3.0.
        fixture.Write("data/m1/models/m1.gltf", gltf.ToJsonString().Replace("\"zone\":\"THREE\"", "\"zone\":3.0"));
        SourceWorkspace workspace = new(fixture.Project);
        var before = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "before"), workspace.Overlay(), token: Token);
        var plan = SourceTerrainConversion.Plan(workspace, "data/m1/models/m1.gltf", SourceTerrainConversion.References(workspace, before.Dependencies, $"gamegen/{before.Mission}.gs", Token), Token);
        Assert.Contains(plan.Kept, k => k.Node == "flat_over" && k.Reason.Contains("bounding box"));
        // flat_a and flat_b no longer share a surface: their model values differ.
        Assert.Equal(2, plan.Groups.Count);
        var unlit = Assert.Single(plan.Groups, g => g.ModelValues != null && g.ModelValues["flags"]?.GetValue<int>() == 7);
        Assert.Contains("_m", unlit.Id);
        Assert.All(plan.Groups, g => Assert.Equal(3, g.Zone));
        SourceTerrainConversion.Apply(workspace, plan, Token);
        var after = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "after"), workspace.Overlay(), token: Token);
        Assert.Null(after.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var pieces = await Nodes(after, p => p.Terrain == plan.Recipe && p.TerrainSurface == unlit.Id);
        Assert.NotEmpty(pieces);
        Assert.All(pieces, n => Assert.Equal(7u, n.Model!.Flags));
        var report = TerrainProbe.Compare(await Nodes(before, p => p.Database && plan.Groups.Any(g => g.Nodes.Contains(p.ModelNode))), await Nodes(after, p => p.Terrain == plan.Recipe), 2, Token);
        AssertCompared(report);
        Assert.Equal(0, report.Mismatches);
    }

    private static void AssertCompared(TerrainProbeReport report)
    {
        Assert.True(report.Complete, report.Limitation ?? "The terrain comparison did not complete.");
        Assert.True(report.Samples > 0, "The terrain comparison took no samples.");
        Assert.True(report.Hits > 0, "The terrain comparison sampled no original terrain hits.");
    }

    private static async Task<List<WorldNode>> Nodes(SourceWorldBuild build, Func<WorldNodeProvenance, bool> select)
    {
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        var slots = GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken);
        return world.Nodes.Where(n => slots.TryGetValue(n, out int slot) && build.Provenance.TryGetValue(slot, out var p) && select(p)).ToList();
    }
}
