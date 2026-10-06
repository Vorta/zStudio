using System.Text;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A game file that the source tree can build, and the source files it is built from.</summary>
public sealed record SourceOutputPlan(string Path, string Family, IReadOnlyList<string> Inputs)
{
    /// <summary>For a texture pack, the budget and largest texture side its profile builds it with.</summary>
    public Formats.TexturePackVariant? Pack { get; init; }
    /// <summary>Whether the pack is the profile's automatic one, named for what its textures need (see <see cref="BuildProfiles.AutomaticPack"/>).</summary>
    public bool Automatic { get; init; }
    /// <summary>What planning found about the output, reported with its build (an automatic pack too small for its textures).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
/// <summary>Per-output result: built (and, for an export, written) or failed.</summary>
public sealed record SourceExportResult(string Path, string Family, string Status, long Bytes, int Items, IReadOnlyList<string> Warnings, string? Error = null);
public sealed record SourceExportReport(string? Destination, IReadOnlyList<SourceExportResult> Outputs)
{
    /// <summary>The build profile the outputs were built with.</summary>
    public string Profile { get; init; } = BuildProfiles.Modern.Name;
    /// <summary>Findings about the destination as a whole, such as larger texture packs there that the game would prefer.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>
    /// The lookups by name the built missions make as the game loads them whose name several nodes share, with the node each
    /// finds (see <see cref="WorldLookups"/>).
    /// </summary>
    public IReadOnlyList<SourceLookup> Lookups { get; init; } = [];
    /// <summary>Lookups that find another node than in the files this export replaced (also in <see cref="Notes"/>).</summary>
    public IReadOnlyList<SourceLookupChange> LookupChanges { get; init; } = [];
    public int Built => Outputs.Count(o => o.Status == "built");
    public int Failed => Outputs.Count(o => o.Status == "failed");
}

/// <summary>
/// Builds game files from a source tree, as the original gamegen build did: resource archives from each <c>zrdr</c> folder,
/// prepared scripts from <c>gamegen</c>, the three sound banks from the best-quality WAVs converted to the formats
/// that <c>sounds.zrd</c> declares, interface images and mission texture packs from PNGs, and each mission world by
/// running its build script (<c>gamegen/mN.gs</c>) over the glTF model sources, and each mission's animations from
/// their definitions (<c>data/mN/zrdr/anim.zad</c>) and keyframe scripts against that world. Output must work in the
/// game; it does not reproduce the shipped bytes.
/// </summary>
public static partial class SourceBuilder
{
    public const string SoundsFolder = "data/common/sounds", SoundDefinitions = "data/common/zrdr/sounds.zrd";
    /// <summary>The HIGH, MED and LOW sound banks, in the order of their sounds.zrd declarations.</summary>
    internal static readonly string[] Banks = ["soundsh.zbd", "soundsm.zbd", "soundsl.zbd"];
    /// <summary>The mission texture packs the default (modern) profile builds; see <see cref="BuildProfiles"/>.</summary>
    public static IReadOnlyList<string> TexturePacks => BuildProfiles.Modern.TexturePacks.Select(p => p.File).ToArray();

    /// <summary>
    /// Every game file this tree can build, in a stable order; <paramref name="added"/> are pending new files (see
    /// <see cref="SourceWorkspace"/>). Texture packs follow <paramref name="profile"/> (the built-in modern one when null).
    /// Without <paramref name="automaticPacks"/> the automatic packs are left out: naming them reads every mission texture's
    /// PNG header, which listing the worlds or building one for a preview does not need. <paramref name="token"/> is
    /// observed while the project's folders are scanned (each scan visits at most <see cref="SourceProject.MaximumScannedEntries"/>).
    /// </summary>
    public static IReadOnlyList<SourceOutputPlan> Plan(string root, IReadOnlyCollection<string>? added = null, BuildProfile? profile = null, bool automaticPacks = true, CancellationToken token = default)
    {
        profile ??= BuildProfiles.Modern;
        if (!SourceProject.IsProject(root)) throw new InvalidDataException("This folder is not a source project (it needs data and gamegen folders).");
        List<SourceOutputPlan> plans = [];
        static bool Zrd(string name) => name.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase);
        // Common resources come from every zrdr folder under data/common (including multi_bft/zrdr).
        var common = SourceProject.Files(root, "data/common", Zrd, added, token).Where(p => p.Split('/').Contains("zrdr", StringComparer.OrdinalIgnoreCase)).ToArray();
        if (common.Length > 0) plans.Add(new("zrdr.zbd", "archive", common));
        var scripts = SourceProject.Files(root, SourceProject.GameGenFolder, n => n.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".gw", StringComparison.OrdinalIgnoreCase), added, token);
        if (scripts.Count > 0) plans.Add(new("interp.zbd", "scripts", scripts));
        var sounds = SourceProject.Files(root, SoundsFolder, n => n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase), added, token);
        if (sounds.Count > 0) plans.AddRange(Banks.Select(bank => new SourceOutputPlan(bank, "sounds", sounds)));
        // Listing data for its mission folders is a scan too: every entry counts, and the token is observed at each.
        var missions = SourceProject.MissionFolders(root, token);
        // A world may load any model in the project, and animations any keyframe script; which depends on the sources.
        IReadOnlyList<string>? models = null, scriptsFound = null;
        // Interface images: fonts, the images tree and each mission's objective images.
        var images = SourceProject.Files(root, TextureSources.Fonts, Png, added, token).Concat(SourceProject.Files(root, TextureSources.Images, Png, added, token))
            .Concat(missions.SelectMany(m => SourceProject.Files(root, $"data/{m}/images", Png, added, token))).ToArray();
        if (images.Length > 0) plans.Add(new("image.zbd", "images", images));
        foreach (var mission in missions)
        {
            token.ThrowIfCancellationRequested();
            string name = mission.ToLowerInvariant();
            string entry = WorldScript(name);
            if (File.Exists(SourceProject.Resolve(root, entry)) || added?.Contains(entry, StringComparer.OrdinalIgnoreCase) == true)
            {
                models ??= SourceProject.Files(root, SourceProject.DataFolder, IsModelSource, added, token);
                // A project without glTF models has no world to build (buffers alone are not models).
                if (models.Any(m => !m.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))) plans.Add(new($"{name}/gamez.zbd", "world", [entry, .. models]));
            }
            var resources = SourceProject.Files(root, $"data/{name}/zrdr", Zrd, added, token);
            if (resources.Count > 0) plans.Add(new($"{name}/zrdr.zbd", "archive", resources));
            string definitions = AnimationRoot(name);
            if (File.Exists(SourceProject.Resolve(root, definitions)) || added?.Contains(definitions, StringComparer.OrdinalIgnoreCase) == true)
            {
                scriptsFound ??= SourceProject.Files(root, SourceProject.DataFolder, n => n.EndsWith(Animation.AnimationScript.Extension, StringComparison.OrdinalIgnoreCase), added, token);
                plans.Add(new($"{name}/anim.zbd", "animations", [definitions, .. scriptsFound]));
            }
            var textures = MissionTextures(root, name, added, token);
            if (textures.Count > 0)
                foreach (var pack in profile.TexturePacks.Where(pack => pack.Builds(name)))
                {
                    if (!pack.Automatic) { plans.Add(new($"{name}/{pack.File}", "textures", textures) { Pack = pack.Variant }); continue; }
                    if (!automaticPacks) continue;
                    // The automatic pack is named for the texture memory the mission's textures need at full size.
                    long memory = TextureMemory(root, textures, pack.Variant, token);
                    if (BuildProfiles.AutomaticPack(profile, name, memory) is not { BudgetBytes: long budget } automatic) continue;
                    plans.Add(new($"{name}/{automatic.FileName}", "textures", textures)
                    {
                        Pack = automatic, Automatic = true,
                        Notes = memory > budget
                            ? [$"The {name} textures need {(memory + (1 << 20) - 1) >> 20} MB of texture memory at full size, more than the {budget >> 20} MB the profile's automatic rtexture pack may hold; {automatic.FileName} holds them reduced to fit."] : [],
                    });
                }
        }
        return plans;
        static bool Png(string name) => name.EndsWith(TextureSources.Extension, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>The script that builds a mission's world (gamegen/mN.gs).</summary>
    internal static string WorldScript(string mission) => $"{SourceProject.GameGenFolder}/{mission}.gs";
    /// <summary>The root of a mission's animation definitions.</summary>
    internal static string AnimationRoot(string mission) => $"data/{mission}/zrdr/anim{Animation.AnimationDefinitionSet.Extension}";
    private static bool IsModelSource(string name) => name.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every project file read by one run. A file read by several outputs must have the same content each time, and no
    /// file may change before the run publishes, so an export always corresponds to one state of the project.
    /// <paramref name="changed"/> makes the error for a file that changed during the run (another run than an export, such
    /// as a Blender checkout, says what to do again); by default it is an export's.
    /// </summary>
    internal sealed class Snapshot(string root, IReadOnlyDictionary<string, byte[]>? overlay = null, Func<string, Exception>? changed = null,
        long maximumRetainedBytes = SourceExtractor.MaximumRetainedBytes)
    {
        internal long RetainedBytes { get; private set; }
        /// <summary>Worlds and compiled animation packages share the run's retained-data limit, across every output.</summary>
        internal void Retain(long bytes)
        {
            if (bytes < 0 || bytes > maximumRetainedBytes - RetainedBytes)
                throw new InvalidDataException($"The export's retained worlds and animations exceed {maximumRetainedBytes / (1024 * 1024):N0} MiB; export fewer missions together.");
            RetainedBytes += bytes;
        }
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
        internal byte[] Read(string relative, CancellationToken token, long maximum = FormatRegistry.MaximumDocumentBytes)
        {
            token.ThrowIfCancellationRequested();
            Depend(relative);
            if (overlay?.TryGetValue(relative, out var pending) == true)
            {
                if (pending.LongLength > maximum) throw new InvalidDataException($"{JsonData.ShownText(relative)} exceeds {maximum:N0} bytes.");
                return pending;
            }
            string path = SourceProject.Resolve(root, relative);
            var stamp = FileStamp.Read(path);
            if (stamp.Length > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException($"{relative} exceeds 512 MiB.");
            byte[] bytes = SourceRead.All(path, maximum, token); string sha = SourceProject.Sha256(bytes);
            if (FileStamp.Read(path) != stamp) throw Changed(relative, $"{relative} changed while it was read; export again.");
            if (files.TryGetValue(relative, out var first) && (first.Sha != sha || first.Stamp != stamp)) throw Changed(relative, $"{relative} changed while exporting; export again.");
            files[relative] = (sha, stamp); return bytes;
        }
        /// <summary>The error for a project file that changed during the run: the run's own (see the constructor), else an export's <paramref name="message"/>.</summary>
        internal Exception Changed(string relative, string message) => changed?.Invoke(relative) ?? new InvalidDataException(message);
        /// <summary>
        /// A PNG's size from its header, without reading the image: what decoding it will take, known before anything is
        /// decoded (null when it has no readable header; decoding reports why).
        /// </summary>
        internal (int Width, int Height)? PngSize(string relative, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Depend(relative);
            if (overlay?.TryGetValue(relative, out var pending) == true) return TextureSources.PngSize((ReadOnlySpan<byte>)pending);
            return TextureSources.PngSize(SourceProject.Resolve(root, relative), cache: false);
        }
        private readonly Dictionary<string, (int Width, int Height, TextureTransparency Transparency)> textures = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// What decoding a PNG of the run found: its size and transparency. A file's content is the same for the whole run
        /// (<see cref="Read"/>), so a texture several packs hold is classified once.
        /// </summary>
        internal (int Width, int Height, TextureTransparency Transparency)? Texture(string relative) => textures.TryGetValue(relative, out var found) ? found : null;
        internal void Remember(string relative, int width, int height, TextureTransparency transparency) => textures[relative] = (width, height, transparency);
        private HashSet<string>? damageMasks;
        /// <summary>Textures the scripts register as damage-mark masks (WriteTextureSetMap), read once per run.</summary>
        internal HashSet<string> DamageMasks(CancellationToken token)
        {
            if (damageMasks != null) return damageMasks;
            damageMasks = new(StringComparer.OrdinalIgnoreCase);
            foreach (string script in SourceProject.Files(root, SourceProject.GameGenFolder, n => n.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".gw", StringComparison.OrdinalIgnoreCase), Added, token))
                foreach (var line in GameGenScriptText.Tokenize(GameGenScriptText.Decode(Read(script, token))))
                    if (line.Count > 1 && ScriptCommands.Core(line[0]) == "WriteTextureSetMap") damageMasks.Add(Path.GetFileNameWithoutExtension(line[1]));
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
                    // Include per-node provenance and its applied instruction references, beside decoded geometry.
                    Retain(SourceExtractor.Footprint(world) + 512L * assembler.Provenance.Count + 256L * assembler.Executions.Count
                        + assembler.Provenance.Values.Sum(p => 128L * p.Applied.Count));
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
                if (!File.Exists(path) || FileStamp.Read(path) != entry.Stamp) throw Changed(relative, $"{relative} changed while exporting; nothing was written.");
            }
        }
    }
    internal sealed record Built(byte[] Bytes, int Items, IReadOnlyList<string> Warnings)
    {
        /// <summary>For a mission's animations, the compiled package, which the lookup report binds.</summary>
        public Animation.AnimationPackage? Package { get; init; }
    }
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
        // The run's view of the project starts before planning: the profile is read through it, so a change to its file is
        // refused like a changed source, and the plan is made again before publishing (see CheckPlanUnchanged).
        Snapshot snapshot = new(root);
        var profile = await Task.Run(() => FindProfile(root, profileName, snapshot, token), token);
        var all = await Task.Run(() => Plan(root, null, profile, token: token), token);
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
            List<SourceExportResult> results = [];
            // The digest of each output's bytes as they were built and reopened, which its staged file must still have when it is installed.
            Dictionary<string, JournalDigest> contents = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Animation.AnimationPackage> packages = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < selected.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var plan = selected[i]; progress?.Report(new(i, selected.Count, plan.Path));
                try
                {
                    var built = await Task.Run(() => Build(root, plan, snapshot, token), token);
                    if (built.Package != null) snapshot.Retain(4L * built.Bytes.LongLength);
                    // Every output must reopen through the shared readers before it can be written.
                    var check = FormatRegistry.Default.OpenBytes(plan.Path, built.Bytes, token: token);
                    if (check.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException("The built file does not reopen: " + error.Message);
                    if (staging != null)
                    {
                        contents[plan.Path] = JournalDigest.OfContent(built.Bytes);
                        string path = SourceProject.Resolve(staging, plan.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, built.Bytes, token);
                    }
                    results.Add(new(plan.Path, plan.Family, "built", built.Bytes.Length, built.Items, built.Warnings));
                    if (built.Package != null) packages[plan.Path.Split('/')[0]] = built.Package;
                }
                catch (Exception ex) when (IsBuildFailure(ex))
                { results.Add(new(plan.Path, plan.Family, "failed", 0, 0, [], ex.Message)); }
            }
            // Before anything is replaced: what the built missions look up by name, and what the replaced files found.
            progress?.Report(new(selected.Count, selected.Count, "Checking lookups by name"));
            var (lookups, changes, notChecked) = await Task.Run(() => MissionLookups(results, packages, snapshot, destination, token), token);
            // The game opens the largest hardware pack its texture memory allows, so one left from another export would win.
            // These notes come first: results show only the first notes, and many lookup changes must not hide them. Only a
            // report, read before publishing: a destination that cannot be listed must not turn a written export into a failure.
            List<string> notes = [];
            if (destination != null)
                try
                {
                    foreach (string mission in results.Where(r => r.Family == "textures").Select(r => r.Path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
                        foreach (string pack in BuildProfiles.ShadowingPacks(destination, mission, all.Where(p => p.Family == "textures" && p.Path.StartsWith(mission + "/", StringComparison.OrdinalIgnoreCase)).Select(p => p.Path[(mission.Length + 1)..]).ToArray(), token))
                            notes.Add($"{pack} is not a pack the {profile.Name} profile builds, but the game may load it instead of the exported ones. Delete it, or export with a profile that builds a pack of that name.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add($"The destination's texture packs could not be listed ({ex.Message}); a pack left there by another export may be loaded instead of the exported ones."); }
            progress?.Report(new(selected.Count, selected.Count, destination == null ? "Checked" : "Publishing"));
            await Task.Run(() => CheckPlanUnchanged(root, profileName, profile, outputs == null ? null : selected, all, snapshot, token), token);
            snapshot.CheckUnchanged(token);
            if (staging != null && destination != null)
            {
                if (results.Any(r => r.Status == "failed")) throw new InvalidDataException("Nothing was written because some outputs failed: " + string.Join("; ", results.Where(r => r.Status == "failed").Select(r => $"{r.Path}: {r.Error}")));
                Publish(staging, destination, [.. results.Select(r => (r.Path, contents[r.Path]))], overwrite, token);
            }
            notes.AddRange(notChecked);
            notes.AddRange(changes.Select(c => WorldLookups.Describe(c, " in the files this export replaced")));
            return new(destination, results) { Profile = profile.Name, Notes = notes, Lookups = lookups, LookupChanges = changes };
        }
        // A staging folder another program holds must not replace the export's own result or error.
        finally { try { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>The build profile (the project's default when <paramref name="name"/> is null), its files read through the run's snapshot.</summary>
    private static BuildProfile FindProfile(string root, string? name, Snapshot snapshot, CancellationToken token) => BuildProfiles.Find(root, name, path =>
    {
        // As a profile is read without a snapshot: a file over 64 KB is refused before it is read.
        if (new FileInfo(SourceProject.Resolve(root, path)).Length > BuildProfiles.MaximumFileBytes) throw new InvalidDataException($"{path} is larger than 64 KB.");
        return snapshot.Read(path, token, BuildProfiles.MaximumFileBytes);
    });
    /// <summary>
    /// Refuses outputs whose plan the project no longer gives, because it changed after it was planned: the plan is made again,
    /// as the run made it, and must name the same profile and give each built output the same inputs, pack and notes (with no outputs
    /// selected, the same outputs, so one added meanwhile is not left out). Planning lists the source folders and reads the
    /// multiplayer load scripts and the mission textures' PNG headers, which the run's snapshot does not hold; the profile
    /// files it reads are in the snapshot, so a changed one fails here or in <see cref="Snapshot.CheckUnchanged"/>.
    /// </summary>
    private static void CheckPlanUnchanged(string root, string? profileName, BuildProfile profile, IReadOnlyList<SourceOutputPlan>? selected, IReadOnlyList<SourceOutputPlan> planned, Snapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BuildProfile again; IReadOnlyList<SourceOutputPlan> now;
        try { again = FindProfile(root, profileName, snapshot, token); now = Plan(root, null, again, token: token); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { throw new InvalidDataException($"The project changed while exporting; nothing was written. Export again. ({ex.Message})", ex); }
        bool same = again.Name == profile.Name && again.Source == profile.Source && (selected == null
            ? now.Count == planned.Count && planned.Zip(now).All(p => SamePlan(p.First, p.Second))
            : selected.All(p => now.FirstOrDefault(n => n.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase)) is { } n && SamePlan(p, n)));
        if (!same) throw new InvalidDataException("The project's sources changed while exporting (files were added, removed or renamed, a texture changed size, or the build profile changed); nothing was written. Export again.");
    }
    /// <summary>Whether two plans of an output build it the same way: the same path, family, inputs (in order), pack and notes.</summary>
    internal static bool SamePlan(SourceOutputPlan a, SourceOutputPlan b) => a.Path == b.Path && a.Family == b.Family && a.Pack == b.Pack && a.Automatic == b.Automatic
        && a.Inputs.SequenceEqual(b.Inputs, StringComparer.Ordinal) && a.Notes.SequenceEqual(b.Notes, StringComparer.Ordinal);

    /// <summary>
    /// Every lookup by name a mission makes as the game loads it (see <see cref="WorldLookups.Resolve"/>), against
    /// <paramref name="world"/> (by default the world this run builds) and <paramref name="animations"/> (texture effects
    /// only without them).
    /// </summary>
    internal static IReadOnlyList<SourceLookup> MissionLookups(string mission, Snapshot snapshot, Animation.AnimationPackage? animations, CancellationToken token, GameZWorld? world = null)
    {
        var findNodes = FindNodes(mission, snapshot, token);
        return WorldLookups.Resolve(mission, world ?? snapshot.World(mission, token).World, animations, findNodes, token);
    }
    /// <summary>The names the scripts the game runs as it loads <paramref name="mission"/> look up (<see cref="WorldLookups.FindNodes"/>), read through the run's snapshot.</summary>
    private static IReadOnlyList<(string Source, string Name)> FindNodes(string mission, Snapshot snapshot, CancellationToken token)
    {
        var files = snapshot.Files();
        return WorldLookups.FindNodes(path => files.Exists(path) ? files.Read(path, token) : null, mission, token);
    }
    /// <summary>Why the lookups by name of a mission could not be checked, as a report says it.</summary>
    internal static string LookupsUnchecked(string mission, Exception ex) =>
        $"The lookups by name the game makes as it loads {mission} were not checked, so none of them is reported: {ex.Message}";
    /// <summary>
    /// The lookups by name of each mission whose world or animations were built, for the report those several nodes share,
    /// and those that find another node than in the destination's files this export replaces (read before they are replaced).
    /// The game binds a built world with the destination's animations when this export leaves them, and built animations
    /// with the destination's world likewise, so those are resolved together.
    /// </summary>
    private static (List<SourceLookup> Lookups, List<SourceLookupChange> Changes, List<string> NotChecked) MissionLookups(IReadOnlyList<SourceExportResult> results, IReadOnlyDictionary<string, Animation.AnimationPackage> packages,
        Snapshot snapshot, string? destination, CancellationToken token)
    {
        List<SourceLookup> lookups = []; List<SourceLookupChange> changes = []; List<string> notChecked = [];
        foreach (string mission in results.Where(r => r.Status == "built" && r.Family is "world" or "animations").Select(r => r.Path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!snapshot.HasWorld(mission)) continue;
            bool worldBuilt = results.Any(r => r.Status == "built" && r.Family == "world" && r.Path.Equals($"{mission}/gamez.zbd", StringComparison.OrdinalIgnoreCase));
            (GameZWorld World, Animation.AnimationPackage? Animations)? previous;
            IReadOnlyList<SourceLookup> resolved; GameZWorld after; IReadOnlyList<(string Source, string Name)> findNodes;
            // Only a report: nothing it reads or resolves may fail the export, but lookups it could not check are not passed
            // over in silence (a script it cannot follow may look up a node the export moves).
            try
            {
                previous = destination == null ? null : Replaced(destination, mission, token);
                after = worldBuilt || previous == null ? snapshot.World(mission, token).World : previous.Value.World;
                findNodes = FindNodes(mission, snapshot, token);
                resolved = WorldLookups.Resolve(mission, after, packages.GetValueOrDefault(mission) ?? previous?.Animations, findNodes, token);
            }
            catch (Exception ex) when (IsBuildFailure(ex)) { notChecked.Add(LookupsUnchecked(mission, ex)); continue; }
            lookups.AddRange(resolved.Where(l => l.Ambiguous));
            // Only a report: files that cannot be paired give no changes rather than failing the export.
            if (previous is { } replaced)
                try
                {
                    var before = WorldLookups.Resolve(mission, replaced.World, replaced.Animations, findNodes, token);
                    changes.AddRange(WorldLookups.Changes(replaced.World, before, after, resolved, token));
                }
                catch (Exception ex) when (IsBuildFailure(ex)) { }
        }
        return (lookups, changes, notChecked);
    }
    /// <summary>The destination's world and animations of a mission, when it holds a readable world (version 13 or 15).</summary>
    private static (GameZWorld World, Animation.AnimationPackage? Animations)? Replaced(string destination, string mission, CancellationToken token)
    {
        string world = Path.Combine(destination, mission, "gamez.zbd"), animations = Path.Combine(destination, mission, "anim.zbd");
        try
        {
            if (!File.Exists(world) || new FileInfo(world).Length > FormatRegistry.MaximumDocumentBytes) return null;
            var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", SourceRead.All(world, FormatRegistry.MaximumDocumentBytes, token), token: token);
            if (doc.Probe is not { Family: FormatFamily.GameZ, Version: 13 or 15 }) return null;
            Animation.AnimationPackage? package = null;
            if (File.Exists(animations) && new FileInfo(animations).Length <= FormatRegistry.MaximumDocumentBytes)
                try { package = Animation.AnimationPackage.Read(SourceRead.All(animations, FormatRegistry.MaximumDocumentBytes, token), token); } catch (InvalidDataException) { }
            return (GameZWorldReader.FromDocument(doc, token), package);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Move staged outputs into place; replaced files move aside first and are restored if any later step fails. Without
    /// <paramref name="overwrite"/>, a game file that appeared while the outputs were built is never replaced. Each output's
    /// <c>Content</c> is the digest of the bytes that were built and reopened. Before anything is replaced every staged file
    /// is checked against it and held, so no other program can write or rename it, until it is in place (see
    /// <see cref="SealedFile"/>): a staged file changed after its output was built fails the export before it replaces
    /// anything. <paramref name="fault"/> is a test hook called with an output's index before its staged file is checked
    /// ("check"), before its original is moved aside ("replace") and before it is installed ("install"); it may throw to
    /// simulate a failure.
    /// </summary>
    internal static void Publish(string staging, string destination, IReadOnlyList<(string Relative, JournalDigest Content)> outputs, bool overwrite, CancellationToken token, Action<string, int>? fault = null)
    {
        foreach (var (relative, _) in outputs) { _ = SourceProject.Resolve(destination, relative); SourceProject.RejectNestedLinks(destination, relative); }
        string backup = Path.Combine(destination, ".zstudio-backup-" + Guid.NewGuid().ToString("N"));
        // Original: the digest of the replaced file moved into the backup, so undoing restores it only while the backup still
        // holds it. Installed: the content this export moved into place, so undoing it removes only that file.
        List<(string Target, string? Saved, JournalDigest? Original, JournalDigest? Installed)> steps = [];
        // Each staged output from its check until it is in place: what is installed is what was built and reopened.
        var held = new SealedFile?[outputs.Count];
        try
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (relative, content) = outputs[i];
                fault?.Invoke("check", i);
                try { held[i] = SealedFile.Open(SourceProject.Resolve(staging, relative), content); }
                catch (IOException ex) { throw new IOException($"The built {relative} was not installed: {ex.Message}", ex); }
            }
            for (int i = 0; i < outputs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (relative, content) = outputs[i];
                string target = SourceProject.Resolve(destination, relative), saved = SourceProject.Resolve(backup, relative);
                fault?.Invoke("replace", i);
                if (File.Exists(target) && !overwrite) throw new IOException($"{relative} appeared in {destination} during the export; nothing was replaced. Export again and allow replacing it.");
                if (File.Exists(target))
                {
                    JournalDigest original;
                    using (FileStream source = new(target, FileMode.Open, FileAccess.Read, FileShare.Read)) original = JournalDigest.Of(source, token);
                    // Verify again through a held handle before moving: the original cannot change between its digest and rename.
                    using SealedFile originalFile = SealedFile.Open(target, original);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!); originalFile.MoveTo(saved);
                    steps.Add((target, saved, original, null));
                }
                else steps.Add((target, null, null, null));
                fault?.Invoke("install", i);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                held[i]!.MoveTo(target);
                held[i]!.Dispose(); held[i] = null;
                steps[^1] = steps[^1] with { Installed = content };
            }
        }
        catch (Exception failure)
        {
            // Released first: undoing moves installed files, which a held file would refuse.
            foreach (var file in held) file?.Dispose();
            // Only files this export installed are removed, while they still have the content it installed: a file another
            // program put at a target meanwhile stays, and the original that moved aside for it stays in the backup. An
            // installed file is removed only while the backup still holds the original that replaces it, so a backup another
            // program changed never leaves a game file missing.
            List<string> unrestored = [], leftover = [], others = [], stranded = [], kept = [], lost = [];
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                var (target, saved, original, installed) = steps[i];
                try
                {
                    // Keep the original sealed through removal of the installed output and restoration of its name.
                    using SealedFile? restore = saved != null ? SealedFile.Open(saved, original!) : null;
                    if (installed != null)
                    {
                        // Moved into the backup and deleted there only while it is the file this export installed.
                        string taken = Path.Combine(backup, ".removed", $"{i}-{Guid.NewGuid():N}.bin");
                        switch (SourcePublisher.MoveIfContent(target, taken, installed))
                        {
                            case SourcePublisher.Moved.Done:
                                try { File.Delete(taken); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                                break;
                            case SourcePublisher.Moved.Stranded:
                                stranded.Add(taken); if (saved != null) unrestored.Add(target);
                                continue;
                            default:
                                if (Path.Exists(target)) { (saved != null ? unrestored : others).Add(target); continue; }
                                break;
                        }
                    }
                    if (saved != null) { if (Path.Exists(target)) unrestored.Add(target); else restore!.MoveTo(target); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { (saved == null ? leftover : Holds(saved, original) ? unrestored : installed != null && Path.Exists(target) ? kept : lost).Add(target); }
            }
            string notes = (leftover.Count > 0 ? $". New files that could not be removed: {string.Join(", ", leftover.Take(8))}" : "")
                + (others.Count > 0 ? $". Files another program wrote at the outputs' names during the export were left as they are: {string.Join(", ", others.Take(8))}" : "")
                + (stranded.Count > 0 ? $". Files another program put in place while the export was undone were kept as {string.Join(", ", stranded.Take(8))}" : "")
                + (kept.Count > 0 ? $". The originals of {kept.Count} replaced files are no longer in {backup}, which another program changed, so the files at their names were left as they are: {string.Join(", ", kept.Take(8))}" : "")
                + (lost.Count > 0 ? $". The originals of {lost.Count} replaced files could not be restored because they are no longer in {backup}, which another program changed: {string.Join(", ", lost.Take(8))}" : "");
            // A canceled export stays a cancellation, with what it could not undo.
            string failed = failure is OperationCanceledException ? "The export was canceled" : $"Export failed ({failure.Message})";
            Exception Failure(string message) => failure is OperationCanceledException ? new OperationCanceledException(message, failure, token) : new IOException(message, failure);
            if (unrestored.Count > 0 || stranded.Count > 0)
                throw Failure(failed + (unrestored.Count > 0
                    ? $" and {unrestored.Count} previous files could not be restored because another program changed or holds them; the originals remain in {backup}: {string.Join(", ", unrestored.Take(8))}"
                    : $"; {backup} is kept") + notes);
            // A backup another program changed is left to it.
            if (kept.Count == 0 && lost.Count == 0)
                try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (notes.Length > 0) throw Failure(failed + notes);
            throw;
        }
        try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // Whether the backup still holds a replaced file as it was moved there.
        static bool Holds(string saved, JournalDigest? original)
        {
            try { if (original == null) return false; using var file = SealedFile.Open(saved, original); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    internal static Built Build(string root, SourceOutputPlan plan, Snapshot snapshot, CancellationToken token) => plan.Family switch
    {
        "archive" => BuildArchive(plan, snapshot, token),
        "scripts" => BuildScripts(root, plan, snapshot, token),
        "sounds" => BuildSounds(root, plan, snapshot, token),
        "images" => BuildImages(plan, snapshot, token),
        "textures" => BuildTexturePack(plan, snapshot, token),
        "world" => BuildWorld(plan, snapshot, token),
        "animations" => BuildAnimations(root, plan, snapshot, token),
        _ => throw new InvalidDataException($"Unknown output family '{plan.Family}'.")
    };

    /// <summary>
    /// The source-path field carries the project-relative source (<c>data\m1\zrdr\ai.zrd</c>), which placement edits of a
    /// built world write back to (see <see cref="SourceResourceEdits"/>).
    /// </summary>
    internal static string SourceField(string relative) => relative.Replace('/', '\\');

    private static Built BuildArchive(SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase); List<ArchiveSources.Entry> entries = [];
        long retained = 8L + plan.Inputs.Count * ArchiveSources.RecordSize;
        FormatRegistry.ValidateDocumentSize(retained);
        // Members are ordered by source path. The engine scans members for the first case-insensitive name match
        // (zIndexArchive::FindRecordByNameCI, retail 0x4A65D0), so order is free but names must be unique.
        foreach (string input in plan.Inputs.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested(); string name = Path.GetFileName(input);
            if (names.TryGetValue(name, out string? other)) throw new InvalidDataException($"{other} and {input} would both become archive member {name}; the engine finds members by name.");
            names[name] = input;
            // The member records its whole source path, which edits made in a built world are written back to.
            string field = SourceField(input);
            if (!ArchiveSources.FitsSourceField(field))
                throw new InvalidDataException($"{input}: an archive member records its source path in at most {ArchiveSources.MaximumSourceField} Latin-1 characters, and edits made in a built world find the source by it; "
                    + (field.Length > ArchiveSources.MaximumSourceField ? $"this path has {field.Length}. Move the file to a folder with a shorter path." : "this path has characters outside Latin-1. Rename the file or its folders."));
            byte[] bytes = snapshot.Read(input, token);
            byte[] payload;
            try
            {
                var tree = ZrdText.LooksLikeText(bytes) ? ZrdText.Parse(bytes, token) : ZrdDecoder.Read(bytes, token);
                // Animation definitions are compiled into anim.zbd; the game never reads them from an archive.
                if (Animation.AnimationDefinitionSet.HoldsDefinitions(tree))
                    throw new InvalidDataException($"it holds animation definitions (ANIMATION_DEFINITIONS), which belong in a {Animation.AnimationDefinitionSet.Extension} file beside it; a project reconstructed before zStudio kept definitions in {Animation.AnimationDefinitionSet.Extension} files must be reconstructed again.");
                payload = ZrdWriter.Write(tree, token);
            }
            catch (InvalidDataException ex) { throw new InvalidDataException($"{input}: {ex.Message}", ex); }
            FormatRegistry.ValidateDocumentSize(retained += payload.Length);
            entries.Add(new(name, field, payload));
        }
        return new(ArchiveSources.Write(entries), entries.Count, []);
    }

    internal static Built BuildScripts(string root, SourceOutputPlan plan, Snapshot snapshot, CancellationToken token,
        long maximumSourceBytes = 64 * 1024 * 1024, int maximumInstructions = GameGenScriptText.MaximumLines, int maximumTokens = GameGenScriptText.MaximumTokens)
    {
        List<PreparedScriptEntry> entries = [];
        long sourceBytes = 0, instructionCount = 0, tokenCount = 0;
        foreach (string input in plan.Inputs)
        {
            token.ThrowIfCancellationRequested();
            // Scripts are indexed by their path below the gamegen folder, e.g. support\common.gw.
            string name = input[(SourceProject.GameGenFolder.Length + 1)..].Replace('/', '\\');
            PreparedScriptWriter.ValidateName(name);
            byte[] source = snapshot.Read(input, token, Math.Min(SourceProject.MaximumSourceTextBytes, maximumSourceBytes - sourceBytes));
            sourceBytes += source.Length;
            var lines = GameGenScriptText.Tokenize(GameGenScriptText.Decode(source));
            if ((instructionCount += lines.Count) > maximumInstructions || (tokenCount += lines.Sum(l => (long)l.Count)) > maximumTokens)
                throw new InvalidDataException("The prepared scripts together exceed the instruction or token limit; split or simplify the sources.");
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
        CheckCount(plan.Path, plan.Inputs.Count);
        List<PackSource> textures = [];
        foreach (string input in plan.Inputs) textures.Add(Source(input, TextureName(input), 0, false, snapshot, token));
        var built = TexturePackBuilder.BuildFromSources(textures, TexturePackVariant.FromFileName("image.zbd")!, token);
        return new(built.Bytes, textures.Count, built.Warnings);
    }
    /// <summary>
    /// The textures a mission pack holds: every PNG in the folders the mission searches (support\common.gw), without
    /// the other campaign missions' vehicle folders. The first folder holding a name wins, as the engine takes the first
    /// match.
    /// </summary>
    internal static IReadOnlyList<string> MissionTextures(string root, string mission, IReadOnlyCollection<string>? added = null, CancellationToken token = default)
    {
        List<string> inputs = []; HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in TextureSources.MissionFolders(mission, Multiplayer(root, mission)))
            foreach (string file in SourceProject.Files(root, folder, n => n.EndsWith(TextureSources.Extension, StringComparison.OrdinalIgnoreCase), added, token))
                // Subfolders of a search folder are separate folders (bft is listed on its own).
                if (Path.GetDirectoryName(file)!.Replace('\\', '/').Equals(folder, StringComparison.OrdinalIgnoreCase) && names.Add(Path.GetFileNameWithoutExtension(file))) inputs.Add(file);
        return inputs;
    }
    /// <summary>
    /// The texture memory a mission's textures need at full size in a Direct3D pack: two bytes a texel at the sizes the pack
    /// stores (powers of two up to its largest side), from the PNG headers. Planning names the automatic pack from it; only
    /// the build knows the textures the world brings from other missions' folders and which textures have alpha planes,
    /// so it counts them and keeps the pack within its name (see <see cref="BuildTexturePack"/>). Files not yet on disk are
    /// not counted.
    /// </summary>
    internal static long TextureMemory(string root, IEnumerable<string> textures, TexturePackVariant variant, CancellationToken token = default)
    {
        long total = 0;
        foreach (string texture in textures)
        {
            token.ThrowIfCancellationRequested();
            if (TextureSources.PngSize(SourceProject.Resolve(root, texture)) is var (width, height))
            {
                var (w, h) = TexturePackBuilder.Normalize(width, height, variant);
                total += 2L * w * h;
            }
        }
        return total;
    }
    /// <summary>A mission is multiplayer when its load script sources the shared multiplayer vehicle.</summary>
    internal static bool Multiplayer(string root, string mission)
    {
        string script = SourceProject.Resolve(root, $"{SourceProject.GameGenFolder}/support/load{mission}.gw");
        if (!File.Exists(script) || new FileInfo(script).Length > SourceProject.MaximumSourceTextBytes) return false;
        return GameGenScriptText.Tokenize(GameGenScriptText.Decode(SourceRead.All(script, SourceProject.MaximumSourceTextBytes))).Any(l => l.Any(t => t.Equals("support\\bftmulti.gw", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A mission texture pack at the variant's budget. Direct colour in software packs is kept for the textures the
    /// engine requires unpaletted: the damage masks that WriteTextureSetMap stamps and the player-vehicle textures they
    /// stamp (ApplyDamageMaskStampOnHit, retail 0x479660).
    /// </summary>
    private static Built BuildTexturePack(SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        var variant = plan.Pack ?? TexturePackVariant.FromFileName(Path.GetFileName(plan.Path)) ?? throw new InvalidDataException($"{plan.Path} is not a texture pack name.");
        List<PackSource> textures = [];
        var (inputs, addressing, warnings) = PackInputs(plan, snapshot, token);
        CheckCount(plan.Path, inputs.Count);
        foreach (var (input, name) in inputs)
        {
            string folder = Path.GetDirectoryName(input)!.Replace('\\', '/');
            bool vehicle = folder.EndsWith("/bft", StringComparison.OrdinalIgnoreCase) || folder.Equals(TextureSources.MultiBftTextures, StringComparison.OrdinalIgnoreCase);
            textures.Add(Source(input, name, addressing.GetValueOrDefault(name), vehicle || snapshot.DamageMasks(token).Contains(name), snapshot, token));
        }
        var built = TexturePackBuilder.BuildFromSources(textures, variant, token);
        // The automatic pack was named from its folders' PNG headers. Counted as every pack's budget is, its textures (with
        // those the world brings from other folders, and alpha planes) may need more than its name holds: they are fitted to it.
        if (plan.Automatic && plan.Notes.Count == 0 && built.FullSizeBytes > variant.BudgetBytes)
            warnings.Add($"The {plan.Path.Split('/')[0]} pack's textures need {(built.FullSizeBytes + (1 << 20) - 1) >> 20} MB at full size, counting those its world brings from other folders and their alpha planes, more than {variant.FileName} holds; they were reduced to fit.");
        return new(built.Bytes, textures.Count, [.. plan.Notes, .. warnings, .. built.Warnings]);
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
        return new(result.Bytes, result.Package.Entries.Count - 1, [.. warnings, .. result.Warnings]) { Package = result.Package };
    }
    /// <summary>
    /// The effect templates the game can find for <paramref name="mission"/>: it loads effects.zrd by name from the
    /// resource archives, which hold the zrdr folders under data/common and the mission's. Names from every such file
    /// count, so only an effect none of them defines is reported. Null when there is no effects.zrd.
    /// </summary>
    private static HashSet<string>? EffectNames(string root, string mission, Snapshot snapshot, CancellationToken token)
    {
        static bool Effects(string name) => name.Equals("effects.zrd", StringComparison.OrdinalIgnoreCase);
        var sources = SourceProject.Files(root, "data/common", Effects, snapshot.Added, token).Where(p => p.Split('/').Contains("zrdr", StringComparer.OrdinalIgnoreCase))
            .Concat(SourceProject.Files(root, $"data/{mission}/zrdr", Effects, snapshot.Added, token)).ToArray();
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
        try { return Export.PngDecoder.Decode(bytes, TextureSources.MaximumSide, token); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{input}: {ex.Message}", ex); }
    }

    /// <summary>Refuses a pack with more textures than a pack holds before any of their files is read.</summary>
    private static void CheckCount(string output, int count)
    {
        if (count > TexturePackBuilder.MaximumRecords) throw new InvalidDataException($"{output} would hold {count:N0} textures; a texture pack holds at most {TexturePackBuilder.MaximumRecords:N0}.");
    }
    /// <summary>
    /// One texture of a pack from a project PNG, decoded only when the pack needs its pixels (see
    /// <see cref="TexturePackBuilder.BuildFromSources"/>): its size comes from the PNG header, so a pack keeps its textures
    /// only at the sizes it stores. What a decode finds (size and transparency) is remembered for the run, whose files
    /// cannot change, so later packs need not decode it to classify it.
    /// </summary>
    private static PackSource Source(string input, string name, int addressing, bool direct, Snapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var known = snapshot.Texture(input);
        int width, height;
        if (known is { } found) (width, height) = (found.Width, found.Height);
        else if (snapshot.PngSize(input, token) is { } header) (width, height) = header;
        // A file without a readable header is decoded at once, which reports what is wrong with it.
        else { var image = Decode(input, snapshot, token); (width, height) = (image.Width, image.Height); }
        return new(name, TextureSources.SortKey(input), width, height, t =>
        {
            var image = Decode(input, snapshot, t);
            // The header was read on its own: the file may have changed before it was read whole.
            if (image.Width != width || image.Height != height) throw snapshot.Changed(input, $"{input} changed while exporting; export again.");
            return image;
        }, addressing, direct) { Transparency = known?.Transparency, File = input };
    }
    /// <summary>Decodes a project PNG through the run's snapshot, remembering its size and transparency.</summary>
    private static DecodedImage Decode(string input, Snapshot snapshot, CancellationToken token)
    {
        var image = DecodeTexture(input, snapshot.Read(input, token), token);
        if (snapshot.Texture(input) == null) snapshot.Remember(input, image.Width, image.Height, TexturePackBuilder.Classify(image));
        return image;
    }

    private static Built BuildSounds(string root, SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        long retained = 8L + plan.Inputs.Count * ArchiveSources.RecordSize;
        FormatRegistry.ValidateDocumentSize(retained);
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
            // Nothing reads a sound's source field (the game finds sounds by name, and the shipped banks hold no paths there),
            // so a path the field cannot hold whole is left out rather than refused or shortened.
            string field = SourceField(input);
            FormatRegistry.ValidateDocumentSize(retained += payload.Length);
            entries.Add(new(name, ArchiveSources.FitsSourceField(field) ? field : "", payload));
        }
        return new(ArchiveSources.Write(entries), entries.Count, warnings);
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
