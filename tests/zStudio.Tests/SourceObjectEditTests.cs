using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

/// <summary>
/// World object edits in a source world: a built node knows where it came from, and an edit changes that source — the
/// glTF node of the mission database, or the script instruction that placed it — then the rebuilt world shows it.
/// </summary>
public sealed class SourceObjectEditTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(SourceSceneState Build, GameZWorld World)> BuildAsync(SourceWorldFixture fixture, SourceWorkspace workspace, string mission = "m2")
    {
        var build = await SourceWorlds.LoadSceneAsync(fixture.Project, mission, workspace.Overlay(), token: Token);
        var world = build.World;
        return (build.BuildState, world);
    }
    private static int Slot(GameZWorld world, string name) => world.Nodes.IndexOf(world.Nodes.Single(n => n.Name == name));

    [Fact]
    public async Task EveryBuiltNodeKnowsItsSource()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        // The ground is a node of the mission database; the tank's root is the node LoadGameGen created in m2.gs.
        var ground = build.Provenance[Slot(world, "ground")];
        Assert.Equal("data/m2/models/m2.gltf", ground.ModelFile); Assert.Equal(0, ground.ModelNode); Assert.True(ground.Database);
        Assert.Equal("LoadGameGen", ground.Load!.Command);
        var tank = build.Provenance[Slot(world, "tank")];
        Assert.Null(tank.ModelFile);
        Assert.Equal(("gamegen/m2.gs", "LoadGameGen"), (tank.Created!.Script, tank.Created.Command));
        Assert.Equal(1, build.Executions[(tank.Created.Script, tank.Created.Line)]);
        // The hull inside the tank model comes from the model file, not the database.
        var hull = build.Provenance[Slot(world, "hull")];
        Assert.Equal(fixture.Tank, hull.ModelFile); Assert.False(hull.Database);
        // The world node records the commands that set it up.
        var worldNode = build.Provenance[world.Nodes.FindIndex(n => n.Class == WorldNodeClass.World)];
        Assert.Equal("NewWorld", worldNode.Created!.Command);
        Assert.Equal("WorldPartition", worldNode.Writers["WorldPartition"].Command);
    }

    [Fact]
    public async Task MovingADatabaseObjectEditsItsGltfNode()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        var ground = build.Provenance[Slot(world, "ground")];
        ObjectTransform requested = new(new(10, 2, -30), new(0, 90, 0), Vector3.One);
        var plan = SourceObjectEdits.PlanTransform(workspace, "ground", ground, build.Executions, requested, Token);
        Assert.Equal("data/m2/models/m2.gltf", plan.Changes.Single().Relative);
        var node = JsonNode.Parse(plan.Changes.Single().Content)!["nodes"]![0]!;
        Assert.Null(node["matrix"]);
        Assert.Equal([10f, 2f, -30f], node["translation"]!.AsArray().Select(v => v!.GetValue<float>()));
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var (_, moved) = await BuildAsync(fixture, workspace);
        var local = WorldUpdate.LocalMatrix(moved.Nodes.Single(n => n.Name == "ground"))!.Value;
        var back = ObjectTransform.FromMatrix(local);
        Assert.Equal(requested.Position, back.Position);
        Assert.Equal(90, back.RotationDegrees.Y, 3); Assert.Equal(0, back.RotationDegrees.X, 3); Assert.Equal(0, back.RotationDegrees.Z, 3);
    }

    [Fact]
    public async Task MovingAScriptPlacedObjectEditsOrAddsItsInstructions()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        // A placed copy of the tank: LoadGameGen, Object3DTranslate, FindNode and AddChild.
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var origin = build.Provenance[Slot(world, "tank_at")];
        Assert.Equal("Object3DTranslate", origin.Writers["Object3DTranslate"].Command);
        string before = Encoding.Latin1.GetString(workspace.Read("gamegen/m1.gs", Token)!);
        // A new position changes the translation tokens; a rotation adds an Object3DRotate after it.
        var plan = SourceObjectEdits.PlanTransform(workspace, "tank_at", origin, build.Executions, new(new(110, 5, -50), new(0, 45, 0), Vector3.One), Token);
        string after = Encoding.Latin1.GetString(plan.Changes.Single().Content);
        Assert.Equal(before.Replace("Object3DTranslate 100.0 0.0 -50.0\r\n", "Object3DTranslate 110.0 5.0 -50.0\r\nObject3DRotate 0.0 45.0 0.0\r\n"), after);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var (rebuilt, moved) = await BuildAsync(fixture, workspace, "m1");
        var transform = ObjectTransform.FromMatrix(WorldUpdate.LocalMatrix(moved.Nodes.Single(n => n.Name == "tank_at"))!.Value);
        Assert.Equal(new Vector3(110, 5, -50), transform.Position); Assert.Equal(45, transform.RotationDegrees.Y, 3);
        // The rotation instruction now exists, so the next edit changes it in place.
        var again = SourceObjectEdits.PlanTransform(workspace, "tank_at", rebuilt.Provenance[Slot(moved, "tank_at")], rebuilt.Executions, new(new(110, 5, -50), new(0, 90, 0), Vector3.One), Token);
        Assert.Equal(after.Replace("Object3DRotate 0.0 45.0 0.0", "Object3DRotate 0.0 90.0 0.0"), Encoding.Latin1.GetString(again.Changes.Single().Content));
    }

    [Fact]
    public async Task FlagsChangeInTheGltfExtrasOrTheScriptCommandThatSetThem()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        var ground = build.Provenance[Slot(world, "ground")];
        // CanModify on the database ground: the glTF node's carried flags.
        var plan = SourceObjectEdits.PlanFlag(workspace, world.Nodes.Single(n => n.Name == "ground"), ground, build.Executions, 0x10000, true, Token);
        var recoil = JsonNode.Parse(plan.Changes.Single().Content)!["nodes"]![0]!["extras"]!["recoil"]!;
        Assert.Equal("canModify", recoil["declient"]!.GetValue<string>());
        Assert.Null(recoil["flags"]);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var (_, flagged) = await BuildAsync(fixture, workspace);
        Assert.NotEqual(0u, flagged.Nodes.Single(n => n.Name == "ground").Flags & 0x10000);
        // Switching mode clears the previous bit atomically; undo restores it, without changing the zone gate.
        var (b2, w2) = await BuildAsync(fixture, workspace);
        var clip = SourceObjectEdits.PlanFlag(workspace, w2.Nodes.Single(n => n.Name == "ground"), b2.Provenance[Slot(w2, "ground")], b2.Executions, 0x20000, true, Token);
        workspace.Apply(clip.Label, clip.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var (b3, w3) = await BuildAsync(fixture, workspace);
        Assert.Equal(0x20000u, w3.Nodes.Single(n => n.Name == "ground").Flags & 0x30000u);
        workspace.Undo();
        var (_, undone) = await BuildAsync(fixture, workspace);
        Assert.Equal(0x10000u, undone.Nodes.Single(n => n.Name == "ground").Flags & 0x30000u);
        workspace.Redo();
        var clear = SourceObjectEdits.PlanFlag(workspace, w3.Nodes.Single(n => n.Name == "ground"), b3.Provenance[Slot(w3, "ground")], b3.Executions, 0x20000, false, Token);
        var cleared = JsonNode.Parse(clear.Changes.Single().Content)!["nodes"]![0]!["extras"]!["recoil"]!.AsObject();
        Assert.False(cleared.ContainsKey("declient")); Assert.False(cleared.ContainsKey("flags"));
        Assert.Equal(WorldGltf.DefaultCarried, WorldGltf.StoredNodeFlags(cleared));
        workspace.Apply(clear.Label, clear.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        workspace.Save(Token);
        var (_, reopened) = await BuildAsync(fixture, new(fixture.Project));
        Assert.Equal(0u, reopened.Nodes.Single(n => n.Name == "ground").Flags & 0x30000u);
        // A flag of a script-created node is set by a new instruction after the one that created it.
        var tank = build.Provenance[Slot(world, "tank")];
        var scripted = SourceObjectEdits.PlanFlag(workspace, world.Nodes.Single(n => n.Name == "tank"), tank, build.Executions, 0x10, false, Token);
        Assert.Contains("LoadGameGen tank.gltf tank\r\nSetIntersectSurface off\r\n", Encoding.Latin1.GetString(scripted.Changes.Single().Content));
    }

    [Fact]
    public async Task DeclientModeTransitionsReconcileModelAndScriptWriters()
    {
        // Editing only the final writer used to leave the opposite bit active in the model or re-enabled by the script.
        foreach (bool toModify in new[] { true, false })
        {
            using SourceWorldFixture fixture = new();
            const string model = "data/m2/models/m2.gltf", script = "gamegen/m2.gs";
            var json = JsonNode.Parse(File.ReadAllBytes(fixture.Path(model)))!;
            json["nodes"]![0]!["extras"]![WorldGltf.Key]!["declient"] = toModify ? "clipTo" : "canModify";
            fixture.Write(model, json.ToJsonString());
            fixture.Write(script, File.ReadAllText(fixture.Path(script)).Replace("GameZWriteZBDFile",
                $"FindNode ground\r\nNodeSetCanModify {(toModify ? "off" : "on")}\r\nGameZWriteZBDFile", StringComparison.Ordinal));
            SourceWorkspace workspace = new(fixture.Project);
            var (build, world) = await BuildAsync(fixture, workspace);
            var ground = world.Nodes.Single(n => n.Name == "ground");
            var plan = SourceObjectEdits.PlanFlag(workspace, ground, build.Provenance[Slot(world, "ground")], build.Executions,
                toModify ? 0x10000u : 0x20000u, true, Token, "m2", null, world, build.WriteInstruction);
            workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
            var (_, after) = await BuildAsync(fixture, workspace);
            Assert.Equal(toModify ? 0x10000u : 0x20000u, after.Nodes.Single(n => n.Name == "ground").Flags & 0x30000u);
            workspace.Undo();
            var (_, undone) = await BuildAsync(fixture, workspace);
            Assert.Equal(ground.Flags & 0x30000u, undone.Nodes.Single(n => n.Name == "ground").Flags & 0x30000u);
            workspace.Redo(); workspace.Save(Token);
            var (_, saved) = await BuildAsync(fixture, new(fixture.Project));
            Assert.Equal(toModify ? 0x10000u : 0x20000u, saved.Nodes.Single(n => n.Name == "ground").Flags & 0x30000u);
        }
    }

    [Fact]
    public async Task SharedClipToEditRefusesAnotherMissionsCanModifyWithoutChangingSourcesOrHistory()
    {
        using SourceWorldFixture fixture = new();
        const string firstScript = "gamegen/m1.gs", secondScript = "gamegen/m2.gs";
        fixture.Write(firstScript, File.ReadAllText(fixture.Path(firstScript)).Replace("GameZWriteZBDFile",
            "SetModelDirectory ..\\data\\m2\\models\\bft\r\nLoadGameGen tank.gltf shared_tank\r\nGameZWriteZBDFile", StringComparison.Ordinal));
        string second = File.ReadAllText(fixture.Path(secondScript)).Replace("GameZWriteZBDFile",
            "FindNode hull\r\nNodeSetCanModify on\r\nGameZWriteZBDFile", StringComparison.Ordinal);
        fixture.Write(secondScript, second);
        byte[] modelBefore = File.ReadAllBytes(fixture.Path(fixture.Tank));
        byte[] firstBefore = File.ReadAllBytes(fixture.Path(firstScript));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        SourceEditPlan Plan()
        {
            var hull = world.Nodes.Single(n => n.Name == "hull");
            return SourceObjectEdits.PlanFlag(workspace, hull, build.Provenance[Slot(world, "hull")], build.Executions,
                0x20000, true, Token, "m1", null, world, build.WriteInstruction);
        }
        var error = Assert.Throws<InvalidDataException>(() => Plan());
        Assert.Contains("m2", error.Message);
        Assert.False(workspace.IsDirty); Assert.Equal(0, workspace.Revision); Assert.Equal(0, workspace.UndoCount);
        Assert.Equal(modelBefore, workspace.Read(fixture.Tank, Token)); Assert.Equal(firstBefore, workspace.Read(firstScript, Token));
        Assert.Equal(second, Encoding.ASCII.GetString(workspace.Read(secondScript, Token)!));

        // Once that mission explicitly leaves CanModify off, the exact same shared edit can be prepared and saved.
        workspace.Apply("Clear competing mode", [(secondScript, (byte[]?)Encoding.ASCII.GetBytes(second.Replace("NodeSetCanModify on", "NodeSetCanModify off", StringComparison.Ordinal)))], Token);
        var allowed = Plan();
        workspace.Apply(allowed.Label, allowed.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        foreach (string mission in new[] { "m1", "m2" })
        {
            var (_, after) = await BuildAsync(fixture, workspace, mission);
            Assert.Equal(0x20000u, after.Nodes.Single(n => n.Name == "hull").Flags & 0x30000u);
        }
        workspace.Save(Token);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void InstructionsThatRunMoreThanOnceOrUseMacrosAreRefused()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceInstruction translate = new("gamegen/m1.gs", 3, "Object3DTranslate", ["Object3DTranslate", "1.0", "2.0", "3.0"], ["1.0", "2.0", "3.0"]);
        WorldNodeProvenance origin = new();
        origin.Writers["Object3DTranslate"] = translate;
        var twice = new Dictionary<(string, int), int> { [("gamegen/m1.gs", 3)] = 2 };
        Assert.Contains("runs 2 times", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "x", origin, twice, new(new(5, 5, 5), Vector3.Zero, Vector3.One), Token)).Message);
        origin.Writers["Object3DTranslate"] = translate with { Tokens = ["Object3DTranslate", "%x%", "2.0", "3.0"] };
        var once = new Dictionary<(string, int), int> { [("gamegen/m1.gs", 3)] = 1 };
        Assert.Contains("macro", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "x", origin, once, new(new(5, 5, 5), Vector3.Zero, Vector3.One), Token)).Message);
        Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, "x", origin, once, new(new(float.NaN, 0, 0), Vector3.Zero, Vector3.One), Token));
    }

    [Fact]
    public void TransformsRoundTripThroughTheEngineMatrix()
    {
        foreach (var rotation in new Vector3[] { new(10, 20, 30), new(-45, 170, 5), new(0, -90, 0), new(30, 0, -60) })
        {
            ObjectTransform t = new(new(1, 2, 3), rotation, new(1, 2, 0.5f));
            var back = ObjectTransform.FromMatrix(t.Matrix());
            Assert.Equal(t.Position, back.Position);
            for (int i = 0; i < 3; i++) { Assert.Equal(t.RotationDegrees[i], back.RotationDegrees[i], 2); Assert.Equal(t.Scale[i], back.Scale[i], 4); }
        }
    }

    [Fact]
    public void ScriptTokensAndLinesChangeLosslessly()
    {
        string text = "# header\r\nset a 1\r\n  LoadGameGen x.gltf x   # trailing\r\nObject3DTranslate 1.0,2.0 3.0\r\nQuit\r\n";
        var syntax = GameGenScriptSyntax.Parse(text, TestContext.Current.CancellationToken);
        Assert.Equal("\r\n", syntax.Newline);
        Assert.Equal(["Object3DTranslate", "1.0", "2.0", "3.0"], syntax.Line(4).Tokens);
        string replaced = syntax.ReplaceTokens(4, new Dictionary<int, string> { [1] = "7.5", [3] = "-1.0" }, TestContext.Current.CancellationToken);
        Assert.Equal(text.Replace("1.0,2.0 3.0", "7.5,2.0 -1.0"), replaced);
        string inserted = GameGenScriptSyntax.Parse(replaced, TestContext.Current.CancellationToken).InsertLines(4, [["Object3DRotate", "0.0", "90.0", "0.0"]], TestContext.Current.CancellationToken);
        Assert.Contains("# trailing\r\nObject3DRotate 0.0 90.0 0.0\r\nObject3DTranslate", inserted);
        Assert.StartsWith("#   LoadGameGen", GameGenScriptSyntax.Parse(text, TestContext.Current.CancellationToken).CommentOut(3, TestContext.Current.CancellationToken)[(text.IndexOf("  LoadGameGen", StringComparison.Ordinal))..]);
        Assert.Throws<InvalidDataException>(() => syntax.ReplaceTokens(4, new Dictionary<int, string> { [1] = "two words" }, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => syntax.ReplaceTokens(1, new Dictionary<int, string> { [0] = "x" }, TestContext.Current.CancellationToken));
        // A file without a final newline gets one before an appended line.
        Assert.Equal("Quit\nNewWorld w\n", GameGenScriptSyntax.Parse("Quit", TestContext.Current.CancellationToken).InsertLines(2, [["NewWorld", "w"]], TestContext.Current.CancellationToken).Replace("\r\n", "\n"));
    }
}
