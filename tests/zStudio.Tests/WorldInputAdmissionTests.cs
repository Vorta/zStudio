using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldInputAdmissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Empty = """{"asset":{"version":"2.0"},"nodes":[],"scenes":[{"nodes":[]}],"scene":0}""";

    [Fact]
    public void ActualExternalBytesAreAdmittedBeforeTheSecondPayloadIsCopied()
    {
        byte[] json = Model(("first.bin", 1), ("second.bin", 1));
        List<long> allowances = []; int copied = 0; byte[] payload = new byte[6];
        byte[] Read(string _, long remaining)
        {
            allowances.Add(remaining);
            ProjectReadLimits.Bytes(remaining).Validate(payload);
            copied++;
            return payload.ToArray();
        }
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, Read, 10, Token));
        Assert.Equal([10L, 4L], allowances);
        Assert.Equal(1, copied);
        allowances.Clear(); copied = 0;
        Assert.Empty(GltfDocument.Read(json, Read, 12, Token).Roots);
        Assert.Equal([12L, 6L], allowances);
        Assert.Equal(2, copied);
    }

    [Theory]
    [InlineData("./buffer.bin")]
    [InlineData("sub/../buffer.bin")]
    [InlineData("BUFFER.bin")]
    [InlineData("%62uffer.bin")]
    public void ProjectBufferAliasesShareOneAllowanceAndKeepTheirFullIdentity(string alias)
    {
        byte[] json = Model(("buffer.bin", 6), (alias, 6));
        List<string> paths = [];
        var document = WorldAssembler.ReadModel(json, "data/m1/models/source.gltf", (path, remaining) =>
        {
            paths.Add(path);
            Assert.Equal(6, remaining);
            return new byte[6];
        }, Token, 6);
        Assert.Empty(document.Roots);
        Assert.Equal(["data/m1/models/buffer.bin"], paths);
    }

    [Fact]
    public void InlineAndGlbBytesReduceTheExternalAllowance()
    {
        byte[] external = Model(("data:application/octet-stream;base64,AQIDBA==", 4), ("next.bin", 4));
        long offered = -1;
        _ = GltfDocument.Read(external, (_, remaining) => { offered = remaining; return new byte[4]; }, 8, Token);
        Assert.Equal(4, offered);

        byte[] glb = Glb(Model((null, 8), ("next.bin", 4)), new byte[8]);
        offered = -1;
        _ = GltfDocument.Read(glb, (_, remaining) => { offered = remaining; return new byte[4]; }, 12, Token);
        Assert.Equal(4, offered);
        int calls = 0;
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(glb, (_, _) => { calls++; return new byte[4]; }, 11, Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void EqualBufferBasenamesInDifferentFoldersRemainDistinct()
    {
        List<(string Path, long Remaining)> reads = [];
        _ = WorldAssembler.ReadModel(Model(("one/buffer.bin", 6), ("two/buffer.bin", 6)), "data/model.gltf", (path, remaining) =>
        {
            reads.Add((path, remaining)); return new byte[6];
        }, Token, 12);
        Assert.Equal(new[] { ("data/one/buffer.bin", 12L), ("data/two/buffer.bin", 6L) }, reads);
    }

    [Fact]
    public void CancellationDoesNotResolveBuffersAndASecondReadHasAFreshAllowance()
    {
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        byte[] json = Model(("buffer.bin", 4)); int calls = 0;
        byte[] Read(string _, long remaining) { calls++; Assert.Equal(4, remaining); return new byte[4]; }
        Assert.Throws<OperationCanceledException>(() => WorldAssembler.ReadModel(json, "data/model.gltf", Read, canceled.Token, 4));
        Assert.Equal(0, calls);
        _ = WorldAssembler.ReadModel(json, "data/model.gltf", Read, Token, 4);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CancellationAfterOneBufferCannotCarryPartialCacheIntoTheNextRead()
    {
        using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        byte[] json = Model(("first.bin", 4), ("second.bin", 4)); int calls = 0;
        Assert.Throws<OperationCanceledException>(() => WorldAssembler.ReadModel(json, "data/model.gltf", (_, _) =>
        {
            calls++; canceled.Cancel(); return new byte[4];
        }, canceled.Token, 8));
        Assert.Equal(1, calls);
        List<long> remaining = [];
        _ = WorldAssembler.ReadModel(json, "data/model.gltf", (_, allowance) => { remaining.Add(allowance); return new byte[4]; }, Token, 8);
        Assert.Equal([8L, 4L], remaining);
    }

    [Fact]
    public void AssemblerRejectsJsonAtTheProviderButAcceptsGlbWithLargerBin()
    {
        var files = new Files();
        files.Put("gamegen/m1.gs", Load("model.gltf"));
        files.Put("data/models/model.gltf", Empty + new string(' ', 512));
        Assert.Throws<InvalidDataException>(() => new WorldAssembler(files, Token) { ModelJsonByteLimit = 256 }.Assemble("m1.gs"));
        Assert.DoesNotContain("data/models/model.gltf", files.Copied);
        Assert.Contains(files.Requests, r => r.Path == "data/models/model.gltf");

        files.Put("gamegen/m1.gs", Load("model.glb"));
        files.Put("data/models/model.glb", Glb(Model((null, 1024)), new byte[1024]));
        var world = new WorldAssembler(files, Token) { ModelJsonByteLimit = 256, ModelBufferByteLimit = 1024 }.Assemble("m1.gs");
        Assert.Contains(world.Nodes, n => n.Name == "loaded");
        Assert.Contains("data/models/model.glb", files.Copied);
    }

    [Fact]
    public void AssemblerPassesTheActualRemainingBufferBudgetThroughItsProvider()
    {
        var files = new Files();
        files.Put("gamegen/m1.gs", Load("model.gltf"));
        files.Put("data/models/model.gltf", Model(("a.bin", 1), ("b.bin", 1)));
        files.Put("data/models/a.bin", new byte[6]); files.Put("data/models/b.bin", new byte[6]);
        Assert.Throws<InvalidDataException>(() => new WorldAssembler(files, Token) { ModelBufferByteLimit = 10 }.Assemble("m1.gs"));
        Assert.Equal(4, files.Requests.Single(r => r.Path == "data/models/b.bin").Maximum);
        Assert.Contains("data/models/a.bin", files.Copied);
        Assert.DoesNotContain("data/models/b.bin", files.Copied);
        Assert.Contains(new WorldAssembler(files, Token) { ModelBufferByteLimit = 12 }.Assemble("m1.gs").Nodes, n => n.Name == "loaded");
    }

    [Fact]
    public void RepeatedWorldLoadsKeepSeparateInstancesWhileTheirBufferAliasesStayShared()
    {
        var files = new Files();
        files.Put("gamegen/m1.gs", "SetModelDirectory ../data/models\nLoadGameGen model.gltf first\nLoadGameGen model.gltf second\nGameZWriteZBDFile out.zbd\n");
        var json = JsonNode.Parse(Model(("a.bin", 6), ("./a.bin", 6)))!;
        json["nodes"]!.AsArray().Add(new JsonObject { ["name"] = "inside" });
        json["scenes"]![0]!["nodes"]!.AsArray().Add(0);
        files.Put("data/models/model.gltf", json.ToJsonString()); files.Put("data/models/a.bin", new byte[6]);
        var world = new WorldAssembler(files, Token) { ModelBufferByteLimit = 6 }.Assemble("m1.gs");
        Assert.Single(world.Nodes, n => n.Name == "first"); Assert.Single(world.Nodes, n => n.Name == "second");
        var children = world.Nodes.Where(n => n.Name == "inside").ToArray();
        Assert.Equal(2, children.Length); Assert.NotSame(children[0], children[1]);
        Assert.Equal(2, files.Copied.Count(p => p == "data/models/a.bin"));
    }

    [Fact]
    public void AssemblerRecipeAdmissionRetainsItsSeparate64MiBPolicy()
    {
        var files = new Files();
        files.Put("gamegen/m1.gs", "SetModelDirectory ../data/models\nNewWorld world\nFindNode world\nGameGenSetWorld world\nWorldOrigin 0 512\nWorldExtents 512 -512\nWorldPartition 256 -256\nLoadGameGen model.gltf database\nGameZWriteZBDFile out.zbd\n");
        files.Put("data/models/model.gltf", """{"asset":{"version":"2.0"},"nodes":[{"name":"terrain","extras":{"recoil":{"terrain":"land.terrain.json"}}}],"scenes":[{"nodes":[0]}],"scene":0}""");
        files.Put("data/models/land.terrain.json", "{}");
        // A small malformed recipe reaches the recipe parser. The producer boundary must not accidentally give this
        // JSON format the model/script 16/32 MiB allowance; real recipe admission remains 64 MiB before reading it.
        Assert.Throws<InvalidDataException>(() => new WorldAssembler(files, Token).Assemble("m1.gs"));
        Assert.Equal(64L << 20, files.Requests.Single(r => r.Path == "data/models/land.terrain.json").Maximum);
        Assert.Contains("data/models/land.terrain.json", files.Copied);
    }

    [Fact]
    public void ScriptAdmissionPrecedesCopyAndRepeatedSourcesSpendTheirBytesOnce()
    {
        const string main = "source child.gw\nsource child.gw\nGameZWriteZBDFile out.zbd\n";
        const string child = "NewObject3D object\n";
        var files = new Files(); files.Put("gamegen/m1.gs", main); files.Put("gamegen/child.gw", child);
        long exact = main.Length + child.Length;
        Assert.Throws<InvalidDataException>(() => new WorldAssembler(files, Token) { ScriptSourceByteLimit = exact - 1 }.Assemble("m1.gs"));
        Assert.DoesNotContain("gamegen/child.gw", files.Copied);
        files.Copied.Clear(); files.Requests.Clear();
        var world = new WorldAssembler(files, Token) { ScriptSourceByteLimit = exact }.Assemble("m1.gs");
        Assert.Equal(2, world.Nodes.Count(n => n.Name == "object"));
        Assert.Single(files.Copied, p => p == "gamegen/child.gw");
    }

    [Fact]
    public void LookupScriptsPassTheRemainingAllowanceBeforeMaterialization()
    {
        const string main = "source child.gw\nsource child.gw\nFindNode root\n";
        const string child = "FindNode child\n";
        var files = new Files(); files.Put("gamegen/m1_zbd.gs", main); files.Put("gamegen/child.gw", child);
        byte[]? Read(string path, long remaining) => files.Exists(path) ? files.Read(path, Token, ProjectReadLimits.Text(remaining)) : null;
        Assert.Throws<InvalidDataException>(() => WorldLookups.FindNodes(Read, "m1", Token, main.Length + child.Length - 1, 1000));
        Assert.DoesNotContain("gamegen/child.gw", files.Copied);
        files.Copied.Clear();
        var names = WorldLookups.FindNodes(Read, "m1", Token, main.Length + child.Length, 1000);
        Assert.Equal(["child", "root"], names.Select(n => n.Name));
        Assert.Single(files.Copied, p => p == "gamegen/child.gw");
    }

    [Fact]
    public void TerrainRefusalPreservesHistoryAndCanBeRetriedWithAdmittedBuffers()
    {
        using SourceWorldFixture fixture = new();
        const string database = "data/m1/models/m1.gltf";
        const string model = "data/m1/models/surface.gltf";
        var root = JsonNode.Parse(File.ReadAllBytes(fixture.Path(fixture.Tank)))!.AsObject();
        byte[] bin = File.ReadAllBytes(fixture.Path("data/m2/models/bft/tank.bin"));
        root["buffers"]![0]!["uri"] = "surface.bin";
        root["buffers"]!.AsArray().Add(new JsonObject { ["uri"] = "extra-a.bin", ["byteLength"] = 1 });
        root["buffers"]!.AsArray().Add(new JsonObject { ["uri"] = "extra-b.bin", ["byteLength"] = 1 });
        fixture.Write(model, root.ToJsonString()); fixture.Write("data/m1/models/surface.bin", bin);
        fixture.Write("data/m1/models/extra-a.bin", new byte[6]); fixture.Write("data/m1/models/extra-b.bin", new byte[6]);
        SourceWorkspace workspace = new(fixture.Project);
        long revision = workspace.Revision;
        Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, database, model, ["hull"], null, Token,
            SourceProject.MaximumFiles, maximumModelBufferBytes: bin.Length + 10));
        Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo); Assert.Equal(revision, workspace.Revision);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SourceTerrain.Create(workspace, database, model, ["hull"], token: canceled.Token));
        Assert.False(workspace.CanUndo);
        Assert.Contains("hull", SourceTerrain.MeshNodes(workspace, model, Token, 32 * 1024, bin.Length + 12));
        Assert.NotNull(SourceTerrain.Create(workspace, database, model, ["hull"], null, Token,
            SourceProject.MaximumFiles, maximumModelBufferBytes: bin.Length + 12));
        workspace.Undo(); Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void TerrainModelListingAdmitsJsonWithoutConfusingGlbBinForJson()
    {
        using SourceWorldFixture fixture = new(); SourceWorkspace workspace = new(fixture.Project);
        const string path = "data/m1/models/padded.gltf";
        fixture.Write(path, Empty + new string(' ', 512));
        Assert.Throws<InvalidDataException>(() => SourceTerrain.MeshNodes(workspace, path, Token, 256, 1024));
        fixture.Write("data/m1/models/large-bin.glb", Glb(Model((null, 1024)), new byte[1024]));
        Assert.Empty(SourceTerrain.MeshNodes(workspace, "data/m1/models/large-bin.glb", Token, 256, 1024));
        Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo);
    }

    [Theory]
    [InlineData(".zrd", "( shared )")]
    [InlineData(".zan", "FRAME 0")]
    public void ExpandedReferenceDependenciesSpendCombinedBytesEvenWithOnlyRepeatedWords(string extension, string text)
    {
        using SourceWorldFixture fixture = new(); SourceWorkspace workspace = new(fixture.Project);
        string first = "data/m1/zrdr/first" + extension, second = "data/m1/zrdr/second" + extension;
        fixture.Write(first, text); fixture.Write(second, text);
        // The dependency identities can come from a successful earlier build. An external edit can enlarge both
        // without changing the workspace revision or introducing another distinct retained reference name.
        byte[] expanded = Encoding.ASCII.GetBytes(text + new string(' ', 1024));
        fixture.Write(first, expanded); fixture.Write(second, expanded);
        long revision = workspace.Revision;
        var error = Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.References(workspace, [first, second], Token, expanded.Length * 2L - 1));
        Assert.Contains("combined", error.Message);
        Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo); Assert.Equal(revision, workspace.Revision);
        var exact = SourceTerrainConversion.References(workspace, [first, second, first], Token, expanded.Length * 2L);
        Assert.NotEmpty(exact.Names);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SourceTerrainConversion.References(workspace, [first, second], canceled.Token, expanded.Length * 2L));
        Assert.Equal(exact.Names, SourceTerrainConversion.References(workspace, [first, second], Token, expanded.Length * 2L).Names);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void ResourcesAndScriptsSourcedOutsideTheInventoryShareOneInputAllowance()
    {
        using SourceWorldFixture fixture = new(); SourceWorkspace workspace = new(fixture.Project);
        const string resource = "data/m1/zrdr/extra.zrd", main = "gamegen/reference.gs", child = "gamegen/reference-child.gw";
        const string data = "( shared )", source = "source reference-child.gw\n", sourced = "FindNode target\n";
        fixture.Write(resource, data); fixture.Write(main, source); fixture.Write(child, sourced);
        long total = data.Length + source.Length + sourced.Length;
        Assert.Contains("combined", Assert.Throws<InvalidDataException>(() =>
            SourceTerrainConversion.References(workspace, [resource, main], main, Token, total - 1)).Message);
        Assert.Contains("target", SourceTerrainConversion.References(workspace, [resource, main], main, Token, total).Names);
        Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo);
    }

    private static string Load(string model) => $"SetModelDirectory ../data/models\nLoadGameGen {model} loaded\nGameZWriteZBDFile out.zbd\n";
    private static byte[] Model(params (string? Uri, int Length)[] buffers)
    {
        var root = JsonNode.Parse(Empty)!.AsObject(); JsonArray array = [];
        foreach (var (uri, length) in buffers)
        {
            JsonObject buffer = new() { ["byteLength"] = length };
            if (uri != null) buffer["uri"] = uri;
            array.Add(buffer);
        }
        root["buffers"] = array; return Encoding.UTF8.GetBytes(root.ToJsonString());
    }
    private static byte[] Glb(byte[] json, byte[] bin)
    {
        int jsonLength = (json.Length + 3) & ~3, binLength = (bin.Length + 3) & ~3;
        byte[] result = new byte[28 + jsonLength + binLength];
        "glTF"u8.CopyTo(result); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), result.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 0x4E4F534A);
        result.AsSpan(20, jsonLength).Fill((byte)' '); json.CopyTo(result, 20);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20 + jsonLength), binLength);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24 + jsonLength), 0x004E4942);
        bin.CopyTo(result, 28 + jsonLength); return result;
    }
    private sealed class Files : IProjectFiles
    {
        private readonly Dictionary<string, byte[]> content = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Path, long Maximum)> Requests { get; } = [];
        public List<string> Copied { get; } = [];
        public void Put(string path, string text) => Put(path, Encoding.UTF8.GetBytes(text));
        public void Put(string path, byte[] bytes) => content[path] = bytes;
        public bool Exists(string relative) => content.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested(); Requests.Add((relative, limits.MaximumBytes));
            var bytes = content[relative]; limits.Validate(bytes);
            Copied.Add(relative); return bytes.ToArray();
        }
    }
}
