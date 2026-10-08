using System.ComponentModel;
using System.Buffers.Binary;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Recoil.Zbd.Core;

/// <summary>
/// Keeps ordinary directory ancestors in place while path-based file operations use them. On Windows each component is
/// opened relative to its held parent without delete sharing and checked through that handle. File opens through this
/// lease use the held parent rather than resolving the ancestor path again. Sharing alone does not prevent attribute-only
/// reparse changes; callers must use the relative operations when the directory may change while it is held.
/// Non-Windows callers retain checked-path semantics; their platforms do not provide Windows sharing guarantees.
/// </summary>
internal sealed partial class DirectoryLease : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle?> held = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public void Parent(string file, bool create = false) => Hold(Path.GetDirectoryName(Path.GetFullPath(file))!, create);

    internal void CreateDirectory(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        Parent(directory, create: true);
        if (!OperatingSystem.IsWindows())
        {
            if (Path.Exists(directory)) throw new IOException($"{directory} already exists.");
            Directory.CreateDirectory(directory); Hold(directory); return;
        }
        SafeFileHandle handle = RelativeOpen(ParentHandle(directory), Path.GetFileName(directory), 0x81, FileShare.ReadWrite, 2, 0x21);
        try { Check(Attributes(handle, directory), directory); held.Add(directory, handle); }
        catch { handle.Dispose(); throw; }
    }

    public void Hold(string directory, bool create = false)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        Stack<string> ancestors = new();
        for (string? current = directory; current != null && !held.ContainsKey(current); current = Path.GetDirectoryName(current))
        {
            if (ancestors.Count == 1024) throw new IOException("The save path has more than 1,024 directory ancestors; choose a shallower folder.");
            ancestors.Push(current);
        }
        while (ancestors.TryPop(out string? ancestor)) Open(ancestor, create);
        if (OperatingSystem.IsWindows()) Check(Attributes(held[directory]!, directory), directory);
        else
            for (string? current = directory; current != null; current = Path.GetDirectoryName(current))
                Check(File.GetAttributes(current), current);
    }

    private void Open(string directory, bool create)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (create && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            FileAttributes attributes = File.GetAttributes(directory);
            Check(attributes, directory); held.Add(directory, null); return;
        }
        string? parent = Path.GetDirectoryName(directory);
        SafeFileHandle handle;
        try { handle = OpenDirectory(0x81 /* FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES */); }
        catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 5 })
        {
            // A writable drop folder may deny listing. FILE_ADD_FILE participates in sharing just as
            // FILE_LIST_DIRECTORY does, so it still prevents renaming while relative writes use it.
            handle = OpenDirectory(0x82 /* FILE_ADD_FILE | FILE_READ_ATTRIBUTES */);
        }
        try
        {
            if (handle.IsInvalid) throw Error(directory);
            if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out AttributeTag info, 8)) throw Error(directory);
            Check((FileAttributes)info.Attributes, directory);
            held.Add(directory, handle);
        }
        catch { handle.Dispose(); throw; }

        SafeFileHandle OpenDirectory(uint access)
        {
            if (parent != null)
                return RelativeOpen(held[parent]!, Path.GetFileName(directory), access, FileShare.ReadWrite,
                    create ? 3u /* FILE_OPEN_IF */ : 1u /* FILE_OPEN */, 0x21 /* DIRECTORY_FILE | SYNCHRONOUS_IO_NONALERT */);
            SafeFileHandle root = CreateFile(Extended(directory), access, FileShare.ReadWrite,
                0, 3 /* OPEN_EXISTING */, 0x02000000 /* BACKUP_SEMANTICS */ | 0x00200000 /* OPEN_REPARSE_POINT */, 0);
            if (!root.IsInvalid) return root;
            IOException error = Error(directory); root.Dispose(); throw error;
        }
    }

    internal SafeFileHandle ParentHandle(string file)
    {
        Parent(file);
        return held[Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(file))!)]!;
    }

    /// <summary>Opens a regular file by its name in the held parent, without traversing a mutable ancestor again.</summary>
    internal SafeFileHandle FileHandle(string path, uint access, FileShare share, FileMode mode = FileMode.Open, FileOptions options = FileOptions.None)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        SafeFileHandle parent = ParentHandle(path);
        uint disposition = mode switch
        {
            FileMode.CreateNew => 2, FileMode.Open => 1, FileMode.OpenOrCreate => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        uint flags = 0x40 /* FILE_NON_DIRECTORY_FILE */;
        if ((options & FileOptions.Asynchronous) == 0) flags |= 0x20 /* FILE_SYNCHRONOUS_IO_NONALERT */;
        if ((options & FileOptions.WriteThrough) != 0) flags |= 0x2;
        if ((options & FileOptions.SequentialScan) != 0) flags |= 0x4;
        if ((options & FileOptions.RandomAccess) != 0) flags |= 0x800;
        if ((options & FileOptions.DeleteOnClose) != 0) { flags |= 0x1000; access |= 0x10000 /* DELETE */; }
        SafeFileHandle file = RelativeOpen(parent, Path.GetFileName(path), access | 0x80 /* FILE_READ_ATTRIBUTES */, share, disposition, flags);
        try
        {
            if (!GetFileInformationByHandleEx(file, 9, out AttributeTag info, 8)) throw Error(path);
            if (((FileAttributes)info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException($"{path} is not a regular file; it was not followed.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize = 4096, FileOptions options = FileOptions.None)
    {
        Parent(path);
        if (!OperatingSystem.IsWindows()) return new(path, mode, access, share, bufferSize, options);
        uint rights = access switch { FileAccess.Read => 0x80000000, FileAccess.Write => 0x40000000, _ => 0xC0000000 };
        SafeFileHandle handle = FileHandle(path, rights, share, mode switch { FileMode.Create => FileMode.OpenOrCreate, FileMode.Truncate => FileMode.Open, _ => mode }, options);
        try
        {
            FileStream stream = new(handle, access, bufferSize, (options & FileOptions.Asynchronous) != 0);
            if (mode is FileMode.Create or FileMode.Truncate) stream.SetLength(0);
            return stream;
        }
        catch { handle.Dispose(); throw; }
    }

    internal void DeleteFile(string path)
    {
        if (!OperatingSystem.IsWindows()) { File.Delete(path); return; }
        try
        {
            using SafeFileHandle file = FileHandle(path, 0x10000 /* DELETE */, FileShare.ReadWrite | FileShare.Delete);
            Delete(file, path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
    }

    /// <summary>Deletes an empty directory this lease holds, only if reopening it for deletion retains its identity.</summary>
    internal void DeleteDirectory(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows() && held.TryGetValue(directory, out SafeFileHandle? existing)) Check(Attributes(existing!, directory), directory);
        if (!held.Remove(directory, out SafeFileHandle? old)) throw new IOException($"{directory} was not held by this operation.");
        if (!OperatingSystem.IsWindows()) { Directory.Delete(directory); return; }
        FileIdentity identity;
        using (old) identity = Identity(old!, directory);
        using SafeFileHandle current = RelativeOpen(ParentHandle(directory), Path.GetFileName(directory), 0x10081,
            FileShare.ReadWrite | FileShare.Delete, 1, 0x21);
        if (Identity(current, directory) != identity) throw new IOException($"{directory} changed before cleanup; it was left as it is.");
        Delete(current, directory);
    }

    internal void MoveDirectory(string source, string destination)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        Hold(source); Parent(destination);
        if (!OperatingSystem.IsWindows()) { Directory.Move(source, destination); held.Remove(source); return; }
        FileIdentity identity = Identity(held[source]!, source);
        string prefix = source + Path.DirectorySeparatorChar;
        foreach (string path in held.Keys.Where(p => p.Equals(source, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            if (held.Remove(path, out SafeFileHandle? handle)) handle?.Dispose();
        using SafeFileHandle current = RelativeOpen(ParentHandle(source), Path.GetFileName(source), 0x10081, FileShare.ReadWrite | FileShare.Delete, 1, 0x21);
        if (Identity(current, source) != identity) throw new IOException($"{source} changed before moving; it was left as it is.");
        int error = SealedFile.RenameRelative(current, ParentHandle(destination), Path.GetFileName(destination), replace: false);
        if (error != 0) throw new IOException($"{source} could not be moved: {new Win32Exception(error).Message}", new Win32Exception(error));
    }

    internal sealed record Entry(string Name, FileAttributes Attributes, long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);

    internal bool Exists(string path) => Inspect(path) != null;

    /// <summary>Inspects the named entry itself, including a link, without following it.</summary>
    internal Entry? Inspect(string path)
    {
        try
        {
            Parent(path);
            if (!OperatingSystem.IsWindows())
            {
                FileAttributes attributes = File.GetAttributes(path);
                return new(Path.GetFileName(path), attributes, attributes.HasFlag(FileAttributes.Directory) ? 0 : new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), File.GetCreationTimeUtc(path));
            }
            using SafeFileHandle file = RelativeOpen(ParentHandle(path), Path.GetFileName(path), 0x80, FileShare.ReadWrite | FileShare.Delete, 1, 0x20);
            FileAttributes flags = Attributes(file, path);
            return new(Path.GetFileName(path), flags, (flags & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 ? RandomAccess.GetLength(file) : 0, File.GetLastWriteTimeUtc(file), File.GetCreationTimeUtc(file));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    /// <summary>Deletes an internal working tree through held identities, refusing links and excessive traversal.</summary>
    internal void DeleteTree(string directory, int maximumEntries = 100_000, CancellationToken token = default)
    {
        int visited = 0;
        DeleteTree(directory, _ =>
        {
            if (++visited > maximumEntries) throw new IOException($"Cleanup exceeds {maximumEntries:N0} entries; the remaining working files were kept.");
        }, token);
    }

    /// <summary>Shares a caller's traversal/path allowance across every tree in one cleanup operation.</summary>
    internal void DeleteTree(string directory, Action<long> visit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Exists(directory)) return;
        Stack<(string Path, bool Remove)> pending = new(); pending.Push((Path.GetFullPath(directory), false));
        while (pending.TryPop(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (next.Remove) { DeleteDirectory(next.Path); continue; }
            Hold(next.Path); pending.Push((next.Path, true));
            foreach (Entry entry in Entries(next.Path, token))
            {
                visit((long)next.Path.Length + 1 + entry.Name.Length);
                string path = Path.Combine(next.Path, entry.Name);
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{path} is a link; cleanup did not follow it.");
                if (entry.Attributes.HasFlag(FileAttributes.Directory)) pending.Push((path, false)); else DeleteFile(path);
            }
        }
    }

    /// <summary>The physical parent captured by this lease, for source/destination policy checks after capture.</summary>
    internal string CapturedPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return path;
        string directory = Path.GetDirectoryName(path) ?? Path.GetPathRoot(path)!;
        Stack<string> missing = new();
        if (Path.GetFileName(path).Length != 0) missing.Push(Path.GetFileName(path));
        while (true)
        {
            try { Hold(directory); break; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                string? ancestor = Path.GetDirectoryName(directory);
                if (ancestor == null) throw;
                missing.Push(Path.GetFileName(directory)); directory = ancestor;
            }
        }
        char[] buffer = new char[512]; SafeFileHandle parent = held[Path.TrimEndingDirectorySeparator(directory)]!;
        while (true)
        {
            uint length = GetFinalPathNameByHandle(parent, buffer, (uint)buffer.Length, 0 /* normalized DOS volume path */);
            if (length == 0) throw Error(path);
            if (length < buffer.Length)
            {
                string captured = new(buffer, 0, (int)length);
                if (captured.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) captured = @"\\" + captured[8..];
                else if (captured.StartsWith(@"\\?\", StringComparison.Ordinal)) captured = captured[4..];
                foreach (string component in missing) captured = Path.Combine(captured, component);
                return captured;
            }
            if (length > 32768) throw new IOException("The captured directory path is too long.");
            buffer = new char[checked((int)length + 1)];
        }
    }

    /// <summary>Lists the held directory itself, using one 64 KiB buffer and checking cancellation for every entry.</summary>
    internal IEnumerable<Entry> Entries(string directory, CancellationToken token = default)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); Hold(directory);
        if (!OperatingSystem.IsWindows())
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            { token.ThrowIfCancellationRequested(); yield return new(entry.Name, entry.Attributes, entry is FileInfo file ? file.Length : 0, entry.LastWriteTimeUtc, entry.CreationTimeUtc); }
            yield break;
        }
        byte[] buffer = new byte[64 * 1024]; bool restart = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int status = NtQueryDirectoryFile(held[directory]!, 0, 0, 0, out IoStatus result, buffer, (uint)buffer.Length, 1 /* FileDirectoryInformation */, false, 0, restart);
            restart = false;
            if (status == unchecked((int)0x80000006) /* STATUS_NO_MORE_FILES */) yield break;
            if (status < 0) throw new IOException($"{directory} could not be listed: {new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))).Message}");
            int length = checked((int)result.Information), offset = 0;
            if (length == 0 || length > buffer.Length) throw new IOException($"{directory} returned an invalid directory listing.");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (offset > length - 64) throw new IOException($"{directory} returned a truncated directory entry.");
                int next = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset)), nameLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 60));
                if (nameLength < 0 || (nameLength & 1) != 0 || nameLength > length - offset - 64) throw new IOException($"{directory} returned an invalid directory name.");
                string name = Encoding.Unicode.GetString(buffer, offset + 64, nameLength);
                if (name is not ("." or "..")) yield return new(name,
                    (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 56)),
                    BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset + 40)),
                    DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset + 24))),
                    DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset + 8))));
                if (next == 0) break;
                if (next < 64 || next > length - offset) throw new IOException($"{directory} returned an invalid directory entry offset.");
                offset += next;
            }
        }
    }

    private static FileAttributes Attributes(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag info, 8)) throw Error(path);
        return (FileAttributes)info.Attributes;
    }
    internal static FileIdentity Identity(SafeFileHandle handle, string path)
    {
        if (!GetFileIdentity(handle, 18 /* FileIdInfo */, out FileIdentity id, 24)) throw Error(path);
        return id;
    }
    internal static void Delete(SafeFileHandle handle, string path)
    {
        if (!SetFileInformationByHandle(handle, 4 /* FileDispositionInfo */, [1], 1)) throw Error(path);
    }

    private static unsafe SafeFileHandle RelativeOpen(SafeFileHandle parent, string name, uint access, FileShare share, uint disposition, uint options)
    {
        fixed (char* text = name)
        {
            UnicodeName unicode = new() { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = (nint)text };
            ObjectAttributes attributes = new() { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = (nint)(&unicode), Attributes = 0x40 /* OBJ_CASE_INSENSITIVE */ };
            int status = NtCreateFile(out SafeFileHandle handle, access | 0x100000 /* SYNCHRONIZE */, ref attributes, out _, 0,
                0x80 /* FILE_ATTRIBUTE_NORMAL */, share, disposition, options | 0x00200000 /* FILE_OPEN_REPARSE_POINT */, 0, 0);
            if (status >= 0) return handle;
            handle.Dispose();
            int error = unchecked((int)RtlNtStatusToDosError(status));
            var cause = new Win32Exception(error);
            if (error == 2) throw new FileNotFoundException($"{name} is no longer available in its held parent.", name, cause);
            if (error == 3) throw new DirectoryNotFoundException($"{name} is no longer available in its held parent.", cause);
            if (error == 32) throw new IOException($"Another program has {name} open.", cause);
            throw new IOException($"Cannot open {name} in its held parent: {cause.Message}", cause);
        }
    }

    public void Dispose()
    {
        foreach (SafeFileHandle? handle in held.Values) handle?.Dispose();
        held.Clear();
    }

    private static void Check(FileAttributes attributes, string directory)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{directory} is a link; no file was written through it.");
        if (!attributes.HasFlag(FileAttributes.Directory))
            throw new IOException($"{directory} is not an ordinary folder; no file was written through it.");
    }
    private static IOException Error(string directory)
    {
        int error = Marshal.GetLastPInvokeError();
        var cause = new Win32Exception(error);
        if (error is 2 or 3) return new DirectoryNotFoundException($"The folder {directory} is no longer available.", cause);
        return new IOException($"Cannot hold the folder {directory} in place while saving: {cause.Message}", cause);
    }
    private static string Extended(string full) => full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) ? full
        : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeName { public ushort Length; public ushort MaximumLength; public nint Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public int Length; public nint RootDirectory; public nint ObjectName; public uint Attributes; public nint SecurityDescriptor; public nint SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public nint Status; public nuint Information; }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileIdentity(SafeFileHandle handle, int informationClass, out FileIdentity information, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint size);
    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status, nint allocationSize,
        uint fileAttributes, FileShare share, uint disposition, uint options, nint extendedAttributes, uint extendedAttributeLength);
    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryDirectoryFile(SafeFileHandle file, nint eventHandle, nint apcRoutine, nint apcContext, out IoStatus status,
        [Out] byte[] buffer, uint length, int informationClass, [MarshalAs(UnmanagedType.U1)] bool singleEntry, nint fileName, [MarshalAs(UnmanagedType.U1)] bool restartScan);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, FileShare share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
}
