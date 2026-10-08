using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record TextureTarget(string Path, int Index);
public sealed record TextureTargetCandidate(string Path, int? Index, string Name, int Width, int Height, bool Ambiguous, string? Problem);
public sealed class TextureEditSession : ContentEditSession
{
    public const int MaximumPngBytes = 16 * 1024 * 1024;
    private IReadOnlyDictionary<string, TexturePackEdit> Packs => (IReadOnlyDictionary<string, TexturePackEdit>)Current.State;
    private HashSet<string> SaveAsAliases() => Documents.Where(d => !d.Path.Equals(TargetPath(d.Path), StringComparison.OrdinalIgnoreCase))
        .Select(d => TargetPath(d.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public TextureEditSession(ZbdDocument source) : base(source, Initial(source)) { }
    private static IReadOnlyDictionary<string, TexturePackEdit> Initial(ZbdDocument source)
    {
        if (source.Probe.Family != FormatFamily.TexturePack || source.Probe.Version != 1) throw new InvalidDataException("A v1 texture pack is required.");
        return new Dictionary<string, TexturePackEdit>(StringComparer.OrdinalIgnoreCase) { [source.Path] = new(source, new Dictionary<int, TexturePayload>(), []) };
    }
    public async Task<IReadOnlyList<TextureTargetCandidate>> DiscoverTargetsAsync(int index, AssetResolver resolver, CancellationToken token = default)
    {
        var before = Current; var aliases = SaveAsAliases();
        return await Task.Run(async () =>
        {
        var selected = before.Documents[SourcePath].Assets.SingleOrDefault(a => a.Index == index) ?? throw new InvalidDataException("Texture no longer exists.");
        List<TextureTargetCandidate> results = [];
        string sourceDirectory = Path.GetDirectoryName(SourcePath)!;
        var paths = resolver.TexturePacks(SourcePath, token).Where(p => !aliases.Contains(p) && Path.GetDirectoryName(p)!.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paths.Length > 64) throw new InvalidDataException("Discovery supports at most 64 sibling packs. Select explicit targets instead.");
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            var doc = before.Documents.TryGetValue(path, out var current) ? current : await resolver.OpenCachedAsync(path, token);
            var matches = doc.Assets.Where(a => a.Name.Equals(selected.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) results.Add(new(path, null, selected.Name, 0, 0, false, "No matching texture."));
            foreach (var asset in matches)
            {
                var info = asset.Content as TextureInfo;
                results.Add(new(path, asset.Index, asset.Name, info?.Width ?? 0, info?.Height ?? 0, matches.Length > 1,
                    doc.Diagnostics.Any(d => d.Severity == "Error") || info == null ? "Malformed texture pack." : null));
                if (results.Count > 16384) throw new InvalidDataException("Too many ambiguous candidates. Select explicit record targets instead.");
            }
        }
        return results;
        }, token);
    }
    public Task<PreparedContentEdit> PrepareAsync(string pngPath, int? index, string name, IReadOnlyList<TextureTarget>? targets,
        AssetResolver resolver, CancellationToken token = default) => PrepareAsync(pngPath, index, name, targets, resolver, FormatRegistry.MaximumDocumentBytes, token);
    internal async Task<PreparedContentEdit> PrepareAsync(string pngPath, int? index, string name, IReadOnlyList<TextureTarget>? targets,
        AssetResolver resolver, long maximumSourceBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var before = Current; var packs = new Dictionary<string, TexturePackEdit>((IReadOnlyDictionary<string, TexturePackEdit>)before.State, StringComparer.OrdinalIgnoreCase);
        EditBufferBudget sources = new(maximumSourceBytes);
        foreach (var pack in packs.Values) { token.ThrowIfCancellationRequested(); sources.Document(pack.Source); }
        using var input = new FileStream(pngPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumPngBytes) throw new InvalidDataException("PNG input exceeds 16 MiB.");
        byte[] png = new byte[(int)input.Length]; await input.ReadExactlyAsync(png, token);
        var image = await Task.Run(() => ModelImport.ReadPng(png, token, 4096), token);
        var chosen = targets?.Select(t => t with { Path = Path.GetFullPath(t.Path) }).ToArray() ?? (index is int i ? [new TextureTarget(SourcePath, i)] : []);
        var aliases = SaveAsAliases();
        if (chosen.Any(t => aliases.Contains(t.Path))) throw new InvalidDataException("A current Save As destination is already part of this texture batch. Select its original source record instead of adding the saved copy as a variant.");
        if (index == null && chosen.Length > 0) throw new InvalidDataException("Adding a texture targets only the open pack.");
        if (index != null && (chosen.Length is < 1 or > 64 || chosen.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != chosen.Length || !chosen.Any(t => t.Path.Equals(SourcePath, StringComparison.OrdinalIgnoreCase) && t.Index == index)))
            throw new InvalidDataException("Choose the selected texture and at most one record per sibling pack (up to 64 packs).");
        var baselines = new Dictionary<string, ZbdDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in chosen)
        {
            if (!Path.GetDirectoryName(target.Path)!.Equals(Path.GetDirectoryName(SourcePath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Variant targets must be sibling mission texture packs.");
            if (!packs.ContainsKey(target.Path))
            {
                var doc = await resolver.OpenCachedAsync(target.Path, sources.Remaining, token);
                sources.Document(doc);
                if (doc.Probe.Family != FormatFamily.TexturePack || doc.Probe.Version != 1 || doc.Diagnostics.Any(d => d.Severity == "Error")) throw new InvalidDataException("An intact v1 texture pack is required: " + target.Path);
                packs[target.Path] = new(doc, new Dictionary<int, TexturePayload>(), []); baselines[target.Path] = doc;
                if (packs.Values.Sum(p => (long)p.Source.Bytes.Length) > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException("Texture batch exceeds 512 MiB.");
            }
        }
        if (packs.Values.Sum(p => (long)p.Source.Bytes.Length) > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException("Selected texture packs exceed the 512 MiB batch budget.");
        return await Task.Run(() =>
        {
            var documents = new Dictionary<string, ZbdDocument>(before.Documents, StringComparer.OrdinalIgnoreCase);
            if (index == null)
            {
                TexturePackWriter.ValidateTextureName(name);
                if (documents[SourcePath].Assets.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(a.Name).Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Choose a new texture name; texture lookup also compares names without extensions.");
                var state = packs[SourcePath]; packs[SourcePath] = state with { Added = [.. state.Added, TexturePackWriter.Encode(name, image, token: token).Payload] };
                Publish(SourcePath);
            }
            else
            {
                var main = documents[SourcePath].Assets.SingleOrDefault(a => a.Index == index) ?? throw new InvalidDataException("Texture no longer exists.");
                if (main.Content is not TextureInfo mainInfo || mainInfo.Width != image.Width || mainInfo.Height != image.Height) throw new InvalidDataException("PNG dimensions must match the selected texture. Other selected variants retain their own dimensions.");
                foreach (var target in chosen)
                {
                    token.ThrowIfCancellationRequested(); var state = packs[target.Path];
                    var doc = documents.GetValueOrDefault(target.Path) ?? state.Source;
                    var asset = doc.Assets.SingleOrDefault(a => a.Index == target.Index) ?? throw new InvalidDataException("Missing target texture.");
                    if (!asset.Name.Equals(main.Name, StringComparison.OrdinalIgnoreCase) || asset.Content is not TextureInfo info) throw new InvalidDataException("Variant targets must identify the same texture name explicitly.");
                    var resized = TexturePackWriter.Resize(image, info.Width, info.Height, token);
                    if (TextureDecoder.Decode(doc, asset, token).Rgba.AsSpan().SequenceEqual(resized.Rgba)) { documents[target.Path] = doc; continue; }
                    var encoded = TexturePackWriter.Encode(asset.Name, resized, doc, asset, token);
                    if (target.Index < state.Source.Assets.Count)
                    { var replacements = new Dictionary<int, TexturePayload>(state.Replacements) { [target.Index] = encoded.Payload }; packs[target.Path] = state with { Replacements = replacements }; }
                    else { var added = state.Added.ToArray(); added[target.Index - state.Source.Assets.Count] = encoded.Payload; packs[target.Path] = state with { Added = added }; }
                    Publish(target.Path);
                    if (encoded.QuantizedColors > 0) documents[target.Path].Diagnostics.Add(new("Info", $"Texture {asset.Index}: {encoded.QuantizedColors} RGB565 colors mapped to a private {encoded.PaletteColors}-color palette.", asset.Index));
                }
            }
            if (documents.Values.Sum(d => (long)d.Bytes.Length) > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException("Prepared texture batch exceeds 512 MiB.");
            return new PreparedContentEdit(before, new(documents, packs), baselines);
            void Publish(string path)
            {
                long total = packs.Values.Sum(p => (long)p.Source.Bytes.Length + p.Added.Count * 40L + p.Replacements.Values.Sum(v => (long)v.Bytes.Length) + p.Added.Sum(v => (long)v.Bytes.Length));
                if (total > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException("Prepared texture batch exceeds 512 MiB.");
                var state = packs[path]; var bytes = TexturePackWriter.Write(state, token);
                var doc = FormatRegistry.Default.OpenBytes(path, bytes, state.Source.Stamp, token);
                if (doc.Diagnostics.Any(d => d.Severity == "Error")) throw new InvalidDataException("Texture output failed shared-reader verification.");
                documents[path] = doc;
            }
        }, token);
    }
}
