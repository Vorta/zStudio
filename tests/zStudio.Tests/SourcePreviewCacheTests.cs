using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourcePreviewCacheTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClosingDuringABuildCancelsQueuedBuildAndDefersOwnedCleanup()
    {
        using SourceWorldFixture fixture = new();
        using SourcePreviewCache cache = new(fixture.Project, Token);
        string first = cache.NextFolder(), second = cache.NextFolder();
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reports = 0;
        var progress = new OnReport(_ =>
        {
            if (Interlocked.Increment(ref reports) != 1) return;
            reached.SetResult(); release.Task.GetAwaiter().GetResult();
        });
        Task<SourceWorldBuild> running = Task.Run(() => cache.BuildAsync("m1", first, progress: progress, token: Token), Token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15), Token);
            Task<SourceWorldBuild> queued = cache.BuildAsync("m1", second, token: Token);
            cache.DeleteBuild(first);
            cache.Dispose();
            Assert.Throws<ObjectDisposedException>(() => cache.NextFolder());
            Assert.True(Directory.Exists(first));
            Assert.True(File.Exists(Path.Combine(cache.Folder, ".lock")));
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(Directory.Exists(cache.Folder));
            Assert.False(Directory.Exists(second));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CleanupAcceptsOnlyReservedBuildsAndKeepsTheOtherGeneration()
    {
        using SourceWorldFixture fixture = new();
        using SourcePreviewCache cache = new(fixture.Project, Token);
        string first = cache.NextFolder(), second = cache.NextFolder();
        var built = await cache.BuildAsync("m1", first, token: Token);
        Directory.CreateDirectory(second); File.WriteAllText(Path.Combine(second, "keep.txt"), "keep");
        string outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");

        Assert.Throws<ArgumentException>(() => cache.DeleteBuild(outside));
        cache.DeleteBuild(built.Folder);
        Assert.False(Directory.Exists(first));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(second, "keep.txt")));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        cache.Dispose();
        Assert.False(Directory.Exists(cache.Folder));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void InspectingAnOlderLiveCacheDoesNotKeepItsFolderAlive()
    {
        using SourceWorldFixture fixture = new();
        using SourcePreviewCache first = new(fixture.Project, Token);
        Directory.SetCreationTimeUtc(first.Folder, DateTime.UtcNow.AddMinutes(-2));
        using SourcePreviewCache second = new(fixture.Project, Token);

        Assert.True(Directory.Exists(first.Folder));
        first.Dispose();
        Assert.False(Directory.Exists(first.Folder));
        Assert.True(Directory.Exists(second.Folder));
        Assert.True(File.Exists(Path.Combine(second.Folder, ".lock")));
    }

    [Fact]
    public void CacheCleanupNeverTraversesAnInPlaceRedirectedSessionDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using SourceWorldFixture fixture = new();
        using SourcePreviewCache cache = new(fixture.Project, Token);
        string build = cache.NextFolder(); Directory.CreateDirectory(build);
        string outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "keep.txt"); File.WriteAllText(sentinel, "keep");
        using (new InPlaceDirectoryJunction(build, outside))
        {
            cache.DeleteBuild(build);
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        cache.DeleteBuild(build);
        Assert.False(Directory.Exists(build));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress>
    {
        public void Report(SourceProgress value) => action(value);
    }
}
