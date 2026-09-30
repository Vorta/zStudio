using System.Security.Cryptography;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Reconstructs model sources from shipped worlds: each mission database (<c>data/mN/models/mN.gltf</c>), each file the
/// build scripts loaded, and each OpenFlight external reference (a node named <c>*.flt</c>) as its own file that the
/// referencing node names in <c>extras.recoil.ref</c>. Identical files are written once: shared content goes to
/// <c>data/common/models</c>, mission content to <c>data/mN/models</c>, and a file whose script chose its folder
/// (<c>SetModelDirectory</c>) goes there.
/// </summary>
internal static class WorldSources
{
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
    public static List<Output> Reconstruct(IReadOnlyList<MissionWorld> missions, Func<string, IReadOnlyList<IReadOnlyList<string>>?> scripts,
        Func<int, string, string?> texturePath, IReadOnlySet<string> textureFiles, Func<string, int> addressing, List<string> notes, CancellationToken token)
    {
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

        // Placement. External references are found by path, so any folder works; shared ones go to common.
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in references.Values.OrderBy(u => u.Stem, StringComparer.Ordinal).ThenBy(u => u.Missions.Min).ThenBy(u => u.Hash, StringComparer.Ordinal))
            Place(unit, unit.Missions.Count > 1 ? CommonModels : $"data/m{unit.Missions.Min}/models");
        // Loaded files are found through the model directories. A file goes where its script pointed them; otherwise
        // content every loading mission shares goes to common and the rest to each mission's folder.
        List<(int Mission, LoadedModel Load, Unit Unit)> loads = [];
        // Loads whose folder is known come first, so a load without one can use an identical file its search finds.
        foreach (var (mission, load, hash) in traced.OrderBy(t => t.Load.Database || t.Load.Instruction.ScriptModelDirectory != null ? 0 : 1))
        {
            string stem = Stem(load.File);
            bool shared = !load.Database && traced.Where(t => Stem(t.Load.File) == stem && !t.Load.Database).Select(t => t.Hash).Distinct().Count() == 1
                && traced.Where(t => Stem(t.Load.File) == stem).Select(t => t.Mission).Distinct().Count() > 1;
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
        void Place(Unit unit, string folder)
        {
            string path = $"{folder}/{unit.Stem}.gltf";
            for (int i = 2; !taken.Add(path); i++) path = $"{folder}/{unit.Stem}_{i}.gltf";
            unit.Folder = folder; unit.Path = path;
        }

        List<Output> outputs = [];
        foreach (var unit in references.Values.Concat(loadUnits.Values).OrderBy(u => u.Path, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            int mission = unit.Missions.Min;
            var doc = WorldGltf.Export(unit.Content, unit.Zone, new()
            {
                Texture = t =>
                {
                    // The image the build finds by name in the folders it searches, else the one the mission's packs held.
                    string target = unit.TextureDirectories.Select(d => $"{d}/{t.Name.ToLowerInvariant()}{TextureSources.Extension}").FirstOrDefault(textureFiles.Contains)
                        ?? texturePath(mission, t.Name) ?? unit.Missions.Select(m => texturePath(m, t.Name)).FirstOrDefault(p => p != null) ?? $"data/m{mission}/textures/{t.Name}{TextureSources.Extension}";
                    return (RelativeUri(unit.Folder!, target), addressing(t.Name));
                },
                Reference = n => referenceOf.TryGetValue(n, out var r) ? RelativeUri(unit.Folder!, r.Unit.Path!) : null,
            }, unit.LoadRoot);
            string stem = System.IO.Path.GetFileNameWithoutExtension(unit.Path!);
            var (json, bin) = doc.Write(stem + ".bin");
            outputs.Add(new(unit.Path!, json));
            outputs.Add(new($"{unit.Folder}/{stem}.bin", bin));
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
