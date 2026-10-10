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
    private readonly DirectoryLease directories;
    private readonly bool ownsDirectories;
    private bool deleteOnDispose, disposed;
    private int moves;

    internal sealed record Ownership(string Path, DirectoryLease.FileIdentity? Identity, JournalDigest Content);

    /// <summary>Releases a created file for use, retaining a compact identity/content receipt for conditional cleanup.</summary>
    internal Ownership RetainCreated()
    {
        if (!deleteOnDispose || disposed) throw new InvalidOperationException("Only an owned created file can be retained.");
        var receipt = new Ownership(path, handle == null ? null : DirectoryLease.Identity(handle, path), Digest());
        deleteOnDispose = false; return receipt;
    }

    internal static void DeleteOwned(Ownership receipt, DirectoryLease directories)
    {
        if (receipt.Identity == null) return;
        using var file = Open(receipt.Path, receipt.Content, directories);
        if (DirectoryLease.Identity(file.handle!, receipt.Path) != receipt.Identity.Value) return;
        file.DeleteHeld();
    }

    /// <summary>Deletes only the held identity. Callers must already have established its ownership/content.</summary>
    internal void DeleteHeld()
    {
        if (handle == null) throw new IOException("The cleanup file was retained because this platform cannot delete through its held identity.");
        DirectoryLease.Delete(handle, path); deleteOnDispose = false;
    }

    private SealedFile(string path, SafeFileHandle? handle, FileStream? stream, DirectoryLease directories, bool ownsDirectories) { this.path = path; this.handle = handle; this.stream = stream; this.directories = directories; this.ownsDirectories = ownsDirectories; }

    /// <summary>Creates, writes and verifies a temporary through one owned handle. An unpublished file is cleaned by that handle.</summary>
    internal static async Task<SealedFile> CreateAsync(string path, ReadOnlyMemory<byte> bytes, DirectoryLease directories, CancellationToken token, Action? afterWrite = null)
    {
        token.ThrowIfCancellationRequested();
        var file = CreateEmpty(path, directories, asynchronous: true);
        try
        {
            await file.stream!.WriteAsync(bytes, token).ConfigureAwait(false);
            afterWrite?.Invoke();
            await file.stream.FlushAsync(token).ConfigureAwait(false); file.stream.Flush(true);
            if (file.stream.Length != bytes.Length) throw new IOException("The written file did not pass verification.");
            file.stream.Position = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    token.ThrowIfCancellationRequested();
                    int read = await file.stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, bytes.Length - offset)), token).ConfigureAwait(false);
                    if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(bytes.Span.Slice(offset, read)))
                        throw new IOException("The written file did not pass verification.");
                    offset += read;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            token.ThrowIfCancellationRequested(); file.RequireRegular();
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal static SealedFile Create(string path, ReadOnlyMemory<byte> bytes, DirectoryLease directories)
    {
        var file = CreateEmpty(path, directories, asynchronous: false);
        try
        {
            file.stream!.Write(bytes.Span); file.stream.Flush(true);
            if (!file.Has(JournalDigest.OfContent(bytes.Span))) throw new IOException("The written file did not pass verification.");
            file.RequireRegular(); return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static SealedFile CreateEmpty(string path, DirectoryLease directories, bool asynchronous)
    {
        path = Path.GetFullPath(path); directories.Parent(path);
        FileOptions options = FileOptions.WriteThrough | (asynchronous ? FileOptions.Asynchronous : FileOptions.None);
        SafeFileHandle? created = null;
        try
        {
            if (OperatingSystem.IsWindows())
                created = directories.FileHandle(path, GenericRead | 0x40000000 /* GENERIC_WRITE */ | Delete | WriteAttributes, FileShare.Read, FileMode.CreateNew, options);
            // Callers already supply complete memory or copy in blocks, then flush and verify immediately.
            // A private 64 KiB FileStream buffer would be allocated even for each one-byte checkout member.
            // Disable that redundant buffer; the same handle, durable flush and readback checks still own the file.
            FileStream output = created == null ? new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1, options)
                : new(created, FileAccess.ReadWrite, 1, asynchronous);
            return new(path, created, output, directories, false) { deleteOnDispose = true };
        }
        catch
        {
            if (created != null)
            {
                try { DirectoryLease.Delete(created, path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                created.Dispose();
            }
            throw;
        }
    }

    /// <summary>Reads the already owned, verified file; never resolves its temporary pathname again.</summary>
    internal async Task<byte[]> ReadAllAsync(int expectedLength, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stream == null || stream.Length != expectedLength) throw new IOException("The staged file length changed.");
        byte[] bytes = new byte[expectedLength]; stream.Position = 0;
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); return bytes;
    }

    /// <summary>
    /// Holds <paramref name="path"/> and checks that it has <paramref name="expected"/>. Throws <see cref="IOException"/>,
    /// holding nothing, when it differs, is missing or a link, or another program has it open for writing or renaming.
    /// </summary>
    public static SealedFile Open(string path, JournalDigest expected, DirectoryLease? directories = null)
    {
        path = Path.GetFullPath(path);
        bool ownsDirectories = directories == null; directories ??= new();
        try { directories.Parent(path); }
        catch { if (ownsDirectories) directories.Dispose(); throw; }
        SealedFile file;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // OPEN_REPARSE_POINT: a link put at the name is held itself (and differs), never the file it leads to.
                // FILE_WRITE_ATTRIBUTES is not shared access: it only lets the replaced file's attributes be given to this one.
                SafeFileHandle opened = directories.FileHandle(path, GenericRead | Delete | WriteAttributes, FileShare.Read);
                if (opened.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError(); opened.Dispose();
                    throw new IOException(error == SharingViolation
                        ? $"Another program has {path} open."
                        : $"{path} cannot be opened: {new Win32Exception(error).Message}", new Win32Exception(error));
                }
                file = new(path, opened, null, directories, ownsDirectories);
            }
            else file = new(path, null, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), directories, ownsDirectories);
        }
        catch { if (ownsDirectories) directories.Dispose(); throw; }
        try
        {
            if (!file.Has(expected)) throw new IOException($"{path} changed after it was checked.");
            file.RequireRegular();
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
        directories.Parent(destination);
        if (handle == null) { File.Move(path, destination, replace); deleteOnDispose = false; moves++; return; }
        // What the replaced file keeps under File.Replace: its attributes, creation time and explicit access rules.
        var kept = replace && OperatingSystem.IsWindows() && directories.Exists(destination) ? Kept.Of(destination, directories) : null;
        RequireRegular();
        int error = RenameRelative(handle, directories.ParentHandle(destination), Path.GetFileName(destination), replace);
        if (error != 0)
            throw new IOException(error is AlreadyExists or FileExists ? $"{destination} already exists."
                : $"{path} could not be moved to {destination}: {new Win32Exception(error).Message}", new Win32Exception(error));
        deleteOnDispose = false; moves++;
        if (kept != null && OperatingSystem.IsWindows()) kept.Apply(handle);
    }

    internal static int RenameRelative(SafeFileHandle handle, SafeFileHandle directory, string name, bool replace)
    {
        int nameOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName)), lengthOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileNameLength));
        byte[] information = new byte[Math.Max(nameOffset + (name.Length + 1) * sizeof(char), Marshal.SizeOf<RenameInformation>())];
        nint parent = directory.DangerousGetHandle();
        int rootOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.RootDirectory));
        if (IntPtr.Size == 8) BinaryPrimitives.WriteInt64LittleEndian(information.AsSpan(rootOffset), parent);
        else BinaryPrimitives.WriteInt32LittleEndian(information.AsSpan(rootOffset), (int)parent);
        BinaryPrimitives.WriteUInt32LittleEndian(information.AsSpan(lengthOffset), checked((uint)(name.Length * sizeof(char))));
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(information.AsSpan(nameOffset));
        // FILE_RENAME_INFO_EX with POSIX semantics replaces a file others have open with delete sharing (readers such as
        // indexers, antivirus or sync clients), which the plain rename refuses; file systems without it (FAT, older SMB)
        // take the plain rename. Its flags share the first DWORD with the plain form's ReplaceIfExists BOOLEAN; the root
        // directory is the held parent and the name is one relative component, so ancestor path changes cannot redirect it.
        BinaryPrimitives.WriteUInt32LittleEndian(information, replace ? RenameReplaceIfExists | RenamePosixSemantics : 0);
        int error = Rename(handle, information, extended: true);
        bool renamed = error == 0;
        if (!renamed && error is InvalidParameter or InvalidFunction or NotSupported)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(information, replace ? 1u : 0u);
            error = Rename(handle, information, extended: false);
            renamed = error == 0;
        }
        return error;
    }

    /// <summary>The replaced file's attributes, creation time and explicit access rules, given to the file that takes its place.</summary>
    private sealed record Kept(FileAttributes Attributes, DateTime CreationUtc, System.Security.AccessControl.FileSecurity? Security)
    {
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static Kept Of(string file, DirectoryLease directories)
        {
            using FileStream info = directories.OpenFile(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            System.Security.AccessControl.FileSecurity? security = null;
            try
            {
                var read = info.GetAccessControl();
                // Only rules set on the file itself; inherited ones the new file has from its folder already.
                if (read.AreAccessRulesProtected || read.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Count > 0)
                {
                    // A descriptor read is written back only where it was changed: copied in, its access rules count as changed.
                    security = new();
                    security.SetSecurityDescriptorBinaryForm(read.GetSecurityDescriptorBinaryForm(), System.Security.AccessControl.AccessControlSections.Access);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { }
            return new(File.GetAttributes(info.SafeFileHandle) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed), File.GetCreationTimeUtc(info.SafeFileHandle), security);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void Apply(SafeFileHandle handle)
        {
            // FILE_BASIC_INFO: zero times stay as they are; the creation time and attributes are the replaced file's.
            byte[] basic = new byte[40];
            BinaryPrimitives.WriteInt64LittleEndian(basic, CreationUtc.ToFileTimeUtc());
            BinaryPrimitives.WriteUInt32LittleEndian(basic.AsSpan(32), Attributes == 0 ? (uint)FileAttributes.Normal : (uint)Attributes);
            SetFileInformationByHandle(handle, FileBasicInfo, basic, (uint)basic.Length);
            if (Security == null) return;
            try
            {
                // Reopen the held identity for ACL changes; looking up its path could target another file after a device
                // alias change. Metadata access does not require changing the held content's sharing protocol.
                using SafeFileHandle writable = ReOpenFile(handle, 0x40000 /* WRITE_DAC */ | 0x20000 /* READ_CONTROL */ | 0x80 /* READ_ATTRIBUTES */,
                    FileShare.ReadWrite | FileShare.Delete, 0);
                if (writable.IsInvalid) return;
                using FileStream file = new(writable, FileAccess.Read, 1);
                file.SetAccessControl(Security);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { }
        }
    }

    /// <summary>
    /// Puts the held file in place of <paramref name="destination"/> in one step. With <paramref name="backup"/> (a new file)
    /// the replaced file is copied there first; the copy is removed again when the held file cannot take its place.
    /// </summary>
    public void Replace(string destination, string? backup, Action? beforePublish = null)
    {
        RequireRegular();
        directories.Parent(destination);
        if (backup != null) directories.Parent(backup);
        using SealedFile? copy = backup == null ? null : CreateEmpty(backup, directories, asynchronous: false);
        if (copy != null)
        {
            using FileStream source = directories.OpenFile(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            source.CopyTo(copy.stream!); copy.stream!.Flush(true);
        }
        int before = moves;
        try { beforePublish?.Invoke(); MoveTo(destination, replace: true); }
        finally { if (copy != null && moves != before) copy.deleteOnDispose = false; }
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        // An open Unix descriptor does not own its pathname. Leave an unpublished remnant there rather than unlinking
        // an unrelated replacement. Windows deletes the created identity, even after its old name is reused.
        if (deleteOnDispose && handle != null)
            try { DirectoryLease.Delete(handle, path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { stream?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { handle?.Dispose(); if (ownsDirectories) directories.Dispose(); }
    }

    private void RequireRegular()
    {
        FileAttributes attributes = handle != null ? File.GetAttributes(handle) : File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException($"{path} became a link or another non-regular entry after it was checked; the verified file was not published.");
    }

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

    private const uint GenericRead = 0x80000000, Delete = 0x00010000, WriteAttributes = 0x100;
    private const uint RenameReplaceIfExists = 0x1, RenamePosixSemantics = 0x2;
    private const int FileBasicInfo = 0;
    private const int InvalidFunction = 1, SharingViolation = 32, NotSupported = 50, FileExists = 80, InvalidParameter = 87, AlreadyExists = 183;

    private static int Rename(SafeFileHandle handle, byte[] information, bool extended)
    {
        int status = NtSetInformationFile(handle, out _, information, (uint)information.Length,
            extended ? 65 /* FileRenameInformationEx */ : 10 /* FileRenameInformation */);
        return status >= 0 ? 0 : unchecked((int)RtlNtStatusToDosError(status));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public nint Status; public nuint Information; }
    [LibraryImport("ntdll.dll")]
    private static partial int NtSetInformationFile(SafeFileHandle handle, out IoStatus status, byte[] information, uint size, int informationClass);
    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);

    /// <summary>FILE_RENAME_INFO, whose file name follows the header.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation
    {
        public uint ReplaceIfExists;
        public nint RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle ReOpenFile(SafeFileHandle file, uint access, FileShare share, uint flags);
}
