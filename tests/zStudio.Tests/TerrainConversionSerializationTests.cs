using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainConversionSerializationTests
{
    private const string Database = "data/m1/models/m1.gltf";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static (HashSet<string>, IReadOnlyList<Regex>) References => (new(StringComparer.Ordinal), []);

    [Fact]
    public void RepeatedMeshGeometryIsReservedBeforePlanOrApplyCanExpandIt()
    {
        using SourceWorldFixture fixture = new();
        WriteDatabase(fixture, 200, 2000);
        SourceWorkspace workspace = new(fixture.Project);
        var limit = new TerrainConversionSerializationBudget.Limits(BinaryBytes: 1_000_000);
        var plan = SourceTerrainConversion.Plan(workspace, Database, References, Token);
        Assert.Equal(200, plan.Converted);
        Assert.Contains("expanded binary", Assert.Throws<InvalidDataException>(() =>
            SourceTerrainConversion.Plan(workspace, Database, References, limit, Token)).Message);
        // Warm the refusal path, then measure without allocating the source fixture inside the interval.
        Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Apply(workspace, plan, limit, Token));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var failure = Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Apply(workspace, plan, limit, Token));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("expanded binary", failure.Message);
        // The original public fixture allocated about62MB while serializing200copies from30KBinput.
        Assert.InRange(allocated, 0, 8_000_000);
        Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.Revision);

        // A smaller selection under the same allowance remains meaningful geometry, creates one undoable change,
        // and retains each occurrence rather than deduplicating away the pieces that happened to share a mesh.
        var first = Assert.Single(plan.Groups);
        var smaller = plan with { Groups = [first with { Nodes = first.Nodes.Take(4).ToArray() }] };
        var transaction = SourceTerrainConversion.Apply(workspace, smaller, limit, Token);
        Assert.Equal(4, transaction.Files.Count);
        var output = ReadSurfaces(workspace, plan.Surfaces);
        var primitives = Assert.Single(output.Roots).Mesh!.Primitives;
        Assert.Equal(4, primitives.Count);
        Assert.All(primitives, primitive =>
        {
            Assert.Equal(2000, primitive.Positions.Count);
            Assert.Equal([0, 1, 2], primitive.Indices);
            Assert.Equal(new Vector3(1, 0, 0), primitive.Positions[1]);
            Assert.Equal(new Vector3(0, 1, 0), primitive.Positions[2]);
        });
        workspace.Undo();
        Assert.False(workspace.IsDirty);
        SourceTerrainConversion.Apply(workspace, smaller, limit, Token);
        Assert.True(workspace.IsDirty);
    }

    [Theory]
    [InlineData("component")]
    [InlineData("primitive")]
    [InlineData("accessor")]
    [InlineData("JSON")]
    public void EveryEmittedOccurrenceSpendsReaderAndMetadataAllowances(string kind)
    {
        using SourceWorldFixture fixture = new();
        WriteDatabase(fixture, 40, 200, extras: true);
        SourceWorkspace workspace = new(fixture.Project);
        var plan = SourceTerrainConversion.Plan(workspace, Database, References, Token);
        var limits = kind switch
        {
            "component" => new TerrainConversionSerializationBudget.Limits(Components: 1000),
            "primitive" => new TerrainConversionSerializationBudget.Limits(Primitives: 10),
            "accessor" => new TerrainConversionSerializationBudget.Limits(Accessors: 20),
            _ => new TerrainConversionSerializationBudget.Limits(JsonBytes: 100_000),
        };
        Assert.Contains(kind, Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Apply(workspace, plan, limits, Token)).Message);
        Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.Revision);
        // Valid multiplicity and escaped metadata survive an ordinary conversion under the real reader bounds.
        SourceTerrainConversion.Apply(workspace, plan, Token);
        var primitives = Assert.Single(ReadSurfaces(workspace, plan.Surfaces).Roots).Mesh!.Primitives;
        Assert.Equal(40, primitives.Count);
        Assert.All(primitives, primitive => Assert.Equal(new string('"', 1000), primitive.Extras!["notes"]!.GetValue<string>()));
    }

    [Fact]
    public void GroupModelMetadataIsReservedBeforeEachClone()
    {
        using SourceWorldFixture fixture = new();
        WriteDatabase(fixture, 4, 3);
        SourceWorkspace workspace = new(fixture.Project);
        var plan = SourceTerrainConversion.Plan(workspace, Database, References, Token);
        var group = Assert.Single(plan.Groups);
        // The same parsed model values may be shared by several planned sheets; every output mesh clones them.
        var values = JsonNode.Parse("{\"notes\":\"" + new string('x', 1000) + "\"}")!.AsObject();
        plan = plan with { Groups = group.Nodes.Select((node, i) => group with { Id = $"sheet{i}", Nodes = [node], ModelValues = values }).ToArray() };
        var limit = new TerrainConversionSerializationBudget.Limits(JsonBytes: 20_000);
        Assert.Contains("JSON", Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Apply(workspace, plan, limit, Token)).Message);
        Assert.False(workspace.IsDirty);
        SourceTerrainConversion.Apply(workspace, plan, Token);
        Assert.Equal(4, ReadSurfaces(workspace, plan.Surfaces).Roots.Count);
    }

    private static void WriteDatabase(SourceWorldFixture fixture, int copies, int positions, bool extras = false)
    {
        GltfPrimitive primitive = new() { Material = new() };
        // A real vertical triangle has zero plan-view overlap, so identity copies legally join one surface.
        primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitY]);
        while (primitive.Positions.Count < positions) primitive.Positions.Add(Vector3.Zero);
        primitive.Indices.AddRange([0, 1, 2]);
        if (extras) primitive.Extras = new() { ["notes"] = new string('"', 1000) };
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument document = new();
        for (int i = 0; i < copies; i++) document.Roots.Add(new() { Name = $"piece{i}", Mesh = mesh });
        var (json, binary) = document.Write("input.bin", Token);
        fixture.Write(Database, json);
        fixture.Write("data/m1/models/input.bin", binary);
    }

    private static GltfDocument ReadSurfaces(SourceWorkspace workspace, string path) => GltfDocument.Read(workspace.Read(path, Token)!,
        uri => workspace.Read("data/m1/models/" + uri, Token)!, Token);
}
