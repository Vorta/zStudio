using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class SourceTransformUpdateTests
{
    [Fact]
    public async Task ModelAlsoUsedByTerrainRequiresCompleteEvaluation()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new();
        const string model = "data/m1/models/m1.gltf";
        var document = JsonNode.Parse(System.IO.File.ReadAllBytes(fixture.Path(model)))!;
        var nodes = document["nodes"]!.AsArray(); int marker = nodes.Count;
        nodes.Add(new JsonObject { ["name"] = "terrain", ["extras"] = new JsonObject
            { ["recoil"] = new JsonObject { ["terrain"] = "shared.terrain.json" } } });
        document["scenes"]![0]!["nodes"]!.AsArray().Add(marker);
        fixture.Write(model, document.ToJsonString());
        zStudio.Core.Terrain.TerrainRecipe recipe = new(1,
            [new("surface", "m1.gltf", "ground", zStudio.Core.Terrain.TerrainAttributes.None)],
            zStudio.Core.Terrain.TerrainAttributes.None, []);
        fixture.Write("data/m1/models/shared.terrain.json", recipe.Write());
        SourceWorkspace workspace = new(fixture.Project);
        var source = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token);
        var ground = source.World.Nodes.Single(n => n.Name == "ground");
        int slot = GameZWriter.NodeSlots(source.World, token)[ground];
        var plan = SourceObjectEdits.PlanTransform(workspace, ground.Name, source.Provenance[slot], source.Executions,
            ObjectTransform.Of(ground) with { Position = new(0, 5, 0) }, token, mission: "m1", current: ObjectTransform.Of(ground),
            world: source.World, write: source.WriteInstruction);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        Assert.Null(SourceWorlds.TryUpdateTransforms(source, workspace.Overlay(), out string reason, token));
        Assert.Contains("baked terrain", reason);
        var evaluated = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token);
        var oldSlots = GameZWriter.NodeSlots(source.World, token);
        var oldTerrain = source.World.Nodes.First(n => source.Provenance[oldSlots[n]].Terrain != null);
        var newTerrain = evaluated.World.Nodes.Single(n => n.Name == oldTerrain.Name);
        Assert.Equal(oldTerrain.Model!.Vertices.Select(p => p + new Vector3(0, 5, 0)), newTerrain.Model!.Vertices);
        Assert.DoesNotContain(source.World.Models.SelectMany(m => m.Vertices), p => p.Y == 5);
    }

    [Fact]
    public async Task ReferencedTurretTransformAndHistoryMatchFullEvaluationWithoutReplacingGeometry()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new(); fixture.WriteReferencedTurretDatabase();
        SourceWorkspace workspace = new(fixture.Project);
        var original = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", workspace.Overlay(), token);
        var source = original; var originalBytes = GameZWriter.Write(original.World, token);
        var turret = source.World.Nodes.Single(n => n.Name == "tur_101");
        int slot = GameZWriter.NodeSlots(source.World, token)[turret];
        var current = ObjectTransform.Of(turret);
        var plan = SourceObjectEdits.PlanTransform(workspace, turret.Name, source.Provenance[slot], source.Executions,
            current with { Position = new(290, 17, 230), RotationDegrees = new(0, 30, 0), Scale = new(1.25f) }, token,
            mission: "m2", current: current, world: source.World, write: source.WriteInstruction);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        await Check();
        Assert.Equal(originalBytes, GameZWriter.Write(original.World, token));
        workspace.Undo(); await Check();
        Assert.Equal(originalBytes, GameZWriter.Write(source.World, token));
        workspace.Redo(); await Check();
        await workspace.SaveAsync(token);
        turret = source.World.Nodes.Single(n => n.Name == "tur_101"); current = ObjectTransform.Of(turret);
        plan = SourceObjectEdits.PlanTransform(workspace, turret.Name, source.Provenance[slot], source.Executions,
            current with { Position = current.Position + Vector3.UnitX }, token, mission: "m2", current: current,
            world: source.World, write: source.WriteInstruction);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        await Check(); workspace.Undo(); await Check(); workspace.Redo(); await Check();
        // Appearance and topology edits must use complete source evaluation.
        var changed = workspace.Overlay().ToDictionary(p => p.Key, p => p.Value);
        string path = "data/m2/models/m2.gltf";
        var json = JsonNode.Parse(changed[path])!;
        json["nodes"]![source.Provenance[slot].ModelNode]!["name"] = "renamed_turret";
        changed[path] = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        Assert.Null(SourceWorlds.TryUpdateTransforms(source, changed, out _, token));
        // A new unsaved script disappears from the overlay on Undo; there is no disk file to read.
        const string note = "gamegen/note.gs";
        workspace.Apply("Add note", [(note, System.Text.Encoding.UTF8.GetBytes("# pending note\n"))], token);
        var withNote = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", workspace.Overlay(), token);
        Assert.Contains(note, withNote.ReadHashes.Keys);
        workspace.Undo();
        Assert.Null(SourceWorlds.TryUpdateTransforms(withNote, workspace.Overlay(), out _, token));
        var withoutNote = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", workspace.Overlay(), token);
        Assert.DoesNotContain(note, withoutNote.ReadHashes.Keys);
        Assert.Equal(GameZWriter.Write(source.World, token), GameZWriter.Write(withoutNote.World, token));

        async Task Check()
        {
            var previous = source;
            var incremental = SourceWorlds.TryUpdateTransforms(previous, workspace.Overlay(), out string reason, token);
            Assert.True(incremental != null, reason); source = incremental!;
            var oracle = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", workspace.Overlay(), token);
            Assert.Equal(GameZWriter.Write(oracle.World, token), GameZWriter.Write(source.World, token));
            Assert.Same(previous.Resources, source.Resources); Assert.Same(previous.Animations, source.Animations);
            Assert.Same(previous.Textures, source.Textures);
            Assert.Equal(oracle.ReadHashes.OrderBy(p => p.Key), source.ReadHashes.OrderBy(p => p.Key));
            Assert.Equal(oracle.InputHashes.OrderBy(p => p.Key), source.InputHashes.OrderBy(p => p.Key));
            Assert.Equal(oracle.Inputs.OrderBy(p => p.Key), source.Inputs.OrderBy(p => p.Key));
            for (int i = 0; i < previous.Scene!.Models.Count; i++) Assert.Same(previous.Scene.Models[i], source.Scene!.Models[i]);
            for (int i = 0; i < previous.World.Models.Count; i++) Assert.Same(previous.World.Models[i], source.World.Models[i]);
            Assert.NotSame(previous.World.Nodes.Single(n => n.Name == "tur_101"), source.World.Nodes.Single(n => n.Name == "tur_101"));
        }
    }
}
