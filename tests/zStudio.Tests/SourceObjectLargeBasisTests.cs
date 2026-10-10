using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceObjectLargeBasisTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1e20f)]
    [InlineData(-1e20f)]
    public void FiniteLargeAuthoredScaleRemainsInspectableAndCanRotate(float scale)
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/models/m1.gltf";
        var json = JsonNode.Parse(File.ReadAllBytes(fixture.Path(path)))!;
        json["nodes"]![0]!["scale"] = new JsonArray(scale, 1f, 1f);
        fixture.Write(path, json.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var world = assembler.Assemble("m1.gs");
        Assert.NotEmpty(GameZWriter.Write(world, token: Token));
        var node = world.Nodes.Single(n => n.Name == "ground");
        var shown = ObjectTransform.Of(node);
        Assert.Equal(scale, shown.Scale.X);
        Assert.DoesNotContain("0x", JsonData.Vector(shown.Scale).ToJsonString());
        var requested = shown with { RotationDegrees = new(0, 45, 0) };
        var plan = SourceObjectEdits.PlanTransform(workspace, node.Name, assembler.Provenance[node], assembler.Executions,
            requested, Token, mission: "m1", current: shown);
        Assert.Single(plan.Changes);
        Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
        WorldAssembler rebuilt = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
        var after = rebuilt.Assemble("m1.gs");
        Assert.NotEmpty(GameZWriter.Write(after, token: Token));
        Near(requested.Matrix(), WorldUpdate.LocalMatrix(after.Nodes.Single(n => n.Name == "ground"))!.Value);
    }

    [Fact]
    public void HugeShearIsRefusedBeforeAnEditIsPublished()
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/models/m1.gltf";
        var json = JsonNode.Parse(File.ReadAllBytes(fixture.Path(path)))!;
        json["nodes"]![0]!["matrix"] = new JsonArray(1e20f, 1e20f, 0f, 0f, 0f, 1e20f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f);
        fixture.Write(path, json.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var world = assembler.Assemble("m1.gs");
        var node = world.Nodes.Single(n => n.Name == "ground");
        var shown = ObjectTransform.Of(node);
        Assert.Contains("shear", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, node.Name,
            assembler.Provenance[node], assembler.Executions, shown with { RotationDegrees = new(0, 45, 0) }, Token, mission: "m1", current: shown)).Message);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void DecompositionKeepsLargeRotatedMirrorsAndRejectsUnrepresentableScales()
    {
        var expected = new ObjectTransform(new(1, 2, 3), new(23, 45, 61), new(-1e20f, 2e20f, 3e20f));
        var actual = ObjectTransform.FromMatrix(expected.Matrix());
        Assert.True(actual.Scale.X < 0);
        Near(expected.Matrix(), actual.Matrix());
        var overflow = Matrix4x4.Identity;
        overflow.M11 = overflow.M12 = float.MaxValue;
        Assert.Throws<InvalidDataException>(() => ObjectTransform.FromMatrix(overflow));
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var m = Matrix4x4.Identity; m.M11 = invalid;
            Assert.Throws<InvalidDataException>(() => ObjectTransform.FromMatrix(m));
        }
    }

    private static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
        {
            Assert.True(float.IsFinite(actual[i, j]));
            Assert.True(Math.Abs((double)expected[i, j] - actual[i, j]) <= 1e-5 * Math.Max(1, Math.Abs((double)expected[i, j])));
        }
    }
}
