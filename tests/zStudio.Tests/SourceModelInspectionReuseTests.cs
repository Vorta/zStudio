using System.Text;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class SourceModelInspectionReuseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Shared = "data/common/models/shared.gltf";
    private const string Target = "data/m1/models/target.gltf";
    private const string Empty = """{"asset":{"version":"2.0"},"nodes":[{"name":"empty"}]}""";

    [Fact]
    public void MissionDiscoveryReusesSharedHierarchiesWithinOneWorkAllowance()
    {
        using SourceWorldFixture fixture = new();
        // Two large shared files fit once, but parsing them again for the next mission exceeds this allowance.
        fixture.Write(Shared, """{"asset":{"version":"2.0"},"nodes":[{"extras":{"recoil":{"ref":"leaf.gltf"}}}]}""".PadRight(16_000));
        fixture.Write("data/common/models/leaf.gltf", Empty.PadRight(16_000));
        fixture.Write(Target, Empty);
        foreach (string mission in new[] { "m1", "m2", "m3", "m4" })
            fixture.Write($"data/{mission}/models/owned.gltf", Empty);
        foreach (string mission in new[] { "m1", "m2", "m3" })
            fixture.Write($"gamegen/{mission}.gs", "SetModelDirectory ../data/common/models\nLoadGameGen shared.gltf root\nGameZWriteZBDFile out\n");
        fixture.Write("gamegen/m4.gs", "SetModelDirectory ../data/m1/models\nLoadGameGen target.gltf root\nGameZWriteZBDFile out\n");
        SourceWorkspace workspace = new(fixture.Project);
        SourceScriptInspection inspection = new(workspace, Token, maximumWork: 40_000);
        Assert.Equal(["m4"], SourceBlender.MissionsUsingModel(workspace, Target, Token, inspection));
        Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.Revision);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void TerrainRecipesCountOnlyForDirectLoadsRegardlessOfWhichMissionWarmsTheCache()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Target, Empty);
        fixture.Write(Shared, """{"asset":{"version":"2.0"},"nodes":[{"extras":{"recoil":{"group":true}},"children":[1]},{"extras":{"recoil":{"terrain":"land.terrain.json"}}}],"scenes":[{"nodes":[0]}]}""");
        fixture.Write("data/common/models/land.terrain.json", new zStudio.Core.Terrain.TerrainRecipe(1,
            [new("land", "../../m1/models/target.gltf", "empty", zStudio.Core.Terrain.TerrainAttributes.None)], zStudio.Core.Terrain.TerrainAttributes.None, []).Write());
        fixture.Write("data/common/models/wrapper.gltf", """{"asset":{"version":"2.0"},"nodes":[{"extras":{"recoil":{"ref":"shared.gltf"}}}]}""");
        string Direct(string file) => $"SetModelDirectory ../data/common/models\nLoadGameGen {file} root\nGameZWriteZBDFile out\n";
        SourceWorkspace workspace = new(fixture.Project);
        foreach (bool directFirst in new[] { true, false })
        {
            fixture.Write("gamegen/m1.gs", Direct(directFirst ? "shared.gltf" : "wrapper.gltf"));
            fixture.Write("gamegen/m2.gs", Direct(directFirst ? "wrapper.gltf" : "shared.gltf"));
            Assert.Equal([directFirst ? "m1" : "m2"], SourceBlender.MissionsUsingModel(workspace, Target, Token));
        }
    }

    [Fact]
    public void CachedReferencesRemainBoundedCancelableAndPartOfPreparedDependencies()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Shared, Empty);
        SourceWorkspace workspace = new(fixture.Project);
        var prepared = workspace.BeginPreparedEdit();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        SourceScriptInspection inspection = new(prepared.Workspace, cancellation.Token, maximumWork: 1000);
        var first = inspection.InspectModel(Shared);
        Assert.Same(first, inspection.InspectModel(Shared.ToUpperInvariant().Replace('/', '\\')));
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => inspection.InspectModel(Shared));
        SourceScriptInspection limited = new(prepared.Workspace, Token, maximumWork: 1000);
        Assert.NotNull(limited.InspectModel(Shared));
        Assert.Contains("inspection work", Assert.Throws<IOException>(() =>
        {
            for (int i = 0; i < 100; i++) limited.InspectModel(Shared);
        }).Message);
        fixture.Write(Shared, Empty.Replace("empty", "other", StringComparison.Ordinal));
        Assert.Throws<SourceFileChangedException>(() => workspace.VerifyPreparedEdit(prepared, Token));
        Assert.Equal(0, workspace.Revision);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void ANewInspectionSeesPendingReferenceChangesAndUndo()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Shared, Empty);
        fixture.Write(Target, Empty);
        fixture.Write("data/common/models/target.gltf", Empty); // Same filename, different physical identity.
        fixture.Write("gamegen/m1.gs", "SetModelDirectory ../data/common/models\nLoadGameGen shared.gltf root\nGameZWriteZBDFile out\n");
        fixture.Write("gamegen/m2.gs", "SetModelDirectory ../data/common/models\nLoadGameGen target.gltf root\nGameZWriteZBDFile out\n");
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Empty(SourceBlender.MissionsUsingModel(workspace, Target, Token));
        workspace.Apply("Reference target", [(Shared, Encoding.UTF8.GetBytes("""{"asset":{"version":"2.0"},"nodes":[{"extras":{"recoil":{"ref":"../../m1/models/target.gltf"}}}]}"""))], Token);
        Assert.Equal(["m1"], SourceBlender.MissionsUsingModel(workspace, Target, Token));
        workspace.Undo();
        Assert.Empty(SourceBlender.MissionsUsingModel(workspace, Target, Token));
        Assert.Equal(Empty, File.ReadAllText(fixture.Path(Shared)));
    }
}
