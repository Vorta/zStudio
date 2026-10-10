using System.Text.Json;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class FileResultPreviewTests
{
    [Fact]
    public void SavedAndRemainingIdentitiesStayCompleteWhileEscapedPreviewsAreBounded()
    {
        string huge = new('&', 100_000);
        string[] saved = [.. Enumerable.Repeat(huge, 4000)];
        string[] errors = [.. Enumerable.Repeat(huge, 40)];
        var result = FileResultPreview.Saved(saved, errors, saved);
        Assert.Equal(4000, result.SavedPathCount); Assert.Equal(4000, result.RemainingPathCount);
        Assert.Equal(40, result.ErrorCount);
        Assert.True(result.SavedPathsTruncated); Assert.True(result.RemainingPathsTruncated); Assert.True(result.ErrorsTruncated);
        Assert.Equal(64, result.SavedPaths.Length); Assert.Equal(64, result.RemainingPaths!.Length); Assert.Equal(32, result.Errors.Length);
        Assert.All(result.SavedPaths.Concat(result.RemainingPaths!).Concat(result.Errors), text => Assert.Equal(512, text.Length));
        Assert.True(JsonSerializer.SerializeToNode(result)!.ToJsonString().Length < 1024 * 1024);
        Assert.Same(huge, saved[^1]); // Preview clipping never changes the save's full identities.
    }

    [Fact]
    public void ShortCompleteResultsKeepTheirExactPathsAndExplicitCounts()
    {
        string[] paths = ["data/one.zrd", "data/two.zad"];
        var result = FileResultPreview.Saved(paths, [], []);
        Assert.Equal(paths, result.SavedPaths); Assert.Equal(2, result.SavedPathCount); Assert.False(result.SavedPathsTruncated);
        Assert.Empty(result.Errors); Assert.Equal(0, result.ErrorCount); Assert.False(result.ErrorsTruncated);
        Assert.Empty(result.RemainingPaths!); Assert.Equal(0, result.RemainingPathCount); Assert.False(result.RemainingPathsTruncated);
    }
}
