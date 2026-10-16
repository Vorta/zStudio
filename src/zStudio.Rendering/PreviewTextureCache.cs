using System.IO;
using zStudio.Core;
using zStudio.Core.Formats;

namespace zStudio.Rendering;

/// <summary>Managed pixel storage across current and pending generations of one viewport.</summary>
internal sealed class TextureMemoryBudget(long maximumBytes = TextureMemoryBudget.MaximumBytes)
{
    internal const long MaximumBytes = 256L * 1024 * 1024;
    private readonly object gate = new();
    private long used;
    internal long Used { get { lock (gate) return used; } }
    internal Reservation Reserve(long bytes)
    {
        lock (gate)
        {
            if (bytes < 0 || bytes > maximumBytes - used)
                throw new InvalidDataException("Preview textures exceed the 256 MiB decoded pixel/mask allowance. Choose a smaller texture pack or preview fewer textures.");
            used += bytes; return new(this, bytes);
        }
    }
    internal sealed class Reservation(TextureMemoryBudget owner, long bytes) : IDisposable
    {
        public void Dispose() { lock (owner.gate) { owner.used -= bytes; bytes = 0; } }
    }
}

/// <summary>Serial decoding, immutable-record deduplication and preallocation reservations.
/// Does not retain resolved documents. A canceled owner keeps its allowance until all users finish.</summary>
internal sealed class PreviewTextureCache : IDisposable
{
    private readonly TextureMemoryBudget budget;
    private readonly Func<string, CancellationToken, Task<Input?>> resolve;
    private readonly bool masks;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource stopped = new();
    private readonly object lifetime = new();
    private readonly Dictionary<object, LoadedPreviewTexture> images = [];
    private readonly List<TextureMemoryBudget.Reservation> reservations = [];
    private int users;
    private bool disposed;

    internal PreviewTextureCache(TextureMemoryBudget budget, TextureLookupOperation lookup, bool masks)
        : this(budget, lookup.ResolveAsync, masks, (match, token) => TextureDecoder.Decode(match.Document, match.Asset, token)) { }
    internal PreviewTextureCache(TextureMemoryBudget budget, Func<string, CancellationToken, Task<ResolvedTexture?>> resolve,
        bool masks, Func<ResolvedTexture, CancellationToken, DecodedImage> decode)
    {
        this.budget = budget; this.masks = masks;
        this.resolve = async (name, token) =>
        {
            var match = await resolve(name, token).ConfigureAwait(false);
            if (match == null) return null;
            if (match.Asset.Content is not TextureInfo info) throw new InvalidDataException("Resolved asset is not a texture.");
            return new(new Identity(match.Document.Path, match.Document.Stamp, match.Asset.Index, match.Asset.Offset, info),
                info.Width, info.Height, match.Asset.Index, match.Ambiguous, t => decode(match, t));
        };
    }
    internal PreviewTextureCache(TextureMemoryBudget budget, zStudio.Core.Sources.SourceSceneSnapshot source, bool masks)
    {
        this.budget = budget; this.masks = masks;
        resolve = (name, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(source.Textures.TryGetValue(name, out var texture)
                ? new Input(texture.Hash, texture.Width, texture.Height, -1, false, texture.Decode) : null);
        };
    }
    private sealed record Input(object Identity, int Width, int Height, int Index, bool Ambiguous, Func<CancellationToken, DecodedImage> Decode);

    // Callers that hand buffers to the UI retain this across their continuation too.
    internal IDisposable Retain()
    {
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(disposed, this); users++; return new Use(this);
        }
    }
    private void Release()
    {
        lock (lifetime) { users--; if (disposed && users == 0) ReleaseImages(); }
    }
    private void ReleaseImages()
    {
        images.Clear(); foreach (var reservation in reservations) reservation.Dispose(); reservations.Clear();
    }
    public void Dispose()
    {
        lock (lifetime)
        {
            if (disposed) return;
            disposed = true; if (users == 0) ReleaseImages();
        }
        stopped.Cancel();
    }
    internal async Task<LoadedPreviewTexture?> GetAsync(string name, CancellationToken token)
    {
        using var use = Retain();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, stopped.Token);
        token = cancel.Token;
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var match = await resolve(name, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (match == null) return null;
            var key = match.Identity;
            if (images.TryGetValue(key, out var retained)) return retained;
            long bytes = checked((long)match.Width * match.Height * 4);
            if (match.Width <= 0 || match.Height <= 0) throw new InvalidDataException("Invalid texture dimensions.");
            // Reserve the possible mask before either allocation. Opaque images release
            // that part immediately; no second image can race the shared allowance.
            var pixels = budget.Reserve(bytes);
            TextureMemoryBudget.Reservation? mask = null;
            try
            {
                if (masks) mask = budget.Reserve(bytes);
                var image = match.Decode(token);
                if (image.Width != match.Width || image.Height != match.Height || image.Rgba.LongLength != bytes) throw new InvalidDataException("Decoded texture dimensions differ from the frozen texture header.");
                bool alpha = false;
                for (int i = 3; i < image.Rgba.Length; i += 4)
                {
                    if ((i & 16383) == 3) token.ThrowIfCancellationRequested();
                    if (image.Rgba[i] != 255) { alpha = true; break; }
                }
                byte[]? white = null;
                if (masks && alpha)
                {
                    white = new byte[image.Rgba.Length];
                    for (int i = 0; i < white.Length; i += 4)
                    {
                        if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
                        white[i] = white[i + 1] = white[i + 2] = 255; white[i + 3] = image.Rgba[i + 3];
                    }
                }
                else { mask?.Dispose(); mask = null; }
                token.ThrowIfCancellationRequested();
                var result = new LoadedPreviewTexture(image, alpha, white, match.Index, match.Ambiguous);
                images.Add(key, result); reservations.Add(pixels);
                if (mask != null) reservations.Add(mask);
                return result;
            }
            catch { pixels.Dispose(); mask?.Dispose(); throw; }
        }
        finally { serial.Release(); }
    }
    internal static string Label(string name) => name.Length <= 128 ? name : name[..128] + "…";
    private sealed class Use(PreviewTextureCache owner) : IDisposable
    {
        private PreviewTextureCache? retained = owner;
        public void Dispose() => Interlocked.Exchange(ref retained, null)?.Release();
    }
    private sealed record Identity(string Path, FileStamp Stamp, int Index, long Offset, TextureInfo Info);
}

internal sealed record LoadedPreviewTexture(DecodedImage Image, bool Alpha, byte[]? WhiteAlphaMask, int RecordIndex, bool Ambiguous);
