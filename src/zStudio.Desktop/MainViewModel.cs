using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly StringComparer DisplayPathComparer = CultureInfo.InvariantCulture.CompareInfo
        .GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
    public ObservableCollection<FolderNode> Folders { get; } = [];
    public ObservableCollection<DocumentModel> Documents { get; } = [];
    public ObservableCollection<SearchHit> SearchResults { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];
    public ObservableCollection<StudioProblem> Problems { get; } = [];
    /// <summary>Replace a remembered MW3 mission that no longer qualified with the reported fallback.</summary>
    internal void AdoptMissionFallback(string worldPath, MissionLayoutSelection layout)
    {
        if (layout.UnavailableMission is not { } stale || layout.MissionArchive is not { } effective) return;
        if (!Settings.Mw3Missions.TryGetValue(worldPath, out var saved) || !Path.GetFullPath(saved).Equals(stale, StringComparison.OrdinalIgnoreCase)) return;
        Settings.Mw3Missions[worldPath] = effective;
        try { Settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddProblem("Could not save the mission selection: " + ex.Message); }
    }
    public void AddProblem(string message, string severity = "Error", string? file = null, int? assetIndex = null, long? offset = null)
    {
        Diagnostics.Add(message); Problems.Add(new(severity, "File / operation", message, file, assetIndex, offset));
    }
    public List<FileEntry> Files { get; private set; } = [];
    public AssetResolver? Resolver { get; private set; }
    public StudioSettings Settings { get; } = StudioSettings.Load();
    public static MissionDifficulty[] DifficultyChoices { get; } = Enum.GetValues<MissionDifficulty>();
    [ObservableProperty] private MissionDifficulty difficulty = MissionDifficulty.Medium;
    private readonly Dictionary<string,FolderNode> fileNodes = new(StringComparer.OrdinalIgnoreCase);
    private FolderNode? otherOpenFiles;
    private long navigationGeneration;
    private bool disposed;
    internal long NavigationGeneration => navigationGeneration;
    internal long WorkspaceGeneration { get; private set; }
    internal long WorkspaceNavigationGeneration { get; private set; }
    internal Func<string, CancellationToken, Task<ZbdDocument>> LoadDocumentAsync { get; set; } =
        static (path, token) => Task.Run(() => FormatRegistry.Default.OpenAsync(path, token), token).WaitAsync(token);
    internal Func<string, CancellationToken, Task<bool>> CheckRootExistsAsync { get; set; } =
        static (path, token) => Task.Run(() => Directory.Exists(path), token).WaitAsync(token);
    public MainViewModel()
    {
        difficulty = Settings.Difficulty;
        Documents.CollectionChanged += (_,_) => { ++navigationGeneration; SynchronizeOpenFiles(); };
    }
    partial void OnSelectedDocumentChanging(DocumentModel? value)
    { if (SelectedDocument != null) SelectedDocument.PropertyChanged -= SelectedDocumentSelectionChanged; }
    partial void OnSelectedDocumentChanged(DocumentModel? value)
    {
        ++navigationGeneration;
        if (value != null) value.PropertyChanged += SelectedDocumentSelectionChanged;
        SynchronizeOpenFiles();
    }
    private void SelectedDocumentSelectionChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(DocumentModel.SelectedAsset)) ++navigationGeneration; }
    private void RequireCurrentNavigation(long generation)
    {
        if (disposed || generation != navigationGeneration)
            throw new StudioCommandException("context_changed", "A newer workspace navigation superseded this request.");
    }
    internal async Task EnsureRootForFileAsync(string path, bool forceRoot = false, CancellationToken cancellationToken = default, Action? beforePublish = null)
    {
        if (HasRoot && !forceRoot) return;
        long expectedWorkspace = WorkspaceGeneration + 1;
        await OpenRootAsync(Path.GetDirectoryName(Path.GetFullPath(path))!, cancellationToken, beforePublish);
        if (WorkspaceGeneration != expectedWorkspace || NavigationGeneration != WorkspaceNavigationGeneration || SelectedDocument != null)
            throw new StudioCommandException("context_changed", "The user navigated elsewhere while the workspace was opening.");
    }
    private void SynchronizeOpenFiles()
    {
        foreach (var node in fileNodes.Values)
        {
            node.Document = Documents.FirstOrDefault(d => d.Path.Equals(node.Path,StringComparison.OrdinalIgnoreCase));
            node.IsActive = node.Document != null && node.Document == SelectedDocument;
        }
        foreach (var doc in Documents.Where(d => !fileNodes.ContainsKey(d.Path)))
        {
            if (otherOpenFiles == null) { otherOpenFiles = new("Other open files", "Files opened outside the selected root"); Folders.Add(otherOpenFiles); }
            var file = new FileEntry(doc.Path,doc.Path,doc.Document.Probe);
            FolderNode node = new(doc.SourceWorld?.Label ?? Path.GetFileName(Path.GetDirectoryName(doc.Path)) + "/" + file.Name,doc.Path,file) { Document = doc, IsActive = doc == SelectedDocument };
            fileNodes.Add(doc.Path,node); otherOpenFiles.Children.Add(node);
        }
        if (otherOpenFiles != null)
        {
            foreach (var node in otherOpenFiles.Children.Where(n => !n.IsOpen).ToArray()) { otherOpenFiles.Children.Remove(node); fileNodes.Remove(node.Path); }
            if (otherOpenFiles.Children.Count == 0) { Folders.Remove(otherOpenFiles); otherOpenFiles = null; }
        }
    }
    partial void OnDifficultyChanged(MissionDifficulty value)
    {
        Settings.Difficulty = value;
        try { Settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddProblem("Could not save mission difficulty: " + ex.Message); }
    }
    public Func<DocumentModel, Task<bool>>? ConfirmDiscardAsync { get; set; }
    /// <summary>Raised once a root (also the same one again) is open, with its path and HasRoot set.</summary>
    public event Action? RootPublished;
    /// <summary>Resolves pending GUI input before close decisions: committing it can rebuild (replace) a source world.</summary>
    public Func<Task<bool>>? ResolveDraftsAsync { get; set; }
    /// <summary>A new set of close decisions starts: decisions left from an earlier, unfinished one no longer apply.</summary>
    public Action? CloseDecisionsStarting { get; set; }
    /// <summary>Every document is being closed (a root change), so a project's edits cannot stay with another of its documents.</summary>
    internal bool ClosingAllDocuments { get; private set; }
    internal Action<bool>? ValidateNavigationPublication { get; set; }
    private CancellationTokenSource workspace = new();
    /// <summary>Canceled when the workspace root is replaced; long operations owned by a workspace link to it.</summary>
    internal CancellationToken WorkspaceToken => workspace.Token;
    private readonly List<SearchHit> index = [];
    [ObservableProperty] private string rootPath = "Open a ZBD folder to start exploring";
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private DocumentModel? selectedDocument;
    [ObservableProperty] private string globalQuery = "";
    [ObservableProperty] private bool hasRoot;
    [ObservableProperty] private bool searchIsLimited;
    public async Task OpenRootAsync(string root, CancellationToken cancellationToken = default, Action? beforePublish = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (beforePublish == null && ResolveDraftsAsync != null && !await ResolveDraftsAsync()) return;
        long generation = ++navigationGeneration;
        RequireCurrentNavigation(generation);
        root = Path.GetFullPath(root);
        // Directory checks can block on unavailable network shares. Keep that work off
        // the dispatcher and abandon the wait on shutdown; it has no workspace effects.
        bool exists;
        using (var preflight = CancellationTokenSource.CreateLinkedTokenSource(workspace.Token, cancellationToken))
        {
            try { exists = await CheckRootExistsAsync(root, preflight.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new StudioCommandException("context_changed", "The workspace changed while checking the requested folder."); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        RequireCurrentNavigation(generation);
        if (!exists) throw new DirectoryNotFoundException(root);
        Dictionary<DocumentModel, long> acceptedRevisions = [];
        if (beforePublish == null)
            try
            {
                ClosingAllDocuments = true;
                CloseDecisionsStarting?.Invoke();
                foreach (var document in Documents.ToArray())
                {
                    if (!await CanRemoveAsync(document)) return;
                    cancellationToken.ThrowIfCancellationRequested(); RequireCurrentNavigation(generation);
                    var decided = Replacement(document);
                    if (!decided.IsDisposed && Documents.Contains(decided)) acceptedRevisions[decided] = decided.Revision;
                }
            }
            finally { ClosingAllDocuments = false; }
        cancellationToken.ThrowIfCancellationRequested();
        if (acceptedRevisions.Any(pair => pair.Key.IsDisposed || pair.Key.Revision != pair.Value))
            throw new StudioCommandException("revision_conflict", "A document changed after its close decision. Its current edits were retained.");
        ValidateNavigationPublication?.Invoke(true);
        beforePublish?.Invoke(); RequireCurrentNavigation(generation);
        long committedWorkspace = ++WorkspaceGeneration;
        workspace.Cancel(); workspace.Dispose(); workspace = new(); var workspaceToken = workspace.Token;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(workspaceToken, cancellationToken);
        var token = cancellation.Token;
        foreach (var doc in Documents) doc.Dispose(); Documents.Clear(); SelectedDocument = null;
        // A previous asynchronous operation may still hold its resolver; its
        // canceled task owns that short remaining lifetime, not the new workspace.
        Resolver = new AssetResolver(root);
        // Check the cheap root prefix first: remembered maps on unavailable shares must not
        // stall the UI thread. A remembered reader is seeded even if it was deleted (SelectMission
        // keeps it in the map directory), so loading reports the fallback and replaces the setting.
        string rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var (map, mission) in (Settings.Mw3Missions ?? []).Take(128))
            try { if (Path.GetFullPath(map).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(map)) Resolver.SelectMission(map, mission); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        Files = []; Folders.Clear(); fileNodes.Clear(); otherOpenFiles = null; Diagnostics.Clear(); Problems.Clear(); SearchResults.Clear(); index.Clear();
        RootPath = root; HasRoot = true; IsBusy = true; WorkspaceNavigationGeneration = navigationGeneration; Status = "Scanning files…";
        RootPublished?.Invoke();
        Settings.LastRoot = root; Settings.RecentRoots.RemoveAll(p => p.Equals(root, StringComparison.OrdinalIgnoreCase)); Settings.RecentRoots.Insert(0, root); Settings.RecentRoots = Settings.RecentRoots.Take(8).ToList();
        try
        {
            List<FileEntry> found = await Task.Run(() =>
            {
                List<FileEntry> entries = [];
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                // Derived world builds and the save/recovery coordination file are not assets. Even a shared read of
                // the lock file can race the recovery check's exclusive open when a project is first scanned.
                string previews = Path.DirectorySeparatorChar + Recoil.Zbd.Core.Sources.SourceWorlds.PreviewFolder.Replace('/', Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string recoveryLock = Path.DirectorySeparatorChar + Recoil.Zbd.Core.Sources.SourcePublisher.LockFile.Replace('/', Path.DirectorySeparatorChar);
                Dictionary<string, bool> projects = new(StringComparer.OrdinalIgnoreCase);
                bool WorkingFile(string file)
                {
                    // Only below the folder opened: a build folder opened itself lists its files.
                    int at = file.EndsWith(recoveryLock, StringComparison.OrdinalIgnoreCase) ? file.Length - recoveryLock.Length
                        : file.IndexOf(previews, Math.Max(0, root.Length - 1), StringComparison.OrdinalIgnoreCase);
                    if (at < Math.Max(0, root.Length - 1)) return false;
                    string project = file[..at];
                    if (!projects.TryGetValue(project, out bool isProject)) projects[project] = isProject = Recoil.Zbd.Core.Sources.SourceProject.IsProject(project);
                    return isProject;
                }
                foreach (string file in Directory.EnumerateFiles(root, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (WorkingFile(file)) continue;
                    entries.Add(new(file, Path.GetRelativePath(root, file), FormatRegistry.Probe(file)));
                }
                return entries.OrderBy(f => f.RelativePath, DisplayPathComparer)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            }, token).WaitAsync(token);
            token.ThrowIfCancellationRequested(); Files = found;
            FolderNode rootNode = new(Path.GetFileName(root), root); Dictionary<string, FolderNode> directories = new(StringComparer.OrdinalIgnoreCase) { [root] = rootNode };
            foreach (var file in found)
            {
                string directory = Path.GetDirectoryName(file.Path)!;
                FolderNode node = new(file.Name,file.Path,file); fileNodes[file.Path] = node; EnsureDirectory(directory).Children.Add(node);
            }
            Folders.Add(rootNode); Status = $"{found.Count:N0} files · {found.Count(f => f.Path.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase)):N0} ZBDs · indexing names…";
            await IndexAsync(found, token);
            FolderNode EnsureDirectory(string path)
            {
                if (directories.TryGetValue(path, out var node)) return node;
                node = new(Path.GetFileName(path), path); directories[path] = node; EnsureDirectory(Path.GetDirectoryName(path)!).Children.Add(node); return node;
            }
        }
        catch (OperationCanceledException) { if (WorkspaceGeneration == committedWorkspace) Status = "Scan canceled"; cancellationToken.ThrowIfCancellationRequested(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (WorkspaceGeneration != committedWorkspace)
                throw new StudioCommandException("context_changed", "A newer workspace replaced the failed folder scan.");
            throw;
        }
        finally { if (WorkspaceGeneration == committedWorkspace) IsBusy = false; }
    }
    private async Task IndexAsync(List<FileEntry> files, CancellationToken token)
    {
        int completed = 0; int warnings = 0;
        foreach (var file in files.Where(f => f.Probe.Recognition == Recognition.Supported))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var doc = await Task.Run(() => FormatRegistry.Default.OpenAsync(file.Path, token), token).WaitAsync(token);
                token.ThrowIfCancellationRequested(); index.AddRange(doc.Assets.Select(a => new SearchHit(file.Path, a.Kind, a.Index, a.Name)));
                foreach (var diagnostic in doc.Diagnostics) { warnings++; if (Diagnostics.Count < 500) AddProblem(diagnostic.Message, diagnostic.Severity, file.Path, diagnostic.AssetIndex, diagnostic.Offset); }
                Status = $"Indexed {++completed} containers · {index.Count:N0} assets";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { token.ThrowIfCancellationRequested(); AddProblem(ex.Message, "Error", file.Path); }
        }
        token.ThrowIfCancellationRequested(); RefreshSearch(); Status = $"{Files.Count:N0} files · {index.Count:N0} indexed assets · {warnings} reader diagnostics";
    }
    public async Task<DocumentModel?> OpenFileAsync(string path, CancellationToken cancellationToken = default, Action? beforePublish = null, bool activate = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long generation = ++navigationGeneration;
        RequireCurrentNavigation(generation);
        path = Path.GetFullPath(path);
        var existing = Documents.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ValidateNavigationPublication?.Invoke(false); beforePublish?.Invoke(); RequireCurrentNavigation(generation); if (activate) SelectedDocument = existing; return existing; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(workspace.Token, cancellationToken);
        var token = cancellation.Token; Status = "Opening " + Path.GetFileName(path) + "…";
        try
        {
            var doc = await LoadDocumentAsync(path, token); token.ThrowIfCancellationRequested();
            RequireCurrentNavigation(generation); ValidateNavigationPublication?.Invoke(false); beforePublish?.Invoke(); RequireCurrentNavigation(generation);
            existing = Documents.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { if (activate) SelectedDocument = existing; return existing; }
            DocumentModel model = new(doc); model.AttachResolver(Resolver); Documents.Add(model); if (activate) SelectedDocument = model;
            foreach (var diagnostic in doc.Diagnostics) AddProblem(diagnostic.Message, diagnostic.Severity, path, diagnostic.AssetIndex, diagnostic.Offset);
            Status = model.Description; return model;
        }
        catch (OperationCanceledException)
        {
            if (!disposed && generation == navigationGeneration) Status = "Opening canceled; the current document was retained.";
            cancellationToken.ThrowIfCancellationRequested();
            if (beforePublish != null) throw new StudioCommandException("context_changed", "The workspace changed while opening the requested document.");
            return null;
        }
        catch (StudioCommandException ex)
        { if (!disposed && generation == navigationGeneration) Status = ex.Message; throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { RequireCurrentNavigation(generation); token.ThrowIfCancellationRequested(); AddProblem(ex.Message, "Error", path); Status = "Could not open file: " + ex.Message; return null; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { RequireCurrentNavigation(generation); throw; }
    }
    public void Close(DocumentModel document) { if (document.IsDirty) throw new InvalidOperationException("Use CloseAsync to resolve unsaved edits."); RemoveDocument(document); }
    public async Task CloseAsync(DocumentModel document)
    {
        CloseDecisionsStarting?.Invoke();
        if (!await CanRemoveAsync(document)) return;
        RemoveDocument(Replacement(document));
    }
    /// <summary>A source world's document after resolving its drafts rebuilt it (the original is then disposed).</summary>
    private DocumentModel Replacement(DocumentModel document) =>
        document.IsDisposed && document.SourceWorld?.Owner is { IsDisposed: false } owner && Documents.Contains(owner) ? owner : document;
    private Task<bool> CanRemoveAsync(DocumentModel document) => ConfirmDiscardAsync?.Invoke(document) ?? Task.FromResult(!document.IsDirty);
    private void RemoveDocument(DocumentModel document) { int i = Documents.IndexOf(document); Documents.Remove(document); document.Dispose(); if (SelectedDocument == document) SelectedDocument = Documents.Count > 0 ? Documents[Math.Clamp(i, 0, Documents.Count - 1)] : null; }
    public Task ReloadAsync() => ReloadSelectedAsync();
    public void CheckExternalChanges() => _ = CheckExternalChangesAsync();
    /// <summary>
    /// Marks documents whose files changed on disk. A source world compares the stamps of every project file its build read
    /// (thousands for a retail mission), so that runs off the UI thread; the task completes when those results are shown.
    /// </summary>
    internal Task CheckExternalChangesAsync()
    {
        List<Task> pending = [];
        foreach (var doc in Documents)
        {
            // While a save replaces the project's files they differ from what the workspace last read; the save's end is the next state.
            if (doc.SourceWorld != null) { if (!doc.SourceWorld.Workspace.IsSaving) pending.Add(CheckSourceWorldAsync(doc)); continue; }
            try { doc.IsStale = (doc.ContentEdits is { } content ? content.HasExternalChanges() : doc.ResourceEdits is { } resources ? FileStamp.Read(resources.TargetPath) != resources.TargetStamp : doc.ModelEdits?.HasExternalChanges() ?? FileStamp.Read(doc.Path) != doc.Document.Stamp) || doc.PickupEdits?.HasExternalChanges() == true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { doc.IsStale = true; }
        }
        return Task.WhenAll(pending);
    }
    private readonly Dictionary<DocumentModel, Task> sourceWorldChecks = [];
    private Task CheckSourceWorldAsync(DocumentModel doc)
    {
        if (sourceWorldChecks.TryGetValue(doc, out var running)) return running;
        TaskCompletionSource done = new();
        sourceWorldChecks[doc] = done.Task;
        _ = Run();
        return done.Task;
        async Task Run()
        {
            long revision = doc.Revision; bool stale;
            try
            {
                try { stale = await Task.Run(() => doc.SourceInputsChanged()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { stale = true; }
                // An edit, save or rebuild during the check makes its result obsolete; the next check reads the new state.
                if (!doc.IsDisposed && doc.Revision == revision && doc.SourceWorld?.Workspace.IsSaving != true) doc.IsStale = stale;
            }
            finally { sourceWorldChecks.Remove(doc); done.SetResult(); }
        }
    }
    /// <summary>Publishes a document built elsewhere (a source world), as opening a file would.</summary>
    internal void AddDocument(DocumentModel model, bool activate)
    {
        model.AttachResolver(Resolver); Documents.Add(model); if (activate) SelectedDocument = model;
        Status = model.Description;
    }
    /// <summary>Replaces <paramref name="original"/> in place with a rebuilt document; the original is disposed.</summary>
    internal void ReplaceDocument(DocumentModel original, DocumentModel replacement)
    {
        int index = Documents.IndexOf(original);
        if (index < 0) throw new StudioCommandException("context_changed", "The document was closed.");
        ++navigationGeneration;
        replacement.Query = original.Query; replacement.KindFilter = original.KindFilter;
        replacement.AttachResolver(Resolver);
        var asset = original.SelectedAsset?.Record;
        replacement.SelectedAsset = asset == null ? null : replacement.Assets.FirstOrDefault(a => a.Record.Kind == asset.Kind && a.Record.Index == asset.Index) ?? replacement.Assets.FirstOrDefault(a => a.Record.Kind == asset.Kind);
        bool selected = SelectedDocument == original;
        Documents[index] = replacement;
        if (selected) SelectedDocument = replacement;
        original.Dispose();
        SynchronizeOpenFiles();
    }
    partial void OnGlobalQueryChanged(string value) => RefreshSearch();
    private void RefreshSearch()
    {
        SearchIsLimited = false; SearchResults.Clear(); string query = GlobalQuery.Trim(); if (query.Length < 2) return;
        var matches = index.Where(h => h.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || h.Location.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(501).ToArray();
        SearchIsLimited = matches.Length > 500;
        foreach (var hit in matches.Take(500)) SearchResults.Add(hit);
    }
    internal sealed record RelatedMatches(IReadOnlyList<SearchHit> Items, bool Truncated);
    internal RelatedMatches Related(IEnumerable<string> names, string context) => MatchRelated(names, index, context);
    /// <summary>One index scan, preserving reference order and index order within each name, with bounded input and retained matches.</summary>
    internal static RelatedMatches MatchRelated(IEnumerable<string> names, IEnumerable<SearchHit> source, string context,
        int maximumNames = 65_536, int maximumEntries = 1_000_000, long maximumNameCharacters = 4 * 1024 * 1024, long maximumIndexCharacters = 64 * 1024 * 1024)
    {
        Dictionary<string, int> ranks = new(StringComparer.OrdinalIgnoreCase);
        bool truncated = false; int inspected = 0; long characters = 0;
        foreach (string name in names)
        {
            if (++inspected > maximumNames || (characters += name.Length) > maximumNameCharacters) { truncated = true; break; }
            if (name.Length <= 1) continue;
            string key = Path.GetFileNameWithoutExtension(name);
            ranks.TryAdd(key, ranks.Count);
        }
        if (ranks.Count == 0) return new([], truncated);
        var comparer = Comparer<(int Rank, int Position, SearchHit Hit)>.Create((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Position.CompareTo(b.Position));
        SortedSet<(int Rank, int Position, SearchHit Hit)> kept = new(comparer);
        HashSet<SearchHit> identities = [];
        Dictionary<int, int> counts = [];
        inspected = 0; characters = 0;
        foreach (var hit in source)
        {
            if (++inspected > maximumEntries || (characters += (long)hit.Name.Length + hit.File.Length) > maximumIndexCharacters) { truncated = true; break; }
            if (hit.File.Equals(context, StringComparison.OrdinalIgnoreCase) || !ranks.TryGetValue(Path.GetFileNameWithoutExtension(hit.Name), out int rank) || identities.Contains(hit)) continue;
            if (counts.GetValueOrDefault(rank) == 100 || kept.Count == 300 && rank >= kept.Max.Rank) { truncated = true; continue; }
            kept.Add((rank, inspected, hit)); identities.Add(hit); counts[rank] = counts.GetValueOrDefault(rank) + 1;
            if (kept.Count > 300)
            {
                var last = kept.Max; kept.Remove(last); identities.Remove(last.Hit); counts[last.Rank]--;
                truncated = true;
            }
        }
        return new(kept.Select(p => p.Hit).ToArray(), truncated);
    }
    internal IEnumerable<SearchHit> SearchIndex(string query) => index.Where(h => h.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || h.Location.Contains(query, StringComparison.OrdinalIgnoreCase));
    internal void CloseResolved(DocumentModel doc) => RemoveDocument(doc);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; ++navigationGeneration; ++WorkspaceGeneration;
        if (SelectedDocument != null) SelectedDocument.PropertyChanged -= SelectedDocumentSelectionChanged;
        workspace.Cancel(); foreach (var doc in Documents) doc.Dispose(); workspace.Dispose(); GC.SuppressFinalize(this);
    }
}
