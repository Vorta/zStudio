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

    /// <summary>Build every output in memory and compare it with the shipped file, without writing anything.</summary>
    public static Task<SourcePackReport> VerifyAsync(string projectRoot, IProgress<SourceProgress>? progress = null, CancellationToken token = default)
        => RunAsync(projectRoot, null, progress, token);

    /// <summary>Build every output into <paramref name="destination"/>: a new, empty or previously packed folder outside the project.</summary>
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
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any() && !File.Exists(Path.Combine(destination, MarkerFileName)))
                throw new IOException("Pack into a new or empty folder, or into a folder that zStudio packed before.");
            Directory.CreateDirectory(destination);
            staging = Path.Combine(destination, ".zstudio-staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
        }
        try
        {
            List<SourcePackResult> results = [];
            for (int i = 0; i < manifest.Outputs.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var output = manifest.Outputs[i]; progress?.Report(new(i, manifest.Outputs.Count, output.Path));
                try
                {
                    var built = await Task.Run(() => Build(projectRoot, output, token), token);
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
            if (staging != null && destination != null)
            {
                if (results.Any(r => r.Status == "failed")) throw new InvalidDataException("Nothing was written because some outputs failed: " + string.Join("; ", results.Where(r => r.Status == "failed").Select(r => $"{r.Path}: {r.Error}")));
                await PublishAsync(staging, destination, manifest, results, token);
            }
            return new(destination, results);
        }
        finally { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    /// <summary>Move staged outputs into place, removing outputs of a previous pack that this project no longer produces.</summary>
    private static async Task PublishAsync(string staging, string destination, SourceProjectManifest manifest, IReadOnlyList<SourcePackResult> results, CancellationToken token)
    {
        string marker = Path.Combine(destination, MarkerFileName);
        if (File.Exists(marker))
        {
            var previous = JsonSerializer.Deserialize<PackMarker>(await File.ReadAllBytesAsync(marker, token), SourceProject.Json);
            foreach (var old in previous?.Outputs ?? [])
                if (!results.Any(r => r.Path.Equals(old.Path, StringComparison.OrdinalIgnoreCase))) { string stale = SourceProject.Resolve(destination, old.Path); if (File.Exists(stale)) File.Delete(stale); }
        }
        foreach (var result in results)
        {
            string target = SourceProject.Resolve(destination, result.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(SourceProject.Resolve(staging, result.Path), target, true);
        }
        var packed = new PackMarker(manifest.Origin.Fingerprint, results.Select(r => new PackMarkerOutput(r.Path, r.Sha256!, r.Status)).ToArray());
        await File.WriteAllBytesAsync(marker, JsonSerializer.SerializeToUtf8Bytes(packed, SourceProject.Json), token);
    }
    private sealed record PackMarker(string Origin, IReadOnlyList<PackMarkerOutput> Outputs);
    private sealed record PackMarkerOutput(string Path, string Sha256, string Status);

    internal static Built Build(string root, SourceOutput output, CancellationToken token) => output.Family switch
    {
        "passthrough" => new(Read(root, output.Layout, token), []),
        "archive" => BuildArchive(root, SourceProject.ReadLayout<ArchiveLayout>(root, output.Layout), token),
        "scripts" => BuildScripts(root, SourceProject.ReadLayout<ScriptLayout>(root, output.Layout), token),
        _ => throw new InvalidDataException($"Unknown output family '{output.Family}'.")
    };

    private static byte[] Read(string root, string relative, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = SourceProject.Resolve(root, relative);
        if (new FileInfo(path).Length > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException($"{relative} exceeds 512 MiB.");
        return File.ReadAllBytes(path);
    }

    private static Built BuildArchive(string root, ArchiveLayout layout, CancellationToken token)
    {
        List<(ArchiveLayoutRecord, byte[])> members = []; SortedSet<string> changed = new(StringComparer.Ordinal);
        Dictionary<string, (byte[] Bytes, string Sha)> read = new(StringComparer.OrdinalIgnoreCase);
        foreach (var record in layout.Records)
        {
            token.ThrowIfCancellationRequested();
            if (!read.TryGetValue(record.Source, out var source)) { byte[] bytes = Read(root, record.Source, token); read[record.Source] = source = (bytes, SourceProject.Sha256(bytes)); }
            bool edited = source.Sha != record.SourceSha256;
            if (edited) changed.Add(record.Source);
            byte[] payload = record.Encoding switch
            {
                "zrd-text" => ZrdWriter.Write(Parse(record.Source, source.Bytes, token), token),
                "verbatim" => source.Bytes,
                "derived" => edited ? throw new InvalidDataException($"{record.Name} is a stored variant of {record.Source}; regenerating it after the source changes is not supported yet.") : Cached(root, record.Cache!, token),
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
    private static byte[] Cached(string root, string sha, CancellationToken token)
    {
        byte[] bytes = Read(root, Path.GetRelativePath(root, SourceProject.CachePath(root, sha)).Replace('\\', '/'), token);
        if (SourceProject.Sha256(bytes) != sha) throw new InvalidDataException($"Cached artifact {sha} is damaged.");
        return bytes;
    }

    private static Built BuildScripts(string root, ScriptLayout layout, CancellationToken token)
    {
        List<PreparedScriptEntry> entries = []; List<string> changed = [];
        foreach (var entry in layout.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = SourceProject.Resolve(root, entry.Source); byte[] bytes = Read(root, entry.Source, token);
            bool edited = SourceProject.Sha256(bytes) != entry.SourceSha256;
            if (edited) changed.Add(entry.Source);
            var lines = GameGenScriptText.Tokenize(Encoding.Latin1.GetString(bytes));
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
