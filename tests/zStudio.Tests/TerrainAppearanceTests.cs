using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainAppearanceTests
{
    private const string Database = "data/m1/models/m1.gltf", Model = "data/m1/models/surface.gltf";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static (HashSet<string>, IReadOnlyList<Regex>) References => (new(StringComparer.Ordinal), []);

    [Theory]
    [InlineData(0)] // Stored inactive values must survive too.
    [InlineData(2)] // Active inherited alpha override.
    [InlineData(0x46)] // Color controls and an unknown retained appearance bit.
    public void ConversionKeepsEveryAppearanceWordThroughCuttingAndGameZ(int flags)
    {
        using SourceWorldFixture fixture = new();
        GltfDocument source = new();
        var appearance = Appearance(flags);
        if (flags == 0) appearance["alphaScale"] = -0.0f;
        source.Roots.Add(Node("surface", appearance, vertical: false));
        Write(fixture, Database, source);
        SourceWorkspace workspace = new(fixture.Project);
        var before = Assert.Single(Import(workspace, Database));
        var plan = SourceTerrainConversion.Plan(workspace, Database, References, Token);
        Assert.Equal(1, plan.Converted);
        Assert.Empty(plan.Kept);
        SourceTerrainConversion.Apply(workspace, plan, Token);
        var emitted = Read(workspace, plan.Surfaces);
        Assert.True(JsonNode.DeepEquals(source.Roots[0].Extras!["recoil"]!["appearance"], emitted.Roots[0].Extras!["recoil"]!["appearance"]));
        var after = Import(workspace, Database);
        Assert.True(after.Count > 1); // The source triangle actually crosses grid cells.
        Assert.All(after, piece =>
        {
            Assert.Equal(before.PayloadInt(0) & ~0x39, piece.PayloadInt(0) & ~0x39);
            for (int offset = 4; offset <= 20; offset += 4)
                Assert.Equal(BitConverter.SingleToInt32Bits(before.PayloadFloat(offset)), BitConverter.SingleToInt32Bits(piece.PayloadFloat(offset)));
        });
        workspace.Undo();
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void DifferingAppearanceSeparatesSurfacesWhileIdenticalAppearanceStillMerges()
    {
        using SourceWorldFixture fixture = new();
        GltfDocument source = new();
        source.Roots.Add(Node("a", Appearance(2)));
        source.Roots.Add(Node("b", Appearance(2)));
        var different = Appearance(2); different["alphaScale"] = 0.75f;
        source.Roots.Add(Node("c", different));
        source.Roots.Add(Node("d", null));
        Write(fixture, Database, source);
        SourceWorkspace workspace = new(fixture.Project);
        var plan = SourceTerrainConversion.Plan(workspace, Database, References, Token);
        Assert.Equal(4, plan.Converted);
        Assert.Equal([1, 1, 2], plan.Groups.Select(g => g.Nodes.Count).Order());
        Assert.Equal(3, plan.Groups.Select(g => g.Id).Distinct().Count());
        SourceTerrainConversion.Apply(workspace, plan, Token);
        var output = Import(workspace, Database);
        Assert.Contains(output, n => n.PayloadInt(0) == 0x2A && n.PayloadFloat(4) == 0.25f);
        Assert.Contains(output, n => n.PayloadInt(0) == 0x2A && n.PayloadFloat(4) == 0.75f);
        Assert.Contains(output, n => n.PayloadInt(0) == 0x28 && n.PayloadFloat(4) == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateAndDirectImportRefuseFlatteningAncestorAppearance(bool leafOverrides)
    {
        using SourceWorldFixture fixture = new();
        GltfDocument source = new();
        var parent = new GltfNode { Name = "parent", Extras = Extras(Appearance(2)) };
        parent.Children.Add(Node("surface", leafOverrides ? Appearance(0x46) : null));
        source.Roots.Add(parent);
        Write(fixture, Model, source);
        SourceWorkspace workspace = new(fixture.Project);
        var failure = Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, Database, Model, ["surface"], token: Token));
        Assert.Contains("ancestor with Object3D appearance", failure.Message);
        Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.Revision);

        // A hand-authored recipe reaches the same guard without going through Create.
        TerrainRecipe recipe = new(1, [new("surface", "surface.gltf", "surface", TerrainAttributes.None)], TerrainAttributes.None, []);
        fixture.Write("data/m1/models/manual.terrain.json", recipe.Write());
        GltfDocument marker = new();
        marker.Roots.Add(new() { Name = "terrain", Extras = new() { ["recoil"] = new JsonObject { ["terrain"] = "manual.terrain.json" } } });
        Write(fixture, Database, marker);
        SourceWorkspace fresh = new(fixture.Project);
        Assert.Contains("ancestor with Object3D appearance", Assert.Throws<InvalidDataException>(() => Import(fresh, Database)).Message);
    }

    [Fact]
    public void CreatePreservesOwnAppearanceBeneathAnOrdinaryTransformParent()
    {
        using SourceWorldFixture fixture = new();
        GltfDocument source = new();
        var parent = new GltfNode { Name = "parent", Matrix = Matrix4x4.CreateTranslation(0, 2, 0) };
        parent.Children.Add(Node("surface", Appearance(0x46), vertical: false));
        source.Roots.Add(parent);
        Write(fixture, Model, source);
        // Empty database isolates the newly created terrain from the fixture's ordinary ground.
        Write(fixture, Database, new());
        SourceWorkspace workspace = new(fixture.Project);
        SourceTerrain.Create(workspace, Database, Model, ["surface"], token: Token);
        var pieces = Import(workspace, Database);
        Assert.NotEmpty(pieces);
        Assert.All(pieces, n =>
        {
            Assert.Equal(0x6E, n.PayloadInt(0));
            Assert.Equal(0.25f, n.PayloadFloat(4));
            Assert.Equal(0.5f, n.PayloadFloat(8));
            Assert.Equal(0.625f, n.PayloadFloat(12));
            Assert.Equal(0.75f, n.PayloadFloat(16));
            Assert.Equal(0.875f, n.PayloadFloat(20));
            Assert.All(n.Model!.Vertices, v => Assert.Equal(2f, v.Y));
        });
    }

    [Fact]
    public void AppearanceMetadataIsChargedBeforeSurfaceClones()
    {
        using SourceWorldFixture fixture = new();
        var appearance = Appearance(2); appearance["retained"] = new string('"', 2000);
        GltfDocument source = new(); source.Roots.Add(Node("surface", appearance));
        Write(fixture, Database, source);
        SourceWorkspace workspace = new(fixture.Project);
        var plan = SourceTerrainConversion.Plan(workspace, Database, References, Token);
        Assert.Contains("JSON", Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Apply(workspace, plan,
            new TerrainConversionSerializationBudget.Limits(JsonBytes: 12_000), Token)).Message);
        Assert.False(workspace.IsDirty);
        SourceTerrainConversion.Apply(workspace, plan, Token);
        Assert.True(JsonNode.DeepEquals(appearance, Read(workspace, plan.Surfaces).Roots[0].Extras!["recoil"]!["appearance"]));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"flags\":\"0x8\"}")]
    public void MalformedAppearanceRefusesBothConversionAndCreationBeforeAcceptance(string appearance)
    {
        using SourceWorldFixture fixture = new();
        var node = Node("surface", Appearance(2));
        node.Extras!["recoil"]!["appearance"] = JsonNode.Parse(appearance);
        GltfDocument source = new(); source.Roots.Add(node);
        Write(fixture, Database, source); Write(fixture, Model, source);
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Contains("appearance", Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Plan(workspace, Database, References, Token)).Message);
        Assert.Contains("appearance", Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, Database, Model, ["surface"], token: Token)).Message);
        Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.Revision);
    }

    private static JsonObject Appearance(int flags) => new()
    {
        ["flags"] = $"0x{flags:X8}", ["alphaScale"] = 0.25f,
        ["color"] = new JsonArray(0.5f, 0.625f, 0.75f), ["colorAlpha"] = 0.875f,
    };
    private static JsonObject? Extras(JsonObject? appearance) => appearance == null ? null
        : new() { ["recoil"] = new JsonObject { ["appearance"] = appearance } };
    private static GltfNode Node(string name, JsonObject? appearance, bool vertical = true)
    {
        GltfPrimitive primitive = new() { Material = new() };
        primitive.Positions.AddRange(vertical ? [Vector3.Zero, Vector3.UnitX, Vector3.UnitY] : [Vector3.Zero, new(4, 0, 0), new(0, 0, 4)]);
        primitive.Indices.AddRange([0, 1, 2]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        return new() { Name = name, Mesh = mesh, Extras = Extras(appearance) };
    }
    private static void Write(SourceWorldFixture fixture, string path, GltfDocument doc)
    {
        string binaryName = Path.GetFileNameWithoutExtension(path) + ".bin";
        var (json, binary) = doc.Write(binaryName, Token);
        fixture.Write(path, json);
        fixture.Write(path[..(path.LastIndexOf('/') + 1)] + binaryName, binary);
    }
    private static GltfDocument Read(SourceWorkspace workspace, string path) => GltfDocument.Read(workspace.Read(path, Token)!,
        uri => workspace.Read(WorldAssembler.Relative(path, uri), Token)!, Token);
    private static List<WorldNode> Import(SourceWorkspace workspace, string path)
    {
        GameZWorld world = new();
        WorldGltf.ImportContext context = new()
        {
            World = world, Token = Token, TextureName = (_, _, _) => "unused",
            Grid = () => new(0, 4, 4, -4, 2, -2, 2, 2),
            Reference = (uri, from) => { string resolved = WorldAssembler.Relative(from, uri); return (Read(workspace, resolved), resolved); },
            ReadFile = (uri, from) => { string resolved = WorldAssembler.Relative(from, uri); return (workspace.Read(resolved, Token)!, resolved); },
        };
        world.Nodes.AddRange(WorldGltf.Import(Read(workspace, path), path, 0xFF, context));
        var parsed = FormatRegistry.Default.OpenBytes("world.zbd", GameZWriter.Write(world, Token), token: Token);
        Assert.Empty(parsed.Diagnostics);
        return GameZWorldReader.FromDocument(parsed, Token).Nodes.Where(n => n.Model != null).ToList();
    }
}
