using System.IO;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainCreationReferenceBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Database = "data/m1/models/m1.gltf";

    [Fact]
    public void TerrainCreationRefusesAnIncompleteReferenceGraphBeforeChangingSources()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Database, Document("../../../gamegen/first.gltf", "../../../gamegen/first.gltf"));
        fixture.Write("gamegen/first.gltf", Document("second.gltf"));
        fixture.Write("gamegen/second.gltf", Document("../" + fixture.Tank));
        SourceWorkspace workspace = new(fixture.Project);
        byte[] before = File.ReadAllBytes(fixture.Path(Database));

        var limit = Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, Database, fixture.Tank, ["hull"], null, Token, maximumReferenceFiles: 2));
        Assert.Contains("complete model references", limit.Message);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.CanUndo);
        Assert.Equal(0, workspace.Revision);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path(Database)));
        Assert.False(File.Exists(fixture.Path("data/m2/models/bft/tank.terrain.json")));

        // Exactly three distinct references completes, including duplicate edges. It finds the protected model
        // reached through gamegen/, instead of reporting capacity merely because the queue has more entries.
        var protectedModel = Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, Database, fixture.Tank, ["hull"], null, Token, maximumReferenceFiles: 3));
        Assert.Contains("loaded with the mission database", protectedModel.Message);
        Assert.False(workspace.IsDirty);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SourceTerrain.Create(workspace, Database, fixture.Tank, ["hull"], null, canceled.Token, maximumReferenceFiles: 3));
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void CompletedReferenceCyclesAndDuplicateEdgesDoNotConsumeAdditionalCapacity()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Database, Document("../../../gamegen/first.gltf", "../../../gamegen/first.gltf"));
        fixture.Write("gamegen/first.gltf", Document("../" + Database));
        SourceWorkspace workspace = new(fixture.Project);
        var references = SourceTerrain.Referenced(workspace, Database, Token, maximumFiles: 2);
        Assert.Equal(2, references.Count);
        Assert.Contains(Database, references);
        Assert.Contains("gamegen/first.gltf", references);
        Assert.False(workspace.IsDirty);
    }

    private static string Document(params string[] references)
    {
        JsonArray nodes = [], roots = [];
        foreach (string reference in references)
        {
            roots.Add(nodes.Count);
            nodes.Add(new JsonObject { ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["ref"] = reference } } });
        }
        return new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["nodes"] = nodes,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = roots }), ["scene"] = 0 }.ToJsonString();
    }
}
