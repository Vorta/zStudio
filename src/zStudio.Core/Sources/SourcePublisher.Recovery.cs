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
/// <summary>A file a resolution left alone because acting on it could overwrite or lose content, and why.</summary>
public sealed record SourceRecoveryConflict(string Relative, string Reason);
/// <summary>
/// The outcome of <see cref="SourcePublisher.Resolve"/>: the files whose content it changed (originals put back, new files
/// installed or files deleted), the files that still need a decision, and whether the journal was retired.
/// </summary>
public sealed record SourceRecoveryResult(IReadOnlyList<string> Changed, IReadOnlyList<SourceRecoveryConflict> Conflicts, bool Resolved);

/// <summary>A content's length and lowercase SHA-256, as a journal records it.</summary>
internal sealed record JournalDigest(long Length, string Sha256)
{
    public static JournalDigest? Of(byte[]? bytes) => bytes == null ? null : new(bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    public static JournalDigest Of(Stream stream) { long length = stream.Length; return new(length, Convert.ToHexStringLower(SHA256.HashData(stream))); }
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
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    };

    private readonly record struct JournalEvent(string Step, int Index);
    private sealed class Journal(JournalManifest manifest, IReadOnlyList<JournalEvent> events)
    {
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
        if (!Directory.Exists(SourceProject.Resolve(root, RecoveryFolder))) return [];
        using FileStream gate = Lock();
        List<SourceRecoveryCase> cases = [];
        foreach (var journal in Journals())
        {
            token.ThrowIfCancellationRequested();
            if (journal.RolledBack) continue;
            string folder = JournalPath(journal.Id);
            // Each state reads its file whole, and a journal may list 50,000 files.
            var files = journal.Manifest.Files.Select((f, i) => { token.ThrowIfCancellationRequested(); return new SourceRecoveryFile(f.Relative, State(f), File.Exists(HeldPath(folder, i))); }).ToArray();
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
        if (!IsSaveId(saveId) || !Directory.Exists(SourceProject.Resolve(root, RecoveryFolder))) return [];
        using FileStream gate = Lock();
        if (!File.Exists(Path.Combine(JournalPath(saveId), ManifestName))) return [];
        Journal journal = Load(saveId);
        return journal.RolledBack ? [] : [.. journal.Manifest.Files.Select(f => f.Relative)];
    }

    private SourceRecoveryFileState State(JournalFile file)
    {
        Probe probe = LookAt(file.Relative);
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
    /// <paramref name="token"/> is observed between files, never while one is being changed. Canceled before any file
    /// changed, it throws <see cref="OperationCanceledException"/> with no file changed; canceled later, the resolution stops
    /// before the next file, as an interruption would, and throws one whose message names the files it changed (its inner
    /// exception is the cancellation). Either way the journal records exactly what was done, the save still needs a
    /// decision, and any action resolves it from there.
    /// </remarks>
    public SourceRecoveryResult Resolve(string saveId, SourceRecoveryAction action, CancellationToken token = default)
    {
        if (!IsSaveId(saveId)) throw new ArgumentException($"'{saveId}' is not a save identity.", nameof(saveId));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        token.ThrowIfCancellationRequested();
        using FileStream gate = Lock();
        if (!File.Exists(Path.Combine(JournalPath(saveId), ManifestName))) throw new FileNotFoundException($"There is no save journal {saveId} in {RecoveryFolder}.");
        Journal journal = Load(saveId);
        token.ThrowIfCancellationRequested();
        if (journal.RolledBack) return HasFiles(Path.Combine(JournalPath(saveId), HeldFolder)) ? Abandon(journal) : Retire(saveId);
        return action switch
        {
            SourceRecoveryAction.RollBack when journal.Committed => throw new InvalidOperationException($"The save {saveId} was committed: its files are final and only its cleanup remains. Complete or abandon it instead."),
            SourceRecoveryAction.RollBack => RollBack(journal, token),
            SourceRecoveryAction.Complete when journal.Committed => Retire(saveId),
            SourceRecoveryAction.Complete => Complete(journal, token),
            _ => Abandon(journal),
        };
    }

    private SourceRecoveryResult RollBack(Journal journal, CancellationToken token)
    {
        string folder = JournalPath(journal.Id); var files = journal.Manifest.Files;
        List<string> changed = []; List<SourceRecoveryConflict> conflicts = [];
        using (EventLog log = EventLog.Open(folder, files.Count))
        {
            log.Append("rollback", -1);
            for (int i = files.Count - 1; i >= 0; i--)
            {
                if (token.IsCancellationRequested) throw Canceled(journal.Id, changed, token);
                JournalFile file = files[i];
                bool moved = File.Exists(HeldPath(folder, i));
                // Files the save never reached (no intent is recorded before a file is touched) keep whatever they have.
                if (!journal.Intended(i) && !moved) continue;
                bool installed = file.Content != null && LookAt(file.Relative).Is(file.Content);
                var (done, conflict) = Undo(folder, file, i, log, moved, installed);
                if (done) changed.Add(file.Relative);
                conflict ??= State(file) switch
                {
                    SourceRecoveryFileState.Before => null,
                    SourceRecoveryFileState.Missing => new(file.Relative, "is missing and its original is not in the save journal; it was not recreated"),
                    _ => new(file.Relative, "was changed by another program during the interrupted save; it was left as it is"),
                };
                if (conflict != null) conflicts.Add(conflict);
            }
            changed.Reverse(); conflicts.Reverse();
            // Only empty folders go, so this is safe beside a conflict.
            RemoveFolders(journal.Manifest.Folders);
            if (conflicts.Count > 0) return new(changed, conflicts, false);
            log.Append("rolled-back", -1);
        }
        return Retire(journal.Id, changed);
    }

    /// <summary>
    /// Undoes what a save did to one file: takes back the new content it installed (only while that content is unchanged),
    /// then returns the original it moved aside (only while the name is free).
    /// </summary>
    private (bool Changed, SourceRecoveryConflict? Conflict) Undo(string journal, JournalFile file, int index, EventLog log, bool moved, bool installed)
    {
        string path = SourceProject.Resolve(root, file.Relative), held = HeldPath(journal, index); bool changed = false;
        try
        {
            Step("undo", index);
            SourceProject.RejectNestedLinks(root, file.Relative);
            if (installed && file.Content is { } content)
            {
                if (file.Expected != null && !moved) return (false, new(file.Relative, "has the saved content but its original is no longer in the save journal, so it was left as it is"));
                string taken = TakenPath(journal, index);
                switch (MoveIfContent(path, taken, content))
                {
                    case Moved.Done: changed = true; log.Append("removed", index); break;
                    case Moved.Stranded: return (true, new(file.Relative, $"was replaced by another program while the save was undone; that file was kept as {Display(taken)}"));
                    default:
                        if (Path.Exists(path)) return (false, new(file.Relative, "was changed by another program after the save wrote it, so it was left as it is" + (moved ? $"; its original remains as {Display(held)}" : "")));
                        break;
                }
            }
            if (moved)
            {
                if (Path.Exists(path))
                {
                    // Another program already put the original content back: the journal's copy is a duplicate.
                    if (Look(path).Is(file.Expected) && Look(held).Is(file.Expected)) return (changed, null);
                    return (changed, new(file.Relative, $"holds content from another program, so its original was not put back; it remains as {Display(held)}"));
                }
                File.Move(held, path, false); changed = true; log.Append("restored", index);
            }
            return (changed, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return (changed, new(file.Relative, $"could not be restored: {ex.Message}")); }
    }

    private SourceRecoveryResult Complete(Journal journal, CancellationToken token)
    {
        string folder = JournalPath(journal.Id), staging = StagingPath(journal.Id); var files = journal.Manifest.Files;
        // Decide first: nothing changes unless every file can be finished.
        List<SourceRecoveryConflict> conflicts = [];
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (Blocker(folder, files[i], i) is { } blocker) conflicts.Add(blocker);
        }
        if (conflicts.Count > 0) return new([], conflicts, false);
        token.ThrowIfCancellationRequested();
        List<string> changed = [];
        using (EventLog log = EventLog.Open(folder, files.Count))
        {
            for (int i = 0; i < files.Count; i++)
            {
                if (token.IsCancellationRequested) throw Canceled(journal.Id, changed, token);
                JournalFile file = files[i]; string path = SourceProject.Resolve(root, file.Relative), held = HeldPath(folder, i);
                try
                {
                    Step("complete", i);
                    SourceProject.RejectNestedLinks(root, file.Relative);
                    Probe probe = Look(path);
                    if (probe.Is(file.Content)) continue;
                    log.Append("intent", i);
                    if (file.Expected is { } expected && probe.Is(expected))
                    {
                        if (MoveIfContent(path, held, expected) is not Moved.Done and var outcome)
                        {
                            conflicts.Add(new(file.Relative, outcome == Moved.Stranded ? $"was replaced by another program while the save was being completed; the replaced file was kept as {Display(held)}" : "changed while the save was being completed; it was left as it is"));
                            continue;
                        }
                        log.Append("held", i);
                    }
                    else if (!probe.Is(file.Expected) && !(probe.Kind == Presence.Absent && Look(held).Is(file.Expected)))
                    { conflicts.Add(new(file.Relative, "changed while the save was being completed; it was left as it is")); continue; }
                    if (file.Content is { } content)
                    {
                        string temporary = StagedPath(staging, i);
                        SourceProject.RejectNestedLinks(root, StagingFolder); Directory.CreateDirectory(staging);
                        WriteVerified(temporary, NewContent(folder, i, content));
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        // Held from its check against the journaled content until it is in place (see SealedFile).
                        using (SealedFile staged = SealedFile.Open(temporary, content))
                        {
                            try { staged.MoveTo(path); }
                            catch (IOException) when (Path.Exists(path)) { conflicts.Add(new(file.Relative, "was created by another program while the save was being completed; it was not replaced")); continue; }
                        }
                        log.Append("installed", i);
                    }
                    changed.Add(file.Relative);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { conflicts.Add(new(file.Relative, $"could not be completed: {ex.Message}")); }
            }
            if (conflicts.Count > 0) return new(changed, conflicts, false);
            log.Append("committed", -1);
        }
        return Retire(journal.Id, changed);
    }

    /// <summary>
    /// A resolution canceled between two files. With nothing changed, a plain cancellation; otherwise one that says which
    /// files changed, since the journal (which recorded each step) leaves the save to be resolved again.
    /// </summary>
    private static OperationCanceledException Canceled(string id, IReadOnlyCollection<string> changed, CancellationToken token) => changed.Count == 0 ? new(token)
        : new($"Resolving the interrupted save {id} was canceled after it changed {changed.Count:N0} file{(changed.Count == 1 ? "" : "s")} ({string.Join(", ", changed.Take(8))}{(changed.Count > 8 ? ", …" : "")}); its journal records them, and the save still needs a decision.", new OperationCanceledException(token), token);

    /// <summary>Why one file of a save cannot be completed, or null when it can.</summary>
    private SourceRecoveryConflict? Blocker(string journal, JournalFile file, int index)
    {
        Probe probe = LookAt(file.Relative); string held = HeldPath(journal, index);
        if (probe.Is(file.Content)) return null;
        if (file.Content is { } content && !Look(AfterPath(journal, index)).Is(content)) return new(file.Relative, "cannot be completed because the save journal's copy of its new content is missing or damaged");
        if (probe.Is(file.Expected))
            return file.Expected != null && File.Exists(held) ? new(file.Relative, $"has its original content while the save journal also holds an original ({Display(held)}); it was left as it is") : null;
        if (probe.Kind == Presence.Absent)
            return !File.Exists(held) ? new(file.Relative, "is missing and its original is not in the save journal; it was not recreated")
                : Look(held).Is(file.Expected) ? null : new(file.Relative, $"cannot be completed because the original the save journal holds ({Display(held)}) differs from the one the save expected");
        return new(file.Relative, "was changed by another program; it was left as it is");
    }

    private static byte[] NewContent(string journal, int index, JournalDigest content)
    {
        byte[] bytes = File.ReadAllBytes(AfterPath(journal, index));
        if (JournalDigest.Of(bytes) != content) throw new InvalidDataException("the save journal's copy of its new content is damaged.");
        return bytes;
    }

    private SourceRecoveryResult Abandon(Journal journal)
    {
        string folder = JournalPath(journal.Id);
        if (!HasFiles(Path.Combine(folder, HeldFolder)) && !HasFiles(Path.Combine(folder, TakenFolder))) return Retire(journal.Id);
        // Originals (and files taken back from other programs) are kept where the user can find them.
        SourceProject.RejectNestedLinks(root, AbandonedFolder);
        string abandoned = SourceProject.Resolve(root, AbandonedFolder), destination = Path.Combine(abandoned, journal.Id);
        Directory.CreateDirectory(abandoned);
        for (int n = 2; Path.Exists(destination); n++) destination = Path.Combine(abandoned, $"{journal.Id}-{n}");
        Directory.Move(folder, destination);
        TryDelete(StagingPath(journal.Id));
        return new([], [], true);
    }

    /// <summary>Removes a finished journal; a journal that cannot be removed is reported and retried by the next save.</summary>
    private SourceRecoveryResult Retire(string id, IReadOnlyList<string>? changed = null)
    {
        Discard(id);
        return Directory.Exists(JournalPath(id))
            ? new(changed ?? [], [new(RecoveryFolder + "/" + id, "could not be removed; close programs that use it and resolve the save again")], false)
            : new(changed ?? [], [], true);
    }

    /// <summary>Every readable journal in the recovery folder, oldest first; a journal that cannot be read blocks saving.</summary>
    private List<Journal> Journals()
    {
        string recovery = SourceProject.Resolve(root, RecoveryFolder);
        if (!Directory.Exists(recovery)) return [];
        List<string> ids = [];
        foreach (var directory in new DirectoryInfo(recovery).EnumerateDirectories())
        {
            if (!IsSaveId(directory.Name) || directory.Attributes.HasFlag(FileAttributes.ReparsePoint) || !File.Exists(Path.Combine(directory.FullName, ManifestName))) continue;
            if (ids.Count == MaximumJournals) throw new SourceRecoveryRequiredException($"{RecoveryFolder} holds more than {MaximumJournals} save journals; resolve or move them before saving.", directory.Name, []);
            ids.Add(directory.Name);
        }
        return [.. ids.Order(StringComparer.Ordinal).Select(Load)];
    }

    private Journal Load(string id)
    {
        string folder = JournalPath(id);
        try
        {
            JournalManifest manifest = ReadManifest(folder, id);
            return new(manifest, EventLog.Read(folder, manifest.Files.Count));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            throw new SourceRecoveryRequiredException($"The save journal {RecoveryFolder}/{id} cannot be read: {ex.Message} Recover any originals from its {HeldFolder} folder, then move the journal out of {RecoveryFolder}.", id, [], ex);
        }
    }

    private static void WriteManifest(string journal, JournalManifest manifest)
    {
        // Written whole under another name first, so a manifest that exists is complete.
        string temporary = Path.Combine(journal, ManifestName + ".tmp");
        WriteDurable(temporary, JsonSerializer.SerializeToUtf8Bytes(manifest, JournalJson));
        File.Move(temporary, Path.Combine(journal, ManifestName), false);
    }

    private JournalManifest ReadManifest(string journal, string id)
    {
        FileInfo info = new(Path.Combine(journal, ManifestName));
        if (info.Length > MaximumManifestBytes) throw new InvalidDataException($"its manifest is larger than {MaximumManifestBytes:N0} bytes.");
        JournalManifest manifest = JsonSerializer.Deserialize<JournalManifest>(File.ReadAllBytes(info.FullName), JournalJson) ?? throw new InvalidDataException("its manifest is empty.");
        if (manifest.Format != JournalFormat) throw new InvalidDataException($"its format {manifest.Format} is not supported by this version of zStudio.");
        if (manifest.SaveId != id) throw new InvalidDataException($"its manifest belongs to the save {manifest.SaveId}.");
        if (manifest.Description.Length > MaximumDescriptionLength) throw new InvalidDataException("its description is too long.");
        if (manifest.Files.Count == 0 || manifest.Files.Count > SourceProject.MaximumFiles) throw new InvalidDataException($"it lists {manifest.Files.Count:N0} files.");
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (JournalFile? file in manifest.Files)
        {
            if (file == null) throw new InvalidDataException("it lists an empty file entry.");
            CheckSyntax(file.Relative);
            if (!seen.Add(file.Relative)) throw new InvalidDataException($"it lists {file.Relative} more than once.");
            if (file.Expected == file.Content || !Valid(file.Expected) || !Valid(file.Content)) throw new InvalidDataException($"its entry for {file.Relative} is not a change.");
        }
        if (manifest.Folders.Count > manifest.Files.Count * 64) throw new InvalidDataException("it lists too many folders.");
        // Each folder must hold one of the new files. Sorted once, the new files that start with a folder's path are
        // adjacent, and the first path at or after it is one of them if any is: a binary search per folder instead of a
        // scan of every file (a journal may list 50,000 files and 64 folders for each).
        string[] created = [.. manifest.Files.Where(f => f.Content != null).Select(f => f.Relative).Order(StringComparer.OrdinalIgnoreCase)];
        foreach (string folder in manifest.Folders)
        {
            CheckSyntax(folder);
            string prefix = folder + "/";
            int at = Array.BinarySearch(created, prefix, StringComparer.OrdinalIgnoreCase);
            if (at < 0) at = ~at;
            if (at == created.Length || !created[at].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"it lists the folder {folder}, which holds none of its new files.");
        }
        return manifest;
        static bool Valid(JournalDigest? digest) => digest == null || digest.Length >= 0 && digest.Sha256 is { Length: 64 } hash && hash.All(char.IsAsciiHexDigitLower);
    }

    /// <summary>
    /// <c>events.log</c>: one record per line, <c>sequence|step|file index|check</c>, where the check is the first 16 hex digits
    /// of the SHA-256 of the rest. Each record is flushed to disk before the step it announces or after the step it confirms.
    /// A torn last record (from a write that did not finish) is ignored, and cut off before anything is appended.
    /// </summary>
    private sealed class EventLog : IDisposable
    {
        private const long MaximumBytes = 64L * 1024 * 1024;
        private static readonly string[] Steps = ["prepared", "intent", "held", "installed", "committed", "rollback", "removed", "restored", "rolled-back"];
        private readonly FileStream stream; private int count; private bool broken;
        private EventLog(FileStream stream, int count) { this.stream = stream; this.count = count; }

        public static EventLog Open(string journal, int files)
        {
            FileStream stream = new(Path.Combine(journal, EventsName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                var (events, valid) = Parse(Contents(stream), files);
                if (valid != stream.Length) { stream.SetLength(valid); stream.Flush(true); }
                stream.Seek(0, SeekOrigin.End);
                return new(stream, events.Count);
            }
            catch { stream.Dispose(); throw; }
        }

        public static IReadOnlyList<JournalEvent> Read(string journal, int files)
        {
            string path = Path.Combine(journal, EventsName);
            if (!File.Exists(path)) return [];
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Parse(Contents(stream), files).Events;
        }

        public void Append(string step, int index)
        {
            if (broken) throw new IOException("The save journal's event log could not be extended.");
            string body = string.Create(CultureInfo.InvariantCulture, $"{count + 1}|{step}|{index}");
            long start = stream.Position;
            try { stream.Write(Encoding.ASCII.GetBytes($"{body}|{Check(body)}\n")); stream.Flush(true); count++; }
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
        public void Dispose() => stream.Dispose();

        private static byte[] Contents(FileStream stream)
        {
            if (stream.Length > MaximumBytes) throw new InvalidDataException($"its event log is larger than {MaximumBytes:N0} bytes.");
            byte[] bytes = new byte[stream.Length]; stream.Position = 0; stream.ReadExactly(bytes);
            return bytes;
        }
        private static (List<JournalEvent> Events, long Valid) Parse(byte[] bytes, int files)
        {
            List<JournalEvent> events = []; int start = 0;
            while (start < bytes.Length)
            {
                int end = Array.IndexOf(bytes, (byte)'\n', start);
                if (end < 0) break; // An unterminated last record is torn.
                if (Record(Encoding.ASCII.GetString(bytes, start, end - start), events.Count + 1, files) is not { } record)
                {
                    if (Array.IndexOf(bytes, (byte)'\n', end + 1) >= 0) throw new InvalidDataException($"its event log is damaged at record {events.Count + 1}.");
                    break; // So is an unreadable last record.
                }
                events.Add(record); start = end + 1;
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
