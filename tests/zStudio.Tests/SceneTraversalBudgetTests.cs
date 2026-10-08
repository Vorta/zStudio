using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SceneTraversalBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GameZWorld Graph(int levels, bool model = false, bool secondRoot = false)
    {
        GameZWorld world = new() { NodeCapacity = levels + 3, ModelCapacity = 1, MaterialCapacity = 1 };
        WorldNode root = new("world", WorldNodeClass.World); world.Nodes.Add(root);
        WorldNode previous = root;
        for (int i = 0; i < levels; i++)
        {
            WorldNode node = new($"group{i}", WorldNodeClass.Object3D) { Flags = 4 };
            node.SetPayloadInt(0, 8); world.Nodes.Add(node);
            Link(previous, node); if (i > 0) Link(previous, node);
            previous = node;
        }
        if (model)
        {
            WorldModel triangle = new(); triangle.Vertices.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]);
            triangle.Polygons.Add(new() { Vertices = [0, 1, 2] });
            world.Models.Add(triangle); previous.Model = triangle;
        }
        if (secondRoot)
        {
            WorldNode other = new("other", WorldNodeClass.World); world.Nodes.Add(other); Link(other, world.Nodes[1]);
        }
        return world;
    }
    private static void Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); }
    private static ZbdDocument Read(GameZWorld world)
    {
        var document = FormatRegistry.Default.OpenBytes("scene.zbd", GameZWriter.Write(world, Token), token: Token);
        Assert.DoesNotContain(document.Diagnostics, d => d.Severity == "Error");
        Assert.NotNull(document.Scene);
        return document;
    }
    private static SceneView Assemble(GameScene scene, LookupWorkBudget work, int? root = null, int placements = SceneBuilder.MaximumPlacements)
        => SceneBuilder.Assemble(scene, 0, false, Token, root, work, placements);

    [Fact]
    public void ReaderAcceptedModelLessDagRefusesOccurrenceWorkWithoutReturningPartialScene()
    {
        var scene = Read(Graph(12)).Scene!;
        Assert.Null(scene.Nodes[1].ModelIndex);
        Assert.Equal(new[] { 2, 2 }, scene.Nodes[1].Children);
        LookupWorkBudget work = new(256, Token);
        var failure = Assert.Throws<InvalidDataException>(() => Assemble(scene, work));
        Assert.Contains("Scene traversal", failure.Message);
        Assert.True(work.Exhausted);
        Assert.InRange(work.UsedUnits, 1, 256);
        // The node selection used by model/group preview has the same bounded traversal.
        Assert.Throws<InvalidDataException>(() => Assemble(scene, new(256, Token), root: 1));
    }

    [Fact]
    public void SharedBudgetIncludesAllWorldRoots()
    {
        var scene = Read(Graph(5, secondRoot: true)).Scene!;
        LookupWorkBudget selected = new(10_000, Token);
        Assert.Empty(Assemble(scene, selected, root: 0).Placements);
        // Exactly enough for the same prepass and first root cannot also traverse the second root.
        Assert.Throws<InvalidDataException>(() => Assemble(scene, new(selected.UsedUnits, Token)));
        Assert.Empty(Assemble(scene, new(10_000, Token)).Placements);
    }

    [Fact]
    public void LegitimateInstancesRemainDistinctAndPlacementRefusalEscapesAncestors()
    {
        var document = Read(Graph(4, model: true)); var scene = document.Scene!;
        var view = Assemble(scene, new(10_000, Token), placements: 8);
        Assert.Equal(8, view.Placements.Count);
        Assert.All(view.Placements, p => { Assert.Equal(4, p.NodeIndex); Assert.Equal(0, p.ModelIndex); Assert.Equal(Matrix4x4.Identity, p.Transform); });
        Assert.Equal(8, SceneBuilder.ForAsset(scene, Assert.Single(document.Assets, a => a.Kind == AssetKind.World), token: Token).Placements.Count);
        var failure = Assert.Throws<InvalidDataException>(() => Assemble(scene, new(10_000, Token), placements: 7));
        Assert.Contains("instance limit", failure.Message);
    }

    [Fact]
    public void RepeatedWarningsHaveBoundedFormattingAndDiscloseOmissions()
    {
        var scene = Read(Graph(11)).Scene!;
        // The reader normalizes the no-model sentinel to null. Deliberately malformed inspection
        // metadata exercises diagnostics separately from the admitted model-less traversal regression.
        for (int i = 0; i < scene.Nodes.Count; i++) scene.Nodes[i] = scene.Nodes[i] with { ModelIndex = -1 };
        var view = Assemble(scene, new(20_000, Token));
        Assert.Empty(view.Placements);
        Assert.Contains(view.Diagnostics, d => d.Message == BoundedDiagnostics.OmissionNotice);
        Assert.Contains(view.Diagnostics, d => d.Message == "Node 1 references missing model -1.");
        Assert.InRange(view.Diagnostics.Count, 1, scene.Nodes.Count + 1);
        Assert.All(view.Diagnostics, d => Assert.InRange(d.Message.Length, 1, BoundedDiagnostics.MaximumMessageCharacters));
        Assert.Equal(view.Diagnostics.Count, view.Diagnostics.Select(d => d.Message).Distinct().Count());
    }

    [Fact]
    public void LodRejectedEdgesStillSpendTraversalWork()
    {
        var world = Graph(1);
        WorldNode near = new("near", WorldNodeClass.Lod) { Flags = 4 }, far = new("far", WorldNodeClass.Lod) { Flags = 4 };
        near.SetPayloadFloat(4, 0); near.SetPayloadFloat(12, 100);
        far.SetPayloadFloat(4, 100); far.SetPayloadFloat(12, 1000);
        world.NodeCapacity = 5; world.Nodes.Add(near); world.Nodes.Add(far);
        Link(world.Nodes[1], near);
        for (int i = 0; i < 100; i++) Link(world.Nodes[1], far);
        var scene = Read(world).Scene!;
        SceneLods lods = new(scene);
        Assert.True(lods.Includes(1, 2, 0)); Assert.False(lods.Includes(1, 3, 0));
        long prepass = scene.Nodes.Sum(n => 1L + n.Children.Length);
        Assert.Throws<InvalidDataException>(() => Assemble(scene, new(prepass + 20, Token)));
        Assert.Empty(Assemble(scene, new(1000, Token)).Placements);
    }

    [Fact]
    public void DuplicatePartitionEntriesAreChargedBeforeCollapsing()
    {
        var scene = Read(Graph(1, model: true)).Scene!;
        scene.Nodes[0].Data["partitions"] = new JsonArray(new JsonArray(new JsonObject
        { ["node_indices"] = new JsonArray(Enumerable.Repeat(1, 100).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) }));
        Assert.Throws<InvalidDataException>(() => Assemble(scene, new(30, Token)));
        Assert.Single(Assemble(scene, new(1000, Token)).Placements);
    }

    [Fact]
    public void CancellationPrecedesPrepassAndAssetRootSelection()
    {
        var document = Read(Graph(3));
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => SceneBuilder.Assemble(document.Scene!, token: cancel.Token));
        Assert.Throws<OperationCanceledException>(() => SceneBuilder.ForAsset(document.Scene!, document.Assets[0], token: cancel.Token));
    }

    [Fact]
    public void HiddenSubtreeAndExplicitRootKeepTheirExistingVisibilityRules()
    {
        var world = Graph(4, model: true); world.Nodes[1].Flags = 0;
        var scene = Read(world).Scene!;
        Assert.Empty(Assemble(scene, new(1000, Token)).Placements);
        Assert.Equal(8, Assemble(scene, new(1000, Token), root: 1).Placements.Count);
        Assert.Equal(8, SceneBuilder.Assemble(scene, includeHidden: true, token: Token).Placements.Count);
    }

    [Fact]
    public void LocalMalformedTransformRemainsDiagnosticWithoutHidingItsValidSibling()
    {
        var scene = Read(Graph(2, model: true)).Scene!;
        scene.Nodes[1].Data["flags"] = 0;
        scene.Nodes[1].Data["transform"] = new JsonArray(1);
        scene.Nodes[0] = scene.Nodes[0] with { Children = [1, 2] };
        var view = Assemble(scene, new(1000, Token));
        Assert.Equal(2, Assert.Single(view.Placements).NodeIndex);
        Assert.Contains(view.Diagnostics, d => d.Message.Contains("Object transform must have 12 elements.", StringComparison.Ordinal));
    }
}
