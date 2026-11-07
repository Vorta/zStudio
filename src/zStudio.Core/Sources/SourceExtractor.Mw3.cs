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
        var files = all.Where(f => f.Relative.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase) && f.Relative.Count(c => c == '/') <= 1).ToList();
        RequireCheckedInputCapacity(files, token);
        Dictionary<string, string> hashes = new(StringComparer.Ordinal); Dictionary<string, int> families = [];
        foreach (var file in files) hashes.Add(file.Path, (await ProbeInputAsync(file, token)).Digest);
        Context context = new(root, writes, token);
        List<WorldSources.MissionWorld> worlds = []; List<(string Name, AnimationPackage Package)> animations = [];
        List<(string Relative, AssetDocument Document)> textures = []; AssetDocument? library = null;
        List<(string Relative, IReadOnlyList<ArchiveSources.Member> Members)> sounds = [];
        long retained = 0;
        try
        {
            writes.CreateDirectory(root); writes.CheckEmptyRoot();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested(); progress?.Report(new(families.Values.Sum(), files.Count, file.Relative));
                byte[] bytes = await ReadInputAsync(file, hashes[file.Path], token);
                var probe = FormatRegistry.Probe(bytes.AsSpan(0, Math.Min(bytes.Length, 36)), bytes.AsSpan(Math.Max(0, bytes.Length - 8)), bytes.Length);
                long reservation = probe.Family == FormatFamily.GameZ ? WorldDecodeReservation(bytes.Length) : probe.Family == FormatFamily.Scripts ? ScriptDecodeReservation(bytes.Length) : 2L * bytes.Length + 65536;
                RequireReconstructionCapacity(file.Relative, retained, reservation, budget);
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
                            context.Notes.Add("Sampled mech motions use data/common/mechs/motions: motion.zbd records temporary compiler paths, so their original authoring folder is not recoverable."); family = "motions";
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
            }
            if (worlds.Count == 0 || animations.Count != worlds.Count) throw new InvalidDataException("MW3 reconstruction needs each campaign's original world and animation package.");
            foreach (var mission in worlds)
                if (!context.Resources.Any(r => r.Archive.Equals($"{mission.Name}/reader.zbd", StringComparison.OrdinalIgnoreCase) && r.Member.Equals("anim.zrd", StringComparison.OrdinalIgnoreCase) && AnimationDefinitionSet.HoldsDefinitions(r.Tree)))
                    throw new InvalidDataException(NotOriginal);
            void Phase(string item) => progress?.Report(new(files.Count, files.Count, item, SourceStage.Reconstructing));
            Phase("sound sources"); await context.ExtractSoundsAsync(sounds, useSoundIds: true);
            Phase("native model boundaries");
            var preliminary = WorldSources.Reconstruct(worlds, n => context.Scripts.GetValueOrDefault(n),
                (id, name) => $"data/{worlds.Single(w => w.Mission == id).Name}/textures/{name}.png", new HashSet<string>(), _ => 0, [], token);
            var librarySources = library == null ? [] : Mw3LibrarySources.Models(library, (_, name) => $"data/common/mechs/textures/{name}.png", token);
            Phase("texture source locations"); await context.ExtractMw3TexturesAsync(textures, worlds, preliminary.Concat(librarySources).ToArray());
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
            return new(root, context.Written, families, all.Except(files).Select(f => f.Relative).ToArray(), context.Notes) { Game = GameVariant.MechWarrior3 };
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
        internal async Task ExtractMw3TexturesAsync(IReadOnlyList<(string Relative, AssetDocument Document)> packs,
            IReadOnlyList<WorldSources.MissionWorld> worlds, IReadOnlyList<WorldSources.Output> models)
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
            foreach (var (relative, document) in packs)
            {
                token.ThrowIfCancellationRequested();
                var assets = document.Assets.Where(a => a.Kind == AssetKind.Texture).ToArray();
                bool imagePack = Path.GetFileName(relative).Equals("rimage.zbd", StringComparison.OrdinalIgnoreCase);
                var imageFolders = imagePack ? TextureSources.PlaceImages(assets.Select(a => a.Name).ToArray(), Resources) : null;
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
                    var decoded = TextureDecoder.Decode(document, asset, token); byte[] png = Export.PngEncoder.Encode(decoded, token);
                    int addressing = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(document.Bytes.Span[((int)asset.Offset + 14)..]) & 3;
                    // WriteAsync retains the first source image. Lower-quality pack variants must not replace
                    // its sampler modes while leaving those pixels in place.
                    if (id > 0) NativeTextureAddressing.TryAdd((id, name), addressing);
                    foreach (string folder in locations)
                    {
                        string path = $"{folder}/{name}.png"; await WriteAsync(path, png); TextureFiles.Add(path); TextureTransparencies.TryAdd(path, TexturePackBuilder.Classify(decoded));
                        if (id > 0) TexturePaths.TryAdd((id, name), path);
                    }
                    if (addressing != 0) TextureAddressing.TryAdd(name, addressing);
                }
            }
            Notes.Add("Texture folders follow the recovered model locations and IMAGE_PATH resources. Unreferenced campaign textures remain in that campaign's textures folder; their original loose-file subfolders are not recorded in the packs.");
        }
    }
}
