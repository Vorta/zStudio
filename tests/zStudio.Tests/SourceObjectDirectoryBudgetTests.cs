using System.IO;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class SourceObjectDirectoryBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void OtherMissionLoadsShareUnchangedHistoryAndKeepItsEarlierSearchOrder()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "SetModelDirectory ../data/m2/models/bft\nLoadGameGen tank.gltf first\nLoadGameGen tank.gltf second\nSetModelDirectory ../data/m1/models\nFindNode first\nFindSubNode hull\nObject3DRotate 0 1 0\n");
        ScriptTraceBudget budget = new(5);
        var hit = SourceObjectEdits.TransformElsewhere(new(fixture.Project), "m1", new() { ModelFile = fixture.Tank }, "hull", Token, budget);
        Assert.NotNull(hit);
        Assert.Equal("m2", hit.Value.Mission);
        Assert.True(hit.Value.Certain);
        Assert.Equal(5, budget.UsedUnits);
    }

    [Fact]
    public void OtherMissionHistoryCapacityCannotBeSwallowedAsAnInvalidMission()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "SetModelDirectory ../data/a\nLoadGameGen a.gltf a\n");
        fixture.Write("gamegen/m3.gs", "SetModelDirectory ../data/b\nLoadGameGen b.gltf b\n");
        ScriptTraceBudget budget = new(9);
        var error = Assert.Throws<InvalidDataException>(() => SourceObjectEdits.TransformElsewhere(new(fixture.Project), "m1", new() { ModelFile = fixture.Tank }, "hull", Token, budget));
        Assert.Contains("directory history", error.Message);
        Assert.True(budget.Exhausted);
        Assert.Equal(5, budget.UsedUnits);
    }

    [Fact]
    public void OtherMissionDirectoryWorkCapacityIsNotSwallowedBeforeTheFirstLoad()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "SetModelDirectory ../data/a;../data/b;../data/c\n");
        ScriptTraceBudget budget = new(maximumWorkUnits: 1);
        var error = Assert.Throws<InvalidDataException>(() => SourceObjectEdits.TransformElsewhere(new(fixture.Project), "m1", new() { ModelFile = fixture.Tank }, "hull", Token, budget));
        Assert.Contains("directory search-path work", error.Message);
        Assert.True(budget.Exhausted);
        Assert.Equal(0, budget.UsedUnits);
    }
}
