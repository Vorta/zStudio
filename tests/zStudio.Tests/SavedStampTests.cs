using zStudio.Core;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>zStudio's own disk checks share a file with the save that holds it to move it (either may open it first).</summary>
public sealed class SavedStampTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SourceChecksShareAFileWithTheSealThatHoldsIt()
    {
        string path = Path.Combine(Path.GetTempPath(), "zstudio-stamp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            using (SealedFile.Open(path, JournalDigest.OfContent([1, 2, 3, 4]))) Assert.True(SourceRead.Matches(path, 4, SourceProject.Sha256([1, 2, 3, 4]), Token));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
