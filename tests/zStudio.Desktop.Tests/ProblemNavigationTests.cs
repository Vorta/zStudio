using Recoil.Zbd.Core;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class ProblemNavigationTests
{
    private static AssetRecord Asset(int index, long offset, long length, AssetKind kind = AssetKind.Animation) =>
        new() { Id = new("fixture.zbd", kind, index), Name = "duplicate name", Offset = offset, Length = length };

    [Fact]
    public void AssetIdentityTakesPriorityOverAnInteriorOrConflictingOffset()
    {
        var first = Asset(0, 100, 100); var second = Asset(1, 200, 100);
        foreach (long offset in new long[] { 225, 125 })
            Assert.Same(second, new StudioProblem("Warning", "File", "Malformed event", AssetIndex: 1, Offset: offset).ResolveAsset([first, second]));
        Assert.Same(second, new StudioProblem("Warning", "File", "Malformed entry", AssetIndex: 1).ResolveAsset([first, second]));
        Assert.Null(new StudioProblem("Warning", "File", "Unknown identity", AssetIndex: 9, Offset: 125).ResolveAsset([first, second]));
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(150, true)]
    [InlineData(199, true)]
    [InlineData(200, false)]
    [InlineData(-1, false)]
    public void OffsetOnlyUsesHalfOpenSourceRanges(long offset, bool contained)
    {
        var asset = Asset(0, 100, 100);
        var result = new StudioProblem("Warning", "File", "Offset only", Offset: offset).ResolveAsset([asset]);
        Assert.Equal(contained, ReferenceEquals(asset, result));
    }

    [Fact]
    public void AmbiguousRangesRemainUnselectedAndLargeRangesDoNotOverflow()
    {
        var outer = Asset(0, 0, 1000, AssetKind.World); var inner = Asset(0, 100, 20, AssetKind.Model);
        Assert.Null(new StudioProblem("Warning", "File", "Ambiguous", Offset: 110).ResolveAsset([outer, inner]));
        Assert.Null(new StudioProblem("Warning", "File", "Ambiguous index", AssetIndex: 0).ResolveAsset([outer, inner]));
        var separate = Asset(0, 1000, 20, AssetKind.Model);
        Assert.Same(separate, new StudioProblem("Warning", "File", "Shared index", AssetIndex: 0, Offset: 1010).ResolveAsset([outer, separate]));
        var large = Asset(1, long.MaxValue - 10, 20);
        Assert.Same(large, new StudioProblem("Warning", "File", "Large range", Offset: long.MaxValue).ResolveAsset([large]));
        Assert.Null(new StudioProblem("Warning", "File", "No source").ResolveAsset([inner]));
        Assert.Null(new StudioProblem("Warning", "File", "Empty record", Offset: 100).ResolveAsset([Asset(0, 100, 0)]));
    }
}
