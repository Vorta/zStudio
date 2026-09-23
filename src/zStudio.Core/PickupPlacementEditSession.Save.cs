using System.Security.Cryptography;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record PickupPlacementSaveResult(IReadOnlyList<string> SavedPaths, IReadOnlyList<string> Errors);

public sealed partial class PickupPlacementEditSession
{
    private sealed record StagedArchive(string Source, ArchiveState Archive, string Destination, string Temporary, byte[] Bytes, bool Replace);

    public static bool IsProtectedPath(string path) => Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(p => p.Equals("zbd_1998", StringComparison.OrdinalIgnoreCase) || p.Equals("zbd_1999", StringComparison.OrdinalIgnoreCase));

    /// <summary>Stage and verify every output first. Report each atomic replacement independently.</summary>
    public async Task<PickupPlacementSaveResult> SaveAsync(IReadOnlyDictionary<string, string>? destinations = null, bool createBackup = false, CancellationToken token = default)
    {
        if (saving) throw new InvalidOperationException("A pickup save is already running.");
        if (destinations != null)
        {
            destinations = destinations.ToDictionary(p => Path.GetFullPath(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (destinations.Keys.Any(p => !archives.ContainsKey(p))) throw new ArgumentException("Save destination references an unknown pickup archive.", nameof(destinations));
        }
        saving = true; List<StagedArchive> staged = []; List<string> saved = [], errors = [];
        try
        {
            HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (source, archive) in archives)
            {
                bool explicitDestination = destinations?.ContainsKey(source) == true;
                if (!explicitDestination && !positions.Any(p => p.Key.ArchivePath == source && p.Value != savedPositions[p.Key])) continue;
                string destination = Path.GetFullPath(explicitDestination ? destinations![source] : archive.Target);
                ValidateDestination(destination);
                if (!targets.Add(destination)) throw new IOException("Two pickup archives cannot be saved to the same file.");
                bool replace = destination.Equals(Path.GetFullPath(archive.Target), StringComparison.OrdinalIgnoreCase);
                if (replace) await CheckBaselineAsync(archive, token);
                else if (File.Exists(destination)) throw new IOException($"Save As requires a new file: {destination}");
                byte[] bytes = await Task.Run(() => EncodeArchive(source), token);
                await Task.Run(() => Verify(source, bytes, token), token);
                string directory = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(directory);
                string temporary = Path.Combine(directory, ".zstudio-pickups-" + Guid.NewGuid().ToString("N") + ".tmp");
                // Register before writing so failed/canceled writes are cleaned up too.
                staged.Add(new(source, archive, destination, temporary, bytes, replace));
                await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
                byte[] reopened = await File.ReadAllBytesAsync(temporary, token);
                if (!EqualBytes(bytes, reopened)) throw new IOException($"Written pickup archive verification failed: {destination}");
                await Task.Run(() => Verify(source, reopened, token), token);
            }
            foreach (var output in staged)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (output.Replace)
                    {
                        await CheckBaselineAsync(output.Archive, token);
                        string? backup = createBackup ? output.Destination + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8] + ".bak" : null;
                        File.Replace(output.Temporary, output.Destination, backup);
                    }
                    else File.Move(output.Temporary, output.Destination, false);
                    output.Archive.Target = output.Destination; output.Archive.SavedBytes = output.Bytes;
                    output.Archive.Stamp = FileStamp.Read(output.Destination);
                    if (output.Destination.Equals(output.Archive.Original.Path, StringComparison.OrdinalIgnoreCase)) output.Archive.SourceStamp = output.Archive.Stamp;
                    foreach (var source in positions.Keys.Where(s => s.ArchivePath == output.Source)) savedPositions[source] = positions[source];
                    saved.Add(output.Destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    errors.Add($"{output.Destination}: {ex.Message}");
                    break; // Earlier commits remain saved; all remaining entries stay dirty.
                }
            }
            return new(saved.AsReadOnly(), errors.AsReadOnly());
        }
        finally
        {
            foreach (var output in staged)
                try { if (File.Exists(output.Temporary)) File.Delete(output.Temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { diagnostics.Add($"Could not remove save temporary {output.Temporary}: {ex.Message}"); }
            saving = false; Changed?.Invoke();
        }
    }
    private void Verify(string source, byte[] bytes, CancellationToken token)
    {
        var original = archives[source].Original;
        if (bytes.Length != original.Bytes.Length) throw new InvalidDataException("Pickup patch changed the archive length.");
        HashSet<int> permitted = [];
        foreach (var entry in entries.Values.Where(e => e.Record.Source.ArchivePath == source))
            for (int axis = 0; axis < 3; axis++)
                if (positions[entry.Record.Source][axis] != entry.Record.OriginalPosition[axis])
                    for (int i = 0; i < 8; i++) permitted.Add(entry.Offsets[axis] + i);
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] != original.Bytes.Span[i] && !permitted.Contains(i)) throw new InvalidDataException($"Pickup patch changed unrelated byte 0x{i:X}.");
        var reopened = FormatRegistry.Default.OpenBytes(original.Path, bytes, token: token);
        if (reopened.Diagnostics.Any(d => d.Severity == "Error")) throw new InvalidDataException("Saved pickup archive could not be parsed completely.");
        var resources = entries.Values.Where(e => e.Record.Source.ArchivePath == source).Select(e => e.Record.Source.AssetIndex).Distinct()
            .Select(i => new PickupPlacementResource(MissionDifficulty.Medium, reopened, reopened.Assets.Single(a => a.Index == i)));
        var check = Create(resources, token: token);
        foreach (var entry in entries.Values.Where(e => e.Record.Source.ArchivePath == source))
            if (!check.positions.TryGetValue(entry.Record.Source, out var position) || position != positions[entry.Record.Source])
                throw new InvalidDataException($"Saved pickup {entry.Record.Source.ResourceName} #{entry.Record.Source.RecordIndex} has an unexpected position.");
    }
    private static async Task CheckBaselineAsync(ArchiveState archive, CancellationToken token)
    {
        byte[] current = await File.ReadAllBytesAsync(archive.Target, token);
        if (!EqualBytes(current, archive.SavedBytes)) throw new IOException($"File changed outside zStudio: {archive.Target}. Reload or use Save As to preserve both versions.");
    }
    private static bool EqualBytes(byte[] a, byte[] b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b));
    private static void ValidateDestination(string path)
    {
        if (IsProtectedPath(path)) throw new IOException("Save pickup edits outside the protected zbd_1998 and zbd_1999 reference datasets.");
        if (!Path.GetExtension(path).Equals(".zbd", StringComparison.OrdinalIgnoreCase)) throw new IOException("Pickup archives must use the .zbd extension.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Saving through file links is not supported.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory != null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Saving through directory links is not supported; choose a direct destination.");
    }
}
