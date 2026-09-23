using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

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
    public MissionSceneContext? Mission { get; set; }
    public GameScene Scene => Mission?.Scene ?? World.Scene!;
    public Dictionary<string, AnimationEffectTemplate> Effects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AnimationSound> Sounds { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, TextureCycle> MaterialCycles { get; } = [];
    private SceneLods? lods;
    public SceneLods Lods => lods ??= new(Scene);
    public List<string> Diagnostics { get; } = [];
    public Dictionary<int, int> RootOverrides { get; } = [];
    private readonly Dictionary<int, int> roots = [];
    /// <summary>Freeze editable programs before background analysis; scene and decoded resources are read-only.</summary>
    public AnimationPreviewContext Snapshot()
    {
        var package = new AnimationPackage { Prefix = Package.Prefix, Tail = Package.Tail };
        package.Entries.AddRange(Package.Entries.Select(e => e.Clone()));
        package.Diagnostics.AddRange(Package.Diagnostics);
        var copy = new AnimationPreviewContext { Package = package, World = World, Mission = Mission };
        foreach (var pair in Effects) copy.Effects.Add(pair.Key, pair.Value);
        foreach (var pair in Sounds) copy.Sounds.Add(pair.Key, pair.Value);
        foreach (var pair in MaterialCycles) copy.MaterialCycles.Add(pair.Key, pair.Value);
        foreach (var pair in RootOverrides) copy.RootOverrides.Add(pair.Key, pair.Value);
        copy.Diagnostics.AddRange(Diagnostics);
        return copy;
    }
    public async Task<AnimationPreviewContext> WithDifficultyAsync(AssetResolver resolver, MissionDifficulty difficulty, CancellationToken token = default)
    {
        var mission = await MissionSceneLoader.LoadAsync(World, resolver, Package, token, difficulty).ConfigureAwait(false);
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
    public static async Task<AnimationPreviewContext> LoadAsync(AnimationPackage package, string animationPath, AssetResolver resolver, string? worldPath = null, CancellationToken token = default, MissionDifficulty difficulty = MissionDifficulty.Medium)
    {
        var frozen = new AnimationPackage { Prefix = package.Prefix, Tail = package.Tail };
        frozen.Entries.AddRange(package.Entries.Select(e => e.Clone())); frozen.Diagnostics.AddRange(package.Diagnostics); package = frozen;
        string directory = Path.GetDirectoryName(animationPath)!;
        worldPath ??= Directory.EnumerateFiles(directory, "*.zbd").FirstOrDefault(p => FormatRegistry.Probe(p).Family == FormatFamily.GameZ);
        if (worldPath == null) throw new InvalidDataException("Select the matching mission GameZ file to bind this animation.");
        var world = await resolver.OpenCachedAsync(worldPath, token).ConfigureAwait(false);
        if (world.Scene == null) throw new InvalidDataException("The selected file has no GameZ scene.");
        var context = new AnimationPreviewContext { Package = package, World = world };
        context.Mission = await MissionSceneLoader.LoadAsync(world, resolver, package, token, difficulty).ConfigureAwait(false);
        context.Diagnostics.AddRange(context.Mission.Diagnostics);
        var files = resolver.ResourceDirectories(world.Path).SelectMany(d => Directory.EnumerateFiles(d,"*.zbd")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        List<(string Name, string File, bool Loop)> aliases = []; List<ZbdDocument> soundArchives = [];
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested(); if (FormatRegistry.Probe(file).Family != FormatFamily.Archive) continue;
            var archive = await resolver.OpenCachedAsync(file, token).ConfigureAwait(false);
            if (archive.Assets.Any(a => a.Kind == AssetKind.Sound)) soundArchives.Add(archive);
            foreach (var asset in archive.Assets.Where(a => a.Kind == AssetKind.Zrd && (a.Name.Equals("effects.zrd", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("sounds.zrd", StringComparison.OrdinalIgnoreCase))))
            {
                var tree = ZrdDecoder.Decode(archive.Slice(asset.Offset, asset.Length), token);
                if (asset.Name.Equals("effects.zrd", StringComparison.OrdinalIgnoreCase)) ReadEffects(tree);
                else ReadSounds(tree);
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
        await context.LoadScriptCyclesAsync(files, resolver, token).ConfigureAwait(false);
        context.BindEffectCycles();
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
                int root = world.Scene.Nodes.FirstOrDefault(n => n.Name == model)?.Index ?? -1;
                context.Effects.TryAdd(name, new(name, model, root, textures, speed, Value(array, "LOOPING").Text("value") == "ON"));
            }
        }
        void ReadSounds(JsonNode? tree)
        {
            foreach (var array in Arrays(tree))
            {
                if (array.Count < 2) continue; string name = array[0].Text("value"), file = array[1].Text("value");
                if (file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) aliases.Add((name, Path.GetFileName(file), array.Any(n => n.Text("value") == "LOOPED")));
            }
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
    public int ResolveRoot(AnimationEntry entry)
    {
        if (RootOverrides.TryGetValue(entry.Index, out int selected)) return selected;
        if (roots.TryGetValue(entry.Index, out int root)) return root;
        var matches = Scene.Nodes.Where(n => n.Name == entry.RootName).ToArray();
        if (matches.Length == 0) return roots[entry.Index] = -1;
        // LoadZbd advances FindNextByName for consecutive entries sharing a root.
        int occurrence = 0; for (int i = entry.Index - 1; i >= 0 && Package.Entries[i].RootName == entry.RootName; i--) occurrence++;
        return roots[entry.Index] = matches[occurrence % matches.Length].Index;
    }
    public int ResolveNode(AnimationEntry entry, int reference, int? boundRoot = null)
    {
        int root = boundRoot ?? ResolveRoot(entry);
        if (reference is -100 or -200) return root;
        if (reference == 0) return -1; // Reserved null reference, not the bound root.
        if (reference < 0 || reference >= entry.References[1].Count) return -1;
        string name = entry.References[1][reference].Text(0, 36);
        if (name == entry.RootName) return root;
        int attachment = FindBelow(root, entry.AttachName);
        int found = FindBelow(attachment, name); if (found < 0) found = FindBelow(root, name);
        return found >= 0 ? found : Scene.Nodes.FirstOrDefault(n => n.Name == name)?.Index ?? -1;
    }
    public int FindBelow(int root, string name) => Descendants(root).FirstOrDefault(i => Scene.Nodes[i].Name == name, -1);
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
