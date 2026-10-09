using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

/// <summary>Bounded discovery and lookup shared by all textures in one preview or export.
/// Indexes retain ordinals and stamps, never the raw pack documents.</summary>
public sealed class TextureLookupOperation
{
    public const long MaximumWork = 1_000_000;
    public const long MaximumColdReadBytes = 512L * 1024 * 1024;
    private readonly AssetResolver resolver;
    private readonly string context;
    private readonly string? preferred;
    private readonly long revision;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, PackIndex> indexes = new(StringComparer.OrdinalIgnoreCase);
    private string[]? packs;
    private long work;
    private readonly AssetReadBudget reads;

    internal TextureLookupOperation(AssetResolver resolver, string context, string? preferred,
        long maximumWork = MaximumWork, long maximumColdReadBytes = MaximumColdReadBytes,
        long maximumDiscoveryBytes = MaximumColdReadBytes)
    {
        if (maximumWork < 0 || maximumColdReadBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumWork));
        this.resolver = resolver; this.context = context; this.preferred = preferred;
        revision = resolver.SnapshotRevision; work = maximumWork;
        reads = new(maximumDiscoveryBytes, maximumColdReadBytes);
    }
    private void Spend(long units, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (units < 0 || units > work)
        { work = -1; throw new InvalidDataException("Texture lookup exceeds its discovery/index work allowance; use fewer textures or packs."); }
        work -= units;
    }
    private void CheckRevision()
    {
        reads.Check();
        if (revision != resolver.SnapshotRevision) throw new InvalidDataException("Texture sources changed during lookup; reload the preview or retry the export.");
    }
    public async Task<ResolvedTexture?> ResolveAsync(string name, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckRevision(); Spend(name.Length + 1L, token);
            if (packs == null)
            {
                var inventory = new CompiledInventory(token);
                var discovered = resolver.TexturePacks(context, inventory);
                Spend(discovered.Length, token);
                if (preferred != null)
                {
                    List<string> ordered = [.. discovered];
                    inventory.Sort(ordered, (a, b) => b.Equals(preferred, StringComparison.OrdinalIgnoreCase).CompareTo(a.Equals(preferred, StringComparison.OrdinalIgnoreCase)), p => p);
                    discovered = ordered.ToArray();
                }
                packs = discovered;
            }
            string key = Path.GetFileNameWithoutExtension(name);
            foreach (string path in packs)
            {
                Spend(key.Length + path.Length + 1L, token); CheckRevision();
                ZbdDocument? document = null;
                if (!indexes.TryGetValue(path, out var index))
                {
                    document = await resolver.OpenCachedAsync(path, FormatRegistry.MaximumDocumentBytes, token, reads).ConfigureAwait(false);
                    CheckRevision();
                    Dictionary<string, Match> names = new(StringComparer.OrdinalIgnoreCase);
                    for (int ordinal = 0; ordinal < document.Assets.Count; ordinal++)
                    {
                        var asset = document.Assets[ordinal]; Spend(asset.Name.Length + 1L, token);
                        if (asset.Kind != AssetKind.Texture) continue;
                        // Exact names necessarily have the same basename too. Count every record
                        // once and keep list order even when serialized indices contain gaps.
                        string stem = Path.GetFileNameWithoutExtension(asset.Name);
                        if (names.TryGetValue(stem, out var match)) names[stem] = match with { Ambiguous = true };
                        else names.Add(stem, new(ordinal, false));
                    }
                    index = new(document.Stamp, names); indexes.Add(path, index);
                }
                else if (!resolver.HasTextureLookupStamp(path, index.Stamp))
                    throw new InvalidDataException("A texture pack changed during lookup; reload the preview or retry the export.");
                CheckRevision();
                if (!index.Names.TryGetValue(key, out var found)) continue;
                document ??= await resolver.OpenCachedAsync(path, FormatRegistry.MaximumDocumentBytes, token, reads).ConfigureAwait(false);
                CheckRevision();
                if (document.Stamp != index.Stamp) throw new InvalidDataException("A texture pack changed during lookup; retry the operation.");
                return new(document, document.Assets[found.Ordinal], found.Ambiguous);
            }
            CheckRevision(); return null;
        }
        finally { gate.Release(); }
    }
    private sealed record Match(int Ordinal, bool Ambiguous);
    private sealed record PackIndex(FileStamp Stamp, Dictionary<string, Match> Names);
}

/// <summary>One discovery's raw input allowance, including borrowed snapshots and warm documents.
/// Stores only paths and lengths, so its accounting cannot keep evicted raw buffers alive.</summary>
internal sealed class AssetReadBudget(long maximumBytes = TextureLookupOperation.MaximumColdReadBytes,
    long maximumColdBytes = TextureLookupOperation.MaximumColdReadBytes)
{
    private readonly Dictionary<string, long> documents = new(StringComparer.OrdinalIgnoreCase);
    private long remaining = maximumBytes, coldRemaining = maximumColdBytes;
    internal bool Exhausted => remaining < 0 || coldRemaining < 0;
    internal void Check()
    {
        if (Exhausted)
            throw new InvalidDataException("Asset discovery exhausted its byte allowance; use fewer resource files or start a new operation.");
    }
    internal void Document(string path, long bytes)
    {
        Check();
        if (documents.TryGetValue(path, out long previous))
        {
            if (previous != bytes) { remaining = -1; Check(); }
            return;
        }
        if (bytes < 0 || bytes > remaining) { remaining = -1; Check(); }
        remaining -= bytes; documents.Add(path, bytes);
    }
    internal void ColdRead(long bytes)
    {
        Check();
        if (bytes < 0 || bytes > coldRemaining) { coldRemaining = -1; Check(); }
        coldRemaining -= bytes;
    }
}
