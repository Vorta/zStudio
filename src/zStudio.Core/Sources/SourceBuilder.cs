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
    /// <summary>
    /// Whether the profile promises this pack's textures at full size: it has an automatic pack, and this is the mission's
    /// largest Direct3D pack (the automatic one, or the largest fixed one when none is planned). Its build says which
    /// textures its budget had to reduce.
    /// </summary>
    public bool FullSize { get; init; }
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

    /// <summary>Bounds aggregate enumeration and publication work across all planned outputs.</summary>
    internal const int MaximumPlanInputs = 1_000_000;
    /// <summary>A mission-specific first input followed by one immutable inventory shared by all mission plans.</summary>
    private sealed class PrefixedInputs(string first, IReadOnlyList<string> shared) : IReadOnlyList<string>
    {
        public int Count => checked(1 + shared.Count);
        public string this[int index] => index == 0 ? first : index > 0 && index <= shared.Count ? shared[index - 1] : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<string> GetEnumerator() { yield return first; foreach (var item in shared) yield return item; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Every game file this tree can build, in a stable order; <paramref name="added"/> are pending new files (see
    /// <see cref="SourceWorkspace"/>). Texture packs follow <paramref name="profile"/> (the built-in modern one when null).
    /// Without <paramref name="automaticPacks"/> the automatic packs are left out: naming them reads every mission texture's
    /// PNG header, which listing the worlds or building one for a preview does not need. <paramref name="token"/> is
    /// observed while the project's folders are scanned (each scan visits at most <see cref="SourceProject.MaximumScannedEntries"/>).
    /// </summary>
    public static IReadOnlyList<SourceOutputPlan> Plan(string root, IReadOnlyCollection<string>? added = null, BuildProfile? profile = null, bool automaticPacks = true, CancellationToken token = default)
        => Plan(root, added, profile, automaticPacks, token, null);

    /// <remarks>
    /// With <paramref name="countPacks"/> (an export or check of every output), the automatic packs are named from the
    /// textures as their build counts them, through <paramref name="snapshot"/>: every texture the pack holds, with those its
    /// world brings from other folders, and their alpha planes (see <see cref="PackMemory"/>). Otherwise from the PNG headers
    /// of the mission's folders alone (see <see cref="TextureMemory"/>), which is all a listing of the outputs reads.
    /// </remarks>
    internal static IReadOnlyList<SourceOutputPlan> Plan(string root, IReadOnlyCollection<string>? added, BuildProfile? profile, bool automaticPacks, CancellationToken token, Snapshot? snapshot, string? onlyMission = null,
        InventoryBudget? inventory = null, bool countPacks = false)
    {
        inventory ??= snapshot?.Inventory ?? new();
        profile ??= BuildProfiles.Modern;
        if (!SourceProject.IsProject(root)) throw new InvalidDataException("This folder is not a source project (it needs data and gamegen folders).");
        PendingInventory? pending = added == null ? null : PendingInventory.Capture(added, inventory, token);
        added = pending;
        List<SourceOutputPlan> plans = [];
        long plannedInputs = 0;
        void AddPlan(SourceOutputPlan plan)
        {
            token.ThrowIfCancellationRequested();
            if (plan.Inputs.Count > MaximumPlanInputs - plannedInputs)
                throw new InvalidDataException($"The source plan exceeds {MaximumPlanInputs:N0} aggregate inputs; split the project into smaller source projects.");
            plannedInputs += plan.Inputs.Count;
            inventory.Rows(1, token); // Shared PrefixedInputs do not copy the underlying path strings.
            plans.Add(plan);
        }
        static bool Zrd(string name) => name.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase);
        // Common resources come from every zrdr folder under data/common (including multi_bft/zrdr).
        var commonFiles = SourceProject.Files(root, "data/common", Zrd, added, token, inventory);
        inventory.Rows(commonFiles.Count, token);
        var common = commonFiles.Where(p => p.AsSpan().Contains("/zrdr/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (common.Length > 0) AddPlan(new("zrdr.zbd", "archive", common));
        // The game opens a resource from the first mounted archive that holds its name, and zrdr.zbd stays ahead of the
        // mission's (docs/engine-evidence.md, Resource archives): a mission resource named like a common one is never read.
        Dictionary<string, string>? commonNames = null;
        IReadOnlyList<string> Shadowed(string mission, IReadOnlyList<string> resources)
        {
            if (common.Length == 0) return [];
            if (commonNames == null)
            {
                commonNames = new(StringComparer.OrdinalIgnoreCase);
                foreach (string path in common.Order(StringComparer.Ordinal)) { token.ThrowIfCancellationRequested(); commonNames.TryAdd(Path.GetFileName(path), path); }
            }
            List<(string Resource, string Common)> shown = []; int count = 0;
            foreach (string resource in resources)
            {
                token.ThrowIfCancellationRequested();
                if (commonNames.TryGetValue(Path.GetFileName(resource), out string? shadow) && count++ < 8)
                    shown.Add((JsonData.ShownText(resource, 192), JsonData.ShownText(shadow, 192)));
            }
            if (count == 0) return [];
            const string Why = "it opens a resource from the first archive that holds its name";
            return [count == 1
                ? $"The game never reads {shown[0].Resource}: {Why}, and zrdr.zbd, mounted before {mission}/zrdr.zbd, holds {shown[0].Common}."
                : $"The game never reads {count} of these resources: {Why}, and zrdr.zbd, mounted before {mission}/zrdr.zbd, holds files of the same names: "
                    + string.Join(", ", shown.Select(s => $"{s.Resource} ({s.Common})")) + (count > shown.Count ? $" and {count - shown.Count} more." : ".")];
        }
        var scripts = SourceProject.Files(root, SourceProject.GameGenFolder, n => n.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".gw", StringComparison.OrdinalIgnoreCase), added, token, inventory);
        if (scripts.Count > 0) AddPlan(new("interp.zbd", "scripts", scripts));
        var sounds = SourceProject.Files(root, SoundsFolder, n => n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase), added, token, inventory);
        if (sounds.Count > 0) foreach (string bank in Banks) AddPlan(new(bank, "sounds", sounds));
        // Listing data for its mission folders is a scan too: every entry counts, and the token is observed at each.
        var missions = SourceProject.MissionFolders(root, token, inventory: inventory);
        // A world may load any model in the project, and animations any keyframe script; which depends on the sources.
        IReadOnlyList<string>? models = null, scriptsFound = null;
        bool hasModels = false;
        // Interface images: fonts, the images tree and each mission's objective images.
        List<string> images = [];
        void Images(IReadOnlyList<string> entries)
        {
            if (entries.Count > MaximumPlanInputs - images.Count)
                throw new InvalidDataException($"The source plan exceeds {MaximumPlanInputs:N0} image inputs; split the project into smaller source projects.");
            inventory.Rows(entries.Count, token);
            images.AddRange(entries);
        }
        Images(SourceProject.Files(root, TextureSources.Fonts, Png, added, token, inventory));
        Images(SourceProject.Files(root, TextureSources.Images, Png, added, token, inventory));
        foreach (var mission in missions) Images(SourceProject.Files(root, $"data/{mission}/images", Png, added, token, inventory));
        if (images.Count > 0) AddPlan(new("image.zbd", "images", images));
        foreach (var mission in missions)
        {
            token.ThrowIfCancellationRequested();
            string name = mission.ToLowerInvariant();
            if (onlyMission != null && !name.Equals(onlyMission, StringComparison.OrdinalIgnoreCase)) continue;
            string entry = WorldScript(name);
            if (SourceRead.FileExists(SourceProject.Resolve(root, entry)) || pending?.ContainsPath(entry, token) == true)
            {
                if (models == null)
                {
                    models = SourceProject.Files(root, SourceProject.DataFolder, IsModelSource, added, token, inventory);
                    hasModels = models.Any(m => !m.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
                }
                // A project without glTF models has no world to build (buffers alone are not models).
                if (hasModels) AddPlan(new($"{name}/gamez.zbd", "world", new PrefixedInputs(entry, models)));
            }
            var resources = SourceProject.Files(root, $"data/{name}/zrdr", Zrd, added, token, inventory);
            if (resources.Count > 0) AddPlan(new($"{name}/zrdr.zbd", "archive", resources) { Notes = Shadowed(name, resources) });
            string definitions = AnimationRoot(name);
            if (SourceRead.FileExists(SourceProject.Resolve(root, definitions)) || pending?.ContainsPath(definitions, token) == true)
            {
                scriptsFound ??= SourceProject.Files(root, SourceProject.DataFolder, n => n.EndsWith(Animation.AnimationScript.Extension, StringComparison.OrdinalIgnoreCase), added, token, inventory);
                AddPlan(new($"{name}/anim.zbd", "animations", new PrefixedInputs(definitions, scriptsFound)));
            }
            var textures = MissionTextures(root, name, added, token, snapshot, inventory);
            if (textures.Count > 0)
            {
                List<SourceOutputPlan> packs = [];
                bool promised = false, planned = false;
                foreach (var pack in profile.TexturePacks.Where(pack => pack.Builds(name)))
                {
                    if (!pack.Automatic) { packs.Add(new($"{name}/{pack.File}", "textures", textures) { Pack = pack.Variant }); continue; }
                    if (!automaticPacks) continue;
                    promised = true;
                    // The automatic pack is named for the texture memory the mission's textures need at full size.
                    long memory = countPacks && snapshot != null ? PackMemory(root, name, textures, pack.Variant, snapshot, token) : TextureMemory(root, textures, pack.Variant, token);
                    if (BuildProfiles.AutomaticPack(profile, name, memory) is not { BudgetBytes: long budget } automatic) continue;
                    planned = true;
                    packs.Add(new($"{name}/{automatic.FileName}", "textures", textures)
                    {
                        Pack = automatic, Automatic = true, FullSize = true,
                        Notes = memory > budget
                            ? [$"The {name} textures need {(memory + (1 << 20) - 1) >> 20} MB of texture memory at full size, more than the {budget >> 20} MB the profile's automatic rtexture pack may hold; {automatic.FileName} holds them reduced to fit."] : [],
                    });
                }
                // Without an automatic pack the largest fixed one is what modern cards load, so it must hold them at full size.
                if (promised && !planned && packs.Where(p => p.Pack!.Kind == TexturePackKind.Hardware).MaxBy(p => p.Pack!.BudgetBytes ?? long.MaxValue) is { } largest)
                    packs[packs.IndexOf(largest)] = largest with { FullSize = true };
                foreach (var pack in packs) AddPlan(pack);
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
        long maximumRetainedBytes = SourceExtractor.MaximumRetainedBytes, EffectLimits? effectLimits = null,
        long inventoryLimit = InventoryBudget.MaximumUnits, CancellationToken inventoryToken = default)
    {
        private readonly (InventoryBudget Budget, PendingInventory Added) inventory = CapturePending(root, overlay, inventoryLimit, inventoryToken);
        internal InventoryBudget Inventory => inventory.Budget;
        private EffectDiscovery? effectDiscovery;
        internal HashSet<string>? Effects(string mission, CancellationToken token) =>
            (effectDiscovery ??= new(this, root, effectLimits ?? new())).ForMission(mission, token);
        private readonly Dictionary<string, IReadOnlyDictionary<string, int>> textureAddressing = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// The clamp word of each texture a mission's packs store (<see cref="TextureCycleReader.Addressing"/>): from the
        /// samplers of its world's models, and for a texture no model samples, from the material that its world's texture
        /// cycles or its effects' cycles show it on.
        /// </summary>
        internal IReadOnlyDictionary<string, int> TextureAddressing(string mission, CancellationToken token)
        {
            if (textureAddressing.TryGetValue(mission, out var known)) return known;
            var addressing = TextureCycleReader.Addressing(World(mission, token).TextureAddressing, TextureCycles(mission, token), token);
            textureAddressing.Add(mission, addressing);
            return addressing;
        }
        private readonly Dictionary<string, IReadOnlyList<CycledTextures>> textureCycles = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The texture cycles a mission sets up as the game loads it: its texture-effect scripts', then its effects'.</summary>
        private IReadOnlyList<CycledTextures> TextureCycles(string mission, CancellationToken token)
        {
            if (textureCycles.TryGetValue(mission, out var known)) return known;
            var world = World(mission, token);
            TextureCycleReader effects = new(world.World, new(token: token), token);
            foreach (var (node, maps) in (effectDiscovery ??= new(this, root, effectLimits ?? new())).CyclesForMission(mission, token)) effects.Effect(node, maps);
            IReadOnlyList<CycledTextures> cycles = [.. world.TextureCycles, .. effects.Cycles];
            textureCycles.Add(mission, cycles);
            return cycles;
        }
        private readonly Dictionary<string, HashSet<string>> directTextures = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// The textures a mission's software packs keep in direct colour, which the software renderer copies without shading
        /// or fog (zRndrDrawTexturedQueued, retail 0x495850; a paletted texture goes through the shade path,
        /// SpanShade16FromPal8): the damage masks its scripts register (<see cref="DamageMasks"/>), which can stamp only from
        /// direct textures (ApplyDamageMaskStampOnHit, retail 0x479660), and the horizon's textures. The horizon is the node the
        /// game finds by the name <c>horizon</c> from the world node (Player::InitMissionRuntimeFromWorldAndCamera, retail
        /// 0x41FE90) and keeps at the camera (Player::UpdateThirdPersonCamera, 0x405650), with the nodes the cameras hold as
        /// their horizons (CameraSetHorizon, CameraSetHorizonXZ): the textures of every model at or below them, and the frames
        /// of texture cycles shown in place of one. Both shipped releases keep exactly these direct, besides choices no
        /// source records (docs/engine-evidence.md, Textures and the rendering target).
        /// </summary>
        internal HashSet<string> DirectTextures(string mission, CancellationToken token)
        {
            if (directTextures.TryGetValue(mission, out var known)) return known;
            HashSet<string> direct = new(DamageMasks(mission, token), StringComparer.OrdinalIgnoreCase);
            if (HasWorld(mission))
            {
                var world = World(mission, token).World;
                List<WorldNode> horizons = [];
                foreach (var node in world.Nodes)
                {
                    token.ThrowIfCancellationRequested();
                    if (node.Class == WorldNodeClass.World && WorldAssembler.FindSub(node, "horizon") is { } named) horizons.Add(named);
                    if (node.Class == WorldNodeClass.Camera) horizons.AddRange([.. new[] { node.CameraHorizon, node.CameraHorizonXZ }.OfType<WorldNode>()]);
                }
                HashSet<string> sky = new(StringComparer.OrdinalIgnoreCase);
                foreach (var node in WorldAssembler.Subtree(horizons))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var polygon in node.Model?.Polygons ?? []) if (polygon.Material?.Texture is { } texture) sky.Add(texture.Name);
                }
                foreach (var cycle in TextureCycles(mission, token))
                    if (cycle.Base != null && sky.Contains(cycle.Base)) direct.UnionWith(cycle.Frames);
                direct.UnionWith(sky);
            }
            directTextures.Add(mission, direct);
            return direct;
        }
        internal long EffectInputBytes => effectDiscovery?.InputBytes ?? 0;
        internal int EffectFilesDecoded => effectDiscovery?.FilesDecoded ?? 0;
        internal long RetainedBytes { get; private set; }
        /// <summary>
        /// Worlds, compiled animation packages and the project paths the run looked up (every distinct path is kept to check
        /// before publication, absent ones included) share the run's retained-data limit, across every output.
        /// </summary>
        internal void Retain(long bytes)
        {
            if (bytes < 0 || bytes > maximumRetainedBytes - RetainedBytes)
                throw new InvalidDataException($"The export's retained worlds, animations and looked-up project paths exceed {maximumRetainedBytes / (1024 * 1024):N0} MiB; export fewer missions together, or name fewer search folders and missing files in their scripts.");
            RetainedBytes += bytes;
        }
        private readonly Dictionary<string, (string Sha, FileStamp Stamp)> files = new(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, string> Hashes() => files.ToDictionary(p => p.Key, p => p.Value.Sha, StringComparer.OrdinalIgnoreCase);
        // Negative lookups affect compilation too (optional definitions, search paths and fallback resources).
        private readonly Dictionary<string, FileStamp?> probes = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Every project file the run read or asked about, from the overlay or the disk: what its outputs depend on.</summary>
        private readonly HashSet<string> dependencies = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock dependencyGate = new();
        internal IReadOnlyCollection<string> Dependencies() { lock (dependencyGate) return dependencies.ToArray(); }
        /// <summary>Pending files the disk does not hold yet (new files of a workspace), which count as present.</summary>
        internal IReadOnlyCollection<string> Added => inventory.Added;
        private static (InventoryBudget, PendingInventory) CapturePending(string root, IReadOnlyDictionary<string, byte[]>? overlay,
            long maximum, CancellationToken token)
        {
            InventoryBudget budget = new(maximum);
            List<string> added = [];
            if (overlay != null)
                foreach (string path in overlay.Keys)
                {
                    budget.Path((long)root.Length + 1 + path.Length, token); // Before Resolve constructs the full path.
                    if (!SourceRead.FileExists(SourceProject.Resolve(root, path))) added.Add(path);
                }
            return (budget, new(added, budget, token));
        }
        /// <summary>
        /// Records a path the run depends on. A new path is charged to <see cref="Retain"/>: its text, its dependency entry and
        /// its probe or read entry, which the run keeps until publication.
        /// </summary>
        internal void Depend(string relative)
        {
            lock (dependencyGate)
                if (dependencies.Add(relative)) Retain(160L + 2L * relative.Length);
        }
        /// <summary>The disk files read or found by a search and their stamps (pending content is not included).</summary>
        internal IReadOnlyDictionary<string, FileStamp> Stamps() => probes.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase);
        /// <summary>Disk files and search folders absent when the run looked for them.</summary>
        internal IReadOnlyList<string> Missing() => [.. probes.Where(p => p.Value == null).Select(p => p.Key), .. missingFolders];
        private readonly HashSet<string> missingFolders = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The search folders absent when the run looked for them: a file added below one can change what the build finds.</summary>
        internal IReadOnlyList<string> MissingFolders() => [.. missingFolders];
        private HashSet<string>? pendingFolders;
        /// <summary>
        /// Whether a folder a script names for a search path exists (see <see cref="DirectorySearchList"/>): on disk, or holding a
        /// pending file. A folder found missing is kept with the missing files, so its appearance before publication refuses
        /// the export, and a preview built without it is stale once a file is added below it.
        /// </summary>
        internal bool FolderExists(string relative)
        {
            SourceProject.RequireSource(relative);
            // A name Windows cannot give a folder never exists, as _access reports it.
            if (relative.AsSpan().IndexOfAny(InvalidFolderCharacters) >= 0) return false;
            Depend(relative);
            if (overlay != null)
            {
                if (pendingFolders == null)
                {
                    pendingFolders = new(StringComparer.OrdinalIgnoreCase);
                    foreach (string pending in overlay.Keys)
                        for (int slash = pending.LastIndexOf('/'); slash > 0; slash = pending.LastIndexOf('/', slash - 1))
                            if (!pendingFolders.Add(pending[..slash])) break;
                }
                if (pendingFolders.Contains(relative)) return true;
            }
            string path = SourceProject.Resolve(root, relative);
            SourceProject.RejectNestedLinks(root, relative);
            bool present = SourceRead.DirectoryExists(path);
            if (!present) missingFolders.Add(relative);
            else if (missingFolders.Contains(relative)) throw Changed(relative, $"{relative} changed while exporting; export again.");
            return present;
        }
        private static readonly System.Buffers.SearchValues<char> InvalidFolderCharacters = System.Buffers.SearchValues.Create(
            [.. Enumerable.Range(0, 32).Select(c => (char)c), '<', '>', ':', '"', '|', '?', '*']);
        internal bool Exists(string relative)
        {
            SourceProject.RequireSource(relative);
            Depend(relative);
            if (overlay?.ContainsKey(relative) == true) return true;
            string path = SourceProject.Resolve(root, relative);
            SourceProject.RejectNestedLinks(root, relative);
            FileStamp? observed = SourceRead.FileExists(path) ? FileStamp.Read(path) : null;
            if (probes.TryGetValue(relative, out var first) && first != observed)
                throw Changed(relative, $"{relative} changed while exporting; export again.");
            probes[relative] = observed;
            return observed != null;
        }
        internal byte[] Read(string relative, CancellationToken token, long maximum = FormatRegistry.MaximumDocumentBytes)
            => Read(relative, token, ProjectReadLimits.Bytes(maximum));

        internal byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested();
            Depend(relative);
            if (overlay?.TryGetValue(relative, out var pending) == true)
            {
                limits.Validate(pending);
                return pending;
            }
            if (!Exists(relative)) throw new FileNotFoundException($"{relative} does not exist.");
            string path = SourceProject.Resolve(root, relative);
            var stamp = FileStamp.Read(path);
            byte[] bytes = SourceRead.All(path, limits, token); string sha = SourceProject.Sha256(bytes);
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
            return TextureSources.PngSize(SourceProject.Resolve(root, relative), recordPlanningRead: false);
        }
        private readonly Dictionary<string, (int Width, int Height, TextureTransparency Transparency)> textures = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// What decoding a PNG of the run found: its size and transparency. A file's content is the same for the whole run
        /// (<see cref="Read"/>), so a texture several packs hold is classified once.
        /// </summary>
        internal (int Width, int Height, TextureTransparency Transparency)? Texture(string relative) => textures.TryGetValue(relative, out var found) ? found : null;
        internal void Remember(string relative, int width, int height, TextureTransparency transparency) => textures[relative] = (width, height, transparency);
        private readonly Dictionary<string, HashSet<string>> damageMasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> damageScripts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ScriptTraceBudget damageWork = new();
        private long damageSourceBytes, damageTokens, damageLines;
        /// <summary>Executed damage-mask registrations for one mission; source and execution allowances are shared by the export.</summary>
        internal HashSet<string> DamageMasks(string mission, CancellationToken token, long maximumSourceBytes = 64L * 1024 * 1024,
            long maximumTokens = GameGenScriptText.MaximumTokens, long maximumLines = GameGenScriptText.MaximumLines)
        {
            token.ThrowIfCancellationRequested();
            if (damageMasks.TryGetValue(mission, out var known)) return known;
            HashSet<string> masks = new(StringComparer.OrdinalIgnoreCase);
            // Build and runtime load scripts are separate interpreter entries, with independent macro state.
            // Each follows only its executed sources, including registrations after GameZWriteZBDFile.
            foreach (string entry in new[] { WorldScript(mission), WorldLookups.LoadScript(mission) })
            {
                if (Exists(entry)) ScriptTrace.Visit(Script, entry[(SourceProject.GameGenFolder.Length + 1)..], Executed,
                    token, stopAtWorldWrite: false, budget: damageWork);
            }
            token.ThrowIfCancellationRequested();
            damageMasks.Add(mission, masks);
            return masks;

            IReadOnlyList<IReadOnlyList<string>> Script(string name)
            {
                damageWork.Operands.Inspect(name, token, 2);
                string script = SourceProject.GameGenFolder + "/" + name.Replace('\\', '/');
                if (damageScripts.TryGetValue(script, out var cached)) return cached;
                // A missing executed source is not an empty mask set. Read retains the snapshot's dependency checks.
                if (damageSourceBytes > maximumSourceBytes)
                    throw new InvalidDataException("Damage-mask discovery exceeds its aggregate script byte allowance. Reduce the scripts before retrying.");
                byte[] bytes = Read(script, token, ProjectReadLimits.Text(maximumSourceBytes - damageSourceBytes));
                string text = GameGenScriptText.Decode(bytes, token);
                long lines = SourceTextScan.Count(text, '\n', text.Length, token) + (text.Length == 0 || text[^1] != '\n' ? 1 : 0);
                long tokens = GameGenScriptText.CountTokens(text, token);
                if (lines > maximumLines - damageLines || tokens > maximumTokens - damageTokens)
                    throw new InvalidDataException("Damage-mask discovery exceeds its aggregate script line or token allowance. Reduce the scripts before retrying.");
                var parsed = GameGenScriptText.TokenizeCancellable(text, token);
                damageSourceBytes += bytes.LongLength; damageLines += lines; damageTokens += tokens;
                damageScripts.Add(script, parsed);
                return parsed;
            }
            void Executed(TracedInstruction instruction)
            {
                if (instruction.Command != "WriteTextureSetMap" || WorldAssembler.ScriptTexture(instruction.Command, instruction.Args) is not { } operand) return;
                damageWork.Operands.Inspect(operand, token, 3); // Normalize, extract the basename and hash before retaining it.
                masks.Add(Path.GetFileNameWithoutExtension(operand.Replace('\\', '/')));
            }
        }
        /// <summary>A mission world assembled once per run; its texture packs hold the textures it uses.</summary>
        internal sealed record AssembledWorld(GameZWorld World, IReadOnlyList<string> Warnings, IReadOnlyDictionary<string, string> TextureFiles, IReadOnlyDictionary<string, int> TextureAddressing, IReadOnlyList<WorldNode> LoadedRoots)
        {
            public IReadOnlyDictionary<WorldNode, WorldNodeProvenance> Provenance { get; init; } = new Dictionary<WorldNode, WorldNodeProvenance>();
            public IReadOnlyDictionary<(string Script, int Line), int> Executions { get; init; } = new Dictionary<(string, int), int>();
            public SourceInstruction? WriteInstruction { get; init; }
            /// <summary>The texture cycles the world's scripts set up after writing it, with the materials they are shown on.</summary>
            public IReadOnlyList<CycledTextures> TextureCycles { get; init; } = [];
        }
        private readonly Dictionary<string, (AssembledWorld? World, Exception? Failure)> worlds = new(StringComparer.OrdinalIgnoreCase);
        internal bool HasWorld(string mission) => Exists(WorldScript(mission));
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
                        + assembler.Provenance.Values.Sum(p => 128L * p.Applied.Count) + assembler.TextureCycles.Sum(c => 64L + 16L * c.Frames.Count));
                    cached = (new(world, assembler.Warnings, new Dictionary<string, string>(assembler.TextureFiles, StringComparer.OrdinalIgnoreCase),
                        new Dictionary<string, int>(assembler.TextureAddressing, StringComparer.OrdinalIgnoreCase), [.. assembler.LoadedRoots])
                    { Provenance = assembler.Provenance, Executions = assembler.Executions, WriteInstruction = assembler.WriteInstruction, TextureCycles = assembler.TextureCycles }, null);
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
            public bool Exists(string relative) => snapshot.Exists(relative);
            public bool FolderExists(string relative) => snapshot.FolderExists(relative);
            public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { SourceProject.RequireSource(relative); if (overlay?.ContainsKey(relative) != true) SourceProject.RejectNestedLinks(root, relative); return snapshot.Read(relative, token, limits); }
        }

        internal void CheckUnchanged(CancellationToken token)
        {
            effectDiscovery?.CheckUnchanged(token);
            foreach (var (relative, stamp) in probes)
            {
                token.ThrowIfCancellationRequested();
                string path = SourceProject.Resolve(root, relative);
                SourceProject.RejectNestedLinks(root, relative);
                if (stamp == null ? SourceRead.PathExists(path) : !SourceRead.FileExists(path) || FileStamp.Read(path) != stamp)
                    throw Changed(relative, $"{relative} changed while exporting; nothing was written.");
            }
            // A search folder that appeared would join the search paths the build skipped.
            foreach (string folder in missingFolders)
            {
                token.ThrowIfCancellationRequested();
                SourceProject.RejectNestedLinks(root, folder);
                if (SourceRead.DirectoryExists(SourceProject.Resolve(root, folder)))
                    throw Changed(folder, $"{folder} changed while exporting; nothing was written.");
            }
            foreach (var (relative, entry) in files)
            {
                token.ThrowIfCancellationRequested();
                string path = SourceProject.Resolve(root, relative);
                if (!SourceRead.FileExists(path) || FileStamp.Read(path) != entry.Stamp) throw Changed(relative, $"{relative} changed while exporting; nothing was written.");
                // Timestamps and lengths are hints, not content identities: editors can preserve both.
                if (!SourceRead.Matches(path, entry.Stamp.Length, entry.Sha, token) || FileStamp.Read(path) != entry.Stamp)
                    throw Changed(relative, $"{relative} changed while exporting; nothing was written.");
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
    /// only with <paramref name="overwrite"/>, or when <paramref name="confirmReplace"/>, shown exactly the existing files this
    /// export's plan writes (for an export of every output, the automatic pack as its build sizes it), confirms them before
    /// anything is built; then only those are replaced (declining cancels the export). Publication restores them if any step
    /// fails, and nothing is written if any output fails.
    /// </summary>
    public static Task<SourceExportReport> ExportAsync(string root, string destination, IReadOnlyCollection<string>? outputs = null, bool overwrite = false, IProgress<SourceProgress>? progress = null, CancellationToken token = default, string? profile = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? confirmReplace = null)
        => RunAsync(root, destination, outputs, overwrite, progress, token, profile, confirmReplace: confirmReplace);

    /// <summary><paramref name="profileName"/> selects the build profile (the project's default when null).</summary>
    internal static async Task<SourceExportReport> RunAsync(string root, string? destination, IReadOnlyCollection<string>? outputs, bool overwrite, IProgress<SourceProgress>? progress, CancellationToken token, string? profileName, EffectLimits? effectLimits = null,
        long inventoryLimit = InventoryBudget.MaximumUnits, Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? confirmReplace = null)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // The run's view of the project starts before planning: the profile is read through it, so a change to its file is
        // refused like a changed source, and the plan is made again before publishing (see CheckPlanUnchanged).
        Snapshot snapshot = new(root, effectLimits: effectLimits, inventoryLimit: inventoryLimit, inventoryToken: token);
        var profile = await Task.Run(() => FindProfile(root, profileName, snapshot, token), token);
        // An export of every output names the automatic packs from the textures as their builds count them.
        var all = await Task.Run(() => Plan(root, null, profile, true, token, snapshot, countPacks: outputs == null), token);
        var selected = outputs == null ? all : outputs.Select(o => all.FirstOrDefault(p => p.Path.Equals(o.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"This source project cannot build {o}.")).Distinct().ToArray();
        if (selected.Count == 0) throw new InvalidDataException("This source project has nothing to build yet.");
        string? staging = null;
        // Without overwrite, the existing game files the caller confirmed: the only ones publication may replace.
        HashSet<string>? confirmed = null;
        using DirectoryLease directories = new();
        if (destination != null)
        {
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
            SourceProject.ValidateSeparate(destination, root, "export destination"); SourceProject.RejectLinks(destination);
            SourceProject.ValidateSeparate(Path.GetDirectoryName(directories.CapturedPath(Path.Combine(destination, "_")))!,
                Path.GetDirectoryName(directories.CapturedPath(Path.Combine(root, "_")))!, "export destination");
            directories.Hold(destination, create: true);
            var existing = selected.Where(p => directories.Exists(SourceProject.Resolve(destination, p.Path))).Select(p => p.Path).ToArray();
            if (existing.Length > 0 && !overwrite)
            {
                if (confirmReplace == null) throw new IOException($"The destination already has {existing.Length} of these game files ({string.Join(", ", existing.Take(8))}). Choose another folder or allow replacing them.");
                if (!await confirmReplace(existing, token)) throw new OperationCanceledException("Replacing the existing game files was declined; nothing was exported.");
                confirmed = new(existing, StringComparer.OrdinalIgnoreCase);
            }
            staging = Path.Combine(destination, ".zstudio-staging-" + Guid.NewGuid().ToString("N"));
            directories.CreateDirectory(staging);
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
                        string path = SourceProject.Resolve(staging, plan.Path); directories.Parent(path, create: true);
                        await using FileStream written = directories.OpenFile(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
                        await written.WriteAsync(built.Bytes, token); await written.FlushAsync(token); written.Flush(true);
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
            BoundedDiagnostics notes = new();
            if (destination != null)
                try
                {
                    InventoryBudget destinationInventory = new();
                    foreach (string mission in results.Where(r => r.Family == "textures").Select(r => r.Path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
                        foreach (string pack in BuildProfiles.ShadowingPacks(destination, mission, results.Where(p => p.Family == "textures" && p.Status == "built" && p.Path.StartsWith(mission + "/", StringComparison.OrdinalIgnoreCase)).Select(p => p.Path[(mission.Length + 1)..]).ToArray(), SourceProject.MaximumScannedEntries, token, destinationInventory))
                            notes.Add($"{pack} is not being replaced by this export, but the game may load it instead of the exported ones. Delete it, or include that pack in the selected outputs of a profile that builds it.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add($"The destination's texture packs could not be listed ({ex.Message}); a pack left there by another export may be loaded instead of the exported ones."); }
            // Prepare all diagnostic text before publication. Cancellation or refused formatting must never
            // hide a successful export, and bounded MCP/GUI projections cannot protect an already-expanded report.
            var reportNotes = LookupNotes(notes, notChecked, changes, token);
            progress?.Report(new(selected.Count, selected.Count, destination == null ? "Checked" : "Publishing"));
            await Task.Run(() =>
            {
                CheckPlanUnchanged(root, profileName, profile, outputs == null ? null : selected, all, snapshot, token);
                snapshot.CheckUnchanged(token);
            }, token);
            if (staging != null && destination != null)
            {
                if (results.Any(r => r.Status == "failed")) throw new InvalidDataException(FailedOutputs(results, token));
                Publish(staging, destination, [.. results.Select(r => (r.Path, contents[r.Path]))], overwrite, token, captured: directories, replace: confirmed);
            }
            return new(destination, results) { Profile = profile.Name, Notes = reportNotes, Lookups = lookups, LookupChanges = changes };
        }
        // A staging folder another program holds must not replace the export's own result or error.
        finally
        {
            try { if (staging != null) directories.DeleteTree(staging); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static List<string> LookupNotes(BoundedDiagnostics notes, IEnumerable<string> notChecked, IEnumerable<SourceLookupChange> changes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (string note in notChecked) { token.ThrowIfCancellationRequested(); notes.Add($"{new BoundedDiagnostics.PreparedMessage(note)}"); }
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            WorldLookups.Describe(notes, change, " in the files this export replaced");
        }
        token.ThrowIfCancellationRequested();
        var result = notes.Snapshot();
        if (result.Remove(BoundedDiagnostics.OmissionNotice)) result.Insert(0, BoundedDiagnostics.OmissionNotice);
        return result;
    }

    internal static string FailedOutputs(IEnumerable<SourceExportResult> results, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BoundedDiagnostics failures = new();
        foreach (var result in results)
        {
            token.ThrowIfCancellationRequested();
            if (result.Status == "failed") failures.Add($"{result.Path}: {result.Error}");
        }
        token.ThrowIfCancellationRequested();
        var messages = failures.Snapshot();
        if (messages.Remove(BoundedDiagnostics.OmissionNotice)) messages.Insert(0, BoundedDiagnostics.OmissionNotice);
        return "Nothing was written because some outputs failed: " + string.Join("; ", messages);
    }

    /// <summary>The build profile (the project's default when <paramref name="name"/> is null), its files read through the run's snapshot.</summary>
    private static BuildProfile FindProfile(string root, string? name, Snapshot snapshot, CancellationToken token) => BuildProfiles.Find(root, name, path =>
    {
        // As a profile is read without a snapshot: a file over 64 KB is refused before it is read.
        if (new FileInfo(SourceProject.Resolve(root, path)).Length > BuildProfiles.MaximumFileBytes) throw new InvalidDataException($"{path} is larger than 64 KB.");
        return snapshot.Read(path, token, BuildProfiles.MaximumFileBytes);
    }, files: SourceProject.Files(root, BuildProfiles.Folder, n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase), token: token, inventory: snapshot.Inventory), token: token);
    /// <summary>
    /// Refuses outputs whose plan the project no longer gives, because it changed after it was planned: the plan is made again,
    /// as the run made it, and must name the same profile and give each built output the same inputs, pack and notes (with no outputs
    /// selected, the same outputs, so one added meanwhile is not left out). Planning lists the source folders and reads the
    /// multiplayer load scripts and the mission textures' PNG headers, which the run's snapshot does not hold; the profile
    /// files it reads are in the snapshot, so a changed one fails here or in <see cref="Snapshot.CheckUnchanged"/>.
    /// </summary>
    internal static void CheckPlanUnchanged(string root, string? profileName, BuildProfile profile, IReadOnlyList<SourceOutputPlan>? selected, IReadOnlyList<SourceOutputPlan> planned, Snapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BuildProfile again; IReadOnlyList<SourceOutputPlan> now;
        try { again = FindProfile(root, profileName, snapshot, token); now = Plan(root, null, again, true, token, snapshot, countPacks: selected == null); }
        catch (InventoryCapacityException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { throw new InvalidDataException($"The project changed while exporting; nothing was written. Export again. ({ex.Message})", ex); }
        bool same = again.Name == profile.Name && again.Source == profile.Source && (selected == null
            ? now.Count == planned.Count && planned.Zip(now).All(p => SamePlan(p.First, p.Second))
            : selected.All(p => now.FirstOrDefault(n => n.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase)) is { } n && SamePlan(p, n)));
        if (!same) throw new InvalidDataException("The project's sources changed while exporting (files were added, removed or renamed, a texture changed size, or the build profile changed); nothing was written. Export again.");
    }
    /// <summary>Whether two plans of an output build it the same way: the same path, family, inputs (in order), pack and notes.</summary>
    internal static bool SamePlan(SourceOutputPlan a, SourceOutputPlan b) => a.Path == b.Path && a.Family == b.Family && a.Pack == b.Pack && a.Automatic == b.Automatic && a.FullSize == b.FullSize
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
        return WorldLookups.FindNodes((path, remaining) => files.Exists(path) ? files.Read(path, token, ProjectReadLimits.Text(remaining)) : null, mission, token);
    }
    /// <summary>Why the lookups by name of a mission could not be checked, as a report says it.</summary>
    internal static string LookupsUnchecked(string mission, Exception ex)
    {
        BoundedDiagnostics note = new(); LookupsUnchecked(note, mission, ex); return note.Messages[0];
    }
    private static void LookupsUnchecked(BoundedDiagnostics notes, string mission, Exception ex) =>
        notes.Add($"The lookups by name the game makes as it loads {mission} were not checked, so none of them is reported: {ex.Message}");
    /// <summary>
    /// The lookups by name of each mission whose world or animations were built, for the report those several nodes share,
    /// and those that find another node than in the destination's files this export replaces (read before they are replaced).
    /// The game binds a built world with the destination's animations when this export leaves them, and built animations
    /// with the destination's world likewise, so those are resolved together.
    /// </summary>
    internal static (List<SourceLookup> Lookups, List<SourceLookupChange> Changes, List<string> NotChecked) MissionLookups(IReadOnlyList<SourceExportResult> results, IReadOnlyDictionary<string, Animation.AnimationPackage> packages,
        Snapshot snapshot, string? destination, CancellationToken token, long lookupWorkLimit = LookupWorkBudget.MaximumUnits)
    {
        List<SourceLookup> lookups = []; List<SourceLookupChange> changes = []; BoundedDiagnostics notChecked = new();
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
                resolved = WorldLookups.Resolve(mission, after, packages.GetValueOrDefault(mission) ?? previous?.Animations, findNodes, token, new(lookupWorkLimit, token));
            }
            catch (Exception ex) when (IsBuildFailure(ex)) { LookupsUnchecked(notChecked, mission, ex); continue; }
            lookups.AddRange(resolved.Where(l => l.Ambiguous));
            // Only a report: files that cannot be paired give no changes rather than failing the export.
            if (previous is { } replaced)
                try
                {
                    var before = WorldLookups.Resolve(mission, replaced.World, replaced.Animations, findNodes, token, new(lookupWorkLimit, token));
                    changes.AddRange(WorldLookups.Changes(replaced.World, before, after, resolved, token));
                }
                catch (Exception ex) when (IsBuildFailure(ex))
                {
                    notChecked.Add($"The node lookup comparison with the previous {mission} world was not completed; changes from the replaced files are not reported: {JsonData.ShownText(ex.Message, 512)}");
                }
        }
        return (lookups, changes, notChecked.Snapshot());
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
    /// <paramref name="overwrite"/>, an existing game file is replaced only when <paramref name="replace"/> names it (the
    /// files the caller confirmed), so one that appeared while the outputs were built is never replaced. Each output's
    /// <c>Content</c> is the digest of the bytes that were built and reopened. Before anything is replaced every staged file
    /// is checked against it and held, so no other program can write or rename it, until it is in place (see
    /// <see cref="SealedFile"/>): a staged file changed after its output was built fails the export before it replaces
    /// anything. <paramref name="fault"/> is a test hook called with an output's index before its staged file is checked
    /// ("check"), before its original is moved aside ("replace") and before it is installed ("install"); it may throw to
    /// simulate a failure.
    /// </summary>
    internal static void Publish(string staging, string destination, IReadOnlyList<(string Relative, JournalDigest Content)> outputs, bool overwrite, CancellationToken token, Action<string, int>? fault = null, DirectoryLease? captured = null,
        IReadOnlySet<string>? replace = null)
    {
        foreach (var (relative, _) in outputs) { _ = SourceProject.Resolve(destination, relative); SourceProject.RejectNestedLinks(destination, relative); }
        string backup = Path.Combine(destination, ".zstudio-backup-" + Guid.NewGuid().ToString("N"));
        // Original: the digest of the replaced file moved into the backup, so undoing restores it only while the backup still
        // holds it. Installed: the content this export moved into place, so undoing it removes only that file.
        List<(string Target, string? Saved, JournalDigest? Original, JournalDigest? Installed)> steps = [];
        // Each staged output from its check until it is in place: what is installed is what was built and reopened.
        var held = new SealedFile?[outputs.Count];
        using DirectoryLease? owned = captured == null ? new() : null;
        DirectoryLease directories = captured ?? owned!;
        try
        {
            // Hold every destination ancestor before checks/hooks, and keep it through rollback. A path that passed a
            // link check must not be redirected while other outputs are being validated or installed.
            foreach (var (relative, _) in outputs)
            {
                token.ThrowIfCancellationRequested();
                directories.Parent(SourceProject.Resolve(destination, relative), create: true);
                directories.Parent(SourceProject.Resolve(staging, relative));
            }
            for (int i = 0; i < outputs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (relative, content) = outputs[i];
                fault?.Invoke("check", i);
                try { held[i] = SealedFile.Open(SourceProject.Resolve(staging, relative), content, directories); }
                catch (IOException ex) { throw new IOException($"The built {relative} was not installed: {ex.Message}", ex); }
            }
            for (int i = 0; i < outputs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var (relative, content) = outputs[i];
                string target = SourceProject.Resolve(destination, relative), saved = SourceProject.Resolve(backup, relative);
                fault?.Invoke("replace", i);
                if (directories.Exists(target) && !overwrite && replace?.Contains(relative) != true) throw new IOException($"{relative} appeared in {destination} during the export; nothing was replaced. Export again and allow replacing it.");
                if (directories.Exists(target))
                {
                    JournalDigest original;
                    using (FileStream source = directories.OpenFile(target, FileMode.Open, FileAccess.Read, FileShare.Read)) original = JournalDigest.Of(source, token);
                    // Verify again through a held handle before moving: the original cannot change between its digest and rename.
                    using SealedFile originalFile = SealedFile.Open(target, original, directories);
                    directories.Parent(saved, create: true); originalFile.MoveTo(saved);
                    steps.Add((target, saved, original, null));
                }
                else steps.Add((target, null, null, null));
                fault?.Invoke("install", i);
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
                    using SealedFile? restore = saved != null ? SealedFile.Open(saved, original!, directories) : null;
                    if (installed != null)
                    {
                        // Moved into the backup and deleted there only while it is the file this export installed.
                        string taken = Path.Combine(backup, ".removed", $"{i}-{Guid.NewGuid():N}.bin");
                        switch (SourcePublisher.MoveIfContent(target, taken, installed, directories, removeMoved: true))
                        {
                            case SourcePublisher.Moved.Done:
                                break;
                            case SourcePublisher.Moved.Stranded:
                                stranded.Add(taken); if (saved != null) unrestored.Add(target);
                                continue;
                            default:
                                if (Occupied(target)) { (saved != null ? unrestored : others).Add(target); continue; }
                                break;
                        }
                    }
                    if (saved != null) { if (Occupied(target)) unrestored.Add(target); else restore!.MoveTo(target); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { (saved == null ? leftover : Holds(saved, original) ? unrestored : installed != null && Occupied(target) ? kept : lost).Add(target); }
            }
            string notes = (leftover.Count > 0 ? $". New files that could not be removed: {string.Join(", ", leftover.Take(8))}" : "")
                + (others.Count > 0 ? $". Files another program wrote at the outputs' names during the export were left as they are: {string.Join(", ", others.Take(8))}" : "")
                + (stranded.Count > 0 ? $". Files that could not be removed while the export was undone were kept as {string.Join(", ", stranded.Take(8))}" : "")
                + (kept.Count > 0 ? $". The originals of {kept.Count} replaced files are no longer in {backup}, which another program changed, so the files at their names were left as they are: {string.Join(", ", kept.Take(8))}" : "")
                + (lost.Count > 0 ? $". The originals of {lost.Count} replaced files could not be restored because they are no longer in {backup}, which another program changed: {string.Join(", ", lost.Take(8))}" : "");
            // A canceled export stays a cancellation, with what it could not undo.
            string failed = failure is OperationCanceledException ? "The export was canceled" : $"Export failed ({failure.Message})";
            Exception Failure(string message) => failure is OperationCanceledException ? new OperationCanceledException(message, failure, token) : new IOException(message, failure);
            if (unrestored.Count > 0 || stranded.Count > 0)
                throw Failure(failed + (unrestored.Count > 0
                    ? $" and {unrestored.Count} previous files could not be restored; the originals remain in {backup}: {string.Join(", ", unrestored.Take(8))}"
                    : $"; {backup} is kept") + notes);
            // A backup another program changed is left to it.
            if (kept.Count == 0 && lost.Count == 0)
            {
                try { directories.DeleteTree(backup); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (notes.Length > 0) throw Failure(failed + notes);
            throw;
        }
        try { directories.DeleteTree(backup); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // Whether the backup still holds a replaced file as it was moved there.
        bool Holds(string saved, JournalDigest? original)
        {
            try { if (original == null) return false; using var file = SealedFile.Open(saved, original, directories); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        // An inaccessible or redirected name is never a vacancy. Its inspection must not stop rollback of other outputs.
        bool Occupied(string target)
        {
            try { return directories.Exists(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
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
        long remainingInput = FormatRegistry.MaximumDocumentBytes;
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
            byte[] bytes = snapshot.Read(input, token, ProjectReadLimits.Resource(remainingInput));
            remainingInput -= bytes.LongLength;
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
        return new(ArchiveSources.Write(entries), entries.Count, plan.Notes);
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
            var lines = GameGenScriptText.TokenizeCancellable(GameGenScriptText.Decode(source, token), token);
            if ((instructionCount += lines.Count) > maximumInstructions || (tokenCount += lines.Sum(l => (long)l.Count)) > maximumTokens)
                throw new InvalidDataException("The prepared scripts together exceed the instruction or token limit; split or simplify the sources.");
            int line = 0; List<ScriptInstruction> instructions = [];
            foreach (var tokens in lines)
            {
                token.ThrowIfCancellationRequested();
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
    internal static IReadOnlyList<string> MissionTextures(string root, string mission, IReadOnlyCollection<string>? added = null, CancellationToken token = default, Snapshot? snapshot = null,
        InventoryBudget? inventory = null)
    {
        inventory ??= snapshot?.Inventory ?? new();
        added = added == null ? null : PendingInventory.Capture(added, inventory, token);
        List<string> inputs = []; HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in TextureSources.MissionFolders(mission, Multiplayer(root, mission, snapshot, token)))
            foreach (string file in SourceProject.Files(root, folder, n => n.EndsWith(TextureSources.Extension, StringComparison.OrdinalIgnoreCase), added, token, inventory))
                // Subfolders of a search folder are separate folders (bft is listed on its own).
                if (Path.GetDirectoryName(file)!.Replace('\\', '/').Equals(folder, StringComparison.OrdinalIgnoreCase) && names.Add(Path.GetFileNameWithoutExtension(file))) inputs.Add(file);
        return inputs;
    }
    /// <summary>
    /// The texture memory a mission's textures need at full size in a Direct3D pack, estimated from the PNG headers of its
    /// folders as the build counts them (<see cref="TexturePackBuilder.StoredBytes"/> at the sizes the pack stores), without
    /// what only decoding and the world tell: alpha planes and the textures the world brings from other folders. A listing
    /// of the outputs names the automatic pack from it; an export of every output counts them all (<see cref="PackMemory"/>),
    /// and the build of a pack the profile promises at full size says which textures its budget reduced (see
    /// <see cref="BuildTexturePack"/>). Files not yet on disk are not counted.
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
                total += TexturePackBuilder.StoredBytes(w, h, false, false);
            }
        }
        return total;
    }
    /// <summary>
    /// The texture memory the textures of a mission's Direct3D pack need at full size, counted as its build counts them
    /// (<see cref="TexturePackBuilder.StoredBytes"/>): every texture the pack holds (<see cref="PackInputs"/>, with those the
    /// world brings from other folders) at the size the pack stores, with its alpha plane. Each PNG's transparency is learned
    /// by decoding it once for the run, as the first pack built would. When the world does not assemble or a texture does not
    /// decode, the headers' estimate stands (<see cref="TextureMemory"/>), and the build of those outputs reports why.
    /// </summary>
    internal static long PackMemory(string root, string mission, IReadOnlyList<string> textures, TexturePackVariant variant, Snapshot snapshot, CancellationToken token)
    {
        try
        {
            var (inputs, _, _) = PackInputs(new($"{mission}/{variant.FileName}", "textures", textures), snapshot, token);
            long total = 0;
            foreach (var (input, _) in inputs)
            {
                token.ThrowIfCancellationRequested();
                if (snapshot.Texture(input) is null) Decode(input, snapshot, token);
                var (width, height, transparency) = snapshot.Texture(input)!.Value;
                var (w, h) = TexturePackBuilder.Normalize(width, height, variant);
                total += TexturePackBuilder.StoredBytes(w, h, false, transparency == TextureTransparency.Alpha);
            }
            return total;
        }
        catch (Exception ex) when (IsBuildFailure(ex) && ex is not InventoryCapacityException) { return TextureMemory(root, textures, variant, token); }
    }
    /// <summary>A mission is multiplayer when its load script sources the shared multiplayer vehicle.</summary>
    internal static bool Multiplayer(string root, string mission, Snapshot? snapshot = null, CancellationToken token = default)
    {
        string relative = $"{SourceProject.GameGenFolder}/support/load{mission}.gw";
        byte[] bytes;
        if (snapshot != null)
        {
            if (!snapshot.Exists(relative)) return false;
            bytes = snapshot.Read(relative, token, SourceProject.MaximumSourceTextBytes);
        }
        else
        {
            string path = SourceProject.Resolve(root, relative);
            if (!File.Exists(path)) return false;
            bytes = SourceRead.All(path, SourceProject.MaximumSourceTextBytes, token);
        }
        foreach (var line in GameGenScriptText.TokenizeCancellable(GameGenScriptText.Decode(bytes, token), token))
            foreach (string operand in line)
            {
                token.ThrowIfCancellationRequested();
                if (operand.Equals("support\\bftmulti.gw", StringComparison.OrdinalIgnoreCase)) return true;
            }
        token.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>
    /// A mission texture pack at the variant's budget. Software packs keep direct colour where the retail packs do by rule
    /// (<see cref="Snapshot.DirectTextures"/>): the damage masks and the horizon's textures; every other texture is paletted.
    /// A pack the profile promises at full size (<see cref="SourceOutputPlan.FullSize"/>) says which textures its budget reduced.
    /// </summary>
    private static Built BuildTexturePack(SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        var variant = plan.Pack ?? TexturePackVariant.FromFileName(Path.GetFileName(plan.Path)) ?? throw new InvalidDataException($"{plan.Path} is not a texture pack name.");
        List<PackSource> textures = [];
        var (inputs, addressing, warnings) = PackInputs(plan, snapshot, token);
        CheckCount(plan.Path, inputs.Count);
        string mission = plan.Path.Split('/')[0];
        // Only the software renderer reads palettes; Direct3D packs are direct colour throughout.
        IReadOnlySet<string> direct = variant.Kind == TexturePackKind.Software ? snapshot.DirectTextures(mission, token) : new HashSet<string>();
        foreach (var (input, name) in inputs)
            textures.Add(Source(input, name, addressing.GetValueOrDefault(name), direct.Contains(name), snapshot, token));
        var built = TexturePackBuilder.BuildFromSources(textures, variant, token);
        // Counted as every pack's budget is (TexturePackBuilder.StoredBytes), with the textures the world brings from other
        // folders and alpha planes, the textures may need more than planning from the folders' PNG headers estimated.
        if (plan.FullSize && built.Reduced.Count > 0 && !(plan.Automatic && plan.Notes.Count > 0))
        {
            var shown = built.Reduced.Take(8).Select(r => $"{JsonData.ShownText(r.Name, 64)} ({r.Width} × {r.Height}, stored {r.StoredWidth} × {r.StoredHeight})");
            warnings.Add($"The {mission} textures need {(built.FullSizeBytes + (1 << 20) - 1) >> 20} MB at full size, counting those its world brings from other folders and their alpha planes, "
                + $"more than {variant.FileName} holds; they were reduced to fit: {string.Join(", ", shown)}{(built.Reduced.Count > 8 ? $" and {built.Reduced.Count - 8} more" : "")}."
                + (plan.Automatic ? "" : " Exporting this pack alone plans from the PNG headers of the mission's folders; an export of every output counts the textures as their build does and adds the larger automatic rtexture pack that holds them at full size."));
        }
        return new(built.Bytes, textures.Count, [.. plan.Notes, .. warnings, .. built.Warnings]);
    }
    /// <summary>
    /// A mission pack holds its texture folders and every texture its world uses from elsewhere: a model brought in
    /// from another mission names textures in that mission's folders, and the game only finds textures in its packs.
    /// A name already in the folders keeps the folder's image, as the engine finds the first match. A texture from
    /// elsewhere is stored under the name the world uses, which a model may give an image of another name. Each
    /// texture's edge mode (clamp word) comes from the glTF samplers that use it, or for a texture that only texture cycles
    /// show, from the material they show it on (see <see cref="Snapshot.TextureAddressing"/>).
    /// </summary>
    private static (List<(string Input, string Name)> Inputs, IReadOnlyDictionary<string, int> Addressing, List<string> Warnings) PackInputs(SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        List<(string Input, string Name)> inputs = [.. plan.Inputs.Select(input => (input, TextureName(input)))];
        List<string> warnings = [];
        string mission = plan.Path.Split('/')[0];
        if (!snapshot.HasWorld(mission)) return (inputs, new Dictionary<string, int>(), warnings);
        var world = snapshot.World(mission, token);
        HashSet<string> names = new(inputs.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, file) in world.TextureFiles.OrderBy(t => t.Key, StringComparer.Ordinal))
            if (names.Add(name)) inputs.Add((file, name.ToLowerInvariant()));
        return (inputs, snapshot.TextureAddressing(mission, token), warnings);
    }

    /// <summary>
    /// A mission's animations (see <see cref="Animation.AnimationCompiler"/>), bound to the world this export builds:
    /// definitions whose root the world lacks are left out, and names the game could not resolve (world nodes, and
    /// effect templates that the effects.zrd the game reads does not define: the common one when data/common has one,
    /// otherwise the mission's) prevent publication.
    /// </summary>
    private static Built BuildAnimations(string root, SourceOutputPlan plan, Snapshot snapshot, CancellationToken token)
    {
        string mission = plan.Path.Split('/')[0];
        List<string> warnings = [];
        IReadOnlyCollection<string>? nodes = null;
        if (snapshot.HasWorld(mission)) nodes = snapshot.World(mission, token).World.Nodes.Select(n => n.Name).ToArray();
        else warnings.Add($"{mission} has no world script ({WorldScript(mission)}), so animation roots and node names are not checked and patterns do not expand.");
        // Without an effects.zrd in the project the game's own resource archives supply it, so nothing can be checked.
        var effects = snapshot.Effects(mission, token);
        var result = Animation.AnimationCompiler.Compile(snapshot.Files(), AnimationRoot(mission), nodes, effects, token);
        if (result.EngineRejection is { } rejection) throw new InvalidDataException(rejection);
        return new(result.Bytes, result.Package.Entries.Count - 1, [.. warnings, .. result.Warnings]) { Package = result.Package };
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
        var declared = snapshot.Exists(SoundDefinitions) ? DeclaredFormats(snapshot.Read(SoundDefinitions, token, ProjectReadLimits.Resource()), token) : new();
        BoundedDiagnostics warnings = new(); List<ArchiveSources.Entry> entries = []; Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
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
        var messages = warnings.Snapshot();
        // GUI and MCP preview only the first few warnings; disclose aggregate omissions there too.
        if (messages.Remove(BoundedDiagnostics.OmissionNotice)) messages.Insert(0, BoundedDiagnostics.OmissionNotice);
        return new(ArchiveSources.Write(entries), entries.Count, messages);
    }

    /// <summary>
    /// HIGH/MED/LOW formats per WAV file from sounds.zrd, whose sound rows read
    /// <c>( id file flags… HIGH ( rate bits channels ) MED ( … ) LOW ( … ) )</c>. The first declaration of a file wins.
    /// </summary>
    internal static Dictionary<string, WaveFormat[]> DeclaredFormats(byte[] definitions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ProjectReadLimits.Resource().Validate(definitions);
        var root = ZrdText.LooksLikeText(definitions) ? ZrdText.Parse(definitions, token) : ZrdDecoder.Read(definitions, token);
        Dictionary<string, WaveFormat[]> formats = new(StringComparer.OrdinalIgnoreCase);
        Visit(root, 0);
        return formats;
        void Visit(ZrdNode node, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (node.Kind != ZrdKind.Array) return;
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
