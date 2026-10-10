using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

[Collection("Allocation-sensitive")]
public sealed class TerrainPlacementIndexTests
{
    private const string Database = "data/m1/models/m1.gltf", Model = "data/m1/models/surfaces.gltf";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(20_000, 64)]
    [InlineData(200_000, 256)]
    public void SeparateLargeSurfaceDocumentIsIndexedOnceAcrossSelectionsAndRecipes(int nodes, int surfaces)
    {
        var parsed = Parse(Document(nodes, surfaces));
        TerrainPlacementBudget budget = nodes == GltfDocument.MaximumNodes ? new(Token) : new(Token, nodes + 2 * surfaces);
        var recipe = Recipe(Enumerable.Range(0, surfaces).Select(i => ($"s{i:000}", "surfaces.gltf")));
        var context = Context((_, _) => (parsed, Model), recipe, budget);
        var first = WorldGltf.ImportTerrain("terrain.json", Database, context, 10000);
        Assert.Equal(surfaces, first.Count);
        Assert.Equal(nodes, budget.NodeVisits);
        Assert.Equal(surfaces, budget.Lookups);
        // A second recipe in the same load uses the cached immutable document's placements too.
        var second = WorldGltf.ImportTerrain("other.json", Database, context, 10000);
        Assert.Equal(surfaces, second.Count);
        Assert.Equal(nodes, budget.NodeVisits);
        Assert.Equal(2 * surfaces, budget.Lookups);
        context.World.Nodes.AddRange(first);
        var reread = FormatRegistry.Default.OpenBytes("terrain.zbd", GameZWriter.Write(context.World, Token), token: Token);
        Assert.Empty(reread.Diagnostics);
        Assert.Equal(surfaces, GameZWorldReader.FromDocument(reread, Token).Nodes.Count(n => n.Model != null));
        Assert.All(first, n => Assert.Single(n.Model!.Polygons));
    }

    [Fact]
    public void CreationSharesOneScanAndKeepsRequestedSurfaceOrder()
    {
        using SourceWorldFixture fixture = new();
        const int count = 4000, surfaces = 32;
        Write(fixture, Document(count, surfaces));
        var names = Enumerable.Range(0, surfaces).Reverse().Select(i => $"s{i:000}").ToArray();
        SourceWorkspace workspace = new(fixture.Project);
        TerrainPlacementBudget small = new(Token, count + surfaces - 1);
        Assert.Contains("aggregate traversal", Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, Database,
            Model, names, null, Token, SourceProject.MaximumFiles, small)).Message);
        Assert.Equal(count, small.NodeVisits);
        Assert.False(workspace.IsDirty);
        Assert.Equal(0, workspace.Revision);
        TerrainPlacementBudget budget = new(Token, count + surfaces);
        SourceTerrain.Create(workspace, Database, Model, names, null, Token, SourceProject.MaximumFiles, budget);
        Assert.Equal(count, budget.NodeVisits);
        Assert.Equal(surfaces, budget.Lookups);
        var recipe = SourceTerrain.Read(workspace, "data/m1/models/surfaces.terrain.json", Token);
        Assert.Equal(names, recipe.Surfaces.Select(s => s.Node));
        workspace.Undo(); Assert.False(workspace.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedBudgetSpansDifferentDocumentsAndRecipeCalls(bool separateRecipes)
    {
        var a = Parse(Document(64, 1)); var b = Parse(Document(64, 1));
        TerrainPlacementBudget budget = new(Token, 100);
        TerrainRecipe recipe = separateRecipes ? Recipe([("s000", "a.gltf")]) : Recipe([("s000", "a.gltf"), ("s000", "b.gltf")]);
        bool second = false;
        var context = Context((uri, _) => (uri == "b.gltf" || second ? b : a, uri), recipe, budget);
        if (separateRecipes)
        {
            Assert.Single(WorldGltf.ImportTerrain("first.json", Database, context, 100));
            second = true;
        }
        Assert.Contains("aggregate traversal", Assert.Throws<InvalidDataException>(() =>
            WorldGltf.ImportTerrain("next.json", Database, context, 100)).Message);
        Assert.True(budget.NodeVisits < 128);
        var retry = Context((uri, _) => (uri == "b.gltf" || second ? b : a, uri), recipe, new(Token));
        Assert.NotEmpty(WorldGltf.ImportTerrain("retry.json", Database, retry, 100));
    }

    [Theory]
    [InlineData("nonmesh")]
    [InlineData("instance")]
    [InlineData("explicit")]
    public void FullSceneNameAmbiguitySurvivesMeshAndInstanceSharing(string kind)
    {
        using SourceWorldFixture fixture = new();
        var source = Document(0, 1);
        var first = source.Roots[0]; first.Name = "surface";
        GltfNode other = new() { Name = "surface.001", Mesh = kind == "nonmesh" ? null : first.Mesh };
        if (kind == "instance")
        {
            first.Extras = new() { ["recoil"] = new JsonObject { ["instance"] = 7 } };
            other.Extras = new() { ["recoil"] = new JsonObject { ["instance"] = 7 } };
        }
        if (kind == "explicit") { other.Name = "unrelated"; other.Extras = new() { ["recoil"] = new JsonObject { ["name"] = "surface" } }; }
        source.Roots.Add(other); Write(fixture, source);
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Empty(SourceTerrain.MeshNodes(workspace, Model, Token));
        Assert.Contains("more than one", Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, Database, Model, ["surface"], token: Token)).Message);
        var context = Context((_, _) => (Parse(source), Model), Recipe([("surface", "surfaces.gltf")]), new(Token));
        Assert.Contains("more than one", Assert.Throws<InvalidDataException>(() => WorldGltf.ImportTerrain("terrain.json", Database, context, 100)).Message);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void NestedPlacementRetainsTransformZoneAndAppearanceAncestry()
    {
        using SourceWorldFixture fixture = new();
        var source = Document(0, 1); var leaf = source.Roots[0]; leaf.Name = "surface.007";
        leaf.Matrix = Matrix4x4.CreateTranslation(0, 1, 0);
        const uint carried = WorldGltf.DefaultCarried | 0x10000;
        leaf.Extras = new() { ["recoil"] = new JsonObject { ["flags"] = $"0x{carried:X8}" } };
        GltfNode parent = new() { Name = "parent", Matrix = Matrix4x4.CreateTranslation(0, 2, 0),
            Extras = new() { ["recoil"] = new JsonObject { ["zone"] = 3 } } };
        parent.Children.Add(leaf); source.Roots.Clear(); source.Roots.Add(parent); Write(fixture, source);
        SourceWorkspace workspace = new(fixture.Project);
        SourceTerrain.Create(workspace, Database, Model, ["surface"], token: Token);
        var recipe = SourceTerrain.Read(workspace, "data/m1/models/surfaces.terrain.json", Token);
        Assert.Equal(3, Assert.Single(recipe.Surfaces).Defaults.NodeZone);
        Assert.Equal(carried, recipe.Surfaces[0].Defaults.Flags);
        var parsed = Parse(source);
        var context = Context((_, _) => (parsed, Model), recipe, new(Token));
        var piece = Assert.Single(WorldGltf.ImportTerrain("terrain.json", Database, context, 100));
        Assert.Equal(3u, piece.Zone);
        Assert.Equal(carried, piece.Flags & WorldGltf.CarriedFlags);
        Assert.All(piece.Model!.Vertices, v => Assert.Equal(3f, v.Y));
        parent.Extras!["recoil"]!["appearance"] = new JsonObject { ["flags"] = "0x2", ["alphaScale"] = 0.25f };
        var withAppearance = Parse(source);
        var refused = Context((_, _) => (withAppearance, Model), recipe, new(Token));
        Assert.Contains("ancestor", Assert.Throws<InvalidDataException>(() => WorldGltf.ImportTerrain("terrain.json", Database, refused, 100)).Message);
    }

    [Fact]
    public void RepeatedSelectionsDecodeCarriedFlagsOnceBeforeDuplicateRefusal()
    {
        using SourceWorldFixture fixture = new();
        var source = Document(0, 1);
        source.Roots[0].Extras = new() { ["recoil"] = new JsonObject { ["flags"] = new string('x', 1_000_000) } };
        Write(fixture, source);
        SourceWorkspace workspace = new(fixture.Project);
        string[] names = Enumerable.Repeat("s000", TerrainRecipe.MaximumSurfaces).ToArray();
        long start = GC.GetAllocatedBytesForCurrentThread();
        var refusal = Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, Database, Model, names, token: Token));
        Assert.Contains("for two surfaces", refusal.Message);
        // The source is parsed once; the old per-selection cold scalar decode alone copied about 512 MiB.
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - start, 0, 64L * 1024 * 1024);
        Assert.False(workspace.IsDirty); Assert.Equal(0, workspace.Revision);
        SourceTerrain.Create(workspace, Database, Model, ["s000"], token: Token);
        Assert.Null(Assert.Single(SourceTerrain.Read(workspace, "data/m1/models/surfaces.terrain.json", Token).Surfaces).Defaults.Flags);
        workspace.Undo(); Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void CreationCanCancelInsideItsInitialPlacementAndInheritedZoneScan()
    {
        using SourceWorldFixture fixture = new(); Write(fixture, Document(4000, 1));
        SourceWorkspace workspace = new(fixture.Project);
        using CancellationTokenSource cancel = new();
        int visited = 0;
        TerrainPlacementBudget budget = new(cancel.Token, visited: () => { if (++visited == 100) cancel.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => SourceTerrain.Create(workspace, Database, Model, ["s000"], null,
            cancel.Token, SourceProject.MaximumFiles, budget));
        Assert.Equal(100, budget.NodeVisits);
        Assert.False(workspace.IsDirty); Assert.Equal(0, workspace.Revision);
        SourceTerrain.Create(workspace, Database, Model, ["s000"], token: Token);
        Assert.True(workspace.IsDirty);
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("name")]
    [InlineData("flags")]
    public void NameTextIsChargedBeforeSuffixCopyOrColdJsonStringDecode(string field)
    {
        var source = Document(0, 1); var node = source.Roots[0];
        string name = new string('x', 100_000) + ".001";
        if (field != "raw") node.Extras = JsonNode.Parse("{\"recoil\":{\"" + field + "\":\"" + name + "\"}}")!.AsObject();
        else node.Name = name;
        var warm = Document(0, 1);
        Assert.Throws<InvalidDataException>(() => new TerrainPlacementIndex(warm, new(Token, maximumText: 1)));
        long start = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => new TerrainPlacementIndex(source, new(Token, maximumText: 100)));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - start, 0, 64 * 1024);
    }

    [Fact]
    public void DirectCyclicAndExpandingDagInputsAreRefusedWithoutRecursiveTraversal()
    {
        GltfDocument cyclic = new(); GltfNode self = new() { Name = "cycle" }; self.Children.Add(self); cyclic.Roots.Add(self);
        Assert.Contains("cyclic", Assert.Throws<InvalidDataException>(() => new TerrainPlacementIndex(cyclic, new(Token))).Message);
        GltfNode leaf = new() { Name = "leaf" }, current = leaf;
        for (int i = 0; i < 20; i++) { GltfNode parent = new() { Name = $"level{i}" }; parent.Children.Add(current); parent.Children.Add(current); current = parent; }
        GltfDocument dag = new(); dag.Roots.Add(current);
        TerrainPlacementBudget budget = new(Token, 100);
        Assert.Contains("aggregate traversal", Assert.Throws<InvalidDataException>(() => new TerrainPlacementIndex(dag, budget)).Message);
        Assert.Equal(100, budget.NodeVisits);
        GltfDocument twice = new(); twice.Roots.Add(leaf); twice.Roots.Add(leaf);
        Assert.False(new TerrainPlacementIndex(twice, new(Token)).TryGet("leaf", out _, out bool ambiguous));
        Assert.True(ambiguous);
    }

    private static TerrainRecipe Recipe(IEnumerable<(string Node, string Model)> entries) => new(1,
        entries.Select((e, i) => new TerrainSurface($"surface{i}", e.Model, e.Node, TerrainAttributes.None)).ToArray(), TerrainAttributes.None, []);
    private static WorldGltf.ImportContext Context(Func<string, string, (GltfDocument, string)> reference, TerrainRecipe recipe, TerrainPlacementBudget budget) => new()
    {
        World = new(), Token = Token, TerrainPlacementBudget = budget, Reference = reference, TextureName = (_, _, _) => "unused",
        ReadFile = (_, _) => (recipe.Write(), "data/m1/models/terrain.json"), Grid = () => new(0, 4, 4, -4, 4, -4, 1, 1),
    };
    private static GltfDocument Document(int total, int selected)
    {
        GltfPrimitive primitive = new() { Material = new() };
        primitive.Positions.AddRange([new(1, 0, 1), new(1, 0, 2), new(2, 0, 1)]); primitive.Indices.AddRange([0, 1, 2]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument document = new();
        for (int i = 0; i < total - selected; i++) document.Roots.Add(new() { Name = $"dummy{i}" });
        for (int i = 0; i < selected; i++) document.Roots.Add(new() { Name = $"s{i:000}", Mesh = mesh });
        return document;
    }
    private static GltfDocument Parse(GltfDocument doc)
    {
        var (json, binary) = doc.Write("surfaces.bin", Token);
        return GltfDocument.Read(json, _ => binary, Token);
    }
    private static void Write(SourceWorldFixture fixture, GltfDocument doc)
    {
        var (json, binary) = doc.Write("surfaces.bin", Token);
        fixture.Write(Model, json); fixture.Write("data/m1/models/surfaces.bin", binary);
    }
}
