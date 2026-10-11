using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldLookupFingerprintBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DistinctRootsShareSignaturesWithoutChangingPairingKeys()
    {
        var (world, roots) = SharedWorld();
        byte[] original = GameZWriter.Write(world, Token);
        LookupWorkBudget sharedWork = new(400, Token);
        WorldComparer.PairKeyMemo shared = new(Token, sharedWork);
        string[] keys = roots.Select(shared.Key).ToArray();
        Assert.Equal(roots.Select(WorldComparer.PairKey), keys);
        // Repeating the old per-root construction spends this same allowance. No large graph or timing assertion.
        LookupWorkBudget separateWork = new(400, Token);
        Assert.Throws<InvalidDataException>(() =>
        {
            foreach (var root in roots) _ = new WorldComparer.PairKeyMemo(Token, separateWork).Key(root);
        });
        Assert.InRange(sharedWork.UsedUnits, 1, 399);
        Assert.Equal(original, GameZWriter.Write(world, Token));
    }

    [Fact]
    public void SignatureDepthAndChildOrderRemainPartOfTheExistingKey()
    {
        var (_, roots) = SharedWorld();
        WorldNode shallow = roots[0], deep = new("deep", WorldNodeClass.Object3D);
        Link(deep, shallow);
        WorldComparer.PairKeyMemo shared = new(Token);
        Assert.Equal(WorldComparer.PairKey(deep), shared.Key(deep));
        Assert.Equal(WorldComparer.PairKey(shallow), shared.Key(shallow));
        string before = shared.Key(shallow);
        shallow.Children.Reverse();
        // Each operation gets a new memo: edits cannot reuse the previous snapshot's cached structure.
        Assert.NotEqual(before, new WorldComparer.PairKeyMemo(Token).Key(shallow));
        Assert.Equal(WorldComparer.PairKey(shallow), new WorldComparer.PairKeyMemo(Token).Key(shallow));
    }

    [Fact]
    public void RefusalReservesWholeChildPassBeforeCacheGrowthAndAllowsFreshRetry()
    {
        var (_, roots) = SharedWorld();
        LookupWorkBudget work = new(1, Token);
        WorldComparer.PairKeyMemo limited = new(Token, work);
        Assert.Contains("node lookup work", Assert.Throws<InvalidDataException>(() => limited.Key(roots[0])).Message);
        Assert.Equal(1, work.UsedUnits); // Entry lookup only; the three-child/cache reservation did not fit.
        Assert.True(work.Exhausted);
        Assert.Equal(WorldComparer.PairKey(roots[0]), new WorldComparer.PairKeyMemo(Token).Key(roots[0]));
    }

    [Fact]
    public void RepeatedTargetsKeepFullIdentityAndReuseTheirDescription()
    {
        var (world, roots) = SharedWorld();
        string name = roots[0].Name;
        LookupWorkBudget once = new(token: Token), repeated = new(token: Token);
        var one = Assert.Single(WorldLookups.Resolve("m1", world, null, [("first", name)], Token, once));
        var many = WorldLookups.Resolve("m1", world, null,
            Enumerable.Range(0, 100).Select(i => ($"source{i}", name)), Token, repeated);
        Assert.Equal(100, many.Count);
        Assert.Equal(once.UsedUnits + 99, repeated.UsedUnits);
        Assert.All(many, found =>
        {
            Assert.Equal(name, found.Name);
            Assert.Equal(one.Found, found.Found);
            Assert.Equal(one.Fingerprint, found.Fingerprint);
            Assert.Equal(64, found.Fingerprint!.Length);
        });
        // Same name in another slot is a different identity, with highest-slot-first lookup unchanged.
        WorldNode newer = new(name, WorldNodeClass.Object3D); world.Nodes.Add(newer);
        var after = Assert.Single(WorldLookups.Resolve("m1", world, null, [("first", name)], Token));
        Assert.Equal(2, after.Candidates);
        Assert.Equal(GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)[newer], after.Slot);
        Assert.True(one.MayDiffer(after));
    }

    [Fact]
    public void CancellationStopsCachedKeysAndRepeatedTextureLookupsWithoutPublishingPartialResults()
    {
        var (world, roots) = SharedWorld();
        byte[] before = GameZWriter.Write(world, Token);
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        WorldComparer.PairKeyMemo memo = new(cancel.Token);
        _ = memo.Key(roots[0]); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => memo.Key(roots[0]));
        using CancellationTokenSource during = CancellationTokenSource.CreateLinkedTokenSource(Token);
        IEnumerable<(string, string)> Names()
        {
            yield return ("first", roots[0].Name);
            during.Cancel();
            yield return ("second", roots[0].Name); // Cached description must still observe cancellation.
        }
        Assert.ThrowsAny<OperationCanceledException>(() => WorldLookups.Resolve("m1", world, null, Names(), during.Token));
        var retry = WorldLookups.Resolve("m1", world, null, roots.Select(n => ("retry", n.Name)), Token);
        Assert.Equal(roots.Length, retry.Count);
        Assert.Equal(before, GameZWriter.Write(world, Token));
    }

    [Fact]
    public void NewLookupOperationReadsCurrentTransformFlagsAndDescendantBytes()
    {
        var (world, roots) = SharedWorld();
        var root = roots[0];
        SourceLookup Read() => Assert.Single(WorldLookups.Resolve("m1", world, null, [("script", root.Name)], Token));
        var before = Read();
        root.SetPayloadInt(0, root.PayloadInt(0) ^ 2);
        var flags = Read(); Assert.NotEqual(before.Fingerprint, flags.Fingerprint);
        root.Children[0].Name = "changed";
        var childName = Read(); Assert.NotEqual(flags.Fingerprint, childName.Fingerprint);
        // Exact fingerprint still includes the full transform after the pairing key's rounded position.
        root.SetPayloadInt(0, root.PayloadInt(0) | 0x20);
        root.SetPayloadFloat(0x30, 1); root.SetPayloadFloat(0x40, 1); root.SetPayloadFloat(0x50, 1);
        root.SetPayloadFloat(0x54, 0.0001f);
        var transform = Read(); Assert.NotEqual(childName.Fingerprint, transform.Fingerprint);
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            WorldComparer.PairKey(root) + "|" + WorldUpdate.LocalMatrix(root)!.Value.ToString() + "|" + Convert.ToHexString(root.Payload.AsSpan(0, 4)))));
        Assert.Equal(expected, transform.Fingerprint);
    }

    [Fact]
    public void OrdinaryGltfSharedInstancesAndWrittenWorldUseOneLookupAllowance()
    {
        const int rootCount = 12, hubCount = 3, leafCount = 8;
        JsonArray nodes = [], sceneRoots = [];
        int Add(string name, int? instance = null)
        {
            JsonObject node = new() { ["name"] = name };
            if (instance is int id) node["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["instance"] = id } };
            nodes.Add(node); return nodes.Count - 1;
        }
        for (int r = 0; r < rootCount; r++)
        {
            int root = Add(RootName(r)); sceneRoots.Add(root);
            JsonArray children = []; nodes[root]!["children"] = children;
            for (int h = 0; h < hubCount; h++)
            {
                int hub = Add($"h{h}", h + 1); children.Add(hub);
                if (r != 0) continue; // Ordinary import uses the first definition of each engine instance.
                JsonArray leaves = []; nodes[hub]!["children"] = leaves;
                for (int l = 0; l < leafCount; l++) leaves.Add(Add($"l{l}", 100 + l));
            }
        }
        JsonObject source = new()
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" }, ["nodes"] = nodes,
            ["scene"] = 0, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneRoots })
        };
        byte[] json = Encoding.UTF8.GetBytes(source.ToJsonString()); byte[] original = json.ToArray();
        var files = new Files(new()
        {
            ["data/common/models/model.gltf"] = json,
            ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes("SetGameZNodeArraySize 64\nSetModel3DArraySize 16\nSetMaterialArraySize 16\nNewWorld world\nSetModelDirectory ../data/common/models\nLoadGameGen model.gltf holder\nFindNode world\nAddChild holder\nGameZWriteZBDFile gamez.zbd\n"),
            ["gamegen/m1_zbd.gs"] = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, rootCount).Select(i => $"FindNode {RootName(i)}\n")))
        });
        var world = new WorldAssembler(files, Token).Assemble("m1.gs");
        Assert.Equal(rootCount + hubCount + leafCount + 2, world.Nodes.Count);
        Assert.Equal(2 * (rootCount * hubCount + hubCount * leafCount + rootCount + 1), world.Nodes.Sum(n => n.Parents.Count + n.Children.Count));
        byte[] bytes = GameZWriter.Write(world, Token);
        var reopened = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        var names = WorldLookups.FindNodes(path => files.Read(path, Token), "m1", Token);
        LookupWorkBudget work = new(500, Token);
        var results = WorldLookups.Resolve("m1", reopened, null, names, Token, work);
        Assert.Equal(Enumerable.Range(0, rootCount).Select(RootName), results.Select(x => x.Name));
        Assert.All(results, found => { Assert.Equal(1, found.Candidates); Assert.EndsWith(found.Name, found.Found); });
        Assert.Contains("node lookup work", Assert.Throws<InvalidDataException>(() =>
            WorldLookups.Resolve("m1", reopened, null, names, Token, new LookupWorkBudget(80, Token))).Message);
        Assert.Equal(results, WorldLookups.Resolve("m1", reopened, null, names, Token));
        Assert.Equal(original, json);
        Assert.Equal(bytes, GameZWriter.Write(reopened, Token));
    }

    private static string RootName(int i) => new string('r', 33) + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
    private static (GameZWorld World, WorldNode[] Roots) SharedWorld()
    {
        GameZWorld world = new() { NodeCapacity = 64, ModelCapacity = 16, MaterialCapacity = 16 };
        WorldNode[] roots = [.. Enumerable.Range(0, 12).Select(i => new WorldNode(RootName(i), WorldNodeClass.Object3D))];
        WorldNode[] hubs = [.. Enumerable.Range(0, 3).Select(i => new WorldNode($"h{i}", WorldNodeClass.Object3D))];
        WorldNode[] leaves = [.. Enumerable.Range(0, 8).Select(i => new WorldNode($"l{i}", WorldNodeClass.Object3D))];
        foreach (var root in roots) foreach (var hub in hubs) Link(root, hub);
        foreach (var hub in hubs) foreach (var leaf in leaves) Link(hub, leaf);
        world.Nodes.AddRange(roots); world.Nodes.AddRange(hubs); world.Nodes.AddRange(leaves);
        return (world, roots);
    }
    private static void Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); }
    private sealed class Files(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public bool Exists(string relative) => files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        { token.ThrowIfCancellationRequested(); byte[] bytes = files[relative]; limits.Validate(bytes); return bytes; }
    }
}
