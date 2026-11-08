using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class WorldComparisonRootFieldsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void NativeWorldComparisonReportsAuthoredNativeData()
    {
        using SourceWorldFixture fixture = new();
        string corpus = Path.Combine(fixture.Root, "native"); Mw3TestCorpus.Create(corpus);
        byte[] bytes = File.ReadAllBytes(Path.Combine(corpus, "c1/gamez.zbd"));
        GameZWorld ReadNative() => GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        var before = ReadNative();
        Assert.Empty(WorldComparer.CompareTree(before, ReadNative(), token: Token).Differences);
        var pointerOnly = ReadNative(); pointerOnly.Models[0].Polygons[0].Mw3Field24 ^= 1;
        Assert.Empty(WorldComparer.CompareTree(before, pointerOnly, token: Token).Differences);
        foreach (string change in new[] { "words", "virtualBounds", "colours", "material", "cycle", "facade" })
        {
            var after = ReadNative(); var node = after.Nodes.First(n => n.Model != null); var model = node.Model!; var polygon = model.Polygons[0];
            switch (change)
            {
                case "words": node.Mw3Words[0] ^= 1; break;
                case "virtualBounds": after.Nodes.First(n => n.Class == WorldNodeClass.World).Mw3VirtualBounds[0] ^= 1; break;
                case "colours": polygon.Colors[0] = System.Numerics.Vector3.Zero; break;
                case "material": polygon.Material!.Mw3Flags = (ushort)(polygon.Material.Mw3Flags!.Value ^ 0x20); break;
                case "cycle": polygon.Material!.Mw3Cycle = new() { Loop = true, Speed = 2 }; polygon.Material.Mw3Cycle.Textures.Add(after.Textures[0]); break;
                case "facade": model.Mw3FacadeMode = model.Mw3FacadeMode == 1 ? 0u : 1u; break;
            }
            var result = WorldComparer.CompareTree(before, after, token: Token);
            Assert.Contains(result.Differences, d => d.Field == (change == "words" ? "mw3.nodeWords" : change == "virtualBounds" ? "mw3.virtualBounds" : change == "facade" ? "model.mw3FacadeMode" : "model.polygons"));
        }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredRootFlagChangeRemainsVisibleAfterWriterReaderRoundTrip(bool demo)
    {
        using SourceWorldFixture fixture = new();
        var before = Build("OFF");
        var after = Build("ON");
        Assert.NotEqual(before.Nodes[0].Flags, after.Nodes[0].Flags);
        Changed(before, after, "flags.carried");
        Changed(after, before, "flags.carried");
        Assert.Empty(WorldComparer.CompareTree(before, Build("OFF"), token: Token).Differences);

        GameZWorld Build(string state)
        {
            fixture.Write("gamegen/m1.gs", $"NewWorld world\nGameGenSetWorld world\nFindNode world\nNodeSetCanModify {state}\nGameZWriteZBDFile gamez.zbd\n");
            WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
            var world = assembler.Assemble("m1.gs");
            Assert.Empty(assembler.Warnings);
            return Read(world, demo);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RootZoneAndNonCarriedFlagsAreCompared(bool demo, bool zone)
    {
        GameZWorld world = new() { NodeCapacity = 1, ModelCapacity = 1, MaterialCapacity = 1 };
        WorldNode root = new("world", WorldNodeClass.World); world.Nodes.Add(root);
        var before = Read(world, demo);
        if (zone) root.Zone = 7; else root.Flags ^= 4;
        var after = Read(world, demo);
        Changed(before, after, zone ? "zone" : "flags.derived");
    }

    [Fact]
    public void RootRuntimePointersDoNotChangeEqualMembership()
    {
        GameZWorld before = new(), after = new();
        WorldNode a = new("world", WorldNodeClass.World), b = new("world", WorldNodeClass.World);
        WorldNode ac = new("child", WorldNodeClass.Object3D), bc = new("child", WorldNodeClass.Object3D);
        before.Nodes.AddRange([a, ac]); after.Nodes.AddRange([b, bc]);
        a.Children.Add(ac); b.Children.Add(bc); ac.Parents.Add(a); bc.Parents.Add(b);
        a.Areas.Add(new()); a.Areas[0].Nodes.Add(ac);
        b.Areas.Add(new()); b.Areas[0].Nodes.Add(bc);
        b.SetPayloadInt(4, 1234);
        Assert.Empty(WorldComparer.CompareTree(before, after, token: Token).Differences);
    }

    private static GameZWorld Read(GameZWorld world, bool demo)
    {
        byte[] bytes = GameZWriter.Write(world, Token);
        if (demo) bytes = DemoWorldFixture.FromVersion15(bytes);
        return GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
    }

    private static void Changed(GameZWorld before, GameZWorld after, string field)
    {
        // The zero flat-list limit does not hide details from the root row consumed by GUI and MCP.
        var comparison = WorldComparer.CompareTree(before, after, limit: 0, token: Token);
        var root = comparison.Roots[0];
        Assert.Equal(WorldComparisonStatus.Changed, root.Status);
        Assert.True(comparison.DifferenceCount > 0);
        Assert.Contains(root.Differences, d => d.Field == field);
        Assert.Equal(1, comparison.Counts[WorldComparisonStatus.Changed]);
    }
}
