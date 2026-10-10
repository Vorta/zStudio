using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23CopyIdentityTests
{
    [Fact]
    public void PunctuationInSourcePathsCannotShiftTheCopyIdentityFields()
    {
        WorldNodeProvenance first = Origin("data/a.gltf#0@gamegen/b.gltf", "gamegen/c.gs");
        WorldNodeProvenance second = Origin("data/a.gltf", "gamegen/b.gltf#0@gamegen/c.gs");
        // Both formerly became data/a.gltf#0@gamegen/b.gltf#0@gamegen/c.gs:1/.
        static string Former(WorldNodeProvenance origin)
        {
            var by = origin.ReferencedBy!;
            return $"{by.ModelFile}#{by.ModelNode}@{by.Load!.Script}:{by.Load.Line}/";
        }
        Assert.Equal(Former(first), Former(second));
        Assert.NotEqual(SourceObjectEdits.CopyKey(first), SourceObjectEdits.CopyKey(second));
        Assert.Equal(SourceObjectEdits.CopyKey(first), SourceObjectEdits.CopyKey(
            Origin("DATA/A.GLTF#0@GAMEGEN/B.GLTF", "GAMEGEN/C.GS")));
        var noLoad = Origin("data/a.gltf", "gamegen/c.gs"); noLoad.ReferencedBy!.Load = null;
        Assert.NotEqual(SourceObjectEdits.CopyKey(noLoad), SourceObjectEdits.CopyKey(Origin("data/a.gltf", "gamegen/c.gs")));
    }

    [Fact]
    public void ADirectAssemblerEntryMayCarryThePunctuationInItsScriptProvenance()
    {
        using SourceWorldFixture fixture = new();
        const string script = "gamegen/b.gltf#0@gamegen/c.gs";
        fixture.Write(script, "NewWorld world\nGameZWriteZBDFile gamez.zbd\n");
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new WorkspaceFiles(workspace), TestContext.Current.CancellationToken);
        assembler.Assemble(script["gamegen/".Length..]);
        Assert.Equal(script, assembler.WriteInstruction!.Script);
    }

    private static WorldNodeProvenance Origin(string model, string script) => new()
    {
        ModelFile = "data/leaf.gltf", ModelNode = 0,
        ReferencedBy = new()
        {
            ModelFile = model, ModelNode = 0,
            Load = new(script, 1, "LoadGameGen", [], []),
        },
    };

    private sealed class WorkspaceFiles(SourceWorkspace workspace) : IProjectFiles
    {
        public bool Exists(string relative) => workspace.Exists(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); return workspace.Read(relative, token, limits) ?? throw new FileNotFoundException(relative); }
    }
}
