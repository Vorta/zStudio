using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record ResolvedTexture(ZbdDocument Document, AssetRecord Asset, bool Ambiguous);
public sealed class AssetResolver(string root) : IDisposable
{
    public ResourceEditOwnership EditOwnership { get; } = new();
    public string Root { get; } = Path.GetFullPath(root);
    private readonly Dictionary<string, string> missions = new(StringComparer.OrdinalIgnoreCase);
    public string? SelectedMission(string worldPath) { lock (missions) return missions.GetValueOrDefault(Path.GetDirectoryName(worldPath)!); }
    public void SelectMission(string worldPath, string archive)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(worldPath))!;
        if (!Path.GetDirectoryName(Path.GetFullPath(archive))!.Equals(directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A mission reader must belong to the selected map directory.");
        lock (missions) missions[directory] = Path.GetFullPath(archive);
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (ZbdDocument Document, long Used)> cache = new(StringComparer.OrdinalIgnoreCase);
    private long clock;
    private readonly object snapshotGate = new();
    private readonly Dictionary<Guid, ZbdDocument[]> workspaceSnapshots = [];
    public long SnapshotRevision { get; private set; }
    public event Action? WorkspaceSnapshotsChanged;
    public ZbdDocument? WorkspaceSnapshot(string path, Guid excludingOwner)
    { lock (snapshotGate) return workspaceSnapshots.Where(p => p.Key != excludingOwner).SelectMany(p => p.Value).SingleOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase)); }
    private IReadOnlyDictionary<string, ZbdDocument> publishedSnapshots = new Dictionary<string, ZbdDocument>(StringComparer.OrdinalIgnoreCase);
    public void SetWorkspaceSnapshots(Guid owner, IEnumerable<ZbdDocument> documents)
    {
        lock (snapshotGate)
        {
            var values = documents.ToArray();
            // Build the complete replacement before changing either published state or ownership history.
            var next = workspaceSnapshots.Where(p => p.Key != owner).SelectMany(p => p.Value).Concat(values)
                .ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
            if (values.Length == 0) workspaceSnapshots.Remove(owner); else workspaceSnapshots[owner] = values;
            publishedSnapshots = next; SnapshotRevision++;
        }
        WorkspaceSnapshotsChanged?.Invoke();
    }
    public IEnumerable<string> ResourceDirectories(string context)
    {
        string directory = Path.GetDirectoryName(context)!;
        var candidates = new List<string> { directory, Root };
        string? parent = Path.GetDirectoryName(directory);
        if (parent != null && File.Exists(Path.Combine(parent,"image.zbd")) && FormatRegistry.Probe(Path.Combine(parent,"image.zbd")).Family == FormatFamily.TexturePack) candidates.Add(parent);
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }
    public string[] TexturePacks(string context)
    {
        string directory = Path.GetDirectoryName(context)!;
        var paths = ResourceDirectories(context).SelectMany(Directory.EnumerateFiles);
        return paths.Where(p => Path.GetExtension(p).Equals(".zbd", StringComparison.OrdinalIgnoreCase))
            .Where(p => Path.GetDirectoryName(p)!.Equals(directory, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Contains("image", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Contains("mechtex", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => FormatRegistry.Probe(p).Family == FormatFamily.TexturePack)
            .OrderByDescending(p => PackPriority(Path.GetFileNameWithoutExtension(p))).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static int PackPriority(string name)
    {
        var m = Regex.Match(name, @"^(r?texture)(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success && int.TryParse(m.Groups[2].Value, out int n) ? (m.Groups[1].Value.StartsWith('r') ? 1000 : 2000) + Math.Min(n, 999) : name.Contains("image", StringComparison.OrdinalIgnoreCase) ? -100 : 0;
    }
    public async Task<ZbdDocument> OpenCachedAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (snapshotGate) if (publishedSnapshots.TryGetValue(path, out var snapshot)) return snapshot;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(path, out var found) && found.Document.Stamp == FileStamp.Read(path)) { cache[path] = (found.Document, ++clock); return found.Document; }
            var doc = await FormatRegistry.Default.OpenAsync(path, token).ConfigureAwait(false); cache[path] = (doc, ++clock);
            while (cache.Count > 4) cache.Remove(cache.MinBy(p => p.Value.Used).Key);
            return doc;
        }
        finally { gate.Release(); }
    }
    public async Task<ResolvedTexture?> ResolveTextureAsync(string context, string name, string? preferredPack, CancellationToken token)
    {
        string[] packs = TexturePacks(context);
        if (preferredPack != null) packs = packs.OrderByDescending(p => p.Equals(preferredPack, StringComparison.OrdinalIgnoreCase)).ToArray();
        string key = Path.GetFileNameWithoutExtension(name);
        foreach (string path in packs)
        {
            token.ThrowIfCancellationRequested(); var doc = await OpenCachedAsync(path, token).ConfigureAwait(false);
            var matches = doc.Assets.Where(a => a.Kind == AssetKind.Texture && (a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(a.Name).Equals(key, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length > 0) return new(doc, matches[0], matches.Length > 1);
        }
        return null;
    }
    public async Task InvalidateAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { foreach (string path in paths) cache.Remove(path); }
        finally { gate.Release(); }
    }
    public void Dispose() { cache.Clear(); gate.Dispose(); }
}
