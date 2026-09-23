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
public sealed class FolderNode(string name, string path, FileEntry? file = null)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public FileEntry? File { get; } = file;
    public string FileIcon => File?.Probe.Recognition == Recognition.Malformed ? "!" : "▧";
    public string ToolTip => File?.Detail ?? Path;
    public ObservableCollection<FolderNode> Children { get; } = [];
}
public sealed partial class AssetItem(AssetRecord record) : ObservableObject
{
    public AssetRecord Record { get; } = record;
    public string Name => Record.Name;
    public string Kind => Record.Kind.ToString();
    public string Summary => Record.Summary.Length > 0 ? Record.Summary : $"{Record.Length:N0} bytes";
    public int Index => Record.Index;
    public string Identity => $"{Record.Kind} #{Record.Index}";
    [ObservableProperty] private BitmapSource? thumbnail;
    internal bool ThumbnailRequested { get; set; }
}
public sealed partial class DocumentModel : ObservableObject, IDisposable
{
    public ZbdDocument Document { get; }
    public string Path => Document.Path;
    public string Title => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) + "/" + System.IO.Path.GetFileName(Path) + (IsDirty ? " *" : "");
    public AnimationEditSession? AnimationEdits { get; }
    public PickupPlacementEditSession? PickupEdits { get; private set; }
    private Task<PickupPlacementEditSession>? pickupLoading;
    public bool PickupsLocked { get; set; } = true;
    public bool PickupDiagnosticsReported { get; set; }
    public bool IsDirty => AnimationEdits?.IsDirty == true || PickupEdits?.IsDirty == true;
    public event Action? PickupEditsChanged;
    public async Task<PickupPlacementEditSession> GetPickupEditsAsync(AssetResolver resolver, CancellationToken token)
    {
        if (PickupEdits != null)
        {
            if (PickupEdits.HasSourceChanges()) throw new IOException("A pickup source archive changed outside zStudio. Save pending edits as a copy, then reload the map (F5) before rebuilding its preview.");
            return PickupEdits;
        }
        if (pickupLoading == null || pickupLoading.IsCanceled || pickupLoading.IsFaulted)
            pickupLoading = PickupPlacementEditSession.LoadAsync(Path, resolver, Lifetime.Token);
        var edits = await pickupLoading.WaitAsync(token);
        if (PickupEdits == null)
        {
            PickupEdits = edits;
            edits.Changed += () => { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(IsDirty)); PickupEditsChanged?.Invoke(); };
        }
        return edits;
    }
    public void InvalidateMissionContext() { contextLoading?.Cancel(); animationContext = null; MissionSceneLoader.Invalidate(Document); }
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
    public string Description => $"{Document.Probe.Description} · {Document.Assets.Count:N0} assets";
    public ObservableCollection<AssetItem> Assets { get; }
    public ICollectionView FilteredAssets { get; }
    public ObservableCollection<SceneTreeItem> SceneRoots { get; } = [];
    public CancellationTokenSource Lifetime { get; } = new();
    [ObservableProperty] private string query = "";
    [ObservableProperty] private string kindFilter = "All types";
    [ObservableProperty] private AssetItem? selectedAsset;
    [ObservableProperty] private bool isStale;
    public string[] Kinds { get; }
    public DocumentModel(ZbdDocument doc)
    {
        Document = doc;
        if (doc.Animations != null) { AnimationEdits = new(doc.Animations); AnimationEdits.Changed += () => { contextLoading?.Cancel(); animationContext = null; OnPropertyChanged(nameof(Title)); }; }
        Assets = new(doc.Assets.OrderBy(a => a.Kind == AssetKind.World ? -1 : (int)a.Kind).ThenBy(a => a.Index).Select(a => new AssetItem(a)));
        Kinds = ["All types", .. Assets.Select(a => a.Kind).Distinct().Order()];
        FilteredAssets = CollectionViewSource.GetDefaultView(Assets); FilteredAssets.Filter = Matches;
        if (doc.Scene is GameScene scene)
            foreach (var root in scene.Nodes.Where(n => n.Class == "world")) SceneRoots.Add(new(scene, root.Index, []));
    }
    private bool Matches(object o) => o is AssetItem a && (KindFilter == "All types" || KindFilter == a.Kind) && (Query.Length == 0 || a.Name.Contains(Query, StringComparison.OrdinalIgnoreCase) || a.Identity.Contains(Query, StringComparison.OrdinalIgnoreCase));
    partial void OnQueryChanged(string value) => FilteredAssets.Refresh();
    partial void OnKindFilterChanged(string value) => FilteredAssets.Refresh();
    public void Dispose() { Lifetime.Cancel(); contextLoading?.Cancel(); contextLoading?.Dispose(); Lifetime.Dispose(); foreach (var a in Assets) a.Thumbnail = null; GC.SuppressFinalize(this); }
}
public sealed class InspectorNode
{
    private readonly JsonNode? node;
    private IReadOnlyList<InspectorNode>? children;
    public string Name { get; }
    public string Value { get; }
    public string Label => string.IsNullOrEmpty(Value) ? Name : Name + ": " + Value;
    public IReadOnlyList<InspectorNode> Children => children ??= node switch
    {
        JsonObject obj => obj.Select(p => new InspectorNode(p.Key, p.Value)).ToArray(),
        JsonArray array => array.Select((v, i) => new InspectorNode($"[{i}]", v)).ToArray(),
        _ => []
    };
    public InspectorNode(string name, JsonNode? value)
    {
        Name = name; node = value;
        Value = value switch { JsonObject o when o.ContainsKey("text") => o.Text(), JsonObject o => $"{{{o.Count} fields}}", JsonArray a => $"[{a.Count} items]", null => "null", _ => value.ToString() };
        if (Value.Length > 240) Value = Value[..240] + "…";
    }
    public string FullText => node?.ToJsonString(JsonData.Options) ?? "null";
}
public sealed class SceneTreeItem(GameScene scene, int index, HashSet<int> ancestors)
{
    public GameNode Node => scene.Nodes[index];
    public string Label => $"{Node.Name}  ·  {Node.Class}  #{index}";
    private IReadOnlyList<SceneTreeItem>? children;
    public IReadOnlyList<SceneTreeItem> Children => children ??= ancestors.Contains(index) || ancestors.Count > 128 ? [] : SceneBuilder.Children(Node).Where(i => i >= 0 && i < scene.Nodes.Count).Select(i => new SceneTreeItem(scene, i, [.. ancestors, index])).ToArray();
}
public sealed record SearchHit(string File, AssetKind Kind, int Index, string Name)
{
    public string Display => $"{Name} · {Kind}";
    public string Location => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(File)) + "/" + System.IO.Path.GetFileName(File);
}
public sealed class StudioSettings
{
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
