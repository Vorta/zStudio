using System.IO;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceObjectDirectoryChallengeTests
{
    [Fact]
    public void MalformedMissionRecoveryDoesNotResetTheSharedHistoryBudget()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "SetModelDirectory ../data/a\nLoadGameGen absent.flt first\nset big " + new string('x', 2000) + "\nNewObject3D %big%\n");
        fixture.Write("gamegen/m3.gs", "source shared.gw\nFindNode tank\nFindSubNode hull\nObject3DRotate 0 1 0\n");
        fixture.Write("gamegen/shared.gw", "SetModelDirectory ../data/m2/models/bft\nLoadGameGen tank.flt tank\n");
        SourceWorkspace workspace = new(fixture.Project);
        WorldNodeProvenance origin = new() { ModelFile = fixture.Tank };
        var positive = SourceObjectEdits.TransformElsewhere(workspace, "m1", origin, "hull", TestContext.Current.CancellationToken);
        Assert.NotNull(positive);
        Assert.Equal("m3", positive.Value.Mission);
        ScriptTraceBudget budget = new(9);
        Assert.Contains("directory history", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.TransformElsewhere(workspace,
            "m1", origin, "hull", TestContext.Current.CancellationToken, budget)).Message);
        Assert.Equal(5, budget.UsedUnits);
        Assert.True(budget.Exhausted);
    }
}
