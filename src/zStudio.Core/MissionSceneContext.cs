using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record MissionActor(int Root, int SourceRoot, string Name, string PlacementSource, MissionPickup? Pickup = null, MissionPickupSource? CoordinateSource = null, Vector3? PlacementPosition = null, Vector3? PlacementRotation = null)
{
    public const int MaximumNamePreviewCharacters = 128;
    public int NameCharacters { get; init; } = Name.Length;
    public bool NameTruncated => NameCharacters > Name.Length;
}
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
    public AiNetworkSnapshot AiNetworks { get; internal set; } = AiNetworkSnapshot.Empty;
    private readonly int originalNodeCount;
    private readonly MissionActorIndex actorIndex;
    /// <summary>The unique authored actor occurrence owning this node, or null for absent/ambiguous ownership.</summary>
    public MissionActor? ActorAt(int node) => actorIndex.At(node);

    /// <summary>Prepare ownership for the actual preview topology, reusing this baseline's index only for its own scene.</summary>
    public MissionActorIndex PrepareActorIndex(GameScene scene, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ReferenceEquals(scene, Scene) ? actorIndex : MissionActorIndex.Build(scene, Actors, token);
    }

    internal MissionSceneContext(GameScene scene, List<int> sources, List<MissionActor> actors, HashSet<int> dormant, IEnumerable<string> diagnostics, MissionLayoutSelection layout, int originalNodeCount, CancellationToken token = default)
    {
        Layout = layout; this.originalNodeCount = originalNodeCount;
        Scene = scene; SourceNodes = sources.AsReadOnly(); Actors = actors.AsReadOnly(); DormantRoots = dormant;
        actorIndex = MissionActorIndex.Build(scene, Actors, token);
        Diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray(); Horizons = FindHorizons(scene);
    }
    /// <summary>Match an instance and its source node; never carry clone indices between layouts.</summary>
    public int RemapNodeFrom(MissionSceneContext previous, int index, IReadOnlySet<MissionPickupSource>? coordinateMatches = null)
    {
        if (index < 0 || index >= previous.Scene.Nodes.Count) return -1;
        int ancestor = index; HashSet<int> visited = [];
        while (ancestor >= 0 && ancestor < previous.Scene.Nodes.Count && visited.Add(ancestor))
        {
            var actor = previous.Actors.FirstOrDefault(a => a.Root == ancestor);
            if (actor != null)
            {
                bool Matches(MissionActor a) => a.SourceRoot == actor.SourceRoot &&
                    (actor.Pickup is { } pickup ? a.Pickup?.Source == pickup.Source :
                     actor.CoordinateSource is { } coordinate ? a.CoordinateSource is { } source && (coordinateMatches?.Contains(source) ?? source == coordinate) :
                     a.Pickup == null && a.CoordinateSource == null && a.Name == actor.Name);
                if (previous.Actors.Count(Matches) != 1) return -1;
                var matches = Actors.Where(Matches).ToArray();
                if (matches.Length != 1) return -1;
                int source = previous.SourceNodes[index];
                HashSet<int> nodes = []; Stack<int> pending = new(); pending.Push(matches[0].Root);
                while (pending.TryPop(out int node))
                    if (node >= 0 && node < Scene.Nodes.Count && nodes.Add(node)) foreach (int child in SceneBuilder.Children(Scene.Nodes[node])) pending.Push(child);
                var provenance = previous.Scene.Nodes[index].Metadata;
                int[] candidates = nodes.Where(n => source >= 0 ? SourceNodes[n] == source :
                    Scene.Nodes[n].Metadata.Text("source_library") == provenance.Text("source_library") &&
                    Scene.Nodes[n].Metadata.Int("source_node", -1) == provenance.Int("source_node", -2)).ToArray();
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
    internal static string[] ResourceFiles(string worldPath, AssetResolver resolver, CancellationToken token = default, CompiledInventory? inventory = null, bool sort = true)
    {
        inventory ??= new(token);
        List<string> files = []; HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in resolver.ResourceDirectories(worldPath, inventory))
            foreach (string path in inventory.Files(directory, sort))
            {
                inventory.Path(path.Length);
                if (seen.Add(path)) { inventory.Rows(1); files.Add(path); }
            }
        inventory.Rows(files.Count); return files.ToArray();
    }
    public static void Invalidate(ZbdDocument world) => Cache.Remove(world);

    /// <param name="mission">MW3 reader requested by the caller. Omitted, the resolver selection is captured before any await.</param>
    /// <param name="exactMission">Fail when the requested MW3 reader is unavailable instead of reporting a fallback.</param>
    public static async Task<MissionSceneContext> LoadAsync(ZbdDocument world, AssetResolver resolver, AnimationPackage? package = null, CancellationToken token = default, MissionDifficulty difficulty = MissionDifficulty.Medium,
        string? mission = null, bool exactMission = false)
    {
        token.ThrowIfCancellationRequested();
        if (world.Game == GameVariant.MechWarrior3) return await LoadMw3Async(world, resolver, mission ?? resolver.SelectedMission(world.Path), exactMission && mission != null, token).ConfigureAwait(false);
        var requested = MissionLayoutSelection.For(difficulty);
        string directory = Path.GetDirectoryName(world.Path)!;
        CompiledInventory inventory = new(token);
        var files = ResourceFiles(world.Path, resolver, token, inventory);
        string key = difficulty + "|" + resolver.SnapshotRevision + "|" + inventory.Fingerprint(files) + (package == null ? "" : Convert.ToHexString(SHA256.HashData(AnimationWriter.Write(package, token))));
        var cache = Cache.GetOrCreateValue(world);
        if (cache.TryGetValue(key, out var cached)) return cached;
        List<string> diagnostics = []; Dictionary<string, (ZbdDocument Archive, AssetRecord Asset)> resources = new(StringComparer.OrdinalIgnoreCase);
        List<(ZbdDocument Archive, AssetRecord Asset)> aiResources = [];
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
                if (MissionSceneLoader.ParseError(archive) is { } parseError) diagnostics.Add($"Mission layout: {Path.GetFileName(file)}: {parseError}");
                aiResources.AddRange(archive.Assets.Where(a => MissionAiNetworks.IsCandidate(a.Name)).Select(a => (archive, a)));
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
        var result = await Task.Run(() =>
        {
            var context = Build(world, package, Decode(aivName), Decode(vehicleName), Decode("startanims.zrd"), diagnostics, token, selection,
                Decode("ai.zrd"), Decode(pickupName), pickupSource,
                resources.TryGetValue(aivName, out var aivResource) ? new(aivResource.Archive.Path, aivResource.Asset.Index, aivResource.Asset.Name) : null);
            context.AiNetworks = MissionAiNetworks.Read(aiResources, token);
            return context;
        }, token).ConfigureAwait(false);
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
                var tree = ZrdDecoder.ReadAsset(resource.Archive, resource.Asset, token).ToJson(token);
                if (name.StartsWith("puppies", StringComparison.Ordinal)) _ = PickupRecords(tree);
                else if (name != "startanims.zrd") _ = Records(tree).ToArray();
                return tree;
            }
            catch (InvalidDataException ex) when (resource.Asset.Metadata["typed_decode_limited"]?.GetValue<bool>() == true)
            {
                // The archive deliberately kept this member raw. Do not decode it again or silently substitute a
                // different difficulty's layout; the remaining preview stays available with an explicit omission.
                diagnostics.Add($"Mission {difficulty}: {name} in {resource.Archive.Path}: {ex.Message}");
                return null;
            }
            catch (InvalidDataException ex) { throw new InvalidDataException($"Mission {difficulty}: {name} in {resource.Archive.Path}: {ex.Message}", ex); }
        }
    }

    public static MissionSceneContext Build(ZbdDocument world, AnimationPackage? package, JsonNode? aiv, JsonNode? vehicles, JsonNode? starts, IEnumerable<string>? initialDiagnostics = null, CancellationToken token = default, MissionLayoutSelection? selection = null,
        JsonNode? ai = null, JsonNode? pickups = null, MissionResourceSource? pickupSource = null, MissionResourceSource? aivSource = null)
        => BuildWithBudget(world, new MissionPlacementBudget(token), package, aiv, vehicles, starts, initialDiagnostics, token, selection, ai, pickups, pickupSource, aivSource);

    internal static MissionSceneContext BuildWithBudget(ZbdDocument world, MissionPlacementBudget budget, AnimationPackage? package, JsonNode? aiv, JsonNode? vehicles, JsonNode? starts,
        IEnumerable<string>? initialDiagnostics = null, CancellationToken token = default, MissionLayoutSelection? selection = null,
        JsonNode? ai = null, JsonNode? pickups = null, MissionResourceSource? pickupSource = null, MissionResourceSource? aivSource = null,
        long maximumBindingWork = Worlds.LookupWorkBudget.MaximumUnits)
    {
        selection ??= MissionLayoutSelection.For(MissionDifficulty.Medium);
        token.ThrowIfCancellationRequested();
        var original = world.Scene ?? throw new InvalidDataException("Mission layout requires a GameZ scene.");
        GameScene scene = new(); scene.Models.AddRange(original.Models); scene.Materials.AddRange(original.Materials); scene.Textures.AddRange(original.Textures);
        foreach (var node in original.Nodes)
        {
            budget.Clone(node);
            scene.Nodes.Add(node with { Parents = [.. node.Parents], Children = [.. node.Children], Data = (JsonObject)node.Data.DeepClone(), Metadata = (JsonObject)node.Metadata.DeepClone() });
        }
        List<int> sources = original.Nodes.Select(n => n.Index).ToList(); List<MissionActor> actors = []; List<string> notes = initialDiagnostics?.ToList() ?? [];
        HashSet<int> positioned = [];
        var previewWorld = new ZbdDocument(world.Path, world.Stamp, world.Probe, world.Bytes) { Scene = scene };
        AnimationPreviewContext? context = package == null ? null : new() { Package = package, World = previewWorld, LoadedNodeCount = original.Nodes.Count };
        AnimationBindingOperation? bindings = context == null ? null : new(context, token, maximumBindingWork);
        if (context != null) Initialize(true, []);
        try { InitializeTurrets(scene, context, ai, notes, positioned, token, bindings); }
        catch (InvalidDataException ex) { notes.Add($"Mission turrets: ai.zrd initialization is incomplete: {ex.Message}"); }
        int worldRoot = scene.Nodes.FirstOrDefault(n => n.Class == "world")?.Index ?? -1;
        MissionPlacementState placement = new(scene, original.Nodes.Count, worldRoot, budget); placement.Initialize();
        BoundedDiagnostics placementNotes = new(notes);
        MissionResourceSource? coordinateSource = null; string? actorLabel = null;
        HashSet<string> vehicleNames = new(StringComparer.Ordinal);
        foreach (var record in Records(vehicles)) { budget.Name(record.Name); vehicleNames.Add(record.Name); }
        if (vehicles == null) notes.Add($"Mission starting layout: {selection.VehicleResource} is unavailable; vehicle definitions could not be recovered.");
        if (aiv == null) notes.Add($"Mission starting layout: {selection.AivResource} is unavailable; vehicle spawn positions could not be recovered.");
        else if (worldRoot >= 0)
            foreach (var ((name, value), recordIndex) in Records(aiv).Select((record, index) => (record, index)))
            {
                budget.Name(name);
                if (value?["children"] is not JsonArray data || data.Count != 3 || data[1]?["children"] is not JsonArray xyz) continue;
                string templateName = VehicleTemplateName(name);
                budget.Name(templateName);
                if (!vehicleNames.Contains(templateName)) continue;
                try
                {
                    if (xyz.Count != 3) throw new InvalidDataException("Expected three spawn coordinates.");
                    Vector3 position = new(Number(xyz[0]), Number(xyz[1]), Number(xyz[2])); float yaw = Number(data[2]) * MathF.PI / 180;
                    // CreateFromNamesAtPose (0x421ab0) looks both names up among all live nodes, the most recently created first
                    // (FindByTypeAndName, docs/engine-evidence.md), and uses what it finds as the vehicle whatever its class.
                    int root = placement.Live(name);
                    if (root < 0)
                    {
                        int template = placement.Live(templateName);
                        if (template < 0) throw new InvalidDataException($"Missing template {JsonData.ShownText(templateName, 192)}.");
                        if (scene.Nodes[template].Class != "object3d") throw new InvalidDataException($"The game copies the {JsonData.ShownText(scene.Nodes[template].Class, 64)} node {JsonData.ShownText(templateName, 192)} (#{template}) as this vehicle, which the preview does not show.");
                        root = CloneTree(template, name);
                    }
                    else if (scene.Nodes[root].Class != "object3d") throw new InvalidDataException($"The game places the {JsonData.ShownText(scene.Nodes[root].Class, 64)} node #{root} of this name as the vehicle, which the preview does not show.");
                    SetPose(scene, root, Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position));
                    scene.Nodes[root].Metadata["flags"] = scene.Nodes[root].Metadata.UInt("flags") | 4;
                    budget.Take(scene.Nodes[root].Parents.Length);
                    if (!scene.Nodes[root].Parents.Contains(worldRoot)) scene.Nodes[root] = scene.Nodes[root] with { Parents = [worldRoot] };
                    placement.Attach(root, aiv: true); placement.Actor(root);
                    if (actorLabel == null) { budget.TextCopy(selection.AivResource.Length + 32L); actorLabel = $"{selection.AivResource} · {selection.Difficulty}"; }
                    positioned.Add(root); actors.Add(new(root, sources[root], name, actorLabel, CoordinateSource: CoordinateSource(recordIndex),
                        PlacementPosition: position, PlacementRotation: new(0, Number(data[2]), 0)));
                }
                catch (InvalidDataException ex) when (!budget.Exhausted) { placementNotes.Add($"Mission actor {name}: {ex.Message}"); }
            }
        PlacePickups(scene, sources, actors, positioned, worldRoot, world.Path, original.Nodes.Count, pickups,
            pickupSource ?? new(world.Path, -1, selection.PickupResource), selection, CloneTree, placementNotes, token, placement, budget);
        // Startup initialization traverses the scene, so publish the one accumulated adjacency before it runs.
        placement.PublishChildren();
        if (context != null)
        {
            string[] startup = Pairs(starts).Where(p => p.Name == "NEW_GAME_START").SelectMany(p => Strings(p.Value)).ToArray();
            Initialize(false, startup);
        }
        HashSet<int> dormant = [];
        if (context != null)
        {
            // Startup and placement may have changed topology without changing its count. Dormant references
            // belong to this same preparation allowance, including entries that skipped initialization entirely.
            bindings!.Invalidate();
            foreach (var entry in package!.Entries)
            {
                bindings.Reserve(1);
                foreach (var ev in entry.Sequences.SelectMany(s => s.Events))
                {
                    bindings.Reserve(1);
                    if (ev.Spec == null || ev.Bytes.Length < ev.Spec.Size) continue;
                    try
                    {
                        int reference; Vector3 position;
                        if (ev.Type == 12 && ev.Keyframes().FirstOrDefault() is { } first && first.ChannelOffset(0) is >= 0 and int channel)
                        { reference = ev.I32(12); position = first.Vector(channel); }
                        else if (ev.Type == 7 && (ev.U32(12) & 1) == 0 && ev.I16(30) == 0)
                        { reference = ev.I16(28); position = ev.Vector(16); }
                        else continue;
                        int boundRoot = bindings.Root(entry);
                        int root = context.ResolveInstanceNode(entry, reference, boundRoot, context.Binding(entry, boundRoot, bindings), bindings);
                        if (root < 0 || positioned.Contains(root) || position.LengthSquared() < .0001f) continue;
                        var node = scene.Nodes[root];
                        bindings.Reserve(32L + node.Parents.Length);
                        if (node.Class == "object3d" && node.Parents.Contains(worldRoot) && SceneBuilder.LocalTransform(node).Translation.LengthSquared() < .0001f)
                            dormant.Add(root);
                    }
                    catch (InvalidDataException ex) { notes.Add($"Mission placement {entry.Name}: {ex.Message}"); }
                }
            }
        }
        foreach (int root in dormant)
        {
            scene.Nodes[root].Metadata["preview_pending_placement"] = true;
            notes.Add($"Mission preview approximation: {scene.Nodes[root].Name} is hidden until an animation supplies its world placement.");
        }
        foreach (int root in positioned)
        {
            budget.Take();
            if (scene.Nodes[root].Class != "object3d" || placement.HasActor(root)) continue;
            budget.Take(scene.Nodes[root].Parents.Length);
            if (scene.Nodes[root].Parents.Contains(worldRoot)) { placement.Actor(root); actors.Add(new(root, sources[root], scene.Nodes[root].Name, "Animation initialization")); }
        }
        return new(scene, sources, actors, dormant, notes, selection, original.Nodes.Count, token);

        MissionPickupSource? CoordinateSource(int recordIndex)
        {
            if (aivSource == null) return null;
            // Resolve only for a successfully placed record, just as before. Each record keeps its own index,
            // while immutable full path/member identities are normalized and retained once for this operation.
            coordinateSource ??= new(budget.SourceIdentity(aivSource.ArchivePath, fullPath: true), aivSource.AssetIndex,
                budget.SourceIdentity(aivSource.ResourceName));
            return new(coordinateSource.ArchivePath, coordinateSource.AssetIndex, coordinateSource.ResourceName, recordIndex);
        }

        void Initialize(bool cleanup, string[] startup)
        {
            try { positioned.UnionWith(AnimationPlayer.ApplyInitialization(context!, cleanup, startup, notes, token, bindings)); }
            catch (InvalidDataException ex) { notes.Add($"Mission initialization is incomplete: {ex.Message}"); }
        }

        int CloneTree(int template, string name)
        {
            Dictionary<int, int> map = []; HashSet<int> path = []; Dictionary<int, List<int>> parents = [];
            int Copy(int index, int depth)
            {
                budget.Take();
                if (depth > 256 || !path.Add(index)) throw new InvalidDataException("Cyclic mission template hierarchy.");
                try
                {
                    if (map.TryGetValue(index, out int existing)) return existing;
                    if (index < 0 || index >= original.Nodes.Count || scene.Nodes.Count >= 200000) throw new InvalidDataException("Invalid or excessive mission hierarchy.");
                    var source = scene.Nodes[index]; var explicitChildren = placement.Children(source); budget.Clone(source, explicitChildren.Count);
                    int id = scene.Nodes.Count; map[index] = id; sources.Add(sources[index]); parents.Add(id, []);
                    scene.Nodes.Add(source with { Index = id, Name = index == template ? name : source.Name, Parents = [], Children = [], Data = (JsonObject)source.Data.DeepClone(), Metadata = (JsonObject)source.Metadata.DeepClone() });
                    List<int> children = [];
                    // Clone admission already charged all raw children and partition JSON, before this iterator's
                    // stable Distinct can allocate. Keep the shared child semantics (including malformed input).
                    foreach (int originalChild in SceneBuilder.Children(source, explicitChildren))
                    {
                        budget.Take(); int child = Copy(originalChild, depth + 1); children.Add(child);
                    }
                    budget.Take(children.Count);
                    scene.Nodes[id] = scene.Nodes[id] with { Children = children.ToArray() };
                    // Add only after all descendants, matching the original reciprocal-parent occurrence order.
                    foreach (int child in children) { budget.Take(); parents[child].Add(id); }
                    return id;
                }
                finally { path.Remove(index); }
            }
            int before = scene.Nodes.Count;
            try
            {
                int root = Copy(template, 0);
                foreach (var (id, list) in parents) { budget.Take(list.Count); scene.Nodes[id] = scene.Nodes[id] with { Parents = list.ToArray() }; }
                placement.Cloned(before); return root;
            }
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
        // Version 13 (the 1998 demos) also stores the translation of these components (Object3DTranslate's), beside the matrix.
        if (data.ContainsKey("translate")) data["translate"] = Vector(m.Translation);
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
