using System.Text.Json;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceExtractor
{
    private static async Task<SourceReconstructionReport> ExtractMw3Async(string root, List<Input> all, Writes writes, long budget,
        IProgress<SourceProgress>? progress, CancellationToken token)
    {
        var files = all.Where(f => f.Relative.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase) && f.Relative.Count(c => c == '/') <= 1
            || !f.Relative.Contains('/') && f.Relative.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).ToList();
        RequireCheckedInputCapacity(files, token);
        Dictionary<string, string> hashes = new(StringComparer.Ordinal); Dictionary<string, int> families = [];
        foreach (var file in files) hashes.Add(file.Path, (await ProbeInputAsync(file, token)).Digest);
        Context context = new(root, writes, token);
        List<WorldSources.MissionWorld> worlds = []; List<(string Name, AnimationPackage Package)> animations = [];
        List<(string Relative, AssetDocument Document)> textures = []; AssetDocument? library = null;
        List<(string Relative, IReadOnlyList<ArchiveSources.Member> Members)> sounds = [];
        HashSet<Input> consumed = [];
        long retained = 0;
        try
        {
            writes.CreateDirectory(root); writes.CheckEmptyRoot();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested(); progress?.Report(new(families.Values.Sum(), files.Count, file.Relative));
                if (file.Relative.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    if (file.Length > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException($"{file.Relative}: loose WAV exceeds the document size limit.");
                    RequireReconstructionCapacity(file.Relative, retained, file.Length, budget);
                }
                byte[] bytes = await ReadInputAsync(file, hashes[file.Path], token);
                var probe = FormatRegistry.Probe(bytes.AsSpan(0, Math.Min(bytes.Length, 36)), bytes.AsSpan(Math.Max(0, bytes.Length - 8)), bytes.Length);
                long reservation = probe.Family == FormatFamily.GameZ ? WorldDecodeReservation(bytes.Length) : probe.Family == FormatFamily.Scripts ? ScriptDecodeReservation(bytes.Length) : 2L * bytes.Length + 65536;
                RequireReconstructionCapacity(file.Relative, retained, reservation, budget);
                if (file.Relative.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var format = WaveConverter.Format(bytes);
                        if (WaveDecoder.Read(bytes, token).Encoding != 1 || format.Bits is not (8 or 16) || format.Channels is < 1 or > 2) throw new InvalidDataException("Only mono/stereo 8/16-bit PCM sources are supported.");
                    }
                    catch (InvalidDataException ex) { context.Notes.Add($"{file.Relative}: loose WAV was not imported: {JsonData.ShownText(ex.Message, 256)}"); continue; }
                    Retain(file.Relative, bytes.LongLength);
                    sounds.Add((file.Relative, new[] { new ArchiveSources.Member(0, file.Relative, 0, null, 0, bytes) }));
                    consumed.Add(file); families["loose sounds"] = families.GetValueOrDefault("loose sounds") + 1;
                    continue;
                }
                var doc = FormatRegistry.Default.OpenBytes(file.Relative, bytes, token: token);
                if (doc.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException($"{file.Relative}: {error.Message}");
                string family;
                switch (probe.Family)
                {
                    case FormatFamily.GameZ when probe.Version == 27:
                        var world = GameZWorldReader.FromDocument(doc, token); Retain(file.Relative, Footprint(world));
                        worlds.Add(new(worlds.Count + 1, world, file.Relative.Split('/')[0])); family = "worlds"; break;
                    case FormatFamily.Animation when probe.Version == 39:
                        Retain(file.Relative, 4L * bytes.Length); animations.Add((file.Relative.Split('/')[0], doc.Animations!)); family = "animations"; break;
                    case FormatFamily.Scripts when probe.Version == 7:
                        Retain(file.Relative, ScriptDecodeReservation(bytes.Length)); await context.ExtractScriptsAsync(file.Relative, bytes); family = "scripts"; break;
                    case FormatFamily.TexturePack:
                        Retain(file.Relative, bytes.Length + 2048L * doc.Assets.Count); textures.Add((file.Relative, doc)); family = "textures"; break;
                    case FormatFamily.Archive:
                        if (doc.Scene != null && doc.Assets.Any(a => a.Content is MechAssembly)) { Retain(file.Relative, 16L * bytes.Length); library = doc; family = "models"; }
                        else if (doc.Assets.Count > 0 && doc.Assets.All(a => a.Content is MotionClip))
                        {
                            foreach (var asset in doc.Assets)
                                await context.WriteAsync($"{Mw3LibrarySources.MotionFolder}/{asset.Name}.zan", Mw3LibrarySources.MotionText((MotionClip)asset.Content!, token));
                            family = "motions";
                        }
                        else
                        {
                            var members = ArchiveSources.Read(bytes);
                            if (members.Count > 0 && members.All(m => IsWave(m.Payload.Span))) { Retain(file.Relative, bytes.LongLength + MemberCost * members.Count); sounds.Add((file.Relative, members)); family = "sounds"; }
                            else { Retain(file.Relative, 16L * bytes.LongLength + MemberCost * members.Count); await context.ExtractResourcesAsync(file.Relative, members); family = "resources"; }
                        }
                        break;
                    default: throw new InvalidDataException($"{file.Relative}: unsupported or mixed game data; no incomplete native source project was created.");
                }
                families[family] = families.GetValueOrDefault(family) + 1;
                consumed.Add(file);
            }
            if (worlds.Count == 0 || animations.Count != worlds.Count) throw new InvalidDataException("MW3 reconstruction needs each campaign's original world and animation package.");
            foreach (var mission in worlds)
                if (!context.Resources.Any(r => r.Archive.Equals($"{mission.Name}/reader.zbd", StringComparison.OrdinalIgnoreCase) && r.Member.Equals("anim.zrd", StringComparison.OrdinalIgnoreCase) && AnimationDefinitionSet.HoldsDefinitions(r.Tree)))
                    throw new InvalidDataException(NotOriginal);
            void Phase(string item) => progress?.Report(new(files.Count, files.Count, item, SourceStage.Reconstructing));
            Phase("sound sources"); await context.ExtractSoundsAsync(sounds, useSoundIds: true);
            if (families.GetValueOrDefault("loose sounds") is > 0 and var looseCount)
                context.Notes.Add($"Imported {looseCount} loose WAV sources alongside the archived audio.");
            Phase("native model boundaries");
            var preliminary = WorldSources.Reconstruct(worlds, n => context.Scripts.GetValueOrDefault(n),
                (id, name) => $"data/{worlds.Single(w => w.Mission == id).Name}/textures/{name}.png", new HashSet<string>(), _ => 0, [], token);
            var librarySources = library == null ? [] : Mw3LibrarySources.Models(library, (_, name) => $"data/common/mechs/textures/{name}.png", token);
            Phase("texture source locations"); await context.ExtractMw3TexturesAsync(textures, worlds, preliminary.Concat(librarySources).ToArray(), Retain);
            Phase("native model sources");
            Dictionary<WorldTexture, int> sourceAddressing = new(ReferenceEqualityComparer.Instance);
            foreach (var mission in worlds)
                foreach (var texture in mission.World.Textures) sourceAddressing[texture] = context.NativeTextureAddressing.GetValueOrDefault((mission.Mission, texture.Name.ToLowerInvariant()));
            var models = WorldSources.Reconstruct(worlds, n => context.Scripts.GetValueOrDefault(n),
                (id, name) => context.TexturePaths.GetValueOrDefault((id, name)), context.TextureFiles, n => context.TextureAddressing.GetValueOrDefault(n), context.Notes, token,
                p => context.TextureTransparencies.TryGetValue(p, out var transparency) ? transparency : null,
                textureAddressing: t => sourceAddressing.GetValueOrDefault(t),
                textureDefaults: id => context.NativeTextureAddressing.Where(p => p.Key.Mission == id).ToDictionary(p => p.Key.Name, p => p.Value, StringComparer.OrdinalIgnoreCase));
            foreach (var output in models) await context.WriteAsync(output.Path, output.Bytes);
            if (library != null)
                foreach (var output in Mw3LibrarySources.Models(library, (folder, name) => context.TextureFiles.FirstOrDefault(p => p.Equals(folder.Replace("/models", "/textures", StringComparison.OrdinalIgnoreCase) + "/" + name + ".png", StringComparison.OrdinalIgnoreCase))
                    ?? context.TextureFiles.FirstOrDefault(p => p.Equals(TextureSources.CommonTextures + "/" + name + ".png", StringComparison.OrdinalIgnoreCase))
                    ?? context.TextureFiles.FirstOrDefault(p => p.Equals(TextureSources.EffectsTextures + "/" + name + ".png", StringComparison.OrdinalIgnoreCase)), token))
                    await context.WriteAsync(output.Path, output.Bytes);
            Phase("native animation scripts");
            var scripts = Mw3AnimationSources.Reconstruct(animations.Select(a => new Mw3AnimationSources.Mission(a.Name, a.Package,
                worlds.Single(w => w.Name == a.Name).World.Nodes.Select(n => n.Name).ToArray())).ToArray(), new NativeFiles(root), context.Notes, token, Phase);
            foreach (var output in scripts) await context.ReplaceAsync(output.Path, output.Bytes);
            foreach (var file in files) _ = await ReadInputAsync(file, hashes[file.Path], token);
            progress?.Report(new(files.Count, files.Count, "Done"));
            return new(root, context.Written, families, all.Except(consumed).Select(f => f.Relative).ToArray(), context.Notes) { Game = GameVariant.MechWarrior3 };
        }
        catch
        {
            var (changed, failed) = writes.Remove();
            if (changed.Count > 0 || failed.Count > 0) throw new IOException("MW3 reconstruction stopped; externally changed or locked files were preserved in the destination.");
            throw;
        }
        void Retain(string path, long cost) { RequireReconstructionCapacity(path, retained, cost, budget); retained += cost; }
    }

    private sealed class NativeFiles(string root) : IProjectFiles
    {
        public bool Exists(string path) { SourceProject.RejectNestedLinks(root, path); return SourceRead.FileExists(SourceProject.Resolve(root, path)); }
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested(); SourceProject.RequireSource(path); SourceProject.RejectNestedLinks(root, path);
            using var stream = new FileStream(SourceProject.Resolve(root, path), FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] prefix = new byte[(int)Math.Min(20, stream.Length)]; stream.ReadExactly(prefix); limits.CheckPrefix(prefix, stream.Length); stream.Position = 0;
            byte[] bytes = new byte[stream.Length]; stream.ReadExactly(bytes); limits.Validate(bytes); return bytes;
        }
    }
    private sealed partial class Context
    {
        internal Dictionary<(int Mission, string Name), int> NativeTextureAddressing { get; } = [];
        private sealed record NativeTexture(string Pack, AssetDocument Document, AssetRecord Asset, int Mission, string Scope, string Name, string[] Locations)
        {
            internal TextureInfo Info => (TextureInfo)Asset.Content!;
            internal bool Canonical => Info.PaletteCount == 0 && Path.GetFileName(Pack).ToLowerInvariant() is "rtexture.zbd" or "rmechtex.zbd" or "rimage.zbd";
        }
        internal async Task ExtractMw3TexturesAsync(IReadOnlyList<(string Relative, AssetDocument Document)> packs,
            IReadOnlyList<WorldSources.MissionWorld> worlds, IReadOnlyList<WorldSources.Output> models, Action<string, long> reserve)
        {
            Dictionary<string, HashSet<string>> usage = new(StringComparer.OrdinalIgnoreCase);
            foreach (var output in models.Where(o => o.Path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)))
            {
                token.ThrowIfCancellationRequested(); using var json = JsonDocument.Parse(output.Bytes);
                if (!json.RootElement.TryGetProperty("images", out var images)) continue;
                string folder = Path.GetDirectoryName(output.Path)!.Replace('\\', '/').Replace("/models", "/textures", StringComparison.OrdinalIgnoreCase);
                foreach (var image in images.EnumerateArray())
                {
                    string name = Path.GetFileNameWithoutExtension(image.GetProperty("uri").GetString()!).ToLowerInvariant();
                    if (!usage.TryGetValue(name, out var locations)) usage.Add(name, locations = new(StringComparer.OrdinalIgnoreCase));
                    locations.Add(folder);
                }
            }
            Dictionary<string, List<NativeTexture>> candidates = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (relative, document) in packs.OrderBy(p => p.Relative, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                reserve(relative, 256L * document.Assets.Count);
                var assets = document.Assets.Where(a => a.Kind == AssetKind.Texture).ToArray();
                bool imagePack = Path.GetFileName(relative).Equals("rimage.zbd", StringComparison.OrdinalIgnoreCase);
                var imageFolders = imagePack ? TextureSources.PlaceImages(assets.Select(a => a.Name).ToArray(), Resources) : null;
                var identities = assets.Select((a, i) => (a, Key: imagePack ? imageFolders![i] + "/" + a.Name : a.Name)).ToDictionary(p => p.a, p => p.Key);
                ValidateTextureNames(relative, document, token, identity: a => identities[a]);
                string? campaign = relative.Contains('/') ? relative.Split('/')[0] : null;
                int id = worlds.FirstOrDefault(w => w.Name.Equals(campaign, StringComparison.OrdinalIgnoreCase))?.Mission ?? 0;
                for (int i = 0; i < assets.Length; i++)
                {
                    token.ThrowIfCancellationRequested(); var asset = assets[i]; string name = asset.Name.ToLowerInvariant();
                    if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException($"{relative}: invalid texture source name.");
                    string fallback = imagePack ? imageFolders![i] : campaign == null ? "data/common/mechs/textures" : $"data/{campaign}/textures";
                    var locations = imagePack ? new[] { fallback } : usage.GetValueOrDefault(name)?.Where(p => p.StartsWith("data/common/", StringComparison.OrdinalIgnoreCase) || p.StartsWith($"data/{campaign}/", StringComparison.OrdinalIgnoreCase) || campaign == null && p.StartsWith("data/lab/", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
                    if (!imagePack && campaign != null) locations = locations.Select(p => p.Equals("data/common/mechs/textures", StringComparison.OrdinalIgnoreCase) ? TextureSources.CommonTextures : p).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (locations.Length == 0) locations = [fallback];
                    if (asset.Content is not TextureInfo) throw new InvalidDataException($"{relative}: texture has no validated layout.");
                    reserve(relative, 1024L + 2L * (relative.Length + name.Length) + 128L * locations.Length);
                    string scope = imagePack ? fallback : campaign ?? "mechs";
                    string identity = scope + "/" + name;
                    if (!candidates.TryGetValue(identity, out var list)) candidates.Add(identity, list = []);
                    list.Add(new(relative, document, asset, id, scope, name, locations));
                }
            }
            BoundedDiagnostics diagnostics = new(Notes);
            Dictionary<string, (int Textures, int Alternatives)> summaries = new(StringComparer.Ordinal);
            long compared = 0;
            foreach (var list in candidates.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
            {
                token.ThrowIfCancellationRequested();
                var best = list.OrderByDescending(c => c.Canonical).ThenByDescending(c => (long)c.Info.Width * c.Info.Height)
                    .ThenBy(c => c.Info.PaletteCount == 0 ? 0 : 1).ThenBy(c => c.Pack, StringComparer.Ordinal).ThenBy(c => c.Asset.Index).First();
                foreach (var other in list)
                    if (!ReferenceEquals(other, best) && (!KnownTier(other.Pack) || !KnownTier(best.Pack)) && !Equal(best, other))
                        diagnostics.Add($"Conflicting texture alternative for {best.Scope}/{best.Name}: retained {best.Pack}, skipped {other.Pack}.");
                var decoded = TextureDecoder.Decode(best.Document, best.Asset, token); byte[] png = Export.PngEncoder.Encode(decoded, token);
                int addressing = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(best.Document.Bytes.Span[((int)best.Asset.Offset + 14)..]) & 3;
                if (best.Mission > 0) NativeTextureAddressing.TryAdd((best.Mission, best.Name), addressing);
                foreach (string folder in best.Locations)
                {
                    string path = $"{folder}/{best.Name}.png"; await WriteAsync(path, png); TextureFiles.Add(path);
                    TextureTransparencies.TryAdd(path, TexturePackBuilder.Classify(decoded));
                    if (best.Mission > 0) TexturePaths.TryAdd((best.Mission, best.Name), path);
                }
                if (addressing != 0) TextureAddressing.TryAdd(best.Name, addressing);
                if (list.Count > 1)
                {
                    var summary = summaries.GetValueOrDefault(best.Scope);
                    summaries[best.Scope] = (summary.Textures + 1, summary.Alternatives + list.Count - 1);
                }
            }
            foreach (var (scope, summary) in summaries.OrderBy(p => p.Key, StringComparer.Ordinal))
                diagnostics.Add($"{scope}: selected {summary.Textures} texture sources from quality variants; {summary.Alternatives} alternative copies were consolidated.");
            Notes.Add("Texture folders follow the recovered model locations and IMAGE_PATH resources. Unreferenced campaign textures remain in that campaign's textures folder; their original loose-file subfolders are not recorded in the packs.");

            static bool KnownTier(string path)
            {
                string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (name is "rtexture" or "texture" or "rmechtex" or "rmechtexs" or "rimage") return true;
                foreach (string prefix in new[] { "rtexture", "texture", "rmechtex" })
                    if (name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(prefix.Length), out int size) && size > 0) return true;
                return false;
            }
            bool Equal(NativeTexture a, NativeTexture b)
            {
                var first = a.Document.Slice(a.Asset.Offset, a.Asset.Length); var second = b.Document.Slice(b.Asset.Offset, b.Asset.Length);
                if (first.Length != second.Length) return false;
                if (first.Length > MaximumRetainedBytes - compared) throw new InvalidDataException("Native texture alternative comparisons exceed their work limit.");
                compared += first.Length;
                for (int at = 0; at < first.Length; at += 65536)
                { token.ThrowIfCancellationRequested(); int n = Math.Min(65536, first.Length - at); if (!first.Span.Slice(at, n).SequenceEqual(second.Span.Slice(at, n))) return false; }
                var ap = a.Info.PaletteOffset < 0 ? ReadOnlyMemory<byte>.Empty : a.Document.Bytes.Slice(a.Info.PaletteOffset, a.Info.PaletteLength);
                var bp = b.Info.PaletteOffset < 0 ? ReadOnlyMemory<byte>.Empty : b.Document.Bytes.Slice(b.Info.PaletteOffset, b.Info.PaletteLength);
                if (ap.Length > MaximumRetainedBytes - compared) throw new InvalidDataException("Native palette alternative comparisons exceed their work limit.");
                compared += ap.Length;
                return ap.Span.SequenceEqual(bp.Span);
            }
        }
    }
}
