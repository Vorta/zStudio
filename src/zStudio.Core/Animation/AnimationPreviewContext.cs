using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Animation;

public sealed record AnimationEffectTemplate(string Name, string ModelName, int RootNode, string[] Textures, float Speed, bool Loop);
public sealed record AnimationSound(string Name, string FileName, bool Loop, ReadOnlyMemory<byte> Bytes)
{
    public double Duration { get; } = WaveDecoder.Read(Bytes).Duration;
}

public sealed partial class AnimationPreviewContext
{
    public required AnimationPackage Package { get; init; }
    public required ZbdDocument World { get; init; }
    private MissionSceneContext? mission;
    /// <summary>Replacing the mission replaces <see cref="Scene"/>, so scene-derived lookups are rebuilt.</summary>
    public MissionSceneContext? Mission { get => mission; set { mission = value; roots.Clear(); nodes.Clear(); trackedNodes.Clear(); lods = null; loadedNames = null; rootPositions = null; } }
    public IReadOnlySet<int>? InspectionNodes { get; init; }
    /// <summary>
    /// The world file's node count when <see cref="World"/>'s scene is a mission scene still growing (the mission layout's
    /// own context): lookups made while the game loads see only those nodes (<see cref="LoadedNamed"/>).
    /// </summary>
    public int? LoadedNodeCount { get; init; }
    public GameScene Scene => Mission?.Scene ?? World.Scene!;
    public Dictionary<string, AnimationEffectTemplate> Effects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AnimationSound> Sounds { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, TextureCycle> MaterialCycles { get; } = [];
    private SceneLods? lods;
    public SceneLods Lods => lods ??= new(Scene);
    public List<string> Diagnostics { get; } = [];
    public Dictionary<int, int> RootOverrides { get; } = [];
    // Playback may advance off the UI thread while the dispatcher reads bindings.
    private readonly ConcurrentDictionary<int, int> roots = new();
    /// <summary>Resolved references per entry object and bound root. Edits replace entry objects, so a retargeted
    /// reference is a new key and replaced entries are not retained; playback repeats the same lookups on every update.</summary>
    private readonly ConditionalWeakTable<AnimationEntry, ConcurrentDictionary<(int Root, int Reference, bool Instance), int>> nodes = new();
    private ConcurrentDictionary<(int Root, int Reference, bool Instance), int> References(AnimationEntry entry) => nodes.GetValue(entry, _ => new());
    /// <summary>Resolved tracked nodes per entry object, bound root and name (each cleanup restores them).</summary>
    private readonly ConditionalWeakTable<AnimationEntry, ConcurrentDictionary<(int Root, string Name), int>> trackedNodes = new();
    /// <summary>Freeze editable programs before background analysis; scene and decoded resources are read-only.</summary>
    public AnimationPreviewContext Snapshot()
    {
        var package = new AnimationPackage { Prefix = Package.Prefix, Tail = Package.Tail };
        package.Entries.AddRange(Package.Entries.Select(e => e.Clone()));
        package.Diagnostics.AddRange(Package.Diagnostics);
        var copy = new AnimationPreviewContext { Package = package, World = World, Mission = Mission, InspectionNodes = InspectionNodes, LoadedNodeCount = LoadedNodeCount };
        foreach (var pair in Effects) copy.Effects.Add(pair.Key, pair.Value);
        foreach (var pair in Sounds) copy.Sounds.Add(pair.Key, pair.Value);
        foreach (var pair in MaterialCycles) copy.MaterialCycles.Add(pair.Key, pair.Value);
        foreach (var pair in RootOverrides) copy.RootOverrides.Add(pair.Key, pair.Value);
        copy.Diagnostics.AddRange(Diagnostics);
        return copy;
    }
    public async Task<AnimationPreviewContext> WithDifficultyAsync(AssetResolver resolver, MissionDifficulty difficulty, CancellationToken token = default, string? exactMission = null)
    {
        var mission = await MissionSceneLoader.LoadAsync(World, resolver, Package, token, difficulty, exactMission, exactMission != null).ConfigureAwait(false);
        var copy = Snapshot(); copy.Mission = mission;
        copy.RootOverrides.Clear();
        if (Mission != null) copy.Diagnostics.RemoveAll(message => Mission.Diagnostics.Contains(message));
        copy.Diagnostics.AddRange(mission.Diagnostics);
        return copy;
    }
    public void RemapBindingsFrom(AnimationPreviewContext previous)
    {
        if (ReferenceEquals(this, previous)) return;
        RootOverrides.Clear();
        if (!World.Path.Equals(previous.World.Path, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var binding in previous.RootOverrides)
        {
            int node = Mission != null && previous.Mission != null ? Mission.RemapNodeFrom(previous.Mission, binding.Value) : -1;
            if (node >= 0) RootOverrides[binding.Key] = node;
            else Diagnostics.Add($"Preview binding for animation #{binding.Key} was cleared: its actor is absent or ambiguous in this layout.");
        }
    }
    /// <param name="exactMission">An explicitly requested MW3 reader; loading fails rather than falling back when it is unavailable.</param>
    public static async Task<AnimationPreviewContext> LoadAsync(AnimationPackage package, string animationPath, AssetResolver resolver, string? worldPath = null, CancellationToken token = default, MissionDifficulty difficulty = MissionDifficulty.Medium, string? exactMission = null)
    {
        var frozen = new AnimationPackage { Prefix = package.Prefix, Tail = package.Tail };
        frozen.Entries.AddRange(package.Entries.Select(e => e.Clone())); frozen.Diagnostics.AddRange(package.Diagnostics); package = frozen;
        string directory = Path.GetDirectoryName(animationPath)!;
        // MechWarrior 3 animations bind to its version-27 worlds; RECOIL's to version 15, or 13 in the August 1998 demo.
        bool Matches(uint? version) => package.Version == 39 ? version == 27 : version is 15 or 13;
        worldPath ??= Directory.EnumerateFiles(directory, "*.zbd").FirstOrDefault(p =>
        {
            token.ThrowIfCancellationRequested();
            var probe = FormatRegistry.Probe(p);
            return probe.Family == FormatFamily.GameZ && Matches(probe.Version);
        });
        if (worldPath == null) throw new InvalidDataException("Select the matching mission GameZ file to bind this animation.");
        var world = await resolver.OpenCachedAsync(worldPath, token).ConfigureAwait(false);
        if (world.Scene == null) throw new InvalidDataException("The selected file has no GameZ scene.");
        if (!Matches(world.Probe.Version))
            throw new InvalidDataException("The animation and world formats belong to different games. Choose the matching world.");
        var context = new AnimationPreviewContext { Package = package, World = world };
        context.Mission = await MissionSceneLoader.LoadAsync(world, resolver, package, token, difficulty, exactMission, exactMission != null).ConfigureAwait(false);
        context.Diagnostics.AddRange(context.Mission.Diagnostics);
        // The loaded mission is exact here: another reader must not silently supply its resources.
        var files = world.Game == GameVariant.MechWarrior3 ? (await MissionSceneLoader.Mw3ResourcesAsync(world.Path, resolver, context.Mission.Layout.MissionArchive, true, token).ConfigureAwait(false)).Files :
            resolver.ResourceDirectories(world.Path).SelectMany(d => Directory.EnumerateFiles(d,"*.zbd")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        List<(string Name, string File, bool Loop)> aliases = []; List<ZbdDocument> soundArchives = [];
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested(); if (FormatRegistry.Probe(file).Family != FormatFamily.Archive) continue;
            ZbdDocument archive;
            try { archive = await resolver.OpenCachedAsync(file, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { context.Diagnostics.Add($"Animation resources {Path.GetFileName(file)}: {ex.Message}"); continue; }
            if (archive.Assets.Any(a => a.Kind == AssetKind.Sound)) soundArchives.Add(archive);
            foreach (var asset in archive.Assets.Where(a => a.Kind == AssetKind.Zrd && (a.Name.Equals("effects.zrd", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("sounds.zrd", StringComparison.OrdinalIgnoreCase))))
            {
                bool effects = asset.Name.Equals("effects.zrd", StringComparison.OrdinalIgnoreCase);
                if (effects && world.Game == GameVariant.MechWarrior3) continue;
                var tree = asset.Content as ZrdNode ?? ZrdDecoder.Read(archive.Slice(asset.Offset, asset.Length), token);
                if (effects) ReadEffects(tree.ToJson(token));
                else aliases.AddRange(ReadSoundAliases(tree, token));
            }
        }
        // Prefer the highest decoded quality, independent of the archive filename.
        Dictionary<string, (ReadOnlyMemory<byte> Bytes, long Quality)> waves = new(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in soundArchives) foreach (var asset in archive.Assets.Where(a => a.Kind == AssetKind.Sound))
        {
            token.ThrowIfCancellationRequested(); var bytes = archive.Slice(asset.Offset, asset.Length);
            try
            {
                var info = WaveDecoder.Read(bytes); long quality = (long)info.SampleRate * info.BitsPerSample * info.Channels;
                string name = Path.GetFileName(asset.Name);
                if (!waves.TryGetValue(name, out var current) || current.Quality < quality) waves[name] = (bytes, quality);
            }
            catch (InvalidDataException ex) { context.Diagnostics.Add($"Sound {asset.Name}: {ex.Message}"); }
        }
        foreach (var alias in aliases)
        {
            if (context.Sounds.ContainsKey(alias.Name)) continue;
            if (waves.TryGetValue(alias.File, out var wave)) context.Sounds[alias.Name] = new(alias.Name, alias.File, alias.Loop, wave.Bytes);
        }
        foreach (var (name, wave) in waves) context.Sounds.TryAdd(Path.GetFileNameWithoutExtension(name), new(name, name, false, wave.Bytes));
        context.BindMaterialCycles();
        if (world.Game != GameVariant.MechWarrior3)
        {
            await context.LoadScriptCyclesAsync(files, resolver, token).ConfigureAwait(false);
            context.BindEffectCycles();
        }
        return context;

        void ReadEffects(JsonNode? tree)
        {
            foreach (var array in Arrays(tree))
            {
                if (array.Count < 3 || array[0]?.Text("type") != "string") continue;
                string model = array[0].Text("value"); string name = Value(array, "NAME").Text("value");
                if (name.Length == 0 || !array.Any(n => n.Text("value") == "MAPS")) continue;
                var maps = Value(array, "MAPS", false)?["children"]?.AsArray();
                string[] textures = maps?.Select(n => n.Text("value")).Where(s => s.Length > 0).ToArray() ?? [];
                float speed = float.TryParse(Value(array, "SPEED").Text("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 15;
                // zeff_init FindByTypeAndName: the highest slot of the template's name.
                int root = world.Scene.Nodes.LastOrDefault(n => n.Name == model && n.Class != "none")?.Index ?? -1;
                context.Effects.TryAdd(name, new(name, model, root, textures, speed, Value(array, "LOOPING").Text("value") == "ON"));
            }
        }
    }
    internal static IEnumerable<(string Name, string File, bool Loop)> ReadSoundAliases(ZrdNode tree, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (tree.Kind != ZrdKind.Array) yield break;
        var row = tree.Children;
        if (row.Count >= 2 && row[0].Kind == ZrdKind.String && row[1].Kind == ZrdKind.String && row[1].Text.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            yield return (row[0].Text, Path.GetFileName(row[1].Text), row.Any(n => n.Kind == ZrdKind.String && n.Text == "LOOPED"));
        foreach (var child in row)
        {
            token.ThrowIfCancellationRequested();
            if (child.Kind == ZrdKind.Array) foreach (var alias in ReadSoundAliases(child, token)) yield return alias;
        }
    }
    private static IEnumerable<JsonArray> Arrays(JsonNode? node)
    {
        if (node?["children"] is not JsonArray children) yield break;
        yield return children;
        foreach (var child in children) foreach (var array in Arrays(child)) yield return array;
    }
    private static JsonNode? Value(JsonArray array, string name, bool scalar = true)
    {
        for (int i = 1; i < array.Count - 1; i++) if (array[i].Text("value") == name)
        { var value = array[i + 1]; return scalar && value?["children"] is JsonArray { Count: > 0 } values ? values[0] : value; }
        return null;
    }
    /// <summary>The node an entry is bound to: the editor's chosen root, else the one the game binds when it loads (<see cref="LoadedRoot"/>).</summary>
    public int ResolveRoot(AnimationEntry entry) => RootOverrides.TryGetValue(entry.Index, out int selected) ? selected : LoadedRoot(entry);
    private int LoadedRoot(AnimationEntry entry)
    {
        if (roots.TryGetValue(entry.Index, out int root)) return root;
        // MW3 has no engine evidence for its load order: a root name must be unique in the mission scene.
        if (World.Game == GameVariant.MechWarrior3)
        {
            var named = Scene.Nodes.Where(n => n.Name == entry.RootName).ToArray();
            return roots[entry.Index] = named.Length == 1 ? named[0].Index : -1;
        }
        var matches = LoadedNamed(entry.RootName);
        if (matches.Count == 0) return roots[entry.Index] = -1;
        // LoadZbd's binding loop (NameLookups.RootPositions): consecutive entries with one root name take the following
        // nodes of that name, highest slot first. The game binds no node to the entries it skips (entry 0, state 5); the
        // preview shows them on the highest slot.
        rootPositions ??= NameLookups.RootPositions(Package.Entries, name => LoadedNamed(name).Count);
        // The editor asks with its document's entries, not this context's copies: the same position with the same root.
        int position = entry.Index >= 0 && entry.Index < rootPositions.Length && Package.Entries[entry.Index].RootName == entry.RootName ? rootPositions[entry.Index] : -1;
        return roots[entry.Index] = matches[Math.Clamp(position, 0, matches.Count - 1)];
    }
    private int[]? rootPositions;
    private Dictionary<string, List<int>>? loadedNames;
    /// <summary>
    /// The world file's live nodes with a name, highest slot first: what a lookup made while the game loads the mission
    /// finds (roots, texture effects), before anything the mission creates (the scene's nodes past the world file's slots).
    /// Freed slots keep their old names but are not nodes.
    /// </summary>
    public IReadOnlyList<int> LoadedNamed(string name)
    {
        loadedNames ??= Scene.Nodes.Take(LoadedNodeCount ?? World.Scene!.Nodes.Count).Where(n => n.Class != "none").GroupBy(n => n.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(n => n.Index).OrderByDescending(i => i).ToList(), StringComparer.Ordinal);
        return loadedNames.TryGetValue(name, out var list) ? list : [];
    }
    /// <summary>The node a lookup by name finds while the mission runs: the most recently created live node with the name.</summary>
    public int FindNamed(string name)
    {
        for (int i = Scene.Nodes.Count - 1; i >= 0; i--) if (Scene.Nodes[i].Name == name && Scene.Nodes[i].Class != "none") return i;
        return -1;
    }
    public int ResolveNode(AnimationEntry entry, int reference, int? boundRoot = null)
    {
        int root = boundRoot ?? ResolveRoot(entry);
        if (reference is -100 or -200) return root;
        if (reference == 0) return -1; // Reserved null reference, not the bound root.
        if (reference < 0 || reference >= entry.References[1].Count) return -1;
        var cache = References(entry);
        if (cache.TryGetValue((root, reference, false), out int cached)) return cached;
        return cache[(root, reference, false)] = FindReference(entry, reference, root);
    }
    /// <summary>A runtime node reference of an instance bound at <paramref name="root"/>.</summary>
    public int ResolveInstanceNode(AnimationEntry entry, int reference, int root)
    {
        if (reference is -100 or -200) return root;
        if (World.Game == GameVariant.MechWarrior3 || reference <= 0 || reference >= entry.References[1].Count) return ResolveNode(entry, reference, root);
        var cache = References(entry);
        if (cache.TryGetValue((root, reference, true), out int cached)) return cached;
        // RECOIL's ResolveNodeByName prefers the first match below the callback node, then below the bound instance. MW3
        // names are not unique identities, so ResolveNode's unique-or-unresolved result stands.
        string name = entry.References[1][reference].Text(0, 36);
        int local = name == entry.RootName ? root : FindBelow(Callback(entry, root), name);
        if (local < 0) local = FindBelow(root, name);
        return cache[(root, reference, true)] = local >= 0 ? local : ResolveNode(entry, reference, root);
    }
    /// <summary>
    /// A tracked node of an instance bound at <paramref name="root"/> (cleanup restores it): RECOIL resolves it with
    /// ResolveNodeByName like a reference; MW3 names must be unique below the root.
    /// </summary>
    public int ResolveTrackedNode(AnimationEntry entry, string name, int root)
    {
        if (World.Game == GameVariant.MechWarrior3) return FindNamedBelow(root, name);
        return trackedNodes.GetValue(entry, _ => new()).GetOrAdd((root, name), key =>
        {
            int found = FindBelow(Callback(entry, key.Root), key.Name); if (found < 0) found = FindBelow(key.Root, key.Name);
            return found >= 0 ? found : FindNamed(key.Name);
        });
    }
    /// <summary>
    /// The callback (attachment) node of <paramref name="entry"/> bound at <paramref name="root"/>, whose subtree every name
    /// lookup searches first (ResolveNodeByName). LoadZbd looks the attach name up like any name: inside the root, else the
    /// world's highest slot (m4–m13 hit walls attach to one wall1/ware_5 outside their root). A copy or rebinding to another
    /// node (CloneEntryForNode, RebindEntryToNode) looks it up only inside that node, keeping the node itself when the loaded
    /// entry's callback was its root; a copy without it does not run in the game, which the preview approximates with the root.
    /// </summary>
    private int Callback(AnimationEntry entry, int root)
    {
        int below = FindBelow(root, entry.AttachName);
        if (below >= 0 || root != LoadedRoot(entry)) return below >= 0 ? below : root;
        return LoadedNamed(entry.AttachName) is { Count: > 0 } named ? named[0] : root;
    }
    private int FindReference(AnimationEntry entry, int reference, int root)
    {
        string name = entry.References[1][reference].Text(0, 36);
        if (name == entry.RootName) return root;
        if (World.Game == GameVariant.MechWarrior3)
        {
            var local = Descendants(root).Where(i => Scene.Nodes[i].Name == name).ToArray();
            if (local.Length == 1) return local[0];
            if (local.Length > 1) return -1;
            var global = Scene.Nodes.Where(n => n.Name == name).ToArray();
            return global.Length == 1 ? global[0].Index : -1;
        }
        // References search the callback (attachment) node's subtree first, then the bound root's.
        int found = FindBelow(Callback(entry, root), name); if (found < 0) found = FindBelow(root, name);
        // ResolveNodeByName's last step: the whole world, the most recently created node first.
        return found >= 0 ? found : FindNamed(name);
    }
    public int FindBelow(int root, string name) => Descendants(root).FirstOrDefault(i => Scene.Nodes[i].Name == name, -1);
    /// <summary>FindSubNodeByName (scripts' FindSubNode): the node itself, then its children last to first, depth first.</summary>
    public int FindSubBelow(int root, string name)
    {
        HashSet<int> visited = []; Stack<int> pending = new(); pending.Push(root);
        while (pending.TryPop(out int index))
        {
            if (index < 0 || index >= Scene.Nodes.Count || !visited.Add(index)) continue;
            if (Scene.Nodes[index].Name == name) return index;
            foreach (int child in SceneBuilder.Children(Scene.Nodes[index])) pending.Push(child);
        }
        return -1;
    }
    /// <summary>Name lookup below an instance root: RECOIL keeps its loader's first match; MW3 names must be unique there.</summary>
    public int FindNamedBelow(int root, string name)
    {
        if (World.Game != GameVariant.MechWarrior3) return FindBelow(root, name);
        int found = -1;
        foreach (int i in Descendants(root)) if (Scene.Nodes[i].Name == name) { if (found >= 0) return -1; found = i; }
        return found;
    }
    public IEnumerable<int> Descendants(int root)
    {
        HashSet<int> visited = []; Stack<int> pending = new(); pending.Push(root);
        while (pending.TryPop(out int index))
        {
            if (index < 0 || index >= Scene.Nodes.Count || !visited.Add(index)) continue;
            yield return index; foreach (int child in SceneBuilder.Children(Scene.Nodes[index]).Reverse()) pending.Push(child);
        }
    }
    public Matrix4x4 WorldTransform(int index)
    {
        Matrix4x4 result = Matrix4x4.Identity; HashSet<int> seen = [];
        while (index >= 0 && index < Scene.Nodes.Count && seen.Add(index))
        {
            var node = Scene.Nodes[index]; result *= SceneBuilder.LocalTransform(node); index = node.Parents.FirstOrDefault(-1);
        }
        return result;
    }
}
