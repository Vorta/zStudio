using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record MissionActor(int Root, int SourceRoot, string Name, string PlacementSource, MissionPickup? Pickup = null);
public sealed record HorizonBinding(int Root, bool FollowHeight);

/// <summary>A published, read-only preview baseline. Its nodes never alias serialized node data.</summary>
public sealed class MissionSceneContext
{
    public GameScene Scene { get; }
    public IReadOnlyList<int> SourceNodes { get; }
    public IReadOnlyList<MissionActor> Actors { get; }
    public IReadOnlyList<HorizonBinding> Horizons { get; }
    public IReadOnlySet<int> DormantRoots { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public MissionLayoutSelection Layout { get; }
    private readonly int originalNodeCount;
    internal MissionSceneContext(GameScene scene, List<int> sources, List<MissionActor> actors, HashSet<int> dormant, List<string> diagnostics, MissionLayoutSelection layout, int originalNodeCount)
    {
        Layout = layout; this.originalNodeCount = originalNodeCount;
        Scene = scene; SourceNodes = sources.AsReadOnly(); Actors = actors.AsReadOnly(); DormantRoots = dormant;
        Diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray(); Horizons = FindHorizons(scene);
    }
    /// <summary>Match an instance and its source node; never carry clone indices between layouts.</summary>
    public int RemapNodeFrom(MissionSceneContext previous, int index)
    {
        if (index < 0 || index >= previous.Scene.Nodes.Count) return -1;
        int ancestor = index; HashSet<int> visited = [];
        while (ancestor >= 0 && ancestor < previous.Scene.Nodes.Count && visited.Add(ancestor))
        {
            var actor = previous.Actors.FirstOrDefault(a => a.Root == ancestor);
            if (actor != null)
            {
                bool Matches(MissionActor a) => a.SourceRoot == actor.SourceRoot &&
                    (actor.Pickup is { } pickup ? a.Pickup?.Source == pickup.Source : a.Pickup == null && a.Name == actor.Name);
                if (previous.Actors.Count(Matches) != 1) return -1;
                var matches = Actors.Where(Matches).ToArray();
                if (matches.Length != 1) return -1;
                int source = previous.SourceNodes[index];
                HashSet<int> nodes = []; Stack<int> pending = new(); pending.Push(matches[0].Root);
                while (pending.TryPop(out int node))
                    if (node >= 0 && node < Scene.Nodes.Count && nodes.Add(node)) foreach (int child in SceneBuilder.Children(Scene.Nodes[node])) pending.Push(child);
                int[] candidates = nodes.Where(n => SourceNodes[n] == source).ToArray();
                return candidates.Length == 1 ? candidates[0] : -1;
            }
            ancestor = previous.Scene.Nodes[ancestor].Parents.FirstOrDefault(-1);
        }
        return index < originalNodeCount && index < previous.originalNodeCount && Scene.Nodes[index].Name == previous.Scene.Nodes[index].Name ? index : -1;
    }
    public static IReadOnlyList<HorizonBinding> FindHorizons(GameScene scene)
    {
        Dictionary<int, HorizonBinding> found = [];
        foreach (var camera in scene.Nodes.Where(n => n.Class == "camera"))
            foreach (var (field, height) in new[] { ("focus_node_xy", true), ("focus_node_xz", false) })
                if (camera.Data[field] != null && camera.Data.Int(field, -1) is >= 0 and int index && index < scene.Nodes.Count)
                    found[index] = new(index, height);
        // Player::InitMissionRuntimeFromWorldAndCamera also finds this node by name,
        // even when the saved camera has no explicit binding (m1).
        foreach (var node in scene.Nodes.Where(n => n.Name == "horizon" && n.Class == "object3d"))
            found.TryAdd(node.Index, new(node.Index, true));
        return found.Values.ToArray();
    }
}

public static partial class MissionSceneLoader
{
    private static readonly ConditionalWeakTable<ZbdDocument, ConcurrentDictionary<string, MissionSceneContext>> Cache = new();
    internal static string[] ResourceFiles(string worldPath, AssetResolver resolver) => resolver.ResourceDirectories(worldPath)
        .SelectMany(d => Directory.EnumerateFiles(d, "*.zbd").Order(StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static void Invalidate(ZbdDocument world) => Cache.Remove(world);

    public static async Task<MissionSceneContext> LoadAsync(ZbdDocument world, AssetResolver resolver, AnimationPackage? package = null, CancellationToken token = default, MissionDifficulty difficulty = MissionDifficulty.Medium)
    {
        token.ThrowIfCancellationRequested();
        var requested = MissionLayoutSelection.For(difficulty);
        string directory = Path.GetDirectoryName(world.Path)!;
        var files = ResourceFiles(world.Path, resolver);
        string key = difficulty + "|" + string.Join('|', files.Select(p => p + FileStamp.Read(p))) + (package == null ? "" : Convert.ToHexString(SHA256.HashData(AnimationWriter.Write(package))));
        var cache = Cache.GetOrCreateValue(world);
        if (cache.TryGetValue(key, out var cached)) return cached;
        List<string> diagnostics = []; Dictionary<string, (ZbdDocument Archive, AssetRecord Asset)> resources = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            var family = FormatRegistry.Probe(file).Family;
            try
            {
                if (package == null && family == FormatFamily.Animation && Path.GetDirectoryName(file)!.Equals(directory, StringComparison.OrdinalIgnoreCase))
                    package = (await resolver.OpenCachedAsync(file, token).ConfigureAwait(false)).Animations;
                if (family != FormatFamily.Archive) continue;
                var archive = await resolver.OpenCachedAsync(file, token).ConfigureAwait(false);
                foreach (var asset in archive.Assets.Where(a => a.Name.ToLowerInvariant() is "aiv.zrd" or "aiv_easy.zrd" or "aiv_hard.zrd" or "vehicle.zrd" or "vehicle_easy.zrd" or "vehicle_hard.zrd" or "startanims.zrd" or "ai.zrd" or "puppies.zrd" or "puppies_easy.zrd" or "puppies_hard.zrd"))
                {
                    resources.TryAdd(asset.Name, (archive, asset));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { diagnostics.Add($"Mission layout: {Path.GetFileName(file)}: {ex.Message}"); }
        }
        string aivName = Select(requested.AivResource, "aiv.zrd"), vehicleName = Select(requested.VehicleResource, "vehicle.zrd");
        string pickupName = Select(requested.PickupResource, "puppies.zrd");
        var selection = new MissionLayoutSelection(difficulty, aivName, vehicleName, pickupName);
        MissionResourceSource? pickupSource = resources.TryGetValue(pickupName, out var pickupResource)
            ? new(pickupResource.Archive.Path, pickupResource.Asset.Index, pickupResource.Asset.Name) : null;
        var result = await Task.Run(() => Build(world, package, Decode(aivName), Decode(vehicleName), Decode("startanims.zrd"), diagnostics, token, selection,
            Decode("ai.zrd"), Decode(pickupName), pickupSource), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (cache.Count >= 4) cache.Clear();
        cache[key] = result; return result;

        string Select(string name, string fallback)
        {
            if (resources.ContainsKey(name) || name == fallback) return name;
            diagnostics.Add($"Mission {difficulty}: {name} is unavailable; using {fallback}."); return fallback;
        }
        JsonNode? Decode(string name)
        {
            if (!resources.TryGetValue(name, out var resource)) return null;
            try
            {
                var tree = ZrdDecoder.Decode(resource.Archive.Slice(resource.Asset.Offset, resource.Asset.Length), token);
                if (name.StartsWith("puppies", StringComparison.Ordinal)) _ = PickupRecords(tree);
                else if (name != "startanims.zrd") _ = Records(tree).ToArray();
                return tree;
            }
            catch (InvalidDataException ex) { throw new InvalidDataException($"Mission {difficulty}: {name} in {resource.Archive.Path}: {ex.Message}", ex); }
        }
    }

    public static MissionSceneContext Build(ZbdDocument world, AnimationPackage? package, JsonNode? aiv, JsonNode? vehicles, JsonNode? starts, IEnumerable<string>? initialDiagnostics = null, CancellationToken token = default, MissionLayoutSelection? selection = null,
        JsonNode? ai = null, JsonNode? pickups = null, MissionResourceSource? pickupSource = null)
    {
        selection ??= MissionLayoutSelection.For(MissionDifficulty.Medium);
        token.ThrowIfCancellationRequested();
        var original = world.Scene ?? throw new InvalidDataException("Mission layout requires a GameZ scene.");
        GameScene scene = new(); scene.Models.AddRange(original.Models); scene.Materials.AddRange(original.Materials); scene.Textures.AddRange(original.Textures);
        foreach (var node in original.Nodes)
        {
            token.ThrowIfCancellationRequested();
            scene.Nodes.Add(node with { Parents = [.. node.Parents], Children = [.. node.Children], Data = (JsonObject)node.Data.DeepClone(), Metadata = (JsonObject)node.Metadata.DeepClone() });
        }
        List<int> sources = original.Nodes.Select(n => n.Index).ToList(); List<MissionActor> actors = []; List<string> notes = initialDiagnostics?.ToList() ?? [];
        HashSet<int> positioned = [];
        var previewWorld = new ZbdDocument(world.Path, world.Stamp, world.Probe, world.Bytes) { Scene = scene };
        AnimationPreviewContext? context = package == null ? null : new() { Package = package, World = previewWorld };
        if (context != null) Initialize(true, []);
        try { InitializeTurrets(scene, context, ai, notes, positioned, token); }
        catch (InvalidDataException ex) { notes.Add($"Mission turrets: ai.zrd initialization is incomplete: {ex.Message}"); }
        int worldRoot = scene.Nodes.FirstOrDefault(n => n.Class == "world")?.Index ?? -1;
        var vehicleNames = Records(vehicles).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (vehicles == null) notes.Add($"Mission starting layout: {selection.VehicleResource} is unavailable; vehicle definitions could not be recovered.");
        if (aiv == null) notes.Add($"Mission starting layout: {selection.AivResource} is unavailable; vehicle spawn positions could not be recovered.");
        else if (worldRoot >= 0)
            foreach (var (name, value) in Records(aiv))
            {
                token.ThrowIfCancellationRequested();
                if (value?["children"] is not JsonArray data || data.Count != 3 || data[1]?["children"] is not JsonArray xyz) continue;
                string templateName = VehicleTemplateName(name);
                if (!vehicleNames.Contains(templateName)) continue;
                try
                {
                    if (xyz.Count != 3) throw new InvalidDataException("Expected three spawn coordinates.");
                    Vector3 position = new(Number(xyz[0]), Number(xyz[1]), Number(xyz[2])); float yaw = Number(data[2]) * MathF.PI / 180;
                    int root = scene.Nodes.FirstOrDefault(n => n.Class == "object3d" && n.Name == name)?.Index ?? -1;
                    if (root < 0)
                    {
                        int template = scene.Nodes.FirstOrDefault(n => n.Class == "object3d" && n.Name == templateName)?.Index ?? -1;
                        if (template < 0) throw new InvalidDataException($"Missing template {templateName}.");
                        root = CloneTree(template, name);
                    }
                    SetPose(scene, root, Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position));
                    scene.Nodes[root].Metadata["flags"] = scene.Nodes[root].Metadata.UInt("flags") | 4;
                    if (!scene.Nodes[root].Parents.Contains(worldRoot)) scene.Nodes[root] = scene.Nodes[root] with { Parents = [worldRoot] };
                    scene.Nodes[worldRoot] = scene.Nodes[worldRoot] with { Children = scene.Nodes[worldRoot].Children.Append(root).Distinct().ToArray() };
                    positioned.Add(root); actors.Add(new(root, sources[root], name, $"{selection.AivResource} · {selection.Difficulty}"));
                }
                catch (InvalidDataException ex) { notes.Add($"Mission actor {name}: {ex.Message}"); }
            }
        PlacePickups(scene, sources, actors, positioned, worldRoot, world.Path, original.Nodes.Count, pickups,
            pickupSource ?? new(world.Path, -1, selection.PickupResource), selection, CloneTree, notes, token);
        if (context != null)
        {
            string[] startup = Pairs(starts).Where(p => p.Name == "NEW_GAME_START").SelectMany(p => Strings(p.Value)).ToArray();
            Initialize(false, startup);
        }
        HashSet<int> dormant = [];
        if (context != null)
            foreach (var entry in package!.Entries)
                foreach (var ev in entry.Sequences.SelectMany(s => s.Events))
                {
                    token.ThrowIfCancellationRequested();
                    if (ev.Spec == null || ev.Bytes.Length < ev.Spec.Size) continue;
                    try
                    {
                        int reference; Vector3 position;
                        if (ev.Type == 12 && ev.Keyframes().FirstOrDefault() is { } first && first.ChannelOffset(0) is >= 0 and int channel)
                        { reference = ev.I32(12); position = first.Vector(channel); }
                        else if (ev.Type == 7 && (ev.U32(12) & 1) == 0 && ev.I16(30) == 0)
                        { reference = ev.I16(28); position = ev.Vector(16); }
                        else continue;
                        int root = context.ResolveNode(entry, reference);
                        if (root < 0 || positioned.Contains(root) || position.LengthSquared() < .0001f) continue;
                        var node = scene.Nodes[root];
                        if (node.Class == "object3d" && node.Parents.Contains(worldRoot) && SceneBuilder.LocalTransform(node).Translation.LengthSquared() < .0001f)
                            dormant.Add(root);
                    }
                    catch (InvalidDataException ex) { notes.Add($"Mission placement {entry.Name}: {ex.Message}"); }
                }
        foreach (int root in dormant)
        {
            scene.Nodes[root].Metadata["preview_pending_placement"] = true;
            notes.Add($"Mission preview approximation: {scene.Nodes[root].Name} is hidden until an animation supplies its world placement.");
        }
        foreach (int root in positioned.Where(i => scene.Nodes[i].Class == "object3d" && !actors.Any(a => a.Root == i)))
            if (scene.Nodes[root].Parents.Contains(worldRoot)) actors.Add(new(root, sources[root], scene.Nodes[root].Name, "Animation initialization"));
        return new(scene, sources, actors, dormant, notes, selection, original.Nodes.Count);

        void Initialize(bool cleanup, string[] startup)
        {
            try { positioned.UnionWith(AnimationPlayer.ApplyInitialization(context!, cleanup, startup, notes, token)); }
            catch (InvalidDataException ex) { notes.Add($"Mission initialization is incomplete: {ex.Message}"); }
        }

        int CloneTree(int template, string name)
        {
            Dictionary<int, int> map = []; HashSet<int> path = [];
            int Copy(int index, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 256 || !path.Add(index)) throw new InvalidDataException("Cyclic mission template hierarchy.");
                try
                {
                    if (map.TryGetValue(index, out int existing)) return existing;
                    if (index < 0 || index >= original.Nodes.Count || scene.Nodes.Count >= 200000) throw new InvalidDataException("Invalid or excessive mission hierarchy.");
                    var source = scene.Nodes[index]; int id = scene.Nodes.Count; map[index] = id; sources.Add(sources[index]);
                    scene.Nodes.Add(source with { Index = id, Name = index == template ? name : source.Name, Parents = [], Children = [], Data = (JsonObject)source.Data.DeepClone(), Metadata = (JsonObject)source.Metadata.DeepClone() });
                    var children = SceneBuilder.Children(source).Select(c => Copy(c, depth + 1)).ToArray();
                    scene.Nodes[id] = scene.Nodes[id] with { Children = children };
                    foreach (int child in children) scene.Nodes[child] = scene.Nodes[child] with { Parents = scene.Nodes[child].Parents.Append(id).ToArray() };
                    return id;
                }
                finally { path.Remove(index); }
            }
            int before = scene.Nodes.Count;
            try { return Copy(template, 0); }
            catch { scene.Nodes.RemoveRange(before, scene.Nodes.Count - before); sources.RemoveRange(before, sources.Count - before); throw; }
        }
    }
    public static string VehicleTemplateName(string name)
    {
        for (int i = 0; i + 1 < name.Length; i++) if (name[i] == '_' && char.IsAsciiDigit(name[i + 1])) return name[..i];
        return name;
    }
    internal static void SetPose(GameScene scene, int index, Matrix4x4 m)
    {
        if (!float.IsFinite(m.GetDeterminant()) || !float.IsFinite(m.M41) || !float.IsFinite(m.M42) || !float.IsFinite(m.M43)) throw new InvalidDataException("Nonfinite mission pose.");
        var data = scene.Nodes[index].Data; data["flags"] = data.UInt("flags") & ~8u;
        data["transform"] = new JsonArray(new[] { m.M11,m.M12,m.M13,m.M21,m.M22,m.M23,m.M31,m.M32,m.M33,m.M41,m.M42,m.M43 }.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        Matrix4x4.Decompose(m, out var scale, out var rotation, out _); var euler = AnimationMath.ToEuler(rotation);
        data["scale"] = Vector(scale); data["rotate"] = Vector(euler);
        static JsonObject Vector(Vector3 v) => new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
    }
    private static float Number(JsonNode? n)
    { float value = JsonData.Scalar(n?["value"], float.NaN); if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite or missing spawn coordinate/heading."); return value; }
    private static IEnumerable<string> Strings(JsonNode? node)
    {
        if (node.Text("type") == "string") yield return node.Text("value");
        if (node?["children"] is JsonArray children) foreach (var child in children) foreach (string value in Strings(child)) yield return value;
    }
    private static IEnumerable<(string Name, JsonNode? Value)> Pairs(JsonNode? node)
    {
        if (node?["children"] is not JsonArray children) yield break;
        for (int i = 0; i + 1 < children.Count; i++) if (children[i].Text("type") == "string") yield return (children[i].Text("value"), children[i + 1]);
        foreach (var child in children) foreach (var pair in Pairs(child)) yield return pair;
    }
    private static IEnumerable<(string Name, JsonNode? Value)> Records(JsonNode? node)
    {
        if (node == null) yield break;
        if (node["children"] is not JsonArray children) throw new InvalidDataException("Expected a mission record array.");
        if (children is { Count: 1 } && children[0]?["children"] is JsonArray inner) children = inner;
        if (children.Count % 2 != 0) throw new InvalidDataException("Incomplete mission name/value pair.");
        for (int i = 0; i < children.Count; i += 2)
        {
            if (children[i].Text("type") != "string" || children[i + 1]?["children"] is not JsonArray) throw new InvalidDataException("Expected a mission name and record array.");
            yield return (children[i].Text("value"), children[i + 1]);
        }
    }
}
