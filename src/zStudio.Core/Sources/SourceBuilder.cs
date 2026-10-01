using System.Text;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A game file that the source tree can build, and the source files it is built from.</summary>
public sealed record SourceOutputPlan(string Path, string Family, IReadOnlyList<string> Inputs)
{
    /// <summary>For a texture pack, the budget and largest texture side its profile builds it with.</summary>
    public Formats.TexturePackVariant? Pack { get; init; }
}
/// <summary>Per-output result: built (and, for an export, written) or failed.</summary>
public sealed record SourceExportResult(string Path, string Family, string Status, long Bytes, int Items, IReadOnlyList<string> Warnings, string? Error = null);
public sealed record SourceExportReport(string? Destination, IReadOnlyList<SourceExportResult> Outputs)
{
    /// <summary>The build profile the outputs were built with.</summary>
    public string Profile { get; init; } = BuildProfiles.Modern.Name;
    /// <summary>Findings about the destination as a whole, such as larger texture packs there that the game would prefer.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    public int Built => Outputs.Count(o => o.Status == "built");
    public int Failed => Outputs.Count(o => o.Status == "failed");
}

/// <summary>
/// Builds game files from a source tree, as the original gamegen build did: resource archives from each <c>zrdr</c> folder,
/// prepared scripts from <c>gamegen</c>, the three sound banks from the best-quality WAVs converted to the formats
/// that <c>sounds.zrd</c> declares, interface images and mission texture packs from PNGs, and each mission world by
/// running its build script (<c>gamegen/mN.gs</c>) over the glTF model sources, and each mission's animations from
/// their definitions (<c>data/mN/zrdr/anim.zrd</c>) and keyframe scripts against that world. Output must work in the
/// game; it does not reproduce the shipped bytes.
/// </summary>
public static partial class SourceBuilder
{
    public const string SoundsFolder = "data/common/sounds", SoundDefinitions = "data/common/zrdr/sounds.zrd";
    /// <summary>The HIGH, MED and LOW sound banks, in the order of their sounds.zrd declarations.</summary>
    internal static readonly string[] Banks = ["soundsh.zbd", "soundsm.zbd", "soundsl.zbd"];
    /// <summary>The mission texture packs the default (modern) profile builds; see <see cref="BuildProfiles"/>.</summary>
    public static IReadOnlyList<string> TexturePacks => BuildProfiles.Modern.TexturePacks.Select(p => p.File).ToArray();
    [GeneratedRegex(@"\Am\d{1,3}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex MissionFolder();

    /// <summary>
    /// Every game file this tree can build, in a stable order; <paramref name="added"/> are pending new files (see
    /// <see cref="SourceWorkspace"/>). Texture packs follow <paramref name="profile"/> (the built-in modern one when null).
    /// </summary>
    public static IReadOnlyList<SourceOutputPlan> Plan(string root, IReadOnlyCollection<string>? added = null, BuildProfile? profile = null)
    {
        profile ??= BuildProfiles.Modern;
        if (!SourceProject.IsProject(root)) throw new InvalidDataException("This folder is not a source project (it needs data and gamegen folders).");
        List<SourceOutputPlan> plans = [];
        static bool Zrd(string name) => name.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase);
        // Common resources come from every zrdr folder under data/common (including multi_bft/zrdr).
        var common = SourceProject.Files(root, "data/common", Zrd, added).Where(p => p.Split('/').Contains("zrdr", StringComparer.OrdinalIgnoreCase)).ToArray();
        if (common.Length > 0) plans.Add(new("zrdr.zbd", "archive", common));
        var scripts = SourceProject.Files(root, SourceProject.GameGenFolder, n => n.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".gw", StringComparison.OrdinalIgnoreCase), added);
        if (scripts.Count > 0) plans.Add(new("interp.zbd", "scripts", scripts));
        var sounds = SourceProject.Files(root, SoundsFolder, n => n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase), added);
        if (sounds.Count > 0) plans.AddRange(Banks.Select(bank => new SourceOutputPlan(bank, "sounds", sounds)));
        var missions = new DirectoryInfo(SourceProject.Resolve(root, SourceProject.DataFolder)).EnumerateDirectories().Where(d => MissionFolder().IsMatch(d.Name)).OrderBy(d => int.Parse(d.Name.AsSpan(1))).ToArray();
        // A world may load any model in the project, and animations any keyframe script; which depends on the sources.
        IReadOnlyList<string>? models = null, scriptsFound = null;
        // Interface images: fonts, the images tree and each mission's objective images.
        var images = SourceProject.Files(root, TextureSources.Fonts, Png, added).Concat(SourceProject.Files(root, TextureSources.Images, Png, added))
            .Concat(missions.SelectMany(m => SourceProject.Files(root, $"data/{m.Name}/images", Png, added))).ToArray();
        if (images.Length > 0) plans.Add(new("image.zbd", "images", images));
        foreach (var mission in missions)
        {
            string name = mission.Name.ToLowerInvariant();
            string entry = WorldScript(name);
            if (File.Exists(SourceProject.Resolve(root, entry)) || added?.Contains(entry, StringComparer.OrdinalIgnoreCase) == true)
            {
                models ??= SourceProject.Files(root, SourceProject.DataFolder, IsModelSource, added);
                // A project without glTF models has no world to build (buffers alone are not models).
                if (models.Any(m => !m.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))) plans.Add(new($"{name}/gamez.zbd", "world", [entry, .. models]));
            }
            var resources = SourceProject.Files(root, $"data/{name}/zrdr", Zrd, added);
            if (resources.Count > 0) plans.Add(new($"{name}/zrdr.zbd", "archive", resources));
            string definitions = AnimationRoot(name);
            if (File.Exists(SourceProject.Resolve(root, definitions)) || added?.Contains(definitions, StringComparer.OrdinalIgnoreCase) == true)
            {
                scriptsFound ??= SourceProject.Files(root, SourceProject.DataFolder, n => n.EndsWith(Animation.AnimationScript.Extension, StringComparison.OrdinalIgnoreCase), added);
                plans.Add(new($"{name}/anim.zbd", "animations", [definitions, .. scriptsFound]));
            }
            var textures = MissionTextures(root, name, added);
            if (textures.Count > 0) plans.AddRange(profile.TexturePacks.Select(pack => new SourceOutputPlan($"{name}/{pack.File}", "textures", textures) { Pack = pack.Variant }));
        }
        return plans;
        static bool Png(string name) => name.EndsWith(TextureSources.Extension, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>The script that builds a mission's world (gamegen/mN.gs).</summary>
    internal static string WorldScript(string mission) => $"{SourceProject.GameGenFolder}/{mission}.gs";
    /// <summary>The root of a mission's animation definitions.</summary>
    internal static string AnimationRoot(string mission) => $"data/{mission}/zrdr/anim.zrd";
    private static bool IsModelSource(string name) => name.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every project file read by one run. A file read by several outputs must have the same content each time, and no
    /// file may change before the run publishes, so an export always corresponds to one state of the project.
    /// </summary>
    internal sealed class Snapshot(string root, IReadOnlyDictionary<string, byte[]>? overlay = null)
    {
        private readonly Dictionary<string, (string Sha, FileStamp Stamp)> files = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Every project file the run read or asked about, from the overlay or the disk: what its outputs depend on.</summary>
        private readonly HashSet<string> dependencies = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock dependencyGate = new();
        internal IReadOnlyCollection<string> Dependencies() { lock (dependencyGate) return dependencies.ToArray(); }
        /// <summary>Pending files the disk does not hold yet (new files of a workspace), which count as present.</summary>
        internal IReadOnlyCollection<string> Added { get; } = overlay?.Keys.Where(k => !File.Exists(SourceProject.Resolve(root, k))).ToArray() ?? [];
        internal void Depend(string relative) { lock (dependencyGate) dependencies.Add(relative); }
        /// <summary>The project files read from disk and their stamps (pending content that replaced files is not included).</summary>
        internal IReadOnlyDictionary<string, FileStamp> Stamps() => files.ToDictionary(f => f.Key, f => f.Value.Stamp, StringComparer.OrdinalIgnoreCase);
        internal byte[] Read(string relative, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Depend(relative);
            if (overlay?.TryGetValue(relative, out var pending) == true) return pending;
            string path = SourceProject.Resolve(root, relative);
            var stamp = FileStamp.Read(path);
            if (stamp.Length > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException($"{relative} exceeds 512 MiB.");
            byte[] bytes = File.ReadAllBytes(path); string sha = SourceProject.Sha256(bytes);
            if (FileStamp.Read(path) != stamp) throw new InvalidDataException($"{relative} changed while it was read; export again.");
            if (files.TryGetValue(relative, out var first) && (first.Sha != sha || first.Stamp != stamp)) throw new InvalidDataException($"{relative} changed while exporting; export again.");
            files[relative] = (sha, stamp); return bytes;
        }
        private HashSet<string>? damageMasks;
        /// <summary>Textures the scripts register as damage-mark masks (WriteTextureSetMap), read once per run.</summary>
        internal HashSet<string> DamageMasks(CancellationToken token)
        {
            if (damageMasks != null) return damageMasks;
            damageMasks = new(StringComparer.OrdinalIgnoreCase);
            foreach (string script in SourceProject.Files(root, SourceProject.GameGenFolder, n => n.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".gw", StringComparison.OrdinalIgnoreCase), Added))
                foreach (var line in GameGenScriptText.Tokenize(GameGenScriptText.Decode(Read(script, token))))
                    if (line.Count > 1 && line[0].Equals("WriteTextureSetMap", StringComparison.OrdinalIgnoreCase)) damageMasks.Add(Path.GetFileNameWithoutExtension(line[1]));
            return damageMasks;
        }
        /// <summary>A mission world assembled once per run; its texture packs hold the textures it uses.</summary>
        internal sealed record AssembledWorld(GameZWorld World, IReadOnlyList<string> Warnings, IReadOnlyDictionary<string, string> TextureFiles, IReadOnlyDictionary<string, int> TextureAddressing, IReadOnlyList<WorldNode> LoadedRoots)
        {
            public IReadOnlyDictionary<WorldNode, WorldNodeProvenance> Provenance { get; init; } = new Dictionary<WorldNode, WorldNodeProvenance>();
            public IReadOnlyDictionary<(string Script, int Line), int> Executions { get; init; } = new Dictionary<(string, int), int>();
            public SourceInstruction? WriteInstruction { get; init; }
        }
        private readonly Dictionary<string, (AssembledWorld? World, Exception? Failure)> worlds = new(StringComparer.OrdinalIgnoreCase);
        internal bool HasWorld(string mission) => overlay?.ContainsKey(WorldScript(mission)) == true || File.Exists(SourceProject.Resolve(root, WorldScript(mission)));
        internal AssembledWorld World(string mission, CancellationToken token)
        {
            if (!worlds.TryGetValue(mission, out var cached))
            {
                try
                {
                    WorldAssembler assembler = new(new ProjectFiles(this, root, overlay), token);
                    var world = assembler.Assemble($"{mission}.gs");
                    cached = (new(world, assembler.Warnings, new Dictionary<string, string>(assembler.TextureFiles, StringComparer.OrdinalIgnoreCase),
                        new Dictionary<string, int>(assembler.TextureAddressing, StringComparer.OrdinalIgnoreCase), [.. assembler.LoadedRoots]) { Provenance = assembler.Provenance, Executions = assembler.Executions, WriteInstruction = assembler.WriteInstruction }, null);
                }
                catch (Exception ex) when (IsBuildFailure(ex)) { cached = (null, ex); }
                worlds[mission] = cached;
            }
            if (cached.Failure != null) throw new InvalidDataException($"The {mission} world does not assemble: {cached.Failure.Message}", cached.Failure);
            return cached.World!;
        }
        /// <summary>The project as the compilers read it: through the snapshot, refusing links.</summary>
        internal IProjectFiles Files() => new ProjectFiles(this, root, overlay);
        /// <summary>The assembler's view of the project: reads go through the snapshot, and links are refused.</summary>
        private sealed class ProjectFiles(Snapshot snapshot, string root, IReadOnlyDictionary<string, byte[]>? overlay) : IProjectFiles
        {
            public bool Exists(string relative)
            {
                snapshot.Depend(relative);
                if (overlay?.ContainsKey(relative) == true) return true;
                if (!File.Exists(SourceProject.Resolve(root, relative))) return false;
                SourceProject.RejectNestedLinks(root, relative);
                return true;
            }
            public byte[] Read(string relative, CancellationToken token) { if (overlay?.ContainsKey(relative) != true) SourceProject.RejectNestedLinks(root, relative); return snapshot.Read(relative, token); }
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
    /// <summary>
    /// Whether an exception from building one output means that output failed. Sources are user files in any state
    /// (a glTF with invalid JSON, for example), so every failure is reported with its output and nothing is written;
    /// only cancellation and exhausted memory end the run.
    /// </summary>
    internal static bool IsBuildFailure(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException);

    /// <summary>Build the selected outputs (all when null) in memory and report problems without writing anything.</summary>
    public static Task<SourceExportReport> CheckAsync(string root, IReadOnlyCollection<string>? outputs = null, IProgress<SourceProgress>? progress = null, CancellationToken token = default, string? profile = null)
        => RunAsync(root, null, outputs, false, progress, token, profile);

    /// <summary>
    /// Build the selected outputs (all when null) into <paramref name="destination"/>. Existing game files there are replaced
    /// only with <paramref name="overwrite"/>; publication restores them if any step fails, and nothing is written if any output fails.
    /// </summary>
    public static Task<SourceExportReport> ExportAsync(string root, string destination, IReadOnlyCollection<string>? outputs = null, bool overwrite = false, IProgress<SourceProgress>? progress = null, CancellationToken token = default, string? profile = null)
        => RunAsync(root, destination, outputs, overwrite, progress, token, profile);

    /// <summary><paramref name="profileName"/> selects the build profile (the project's default when null).</summary>
    private static async Task<SourceExportReport> RunAsync(string root, string? destination, IReadOnlyCollection<string>? outputs, bool overwrite, IProgress<SourceProgress>? progress, CancellationToken token, string? profileName)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var profile = await Task.Run(() => BuildProfiles.Find(root, profileName), token);
        var all = await Task.Run(() => Plan(root, null, profile), token);
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
                catch (Exception ex) when (IsBuildFailure(ex))
                { results.Add(new(plan.Path, plan.Family, "failed", 0, 0, [], ex.Message)); }
            }
            progress?.Report(new(selected.Count, selected.Count, destination == null ? "Checked" : "Publishing"));
            snapshot.CheckUnchanged(token);
            if (staging != null && destination != null)
            {
                if (results.Any(r => r.Status == "failed")) throw new InvalidDataException("Nothing was written because some outputs failed: " + string.Join("; ", results.Where(r => r.Status == "failed").Select(r => $"{r.Path}: {r.Error}")));
                Publish(staging, destination, results.Select(r => r.Path).ToArray(), overwrite, token);
            }
            // The game opens the largest hardware pack its texture memory allows, so one left from another export would win.
            List<string> notes = [];
            if (destination != null)
                foreach (string mission in results.Where(r => r.Family == "textures").Select(r => r.Path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (string pack in BuildProfiles.ShadowingPacks(destination, mission, profile))
                        notes.Add($"{pack} is not a pack the {profile.Name} profile builds, but the game may load it instead of the exported ones. Delete it, or export with a profile that builds it.");
            return new(destination, results) { Profile = profile.Name, Notes = notes };
        }
        finally { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    /// <summary>
    /// Move staged outputs into place; replaced files move aside first and are restored if any later step fails. Without
    /// <paramref name="overwrite"/>, a game file that appeared while the outputs were built is never replaced.
    /// </summary>
    private static void Publish(string staging, string destination, IReadOnlyList<string> outputs, bool overwrite, CancellationToken token)
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
                if (File.Exists(target) && !overwrite) throw new IOException($"{relative} appeared in {destination} during the export; nothing was replaced. Export again and allow replacing it.");
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
        "images" => BuildImages(plan, snapshot, token),
        "textures" => BuildTexturePack(plan, snapshot, token),
        "world" => BuildWorld(plan, snapshot, token),
        "animations" => BuildAnimations(root, plan, snapshot, token),
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

    /// <summary>image.zbd: every interface image at its authored size in direct colour, ordered by source path.</summary>
    private static Built BuildImages(SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        List<PackTexture> textures = [];
        foreach (string input in plan.Inputs)
        {
            token.ThrowIfCancellationRequested();
            textures.Add(new(TextureName(input), TextureSources.SortKey(input), DecodeTexture(input, snapshot.Read(input, token), token)));
        }
        var built = TexturePackBuilder.Build(textures, TexturePackVariant.FromFileName("image.zbd")!, token);
        return new(built.Bytes, textures.Count, built.Warnings);
    }
    /// <summary>
    /// The textures a mission pack holds: every PNG in the folders the mission searches (support\common.gw), without
    /// the other campaign missions' vehicle folders. The first folder holding a name wins, as the engine takes the first
    /// match.
    /// </summary>
    internal static IReadOnlyList<string> MissionTextures(string root, string mission, IReadOnlyCollection<string>? added = null)
    {
        List<string> inputs = []; HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in TextureSources.MissionFolders(mission, Multiplayer(root, mission)))
            foreach (string file in SourceProject.Files(root, folder, n => n.EndsWith(TextureSources.Extension, StringComparison.OrdinalIgnoreCase), added))
                // Subfolders of a search folder are separate folders (bft is listed on its own).
                if (Path.GetDirectoryName(file)!.Replace('\\', '/').Equals(folder, StringComparison.OrdinalIgnoreCase) && names.Add(Path.GetFileNameWithoutExtension(file))) inputs.Add(file);
        return inputs;
    }
    /// <summary>A mission is multiplayer when its load script sources the shared multiplayer vehicle.</summary>
    internal static bool Multiplayer(string root, string mission)
    {
        string script = SourceProject.Resolve(root, $"{SourceProject.GameGenFolder}/support/load{mission}.gw");
        if (!File.Exists(script) || new FileInfo(script).Length > SourceProject.MaximumSourceTextBytes) return false;
        return GameGenScriptText.Tokenize(GameGenScriptText.Decode(File.ReadAllBytes(script))).Any(l => l.Any(t => t.Equals("support\\bftmulti.gw", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A mission texture pack at the variant's budget. Direct colour in software packs is kept for the textures the
    /// engine requires unpaletted: the damage masks that WriteTextureSetMap stamps and the player-vehicle textures they
    /// stamp (ApplyDamageMaskStampOnHit, retail 0x479660).
    /// </summary>
    private static Built BuildTexturePack(SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        var variant = plan.Pack ?? TexturePackVariant.FromFileName(Path.GetFileName(plan.Path)) ?? throw new InvalidDataException($"{plan.Path} is not a texture pack name.");
        List<PackTexture> textures = [];
        var (inputs, addressing, warnings) = PackInputs(plan, snapshot, token);
        foreach (var (input, name) in inputs)
        {
            token.ThrowIfCancellationRequested();
            string folder = Path.GetDirectoryName(input)!.Replace('\\', '/');
            bool vehicle = folder.EndsWith("/bft", StringComparison.OrdinalIgnoreCase) || folder.Equals(TextureSources.MultiBftTextures, StringComparison.OrdinalIgnoreCase);
            textures.Add(new(name, TextureSources.SortKey(input), DecodeTexture(input, snapshot.Read(input, token), token), addressing.GetValueOrDefault(name), vehicle || snapshot.DamageMasks(token).Contains(name)));
        }
        var built = TexturePackBuilder.Build(textures, variant, token);
        return new(built.Bytes, textures.Count, [.. warnings, .. built.Warnings]);
    }
    /// <summary>
    /// A mission pack holds its texture folders and every texture its world uses from elsewhere: a model brought in
    /// from another mission names textures in that mission's folders, and the game only finds textures in its packs.
    /// A name already in the folders keeps the folder's image, as the engine finds the first match. A texture from
    /// elsewhere is stored under the name the world uses, which a model may give an image of another name. Each
    /// texture's edge mode (clamp word) comes from the glTF samplers that use it.
    /// </summary>
    private static (List<(string Input, string Name)> Inputs, IReadOnlyDictionary<string, int> Addressing, List<string> Warnings) PackInputs(SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        List<(string Input, string Name)> inputs = [.. plan.Inputs.Select(input => (input, TextureName(input)))];
        List<string> warnings = [];
        string mission = plan.Path.Split('/')[0];
        if (!snapshot.HasWorld(mission)) return (inputs, new Dictionary<string, int>(), warnings);
        try
        {
            var world = snapshot.World(mission, token);
            HashSet<string> names = new(inputs.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, file) in world.TextureFiles.OrderBy(t => t.Key, StringComparer.Ordinal))
                if (names.Add(name)) inputs.Add((file, name.ToLowerInvariant()));
            return (inputs, world.TextureAddressing, warnings);
        }
        catch (InvalidDataException ex) { warnings.Add($"{ex.Message} The pack holds only the mission's texture folders, without edge modes."); }
        return (inputs, new Dictionary<string, int>(), warnings);
    }

    /// <summary>
    /// A mission's animations (see <see cref="Animation.AnimationCompiler"/>), bound to the world this export builds:
    /// definitions whose root the world lacks are left out, and names the game could not resolve (world nodes, and
    /// effect templates that effects.zrd does not define) are reported.
    /// </summary>
    private static Built BuildAnimations(string root, SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        string mission = plan.Path.Split('/')[0];
        List<string> warnings = [];
        IReadOnlyCollection<string>? nodes = null;
        if (snapshot.HasWorld(mission)) nodes = snapshot.World(mission, token).World.Nodes.Select(n => n.Name).ToArray();
        else warnings.Add($"{mission} has no world script ({WorldScript(mission)}), so animation roots and node names are not checked and patterns do not expand.");
        // Without an effects.zrd in the project the game's own resource archives supply it, so nothing can be checked.
        var effects = EffectNames(root, mission, snapshot, token);
        var result = Animation.AnimationCompiler.Compile(snapshot.Files(), AnimationRoot(mission), nodes, effects, token);
        return new(result.Bytes, result.Package.Entries.Count - 1, [.. warnings, .. result.Warnings]);
    }
    /// <summary>
    /// The effect templates the game can find for <paramref name="mission"/>: it loads effects.zrd by name from the
    /// resource archives, which hold the zrdr folders under data/common and the mission's. Names from every such file
    /// count, so only an effect none of them defines is reported. Null when there is no effects.zrd.
    /// </summary>
    private static HashSet<string>? EffectNames(string root, string mission, Snapshot snapshot, CancellationToken token)
    {
        static bool Effects(string name) => name.Equals("effects.zrd", StringComparison.OrdinalIgnoreCase);
        var sources = SourceProject.Files(root, "data/common", Effects, snapshot.Added).Where(p => p.Split('/').Contains("zrdr", StringComparer.OrdinalIgnoreCase))
            .Concat(SourceProject.Files(root, $"data/{mission}/zrdr", Effects, snapshot.Added)).ToArray();
        if (sources.Length == 0) return null;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string source in sources) names.UnionWith(Animation.AnimationCompiler.EffectNames(Animation.AnimationDefinitionSet.Read(snapshot.Files(), source, token)));
        return names;
    }

    /// <summary>The mission world, built by its script from the model sources (see <see cref="WorldAssembler"/>).</summary>
    private static Built BuildWorld(SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        var assembled = snapshot.World(plan.Path.Split('/')[0], token);
        return new(GameZWriter.Write(assembled.World, token), assembled.World.Nodes.Count, assembled.Warnings);
    }
    /// <summary>Damage-mark masks (support\weapons.gw WriteTextureSetMap), which must stay unpaletted.</summary>
    private static readonly HashSet<string> DamageMasks = new(["pock1", "pock2", "pock3"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A texture is named by its file name without the extension, as the engine looks textures up by name.</summary>
    internal static string TextureName(string input)
    {
        string name = Path.GetFileNameWithoutExtension(input).ToLowerInvariant();
        if (name.Length is < 1 or > 31 || name.Any(c => c > 255)) throw new InvalidDataException($"{input}: texture names need 1–31 Latin-1 characters.");
        return name;
    }
    internal static DecodedImage DecodeTexture(string input, byte[] bytes, CancellationToken token)
    {
        try { return Export.PngDecoder.Decode(bytes, 4096, token); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{input}: {ex.Message}", ex); }
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
