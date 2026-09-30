using System.Text;
using System.Text.Json;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>Per-output result. Status: identical (matches the shipped file), changed (sources were edited) or failed.</summary>
public sealed record SourcePackResult(string Path, string Family, string Status, string? Sha256, string OriginalSha256, IReadOnlyList<string> ChangedSources, string? Error = null);
public sealed record SourcePackReport(string? Destination, IReadOnlyList<SourcePackResult> Outputs)
{
    public int Identical => Outputs.Count(o => o.Status == "identical");
    public int Changed => Outputs.Count(o => o.Status == "changed");
    public int Failed => Outputs.Count(o => o.Status == "failed");
}

/// <summary>Build shipped ZBD files from a source project.</summary>
public static class SourcePacker
{
    public const string MarkerFileName = "zstudio-pack.json";
    internal sealed record Built(byte[] Bytes, IReadOnlyList<string> ChangedSources);

    /// <summary>
    /// Every project file read by one run. A file read by several outputs must have the same content each time, and
    /// no file may change before the run publishes, so a pack always corresponds to one state of the project.
    /// </summary>
    internal sealed class Snapshot(string root)
    {
        private readonly Dictionary<string, (string Sha, FileStamp Stamp)> files = new(StringComparer.OrdinalIgnoreCase);
        internal byte[] Read(string relative, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = SourceProject.Resolve(root, relative);
            var stamp = FileStamp.Read(path);
            if (stamp.Length > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException($"{relative} exceeds 512 MiB.");
            byte[] bytes = File.ReadAllBytes(path); string sha = SourceProject.Sha256(bytes);
            if (FileStamp.Read(path) != stamp) throw new InvalidDataException($"{relative} changed while it was read; pack again.");
            if (files.TryGetValue(relative, out var first) && (first.Sha != sha || first.Stamp != stamp)) throw new InvalidDataException($"{relative} changed while packing; pack again.");
            files[relative] = (sha, stamp); return bytes;
        }
        internal void CheckUnchanged(CancellationToken token)
        {
            foreach (var (relative, entry) in files)
            {
                token.ThrowIfCancellationRequested();
                if (FileStamp.Read(SourceProject.Resolve(root, relative)) != entry.Stamp) throw new InvalidDataException($"{relative} changed while packing; nothing was written.");
            }
        }
    }

    /// <summary>Build every output in memory and compare it with the shipped file, without writing anything.</summary>
    public static Task<SourcePackReport> VerifyAsync(string projectRoot, IProgress<SourceProgress>? progress = null, CancellationToken token = default)
        => RunAsync(projectRoot, null, progress, token);

    /// <summary>Build every output into <paramref name="destination"/>: a new, empty or previously packed folder of this project.</summary>
    public static Task<SourcePackReport> PackAsync(string projectRoot, string destination, IProgress<SourceProgress>? progress = null, CancellationToken token = default)
        => RunAsync(projectRoot, destination, progress, token);

    private static async Task<SourcePackReport> RunAsync(string projectRoot, string? destination, IProgress<SourceProgress>? progress, CancellationToken token)
    {
        projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var manifest = await SourceProject.LoadAsync(projectRoot, token);
        string? staging = null;
        if (destination != null)
        {
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
            SourceProject.ValidateSeparate(destination, projectRoot, "pack destination"); SourceProject.RejectLinks(destination);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            {
                // Reuse only a folder this project packed; another project's outputs are never replaced or removed.
                var marker = await ReadMarkerAsync(destination, token) ?? throw new IOException("Pack into a new or empty folder, or into a folder that this project packed before.");
                if (marker.Project != manifest.Id) throw new IOException("This folder holds files packed from a different source project; choose a new or empty folder.");
            }
            Directory.CreateDirectory(destination);
            staging = Path.Combine(destination, ".zstudio-staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
        }
        try
        {
            Snapshot snapshot = new(projectRoot);
            List<SourcePackResult> results = [];
            for (int i = 0; i < manifest.Outputs.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var output = manifest.Outputs[i]; progress?.Report(new(i, manifest.Outputs.Count, output.Path));
                try
                {
                    var built = await Task.Run(() => Build(projectRoot, output, snapshot, token), token);
                    // Every output must reopen through the shared readers before it can be written.
                    var check = FormatRegistry.Default.OpenBytes(output.Path, built.Bytes, token: token);
                    if (check.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException("The built file does not reopen: " + error.Message);
                    string sha = SourceProject.Sha256(built.Bytes);
                    if (staging != null) { string path = SourceProject.Resolve(staging, output.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, built.Bytes, token); }
                    results.Add(new(output.Path, output.Family, sha == output.Sha256 ? "identical" : "changed", sha, output.Sha256, built.ChangedSources));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or FormatException or JsonException)
                { results.Add(new(output.Path, output.Family, "failed", null, output.Sha256, [], ex.Message)); }
            }
            progress?.Report(new(manifest.Outputs.Count, manifest.Outputs.Count, destination == null ? "Verified" : "Publishing"));
            snapshot.CheckUnchanged(token);
            if (staging != null && destination != null)
            {
                if (results.Any(r => r.Status == "failed")) throw new InvalidDataException("Nothing was written because some outputs failed: " + string.Join("; ", results.Where(r => r.Status == "failed").Select(r => $"{r.Path}: {r.Error}")));
                await PublishAsync(staging, destination, manifest, results, token);
            }
            return new(destination, results);
        }
        finally { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static async Task<PackMarker?> ReadMarkerAsync(string destination, CancellationToken token)
    {
        string path = Path.Combine(destination, MarkerFileName);
        if (!File.Exists(path)) return null;
        SourceProject.RejectNestedLinks(destination, MarkerFileName);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("The pack marker is too large.");
        try { return JsonSerializer.Deserialize<PackMarker>(await File.ReadAllBytesAsync(path, token), SourceProject.Json); }
        catch (JsonException ex) { throw new IOException("The pack marker is damaged.", ex); }
    }

    /// <summary>
    /// Replace outputs so that any failure restores the previous pack: replaced and stale files move into a backup folder
    /// first and are moved back if a later step fails. Every path is checked for links before anything moves.
    /// </summary>
    private static async Task PublishAsync(string staging, string destination, SourceProjectManifest manifest, IReadOnlyList<SourcePackResult> results, CancellationToken token)
    {
        var previous = await ReadMarkerAsync(destination, token);
        var stale = (previous?.Outputs ?? []).Select(o => o.Path).Where(p => !results.Any(r => r.Path.Equals(p, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (string relative in results.Select(r => r.Path).Concat(stale).Append(MarkerFileName))
        { _ = SourceProject.Resolve(destination, relative); SourceProject.RejectNestedLinks(destination, relative); }
        string backup = Path.Combine(destination, ".zstudio-backup-" + Guid.NewGuid().ToString("N"));
        // After Save, the original file (if any) is in the backup, so anything at the target is new and may be removed on rollback.
        List<(string Target, string? Saved)> steps = [];
        void Save(string relative)
        {
            string target = SourceProject.Resolve(destination, relative), saved = SourceProject.Resolve(backup, relative);
            if (!File.Exists(target)) { steps.Add((target, null)); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Move(target, saved); steps.Add((target, saved));
        }
        try
        {
            foreach (string relative in stale) { token.ThrowIfCancellationRequested(); Save(relative); }
            foreach (var result in results)
            {
                token.ThrowIfCancellationRequested(); Save(result.Path);
                string target = SourceProject.Resolve(destination, result.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(SourceProject.Resolve(staging, result.Path), target);
            }
            Save(MarkerFileName);
            var packed = new PackMarker(manifest.Id, manifest.Origin.Fingerprint, results.Select(r => new PackMarkerOutput(r.Path, r.Sha256!, r.Status)).ToArray());
            await File.WriteAllBytesAsync(Path.Combine(destination, MarkerFileName), JsonSerializer.SerializeToUtf8Bytes(packed, SourceProject.Json), CancellationToken.None);
        }
        catch
        {
            List<string> unrestored = [];
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                var (target, saved) = steps[i];
                try { if (File.Exists(target)) File.Delete(target); if (saved != null) File.Move(saved, target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unrestored.Add(target); }
            }
            if (unrestored.Count > 0) throw new IOException($"Publishing failed and {unrestored.Count} previous files could not be restored; they remain in {backup}: {string.Join(", ", unrestored.Take(8))}");
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            throw;
        }
        // The replaced files are no longer needed; a leftover backup folder never fails a completed publish.
        try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private sealed record PackMarker(Guid Project, string Origin, IReadOnlyList<PackMarkerOutput> Outputs);
    private sealed record PackMarkerOutput(string Path, string Sha256, string Status);

    internal static Built Build(string root, SourceOutput output, Snapshot snapshot, CancellationToken token) => output.Family switch
    {
        "passthrough" => new(snapshot.Read(output.Layout, token), []),
        "archive" => BuildArchive(root, SourceProject.ReadLayout<ArchiveLayout>(root, output.Layout), snapshot, token),
        "scripts" => BuildScripts(root, SourceProject.ReadLayout<ScriptLayout>(root, output.Layout), snapshot, token),
        _ => throw new InvalidDataException($"Unknown output family '{output.Family}'.")
    };

    private static Built BuildArchive(string root, ArchiveLayout layout, Snapshot snapshot, CancellationToken token)
    {
        List<(ArchiveLayoutRecord, byte[])> members = []; SortedSet<string> changed = new(StringComparer.Ordinal);
        Dictionary<string, (byte[] Bytes, string Sha)> read = new(StringComparer.OrdinalIgnoreCase);
        foreach (var record in layout.Records)
        {
            token.ThrowIfCancellationRequested();
            if (!read.TryGetValue(record.Source, out var source)) { byte[] bytes = snapshot.Read(record.Source, token); read[record.Source] = source = (bytes, SourceProject.Sha256(bytes)); }
            bool edited = source.Sha != record.SourceSha256;
            if (edited) changed.Add(record.Source);
            byte[] payload = record.Encoding switch
            {
                "zrd-text" => ZrdWriter.Write(Parse(record.Source, source.Bytes, token), token),
                "verbatim" => source.Bytes,
                "derived" => edited ? throw new InvalidDataException($"{record.Name} is a stored variant of {record.Source}; regenerating it after the source changes is not supported yet.") : Cached(root, record.Cache!, snapshot, token),
                _ => throw new InvalidDataException($"Unknown member encoding '{record.Encoding}'.")
            };
            members.Add((record, payload));
        }
        return new(ArchiveSources.Write(members), changed.ToArray());
    }
    private static ZrdNode Parse(string source, byte[] bytes, CancellationToken token)
    {
        try { return ZrdText.Parse(bytes, token); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{source}: {ex.Message}", ex); }
    }
    private static byte[] Cached(string root, string sha, Snapshot snapshot, CancellationToken token)
    {
        byte[] bytes = snapshot.Read(Path.GetRelativePath(root, SourceProject.CachePath(root, sha)).Replace('\\', '/'), token);
        if (SourceProject.Sha256(bytes) != sha) throw new InvalidDataException($"Cached artifact {sha} is damaged.");
        return bytes;
    }

    private static Built BuildScripts(string root, ScriptLayout layout, Snapshot snapshot, CancellationToken token)
    {
        List<PreparedScriptEntry> entries = []; List<string> changed = [];
        foreach (var entry in layout.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = SourceProject.Resolve(root, entry.Source); byte[] bytes = snapshot.Read(entry.Source, token);
            bool edited = SourceProject.Sha256(bytes) != entry.SourceSha256;
            if (edited) changed.Add(entry.Source);
            var lines = GameGenScriptText.Tokenize(GameGenScriptText.Decode(bytes));
            // Original padding and time belong to the original text; an edited script is re-encoded and takes its file time.
            var padding = edited ? [] : entry.Padding.ToDictionary(p => p.Instruction, p => (ReadOnlyMemory<byte>)Convert.FromBase64String(p.Bytes));
            var instructions = lines.Select((tokens, n) =>
            {
                if (edited) PreparedScriptWriter.ValidateTokens(tokens);
                return new ScriptInstruction(Guid.NewGuid(), tokens, ReadOnlyMemory<byte>.Empty, null) { Padding = padding.GetValueOrDefault(n) };
            }).ToArray();
            uint time = edited ? checked((uint)Math.Clamp(new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds(), 0, uint.MaxValue)) : entry.FileTime;
            entries.Add(new(Guid.NewGuid(), null, entry.Name, time, Convert.FromBase64String(entry.DirectoryRecord), instructions, Convert.FromBase64String(entry.Following)));
        }
        var package = new PreparedScriptPackage(Convert.FromBase64String(layout.Header), Convert.FromBase64String(layout.PrefixGap), entries, Convert.FromBase64String(layout.Tail));
        return new(PreparedScriptWriter.Write(package, token), changed);
    }
}
