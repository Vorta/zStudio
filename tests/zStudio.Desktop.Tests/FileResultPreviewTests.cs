using System.Text.Json;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class FileResultPreviewTests
{
    [Fact]
    public void PathPreviewsAreBoundedWhileTheSavedIdentitiesStayComplete()
    {
        string huge = new('&', 100_000);
        string[] saved = [.. Enumerable.Repeat(huge, 4000)];
        var result = FileResultPreview.Paths(saved, saved.Length);
        Assert.Equal(4000, result.Count); Assert.True(result.Truncated);
        Assert.Equal(64, result.Values.Length);
        Assert.All(result.Values, text => Assert.Equal(512, text.Length));
        Assert.True(JsonSerializer.SerializeToNode(result)!.ToJsonString().Length < 1024 * 1024);
        Assert.Same(huge, saved[^1]); // Preview clipping never changes the save's full identities.
        string[] paths = ["data/one.zrd", "data/two.zad"];
        var complete = FileResultPreview.Paths(paths, paths.Length);
        Assert.Equal(paths, complete.Values); Assert.Equal(2, complete.Count); Assert.False(complete.Truncated);
    }
}
