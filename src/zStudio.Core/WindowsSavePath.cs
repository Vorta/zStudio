using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Recoil.Zbd.Core;

/// <summary>Resolves directory aliases before checking a Windows save destination.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsSavePath
{
    public static string ResolveExistingParent(string fullPath)
    {
        string directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("Choose a destination file inside an existing drive.");
        Stack<string> missing = new();
        missing.Push(Path.GetFileName(fullPath));
        while (true)
        {
            // Query the directory, not the destination file: Save As may create a new file,
            // and an existing archive can be locked. Zero access still permits a name query.
            using SafeFileHandle handle = CreateFile(directory, 0, FileShare.Read | FileShare.Write | FileShare.Delete,
                0, 3 /* OPEN_EXISTING */, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, 0);
            if (!handle.IsInvalid)
            {
                string resolved = FinalName(handle, directory);
                foreach (string component in missing) resolved = Path.Combine(resolved, component);
                return resolved;
            }
            int error = Marshal.GetLastPInvokeError();
            // A new nested destination is checked using its nearest existing ancestor.
            // Access denied, unavailable drives and other resolution errors must not allow writes.
            string? parent = Path.GetDirectoryName(directory);
            if (error is not (2 or 3) || parent == null) throw ResolutionError(directory, error);
            missing.Push(Path.GetFileName(directory));
            directory = parent;
        }
    }

    private static string FinalName(SafeFileHandle handle, string directory)
    {
        char[] buffer = new char[512];
        while (true)
        {
            // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS expands short names and SUBST roots.
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) throw ResolutionError(directory, Marshal.GetLastPInvokeError());
            if (length < buffer.Length) return new string(buffer, 0, (int)length);
            buffer = new char[checked((int)length + 1)];
        }
    }

    private static IOException ResolutionError(string directory, int error)
    {
        var cause = new Win32Exception(error);
        return new IOException($"Cannot verify the save destination '{directory}': {cause.Message}. Choose an accessible destination on an existing drive.", cause);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, FileShare share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);
}
