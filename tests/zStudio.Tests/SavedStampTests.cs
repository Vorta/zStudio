using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>A file saved and then replaced by another program before its stamp is read must not look like zStudio's own save.</summary>
public sealed class SavedStampTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void AStampIsTakenOnlyWhileTheFileHoldsWhatWasSaved()
    {
        string path = Path.Combine(Path.GetTempPath(), "zstudio-stamp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            Assert.Equal(FileStamp.Read(path), FileStamp.ReadHolding(path, [1, 2, 3, 4]));
            // zStudio's own disk checks share a file with the save that holds it to move it (either may open it first).
            using (SealedFile.Open(path, JournalDigest.OfContent([1, 2, 3, 4]))) Assert.True(SourceRead.Matches(path, 4, SourceProject.Sha256([1, 2, 3, 4]), Token));
            // The same length, even the same write time: another program's bytes are not what was saved.
            var time = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, [1, 2, 3, 5]); File.SetLastWriteTimeUtc(path, time);
            Assert.Equal(FileStamp.Unverified, FileStamp.ReadHolding(path, [1, 2, 3, 4]));
            File.Delete(path);
            Assert.Equal(FileStamp.Unverified, FileStamp.ReadHolding(path, [1, 2, 3, 4]));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

}
