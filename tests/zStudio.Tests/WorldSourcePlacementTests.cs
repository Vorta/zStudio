using System.Text;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldSourcePlacementTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GameZWorld Shipped(int count, string? child = null, bool distinctChildren = false)
    {
        GameZWorld world = new();
        world.Nodes.Add(new("world", WorldNodeClass.World));
        for (int i = 0; i < count; i++)
        {
            WorldNode root = new($"root{i}", WorldNodeClass.Object3D);
            world.Nodes.Add(root);
            if (child is null) continue;
            WorldNode content = new(distinctChildren ? child + i : child, WorldNodeClass.Object3D);
            root.Children.Add(content); content.Parents.Add(root); world.Nodes.Add(content);
        }
        // Reconstruction receives a reader-validated shipped world, including its real slot/capacity metadata.
        return GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(world, Token), token: Token), Token);
    }

    [Fact]
    public void DistinctVersionsDoNotRetryEveryEarlierGeneratedSuffix()
    {
        const int count = 3_200;
        var world = Shipped(count, "child", distinctChildren: true);
        var script = GameGenScriptText.Tokenize("NewWorld world\nSetModelDirectory ..\\data\\m1\\models\n"
            + string.Concat(Enumerable.Range(0, count).Select(i => $"LoadGameGen model.flt root{i}\n"))
            + "GameZWriteZBDFile gamez.zbd\n");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var outputs = WorldSources.Reconstruct([new(1, world)], _ => script, (_, _) => null, new HashSet<string>(), _ => 0, [], Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The old restart-at-two loop alone grew quadratically: the complete operation exceeded 575 MiB.
        Assert.True(allocated < 192L << 20, $"Distinct-version placement allocated {allocated:N0} bytes.");
        Assert.Equal(count * 2 + 1, outputs.Count);
        Assert.Equal(count * 2 + 1, outputs.Select(o => o.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(outputs, o => o.Path == "data/m1/models/model.gltf");
        Assert.Contains(outputs, o => o.Path == "data/m1/models/model_3200.gltf");
    }

    [Fact]
    public void GeneratedSuffixesStillAvoidAuthoredNames()
    {
        var world = Shipped(4, "child", distinctChildren: true);
        var script = GameGenScriptText.Tokenize("NewWorld world\nSetModelDirectory ../data/m1/models\n"
            + "LoadGameGen model.flt root0\nLoadGameGen model.flt root1\nLoadGameGen model_2.flt root2\nLoadGameGen model.flt root3\nGameZWriteZBDFile gamez.zbd\n");
        var outputs = WorldSources.Reconstruct([new(1, world)], _ => script, (_, _) => null, new HashSet<string>(), _ => 0, [], Token);
        Assert.Equal(["data/m1/models/model.gltf", "data/m1/models/model_2.gltf", "data/m1/models/model_2_2.gltf", "data/m1/models/model_3.gltf"],
            LogicalPaths(outputs));
    }

    [Fact]
    public void FirstSearchedVersionControlsPlacementEvenWhenALaterDirectoryMatches()
    {
        var world = Shipped(3, "child", distinctChildren: true);
        world.Nodes.Single(n => n.Name == "child2").Name = "child1";
        var main = GameGenScriptText.Tokenize("NewWorld world\nSetModelDirectory ../data/first/models\nLoadGameGen model.flt root0\n"
            + "SetModelDirectory ../data/second/models\nLoadGameGen model.flt root1\nSetModelDirectory ../data/first/models\nsource helper.gw\nGameZWriteZBDFile gamez.zbd\n");
        var helper = GameGenScriptText.Tokenize("LoadGameGen model.flt root2\n");
        var outputs = WorldSources.Reconstruct([new(1, world)], n => n == "m1.gs" ? main : helper,
            (_, _) => null, new HashSet<string>(), _ => 0, [], Token);
        Assert.Equal(["data/first/models/model.gltf", "data/m1/models/model.gltf", "data/second/models/model.gltf"],
            LogicalPaths(outputs));
    }

    [Fact]
    public void RepeatedLoadsDoNotAllocateAFullMatchingLoadListForEachOccurrence()
    {
        const int count = 3_200;
        var world = Shipped(count);
        var script = GameGenScriptText.Tokenize("NewWorld world\nSetModelDirectory ..\\data\\m1\\models\n"
            + string.Concat(Enumerable.Range(0, count).Select(i => $"LoadGameGen model.flt root{i}\n"))
            + "GameZWriteZBDFile gamez.zbd\n");
        long before = GC.GetAllocatedBytesForCurrentThread();
        List<string> notes = [];
        var outputs = WorldSources.Reconstruct([new(1, world)], _ => script, (_, _) => null, new HashSet<string>(), _ => 0, notes, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The previous per-load scan allocated over 600 MiB for this ~4 MiB accepted world. Leave ample room for
        // trace/decomposition/glTF work; this tests total allocation, not machine-dependent execution time.
        Assert.True(allocated < 128L << 20, $"Repeated-load reconstruction allocated {allocated:N0} bytes.");
        Assert.Empty(notes);
        Assert.Equal(["data/m1/meta/zones.json", "data/m1/models/model.bin", "data/m1/models/model.gltf"], outputs.Select(o => o.Path).Order(StringComparer.Ordinal));
        Assert.Contains("\"asset\"", Encoding.UTF8.GetString(outputs.Single(o => o.Path.EndsWith(".gltf", StringComparison.Ordinal)).Bytes));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void SharingRequiresTheSameScriptAndContentAcrossMissions(bool differentContent, bool differentScript, bool shared)
    {
        Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> scripts = new(StringComparer.OrdinalIgnoreCase)
        {
            ["m1.gs"] = GameGenScriptText.Tokenize("NewWorld world\nSetModelDirectory ..\\data\\common\\models\nsource helper.gw\nGameZWriteZBDFile gamez.zbd\n"),
            ["m2.gs"] = GameGenScriptText.Tokenize($"NewWorld world\nSetModelDirectory ..\\data\\common\\models\nsource {(differentScript ? "other.gw" : "HELPER.GW")}\nGameZWriteZBDFile gamez.zbd\n"),
            ["helper.gw"] = GameGenScriptText.Tokenize("LoadGameGen Model.FLT root0\n"),
            ["other.gw"] = GameGenScriptText.Tokenize("LoadGameGen model.flt root0\n"),
        };
        List<string> notes = [];
        var outputs = WorldSources.Reconstruct([new(1, Shipped(1, "shape")), new(2, Shipped(1, differentContent ? "otherShape" : "shape"))],
            n => scripts.GetValueOrDefault(n), (_, _) => null, new HashSet<string>(), _ => 0, notes, Token);
        string[] paths = LogicalPaths(outputs);
        Assert.Equal(shared ? ["data/common/models/model.gltf"] : new[] { "data/m1/models/model.gltf", "data/m2/models/model.gltf" }, paths);
        // Upper-case invocation still identifies the same script; another script or another content hash cannot
        // create a shared file merely because the authored model names match.
        if (shared) Assert.Empty(notes);
        else Assert.Equal(2, notes.Count);
    }

    private static string[] LogicalPaths(IReadOnlyList<WorldSources.Output> outputs) => outputs
        .Where(o => o.Path.EndsWith("/meta/zones.json", StringComparison.Ordinal))
        .SelectMany(o => SourceMapZones.Parse(o.Bytes, Token).Assets).Select(a => a.LogicalPath)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
}
