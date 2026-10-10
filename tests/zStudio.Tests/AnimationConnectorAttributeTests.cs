using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationConnectorAttributeTests
{
    [Theory]
    [InlineData("OBJECT_CONNECTOR", 24, "from_pos ( 9 9 9 ) ", false)]
    [InlineData("CALL_OBJECT_CONNECTOR", 56, "from_pos ( 9 9 9 ) ", false)]
    [InlineData("OBJECT_CONNECTOR", 24, "FROM_POS ( 9 9 9 ) ", true)]
    [InlineData("CALL_OBJECT_CONNECTOR", 56, "FROM_POS ( 9 9 9 ) ", true)]
    public void IgnoredCaseVariantCannotHideARecognizedAttributeButExactDuplicatesKeepTheirFirstValue(
        string kind, int vectorOffset, string prefix, bool exactDuplicate)
    {
        var baseline = Compile(kind, "");
        var result = Compile(kind, prefix);
        var ev = result.Package.Entries[1].Sequences[0].Events.Single();
        Assert.Equal(8u, ev.U32(12)); // FROM_POS is an explicit, absolute source point.
        Assert.Equal(exactDuplicate ? new Vector3(9) : new Vector3(1, 2, 3), ev.Vector(vectorOffset));
        if (exactDuplicate) Assert.Empty(result.Warnings);
        else
        {
            Assert.Contains(result.Warnings, warning => warning.Contains("from_pos was ignored.", StringComparison.Ordinal));
            Assert.Null(AnimationComparer.Difference(baseline.Package.Entries[1], result.Package.Entries[1], TestContext.Current.CancellationToken));
        }
    }

    private static AnimationCompiler.Result Compile(string kind, string prefix)
    {
        string text = $"( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( beam ) " +
            $"SEQUENCE_DEFINITION ( NAME ( go ) {kind} ( NAME ( beam ) {prefix} FROM_POS ( 1 2 3 ) ) ) ) ) ) )";
        return AnimationCompiler.Compile(new Files(Encoding.Latin1.GetBytes(text)), "data/m1/zrdr/anim.zad", ["beam"], TestContext.Current.CancellationToken);
    }

    private sealed class Files(byte[] bytes) : IProjectFiles
    {
        public bool Exists(string path) => true;
        public byte[] Read(string path, CancellationToken token) => Read(path, token, ProjectReadLimits.Document);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); limits.Validate(bytes); return bytes; }
    }
}
