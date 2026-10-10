using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourcePreviewCleanupBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void AbandonedTreesShareOneTraversalAllowanceAndRemainRetryable()
    {
        using SourceWorldFixture fixture = new();
        string[] old = Abandoned(fixture.Project, 2);
        using (SourcePreviewCache cache = new(fixture.Project, Token, 4, InventoryBudget.MaximumUnits))
        {
            // root, candidate, deletion root and one descendant spend the complete allowance.
            Assert.Single(old, Directory.Exists);
            Assert.True(File.Exists(Path.Combine(cache.Folder, ".lock")));
        }
        using SourcePreviewCache retry = new(fixture.Project, Token);
        Assert.All(old, path => Assert.False(Directory.Exists(path)));
    }

    [Fact]
    public void RequestCancellationInsideDeletionStopsBeforeItsNextMutation()
    {
        using SourceWorldFixture fixture = new();
        string[] old = Abandoned(fixture.Project, 2);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int visits = 0;
        Assert.Throws<OperationCanceledException>(() => new SourcePreviewCache(fixture.Project, cancellation.Token,
            100, InventoryBudget.MaximumUnits, () => { if (++visits == 4) cancellation.Cancel(); }));
        Assert.Equal(4, visits);
        Assert.All(old, path => Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "data.bin"))));
        // A canceled request does not poison a later constructor or its independent shutdown cleanup.
        string current;
        using (SourcePreviewCache retry = new(fixture.Project, Token))
        {
            current = retry.Folder;
            Assert.All(old, path => Assert.False(Directory.Exists(path)));
        }
        Assert.False(Directory.Exists(current));
    }

    [Fact]
    public void PathAllowanceStopsWholePassBeforeDeletingAnyOldTree()
    {
        using SourceWorldFixture fixture = new();
        string[] old = Abandoned(fixture.Project, 2);
        using SourcePreviewCache cache = new(fixture.Project, Token, 100, 1);
        Assert.All(old, path => Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "data.bin"))));
    }

    [Fact]
    public void FailedOwnedRemovalIsRetriedOnTheNextDrainWithoutReissuingIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using SourceWorldFixture fixture = new();
        using SourcePreviewCache cache = new(fixture.Project, Token);
        string first = cache.NextFolder(), second = cache.NextFolder();
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        string outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "keep.txt"); File.WriteAllText(sentinel, "keep");
        using (new InPlaceDirectoryJunction(first, outside)) cache.DeleteBuild(first);
        Assert.True(Directory.Exists(first));
        cache.DeleteBuild(second);
        Assert.False(Directory.Exists(first)); Assert.False(Directory.Exists(second));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void ShutdownUsesIndependentAllowanceAfterSuccessfulRequestTokenIsCanceled()
    {
        using SourceWorldFixture fixture = new();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        SourcePreviewCache cache = new(fixture.Project, cancellation.Token);
        string folder = cache.Folder, build = cache.NextFolder();
        Directory.CreateDirectory(build); File.WriteAllText(Path.Combine(build, "data.bin"), "owned");
        cancellation.Cancel(); cache.Dispose();
        Assert.False(Directory.Exists(folder));
    }

    private static string[] Abandoned(string project, int count)
    {
        string previews = SourceWorlds.PreviewRoot(project);
        string[] result = new string[count];
        for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(previews, "abandoned" + i); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "data.bin"), "keep");
            Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-2)); result[i] = path;
        }
        return result;
    }
}
