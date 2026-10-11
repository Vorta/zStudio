using System.IO;
using System.Numerics;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceObjectDerivedTransformTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ReparentRefusesOverflowBeforePublishingAndAValidFollowupKeepsThePose()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var (target, parent) = Build("1e30", "1e-30");
        byte[] before = File.ReadAllBytes(fixture.Path("gamegen/m1.gs"));
        Assert.NotEmpty(GameZWriter.Write(target.World, Token)); // The original finite source world is writable.
        var error = Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(target, parent, Token));
        Assert.Contains("finite", error.Message);
        Assert.Equal(0, workspace.Revision);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.CanUndo);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path("gamegen/m1.gs")));

        (target, parent) = Build("10", "0.5");
        var plan = SourceObjectEdits.PlanReparent(target, parent, Token);
        Assert.False(workspace.IsDirty);
        Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
        WorldAssembler rebuilt = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
        var after = rebuilt.Assemble("m1.gs");
        var moved = after.Nodes.Single(n => n.Name == "moved");
        var newParent = Assert.Single(moved.Parents);
        Assert.Equal("small", newParent.Name);
        Matrix4x4 pose = (WorldUpdate.LocalMatrix(moved) ?? Matrix4x4.Identity) * (WorldUpdate.LocalMatrix(newParent) ?? Matrix4x4.Identity);
        Assert.Equal(new Vector3(10, 0, 0), pose.Translation);
        Assert.NotEmpty(GameZWriter.Write(after, Token));

        (SourceObjectTarget Target, WorldNode Parent) Build(string position, string scale)
        {
            fixture.Write("gamegen/m1.gs", $"NewWorld world\nNewObject3D small\nObject3DScale {scale} 1 1\nFindNode world\nAddChild small\nNewObject3D moved\nObject3DTranslate {position} 0 0\nFindNode world\nAddChild moved\nGameZWriteZBDFile world.zbd\n");
            WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
            var world = assembler.Assemble("m1.gs");
            return (new(workspace, "m1", world, world.Nodes.Single(n => n.Name == "moved"), assembler.Provenance, assembler.Executions)
                { Write = assembler.WriteInstruction }, world.Nodes.Single(n => n.Name == "small"));
        }
    }
}
