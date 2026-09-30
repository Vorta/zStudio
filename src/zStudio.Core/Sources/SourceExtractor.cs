using System.Text;
using System.Text.Json;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>Reconstruct a source project from a shipped RECOIL data folder, verifying that every output packs back exactly.</summary>
public static class SourceExtractor
{
    public const int MaximumFiles = 10_000;

    public static async Task<SourceProjectManifest> ExtractAsync(string corpusRoot, string projectRoot, IProgress<SourceProgress>? progress = null, CancellationToken token = default)
    {
        corpusRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(corpusRoot)); projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!Directory.Exists(corpusRoot)) throw new DirectoryNotFoundException("The game data folder does not exist.");
        SourceProject.ValidateSeparate(projectRoot, corpusRoot, "project folder");
        SourceProject.RejectLinks(projectRoot);
        if (Directory.Exists(projectRoot) && Directory.EnumerateFileSystemEntries(projectRoot).Any()) throw new IOException("Choose a new or empty folder for the source project.");
        var files = Corpus(corpusRoot);
        // The recovered build layout is RECOIL's; MechWarrior 3 data uses other formats and folders.
        if (files.FirstOrDefault(f => FormatRegistry.Probe(f.Path) is { Family: FormatFamily.GameZ, Version: 27 } or { Family: FormatFamily.Animation, Version: 39 }) is { Path: not null } mw3)
            throw new InvalidDataException($"{mw3.Relative} is MechWarrior 3 data; source reconstruction supports RECOIL.");
        Directory.CreateDirectory(projectRoot);
        Context context = new(projectRoot, token);
        List<SourceOutput> outputs = []; List<string> fingerprint = []; long total = 0;
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var (path, relative) = files[i]; progress?.Report(new(i, files.Count, relative));
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            string sha = SourceProject.Sha256(bytes); fingerprint.Add(relative + "\0" + sha); total += bytes.Length;
            var probe = FormatRegistry.Probe(bytes.AsSpan(0, Math.Min(36, bytes.Length)), bytes.AsSpan(Math.Max(0, bytes.Length - 8)), bytes.Length, Path.GetExtension(path));
            SourceOutput? output = null; context.BeginOutput();
            try
            {
                if (probe.Recognition == Recognition.Supported && probe.Family == FormatFamily.Archive) output = await context.ExtractArchiveAsync(relative, bytes, sha);
                else if (probe.Recognition == Recognition.Supported && probe.Family == FormatFamily.Scripts) output = await context.ExtractScriptsAsync(relative, bytes, sha);
                // Verify the reconstruction before accepting it; anything that does not pack back exactly stays verbatim.
                if (output != null && !SourcePacker.Build(projectRoot, output, token).Bytes.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException("the reconstructed sources do not pack back byte-identically");
            }
            catch (InvalidDataException ex) { context.Rollback(); context.Notes.Add($"{relative}: kept verbatim because {ex.Message}"); output = null; }
            output ??= await context.PassthroughAsync(relative, bytes, sha);
            outputs.Add(output);
        }
        progress?.Report(new(files.Count, files.Count, "Writing manifest"));
        SourceProjectManifest manifest = new()
        {
            Origin = new(Path.GetFileName(corpusRoot), files.Count, total, SourceProject.Sha256(Encoding.UTF8.GetBytes(string.Join('\n', fingerprint)))),
            Outputs = outputs, Notes = context.Notes
        };
        await context.WriteAsync(SourceProjectManifest.FileName, JsonSerializer.SerializeToUtf8Bytes(manifest, SourceProject.Json));
        return manifest;
    }

    /// <summary>Shipped files in a stable order, relative with forward slashes. Links are refused.</summary>
    internal static List<(string Path, string Relative)> Corpus(string root)
    {
        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 };
        List<(string, string)> files = [];
        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{info.FullName} is a link; reconstruct from a folder of regular files.");
            if (info is not FileInfo file) continue;
            if (file.Length > FormatRegistry.MaximumDocumentBytes) throw new IOException($"{file.FullName} exceeds 512 MiB.");
            files.Add((file.FullName, Path.GetRelativePath(root, file.FullName).Replace('\\', '/')));
            if (files.Count > MaximumFiles) throw new IOException($"The game data folder has more than {MaximumFiles:N0} files.");
        }
        // Sound banks are read best-first, so the high-quality bank supplies the source WAVs.
        return files.OrderBy(f => SoundRank(f.Item2)).ThenBy(f => f.Item2, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Item2, StringComparer.Ordinal).ToList();
    }
    private static int SoundRank(string relative) => Path.GetFileName(relative).ToLowerInvariant() switch { "soundsh.zbd" => 0, "soundsm.zbd" => 1, "soundsl.zbd" => 2, _ => 3 };

    private sealed class Context(string root, CancellationToken token)
    {
        private readonly Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> created = [];
        private int notesAtStart;
        internal List<string> Notes { get; } = [];
        internal void BeginOutput() { created.Clear(); notesAtStart = Notes.Count; }
        /// <summary>Remove sources, cache entries and notes that only a rejected output created.</summary>
        internal void Rollback()
        {
            foreach (string relative in created)
            {
                sources.Remove(relative);
                string path = SourceProject.Resolve(root, relative); if (File.Exists(path)) File.Delete(path);
            }
            created.Clear(); Notes.RemoveRange(notesAtStart, Notes.Count - notesAtStart);
        }

        internal async Task WriteAsync(string relative, byte[] bytes)
        {
            string path = SourceProject.Resolve(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, token);
        }
        private async Task<string> LayoutAsync<T>(string output, T layout)
        {
            string relative = $"{SourceProject.MetadataFolder}/layouts/{output}.json";
            await WriteAsync(relative, JsonSerializer.SerializeToUtf8Bytes(layout, SourceProject.Json));
            created.Add(relative); return relative;
        }
        internal async Task<SourceOutput> PassthroughAsync(string relative, byte[] bytes, string sha)
        {
            string stored = $"{SourceProject.MetadataFolder}/passthrough/{relative}";
            await WriteAsync(stored, bytes);
            return new(relative, "passthrough", sha, bytes.Length, stored);
        }
        /// <summary>Write a source once. The same path with other content gets a numbered sibling, reported in the notes.</summary>
        private async Task<string> SourceAsync(string relative, byte[] bytes)
        {
            string sha = SourceProject.Sha256(bytes), candidate = relative;
            for (int n = 2; sources.TryGetValue(candidate, out string? existing); n++)
            {
                if (existing == sha) return candidate;
                candidate = Path.ChangeExtension(relative, null) + "~" + n + Path.GetExtension(relative);
            }
            if (candidate != relative) Notes.Add($"{relative} has another version with different content; stored as {candidate}.");
            sources[candidate] = sha; created.Add(candidate); await WriteAsync(candidate, bytes); return candidate;
        }
        internal async Task<string> CacheAsync(byte[] bytes)
        {
            string sha = SourceProject.Sha256(bytes), path = SourceProject.CachePath(root, sha);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, bytes, token);
                created.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
            return sha;
        }

        internal async Task<SourceOutput> ExtractArchiveAsync(string output, byte[] bytes, string sha)
        {
            var members = ArchiveSources.Read(bytes);
            bool sounds = members.Count > 0 && members.All(m => m.Payload.Length >= 12 && m.Payload.Span[..4].SequenceEqual("RIFF"u8) && m.Payload.Span.Slice(8, 4).SequenceEqual("WAVE"u8));
            List<ArchiveLayoutRecord> records = [];
            foreach (var m in members)
            {
                token.ThrowIfCancellationRequested();
                string? sourcePath = ArchiveSources.PlainField(m.SourceField.Span), name = ArchiveSources.PlainField(m.NameField.Span);
                if (m.Name.Length == 0 || m.Name.Contains('/') || m.Name.Contains('\\') || m.Name is "." or "..") throw new InvalidDataException($"member {m.Index} has an unusable name");
                string directory = sounds ? $"{SourceProject.DataFolder}/common/sounds" : SourceDirectory(sourcePath) ?? $"{SourceProject.DataFolder}/_unplaced/{Path.GetFileNameWithoutExtension(output)}";
                string target = $"{directory}/{m.Name}"; byte[] payload = m.Payload.ToArray();
                string encoding = "verbatim"; byte[] source = payload; string? cache = null;
                if (!sounds && ZrdDecoder.TryRead(payload, token) is { } tree && tree.Kind == ZrdKind.Array)
                {
                    byte[] text = ZrdText.Encode(tree, token);
                    if (ZrdWriter.Write(ZrdText.Parse(text, token), token).AsSpan().SequenceEqual(payload)) { encoding = "zrd-text"; source = text; }
                    else Notes.Add($"{output}: {m.Name} kept as compiled data because its text form does not round-trip.");
                }
                // A lower-quality sound bank entry is a derived variant of the best source already written.
                if (sounds && sources.TryGetValue(target, out string? best) && best != SourceProject.Sha256(payload))
                {
                    encoding = "derived"; cache = await CacheAsync(payload);
                    records.Add(new(m.Name, encoding, target, best, m.Aux, m.FileTime) { NameField = name == m.Name ? null : Convert.ToBase64String(m.NameField.Span), SourcePath = sourcePath, SourceField = sourcePath == null ? Convert.ToBase64String(m.SourceField.Span) : null, Cache = cache });
                    continue;
                }
                string stored = await SourceAsync(target, source);
                records.Add(new(m.Name, encoding, stored, SourceProject.Sha256(source), m.Aux, m.FileTime)
                { NameField = name == m.Name ? null : Convert.ToBase64String(m.NameField.Span), SourcePath = sourcePath, SourceField = sourcePath == null ? Convert.ToBase64String(m.SourceField.Span) : null });
            }
            return new(output, "archive", sha, bytes.Length, await LayoutAsync(output, new ArchiveLayout(records)));
        }
        /// <summary>The recorded compile directory, e.g. <c>D:\battlesportdev\data\m1\zrdr\envmodels\fueE3B0.TMP</c> → <c>data/m1/zrdr/envmodels</c>.</summary>
        internal static string? SourceDirectory(string? recorded)
        {
            if (recorded == null) return null;
            int data = recorded.IndexOf("\\data\\", StringComparison.OrdinalIgnoreCase);
            if (data < 0) return null;
            var parts = recorded[(data + 6)..].Split('\\');
            if (parts.Length < 2 || parts[..^1].Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
            return SourceProject.DataFolder + "/" + string.Join('/', parts[..^1]).ToLowerInvariant();
        }

        internal async Task<SourceOutput> ExtractScriptsAsync(string output, byte[] bytes, string sha)
        {
            var doc = FormatRegistry.Default.OpenBytes(output, bytes, token: token);
            var package = doc.Scripts ?? throw new InvalidDataException("the prepared scripts are not a complete package");
            List<ScriptLayoutEntry> entries = [];
            foreach (var entry in package.Entries)
            {
                token.ThrowIfCancellationRequested();
                var parts = entry.Name.Split('\\');
                if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new InvalidDataException($"script name '{entry.Name}' is not a relative path");
                string text = GameGenScriptText.Write(entry.Instructions.Select(i => i.Tokens));
                var lines = GameGenScriptText.Tokenize(text);
                if (lines.Count != entry.Instructions.Count || lines.Where((tokens, n) => !tokens.SequenceEqual(entry.Instructions[n].Tokens)).Any())
                    throw new InvalidDataException($"script {entry.Name} does not round-trip through text");
                byte[] source = Encoding.Latin1.GetBytes(text);
                string stored = await SourceAsync($"{SourceProject.GameGenFolder}/{string.Join('/', parts)}", source);
                // The prepared index records each script's modification time; the reconstructed file carries it too.
                File.SetLastWriteTimeUtc(SourceProject.Resolve(root, stored), DateTime.UnixEpoch.AddSeconds(entry.FileTime));
                var padding = entry.Instructions.Select((i, n) => (i, n)).Where(x => !x.i.Padding.IsEmpty).Select(x => new ScriptLayoutPadding(x.n, Convert.ToBase64String(x.i.Padding.Span))).ToArray();
                entries.Add(new(entry.Name, stored, SourceProject.Sha256(source), entry.FileTime, Convert.ToBase64String(entry.DirectoryRecord.Span), Convert.ToBase64String(entry.FollowingBytes.Span)) { Padding = padding });
            }
            var layout = new ScriptLayout(Convert.ToBase64String(package.Header.Span), Convert.ToBase64String(package.PrefixGap.Span), Convert.ToBase64String(package.Tail.Span), entries);
            return new(output, "scripts", sha, bytes.Length, await LayoutAsync(output, layout));
        }
    }
}
