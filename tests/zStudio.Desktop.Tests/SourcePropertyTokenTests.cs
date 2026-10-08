using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class SourcePropertyTokenTests
{
    [Theory]
    [InlineData(8, 64, " ,\t")]
    [InlineData(256, 32, ", ")]
    [InlineData(3, 4096, " ,")]
    public void ExcessListAndSingleWideTokenRefuseBeforeCreatingTokenStrings(int maximum, int tokenLength, string separators)
    {
        string many = string.Concat(Enumerable.Repeat("0 ", 500_000));
        string wide = new('x', 1_000_000);
        _ = ComponentText.BoundedTokens("1", separators, 1, maximum, tokenLength, "Invalid list.");
        foreach (string value in new[] { many, wide })
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<FormatException>(() => ComponentText.BoundedTokens(value, separators, 1, maximum, tokenLength, "Invalid list."));
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        }
    }

    [Fact]
    public void ArgumentBoundariesPreserveFullIdentityAndOriginalSeparators()
    {
        string[] values = Enumerable.Range(0, 8).Select(i => new string('0', 63) + i).ToArray();
        Assert.Equal(values, ComponentText.BoundedTokens(string.Join(" ,\t", values), " ,\t", 1, 8, 64, "Invalid args."));
        Assert.Throws<FormatException>(() => ComponentText.BoundedTokens(" ,\t ", " ,\t", 1, 8, 64, "Invalid args."));
        Assert.Equal(["1\r2"], ComponentText.BoundedTokens("1\r2", " ,\t", 1, 8, 64, "Invalid args."));
        Assert.Equal(["a\tb"], ComponentText.BoundedTokens("a\tb", ", ", 0, 256, 32, "Invalid surfaces."));
        Assert.Empty(ComponentText.BoundedTokens(" , ", ", ", 0, 256, 32, "Invalid surfaces."));
        Assert.Empty(ComponentText.BoundedTokens(", ,", " ,", 0, 3, 4096, "Invalid zones.")); // Legacy separator-only zones mean an empty (any) list.
    }

    [Fact]
    public void SurfaceAndZoneBoundaryTokensRemainComplete()
    {
        string[] ids = Enumerable.Range(0, 256).Select(i => $"s{i:D3}" + new string('x', 28)).ToArray();
        Assert.Equal(ids, ComponentText.BoundedTokens(string.Join(", ", ids), ", ", 0, 256, 32, "Invalid surfaces."));
        string zone = new('0', 4095);
        Assert.Equal([zone + "1", zone + "2", zone + "3"], ComponentText.BoundedTokens(zone + "1, " + zone + "2 " + zone + "3", " ,", 1, 3, 4096, "Invalid zones."));
    }
}
