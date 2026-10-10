using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SealedFileMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEmptyStagedFileChangedIntoALinkIsRefusedBeforePublicationOrBackup(bool backup)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows reparse metadata requires Windows.");
        string root = Path.Combine(Path.GetTempPath(), "zstudio-empty-seal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string staged = Path.Combine(root, "staged.tmp"), target = Path.Combine(root, "target.txt"), saved = Path.Combine(root, "backup.txt"), other = Path.Combine(root, "other.txt");
        File.WriteAllBytes(staged, []); File.WriteAllText(other, "untouched");
        if (backup) File.WriteAllText(target, "original");
        try
        {
            using SealedFile file = SealedFile.Open(staged, JournalDigest.OfContent([]));
            using FileLink mutation = FileLink.Create(staged, other);
            IOException error = Assert.Throws<IOException>(() => { if (backup) file.Replace(target, saved); else file.MoveTo(target); });
            Assert.Contains("became a link", error.Message);
            Assert.Equal("untouched", File.ReadAllText(other)); Assert.False(File.Exists(saved));
            if (backup) Assert.Equal("original", File.ReadAllText(target)); else Assert.False(File.Exists(target));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FileLink(SafeFileHandle handle) : IDisposable
    {
        internal static FileLink Create(string file, string target)
        {
            SafeFileHandle handle = CreateFile(file, 0x100 /* FILE_WRITE_ATTRIBUTES */, FileShare.ReadWrite | FileShare.Delete, 0, 3, 0x00200000, 0);
            if (handle.IsInvalid) { int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error); }
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target), printed = Encoding.Unicode.GetBytes(target);
            byte[] data = new byte[20 + substitute.Length + 2 + printed.Length + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA000000C);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)substitute.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)printed.Length));
            substitute.CopyTo(data, 20); printed.CopyTo(data, 22 + substitute.Length);
            if (DeviceIoControl(handle, 0x900A4, data, data.Length, 0, 0, out _, 0)) return new(handle);
            int failure = Marshal.GetLastPInvokeError(); handle.Dispose();
            Assert.SkipWhen(failure == 1314, "The current token cannot create file symlink metadata.");
            throw new Win32Exception(failure);
        }
        public void Dispose()
        {
            using (handle)
            {
                byte[] clear = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(clear, 0xA000000C);
                if (!DeviceIoControl(handle, 0x900AC, clear, clear.Length, 0, 0, out _, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security, uint mode, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int length, nint output, int outputLength, out int returned, nint overlap);
    }
}
