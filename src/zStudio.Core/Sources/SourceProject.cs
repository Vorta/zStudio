using System.Security.Cryptography;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// A RECOIL source tree in the original build layout: <c>data/</c> holds the assets and <c>gamegen/</c> the build scripts.
/// The tree carries no zStudio metadata; game files are built from it on export and must work in the game, not match
/// the shipped bytes.
/// </summary>
public static partial class SourceProject
{
    public const string DataFolder = "data", GameGenFolder = "gamegen";
    /// <summary>Text sources are bounded before they are decoded; retail sources are at most a few hundred kilobytes.</summary>
    public const int MaximumSourceTextBytes = 16 * 1024 * 1024;
    public const int MaximumFiles = 50_000;
    /// <summary>
    /// The files and folders a scan of a source folder visits, whatever it looks for: the reconstructed releases have about
    /// 5,750, and editor files, backups and design sources beside them leave a project far below this.
    /// </summary>
    public const int MaximumScannedEntries = 250_000;

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
    /// <param name="share">What others may do meanwhile: by default only read; a file another of zStudio's handles holds
    /// (a sealed output, <see cref="SealedFile"/>) needs <see cref="FileShare.ReadWrite"/> and <see cref="FileShare.Delete"/>.</param>
    internal static bool FileEquals(string path, ReadOnlySpan<byte> expected, CancellationToken token = default, FileShare share = FileShare.Read)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, share, 0, FileOptions.SequentialScan);
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
    /// Every file and folder visited counts towards <see cref="MaximumScannedEntries"/>, whether it is included or not, and
    /// <paramref name="token"/> is observed at each.
    /// </summary>
    internal static IReadOnlyList<string> Files(string root, string folder, Func<string, bool> include, IReadOnlyCollection<string>? added = null, CancellationToken token = default)
        => Files(root, folder, include, added, MaximumScannedEntries, token);
    /// <param name="maximumEntries">The entries the scan may visit (<see cref="MaximumScannedEntries"/>; smaller in tests).</param>
    internal static IReadOnlyList<string> Files(string root, string folder, Func<string, bool> include, IReadOnlyCollection<string>? added, int maximumEntries, CancellationToken token)
    {
        var files = DiskFiles(root, folder, include, maximumEntries, token);
        if (added == null || added.Count == 0) return files;
        string prefix = folder.TrimEnd('/') + "/";
        HashSet<string> listed = new(files, StringComparer.OrdinalIgnoreCase);
        var extra = added.Where(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && include(System.IO.Path.GetFileName(a)) && !listed.Contains(a));
        return files.Concat(extra).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static IReadOnlyList<string> DiskFiles(string root, string folder, Func<string, bool> include, int maximumEntries, CancellationToken token)
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
        // Counted before it is looked at: files the scan does not want (editor caches, backups) cost as much to visit.
        foreach (var info in Entries(path, new ScanBudget(maximumEntries, maximum => TooManyEntries(folder, maximum), token), recurse: true))
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{info.FullName} is a link; source projects contain regular files.");
            if (info is FileInfo file && include(file.Name)) files.Add(Relative(root, file.FullName));
            if (files.Count > MaximumFiles) throw new IOException($"{folder} has more than {MaximumFiles:N0} files.");
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    /// <summary>A scan that would visit more than <paramref name="maximum"/> files and folders, and what to do about it.</summary>
    internal static IOException TooManyEntries(string folder, int maximum) => new(
        $"{folder} holds more than {maximum:N0} files and folders, far more than a source project needs. " +
        $"Move files the build does not use (editor caches, backups, design files) out of the project's {DataFolder} and {GameGenFolder} folders, then try again.");

    /// <summary>
    /// The files and folders one scan visits, counted together whatever the scan looks for: at most <paramref name="maximum"/>
    /// (<see cref="MaximumScannedEntries"/>, or less), beyond which <paramref name="refusal"/> says what to do. The token is
    /// observed at each entry, so a folder of millions of entries neither runs on unbounded nor ignores a cancellation.
    /// </summary>
    internal sealed class ScanBudget(int maximum, Func<int, Exception> refusal, CancellationToken token)
    {
        private long visited;
        public void Visit(long entries = 1)
        {
            token.ThrowIfCancellationRequested();
            if ((visited += entries) > maximum) throw refusal(maximum);
        }
    }
    /// <summary>
    /// The entries of <paramref name="path"/> (with <paramref name="recurse"/>, of every folder below it as well) as the file
    /// system lists them, hidden ones and links included unless <paramref name="skip"/> leaves them out, and a folder that
    /// cannot be read refused unless <paramref name="ignoreInaccessible"/>; each is counted by <paramref name="budget"/> before
    /// it is returned. Every scan of a project's folders, of zStudio's working folder and of a game folder lists through
    /// this, never through an unbounded enumeration.
    /// </summary>
    internal static IEnumerable<FileSystemInfo> Entries(string path, ScanBudget budget, bool recurse = false, FileAttributes skip = 0, bool ignoreInaccessible = false)
    {
        EnumerationOptions options = new() { RecurseSubdirectories = recurse, IgnoreInaccessible = ignoreInaccessible, AttributesToSkip = skip };
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            budget.Visit();
            yield return info;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\Am[0-9]{1,3}\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    internal static partial System.Text.RegularExpressions.Regex MissionName();
    /// <summary>
    /// The mission folders (<c>data/mN</c>) as the disk spells them, by mission number. Listing <c>data</c> is a scan like any
    /// other (see <see cref="Files(string, string, Func{string, bool}, IReadOnlyCollection{string}?, CancellationToken)"/>):
    /// every entry there counts towards <paramref name="maximumEntries"/>, whether it is a mission folder or not, and
    /// <paramref name="token"/> is observed at each. Links are listed like folders; the scans of what they hold refuse them.
    /// </summary>
    internal static IReadOnlyList<string> MissionFolders(string root, CancellationToken token, int maximumEntries = MaximumScannedEntries)
    {
        string data = Resolve(root, DataFolder);
        if (!Directory.Exists(data)) return [];
        return Entries(data, new ScanBudget(maximumEntries, maximum => TooManyEntries(DataFolder, maximum), token))
            .Where(e => e is DirectoryInfo && MissionName().IsMatch(e.Name)).Select(e => e.Name).OrderBy(n => int.Parse(n.AsSpan(1))).ToArray();
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
    /// <summary>
    /// Refuse a source project that is a link or lies below one: its edits, saves and recovery would be written into the
    /// folder the link leads to, and the checks of the files inside it (<see cref="RejectNestedLinks"/>) start below the root.
    /// </summary>
    public static void RejectLinkedProject(string root)
    {
        for (var directory = new DirectoryInfo(System.IO.Path.GetFullPath(root)); directory != null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"{directory.FullName} is a link; source projects are opened only from regular folders, so that nothing is written through a link into another folder.");
    }
    /// <summary>Refuse links on the way from <paramref name="root"/> to a relative file, including the file itself; nothing is followed.</summary>
    public static void RejectNestedLinks(string root, string relative)
    {
        string current = System.IO.Path.GetFullPath(root); var parts = relative.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            current = System.IO.Path.Combine(current, parts[i]);
            // FileInfo.Exists is false for directory links, including the last component (recovery/staging).
            // Attributes inspect either kind and also reject dangling links rather than following them later.
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"{current} is a link; nothing was written through it.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
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
