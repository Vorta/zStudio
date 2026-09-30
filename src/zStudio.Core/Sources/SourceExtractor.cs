using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>What a reconstruction wrote. Game files whose family is not reconstructed yet are listed, not copied.</summary>
public sealed record SourceReconstructionReport(string Project, int SourceFiles, IReadOnlyDictionary<string, int> Families, IReadOnlyList<string> NotReconstructed, IReadOnlyList<string> Notes);

/// <summary>Reconstruct the original source tree (data/, gamegen/) from a shipped or exported RECOIL data folder.</summary>
public static class SourceExtractor
{
    public const int MaximumFiles = 10_000;

    public static async Task<SourceReconstructionReport> ExtractAsync(string corpusRoot, string projectRoot, IProgress<SourceProgress>? progress = null, CancellationToken token = default)
    {
        corpusRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(corpusRoot)); projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!Directory.Exists(corpusRoot)) throw new DirectoryNotFoundException("The game data folder does not exist.");
        SourceProject.ValidateSeparate(projectRoot, corpusRoot, "project folder");
        SourceProject.RejectLinks(projectRoot);
        if (Directory.Exists(projectRoot) && Directory.EnumerateFileSystemEntries(projectRoot).Any()) throw new IOException("Choose a new or empty folder for the source project.");
        var files = Corpus(corpusRoot);
        // The recovered build layout is RECOIL's; MechWarrior 3 data uses other formats and folders.
        var probes = files.Select(f => (f.Relative, Probe: FormatRegistry.Probe(f.Path))).ToArray();
        if (probes.FirstOrDefault(f => f.Probe is { Family: FormatFamily.GameZ, Version: 27 } or { Family: FormatFamily.Animation, Version: 39 }) is { Relative: not null } mw3)
            throw new InvalidDataException($"{mw3.Relative} is MechWarrior 3 data; source reconstruction supports RECOIL.");
        // Require positive RECOIL evidence: prepared scripts, a version-15 world or a version-28 animation program.
        if (!probes.Any(f => f.Probe is { Family: FormatFamily.Scripts, Version: 7 } or { Family: FormatFamily.GameZ, Version: 15 } or { Family: FormatFamily.Animation, Version: 28 }))
            throw new InvalidDataException("No RECOIL game data was found. Choose the folder that contains interp.zbd, zrdr.zbd and the mission folders.");
        bool created = !Directory.Exists(projectRoot);
        Directory.CreateDirectory(projectRoot);
        try { return await ExtractFilesAsync(projectRoot, files, progress, token); }
        catch
        {
            // The folder was new or empty: remove everything this reconstruction wrote so it can be retried.
            if (created) Directory.Delete(projectRoot, true);
            else foreach (var entry in new DirectoryInfo(projectRoot).EnumerateFileSystemInfos())
                { if (entry is DirectoryInfo directory) directory.Delete(true); else entry.Delete(); }
            throw;
        }
    }

    private static async Task<SourceReconstructionReport> ExtractFilesAsync(string projectRoot, List<(string Path, string Relative)> files, IProgress<SourceProgress>? progress, CancellationToken token)
    {
        Context context = new(projectRoot, token);
        Directory.CreateDirectory(Path.Combine(projectRoot, SourceProject.DataFolder)); Directory.CreateDirectory(Path.Combine(projectRoot, SourceProject.GameGenFolder));
        Dictionary<string, int> families = []; List<string> skipped = [];
        List<(string Relative, IReadOnlyList<ArchiveSources.Member> Members)> soundBanks = [];
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var (path, relative) = files[i]; progress?.Report(new(i, files.Count, relative));
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            var probe = FormatRegistry.Probe(bytes.AsSpan(0, Math.Min(36, bytes.Length)), bytes.AsSpan(Math.Max(0, bytes.Length - 8)), bytes.Length, Path.GetExtension(path));
            string? family = null;
            try
            {
                if (probe is { Recognition: Recognition.Supported, Family: FormatFamily.Archive })
                {
                    var members = ArchiveSources.Read(bytes);
                    bool waves = members.Count > 0 && members.All(m => IsWave(m.Payload.Span));
                    if (waves && SourceBuilder.Banks.Contains(relative, StringComparer.OrdinalIgnoreCase)) { soundBanks.Add((relative, members)); family = "sounds"; }
                    else if (waves) context.Notes.Add($"{relative}: an archive of sounds that is not one of the soundsh/m/l banks; it was not reconstructed.");
                    else { await context.ExtractResourcesAsync(relative, members); family = "resources"; }
                }
                else if (probe is { Recognition: Recognition.Supported, Family: FormatFamily.Scripts, Version: 7 }) { await context.ExtractScriptsAsync(relative, bytes); family = "scripts"; }
            }
            catch (InvalidDataException ex) { context.Notes.Add($"{relative}: not reconstructed because {ex.Message}"); family = null; }
            if (family != null) families[family] = families.GetValueOrDefault(family) + 1; else skipped.Add(relative);
        }
        if (soundBanks.Count > 0) await context.ExtractSoundsAsync(soundBanks);
        progress?.Report(new(files.Count, files.Count, "Done"));
        return new(projectRoot, context.Written, families, skipped, context.Notes);
    }
    private static bool IsWave(ReadOnlySpan<byte> bytes) => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8);

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
        return files.OrderBy(f => f.Item2, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Item2, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The folder a resource was built from. Retail archives record the compiler's temporary file
    /// (<c>D:\battlesportdev\data\m1\zrdr\envmodels\fueE3B0.TMP</c>); zStudio exports record the source itself
    /// (<c>data\m1\zrdr\envmodels\fuel.zrd</c>). Both yield <c>data/m1/zrdr/envmodels</c>.
    /// </summary>
    internal static string? SourceDirectory(string? recorded)
    {
        if (recorded == null) return null;
        int data = recorded.StartsWith("data\\", StringComparison.OrdinalIgnoreCase) ? 0 : recorded.IndexOf("\\data\\", StringComparison.OrdinalIgnoreCase) is >= 0 and int at ? at + 1 : -1;
        if (data < 0) return null;
        var parts = recorded[(data + 5)..].Split('\\');
        if (parts.Length < 2 || parts[..^1].Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        return SourceProject.DataFolder + "/" + string.Join('/', parts[..^1]).ToLowerInvariant();
    }

    private sealed class Context(string root, CancellationToken token)
    {
        private readonly Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Notes { get; } = [];
        internal int Written => sources.Count;

        /// <summary>Write a source once. Another version at the same path is reported and the first is kept.</summary>
        private async Task WriteAsync(string relative, byte[] bytes, DateTime? modified = null)
        {
            string sha = SourceProject.Sha256(bytes);
            if (sources.TryGetValue(relative, out string? existing)) { if (existing != sha) Notes.Add($"{relative} has another version with different content; the first one was kept."); return; }
            string path = SourceProject.Resolve(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, token);
            if (modified is { } time) File.SetLastWriteTimeUtc(path, time);
            sources[relative] = sha;
        }

        internal async Task ExtractResourcesAsync(string output, IReadOnlyList<ArchiveSources.Member> members)
        {
            // Without a recorded folder, a resource belongs to its archive's own zrdr folder.
            string fallback = Path.GetDirectoryName(output)?.Replace('\\', '/') is { Length: > 0 } mission ? $"{SourceProject.DataFolder}/{mission.ToLowerInvariant()}/zrdr" : $"{SourceProject.DataFolder}/common/zrdr";
            foreach (var m in members)
            {
                token.ThrowIfCancellationRequested();
                if (m.Name.Length == 0 || m.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || m.Name is "." or "..") { Notes.Add($"{output}: member {m.Index} has an unusable name and was skipped."); continue; }
                string directory = SourceDirectory(m.SourceField) ?? fallback;
                byte[] payload = m.Payload.ToArray(), source = payload;
                if (ZrdDecoder.TryRead(payload, token) is { Kind: ZrdKind.Array } tree)
                {
                    byte[] text = ZrdText.Encode(tree, token);
                    if (text.Length <= SourceProject.MaximumSourceTextBytes && ZrdWriter.Write(ZrdText.Parse(text, token), token).AsSpan().SequenceEqual(payload)) source = text;
                    else Notes.Add($"{output}: {m.Name} kept as compiled data because its text form does not round-trip.");
                }
                // Exported archives contain the .zrd resources of their zrdr folders, as the original build read them.
                else Notes.Add(m.Name.EndsWith(ZrdText.Extension, StringComparison.OrdinalIgnoreCase)
                    ? $"{output}: {m.Name} is not valid zReader data; it was written as stored and exports fail until it is replaced."
                    : $"{output}: {m.Name} is not zReader data; it was written as stored and exported archives leave it out.");
                await WriteAsync($"{directory}/{m.Name}", source);
            }
        }

        internal async Task ExtractScriptsAsync(string output, byte[] bytes)
        {
            var doc = FormatRegistry.Default.OpenBytes(output, bytes, token: token);
            var package = doc.Scripts ?? throw new InvalidDataException("the prepared scripts are not a complete package");
            foreach (var entry in package.Entries)
            {
                token.ThrowIfCancellationRequested();
                var parts = entry.Name.Split('\\');
                if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) { Notes.Add($"{output}: script '{entry.Name}' is not a relative path and was skipped."); continue; }
                string text;
                try { text = GameGenScriptText.Write(entry.Instructions.Select(i => i.Tokens)); }
                catch (InvalidDataException) { Notes.Add($"{output}: script {entry.Name} has instructions that cannot be written as text and was skipped."); continue; }
                // The prepared index records each script's modification time; the source file keeps it.
                await WriteAsync($"{SourceProject.GameGenFolder}/{string.Join('/', parts)}", Encoding.Latin1.GetBytes(text), DateTime.UnixEpoch.AddSeconds(entry.FileTime));
            }
        }

        /// <summary>Each sound's best-quality version across all banks becomes the source; lower banks are regenerated on export.</summary>
        internal async Task ExtractSoundsAsync(IReadOnlyList<(string Relative, IReadOnlyList<ArchiveSources.Member> Members)> banks)
        {
            Dictionary<string, (ReadOnlyMemory<byte> Bytes, int Quality)> best = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (relative, members) in banks)
                foreach (var m in members)
                {
                    token.ThrowIfCancellationRequested();
                    if (m.Name.Length == 0 || m.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Notes.Add($"{relative}: sound {m.Index} has an unusable name and was skipped."); continue; }
                    int quality;
                    try { quality = WaveConverter.Format(m.Payload).Quality; }
                    catch (InvalidDataException ex) { Notes.Add($"{relative}: {m.Name} is not a readable WAV ({ex.Message}) and was skipped."); continue; }
                    if (!best.TryGetValue(m.Name, out var current) || quality > current.Quality) best[m.Name] = (m.Payload, quality);
                }
            foreach (var (name, sound) in best.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) await WriteAsync($"{SourceBuilder.SoundsFolder}/{name}", sound.Bytes.ToArray());
        }
    }
}
