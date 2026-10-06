using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>What a reconstruction wrote. Game files whose family is not reconstructed yet are listed, not copied.</summary>
public sealed record SourceReconstructionReport(string Project, int SourceFiles, IReadOnlyDictionary<string, int> Families, IReadOnlyList<string> NotReconstructed, IReadOnlyList<string> Notes);

/// <summary>Reconstruct the original source tree (data/, gamegen/) from a shipped or exported RECOIL data folder.</summary>
public static class SourceExtractor
{
    public const int MaximumFiles = 10_000;
    /// <summary>
    /// What a reconstruction may hold in memory at once from the files it has read: sound banks, texture packs, worlds,
    /// animations, decoded resources and scripts stay until every file is read, because placing one family's sources needs
    /// the others' evidence. Each is counted as its bytes and an estimate of what decoding it keeps (see
    /// <see cref="ExtractFilesAsync"/>); the RECOIL releases need about 500 MiB (1999) and 360 MiB (1998). A folder that needs
    /// more is refused once the file that exceeds it has been read, and what was written is removed.
    /// </summary>
    public const long MaximumRetainedBytes = 1536L * 1024 * 1024;

    public static Task<SourceReconstructionReport> ExtractAsync(string corpusRoot, string projectRoot, IProgress<SourceProgress>? progress = null, CancellationToken token = default)
        => ExtractAsync(corpusRoot, projectRoot, MaximumRetainedBytes, progress, token);

    /// <param name="retainedBudget">The memory the reconstruction may hold at once (<see cref="MaximumRetainedBytes"/>; smaller in tests).</param>
    internal static async Task<SourceReconstructionReport> ExtractAsync(string corpusRoot, string projectRoot, long retainedBudget, IProgress<SourceProgress>? progress = null, CancellationToken token = default)
    {
        corpusRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(corpusRoot)); projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!Directory.Exists(corpusRoot)) throw new DirectoryNotFoundException("The game data folder does not exist.");
        SourceProject.ValidateSeparate(projectRoot, corpusRoot, "project folder");
        SourceProject.RejectLinks(projectRoot);
        if (Directory.Exists(projectRoot) && Directory.EnumerateFileSystemEntries(projectRoot).Any()) throw new IOException("Choose a new or empty folder for the source project.");
        var all = Corpus(corpusRoot, token);
        // The game reads its data from the folder itself and its mission folders (mN): only those files are reconstructed
        // and decide whether the folder can be. Files anywhere else, such as a demo copied into a subfolder, are not read.
        var files = all.Where(f => GameFile(f.Relative)).ToList();
        var probes = files.Select(f => (f.Relative, Probe: FormatRegistry.Probe(f.Path))).ToArray();
        // The recovered build layout is RECOIL's; MechWarrior 3 data uses other formats and folders (c1, t1), so a folder
        // without RECOIL mission folders is MechWarrior 3's when any of its files is.
        static bool Mw3(FormatProbe probe) => probe is { Family: FormatFamily.GameZ, Version: 27 } or { Family: FormatFamily.Animation, Version: 39 };
        string? mw3 = probes.FirstOrDefault(f => Mw3(f.Probe)).Relative ?? (files.Any(f => TextureSources.MissionNumber(f.Relative) > 0) ? null
            : all.Where(f => !GameFile(f.Relative)).Select(f => (f.Relative, Probe: FormatRegistry.Probe(f.Path))).FirstOrDefault(f => Mw3(f.Probe)).Relative);
        if (mw3 != null) throw new InvalidDataException($"{mw3} is MechWarrior 3 data; source reconstruction supports RECOIL.");
        // The 1998 demos' worlds (version 13) open read-only; projects are reconstructed from the releases.
        if (probes.FirstOrDefault(f => f.Probe is { Family: FormatFamily.GameZ, Version: 13 } && MissionWorld(f.Relative)) is { Relative: not null } demo)
            throw new InvalidDataException($"{demo.Relative} is a 1998 demo world (GameZ version 13), which zStudio opens read-only; source projects are reconstructed from the RECOIL releases.");
        // Require positive RECOIL evidence: prepared scripts, a version-15 world or a version-28 animation program.
        if (!probes.Any(f => f.Probe is { Family: FormatFamily.Scripts, Version: 7 } or { Family: FormatFamily.GameZ, Version: 15 } or { Family: FormatFamily.Animation, Version: 28 }))
            throw new InvalidDataException("No RECOIL game data was found. Choose the folder that contains interp.zbd, zrdr.zbd and the mission folders.");
        // The checks above decided on the files as the folder was listed; each file is read again when its sources are
        // reconstructed and must still be that file (see ReadInputAsync), and an archive the definitions check read whole
        // must still hold what it read.
        Dictionary<string, string> checkedContent = new(StringComparer.Ordinal);
        if (!await CarriesDefinitionsAsync(files, checkedContent, token)) throw new InvalidDataException(NotOriginal);
        Writes writes = new(projectRoot);
        writes.CreateDirectory(projectRoot);
        try { return await ExtractFilesAsync(projectRoot, files, checkedContent, [.. all.Where(f => !GameFile(f.Relative)).Select(f => f.Relative)], writes, retainedBudget, progress, token); }
        catch (Exception stopped)
        {
            // The folder was new or empty: remove everything this reconstruction wrote so it can be retried. Only that: a file
            // another program put there or changed during the run stays, with the folders holding it.
            var (changed, failed) = writes.Remove();
            if (changed.Count > 0 || failed.Count > 0)
            {
                // Say so, rather than leaving a folder later refused as not empty.
                string reason = (changed.Count > 0 ? $" Files another program changed during the reconstruction were left as they are: {string.Join(", ", changed.Take(8))}." : "")
                    + (failed.Count > 0 ? $" Files that could not be removed: {string.Join(", ", failed.Take(8))}." : "");
                throw new IOException($"{(stopped is OperationCanceledException ? "Canceled" : $"Stopped: {stopped.Message}")}. {projectRoot} could not be removed completely.{reason} Move them away before choosing the folder again.", stopped);
            }
            throw;
        }
    }

    /// <summary>Why a folder of game files that are not the original ones is refused.</summary>
    public const string NotOriginal = "zStudio can unpack only the original ZBD files.";

    /// <summary>
    /// Whether the files are the original ones, which a source tree is reconstructed from once: their resource archives
    /// carry the animation definitions (<c>anim.zrd</c>) every <c>anim.zbd</c> was compiled from. zStudio's exports leave
    /// the definitions in the project (<c>.zad</c>), so the tree only ever goes on to be exported, not unpacked again.
    /// </summary>
    /// <param name="read">Receives the digest of each archive read (by path), which it must still have when it is reconstructed.</param>
    private static async Task<bool> CarriesDefinitionsAsync(List<Input> files, Dictionary<string, string> read, CancellationToken token)
    {
        HashSet<string> carrying = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(f => Path.GetFileName(f.Relative).Equals("zrdr.zbd", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = await ReadInputAsync(file, null, token);
            read[file.Path] = SourceProject.Sha256(bytes);
            try
            {
                if (ArchiveSources.Read(bytes).Any(m => m.Name.Equals("anim.zrd", StringComparison.OrdinalIgnoreCase)))
                    carrying.Add(Path.GetDirectoryName(file.Relative) ?? "");
            }
            catch (InvalidDataException) { }
        }
        // Every mission's animations need the definitions beside them.
        return carrying.Count > 0 && files.Where(f => Path.GetFileName(f.Relative).Equals("anim.zbd", StringComparison.OrdinalIgnoreCase))
            .All(f => carrying.Contains(Path.GetDirectoryName(f.Relative) ?? ""));
    }

    /// <summary>A game file as the folder was listed: its size and modification time, which it must still have when it is read.</summary>
    internal sealed record Input(string Path, string Relative, long Length, DateTime Modified);

    /// <summary>
    /// A game file's bytes, read while no other program can write it. The checks before reconstruction decided on the files
    /// as the folder was listed, so a file that is no longer that file (gone, or another size or modification time), or no
    /// longer holds what a check read whole (<paramref name="checkedDigest"/>), is refused rather than reconstructed from
    /// data nothing checked: an original archive replaced by an exported one would otherwise give an incomplete project.
    /// </summary>
    private static async Task<byte[]> ReadInputAsync(Input file, string? checkedDigest, CancellationToken token)
    {
        FileStream stream;
        try { stream = new(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { throw Changed(ex); }
        await using (stream)
        {
            if (stream.Length != file.Length || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != file.Modified) throw Changed();
            byte[] bytes = new byte[file.Length];
            await stream.ReadExactlyAsync(bytes, token);
            if (checkedDigest != null && SourceProject.Sha256(bytes) != checkedDigest) throw Changed();
            return bytes;
        }
        IOException Changed(Exception? inner = null) =>
            new($"{file.Relative} changed after the game data folder was checked, so it is not the file that was checked. Reconstruct again once no other program is changing the folder.", inner);
    }

    /// <summary>
    /// The files and folders a reconstruction created, in order, and the content it left in each file, so that stopping
    /// removes exactly those. Files are created as new files: a name another program took during the run is never replaced.
    /// </summary>
    private sealed class Writes(string root)
    {
        /// <summary>The content each created file has (null: unknown, after a write that failed and could not be undone).</summary>
        private readonly Dictionary<string, JournalDigest?> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> folders = [];
        private static readonly JournalDigest Empty = JournalDigest.Of(Array.Empty<byte>())!;
        /// <summary>Creates <paramref name="folder"/> with the parents it lacks, recording each one created.</summary>
        internal void CreateDirectory(string folder)
        {
            List<string> missing = [];
            for (string? f = folder; f != null && !Directory.Exists(f); f = Path.GetDirectoryName(f)) missing.Add(f);
            Directory.CreateDirectory(folder);
            folders.AddRange(Enumerable.Reverse(missing));
        }
        /// <summary>
        /// Creates <paramref name="path"/> with <paramref name="bytes"/> (and its modification time). No other program can
        /// write, rename or delete it while it is written; a write that fails part-way leaves it empty, so stopping
        /// recognises and removes it.
        /// </summary>
        internal async Task CreateAsync(string path, byte[] bytes, DateTime? modified, CancellationToken token)
        {
            FileStream stream;
            // Unbuffered, so nothing reaches the file after a failed write is undone.
            try { stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.Asynchronous); }
            catch (IOException ex) when (Path.Exists(path))
            { throw new IOException($"{Display(path)} was created by another program during the reconstruction; it was not replaced, and the reconstruction stopped.", ex); }
            await using (stream)
            {
                files[path] = Empty;
                await WriteAsync(stream, path, bytes, modified, token);
            }
        }
        /// <summary>
        /// Rewrites a file this reconstruction created, keeping its modification time, only while it still has the content
        /// the reconstruction left in it (other programs cannot write it between the comparison and the write); another
        /// program's change is never overwritten.
        /// </summary>
        internal async Task ReplaceAsync(string path, byte[] bytes, CancellationToken token)
        {
            if (!files.TryGetValue(path, out var written) || written == null) throw new InvalidOperationException($"{Display(path)} was not written by this reconstruction.");
            FileStream stream;
            try { stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.Asynchronous); }
            catch (FileNotFoundException ex) { throw ChangedDuringRun(path, ex); }
            catch (DirectoryNotFoundException ex) { throw ChangedDuringRun(path, ex); }
            await using (stream)
            {
                // Another length differs without being read.
                if (!written.Matches(stream, token)) throw ChangedDuringRun(path);
                DateTime time = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                stream.Position = 0;
                await WriteAsync(stream, path, bytes, time, token);
            }
        }
        private async Task WriteAsync(FileStream stream, string path, byte[] bytes, DateTime? modified, CancellationToken token)
        {
            try
            {
                stream.SetLength(0);
                files[path] = Empty;
                await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
                if (modified is { } time) File.SetLastWriteTimeUtc(stream.SafeFileHandle, time);
                files[path] = JournalDigest.Of(bytes);
            }
            catch
            {
                // Still held: emptied, it is recognisably this run's file.
                try { stream.SetLength(0); files[path] = Empty; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { files[path] = null; }
                throw;
            }
        }
        private IOException ChangedDuringRun(string path, Exception? inner = null) =>
            new($"{Display(path)} was changed by another program during the reconstruction; it was not replaced, and the reconstruction stopped.", inner);
        private string Display(string path) => SourceProject.Relative(root, path);

        /// <summary>
        /// Deletes the recorded files that still have the content this reconstruction left in them, then the recorded
        /// folders left empty, deepest first. Returns the files left because another program changed them, and those that
        /// could not be removed.
        /// </summary>
        internal (List<string> Changed, List<string> Failed) Remove()
        {
            List<string> changed = [], failed = [];
            foreach (var (path, content) in files)
            {
                if (content == null) { if (Path.Exists(path)) failed.Add(Display(path)); continue; }
                // Moved to a new name beside it and deleted there, only while it is this run's file.
                string holding = Path.Combine(Path.GetDirectoryName(path)!, $".zstudio-removing-{Guid.NewGuid():N}");
                try
                {
                    switch (SourcePublisher.MoveIfContent(path, holding, content))
                    {
                        case SourcePublisher.Moved.Done: File.Delete(holding); break;
                        case SourcePublisher.Moved.Stranded: changed.Add(Display(holding)); break;
                        default: if (Path.Exists(path)) changed.Add(Display(path)); break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(Display(Path.Exists(holding) ? holding : path)); }
            }
            // A folder that is not empty holds files reported above or another program's, and stays.
            for (int i = folders.Count - 1; i >= 0; i--)
                try { if (Directory.Exists(folders[i]) && !Directory.EnumerateFileSystemEntries(folders[i]).Any()) Directory.Delete(folders[i]); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(folders[i]); }
            return (changed, failed);
        }
    }

    /// <summary>A file of the game's data: in the folder itself or in a mission folder (<c>mN</c>), where the game reads it.</summary>
    private static bool GameFile(string relative) => !relative.Contains('/') || relative.Count(c => c == '/') == 1 && TextureSources.MissionNumber(relative) > 0;
    /// <summary>A mission's world (<c>mN/gamez.zbd</c>), the worlds reconstruction reads.</summary>
    private static bool MissionWorld(string relative) => TextureSources.MissionNumber(relative) > 0 && Path.GetFileName(relative).Equals("gamez.zbd", StringComparison.OrdinalIgnoreCase);

    /// <param name="checkedContent">The digest of each file the checks before reconstruction read whole (by path), which it must still have.</param>
    /// <param name="elsewhere">Files outside the game's folders, listed as not reconstructed.</param>
    /// <param name="budget">
    /// The most the files read may keep in memory together (<see cref="MaximumRetainedBytes"/>). Each file is counted with
    /// what it keeps until the end: a sound bank its bytes, a texture pack its bytes and its decoded records, a world what its
    /// decoded nodes and geometry hold (its document is not kept), an animation file its bytes and the package decoded from
    /// them later, and resources and scripts what their decoded trees and tokens take (resources counted before they are
    /// decoded, since several members may share one payload). Each archive member also counts what is kept of it whatever it
    /// holds (the source written, or a note), so an archive of many empty members is counted too.
    /// </param>
    private static async Task<SourceReconstructionReport> ExtractFilesAsync(string projectRoot, List<Input> files, IReadOnlyDictionary<string, string> checkedContent, List<string> elsewhere, Writes writes, long budget, IProgress<SourceProgress>? progress, CancellationToken token)
    {
        Context context = new(projectRoot, writes, token);
        writes.CreateDirectory(Path.Combine(projectRoot, SourceProject.DataFolder)); writes.CreateDirectory(Path.Combine(projectRoot, SourceProject.GameGenFolder));
        Dictionary<string, int> families = []; List<string> skipped = [];
        List<(string Relative, IReadOnlyList<ArchiveSources.Member> Members)> soundBanks = [];
        List<(string Relative, ZbdDocument Document)> texturePacks = [];
        // A world is kept as the model its sources are reconstructed from (or why it cannot be one), not as its document,
        // which holds many times more.
        List<(string Relative, Worlds.GameZWorld? World, string? Failure)> worlds = [];
        List<(string Relative, byte[] Bytes)> animations = [];
        long retained = 0;
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var file = files[i]; string path = file.Path, relative = file.Relative; progress?.Report(new(i, files.Count, relative));
            byte[] bytes = await ReadInputAsync(file, checkedContent.GetValueOrDefault(path), token);
            var probe = FormatRegistry.Probe(bytes.AsSpan(0, Math.Min(36, bytes.Length)), bytes.AsSpan(Math.Max(0, bytes.Length - 8)), bytes.Length, Path.GetExtension(path));
            string? family = null;
            try
            {
                if (probe is { Recognition: Recognition.Supported, Family: FormatFamily.Archive })
                {
                    var members = ArchiveSources.Read(bytes);
                    bool waves = members.Count > 0 && members.All(m => IsWave(m.Payload.Span));
                    if (waves && SourceBuilder.Banks.Contains(relative, StringComparer.OrdinalIgnoreCase)) { Retain(relative, bytes.LongLength + MemberCost * members.Count); soundBanks.Add((relative, members)); family = "sounds"; }
                    else if (waves) context.Notes.Add($"{relative}: an archive of sounds that is not one of the soundsh/m/l banks; it was not reconstructed.");
                    else
                    {
                        // A decoded tree takes about ten times its compiled bytes, and each member is decoded on its own.
                        Retain(relative, Math.Max(bytes.LongLength, 16 * members.Sum(m => (long)m.Payload.Length)) + MemberCost * members.Count);
                        await context.ExtractResourcesAsync(relative, members); family = "resources";
                    }
                }
                else if (probe is { Recognition: Recognition.Supported, Family: FormatFamily.Scripts, Version: 7 }) { Retain(relative, 8L * bytes.LongLength); await context.ExtractScriptsAsync(relative, bytes); family = "scripts"; }
                else if (probe is { Recognition: Recognition.Supported, Family: FormatFamily.GameZ, Version: 15 } && MissionWorld(relative))
                {
                    var doc = FormatRegistry.Default.OpenBytes(relative, bytes, token: token);
                    if (doc.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException(error.Message);
                    // A world whose nodes the builder cannot hold (a cycle, an unsupported class) is reported when the worlds are reconstructed.
                    Worlds.GameZWorld? world = null; string? failure = null;
                    try { world = Worlds.GameZWorldReader.FromDocument(doc, token); } catch (InvalidDataException ex) { failure = ex.Message; }
                    if (world != null) Retain(relative, Footprint(world));
                    worlds.Add((relative, world, failure)); family = "worlds";
                }
                else if (probe is { Recognition: Recognition.Supported, Family: FormatFamily.Animation, Version: 28 } && TextureSources.MissionNumber(relative) > 0 && Path.GetFileName(relative).Equals("anim.zbd", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Animation.AnimationPackage.Read(bytes, token);
                    // The packages are decoded again together when the keyframe scripts are reconstructed (about three times the bytes).
                    Retain(relative, 4L * bytes.LongLength);
                    animations.Add((relative, bytes)); family = "animations";
                }
                else if (probe is { Recognition: Recognition.Supported, Family: FormatFamily.TexturePack } && IsTexturePack(relative))
                {
                    var doc = FormatRegistry.Default.OpenBytes(relative, bytes, token: token);
                    if (doc.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException(error.Message);
                    Retain(relative, bytes.LongLength + 2048L * doc.Assets.Count);
                    texturePacks.Add((relative, doc)); family = TextureSources.MissionNumber(relative) > 0 ? "textures" : "images";
                }
            }
            catch (InvalidDataException ex) { context.Notes.Add($"{relative}: not reconstructed because {ex.Message}"); family = null; }
            if (family != null) families[family] = families.GetValueOrDefault(family) + 1; else skipped.Add(relative);
        }
        // The work after reading the files reports what it is doing rather than leaving the last file's name shown.
        void Phase(SourceStage stage, string item) => progress?.Report(new(files.Count, files.Count, item, stage));
        if (soundBanks.Count > 0) { Phase(SourceStage.Reconstructing, "sound banks"); await context.ExtractSoundsAsync(soundBanks); }
        if (texturePacks.Count > 0) { Phase(SourceStage.Reconstructing, "textures"); await context.ExtractTexturesAsync(texturePacks); }
        if (worlds.Count > 0) { Phase(SourceStage.Reconstructing, "worlds"); await context.ExtractWorldsAsync(worlds); }
        if (animations.Count > 0) { Phase(SourceStage.Reconstructing, "animations"); await context.ExtractAnimationsAsync(animations, Phase); }
        progress?.Report(new(files.Count, files.Count, "Done"));
        return new(projectRoot, context.Written, families, [.. skipped, .. elsewhere], context.Notes);

        // Counted as each file is kept, so nothing more is read once the budget is exceeded; a refusal, not a file left out.
        void Retain(string relative, long bytes)
        {
            retained += bytes;
            if (retained > budget)
                throw new IOException($"The game data folder needs more memory than reconstruction holds at once ({budget / (1024 * 1024):N0} MiB, about three times what the RECOIL releases need): the sound banks, texture packs, worlds, animations, resources and scripts read up to {relative} are kept together until their sources are written. Reconstruct from the original game files.");
        }
    }
    /// <summary>What an archive member keeps besides its data: the record of its written source, or a note about it.</summary>
    private const long MemberCost = 512;
    /// <summary>
    /// What a decoded world keeps (the reconstruction counts it against <see cref="MaximumRetainedBytes"/>): its geometry as
    /// decoded, so models that share stored data count each copy, and its nodes, materials and textures. Measured on the
    /// releases, it is slightly more than the world holds.
    /// </summary>
    internal static long Footprint(Worlds.GameZWorld world)
    {
        long bytes = 1024L * (world.Nodes.Count + world.FreedSlots.Count) + 256L * (world.Materials.Count + world.Textures.Count);
        foreach (var model in world.Models)
        {
            bytes += 256 + 24L * (model.Vertices.Count + model.Normals.Count + model.Morphs.Count);
            foreach (var point in model.Points) bytes += 160 + 12L * point.Vertices.Length;
            foreach (var polygon in model.Polygons) bytes += 160 + 4L * (polygon.Vertices.Length + polygon.Normals.Length) + 8L * polygon.Uvs.Length;
        }
        return bytes;
    }
    /// <summary>Mission packs (<c>mN/texture*.zbd</c>, <c>mN/rtexture*.zbd</c>) and the interface pack (<c>image.zbd</c>, <c>rimage.zbd</c>).</summary>
    private static bool IsTexturePack(string relative)
    {
        string name = Path.GetFileName(relative).ToLowerInvariant();
        return TextureSources.MissionNumber(relative) > 0 && relative.Count(c => c == '/') == 1 && TexturePackVariant.FromFileName(name) is { Kind: not TexturePackKind.Interface }
            || !relative.Contains('/') && name is "image.zbd" or "rimage.zbd";
    }
    private static bool IsWave(ReadOnlySpan<byte> bytes) => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8);

    /// <summary>Shipped files in a stable order, relative with forward slashes, with their size and modification time. Links are refused.</summary>
    /// <param name="maximumEntries">The files and folders the listing may visit (<see cref="SourceProject.MaximumScannedEntries"/>; smaller in tests).</param>
    internal static List<Input> Corpus(string root, CancellationToken token = default, int maximumEntries = SourceProject.MaximumScannedEntries)
    {
        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 };
        List<Input> files = []; int visited = 0;
        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            token.ThrowIfCancellationRequested();
            // Folders count too: a tree of empty folders costs as much to walk as one of files.
            if (++visited > maximumEntries)
                throw new IOException($"The game data folder holds more than {maximumEntries:N0} files and folders; choose the folder that holds the game's ZBD files.");
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"{info.FullName} is a link; reconstruct from a folder of regular files.");
            if (info is not FileInfo file) continue;
            // As the file itself records them (a directory listing may lag behind), the way they are compared when it is read.
            file.Refresh();
            if (file.Length > FormatRegistry.MaximumDocumentBytes) throw new IOException($"{file.FullName} exceeds 512 MiB.");
            files.Add(new(file.FullName, Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.Length, file.LastWriteTimeUtc));
            if (files.Count > MaximumFiles) throw new IOException($"The game data folder has more than {MaximumFiles:N0} files.");
        }
        return files.OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Relative, StringComparer.Ordinal).ToList();
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

    private sealed class Context(string root, Writes writes, CancellationToken token)
    {
        private readonly Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Notes { get; } = [];
        internal int Written => sources.Count;
        /// <summary>Decoded resources and scripts, kept as evidence for placing textures and images.</summary>
        internal List<(string Archive, string Member, ZrdNode Tree)> Resources { get; } = [];
        internal Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> Scripts { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Direct3D clamp word of each reconstructed texture (by name), for the materials that use it.</summary>
        internal Dictionary<string, int> TextureAddressing { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Every texture source written, for resolving model textures the way the build searches its folders.</summary>
        internal HashSet<string> TextureFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>How each texture source written is transparent (by project path), for the materials that use it.</summary>
        internal Dictionary<string, Formats.TextureTransparency> TextureTransparencies { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Reconstructed texture source per mission and name.</summary>
        internal Dictionary<(int Mission, string Name), string> TexturePaths { get; } = [];

        /// <summary>Write a source once. Another version at the same path is reported and the first is kept.</summary>
        private async Task WriteAsync(string relative, byte[] bytes, DateTime? modified = null)
        {
            string sha = SourceProject.Sha256(bytes);
            if (sources.TryGetValue(relative, out string? existing)) { if (existing != sha) Notes.Add($"{relative} has another version with different content; the first one was kept."); return; }
            string path = SourceProject.Resolve(root, relative);
            writes.CreateDirectory(Path.GetDirectoryName(path)!);
            await writes.CreateAsync(path, bytes, modified, token);
            sources[relative] = sha;
        }

        /// <summary>Replaces a source written earlier (a definition rebuilt from what it compiled to), keeping its time.</summary>
        private async Task ReplaceAsync(string relative, byte[] bytes)
        {
            if (!sources.ContainsKey(relative)) { await WriteAsync(relative, bytes); return; }
            await writes.ReplaceAsync(SourceProject.Resolve(root, relative), bytes, token);
            sources[relative] = SourceProject.Sha256(bytes);
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
                    Resources.Add((output, m.Name, tree));
                    // The text form is bounded while it is built: a resource whose text would exceed the text-source limit stays compiled.
                    byte[]? text = null;
                    try { text = ZrdText.Encode(tree, token, SourceProject.MaximumSourceTextBytes); } catch (InvalidDataException) { }
                    bool asText = text != null && ZrdWriter.Write(ZrdText.Parse(text, token), token).AsSpan().SequenceEqual(payload);
                    if (!asText) Notes.Add($"{output}: {m.Name} kept as compiled data because its text form " + (text == null ? $"would exceed {SourceProject.MaximumSourceTextBytes / (1024 * 1024)} MiB." : "does not round-trip."));
                    if (Animation.AnimationDefinitionSet.HoldsDefinitions(tree))
                    {
                        // Animation definitions are not resources: they go to a .zad file beside it, and a resource that
                        // also holds other data (pickup.zrd: PICKUP_DATA) keeps only that.
                        var (definitions, rest) = Animation.AnimationDefinitionSet.Split(tree);
                        await WriteAsync($"{directory}/{Path.GetFileNameWithoutExtension(m.Name)}{Animation.AnimationDefinitionSet.Extension}", Encode(definitions, asText));
                        if (rest == null) continue;
                        source = Encode(rest, asText);
                    }
                    else if (asText) source = text!;
                }
                // Exported archives contain the .zrd resources of their zrdr folders, as the original build read them.
                else Notes.Add(m.Name.EndsWith(ZrdText.Extension, StringComparison.OrdinalIgnoreCase)
                    ? $"{output}: {m.Name} is not valid zReader data; it was written as stored and exports fail until it is replaced."
                    : $"{output}: {m.Name} is not zReader data; it was written as stored and exported archives leave it out.");
                await WriteAsync($"{directory}/{m.Name}", source);
            }

            byte[] Encode(ZrdNode node, bool text) => text ? ZrdText.Encode(node, token, SourceProject.MaximumSourceTextBytes) : ZrdWriter.Write(node, token);
        }

        internal async Task ExtractScriptsAsync(string output, byte[] bytes)
        {
            var doc = FormatRegistry.Default.OpenBytes(output, bytes, token: token);
            var package = doc.Scripts ?? throw new InvalidDataException("the prepared scripts are not a complete package");
            // The source scripts name the project's files: glTF models and PNG textures (see GameGenScriptText.ProjectFileNames).
            var modelMacros = GameGenScriptText.ModelMacros(package.Entries.Select(e => (IReadOnlyList<IReadOnlyList<string>>)[.. e.Instructions.Select(i => (IReadOnlyList<string>)i.Tokens)]));
            foreach (var entry in package.Entries)
            {
                token.ThrowIfCancellationRequested();
                var parts = entry.Name.Split('\\');
                if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) { Notes.Add($"{output}: script '{entry.Name}' is not a relative path and was skipped."); continue; }
                Scripts[entry.Name] = entry.Instructions.Select(i => (IReadOnlyList<string>)i.Tokens).ToArray();
                string text;
                try { text = GameGenScriptText.Write(GameGenScriptText.ProjectFileNames(Scripts[entry.Name], modelMacros)); }
                catch (InvalidDataException) { Notes.Add($"{output}: script {entry.Name} has instructions that cannot be written as text and was skipped."); continue; }
                // The prepared index records each script's modification time; the source file keeps it.
                await WriteAsync($"{SourceProject.GameGenFolder}/{string.Join('/', parts)}", Encoding.Latin1.GetBytes(text), DateTime.UnixEpoch.AddSeconds(entry.FileTime));
            }
        }

        /// <summary>Each sound's best-quality version across all banks becomes the source; lower banks are regenerated on export.</summary>
        internal async Task ExtractSoundsAsync(IReadOnlyList<(string Relative, IReadOnlyList<ArchiveSources.Member> Members)> banks)
        {
            Dictionary<string, (ReadOnlyMemory<byte> Bytes, long Quality)> best = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (relative, members) in banks)
                foreach (var m in members)
                {
                    token.ThrowIfCancellationRequested();
                    if (m.Name.Length == 0 || m.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Notes.Add($"{relative}: sound {m.Index} has an unusable name and was skipped."); continue; }
                    long quality;
                    try { quality = WaveConverter.Format(m.Payload).Quality; }
                    catch (InvalidDataException ex) { Notes.Add($"{relative}: {m.Name} is not a readable WAV ({ex.Message}) and was skipped."); continue; }
                    if (!best.TryGetValue(m.Name, out var current) || quality > current.Quality) best[m.Name] = (m.Payload, quality);
                }
            foreach (var (name, sound) in best.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) await WriteAsync($"{SourceBuilder.SoundsFolder}/{name}", sound.Bytes.ToArray());
        }

        /// <summary>
        /// Each texture's best version across every pack that holds it becomes one PNG in its source folder: the largest
        /// stored copy, preferring direct colour over a palette at equal size. Lower-quality packs are rebuilt on export.
        /// </summary>
        internal async Task ExtractTexturesAsync(IReadOnlyList<(string Relative, ZbdDocument Document)> packs)
        {
            Dictionary<(string Folder, string Name), List<(int Mission, ZbdDocument Doc, AssetRecord Asset)>> candidates = [];
            foreach (var mission in packs.Where(p => TextureSources.MissionNumber(p.Relative) > 0).GroupBy(p => TextureSources.MissionNumber(p.Relative)).OrderBy(g => g.Key))
            {
                token.ThrowIfCancellationRequested();
                // Every variant of a mission lists the same names in the same order; the largest pack is the reference.
                var reference = mission.OrderByDescending(p => p.Document.Assets.Count).ThenBy(p => p.Relative, StringComparer.Ordinal).First();
                var names = reference.Document.Assets.Where(a => a.Kind == AssetKind.Texture).Select(a => a.Name).ToArray();
                var folders = TextureSources.PlaceMission($"m{mission.Key}", names, Multiplayer(mission.Key), Notes);
                foreach (var (relative, doc) in mission.OrderBy(p => p.Relative, StringComparer.Ordinal))
                    foreach (var asset in doc.Assets.Where(a => a.Kind == AssetKind.Texture && a.Content is TextureInfo))
                    {
                        string name = asset.Name.ToLowerInvariant();
                        if (!folders.TryGetValue(name, out string? folder)) { folder = $"data/m{mission.Key}/textures"; Notes.Add($"{relative}: {asset.Name} is not in the reference pack; placed in {folder}."); }
                        (candidates.TryGetValue((folder, name), out var list) ? list : candidates[(folder, name)] = []).Add((mission.Key, doc, asset));
                    }
            }
            foreach (var (relative, doc) in packs.Where(p => TextureSources.MissionNumber(p.Relative) == 0).OrderBy(p => p.Relative, StringComparer.Ordinal))
            {
                var assets = doc.Assets.Where(a => a.Kind == AssetKind.Texture && a.Content is TextureInfo).ToArray();
                var folders = TextureSources.PlaceImages(assets.Select(a => a.Name).ToArray(), Resources);
                for (int i = 0; i < assets.Length; i++)
                {
                    string name = assets[i].Name.ToLowerInvariant();
                    (candidates.TryGetValue((folders[i], name), out var list) ? list : candidates[(folders[i], name)] = []).Add((0, doc, assets[i]));
                }
            }
            foreach (var ((folder, name), list) in candidates.OrderBy(c => c.Key.Folder, StringComparer.Ordinal).ThenBy(c => c.Key.Name, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.Length == 0) { Notes.Add($"Texture '{name}' has an unusable name and was skipped."); continue; }
                var best = list.Select(c => (c, Info: (TextureInfo)c.Asset.Content!))
                    .OrderByDescending(c => (long)c.Info.Width * c.Info.Height).ThenBy(c => c.Info.PaletteCount > 0 ? 1 : 0).First().c;
                string path = $"{folder}/{name}{TextureSources.Extension}";
                var image = TextureDecoder.Decode(best.Doc, best.Asset, token);
                await WriteAsync(path, Export.PngEncoder.Encode(image, token)); TextureFiles.Add(path);
                TextureTransparencies[path] = Formats.TexturePackBuilder.Classify(image);
                int addressing = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(best.Doc.Bytes.Span[(int)(best.Asset.Offset + 14)..]) & 3;
                if (addressing != 0) TextureAddressing.TryAdd(name, addressing);
                foreach (int mission in list.Select(c => c.Mission).Distinct()) TexturePaths.TryAdd((mission, name), path);
            }
        }
        /// <summary>Model sources from the mission worlds, replayed against their build scripts (see <see cref="WorldSources"/>).</summary>
        internal async Task ExtractWorldsAsync(IReadOnlyList<(string Relative, Worlds.GameZWorld? World, string? Failure)> worlds)
        {
            List<WorldSources.MissionWorld> missions = [];
            foreach (var (relative, world, failure) in worlds.OrderBy(w => TextureSources.MissionNumber(w.Relative)))
            {
                // A world whose nodes the builder cannot hold (a cycle, an unsupported class) is left out, like other files.
                if (world != null) missions.Add(new(TextureSources.MissionNumber(relative), world));
                else Notes.Add($"{relative}: its models were not reconstructed because {failure}");
            }
            foreach (var mission in missions) WorldNodes[mission.Mission] = mission.World.Nodes.Select(n => n.Name).ToArray();
            var outputs = await Task.Run(() => WorldSources.Reconstruct(missions, name => Scripts.GetValueOrDefault(name),
                (mission, name) => TexturePaths.GetValueOrDefault((mission, name)), TextureFiles, name => TextureAddressing.GetValueOrDefault(name), Notes, token,
                path => TextureTransparencies.TryGetValue(path, out var transparency) ? transparency : null), token);
            // Only folders that receive files are created: a folder the scripts search but no shipped file came from (the
            // multiplayer missions' vehicle folders) is left out; the build finds nothing in a missing folder either.
            foreach (var output in outputs) await WriteAsync(output.Path, output.Bytes);
        }
        /// <summary>Node names of each shipped world, which animation definitions bind to.</summary>
        internal Dictionary<int, IReadOnlyCollection<string>> WorldNodes { get; } = [];
        /// <summary>Keyframe scripts of the shipped animations (see <see cref="AnimationSources"/>); definitions come with the resources.</summary>
        internal async Task ExtractAnimationsAsync(IReadOnlyList<(string Relative, byte[] Bytes)> animations, Action<SourceStage, string>? status = null)
        {
            List<AnimationSources.MissionAnimation> missions = [];
            foreach (var (relative, bytes) in animations.OrderBy(a => TextureSources.MissionNumber(a.Relative)))
            {
                int mission = TextureSources.MissionNumber(relative);
                if (!WorldNodes.TryGetValue(mission, out var nodes)) { Notes.Add($"{relative}: the mission has no world, so its animations' keyframe scripts were not reconstructed."); continue; }
                missions.Add(new(mission, Animation.AnimationPackage.Read(bytes, token), Stamps(bytes), nodes));
            }
            var outputs = await Task.Run(() => AnimationSources.Reconstruct(missions, new DiskFiles(root), Notes, token, status), token);
            // Scripts are new; definitions are the shipped ones rebuilt where they no longer matched anim.zbd.
            foreach (var output in outputs)
                if (output.Path.EndsWith(Animation.AnimationDefinitionSet.Extension, StringComparison.OrdinalIgnoreCase)) await ReplaceAsync(output.Path, output.Bytes);
                else await WriteAsync(output.Path, output.Bytes);
        }
        /// <summary>The source paths an animation file records (80-character paths, each with its modification time).</summary>
        private static List<(string Path, uint Time)> Stamps(byte[] bytes)
        {
            int count = (int)Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), (uint)((bytes.Length - 12) / 84));
            List<(string, uint)> stamps = [];
            for (int i = 0; i < count; i++)
            {
                var field = bytes.AsSpan(12 + i * 84, 80); int end = field.IndexOf((byte)0);
                stamps.Add((System.Text.Encoding.Latin1.GetString(end < 0 ? field : field[..end]), System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12 + i * 84 + 80))));
            }
            return stamps;
        }
        /// <summary>The project as written so far.</summary>
        private sealed class DiskFiles(string root) : Worlds.IProjectFiles
        {
            public bool Exists(string relative) => File.Exists(SourceProject.Resolve(root, relative));
            public byte[] Read(string relative, CancellationToken token) => File.ReadAllBytes(SourceProject.Resolve(root, relative));
        }
        /// <summary>A mission is multiplayer when its load script sources the shared multiplayer vehicle (support\bftmulti.gw).</summary>
        private bool Multiplayer(int mission) =>
            Scripts.TryGetValue($"support\\loadm{mission}.gw", out var lines) && lines.Any(l => l.Any(t => t.Equals("support\\bftmulti.gw", StringComparison.OrdinalIgnoreCase)));
    }
}
