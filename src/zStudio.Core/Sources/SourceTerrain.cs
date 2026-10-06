using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>Every recipe of the project (data/**/*.terrain.json), including new ones the workspace holds.</summary>
    public static IReadOnlyList<string> Recipes(SourceWorkspace workspace)
    {
        var added = workspace.Overlay().Keys.Where(k => !File.Exists(SourceProject.Resolve(workspace.Root, k))).ToArray();
        return SourceProject.Files(workspace.Root, SourceProject.DataFolder, n => n.EndsWith(TerrainRecipe.Extension, StringComparison.OrdinalIgnoreCase), added)
            .Where(workspace.Exists).ToArray();
    }

    public static TerrainRecipe Read(SourceWorkspace workspace, string path, CancellationToken token = default)
    {
        path = Checked(path);
        var bytes = workspace.Read(path, token) ?? throw new InvalidDataException($"The project has no terrain recipe {path}.");
        return TerrainRecipe.Parse(bytes, path);
    }

    /// <summary>Changes a recipe as one change of the workspace; null when the edit leaves it as it was.</summary>
    public static SourceTransaction? Edit(SourceWorkspace workspace, string path, string label, Func<TerrainRecipe, TerrainRecipe> change, CancellationToken token = default)
    {
        path = Checked(path);
        var recipe = Read(workspace, path, token);
        var changed = change(recipe);
        byte[] bytes = changed.Write();
        return workspace.Read(path, token) is { } current && current.AsSpan().SequenceEqual(bytes) ? null : workspace.Apply(label, [(path, bytes)], token);
    }

    /// <summary>
    /// Creates a recipe for <paramref name="nodes"/> of the glTF file <paramref name="model"/> (each named once there, with a
    /// mesh) and adds a marker for it at the end of the mission database's roots (<paramref name="database"/>), as one change.
    /// The surfaces must be in their own file: nodes of the database itself, or of a file it references (its parts), would
    /// also stay ordinary objects.
    /// </summary>
    public static SourceTransaction Create(SourceWorkspace workspace, string database, string model, IReadOnlyList<string> nodes, string? recipePath = null, CancellationToken token = default)
    {
        database = Checked(database); model = Checked(model);
        if (!database.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The mission database {database} must be a .gltf file to hold a terrain marker.");
        if (!model.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) && !model.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{model} is not a glTF file.");
        if (model.Equals(database, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Terrain surfaces must be in their own glTF file, not in the mission database, whose nodes are objects of the world.");
        // A file the database references (a part of it, or a model its objects reference) is loaded with it: its nodes would
        // stay objects as well, and take their zones from the references.
        if (Referenced(workspace, database, token).Contains(model))
            throw new InvalidDataException($"{model} is loaded with the mission database (a part of it, or a model its objects reference), so its nodes are objects of the world; terrain surfaces must be in a glTF file of their own.");
        if (nodes.Count is 0 or > TerrainRecipe.MaximumSurfaces) throw new InvalidDataException($"Choose 1–{TerrainRecipe.MaximumSurfaces} surfaces.");
        recipePath = Checked(recipePath ?? model[..model.LastIndexOf('.')] + TerrainRecipe.Extension);
        if (!recipePath.EndsWith(TerrainRecipe.Extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"A recipe's name ends with {TerrainRecipe.Extension}.");
        if (workspace.Exists(recipePath)) throw new InvalidDataException($"{recipePath} already exists.");
        var doc = GltfDocument.Read(workspace.Read(model, token) ?? throw new InvalidDataException($"The project has no {model}."),
            uri => workspace.Read(WorldAssembler.Relative(model, uri), token) ?? throw new InvalidDataException($"{model} names {JsonData.ShownText(uri)}, which does not exist."), token);
        List<TerrainSurface> surfaces = [];
        var zones = InheritedZones(doc);
        foreach (string name in nodes)
        {
            var matches = doc.AllNodes().Where(n => WorldGltf.EngineName(n) == name).Take(2).ToList();
            if (matches.Count != 1) throw new InvalidDataException($"{model} has {(matches.Count == 0 ? "no" : "more than one")} node named {name}.");
            if (matches[0].Mesh == null) throw new InvalidDataException($"Node {name} of {model} has no mesh.");
            string id = new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').Take(32)]);
            if (id.Length == 0) id = "surface";
            for (int n = 2; surfaces.Any(s => s.Id == id); n++) id = $"{id[..Math.Min(id.Length, 28)]}_{n}";
            surfaces.Add(new(id, RelativePath(recipePath, model), name, NodeDefaults(matches[0], zones.TryGetValue(matches[0], out var z) ? z : (0xFFu, false))));
        }
        var recipe = TerrainRecipe.Parse(new TerrainRecipe(TerrainRecipe.CurrentCompiler, surfaces, TerrainAttributes.None, []).Write(), recipePath);
        // The marker: a root of the database's scene that names the recipe.
        byte[] databaseBytes = workspace.Read(database, token) ?? throw new InvalidDataException($"The project has no {database}.");
        JsonObject root;
        try { root = JsonNode.Parse(databaseBytes, documentOptions: new() { MaxDepth = 256 }) as JsonObject ?? throw new InvalidDataException($"{database} is not a JSON object."); }
        catch (JsonException ex) { throw new InvalidDataException($"{database} is not valid JSON: {ex.Message}", ex); }
        var nodeList = root["nodes"] as JsonArray ?? (JsonArray)(root["nodes"] = new JsonArray());
        var scenes = root["scenes"] as JsonArray ?? throw new InvalidDataException($"{database} has no scene.");
        int sceneIndex = root["scene"] == null ? 0 : GltfInteger.Int32(root["scene"], "scene");
        if (sceneIndex < 0 || sceneIndex >= scenes.Count) throw new InvalidDataException($"{database} has no scene {sceneIndex}.");
        if (scenes.Count == 0 || scenes[sceneIndex] is not JsonObject scene) throw new InvalidDataException($"{database} has no scene.");
        string stem = Path.GetFileName(recipePath)[..^TerrainRecipe.Extension.Length];
        nodeList.Add(new JsonObject { ["name"] = $"{stem}_terrain", ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["terrain"] = RelativePath(database, recipePath) } } });
        (scene["nodes"] as JsonArray ?? (JsonArray)(scene["nodes"] = new JsonArray())).Add(nodeList.Count - 1);
        bool indented = databaseBytes.AsSpan(0, Math.Min(databaseBytes.Length, 4096)).Contains((byte)'\n');
        byte[] marked = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }));
        return workspace.Apply($"Create terrain {stem}", [(recipePath, recipe.Write()), (database, marked)], token) ?? throw new InvalidDataException("Creating the terrain changed nothing.");
    }

    /// <summary>
    /// A surface node's attributes as an object of the file has them: its flags when not the default, and its zone (its
    /// own, else its parents', as the importer inherits it) when it has one; a node without a zone keeps auto.
    /// </summary>
    private static TerrainAttributes NodeDefaults(Gltf.GltfNode node, (uint Zone, bool Explicit) zone)
    {
        uint? flags = (node.Extras?[WorldGltf.Key] as JsonObject)?["flags"] is JsonValue f && f.TryGetValue(out string? hex)
            && uint.TryParse(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.AsSpan(2) : hex.AsSpan(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint parsed)
            && (parsed & WorldGltf.CarriedFlags) != WorldGltf.DefaultCarried ? parsed & WorldGltf.CarriedFlags : null;
        return new() { Flags = flags, NodeZone = !zone.Explicit ? null : zone.Zone == 0xFF ? TerrainAttributes.AnyZone : (int)zone.Zone };
    }
    /// <summary>The files a glTF file references (<c>extras.recoil.ref</c>), and theirs in turn, as project paths.</summary>
    private static HashSet<string> Referenced(SourceWorkspace workspace, string file, CancellationToken token)
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase); Queue<string> pending = new([file]);
        while (pending.TryDequeue(out var current) && found.Count < SourceProject.MaximumFiles)
        {
            token.ThrowIfCancellationRequested();
            if (workspace.Read(current, token) is not { } bytes) continue;
            JsonObject? root;
            try { root = JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 256 }) as JsonObject; }
            catch (JsonException) { continue; }
            foreach (var node in root?["nodes"] as JsonArray ?? [])
                if (((node as JsonObject)?["extras"] as JsonObject)?[WorldGltf.Key] is JsonObject engine && engine["ref"] is JsonValue reference && reference.TryGetValue(out string? uri))
                {
                    string target;
                    try { target = WorldAssembler.Relative(current, uri); } catch (InvalidDataException) { continue; }
                    if (found.Add(target)) pending.Enqueue(target);
                }
        }
        return found;
    }
    /// <summary>The project path of a recipe surface's glTF file.</summary>
    public static string SurfaceFile(string recipe, TerrainSurface surface) => WorldAssembler.Relative(recipe, surface.Model);
    /// <summary>
    /// Each node's zone as the importer gives it: its own, else its parent's; the file's roots start with any (0xFF).
    /// Explicit tells whether a node on the way set it (an explicit any differs from no zone at all).
    /// </summary>
    private static Dictionary<Gltf.GltfNode, (uint Zone, bool Explicit)> InheritedZones(Gltf.GltfDocument doc)
    {
        Dictionary<Gltf.GltfNode, (uint, bool)> zones = new(ReferenceEqualityComparer.Instance);
        Stack<(Gltf.GltfNode Node, uint Parent, bool Explicit)> pending = new(doc.Roots.Select(r => (r, 0xFFu, false)));
        while (pending.TryPop(out var item))
        {
            if (zones.ContainsKey(item.Node) || zones.Count > 1_000_000) continue;
            uint? own = WorldGltf.StatedZone(item.Node.Extras?[WorldGltf.Key]);
            uint zone = own ?? item.Parent;
            bool isExplicit = own != null || item.Explicit;
            zones[item.Node] = (zone, isExplicit);
            foreach (var child in item.Node.Children) pending.Push((child, zone, isExplicit));
        }
        return zones;
    }
    /// <summary>
    /// The engine names of a glTF file's nodes that have meshes (the candidates for terrain surfaces), as the workspace holds
    /// the file; a name several nodes share (such as rock and rock.001) picks none, so it is left out.
    /// </summary>
    public static IReadOnlyList<string> MeshNodes(SourceWorkspace workspace, string model, CancellationToken token = default)
    {
        model = Checked(model);
        var doc = GltfDocument.Read(workspace.Read(model, token) ?? throw new InvalidDataException($"The project has no {model}."),
            uri => workspace.Read(WorldAssembler.Relative(model, uri), token) ?? throw new InvalidDataException($"{model} names {JsonData.ShownText(uri)}, which does not exist."), token);
        var named = doc.AllNodes().GroupBy(WorldGltf.EngineName).Where(g => g.Count() == 1).Select(g => g.Single());
        return named.Where(n => n.Mesh != null).Select(WorldGltf.EngineName).ToArray();
    }

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
