using System.Text;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldComparisonOccurrenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AuthoredOverflowOccurrencesSurviveComparison(bool secondary, bool demo)
    {
        var single = Build(1, 0, secondary, demo);
        var twice = Build(2, 0, secondary, demo);
        Assert.Single(Owner(single).Children);
        Assert.Equal(2, Owner(twice).Children.Count);
        Assert.Same(Owner(twice).Children[0], Owner(twice).Children[1]);

        Changed(single, twice, "world.overflowOccurrences");
        Changed(twice, single, "world.overflowOccurrences");
        Same(single, Build(1, 0, secondary, demo));
        Same(twice, Build(2, 0, secondary, demo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualCountsCompareRepeatedTargetIdentityRatherThanItsName(bool ordinaryParent)
    {
        // Both targets have the same final name, but the second has a different authored transform.
        // The same three edges repeat different identities; comparing just total/name counts loses the change.
        var firstRepeated = Build(2, 1, ordinaryParent: ordinaryParent, sameNames: true);
        var secondRepeated = Build(1, 2, ordinaryParent: ordinaryParent, sameNames: true);
        Assert.Equal(3, Owner(firstRepeated).Children.Count);
        Assert.Equal(3, Owner(secondRepeated).Children.Count);
        Assert.All(Owner(firstRepeated).Children, n => Assert.Equal("twin", n.Name));
        string field = ordinaryParent ? "children" : "world.overflowOccurrences";
        Changed(firstRepeated, secondRepeated, field);
        Changed(secondRepeated, firstRepeated, field);
        Same(firstRepeated, Build(2, 1, ordinaryParent: ordinaryParent, sameNames: true));
    }

    [Fact]
    public void AreaOnlyChangeIsReportedOnceWithoutAnOverflowDifference()
    {
        var expected = Build(1, 1); var actual = Build(1, 1);
        foreach (var world in new[] { expected, actual }) Owner(world).Areas.Add(new());
        Owner(expected).Areas[0].Nodes.Add(Owner(expected).Children[0]);
        Owner(actual).Areas[0].Nodes.Add(Owner(actual).Children[1]);
        var comparison = WorldComparer.CompareTree(expected, actual, token: Token);
        Assert.Equal(1, comparison.DifferenceCount);
        Assert.Equal("world.area0", Assert.Single(comparison.Roots[0].Differences).Field);
    }

    [Fact]
    public void CancellationDoesNotPoisonTheNextComparison()
    {
        var single = Build(1, 0); var twice = Build(2, 0);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorldComparer.CompareTree(single, twice, token: canceled.Token));
        Changed(single, twice, "world.overflowOccurrences");
    }

    private static WorldNode Owner(GameZWorld world) => world.Nodes.Single(n => n.Name == "owner");

    private static GameZWorld Build(int first, int second, bool secondary = false, bool demo = false,
        bool ordinaryParent = false, bool sameNames = false)
    {
        string script = (secondary || ordinaryParent ? "NewWorld primary\n" : "")
            + "NewObject3D first\nNewObject3D second\nObject3DTranslate 10 0 0\n"
            + (ordinaryParent ? "NewObject3D owner\n" : "NewWorld owner\n");
        string firstEdges = string.Concat(Enumerable.Repeat("AddChild first\n", first));
        string secondEdges = string.Concat(Enumerable.Repeat("AddChild second\n", second));
        script += firstEdges + secondEdges;
        if (sameNames) script += "FindNode first\nNodeSetDescription twin\nFindNode second\nNodeSetDescription twin\n";
        script += "GameZWriteZBDFile out\n";
        WorldAssembler assembler = new(new Files(script), Token);
        var assembled = assembler.Assemble("m1.gs");
        Assert.Empty(assembler.Warnings);
        byte[] bytes = GameZWriter.Write(assembled, Token);
        if (demo) bytes = DemoWorldFixture.FromVersion15(bytes);
        return GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
    }

    private static void Changed(GameZWorld expected, GameZWorld actual, string field)
    {
        var comparison = WorldComparer.CompareTree(expected, actual, limit: 0, token: Token);
        Assert.Empty(comparison.Differences);
        var owner = comparison.Roots.Single(n => n.Expected?.Name == "owner");
        Assert.Equal(WorldComparisonStatus.Changed, owner.Status);
        Assert.True(comparison.DifferenceCount > 0);
        Assert.Contains(owner.Differences, d => d.Field == field);
        Assert.Contains(WorldComparer.Compare(expected, actual), d => d.Field == field);
        Assert.Contains(WorldComparer.Compare(expected, actual, limit: 0), d => d.Field == "truncated");
    }

    private static void Same(GameZWorld expected, GameZWorld actual)
    {
        var comparison = WorldComparer.CompareTree(expected, actual, token: Token);
        Assert.Equal(0, comparison.DifferenceCount);
        Assert.Empty(comparison.Differences);
        Assert.All(comparison.Roots, n => Assert.Equal(WorldComparisonStatus.Same, n.Status));
    }

    private sealed class Files(string script) : IProjectFiles
    {
        private readonly byte[] ownedInput = Encoding.ASCII.GetBytes(script);
        public bool Exists(string path) => path == "gamegen/m1.gs";
        public byte[] Read(string path, CancellationToken token) => Read(path, token, ProjectReadLimits.Document);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = ownedInput; limits.Validate(result); return result; }
    }
}
