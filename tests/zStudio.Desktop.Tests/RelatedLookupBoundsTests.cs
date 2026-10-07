using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class RelatedLookupBoundsTests
{
    [Fact]
    public void TenThousandUnmatchedReferencesReadTheIndexOnlyOnce()
    {
        string[] names = [.. Enumerable.Range(0, 10_000).Select(i => $"missing{i}.png")];
        SearchHit[] entries = [.. Enumerable.Range(0, 100_000).Select(i => new SearchHit("other.zbd", AssetKind.Texture, i, $"present{i}.png"))];
        int enumerations = 0, visits = 0;
        IEnumerable<SearchHit> Counted()
        {
            enumerations++;
            foreach (var hit in entries) { visits++; yield return hit; }
        }
        var result = MainViewModel.MatchRelated(names, Counted(), "source.zbd");
        Assert.Empty(result.Items); Assert.False(result.Truncated);
        Assert.Equal(1, enumerations); Assert.Equal(entries.Length, visits);
    }

    [Fact]
    public void ReferencesRetainPriorityCaseInsensitiveBasenamesAndPerNameLimits()
    {
        SearchHit[] entries = [.. Enumerable.Range(0, 120).SelectMany(i => new[] { "a", "b", "c", "d" }.Select(n => new SearchHit("other.zbd", AssetKind.Texture, i, n + ".png")))];
        var result = MainViewModel.MatchRelated(["folder/D.bmp", "b.tga", "D.jpg", "A.png", "c.png"], entries.Prepend(new("SOURCE.ZBD", AssetKind.Texture, 0, "d.png")), "source.zbd");
        var expected = new[] { "d", "b", "a" }.SelectMany(n => entries.Where(h => h.Name == n + ".png").Take(100)).ToArray();
        Assert.Equal(expected, result.Items); Assert.True(result.Truncated);
    }

    [Fact]
    public void InputLimitsStopEnumerationAndDiscloseIncompleteResults()
    {
        int names = 0, entries = 0;
        IEnumerable<string> Names() { while (true) { names++; yield return "wanted.png"; } }
        IEnumerable<SearchHit> Entries() { while (true) { entries++; yield return new("other.zbd", AssetKind.Texture, entries, "wanted.png"); } }
        var result = MainViewModel.MatchRelated(Names(), Entries(), "source.zbd", maximumNames: 7, maximumEntries: 11);
        Assert.Equal(8, names); Assert.Equal(12, entries); Assert.Equal(11, result.Items.Count); Assert.True(result.Truncated);
        var large = MainViewModel.MatchRelated([new string('x', 1_000_000)], Entries(), "source.zbd", maximumNameCharacters: 128);
        Assert.Empty(large.Items); Assert.True(large.Truncated); Assert.Equal(12, entries);
    }
}
