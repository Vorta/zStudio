using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
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
    public List<FileEntry> Files { get; private set; } = [];
    public AssetResolver? Resolver { get; private set; }
    public StudioSettings Settings { get; } = StudioSettings.Load();
    public static MissionDifficulty[] DifficultyChoices { get; } = Enum.GetValues<MissionDifficulty>();
    [ObservableProperty] private MissionDifficulty difficulty = MissionDifficulty.Medium;
    public MainViewModel() => difficulty = Settings.Difficulty;
    partial void OnDifficultyChanged(MissionDifficulty value)
    {
        Settings.Difficulty = value;
        try { Settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Add("Could not save mission difficulty: " + ex.Message); }
    }
    public Func<DocumentModel, Task<bool>>? ConfirmDiscardAsync { get; set; }
    private CancellationTokenSource workspace = new();
    private readonly List<SearchHit> index = [];
    [ObservableProperty] private string rootPath = "Open a ZBD folder to start exploring";
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private DocumentModel? selectedDocument;
    [ObservableProperty] private string globalQuery = "";
    [ObservableProperty] private bool hasRoot;
    public async Task OpenRootAsync(string root)
    {
        root = Path.GetFullPath(root); if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        foreach (var document in Documents.ToArray()) if (!await CanRemoveAsync(document)) return;
        workspace.Cancel(); workspace.Dispose(); workspace = new(); var token = workspace.Token;
        foreach (var doc in Documents) doc.Dispose(); Documents.Clear(); SelectedDocument = null;
        // A previous asynchronous operation may still hold its resolver; its
        // canceled task owns that short remaining lifetime, not the new workspace.
        Resolver = new AssetResolver(root); Files = []; Folders.Clear(); Diagnostics.Clear(); SearchResults.Clear(); index.Clear();
        RootPath = root; HasRoot = true; IsBusy = true; Status = "Scanning files…";
        Settings.LastRoot = root; Settings.RecentRoots.RemoveAll(p => p.Equals(root, StringComparison.OrdinalIgnoreCase)); Settings.RecentRoots.Insert(0, root); Settings.RecentRoots = Settings.RecentRoots.Take(8).ToList();
        try
        {
            List<FileEntry> found = await Task.Run(() =>
            {
                List<FileEntry> entries = [];
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string file in Directory.EnumerateFiles(root, "*", options))
                { token.ThrowIfCancellationRequested(); entries.Add(new(file, Path.GetRelativePath(root, file), FormatRegistry.Probe(file))); }
                return entries.OrderBy(f => f.RelativePath, DisplayPathComparer)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            }, token);
            token.ThrowIfCancellationRequested(); Files = found;
            FolderNode rootNode = new(Path.GetFileName(root), root); Dictionary<string, FolderNode> directories = new(StringComparer.OrdinalIgnoreCase) { [root] = rootNode };
            foreach (var file in found)
            {
                string directory = Path.GetDirectoryName(file.Path)!; EnsureDirectory(directory).Children.Add(new(file.Name, file.Path, file));
            }
            Folders.Add(rootNode); Status = $"{found.Count:N0} files · {found.Count(f => f.Path.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase)):N0} ZBDs · indexing names…";
            await IndexAsync(found, token);
            FolderNode EnsureDirectory(string path)
            {
                if (directories.TryGetValue(path, out var node)) return node;
                node = new(Path.GetFileName(path), path); directories[path] = node; EnsureDirectory(Path.GetDirectoryName(path)!).Children.Add(node); return node;
            }
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested || workspace.Token == token) Status = "Scan canceled"; }
        finally { if (workspace.Token == token) IsBusy = false; }
    }
    private async Task IndexAsync(List<FileEntry> files, CancellationToken token)
    {
        int completed = 0; int warnings = 0;
        foreach (var file in files.Where(f => f.Probe.Recognition == Recognition.Supported))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var doc = await FormatRegistry.Default.OpenAsync(file.Path, token);
                token.ThrowIfCancellationRequested(); index.AddRange(doc.Assets.Select(a => new SearchHit(file.Path, a.Kind, a.Index, a.Name)));
                foreach (var diagnostic in doc.Diagnostics) { warnings++; if (Diagnostics.Count < 500) Diagnostics.Add($"{file.RelativePath}: {diagnostic.Message}"); }
                Status = $"Indexed {++completed} containers · {index.Count:N0} assets";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Diagnostics.Add($"{file.RelativePath}: {ex.Message}"); }
        }
        RefreshSearch(); Status = $"{Files.Count:N0} files · {index.Count:N0} indexed assets · {warnings} reader diagnostics";
    }
    public async Task<DocumentModel?> OpenFileAsync(string path)
    {
        var existing = Documents.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { SelectedDocument = existing; return existing; }
        var token = workspace.Token; Status = "Opening " + Path.GetFileName(path) + "…";
        try
        {
            var doc = await FormatRegistry.Default.OpenAsync(path, token); token.ThrowIfCancellationRequested();
            existing = Documents.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { SelectedDocument = existing; return existing; }
            DocumentModel model = new(doc); Documents.Add(model); SelectedDocument = model;
            foreach (var diagnostic in doc.Diagnostics) Diagnostics.Add($"{model.Title}: {diagnostic.Message}");
            Status = model.Description; return model;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Diagnostics.Add(ex.Message); Status = "Could not open file: " + ex.Message; return null; }
    }
    public void Close(DocumentModel document) { if (document.AnimationEdits?.IsDirty == true) throw new InvalidOperationException("Use CloseAsync to resolve unsaved animation edits."); RemoveDocument(document); }
    public async Task CloseAsync(DocumentModel document) { if (await CanRemoveAsync(document)) RemoveDocument(document); }
    private Task<bool> CanRemoveAsync(DocumentModel document) => document.AnimationEdits?.IsDirty != true ? Task.FromResult(true) : ConfirmDiscardAsync?.Invoke(document) ?? Task.FromResult(false);
    private void RemoveDocument(DocumentModel document) { int i = Documents.IndexOf(document); Documents.Remove(document); document.Dispose(); if (SelectedDocument == document) SelectedDocument = Documents.Count > 0 ? Documents[Math.Clamp(i, 0, Documents.Count - 1)] : null; }
    public async Task ReloadAsync() { if (SelectedDocument is not { } doc || !await CanRemoveAsync(doc)) return; string path = doc.Path; RemoveDocument(doc); await OpenFileAsync(path); }
    public void CheckExternalChanges()
    {
        foreach (var doc in Documents)
            try { doc.IsStale = FileStamp.Read(doc.Path) != doc.Document.Stamp; }
            catch (IOException) { doc.IsStale = true; }
    }
    partial void OnGlobalQueryChanged(string value) => RefreshSearch();
    private void RefreshSearch()
    {
        SearchResults.Clear(); if (GlobalQuery.Length < 2) return;
        foreach (var hit in index.Where(h => h.Name.Contains(GlobalQuery, StringComparison.OrdinalIgnoreCase) || h.Location.Contains(GlobalQuery, StringComparison.OrdinalIgnoreCase)).Take(500)) SearchResults.Add(hit);
    }
    public IEnumerable<SearchHit> Related(string name, string context) => index.Where(h => !h.File.Equals(context, StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(h.Name).Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)).Take(100);
    public void Dispose() { workspace.Cancel(); foreach (var doc in Documents) doc.Dispose(); workspace.Dispose(); GC.SuppressFinalize(this); }
}
