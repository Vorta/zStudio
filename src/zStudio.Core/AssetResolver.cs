using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record ResolvedTexture(ZbdDocument Document, AssetRecord Asset, bool Ambiguous);
public sealed class AssetResolver(string root) : IDisposable
{
    public string Root { get; } = Path.GetFullPath(root);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (ZbdDocument Document, long Used)> cache = new(StringComparer.OrdinalIgnoreCase);
    private long clock;
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
            .Where(p => Path.GetDirectoryName(p)!.Equals(directory, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Contains("image", StringComparison.OrdinalIgnoreCase))
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
