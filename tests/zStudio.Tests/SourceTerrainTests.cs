using System.IO;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>A terrain recipe in a source project: the mission database names it, and the world build compiles its pieces.</summary>
public sealed class SourceTerrainTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TexturedTerrainWithoutUvsIsRefusedBeforePublishingPieces()
    {
        using var fixture = Fixture(); const string model = "data/m1/models/coast.gltf";
        var json = JsonNode.Parse(File.ReadAllBytes(fixture.Path(model)))!;
        json["meshes"]![0]!["primitives"]![0]!["attributes"]!.AsObject().Remove("TEXCOORD_0");
        fixture.Write(model, json.ToJsonString());
        string output = Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "missing-uv");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", output, token: Token));
        Assert.Contains("TEXCOORD_0", error.Message);
        Assert.False(File.Exists(Path.Combine(output, "m1", "gamez.zbd")));
    }

    [Fact]
    public void TerrainMarkerRefusesOversizedJsonBeforeReadingOrBuildingItsDom()
    {
        using var fixture = Fixture(); const string database = "data/m1/models/m1.gltf";
        byte[] original = File.ReadAllBytes(fixture.Path(database));
        var root = JsonNode.Parse(original)!; root["ignored"] = new string('x', 16 * 1024 * 1024);
        fixture.Write(database, root.ToJsonString()); SourceWorkspace workspace = new(fixture.Project);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, database, "data/m1/models/coast.gltf", ["land"], "data/m1/models/new.terrain.json", Token));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1_000_000);
        Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo);
        fixture.Write(database, original);
        Assert.NotNull(SourceTerrain.Create(workspace, database, "data/m1/models/coast.gltf", ["land"], "data/m1/models/new.terrain.json", Token));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    public void CreatingTerrainUsesTheExactlyIntegralSelectedScene(string index)
    {
        using var fixture = Fixture();
        const string databasePath = "data/m1/models/m1.gltf";
        var database = JsonNode.Parse(File.ReadAllText(fixture.Path(databasePath)))!.AsObject();
        var scenes = database["scenes"]!.AsArray();
        scenes.Add(scenes[0]!.DeepClone());
        database["scene"] = JsonNode.Parse(index);
        int before = scenes[0]!["nodes"]!.AsArray().Count;
        fixture.Write(databasePath, database.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        SourceTerrain.Create(workspace, databasePath, "data/m1/models/coast.gltf", ["land"], "data/m1/models/new.terrain.json", Token);
        var saved = JsonNode.Parse(workspace.Read(databasePath, Token)!)!;
        Assert.Equal(before, saved["scenes"]![0]!["nodes"]!.AsArray().Count);
        Assert.Equal(before + 1, saved["scenes"]![1]!["nodes"]!.AsArray().Count);
        workspace.Undo();
        Assert.Equal(database.ToJsonString(), System.Text.Encoding.UTF8.GetString(workspace.Read(databasePath, Token)!));
    }
    private const string Recipe = """
        { "format": "recoil-terrain", "version": 1, "compiler": 1,
          "surfaces": [ { "id": "land", "model": "coast.gltf", "node": "land", "defaults": { "craters": "allowed" } } ],
          "defaults": { "zones": [1] },
          "regions": [ { "name": "road", "shape": { "polygons": [ { "outer": [[240, 240], [260, 240], [260, 260], [240, 260]] } ] }, "set": { "craters": "blocked", "zones": [1, 2] } } ] }
        """;

    /// <summary>m1 with a 100 × 100 surface crossing both of its grid's cell lines, included by a terrain marker among the database's roots.</summary>
    private static SourceWorldFixture Fixture()
    {
        SourceWorldFixture fixture = new();
        WorldTexture rock = new("rock");
        ModelBuilder builder = new();
        builder.Add(new([new(200, 0, 300), new(300, 0, 300), new(300, 0, 200), new(200, 0, 200)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], new() { Texture = rock, Flags = 0x1FF }));
        WorldNode land = new("land", WorldNodeClass.Object3D) { Model = builder.Finish(), Flags = WorldGltf.DefaultCarried };
        land.SetPayloadInt(0, 0x28);
        var (json, bin) = WorldGltf.Export([land], 0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 0) }).Write("coast.bin");
        fixture.Write("data/m1/models/coast.gltf", json); fixture.Write("data/m1/models/coast.bin", bin);
        fixture.Write("data/m1/models/coast.terrain.json", Recipe);
        // The database keeps its ground and gains the marker after it.
        var database = JsonNode.Parse(File.ReadAllText(fixture.Path("data/m1/models/m1.gltf")))!.AsObject();
        var nodes = database["nodes"]!.AsArray();
        nodes.Add(new JsonObject { ["name"] = "terrain", ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["terrain"] = "coast.terrain.json" } } });
        database["scenes"]![0]!["nodes"]!.AsArray().Add(nodes.Count - 1);
        fixture.Write("data/m1/models/m1.gltf", database.ToJsonString());
        return fixture;
    }

    [Fact]
    public async Task TheDatabaseMarkerBecomesPiecesInTheirGridCells()
    {
        using var fixture = Fixture();
        SourceWorkspace workspace = new(fixture.Project);
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "preview"), workspace.Overlay(), token: Token);
        Assert.Null(build.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        var worldNode = world.Nodes.Single(n => n.Class == WorldNodeClass.World);
        var pieces = world.Nodes.Where(n => n.Name.StartsWith("coast_land_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(8, pieces.Length);
        Assert.DoesNotContain(world.Nodes, n => n.Name == "terrain");
        Assert.Contains(world.Nodes, n => n.Name == "ground");
        foreach (var piece in pieces)
        {
            // Each piece is a world child in the cell its name gives (columns then rows), never the overflow list.
            Assert.Contains(worldNode, piece.Parents);
            int column = int.Parse(piece.Name.AsSpan(11, 2)), row = int.Parse(piece.Name.AsSpan(14, 2));
            Assert.Contains(piece, worldNode.Areas[row * 2 + column].Nodes);
            bool road = (piece.Flags & 0x20000) != 0;
            Assert.Equal(road ? 0xFFu : 1u, piece.Zone & 0xFF);
            Assert.Equal(road ? 0u : 0x10000u, piece.Flags & 0x10000);
            Assert.All(piece.Model!.Polygons, p => Assert.Equal(road ? 0xFF020102u : 0xFFFF0101u, p.Zone));
            Assert.Equal("rock", piece.Model.Polygons[0].Material!.Texture!.Name);
        }
        // The pieces know their recipe; the build depends on it and on the surface file.
        var slots = GameZWriter.NodeSlots(world);
        var origin = build.Provenance[slots[pieces[0]]];
        Assert.Equal("data/m1/models/coast.terrain.json", origin.Terrain);
        Assert.Equal("land", origin.TerrainSurface);
        Assert.Contains("data/m1/models/coast.terrain.json", build.Dependencies);
        Assert.Contains("data/m1/models/coast.gltf", build.Dependencies);
    }

    [Fact]
    public async Task RecipeEditsInTheWorkspaceRebuildThePiecesAndPiecesAreNotEditedDirectly()
    {
        using var fixture = Fixture();
        SourceWorkspace workspace = new(fixture.Project);
        // Widening the road in the workspace: the next build uses the pending recipe.
        workspace.Apply("Widen road", [("data/m1/models/coast.terrain.json", System.Text.Encoding.UTF8.GetBytes(Recipe.Replace("[260, 240], [260, 260]", "[280, 240], [280, 260]")))], Token);
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "preview"), workspace.Overlay(), token: Token);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        var road = world.Nodes.Where(n => n.Name.StartsWith("coast_land_", StringComparison.Ordinal) && (n.Flags & 0x20000) != 0).ToArray();
        float area = 0;
        foreach (var node in road)
            foreach (var polygon in node.Model!.Polygons)
            {
                var v = polygon.Vertices.Select(i => node.Model.Vertices[i]).ToArray();
                System.Numerics.Vector3 sum = default;
                for (int i = 1; i + 1 < v.Length; i++) sum += System.Numerics.Vector3.Cross(v[i] - v[0], v[i + 1] - v[0]);
                area += sum.Length() / 2;
            }
        Assert.Equal(40f * 20f, area, 0.5f);
        // A piece's own transform and flags are not sources: the planner points at the recipe.
        var slots = GameZWriter.NodeSlots(world);
        var origin = build.Provenance[slots[road[0]]];
        var refused = Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanFlag(workspace, road[0].Name, origin, build.Executions, 0x10, false, Token));
        Assert.Contains("terrain recipe", refused.Message);
    }

    [Fact]
    public async Task AMirroredSurfaceKeepsFacingAsAuthoredAndCreateUsesEngineNames()
    {
        using var fixture = Fixture();
        // The land is placed through a mirroring matrix (x → 500 − x), which keeps it over the same area.
        var coast = JsonNode.Parse(File.ReadAllText(fixture.Path("data/m1/models/coast.gltf")))!;
        coast["nodes"]![0]!["matrix"] = new JsonArray(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 500, 0, 0, 1);
        // Blender's copy suffix: the engine name is still land.
        coast["nodes"]![0]!["name"] = "land.001";
        fixture.Write("data/m1/models/coast.gltf", coast.ToJsonString());
        // Two nodes sharing an engine name pick neither; extras that are not an object do not break the names.
        var nodes = coast["nodes"]!.AsArray();
        nodes.Add(new JsonObject { ["name"] = "rock", ["mesh"] = 0 }); nodes.Add(new JsonObject { ["name"] = "rock.002", ["mesh"] = 0 });
        nodes.Add(new JsonObject { ["name"] = "odd", ["mesh"] = 0, ["extras"] = new JsonObject { ["recoil"] = "x" } });
        foreach (int added in new[] { nodes.Count - 3, nodes.Count - 2, nodes.Count - 1 }) coast["scenes"]![0]!["nodes"]!.AsArray().Add(added);
        fixture.Write("data/m1/models/coast.gltf", coast.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var candidates = SourceTerrain.MeshNodes(workspace, "data/m1/models/coast.gltf", Token);
        Assert.Contains("land", candidates); Assert.Contains("odd", candidates); Assert.DoesNotContain("rock", candidates);
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "preview"), workspace.Overlay(), token: Token);
        Assert.Null(build.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        var pieces = world.Nodes.Where(n => n.Name.StartsWith("coast_land_", StringComparison.Ordinal)).ToArray();
        // The probe finds the land facing up wherever it lies.
        foreach (var (x, z) in new[] { (210f, 210f), (290f, 290f), (250f, 230f) })
            Assert.Equal([0f], Recoil.Zbd.Core.Terrain.TerrainProbe.At(pieces, x, z).Select(h => h.Height));
    }

    [Fact]
    public async Task AFacadeMeshIsNotATerrainSurface()
    {
        using var fixture = Fixture();
        var coast = JsonNode.Parse(File.ReadAllText(fixture.Path("data/m1/models/coast.gltf")))!;
        var mesh = (JsonObject)coast["meshes"]![0]!;
        if (mesh["extras"] is not JsonObject extras) mesh["extras"] = extras = new JsonObject();
        if (extras["recoil"] is not JsonObject recoil) extras["recoil"] = recoil = new JsonObject();
        recoil["mode"] = 1;
        fixture.Write("data/m1/models/coast.gltf", coast.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "preview"), workspace.Overlay(), token: Token));
        Assert.Contains("facade or point model", refused.Message);
    }

    [Fact]
    public async Task NothingMovesUnderATerrainPiece()
    {
        using var fixture = Fixture();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "preview"), workspace.Overlay(), token: Token);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        Dictionary<WorldNode, WorldNodeProvenance> provenance = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, slot) in GameZWriter.NodeSlots(world)) if (build.Provenance.TryGetValue(slot, out var origin)) provenance[node] = origin;
        var tank = world.Nodes.Single(n => n.Name == "tank_at");
        SourceObjectTarget target = new(workspace, "m1", world, tank, provenance, build.Executions) { Write = build.WriteInstruction };
        // A piece's name is a build label: a script line finding it would break when the recipe changes.
        var piece = world.Nodes.First(n => n.Name.StartsWith("coast_land_", StringComparison.Ordinal));
        Assert.Contains("is a terrain piece", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(target, piece, Token)).Message);
    }

    [Fact]
    public async Task TerrainOutsideTheDatabaseOrWithoutAGridIsRefused()
    {
        using var fixture = Fixture();
        // m2's world script loads the tank model after the database; a recipe marker inside a model file is refused.
        var tank = JsonNode.Parse(File.ReadAllText(fixture.Path(fixture.Tank)))!.AsObject();
        tank["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "terrain", ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["terrain"] = "../coast.terrain.json" } } });
        tank["scenes"]![0]!["nodes"]!.AsArray().Add(tank["nodes"]!.AsArray().Count - 1);
        fixture.Write(fixture.Tank, tank.ToJsonString());
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m2", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "preview"), null, token: Token));
        Assert.Contains("mission database", failure.Message);
    }
}
