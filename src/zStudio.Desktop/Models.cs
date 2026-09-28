using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

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
    public Guid? ResourceId { get; init; }
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
    public ZbdDocument Document { get; }
    public string Path => Document.Path;
    public string Title => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) + "/" + System.IO.Path.GetFileName(Path) + (IsDirty ? " *" : "");
    public AnimationEditSession? AnimationEdits { get; }
    public ModelEditSession? ModelEdits { get; }
    public ResourceEditSession? ResourceEdits { get; }
    public event Action? ResourceEditsChanged;
    public ZbdDocument PreviewDocument => contentMirror ?? ContentEdits?.Current.Documents.GetValueOrDefault(Path) ?? ResourceEdits?.Current.Document ?? ModelEdits?.Current.World ?? Document;
    public AssetRecord? OriginalAsset(AssetRecord asset) => ResourceEdits is { } resources
        ? resources.OriginalAsset(resources.Current.Members[asset.Index])
        : ScriptEdits is { } scripts ? scripts.Package.Entries.ElementAtOrDefault(asset.Index)?.SourceIndex is int index ? Document.Assets.SingleOrDefault(a => a.Index == index) : null
        : Document.Assets.SingleOrDefault(a => a.Kind == asset.Kind && a.Index == asset.Index);
    public event Action? ModelEditsChanged;
    private readonly Stack<bool> sceneUndo = [], sceneRedo = [];
    private AssetResolver? workspaceResolver;
    public bool CanUndoScene => sceneUndo.Count > 0;
    public bool CanRedoScene => sceneRedo.Count > 0;
    internal bool NextSceneEditIsModel(bool redo) => (redo ? sceneRedo : sceneUndo).TryPeek(out bool model) && model;
    private void RecordSceneEdit(bool model) { sceneUndo.Push(model); sceneRedo.Clear(); }
    public void UndoScene(bool redo)
    {
        var from = redo ? sceneRedo : sceneUndo; var to = redo ? sceneUndo : sceneRedo;
        if (!from.TryPop(out bool model)) return;
        to.Push(model);
        if (model) { if (redo) ModelEdits!.Redo(); else ModelEdits!.Undo(); }
        else { if (redo) PickupEdits!.Redo(); else PickupEdits!.Undo(); }
    }
    public void AttachResolver(AssetResolver? resolver)
    {
        if (workspaceResolver != null) workspaceResolver.WorkspaceSnapshotsChanged -= ContentSnapshotsChanged;
        workspaceResolver = resolver;
        if (resolver != null) resolver.WorkspaceSnapshotsChanged += ContentSnapshotsChanged;
        ContentSnapshotsChanged();
    }
    public PickupPlacementEditSession? PickupEdits { get; private set; }
    private Task<PickupPlacementEditSession>? pickupLoading;
    private long pickupSnapshotRevision;
    private bool pickupsLocked = true;
    public bool PickupsLocked { get => pickupsLocked; set => SetProperty(ref pickupsLocked, value); }
    public bool IsDisposed { get; private set; }
    public event Action? Disposing;
    public bool PickupDiagnosticsReported { get; set; }
    internal Dictionary<AssetId,Dictionary<string,bool>> DataTreeExpansion { get; } = [];
    public bool IsDirty => ContentEdits?.IsDirty == true || ResourceEdits?.IsDirty == true || AnimationEdits?.IsDirty == true || PickupEdits?.IsDirty == true || ModelEdits?.IsDirty == true;
    public void ClaimResourcePaths(IEnumerable<string> paths) => workspaceResolver?.EditOwnership.Acquire(SessionId, Title, paths);
    public void InvalidateCleanPickupEdits()
    {
        if (PickupEdits is { IsDirty: false, CanUndo: false, CanRedo: false } || PickupEdits == null)
        { PickupEdits = null; pickupLoading = null; PickupDiagnosticsReported = false; }
    }
    public event Action? PickupEditsChanged;
    public async Task<PickupPlacementEditSession> GetPickupEditsAsync(AssetResolver resolver, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsDisposed) throw new OperationCanceledException("The pickup document was closed.", token);
        var lifetime = Lifetime.Token;
        if (PickupEdits != null)
        {
            if (pickupSnapshotRevision != resolver.SnapshotRevision && !PickupEdits.CanUndo && !PickupEdits.CanRedo) InvalidateCleanPickupEdits();
        }
        if (PickupEdits != null)
        {
            if (PickupEdits.HasSourceChanges()) throw new IOException("A pickup source archive changed outside zStudio. Save pending edits as a copy, then reload the map (F5) before rebuilding its preview.");
            return PickupEdits;
        }
        if (pickupLoading == null || pickupLoading.IsCanceled || pickupLoading.IsFaulted)
        {
            pickupSnapshotRevision = resolver.SnapshotRevision;
            pickupLoading = PickupPlacementEditSession.LoadAsync(Path, resolver, lifetime);
        }
        var edits = await pickupLoading.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        if (PickupEdits == null)
        {
            PickupEdits = edits;
            if (PreviewDocument.Scene is { } templateScene) edits.BindCoordinateTemplates(templateScene);
            edits.BeforeEdit += () =>
            {
                if (pickupSnapshotRevision != resolver.SnapshotRevision && !edits.CanUndo && !edits.CanRedo)
                    throw new InvalidOperationException("Resource previews changed. Refresh this map before editing pickup placements.");
                ClaimResourcePaths(edits.ArchivePaths.Concat(edits.ArchivePaths.Select(edits.TargetPath)));
            };
            edits.EditAccepted += () => RecordSceneEdit(false);
            edits.Changed += () =>
            {
                Revision++;
                PublishSceneSnapshots(); pickupSnapshotRevision = resolver.SnapshotRevision;
                InvalidateMissionContext();
                OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(IsDirty)); PickupEditsChanged?.Invoke();
            };
        }
        return edits;
    }
    public void InvalidateMissionContext()
    {
        contextLoading?.Cancel(); animationContext = null; MissionSceneLoader.Invalidate(Document);
        if (!ReferenceEquals(PreviewDocument, Document)) MissionSceneLoader.Invalidate(PreviewDocument);
    }
    private void PublishSceneSnapshots()
    {
        if (workspaceResolver == null) return;
        var coordinates = PickupEdits is { CanUndo: true } or { CanRedo: true } or { IsDirty: true } ? PickupEdits.WorkingArchives(Lifetime.Token) : [];
        var models = ModelEdits is { CanUndo: true } or { CanRedo: true } or { IsDirty: true } ? ModelEdits.Documents : [];
        workspaceResolver.SetWorkspaceSnapshots(SessionId, models.Concat(coordinates));
    }
    public string? LastSavedCopy { get; set; }
    private Task<AnimationPreviewContext>? animationContext;
    private string? animationWorldPath;
    private MissionDifficulty animationDifficulty = MissionDifficulty.Medium;
    private CancellationTokenSource? contextLoading;
    public Task<AnimationPreviewContext> GetAnimationContextAsync(AssetResolver resolver, CancellationToken token, string? worldPath = null, MissionDifficulty difficulty = MissionDifficulty.Medium)
    {
        bool worldChanged = worldPath != null;
        if (worldPath != null) animationWorldPath = worldPath;
        if (worldChanged || animationContext == null || animationContext.IsFaulted || animationContext.IsCanceled || animationDifficulty != difficulty)
        {
            var previous = !worldChanged && animationContext?.IsCompletedSuccessfully == true ? animationContext.Result : null;
            contextLoading?.Cancel(); contextLoading?.Dispose(); contextLoading = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            animationDifficulty = difficulty;
            animationContext = previous != null ? previous.WithDifficultyAsync(resolver, difficulty, contextLoading.Token) :
                AnimationPreviewContext.LoadAsync(AnimationEdits!.Package, Path, resolver, animationWorldPath, contextLoading.Token, difficulty);
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
    public DocumentModel(ZbdDocument doc)
    {
        Document = doc;
        Assets = new(doc.Assets.OrderBy(a => a.Kind == AssetKind.World ? -1 : (int)a.Kind).ThenBy(a => a.Index).Select(a => new AssetItem(a)));
        Kinds = ["All types", .. Assets.Select(a => a.Kind).Distinct().Order()];
        FilteredAssets = CollectionViewSource.GetDefaultView(Assets); FilteredAssets.Filter = Matches;
        InitializeContentEdits(doc);
        if (doc.Probe.Family is FormatFamily.Archive or FormatFamily.Zrd && !doc.Diagnostics.Any(d => d.Severity == "Error"))
        {
            ResourceEdits = new(doc);
            ResourceEdits.BeforeEdit += () => ClaimResourcePaths([Path, ResourceEdits.TargetPath]);
            RebuildResourceAssets();
            ResourceEdits.Changed += () =>
            {
                Revision++; workspaceResolver?.SetWorkspaceSnapshots(SessionId, [ResourceEdits.Current.Document]);
                RebuildResourceAssets(); InvalidateMissionContext();
                OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(IsDirty)); ResourceEditsChanged?.Invoke();
            };
        }
        if (doc.GameZLayout != null && doc.Probe.Version == 15 && !doc.Diagnostics.Any(d => d.Severity == "Error"))
        {
            ModelEdits = new(doc);
            ModelEdits.BeforeEdit += ClaimResourcePaths;
            ModelEdits.EditAccepted += () => RecordSceneEdit(true);
            ModelEdits.Changed += () =>
            {
                Revision++; PublishSceneSnapshots();
                RebuildModelAssets();
                InvalidateMissionContext(); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(IsDirty)); ModelEditsChanged?.Invoke();
            };
        }
        if (doc.Animations is { } source)
        {
            // Accepted edits replace complete entry snapshots. Give that working
            // set its own container so source inspection/export stays original;
            // sharing initial entries is safe because edits clone before writing.
            var working = new AnimationPackage { Prefix = source.Prefix, Tail = source.Tail };
            working.Entries.AddRange(source.Entries); working.Diagnostics.AddRange(source.Diagnostics);
            AnimationEdits = new(working);
            AnimationEdits.Changed += () => { Revision++; contextLoading?.Cancel(); animationContext = null; OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(IsDirty)); };
        }
        if (doc.Scene is GameScene scene) ResetStoredSceneTree(scene);
    }
    private void RebuildModelAssets()
    {
        var current = ModelEdits!.Current.World;
        var selected = SelectedAsset?.Record.Id;
        var existing = Assets.ToDictionary(a => a.Record.Id);
        var records = current.Assets.OrderBy(a => a.Kind == AssetKind.World ? -1 : (int)a.Kind).ThenBy(a => a.Index).ToArray();
        var retained = records.Select(a => a.Id).ToHashSet();
        foreach (var row in Assets.Where(a => !retained.Contains(a.Record.Id)).ToArray()) Assets.Remove(row);
        for (int i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (existing.TryGetValue(record.Id, out var row)) row.UpdateRecord(record);
            else { row = new(record); Assets.Insert(i, row); }
        }
        Kinds = ["All types", .. Assets.Select(a => a.Kind).Distinct().Order()]; OnPropertyChanged(nameof(Kinds));
        if (!Kinds.Contains(KindFilter)) KindFilter = "All types";
        SelectedAsset = Assets.FirstOrDefault(a => a.Record.Id == selected) ?? (selected == null ? null : Assets.FirstOrDefault());
        ResetStoredSceneTree(current.Scene!);
        OnPropertyChanged(nameof(Description));
    }
    private void RebuildResourceAssets()
    {
        if (ResourceEdits == null) return;
        Guid? selected = SelectedAsset?.ResourceId; int oldIndex = SelectedAsset?.Index ?? 0;
        Assets.Clear();
        foreach (var a in ResourceEdits.Current.Document.Assets) Assets.Add(new(a) { ResourceId = ResourceEdits.Current.Members[a.Index].Id });
        Kinds = ["All types", .. Assets.Select(a => a.Kind).Distinct().Order()]; OnPropertyChanged(nameof(Kinds));
        if (!Kinds.Contains(KindFilter)) KindFilter = "All types";
        SelectedAsset = Assets.FirstOrDefault(a => a.ResourceId == selected) ?? Assets.ElementAtOrDefault(Math.Clamp(oldIndex, 0, Math.Max(0, Assets.Count - 1)));
        OnPropertyChanged(nameof(Description));
    }
    private bool Matches(object o) => o is AssetItem a && (KindFilter == "All types" || KindFilter == a.Kind) && (Query.Length == 0 || a.Name.Contains(Query, StringComparison.OrdinalIgnoreCase) || a.Identity.Contains(Query, StringComparison.OrdinalIgnoreCase));
    partial void OnQueryChanged(string value) => FilteredAssets.Refresh();
    partial void OnKindFilterChanged(string value) => FilteredAssets.Refresh();
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; if (workspaceResolver != null) workspaceResolver.WorkspaceSnapshotsChanged -= ContentSnapshotsChanged; workspaceResolver?.SetWorkspaceSnapshots(SessionId, []); workspaceResolver?.EditOwnership.Release(SessionId); Disposing?.Invoke(); Lifetime.Cancel(); contextLoading?.Cancel(); contextLoading?.Dispose(); Lifetime.Dispose(); foreach (var a in Assets) a.Thumbnail = null; GC.SuppressFinalize(this); }
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
    public string FullText => node?.ToJsonString(JsonData.Options) ?? "null";
}
public sealed record SearchHit(string File, AssetKind Kind, int Index, string Name)
{
    public string Identity => $"{Kind} #{Index}";
    public string Display => $"{Name} · {Kind} #{Index}";
    public string Location => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(File)) + "/" + System.IO.Path.GetFileName(File);
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
    public string Scope => File == null ? "Workspace" : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(File)) + "/" + System.IO.Path.GetFileName(File);
    public string Details => (File ?? "No file identity supplied") + (AssetIndex is int index ? $" · asset #{index}" : "") + (Offset is long offset ? $" · source 0x{offset:X}" : "");
}
public sealed class StudioSettings
{
    public bool McpEnabled { get; set; }
    public WorkspaceLayout? Workspace { get; set; }
    public WorkspaceLayout GetWorkspace()
    {
        // Old expander heights described a different layout and deliberately do not migrate.
        Workspace ??= new() { InspectorWidth = double.IsFinite(PropertiesWidth) && PropertiesWidth >= 320 ? PropertiesWidth : 352 };
        Workspace.Normalize(); return Workspace;
    }
    public bool CreateBackupOnSave { get; set; }
    private MissionDifficulty difficulty = MissionDifficulty.Medium;
    public MissionDifficulty Difficulty { get => difficulty; set => difficulty = Enum.IsDefined(value) ? value : MissionDifficulty.Medium; }
    public double Width { get; set; } = 1560;
    public double Height { get; set; } = 940;
    public double FilesWidth { get; set; } = 215;
    public double AssetsWidth { get; set; } = 280;
    public double PropertiesWidth { get; set; } = 300;
    public double AnimationSidebarWidth { get; set; } = 360;
    public Dictionary<string, bool> AnimationSections { get; set; } = [];
    public Dictionary<string, double> AnimationSectionHeights { get; set; } = [];
    public string Theme { get; set; } = "System";
    public string LastRoot { get; set; } = "";
    public List<string> RecentRoots { get; set; } = [];
    private static string SettingsPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
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
