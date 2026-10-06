using System.Security.AccessControl;
using System.Security.Principal;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>A sealed output replaces a file as File.Replace did: also while others read it, and keeping its metadata.</summary>
public sealed class SealedFileReplaceTests
{
    private static (string Folder, string Staged, string Target) Files(byte[] staged, byte[] target)
    {
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-sealed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string stagedPath = Path.Combine(folder, "staged.tmp"), targetPath = Path.Combine(folder, "target.zbd");
        File.WriteAllBytes(stagedPath, staged); File.WriteAllBytes(targetPath, target);
        return (folder, stagedPath, targetPath);
    }

    [Fact]
    public void AFileOthersAreReadingIsReplaced()
    {
        byte[] content = [1, 2, 3];
        var (folder, staged, target) = Files(content, [9, 9]);
        try
        {
            // An indexer, antivirus or sync client reading the file shares its writing and deletion.
            using (var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var file = SealedFile.Open(staged, JournalDigest.OfContent(content)))
                file.MoveTo(target, replace: true);
            Assert.Equal(content, File.ReadAllBytes(target));
            Assert.False(File.Exists(staged));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AReplacedFileKeepsItsAttributesCreationTimeAndExplicitRules()
    {
        byte[] content = [4, 5, 6];
        var (folder, staged, target) = Files(content, [7]);
        try
        {
            var created = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetCreationTimeUtc(target, created);
            File.SetAttributes(target, FileAttributes.Hidden | FileAttributes.Archive);
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var security = new FileInfo(target).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(target).SetAccessControl(security);
            using (var file = SealedFile.Open(staged, JournalDigest.OfContent(content)))
                file.MoveTo(target, replace: true);
            Assert.Equal(content, File.ReadAllBytes(target));
            Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.Hidden));
            Assert.Equal(created, File.GetCreationTimeUtc(target));
            var rules = new FileInfo(target).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
            Assert.Contains(rules, r => r.IdentityReference == everyone && r.FileSystemRights.HasFlag(FileSystemRights.Read));
        }
        finally { File.SetAttributes(target, FileAttributes.Normal); Directory.Delete(folder, true); }
    }
}
