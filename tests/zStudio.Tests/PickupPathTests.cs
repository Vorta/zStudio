using System.ComponentModel;
using System.Runtime.InteropServices;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Theory]
    [InlineData("zbd_1998")]
    [InlineData("zbd_1999")]
    public async Task PickupSaveRejectsProtectedFilesystemAliases(string dataset)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows filesystem aliases require Windows.");
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, async (world, _) => {
            string fixture = Path.GetDirectoryName(world.Path)!;
            string protectedDirectory = Path.Combine(fixture, dataset);
            string mission = Path.Combine(protectedDirectory, "m1");
            Directory.CreateDirectory(mission);
            string physicalSource = Path.Combine(mission, "resources.zbd");
            byte[] original = await File.ReadAllBytesAsync(Path.Combine(fixture, "resources.zbd"), TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(physicalSource, original, TestContext.Current.CancellationToken);
            using var alias = WindowsTestPathAlias.Create(protectedDirectory, shortName: false);
            Assert.DoesNotContain(dataset, alias.Path, StringComparison.OrdinalIgnoreCase);
            string source = Path.Combine(alias.Path, "m1", "resources.zbd");
            Assert.True(PickupPlacementEditSession.IsProtectedPath(source));
            var archive = await FormatRegistry.Default.OpenAsync(source, TestContext.Current.CancellationToken);
            source = archive.Path;
            var edits = PickupPlacementEditSession.Create([new(MissionDifficulty.Medium, archive, archive.Assets[0])]);
            edits.MoveTo(Assert.Single(edits.Records).Source, new(44, 55, 66));

            // Neither in-place Save nor Save As into existing/new directories may stage a write.
            var failure = await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: TestContext.Current.CancellationToken));
            Assert.Contains("protected", failure.Message);
            foreach (string relative in new[] { "copy.zbd", Path.Combine("new", "nested", "copy.zbd") })
            {
                string destination = Path.Combine(alias.Path, relative);
                Assert.True(PickupPlacementEditSession.IsProtectedPath(destination));
                failure = await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(new Dictionary<string, string> { [source] = destination }, token: TestContext.Current.CancellationToken));
                Assert.Contains("protected", failure.Message);
            }
            Assert.True(edits.IsDirty);
            Assert.Equal(original, await File.ReadAllBytesAsync(physicalSource, TestContext.Current.CancellationToken));
            Assert.Equal(new[] { physicalSource }, Directory.GetFiles(protectedDirectory, "*", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(protectedDirectory, "new")));

            // A protected source can still be copied safely to an ordinary working directory.
            string copy = Path.Combine(fixture, "working", "copy.zbd");
            var saved = await edits.SaveAsync(new Dictionary<string, string> { [source] = copy }, token: TestContext.Current.CancellationToken);
            Assert.Empty(saved.Errors); Assert.Equal(Path.GetFullPath(copy), Assert.Single(saved.SavedPaths)); Assert.False(edits.IsDirty);
            Assert.Equal(edits.EncodeArchive(source), await File.ReadAllBytesAsync(copy, TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllBytesAsync(physicalSource, TestContext.Current.CancellationToken));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PickupSaveAllowsWorkingDirectoryAliases(bool shortName)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows filesystem aliases require Windows.");
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, async (world, _) => {
            string fixture = Path.GetDirectoryName(world.Path)!;
            using var alias = WindowsTestPathAlias.Create(fixture, shortName);
            string source = Path.Combine(alias.Path, "resources.zbd");
            Assert.False(PickupPlacementEditSession.IsProtectedPath(source));
            var archive = await FormatRegistry.Default.OpenAsync(source, TestContext.Current.CancellationToken);
            source = archive.Path;
            var edits = PickupPlacementEditSession.Create([new(MissionDifficulty.Medium, archive, archive.Assets[0])]);
            var record = Assert.Single(edits.Records); edits.MoveTo(record.Source, new(44, 55, 66));
            var saved = await edits.SaveAsync(token: TestContext.Current.CancellationToken);
            Assert.Empty(saved.Errors); Assert.Single(saved.SavedPaths); Assert.False(edits.IsDirty);
            Assert.Equal(edits.EncodeArchive(source), await File.ReadAllBytesAsync(Path.Combine(fixture, "resources.zbd"), TestContext.Current.CancellationToken));
            string copy = Path.Combine(alias.Path, "new", "nested", "copy.zbd");
            edits.MoveTo(record.Source, new(77, 88, 99));
            saved = await edits.SaveAsync(new Dictionary<string, string> { [source] = copy }, token: TestContext.Current.CancellationToken);
            Assert.Empty(saved.Errors); Assert.Equal(Path.GetFullPath(copy), Assert.Single(saved.SavedPaths)); Assert.False(edits.IsDirty);
            Assert.Equal(edits.EncodeArchive(source), await File.ReadAllBytesAsync(Path.Combine(fixture, "new", "nested", "copy.zbd"), TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task PickupProtectionResolvesLongDirectoryNamesAndMatchesWholeComponents()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows filesystem aliases require Windows.");
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, (world, _) => {
            string directory = Path.GetDirectoryName(world.Path)!;
            for (int i = 0; i < 8; i++) directory = Path.Combine(directory, new string('a', 60));
            string protectedDirectory = Path.Combine(directory, "zbd_1999");
            string workingDirectory = Path.Combine(directory, "zbd_1999_working");
            Directory.CreateDirectory(protectedDirectory); Directory.CreateDirectory(workingDirectory);
            Assert.True(protectedDirectory.Length > 512);
            using var protectedAlias = WindowsTestPathAlias.Create(protectedDirectory, shortName: false);
            using var workingAlias = WindowsTestPathAlias.Create(workingDirectory, shortName: false);
            Assert.True(PickupPlacementEditSession.IsProtectedPath(Path.Combine(protectedAlias.Path, "new", "copy.zbd")));
            Assert.False(PickupPlacementEditSession.IsProtectedPath(Path.Combine(workingAlias.Path, "new", "copy.zbd")));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PickupSaveRefusesUnresolvableDestinationAndRetainsEdits()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows filesystem aliases require Windows.");
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, async (world, _) => {
            string fixture = Path.GetDirectoryName(world.Path)!;
            string physicalSource = Path.Combine(fixture, "resources.zbd");
            byte[] original = await File.ReadAllBytesAsync(physicalSource, TestContext.Current.CancellationToken);
            PickupPlacementEditSession edits;
            string destination;
            using (var alias = WindowsTestPathAlias.Create(fixture, shortName: false))
            {
                var archive = await FormatRegistry.Default.OpenAsync(Path.Combine(alias.Path, "resources.zbd"), TestContext.Current.CancellationToken);
                edits = PickupPlacementEditSession.Create([new(MissionDifficulty.Medium, archive, archive.Assets[0])]);
                destination = Path.Combine(alias.Path, "new", "copy.zbd");
            }
            string source = Assert.Single(edits.ArchivePaths);
            Assert.Contains("Cannot verify the save destination", Assert.Throws<IOException>(() => PickupPlacementEditSession.IsProtectedPath(destination)).Message);
            edits.MoveTo(Assert.Single(edits.Records).Source, new(44, 55, 66));
            await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: TestContext.Current.CancellationToken));
            var failure = await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(new Dictionary<string, string> { [source] = destination }, token: TestContext.Current.CancellationToken));
            Assert.Contains("Cannot verify the save destination", failure.Message);
            Assert.True(edits.IsDirty); Assert.True(edits.CanUndo);
            Assert.Equal(original, await File.ReadAllBytesAsync(physicalSource, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(fixture, "new")));
            string recovered = Path.Combine(fixture, "recovered.zbd");
            var saved = await edits.SaveAsync(new Dictionary<string, string> { [source] = recovered }, token: TestContext.Current.CancellationToken);
            Assert.Empty(saved.Errors); Assert.Equal(recovered, Assert.Single(saved.SavedPaths)); Assert.False(edits.IsDirty);
            Assert.Equal(edits.EncodeArchive(source), await File.ReadAllBytesAsync(recovered, TestContext.Current.CancellationToken));
        });
    }

    // SUBST uses DOS drive definitions. Remove only this fixture's exact mapping, without
    // touching existing drives or changing the machine's 8.3-name policy.
    private sealed partial class WindowsTestPathAlias(string path, string? drive = null, string? target = null) : IDisposable
    {
        public string Path { get; } = path;
        public static WindowsTestPathAlias Create(string directory, bool shortName)
        {
            if (shortName)
            {
                char[] buffer = new char[32768];
                uint length = GetShortPathName(directory, buffer, (uint)buffer.Length);
                if (length == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                Assert.True(length < buffer.Length);
                string alias = new(buffer, 0, (int)length);
                Assert.SkipWhen(alias.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                    System.IO.Path.GetFileName(alias).Equals(System.IO.Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase),
                    "The temporary volume does not provide an 8.3 alias for this fixture.");
                return new(alias);
            }
            char[] names = new char[32768];
            for (char letter = 'Z'; letter >= 'D'; letter--)
            {
                string drive = letter + ":";
                if (QueryDosDevice(drive, names, (uint)names.Length) != 0) continue;
                int error = Marshal.GetLastPInvokeError();
                if (error != 2) throw new Win32Exception(error);
                string target = @"\??\" + directory;
                if (!DefineDosDevice(0x1 | 0x8, drive, target)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                return new(drive + @"\", drive, target);
            }
            throw new IOException("No unused drive letter is available for the temporary SUBST regression fixture.");
        }
        public void Dispose()
        {
            if (drive != null && !DefineDosDevice(0x1 | 0x2 | 0x4 | 0x8, drive, target!))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial uint GetShortPathName(string path, [Out] char[] buffer, uint length);
        [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial uint QueryDosDevice(string device, [Out] char[] buffer, uint length);
        [LibraryImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DefineDosDevice(uint flags, string device, string target);
    }
}
