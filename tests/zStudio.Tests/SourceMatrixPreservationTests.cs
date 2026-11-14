using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

// Data-integrity regression: editing a matrix must not silently change its source representation or basis.
public sealed class SourceMatrixPreservationTests
{
    [Fact]
    public async Task MatrixObjectsKeepTheirRepresentationThroughPlacementAndStructuralEdits()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new(); fixture.WriteNestedDatabase();
        const string file = "data/m1/models/m1.gltf";
        var root = JsonNode.Parse(File.ReadAllBytes(fixture.Path(file)))!;
        var crate = root["nodes"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "crate")!;
        crate["matrix"] = JsonNode.Parse("[-1,0,0,0,0,0.60000000000000001,0.8,0,0,0.8,-0.6,0,10,2,-30,1]");
        fixture.Write(file, root.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var source = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token);
        int index = Target().Origin.ModelNode;
        var before = root.DeepClone();
        var current = ObjectTransform.Of(Target().Node);
        await Apply(SourceObjectEdits.PlanTransform(workspace, "crate", Target().Origin, source.Executions,
            current with { Position = new(15, 3, -20) }, token, "m1", current));
        var expected = before.DeepClone();
        var values = expected["nodes"]![index]!["matrix"]!.AsArray();
        values[12] = 15; values[13] = 3; values[14] = -20;
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(workspace.Read(file, token)!)));
        AssertMatrix("crate");

        current = ObjectTransform.Of(Target().Node);
        await Apply(SourceObjectEdits.PlanTransform(workspace, "crate", Target().Origin, source.Executions,
            current with { RotationDegrees = new(10, 25, 5), Scale = new(2, 3, 4) }, token, "m1", current));
        AssertMatrix("crate");
        var placed = WorldUpdate.LocalMatrix(Target().Node)!.Value;
        await Apply(SourceObjectEdits.PlanDuplicate(Target(), "crate_copy", ObjectTransform.Of(Target().Node) with { Position = new(50, 4, -10) }, token, keepBasis: true));
        AssertMatrix("crate_copy");
        Assert.Equal(placed with { Translation = new(50, 4, -10) }, WorldUpdate.LocalMatrix(source.World.Nodes.Single(n => n.Name == "crate_copy")));

        var parent = source.World.Nodes.Single(n => n.Name == "ground");
        await Apply(SourceObjectEdits.PlanReparent(Target(), parent, token));
        AssertMatrix("crate");
        Assert.Equal(placed, WorldUpdate.LocalMatrix(Target().Node));
        await Apply(SourceObjectEdits.PlanPatch(Target(), new() { Reparent = true, Parent = null, Position = new(21, 5, -10) }, token));
        AssertMatrix("crate");
        Assert.Equal(placed with { Translation = new(21, 5, -10) }, WorldUpdate.LocalMatrix(Target().Node));

        // Even an identity result keeps an explicitly authored matrix.
        current = ObjectTransform.Of(Target().Node);
        await Apply(SourceObjectEdits.PlanTransform(workspace, "crate", Target().Origin, source.Executions,
            ObjectTransform.Identity, token, "m1", current));
        AssertMatrix("crate");
        Assert.Equal(Matrix4x4.Identity, GltfNodeEdits.Local((JsonObject)JsonNode.Parse(workspace.Read(file, token)!)!["nodes"]![index]!));
        workspace.Undo(); workspace.Redo(); AssertMatrix("crate");
        await workspace.SaveAsync(token);
        AssertMatrix("crate");
        var reopened = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: token);
        Assert.Equal(GameZWriter.Write(source.World, token), GameZWriter.Write(reopened.World, token));

        SourceObjectTarget Target() => new(workspace, "m1", source.World, source.World.Nodes.Single(n => n.Name == "crate"),
            GameZWriter.NodeSlots(source.World, token).ToDictionary(p => p.Key, p => source.Provenance[p.Value]), source.Executions) { Write = source.WriteInstruction };
        async Task Apply(SourceEditPlan plan)
        {
            workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
            source = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token);
        }
        void AssertMatrix(string name)
        {
            var node = JsonNode.Parse(workspace.Read(file, token)!)!["nodes"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == name)!;
            Assert.Equal(16, Assert.IsType<JsonArray>(node["matrix"]).Count);
            foreach (string field in new[] { "translation", "rotation", "scale" }) Assert.Null(node[field]);
        }
    }
}
