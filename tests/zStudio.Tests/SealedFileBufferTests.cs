using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

[Collection("Allocation-sensitive")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SealedFileBufferTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zstudio-seal-buffer-" + Guid.NewGuid().ToString("N"))).FullName;
    private string At(string name) => Path.Combine(root, name);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void TinyOwnedFilesDoNotAllocateAFullPrivateIoBufferPerReceipt()
    {
        using DirectoryLease directories = new();
        byte[] bytes = [7];
        SealedFile.Ownership warm;
        using (var created = SealedFile.Create(At("warm"), bytes, directories)) warm = created.RetainCreated();
        SealedFile.DeleteOwned(warm, directories);
        string[] paths = [.. Enumerable.Range(0, 32).Select(i => At("small" + i))];
        SealedFile.Ownership[] receipts = new SealedFile.Ownership[paths.Length];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < paths.Length; i++)
        {
            Token.ThrowIfCancellationRequested();
            using var created = SealedFile.Create(paths[i], bytes, directories);
            receipts[i] = created.RetainCreated();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The old 32 private 64 KiB buffers alone reach 2 MiB. Receipts and path/handle bookkeeping stay below 1 MiB.
        Assert.InRange(allocated, 0, 1024 * 1024);
        foreach (var receipt in receipts)
        {
            Assert.Equal(bytes, File.ReadAllBytes(receipt.Path));
            Assert.Equal(JournalDigest.OfContent(bytes), receipt.Content);
            Assert.NotNull(receipt.Identity);
            SealedFile.DeleteOwned(receipt, directories); Assert.False(File.Exists(receipt.Path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockSpanningWritesReadBackAndRetainTheSameOwnedIdentity(bool asynchronous)
    {
        byte[] bytes = Enumerable.Range(0, 131089).Select(i => (byte)(i * 17)).ToArray();
        using DirectoryLease directories = new();
        SealedFile.Ownership receipt;
        using (var created = asynchronous ? await SealedFile.CreateAsync(At("stage"), bytes, directories, Token)
            : SealedFile.Create(At("stage"), bytes, directories))
        {
            Assert.Equal(bytes, await created.ReadAllAsync(bytes.Length, Token));
            receipt = created.RetainCreated();
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(receipt.Path, [9]));
        }
        Assert.Equal(bytes, File.ReadAllBytes(receipt.Path));
        Assert.Equal(JournalDigest.OfContent(bytes), receipt.Content);
        SealedFile.DeleteOwned(receipt, directories); Assert.False(File.Exists(receipt.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackupCopyAndPublishedContentRemainExactWithoutPrivateBuffering(bool asynchronous)
    {
        byte[] original = Enumerable.Range(0, 70003).Select(i => (byte)(i * 13)).ToArray();
        byte[] replacement = Enumerable.Range(0, 65553).Select(i => (byte)(i * 19)).ToArray();
        File.WriteAllBytes(At("target"), original);
        using DirectoryLease directories = new();
        using (var created = asynchronous ? await SealedFile.CreateAsync(At("stage"), replacement, directories, Token)
            : SealedFile.Create(At("stage"), replacement, directories))
            created.Replace(At("target"), At("backup"));
        Assert.Equal(replacement, File.ReadAllBytes(At("target")));
        Assert.Equal(original, File.ReadAllBytes(At("backup")));
        Assert.False(File.Exists(At("stage")));
    }
}
