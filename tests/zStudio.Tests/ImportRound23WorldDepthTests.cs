using System.Text;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23WorldDepthTests
{
    [Fact]
    public void CopyIdentityIncludesOuterReferencesBeyondSixtyFourLevels()
    {
        static WorldNodeProvenance Chain(int outer)
        {
            WorldNodeProvenance end = new() { ModelFile = "data/m1/models/database.gltf", ModelNode = outer };
            for (int i = 0; i < 64; i++)
                end = new() { ModelFile = $"data/m1/models/part{i}.gltf", ModelNode = 0, ReferencedBy = end };
            return new() { ModelFile = "data/m1/models/leaf.gltf", ModelNode = 0, ReferencedBy = end };
        }
        Assert.NotEqual(SourceObjectEdits.CopyKey(Chain(1)), SourceObjectEdits.CopyKey(Chain(2)));
        Assert.Equal(SourceObjectEdits.CopyKey(Chain(1)), SourceObjectEdits.CopyKey(Chain(1)));
        WorldNodeProvenance cyclic = new(); cyclic.ReferencedBy = cyclic;
        Assert.Throws<InvalidDataException>(() => SourceObjectEdits.CopyKey(cyclic));
    }

    [Fact]
    public void ReparentingAScriptNodeKeepsItsPositionBeyondTheGltfDepthLimit()
    {
        using SourceWorldFixture fixture = new();
        StringBuilder script = new("NewWorld world\nGameGenSetWorld world\nWorldOrigin 0 512\nWorldExtents 512 -512\nWorldPartition 256 -256\n");
        for (int i = 0; i < 300; i++)
            script.Append($"NewObject3D n{i}\nObject3DTranslate 1 0 0\nFindNode {(i == 0 ? "world" : $"n{i - 1}")}\nAddChild n{i}\n");
        script.Append("GameZWriteZBDFile gamez.zbd\n");
        fixture.Write("gamegen/m1.gs", script.ToString());
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler original = new(new WorkspaceFiles(workspace), TestContext.Current.CancellationToken);
        var world = original.Assemble("m1.gs");
        var leaf = world.Nodes.Single(n => n.Name == "n299");
        SourceObjectTarget target = new(workspace, "m1", world, leaf, original.Provenance, original.Executions)
        { Write = original.WriteInstruction };
        var plan = SourceObjectEdits.PlanReparent(target, world.Nodes.Single(n => n.Name == "world"), TestContext.Current.CancellationToken);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), TestContext.Current.CancellationToken);
        var rebuilt = new WorldAssembler(new WorkspaceFiles(workspace), TestContext.Current.CancellationToken).Assemble("m1.gs");
        var moved = rebuilt.Nodes.Single(n => n.Name == "n299");
        Assert.Equal("world", Assert.Single(moved.Parents).Name);
        Assert.Equal(300f, ObjectTransform.Of(moved).Position.X);
        Assert.Equal(script.ToString(), File.ReadAllText(fixture.Path("gamegen/m1.gs")));
    }

    private sealed class WorkspaceFiles(SourceWorkspace workspace) : IProjectFiles
    {
        public bool Exists(string relative) => workspace.Exists(relative);
        public byte[] Read(string relative, CancellationToken token) => workspace.Read(relative, token) ?? throw new FileNotFoundException(relative);
    }
}
