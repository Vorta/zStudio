using System.Security.Cryptography;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// A RECOIL source tree in the original build layout: <c>data/</c> holds the assets and <c>gamegen/</c> the build scripts.
/// The tree carries no zStudio metadata; game files are built from it on export and must work in the game, not match
/// the shipped bytes.
/// </summary>
public static class SourceProject
{
    public const string DataFolder = "data", GameGenFolder = "gamegen";
    /// <summary>Text sources are bounded before they are decoded; retail sources are at most a few hundred kilobytes.</summary>
    public const int MaximumSourceTextBytes = 16 * 1024 * 1024;
    public const int MaximumFiles = 50_000;

    /// <summary>A folder is a source project when it has both top-level folders of the build layout.</summary>
    public static bool IsProject(string? root) => root != null && Directory.Exists(System.IO.Path.Combine(root, DataFolder)) && Directory.Exists(System.IO.Path.Combine(root, GameGenFolder));
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Resolve a root-relative path (forward slashes), refusing anything that would escape the root.</summary>
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException($"'{relative}' is not a relative path.");
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        string prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"'{relative}' escapes its folder.");
        return full;
    }
    public static string Relative(string root, string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// Whether the file at <paramref name="path"/> holds exactly <paramref name="expected"/>. The file is read in small
    /// blocks, so checking a file of hundreds of megabytes against the copy already in memory needs no second copy of it.
    /// </summary>
    internal static bool FileEquals(string path, ReadOnlySpan<byte> expected, CancellationToken token = default)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan);
        if (stream.Length != expected.Length) return false;
        byte[] block = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            for (int offset = 0; ;)
            {
                token.ThrowIfCancellationRequested();
                int read = stream.Read(block);
                // A file that grew or shrank since its length was read differs too.
                if (read == 0) return offset == expected.Length;
                if (read > expected.Length - offset || !block.AsSpan(0, read).SequenceEqual(expected.Slice(offset, read))) return false;
                offset += read;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(block); }
    }

    /// <summary>
    /// Regular files below <paramref name="folder"/> (root-relative, forward slashes) in a stable order; links are refused.
    /// <paramref name="added"/> are files that exist only as pending content (a workspace's new files) and count as present.
    /// </summary>
    internal static IReadOnlyList<string> Files(string root, string folder, Func<string, bool> include, IReadOnlyCollection<string>? added = null)
    {
        var files = DiskFiles(root, folder, include);
        if (added == null || added.Count == 0) return files;
        string prefix = folder.TrimEnd('/') + "/";
        var extra = added.Where(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && include(System.IO.Path.GetFileName(a)) && !files.Contains(a, StringComparer.OrdinalIgnoreCase));
        return files.Concat(extra).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static IReadOnlyList<string> DiskFiles(string root, string folder, Func<string, bool> include)
    {
        string path = Resolve(root, folder), current = System.IO.Path.GetFullPath(root);
        // The folders leading to the listed one must be regular too; enumeration below only sees their contents.
        foreach (string part in folder.Split('/'))
        {
            current = System.IO.Path.Combine(current, part); DirectoryInfo step = new(current);
            if (step.Exists && step.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{step.FullName} is a link; source projects contain regular files.");
        }
        if (!Directory.Exists(path)) return [];
        List<string> files = [];
        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 };
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{info.FullName} is a link; source projects contain regular files.");
            if (info is FileInfo file && include(file.Name)) files.Add(Relative(root, file.FullName));
            if (files.Count > MaximumFiles) throw new IOException($"{folder} has more than {MaximumFiles:N0} files.");
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// A destination: never a protected original corpus, never inside or containing the input. Both are compared as
    /// written and, on Windows, as the file system resolves them, so a short (8.3) name, a SUBST drive or a link above
    /// the input cannot disguise the same folder.
    /// </summary>
    public static void ValidateSeparate(string destination, string input, string role)
    {
        string d = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(destination)), i = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(input));
        if (PickupPlacementEditSession.IsProtectedPath(d)) throw new InvalidDataException($"The {role} cannot be inside the protected zbd_1998/zbd_1999 folders.");
        if (Overlap(d, i) || OperatingSystem.IsWindows() && Overlap(Resolved(d), Resolved(i)))
            throw new InvalidDataException($"The {role} must be separate from {input}.");
        static bool Overlap(string a, string b) => Within(a, b) || Within(b, a);
        // A drive root already ends with its separator.
        static bool Within(string path, string folder) => path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(System.IO.Path.EndsInDirectorySeparator(folder) ? folder : folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        // The folder's final path, or its nearest existing ancestor's with the missing names appended.
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        static string Resolved(string folder) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetDirectoryName(WindowsSavePath.ResolveExistingParent(System.IO.Path.Combine(folder, "_")))!);
    }
    /// <summary>Refuse to write through directory links anywhere above a destination.</summary>
    public static void RejectLinks(string path)
    {
        for (var directory = new DirectoryInfo(System.IO.Path.GetFullPath(path)); directory != null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{directory.FullName} is a link; choose a folder of regular directories.");
    }
    /// <summary>Refuse links on the way from <paramref name="root"/> to a relative file, including the file itself; nothing is followed.</summary>
    public static void RejectNestedLinks(string root, string relative)
    {
        string current = System.IO.Path.GetFullPath(root); var parts = relative.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            current = System.IO.Path.Combine(current, parts[i]);
            FileSystemInfo info = i < parts.Length - 1 ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{info.FullName} is a link; nothing was written through it.");
        }
    }
}

/// <summary>
/// Progress of a source operation: <see cref="Completed"/> of <see cref="Total"/> counted items and the current
/// <see cref="Item"/>. Work after the counted items reports its <see cref="Stage"/> instead, the item naming what it is on.
/// </summary>
public sealed record SourceProgress(int Completed, int Total, string Item, SourceStage Stage = SourceStage.Items);

/// <summary>What a <see cref="SourceProgress"/> reports: a counted item, or later work that writes sources or checks them.</summary>
public enum SourceStage { Items, Reconstructing, Validating }
