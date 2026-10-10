using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

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

    [Fact]
    public async Task ASavedFileIsTheSessionsOwnUntilAnotherProgramWritesIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-saved-stamp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string texture = Path.Combine(root, "texture1.zbd"), png = Path.Combine(root, "image.png");
            await File.WriteAllBytesAsync(texture, ContentFixture.Texture(2, 1, true), Token);
            await File.WriteAllBytesAsync(png, PngEncoder.Encode(new(2, 1, [0, 0, 255, 255, 0, 0, 255, 255]), Token), Token);
            using AssetResolver resolver = new(root); var edits = new TextureEditSession(await resolver.OpenCachedAsync(texture, Token));
            edits.Accept(await edits.PrepareAsync(png, 0, "", [new(texture, 0)], resolver, Token));
            // The stamp is taken while the save's seal still holds the file, so no other program can write it in between:
            // the saved file is zStudio's own, and a later write by another program is a change.
            var result = await edits.SaveAsync(token: Token);
            Assert.Equal(new[] { texture }, result.SavedPaths);
            Assert.False(edits.HasExternalChanges());
            await File.WriteAllTextAsync(texture, "external", Token);
            Assert.True(edits.HasExternalChanges());
        }
        finally { Directory.Delete(root, true); }
    }
}
