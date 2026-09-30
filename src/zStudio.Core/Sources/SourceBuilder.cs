using System.Text;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A game file that the source tree can build, and the source files it is built from.</summary>
public sealed record SourceOutputPlan(string Path, string Family, IReadOnlyList<string> Inputs);
/// <summary>Per-output result: built (and, for an export, written) or failed.</summary>
public sealed record SourceExportResult(string Path, string Family, string Status, long Bytes, int Items, IReadOnlyList<string> Warnings, string? Error = null);
public sealed record SourceExportReport(string? Destination, IReadOnlyList<SourceExportResult> Outputs)
{
    public int Built => Outputs.Count(o => o.Status == "built");
    public int Failed => Outputs.Count(o => o.Status == "failed");
}

/// <summary>
/// Builds game files from a source tree, as the original gamegen build did: resource archives from each <c>zrdr</c> folder,
/// prepared scripts from <c>gamegen</c>, and the three sound banks from the best-quality WAVs converted to the formats
/// that <c>sounds.zrd</c> declares. Output must work in the game; it does not reproduce the shipped bytes.
/// </summary>
public static partial class SourceBuilder
{
    public const string SoundsFolder = "data/common/sounds", SoundDefinitions = "data/common/zrdr/sounds.zrd";
    /// <summary>The HIGH, MED and LOW sound banks, in the order of their sounds.zrd declarations.</summary>
    internal static readonly string[] Banks = ["soundsh.zbd", "soundsm.zbd", "soundsl.zbd"];
    [GeneratedRegex(@"\Am\d{1,3}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex MissionFolder();

    /// <summary>Every game file this tree can build, in a stable order.</summary>
    public static IReadOnlyList<SourceOutputPlan> Plan(string root)
    {
        if (!SourceProject.IsProject(root)) throw new InvalidDataException("This folder is not a source project (it needs data and gamegen folders).");
        List<SourceOutputPlan> plans = [];
        static bool Zrd(string name) => name.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase);
        // Common resources come from every zrdr folder under data/common (including multi_bft/zrdr).
        var common = SourceProject.Files(root, "data/common", Zrd).Where(p => p.Split('/').Contains("zrdr", StringComparer.OrdinalIgnoreCase)).ToArray();
        if (common.Length > 0) plans.Add(new("zrdr.zbd", "archive", common));
        var scripts = SourceProject.Files(root, SourceProject.GameGenFolder, n => n.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".gw", StringComparison.OrdinalIgnoreCase));
        if (scripts.Count > 0) plans.Add(new("interp.zbd", "scripts", scripts));
        var sounds = SourceProject.Files(root, SoundsFolder, n => n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
        if (sounds.Count > 0) plans.AddRange(Banks.Select(bank => new SourceOutputPlan(bank, "sounds", sounds)));
        foreach (var mission in new DirectoryInfo(SourceProject.Resolve(root, SourceProject.DataFolder)).EnumerateDirectories().Where(d => MissionFolder().IsMatch(d.Name)).OrderBy(d => int.Parse(d.Name.AsSpan(1))))
        {
            var resources = SourceProject.Files(root, $"data/{mission.Name}/zrdr", Zrd);
            if (resources.Count > 0) plans.Add(new($"{mission.Name.ToLowerInvariant()}/zrdr.zbd", "archive", resources));
        }
        return plans;
    }

    /// <summary>
    /// Every project file read by one run. A file read by several outputs must have the same content each time, and no
    /// file may change before the run publishes, so an export always corresponds to one state of the project.
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
            if (FileStamp.Read(path) != stamp) throw new InvalidDataException($"{relative} changed while it was read; export again.");
            if (files.TryGetValue(relative, out var first) && (first.Sha != sha || first.Stamp != stamp)) throw new InvalidDataException($"{relative} changed while exporting; export again.");
            files[relative] = (sha, stamp); return bytes;
        }
        internal void CheckUnchanged(CancellationToken token)
        {
            foreach (var (relative, entry) in files)
            {
                token.ThrowIfCancellationRequested();
                string path = SourceProject.Resolve(root, relative);
                if (!File.Exists(path) || FileStamp.Read(path) != entry.Stamp) throw new InvalidDataException($"{relative} changed while exporting; nothing was written.");
            }
        }
    }
    internal sealed record Built(byte[] Bytes, int Items, IReadOnlyList<string> Warnings);

    /// <summary>Build the selected outputs (all when null) in memory and report problems without writing anything.</summary>
    public static Task<SourceExportReport> CheckAsync(string root, IReadOnlyCollection<string>? outputs = null, IProgress<SourceProgress>? progress = null, CancellationToken token = default)
        => RunAsync(root, null, outputs, false, progress, token);

    /// <summary>
    /// Build the selected outputs (all when null) into <paramref name="destination"/>. Existing game files there are replaced
    /// only with <paramref name="overwrite"/>; publication restores them if any step fails, and nothing is written if any output fails.
    /// </summary>
    public static Task<SourceExportReport> ExportAsync(string root, string destination, IReadOnlyCollection<string>? outputs = null, bool overwrite = false, IProgress<SourceProgress>? progress = null, CancellationToken token = default)
        => RunAsync(root, destination, outputs, overwrite, progress, token);

    private static async Task<SourceExportReport> RunAsync(string root, string? destination, IReadOnlyCollection<string>? outputs, bool overwrite, IProgress<SourceProgress>? progress, CancellationToken token)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var all = await Task.Run(() => Plan(root), token);
        var selected = outputs == null ? all : outputs.Select(o => all.FirstOrDefault(p => p.Path.Equals(o.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"This source project cannot build {o}.")).Distinct().ToArray();
        if (selected.Count == 0) throw new InvalidDataException("This source project has nothing to build yet.");
        string? staging = null;
        if (destination != null)
        {
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
            SourceProject.ValidateSeparate(destination, root, "export destination"); SourceProject.RejectLinks(destination);
            var existing = selected.Where(p => File.Exists(Path.Combine(destination, p.Path))).Select(p => p.Path).ToArray();
            if (existing.Length > 0 && !overwrite) throw new IOException($"The destination already has {existing.Length} of these game files ({string.Join(", ", existing.Take(8))}). Choose another folder or allow replacing them.");
            Directory.CreateDirectory(destination);
            staging = Path.Combine(destination, ".zstudio-staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
        }
        try
        {
            Snapshot snapshot = new(root); DateTime now = DateTime.UtcNow; List<SourceExportResult> results = [];
            for (int i = 0; i < selected.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var plan = selected[i]; progress?.Report(new(i, selected.Count, plan.Path));
                try
                {
                    var built = await Task.Run(() => Build(root, plan, snapshot, now, token), token);
                    // Every output must reopen through the shared readers before it can be written.
                    var check = FormatRegistry.Default.OpenBytes(plan.Path, built.Bytes, token: token);
                    if (check.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException("The built file does not reopen: " + error.Message);
                    if (staging != null) { string path = SourceProject.Resolve(staging, plan.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, built.Bytes, token); }
                    results.Add(new(plan.Path, plan.Family, "built", built.Bytes.Length, built.Items, built.Warnings));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
                { results.Add(new(plan.Path, plan.Family, "failed", 0, 0, [], ex.Message)); }
            }
            progress?.Report(new(selected.Count, selected.Count, destination == null ? "Checked" : "Publishing"));
            snapshot.CheckUnchanged(token);
            if (staging != null && destination != null)
            {
                if (results.Any(r => r.Status == "failed")) throw new InvalidDataException("Nothing was written because some outputs failed: " + string.Join("; ", results.Where(r => r.Status == "failed").Select(r => $"{r.Path}: {r.Error}")));
                Publish(staging, destination, results.Select(r => r.Path).ToArray(), token);
            }
            return new(destination, results);
        }
        finally { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    /// <summary>Move staged outputs into place; replaced files move aside first and are restored if any later step fails.</summary>
    private static void Publish(string staging, string destination, IReadOnlyList<string> outputs, CancellationToken token)
    {
        foreach (string relative in outputs) { _ = SourceProject.Resolve(destination, relative); SourceProject.RejectNestedLinks(destination, relative); }
        string backup = Path.Combine(destination, ".zstudio-backup-" + Guid.NewGuid().ToString("N"));
        List<(string Target, string? Saved)> steps = [];
        try
        {
            foreach (string relative in outputs)
            {
                token.ThrowIfCancellationRequested();
                string target = SourceProject.Resolve(destination, relative), saved = SourceProject.Resolve(backup, relative);
                if (File.Exists(target)) { Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Move(target, saved); steps.Add((target, saved)); }
                else steps.Add((target, null));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(SourceProject.Resolve(staging, relative), target);
            }
        }
        catch
        {
            // After a file's original moved aside, anything at the target is new.
            List<string> unrestored = [];
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                var (target, saved) = steps[i];
                try { if (File.Exists(target)) File.Delete(target); if (saved != null) File.Move(saved, target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unrestored.Add(target); }
            }
            if (unrestored.Count > 0) throw new IOException($"Export failed and {unrestored.Count} previous files could not be restored; they remain in {backup}: {string.Join(", ", unrestored.Take(8))}");
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            throw;
        }
        try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static Built Build(string root, SourceOutputPlan plan, Snapshot snapshot, DateTime now, CancellationToken token) => plan.Family switch
    {
        "archive" => BuildArchive(plan, snapshot, now, token),
        "scripts" => BuildScripts(root, plan, snapshot, token),
        "sounds" => BuildSounds(root, plan, snapshot, now, token),
        _ => throw new InvalidDataException($"Unknown output family '{plan.Family}'.")
    };

    /// <summary>The source-path field carries the project-relative source, so reconstructing an exported archive restores its folders.</summary>
    internal static string SourceField(string relative) => relative.Replace('/', '\\');

    private static Built BuildArchive(SourceOutputPlan plan, Snapshot snapshot, DateTime now, CancellationToken token)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase); List<ArchiveSources.Entry> entries = [];
        // Members are ordered by source path. The engine scans members for the first case-insensitive name match
        // (zIndexArchive::FindRecordByNameCI, retail 0x4A65D0), so order is free but names must be unique.
        foreach (string input in plan.Inputs.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested(); string name = Path.GetFileName(input);
            if (names.TryGetValue(name, out string? other)) throw new InvalidDataException($"{other} and {input} would both become archive member {name}; the engine finds members by name.");
            names[name] = input;
            byte[] bytes = snapshot.Read(input, token);
            byte[] payload;
            try { payload = ZrdText.LooksLikeText(bytes) ? ZrdWriter.Write(ZrdText.Parse(bytes, token), token) : ZrdWriter.Write(ZrdDecoder.Read(bytes, token), token); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"{input}: {ex.Message}", ex); }
            entries.Add(new(name, SourceField(input), payload));
        }
        return new(ArchiveSources.Write(entries, now), entries.Count, []);
    }

    private static Built BuildScripts(string root, SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        List<PreparedScriptEntry> entries = [];
        foreach (string input in plan.Inputs)
        {
            token.ThrowIfCancellationRequested();
            // Scripts are indexed by their path below the gamegen folder, e.g. support\common.gw.
            string name = input[(SourceProject.GameGenFolder.Length + 1)..].Replace('/', '\\');
            PreparedScriptWriter.ValidateName(name);
            var lines = GameGenScriptText.Tokenize(GameGenScriptText.Decode(snapshot.Read(input, token)));
            int line = 0; List<ScriptInstruction> instructions = [];
            foreach (var tokens in lines)
            {
                line++;
                try { PreparedScriptWriter.ValidateTokens(tokens); }
                catch (InvalidDataException ex) { throw new InvalidDataException($"{input}, instruction {line}: {ex.Message}", ex); }
                instructions.Add(new(Guid.NewGuid(), tokens, ReadOnlyMemory<byte>.Empty, null));
            }
            // The engine prefers a loose script newer than its prepared copy, so the index records each file's time.
            long seconds = new DateTimeOffset(File.GetLastWriteTimeUtc(SourceProject.Resolve(root, input))).ToUnixTimeSeconds();
            entries.Add(new(Guid.NewGuid(), null, name, (uint)Math.Clamp(seconds, 0, uint.MaxValue), new byte[128], instructions, ReadOnlyMemory<byte>.Empty));
        }
        byte[] header = new byte[12]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0x08971119); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 7);
        var package = new PreparedScriptPackage(header, ReadOnlyMemory<byte>.Empty, entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray(), ReadOnlyMemory<byte>.Empty);
        return new(PreparedScriptWriter.Write(package, token), entries.Count, []);
    }

    private static Built BuildSounds(string root, SourceOutputPlan plan, Snapshot snapshot, DateTime now, CancellationToken token)
    {
        int bank = Array.IndexOf(Banks, plan.Path.ToLowerInvariant());
        var declared = File.Exists(SourceProject.Resolve(root, SoundDefinitions)) ? DeclaredFormats(snapshot.Read(SoundDefinitions, token), token) : new();
        List<string> warnings = []; List<ArchiveSources.Entry> entries = []; Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string input in plan.Inputs)
        {
            token.ThrowIfCancellationRequested(); string name = Path.GetFileName(input);
            if (names.TryGetValue(name, out string? other)) throw new InvalidDataException($"{other} and {input} would both become sound {name}; the engine finds sounds by name.");
            names[name] = input;
            byte[] source = snapshot.Read(input, token); byte[] payload;
            try
            {
                if (declared.TryGetValue(name, out var formats)) payload = WaveConverter.Convert(source, formats[bank], token);
                else { _ = WaveDecoder.Read(source, token); payload = source; if (bank == 0) warnings.Add($"{name} has no format in sounds.zrd; every bank uses the source format."); }
            }
            catch (InvalidDataException ex) { throw new InvalidDataException($"{input}: {ex.Message}", ex); }
            entries.Add(new(name, SourceField(input), payload));
        }
        return new(ArchiveSources.Write(entries, now), entries.Count, warnings);
    }

    /// <summary>
    /// HIGH/MED/LOW formats per WAV file from sounds.zrd, whose sound rows read
    /// <c>( id file flags… HIGH ( rate bits channels ) MED ( … ) LOW ( … ) )</c>. The first declaration of a file wins.
    /// </summary>
    internal static Dictionary<string, WaveFormat[]> DeclaredFormats(byte[] definitions, CancellationToken token)
    {
        var root = ZrdText.LooksLikeText(definitions) ? ZrdText.Parse(definitions, token) : ZrdDecoder.Read(definitions, token);
        Dictionary<string, WaveFormat[]> formats = new(StringComparer.OrdinalIgnoreCase);
        Visit(root, 0);
        return formats;
        void Visit(ZrdNode node, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (node.Kind != ZrdKind.Array || depth > 64) return;
            var c = node.Children;
            string? file = c.FirstOrDefault(n => n.Kind == ZrdKind.String && n.Text.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))?.Text;
            if (file != null)
            {
                WaveFormat? Read(string key)
                {
                    for (int i = 0; i + 1 < c.Count; i++)
                        if (c[i].Kind == ZrdKind.String && c[i].Text == key && c[i + 1] is { Kind: ZrdKind.Array, Children: [{ Kind: ZrdKind.Int } r, { Kind: ZrdKind.Int } b, { Kind: ZrdKind.Int } ch] })
                            return new((int)r.Bits, (int)b.Bits, (int)ch.Bits);
                    return null;
                }
                if (Read("HIGH") is { } high && Read("MED") is { } medium && Read("LOW") is { } low) formats.TryAdd(Path.GetFileName(file), [high, medium, low]);
            }
            foreach (var child in c) Visit(child, depth + 1);
        }
    }
}
