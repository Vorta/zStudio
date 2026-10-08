using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldGltfLinkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void WideParentKeepsOrderedIdentitiesWithOneAdmissionPerEdge()
    {
        JsonArray nodes = [Node("root", Enumerable.Range(1, 32).ToArray())];
        for (int i = 0; i < 32; i++) nodes.Add(Node("leaf" + i));
        var doc = Read(nodes);
        LookupWorkBudget work = new(32, Token);
        var root = Assert.Single(WorldGltf.Import(doc, "wide.gltf", 255, Context(work)));
        Assert.Equal(Enumerable.Range(0, 32).Select(i => "leaf" + i), root.Children.Select(c => c.Name));
        Assert.All(root.Children, child => Assert.Same(root, Assert.Single(child.Parents)));
        Assert.Equal(32, work.UsedUnits);

        List<WorldNode> built = [];
        LookupWorkBudget shortWork = new(31, Token);
        var limited = Context(shortWork, imported: built.Add);
        Assert.Contains("node lookup work", Assert.Throws<InvalidDataException>(() => WorldGltf.Import(doc, "wide.gltf", 255, limited)).Message);
        Assert.True(shortWork.Exhausted);
        Assert.Equal(31, built[0].Children.Count);
        Assert.Empty(built[^1].Parents); // Rejected edge changes neither endpoint.
        Assert.DoesNotContain(built[^1], built[0].Children);
        Assert.Equal(32, Assert.Single(WorldGltf.Import(doc, "wide.gltf", 255, Context())).Children.Count);
    }

    [Fact]
    public void InstanceDuplicatesCoalesceOnlyByIdentityAndKeepFirstOrder()
    {
        var doc = Read([
            Node("root", [1, 2]), Node("first", [3, 4, 5, 6]), Node("second", [7]),
            Node("same", mark: 9), Node("middle"), Node("later", mark: 9), Node("same"), Node("copy", mark: 9)
        ]);
        var root = Assert.Single(WorldGltf.Import(doc, "instances.gltf", 255, Context()));
        var first = root.Children[0]; var second = root.Children[1];
        Assert.Equal(["same", "middle", "same"], first.Children.Select(c => c.Name));
        Assert.NotSame(first.Children[0], first.Children[2]);
        Assert.Same(first.Children[0], Assert.Single(second.Children));
        Assert.Equal([first, second], first.Children[0].Parents);
        Assert.Same(first, Assert.Single(first.Children[2].Parents));
    }

    [Fact]
    public void ReferencedRootsPrecedeOwnChildrenAndDoNotShareFileLocalMarkers()
    {
        var part = Read([Node("part", mark: 1)]);
        var parent = Node("root", [1, 2]);
        parent["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["ref"] = "part.gltf" } };
        var doc = Read([parent, Node("own", mark: 1), Node("copy", mark: 1)]);
        LookupWorkBudget work = new(3, Token);
        var root = Assert.Single(WorldGltf.Import(doc, "holder.gltf", 255,
            Context(work, reference: (_, _) => (part, "part.gltf"))));
        Assert.Equal(["part", "own"], root.Children.Select(c => c.Name));
        Assert.NotSame(root.Children[0], root.Children[1]);
        Assert.All(root.Children, child => Assert.Same(root, Assert.Single(child.Parents)));
        Assert.Equal(3, work.UsedUnits); // Repeated authored marker still consumes one attempt.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EngineInstanceCyclesRefuseWhileAValidDiamondRemainsShared(bool longer)
    {
        JsonArray cyclic = longer
            ? [Node("root", [1], 1), Node("branch", [2], 2), Node("back", mark: 1)]
            : [Node("root", [1], 1), Node("back", mark: 1)];
        var doc = Read(cyclic); // Ordinary glTF edges are acyclic; the engine instance markers introduce the cycle.
        Assert.Contains("own ancestor", Assert.Throws<InvalidDataException>(() => WorldGltf.Import(doc, "cycle.gltf", 255, Context())).Message);

        var diamond = Read([Node("root", [1, 2]), Node("a", [3]), Node("b", [4]), Node("leaf", mark: 1), Node("copy", mark: 1)]);
        var root = Assert.Single(WorldGltf.Import(diamond, "diamond.gltf", 255, Context()));
        Assert.Same(Assert.Single(root.Children[0].Children), Assert.Single(root.Children[1].Children));
        Assert.Equal(root.Children, root.Children[0].Children[0].Parents);
    }

    [Fact]
    public void DuplicateAttemptsAndSeparateImportsShareTheSuppliedWorkAllowance()
    {
        var doc = Read([Node("root", [1, 2, 3]), Node("first", mark: 1), Node("copy", mark: 1), Node("copy2", mark: 1)]);
        LookupWorkBudget work = new(5, Token);
        var context = Context(work);
        Assert.Single(Assert.Single(WorldGltf.Import(doc, "first.gltf", 255, context)).Children);
        Assert.Contains("node lookup work", Assert.Throws<InvalidDataException>(() => WorldGltf.Import(doc, "second.gltf", 255, context)).Message);
        Assert.Equal(5, work.UsedUnits);
        Assert.True(work.Exhausted);
        Assert.Single(Assert.Single(WorldGltf.Import(doc, "fresh.gltf", 255, Context())).Children);
    }

    [Fact]
    public void CancellationBetweenConstructionAndLinkingDoesNotAddOneSidedEdges()
    {
        var doc = Read([Node("root", [1]), Node("leaf")]);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        List<WorldNode> built = [];
        var context = Context(token: canceled.Token, imported: node =>
        {
            built.Add(node);
            if (node.Name == "leaf") canceled.Cancel();
        });
        var error = Assert.Throws<OperationCanceledException>(() => WorldGltf.Import(doc, "cancel.gltf", 255, context));
        Assert.Equal(canceled.Token, error.CancellationToken);
        Assert.Equal(2, built.Count);
        Assert.Empty(built[0].Children); Assert.Empty(built[1].Parents);
        Assert.Single(Assert.Single(WorldGltf.Import(doc, "retry.gltf", 255, Context())).Children);
    }

    [Fact]
    public void AssemblySharesLinkAdmissionAcrossSeparateModelLoads()
    {
        var model = Bytes([Node("root", [1]), Node("leaf")]);
        var files = new Files(new()
        {
            ["data/models/part.gltf"] = model,
            ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes("SetModelDirectory ../data/models\nLoadGameGen part.gltf first\nLoadGameGen part.gltf second\nGameZWriteZBDFile out.zbd\n")
        });
        var limited = new WorldAssembler(files, Token) { LookupWorkLimit = 1 };
        Assert.Contains("node lookup work", Assert.Throws<InvalidDataException>(() => limited.Assemble("m1.gs")).Message);
        Assert.Null(limited.WorldFile); // The failed candidate never reaches the write instruction.
        var world = new WorldAssembler(files, Token) { LookupWorkLimit = 2 }.Assemble("m1.gs");
        Assert.Equal(6, world.Nodes.Count);
        var first = world.Nodes.Single(n => n.Name == "first");
        var second = world.Nodes.Single(n => n.Name == "second");
        Assert.NotSame(Assert.Single(first.Children), Assert.Single(second.Children));
        Assert.Equal(model, files.Read("data/models/part.gltf", Token, ProjectReadLimits.Model()));
    }

    [Fact]
    public async Task CyclicPreviewCandidateLeavesExistingPreviewSourcesAndHistoryUnchanged()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        const string model = "data/m1/models/m1.gltf";
        byte[] source = File.ReadAllBytes(fixture.Path(model));
        long revision = workspace.Revision;
        string previews = SourceWorlds.PreviewRoot(fixture.Project);
        var baseline = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(previews, "baseline"), token: Token);
        byte[] shown = File.ReadAllBytes(baseline.WorldPath);
        byte[] cyclic = Bytes([Node("root", [1], 1), Node("back", mark: 1)]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m1",
            Path.Combine(previews, "rejected"), new Dictionary<string, byte[]> { [model] = cyclic }, token: Token));
        Assert.Contains("own ancestor", error.Message);
        Assert.Equal(source, File.ReadAllBytes(fixture.Path(model)));
        Assert.Equal(shown, File.ReadAllBytes(baseline.WorldPath));
        Assert.False(File.Exists(Path.Combine(previews, "rejected", "m1", "gamez.zbd")));
        Assert.Empty(workspace.History); Assert.False(workspace.IsDirty); Assert.Equal(revision, workspace.Revision);
        var retry = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(previews, "retry"), token: Token);
        Assert.Equal(shown, File.ReadAllBytes(retry.WorldPath));
    }

    private static WorldGltf.ImportContext Context(LookupWorkBudget? work = null, CancellationToken? token = null,
        Action<WorldNode>? imported = null, Func<string, string, (GltfDocument, string)>? reference = null) => new()
    {
        World = new(), Token = token ?? Token, LinkWork = work ?? new(token: token ?? Token),
        Reference = reference ?? ((_, _) => throw new InvalidDataException("Unexpected external reference.")),
        TextureName = (_, _, _) => throw new InvalidDataException("Unexpected texture."),
        NodeImported = imported == null ? null : (node, _, _, _) => imported(node)
    };

    private static JsonObject Node(string name, int[]? children = null, int? mark = null)
    {
        JsonObject node = new() { ["name"] = name };
        if (children != null) node["children"] = new JsonArray(children.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        if (mark != null) node["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["instance"] = mark.Value } };
        return node;
    }

    private static byte[] Bytes(JsonArray nodes) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["asset"] = new JsonObject { ["version"] = "2.0" }, ["nodes"] = nodes,
        ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }), ["scene"] = 0
    }.ToJsonString());

    private static GltfDocument Read(JsonArray nodes) => GltfDocument.Read(Bytes(nodes), _ => throw new InvalidDataException("Unexpected buffer."), Token);

    private sealed class Files(Dictionary<string, byte[]> contents) : IProjectFiles
    {
        public bool Exists(string relative) => contents.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested(); byte[] bytes = contents[relative]; limits.Validate(bytes); return bytes.ToArray();
        }
    }
}
