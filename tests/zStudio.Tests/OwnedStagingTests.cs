using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class OwnedStagingTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "zstudio-owned-stage-" + Guid.NewGuid().ToString("N"));
    public OwnedStagingTests() => Directory.CreateDirectory(folder);
    private string At(string name) => Path.Combine(folder, name);
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public async Task PublishedFileAndReusedTemporaryNameSurviveDisposal()
    {
        using DirectoryLease directories = new();
        var staged = await SealedFile.CreateAsync(At("stage"), new byte[] { 1, 2 }, directories, TestContext.Current.CancellationToken);
        staged.MoveTo(At("target"));
        File.WriteAllBytes(At("stage"), [9]);
        staged.Dispose(); staged.Dispose();
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(At("target")));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(At("stage")));
    }

    [Fact]
    public async Task CollidingCreationNeverOwnsTheExistingFile()
    {
        using DirectoryLease directories = new();
        File.WriteAllBytes(At("stage"), [9]);
        await Assert.ThrowsAnyAsync<IOException>(() => SealedFile.CreateAsync(At("stage"), new byte[] { 1 }, directories, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(At("stage")));
    }

    [Fact]
    public async Task FailedWriteKeepsPrimaryErrorAndReleasesOwnedFileForRetry()
    {
        using DirectoryLease directories = new();
        var expected = new IOException("injected after write");
        var error = await Assert.ThrowsAsync<IOException>(() => SealedFile.CreateAsync(At("stage"), new byte[] { 1, 2 }, directories, TestContext.Current.CancellationToken, () => throw expected));
        Assert.Same(expected, error); Assert.False(File.Exists(At("stage")));
        using var retry = await SealedFile.CreateAsync(At("stage"), new byte[] { 3 }, directories, TestContext.Current.CancellationToken);
        retry.MoveTo(At("target")); retry.Dispose();
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(At("target")));
    }

    [Fact]
    public async Task CancellationAfterWriteCleansOnlyCreatedIdentity()
    {
        using DirectoryLease directories = new(); using CancellationTokenSource cancellation = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SealedFile.CreateAsync(At("stage"), new byte[] { 1 }, directories, cancellation.Token, cancellation.Cancel));
        Assert.False(File.Exists(At("stage")));
    }

    [Fact]
    public async Task OwnedStagePreventsMutationAndRenameUntilPublication()
    {
        using DirectoryLease directories = new();
        using var staged = await SealedFile.CreateAsync(At("stage"), new byte[] { 1, 2 }, directories, TestContext.Current.CancellationToken);
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(At("stage"), [9]));
        Assert.ThrowsAny<IOException>(() => File.Move(At("stage"), At("foreign")));
        Assert.Equal(new byte[] { 1, 2 }, await staged.ReadAllAsync(2, TestContext.Current.CancellationToken));
        staged.Dispose(); Assert.False(File.Exists(At("stage")));
    }

    [Fact]
    public async Task FailedPublicationCleansStageAndPreservesDestination()
    {
        using DirectoryLease directories = new();
        File.WriteAllBytes(At("target"), [9]);
        using var staged = await SealedFile.CreateAsync(At("stage"), new byte[] { 1 }, directories, TestContext.Current.CancellationToken);
        Assert.ThrowsAny<IOException>(() => staged.MoveTo(At("target")));
        staged.Dispose(); Assert.False(File.Exists(At("stage")));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(At("target")));
    }

    [Fact]
    public async Task BackupCannotBeReplacedBeforeFailedPublicationCleanup()
    {
        using DirectoryLease directories = new();
        File.WriteAllBytes(At("target"), [9]);
        using var staged = await SealedFile.CreateAsync(At("stage"), new byte[] { 1 }, directories, TestContext.Current.CancellationToken);
        using var blocker = new FileStream(At("target"), FileMode.Open, FileAccess.Read, FileShare.Read);
        bool observed = false;
        Assert.ThrowsAny<IOException>(() => staged.Replace(At("target"), At("backup"), () =>
        {
            observed = true;
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(At("backup"), [7]));
            Assert.ThrowsAny<IOException>(() => File.Move(At("backup"), At("foreign")));
        }));
        Assert.True(observed); Assert.False(File.Exists(At("backup")));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(At("target")));
    }

    [Fact]
    public async Task SuccessfulBackupAndPreexistingBackupRemainIntact()
    {
        using DirectoryLease directories = new();
        File.WriteAllBytes(At("target"), [9]); File.WriteAllBytes(At("occupied"), [8]);
        using var staged = await SealedFile.CreateAsync(At("stage"), new byte[] { 1 }, directories, TestContext.Current.CancellationToken);
        Assert.ThrowsAny<IOException>(() => staged.Replace(At("target"), At("occupied")));
        Assert.Equal(new byte[] { 8 }, File.ReadAllBytes(At("occupied")));
        staged.Replace(At("target"), At("backup")); staged.Dispose();
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(At("backup")));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(At("target")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredCleanupLeavesModifiedOrReplacedFile(bool replace)
    {
        using DirectoryLease directories = new();
        SealedFile.Ownership receipt;
        using (var created = SealedFile.Create(At("copy"), new byte[] { 1 }, directories)) receipt = created.RetainCreated();
        if (replace)
        {
            File.Move(At("copy"), At("original"));
            File.WriteAllBytes(At("copy"), [1]); // Identical content is still a different file identity.
        }
        else File.WriteAllBytes(At("copy"), [9]);
        if (replace) SealedFile.DeleteOwned(receipt, directories);
        else Assert.ThrowsAny<IOException>(() => SealedFile.DeleteOwned(receipt, directories));
        Assert.Equal(new byte[] { replace ? (byte)1 : (byte)9 }, File.ReadAllBytes(At("copy")));
    }

    [Fact]
    public void FailedHeldRemovalReportsTheRetainedHoldingFile()
    {
        using DirectoryLease directories = new();
        File.WriteAllBytes(At("source"), [2]); File.SetAttributes(At("source"), FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(SourcePublisher.Moved.Stranded, SourcePublisher.MoveIfContent(At("source"), At("holding"), JournalDigest.OfContent(new byte[] { 2 }), directories, removeMoved: true));
            Assert.False(File.Exists(At("source")));
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(At("holding")));
        }
        finally
        {
            foreach (string name in new[] { "source", "holding" }) if (File.Exists(At(name))) File.SetAttributes(At(name), FileAttributes.Normal);
        }
    }

    [Fact]
    public void DeferredCleanupDeletesUnchangedOwnedFileAndMoveCleanupIsImmediate()
    {
        using DirectoryLease directories = new();
        SealedFile.Ownership receipt;
        using (var created = SealedFile.Create(At("copy"), new byte[] { 1 }, directories)) receipt = created.RetainCreated();
        SealedFile.DeleteOwned(receipt, directories); Assert.False(File.Exists(At("copy")));
        File.WriteAllBytes(At("source"), [2]);
        Assert.Equal(SourcePublisher.Moved.Done, SourcePublisher.MoveIfContent(At("source"), At("holding"), JournalDigest.OfContent(new byte[] { 2 }), directories, removeMoved: true));
        Assert.False(File.Exists(At("source"))); Assert.False(File.Exists(At("holding")));
        File.WriteAllBytes(At("holding"), [7]);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(At("holding")));
    }
}
