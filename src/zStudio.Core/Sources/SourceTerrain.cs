using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Terrain recipes of a source project, edited through its workspace: listing them, changing one as an undoable change,
/// and creating one for surfaces of a glTF file, with the marker that places it among the mission database's roots.
/// </summary>
public static class SourceTerrain
{
    // Bound the input before DOM construction; compact UTF-8 output avoids a second full UTF-16 copy.
    public const int MaximumDatabaseJsonBytes = 16 * 1024 * 1024;
    /// <summary>Every recipe of the project (data/**/*.terrain.json), including new ones the workspace holds.</summary>
    public static IReadOnlyList<string> Recipes(SourceWorkspace workspace, CancellationToken token = default)
    {
        var added = workspace.Overlay().Keys.Where(k => !File.Exists(SourceProject.Resolve(workspace.Root, k))).ToArray();
        return SourceProject.Files(workspace.Root, SourceProject.DataFolder, n => n.EndsWith(TerrainRecipe.Extension, StringComparison.OrdinalIgnoreCase), added, token)
            .Where(path => workspace.Exists(path, token)).ToArray();
    }

    public static TerrainRecipe Read(SourceWorkspace workspace, string path, CancellationToken token = default)
    {
        path = Checked(path);
        var bytes = workspace.Read(path, token, TerrainRecipe.MaximumBytes) ?? throw new InvalidDataException($"The project has no terrain recipe {path}.");
        return TerrainRecipe.Parse(bytes, path);
    }

    /// <summary>Changes a recipe as one change of the workspace; null when the edit leaves it as it was.</summary>
    public static SourceTransaction? Edit(SourceWorkspace workspace, string path, string label, Func<TerrainRecipe, TerrainRecipe> change, CancellationToken token = default)
    {
        path = Checked(path);
        var recipe = Read(workspace, path, token);
        var changed = change(recipe);
        byte[] bytes = changed.Write();
        return workspace.Read(path, token, TerrainRecipe.MaximumBytes) is { } current && current.AsSpan().SequenceEqual(bytes) ? null : workspace.Apply(label, [(path, bytes)], token);
    }

    /// <summary>
    /// Creates a recipe for <paramref name="nodes"/> of the glTF file <paramref name="model"/> (each named once there, with a
    /// mesh) and adds a marker for it at the end of the mission database's roots (<paramref name="database"/>), as one change.
    /// The surfaces must be in their own file: nodes of the database itself, or of a file it references (its parts), would
    /// also stay ordinary objects.
    /// </summary>
    public static SourceTransaction Create(SourceWorkspace workspace, string database, string model, IReadOnlyList<string> nodes, string? recipePath = null, CancellationToken token = default)
        => Create(workspace, database, model, nodes, recipePath, token, SourceProject.MaximumFiles);

    internal static SourceTransaction Create(SourceWorkspace workspace, string database, string model, IReadOnlyList<string> nodes, string? recipePath, CancellationToken token, int maximumReferenceFiles,
        TerrainPlacementBudget? placementBudget = null, int maximumModelJsonBytes = GltfDocument.MaximumJsonBytes,
        long maximumModelBufferBytes = GltfDocument.MaximumBufferBytes,
        long maximumReferenceInputBytes = FormatRegistry.MaximumDocumentBytes)
    {
        database = Checked(database); model = Checked(model);
        if (!database.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The mission database {database} must be a .gltf file to hold a terrain marker.");
        if (!model.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) && !model.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{model} is not a glTF file.");
        if (model.Equals(database, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Terrain surfaces must be in their own glTF file, not in the mission database, whose nodes are objects of the world.");
        // A file the database references (a part of it, or a model its objects reference) is loaded with it: its nodes would
        // stay objects as well, and take their zones from the references.
        if (Referenced(workspace, database, token, maximumReferenceFiles, maximumReferenceInputBytes).Contains(model))
            throw new InvalidDataException($"{model} is loaded with the mission database (a part of it, or a model its objects reference), so its nodes are objects of the world; terrain surfaces must be in a glTF file of their own.");
        if (nodes.Count is 0 or > TerrainRecipe.MaximumSurfaces) throw new InvalidDataException($"Choose 1–{TerrainRecipe.MaximumSurfaces} surfaces.");
        recipePath = Checked(recipePath ?? model[..model.LastIndexOf('.')] + TerrainRecipe.Extension);
        if (!recipePath.EndsWith(TerrainRecipe.Extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"A recipe's name ends with {TerrainRecipe.Extension}.");
        if (workspace.Exists(recipePath, token)) throw new InvalidDataException($"{recipePath} already exists.");
        SourceMapZoneAsset? surfaceBinding = null;
        string[] databaseParts = database.Split('/');
        if (databaseParts.Length > 2 && SourceProject.MissionName().IsMatch(databaseParts[1]) &&
            workspace.Read(SourceMapZones.PathForMission(databaseParts[1]), token, SourceMapZones.MaximumBytes) is { } zoneBytes)
        {
            var map = SourceMapZones.Parse(zoneBytes, token);
            if (map.TryGetAsset(model, out var binding)) surfaceBinding = binding;
        }
        var doc = ReadModel(workspace, surfaceBinding?.GeometryPath ?? model, token, maximumModelJsonBytes, maximumModelBufferBytes);
        List<TerrainSurface> surfaces = [];
        TerrainPlacementIndex placements = new(doc, placementBudget ?? new(token), surfaceBinding?.Profile);
        foreach (string name in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (!placements.TryGet(name, out var placement, out bool ambiguous))
                throw new InvalidDataException($"{model} has {(ambiguous ? "more than one" : "no")} node named {JsonData.ShownText(name)}.");
            if (placement.Node.Mesh == null) throw new InvalidDataException($"Node {JsonData.ShownText(name)} of {model} has no mesh.");
            WorldGltf.CheckTerrainAppearance(placement.Node, placement.InheritedAppearance, model);
            string id = new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').Take(32)]);
            if (id.Length == 0) id = "surface";
            for (int n = 2; surfaces.Any(s => s.Id == id); n++) id = $"{id[..Math.Min(id.Length, 28)]}_{n}";
            surfaces.Add(new(id, RelativePath(recipePath, model), name, NodeDefaults(placement)));
        }
        var recipe = TerrainRecipe.Parse(new TerrainRecipe(TerrainRecipe.CurrentCompiler, surfaces, TerrainAttributes.None, []).Write(), recipePath);
        // The marker: a root of the database's scene that names the recipe.
        byte[] databaseBytes = workspace.Read(database, token, MaximumDatabaseJsonBytes) ?? throw new InvalidDataException($"The project has no {database}.");
        JsonObject root;
        try { root = JsonNode.Parse(databaseBytes, documentOptions: new() { MaxDepth = 256 }) as JsonObject ?? throw new InvalidDataException($"{database} is not a JSON object."); }
        catch (JsonException ex) { throw new InvalidDataException($"{database} is not valid JSON: {ex.Message}", ex); }
        var nodeList = root["nodes"] as JsonArray ?? (JsonArray)(root["nodes"] = new JsonArray());
        var original = WorldAssembler.ReadModel(databaseBytes, database, (path, remaining) => workspace.Read(path, token, ProjectReadLimits.Bytes(remaining))
            ?? throw new InvalidDataException($"{database}: a buffer is unavailable."), token);
        var bindings = SourceMapZoneBindings.Read(workspace, database, original, token);
        bindings.RequireSingleTerrainOwner(database);
        Dictionary<JsonNode, int> originalObjects = new(ReferenceEqualityComparer.Instance);
        if (bindings.Any) for (int i = 0; i < nodeList.Count; i++) originalObjects.Add(nodeList[i]!, i);
        var scenes = root["scenes"] as JsonArray ?? throw new InvalidDataException($"{database} has no scene.");
        int sceneIndex = root["scene"] == null ? 0 : GltfInteger.Int32(root["scene"], "scene");
        if (sceneIndex < 0 || sceneIndex >= scenes.Count) throw new InvalidDataException($"{database} has no scene {sceneIndex}.");
        if (scenes.Count == 0 || scenes[sceneIndex] is not JsonObject scene) throw new InvalidDataException($"{database} has no scene.");
        string stem = Path.GetFileName(recipePath)[..^TerrainRecipe.Extension.Length];
        nodeList.Add(new JsonObject { ["name"] = MarkerName($"{stem}_terrain", nodeList, token), ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["terrain"] = RelativePath(database, recipePath) } } });
        (scene["nodes"] as JsonArray ?? (JsonArray)(scene["nodes"] = new JsonArray())).Add(nodeList.Count - 1);
        byte[] marked = GltfJson.Write(root, indented: false, token);
        var updated = WorldAssembler.ReadModel(marked, database, (path, remaining) => workspace.Read(path, token, ProjectReadLimits.Bytes(remaining))
            ?? throw new InvalidDataException($"{database}: buffer {JsonData.ShownText(path)} is unavailable."), token);
        List<(string Relative, byte[] Content)> changes = [(recipePath, recipe.Write()), (database, marked)];
        changes.AddRange(bindings.Remap(original, updated, root, originalObjects, new Dictionary<int, int>(), token, new HashSet<int> { nodeList.Count - 1 }));
        return workspace.Apply($"Create terrain {stem}", changes.Select(c => (c.Relative, (byte[]?)c.Content)), token) ?? throw new InvalidDataException("Creating the terrain changed nothing.");
    }

    /// <summary>
    /// A new marker's node name: <paramref name="name"/> when the source-name validator every database node passes accepts it,
    /// else <c>terrain</c> (as conversion falls back for a long or non-Latin-1 recipe name; the recipe URI keeps its
    /// identity), numbered while another node of the database has that engine name.
    /// </summary>
    private static string MarkerName(string name, JsonArray nodes, CancellationToken token)
    {
        if (!WorldGltf.IsEngineName(name)) name = "terrain";
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (node is JsonObject named) taken.Add(WorldGltf.EngineName(named));
        }
        string candidate = name;
        for (int n = 2; taken.Contains(candidate); n++)
        {
            string suffix = $"_{n}";
            candidate = name[..Math.Min(name.Length, 35 - suffix.Length)] + suffix;
        }
        return candidate;
    }
    /// <summary>
    /// A surface node's attributes as an object of the file has them: its flags when not the default, and its zone (its
    /// own, else its parents', as the importer inherits it) when it has one; a node without a zone keeps auto.
    /// </summary>
    private static TerrainAttributes NodeDefaults(TerrainPlacementIndex.Placement placement) => new()
    {
        Flags = placement.Flags,
        NodeZone = !placement.ExplicitZone ? null : placement.Zone == 0xFF ? TerrainAttributes.AnyZone : (int)placement.Zone,
    };
    /// <summary>The files a glTF file references (<c>extras.recoil.ref</c>), and theirs in turn, as project paths.</summary>
    internal static HashSet<string> Referenced(SourceWorkspace workspace, string file, CancellationToken token,
        int maximumFiles = SourceProject.MaximumFiles, long maximumInputBytes = FormatRegistry.MaximumDocumentBytes)
    {
        string[] parts = SourceWorkspace.Normalize(file).Split('/');
        SourceMapZones? zones = null;
        if (parts.Length > 2 && SourceProject.MissionName().IsMatch(parts[1]))
        {
            var bytes = workspace.Read(SourceMapZones.PathForMission(parts[1].ToLowerInvariant()), token, Math.Min(maximumInputBytes, SourceMapZones.MaximumBytes));
            if (bytes != null) { maximumInputBytes -= bytes.Length; zones = SourceMapZones.Parse(bytes, token); }
        }
        return Referenced(file, (path, limits) => workspace.Read(path, token, limits), token, maximumFiles, maximumInputBytes, zones: zones);
    }

    internal static HashSet<string> Referenced(string file, Func<string, ProjectReadLimits, byte[]?> readFile, CancellationToken token,
        int maximumFiles = SourceProject.MaximumFiles, long maximumInputBytes = FormatRegistry.MaximumDocumentBytes,
        int maximumNodes = GltfDocument.MaximumNodes, long maximumWork = LookupWorkBudget.MaximumUnits, Action<long>? reserved = null,
        SourceMapZones? zones = null)
    {
        if (maximumInputBytes < 0 || maximumInputBytes > FormatRegistry.MaximumDocumentBytes) throw new ArgumentOutOfRangeException(nameof(maximumInputBytes));
        file = WorldAssembler.Relative("reference.gltf", SourceWorkspace.Normalize(file));
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase), read = new(StringComparer.OrdinalIgnoreCase); Queue<string> pending = new([file]);
        HashSet<string> queued = new(StringComparer.OrdinalIgnoreCase) { file };
        LookupWorkBudget work = new(maximumWork, token);
        void Reserve(long amount)
        {
            try { work.Reserve(amount); }
            catch (InvalidDataException ex) when (work.Exhausted)
            { throw new InvalidDataException("The mission database's complete model references exceed the reference inspection work allowance; terrain creation is refused.", ex); }
            reserved?.Invoke(work.UsedUnits);
            token.ThrowIfCancellationRequested();
        }
        long inputBytes = 0;
        while (pending.TryDequeue(out var current))
        {
            token.ThrowIfCancellationRequested();
            if (!read.Add(current)) continue;
            string geometry = current;
            if (zones?.TryGetAsset(current, out var assignment) == true)
            {
                geometry = assignment.GeometryPath;
                foreach (var binding in assignment.References)
                {
                    Reserve(1L + binding.Asset.Length);
                    AddReference(binding.Asset);
                    if (zones.TryGetAsset(binding.Asset, out var target) && !target.GeometryPath.Equals(binding.Asset, StringComparison.OrdinalIgnoreCase))
                        AddReference(target.GeometryPath, visit: false);
                }
            }
            // Keep the whole-file cap, including GLB BIN/unknown chunks. Typed admission also precedes
            // prepared dependency hashing; a post-read count or an untyped cached read is too late.
            var limits = ProjectReadLimits.Model(Math.Min(MaximumDatabaseJsonBytes, maximumInputBytes - inputBytes), MaximumDatabaseJsonBytes);
            byte[]? bytes;
            try
            {
                bytes = readFile(geometry, limits);
                if (bytes != null && bytes.LongLength > limits.MaximumBytes) throw new InvalidDataException("The model reader exceeded its input allowance.");
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"The mission database's complete model references cannot be checked: {JsonData.ShownText(current)} could not be read within the {maximumInputBytes:N0}-byte combined and {MaximumDatabaseJsonBytes:N0}-byte per-file limits; terrain creation is refused. {JsonData.ShownText(ex.Message, 512)}", ex);
            }
            token.ThrowIfCancellationRequested();
            if (bytes == null) continue;
            inputBytes += bytes.LongLength;
            // Inspect the cold element representation. Never hydrate an auxiliary node array merely
            // to discover that its entries are malformed or exceed the shared model limits.
            JsonElement root;
            Reserve(bytes.LongLength);
            try { root = JsonElement.Parse(GltfDocument.ContainerJson(bytes, token, out _), new JsonDocumentOptions { MaxDepth = 64 }); }
            catch (JsonException) { continue; }
            GltfDocument.Bound(root, GltfDocument.MaximumMetadataBytes, token, maximumNodes);
            if (!root.TryGetProperty("nodes", out var nodeArray)) continue;
            foreach (var node in nodeArray.EnumerateArray())
            {
                Reserve(1);
                if (node.TryGetProperty("extras", out var extras) && extras.ValueKind == JsonValueKind.Object
                    && extras.TryGetProperty(WorldGltf.Key, out var engine) && engine.ValueKind == JsonValueKind.Object
                    && engine.TryGetProperty("ref", out var reference) && reference.ValueKind == JsonValueKind.String)
                {
                    Reserve(4L * (current.Length + JsonMarshal.GetRawUtf8Value(reference).Length + 1));
                    string uri = reference.GetString()!;
                    string target;
                    try { target = WorldAssembler.Relative(current, uri); } catch (InvalidDataException) { continue; }
                    AddReference(target);
                }
                else if (node.TryGetProperty("extras", out var holder) && holder.ValueKind == JsonValueKind.Object &&
                    holder.TryGetProperty(WorldGltf.Key, out var values) && values.ValueKind == JsonValueKind.Object && values.TryGetProperty(WorldGltf.ZoneReference, out _) &&
                    zones?.TryGetAsset(current, out _) != true)
                    throw new InvalidDataException("The database needs its map zone reference bindings before terrain creation.");
            }
        }
        token.ThrowIfCancellationRequested();
        return found;
        void AddReference(string target, bool visit = true)
        {
            if (!found.Contains(target))
            {
                if (found.Count >= maximumFiles)
                    throw new InvalidDataException($"The mission database references more than {maximumFiles:N0} files; its complete model references cannot be checked, so terrain creation is refused.");
                found.Add(target);
            }
            if (visit && queued.Add(target)) pending.Enqueue(target);
        }
    }
    /// <summary>The project path of a recipe surface's glTF file.</summary>
    public static string SurfaceFile(string recipe, TerrainSurface surface) => WorldAssembler.Relative(recipe, surface.Model);
    /// <summary>
    /// The engine names of a glTF file's nodes that have meshes (the candidates for terrain surfaces), as the workspace holds
    /// the file; a name several nodes share (such as rock and rock.001) picks none, so it is left out.
    /// </summary>
    public static IReadOnlyList<string> MeshNodes(SourceWorkspace workspace, string model, CancellationToken token = default)
        => MeshNodes(workspace, model, token, GltfDocument.MaximumJsonBytes, GltfDocument.MaximumBufferBytes);

    internal static IReadOnlyList<string> MeshNodes(SourceWorkspace workspace, string model, CancellationToken token,
        int maximumModelJsonBytes, long maximumModelBufferBytes)
    {
        model = Checked(model);
        var doc = ReadModel(workspace, model, token, maximumModelJsonBytes, maximumModelBufferBytes);
        return new TerrainPlacementIndex(doc, new(token)).MeshNames();
    }

    private static GltfDocument ReadModel(SourceWorkspace workspace, string model, CancellationToken token,
        int maximumModelJsonBytes, long maximumModelBufferBytes)
        => WorldAssembler.ReadModel(workspace.Read(model, token, ProjectReadLimits.Model(maximumJsonBytes: maximumModelJsonBytes))
            ?? throw new InvalidDataException($"The project has no {model}."), model,
            (path, remaining) => workspace.Read(path, token, ProjectReadLimits.Bytes(remaining))
                ?? throw new InvalidDataException($"{model}: buffer {JsonData.ShownText(path)} is unavailable."), token, maximumModelBufferBytes);

    /// <summary>A file's path relative to the folder of <paramref name="from"/>, with forward slashes.</summary>
    public static string RelativePath(string from, string to) => Path.GetRelativePath(Path.GetDirectoryName(from.Replace('/', Path.DirectorySeparatorChar)) ?? "", to.Replace('/', Path.DirectorySeparatorChar)).Replace('\\', '/');
    private static string Checked(string path)
    {
        path = SourceWorkspace.Normalize(path);
        if (!path.StartsWith(SourceProject.DataFolder + "/", StringComparison.OrdinalIgnoreCase) || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException($"'{path}' is not a file in the project's data folder.");
        return path;
    }
}
