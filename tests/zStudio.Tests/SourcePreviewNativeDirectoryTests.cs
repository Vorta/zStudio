using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourcePreviewNativeDirectoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APreviewNeverWritesThroughAnInPlaceRedirectedDestination(bool existing)
    {
        if (!OperatingSystem.IsWindows()) return;
        using SourceWorldFixture fixture = new();
        string destination = Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "native");
        string outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "sentinel.txt"); File.WriteAllText(sentinel, "keep");
        if (existing) Directory.CreateDirectory(destination);
        InPlaceDirectoryJunction? mutation = null;
        OnReport progress = new(_ =>
        {
            if (mutation != null) return;
            Assert.ThrowsAny<IOException>(() => Directory.Move(destination, destination + "-moved"));
            mutation = new(destination, outside);
        });
        try
        {
            var failure = await Record.ExceptionAsync(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", destination,
                progress: progress, token: TestContext.Current.CancellationToken));
            Assert.NotNull(mutation); Assert.NotNull(failure);
            Assert.True(failure is IOException or InvalidDataException, failure.ToString());
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.Equal([sentinel], Directory.GetFileSystemEntries(outside));
        }
        finally { mutation?.Dispose(); }
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress>
    {
        public void Report(SourceProgress value) => action(value);
    }
}
