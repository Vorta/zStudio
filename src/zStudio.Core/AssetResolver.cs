using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record ResolvedTexture(ZbdDocument Document, AssetRecord Asset, bool Ambiguous);
public sealed class AssetResolver(string root) : IDisposable
{
    public ResourceEditOwnership EditOwnership { get; } = new();
    public string Root { get; } = Path.GetFullPath(root);
    private readonly Dictionary<string, string> missions = new(StringComparer.OrdinalIgnoreCase);
    public string? SelectedMission(string worldPath) { lock (missions) return missions.GetValueOrDefault(Path.GetDirectoryName(Path.GetFullPath(worldPath))!); }
    public void SelectMission(string worldPath, string archive) => TrySelectMission(worldPath, archive, null, false);
    /// <summary>Replace only the selection a load started from, so an older fallback cannot overwrite a newer choice.</summary>
    public bool TrySelectMission(string worldPath, string archive, string? expected) => TrySelectMission(worldPath, archive, expected, true);
    private bool TrySelectMission(string worldPath, string archive, string? expected, bool compare)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(worldPath))!;
        if (!Path.GetDirectoryName(Path.GetFullPath(archive))!.Equals(directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A mission reader must belong to the selected map directory.");
        lock (missions)
        {
            if (compare && !string.Equals(missions.GetValueOrDefault(directory), expected == null ? null : Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) return false;
            missions[directory] = Path.GetFullPath(archive); return true;
        }
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (ZbdDocument Document, long Used)> cache = new(StringComparer.OrdinalIgnoreCase);
    private long clock;
    private readonly object snapshotGate = new();
    private readonly Dictionary<Guid, ZbdDocument[]> workspaceSnapshots = [];
    public long SnapshotRevision { get; private set; }
    public event Action? WorkspaceSnapshotsChanged;
    public bool IsWorkspaceSnapshot(ZbdDocument document)
    { lock (snapshotGate) return publishedSnapshots.TryGetValue(document.Path, out var published) && ReferenceEquals(published, document); }
    public ZbdDocument? WorkspaceSnapshot(string path, Guid excludingOwner)
    { lock (snapshotGate) return workspaceSnapshots.Where(p => p.Key != excludingOwner).SelectMany(p => p.Value).SingleOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase)); }
    private IReadOnlyDictionary<string, ZbdDocument> publishedSnapshots = new Dictionary<string, ZbdDocument>(StringComparer.OrdinalIgnoreCase);
    public void SetWorkspaceSnapshots(Guid owner, IEnumerable<ZbdDocument> documents)
    {
        lock (snapshotGate)
        {
            var values = documents.ToArray();
            // Closing a document that published nothing changes no source; it must not expire other previews.
            if (values.Length == 0 && !workspaceSnapshots.ContainsKey(owner)) return;
            // Build the complete replacement before changing either published state or ownership history.
            var next = workspaceSnapshots.Where(p => p.Key != owner).SelectMany(p => p.Value).Concat(values)
                .ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
            if (values.Length == 0) workspaceSnapshots.Remove(owner); else workspaceSnapshots[owner] = values;
            publishedSnapshots = next; SnapshotRevision++;
        }
        WorkspaceSnapshotsChanged?.Invoke();
    }
    public IEnumerable<string> ResourceDirectories(string context) => ResourceDirectories(context, new CompiledInventory());
    internal IEnumerable<string> ResourceDirectories(string context, CompiledInventory inventory)
    {
        inventory.Path(context.Length); inventory.Path(Root.Length);
        string directory = Path.GetDirectoryName(context)!;
        var candidates = new List<string> { directory, Root };
        string? parent = Path.GetDirectoryName(directory);
        if (parent != null)
        {
            inventory.Path(parent.Length + 10L);
            string image = Path.Combine(parent, "image.zbd");
            if (File.Exists(image) && FormatRegistry.Probe(image).Family == FormatFamily.TexturePack) candidates.Add(parent);
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }
    public string[] TexturePacks(string context, CancellationToken token = default) => TexturePacks(context, new CompiledInventory(token));
    internal string[] TexturePacks(string context, CompiledInventory inventory)
    {
        inventory.Path(context.Length);
        string directory = Path.GetDirectoryName(context)!;
        List<(string Path, int Priority)> paths = []; HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in ResourceDirectories(context, inventory))
            foreach (string path in inventory.Files(folder, sort: false))
            {
                inventory.Path(path.Length);
                string name = Path.GetFileName(path);
                if (!Path.GetDirectoryName(path)!.Equals(directory, StringComparison.OrdinalIgnoreCase) && !name.Contains("image", StringComparison.OrdinalIgnoreCase) && !name.Contains("mechtex", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(path) && FormatRegistry.Probe(path).Family == FormatFamily.TexturePack)
                { inventory.Rows(1); paths.Add((path, PackPriority(Path.GetFileNameWithoutExtension(path)))); }
            }
        inventory.Sort(paths, (a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path), p => p.Path);
        inventory.Rows(paths.Count); return paths.Select(p => p.Path).ToArray();
    }
    private static int PackPriority(string name)
    {
        var m = Regex.Match(name, @"^(r?texture)(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success && int.TryParse(m.Groups[2].Value, out int n) ? (m.Groups[1].Value.StartsWith('r') ? 1000 : 2000) + Math.Min(n, 999) : name.Contains("image", StringComparison.OrdinalIgnoreCase) ? -100 : 0;
    }
    public Task<ZbdDocument> OpenCachedAsync(string path, CancellationToken token) => OpenCachedAsync(path, FormatRegistry.MaximumDocumentBytes, token);
    internal async Task<ZbdDocument> OpenCachedAsync(string path, long maximumBytes, CancellationToken token, TextureLookupOperation? lookup = null)
    {
        if (maximumBytes < 0 || maximumBytes > FormatRegistry.MaximumDocumentBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        token.ThrowIfCancellationRequested();
        lock (snapshotGate) if (publishedSnapshots.TryGetValue(path, out var snapshot)) return Admitted(snapshot);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(path, out var found) && found.Document.Stamp == FileStamp.Read(path)) { var retained = Admitted(found.Document); cache[path] = (retained, ++clock); return retained; }
            if (lookup != null) maximumBytes = Math.Min(maximumBytes, lookup.AdmitColdRead(FileStamp.Read(path).Length));
            var doc = await FormatRegistry.Default.OpenAsync(path, maximumBytes, token).ConfigureAwait(false); cache[path] = (doc, ++clock);
            while (cache.Count > 4) cache.Remove(cache.MinBy(p => p.Value.Used).Key);
            return doc;
        }
        finally { gate.Release(); }
        ZbdDocument Admitted(ZbdDocument document)
        {
            if (document.Bytes.Length > maximumBytes) throw new InvalidDataException("The texture pack exceeds the remaining edit buffer budget.");
            return document;
        }
    }
    public TextureLookupOperation BeginTextureLookup(string context, string? preferredPack = null) => new(this, context, preferredPack);
    public Task<ResolvedTexture?> ResolveTextureAsync(string context, string name, string? preferredPack, CancellationToken token)
        => BeginTextureLookup(context, preferredPack).ResolveAsync(name, token);
    internal bool HasTextureLookupStamp(string path, FileStamp stamp)
    {
        lock (snapshotGate) if (publishedSnapshots.TryGetValue(path, out var snapshot)) return snapshot.Stamp == stamp;
        return FileStamp.Read(path) == stamp;
    }
    public async Task InvalidateAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { foreach (string path in paths) cache.Remove(path); }
        finally { gate.Release(); }
    }
    public void Dispose() { cache.Clear(); gate.Dispose(); }
}
