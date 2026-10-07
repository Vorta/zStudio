using System.Text.RegularExpressions;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// One file of a source save: its project-relative path (forward slashes), the bytes it must have now
/// (<see langword="null"/>: it must not exist) and the bytes to write (<see langword="null"/>: delete it). An entry whose
/// content equals its expectation is checked before anything is written, but not written.
/// </summary>
public sealed record SourceFileWrite(string Relative, byte[]? Expected, byte[]? Content);

/// <summary>A completed save: its journal identity and the files it wrote or deleted, in request order and spelling.</summary>
public sealed record SourcePublishResult(string SaveId, IReadOnlyList<string> Written);

/// <summary>Files that did not have their expected content. The save was refused or undone; it left nothing on disk.</summary>
public sealed class SourceConflictException(string message, IReadOnlyList<string> files, Exception? inner = null) : IOException(message, inner)
{
    public IReadOnlyList<string> Files { get; } = files;
}

/// <summary>
/// A save whose journal remains in <c>zstudio/recovery</c> because it was interrupted, could not be fully undone or cannot be
/// read. Source saves stay blocked until it is resolved with <see cref="SourcePublisher.Resolve"/>.
/// </summary>
public sealed class SourceRecoveryRequiredException(string message, string saveId, IReadOnlyList<string> files, Exception? inner = null) : IOException(message, inner)
{
    public string SaveId { get; } = saveId;
    /// <summary>The files that still need a decision, when they are known.</summary>
    public IReadOnlyList<string> Files { get; } = files;
}

/// <summary>
/// Saves several source files of one project as a recoverable transaction. The new contents are journaled and staged in the
/// project's <c>zstudio</c> folder first; then, under a project-wide lock, each original is moved aside only while it still
/// has its expected content (other writers are excluded while it is compared) and each new file is moved in only while its
/// name is free, its staged copy checked against the journaled content and held against other writers until it is in place.
/// A failure undoes exactly what the save did. If that is impossible, or the process ends, the journal stays and further
/// saves are refused until <see cref="Resolve"/> rolls the save back, completes it or abandons it.
/// <para>
/// Limits: a file name is briefly absent while it is published, and other programs do not see the files change at the
/// same instant. Replaced files get new file-system metadata (attributes, timestamps, permissions). Publication never
/// replaces a file it did not expect and never recreates a file that went missing for reasons it does not know.
/// </para>
/// </summary>
public sealed partial class SourcePublisher
{
    /// <summary>zStudio's working data beside <c>data</c> and <c>gamegen</c>; builds never read it.</summary>
    public const string WorkingFolder = "zstudio";
    public const string RecoveryFolder = WorkingFolder + "/recovery", StagingFolder = WorkingFolder + "/staging", AbandonedFolder = RecoveryFolder + "/abandoned";
    /// <summary>Coordination only: opening this file to probe content can prevent an exclusive save/recovery lock.</summary>
    public const string LockFile = RecoveryFolder + "/" + LockName;
    public const int MaximumDescriptionLength = 1024;
    private const string ManifestName = "manifest.json", EventsName = "events.log", LockName = ".lock", RemovedSuffix = ".removed";
    private const string AfterFolder = "after", HeldFolder = "held", TakenFolder = "removed";
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();
    private readonly string root;
    /// <summary>
    /// The files and folders one listing may visit: the folders a save writes into (together), and the recovery and staging
    /// folders (each). <see cref="SourceProject.MaximumScannedEntries"/>, as for any scan of the project; smaller in tests.
    /// </summary>
    internal int ScanLimit { get; init; } = SourceProject.MaximumScannedEntries;

    /// <summary>A publisher for the source project at <paramref name="projectRoot"/>; nothing is written until a save or recovery.</summary>
    public SourcePublisher(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!SourceProject.IsProject(root)) throw new DirectoryNotFoundException($"{root} is not a source project; it needs both a data and a gamegen folder.");
        SourceProject.RejectLinkedProject(root);
    }

    public string Root => root;

    /// <summary>Test hook called before each step with its name and file index (-1 for steps of the whole save); it may throw to simulate a failure, or <see cref="Crash"/> to simulate the process ending.</summary>
    internal Action<string, int>? Fault { get; set; }
    /// <summary>Thrown by <see cref="Fault"/> to stand for the end of the process: nothing after it runs, not even rollback or cleanup.</summary>
    internal sealed class Crash() : Exception("Simulated end of the zStudio process.");
    private void Step(string step, int index) => Fault?.Invoke(step, index);

    private sealed record Target(int Order, string Name, string Relative, string Path, JournalDigest? Expected, JournalDigest? Content, byte[]? Bytes);

    /// <summary>
    /// Publishes every changed file of <paramref name="writes"/> or none of them. Every target must have its expected
    /// content first; a target that changes during the save stops it and the save is undone (<see cref="SourceConflictException"/>).
    /// Cancellation is honoured until publication starts; from then on the save finishes or is undone. If it cannot be
    /// undone completely, its journal is kept and <see cref="SourceRecoveryRequiredException"/> names the files.
    /// </summary>
    public SourcePublishResult Publish(IReadOnlyList<SourceFileWrite> writes, string description, CancellationToken token = default)
    {
        using DirectoryLease directories = new();
        ArgumentNullException.ThrowIfNull(writes); ArgumentNullException.ThrowIfNull(description);
        if (description.Length > MaximumDescriptionLength) throw new ArgumentException($"The save description is longer than {MaximumDescriptionLength} characters.", nameof(description));

       directories.Hold(root);
        var (changes, checks) = Plan(directories, writes, token);
        string id = NewSaveId();
        JournalManifest manifest = new(JournalFormat, id, description, DateTime.UtcNow, [.. changes.Select(c => new JournalFile(c.Relative, c.Expected, c.Content))], NewFolders(directories, changes, token));
        byte[] manifestBytes = PrepareManifest(manifest, token);
        token.ThrowIfCancellationRequested();
        using FileStream gate = Lock(directories);
        Tidy(directories, token);
        if (Journals(directories, token).FirstOrDefault(j => !j.Committed && !j.RolledBack) is { } pending) throw Blocked(pending);
        string[] conflicts = [.. changes.Concat(checks).OrderBy(t => t.Order).Where(t => !Look(directories, t.Path, token, t.Expected).Is(t.Expected)).Select(t => t.Name)];
        if (conflicts.Length > 0) throw new SourceConflictException($"{string.Join(", ", conflicts)} changed on disk since {(conflicts.Length == 1 ? "it was" : "they were")} read, or cannot be read now; nothing was saved. Reload to continue from the files on disk.", conflicts);
        foreach (var change in changes)
        {
            try
            {
                using FileStream file = directories.OpenFile(change.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (File.GetAttributes(file.SafeFileHandle).HasFlag(FileAttributes.ReadOnly)) throw new UnauthorizedAccessException($"{change.Name} is read-only; nothing was saved.");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }

        EventLog log = Prepare(directories, manifest, manifestBytes, changes, token);
        try { Install(directories, manifest, changes, log); }
        finally { log.Dispose(); }
        // The save is committed: a failure to clean up only leaves a committed journal that the next save removes.
        try { Step("cleanup", -1); Discard(directories, id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(id, [.. changes.Select(c => c.Name)]);
    }

    /// <summary>Writes the journal (new contents, then the manifest, then "prepared") and stages every new content; on failure removes them again.</summary>
    private EventLog Prepare(DirectoryLease directories, JournalManifest manifest, byte[] manifestBytes, Target[] changes, CancellationToken token)
    {
        string journal = JournalPath(manifest.SaveId), staging = StagingPath(manifest.SaveId); EventLog? log = null;

        try
        {
           directories.Hold(Path.Combine(journal, AfterFolder), create: true);
            for (int i = 0; i < changes.Length; i++)
                if (changes[i].Bytes is { } bytes) { token.ThrowIfCancellationRequested(); Step("after", i); CheckWorkingPath(AfterPath(journal, i)); WriteDurable(directories, AfterPath(journal, i), bytes); }
            Step("manifest", -1); CheckJournalPaths(manifest.SaveId); WriteManifest(directories, journal, manifestBytes);
            log = EventLog.Open(directories, journal, manifest.Files.Count);
            Step("prepared", -1); log.Append("prepared", -1);
            SourceProject.RejectNestedLinks(root, StagingFolder);
           directories.Hold(staging, create: true);
            for (int i = 0; i < changes.Length; i++)
                if (changes[i].Bytes is { } bytes) { token.ThrowIfCancellationRequested(); Step("stage", i); WriteVerified(directories, StagedPath(staging, i), bytes); }
            token.ThrowIfCancellationRequested();
            return log;
        }
        catch (Crash) { log?.Dispose(); throw; }
        catch
        {
            // Nothing in the project changed yet.
            log?.TryAppend("rolled-back"); log?.Dispose(); Discard(directories, manifest.SaveId);
            throw;
        }
    }

    /// <summary>Moves each original aside and each new file into place, then commits; a failure undoes what was done, in reverse.</summary>
    private void Install(DirectoryLease directories, JournalManifest manifest, Target[] changes, EventLog log)
    {
        string journal = JournalPath(manifest.SaveId), staging = StagingPath(manifest.SaveId);
        bool[] moved = new bool[changes.Length], installed = new bool[changes.Length];
        int reached = -1; bool committing = false;

        try
        {
            // Capture existing ancestors without creating folders for files this save may never reach.
            foreach (Target file in changes) _ = directories.CapturedPath(file.Path);
           directories.Hold(journal);
           directories.Hold(Path.Combine(journal, HeldFolder), create: true);
           directories.Hold(Path.Combine(journal, TakenFolder), create: true);
           directories.Hold(staging);
            for (int i = 0; i < changes.Length; i++)
            {
                Target file = changes[i]; reached = i;
                directories.Parent(file.Path, create: file.Content != null);
                Step("intent", i); log.Append("intent", i);
                CheckWorkingPath(StagedPath(staging, i));
                // The staged copy is checked against the journaled content before the original moves aside, and held until it
                // is in place: no other program can write or rename it meanwhile, so the file installed is the one journaled.
                using SealedFile? staged = file.Content is { } content ? Seal(StagedPath(staging, i), content, file, directories) : null;
                Step("hold", i);
                SourceProject.RejectNestedLinks(root, file.Relative);
                if (file.Expected is { } expected)
                {
                    var result = MoveIfContent(file.Path, HeldPath(journal, i), expected, directories);
                    moved[i] = result != Moved.Unchanged;
                    if (result != Moved.Done) throw ChangedDuringSave(file);
                    log.Append("held", i);
                }
                else if (Exists(directories, file.Path)) throw CreatedDuringSave(file);
                if (staged == null) continue;
                Step("install", i);
               directories.Parent(file.Path, create: true);
                try { staged.MoveTo(file.Path); }
                catch (IOException ex) when (Exists(directories, file.Path)) { throw CreatedDuringSave(file, ex); }
                installed[i] = true; log.Append("installed", i);
            }
            Step("commit", -1); committing = true; log.Append("committed", -1);
        }
        catch (Exception ex) when (ex is not Crash)
        {
            // A commit record that may have reached the disk must be voided before anything is undone.
            if (!log.TryAppend("rollback") && committing) throw Unfinished(manifest.SaveId, [.. changes.Select(c => c.Name)], ex);
            List<SourceRecoveryConflict> left = [];
            for (int i = reached; i >= 0; i--)
                if (Undo(directories, journal, manifest.Files[i], i, log, moved[i], installed[i]).Conflict is { } conflict) left.Add(conflict with { Relative = changes[i].Name });

            RemoveFolders(directories, manifest.Folders);
            if (left.Count > 0) throw Unfinished(manifest.SaveId, [.. left.Select(c => c.Relative)], ex, left);
            log.TryAppend("rolled-back"); log.Dispose(); Discard(directories, manifest.SaveId);
            throw;
        }
    }

    /// <summary>Holds a staged copy while it has <paramref name="content"/> (see <see cref="SealedFile"/>).</summary>
    private static SealedFile Seal(string staged, JournalDigest content, Target file, DirectoryLease directories)
    {
        try { return SealedFile.Open(staged, content, directories); }
        catch (IOException ex) { throw new IOException($"The staged copy of {file.Name} was not installed: {ex.Message}", ex); }
    }
    private static SourceConflictException ChangedDuringSave(Target file) => new($"{file.Name} changed on disk during the save; the save was undone. Reload to continue from the file on disk.", [file.Name]);
    private static SourceConflictException CreatedDuringSave(Target file, Exception? inner = null) => new($"{file.Name} was created by another program during the save; it was not replaced, and the save was undone.", [file.Name], inner);
    private static SourceRecoveryRequiredException Unfinished(string id, IReadOnlyList<string> files, Exception cause, IReadOnlyList<SourceRecoveryConflict>? conflicts = null) => new(
        $"Saving failed ({cause.Message}) and could not be completely undone{(conflicts == null ? "" : ": " + string.Join("; ", conflicts.Select(c => $"{c.Relative} {c.Reason}")))}. " +
        $"The save journal {RecoveryFolder}/{id} keeps the originals; roll the save back, complete it or abandon it before saving again.", id, files, cause);
    private static SourceRecoveryRequiredException Blocked(Journal pending) => new(
        $"The save \"{pending.Manifest.Description}\" ({pending.Id}) was interrupted. Roll it back, complete it or abandon it before saving again.",
        pending.Id, [.. pending.Manifest.Files.Select(f => f.Relative)]);

    /// <summary>Validates every write before anything is written: changed entries are published, identical ones only checked.</summary>
    private (Target[] Changes, Target[] Checks) Plan(DirectoryLease directories, IReadOnlyList<SourceFileWrite> writes, CancellationToken token)
    {
        if (writes.Count == 0) throw new ArgumentException("A save needs at least one file.", nameof(writes));
        if (writes.Count > SourceProject.MaximumFiles) throw new ArgumentException($"A save can include at most {SourceProject.MaximumFiles:N0} files.", nameof(writes));
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase); List<Target> changes = [], checks = [];
        Dictionary<string, Dictionary<string, WorkingEntry>> listings = new(StringComparer.OrdinalIgnoreCase);
        // Each folder on the way to a written file is listed once; together the listings are a scan of the project like any other.
        SourceProject.ScanBudget budget = new(ScanLimit, maximum => new IOException(
            $"The folders this save writes into hold more than {maximum:N0} files and folders, far more than a source project needs. " +
            $"Move files the build does not use (editor caches, backups, design files) out of the project's {SourceProject.DataFolder} and {SourceProject.GameGenFolder} folders, then save again."), token);
        for (int i = 0; i < writes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            SourceFileWrite write = writes[i] ?? throw new ArgumentException($"Save entry {i} is missing.", nameof(writes));
            ArgumentNullException.ThrowIfNull(write.Relative, nameof(writes));
            string relative = Canonical(directories, write.Relative, listings, budget);
            if (!seen.Add(relative)) throw new ArgumentException($"{write.Relative} is listed more than once in the save.", nameof(writes));
            string path = SourceProject.Resolve(root, relative);
            if (PickupPlacementEditSession.IsProtectedPath(path)) throw new IOException($"{write.Relative} is inside the protected zbd_1998/zbd_1999 folders; nothing was saved.");
            SourceProject.RejectNestedLinks(root, relative);
            Target target = new(i, write.Relative, relative, path, JournalDigest.Of(write.Expected), JournalDigest.Of(write.Content), write.Content);
            (Same(write.Expected, write.Content) ? checks : changes).Add(target);
        }
        if (changes.Count == 0) throw new ArgumentException("No file differs from its expected content; there is nothing to save.", nameof(writes));
        return ([.. changes], [.. checks]);
        static bool Same(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);
    }

    /// <summary>
    /// The path with each existing component spelled as it is on disk. Refuses paths outside <c>data</c> and <c>gamegen</c>
    /// (so never zStudio's working data), names Windows would alter or treat as devices, other spellings of an existing
    /// entry (such as short 8.3 names, which would let one file be listed twice) and files used as folders.
    /// </summary>
    private string Canonical(DirectoryLease directories, string relative, Dictionary<string, Dictionary<string, WorkingEntry>> listings, SourceProject.ScanBudget budget)
    {
        CheckSyntax(relative);
        string[] parts = relative.Split('/'); string current = root; bool exists = true;
        for (int i = 0; i < parts.Length; i++)
        {
            string next = Path.Combine(current, parts[i]);
            if (exists)
            {
                if (!listings.TryGetValue(current, out var entries))
                {
                    entries = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var info in WorkingEntries(directories, current, budget))
                        if (entries.TryAdd(info.Name, info) && entries.Count > SourceProject.MaximumFiles) throw new IOException($"{current} has more than {SourceProject.MaximumFiles:N0} entries.");
                    listings[current] = entries;
                }
                if (entries.TryGetValue(parts[i], out var entry))
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{entry.FullName} is a link; nothing was written through it.");
                    if (i < parts.Length - 1 && !entry.IsDirectory) throw new InvalidDataException($"'{relative}' uses the file {entry.FullName} as a folder.");
                    parts[i] = entry.Name; next = Path.Combine(current, entry.Name);
                }
                else if (Exists(directories, next)) throw new InvalidDataException($"'{relative}' names an existing entry by another spelling (such as a short name); use its full name.");
                else exists = false;
            }
            current = next;
        }
        return string.Join('/', parts);
    }

    /// <summary>Path rules that need no disk access; journals are checked with them too.</summary>
    private void CheckSyntax(string relative)
    {
        string full = SourceProject.Resolve(root, relative);
        string[] parts = relative.Split('/');
        if (parts.Length < 2 || !(parts[0].Equals(SourceProject.DataFolder, StringComparison.OrdinalIgnoreCase) || parts[0].Equals(SourceProject.GameGenFolder, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"'{relative}' is not a source file inside the {SourceProject.DataFolder} or {SourceProject.GameGenFolder} folder.");
        foreach (string part in parts)
            if (part.IndexOfAny(InvalidNameCharacters) >= 0 || DeviceName().IsMatch(part) || part.EndsWith('.') || part.EndsWith(' '))
                throw new InvalidDataException($"'{relative}' contains the name '{part}', which Windows does not store as written.");
        if (!SourceProject.Relative(root, full).Equals(relative, StringComparison.Ordinal)) throw new InvalidDataException($"'{relative}' is not a normalized relative path.");
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])\s*(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();

    /// <summary>Folders a save creates for new files, outermost first; a rollback removes them again while they are empty.</summary>
    private string[] NewFolders(DirectoryLease directories, IEnumerable<Target> changes, CancellationToken token)
    {
        List<string> folders = []; HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        foreach (var change in changes.Where(c => c.Bytes != null))
        {
            string[] parts = change.Relative.Split('/');
            for (int n = 2; n < parts.Length; n++)
            {
                token.ThrowIfCancellationRequested();
                string folder = string.Join('/', parts[..n]);
                if (seen.Add(folder) && !ExistingDirectory(directories, SourceProject.Resolve(root, folder)))
                {
                    if (folders.Count == JournalFoldersLimit) throw new InvalidDataException($"A save would create more than {JournalFoldersLimit:N0} folders; split it into smaller saves.");
                    characters += folder.Length;
                    if (characters > ManifestBytesLimit) throw new InvalidDataException("The save journal's folder paths exceed its byte budget; split it into smaller saves.");
                    folders.Add(folder);
                }
            }
        }
        return [.. folders];
    }

    private void RemoveFolders(DirectoryLease directories, IReadOnlyList<string> folders)
    {
        foreach (string folder in folders.OrderByDescending(f => f.Count(c => c == '/')).ThenByDescending(f => f, StringComparer.Ordinal))
        {
            try
            {
                SourceProject.RejectNestedLinks(root, folder);

                string path = SourceProject.Resolve(root, folder);
               directories.Hold(path);
                // Native empty-directory deletion is the check: a concurrent new entry prevents removal.
               directories.DeleteDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The project-wide publication lock; saves and recoveries in every process take it for their whole duration.</summary>
    private FileStream Lock(DirectoryLease directories)
    {
        // Working data is deleted recursively, so neither folder may lead elsewhere; nor may the project itself, which can
        // have become a link since this publisher was made.
        SourceProject.RejectLinkedProject(root);
        SourceProject.RejectNestedLinks(root, RecoveryFolder); SourceProject.RejectNestedLinks(root, StagingFolder);
        string folder = SourceProject.Resolve(root, RecoveryFolder);
        if (PickupPlacementEditSession.IsProtectedPath(folder) || PickupPlacementEditSession.IsProtectedPath(directories.CapturedPath(folder)))
            throw new IOException("Source projects inside the protected zbd_1998/zbd_1999 folders cannot be saved.");
       directories.Hold(folder, create: true);
        try { return directories.OpenFile(Path.Combine(folder, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
            || ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 })
        { throw new IOException($"Another save or recovery is running for {root}; nothing was changed. Try again when it has finished.", ex); }
    }

    private static string NewSaveId() => $"{DateTime.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4))}";
    [GeneratedRegex(@"^[0-9]{8}T[0-9]{9}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex SaveIdPattern();
    private static bool IsSaveId(string? text) => text != null && SaveIdPattern().IsMatch(text);

    private string JournalPath(string id) => SourceProject.Resolve(root, RecoveryFolder + "/" + id);
    private string StagingPath(string id) => SourceProject.Resolve(root, StagingFolder + "/" + id);
    private static string AfterPath(string journal, int index) => Path.Combine(journal, AfterFolder, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");
    private static string HeldPath(string journal, int index) => Path.Combine(journal, HeldFolder, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");
    /// <summary>A new name for a file a rollback takes back; repeated rollbacks of one save never collide.</summary>
    private static string TakenPath(string journal, int index) => Path.Combine(journal, TakenFolder, $"{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.bin");
    private static string StagedPath(string staging, int index) => Path.Combine(staging, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp");
    private string Display(string path) => SourceProject.Relative(root, path);

    private enum Presence { Absent, File, Other }
    // An unreadable or non-file entry counts as present. It must never be mistaken for a free publication name or a
    // missing manifest that would let a later save bypass an unresolved journal.
    private static bool Exists(DirectoryLease directories, string path) => Look(directories, path, default).Kind != Presence.Absent;
    private static bool ExistingDirectory(DirectoryLease directories, string path)
    {
        try { directories.Hold(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
    /// <summary>
    /// What a path holds now: nothing, a regular file of <see cref="Length"/> bytes, or something else (a folder, a link, an
    /// unreadable file). A file's <see cref="Digest"/> is known only when its length is that of a content it was probed for.
    /// </summary>
    private readonly record struct Probe(Presence Kind, JournalDigest? Digest, long Length = -1)
    {
        /// <summary>Whether the path has <paramref name="expected"/> (null: is absent); only contents it was probed for can be compared.</summary>
        public bool Is(JournalDigest? expected) => expected == null ? Kind == Presence.Absent
            : Kind == Presence.File && Length == expected.Length && (Digest ?? throw new InvalidOperationException("A file was compared with content it was not probed for.")) == expected;
    }
    /// <summary>
    /// What <paramref name="path"/> holds, compared with the <paramref name="candidates"/> the caller will ask about. The file
    /// is held against writers while it is read; it is read (in blocks, observing <paramref name="token"/>) only when its
    /// length is a candidate's, so a file another program replaced with something of another size is never hashed.
    /// </summary>
    private static Probe Look(DirectoryLease directories, string path, CancellationToken token, params ReadOnlySpan<JournalDigest?> candidates)
    {
        try
        {

            using FileStream stream = directories.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            foreach (JournalDigest? candidate in candidates)
                if (candidate?.Length == length) return new(Presence.File, JournalDigest.Of(stream, token), length);
            return new(Presence.File, null, length);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(Presence.Absent, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(Presence.Other, null); }
    }
    /// <summary><see cref="Look"/> for a journal path, treating a link that appeared on the way as an unexpected entry.</summary>
    private Probe LookAt(DirectoryLease directories, string relative, CancellationToken token, params ReadOnlySpan<JournalDigest?> candidates)
    {
        try { SourceProject.RejectNestedLinks(root, relative); }
        catch (IOException) { return new(Presence.Other, null); }
        return Look(directories, SourceProject.Resolve(root, relative), token, candidates);
    }

    internal enum Moved { Done, Unchanged, Stranded }
    /// <summary>
    /// Moves a file only while it has the expected content, excluding other writers and renames from comparison through
    /// the handle-based move. A missing, changed or unavailable original is left alone (<see cref="Moved.Unchanged"/>).
    /// Exports and reconstructions undo their own files with it too: moved to a new name on the same volume and deleted
    /// there, a file is removed only while it still has the content the run wrote. The Stranded outcome remains understood
    /// by recovery callers for the former path-based move; this implementation never moves an unverified replacement.
    /// </summary>
    internal static Moved MoveIfContent(string path, string destination, JournalDigest expected, DirectoryLease? captured = null)
    {
        using DirectoryLease? owned = captured == null ? new() : null;
        DirectoryLease directories = captured ?? owned!;
       directories.Parent(destination, create: true);
        SealedFile file;
        try
        {
           directories.Parent(path);
            file = SealedFile.Open(path, expected, directories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Moved.Unchanged; }
        // Rename the exact compared file through its held handle; neither its content nor its directory can redirect us.
        using (file) file.MoveTo(destination);
        return Moved.Done;
    }

    private static void WriteDurable(DirectoryLease directories, string path, byte[] bytes)
    {
        directories.Parent(path);
        using FileStream stream = directories.OpenFile(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
    /// <summary>Writes, flushes and reads a file back before it may be published.</summary>
    private void WriteVerified(DirectoryLease directories, string path, byte[] bytes)
    {
        directories.Parent(path);
        CheckWorkingPath(path);
        using (FileStream stream = directories.OpenFile(path, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        using FileStream verify = directories.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!JournalDigest.OfContent(bytes).Matches(verify)) throw new IOException($"{path} did not read back as it was written; nothing was saved.");
    }

    private void TryDelete(DirectoryLease directories, string folder)
    {
        try
        {
            directories.Hold(folder);
            SourceProject.ScanBudget budget = WorkingBudget(folder, default);
            Stack<(string Path, bool Remove)> pending = new([(folder, false)]);
            while (pending.TryPop(out var next))
            {
                if (next.Remove) { directories.DeleteDirectory(next.Path); continue; }
               directories.Hold(next.Path);
                pending.Push((next.Path, true));
                // Finish enumeration before deleting entries, and bound unknown material in a damaged journal too.
                var entries = directories.Entries(next.Path).Select(entry => { budget.Visit(); return entry; }).ToArray();
                foreach (var entry in entries)
                {
                    string path = Path.Combine(next.Path, entry.Name);
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{path} is a link; journal cleanup left it alone.");
                    if (entry.Attributes.HasFlag(FileAttributes.Directory)) pending.Push((path, false));
                    else directories.DeleteFile(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static bool HasFiles(DirectoryLease directories, string folder)
    {
        try { return directories.Entries(folder).Any(); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private sealed record WorkingEntry(string Name, string FullName, FileAttributes Attributes)
    {
        public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);
    }
    private static IEnumerable<WorkingEntry> WorkingEntries(DirectoryLease directories, string folder, SourceProject.ScanBudget budget)
    {

        foreach (var entry in directories.Entries(folder))
        {
            budget.Visit();
            yield return new(entry.Name, Path.Combine(folder, entry.Name), entry.Attributes);
        }
    }

    /// <summary>Removes a journal and its staging folder, renaming the journal first so a partial deletion never leaves a journal that looks interrupted.</summary>
    private void Discard(DirectoryLease directories, string id)
    {
        CheckJournalPaths(id);
        string journal = JournalPath(id);
        TryDelete(directories, StagingPath(id));
        if (!ExistingDirectory(directories, journal)) return;
        string removed = journal + RemovedSuffix;
        try { directories.Hold(journal); directories.MoveDirectory(journal, removed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        TryDelete(directories, removed);
    }

    private void CheckWorkingPath(string path) => SourceProject.RejectNestedLinks(root, SourceProject.Relative(root, path));
    private void CheckJournalPaths(string id)
    {
        string journal = JournalPath(id);
        foreach (string name in new[] { AfterFolder, HeldFolder, TakenFolder, ManifestName, EventsName })
            CheckWorkingPath(Path.Combine(journal, name));
        CheckWorkingPath(StagingPath(id));
    }

    /// <summary>
    /// Removes what finished saves left behind (under the lock): journals already renamed for removal, committed or rolled-back
    /// journals, journals that never got a manifest and hold no original, and staging folders without a journal.
    /// </summary>
    private void Tidy(DirectoryLease directories, CancellationToken token)
    {
        string recovery = SourceProject.Resolve(root, RecoveryFolder);
        foreach (var entry in WorkingEntries(directories, recovery, WorkingBudget(RecoveryFolder, token)))
        {
            if (!entry.IsDirectory || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var directory = entry;
            if (directory.Name.EndsWith(RemovedSuffix, StringComparison.Ordinal) && IsSaveId(directory.Name[..^RemovedSuffix.Length])) TryDelete(directories, directory.FullName);
            else if (IsSaveId(directory.Name) && !Exists(directories, Path.Combine(directory.FullName, ManifestName))
                && !HasFiles(directories, Path.Combine(directory.FullName, HeldFolder)) && !HasFiles(directories, Path.Combine(directory.FullName, TakenFolder))) TryDelete(directories, directory.FullName);
        }
        foreach (var journal in Journals(directories, token))
            if (journal.Committed || journal.RolledBack && !HasFiles(directories, Path.Combine(JournalPath(journal.Id), HeldFolder))) Discard(directories, journal.Id);
        string staging = SourceProject.Resolve(root, StagingFolder);
        if (ExistingDirectory(directories, staging)) // Lock() refused links on the way.
            foreach (var entry in WorkingEntries(directories, staging, WorkingBudget(StagingFolder, token)))
                if (entry.IsDirectory && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsSaveId(entry.Name) && !ExistingDirectory(directories, JournalPath(entry.Name))) TryDelete(directories, entry.FullName);
    }
    /// <summary>
    /// A listing of one of zStudio's own folders (<paramref name="folder"/>: recovery or staging), which holds a folder for each
    /// save at most: entries other programs put there count too, and <paramref name="token"/> is observed at each.
    /// </summary>
    private SourceProject.ScanBudget WorkingBudget(string folder, CancellationToken token) => new(ScanLimit, maximum => new IOException(
        $"{folder} holds more than {maximum:N0} files and folders, where zStudio keeps a folder for each unfinished save. Move what other programs put there out of it, then try again."), token);
}
