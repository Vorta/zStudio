using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recoil.Zbd.Core.Sources;

/// <summary>How to resolve an interrupted save.</summary>
public enum SourceRecoveryAction
{
    /// <summary>Undo what the save provably wrote: take back its new files while they are unchanged and put the originals back while their names are free.</summary>
    RollBack,
    /// <summary>Finish the save from its journal, replacing only files that still have their expected content.</summary>
    Complete,
    /// <summary>Keep the files as they are and retire the journal; originals it still holds move to <c>zstudio/recovery/abandoned</c>.</summary>
    Abandon,
}

/// <summary>A target's current content compared with its save journal.</summary>
public enum SourceRecoveryFileState
{
    /// <summary>The content it had before the save (absent for a file the save creates).</summary>
    Before,
    /// <summary>The content the save writes (absent for a file the save deletes).</summary>
    After,
    /// <summary>Absent although both the previous and the new content exist.</summary>
    Missing,
    /// <summary>Something else: content from another program, a folder, a link or an unreadable file.</summary>
    Other,
}

/// <summary>One file of an interrupted save and whether the journal holds the original it moved aside.</summary>
public sealed record SourceRecoveryFile(string Relative, SourceRecoveryFileState State, bool HeldOriginal);
/// <summary>A save journal without a completed outcome. A committed one is final and only its cleanup remains.</summary>
public sealed record SourceRecoveryCase(string SaveId, string Description, DateTime CreatedUtc, bool Committed, IReadOnlyList<SourceRecoveryFile> Files);
/// <summary>A file a resolution could not finish safely, and why. An earlier step may already have changed it.</summary>
public sealed record SourceRecoveryConflict(string Relative, string Reason);
/// <summary>
/// The outcome of <see cref="SourcePublisher.Resolve"/>: the files whose content it changed (originals put back, new files
/// installed or files deleted, including originals moved into the journal before a failed installation), the files that
/// still need a decision, and whether the journal was retired. Each changed path is listed once per invocation.
/// </summary>
public sealed record SourceRecoveryResult(IReadOnlyList<string> Changed, IReadOnlyList<SourceRecoveryConflict> Conflicts, bool Resolved);

/// <summary>A content's length and lowercase SHA-256, as a journal records it.</summary>
internal sealed record JournalDigest(long Length, string Sha256)
{
    public static JournalDigest? Of(byte[]? bytes) => bytes == null ? null : new(bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    /// <summary>
    /// The digest of a stream's content from its position to its end, read in blocks of 1 MiB with <paramref name="token"/>
    /// observed before each, so a large file can be given up part-way.
    /// </summary>
    public static JournalDigest Of(Stream stream, CancellationToken token = default)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] block = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long length = 0;
            for (int read; ; length += read)
            {
                token.ThrowIfCancellationRequested();
                if ((read = stream.Read(block)) == 0) break;
                hash.AppendData(block, 0, read);
            }
            return new(length, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(block); }
    }
    /// <summary>
    /// Whether a stream (at its start) has this content. A stream of another length differs without being read, so a file
    /// another program replaced with gigabytes is not hashed to find that out.
    /// </summary>
    public bool Matches(Stream stream, CancellationToken token = default) => stream.Length == Length && Of(stream, token) == this;
    /// <summary>The digest of bytes held in memory (a document's or a built output's), without copying them.</summary>
    public static JournalDigest OfContent(ReadOnlySpan<byte> bytes) => new(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
}
/// <summary>One journaled file: the canonical project-relative path and its previous and new content (null: absent).</summary>
internal sealed record JournalFile(string Relative, JournalDigest? Expected, JournalDigest? Content);
/// <summary><c>manifest.json</c>: what a save intends, written completely before any source changes.</summary>
internal sealed record JournalManifest(int Format, string SaveId, string Description, DateTime CreatedUtc, IReadOnlyList<JournalFile> Files, IReadOnlyList<string> Folders);

public sealed partial class SourcePublisher
{
    private const int JournalFormat = 1, MaximumJournals = 1024;
    private const long MaximumManifestBytes = 64L * 1024 * 1024;
    internal int JournalFoldersLimit { get; init; } = SourceProject.MaximumScannedEntries;
    internal long ManifestBytesLimit { get; init; } = MaximumManifestBytes;
    internal long RecoveryBytesLimit { get; init; } = MaximumManifestBytes;
    internal int RecoveryFilesLimit { get; init; } = SourceProject.MaximumFiles;
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    };

    private readonly record struct JournalEvent(string Step, int Index);
    private sealed class Journal(JournalManifest manifest, IReadOnlyList<JournalEvent> events, long workingBytes)
    {
        public long WorkingBytes { get; } = workingBytes;
        private readonly HashSet<int> intended = [.. events.Where(e => e.Step == "intent").Select(e => e.Index)];
        public JournalManifest Manifest { get; } = manifest;
        public string Id => Manifest.SaveId;
        /// <summary>A commit record counts unless a later rollback record voids it.</summary>
        public bool Committed { get; } = events.Aggregate(false, (committed, e) => e.Step == "committed" || committed && e.Step != "rollback");
        public bool RolledBack { get; } = events.Any(e => e.Step == "rolled-back");
        public bool Intended(int index) => intended.Contains(index);
    }

    /// <summary>
    /// Save journals without a completed outcome, oldest first, with each target's current state. Committed journals are
    /// included (their files are final; only cleanup remains). Takes the publication lock briefly; nothing is changed.
    /// </summary>
    public IReadOnlyList<SourceRecoveryCase> FindInterrupted(CancellationToken token = default)
    {
        using DirectoryLease directories = new(); directories.Hold(root);
        if (!ExistingDirectory(directories, SourceProject.Resolve(root, RecoveryFolder))) return [];

        using FileStream gate = Lock(directories);
        List<SourceRecoveryCase> cases = [];
        foreach (var journal in Journals(directories, token))
        {
            token.ThrowIfCancellationRequested();
            if (journal.RolledBack) continue;
            string folder = JournalPath(journal.Id);
            // A state reads its file whole when its length is the journal's, and a journal may list 50,000 files.
            var files = journal.Manifest.Files.Select((f, i) => { token.ThrowIfCancellationRequested(); return new SourceRecoveryFile(f.Relative, State(directories, f, token), Exists(directories, HeldPath(folder, i))); }).ToArray();
            DateTime created = journal.Manifest.CreatedUtc.Kind == DateTimeKind.Local ? journal.Manifest.CreatedUtc.ToUniversalTime() : DateTime.SpecifyKind(journal.Manifest.CreatedUtc, DateTimeKind.Utc);
            cases.Add(new(journal.Id, journal.Manifest.Description, created, journal.Committed, files));
        }
        return cases;
    }

    /// <summary>
    /// The files an interrupted save involves, as its journal lists them (none when there is no such save, or when it was
    /// rolled back and only its journal remains). Unlike <see cref="FindInterrupted"/>, no file is read. Takes the
    /// publication lock briefly; nothing is changed.
    /// </summary>
    public IReadOnlyList<string> SaveFiles(string saveId)
    {
        using DirectoryLease directories = new(); directories.Hold(root);
        if (!IsSaveId(saveId) || !ExistingDirectory(directories, SourceProject.Resolve(root, RecoveryFolder))) return [];

        using FileStream gate = Lock(directories);
        if (!Exists(directories, Path.Combine(JournalPath(saveId), ManifestName))) return [];
        Journal journal = Load(directories, saveId);
        return journal.RolledBack ? [] : [.. journal.Manifest.Files.Select(f => f.Relative)];
    }

    private SourceRecoveryFileState State(DirectoryLease directories, JournalFile file, CancellationToken token)
    {
        Probe probe = LookAt(directories, file.Relative, token, file.Expected, file.Content);
        return probe.Is(file.Expected) ? SourceRecoveryFileState.Before : probe.Is(file.Content) ? SourceRecoveryFileState.After
            : probe.Kind == Presence.Absent ? SourceRecoveryFileState.Missing : SourceRecoveryFileState.Other;
    }

    /// <summary>
    /// Resolves an interrupted save under the publication lock. Every file is re-checked immediately before it is touched,
    /// nothing is overwritten and nothing that went missing is recreated; files that would need either are reported as
    /// conflicts and the journal stays until they are settled (or the save is abandoned). A committed save cannot be rolled
    /// back; completing or abandoning it only retires its journal.
    /// </summary>
    /// <remarks>
    /// <paramref name="token"/> is observed between files, never while one is being changed; it also stops the reading of a
    /// file that decides what to do (a completion's check of every file before any changes, a rollback's check of the file it
    /// reaches). A file is read only when its length is one the journal records. Canceled before any file
    /// changed, it throws <see cref="OperationCanceledException"/> with no file changed; canceled later, the resolution stops
    /// before the next file, as an interruption would, and throws one whose message names the files it changed (its inner
    /// exception is the cancellation). Either way the journal records exactly what was done, the save still needs a
    /// decision, and any action resolves it from there.
    /// </remarks>
    public SourceRecoveryResult Resolve(string saveId, SourceRecoveryAction action, CancellationToken token = default)
    {
        using DirectoryLease directories = new(); directories.Hold(root);
        if (!IsSaveId(saveId)) throw new ArgumentException($"'{JsonData.ShownText(saveId ?? "", 128)}' is not a save identity.", nameof(saveId));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        token.ThrowIfCancellationRequested();

        using FileStream gate = Lock(directories);
        if (!Exists(directories, Path.Combine(JournalPath(saveId), ManifestName))) throw new FileNotFoundException($"There is no save journal {saveId} in {RecoveryFolder}.");
        Journal journal = Load(directories, saveId, token: token);
        token.ThrowIfCancellationRequested();
        if (journal.RolledBack) return HasFiles(directories, Path.Combine(JournalPath(saveId), HeldFolder)) ? Abandon(directories, journal) : Retire(directories, saveId);
        return action switch
        {
            SourceRecoveryAction.RollBack when journal.Committed => throw new InvalidOperationException($"The save {saveId} was committed: its files are final and only its cleanup remains. Complete or abandon it instead."),
            SourceRecoveryAction.RollBack => RollBack(directories, journal, token),
            SourceRecoveryAction.Complete when journal.Committed => Retire(directories, saveId),
            SourceRecoveryAction.Complete => Complete(directories, journal, token),
            _ => Abandon(directories, journal),
        };
    }

    private SourceRecoveryResult RollBack(DirectoryLease directories, Journal journal, CancellationToken token)
    {
        string folder = JournalPath(journal.Id); var files = journal.Manifest.Files;
        List<string> changed = []; List<SourceRecoveryConflict> conflicts = [];
        using (EventLog log = EventLog.Open(directories, folder, files.Count))
        {
            log.Append("rollback", -1);
            for (int i = files.Count - 1; i >= 0; i--)
            {
                if (token.IsCancellationRequested) throw Canceled(journal.Id, changed, conflicts, token);
                JournalFile file = files[i];
                bool moved = Exists(directories, HeldPath(folder, i));
                // Files the save never reached (no intent is recorded before a file is touched) keep whatever they have.
                if (!journal.Intended(i) && !moved) continue;
                // Reading a large file to compare it gives way to the cancellation, which then counts as coming between files.
                bool installed;
                try { installed = file.Content != null && LookAt(directories, file.Relative, token, file.Content).Is(file.Content); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw Canceled(journal.Id, changed, conflicts, token); }
                var (done, conflict) = Undo(directories, folder, file, i, log, moved, installed);
                if (done) changed.Add(file.Relative);
                try
                {
                    conflict ??= State(directories, file, token) switch
                    {
                        SourceRecoveryFileState.Before => null,
                        SourceRecoveryFileState.Missing => new(file.Relative, "is missing and its original is not in the save journal; it was not recreated"),
                        _ => new(file.Relative, "was changed by another program during the interrupted save; it was left as it is"),
                    };
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw Canceled(journal.Id, changed, conflicts, token); }
                if (conflict != null) conflicts.Add(conflict);
            }
            changed.Reverse(); conflicts.Reverse();
            // Only empty folders go, so this is safe beside a conflict.
            RemoveFolders(directories, journal.Manifest.Folders);
            if (conflicts.Count > 0) return new(changed, conflicts, false);
            log.Append("rolled-back", -1);
        }
        return Retire(directories, journal.Id, changed);
    }

    /// <summary>
    /// Undoes what a save did to one file: takes back the new content it installed (only while that content is unchanged),
    /// then returns the original it moved aside (only while the name is free).
    /// </summary>
    private (bool Changed, SourceRecoveryConflict? Conflict) Undo(DirectoryLease directories, string journal, JournalFile file, int index, EventLog log, bool moved, bool installed)
    {
        string path = SourceProject.Resolve(root, file.Relative), held = HeldPath(journal, index); bool changed = false;
        try
        {
            // A previous rollback may already have removed a newly-created file and its now-empty parent. There is
            // nothing to reopen or recreate in that case; the caller still checks the current state for conflicts.
            if (!moved && !installed) { Step("undo", index); return (false, null); }
            directories.Parent(path, create: moved);
            if (moved) directories.Parent(held); else directories.Hold(journal);
            Step("undo", index);
            SourceProject.RejectNestedLinks(root, file.Relative);
            CheckWorkingPath(held); CheckWorkingPath(Path.Combine(journal, TakenFolder));
            if (moved && file.Expected == null)
                return (false, new(file.Relative, "the save journal holds an unexpected original; nothing was replaced"));
            // Validate and hold the original BEFORE taking back the valid installed file. A damaged or replaced
            // recovery copy must not destroy the only good version, and cannot change between verification and rename.
            using SealedFile? original = moved ? SealedFile.Open(held, file.Expected!, directories) : null;
            if (installed && file.Content is { } content)
            {
                if (file.Expected != null && !moved) return (false, new(file.Relative, "has the saved content but its original is no longer in the save journal, so it was left as it is"));
                string taken = TakenPath(journal, index);
                switch (MoveIfContent(path, taken, content, directories))
                {
                    case Moved.Done: changed = true; log.Append("removed", index); break;
                    case Moved.Stranded: return (true, new(file.Relative, $"was replaced by another program while the save was undone; that file was kept as {Display(taken)}"));
                    default:
                        if (Exists(directories, path)) return (false, new(file.Relative, "was changed by another program after the save wrote it, so it was left as it is" + (moved ? $"; its original remains as {Display(held)}" : "")));
                        break;
                }
            }
            if (moved)
            {
                if (Exists(directories, path))
                {
                    // Another program already put the original content back: the journal's copy is a duplicate. Not
                    // canceled part-way, as the file may have changed above; only a file of the original's length is read.
                    if (Look(directories, path, default, file.Expected).Is(file.Expected)) return (changed, null);
                    return (changed, new(file.Relative, $"holds content from another program, so its original was not put back; it remains as {Display(held)}"));
                }
                original!.MoveTo(path); changed = true; log.Append("restored", index);
            }
            return (changed, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return (changed, new(file.Relative, $"could not be restored: {JsonData.ShownText(ex.Message, 512)}")); }
    }

    private SourceRecoveryResult Complete(DirectoryLease directories, Journal journal, CancellationToken token)
    {
        string folder = JournalPath(journal.Id), staging = StagingPath(journal.Id); var files = journal.Manifest.Files;
        // Decide first: nothing changes unless every file can be finished.
        List<SourceRecoveryConflict> conflicts = [];
        // Admission is specific to completing an uncommitted save. Keep even unsupported journals readable so
        // inspection, rollback and abandon can preserve/restore originals, including files already moved aside.
        // Check the whole batch before Blocker hashes any body or completion changes its first source file.
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (file.Content is { Length: > Formats.FormatRegistry.MaximumDocumentBytes })
                conflicts.Add(new(file.Relative, "cannot be completed because its new content exceeds the 512 MiB source-file limit; roll back or abandon the save instead"));
        }
        if (conflicts.Count > 0) return new([], conflicts, false);
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (Blocker(directories, folder, files[i], i, token) is { } blocker) conflicts.Add(blocker);
        }
        if (conflicts.Count > 0) return new([], conflicts, false);
        token.ThrowIfCancellationRequested();
        List<string> changed = [];
        using (EventLog log = EventLog.Open(directories, folder, files.Count))
        {
            for (int i = 0; i < files.Count; i++)
            {
                if (token.IsCancellationRequested) throw Canceled(journal.Id, changed, conflicts, token);
                JournalFile file = files[i]; string path = SourceProject.Resolve(root, file.Relative), held = HeldPath(folder, i);
                bool fileChanged = false;
                try
                {

                    try { directories.Parent(path, create: file.Content != null); }
                    catch (Exception ex) when (file.Content == null && ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        // A completed deletion can also have lost its now-empty parent. The probe after the fault hook
                        // still verifies the requested absence; recovery neither recreates the parent nor declares a conflict.
                    }
                   directories.Hold(folder);
                   directories.Parent(held, create: true);
                   directories.Hold(staging, create: true);
                    Step("complete", i);
                    SourceProject.RejectNestedLinks(root, file.Relative);
                    CheckJournalPaths(journal.Id); CheckWorkingPath(held);
                    // A file whose turn has come is completed, as a cancellation is observed between files; the check above
                    // already read it (giving way to the cancellation), and only a file of a journaled length is read again.
                    Probe probe = Look(directories, path, default, file.Content, file.Expected);
                    if (probe.Is(file.Content)) continue;
                    log.Append("intent", i);
                    if (file.Expected is { } expected && probe.Is(expected))
                    {
                        var outcome = MoveIfContent(path, held, expected, directories);
                        // Removing the source is a change even if staging, installation or journal logging fails next.
                        fileChanged = outcome is Moved.Done or Moved.Stranded;
                        if (outcome != Moved.Done)
                        {
                            conflicts.Add(new(file.Relative, outcome == Moved.Stranded ? $"was replaced by another program while the save was being completed; the replaced file was kept as {Display(held)}" : "changed while the save was being completed; it was left as it is"));
                            continue;
                        }
                        log.Append("held", i);
                    }
                    else if (!probe.Is(file.Expected) && !(probe.Kind == Presence.Absent && Look(directories, held, default, file.Expected).Is(file.Expected)))
                    { conflicts.Add(new(file.Relative, "changed while the save was being completed; it was left as it is")); continue; }
                    if (file.Content is { } content)
                    {
                        string temporary = StagedPath(staging, i);
                        SourceProject.RejectNestedLinks(root, StagingFolder); directories.Hold(staging, create: true);
                        WriteVerified(directories, temporary, NewContent(directories, folder, i, content));
                       directories.Parent(path, create: true);
                        // Held from its check against the journaled content until it is in place (see SealedFile).
                        using (SealedFile staged = SealedFile.Open(temporary, content, directories))
                        {
                            try { staged.MoveTo(path); fileChanged = true; }
                            catch (IOException) when (Exists(directories, path)) { conflicts.Add(new(file.Relative, "was created by another program while the save was being completed; it was not replaced")); continue; }
                        }
                        log.Append("installed", i);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { conflicts.Add(new(file.Relative, $"could not be completed: {JsonData.ShownText(ex.Message, 512)}")); }
                finally
                {
                    // A replacement can move twice; the result names each affected source once, including conflicts.
                    if (fileChanged) changed.Add(file.Relative);
                }
            }
            if (conflicts.Count > 0) return new(changed, conflicts, false);
            log.Append("committed", -1);
        }
        return Retire(directories, journal.Id, changed);
    }

    /// <summary>
    /// A resolution canceled between two files. With nothing changed, a plain cancellation; otherwise one that says which
    /// files changed, since the journal (which recorded each step) leaves the save to be resolved again.
    /// </summary>
    internal static OperationCanceledException Canceled(string id, IReadOnlyCollection<string> changed, IReadOnlyCollection<SourceRecoveryConflict> conflicts, CancellationToken token)
    {
        if (changed.Count == 0 && conflicts.Count == 0) return new(token);
        // Files it could not resolve before the cancellation are named too: their reasons are not lost.
        string done = changed.Count == 0 ? "" : $" after it changed {changed.Count:N0} file{(changed.Count == 1 ? "" : "s")} ({ShownFiles(changed)})";
        string failed = conflicts.Count == 0 ? "" : $"{(done.Length == 0 ? " after" : ", and after")} {conflicts.Count:N0} file{(conflicts.Count == 1 ? "" : "s")} could not be resolved ({ShownConflicts(conflicts)})";
        return new($"Resolving the interrupted save {id} was canceled{done}{failed}; its journal records each step, and the save still needs a decision.", new OperationCanceledException(token), token);
    }

    /// <summary>Why one file of a save cannot be completed, or null when it can.</summary>
    private SourceRecoveryConflict? Blocker(DirectoryLease directories, string journal, JournalFile file, int index, CancellationToken token)
    {
        Probe probe = LookAt(directories, file.Relative, token, file.Content, file.Expected); string held = HeldPath(journal, index);
        if (probe.Is(file.Content)) return null;
        if (file.Content is { } content && !Look(directories, AfterPath(journal, index), token, content).Is(content)) return new(file.Relative, "cannot be completed because the save journal's copy of its new content is missing or damaged");
        if (probe.Is(file.Expected))
            return file.Expected != null && Exists(directories, held) ? new(file.Relative, $"has its original content while the save journal also holds an original ({Display(held)}); it was left as it is") : null;
        if (probe.Kind == Presence.Absent)
            return !Exists(directories, held) ? new(file.Relative, "is missing and its original is not in the save journal; it was not recreated")
                : Look(directories, held, token, file.Expected).Is(file.Expected) ? null : new(file.Relative, $"cannot be completed because the original the save journal holds ({Display(held)}) differs from the one the save expected");
        return new(file.Relative, "was changed by another program; it was left as it is");
    }

    /// <summary>The journal's copy of a new content, read only when it has the journaled length (it may have been replaced since it was checked).</summary>
    private byte[] NewContent(DirectoryLease directories, string journal, int index, JournalDigest content)
    {
        const string Damaged = "the save journal's copy of its new content is damaged.";
        if (content.Length > Formats.FormatRegistry.MaximumDocumentBytes)
            throw new InvalidDataException("its new content exceeds the 512 MiB source-file limit.");
        CheckWorkingPath(AfterPath(journal, index));
        directories.Parent(AfterPath(journal, index));
        using FileStream stream = directories.OpenFile(AfterPath(journal, index), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != content.Length) throw new InvalidDataException(Damaged);
        byte[] bytes = new byte[content.Length]; stream.ReadExactly(bytes);
        if (JournalDigest.Of(bytes) != content) throw new InvalidDataException(Damaged);
        return bytes;
    }

    private SourceRecoveryResult Abandon(DirectoryLease directories, Journal journal)
    {
        string folder = JournalPath(journal.Id);
        // Early interrupted saves may have made a target's parent before moving any original. Retire those empty
        // journal-listed folders too; folders holding saved or external files remain untouched.
        RemoveFolders(directories, journal.Manifest.Folders);
        if (!HasFiles(directories, Path.Combine(folder, HeldFolder)) && !HasFiles(directories, Path.Combine(folder, TakenFolder))) return Retire(directories, journal.Id);
        // Originals (and files taken back from other programs) are kept where the user can find them.
        SourceProject.RejectNestedLinks(root, AbandonedFolder);
        string abandoned = SourceProject.Resolve(root, AbandonedFolder), destination = Path.Combine(abandoned, journal.Id);
        directories.Hold(abandoned, create: true); directories.Hold(folder);
        for (int n = 2; Exists(directories, destination); n++) destination = Path.Combine(abandoned, $"{journal.Id}-{n}");
       directories.MoveDirectory(folder, destination);

        TryDelete(directories, StagingPath(journal.Id), new(CleanupInventoryLimit));
        return new([], [], true);
    }

    /// <summary>Removes a finished journal; a journal that cannot be removed is reported and retried by the next save.</summary>
    private SourceRecoveryResult Retire(DirectoryLease directories, string id, IReadOnlyList<string>? changed = null)
    {
        Discard(directories, id);
        return ExistingDirectory(directories, JournalPath(id))
            ? new(changed ?? [], [new(RecoveryFolder + "/" + id, "could not be removed; close programs that use it and resolve the save again")], false)
            : new(changed ?? [], [], true);
    }

    /// <summary>
    /// Every readable journal in the recovery folder, oldest first; a journal that cannot be read blocks saving. Every entry of
    /// the folder counts towards <see cref="ScanLimit"/>, a journal or not, and <paramref name="token"/> is observed at each.
    /// </summary>
    private List<Journal> Journals(DirectoryLease directories, CancellationToken token, InventoryBudget? inventory = null)
    {
        inventory ??= new(InventoryLimit);
        string recovery = SourceProject.Resolve(root, RecoveryFolder);
        if (!ExistingDirectory(directories, recovery)) return [];
        List<string> ids = [];
        foreach (var entry in WorkingEntries(directories, recovery, WorkingBudget(RecoveryFolder, token, inventory)))
        {
            if (!entry.IsDirectory || !IsSaveId(entry.Name) || entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || !Exists(directories, Path.Combine(entry.FullName, ManifestName))) continue;
            var directory = entry;
            if (ids.Count == MaximumJournals) throw new SourceRecoveryRequiredException($"{RecoveryFolder} holds more than {MaximumJournals} save journals; resolve or move them before saving.", directory.Name, []);
            ids.Add(directory.Name);
        }
        List<Journal> journals = []; long remaining = RecoveryBytesLimit; int files = 0;
        PathWorkBudget pathBudget = new(root.Length, PlanningPathBytesLimit, token);
        foreach (string id in ids.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var journal = Load(directories, id, remaining, token, pathBudget, RecoveryFilesLimit - files);
            inventory.Rows(1L + journal.Manifest.Files.Count + journal.Manifest.Folders.Count, token);
            remaining -= journal.WorkingBytes;
            if ((files += journal.Manifest.Files.Count) > RecoveryFilesLimit)
                throw new SourceRecoveryRequiredException($"The save journals together list more than {RecoveryFilesLimit:N0} files; resolve or move journals before opening recovery again.", id, []);
            journals.Add(journal);
        }
        return journals;
    }

    private Journal Load(DirectoryLease directories, string id, long maximumBytes = MaximumManifestBytes, CancellationToken token = default, PathWorkBudget? pathBudget = null, int maximumFiles = SourceProject.MaximumFiles)
    {
        string folder = JournalPath(id);
        try
        {
            CheckJournalPaths(id);
            JournalManifest manifest = ReadManifest(directories, folder, id, maximumBytes, out long bytes, token, pathBudget, maximumFiles);
            var events = EventLog.Read(directories, folder, manifest.Files.Count, maximumBytes - bytes, out long eventBytes, token);
            return new(manifest, events, bytes + eventBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            throw new SourceRecoveryRequiredException($"The save journal {RecoveryFolder}/{id} cannot be read: {JsonData.ShownText(ex.Message, 512)} Recover any originals from its {HeldFolder} folder, then move the journal out of {RecoveryFolder}.", id, [], ex);
        }
    }

    private byte[] PrepareManifest(JournalManifest manifest, CancellationToken token)
    {
        ValidateManifest(manifest, manifest.SaveId, token);
        // Leave room for every publication and rollback event, including a failed commit followed by recovery.
        long maximum = Math.Min(MaximumManifestBytes, ManifestBytesLimit) - EventLog.ReservedBytes(manifest.Files.Count);
        using ManifestBuffer buffer = new(maximum, token);
        JsonSerializer.Serialize(buffer, manifest, JournalJson);
        return buffer.ToArray();
    }

    private sealed class ManifestBuffer(long maximum, CancellationToken token) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Reserve(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Reserve(buffer.Length); base.Write(buffer); }
        private void Reserve(int count)
        {
            token.ThrowIfCancellationRequested();
            if (count > maximum - Position) throw new InvalidDataException("The save journal exceeds its byte budget after reserving recovery events; split it into smaller saves.");
        }
    }

    private static void WriteManifest(DirectoryLease directories, string journal, byte[] bytes)
    {
        // Written whole under another name first, so a manifest that exists is complete.
        string temporary = Path.Combine(journal, ManifestName + ".tmp");
        directories.Hold(journal);
        WriteDurable(directories, temporary, bytes);
        using SealedFile complete = SealedFile.Open(temporary, JournalDigest.OfContent(bytes), directories);
        complete.MoveTo(Path.Combine(journal, ManifestName));
    }

    private JournalManifest ReadManifest(DirectoryLease directories, string journal, string id, long maximumBytes, out long length, CancellationToken token, PathWorkBudget? pathBudget, int maximumFiles)
    {
        directories.Hold(journal);
        using FileStream stream = directories.OpenFile(Path.Combine(journal, ManifestName), FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = SourceRead.All(stream, Math.Min(MaximumManifestBytes, maximumBytes), "its manifest", token); length = bytes.LongLength;
        return ParseManifest(bytes, id, token, pathBudget, maximumFiles);
    }

    internal void ValidateManifest(JournalManifest manifest, string id, CancellationToken token, PathWorkBudget? pathBudget = null, bool pathsAdmitted = false)
    {
        if (manifest.Format != JournalFormat) throw new InvalidDataException($"its format {manifest.Format} is not supported by this version of zStudio.");
        if (manifest.SaveId != id) throw new InvalidDataException($"its manifest belongs to the save {JsonData.ShownText(manifest.SaveId ?? "", 128)}.");
        if (manifest.Description.Length > MaximumDescriptionLength) throw new InvalidDataException("its description is too long.");
        if (manifest.Files.Count == 0 || manifest.Files.Count > SourceProject.MaximumFiles) throw new InvalidDataException($"it lists {manifest.Files.Count:N0} files.");
        if (manifest.Folders.Count > JournalFoldersLimit) throw new InvalidDataException($"it lists more than {JournalFoldersLimit:N0} folders.");
        // Admit the complete batch before CheckSyntax expands even its first path. Cleanup folders
        // need their own charge: an absent first ancestor makes each folder probe rebuild its chain.
        // Discovery shares this allowance across all journals; direct resolution starts a fresh one.
        if (!pathsAdmitted)
        {
            pathBudget ??= new(root.Length, PlanningPathBytesLimit, token);
            foreach (JournalFile? file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                if (file?.Relative == null) throw new InvalidDataException("it lists an empty file entry or path.");
                pathBudget.Reserve(file.Relative);
            }
            foreach (string? folder in manifest.Folders)
            {
                token.ThrowIfCancellationRequested();
                if (folder == null) throw new InvalidDataException("it lists an empty folder entry.");
                pathBudget.Reserve(folder);
            }
        }
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (JournalFile? file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file == null) throw new InvalidDataException("it lists an empty file entry.");
            CheckSyntax(file.Relative);
            if (!seen.Add(file.Relative)) throw new InvalidDataException($"it lists {JsonData.ShownText(file.Relative, 256)} more than once.");
            if (file.Expected == file.Content || !Valid(file.Expected) || !Valid(file.Content)) throw new InvalidDataException($"its entry for {JsonData.ShownText(file.Relative, 256)} is not a change.");
        }
        // Each folder must hold one of the new files. Sorted once, the new files that start with a folder's path are
        // adjacent, and the first path at or after it is one of them if any is: a binary search per folder instead of a
        // scan of every file (a journal may list 50,000 files and 250,000 folders).
        string[] created = [.. manifest.Files.Where(f => f.Content != null).Select(f => f.Relative).Order(StringComparer.OrdinalIgnoreCase)];
        seen.Clear();
        foreach (string folder in manifest.Folders)
        {
            token.ThrowIfCancellationRequested();
            if (folder == null) throw new InvalidDataException("it lists an empty folder entry.");
            CheckSyntax(folder);
            if (!seen.Add(folder)) throw new InvalidDataException($"it lists the folder {JsonData.ShownText(folder, 256)} more than once.");
            string prefix = folder + "/";
            int at = Array.BinarySearch(created, prefix, StringComparer.OrdinalIgnoreCase);
            if (at < 0) at = ~at;
            if (at == created.Length || !created[at].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"it lists the folder {JsonData.ShownText(folder, 256)}, which holds none of its new files.");
        }
        static bool Valid(JournalDigest? digest) => digest == null || digest.Length >= 0 && digest.Sha256 is { Length: 64 } hash && hash.All(char.IsAsciiHexDigitLower);
    }

    /// <summary>
    /// <c>events.log</c>: one record per line, <c>sequence|step|file index|check</c>, where the check is the first 16 hex digits
    /// of the SHA-256 of the rest. Each record is flushed to disk before the step it announces or after the step it confirms.
    /// A torn last record (from a write that did not finish) is ignored, and cut off before anything is appended.
    /// </summary>
    private sealed class EventLog : IDisposable
    {
        internal static long ReservedBytes(int files) => (6L * files + 8) * 64;
        // ReservedBytes allows 64 bytes including the newline. Even int.MaxValue sequence/index digits,
        // the longest step and the checksum fit; damaged lines must be bounded before decoding or splitting.
        private const int MaximumRecordBytes = 63;
        private const long MaximumBytes = 64L * 1024 * 1024;
        private static readonly string[] Steps = ["prepared", "intent", "held", "installed", "committed", "rollback", "removed", "restored", "rolled-back"];
        private readonly FileStream stream; private int count; private bool broken, committed;
        private readonly HashSet<JournalEvent> recorded;
        private EventLog(FileStream stream, IReadOnlyList<JournalEvent> events)
        {
            this.stream = stream; count = events.Count; recorded = [.. events];
            committed = events.Aggregate(false, (state, e) => e.Step == "committed" || state && e.Step != "rollback");
        }

        public static EventLog Open(DirectoryLease directories, string journal, int files)
        {
            FileStream stream = directories.OpenFile(Path.Combine(journal, EventsName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                var (events, valid) = Parse(Contents(stream), files);
                if (valid != stream.Length) { stream.SetLength(valid); stream.Flush(true); }
                stream.Seek(0, SeekOrigin.End);
                return new(stream, events);
            }
            catch { stream.Dispose(); throw; }
        }

        public static IReadOnlyList<JournalEvent> Read(DirectoryLease directories, string journal, int files, long maximumBytes, out long length, CancellationToken token)
        {
            length = 0;
            string path = Path.Combine(journal, EventsName);
            if (!Exists(directories, path)) return [];

            using FileStream stream = directories.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = Contents(stream, maximumBytes, token); length = bytes.Length;
            return Parse(bytes, files, token).Events;
        }

        public void Append(string step, int index)
        {
            if (broken) throw new IOException("The save journal's event log could not be extended.");
            JournalEvent entry = new(step, index);
            // Recovery depends on whether an intent ever occurred and the last commit/rollback transition.
            // Repeated attempts do not need duplicate facts; retain real commit transitions after a rollback.
            if (step == "committed" ? committed : step == "rollback" ? !committed && recorded.Contains(entry) : recorded.Contains(entry)) return;
            string body = string.Create(CultureInfo.InvariantCulture, $"{count + 1}|{step}|{index}");
            long start = stream.Position;
            try
            {
                stream.Write(Encoding.ASCII.GetBytes($"{body}|{Check(body)}\n")); stream.Flush(true); count++;
                recorded.Add(entry);
                if (step == "committed") committed = true;
                else if (step == "rollback") committed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A partial record must not remain in front of later ones.
                try { stream.SetLength(start); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { broken = true; }
                throw;
            }
        }
        public bool TryAppend(string step, int index = -1)
        {
            try { Append(step, index); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { return false; }
        }
        public void Dispose() { stream.Dispose();  }

        private static byte[] Contents(FileStream stream, long maximumBytes = MaximumBytes, CancellationToken token = default)
        {
            stream.Position = 0;
            return SourceRead.All(stream, Math.Min(MaximumBytes, maximumBytes), "its event log", token);
        }
        private static (List<JournalEvent> Events, long Valid) Parse(byte[] bytes, int files, CancellationToken token = default)
        {
            List<JournalEvent> events = []; int start = 0;
            while (start < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                int end = Array.IndexOf(bytes, (byte)'\n', start);
                if (end < 0) break; // An unterminated last record is torn.
                JournalEvent? record = end - start <= MaximumRecordBytes
                    ? Record(Encoding.ASCII.GetString(bytes, start, end - start), events.Count + 1, files) : null;
                if (record == null)
                {
                    if (Array.IndexOf(bytes, (byte)'\n', end + 1) >= 0) throw new InvalidDataException($"its event log is damaged at record {events.Count + 1}.");
                    break; // So is an unreadable last record.
                }
                events.Add(record.Value); start = end + 1;
            }
            return (events, start);
        }
        private static JournalEvent? Record(string line, int sequence, int files)
        {
            string[] parts = line.Split('|');
            if (parts.Length != 4 || parts[0] != sequence.ToString(CultureInfo.InvariantCulture) || !Steps.Contains(parts[1], StringComparer.Ordinal)
                || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int index) || index < -1 || index >= files
                || parts[3] != Check($"{parts[0]}|{parts[1]}|{parts[2]}")) return null;
            return new(parts[1], index);
        }
        private static string Check(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(body)))[..16];
    }
}
