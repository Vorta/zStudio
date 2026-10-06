namespace Recoil.Zbd.Core.Sources;

/// <summary>One file of an accepted change: its project-relative path and its content before and after (null: the file does not exist).</summary>
public sealed record SourceFileChange(string Relative, byte[]? Before, byte[]? After);
/// <summary>One accepted, undoable change to a source project's files.</summary>
public sealed record SourceTransaction(long Id, string Label, IReadOnlyList<SourceFileChange> Files);
/// <summary>A change to the workspace: an applied, undone or redone transaction, a save or a discard, with the files whose content changed.</summary>
public sealed record SourceWorkspaceChange(string Kind, string Label, IReadOnlyList<string> Files, long Revision);
/// <summary>
/// A project file was changed on disk by another program: one the workspace holds edits for, or one a world's build or a
/// Blender checkout read (or a source added, removed or renamed after a world's build listed the project) before it finished.
/// </summary>
public sealed class SourceFileChangedException(string message, IReadOnlyList<string> files) : IOException(message)
{
    public IReadOnlyList<string> Files { get; } = files;
}

/// <summary>
/// The working state of one source project: accepted edits to any of its source files (<c>data/</c>, <c>gamegen/</c>), held in
/// memory with one project-wide undo history until <see cref="Save"/> writes every changed file together. Mission worlds,
/// resource editors and MCP all edit through it, so an edit shared by two missions is one change, undone once. Builds read
/// <see cref="Overlay"/> in place of the files on disk. Edits are applied on one thread; reads such as <see cref="Overlay"/> and
/// <see cref="ExternalChanges"/> may run on others.
/// </summary>
public sealed class SourceWorkspace
{
    /// <summary>Saves several files together: each write names the bytes the file must have now (null: absent) and its new bytes (null: delete). Returns the files written.</summary>
    public delegate IReadOnlyList<string> Saver(IReadOnlyList<(string Relative, byte[]? Expected, byte[]? Content)> writes, string description, CancellationToken token);
    private sealed record Baseline(byte[]? Bytes, FileStamp? Stamp);
    /// <summary>Each touched file as it was on disk when first read or last saved.</summary>
    private readonly Dictionary<string, Baseline> baselines = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each touched file's accepted content; equal to its baseline when it is not dirty.</summary>
    private readonly Dictionary<string, byte[]?> working = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceTransaction> history = [];
    /// <summary>The files each content change touched, by the <see cref="ContentRevision"/> it produced.</summary>
    private readonly List<(long Revision, string[] Files)> changeLog = [];
    private readonly Saver save;
    private readonly Lock gate = new();
    private int position;
    /// <summary>What the newest edit displaced (the redo steps after it, the oldest steps trimmed), until another step moves.</summary>
    private (SourceTransaction Transaction, SourceTransaction[] Redo, SourceTransaction[] Trimmed)? displaced;
    private long nextId = 1;
    public string Root { get; }
    /// <summary>Increases with every applied, undone or redone change, save and discard.</summary>
    public long Revision { get; private set; }
    /// <summary>Increases whenever the accepted content of any file changes (not on save), so a build made at one value is current while it holds.</summary>
    public long ContentRevision { get; private set; }
    /// <summary>A save is writing the files (see <see cref="SaveAsync"/>, which may end on another thread than it began).</summary>
    public bool IsSaving { get => saving; private set => saving = value; }
    private volatile bool saving;
    public event Action<SourceWorkspaceChange>? Changed;
    /// <summary>Asked for each file an edit would change; a returned reason refuses the edit (another editor holds unsaved changes of the file).</summary>
    public Func<string, string?>? EditGuard { get; set; }

    public SourceWorkspace(string root, Saver? saver = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!SourceProject.IsProject(Root)) throw new InvalidDataException("This folder is not a source project (it needs data and gamegen folders).");
        // Every later check of a file starts below Root; a linked root or ancestor would let edits and saves leave the folder.
        SourceProject.RejectLinkedProject(Root);
        save = saver ?? new Saver((writes, description, token) => new SourcePublisher(Root).Publish(writes.Select(w => new SourceFileWrite(w.Relative, w.Expected, w.Content)).ToArray(), description, token).Written);
    }

    /// <summary>A project-relative path with forward slashes, as builds and history key files.</summary>
    public static string Normalize(string relative) => relative.Replace('\\', '/');
    /// <summary>Only source files can be edited: under data or gamegen, inside the project, not through links and not in a protected corpus.</summary>
    public void CheckEditable(string relative)
    {
        relative = Normalize(relative);
        string path = SourceProject.Resolve(Root, relative);
        string first = relative.Split('/')[0];
        if (!first.Equals(SourceProject.DataFolder, StringComparison.OrdinalIgnoreCase) && !first.Equals(SourceProject.GameGenFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{relative} is not a source file: sources are under {SourceProject.DataFolder}/ or {SourceProject.GameGenFolder}/.");
        SourceProject.RejectNestedLinks(Root, relative);
        if (PickupPlacementEditSession.IsProtectedPath(path)) throw new InvalidDataException($"{relative} is inside the protected zbd_1998/zbd_1999 folders.");
    }

    /// <summary>
    /// The accepted content of a file: the workspace's when it holds unsaved edits for it, otherwise the file on disk now
    /// (null when absent). A clean file another program changed is read again, so an edit is never computed from old bytes.
    /// </summary>
    public byte[]? Read(string relative, CancellationToken token = default)
    {
        relative = Normalize(relative);
        Baseline? baseline = null;
        lock (gate)
            if (working.TryGetValue(relative, out var bytes))
            {
                baseline = baselines[relative];
                if (!Same(bytes, baseline.Bytes)) return bytes;
            }
        token.ThrowIfCancellationRequested();
        if (baseline != null && Matches(relative, baseline)) return baseline.Bytes;
        return ReadDisk(relative).Bytes;
    }
    public bool Exists(string relative) => Read(relative) != null;
    /// <summary>Whether the workspace holds edits for a file that are not saved.</summary>
    public bool IsFileDirty(string relative) { lock (gate) return working.TryGetValue(Normalize(relative), out var bytes) && !Same(bytes, baselines[Normalize(relative)].Bytes); }
    /// <summary>Files whose accepted content differs from the disk state the workspace last read or saved, in path order.</summary>
    public IReadOnlyList<string> DirtyFiles { get { lock (gate) return working.Where(p => !Same(p.Value, baselines[p.Key].Bytes)).Select(p => p.Key).Order(StringComparer.OrdinalIgnoreCase).ToArray(); } }
    public bool IsDirty { get { lock (gate) return working.Any(p => !Same(p.Value, baselines[p.Key].Bytes)); } }
    /// <summary>The accepted content of every changed file, for builds to read instead of the disk.</summary>
    public IReadOnlyDictionary<string, byte[]> Overlay()
    {
        Dictionary<string, byte[]> overlay = new(StringComparer.OrdinalIgnoreCase);
        lock (gate)
            foreach (var (relative, bytes) in working)
                if (bytes != null && !Same(bytes, baselines[relative].Bytes)) overlay[relative] = bytes;
        return overlay;
    }

    public bool CanUndo => !IsSaving && position > 0;
    public bool CanRedo => !IsSaving && position < history.Count;
    public string? UndoLabel => CanUndo ? history[position - 1].Label : null;
    public string? RedoLabel => CanRedo ? history[position].Label : null;
    /// <summary>The accepted transactions, oldest first (those after <see cref="UndoCount"/> can be redone).</summary>
    public IReadOnlyList<SourceTransaction> History => history;
    public int UndoCount => position;

    /// <summary>
    /// Accepts one change of several files as a single undoable step; files whose content does not change are left out, and
    /// a change of nothing returns null. Every file must be a source file. A file changed on disk since the workspace read it
    /// is re-read first when the workspace holds no unsaved edits for it, and refused otherwise.
    /// </summary>
    public SourceTransaction? Apply(string label, IEnumerable<(string Relative, byte[]? Content)> changes, CancellationToken token = default)
    {
        if (IsSaving) throw new InvalidOperationException("Wait for the save to finish.");
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        List<SourceFileChange> files = []; HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, content) in changes)
        {
            token.ThrowIfCancellationRequested();
            string relative = Normalize(raw);
            if (!seen.Add(relative)) throw new InvalidDataException($"{relative} is changed twice in one edit.");
            CheckEditable(relative);
            if (EditGuard?.Invoke(relative) is { } refused) throw new InvalidDataException(refused);
            if (content != null && content.Length > Formats.FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException($"{relative} would exceed 512 MiB.");
            Refresh(relative);
            byte[]? before; lock (gate) before = working.TryGetValue(relative, out var current) ? current : baselines[relative].Bytes;
            if (Same(before, content)) continue;
            // A build reads the overlay in place of the disk; a file the disk still holds cannot be hidden from it yet.
            if (content == null && BaselineOf(relative).Bytes != null) throw new NotSupportedException($"Deleting {relative} from the project is not supported.");
            files.Add(new(relative, before, content));
        }
        if (files.Count == 0) return null;
        SourceTransaction transaction = new(nextId++, label, files);
        lock (gate)
        {
            SourceTransaction[] redo = [.. history.Skip(position)];
            history.RemoveRange(position, history.Count - position);
            history.Add(transaction); position++;
            foreach (var file in files) working[file.Relative] = file.After;
            displaced = (transaction, redo, Trim());
        }
        Publish("apply", label, files.Select(f => f.Relative));
        return transaction;
    }

    public SourceWorkspaceChange Undo()
    {
        if (!CanUndo) throw new InvalidOperationException("Nothing to undo.");
        var transaction = history[position - 1];
        Guard(transaction);
        // Builds read the overlay in place of the disk and cannot see a file go away: a created file that was saved stays.
        if (transaction.Files.FirstOrDefault(f => f.Before == null && BaselineOf(f.Relative).Bytes != null) is { } created)
            throw new NotSupportedException($"{created.Relative} was created by {transaction.Label} and saved since; undoing it would delete the file, which the workspace does not do. Delete it by hand.");
        lock (gate) { foreach (var file in transaction.Files) working[file.Relative] = file.Before; position--; displaced = null; }
        return Publish("undo", transaction.Label, transaction.Files.Select(f => f.Relative));
    }
    public SourceWorkspaceChange Redo()
    {
        if (!CanRedo) throw new InvalidOperationException("Nothing to redo.");
        var transaction = history[position];
        Guard(transaction);
        lock (gate) { foreach (var file in transaction.Files) working[file.Relative] = file.After; position++; displaced = null; }
        return Publish("redo", transaction.Label, transaction.Files.Select(f => f.Relative));
    }
    /// <summary>Undo and redo change files too: one another editor holds unsaved changes of is refused, as for an edit.</summary>
    private void Guard(SourceTransaction transaction)
    {
        foreach (var file in transaction.Files)
            if (EditGuard?.Invoke(file.Relative) is { } refused) throw new InvalidDataException(refused);
    }
    /// <summary>
    /// Forgets the content changes after <paramref name="contentRevision"/>, after a change was taken back so that every file
    /// again holds what it held then: builds made at that revision are current again (see <see cref="ChangedSince"/>).
    /// </summary>
    public void ForgetChangesAfter(long contentRevision)
    {
        lock (gate) changeLog.RemoveAll(c => c.Revision > contentRevision);
    }
    /// <summary>The most steps and bytes (before and after copies) the history keeps; the oldest steps go first.</summary>
    public const int MaximumHistory = 256;
    public const long MaximumHistoryBytes = 1024L * 1024 * 1024;
    private SourceTransaction[] Trim()
    {
        List<SourceTransaction> trimmed = [];
        long bytes = history.Sum(t => t.Files.Sum(f => (long)(f.Before?.Length ?? 0) + (f.After?.Length ?? 0)));
        while (history.Count > 1 && position > 1 && (history.Count > MaximumHistory || bytes > MaximumHistoryBytes))
        {
            bytes -= history[0].Files.Sum(f => (long)(f.Before?.Length ?? 0) + (f.After?.Length ?? 0));
            trimmed.Add(history[0]); history.RemoveAt(0); position--;
        }
        return [.. trimmed];
    }
    /// <summary>
    /// Withdraws the newest transaction, one that turned out not to build; unlike Undo it cannot be redone. The history
    /// is as it was before: the redo steps and the oldest steps the edit displaced return.
    /// </summary>
    public SourceWorkspaceChange Retract(SourceTransaction transaction)
    {
        if (position == 0 || history[position - 1] != transaction) throw new InvalidOperationException("Only the newest accepted change can be withdrawn.");
        lock (gate)
        {
            foreach (var file in transaction.Files) working[file.Relative] = file.Before;
            position--; history.RemoveRange(position, history.Count - position);
            if (displaced is { } d && d.Transaction == transaction)
            {
                history.InsertRange(0, d.Trimmed); position += d.Trimmed.Length;
                history.AddRange(d.Redo);
            }
            displaced = null;
        }
        return Publish("retract", transaction.Label, transaction.Files.Select(f => f.Relative));
    }

    /// <summary>Files whose accepted content changed after <paramref name="contentRevision"/> (see <see cref="ContentRevision"/>). Safe on any thread.</summary>
    public IReadOnlySet<string> ChangedSince(long contentRevision)
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        lock (gate)
            for (int i = changeLog.Count - 1; i >= 0 && changeLog[i].Revision > contentRevision; i--) files.UnionWith(changeLog[i].Files);
        return files;
    }

    /// <summary>Touched files changed on disk by another program since the workspace read or saved them, in path order.</summary>
    public IReadOnlyList<string> ExternalChanges()
    {
        List<string> changed = []; KeyValuePair<string, Baseline>[] entries;
        lock (gate) entries = [.. baselines];
        foreach (var (relative, baseline) in entries)
            if (!Matches(relative, baseline)) changed.Add(relative);
        return changed.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Writes every dirty file together (see <see cref="SourcePublisher"/>); a file changed on disk since the workspace read
    /// it is a conflict and nothing is written. History is kept, so saved changes can still be undone. Returns the files written.
    /// </summary>
    public IReadOnlyList<string> Save(CancellationToken token = default)
    {
        if (BeginSave() is not { } pending) return [];
        (IReadOnlyList<string> Written, Baseline[] Saved) published;
        try { published = Write(pending, token); }
        finally { IsSaving = false; }
        return EndSave(pending, published);
    }
    /// <summary>
    /// <see cref="Save"/> with the files published off the calling thread. The dirty files are taken on the calling thread
    /// (the one edits are applied on), published and read back on the thread pool, and the saved state is applied when the
    /// calling thread's synchronization context resumes this method (the UI dispatcher in the application), where
    /// <see cref="Changed"/> is raised. <see cref="IsSaving"/> holds until then, so edits, undo, redo, reload and discard are
    /// refused while the files are written. Cancellation is honoured until publication starts; from then on the save
    /// finishes or is undone (see <see cref="SourcePublisher.Publish"/>).
    /// </summary>
    public async Task<IReadOnlyList<string>> SaveAsync(CancellationToken token = default)
    {
        if (BeginSave() is not { } pending) return [];
        (IReadOnlyList<string> Written, Baseline[] Saved) published;
        try { published = await Task.Run(() => Write(pending, token), token); }
        finally { IsSaving = false; }
        return EndSave(pending, published);
    }
    private sealed record PendingSave(IReadOnlyList<string> Dirty, (string Relative, byte[]? Expected, byte[]? Content)[] Writes);
    /// <summary>Takes the dirty files and marks the workspace saving; null when nothing is dirty.</summary>
    private PendingSave? BeginSave()
    {
        if (IsSaving) throw new InvalidOperationException("A save is already running.");
        var dirty = DirtyFiles;
        if (dirty.Count == 0) return null;
        (string Relative, byte[]? Expected, byte[]? Content)[] writes;
        lock (gate) writes = dirty.Select(f => (f, baselines[f].Bytes, working[f])).ToArray();
        IsSaving = true;
        return new(dirty, writes);
    }
    /// <summary>Publishes a save's files and reads them back; touches no workspace state, so it may run on any thread.</summary>
    private (IReadOnlyList<string> Written, Baseline[] Saved) Write(PendingSave pending, CancellationToken token)
    {
        var written = save(pending.Writes, $"Save {pending.Dirty.Count} source file{(pending.Dirty.Count == 1 ? "" : "s")}", token);
        // The files are on disk now; another program may already have replaced one, so each is read back with its stamp.
        Baseline[] saved = [.. pending.Writes.Select(w => Saved(w.Relative, w.Content))];
        return (written, saved);
    }
    private IReadOnlyList<string> EndSave(PendingSave pending, (IReadOnlyList<string> Written, Baseline[] Saved) published)
    {
        lock (gate)
            for (int i = 0; i < pending.Writes.Length; i++) baselines[pending.Writes[i].Relative] = published.Saved[i];
        Publish("save", "Save", pending.Dirty);
        return published.Written;
    }
    /// <summary>
    /// The baseline of a file just saved with <paramref name="bytes"/>: its stamp, taken while the file still has exactly those
    /// bytes. A file another program changed or removed since gets no stamp, so <see cref="ExternalChanges"/> reports it and
    /// <see cref="Read"/> serves the disk's content instead of the bytes saved.
    /// </summary>
    private Baseline Saved(string relative, byte[]? bytes)
    {
        if (bytes == null) return new(null, null);
        try
        {
            string path = SourceProject.Resolve(Root, relative);
            if (!File.Exists(path)) return new(bytes, null);
            var stamp = FileStamp.Read(path);
            return SourceProject.FileEquals(path, bytes) && FileStamp.Read(path) == stamp ? new(bytes, stamp) : new(bytes, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(bytes, null); }
    }

    /// <summary>Drops every unsaved edit and the history; files on disk are what the workspace serves afterwards.</summary>
    public SourceWorkspaceChange Discard()
    {
        if (IsSaving) throw new InvalidOperationException("Wait for the save to finish.");
        var files = DirtyFiles;
        lock (gate) { working.Clear(); baselines.Clear(); history.Clear(); position = 0; displaced = null; }
        return Publish("discard", "Discard", files);
    }

    /// <summary>
    /// Re-reads files changed on disk that the workspace holds no unsaved edits for, so it serves their new content; a file
    /// with unsaved edits that changed on disk is a conflict. History is cleared when any file is re-read, since its steps
    /// no longer describe the files. Returns the re-read files.
    /// </summary>
    public IReadOnlyList<string> Reload()
    {
        if (IsSaving) throw new InvalidOperationException("Wait for the save to finish.");
        var changed = ExternalChanges();
        var conflicts = changed.Where(IsFileDirty).ToArray();
        if (conflicts.Length > 0) throw new SourceFileChangedException($"{string.Join(", ", conflicts)} changed on disk while the workspace holds unsaved edits for {(conflicts.Length == 1 ? "it" : "them")}; save elsewhere or discard the edits first.", conflicts);
        if (changed.Count == 0) return [];
        lock (gate) { foreach (string relative in changed) { baselines.Remove(relative); working.Remove(relative); } history.Clear(); position = 0; displaced = null; }
        Publish("reload", "Reload", changed);
        return changed;
    }

    /// <summary>Reads a touched file's baseline once; a clean file that changed on disk since is re-read, a dirty one is a conflict.</summary>
    private void Refresh(string relative)
    {
        Baseline? baseline; lock (gate) baselines.TryGetValue(relative, out baseline);
        if (baseline == null) { var read = ReadDisk(relative); lock (gate) baselines[relative] = read; return; }
        if (Matches(relative, baseline)) return;
        if (IsFileDirty(relative)) throw new SourceFileChangedException($"{relative} changed on disk while the workspace holds unsaved edits for it; save elsewhere or discard the edits first.", [relative]);
        // History steps of this file no longer describe it; keeping them would undo another program's edit.
        if (history.Any(t => t.Files.Any(f => f.Relative.Equals(relative, StringComparison.OrdinalIgnoreCase))))
            throw new SourceFileChangedException($"{relative} changed on disk since the workspace edited it; reload the project's worlds to continue from the file.", [relative]);
        var fresh = ReadDisk(relative);
        lock (gate) { baselines[relative] = fresh; working.Remove(relative); }
    }
    private Baseline BaselineOf(string relative) { lock (gate) return baselines[relative]; }
    private bool Matches(string relative, Baseline baseline)
    {
        string path = SourceProject.Resolve(Root, relative);
        bool exists = File.Exists(path);
        if (!exists || baseline.Stamp == null) return !exists && baseline.Bytes == null;
        try { return FileStamp.Read(path) == baseline.Stamp; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private Baseline ReadDisk(string relative)
    {
        string path = SourceProject.Resolve(Root, relative);
        if (!File.Exists(path)) return new(null, null);
        SourceProject.RejectNestedLinks(Root, relative);
        var stamp = FileStamp.Read(path);
        if (stamp.Length > Formats.FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException($"{relative} exceeds 512 MiB.");
        byte[] bytes = File.ReadAllBytes(path);
        if (FileStamp.Read(path) != stamp) throw new IOException($"{relative} changed while it was read; try again.");
        return new(bytes, stamp);
    }
    private SourceWorkspaceChange Publish(string kind, string label, IEnumerable<string> files)
    {
        string[] changed = files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (gate)
        {
            Revision++;
            if (kind != "save") { ContentRevision++; changeLog.Add((ContentRevision, changed)); }
        }
        SourceWorkspaceChange change = new(kind, label, changed, Revision);
        Changed?.Invoke(change);
        return change;
    }
    private static bool Same(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);
}
