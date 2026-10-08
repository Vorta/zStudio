using System.Security.Cryptography;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record PickupPlacementSaveResult(IReadOnlyList<string> SavedPaths, IReadOnlyList<string> Errors);

public sealed partial class PickupPlacementEditSession
{
    private sealed record StagedArchive(string Source, ArchiveState Archive, string Destination, SealedFile File, byte[] Bytes, bool Replace);
    /// <summary>
    /// Puts a staged archive in place. It stays held against writes and renames from its check against the verified bytes
    /// until it is in place (<see cref="VerifiedDocumentSave.Seal"/>); a replaced archive is kept as the backup when one is given.
    /// </summary>
    internal Action<SealedFile, string, bool, string?> PublishFile { get; set; } = static (staged, destination, replace, backup) =>
    {
        if (replace) staged.Replace(destination, backup);
        else staged.MoveTo(destination);
    };

    /// <summary>Checks both the supplied and resolved destination. Throws if its location cannot be verified.</summary>
    public static bool IsProtectedPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return HasProtectedComponent(fullPath) ||
            (OperatingSystem.IsWindows() && HasProtectedComponent(WindowsSavePath.ResolveExistingParent(fullPath)));
    }
    private static bool HasProtectedComponent(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(p => p.Equals("zbd_1998", StringComparison.OrdinalIgnoreCase) || p.Equals("zbd_1999", StringComparison.OrdinalIgnoreCase));

    /// <summary>Stage and verify every output first. Report each atomic replacement independently.</summary>
    public async Task<PickupPlacementSaveResult> SaveAsync(IReadOnlyDictionary<string, string>? destinations = null, bool createBackup = false, CancellationToken token = default)
    {
        if (saving) throw new InvalidOperationException("A pickup save is already running.");
        if (ReadOnlyReason is { } reason) throw new InvalidOperationException(reason);
        if (destinations != null)
        {
            destinations = destinations.ToDictionary(p => Path.GetFullPath(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (destinations.Keys.Any(p => !archives.ContainsKey(p))) throw new ArgumentException("Save destination references an unknown pickup archive.", nameof(destinations));
        }
        saving = true; List<StagedArchive> staged = []; List<string> saved = [], errors = [];
        using DirectoryLease directories = new();
        try
        {
            HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (source, archive) in archives)
            {
                bool explicitDestination = destinations?.ContainsKey(source) == true;
                if (!explicitDestination && !IsArchiveDirty(source)) continue;
                string destination = Path.GetFullPath(explicitDestination ? destinations![source] : archive.Target);
                ValidateDestination(destination);
                ValidateDestination(directories.CapturedPath(destination));
                directories.Parent(destination, create: true);
                if (!targets.Add(destination)) throw new IOException("Two pickup archives cannot be saved to the same file.");
                // Explicit destinations are Save As, including copies requested by protected-source Save.
                // Only ordinary Save may replace the active target.
                bool replace = !explicitDestination && !archive.PendingCopy;
                if (replace) await CheckBaselineAsync(archive, token, directories);
                else if (explicitDestination && destination.Equals(Path.GetFullPath(archive.Target), StringComparison.OrdinalIgnoreCase) || File.Exists(destination) || Directory.Exists(destination))
                    throw new IOException($"Save As requires a new file: {destination}");
                if (!replace && archives.Any(a => destination.Equals(a.Value.Original.Path, StringComparison.OrdinalIgnoreCase) ||
                    a.Key != source && destination.Equals(a.Value.Target, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException($"Save As cannot alias a coordinate source or another archive destination: {destination}");
                byte[] bytes = await Task.Run(() => EncodeArchive(source), token);
                await Task.Run(() => Verify(source, bytes, token), token);
                string directory = Path.GetDirectoryName(destination)!;
                string temporary = Path.Combine(directory, ".zstudio-pickups-" + Guid.NewGuid().ToString("N") + ".tmp");
                // Creation owns failed-write cleanup; retain that same identity through verification and publication.
                var file = await SealedFile.CreateAsync(temporary, bytes, directories, token);
                staged.Add(new(source, archive, destination, file, bytes, replace));
                byte[] reopened = await file.ReadAllAsync(bytes.Length, token);
                if (!EqualBytes(bytes, reopened)) throw new IOException($"Written pickup archive verification failed: {destination}");
                await Task.Run(() => Verify(source, reopened, token), token);
            }
            // Once staging succeeds, preserve every requested copy even when later
            // publication fails. Ordinary Save retries the new path without replacement.
            foreach (var output in staged.Where(s => !s.Replace))
            { output.Archive.Target = output.Destination; output.Archive.PendingCopy = true; touched.Add(output.Source); }
            foreach (var output in staged)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    ValidateDestination(output.Destination);
                    if (output.Replace)
                    {
                        await CheckBaselineAsync(output.Archive, token, directories);
                        string? backup = createBackup ? output.Destination + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8] + ".bak" : null;
                        PublishFile(output.File, output.Destination, true, backup);
                    }
                    else PublishFile(output.File, output.Destination, false, null);
                    output.File.Dispose();
                    output.Archive.Target = output.Destination; output.Archive.SavedBytes = output.Bytes;
                    output.Archive.PendingCopy = false;
                    output.Archive.Stamp = FileStamp.ReadHolding(output.Destination, output.Bytes, directories);
                    if (output.Destination.Equals(output.Archive.Original.Path, StringComparison.OrdinalIgnoreCase)) output.Archive.SourceStamp = output.Archive.Stamp;
                    foreach (var entry in EntriesForArchive(output.Source, exact: true))
                    { var source = entry.Record.Source; savedPositions[source] = positions[source]; savedRotations[source] = rotations[source]; }
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
            foreach (var output in staged) output.File.Dispose();
            saving = false; Changed?.Invoke();
        }
    }
    private void Verify(string source, byte[] bytes, CancellationToken token)
    {
        var original = archives[source].Original;
        if (bytes.Length != original.Bytes.Length) throw new InvalidDataException("Pickup patch changed the archive length.");
        HashSet<int> permitted = [];
        foreach (var entry in EntriesForArchive(source, exact: true))
            foreach (var scalar in Scalars(entry))
                if (scalar.Value != scalar.Original)
                    for (int i = 0; i < 8; i++) permitted.Add(scalar.Offset + i);
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] != original.Bytes.Span[i] && !permitted.Contains(i)) throw new InvalidDataException($"Pickup patch changed unrelated byte 0x{i:X}.");
        var reopened = FormatRegistry.Default.OpenBytes(original.Path, bytes, token: token);
        if (reopened.Diagnostics.Any(d => d.Severity == "Error")) throw new InvalidDataException("Saved pickup archive could not be parsed completely.");
        foreach (var entry in EntriesForArchive(source, exact: true))
            foreach (var expected in Scalars(entry))
            {
                var scalar = ZrdDecoder.Read(reopened.Slice(expected.Offset, 8), token);
                float value = scalar.Kind == ZrdKind.Float ? BitConverter.UInt32BitsToSingle(scalar.Bits) : scalar.Kind == ZrdKind.Int ? unchecked((int)scalar.Bits) : float.NaN;
                if (value != expected.Value)
                    throw new InvalidDataException($"Saved transform {entry.Record.Source.ResourceName} #{entry.Record.Source.RecordIndex} has an unexpected component at 0x{expected.Offset:X}.");
            }
    }
    private static Task CheckBaselineAsync(ArchiveState archive, CancellationToken token, DirectoryLease directories) =>
        VerifiedDocumentSave.CheckBaselineAsync(archive.Target, archive.SavedBytes, token, directories);
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
