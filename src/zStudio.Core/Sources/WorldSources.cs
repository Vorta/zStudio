using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Reconstructs model sources from shipped worlds: each mission database (<c>data/mN/models/mN.gltf</c>), each file the
/// build scripts loaded, and each OpenFlight external reference (a node named <c>*.flt</c>) as its own file that the
/// referencing node names in <c>extras.recoil.ref</c>. Files go where the original tree had them as far as the scripts
/// show (docs/recoil-original-worktree.md): a file whose script chose its folder (<c>SetModelDirectory</c>) goes there,
/// a file that a script several missions run loads identically (the pickups) goes to <c>data/common/models</c>, and a
/// file a mission's own scripts load to that mission's <c>models</c> folder, a copy per mission. A vehicle that several
/// missions' vehicle scripts load identically from their own folders is one file in <c>data/common/models</c> instead
/// (see <c>ShareVehicles</c>), and effects are kept with their textures in <c>data/common/effects/models</c> (see
/// <c>GatherEffects</c>). An external reference is written beside every file that references it, so missions
/// sharing one keep a copy each, as they do textures.
/// </summary>
internal static partial class WorldSources
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^data/m(\d+)/", System.Text.RegularExpressions.RegexOptions.CultureInvariant)] private static partial System.Text.RegularExpressions.Regex MissionFolder();
    public const string CommonModels = "data/common/models", CommonEffectsModels = "data/common/effects/models", EffectsModels = "data/effects/models";
    internal const long MaximumReferenceVisits = 4_194_304;
    internal sealed record MissionWorld(int Mission, GameZWorld World);
    internal sealed record Output(string Path, byte[] Bytes);
    /// <summary>A file to write: its content roots, the zone they inherit and the missions that use it.</summary>
    private sealed class Unit(string stem, string hash, IReadOnlyList<WorldNode> content, uint zone)
    {
        /// <summary>The texture folders the first load using this file searched, most recent first.</summary>
        public IReadOnlyList<string> TextureDirectories { get; set; } = [];
        /// <summary>A root the script loaded this file into without changing its flags; its flags are the file's.</summary>
        public WorldNode? LoadRoot { get; set; }
        public string Stem { get; } = stem; public string Hash { get; } = hash; public IReadOnlyList<WorldNode> Content { get; } = content; public uint Zone { get; } = zone;
        public SortedSet<int> Missions { get; } = [];
        public string? Folder { get; set; }
        public string? Path { get; set; }
        public string? NeutralHash { get; set; }
    }

    /// <summary>
    /// An OpenFlight external reference as the shipped world shows it: an object3d without geometry named for its file. A
    /// reference to a file without nodes has no children, but the loader cached and copied it like any other (see
    /// <see cref="OriginalLoader"/>), so the inference replays it and the reconstruction writes it, as the build reads it.
    /// </summary>
    public static bool IsReference(WorldNode node) => node.Class == WorldNodeClass.Object3D && node.Model == null && node.Name.EndsWith(".flt", StringComparison.OrdinalIgnoreCase);
    private static string Stem(string file) => System.IO.Path.GetFileNameWithoutExtension(file.Replace('\\', '/')).ToLowerInvariant();
    /// <summary>
    /// What tells a file's canonical content apart: the first 16 hex digits of the SHA-256 of its glTF JSON followed by its
    /// binary buffer, hashed as written rather than copied into one buffer.
    /// </summary>
    internal static string ContentHash(GltfDocument canonical)
    {
        var (json, bin) = canonical.Write("content.bin");
        CheckJsonLength(json);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(json); hash.AppendData(bin);
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <param name="textureFiles">Every texture source written (project paths).</param>
    /// <param name="transparency">How a texture source (project path) is transparent, so viewers draw its materials as the game does.</param>
    public static List<Output> Reconstruct(IReadOnlyList<MissionWorld> missions, Func<string, IReadOnlyList<IReadOnlyList<string>>?> scripts,
        Func<int, string, string?> texturePath, IReadOnlySet<string> textureFiles, Func<string, int> addressing, List<string> notes, CancellationToken token,
        Func<string, TextureTransparency?>? transparency = null, long maximumOutputBytes = ReconstructionBudget.MaximumBytes,
        long maximumTraceUnits = ScriptTraceBudget.MaximumUnits, long maximumOperandReferences = ScriptOperandBudget.MaximumReferences,
        long maximumReferenceVisits = MaximumReferenceVisits, long maximumPlacementUnits = WorldCopyBudget.MaximumUnits)
    {
        if (maximumReferenceVisits is < 0 or > MaximumReferenceVisits) throw new ArgumentOutOfRangeException(nameof(maximumReferenceVisits));
        long remainingReferenceVisits = maximumReferenceVisits;
        ReconstructionBudget outputBudget = new(maximumOutputBytes);
        WorldCopyBudget placement = new(maximumPlacementUnits, token);
        long MaximumKey<T>(IEnumerable<T> values, Func<T, long> length)
        {
            long maximum = 0;
            foreach (var value in values) { token.ThrowIfCancellationRequested(); maximum = Math.Max(maximum, length(value)); }
            return maximum;
        }
        ScriptTraceBudget traceBudget = new(maximumTraceUnits, maximumOperandReferences: maximumOperandReferences);
        BoundedDiagnostics diagnostics = new(notes);
        // The missions that run each script: a model a mission's own script loads belongs to that mission.
        Dictionary<string, SortedSet<int>> scriptMissions = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<WorldNode, string> hashes = new(ReferenceEqualityComparer.Instance);
        Dictionary<(string Stem, string Hash), Unit> references = [];
        Dictionary<WorldNode, (Unit Unit, int Mission)> referenceOf = new(ReferenceEqualityComparer.Instance);
        Dictionary<WorldNode, string> inlineSources = new(ReferenceEqualityComparer.Instance);
        Dictionary<(int Mission, string Id), IReadOnlyList<WorldNode>> canonicalParts = [];
        Dictionary<WorldNode, IReadOnlyList<WorldNode>> inlineContents = new(ReferenceEqualityComparer.Instance);
        List<(int Mission, LoadedModel Load, string Stem, string Hash)> traced = [];
        Dictionary<(string Folder, string Stem, string Hash), Unit> loadUnits = [];
        HashSet<WorldNode> roots = new(ReferenceEqualityComparer.Instance);
        HashSet<(WorldNode, int)> visited = [];
        // Recovered group records, embedded inferred load sections, and references that named a known file by another path.
        HashSet<WorldNode> groups = new(ReferenceEqualityComparer.Instance);
        Dictionary<WorldNode, IReadOnlyList<WorldNode>> parts = new(ReferenceEqualityComparer.Instance);
        Dictionary<IReadOnlyList<WorldNode>, HashSet<WorldNode>> partMembers = new(ReferenceEqualityComparer.Instance);
        HashSet<WorldNode>? PartMembers(WorldNode node)
        {
            if (!parts.TryGetValue(node, out var content)) return null;
            if (!partMembers.TryGetValue(content, out var members))
                partMembers[content] = members = new(content, ReferenceEqualityComparer.Instance);
            return members;
        }
        Dictionary<WorldNode, int> secondPaths = new(ReferenceEqualityComparer.Instance);
        IReadOnlyList<WorldNode> ContentOf(WorldNode reference) => parts.TryGetValue(reference, out var content) ? content : reference.Children;
        string? Spelled(WorldNode reference, string? uri) => uri == null ? null : string.Concat(Enumerable.Repeat("./", secondPaths.GetValueOrDefault(reference))) + uri;

        List<(int Mission, List<LoadedModel> Loads, List<WorldNode> DatabaseReferences)> decompositions = [];
        // The second paths each mission's database inference found in model files, with the loads it checked them on.
        List<(HashSet<(string LoadFile, string Reference, int Occurrence)> Paths, List<LoadedModel> Loads)> laterPaths = [];
        foreach (var mission in missions)
        {
            token.ThrowIfCancellationRequested();
            var trace = ScriptTrace.Trace(scripts, $"m{mission.Mission}.gs", notes, traceBudget, diagnostics, token);
            foreach (var instruction in trace)
            {
                if (!scriptMissions.TryGetValue(instruction.Script, out var runs)) scriptMissions[instruction.Script] = runs = [];
                runs.Add(mission.Mission);
            }
            var decomposition = WorldDecomposer.DecomposeAll(mission.World, trace, notes, token, diagnostics);
            var decomposed = decomposition.Loads.ToList();
            foreach (var load in decomposed) if (load.Root != null) roots.Add(load.Root);
            // The database in its file's record order, with the groups the build deleted.
            int database = decomposed.FindIndex(l => l.Database);
            List<WorldNode> databaseReferences = [];
            DatabaseRecords.Records? records = null;
            // A world the inference cannot replay (its caches exceed what a load can hold) keeps its order, like any other.
            try { if (database >= 0) records = DatabaseRecords.Infer(mission.World, decomposition, node => IsReference(node) && !roots.Contains(node), $"m{mission.Mission}", notes, token); }
            catch (InvalidDataException ex) { diagnostics.Add($"m{mission.Mission}: the mission database keeps the world's object order without its groups: {ex.Message}"); }
            if (records != null)
            {
                decomposed[database] = decomposed[database] with { Content = records.Roots };
                groups.UnionWith(records.Groups);
                foreach (var (reference, content) in records.Parts) parts[reference] = content;
                foreach (var (reference, identity) in records.InlineSources) inlineSources[reference] = identity;
                if (records.InlineSources.Count > 0)
                    diagnostics.Add($"m{mission.Mission}: original filenames for {records.InlineSources.Values.Distinct(StringComparer.Ordinal).Count()} inferred load sections are unrecoverable; their content and cache boundaries remain embedded in the mission master glTF.");
                foreach (var (reference, path) in records.SecondPaths) secondPaths[reference] = path;
                databaseReferences = DatabaseReferences(records.Roots);
                // The inference replayed the loads after the database (and the database) with these paths, the loads
                // before it without them; another mission's file of the name is another reading.
                if (records.LaterPaths.Count > 0)
                    laterPaths.Add(([.. records.LaterPaths], [.. decomposed.Where(l => l.Database || l.Root != null && l.Step > decomposed[database].Step)]));
            }
            decompositions.Add((mission.Mission, decomposed, databaseReferences));
        }
        // A model file that named another file by a second path does so where it is loaded or referenced in those loads.
        void Paths(string file, IReadOnlyList<WorldNode> records, HashSet<(string, string, int)> found)
        {
            foreach (var group in OwnReferences(records).GroupBy(r => r.Name.ToLowerInvariant()))
                for (int k = 1; k < group.Count(); k++)
                    if (found.Contains((file.ToLowerInvariant(), group.Key, k))) secondPaths[group.ElementAt(k)] = 1;
        }
        HashSet<WorldNode> pathsSeen = new(ReferenceEqualityComparer.Instance);
        void PathsWithin(WorldNode node, HashSet<(string, string, int)> found)
        {
            if (!pathsSeen.Add(node)) return;
            if (Reference(node) && !parts.ContainsKey(node)) Paths(node.Name, node.Children, found);
            foreach (var child in node.Children) PathsWithin(child, found);
        }
        foreach (var (found, checkedLoads) in laterPaths)
            foreach (var load in checkedLoads)
            {
                if (!load.Database) Paths(load.File, load.Content, found);
                foreach (var record in load.Content) PathsWithin(record, found);
            }
        foreach (var (mission, decomposed, databaseReferences) in decompositions)
        {
            // External references anywhere in loaded content, innermost first so their hashes are known.
            foreach (var load in decomposed)
                foreach (var node in load.Content)
                    Visit(node, mission, load.Instruction.TextureDirectories);
            // Retain the inferred original spelling: zone-only readings now share one physical file but still need
            // separate cache identities when the loader named that file through another path.
            // A root's flags are the file's only when the script left them alone, so they are not part of its identity.
            foreach (var load in decomposed) traced.Add((mission, load, Stem(load.File), Hash(load.Content, 0xFF)));
        }
        // Only an evidenced external filename creates a file. Deleted groups may retain a later cache's old name;
        // they and unnamed inferred sections are never external-file evidence.
        bool Reference(WorldNode node) => IsReference(node) && !roots.Contains(node) && !groups.Contains(node);
        // A file's references in record order, not looking inside their content.
        List<WorldNode> OwnReferences(IReadOnlyList<WorldNode> records)
        {
            List<WorldNode> found = []; int visits = 0;
            void Walk(WorldNode node)
            {
                ReferenceVisit(ref visits);
                if (Reference(node)) { found.Add(node); return; }
                foreach (var child in node.Children) Walk(child);
            }
            foreach (var record in records) Walk(record);
            return found;
        }
        // The database's model references in record order, not looking inside its parts' content.
        List<WorldNode> DatabaseReferences(IReadOnlyList<WorldNode> records)
        {
            List<WorldNode> found = []; int visits = 0;
            void Walk(WorldNode node)
            {
                ReferenceVisit(ref visits);
                if (PartMembers(node) is { } content) { foreach (var child in node.Children) if (!content.Contains(child)) Walk(child); return; }
                if (Reference(node)) { found.Add(node); return; }
                foreach (var child in node.Children) Walk(child);
            }
            foreach (var record in records) Walk(record);
            return found;
        }
        void ReferenceVisit(ref int visits)
        {
            token.ThrowIfCancellationRequested();
            // These walks count occurrences in authored order, not distinct nodes: repeated references carry
            // separate cache-path meaning. Bound the expansion before traversing/copying it, including inference
            // fallbacks, and share the allowance across all missions and files in this reconstruction.
            if (visits >= WorldGltf.MaximumExportedNodes || remainingReferenceVisits == 0)
                throw new InvalidDataException("Reconstructed model references exceed the expanded reference traversal allowance. Reduce shared-node paths or split the source operation into fewer missions.");
            visits++; remainingReferenceVisits--;
        }
        void Visit(WorldNode node, int mission, IReadOnlyList<string> textureDirectories)
        {
            // A node under several parents is visited once per mission; the hierarchy is bounded by the reader.
            if (!visited.Add((node, mission))) return;
            // Inline sections remain in this owner's hierarchy. Every actual external reference below them still
            // supplies file evidence and retains its occurrence-specific assignments.
            if (inlineSources.TryGetValue(node, out string? identity))
            {
                var sectionKey = (mission, identity);
                if (!canonicalParts.TryGetValue(sectionKey, out var canonical)) canonicalParts[sectionKey] = canonical = parts[node];
                inlineContents[node] = canonical;
            }
            foreach (var child in InlineChildren(node)) Visit(child, mission, textureDirectories);
            if (!Reference(node) || referenceOf.ContainsKey(node)) return;
            var content = ContentOf(node);
            string hash = Hash(content, node.Zone & 0xFF);
            var key = (Stem(node.Name), hash);
            if (!references.TryGetValue(key, out var unit)) references[key] = unit = new(key.Item1, hash, content.ToList(), node.Zone & 0xFF) { TextureDirectories = textureDirectories };
            unit.NeutralHash ??= Hash(content, node.Zone & 0xFF, neutral: true);
            unit.Missions.Add(mission); referenceOf[node] = (unit, mission);
        }
        string Hash(IReadOnlyList<WorldNode> content, uint zone, bool neutral = false)
        {
            WorldGltf.ExportContext context = new()
            {
                Token = token,
                Texture = t => ($"texture:{t.Name}", addressing(t.Name)),
                Reference = n => referenceOf.TryGetValue(n, out var r) ? Spelled(n, $"ref:{r.Unit.Stem}:{(neutral ? r.Unit.NeutralHash : r.Unit.Hash)}") : null,
                Content = n => parts.TryGetValue(n, out var c) ? c : null,
                InlineSource = n => inlineSources.GetValueOrDefault(n),
                InlineContent = n => inlineContents.GetValueOrDefault(n),
                Group = groups.Contains,
                Canonical = true,
            };
            var zoned = neutral ? WorldGltf.ExportZoned(content, zone, context) : null;
            var doc = zoned?.Geometry ?? WorldGltf.Export(content, zone, context);
            if (zoned != null)
            {
                var nodes = doc.AllNodes().ToArray();
                foreach (var (index, uri) in zoned.References)
                {
                    var extras = (JsonObject)nodes[index].Extras![WorldGltf.Key]!;
                    extras.Remove(WorldGltf.ZoneReference); extras["ref"] = uri;
                }
            }
            return ContentHash(doc);
        }
        IEnumerable<WorldNode> InlineChildren(WorldNode node)
        {
            if (!inlineContents.TryGetValue(node, out var content)) return node.Children;
            var members = PartMembers(node)!;
            return content.Concat(node.Children.Where(c => !members.Contains(c)));
        }

        // Loaded files are found through the model directories. A file goes where its script pointed them; otherwise a
        // file every mission running a shared script loads identically goes to common, and the rest to each mission.
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> nextSuffix = new(StringComparer.OrdinalIgnoreCase);
        List<(int Mission, LoadedModel Load, Unit Unit)> loads = [];
        // Sharing is a fact about all loads of one stem by one script, not a scan to repeat for every load.
        // Keep the original identity rules: normalized stems compare ordinally, script paths without case,
        // and only non-database loads with one content hash across several missions may share a file.
        var sharedLoads = traced.Where(t => !t.Load.Database).GroupBy(t => t.Stem, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.GroupBy(t => t.Load.Instruction.Script, StringComparer.OrdinalIgnoreCase)
                .Where(s => s.Select(t => t.Hash).Distinct(StringComparer.Ordinal).Take(2).Count() == 1
                    && s.Select(t => t.Mission).Distinct().Take(2).Count() > 1)
                .Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        HashSet<(string Folder, string Stem)> placedNames = [];
        // Loads whose folder is known come first, so a load without one can use an identical file its search finds.
        foreach (var (mission, load, stem, hash) in traced.OrderBy(t => t.Load.Database || t.Load.Instruction.ScriptModelDirectory != null ? 0 : 1))
        {
            token.ThrowIfCancellationRequested();
            string script = load.Instruction.Script;
            bool shared = !load.Database && scriptMissions.TryGetValue(script, out var runs) && runs.Count > 1
                && sharedLoads.TryGetValue(stem, out var sharingScripts) && sharingScripts.Contains(script);
            string? found = load.Database || load.Instruction.ScriptModelDirectory != null ? null
                : load.Instruction.ModelDirectories.FirstOrDefault(d => placedNames.Contains((d, stem)));
            string folder = load.Database ? $"data/m{mission}/models"
                : load.Instruction.ScriptModelDirectory ?? (found != null && loadUnits.ContainsKey((found, stem, hash)) ? found : shared ? CommonModels : $"data/m{mission}/models");
            var key = (folder, stem, hash);
            if (!loadUnits.TryGetValue(key, out var unit)) loadUnits[key] = unit = new(stem, hash, load.Content, 0xFF) { Folder = folder, TextureDirectories = load.Instruction.TextureDirectories };
            placedNames.Add((folder, stem));
            if (!load.RootEdited) unit.LoadRoot ??= load.Root;
            unit.Missions.Add(mission); loads.Add((mission, load, unit));
        }
        // Relocation decisions query by stem/folder, and retarget only the occurrences of the moved units.
        // Do not scan every unrelated file and load for each vehicle or effect.
        var unitsByStem = loadUnits.Values.GroupBy(u => u.Stem, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.GroupBy(u => u.Folder!, StringComparer.OrdinalIgnoreCase).ToDictionary(f => f.Key,
                f => f.ToHashSet(), StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        var loadIndices = loads.Select((load, index) => (load.Unit, Index: index)).GroupBy(l => l.Unit)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Index).ToList());
        ShareVehicles();
        GatherEffects();
        HashSet<Unit> pickupUnits = [.. loads.Where(l => WorldGltf.IsPickupName(l.Load.NodeName)).Select(l => l.Unit)];
        placement.Sort(loadUnits.Count, MaximumKey(loadUnits.Values, u => (long)u.Folder!.Length + u.Stem.Length + 12));
        foreach (var unit in loadUnits.Values.OrderBy(u => u.Folder, StringComparer.Ordinal).ThenBy(u => u.Stem, StringComparer.Ordinal).ThenBy(u => u.Missions.Min))
            Place(unit, unit.Folder!);
        // The names loads find their files by, in the folders each searches up to its own file's: a reference's copy (found
        // by its path) takes none of them, where it would shadow a load's file (such as a vehicle moved to data/common/models
        // that a mission's own vehicle folder references).
        HashSet<string> reserved = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, load, unit) in loads)
            foreach (string directory in load.Instruction.ModelDirectories)
            {
                reserved.Add(placement.Path(directory, Stem(load.File)));
                if (directory.Equals(unit.Folder, StringComparison.OrdinalIgnoreCase)) break;
            }

        // External references are found by path from the file that names them. Each goes beside every file that
        // references it (following references of references), with the texture folders that file searched.
        Dictionary<Unit, List<Unit>> referenced = new(ReferenceEqualityComparer.Instance);
        List<Unit> Referenced(Unit owner)
        {
            placement.Reserve(0, 1);
            if (referenced.TryGetValue(owner, out var list)) return list;
            placement.Reserve(128L + 32L * owner.Content.Count);
            list = []; HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); HashSet<Unit> units = new(ReferenceEqualityComparer.Instance);
            Stack<WorldNode> pending = new(owner.Content);
            while (pending.Count > 0)
            {
                placement.Reserve(0, 1);
                var node = pending.Pop();
                placement.Reserve(64);
                if (!seen.Add(node)) continue;
                if (referenceOf.TryGetValue(node, out var r))
                {
                    placement.Reserve(64);
                    if (units.Add(r.Unit)) list.Add(r.Unit);
                    // A reference to a part keeps records of its own in the referencing file.
                    if (PartMembers(node) is { } content) foreach (var child in node.Children)
                    {
                        placement.Reserve(0, 1);
                        if (!content.Contains(child)) { placement.Reserve(16); pending.Push(child); }
                    }
                    continue;
                }
                foreach (var child in InlineChildren(node)) { placement.Reserve(16, 1); pending.Push(child); }
            }
            return referenced[owner] = list;
        }
        Dictionary<(Unit Unit, string Folder), IReadOnlyList<string>> copies = [];
        Queue<(Unit Unit, string Folder)> spread = new();
        void QueueCopy(Unit unit, string folder, IReadOnlyList<string> textures)
        {
            placement.Lookup(folder);
            if (copies.ContainsKey((unit, folder))) return;
            placement.Pair(folder);
            // First enqueue is also the first dequeue in the original FIFO: keep its texture context.
            copies.Add((unit, folder), textures);
            spread.Enqueue((unit, folder));
        }
        placement.Sort(loadUnits.Count, MaximumKey(loadUnits.Values, u => (long)u.Path!.Length));
        foreach (var unit in loadUnits.Values.OrderBy(u => u.Path, StringComparer.Ordinal))
            foreach (var r in Referenced(unit)) QueueCopy(r, unit.Folder!, unit.TextureDirectories);
        while (spread.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (unit, folder) = spread.Dequeue();
            foreach (var r in Referenced(unit)) QueueCopy(r, folder, copies[(unit, folder)]);
        }
        Dictionary<(Unit Unit, string Folder), string> copyPaths = [];
        Dictionary<string, Unit> physicalUnits = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<(string Folder, string Stem, string Hash), string> neutralPaths = [];
        HashSet<string> referenceStems = references.Values.Select(u => u.Stem).ToHashSet(StringComparer.Ordinal);
        foreach (var loadedUnit in loadUnits.Values)
        {
            physicalUnits.Add(loadedUnit.Path!, loadedUnit);
            if (!referenceStems.Contains(loadedUnit.Stem)) continue;
            loadedUnit.NeutralHash ??= Hash(loadedUnit.Content, loadedUnit.Zone, neutral: true);
            neutralPaths.TryAdd((loadedUnit.Folder!, loadedUnit.Stem, loadedUnit.NeutralHash), loadedUnit.Path!);
        }
        placement.Sort(copies.Count, MaximumKey(copies.Keys, c => (long)c.Folder.Length + c.Unit.Stem.Length + c.Unit.Hash.Length));
        foreach (var (unit, folder) in copies.Keys.OrderBy(c => c.Folder, StringComparer.Ordinal).ThenBy(c => c.Unit.Stem, StringComparer.Ordinal).ThenBy(c => c.Unit.Hash, StringComparer.Ordinal))
        {
            placement.Lookup(folder);
            // Zone assignments belong to placements, not extra invented original files. Distinct original stems
            // remain distinct physical sources even if every geometry byte agrees.
            var identity = (folder, unit.Stem, unit.NeutralHash!);
            if (neutralPaths.TryGetValue(identity, out string? same)) { copyPaths[(unit, folder)] = same; continue; }
            string path = ClaimPath(folder, unit.Stem, reserved);
            copyPaths[(unit, folder)] = path; neutralPaths.Add(identity, path); physicalUnits.Add(path, unit);
        }
        // Each load finds its own file among every file written.
        foreach (var (mission, load, unit) in loads)
        {
            string? resolved = load.Instruction.ModelDirectories.Select(d => placement.Path(d, Stem(load.File))).FirstOrDefault(taken.Contains);
            if (resolved == null) diagnostics.Add($"m{mission}: {load.File} is written to {unit.Path}, which the build does not search when it loads {load.NodeName}.");
            else if (!resolved.Equals(unit.Path, StringComparison.OrdinalIgnoreCase)) diagnostics.Add($"m{mission}: {load.File} ({load.NodeName}) resolves to {resolved}, not to its own version {unit.Path}.");
        }
        HashSet<Unit> copied = new(copies.Keys.Select(k => k.Unit), ReferenceEqualityComparer.Instance);
        foreach (var unit in references.Values.Where(u => !copied.Contains(u)).OrderBy(u => u.Stem, StringComparer.Ordinal))
            diagnostics.Add($"External reference {unit.Stem}.flt is used by no loaded file and was not written.");
        // The campaign's vehicle scripts (bft1.gw–bft6.gw) each set their mission's own folder and load the same enemy files.
        // Worlds record no file paths, a file in data/common/models (which those loads search too) builds the same world,
        // and the packs show the vehicles' textures were one shared copy in data/common/textures. So the version of a file
        // that most missions' script folders hold identically becomes one file there (user decision, 2026-10-02); another
        // version stays in its mission's folder, which its loads search first. Each load must still find the shared file:
        // it searches data/common/models, no folder before it holds another file of the name, and the file's own flags agree.
        void ShareVehicles()
        {
            var loadsOf = LoadsOf();
            var vehicles = loadUnits.Where(u => MissionFolder().IsMatch(u.Key.Folder) && loadsOf[u.Value].Any(l => u.Key.Folder.Equals(l.Instruction.ScriptModelDirectory, StringComparison.OrdinalIgnoreCase)));
            foreach (var byStem in vehicles.GroupBy(u => u.Key.Stem).OrderBy(g => g.Key, StringComparer.Ordinal).ToList())
            {
                token.ThrowIfCancellationRequested();
                var version = byStem.GroupBy(u => u.Key.Hash).Select(g => g.Select(u => u.Value).ToList())
                    .OrderByDescending(g => g.Count).ThenBy(g => g.Min(u => u.Missions.Min)).First();
                if (version.Count < 2 || version.Where(u => u.LoadRoot != null).Select(u => u.LoadRoot!.Flags & WorldGltf.CarriedFlags).Distinct().Count() > 1) continue;
                MoveIfFound(version, CommonModels, loadsOf);
            }
        }
        // The effect textures came from data/common/effects/textures (the packs' record order), although weapons.gw sets
        // data/effects/textures before naming the damage masks, and no packed texture came from data/effects. So weapons.gw's
        // own data/effects/models does not show where its models were either: they are kept with their textures in
        // data/common/effects/models, which the same loads search (user decision, 2026-10-03), and data/effects gets no files.
        void GatherEffects()
        {
            var loadsOf = LoadsOf();
            foreach (var unit in loadUnits.Where(u => u.Key.Folder.Equals(EffectsModels, StringComparison.OrdinalIgnoreCase)).OrderBy(u => u.Key.Stem, StringComparer.Ordinal)
                .ThenBy(u => u.Key.Hash, StringComparer.Ordinal).Select(u => u.Value).ToList())
            {
                token.ThrowIfCancellationRequested();
                MoveIfFound([unit], CommonEffectsModels, loadsOf);
            }
        }
        Dictionary<Unit, List<LoadedModel>> LoadsOf() => loads.GroupBy(l => l.Unit).ToDictionary(g => g.Key, g => g.Select(l => l.Load).ToList());
        // Makes identical units one file in another folder when every load still finds it there: no file of the name is
        // there yet, each load searches the folder, and no folder it searches before holds another file of the name.
        void MoveIfFound(IReadOnlyList<Unit> units, string folder, Dictionary<Unit, List<LoadedModel>> loadsOf)
        {
            string stem = units[0].Stem;
            var namedFolders = unitsByStem[stem];
            if (namedFolders.ContainsKey(folder)) return;
            HashSet<Unit> moving = [.. units];
            bool Finds(LoadedModel load)
            {
                foreach (string directory in load.Instruction.ModelDirectories)
                {
                    if (directory.Equals(folder, StringComparison.OrdinalIgnoreCase)) return true;
                    if (namedFolders.TryGetValue(directory, out var existing) && existing.Any(u => !moving.Contains(u))) return false;
                }
                return false;
            }
            if (!units.SelectMany(u => loadsOf[u]).All(Finds)) return;
            var first = units.MinBy(u => u.Missions.Min)!;
            Unit moved = new(stem, first.Hash, first.Content, first.Zone)
            {
                Folder = folder, TextureDirectories = first.TextureDirectories, LoadRoot = units.Select(u => u.LoadRoot).FirstOrDefault(r => r != null),
            };
            List<int> indices = [];
            foreach (var unit in units)
            {
                moved.Missions.UnionWith(unit.Missions); loadUnits.Remove((unit.Folder!, stem, unit.Hash));
                var existing = namedFolders[unit.Folder!]; existing.Remove(unit);
                if (existing.Count == 0) namedFolders.Remove(unit.Folder!);
                foreach (int index in loadIndices[unit]) { loads[index] = (loads[index].Mission, loads[index].Load, moved); indices.Add(index); }
                loadIndices.Remove(unit);
            }
            loadUnits[(folder, stem, moved.Hash)] = moved;
            namedFolders[folder] = [moved]; loadIndices[moved] = indices;
        }
        void Place(Unit unit, string folder)
        {
            unit.Folder = folder; unit.Path = ClaimPath(folder, unit.Stem, null);
        }
        string ClaimPath(string folder, string stem, HashSet<string>? reservedPaths)
        {
            string original = placement.Path(folder, stem), path = original;
            int suffix = nextSuffix.GetValueOrDefault(original, 2);
            while (reservedPaths?.Contains(path) == true || !taken.Add(path))
            {
                token.ThrowIfCancellationRequested();
                path = placement.Path(folder, stem, suffix++);
            }
            // Taken and reserved names only grow while allocating paths, so an earlier rejected suffix
            // cannot become available later. Every candidate still checks all authored and generated names.
            nextSuffix[original] = suffix;
            return path;
        }

        List<Output> outputs = [];
        HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<Unit, WorldZoneProfile> profiles = new(ReferenceEqualityComparer.Instance);
        Dictionary<Unit, Dictionary<int, Unit>> profileReferences = new(ReferenceEqualityComparer.Instance);
        WorldZoneProfile Profile(Unit unit)
        {
            if (profiles.TryGetValue(unit, out var cached)) return cached;
            var exported = WorldGltf.ExportZoned(unit.Content, unit.Zone, new()
            {
                Token = token, Texture = t => ($"texture:{t.Name}", addressing(t.Name)),
                Reference = n => referenceOf.TryGetValue(n, out var r) ? Spelled(n, $"ref:{r.Unit.Stem}") : null,
                Content = n => parts.TryGetValue(n, out var c) ? c : null,
                InlineSource = n => inlineSources.GetValueOrDefault(n), Group = groups.Contains,
                InlineContent = n => inlineContents.GetValueOrDefault(n),
            });
            placement.Reserve(128L + 32L * exported.Profile.Nodes.Count);
            foreach (var mesh in exported.Profile.MeshPolygons) placement.Reserve(4L * mesh.Count);
            return profiles[unit] = exported.Profile;
        }
        Dictionary<int, Unit> ProfileReferences(Unit unit)
        {
            if (profileReferences.TryGetValue(unit, out var found)) return found;
            Dictionary<int, Unit> result = []; int ordinal = 0, visits = 0;
            void Walk(WorldNode node)
            {
                ReferenceVisit(ref visits); int index = ordinal++;
                if (referenceOf.TryGetValue(node, out var reference))
                {
                    placement.Reserve(64); result.Add(index, reference.Unit);
                    if (PartMembers(node) is { } members)
                        foreach (var child in node.Children) if (!members.Contains(child)) Walk(child);
                    return;
                }
                foreach (var child in InlineChildren(node)) Walk(child);
            }
            foreach (var root in unit.Content) Walk(root);
            return profileReferences[unit] = result;
        }
        JsonObject? Override(Unit actual, Unit baseline, int depth = 0)
        {
            token.ThrowIfCancellationRequested(); placement.Reserve(0, 1);
            if (ReferenceEquals(actual, baseline)) return null;
            if (depth >= GltfDocument.MaximumDepth) throw new InvalidDataException("Reconstructed source overrides exceed the reference depth allowance.");
            var profile = Profile(actual); var original = Profile(baseline);
            if (profile.Fingerprint != original.Fingerprint)
                throw new InvalidDataException($"{actual.Stem}.flt: placement attributes cannot be associated with the original file's polygon identities.");
            // An inherited node has no authored ID: each placement supplies that byte. Its
            // padding and gate still belong to the source and must participate in comparison.
            bool same = profile.Nodes.Count == original.Nodes.Count && profile.Nodes.Zip(original.Nodes).All(p =>
                p.First.Gate == p.Second.Gate && p.First.Inherit == p.Second.Inherit
                && (p.First.Inherit ? (p.First.Word & ~255u) == (p.Second.Word & ~255u) : p.First.Word == p.Second.Word))
                && profile.MeshPolygons.Count == original.MeshPolygons.Count
                && profile.MeshPolygons.Zip(original.MeshPolygons).All(p => p.First.SequenceEqual(p.Second));
            JsonObject children = [];
            var baselineReferences = ProfileReferences(baseline);
            foreach (var (index, referenced) in ProfileReferences(actual))
            {
                if (!baselineReferences.TryGetValue(index, out var originalReference))
                    throw new InvalidDataException($"{actual.Stem}.flt: a placement reference has no matching original source node.");
                if (Override(referenced, originalReference, depth + 1) is { } child)
                    children[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = child;
            }
            if (same && children.Count == 0) return null;
            // Reserve before serializing an entire profile; nested overrides share this reconstruction's allowance.
            placement.Reserve(256L + 96L * profile.Nodes.Count + profile.MeshPolygons.Sum(m => 16L * m.Count));
            var value = WorldGltf.WriteZoneOverride(profile, token);
            if (children.Count > 0) value["references"] = children;
            return value;
        }
        // Every evidenced original file retains a physical source, even when its geometry equals another file.
        placement.Sort(loadUnits.Count, MaximumKey(loadUnits.Values, u => (long)u.Path!.Length));
        foreach (var unit in loadUnits.Values.OrderBy(u => u.Path, StringComparer.Ordinal))
            Write(unit, unit.Folder!, unit.Path!, unit.TextureDirectories, unit.Missions.Min, unit.LoadRoot);
        placement.Sort(copyPaths.Count, MaximumKey(copyPaths.Values, p => (long)p.Length));
        foreach (var ((unit, folder), path) in copyPaths.OrderBy(c => c.Value, StringComparer.Ordinal))
        {
            placement.Lookup(folder);
            if (loadUnits.TryGetValue((folder, unit.Stem, unit.Hash), out var same) && same.Path == path) continue;
            // A copy in a mission's folder takes that mission's textures; one in a shared folder the first mission's.
            var match = MissionFolder().Match(folder);
            Write(unit, folder, path, copies[(unit, folder)], match.Success ? int.Parse(match.Groups[1].Value) : unit.Missions.Min, null);
        }
        void Write(Unit unit, string folder, string path, IReadOnlyList<string> textureDirectories, int mission, WorldNode? loadRoot)
        {
            token.ThrowIfCancellationRequested();
            if (!written.Add(path)) return;
            unit = physicalUnits[path];
            var zoned = WorldGltf.ExportZoned(unit.Content, unit.Zone, new()
            {
                Token = token,
                Texture = t =>
                {
                    // The image the build finds by name in the folders it searches, else the one the mission's packs held.
                    string target = textureDirectories.Select(d => $"{d}/{t.Name.ToLowerInvariant()}{TextureSources.Extension}").FirstOrDefault(textureFiles.Contains)
                        ?? texturePath(mission, t.Name) ?? unit.Missions.Select(m => texturePath(m, t.Name)).FirstOrDefault(p => p != null) ?? $"data/m{mission}/textures/{t.Name}{TextureSources.Extension}";
                    return (RelativeUri(folder, target), addressing(t.Name));
                },
                Reference = n => !referenceOf.TryGetValue(n, out var r) ? null
                    : copyPaths.TryGetValue((r.Unit, folder), out var target) ? Spelled(n, RelativeUri(folder, target))
                    : throw new InvalidDataException($"{path}: the evidenced original file {n.Name} has no reconstructed source."),
                Content = n => parts.TryGetValue(n, out var c) ? c : null,
                InlineSource = n => inlineSources.GetValueOrDefault(n),
                InlineContent = n => inlineContents.GetValueOrDefault(n),
                Group = groups.Contains,
            }, loadRoot);
            placement.Reserve(256L + 16L * zoned.Profile.Nodes.Count + 32L * zoned.Profile.MeshPolygons.Count);
            foreach (var mesh in zoned.Profile.MeshPolygons) placement.Reserve(4L * mesh.Count);
            foreach (var reference in zoned.References) placement.Reserve(128L + 8L * (path.Length + reference.Value.Length));
            var doc = zoned.Geometry;
            WorldGltf.EmbedZones(doc, zoned.Profile, zoned.References, token);
            var emittedNodes = doc.AllNodes().ToArray();
            foreach (var (index, referenced) in ProfileReferences(unit))
            {
                if (!copyPaths.TryGetValue((referenced, folder), out string? target))
                    throw new InvalidDataException($"{path}: a reconstructed external reference has no physical source file.");
                if (Override(referenced, physicalUnits[target]) is { } attributes)
                    ((JsonObject)emittedNodes[index].Extras![WorldGltf.Key]!)["zoneOverrides"] = attributes;
            }
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);
            var (writtenJson, bin) = doc.Write(stem + ".bin", token);
            CheckJsonLength(writtenJson);
            var root = (JsonObject)JsonNode.Parse(writtenJson)!;
            // Viewers show transparent textures and a pickup's collision volume hidden as the game does; builds read the same values.
            bool pickup = pickupUnits.Contains(unit);
            WorldGltf.ApplyPresentation(root, uri => transparency?.Invoke(WorldAssembler.Relative(path, uri)), pickup);
            byte[] json = GltfJson.Write(root, indented: true, token);
            CheckJsonLength(json);
            // Re-open exactly the bytes being published through the source reader, including its aggregate metadata,
            // accessor, hierarchy and buffer contracts. Only this document's emitted binary can resolve here;
            // external references and images remain source URIs and the glTF reader does not load those assets.
            _ = GltfDocument.Read(json, uri => uri == stem + ".bin" ? bin
                : throw new InvalidDataException($"{path}: reconstructed glTF unexpectedly references buffer {uri}."), token);
            outputBudget.Retain(json.LongLength + bin.LongLength);
            outputs.Add(new(path, json));
            outputs.Add(new($"{folder}/{stem}.bin", bin));
        }
        token.ThrowIfCancellationRequested();
        return outputs;
    }

    private static void CheckJsonLength(byte[] json)
    {
        if (json.Length > GltfDocument.MaximumJsonBytes)
            throw new InvalidDataException("The reconstructed glTF source exceeds the 32 MiB JSON budget supported by the source reader. Split the model into referenced files before reconstructing it.");
    }

    /// <summary>A URI from a folder to a project path, with forward slashes.</summary>
    internal static string RelativeUri(string fromFolder, string to)
    {
        var from = fromFolder.Split('/', StringSplitOptions.RemoveEmptyEntries); var target = to.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int common = 0; while (common < from.Length && common < target.Length - 1 && from[common].Equals(target[common], StringComparison.OrdinalIgnoreCase)) common++;
        return string.Join('/', Enumerable.Repeat("..", from.Length - common).Concat(target.Skip(common)));
    }
}
