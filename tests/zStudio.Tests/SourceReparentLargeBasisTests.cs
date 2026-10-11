using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceReparentLargeBasisTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string File = "data/m1/models/m1.gltf";

    [Fact]
    public void IllConditionedParentRefusesBeforeMutationAndSameWorkspaceCanReparentAccurately()
    {
        using SourceWorldFixture fixture = new();
        var root = JsonNode.Parse(System.IO.File.ReadAllBytes(fixture.Path(File)))!.AsObject();
        var nodes = root["nodes"]!.AsArray();
        nodes[0]!["scale"] = new JsonArray(1e20f, 1e20f, 1e20f);
        int bad = Add("bad", new(1, 1, 0, 0, 1, 1.0000001f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1));
        int good = Add("good", Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationY(0.4f));
        fixture.Write(File, root.ToJsonString());
        byte[] originalBytes = System.IO.File.ReadAllBytes(fixture.Path(File));
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var world = assembler.Assemble("m1.gs");
        Assert.NotEmpty(GameZWriter.Write(world, Token));
        var child = world.Nodes.Single(n => n.Name == "ground");
        SourceObjectTarget target = new(workspace, "m1", world, child, assembler.Provenance, assembler.Executions);
        Assert.Contains("precisely", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(target, world.Nodes.Single(n => n.Name == "bad"), token: Token)).Message);
        Assert.False(workspace.IsDirty);
        Assert.Equal(originalBytes, System.IO.File.ReadAllBytes(fixture.Path(File)));
        var unchanged = root.DeepClone();
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Reparent(root, 0, bad, token: Token));
        Assert.True(JsonNode.DeepEquals(unchanged, root)); // Shared helper also validates before mutating its DOM.

        var plan = SourceObjectEdits.PlanReparent(target, world.Nodes.Single(n => n.Name == "good"), token: Token);
        Assert.NotEmpty(plan.Changes);
        var updated = JsonNode.Parse(plan.Changes.Single(c => c.Relative == File).Content)!.AsObject();
        var expected = GltfNodeEdits.World(root, 0, Token);
        Near(expected, GltfNodeEdits.World(updated, 0, Token));
        Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
        WorldAssembler rebuild = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
        Assert.NotEmpty(GameZWriter.Write(rebuild.Assemble("m1.gs"), Token));
        Assert.Equal(good, GltfNodeEdits.Parent(updated, 0, Token));

        int Add(string name, Matrix4x4 matrix)
        {
            int index = nodes.Count;
            JsonObject node = new() { ["name"] = name };
            GltfNodeEdits.SetLocal(node, matrix);
            nodes.Add(node); root["scenes"]![0]!["nodes"]!.AsArray().Add(index);
            return index;
        }
    }

    [Fact]
    public void NonfiniteDerivedLocalAndNonfiniteSetLocalRefuseBeforeChangingJson()
    {
        JsonObject root = new()
        {
            ["nodes"] = new JsonArray(new JsonObject { ["scale"] = new JsonArray(1e30f, 1e30f, 1e30f) }, new JsonObject { ["scale"] = new JsonArray(1e-10f, 1e-10f, 1e-10f) }),
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0, 1) }), ["scene"] = 0
        };
        var before = root.DeepClone();
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Reparent(root, 0, 1, token: Token));
        Assert.True(JsonNode.DeepEquals(before, root));
        JsonObject node = new() { ["translation"] = new JsonArray(1, 2, 3) };
        var original = node.DeepClone();
        var invalid = Matrix4x4.Identity; invalid.M11 = float.PositiveInfinity;
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.SetLocal(node, invalid));
        Assert.True(JsonNode.DeepEquals(original, node));
        var large = Matrix4x4.CreateScale(-1e20f, 2e20f, 3e20f) * Matrix4x4.CreateRotationZ(0.4f);
        GltfNodeEdits.SetLocal(node, large);
        Near(large, GltfNodeEdits.Local(node));
    }

    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        for (int row = 0; row < 4; row++)
        {
            double magnitude = ObjectTransform.Length(new(expected[row, 0], expected[row, 1], expected[row, 2]));
            for (int col = 0; col < 4; col++)
            {
                Assert.True(float.IsFinite(actual[row, col]));
                Assert.True(Math.Abs((double)actual[row, col] - expected[row, col]) <= 1e-4 * Math.Max(1, magnitude));
            }
        }
    }
}
