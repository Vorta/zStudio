using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core;

/// <summary>
/// A staged file held from the check of its content until it has its final name, so a publication puts in place exactly the
/// bytes it validated and never what another program wrote to the staged copy after they were checked. On Windows the file
/// is opened for reading and renaming while sharing only reading: as long as it is held no other program can open it for
/// writing, rename it or delete it (only read it, sharing its deletion), and it is renamed through that handle. A program
/// that already has it open for writing, renaming or deleting makes <see cref="Open"/> fail. The file keeps its own
/// file-system metadata where it is put, also when it replaces another file.
/// </summary>
internal sealed partial class SealedFile : IDisposable
{
    private readonly string path;
    private readonly SafeFileHandle? handle;
    private readonly FileStream? stream;

    private SealedFile(string path, SafeFileHandle? handle, FileStream? stream) { this.path = path; this.handle = handle; this.stream = stream; }

    /// <summary>
    /// Holds <paramref name="path"/> and checks that it has <paramref name="expected"/>. Throws <see cref="IOException"/>,
    /// holding nothing, when it differs, is missing or a link, or another program has it open for writing or renaming.
    /// </summary>
    public static SealedFile Open(string path, JournalDigest expected)
    {
        path = Path.GetFullPath(path);
        SealedFile file;
        if (OperatingSystem.IsWindows())
        {
            // OPEN_REPARSE_POINT: a link put at the name is held itself (and differs), never the file it leads to.
            SafeFileHandle opened = CreateFile(Extended(path), GenericRead | Delete, FileShare.Read, 0, OpenExisting, OpenReparsePoint, 0);
            if (opened.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError(); opened.Dispose();
                throw new IOException(error == SharingViolation
                    ? $"Another program has {path} open."
                    : $"{path} cannot be opened: {new Win32Exception(error).Message}", new Win32Exception(error));
            }
            file = new(path, opened, null);
        }
        else file = new(path, null, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        try
        {
            if (file.Digest() != expected) throw new IOException($"{path} changed after it was checked.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    /// <summary>
    /// Gives the held file the name <paramref name="destination"/> (on the same volume), replacing a file there only with
    /// <paramref name="replace"/>. It stays held until it is disposed.
    /// </summary>
    public void MoveTo(string destination, bool replace = false)
    {
        destination = Path.GetFullPath(destination);
        if (handle == null) { File.Move(path, destination, replace); return; }
        string name = NtName(destination);
        int nameOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName)), lengthOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileNameLength));
        byte[] information = new byte[Math.Max(nameOffset + (name.Length + 1) * sizeof(char), Marshal.SizeOf<RenameInformation>())];
        // ReplaceIfExists is a BOOLEAN in a union with a DWORD of flags; the root directory stays null, as the name is absolute.
        information[0] = replace ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(information.AsSpan(lengthOffset), checked((uint)(name.Length * sizeof(char))));
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(information.AsSpan(nameOffset));
        if (!SetFileInformationByHandle(handle, FileRenameInfo, information, (uint)information.Length))
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException(error is AlreadyExists or FileExists ? $"{destination} already exists."
                : $"{path} could not be moved to {destination}: {new Win32Exception(error).Message}", new Win32Exception(error));
        }
    }

    /// <summary>
    /// Puts the held file in place of <paramref name="destination"/> in one step. With <paramref name="backup"/> (a new file)
    /// the replaced file is copied there first; the copy is removed again when the held file cannot take its place.
    /// </summary>
    public void Replace(string destination, string? backup)
    {
        if (backup != null) File.Copy(destination, backup, overwrite: false);
        try { MoveTo(destination, replace: true); }
        catch when (backup != null)
        {
            try { File.Delete(backup); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public void Dispose() { handle?.Dispose(); stream?.Dispose(); }

    /// <summary>The held content's length and SHA-256, read through the handle that holds it.</summary>
    private JournalDigest Digest()
    {
        if (stream != null) { stream.Position = 0; return JournalDigest.Of(stream); }
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long length = 0;
            for (int read; (read = RandomAccess.Read(handle!, buffer, length)) > 0; length += read) hash.AppendData(buffer, 0, read);
            return new(length, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>A full path in the extended form CreateFileW takes beyond MAX_PATH.</summary>
    private static string Extended(string full) => full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) ? full
        : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    /// <summary>A full path as the NT name a rename takes (<c>\??\C:\…</c>, <c>\??\UNC\server\…</c>).</summary>
    private static string NtName(string full)
    {
        string extended = Extended(full);
        return extended.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\??\UNC\" + extended[8..] : @"\??\" + extended[4..];
    }

    private const uint GenericRead = 0x80000000, Delete = 0x00010000, OpenExisting = 3, OpenReparsePoint = 0x00200000;
    private const int FileRenameInfo = 3, SharingViolation = 32, FileExists = 80, AlreadyExists = 183;

    /// <summary>FILE_RENAME_INFO, whose file name follows the header.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation
    {
        public uint ReplaceIfExists;
        public nint RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, FileShare share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint size);
}
