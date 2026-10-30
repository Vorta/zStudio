using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using zStudio.Core;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

public sealed record FileEntry(string Path, string RelativePath, FormatProbe Probe)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Detail => Probe.Description;
}
public sealed partial class FolderNode(string name, string path, FileEntry? file = null) : ObservableObject
{
    [ObservableProperty] private bool isExpanded = true;
    [ObservableProperty] private bool isSelected;
    public string Name { get; } = name;
    public string Path { get; } = path;
    public FileEntry? File { get; } = file;
    public string FileIcon => File?.Probe.Recognition == Recognition.Malformed ? "!" : "▧";
    public string ToolTip => Path + (File == null ? "" : "\n" + File.Detail) + (IsOpen ? "\nOpen" + (IsActive ? " · active" : "") + (Document?.IsDirty == true ? " · unsaved changes" : "") : "");
    public string DisplayName => Name + (Document?.IsDirty == true ? " *" : "");
    public bool IsOpen => Document != null;
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private DocumentModel? document;
    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(ToolTip));
    partial void OnDocumentChanging(DocumentModel? value) { if (Document != null) Document.PropertyChanged -= DocumentChanged; }
    partial void OnDocumentChanged(DocumentModel? value)
    {
        if (value != null) value.PropertyChanged += DocumentChanged;
        OnPropertyChanged(nameof(IsOpen)); RefreshDocumentLabel();
    }
    private void DocumentChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(DocumentModel.Title) or nameof(DocumentModel.IsDirty)) RefreshDocumentLabel(); }
    private void RefreshDocumentLabel() { OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(ToolTip)); }
    public ObservableCollection<FolderNode> Children { get; } = [];
}
public sealed partial class AssetItem(AssetRecord record) : ObservableObject
{
    public Guid? ResourceId { get; internal set; }
    public AssetRecord Record { get; private set; } = record;
    public string Name => Record.Name;
    public string Kind => Record.Kind.ToString();
    public string Summary => Record.Summary.Length > 0 ? Record.Summary : $"{Record.Length:N0} bytes";
    internal void UpdateRecord(AssetRecord value)
    {
        if (Record.Id != value.Id) throw new InvalidOperationException("An asset row must retain its identity.");
        Record = value; OnPropertyChanged(nameof(Record)); OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Summary));
    }
    public int Index => Record.Index;
    public string Identity => $"{Record.Kind} #{Record.Index}";
    public string SequenceCount => Record.Content is AnimationEntry entry ? entry.Sequences.Count.ToString() : "";
    [ObservableProperty] private BitmapSource? thumbnail;
    internal bool ThumbnailRequested { get; set; }
}
public sealed partial class DocumentModel : ObservableObject, IDisposable
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public long Revision { get; private set; }
    /// <summary>
    /// The revision the last close decision approved closing this document at (the state its question showed, or the saved
    /// one); an edit accepted after it is not covered. Callers clear it before asking.
    /// </summary>
    internal long? ApprovedCloseRevision { get; set; }
    public AssetDocument Document { get; private set; }
    /// <summary>
    /// What a read of this map's sources holds for: the built map and the project's accepted content. A save changes neither (it
    /// changes <see cref="Revision"/>); an edit, undo, redo, discard or another world's edit changes the content, a rebuild the map.
    /// </summary>
    internal (AssetDocument Map, long Content) SourceState => (Document, SourceWorld?.Workspace.ContentRevision ?? 0);
    public string Path => Document.Path;
    public string Title => (SourceWorld?.Label ?? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) + "/" + System.IO.Path.GetFileName(Path)) + (IsDirty ? " *" : "");
    /// <summary>A mission world built from the open source project; its edits go to the project's sources, never to this file.</summary>
    internal SourceWorldSession? SourceWorld { get; }
    /// <summary>The evaluated scene state this document shows, with the project files it read.</summary>
    internal zStudio.Core.Sources.SourceSceneState? SourceBuild { get; private set; }
    /// <summary>The project workspace's content revision the shown build was made from.</summary>
    internal long SourceRevision { get; private set; }
    /// <summary>The notes on the reach of the edit this build was made for (a copy's part names, a part's other copies).</summary>
    internal IReadOnlyList<string> SourceEditNotes { get; set; } = [];
    public event Action? SourceWorldChanged;
    private void SourceEditsChanged(zStudio.Core.Sources.SourceWorkspaceChange change)
    {
        if (IsDisposed) return;
        Revision++; OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(IsDirty)); SourceWorldChanged?.Invoke();
    }
    /// <summary>
    /// Whether a project file this world was built from changed since: in the workspace (another world's edit, an undo) or on
    /// disk. Reads only immutable build state and thread-safe workspace queries, so it runs off the UI thread.
    /// </summary>
    internal bool SourceInputsChanged(CancellationToken token = default, bool verifyContent = true)
    {
        if (SourceWorld is not { } world || SourceBuild is not { } build) return false;
        var changed = world.Workspace.ChangedSince(SourceRevision);
        if (changed.Count > 0 && build.Dependencies.Any(changed.Contains)) return true;
        if (changed.Count > 0 && build.AddsToMissingFolder(changed)) return true;
        // Directory discovery, like content hashing, belongs to the worker checks before and after edit preparation.
        if (verifyContent && build.ResourceInputsChanged(world.Workspace, token)) return true;
        if (world.Workspace.ExternalChanges(token, verifyContent, build.Dependencies).Count > 0) return true;
        if (build.MissingInputs.Any(relative => System.IO.Path.Exists(zStudio.Core.Sources.SourceProject.Resolve(world.Root, relative)))) return true;
        foreach (var (relative, stamp) in build.Inputs)
        {
            token.ThrowIfCancellationRequested();
            string path = zStudio.Core.Sources.SourceProject.Resolve(world.Root, relative);
            if (!File.Exists(path) || FileStamp.Read(path) != stamp) return true;
            if (verifyContent && build.InputHashes.TryGetValue(relative, out string? hash) && !zStudio.Core.Sources.SourceRead.Matches(path, stamp.Length, hash, token)) return true;
        }
        return false;
    }
    /// <summary>The members of a compiled archive or standalone ZRD, read for inspection with stable identities.</summary>
    public ResourceMembers? ResourceMembers { get; }
    public AssetRecord? OriginalAsset(AssetRecord asset) => Document.Assets.SingleOrDefault(a => a.Kind == asset.Kind && a.Index == asset.Index);
    private PickupPlacementEditSession? pickupEdits;
    /// <summary>The placements this document shows: a source world's change through its sources, a compiled world's are read only.</summary>
    public PickupPlacementEditSession? PickupEdits { get => pickupEdits; private set => SetProperty(ref pickupEdits, value); }
    private Task<PickupPlacementEditSession>? pickupLoading;
    public bool IsDisposed { get; private set; }
    public event Action? Disposing;
    public bool PickupDiagnosticsReported { get; set; }
    internal Dictionary<AssetId,Dictionary<string,bool>> DataTreeExpansion { get; } = [];
    /// <summary>Only a source world has unsaved changes: its edits are its project's, persisted only by Save. Compiled documents are read only.</summary>
    public bool IsDirty => SourceWorld?.Workspace.IsDirty == true;
    public async Task<PickupPlacementEditSession> GetPickupEditsAsync(AssetResolver resolver, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsDisposed) throw new OperationCanceledException("The pickup document was closed.", token);
        var lifetime = Lifetime.Token;
        if (Document is zStudio.Core.Sources.SourceSceneSnapshot sourceSnapshot)
        {
            if (PickupEdits != null) return PickupEdits;
            if (pickupLoading == null || pickupLoading.IsCanceled || pickupLoading.IsFaulted)
                pickupLoading = Task.Run(() => PickupPlacementEditSession.FromSources(sourceSnapshot, lifetime), lifetime);
            var sourceLoading = pickupLoading;
            var sourceEdits = await sourceLoading.WaitAsync(token);
            token.ThrowIfCancellationRequested(); lifetime.ThrowIfCancellationRequested();
            // Evaluation may accept a newer source scene while an earlier caller is awaiting its resources.
            // Never install a retired snapshot, including when two readers share the same loading task.
            if (!ReferenceEquals(Document, sourceSnapshot) || !ReferenceEquals(pickupLoading, sourceLoading))
                return await GetPickupEditsAsync(resolver, token);
            PickupEdits ??= sourceEdits;
            return PickupEdits;
        }
        if (PickupEdits != null) return PickupEdits;
        if (pickupLoading == null || pickupLoading.IsCanceled || pickupLoading.IsFaulted)
            pickupLoading = PickupPlacementEditSession.LoadAsync(Path, resolver, lifetime);
        var loading = pickupLoading;
        var edits = await loading.WaitAsync(token);
        token.ThrowIfCancellationRequested(); lifetime.ThrowIfCancellationRequested();
        // The placements were invalidated while this session loaded: it is stale. Load the current one.
        if (!ReferenceEquals(pickupLoading, loading)) return await GetPickupEditsAsync(resolver, token);
        if (PickupEdits == null)
        {
            if (Document.Scene is { } templateScene) edits.BindCoordinateTemplates(templateScene);
            PickupEdits = edits;
        }
        return PickupEdits;
    }
    public void InvalidateMissionContext()
    {
        contextLoading?.Cancel(); animationContext = null; MissionSceneLoader.Invalidate(Document);
    }
    private Task<AnimationPreviewContext>? animationContext;
    private string? animationWorldPath;
    private MissionDifficulty animationDifficulty = MissionDifficulty.Medium;
    private CancellationTokenSource? contextLoading;
    public Task<AnimationPreviewContext> GetAnimationContextAsync(AssetResolver resolver, CancellationToken token, string? worldPath = null, MissionDifficulty difficulty = MissionDifficulty.Medium, string? exactMission = null)
    {
        if (Document is zStudio.Core.Sources.SourceSceneSnapshot source)
            return Task.Run(() => AnimationPreviewContext.FromSources(source, difficulty, token), token);
        bool worldChanged = worldPath != null;
        // "" returns to the automatic scene beside the file; null keeps the one chosen before.
        if (worldPath != null) animationWorldPath = worldPath.Length == 0 ? null : worldPath;
        // An explicit mission request must load exactly that reader, never a cached or fallback context.
        bool missionChanged = exactMission != null && (animationContext?.IsCompletedSuccessfully != true ||
            !string.Equals(animationContext.Result.Mission?.Layout.MissionArchive, exactMission, StringComparison.OrdinalIgnoreCase));
        if (worldChanged || missionChanged || animationContext == null || animationContext.IsFaulted || animationContext.IsCanceled || animationDifficulty != difficulty)
        {
            var previous = !worldChanged && animationContext?.IsCompletedSuccessfully == true ? animationContext.Result : null;
            contextLoading?.Cancel(); contextLoading?.Dispose(); contextLoading = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            animationDifficulty = difficulty;
            animationContext = previous != null ? previous.WithDifficultyAsync(resolver, difficulty, contextLoading.Token, exactMission) :
                AnimationPreviewContext.LoadAsync(Document.Animations!, Path, resolver, animationWorldPath, contextLoading.Token, difficulty, exactMission);
        }
        return animationContext.WaitAsync(token);
    }
    public string Description => $"{Document.Probe.Description} · {Assets.Count:N0} assets";
    public ObservableCollection<AssetItem> Assets { get; }
    public ICollectionView FilteredAssets { get; }
    public ObservableCollection<SceneTreeItem> SceneRoots { get; } = [];
    internal SceneTreeModel? StoredSceneTree { get; private set; }
    private readonly SceneTreeState storedSceneState = new();
    private void ResetStoredSceneTree(GameScene scene)
    {
        StoredSceneTree = new(scene, Path, false, storedSceneState);
        SceneRoots.Clear(); foreach (var root in StoredSceneTree.Roots) SceneRoots.Add(root);
    }
    public CancellationTokenSource Lifetime { get; } = new();
    [ObservableProperty] private string query = "";
    [ObservableProperty] private string kindFilter = "All types";
    [ObservableProperty] private AssetItem? selectedAsset;
    [ObservableProperty] private bool isStale;
    public string[] Kinds { get; private set; }
    public DocumentModel(AssetDocument doc) : this(doc, null, null) { }
    internal DocumentModel(PreparedDocument prepared) : this(prepared.Document, null, null, 0, prepared.Resources) { }
    internal DocumentModel(AssetDocument doc, SourceWorldSession? sourceWorld, zStudio.Core.Sources.SourceSceneState? sourceBuild, long sourceRevision = 0, ResourceMembers? resources = null)
    {
        Document = doc;
        SourceWorld = sourceWorld; SourceBuild = sourceBuild; SourceRevision = sourceRevision;
        if (sourceWorld != null) { sourceWorld.Owner = this; sourceWorld.Workspace.Changed += SourceEditsChanged; }
        Assets = new(doc.Assets.OrderBy(a => a.Kind == AssetKind.World ? -1 : (int)a.Kind).ThenBy(a => a.Index).Select(a => new AssetItem(a)));
        Kinds = ["All types", .. Assets.Select(a => a.Kind).Distinct().Order()];
        FilteredAssets = CollectionViewSource.GetDefaultView(Assets); FilteredAssets.Filter = Matches;
        if (doc.Probe.Family is FormatFamily.Archive or FormatFamily.Zrd && !doc.Diagnostics.Any(d => d.Severity == "Error"))
        {
            ResourceMembers = resources ?? new(doc);
            foreach (var item in Assets) item.ResourceId = ResourceMembers.Members[item.Index].Id;
        }
        else if (doc.Scripts is { } scripts)
            foreach (var item in Assets.Where(a => a.Record.Kind == AssetKind.Script)) item.ResourceId = scripts.Entries[item.Index].Id;
        if (doc.Scene is GameScene scene) ResetStoredSceneTree(scene);
    }
    internal void AcceptSourceScene(zStudio.Core.Sources.SourceSceneSnapshot source, long revision, PickupPlacementEditSession edits)
    {
        if (SourceWorld == null || IsDisposed) throw new InvalidOperationException("The source document is no longer open.");
        Document = source; SourceBuild = source.BuildState; SourceRevision = revision;
        PickupEdits = edits; pickupLoading = null; PickupDiagnosticsReported = false;
        contextLoading?.Cancel(); animationContext = null;
        var records = source.Assets.ToDictionary(asset => asset.Id);
        foreach (var item in Assets) if (records.TryGetValue(item.Record.Id, out var record)) item.UpdateRecord(record);
        ResetStoredSceneTree(source.Scene!);
        Revision++; OnPropertyChanged(nameof(Document)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(IsDirty));
    }
    private bool Matches(object o) => o is AssetItem a && (KindFilter == "All types" || KindFilter == a.Kind) && (Query.Length == 0 || a.Name.Contains(Query, StringComparison.OrdinalIgnoreCase) || a.Identity.Contains(Query, StringComparison.OrdinalIgnoreCase));
    partial void OnQueryChanged(string value) => FilteredAssets.Refresh();
    partial void OnKindFilterChanged(string? oldValue, string newValue)
    {
        // Native selectors clear SelectedItem during document/ItemsSource replacement.
        // Null is presentation churn, not an authored filter that matches no asset kind.
        if (newValue == null) { KindFilter = oldValue ?? "All types"; return; }
        FilteredAssets.Refresh();
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        DisposeCore();
        if (SourceWorld is { } world)
        {
            world.Workspace.Changed -= SourceEditsChanged;
            // The document showing the world owns the session; a replaced document only releases its build.
            if (world.Owner == this) world.Dispose();
        }
    }
    private void DisposeCore() { IsDisposed = true; Disposing?.Invoke(); Lifetime.Cancel(); contextLoading?.Cancel(); contextLoading?.Dispose(); Lifetime.Dispose(); foreach (var a in Assets) a.Thumbnail = null; GC.SuppressFinalize(this); }
}
public sealed partial class InspectorNode : ObservableObject
{
    [ObservableProperty] private bool isSelected;
    private readonly JsonNode? node;
    private readonly IDictionary<string,bool>? expansion;
    private readonly string path;
    private readonly int initialDepth;
    [ObservableProperty] private bool isExpanded;
    partial void OnIsExpandedChanged(bool value) { if (expansion != null) expansion[path] = value; }
    private IReadOnlyList<InspectorNode>? children;
    public string Name { get; }
    public string Value { get; }
    public string Label => string.IsNullOrEmpty(Value) ? Name : Name + ": " + Value;
    public IReadOnlyList<InspectorNode> Children => children ??= node switch
    {
        JsonObject obj => obj.Select((p,i) => new InspectorNode(p.Key, p.Value,expansion,path + "/" + i,Math.Max(0,initialDepth - 1))).ToArray(),
        JsonArray array => array.Select((v, i) => new InspectorNode($"[{i}]", v,expansion,path + "/" + i,Math.Max(0,initialDepth - 1))).ToArray(),
        _ => []
    };
    public InspectorNode(string name, JsonNode? value,IDictionary<string,bool>? expansion = null,string path = "",int initialDepth = 0)
    {
        Name = name; node = value;
        this.expansion = expansion; this.path = path; this.initialDepth = initialDepth;
        int count = value is JsonObject obj ? obj.Count : value is JsonArray array ? array.Count : 0;
        isExpanded = expansion?.TryGetValue(path,out bool saved) == true ? saved : initialDepth > 0 && count is > 0 and <= 32;
        Value = value switch { JsonObject o when o.ContainsKey("text") => o.Text(), JsonObject o when o["type"]?.ToString() is "array" or "string" or "int" or "float" && o.ContainsKey("offset") => $"{o["type"]} · {o["offset"]}" + (o.ContainsKey("value") ? " · " + o["value"] : ""), JsonObject o => $"{{{o.Count} field{(o.Count == 1 ? "" : "s")}}}", JsonArray a => $"[{a.Count} item{(a.Count == 1 ? "" : "s")}]", null => "null", _ => value.ToString() };
        if (Value.Length > 240) Value = Value[..240] + "…";
    }
}
public sealed record SearchHit(string File, AssetKind Kind, int Index, string Name)
{
    public string Identity => $"{Kind} #{Index}";
    public string Display => $"{Name} · {Kind} #{Index}";
    public string Location => string.Concat(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(File.AsSpan())),
        "/", System.IO.Path.GetFileName(File.AsSpan()));
}
public sealed record StudioProblem(string Severity, string Category, string Message, string? File = null, int? AssetIndex = null, long? Offset = null)
{
    internal AssetRecord? ResolveAsset(IEnumerable<AssetRecord> assets)
    {
        if (AssetIndex is int index)
        {
            var indexed = assets.Where(a => a.Index == index).ToArray();
            if (indexed.Length == 1) return indexed[0];
            assets = indexed; // An offset may disambiguate an index shared by different asset kinds.
        }
        if (Offset is not long offset || offset < 0) return null;
        // Half-open ranges, without adding Offset + Length (which may overflow).
        var matches = assets.Where(a => a.Offset >= 0 && offset >= a.Offset && offset - a.Offset < a.Length).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    /// <summary>Where the problem comes from when it is narrower than its file ("Entry #241 / instance 3", "Audio").</summary>
    public string? Source { get; init; }
    /// <summary>A compiled animation preview's own problem (its run, preparation or audio), listed while that preview is shown.</summary>
    internal bool AnimationPreview { get; init; }
    public string Scope => Source ?? (File == null ? "Workspace" : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(File)) + "/" + System.IO.Path.GetFileName(File));
    public string Details => (File ?? "No file identity supplied") + (AssetIndex is int index ? $" · asset #{index}" : "") + (Offset is long offset ? $" · source 0x{offset:X}" : "");
}
public sealed class StudioSettings
{
    internal const int MaximumExportChoices = 4096;
    private List<string> exportUncheckedOutputs = [];
    public List<string> ExportUncheckedOutputs
    {
        get => exportUncheckedOutputs;
        set => exportUncheckedOutputs = (value ?? []).Take(MaximumExportChoices).Where(p => !string.IsNullOrEmpty(p) && p.Length <= 260)
            .Select(SourceExportSelection.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    internal const int MaximumExportDestination = 1024;
    private string? exportDestination;
    /// <summary>The Export ZBD files window's destination folder, one for every project (null: Export asks for a folder).</summary>
    public string? ExportDestination
    {
        get => exportDestination;
        set => exportDestination = SourceExportDestination.Clean(value) is { Length: <= MaximumExportDestination } folder ? folder : null;
    }
    public bool McpEnabled { get; set; }
    public WorkspaceLayout? Workspace { get; set; }
    public WorkspaceLayout GetWorkspace()
    {
        if (Workspace?.Version != WorkspaceLayout.CurrentVersion) Workspace = new();
        Workspace.Normalize(); return Workspace;
    }
    private MissionDifficulty difficulty = MissionDifficulty.Medium;
    public MissionDifficulty Difficulty { get => difficulty; set => difficulty = Enum.IsDefined(value) ? value : MissionDifficulty.Medium; }
    private Dictionary<string, string> mw3Missions = new(StringComparer.OrdinalIgnoreCase);
    // Deserialization supplies an ordinal dictionary; later duplicate spellings replace earlier ones.
    public Dictionary<string, string> Mw3Missions
    {
        get => mw3Missions;
        set { mw3Missions = new(StringComparer.OrdinalIgnoreCase); foreach (var (map, mission) in value ?? []) mw3Missions[map] = mission; }
    }
    public double Width { get; set; } = 1560;
    public double Height { get; set; } = 940;
    public string Theme { get; set; } = "System";
    public string LastRoot { get; set; } = "";
    /// <summary>The worlds Compare worlds compared last.</summary>
    public string CompareRetail { get; set; } = "";
    public string CompareRebuilt { get; set; } = "";
    public List<string> RecentRoots { get; set; } = [];
    /// <summary>Whether zStudio asks GitHub for a newer release by itself after it starts, at most once a day (Help menu).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>The last check that asked GitHub, successful or not: automatic checks wait a day after it.</summary>
    public DateTimeOffset? UpdateAttemptedUtc { get; set; }
    /// <summary>The last successful update check and the latest release it found.</summary>
    public DateTimeOffset? UpdateCheckedUtc { get; set; }
    public string? UpdateLatestVersion { get; set; }
    public string? UpdateLatestUrl { get; set; }
    /// <summary>Animation mode shows the World tree once it was shown there (hidden by default).</summary>
    public bool AnimationModeWorldTree { get; set; }
    /// <summary>The first-entry tip of Animation mode was dismissed.</summary>
    public bool AnimationModeTipDismissed { get; set; }
    internal static string SettingsPath => System.IO.Path.Combine(zStudio.Automation.StudioProfile.Directory, "settings.json");
    public static StudioSettings Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
        string temp = SettingsPath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonData.Options)); File.Move(temp, SettingsPath, true);
    }
}
