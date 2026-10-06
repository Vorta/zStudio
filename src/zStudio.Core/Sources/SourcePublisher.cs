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
    public const int MaximumDescriptionLength = 1024;
    private const string ManifestName = "manifest.json", EventsName = "events.log", LockName = ".lock", RemovedSuffix = ".removed";
    private const string AfterFolder = "after", HeldFolder = "held", TakenFolder = "removed";
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();
    private readonly string root;

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
        ArgumentNullException.ThrowIfNull(writes); ArgumentNullException.ThrowIfNull(description);
        if (description.Length > MaximumDescriptionLength) throw new ArgumentException($"The save description is longer than {MaximumDescriptionLength} characters.", nameof(description));
        var (changes, checks) = Plan(writes, token);
        token.ThrowIfCancellationRequested();
        using FileStream gate = Lock();
        Tidy();
        if (Journals().FirstOrDefault(j => !j.Committed && !j.RolledBack) is { } pending) throw Blocked(pending);
        string[] conflicts = [.. changes.Concat(checks).OrderBy(t => t.Order).Where(t => !Look(t.Path).Is(t.Expected)).Select(t => t.Name)];
        if (conflicts.Length > 0) throw new SourceConflictException($"{string.Join(", ", conflicts)} changed on disk since {(conflicts.Length == 1 ? "it was" : "they were")} read, or cannot be read now; nothing was saved. Reload to continue from the files on disk.", conflicts);
        foreach (var change in changes)
            if (new FileInfo(change.Path) is { Exists: true } file && file.Attributes.HasFlag(FileAttributes.ReadOnly)) throw new UnauthorizedAccessException($"{change.Name} is read-only; nothing was saved.");

        string id = NewSaveId();
        JournalManifest manifest = new(JournalFormat, id, description, DateTime.UtcNow, [.. changes.Select(c => new JournalFile(c.Relative, c.Expected, c.Content))], NewFolders(changes));
        EventLog log = Prepare(manifest, changes, token);
        try { Install(manifest, changes, log); }
        finally { log.Dispose(); }
        // The save is committed: a failure to clean up only leaves a committed journal that the next save removes.
        try { Step("cleanup", -1); Discard(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(id, [.. changes.Select(c => c.Name)]);
    }

    /// <summary>Writes the journal (new contents, then the manifest, then "prepared") and stages every new content; on failure removes them again.</summary>
    private EventLog Prepare(JournalManifest manifest, Target[] changes, CancellationToken token)
    {
        string journal = JournalPath(manifest.SaveId), staging = StagingPath(manifest.SaveId); EventLog? log = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(journal, AfterFolder));
            for (int i = 0; i < changes.Length; i++)
                if (changes[i].Bytes is { } bytes) { token.ThrowIfCancellationRequested(); Step("after", i); WriteDurable(AfterPath(journal, i), bytes); }
            Step("manifest", -1); WriteManifest(journal, manifest);
            log = EventLog.Open(journal, manifest.Files.Count);
            Step("prepared", -1); log.Append("prepared", -1);
            SourceProject.RejectNestedLinks(root, StagingFolder);
            Directory.CreateDirectory(staging);
            for (int i = 0; i < changes.Length; i++)
                if (changes[i].Bytes is { } bytes) { token.ThrowIfCancellationRequested(); Step("stage", i); WriteVerified(StagedPath(staging, i), bytes); }
            token.ThrowIfCancellationRequested();
            return log;
        }
        catch (Crash) { log?.Dispose(); throw; }
        catch
        {
            // Nothing in the project changed yet.
            log?.TryAppend("rolled-back"); log?.Dispose(); Discard(manifest.SaveId);
            throw;
        }
    }

    /// <summary>Moves each original aside and each new file into place, then commits; a failure undoes what was done, in reverse.</summary>
    private void Install(JournalManifest manifest, Target[] changes, EventLog log)
    {
        string journal = JournalPath(manifest.SaveId), staging = StagingPath(manifest.SaveId);
        bool[] moved = new bool[changes.Length], installed = new bool[changes.Length];
        int reached = -1; bool committing = false;
        try
        {
            for (int i = 0; i < changes.Length; i++)
            {
                Target file = changes[i]; reached = i;
                Step("intent", i); log.Append("intent", i);
                // The staged copy is checked against the journaled content before the original moves aside, and held until it
                // is in place: no other program can write or rename it meanwhile, so the file installed is the one journaled.
                using SealedFile? staged = file.Content is { } content ? Seal(StagedPath(staging, i), content, file) : null;
                Step("hold", i);
                SourceProject.RejectNestedLinks(root, file.Relative);
                if (file.Expected is { } expected)
                {
                    var result = MoveIfContent(file.Path, HeldPath(journal, i), expected);
                    moved[i] = result != Moved.Unchanged;
                    if (result != Moved.Done) throw ChangedDuringSave(file);
                    log.Append("held", i);
                }
                else if (Path.Exists(file.Path)) throw CreatedDuringSave(file);
                if (staged == null) continue;
                Step("install", i);
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                try { staged.MoveTo(file.Path); }
                catch (IOException ex) when (Path.Exists(file.Path)) { throw CreatedDuringSave(file, ex); }
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
                if (Undo(journal, manifest.Files[i], i, log, moved[i], installed[i]).Conflict is { } conflict) left.Add(conflict with { Relative = changes[i].Name });
            RemoveFolders(manifest.Folders);
            if (left.Count > 0) throw Unfinished(manifest.SaveId, [.. left.Select(c => c.Relative)], ex, left);
            log.TryAppend("rolled-back"); log.Dispose(); Discard(manifest.SaveId);
            throw;
        }
    }

    /// <summary>Holds a staged copy while it has <paramref name="content"/> (see <see cref="SealedFile"/>).</summary>
    private static SealedFile Seal(string staged, JournalDigest content, Target file)
    {
        try { return SealedFile.Open(staged, content); }
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
    private (Target[] Changes, Target[] Checks) Plan(IReadOnlyList<SourceFileWrite> writes, CancellationToken token)
    {
        if (writes.Count == 0) throw new ArgumentException("A save needs at least one file.", nameof(writes));
        if (writes.Count > SourceProject.MaximumFiles) throw new ArgumentException($"A save can include at most {SourceProject.MaximumFiles:N0} files.", nameof(writes));
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase); List<Target> changes = [], checks = [];
        Dictionary<string, Dictionary<string, FileSystemInfo>> listings = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < writes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            SourceFileWrite write = writes[i] ?? throw new ArgumentException($"Save entry {i} is missing.", nameof(writes));
            ArgumentNullException.ThrowIfNull(write.Relative, nameof(writes));
            string relative = Canonical(write.Relative, listings);
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
    private string Canonical(string relative, Dictionary<string, Dictionary<string, FileSystemInfo>> listings)
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
                    foreach (var info in new DirectoryInfo(current).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }))
                        if (entries.TryAdd(info.Name, info) && entries.Count > SourceProject.MaximumFiles) throw new IOException($"{current} has more than {SourceProject.MaximumFiles:N0} entries.");
                    listings[current] = entries;
                }
                if (entries.TryGetValue(parts[i], out var entry))
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{entry.FullName} is a link; nothing was written through it.");
                    if (i < parts.Length - 1 && entry is not DirectoryInfo) throw new InvalidDataException($"'{relative}' uses the file {entry.FullName} as a folder.");
                    parts[i] = entry.Name; next = Path.Combine(current, entry.Name);
                }
                else if (Path.Exists(next)) throw new InvalidDataException($"'{relative}' names an existing entry by another spelling (such as a short name); use its full name.");
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
    private string[] NewFolders(IEnumerable<Target> changes)
    {
        List<string> folders = []; HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes.Where(c => c.Bytes != null))
        {
            string[] parts = change.Relative.Split('/');
            for (int n = 2; n < parts.Length; n++)
            {
                string folder = string.Join('/', parts[..n]);
                if (seen.Add(folder) && !Directory.Exists(SourceProject.Resolve(root, folder))) folders.Add(folder);
            }
        }
        return [.. folders];
    }

    private void RemoveFolders(IReadOnlyList<string> folders)
    {
        foreach (string folder in folders.OrderByDescending(f => f.Count(c => c == '/')).ThenByDescending(f => f, StringComparer.Ordinal))
        {
            try
            {
                SourceProject.RejectNestedLinks(root, folder);
                DirectoryInfo directory = new(SourceProject.Resolve(root, folder));
                if (directory.Exists && !directory.EnumerateFileSystemInfos().Any()) directory.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The project-wide publication lock; saves and recoveries in every process take it for their whole duration.</summary>
    private FileStream Lock()
    {
        // Working data is deleted recursively, so neither folder may lead elsewhere; nor may the project itself, which can
        // have become a link since this publisher was made.
        SourceProject.RejectLinkedProject(root);
        SourceProject.RejectNestedLinks(root, RecoveryFolder); SourceProject.RejectNestedLinks(root, StagingFolder);
        string folder = SourceProject.Resolve(root, RecoveryFolder);
        if (PickupPlacementEditSession.IsProtectedPath(folder)) throw new IOException("Source projects inside the protected zbd_1998/zbd_1999 folders cannot be saved.");
        Directory.CreateDirectory(folder);
        try { return new FileStream(Path.Combine(folder, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
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
    /// <summary>What a path holds now: nothing, a regular file with this digest, or something else (a folder, a link, an unreadable file).</summary>
    private readonly record struct Probe(Presence Kind, JournalDigest? Digest)
    {
        public bool Is(JournalDigest? expected) => expected == null ? Kind == Presence.Absent : Kind == Presence.File && Digest == expected;
    }
    private static Probe Look(string path)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists) return Path.Exists(path) ? new(Presence.Other, null) : new(Presence.Absent, null);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return new(Presence.Other, null);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new(Presence.File, JournalDigest.Of(stream));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(Presence.Absent, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(Presence.Other, null); }
    }
    /// <summary><see cref="Look"/> for a journal path, treating a link that appeared on the way as an unexpected entry.</summary>
    private Probe LookAt(string relative)
    {
        try { SourceProject.RejectNestedLinks(root, relative); }
        catch (IOException) { return new(Presence.Other, null); }
        return Look(SourceProject.Resolve(root, relative));
    }

    internal enum Moved { Done, Unchanged, Stranded }
    /// <summary>
    /// Moves a file only while it has the expected content: other writers are excluded while it is compared, and the moved
    /// file is checked again in case another program renamed something over the name in between. A file that turns out to
    /// differ is moved back while the name is free (<see cref="Moved.Unchanged"/>); otherwise it stays at the destination
    /// (<see cref="Moved.Stranded"/>). Exports and reconstructions undo their own files with it too: moved to a new name
    /// on the same volume and deleted there, a file is removed only while it still has the content the run wrote.
    /// </summary>
    internal static Moved MoveIfContent(string path, string destination, JournalDigest expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            FileInfo info = new(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Moved.Unchanged;
            using FileStream guard = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (JournalDigest.Of(guard) != expected) return Moved.Unchanged;
            File.Move(path, destination, false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return Moved.Unchanged; }
        if (Look(destination).Is(expected)) return Moved.Done;
        try { File.Move(destination, path, false); return Moved.Unchanged; }
        catch (IOException) { return Moved.Stranded; }
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
    /// <summary>Writes, flushes and reads a file back before it may be published.</summary>
    private static void WriteVerified(string path, byte[] bytes)
    {
        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        if (!SourceProject.FileEquals(path, bytes)) throw new IOException($"{path} did not read back as it was written; nothing was saved.");
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static bool HasFiles(string folder) => Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();

    /// <summary>Removes a journal and its staging folder, renaming the journal first so a partial deletion never leaves a journal that looks interrupted.</summary>
    private void Discard(string id)
    {
        string journal = JournalPath(id);
        TryDelete(StagingPath(id));
        if (!Directory.Exists(journal)) return;
        string removed = journal + RemovedSuffix;
        try { Directory.Move(journal, removed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        TryDelete(removed);
    }

    /// <summary>
    /// Removes what finished saves left behind (under the lock): journals already renamed for removal, committed or rolled-back
    /// journals, journals that never got a manifest and hold no original, and staging folders without a journal.
    /// </summary>
    private void Tidy()
    {
        string recovery = SourceProject.Resolve(root, RecoveryFolder);
        foreach (var directory in new DirectoryInfo(recovery).EnumerateDirectories())
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (directory.Name.EndsWith(RemovedSuffix, StringComparison.Ordinal) && IsSaveId(directory.Name[..^RemovedSuffix.Length])) TryDelete(directory.FullName);
            else if (IsSaveId(directory.Name) && !File.Exists(Path.Combine(directory.FullName, ManifestName))
                && !HasFiles(Path.Combine(directory.FullName, HeldFolder)) && !HasFiles(Path.Combine(directory.FullName, TakenFolder))) TryDelete(directory.FullName);
        }
        foreach (var journal in Journals())
            if (journal.Committed || journal.RolledBack && !HasFiles(Path.Combine(JournalPath(journal.Id), HeldFolder))) Discard(journal.Id);
        string staging = SourceProject.Resolve(root, StagingFolder);
        if (Directory.Exists(staging)) // Lock() refused links on the way.
            foreach (var directory in new DirectoryInfo(staging).EnumerateDirectories())
                if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsSaveId(directory.Name) && !Directory.Exists(JournalPath(directory.Name))) TryDelete(directory.FullName);
    }
}
