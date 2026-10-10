using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound22FormatTests
{
    private sealed class CountedNodes(IReadOnlyList<WorldNode> nodes) : IReadOnlyCollection<WorldNode>
    {
        public int Count => nodes.Count;
        public int Visits { get; private set; }
        public IEnumerator<WorldNode> GetEnumerator()
        {
            foreach (var node in nodes) { Visits++; yield return node; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Files(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public bool Exists(string relative) => files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = files[relative]; limits.Validate(result); return result; }
    }
    private static WorldAssembler Assembler(string script, string? model = null, string? part = null)
    {
        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase)
        {
            ["gamegen/test.gs"] = Encoding.UTF8.GetBytes(script),
        };
        if (model != null) files["data/common/models/model.gltf"] = Encoding.UTF8.GetBytes(model);
        if (part != null) files["data/common/models/part.gltf"] = Encoding.UTF8.GetBytes(part);
        return new(new Files(files), Token);
    }
    private const string Load = "SetModelDirectory ../data/common/models\nLoadGameGen model.gltf root\nGameZWriteZBDFile gamez.zbd";

    [Theory]
    [InlineData("\"translation\":[100,0,0]")]
    [InlineData("\"scale\":[2,1,1]")]
    [InlineData("\"rotation\":[0,0,1,0]")]
    [InlineData("\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,100,0,0,1]")]
    public void LodTransformsAreRefusedBeforeWorldPoolsChange(string transform)
    {
        string json = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"band\"," + transform
            + ",\"children\":[1],\"extras\":{\"recoil\":{\"class\":\"lod\"}}},{\"name\":\"child\",\"translation\":[5,0,0]}]}";
        var assembler = Assembler(Load, json);
        Assert.Contains("LOD", Assert.Throws<InvalidDataException>(() => assembler.Assemble("test.gs")).Message);
        Assert.Empty(assembler.World.Nodes);
        Assert.Empty(assembler.World.Models);
        Assert.Empty(assembler.World.Materials);
    }

    [Fact]
    public void IdentityLodBelowTransformedObjectKeepsTheParentPlacement()
    {
        const string json = """
            {"asset":{"version":"2.0"},"nodes":[
              {"name":"holder","translation":[100,0,0],"children":[1]},
              {"name":"band","translation":[0,0,0],"children":[2],"extras":{"recoil":{"class":"lod"}}},
              {"name":"child","translation":[5,0,0]}]}
            """;
        var assembler = Assembler(Load, json);
        var world = assembler.Assemble("test.gs");
        Assert.Empty(assembler.Warnings);
        Assert.Equal(100, WorldUpdate.LocalMatrix(world.Nodes.Single(n => n.Name == "holder"))!.Value.Translation.X);
        Assert.Equal(WorldNodeClass.Lod, world.Nodes.Single(n => n.Name == "band").Class);
        Assert.Equal(5, WorldUpdate.LocalMatrix(world.Nodes.Single(n => n.Name == "child"))!.Value.Translation.X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceLoadsUseTheFirstSharedDefinitionWhileEditorUpdatesRequireAgreement(bool referenced)
    {
        const string copies = """
            {"asset":{"version":"2.0"},"nodes":[
              {"name":"shared","translation":[5,0,0],"extras":{"recoil":{"instance":1}}},
              {"name":"shared","translation":[99,0,0],"extras":{"recoil":{"instance":1}}}]}
            """;
        const string reference = """{"asset":{"version":"2.0"},"nodes":[{"name":"part","extras":{"recoil":{"ref":"part.gltf"}}}]}""";
        var assembler = Assembler(Load, referenced ? reference : copies, referenced ? copies : null);
        var world = assembler.Assemble("test.gs");
        var shared = Assert.Single(world.Nodes, n => n.Name == "shared");
        Assert.Equal(5, WorldUpdate.LocalMatrix(shared)!.Value.Translation.X);
        var edited = GltfDocument.Read(Encoding.UTF8.GetBytes(copies), _ => throw new InvalidOperationException(), Token);
        Assert.Contains("copies", Assert.Throws<InvalidDataException>(() => WorldGltf.CheckInstances(edited, "edited.gltf")).Message);
        Assert.Empty(assembler.World.Models);
        Assert.Empty(assembler.World.Materials);
    }

    [Fact]
    public void AgreeingSharedCopiesRemainOneEngineNode()
    {
        const string json = """
            {"asset":{"version":"2.0"},"nodes":[
              {"name":"left","children":[2]},{"name":"right","children":[3]},
              {"name":"shared","translation":[5,0,0],"extras":{"recoil":{"instance":1}}},
              {"name":"shared","translation":[5,0,0],"extras":{"recoil":{"instance":1}}}]}
            """;
        var assembler = Assembler(Load, json);
        var world = assembler.Assemble("test.gs");
        Assert.Empty(assembler.Warnings);
        var shared = Assert.Single(world.Nodes, n => n.Name == "shared");
        Assert.Equal(2, shared.Parents.Count);
    }

    private static (GameZWorld World, WorldNode Node) ObjectWorld()
    {
        GameZWorld world = new();
        WorldNode root = new("world", WorldNodeClass.World), node = new("object", WorldNodeClass.Object3D);
        node.SetPayloadInt(0, 0x2A);
        node.SetPayloadFloat(4, 1);
        root.Children.Add(node); node.Parents.Add(root); world.Nodes.AddRange([root, node]);
        return (world, node);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    public void ObjectAppearanceIsComparedAtIdentity(int offset)
    {
        var (a, x) = ObjectWorld(); var (b, y) = ObjectWorld();
        y.SetPayloadFloat(offset, 0.25f);
        var comparison = WorldComparer.CompareTree(a, b, token: Token);
        Assert.Equal(WorldComparisonStatus.Changed, comparison.Roots[0].Children[0].Status);
        Assert.Contains(comparison.Differences, d => d.Field.StartsWith("object.", StringComparison.Ordinal));
        x.Parents.Clear(); y.Parents.Clear();
        Assert.False(WorldComparer.Interchangeable(x, y, token: Token));
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x04)]
    [InlineData(0x40)]
    public void ObjectAppearanceFlagsAreNotTransformBookkeeping(int bit)
    {
        var (a, x) = ObjectWorld(); var (b, y) = ObjectWorld();
        y.SetPayloadInt(0, x.PayloadInt(0) ^ bit);
        var comparison = WorldComparer.CompareTree(a, b, token: Token);
        Assert.Contains(comparison.Differences, d => d.Field == "object.flags");
        x.Parents.Clear(); y.Parents.Clear();
        Assert.False(WorldComparer.Interchangeable(x, y, token: Token));
    }

    [Fact]
    public void IdentityTransformBookkeepingAndCachedWorldMatrixStillDoNotCompare()
    {
        var (a, x) = ObjectWorld(); var (b, y) = ObjectWorld();
        // Both local transforms resolve to identity; one stores an authored identity matrix rather than the identity bit.
        y.SetPayloadInt(0, 0x12);
        y.SetPayloadFloat(0x30, 1); y.SetPayloadFloat(0x40, 1); y.SetPayloadFloat(0x50, 1);
        y.SetPayloadFloat(0x60, 123);
        Assert.Empty(WorldComparer.CompareTree(a, b, token: Token).Differences);
        x.Parents.Clear(); y.Parents.Clear();
        Assert.True(WorldComparer.Interchangeable(x, y, token: Token));
    }

    [Theory]
    [InlineData(0x28)] // Inactive appearance values are authored data too.
    [InlineData(0x6E)] // Alpha/color controls and an unknown retained bit.
    public void SourceRoundTripPreservesAuthoredObjectAppearance(int flags)
    {
        var (_, original) = ObjectWorld();
        original.SetPayloadInt(0, flags);
        for (int i = 1; i <= 5; i++) original.SetPayloadFloat(i * 4, i * 0.125f);
        var exported = WorldGltf.Export([original], 0xFF, new() { Texture = _ => throw new InvalidOperationException() }).Write("model.bin", TestContext.Current.CancellationToken);
        var assembler = Assembler(Load, Encoding.UTF8.GetString(exported.Json));
        var imported = Assert.Single(assembler.Assemble("test.gs").Nodes, n => n.Name == "object");
        Assert.Equal(flags & ~0x39, imported.PayloadInt(0) & ~0x39);
        for (int i = 1; i <= 5; i++) Assert.Equal(original.PayloadFloat(i * 4), imported.PayloadFloat(i * 4));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"color\":[1,2]}")]
    [InlineData("{\"flags\":\"0x00000008\"}")]
    public void MalformedAppearanceIsRejectedBeforeImport(string appearance)
    {
        var assembler = Assembler(Load, "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"object\",\"extras\":{\"recoil\":{\"appearance\":" + appearance + "}}}]}");
        Assert.Throws<InvalidDataException>(() => assembler.Assemble("test.gs"));
        Assert.Empty(assembler.World.Nodes);
    }

    [Fact]
    public void ExportRejectsHierarchyDeeperThanItsSourceReaderSupports()
    {
        WorldNode root = new("root", WorldNodeClass.Object3D), current = root;
        for (int i = 0; i < GltfDocument.MaximumDepth; i++)
        {
            WorldNode child = new($"node{i}", WorldNodeClass.Object3D);
            current.Children.Add(child); child.Parents.Add(current); current = child;
        }
        Assert.Contains("hierarchy", Assert.Throws<InvalidDataException>(() => WorldGltf.Export([root], 0xFF, new() { Texture = _ => throw new InvalidOperationException() })).Message);
    }

    [Fact]
    public void DeletingADeepScriptTreeUsesPostorderWithoutProcessRecursion()
    {
        const int count = 12_000;
        StringBuilder script = new();
        for (int i = 0; i < count; i++)
        {
            script.AppendLine($"NewObject3D n{i}");
            if (i > 0) script.AppendLine($"AddChild n{i - 1}");
        }
        script.AppendLine($"DeleteTree n{count - 1}");
        script.AppendLine("NewObject3D replacement\nGameZWriteZBDFile gamez.zbd");
        var assembler = Assembler(script.ToString());
        var world = assembler.Assemble("test.gs");
        var replacement = Assert.Single(world.Nodes);
        Assert.Equal("replacement", replacement.Name);
        Assert.Equal(count - 1, GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)[replacement]);
        Assert.Equal(count - 1, world.FreedSlots.Count);
        Assert.Empty(assembler.Warnings);
    }

    [Fact]
    public void DeleteTreeKeepsAChildThatAnotherParentStillOwns()
    {
        const string script = """
            NewObject3D leaf
            NewObject3D left
            AddChild leaf
            NewObject3D right
            AddChild leaf
            DeleteTree left
            GameZWriteZBDFile gamez.zbd
            """;
        var world = Assembler(script).Assemble("test.gs");
        var leaf = Assert.Single(world.Nodes, n => n.Name == "leaf");
        Assert.Equal("right", Assert.Single(leaf.Parents).Name);
        Assert.DoesNotContain(world.Nodes, n => n.Name == "left");
        Assert.Equal(2, world.Nodes.Count);
    }

    private static WorldNode SharedHierarchy(int levels)
    {
        WorldNode Node(string name)
        {
            WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried };
            node.SetPayloadInt(0, 0x28); return node;
        }
        WorldNode[] layer = [Node("leaf")];
        for (int i = 0; i < levels; i++)
        {
            WorldNode[] next = [Node($"a{i}"), Node($"b{i}")];
            foreach (var n in next) foreach (var child in layer) { n.Children.Add(child); child.Parents.Add(n); }
            layer = next;
        }
        var root = Node("root"); root.Children.AddRange(layer); return root;
    }

    [Fact]
    public void SharedGraphMetadataIsBoundedBeforeCloningTheExpandedTree()
    {
        var root = SharedHierarchy(16); // 34 unique nodes, fewer than 200,000 expanded occurrences.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => WorldGltf.Export([root], 0xFF, new() { Texture = _ => throw new InvalidOperationException() }));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("JSON budget", error.Message);
        Assert.True(allocated < 16 * 1024 * 1024, $"Preflight allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void AWithinBudgetSharedGraphProducesReadableSource()
    {
        var exported = WorldGltf.Export([SharedHierarchy(7)], 0xFF, new() { Texture = _ => throw new InvalidOperationException() }).Write("model.bin", TestContext.Current.CancellationToken);
        var parsed = GltfDocument.Read(exported.Json, _ => exported.Binary, Token);
        WorldGltf.CheckInstances(parsed, "model.gltf");
        Assert.Single(parsed.Roots);
        Assert.True(exported.Json.Length < GltfDocument.MaximumJsonBytes);
    }

    [Fact]
    public void ReferencedPartMembershipIsSnapshottedOnceBeforeCountAndEmission()
    {
        WorldNode root = new("part.flt", WorldNodeClass.Object3D);
        for (int i = 0; i < 10_000; i++) root.Children.Add(new($"node{i}", WorldNodeClass.Object3D));
        CountedNodes content = new(root.Children);
        int snapshots = 0;
        var doc = WorldGltf.Export([root], 0xFF, new()
        {
            Texture = _ => throw new InvalidOperationException(),
            Reference = n => ReferenceEquals(n, root) ? "part.gltf" : null,
            Content = _ => { snapshots++; return content; },
        });
        Assert.Empty(Assert.Single(doc.Roots).Children);
        Assert.Equal(1, snapshots);
        Assert.Equal(content.Count, content.Visits);
    }
}
