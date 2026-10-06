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
/// that already has it open for writing, renaming or deleting makes <see cref="Open"/> fail. Replacing a file, it takes the
/// replaced file's attributes, creation time and explicit access rules, as File.Replace keeps them, and it replaces a file
/// other programs merely have open (an indexer, an antivirus or sync client reading it) where the file system allows.
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
            // FILE_WRITE_ATTRIBUTES is not shared access: it only lets the replaced file's attributes be given to this one.
            SafeFileHandle opened = CreateFile(Extended(path), GenericRead | Delete | WriteAttributes, FileShare.Read, 0, OpenExisting, OpenReparsePoint, 0);
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
            if (!file.Has(expected)) throw new IOException($"{path} changed after it was checked.");
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
        // What the replaced file keeps under File.Replace: its attributes, creation time and explicit access rules.
        var kept = replace && OperatingSystem.IsWindows() && File.Exists(destination) ? Kept.Of(destination) : null;
        string name = NtName(destination);
        int nameOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName)), lengthOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileNameLength));
        byte[] information = new byte[Math.Max(nameOffset + (name.Length + 1) * sizeof(char), Marshal.SizeOf<RenameInformation>())];
        BinaryPrimitives.WriteUInt32LittleEndian(information.AsSpan(lengthOffset), checked((uint)(name.Length * sizeof(char))));
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(information.AsSpan(nameOffset));
        // FILE_RENAME_INFO_EX with POSIX semantics replaces a file others have open with delete sharing (readers such as
        // indexers, antivirus or sync clients), which the plain rename refuses; file systems without it (FAT, older SMB)
        // take the plain rename. Its flags share the first DWORD with the plain form's ReplaceIfExists BOOLEAN; the root
        // directory stays null, as the name is absolute.
        BinaryPrimitives.WriteUInt32LittleEndian(information, replace ? RenameReplaceIfExists | RenamePosixSemantics : 0);
        bool renamed = SetFileInformationByHandle(handle, FileRenameInfoEx, information, (uint)information.Length);
        int error = renamed ? 0 : Marshal.GetLastPInvokeError();
        if (!renamed && error is InvalidParameter or InvalidFunction or NotSupported)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(information, replace ? 1u : 0u);
            renamed = SetFileInformationByHandle(handle, FileRenameInfo, information, (uint)information.Length);
            error = renamed ? 0 : Marshal.GetLastPInvokeError();
        }
        if (!renamed)
            throw new IOException(error is AlreadyExists or FileExists ? $"{destination} already exists."
                : $"{path} could not be moved to {destination}: {new Win32Exception(error).Message}", new Win32Exception(error));
        if (kept != null && OperatingSystem.IsWindows()) kept.Apply(handle, destination);
    }

    /// <summary>The replaced file's attributes, creation time and explicit access rules, given to the file that takes its place.</summary>
    private sealed record Kept(FileAttributes Attributes, DateTime CreationUtc, System.Security.AccessControl.FileSecurity? Security)
    {
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static Kept Of(string file)
        {
            FileInfo info = new(file);
            System.Security.AccessControl.FileSecurity? security = null;
            try
            {
                var read = info.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
                // Only rules set on the file itself; inherited ones the new file has from its folder already.
                if (read.AreAccessRulesProtected || read.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Count > 0)
                {
                    // A descriptor read is written back only where it was changed: copied in, its access rules count as changed.
                    security = new();
                    security.SetSecurityDescriptorBinaryForm(read.GetSecurityDescriptorBinaryForm(), System.Security.AccessControl.AccessControlSections.Access);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { }
            return new(info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed), info.CreationTimeUtc, security);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void Apply(SafeFileHandle handle, string file)
        {
            // FILE_BASIC_INFO: zero times stay as they are; the creation time and attributes are the replaced file's.
            byte[] basic = new byte[40];
            BinaryPrimitives.WriteInt64LittleEndian(basic, CreationUtc.ToFileTimeUtc());
            BinaryPrimitives.WriteUInt32LittleEndian(basic.AsSpan(32), Attributes == 0 ? (uint)FileAttributes.Normal : (uint)Attributes);
            SetFileInformationByHandle(handle, FileBasicInfo, basic, (uint)basic.Length);
            if (Security == null) return;
            try { new FileInfo(file).SetAccessControl(Security); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { }
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

    /// <summary>
    /// Whether the held content is <paramref name="expected"/>; a file of another length differs without being read (a
    /// staged copy another program filled with gigabytes).
    /// </summary>
    private bool Has(JournalDigest expected) => (handle != null ? RandomAccess.GetLength(handle) : stream!.Length) == expected.Length && Digest() == expected;
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

    private const uint GenericRead = 0x80000000, Delete = 0x00010000, WriteAttributes = 0x100, OpenExisting = 3, OpenReparsePoint = 0x00200000;
    private const uint RenameReplaceIfExists = 0x1, RenamePosixSemantics = 0x2;
    private const int FileBasicInfo = 0, FileRenameInfo = 3, FileRenameInfoEx = 22;
    private const int InvalidFunction = 1, SharingViolation = 32, NotSupported = 50, FileExists = 80, InvalidParameter = 87, AlreadyExists = 183;

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
