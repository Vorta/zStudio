using System.ComponentModel;
using System.Runtime.InteropServices;
using zStudio.Core;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    [Theory]
    [InlineData("zbd_1998")]
    [InlineData("zbd_1999")]
    public async Task ProtectedPathsResolveFilesystemAliases(string dataset)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows filesystem aliases require Windows.");
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, (world, _) => {
            string fixture = Path.GetDirectoryName(world.Path)!;
            string protectedDirectory = Path.Combine(fixture, dataset);
            Directory.CreateDirectory(Path.Combine(protectedDirectory, "m1"));
            using var alias = WindowsTestPathAlias.Create(protectedDirectory, shortName: false);
            Assert.DoesNotContain(dataset, alias.Path, StringComparison.OrdinalIgnoreCase);
            foreach (string relative in new[] { Path.Combine("m1", "resources.zbd"), "copy.zbd", Path.Combine("new", "nested", "copy.zbd") })
                Assert.True(ProtectedPaths.IsProtected(Path.Combine(alias.Path, relative)));
            Assert.False(ProtectedPaths.IsProtected(Path.Combine(fixture, "working", "copy.zbd")));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkingDirectoryAliasesAreNotProtected(bool shortName)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows filesystem aliases require Windows.");
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, (world, _) => {
            using var alias = WindowsTestPathAlias.Create(Path.GetDirectoryName(world.Path)!, shortName);
            Assert.False(ProtectedPaths.IsProtected(Path.Combine(alias.Path, "resources.zbd")));
            Assert.False(ProtectedPaths.IsProtected(Path.Combine(alias.Path, "new", "nested", "copy.zbd")));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ProtectionResolvesLongDirectoryNamesAndMatchesWholeComponents()
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
            Assert.True(ProtectedPaths.IsProtected(Path.Combine(protectedAlias.Path, "new", "copy.zbd")));
            Assert.False(ProtectedPaths.IsProtected(Path.Combine(workingAlias.Path, "new", "copy.zbd")));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AnUnresolvableDestinationCannotBeVerified()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows filesystem aliases require Windows.");
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, (world, _) => {
            string fixture = Path.GetDirectoryName(world.Path)!, destination;
            using (var alias = WindowsTestPathAlias.Create(fixture, shortName: false)) destination = Path.Combine(alias.Path, "new", "copy.zbd");
            Assert.Contains("Cannot verify the save destination", Assert.Throws<IOException>(() => ProtectedPaths.IsProtected(destination)).Message);
            Assert.False(Directory.Exists(Path.Combine(fixture, "new")));
            return Task.CompletedTask;
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
