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
    }

    /// <summary>
    /// An OpenFlight external reference as the shipped world shows it: an object3d without geometry named for its file. A
    /// reference to a file without nodes has no children, but the loader cached and copied it like any other (see
    /// <see cref="OriginalLoader"/>), so the inference replays it and the reconstruction writes it, as the build reads it.
    /// </summary>
    public static bool IsReference(WorldNode node) => node.Class == WorldNodeClass.Object3D && node.Model == null && node.Name.EndsWith(".flt", StringComparison.OrdinalIgnoreCase);
    private static string Stem(string file) => System.IO.Path.GetFileNameWithoutExtension(file.Replace('\\', '/')).ToLowerInvariant();

    /// <param name="textureFiles">Every texture source written (project paths).</param>
    /// <param name="transparency">How a texture source (project path) is transparent, so viewers draw its materials as the game does.</param>
    public static List<Output> Reconstruct(IReadOnlyList<MissionWorld> missions, Func<string, IReadOnlyList<IReadOnlyList<string>>?> scripts,
        Func<int, string, string?> texturePath, IReadOnlySet<string> textureFiles, Func<string, int> addressing, List<string> notes, CancellationToken token,
        Func<string, TextureTransparency?>? transparency = null)
    {
        // The missions that run each script: a model a mission's own script loads belongs to that mission.
        Dictionary<string, SortedSet<int>> scriptMissions = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<WorldNode, string> hashes = new(ReferenceEqualityComparer.Instance);
        Dictionary<(string Stem, string Hash), Unit> references = [];
        Dictionary<WorldNode, (Unit Unit, int Mission)> referenceOf = new(ReferenceEqualityComparer.Instance);
        // Each part's file by the name its references share (mN_NN.flt, unique to its mission).
        Dictionary<string, Unit> partUnits = new(StringComparer.OrdinalIgnoreCase);
        List<(int Mission, LoadedModel Load, string Hash)> traced = [];
        Dictionary<(string Folder, string Stem, string Hash), Unit> loadUnits = [];
        HashSet<WorldNode> roots = new(ReferenceEqualityComparer.Instance);
        HashSet<(WorldNode, int)> visited = [];
        // The group records of the mission databases, the references to their parts (files of their own) with the content
        // copied from each, and references that named their file by another path (see DatabaseRecords).
        HashSet<WorldNode> groups = new(ReferenceEqualityComparer.Instance);
        Dictionary<WorldNode, IReadOnlyList<WorldNode>> parts = new(ReferenceEqualityComparer.Instance);
        Dictionary<WorldNode, int> secondPaths = new(ReferenceEqualityComparer.Instance);
        IReadOnlyList<WorldNode> ContentOf(WorldNode reference) => parts.TryGetValue(reference, out var content) ? content : reference.Children;
        string? Spelled(WorldNode reference, string? uri) => uri == null ? null : string.Concat(Enumerable.Repeat("./", secondPaths.GetValueOrDefault(reference))) + uri;

        List<(int Mission, List<LoadedModel> Loads, List<WorldNode> DatabaseReferences)> decompositions = [];
        // The second paths each mission's database inference found in model files, with the loads it checked them on.
        List<(HashSet<(string LoadFile, string Reference, int Occurrence)> Paths, List<LoadedModel> Loads)> laterPaths = [];
        foreach (var mission in missions)
        {
            token.ThrowIfCancellationRequested();
            var trace = ScriptTrace.Trace(scripts, $"m{mission.Mission}.gs", notes);
            foreach (var instruction in trace)
            {
                if (!scriptMissions.TryGetValue(instruction.Script, out var runs)) scriptMissions[instruction.Script] = runs = [];
                runs.Add(mission.Mission);
            }
            var decomposition = WorldDecomposer.DecomposeAll(mission.World, trace, notes);
            var decomposed = decomposition.Loads.ToList();
            foreach (var load in decomposed) if (load.Root != null) roots.Add(load.Root);
            // The database in its file's record order, with the groups the build deleted.
            int database = decomposed.FindIndex(l => l.Database);
            List<WorldNode> databaseReferences = [];
            DatabaseRecords.Records? records = null;
            // A world the inference cannot replay (its caches exceed what a load can hold) keeps its order, like any other.
            try { if (database >= 0) records = DatabaseRecords.Infer(mission.World, decomposition, node => IsReference(node) && !roots.Contains(node), $"m{mission.Mission}", notes, token); }
            catch (InvalidDataException ex) { notes.Add($"m{mission.Mission}: the mission database keeps the world's object order without its groups: {ex.Message}"); }
            if (records != null)
            {
                decomposed[database] = decomposed[database] with { Content = records.Roots };
                groups.UnionWith(records.Groups);
                foreach (var (reference, content) in records.Parts) parts[reference] = content;
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
            // The build caches a file once per path that names it. Paths count per file written: the first reference to
            // another version of a file (written as a file of its own) is that file's first path, and the references that
            // copied one cache name it by one path.
            Dictionary<Unit, Dictionary<int, int>> paths = [];
            foreach (var reference in databaseReferences)
            {
                if (!referenceOf.TryGetValue(reference, out var named)) continue;
                if (!paths.TryGetValue(named.Unit, out var spelled)) paths[named.Unit] = spelled = [];
                int cache = secondPaths.GetValueOrDefault(reference);
                if (!spelled.TryGetValue(cache, out int path)) spelled[cache] = path = spelled.Count;
                if (path == 0) secondPaths.Remove(reference); else secondPaths[reference] = path;
            }
            // A root's flags are the file's only when the script left them alone, so they are not part of its identity.
            foreach (var load in decomposed) traced.Add((mission, load, Hash(load.Content, 0xFF)));
        }
        // A reference to a file: not a load's root, nor a group the build deleted unless it holds a part (such a group may
        // keep the name of a later cache, such as phone.flt in 1999 m6).
        bool Reference(WorldNode node) => IsReference(node) && !roots.Contains(node) && (!groups.Contains(node) || parts.ContainsKey(node));
        // A file's references in record order, not looking inside their content.
        List<WorldNode> OwnReferences(IReadOnlyList<WorldNode> records)
        {
            List<WorldNode> found = [];
            void Walk(WorldNode node) { if (Reference(node)) { found.Add(node); return; } foreach (var child in node.Children) Walk(child); }
            foreach (var record in records) Walk(record);
            return found;
        }
        // The database's model references in record order, not looking inside its parts' content.
        List<WorldNode> DatabaseReferences(IReadOnlyList<WorldNode> records)
        {
            List<WorldNode> found = [];
            void Walk(WorldNode node)
            {
                if (parts.TryGetValue(node, out var content)) { foreach (var child in node.Children) if (!content.Contains(child)) Walk(child); return; }
                if (Reference(node)) { found.Add(node); return; }
                foreach (var child in node.Children) Walk(child);
            }
            foreach (var record in records) Walk(record);
            return found;
        }
        void Visit(WorldNode node, int mission, IReadOnlyList<string> textureDirectories)
        {
            // A node under several parents is visited once per mission; the hierarchy is bounded by the reader.
            if (!visited.Add((node, mission))) return;
            // The references the inference found copying one part's cache (one name: the same names in the same shape) are
            // one file, written from the copy visited first, which is the earliest made (records in order; a copy is never
            // inside another copy of its own file). Another copy differs only where a script changed it after the load, and
            // the scripts' lookups (FindNode, FindSubNode) reach the newest node of a name first: a lookup reaches an
            // earlier copy only once the later ones have lost the name, and then the copies' names or shapes differ and
            // they are not one part. The other copies' content is not the file's, so it is not visited (its references
            // would otherwise become files no file uses); their own records are the referencing file's.
            bool laterCopy = parts.TryGetValue(node, out var copied) && Reference(node) && partUnits.ContainsKey(node.Name);
            HashSet<WorldNode>? notFile = laterCopy ? new(copied!, ReferenceEqualityComparer.Instance) : null;
            foreach (var child in node.Children) if (notFile == null || !notFile.Contains(child)) Visit(child, mission, textureDirectories);
            if (!Reference(node) || referenceOf.ContainsKey(node)) return;
            if (laterCopy) { var part = partUnits[node.Name]; part.Missions.Add(mission); referenceOf[node] = (part, mission); return; }
            var content = ContentOf(node);
            string hash = Hash(content, node.Zone & 0xFF);
            var key = (Stem(node.Name), hash);
            if (!references.TryGetValue(key, out var unit)) references[key] = unit = new(key.Item1, hash, content.ToList(), node.Zone & 0xFF) { TextureDirectories = textureDirectories };
            unit.Missions.Add(mission); referenceOf[node] = (unit, mission);
            if (parts.ContainsKey(node)) partUnits[node.Name] = unit;
        }
        string Hash(IReadOnlyList<WorldNode> content, uint zone)
        {
            var doc = WorldGltf.Export(content, zone, new()
            {
                Texture = t => ($"texture:{t.Name}", addressing(t.Name)),
                Reference = n => referenceOf.TryGetValue(n, out var r) ? Spelled(n, $"ref:{r.Unit.Stem}:{r.Unit.Hash}") : null,
                Content = n => parts.TryGetValue(n, out var c) ? [.. c] : null,
                Group = groups.Contains,
                Canonical = true,
            });
            var (json, bin) = doc.Write("content.bin");
            return Convert.ToHexStringLower(SHA256.HashData([.. json, .. bin]))[..16];
        }

        // Loaded files are found through the model directories. A file goes where its script pointed them; otherwise a
        // file every mission running a shared script loads identically goes to common, and the rest to each mission.
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        List<(int Mission, LoadedModel Load, Unit Unit)> loads = [];
        // Loads whose folder is known come first, so a load without one can use an identical file its search finds.
        foreach (var (mission, load, hash) in traced.OrderBy(t => t.Load.Database || t.Load.Instruction.ScriptModelDirectory != null ? 0 : 1))
        {
            string stem = Stem(load.File), script = load.Instruction.Script;
            var sameScript = traced.Where(t => !t.Load.Database && Stem(t.Load.File) == stem && t.Load.Instruction.Script.Equals(script, StringComparison.OrdinalIgnoreCase)).ToList();
            bool shared = !load.Database && scriptMissions.TryGetValue(script, out var runs) && runs.Count > 1
                && sameScript.Select(t => t.Hash).Distinct().Count() == 1 && sameScript.Select(t => t.Mission).Distinct().Count() > 1;
            string? found = load.Database || load.Instruction.ScriptModelDirectory != null ? null
                : load.Instruction.ModelDirectories.FirstOrDefault(d => loadUnits.Keys.Any(k => k.Folder == d && k.Stem == stem));
            string folder = load.Database ? $"data/m{mission}/models"
                : load.Instruction.ScriptModelDirectory ?? (found != null && loadUnits.ContainsKey((found, stem, hash)) ? found : shared ? CommonModels : $"data/m{mission}/models");
            var key = (folder, stem, hash);
            if (!loadUnits.TryGetValue(key, out var unit)) loadUnits[key] = unit = new(stem, hash, load.Content, 0xFF) { Folder = folder, TextureDirectories = load.Instruction.TextureDirectories };
            if (!load.RootEdited) unit.LoadRoot ??= load.Root;
            unit.Missions.Add(mission); loads.Add((mission, load, unit));
        }
        ShareVehicles();
        GatherEffects();
        foreach (var unit in loadUnits.Values.OrderBy(u => u.Folder, StringComparer.Ordinal).ThenBy(u => u.Stem, StringComparer.Ordinal).ThenBy(u => u.Missions.Min))
            Place(unit, unit.Folder!);
        // The names loads find their files by, in the folders each searches up to its own file's: a reference's copy (found
        // by its path) takes none of them, where it would shadow a load's file (such as a vehicle moved to data/common/models
        // that a mission's own vehicle folder references).
        HashSet<string> reserved = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, load, unit) in loads)
            foreach (string directory in load.Instruction.ModelDirectories)
            {
                reserved.Add($"{directory}/{Stem(load.File)}.gltf");
                if (directory.Equals(unit.Folder, StringComparison.OrdinalIgnoreCase)) break;
            }

        // External references are found by path from the file that names them. Each goes beside every file that
        // references it (following references of references), with the texture folders that file searched.
        Dictionary<Unit, List<Unit>> referenced = new(ReferenceEqualityComparer.Instance);
        List<Unit> Referenced(Unit owner)
        {
            if (referenced.TryGetValue(owner, out var list)) return list;
            list = []; HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); HashSet<Unit> units = new(ReferenceEqualityComparer.Instance);
            Stack<WorldNode> pending = new(owner.Content);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (!seen.Add(node)) continue;
                if (referenceOf.TryGetValue(node, out var r))
                {
                    if (units.Add(r.Unit)) list.Add(r.Unit);
                    // A reference to a part keeps records of its own in the referencing file.
                    if (parts.TryGetValue(node, out var content)) foreach (var child in node.Children.Where(c => !content.Contains(c))) pending.Push(child);
                    continue;
                }
                foreach (var child in node.Children) pending.Push(child);
            }
            return referenced[owner] = list;
        }
        Dictionary<(Unit Unit, string Folder), IReadOnlyList<string>> copies = [];
        Queue<(Unit Unit, string Folder, IReadOnlyList<string> Textures)> spread = new();
        foreach (var unit in loadUnits.Values.OrderBy(u => u.Path, StringComparer.Ordinal))
            foreach (var r in Referenced(unit)) spread.Enqueue((r, unit.Folder!, unit.TextureDirectories));
        while (spread.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (unit, folder, textures) = spread.Dequeue();
            if (!copies.TryAdd((unit, folder), textures)) continue;
            foreach (var r in Referenced(unit)) spread.Enqueue((r, folder, textures));
        }
        Dictionary<(Unit Unit, string Folder), string> copyPaths = [];
        foreach (var (unit, folder) in copies.Keys.OrderBy(c => c.Folder, StringComparer.Ordinal).ThenBy(c => c.Unit.Stem, StringComparer.Ordinal).ThenBy(c => c.Unit.Hash, StringComparer.Ordinal))
        {
            // A file the folder already holds for a load with the same content serves the reference too.
            if (loadUnits.TryGetValue((folder, unit.Stem, unit.Hash), out var same)) { copyPaths[(unit, folder)] = same.Path!; continue; }
            string path = $"{folder}/{unit.Stem}.gltf";
            for (int i = 2; reserved.Contains(path) || !taken.Add(path); i++) path = $"{folder}/{unit.Stem}_{i}.gltf";
            copyPaths[(unit, folder)] = path;
        }
        // Each load finds its own file among every file written.
        foreach (var (mission, load, unit) in loads)
        {
            string? resolved = load.Instruction.ModelDirectories.Select(d => $"{d}/{Stem(load.File)}.gltf").FirstOrDefault(taken.Contains);
            if (resolved == null) notes.Add($"m{mission}: {load.File} is written to {unit.Path}, which the build does not search when it loads {load.NodeName}.");
            else if (!resolved.Equals(unit.Path, StringComparison.OrdinalIgnoreCase)) notes.Add($"m{mission}: {load.File} ({load.NodeName}) resolves to {resolved}, not to its own version {unit.Path}.");
        }
        HashSet<Unit> copied = new(copies.Keys.Select(k => k.Unit), ReferenceEqualityComparer.Instance);
        foreach (var unit in references.Values.Where(u => !copied.Contains(u)).OrderBy(u => u.Stem, StringComparer.Ordinal))
            notes.Add($"External reference {unit.Stem}.flt is used by no loaded file and was not written.");
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
            if (loadUnits.Keys.Any(k => k.Stem == stem && k.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))) return;
            HashSet<Unit> moving = [.. units];
            bool Finds(LoadedModel load)
            {
                int index = load.Instruction.ModelDirectories.ToList().FindIndex(d => d.Equals(folder, StringComparison.OrdinalIgnoreCase));
                return index >= 0 && !load.Instruction.ModelDirectories.Take(index).Any(d =>
                    loadUnits.Any(u => u.Key.Stem == stem && u.Key.Folder.Equals(d, StringComparison.OrdinalIgnoreCase) && !moving.Contains(u.Value)));
            }
            if (!units.SelectMany(u => loadsOf[u]).All(Finds)) return;
            var first = units.MinBy(u => u.Missions.Min)!;
            Unit moved = new(stem, first.Hash, first.Content, first.Zone)
            {
                Folder = folder, TextureDirectories = first.TextureDirectories, LoadRoot = units.Select(u => u.LoadRoot).FirstOrDefault(r => r != null),
            };
            foreach (var unit in units) { moved.Missions.UnionWith(unit.Missions); loadUnits.Remove((unit.Folder!, stem, unit.Hash)); }
            loadUnits[(folder, stem, moved.Hash)] = moved;
            for (int i = 0; i < loads.Count; i++) if (moving.Contains(loads[i].Unit)) loads[i] = (loads[i].Mission, loads[i].Load, moved);
        }
        void Place(Unit unit, string folder)
        {
            string path = $"{folder}/{unit.Stem}.gltf";
            for (int i = 2; !taken.Add(path); i++) path = $"{folder}/{unit.Stem}_{i}.gltf";
            unit.Folder = folder; unit.Path = path;
        }

        List<Output> outputs = [];
        foreach (var unit in loadUnits.Values.OrderBy(u => u.Path, StringComparer.Ordinal))
            Write(unit, unit.Folder!, unit.Path!, unit.TextureDirectories, unit.Missions.Min, unit.LoadRoot);
        foreach (var ((unit, folder), path) in copyPaths.OrderBy(c => c.Value, StringComparer.Ordinal))
        {
            if (loadUnits.TryGetValue((folder, unit.Stem, unit.Hash), out var same) && same.Path == path) continue;
            // A copy in a mission's folder takes that mission's textures; one in a shared folder the first mission's.
            var match = MissionFolder().Match(folder);
            Write(unit, folder, path, copies[(unit, folder)], match.Success ? int.Parse(match.Groups[1].Value) : unit.Missions.Min, null);
        }
        void Write(Unit unit, string folder, string path, IReadOnlyList<string> textureDirectories, int mission, WorldNode? loadRoot)
        {
            token.ThrowIfCancellationRequested();
            var doc = WorldGltf.Export(unit.Content, unit.Zone, new()
            {
                Texture = t =>
                {
                    // The image the build finds by name in the folders it searches, else the one the mission's packs held.
                    string target = textureDirectories.Select(d => $"{d}/{t.Name.ToLowerInvariant()}{TextureSources.Extension}").FirstOrDefault(textureFiles.Contains)
                        ?? texturePath(mission, t.Name) ?? unit.Missions.Select(m => texturePath(m, t.Name)).FirstOrDefault(p => p != null) ?? $"data/m{mission}/textures/{t.Name}{TextureSources.Extension}";
                    return (RelativeUri(folder, target), addressing(t.Name));
                },
                Reference = n => referenceOf.TryGetValue(n, out var r) && copyPaths.TryGetValue((r.Unit, folder), out var target) ? Spelled(n, RelativeUri(folder, target)) : null,
                Content = n => parts.TryGetValue(n, out var c) ? [.. c] : null,
                Group = groups.Contains,
            }, loadRoot);
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);
            var (json, bin) = doc.Write(stem + ".bin");
            // Viewers show transparent textures and a pickup's collision volume hidden as the game does; builds read the same values.
            var root = (JsonObject)JsonNode.Parse(json)!;
            bool pickup = loads.Any(l => ReferenceEquals(l.Unit, unit) && WorldGltf.IsPickupName(l.Load.NodeName));
            if (WorldGltf.ApplyPresentation(root, uri => transparency?.Invoke(WorldAssembler.Relative(path, uri)), pickup))
                json = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            outputs.Add(new(path, json));
            outputs.Add(new($"{folder}/{stem}.bin", bin));
        }
        return outputs;
    }

    /// <summary>A URI from a folder to a project path, with forward slashes.</summary>
    internal static string RelativeUri(string fromFolder, string to)
    {
        var from = fromFolder.Split('/', StringSplitOptions.RemoveEmptyEntries); var target = to.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int common = 0; while (common < from.Length && common < target.Length - 1 && from[common].Equals(target[common], StringComparison.OrdinalIgnoreCase)) common++;
        return string.Join('/', Enumerable.Repeat("..", from.Length - common).Concat(target.Skip(common)));
    }
}
