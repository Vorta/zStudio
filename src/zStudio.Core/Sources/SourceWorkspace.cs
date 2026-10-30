using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

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

    /// <summary>Describe a reload refusal without expanding every conflicting identity into its diagnostic.</summary>
    public static SourceFileChangedException ForReload(IReadOnlyList<string> files)
    {
        string shown = string.Join(", ", files.Take(8).Select(path => JsonData.ShownText(path, 256)));
        string omitted = files.Count > 8 ? $" ({files.Count - 8:N0} more not shown)" : "";
        return new($"{files.Count:N0} conflicting file{(files.Count == 1 ? "" : "s")}{omitted}: {shown}. " +
            "These files changed on disk while the workspace holds unsaved edits; undo or discard the conflicting edits before reloading.", files);
    }
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
    /// <summary>Saves several files together: each entry names the bytes the file must have now (null: absent) and its new bytes (null: delete).
    /// Equal bytes are check-only dependencies, never writes. Forward validation to SourcePublisher.Publish so semantic
    /// inputs are verified under its project lock and held through publication. Returns the files written.</summary>
    public delegate IReadOnlyList<string> Saver(IReadOnlyList<(string Relative, byte[]? Expected, byte[]? Content)> writes, string description,
        CancellationToken token, Func<CancellationToken, SourcePublishValidation> validation);
    private sealed record Baseline(byte[]? Bytes, FileStamp? Stamp)
    {
        // Prepared reads retain this digest when dropping the content buffer.
        public string? Sha256 { get; init; } = Bytes == null ? null : SourceProject.Sha256(Bytes);
    }
    private Dictionary<string, Baseline>? preparedReads;
    private Dictionary<string, bool>? preparedPresence;
    internal bool IsPrepared => preparedReads != null;
    /// <summary>Each touched file as it was on disk when first read or last saved.</summary>
    private readonly Dictionary<string, Baseline> baselines = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each touched file's accepted content; equal to its baseline when it is not dirty.</summary>
    private readonly Dictionary<string, byte[]?> working = new(StringComparer.OrdinalIgnoreCase);
    // Content arrays are immutable once accepted. Compute equality when content changes, not for presence/UI queries.
    private readonly HashSet<string> dirtyFiles = new(StringComparer.OrdinalIgnoreCase);
    // Geometry inspected by the shared map-aware editing services. Keep this across history/save/reload:
    // an undo or later edit must still agree with profiles another editor has since created or changed.
    private readonly HashSet<string> mapZoneGeometry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> mapZoneOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, SourceMapZoneBindings.GeometrySource>> mapZoneSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceTransaction> history = [];
    /// <summary>The files each content change touched, by the <see cref="ContentRevision"/> it produced.</summary>
    private readonly List<(long Revision, string[] Files)> changeLog = [];
    private readonly Saver save;
    private readonly long maximumRetainedBytes;
    // A prepared fork shares these immutable arrays with its parent. They stay live there even when
    // the fork replaces a baseline or trims history; do not pretend preparation released them.
    private byte[][] parentContent = [];
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

    /// <summary>An isolated edit, prepared on a worker and accepted on the workspace's owning thread.</summary>
    public sealed class PreparedEdit
    {
        internal SourceWorkspace Owner { get; }
        internal long Revision { get; }
        internal long FirstId { get; }
        public SourceWorkspace Workspace { get; }
        internal PreparedEdit(SourceWorkspace owner, SourceWorkspace workspace)
        { Owner = owner; Revision = owner.Revision; Workspace = workspace; FirstId = workspace.nextId; }
    }

    /// <summary>Dependencies verified on a worker: content reads are held read-only, presence-only files against removal. Dispose after acceptance.</summary>
    public sealed class PreparedValidation : IDisposable
    {
        internal PreparedEdit Edit { get; }
        internal long Revision { get; }
        internal int ReadCount { get; }
        internal int PresenceCount { get; }
        private readonly List<IDisposable> files = [];
        private readonly DirectoryLease directories = new();
        private readonly string capturedRoot;
        internal bool IsDisposed { get; private set; }
        internal PreparedValidation(PreparedEdit edit)
        {
            Edit = edit; Revision = edit.Workspace.Revision; ReadCount = edit.Workspace.preparedReads!.Count;
            PresenceCount = edit.Workspace.preparedPresence!.Count;
            try { capturedRoot = directories.CapturedPath(Path.Combine(edit.Owner.Root, "_prepared-root")); }
            catch { directories.Dispose(); throw; }
        }
        internal void Hold(FileStream file) => files.Add(file);
        internal FileStream Open(string file) => directories.OpenFile(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        internal bool Exists(string file) => directories.Exists(file);
        internal bool FilePresent(string file)
        {
            var entry = directories.Inspect(file);
            if (entry?.Attributes.HasFlag(FileAttributes.ReparsePoint) == true)
                throw new IOException("A prepared presence dependency became a link; reopen the project and try again.");
            return entry != null && !entry.Attributes.HasFlag(FileAttributes.Directory);
        }
        internal void HoldPresence(string file)
        {
            directories.Parent(file);
            // Attribute-only handles do not enforce Windows delete sharing. Request read-data access so the
            // no-delete share participates, but never read the payload. Content writers can still share this read.
            files.Add(OperatingSystem.IsWindows()
                ? directories.FileHandle(file, 0x1 /* FILE_READ_DATA */, FileShare.ReadWrite)
                : directories.OpenFile(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        }
        internal void CheckRoot()
        {
            // DOS aliases such as SUBST can change while the physical directory/file handles remain held.
            // Resolve with a fresh lease; consulting this lease would only return the captured old root.
            using DirectoryLease current = new();
            string now = current.CapturedPath(Path.Combine(Edit.Owner.Root, "_prepared-root"));
            if (!string.Equals(now, capturedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new SourceFileChangedException("The source project folder changed while the edit was prepared; reopen the project and try again.", [.. Edit.Workspace.preparedReads!.Keys.Union(Edit.Workspace.preparedPresence!.Keys, StringComparer.OrdinalIgnoreCase)]);
        }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            foreach (var file in files) file.Dispose();
            directories.Dispose();
        }
    }

    /// <summary>Verify every prepared dependency off-thread, holding content against writes and presence against removal until accepted.</summary>
    public PreparedValidation VerifyPreparedEdit(PreparedEdit prepared, CancellationToken token = default)
    {
        if (prepared.Owner != this) throw new InvalidOperationException("The prepared edit belongs to another workspace.");
        token.ThrowIfCancellationRequested();
        prepared.Workspace.CheckPreparedDependencyLimits(token);
        PreparedValidation verified = new(prepared);
        try
        {
            foreach (var (relative, present) in prepared.Workspace.preparedPresence!)
            {
                token.ThrowIfCancellationRequested();
                if (prepared.Workspace.DiskPresence(relative, token) != present) PresenceChanged(relative);
                if (verified.FilePresent(SourceProject.Resolve(Root, relative)) != present) PresenceChanged(relative);
                if (present && !prepared.Workspace.preparedReads!.ContainsKey(relative))
                {
                    try { verified.HoldPresence(SourceProject.Resolve(Root, relative)); }
                    catch (FileNotFoundException) { PresenceChanged(relative); }
                    catch (DirectoryNotFoundException) { PresenceChanged(relative); }
                }
            }
            foreach (var (relative, baseline) in prepared.Workspace.preparedReads!)
            {
                token.ThrowIfCancellationRequested();
                SourceProject.RejectNestedLinks(Root, relative);
                string path = SourceProject.Resolve(Root, relative);
                if (baseline.Stamp == null)
                {
                    if (verified.Exists(path)) Changed();
                    continue;
                }
                var stream = verified.Open(path);
                verified.Hold(stream);
                FileStamp Stamp() => new(stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
                if (Stamp() != baseline.Stamp) Changed();
                if (baseline.Sha256 == null || !new JournalDigest(baseline.Stamp.Length, baseline.Sha256).Matches(stream, token)
                    || Stamp() != baseline.Stamp) Changed();
                void Changed() => throw new SourceFileChangedException($"{relative} changed while the edit was prepared; try again.", [relative]);
            }
            verified.CheckRoot();
            return verified;
        }
        catch { verified.Dispose(); throw; }
    }

    /// <summary>
    /// Bound verification before acquiring handles. Missing search candidates need checks but no file handles;
    /// shared-model inspection can legitimately probe thousands of them for a single object's edit.
    /// </summary>
    internal void CheckPreparedDependencyLimits(CancellationToken token, int maximumDependencies = 131_072, int maximumHeldFiles = 4096)
    {
        token.ThrowIfCancellationRequested();
        int dependencies = 0, heldFiles = 0;
        foreach (var baseline in preparedReads!.Values)
        {
            token.ThrowIfCancellationRequested();
            Count(baseline.Stamp != null);
        }
        foreach (var (relative, present) in preparedPresence!)
        {
            token.ThrowIfCancellationRequested();
            if (!preparedReads.ContainsKey(relative)) Count(present);
        }
        void Count(bool present)
        {
            if (++dependencies > maximumDependencies)
                throw new InvalidDataException($"A prepared edit checks more than {maximumDependencies:N0} source paths; reduce its dependency scope.");
            if (present && ++heldFiles > maximumHeldFiles)
                throw new InvalidDataException($"A prepared edit requires more than {maximumHeldFiles:N0} existing source files to stay open during verification; reduce its dependency scope.");
        }
    }

    /// <summary>Capture on the owning thread. Immutable content arrays are shared; history and dictionaries are isolated.</summary>
    public PreparedEdit BeginPreparedEdit()
    {
        if (IsSaving) throw new InvalidOperationException("Wait for the save to finish.");
        var fork = new SourceWorkspace(Root, (_, _, _, _) => throw new InvalidOperationException("A prepared edit cannot save."), maximumRetainedBytes)
        { preparedReads = new(StringComparer.OrdinalIgnoreCase), preparedPresence = new(StringComparer.OrdinalIgnoreCase), nextId = nextId, position = position };
        lock (gate)
        {
            foreach (var pair in baselines) fork.baselines.Add(pair.Key, pair.Value);
            foreach (var pair in working) fork.working.Add(pair.Key, pair.Value);
            fork.dirtyFiles.UnionWith(dirtyFiles);
            fork.mapZoneGeometry.UnionWith(mapZoneGeometry);
            foreach (var pair in mapZoneOwners) fork.mapZoneOwners.Add(pair.Key, pair.Value);
            foreach (var pair in mapZoneSources) fork.mapZoneSources.Add(pair.Key, pair.Value);
            fork.history.AddRange(history);
            fork.parentContent = RetainedContent().Distinct<byte[]>(ReferenceEqualityComparer.Instance).ToArray();
        }
        return new(this, fork);
    }

    /// <summary>Accept exactly one prepared transaction. Use held worker verification for metadata-only publication; otherwise verify content here.</summary>
    public SourceTransaction? AcceptPreparedEdit(PreparedEdit prepared, CancellationToken token = default, PreparedValidation? verified = null)
    {
        var transaction = ValidatePreparedEdit(prepared, token, verified);
        if (transaction == null) return null;
        lock (gate)
        {
            foreach (var file in transaction.Files) baselines[file.Relative] = prepared.Workspace.baselines[file.Relative];
            mapZoneGeometry.UnionWith(prepared.Workspace.mapZoneGeometry);
            foreach (var pair in prepared.Workspace.mapZoneOwners) mapZoneOwners[pair.Key] = pair.Value;
            foreach (var pair in prepared.Workspace.mapZoneSources) mapZoneSources[pair.Key] = pair.Value;
        }
        return Commit(transaction.Label, transaction.Files);
    }
    /// <summary>Check a prepared edit before resolving its owning GUI draft; this does not publish it.</summary>
    public SourceTransaction? ValidatePreparedEdit(PreparedEdit prepared, CancellationToken token = default, PreparedValidation? verified = null)
    {
        token.ThrowIfCancellationRequested();
        if (prepared.Owner != this || prepared.Revision != Revision || IsSaving)
            throw new InvalidOperationException("The source workspace changed while the edit was prepared; try again.");
        if (verified != null && (verified.Edit != prepared || verified.IsDisposed || verified.Revision != prepared.Workspace.Revision || verified.ReadCount != prepared.Workspace.preparedReads!.Count || verified.PresenceCount != prepared.Workspace.preparedPresence!.Count))
            throw new InvalidOperationException("The prepared content verification is no longer valid.");
        verified?.CheckRoot();
        var fork = prepared.Workspace;
        if (fork.preparedPresence!.Count != 0)
        {
            // A single edit can search thousands of absent model/texture candidates. On the owning thread,
            // check the project boundaries around the batch, not every mission's format for every candidate.
            CheckPresenceProject(token);
            foreach (var (relative, present) in fork.preparedPresence)
                if (DiskPresenceInCheckedProject(relative, token) != present) PresenceChanged(relative);
            CheckPresenceProject(token);
        }
        foreach (var (relative, baseline) in fork.preparedReads!)
        {
            token.ThrowIfCancellationRequested();
            SourceProject.RejectNestedLinks(Root, relative);
            if (!Matches(relative, baseline, token, verifyContent: verified == null)) throw new SourceFileChangedException($"{relative} changed while the edit was prepared; try again.", [relative]);
        }
        if (fork.nextId == prepared.FirstId) return null;
        if (fork.nextId != prepared.FirstId + 1 || fork.position != fork.history.Count)
            throw new InvalidOperationException("Prepare exactly one source transaction.");
        var transaction = fork.history[^1];
        foreach (var file in transaction.Files)
        {
            CheckEditable(file.Relative);
            if (EditGuard?.Invoke(file.Relative) is { } reason) throw new InvalidDataException(reason);
        }
        verified?.CheckRoot();
        return transaction;
    }

    public SourceWorkspace(string root, Saver? saver = null) : this(root, saver, MaximumHistoryBytes) { }

    internal SourceWorkspace(string root, Saver? saver, long maximumRetainedBytes)
    {
        if (maximumRetainedBytes is < 0 or > MaximumHistoryBytes) throw new ArgumentOutOfRangeException(nameof(maximumRetainedBytes));
        this.maximumRetainedBytes = maximumRetainedBytes;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!SourceProject.IsProject(Root)) throw new InvalidDataException("This folder is not a source project (it needs data and gamegen folders).");
        // Every later check of a file starts below Root; a linked root or ancestor would let edits and saves leave the folder.
        SourceProject.RejectLinkedProject(Root);
        SourceProject.RequireCurrentFormat(Root);
        save = saver ?? new Saver((writes, description, token, validation) => new SourcePublisher(Root).Publish(writes.Select(w => new SourceFileWrite(w.Relative, w.Expected, w.Content)).ToArray(), description, token, validation).Written);
    }

    /// <summary>A project-relative path with forward slashes, as builds and history key files.</summary>
    public static string Normalize(string relative) => relative.Replace('\\', '/');
    /// <summary>
    /// Only source files can be edited: under data or gamegen, inside the project, not through links and not in a protected
    /// corpus, with a path a save can write (<see cref="SourceProject.CheckSavablePath"/>) where the disk lets a save write it
    /// (<see cref="SourceProject.CheckSavableTarget"/>: no file used as a folder, no folder in its place). Every edit admits
    /// each of its files here, so whatever produced the path (an update from Blender, an added model, a copy, terrain or zone
    /// files, MCP or the GUI), a file no save could write is refused when the edit is made instead of blocking every later save.
    /// </summary>
    public void CheckEditable(string relative)
    {
        relative = Normalize(relative);
        SourceProject.CheckSavablePath(Root, relative);
        string path = SourceProject.Resolve(Root, relative);
        SourceProject.CheckSavableTarget(Root, relative);
        if (ProtectedPaths.IsProtected(path)) throw new InvalidDataException($"{relative} is inside the protected zbd_1998/zbd_1999 folders.");
    }

    /// <summary>
    /// The files a save would write once <paramref name="files"/> are accepted (with the project's other unsaved files) cannot
    /// include one inside another: the save would have to create one name as a file and a folder. Files on disk are
    /// checked by <see cref="CheckEditable"/>.
    /// </summary>
    private void CheckWrittenLayout(IReadOnlyList<SourceFileChange> files, CancellationToken token)
    {
        if (!files.Any(f => f.After != null)) return;
        HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);
        lock (gate) foreach (string relative in dirtyFiles) if (working[relative] != null) written.Add(relative);
        foreach (var file in files) if (file.After != null) written.Add(file.Relative); else written.Remove(file.Relative);
        HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in written)
        {
            token.ThrowIfCancellationRequested();
            for (int slash = relative.IndexOf('/'); slash > 0; slash = relative.IndexOf('/', slash + 1)) folders.Add(relative[..slash]);
        }
        foreach (var file in files)
        {
            if (file.After == null) continue;
            string shown = JsonData.ShownText(file.Relative, 256);
            if (folders.Contains(file.Relative))
            {
                string inner = written.First(w => w.StartsWith(file.Relative + "/", StringComparison.OrdinalIgnoreCase));
                throw new InvalidDataException($"'{shown}' would be a file, but the project's edits also write '{JsonData.ShownText(inner, 256)}' inside it as a folder, so the project cannot save both; choose another name.");
            }
            for (int slash = file.Relative.IndexOf('/'); slash > 0; slash = file.Relative.IndexOf('/', slash + 1))
                if (written.Contains(file.Relative[..slash]))
                    throw new InvalidDataException($"'{shown}' uses '{JsonData.ShownText(file.Relative[..slash], 256)}', a file the project's edits write, as a folder, so the project cannot save both; choose another folder.");
        }
    }

    /// <summary>
    /// The accepted content of a file: the workspace's when it holds unsaved edits for it, otherwise the file on disk now
    /// (null when absent). A clean file another program changed is read again, so an edit is never computed from old bytes.
    /// Treat returned buffers as immutable; submit replacement content through <see cref="Apply"/>.
    /// </summary>
    public byte[]? Read(string relative, CancellationToken token = default, long maximumBytes = Formats.FormatRegistry.MaximumDocumentBytes)
        => Read(relative, token, ProjectReadLimits.Bytes(maximumBytes));

    /// <summary>Read with the caller's byte and structural allowances, checked before payload allocation or hashing.</summary>
    public byte[]? Read(string relative, CancellationToken token, ProjectReadLimits limits)
        => ReadCore(relative, token, limits.MaximumBytes, limits.RequiresPrefix ? limits.CheckPrefix : null);

    internal byte[]? ReadModel(string relative, CancellationToken token, long maximumBytes, int maximumJsonBytes)
        => Read(relative, token, ProjectReadLimits.Model(maximumBytes, maximumJsonBytes));

    /// <summary>
    /// Hash accepted replacement targets without allocating their old disk payloads. One operation admits
    /// each path once (shared pending buffers once by identity), before hashing through its held handle.
    /// </summary>
    public IReadOnlyDictionary<string, string?> ReadEditHashes(IEnumerable<string> relatives, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<byte[], string> pending = new(ReferenceEqualityComparer.Instance);
        long remaining = maximumRetainedBytes;
        foreach (string raw in relatives)
        {
            token.ThrowIfCancellationRequested();
            string relative = Normalize(raw);
            if (result.ContainsKey(relative)) continue;
            CheckEditable(relative); CheckTrackedPresence(relative, token);
            byte[]? content = null; Baseline? baseline = null; bool dirty;
            lock (gate)
            {
                dirty = dirtyFiles.Contains(relative);
                if (dirty) { content = working[relative]; baseline = baselines[relative]; }
            }
            if (dirty)
            {
                string? hash = null;
                if (content != null && !pending.TryGetValue(content, out hash))
                {
                    Admit(content.LongLength); pending.Add(content, hash = SourceProject.Sha256(content));
                }
                preparedReads?.TryAdd(relative, baseline! with { Bytes = null });
                result.Add(relative, hash); continue;
            }
            string path = SourceProject.Resolve(Root, relative);
            SourceProject.RejectNestedLinks(Root, relative);
            Baseline? frozen = null; preparedReads?.TryGetValue(relative, out frozen);
            if (!SourceRead.FileExists(path))
            {
                if (frozen?.Stamp != null) Changed(relative);
                preparedReads?.TryAdd(relative, new(null, null)); result.Add(relative, null); continue;
            }
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            FileStamp Stamp() => new(stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
            var stamp = Stamp();
            Admit(stamp.Length); // Includes actual held length, before any payload read or digest work.
            if (frozen != null && frozen.Stamp != stamp) Changed(relative);
            var digest = JournalDigest.Of(stream, token);
            if (digest.Length != stamp.Length || Stamp() != stamp) Changed(relative);
            if (frozen != null && frozen.Sha256 != digest.Sha256) Changed(relative);
            preparedReads?.TryAdd(relative, new(null, stamp) { Sha256 = digest.Sha256 });
            result.Add(relative, digest.Sha256);
        }
        return result;

        void Admit(long bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytes < 0 || bytes > Formats.FormatRegistry.MaximumDocumentBytes || bytes > remaining)
                throw RetainedContentLimit(maximumRetainedBytes);
            remaining -= bytes;
        }
        static void Changed(string relative) => throw new SourceFileChangedException($"{relative} changed while the edit was prepared; try again.", [relative]);
    }

    private byte[]? ReadCore(string relative, CancellationToken token, long maximumBytes, SourceRead.Admission? admission)
    {
        token.ThrowIfCancellationRequested();
        relative = Normalize(relative);
        CheckTrackedPresence(relative, token);
        Baseline? baseline = null;
        lock (gate)
            if (working.TryGetValue(relative, out var bytes))
            {
                baseline = baselines[relative];
                if (dirtyFiles.Contains(relative))
                {
                    _ = BoundedRead(bytes);
                    if (bytes != null) admission?.Invoke(bytes.AsSpan(0, Math.Min(20, bytes.Length)), bytes.LongLength);
                    preparedReads?.TryAdd(relative, baseline with { Bytes = null }); return bytes;
                }
            }
        token.ThrowIfCancellationRequested();
        // A clean oversized old baseline must not be hashed first. The current disk file may have become smaller.
        if (admission == null && baseline != null && (baseline.Bytes == null || baseline.Bytes.LongLength <= maximumBytes) && Matches(relative, baseline, token))
        { preparedReads?.TryAdd(relative, baseline with { Bytes = null }); return BoundedRead(baseline.Bytes); }
        return ReadDisk(relative, maximumBytes, token, admission).Bytes;
        byte[]? BoundedRead(byte[]? value) => value == null || value.LongLength <= maximumBytes ? value
            : throw new InvalidDataException($"{relative} exceeds this operation's {maximumBytes:N0}-byte source limit; move inline buffers out and simplify metadata.");
    }
    /// <summary>Accepted presence, without reading or hashing payloads. Clean files follow current disk metadata.</summary>
    public bool Exists(string relative) => Exists(relative, CancellationToken.None);
    public bool Exists(string relative, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        relative = Normalize(relative);
        bool present = DiskPresence(relative, token);
        if (preparedPresence != null)
        {
            if (preparedPresence.TryGetValue(relative, out bool expected) && expected != present) PresenceChanged(relative);
            preparedPresence.TryAdd(relative, present);
        }
        lock (gate) return dirtyFiles.Contains(relative) ? working[relative] != null : present;
    }
    private bool DiskPresence(string relative, CancellationToken token)
    {
        CheckPresenceProject(token);
        return DiskPresenceInCheckedProject(relative, token);
    }
    private void CheckPresenceProject(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SourceProject.RejectLinkedProject(Root);
        SourceProject.RequireCurrentFormat(Root, token);
    }
    private bool DiskPresenceInCheckedProject(string relative, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = SourceProject.Resolve(Root, relative);
        SourceProject.RejectNestedLinks(Root, relative);
        token.ThrowIfCancellationRequested();
        return SourceRead.FileExists(path);
    }
    private void CheckTrackedPresence(string relative, CancellationToken token)
    {
        if (preparedPresence?.TryGetValue(relative, out bool expected) == true && DiskPresence(relative, token) != expected)
            PresenceChanged(relative);
    }
    private static void PresenceChanged(string relative) => throw new SourceFileChangedException(
        $"{JsonData.ShownText(relative, 256)} was added or removed while the edit was prepared; try again.", [relative]);
    /// <summary>Whether the workspace holds edits for a file that are not saved.</summary>
    public bool IsFileDirty(string relative) { lock (gate) return dirtyFiles.Contains(Normalize(relative)); }
    /// <summary>Files whose accepted content differs from the disk state the workspace last read or saved, in path order.</summary>
    public IReadOnlyList<string> DirtyFiles { get { lock (gate) return dirtyFiles.Order(StringComparer.OrdinalIgnoreCase).ToArray(); } }
    internal void TrackMapZoneGeometry(string relative) { lock (gate) mapZoneGeometry.Add(Normalize(relative)); }
    internal byte[]? ReadMapZoneSource(string relative, bool disk, long maximum, CancellationToken token)
    {
        var limits = ProjectReadLimits.Model().WithMaximum(maximum);
        return disk ? ReadDisk(relative, limits.MaximumBytes, token, limits.CheckPrefix).Bytes : Read(relative, token, limits);
    }
    internal byte[]? ReadMapZoneBuffer(string relative, bool disk, long maximum, CancellationToken token) => disk
        ? ReadDisk(relative, Math.Min(maximum, Formats.FormatRegistry.MaximumDocumentBytes), token).Bytes
        : Read(relative, token, ProjectReadLimits.Bytes(maximum));
    internal void TrackMapZoneSources(IReadOnlyCollection<string> owners, IReadOnlyList<(string Relative, byte[] Content)> proposed, CancellationToken token)
    {
        var replacements = proposed.ToDictionary(p => p.Relative, p => p.Content, StringComparer.OrdinalIgnoreCase);
        string[] assigned = owners.Where(p => replacements.TryGetValue(p, out var content)
            && SourceMapZoneBindings.AssignmentChanges(Read(p, token, ProjectReadLimits.Model()), content, token)).ToArray();
        var closures = SourceMapZoneBindings.SourceClosures(this, assigned, proposed, token);
        var sources = SourceMapZoneBindings.GeometryIdentities(this, closures.Values.SelectMany(p => p), true, token, replacements);
        Dictionary<string, IReadOnlyDictionary<string, SourceMapZoneBindings.GeometrySource>> captured = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (owner, dependencies) in closures)
        {
            var next = dependencies.ToDictionary(p => p, p => sources[p], StringComparer.OrdinalIgnoreCase);
            if (IsFileDirty(owner) && mapZoneSources.TryGetValue(owner, out var expected))
            {
                foreach (string path in next.Keys.ToArray())
                    if (expected.TryGetValue(path, out var previous))
                    {
                        var source = next[path];
                        RequireSameGeometry(path, previous, source, allowNewBuffers: true);
                        var buffers = new Dictionary<string, string?>(source.Buffers, StringComparer.OrdinalIgnoreCase);
                        foreach (var buffer in previous.Buffers) buffers.TryAdd(buffer.Key, buffer.Value);
                        next[path] = source with { Buffers = buffers };
                    }
                foreach (var pair in expected) next.TryAdd(pair.Key, pair.Value);
            }
            captured.Add(owner, next);
        }
        CheckZoneSourceSize(mapZoneSources.Where(p => !captured.ContainsKey(p.Key)).Concat(captured));
        foreach (var pair in captured) mapZoneSources[pair.Key] = pair.Value;
    }
    private static void CheckZoneSourceSize(IEnumerable<KeyValuePair<string, IReadOnlyDictionary<string, SourceMapZoneBindings.GeometrySource>>> owners)
    {
        long remaining = Gltf.GltfDocument.MaximumMetadataBytes;
        HashSet<SourceMapZoneBindings.GeometrySource> seen = new(ReferenceEqualityComparer.Instance);
        foreach (var owner in owners)
        {
            Charge(96L + owner.Key.Length * 2L);
            foreach (var (path, source) in owner.Value)
            {
                Charge(96L + path.Length * 2L);
                if (!seen.Add(source)) continue;
                Charge(96L + 64 * 2L);
                foreach (var buffer in source.Buffers) Charge(96L + (buffer.Key.Length + 64) * 2L);
            }
        }
        void Charge(long bytes)
        {
            if (bytes > remaining) throw new InvalidDataException("Retained map geometry dependencies exceed their metadata allowance; reopen the project before editing more source graphs.");
            remaining -= bytes;
        }
    }
    private static void RequireSameGeometry(string path, SourceMapZoneBindings.GeometrySource expected, SourceMapZoneBindings.GeometrySource current, bool allowNewBuffers = false)
    {
        if (expected.Identity != current.Identity || current.Buffers.Any(p => expected.Buffers.TryGetValue(p.Key, out var previous) ? previous != p.Value : !allowNewBuffers))
            throw new SourceFileChangedException($"Geometry used by pending map assignments changed outside this edit: {JsonData.ShownText(path, 256)}. Reopen the project and redo the assignment against its current geometry.", [path]);
    }
    internal void TrackMapZoneOwners(IReadOnlyCollection<string> geometries, CancellationToken token)
    {
        var incoming = SourceMapZoneBindings.IncomingOwners(this, geometries, true, token);
        foreach (var (geometry, owners) in incoming)
        {
            if (IsFileDirty(geometry) && mapZoneOwners.TryGetValue(geometry, out var expected)) RequireSameOwners(geometry, expected, owners);
            mapZoneOwners[geometry] = owners;
        }
    }
    internal void CopyMapZoneOwnersFrom(SourceWorkspace prepared, IReadOnlyCollection<string> geometries)
    {
        foreach (string geometry in geometries)
        {
            if (prepared.mapZoneOwners.TryGetValue(geometry, out var owners)) mapZoneOwners[geometry] = owners;
            if (prepared.mapZoneSources.TryGetValue(geometry, out var sources)) mapZoneSources[geometry] = sources;
        }
    }
    private static void RequireSameOwners(string geometry, IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> current)
    {
        if (expected.Count != current.Count || expected.Any(p => !current.TryGetValue(p.Key, out string? digest) || digest != p.Value))
            throw new SourceFileChangedException($"Placement assignments referencing {JsonData.ShownText(geometry, 256)} changed outside this edit. Reload the project and update from Blender again before saving.", [geometry]);
    }
    public bool IsDirty { get { lock (gate) return dirtyFiles.Count != 0; } }
    /// <summary>The accepted content of every changed file, for builds to read instead of the disk.</summary>
    public IReadOnlyDictionary<string, byte[]> Overlay()
    {
        Dictionary<string, byte[]> overlay = new(StringComparer.OrdinalIgnoreCase);
        lock (gate)
            foreach (var (relative, bytes) in working)
                if (bytes != null && dirtyFiles.Contains(relative)) overlay[relative] = bytes;
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
        var allowance = ContentAllowance(token);
        Dictionary<string, Baseline> refreshed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, content) in changes)
        {
            token.ThrowIfCancellationRequested();
            string relative = Normalize(raw);
            if (!seen.Add(relative)) throw new InvalidDataException($"{relative} is changed twice in one edit.");
            CheckEditable(relative);
            if (EditGuard?.Invoke(relative) is { } refused) throw new InvalidDataException(refused);
            if (content != null && content.Length > Formats.FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException($"{relative} would exceed 512 MiB.");
            allowance.Add(content);
            var baseline = Refresh(relative, allowance, token);
            byte[]? before;
            lock (gate)
            {
                bool changed = !baselines.TryGetValue(relative, out var previous) || !ReferenceEquals(previous, baseline);
                if (changed) refreshed.Add(relative, baseline);
                before = !changed && working.TryGetValue(relative, out var current) ? current : baseline.Bytes;
            }
            if (Same(before, content)) continue;
            // A build reads the overlay in place of the disk; a file the disk still holds cannot be hidden from it yet.
            if (content == null && baseline.Bytes != null) throw new NotSupportedException($"Deleting {relative} from the project is not supported.");
            files.Add(new(relative, before, content));
        }
        token.ThrowIfCancellationRequested();
        CheckWrittenLayout(files, token);
        // Do not publish even clean baselines from a partially admitted transaction.
        lock (gate)
            foreach (var (relative, baseline) in refreshed)
            { baselines[relative] = baseline; working.Remove(relative); dirtyFiles.Remove(relative); }
        if (files.Count == 0) return null;
        return Commit(label, files);
    }
    private SourceTransaction Commit(string label, IReadOnlyList<SourceFileChange> files)
    {
        SourceTransaction transaction = new(nextId++, label, files);
        lock (gate)
        {
            SourceTransaction[] redo = [.. history.Skip(position)];
            history.RemoveRange(position, history.Count - position);
            history.Add(transaction); position++;
            foreach (var file in files) SetWorking(file.Relative, file.After);
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
        // Builds cannot hide saved files. Ordinary created sources therefore still prevent undo,
        // but an immutable content-addressed glTF buffer can remain as an unused dependency.
        // Restoring the owning glTF restores its original buffer references without deleting it.
        Dictionary<string, byte[]> retained = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in transaction.Files.Where(f => f.Before == null))
        {
            var baseline = BaselineOf(file.Relative);
            if (baseline.Bytes == null) continue;
            string stem = Path.GetFileNameWithoutExtension(file.Relative);
            bool contentAddressed = Path.GetExtension(file.Relative).Equals(".bin", StringComparison.OrdinalIgnoreCase)
                && stem.Length > 65 && stem[^65] == '.' && stem[^64..].Equals(baseline.Sha256, StringComparison.OrdinalIgnoreCase)
                && Same(file.After, baseline.Bytes);
            if (!contentAddressed)
                throw new NotSupportedException($"{file.Relative} was created by {transaction.Label} and saved since; undoing it would delete the file, which the workspace does not do. Delete it by hand.");
            if (!Matches(file.Relative, baseline))
                throw new SourceFileChangedException("A saved geometry buffer changed outside the workspace; reload the project before undoing.", [file.Relative]);
            retained.Add(file.Relative, baseline.Bytes);
        }
        lock (gate) { foreach (var file in transaction.Files) SetWorking(file.Relative, retained.TryGetValue(file.Relative, out var bytes) ? bytes : file.Before); position--; displaced = null; }
        return Publish("undo", transaction.Label, transaction.Files.Select(f => f.Relative));
    }
    public SourceWorkspaceChange Redo()
    {
        if (!CanRedo) throw new InvalidOperationException("Nothing to redo.");
        var transaction = history[position];
        Guard(transaction);
        lock (gate) { foreach (var file in transaction.Files) SetWorking(file.Relative, file.After); position++; displaced = null; }
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
    private static InvalidDataException RetainedContentLimit(long maximum) => new(
        $"Source edits exceed the {maximum:N0}-byte retained-content allowance, including old files and undo history. Use smaller replacement files or save and reopen the project before retrying.");

    private sealed class EditContentAllowance(long maximum, CancellationToken token)
    {
        private readonly HashSet<byte[]> held = new(ReferenceEqualityComparer.Instance);
        private readonly long maximumBytes = maximum;
        internal long Remaining { get; private set; } = maximum;
        internal void Add(byte[]? bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytes == null || held.Contains(bytes)) return;
            if (bytes.LongLength > Remaining) throw RetainedContentLimit(maximumBytes);
            Remaining -= bytes.LongLength; held.Add(bytes);
        }
    }
    // Caller holds gate. History/rollback can reference the same immutable content many times.
    private IEnumerable<byte[]> RetainedContent()
    {
        foreach (var bytes in parentContent) yield return bytes;
        foreach (var baseline in baselines.Values) if (baseline.Bytes != null) yield return baseline.Bytes;
        foreach (var bytes in working.Values) if (bytes != null) yield return bytes;
        foreach (var transaction in history) foreach (var file in transaction.Files)
        { if (file.Before != null) yield return file.Before; if (file.After != null) yield return file.After; }
        if (displaced is { } previous)
            foreach (var transaction in previous.Redo.Concat(previous.Trimmed)) foreach (var file in transaction.Files)
            { if (file.Before != null) yield return file.Before; if (file.After != null) yield return file.After; }
    }
    private EditContentAllowance ContentAllowance(CancellationToken token)
    {
        EditContentAllowance allowance = new(maximumRetainedBytes, token);
        lock (gate) foreach (var bytes in RetainedContent()) allowance.Add(bytes);
        return allowance;
    }
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
            foreach (var file in transaction.Files) SetWorking(file.Relative, file.Before);
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
    public IReadOnlyList<string> ExternalChanges() => ExternalChanges(CancellationToken.None);
    /// <summary>Content checking is the default; metadata-only checks are preliminary UI guards, never publication evidence.
    /// A dependency set limits checks before disk access; Reload omits it to check all touched baselines.
    /// Save separately verifies the dirty files it will write through the publisher.</summary>
    public IReadOnlyList<string> ExternalChanges(CancellationToken token, bool verifyContent = true, IReadOnlyCollection<string>? dependencies = null)
    {
        List<string> changed = []; KeyValuePair<string, Baseline>[] entries;
        var selected = dependencies?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (gate) entries = [.. baselines.Where(p => selected == null || selected.Contains(p.Key))];
        foreach (var (relative, baseline) in entries)
            // A folder that appeared where the workspace knows no file is a change as well: a save refuses to replace it,
            // so Reload reports it (a conflict while its new file is unsaved) instead of finding nothing to reload.
            if (!Matches(relative, baseline, token, verifyContent)
                || baseline.Stamp == null && baseline.Sha256 == null && SourceRead.PathExists(SourceProject.Resolve(Root, relative))) changed.Add(relative);
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
    private sealed record PendingSave(IReadOnlyList<string> Dirty, (string Relative, byte[]? Expected, byte[]? Content)[] Writes,
        string[] Geometry, string[] Profiles, PreparedEdit? Validation)
    {
        internal Dictionary<string, IReadOnlyDictionary<string, string>>? OwnersAfter { get; set; }
        internal Dictionary<string, IReadOnlyDictionary<string, SourceMapZoneBindings.GeometrySource>>? SourcesAfter { get; set; }
    }
    /// <summary>Takes the dirty files and marks the workspace saving; null when nothing is dirty.</summary>
    private PendingSave? BeginSave()
    {
        if (IsSaving) throw new InvalidOperationException("A save is already running.");
        var dirty = DirtyFiles;
        if (dirty.Count == 0) return null;
        (string Relative, byte[]? Expected, byte[]? Content)[] writes;
        string[] geometry;
        lock (gate)
        {
            writes = dirty.Select(f => (f, baselines[f].Bytes, working[f])).ToArray();
            geometry = dirty.Where(mapZoneGeometry.Contains).ToArray();
        }
        // Any glTF can own attributes for its referenced placements. Save verifies both directions:
        // the edited file's outgoing assignments and other files' assignments on edited geometry.
        string[] profiles = dirty.Where(f => f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)).ToArray();
        var validation = geometry.Length == 0 && profiles.Length == 0 ? null : BeginPreparedEdit();
        IsSaving = true;
        return new(dirty, writes, geometry, profiles, validation);
    }
    /// <summary>Publishes a save's files and reads them back; touches no workspace state, so it may run on any thread.</summary>
    private (IReadOnlyList<string> Written, Baseline[] Saved) Write(PendingSave pending, CancellationToken token)
    {
        bool validated = false;
        var written = save(pending.Writes, $"Save {pending.Dirty.Count} source file{(pending.Dirty.Count == 1 ? "" : "s")}", token, Validate);
        if (!validated) throw new InvalidOperationException("The source saver did not run publication validation; the workspace remains unsaved.");
        // The files are on disk now; another program may already have replaced one, so each is read back with its stamp.
        Baseline[] saved = [.. pending.Writes.Select(w => Saved(w.Relative, w.Content))];
        return (written, saved);

        SourcePublishValidation Validate(CancellationToken validationToken)
        {
            if (validated) throw new InvalidOperationException("A source save can be validated only once.");
            var verified = VerifyMapZonesForSave(pending, validationToken);
            try
            {
                // Read-only inputs stay held; absent dependencies use the publisher's checks through commit.
                var checks = pending.Validation?.Workspace.preparedReads!
                    .Where(p => p.Value.Stamp == null && p.Value.Sha256 == null)
                    .Select(p => new SourceFileWrite(p.Key, null, null)).ToArray() ?? [];
                validated = true;
                return new(verified, checks);
            }
            catch { verified?.Dispose(); throw; }
        }
    }
    private PreparedValidation? VerifyMapZonesForSave(PendingSave pending, CancellationToken token)
    {
        if (pending.Validation is not { } validation) return null;
        var snapshot = validation.Workspace;
        string[] sourceOwners = pending.Writes.Where(p => snapshot.mapZoneSources.ContainsKey(p.Relative)
            && SourceMapZoneBindings.AssignmentChanges(p.Expected, p.Content, token)).Select(p => p.Relative).ToArray();
        var closures = SourceMapZoneBindings.SourceClosures(snapshot, sourceOwners, [], token);
        var future = pending.Writes.Where(p => p.Content != null).ToDictionary(p => p.Relative, p => p.Content!, StringComparer.OrdinalIgnoreCase);
        var sourceNow = SourceMapZoneBindings.GeometryIdentities(snapshot, closures.Values.SelectMany(p => p), true, token, future);
        foreach (var (owner, dependencies) in closures)
        foreach (string path in dependencies)
        {
            if (!snapshot.mapZoneSources[owner].TryGetValue(path, out var expected))
                throw new SourceFileChangedException("The pending map assignments have a new geometry dependency; replan the edit before saving.", [owner, path]);
            RequireSameGeometry(path, expected, sourceNow[path]);
        }
        var sourceAfter = SourceMapZoneBindings.GeometryIdentities(snapshot, snapshot.mapZoneSources.Values.SelectMany(p => p.Keys), false, token);
        var writtenInputs = pending.Dirty.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<SourceMapZoneBindings.GeometrySource, SourceMapZoneBindings.GeometrySource> refreshed = new(ReferenceEqualityComparer.Instance);
        pending.SourcesAfter = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (owner, previousSources) in snapshot.mapZoneSources)
        {
            Dictionary<string, SourceMapZoneBindings.GeometrySource> next = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, previous) in previousSources)
            {
                var current = sourceAfter[path];
                if (!writtenInputs.Contains(path) && !current.Buffers.Keys.Any(writtenInputs.Contains)) { next[path] = previous; continue; }
                if (!refreshed.TryGetValue(previous, out var replacement))
                {
                    var buffers = new Dictionary<string, string?>(previous.Buffers, StringComparer.OrdinalIgnoreCase);
                    foreach (var buffer in current.Buffers)
                        if (writtenInputs.Contains(buffer.Key)) buffers[buffer.Key] = buffer.Value;
                    refreshed.Add(previous, replacement = new(writtenInputs.Contains(path) ? current.Identity : previous.Identity, buffers));
                }
                next[path] = replacement;
            }
            pending.SourcesAfter.Add(owner, next);
        }
        CheckZoneSourceSize(pending.SourcesAfter);
        string[] guarded = pending.Geometry.Where(snapshot.mapZoneOwners.ContainsKey).ToArray();
        var incoming = SourceMapZoneBindings.IncomingOwners(snapshot, guarded, true, token);
        foreach (var (geometry, owners) in incoming) RequireSameOwners(geometry, snapshot.mapZoneOwners[geometry], owners);
        pending.OwnersAfter = SourceMapZoneBindings.IncomingOwners(snapshot, snapshot.mapZoneOwners.Keys.ToArray(), false, token);
        foreach (string profile in pending.Profiles)
        {
            // The other save order matters too: a pending zone edit must not publish assignments for geometry
            // another editor has changed since acceptance. Decode one geometry at a time, not a whole map scene.
            if (snapshot.Exists(profile, token)) SourceMapZoneBindings.ValidateOwned(snapshot, profile, token);
        }
        foreach (string geometry in pending.Geometry)
        {
            token.ThrowIfCancellationRequested();
            var bindings = SourceMapZoneBindings.Read(snapshot, geometry, null, token);
            if (!bindings.Any) continue;
            bindings.Validate(ReadGeometry(geometry), token);
        }
        // The publisher verifies and replaces these files. Holding their read handles would prevent replacement;
        // hold only the other semantic inputs, with the existing prepared checks for absent profiles and buffers.
        foreach (var write in pending.Writes)
        {
            snapshot.preparedReads!.Remove(write.Relative);
            snapshot.preparedPresence!.Remove(write.Relative);
        }
        return VerifyPreparedEdit(validation, token);

        Gltf.GltfDocument ReadGeometry(string geometry) => WorldAssembler.ReadModel(snapshot.Read(geometry, token, ProjectReadLimits.Model())
            ?? throw new InvalidDataException($"{JsonData.ShownText(geometry)} is unavailable for map zone validation."), geometry,
            (path, allowance) => snapshot.Read(path, token, ProjectReadLimits.Bytes(allowance))
                ?? throw new InvalidDataException($"{JsonData.ShownText(geometry)}: buffer {JsonData.ShownText(path)} is unavailable."), token);
    }
    private IReadOnlyList<string> EndSave(PendingSave pending, (IReadOnlyList<string> Written, Baseline[] Saved) published)
    {
        lock (gate)
        {
            for (int i = 0; i < pending.Writes.Length; i++)
            {
                string relative = pending.Writes[i].Relative;
                baselines[relative] = published.Saved[i];
                SetWorking(relative, working[relative]);
            }
            if (pending.OwnersAfter is { } snapshots)
            {
                var savedOwners = pending.Dirty.Select(p => p.ToUpperInvariant() + "|").ToArray();
                foreach (var (geometry, after) in snapshots)
                {
                    if (pending.Geometry.Contains(geometry, StringComparer.OrdinalIgnoreCase)) { mapZoneOwners[geometry] = after; continue; }
                    // An owner-only save acknowledges only assignments this workspace actually wrote.
                    // Never bless another program's new/changed owner during an unrelated save.
                    var updated = new Dictionary<string, string>(mapZoneOwners[geometry], StringComparer.OrdinalIgnoreCase);
                    bool Written(string key) => savedOwners.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                    foreach (string key in updated.Keys.Where(Written).ToArray()) updated.Remove(key);
                    foreach (var pair in after.Where(p => Written(p.Key))) updated[pair.Key] = pair.Value;
                    mapZoneOwners[geometry] = updated;
                }
            }
            if (pending.SourcesAfter is { } identities)
                foreach (var pair in identities) mapZoneSources[pair.Key] = pair.Value;
        }
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
        lock (gate) { working.Clear(); dirtyFiles.Clear(); mapZoneGeometry.Clear(); mapZoneOwners.Clear(); mapZoneSources.Clear(); baselines.Clear(); history.Clear(); position = 0; displaced = null; }
        return Publish("discard", "Discard", files);
    }

    /// <summary>
    /// Re-reads files changed on disk that the workspace holds no unsaved edits for, so it serves their new content; a file
    /// with unsaved edits that changed on disk is a conflict. History is cleared when any file is re-read, since its steps
    /// no longer describe the files. Returns the re-read files.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReloadAsync(CancellationToken token = default)
    {
        long revision = Revision;
        var changed = await Task.Run(() => ExternalChanges(token), token);
        token.ThrowIfCancellationRequested();
        if (Revision != revision) throw new InvalidOperationException("The source workspace changed during reload; try again.");
        return ReloadChanged(changed);
    }
    public IReadOnlyList<string> Reload() => ReloadChanged(ExternalChanges());
    private IReadOnlyList<string> ReloadChanged(IReadOnlyList<string> changed)
    {
        if (IsSaving) throw new InvalidOperationException("Wait for the save to finish.");
        var conflicts = changed.Where(IsFileDirty).ToArray();
        if (conflicts.Length > 0) throw SourceFileChangedException.ForReload(conflicts);
        if (changed.Count == 0) return [];
        lock (gate) { foreach (string relative in changed) { baselines.Remove(relative); working.Remove(relative); dirtyFiles.Remove(relative); } history.Clear(); position = 0; displaced = null; }
        Publish("reload", "Reload", changed);
        return changed;
    }

    /// <summary>Reads a touched file's baseline once; a clean file that changed on disk since is re-read, a dirty one is a conflict.</summary>
    private Baseline Refresh(string relative, EditContentAllowance allowance, CancellationToken token)
    {
        CheckTrackedPresence(relative, token);
        Baseline? baseline; lock (gate) baselines.TryGetValue(relative, out baseline);
        if (baseline == null) return Read();
        if (Matches(relative, baseline, token)) { preparedReads?.TryAdd(relative, baseline with { Bytes = null }); return baseline; }
        if (IsFileDirty(relative)) throw new SourceFileChangedException($"{relative} changed on disk while the workspace holds unsaved edits for it; save elsewhere or discard the edits first.", [relative]);
        // History steps of this file no longer describe it; keeping them would undo another program's edit.
        if (history.Any(t => t.Files.Any(f => f.Relative.Equals(relative, StringComparison.OrdinalIgnoreCase))))
            throw new SourceFileChangedException($"{relative} changed on disk since the workspace edited it; reload the project's worlds to continue from the file.", [relative]);
        return Read();
        Baseline Read()
        {
            var read = ReadDisk(relative, Math.Min(Formats.FormatRegistry.MaximumDocumentBytes, allowance.Remaining), token);
            allowance.Add(read.Bytes); return read;
        }
    }
    private Baseline BaselineOf(string relative) { lock (gate) return baselines[relative]; }
    private bool Matches(string relative, Baseline baseline, CancellationToken token = default, bool verifyContent = true)
    {
        token.ThrowIfCancellationRequested();
        string path = SourceProject.Resolve(Root, relative);
        bool exists = SourceRead.FileExists(path);
        if (!exists) return baseline.Stamp == null && baseline.Sha256 == null;
        if (baseline.Stamp == null) return false;
        try
        {
            SourceProject.RejectNestedLinks(Root, relative);
            return FileStamp.Read(path) == baseline.Stamp && baseline.Sha256 != null
                && (!verifyContent || SourceRead.Matches(path, baseline.Stamp.Length, baseline.Sha256, token))
                && FileStamp.Read(path) == baseline.Stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private Baseline ReadDisk(string relative, long maximumBytes = Formats.FormatRegistry.MaximumDocumentBytes, CancellationToken token = default, SourceRead.Admission? admission = null)
    {
        CheckTrackedPresence(relative, token);
        Baseline? frozen = null;
        if (preparedReads?.TryGetValue(relative, out frozen) == true)
        {
            if (frozen.Stamp?.Length > maximumBytes)
                throw new InvalidDataException($"{relative} exceeds this operation's {maximumBytes:N0}-byte source limit; simplify the source before retrying.");
            if (admission == null && !Matches(relative, frozen, token)) Changed();
        }
        string path = SourceProject.Resolve(Root, relative);
        SourceProject.RejectNestedLinks(Root, relative);
        if (!SourceRead.FileExists(path))
        {
            if (frozen?.Stamp != null) Changed();
            Baseline absent = new(null, null); preparedReads?.TryAdd(relative, absent); return absent;
        }
        var stamp = FileStamp.Read(path);
        if (frozen != null && frozen.Stamp != stamp) Changed();
        if (stamp.Length > maximumBytes) throw new InvalidDataException($"{relative} exceeds this operation's {maximumBytes:N0}-byte source limit; move inline buffers out and simplify metadata.");
        byte[] bytes = admission == null ? SourceRead.All(path, maximumBytes, token)
            : SourceRead.AllAdmitted(path, maximumBytes, admission, token, frozen == null ? null : Verify);
        if (FileStamp.Read(path) != stamp) throw new IOException($"{relative} changed while it was read; try again.");
        Baseline read = new(bytes, stamp); preparedReads?.TryAdd(relative, read with { Bytes = null }); return read;
        void Verify(Stream stream)
        {
            if (frozen?.Sha256 == null || !new JournalDigest(stamp.Length, frozen.Sha256).Matches(stream, token)) Changed();
        }
        void Changed() => throw new SourceFileChangedException($"{relative} changed while the edit was prepared; try again.", [relative]);
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
    private void SetWorking(string relative, byte[]? bytes)
    {
        working[relative] = bytes;
        if (Same(bytes, baselines[relative].Bytes)) dirtyFiles.Remove(relative);
        else dirtyFiles.Add(relative);
    }
    private static bool Same(byte[]? a, byte[]? b) => ReferenceEquals(a, b) || a != null && b != null && a.AsSpan().SequenceEqual(b);
}
