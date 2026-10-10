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
            throw new InvalidDataException($"'{JsonData.ShownText(relative ?? "", 256)}' is not a relative path.");
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        string prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        if (!System.IO.Path.EndsInDirectorySeparator(prefix)) prefix += System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"'{JsonData.ShownText(relative, 256)}' escapes its folder.");
        return full;
    }
    public static string Relative(string root, string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>The longest name Windows file systems store (NTFS, ReFS, exFAT and FAT long names): 255 UTF-16 code units.</summary>
    public const int MaximumNameLength = 255;
    /// <summary>
    /// The longest full path Windows creates a file at, in its extended form (<c>\\?\C:\…</c>): 32,766 characters, so
    /// 32,762 for a path written with a drive letter. A longer one cannot be created, and .NET cannot open it by its path.
    /// </summary>
    public const int MaximumExtendedPathLength = 32_766;

    /// <summary>
    /// The path rules of a source save (<see cref="SourcePublisher"/>), checked without disk access. Edits are admitted
    /// with them too (<see cref="SourceWorkspace.CheckEditable"/>, with <see cref="CheckSavableTarget"/> for what is on disk),
    /// so a file no save could write never becomes an accepted edit that blocks every later save: a normalized relative path
    /// inside <c>data</c> or <c>gamegen</c> whose every name Windows stores as written (no device name such as AUX or COM1,
    /// with or without an extension, no character Windows forbids in names, no trailing dot or space, at most
    /// <see cref="MaximumNameLength"/> characters), at most <see cref="DirectoryLease.MaximumAncestors"/> folders below the
    /// project, whose full path Windows can create (<see cref="MaximumExtendedPathLength"/>) and whose depth and length a
    /// save's path planning admits (<see cref="SourcePublisher.MaximumPlanningPathWork"/>).
    /// </summary>
    public static void CheckSavablePath(string root, string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        string[] parts = relative.Split('/');
        string shown = JsonData.ShownText(relative, 256);
        if (parts.Length < 2 || !(parts[0].Equals(DataFolder, StringComparison.OrdinalIgnoreCase) || parts[0].Equals(GameGenFolder, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"'{shown}' is not a source file inside the {DataFolder} or {GameGenFolder} folder.");
        foreach (string part in parts)
        {
            if (part is "" or "." or "..") continue; // Resolve refuses these below.
            string reason = IsDeviceName(part) ? "which Windows reserves for a device (CON, PRN, AUX, NUL, or COM or LPT followed by a digit, with or without an extension)"
                : part.IndexOfAny(InvalidNameCharacters) >= 0 || part.EndsWith('.') || part.EndsWith(' ') ? "which Windows does not store as written (it has a character Windows forbids in names, or ends with a dot or space)"
                : part.Length > MaximumNameLength ? $"of {part.Length:N0} characters, longer than the {MaximumNameLength} Windows stores in a name" : "";
            if (reason.Length > 0)
                throw new InvalidDataException($"'{shown}' contains the name '{JsonData.ShownText(part, 128)}', {reason}, so the project cannot save it; rename '{JsonData.ShownText(part, 128)}' and try again.");
        }
        if (parts.Length - 1 > DirectoryLease.MaximumAncestors)
            throw new InvalidDataException($"'{shown}' lies {parts.Length - 1:N0} folders deep, more than the {DirectoryLease.MaximumAncestors:N0} a save creates below the project, so the project cannot save it; choose a shallower folder.");
        // The full path as the save computes it (Resolve), measured before .NET is asked to expand it.
        string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        long length = full.Length + (System.IO.Path.EndsInDirectorySeparator(full) ? 0 : 1) + relative.Length;
        long extended = length + (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) ? 0 : full.StartsWith(@"\\", StringComparison.Ordinal) ? 6 : 4);
        if (extended > MaximumExtendedPathLength)
            throw new InvalidDataException($"'{shown}' would have a full path of {length:N0} characters in this project, longer than Windows creates a file at, so the project cannot save it; use shorter folder and file names.");
        try { new SourcePublisher.PathWorkBudget(full.Length, SourcePublisher.MaximumPlanningPathWork, CancellationToken.None).Reserve(relative); }
        catch (InvalidDataException)
        {
            throw new InvalidDataException($"'{shown}' is too deep and long for a save to plan ({parts.Length:N0} names, {relative.Length:N0} characters), so the project cannot save it; use shallower folders or shorter names.");
        }
        if (!Relative(root, Resolve(root, relative)).Equals(relative, StringComparison.Ordinal))
            throw new InvalidDataException($"'{shown}' is not a normalized relative path.");
    }

    /// <summary>
    /// The rules of a source save that depend on what is on disk now, checked when an edit is made
    /// (<see cref="SourceWorkspace.CheckEditable"/>) after <see cref="CheckSavablePath"/>: the save
    /// (<see cref="SourcePublisher"/>) refuses a link on the way (also the file itself), an existing file used as a folder, a
    /// folder where the file would be, and another spelling of an existing entry (a short 8.3 name). Nothing is followed;
    /// the walk ends at the first name that does not exist. The save checks them again, holding the folders it writes into.
    /// </summary>
    public static void CheckSavableTarget(string root, string relative, CancellationToken token = default)
    {
        _ = Resolve(root, relative);
        string current = System.IO.Path.GetFullPath(root); var parts = relative.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            string parent = current;
            current = System.IO.Path.Combine(current, parts[i]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{current} is a link; nothing was written through it.");
            string shown = JsonData.ShownText(relative, 256), name = JsonData.ShownText(string.Join('/', parts, 0, i + 1), 256);
            bool folder = attributes.HasFlag(FileAttributes.Directory);
            if (i < parts.Length - 1 && !folder)
                throw new InvalidDataException($"'{shown}' uses the file '{name}' as a folder, so the project cannot save it; choose another folder, or rename '{name}' outside zStudio.");
            if (i == parts.Length - 1 && folder)
                throw new InvalidDataException($"'{shown}' is a folder in the project, so the project cannot save a file there; choose another name.");
            // Windows makes short names with a tilde (MODELS~1); the save lists each folder and refuses a name it does not
            // list, which would let one file be edited and saved under two names.
            if (parts[i].Contains('~') && !Listed(parent, parts[i], token))
                throw new InvalidDataException($"'{shown}' names an existing entry by another spelling ('{name}', such as a short name), so the project cannot save it; use its full name.");
        }

        static bool Listed(string folder, string name, CancellationToken token)
        {
            foreach (var entry in Entries(folder, new ScanBudget(MaximumScannedEntries, maximum => TooManyEntries(folder, maximum), token)))
                if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
    /// <summary>A name Windows reserves for a device, with or without an extension (<c>aux.png</c>, <c>COM1.txt</c>, <c>nul</c>).</summary>
    public static bool IsDeviceName(string name) => DeviceName().IsMatch(name);
    [System.Text.RegularExpressions.GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])\s*(\..*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DeviceName();
    private static readonly char[] InvalidNameCharacters = System.IO.Path.GetInvalidFileNameChars();

    internal static void RequireSource(string relative)
    {
        string first = relative.Split('/')[0];
        if (!first.Equals(DataFolder, StringComparison.OrdinalIgnoreCase) && !first.Equals(GameGenFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{JsonData.ShownText(relative)} is not an authoritative source: build inputs must stay under data/ or gamegen/.");
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> holds exactly <paramref name="expected"/>. The file is read in small
    /// blocks, so checking a file of hundreds of megabytes against the copy already in memory needs no second copy of it.
    /// </summary>
    /// <param name="share">What others may do meanwhile: by default read, and zStudio's own save may hold and move it
    /// (<see cref="SourceRead.Sharing"/>); a file a sealed output being written holds (<see cref="SealedFile"/>) needs
    /// <see cref="FileShare.ReadWrite"/> and <see cref="FileShare.Delete"/>.</param>
    internal static bool FileEquals(string path, ReadOnlySpan<byte> expected, CancellationToken token = default, FileShare share = SourceRead.Sharing)
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
    internal static IReadOnlyList<string> Files(string root, string folder, Func<string, bool> include, IReadOnlyCollection<string>? added = null, CancellationToken token = default,
        InventoryBudget? inventory = null, Action<int>? retainPath = null)
        => Files(root, folder, include, added, MaximumScannedEntries, token, inventory, retainPath);
    /// <param name="maximumEntries">The entries the scan may visit (<see cref="MaximumScannedEntries"/>; smaller in tests).</param>
    internal static IReadOnlyList<string> Files(string root, string folder, Func<string, bool> include, IReadOnlyCollection<string>? added, int maximumEntries, CancellationToken token,
        InventoryBudget? inventory = null, Action<int>? retainPath = null)
    {
        inventory ??= new();
        var files = DiskFiles(root, folder, include, maximumEntries, token, inventory, retainPath);
        if (added == null || added.Count == 0) return files;
        string prefix = folder.TrimEnd('/') + "/";
        inventory.Rows(files.Count, token);
        HashSet<string> listed = new(files, StringComparer.OrdinalIgnoreCase);
        if (added.Count > maximumEntries) throw TooManyEntries(folder, maximumEntries);
        var pending = PendingInventory.Capture(added, inventory, token);
        foreach (string entry in pending.Below(prefix, token))
        {
            token.ThrowIfCancellationRequested();
            inventory.Inspect(entry.Length, token); // Bound the filename scan before looking back through the path.
            var name = System.IO.Path.GetFileName(entry.AsSpan());
            inventory.Path(name.Length, token); // Before allocating a leaf string for the existing predicate.
            if (include(name.ToString()))
            {
                inventory.Path(entry.Length, token); // Actual full-path hashing and retained result ownership.
                if (!listed.Contains(entry)) { retainPath?.Invoke(entry.Length); listed.Add(entry); }
                if (listed.Count > MaximumFiles) throw new IOException($"{folder} has more than {MaximumFiles:N0} files including pending sources.");
            }
        }
        inventory.Rows(listed.Count, token);
        return listed.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static IReadOnlyList<string> DiskFiles(string root, string folder, Func<string, bool> include, int maximumEntries, CancellationToken token,
        InventoryBudget inventory, Action<int>? retainPath)
    {
        string path = Resolve(root, folder);
        // The folders leading to the listed one must be regular too; enumeration below only sees their contents.
        RejectNestedLinks(root, folder);
        if (!SourceRead.DirectoryExists(path)) return [];
        List<string> files = [];
        // Counted before it is looked at: files the scan does not want (editor caches, backups) cost as much to visit.
        string fullRoot = System.IO.Path.GetFullPath(root);
        int prefixLength = System.IO.Path.TrimEndingDirectorySeparator(fullRoot).Length + (System.IO.Path.EndsInDirectorySeparator(fullRoot) && fullRoot == System.IO.Path.GetPathRoot(fullRoot) ? 0 : 1);
        foreach (var info in Entries(path, new ScanBudget(maximumEntries, maximum => TooManyEntries(folder, maximum), token, inventory), recurse: true))
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{info.FullName} is a link; source projects contain regular files.");
            if (info is FileInfo file && include(file.Name))
            {
                // Entries admits the full identity before retention. Reserve the consumer's narrower allowance
                // before GetRelativePath/Replace constructs another string (all entries are below this root).
                retainPath?.Invoke(file.FullName.Length - prefixLength);
                files.Add(Relative(root, file.FullName));
            }
            if (files.Count > MaximumFiles) throw new IOException($"{folder} has more than {MaximumFiles:N0} files.");
        }
        inventory.Rows(files.Count, token);
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
    internal sealed class ScanBudget(int maximum, Func<int, Exception> refusal, CancellationToken token, InventoryBudget? inventory = null)
    {
        private long visited;
        internal CancellationToken Token => token;
        internal InventoryBudget Inventory { get; } = inventory ?? new();
        internal void Path(long characters) => Inventory.Path(characters, token);
        internal void Rows(long count) => Inventory.Rows(count, token);
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
            // The framework owns one OS-bounded transient entry. Admit its complete path before a caller
            // retains it or constructs relative/normalized copies; ignored entries spend the same path work.
            budget.Path(info.FullName.Length);
            yield return info;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\Am[0-9]{1,3}\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    internal static partial System.Text.RegularExpressions.Regex MissionName();
    /// <summary>
    /// The mission folders (<c>data/mN</c>) as the disk spells them, by mission number. Listing <c>data</c> is a scan like any
    /// other (see <see cref="Files(string, string, Func{string, bool}, IReadOnlyCollection{string}?, CancellationToken, InventoryBudget?, Action{int}?)"/>):
    /// every entry there counts towards <paramref name="maximumEntries"/>, whether it is a mission folder or not, and
    /// <paramref name="token"/> is observed at each. Links are listed like folders; the scans of what they hold refuse them.
    /// </summary>
    internal static IReadOnlyList<string> MissionFolders(string root, CancellationToken token, int maximumEntries = MaximumScannedEntries, InventoryBudget? inventory = null)
    {
        string data = Resolve(root, DataFolder);
        if (!SourceRead.DirectoryExists(data)) return [];
        return Entries(data, new ScanBudget(maximumEntries, maximum => TooManyEntries(DataFolder, maximum), token, inventory))
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
        => RejectNestedLinks(root, relative, null);

    internal static void RejectNestedLinks(string root, string relative, Action<long>? reservePathWork)
    {
        // Validate the whole spelling before stopping at an absent ancestor: a later '..' or rooted component
        // must not redirect a caller beyond the missing prefix after this check returns.
        _ = Resolve(root, relative);
        string current = System.IO.Path.GetFullPath(root); var parts = relative.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            // A discovery may visit the same deep existing chain for many missing candidates. Its shared work
            // allowance covers every actual prefix before constructing it; missing ancestors still end the walk.
            reservePathWork?.Invoke(128L + 4L * (current.Length + parts[i].Length + 1));
            current = System.IO.Path.Combine(current, parts[i]);
            // FileInfo.Exists is false for directory links, including the last component (recovery/staging).
            // Attributes inspect either kind and also reject dangling links rather than following them later.
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"{current} is a link; nothing was written through it.");
            }
            // No descendant exists below an absent ancestor. Continuing would allocate/probe every growing
            // prefix, making a deep missing search path quadratic. Other I/O/access failures still propagate.
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
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
