using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldSourceCopyBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ReaderAdmittedSharedReferencesReserveTheirFolderProductBeforeOutput()
    {
        byte[] input = World(4, 6);
        var read = Read(input);
        var roots = read.Nodes.Where(n => n.Name.StartsWith("load", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, roots.Length);
        Assert.All(roots, r => Assert.Same(roots[0].Children[0], r.Children[0]));
        var script = Script(4);
        var originalRows = script.Select(row => row.ToArray()).ToArray();
        var refused = Assert.Throws<InvalidDataException>(() => Reconstruct(input, script, 4_000));
        Assert.Contains("model placement", refused.Message);

        var outputs = Reconstruct(input, script);
        var zones = SourceMapZones.Parse(outputs.Single(o => o.Path == "data/m1/meta/zones.json").Bytes, Token);
        Assert.Equal(4 * 7, zones.Assets.Count);
        Assert.Equal(7 * 2 + 1, outputs.Count); // Four logical folder copies share seven physical geometries.
        Assert.Equal(outputs.Count, outputs.Select(o => o.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        for (int folder = 0; folder < 4; folder++)
        {
            string directory = $"data/f{folder:00}";
            Assert.True(zones.TryGetAsset(directory + "/model.gltf", out var model));
            Assert.Equal("r0.gltf", Assert.Single(model.References).Spelling);
            for (int reference = 0; reference < 6; reference++)
            {
                Assert.True(zones.TryGetAsset($"{directory}/r{reference}.gltf", out var asset));
                if (reference < 5) Assert.Equal($"r{reference + 1}.gltf", Assert.Single(asset.References).Spelling);
                else Assert.Empty(JsonNode.Parse(outputs.Single(o => o.Path == asset.GeometryPath).Bytes)!["nodes"]!.AsArray());
            }
        }
        Assert.Equal(originalRows, script.Select(row => row.ToArray()).ToArray());
        // Fresh reader-owned input after refusal produces exactly the same paths and bytes on every call.
        var again = Reconstruct(input, script);
        Assert.Equal(outputs.Select(o => o.Path), again.Select(o => o.Path));
        for (int i = 0; i < outputs.Count; i++) Assert.Equal(outputs[i].Bytes, again[i].Bytes);
    }

    [Fact]
    public void DuplicateIncomingReferencesKeepOneCopyAndFullAuthoredFolderIdentity()
    {
        const string folder = "data/authored_folder_with_a_longer_name";
        byte[] input = World(2, 3, duplicate: true);
        var script = Script(2, folder);
        var outputs = Reconstruct(input, script);
        // Both equal loads in the same folder reuse model.gltf; repeated incoming references reuse r0..r2.
        Assert.Equal(9, outputs.Count);
        Assert.All(outputs.Where(o => !o.Path.EndsWith("/meta/zones.json", StringComparison.Ordinal)), o => Assert.StartsWith(folder + "/", o.Path));
        Assert.Contains(outputs, o => o.Path == folder + "/r2.gltf");
        Assert.Equal(outputs.Select(o => o.Path), Reconstruct(input, script).Select(o => o.Path));
    }

    [Fact]
    public void FirstPathOrderedSeedRetainsItsTextureContextWhenReferencesConverge()
    {
        var world = Read(World(2, 1));
        WorldTexture texture = new("rock"); WorldMaterial material = new() { Texture = texture, Flags = 0x1FF };
        WorldModel model = new();
        model.Vertices.AddRange([new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)]);
        model.Polygons.Add(new() { Material = material, Vertices = [0, 1, 2], Uvs = [new(0, 0), new(1, 0), new(0, 1)] });
        WorldNode shape = Node("shape"); shape.Model = model;
        Link(world.Nodes.Single(n => n.Name == "r0.flt"), shape);
        world.Nodes.Add(shape); world.Models.Add(model); world.Materials.Add(material); world.Textures.Add(texture);
        byte[] bytes = GameZWriter.Write(world, Token);
        var script = GameGenScriptText.Tokenize("NewWorld world\nSetModelDirectory ../data/models\n"
            + "SetTextureDirectory ../data/second/textures\nLoadGameGen z.flt load0\n"
            + "SetTextureDirectory ../data/first/textures\nLoadGameGen a.flt load1\nGameZWriteZBDFile world.zbd\n");
        var outputs = WorldSources.Reconstruct([new(1, Read(bytes))], _ => script, (_, _) => null,
            new HashSet<string>(["data/first/textures/rock.png", "data/second/textures/rock.png"]), _ => 0, [], Token);
        Assert.Equal(5, outputs.Count); // Two logical loads share geometry; the reference and map zones remain separate.
        var zones = SourceMapZones.Parse(outputs.Single(o => o.Path == "data/m1/meta/zones.json").Bytes, Token);
        Assert.Equal(3, zones.Assets.Count);
        var reference = JsonNode.Parse(outputs.Single(o => o.Path == "data/models/r0.gltf").Bytes)!;
        // Script order is z then a; the established Path-ordered FIFO makes a's context authoritative.
        Assert.Equal("../first/textures/rock.png", (string?)reference["images"]![0]!["uri"]);
    }

    [Fact]
    public void MetadataAndOutputAllowancesRemainIndependentAndCancellationPermitsANewCall()
    {
        byte[] input = World(2, 2); var script = Script(2);
        var complete = Reconstruct(input, script);
        long outputBytes = complete.Sum(o => o.Bytes.LongLength);
        Assert.Equal(outputBytes, Reconstruct(input, script, outputBytes: outputBytes).Sum(o => o.Bytes.LongLength));
        Assert.Contains("memory limit", Assert.Throws<IOException>(() => Reconstruct(input, script, outputBytes: outputBytes - 1)).Message);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Reconstruct(input, script, token: canceled.Token));
        Assert.Equal(complete.Select(o => o.Path), Reconstruct(input, script).Select(o => o.Path));
    }

    [Fact]
    public void PairPathAndSortReservationsRefuseBeforeAdvancingTheirState()
    {
        const string folder = "data/full_identity", stem = "reference";
        WorldCopyBudget measure = new(token: Token);
        measure.Pair(folder); Assert.Equal(folder + "/" + stem + ".gltf", measure.Path(folder, stem));
        measure.Sort(4, folder.Length + stem.Length);
        long exact = Math.Max(measure.Storage, measure.Work);
        WorldCopyBudget admitted = new(exact, Token);
        admitted.Pair(folder); admitted.Path(folder, stem); admitted.Sort(4, folder.Length + stem.Length);
        Assert.Equal((measure.Storage, measure.Work), (admitted.Storage, admitted.Work));
        WorldCopyBudget refused = new(exact - 1, Token);
        refused.Pair(folder); refused.Path(folder, stem);
        var before = (refused.Storage, refused.Work);
        Assert.Throws<InvalidDataException>(() => refused.Sort(4, folder.Length + stem.Length));
        Assert.Equal(before, (refused.Storage, refused.Work));
        WorldCopyBudget path = new(200, Token);
        Assert.Throws<InvalidDataException>(() => path.Path(new string('x', 100), stem));
        Assert.Equal(0, path.Storage);
        Assert.Equal("data/a.gltf", path.Path("data", "a"));
        var pathBefore = (path.Storage, path.Work);
        Assert.Throws<InvalidDataException>(() => path.Sort(int.MaxValue, long.MaxValue));
        Assert.Equal(pathBefore, (path.Storage, path.Work));
        using CancellationTokenSource canceled = new();
        WorldCopyBudget stopped = new(token: canceled.Token); stopped.Pair(folder); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => stopped.Lookup(folder)); // Even repeated-key lookups observe cancellation.
    }

    private static GameZWorld Read(byte[] bytes) => GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("m1/gamez.zbd", bytes, token: Token), Token);
    private static List<WorldSources.Output> Reconstruct(byte[] bytes, IReadOnlyList<IReadOnlyList<string>> script,
        long placement = WorldCopyBudget.MaximumUnits, long outputBytes = ReconstructionBudget.MaximumBytes, CancellationToken? token = null)
        => WorldSources.Reconstruct([new(1, Read(bytes))], name => name == "m1.gs" ? script : null,
            (_, _) => null, new HashSet<string>(), _ => 0, [], token ?? Token,
            maximumOutputBytes: outputBytes, maximumPlacementUnits: placement);

    private static IReadOnlyList<IReadOnlyList<string>> Script(int roots, string? folder = null)
        => GameGenScriptText.Tokenize("NewWorld world\n" + string.Concat(Enumerable.Range(0, roots).Select(i =>
            $"SetModelDirectory ../{folder ?? $"data/f{i:00}"}\nLoadGameGen model.flt load{i}\n")) + "GameZWriteZBDFile world.zbd\n");

    private static byte[] World(int roots, int references, bool duplicate = false)
    {
        GameZWorld world = new(); world.Nodes.Add(new("world", WorldNodeClass.World));
        WorldNode[] chain = [.. Enumerable.Range(0, references).Select(i => Node($"r{i}.flt"))];
        for (int i = 0; i + 1 < chain.Length; i++) Link(chain[i], chain[i + 1]);
        for (int i = 0; i < roots; i++)
        {
            WorldNode root = Node($"load{i}"); world.Nodes.Add(root); Link(root, chain[0]);
            if (duplicate) Link(root, chain[0]);
        }
        world.Nodes.AddRange(chain);
        return GameZWriter.Write(world, Token);
    }
    private static WorldNode Node(string name)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried };
        node.SetPayloadInt(0, 0x28); return node;
    }
    private static void Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); }
}
