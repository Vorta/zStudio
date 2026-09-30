using System.Security.Cryptography;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Reconstructs model sources from shipped worlds: each mission database (<c>data/mN/models/mN.gltf</c>), each file the
/// build scripts loaded, and each OpenFlight external reference (a node named <c>*.flt</c>) as its own file that the
/// referencing node names in <c>extras.recoil.ref</c>. Files go where the original tree had them as far as the scripts
/// show (docs/recoil-original-worktree.md): a file whose script chose its folder (<c>SetModelDirectory</c>) goes there,
/// a file that a script several missions run loads identically (the pickups) goes to <c>data/common/models</c>, and a
/// file a mission's own scripts load to that mission's <c>models</c> folder, a copy per mission. An external reference
/// is written beside every file that references it, so missions sharing one keep a copy each, as they do textures.
/// </summary>
internal static partial class WorldSources
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^data/m(\d+)/", System.Text.RegularExpressions.RegexOptions.CultureInvariant)] private static partial System.Text.RegularExpressions.Regex MissionFolder();
    public const string CommonModels = "data/common/models";
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

    public static bool IsReference(WorldNode node) => node.Class == WorldNodeClass.Object3D && node.Model == null && node.Children.Count > 0 && node.Name.EndsWith(".flt", StringComparison.OrdinalIgnoreCase);
    private static string Stem(string file) => System.IO.Path.GetFileNameWithoutExtension(file.Replace('\\', '/')).ToLowerInvariant();

    /// <param name="textureFiles">Every texture source written (project paths).</param>
    /// <param name="namedFolders">Receives every project folder the scripts search for models, textures or resources.</param>
    public static List<Output> Reconstruct(IReadOnlyList<MissionWorld> missions, Func<string, IReadOnlyList<IReadOnlyList<string>>?> scripts,
        Func<int, string, string?> texturePath, IReadOnlySet<string> textureFiles, Func<string, int> addressing, List<string> notes, CancellationToken token,
        ISet<string>? namedFolders = null)
    {
        // The missions that run each script: a model a mission's own script loads belongs to that mission.
        Dictionary<string, SortedSet<int>> scriptMissions = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<WorldNode, string> hashes = new(ReferenceEqualityComparer.Instance);
        Dictionary<(string Stem, string Hash), Unit> references = [];
        Dictionary<WorldNode, (Unit Unit, int Mission)> referenceOf = new(ReferenceEqualityComparer.Instance);
        List<(int Mission, LoadedModel Load, string Hash)> traced = [];
        Dictionary<(string Folder, string Stem, string Hash), Unit> loadUnits = [];
        HashSet<WorldNode> roots = new(ReferenceEqualityComparer.Instance);
        HashSet<(WorldNode, int)> visited = [];

        foreach (var mission in missions)
        {
            token.ThrowIfCancellationRequested();
            var trace = ScriptTrace.Trace(scripts, $"m{mission.Mission}.gs", notes);
            foreach (var instruction in trace)
            {
                if (!scriptMissions.TryGetValue(instruction.Script, out var runs)) scriptMissions[instruction.Script] = runs = [];
                runs.Add(mission.Mission);
                if (namedFolders == null) continue;
                foreach (string folder in instruction.ModelDirectories.Concat(instruction.TextureDirectories)) namedFolders.Add(folder);
                if (instruction.Command == "RdrSetPath" && instruction.Args.Count > 0)
                    foreach (string part in instruction.Args[0].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (WorldAssembler.ProjectPath(part) is { } folder) namedFolders.Add(folder);
            }
            var decomposed = WorldDecomposer.Decompose(mission.World, trace, notes);
            foreach (var load in decomposed) if (load.Root != null) roots.Add(load.Root);
            // External references anywhere in loaded content, innermost first so their hashes are known.
            foreach (var load in decomposed)
                foreach (var node in load.Content)
                    Visit(node, mission.Mission, load.Instruction.TextureDirectories);
            // A root's flags are the file's only when the script left them alone, so they are not part of its identity.
            foreach (var load in decomposed) traced.Add((mission.Mission, load, Hash(load.Content, 0xFF)));
        }
        void Visit(WorldNode node, int mission, IReadOnlyList<string> textureDirectories)
        {
            // A node under several parents is visited once per mission; the hierarchy is bounded by the reader.
            if (!visited.Add((node, mission))) return;
            foreach (var child in node.Children) Visit(child, mission, textureDirectories);
            if (!IsReference(node) || roots.Contains(node) || referenceOf.ContainsKey(node)) return;
            string hash = Hash(node.Children, node.Zone & 0xFF);
            var key = (Stem(node.Name), hash);
            if (!references.TryGetValue(key, out var unit)) references[key] = unit = new(key.Item1, hash, node.Children.ToList(), node.Zone & 0xFF) { TextureDirectories = textureDirectories };
            unit.Missions.Add(mission); referenceOf[node] = (unit, mission);
        }
        string Hash(IReadOnlyList<WorldNode> content, uint zone)
        {
            var doc = WorldGltf.Export(content, zone, new()
            {
                Texture = t => ($"texture:{t.Name}", addressing(t.Name)),
                Reference = n => referenceOf.TryGetValue(n, out var r) ? $"ref:{r.Unit.Stem}:{r.Unit.Hash}" : null,
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
        foreach (var unit in loadUnits.Values.OrderBy(u => u.Folder, StringComparer.Ordinal).ThenBy(u => u.Stem, StringComparer.Ordinal).ThenBy(u => u.Missions.Min))
            Place(unit, unit.Folder!);
        foreach (var (mission, load, unit) in loads)
        {
            string? resolved = load.Instruction.ModelDirectories.Select(d => $"{d}/{Stem(load.File)}.gltf").FirstOrDefault(taken.Contains);
            if (resolved == null) notes.Add($"m{mission}: {load.File} is written to {unit.Path}, which the build does not search when it loads {load.NodeName}.");
            else if (!resolved.Equals(unit.Path, StringComparison.OrdinalIgnoreCase)) notes.Add($"m{mission}: {load.File} ({load.NodeName}) resolves to {resolved}, not to its own version {unit.Path}.");
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
                if (referenceOf.TryGetValue(node, out var r)) { if (units.Add(r.Unit)) list.Add(r.Unit); continue; }
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
            for (int i = 2; !taken.Add(path); i++) path = $"{folder}/{unit.Stem}_{i}.gltf";
            copyPaths[(unit, folder)] = path;
        }
        HashSet<Unit> copied = new(copies.Keys.Select(k => k.Unit), ReferenceEqualityComparer.Instance);
        foreach (var unit in references.Values.Where(u => !copied.Contains(u)).OrderBy(u => u.Stem, StringComparer.Ordinal))
            notes.Add($"External reference {unit.Stem}.flt is used by no loaded file and was not written.");
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
                Reference = n => referenceOf.TryGetValue(n, out var r) && copyPaths.TryGetValue((r.Unit, folder), out var target) ? RelativeUri(folder, target) : null,
            }, loadRoot);
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);
            var (json, bin) = doc.Write(stem + ".bin");
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
