using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldComparisonRootFieldsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
